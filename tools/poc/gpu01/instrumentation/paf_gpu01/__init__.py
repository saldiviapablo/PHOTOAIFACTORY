"""Temporary CUDA instrumentation used only by Gate GPU-01."""

import time
import torch


class PafGpu01CudaProbe:
    @classmethod
    def INPUT_TYPES(cls):
        return {"required": {
            "megabytes": ("INT", {"default": 8, "min": 1, "max": 1024, "step": 1}),
            "hold_ms": ("INT", {"default": 10, "min": 0, "max": 5000, "step": 5}),
            "nonce": ("STRING", {"default": "gpu01"}),
        }}

    RETURN_TYPES = ("STRING",)
    RETURN_NAMES = ("measurement",)
    FUNCTION = "run"
    CATEGORY = "_photo_ai_factory/gpu01"
    OUTPUT_NODE = True

    @classmethod
    def IS_CHANGED(cls, **_kwargs):
        return float("nan")

    def run(self, megabytes, hold_ms, nonce):
        device = torch.device("cuda:0")
        elements = max(1, (int(megabytes) * 1024 * 1024) // 4)
        tensor = torch.empty(elements, dtype=torch.float32, device=device)
        tensor.fill_(1.25)
        tensor.mul_(2.0)
        torch.cuda.synchronize(device)
        if hold_ms:
            time.sleep(hold_ms / 1000.0)
        checksum = float(tensor[: min(1024, elements)].sum().item())
        allocated = torch.cuda.memory_allocated(device) / 1048576.0
        del tensor
        torch.cuda.synchronize(device)
        torch.cuda.empty_cache()
        return (f"{nonce}|checksum={checksum:.3f}|allocated_mb={allocated:.3f}",)


NODE_CLASS_MAPPINGS = {"PafGpu01CudaProbe": PafGpu01CudaProbe}
NODE_DISPLAY_NAME_MAPPINGS = {"PafGpu01CudaProbe": "PAF GPU-01 CUDA Probe"}
