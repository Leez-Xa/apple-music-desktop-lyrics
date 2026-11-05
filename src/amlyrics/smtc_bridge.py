"""Read-only Windows SMTC through the installed Windows PowerShell 5.1 runtime.

The bridge wire format is internal, not the public IPC envelope. Positions are
raw samples: neither LastUpdatedTime nor PlaybackRate is used to extrapolate.
Callbacks run on a background thread and must return promptly. Call stop() from
an application's shutdown path (or use the monitor as a context manager).
"""
from __future__ import annotations

from collections import deque
from dataclasses import dataclass
from datetime import datetime, timezone
import json
import math
import os
from pathlib import Path
import queue
import subprocess
import threading
import time
from typing import Any

from .contracts import PlaybackState
from .playback import PlaybackMonitor

APPLE_MUSIC_SOURCE = "AppleInc.AppleMusicWin_nzyj5cx40ttqa!App"
MAX_LINE_BYTES = 65536
_SCRIPT_PATH = Path(__file__).with_name("bridge") / "smtc.ps1"


@dataclass(frozen=True, slots=True)
class SmtcBridgeCapability:
    supported: bool
    implementation: str
    reason: str


class SmtcBridgeError(RuntimeError):
    """Bridge launch, wire-format, or native runtime failure."""


def _powershell_path() -> Path:
    return Path(os.environ.get("SystemRoot", r"C:\Windows")) / "System32/WindowsPowerShell/v1.0/powershell.exe"


def _bridge_command(*, once: bool = False, poll_interval_ms: int = 500,
                    async_timeout_ms: int = 5000) -> list[str]:
    command = [str(_powershell_path()), "-NoLogo", "-NoProfile", "-NonInteractive",
               "-ExecutionPolicy", "Bypass", "-File", str(_SCRIPT_PATH),
               "-PollIntervalMs", str(poll_interval_ms),
               "-AsyncTimeoutMs", str(async_timeout_ms)]
    if once:
        command.append("-Once")
    return command


def _launch(command: list[str]) -> subprocess.Popen[bytes]:
    return subprocess.Popen(command, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                            stderr=subprocess.PIPE, bufsize=0,
                            creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)


def _terminate_process(process: subprocess.Popen[bytes], timeout: float) -> None:
    # Always wait, including a child that has already exited. No child processes
    # are launched by smtc.ps1, so this directly owns the entire bridge lifetime.
    if process.poll() is None:
        try:
            process.terminate()
        except ProcessLookupError:
            pass
    try:
        process.wait(timeout=timeout)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=timeout)


def _timestamp(value: Any, field: str) -> datetime:
    if not isinstance(value, str):
        raise SmtcBridgeError(f"Missing or invalid {field}")
    try:
        result = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as exc:
        raise SmtcBridgeError(f"Invalid {field}") from exc
    if result.tzinfo is None:
        raise SmtcBridgeError(f"{field} must include a timezone")
    return result


def _number(value: Any, field: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
        raise SmtcBridgeError(f"Invalid finite numeric {field}")
    return float(value)


def _mapping(value: Any, field: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise SmtcBridgeError(f"Invalid {field}")
    return value


def playback_state_from_snapshot(snapshot: dict[str, Any]) -> PlaybackState:
    """Map raw samples to the existing model without inventing elapsed playback.

    Raw timestamps, playback rate, and seek ranges remain in last_snapshot.
    A nonzero timeline StartTime becomes position/duration relative to start.
    An unchanged, even very old LastUpdatedTime never advances the position.
    """
    snapshot = _mapping(snapshot, "snapshot")
    if (type(snapshot.get("version")) is not int or snapshot["version"] != 1
            or snapshot.get("type") != "snapshot"):
        raise SmtcBridgeError("Unsupported SMTC snapshot protocol")
    observed_at = _timestamp(snapshot.get("observed_at"), "observed_at")
    if not isinstance(snapshot.get("sessions"), list):
        raise SmtcBridgeError("Invalid session list")
    status = snapshot.get("status")
    if status in ("disconnected", "error"):
        return PlaybackState(observed_at=observed_at)
    if status != "connected":
        raise SmtcBridgeError("Unknown SMTC connection status")
    source = snapshot.get("selected_source")
    if not isinstance(source, str) or source.casefold() != APPLE_MUSIC_SOURCE.casefold():
        raise SmtcBridgeError("Refusing a non-Apple Music session")
    if not any(isinstance(row, dict) and isinstance(row.get("source_app"), str)
               and row["source_app"].casefold() == source.casefold()
               for row in snapshot["sessions"]):
        raise SmtcBridgeError("Selected Apple Music session is absent from session list")
    media = _mapping(snapshot.get("media"), "media")
    timeline = _mapping(snapshot.get("timeline"), "timeline")
    playback = _mapping(snapshot.get("playback"), "playback")
    _timestamp(timeline.get("last_updated_time"), "timeline.last_updated_time")
    start = _number(timeline.get("start_ms"), "timeline.start_ms")
    end = _number(timeline.get("end_ms"), "timeline.end_ms")
    position = _number(timeline.get("position_ms"), "timeline.position_ms")
    if end < start:
        raise SmtcBridgeError("Timeline end precedes start")
    duration = int(end - start)
    relative_position = max(0, int(position - start))
    if duration > 0:
        relative_position = min(duration, relative_position)
    if playback.get("rate") is not None:
        _number(playback["rate"], "playback.rate")
    for field in ("title", "artist", "album"):
        if not isinstance(media.get(field), str):
            raise SmtcBridgeError(f"Invalid media.{field}")
    return PlaybackState(title=media["title"], artist=media["artist"], album=media["album"],
                         duration_ms=duration, position_ms=relative_position,
                         is_playing=playback.get("status") == "Playing", source_app=source,
                         observed_at=observed_at)


def _decode_line(raw: bytes) -> dict[str, Any]:
    if len(raw) > MAX_LINE_BYTES or not raw.endswith(b"\n"):
        raise SmtcBridgeError("Oversized or truncated SMTC JSON line")
    try:
        snapshot = json.loads(raw.decode("utf-8", errors="strict"))
    except (UnicodeDecodeError, ValueError) as exc:
        raise SmtcBridgeError(f"Invalid UTF-8 SMTC JSON line: {exc}") from exc
    playback_state_from_snapshot(snapshot)
    return snapshot


def inspect_sessions(timeout: float = 10.0) -> dict[str, Any]:
    """Run one bounded read-only probe; no Apple session returns disconnected.

    The returned dict includes all source IDs and the selected Apple's media,
    timeline, and playback info. Other players' metadata is never selected.
    Raises TimeoutError or SmtcBridgeError instead of returning stale data.
    """
    if not math.isfinite(timeout) or timeout <= 0:
        raise ValueError("timeout must be finite and positive")
    capability = SmtcBridgeMonitor.detect_capability()
    if not capability.supported:
        raise SmtcBridgeError(capability.reason)
    command = _bridge_command(once=True, async_timeout_ms=max(100, min(5000, int(timeout * 350))))
    try:
        process = _launch(command)
    except OSError as exc:
        raise SmtcBridgeError(f"Cannot launch SMTC inspection: {exc}") from exc
    try:
        try:
            stdout, stderr = process.communicate(timeout=timeout)
        except subprocess.TimeoutExpired as exc:
            _terminate_process(process, 2.0)
            raise TimeoutError(f"SMTC inspection exceeded {timeout:g} seconds") from exc
        if process.returncode:
            raise SmtcBridgeError(stderr.decode("utf-8", errors="replace").strip()
                                  or f"SMTC inspection exited with code {process.returncode}")
        lines = stdout.splitlines(keepends=True)
        if len(lines) != 1:
            raise SmtcBridgeError("SMTC inspection must return exactly one JSON line")
        snapshot = _decode_line(lines[0])
        if snapshot["status"] == "error":
            raise SmtcBridgeError(snapshot.get("message") or snapshot.get("reason") or "SMTC read failed")
        return snapshot
    finally:
        _terminate_process(process, 2.0)
        if process.stdout:
            process.stdout.close()
        if process.stderr:
            process.stderr.close()


class SmtcBridgeMonitor(PlaybackMonitor):
    """Nonblocking monitor; call stop() to terminate + wait for its owned child.

    capability reports installed prerequisites, not the presence of an Apple
    session. Read last_snapshot['status'], last_error, and diagnostics for live
    connection/runtime diagnostics. A failed/disconnected read publishes an
    empty PlaybackState so listeners cannot retain a previous player's state.
    """

    def __init__(self, *, poll_interval_ms: int = 500, async_timeout_ms: int = 5000,
                 read_timeout: float = 15.0, stop_timeout: float = 2.0) -> None:
        super().__init__()
        if not 50 <= poll_interval_ms <= 60000 or not 100 <= async_timeout_ms <= 60000:
            raise ValueError("poll_interval_ms/async_timeout_ms are outside bridge bounds")
        if (not math.isfinite(read_timeout) or read_timeout <= poll_interval_ms / 1000
                or not math.isfinite(stop_timeout) or stop_timeout <= 0):
            raise ValueError("read_timeout must exceed poll interval; timeouts must be finite and positive")
        self.poll_interval_ms = poll_interval_ms
        self.async_timeout_ms = async_timeout_ms
        self.read_timeout = read_timeout
        self.stop_timeout = stop_timeout
        self.capability = self.detect_capability()
        self.state = PlaybackState()
        self.last_snapshot: dict[str, Any] | None = None
        self.last_error: str | None = None
        self._diagnostics: deque[str] = deque(maxlen=50)
        self._process: subprocess.Popen[bytes] | None = None
        self._worker: threading.Thread | None = None
        self._readers: list[threading.Thread] = []
        self._cancel = threading.Event()
        self._lifecycle_lock = threading.Lock()
        self._reap_lock = threading.Lock()
        self._stopping = False

    @staticmethod
    def detect_capability() -> SmtcBridgeCapability:
        if os.name != "nt":
            return SmtcBridgeCapability(False, "unavailable", "SMTC bridge requires Windows")
        if not _powershell_path().is_file() or not _SCRIPT_PATH.is_file():
            return SmtcBridgeCapability(False, "unavailable", "PowerShell 5.1 or smtc.ps1 bridge is missing")
        return SmtcBridgeCapability(True, "native-powershell-winrt",
                                    "Installed bridge prerequisites found; runtime errors appear in last_error")

    @property
    def diagnostics(self) -> tuple[str, ...]:
        return tuple(self._diagnostics)

    @property
    def is_running(self) -> bool:
        return self._worker is not None and self._worker.is_alive()

    def start(self) -> None:
        with self._lifecycle_lock:
            if self._stopping:
                raise SmtcBridgeError("SMTC bridge is still stopping")
            if self.is_running:
                return
            if not self.capability.supported:
                raise SmtcBridgeError(self.capability.reason)
            self._cancel = threading.Event()
            self.last_error = None
            self.last_snapshot = None
            self._diagnostics.clear()
            # Only process creation occurs here; all WinRT reads and callbacks
            # are asynchronous to the caller of start().
            try:
                self._process = _launch(_bridge_command(poll_interval_ms=self.poll_interval_ms,
                                                       async_timeout_ms=self.async_timeout_ms))
            except OSError as exc:
                self.last_error = str(exc)
                raise SmtcBridgeError(f"Cannot launch SMTC bridge: {exc}") from exc
            try:
                self._worker = threading.Thread(target=self._consume,
                                                args=(self._process, self._cancel),
                                                name="amlyrics-smtc", daemon=True)
                self._worker.start()
            except Exception as exc:
                self._cancel.set()
                self._reap(self._process)
                if self._process.stdout:
                    self._process.stdout.close()
                if self._process.stderr:
                    self._process.stderr.close()
                self._worker = None
                self.last_error = f"Cannot start SMTC reader: {exc}"
                raise SmtcBridgeError(self.last_error) from exc

    def _reap(self, process: subprocess.Popen[bytes]) -> None:
        with self._reap_lock:
            _terminate_process(process, self.stop_timeout)

    def stop(self) -> None:
        with self._lifecycle_lock:
            self._stopping = True
            self._cancel.set()
            process, worker = self._process, self._worker
        try:
            if process is not None:
                self._reap(process)
            if worker is not None and worker is not threading.current_thread():
                worker.join(timeout=2 * self.stop_timeout + 1)
        finally:
            with self._lifecycle_lock:
                self._stopping = False

    def _publish_state(self, state: PlaybackState) -> None:
        self.state = state
        try:
            self._publish(state)
        except Exception as exc:
            self.last_error = f"Playback listener failed: {exc}"
            self._diagnostics.append(self.last_error)

    def _consume(self, process: subprocess.Popen[bytes], cancel: threading.Event) -> None:
        incoming: queue.Queue[bytes | Exception | None] = queue.Queue(maxsize=32)

        def enqueue(value: bytes | Exception | None) -> None:
            while not cancel.is_set():
                try:
                    incoming.put(value, timeout=0.1)
                    return
                except queue.Full:
                    pass

        def read_stdout() -> None:
            try:
                assert process.stdout is not None
                while not cancel.is_set():
                    raw = process.stdout.readline(MAX_LINE_BYTES + 1)
                    if not raw:
                        enqueue(None)
                        return
                    enqueue(raw)
                    if len(raw) > MAX_LINE_BYTES:
                        return
            except Exception as exc:
                enqueue(exc)

        def read_stderr() -> None:
            try:
                assert process.stderr is not None
                while not cancel.is_set():
                    raw = process.stderr.readline(MAX_LINE_BYTES + 1)
                    if not raw:
                        return
                    self._diagnostics.append(raw.decode("utf-8", errors="replace").rstrip())
            except (OSError, ValueError):
                return

        self._readers = [threading.Thread(target=read_stdout, name="amlyrics-smtc-stdout", daemon=True),
                         threading.Thread(target=read_stderr, name="amlyrics-smtc-stderr", daemon=True)]
        try:
            for reader in self._readers:
                reader.start()
            deadline = time.monotonic() + self.read_timeout
            while not cancel.is_set():
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise TimeoutError(f"SMTC bridge produced no snapshot for {self.read_timeout:g} seconds")
                try:
                    raw = incoming.get(timeout=min(0.1, remaining))
                except queue.Empty:
                    continue
                if cancel.is_set():
                    break
                if raw is None:
                    raise SmtcBridgeError(f"SMTC bridge stdout closed (exit code {process.poll()})")
                if isinstance(raw, Exception):
                    raise SmtcBridgeError(f"SMTC stdout read failed: {raw}") from raw
                snapshot = _decode_line(raw)
                state = playback_state_from_snapshot(snapshot)
                age = (datetime.now(timezone.utc) - state.observed_at).total_seconds()
                if age > self.read_timeout or age < -5:
                    raise SmtcBridgeError("SMTC snapshot observation time is stale or in the future")
                self.last_snapshot = snapshot
                self.last_error = (snapshot.get("message") or snapshot.get("reason")) if snapshot["status"] == "error" else None
                deadline = time.monotonic() + self.read_timeout
                self._publish_state(state)
        except Exception as exc:
            if not cancel.is_set():
                self.last_error = str(exc)
                self._diagnostics.append(str(exc))
                self.last_snapshot = {"version": 1, "type": "snapshot", "status": "error",
                                      "reason": "bridge_failed", "message": str(exc), "sessions": [],
                                      "observed_at": datetime.now(timezone.utc).isoformat()}
                self._publish_state(PlaybackState())
        finally:
            cancel.set()
            try:
                self._reap(process)
            finally:
                for reader in self._readers:
                    if reader.ident is not None:
                        reader.join(timeout=self.stop_timeout)
                if process.stdout:
                    process.stdout.close()
                if process.stderr:
                    process.stderr.close()

    def __enter__(self) -> SmtcBridgeMonitor:
        self.start()
        return self

    def __exit__(self, *_: Any) -> None:
        self.stop()
