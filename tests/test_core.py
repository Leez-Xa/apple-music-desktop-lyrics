import unittest

from amlyrics.contracts import IpcMessage, LyricLine, MessageType, PlaybackState
from amlyrics.ipc import JsonLineCodec
from amlyrics.lrc import parse_lrc
from amlyrics.synchronizer import LyricSynchronizer


class LrcTests(unittest.TestCase):
    def test_parse_metadata_repeated_timestamps_and_fraction(self) -> None:
        parsed = parse_lrc("[ar:Tester]\n[offset:100]\n[00:01.2][00:02:345]hello\n[00:03.00]world", end_ms=5000)
        self.assertEqual(parsed.metadata["ar"], "Tester")
        self.assertEqual(parsed.offset_ms, 100)
        self.assertEqual([line.start_ms for line in parsed.lines], [1300, 2445, 3100])
        self.assertEqual(parsed.lines[0].end_ms, 2445)

    def test_empty_and_untimed_lines_are_ignored(self) -> None:
        parsed = parse_lrc("title\n\n[00:00.00]hello")
        self.assertEqual(len(parsed.lines), 1)
        self.assertEqual(parsed.lines[0].text, "hello")


class SynchronizerTests(unittest.TestCase):
    def setUp(self) -> None:
        self.lines = [
            LyricLine(1000, 3000, "one"),
            LyricLine(3000, 5000, "two"),
        ]

    def test_before_first_line_exposes_next(self) -> None:
        frame = LyricSynchronizer(self.lines).frame_at(0)
        self.assertIsNone(frame.current_line)
        self.assertEqual(frame.next_line.text, "one")

    def test_current_and_neighbors(self) -> None:
        frame = LyricSynchronizer(self.lines).frame_at(3500)
        self.assertEqual(frame.current_line.text, "two")
        self.assertEqual(frame.previous_line.text, "one")

    def test_offset(self) -> None:
        frame = LyricSynchronizer(self.lines, offset_ms=500).frame_at(500)
        self.assertEqual(frame.current_line.text, "one")


class ProtocolTests(unittest.TestCase):
    def test_message_round_trip(self) -> None:
        message = IpcMessage(MessageType.TOGGLE_OVERLAY, {"enabled": True}, "req-1")
        decoded = JsonLineCodec.decode(JsonLineCodec.encode(message))
        self.assertEqual(decoded, message)

    def test_playback_key_normalizes_case_and_spaces(self) -> None:
        state = PlaybackState("  Song ", "Artist", "Album")
        self.assertEqual(state.track_key, ("song", "artist", "album"))


if __name__ == "__main__":
    unittest.main()


