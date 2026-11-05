from __future__ import annotations

import unittest
from dataclasses import FrozenInstanceError, replace
from datetime import datetime, timezone
from unittest.mock import patch

from amlyrics.apple_lyrics import (
    MAX_LINES, MAX_LINE_CHARS, MAX_METADATA_CHARS, MAX_TOTAL_TEXT_CHARS,
    AppleLyricLine, AppleLyricsObservation, combine_apple_text_with_timing,
    get_apple_timed_result, normalize_apple_text, parse_apple_lyrics_observation,
)
from amlyrics.contracts import LyricLine, LyricsResult, PlaybackState
from amlyrics.synchronizer import LyricSynchronizer

STATE = PlaybackState("Fixture (Live)", "Artist — Fixture - Single", "Edition", 20_000)
# Invented test labels, not transcribed lyrics.
TEXT = ("Echo", "Café", "Echo")


def snapshot(texts=TEXT, **changes):
    value = {
        "version": 1, "status": "observed", "observed_at": "2026-11-05T10:00:00Z",
        "title": STATE.title, "subtitle": STATE.artist,
        "lines": [{"index": index, "text": text} for index, text in enumerate(texts)],
        "timing_status": "not_exposed", "current_index": None,
    }
    value.update(changes)
    return value


def observation(texts=TEXT):
    return parse_apple_lyrics_observation(snapshot(texts), STATE)


def external(texts=TEXT, **changes):
    result = LyricsResult(
        STATE.track_key, "local", "en",
        tuple(LyricLine(1_000 + i * 2_000, 3_000 + i * 2_000, text)
              for i, text in enumerate(texts)), confidence=0.75,
    )
    return replace(result, **changes)


class OfflineTests(unittest.TestCase):
    def setUp(self):
        for target in ("urllib.request.urlopen", "socket.create_connection",
                       "socket.getaddrinfo", "socket.socket.connect",
                       "socket.socket.connect_ex", "subprocess.Popen"):
            guard = patch(target, side_effect=AssertionError("Offline test escaped its fixture"))
            guard.start()
            self.addCleanup(guard.stop)


class AppleObservationTests(OfflineTests):
    def test_bridge_snapshot_is_an_immutable_untimed_observation(self):
        result = observation()
        self.assertIsInstance(result, AppleLyricsObservation)
        self.assertEqual(result.observed_at, datetime(2026, 11, 5, 10, tzinfo=timezone.utc))
        self.assertEqual(tuple(line.text for line in result.lines), TEXT)
        self.assertEqual(tuple(line.index for line in result.lines), (0, 1, 2))
        self.assertEqual(result.timing_status, "not_exposed")
        self.assertIsNone(result.current_index)
        with self.assertRaises(FrozenInstanceError):
            result.title = "Changed"
        with self.assertRaises(FrozenInstanceError):
            result.lines[0].text = "Changed"

    def test_only_word_joiner_and_bom_are_removed(self):
        text = " A\u2060 B\ufeff!\u200c\u200d\u200b\ufe0f "
        self.assertEqual(normalize_apple_text(text), " A B!\u200c\u200d\u200b\ufe0f ")
        value = snapshot(("E\u2060cho\ufeff",))
        self.assertEqual(parse_apple_lyrics_observation(value, STATE).lines[0].text, "Echo")
        self.assertEqual(normalize_apple_text("Cafe\u0301"), "Cafe\u0301")

    def test_metadata_matches_complete_smtc_artist_and_unicode_nfc(self):
        state = replace(STATE, title="Café (Live)")
        value = snapshot(title="Cafe\u0301 (Live)\u2060", subtitle=STATE.artist + "\ufeff")
        self.assertIsNotNone(parse_apple_lyrics_observation(value, state))
        for changes in ({"title": "Fixture"}, {"title": "fixture (live)"},
                        {"title": STATE.title + " "}, {"subtitle": "Artist"},
                        {"subtitle": STATE.artist.casefold()}, {"subtitle": "Other"}):
            with self.subTest(changes=changes):
                self.assertIsNone(parse_apple_lyrics_observation(snapshot(**changes), STATE))

    def test_missing_current_playback_identity_is_not_a_match(self):
        for state in (replace(STATE, title=""), replace(STATE, artist=""),
                      replace(STATE, artist=" "), None):
            self.assertIsNone(parse_apple_lyrics_observation(snapshot(), state))

    def test_unavailable_reason_and_empty_fields_are_preserved(self):
        value = snapshot((), status="unavailable", title="", subtitle="", reason="no_session")
        result = parse_apple_lyrics_observation(value, STATE)
        self.assertEqual(result.status, "unavailable")
        self.assertEqual(result.reason, "no_session")
        self.assertEqual(result.lines, ())
        self.assertIsNone(get_apple_timed_result(result, STATE))
        self.assertIsNone(combine_apple_text_with_timing(result, external(), STATE))
        for field, bad in (("title", STATE.title), ("subtitle", STATE.artist),
                           ("lines", snapshot()["lines"]), ("reason", 1)):
            self.assertIsNone(parse_apple_lyrics_observation({**value, field: bad}, STATE))

    def test_extra_handle_and_geometry_fields_have_no_sync_meaning(self):
        value = snapshot(main_handle=123, uia_handle=456, centered_index=1,
                         selected_index=2, patterns=["Invoke", "ScrollItem"],
                         bounds={"center": 500})
        result = parse_apple_lyrics_observation(value, STATE)
        self.assertIsNotNone(result)
        self.assertIsNone(result.current_index)
        self.assertIsNone(get_apple_timed_result(result, STATE))

    def test_every_required_field_is_required(self):
        for field in snapshot():
            value = snapshot()
            del value[field]
            with self.subTest(field=field):
                self.assertIsNone(parse_apple_lyrics_observation(value, STATE))

    def test_invalid_root_and_schema_types_fail_closed(self):
        for value in (None, [], "json", 1, True):
            self.assertIsNone(parse_apple_lyrics_observation(value, STATE))
        for field, bad in (("version", True), ("version", 2), ("version", "1"),
                           ("status", "partial"), ("status", []), ("title", None),
                           ("subtitle", 1), ("lines", ()), ("lines", None),
                           ("timing_status", "line"), ("timing_status", None),
                           ("current_index", 0), ("current_index", False)):
            with self.subTest(field=field, bad=bad):
                self.assertIsNone(parse_apple_lyrics_observation(snapshot(**{field: bad}), STATE))

    def test_only_explicit_utc_iso_timestamps_are_accepted(self):
        for stamp in ("2026-11-05T10:00:00+00:00", "2026-11-05T10:00:00.123456Z",
                      "2026-11-05T10:00:00.1234567+00:00"):
            self.assertIsNotNone(parse_apple_lyrics_observation(snapshot(observed_at=stamp), STATE))
        for stamp in (None, 42, "2026-11-05", "2026-11-05T10:00:00",
                      "2026-11-05T10:00:00+08:00", "2026-11-05T10:00:00-00:00",
                      "2026-02-30T10:00:00Z", "2026-11-05T10:00:60Z",
                      "2026-11-05T10:00:00.12345678Z", "Z" * 65):
            with self.subTest(stamp=stamp):
                self.assertIsNone(parse_apple_lyrics_observation(snapshot(observed_at=stamp), STATE))

    def test_indices_are_order_only_unique_increasing_and_bounded(self):
        for indices in ((True,), (-1,), (MAX_LINES,), (0, 0), (1, 0), ("0",)):
            lines = [{"index": index, "text": "Echo"} for index in indices]
            with self.subTest(indices=indices):
                self.assertIsNone(parse_apple_lyrics_observation(snapshot(lines=lines), STATE))
        # A gap is preserved; list position is never treated as a current line.
        value = snapshot(lines=[{"index": 2, "text": "Echo"}, {"index": 5, "text": "Café"}])
        self.assertEqual(tuple(line.index for line in
                               parse_apple_lyrics_observation(value, STATE).lines), (2, 5))

    def test_malformed_line_records_and_control_characters_are_rejected(self):
        for line in (None, "Echo", {}, {"index": 0}, {"text": "Echo"},
                     {"index": 0, "text": None}):
            self.assertIsNone(parse_apple_lyrics_observation(snapshot(lines=[line]), STATE))
        for text in ("a\nb", "a\rb", "a\x00b", "\ud800"):
            self.assertIsNone(parse_apple_lyrics_observation(snapshot((text,)), STATE))

    def test_line_count_and_per_line_limits_are_inclusive(self):
        self.assertIsNotNone(observation(("x",) * MAX_LINES))
        self.assertIsNone(observation(("x",) * (MAX_LINES + 1)))
        self.assertIsNotNone(observation(("x" * MAX_LINE_CHARS,)))
        self.assertIsNone(observation(("x" * (MAX_LINE_CHARS + 1),)))
        self.assertIsNone(observation(("\u2060" * (MAX_LINE_CHARS + 1),)))

    def test_total_text_limit_is_inclusive_and_precedes_normalization(self):
        texts = ("x" * MAX_LINE_CHARS,) * (MAX_TOTAL_TEXT_CHARS // MAX_LINE_CHARS)
        self.assertIsNotNone(observation(texts))
        self.assertIsNone(observation(texts + ("x",)))
        invisible = ("\u2060" * MAX_LINE_CHARS,) * (MAX_TOTAL_TEXT_CHARS // MAX_LINE_CHARS)
        self.assertIsNone(observation(invisible + ("\u2060",)))

    def test_metadata_is_bounded_before_normalization(self):
        state = replace(STATE, title="x" * MAX_METADATA_CHARS)
        self.assertIsNotNone(parse_apple_lyrics_observation(snapshot(title=state.title), state))
        self.assertIsNone(parse_apple_lyrics_observation(
            snapshot(title=state.title + "\u2060"), state))
        self.assertIsNone(parse_apple_lyrics_observation(
            snapshot(reason="x" * (MAX_METADATA_CHARS + 1)), STATE))

    def test_manual_dataclass_cannot_claim_current_line_or_timing(self):
        result = observation()
        for changes in ({"current_index": 1}, {"timing_status": "line"},
                        {"observed_at": datetime(2026, 11, 5)},
                        {"lines": (AppleLyricLine(0, "x"), AppleLyricLine(0, "y"))}):
            with self.assertRaises(ValueError):
                replace(result, **changes)

    def test_capture_time_playback_position_and_list_index_never_create_timing(self):
        result = observation()
        for position in (0, 1_000, 10_000, STATE.duration_ms):
            for playing in (True, False):
                self.assertIsNone(get_apple_timed_result(
                    result, replace(STATE, position_ms=position, is_playing=playing)))
        self.assertIsNone(get_apple_timed_result(None, STATE))


    def test_resolver_callback_cannot_promote_observed_plain_text(self):
        from amlyrics.lyrics_sources import LyricsResolver
        observed = observation()
        resolver = LyricsResolver(
            apple_provider=lambda state: get_apple_timed_result(observed, state))
        self.assertIsNone(resolver.lookup(STATE))
        self.assertNotEqual(resolver.last_diagnostic, "apple_music")


class AppleCombinationTests(OfflineTests):
    def test_exact_ordered_match_copies_only_external_timing_with_provenance(self):
        observed = observation()
        timed = external()
        result = combine_apple_text_with_timing(observed, timed, STATE)
        self.assertEqual(result.source, "apple_music+external_timing")
        self.assertEqual(result.track_key, STATE.track_key)
        self.assertEqual(result.language, timed.language)
        self.assertEqual(result.confidence, timed.confidence)
        self.assertEqual(result.lines, timed.lines)
        self.assertEqual([line.start_ms for line in result.lines], [1000, 3000, 5000])
        frame = LyricSynchronizer(result.lines).frame_at(5500)
        self.assertEqual(frame.current_index, 2)
        self.assertEqual(frame.current_line.text, "Echo")
        self.assertIsNone(observed.current_index)
        self.assertEqual(timed.source, "local")

    def test_repeated_lines_are_paired_by_occurrence_not_dictionary(self):
        result = combine_apple_text_with_timing(observation(), external(), STATE)
        echoes = [line.start_ms for line in result.lines if line.text == "Echo"]
        self.assertEqual(echoes, [1000, 5000])
        for texts in (("Echo", "Echo", "Café"), ("Echo", "Café"),
                      ("Echo", "Café", "Echo", "Echo")):
            self.assertIsNone(combine_apple_text_with_timing(observation(), external(texts), STATE))

    def test_nfc_equivalence_preserves_apple_text_representation(self):
        observed = observation(("Cafe\u0301\u2060",))
        result = combine_apple_text_with_timing(observed, external(("Café",)), STATE)
        self.assertEqual(result.lines[0].text, "Cafe\u0301")

    def test_punctuation_spaces_case_compatibility_and_joiners_do_not_fuzzy_match(self):
        for apple_text, timed_text in (("Echo!", "Echo"), ("Echo ", "Echo"),
                ("echo", "Echo"), ("Ａ", "A"), ("a\u200db", "ab"),
                ("a\u200cb", "ab"), ("A  B", "A B"), ("Café", "Cafe")):
            with self.subTest(apple=apple_text, timed=timed_text):
                self.assertIsNone(combine_apple_text_with_timing(
                    observation((apple_text,)), external((timed_text,)), STATE))

    def test_blank_lines_are_omitted_without_shifting_nonblank_pairing(self):
        observed = observation(("", "Echo", " ", "Café", "Echo", "\u2060"))
        timed = external((" ", "Echo", "", "Café", "\ufeff", "Echo"))
        result = combine_apple_text_with_timing(observed, timed, STATE)
        self.assertEqual(tuple(line.text for line in result.lines), TEXT)
        self.assertEqual([line.start_ms for line in result.lines], [3000, 7000, 11000])

    def test_empty_or_whitespace_only_observations_do_not_form_a_timeline(self):
        for texts in ((), ("",), (" ", "\u2060")):
            self.assertIsNone(combine_apple_text_with_timing(observation(texts), external(texts), STATE))
        self.assertIsNone(combine_apple_text_with_timing(None, external(), STATE))
        self.assertIsNone(combine_apple_text_with_timing(observation(), None, STATE))

    def test_track_change_rechecks_title_artist_and_external_album_key(self):
        for state in (replace(STATE, title="Fixture (Studio)"), replace(STATE, artist="Artist"),
                      replace(STATE, album="Other edition")):
            with self.subTest(state=state.track_key):
                self.assertIsNone(combine_apple_text_with_timing(observation(), external(), state))
        for key in (("other", *STATE.track_key[1:]),
                    (STATE.track_key[0], "Artist", STATE.track_key[2]),
                    (*STATE.track_key[:2], "other"), list(STATE.track_key)):
            self.assertIsNone(combine_apple_text_with_timing(
                observation(), external(track_key=key), STATE))

    def test_partial_or_different_version_sequences_fail_closed(self):
        for texts in (TEXT[:2], TEXT[1:], ("Echo", "New verse", "Echo"),
                      ("Echo", "Café (Live)", "Echo")):
            self.assertIsNone(combine_apple_text_with_timing(observation(texts), external(), STATE))

    def test_external_timing_must_be_valid_sorted_integer_and_within_duration(self):
        baseline = external()
        for bad in (LyricLine(-1, 1000, "Echo"), LyricLine(True, 1000, "Echo"),
                    LyricLine(1.5, 1000, "Echo"), LyricLine(1000, False, "Echo"),
                    LyricLine(1000, 999, "Echo"), LyricLine(1000, 20001, "Echo"),
                    LyricLine(20001, None, "Echo"), LyricLine(2**63, None, "Echo")):
            with self.subTest(bad=bad):
                self.assertIsNone(combine_apple_text_with_timing(
                    observation(), replace(baseline, lines=(bad,) + baseline.lines[1:]), STATE))
        reversed_time = replace(baseline, lines=(baseline.lines[0],
                               LyricLine(500, 3000, "Café"), baseline.lines[2]))
        self.assertIsNone(combine_apple_text_with_timing(observation(), reversed_time, STATE))

    def test_invalid_blank_timing_is_not_silently_discarded(self):
        timed = external()
        invalid = replace(timed, lines=(LyricLine(-1, 0, " "),) + timed.lines)
        self.assertIsNone(combine_apple_text_with_timing(observation(), invalid, STATE))

    def test_optional_end_is_preserved_and_no_time_is_fabricated(self):
        timed = external(lines=(LyricLine(1234, None, "Echo"),))
        result = combine_apple_text_with_timing(observation(("Echo",)), timed, STATE)
        self.assertEqual(result.lines, (LyricLine(1234, None, "Echo"),))

    def test_external_precision_confidence_and_shape_are_validated(self):
        for changes in ({"timing_precision": "word"}, {"confidence": True},
                        {"confidence": float("nan")}, {"confidence": float("inf")},
                        {"confidence": -0.1}, {"confidence": 1.1}, {"lines": []},
                        {"lines": ("Echo",)}, {"lines": ()}):
            self.assertIsNone(combine_apple_text_with_timing(
                observation(), external(**changes), STATE))

    def test_external_text_and_collection_limits_are_checked(self):
        for lines in ((LyricLine(0, None, "x" * (MAX_LINE_CHARS + 1)),),
                      (LyricLine(0, None, "Echo"),) * (MAX_LINES + 1),
                      (LyricLine(0, None, "x" * MAX_LINE_CHARS),)
                      * (MAX_TOTAL_TEXT_CHARS // MAX_LINE_CHARS + 1)):
            self.assertIsNone(combine_apple_text_with_timing(
                observation(), external(lines=lines), STATE))


if __name__ == "__main__":
    unittest.main()
