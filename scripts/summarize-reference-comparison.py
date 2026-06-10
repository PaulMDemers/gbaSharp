#!/usr/bin/env python3
from __future__ import annotations

import argparse
import csv
from pathlib import Path

from PIL import Image

from image_match_metrics import classify_match, image_metrics


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Summarize external reference comparisons with visual metrics and coarse classifications."
    )
    parser.add_argument("--manifest", default="docs/gba-longplay-reference-frames.csv")
    parser.add_argument("--root", default=".")
    parser.add_argument("--output", required=True)
    return parser.parse_args()


def resolve_path(root: Path, value: str) -> Path:
    path = Path(value)
    return path if path.is_absolute() else root / path


def int_or_default(value: str, fallback: int) -> int:
    value = (value or "").strip()
    return int(value) if value else fallback


def read_rows(path: Path) -> list[dict[str, str]]:
    with path.open(newline="", encoding="utf-8-sig") as handle:
        return list(csv.DictReader(handle))


def size_bucket(different_percent: float) -> str:
    if different_percent == 0:
        return "exact"
    if different_percent < 1:
        return "tiny"
    if different_percent < 5:
        return "small"
    if different_percent < 15:
        return "medium"
    return "large"


def count_pixels_over_threshold(actual_path: Path, reference: Image.Image, threshold: int) -> int:
    actual = Image.open(actual_path).convert("RGB")
    if actual.size != reference.size:
        return actual.width * actual.height

    actual_pixels = actual.load()
    reference_pixels = reference.load()
    different = 0
    for y in range(actual.height):
        for x in range(actual.width):
            ar, ag, ab = actual_pixels[x, y]
            rr, rg, rb = reference_pixels[x, y]
            if max(abs(ar - rr), abs(ag - rg), abs(ab - rb)) > threshold:
                different += 1

    return different


def main() -> int:
    args = parse_args()
    root = Path(args.root).resolve()
    manifest = resolve_path(root, args.manifest)
    output = resolve_path(root, args.output)
    output.parent.mkdir(parents=True, exist_ok=True)

    fieldnames = [
        "label",
        "status",
        "differentPixels",
        "thresholdedDifferentPixels",
        "differentPercent",
        "maxChannelDelta",
        "structuralSimilarity",
        "coarseMeanDelta",
        "allowedDifferentPixels",
        "allowedChannelDelta",
        "sizeBucket",
        "classification",
        "actualImage",
        "referenceImage",
        "notes",
    ]

    rows = []
    for row in read_rows(manifest):
        label = row["label"]
        actual_path = resolve_path(root, row["actualImage"])
        reference_path = resolve_path(root, row["referenceImage"])
        allowed_pixels = int_or_default(row.get("maxDifferentPixels", ""), 0)
        allowed_channel = int_or_default(row.get("maxChannelDelta", ""), 0)

        if not actual_path.exists():
            rows.append({
                "label": label,
                "status": "missing-actual",
                "differentPixels": "",
                "thresholdedDifferentPixels": "",
                "differentPercent": "",
                "maxChannelDelta": "",
                "structuralSimilarity": "",
                "coarseMeanDelta": "",
                "allowedDifferentPixels": str(allowed_pixels),
                "allowedChannelDelta": str(allowed_channel),
                "sizeBucket": "",
                "classification": "missing",
                "actualImage": str(actual_path),
                "referenceImage": str(reference_path),
                "notes": row.get("notes", ""),
            })
            continue

        if not reference_path.exists():
            rows.append({
                "label": label,
                "status": "missing-reference",
                "differentPixels": "",
                "thresholdedDifferentPixels": "",
                "differentPercent": "",
                "maxChannelDelta": "",
                "structuralSimilarity": "",
                "coarseMeanDelta": "",
                "allowedDifferentPixels": str(allowed_pixels),
                "allowedChannelDelta": str(allowed_channel),
                "sizeBucket": "",
                "classification": "missing",
                "actualImage": str(actual_path),
                "referenceImage": str(reference_path),
                "notes": row.get("notes", ""),
            })
            continue

        reference = Image.open(reference_path).convert("RGB")
        metrics = image_metrics(actual_path, reference)
        thresholded_pixels = count_pixels_over_threshold(actual_path, reference, allowed_channel)
        status = "pass" if (
            metrics.status == "ok"
            and thresholded_pixels <= allowed_pixels
        ) else metrics.status if metrics.status != "ok" else "diff"

        rows.append({
            "label": label,
            "status": status,
            "differentPixels": str(metrics.different_pixels),
            "thresholdedDifferentPixels": str(thresholded_pixels),
            "differentPercent": f"{metrics.different_percent:.4f}",
            "maxChannelDelta": str(metrics.max_channel_delta),
            "structuralSimilarity": f"{metrics.structural_similarity:.6f}",
            "coarseMeanDelta": f"{metrics.coarse_mean_delta:.4f}",
            "allowedDifferentPixels": str(allowed_pixels),
            "allowedChannelDelta": str(allowed_channel),
            "sizeBucket": size_bucket(metrics.different_percent),
            "classification": classify_match(metrics, metrics, 0),
            "actualImage": str(actual_path),
            "referenceImage": str(reference_path),
            "notes": row.get("notes", ""),
        })

    with output.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(rows)

    print(output)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
