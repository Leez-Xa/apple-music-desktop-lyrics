using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using System.Runtime.InteropServices;

namespace AMLyrics {
 public sealed partial class LyricsWindow : Window {
  // Neutral OKLCH roles mapped to sRGB: ink L~0.96, muted L~0.75.
  internal static readonly Brush Ink = Brush("#F4F4F5");
  internal static readonly Brush Muted = Brush("#B0B0B8");
  readonly LyricsLinePresenter current = new LyricsLinePresenter();
  readonly LyricsLinePresenter next = new LyricsLinePresenter();
  readonly LyricsLinePresenter third = new LyricsLinePresenter();
  readonly IMediaSource media;
  readonly LocalLyricsProvider local;
  readonly LyricSession lyrics;
  readonly LrclibLyricsProvider online;
  readonly AppleMusicNativeLyricsService nativeLyrics;
  readonly LyricTiming timing = new LyricTiming();
  readonly LyricsDisplaySettings displaySettings = new LyricsDisplaySettings();
  readonly LyricsOverlayChrome overlayChrome;
  readonly LyricsSettingsPanel settingsPanel;
  readonly LyricsHitTestBridge hitTestBridge;
  readonly LyricsPreferencesStore preferencesStore;
  readonly bool smokeMode;
  readonly bool autoLaunchMode;
  bool userEnabled;
  bool startupInitialized;
  bool applyingPreferences;
  bool threeTrackActive;
  string configurationNotice = string.Empty;
  string hotkeyNotice = string.Empty;
  string currentTrackHeading = "Apple Music 桌面歌词";
  string currentStatusText = "等待播放";
  string currentSourceText = "自动歌词 · LRCLIB";
  string currentDetailNotice = string.Empty;
  string currentTimingLabel = "默认";
  string currentLanguageStatus = string.Empty;
  readonly Grid contentRoot;
  readonly Border leftResizeHandle;
  readonly Border rightResizeHandle;
  readonly Border topResizeHandle;
  readonly Border bottomResizeHandle;
  int attempts;
  double selectedDuration;
  DateTime retryAt = DateTime.MaxValue;
  readonly DispatcherTimer timer;
  readonly Forms.NotifyIcon tray;
  readonly Forms.ToolStripMenuItem trayLockItem;
  string selectedKey = "";
  string notice = "";
  string noticeKey = "";
  string versionNotice = "";
  string versionNoticeKey = "";
  bool importing, switchingVersion, closing;
  double lyricSize = LyricsLayoutMetrics.DefaultFontSize;
  int displayedLineIndex = -1;
  IntPtr handle;
  bool hotkeyRegistered;
  bool lockHotkeyRegistered;
  internal enum ResizeEdge { Left, Right, Top, Bottom }
  bool dragging;
  bool resizing;
  ResizeEdge resizingEdge;
  Point resizeStartScreen;
  double resizeStartWidth;
  double resizeStartLeft;
  double resizeStartHeight;
  double resizeStartBottom;
  double resizeStartFontSize;
  Task pendingSelection = Task.FromResult(0);
  [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
  [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
  static Brush Brush(string hex) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex); b.Freeze(); return b; }
  public LyricsWindow(IMediaSource media, LocalLyricsProvider local, bool smoke, bool autoLaunch = false, string settingsPath = null) {
   smokeMode = smoke;
   autoLaunchMode = autoLaunch;
   userEnabled = smoke || !autoLaunch;
   preferencesStore = new LyricsPreferencesStore(settingsPath);
   this.media = media; this.local = local;
   online = media is TestMediaSource ? null : new LrclibLyricsProvider();
   nativeLyrics = media is TestMediaSource ? null : new AppleMusicNativeLyricsService();
   lyrics = new LyricSession(online == null ? new LyricsProviders(local) : new LyricsProviders(local, online));
   displaySettings.Normalize();
   Title = "Apple Music 桌面歌词";
   Width = LyricsLayoutMetrics.DefaultWidth;
   SizeToContent = SizeToContent.Height;
   MinWidth = LyricsLayoutMetrics.MinimumWidth; MaxWidth = LyricsLayoutMetrics.MaximumWidth;
   MinHeight = LyricsLayoutMetrics.MinimumHeight; MaxHeight = LyricsLayoutMetrics.MaximumHeight;
   Topmost = true; AllowsTransparency = true; Background = Brushes.Transparent; Foreground = Ink;
   WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
   WindowStartupLocation = WindowStartupLocation.CenterScreen;
   FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI, Microsoft YaHei");
   FontSize = 12; UseLayoutRounding = true; SnapsToDevicePixels = true;
   AutomationProperties.SetName(this, "Apple Music 桌面歌词");

   overlayChrome = new LyricsOverlayChrome();
   settingsPanel = new LyricsSettingsPanel(RetryLyrics, () => { var ignored = ImportAsync(); }, (delta) => ChangeTiming(delta, false), () => ChangeTiming(0, true), () => { var ignored = SwitchLyricsVersionAsync(); });
   hitTestBridge = new LyricsHitTestBridge(this);
   overlayChrome.SettingsRequested += (s,e) => { if (settingsPanel.Visibility == Visibility.Visible) CloseSettings(); else OpenSettings(); };
   overlayChrome.ToggleLockRequested += (s,e) => SetLocked(!overlayChrome.IsLocked);
   overlayChrome.CloseRequested += (s,e) => SetEnabled(false);
   settingsPanel.DismissRequested += (s,e) => CloseSettings();
   settingsPanel.AppearanceChanged += (s,e) => {
    CopyDisplaySettings(settingsPanel.ReadAppearance(), displaySettings);
    ApplyDisplaySettings();
    Refresh();
    SavePreferences();
   };

   current.ForegroundBrush = Ink; current.LyricsFontWeight = FontWeights.SemiBold;
   next.ForegroundBrush = Muted;
   third.ForegroundBrush = Muted;
   third.Visibility = Visibility.Collapsed;
   current.Text = "等待 Apple Music";
   next.Text = "播放歌曲后，在这里显示歌词";
   third.Text = "";
   AutomationProperties.SetName(current, "当前歌词或状态");
   AutomationProperties.SetName(next, "下一句歌词或说明");
   AutomationProperties.SetName(third, "第三轨翻译歌词");

   contentRoot = new Grid { Background = Brushes.Transparent };
   var tracks = new Grid { Margin = new Thickness(LyricsLayoutMetrics.HorizontalPadding, 0, LyricsLayoutMetrics.HorizontalPadding, 4), VerticalAlignment = VerticalAlignment.Top };
   tracks.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
   tracks.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
   tracks.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
   Grid.SetRow(current, 0); Grid.SetRow(next, 1); Grid.SetRow(third, 2);
   next.Margin = new Thickness(0, LyricsLayoutMetrics.TrackGap, 0, 0);
   third.Margin = new Thickness(0, LyricsLayoutMetrics.CompactTrackGap, 0, 0);
   tracks.Children.Add(current); tracks.Children.Add(next); tracks.Children.Add(third);
   tracks.IsHitTestVisible = false;
   Grid.SetZIndex(overlayChrome, 10); contentRoot.Children.Add(overlayChrome);
   Grid.SetZIndex(tracks, 20); contentRoot.Children.Add(tracks);
   contentRoot.MouseLeftButtonDown += (s,e) => {
    if (overlayChrome.IsLocked || settingsPanel.Visibility == Visibility.Visible || resizing) return;
    dragging = true;
    overlayChrome.SetHovered(false);
    try { DragMove(); KeepWindowVisible(); SavePreferences(); } catch (InvalidOperationException) { }
    finally { dragging = false; }
    e.Handled = true;
   };

   leftResizeHandle = MakeResizeHandle(ResizeEdge.Left);
   rightResizeHandle = MakeResizeHandle(ResizeEdge.Right);
   topResizeHandle = MakeResizeHandle(ResizeEdge.Top);
   bottomResizeHandle = MakeResizeHandle(ResizeEdge.Bottom);
   contentRoot.Children.Add(leftResizeHandle);
   contentRoot.Children.Add(rightResizeHandle);
   contentRoot.Children.Add(topResizeHandle);
   contentRoot.Children.Add(bottomResizeHandle);
   Grid.SetZIndex(leftResizeHandle, 30); Grid.SetZIndex(rightResizeHandle, 30);
   Grid.SetZIndex(topResizeHandle, 30); Grid.SetZIndex(bottomResizeHandle, 30);
   settingsPanel.Visibility = Visibility.Collapsed;
   settingsPanel.Margin = new Thickness(12);
   settingsPanel.AnchorTarget = contentRoot;
   settingsPanel.ToolbarSurface = overlayChrome.HoverToolbarSurface;
   var settingsLayer = new Canvas();
   Canvas.SetRight(settingsPanel, 0); Canvas.SetTop(settingsPanel, 0);
   settingsLayer.Children.Add(settingsPanel);
   Grid.SetZIndex(settingsLayer, 50); contentRoot.Children.Add(settingsLayer);
   Content = contentRoot;

   Bind(Key.O, ModifierKeys.Control, () => { var ignored = ImportAsync(); });
   Bind(Key.Q, ModifierKeys.Control, ShutdownPreservingEnabled); Bind(Key.Escape, ModifierKeys.None, CloseSettingsOrHide);
   Bind(Key.H, ModifierKeys.Control, () => SetEnabled(false)); Bind(Key.OemPlus, ModifierKeys.Control, () => ResizeLyrics(2));
   Bind(Key.Add, ModifierKeys.Control, () => ResizeLyrics(2)); Bind(Key.OemMinus, ModifierKeys.Control, () => ResizeLyrics(-2));
   Bind(Key.Subtract, ModifierKeys.Control, () => ResizeLyrics(-2));
   if (!smoke) {
    tray = new Forms.NotifyIcon { Text = "Apple Music 桌面歌词 · 双击显示", Icon = System.Drawing.SystemIcons.Application, Visible = true };
    var menu = new Forms.ContextMenuStrip();
    menu.Items.Add("显示歌词", null, (s,e) => Dispatcher.BeginInvoke(new Action(() => SetEnabled(true))));
    trayLockItem = new Forms.ToolStripMenuItem("锁定歌词（Ctrl+Alt+Shift+L）", null, (s,e) => Dispatcher.BeginInvoke(new Action(() => SetLocked(!overlayChrome.IsLocked))));
    menu.Items.Add(trayLockItem);
    menu.Items.Add("退出", null, (s,e) => Dispatcher.BeginInvoke(new Action(ShutdownPreservingEnabled)));
    tray.ContextMenuStrip = menu; tray.DoubleClick += (s,e) => Dispatcher.BeginInvoke(new Action(() => SetEnabled(true)));
   }
   SourceInitialized += (s,e) => {
    handle = new WindowInteropHelper(this).Handle;
    if (!smoke) {
     hotkeyRegistered = RegisterHotKey(handle, 1, 0x0001 | 0x0002 | 0x4000, 0x4C);
     lockHotkeyRegistered = RegisterHotKey(handle, 2, 0x0001 | 0x0002 | 0x0004 | 0x4000, 0x4C);
     UpdateHotkeyNotice();
     HwndSource.FromHwnd(handle).AddHook(WndProc);
    }
   };
   IsKeyboardFocusWithinChanged += (s,e) => {
    if (!(smokeMode && media is TestMediaSource)) UpdateHoverChrome();
   };
   overlayChrome.HoverToolbarSurface.IsKeyboardFocusWithinChanged += (s,e) => {
    if (!(smokeMode && media is TestMediaSource)) UpdateHoverChrome();
   };
   timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
   timer.Tick += (s,e) => {
    if (!(smokeMode && media is TestMediaSource)) UpdateHoverChrome();
    Refresh();
   };
   IsVisibleChanged += (s,e) => { if (!IsVisible) overlayChrome.SetHovered(false); };
   SizeChanged += (s,e) => {
    if (e.HeightChanged) KeepWindowVisible();
    if (settingsPanel.Visibility == Visibility.Visible) { overlayChrome.RepositionToolbar(); settingsPanel.RepositionPopup(); }
   };
   LocationChanged += (s,e) => {
    if (settingsPanel.Visibility == Visibility.Visible) { overlayChrome.RepositionToolbar(); settingsPanel.RepositionPopup(); }
   };
   Loaded += (s,e) => {
    InitializeStartupState();
    ApplyDisplaySettings();
    KeepWindowVisible();
    if (ShouldStartVisible) {
     Refresh();
     if (!timer.IsEnabled) timer.Start();
    }
   };
   Closed += (s,e) => {
    SavePreferences();
    closing = true; timer.Stop(); StopMediaPolling(); hitTestBridge.Dispose(); lyrics.Dispose(); if (nativeLyrics != null) nativeLyrics.Dispose(); if (online != null) online.Dispose(); media.Dispose();
    if (hotkeyRegistered) UnregisterHotKey(handle, 1);
    if (lockHotkeyRegistered) UnregisterHotKey(handle, 2);
    if (tray != null) { tray.Visible = false; tray.ContextMenuStrip.Dispose(); tray.Dispose(); }
    try {
     if (Application.Current != null && (Application.Current.MainWindow == null || ReferenceEquals(Application.Current.MainWindow, this))) {
      Application.Current.Shutdown();
     }
    } catch { }
   };
  }
  Border MakeResizeHandle(ResizeEdge edge) {
   bool horizontal = edge == ResizeEdge.Left || edge == ResizeEdge.Right;
   var handle = new Border {
    Background = Brushes.Transparent,
    HorizontalAlignment = horizontal ? (edge == ResizeEdge.Left ? HorizontalAlignment.Left : HorizontalAlignment.Right) : HorizontalAlignment.Stretch,
    VerticalAlignment = horizontal ? VerticalAlignment.Stretch : (edge == ResizeEdge.Top ? VerticalAlignment.Top : VerticalAlignment.Bottom),
    Cursor = horizontal ? Cursors.SizeWE : Cursors.SizeNS,
    Focusable = false
   };
   if (horizontal) handle.Width = 8;
   else { handle.Height = 8; handle.Margin = new Thickness(8, 0, 8, 0); }
   handle.MouseLeftButtonDown += (s,e) => BeginResize(edge, e);
   handle.MouseMove += (s,e) => ContinueResize(e);
   handle.MouseLeftButtonUp += (s,e) => EndResize(e);
   handle.LostMouseCapture += (s,e) => FinishResize(false);
   return handle;
  }
  void BeginResize(ResizeEdge edge, MouseButtonEventArgs e) {
   if (overlayChrome.IsLocked || settingsPanel.Visibility == Visibility.Visible) return;
   StartResizeSession(edge, PointToScreen(e.GetPosition(this)));
   ((UIElement)e.Source).CaptureMouse(); e.Handled = true;
  }
  internal void StartResizeSession(ResizeEdge edge, Point startScreen) {
   resizing = true; resizingEdge = edge; resizeStartWidth = Width; resizeStartLeft = Left;
   resizeStartHeight = ActualHeight > 0 ? ActualHeight : MinHeight;
   resizeStartBottom = Top + resizeStartHeight;
   resizeStartFontSize = lyricSize;
   resizeStartScreen = startScreen;
   overlayChrome.SetResizing(true);
  }
  internal void ApplyResizeDelta(double deltaX, double deltaY) {
   if (!resizing) return;
   if (resizingEdge == ResizeEdge.Left || resizingEdge == ResizeEdge.Right) {
    double width = LyricsLayoutMetrics.ClampWidth(resizingEdge == ResizeEdge.Left ? resizeStartWidth - deltaX : resizeStartWidth + deltaX);
    if (resizingEdge == ResizeEdge.Left) Left = resizeStartLeft + (resizeStartWidth - width);
    Width = width;
   } else {
    double updatedFontSize = LyricsLayoutMetrics.ComputeVerticalResizeFontSize(resizeStartFontSize, resizeStartHeight, deltaY, resizingEdge == ResizeEdge.Top);
    if (Math.Abs(updatedFontSize - lyricSize) > 0.01 || Math.Abs(displaySettings.FontSize - updatedFontSize) > 0.01) {
     displaySettings.FontSize = updatedFontSize;
     ApplyDisplaySettings();
     UpdateLayout();
    }
    if (resizingEdge == ResizeEdge.Top) {
     double currentHeight = ActualHeight > 0 ? ActualHeight : resizeStartHeight;
     Top = resizeStartBottom - currentHeight;
    }
   }
   KeepWindowVisible();
  }
  void ContinueResize(MouseEventArgs e) {
   if (!resizing) return;
   Point currentScreen = PointToScreen(e.GetPosition(this));
   DpiScale dpi = VisualTreeHelper.GetDpi(this);
   double scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1;
   double scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1;
   ApplyResizeDelta((currentScreen.X - resizeStartScreen.X) / scaleX, (currentScreen.Y - resizeStartScreen.Y) / scaleY);
   e.Handled = true;
  }
  void EndResize(MouseButtonEventArgs e) {
   if (!resizing) return;
   FinishResize(true);
   e.Handled = true;
  }
  internal void FinishResize(bool releaseCapture) {
   if (!resizing) return;
   resizing = false;
   overlayChrome.SetResizing(false);
   if (releaseCapture) Mouse.Capture(null);
   KeepWindowVisible(); SavePreferences(); UpdateHoverChrome();
  }
  internal bool ShouldStartVisible { get { return smokeMode || userEnabled; } }
  internal void InitializeStartupState() {
   if (startupInitialized) return;
   startupInitialized = true;
   if (!(smokeMode && media is TestMediaSource)) {
    LoadStartupPreferences(preferencesStore, !autoLaunchMode, false);
   } else {
    userEnabled = true;
   }
   ApplyDisplaySettings();
   KeepWindowVisible();
   if (ShouldStartVisible) {
    StartMediaPolling(false);
    Refresh();
    timer.Start();
   } else {
    timer.Stop();
    StopMediaPolling();
   }
  }
  void StartMediaPolling(bool resetSample) {
   if (nativeLyrics != null) nativeLyrics.SetActive(true);
   var lifecycle = media as IMediaSourceLifecycle;
   if (lifecycle == null) return;
   if (resetSample) lifecycle.ResetSample();
   lifecycle.StartPolling();
  }
  void StopMediaPolling() {
   if (nativeLyrics != null) nativeLyrics.SetActive(false);
   var lifecycle = media as IMediaSourceLifecycle;
   if (lifecycle != null) lifecycle.StopPolling();
  }
  internal void SetEnabled(bool enabled) {
   userEnabled = enabled;
   if (enabled) {
    selectedKey = "";
    selectedDuration = 0;
    lyrics.Clear();
    StartMediaPolling(true);
    RestoreWindow();
    timer.Start();
   } else {
    HideWindow();
    timer.Stop();
    selectedKey = "";
    selectedDuration = 0;
    lyrics.Clear();
    StopMediaPolling();
   }
   SavePreferences();
  }
  internal void SetLockedFromHost(bool value) { SetLocked(value); }
  internal void OpenSettingsFromHost() {
   if (!userEnabled) SetEnabled(true);
   if (overlayChrome.IsLocked) SetLocked(false);
   OpenSettings();
  }
  internal HostStateSnapshot ReadHostState() {
   var lifecycle = media as IMediaSourceLifecycle;
   var sample = media.Read();
   return new HostStateSnapshot {
    Enabled = userEnabled,
    Locked = overlayChrome.IsLocked,
    Visible = IsVisible,
    Polling = lifecycle != null ? lifecycle.IsPolling : timer.IsEnabled,
    ClientAvailable = sample != null && sample.Status == "Connected"
   };
  }
  internal void ShutdownForClientExit() { ShutdownPreservingEnabled(); }
  internal void ShutdownPreservingEnabled() {
   if (closing) return;
   timer.Stop();
   selectedKey = "";
   selectedDuration = 0;
   lyrics.Clear();
   StopMediaPolling();
   SavePreferences();
   Close();
  }
  void OpenSettings() {
   if (overlayChrome.IsLocked) return;
   FinishResize(true);
   overlayChrome.SetSettingsOpen(true);
   settingsPanel.Visibility = Visibility.Visible;
   hitTestBridge.SetClickThrough(false);
   UpdateLayout();
   overlayChrome.RepositionToolbar();
   settingsPanel.RepositionPopup();
   Dispatcher.BeginInvoke(new Action(() => {
    if (settingsPanel.Visibility == Visibility.Visible) {
     overlayChrome.RepositionToolbar();
     settingsPanel.RepositionPopup();
    }
   }), System.Windows.Threading.DispatcherPriority.Loaded);
   settingsPanel.FocusFirstControl();
  }
  void CloseSettings() {
   settingsPanel.Visibility = Visibility.Collapsed;
   overlayChrome.SetSettingsOpen(false);
   hitTestBridge.SetClickThrough(overlayChrome.IsLocked);
   if (!(smokeMode && media is TestMediaSource)) UpdateHoverChrome();
  }
  void CloseSettingsOrHide() { if (settingsPanel.Visibility == Visibility.Visible) CloseSettings(); else SetEnabled(false); }
  void SetLocked(bool value) {
   if (value) { FinishResize(true); CloseSettings(); }
   overlayChrome.SetLocked(value);
   hitTestBridge.SetClickThrough(value);
   leftResizeHandle.IsHitTestVisible = !value; rightResizeHandle.IsHitTestVisible = !value;
   topResizeHandle.IsHitTestVisible = !value; bottomResizeHandle.IsHitTestVisible = !value;
   UpdateTrayLockMenuItem();
   SavePreferences();
  }
  void UpdateTrayLockMenuItem() {
   if (trayLockItem != null) trayLockItem.Text = overlayChrome.IsLocked ? "解锁歌词（Ctrl+Alt+Shift+L）" : "锁定歌词（Ctrl+Alt+Shift+L）";
  }
  internal void UpdateHotkeyNotice() {
   if (!hotkeyRegistered && !lockHotkeyRegistered) hotkeyNotice = "快捷键 Ctrl+Alt+L 和 Ctrl+Alt+Shift+L 已被占用，请通过系统托盘菜单恢复或解锁";
   else if (!hotkeyRegistered) hotkeyNotice = "快捷键 Ctrl+Alt+L 已被占用，请通过系统托盘菜单或再次启动恢复";
   else if (!lockHotkeyRegistered) hotkeyNotice = "快捷键 Ctrl+Alt+Shift+L 已被占用，锁定时请通过系统托盘菜单解锁";
   else hotkeyNotice = string.Empty;
  }
  internal bool ShouldShowChrome(bool pointerInside, bool keyboardFocused) {
   if (dragging || resizing) return false;
   if (settingsPanel.Visibility == Visibility.Visible) return !overlayChrome.IsLocked;
   if (overlayChrome.IsLocked) return pointerInside;
   return pointerInside || keyboardFocused;
  }
  bool HasOverlayKeyboardFocus() {
   return IsKeyboardFocusWithin || overlayChrome.HoverToolbarSurface.IsKeyboardFocusWithin;
  }
  void UpdateHoverChrome() {
   if (LyricsOverlayHover.IsWindowCloaked(this)) {
    overlayChrome.SetHovered(false);
    return;
   }
   bool pointerInside = LyricsOverlayHover.IsPointerInside(this, overlayChrome.HoverToolbarSurface);
   overlayChrome.SetHovered(ShouldShowChrome(pointerInside, HasOverlayKeyboardFocus()));
  }
  internal void ApplyTrackLayoutMode(bool threeTrack) {
   threeTrackActive = threeTrack;
   third.Visibility = threeTrack ? Visibility.Visible : Visibility.Collapsed;
   MaxHeight = threeTrack ? LyricsLayoutMetrics.MaximumThreeTrackHeight : LyricsLayoutMetrics.MaximumHeight;
   double secondarySize = threeTrack ? LyricsLayoutMetrics.SecondaryThreeTrackFontSize(lyricSize) : LyricsLayoutMetrics.NextLineFontSize(lyricSize);
   double gap = threeTrack ? LyricsLayoutMetrics.CompactTrackGap : LyricsLayoutMetrics.TrackGap;
   next.RequestedFontSize = secondarySize;
   third.RequestedFontSize = secondarySize;
   next.Margin = new Thickness(0, gap, 0, 0);
   third.Margin = new Thickness(0, gap, 0, 0);
  }
  void ApplyDisplaySettings() {
   displaySettings.Normalize(); lyricSize = LyricsLayoutMetrics.ClampFontSize(displaySettings.FontSize);
   current.RequestedFontSize = lyricSize;
   current.AnimateLongLines = displaySettings.AnimateLongLines;
   next.AnimateLongLines = displaySettings.AnimateLongLines;
   third.AnimateLongLines = displaySettings.AnimateLongLines;
   ApplyTrackLayoutMode(threeTrackActive);
   Brush foreground;
   try { foreground = Brush(displaySettings.ForegroundHex); } catch (Exception) { foreground = Ink; }
   Brush stroke;
   try { stroke = Brush(displaySettings.StrokeHex); } catch (Exception) { stroke = Brush("#0F0F12"); }
   FontFamily family;
   try { family = new FontFamily(displaySettings.FontFamilyName); } catch (Exception) { family = new FontFamily("Segoe UI"); }
   FontWeight weight = FontWeights.Normal;
   if (displaySettings.FontWeightName == "半粗") weight = FontWeights.SemiBold;
   else if (displaySettings.FontWeightName == "粗体") weight = FontWeights.Bold;
   var secondaryBrush = new SolidColorBrush(Color.FromArgb(190, foreground is SolidColorBrush ? ((SolidColorBrush)foreground).Color.R : (byte)176, foreground is SolidColorBrush ? ((SolidColorBrush)foreground).Color.G : (byte)176, foreground is SolidColorBrush ? ((SolidColorBrush)foreground).Color.B : (byte)184));
   current.ForegroundBrush = foreground; next.ForegroundBrush = secondaryBrush; third.ForegroundBrush = secondaryBrush;
   current.StrokeBrush = stroke; next.StrokeBrush = stroke; third.StrokeBrush = stroke;
   current.StrokeEnabled = displaySettings.StrokeEnabled; next.StrokeEnabled = displaySettings.StrokeEnabled; third.StrokeEnabled = displaySettings.StrokeEnabled;
   current.ShadowEnabled = displaySettings.ShadowEnabled; next.ShadowEnabled = displaySettings.ShadowEnabled; third.ShadowEnabled = displaySettings.ShadowEnabled;
   current.LyricsFontFamily = family; next.LyricsFontFamily = family; third.LyricsFontFamily = family;
   current.LyricsFontWeight = weight; next.LyricsFontWeight = weight; third.LyricsFontWeight = weight;
   overlayChrome.SetBackdropOpacity(displaySettings.BackgroundOpacity);
  }
  void KeepWindowVisible() {
   if (double.IsNaN(Left) || double.IsNaN(Top) || double.IsNaN(Width) || ActualHeight <= 0) return;
   Rect workArea = SystemParameters.WorkArea;
   Rect visible = LyricsWindowGeometry.KeepVisible(new Rect(Left, Top, Width, ActualHeight), workArea, 0);
   Left = visible.Left; Top = visible.Top;
   current.AlignInkToTop = visible.Top <= workArea.Top + 0.5;
  }
  internal void LoadStartupPreferences(LyricsPreferencesStore store, bool forceEnable = true, bool applyVisibility = true) {
   var result = store.Load();
   configurationNotice = result.Status == LyricsPreferencesLoadStatus.Loaded || result.Status == LyricsPreferencesLoadStatus.DefaultsUsed ? string.Empty : "设置未加载：" + result.Error;
   var preferences = result.Preferences;
   if (forceEnable) preferences.Enabled = true;
   userEnabled = preferences.Enabled;
   applyingPreferences = true;
   try { ApplyPreferencesFromPersistence(preferences, applyVisibility); }
   finally { applyingPreferences = false; }
  }
  void SavePreferences() {
   if (smokeMode || applyingPreferences) return;
   var result = preferencesStore.Save(ReadPreferencesForPersistence());
   configurationNotice = result.Succeeded ? string.Empty : "设置未保存：" + result.Error;
  }
  string CurrentScreenDeviceName() {
   try {
    IntPtr hwnd = new WindowInteropHelper(this).Handle;
    return hwnd == IntPtr.Zero ? Forms.Screen.PrimaryScreen.DeviceName : Forms.Screen.FromHandle(hwnd).DeviceName;
   } catch { return string.Empty; }
  }
  internal LyricsWindowPreferences ReadPreferencesForPersistence() {
   return new LyricsWindowPreferences {
    Enabled = userEnabled, Locked = overlayChrome.IsLocked, Width = Width, Left = Left, Top = Top,
    ScreenDeviceName = CurrentScreenDeviceName(), Display = displaySettings.Clone(), TimingOffsets = timing.ReadOffsets(),
    PreferredLrclibIds = online == null ? new System.Collections.Generic.Dictionary<string,int>() : online.ReadPreferredIds()
   };
  }
  internal void ApplyPreferencesFromPersistence(LyricsWindowPreferences preferences, bool applyVisibility = true) {
   if (preferences == null) return;
   var copy = preferences.Clone(); copy.Normalize();
   userEnabled = copy.Enabled;
   timing.LoadOffsets(copy.TimingOffsets);
   if (online != null) online.LoadPreferredIds(copy.PreferredLrclibIds);
   Width = copy.Width;
   bool hasLeft = !double.IsNaN(copy.Left);
   bool hasTop = !double.IsNaN(copy.Top);
   if (hasLeft || hasTop) {
    WindowStartupLocation = WindowStartupLocation.Manual;
    if (hasLeft) Left = copy.Left;
    if (hasTop) Top = copy.Top;
   } else {
    WindowStartupLocation = WindowStartupLocation.CenterScreen;
   }
   CopyDisplaySettings(copy.Display, displaySettings);
   ApplyDisplaySettings(); SetLocked(copy.Locked); KeepWindowVisible();
   if (applyVisibility) {
    if (copy.Enabled) RestoreWindow(); else HideWindow();
   }
  }
  static void CopyDisplaySettings(LyricsDisplaySettings source, LyricsDisplaySettings target) {
   target.FontSize = source.FontSize;
   target.ForegroundHex = source.ForegroundHex;
   target.FontFamilyName = source.FontFamilyName;
   target.FontWeightName = source.FontWeightName;
   target.StrokeHex = source.StrokeHex;
   target.BackgroundOpacity = source.BackgroundOpacity;
   target.StrokeEnabled = source.StrokeEnabled;
   target.ShadowEnabled = source.ShadowEnabled;
   target.AnimateLongLines = source.AnimateLongLines;
   target.ShowTranslation = source.ShowTranslation;
   target.ShowRomanization = source.ShowRomanization;
   target.CustomColors = LyricsDisplaySettings.NormalizeCustomColors(source.CustomColors);
  }
  void Bind(Key key, ModifierKeys mods, Action action) {
   var command = new RoutedCommand(); CommandBindings.Add(new CommandBinding(command, (s,e) => action()));
   InputBindings.Add(new KeyBinding(command, new KeyGesture(key, mods)));
  }
  IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wparam, IntPtr lparam, ref bool handled) {
   if (msg == 0x0312 && wparam.ToInt32() == 1) { SetEnabled(true); handled = true; }
   else if (msg == 0x0312 && wparam.ToInt32() == 2) { SetLocked(!overlayChrome.IsLocked); handled = true; }
   return IntPtr.Zero;
  }
  public void RestoreWindow() { Refresh(); Show(); WindowState = WindowState.Normal; KeepWindowVisible(); if (ShowActivated) Activate(); }
  void HideWindow() { FinishResize(true); CloseSettings(); Hide(); }
  void ResizeLyrics(double delta) { lyricSize = LyricsLayoutMetrics.ClampFontSize(lyricSize + delta); displaySettings.FontSize = lyricSize; ApplyDisplaySettings(); SavePreferences(); }
 }
}
