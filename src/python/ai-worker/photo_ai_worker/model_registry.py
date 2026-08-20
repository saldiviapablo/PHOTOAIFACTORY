from __future__ import annotations

from pathlib import Path
from threading import RLock
from typing import Callable

from .settings import settings

# Logical IDs remain stable even when a native-runtime conversion requires a
# different physical artifact directory.
BASELINE_IDS = [
    "rf-detr-medium",
    "mediapipe-face-landmarker",
    "mediapipe-pose-landmarker-full",
    "florence-2-large",
    "qwen3-vl-2b-instruct-fp8",
    "dinov2-vits14-standard",
]

MODEL_DIRECTORIES = {
    "florence-2-large": (
        "florence-2-large-native-"
        "4271c66b88cdbc05735372ec13b2360108de5317"
    ),
}


class ModelRegistry:
    def __init__(self, root: str | None = None):
        self.root = Path(root or settings.models_root)
        self.loaded: set[str] = set()
        self._hashes: dict[str, str | None] = {}
        self._lock = RLock()

    def path(self, model_id: str) -> Path:
        if model_id not in BASELINE_IDS:
            raise KeyError(f"Unregistered model id: {model_id}")
        return self.root / MODEL_DIRECTORIES.get(model_id, model_id)

    def require(self, model_id: str) -> Path:
        path = self.path(model_id)
        if not path.exists():
            raise FileNotFoundError(f"Required local model artifact is missing: {path}")
        return path

    def mark_loaded(self, model_id: str) -> None:
        with self._lock:
            self.loaded.add(model_id)

    def mark_released(self, model_id: str) -> None:
        with self._lock:
            self.loaded.discard(model_id)

    def artifact_hash(self, model_id: str, factory: Callable[[], str | None]) -> str | None:
        with self._lock:
            if model_id not in self._hashes:
                self._hashes[model_id] = factory()
            return self._hashes[model_id]

    def status(self) -> list[dict]:
        with self._lock:
            rows = []
            for model_id in BASELINE_IDS:
                path = self.path(model_id)
                rows.append({
                    "id": model_id,
                    "path": str(path),
                    "present": path.exists(),
                    "loaded": model_id in self.loaded,
                    "artifact_set_sha256": self._hashes.get(model_id),
                })
            return rows


registry = ModelRegistry()
