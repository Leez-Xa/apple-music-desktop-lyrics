"""Version-gated, read-only probes derived from the 2026-11-05 UIA evidence.

No invocation, focus changes, XAML edits, DLL injection or client patching.
UIA positions can be offscreen while minimized and are not attach coordinates.
The probe establishes transport controls, not availability of Apple lyrics.
"""
from __future__ import annotations
from dataclasses import dataclass
import json
import math
import os
from pathlib import Path
import subprocess
from typing import Any

VERIFIED_FILE_VERSION = "1.7.0.155"
VERIFIED_PACKAGE_DIRECTORY = "AppleInc.AppleMusicWin_1.1792.39682.0_x64__nzyj5cx40ttqa"
_IDS = {"TransportControl_PlayPauseStop", "LCDScrubber", "LyricsToggleButton", "PlayQueueToggleButton"}

@dataclass(frozen=True, slots=True)
class TransportObservation:
    status: str
    minimized: bool = False
    play_pause_name: str = ""
    lyrics_toggle: str = "unknown"
    raw_position: float | None = None
    raw_minimum: float | None = None
    raw_maximum: float | None = None
    reason: str = ""
    # Slider units remain raw until correlated against SMTC during real playback.
    lyrics_timeline: str = "not_observed"


def parse_transport_snapshot(value: Any) -> TransportObservation:
    if not isinstance(value, dict) or value.get("version") != 1:
        raise ValueError("unsupported UIA observation protocol")
    status = value.get("status")
    if status in {"unavailable", "unsupported"}:
        return TransportObservation(status, reason=str(value.get("reason", "")))
    if status != "observed" or value.get("mode") != "read_only":
        raise ValueError("invalid UIA observation")
    if value.get("file_version") != VERIFIED_FILE_VERSION or value.get("package_directory") != VERIFIED_PACKAGE_DIRECTORY:
        raise ValueError("unverified Apple Music version")
    if value.get("transport_framework") != "XAML":
        raise ValueError("unverified transport framework")
    if type(value.get("minimized")) is not bool:
        raise ValueError("invalid window state")
    controls = value.get("controls")
    if not isinstance(controls, list):
        raise ValueError("missing controls")
    nodes = {}
    for node in controls:
        if not isinstance(node, dict) or node.get("automation_id") not in _IDS or node.get("framework_id") != "XAML":
            raise ValueError("unexpected control")
        identifier = node["automation_id"]
        if identifier in nodes:
            raise ValueError("duplicate transport control")
        nodes[identifier] = node
    if "TransportControl_PlayPauseStop" not in nodes:
        raise ValueError("missing playback control")
    name = nodes["TransportControl_PlayPauseStop"].get("name", "")
    if not isinstance(name, str) or len(name) > 96:
        raise ValueError("invalid control label")
    toggle = nodes.get("LyricsToggleButton", {}).get("toggle", "unknown")
    if toggle not in {"Off", "On", "Indeterminate", "unknown"}:
        raise ValueError("invalid lyric toggle")
    raw = nodes.get("LCDScrubber", {}).get("range")
    position = minimum = maximum = None
    if raw is not None:
        if not isinstance(raw, dict):
            raise ValueError("invalid slider")
        values = [raw.get(key) for key in ("value", "minimum", "maximum")]
        if any(type(number) not in (int, float) or not math.isfinite(number) for number in values):
            raise ValueError("invalid slider range")
        position, minimum, maximum = map(float, values)
        if not minimum <= position <= maximum:
            raise ValueError("slider outside range")
    return TransportObservation("observed", value["minimized"], name, toggle, position, minimum, maximum)


class AppleMusicUiaProbe:
    def inspect(self, *, timeout: float = 10.0) -> TransportObservation:
        if os.name != "nt":
            return TransportObservation("unavailable", reason="windows_only")
        if timeout <= 0 or not math.isfinite(timeout):
            raise ValueError("timeout must be finite and positive")
        from .apple_window import find_apple_music_window
        window = find_apple_music_window()
        if window is None:
            return TransportObservation("unavailable", reason="verified_apple_window_unavailable")
        powershell = Path(os.environ.get("SystemRoot", "C:/Windows")) / "System32/WindowsPowerShell/v1.0/powershell.exe"
        script = Path(__file__).with_name("bridge") / "apple_uia.ps1"
        result = subprocess.run([str(powershell), "-NoLogo", "-NoProfile", "-NonInteractive",
                                 "-ExecutionPolicy", "Bypass", "-STA", "-File", str(script),
                                 "-UiaRootHandle", str(window.uia_hwnd)],
                                capture_output=True, timeout=timeout, check=False,
                                creationflags=subprocess.CREATE_NO_WINDOW)
        if result.returncode:
            raise RuntimeError("read-only UI Automation probe failed")
        return parse_transport_snapshot(json.loads(result.stdout.decode("utf-8-sig")))
