"""Read only targeted lyric rendering. No screen/desktop switching or activation."""
from __future__ import annotations
import ctypes
from ctypes import wintypes
from datetime import datetime
import json
from pathlib import Path
import sys

ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'src'))
from amlyrics.apple_window import find_apple_music_window
from amlyrics.apple_lyrics_bridge import AppleLyricsBridgeMonitor
from amlyrics.virtual_desktop import same_dpi_context
from PIL import ImageGrab

w=find_apple_music_window()
if w is None: raise RuntimeError('Apple window unavailable')
snapshot=AppleLyricsBridgeMonitor()._read(w)
if snapshot['status']!='observed': raise RuntimeError('Apple lyric panel unavailable')
u=ctypes.WinDLL('user32',use_last_error=True)
u.GetWindowRect.argtypes=[wintypes.HWND,ctypes.POINTER(wintypes.RECT)]
u.GetWindowRect.restype=wintypes.BOOL
u.GetForegroundWindow.restype=wintypes.HWND
foreground=int(u.GetForegroundWindow() or 0)
with same_dpi_context(w.hwnd):
 rect=wintypes.RECT()
 if not u.GetWindowRect(w.hwnd,ctypes.byref(rect)): raise RuntimeError('Window bounds unavailable')
 image=ImageGrab.grab(window=w.hwnd)
 panel=snapshot['lyrics_rect']
 box=(round(panel[0]-rect.left),round(panel[1]-rect.top),round(panel[0]+panel[2]-rect.left),round(panel[1]+panel[3]-rect.top))
 crop=image.crop(box)
 output=ROOT/'research/apple-music/evidence'/('lyric-render-'+datetime.now().strftime('%Y%m%d-%H%M%S-%f')+'.png')
 crop.save(output)
 # Metadata needed to review color evidence; hashes/no text are persisted.
 lines=[{'index':row['index'],'rect':row.get('rect'),'text_length':len(row['text'])} for row in snapshot['lines']]
 record={'window_size':image.size,'window_rect':[rect.left,rect.top,rect.right,rect.bottom],
         'panel_rect':panel,'panel_crop':str(output),'cloaked':w.cloaked,'foreground_unchanged':foreground==int(u.GetForegroundWindow() or 0),
         'lines':lines}
 with output.with_suffix('.json').open('x',encoding='utf-8') as stream:json.dump(record,stream,ensure_ascii=True,indent=2)
 print(json.dumps(record,ensure_ascii=True))
