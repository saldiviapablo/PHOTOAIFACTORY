from __future__ import annotations

from pathlib import Path

import cv2
import numpy as np

from photo_ai_worker.preselection import preselect_from_analysis
from photo_ai_worker.technical import analyze_image
from photo_ai_worker.adapters import Florence2Adapter
from photo_ai_worker.analysis_models import ModelIntegrityError, ModelMissingError
from photo_ai_worker.contracts import AiRequest
from photo_ai_worker import main as worker_main
from photo_ai_worker.model_registry import MODEL_DIRECTORIES, registry


def _image(tmp_path: Path) -> Path:
    path = tmp_path / "unicode espacio á.jpg"
    data = np.full((64, 96, 3), 128, dtype=np.uint8)
    success, encoded = cv2.imencode(".jpg", data)
    assert success
    path.write_bytes(encoded.tobytes())
    return path


def test_opencv_analysis_is_structured(tmp_path):
    result = analyze_image(str(_image(tmp_path)))
    assert result["width"] == 96
    assert result["height"] == 64
    assert "laplacian_variance" in result["sharpness"]
    assert "white_fraction" in result["clipping"]


def test_preselection_without_benchmarked_thresholds_routes_to_review(tmp_path):
    analysis = {
        "technical": analyze_image(str(_image(tmp_path))),
        "faces": {"face_count": 0, "faces": []},
        "embedding": {"dimension": 384},
    }
    result = preselect_from_analysis(analysis, {"enabled": True})
    assert result["decision"] == "REVIEW_PRE"
    assert result["auto_reject_enabled"] is False
    assert any(item["code"] == "PRESELECTION_THRESHOLDS_NOT_BENCHMARKED" for item in result["findings"])


def test_preselection_disabled_approves_without_rejection():
    result = preselect_from_analysis({}, {"enabled": False})
    assert result["decision"] == "APPROVED"
    assert result["auto_reject_enabled"] is False


def test_preselection_never_auto_rejects_even_for_extreme_evidence():
    analysis = {
        "technical": {
            "sharpness": {"laplacian_variance": 0.0},
            "clipping": {"white_fraction": 1.0, "black_fraction": 0.0},
        },
        "faces": {"faces": [{
            "eye_blink_left": 1.0,
            "eye_blink_right": 1.0,
        }]},
        "embedding": {"dimension": 384},
    }
    config = {
        "enabled": True,
        "allow_auto_reject": True,
        "policy": {"thresholds": {
            "review_laplacian_variance": 9999,
            "review_clipping_fraction": 0.01,
            "review_eye_blink_probability": 0.8,
        }},
    }
    result = preselect_from_analysis(analysis, config)
    assert result["decision"] == "REVIEW_PRE"
    assert result["auto_reject_enabled"] is False


def test_florence_native_artifact_identity_is_exactly_pinned():
    adapter = Florence2Adapter()
    assert adapter.artifact_version == "4271c66b88cdbc05735372ec13b2360108de5317"
    assert adapter.artifact_sha256 == (
        "7715423d6549bf1e71188bdd84f4ac960cc0597886af24a5ef7b66f128660685"
    )
    assert MODEL_DIRECTORIES[adapter.model_id].endswith(adapter.artifact_version)


def test_florence_missing_and_wrong_artifacts_fail_before_model_load(tmp_path):
    original_root = registry.root
    try:
        registry.root = tmp_path
        model_root = tmp_path / MODEL_DIRECTORIES["florence-2-large"]
        model_root.mkdir()
        registry._hashes.clear()

        try:
            Florence2Adapter().load()
        except ModelMissingError:
            pass
        else:
            raise AssertionError("Missing Florence weight did not fail structurally")

        (model_root / "model.safetensors").write_bytes(b"partial-corrupt-artifact")
        registry._hashes.clear()
        try:
            Florence2Adapter().load()
        except ModelIntegrityError:
            pass
        else:
            raise AssertionError("Wrong Florence hash did not fail structurally")
    finally:
        registry.root = original_root
        registry._hashes.clear()


def test_florence_integrity_failure_is_a_non_retryable_structured_worker_error(monkeypatch):
    def fail_integrity(_path, _config):
        raise ModelIntegrityError("florence-2-large", "artifact hash mismatch")

    monkeypatch.setattr(worker_main.pipeline, "analyze", fail_integrity)
    response = worker_main.analyze(AiRequest(
        request_id="integrity-audit",
        job_id="audit-job",
        operation="analyze",
        input_paths=["managed-fixture.jpg"],
        config={"schema_version": 1, "semantic_mode": "STANDARD"},
    ))

    assert response.success is False
    assert response.error is not None
    assert response.error.code == "MODEL_INTEGRITY_ERROR"
    assert response.error.category == "model"
    assert response.error.retryable is False
    assert response.error.details == {"model_id": "florence-2-large"}
