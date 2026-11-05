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
from amlyrics.lrclib_search import AppleDisplayLyricsResolver
from amlyrics.lyrics_sources import LyricsResolver, track_identity
from amlyrics.smtc_bridge import APPLE_MUSIC_SOURCE

STATE = PlaybackState("Café (Live) & 光?", "Björk — Night (Deluxe)", "", 180_000,
                      source_app=APPLE_MUSIC_SOURCE)
SYNCED = "[00:01]Fixture one\n[00:03.50]Fixture two"
LIMIT = 1_048_576


def candidate(**changes):
    value = dict(id=1, trackName=STATE.title, artistName="Björk", albumName="Night (Deluxe)",
                 duration=180, syncedLyrics=SYNCED)
    value.update(changes)
    return value


def response(payload=None):
    return io.BytesIO(json.dumps([candidate()] if payload is None else payload,
                                ensure_ascii=False).encode("utf-8"))


class SearchResolverTests(unittest.TestCase):
    def setUp(self):
        for target in (
            "urllib.request.urlopen", "socket.create_connection", "socket.getaddrinfo",
            "socket.socket.connect", "socket.socket.connect_ex", "subprocess.Popen",
            "amlyrics.smtc_bridge.inspect_sessions", "amlyrics.smtc_bridge.SmtcBridgeMonitor.start",
        ):
            guard = patch(target, side_effect=AssertionError("Actual network/Apple/process access forbidden"))
            guard.start()
            self.addCleanup(guard.stop)

    def test_subclass_construction_and_default_lookup_do_not_open_network(self):
        self.assertTrue(issubclass(AppleDisplayLyricsResolver, LyricsResolver))
        for enabled in (False, True):
            resolver = AppleDisplayLyricsResolver(enable_external=enabled)
            self.assertIsNone(resolver.lookup(STATE))
            self.assertEqual(resolver.last_diagnostic, "disabled")

    def test_both_switches_required_on_every_call_even_after_positive_cache(self):
        with patch("urllib.request.urlopen", return_value=response()) as opener:
            self.assertIsNone(AppleDisplayLyricsResolver().lookup(STATE, allow_network=True))
            resolver = AppleDisplayLyricsResolver(enable_external=True)
            for permission in (False, None, 1, "true"):
                self.assertIsNone(resolver.lookup(STATE, allow_network=permission))
            opener.assert_not_called()
            self.assertIsNotNone(resolver.lookup(STATE, allow_network=True))
            self.assertIsNone(resolver.lookup(STATE))
            self.assertEqual(resolver.last_diagnostic, "disabled")
            opener.assert_called_once()

    def test_search_get_encodes_only_raw_title_and_sets_timeout_and_headers(self):
        resolver = AppleDisplayLyricsResolver(enable_external=True, timeout_seconds=1.25)
        with patch("urllib.request.urlopen", return_value=response()) as opener:
            result = resolver.lookup(STATE, allow_network=True)
        request = opener.call_args.args[0]
        parsed = urlsplit(request.full_url)
        self.assertEqual((parsed.scheme, parsed.netloc, parsed.path), ("https", "lrclib.net", "/api/search"))
        self.assertEqual(parse_qs(parsed.query), {"track_name": [STATE.title]})
        self.assertEqual(request.get_method(), "GET")
        self.assertIsNone(request.data)
        self.assertEqual(request.get_header("Accept"), "application/json")
        self.assertEqual(request.get_header("User-agent"), "apple-music-desktop-lyrics/0.1.0")
        self.assertEqual(opener.call_args.kwargs, {"timeout": 1.25})
        self.assertEqual(result.track_key, STATE.track_key)
        self.assertEqual(result.source, "lrclib")
        self.assertEqual(resolver.last_diagnostic, "lrclib")

    def test_source_aumid_is_case_insensitive(self):
        with patch("urllib.request.urlopen", return_value=response()) as opener:
            result = AppleDisplayLyricsResolver(enable_external=True).lookup(
                replace(STATE, source_app=APPLE_MUSIC_SOURCE.upper()), allow_network=True)
        self.assertIsNotNone(result)
        self.assertEqual(urlsplit(opener.call_args.args[0].full_url).path, "/api/search")

    def test_nonmatching_branch_delegates_to_parent(self):
        states = [replace(STATE, source_app=source) for source in (
            "", "Spotify.exe", "AppleMusic", " " + APPLE_MUSIC_SOURCE, APPLE_MUSIC_SOURCE + "-other",
        )]
        states += [replace(STATE, album="Known"), replace(STATE, album=" "),
                   replace(STATE, artist="Björk"), replace(STATE, artist="Björk—Night (Deluxe)"),
                   replace(STATE, artist="Björk – Night (Deluxe)")]
        for state in states:
            with self.subTest(state=state), patch.object(
                LyricsResolver, "_fetch_external", return_value=(None, "parent_fixture"),
            ) as parent, patch("urllib.request.urlopen") as opener:
                resolver = AppleDisplayLyricsResolver(enable_external=True)
                self.assertIsNone(resolver.lookup(state, allow_network=True))
                parent.assert_called_once_with(state)
                opener.assert_not_called()
                self.assertEqual(resolver.last_diagnostic, "parent_fixture")

    def test_nonapple_parent_get_really_uses_existing_query(self):
        state = replace(STATE, artist="Björk", album="Night (Deluxe)", source_app="Spotify.exe")
        with patch("urllib.request.urlopen", return_value=response(candidate())) as opener:
            result = AppleDisplayLyricsResolver(enable_external=True).lookup(state, allow_network=True)
        parsed = urlsplit(opener.call_args.args[0].full_url)
        self.assertEqual(parsed.path, "/api/get")
        self.assertEqual(parse_qs(parsed.query), dict(track_name=[state.title], artist_name=[state.artist],
                                                   album_name=[state.album], duration=["180.0"]))
        self.assertIsNotNone(result)

    def test_apple_then_local_priority_still_precedes_search_and_cache(self):
        mapping = {}
        provider = Mock(return_value=None)
        resolver = AppleDisplayLyricsResolver(provider, mapping, enable_external=True)
        with patch("urllib.request.urlopen", return_value=response()) as opener:
            self.assertEqual(resolver.lookup(STATE, allow_network=True).source, "lrclib")
            mapping[track_identity(STATE)] = SYNCED
            self.assertEqual(resolver.lookup(STATE, allow_network=True).source, "local")
            provider.return_value = LyricsResult(STATE.track_key, "fixture", None,
                                                 (LyricLine(1000, 180000, "Apple fixture"),))
            self.assertEqual(resolver.lookup(STATE, allow_network=True).source, "apple_music")
            opener.assert_called_once()

    def test_missing_metadata_never_requests_search(self):
        for changes in (dict(title=""), dict(title=" "), dict(duration_ms=0),
                        dict(duration_ms=-1), dict(duration_ms=True)):
            resolver = AppleDisplayLyricsResolver(enable_external=True)
            self.assertIsNone(resolver.lookup(replace(STATE, **changes), allow_network=True))
            self.assertEqual(resolver.last_diagnostic, "missing_metadata")

    def test_all_candidates_use_exact_parser_and_ambiguous_search_is_rejected(self):
        payloads = [[], [candidate(trackName="Café & 光?")], [candidate(artistName="Guest")],
                    [candidate(albumName="Night")], [candidate(duration=183)],
                    [candidate(syncedLyrics=None)], [candidate(), candidate()],
                    [candidate(), candidate(id=2)], [None, "bad"]]
        for payload in payloads:
            with self.subTest(payload=payload), patch("urllib.request.urlopen", return_value=response(payload)):
                resolver = AppleDisplayLyricsResolver(enable_external=True)
                self.assertIsNone(resolver.lookup(STATE, allow_network=True))
                self.assertEqual(resolver.last_diagnostic, "no_synced_match")

    def test_failed_candidates_do_not_hide_unique_match_and_no_twenty_row_truncation(self):
        payload = [candidate(id=i, trackName="Different") for i in range(20)] + [candidate(id=21)]
        with patch("urllib.request.urlopen", return_value=response(payload)):
            result = AppleDisplayLyricsResolver(enable_external=True).lookup(STATE, allow_network=True)
        self.assertEqual(result.track_key, STATE.track_key)

    def test_configured_tolerance_is_forwarded(self):
        for tolerance, success in ((0, False), (1000, True)):
            with patch("urllib.request.urlopen", return_value=response([candidate(duration=181)])):
                result = AppleDisplayLyricsResolver(enable_external=True, duration_tolerance_ms=tolerance).lookup(
                    STATE, allow_network=True)
            self.assertEqual(result is not None, success)

    def test_reads_only_limit_plus_one_and_closes_response(self):
        stream = Mock(wraps=response())
        manager = Mock()
        manager.__enter__ = Mock(return_value=stream)
        manager.__exit__ = Mock(return_value=False)
        with patch("urllib.request.urlopen", return_value=manager):
            result = AppleDisplayLyricsResolver(enable_external=True).lookup(STATE, allow_network=True)
        self.assertIsNotNone(result)
        stream.read.assert_called_once_with(LIMIT + 1)
        manager.__exit__.assert_called_once()

    def test_response_limit_is_inclusive_and_streams_are_closed(self):
        for size, success in ((LIMIT, True), (LIMIT + 1, False)):
            raw = json.dumps([candidate()]).encode()
            stream = io.BytesIO(raw + b" " * (size-len(raw)))
            with patch("urllib.request.urlopen", return_value=stream):
                resolver = AppleDisplayLyricsResolver(enable_external=True)
                result = resolver.lookup(STATE, allow_network=True)
            self.assertEqual(result is not None, success)
            self.assertTrue(stream.closed)
            self.assertEqual(resolver.last_diagnostic, "lrclib" if success else "invalid_response")

    def test_invalid_utf8_json_depth_and_nonarray_responses_fail_closed(self):
        for raw in (b"\xff", b"not json", b"[", b"[" * 2000, b"{}", b"null", b"42", b'"text"'):
            with self.subTest(raw=raw[:20]), patch("urllib.request.urlopen", return_value=io.BytesIO(raw)):
                resolver = AppleDisplayLyricsResolver(enable_external=True)
                self.assertIsNone(resolver.lookup(STATE, allow_network=True))
                self.assertEqual(resolver.last_diagnostic, "invalid_response")

    def test_http_errors_close_the_error_body_and_keep_diagnostics(self):
        for code, diagnostic in ((404, "not_found"), (429, "rate_limited"), (500, "http_error")):
            body = io.BytesIO(b"sensitive fixture")
            failure = HTTPError("https://fixture.invalid", code, "sensitive fixture", {}, body)
            with patch("urllib.request.urlopen", side_effect=failure) as opener:
                resolver = AppleDisplayLyricsResolver(enable_external=True)
                self.assertIsNone(resolver.lookup(STATE, allow_network=True))
                self.assertIsNone(resolver.lookup(STATE, allow_network=True))
                self.assertEqual(resolver.last_diagnostic, diagnostic)
                opener.assert_called_once()
            self.assertTrue(body.closed)

    def test_timeout_urlerror_and_oserror_match_parent_diagnostics(self):
        failures = ((TimeoutError("sensitive"), "timeout"), (socket.timeout(), "timeout"),
                    (URLError(TimeoutError()), "timeout"), (URLError("sensitive"), "network_error"),
                    (OSError("sensitive"), "network_error"))
        for failure, diagnostic in failures:
            with self.subTest(diagnostic=diagnostic), patch("urllib.request.urlopen", side_effect=failure):
                resolver = AppleDisplayLyricsResolver(enable_external=True)
                self.assertIsNone(resolver.lookup(STATE, allow_network=True))
                self.assertEqual(resolver.last_diagnostic, diagnostic)

    def test_read_failure_closes_response(self):
        stream = io.BytesIO(b"fixture")
        stream.read = Mock(side_effect=OSError("read failed"))
        with patch("urllib.request.urlopen", return_value=stream):
            resolver = AppleDisplayLyricsResolver(enable_external=True)
            self.assertIsNone(resolver.lookup(STATE, allow_network=True))
        self.assertTrue(stream.closed)
        self.assertEqual(resolver.last_diagnostic, "network_error")

    def test_positive_and_negative_cache_expire(self):
        for payload, expected in (([candidate()], True), ([], False)):
            clock = Mock(return_value=0.0)
            resolver = AppleDisplayLyricsResolver(enable_external=True, clock=clock,
                                                  positive_cache_ttl_seconds=3, negative_cache_ttl_seconds=3)
            with patch("urllib.request.urlopen", side_effect=[response(payload), response(payload)]) as opener:
                for moment in (0.0, 2.0, 3.0):
                    clock.return_value = moment
                    self.assertEqual(resolver.lookup(STATE, allow_network=True) is not None, expected)
                self.assertEqual(opener.call_count, 2)

    def test_normalized_cache_hit_rebinds_raw_track_key(self):
        alternate = replace(STATE, title="CAFE\u0301 (LIVE) & 光?", source_app=APPLE_MUSIC_SOURCE.upper())
        with patch("urllib.request.urlopen", return_value=response()) as opener:
            resolver = AppleDisplayLyricsResolver(enable_external=True)
            resolver.lookup(STATE, allow_network=True)
            result = resolver.lookup(alternate, allow_network=True)
            opener.assert_called_once()
        self.assertEqual(result.track_key, alternate.track_key)

    def test_source_change_cannot_reuse_positive_or_negative_search_cache(self):
        other = replace(STATE, source_app="Spotify.exe")
        for payload in ([candidate()], []):
            with patch("urllib.request.urlopen", side_effect=[response(payload), response(payload)]) as opener, patch.object(
                LyricsResolver, "_fetch_external", return_value=(None, "parent_fixture"),
            ) as parent:
                resolver = AppleDisplayLyricsResolver(enable_external=True)
                resolver.lookup(STATE, allow_network=True)
                resolver.lookup(other, allow_network=True)
                self.assertEqual(resolver.last_diagnostic, "parent_fixture")
                parent.assert_called_once_with(other)
                resolver.lookup(STATE, allow_network=True)
                self.assertEqual(opener.call_count, 2)

    def test_nonapple_cache_cannot_bypass_later_apple_validation(self):
        other = replace(STATE, source_app="Spotify.exe")
        parent_result = LyricsResult(other.track_key, "lrclib", None, (LyricLine(1000, 180000, "Fixture"),))
        with patch.object(LyricsResolver, "_fetch_external", return_value=(parent_result, "lrclib")), patch(
            "urllib.request.urlopen", return_value=response([]),
        ) as opener:
            resolver = AppleDisplayLyricsResolver(enable_external=True)
            self.assertIsNotNone(resolver.lookup(other, allow_network=True))
            self.assertIsNone(resolver.lookup(STATE, allow_network=True))
            opener.assert_called_once()


if __name__ == "__main__":
    unittest.main()
