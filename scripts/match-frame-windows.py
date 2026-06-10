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


FRAME_RE = re.compile(r"-f-(\d+)")


@dataclass(frozen=True)
class Region:
    name: str
    x: int
    y: int
    width: int
    height: int


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


def score(actual: Image.Image, reference: Image.Image) -> dict[str, str | int | float]:
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
        scored: list[tuple[float, dict[str, str]]] = []
        for actual_path, actual_image in actual_images:
            actual_crop = crop(actual_image, region)
            for reference_path, reference_image in reference_images:
                reference_crop = crop(reference_image, region)
                metrics = score(actual_crop, reference_crop)
                row = {
                    "region": region.name,
                    "actualFrame": str(frame_number(actual_path)),
                    "referenceFrame": str(frame_number(reference_path)),
                    "frameOffset": str(frame_number(actual_path) - frame_number(reference_path)),
                    "actualImage": str(actual_path),
                    "referenceImage": str(reference_path),
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
