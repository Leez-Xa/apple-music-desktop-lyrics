from __future__ import annotations

import copy
import unittest
from dataclasses import replace
from unittest.mock import patch

from amlyrics.apple_external import parse_apple_display_candidates
from amlyrics.contracts import LyricsResult, PlaybackState
from amlyrics.lyrics_sources import parse_lrclib_response
from amlyrics.smtc_bridge import APPLE_MUSIC_SOURCE

STATE = PlaybackState(
    title="裹着心的光", artist="林俊杰 — 裹着心的光 - Single", album="",
    duration_ms=273_000, position_ms=166_000, source_app=APPLE_MUSIC_SOURCE,
)
SYNCED = "[00:01.00]Fixture first line\n[00:03.50]Fixture second line"


def fixture(**changes):
    # Synthetic candidates; no LRCLIB response has been fetched.
    value = dict(id=1, trackName=STATE.title, artistName="林俊杰",
                 albumName="裹着心的光 - Single", duration=273,
                 syncedLyrics=SYNCED, plainLyrics="Fixture plain line")
    value.update(changes)
    return value


class AppleDisplayParserTests(unittest.TestCase):
    def setUp(self):
        # Any unexpected network/process/UI path fails before leaving this test.
        for target in (
            "urllib.request.urlopen", "socket.create_connection", "socket.getaddrinfo",
            "socket.socket.connect", "socket.socket.connect_ex", "subprocess.Popen",
            "amlyrics.smtc_bridge.inspect_sessions", "amlyrics.smtc_bridge.SmtcBridgeMonitor.start",
        ):
            guard = patch(target, side_effect=AssertionError("Offline parser must not perform I/O"))
            guard.start()
            self.addCleanup(guard.stop)

    def test_observed_display_returns_raw_track_key_and_lrclib_source(self):
        result = parse_apple_display_candidates([fixture()], STATE)
        self.assertIsInstance(result, LyricsResult)
        self.assertEqual(result.track_key, STATE.track_key)
        self.assertEqual(result.source, "lrclib")
        self.assertEqual([line.start_ms for line in result.lines], [1000, 3500])
        self.assertEqual(result.lines[-1].end_ms, STATE.duration_ms)

    def test_other_observed_display_uses_candidate_evidence(self):
        state = replace(STATE, title="流沙", artist="陶喆 — 陶喆同名专辑", duration_ms=242_000)
        candidate = fixture(trackName="流沙", artistName="陶喆", albumName="陶喆同名专辑", duration=242)
        result = parse_apple_display_candidates([candidate], state)
        self.assertEqual(result.track_key, state.track_key)

    def test_full_display_title_and_source_normalization(self):
        state = replace(STATE, title="  ＣＡＦＥ\u0301 (LIVE)  ",
                        artist="  ＢＪÖＲＫ — ＮＩＧＨＴ (DELUXE)  ",
                        source_app=APPLE_MUSIC_SOURCE.upper())
        candidate = fixture(trackName="Café (Live)", artistName="Björk", albumName="Night (Deluxe)")
        result = parse_apple_display_candidates([candidate], state)
        self.assertEqual(result.track_key, state.track_key)

    def test_only_trusted_aumid_is_accepted(self):
        for source in ("", "AppleMusic", "AppleMusic.exe", "Spotify.exe",
                       APPLE_MUSIC_SOURCE + ".other", " " + APPLE_MUSIC_SOURCE, None):
            with self.subTest(source=source):
                self.assertIsNone(parse_apple_display_candidates([fixture()], replace(STATE, source_app=source)))

    def test_raw_album_must_be_literally_empty(self):
        for album in ("裹着心的光 - Single", " ", "\t", None):
            with self.subTest(album=album):
                self.assertIsNone(parse_apple_display_candidates([fixture()], replace(STATE, album=album)))

    def test_nonmerged_artist_is_rejected_without_fallback(self):
        for artist in ("林俊杰", "", "林俊杰 - 裹着心的光 - Single",
                       "林俊杰 – 裹着心的光 - Single", "林俊杰—裹着心的光 - Single"):
            with self.subTest(artist=artist):
                self.assertIsNone(parse_apple_display_candidates([fixture()], replace(STATE, artist=artist)))

    def test_candidate_fields_required_and_nonblank(self):
        for field in ("artistName", "albumName"):
            for value in (None, False, 4, [], {}, "", "  ", "\u3000"):
                with self.subTest(field=field, value=value):
                    self.assertIsNone(parse_apple_display_candidates([fixture(**{field: value})], STATE))
            candidate = fixture()
            del candidate[field]
            self.assertIsNone(parse_apple_display_candidates([candidate], STATE))

    def test_requires_decoded_candidate_list(self):
        for payload in (None, fixture(), (fixture(),), "[]", 1, False, []):
            with self.subTest(payload=payload):
                self.assertIsNone(parse_apple_display_candidates(payload, STATE))
        result = parse_apple_display_candidates([None, 1, "bad", [], fixture()], STATE)
        self.assertIsNotNone(result)

    def test_version_labels_and_spelling_are_preserved(self):
        for changes in (
            dict(trackName="裹着心的光 (Live)"), dict(trackName="裹着心的光 (Remastered)"),
            dict(trackName="裹着心的光!"), dict(artistName="林俊傑"),
            dict(artistName="林俊杰 feat. Guest"), dict(albumName="裹着心的光"),
            dict(albumName="裹着心的光 - Single (Deluxe)"),
        ):
            with self.subTest(changes=changes):
                self.assertIsNone(parse_apple_display_candidates([fixture(**changes)], STATE))

    def test_internal_whitespace_is_not_collapsed_or_trimmed_per_field(self):
        for candidate in (fixture(artistName="林俊杰 "), fixture(albumName=" 裹着心的光 - Single")):
            self.assertIsNone(parse_apple_display_candidates([candidate], STATE))
        self.assertIsNone(parse_apple_display_candidates(
            [fixture()], replace(STATE, artist="林俊杰  — 裹着心的光 - Single")))

    def test_candidate_fields_can_contain_separator_without_guessing(self):
        state = replace(STATE, artist="A — B — C")
        for candidate in (fixture(artistName="A — B", albumName="C"),
                          fixture(artistName="A", albumName="B — C")):
            self.assertIsNotNone(parse_apple_display_candidates([candidate], state))

    def test_multiple_valid_partitions_are_ambiguous(self):
        state = replace(STATE, artist="A — B — C")
        candidates = [fixture(artistName="A — B", albumName="C"),
                      fixture(id=2, artistName="A", albumName="B — C")]
        for rows in (candidates, list(reversed(candidates))):
            self.assertIsNone(parse_apple_display_candidates(rows, state))

    def test_multiple_matches_rejected_regardless_of_id_or_order(self):
        for second in (fixture(id=2), fixture(), fixture(id=None), fixture(syncedLyrics="[00:02]Different")):
            with self.subTest(second=second):
                self.assertIsNone(parse_apple_display_candidates([fixture(), second], STATE))
        candidate = fixture()
        self.assertIsNone(parse_apple_display_candidates([candidate, candidate], STATE))

    def test_ambiguity_counts_only_fully_valid_candidates(self):
        failures = [fixture(trackName="Wrong"), fixture(duration=290),
                    fixture(syncedLyrics=None), fixture(albumName="Wrong"),
                    fixture(syncedLyrics="[04:34]Beyond duration")]
        for rows in (failures + [fixture()], [fixture()] + failures):
            self.assertIsNotNone(parse_apple_display_candidates(rows, STATE))
        self.assertIsNone(parse_apple_display_candidates(failures, STATE))

    def test_default_duration_tolerance_is_inclusive(self):
        for duration in (271, 273, 275):
            self.assertIsNotNone(parse_apple_display_candidates([fixture(duration=duration)], STATE))
        for duration in (270.999, 275.001):
            self.assertIsNone(parse_apple_display_candidates([fixture(duration=duration)], STATE))

    def test_custom_duration_tolerance_reaches_shared_parser(self):
        self.assertIsNone(parse_apple_display_candidates([fixture(duration=274)], STATE,
                                                        duration_tolerance_ms=0))
        self.assertIsNotNone(parse_apple_display_candidates([fixture(duration=274)], STATE,
                                                           duration_tolerance_ms=1000))
        self.assertIsNotNone(parse_apple_display_candidates([fixture()], STATE, duration_tolerance_ms=0))

    def test_invalid_tolerance_raises_even_for_empty_candidates(self):
        for tolerance in (-1, True, 1.5, None, "2000"):
            with self.subTest(tolerance=tolerance), self.assertRaises(ValueError):
                parse_apple_display_candidates([], STATE, duration_tolerance_ms=tolerance)

    def test_missing_title_or_duration_rejected(self):
        for changes in (dict(title=""), dict(title=" "), dict(duration_ms=0),
                        dict(duration_ms=-1), dict(duration_ms=True), dict(duration_ms=273000.0)):
            self.assertIsNone(parse_apple_display_candidates([fixture()], replace(STATE, **changes)))

    def test_duration_and_lyrics_validation_match_existing_parser(self):
        canonical = replace(STATE, artist="林俊杰", album="裹着心的光 - Single")
        changes = [dict(duration=value) for value in (
            None, True, "273", 0, -1, float("nan"), float("inf"), 10**400,
        )]
        changes += [dict(syncedLyrics=value) for value in (
            None, "", "Plain only", "[00:01]   ", "[04:34]Too late", 3,
        )]
        for change in changes:
            with self.subTest(change=change):
                candidate = fixture(**change)
                self.assertIsNone(parse_lrclib_response(candidate, canonical))
                self.assertIsNone(parse_apple_display_candidates([candidate], STATE))

    def test_delegates_with_temporary_fields_then_restores_original_key(self):
        candidate = fixture()
        canonical = replace(STATE, artist=candidate["artistName"], album=candidate["albumName"])
        expected = parse_lrclib_response(candidate, canonical, duration_tolerance_ms=0)
        with patch("amlyrics.apple_external.parse_lrclib_response", wraps=parse_lrclib_response) as parser:
            result = parse_apple_display_candidates([candidate], STATE, duration_tolerance_ms=0)
        parser.assert_called_once_with(candidate, canonical, duration_tolerance_ms=0)
        self.assertEqual(result, replace(expected, track_key=STATE.track_key))
        self.assertNotEqual(result.track_key, canonical.track_key)

    def test_inputs_are_unchanged(self):
        payload = [fixture()]
        before = copy.deepcopy(payload)
        state_before = STATE.to_dict()
        parse_apple_display_candidates(payload, STATE)
        self.assertEqual(payload, before)
        self.assertEqual(STATE.to_dict(), state_before)


if __name__ == "__main__":
    unittest.main()
