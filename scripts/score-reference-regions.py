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


FRAME_RE = re.compile(r"-f-(\d+)-")


@dataclass(frozen=True)
class Region:
    name: str
    x: int
    y: int
    width: int
    height: int


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Score full-frame and named-region differences between candidate frames and a reference image."
    )
    parser.add_argument("--reference", required=True, help="Reference PNG/PPM.")
    parser.add_argument("--actual", action="append", default=[], help="Actual frame path. May be passed more than once.")
    parser.add_argument("--actual-glob", default="", help="Glob for candidate actual frames.")
    parser.add_argument("--region", action="append", default=[], help="Named region as name:x:y:w:h.")
    parser.add_argument("--output", required=True, help="CSV output path.")
    return parser.parse_args()


def parse_region(value: str) -> Region:
    parts = value.split(":")
    if len(parts) != 5:
        raise SystemExit(f"Invalid --region {value!r}; expected name:x:y:w:h")

    name = parts[0].strip()
    if not name:
        raise SystemExit(f"Invalid --region {value!r}; name cannot be blank")

    try:
        x, y, width, height = (int(item) for item in parts[1:])
    except ValueError as exc:
        raise SystemExit(f"Invalid --region {value!r}; coordinates must be integers") from exc

    if x < 0 or y < 0 or width <= 0 or height <= 0:
        raise SystemExit(f"Invalid --region {value!r}; coordinates must define a positive rectangle")

    return Region(name, x, y, width, height)


def resolve_actuals(args: argparse.Namespace) -> list[Path]:
    paths = [Path(item) for item in args.actual]
    if args.actual_glob:
        paths.extend(Path(item) for item in glob.glob(args.actual_glob))

    unique = sorted({path for path in paths})
    if not unique:
        raise SystemExit("Pass at least one --actual or --actual-glob.")

    return unique


def crop_region(image: Image.Image, region: Region) -> Image.Image:
    right = min(image.width, region.x + region.width)
    bottom = min(image.height, region.y + region.height)
    if region.x >= image.width or region.y >= image.height or right <= region.x or bottom <= region.y:
        raise SystemExit(f"Region {region.name!r} is outside image bounds {image.width}x{image.height}.")

    return image.crop((region.x, region.y, right, bottom))


def score_images(actual: Image.Image, reference: Image.Image) -> dict[str, str]:
    if actual.size != reference.size:
        return {
            "status": "size-mismatch",
            "differentPixels": "",
            "differentPercent": "100.0000",
            "maxChannelDelta": "255",
            "meanAbsChannelDelta": "255.0000",
            "structuralSimilarity": "0.000000",
            "coarseMeanDelta": "255.0000",
        }

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
        "status": "ok",
        "differentPixels": str(different),
        "differentPercent": f"{(different / pixels) * 100.0:.4f}",
        "maxChannelDelta": str(max_delta),
        "meanAbsChannelDelta": f"{channel_total / (pixels * 3):.4f}",
        "structuralSimilarity": f"{structural_similarity(actual, reference):.6f}",
        "coarseMeanDelta": f"{coarse_mean_delta(actual, reference):.4f}",
    }


def frame_number(path: Path) -> str:
    match = FRAME_RE.search(path.name)
    return match.group(1) if match else ""


def main() -> int:
    args = parse_args()
    reference_path = Path(args.reference)
    reference = Image.open(reference_path).convert("RGB")
    regions = [Region("full", 0, 0, reference.width, reference.height)]
    regions.extend(parse_region(value) for value in args.region)
    actual_paths = resolve_actuals(args)

    output_path = Path(args.output)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    fieldnames = [
        "actualImage",
        "referenceImage",
        "frame",
        "region",
        "x",
        "y",
        "width",
        "height",
        "status",
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
        for actual_path in actual_paths:
            actual = Image.open(actual_path).convert("RGB")
            for region in regions:
                actual_region = crop_region(actual, region)
                reference_region = crop_region(reference, region)
                row = {
                    "actualImage": str(actual_path),
                    "referenceImage": str(reference_path),
                    "frame": frame_number(actual_path),
                    "region": region.name,
                    "x": str(region.x),
                    "y": str(region.y),
                    "width": str(actual_region.width),
                    "height": str(actual_region.height),
                }
                row.update(score_images(actual_region, reference_region))
                writer.writerow(row)

    print(output_path)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
