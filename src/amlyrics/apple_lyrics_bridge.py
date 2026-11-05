"""Read-only, bounded polling of Apple Music's public UI Automation lyrics.

Worker subprocesses have no console window. This does not start playback, change
virtual desktops, activate Apple Music, access credentials, or invent timing.
"""
from __future__ import annotations
import json
from datetime import datetime, timezone
import math
import os
from pathlib import Path
import subprocess
import threading
from typing import Any
from .apple_window import AppleWindow, find_apple_music_window, physical_window_rect

_MAX_BYTES = 1_048_576

def _unavailable(reason: str) -> dict[str, Any]:
    return {"version": 1, "status": "unavailable", "reason": reason, "title": "", "subtitle": "",
            "lines": [], "timing_status": "not_exposed", "current_index": None,
            "observed_at": datetime.now(timezone.utc).isoformat()}

class AppleLyricsBridgeMonitor:
    def __init__(self, *, poll_interval: float = 2.0, timeout: float = 5.0) -> None:
        if not all(math.isfinite(n) and n > 0 for n in (poll_interval, timeout)):
            raise ValueError("poll interval and timeout must be finite and positive")
        self.poll_interval, self.timeout = poll_interval, timeout
        self._cancel = threading.Event()
        self._lock = threading.Lock()
        self._worker: threading.Thread | None = None
        self._process: subprocess.Popen | None = None
        self._snapshot = _unavailable("not_started")
        self.last_error: str | None = None

    @property
    def latest_snapshot(self) -> dict[str, Any]:
        with self._lock:
            return dict(self._snapshot)

    @property
    def is_running(self) -> bool:
        return bool(self._worker and self._worker.is_alive())

    def start(self) -> None:
        if self.is_running:
            return
        if os.name != "nt":
            raise RuntimeError("Windows is required")
        self._cancel.clear()
        self._worker = threading.Thread(target=self._run, name="apple-lyrics-probe", daemon=True)
        self._worker.start()

    def _read(self, window: AppleWindow) -> dict[str, Any]:
        bounds_before = physical_window_rect(window.hwnd)
        script = Path(__file__).with_name("bridge") / "apple_lyrics.ps1"
        powershell = Path(os.environ.get("SystemRoot", "C:/Windows")) / "System32/WindowsPowerShell/v1.0/powershell.exe"
        command = [str(powershell), "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-STA",
                   "-File", str(script), "-UiaRootHandle", str(window.uia_hwnd), "-TargetProcessId", str(window.process_id)]
        with self._lock:
            if self._cancel.is_set():
                return _unavailable("stopped")
            process = subprocess.Popen(command, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                                       stderr=subprocess.PIPE, creationflags=subprocess.CREATE_NO_WINDOW)
            self._process = process
        try:
            try:
                out, err = process.communicate(timeout=self.timeout)
            except subprocess.TimeoutExpired:
                process.kill()
                process.communicate(timeout=2)
                raise TimeoutError("Apple UI Automation probe timed out")
            if process.returncode:
                raise RuntimeError("Apple UI Automation probe failed")
            if len(out) > _MAX_BYTES:
                raise ValueError("Apple lyric snapshot exceeds limit")
            value = json.loads(out.decode("utf-8-sig"))
            if not isinstance(value, dict) or type(value.get("version")) is not int or value["version"] != 1:
                raise ValueError("Invalid Apple lyric snapshot")
            if value.get("process_id") != window.process_id or value.get("uia_root_handle") != window.uia_hwnd:
                raise ValueError("Apple lyric window identity changed")
            bounds_after = physical_window_rect(window.hwnd)
            value["native_window_rect"] = bounds_after
            if bounds_before != bounds_after:
                value["layout"] = {}
            return value
        finally:
            if process.poll() is None:
                process.kill()
            process.wait(timeout=2)
            for stream in (process.stdout, process.stderr):
                if stream:
                    stream.close()
            with self._lock:
                if self._process is process:
                    self._process = None

    def _run(self) -> None:
        while not self._cancel.is_set():
            try:
                window = find_apple_music_window()
                value = self._read(window) if window else _unavailable("verified_apple_window_unavailable")
                self.last_error = None
            except Exception as exc:
                value = _unavailable("probe_failed")
                self.last_error = type(exc).__name__
            with self._lock:
                if not self._cancel.is_set():
                    self._snapshot = value
            self._cancel.wait(self.poll_interval)

    def stop(self) -> None:
        self._cancel.set()
        with self._lock:
            process = self._process
            self._snapshot = _unavailable("stopped")
        if process is not None and process.poll() is None:
            try:
                process.terminate()
            except ProcessLookupError:
                pass
        if self._worker is not None and self._worker is not threading.current_thread():
            self._worker.join(timeout=self.timeout+3)
        if self.is_running:
            raise RuntimeError("Apple lyric worker did not stop within its deadline")
