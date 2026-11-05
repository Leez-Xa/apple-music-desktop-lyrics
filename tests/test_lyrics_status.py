from __future__ import annotations

import io
import json
import unittest
from datetime import datetime, timezone
from types import SimpleNamespace
from unittest.mock import Mock, patch

from amlyrics.apple_lyrics import get_apple_timed_result, parse_apple_lyrics_observation
from amlyrics.apple_preferred import ApplePreferredLookup
from amlyrics.contracts import PlaybackState
from amlyrics.live_session import LiveSession
from amlyrics.lyrics_sources import LyricsResolver


STATE = PlaybackState("Offline fixture", "Fixture Artist", "Fixture Album", 10_000)
SYNCED_PAYLOAD = {
    "trackName": STATE.title,
    "artistName": STATE.artist,
    "albumName": STATE.album,
    "duration": 10,
    "syncedLyrics": "[00:01.00]First fixture line\n[00:04.00]Second fixture line",
    "plainLyrics": "First fixture line\nSecond fixture line",
}


def apple_plain_snapshot() -> dict[str, object]:
    return {
        "version": 1,
        "status": "observed",
        "observed_at": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "title": STATE.title,
        "subtitle": STATE.artist,
        "lines": [
            {"index": 0, "text": "First fixture line"},
            {"index": 1, "text": "Second fixture line"},
        ],
        "timing_status": "not_exposed",
        "current_index": None,
    }


class OfflineResponse(io.BytesIO):
    def __enter__(self):
        return self

    def __exit__(self, exc_type, exc_value, traceback):
        self.close()
        return False


class OfflineLyricsStatusTests(unittest.TestCase):
    def setUp(self):
        self.urlopen_guard = patch(
            "amlyrics.lyrics_sources.urllib_request.urlopen",
            side_effect=AssertionError("real network is forbidden in this offline test"),
        )
        self.urlopen_guard.start()
        self.addCleanup(self.urlopen_guard.stop)

    def test_resolver_default_is_offline_even_if_lookup_is_called(self):
        resolver = LyricsResolver(enable_external=False)

        result = resolver.lookup(STATE, allow_network=True)

        self.assertIsNone(result)
        self.assertEqual(resolver.last_diagnostic, "disabled")

    def test_external_lookup_requires_both_switches(self):
        response = OfflineResponse(json.dumps(SYNCED_PAYLOAD).encode("utf-8"))
        with patch(
            "amlyrics.lyrics_sources.urllib_request.urlopen",
            return_value=response,
        ) as urlopen:
            disabled_at_construction = LyricsResolver(enable_external=False)
            self.assertIsNone(disabled_at_construction.lookup(STATE, allow_network=True))
            self.assertEqual(disabled_at_construction.last_diagnostic, "disabled")

            disabled_for_call = LyricsResolver(enable_external=True)
            self.assertIsNone(disabled_for_call.lookup(STATE, allow_network=False))
            self.assertEqual(disabled_for_call.last_diagnostic, "disabled")

            enabled = LyricsResolver(enable_external=True)
            result = enabled.lookup(STATE, allow_network=True)

        self.assertIsNotNone(result)
        self.assertEqual(result.source, "lrclib")
        self.assertEqual(enabled.last_diagnostic, "lrclib")
        self.assertEqual(urlopen.call_count, 1)

    def test_apple_preferred_defaults_to_offline_and_forwards_explicit_permission(self):
        resolver = SimpleNamespace(lookup=Mock(return_value=None), last_diagnostic="disabled")
        monitor = SimpleNamespace(latest_snapshot={})

        self.assertIsNone(ApplePreferredLookup(monitor, resolver)(STATE))
        resolver.lookup.assert_called_once_with(STATE, allow_network=False)

        resolver.lookup.reset_mock()
        self.assertIsNone(ApplePreferredLookup(monitor, resolver, allow_network=True)(STATE))
        resolver.lookup.assert_called_once_with(STATE, allow_network=True)

    def test_live_diagnostics_are_user_facing_chinese_prompts(self):
        session = LiveSession(SimpleNamespace(), lambda _state: None, executor=SimpleNamespace())
        cases = {
            "disabled": "请先启用在线同步",
            "timeout": "歌词查询超时，稍后重试",
            "network_error": "连接歌词源失败，稍后重试",
            "not_found": "未找到这首歌的同步歌词",
            "no_synced_match": "当前歌曲版本暂无匹配的同步歌词",
            "missing_metadata": "等待完整歌曲信息",
            "invalid_response": "歌词源返回内容无效，稍后重试",
            "http_error": "歌词源暂时不可用，稍后重试",
            "rate_limited": "歌词源暂时限制请求，稍后重试",
        }
        for code, expected in cases.items():
            with self.subTest(code=code):
                session.lookup = SimpleNamespace(last_diagnostic=code)
                message = session._missing_lyrics_message()
                self.assertEqual(message, expected)
                self.assertNotIn(code, message)
        session.lookup = SimpleNamespace(last_diagnostic="untrusted-provider-text")
        self.assertEqual(session._missing_lyrics_message(), "暂无同步歌词")

    def test_apple_plain_lines_never_become_real_time_lyrics(self):
        observation = parse_apple_lyrics_observation(apple_plain_snapshot(), STATE)

        self.assertIsNotNone(observation)
        self.assertIsNone(get_apple_timed_result(observation, STATE))

        resolver = SimpleNamespace(lookup=Mock(return_value=None), last_diagnostic="disabled")
        result = ApplePreferredLookup(
            SimpleNamespace(latest_snapshot=apple_plain_snapshot()), resolver
        )(STATE)
        self.assertIsNone(result)
        resolver.lookup.assert_called_once_with(STATE, allow_network=False)


if __name__ == "__main__":
    unittest.main()
