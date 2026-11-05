"""Local GUI smoke check. Uses generated test lines, never controls Apple Music."""
from __future__ import annotations
import json
import sys
from datetime import datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "src"))
from amlyrics.contracts import PlaybackState, LyricsResult, LyricLine
from amlyrics.live_session import LiveSession
from amlyrics.live_overlay import LiveLyricsOverlay
from amlyrics.playback import PlaybackMonitor

class TestMonitor(PlaybackMonitor):
    running = False
    def start(self):
        self.running = True
        self._publish(PlaybackState(title="本地界面验证", artist="测试数据", duration_ms=10000,
                                    position_ms=1500, is_playing=False, source_app="local-test"))
    def stop(self): self.running = False

def main() -> int:
    monitor = TestMonitor()
    def lookup(state):
        return LyricsResult(state.track_key, "local-test", None,
                            (LyricLine(0, 3000, "逐行同步测试"), LyricLine(3000, 10000, "下一行测试")),
                            confidence=1.0)
    session = LiveSession(monitor, lookup)
    window = LiveLyricsOverlay(session)
    errors = []
    def verify():
        try:
            assert window.root.attributes("-topmost")
            assert window._label.cget("text") == "逐行同步测试"
            assert "已暂停" in window._status.get()
            window.hide()
            assert window.root.state() == "withdrawn"
            window.toggle()
            window.root.update_idletasks()
            assert window.root.state() != "withdrawn"
            try:
                from PIL import ImageGrab
                import ctypes
                from ctypes import wintypes
            except ImportError:
                print("Screenshot skipped: Pillow is optional")
            else:
                x, y = window.root.winfo_rootx(), window.root.winfo_rooty()
                w, h = window.root.winfo_width(), window.root.winfo_height()
                out = ROOT / "research/apple-music/evidence" / ("live-overlay-" + datetime.now().strftime("%Y%m%d-%H%M%S-%f") + ".png")
                user32 = ctypes.WinDLL("user32", use_last_error=True)
                user32.GetAncestor.argtypes = [wintypes.HWND, wintypes.UINT]
                user32.GetAncestor.restype = wintypes.HWND
                hwnd = user32.GetAncestor(window.root.winfo_id(), 2)
                ImageGrab.grab(window=hwnd).save(out)
                print(json.dumps({"screenshot": str(out), "topmost": True, "width": w, "height": h}))
        except BaseException as exc:
            errors.append(str(exc) or type(exc).__name__)
        finally:
            window.close()
    window.start()
    window.root.after(1200, verify)
    window.run()
    assert not monitor.running
    if errors:
        print(json.dumps({"errors": errors}))
        return 1
    print("PASS: topmost, generated line rendering, pause label, hide/toggle and clean shutdown")
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
