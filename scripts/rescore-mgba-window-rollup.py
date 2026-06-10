#!/usr/bin/env python3
import argparse
import csv
import re
from pathlib import Path

from PIL import Image

from image_match_metrics import classify_match, image_metrics


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Add structural image scores and refined classifications to mGBA window-match CSV rows."
    )
    parser.add_argument("inputs", nargs="+", help="One or more window-match CSV files.")
    parser.add_argument("--output", default="artifacts/mgba-window-match-rescored.csv")
    parser.add_argument("--root", default=".")
    return parser.parse_args()


def resolve_path(root: Path, value: str) -> Path:
    path = Path(value)
    return path if path.is_absolute() else root / path


def read_rows(paths: list[Path]) -> list[dict[str, str]]:
    rows: list[dict[str, str]] = []
    for path in paths:
        with path.open(newline="", encoding="utf-8-sig") as handle:
            rows.extend(csv.DictReader(handle))
    return rows


def find_original_image(best_path: Path, stop_frame: int) -> Path | None:
    pattern = re.compile(rf"-f-{stop_frame:05d}-")
    for candidate in best_path.parent.glob("*.ppm"):
        if pattern.search(candidate.name):
            return candidate
    return None


def rescore_row(root: Path, row: dict[str, str]) -> dict[str, str]:
    best_image = row.get("bestImage", "").strip()
    reference_image = row.get("referenceImage", "").strip()
    if not best_image or not reference_image:
        return row

    best_path = resolve_path(root, best_image)
    reference_path = resolve_path(root, reference_image)
    if not best_path.exists() or not reference_path.exists():
        return row

    reference = Image.open(reference_path).convert("RGB")
    best_metrics = image_metrics(best_path, reference)

    original_metrics = best_metrics
    original_path = None
    stop_frame = int(row.get("stopFrame", "0") or "0")
    if stop_frame > 0:
        original_path = find_original_image(best_path, stop_frame)
        if original_path is not None:
            original_metrics = image_metrics(original_path, reference)

    offset = int(row.get("frameOffset", "0") or "0")
    output = dict(row)
    output["bestDifferentPixels"] = str(best_metrics.different_pixels)
    output["bestDifferentPercent"] = f"{best_metrics.different_percent:.4f}"
    output["bestMaxChannelDelta"] = str(best_metrics.max_channel_delta)
    output["bestStructuralSimilarity"] = f"{best_metrics.structural_similarity:.6f}"
    output["bestCoarseMeanDelta"] = f"{best_metrics.coarse_mean_delta:.4f}"
    output["originalDifferentPixels"] = str(original_metrics.different_pixels)
    output["originalStructuralSimilarity"] = f"{original_metrics.structural_similarity:.6f}"
    output["originalCoarseMeanDelta"] = f"{original_metrics.coarse_mean_delta:.4f}"
    output["originalImage"] = str(original_path) if original_path is not None else ""
    output["classification"] = classify_match(best_metrics, original_metrics, offset)
    return output


def main() -> int:
    args = parse_args()
    root = Path(args.root).resolve()
    inputs = [resolve_path(root, value) for value in args.inputs]
    rows = read_rows(inputs)
    if not rows:
        raise SystemExit("No rows found.")

    rescored = [rescore_row(root, row) for row in rows]
    fieldnames: list[str] = []
    for row in rescored:
        for key in row.keys():
            if key not in fieldnames:
                fieldnames.append(key)

    output = resolve_path(root, args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(rescored)

    for classification in sorted({row.get("classification", "") for row in rescored}):
        count = sum(1 for row in rescored if row.get("classification", "") == classification)
        print(f"{classification}: {count}")
    print(output)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
