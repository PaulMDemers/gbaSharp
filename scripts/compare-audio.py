#!/usr/bin/env python3
import argparse
import csv
import math
import struct
import wave
from pathlib import Path


def read_pcm16_wav(path: Path) -> tuple[int, list[list[int]]]:
    with wave.open(str(path), "rb") as handle:
        channels = handle.getnchannels()
        sample_width = handle.getsampwidth()
        sample_rate = handle.getframerate()
        frames = handle.getnframes()
        if sample_width != 2:
            raise ValueError(f"{path} is {sample_width * 8}-bit audio; only 16-bit PCM WAV is supported.")
        if channels not in (1, 2):
            raise ValueError(f"{path} has {channels} channels; only mono/stereo WAV is supported.")

        raw = handle.readframes(frames)

    values = struct.unpack("<" + "h" * (len(raw) // 2), raw)
    samples = [[] for _ in range(channels)]
    for index, value in enumerate(values):
        samples[index % channels].append(value)
    if channels == 1:
        samples.append(samples[0][:])
    return sample_rate, samples


def mono(samples: list[list[int]]) -> list[float]:
    left, right = samples
    return [(l + r) / 2.0 for l, r in zip(left, right)]


def trim_leading_silence(samples: list[list[int]], threshold: int, padding: int) -> tuple[list[list[int]], int]:
    if threshold <= 0:
        return samples, 0

    frames = len(samples[0])
    first = 0
    for index in range(frames):
        if abs(samples[0][index]) >= threshold or abs(samples[1][index]) >= threshold:
            first = max(0, index - padding)
            break
    else:
        return samples, 0

    return [channel[first:] for channel in samples], first


def rms(values: list[int | float]) -> float:
    if not values:
        return 0.0
    return math.sqrt(sum(float(value) * float(value) for value in values) / len(values))


def peak(values: list[int]) -> int:
    return max((abs(value) for value in values), default=0)


def clipping(values: list[int]) -> int:
    return sum(1 for value in values if value in (-32768, 32767))


def first_non_silent(samples: list[list[int]], threshold: int) -> int:
    for index, (left, right) in enumerate(zip(samples[0], samples[1])):
        if abs(left) >= threshold or abs(right) >= threshold:
            return index
    return -1


def channel_balance(samples: list[list[int]]) -> float:
    left_rms = rms(samples[0])
    right_rms = rms(samples[1])
    if right_rms == 0:
        return 0.0 if left_rms == 0 else math.inf
    return left_rms / right_rms


def correlation(left: list[float], right: list[float]) -> float:
    count = min(len(left), len(right))
    if count == 0:
        return 0.0
    left = left[:count]
    right = right[:count]
    left_mean = sum(left) / count
    right_mean = sum(right) / count
    numerator = 0.0
    left_energy = 0.0
    right_energy = 0.0
    for a, b in zip(left, right):
        da = a - left_mean
        db = b - right_mean
        numerator += da * db
        left_energy += da * da
        right_energy += db * db
    denominator = math.sqrt(left_energy * right_energy)
    return 0.0 if denominator == 0 else numerator / denominator


def best_shift(reference: list[float], actual: list[float], max_shift: int, stride: int) -> tuple[int, float]:
    ref = reference[::stride]
    act = actual[::stride]
    max_shift_down = max_shift // stride
    best = (0, -2.0)
    for shift in range(-max_shift_down, max_shift_down + 1):
        if shift >= 0:
            ref_window = ref[shift:]
            act_window = act[: len(ref_window)]
        else:
            act_window = act[-shift:]
            ref_window = ref[: len(act_window)]
        count = min(len(ref_window), len(act_window))
        if count < 100:
            continue
        score = correlation(ref_window[:count], act_window[:count])
        if score > best[1]:
            best = (shift * stride, score)
    return best


def aligned_channel(reference: list[int], actual: list[int], shift: int) -> tuple[list[int], list[int]]:
    if shift >= 0:
        ref = reference[shift:]
        act = actual[: len(ref)]
    else:
        act = actual[-shift:]
        ref = reference[: len(act)]
    count = min(len(ref), len(act))
    return ref[:count], act[:count]


def compare(reference: list[list[int]], actual: list[list[int]], shift: int) -> dict[str, float | int]:
    metrics: dict[str, float | int] = {"shiftSamples": shift}
    all_abs_errors = []
    all_sq_errors = []
    channel_correlations = []
    for channel_name, channel in (("left", 0), ("right", 1)):
        ref, act = aligned_channel(reference[channel], actual[channel], shift)
        count = min(len(ref), len(act))
        errors = [a - b for a, b in zip(ref, act)]
        abs_errors = [abs(error) for error in errors]
        sq_errors = [error * error for error in errors]
        all_abs_errors.extend(abs_errors)
        all_sq_errors.extend(sq_errors)
        corr = correlation([float(value) for value in ref], [float(value) for value in act])
        channel_correlations.append(corr)
        metrics[f"{channel_name}SamplesCompared"] = count
        metrics[f"{channel_name}Correlation"] = corr
        metrics[f"{channel_name}Mae"] = sum(abs_errors) / count if count else 0.0
        metrics[f"{channel_name}Rmse"] = math.sqrt(sum(sq_errors) / count) if count else 0.0
        metrics[f"{channel_name}MaxAbsError"] = max(abs_errors, default=0)

    compared = len(all_abs_errors)
    metrics["overallCorrelation"] = sum(channel_correlations) / len(channel_correlations)
    metrics["overallMae"] = sum(all_abs_errors) / compared if compared else 0.0
    metrics["overallRmse"] = math.sqrt(sum(all_sq_errors) / compared) if compared else 0.0
    metrics["overallMaxAbsError"] = max(all_abs_errors, default=0)
    return metrics


def summarize_wav(prefix: str, path: Path, sample_rate: int, samples: list[list[int]]) -> dict[str, float | int | str]:
    frames = len(samples[0])
    first64 = first_non_silent(samples, 64)
    first512 = first_non_silent(samples, 512)
    return {
        f"{prefix}Path": str(path),
        f"{prefix}SampleRate": sample_rate,
        f"{prefix}Frames": frames,
        f"{prefix}DurationSeconds": frames / sample_rate if sample_rate else 0.0,
        f"{prefix}FirstNonSilent64Samples": first64,
        f"{prefix}FirstNonSilent64Seconds": first64 / sample_rate if first64 >= 0 and sample_rate else -1,
        f"{prefix}FirstNonSilent512Samples": first512,
        f"{prefix}FirstNonSilent512Seconds": first512 / sample_rate if first512 >= 0 and sample_rate else -1,
        f"{prefix}LeftRms": rms(samples[0]),
        f"{prefix}RightRms": rms(samples[1]),
        f"{prefix}LeftPeak": peak(samples[0]),
        f"{prefix}RightPeak": peak(samples[1]),
        f"{prefix}LeftClips": clipping(samples[0]),
        f"{prefix}RightClips": clipping(samples[1]),
        f"{prefix}Balance": channel_balance(samples),
    }


def write_csv(path: Path, metrics: dict[str, float | int | str]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=list(metrics.keys()))
        writer.writeheader()
        writer.writerow(metrics)


def write_markdown(path: Path, metrics: dict[str, float | int | str]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    lines = ["# Audio Comparison", "", "| Metric | Value |", "| --- | ---: |"]
    for key, value in metrics.items():
        if isinstance(value, float):
            rendered = f"{value:.6f}"
        else:
            rendered = str(value)
        lines.append(f"| `{key}` | {rendered} |")
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Compare two 16-bit PCM WAV captures with tolerance-friendly audio metrics.")
    parser.add_argument("reference", help="Reference WAV, for example from mGBA or MAME")
    parser.add_argument("actual", help="gbaSharp WAV")
    parser.add_argument("--output-csv", default="", help="Optional one-row CSV metrics output")
    parser.add_argument("--output-md", default="", help="Optional Markdown metrics output")
    parser.add_argument("--max-shift-ms", type=float, default=250.0, help="Maximum alignment search shift in milliseconds")
    parser.add_argument("--stride", type=int, default=16, help="Downsample stride used for alignment search")
    parser.add_argument("--trim-leading-silence", type=int, default=0, help="Trim leading frames below this absolute sample threshold before alignment")
    parser.add_argument("--trim-padding-ms", type=float, default=50.0, help="Padding to keep before the first non-silent sample when trimming")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    reference_path = Path(args.reference).resolve()
    actual_path = Path(args.actual).resolve()
    reference_rate, reference_samples = read_pcm16_wav(reference_path)
    actual_rate, actual_samples = read_pcm16_wav(actual_path)
    if reference_rate != actual_rate:
        raise SystemExit(f"Sample rate mismatch: reference={reference_rate}, actual={actual_rate}")
    if args.stride <= 0:
        raise SystemExit("--stride must be greater than zero")

    trim_padding = int(reference_rate * args.trim_padding_ms / 1000.0)
    reference_samples, reference_trim = trim_leading_silence(reference_samples, args.trim_leading_silence, trim_padding)
    actual_samples, actual_trim = trim_leading_silence(actual_samples, args.trim_leading_silence, trim_padding)
    max_shift = int(reference_rate * args.max_shift_ms / 1000.0)
    shift, alignment_correlation = best_shift(mono(reference_samples), mono(actual_samples), max_shift, args.stride)
    metrics: dict[str, float | int | str] = {}
    metrics.update(summarize_wav("reference", reference_path, reference_rate, reference_samples))
    metrics.update(summarize_wav("actual", actual_path, actual_rate, actual_samples))
    metrics["durationDeltaSeconds"] = float(metrics["actualDurationSeconds"]) - float(metrics["referenceDurationSeconds"])
    metrics["referenceTrimSamples"] = reference_trim
    metrics["actualTrimSamples"] = actual_trim
    metrics["alignmentShiftSamples"] = shift
    metrics["alignmentShiftMs"] = shift * 1000.0 / reference_rate
    metrics["alignmentCorrelation"] = alignment_correlation
    metrics.update(compare(reference_samples, actual_samples, shift))

    if args.output_csv:
        write_csv(Path(args.output_csv).resolve(), metrics)
    if args.output_md:
        write_markdown(Path(args.output_md).resolve(), metrics)

    print(f"Alignment shift: {metrics['alignmentShiftSamples']} samples ({metrics['alignmentShiftMs']:.3f} ms)")
    print(f"Overall correlation: {metrics['overallCorrelation']:.6f}")
    print(f"Overall RMSE: {metrics['overallRmse']:.2f}")
    print(f"Duration delta: {metrics['durationDeltaSeconds']:.6f}s")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
