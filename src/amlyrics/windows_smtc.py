from __future__ import annotations

import os
from dataclasses import dataclass

from .contracts import PlaybackState
from .playback import PlaybackMonitor


@dataclass(frozen=True, slots=True)
class SmtcCapability:
    supported: bool
    implementation: str
    reason: str


class WindowsSmtcPlaybackMonitor(PlaybackMonitor):
    """Safe boundary for the Windows SMTC implementation.

    Python's standard library does not expose Windows.Media.Control. This class
    reports capability instead of pretending to read Apple Music. A compiled
    WinRT bridge can call emit_for_bridge() without changing service contracts.
    """

    def __init__(self) -> None:
        super().__init__()
        self.capability = self.detect_capability()

    @staticmethod
    def detect_capability() -> SmtcCapability:
        if os.name != "nt":
            return SmtcCapability(False, "unavailable", "SMTC is only available on Windows")
        return SmtcCapability(False, "boundary-only", "Windows.Media.Control bridge is not bundled yet")

    def start(self) -> None:
        if not self.capability.supported:
            raise RuntimeError(self.capability.reason)

    def stop(self) -> None:
        return None

    def emit_for_bridge(self, state: PlaybackState) -> None:
        self._publish(state)
