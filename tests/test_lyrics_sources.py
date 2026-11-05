from __future__ import annotations

import io
import json
import socket
import unittest
from dataclasses import replace
from unittest.mock import Mock, patch
from urllib.error import HTTPError, URLError
from urllib.parse import parse_qs, urlsplit

from amlyrics.contracts import LyricLine, LyricsResult, PlaybackState
from amlyrics.lyrics_sources import LyricsResolver, parse_lrclib_response, track_identity
from amlyrics.playback import MockPlaybackMonitor
from amlyrics.service import LyricsService

STATE = PlaybackState("Café (Live)", "Björk", "Night Live", 180_000)
SYNCED = "[00:01.00]First line\n[00:03.50]Second line"


def fixture_payload(**changes):
    payload = {
        "trackName": STATE.title, "artistName": STATE.artist,
        "albumName": STATE.album, "duration": 180,
        "syncedLyrics": SYNCED, "plainLyrics": "First line\nSecond line",
    }
    payload.update(changes)
    return payload


def fixture_response(**changes):
    return io.BytesIO(json.dumps(fixture_payload(**changes), ensure_ascii=False).encode("utf-8"))


def apple_result(state=STATE):
    return LyricsResult(state.track_key, "callback", None,
                        (LyricLine(1_000, state.duration_ms, "Apple fixture"),))


class OfflineTestCase(unittest.TestCase):
    """Fail closed if any source test escapes its in-memory HTTP fixture."""

    def setUp(self):
        for target in (
            "urllib.request.urlopen", "socket.create_connection", "socket.getaddrinfo",
            "socket.socket.connect", "socket.socket.connect_ex",
        ):
            guard = patch(target, side_effect=AssertionError("Network forbidden in offline tests"))
            guard.start()
            self.addCleanup(guard.stop)


class ResponseParserTests(OfflineTestCase):
    def test_accepts_synced_lyrics_with_existing_contract(self):
        result = parse_lrclib_response(fixture_payload(), STATE)
        self.assertIsInstance(result, LyricsResult)
        self.assertEqual(result.source, "lrclib")
        self.assertEqual(result.track_key, STATE.track_key)
        self.assertEqual([line.start_ms for line in result.lines], [1_000, 3_500])
        self.assertEqual(result.lines[-1].end_ms, STATE.duration_ms)

    def test_unicode_normalization_is_exact_after_nfkc_casefold_strip(self):
        result = parse_lrclib_response(fixture_payload(
            trackName="  CAFE\u0301 (LIVE)  ", artistName="Ｂｊöｒｋ", albumName="NIGHT LIVE"), STATE)
        self.assertIsNotNone(result)

    def test_version_artist_album_and_punctuation_are_preserved(self):
        for field, value in (
            ("trackName", "Café"), ("trackName", "Café (Remaster)"),
            ("trackName", "Café (Live 2024)"), ("trackName", "Café - Live"),
            ("artistName", "Björk feat. Guest"), ("artistName", "Bjork"),
            ("albumName", "Night Live (Deluxe)"), ("albumName", None),
        ):
            with self.subTest(field=field, value=value):
                self.assertIsNone(parse_lrclib_response(fixture_payload(**{field: value}), STATE))

    def test_album_optional_only_when_playback_album_unknown(self):
        self.assertIsNotNone(parse_lrclib_response(
            fixture_payload(albumName=None), replace(STATE, album="")))

    def test_duration_tolerance_is_inclusive_two_seconds(self):
        for seconds in (178, 182):
            self.assertIsNotNone(parse_lrclib_response(fixture_payload(duration=seconds), STATE))
        for seconds in (177.999, 182.001):
            self.assertIsNone(parse_lrclib_response(fixture_payload(duration=seconds), STATE))

    def test_custom_duration_tolerance_including_zero(self):
        self.assertIsNotNone(parse_lrclib_response(fixture_payload(), STATE, duration_tolerance_ms=0))
        self.assertIsNone(parse_lrclib_response(
            fixture_payload(duration=180.001), STATE, duration_tolerance_ms=0))

    def test_invalid_or_unknown_duration_and_missing_identity_rejected(self):
        for value in (None, "180", True, 0, -1, float("nan"), float("inf"), 10 ** 1000):
            self.assertIsNone(parse_lrclib_response(fixture_payload(duration=value), STATE))
        for state in (replace(STATE, duration_ms=0), replace(STATE, duration_ms=-1),
                      replace(STATE, title=""), replace(STATE, artist=" ")):
            self.assertIsNone(parse_lrclib_response(fixture_payload(), state))

    def test_plain_lyrics_never_create_timeline(self):
        for synced in (None, "", "First line\nSecond line", "[ar:Artist]", 123, [], "[00:01]   "):
            with self.subTest(synced=synced):
                self.assertIsNone(parse_lrclib_response(fixture_payload(syncedLyrics=synced), STATE))
        payload = fixture_payload()
        del payload["syncedLyrics"]
        self.assertIsNone(parse_lrclib_response(payload, STATE))

    def test_invalid_payload_shapes_and_types_return_none(self):
        for payload in (None, [], [fixture_payload()], "not JSON", 42,
                        fixture_payload(trackName=None), fixture_payload(artistName=False)):
            self.assertIsNone(parse_lrclib_response(payload, STATE))

    def test_timeline_cannot_extend_past_playback_duration(self):
        self.assertIsNone(parse_lrclib_response(fixture_payload(syncedLyrics="[03:01]Too late"), STATE))

    def test_invalid_tolerance_fails_without_io(self):
        for tolerance in (-1, True, 1.2):
            with self.assertRaises(ValueError):
                parse_lrclib_response(fixture_payload(), STATE, duration_tolerance_ms=tolerance)


class ResolverPolicyTests(OfflineTestCase):
    def test_construction_and_default_lookup_are_offline(self):
        for enabled in (False, True):
            resolver = LyricsResolver(enable_external=enabled)
            self.assertIsNone(resolver.last_diagnostic)
            self.assertIsNone(resolver.lookup(STATE))
            self.assertEqual(resolver.last_diagnostic, "disabled")

    def test_both_explicit_switches_required_on_every_call(self):
        with patch("urllib.request.urlopen", return_value=fixture_response()) as opener:
            self.assertIsNone(LyricsResolver().lookup(STATE, allow_network=True))
            resolver = LyricsResolver(enable_external=True)
            for permission in (False, None, 1, "true"):
                self.assertIsNone(resolver.lookup(STATE, allow_network=permission))
                self.assertEqual(resolver.last_diagnostic, "disabled")
            opener.assert_not_called()
            self.assertIsNotNone(resolver.lookup(STATE, allow_network=True))
            opener.assert_called_once()

    def test_apple_callback_wins_over_local_and_external(self):
        provider = Mock(return_value=apple_result())
        resolver = LyricsResolver(provider, {track_identity(STATE): SYNCED}, enable_external=True)
        with patch("urllib.request.urlopen") as opener:
            result = resolver.lookup(STATE, allow_network=True)
            opener.assert_not_called()
        provider.assert_called_once_with(STATE)
        self.assertEqual(result.source, "apple_music")
        self.assertEqual(result.lines[0].text, "Apple fixture")

    def test_local_mapping_works_without_network_permission(self):
        resolver = LyricsResolver(local_mapping={track_identity(STATE): SYNCED})
        self.assertEqual(resolver.lookup(STATE).source, "local")
        self.assertEqual(resolver.last_diagnostic, "local")

    def test_apple_none_error_wrong_track_and_invalid_lines_fall_back(self):
        for provider in (
            Mock(return_value=None), Mock(side_effect=RuntimeError("account-secret")),
            Mock(return_value=replace(apple_result(), track_key=("other", "artist", "album"))),
            Mock(return_value=replace(apple_result(), lines=())),
            Mock(return_value=replace(apple_result(), lines=(LyricLine(-1, None, "bad"),))),
            Mock(return_value=replace(apple_result(), lines=(LyricLine(2, 1, "bad"),))),
            Mock(return_value="untimed text"),
        ):
            resolver = LyricsResolver(provider, {track_identity(STATE): SYNCED})
            self.assertEqual(resolver.lookup(STATE).source, "local")
            self.assertNotIn("secret", resolver.last_diagnostic)

    def test_local_plain_text_ignored_before_external(self):
        resolver = LyricsResolver(local_mapping={track_identity(STATE): "plain"}, enable_external=True)
        with patch("urllib.request.urlopen", return_value=fixture_response()):
            self.assertEqual(resolver.lookup(STATE, allow_network=True).source, "lrclib")

    def test_local_keys_separate_album_duration_artist_and_versions(self):
        resolver = LyricsResolver(local_mapping={track_identity(STATE): SYNCED})
        for state in (replace(STATE, title="Café"), replace(STATE, artist="Guest"),
                      replace(STATE, album="Night Live (Deluxe)"), replace(STATE, duration_ms=181_000)):
            self.assertIsNone(resolver.lookup(state))
            self.assertNotEqual(track_identity(STATE), track_identity(state))
        self.assertEqual(track_identity(STATE), track_identity(replace(STATE, title="CAFE\u0301 (LIVE)")))
        self.assertEqual(track_identity(STATE), track_identity(replace(STATE, position_ms=3_000)))

    def test_service_accepts_bound_lookup_without_contract_changes(self):
        resolver = LyricsResolver(local_mapping={track_identity(STATE): SYNCED})
        monitor = MockPlaybackMonitor(STATE)
        service = LyricsService(monitor, resolver.lookup)
        monitor.set_state(STATE)
        self.assertEqual(service.result.source, "local")

    def test_missing_external_metadata_never_makes_requests(self):
        resolver = LyricsResolver(enable_external=True)
        for state in (replace(STATE, title=""), replace(STATE, artist=""),
                      replace(STATE, duration_ms=0), replace(STATE, duration_ms=True)):
            self.assertIsNone(resolver.lookup(state, allow_network=True))
            self.assertEqual(resolver.last_diagnostic, "missing_metadata")

    def test_bad_configuration_fails_before_io(self):
        for config in (
            {"enable_external": "yes"}, {"duration_tolerance_ms": -1},
            {"timeout_seconds": 0}, {"timeout_seconds": float("inf")},
            {"positive_cache_ttl_seconds": -1}, {"negative_cache_ttl_seconds": float("nan")},
            {"max_cache_entries": 0}, {"max_cache_entries": True},
        ):
            with self.subTest(config=config), self.assertRaises(ValueError):
                LyricsResolver(**config)


class ExternalFixtureTests(OfflineTestCase):
    def test_urllib_get_encodes_metadata_and_sets_timeout(self):
        resolver = LyricsResolver(enable_external=True, timeout_seconds=1.25)
        response = fixture_response()
        with patch("urllib.request.urlopen", return_value=response) as opener:
            self.assertIsNotNone(resolver.lookup(STATE, allow_network=True))
        request = opener.call_args.args[0]
        url = urlsplit(request.full_url)
        self.assertEqual((url.scheme, url.netloc, url.path), ("https", "lrclib.net", "/api/get"))
        self.assertEqual(parse_qs(url.query), {
            "track_name": [STATE.title], "artist_name": [STATE.artist],
            "album_name": [STATE.album], "duration": ["180.0"],
        })
        self.assertEqual(request.get_method(), "GET")
        self.assertIsNone(request.data)
        self.assertNotIn("Authorization", request.headers)
        self.assertEqual(opener.call_args.kwargs["timeout"], 1.25)
        self.assertTrue(response.closed)

    def test_timeout_http_and_network_failures_return_none(self):
        for error, diagnostic in (
            (TimeoutError("account-secret"), "timeout"),
            (socket.timeout("account-secret"), "timeout"),
            (URLError(TimeoutError("account-secret")), "timeout"),
            (HTTPError("https://fixture.invalid", 404, "secret", {}, None), "not_found"),
            (HTTPError("https://fixture.invalid", 429, "secret", {}, None), "rate_limited"),
            (HTTPError("https://fixture.invalid", 503, "secret", {}, None), "http_error"),
            (URLError("account-secret"), "network_error"), (OSError("account-secret"), "network_error"),
        ):
            resolver = LyricsResolver(enable_external=True)
            with patch("urllib.request.urlopen", side_effect=error):
                self.assertIsNone(resolver.lookup(STATE, allow_network=True))
            self.assertEqual(resolver.last_diagnostic, diagnostic)
            self.assertNotIn("secret", resolver.last_diagnostic)

    def test_invalid_json_utf8_and_oversized_responses_return_none(self):
        for raw in (b"{", b"\xff", b"x" * 1_048_577):
            resolver = LyricsResolver(enable_external=True)
            with patch("urllib.request.urlopen", return_value=io.BytesIO(raw)):
                self.assertIsNone(resolver.lookup(STATE, allow_network=True))
            self.assertEqual(resolver.last_diagnostic, "invalid_response")

    def test_no_synced_lyrics_or_exact_match_returns_none(self):
        for changes in ({"syncedLyrics": None}, {"trackName": "Café"}, {"duration": 240}):
            resolver = LyricsResolver(enable_external=True)
            with patch("urllib.request.urlopen", return_value=fixture_response(**changes)):
                self.assertIsNone(resolver.lookup(STATE, allow_network=True))
            self.assertEqual(resolver.last_diagnostic, "no_synced_match")

    def test_positive_cache_expires_and_offline_calls_remain_disabled(self):
        clock = Mock(return_value=10.0)
        resolver = LyricsResolver(enable_external=True, clock=clock, positive_cache_ttl_seconds=5)
        with patch("urllib.request.urlopen", side_effect=[fixture_response(), fixture_response()]) as opener:
            self.assertIsNotNone(resolver.lookup(STATE, allow_network=True))
            self.assertIsNone(resolver.lookup(STATE))
            self.assertEqual(resolver.last_diagnostic, "disabled")
            self.assertIsNotNone(resolver.lookup(STATE, allow_network=True))
            clock.return_value = 15.0
            self.assertIsNotNone(resolver.lookup(STATE, allow_network=True))
            self.assertEqual(opener.call_count, 2)

    def test_normalized_cache_hit_rebinds_contract_track_key(self):
        resolver = LyricsResolver(enable_external=True)
        alternate = replace(STATE, title="CAFE\u0301 (LIVE)")
        with patch("urllib.request.urlopen", return_value=fixture_response()) as opener:
            resolver.lookup(STATE, allow_network=True)
            result = resolver.lookup(alternate, allow_network=True)
            self.assertEqual(result.track_key, alternate.track_key)
            opener.assert_called_once()

    def test_negative_cache_for_timeout_404_429_and_no_sync_expires(self):
        for failure, diagnostic in (
            (TimeoutError(), "timeout"),
            (HTTPError("https://fixture.invalid", 404, "missing", {}, None), "not_found"),
            (HTTPError("https://fixture.invalid", 429, "limited", {}, None), "rate_limited"),
            (fixture_response(syncedLyrics=None), "no_synced_match"),
        ):
            clock = Mock(return_value=0.0)
            resolver = LyricsResolver(enable_external=True, clock=clock, negative_cache_ttl_seconds=3)
            with patch("urllib.request.urlopen", side_effect=[failure, fixture_response()]) as opener:
                self.assertIsNone(resolver.lookup(STATE, allow_network=True))
                self.assertIsNone(resolver.lookup(STATE, allow_network=True))
                self.assertEqual(resolver.last_diagnostic, diagnostic)
                self.assertEqual(opener.call_count, 1)
                clock.return_value = 3.0
                self.assertIsNotNone(resolver.lookup(STATE, allow_network=True))
                self.assertEqual(opener.call_count, 2)

    def test_positive_and_negative_cache_separate_version_fields(self):
        variants = [STATE, replace(STATE, title="Café"), replace(STATE, artist="Guest"),
                    replace(STATE, album="Night Live (Deluxe)"), replace(STATE, duration_ms=181_000)]
        for positive in (False, True):
            resolver = LyricsResolver(enable_external=True)
            responses = [io.BytesIO(json.dumps(fixture_payload(
                trackName=state.title, artistName=state.artist, albumName=state.album,
                duration=state.duration_ms / 1_000, syncedLyrics=SYNCED if positive else None,
            )).encode("utf-8")) for state in variants]
            with patch("urllib.request.urlopen", side_effect=responses) as opener:
                for state in variants + variants:
                    self.assertEqual(resolver.lookup(state, allow_network=True) is not None, positive)
                self.assertEqual(opener.call_count, len(variants))

    def test_external_caches_never_mask_new_apple_or_local_lyrics(self):
        for synced in (SYNCED, None):
            mapping = {}
            provider = Mock(return_value=None)
            resolver = LyricsResolver(provider, mapping, enable_external=True)
            with patch("urllib.request.urlopen", return_value=fixture_response(syncedLyrics=synced)) as opener:
                resolver.lookup(STATE, allow_network=True)
                mapping[track_identity(STATE)] = SYNCED
                self.assertEqual(resolver.lookup(STATE, allow_network=True).source, "local")
                provider.return_value = apple_result()
                self.assertEqual(resolver.lookup(STATE, allow_network=True).source, "apple_music")
                opener.assert_called_once()

    def test_cache_is_bounded_and_zero_ttl_disables_storage(self):
        resolver = LyricsResolver(enable_external=True, max_cache_entries=1)
        other = replace(STATE, duration_ms=181_000)
        with patch("urllib.request.urlopen", side_effect=[fixture_response(), fixture_response(), fixture_response()]) as opener:
            for state in (STATE, other, STATE):
                self.assertIsNotNone(resolver.lookup(state, allow_network=True))
            self.assertEqual(opener.call_count, 3)
        for synced in (SYNCED, None):
            resolver = LyricsResolver(enable_external=True, positive_cache_ttl_seconds=0, negative_cache_ttl_seconds=0)
            with patch("urllib.request.urlopen", side_effect=[fixture_response(syncedLyrics=synced),
                                                               fixture_response(syncedLyrics=synced)]) as opener:
                resolver.lookup(STATE, allow_network=True)
                resolver.lookup(STATE, allow_network=True)
                self.assertEqual(opener.call_count, 2)


if __name__ == "__main__":
    unittest.main()
