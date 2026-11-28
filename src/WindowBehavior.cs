using System;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
namespace AMLyrics {
 public sealed partial class LyricsWindow {
  async Task ImportAsync() {
   if (importing || closing) return;
   var sample = media.Read();
   if (sample.Track == null || !PlaybackMath.IsFresh(sample, DateTime.UtcNow)) return;
   string key = sample.Track.Key;
   var dialog = new OpenFileDialog { Title = "为当前歌曲选择歌词（UTF-8 LRC / TXT）", Filter = "歌词文件 (*.lrc;*.txt)|*.lrc;*.txt|所有文件 (*.*)|*.*", CheckFileExists = true, Multiselect = false };
   importing = true;
   try {
    if (dialog.ShowDialog(this) != true) return;
    var document = await LocalLyricsProvider.ReadFileAsync(dialog.FileName, CancellationToken.None);
    if (closing) return;
    var latest = media.Read();
    if (latest.Track == null || latest.Track.Key != key || !PlaybackMath.IsFresh(latest, DateTime.UtcNow)) {
     noticeKey = latest.Track == null ? "" : latest.Track.Key; notice = "歌曲已变化，请为当前歌曲重新导入"; return;
    }
    if (document.Lines.Count == 0) throw new InvalidDataException("文件中没有可显示的歌词。");
    local.Assign(key, document); notice = "";
    await lyrics.SelectAsync(latest.Track);
   } catch (Exception ex) {
    if (closing) return;
    noticeKey = key;
    notice = ex is System.Text.DecoderFallbackException ? "读取失败：请将歌词另存为 UTF-8 后重试" : "读取失败：" + ex.Message;
   } finally { importing = false; if (!closing) Refresh(); }
  }
  void ChangeTiming(double delta, bool reset) {
   var sample = media.Read();
   if (sample.Track == null || !PlaybackMath.IsFresh(sample, DateTime.UtcNow)) return;
   if (reset) timing.Reset(sample.Track.Key); else timing.Adjust(sample.Track.Key, delta);
   SavePreferences();
   Refresh();
  }
  void RetryLyrics() {
   var sample = media.Read();
   if (sample.Track == null || !PlaybackMath.IsFresh(sample, DateTime.UtcNow)) return;
   notice = ""; attempts = 1; retryAt = DateTime.UtcNow.AddSeconds(30);
   if (online != null) online.Invalidate(sample.Track);
   pendingSelection = lyrics.SelectAsync(sample.Track); Refresh();
  }
  void Empty(string heading, string detail, string state) { displayedLineIndex = -1; current.Text = heading; next.Text = detail; third.Text = ""; ApplyTrackLayoutMode(false); currentStatusText = state; currentSourceText = "自动歌词 · LRCLIB"; currentLanguageStatus = string.Empty; }
  void UpdateLanguageStatus(bool hasTrans, bool hasRom, NativeLyricsSnapshot snap) {
   if (hasTrans && hasRom) {
    currentLanguageStatus = "当前歌曲已加载：原文 + 音译 + 翻译" + (displaySettings.ShowTranslation && displaySettings.ShowRomanization ? "（紧凑三轨）" : "");
   } else if (hasTrans) {
    currentLanguageStatus = "当前歌曲已加载：原文 + 翻译（未检测到音译）";
   } else if (hasRom) {
    currentLanguageStatus = "当前歌曲已加载：原文 + 音译（未检测到翻译）";
   } else if (snap != null && snap.Availability == NativeLyricsAvailability.CloakedOtherDesktop) {
    currentLanguageStatus = "Apple Music 位于其他虚拟桌面，系统暂隐藏其界面树；切回同一桌面后将自动融合并缓存原生翻译/音译";
   } else if (snap != null && snap.Availability == NativeLyricsAvailability.PanelClosed) {
    currentLanguageStatus = "请在 Apple Music 中展开右侧“歌词”面板，以自动读取原生翻译/音译";
   } else {
    currentLanguageStatus = "当前歌曲暂无可用的翻译或音译（可在 Apple Music 展开歌词或导入多语 LRC）";
   }
  }
  void UpdateSettingsPanel(bool fresh, TrackSnapshot track) {
   var state = lyrics.Read();
   string detail = string.IsNullOrWhiteSpace(currentDetailNotice) ? currentStatusText : currentDetailNotice;
   if (!string.IsNullOrWhiteSpace(hotkeyNotice)) detail = hotkeyNotice + (string.IsNullOrWhiteSpace(detail) ? string.Empty : "；" + detail);
   if (!string.IsNullOrWhiteSpace(configurationNotice)) detail = configurationNotice + (string.IsNullOrWhiteSpace(detail) ? string.Empty : "；" + detail);
   string timingSummary = currentTimingLabel == "默认" ? "默认（提前 1.5 秒）" : currentTimingLabel + "（基于默认提前 1.5 秒）";
   settingsPanel.Apply(displaySettings, currentSourceText, detail, timingSummary,
    fresh && track != null && !state.Loading,
    !importing && fresh && track != null,
    fresh && track != null && state.Document != null && state.Document.IsSynchronized,
    currentLanguageStatus);
  }
  internal void Refresh() {
   if (closing) return;
   currentDetailNotice = string.Empty;
   currentLanguageStatus = string.Empty;
   var sample = media.Read();
   bool fresh = PlaybackMath.IsFresh(sample, DateTime.UtcNow);
   currentTimingLabel = "默认";
   if (!fresh || sample.Status != "Connected" || sample.Track == null) {
    if (selectedKey.Length > 0) { selectedKey = ""; lyrics.Clear(); if (nativeLyrics != null) nativeLyrics.OnTrackChanged(""); }
    currentTrackHeading = "Apple Music 桌面歌词";
    if (!fresh) Empty("媒体连接中断", "正在重新连接；确认 Apple Music 仍在运行", "等待连接");
    else if (sample.Status == "Error") { Empty("媒体读取失败", "稍后自动重试；详情见提示", "获取失败"); currentDetailNotice = sample.Message ?? string.Empty; }
    else Empty("等待 Apple Music", "播放歌曲后，将自动获取并同步显示歌词", "等待播放");
    UpdateSettingsPanel(fresh, sample.Track);
    return;
   }
   var track = sample.Track;
   currentTrackHeading = track.Title + (string.IsNullOrWhiteSpace(track.Artist) ? "" : "  /  " + track.Artist);
   if (selectedKey != track.Key || (selectedDuration <= 0 && track.DurationSeconds > 0)) {
    selectedKey = track.Key; selectedDuration = track.DurationSeconds; notice = "";
    attempts = 1; retryAt = DateTime.UtcNow.AddSeconds(30);
    if (nativeLyrics != null) nativeLyrics.OnTrackChanged(track.Key);
    pendingSelection = lyrics.SelectAsync(track);
   }
   var state = lyrics.Read();
   if (online != null && !state.Loading && state.Document == null && DateTime.UtcNow >= retryAt && attempts < 3) {
    attempts++; retryAt = DateTime.UtcNow.AddSeconds(60);
    pendingSelection = lyrics.SelectAsync(track); state = lyrics.Read();
   }
   if (state.Loading) {
    if (nativeLyrics != null) nativeLyrics.SetNeedLiveFallback(false);
    Empty("正在自动获取歌词", "正在匹配当前歌曲的同步歌词", "自动匹配中");
    UpdateSettingsPanel(fresh, track);
    return;
   }
   if (state.Error.Length > 0) { Empty("暂时无法获取歌词", state.Error + " 可点击重试。", "稍后自动重试"); currentDetailNotice = state.Error; UpdateSettingsPanel(fresh, track); return; }
   if (notice.Length > 0 && noticeKey == track.Key) { Empty("歌词导入未完成", notice, "请重试导入"); UpdateSettingsPanel(fresh, track); return; }
   var doc = state.Document;
   bool hasLocalOverride = doc != null && !string.IsNullOrEmpty(doc.Source) && doc.Source.StartsWith("本地", StringComparison.Ordinal);
   LyricDocument enriched = null;
   if (doc != null && doc.IsSynchronized && nativeLyrics != null && !hasLocalOverride) {
    enriched = nativeLyrics.TryEnrich(track.Key, doc) ?? nativeLyrics.TryGetCachedEnriched(track.Key);
    if (enriched != null) doc = enriched;
   }
   bool docHasRequestedExtra = false;
   if (doc != null && doc.Lines.Count > 0) {
    var probeTracks = LyricsTrackResolver.Resolve(doc, 0, displaySettings.ShowTranslation, displaySettings.ShowRomanization);
    docHasRequestedExtra =
     (displaySettings.ShowTranslation && probeTracks.HasTranslation) ||
     (displaySettings.ShowRomanization && probeTracks.HasRomanization);
   }
   bool wantsNativeFallback = !hasLocalOverride && (doc == null || !doc.IsSynchronized ||
    (enriched == null && (displaySettings.ShowTranslation || displaySettings.ShowRomanization) && !docHasRequestedExtra));
   if (nativeLyrics != null) nativeLyrics.SetNeedLiveFallback(wantsNativeFallback);
   if (wantsNativeFallback && nativeLyrics != null) {
    var nativeSnap = nativeLyrics.ReadSnapshot();
    bool snapSupportsRequested = nativeSnap != null &&
     nativeSnap.Availability == NativeLyricsAvailability.Available &&
     nativeSnap.Entries.Count > 0 &&
     nativeLyrics.IsFreshForCurrentTrack(nativeSnap) &&
     (doc == null || !doc.IsSynchronized ||
      (displaySettings.ShowTranslation && nativeSnap.HasTranslation) ||
      (displaySettings.ShowRomanization && (nativeSnap.HasRomanization ||
       nativeSnap.Entries.Any(e => e != null && !e.IsInstrumental && KoreanRomanizer.ContainsHangul(e.Text)))));
    if (snapSupportsRequested) {
     var liveLines = NativeLyricsAligner.ToLiveLines(nativeSnap);
     var resolvedLive = LyricsTrackResolver.ResolveLines(liveLines, nativeSnap.ActiveIndex, displaySettings.ShowTranslation, displaySettings.ShowRomanization);
     ApplyTrackLayoutMode(resolvedLive.ShowTertiary);
     displayedLineIndex = nativeSnap.ActiveIndex;
     current.Text = resolvedLive.Primary;
     next.Text = resolvedLive.Secondary;
     third.Text = resolvedLive.Tertiary;
     currentSourceText = "Apple Music 原生歌词（实时跟随）";
     currentStatusText = track.IsPlaying ? "原生同步中" : "已暂停";
     UpdateLanguageStatus(resolvedLive.HasTranslation, resolvedLive.HasRomanization, nativeSnap);
     UpdateSettingsPanel(fresh, track);
     return;
    }
   }
   if (doc == null || doc.Lines.Count == 0) {
    Empty("未找到匹配歌词", "已自动查询；可以重试，或使用本地备用歌词", "已连接 Apple Music");
    if (nativeLyrics != null) UpdateLanguageStatus(false, false, nativeLyrics.ReadSnapshot());
    UpdateSettingsPanel(fresh, track);
    return;
   }
   currentSourceText = doc.Source;
   if (!doc.IsSynchronized) {
    displayedLineIndex = -1;
    ApplyTrackLayoutMode(false);
    string firstOrig, firstRom, firstTrans;
    LyricsTrackResolver.ExtractParts(doc.Lines[0], out firstOrig, out firstRom, out firstTrans);
    current.Text = firstOrig;
    if (doc.Lines.Count > 1) {
     string secondOrig, secondRom, secondTrans;
     LyricsTrackResolver.ExtractParts(doc.Lines[1], out secondOrig, out secondRom, out secondTrans);
     next.Text = secondOrig;
    } else {
     next.Text = "此文件没有时间戳";
    }
    third.Text = "";
    currentStatusText = "仅有非同步歌词";
    UpdateLanguageStatus(false, false, nativeLyrics == null ? null : nativeLyrics.ReadSnapshot());
    UpdateSettingsPanel(fresh, track);
    return;
   }
   if (!sample.TimelineReliable) {
    ApplyTrackLayoutMode(false); third.Text = "";
    current.Text = "时间轴不可用"; next.Text = "已加载同步歌词，等待可靠播放位置"; currentStatusText = "同步已暂停";
    UpdateSettingsPanel(fresh, track);
    return;
   }
   double offset = timing.Read(track.Key);
   double position = LyricTiming.Position(sample, DateTime.UtcNow, offset);
   int index = PlaybackMath.FindLine(doc.Lines, position);
   displayedLineIndex = index;
   var resolved = LyricsTrackResolver.Resolve(doc, index, displaySettings.ShowTranslation, displaySettings.ShowRomanization);
   ApplyTrackLayoutMode(resolved.ShowTertiary);
   current.Text = resolved.Primary;
   next.Text = resolved.Secondary;
   third.Text = resolved.Tertiary;
   currentStatusText = track.IsPlaying ? "同步中" : "已暂停";
   double adjustment = offset - LyricTiming.DefaultLeadSeconds;
   if (Math.Abs(adjustment) > 0.001) {
    string label = (adjustment > 0 ? "提前 " : "延后 ") + Math.Abs(adjustment).ToString("0.0") + " 秒";
    currentTimingLabel = label;
   }
   UpdateLanguageStatus(resolved.HasTranslation, resolved.HasRomanization, nativeLyrics == null ? null : nativeLyrics.ReadSnapshot());
   UpdateSettingsPanel(fresh, track);
  }
  internal void SavePreview(string path, FrameworkElement surface = null) {
   surface = surface ?? this;
   surface.UpdateLayout();
   var image = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth), (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
   image.Render(surface); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
   using (var stream = File.Create(path)) encoder.Save(stream);
  }
  static bool FitsTrackVertically(LyricsLinePresenter track) {
   var drawing = VisualTreeHelper.GetDrawing(track);
   if (drawing == null) return false;
   Rect bounds = drawing.Bounds;
   return !bounds.IsEmpty && bounds.Top > 0 && bounds.Bottom < track.ActualHeight;
  }
  void CheckLyricLayout(string folder, string scenario, Action<bool,string> check) {
   overlayChrome.SetHovered(false);
   UpdateLayout();
   Point currentPosition = current.TranslatePoint(new Point(), contentRoot);
   Point nextPosition = next.TranslatePoint(new Point(), contentRoot);
   double windowHeight = ActualHeight;
   check(FitsTrackVertically(current) && FitsTrackVertically(next) && nextPosition.Y + next.ActualHeight <= windowHeight,
    "Window: both lyric drawings, including outline and shadow, fit the " + scenario + " tracks");
   check(nextPosition.Y - currentPosition.Y - current.ActualHeight >= LyricsLayoutMetrics.TrackGap - 1,
    "Window: track gap is reserved at " + scenario);
   SavePreview(Path.Combine(folder, "lyrics-" + scenario + "-idle.png"));
   overlayChrome.SetHovered(true);
   Dispatcher.Invoke(new Action(() => { }), System.Windows.Threading.DispatcherPriority.ContextIdle);
   UpdateLayout();
   var toolbar = overlayChrome.HoverToolbarSurface;
   double toolbarBottom = toolbar.PointToScreen(new Point(0, toolbar.ActualHeight)).Y;
   double windowTop = contentRoot.PointToScreen(new Point()).Y;
   check(Math.Abs(toolbarBottom - windowTop) <= 2, "Window: the floating toolbar appears just above a non-docked overlay at " + scenario);
   check(ActualHeight == windowHeight && current.TranslatePoint(new Point(), contentRoot) == currentPosition && next.TranslatePoint(new Point(), contentRoot) == nextPosition,
    "Window: the independent toolbar leaves lyric positions unchanged at " + scenario);
   SavePreview(Path.Combine(folder, "lyrics-" + scenario + "-hover.png"));
   SavePreview(Path.Combine(folder, "lyrics-" + scenario + "-toolbar.png"), toolbar);
  }
  void CheckDockedLyrics(string folder, string scenario, Action<bool,string> check) {
   overlayChrome.SetHovered(false);
   Rect workArea = SystemParameters.WorkArea;
   Top = workArea.Top - 30;
   KeepWindowVisible();
   UpdateLayout();
   var ink = VisualTreeHelper.GetDrawing(current).Bounds;
   double firstInk = current.TranslatePoint(new Point(0, ink.Top), contentRoot).Y;
   double firstInkPixels = firstInk * VisualTreeHelper.GetDpi(current).DpiScaleY;
   check(Math.Abs(Top - workArea.Top) < 0.1 && firstInkPixels > 0 && firstInkPixels <= 2 && FitsTrackVertically(current) && FitsTrackVertically(next),
    "Window: first lyric ink is " + firstInkPixels.ToString("0.0") + " px from the work-area top without clipping at " + scenario);
   SavePreview(Path.Combine(folder, "lyrics-" + scenario + "-top-docked.png"));
   overlayChrome.SetHovered(true);
   Dispatcher.Invoke(new Action(() => { }), System.Windows.Threading.DispatcherPriority.ContextIdle);
   var toolbar = overlayChrome.HoverToolbarSurface;
   double windowBottom = contentRoot.PointToScreen(new Point(0, contentRoot.ActualHeight)).Y;
   double toolbarTop = toolbar.PointToScreen(new Point()).Y;
   check(Math.Abs(toolbarTop - windowBottom) <= 2 && Math.Abs(Top - workArea.Top) < 0.1,
    "Window: docked toolbar flips below without moving the lyrics at " + scenario);
   overlayChrome.SetHovered(false);
   Top = workArea.Top + 80;
   KeepWindowVisible();
   UpdateLayout();
  }
  void CheckColorPickers(Action<bool,string> check, string folder) {
   displaySettings.ForegroundHex = "#A0F0F1F2";
   displaySettings.StrokeHex = "#123456";
   ApplyDisplaySettings();
   Refresh();
   OpenSettings();
   Dispatcher.Invoke(new Action(() => { }), System.Windows.Threading.DispatcherPriority.ContextIdle);
   var foregroundPicker = ((Grid)settingsPanel.ForegroundColorBox.Parent).Children.OfType<Button>().Single();
   var strokePicker = ((Grid)settingsPanel.StrokeColorBox.Parent).Children.OfType<Button>().Single();
   int changes = 0;
   EventHandler onAppearanceChanged = (sender, args) => changes++;
   settingsPanel.AppearanceChanged += onAppearanceChanged;
   try {
    bool seeded = false;
    settingsPanel.ColorDialogPicker = (owner, initial) => {
     seeded = owner.Handle != IntPtr.Zero && initial.R == 240 && initial.G == 241 && initial.B == 242;
     return System.Drawing.Color.FromArgb(18, 52, 86);
    };
    foregroundPicker.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    check(seeded && changes == 1 && displaySettings.ForegroundHex == "#A0123456" && displaySettings.StrokeHex == "#123456" &&
     ((SolidColorBrush)current.ForegroundBrush).Color == Color.FromArgb(160, 18, 52, 86),
     "Window: foreground picker starts from the saved color and applies only the chosen RGB while retaining alpha");
    settingsPanel.ColorDialogPicker = (owner, initial) => null;
    strokePicker.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    check(changes == 1 && displaySettings.StrokeHex == "#123456" && settingsPanel.IsVisible,
     "Window: cancelling color selection keeps the color and settings panel unchanged");
    settingsPanel.ColorDialogPicker = (owner, initial) => System.Drawing.Color.FromArgb(171, 205, 239);
    strokePicker.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    check(changes == 2 && displaySettings.StrokeHex == "#ABCDEF" && displaySettings.ForegroundHex == "#A0123456" &&
     ((SolidColorBrush)current.StrokeBrush).Color == Color.FromRgb(171, 205, 239),
     "Window: stroke picker updates the outline without changing the foreground");
    settingsPanel.ColorDialogPicker = null;
    settingsPanel.CustomColorDialogPicker = (owner, initial, existing) => Tuple.Create<System.Drawing.Color?, int[]>(null, new[] { 0x00336699 });
    foregroundPicker.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    check(changes == 3 && displaySettings.CustomColors.Length == 16 && displaySettings.CustomColors[0] == 0x00336699,
     "Window: adding a custom color updates and saves CustomColors even when the dialog is cancelled");
    int[] receivedExisting = null;
    settingsPanel.CustomColorDialogPicker = (owner, initial, existing) => {
     receivedExisting = existing;
     return Tuple.Create<System.Drawing.Color?, int[]>(null, existing);
    };
    strokePicker.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    check(receivedExisting != null && receivedExisting.Length == 16 && receivedExisting[0] == 0x00336699 && changes == 3,
     "Window: reopening another color picker receives the saved CustomColors palette");
    displaySettings.CustomColors = new int[0];
    settingsPanel.ApplyAppearance(displaySettings);

    var pickerDialog = new LyricsColorPickerDialog(Color.FromRgb(255, 247, 250), new[] { 0x000000FF }, "选择字体颜色");
    try {
     pickerDialog.ShowActivated = false;
     pickerDialog.SetOwnerAndPosition(handle);
     check(pickerDialog.SelectedCustomSlotIndex == 1 &&
      pickerDialog.CustomSlotFrames[1].BorderBrush == Brushes.Black &&
      pickerDialog.CustomSlotFrames[0].BorderBrush != Brushes.Black,
      "Window: custom color picker defaults selection frame to first empty slot when slot 0 is occupied");

     pickerDialog.SetCurrentColor(Color.FromRgb(0, 0, 255));
     pickerDialog.ClickSaveCustomColor();
     int[] afterBlue = pickerDialog.GetUpdatedCustomColors();
     check(afterBlue.Length == 16 && afterBlue[0] == 0x000000FF && afterBlue[1] == 0x00FF0000 &&
      pickerDialog.SelectedCustomSlotIndex == 2 &&
      pickerDialog.CustomSlotFrames[2].BorderBrush == Brushes.Black,
      "Window: saving blue writes slot 1, keeps slot 0 red, and moves black frame to slot 2");

     pickerDialog.SetCurrentColor(Color.FromRgb(0, 204, 68));
     pickerDialog.ClickCustomSlot(5);
     check(pickerDialog.SelectedCustomSlotIndex == 5 &&
      pickerDialog.SelectedColor == Color.FromRgb(0, 204, 68) &&
      pickerDialog.CustomSlotFrames[5].BorderBrush == Brushes.Black,
      "Window: clicking empty slot 5 moves black frame without clearing current edited color");

     pickerDialog.ClickSaveCustomColor();
     int[] afterGreen = pickerDialog.GetUpdatedCustomColors();
     check(afterGreen.Length == 16 && afterGreen[5] == 0x0044CC00 &&
      pickerDialog.SelectedCustomSlotIndex == 6 &&
      pickerDialog.CustomSlotFrames[6].BorderBrush == Brushes.Black,
      "Window: saving green writes user-selected slot 5 and advances frame to slot 6");

     pickerDialog.ClickCustomSlot(0);
     check(pickerDialog.SelectedCustomSlotIndex == 0 &&
      pickerDialog.SelectedColor == Color.FromRgb(255, 0, 0) &&
      pickerDialog.ClearCustomColorButton.IsEnabled &&
      pickerDialog.CustomSlotFrames[0].BorderBrush == Brushes.Black &&
      pickerDialog.CustomSlotStatusText.Text.Contains("第 1 格"),
      "Window: clicking occupied slot 0 moves black frame, loads red, and enables clear button");

     pickerDialog.Show();
     pickerDialog.UpdateLayout();
     SavePreview(Path.Combine(folder, "lyrics-color-picker-preview.png"), pickerDialog);

     pickerDialog.ClickClearCustomColor();
     int[] afterClear = pickerDialog.GetUpdatedCustomColors();
     check(afterClear.Length == 16 && afterClear[0] == CustomColorPaletteState.EmptyColorRef &&
      pickerDialog.SelectedCustomSlotIndex == 0 &&
      !pickerDialog.ClearCustomColorButton.IsEnabled,
      "Window: clearing slot 0 resets it to empty and keeps selection frame at slot 0");
    } finally {
     pickerDialog.Close();
    }
    check(!pickerDialog.IsVisible, "Window: custom color picker closes cleanly without lingering");
   } finally {
    settingsPanel.AppearanceChanged -= onAppearanceChanged;
    settingsPanel.ColorDialogPicker = null;
    settingsPanel.CustomColorDialogPicker = null;
    CloseSettings();
   }
  }
  internal async Task SmokeAsync(string folder, Action<bool,string> check) {
   check(Topmost && MinWidth == 420 && MaxWidth == 1400 && MinHeight == 96 && MaxHeight == 220, "Window: topmost and bounded size");
   check(InputBindings.Count == 11, "Window: keyboard shortcuts registered");
   check(System.Windows.Automation.AutomationProperties.GetName(settingsPanel).Length > 0, "Window: settings panel has accessible name");
   check(current.Text == "等待 Apple Music", "Window: disconnected state");
   var fake = media as TestMediaSource;
   if (fake == null) throw new InvalidOperationException("Smoke test requires test media source.");
   fake.Sample = TestMediaSource.Connected("界面验证 · 中文与长歌词", 12);
   fake.Sample.Track.IsPlaying = false;
   Refresh(); await pendingSelection; Refresh();
   check(current.Text == "未找到匹配歌词" && settingsPanel.CanImport, "Window: connected without lyrics");
   local.Assign(fake.Sample.Track.Key, LrcParser.Parse("[00:00.00]界面验证\n[00:10.00]你愛我還是他\n[00:20.00]是不是真的他有比我好", "界面测试 · 本地 LRC"));
   await lyrics.SelectAsync(fake.Sample.Track); Refresh();
   check(current.Text == "你愛我還是他" && next.Text == "是不是真的他有比我好", "Window: synchronized traditional Chinese lyric pair");
   SetLocked(false);
   Width = LyricsLayoutMetrics.DefaultWidth;
   displaySettings.FontSize = LyricsLayoutMetrics.DefaultFontSize;
   displaySettings.FontFamilyName = "Segoe UI";
   displaySettings.FontWeightName = "常规";
   displaySettings.ForegroundHex = "#FFF7FA";
   displaySettings.StrokeHex = "#0F0F12";
   displaySettings.StrokeEnabled = displaySettings.ShadowEnabled = true;
   displaySettings.BackgroundOpacity = 0.65;
   ApplyDisplaySettings();
   CheckDockedLyrics(folder, "default", check);
   CheckLyricLayout(folder, "default", check);
   check(overlayChrome.Frame.IsVisible && overlayChrome.Toolbar.IsVisible &&
    ((SolidColorBrush)overlayChrome.Frame.Background).Color.A > 158, "Window: hover shows frame and controls with the saved backdrop opacity");
   check(overlayChrome.Toolbar.HorizontalAlignment == HorizontalAlignment.Center &&
    PresentationSource.FromVisual(overlayChrome.Toolbar) != PresentationSource.FromVisual(overlayChrome.Frame) &&
    overlayChrome.Toolbar.Children.Count == 3 &&
    overlayChrome.SettingsButton == overlayChrome.Toolbar.Children[0] &&
    overlayChrome.LockButton == overlayChrome.Toolbar.Children[1] &&
    ((Button)overlayChrome.Toolbar.Children[0]).Content.ToString() == "⚙" &&
    ((Button)overlayChrome.Toolbar.Children[1]).Content.ToString() == "🔒" &&
    ((Button)overlayChrome.Toolbar.Children[2]).Content.ToString() == "×",
    "Window: toolbar is centered with settings, lock, and close buttons");
   check(ShouldShowChrome(false, true), "Window: keyboard focus reveals chrome when unlocked and settings are closed");
   overlayChrome.SettingsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
   UpdateLayout();
   overlayChrome.Toolbar.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Next));
   check(!overlayChrome.Frame.IsVisible && !overlayChrome.Toolbar.IsVisible && !overlayChrome.SettingsButton.IsVisible &&
    !ShouldShowChrome(true, true) && ((SolidColorBrush)Background).Color.A == 0 && settingsPanel.IsVisible &&
    settingsPanel.TranslationCheckBox != null && settingsPanel.RomanizationCheckBox != null,
    "Window: opening settings hides all hover chrome and keeps it suppressed while pointer hovers over the overlay");
   CloseSettings();
   SetLocked(true); overlayChrome.SetHovered(ShouldShowChrome(true, false)); UpdateLayout();
   check(overlayChrome.IsLocked && overlayChrome.Frame.Visibility == Visibility.Collapsed &&
    overlayChrome.Toolbar.Visibility == Visibility.Collapsed && overlayChrome.UnlockButton.Visibility == Visibility.Visible &&
    overlayChrome.UnlockButton.Content.ToString() == "🔓" &&
    overlayChrome.UnlockButton.IsEnabled && hitTestBridge.IsClickThrough &&
    !leftResizeHandle.IsHitTestVisible && !rightResizeHandle.IsHitTestVisible,
    "Window: locked state keeps main overlay click-through while showing a clickable popup unlock button");
   SavePreview(Path.Combine(folder, "lyrics-locked-preview.png"));
   overlayChrome.SetHovered(ShouldShowChrome(false, true)); UpdateLayout();
   check(!overlayChrome.UnlockButton.IsVisible && !overlayChrome.Toolbar.IsVisible && !overlayChrome.Frame.IsVisible,
    "Window: leaving a locked overlay hides the unlock button even with keyboard focus");
   HideWindow(); RestoreWindow();
   check(hitTestBridge.IsClickThrough, "Window: locked click-through persists across hide and restore");
   overlayChrome.SetHovered(ShouldShowChrome(true, false)); UpdateLayout();
   overlayChrome.UnlockButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
   UpdateLayout();
   check(!overlayChrome.IsLocked && overlayChrome.Frame.Visibility == Visibility.Visible &&
    overlayChrome.Toolbar.Visibility == Visibility.Visible && overlayChrome.UnlockButton.Visibility == Visibility.Collapsed &&
    overlayChrome.LockButton.IsEnabled && !hitTestBridge.IsClickThrough,
    "Window: clicking popup unlock restores the toolbar, lyric frame, and hit testing");
   ResizeLyrics(100); check(current.RequestedFontSize == 48, "Window: maximum font size");
   Width = LyricsLayoutMetrics.MinimumWidth;
   CheckDockedLyrics(folder, "max-narrow", check);
   CheckLyricLayout(folder, "max-narrow", check);
   check(next.Presentation.FontSize < current.Presentation.FontSize, "Window: current and next line retain their font size hierarchy");
   double lyricHeightBeforeSettings = ActualHeight;
   OpenSettings(); UpdateLayout(); SavePreview(Path.Combine(folder, "lyrics-settings-preview.png"));
   check(settingsPanel.Visibility == Visibility.Visible && settingsPanel.ActualWidth > 0 && settingsPanel.CanImport &&
    Math.Abs(ActualHeight - lyricHeightBeforeSettings) < 0.1, "Window: settings panel is a visible floating layer without changing lyric height");
   CloseSettings();
   overlayChrome.SetHovered(false);
   current.Text = "你愛我還是他你愛我還是他";
   UpdateLayout();
   check(current.Presentation.FontSize == 48 && current.Presentation.FontSize > next.Presentation.FontSize &&
    current.Presentation.Overflow > 0 &&
    current.Presentation.Mode == (SystemParameters.ClientAreaAnimation ? LyricsLongLineMode.Marquee : LyricsLongLineMode.Ellipsis) &&
    FitsTrackVertically(current),
    "Window: Method 1 preserves requested font size on overflowing line and scrolls without shrinking");
   current.Text = string.Concat(System.Linq.Enumerable.Repeat("你愛我還是他", 12));
   current.AnimateLongLines = false;
   UpdateLayout();
   check(current.Presentation.FontSize == 48 && current.Presentation.Mode == LyricsLongLineMode.Ellipsis && FitsTrackVertically(current), "Window: long lyrics ellipsize at requested font size when animation is off");
   current.AnimateLongLines = true;
   UpdateLayout();
   check(current.Presentation.FontSize == 48 && current.Presentation.Mode == (SystemParameters.ClientAreaAnimation ? LyricsLongLineMode.Marquee : LyricsLongLineMode.Ellipsis) && FitsTrackVertically(current),
    "Window: long lyrics respect the system animation setting");
   ResizeLyrics(-100); check(current.RequestedFontSize == 16, "Window: minimum font size");
   Width = LyricsLayoutMetrics.DefaultWidth;
   displaySettings.FontSize = LyricsLayoutMetrics.DefaultFontSize;
   ApplyDisplaySettings();
   var triSmokeDoc = new LyricDocument {
    IsSynchronized = true, Source = "三轨验证",
    Lines = new System.Collections.Generic.List<LyricLine> {
     new LyricLine(10, "夢灯籠", "梦灯笼", "yumetourou"),
     new LyricLine(20, "この声が届く", "这声音能传达到", "kono koe ga todoku")
    }
   };
   var triTrack = fake.Sample.Track;
   local.Assign(triTrack.Key, triSmokeDoc);
   await lyrics.SelectAsync(triTrack);
   fake.Sample.Track.PositionSeconds = 11.0;
   displaySettings.ShowTranslation = false; displaySettings.ShowRomanization = false; ApplyDisplaySettings(); Refresh(); UpdateLayout();
   check(current.Text == "夢灯籠" && next.Text == "この声が届く" && third.Visibility == Visibility.Collapsed && MaxHeight == LyricsLayoutMetrics.MaximumHeight,
    "Window: 2-track default renders original and next line with third track collapsed");
   displaySettings.ShowTranslation = true; displaySettings.ShowRomanization = false; ApplyDisplaySettings(); Refresh(); UpdateLayout();
   check(current.Text == "夢灯籠" && next.Text == "梦灯笼" && third.Visibility == Visibility.Collapsed && MaxHeight == LyricsLayoutMetrics.MaximumHeight,
    "Window: translation-only renders original and translation in 2-track mode");
   displaySettings.ShowTranslation = false; displaySettings.ShowRomanization = true; ApplyDisplaySettings(); Refresh(); UpdateLayout();
   check(current.Text == "夢灯籠" && next.Text == "yumetourou" && third.Visibility == Visibility.Collapsed && MaxHeight == LyricsLayoutMetrics.MaximumHeight,
    "Window: romanization-only renders original and romanization in 2-track mode");
   displaySettings.ShowTranslation = true; displaySettings.ShowRomanization = true; ApplyDisplaySettings(); Refresh(); UpdateLayout();
   check(current.Text == "夢灯籠" && next.Text == "yumetourou" && third.Text == "梦灯笼" &&
    third.Visibility == Visibility.Visible && MaxHeight == LyricsLayoutMetrics.MaximumThreeTrackHeight &&
    FitsTrackVertically(current) && FitsTrackVertically(next) && FitsTrackVertically(third),
    "Window: compact 3-track mode renders original, romanization, and translation without vertical clipping");
   displaySettings.ShowTranslation = false; displaySettings.ShowRomanization = false; ApplyDisplaySettings();
   local.Assign(fake.Sample.Track.Key, LrcParser.Parse("[00:00.00]界面验证\n[00:10.00]你愛我還是他\n[00:20.00]是不是真的他有比我好", "界面测试 · 本地 LRC"));
   await lyrics.SelectAsync(fake.Sample.Track);
   ResizeLyrics(-100);
   fake.Sample.Track.IsPlaying = false; fake.Sample.Track.PositionSeconds = 18.4;
   ChangeTiming(0.2, false);
   check(displayedLineIndex == 2 && currentTimingLabel == "提前 0.2 秒" && settingsPanel.TimingStatusText.Contains("提前 0.2 秒"), "Window: manual calibration displays only its adjustment from default");
   ChangeTiming(0, true);
   check(displayedLineIndex == 1 && timing.Read(fake.Sample.Track.Key) == 1.5 && currentTimingLabel == "默认" && settingsPanel.TimingStatusText.Contains("默认"), "Window: reset shows default while retaining the calibrated baseline");
   fake.Sample.TimelineReliable = false; Refresh(); check(current.Text == "时间轴不可用", "Window: missing timeline state");
   fake.Sample.TimelineReliable = true; fake.Sample.Track.IsPlaying = false; Refresh(); check(currentStatusText == "已暂停", "Window: paused state");
   string[] untimedLines = {
    "櫻花覆蓋地面", "風輕輕吹過", "沿著河岸慢慢走過熟悉的街道",
    "還記得那天我們一起看見的晚霞", "那些沒有說完的話留在春天裡"
   };
   string untimedText = string.Join("\n", untimedLines);
   local.Assign(fake.Sample.Track.Key, LrcParser.Parse(untimedText, "文本测试"));
   await lyrics.SelectAsync(fake.Sample.Track); Refresh(); UpdateLayout();
   check(current.Text == untimedLines[0] && next.Text == untimedLines[1] && next.Presentation.Mode == LyricsLongLineMode.Fit,
    "Window: unsynchronized text displays only the first two lines without overflowing the next track");
   var untimedDocument = lyrics.Read().Document;
   check(currentStatusText == "仅有非同步歌词" && displayedLineIndex == -1 && untimedDocument != null && !untimedDocument.IsSynchronized &&
    string.Join("\n", untimedDocument.Lines.ConvertAll(line => line.Text)) == untimedText,
    "Window: unsynchronized text is labeled and preserves every source line");
   hotkeyRegistered = true; lockHotkeyRegistered = false; UpdateHotkeyNotice(); Refresh();
   check(settingsPanel.DetailText.Contains("Ctrl+Alt+Shift+L"), "Window: hotkey registration failure surfaces in settings details");
   hotkeyRegistered = true; lockHotkeyRegistered = true; UpdateHotkeyNotice(); Refresh();
   fake.Sample = new MediaSample { Status = "Error", Message = "Test error" }; Refresh(); check(current.Text == "媒体读取失败", "Window: media error state");
   fake.Sample = new MediaSample { ReceivedAtUtc = DateTime.UtcNow.AddSeconds(-5) }; Refresh(); check(current.Text == "媒体连接中断", "Window: stale media state");
   CheckColorPickers(check, folder);
   HideWindow(); check(!IsVisible, "Window: hide"); RestoreWindow(); check(IsVisible, "Window: restore");

   var startupPreferences = new LyricsWindowPreferences {
    Enabled = false, Locked = true, Width = 637,
    Display = new LyricsDisplaySettings {
     FontSize = 37, ForegroundHex = "#4A91C3", FontFamilyName = "Consolas", FontWeightName = "粗体",
     StrokeHex = "#234567", BackgroundOpacity = 0.35, StrokeEnabled = false, ShadowEnabled = false, AnimateLongLines = false,
     ShowTranslation = true, ShowRomanization = true,
     CustomColors = new[] { 0x00336699, 0x00CC8844 }
    }
   };
   const string startupTrackKey = "smoke-startup-track";
   startupPreferences.TimingOffsets[startupTrackKey] = 2.4;
   string startupSettingsPath = Path.Combine(folder, "startup-preferences-" + Guid.NewGuid().ToString("N"), "settings.json");
   var startupStore = new LyricsPreferencesStore(startupSettingsPath);
   var saved = startupStore.Save(startupPreferences);
   check(saved.Succeeded, "Window: save isolated startup preferences with lyrics disabled");
   if (!saved.Succeeded) return;
   byte[] originalSettings = File.ReadAllBytes(startupSettingsPath);

   HideWindow();
   bool wasHidden = !IsVisible;
   LoadStartupPreferences(startupStore);
   check(wasHidden && IsVisible && startupStore.LastLoadStatus == LyricsPreferencesLoadStatus.Loaded,
    "Window: manual startup shows lyrics despite saved Enabled=false");
   UpdateLayout();
   var applied = ReadPreferencesForPersistence();
   var display = applied.Display;
   var expectedDisplay = startupPreferences.Display;
   double expectedWidth = startupPreferences.Width;
   double dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
   double widthErrorPixels = Math.Max(Math.Abs(applied.Width - expectedWidth), Math.Abs(ActualWidth - expectedWidth)) * dpiScale;
   bool widthPreserved = startupStore.Current.Width == expectedWidth && widthErrorPixels <= 1.000001;
   check(widthPreserved, widthPreserved ? "Window: manual startup preserves width within one device pixel" :
    string.Format("Window: manual startup preserves width (expected={0:R}, loaded={1:R}, applied={2:R}, actual={3:R}, dpiScale={4:R})",
     expectedWidth, startupStore.Current.Width, applied.Width, ActualWidth, dpiScale));

   double appliedTiming = timing.Read(startupTrackKey);
   double expectedTiming = startupPreferences.TimingOffsets[startupTrackKey];
   bool lockAndTimingPreserved = applied.Locked == startupPreferences.Locked && appliedTiming == expectedTiming;
   check(lockAndTimingPreserved, lockAndTimingPreserved ? "Window: manual startup preserves lock and track timing" :
    string.Format("Window: manual startup preserves lock and track timing (lock={0}, expected={1}; timing={2:R}, expected={3:R})",
     applied.Locked, startupPreferences.Locked, appliedTiming, expectedTiming));

   var foregroundBrush = current.ForegroundBrush as SolidColorBrush;
   Color? foregroundColor = foregroundBrush == null ? (Color?)null : foregroundBrush.Color;
   check(display.FontSize == expectedDisplay.FontSize && current.RequestedFontSize == expectedDisplay.FontSize &&
    display.ForegroundHex == expectedDisplay.ForegroundHex && foregroundColor == Color.FromRgb(0x4A, 0x91, 0xC3) &&
    display.FontFamilyName == expectedDisplay.FontFamilyName && display.FontWeightName == expectedDisplay.FontWeightName,
    "Window: manual startup preserves font and foreground color");
   check(display.StrokeHex == expectedDisplay.StrokeHex && display.BackgroundOpacity == expectedDisplay.BackgroundOpacity &&
    display.StrokeEnabled == expectedDisplay.StrokeEnabled && display.ShadowEnabled == expectedDisplay.ShadowEnabled && display.AnimateLongLines == expectedDisplay.AnimateLongLines &&
    display.ShowTranslation == expectedDisplay.ShowTranslation && display.ShowRomanization == expectedDisplay.ShowRomanization &&
    LyricsDisplaySettings.CustomColorsEqual(display.CustomColors, expectedDisplay.CustomColors),
    "Window: manual startup preserves stroke, opacity, custom colors and appearance switches");
   check(!startupStore.Current.Enabled && File.Exists(startupSettingsPath) && originalSettings.SequenceEqual(File.ReadAllBytes(startupSettingsPath)),
    "Window: manual startup leaves the source preferences unchanged and disabled");

   SetEnabled(false);
   check(!IsVisible && !fake.IsPolling && !timer.IsEnabled && fake.StopCount >= 1 && lyrics.Read().Document == null && !ReadPreferencesForPersistence().Enabled,
    "Window: SetEnabled(false) hides overlay, stops media polling, and clears cached lyrics");
   fake.Sample = TestMediaSource.Connected("切歌后旧歌词不可闪回", 15);
   int resetsBeforeEnable = fake.ResetCount;
   SetEnabled(true);
   check(IsVisible && fake.IsPolling && timer.IsEnabled && fake.ResetCount > resetsBeforeEnable &&
    ReadPreferencesForPersistence().Enabled && current.Text == "等待 Apple Music",
    "Window: SetEnabled(true) resets stale sample, restarts polling, and shows overlay without stale track flash");

   LoadStartupPreferences(startupStore, false, true);
   if (userEnabled) StartMediaPolling(false); else { timer.Stop(); StopMediaPolling(); }
   var autoHiddenState = ReadHostState();
   check(!autoHiddenState.Enabled && !autoHiddenState.Visible && !autoHiddenState.Polling,
    "Window: auto-launch keeps overlay hidden and polling stopped when Enabled=false");

   var autoEnabledPrefs = LyricsPreferencesStore.CreateDefaultPreferences();
   autoEnabledPrefs.Enabled = true;
   string autoEnabledPath = Path.Combine(folder, "auto-enabled-" + Guid.NewGuid().ToString("N"), "settings.json");
   var autoEnabledStore = new LyricsPreferencesStore(autoEnabledPath);
   check(autoEnabledStore.Save(autoEnabledPrefs).Succeeded, "Window: save isolated auto-launch Enabled=true preferences");
   LoadStartupPreferences(autoEnabledStore, false, true);
   if (userEnabled) { StartMediaPolling(false); timer.Start(); } else { timer.Stop(); StopMediaPolling(); }
   var autoVisibleState = ReadHostState();
   check(autoVisibleState.Enabled && autoVisibleState.Visible && autoVisibleState.Polling,
    "Window: auto-launch immediately shows overlay and starts polling when Enabled=true");
   StopMediaPolling();
   HideWindow();
   check(ReadPreferencesForPersistence().Enabled && !IsVisible && !fake.IsPolling,
    "Window: client-exit cleanup preserves Enabled=true while stopping polling");

   Rect workArea = SystemParameters.WorkArea;
   double expectedLeft = Math.Round(workArea.Left + 123);
   double expectedTop = Math.Round(workArea.Top);
   var positionedPrefs = LyricsPreferencesStore.CreateDefaultPreferences();
   positionedPrefs.Enabled = true;
   positionedPrefs.Left = expectedLeft;
   positionedPrefs.Top = expectedTop;
   string positionedPath = Path.Combine(folder, "positioned-" + Guid.NewGuid().ToString("N"), "settings.json");
   var positionedStore = new LyricsPreferencesStore(positionedPath);
   check(positionedStore.Save(positionedPrefs).Succeeded, "Window: save isolated non-center window position");
   var positioned = new LyricsWindow(new TestMediaSource(), new LocalLyricsProvider(), true, false, positionedPath) {
    ShowActivated = false,
    ShowInTaskbar = false
   };
   try {
    positioned.LoadStartupPreferences(new LyricsPreferencesStore(positionedPath), true, false);
    positioned.Show();
    positioned.UpdateLayout();
    check(positioned.WindowStartupLocation == WindowStartupLocation.Manual &&
     Math.Abs(positioned.Left - expectedLeft) <= 1.0 &&
     Math.Abs(positioned.Top - expectedTop) <= 1.0 &&
     positioned.current.AlignInkToTop,
     "Window: initial Show() restores saved non-center Left/Top without reverting to CenterScreen");
   } finally {
    positioned.Close();
   }
   check(!LyricsOverlayHover.IsHandleCloaked(IntPtr.Zero) && !LyricsOverlayHover.IsWindowCloaked(this),
    "Window: active overlay is not reported as DWM-cloaked");
  }
 }
 public sealed class TestMediaSource : IMediaSource, IMediaSourceLifecycle {
  public MediaSample Sample = new MediaSample();
  public bool IsPolling { get; private set; }
  public int StartCount { get; private set; }
  public int StopCount { get; private set; }
  public int ResetCount { get; private set; }
  public void StartPolling() { IsPolling = true; StartCount++; }
  public void StopPolling() { IsPolling = false; StopCount++; Sample = new MediaSample(); }
  public void ResetSample() { ResetCount++; Sample = new MediaSample(); }
  public MediaSample Read() { return Sample; }
  public void Dispose() { StopPolling(); }
  public static MediaSample Connected(string title, double position) {
   return new MediaSample { Status = "Connected", TimelineReliable = true, Track = new TrackSnapshot {
    SourceAppId = "AppleMusic.Test", IsAppleMusic = true, Title = title, Artist = "测试数据", PositionSeconds = position,
    DurationSeconds = 180, IsPlaying = true, CapturedAtUtc = DateTime.UtcNow
   } };
  }
 }
}
