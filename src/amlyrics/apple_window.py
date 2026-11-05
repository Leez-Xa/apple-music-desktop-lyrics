"""Read-only native Apple Music window discovery, including other virtual desktops."""
from __future__ import annotations
from dataclasses import dataclass
import ctypes
from ctypes import wintypes
import os
from pathlib import PureWindowsPath

VERIFIED_PACKAGE = "AppleInc.AppleMusicWin_1.1792.39682.0_x64__nzyj5cx40ttqa"

@dataclass(frozen=True, slots=True)
class AppleWindow:
    hwnd: int
    uia_hwnd: int
    process_id: int
    image_path: str
    minimized: bool
    cloaked: bool
    client_width: int
    client_height: int
    dpi: int


def is_verified_image(path: str) -> bool:
    p = PureWindowsPath(path)
    return (p.name.casefold() == "applemusic.exe" and p.parent.name == VERIFIED_PACKAGE
            and p.parent.parent.name.casefold() == "windowsapps")


def find_apple_music_window() -> AppleWindow | None:
    if os.name != "nt":
        return None
    user = ctypes.WinDLL("user32", use_last_error=True)
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    enumproc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    user.EnumWindows.argtypes = [enumproc, wintypes.LPARAM]
    user.EnumWindows.restype = wintypes.BOOL
    user.EnumChildWindows.argtypes = [wintypes.HWND, enumproc, wintypes.LPARAM]
    user.EnumChildWindows.restype = wintypes.BOOL
    user.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
    user.GetWindowThreadProcessId.restype = wintypes.DWORD
    user.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
    user.GetClassNameW.restype = ctypes.c_int
    user.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
    user.GetWindowTextW.restype = ctypes.c_int
    user.IsIconic.argtypes = [wintypes.HWND]
    user.IsIconic.restype = wintypes.BOOL
    user.GetClientRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
    user.GetClientRect.restype = wintypes.BOOL
    user.GetDpiForWindow.argtypes = [wintypes.HWND]
    user.GetDpiForWindow.restype = wintypes.UINT
    kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]
    kernel.QueryFullProcessImageNameW.restype = wintypes.BOOL
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel.CloseHandle.restype = wintypes.BOOL
    found = []
    @enumproc
    def on_window(hwnd, _):
        cls = ctypes.create_unicode_buffer(256)
        title = ctypes.create_unicode_buffer(256)
        user.GetClassNameW(hwnd, cls, len(cls))
        if cls.value != "WinUIDesktopWin32WindowClass":
            return True
        user.GetWindowTextW(hwnd, title, len(title))
        if title.value != "Apple Music":
            return True
        process_id = wintypes.DWORD()
        user.GetWindowThreadProcessId(hwnd, ctypes.byref(process_id))
        process = kernel.OpenProcess(0x1000, False, process_id.value)
        if not process:
            return True
        try:
            path = ctypes.create_unicode_buffer(32768)
            size = wintypes.DWORD(len(path))
            if not kernel.QueryFullProcessImageNameW(process, 0, path, ctypes.byref(size)) or not is_verified_image(path.value):
                return True
            # WindowsAppRuntime's content site can expose its XAML fragment even
            # when the top-level UIA provider is clipped on another virtual desktop.
            children = []
            @enumproc
            def on_child(child, _):
                child_pid = wintypes.DWORD()
                user.GetWindowThreadProcessId(child, ctypes.byref(child_pid))
                child_cls = ctypes.create_unicode_buffer(256)
                user.GetClassNameW(child, child_cls, len(child_cls))
                if child_pid.value == process_id.value and child_cls.value == "Microsoft.UI.Content.DesktopChildSiteBridge":
                    children.append(int(child))
                return True
            user.EnumChildWindows(hwnd, on_child, 0)
            rect = wintypes.RECT()
            if not user.GetClientRect(hwnd, ctypes.byref(rect)):
                return True
            cloaked = wintypes.DWORD()
            dwm = ctypes.WinDLL("dwmapi")
            dwm.DwmGetWindowAttribute.argtypes = [wintypes.HWND, wintypes.DWORD, wintypes.LPVOID, wintypes.DWORD]
            dwm.DwmGetWindowAttribute.restype = ctypes.c_long
            dwm.DwmGetWindowAttribute(hwnd, 14, ctypes.byref(cloaked), ctypes.sizeof(cloaked))
            found.append(AppleWindow(int(hwnd), children[0] if children else int(hwnd), process_id.value,
                                     path.value, bool(user.IsIconic(hwnd)), bool(cloaked.value),
                                     rect.right-rect.left, rect.bottom-rect.top, user.GetDpiForWindow(hwnd) or 96))
        finally:
            kernel.CloseHandle(process)
        return True
    user.EnumWindows(on_window, 0)
    # Refuse ambiguous targets rather than attaching to an arbitrary window.
    return found[0] if len(found) == 1 else None


def physical_window_rect(hwnd: int) -> tuple[int, int, int, int]:
    """Read physical bounds under the target DPI context; never moves a window."""
    from .virtual_desktop import same_dpi_context
    api=ctypes.WinDLL("user32",use_last_error=True)
    api.GetWindowRect.argtypes=[wintypes.HWND,ctypes.POINTER(wintypes.RECT)]
    api.GetWindowRect.restype=wintypes.BOOL
    with same_dpi_context(hwnd):
        rect=wintypes.RECT()
        if not api.GetWindowRect(hwnd,ctypes.byref(rect)):
            raise OSError(ctypes.get_last_error(),"Cannot read Apple window bounds")
        return rect.left,rect.top,rect.right,rect.bottom
