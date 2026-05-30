#!/usr/bin/env python3
import argparse
import csv
from pathlib import Path

try:
    from PIL import Image, ImageDraw
except ImportError as exc:
    raise SystemExit("Pillow is required: python -m pip install pillow") from exc


def resolve_path(root: Path, value: str) -> Path:
    path = Path(value)
    if not path.is_absolute():
        path = root / path
    return path


def load_rows(report: Path) -> list[dict[str, str]]:
    with report.open(newline="", encoding="utf-8-sig") as handle:
        return list(csv.DictReader(handle))


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Create a PNG contact sheet from a deep gameplay CSV report.")
    parser.add_argument("report", help="Path to deep-gameplay.csv")
    parser.add_argument("--output", "-o", default="", help="Output PNG path")
    parser.add_argument("--columns", "-c", type=int, default=5, help="Number of image columns")
    parser.add_argument("--scale", "-s", type=int, default=2, help="Nearest-neighbor scale factor")
    parser.add_argument("--root", default=".", help="Root used to resolve relative finalPpm paths")
    parser.add_argument("--include-nonpass", action="store_true", help="Include rows that did not pass")
    parser.add_argument("--label-field", default="label", help="CSV field to use as the tile label")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    if args.columns <= 0:
        raise SystemExit("--columns must be greater than zero")
    if args.scale <= 0:
        raise SystemExit("--scale must be greater than zero")

    report = Path(args.report).resolve()
    root = Path(args.root).resolve()
    rows = load_rows(report)
    if not args.include_nonpass:
        rows = [row for row in rows if row.get("status") == "pass"]

    frames: list[tuple[str, Image.Image]] = []
    for row in rows:
        ppm = row.get("finalPpm", "")
        if not ppm:
            continue
        frame_path = resolve_path(root, ppm)
        if not frame_path.exists():
            continue
        label = row.get(args.label_field) or row.get("label") or frame_path.stem
        frames.append((label, Image.open(frame_path).convert("RGB")))

    if not frames:
        raise SystemExit("No frame images found in the report.")

    base_width, base_height = frames[0][1].size
    tile_width = base_width * args.scale
    tile_height = base_height * args.scale
    label_height = 28
    columns = min(args.columns, len(frames))
    rows_count = (len(frames) + columns - 1) // columns

    sheet = Image.new("RGB", (columns * tile_width, rows_count * (tile_height + label_height)), "white")
    draw = ImageDraw.Draw(sheet)

    for index, (label, image) in enumerate(frames):
        col = index % columns
        row = index // columns
        x = col * tile_width
        y = row * (tile_height + label_height)
        resized = image.resize((tile_width, tile_height), Image.Resampling.NEAREST)
        sheet.paste(resized, (x, y))
        draw.rectangle([x, y + tile_height, x + tile_width - 1, y + tile_height + label_height - 1], fill=(245, 245, 245))
        draw.text((x + 6, y + tile_height + 8), label[:64], fill=(0, 0, 0))

    output = Path(args.output).resolve() if args.output else report.with_name("contact-sheet.png")
    output.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(output)
    print(output)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
