from __future__ import annotations
from dataclasses import replace
from datetime import datetime, timedelta, timezone
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

from amlyrics.apple_preferred import ApplePreferredLookup
from amlyrics.apple_window import AppleWindow, is_verified_image
from amlyrics.contracts import PlaybackState, LyricsResult, LyricLine
from amlyrics.integrated_app import IntegratedDesktopLyricsApp, _fresh


class PreferredPipelineTests(unittest.TestCase):
    def setUp(self):
        self.state=PlaybackState(title='Song',artist='Artist — Album',duration_ms=10000,source_app='AppleInc.AppleMusicWin_nzyj5cx40ttqa!App')
        self.external=LyricsResult(self.state.track_key,'lrclib',None,
                                  (LyricLine(1000,5000,'Line one'),LyricLine(5000,10000,'Line two')),confidence=1)
        self.payload={'version':1,'status':'observed','observed_at':datetime.now(timezone.utc).isoformat(),
                      'title':'Song','subtitle':'Artist — Album','lines':[{'index':0,'text':''},
                        {'index':1,'text':'Line⁠ one'},{'index':2,'text':'Line two'}],
                      'timing_status':'not_exposed','current_index':None}
        self.resolver=SimpleNamespace(lookup=Mock(return_value=self.external),last_diagnostic='lrclib')
        self.monitor=SimpleNamespace(latest_snapshot=self.payload)
    def test_apple_text_and_external_times_keep_explicit_provenance(self):
        result=ApplePreferredLookup(self.monitor,self.resolver)(self.state)
        self.assertEqual(result.source,'apple_music+external_timing')
        self.assertEqual(result.lines[0].start_ms,1000)
        self.resolver.lookup.assert_called_once_with(self.state,allow_network=False)
    def test_different_apple_text_uses_verified_external_timeline(self):
        self.payload['lines'][1]['text']='Different version'
        result=ApplePreferredLookup(self.monitor,self.resolver)(self.state)
        self.assertEqual(result.source,'lrclib')
    def test_plain_apple_text_with_no_external_time_stays_unsynchronized(self):
        self.resolver.lookup.return_value=None
        self.assertIsNone(ApplePreferredLookup(self.monitor,self.resolver)(self.state))
    def test_stale_or_wrong_song_apple_text_cannot_be_used(self):
        self.payload['observed_at']=(datetime.now(timezone.utc)-timedelta(seconds=30)).isoformat()
        self.assertEqual(ApplePreferredLookup(self.monitor,self.resolver)(self.state).source,'lrclib')
        self.payload['observed_at']=datetime.now(timezone.utc).isoformat()
        self.payload['title']='Another song'
        self.assertEqual(ApplePreferredLookup(self.monitor,self.resolver)(self.state).source,'lrclib')
    def test_network_permission_is_passed_only_when_explicit(self):
        ApplePreferredLookup(self.monitor,self.resolver,allow_network=True)(self.state)
        self.resolver.lookup.assert_called_once_with(self.state,allow_network=True)


class UserInteractionGateTests(unittest.TestCase):
    def setUp(self):
        self.app=object.__new__(IntegratedDesktopLyricsApp)
        self.app.toggle_count=0
        self.app.suppress_show=False
        self.app.root=SimpleNamespace(state=Mock(return_value='withdrawn'))
        self.app.show=Mock()
        self.app.hide=Mock()
        self.app._api=SimpleNamespace(GetForegroundWindow=Mock(return_value=100),GetAncestor=Mock(return_value=100))
        self.window=AppleWindow(100,101,200,'',False,False,1200,800,144)
    def prepare_online_control(self):
        self.app.network_lookup=SimpleNamespace(allow_network=False,resolver=SimpleNamespace(enable_external=False))
        self.app.session=SimpleNamespace(refresh_lyrics=Mock())
        self.app._online_button=Mock()
    def test_declined_online_request_never_enables_network(self):
        self.prepare_online_control()
        with patch('amlyrics.integrated_app.messagebox.askyesno',return_value=False):
            self.app._request_network()
        self.assertFalse(self.app.network_lookup.allow_network)
        self.assertFalse(self.app.network_lookup.resolver.enable_external)
        self.app.session.refresh_lyrics.assert_not_called()
    def test_user_consent_opens_both_switches_and_refreshes(self):
        self.prepare_online_control()
        with patch('amlyrics.integrated_app.messagebox.askyesno',return_value=True):
            self.app._request_network()
        self.assertTrue(self.app.network_lookup.allow_network)
        self.assertTrue(self.app.network_lookup.resolver.enable_external)
        self.app.session.refresh_lyrics.assert_called_once()
    def test_existing_session_permission_does_not_prompt_again(self):
        self.prepare_online_control()
        self.app.network_lookup.allow_network=True
        with patch('amlyrics.integrated_app.messagebox.askyesno') as prompt:
            self.app._request_network()
        prompt.assert_not_called()
    def test_foreground_apple_click_shows_without_desktop_switch(self):
        with patch('amlyrics.integrated_app.find_apple_music_window',return_value=self.window):
            self.app._on_native_toggle()
        self.app.show.assert_called_once()
    def test_background_or_other_desktop_never_opens_overlay(self):
        for window in (None,replace(self.window,cloaked=True),replace(self.window,minimized=True)):
            with patch('amlyrics.integrated_app.find_apple_music_window',return_value=window):
                self.app._on_native_toggle()
        self.app.show.assert_not_called()
    def test_direct_button_click_on_visible_apple_can_open_overlay(self):
        self.app._api.GetAncestor.return_value=999
        with patch('amlyrics.integrated_app.find_apple_music_window',return_value=self.window):
            self.app._on_native_toggle()
        self.app.show.assert_called_once()
    def test_automation_verification_cannot_show_overlay(self):
        self.app.suppress_show=True
        with patch('amlyrics.integrated_app.find_apple_music_window') as discovery:
            self.app._on_native_toggle()
        discovery.assert_not_called()
        self.app.show.assert_not_called()
        self.assertEqual(self.app.toggle_count,1)
    def test_second_click_hides_existing_overlay(self):
        self.app.root.state.return_value='normal'
        self.app._on_native_toggle()
        self.app.hide.assert_called_once()
        self.app.show.assert_not_called()


class DiscoveryBoundaryTests(unittest.TestCase):
    def test_resolved_package_volume_and_alias_both_verify(self):
        family='AppleInc.AppleMusicWin_1.1792.39682.0_x64__nzyj5cx40ttqa'
        for path in ('D:/WindowsApps/'+family+'/AppleMusic.exe','C:/Program Files/WindowsApps/'+family+'/AppleMusic.exe'):
            self.assertTrue(is_verified_image(path))
        for path in ('D:/work/'+family+'/AppleMusic.exe','D:/WindowsApps/unknown/AppleMusic.exe','D:/WindowsApps/'+family+'/Other.exe'):
            self.assertFalse(is_verified_image(path))
    def test_old_future_and_missing_layout_samples_are_rejected(self):
        self.assertTrue(_fresh({'observed_at':datetime.now(timezone.utc).isoformat()}))
        self.assertFalse(_fresh({'observed_at':(datetime.now(timezone.utc)-timedelta(seconds=11)).isoformat()}))
        self.assertFalse(_fresh({'observed_at':(datetime.now(timezone.utc)+timedelta(seconds=5)).isoformat()}))
        self.assertFalse(_fresh({}))

if __name__=='__main__': unittest.main()
