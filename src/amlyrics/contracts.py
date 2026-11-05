from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime, timezone
from enum import StrEnum
from typing import Any


class MessageType(StrEnum):
    TOGGLE_OVERLAY = "ToggleOverlay"
    SHOW_OVERLAY = "ShowOverlay"
    HIDE_OVERLAY = "HideOverlay"
    PLAYBACK_STATE_CHANGED = "PlaybackStateChanged"
    LYRICS_UPDATED = "LyricsUpdated"
    APPLE_MUSIC_UNAVAILABLE = "AppleMusicUnavailable"
    DIAGNOSTIC = "Diagnostic"


@dataclass(frozen=True, slots=True)
class PlaybackState:
    title: str = ""
    artist: str = ""
    album: str = ""
    duration_ms: int = 0
    position_ms: int = 0
    is_playing: bool = False
    source_app: str = ""
    observed_at: datetime = field(default_factory=lambda: datetime.now(timezone.utc))

    @property
    def track_key(self) -> tuple[str, str, str]:
        return (self.title.casefold().strip(), self.artist.casefold().strip(), self.album.casefold().strip())

    def to_dict(self) -> dict[str, Any]:
        return {
            "title": self.title,
            "artist": self.artist,
            "album": self.album,
            "duration_ms": self.duration_ms,
            "position_ms": self.position_ms,
            "is_playing": self.is_playing,
            "source_app": self.source_app,
            "observed_at": self.observed_at.isoformat(),
        }


@dataclass(frozen=True, slots=True)
class LyricLine:
    start_ms: int
    end_ms: int | None
    text: str

    def to_dict(self) -> dict[str, Any]:
        return {"start_ms": self.start_ms, "end_ms": self.end_ms, "text": self.text}


@dataclass(frozen=True, slots=True)
class LyricsResult:
    track_key: tuple[str, str, str]
    source: str
    language: str | None
    lines: tuple[LyricLine, ...]
    timing_precision: str = "line"
    confidence: float = 0.0

    def to_dict(self) -> dict[str, Any]:
        return {
            "track_key": list(self.track_key),
            "source": self.source,
            "language": self.language,
            "lines": [line.to_dict() for line in self.lines],
            "timing_precision": self.timing_precision,
            "confidence": self.confidence,
        }


@dataclass(frozen=True, slots=True)
class SyncFrame:
    position_ms: int
    current_index: int | None
    current_line: LyricLine | None
    previous_line: LyricLine | None
    next_line: LyricLine | None
    is_playing: bool

    def to_dict(self) -> dict[str, Any]:
        return {
            "position_ms": self.position_ms,
            "current_index": self.current_index,
            "current_line": self.current_line.to_dict() if self.current_line else None,
            "previous_line": self.previous_line.to_dict() if self.previous_line else None,
            "next_line": self.next_line.to_dict() if self.next_line else None,
            "is_playing": self.is_playing,
        }


@dataclass(frozen=True, slots=True)
class IpcMessage:
    type: MessageType
    payload: dict[str, Any] = field(default_factory=dict)
    request_id: str | None = None
    version: int = 1

    def to_dict(self) -> dict[str, Any]:
        return {"version": self.version, "type": self.type.value, "request_id": self.request_id, "payload": self.payload}

    @classmethod
    def from_dict(cls, value: dict[str, Any]) -> "IpcMessage":
        return cls(
            type=MessageType(value["type"]),
            payload=value.get("payload") or {},
            request_id=value.get("request_id"),
            version=int(value.get("version", 1)),
        )
