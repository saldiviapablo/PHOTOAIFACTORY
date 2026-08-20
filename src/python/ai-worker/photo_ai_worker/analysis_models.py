from __future__ import annotations

import hashlib
import importlib.metadata
import json
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from .model_registry import registry


class ModelMissingError(RuntimeError):
    def __init__(self, model_id: str, detail: str):
        super().__init__(f"{model_id}: {detail}")
        self.model_id = model_id


class ModelExecutionError(RuntimeError):
    def __init__(self, model_id: str, detail: str):
        super().__init__(f"{model_id}: {detail}")
        self.model_id = model_id


class ModelIntegrityError(RuntimeError):
    def __init__(self, model_id: str, detail: str):
        super().__init__(f"{model_id}: {detail}")
        self.model_id = model_id


@dataclass(frozen=True)
class ArtifactIdentity:
    model_id: str
    model_version: str
    artifact_set_sha256: str | None


def package_version(distribution: str) -> str:
    try:
        return importlib.metadata.version(distribution)
    except importlib.metadata.PackageNotFoundError:
        return "unknown"


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def artifact_set_sha256(root: Path) -> str | None:
    """Hash the exact local weight/model artifact set without hashing unrelated caches."""
    if root.is_file():
        return sha256_file(root)

    candidates: list[Path] = []
    for pattern in ("*.safetensors", "*.bin", "*.pth", "*.pt", "*.task", "*.onnx"):
        candidates.extend(path for path in root.rglob(pattern) if path.is_file())
    candidates = sorted(set(candidates), key=lambda p: p.as_posix().lower())
    if not candidates:
        return None

    outer = hashlib.sha256()
    for path in candidates:
        relative = path.relative_to(root).as_posix()
        outer.update(relative.encode("utf-8"))
        outer.update(b"\0")
        outer.update(sha256_file(path).encode("ascii"))
        outer.update(b"\n")
    return outer.hexdigest()


def model_identity(
    model_id: str,
    distribution: str,
    artifact_version: str | None = None,
    artifact_sha256: str | None = None,
) -> ArtifactIdentity:
    root = registry.require(model_id)
    return ArtifactIdentity(
        model_id=model_id,
        model_version=artifact_version or package_version(distribution),
        artifact_set_sha256=artifact_sha256 or registry.artifact_hash(
            model_id, lambda: artifact_set_sha256(root)),
    )


def ensure_local_image(path: str) -> Path:
    lowered = path.strip().lower()
    if lowered.startswith(("http://", "https://", "ftp://")):
        raise ValueError("Network image inputs are forbidden")
    p = Path(path)
    if not p.is_file():
        raise FileNotFoundError(path)
    return p


def json_safe(value: Any) -> Any:
    """Convert common numpy/torch scalar containers to plain JSON-compatible values."""
    if value is None or isinstance(value, (str, int, float, bool)):
        return value
    if isinstance(value, dict):
        return {str(k): json_safe(v) for k, v in value.items()}
    if isinstance(value, (list, tuple)):
        return [json_safe(v) for v in value]
    if hasattr(value, "item"):
        try:
            return json_safe(value.item())
        except Exception:
            pass
    if hasattr(value, "tolist"):
        try:
            return json_safe(value.tolist())
        except Exception:
            pass
    return str(value)
