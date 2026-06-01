#!/usr/bin/env python3
import argparse
import csv
from pathlib import Path

try:
    from PIL import Image
except ImportError as exc:
    raise SystemExit("Pillow is required: python -m pip install pillow") from exc


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Compare emulator frame captures against external reference images.")
    parser.add_argument("--manifest", default="docs/gba-reference-frames.csv")
    parser.add_argument("--output", default="reference-frame-comparison.csv")
    parser.add_argument("--diff-dir", default="reference-frame-diffs")
    parser.add_argument("--root", default=".")
    parser.add_argument("--write-diffs", action="store_true")
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

    bad = [row for row in rows if row["status"] not in {"pass", "missing-reference"}]
    missing = [row for row in rows if row["status"] == "missing-reference"]
    if args.fail_on_missing and missing:
        return 1
    if args.fail_on_diff and bad:
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
