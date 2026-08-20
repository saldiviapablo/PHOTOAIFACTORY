from __future__ import annotations

import time
from typing import Any

from .adapters import (
    DinoV2Adapter,
    Florence2Adapter,
    MediaPipeFaceAdapter,
    MediaPipePoseAdapter,
    Qwen3VlAdapter,
    RfDetrMediumAdapter,
)
from .technical import analyze_image


class AnalysisPipeline:
    """Fixed V1 analysis line. Stations never reorder per photo.

    Heavy adapters are released immediately after their station to keep peak
    VRAM bounded on the reference RTX 4060 Ti 8 GB.
    """

    def __init__(self) -> None:
        self.rf_detr = RfDetrMediumAdapter()
        self.face = MediaPipeFaceAdapter()
        self.pose = MediaPipePoseAdapter()
        self.florence = Florence2Adapter()
        self.qwen = Qwen3VlAdapter()
        self.dino = DinoV2Adapter()

    def analyze(self, image_path: str, config: dict[str, Any]) -> dict[str, Any]:
        started = time.perf_counter()
        semantic_mode = str(config.get("semantic_mode", "STANDARD")).upper()
        if semantic_mode not in {"OFF", "STANDARD", "FULL"}:
            raise ValueError(f"Unsupported semantic_mode {semantic_mode!r}")

        executions: list[dict[str, Any]] = []

        technical_started = time.perf_counter()
        technical = analyze_image(image_path)
        executions.append({
            "model_id": "opencv",
            "model_version": self._opencv_version(),
            "artifact_set_sha256": None,
            "parameters": {"operation": "technical_analysis"},
            "timings": {"inference_ms": (time.perf_counter() - technical_started) * 1000.0},
        })

        try:
            detections, timing = self.rf_detr.predict(
                image_path, threshold=float(config.get("rfdetr_threshold", 0.35)))
            executions.append(self._execution(
                self.rf_detr, {"threshold": detections["threshold"]}, timing))
        finally:
            self.rf_detr.release()

        try:
            faces, timing = self.face.predict(image_path)
            executions.append(self._execution(self.face, {}, timing))
        finally:
            self.face.release()

        pose = {
            "pose_count": 0,
            "poses": [],
            "skipped": True,
            "reason": "no_person_detected",
        }
        if int(detections.get("person_count", 0)) > 0:
            try:
                pose, timing = self.pose.predict(image_path)
                pose["skipped"] = False
                executions.append(self._execution(self.pose, {}, timing))
            finally:
                self.pose.release()

        semantic: dict[str, Any] = {"mode": semantic_mode}
        if semantic_mode in {"STANDARD", "FULL"}:
            try:
                florence, timing = self.florence.describe(image_path)
                semantic["florence"] = florence
                executions.append(self._execution(
                    self.florence, {"semantic_mode": semantic_mode}, timing))
            finally:
                self.florence.release()

        if semantic_mode == "FULL":
            try:
                qwen, timing = self.qwen.describe(image_path)
                semantic["qwen"] = qwen
                executions.append(self._execution(
                    self.qwen, {"semantic_mode": semantic_mode}, timing))
            finally:
                self.qwen.release()

        try:
            embedding, timing = self.dino.embed(image_path)
            executions.append(self._execution(
                self.dino, {"normalization": "L2"}, timing))
        finally:
            self.dino.release()

        return {
            "schema_version": 1,
            "pipeline_order": [
                "OpenCV", "RF-DETR Medium", "MediaPipe Face",
                "MediaPipe Pose (conditional)", "Florence-2 (mode)",
                "Qwen3-VL (mode)", "DINOv2-S",
            ],
            "analysis_input_kind": config.get("analysis_input_kind"),
            "technical": technical,
            "detections": detections,
            "faces": faces,
            "pose": pose,
            "semantic": semantic,
            "embedding": embedding,
            "model_executions": executions,
            "timings": {"total_ms": (time.perf_counter() - started) * 1000.0},
        }

    def release(self) -> None:
        for adapter in (
            self.qwen, self.florence, self.pose, self.face, self.rf_detr, self.dino
        ):
            adapter.release()

    @staticmethod
    def _execution(adapter: Any, parameters: dict, timings: dict) -> dict:
        identity = adapter.identity
        return {
            "model_id": identity.model_id,
            "model_version": identity.model_version,
            "artifact_set_sha256": identity.artifact_set_sha256,
            "parameters": parameters,
            "timings": timings,
        }

    @staticmethod
    def _opencv_version() -> str:
        import cv2
        return cv2.__version__


pipeline = AnalysisPipeline()
