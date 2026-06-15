#!/usr/bin/env python3
import argparse
import collections
import csv
import statistics
from pathlib import Path

GBA_FRAME_CYCLES = 280_896


def common_intervals(cycles: list[int]) -> str:
    intervals = [right - left for left, right in zip(cycles, cycles[1:])]
    if not intervals:
        return ""
    common = collections.Counter(intervals).most_common(8)
    return "; ".join(f"{value}x{count}" for value, count in common)


def summarize(rows: list[dict[str, str]], max_frame_rows: int) -> str:
    lines: list[str] = []
    lines.append("# PC Trace Summary")
    lines.append("")
    lines.append(f"- hits: {len(rows):,}")
    if not rows:
        return "\n".join(lines) + "\n"

    frames = [int(row["frame"]) for row in rows]
    lines.append(f"- frame span: {min(frames)}-{max(frames)}")
    lines.append("")

    for pc in sorted({row["pc"] for row in rows}):
        pc_rows = [row for row in rows if row["pc"] == pc]
        by_frame: dict[int, list[int]] = collections.defaultdict(list)
        for row in pc_rows:
            by_frame[int(row["frame"])].append(int(row["cycle"]))

        counts = [len(cycles) for cycles in by_frame.values()]
        all_cycles = [int(row["cycle"]) for row in pc_rows]
        first_offsets = [min(cycles) % GBA_FRAME_CYCLES for cycles in by_frame.values()]
        last_offsets = [max(cycles) % GBA_FRAME_CYCLES for cycles in by_frame.values()]

        lines.append(f"## `{pc}`")
        lines.append("")
        lines.append(f"- hits: {len(pc_rows):,}")
        lines.append(f"- frames hit: {len(by_frame):,}")
        lines.append(f"- hits/frame: min={min(counts):,}; avg={statistics.mean(counts):.3f}; max={max(counts):,}")
        lines.append(f"- first offset cycles: min={min(first_offsets):,}; avg={statistics.mean(first_offsets):.3f}; max={max(first_offsets):,}")
        lines.append(f"- last offset cycles: min={min(last_offsets):,}; avg={statistics.mean(last_offsets):.3f}; max={max(last_offsets):,}")
        lines.append(f"- common hit intervals: {common_intervals(all_cycles)}")
        lines.append("")
        lines.append("| frame | hits | firstOffset | lastOffset | spanCycles | line | videoLine | dispstat |")
        lines.append("| ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |")
        for frame in sorted(by_frame)[:max_frame_rows]:
            frame_rows = [row for row in pc_rows if int(row["frame"]) == frame]
            cycles = by_frame[frame]
            first = min(cycles)
            last = max(cycles)
            first_row = min(frame_rows, key=lambda row: int(row["cycle"]))
            lines.append(
                f"| {frame} | {len(cycles)} | {first % GBA_FRAME_CYCLES} | {last % GBA_FRAME_CYCLES} | {last - first} | "
                f"{first_row['line']} | {first_row['videoLine']} | `{first_row['dispstat']}` |"
            )
        lines.append("")

    return "\n".join(lines) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser(description="Summarize gbaSharp --trace-pc-csv output.")
    parser.add_argument("csv", help="Path to a gbaSharp PC trace CSV")
    parser.add_argument("--output-md", default="", help="Optional Markdown output path")
    parser.add_argument("--max-frame-rows", type=int, default=24, help="Maximum per-PC frame rows to include")
    args = parser.parse_args()

    if args.max_frame_rows <= 0:
        raise SystemExit("--max-frame-rows must be greater than zero")

    with Path(args.csv).open(newline="", encoding="utf-8-sig") as handle:
        rows = list(csv.DictReader(handle))

    text = summarize(rows, args.max_frame_rows)
    if args.output_md:
        output = Path(args.output_md)
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(text, encoding="utf-8")
    else:
        print(text, end="")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
