"""Synthetic block shapes only; no image files, lyrics, windows or Pillow."""
import unittest

from amlyrics.screen_verification import evaluate_screen_pixels


SIZE = (64, 24)
WHITE = (248, 248, 248)
DARK = (24, 24, 24)


def button_pixels():
    """Four invented block glyphs with a gray antialiasing fringe."""
    width, height = SIZE
    ink = set()
    for left in (20, 28, 36, 44):
        for y in range(7, 17):
            for x in range(left, left + 5):
                if x in (left, left + 4) or y in (11, 12):
                    ink.add((x, y))
    pixels = [WHITE] * (width * height)
    for x, y in ink:
        for nx, ny in ((x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1)):
            if 0 <= nx < width and 0 <= ny < height:
                pixels[ny * width + nx] = (170, 170, 170)
    for x, y in ink:
        pixels[y * width + x] = DARK
    return pixels


class ScreenVerificationTests(unittest.TestCase):
    def test_black_and_white_references_never_validate_even_when_identical(self):
        for color in ((0, 0, 0), (255, 255, 255)):
            reference = [color] * (SIZE[0] * SIZE[1])
            for actual in (reference, button_pixels()):
                with self.subTest(color=color, identical=actual is reference):
                    result = evaluate_screen_pixels(reference, actual, SIZE)
                    self.assertFalse(result.valid_reference)
                    self.assertFalse(result.passed)
                    self.assertEqual(result.reason, "reference_insufficient_contrast")

    def test_near_constant_images_and_isolated_outliers_fail_in_both_roles(self):
        count = SIZE[0] * SIZE[1]
        near_gray = [(126 + i % 4,) * 3 for i in range(count)]
        near_white = [(249 + i % 4,) * 3 for i in range(count)]
        outlier = [(251, 251, 251)] * count
        outlier[0] = (0, 0, 0)
        for flat in (near_gray, near_white, outlier):
            with self.subTest(first_pixel=flat[0]):
                reference_result = evaluate_screen_pixels(flat, button_pixels(), SIZE)
                actual_result = evaluate_screen_pixels(button_pixels(), flat, SIZE)
                self.assertFalse(reference_result.valid_reference)
                self.assertFalse(reference_result.passed)
                self.assertTrue(actual_result.valid_reference)
                self.assertFalse(actual_result.passed)

    def test_black_actual_fails_despite_perfect_dark_recall(self):
        actual = [(0, 0, 0)] * (SIZE[0] * SIZE[1])
        result = evaluate_screen_pixels(button_pixels(), actual, SIZE)
        self.assertTrue(result.valid_reference)
        self.assertFalse(result.passed)
        self.assertEqual(result.metrics["dark_recall"], 1.0)
        self.assertEqual(result.metrics["background_recall"], 0.0)
        self.assertEqual(result.metrics["background_precision"], 0.0)

    def test_white_actual_fails_despite_perfect_background_recall(self):
        actual = [(255, 255, 255)] * (SIZE[0] * SIZE[1])
        result = evaluate_screen_pixels(button_pixels(), actual, SIZE)
        self.assertTrue(result.valid_reference)
        self.assertFalse(result.passed)
        self.assertEqual(result.metrics["dark_recall"], 0.0)
        self.assertEqual(result.metrics["background_recall"], 1.0)
        self.assertLess(result.metrics["background_precision"], 1.0)

    def test_blank_gap_with_dark_border_has_signal_but_no_matching_text(self):
        width, height = SIZE
        actual = [
            DARK if x in (0, width - 1) or y in (0, height - 1) else WHITE
            for y in range(height) for x in range(width)
        ]
        result = evaluate_screen_pixels(button_pixels(), actual, SIZE)
        self.assertTrue(result.valid_reference)
        self.assertFalse(result.passed)
        self.assertGreater(result.metrics["actual_dark_pixels"], 21)
        self.assertGreater(result.metrics["actual_luminance_range"], 100)
        self.assertEqual(result.metrics["dark_recall"], 0.0)
        self.assertEqual(result.reason, "dark_recall_below_threshold")

    def test_inverted_text_is_rejected(self):
        expected = button_pixels()
        actual = [tuple(255 - c for c in pixel) for pixel in expected]
        result = evaluate_screen_pixels(expected, actual, SIZE)
        self.assertTrue(result.valid_reference)
        self.assertFalse(result.passed)
        self.assertEqual(result.metrics["dark_recall"], 0.0)
        self.assertEqual(result.metrics["background_recall"], 0.0)
        self.assertEqual(result.metrics["background_precision"], 0.0)

    def test_dark_spill_fails_background_recall_while_retaining_all_text(self):
        expected = button_pixels()
        actual = [DARK if i % SIZE[0] < 16 else pixel for i, pixel in enumerate(expected)]
        result = evaluate_screen_pixels(expected, actual, SIZE)
        self.assertTrue(result.valid_reference)
        self.assertFalse(result.passed)
        self.assertEqual(result.metrics["dark_recall"], 1.0)
        self.assertGreaterEqual(result.metrics["actual_bright_fraction"], .50)
        self.assertEqual(result.metrics["background_precision"], 1.0)
        self.assertEqual(result.reason, "background_recall_below_threshold")

    def test_excess_bright_area_fails_precision_with_both_recalls_perfect(self):
        actual = button_pixels()
        expected = [
            (175, 175, 175) if i % SIZE[0] < 8 else pixel
            for i, pixel in enumerate(actual)
        ]
        result = evaluate_screen_pixels(expected, actual, SIZE)
        self.assertTrue(result.valid_reference)
        self.assertFalse(result.passed)
        self.assertEqual(result.metrics["dark_recall"], 1.0)
        self.assertEqual(result.metrics["background_recall"], 1.0)
        self.assertEqual(result.reason, "background_precision_below_threshold")

    def test_realistic_color_and_antialiasing_changes_pass_without_mutation(self):
        expected = button_pixels()
        original = expected[:]
        changed = [
            (42, 36, 32) if pixel == DARK else
            (182, 178, 180) if pixel == (170, 170, 170) else (241, 245, 243)
            for pixel in expected
        ]
        softened = changed[:]
        dark_indices = [i for i, pixel in enumerate(expected) if pixel == DARK]
        for i in dark_indices[::10]:
            softened[i] = (160, 160, 160)
        for actual in (expected[:], changed, softened):
            with self.subTest(softened=actual is softened):
                before = actual[:]
                result = evaluate_screen_pixels(expected, actual, SIZE, actual_size=SIZE)
                self.assertTrue(result.valid_reference)
                self.assertTrue(result.passed, result)
                self.assertEqual(result.reason, "matched")
                self.assertGreaterEqual(result.metrics["dark_recall"], .70)
                self.assertEqual(actual, before)
                self.assertEqual(expected, original)
        self.assertLess(evaluate_screen_pixels(expected, softened, SIZE).metrics["dark_recall"], 1)

    def test_malformed_pixels_and_dimensions_fail_closed(self):
        valid = button_pixels()
        cases = [
            (valid, valid, (0, 24), None, False, "invalid_reference_size"),
            (valid, valid, (True, 24), None, False, "invalid_reference_size"),
            ([], valid, SIZE, None, False, "invalid_reference_pixels"),
            (None, valid, SIZE, None, False, "invalid_reference_pixels"),
            (valid, valid[:-1], SIZE, None, True, "invalid_actual_pixels"),
            (valid, valid, SIZE, (32, 48), True, "capture_size_mismatch"),
            (valid, valid, SIZE, (-1, 24), True, "invalid_actual_size"),
        ]
        for bad_pixel in ((0, 0), (256, 0, 0), (-1, 0, 0), (float("nan"), 0, 0), (True, 0, 0)):
            bad = valid[:]
            bad[0] = bad_pixel
            cases.append((bad, valid, SIZE, None, False, "invalid_reference_pixels"))
            cases.append((valid, bad, SIZE, None, True, "invalid_actual_pixels"))
        for expected, actual, size, actual_size, valid_reference, reason in cases:
            with self.subTest(reason=reason, size=size, actual_size=actual_size):
                result = evaluate_screen_pixels(expected, actual, size, actual_size=actual_size)
                self.assertEqual(result.valid_reference, valid_reference)
                self.assertFalse(result.passed)
                self.assertEqual(result.reason, reason)


if __name__ == "__main__":
    unittest.main()
