import os
import importlib
from pathlib import Path
import cv2
import numpy as np
import pytest
from fastapi.testclient import TestClient


@pytest.fixture
def auth_client(monkeypatch):
    monkeypatch.setenv("PAF_AI_TOKEN", "phase7-test-token")
    monkeypatch.delenv("PAF_ALLOW_TEST_FORCE_DECISION", raising=False)
    import photo_ai_worker.settings as settings_module
    import photo_ai_worker.auth as auth_module
    import photo_ai_worker.technical as technical_module
    import photo_ai_worker.main as main_module

    importlib.reload(settings_module)
    importlib.reload(auth_module)
    importlib.reload(technical_module)
    main_module = importlib.reload(main_module)

    client = TestClient(main_module.app)
    return client


AUTH_HEADER = {"Authorization": "Bearer phase7-test-token"}


@pytest.fixture
def sample_jpeg(tmp_path):
    img_path = tmp_path / "sample.jpg"
    img = np.full((200, 200, 3), 128, dtype=np.uint8)
    cv2.circle(img, (100, 100), 50, (255, 0, 0), -1)
    cv2.imwrite(str(img_path), img)
    return str(img_path)


def test_phase7_qa_capabilities(auth_client):
    res = auth_client.get("/v1/capabilities", headers=AUTH_HEADER)
    assert res.status_code == 200
    data = res.json()
    assert "qa:phase7-v1" in data["implemented"]


def test_phase7_qa_pass_and_contract(auth_client, sample_jpeg):
    payload = {
        "api_version": "v1",
        "request_id": "req-qa-pass-1",
        "job_id": "job-1",
        "operation": "qa",
        "input_paths": [sample_jpeg],
        "config": {
            "thresholds": {
                "min_laplacian_variance": 5.0,
                "max_clipping_fraction": 0.5,
            }
        },
    }
    res = auth_client.post("/v1/qa", json=payload, headers=AUTH_HEADER)
    assert res.status_code == 200
    data = res.json()
    assert data["success"] is True
    assert data["request_id"] == "req-qa-pass-1"
    result = data["result"]
    assert result["schema_version"] == 1
    assert result["decision"] == "QA_PASS"
    assert "technical" in result
    assert result["calibration_status"] == "BASELINE_NOT_CALIBRATED"


def test_phase7_qa_review_on_blur(auth_client, tmp_path):
    img_path = tmp_path / "flat.jpg"
    img = np.full((100, 100, 3), 128, dtype=np.uint8)
    cv2.imwrite(str(img_path), img)

    payload = {
        "api_version": "v1",
        "request_id": "req-qa-review",
        "job_id": "job-2",
        "operation": "qa",
        "input_paths": [str(img_path)],
        "config": {
            "thresholds": {
                "min_laplacian_variance": 35.0,
                "reprocess_laplacian_variance": 0.0,
                "max_clipping_fraction": 0.08,
            }
        },
    }
    res = auth_client.post("/v1/qa", json=payload, headers=AUTH_HEADER)
    assert res.status_code == 200
    data = res.json()
    assert data["success"] is True
    assert data["result"]["decision"] == "QA_REVIEW"
    assert any(f["code"] == "LOW_SHARPNESS" for f in data["result"]["findings"])


def test_phase7_qa_reprocess_on_severe_blur(auth_client, tmp_path):
    img_path = tmp_path / "flat.jpg"
    img = np.full((100, 100, 3), 128, dtype=np.uint8)
    cv2.imwrite(str(img_path), img)

    payload = {
        "api_version": "v1",
        "request_id": "req-qa-reprocess",
        "job_id": "job-3",
        "operation": "qa",
        "input_paths": [str(img_path)],
        "config": {
            "thresholds": {
                "min_laplacian_variance": 35.0,
                "reprocess_laplacian_variance": 15.0,
                "max_clipping_fraction": 0.08,
            }
        },
    }
    res = auth_client.post("/v1/qa", json=payload, headers=AUTH_HEADER)
    assert res.status_code == 200
    data = res.json()
    assert data["success"] is True
    assert data["result"]["decision"] == "QA_REPROCESS"
    assert any(f["code"] == "SEVERE_LOW_SHARPNESS" for f in data["result"]["findings"])


def test_phase7_qa_force_decision_ignored_in_production_mode(auth_client, tmp_path):
    # In default production mode (no PAF_ALLOW_TEST_FORCE_DECISION), force_decision must be ignored
    img_path = tmp_path / "flat.jpg"
    img = np.full((100, 100, 3), 128, dtype=np.uint8)
    cv2.imwrite(str(img_path), img)

    payload = {
        "api_version": "v1",
        "request_id": "req-force-prod",
        "job_id": "job-prod",
        "operation": "qa",
        "input_paths": [str(img_path)],
        "config": {
            "force_decision": "QA_PASS",  # Attempt to force PASS on a flat blurry image
            "thresholds": {
                "min_laplacian_variance": 35.0,
                "reprocess_laplacian_variance": 15.0,
            },
        },
    }
    res = auth_client.post("/v1/qa", json=payload, headers=AUTH_HEADER)
    assert res.status_code == 200
    data = res.json()
    assert data["success"] is True
    # Must NOT be QA_PASS since forced decision is ignored in production mode
    assert data["result"]["decision"] == "QA_REPROCESS"


def test_phase7_qa_forced_decisions_under_test_flag(monkeypatch, sample_jpeg):
    monkeypatch.setenv("PAF_AI_TOKEN", "phase7-test-token")
    monkeypatch.setenv("PAF_ALLOW_TEST_FORCE_DECISION", "1")
    import photo_ai_worker.settings as settings_module
    import photo_ai_worker.auth as auth_module
    import photo_ai_worker.technical as technical_module
    import photo_ai_worker.main as main_module

    importlib.reload(settings_module)
    importlib.reload(auth_module)
    importlib.reload(technical_module)
    main_module = importlib.reload(main_module)
    client = TestClient(main_module.app)

    for decision in ["QA_PASS", "QA_REVIEW", "QA_REPROCESS", "QA_TECH_RETRY", "QA_FATAL"]:
        payload = {
            "api_version": "v1",
            "request_id": f"req-{decision}",
            "job_id": "job-forced",
            "operation": "qa",
            "input_paths": [sample_jpeg],
            "config": {"force_decision": decision},
        }
        res = client.post("/v1/qa", json=payload, headers=AUTH_HEADER)
        assert res.status_code == 200
        data = res.json()
        assert data["success"] is True
        assert data["result"]["decision"] == decision


def test_phase7_qa_missing_input_validation_error(auth_client):
    payload = {
        "api_version": "v1",
        "request_id": "req-qa-missing-input",
        "job_id": "job-missing-in",
        "operation": "qa",
        "input_paths": [],
        "config": {},
    }
    res = auth_client.post("/v1/qa", json=payload, headers=AUTH_HEADER)
    assert res.status_code == 200
    data = res.json()
    assert data["success"] is False
    assert data["error"]["code"] == "MISSING_INPUT"
    assert data["error"]["category"] == "validation"
    assert data["error"]["retryable"] is False


def test_phase7_qa_missing_file_error(auth_client):
    payload = {
        "api_version": "v1",
        "request_id": "req-qa-missing-file",
        "job_id": "job-missing-file",
        "operation": "qa",
        "input_paths": ["C:\\non_existent_image_12345.jpg"],
        "config": {},
    }
    res = auth_client.post("/v1/qa", json=payload, headers=AUTH_HEADER)
    assert res.status_code == 200
    data = res.json()
    assert data["success"] is False
    assert data["error"]["code"] == "INPUT_READ_ERROR"
    assert data["error"]["category"] == "input"
    assert data["error"]["retryable"] is False
