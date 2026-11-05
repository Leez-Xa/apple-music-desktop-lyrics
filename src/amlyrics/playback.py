from __future__ import annotations

from collections.abc import Callable
from dataclasses import replace

from .contracts import PlaybackState

PlaybackListener = Callable[[PlaybackState], None]


class PlaybackMonitor:
    """Common monitor contract used by mock, SMTC, and future adapters."""

    def __init__(self) -> None:
        self._listeners: list[PlaybackListener] = []

    def subscribe(self, listener: PlaybackListener) -> None:
        self._listeners.append(listener)

    def unsubscribe(self, listener: PlaybackListener) -> None:
        self._listeners.remove(listener)

    def _publish(self, state: PlaybackState) -> None:
        for listener in tuple(self._listeners):
            listener(state)


class MockPlaybackMonitor(PlaybackMonitor):
    """Deterministic playback source for local development and tests."""

    def __init__(self, state: PlaybackState | None = None) -> None:
        super().__init__()
        self.state = state or PlaybackState()

    def set_state(self, state: PlaybackState) -> None:
        self.state = state
        self._publish(state)

    def advance(self, delta_ms: int) -> PlaybackState:
        next_position = min(self.state.duration_ms, self.state.position_ms + max(0, delta_ms))
        self.set_state(replace(self.state, position_ms=next_position))
        return self.state
