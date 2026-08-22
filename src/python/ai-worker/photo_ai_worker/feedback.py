from __future__ import annotations

import hashlib
from pathlib import Path
from typing import Any

import cv2
import numpy as np


class FeedbackInputError(ValueError):
    pass


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def _require_dict(value: Any, name: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise FeedbackInputError(f"{name} must be an object")
    return value


def _require_sha256(value: Any, name: str) -> str:
    if not isinstance(value, str) or len(value) != 64:
        raise FeedbackInputError(f"{name} must be a SHA-256 hex string")
    try:
        int(value, 16)
    except ValueError as exc:
        raise FeedbackInputError(
            f"{name} must be a SHA-256 hex string") from exc
    return value.lower()


def inspect_feedback(tiff_path: str, config: dict[str, Any]) -> dict[str, Any]:
    if config.get("schema_version") != 1:
        raise FeedbackInputError("schema_version must be 1")

    analysis = _require_dict(config.get("analysis"), "analysis")
    pass1 = _require_dict(config.get("pass1"), "pass1")
    if analysis.get("schema_version") != 1:
        raise FeedbackInputError("analysis.schema_version must be 1")
    darktable_version = pass1.get("darktable_version")
    if not isinstance(darktable_version, str) or not darktable_version.strip():
        raise FeedbackInputError("pass1.darktable_version is required")
    pass1_image_sha256 = _require_sha256(
        pass1.get("image_sha256"), "pass1.image_sha256")
    pass1_xmp_sha256 = _require_sha256(
        pass1.get("xmp_sha256"), "pass1.xmp_sha256")
    input_kind = config.get("input_kind")
    if input_kind not in {"RAW", "JPEG"}:
        raise FeedbackInputError("input_kind must be RAW or JPEG")

    path = Path(tiff_path)
    if not path.is_file():
        raise FileNotFoundError(f"Pass 1 TIFF does not exist: {path}")

    payload = np.fromfile(path, dtype=np.uint8)
    image = cv2.imdecode(payload, cv2.IMREAD_UNCHANGED)
    if image is None:
        raise FeedbackInputError("Pass 1 TIFF could not be decoded")
    if image.dtype != np.uint16:
        raise FeedbackInputError(
            f"Pass 1 must be 16-bit TIFF, got dtype={image.dtype}")
    if image.ndim != 3 or image.shape[2] not in (3, 4):
        raise FeedbackInputError(
            "Pass 1 TIFF must contain 3 or 4 color channels")

    bgr16 = image[:, :, :3]
    bgr8 = np.right_shift(bgr16, 8).astype(np.uint8)

    height, width = bgr8.shape[:2]
    max_edge = max(width, height)
    if max_edge > 1280:
        scale = 1280.0 / float(max_edge)
        preview = cv2.resize(
            bgr8,
            (max(1, round(width * scale)), max(1, round(height * scale))),
            interpolation=cv2.INTER_AREA,
        )
    else:
        preview = bgr8

    # OpenCV decodes TIFF in BGR; convert the in-memory VLM/inspection preview
    # to the sRGB channel order expected by the FEEDBACK contract.
    rgb_preview = cv2.cvtColor(preview, cv2.COLOR_BGR2RGB)
    gray = cv2.cvtColor(preview, cv2.COLOR_BGR2GRAY)

    full_float = bgr16.astype(np.float32) / 65535.0
    technical = {
        "mean_luminance_approx": round(float(gray.mean()) / 255.0, 6),
        "preview_stddev": round(float(gray.std()) / 255.0, 6),
        "clip_low_fraction": round(float(np.mean(full_float <= (1.0 / 65535.0))), 8),
        "clip_high_fraction": round(float(np.mean(full_float >= (65534.0 / 65535.0))), 8),
        "laplacian_variance_preview": round(
            float(cv2.Laplacian(gray, cv2.CV_64F).var()), 6),
    }

    disabled_reason = "NOT_HEADLESS_PROVEN_AND_BENCHMARK_PENDING"

    recipe = {
        "schema_version": 1,
        "recipe_version": "phase5-feedback-v1",
        "strategy": "CONSERVATIVE_REUSE_PASS1",
        "benchmark_status": "NOT_CALIBRATED",
        "generation_method":
            "DETERMINISTIC_FROM_PASS1_AND_PERSISTED_ANALYSIS",
        "operations": [],
        "pass2_control": {
            "mode": "REUSE_PASS1_XMP",
            "arbitrary_xmp_compilation": False,
            "restart_from_managed_original": True,
            "pass1_derivative_as_source": False,
        },
        "darktable_ai": {
            "raw_denoise": {
                "enabled": False,
                "reason": disabled_reason,
            },
            "rgb_denoise": {
                "enabled": False,
                "reason": disabled_reason,
            },
            "upscale": {
                "enabled": False,
                "reason": disabled_reason,
            },
        },
        "input_policy": {
            "input_kind": input_kind,
            "jpeg_only_skips_raw_specific_stages": input_kind == "JPEG",
        },
        "limitations": [
            "CREATIVE_CORRECTION_BENCHMARK_PENDING",
            "DARKTABLE_NEURAL_RESTORE_NOT_HEADLESS_PROVEN",
            "NO_GENERIC_XMP_SYNTHESIS",
        ],
    }

    inspection = {
        "schema_version": 1,
        "source": "PASS1_TIFF16",
        "source_sha256": _sha256(path),
        "technical": technical,
        "preview": {
            "in_memory_only": True,
            "color_space": "sRGB",
            "width": int(rgb_preview.shape[1]),
            "height": int(rgb_preview.shape[0]),
            "dtype": str(rgb_preview.dtype),
        },
        "pass1": {
            "darktable_version": darktable_version,
            "image_sha256": pass1_image_sha256,
            "xmp_sha256": pass1_xmp_sha256,
        },
        "prior_analysis_present": bool(analysis),
        "semantic_context_reused": bool(
            analysis.get("semantic") or analysis.get("semantics")),
    }

    return {"recipe": recipe, "inspection": inspection}
