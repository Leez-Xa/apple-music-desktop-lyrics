using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AMLyrics {
 public sealed class LyricsLinePresenter : Grid {
  readonly TranslateTransform shift = new TranslateTransform();
  string text = "";
  FontFamily family = new FontFamily("Segoe UI");
  FontWeight weight = FontWeights.Normal;
  bool animateLongLines = true;
  bool strokeEnabled = true;
  bool motionStrokeEnabled = true;
  bool shadowEnabled = true;
  bool alignInkToTop;
  double requestedFontSize = LyricsLayoutMetrics.DefaultFontSize;
  Brush foregroundBrush = new SolidColorBrush(Color.FromRgb(255, 247, 250));
  Brush strokeBrush = new SolidColorBrush(Color.FromArgb(220, 15, 15, 18));
  Geometry glyphs;
  double textWidth;
  double drawingHeight;
  double drawingOffsetY;

  public LyricsLinePresenter() {
   ClipToBounds = true;
   Focusable = false;
   shift.Changed += (s, e) => InvalidateVisual();
   AutomationProperties.SetName(this, "歌词显示轨道");
  }

  public string Text {
   get { return text; }
   set { string updated = value ?? ""; if (text != updated) { text = updated; RefreshLayout(); } }
  }
  public double RequestedFontSize {
   get { return requestedFontSize; }
   set { double updated = LyricsLayoutMetrics.ClampTrackFontSize(value); if (requestedFontSize != updated) { requestedFontSize = updated; RefreshLayout(); } }
  }
  public bool AnimateLongLines {
   get { return animateLongLines; }
   set { if (animateLongLines != value) { animateLongLines = value; RefreshLayout(); } }
  }
  public Brush ForegroundBrush {
   get { return foregroundBrush; }
   set { foregroundBrush = value ?? Brushes.White; InvalidateVisual(); }
  }
  public Brush StrokeBrush {
   get { return strokeBrush; }
   set { strokeBrush = value ?? Brushes.Transparent; InvalidateVisual(); }
  }
  public bool StrokeEnabled {
   get { return strokeEnabled; }
   set { if (strokeEnabled != value) { strokeEnabled = value; RefreshLayout(); } }
  }
  public bool ShadowEnabled {
   get { return shadowEnabled; }
   set { if (shadowEnabled != value) { shadowEnabled = value; RefreshLayout(); } }
  }
  public FontFamily LyricsFontFamily {
   get { return family; }
   set { FontFamily updated = value ?? new FontFamily("Segoe UI"); if (!family.Equals(updated)) { family = updated; RefreshLayout(); } }
  }
  public FontWeight LyricsFontWeight {
   get { return weight; }
   set { if (weight != value) { weight = value; RefreshLayout(); } }
  }
  public LyricsLinePresentation Presentation { get; private set; }
  internal bool AlignInkToTop {
   get { return alignInkToTop; }
   set { if (alignInkToTop != value) { alignInkToTop = value; InvalidateVisual(); } }
  }

  void RefreshLayout() { InvalidateMeasure(); InvalidateVisual(); }

  protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi) {
   base.OnDpiChanged(oldDpi, newDpi);
   RefreshLayout();
  }

  protected override Size MeasureOverride(Size availableSize) {
   double width = double.IsInfinity(availableSize.Width) ? 1 : Math.Max(1, availableSize.Width);
   var dpi = VisualTreeHelper.GetDpi(this);
   var typeface = new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal);
   Func<string, double, FormattedText> format = (line, size) => LyricsLineLayoutEngine.CreateWpfFormattedText(line, size, typeface, dpi.PixelsPerDip);
   string lineText = text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Replace('\u2028', ' ').Replace('\u2029', ' ');
   var requested = format(lineText.Length == 0 ? " " : lineText, requestedFontSize);
   var measured = requested;
   var previous = Presentation;
   Presentation = LyricsLineLayoutEngine.Layout(lineText, width, requestedFontSize, animateLongLines && SystemParameters.ClientAreaAnimation, (line, size) => {
    measured = size == requestedFontSize ? requested : format(line, size);
    return measured.WidthIncludingTrailingWhitespace;
   });
   if (Presentation.Mode == LyricsLongLineMode.Ellipsis) {
    string trimmed = TrimToWidth(lineText, width, value => format(value, Presentation.FontSize).WidthIncludingTrailingWhitespace);
    measured = format(trimmed, Presentation.FontSize);
   }
   textWidth = measured.WidthIncludingTrailingWhitespace;
   glyphs = measured.BuildGeometry(new Point());
   var vertical = LyricsLineLayoutEngine.MeasureVertical(measured, glyphs, strokeEnabled, shadowEnabled, dpi.DpiScaleY);
   drawingHeight = vertical.Height;
   drawingOffsetY = vertical.DrawingOffsetY;
   if (previous == null || previous.Text != Presentation.Text || previous.Mode != Presentation.Mode || previous.Overflow != Presentation.Overflow || previous.FontSize != Presentation.FontSize || motionStrokeEnabled != strokeEnabled) UpdateMotion();
   double nominalHeight = Math.Ceiling(requested.Height * dpi.DpiScaleY + 2) / dpi.DpiScaleY;
   return new Size(width, Math.Max(drawingHeight, nominalHeight));
  }

  void UpdateMotion() {
   motionStrokeEnabled = strokeEnabled;
   shift.BeginAnimation(TranslateTransform.XProperty, null);
   shift.X = 0;
   if (Presentation.Mode == LyricsLongLineMode.Marquee) {
    var animation = new DoubleAnimation(0, -(Presentation.Overflow + (strokeEnabled ? 2 : 0)), Presentation.MotionDuration) {
     AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
     BeginTime = TimeSpan.FromMilliseconds(450)
    };
    shift.BeginAnimation(TranslateTransform.XProperty, animation, HandoffBehavior.SnapshotAndReplace);
   }
  }

  protected override void OnRender(DrawingContext drawingContext) {
   base.OnRender(drawingContext);
   if (Presentation == null || glyphs == null || glyphs.IsEmpty()) return;
   double x = Presentation.Mode == LyricsLongLineMode.Marquee ? shift.X : Math.Max(0, (ActualWidth - textWidth) / 2);
   double y = (ActualHeight - drawingHeight) / 2 + drawingOffsetY;
   if (alignInkToTop) {
    double glyphTop = glyphs.Bounds.Top;
    double inkTop = glyphTop - (strokeEnabled ? LyricsLineLayoutEngine.StrokeThickness / 2 : 0);
    if (shadowEnabled) inkTop = Math.Min(inkTop, glyphTop + LyricsLineLayoutEngine.ShadowOffsetY);
    y = 1 / VisualTreeHelper.GetDpi(this).DpiScaleY - inkTop;
   }
   drawingContext.PushTransform(new TranslateTransform(x, y));
   if (shadowEnabled) {
    drawingContext.PushTransform(new TranslateTransform(0, LyricsLineLayoutEngine.ShadowOffsetY));
    drawingContext.DrawGeometry(new SolidColorBrush(Color.FromArgb(205, 0, 0, 0)), null, glyphs);
    drawingContext.Pop();
   }
   Pen outline = strokeEnabled ? new Pen(strokeBrush, LyricsLineLayoutEngine.StrokeThickness) { LineJoin = PenLineJoin.Round } : null;
   drawingContext.DrawGeometry(foregroundBrush, outline, glyphs);
   drawingContext.Pop();
  }

  static string TrimToWidth(string text, double width, Func<string, double> measure) {
   if (string.IsNullOrEmpty(text)) return "";
   string ellipsis = "…";
   int end = text.Length;
   while (end > 1 && measure(text.Substring(0, end) + ellipsis) > width) end--;
   return end <= 1 ? ellipsis : text.Substring(0, end) + ellipsis;
  }

 }
 public sealed class LyricsOverlayChrome : Grid {
  readonly Border frame;
  readonly Popup toolbarPopup;
  readonly Grid toolbarSurface;
  readonly StackPanel buttonPanel;
  readonly Button settingsButton;
  readonly Button lockButton;
  readonly Button closeButton;
  readonly Button unlockButton;
  readonly List<Button> interactiveButtons = new List<Button>();
  bool locked;
  bool hovered;
  bool resizing;
  bool settingsOpen;

  public LyricsOverlayChrome() {
   Background = Brushes.Transparent;
   HorizontalAlignment = HorizontalAlignment.Stretch;
   VerticalAlignment = VerticalAlignment.Stretch;
   frame = new Border {
    Background = new SolidColorBrush(Color.FromArgb(158, 10, 11, 15)),
    BorderBrush = new SolidColorBrush(Color.FromArgb(115, 255, 255, 255)),
    BorderThickness = new Thickness(1),
    CornerRadius = new CornerRadius(10),
    Margin = new Thickness(2, 0, 2, 2),
    IsHitTestVisible = false
   };
   Children.Add(frame);

   buttonPanel = new StackPanel {
    Orientation = Orientation.Horizontal,
    HorizontalAlignment = HorizontalAlignment.Center,
    VerticalAlignment = VerticalAlignment.Top
   };
   settingsButton = MakeButton("⚙", "打开歌词设置", delegate { RaiseEvent(SettingsRequested); });
   lockButton = MakeButton("🔒", "锁定歌词位置和尺寸", delegate { RaiseEvent(ToggleLockRequested); });
   lockButton.FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI Symbol, Segoe UI");
   closeButton = MakeButton("×", "关闭歌词显示", delegate { RaiseEvent(CloseRequested); });
    buttonPanel.Children.Add(settingsButton);
    buttonPanel.Children.Add(lockButton);
    buttonPanel.Children.Add(closeButton);
    interactiveButtons.Add(settingsButton);
    interactiveButtons.Add(lockButton);
    interactiveButtons.Add(closeButton);
    unlockButton = MakeButton("🔓", "解锁歌词位置和尺寸", delegate { RaiseEvent(ToggleLockRequested); });
    unlockButton.FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI Symbol, Segoe UI");
    unlockButton.HorizontalAlignment = HorizontalAlignment.Center;
    unlockButton.VerticalAlignment = VerticalAlignment.Top;
    unlockButton.IsEnabled = false;
    toolbarSurface = new Grid { Background = Brushes.Transparent };
    toolbarSurface.Children.Add(buttonPanel);
    toolbarSurface.Children.Add(unlockButton);
    toolbarPopup = new Popup {
     Child = toolbarSurface,
     PlacementTarget = this,
     Placement = PlacementMode.Custom,
     CustomPopupPlacementCallback = PlaceToolbar,
     StaysOpen = true,
     AllowsTransparency = true,
     Focusable = false
    };
    Children.Add(toolbarPopup);
    UpdateVisibility();
    AutomationProperties.SetName(this, "歌词悬停控件");
   }

   public event EventHandler SettingsRequested;
   public event EventHandler ToggleLockRequested;
   public event EventHandler CloseRequested;
   public bool IsLocked { get { return locked; } }
   public StackPanel Toolbar { get { return buttonPanel; } }
   public Border Frame { get { return frame; } }
   public Button SettingsButton { get { return settingsButton; } }
   public Button LockButton { get { return lockButton; } }
   public Button UnlockButton { get { return unlockButton; } }
   internal FrameworkElement HoverToolbarSurface { get { return toolbarSurface; } }

   static CustomPopupPlacement[] PlaceToolbar(Size popupSize, Size targetSize, Point offset) {
    double left = (targetSize.Width - popupSize.Width) / 2;
    return new[] {
     new CustomPopupPlacement(new Point(left, -popupSize.Height), PopupPrimaryAxis.Horizontal),
     new CustomPopupPlacement(new Point(left, targetSize.Height), PopupPrimaryAxis.Horizontal)
    };
   }

   internal void SetHovered(bool value) {
    if (hovered == value) return;
    hovered = value;
    UpdateVisibility();
   }
   internal void SetResizing(bool value) {
    if (resizing == value) return;
    resizing = value;
    UpdateVisibility();
   }
   internal void SetSettingsOpen(bool value) {
    if (settingsOpen == value) return;
    settingsOpen = value;
    UpdateVisibility();
   }
   internal void RepositionToolbar() {
    if (toolbarPopup == null || !toolbarPopup.IsOpen) return;
    double offset = toolbarPopup.HorizontalOffset;
    toolbarPopup.HorizontalOffset = offset + 0.1;
    toolbarPopup.HorizontalOffset = offset;
   }
   internal void SetBackdropOpacity(double opacity) {
    byte alpha = (byte)Math.Round(158 + (255 - 158) * opacity);
    frame.Background = new SolidColorBrush(Color.FromArgb(alpha, 10, 11, 15));
   }
   public void SetLocked(bool value) {
    locked = value;
    if (locked) { resizing = false; settingsOpen = false; }
    UpdateButtonStates();
    UpdateVisibility();
   }
   void UpdateVisibility() {
    bool showUnlockedControls = (hovered || settingsOpen) && !resizing && !locked;
    frame.Visibility = (hovered || resizing || settingsOpen) && !locked ? Visibility.Visible : Visibility.Collapsed;
    buttonPanel.Visibility = showUnlockedControls ? Visibility.Visible : Visibility.Collapsed;
    unlockButton.Visibility = hovered && locked ? Visibility.Visible : Visibility.Collapsed;
    toolbarPopup.IsOpen = showUnlockedControls || (hovered && locked && !resizing);
   }

  Button MakeButton(string glyph, string name, Action action) {
   Button button = new Button {
    Content = glyph, Width = 32, Height = 30, Margin = new Thickness(2), Padding = new Thickness(0),
    FontSize = 14, Focusable = true, ToolTip = name, Style = CreateButtonStyle()
   };
   AutomationProperties.SetName(button, name);
   button.Click += (s, e) => {
    bool unlock = button == unlockButton;
    if ((unlock ? hovered : (hovered || settingsOpen)) && (unlock || !locked) && button.IsEnabled && action != null) action();
   };
   return button;
  }

  Style CreateButtonStyle() {
   Style style = new Style(typeof(Button));
   style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromArgb(188, 18, 19, 24))));
   style.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(255, 247, 250))));
   style.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(150, 255, 255, 255))));
   style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
   style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
   style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
   ControlTemplate template = new ControlTemplate(typeof(Button));
   FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
   border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
   border.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
   border.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = RelativeSource.TemplatedParent });
   border.SetBinding(Border.BorderThicknessProperty, new Binding("BorderThickness") { RelativeSource = RelativeSource.TemplatedParent });
   FrameworkElementFactory content = new FrameworkElementFactory(typeof(ContentPresenter));
   content.SetBinding(ContentPresenter.ContentProperty, new Binding("Content") { RelativeSource = RelativeSource.TemplatedParent });
   content.SetBinding(ContentPresenter.ContentTemplateProperty, new Binding("ContentTemplate") { RelativeSource = RelativeSource.TemplatedParent });
   content.SetBinding(ContentPresenter.ContentTemplateSelectorProperty, new Binding("ContentTemplateSelector") { RelativeSource = RelativeSource.TemplatedParent });
   content.SetBinding(ContentPresenter.ContentStringFormatProperty, new Binding("ContentStringFormat") { RelativeSource = RelativeSource.TemplatedParent });
   content.SetBinding(TextElement.ForegroundProperty, new Binding("Foreground") { RelativeSource = RelativeSource.TemplatedParent });
   content.SetBinding(TextElement.FontFamilyProperty, new Binding("FontFamily") { RelativeSource = RelativeSource.TemplatedParent });
   content.SetBinding(TextElement.FontSizeProperty, new Binding("FontSize") { RelativeSource = RelativeSource.TemplatedParent });
   content.SetBinding(TextElement.FontStretchProperty, new Binding("FontStretch") { RelativeSource = RelativeSource.TemplatedParent });
   content.SetBinding(TextElement.FontStyleProperty, new Binding("FontStyle") { RelativeSource = RelativeSource.TemplatedParent });
   content.SetBinding(TextElement.FontWeightProperty, new Binding("FontWeight") { RelativeSource = RelativeSource.TemplatedParent });
   content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
   content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
   border.AppendChild(content); template.VisualTree = border; style.Setters.Add(new Setter(Control.TemplateProperty, template));
   Trigger hover = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
   hover.SetValue(Control.BackgroundProperty, new SolidColorBrush(Color.FromArgb(228, 56, 40, 50)));
   hover.SetValue(Control.ForegroundProperty, Brushes.White);
   style.Triggers.Add(hover);
   Trigger focus = new Trigger { Property = Button.IsKeyboardFocusedProperty, Value = true };
   focus.SetValue(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(255, 166, 184)));
   focus.SetValue(Control.BackgroundProperty, new SolidColorBrush(Color.FromArgb(238, 72, 43, 56)));
   focus.SetValue(Control.ForegroundProperty, Brushes.White);
   style.Triggers.Add(focus);
   Trigger disabled = new Trigger { Property = Button.IsEnabledProperty, Value = false };
   disabled.SetValue(Control.BackgroundProperty, new SolidColorBrush(Color.FromArgb(120, 30, 30, 35)));
   disabled.SetValue(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(165, 165, 172)));
   disabled.SetValue(Control.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(75, 180, 180, 188)));
   style.Triggers.Add(disabled);
   return style;
  }

  void UpdateButtonStates() {
   for (int i = 0; i < interactiveButtons.Count; i++) interactiveButtons[i].IsEnabled = !locked;
   if (unlockButton != null) unlockButton.IsEnabled = locked;
  }
  void RaiseEvent(EventHandler handler) { if (handler != null) handler(this, EventArgs.Empty); }
 }
}
