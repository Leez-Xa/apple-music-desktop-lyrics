from __future__ import annotations

from contextlib import nullcontext
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

from amlyrics.apple_window import AppleWindow
from amlyrics.integrated_app import IntegratedDesktopLyricsApp
from amlyrics.live_overlay import DesktopLyricsOverlay, LiveLyricsOverlay


class FakeRoot:
    def __init__(self):
        self.bindings = {}
        self.protocols = {}
        self.after_calls = []
        self.withdraw_calls = 0
        self.destroy_calls = 0
        self._state = "withdrawn"

    def title(self, _value):
        pass

    def configure(self, **_kwargs):
        pass

    def attributes(self, *_args):
        pass

    def overrideredirect(self, _value):
        pass

    def geometry(self, _value):
        pass

    def withdraw(self):
        self.withdraw_calls += 1
        self._state = "withdrawn"

    def deiconify(self):
        self._state = "normal"

    def lift(self):
        pass

    def bind(self, event, callback):
        self.bindings[event] = callback

    def protocol(self, name, callback):
        self.protocols[name] = callback

    def after(self, delay, callback):
        self.after_calls.append((delay, callback))

    def destroy(self):
        self.destroy_calls += 1

    def state(self):
        return self._state


class FakeVariable:
    def __init__(self, value=""):
        self.value = value

    def set(self, value):
        self.value = value


class FakeWidget:
    instances = []

    def __init__(self, _parent=None, **kwargs):
        self.kwargs = kwargs
        self.configure_calls = []
        self.pack_calls = []
        self.__class__.instances.append(self)

    def configure(self, **kwargs):
        self.configure_calls.append(kwargs)

    def pack(self, *args, **kwargs):
        self.pack_calls.append((args, kwargs))

    def bind(self, *_args):
        pass


class FakeNative:
    def __init__(self):
        self.attached = False
        self.container_hwnd = 0
        self.close_calls = 0
        self.attach_calls = []
        self.move_calls = []
        self.visible_calls = []

    def close(self):
        self.close_calls += 1
        self.attached = False
        self.container_hwnd = 0

    def attach(self, parent, rect):
        self.attach_calls.append((parent, rect))
        self.attached = True
        self.container_hwnd = 9001
        return self

    def move(self, rect):
        self.move_calls.append(rect)

    def set_visible(self, visible):
        self.visible_calls.append(visible)


class FakeNativeApi:
    def GetForegroundWindow(self):
        return 0

    def GetWindow(self, *_args):
        return 0

    def GetWindowLongPtrW(self, *_args):
        return 0

    def SetWindowPos(self, *_args):
        return True


class OverlayEventLifecycleTests(unittest.TestCase):
    def test_close_host_false_routes_exit_button_window_close_and_escape_to_hide(self):
        FakeWidget.instances = []
        session = Mock()
        app = object.__new__(IntegratedDesktopLyricsApp)

        with patch("amlyrics.live_overlay.tk.Tk", FakeRoot), \
                patch("amlyrics.live_overlay.tk.StringVar", FakeVariable), \
                patch("amlyrics.live_overlay.tk.Label", FakeWidget), \
                patch("amlyrics.live_overlay.tk.Frame", FakeWidget), \
                patch("amlyrics.live_overlay.tk.Button", FakeWidget):
            LiveLyricsOverlay.__init__(app, session, close_host_on_exit=False)

        app.native = Mock()
        root = app.root
        initial_withdraws = root.withdraw_calls
        exit_button = next(widget for widget in FakeWidget.instances
                           if widget.kwargs.get("text") == "退出")

        exit_button.kwargs["command"]()
        root.protocols["WM_DELETE_WINDOW"]()
        root.bindings["<Escape>"](None)

        self.assertFalse(app.close_host_on_exit)
        self.assertEqual(root.withdraw_calls - initial_withdraws, 3)
        self.assertEqual(root.destroy_calls, 0)
        session.close.assert_not_called()
        app.native.close.assert_not_called()

    def test_explicit_integrated_close_still_closes_session_native_and_apple_bridge(self):
        app = object.__new__(IntegratedDesktopLyricsApp)
        app._closed = False
        app.native = Mock()
        app.apple_lyrics = SimpleNamespace(stop=Mock())
        app.session = SimpleNamespace(close=Mock())

        with patch.object(DesktopLyricsOverlay, "close") as overlay_close:
            app.close()

        app.native.close.assert_called_once_with()
        app.apple_lyrics.stop.assert_called_once_with()
        app.session.close.assert_called_once_with()
        overlay_close.assert_called_once_with()
        self.assertTrue(app._closed)


class AppleWindowRecoveryTests(unittest.TestCase):
    def test_missing_apple_window_past_ten_seconds_keeps_host_alive_and_reattaches(self):
        app = object.__new__(IntegratedDesktopLyricsApp)
        app._closed = False
        app._seen_apple = True
        app._missing_since = None
        app._native_parent = 0
        app._button_visible = False
        app._last_placement = None
        app._last_layout_error = None
        app._last_apple_identity = ("Song", "Artist", "paused")
        app.root = SimpleNamespace(after=Mock())
        app.native = FakeNative()
        app._api = FakeNativeApi()
        app.session = Mock()
        apple_stop = Mock()
        app.apple_lyrics = SimpleNamespace(latest_snapshot={
            "observed_at": "not-used-by-mock",
            "file_version": "1.7.0.155",
            "process_id": 200,
            "uia_root_handle": 101,
            "native_window_rect": [10, 20, 1210, 820],
            "layout": {"button": [1, 2, 40, 20]},
            "title": "Song",
            "subtitle": "Artist",
            "status": "paused",
        }, stop=apple_stop)
        window = AppleWindow(222, 101, 200, "", False, False, 1200, 800, 144)

        with patch("amlyrics.integrated_app.find_apple_music_window",
                   side_effect=[None, None, window]), \
                patch("amlyrics.integrated_app.time.monotonic",
                      side_effect=[100.0, 111.1, 111.1, 111.1]), \
                patch("amlyrics.integrated_app._fresh", return_value=True), \
                patch("amlyrics.integrated_app.physical_window_rect",
                      return_value=(10, 20, 1210, 820)), \
                patch("amlyrics.integrated_app.choose_button_rect",
                      return_value=(1, 2, 40, 20)), \
                patch("amlyrics.integrated_app._client_rect",
                      return_value=(1, 2, 40, 20)), \
                patch("amlyrics.integrated_app.same_dpi_context",
                      return_value=nullcontext()):
            app._poll_native()
            app._poll_native()
            app._poll_native()

        self.assertFalse(app._closed)
        self.assertEqual(app.native.close_calls, 2)
        self.assertEqual(app.native.attach_calls, [(222, (1, 2, 40, 20))])
        self.assertTrue(app.native.attached)
        self.assertEqual(app._native_parent, 222)
        self.assertEqual(app.native.visible_calls, [True])
        self.assertEqual(app.root.after.call_count, 3)
        app.session.close.assert_not_called()
        apple_stop.assert_not_called()


if __name__ == "__main__":
    unittest.main()


