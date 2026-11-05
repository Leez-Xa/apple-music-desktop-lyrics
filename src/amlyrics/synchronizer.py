from __future__ import annotations

from bisect import bisect_right
from collections.abc import Sequence

from .contracts import LyricLine, SyncFrame


class LyricSynchronizer:
    """Select the active line from a time-coded lyric timeline."""

    def __init__(self, lines: Sequence[LyricLine] = (), *, offset_ms: int = 0) -> None:
        self._lines: tuple[LyricLine, ...] = tuple(sorted(lines, key=lambda line: line.start_ms))
        self._starts = tuple(line.start_ms for line in self._lines)
        self.offset_ms = offset_ms

    @property
    def lines(self) -> tuple[LyricLine, ...]:
        return self._lines

    def replace(self, lines: Sequence[LyricLine]) -> None:
        self._lines = tuple(sorted(lines, key=lambda line: line.start_ms))
        self._starts = tuple(line.start_ms for line in self._lines)

    def frame_at(self, position_ms: int, *, is_playing: bool = True) -> SyncFrame:
        if not self._lines:
            return SyncFrame(position_ms, None, None, None, None, is_playing)
        adjusted = max(0, position_ms + self.offset_ms)
        index = bisect_right(self._starts, adjusted) - 1
        if index < 0:
            return SyncFrame(position_ms, None, None, None, self._lines[0], is_playing)
        current = self._lines[index]
        previous = self._lines[index - 1] if index > 0 else None
        next_line = self._lines[index + 1] if index + 1 < len(self._lines) else None
        return SyncFrame(position_ms, index, current, previous, next_line, is_playing)
