"""Single hidden host: an owned child button inside Apple Music and lyric overlay."""
from __future__ import annotations
import ctypes
from ctypes import wintypes
from datetime import datetime, timezone
import logging
import tkinter as tk
from tkinter import messagebox
import time
from .apple_window import find_apple_music_window, physical_window_rect
from .button_layout import choose_button_rect
from .live_overlay import LiveLyricsOverlay
from .native_button import NativeLyricsButton
from .virtual_desktop import same_dpi_context

LOG = logging.getLogger(__name__)


def _native_api():
    api=ctypes.WinDLL("user32",use_last_error=True)
    api.ScreenToClient.argtypes=[wintypes.HWND,ctypes.POINTER(wintypes.POINT)]
    api.ScreenToClient.restype=wintypes.BOOL
    api.GetClientRect.argtypes=[wintypes.HWND,ctypes.POINTER(wintypes.RECT)]
    api.GetClientRect.restype=wintypes.BOOL
    api.GetAncestor.argtypes=[wintypes.HWND,wintypes.UINT]
    api.GetAncestor.restype=wintypes.HWND
    api.GetForegroundWindow.argtypes=[]
    api.GetForegroundWindow.restype=wintypes.HWND
    api.GetWindow.argtypes=[wintypes.HWND,wintypes.UINT]
    api.GetWindow.restype=wintypes.HWND
    api.GetWindowLongPtrW.argtypes=[wintypes.HWND,ctypes.c_int]
    api.GetWindowLongPtrW.restype=ctypes.c_ssize_t
    api.SetWindowPos.argtypes=[wintypes.HWND,wintypes.HWND,ctypes.c_int,ctypes.c_int,ctypes.c_int,ctypes.c_int,wintypes.UINT]
    api.SetWindowPos.restype=wintypes.BOOL
    return api


def _fresh(snapshot):
    try:
        observed=datetime.fromisoformat(snapshot['observed_at'].replace('Z','+00:00'))
        return observed.tzinfo is not None and -2 <= (datetime.now(timezone.utc)-observed).total_seconds() <= 10
    except (KeyError,TypeError,ValueError,AttributeError):
        return False


def _client_rect(api, parent, screen):
    point=wintypes.POINT(screen[0],screen[1])
    area=wintypes.RECT()
    if not api.ScreenToClient(parent,ctypes.byref(point)) or not api.GetClientRect(parent,ctypes.byref(area)):
        return None
    x,y,width,height=point.x,point.y,screen[2],screen[3]
    if x<0 or y<0 or x+width>area.right or y+height>area.bottom:
        return None
    return x,y,width,height


class IntegratedDesktopLyricsApp(LiveLyricsOverlay):
    def __init__(self, session, apple_lyrics, *, suppress_show=False, network_lookup=None):
        super().__init__(session, close_host_on_exit=False)
        self.apple_lyrics=apple_lyrics
        self.native=NativeLyricsButton(self._on_native_toggle,placement_mode="owned_popup")
        self._native_parent=0
        self._button_visible=False
        self._last_placement=None
        self._last_layout_error=None
        self._api=_native_api()
        self._missing_since=None
        self._seen_apple=False
        self._last_apple_identity=None
        self.suppress_show=suppress_show
        self.toggle_count=0
        self.network_lookup=network_lookup
        self._online_button=None
        if network_lookup is not None:
            enabled=network_lookup.allow_network
            self._online_button=tk.Button(self.controls_row,text="在线同步已启用" if enabled else "启用在线同步",
                state="disabled" if enabled else "normal",command=self._request_network,
                bg=self.style.background,fg="#FFFFFF",bd=0,padx=8,takefocus=True)
            self._online_button.pack(side="left")

    def start(self, *, show=False):
        # User-facing display is controlled only by the Apple child button.
        self.apple_lyrics.start()
        super().start(show=False)
        self.root.after(100,self._poll_native)

    def _request_network(self):
        if self.network_lookup is None or self.network_lookup.allow_network:
            return
        allowed=messagebox.askyesno("启用在线同步歌词",
            "是否允许将歌曲名等播放信息发送给 LRCLIB，以查找逐行同步歌词？\n\n"
            "仅对本次运行有效，不涉及 Apple 账号。",parent=self.root)
        if not allowed:
            return
        self.network_lookup.resolver.enable_external=True
        self.network_lookup.allow_network=True
        self.session.refresh_lyrics()
        if self._online_button is not None:
            self._online_button.configure(text="在线同步已启用",state="disabled")
        LOG.info("online_sync_authorized_by_user")

    def _on_native_toggle(self):
        self.toggle_count+=1
        if self.suppress_show:
            LOG.info("native_toggle_verified count=%s",self.toggle_count)
            return
        if self.root.state() != 'withdrawn':
            self.hide()
            LOG.info("overlay_hidden_by_button")
            return
        window=find_apple_music_window()
        # A hidden/off-desktop Apple button cannot be a physical user click.
        # Keep the overlay hidden for programmatic/stale messages on desktop 1.
        if window is None or window.cloaked or window.minimized:
            LOG.info("overlay_kept_hidden reason=apple_not_visible_on_active_desktop")
            return
        try:
            # This callback is reached only through our verified button command.
            # A user may click it while Apple is visible but not foreground;
            # that direct action may open the overlay without activating Apple.
            self.show()
            LOG.info("overlay_opened_by_button")
        except Exception as exc:
            self.hide()
            LOG.warning("overlay_kept_hidden reason=%s",type(exc).__name__)

    def _poll_native(self):
        if self._closed:
            return
        try:
            window=find_apple_music_window()
            snapshot=self.apple_lyrics.latest_snapshot
            if window is None:
                self.native.close()
                if self._seen_apple:
                    self._missing_since=self._missing_since or time.monotonic()
                    if time.monotonic()-self._missing_since>10:
                        LOG.warning("apple_window_temporarily_unavailable; host_kept_alive")
                        self._missing_since=time.monotonic()
            else:
                self._seen_apple=True
                self._missing_since=None
                valid=(_fresh(snapshot) and snapshot.get('file_version')=='1.7.0.155'
                       and snapshot.get('process_id')==window.process_id
                       and snapshot.get('uia_root_handle')==window.uia_hwnd and not window.minimized
                       and tuple(snapshot.get('native_window_rect',()))==physical_window_rect(window.hwnd))
                screen=choose_button_rect(snapshot.get('layout'),window.dpi) if valid else None
                if screen is None:
                    self.native.close()
                    self._last_placement=None
                    self._button_visible=False
                else:
                    with same_dpi_context(window.hwnd):
                        rect=_client_rect(self._api,window.hwnd,screen)
                        if rect is None:
                            self.native.close()
                        else:
                            if self.native.attached and self._native_parent!=window.hwnd:
                                self.native.close()
                                self._button_visible=False
                            if not self.native.attached:
                                self.native.attach(window.hwnd,screen)
                                self._native_parent=window.hwnd
                            else:
                                self.native.move(screen)
                            foreground=self._api.GetForegroundWindow()
                            allowed=not window.cloaked and not window.minimized
                            if allowed:
                                previous=self._api.GetWindow(window.hwnd,3)
                                if previous==self.native.container_hwnd:
                                    previous=self._api.GetWindow(previous,3)
                                # Stack immediately above Apple and below windows
                                # covering Apple. Never promote to TOPMOST.
                                if previous and self._api.GetWindowLongPtrW(previous,-20)&0x8:
                                    previous=0
                                self.native.set_visible(True)
                                if not self._api.SetWindowPos(self.native.container_hwnd,previous or 0,0,0,0,0,0x13):
                                    raise RuntimeError('Cannot position own button layer')
                            else:
                                self.native.set_visible(False)
                            placement=(window.hwnd,screen)
                            if placement!=self._last_placement:
                                LOG.info('owned_button_prepared owner=%s screen_rect=%s',window.hwnd,screen)
                                self._last_placement=placement
                            if allowed!=self._button_visible:
                                LOG.info('owned_button_shown=%s owner=%s',allowed,window.hwnd)
                                self._button_visible=allowed
                identity=(snapshot.get('title'),snapshot.get('subtitle'),snapshot.get('status'))
                if identity!=self._last_apple_identity:
                    self._last_apple_identity=identity
                    if snapshot.get('status')=='observed':
                        self.session.refresh_lyrics()
                self._last_layout_error=None
        except Exception as exc:
            self.native.close()
            error=type(exc).__name__
            if error!=self._last_layout_error:
                LOG.warning('native_button_unavailable reason=%s',error)
                self._last_layout_error=error
        finally:
            if not self._closed:
                self.root.after(250,self._poll_native)

    def close(self, reason="user_exit_or_window_close"):
        if self._closed:
            return
        LOG.info("host_close_requested reason=%s",reason)
        try:
            self.native.close()
        finally:
            try:
                self.apple_lyrics.stop()
            finally:
                super().close()
