#!/usr/bin/env python3
from __future__ import annotations

import argparse
import csv
import glob
import re
from dataclasses import dataclass
from pathlib import Path

from PIL import Image

from image_match_metrics import coarse_mean_delta, structural_similarity

try:
    import numpy as np
except ImportError:  # pragma: no cover - optional speed path
    np = None


FRAME_RE = re.compile(r"-f-(\d+)")


@dataclass(frozen=True)
class Region:
    name: str
    x: int
    y: int
    width: int
    height: int


@dataclass(frozen=True)
class PreparedImage:
    path: Path
    frame: int
    image: Image.Image
    rgb: object | None
    luma: object | None
    luma_mean: float
    luma_variance: float
    coarse_rgb: object | None


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Pairwise-match two frame windows by full-frame and named regions.")
    parser.add_argument("--actual-glob", required=True)
    parser.add_argument("--reference-glob", required=True)
    parser.add_argument("--region", action="append", default=[], help="Named region as name:x:y:w:h.")
    parser.add_argument("--output", required=True)
    parser.add_argument("--top", type=int, default=10)
    return parser.parse_args()


def parse_region(value: str) -> Region:
    parts = value.split(":")
    if len(parts) != 5:
        raise SystemExit(f"Invalid --region {value!r}; expected name:x:y:w:h")
    return Region(parts[0], int(parts[1]), int(parts[2]), int(parts[3]), int(parts[4]))


def frame_number(path: Path) -> int:
    match = FRAME_RE.search(path.name)
    return int(match.group(1)) if match else -1


def crop(image: Image.Image, region: Region) -> Image.Image:
    return image.crop((region.x, region.y, region.x + region.width, region.y + region.height))


def prepare_image(path: Path, image: Image.Image, region: Region) -> PreparedImage:
    cropped = crop(image, region)
    if np is None:
        return PreparedImage(path, frame_number(path), cropped, None, None, 0.0, 0.0, None)

    rgb = np.asarray(cropped, dtype=np.int16)
    luma = np.asarray(cropped.convert("L"), dtype=np.float64)
    coarse = np.asarray(cropped.resize((32, 32), Image.Resampling.BILINEAR), dtype=np.int16)
    return PreparedImage(
        path=path,
        frame=frame_number(path),
        image=cropped,
        rgb=rgb,
        luma=luma,
        luma_mean=float(luma.mean()),
        luma_variance=float(((luma - luma.mean()) ** 2).mean()),
        coarse_rgb=coarse,
    )


def score_slow(actual: Image.Image, reference: Image.Image) -> dict[str, str | int | float]:
    actual_pixels = actual.load()
    reference_pixels = reference.load()
    different = 0
    max_delta = 0
    channel_total = 0
    pixels = actual.width * actual.height
    for y in range(actual.height):
        for x in range(actual.width):
            ar, ag, ab = actual_pixels[x, y]
            rr, rg, rb = reference_pixels[x, y]
            red = abs(ar - rr)
            green = abs(ag - rg)
            blue = abs(ab - rb)
            delta = max(red, green, blue)
            max_delta = max(max_delta, delta)
            channel_total += red + green + blue
            if delta:
                different += 1
    return {
        "differentPixels": different,
        "differentPercent": (different / pixels) * 100.0,
        "maxChannelDelta": max_delta,
        "meanAbsChannelDelta": channel_total / (pixels * 3),
        "structuralSimilarity": structural_similarity(actual, reference),
        "coarseMeanDelta": coarse_mean_delta(actual, reference),
    }


def structural_similarity_fast(actual: PreparedImage, reference: PreparedImage) -> float:
    actual_luma = actual.luma
    reference_luma = reference.luma
    if np is None or actual_luma is None or reference_luma is None:
        return structural_similarity(actual.image, reference.image)

    count = actual_luma.size
    if count == 0:
        return 0.0

    covariance = float(((actual_luma - actual.luma_mean) * (reference_luma - reference.luma_mean)).mean())
    c1 = (0.01 * 255) ** 2
    c2 = (0.03 * 255) ** 2
    denominator = (actual.luma_mean**2 + reference.luma_mean**2 + c1) * (
        actual.luma_variance + reference.luma_variance + c2
    )
    if denominator == 0:
        return 1.0 if actual.luma_mean == reference.luma_mean else 0.0

    score = ((2 * actual.luma_mean * reference.luma_mean + c1) * (2 * covariance + c2)) / denominator
    return max(-1.0, min(1.0, score))


def score_prepared(actual: PreparedImage, reference: PreparedImage) -> dict[str, str | int | float]:
    if np is None or actual.rgb is None or reference.rgb is None:
        return score_slow(actual.image, reference.image)

    delta = np.abs(actual.rgb - reference.rgb)
    pixel_delta = delta.max(axis=2)
    different = int(np.count_nonzero(pixel_delta))
    max_delta = int(pixel_delta.max(initial=0))
    channel_total = int(delta.sum())
    pixels = actual.image.width * actual.image.height
    coarse_delta = np.abs(actual.coarse_rgb - reference.coarse_rgb)
    return {
        "differentPixels": different,
        "differentPercent": (different / pixels) * 100.0,
        "maxChannelDelta": max_delta,
        "meanAbsChannelDelta": channel_total / (pixels * 3),
        "structuralSimilarity": structural_similarity_fast(actual, reference),
        "coarseMeanDelta": float(coarse_delta.mean()) if coarse_delta.size else 255.0,
    }


def main() -> int:
    args = parse_args()
    actual_paths = sorted(Path(item) for item in glob.glob(args.actual_glob))
    reference_paths = sorted(Path(item) for item in glob.glob(args.reference_glob))
    if not actual_paths:
        raise SystemExit("No actual frames matched.")
    if not reference_paths:
        raise SystemExit("No reference frames matched.")

    regions = [Region("full", 0, 0, 240, 160)]
    regions.extend(parse_region(item) for item in args.region)
    actual_images = [(path, Image.open(path).convert("RGB")) for path in actual_paths]
    reference_images = [(path, Image.open(path).convert("RGB")) for path in reference_paths]

    rows: list[dict[str, str]] = []
    for region in regions:
        actual_regions = [prepare_image(path, image, region) for path, image in actual_images]
        reference_regions = [prepare_image(path, image, region) for path, image in reference_images]
        scored: list[tuple[float, dict[str, str]]] = []
        for actual in actual_regions:
            for reference in reference_regions:
                metrics = score_prepared(actual, reference)
                row = {
                    "region": region.name,
                    "actualFrame": str(actual.frame),
                    "referenceFrame": str(reference.frame),
                    "frameOffset": str(actual.frame - reference.frame),
                    "actualImage": str(actual.path),
                    "referenceImage": str(reference.path),
                    "differentPixels": str(metrics["differentPixels"]),
                    "differentPercent": f"{metrics['differentPercent']:.4f}",
                    "maxChannelDelta": str(metrics["maxChannelDelta"]),
                    "meanAbsChannelDelta": f"{metrics['meanAbsChannelDelta']:.4f}",
                    "structuralSimilarity": f"{metrics['structuralSimilarity']:.6f}",
                    "coarseMeanDelta": f"{metrics['coarseMeanDelta']:.4f}",
                }
                sort_key = metrics["meanAbsChannelDelta"] * 100000 + metrics["differentPixels"]
                scored.append((sort_key, row))

        rows.extend(row for _, row in sorted(scored, key=lambda item: item[0])[: args.top])

    output_path = Path(args.output)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    fieldnames = [
        "region",
        "actualFrame",
        "referenceFrame",
        "frameOffset",
        "actualImage",
        "referenceImage",
        "differentPixels",
        "differentPercent",
        "maxChannelDelta",
        "meanAbsChannelDelta",
        "structuralSimilarity",
        "coarseMeanDelta",
    ]
    with output_path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(rows)

    print(output_path)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
