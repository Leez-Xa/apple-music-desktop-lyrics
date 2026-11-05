from __future__ import annotations

import math
import re
import unicodedata
from dataclasses import dataclass
from datetime import datetime, timedelta

from .contracts import LyricLine, LyricsResult, PlaybackState

MAX_LINES = 2_048
MAX_LINE_CHARS = 4_096
MAX_TOTAL_TEXT_CHARS = 262_144
MAX_METADATA_CHARS = 1_024
_MAX_TIMESTAMP_CHARS = 64
_MAX_TIME_MS = 2**63 - 1
_REMOVABLE_FORMATS = str.maketrans({"\u2060": None, "\ufeff": None})
_UTC_ISO = re.compile(
    r"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}"
    r"(?:\.[0-9]{1,7})?(?:Z|[+]00:00)"
)


def normalize_apple_text(text: str) -> str:
    """Remove only WORD JOINER and BOM; preserve words, spacing and case.

    ZWJ/ZWNJ, variation selectors and other potentially meaningful characters
    are deliberately preserved. NFC is used separately for equality only.
    """
    return text.translate(_REMOVABLE_FORMATS)


def _canonical(text: str) -> str:
    return unicodedata.normalize("NFC", normalize_apple_text(text))


def _bounded_text(value: object, limit: int) -> bool:
    return (isinstance(value, str) and len(value) <= limit
            and not any(char in "\r\n\x00" or 0xD800 <= ord(char) <= 0xDFFF
                        for char in value))


@dataclass(frozen=True, slots=True)
class AppleLyricLine:
    """An ordered UIA label, with no timing or active-line semantics."""

    index: int
    text: str


@dataclass(frozen=True, slots=True)
class AppleLyricsObservation:
    version: int
    status: str
    observed_at: datetime
    title: str
    subtitle: str
    lines: tuple[AppleLyricLine, ...]
    timing_status: str = "not_exposed"
    current_index: None = None
    reason: str | None = None

    def __post_init__(self) -> None:
        # Also validate manually constructed instances before helpers use them.
        if type(self.version) is not int or self.version != 1:
            raise ValueError("Unsupported observation version")
        if self.status not in ("observed", "unavailable"):
            raise ValueError("Invalid observation status")
        if (not isinstance(self.observed_at, datetime)
                or self.observed_at.tzinfo is None
                or self.observed_at.utcoffset() != timedelta(0)):
            raise ValueError("observed_at must be an aware UTC datetime")
        if self.timing_status != "not_exposed" or self.current_index is not None:
            raise ValueError("This contract exposes no timing or current line")
        if not all(_bounded_text(text, MAX_METADATA_CHARS)
                   for text in (self.title, self.subtitle)):
            raise ValueError("Invalid metadata")
        if self.reason is not None and not _bounded_text(self.reason, MAX_METADATA_CHARS):
            raise ValueError("Invalid unavailable reason")
        if type(self.lines) is not tuple or len(self.lines) > MAX_LINES:
            raise ValueError("Invalid line collection")
        total = 0
        previous = -1
        for line in self.lines:
            if (not isinstance(line, AppleLyricLine) or type(line.index) is not int
                    or not previous < line.index < MAX_LINES
                    or not _bounded_text(line.text, MAX_LINE_CHARS)):
                raise ValueError("Invalid line or unordered/duplicate index")
            previous = line.index
            total += len(line.text)
            if total > MAX_TOTAL_TEXT_CHARS:
                raise ValueError("Observation text exceeds limit")
        if self.status == "unavailable" and (self.title or self.subtitle or self.lines):
            raise ValueError("Unavailable observations must not retain lyrics")


def _matches_state(observation: AppleLyricsObservation, state: PlaybackState) -> bool:
    if not isinstance(state, PlaybackState):
        return False
    if not all(_bounded_text(text, MAX_METADATA_CHARS)
               for text in (state.title, state.artist, state.album)):
        return False
    title, artist = _canonical(state.title), _canonical(state.artist)
    return (observation.status == "observed" and bool(title.strip())
            and bool(artist.strip()) and _canonical(observation.title) == title
            and _canonical(observation.subtitle) == artist)


def parse_apple_lyrics_observation(
    payload: object, state: PlaybackState,
) -> AppleLyricsObservation | None:
    """Parse the already-decoded bridge dict, without any I/O.

    Unknown metadata (including window handles) is ignored. Required fields are
    never inferred. Invalid/mismatched observations return None. Unavailable
    observations are retained as explicit empty observations, never lyric data.
    Limits are checked against raw text before invisible characters are removed.
    """
    if not isinstance(payload, dict):
        return None
    required = {"version", "status", "observed_at", "title", "subtitle",
                "lines", "timing_status", "current_index"}
    if not required.issubset(payload):
        return None
    timestamp = payload["observed_at"]
    if (not isinstance(timestamp, str) or len(timestamp) > _MAX_TIMESTAMP_CHARS
            or _UTC_ISO.fullmatch(timestamp) is None):
        return None
    raw_lines = payload["lines"]
    if not isinstance(raw_lines, list) or len(raw_lines) > MAX_LINES:
        return None
    if not all(_bounded_text(payload[field], MAX_METADATA_CHARS)
               for field in ("title", "subtitle")):
        return None
    parsed_lines = []
    total = 0
    for line in raw_lines:
        if not isinstance(line, dict) or "index" not in line or "text" not in line:
            return None
        text = line["text"]
        if not _bounded_text(text, MAX_LINE_CHARS):
            return None
        total += len(text)
        if total > MAX_TOTAL_TEXT_CHARS:
            return None
        parsed_lines.append(AppleLyricLine(line["index"], normalize_apple_text(text)))
    try:
        observation = AppleLyricsObservation(
            version=payload["version"], status=payload["status"],
            observed_at=datetime.fromisoformat(timestamp.replace("Z", "+00:00")),
            title=normalize_apple_text(payload["title"]),
            subtitle=normalize_apple_text(payload["subtitle"]),
            lines=tuple(parsed_lines), timing_status=payload["timing_status"],
            current_index=payload["current_index"], reason=payload.get("reason"),
        )
    except (TypeError, ValueError, OverflowError):
        return None
    if observation.status == "observed" and not _matches_state(observation, state):
        return None
    return observation


def get_apple_timed_result(
    observation: AppleLyricsObservation | None, state: PlaybackState,
) -> LyricsResult | None:
    """Always None: bridge v1 has no Apple timestamps or active-line evidence.

    observed_at is capture time, and line indices are ordering only. Playback
    position, Invoke/ScrollItem patterns, geometry and centering cannot create a
    timeline. This adapter is intentionally safe for the resolver callback.
    """
    return None


def combine_apple_text_with_timing(
    observation: AppleLyricsObservation | None,
    external: LyricsResult,
    state: PlaybackState,
) -> LyricsResult | None:
    """Pair every nonblank Apple line with an exact ordered external match.

    Only canonical Unicode equality is accepted; no case/whitespace folding,
    punctuation changes, search, dictionary mapping, offsets or partial matches.
    External track_key must exactly equal the current state key (album included).
    All timings remain external, with that provenance explicit in source.
    """
    if (not isinstance(observation, AppleLyricsObservation)
            or not _matches_state(observation, state)
            or not isinstance(external, LyricsResult)
            or external.track_key != state.track_key
            or external.timing_precision != "line"
            or type(external.lines) is not tuple
            or not 0 < len(external.lines) <= MAX_LINES
            or type(state.duration_ms) is not int or state.duration_ms < 0
            or type(external.confidence) not in (int, float)
            or not 0 <= external.confidence <= 1
            or not math.isfinite(external.confidence)):
        return None
    # Recheck the immutable value's invariants at this public boundary.
    try:
        observation.__post_init__()
    except (TypeError, ValueError, OverflowError):
        return None
    apple = tuple(line for line in observation.lines
                  if normalize_apple_text(line.text).strip())
    timed = []
    total = 0
    previous = -1
    for line in external.lines:
        if not isinstance(line, LyricLine) or not _bounded_text(line.text, MAX_LINE_CHARS):
            return None
        total += len(line.text)
        if total > MAX_TOTAL_TEXT_CHARS:
            return None
        if (type(line.start_ms) is not int
                or not 0 <= line.start_ms <= _MAX_TIME_MS
                or line.start_ms < previous):
            return None
        if line.end_ms is not None and (type(line.end_ms) is not int
                or not line.start_ms <= line.end_ms <= _MAX_TIME_MS):
            return None
        if state.duration_ms > 0 and (line.start_ms > state.duration_ms
                or (line.end_ms is not None and line.end_ms > state.duration_ms)):
            return None
        previous = line.start_ms
        if normalize_apple_text(line.text).strip():
            timed.append(line)
    if not apple or len(apple) != len(timed):
        return None
    if any(_canonical(a.text) != _canonical(b.text) for a, b in zip(apple, timed)):
        return None
    lines = tuple(LyricLine(b.start_ms, b.end_ms, normalize_apple_text(a.text))
                  for a, b in zip(apple, timed))
    return LyricsResult(
        state.track_key, "apple_music+external_timing", external.language,
        lines, timing_precision="line", confidence=external.confidence,
    )
