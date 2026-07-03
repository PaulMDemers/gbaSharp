#!/usr/bin/env python3
import argparse
import csv
from collections import Counter, defaultdict
from pathlib import Path
from statistics import median

GBA_CLOCK_HZ = 16_777_216


def parse_int(row: dict[str, str], name: str, default: int = 0) -> int:
    value = row.get(name, "")
    if value is None or value == "":
        return default
    return int(value)


def load_rows(path: Path) -> tuple[list[dict[str, str]], list[str]]:
    with path.open(newline="", encoding="utf-8-sig") as handle:
        reader = csv.DictReader(handle)
        return list(reader), list(reader.fieldnames or [])


def format_rate(delta_cycles: float) -> str:
    if delta_cycles <= 0:
        return "n/a"
    return f"{GBA_CLOCK_HZ / delta_cycles:.2f} Hz"


def summarize_group(cycles: list[int]) -> tuple[int, int, int, float, str]:
    deltas = [right - left for left, right in zip(cycles, cycles[1:]) if right > left]
    if not deltas:
        return (0, 0, 0, 0.0, "n/a")

    mode_delta, _ = Counter(deltas).most_common(1)[0]
    median_delta = float(median(deltas))
    return (min(deltas), max(deltas), mode_delta, median_delta, format_rate(median_delta))


def build_summary(path: Path, rows: list[dict[str, str]], fieldnames: list[str]) -> str:
    if "fifo" not in fieldnames:
        return build_psg_summary(path, rows)

    samples = []
    cycles_by_group: dict[tuple[int, int], list[int]] = defaultdict(list)
    for row in rows:
        sample = {
            "step": parse_int(row, "step"),
            "frame": parse_int(row, "frame"),
            "cycle": parse_int(row, "cycle"),
            "fifo": parse_int(row, "fifo"),
            "timer": parse_int(row, "timer", -1),
            "raw": parse_int(row, "raw"),
            "left": parse_int(row, "left"),
            "right": parse_int(row, "right"),
        }
        samples.append(sample)
        cycles_by_group[(sample["fifo"], sample["timer"])].append(sample["cycle"])

    lines = [f"# Audio CSV Summary: `{path.name}`", ""]
    lines.append(f"- Samples: {len(samples):,}")
    if not samples:
        return "\n".join(lines)

    nonzero = [sample for sample in samples if sample["raw"] or sample["left"] or sample["right"]]
    left_nonzero = sum(1 for sample in samples if sample["left"])
    right_nonzero = sum(1 for sample in samples if sample["right"])
    lines.extend(
        [
            f"- Frames: {min(sample['frame'] for sample in samples):,}-{max(sample['frame'] for sample in samples):,}",
            f"- Cycles: {min(sample['cycle'] for sample in samples):,}-{max(sample['cycle'] for sample in samples):,}",
            f"- Non-zero samples: {len(nonzero):,} ({len(nonzero) / len(samples):.1%})",
            f"- Routed non-zero: left={left_nonzero:,}, right={right_nonzero:,}",
            f"- Raw range: {min(sample['raw'] for sample in samples):,}..{max(sample['raw'] for sample in samples):,}",
            f"- Left range: {min(sample['left'] for sample in samples):,}..{max(sample['left'] for sample in samples):,}",
            f"- Right range: {min(sample['right'] for sample in samples):,}..{max(sample['right'] for sample in samples):,}",
            "",
            "| FIFO | Timer | Samples | Min Delta | Max Delta | Mode Delta | Median Delta | Est. Rate |",
            "| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |",
        ]
    )

    for (fifo, timer), cycles in sorted(cycles_by_group.items()):
        min_delta, max_delta, mode_delta, median_delta, rate = summarize_group(cycles)
        lines.append(
            f"| {fifo} | {timer} | {len(cycles):,} | {min_delta:,} | {max_delta:,} | "
            f"{mode_delta:,} | {median_delta:,.1f} | {rate} |"
        )

    return "\n".join(lines)


def build_psg_summary(path: Path, rows: list[dict[str, str]]) -> str:
    samples = []
    cycles = []
    for row in rows:
        sample = {
            "step": parse_int(row, "step"),
            "frame": parse_int(row, "frame"),
            "cycle": parse_int(row, "cycle"),
            "left": parse_int(row, "left"),
            "right": parse_int(row, "right"),
        }
        samples.append(sample)
        cycles.append(sample["cycle"])

    lines = [f"# PSG CSV Summary: `{path.name}`", ""]
    lines.append(f"- Samples: {len(samples):,}")
    if not samples:
        return "\n".join(lines)

    nonzero = [sample for sample in samples if sample["left"] or sample["right"]]
    left_nonzero = sum(1 for sample in samples if sample["left"])
    right_nonzero = sum(1 for sample in samples if sample["right"])
    min_delta, max_delta, mode_delta, median_delta, rate = summarize_group(cycles)
    lines.extend(
        [
            f"- Frames: {min(sample['frame'] for sample in samples):,}-{max(sample['frame'] for sample in samples):,}",
            f"- Cycles: {min(cycles):,}-{max(cycles):,}",
            f"- Non-zero samples: {len(nonzero):,} ({len(nonzero) / len(samples):.1%})",
            f"- Routed non-zero: left={left_nonzero:,}, right={right_nonzero:,}",
            f"- Left range: {min(sample['left'] for sample in samples):,}..{max(sample['left'] for sample in samples):,}",
            f"- Right range: {min(sample['right'] for sample in samples):,}..{max(sample['right'] for sample in samples):,}",
            "",
            "| Samples | Min Delta | Max Delta | Mode Delta | Median Delta | Est. Rate |",
            "| ---: | ---: | ---: | ---: | ---: | ---: |",
            f"| {len(samples):,} | {min_delta:,} | {max_delta:,} | {mode_delta:,} | {median_delta:,.1f} | {rate} |",
        ]
    )

    return "\n".join(lines)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Summarize a gbaSharp direct-sound or PSG audio CSV capture.")
    parser.add_argument("csv", help="Path to a dump-frame --audio-csv or --psg-csv output")
    parser.add_argument("--output", "-o", default="", help="Optional Markdown output path")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    path = Path(args.csv).resolve()
    rows, fieldnames = load_rows(path)
    summary = build_summary(path, rows, fieldnames)
    if args.output:
        output = Path(args.output).resolve()
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(summary + "\n", encoding="utf-8")
    print(summary)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
