"""Single entry for a real SMTC-driven prototype. Network is opt-in."""
from __future__ import annotations
import argparse
import json
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "src"))
from amlyrics.contracts import LyricsResult
from amlyrics.lrc import parse_lrc
from amlyrics.live_session import LiveSession
from amlyrics.lyrics_sources import LyricsResolver
from amlyrics.smtc_bridge import SmtcBridgeMonitor, inspect_sessions


def parser():
    p = argparse.ArgumentParser(description="Apple Music real playback lyric prototype")
    p.add_argument("--diagnose", action="store_true", help="Read one raw SMTC snapshot and exit")
    p.add_argument("--headless-seconds", type=float, default=0, help="Observe without opening a window")
    p.add_argument("--duration-seconds", type=float, default=0, help="Close the window after this many seconds")
    p.add_argument("--allow-external-lyrics", action="store_true", help="Allow read-only LRCLIB queries with title, artist, album and duration")
    p.add_argument("--local-lrc", type=Path, help="Explicit local LRC bound to --match-title and --match-artist")
    p.add_argument("--match-title")
    p.add_argument("--match-artist")
    p.add_argument("--match-album")
    return p


def main() -> int:
    p = parser()
    args = p.parse_args()
    if args.headless_seconds < 0 or args.duration_seconds < 0:
        p.error("durations must be non-negative")
    if args.local_lrc and (not args.match_title or not args.match_artist):
        p.error("--local-lrc requires --match-title and --match-artist to avoid showing another song's lyrics")
    if args.diagnose:
        try:
            print(json.dumps(inspect_sessions(), ensure_ascii=True))
            return 0
        except Exception as exc:
            print("SMTC diagnostic failed: " + str(exc), file=sys.stderr)
            return 1
    local_text = args.local_lrc.read_text(encoding="utf-8-sig") if args.local_lrc else None
    resolver = LyricsResolver(enable_external=args.allow_external_lyrics)
    def lookup(state):
        if local_text is not None and (state.title.casefold().strip() == args.match_title.casefold().strip()
                and state.artist.casefold().strip() == args.match_artist.casefold().strip()
                and (not args.match_album or state.album.casefold().strip() == args.match_album.casefold().strip())):
            from amlyrics.lyrics_sources import track_identity
            resolver.local_mapping = {track_identity(state): local_text}
        else:
            resolver.local_mapping = {}
        return resolver.lookup(state, allow_network=args.allow_external_lyrics)
    monitor = SmtcBridgeMonitor()
    session = LiveSession(monitor, lookup)
    if args.headless_seconds:
        try:
            session.start()
            deadline = time.monotonic() + args.headless_seconds
            previous = None
            while time.monotonic() < deadline:
                view = session.tick()
                record = {"state": view.state.to_dict(), "source": view.source,
                          "diagnostic": view.diagnostic, "bridge_error": monitor.last_error}
                key = json.dumps(record, ensure_ascii=True)
                if key != previous:
                    print(key, flush=True)
                    previous = key
                time.sleep(0.1)
            return 1 if monitor.last_error else 0
        except KeyboardInterrupt:
            return 130
        finally:
            session.close()
    from amlyrics.live_overlay import LiveLyricsOverlay
    window = LiveLyricsOverlay(session)
    try:
        window.start()
        if args.duration_seconds:
            window.root.after(max(1, int(args.duration_seconds * 1000)), window.close)
        window.run()
        return 0
    finally:
        window.close()


if __name__ == "__main__":
    raise SystemExit(main())
