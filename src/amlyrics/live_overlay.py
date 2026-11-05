from __future__ import annotations

import tkinter as tk
from .live_session import LiveSession, LiveView
from .overlay import DesktopLyricsOverlay


class LiveLyricsOverlay(DesktopLyricsOverlay):
    """Tk owns its UI thread. Playback and lyrics arrive through LiveSession."""

    def __init__(self, session: LiveSession, *, close_host_on_exit: bool = True) -> None:
        super().__init__()
        self.close_host_on_exit = close_host_on_exit
        self.session = session
        self._closed = False
        self.root.geometry("760x188+80+80")
        self._label.configure(wraplength=700, pady=12)
        self._status = tk.StringVar(value="等待 Apple Music 会话")
        self._next = tk.StringVar(value="")
        self._last_key = None
        self._next_label = tk.Label(self.root, textvariable=self._next, bg=self.style.background,
                                   fg="#C3C3C8", font=(self.style.font_family, 15), wraplength=700)
        self._next_label.pack(fill="x", padx=18)
        row = tk.Frame(self.root, bg=self.style.background)
        self.controls_row = row
        row.pack(fill="x", padx=12, pady=8)
        tk.Label(row, textvariable=self._status, bg=self.style.background, fg="#C3C3C8",
                 font=(self.style.font_family, 10), anchor="w", wraplength=530).pack(side="left", fill="x", expand=True)
        for text, command in (("A−", lambda: self.resize_text(-2)),
                              ("A+", lambda: self.resize_text(2)),
                              ("隐藏", self.hide), ("退出", self.close if self.close_host_on_exit else self.hide)):
            tk.Button(row, text=text, command=command, takefocus=True, bd=0, padx=8,
                      bg=self.style.background, fg="#FFFFFF", activebackground="#343438",
                      activeforeground="#FFFFFF").pack(side="left")
        self.root.bind("<Escape>", lambda _event: self.hide())
        self.root.bind("<Control-plus>", lambda _event: self.resize_text(2))
        self.root.bind("<Control-minus>", lambda _event: self.resize_text(-2))
        self.root.protocol("WM_DELETE_WINDOW", self.close if self.close_host_on_exit else self.hide)

    def resize_text(self, delta: int) -> None:
        self.style.font_size = max(16, min(48, self.style.font_size + delta))
        self._label.configure(font=(self.style.font_family, self.style.font_size, "bold"))
        self.root.geometry("760x" + str(max(188, self.style.font_size * 4 + 92)))

    def start(self, *, show: bool = True) -> None:
        self.session.start()
        if show:
            self.show()
        self.root.after(0, self._tick)

    def toggle(self) -> None:
        if self.root.state() == "withdrawn":
            self.show()
        else:
            self.hide()

    def _tick(self) -> None:
        if self._closed:
            return
        try:
            view = self.session.tick()
            self.render(view)
        finally:
            if not self._closed:
                self.root.after(100, self._tick)

    def render(self, view: LiveView) -> None:
        frame = view.frame
        if frame.current_line:
            current = frame.current_line.text or "♪"
        elif view.source:
            current = "♪"
        else:
            current = view.diagnostic or "暂无同步歌词"
        self._label.configure(text=current)
        self._next.set(frame.next_line.text if frame.next_line else "")
        state = view.state
        seconds = max(0, state.position_ms // 1000)
        duration = max(0, state.duration_ms // 1000)
        position = str(seconds // 60) + ":" + str(seconds % 60).zfill(2)
        total = str(duration // 60) + ":" + str(duration % 60).zfill(2)
        if state.title:
            status = ("播放中" if state.is_playing else "已暂停") + " · " + state.title
            if state.artist:
                status += " — " + state.artist
            status += " · " + position + "/" + total
            labels = {"apple_music": "Apple Music 歌词", "lrclib": "外部同步歌词",
                      "apple_music+external_timing": "Apple Music 歌词 · 外部同步", "local": "本地歌词"}
            source_label = labels.get(view.source, "同步歌词") if view.source else view.diagnostic
            status += " · " + source_label
        else:
            status = "等待 Apple Music 会话"
        self._status.set(status)

    def close(self) -> None:
        if self._closed:
            return
        self._closed = True
        try:
            self.session.close()
        finally:
            super().close()
