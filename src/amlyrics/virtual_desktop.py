"""Public IVirtualDesktopManager and scoped thread-only DPI helpers.

No window discovery, showing, activation, desktop switching, process DPI setter,
private Shell interfaces, or persistent configuration changes. Windows standard
libraries only; COM references never outlive a single calling-thread operation.
"""
from __future__ import annotations

import ctypes
import os
import threading
import uuid
from contextlib import contextmanager
from ctypes import wintypes
from collections.abc import Iterator

__all__ = ["VirtualDesktopError", "get_window_desktop_id",
           "move_own_window_to_same_desktop", "same_dpi_context"]

# Official Microsoft SDK: ShObjIdl.h (CLSID), ShObjIdl_core.h (IID/vtable).
_CLSID = "aa509086-5ca9-4c25-8f95-589d3c07b48a"
_IID = "a5cd92ff-29be-454c-8d04-d82879fb3f1b"
_RPC_E_CHANGED_MODE = 0x80010106
_HRESULT, _ULONG = ctypes.c_int32, ctypes.c_uint32
_CALL = getattr(ctypes, "WINFUNCTYPE", ctypes.CFUNCTYPE)
# IUnknown slots 0,1,2; IsWindowOnCurrentVirtualDesktop=3 (never called).
_GET_DESKTOP, _MOVE_DESKTOP, _RELEASE = 4, 5, 2


class VirtualDesktopError(RuntimeError):
    """Structured diagnostic; hresult is unsigned 32-bit, or None for non-COM errors."""

    def __init__(self, operation: str, detail: str, *, hresult: int | None = None,
                 winerror: int | None = None):
        self.operation, self.hresult, self.winerror = operation, hresult, winerror
        suffix = f" (HRESULT 0x{hresult:08X})" if hresult is not None else ""
        if winerror is not None:
            suffix += f" (WinError {winerror})"
        super().__init__(f"{operation}{suffix}: {detail}")


class _GUID(ctypes.Structure):
    _fields_ = [("Data1", ctypes.c_uint32), ("Data2", ctypes.c_uint16),
                ("Data3", ctypes.c_uint16), ("Data4", ctypes.c_ubyte * 8)]

    @classmethod
    def parse(cls, value: str):
        return cls.from_buffer_copy(uuid.UUID(value).bytes_le)

    def __str__(self):
        return str(uuid.UUID(bytes_le=bytes(self)))


class _Native:
    def __init__(self):
        if os.name != "nt":
            raise VirtualDesktopError("platform", "Windows is required")
        ole32 = ctypes.WinDLL("ole32", use_last_error=True)
        user32 = ctypes.WinDLL("user32", use_last_error=True)
        self._libraries = ole32, user32
        def bind(library, name, result, *arguments):
            try:
                function = getattr(library, name)
            except AttributeError as exc:
                raise VirtualDesktopError(name, "Required Windows API is unavailable") from exc
            function.restype, function.argtypes = result, list(arguments)
            setattr(self, name, function)
        bind(ole32, "CoInitializeEx", _HRESULT, ctypes.c_void_p, ctypes.c_uint32)
        bind(ole32, "CoUninitialize", None)
        bind(ole32, "CoCreateInstance", _HRESULT, ctypes.POINTER(_GUID),
             ctypes.c_void_p, ctypes.c_uint32, ctypes.POINTER(_GUID),
             ctypes.POINTER(ctypes.c_void_p))
        bind(user32, "IsWindow", wintypes.BOOL, wintypes.HWND)
        bind(user32, "GetAncestor", wintypes.HWND, wintypes.HWND, wintypes.UINT)
        bind(user32, "GetWindowThreadProcessId", wintypes.DWORD, wintypes.HWND,
             ctypes.POINTER(wintypes.DWORD))
        bind(user32, "GetWindowDpiAwarenessContext", wintypes.HANDLE, wintypes.HWND)
        bind(user32, "SetThreadDpiAwarenessContext", wintypes.HANDLE, wintypes.HANDLE)


def _get_native():
    # Bindings have no COM apartment or interface state; no cross-thread cache.
    return _Native()


def _u32(value):
    return int(value) & 0xFFFFFFFF


def _check_hresult(operation, result):
    code = _u32(result)
    if code & 0x80000000:
        detail = "Windows rejected the operation; no desktop switch or retry was attempted"
        formatter = getattr(ctypes, "FormatError", None)
        if formatter:
            # FormatError takes a signed C long even though diagnostics expose
            # the HRESULT as an unsigned hexadecimal value.
            try:
                detail = formatter(ctypes.c_int32(code).value).strip() + "; " + detail
            except (OSError, OverflowError, ValueError):
                pass  # Never let optional message formatting hide the HRESULT.
        raise VirtualDesktopError(operation, detail, hresult=code)
    return code


def _win_fail(operation):
    code = getattr(ctypes, "get_last_error", lambda: 0)()
    raise VirtualDesktopError(operation, "Windows returned failure", winerror=code)


def _clear_error():
    setter = getattr(ctypes, "set_last_error", None)
    if setter:
        setter(0)


def _validate_window(native, hwnd, *, top_level=True):
    if type(hwnd) is not int or not 0 < hwnd < 2**(ctypes.sizeof(ctypes.c_void_p) * 8):
        raise VirtualDesktopError("validate HWND", "Expected a nonzero pointer-sized integer")
    if not native.IsWindow(hwnd):
        raise VirtualDesktopError("validate HWND", f"Window 0x{hwnd:X} is not alive")
    if top_level and native.GetAncestor(hwnd, 2) != hwnd:  # GA_ROOT, not GA_ROOTOWNER
        raise VirtualDesktopError("validate HWND", "A top-level HWND is required; do not pass a UIA child HWND")


def _require_own_window(native, hwnd):
    _validate_window(native, hwnd)
    pid = wintypes.DWORD()
    tid = native.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    if not tid or pid.value != os.getpid():
        raise VirtualDesktopError("validate ownership",
                                  "Refusing to move a window not owned by this process")


def _com_call(pointer, slot, result_type, argument_types, *arguments):
    table = ctypes.cast(pointer, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents
    method = _CALL(result_type, ctypes.c_void_p, *argument_types)(table[slot])
    return method(pointer, *arguments)


@contextmanager
def _manager(native):
    # Request STA for UI callers. A pre-existing different apartment remains
    # untouched on RPC_E_CHANGED_MODE; CoCreateInstance uses that apartment.
    initialized = False
    interface = ctypes.c_void_p()
    result = _u32(native.CoInitializeEx(None, 2))  # COINIT_APARTMENTTHREADED
    if result != _RPC_E_CHANGED_MODE:
        _check_hresult("CoInitializeEx", result)
        initialized = True  # Includes S_FALSE: every successful init is balanced.
    try:
        clsid, iid = _GUID.parse(_CLSID), _GUID.parse(_IID)
        _check_hresult("CoCreateInstance(IVirtualDesktopManager)", native.CoCreateInstance(
            ctypes.byref(clsid), None, 1, ctypes.byref(iid), ctypes.byref(interface),
        ))  # CLSCTX_INPROC_SERVER
        if not interface.value:
            raise VirtualDesktopError("CoCreateInstance", "Success returned a null interface pointer")
        yield interface
    finally:
        try:
            if interface.value:
                _com_call(interface, _RELEASE, _ULONG, ())
        finally:
            if initialized:
                native.CoUninitialize()


def _read_desktop(interface, hwnd):
    desktop = _GUID()
    _check_hresult("IVirtualDesktopManager.GetWindowDesktopId", _com_call(
        interface, _GET_DESKTOP, _HRESULT,
        (wintypes.HWND, ctypes.POINTER(_GUID)), hwnd, ctypes.byref(desktop),
    ))
    if not any(bytes(desktop)):
        raise VirtualDesktopError("IVirtualDesktopManager.GetWindowDesktopId",
                                  "Shell returned GUID_NULL; keep the window hidden and do not show as a fallback")
    return desktop


def get_window_desktop_id(hwnd: int) -> str:
    """Read a top-level window's desktop GUID, including a foreign reference HWND.

    Returns a canonical lowercase GUID string. Invalid HWND, GUID_NULL, and
    HRESULT failure raise VirtualDesktopError. Does not move or show anything.
    """
    native = _get_native()
    _validate_window(native, hwnd)
    with _manager(native) as interface:
        return str(_read_desktop(interface, hwnd))


def move_own_window_to_same_desktop(own_hwnd: int, reference_hwnd: int) -> str:
    """Move only a current-process top-level window; return its verified desktop GUID.

    Keep the window hidden until this function succeeds. Only GetWindowDesktopId
    is invoked on the reference. Ownership is checked before COM and immediately
    before MoveWindowToDesktop. No retry, activation, showing or desktop switch.
    Invoke from the window's UI thread to serialize its lifetime with the call.
    """
    native = _get_native()
    _require_own_window(native, own_hwnd)
    _validate_window(native, reference_hwnd)
    with _manager(native) as interface:
        target = _read_desktop(interface, reference_hwnd)
        _require_own_window(native, own_hwnd)
        _check_hresult("IVirtualDesktopManager.MoveWindowToDesktop", _com_call(
            interface, _MOVE_DESKTOP, _HRESULT,
            (wintypes.HWND, ctypes.POINTER(_GUID)), own_hwnd, ctypes.byref(target),
        ))
        _require_own_window(native, own_hwnd)
        actual = _read_desktop(interface, own_hwnd)
        if bytes(actual) != bytes(target):
            raise VirtualDesktopError("verify moved desktop", f"Expected {target}, got {actual}; keep hidden")
        return str(target)


@contextmanager
def same_dpi_context(hwnd: int) -> Iterator[None]:
    """Temporarily match a window's DPI context on this thread, then restore it.

    Enter/exit on the same OS thread. Never changes process/system defaults or
    the reference window. Restoration failures raise an explicit diagnostic;
    an exception from the body remains available through exception chaining.
    """
    native = _get_native()
    _validate_window(native, hwnd, top_level=False)
    _clear_error()
    target = native.GetWindowDpiAwarenessContext(hwnd)
    if not target:
        _win_fail("GetWindowDpiAwarenessContext")
    _clear_error()
    previous = native.SetThreadDpiAwarenessContext(target)
    if not previous:
        _win_fail("SetThreadDpiAwarenessContext(enter)")
    owner_thread = threading.get_native_id()
    try:
        yield
    finally:
        if threading.get_native_id() != owner_thread:
            raise VirtualDesktopError("restore DPI context", "Context must exit on its entering OS thread")
        _clear_error()
        if not native.SetThreadDpiAwarenessContext(previous):
            _win_fail("SetThreadDpiAwarenessContext(restore)")
