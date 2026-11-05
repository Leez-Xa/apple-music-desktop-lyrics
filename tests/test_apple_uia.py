from __future__ import annotations
import copy
import unittest
from amlyrics.apple_uia import VERIFIED_FILE_VERSION, VERIFIED_PACKAGE_DIRECTORY, parse_transport_snapshot


def fixture():
    return {"version": 1, "status": "observed", "mode": "read_only",
            "file_version": VERIFIED_FILE_VERSION, "package_directory": VERIFIED_PACKAGE_DIRECTORY,
            "transport_framework": "XAML", "minimized": True,
            "controls": [
                {"automation_id": "TransportControl_PlayPauseStop", "framework_id": "XAML", "name": "播放"},
                {"automation_id": "LyricsToggleButton", "framework_id": "XAML", "toggle": "Off"},
                {"automation_id": "LCDScrubber", "framework_id": "XAML",
                 "range": {"value": 166, "minimum": 0, "maximum": 273}}]}


class AppleUiaTests(unittest.TestCase):
    def test_observed_minimized_transport_keeps_raw_units_and_no_lyrics(self):
        result = parse_transport_snapshot(fixture())
        self.assertTrue(result.minimized)
        self.assertEqual(result.play_pause_name, "播放")
        self.assertEqual(result.raw_position, 166)
        self.assertEqual(result.lyrics_toggle, "Off")
        self.assertEqual(result.lyrics_timeline, "not_observed")
    def test_unknown_version_cannot_enable_adapter(self):
        value = fixture()
        value["file_version"] = "99.0.0.0"
        with self.assertRaises(ValueError): parse_transport_snapshot(value)
    def test_missing_transport_does_not_match_other_controls(self):
        value = fixture()
        value["controls"] = value["controls"][1:]
        with self.assertRaises(ValueError): parse_transport_snapshot(value)
    def test_wrong_framework_and_duplicate_ids_rejected(self):
        value = fixture()
        value["controls"][0]["framework_id"] = "Chrome"
        with self.assertRaises(ValueError): parse_transport_snapshot(value)
        value = fixture()
        value["controls"].append(copy.deepcopy(value["controls"][0]))
        with self.assertRaises(ValueError): parse_transport_snapshot(value)
    def test_invalid_slider_does_not_become_playback_time(self):
        for number in (float("nan"), float("inf"), True, -1, 500):
            with self.subTest(number=number):
                value = fixture()
                value["controls"][2]["range"]["value"] = number
                with self.assertRaises(ValueError): parse_transport_snapshot(value)
    def test_unavailable_is_explicit(self):
        result = parse_transport_snapshot({"version": 1, "status": "unavailable", "reason": "no_window"})
        self.assertEqual(result.reason, "no_window")
        self.assertIsNone(result.raw_position)

if __name__ == "__main__": unittest.main()
