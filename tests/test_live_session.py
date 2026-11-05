from __future__ import annotations
import unittest
from concurrent.futures import Future
from dataclasses import replace
from amlyrics.contracts import LyricsResult, LyricLine, PlaybackState
from amlyrics.live_session import LiveSession
from amlyrics.playback import PlaybackMonitor

class Monitor(PlaybackMonitor):
    def __init__(self):
        super().__init__()
        self.running = False
    def start(self): self.running = True
    def stop(self): self.running = False
    def send(self, state): self._publish(state)

class ManualExecutor:
    def __init__(self): self.jobs = []
    def submit(self, callback, state):
        future = Future()
        future.set_running_or_notify_cancel()
        self.jobs.append((future, callback, state))
        return future
    def complete(self, index):
        future, callback, state = self.jobs[index]
        future.set_result(callback(state))

def lyrics(state):
    return LyricsResult(state.track_key, "test", None,
                        (LyricLine(1000, 3000, state.title), LyricLine(3000, None, "next")),
                        confidence=1.0)

class LiveSessionTests(unittest.TestCase):
    def setUp(self):
        self.monitor = Monitor()
        self.executor = ManualExecutor()
        self.now = 0.0
        self.session = LiveSession(self.monitor, lyrics, executor=self.executor, clock=lambda: self.now)
        self.session.start()
        self.addCleanup(self.session.close)
        self.state = PlaybackState(title="A", artist="Artist", duration_ms=10000,
                                   position_ms=1500, is_playing=True, source_app="AppleMusic")
    def load(self):
        self.monitor.send(self.state)
        self.session.tick()
        self.executor.complete(0)
        return self.session.tick()
    def _missing_result_with_diagnostic(self, code):
        class MissingLookup:
            last_diagnostic=code
            def __call__(self, _state): return None
        self.session.lookup=MissingLookup()
        return self.load()
    def test_timeout_is_reported_instead_of_generic_missing_lyrics(self):
        self.assertIn("超时",self._missing_result_with_diagnostic("timeout").diagnostic)
    def test_offline_state_prompts_only_for_window_control(self):
        self.assertIn("启用在线同步",self._missing_result_with_diagnostic("disabled").diagnostic)
    def test_unknown_diagnostic_does_not_expose_untrusted_text(self):
        self.assertEqual(self._missing_result_with_diagnostic("untrusted-account-data").diagnostic,"暂无同步歌词")
    def test_real_position_selects_line_and_pause_freezes(self):
        self.assertEqual(self.load().frame.current_line.text, "A")
        self.monitor.send(replace(self.state, position_ms=3500, is_playing=False))
        view = self.session.tick()
        self.assertEqual(view.frame.current_line.text, "next")
        self.assertFalse(view.frame.is_playing)
        self.assertEqual(self.session.tick().frame.position_ms, 3500)
    def test_seek_backwards_uses_current_position(self):
        self.load()
        self.monitor.send(replace(self.state, position_ms=0))
        self.assertIsNone(self.session.tick().frame.current_line)
    def test_track_change_clears_old_lyrics_immediately(self):
        self.load()
        self.monitor.send(replace(self.state, title="B"))
        view = self.session.tick()
        self.assertIsNone(view.frame.current_line)
        self.assertEqual(view.source, "")
    def test_late_previous_track_result_cannot_replace_current(self):
        self.monitor.send(self.state)
        self.session.tick()
        self.monitor.send(replace(self.state, title="B"))
        self.session.tick()
        self.executor.complete(0)
        self.assertIsNone(self.session.tick().frame.current_line)
        self.executor.complete(1)
        self.assertEqual(self.session.tick().frame.current_line.text, "B")
    def test_disconnect_clears_pending_lookup(self):
        self.monitor.send(self.state)
        self.session.tick()
        self.monitor.send(PlaybackState())
        self.session.tick()
        self.executor.complete(0)
        self.assertIsNone(self.session.tick().frame.current_line)
    def test_wrong_song_result_is_rejected(self):
        self.session.lookup = lambda state: replace(lyrics(state), track_key=("other", "", ""))
        self.monitor.send(self.state)
        self.session.tick()
        self.executor.complete(0)
        self.assertIsNone(self.session.tick().frame.current_line)
    def test_many_playback_updates_coalesce_to_latest(self):
        for position in range(500):
            self.monitor.send(replace(self.state, position_ms=position))
        self.assertEqual(self.session.tick().state.position_ms, 499)
        self.assertEqual(len(self.executor.jobs), 1)
    def test_source_refresh_rejects_previous_inflight_result(self):
        self.monitor.send(self.state)
        self.session.tick()
        self.session.refresh_lyrics()
        self.executor.complete(0)
        self.assertIsNone(self.session.tick().frame.current_line)
        self.executor.complete(1)
        self.assertEqual(self.session.tick().frame.current_line.text, "A")
    def test_missing_lyrics_retry_after_backoff(self):
        self.session.lookup = lambda _state: None
        self.load()
        self.now = 29.0
        self.session.tick()
        self.assertEqual(len(self.executor.jobs), 1)
        self.now = 31.0
        self.session.tick()
        self.assertEqual(len(self.executor.jobs), 2)
    def test_successful_lyrics_do_not_retry(self):
        self.load()
        self.now = 1000.0
        self.session.tick()
        self.assertEqual(len(self.executor.jobs), 1)
    def test_expired_final_line_is_cleared(self):
        self.session.lookup = lambda state: replace(lyrics(state), lines=(LyricLine(1000, 2000, "short"),))
        self.load()
        self.monitor.send(replace(self.state, position_ms=2500))
        self.assertIsNone(self.session.tick().frame.current_line)
    def test_close_stops_and_unsubscribes(self):
        self.session.close()
        self.assertFalse(self.monitor.running)
        self.assertEqual(self.monitor._listeners, [])
        self.session.close()
        with self.assertRaises(RuntimeError): self.session.tick()

if __name__ == "__main__": unittest.main()
