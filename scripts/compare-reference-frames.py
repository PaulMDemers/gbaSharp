#!/usr/bin/env python3
import argparse
import csv
from pathlib import Path

try:
    from PIL import Image, ImageDraw
except ImportError as exc:
    raise SystemExit("Pillow is required: python -m pip install pillow") from exc


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Compare emulator frame captures against external reference images.")
    parser.add_argument("--manifest", default="docs/gba-reference-frames.csv")
    parser.add_argument("--output", default="reference-frame-comparison.csv")
    parser.add_argument("--diff-dir", default="reference-frame-diffs")
    parser.add_argument("--root", default=".")
    parser.add_argument("--write-diffs", action="store_true")
    parser.add_argument("--contact-sheet", default="", help="Optional PNG showing actual/reference/diff columns.")
    parser.add_argument("--contact-sheet-scale", type=int, default=2)
    parser.add_argument("--fail-on-diff", action="store_true")
    parser.add_argument("--fail-on-missing", action="store_true")
    return parser.parse_args()


def resolve_path(root: Path, value: str) -> Path:
    path = Path(value)
    if not path.is_absolute():
        path = root / path
    return path


def read_rows(path: Path) -> list[dict[str, str]]:
    with path.open(newline="", encoding="utf-8-sig") as handle:
        return list(csv.DictReader(handle))


def read_image(path: Path) -> Image.Image:
    return Image.open(path).convert("RGB")


def int_or_default(value: str, fallback: int) -> int:
    value = (value or "").strip()
    return int(value) if value else fallback


def build_diff(actual: Image.Image, reference: Image.Image, channel_tolerance: int) -> Image.Image:
    diff = Image.new("RGB", actual.size, "black")
    actual_pixels = actual.load()
    ref_pixels = reference.load()
    diff_pixels = diff.load()
    width, height = actual.size
    for y in range(height):
        for x in range(width):
            ar, ag, ab = actual_pixels[x, y]
            rr, rg, rb = ref_pixels[x, y]
            dr = abs(ar - rr)
            dg = abs(ag - rg)
            db = abs(ab - rb)
            if max(dr, dg, db) > channel_tolerance:
                diff_pixels[x, y] = (255, min(255, dg * 4), min(255, db * 4))
    return diff


def placeholder_image(size: tuple[int, int], title: str, detail: str) -> Image.Image:
    image = Image.new("RGB", size, (238, 238, 238))
    draw = ImageDraw.Draw(image)
    draw.rectangle([0, 0, size[0] - 1, size[1] - 1], outline=(180, 180, 180))
    draw.text((8, 8), title[:32], fill=(60, 60, 60))
    if detail:
        draw.text((8, 28), detail[:42], fill=(95, 95, 95))
    return image


def load_or_placeholder(path: Path, size: tuple[int, int], title: str) -> Image.Image:
    if path.exists():
        return read_image(path)
    return placeholder_image(size, title, path.name)


def make_contact_sheet(rows: list[dict[str, str]], output: Path, scale: int) -> None:
    if scale <= 0:
        raise SystemExit("--contact-sheet-scale must be greater than zero")

    frame_size = (240, 160)
    for row in rows:
        actual_path = Path(row["actualImage"])
        reference_path = Path(row["referenceImage"])
        if actual_path.exists():
            frame_size = read_image(actual_path).size
            break
        if reference_path.exists():
            frame_size = read_image(reference_path).size
            break

    label_height = 34
    header_height = 24
    columns = ["actual", "reference", "diff"]
    tile_width = frame_size[0] * scale
    tile_height = frame_size[1] * scale
    sheet_width = len(columns) * tile_width
    sheet_height = header_height + len(rows) * (tile_height + label_height)
    sheet = Image.new("RGB", (sheet_width, sheet_height), "white")
    draw = ImageDraw.Draw(sheet)

    for index, column in enumerate(columns):
        x = index * tile_width
        draw.rectangle([x, 0, x + tile_width - 1, header_height - 1], fill=(230, 230, 230))
        draw.text((x + 6, 6), column, fill=(0, 0, 0))

    for row_index, row in enumerate(rows):
        y = header_height + row_index * (tile_height + label_height)
        actual_path = Path(row["actualImage"])
        reference_path = Path(row["referenceImage"])
        diff_value = row.get("diffImage", "")
        diff_path = Path(diff_value) if diff_value else None

        actual = load_or_placeholder(actual_path, frame_size, "missing actual")
        reference = load_or_placeholder(reference_path, frame_size, "missing reference")
        if diff_path is not None and diff_path.exists():
            diff = read_image(diff_path)
        elif actual_path.exists() and reference_path.exists() and actual.size == reference.size:
            diff = build_diff(actual, reference, int_or_default(row.get("allowedChannelDelta", ""), 0))
        else:
            diff = placeholder_image(frame_size, row["status"], "no diff")

        for column_index, image in enumerate([actual, reference, diff]):
            resized = image.resize((tile_width, tile_height), Image.Resampling.NEAREST)
            sheet.paste(resized, (column_index * tile_width, y))

        label_y = y + tile_height
        draw.rectangle([0, label_y, sheet_width - 1, label_y + label_height - 1], fill=(245, 245, 245))
        summary = f"{row['label']} | {row['status']} | diffPixels={row.get('differentPixels', '')}"
        draw.text((6, label_y + 9), summary[:120], fill=(0, 0, 0))

    output.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(output)


def compare_images(
    label: str,
    actual_path: Path,
    reference_path: Path,
    max_different_pixels: int,
    max_channel_delta: int,
    diff_dir: Path,
    write_diff: bool,
) -> dict[str, str]:
    if not actual_path.exists():
        return {
            "label": label,
            "status": "missing-actual",
            "actualImage": str(actual_path),
            "referenceImage": str(reference_path),
            "differentPixels": "",
            "differentPercent": "",
            "maxChannelDelta": "",
            "allowedDifferentPixels": str(max_different_pixels),
            "allowedChannelDelta": str(max_channel_delta),
            "diffImage": "",
        }

    if not reference_path.exists():
        return {
            "label": label,
            "status": "missing-reference",
            "actualImage": str(actual_path),
            "referenceImage": str(reference_path),
            "differentPixels": "",
            "differentPercent": "",
            "maxChannelDelta": "",
            "allowedDifferentPixels": str(max_different_pixels),
            "allowedChannelDelta": str(max_channel_delta),
            "diffImage": "",
        }

    actual = read_image(actual_path)
    reference = read_image(reference_path)
    if actual.size != reference.size:
        return {
            "label": label,
            "status": "size-mismatch",
            "actualImage": str(actual_path),
            "referenceImage": str(reference_path),
            "differentPixels": "",
            "differentPercent": "",
            "maxChannelDelta": "",
            "allowedDifferentPixels": str(max_different_pixels),
            "allowedChannelDelta": str(max_channel_delta),
            "diffImage": "",
        }

    width, height = actual.size
    total = width * height
    actual_pixels = actual.load()
    ref_pixels = reference.load()
    different_pixels = 0
    observed_max_delta = 0
    for y in range(height):
        for x in range(width):
            ar, ag, ab = actual_pixels[x, y]
            rr, rg, rb = ref_pixels[x, y]
            pixel_delta = max(abs(ar - rr), abs(ag - rg), abs(ab - rb))
            observed_max_delta = max(observed_max_delta, pixel_delta)
            if pixel_delta > max_channel_delta:
                different_pixels += 1

    status = "pass" if different_pixels <= max_different_pixels else "diff"
    diff_path = ""
    if write_diff and status != "pass":
        diff_dir.mkdir(parents=True, exist_ok=True)
        diff_file = diff_dir / f"{label}.png"
        build_diff(actual, reference, max_channel_delta).save(diff_file)
        diff_path = str(diff_file)

    return {
        "label": label,
        "status": status,
        "actualImage": str(actual_path),
        "referenceImage": str(reference_path),
        "differentPixels": str(different_pixels),
        "differentPercent": f"{(different_pixels / total) * 100:.4f}",
        "maxChannelDelta": str(observed_max_delta),
        "allowedDifferentPixels": str(max_different_pixels),
        "allowedChannelDelta": str(max_channel_delta),
        "diffImage": diff_path,
    }


def main() -> int:
    args = parse_args()
    root = Path(args.root).resolve()
    manifest = resolve_path(root, args.manifest)
    output = resolve_path(root, args.output)
    diff_dir = resolve_path(root, args.diff_dir)
    contact_sheet = resolve_path(root, args.contact_sheet) if args.contact_sheet else None
    manifest_rows = read_rows(manifest)
    if not manifest_rows:
        raise SystemExit(f"No rows found in {manifest}")

    rows: list[dict[str, str]] = []
    for row in manifest_rows:
        label = (row.get("label") or Path(row.get("actualImage", "")).stem).strip()
        actual_path = resolve_path(root, row.get("actualImage", ""))
        reference_path = resolve_path(root, row.get("referenceImage", ""))
        max_different_pixels = int_or_default(row.get("maxDifferentPixels", ""), 0)
        max_channel_delta = int_or_default(row.get("maxChannelDelta", ""), 0)
        rows.append(
            compare_images(
                label,
                actual_path,
                reference_path,
                max_different_pixels,
                max_channel_delta,
                diff_dir,
                args.write_diffs,
            )
        )

    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=list(rows[0].keys()))
        writer.writeheader()
        writer.writerows(rows)

    for status, count in sorted({row["status"]: 0 for row in rows}.items()):
        count = sum(1 for row in rows if row["status"] == status)
        print(f"{status}: {count}")
    print(output)

    if contact_sheet is not None:
        make_contact_sheet(rows, contact_sheet, args.contact_sheet_scale)
        print(contact_sheet)

    bad = [row for row in rows if row["status"] not in {"pass", "missing-reference"}]
    missing = [row for row in rows if row["status"] == "missing-reference"]
    if args.fail_on_missing and missing:
        return 1
    if args.fail_on_diff and bad:
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
