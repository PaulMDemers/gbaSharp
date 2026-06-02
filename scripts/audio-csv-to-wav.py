#!/usr/bin/env python3
import argparse
import csv
import wave
from pathlib import Path

GBA_CLOCK_HZ = 16_777_216
DEFAULT_DIRECT_SCALE = 192.0
DEFAULT_PSG_SCALE = 32.0
DEFAULT_MAX_FRAMES_PER_EVENT = 4_410


def parse_int(row: dict[str, str], name: str, default: int = 0) -> int:
    value = row.get(name, "")
    if value == "":
        return default
    return int(value)


def clamp16(value: float) -> int:
    rounded = int(round(value))
    return max(-32768, min(32767, rounded))


def load_events(
    path: Path,
    gain: float,
    direct_scale: float,
    psg_scale: float,
    scale_override: float,
    sequence_start: int,
) -> tuple[list[tuple[int, int, int, int, int]], bool]:
    events = []
    has_fifo = False
    sequence = sequence_start
    with path.open(newline="", encoding="utf-8-sig") as handle:
        reader = csv.DictReader(handle)
        has_fifo = "fifo" in (reader.fieldnames or [])
        base_scale = scale_override if scale_override > 0 else direct_scale if has_fifo else psg_scale
        scale = base_scale * gain
        for row in reader:
            cycle = parse_int(row, "cycle", -1)
            if cycle < 0:
                continue
            fifo = parse_int(row, "fifo", -1) if has_fifo else -1
            left = int(round(parse_int(row, "left") * scale))
            right = int(round(parse_int(row, "right") * scale))
            events.append((cycle, sequence, fifo, left, right))
            sequence += 1

    return sorted(events), has_fifo


def render_samples(
    events: list[tuple[int, int, int, int, int]],
    sample_rate: int,
    max_frames_per_event: int = DEFAULT_MAX_FRAMES_PER_EVENT,
) -> bytes:
    if not events:
        return b""

    current_by_fifo = [[0, 0], [0, 0]]
    current_psg = [0, 0]
    last_cycle = None
    fractional_frames = 0.0
    data = bytearray()

    for cycle, _, fifo, left, right in sorted(events):
        if last_cycle is None:
            last_cycle = cycle
        elif cycle < last_cycle:
            continue
        elif cycle > last_cycle:
            exact_frames = ((cycle - last_cycle) * sample_rate / GBA_CLOCK_HZ) + fractional_frames
            frames = int(exact_frames)
            fractional_frames = exact_frames - frames
            if frames > max_frames_per_event:
                frames = max_frames_per_event
                fractional_frames = 0.0

            current_left = current_by_fifo[0][0] + current_by_fifo[1][0] + current_psg[0]
            current_right = current_by_fifo[0][1] + current_by_fifo[1][1] + current_psg[1]
            frame = clamp16(current_left).to_bytes(2, byteorder="little", signed=True)
            frame += clamp16(current_right).to_bytes(2, byteorder="little", signed=True)
            data.extend(frame * frames)
            last_cycle = cycle

        if 0 <= fifo < len(current_by_fifo):
            current_by_fifo[fifo][0] = left
            current_by_fifo[fifo][1] = right
        elif fifo < 0:
            current_psg[0] = left
            current_psg[1] = right

    return bytes(data)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Convert a gbaSharp direct-sound or PSG audio CSV capture to a stereo PCM WAV.")
    parser.add_argument("csv", help="Path to a dump-frame --audio-csv or --psg-csv output")
    parser.add_argument("wav", help="Output WAV path")
    parser.add_argument("--mix", action="append", default=[], help="Additional direct-sound or PSG CSV to mix into the output WAV")
    parser.add_argument("--sample-rate", "-r", type=int, default=44_100, help="Output sample rate")
    parser.add_argument("--gain", "-g", type=float, default=1.0, help="Linear output gain applied after source scale")
    parser.add_argument("--direct-scale", type=float, default=DEFAULT_DIRECT_SCALE, help="Direct-sound sample scale before gain")
    parser.add_argument("--psg-scale", type=float, default=DEFAULT_PSG_SCALE, help="PSG sample scale before gain")
    parser.add_argument("--scale", type=float, default=0, help="Legacy override for all source scales before gain")
    parser.add_argument("--max-frames-per-event", type=int, default=DEFAULT_MAX_FRAMES_PER_EVENT, help="Frame cap for a single cycle gap")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    if args.sample_rate <= 0:
        raise SystemExit("--sample-rate must be greater than zero")
    if args.gain <= 0:
        raise SystemExit("--gain must be greater than zero")
    if args.direct_scale <= 0:
        raise SystemExit("--direct-scale must be greater than zero")
    if args.psg_scale <= 0:
        raise SystemExit("--psg-scale must be greater than zero")
    if args.scale < 0:
        raise SystemExit("--scale must be zero or greater")
    if args.max_frames_per_event <= 0:
        raise SystemExit("--max-frames-per-event must be greater than zero")

    csv_paths = [Path(args.csv).resolve(), *[Path(path).resolve() for path in args.mix]]
    wav_path = Path(args.wav).resolve()
    all_events = []
    source_counts = {"direct-sound": 0, "PSG": 0}
    sequence = 0
    for csv_path in csv_paths:
        events, has_fifo = load_events(csv_path, args.gain, args.direct_scale, args.psg_scale, args.scale, sequence)
        sequence += len(events)
        all_events.extend(events)
        source_counts["direct-sound" if has_fifo else "PSG"] += len(events)

    frames = render_samples(sorted(all_events), args.sample_rate, args.max_frames_per_event)

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
    print(f"Wrote {frame_count:,} stereo frames ({duration:.3f}s) from {source_summary} cycle events on a shared timeline to {wav_path}.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
