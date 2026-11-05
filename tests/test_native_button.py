"""Real Win32 tests using ONLY hidden, current-process parents; no Apple HWNDs."""
from __future__ import annotations

import ctypes
import gc
import os
import threading
import unittest
import weakref
from ctypes import wintypes
from unittest.mock import patch

from amlyrics import native_button as native


class NativeButtonValidationTests(unittest.TestCase):
    def test_callback_and_text_validation(self):
        with self.assertRaises(TypeError):
            native.NativeLyricsButton(None)
        with self.assertRaises(ValueError):
            native.NativeLyricsButton(lambda: None, "bad\0text")

    def test_rect_validation_before_any_native_call(self):
        obj = native.NativeLyricsButton(lambda: None)
        for rect in [(), (0, 0, 0, 10), (0, 0, 10, -1), (0, 0, True, 10),
                     (0, 0, 1.5, 10), (2**31, 0, 10, 10), None]:
            with self.subTest(rect=rect), self.assertRaises(ValueError):
                obj.attach(1, rect)
        obj.close()  # No Windows API needed for unattached cleanup.

    def test_construction_outside_main_thread_is_rejected(self):
        errors = []
        def worker():
            try:
                native.NativeLyricsButton(lambda: None)
            except native.NativeButtonError as exc:
                errors.append(str(exc))
        thread = threading.Thread(target=worker)
        thread.start()
        thread.join(3)
        self.assertFalse(thread.is_alive())
        self.assertEqual(len(errors), 1)
        self.assertIn("main thread", errors[0])

    def test_invalid_pump_limit(self):
        for limit in [0, -1, True, 1.5]:
            with self.subTest(limit=limit), self.assertRaises(ValueError):
                native.pump_messages(limit)


@unittest.skipUnless(os.name == "nt", "Win32 integration requires Windows")
class HiddenNativeButtonTests(unittest.TestCase):
    def setUp(self):
        self.api = native._get_api()
        self.initial_live = set(native._live)
        self.initial_pending = set(native._pending)
        # WS_OVERLAPPEDWINDOW without WS_VISIBLE; no activation/show calls.
        self.parent = self.api.CreateWindowExW(
            0x08000080, "STATIC", "AM hidden native-button verification",
            0x00CF0000, 0, 0, 640, 480, None, None, self.api.instance, None,
        )
        self.assertTrue(self.parent)
        self.assertEqual(native._identity(self.api, self.parent),
                         (threading.get_native_id(), os.getpid()))
        self.assertFalse(self.api.IsWindowVisible(self.parent))
        self.objects = []

    def tearDown(self):
        try:
            for obj in self.objects:
                obj.close()
        finally:
            if self.api.IsWindow(self.parent):
                self.assertFalse(self.api.IsWindowVisible(self.parent))
                self.assertTrue(self.api.DestroyWindow(self.parent))
        native.pump_messages()
        self.assertEqual(set(native._live), self.initial_live)
        self.assertEqual(set(native._pending), self.initial_pending)

    def attach(self, callback=lambda: None, rect=(10, 20, 120, 32)):
        obj = native.NativeLyricsButton(callback)
        self.objects.append(obj)
        self.assertIs(obj.attach(self.parent, rect), obj)
        self.assertFalse(self.api.IsWindowVisible(obj.container_hwnd))
        self.assertFalse(self.api.IsWindowVisible(obj.button_hwnd))
        return obj

    def bounds(self, hwnd, relative_to):
        area = wintypes.RECT()
        self.assertTrue(self.api.GetWindowRect(hwnd, ctypes.byref(area)))
        origin = wintypes.POINT(area.left, area.top)
        self.assertTrue(self.api.ScreenToClient(relative_to, ctypes.byref(origin)))
        return origin.x, origin.y, area.right - area.left, area.bottom - area.top

    def test_both_children_owned_here_with_expected_parent_chain(self):
        obj = self.attach()
        self.assertEqual(self.api.GetParent(obj.container_hwnd), self.parent)
        self.assertEqual(self.api.GetParent(obj.button_hwnd), obj.container_hwnd)
        for hwnd in [obj.container_hwnd, obj.button_hwnd]:
            self.assertEqual(native._identity(self.api, hwnd),
                             (threading.get_native_id(), os.getpid()))
        self.assertIsNone(obj.last_native_error)

    def test_queued_standard_button_click_runs_callback_on_owner_thread(self):
        calls = []
        obj = self.attach(lambda: calls.append(threading.get_native_id()))
        self.assertTrue(self.api.PostMessageW(obj.button_hwnd, native.BM_CLICK, 0, 0))
        self.assertEqual(calls, [])
        self.assertGreaterEqual(native.pump_messages(), 1)
        self.assertEqual(calls, [threading.main_thread().native_id])
        self.assertIsNone(obj.last_callback_error)
        self.assertIsNone(obj.last_native_error)

    def test_wrong_sender_or_id_does_not_invoke_callback(self):
        calls = []
        obj = self.attach(lambda: calls.append(True))
        for wparam, lparam in [(1, 0), (2, obj.button_hwnd),
                                (1 | (1 << 16), obj.button_hwnd), (1, self.parent)]:
            self.api.SendMessageW(obj.container_hwnd, native.WM_COMMAND, wparam, lparam)
        self.assertEqual(calls, [])

    def test_move_resize_and_parent_resize_stay_hidden(self):
        obj = self.attach()
        self.assertEqual(self.bounds(obj.container_hwnd, self.parent), (10, 20, 120, 32))
        self.assertTrue(self.api.SetWindowPos(self.parent, None, 0, 0, 800, 600,
                                             native.SWP_NOZORDER | native.SWP_NOACTIVATE))
        obj.move((30, 45, 190, 44))
        self.assertEqual(self.bounds(obj.container_hwnd, self.parent), (30, 45, 190, 44))
        self.assertEqual(self.bounds(obj.button_hwnd, obj.container_hwnd), (0, 0, 190, 44))
        self.assertFalse(self.api.IsWindowVisible(self.parent))
        self.assertIsNone(obj.last_native_error)

    def test_parent_destroy_clears_both_children_and_python_registry(self):
        obj = self.attach()
        handles = [obj.container_hwnd, obj.button_hwnd]
        self.assertTrue(self.api.DestroyWindow(self.parent))
        native.pump_messages()
        self.assertFalse(obj.attached)
        self.assertEqual((obj.container_hwnd, obj.button_hwnd), (0, 0))
        for hwnd in handles:
            self.assertFalse(self.api.IsWindow(hwnd))
        obj.close()
        obj.close()
        with self.assertRaises(native.NativeButtonError):
            obj.move((0, 0, 10, 10))

    def test_close_is_idempotent_preserves_parent_and_allows_reattach(self):
        obj = self.attach()
        handles = [obj.container_hwnd, obj.button_hwnd]
        obj.close()
        obj.close()
        for hwnd in handles:
            self.assertFalse(self.api.IsWindow(hwnd))
        self.assertTrue(self.api.IsWindow(self.parent))
        self.assertIs(obj.attach(self.parent, (0, 0, 80, 24)), obj)
        with self.assertRaises(native.NativeButtonError):
            obj.attach(self.parent, (0, 0, 80, 24))

    def test_callback_can_close_its_own_button(self):
        calls = []
        def clicked():
            calls.append(True)
            obj.close()
        obj = self.attach(clicked)
        self.api.SendMessageW(obj.button_hwnd, native.BM_CLICK, 0, 0)
        self.assertEqual(calls, [True])
        self.assertFalse(obj.attached)
        self.assertIsNone(obj.last_native_error)

    def test_callback_exception_is_contained_and_reported(self):
        def broken():
            raise ValueError("expected callback failure")
        obj = self.attach(broken)
        self.api.SendMessageW(obj.button_hwnd, native.BM_CLICK, 0, 0)
        self.assertIsInstance(obj.last_callback_error, ValueError)
        self.assertTrue(obj.attached)
        self.assertIsNone(obj.last_native_error)

    def test_cross_thread_methods_fail_without_changing_windows(self):
        obj = self.attach()
        errors = []
        def worker():
            for operation in [obj.close, lambda: obj.move((0, 0, 10, 10)),
                              lambda: obj.attach(self.parent, (0, 0, 10, 10))]:
                try:
                    operation()
                except native.NativeButtonError as exc:
                    errors.append(str(exc))
        thread = threading.Thread(target=worker)
        thread.start()
        thread.join(3)
        self.assertFalse(thread.is_alive())
        self.assertEqual(len(errors), 3)
        self.assertTrue(obj.attached)

    def test_invalid_parent_and_dpi_mismatch_create_no_windows(self):
        obj = native.NativeLyricsButton(lambda: None)
        self.objects.append(obj)
        with self.assertRaises(native.NativeButtonError):
            obj.attach(1, (0, 0, 100, 30))
        with patch.object(self.api, "AreDpiAwarenessContextsEqual", return_value=False), \
                patch.object(self.api, "CreateWindowExW", wraps=self.api.CreateWindowExW) as create:
            with self.assertRaisesRegex(native.NativeButtonError, "DPI"):
                obj.attach(self.parent, (0, 0, 100, 30))
            create.assert_not_called()
        self.assertFalse(obj.attached)

    def test_button_creation_failure_rolls_back_container(self):
        obj = native.NativeLyricsButton(lambda: None)
        self.objects.append(obj)
        original = self.api.CreateWindowExW
        created = []
        def create(*args):
            if args[1] == "BUTTON":
                return 0
            hwnd = original(*args)
            created.append(hwnd)
            return hwnd
        with patch.object(self.api, "CreateWindowExW", side_effect=create):
            with self.assertRaises(native.NativeButtonError):
                obj.attach(self.parent, (0, 0, 100, 30))
        self.assertEqual(len(created), 1)
        self.assertFalse(self.api.IsWindow(created[0]))
        self.assertFalse(obj.attached)

    def test_live_registry_keeps_instance_until_parent_destroy(self):
        obj = native.NativeLyricsButton(lambda: None).attach(self.parent, (0, 0, 100, 30))
        reference = weakref.ref(obj)
        del obj
        gc.collect()
        self.assertIsNotNone(reference())
        self.api.DestroyWindow(self.parent)
        native.pump_messages()
        gc.collect()
        self.assertIsNone(reference())

    def test_repeated_attach_close_has_no_per_instance_registry_leak(self):
        class_name = self.api.class_name
        obj = native.NativeLyricsButton(lambda: None)
        self.objects.append(obj)
        for _ in range(20):
            obj.attach(self.parent, (0, 0, 100, 30))
            obj.close()
            self.assertEqual(set(native._live), self.initial_live)
            self.assertEqual(set(native._pending), self.initial_pending)
        self.assertEqual(self.api.class_name, class_name)

    def test_pump_preserves_quit_for_outer_loop(self):
        self.assertTrue(self.api.PostMessageW(None, 0x12, 17, 0))
        try:
            native.pump_messages()
            message = wintypes.MSG()
            self.assertTrue(self.api.PeekMessageW(ctypes.byref(message), None, 0x12, 0x12, 0))
            self.assertEqual(message.wParam, 17)
        finally:
            message = wintypes.MSG()
            self.api.PeekMessageW(ctypes.byref(message), None, 0x12, 0x12, 1)


if __name__ == "__main__":
    unittest.main()
