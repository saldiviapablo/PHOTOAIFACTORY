from pathlib import Path
from .settings import settings

BASELINE_IDS = [
    "rf-detr-medium", "mediapipe-face-landmarker", "mediapipe-pose-landmarker",
    "florence-2-large", "qwen3-vl-2b-instruct", "dinov2-s-standard"
]

class ModelRegistry:
    def __init__(self, root: str | None = None):
        self.root = Path(root or settings.models_root)
        self.loaded: set[str] = set()

    def status(self) -> list[dict]:
        rows=[]
        for mid in BASELINE_IDS:
            p=self.root/mid
            rows.append({"id":mid,"path":str(p),"present":p.exists(),"loaded":mid in self.loaded})
        return rows

registry=ModelRegistry()
