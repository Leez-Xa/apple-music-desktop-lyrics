from __future__ import annotations

from concurrent.futures import Executor, Future, ThreadPoolExecutor
from dataclasses import dataclass
from threading import Lock
import math
import time
from typing import Callable

from .contracts import LyricsResult, PlaybackState, SyncFrame
from .playback import PlaybackMonitor
from .synchronizer import LyricSynchronizer


@dataclass(frozen=True, slots=True)
class LiveView:
    state: PlaybackState
    frame: SyncFrame
    source: str
    diagnostic: str


class LiveSession:
    """Bounded playback handoff; mutate lyric/UI state only from tick().

    Generation checks reject lookup results from a previous track. Playback
    time comes from the bridge; this layer never fabricates progress.
    """

    def __init__(self, monitor: PlaybackMonitor,
                 lookup: Callable[[PlaybackState], LyricsResult | None], *,
                 executor: Executor | None = None,
                 retry_interval_seconds: float = 30.0,
                 clock: Callable[[], float] = time.monotonic) -> None:
        if not math.isfinite(retry_interval_seconds) or retry_interval_seconds <= 0:
            raise ValueError("retry interval must be finite and positive")
        self._retry_interval = retry_interval_seconds
        self._clock = clock
        self._next_retry_at = 0.0
        self.monitor = monitor
        self.lookup = lookup
        self._executor = executor or ThreadPoolExecutor(max_workers=1, thread_name_prefix="lyrics")
        self._owns_executor = executor is None
        self._lock = Lock()
        self._pending_state: PlaybackState | None = None
        self.state = PlaybackState()
        self.synchronizer = LyricSynchronizer()
        self.result: LyricsResult | None = None
        self.diagnostic = "等待 Apple Music 会话"
        self._identity: tuple | None = None
        self._generation = 0
        self._pending_lookup: tuple[int, Future] | None = None
        self._started = False
        self._closed = False

    def start(self) -> None:
        if self._closed:
            raise RuntimeError("session is closed")
        if self._started:
            return
        self.monitor.subscribe(self._receive)
        try:
            self.monitor.start()
        except Exception:
            self.monitor.unsubscribe(self._receive)
            raise
        self._started = True

    def _receive(self, state: PlaybackState) -> None:
        with self._lock:
            if not self._closed:
                self._pending_state = state

    def tick(self) -> LiveView:
        if self._closed:
            raise RuntimeError("session is closed")
        with self._lock:
            state, self._pending_state = self._pending_state, None
        if state is not None:
            self.state = state
            identity = (state.source_app, state.track_key, state.duration_ms)
            if identity != self._identity:
                self._identity = identity
                self._generation += 1
                if self._pending_lookup:
                    self._pending_lookup[1].cancel()
                self._pending_lookup = None
                self.result = None
                self.synchronizer.replace(())
                if state.title.strip() and state.source_app:
                    self.diagnostic = "正在查找同步歌词"
                    self._begin_lookup(state)
                else:
                    self.diagnostic = "等待 Apple Music 会话"
        if (self._pending_lookup is None and self.result is None and self.state.title.strip()
                and self.state.source_app and self._clock() >= self._next_retry_at):
            self._begin_lookup(self.state)
        pending = self._pending_lookup
        if pending and pending[1].done():
            generation, future = pending
            self._pending_lookup = None
            if generation == self._generation:
                try:
                    result = future.result()
                    if result is not None and result.track_key != self.state.track_key:
                        raise ValueError("lyrics belong to another track")
                    if result is not None and result.timing_precision != "line":
                        raise ValueError("lyrics have no line timeline")
                    self.result = result
                    self.synchronizer.replace(result.lines if result else ())
                    self.diagnostic = "" if result and result.lines else self._missing_lyrics_message()
                except Exception as exc:
                    self.result = None
                    self.synchronizer.replace(())
                    self.diagnostic = "歌词查询失败：" + type(exc).__name__
        frame = self.synchronizer.frame_at(self.state.position_ms, is_playing=self.state.is_playing)
        if (frame.current_line is not None and frame.current_line.end_ms is not None
                and max(0, self.state.position_ms + self.synchronizer.offset_ms) >= frame.current_line.end_ms):
            frame = SyncFrame(frame.position_ms, None, None, frame.current_line, frame.next_line, frame.is_playing)
        return LiveView(self.state, frame, self.result.source if self.result else "", self.diagnostic)

    def _missing_lyrics_message(self) -> str:
        messages = {
            "disabled": "请先启用在线同步",
            "timeout": "歌词查询超时，稍后重试",
            "network_error": "连接歌词源失败，稍后重试",
            "rate_limited": "歌词源暂时限制请求，稍后重试",
            "not_found": "未找到这首歌的同步歌词",
            "no_synced_match": "当前歌曲版本暂无匹配的同步歌词",
            "missing_metadata": "等待完整歌曲信息",
            "invalid_response": "歌词源返回内容无效，稍后重试",
            "http_error": "歌词源暂时不可用，稍后重试",
        }
        code = getattr(self.lookup, "last_diagnostic", None)
        return messages.get(code, "暂无同步歌词") if isinstance(code, str) else "暂无同步歌词"

    def refresh_lyrics(self) -> None:
        """Refresh a same-song source without showing a previous song result."""
        if self._closed or not self.state.title.strip() or not self.state.source_app:
            return
        self._generation += 1
        if self._pending_lookup:
            self._pending_lookup[1].cancel()
        self._begin_lookup(self.state)

    def _begin_lookup(self, state: PlaybackState) -> None:
        self.diagnostic = "正在查找同步歌词"
        self._next_retry_at = self._clock() + self._retry_interval
        self._pending_lookup = self._generation, self._executor.submit(self.lookup, state)

    def close(self) -> None:
        with self._lock:
            if self._closed:
                return
            self._closed = True
            self._pending_state = None
        if self._started:
            self.monitor.unsubscribe(self._receive)
            self.monitor.stop()
            self._started = False
        if self._pending_lookup:
            self._pending_lookup[1].cancel()
        if self._owns_executor:
            self._executor.shutdown(wait=False, cancel_futures=True)
