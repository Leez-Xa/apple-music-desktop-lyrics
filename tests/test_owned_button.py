"""Owned-popup contract checks; real windows are ALWAYS hidden and process-local.

ShowWindow is mocked before fixture creation. Creation and positioning guards
reject visibility/activation requests before calling User32. Never use an Apple
HWND or run the old GUI verification scripts from this test module.
"""
from __future__ import annotations

import ctypes
import os
import threading
import unittest
from contextlib import ExitStack
from ctypes import wintypes
from unittest.mock import Mock, call, patch

from amlyrics import native_button as native


WS_POPUP = 0x80000000
WS_CHILD = 0x40000000
WS_VISIBLE = 0x10000000
GWL_STYLE = -16
GW_OWNER = 4
SW_HIDE = 0
SW_SHOWNOACTIVATE = 4
SWP_NOACTIVATE = 0x0010
SWP_SHOWWINDOW = 0x0040


class PlacementModeContractTests(unittest.TestCase):
    def test_default_and_explicit_placement_modes_need_no_native_windows(self):
        with patch.object(native, "_get_api", side_effect=AssertionError("No HWND needed")):
            default = native.NativeLyricsButton(lambda: None)
            self.assertEqual(default.placement_mode, "child")
            default.close()
            for mode in ("child", "owned_popup"):
                with self.subTest(mode=mode):
                    obj = native.NativeLyricsButton(lambda: None, "Lyrics", placement_mode=mode)
                    self.assertEqual(obj.placement_mode, mode)
                    obj.close()


@unittest.skipUnless(os.name == "nt", "Hidden Win32 integration requires Windows")
class HiddenOwnedPopupTests(unittest.TestCase):
    def setUp(self):
        self.api = native._get_api()
        self.initial_live = set(native._live)
        self.initial_pending = set(native._pending)
        self.objects = []
        self.created_hwnds = []
        self.owner = 0
        self.stack = ExitStack()
        self.addCleanup(self.stack.close)
        self.addCleanup(self.cleanup_windows)

        # No genuine show/hide calls, including on teardown. In particular, a
        # hidden owner does NOT make it safe to show an owned top-level popup.
        self.show = Mock(name="ShowWindow_NEVER_forward_to_User32", return_value=0)
        self.stack.enter_context(patch.object(self.api, "ShowWindow", self.show))
        self.stack.enter_context(patch.object(self.api.user32, "ShowWindow", self.show))
        for name in ("ShowWindowAsync", "ShowOwnedPopups", "SetForegroundWindow",
                     "SetActiveWindow", "BringWindowToTop", "SetFocus"):
            denied = Mock(name=name, side_effect=AssertionError(name + " forbidden in hidden tests"))
            self.stack.enter_context(patch.object(self.api, name, denied, create=True))
            self.stack.enter_context(patch.object(self.api.user32, name, denied))

        real_create = self.api.CreateWindowExW
        real_position = self.api.SetWindowPos

        def create_hidden(*args):
            style = int(args[3])
            if style & WS_VISIBLE:
                # Only the standard BUTTON may carry WS_VISIBLE, and only
                # underneath our already-hidden, current-process container.
                self.assertEqual(args[1], "BUTTON", "Refusing visible container/owner creation")
                self.assertTrue(style & WS_CHILD, "Refusing visible top-level creation")
                self.assertIn(args[8], self.created_hwnds)
                self.assertFalse(self.api.IsWindowVisible(args[8]))
            hwnd = real_create(*args)
            if hwnd:
                self.created_hwnds.append(hwnd)
            return hwnd

        def position_hidden(*args):
            self.assertFalse(int(args[6]) & SWP_SHOWWINDOW, "Refusing SWP_SHOWWINDOW")
            self.assertTrue(int(args[6]) & SWP_NOACTIVATE, "Refusing activating movement")
            self.assertIn(args[0], self.created_hwnds, "Only fixture HWNDs may move")
            return real_position(*args)

        self.stack.enter_context(patch.object(self.api, "CreateWindowExW", side_effect=create_hidden))
        self.position = self.stack.enter_context(
            patch.object(self.api, "SetWindowPos", side_effect=position_hidden))

        # Use a nonzero location and an ordinary frame so parent-client and
        # screen coordinates cannot accidentally produce the same answer.
        self.owner = self.api.CreateWindowExW(
            0x08000080, "STATIC", "AM hidden owned-popup fixture",
            0x00CF0000, 331, 219, 640, 480, None, None, self.api.instance, None,
        )
        self.assertTrue(self.owner)
        self.assertEqual(native._identity(self.api, self.owner),
                         (threading.get_native_id(), os.getpid()))
        self.assertFalse(self.api.IsWindowVisible(self.owner))

        # Independent read-only queries; do not rely on GetParent alone to
        # distinguish top-level ownership from a WS_CHILD relationship.
        self.query = ctypes.WinDLL("user32", use_last_error=True)
        self.query.GetWindow.argtypes = [wintypes.HWND, wintypes.UINT]
        self.query.GetWindow.restype = wintypes.HWND
        self.query.GetWindowLongW.argtypes = [wintypes.HWND, ctypes.c_int]
        self.query.GetWindowLongW.restype = wintypes.LONG

    def cleanup_windows(self):
        try:
            for obj in self.objects:
                obj.close()
        finally:
            # Also reclaim native handles if a failed attach never returned.
            for hwnd in reversed(self.created_hwnds):
                if self.api.IsWindow(hwnd):
                    self.assertFalse(self.api.IsWindowVisible(hwnd), "Fixture became visible")
                    self.assertTrue(self.api.DestroyWindow(hwnd))
        self.assertEqual(set(native._live), self.initial_live)
        self.assertEqual(set(native._pending), self.initial_pending)

    def attach_popup(self, rect=(72, 91, 120, 32)):
        obj = native.NativeLyricsButton(lambda: None, "Lyrics", placement_mode="owned_popup")
        self.objects.append(obj)
        self.assertIs(obj.attach(self.owner, rect), obj)
        self.assert_hidden(obj)
        return obj

    def assert_hidden(self, obj):
        for hwnd in (self.owner, obj.container_hwnd, obj.button_hwnd):
            self.assertTrue(self.api.IsWindow(hwnd))
            self.assertFalse(self.api.IsWindowVisible(hwnd))
        self.assertIsNone(obj.last_native_error)

    def bounds(self, hwnd):
        rect = wintypes.RECT()
        self.assertTrue(self.api.GetWindowRect(hwnd, ctypes.byref(rect)))
        return rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top

    def style(self, hwnd):
        return self.query.GetWindowLongW(hwnd, GWL_STYLE) & 0xFFFFFFFF

    def test_popup_style_and_owner_chain_are_distinct_from_child_parenting(self):
        obj = self.attach_popup()
        self.assertEqual(obj.placement_mode, "owned_popup")
        style = self.style(obj.container_hwnd)
        self.assertTrue(style & WS_POPUP)
        self.assertFalse(style & WS_CHILD)
        extended = self.query.GetWindowLongW(obj.container_hwnd, -20) & 0xFFFFFFFF
        self.assertTrue(extended & 0x00000080, "WS_EX_TOOLWINDOW required")
        self.assertTrue(extended & 0x08000000, "WS_EX_NOACTIVATE required")
        self.assertEqual(self.query.GetWindow(obj.container_hwnd, GW_OWNER), self.owner)
        self.assertFalse(self.query.GetWindow(self.owner, GW_OWNER))
        self.assertEqual(self.api.GetParent(obj.button_hwnd), obj.container_hwnd)
        self.assertTrue(self.style(obj.button_hwnd) & WS_CHILD)
        for hwnd in (obj.container_hwnd, obj.button_hwnd):
            self.assertEqual(native._identity(self.api, hwnd),
                             (threading.get_native_id(), os.getpid()))
        self.show.assert_not_called()

    def test_attach_is_initially_hidden_without_explicit_show_or_hide(self):
        obj = self.attach_popup()
        self.assertFalse(self.style(obj.container_hwnd) & WS_VISIBLE)
        self.assert_hidden(obj)
        self.show.assert_not_called()

    def test_attach_and_move_use_screen_coordinates_including_negative_values(self):
        obj = self.attach_popup((-910, 114, 120, 39))
        self.assertEqual(self.bounds(obj.container_hwnd), (-910, 114, 120, 39))
        for rect in ((-825, -143, 156, 42), (83, 106, 100, 28)):
            with self.subTest(rect=rect):
                obj.move(rect)
                self.assertEqual(self.bounds(obj.container_hwnd), rect)
                client = wintypes.RECT()
                self.assertTrue(self.api.GetClientRect(obj.container_hwnd, ctypes.byref(client)))
                origin = wintypes.POINT(*self.bounds(obj.button_hwnd)[:2])
                self.assertTrue(self.api.ScreenToClient(obj.container_hwnd, ctypes.byref(origin)))
                self.assertEqual((origin.x, origin.y), (0, 0))
                self.assertEqual(self.bounds(obj.button_hwnd)[2:], (client.right, client.bottom))
                self.assert_hidden(obj)
        self.show.assert_not_called()

    def test_owner_movement_does_not_convert_popup_to_client_coordinates(self):
        obj = self.attach_popup((72, 91, 120, 32))
        self.assertTrue(self.api.SetWindowPos(
            self.owner, None, 517, 307, 700, 510, native.SWP_NOZORDER | SWP_NOACTIVATE))
        # Owned top-level windows do not move like parent-relative children.
        self.assertEqual(self.bounds(obj.container_hwnd), (72, 91, 120, 32))
        obj.move((-600, 124, 130, 38))
        self.assertEqual(self.bounds(obj.container_hwnd), (-600, 124, 130, 38))
        self.assert_hidden(obj)
        self.show.assert_not_called()

    def test_visibility_uses_only_own_container_and_noactivate_via_mock(self):
        obj = self.attach_popup()
        original_owner_bounds = self.bounds(self.owner)
        self.position.reset_mock()
        self.show.assert_not_called()
        # Mock returns zero, as ShowWindow does for a previously hidden window;
        # zero is not an API failure. The genuine function is NEVER invoked.
        obj.set_visible(True)
        self.show.assert_called_once_with(obj.container_hwnd, SW_SHOWNOACTIVATE)
        self.assert_hidden(obj)
        obj.set_visible(False)
        self.assertEqual(self.show.call_args_list, [
            call(obj.container_hwnd, SW_SHOWNOACTIVATE),
            call(obj.container_hwnd, SW_HIDE),
        ])
        self.assertEqual(self.bounds(self.owner), original_owner_bounds)
        for positioned in self.position.call_args_list:
            self.assertEqual(positioned.args[0], obj.container_hwnd)
        self.assert_hidden(obj)

    def test_popup_mouseactivate_returns_noactivate_without_showing(self):
        obj = self.attach_popup()
        # Query our own hidden WndProc directly. Do not send mouse input or
        # activate the owner; WM_MOUSEACTIVATE is just a synchronous message.
        result = self.api.SendMessageW(obj.container_hwnd, 0x0021, self.owner,
                                      1 | (0x0201 << 16))
        self.assertEqual(result, 3, "WM_MOUSEACTIVATE must return MA_NOACTIVATE")
        self.assert_hidden(obj)
        self.show.assert_not_called()

    def test_close_releases_owned_popup_and_button_but_keeps_owner(self):
        obj = self.attach_popup()
        handles = obj.container_hwnd, obj.button_hwnd
        obj.close()
        obj.close()
        self.assertEqual((obj.container_hwnd, obj.button_hwnd), (0, 0))
        self.assertFalse(obj.attached)
        for hwnd in handles:
            self.assertFalse(self.api.IsWindow(hwnd))
        self.assertTrue(self.api.IsWindow(self.owner))
        self.assertFalse(self.api.IsWindowVisible(self.owner))
        self.assertEqual(set(native._live), self.initial_live)
        self.assertEqual(set(native._pending), self.initial_pending)
        self.show.assert_not_called()

    def test_destroying_hidden_owner_releases_its_owned_popup(self):
        obj = self.attach_popup()
        handles = obj.container_hwnd, obj.button_hwnd
        self.assertTrue(self.api.DestroyWindow(self.owner))
        # Same-thread destruction must release native and Python lifetime state.
        self.assertFalse(obj.attached)
        self.assertEqual((obj.container_hwnd, obj.button_hwnd), (0, 0))
        for hwnd in handles:
            self.assertFalse(self.api.IsWindow(hwnd))
        self.assertEqual(set(native._live), self.initial_live)
        self.assertEqual(set(native._pending), self.initial_pending)
        obj.close()
        self.show.assert_not_called()


if __name__ == "__main__":
    unittest.main()
