from __future__ import annotations

import ctypes
import os
import tkinter as tk
from dataclasses import dataclass

from .contracts import SyncFrame


@dataclass(slots=True)
class OverlayStyle:
    font_family: str = "Segoe UI"
    font_size: int = 24
    foreground: str = "#FFFFFF"
    background: str = "#101014"
    alpha: float = 0.92


class DesktopLyricsOverlay:
    """Minimal topmost desktop lyrics window built with Tkinter."""

    def __init__(self, *, style: OverlayStyle | None = None) -> None:
        self.style = style or OverlayStyle()
        self.root = tk.Tk()
        self.root.title("Apple Music Desktop Lyrics")
        self.root.configure(bg=self.style.background)
        self.root.attributes("-topmost", True)
        self.root.attributes("-alpha", self.style.alpha)
        self.root.overrideredirect(True)
        self.root.geometry("760x96+80+80")
        self.root.withdraw()
        self._drag_origin: tuple[int, int] | None = None
        self._click_through = False
        self._label = tk.Label(
            self.root,
            text="桌面歌词等待中",
            bg=self.style.background,
            fg=self.style.foreground,
            font=(self.style.font_family, self.style.font_size, "bold"),
            padx=24,
            pady=16,
        )
        self._label.pack(fill="both", expand=True)
        self._label.bind("<ButtonPress-1>", self._begin_drag)
        self._label.bind("<B1-Motion>", self._drag)

    def show(self) -> None:
        self.root.deiconify()
        self.root.lift()

    def hide(self) -> None:
        self.root.withdraw()

    def close(self) -> None:
        self.root.destroy()

    def update_frame(self, frame: SyncFrame) -> None:
        if frame.current_line is None:
            text = frame.next_line.text if frame.next_line else "暂无同步歌词"
        else:
            text = frame.current_line.text or "♪"
        self._label.configure(text=text)

    def set_click_through(self, enabled: bool) -> None:
        self._click_through = enabled
        if os.name != "nt":
            return
        hwnd = self.root.winfo_id()
        user32 = ctypes.windll.user32
        get_style = user32.GetWindowLongPtrW
        set_style = user32.SetWindowLongPtrW
        exstyle = get_style(hwnd, -20)
        exstyle |= 0x00000020 | 0x00000080 if enabled else 0
        if not enabled:
            exstyle &= ~0x00000020
        set_style(hwnd, -20, exstyle)

    def run(self) -> None:
        self.root.mainloop()

    def _begin_drag(self, event: tk.Event) -> None:
        self._drag_origin = (event.x_root - self.root.winfo_x(), event.y_root - self.root.winfo_y())

    def _drag(self, event: tk.Event) -> None:
        if self._drag_origin is None:
            return
        x_offset, y_offset = self._drag_origin
        self.root.geometry(f"+{event.x_root - x_offset}+{event.y_root - y_offset}")
