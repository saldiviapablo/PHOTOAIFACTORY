from dataclasses import dataclass
import os

@dataclass(frozen=True)
class Settings:
    host: str = os.getenv("PAF_AI_HOST", "127.0.0.1")
    port: int = int(os.getenv("PAF_AI_PORT", "8765"))
    token: str = os.getenv("PAF_AI_TOKEN", "")
    models_root: str = os.getenv("PAF_MODELS_ROOT", os.path.join(os.getenv("LOCALAPPDATA", os.path.expanduser("~")), "PhotoAIFactory", "models"))

settings = Settings()
