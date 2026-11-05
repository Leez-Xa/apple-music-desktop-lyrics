"""Offline COM ABI/ownership tests plus strictly hidden, own-window smoke tests."""
from __future__ import annotations

import ctypes
import os
import unittest
from ctypes import wintypes
from unittest.mock import patch

from amlyrics import virtual_desktop as vd

OWN, REFERENCE, CHILD = 100, 200, 300
DESKTOP = "11111111-2222-4333-8444-555555555555"
OTHER = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"


class FakeNative:
    """A genuine six-slot ctypes COM vtable; methods record their slot/arguments."""
    def __init__(self):
        self.calls = []
        self.init_hr = self.create_hr = self.get_hr = self.move_hr = 0
        self.null_interface = self.null_guid = self.no_move = False
        self.pids = {OWN: os.getpid(), REFERENCE: os.getpid() + 1, CHILD: os.getpid()}
        self.desktops = {OWN: OTHER, REFERENCE: DESKTOP}
        self.on_read = None
        self.dpi, self.target_dpi = 10, 20
        self.dpi_get_fails = False
        self.dpi_set_fail_calls = set()
        self.dpi_set_count = 0
        self._callbacks = [
            vd._CALL(vd._ULONG, ctypes.c_void_p)(self.release),
            vd._CALL(vd._HRESULT, ctypes.c_void_p, wintypes.HWND,
                     ctypes.POINTER(wintypes.BOOL))(self.is_current),
            vd._CALL(vd._HRESULT, ctypes.c_void_p, wintypes.HWND,
                     ctypes.POINTER(vd._GUID))(self.read),
            vd._CALL(vd._HRESULT, ctypes.c_void_p, wintypes.HWND,
                     ctypes.POINTER(vd._GUID))(self.move),
        ]
        self.table = (ctypes.c_void_p * 6)()
        for slot, callback in zip([2, 3, 4, 5], self._callbacks):
            self.table[slot] = ctypes.cast(callback, ctypes.c_void_p).value
        class Object(ctypes.Structure):
            _fields_ = [("vtable", ctypes.POINTER(ctypes.c_void_p))]
        self.object = Object(self.table)
        self.pointer = ctypes.cast(ctypes.pointer(self.object), ctypes.c_void_p)

    def CoInitializeEx(self, reserved, flags):
        self.calls.append(("init", flags))
        return self.init_hr

    def CoUninitialize(self):
        self.calls.append(("uninit",))

    def CoCreateInstance(self, clsid, outer, context, iid, output):
        clsid = str(ctypes.cast(clsid, ctypes.POINTER(vd._GUID)).contents)
        iid = str(ctypes.cast(iid, ctypes.POINTER(vd._GUID)).contents)
        self.calls.append(("create", clsid, iid, context))
        if not self.create_hr and not self.null_interface:
            ctypes.cast(output, ctypes.POINTER(ctypes.c_void_p))[0] = self.pointer.value
        return self.create_hr

    def release(self, interface):
        self.calls.append(("release",))
        return 0

    def is_current(self, interface, hwnd, output):
        self.calls.append(("FORBIDDEN_SLOT_3", hwnd))
        return ctypes.c_int32(0x80004001).value

    def read(self, interface, hwnd, output):
        self.calls.append(("get", hwnd))
        result = self.get_hr
        if not result:
            guid = vd._GUID() if self.null_guid else vd._GUID.parse(self.desktops[hwnd])
            ctypes.memmove(output, ctypes.byref(guid), ctypes.sizeof(guid))
        if self.on_read:
            self.on_read(hwnd)
        return ctypes.c_int32(result).value

    def move(self, interface, hwnd, desktop):
        target = str(desktop.contents)
        self.calls.append(("move", hwnd, target))
        if not self.move_hr and not self.no_move:
            self.desktops[hwnd] = target
        return ctypes.c_int32(self.move_hr).value

    def IsWindow(self, hwnd):
        return hwnd in self.pids

    def GetAncestor(self, hwnd, flag):
        return OWN if hwnd == CHILD else hwnd

    def GetWindowThreadProcessId(self, hwnd, output):
        ctypes.cast(output, ctypes.POINTER(wintypes.DWORD))[0] = self.pids.get(hwnd, 0)
        self.calls.append(("owner", hwnd))
        return 7 if hwnd in self.pids else 0

    def GetWindowDpiAwarenessContext(self, hwnd):
        self.calls.append(("get_dpi", hwnd))
        return 0 if self.dpi_get_fails else self.target_dpi

    def SetThreadDpiAwarenessContext(self, value):
        self.calls.append(("set_dpi", value))
        self.dpi_set_count += 1
        if self.dpi_set_count in self.dpi_set_fail_calls:
            return 0
        previous, self.dpi = self.dpi, value
        return previous


class OfflineVirtualDesktopTests(unittest.TestCase):
    def setUp(self):
        self.native = FakeNative()
        self.patch = patch.object(vd, "_get_native", return_value=self.native)
        self.patch.start()
        self.addCleanup(self.patch.stop)

    def count(self, name):
        return sum(call[0] == name for call in self.native.calls)

    def test_official_guid_layout_and_constants(self):
        self.assertEqual(ctypes.sizeof(vd._GUID), 16)
        self.assertEqual(vd._CLSID, "aa509086-5ca9-4c25-8f95-589d3c07b48a")
        self.assertEqual(vd._IID, "a5cd92ff-29be-454c-8d04-d82879fb3f1b")
        self.assertEqual(bytes(vd._GUID.parse(DESKTOP)), __import__("uuid").UUID(DESKTOP).bytes_le)

    def test_get_foreign_window_is_read_only_and_releases_before_uninit(self):
        self.assertEqual(vd.get_window_desktop_id(REFERENCE), DESKTOP)
        self.assertEqual(self.native.calls[-2:], [("release",), ("uninit",)])
        self.assertEqual(self.count("get"), 1)
        self.assertEqual(self.count("move"), 0)
        self.assertEqual(self.count("FORBIDDEN_SLOT_3"), 0)
        self.assertIn(("create", vd._CLSID, vd._IID, 1), self.native.calls)

    def test_s_false_is_success_and_balances_uninitialize(self):
        self.native.init_hr = 1
        vd.get_window_desktop_id(REFERENCE)
        self.assertEqual(self.count("uninit"), 1)

    def test_changed_mode_uses_existing_apartment_without_uninitialize(self):
        self.native.init_hr = ctypes.c_int32(0x80010106).value
        self.assertEqual(vd.get_window_desktop_id(REFERENCE), DESKTOP)
        self.assertEqual(self.count("release"), 1)
        self.assertEqual(self.count("uninit"), 0)

    def test_init_failure_has_hresult_and_no_uninitialize(self):
        self.native.init_hr = 0x8007000E
        with self.assertRaises(vd.VirtualDesktopError) as raised:
            vd.get_window_desktop_id(REFERENCE)
        self.assertEqual(raised.exception.hresult, 0x8007000E)
        self.assertIn("0x8007000E", str(raised.exception))
        self.assertEqual(self.count("create"), 0)
        self.assertEqual(self.count("uninit"), 0)

    def test_create_failure_uninitializes_only_successful_init(self):
        for init, expected in [(0, 1), (0x80010106, 0)]:
            with self.subTest(init=init):
                self.native.calls.clear()
                self.native.init_hr, self.native.create_hr = init, 0x80040154
                with self.assertRaises(vd.VirtualDesktopError) as raised:
                    vd.get_window_desktop_id(REFERENCE)
                self.assertEqual(raised.exception.hresult, 0x80040154)
                self.assertEqual(self.count("release"), 0)
                self.assertEqual(self.count("uninit"), expected)

    def test_null_interface_has_explicit_diagnostic(self):
        self.native.null_interface = True
        with self.assertRaisesRegex(vd.VirtualDesktopError, "null interface"):
            vd.get_window_desktop_id(REFERENCE)
        self.assertEqual(self.count("release"), 0)
        self.assertEqual(self.count("uninit"), 1)

    def test_get_hresult_failure_releases_and_uninitializes(self):
        self.native.get_hr = 0x80070057
        with self.assertRaises(vd.VirtualDesktopError) as raised:
            vd.get_window_desktop_id(REFERENCE)
        self.assertEqual(raised.exception.hresult, 0x80070057)
        self.assertEqual(self.native.calls[-2:], [("release",), ("uninit",)])

    def test_null_guid_is_rejected_without_fallback(self):
        self.native.null_guid = True
        with self.assertRaisesRegex(vd.VirtualDesktopError, "GUID_NULL"):
            vd.move_own_window_to_same_desktop(OWN, REFERENCE)
        self.assertEqual(self.count("move"), 0)
        self.assertEqual(self.count("release"), 1)

    def test_move_only_own_target_reference_only_queried(self):
        self.assertEqual(vd.move_own_window_to_same_desktop(OWN, REFERENCE), DESKTOP)
        operations = [call for call in self.native.calls if call[0] in {"get", "move"}]
        self.assertEqual(operations, [("get", REFERENCE), ("move", OWN, DESKTOP), ("get", OWN)])
        self.assertEqual(self.native.desktops[REFERENCE], DESKTOP)
        self.assertEqual(self.count("owner"), 3)
        self.assertEqual(self.count("FORBIDDEN_SLOT_3"), 0)

    def test_foreign_target_rejected_before_com(self):
        with self.assertRaisesRegex(vd.VirtualDesktopError, "not owned"):
            vd.move_own_window_to_same_desktop(REFERENCE, OWN)
        self.assertEqual(self.count("init"), 0)
        self.assertEqual(self.count("move"), 0)

    def test_ownership_change_after_reference_read_prevents_move(self):
        self.native.on_read = lambda hwnd: self.native.pids.update({OWN: os.getpid() + 1})
        with self.assertRaisesRegex(vd.VirtualDesktopError, "not owned"):
            vd.move_own_window_to_same_desktop(OWN, REFERENCE)
        self.assertEqual(self.count("move"), 0)
        self.assertEqual(self.count("release"), 1)

    def test_move_failure_is_not_retried_and_releases(self):
        self.native.move_hr = 0x80070005
        with self.assertRaises(vd.VirtualDesktopError) as raised:
            vd.move_own_window_to_same_desktop(OWN, REFERENCE)
        self.assertEqual(raised.exception.hresult, 0x80070005)
        self.assertEqual(self.count("move"), 1)
        self.assertEqual(self.count("get"), 1)
        self.assertEqual(self.native.calls[-2:], [("release",), ("uninit",)])

    def test_success_without_matching_desktop_is_diagnosed(self):
        self.native.no_move = True
        with self.assertRaisesRegex(vd.VirtualDesktopError, "verify moved desktop"):
            vd.move_own_window_to_same_desktop(OWN, REFERENCE)
        self.assertEqual(self.count("move"), 1)
        self.assertEqual(self.count("release"), 1)

    def test_error_formatter_failure_does_not_mask_hresult(self):
        self.native.get_hr = 0x80070057
        with patch.object(ctypes, "FormatError", side_effect=OverflowError("format"), create=True):
            with self.assertRaises(vd.VirtualDesktopError) as raised:
                vd.get_window_desktop_id(REFERENCE)
        self.assertEqual(raised.exception.hresult, 0x80070057)
        self.assertEqual(self.count("release"), 1)

    def test_post_move_query_failure_releases_and_reports_without_second_move(self):
        def after_read(hwnd):
            if hwnd == REFERENCE:
                self.native.get_hr = 0x8002802B
        self.native.on_read = after_read
        with self.assertRaises(vd.VirtualDesktopError) as raised:
            vd.move_own_window_to_same_desktop(OWN, REFERENCE)
        self.assertEqual(raised.exception.hresult, 0x8002802B)
        self.assertEqual(self.count("move"), 1)
        self.assertEqual(self.count("release"), 1)
        self.assertEqual(self.count("uninit"), 1)

    def test_invalid_and_child_hwnd_rejected_before_com(self):
        for hwnd in [0, -1, True, None, "100", 2**64, 999, CHILD]:
            with self.subTest(hwnd=hwnd), self.assertRaises(vd.VirtualDesktopError):
                vd.get_window_desktop_id(hwnd)
        self.assertEqual(self.count("init"), 0)

    def test_dpi_context_restores_on_normal_exit(self):
        with vd.same_dpi_context(REFERENCE):
            self.assertEqual(self.native.dpi, 20)
        self.assertEqual(self.native.dpi, 10)
        self.assertEqual(self.count("init"), 0)
        self.assertEqual(self.count("move"), 0)

    def test_dpi_context_restores_on_body_exception(self):
        with self.assertRaisesRegex(ValueError, "body"):
            with vd.same_dpi_context(REFERENCE):
                raise ValueError("body")
        self.assertEqual(self.native.dpi, 10)

    def test_nested_dpi_contexts_restore_in_stack_order(self):
        with vd.same_dpi_context(REFERENCE):
            self.native.target_dpi = 30
            with vd.same_dpi_context(CHILD):
                self.assertEqual(self.native.dpi, 30)
            self.assertEqual(self.native.dpi, 20)
        self.assertEqual(self.native.dpi, 10)

    def test_dpi_query_failure_does_not_set_context(self):
        self.native.dpi_get_fails = True
        with self.assertRaisesRegex(vd.VirtualDesktopError, "GetWindowDpiAwarenessContext"):
            with vd.same_dpi_context(REFERENCE):
                self.fail("must not enter")
        self.assertEqual(self.native.dpi_set_count, 0)

    def test_dpi_enter_failure_does_not_attempt_restore(self):
        self.native.dpi_set_fail_calls = {1}
        with self.assertRaisesRegex(vd.VirtualDesktopError, "enter"):
            with vd.same_dpi_context(REFERENCE):
                self.fail("must not enter")
        self.assertEqual(self.native.dpi_set_count, 1)
        self.assertEqual(self.native.dpi, 10)

    def test_dpi_restore_failure_reports_and_retains_body_exception(self):
        self.native.dpi_set_fail_calls = {2}
        with self.assertRaises(vd.VirtualDesktopError) as raised:
            with vd.same_dpi_context(REFERENCE):
                raise ValueError("body")
        self.assertIn("restore", raised.exception.operation)
        self.assertIsInstance(raised.exception.__context__, ValueError)


@unittest.skipUnless(os.name == "nt", "Hidden Win32 smoke test requires Windows")
class HiddenOwnWindowTests(unittest.TestCase):
    def setUp(self):
        self.user32 = ctypes.WinDLL("user32", use_last_error=True)
        create = self.user32.CreateWindowExW
        create.restype = wintypes.HWND
        create.argtypes = [wintypes.DWORD, wintypes.LPCWSTR, wintypes.LPCWSTR,
                           wintypes.DWORD, ctypes.c_int, ctypes.c_int, ctypes.c_int,
                           ctypes.c_int, wintypes.HWND, wintypes.HMENU,
                           wintypes.HINSTANCE, ctypes.c_void_p]
        self.user32.IsWindowVisible.restype = wintypes.BOOL
        self.user32.IsWindowVisible.argtypes = [wintypes.HWND]
        self.user32.DestroyWindow.restype = wintypes.BOOL
        self.user32.DestroyWindow.argtypes = [wintypes.HWND]
        # No WS_VISIBLE: no taskbar presence, showing, activation or desktop switch.
        self.hwnd = create(0x08000080, "STATIC", "AM hidden desktop verification",
                           0x00CF0000, 0, 0, 320, 160, None, None, None, None)
        self.assertTrue(self.hwnd)
        self.assertFalse(self.user32.IsWindowVisible(self.hwnd))
        vd._require_own_window(vd._get_native(), self.hwnd)

    def tearDown(self):
        try:
            self.assertFalse(self.user32.IsWindowVisible(self.hwnd))
        finally:
            self.assertTrue(self.user32.DestroyWindow(self.hwnd))

    def unsupported(self, exc):
        # Shell may not assign a desktop to a never-shown window. Preserve the
        # concrete HRESULT/diagnostic rather than displaying it to gain a view.
        if exc.hresult == 0x8002802B or "GUID_NULL" in str(exc):
            self.skipTest(f"Hidden-window Shell limitation: {exc}")
        raise exc

    def test_hidden_own_window_desktop_query(self):
        try:
            desktop = vd.get_window_desktop_id(self.hwnd)
        except vd.VirtualDesktopError as exc:
            self.unsupported(exc)
        self.assertEqual(str(__import__("uuid").UUID(desktop)), desktop)
        print(f"hidden own desktop GUID: {desktop}")

    def test_hidden_own_window_move_to_its_own_desktop(self):
        try:
            desktop = vd.move_own_window_to_same_desktop(self.hwnd, self.hwnd)
        except vd.VirtualDesktopError as exc:
            self.unsupported(exc)
        self.assertEqual(vd.get_window_desktop_id(self.hwnd), desktop)
        print(f"hidden own same-desktop move verified: {desktop}")

    def test_hidden_window_dpi_context_restores_real_thread(self):
        get_context = self.user32.GetThreadDpiAwarenessContext
        get_context.restype, get_context.argtypes = wintypes.HANDLE, []
        equal = self.user32.AreDpiAwarenessContextsEqual
        equal.restype, equal.argtypes = wintypes.BOOL, [wintypes.HANDLE, wintypes.HANDLE]
        before = get_context()
        with vd.same_dpi_context(self.hwnd):
            target = vd._get_native().GetWindowDpiAwarenessContext(self.hwnd)
            self.assertTrue(equal(get_context(), target))
        self.assertTrue(equal(get_context(), before))


if __name__ == "__main__":
    unittest.main()
