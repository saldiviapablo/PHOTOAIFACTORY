from __future__ import annotations

import gc
import time
from pathlib import Path
from typing import Any

import numpy as np
from PIL import Image

from .analysis_models import (
    ArtifactIdentity,
    ModelExecutionError,
    ModelIntegrityError,
    ModelMissingError,
    ensure_local_image,
    json_safe,
    model_identity,
    sha256_file,
)
from .model_registry import registry

FP8_KERNEL_REVISION = "7cdb05d472d6c954c7d03182ed836ebfd4610df0"
QWEN_MAX_VISUAL_PIXELS = 1280 * 28 * 28


def _first_artifact(root: Path, suffix: str) -> Path:
    if root.is_file() and root.suffix.lower() == suffix:
        return root
    matches = sorted(root.rglob(f"*{suffix}"))
    if not matches:
        raise FileNotFoundError(f"No {suffix} artifact found under {root}")
    return matches[0]


def _move_batch_to_device(batch: Any, device: Any) -> Any:
    if hasattr(batch, "to"):
        return batch.to(device)
    return {
        key: value.to(device) if hasattr(value, "to") else value
        for key, value in batch.items()
    }


def _pin_fp8_kernel_revision() -> None:
    # Reuse the exact mechanism that passed GPU-01. It avoids a network-only
    # semantic-version lookup after the verified kernel revision is cached.
    from transformers.integrations import hub_kernels

    mapping = hub_kernels._HUB_KERNEL_MAPPING["finegrained-fp8"]
    mapping["revision"] = FP8_KERNEL_REVISION
    mapping.pop("version", None)


class LazyAdapter:
    model_id: str
    distribution: str
    artifact_version: str | None = None
    artifact_sha256: str | None = None

    def __init__(self) -> None:
        self._loaded = False
        self._identity: ArtifactIdentity | None = None

    @property
    def identity(self) -> ArtifactIdentity:
        if self._identity is None:
            self._identity = model_identity(
                self.model_id,
                self.distribution,
                self.artifact_version,
                self.artifact_sha256,
            )
        return self._identity

    def load(self) -> None:
        if self._loaded:
            return
        try:
            self._load()
        except FileNotFoundError as exc:
            raise ModelMissingError(self.model_id, str(exc)) from exc
        except ModelMissingError:
            raise
        except ModelIntegrityError:
            raise
        except Exception as exc:
            raise ModelExecutionError(self.model_id, f"load failed: {exc}") from exc
        self._loaded = True
        registry.mark_loaded(self.model_id)

    def release(self) -> None:
        try:
            self._release()
        finally:
            self._loaded = False
            registry.mark_released(self.model_id)
            gc.collect()
            try:
                import torch

                if torch.cuda.is_available():
                    torch.cuda.synchronize()
                    torch.cuda.empty_cache()
            except Exception:
                pass

    def _load(self) -> None:
        raise NotImplementedError

    def _release(self) -> None:
        pass


class RfDetrMediumAdapter(LazyAdapter):
    model_id = "rf-detr-medium"
    distribution = "transformers"

    def __init__(self) -> None:
        super().__init__()
        self.model: Any = None
        self.processor: Any = None

    def _load(self) -> None:
        import torch
        from transformers import AutoImageProcessor, AutoModelForObjectDetection

        root = registry.require(self.model_id)
        self.processor = AutoImageProcessor.from_pretrained(
            str(root), local_files_only=True, trust_remote_code=False
        )
        kwargs: dict[str, Any] = {
            "local_files_only": True,
            "trust_remote_code": False,
        }
        if torch.cuda.is_available():
            kwargs["device_map"] = {"": "cuda:0"}
        self.model = AutoModelForObjectDetection.from_pretrained(str(root), **kwargs)
        self.model.eval()

    def predict(self, image_path: str, threshold: float = 0.35) -> tuple[dict, dict]:
        self.load()
        import torch

        start = time.perf_counter()
        try:
            image = Image.open(ensure_local_image(image_path)).convert("RGB")
            device = next(self.model.parameters()).device
            inputs = _move_batch_to_device(
                self.processor(images=image, return_tensors="pt"), device
            )
            with torch.inference_mode():
                outputs = self.model(**inputs)
            target_sizes = torch.tensor(
                [[image.height, image.width]], device=outputs.logits.device
            )
            processed = self.processor.post_process_object_detection(
                outputs, threshold=float(threshold), target_sizes=target_sizes
            )[0]

            rows: list[dict] = []
            id2label = getattr(self.model.config, "id2label", {}) or {}
            for score, label, box in zip(
                processed["scores"], processed["labels"], processed["boxes"]
            ):
                cid = int(label.item())
                label_name = id2label.get(cid, id2label.get(str(cid)))
                rows.append(
                    {
                        "class_id": cid,
                        "label": str(label_name) if label_name is not None else None,
                        "confidence": float(score.item()),
                        "xyxy": [float(value) for value in box.detach().cpu().tolist()],
                    }
                )
            return {
                "threshold": float(threshold),
                "count": len(rows),
                "person_count": sum(
                    1
                    for row in rows
                    if (row["label"] or "").strip().lower() == "person"
                    or row["class_id"] == 0
                ),
                "detections": rows,
            }, {"inference_ms": (time.perf_counter() - start) * 1000.0}
        except ModelExecutionError:
            raise
        except Exception as exc:
            raise ModelExecutionError(
                self.model_id, f"inference failed: {exc}"
            ) from exc

    def _release(self) -> None:
        self.model = None
        self.processor = None


class MediaPipeFaceAdapter(LazyAdapter):
    model_id = "mediapipe-face-landmarker"
    distribution = "mediapipe"

    def __init__(self) -> None:
        super().__init__()
        self.landmarker: Any = None
        self._mp: Any = None

    def _load(self) -> None:
        import mediapipe as mp
        from mediapipe.tasks import python
        from mediapipe.tasks.python import vision

        task = _first_artifact(registry.require(self.model_id), ".task")
        options = vision.FaceLandmarkerOptions(
            base_options=python.BaseOptions(model_asset_path=str(task)),
            running_mode=vision.RunningMode.IMAGE,
            num_faces=20,
            output_face_blendshapes=True,
        )
        self.landmarker = vision.FaceLandmarker.create_from_options(options)
        self._mp = mp

    def predict(self, image_path: str) -> tuple[dict, dict]:
        self.load()
        start = time.perf_counter()
        try:
            image = self._mp.Image.create_from_file(str(ensure_local_image(image_path)))
            result = self.landmarker.detect(image)
            faces: list[dict] = []
            blendshape_sets = list(result.face_blendshapes or [])
            for index, landmarks in enumerate(result.face_landmarks or []):
                xs = [float(point.x) for point in landmarks]
                ys = [float(point.y) for point in landmarks]
                blendshapes: dict[str, float] = {}
                if index < len(blendshape_sets):
                    for category in blendshape_sets[index]:
                        name = (
                            getattr(category, "category_name", None)
                            or getattr(category, "display_name", None)
                        )
                        if name:
                            blendshapes[str(name)] = float(category.score)
                faces.append(
                    {
                        "landmark_count": len(landmarks),
                        "bbox_normalized": (
                            [min(xs), min(ys), max(xs), max(ys)] if xs and ys else None
                        ),
                        "eye_blink_left": blendshapes.get("eyeBlinkLeft"),
                        "eye_blink_right": blendshapes.get("eyeBlinkRight"),
                        "blendshapes": blendshapes,
                    }
                )
            return {
                "face_count": len(faces),
                "faces": faces,
            }, {"inference_ms": (time.perf_counter() - start) * 1000.0}
        except Exception as exc:
            raise ModelExecutionError(
                self.model_id, f"inference failed: {exc}"
            ) from exc

    def _release(self) -> None:
        if self.landmarker is not None:
            try:
                self.landmarker.close()
            except Exception:
                pass
        self.landmarker = None
        self._mp = None


class MediaPipePoseAdapter(LazyAdapter):
    model_id = "mediapipe-pose-landmarker-full"
    distribution = "mediapipe"

    def __init__(self) -> None:
        super().__init__()
        self.landmarker: Any = None
        self._mp: Any = None

    def _load(self) -> None:
        import mediapipe as mp
        from mediapipe.tasks import python
        from mediapipe.tasks.python import vision

        task = _first_artifact(registry.require(self.model_id), ".task")
        options = vision.PoseLandmarkerOptions(
            base_options=python.BaseOptions(model_asset_path=str(task)),
            running_mode=vision.RunningMode.IMAGE,
            num_poses=20,
        )
        self.landmarker = vision.PoseLandmarker.create_from_options(options)
        self._mp = mp

    def predict(self, image_path: str) -> tuple[dict, dict]:
        self.load()
        start = time.perf_counter()
        try:
            image = self._mp.Image.create_from_file(str(ensure_local_image(image_path)))
            result = self.landmarker.detect(image)
            poses = []
            for landmarks in result.pose_landmarks or []:
                poses.append(
                    [
                        {
                            "x": float(point.x),
                            "y": float(point.y),
                            "z": float(point.z),
                            "visibility": float(getattr(point, "visibility", 0.0)),
                            "presence": float(getattr(point, "presence", 0.0)),
                        }
                        for point in landmarks
                    ]
                )
            return {
                "pose_count": len(poses),
                "poses": poses,
            }, {"inference_ms": (time.perf_counter() - start) * 1000.0}
        except Exception as exc:
            raise ModelExecutionError(
                self.model_id, f"inference failed: {exc}"
            ) from exc

    def _release(self) -> None:
        if self.landmarker is not None:
            try:
                self.landmarker.close()
            except Exception:
                pass
        self.landmarker = None
        self._mp = None


class Florence2Adapter(LazyAdapter):
    model_id = "florence-2-large"
    distribution = "transformers"
    artifact_version = "4271c66b88cdbc05735372ec13b2360108de5317"
    artifact_sha256 = "7715423d6549bf1e71188bdd84f4ac960cc0597886af24a5ef7b66f128660685"

    def __init__(self) -> None:
        super().__init__()
        self.model: Any = None
        self.processor: Any = None

    def _load(self) -> None:
        root = registry.require(self.model_id)
        weight = root / "model.safetensors"
        if not weight.is_file():
            raise FileNotFoundError(f"Required Florence-2 weight is missing: {weight}")
        actual_sha256 = registry.artifact_hash(
            self.model_id, lambda: sha256_file(weight)
        )
        if actual_sha256 != self.artifact_sha256:
            raise ModelIntegrityError(
                self.model_id,
                "model.safetensors SHA-256 mismatch: "
                f"expected {self.artifact_sha256}, got {actual_sha256}",
            )
        import torch
        from transformers import AutoProcessor, Florence2ForConditionalGeneration
        self.processor = AutoProcessor.from_pretrained(
            str(root), local_files_only=True, trust_remote_code=False
        )
        kwargs: dict[str, Any] = {
            "local_files_only": True,
            "trust_remote_code": False,
            "dtype": torch.bfloat16 if torch.cuda.is_available() else torch.float32,
        }
        if torch.cuda.is_available():
            kwargs["device_map"] = {"": "cuda:0"}
        self.model = Florence2ForConditionalGeneration.from_pretrained(str(root), **kwargs)
        self.model.eval()

    def describe(self, image_path: str) -> tuple[dict, dict]:
        self.load()
        import torch

        start = time.perf_counter()
        try:
            image = Image.open(ensure_local_image(image_path)).convert("RGB")
            task = "<MORE_DETAILED_CAPTION>"
            inputs = self.processor(text=task, images=image, return_tensors="pt")
            device = next(self.model.parameters()).device
            inputs = _move_batch_to_device(inputs, device)
            if "pixel_values" in inputs and device.type == "cuda":
                inputs["pixel_values"] = inputs["pixel_values"].to(dtype=torch.bfloat16)
            with torch.inference_mode():
                generated_ids = self.model.generate(
                    **inputs, max_new_tokens=256, num_beams=3, do_sample=False
                )
            text = self.processor.batch_decode(
                generated_ids, skip_special_tokens=False
            )[0]
            parsed = self.processor.post_process_generation(
                text, task=task, image_size=(image.width, image.height)
            )
            return {
                "task": task,
                "result": json_safe(parsed),
            }, {"inference_ms": (time.perf_counter() - start) * 1000.0}
        except Exception as exc:
            raise ModelExecutionError(
                self.model_id, f"inference failed: {exc}"
            ) from exc

    def _release(self) -> None:
        self.model = None
        self.processor = None


class Qwen3VlAdapter(LazyAdapter):
    model_id = "qwen3-vl-2b-instruct-fp8"
    distribution = "transformers"

    def __init__(self) -> None:
        super().__init__()
        self.model: Any = None
        self.processor: Any = None

    def _load(self) -> None:
        import torch
        from transformers import AutoProcessor, Qwen3VLForConditionalGeneration

        if not torch.cuda.is_available():
            raise RuntimeError(
                "The approved Qwen3-VL-2B-Instruct-FP8 Phase 0 artifact requires the validated CUDA path."
            )

        _pin_fp8_kernel_revision()
        root = registry.require(self.model_id)
        self.processor = AutoProcessor.from_pretrained(
            str(root), local_files_only=True, trust_remote_code=False
        )
        # Bound visual tokens for the validated 8 GB reference GPU. The default
        # accepts 16 MP and can require an attention allocation larger than VRAM.
        self.processor.image_processor.size.longest_edge = QWEN_MAX_VISUAL_PIXELS
        self.model = Qwen3VLForConditionalGeneration.from_pretrained(
            str(root),
            dtype="auto",
            device_map={"": "cuda:0"},
            local_files_only=True,
            trust_remote_code=False,
        )
        self.model.eval()
        torch.cuda.synchronize()

    def describe(self, image_path: str) -> tuple[dict, dict]:
        self.load()
        import torch

        start = time.perf_counter()
        try:
            image = Image.open(ensure_local_image(image_path)).convert("RGB")
            messages = [
                {
                    "role": "user",
                    "content": [
                        {"type": "image", "image": image},
                        {
                            "type": "text",
                            "text": (
                                "Describe this event photograph as compact factual JSON-like fields: "
                                "scene, indoor_outdoor, people_count_estimate, main_subject, action, "
                                "lighting, background, dominant_colors, composition, expected_motion, tags. "
                                "Do not decide technical focus, clipping, corruption, or eye state."
                            ),
                        },
                    ],
                }
            ]
            inputs = self.processor.apply_chat_template(
                messages,
                tokenize=True,
                add_generation_prompt=True,
                return_dict=True,
                return_tensors="pt",
            )
            inputs.pop("token_type_ids", None)
            inputs = _move_batch_to_device(inputs, next(self.model.parameters()).device)
            with torch.inference_mode():
                generated_ids = self.model.generate(
                    **inputs, max_new_tokens=256, do_sample=False
                )
            trimmed = [
                output_ids[len(input_ids) :]
                for input_ids, output_ids in zip(inputs["input_ids"], generated_ids)
            ]
            answer = self.processor.batch_decode(
                trimmed,
                skip_special_tokens=True,
                clean_up_tokenization_spaces=False,
            )[0]
            torch.cuda.synchronize()
            return {
                "text": answer,
                "fp8_kernel_revision": FP8_KERNEL_REVISION,
            }, {"inference_ms": (time.perf_counter() - start) * 1000.0}
        except Exception as exc:
            raise ModelExecutionError(
                self.model_id, f"inference failed: {exc}"
            ) from exc

    def _release(self) -> None:
        self.model = None
        self.processor = None


def _convert_dinov2_vits14_state_dict(state_dict: dict[str, Any], config: Any) -> dict[str, Any]:
    """Map the official Meta ViT-S/14 checkpoint to Transformers without network access."""
    converted = dict(state_dict)

    rename_pairs = [
        ("cls_token", "embeddings.cls_token"),
        ("mask_token", "embeddings.mask_token"),
        ("pos_embed", "embeddings.position_embeddings"),
        ("patch_embed.proj.weight", "embeddings.patch_embeddings.projection.weight"),
        ("patch_embed.proj.bias", "embeddings.patch_embeddings.projection.bias"),
        ("norm.weight", "layernorm.weight"),
        ("norm.bias", "layernorm.bias"),
    ]
    for index in range(config.num_hidden_layers):
        rename_pairs.extend(
            [
                (f"blocks.{index}.norm1.weight", f"encoder.layer.{index}.norm1.weight"),
                (f"blocks.{index}.norm1.bias", f"encoder.layer.{index}.norm1.bias"),
                (f"blocks.{index}.norm2.weight", f"encoder.layer.{index}.norm2.weight"),
                (f"blocks.{index}.norm2.bias", f"encoder.layer.{index}.norm2.bias"),
                (f"blocks.{index}.mlp.fc1.weight", f"encoder.layer.{index}.mlp.fc1.weight"),
                (f"blocks.{index}.mlp.fc1.bias", f"encoder.layer.{index}.mlp.fc1.bias"),
                (f"blocks.{index}.mlp.fc2.weight", f"encoder.layer.{index}.mlp.fc2.weight"),
                (f"blocks.{index}.mlp.fc2.bias", f"encoder.layer.{index}.mlp.fc2.bias"),
                (
                    f"blocks.{index}.ls1.gamma",
                    f"encoder.layer.{index}.layer_scale1.lambda1",
                ),
                (
                    f"blocks.{index}.ls2.gamma",
                    f"encoder.layer.{index}.layer_scale2.lambda1",
                ),
                (
                    f"blocks.{index}.attn.proj.weight",
                    f"encoder.layer.{index}.attention.output.dense.weight",
                ),
                (
                    f"blocks.{index}.attn.proj.bias",
                    f"encoder.layer.{index}.attention.output.dense.bias",
                ),
            ]
        )

    for old, new in rename_pairs:
        if old not in converted:
            raise KeyError(f"DINOv2 checkpoint key missing: {old}")
        converted[new] = converted.pop(old)

    for index in range(config.num_hidden_layers):
        qkv_weight = converted.pop(f"blocks.{index}.attn.qkv.weight")
        qkv_bias = converted.pop(f"blocks.{index}.attn.qkv.bias")
        hidden = config.hidden_size
        converted[f"encoder.layer.{index}.attention.attention.query.weight"] = qkv_weight[:hidden, :]
        converted[f"encoder.layer.{index}.attention.attention.query.bias"] = qkv_bias[:hidden]
        converted[f"encoder.layer.{index}.attention.attention.key.weight"] = qkv_weight[hidden : hidden * 2, :]
        converted[f"encoder.layer.{index}.attention.attention.key.bias"] = qkv_bias[hidden : hidden * 2]
        converted[f"encoder.layer.{index}.attention.attention.value.weight"] = qkv_weight[-hidden:, :]
        converted[f"encoder.layer.{index}.attention.attention.value.bias"] = qkv_bias[-hidden:]

    return converted


class DinoV2Adapter(LazyAdapter):
    model_id = "dinov2-vits14-standard"
    distribution = "transformers"

    def __init__(self) -> None:
        super().__init__()
        self.model: Any = None
        self.processor: Any = None

    def _load(self) -> None:
        import torch
        from transformers import BitImageProcessor, Dinov2Config, Dinov2Model
        from transformers.image_utils import (
            IMAGENET_DEFAULT_MEAN,
            IMAGENET_DEFAULT_STD,
            PILImageResampling,
        )

        root = registry.require(self.model_id)
        checkpoint = _first_artifact(root, ".pth")
        state = torch.load(checkpoint, map_location="cpu", weights_only=True)
        if isinstance(state, dict) and "model" in state and isinstance(state["model"], dict):
            state = state["model"]
        if not isinstance(state, dict):
            raise ValueError("Unexpected DINOv2 checkpoint structure.")

        config = Dinov2Config(
            image_size=518,
            patch_size=14,
            hidden_size=384,
            num_attention_heads=6,
            num_hidden_layers=12,
        )
        converted = _convert_dinov2_vits14_state_dict(state, config)
        self.model = Dinov2Model(config).eval()
        self.model.load_state_dict(converted, strict=True)
        self.processor = BitImageProcessor(
            size={"shortest_edge": 256},
            resample=PILImageResampling.BICUBIC,
            image_mean=IMAGENET_DEFAULT_MEAN,
            image_std=IMAGENET_DEFAULT_STD,
        )
        if torch.cuda.is_available():
            self.model = self.model.to(device="cuda:0", dtype=torch.float16)

    def embed(self, image_path: str) -> tuple[dict, dict]:
        self.load()
        import torch

        start = time.perf_counter()
        try:
            image = Image.open(ensure_local_image(image_path)).convert("RGB")
            inputs = self.processor(images=image, return_tensors="pt")
            device = next(self.model.parameters()).device
            dtype = next(self.model.parameters()).dtype
            pixel_values = inputs["pixel_values"].to(device=device, dtype=dtype)
            with torch.inference_mode():
                outputs = self.model(pixel_values=pixel_values)
            vector = outputs.last_hidden_state[:, 0, :].float()
            vector = torch.nn.functional.normalize(vector, p=2, dim=1)[0].cpu().tolist()
            return {
                "dimension": len(vector),
                "normalization": "L2",
                "values": [float(value) for value in vector],
            }, {"inference_ms": (time.perf_counter() - start) * 1000.0}
        except Exception as exc:
            raise ModelExecutionError(
                self.model_id, f"inference failed: {exc}"
            ) from exc

    def _release(self) -> None:
        self.model = None
        self.processor = None
