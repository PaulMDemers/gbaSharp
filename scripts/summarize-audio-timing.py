#!/usr/bin/env python3
import argparse
import collections
import csv
import statistics
from pathlib import Path


INTERESTING_REGISTERS = {
    "0x04000060",
    "0x04000062",
    "0x04000064",
    "0x04000068",
    "0x04000069",
    "0x0400006C",
    "0x0400006D",
    "0x04000070",
    "0x04000072",
    "0x04000074",
    "0x04000078",
    "0x0400007C",
    "0x0400007D",
    "0x04000080",
    "0x04000081",
    "0x04000082",
    "0x04000084",
    "0x04000100",
    "0x04000102",
    "0x04000104",
    "0x04000106",
}


def parse_range(value: str) -> tuple[int, int]:
    parts = value.split(":", 1)
    if len(parts) != 2:
        raise argparse.ArgumentTypeError("ranges must be START:END")
    start = int(parts[0])
    end = int(parts[1])
    if end <= start:
        raise argparse.ArgumentTypeError("range END must be greater than START")
    return start, end


def common_intervals(cycles: list[int]) -> str:
    intervals = [right - left for left, right in zip(cycles, cycles[1:])]
    if not intervals:
        return ""
    common = collections.Counter(intervals).most_common(5)
    average = statistics.mean(intervals)
    return "; ".join(f"{value}x{count}" for value, count in common) + f"; avg={average:.3f}"


def summarize(rows: list[dict[str, str]], ranges: list[tuple[int, int]]) -> list[str]:
    lines: list[str] = []
    counts = collections.Counter(row["kind"] for row in rows)
    lines.append("# Audio Timing Summary")
    lines.append("")
    lines.append("## Event Counts")
    lines.append("")
    for kind, count in sorted(counts.items()):
        lines.append(f"- `{kind}`: {count:,}")

    register_rows = [
        row for row in rows
        if row["kind"] == "iowrite" and row["address"] in INTERESTING_REGISTERS
    ]
    if register_rows:
        lines.append("")
        lines.append("## Sound Register Writes")
        lines.append("")
        lines.append("| frame | cycle | address | bytes | value | pc | soundcntH |")
        lines.append("| ---: | ---: | --- | ---: | --- | --- | --- |")
        for row in register_rows:
            lines.append(
                f"| {row['frame']} | {row['cycle']} | `{row['address']}` | {row['bytes']} | `{row['value']}` | `{row['cpuPc']}` | `{row['soundcntH']}` |"
            )

    for start, end in ranges:
        section_rows = [
            row for row in rows
            if start <= int(row["frame"]) < end
        ]
        direct_rows = [row for row in section_rows if row["kind"] == "direct"]
        dma_rows = [
            row for row in section_rows
            if row["kind"] == "dma" and row["dmaDestination"] in {"0x040000A0", "0x040000A4"}
        ]

        lines.append("")
        lines.append(f"## Frames {start}-{end - 1}")
        lines.append("")
        lines.append(f"- direct events: {len(direct_rows):,}")
        for key in sorted({(row["fifo"], row["timer"]) for row in direct_rows}):
            cycles = [int(row["cycle"]) for row in direct_rows if (row["fifo"], row["timer"]) == key]
            lines.append(f"- direct fifo={key[0]} timer={key[1]} count={len(cycles):,}: {common_intervals(cycles)}")

        lines.append(f"- FIFO DMA events: {len(dma_rows):,}")
        for key in sorted({(row["dmaChannel"], row["dmaDestination"]) for row in dma_rows}):
            matching = [row for row in dma_rows if (row["dmaChannel"], row["dmaDestination"]) == key]
            cycles = [int(row["cycle"]) for row in matching]
            first = matching[0] if matching else None
            source = first["dmaSource"] if first else ""
            preview = first["sourcePreview"] if first else ""
            lines.append(
                f"- dma{key[0]}->{key[1]} count={len(cycles):,}: {common_intervals(cycles)}; firstSource={source}; firstPreview={preview}"
            )

    return lines


def main() -> int:
    parser = argparse.ArgumentParser(description="Summarize gbaSharp --audio-timing-csv output.")
    parser.add_argument("csv", help="Path to a gbaSharp audio timing CSV")
    parser.add_argument("--range", dest="ranges", action="append", type=parse_range, default=[], help="Frame range to summarize, START:END")
    parser.add_argument("--output-md", default="", help="Optional Markdown output path")
    args = parser.parse_args()

    path = Path(args.csv)
    with path.open(newline="", encoding="utf-8-sig") as handle:
        rows = list(csv.DictReader(handle))

    ranges = args.ranges or [(0, 10**9)]
    lines = summarize(rows, ranges)
    text = "\n".join(lines) + "\n"
    if args.output_md:
        output = Path(args.output_md)
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(text, encoding="utf-8")
    else:
        print(text, end="")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
