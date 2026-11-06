using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using System.Runtime.InteropServices;

namespace AMLyrics {
 public sealed partial class LyricsWindow : Window {
  // Neutral OKLCH roles mapped to sRGB: surface L~0.19, ink L~0.96, muted L~0.75.
  internal static readonly Brush Surface = Brush("#141416");
  internal static readonly Brush Ink = Brush("#F4F4F5");
  internal static readonly Brush Muted = Brush("#B0B0B8");
  internal static readonly Brush Accent = Brush("#F59BAC");
  readonly TextBlock title = new TextBlock();
  readonly TextBlock current = new TextBlock();
  readonly TextBlock next = new TextBlock();
  readonly TextBlock status = new TextBlock();
  readonly TextBlock source = new TextBlock();
  readonly Button import;
  readonly IMediaSource media;
  readonly LocalLyricsProvider local;
  readonly LyricSession lyrics;
  readonly LrclibLyricsProvider online;
  readonly Button retry;
  readonly Button timingButton;
  readonly LyricTiming timing = new LyricTiming();
  int attempts;
  double selectedDuration;
  DateTime retryAt = DateTime.MaxValue;
  readonly DispatcherTimer timer;
  readonly Forms.NotifyIcon tray;
  string selectedKey = "";
  string notice = "";
  string noticeKey = "";
  bool importing, closing;
  double lyricSize = 26;
  int displayedLineIndex = -1;
  readonly TranslateTransform lyricShift = new TranslateTransform();
  readonly ScrollViewer lyricScroll;
  IntPtr handle;
  bool hotkeyRegistered;
  Task pendingSelection = Task.FromResult(0);
  [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
  [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
  static Brush Brush(string hex) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex); b.Freeze(); return b; }
  public LyricsWindow(IMediaSource media, LocalLyricsProvider local, bool smoke) {
   this.media = media; this.local = local;
   online = media is TestMediaSource ? null : new LrclibLyricsProvider();
   lyrics = new LyricSession(online == null ? new LyricsProviders(local) : new LyricsProviders(local, online));
   Title = "Apple Music 桌面歌词"; Width = 620; Height = 280; MinWidth = 440; MinHeight = 150;
   Topmost = true; Background = Surface; Foreground = Ink;
   WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResizeWithGrip;
   WindowStartupLocation = WindowStartupLocation.CenterScreen;
   FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI, Microsoft YaHei");
   FontSize = 12; UseLayoutRounding = true; SnapsToDevicePixels = true;
   AutomationProperties.SetName(this, "Apple Music 桌面歌词");
   Resources.Add(typeof(Button), XamlReader.Parse(@"<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'>
    <Setter Property='Foreground' Value='#D4D4DA'/><Setter Property='Background' Value='Transparent'/>
    <Setter Property='Padding' Value='9,4'/><Setter Property='MinHeight' Value='28'/><Setter Property='MinWidth' Value='30'/>
    <Setter Property='Cursor' Value='Hand'/><Setter Property='BorderThickness' Value='0'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'>
     <Border x:Name='B' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Background='{TemplateBinding Background}' CornerRadius='4' BorderThickness='1' BorderBrush='Transparent' Padding='{TemplateBinding Padding}'>
      <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>
     <ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='B' Property='Background' Value='#303034'/></Trigger>
      <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='B' Property='BorderBrush' Value='#F59BAC'/></Trigger>
      <Trigger Property='IsPressed' Value='True'><Setter TargetName='B' Property='Background' Value='#424248'/></Trigger>
      <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.45'/></Trigger></ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter></Style>"));
   var frame = new Border { BorderBrush = Brush("#3C3C42"), BorderThickness = new Thickness(1), Padding = new Thickness(18, 12, 18, 10) };
   var grid = new Grid();
   grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
   grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
   grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
   frame.Child = grid; Content = frame;
   var header = new Grid();
   header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
   header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
   title.Text = "Apple Music 桌面歌词"; title.Foreground = Muted; title.FontSize = 12;
   title.TextTrimming = TextTrimming.CharacterEllipsis; title.VerticalAlignment = VerticalAlignment.Center;
   title.ToolTip = "拖动此处移动窗口"; title.Cursor = Cursors.SizeAll;
   title.MouseLeftButtonDown += (s,e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
   header.Children.Add(title);
   var controls = new StackPanel { Orientation = Orientation.Horizontal };
   retry = MakeButton("重试", "重新自动获取当前歌曲歌词", RetryLyrics);
   controls.Children.Add(retry);
   import = MakeButton("本地备用", "导入当前歌曲的 LRC 或文本歌词 · Ctrl+O", () => { var ignored = ImportAsync(); });
   controls.Children.Add(import);
   controls.Children.Add(MakeButton("A−", "缩小字号 · Ctrl+减号", () => ResizeLyrics(-2)));
   controls.Children.Add(MakeButton("A+", "放大字号 · Ctrl+加号", () => ResizeLyrics(2)));
   controls.Children.Add(MakeButton("隐藏", "隐藏窗口 · Esc；托盘或 Ctrl+Alt+L 恢复", HideWindow));
   controls.Children.Add(MakeButton("退出", "退出桌面歌词 · Ctrl+Q", Close));
   Grid.SetColumn(controls, 1); header.Children.Add(controls); grid.Children.Add(header);
   var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 14, 0, 14), RenderTransform = lyricShift };
   current.Foreground = Ink; current.FontSize = lyricSize; current.FontWeight = FontWeights.SemiBold;
   current.TextWrapping = TextWrapping.Wrap; current.Text = "等待 Apple Music";
   next.Foreground = Muted; next.FontSize = 18; next.TextWrapping = TextWrapping.Wrap;
   next.Margin = new Thickness(0, 8, 0, 0); next.Text = "播放歌曲后，在这里显示歌词";
   AutomationProperties.SetName(current, "当前歌词或状态"); AutomationProperties.SetName(next, "下一句歌词或说明");
   lines.Children.Add(current); lines.Children.Add(next);
   var scroll = new ScrollViewer { Content = lines, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = true };
   lyricScroll = scroll;
   AutomationProperties.SetName(scroll, "歌词区域，可滚动阅读长歌词");
   Grid.SetRow(scroll, 1); grid.Children.Add(scroll);
   var footer = new Grid();
   footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
   footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
   status.Foreground = Accent; status.FontSize = 11; status.Margin = new Thickness(0, 0, 16, 0);
   source.Foreground = Muted; source.FontSize = 11; source.TextAlignment = TextAlignment.Right; source.TextTrimming = TextTrimming.CharacterEllipsis;
   footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
   timingButton = MakeButton("校时", "校时：调整当前歌曲歌词提前或延后", () => { timingButton.ContextMenu.IsOpen = true; });
   var timingMenu = new ContextMenu { Background = Surface, Foreground = Ink };
   var earlier = new MenuItem { Header = "歌词提前 0.2 秒（歌词偏慢时）" };
   earlier.Click += (s,e) => ChangeTiming(0.2, false);
   var later = new MenuItem { Header = "歌词延后 0.2 秒（歌词偏快时）" };
   later.Click += (s,e) => ChangeTiming(-0.2, false);
   var reset = new MenuItem { Header = "恢复当前歌曲默认时间" };
   reset.Click += (s,e) => ChangeTiming(0, true);
   timingMenu.Items.Add(earlier); timingMenu.Items.Add(later); timingMenu.Items.Add(new Separator()); timingMenu.Items.Add(reset);
   timingMenu.PlacementTarget = timingButton; timingButton.ContextMenu = timingMenu;
   footer.Children.Add(status); Grid.SetColumn(source, 1); footer.Children.Add(source);
   Grid.SetColumn(timingButton, 2); footer.Children.Add(timingButton); Grid.SetRow(footer, 2); grid.Children.Add(footer);
   Bind(Key.O, ModifierKeys.Control, () => { var ignored = ImportAsync(); });
   Bind(Key.Q, ModifierKeys.Control, Close); Bind(Key.Escape, ModifierKeys.None, HideWindow);
   Bind(Key.H, ModifierKeys.Control, HideWindow); Bind(Key.OemPlus, ModifierKeys.Control, () => ResizeLyrics(2));
   Bind(Key.Add, ModifierKeys.Control, () => ResizeLyrics(2)); Bind(Key.OemMinus, ModifierKeys.Control, () => ResizeLyrics(-2));
   Bind(Key.Subtract, ModifierKeys.Control, () => ResizeLyrics(-2));
   Bind(Key.Left, ModifierKeys.Control, () => ChangeTiming(0.2, false));
   Bind(Key.Right, ModifierKeys.Control, () => ChangeTiming(-0.2, false));
   Bind(Key.D0, ModifierKeys.Control, () => ChangeTiming(0, true));
   if (!smoke) {
    tray = new Forms.NotifyIcon { Text = "Apple Music 桌面歌词 · 双击显示", Icon = System.Drawing.SystemIcons.Application, Visible = true };
    var menu = new Forms.ContextMenuStrip();
    menu.Items.Add("显示歌词", null, (s,e) => Dispatcher.BeginInvoke(new Action(RestoreWindow)));
    menu.Items.Add("退出", null, (s,e) => Dispatcher.BeginInvoke(new Action(Close)));
    tray.ContextMenuStrip = menu; tray.DoubleClick += (s,e) => Dispatcher.BeginInvoke(new Action(RestoreWindow));
   }
   SourceInitialized += (s,e) => {
    handle = new WindowInteropHelper(this).Handle;
    if (!smoke) { hotkeyRegistered = RegisterHotKey(handle, 1, 0x0001 | 0x0002 | 0x4000, 0x4C); HwndSource.FromHwnd(handle).AddHook(WndProc); }
   };
   timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
   timer.Tick += (s,e) => Refresh();
   Loaded += (s,e) => { Refresh(); timer.Start(); };
   Closed += (s,e) => {
    closing = true; timer.Stop(); lyrics.Dispose(); if (online != null) online.Dispose(); media.Dispose();
    if (hotkeyRegistered) UnregisterHotKey(handle, 1);
    if (tray != null) { tray.Visible = false; tray.ContextMenuStrip.Dispose(); tray.Dispose(); }
   };
  }
  Button MakeButton(string text, string name, Action action) {
   var b = new Button { Content = text, ToolTip = name, Margin = new Thickness(2,0,0,0) };
   AutomationProperties.SetName(b, name); b.Click += (s,e) => action(); return b;
  }
  void Bind(Key key, ModifierKeys mods, Action action) {
   var command = new RoutedCommand(); CommandBindings.Add(new CommandBinding(command, (s,e) => action()));
   InputBindings.Add(new KeyBinding(command, new KeyGesture(key, mods)));
  }
  IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wparam, IntPtr lparam, ref bool handled) {
   if (msg == 0x0312 && wparam.ToInt32() == 1) { RestoreWindow(); handled = true; } return IntPtr.Zero;
  }
  public void RestoreWindow() { Show(); WindowState = WindowState.Normal; if (ShowActivated) Activate(); }
  void HideWindow() { Hide(); }
  void ResizeLyrics(double delta) { lyricSize = Math.Max(16, Math.Min(48, lyricSize + delta)); current.FontSize = lyricSize; next.FontSize = Math.Max(14, lyricSize * 0.7); }
 }
}
