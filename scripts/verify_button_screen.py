"""Passive visible-screen validation, never activates or moves Apple Music.

Saves only an uncovered owned-button rectangle with stable foreground.
Unavailable/constant captures are inconclusive, never called success.
"""
from __future__ import annotations
import argparse
import ctypes
from ctypes import wintypes
from datetime import datetime
import json
from pathlib import Path
import sys
import time

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'src'))
from amlyrics.apple_window import find_apple_music_window,physical_window_rect
from amlyrics.virtual_desktop import same_dpi_context
from amlyrics.screen_verification import evaluate_screen_pixels
from PIL import ImageGrab,ImageChops,ImageStat


def main():
    p=argparse.ArgumentParser()
    p.add_argument('--host-pid',required=True,type=int)
    p.add_argument('--seconds',type=float,default=25)
    args=p.parse_args()
    u=ctypes.WinDLL('user32',use_last_error=True)
    CB=ctypes.WINFUNCTYPE(wintypes.BOOL,wintypes.HWND,wintypes.LPARAM)
    for name,ret,params in [
        ('EnumWindows',wintypes.BOOL,[CB,wintypes.LPARAM]),
        ('GetWindowThreadProcessId',wintypes.DWORD,[wintypes.HWND,ctypes.POINTER(wintypes.DWORD)]),
        ('GetClassNameW',ctypes.c_int,[wintypes.HWND,wintypes.LPWSTR,ctypes.c_int]),
        ('GetWindow',wintypes.HWND,[wintypes.HWND,wintypes.UINT]),
        ('GetAncestor',wintypes.HWND,[wintypes.HWND,wintypes.UINT]),
        ('GetForegroundWindow',wintypes.HWND,[]),
        ('IsWindowVisible',wintypes.BOOL,[wintypes.HWND]),
        ('WindowFromPhysicalPoint',wintypes.HWND,[wintypes.POINT]),
        ('FindWindowExW',wintypes.HWND,[wintypes.HWND,wintypes.HWND,wintypes.LPCWSTR,wintypes.LPCWSTR])]:
        f=getattr(u,name);f.restype=ret;f.argtypes=params
    deadline=time.monotonic()+args.seconds
    last_reason='apple_not_foreground'
    while time.monotonic()<deadline:
        owner=find_apple_music_window()
        foreground=u.GetForegroundWindow()
        if owner and not owner.cloaked and not owner.minimized:
            candidates=[]
            @CB
            def top(h,_):
                pid=wintypes.DWORD();u.GetWindowThreadProcessId(h,ctypes.byref(pid))
                if pid.value!=args.host_pid or u.GetWindow(h,4)!=owner.hwnd or not u.IsWindowVisible(h):return True
                cls=ctypes.create_unicode_buffer(256);u.GetClassNameW(h,cls,256)
                if cls.value.startswith('AMLyrics.NativeButton.'):
                    candidates.append(int(h))
                return True
            u.EnumWindows(top,0)
            if candidates:
                container=candidates[0]
                with same_dpi_context(owner.hwnd):
                    bounds=physical_window_rect(container)
                    button=u.FindWindowExW(container,None,'BUTTON',None)
                    if not button:
                        last_reason='owned_button_not_found';continue
                    sample_points=[(bounds[0]+round((bounds[2]-bounds[0]-1)*fx),
                                    bounds[1]+round((bounds[3]-bounds[1]-1)*fy))
                                   for fx in (.08,.5,.92) for fy in (.08,.5,.92)]
                    def is_own_point(point):
                        pid=wintypes.DWORD()
                        target=u.WindowFromPhysicalPoint(wintypes.POINT(*point))
                        u.GetWindowThreadProcessId(target,ctypes.byref(pid))
                        return pid.value==args.host_pid
                    if not all(is_own_point(point) for point in sample_points):
                        last_reason='button_region_is_covered'
                        time.sleep(.1)
                        continue
                    expected=ImageGrab.grab(window=button).convert('RGB')
                    actual=ImageGrab.grab(bbox=bounds,all_screens=True).convert('RGB')
                    hit=u.WindowFromPhysicalPoint(wintypes.POINT((bounds[0]+bounds[2])//2,(bounds[1]+bounds[3])//2))
                if u.GetForegroundWindow()!=foreground:
                    last_reason='foreground_changed_during_capture';continue
                if expected.size!=actual.size:
                    last_reason='capture_size_mismatch';continue
                e=list(getattr(expected,'get_flattened_data',expected.getdata)())
                a=list(getattr(actual,'get_flattened_data',actual.getdata)())
                decision=evaluate_screen_pixels(e,a,expected.size,actual_size=actual.size)
                hit_pid=wintypes.DWORD();u.GetWindowThreadProcessId(hit,ctypes.byref(hit_pid))
                passed=decision.passed and hit_pid.value==args.host_pid
                inconclusive=(not decision.valid_reference or decision.reason=="actual_insufficient_contrast")
                stamp=datetime.now().strftime('%Y%m%d-%H%M%S-%f')
                out=ROOT/'research/apple-music/evidence'/('button-onscreen-'+stamp+'.png')
                actual.save(out)
                record={'status':'passed' if passed else 'inconclusive' if inconclusive else 'failed','owner':owner.hwnd,'container':container,
                        'bounds':bounds,'host_pid':args.host_pid,'hit_hwnd':int(hit or 0),'hit_pid':hit_pid.value,
                        'valid_reference':decision.valid_reference,'capture_reason':decision.reason,
                        'pixel_metrics':decision.metrics,
                        'foreground_unchanged':True,'actual_screen_crop':str(out),'desktop_switch':False}
                with out.with_suffix('.json').open('x',encoding='utf-8') as stream:json.dump(record,stream,indent=2)
                print(json.dumps(record,ensure_ascii=True))
                return 0 if passed else 2 if inconclusive else 1
            last_reason='button_not_visible_with_apple_foreground'
        time.sleep(0.15)
    print(json.dumps({'status':'pending','reason':last_reason,'host_pid':args.host_pid,
                      'desktop_switch':False,'activated_apple':False},ensure_ascii=True))
    return 2

if __name__=='__main__':raise SystemExit(main())
