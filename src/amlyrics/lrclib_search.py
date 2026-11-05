"""Opt-in LRCLIB search for Apple's merged SMTC artist/album display.

The search contract was checked in official docs by the project lead. This
implementation has only fixture-based validation; no live lyrics request.
"""
from __future__ import annotations

import json
import socket
from urllib import error as urllib_error
from urllib import parse as urllib_parse
from urllib import request as urllib_request

from .apple_external import parse_apple_display_candidates
from .contracts import LyricsResult, PlaybackState
from .lyrics_sources import LyricsResolver, _MAX_RESPONSE_BYTES
from .smtc_bridge import APPLE_MUSIC_SOURCE

_SEARCH_ENDPOINT = "https://lrclib.net/api/search"
_USER_AGENT = "apple-music-desktop-lyrics/0.1.0"


class AppleDisplayLyricsResolver(LyricsResolver):
    """Keep Apple/local priority and require both inherited network switches.

    Only an exact Apple AUMID (case-insensitive), a raw empty album and the
    literal separator ' — ' select search. Other states use the parent /get
    behavior. Search sends only the raw title, with no duration or guessed
    metadata. The existing offline parser must accept exactly one candidate.
    Instances share the parent's single-caller cache/concurrency assumptions.
    """

    def lookup(self, state: PlaybackState, *, allow_network: bool = False) -> LyricsResult | None:
        # The parent key omits source_app. Do not reuse an Apple display match
        # (or miss) after changing sources, even when metadata is identical.
        source = state.source_app.casefold() if isinstance(state.source_app, str) else None
        if getattr(self, "_cache_source_app", None) != source:
            self._cache.clear()
            self._cache_source_app = source
        return super().lookup(state, allow_network=allow_network)

    def _fetch_external(self, state: PlaybackState) -> tuple[LyricsResult | None, str]:
        if (not isinstance(state.source_app, str) or
                state.source_app.casefold() != APPLE_MUSIC_SOURCE.casefold() or
                state.album != "" or " — " not in state.artist):
            return super()._fetch_external(state)

        query = urllib_parse.urlencode({"track_name": state.title})
        request = urllib_request.Request(
            f"{_SEARCH_ENDPOINT}?{query}", method="GET",
            headers={"Accept": "application/json", "User-Agent": _USER_AGENT},
        )
        try:
            with urllib_request.urlopen(request, timeout=self.timeout_seconds) as response:
                raw = response.read(_MAX_RESPONSE_BYTES + 1)
            if len(raw) > _MAX_RESPONSE_BYTES:
                return None, "invalid_response"
            payload = json.loads(raw.decode("utf-8"))
            if not isinstance(payload, list):
                return None, "invalid_response"
            result = parse_apple_display_candidates(
                payload, state, duration_tolerance_ms=self.duration_tolerance_ms,
            )
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
