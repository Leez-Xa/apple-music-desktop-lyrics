"""Finite real-host check: only the verified Apple parent receives owned children.

No Apple commands or foreground changes. Only this process's new BUTTON receives
BM_CLICK; the overlay is suppressed throughout. All owned windows/processes close.
"""
from __future__ import annotations
import ctypes
from ctypes import wintypes
from datetime import datetime
import json
import os
from pathlib import Path
import sys
import time

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'src'))
from amlyrics.apple_window import find_apple_music_window
from amlyrics.apple_lyrics_bridge import AppleLyricsBridgeMonitor
from amlyrics.apple_preferred import ApplePreferredLookup
from amlyrics.lyrics_sources import LyricsResolver
from amlyrics.smtc_bridge import SmtcBridgeMonitor
from amlyrics.live_session import LiveSession
from amlyrics.integrated_app import IntegratedDesktopLyricsApp


def main():
    before=find_apple_music_window()
    if before is None:
        print(json.dumps({'status':'skipped','reason':'verified_apple_window_unavailable'}))
        return 2
    user=ctypes.WinDLL('user32',use_last_error=True)
    user.GetForegroundWindow.restype=wintypes.HWND
    user.GetParent.argtypes=[wintypes.HWND]
    user.GetParent.restype=wintypes.HWND
    user.GetWindowThreadProcessId.argtypes=[wintypes.HWND,ctypes.POINTER(wintypes.DWORD)]
    user.PostMessageW.argtypes=[wintypes.HWND,wintypes.UINT,wintypes.WPARAM,wintypes.LPARAM]
    user.PostMessageW.restype=wintypes.BOOL
    user.IsWindow.argtypes=[wintypes.HWND]
    user.IsWindow.restype=wintypes.BOOL
    foreground=user.GetForegroundWindow()
    apple=AppleLyricsBridgeMonitor(poll_interval=1)
    monitor=SmtcBridgeMonitor()
    session=LiveSession(monitor,ApplePreferredLookup(apple,LyricsResolver()))
    app=IntegratedDesktopLyricsApp(session,apple,suppress_show=True)
    record={'status':'pending','foreground_before':int(foreground or 0),'apple_parent':before.hwnd,
            'overlay_show_suppressed':True,'external_network':False,'apple_cloaked_before':before.cloaked}
    handles=[]
    deadline=time.monotonic()+10
    def check():
        if app._closed:
            return
        if app.native.attached:
            container,button=app.native.container_hwnd,app.native.button_hwnd
            handles[:]=[container,button]
            owner=wintypes.DWORD()
            user.GetWindowThreadProcessId(button,ctypes.byref(owner))
            assert owner.value==os.getpid()
            assert user.GetParent(container)==before.hwnd
            assert user.GetParent(button)==container
            assert app.root.state()=='withdrawn'
            record.update(container=container,button=button,button_owner_pid=owner.value,
                          parent_chain_verified=True,overlay_hidden=True)
            try:
                from PIL import ImageGrab
                from amlyrics.virtual_desktop import same_dpi_context
                image_path=ROOT/'research/apple-music/evidence'/('native-button-'+datetime.now().strftime('%Y%m%d-%H%M%S-%f')+'.png')
                with same_dpi_context(button):
                    ImageGrab.grab(window=button).save(image_path)
                record['button_image']=str(image_path)
            except ImportError:
                record['button_image']='optional_Pillow_unavailable'
            assert user.PostMessageW(button,0xF5,0,0)
            app.root.after(200,finish)
        elif time.monotonic()<deadline:
            app.root.after(100,check)
        else:
            record.update(status='failed',reason='button_attachment_timeout')
            app.close()
    def finish():
        record['callback_count']=app.toggle_count
        record['status']='passed' if app.toggle_count==1 and app.root.state()=='withdrawn' else 'failed'
        app.close()
    try:
        app.start()
        app.root.after(200,check)
        app.run()
    finally:
        app.close()
    after=find_apple_music_window()
    record['foreground_after']=int(user.GetForegroundWindow() or 0)
    record['foreground_unchanged']=record['foreground_before']==record['foreground_after']
    record['apple_cloaked_after']=bool(after and after.cloaked)
    record['apple_cloaking_unchanged']=bool(after) and after.cloaked==before.cloaked
    record['owned_windows_destroyed']=bool(handles) and not any(user.IsWindow(h) for h in handles)
    record['workers_stopped']=not monitor.is_running and not apple.is_running
    if not all(record[k] for k in ('foreground_unchanged','apple_cloaking_unchanged','owned_windows_destroyed','workers_stopped')):
        record['status']='failed'
    output=ROOT/'research/apple-music/evidence'/('native-apple-attachment-'+datetime.now().strftime('%Y%m%d-%H%M%S-%f')+'.json')
    with output.open('x',encoding='utf-8') as stream:
        json.dump(record,stream,ensure_ascii=True,indent=2)
    print(json.dumps({'evidence':str(output),**record},ensure_ascii=True))
    return 0 if record['status']=='passed' else 1

if __name__=='__main__': raise SystemExit(main())
