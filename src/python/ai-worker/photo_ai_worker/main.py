import os, time
from fastapi import FastAPI, Depends
import uvicorn
from . import __version__
from .auth import require_token
from .contracts import AiRequest, AiResponse, AiError
from .settings import settings
from .model_registry import registry
from .technical import analyze_image, preselect_from_technical, qa_from_technical, ImageReadError

app=FastAPI(title="PHOTO AI FACTORY AI Worker", version=__version__, docs_url=None, redoc_url=None)

def err(req: AiRequest, code: str, category: str, message: str, retryable: bool=False, details=None):
    return AiResponse(request_id=req.request_id, success=False, error=AiError(code=code,category=category,retryable=retryable,message=message,details=details))

@app.get("/v1/health", dependencies=[Depends(require_token)])
def health():
    device="cpu"
    try:
        import torch
        if torch.cuda.is_available(): device=torch.cuda.get_device_name(0)
    except Exception:
        pass
    return {"status":"HEALTHY","api_version":"v1","worker_version":__version__,"device":device,"models_loaded":sorted(registry.loaded)}

@app.get("/v1/capabilities", dependencies=[Depends(require_token)])
def capabilities():
    return {"api_version":"v1","implemented":["health","models/status","analyze:technical","preselect:technical","qa:technical"],"planned":["rf-detr","mediapipe","florence","qwen","dinov2","pre-ai-recipe","feedback-inspection"]}

@app.get("/v1/models/status", dependencies=[Depends(require_token)])
def models_status(): return {"models":registry.status()}

@app.post("/v1/analyze", response_model=AiResponse, dependencies=[Depends(require_token)])
def analyze(req: AiRequest):
    start=time.perf_counter()
    if not req.input_paths: return err(req,"MISSING_INPUT","validation","No input path supplied")
    try:
        metrics=analyze_image(req.input_paths[0])
        return AiResponse(request_id=req.request_id,success=True,result={"technical":metrics},timings={"total_ms":(time.perf_counter()-start)*1000})
    except (FileNotFoundError,ImageReadError) as e:
        return err(req,"INPUT_READ_ERROR","input",str(e),False)
    except Exception as e:
        return err(req,"ANALYSIS_ERROR","runtime",str(e),True)

@app.post("/v1/preselect", response_model=AiResponse, dependencies=[Depends(require_token)])
def preselect(req: AiRequest):
    start=time.perf_counter()
    if not req.input_paths: return err(req,"MISSING_INPUT","validation","No input path supplied")
    try:
        metrics=analyze_image(req.input_paths[0])
        result=preselect_from_technical(metrics,req.config)
        return AiResponse(request_id=req.request_id,success=True,result=result,timings={"total_ms":(time.perf_counter()-start)*1000})
    except Exception as e:
        return err(req,"PRESELECT_ERROR","runtime",str(e),False)

@app.post("/v1/qa", response_model=AiResponse, dependencies=[Depends(require_token)])
def qa(req: AiRequest):
    start=time.perf_counter()
    if not req.input_paths: return err(req,"MISSING_INPUT","validation","No input path supplied")
    try:
        metrics=analyze_image(req.input_paths[0])
        result=qa_from_technical(metrics,req.config)
        return AiResponse(request_id=req.request_id,success=True,result=result,timings={"total_ms":(time.perf_counter()-start)*1000})
    except Exception as e:
        return err(req,"QA_ERROR","runtime",str(e),False)

@app.post("/v1/recipe/pre-ai", response_model=AiResponse, dependencies=[Depends(require_token)])
def pre_ai_recipe(req: AiRequest):
    return err(req,"MODEL_PIPELINE_NOT_READY","capability","PRE-AI recipe generation requires the real model/recipe adapters and Darktable Gate DT-01",False)

@app.post("/v1/feedback/inspect", response_model=AiResponse, dependencies=[Depends(require_token)])
def feedback(req: AiRequest):
    return err(req,"MODEL_PIPELINE_NOT_READY","capability","FEEDBACK inspection is intentionally blocked until DT-01 and real model adapters are validated",False)

def run():
    if not settings.token:
        raise SystemExit("PAF_AI_TOKEN must be set")
    uvicorn.run(app, host=settings.host, port=settings.port, log_level=os.getenv("PAF_AI_LOG_LEVEL","info"))
