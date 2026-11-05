from __future__ import annotations

import sys
import time
from pathlib import Path

from .contracts import LyricsResult, PlaybackState
from .lrc import parse_lrc
from .playback import MockPlaybackMonitor
from .service import LyricsService

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")


def build_parser():
    import argparse

    parser = argparse.ArgumentParser(description="Apple Music desktop lyrics prototype")
    parser.add_argument("--lrc", type=Path, help="LRC file for local sync simulation")
    parser.add_argument("--duration-ms", type=int, default=60_000)
    parser.add_argument("--step-ms", type=int, default=500)
    return parser


def main() -> int:
    args = build_parser().parse_args()
    lrc_text = args.lrc.read_text(encoding="utf-8") if args.lrc else "[00:00.00]No LRC supplied\n"
    parsed = parse_lrc(lrc_text, end_ms=args.duration_ms)
    initial = PlaybackState("Demo Track", "Demo Artist", "Demo Album", args.duration_ms, 0, True, "demo")
    monitor = MockPlaybackMonitor(initial)
    result = LyricsResult(initial.track_key, "local-demo", None, parsed.lines, confidence=1.0)
    service = LyricsService(monitor, lambda _state: result)
    service.subscribe_frame(lambda frame: print(f"{frame.position_ms:>6}ms | {frame.current_line.text if frame.current_line else '...'}"))
    while monitor.state.position_ms < args.duration_ms:
        monitor.advance(args.step_ms)
        time.sleep(args.step_ms / 1000)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
