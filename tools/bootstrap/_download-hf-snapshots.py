from __future__ import annotations

import argparse
import json
from pathlib import Path

from huggingface_hub import snapshot_download


MODELS = {
    "rf-detr-medium": {
        "repo_id": "Roboflow/rf-detr-medium",
        "revision": "1b5b672408f86dd38e05dd3cf3f2e0834e545a59",
        "allow_patterns": [
            ".gitattributes",
            "README.md",
            "config.json",
            "model.safetensors",
            "preprocessor_config.json",
        ],
    },
    "florence-2-large": {
        "repo_id": "microsoft/Florence-2-large",
        "revision": "21a599d414c4d928c9032694c424fb94458e3594",
        "allow_patterns": [
            ".gitattributes",
            "CODE_OF_CONDUCT.md",
            "LICENSE",
            "README.md",
            "SECURITY.md",
            "SUPPORT.md",
            "config.json",
            "configuration_florence2.py",
            "generation_config.json",
            "model.safetensors",
            "modeling_florence2.py",
            "preprocessor_config.json",
            "processing_florence2.py",
            "tokenizer.json",
            "tokenizer_config.json",
            "vocab.json",
        ],
    },
    "qwen3-vl-2b-instruct-fp8": {
        "repo_id": "Qwen/Qwen3-VL-2B-Instruct-FP8",
        "revision": "46485250d8854c0a9be4f1adbc67ca47e5bb6fa5",
        "allow_patterns": [
            ".gitattributes",
            "README.md",
            "chat_template.json",
            "config.json",
            "generation_config.json",
            "model-00001-of-00001.safetensors",
            "model.safetensors.index.json",
            "preprocessor_config.json",
            "tokenizer.json",
            "tokenizer_config.json",
            "video_preprocessor_config.json",
            "vocab.json",
        ],
    },
}


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--models-root", required=True, type=Path)
    args = parser.parse_args()

    args.models_root.mkdir(parents=True, exist_ok=True)
    results = []
    for model_id, spec in MODELS.items():
        target = args.models_root / model_id
        resolved = snapshot_download(
            repo_id=spec["repo_id"],
            revision=spec["revision"],
            allow_patterns=spec["allow_patterns"],
            local_dir=target,
        )
        results.append(
            {
                "model_id": model_id,
                "repo_id": spec["repo_id"],
                "revision": spec["revision"],
                "local_path": str(Path(resolved).resolve()),
            }
        )

    print(json.dumps(results, indent=2))


if __name__ == "__main__":
    main()

