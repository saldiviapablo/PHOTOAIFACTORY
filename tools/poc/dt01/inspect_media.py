"""Small DT-01 media inspector using the project's isolated Python runtime."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

import exifread
import numpy as np
from PIL import Image
import tifffile


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def exif_value(tags: dict[str, object], *names: str) -> str | None:
    for name in names:
        value = tags.get(name)
        if value is not None:
            return str(value)
    return None


def inspect_raw(path: Path) -> dict[str, object]:
    with path.open("rb") as stream:
        tags = exifread.process_file(stream, details=False)

    width = exif_value(tags, "EXIF ExifImageWidth", "Image ImageWidth")
    height = exif_value(tags, "EXIF ExifImageLength", "Image ImageLength")
    return {
        "name": path.name,
        "path": str(path),
        "size_bytes": path.stat().st_size,
        "sha256": sha256(path),
        "dimensions": f"{width}x{height}" if width and height else None,
        "make": exif_value(tags, "Image Make"),
        "model": exif_value(tags, "Image Model"),
        "date_time_original": exif_value(tags, "EXIF DateTimeOriginal"),
        "exposure_time": exif_value(tags, "EXIF ExposureTime"),
        "f_number": exif_value(tags, "EXIF FNumber"),
        "iso": exif_value(tags, "EXIF ISOSpeedRatings", "EXIF PhotographicSensitivity"),
        "focal_length_mm": exif_value(tags, "EXIF FocalLength"),
        "compression": exif_value(tags, "Image Compression"),
        "subfile_type": exif_value(tags, "Image SubfileType"),
    }


def validate_image(path: Path) -> dict[str, object]:
    result: dict[str, object] = {
        "path": str(path),
        "exists": path.is_file(),
        "size_bytes": path.stat().st_size if path.is_file() else 0,
    }
    if not path.is_file() or path.stat().st_size == 0:
        result["readable"] = False
        return result

    try:
        if path.suffix.lower() in {".tif", ".tiff"}:
            with tifffile.TiffFile(path) as tiff:
                page = tiff.pages[0]
                pixels = page.asarray()
                result.update(
                    {
                        "readable": True,
                        "format": "TIFF",
                        "dimensions": f"{page.imagewidth}x{page.imagelength}",
                        "dtype": str(pixels.dtype),
                        "shape": list(pixels.shape),
                        "bits_per_sample": page.bitspersample,
                        "samples_per_pixel": page.samplesperpixel,
                        "photometric": str(page.photometric.name),
                        "min": int(pixels.min()),
                        "max": int(pixels.max()),
                        "sha256": sha256(path),
                    }
                )
            return result

        with Image.open(path) as image:
            image.load()
            result.update(
                {
                    "readable": True,
                    "format": image.format,
                    "mode": image.mode,
                    "dimensions": f"{image.width}x{image.height}",
                    "bits_per_sample": image.tag_v2.get(258) if image.format == "TIFF" else 8,
                    "sha256": sha256(path),
                }
            )
    except Exception as exc:  # pragma: no cover - diagnostic boundary
        result.update({"readable": False, "error": str(exc)})
    return result


def compare_images(first: Path, second: Path) -> dict[str, object]:
    with Image.open(first) as first_image, Image.open(second) as second_image:
        first_rgb = np.asarray(first_image.convert("RGB"), dtype=np.int16)
        second_rgb = np.asarray(second_image.convert("RGB"), dtype=np.int16)

    result: dict[str, object] = {
        "first": str(first),
        "second": str(second),
        "first_sha256": sha256(first),
        "second_sha256": sha256(second),
        "first_shape": list(first_rgb.shape),
        "second_shape": list(second_rgb.shape),
    }
    if first_rgb.shape != second_rgb.shape:
        result.update({"pixel_equal": False, "error": "dimension mismatch"})
        return result

    difference = first_rgb.astype(np.float64) - second_rgb.astype(np.float64)
    absolute = np.abs(difference)
    first_hist = np.histogram(first_rgb, bins=256, range=(0, 256))[0]
    second_hist = np.histogram(second_rgb, bins=256, range=(0, 256))[0]
    result.update(
        {
            "pixel_equal": bool(np.array_equal(first_rgb, second_rgb)),
            "changed_channel_values": int(np.count_nonzero(difference)),
            "changed_pixels": int(np.count_nonzero(np.any(difference != 0, axis=2))),
            "mae": float(absolute.mean()),
            "rmse": float(np.sqrt(np.mean(np.square(difference)))),
            "max_abs_difference": int(absolute.max()),
            "histogram_l1": int(np.abs(first_hist - second_hist).sum()),
        }
    )
    return result


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=("inventory", "validate", "compare"))
    parser.add_argument("paths", nargs="+", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()

    if args.mode == "inventory":
        value: object = [inspect_raw(path) for path in args.paths]
    elif args.mode == "validate":
        value = [validate_image(path) for path in args.paths]
    else:
        if len(args.paths) != 2:
            parser.error("compare requires exactly two paths")
        value = compare_images(args.paths[0], args.paths[1])
    payload = json.dumps(value, indent=2, ensure_ascii=False)
    if args.output:
        args.output.write_text(payload + "\n", encoding="utf-8")
    print(payload)


if __name__ == "__main__":
    main()
