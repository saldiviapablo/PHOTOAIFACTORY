from pathlib import Path
import cv2
import numpy as np

from .settings import settings

class ImageReadError(RuntimeError): pass

def analyze_image(path: str) -> dict:
    p=Path(path)
    if not p.exists(): raise FileNotFoundError(path)
    encoded=np.frombuffer(p.read_bytes(), dtype=np.uint8)
    image=cv2.imdecode(encoded, cv2.IMREAD_COLOR)
    if image is None: raise ImageReadError(f"OpenCV could not decode {path}")
    gray=cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
    h,w=gray.shape
    pixels=gray.astype(np.float32)
    p01,p50,p99=np.percentile(pixels,[1,50,99]).tolist()
    clipping_black=float(np.mean(gray <= 2))
    clipping_white=float(np.mean(gray >= 253))
    lap_var=float(cv2.Laplacian(gray, cv2.CV_64F).var())
    b,g,r=[float(x) for x in cv2.mean(image)[:3]]
    return {
        "width":int(w),"height":int(h),"megapixels":round(w*h/1_000_000,3),
        "luma":{"p01":p01,"p50":p50,"p99":p99,"mean":float(pixels.mean())},
        "clipping":{"black_fraction":clipping_black,"white_fraction":clipping_white},
        "sharpness":{"laplacian_variance":lap_var},
        "channel_means":{"r":r,"g":g,"b":b},
    }

def preselect_from_technical(metrics: dict, config: dict) -> dict:
    thresholds=config.get("thresholds",{})
    reject_focus=float(thresholds.get("reject_laplacian_variance", 20.0))
    review_focus=float(thresholds.get("review_laplacian_variance", 60.0))
    max_clip=float(thresholds.get("review_clipping_fraction", 0.10))
    score=metrics["sharpness"]["laplacian_variance"]
    findings=[]
    decision="APPROVED"
    if score < reject_focus:
        decision="REJECTED_PRE"; findings.append({"code":"VERY_LOW_SHARPNESS","score":score})
    elif score < review_focus:
        decision="REVIEW_PRE"; findings.append({"code":"LOW_SHARPNESS","score":score})
    if max(metrics["clipping"]["black_fraction"],metrics["clipping"]["white_fraction"]) > max_clip and decision == "APPROVED":
        decision="REVIEW_PRE"; findings.append({"code":"HIGH_CLIPPING"})
    return {"decision":decision,"findings":findings,"technical":metrics}

def qa_from_technical(metrics: dict, config: dict) -> dict:
    forced = config.get("force_decision")
    if forced and getattr(settings, "allow_test_force_decision", False):
        if forced in ("QA_PASS", "QA_REVIEW", "QA_REPROCESS", "QA_TECH_RETRY", "QA_FATAL"):
            return {
                "schema_version": 1,
                "decision": forced,
                "findings": [{"code": f"FORCED_{forced}", "severity": "info", "message": f"Forced decision {forced}"}] if forced != "QA_PASS" else [],
                "suggested_correction": None,
                "technical": metrics,
                "calibration_status": "BASELINE_NOT_CALIBRATED",
            }

    thresholds = config.get("thresholds", {})
    min_focus = float(thresholds.get("min_laplacian_variance", 35.0))
    reprocess_focus = float(thresholds.get("reprocess_laplacian_variance", 15.0))
    max_clip = float(thresholds.get("max_clipping_fraction", 0.08))
    findings = []

    score = metrics["sharpness"]["laplacian_variance"]
    decision = "QA_PASS"

    if score < reprocess_focus:
        findings.append({"code": "SEVERE_LOW_SHARPNESS", "severity": "reprocess", "message": "Severe low sharpness", "score": score})
        decision = "QA_REPROCESS"
    elif score < min_focus:
        findings.append({"code": "LOW_SHARPNESS", "severity": "review", "message": "Low technical sharpness", "score": score})
        decision = "QA_REVIEW"

    if metrics["clipping"]["white_fraction"] > max_clip:
        findings.append({"code": "HIGHLIGHT_CLIPPING", "severity": "review", "message": "High highlight clipping", "score": metrics["clipping"]["white_fraction"]})
        if decision == "QA_PASS":
            decision = "QA_REVIEW"

    if metrics["clipping"]["black_fraction"] > max_clip:
        findings.append({"code": "SHADOW_CLIPPING", "severity": "review", "message": "High shadow clipping", "score": metrics["clipping"]["black_fraction"]})
        if decision == "QA_PASS":
            decision = "QA_REVIEW"

    return {
        "schema_version": 1,
        "decision": decision,
        "findings": findings,
        "suggested_correction": None,
        "technical": metrics,
        "calibration_status": "BASELINE_NOT_CALIBRATED",
    }
