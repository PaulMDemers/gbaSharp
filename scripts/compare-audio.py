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


def remove_mean(values: list[int | float]) -> list[float]:
    if not values:
        return []
    mean = sum(float(value) for value in values) / len(values)
    return [float(value) - mean for value in values]


def optimal_gain(reference: list[int | float], actual: list[int | float]) -> float:
    numerator = 0.0
    denominator = 0.0
    for ref_sample, actual_sample in zip(reference, actual):
        ref_value = float(ref_sample)
        actual_value = float(actual_sample)
        numerator += ref_value * actual_value
        denominator += actual_value * actual_value
    return 0.0 if denominator == 0 else numerator / denominator


def error_metrics(reference: list[int | float], actual: list[int | float], gain: float = 1.0) -> tuple[float, float, float]:
    count = min(len(reference), len(actual))
    if count == 0:
        return 0.0, 0.0, 0.0

    abs_errors = []
    sq_errors = []
    for ref_sample, actual_sample in zip(reference[:count], actual[:count]):
        error = float(ref_sample) - (float(actual_sample) * gain)
        abs_errors.append(abs(error))
        sq_errors.append(error * error)

    return sum(abs_errors) / count, math.sqrt(sum(sq_errors) / count), max(abs_errors, default=0.0)


def correlation_for_shift(reference: list[float], actual: list[float], shift: int, max_samples: int) -> float:
    if shift >= 0:
        ref_start = shift
        act_start = 0
    else:
        ref_start = 0
        act_start = -shift

    count = min(len(reference) - ref_start, len(actual) - act_start)
    if count < 100:
        return -2.0

    if max_samples > 0 and count > max_samples:
        count = max_samples

    left_sum = 0.0
    right_sum = 0.0
    for offset in range(count):
        left_sum += reference[ref_start + offset]
        right_sum += actual[act_start + offset]

    left_mean = left_sum / count
    right_mean = right_sum / count
    numerator = 0.0
    left_energy = 0.0
    right_energy = 0.0
    for offset in range(count):
        da = reference[ref_start + offset] - left_mean
        db = actual[act_start + offset] - right_mean
        numerator += da * db
        left_energy += da * da
        right_energy += db * db

    denominator = math.sqrt(left_energy * right_energy)
    return 0.0 if denominator == 0 else numerator / denominator


def best_shift(reference: list[float], actual: list[float], max_shift: int, stride: int, max_alignment_samples: int) -> tuple[int, float]:
    ref = reference[::stride]
    act = actual[::stride]
    max_shift_down = max_shift // stride
    best = (0, -2.0)
    for shift in range(-max_shift_down, max_shift_down + 1):
        score = correlation_for_shift(ref, act, shift, max_alignment_samples)
        if score > best[1]:
            best = (shift * stride, score)

    refine_radius = max(stride * 2, 1)
    refined = best
    start = max(-max_shift, best[0] - refine_radius)
    end = min(max_shift, best[0] + refine_radius)
    for shift in range(start, end + 1):
        score = correlation_for_shift(reference, actual, shift, max_alignment_samples)
        if score > refined[1]:
            refined = (shift, score)
    return refined


def aligned_channel(reference: list[int], actual: list[int], shift: int) -> tuple[list[int], list[int]]:
    if shift >= 0:
        ref = reference[shift:]
        act = actual[: len(ref)]
    else:
        act = actual[-shift:]
        ref = reference[: len(act)]
    count = min(len(ref), len(act))
    return ref[:count], act[:count]


def compare(reference: list[list[int]], actual: list[list[int]], shift: int, remove_dc: bool) -> dict[str, float | int]:
    metrics: dict[str, float | int] = {"shiftSamples": shift}
    all_abs_errors = []
    all_sq_errors = []
    all_gain_abs_errors = []
    all_gain_sq_errors = []
    channel_correlations = []
    channel_gains = []
    for channel_name, channel in (("left", 0), ("right", 1)):
        ref, act = aligned_channel(reference[channel], actual[channel], shift)
        count = min(len(ref), len(act))
        ref_for_metrics = remove_mean(ref) if remove_dc else [float(value) for value in ref]
        act_for_metrics = remove_mean(act) if remove_dc else [float(value) for value in act]
        gain = optimal_gain(ref_for_metrics, act_for_metrics)
        channel_gains.append(gain)
        errors = [a - b for a, b in zip(ref_for_metrics, act_for_metrics)]
        gain_errors = [a - (b * gain) for a, b in zip(ref_for_metrics, act_for_metrics)]
        abs_errors = [abs(error) for error in errors]
        sq_errors = [error * error for error in errors]
        gain_abs_errors = [abs(error) for error in gain_errors]
        gain_sq_errors = [error * error for error in gain_errors]
        all_abs_errors.extend(abs_errors)
        all_sq_errors.extend(sq_errors)
        all_gain_abs_errors.extend(gain_abs_errors)
        all_gain_sq_errors.extend(gain_sq_errors)
        corr = correlation(ref_for_metrics, act_for_metrics)
        channel_correlations.append(corr)
        metrics[f"{channel_name}SamplesCompared"] = count
        metrics[f"{channel_name}Correlation"] = corr
        metrics[f"{channel_name}ActualToReferenceGain"] = gain
        metrics[f"{channel_name}Mae"] = sum(abs_errors) / count if count else 0.0
        metrics[f"{channel_name}Rmse"] = math.sqrt(sum(sq_errors) / count) if count else 0.0
        metrics[f"{channel_name}MaxAbsError"] = max(abs_errors, default=0)
        metrics[f"{channel_name}GainAdjustedMae"] = sum(gain_abs_errors) / count if count else 0.0
        metrics[f"{channel_name}GainAdjustedRmse"] = math.sqrt(sum(gain_sq_errors) / count) if count else 0.0
        metrics[f"{channel_name}GainAdjustedMaxAbsError"] = max(gain_abs_errors, default=0)

    compared = len(all_abs_errors)
    gain_compared = len(all_gain_abs_errors)
    metrics["overallCorrelation"] = sum(channel_correlations) / len(channel_correlations)
    metrics["averageActualToReferenceGain"] = sum(channel_gains) / len(channel_gains)
    metrics["overallMae"] = sum(all_abs_errors) / compared if compared else 0.0
    metrics["overallRmse"] = math.sqrt(sum(all_sq_errors) / compared) if compared else 0.0
    metrics["overallMaxAbsError"] = max(all_abs_errors, default=0)
    metrics["overallGainAdjustedMae"] = sum(all_gain_abs_errors) / gain_compared if gain_compared else 0.0
    metrics["overallGainAdjustedRmse"] = math.sqrt(sum(all_gain_sq_errors) / gain_compared) if gain_compared else 0.0
    metrics["overallGainAdjustedMaxAbsError"] = max(all_gain_abs_errors, default=0)
    return metrics


def windowed_metrics(
    reference: list[list[int]],
    actual: list[list[int]],
    shift: int,
    sample_rate: int,
    window_samples: int,
    hop_samples: int,
    local_shift_samples: int,
    local_stride: int,
    max_alignment_samples: int,
    remove_dc: bool,
) -> list[dict[str, float | int]]:
    aligned = [aligned_channel(reference[channel], actual[channel], shift) for channel in (0, 1)]
    count = min(len(aligned[0][0]), len(aligned[1][0]))
    if window_samples <= 0 or hop_samples <= 0 or count <= 0:
        return []

    rows: list[dict[str, float | int]] = []
    for start in range(0, count, hop_samples):
        end = min(start + window_samples, count)
        if end <= start:
            break

        channel_correlations = []
        sq_errors = []
        abs_errors = []
        ref_values = []
        act_values = []
        for channel in (0, 1):
            ref = aligned[channel][0][start:end]
            act = aligned[channel][1][start:end]
            ref_for_metrics = remove_mean(ref) if remove_dc else [float(value) for value in ref]
            act_for_metrics = remove_mean(act) if remove_dc else [float(value) for value in act]
            channel_correlations.append(correlation(ref_for_metrics, act_for_metrics))
            for ref_sample, act_sample in zip(ref_for_metrics, act_for_metrics):
                error = ref_sample - act_sample
                abs_errors.append(abs(error))
                sq_errors.append(error * error)
            ref_values.extend(ref)
            act_values.extend(act)

        local_shift = 0
        local_correlation = 0.0
        gain = optimal_gain(ref_values, act_values)
        gain_mae, gain_rmse, gain_max_error = error_metrics(ref_values, act_values, gain)
        if local_shift_samples > 0:
            local_ref = [(left + right) / 2.0 for left, right in zip(aligned[0][0][start:end], aligned[1][0][start:end])]
            local_act = [(left + right) / 2.0 for left, right in zip(aligned[0][1][start:end], aligned[1][1][start:end])]
            if remove_dc:
                local_ref = remove_mean(local_ref)
                local_act = remove_mean(local_act)
            local_shift, local_correlation = best_shift(
                local_ref,
                local_act,
                local_shift_samples,
                local_stride,
                max_alignment_samples,
            )

        compared = len(abs_errors)
        rows.append(
            {
                "startSample": start,
                "endSample": end,
                "startSeconds": start / sample_rate if sample_rate else 0.0,
                "endSeconds": end / sample_rate if sample_rate else 0.0,
                "samplesPerChannel": end - start,
                "overallCorrelation": sum(channel_correlations) / len(channel_correlations),
                "localShiftSamples": local_shift,
                "localShiftMs": local_shift * 1000.0 / sample_rate if sample_rate else 0.0,
                "localAlignmentCorrelation": local_correlation,
                "actualToReferenceGain": gain,
                "overallMae": sum(abs_errors) / compared if compared else 0.0,
                "overallRmse": math.sqrt(sum(sq_errors) / compared) if compared else 0.0,
                "gainAdjustedMae": gain_mae,
                "gainAdjustedRmse": gain_rmse,
                "gainAdjustedMaxAbsError": gain_max_error,
                "referenceRms": rms(ref_values),
                "actualRms": rms(act_values),
                "referencePeak": peak(ref_values),
                "actualPeak": peak(act_values),
            }
        )

        if end == count:
            break

    return rows


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


def write_rows_csv(path: Path, rows: list[dict[str, float | int]]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    fieldnames = list(rows[0].keys()) if rows else [
        "startSample",
        "endSample",
        "startSeconds",
        "endSeconds",
        "samplesPerChannel",
        "overallCorrelation",
        "localShiftSamples",
        "localShiftMs",
        "localAlignmentCorrelation",
        "actualToReferenceGain",
        "overallMae",
        "overallRmse",
        "gainAdjustedMae",
        "gainAdjustedRmse",
        "gainAdjustedMaxAbsError",
        "referenceRms",
        "actualRms",
        "referencePeak",
        "actualPeak",
    ]
    with path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(rows)


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
    parser.add_argument("--window-csv", default="", help="Optional rolling-window CSV metrics output")
    parser.add_argument("--window-ms", type=float, default=1000.0, help="Rolling comparison window size in milliseconds")
    parser.add_argument("--window-hop-ms", type=float, default=500.0, help="Rolling comparison hop size in milliseconds")
    parser.add_argument("--window-local-shift-ms", type=float, default=0.0, help="Optional per-window local alignment search in milliseconds")
    parser.add_argument("--window-local-stride", type=int, default=16, help="Stride for optional per-window local alignment search")
    parser.add_argument("--max-shift-ms", type=float, default=250.0, help="Maximum alignment search shift in milliseconds")
    parser.add_argument("--stride", type=int, default=16, help="Downsample stride used for alignment search")
    parser.add_argument("--max-alignment-samples", type=int, default=12000, help="Maximum samples compared per candidate shift during alignment")
    parser.add_argument("--trim-leading-silence", type=int, default=0, help="Trim leading frames below this absolute sample threshold before alignment")
    parser.add_argument("--trim-padding-ms", type=float, default=50.0, help="Padding to keep before the first non-silent sample when trimming")
    parser.add_argument("--remove-dc", action="store_true", help="Remove per-channel means before correlation/error metrics")
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
    if args.max_alignment_samples < 0:
        raise SystemExit("--max-alignment-samples must be zero or greater")
    if args.window_ms <= 0:
        raise SystemExit("--window-ms must be greater than zero")
    if args.window_hop_ms <= 0:
        raise SystemExit("--window-hop-ms must be greater than zero")
    if args.window_local_shift_ms < 0:
        raise SystemExit("--window-local-shift-ms must be zero or greater")
    if args.window_local_stride <= 0:
        raise SystemExit("--window-local-stride must be greater than zero")

    trim_padding = int(reference_rate * args.trim_padding_ms / 1000.0)
    reference_samples, reference_trim = trim_leading_silence(reference_samples, args.trim_leading_silence, trim_padding)
    actual_samples, actual_trim = trim_leading_silence(actual_samples, args.trim_leading_silence, trim_padding)
    max_shift = int(reference_rate * args.max_shift_ms / 1000.0)
    reference_mono = mono(reference_samples)
    actual_mono = mono(actual_samples)
    if args.remove_dc:
        reference_mono = remove_mean(reference_mono)
        actual_mono = remove_mean(actual_mono)

    shift, alignment_correlation = best_shift(reference_mono, actual_mono, max_shift, args.stride, args.max_alignment_samples)
    metrics: dict[str, float | int | str] = {}
    metrics.update(summarize_wav("reference", reference_path, reference_rate, reference_samples))
    metrics.update(summarize_wav("actual", actual_path, actual_rate, actual_samples))
    metrics["durationDeltaSeconds"] = float(metrics["actualDurationSeconds"]) - float(metrics["referenceDurationSeconds"])
    metrics["referenceTrimSamples"] = reference_trim
    metrics["actualTrimSamples"] = actual_trim
    metrics["alignmentShiftSamples"] = shift
    metrics["alignmentShiftMs"] = shift * 1000.0 / reference_rate
    metrics["alignmentCorrelation"] = alignment_correlation
    metrics["dcRemoved"] = bool(args.remove_dc)
    metrics.update(compare(reference_samples, actual_samples, shift, args.remove_dc))

    if args.output_csv:
        write_csv(Path(args.output_csv).resolve(), metrics)
    if args.output_md:
        write_markdown(Path(args.output_md).resolve(), metrics)
    if args.window_csv:
        window_samples = max(1, int(reference_rate * args.window_ms / 1000.0))
        hop_samples = max(1, int(reference_rate * args.window_hop_ms / 1000.0))
        local_shift_samples = int(reference_rate * args.window_local_shift_ms / 1000.0)
        rows = windowed_metrics(
            reference_samples,
            actual_samples,
            shift,
            reference_rate,
            window_samples,
            hop_samples,
            local_shift_samples,
            args.window_local_stride,
            args.max_alignment_samples,
            args.remove_dc,
        )
        write_rows_csv(Path(args.window_csv).resolve(), rows)

    print(f"Alignment shift: {metrics['alignmentShiftSamples']} samples ({metrics['alignmentShiftMs']:.3f} ms)")
    print(f"Overall correlation: {metrics['overallCorrelation']:.6f}")
    print(f"Overall RMSE: {metrics['overallRmse']:.2f}")
    print(f"Duration delta: {metrics['durationDeltaSeconds']:.6f}s")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
