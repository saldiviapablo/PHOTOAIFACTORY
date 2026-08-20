from __future__ import annotations

from typing import Any


def preselect_from_analysis(analysis: dict[str, Any], config: dict[str, Any]) -> dict[str, Any]:
    """
    Conservative V1 policy before project-dataset benchmark.

    - Never automatically rejects.
    - Uses specialist/deterministic evidence for technical findings.
    - VLM output is never the sole source for focus/eyes/clipping/corruption.
    - If enabled thresholds are absent, route to REVIEW_PRE rather than invent defaults.
    """
    if not bool(config.get("enabled", True)):
        return {
            "schema_version": 1,
            "decision": "APPROVED",
            "auto_reject_enabled": False,
            "findings": [{"code": "PRESELECTION_DISABLED", "severity": "info"}],
        }

    findings: list[dict[str, Any]] = []
    policy = config.get("policy") or {}
    thresholds = policy.get("thresholds") or {}

    technical = analysis.get("technical") or {}
    sharpness = ((technical.get("sharpness") or {}).get("laplacian_variance"))
    clipping = technical.get("clipping") or {}
    white_clip = clipping.get("white_fraction")
    black_clip = clipping.get("black_fraction")

    configured_any = False

    review_focus = thresholds.get("review_laplacian_variance")
    if review_focus is not None and sharpness is not None:
        configured_any = True
        if float(sharpness) < float(review_focus):
            findings.append({
                "code": "LOW_SHARPNESS",
                "severity": "review",
                "source": "opencv",
                "value": float(sharpness),
                "threshold": float(review_focus),
            })

    review_clip = thresholds.get("review_clipping_fraction")
    if review_clip is not None:
        configured_any = True
        threshold = float(review_clip)
        if white_clip is not None and float(white_clip) > threshold:
            findings.append({
                "code": "HIGHLIGHT_CLIPPING",
                "severity": "review",
                "source": "opencv",
                "value": float(white_clip),
                "threshold": threshold,
            })
        if black_clip is not None and float(black_clip) > threshold:
            findings.append({
                "code": "SHADOW_CLIPPING",
                "severity": "review",
                "source": "opencv",
                "value": float(black_clip),
                "threshold": threshold,
            })

    eye_threshold = thresholds.get("review_eye_blink_probability")
    if eye_threshold is not None:
        configured_any = True
        threshold = float(eye_threshold)
        for index, face in enumerate((analysis.get("faces") or {}).get("faces") or []):
            left = face.get("eye_blink_left")
            right = face.get("eye_blink_right")
            if left is not None and right is not None and min(float(left), float(right)) >= threshold:
                findings.append({
                    "code": "POSSIBLE_EYES_CLOSED",
                    "severity": "review",
                    "source": "mediapipe_face_blendshapes",
                    "face_index": index,
                    "left": float(left),
                    "right": float(right),
                    "threshold": threshold,
                })

    if not configured_any:
        findings.append({
            "code": "PRESELECTION_THRESHOLDS_NOT_BENCHMARKED",
            "severity": "review",
            "message": "No project-benchmarked preselection thresholds were supplied; review is safer than hidden defaults.",
        })

    # Similarity is recorded as an embedding in Analysis. Burst-level comparison requires
    # project context in C# and is intentionally not fabricated inside a single-image worker request.
    findings.append({
        "code": "SIMILARITY_EMBEDDING_AVAILABLE",
        "severity": "info",
        "source": "dinov2",
        "dimension": (analysis.get("embedding") or {}).get("dimension"),
    })

    decision = "REVIEW_PRE" if any(item["severity"] == "review" for item in findings) else "APPROVED"
    # Safety invariant from ADR-018 candidate: auto reject remains disabled until dataset benchmark.
    return {
        "schema_version": 1,
        "decision": decision,
        "auto_reject_enabled": False,
        "findings": findings,
    }
