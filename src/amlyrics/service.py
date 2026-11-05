from __future__ import annotations

from collections.abc import Callable

from .contracts import LyricsResult, PlaybackState, SyncFrame
from .playback import PlaybackMonitor
from .synchronizer import LyricSynchronizer

LyricsLookup = Callable[[PlaybackState], LyricsResult | None]
FrameListener = Callable[[SyncFrame], None]


class LyricsService:
    """Glue layer between playback state, lyrics lookup, sync, and UI."""

    def __init__(self, monitor: PlaybackMonitor, lookup: LyricsLookup) -> None:
        self.monitor = monitor
        self.lookup = lookup
        self.synchronizer = LyricSynchronizer()
        self.current_track_key: tuple[str, str, str] | None = None
        self.result: LyricsResult | None = None
        self._listeners: list[FrameListener] = []
        monitor.subscribe(self._on_playback)

    def subscribe_frame(self, listener: FrameListener) -> None:
        self._listeners.append(listener)

    def _on_playback(self, state: PlaybackState) -> None:
        if state.track_key != self.current_track_key:
            self.current_track_key = state.track_key
            self.result = self.lookup(state)
            self.synchronizer.replace(self.result.lines if self.result else ())
            if self.result:
                self.synchronizer.offset_ms = 0
        frame = self.synchronizer.frame_at(state.position_ms, is_playing=state.is_playing)
        for listener in tuple(self._listeners):
            listener(frame)
