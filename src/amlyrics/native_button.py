"""Owned Win32 BUTTON/container prototype; no Apple discovery or UI automation.

Instances must be constructed and operated on the Python main thread. A foreign
parent is accepted only with an identical DPI awareness context.  Successful
creation is not a guarantee of foreign-host layout, rendering or input support.
Import is safe on non-Windows hosts; attaching requires Windows 10 1607+ APIs.
"""
from __future__ import annotations

import ctypes
import os
import threading
import uuid
from collections.abc import Callable
from ctypes import wintypes

__all__ = ["NativeLyricsButton", "NativeButtonError", "pump_messages"]


class NativeButtonError(RuntimeError):
    """A native operation failed or attachment prerequisites were not met."""


# Process-local state keeps each live instance and its WndProc alive. One native
# class is retained until process exit, regardless of the number of attachments.
_api = None
_api_lock = threading.Lock()
_live: dict[int, NativeLyricsButton] = {}
_pending: dict[int, NativeLyricsButton] = {}
_CONTROL_ID = 1
WM_NCCREATE, WM_NCDESTROY, WM_SIZE, WM_COMMAND = 0x81, 0x82, 5, 0x111
BM_CLICK = 0xF5
WS_CHILD, WS_VISIBLE, WS_TABSTOP = 0x40000000, 0x10000000, 0x10000
WS_EX_NOPARENTNOTIFY = 4
SWP_NOZORDER, SWP_NOACTIVATE = 4, 0x10
WS_POPUP = 0x80000000
WS_EX_TOOLWINDOW, WS_EX_NOACTIVATE = 0x80, 0x08000000
SW_HIDE, SW_SHOWNOACTIVATE = 0, 4


class _Win32:
    def __init__(self):
        if os.name != "nt":
            raise NativeButtonError("NativeLyricsButton requires Windows")
        self.user32 = ctypes.WinDLL("user32", use_last_error=True)
        self.kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        self.WNDPROC = ctypes.WINFUNCTYPE(
            ctypes.c_ssize_t, wintypes.HWND, wintypes.UINT,
            wintypes.WPARAM, wintypes.LPARAM,
        )
        wndproc_type = self.WNDPROC

        class WNDCLASS(ctypes.Structure):
            _fields_ = [
                ("style", wintypes.UINT), ("lpfnWndProc", wndproc_type),
                ("cbClsExtra", ctypes.c_int), ("cbWndExtra", ctypes.c_int),
                ("hInstance", wintypes.HINSTANCE), ("hIcon", wintypes.HICON),
                ("hCursor", wintypes.HANDLE), ("hbrBackground", wintypes.HBRUSH),
                ("lpszMenuName", wintypes.LPCWSTR),
                ("lpszClassName", wintypes.LPCWSTR),
            ]

        class CREATESTRUCT(ctypes.Structure):
            _fields_ = [
                ("lpCreateParams", ctypes.c_void_p),
                ("hInstance", wintypes.HINSTANCE), ("hMenu", wintypes.HMENU),
                ("hwndParent", wintypes.HWND),
                ("cy", ctypes.c_int), ("cx", ctypes.c_int),
                ("y", ctypes.c_int), ("x", ctypes.c_int),
                ("style", wintypes.LONG), ("lpszName", wintypes.LPCWSTR),
                ("lpszClass", wintypes.LPCWSTR), ("dwExStyle", wintypes.DWORD),
            ]

        self.CREATESTRUCT = CREATESTRUCT

        def bind(lib, name, result, *args):
            try:
                function = getattr(lib, name)
            except AttributeError as exc:
                raise NativeButtonError(f"Required Windows API unavailable: {name}") from exc
            function.restype, function.argtypes = result, list(args)
            setattr(self, name, function)

        U, H, I, B = wintypes.UINT, wintypes.HWND, ctypes.c_int, wintypes.BOOL
        bind(self.kernel32, "GetModuleHandleW", wintypes.HMODULE, wintypes.LPCWSTR)
        bind(self.user32, "RegisterClassW", wintypes.ATOM, ctypes.POINTER(WNDCLASS))
        bind(self.user32, "CreateWindowExW", H, wintypes.DWORD, wintypes.LPCWSTR,
             wintypes.LPCWSTR, wintypes.DWORD, I, I, I, I, H, wintypes.HMENU,
             wintypes.HINSTANCE, ctypes.c_void_p)
        bind(self.user32, "DefWindowProcW", ctypes.c_ssize_t, H, U,
             wintypes.WPARAM, wintypes.LPARAM)
        bind(self.user32, "DestroyWindow", B, H)
        bind(self.user32, "IsWindow", B, H)
        bind(self.user32, "IsWindowVisible", B, H)
        bind(self.user32, "ShowWindow", B, H, I)
        bind(self.user32, "GetParent", H, H)
        bind(self.user32, "GetWindowThreadProcessId", wintypes.DWORD, H,
             ctypes.POINTER(wintypes.DWORD))
        bind(self.user32, "GetThreadDpiAwarenessContext", wintypes.HANDLE)
        bind(self.user32, "GetWindowDpiAwarenessContext", wintypes.HANDLE, H)
        bind(self.user32, "AreDpiAwarenessContextsEqual", B,
             wintypes.HANDLE, wintypes.HANDLE)
        bind(self.user32, "GetClientRect", B, H, ctypes.POINTER(wintypes.RECT))
        bind(self.user32, "GetWindowRect", B, H, ctypes.POINTER(wintypes.RECT))
        bind(self.user32, "ScreenToClient", B, H, ctypes.POINTER(wintypes.POINT))
        bind(self.user32, "SetWindowPos", B, H, H, I, I, I, I, U)
        bind(self.user32, "PostMessageW", B, H, U, wintypes.WPARAM, wintypes.LPARAM)
        bind(self.user32, "SendMessageW", ctypes.c_ssize_t, H, U,
             wintypes.WPARAM, wintypes.LPARAM)
        bind(self.user32, "PeekMessageW", B, ctypes.POINTER(wintypes.MSG), H, U, U, U)
        bind(self.user32, "TranslateMessage", B, ctypes.POINTER(wintypes.MSG))
        bind(self.user32, "DispatchMessageW", ctypes.c_ssize_t,
             ctypes.POINTER(wintypes.MSG))
        self.instance = self.GetModuleHandleW(None)
        self.class_name = f"AMLyrics.NativeButton.{os.getpid()}.{uuid.uuid4().hex}"
        self.wndproc = self.WNDPROC(self._dispatch)
        window_class = WNDCLASS()
        window_class.lpfnWndProc = self.wndproc
        window_class.hInstance = self.instance
        window_class.hbrBackground = 16  # COLOR_BTNFACE + 1, system-owned brush
        window_class.lpszClassName = self.class_name
        if not self.RegisterClassW(ctypes.byref(window_class)):
            _fail("RegisterClassW")

    def _dispatch(self, hwnd, message, wparam, lparam):
        obj = _live.get(hwnd)
        try:
            if message == WM_NCCREATE:
                token = ctypes.cast(lparam, ctypes.POINTER(self.CREATESTRUCT)).contents.lpCreateParams
                obj = _pending.pop(token, None)
                if obj is None:
                    return 0
                obj._container = hwnd
                _live[hwnd] = obj
            if obj is not None:
                if message == 0x21 and obj.placement_mode == "owned_popup":
                    return 3  # MA_NOACTIVATE, preserve Apple as the foreground owner.
                if message == WM_COMMAND:
                    # Both the sender HWND and control ID must match our BUTTON.
                    if (obj._button and lparam == obj._button
                            and (wparam & 0xFFFF) == _CONTROL_ID
                            and ((wparam >> 16) & 0xFFFF) == 0):  # BN_CLICKED
                        try:
                            obj.callback()
                        except BaseException as exc:
                            # Exceptions must never unwind through a ctypes WndProc.
                            obj.last_callback_error = exc
                        return 0
                elif message == WM_SIZE and obj._button:
                    obj._fit_button()
            return self.DefWindowProcW(hwnd, message, wparam, lparam)
        except BaseException as exc:
            if obj is not None:
                obj.last_native_error = exc
            if message == WM_NCCREATE:
                return 0
            return self.DefWindowProcW(hwnd, message, wparam, lparam)
        finally:
            if message == WM_NCDESTROY:
                _live.pop(hwnd, None)
                if obj is not None:
                    obj._container = obj._button = obj._parent = 0
                    obj._parent_identity = (0, 0)


def _get_api():
    global _api
    with _api_lock:
        if _api is None:
            _api = _Win32()
    return _api


def _fail(operation):
    code = ctypes.get_last_error()
    raise NativeButtonError(f"{operation} failed (WinError {code}): {ctypes.FormatError(code)}")


def _rect(value):
    try:
        values = tuple(value)
    except TypeError as exc:
        raise ValueError("rect must be (x, y, width, height)") from exc
    if len(values) != 4 or any(type(v) is not int for v in values):
        raise ValueError("rect must contain four integers")
    x, y, width, height = values
    if width <= 0 or height <= 0 or any(not -(2**31) <= v < 2**31 for v in values):
        raise ValueError("rect requires positive size and signed 32-bit coordinates")
    return x, y, width, height


def _identity(api, hwnd):
    pid = wintypes.DWORD()
    tid = api.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    return int(tid), int(pid.value)


class NativeLyricsButton:
    """Self-owned container -> standard BUTTON, optionally under a foreign HWND.

    Construct on the Python main thread with a zero-argument callback, then
    call attach(parent_hwnd, rect). Callback execution stays on that main thread.
    rect is (x, y, width, height) in the parent's client coordinate system under
    the calling thread's current DPI context. No DPI context is changed. Pump
    this thread's messages throughout the attachment. Callback errors are held
    in last_callback_error; WndProc errors in last_native_error.
    """

    def __init__(self, callback: Callable[[], None], text: str = "桌面歌词", *, placement_mode: str = "child"):
        if threading.current_thread() is not threading.main_thread():
            raise NativeButtonError("Construct NativeLyricsButton on the Python main thread")
        if not callable(callback):
            raise TypeError("callback must be callable")
        if not isinstance(text, str) or "\0" in text:
            raise ValueError("text must be a string without NUL characters")
        if placement_mode not in {"child", "owned_popup"}:
            raise ValueError("placement_mode must be child or owned_popup")
        self.placement_mode = placement_mode
        self.callback, self.text = callback, text
        self.last_callback_error: BaseException | None = None
        self.last_native_error: BaseException | None = None
        self._thread = threading.get_native_id()
        self._container = self._button = self._parent = 0
        self._parent_identity = (0, 0)

    @property
    def container_hwnd(self) -> int:
        return self._container

    @property
    def button_hwnd(self) -> int:
        return self._button

    @property
    def attached(self) -> bool:
        return bool(self._container)

    def _check_thread(self):
        if threading.get_native_id() != self._thread:
            raise NativeButtonError("Call on the Python main thread; marshal work to its message loop")

    def attach(self, parent_hwnd: int, rect: tuple[int, int, int, int]) -> NativeLyricsButton:
        self._check_thread()
        coordinates = _rect(rect)
        if self._container:
            raise NativeButtonError("Already attached; close before attaching again")
        if type(parent_hwnd) is not int or parent_hwnd <= 0 or parent_hwnd >= 2**(ctypes.sizeof(ctypes.c_void_p) * 8):
            raise ValueError("parent_hwnd must be a nonzero pointer-sized integer")
        api = _get_api()
        if not api.IsWindow(parent_hwnd):
            raise NativeButtonError("parent_hwnd is not a live window")
        identity = _identity(api, parent_hwnd)
        if not all(identity):
            raise NativeButtonError("Cannot identify parent window thread/process")
        caller_dpi = api.GetThreadDpiAwarenessContext()
        parent_dpi = api.GetWindowDpiAwarenessContext(parent_hwnd)
        if not caller_dpi or not parent_dpi or not api.AreDpiAwarenessContextsEqual(caller_dpi, parent_dpi):
            raise NativeButtonError("DPI contexts differ or are unknown; attachment refused without changing DPI")
        self._parent, self._parent_identity = parent_hwnd, identity
        self.last_callback_error = self.last_native_error = None
        token = id(self)
        _pending[token] = self
        try:
            is_popup = self.placement_mode == "owned_popup"
            ex_style = WS_EX_NOPARENTNOTIFY | (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE if is_popup else 0)
            style = WS_POPUP if is_popup else WS_CHILD | WS_VISIBLE
            container = api.CreateWindowExW(
                ex_style, api.class_name, "", style,
                *coordinates, parent_hwnd, None, api.instance, token,
            )
            if not container:
                _fail("CreateWindowExW(container)")
            self._button = api.CreateWindowExW(
                WS_EX_NOPARENTNOTIFY, "BUTTON", self.text,
                WS_CHILD | WS_VISIBLE | WS_TABSTOP, 0, 0, coordinates[2], coordinates[3],
                container, _CONTROL_ID, api.instance, None,
            ) or 0
            if not self._button:
                _fail("CreateWindowExW(BUTTON)")
            if not api.IsWindow(parent_hwnd) or _identity(api, parent_hwnd) != identity:
                raise NativeButtonError("Parent disappeared or changed during attachment")
            if self.last_native_error is not None:
                raise NativeButtonError("Native creation callback failed") from self.last_native_error
            return self
        except BaseException:
            self.close()
            raise
        finally:
            _pending.pop(token, None)
            if not self._container:
                self._parent = 0
                self._parent_identity = (0, 0)

    def _fit_button(self):
        api = _get_api()
        area = wintypes.RECT()
        if not api.GetClientRect(self._container, ctypes.byref(area)):
            _fail("GetClientRect(container)")
        if not api.SetWindowPos(self._button, None, 0, 0, area.right, area.bottom,
                                SWP_NOZORDER | SWP_NOACTIVATE):
            _fail("SetWindowPos(BUTTON)")

    def _reap_orphan(self):
        if self._container:
            api = _get_api()
            if (not api.IsWindow(self._parent)
                    or _identity(api, self._parent) != self._parent_identity
                    or api.GetParent(self._container) != self._parent):
                self.close()

    def move(self, rect: tuple[int, int, int, int]) -> None:
        self._check_thread()
        coordinates = _rect(rect)
        self._reap_orphan()
        if not self._container:
            raise NativeButtonError("Not attached (parent may have been destroyed)")
        api = _get_api()
        if not api.SetWindowPos(self._container, None, *coordinates,
                                SWP_NOZORDER | SWP_NOACTIVATE):
            _fail("SetWindowPos(container)")
        # Also resize explicitly: WM_SIZE need not be sent when only position changes.
        self._fit_button()

    def set_visible(self, visible: bool) -> None:
        """Show only this owned window without activation, or hide it.

        The caller must verify the Apple owner is visible and foreground before
        showing an owned popup. Popup attach is always initially hidden.
        """
        self._check_thread()
        if type(visible) is not bool:
            raise ValueError("visible must be bool")
        self._reap_orphan()
        if not self._container:
            if visible:
                raise NativeButtonError("No attached button to show")
            return
        _get_api().ShowWindow(self._container, SW_SHOWNOACTIVATE if visible else SW_HIDE)

    def close(self) -> None:
        self._check_thread()
        if not self._container:
            return
        api = _get_api()
        hwnd = self._container
        if _live.get(hwnd) is not self:
            raise NativeButtonError("Container lifetime no longer belongs to this instance")
        if not api.DestroyWindow(hwnd):
            _fail("DestroyWindow(container)")
        # WM_NCDESTROY removes registry references and clears handles. Never post
        # WM_QUIT: the owning message loop may serve other components.


def pump_messages(limit: int = 100) -> int:
    """Dispatch at most limit queued messages on the caller thread, without waiting.

    This pumps the entire caller thread, so integrate with an existing GUI loop
    instead if one exists. WM_QUIT is left in the queue for the loop's owner.
    Orphans are checked on every call even when no messages are queued. Foreign
    synchronous parenting/dispatch can still block; limit is not a time limit.
    """
    if type(limit) is not int or limit <= 0:
        raise ValueError("limit must be a positive integer")
    api = _get_api()
    current = threading.get_native_id()
    for obj in tuple(_live.values()):
        if obj._thread == current:
            obj._reap_orphan()
    message = wintypes.MSG()
    count = 0
    while count < limit and api.PeekMessageW(ctypes.byref(message), None, 0, 0, 0):
        if message.message == 0x12:  # WM_QUIT must not be consumed.
            break
        if not api.PeekMessageW(ctypes.byref(message), None, 0, 0, 1):
            break
        api.TranslateMessage(ctypes.byref(message))
        api.DispatchMessageW(ctypes.byref(message))
        count += 1
    return count
