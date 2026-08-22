from __future__ import annotations

from pathlib import Path
import hashlib

import cv2
import numpy as np
import pytest

from photo_ai_worker.feedback import FeedbackInputError, inspect_feedback


def _write_tiff16(path: Path) -> None:
    image = np.zeros((48, 64, 3), dtype=np.uint16)
    image[:, :, 0] = 8192
    image[:, :, 1] = 32768
    image[:, :, 2] = 49152
    ok, encoded = cv2.imencode(".tif", image)
    assert ok
    encoded.tofile(path)


def _config(input_kind: str = "RAW") -> dict:
    return {
        "schema_version": 1,
        "analysis": {
            "schema_version": 1,
            "technical": {"mean_luma": 0.5},
            "semantic": {"caption": "test"},
        },
        "input_kind": input_kind,
        "pass1": {
            "darktable_version": "darktable 5.6.0",
            "image_sha256": "a" * 64,
            "xmp_sha256": "b" * 64,
        },
    }


def test_feedback_inspection_is_deterministic_and_conservative(tmp_path: Path):
    path = tmp_path / "pass1.tif"
    _write_tiff16(path)

    first = inspect_feedback(str(path), _config())
    second = inspect_feedback(str(path), _config())

    assert first == second
    recipe = first["recipe"]
    assert recipe["schema_version"] == 1
    assert recipe["recipe_version"] == "phase5-feedback-v1"
    assert recipe["strategy"] == "CONSERVATIVE_REUSE_PASS1"
    assert recipe["benchmark_status"] == "NOT_CALIBRATED"
    assert recipe["operations"] == []
    assert recipe["pass2_control"]["mode"] == "REUSE_PASS1_XMP"
    assert recipe["pass2_control"]["restart_from_managed_original"] is True
    assert recipe["pass2_control"]["pass1_derivative_as_source"] is False
    assert recipe["darktable_ai"]["raw_denoise"]["enabled"] is False
    assert recipe["darktable_ai"]["rgb_denoise"]["enabled"] is False
    assert recipe["darktable_ai"]["upscale"]["enabled"] is False

    inspection = first["inspection"]
    assert inspection["source"] == "PASS1_TIFF16"
    assert inspection["preview"]["in_memory_only"] is True
    assert inspection["preview"]["color_space"] == "sRGB"
    assert inspection["preview"]["dtype"] == "uint8"


def test_feedback_jpeg_only_marks_raw_specific_skip(tmp_path: Path):
    path = tmp_path / "pass1.tif"
    _write_tiff16(path)

    result = inspect_feedback(str(path), _config("JPEG"))

    assert result["recipe"]["input_policy"]["input_kind"] == "JPEG"
    assert (
        result["recipe"]["input_policy"]["jpeg_only_skips_raw_specific_stages"]
        is True
    )
    assert result["recipe"]["darktable_ai"]["raw_denoise"]["enabled"] is False


def test_feedback_rejects_8bit_pass1(tmp_path: Path):
    path = tmp_path / "pass1.tif"
    image = np.zeros((16, 16, 3), dtype=np.uint8)
    ok, encoded = cv2.imencode(".tif", image)
    assert ok
    encoded.tofile(path)

    with pytest.raises(FeedbackInputError):
        inspect_feedback(str(path), _config())


def test_feedback_requires_analysis_and_supported_input_kind(tmp_path: Path):
    path = tmp_path / "pass1.tif"
    _write_tiff16(path)

    with pytest.raises(FeedbackInputError):
        inspect_feedback(
            str(path),
            {"schema_version": 1, "input_kind": "RAW", "pass1": {}},
        )

    with pytest.raises(FeedbackInputError):
        inspect_feedback(str(path), {**_config(), "input_kind": "DNG"})


def test_feedback_rejects_missing_pass1_identity_and_missing_file(tmp_path: Path):
    path = tmp_path / "pass1.tif"
    _write_tiff16(path)

    malformed = _config()
    malformed["pass1"] = {"darktable_version": "darktable 5.6.0"}
    with pytest.raises(FeedbackInputError):
        inspect_feedback(str(path), malformed)

    with pytest.raises(FileNotFoundError):
        inspect_feedback(str(tmp_path / "missing.tif"), _config())


def test_feedback_http_contract_correlation_and_input_errors(
    tmp_path: Path, monkeypatch
):
    import importlib

    from fastapi.testclient import TestClient

    monkeypatch.setenv("PAF_AI_TOKEN", "phase5-test-token")
    import photo_ai_worker.settings as settings_module
    import photo_ai_worker.auth as auth_module
    import photo_ai_worker.main as main_module

    importlib.reload(settings_module)
    importlib.reload(auth_module)
    main_module = importlib.reload(main_module)
    client = TestClient(main_module.app)
    headers = {"Authorization": "Bearer phase5-test-token"}

    path = tmp_path / "Pass 1 Ω.tif"
    _write_tiff16(path)
    before = hashlib.sha256(path.read_bytes()).hexdigest()
    request_id = "phase5-request-Ω"
    payload = {
        "api_version": "v1",
        "request_id": request_id,
        "job_id": "phase5-job-1",
        "operation": "feedback.inspect",
        "input_paths": [str(path)],
        "config": _config("JPEG"),
    }

    response = client.post("/v1/feedback/inspect", headers=headers, json=payload)
    body = response.json()
    assert response.status_code == 200
    assert body["success"] is True
    assert body["request_id"] == request_id
    assert body["result"]["recipe"]["operations"] == []
    assert hashlib.sha256(path.read_bytes()).hexdigest() == before

    missing = client.post(
        "/v1/feedback/inspect",
        headers=headers,
        json={**payload, "request_id": "missing", "input_paths": []},
    ).json()
    assert missing["success"] is False
    assert missing["request_id"] == "missing"
    assert missing["error"]["code"] == "MISSING_INPUT"

    absent = client.post(
        "/v1/feedback/inspect",
        headers=headers,
        json={
            **payload,
            "request_id": "absent",
            "input_paths": [str(tmp_path / "absent.tif")],
        },
    ).json()
    assert absent["success"] is False
    assert absent["error"]["code"] == "FEEDBACK_INPUT_MISSING"

    corrupt_path = tmp_path / "corrupt.tif"
    corrupt_path.write_bytes(b"not-a-tiff")
    corrupt = client.post(
        "/v1/feedback/inspect",
        headers=headers,
        json={
            **payload,
            "request_id": "corrupt",
            "input_paths": [str(corrupt_path)],
        },
    ).json()
    assert corrupt["success"] is False
    assert corrupt["error"]["code"] == "INVALID_FEEDBACK_INPUT"
