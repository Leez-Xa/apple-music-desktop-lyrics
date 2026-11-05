from __future__ import annotations

import os
import subprocess
from dataclasses import dataclass


@dataclass(frozen=True, slots=True)
class AppleMusicObservation:
    process_name: str
    running: bool
    pids: tuple[int, ...]
    adapter_mode: str
    message: str


class AppleMusicInspector:
    """Read-only process inspection; no DLL injection or file patching."""

    process_names = ("AppleMusic.exe", "AppleMusic")

    def inspect(self) -> AppleMusicObservation:
        if os.name != "nt":
            return AppleMusicObservation("AppleMusic.exe", False, (), "unsupported", "Apple Music Windows inspection requires Windows")
        try:
            result = subprocess.run(
                ["tasklist", "/FO", "CSV", "/NH"],
                capture_output=True,
                text=True,
                timeout=2,
                check=False,
            )
        except (OSError, subprocess.SubprocessError) as exc:
            return AppleMusicObservation("AppleMusic.exe", False, (), "observation-only", f"tasklist unavailable: {exc}")
        wanted = {name.casefold() for name in self.process_names}
        pids: list[int] = []
        for line in result.stdout.splitlines():
            fields = [field.strip('"') for field in line.split('","')]
            if len(fields) < 2 or fields[0].casefold() not in wanted:
                continue
            try:
                pids.append(int(fields[1]))
            except ValueError:
                continue
        return AppleMusicObservation(
            "AppleMusic.exe",
            bool(pids),
            tuple(pids),
            "observation-only",
            "Process presence only; UI injection is intentionally not implemented until runtime evidence exists",
        )

    def inject_button(self) -> None:
        raise RuntimeError("Unsafe injection is not implemented; complete runtime/UI reconnaissance first")
