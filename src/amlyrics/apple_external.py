"""Offline matching of LRCLIB-shaped candidates to Apple's raw SMTC display.

This module implements no search transport and makes no claim about a live API.
"""
from __future__ import annotations

from collections.abc import Mapping
from dataclasses import replace

from .contracts import LyricsResult, PlaybackState
from .lyrics_sources import (
    DEFAULT_DURATION_TOLERANCE_MS,
    _check_tolerance,
    _normalize,
    parse_lrclib_response,
)
from .smtc_bridge import APPLE_MUSIC_SOURCE


def parse_apple_display_candidates(
    payload: object,
    state: PlaybackState,
    *,
    duration_tolerance_ms: int = DEFAULT_DURATION_TOLERANCE_MS,
) -> LyricsResult | None:
    """Accept exactly one fully validated candidate from a decoded JSON list.

    Only the observed Apple AUMID (case-insensitive) and a literally empty raw
    album qualify. Candidate artistName/albumName must be nonblank strings whose
    literal reconstruction, artistName + ' — ' + albumName, matches the entire
    raw artist after NFKC/casefold/strip. Never split the raw display or remove
    version labels. The shared parser enforces title, duration and timed lyrics.

    Two successful candidates are ambiguous, including identical rows/IDs.
    Restore the raw state's track_key after checking a temporary metadata state;
    neither the original state nor any candidate is modified. All rejection
    cases return None; invalid tolerance raises ValueError, as in the base parser.
    """
    _check_tolerance(duration_tolerance_ms)
    if (not isinstance(payload, list) or state.album != "" or
            not isinstance(state.source_app, str) or
            state.source_app.casefold() != APPLE_MUSIC_SOURCE.casefold() or
            not isinstance(state.artist, str)):
        return None

    matched: LyricsResult | None = None
    for candidate in payload:
        if not isinstance(candidate, Mapping):
            continue
        artist = candidate.get("artistName")
        album = candidate.get("albumName")
        if (not isinstance(artist, str) or not _normalize(artist) or
                not isinstance(album, str) or not _normalize(album)):
            continue
        if _normalize(f"{artist} — {album}") != _normalize(state.artist):
            continue
        metadata_state = replace(state, artist=artist, album=album)
        result = parse_lrclib_response(
            candidate, metadata_state, duration_tolerance_ms=duration_tolerance_ms,
        )
        if result is None:
            continue
        if matched is not None:
            return None
        matched = replace(result, track_key=state.track_key)
    return matched
