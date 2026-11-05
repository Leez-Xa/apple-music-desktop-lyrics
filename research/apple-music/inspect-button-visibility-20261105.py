"""Read only Apple/owned window geometry, ownership and visible-screen evidence."""
import ctypes
from ctypes import wintypes
import json
import sys
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'src'))
from amlyrics.apple_window import find_apple_music_window
from amlyrics.virtual_desktop import same_dpi_context
u=ctypes.WinDLL('user32',use_last_error=True)
k=ctypes.WinDLL('kernel32',use_last_error=True)
dwm=ctypes.WinDLL('dwmapi')
CB=ctypes.WINFUNCTYPE(wintypes.BOOL,wintypes.HWND,wintypes.LPARAM)
for name,ret,args in [
 ('EnumWindows',wintypes.BOOL,[CB,wintypes.LPARAM]),('EnumChildWindows',wintypes.BOOL,[wintypes.HWND,CB,wintypes.LPARAM]),
 ('GetWindowThreadProcessId',wintypes.DWORD,[wintypes.HWND,ctypes.POINTER(wintypes.DWORD)]),
 ('GetWindowTextW',ctypes.c_int,[wintypes.HWND,wintypes.LPWSTR,ctypes.c_int]),('GetClassNameW',ctypes.c_int,[wintypes.HWND,wintypes.LPWSTR,ctypes.c_int]),
 ('GetWindowRect',wintypes.BOOL,[wintypes.HWND,ctypes.POINTER(wintypes.RECT)]),('GetWindow',wintypes.HWND,[wintypes.HWND,wintypes.UINT]),
 ('GetParent',wintypes.HWND,[wintypes.HWND]),('GetAncestor',wintypes.HWND,[wintypes.HWND,wintypes.UINT]),
 ('IsWindowVisible',wintypes.BOOL,[wintypes.HWND]),('IsIconic',wintypes.BOOL,[wintypes.HWND]),('GetForegroundWindow',wintypes.HWND,[])]:
 f=getattr(u,name);f.restype=ret;f.argtypes=args
k.OpenProcess.argtypes=[wintypes.DWORD,wintypes.BOOL,wintypes.DWORD];k.OpenProcess.restype=wintypes.HANDLE
k.QueryFullProcessImageNameW.argtypes=[wintypes.HANDLE,wintypes.DWORD,wintypes.LPWSTR,ctypes.POINTER(wintypes.DWORD)];k.QueryFullProcessImageNameW.restype=wintypes.BOOL
k.CloseHandle.argtypes=[wintypes.HANDLE]
dwm.DwmGetWindowAttribute.argtypes=[wintypes.HWND,wintypes.DWORD,wintypes.LPVOID,wintypes.DWORD]
rows=[]
def image_path(pid):
 handle=k.OpenProcess(0x1000,False,pid)
 if not handle:return ''
 try:
  buf=ctypes.create_unicode_buffer(32768);size=wintypes.DWORD(len(buf))
  return buf.value if not k.QueryFullProcessImageNameW(handle,0,buf,ctypes.byref(size)) else buf.value
 finally:k.CloseHandle(handle)
def info(h,child=False):
 pid=wintypes.DWORD();u.GetWindowThreadProcessId(h,ctypes.byref(pid))
 path=image_path(pid.value)
 if not (path.lower().endswith('applemusic.exe') or pid.value==39060):return
 cls=ctypes.create_unicode_buffer(256);name=ctypes.create_unicode_buffer(256)
 u.GetClassNameW(h,cls,256);u.GetWindowTextW(h,name,256)
 rect=wintypes.RECT();cloaked=wintypes.DWORD()
 with same_dpi_context(int(h)):u.GetWindowRect(h,ctypes.byref(rect))
 dwm.DwmGetWindowAttribute(h,14,ctypes.byref(cloaked),ctypes.sizeof(cloaked))
 rows.append({'hwnd':int(h),'pid':pid.value,'class':cls.value,'text':name.value,'child':child,
              'owner':int(u.GetWindow(h,4) or 0),'parent':int(u.GetParent(h) or 0),'previous_z':int(u.GetWindow(h,3) or 0),
              'rect':[rect.left,rect.top,rect.right,rect.bottom],'visible':bool(u.IsWindowVisible(h)),
              'minimized':bool(u.IsIconic(h)),'cloaked':bool(cloaked.value)})
@CB
def child(h,_):
 pid=wintypes.DWORD();u.GetWindowThreadProcessId(h,ctypes.byref(pid))
 if pid.value==39060:info(h,True)
 return True
@CB
def top(h,_):
 info(h)
 u.EnumChildWindows(h,child,0)
 return True
u.EnumWindows(top,0)
print(json.dumps({'foreground':int(u.GetForegroundWindow() or 0),'windows':rows},ensure_ascii=True))
