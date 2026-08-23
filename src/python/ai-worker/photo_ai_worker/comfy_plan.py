SUPPORTED_TASKS = (
    "DENOISE_RGB",
    "COLOR",
    "FACE_RETOUCH",
    "FACE_MASKS",
    "LOW_LIGHT",
    "UPSCALE",
    "SHARPNESS",
)


class ComfyPlanInputError(ValueError):
    pass


def _authorized(config):
    raw = config.get("authorized_tasks", [])
    if not isinstance(raw, list):
        raise ComfyPlanInputError("authorized_tasks must be a list")
    result = []
    seen = set()
    for value in raw:
        if not isinstance(value, str) or not value.strip():
            raise ComfyPlanInputError("authorized_tasks must contain non-empty strings")
        task = value.strip().upper()
        if task not in SUPPORTED_TASKS:
            raise ComfyPlanInputError(f"unsupported ComfyUI task: {task}")
        if task not in seen:
            seen.add(task)
            result.append(task)
    return result


def build_comfy_plan(config):
    if not isinstance(config, dict):
        raise ComfyPlanInputError("config must be an object")
    if config.get("schema_version") != 1:
        raise ComfyPlanInputError("schema_version must be 1")

    mode = config.get("mode")
    if not isinstance(mode, str):
        raise ComfyPlanInputError("mode must be OFF, ON or AUTO")
    mode = mode.strip().upper()
    if mode not in {"OFF", "ON", "AUTO"}:
        raise ComfyPlanInputError("mode must be OFF, ON or AUTO")

    authorized = _authorized(config)
    decisions = []
    execution_order = []

    for task in authorized:
        if mode == "OFF":
            action = "SKIP"
            reason = "COMFYUI_OFF"
        elif mode == "ON":
            action = "EXECUTE"
            reason = "MODE_ON_AUTHORIZED"
            execution_order.append(task)
        else:
            action = "SKIP"
            reason = "AUTO_POLICY_NOT_CALIBRATED"

        decisions.append(
            {
                "task_id": task,
                "action": action,
                "reason": reason,
            }
        )

    return {
        "schema_version": 1,
        "plan_version": "phase6-comfy-v1",
        "mode": mode,
        "benchmark_status": "ENHANCEMENT_WORKFLOWS_BENCHMARK_PENDING",
        "decisions": decisions,
        "execution_order": execution_order,
        "limitations": [
            "AUTO_POLICY_NOT_CALIBRATED",
            "PRODUCTION_ENHANCEMENT_WORKFLOWS_REQUIRE_LICENSE_AND_BENCHMARK",
        ],
    }
