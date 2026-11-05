"""Conservative placement inside the observed gap between RepeatButton and LCD.

All inputs and the returned rectangle use UIA physical screen pixels. Native
conversion to the parent's client coordinates happens under its DPI context.
No valid gap means no button; existing Apple controls are never moved.
"""
from __future__ import annotations
import math
from typing import Any


def _rect(value):
    if not isinstance(value, (list, tuple)) or len(value) != 4:
        return None
    if any(type(n) not in (int, float) or not math.isfinite(n) for n in value):
        return None
    x, y, w, h = map(float, value)
    return (x, y, w, h) if w > 0 and h > 0 else None


def choose_button_rect(layout: Any, dpi: int) -> tuple[int, int, int, int] | None:
    if not isinstance(layout, dict) or type(dpi) is not int or not 72 <= dpi <= 768:
        return None
    bar = _rect(layout.get("transport_rect"))
    controls = layout.get("controls")
    if bar is None or not isinstance(controls, list):
        return None
    nodes = {}
    obstacles = []
    for item in controls:
        if not isinstance(item, dict):
            return None
        identifier = item.get("automation_id")
        bounds = _rect(item.get("rect"))
        if bounds is None:
            return None
        obstacles.append(bounds)
        if identifier:
            if identifier in nodes:
                return None
            nodes[identifier] = bounds
    if not {"RepeatButton", "LCD", "TransportControl_PlayPauseStop"}.issubset(nodes):
        return None
    scale = dpi / 96
    width, height, margin = round(80 * scale), round(26 * scale), max(2, round(2 * scale))
    repeat, lcd = nodes["RepeatButton"], nodes["LCD"]
    left, right = repeat[0] + repeat[2] + margin, lcd[0] - margin
    if right - left < width:
        return None
    x = math.ceil(left + (right - left - width) / 2)
    y = round(bar[1] + (bar[3] - height) / 2)
    result = (x, y, width, height)
    if x < bar[0] or y < bar[1] or x + width > bar[0] + bar[2] or y + height > bar[1] + bar[3]:
        return None
    for ox, oy, ow, oh in obstacles:
        if x < ox+ow and x+width > ox and y < oy+oh and y+height > oy:
            return None
    return result
