#!/usr/bin/env python3
import argparse
import csv
import wave
from pathlib import Path

GBA_CLOCK_HZ = 16_777_216


def parse_int(row: dict[str, str], name: str, default: int = 0) -> int:
    value = row.get(name, "")
    if value == "":
        return default
    return int(value)


def clamp16(value: float) -> int:
    rounded = int(round(value))
    return max(-32768, min(32767, rounded))


def load_events(path: Path, gain: float, scale_override: float) -> tuple[list[tuple[int, int, int, int]], bool]:
    events = []
    has_fifo = False
    with path.open(newline="", encoding="utf-8-sig") as handle:
        reader = csv.DictReader(handle)
        has_fifo = "fifo" in (reader.fieldnames or [])
        base_scale = scale_override if scale_override > 0 else 256.0 if has_fifo else 32.0
        scale = base_scale * gain
        for row in reader:
            cycle = parse_int(row, "cycle", -1)
            if cycle < 0:
                continue
            fifo = parse_int(row, "fifo", -1) if has_fifo else -1
            events.append((cycle, fifo, clamp16(parse_int(row, "left") * scale), clamp16(parse_int(row, "right") * scale)))

    return sorted(events), has_fifo


def render_samples(events: list[tuple[int, int, int, int]], sample_rate: int) -> bytes:
    if not events:
        return b""

    first_cycle = events[0][0]
    last_cycle = events[-1][0]
    duration_cycles = max(1, last_cycle - first_cycle)
    output_frames = max(1, int(round(duration_cycles * sample_rate / GBA_CLOCK_HZ)))
    cycles_per_output = GBA_CLOCK_HZ / sample_rate
    event_index = 0
    current_by_fifo = [[0, 0], [0, 0]]
    current_psg = [0, 0]
    data = bytearray(output_frames * 4)
    for frame in range(output_frames):
        target_cycle = first_cycle + int(frame * cycles_per_output)
        while event_index < len(events) and events[event_index][0] <= target_cycle:
            _, fifo, left, right = events[event_index]
            if 0 <= fifo < len(current_by_fifo):
                current_by_fifo[fifo][0] = left
                current_by_fifo[fifo][1] = right
            elif fifo < 0:
                current_psg[0] = left
                current_psg[1] = right
            event_index += 1

        current_left = current_by_fifo[0][0] + current_by_fifo[1][0] + current_psg[0]
        current_right = current_by_fifo[0][1] + current_by_fifo[1][1] + current_psg[1]
        left = clamp16(current_left)
        right = clamp16(current_right)
        offset = frame * 4
        data[offset:offset + 2] = left.to_bytes(2, byteorder="little", signed=True)
        data[offset + 2:offset + 4] = right.to_bytes(2, byteorder="little", signed=True)

    return bytes(data)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Convert a gbaSharp direct-sound or PSG audio CSV capture to a stereo PCM WAV.")
    parser.add_argument("csv", help="Path to a dump-frame --audio-csv or --psg-csv output")
    parser.add_argument("wav", help="Output WAV path")
    parser.add_argument("--mix", action="append", default=[], help="Additional direct-sound or PSG CSV to mix into the output WAV")
    parser.add_argument("--sample-rate", "-r", type=int, default=44_100, help="Output sample rate")
    parser.add_argument("--gain", "-g", type=float, default=0.75, help="Linear output gain")
    parser.add_argument("--scale", type=float, default=0, help="Override sample scale before gain; defaults to 256 for direct sound and 32 for PSG")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    if args.sample_rate <= 0:
        raise SystemExit("--sample-rate must be greater than zero")
    if args.gain <= 0:
        raise SystemExit("--gain must be greater than zero")

    csv_paths = [Path(args.csv).resolve(), *[Path(path).resolve() for path in args.mix]]
    wav_path = Path(args.wav).resolve()
    all_events = []
    source_counts = {"direct-sound": 0, "PSG": 0}
    for csv_path in csv_paths:
        events, has_fifo = load_events(csv_path, args.gain, args.scale)
        all_events.extend(events)
        source_counts["direct-sound" if has_fifo else "PSG"] += len(events)

    frames = render_samples(sorted(all_events), args.sample_rate)

    wav_path.parent.mkdir(parents=True, exist_ok=True)
    with wave.open(str(wav_path), "wb") as output:
        output.setnchannels(2)
        output.setsampwidth(2)
        output.setframerate(args.sample_rate)
        output.writeframes(frames)

    frame_count = len(frames) // 4
    duration = frame_count / args.sample_rate if args.sample_rate else 0.0
    source_summary = ", ".join(f"{count:,} {name}" for name, count in source_counts.items() if count)
    if not source_summary:
        source_summary = "0 audio"
    print(f"Wrote {frame_count:,} stereo frames ({duration:.3f}s) from {source_summary} cycle events to {wav_path}.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
