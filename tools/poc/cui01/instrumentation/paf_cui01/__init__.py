"""Deterministic interrupt/queue instrumentation used only by Gate CUI-01."""

import time

import torch

import comfy.model_management


class PafCui01Delay:
    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "delay_ms": (
                    "INT",
                    {"default": 1000, "min": 0, "max": 10_000, "step": 25},
                ),
                "width": ("INT", {"default": 64, "min": 1, "max": 512}),
                "height": ("INT", {"default": 48, "min": 1, "max": 512}),
                "color": (
                    "INT",
                    {"default": 0, "min": 0, "max": 0xFFFFFF, "step": 1},
                ),
                "nonce": ("STRING", {"default": "cui01"}),
            }
        }

    RETURN_TYPES = ("IMAGE",)
    FUNCTION = "run"
    CATEGORY = "_photo_ai_factory/cui01"

    @classmethod
    def IS_CHANGED(cls, **_kwargs):
        return float("nan")

    def run(self, delay_ms, width, height, color, nonce):
        del nonce
        deadline = time.perf_counter() + (delay_ms / 1000.0)
        while time.perf_counter() < deadline:
            comfy.model_management.throw_exception_if_processing_interrupted()
            time.sleep(min(0.025, max(0.0, deadline - time.perf_counter())))

        red = ((color >> 16) & 0xFF) / 255.0
        green = ((color >> 8) & 0xFF) / 255.0
        blue = (color & 0xFF) / 255.0
        image = torch.empty((1, height, width, 3), dtype=torch.float32)
        image[:, :, :, 0] = red
        image[:, :, :, 1] = green
        image[:, :, :, 2] = blue
        return (image,)


NODE_CLASS_MAPPINGS = {"PafCui01Delay": PafCui01Delay}
NODE_DISPLAY_NAME_MAPPINGS = {"PafCui01Delay": "PAF CUI-01 Delay"}

