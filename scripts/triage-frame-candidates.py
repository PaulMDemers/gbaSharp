#!/usr/bin/env python3
import argparse
import csv
import hashlib
import math
from pathlib import Path

from PIL import Image

from image_match_metrics import coarse_mean_delta, structural_similarity


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Score frame-candidate captures for static/menu-like output before promoting gameplay baselines."
    )
    parser.add_argument("input", help="frame-candidates.csv from new-deep-gameplay-frame-candidates.py")
    parser.add_argument("--output", default="", help="Output CSV path.")
    parser.add_argument("--markdown-output", default="", help="Optional Markdown summary path.")
    parser.add_argument("--root", default=".")
    parser.add_argument(
        "--static-delta",
        type=float,
        default=0.75,
        help="Coarse mean delta at/below this value is considered visually static.",
    )
    parser.add_argument(
        "--low-motion-delta",
        type=float,
        default=6.0,
        help="Coarse mean delta at/below this value is considered low motion.",
    )
    return parser.parse_args()


def resolve(root: Path, value: str) -> Path:
    path = Path(value)
    return path if path.is_absolute() else root / path


def read_rows(path: Path) -> list[dict[str, str]]:
    with path.open(newline="", encoding="utf-8-sig") as handle:
        return list(csv.DictReader(handle))


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def image_stats(image: Image.Image) -> dict[str, str]:
    rgb = image.convert("RGB")
    raw = rgb.tobytes()
    count = len(raw) // 3
    if count == 0:
        return {
            "meanLuma": "0.00",
            "lumaStdDev": "0.00",
            "uniqueSampleColors": "0",
            "darkPercent": "0.00",
            "brightPercent": "0.00",
        }

    lumas: list[float] = []
    sample_colors: set[tuple[int, int, int]] = set()
    dark = 0
    bright = 0
    for index in range(count):
        offset = index * 3
        r = raw[offset]
        g = raw[offset + 1]
        b = raw[offset + 2]
        luma = (0.299 * r) + (0.587 * g) + (0.114 * b)
        lumas.append(luma)
        if luma < 24:
            dark += 1
        if luma > 232:
            bright += 1
        if index % 16 == 0:
            sample_colors.add((r, g, b))

    mean = sum(lumas) / count
    variance = sum((value - mean) ** 2 for value in lumas) / count
    return {
        "meanLuma": f"{mean:.2f}",
        "lumaStdDev": f"{math.sqrt(variance):.2f}",
        "uniqueSampleColors": str(len(sample_colors)),
        "darkPercent": f"{(dark / count) * 100.0:.2f}",
        "brightPercent": f"{(bright / count) * 100.0:.2f}",
    }


def classify(
    row: dict[str, str],
    image_hash: str,
    previous_hash: str,
    previous_delta: float | None,
    unique_sample_colors: int,
    args: argparse.Namespace,
) -> str:
    if row.get("status") != "pass":
        return "capture-failed"
    if previous_hash and image_hash == previous_hash:
        return "duplicate-static"
    if previous_delta is not None and previous_delta <= args.static_delta:
        return "near-static"
    if previous_delta is not None and previous_delta <= args.low_motion_delta:
        return "low-motion"
    if unique_sample_colors < 24:
        return "low-color-risk"
    return "candidate"


def write_csv(path: Path, rows: list[dict[str, str]]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    fieldnames: list[str] = []
    for row in rows:
        for key in row.keys():
            if key not in fieldnames:
                fieldnames.append(key)
    with path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(rows)


def write_markdown(path: Path, rows: list[dict[str, str]], input_path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    lines = [
        "# Frame Candidate Triage",
        "",
        f"- Input: `{input_path}`",
        f"- Rows: {len(rows)}",
        "",
        "## Classification",
        "",
    ]
    for classification in sorted({row["classification"] for row in rows}):
        count = sum(1 for row in rows if row["classification"] == classification)
        lines.append(f"- {classification}: {count}")

    lines.extend(["", "## Candidates", ""])
    for row in rows:
        lines.append(
            "- "
            + f"frame={row.get('requestedFrame', '')}, "
            + f"class={row['classification']}, "
            + f"prevDelta={row['deltaFromPrevious']}, "
            + f"ssimPrev={row['ssimFromPrevious']}, "
            + f"colors={row['uniqueSampleColors']}, "
            + f"image=`{row.get('finalPpm', '')}`"
        )

    path.write_text("\n".join(lines).rstrip() + "\n", encoding="utf-8")


def main() -> int:
    args = parse_args()
    root = Path(args.root).resolve()
    input_path = resolve(root, args.input)
    output_path = resolve(
        root,
        args.output or str(input_path.with_name("frame-candidate-triage.csv")),
    )
    markdown_path = resolve(root, args.markdown_output) if args.markdown_output else None

    rows = read_rows(input_path)
    if not rows:
        raise SystemExit(f"No rows found in {input_path}")

    output_rows: list[dict[str, str]] = []
    previous_image: Image.Image | None = None
    previous_hash = ""
    for row in rows:
        final_ppm = row.get("finalPpm", "")
        image_path = resolve(root, final_ppm)
        if not image_path.exists():
            output = dict(row)
            output.update(
                {
                    "imageHash": "",
                    "deltaFromPrevious": "",
                    "ssimFromPrevious": "",
                    "classification": "missing-image",
                }
            )
            output_rows.append(output)
            continue

        image = Image.open(image_path).convert("RGB")
        image_hash = sha256(image_path)
        stats = image_stats(image)
        previous_delta: float | None = None
        previous_ssim = ""
        if previous_image is not None:
            previous_delta = coarse_mean_delta(image, previous_image)
            previous_ssim = f"{structural_similarity(image, previous_image):.6f}"

        classification = classify(
            row,
            image_hash,
            previous_hash,
            previous_delta,
            int(stats["uniqueSampleColors"]),
            args,
        )

        output = dict(row)
        output.update(stats)
        output.update(
            {
                "imageHash": image_hash,
                "deltaFromPrevious": f"{previous_delta:.4f}" if previous_delta is not None else "",
                "ssimFromPrevious": previous_ssim,
                "classification": classification,
            }
        )
        output_rows.append(output)
        previous_image = image
        previous_hash = image_hash

    write_csv(output_path, output_rows)
    if markdown_path is not None:
        write_markdown(markdown_path, output_rows, input_path)

    for classification in sorted({row["classification"] for row in output_rows}):
        count = sum(1 for row in output_rows if row["classification"] == classification)
        print(f"{classification}: {count}")
    print(output_path)
    if markdown_path is not None:
        print(markdown_path)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
