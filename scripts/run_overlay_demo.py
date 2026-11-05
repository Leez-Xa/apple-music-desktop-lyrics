from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "src"))

from amlyrics.contracts import LyricLine, SyncFrame
from amlyrics.overlay import DesktopLyricsOverlay


overlay = DesktopLyricsOverlay()
overlay.show()
frame = SyncFrame(0, 0, LyricLine(0, None, "桌面歌词窗口原型"), None, None, True)
overlay.update_frame(frame)
overlay.root.after(3000, overlay.close)
overlay.run()
