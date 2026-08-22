import os
import time

from fastapi import Depends, FastAPI
import uvicorn

from . import __version__
from .analysis_models import ModelExecutionError, ModelIntegrityError, ModelMissingError
from .analysis_pipeline import pipeline
from .auth import require_token
from .contracts import AiError, AiRequest, AiResponse
from .feedback import FeedbackInputError, inspect_feedback
from .model_registry import registry
from .preselection import preselect_from_analysis
from .recipes import build_pre_ai_recipe
from .settings import settings
from .technical import ImageReadError, analyze_image, qa_from_technical

app = FastAPI(
    title="PHOTO AI FACTORY AI Worker",
    version=__version__,
    docs_url=None,
    redoc_url=None,
)


def err(
    req: AiRequest,
    code: str,
    category: str,
    message: str,
    retryable: bool = False,
    details=None,
):
    return AiResponse(
        request_id=req.request_id,
        success=False,
        error=AiError(
            code=code,
            category=category,
            retryable=retryable,
            component="python-ai-worker",
            message=message,
            details=details,
        ),
    )


@app.get("/v1/health", dependencies=[Depends(require_token)])
def health():
    device = "cpu"
    try:
        import torch
        if torch.cuda.is_available():
            device = torch.cuda.get_device_name(0)
    except Exception:
        pass
    return {
        "status": "HEALTHY",
        "api_version": "v1",
        "worker_version": __version__,
        "device": device,
        "models_loaded": sorted(registry.loaded),
    }


@app.get("/v1/capabilities", dependencies=[Depends(require_token)])
def capabilities():
    return {
        "api_version": "v1",
        "implemented": [
            "health",
            "models/status",
            "models/release",
            "analyze:phase3-v1",
            "preselect:phase3-v1",
            "recipe/pre-ai:phase4-v1",
            "feedback/inspect:phase5-v1",
            "qa:technical",
        ],
        "planned": [],
    }


@app.get("/v1/models/status", dependencies=[Depends(require_token)])
def models_status():
    return {"models": registry.status()}


@app.post("/v1/models/release", response_model=AiResponse, dependencies=[Depends(require_token)])
def models_release(req: AiRequest):
    start = time.perf_counter()
    try:
        pipeline.release()
        return AiResponse(
            request_id=req.request_id,
            success=True,
            result={"released": True, "models_loaded": sorted(registry.loaded)},
            timings={"total_ms": (time.perf_counter() - start) * 1000},
        )
    except Exception as exc:
        return err(req, "MODEL_RELEASE_ERROR", "resource", str(exc), True)


@app.post("/v1/analyze", response_model=AiResponse, dependencies=[Depends(require_token)])
def analyze(req: AiRequest):
    start = time.perf_counter()
    if not req.input_paths:
        return err(req, "MISSING_INPUT", "validation", "No input path supplied")
    try:
        if req.config.get("schema_version") == 1:
            result = pipeline.analyze(req.input_paths[0], req.config)
        else:
            # Preserve the Phase 0 technical-analysis contract for callers that
            # do not opt into the versioned Phase 3 envelope.
            result = {"technical": analyze_image(req.input_paths[0])}
        return AiResponse(
            request_id=req.request_id,
            success=True,
            result=result,
            timings={"total_ms": (time.perf_counter() - start) * 1000},
        )
    except FileNotFoundError as exc:
        return err(req, "INPUT_OR_MODEL_MISSING", "input", str(exc), False)
    except ImageReadError as exc:
        return err(req, "INPUT_READ_ERROR", "input", str(exc), False)
    except ModelMissingError as exc:
        return err(
            req, "MODEL_MISSING", "model", str(exc), False,
            {"model_id": exc.model_id})
    except ModelIntegrityError as exc:
        return err(
            req, "MODEL_INTEGRITY_ERROR", "model", str(exc), False,
            {"model_id": exc.model_id})
    except ModelExecutionError as exc:
        message = str(exc)
        oom = "out of memory" in message.lower() or "cuda oom" in message.lower()
        return err(
            req,
            "GPU_OOM" if oom else "MODEL_EXECUTION_ERROR",
            "resource" if oom else "model_runtime",
            message,
            True,
            {"model_id": exc.model_id},
        )
    except ValueError as exc:
        return err(req, "INVALID_ANALYSIS_CONFIG", "validation", str(exc), False)
    except Exception as exc:
        message = str(exc)
        oom = "out of memory" in message.lower() or "cuda oom" in message.lower()
        return err(
            req,
            "GPU_OOM" if oom else "ANALYSIS_ERROR",
            "resource" if oom else "runtime",
            message,
            True,
        )


@app.post("/v1/preselect", response_model=AiResponse, dependencies=[Depends(require_token)])
def preselect(req: AiRequest):
    start = time.perf_counter()
    try:
        analysis = req.config.get("analysis")
        if not isinstance(analysis, dict):
            # Compatibility path for focused worker tests; full C# Phase 3 passes the persisted Analysis.
            if not req.input_paths:
                return err(req, "MISSING_ANALYSIS", "validation", "No persisted Analysis supplied")
            analysis = {
                "technical": analyze_image(req.input_paths[0]),
                "faces": {"face_count": 0, "faces": []},
                "embedding": {},
            }
        result = preselect_from_analysis(analysis, req.config)
        return AiResponse(
            request_id=req.request_id,
            success=True,
            result=result,
            timings={"total_ms": (time.perf_counter() - start) * 1000},
        )
    except (FileNotFoundError, ImageReadError) as exc:
        return err(req, "INPUT_READ_ERROR", "input", str(exc), False)
    except Exception as exc:
        return err(req, "PRESELECT_ERROR", "runtime", str(exc), False)


@app.post("/v1/qa", response_model=AiResponse, dependencies=[Depends(require_token)])
def qa(req: AiRequest):
    start = time.perf_counter()
    if not req.input_paths:
        return err(req, "MISSING_INPUT", "validation", "No input path supplied")
    try:
        metrics = analyze_image(req.input_paths[0])
        result = qa_from_technical(metrics, req.config)
        return AiResponse(
            request_id=req.request_id,
            success=True,
            result=result,
            timings={"total_ms": (time.perf_counter() - start) * 1000},
        )
    except Exception as exc:
        return err(req, "QA_ERROR", "runtime", str(exc), False)


@app.post("/v1/recipe/pre-ai", response_model=AiResponse, dependencies=[Depends(require_token)])
def pre_ai_recipe(req: AiRequest):
    start = time.perf_counter()
    try:
        result = build_pre_ai_recipe(req.config)
        return AiResponse(
            request_id=req.request_id,
            success=True,
            result=result,
            timings={"total_ms": (time.perf_counter() - start) * 1000},
        )
    except ValueError as exc:
        return err(
            req,
            "INVALID_PRE_AI_RECIPE_INPUT",
            "validation",
            str(exc),
            False,
        )
    except Exception as exc:
        return err(req, "PRE_AI_RECIPE_ERROR", "runtime", str(exc), False)


@app.post("/v1/feedback/inspect", response_model=AiResponse, dependencies=[Depends(require_token)])
def feedback(req: AiRequest):
    start = time.perf_counter()
    if not req.input_paths:
        return err(
            req,
            "MISSING_INPUT",
            "validation",
            "FEEDBACK inspection requires the Pass 1 TIFF path",
            False,
        )
    try:
        result = inspect_feedback(req.input_paths[0], req.config)
        return AiResponse(
            request_id=req.request_id,
            success=True,
            result=result,
            timings={"total_ms": (time.perf_counter() - start) * 1000},
        )
    except FeedbackInputError as exc:
        return err(
            req,
            "INVALID_FEEDBACK_INPUT",
            "validation",
            str(exc),
            False,
        )
    except FileNotFoundError as exc:
        return err(
            req,
            "FEEDBACK_INPUT_MISSING",
            "input",
            str(exc),
            False,
        )
    except Exception as exc:
        return err(
            req,
            "FEEDBACK_INSPECTION_ERROR",
            "runtime",
            str(exc),
            True,
        )


def run():
    if not settings.token:
        raise SystemExit("PAF_AI_TOKEN must be set")
    os.environ.setdefault("HF_HUB_OFFLINE", "1")
    os.environ.setdefault("TRANSFORMERS_OFFLINE", "1")
    os.environ.setdefault("HF_DATASETS_OFFLINE", "1")
    uvicorn.run(
        app,
        host=settings.host,
        port=settings.port,
        log_level=os.getenv("PAF_AI_LOG_LEVEL", "info"),
    )
