"""One hidden host. The only toggle control is a native child inside Apple Music."""
from __future__ import annotations
import argparse
import ctypes
from ctypes import wintypes
from datetime import datetime
import logging
from pathlib import Path
import sys

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'src'))


def main():
    parser=argparse.ArgumentParser(description='Apple Music in-client desktop lyric prototype')
    parser.add_argument('--allow-external-lyrics',action='store_true',help='Explicitly permit LRCLIB metadata requests')
    parser.add_argument('--duration-seconds',type=float,default=0,help='Close after a finite development check')
    args=parser.parse_args()
    if args.duration_seconds<0:
        parser.error('duration must be non-negative')
    log_dir=ROOT/'research/apple-music/work'
    log_dir.mkdir(parents=True,exist_ok=True)
    log_path=log_dir/('integrated-host-'+datetime.now().strftime('%Y%m%d-%H%M%S-%f')+'.log')
    handler=logging.FileHandler(log_path,mode='x',encoding='utf-8')
    handler.setFormatter(logging.Formatter('%(asctime)s %(levelname)s %(message)s'))
    logging.getLogger().addHandler(handler)
    logging.getLogger().setLevel(logging.INFO)
    kernel=ctypes.WinDLL('kernel32',use_last_error=True)
    kernel.CreateMutexW.argtypes=[wintypes.LPVOID,wintypes.BOOL,wintypes.LPCWSTR]
    kernel.CreateMutexW.restype=wintypes.HANDLE
    kernel.CloseHandle.argtypes=[wintypes.HANDLE]
    kernel.CloseHandle.restype=wintypes.BOOL
    mutex=kernel.CreateMutexW(None,False,r'Local\AMDesktopLyrics-1.1792.39682.0')
    if not mutex:
        raise OSError(ctypes.get_last_error(),'Cannot create single-instance mutex')
    exists=ctypes.get_last_error()==183
    try:
        if exists:
            logging.info('host_already_running')
            return 0
        from amlyrics.apple_lyrics_bridge import AppleLyricsBridgeMonitor
        from amlyrics.apple_preferred import ApplePreferredLookup
        from amlyrics.lrclib_search import AppleDisplayLyricsResolver
        from amlyrics.smtc_bridge import SmtcBridgeMonitor
        from amlyrics.live_session import LiveSession
        from amlyrics.integrated_app import IntegratedDesktopLyricsApp
        apple=AppleLyricsBridgeMonitor()
        resolver=AppleDisplayLyricsResolver(enable_external=args.allow_external_lyrics)
        lookup=ApplePreferredLookup(apple,resolver,allow_network=args.allow_external_lyrics)
        session=LiveSession(SmtcBridgeMonitor(),lookup)
        app=IntegratedDesktopLyricsApp(session,apple,network_lookup=lookup)
        try:
            app.start()
            if args.duration_seconds:
                app.root.after(max(1,int(args.duration_seconds*1000)),lambda: app.close(reason="verification_timeout"))
            logging.info('hidden_host_started external_lyrics=%s',args.allow_external_lyrics)
            app.run()
        finally:
            app.close()
        logging.info('host_stopped_cleanly')
        return 0
    except BaseException:
        logging.exception('host_failed')
        return 1
    finally:
        kernel.CloseHandle(mutex)
        handler.close()
        logging.getLogger().removeHandler(handler)


if __name__=='__main__':
    raise SystemExit(main())
