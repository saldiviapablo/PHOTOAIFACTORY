from __future__ import annotations

from typing import Any


def build_pre_ai_recipe(config: dict[str, Any]) -> dict[str, Any]:
    if config.get("schema_version") != 1:
        raise ValueError("PRE_AI recipe requires schema_version=1")

    analysis = config.get("analysis")
    if not isinstance(analysis, dict):
        raise ValueError("PRE_AI recipe requires persisted Analysis")

    # The PRD leaves the exact creative PRE_AI recipe/model pending benchmark.
    # Phase 4 therefore establishes the normalized contract without inventing
    # unbenchmarked exposure/color thresholds or Darktable XMP internals.
    return {
        "schema_version": 1,
        "recipe_version": "phase4-pre-ai-v1",
        "strategy": "CONSERVATIVE_BASELINE",
        "benchmark_status": "NOT_CALIBRATED",
        "generation_method": "DETERMINISTIC_FROM_PERSISTED_ANALYSIS",
        "operations": [],
        "darktable_control": {
            "mode": "DEFAULT_PIPELINE",
            "arbitrary_xmp_compilation": False,
            "style": None,
            "apply_custom_presets": False,
        },
        "provenance": {
            "analysis_schema_version": analysis.get("schema_version"),
            "authorized_preset_profiles": list(
                config.get("authorized_preset_profiles") or []
            ),
        },
        "limitations": [
            "CREATIVE_THRESHOLDS_BENCHMARK_PENDING",
            "GENERIC_XMP_COMPILER_NOT_AUTHORIZED",
        ],
    }
