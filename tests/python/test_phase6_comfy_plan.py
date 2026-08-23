import pytest

from photo_ai_worker.comfy_plan import ComfyPlanInputError, build_comfy_plan


def test_off_never_executes():
    plan = build_comfy_plan(
        {
            "schema_version": 1,
            "mode": "OFF",
            "authorized_tasks": ["DENOISE_RGB", "COLOR"],
        }
    )
    assert plan["execution_order"] == []
    assert all(item["action"] == "SKIP" for item in plan["decisions"])
    assert all(item["reason"] == "COMFYUI_OFF" for item in plan["decisions"])


def test_on_requests_exactly_authorized_tasks():
    plan = build_comfy_plan(
        {
            "schema_version": 1,
            "mode": "ON",
            "authorized_tasks": ["denoise_rgb", "UPSCALE", "denoise_rgb"],
        }
    )
    assert plan["execution_order"] == ["DENOISE_RGB", "UPSCALE"]
    assert [item["task_id"] for item in plan["decisions"]] == [
        "DENOISE_RGB",
        "UPSCALE",
    ]


def test_auto_is_conservative_until_benchmark():
    plan = build_comfy_plan(
        {
            "schema_version": 1,
            "mode": "AUTO",
            "authorized_tasks": ["FACE_RETOUCH", "LOW_LIGHT"],
        }
    )
    assert plan["execution_order"] == []
    assert {
        item["reason"] for item in plan["decisions"]
    } == {"AUTO_POLICY_NOT_CALIBRATED"}


def test_on_with_no_tasks_is_valid_noop():
    plan = build_comfy_plan(
        {
            "schema_version": 1,
            "mode": "ON",
            "authorized_tasks": [],
        }
    )
    assert plan["decisions"] == []
    assert plan["execution_order"] == []


@pytest.mark.parametrize(
    "config",
    [
        {"schema_version": 2, "mode": "OFF", "authorized_tasks": []},
        {"schema_version": 1, "mode": "BAD", "authorized_tasks": []},
        {"schema_version": 1, "mode": "ON", "authorized_tasks": ["NOT_A_TASK"]},
        {"schema_version": 1, "mode": "ON", "authorized_tasks": "UPSCALE"},
    ],
)
def test_invalid_config_fails_closed(config):
    with pytest.raises(ComfyPlanInputError):
        build_comfy_plan(config)
