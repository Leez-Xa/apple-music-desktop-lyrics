from __future__ import annotations

import base64
import copy
from datetime import datetime, timedelta, timezone
import json
import os
from pathlib import Path
import subprocess
import sys
import threading
import time
import unittest
from unittest.mock import patch

from amlyrics import smtc_bridge as bridge
from amlyrics.contracts import PlaybackState
from amlyrics.playback import PlaybackMonitor


def snapshot(*, status="connected", playback_status="Playing"):
    return {
        "version": 1, "type": "snapshot", "status": status,
        "reason": None if status == "connected" else "apple_music_session_not_found",
        "observed_at": datetime.now(timezone.utc).isoformat(),
        "sessions": [{"source_app": bridge.APPLE_MUSIC_SOURCE,
                      "is_apple_music": True, "is_current": True}],
        "manager_current_source": bridge.APPLE_MUSIC_SOURCE,
        "selected_source": bridge.APPLE_MUSIC_SOURCE if status == "connected" else None,
        "media": {"title": "流沙", "artist": "陶喆 — 陶喆同名专辑", "album": ""},
        "timeline": {"last_updated_time": "2001-01-01T00:00:00+00:00",
                     "position_ms": 123000, "start_ms": 0, "end_ms": 242000,
                     "min_seek_ms": 0, "max_seek_ms": 242000},
        "playback": {"status": playback_status, "rate": 1},
    }


def encoded_snapshot(value=None):
    return json.dumps(snapshot() if value is None else value, ensure_ascii=False).encode("utf-8") + b"\n"


class SnapshotTests(unittest.TestCase):
    def test_existing_playback_state_and_unicode_are_preserved(self):
        state = bridge.playback_state_from_snapshot(snapshot())
        self.assertIsInstance(state, PlaybackState)
        self.assertEqual(state.title, "流沙")
        self.assertEqual(state.artist, "陶喆 — 陶喆同名专辑")
        self.assertEqual((state.duration_ms, state.position_ms), (242000, 123000))
        self.assertEqual(state.source_app, bridge.APPLE_MUSIC_SOURCE)
        self.assertTrue(state.is_playing)

    def test_old_last_updated_time_and_rate_never_extrapolate(self):
        first = snapshot()
        first["playback"]["rate"] = 3.5
        second = copy.deepcopy(first)
        second["observed_at"] = (datetime.now(timezone.utc) + timedelta(days=7)).isoformat()
        self.assertEqual(bridge.playback_state_from_snapshot(first).position_ms, 123000)
        self.assertEqual(bridge.playback_state_from_snapshot(second).position_ms, 123000)

    def test_pause_and_null_rate_preserve_raw_position(self):
        value = snapshot(playback_status="Paused")
        value["playback"]["rate"] = None
        state = bridge.playback_state_from_snapshot(value)
        self.assertFalse(state.is_playing)
        self.assertEqual(state.position_ms, 123000)

    def test_start_offset_and_outside_range_are_normalized(self):
        value = snapshot()
        value["timeline"].update(start_ms=10000, end_ms=20000, position_ms=13000)
        state = bridge.playback_state_from_snapshot(value)
        self.assertEqual((state.duration_ms, state.position_ms), (10000, 3000))
        for raw, expected in ((-20, 0), (30000, 10000)):
            value["timeline"]["position_ms"] = raw
            self.assertEqual(bridge.playback_state_from_snapshot(value).position_ms, expected)

    def test_disconnected_never_selects_another_player(self):
        value = snapshot(status="disconnected")
        value["sessions"] = [{"source_app": "Spotify.exe", "is_current": True}]
        value["manager_current_source"] = "Spotify.exe"
        value["media"] = value["timeline"] = value["playback"] = None
        state = bridge.playback_state_from_snapshot(value)
        self.assertEqual(state.source_app, "")
        self.assertEqual(state.title, "")
        self.assertFalse(state.is_playing)

    def test_nonapple_connected_or_absent_session_is_rejected(self):
        for source, sessions in (("Spotify.exe", [{"source_app": "Spotify.exe"}]),
                                 (bridge.APPLE_MUSIC_SOURCE, []),
                                 ("Fake.AppleMusic.App", [])):
            with self.subTest(source=source, sessions=sessions):
                value = snapshot()
                value.update(selected_source=source, sessions=sessions)
                with self.assertRaises(bridge.SmtcBridgeError):
                    bridge.playback_state_from_snapshot(value)

    def test_invalid_protocol_and_timestamps_are_rejected(self):
        for key, value in (("version", 2), ("version", True), ("type", "other"),
                           ("status", "other"), ("sessions", None),
                           ("observed_at", "2026-11-05T12:00:00")):
            with self.subTest(key=key, value=value):
                data = snapshot()
                data[key] = value
                with self.assertRaises(bridge.SmtcBridgeError):
                    bridge.playback_state_from_snapshot(data)

    def test_invalid_timeline_numbers_are_rejected(self):
        for value in (float("nan"), float("inf"), True, "100", None):
            with self.subTest(value=value):
                data = snapshot()
                data["timeline"]["position_ms"] = value
                with self.assertRaises(bridge.SmtcBridgeError):
                    bridge.playback_state_from_snapshot(data)
        data = snapshot()
        data["timeline"].update(start_ms=300000, end_ms=100)
        with self.assertRaises(bridge.SmtcBridgeError):
            bridge.playback_state_from_snapshot(data)

    def test_invalid_utf8_truncation_and_oversize_are_rejected(self):
        for raw in (b"\xff\n", b"{}", b"x" * (bridge.MAX_LINE_BYTES + 1) + b"\n", b"not-json\n"):
            with self.subTest(raw=raw[:20]):
                with self.assertRaises(bridge.SmtcBridgeError):
                    bridge._decode_line(raw)


class ProcessTests(unittest.TestCase):
    def setUp(self):
        self.capability = patch.object(bridge.SmtcBridgeMonitor, "detect_capability",
                                      return_value=bridge.SmtcBridgeCapability(True, "test", "test bridge"))
        self.capability.start()
        self.addCleanup(self.capability.stop)

    def monitor(self, child_code, **options):
        command = patch.object(bridge, "_bridge_command", return_value=[sys.executable, "-u", "-c", child_code])
        command.start()
        self.addCleanup(command.stop)
        monitor = bridge.SmtcBridgeMonitor(poll_interval_ms=50, **options)
        self.addCleanup(monitor.stop)
        return monitor

    def sample_code(self, value=None):
        raw = encoded_snapshot(value)
        return f"import sys,time; sys.stdout.buffer.write({raw!r}); sys.stdout.flush(); time.sleep(10)"

    def test_start_nonblocking_publish_and_stop_reaps_all_threads(self):
        monitor = self.monitor("import time; time.sleep(0.4); " + self.sample_code())
        arrived = threading.Event()
        received = []
        monitor.subscribe(lambda state: (received.append(state), arrived.set()))
        started = time.monotonic()
        monitor.start()
        self.assertLess(time.monotonic() - started, 0.35)
        self.assertFalse(arrived.is_set())
        self.assertIsInstance(monitor, PlaybackMonitor)
        process = monitor._process
        self.assertTrue(arrived.wait(3))
        self.assertEqual(received[0].title, "流沙")
        self.assertEqual(monitor.last_snapshot["playback"]["rate"], 1)
        monitor.stop()
        monitor.stop()
        self.assertIsNotNone(process.poll())
        self.assertFalse(monitor.is_running)
        self.assertTrue(all(not reader.is_alive() for reader in monitor._readers))
        self.assertTrue(process.stdout.closed)
        self.assertTrue(process.stderr.closed)

    def test_reader_start_failure_still_reaps_created_child(self):
        monitor = self.monitor("import time;time.sleep(10)")
        with patch.object(threading.Thread, "start", side_effect=RuntimeError("thread unavailable")):
            with self.assertRaisesRegex(bridge.SmtcBridgeError, "thread unavailable"):
                monitor.start()
        self.assertIsNotNone(monitor._process.poll())
        self.assertTrue(monitor._process.stdout.closed)
        self.assertTrue(monitor._process.stderr.closed)
        self.assertFalse(monitor.is_running)

    def test_start_is_idempotent_and_restart_creates_a_new_child(self):
        monitor = self.monitor(self.sample_code())
        monitor.start()
        first = monitor._process
        monitor.start()
        self.assertIs(monitor._process, first)
        monitor.stop()
        monitor.start()
        self.assertIsNot(monitor._process, first)
        monitor.stop()
        self.assertIsNotNone(monitor._process.poll())

    def test_stop_from_listener_does_not_join_itself(self):
        monitor = self.monitor(self.sample_code())
        stopped = threading.Event()
        def receive(state):
            monitor.stop()
            stopped.set()
        monitor.subscribe(receive)
        monitor.start()
        self.assertTrue(stopped.wait(3))
        monitor.stop()
        self.assertIsNotNone(monitor._process.poll())
        self.assertFalse(monitor.is_running)

    def test_stalled_child_times_out_and_is_reaped(self):
        monitor = self.monitor("import time; time.sleep(10)", read_timeout=0.2)
        arrived = threading.Event()
        monitor.subscribe(lambda _: arrived.set())
        monitor.start()
        self.assertTrue(arrived.wait(3))
        monitor.stop()
        self.assertIn("no snapshot", monitor.last_error)
        self.assertEqual(monitor.last_snapshot["status"], "error")
        self.assertEqual(monitor.state.source_app, "")
        self.assertIsNotNone(monitor._process.poll())

    def test_stderr_is_drained_without_contaminating_json(self):
        monitor = self.monitor("import sys; sys.stderr.write('diagnostic\\n'*20000); sys.stderr.flush(); "
                               + self.sample_code())
        arrived = threading.Event()
        monitor.subscribe(lambda _: arrived.set())
        monitor.start()
        self.assertTrue(arrived.wait(3))
        self.assertEqual(monitor.state.title, "流沙")
        self.assertTrue(monitor.diagnostics)
        self.assertLessEqual(len(monitor.diagnostics), 50)
        monitor.stop()

    def test_invalid_output_and_exit_clear_previous_state(self):
        for child in ("import sys,time;sys.stdout.buffer.write(b'\\xff\\n');sys.stdout.flush();time.sleep(10)",
                      "import sys;sys.exit(7)"):
            with self.subTest(child=child):
                monitor = self.monitor(child)
                arrived = threading.Event()
                monitor.subscribe(lambda _: arrived.set())
                monitor.start()
                self.assertTrue(arrived.wait(3))
                monitor.stop()
                self.assertEqual(monitor.state.title, "")
                self.assertEqual(monitor.last_snapshot["status"], "error")
                self.assertTrue(monitor.last_error)

    def test_old_observation_timestamp_is_rejected_but_old_timeline_is_not(self):
        value = snapshot()
        value["observed_at"] = (datetime.now(timezone.utc) - timedelta(days=1)).isoformat()
        monitor = self.monitor(self.sample_code(value))
        arrived = threading.Event()
        monitor.subscribe(lambda _: arrived.set())
        monitor.start()
        self.assertTrue(arrived.wait(3))
        monitor.stop()
        self.assertIn("stale", monitor.last_error)
        self.assertEqual(monitor.state.source_app, "")

    def test_inspect_returns_one_read_only_snapshot(self):
        raw = encoded_snapshot()
        child = f"import sys;sys.stdout.buffer.write({raw!r});sys.stdout.flush()"
        with patch.object(bridge, "_bridge_command", return_value=[sys.executable, "-u", "-c", child]) as command:
            result = bridge.inspect_sessions(timeout=3)
        self.assertEqual(result["media"]["title"], "流沙")
        self.assertTrue(command.call_args.kwargs["once"])

    def test_inspect_timeout_terminates_and_waits(self):
        launched = []
        original_launch = bridge._launch
        def launch(command):
            process = original_launch(command)
            launched.append(process)
            return process
        with patch.object(bridge, "_bridge_command", return_value=[sys.executable, "-u", "-c", "import time;time.sleep(10)"]), \
                patch.object(bridge, "_launch", side_effect=launch):
            with self.assertRaises(TimeoutError):
                bridge.inspect_sessions(timeout=0.2)
        self.assertEqual(len(launched), 1)
        self.assertIsNotNone(launched[0].poll())
        self.assertTrue(launched[0].stdout.closed)
        self.assertTrue(launched[0].stderr.closed)

    def test_inspect_runtime_failure_uses_stderr(self):
        child = "import sys;sys.stderr.write('native WinRT failure');sys.exit(2)"
        with patch.object(bridge, "_bridge_command", return_value=[sys.executable, "-u", "-c", child]):
            with self.assertRaisesRegex(bridge.SmtcBridgeError, "native WinRT failure"):
                bridge.inspect_sessions(timeout=3)

    def test_kill_fallback_still_waits(self):
        from unittest.mock import Mock
        process = Mock()
        process.poll.return_value = None
        process.wait.side_effect = [subprocess.TimeoutExpired("bridge", 0.1), 1]
        bridge._terminate_process(process, 0.1)
        process.terminate.assert_called_once()
        process.kill.assert_called_once()
        self.assertEqual(process.wait.call_count, 2)

    def test_unavailable_capability_refuses_to_launch(self):
        capability = bridge.SmtcBridgeCapability(False, "unavailable", "missing bridge runtime")
        with patch.object(bridge.SmtcBridgeMonitor, "detect_capability", return_value=capability), \
                patch.object(bridge, "_launch") as launch:
            with self.assertRaisesRegex(bridge.SmtcBridgeError, "missing bridge runtime"):
                bridge.SmtcBridgeMonitor().start()
            with self.assertRaises(bridge.SmtcBridgeError):
                bridge.inspect_sessions()
            launch.assert_not_called()


@unittest.skipUnless(os.name == "nt" and bridge._powershell_path().is_file(), "Windows PowerShell required")
class PowerShellSelectionTests(unittest.TestCase):
    def run_functions(self, body):
        script_path = str(bridge._SCRIPT_PATH).replace("'", "''")
        preamble = r"""
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$tokens = $null; $parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile('SCRIPT_PATH', [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
$functions = $ast.FindAll({param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst]}, $true)
foreach ($function in $functions) { . ([scriptblock]::Create($function.Extent.Text)) }
$other = [pscustomobject]@{ SourceAppUserModelId = 'Spotify.exe' }
$other | Add-Member ScriptMethod TryGetMediaPropertiesAsync { throw 'Other player metadata must not be read' }
$manager = [pscustomobject]@{}
$manager | Add-Member ScriptMethod GetCurrentSession { return $other }
""".replace("SCRIPT_PATH", script_path)
        encoded = base64.b64encode((preamble + body).encode("utf-16le")).decode("ascii")
        result = subprocess.run([str(bridge._powershell_path()), "-NoLogo", "-NoProfile", "-NonInteractive",
                                 "-EncodedCommand", encoded], capture_output=True, timeout=8,
                                creationflags=subprocess.CREATE_NO_WINDOW)
        self.assertEqual(result.returncode, 0, result.stderr.decode("utf-8", errors="replace"))
        self.assertEqual(result.stderr, b"")
        return json.loads(result.stdout.decode("utf-8"))

    def test_no_apple_emits_disconnected_even_when_other_player_is_current(self):
        result = self.run_functions(r"""
$manager | Add-Member ScriptMethod GetSessions { return @($other) }
Write-JsonLine (Read-Snapshot $manager)
""")
        self.assertEqual(result["status"], "disconnected")
        self.assertEqual(result["reason"], "apple_music_session_not_found")
        self.assertEqual(result["sessions"][0]["source_app"], "Spotify.exe")
        self.assertIsNone(result["selected_source"])
        self.assertIsNone(result["media"])
        self.assertEqual(bridge.playback_state_from_snapshot(result).title, "")

    def test_apple_selected_when_system_current_session_is_other_player(self):
        result = self.run_functions(r"""
$apple = [pscustomobject]@{ SourceAppUserModelId = 'AppleInc.AppleMusicWin_nzyj5cx40ttqa!App' }
$apple | Add-Member ScriptMethod TryGetMediaPropertiesAsync { return 'fake-op' }
$apple | Add-Member ScriptMethod GetTimelineProperties {
    return [pscustomobject]@{ LastUpdatedTime = [DateTimeOffset]::UtcNow;
        Position = [TimeSpan]::FromSeconds(12); StartTime = [TimeSpan]::Zero;
        EndTime = [TimeSpan]::FromSeconds(100); MinSeekTime = [TimeSpan]::Zero;
        MaxSeekTime = [TimeSpan]::FromSeconds(100) }
}
$apple | Add-Member ScriptMethod GetPlaybackInfo { return [pscustomobject]@{PlaybackStatus='Paused'; PlaybackRate=$null} }
function Wait-WinRt($Operation, [Type]$ResultType) { return [pscustomobject]@{Title='Apple title';Artist='Artist';AlbumTitle=''} }
$script:mediaType = [object]
$manager | Add-Member ScriptMethod GetSessions { return @($other, $apple) }
Write-JsonLine (Read-Snapshot $manager)
""")
        self.assertEqual(result["status"], "connected")
        self.assertEqual(result["manager_current_source"], "Spotify.exe")
        self.assertEqual(result["selected_source"], bridge.APPLE_MUSIC_SOURCE)
        self.assertEqual(result["media"]["title"], "Apple title")
        self.assertIsNone(result["playback"]["rate"])
        self.assertEqual(bridge.playback_state_from_snapshot(result).position_ms, 12000)


if __name__ == "__main__":
    unittest.main()
