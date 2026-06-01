#!/usr/bin/env python3
import argparse
import csv
from pathlib import Path

try:
    from PIL import Image
except ImportError as exc:
    raise SystemExit("Pillow is required: python -m pip install pillow") from exc


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Validate external reference captures before pixel comparison.")
    parser.add_argument("--manifest", default="docs/gba-reference-frames.csv")
    parser.add_argument("--output", default="artifacts/reference-capture-validation.csv")
    parser.add_argument("--reference-root", default="reference-captures")
    parser.add_argument("--root", default=".")
    parser.add_argument("--expected-width", type=int, default=240)
    parser.add_argument("--expected-height", type=int, default=160)
    parser.add_argument("--fail-on-missing", action="store_true")
    parser.add_argument("--fail-on-invalid", action="store_true")
    parser.add_argument("--fail-on-extra", action="store_true")
    return parser.parse_args()


def resolve_path(root: Path, value: str) -> Path:
    path = Path(value)
    return path if path.is_absolute() else root / path


def read_rows(path: Path) -> list[dict[str, str]]:
    with path.open(newline="", encoding="utf-8-sig") as handle:
        return list(csv.DictReader(handle))


def image_size(path: Path) -> tuple[int, int]:
    with Image.open(path) as image:
        return image.size


def expected_size(root: Path, actual_value: str, fallback: tuple[int, int]) -> tuple[int, int]:
    if not actual_value:
        return fallback

    actual_path = resolve_path(root, actual_value)
    if actual_path.exists():
        return image_size(actual_path)
    return fallback


def validate_manifest_rows(
    root: Path,
    manifest_rows: list[dict[str, str]],
    fallback_size: tuple[int, int],
) -> list[dict[str, str]]:
    rows: list[dict[str, str]] = []
    for item in manifest_rows:
        label = (item.get("label") or Path(item.get("referenceImage", "")).stem).strip()
        reference_path = resolve_path(root, item.get("referenceImage", ""))
        actual_image = item.get("actualImage", "")
        expected_width, expected_height = expected_size(root, actual_image, fallback_size)

        status = "ok"
        width = ""
        height = ""
        message = ""
        if not reference_path.exists():
            status = "missing"
            message = "reference image does not exist"
        else:
            try:
                width_value, height_value = image_size(reference_path)
                width = str(width_value)
                height = str(height_value)
                if (width_value, height_value) != (expected_width, expected_height):
                    status = "bad-size"
                    message = f"expected {expected_width}x{expected_height}"
            except Exception as exc:  # Pillow raises several format-specific exception types.
                status = "unreadable"
                message = str(exc)

        rows.append(
            {
                "label": label,
                "status": status,
                "referenceImage": str(reference_path),
                "actualImage": str(resolve_path(root, actual_image)) if actual_image else "",
                "width": width,
                "height": height,
                "expectedWidth": str(expected_width),
                "expectedHeight": str(expected_height),
                "message": message,
            }
        )
    return rows


def find_extra_reference_images(root: Path, reference_root: Path, manifest_rows: list[dict[str, str]]) -> list[dict[str, str]]:
    if not reference_root.exists():
        return []

    expected = {resolve_path(root, row.get("referenceImage", "")).resolve() for row in manifest_rows}
    extras: list[dict[str, str]] = []
    for path in sorted(reference_root.rglob("*.png")):
        resolved = path.resolve()
        if resolved in expected:
            continue
        width = ""
        height = ""
        message = "not listed in manifest"
        try:
            width_value, height_value = image_size(path)
            width = str(width_value)
            height = str(height_value)
        except Exception as exc:
            message = f"not listed in manifest; unreadable: {exc}"
        extras.append(
            {
                "label": path.stem,
                "status": "extra",
                "referenceImage": str(path),
                "actualImage": "",
                "width": width,
                "height": height,
                "expectedWidth": "",
                "expectedHeight": "",
                "message": message,
            }
        )
    return extras


def write_csv(path: Path, rows: list[dict[str, str]]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=list(rows[0].keys()))
        writer.writeheader()
        writer.writerows(rows)


def print_summary(rows: list[dict[str, str]]) -> None:
    statuses = sorted({row["status"] for row in rows})
    for status in statuses:
        print(f"{status}: {sum(1 for row in rows if row['status'] == status)}")


def main() -> int:
    args = parse_args()
    root = Path(args.root).resolve()
    manifest = resolve_path(root, args.manifest)
    output = resolve_path(root, args.output)
    reference_root = resolve_path(root, args.reference_root)
    fallback_size = (args.expected_width, args.expected_height)

    manifest_rows = read_rows(manifest)
    if not manifest_rows:
        raise SystemExit(f"No rows found in {manifest}")

    rows = validate_manifest_rows(root, manifest_rows, fallback_size)
    rows.extend(find_extra_reference_images(root, reference_root, manifest_rows))
    write_csv(output, rows)
    print_summary(rows)
    print(output)

    if args.fail_on_missing and any(row["status"] == "missing" for row in rows):
        return 1
    if args.fail_on_invalid and any(row["status"] in {"bad-size", "unreadable"} for row in rows):
        return 1
    if args.fail_on_extra and any(row["status"] == "extra" for row in rows):
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
