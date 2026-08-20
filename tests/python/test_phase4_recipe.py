import pytest

from photo_ai_worker.recipes import build_pre_ai_recipe


def test_phase4_recipe_is_deterministic_and_conservative():
    config = {
        "schema_version": 1,
        "analysis": {
            "schema_version": 1,
            "technical": {"mean_luma": 0.5},
        },
        "authorized_preset_profiles": ["BASE"],
    }

    first = build_pre_ai_recipe(config)
    second = build_pre_ai_recipe(config)

    assert first == second
    assert first["recipe_version"] == "phase4-pre-ai-v1"
    assert first["strategy"] == "CONSERVATIVE_BASELINE"
    assert first["benchmark_status"] == "NOT_CALIBRATED"
    assert first["operations"] == []
    assert first["darktable_control"]["mode"] == "DEFAULT_PIPELINE"
    assert first["darktable_control"]["arbitrary_xmp_compilation"] is False


def test_phase4_recipe_requires_persisted_analysis():
    with pytest.raises(ValueError):
        build_pre_ai_recipe({"schema_version": 1})


def test_phase4_recipe_rejects_unknown_schema():
    with pytest.raises(ValueError):
        build_pre_ai_recipe(
            {
                "schema_version": 2,
                "analysis": {"schema_version": 1},
            }
        )


def test_phase4_pre_ai_http_contract_and_correlation(monkeypatch):
    import importlib

    from fastapi.testclient import TestClient

    monkeypatch.setenv("PAF_AI_TOKEN", "phase4-test-token")
    import photo_ai_worker.settings as settings_module
    import photo_ai_worker.auth as auth_module
    import photo_ai_worker.main as main_module

    importlib.reload(settings_module)
    importlib.reload(auth_module)
    main_module = importlib.reload(main_module)

    request_id = "phase4-request-1"
    response = TestClient(main_module.app).post(
        "/v1/recipe/pre-ai",
        headers={"Authorization": "Bearer phase4-test-token"},
        json={
            "api_version": "v1",
            "request_id": request_id,
            "job_id": "phase4-job-1",
            "operation": "recipe.pre-ai",
            "input_paths": [r"C:\managed fixtures\foto Ω.ARW"],
            "config": {
                "schema_version": 1,
                "analysis": {"schema_version": 1, "technical": {}},
            },
        },
    )

    assert response.status_code == 200
    body = response.json()
    assert body["api_version"] == "v1"
    assert body["request_id"] == request_id
    assert body["success"] is True
    assert body["result"]["recipe_version"] == "phase4-pre-ai-v1"
    assert body["result"]["operations"] == []
