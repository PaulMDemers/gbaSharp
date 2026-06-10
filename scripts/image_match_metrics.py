from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

from PIL import Image


@dataclass(frozen=True)
class ImageMetrics:
    status: str
    different_pixels: int
    different_percent: float
    max_channel_delta: int
    structural_similarity: float
    coarse_mean_delta: float


def read_rgb(path: Path) -> Image.Image:
    return Image.open(path).convert("RGB")


def structural_similarity(actual: Image.Image, reference: Image.Image) -> float:
    actual_luma = list(actual.convert("L").getdata())
    reference_luma = list(reference.convert("L").getdata())
    count = len(actual_luma)
    if count == 0:
        return 0.0

    mean_a = sum(actual_luma) / count
    mean_b = sum(reference_luma) / count
    variance_a = sum((value - mean_a) ** 2 for value in actual_luma) / count
    variance_b = sum((value - mean_b) ** 2 for value in reference_luma) / count
    covariance = sum((a - mean_a) * (b - mean_b) for a, b in zip(actual_luma, reference_luma)) / count

    c1 = (0.01 * 255) ** 2
    c2 = (0.03 * 255) ** 2
    denominator = (mean_a**2 + mean_b**2 + c1) * (variance_a + variance_b + c2)
    if denominator == 0:
        return 1.0 if mean_a == mean_b else 0.0

    score = ((2 * mean_a * mean_b + c1) * (2 * covariance + c2)) / denominator
    return max(-1.0, min(1.0, score))


def coarse_mean_delta(actual: Image.Image, reference: Image.Image, size: int = 32) -> float:
    actual_small = actual.resize((size, size), Image.Resampling.BILINEAR)
    reference_small = reference.resize((size, size), Image.Resampling.BILINEAR)
    actual_pixels = list(actual_small.getdata())
    reference_pixels = list(reference_small.getdata())
    total = 0
    channels = len(actual_pixels) * 3
    for actual_pixel, reference_pixel in zip(actual_pixels, reference_pixels):
        total += abs(actual_pixel[0] - reference_pixel[0])
        total += abs(actual_pixel[1] - reference_pixel[1])
        total += abs(actual_pixel[2] - reference_pixel[2])
    return total / channels if channels else 255.0


def image_metrics(actual_path: Path, reference: Image.Image) -> ImageMetrics:
    actual = read_rgb(actual_path)
    if actual.size != reference.size:
        return ImageMetrics(
            status="size-mismatch",
            different_pixels=0,
            different_percent=100.0,
            max_channel_delta=255,
            structural_similarity=0.0,
            coarse_mean_delta=255.0,
        )

    width, height = actual.size
    total_pixels = width * height
    actual_pixels = actual.load()
    reference_pixels = reference.load()
    different = 0
    max_delta = 0
    for y in range(height):
        for x in range(width):
            ar, ag, ab = actual_pixels[x, y]
            rr, rg, rb = reference_pixels[x, y]
            delta = max(abs(ar - rr), abs(ag - rg), abs(ab - rb))
            max_delta = max(max_delta, delta)
            if delta:
                different += 1

    return ImageMetrics(
        status="ok",
        different_pixels=different,
        different_percent=(different / total_pixels) * 100.0,
        max_channel_delta=max_delta,
        structural_similarity=structural_similarity(actual, reference),
        coarse_mean_delta=coarse_mean_delta(actual, reference),
    )


def classify_match(
    best: ImageMetrics,
    original: ImageMetrics,
    offset: int,
    *,
    ballpark_pixels: int = 1000,
    ballpark_ssim: float = 0.95,
    ballpark_coarse_delta: float = 8.0,
) -> str:
    if best.different_pixels == 0:
        return "exact"

    strong_timing_improvement = (
        abs(offset) > 0
        and original.different_pixels > 0
        and best.different_pixels < original.different_pixels * 0.25
    )
    structural_timing_improvement = (
        abs(offset) > 0
        and best.structural_similarity >= original.structural_similarity + 0.20
        and best.coarse_mean_delta < original.coarse_mean_delta * 0.50
    )
    if strong_timing_improvement or structural_timing_improvement:
        return "timing-drift"

    if (
        best.different_pixels <= ballpark_pixels
        or (best.structural_similarity >= ballpark_ssim and best.coarse_mean_delta <= ballpark_coarse_delta)
    ):
        return "ballpark-minor-delta"

    if abs(offset) > 0 and original.different_pixels > 0 and best.different_pixels < original.different_pixels * 0.75:
        return "possible-timing-drift"

    if best.structural_similarity >= 0.75 or best.coarse_mean_delta <= 28.0:
        return "same-scene-render-delta"

    return "renderer-or-route-delta"
