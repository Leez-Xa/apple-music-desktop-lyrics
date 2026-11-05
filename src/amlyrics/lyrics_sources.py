from __future__ import annotations

import json
import math
import socket
import time
import unicodedata
from collections.abc import Callable, Mapping
from dataclasses import dataclass, replace
from urllib import error as urllib_error
from urllib import parse as urllib_parse
from urllib import request as urllib_request

from .contracts import LyricLine, LyricsResult, PlaybackState
from .lrc import parse_lrc

# Album and exact duration deliberately remain part of both positive/negative keys.
TrackIdentity = tuple[str, str, str, int]
AppleLyricsProvider = Callable[[PlaybackState], LyricsResult | None]
DEFAULT_DURATION_TOLERANCE_MS = 2_000
_MAX_RESPONSE_BYTES = 1_048_576
_LRCLIB_ENDPOINT = "https://lrclib.net/api/get"


def _normalize(value: str) -> str:
    """Normalize Unicode/case/outer whitespace, preserving all version labels."""
    return unicodedata.normalize("NFKC", value).casefold().strip()


def track_identity(state: PlaybackState) -> TrackIdentity:
    """Key for local LRC mappings and version-sensitive external caches."""
    return (_normalize(state.title), _normalize(state.artist),
            _normalize(state.album), state.duration_ms)


def _valid_lines(lines: tuple[LyricLine, ...], duration_ms: int) -> bool:
    previous = -1
    has_text = False
    for line in lines:
        if not isinstance(line, LyricLine) or not isinstance(line.text, str):
            return False
        if type(line.start_ms) is not int or line.start_ms < 0 or line.start_ms < previous:
            return False
        if line.end_ms is not None and (type(line.end_ms) is not int or line.end_ms < line.start_ms):
            return False
        if duration_ms > 0 and (line.start_ms > duration_ms or
                                (line.end_ms is not None and line.end_ms > duration_ms)):
            return False
        previous = line.start_ms
        has_text = has_text or bool(line.text.strip())
    return has_text


def _from_lrc(text: object, state: PlaybackState, source: str) -> LyricsResult | None:
    if not isinstance(text, str) or not text.strip():
        return None
    parsed = parse_lrc(text, end_ms=state.duration_ms if state.duration_ms > 0 else None)
    if not _valid_lines(parsed.lines, state.duration_ms):
        return None
    return LyricsResult(state.track_key, source, parsed.metadata.get("lang"),
                        parsed.lines, confidence=1.0)


def _check_tolerance(value: int) -> None:
    if type(value) is not int or value < 0:
        raise ValueError("duration_tolerance_ms must be a non-negative integer")


def parse_lrclib_response(
    payload: object,
    state: PlaybackState,
    *,
    duration_tolerance_ms: int = DEFAULT_DURATION_TOLERANCE_MS,
) -> LyricsResult | None:
    """Offline parser for one /api/get object; plainLyrics is never a fallback.

    Title/artist and a supplied album must match after NFKC + casefold + strip.
    A known positive duration is required, with an inclusive millisecond tolerance.
    This schema is an integration assumption, not a claim of a live API test.
    """
    _check_tolerance(duration_tolerance_ms)
    if not isinstance(payload, Mapping):
        return None
    if (not _normalize(state.title) or not _normalize(state.artist) or
            type(state.duration_ms) is not int or state.duration_ms <= 0):
        return None
    for field, expected in (("trackName", state.title), ("artistName", state.artist)):
        actual = payload.get(field)
        if not isinstance(actual, str) or _normalize(actual) != _normalize(expected):
            return None
    if _normalize(state.album):
        album = payload.get("albumName")
        if not isinstance(album, str) or _normalize(album) != _normalize(state.album):
            return None
    duration = payload.get("duration")
    if type(duration) not in (int, float):
        return None
    try:
        duration_ms = float(duration) * 1_000
    except OverflowError:
        return None
    if not math.isfinite(duration_ms) or duration_ms <= 0:
        return None
    if abs(duration_ms - state.duration_ms) > duration_tolerance_ms:
        return None
    return _from_lrc(payload.get("syncedLyrics"), state, "lrclib")


@dataclass(frozen=True, slots=True)
class _CacheEntry:
    expires_at: float
    result: LyricsResult | None
    diagnostic: str


class LyricsResolver:
    """Apple callback -> optional in-memory LRC mapping -> opt-in LRCLIB.

    Construction never performs I/O. External I/O requires BOTH enable_external=True
    at construction and allow_network=True on that specific lookup. The Apple
    callback must itself be offline and return already-obtained, timed lyrics.
    """

    def __init__(
        self,
        apple_provider: AppleLyricsProvider | None = None,
        local_mapping: Mapping[TrackIdentity, str] | None = None,
        *,
        enable_external: bool = False,
        duration_tolerance_ms: int = DEFAULT_DURATION_TOLERANCE_MS,
        timeout_seconds: float = 5.0,
        positive_cache_ttl_seconds: float = 300.0,
        negative_cache_ttl_seconds: float = 30.0,
        max_cache_entries: int = 128,
        clock: Callable[[], float] = time.monotonic,
    ) -> None:
        _check_tolerance(duration_tolerance_ms)
        if type(enable_external) is not bool:
            raise ValueError("enable_external must be a bool")
        for name, value, allow_zero in (
            ("timeout_seconds", timeout_seconds, False),
            ("positive_cache_ttl_seconds", positive_cache_ttl_seconds, True),
            ("negative_cache_ttl_seconds", negative_cache_ttl_seconds, True),
        ):
            if (type(value) not in (int, float) or not math.isfinite(value) or
                    value < 0 or (not allow_zero and value == 0)):
                raise ValueError(f"{name} must be finite and {'non-negative' if allow_zero else 'positive'}")
        if type(max_cache_entries) is not int or max_cache_entries < 1:
            raise ValueError("max_cache_entries must be a positive integer")
        self.apple_provider = apple_provider
        self.local_mapping = local_mapping if local_mapping is not None else {}
        self.enable_external = enable_external
        self.duration_tolerance_ms = duration_tolerance_ms
        self.timeout_seconds = timeout_seconds
        self.positive_cache_ttl_seconds = positive_cache_ttl_seconds
        self.negative_cache_ttl_seconds = negative_cache_ttl_seconds
        self.max_cache_entries = max_cache_entries
        self._clock = clock
        self._cache: dict[TrackIdentity, _CacheEntry] = {}
        self.last_diagnostic: str | None = None

    def lookup(self, state: PlaybackState, *, allow_network: bool = False) -> LyricsResult | None:
        """Compatible with LyricsService; its ordinary lookup(state) is offline."""
        self.last_diagnostic = None
        if self.apple_provider is not None:
            try:
                result = self.apple_provider(state)
                if (isinstance(result, LyricsResult) and len(result.track_key) == 3 and
                        tuple(_normalize(part) for part in result.track_key) == track_identity(state)[:3] and
                        _valid_lines(result.lines, state.duration_ms)):
                    self.last_diagnostic = "apple_music"
                    return replace(result, track_key=state.track_key, source="apple_music")
            except Exception:
                # Do not expose provider exceptions: they could contain account data.
                pass
        identity = track_identity(state)
        result = _from_lrc(self.local_mapping.get(identity), state, "local")
        if result is not None:
            self.last_diagnostic = "local"
            return result
        if not self.enable_external or allow_network is not True:
            self.last_diagnostic = "disabled"
            return None
        if (not identity[0] or not identity[1] or
                type(state.duration_ms) is not int or state.duration_ms <= 0):
            self.last_diagnostic = "missing_metadata"
            return None
        cached = self._cache.get(identity)
        if cached is not None and self._clock() < cached.expires_at:
            self.last_diagnostic = cached.diagnostic
            return replace(cached.result, track_key=state.track_key) if cached.result is not None else None
        if cached is not None:
            del self._cache[identity]
        result, diagnostic = self._fetch_external(state)
        ttl = self.positive_cache_ttl_seconds if result is not None else self.negative_cache_ttl_seconds
        if ttl > 0:
            while len(self._cache) >= self.max_cache_entries:
                del self._cache[next(iter(self._cache))]
            self._cache[identity] = _CacheEntry(self._clock() + ttl, result, diagnostic)
        self.last_diagnostic = diagnostic
        return result

    def _fetch_external(self, state: PlaybackState) -> tuple[LyricsResult | None, str]:
        query = urllib_parse.urlencode({
            "track_name": state.title,
            "artist_name": state.artist,
            "album_name": state.album,
            "duration": state.duration_ms / 1_000,
        })
        request = urllib_request.Request(
            f"{_LRCLIB_ENDPOINT}?{query}",
            headers={"Accept": "application/json", "User-Agent": "amlyrics/0.1"},
            method="GET",
        )
        try:
            with urllib_request.urlopen(request, timeout=self.timeout_seconds) as response:
                raw = response.read(_MAX_RESPONSE_BYTES + 1)
            if len(raw) > _MAX_RESPONSE_BYTES:
                return None, "invalid_response"
            payload = json.loads(raw.decode("utf-8"))
            result = parse_lrclib_response(payload, state, duration_tolerance_ms=self.duration_tolerance_ms)
            return result, "lrclib" if result is not None else "no_synced_match"
        except urllib_error.HTTPError as exc:
            try:
                if exc.code == 404:
                    return None, "not_found"
                if exc.code == 429:
                    return None, "rate_limited"
                return None, "http_error"
            finally:
                exc.close()
        except (TimeoutError, socket.timeout):
            return None, "timeout"
        except urllib_error.URLError as exc:
            return None, "timeout" if isinstance(exc.reason, (TimeoutError, socket.timeout)) else "network_error"
        except (UnicodeError, ValueError, RecursionError):
            return None, "invalid_response"
        except OSError:
            return None, "network_error"
