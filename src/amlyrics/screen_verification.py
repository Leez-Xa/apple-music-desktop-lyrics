"""Pure visibility checks for aligned, row-major RGB captures, without Pillow.

The caller supplies flat RGB sequences and a (width, height), for example
``evaluate_screen_pixels(list(expected.getdata()), list(actual.getdata()),
expected.size, actual_size=actual.size)``. Capture, PID hit testing, foreground
protection and evidence persistence remain the caller's responsibility.

This checks dark text on a light background, not text semantics. Both images
need at least 21 dark pixels (also >= 1% of the area), >= 50% bright background,
and a luminance range >= 100. Reference dark/bright cutoffs are 128/210;
actual cutoffs are relaxed to 150/195 for ordinary antialiasing/color changes.
At aligned coordinates, dark recall must be >= .70, background recall >= .90,
and background precision >= .95. No exact RGB equality is required. Translation,
resizing, and dark themes must be handled by the caller, not inferred here.
"""
from __future__ import annotations

from collections.abc import Sequence
from dataclasses import dataclass

RGB = tuple[int, int, int]
Size = tuple[int, int]


@dataclass(frozen=True, slots=True)
class ScreenVerificationResult:
    valid_reference: bool
    passed: bool
    reason: str
    metrics: dict[str, int | float]


def _valid_size(size: object) -> bool:
    return (
        isinstance(size, (list, tuple))
        and len(size) == 2
        and all(type(value) is int and value > 0 for value in size)
    )


def _luminances(pixels: Sequence[RGB], count: int) -> list[float] | None:
    if not isinstance(pixels, Sequence) or len(pixels) != count:
        return None
    values = []
    for pixel in pixels:
        if (
            not isinstance(pixel, (list, tuple))
            or len(pixel) != 3
            or any(type(c) is not int or not 0 <= c <= 255 for c in pixel)
        ):
            return None
        r, g, b = pixel
        values.append((299 * r + 587 * g + 114 * b) / 1000)
    return values


def _profile(values: list[float], dark: int, bright: int) -> dict[str, int | float]:
    dark_count = sum(value <= dark for value in values)
    bright_count = sum(value >= bright for value in values)
    return {
        "luminance_range": max(values) - min(values),
        "dark_pixels": dark_count,
        "bright_pixels": bright_count,
        "dark_fraction": dark_count / len(values),
        "bright_fraction": bright_count / len(values),
    }


def _signal_failure(profile: dict[str, int | float]) -> str | None:
    if profile["luminance_range"] < 100:
        return "insufficient_contrast"
    if profile["dark_pixels"] < 21 or profile["dark_fraction"] < .01:
        return "insufficient_dark_signal"
    if profile["bright_pixels"] < 21 or profile["bright_fraction"] < .50:
        return "insufficient_bright_background"
    return None


def evaluate_screen_pixels(
    expected: Sequence[RGB],
    actual: Sequence[RGB],
    size: Size,
    *,
    actual_size: Size | None = None,
) -> ScreenVerificationResult:
    """Return an explainable decision without I/O or input mutation.

    Invalid dimensions, RGB channels or pixel counts fail closed in the result.
    ``actual_size`` defaults to ``size``; pass it explicitly for image captures
    so equally sized buffers with different shapes cannot pass. Metrics use the
    ``expected_``/``actual_`` prefixes for signal profiles. Overlap metrics are
    computed even for a degenerate actual image; undefined ratios are zero.
    Reasons are stable machine-readable codes. A valid reference does not imply
    a passing actual capture, or validate the identity/ownership of any window.
    """
    metrics: dict[str, int | float] = {}
    if not _valid_size(size):
        return ScreenVerificationResult(False, False, "invalid_reference_size", metrics)
    width, height = size
    count = width * height
    metrics.update(width=width, height=height, pixel_count=count)
    reference = _luminances(expected, count)
    if reference is None:
        return ScreenVerificationResult(False, False, "invalid_reference_pixels", metrics)
    expected_profile = _profile(reference, 128, 210)
    metrics.update(("expected_" + key, value) for key, value in expected_profile.items())
    failure = _signal_failure(expected_profile)
    if failure:
        return ScreenVerificationResult(False, False, "reference_" + failure, metrics)

    if actual_size is None:
        actual_size = size
    if not _valid_size(actual_size):
        return ScreenVerificationResult(True, False, "invalid_actual_size", metrics)
    if tuple(actual_size) != tuple(size):
        return ScreenVerificationResult(True, False, "capture_size_mismatch", metrics)
    captured = _luminances(actual, count)
    if captured is None:
        return ScreenVerificationResult(True, False, "invalid_actual_pixels", metrics)
    actual_profile = _profile(captured, 150, 195)
    metrics.update(("actual_" + key, value) for key, value in actual_profile.items())
    dark_hits = sum(e <= 128 and a <= 150 for e, a in zip(reference, captured))
    background_hits = sum(e >= 210 and a >= 195 for e, a in zip(reference, captured))
    metrics.update(
        matched_dark_pixels=dark_hits,
        matched_background_pixels=background_hits,
        dark_recall=dark_hits / expected_profile["dark_pixels"],
        background_recall=background_hits / expected_profile["bright_pixels"],
        background_precision=(
            background_hits / actual_profile["bright_pixels"]
            if actual_profile["bright_pixels"] else 0.0
        ),
    )
    failure = _signal_failure(actual_profile)
    if failure:
        return ScreenVerificationResult(True, False, "actual_" + failure, metrics)
    for name, minimum in (
        ("dark_recall", .70),
        ("background_recall", .90),
        ("background_precision", .95),
    ):
        if metrics[name] < minimum:
            return ScreenVerificationResult(True, False, name + "_below_threshold", metrics)
    return ScreenVerificationResult(True, True, "matched", metrics)
