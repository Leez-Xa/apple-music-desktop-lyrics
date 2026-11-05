from __future__ import annotations

import re
from dataclasses import dataclass

from .contracts import LyricLine

_TIMESTAMP = re.compile(r"\[(?P<minutes>\d{1,3}):(?P<seconds>\d{2})(?:[.:](?P<fraction>\d{1,3}))?\]")
_TAG = re.compile(r"\[(?P<key>[a-zA-Z]+):(?P<value>[^]]*)\]")


@dataclass(frozen=True, slots=True)
class ParsedLrc:
    lines: tuple[LyricLine, ...]
    metadata: dict[str, str]
    offset_ms: int = 0


def _fraction_to_ms(raw: str | None) -> int:
    if not raw:
        return 0
    if len(raw) == 1:
        return int(raw) * 100
    if len(raw) == 2:
        return int(raw) * 10
    return int(raw[:3])


def parse_lrc(text: str, *, end_ms: int | None = None) -> ParsedLrc:
    """Parse LRC text, including repeated timestamps and a global [offset:]."""
    metadata: dict[str, str] = {}
    timestamped: list[tuple[int, str]] = []

    for raw_line in text.splitlines():
        line = raw_line.strip("\ufeff\r\n")
        if not line.strip():
            continue
        tags = list(_TAG.finditer(line))
        for tag in tags:
            metadata[tag.group("key").casefold()] = tag.group("value").strip()

        timestamps = list(_TIMESTAMP.finditer(line))
        if not timestamps:
            continue
        lyric_text = _TIMESTAMP.sub("", line).strip()
        for timestamp in timestamps:
            minutes = int(timestamp.group("minutes"))
            seconds = int(timestamp.group("seconds"))
            start = minutes * 60_000 + seconds * 1_000 + _fraction_to_ms(timestamp.group("fraction"))
            timestamped.append((start, lyric_text))

    try:
        parsed_offset = int(metadata.get("offset", "0"))
    except ValueError:
        parsed_offset = 0

    timestamped = [(max(0, start + parsed_offset), lyric_text) for start, lyric_text in timestamped]
    timestamped.sort(key=lambda item: item[0])
    lines = tuple(
        LyricLine(start_ms=start, end_ms=timestamped[index + 1][0] if index + 1 < len(timestamped) else end_ms, text=lyric_text)
        for index, (start, lyric_text) in enumerate(timestamped)
    )
    return ParsedLrc(lines=lines, metadata=metadata, offset_ms=parsed_offset)
