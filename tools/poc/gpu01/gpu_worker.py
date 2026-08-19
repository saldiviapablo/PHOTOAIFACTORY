"""GPU-01 JSON-line worker, executed only by the isolated AI Python runtime."""

from __future__ import annotations

import argparse
import gc
import json
import os
import sys
import time
import traceback

os.environ.setdefault("HF_HUB_OFFLINE", "1")
os.environ.setdefault("TRANSFORMERS_OFFLINE", "1")
os.environ.setdefault("HF_HUB_DISABLE_PROGRESS_BARS", "1")
os.environ.setdefault("TRANSFORMERS_VERBOSITY", "error")

import torch


MODEL = None
TOKENIZER = None
FP8_KERNEL_REVISION = "7cdb05d472d6c954c7d03182ed836ebfd4610df0"


def pin_fp8_kernel_revision() -> None:
    """Avoid a network-only v4 -> commit lookup after the verified kernel is cached."""
    from transformers.integrations import hub_kernels

    mapping = hub_kernels._HUB_KERNEL_MAPPING["finegrained-fp8"]
    mapping["revision"] = FP8_KERNEL_REVISION
    mapping.pop("version", None)


def cuda_stats() -> dict:
    if not torch.cuda.is_available():
        return {
            "cuda_available": False,
            "allocated_mb": 0.0,
            "reserved_mb": 0.0,
            "max_allocated_mb": 0.0,
        }
    return {
        "cuda_available": True,
        "device": torch.cuda.get_device_name(0),
        "capability": list(torch.cuda.get_device_capability(0)),
        "allocated_mb": round(torch.cuda.memory_allocated(0) / 1048576, 3),
        "reserved_mb": round(torch.cuda.memory_reserved(0) / 1048576, 3),
        "max_allocated_mb": round(torch.cuda.max_memory_allocated(0) / 1048576, 3),
    }


def respond(request_id: str, success: bool, result=None, error=None, started=None) -> None:
    duration_ms = (time.perf_counter() - started) * 1000 if started is not None else 0.0
    message = {
        "request_id": request_id,
        "success": success,
        "result": result,
        "error": error,
        "duration_ms": round(duration_ms, 3),
        "process_id": os.getpid(),
    }
    print(json.dumps(message, separators=(",", ":")), flush=True)


def load_qwen(model_path: str) -> dict:
    global MODEL, TOKENIZER
    if MODEL is not None:
        return {"already_loaded": True, "memory": cuda_stats()}

    from transformers import AutoTokenizer, Qwen3VLForConditionalGeneration

    pin_fp8_kernel_revision()
    torch.cuda.reset_peak_memory_stats(0)
    before = cuda_stats()
    TOKENIZER = AutoTokenizer.from_pretrained(model_path, local_files_only=True)
    MODEL = Qwen3VLForConditionalGeneration.from_pretrained(
        model_path,
        dtype="auto",
        device_map={"": "cuda:0"},
        local_files_only=True,
    )
    MODEL.eval()
    torch.cuda.synchronize()
    loaded = cuda_stats()

    inputs = TOKENIZER("GPU lease validation", return_tensors="pt")
    inputs = {name: tensor.to("cuda:0") for name, tensor in inputs.items()}
    with torch.inference_mode():
        output = MODEL.generate(**inputs, max_new_tokens=1, do_sample=False)
    torch.cuda.synchronize()
    inferred = cuda_stats()
    generated_token_id = int(output[0, -1].item())
    del output, inputs
    return {
        "model": "Qwen3-VL-2B-Instruct-FP8",
        "model_class": MODEL.__class__.__name__,
        "fp8_kernel_revision": FP8_KERNEL_REVISION,
        "parameter_count": int(sum(parameter.numel() for parameter in MODEL.parameters())),
        "before": before,
        "loaded": loaded,
        "after_inference": inferred,
        "inference": {"success": True, "generated_tokens": 1, "last_token_id": generated_token_id},
    }


def release_qwen() -> dict:
    global MODEL, TOKENIZER
    before = cuda_stats()
    MODEL = None
    TOKENIZER = None
    gc.collect()
    torch.cuda.synchronize()
    torch.cuda.empty_cache()
    gc.collect()
    torch.cuda.synchronize()
    return {"before": before, "after": cuda_stats(), "released": True}


def cuda_operation(size_mb: int) -> dict:
    if size_mb < 1 or size_mb > 512:
        raise ValueError("size_mb must be between 1 and 512")
    torch.cuda.reset_peak_memory_stats(0)
    before = cuda_stats()
    elements = (size_mb * 1024 * 1024) // 4
    tensor = torch.empty(elements, dtype=torch.float32, device="cuda:0")
    tensor.fill_(1.25)
    tensor.mul_(2.0)
    checksum = float(tensor[0].item())
    torch.cuda.synchronize()
    during = cuda_stats()
    del tensor
    torch.cuda.empty_cache()
    torch.cuda.synchronize()
    return {"requested_mb": size_mb, "checksum": checksum, "before": before, "during": during, "after": cuda_stats()}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--model-path", required=True)
    arguments = parser.parse_args()
    sys.stdout.reconfigure(line_buffering=True)

    for line in sys.stdin:
        if not line.strip():
            continue
        started = time.perf_counter()
        request = json.loads(line)
        request_id = str(request.get("request_id", "missing"))
        command = request.get("command")
        parameters = request.get("parameters") or {}
        try:
            if command == "health":
                respond(
                    request_id,
                    True,
                    {
                        "status": "READY",
                        "python": sys.version.split()[0],
                        "torch": torch.__version__,
                        "cuda_runtime": torch.version.cuda,
                        "memory": cuda_stats(),
                    },
                    started=started,
                )
            elif command == "load_qwen":
                respond(request_id, True, load_qwen(arguments.model_path), started=started)
            elif command == "release_qwen":
                respond(request_id, True, release_qwen(), started=started)
            elif command == "cuda_op":
                size_mb = parameters.get("megabytes", parameters.get("size_mb", request.get("size_mb", 16)))
                respond(request_id, True, cuda_operation(int(size_mb)), started=started)
            elif command == "exit":
                release_qwen()
                respond(request_id, True, {"status": "STOPPED"}, started=started)
                return 0
            else:
                respond(
                    request_id,
                    False,
                    error={"code": "UNKNOWN_COMMAND", "message": str(command)},
                    started=started,
                )
        except torch.cuda.OutOfMemoryError as exc:
            torch.cuda.empty_cache()
            respond(
                request_id,
                False,
                error={"code": "CUDA_OOM", "message": str(exc), "memory": cuda_stats()},
                started=started,
            )
        except Exception as exc:
            traceback.print_exc(file=sys.stderr)
            respond(
                request_id,
                False,
                error={"code": "GPU_WORKER_ERROR", "message": str(exc), "type": type(exc).__name__},
                started=started,
            )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
