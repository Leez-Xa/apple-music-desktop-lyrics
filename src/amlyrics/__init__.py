"""Apple Music desktop lyrics prototype."""

from .contracts import LyricLine, LyricsResult, PlaybackState, SyncFrame
from .lrc import parse_lrc
from .synchronizer import LyricSynchronizer

__all__ = ["LyricLine", "LyricsResult", "PlaybackState", "SyncFrame", "LyricSynchronizer", "parse_lrc"]
