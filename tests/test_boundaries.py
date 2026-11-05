from __future__ import annotations

import unittest

from amlyrics.apple_music_adapter import AppleMusicInspector
from amlyrics.contracts import PlaybackState
from amlyrics.windows_smtc import WindowsSmtcPlaybackMonitor


class BoundaryTests(unittest.TestCase):
    def test_smtc_bridge_is_explicitly_capability_limited(self) -> None:
        monitor = WindowsSmtcPlaybackMonitor()
        received = []
        monitor.subscribe(received.append)
        monitor.emit_for_bridge(PlaybackState(title="Bridge test"))
        self.assertEqual(received[0].title, "Bridge test")
        self.assertIn("bridge", monitor.capability.reason.casefold())

    def test_apple_music_inspector_is_observation_only(self) -> None:
        inspector = AppleMusicInspector()
        with self.assertRaises(RuntimeError):
            inspector.inject_button()
        observation = inspector.inspect()
        self.assertIn(observation.adapter_mode, {"unsupported", "observation-only"})


if __name__ == "__main__":
    unittest.main()
