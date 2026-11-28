using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using DrawingColor = System.Drawing.Color;
using Forms = System.Windows.Forms;

namespace AMLyrics {
 public sealed class LyricsSettingsPanel : Border {
  sealed class ColorField {
   internal string Label;
   internal TextBox Box;
   internal Border Swatch;
   internal string DefaultHex;
   internal string Hex;
   internal Color Color;
  }

  sealed class ColorDialogOwner : Forms.IWin32Window {
   public IntPtr Handle { get; private set; }
   internal ColorDialogOwner(IntPtr handle) { Handle = handle; }
  }

  readonly Popup popup;
  readonly Border surface;
  readonly TabControl sectionTabs;
  readonly StackPanel displayGroup;
  readonly StackPanel lyricsGroup;
  readonly StackPanel syncGroup;
  readonly Slider fontSizeSlider;
  readonly Slider opacitySlider;
  readonly CheckBox animateCheckBox;
  readonly CheckBox strokeCheckBox;
  readonly CheckBox shadowCheckBox;
  readonly ComboBox fontFamilyCombo;
  readonly ComboBox fontWeightCombo;
  readonly ColorField foregroundColor;
  readonly ColorField strokeColor;
  readonly TextBlock sourceText;
  readonly TextBlock detailText;
  readonly Button retryButton;
  readonly Button switchVersionButton;
  readonly Button importButton;
  readonly TextBlock timingStatusText;
  readonly Button earlierLargeButton;
  readonly Button earlierSmallButton;
  readonly Button laterSmallButton;
  readonly Button laterLargeButton;
  readonly Button resetTimingButton;
  readonly CheckBox translationCheckBox;
  readonly CheckBox romanizationCheckBox;
  readonly TextBlock languageAvailabilityText;
  readonly Action retry;
  readonly Action import;
  readonly Action<double> adjustTiming;
  readonly Action resetTiming;
  readonly Action switchVersion;
  bool applyingAppearance;
  bool colorDialogOpen;

  int[] customColors = new int[0];

  internal Func<Forms.IWin32Window, DrawingColor, DrawingColor?> ColorDialogPicker { get; set; }
  internal Func<Forms.IWin32Window, DrawingColor, int[], Tuple<DrawingColor?, int[]>> CustomColorDialogPicker { get; set; }
  internal int[] CustomColors { get { return LyricsDisplaySettings.NormalizeCustomColors(customColors); } }

  public LyricsSettingsPanel(Action retry, Action import, Action<double> adjustTiming, Action resetTiming, Action switchVersion = null) {
   this.retry = retry; this.import = import; this.adjustTiming = adjustTiming; this.resetTiming = resetTiming; this.switchVersion = switchVersion;
   Background = Brushes.Transparent;
   BorderThickness = new Thickness(0); Padding = new Thickness(0);
   Width = 432; MinWidth = 432; MaxWidth = 432; Height = 472; MinHeight = 472; MaxHeight = 472;
   HorizontalAlignment = HorizontalAlignment.Right; VerticalAlignment = VerticalAlignment.Top;
   Focusable = true;
   AutomationProperties.SetName(this, "歌词设置面板");

   surface = new Border {
    Width = Width, Height = Height, Background = Brushes.White,
    BorderBrush = new SolidColorBrush(Color.FromRgb(190, 193, 202)), BorderThickness = new Thickness(1),
    CornerRadius = new CornerRadius(10), Padding = new Thickness(14), SnapsToDevicePixels = true
   };
   popup = new Popup {
    Child = surface, PlacementTarget = this, Placement = PlacementMode.Relative,
    HorizontalOffset = 0, VerticalOffset = 0, StaysOpen = true, AllowsTransparency = true, Focusable = false
   };
   Grid host = new Grid { Width = Width, Height = Height, IsHitTestVisible = false };
   host.Children.Add(popup);
   Child = host;
   IsVisibleChanged += (s, e) => { if (popup != null) popup.IsOpen = IsVisible; };

   Grid root = new Grid();
   root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
   root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
   surface.Child = root;

   DockPanel header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 10) };
   TextBlock title = new TextBlock {
    Text = "歌词设置", FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(28, 29, 34)),
    VerticalAlignment = VerticalAlignment.Center
   };
   Button close = MakeButton("×", "关闭设置", delegate { RaiseEvent(DismissRequested); });
   close.Width = 32; close.MinHeight = 28; close.Margin = new Thickness(0);
   DockPanel.SetDock(close, Dock.Right); header.Children.Add(close); header.Children.Add(title);
   Grid.SetRow(header, 0); root.Children.Add(header);

   sectionTabs = new TabControl {
    Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(214, 216, 224)),
    BorderThickness = new Thickness(1), ItemContainerStyle = CreateTabItemStyle()
   };
   AutomationProperties.SetName(sectionTabs, "歌词设置分组");
   Grid.SetRow(sectionTabs, 1); root.Children.Add(sectionTabs);

   displayGroup = AddGroup("显示");
   fontSizeSlider = AddSlider(displayGroup, "字号", LyricsLayoutMetrics.MinimumFontSize, LyricsLayoutMetrics.MaximumFontSize, LyricsLayoutMetrics.DefaultFontSize, "歌词字号");
   StackPanel fontSizeButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 5) };
   fontSizeButtons.Children.Add(MakeButton("A−", "减小歌词字号", delegate { fontSizeSlider.Value = Math.Max(fontSizeSlider.Minimum, fontSizeSlider.Value - 1); }));
   fontSizeButtons.Children.Add(MakeButton("A＋", "增大歌词字号", delegate { fontSizeSlider.Value = Math.Min(fontSizeSlider.Maximum, fontSizeSlider.Value + 1); }));
   displayGroup.Children.Add(fontSizeButtons);

   fontFamilyCombo = AddComboBox(displayGroup, "字体样式", new[] { "Segoe UI", "Microsoft YaHei UI", "微软雅黑", "等线" }, "歌词字体样式");
   fontWeightCombo = AddComboBox(displayGroup, "字体粗细", new[] { "常规", "半粗", "粗体" }, "歌词字体粗细");
   foregroundColor = AddColorBox(displayGroup, "字体颜色", "#FFF7FA", "歌词字体颜色（HEX）");
   strokeCheckBox = AddCheckBox(displayGroup, "文字描边", true, "使用文字描边提升可读性");
   strokeColor = AddColorBox(displayGroup, "描边颜色", "#0F0F12", "歌词描边颜色（HEX）");
   shadowCheckBox = AddCheckBox(displayGroup, "文字阴影", true, "使用轻阴影提升可读性");
   opacitySlider = AddSlider(displayGroup, "背景透明度", 0, 1, 0, "歌词背景透明度");
   opacitySlider.SmallChange = 0.05; opacitySlider.LargeChange = 0.1;
   animateCheckBox = AddCheckBox(displayGroup, "长句平滑横向展示", true, "超长歌词允许横向展示");

   lyricsGroup = AddGroup("歌词");
   sourceText = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(31, 32, 38)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
   detailText = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(88, 91, 101)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
   AutomationProperties.SetName(sourceText, "歌词来源"); AutomationProperties.SetName(detailText, "歌词状态详情");
   lyricsGroup.Children.Add(sourceText); lyricsGroup.Children.Add(detailText);
   StackPanel lyricsButtons = new StackPanel { Orientation = Orientation.Horizontal };
   retryButton = MakeButton("重试", "重新获取歌词", delegate { if (retry != null) retry(); });
   switchVersionButton = MakeButton("换一版歌词", "切换当前歌曲的其他 LRCLIB 歌词版本", delegate { if (this.switchVersion != null) this.switchVersion(); });
   importButton = MakeButton("本地备用", "导入本地 LRC 或 TXT 歌词", delegate { if (import != null) import(); });
   lyricsButtons.Children.Add(retryButton); lyricsButtons.Children.Add(switchVersionButton); lyricsButtons.Children.Add(importButton); lyricsGroup.Children.Add(lyricsButtons);
   translationCheckBox = AddCheckBox(lyricsGroup, "显示翻译", false, "显示歌词翻译");
   romanizationCheckBox = AddCheckBox(lyricsGroup, "显示音译", false, "显示歌词音译");
   languageAvailabilityText = new TextBlock {
    Foreground = new SolidColorBrush(Color.FromRgb(88, 91, 101)),
    TextWrapping = TextWrapping.Wrap,
    Margin = new Thickness(0, 4, 0, 6),
    Text = "翻译/音译：随 Apple Music 原生歌词或双语 LRC 自动识别"
   };
   AutomationProperties.SetName(languageAvailabilityText, "翻译与音译状态");
   lyricsGroup.Children.Add(languageAvailabilityText);

   syncGroup = AddGroup("同步");
   timingStatusText = new TextBlock {
    Text = "当前歌曲校时：默认（0.0 秒）",
    Foreground = new SolidColorBrush(Color.FromRgb(88, 91, 101)),
    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
   };
   AutomationProperties.SetName(timingStatusText, "当前歌曲校时状态");
   syncGroup.Children.Add(timingStatusText);
   StackPanel earlierRow = new StackPanel { Orientation = Orientation.Horizontal };
   earlierLargeButton = MakeButton("提前 0.5 秒", "当前歌曲歌词提前 0.5 秒", delegate { if (adjustTiming != null) adjustTiming(0.5); });
   earlierSmallButton = MakeButton("提前 0.1 秒", "当前歌曲歌词提前 0.1 秒", delegate { if (adjustTiming != null) adjustTiming(0.1); });
   earlierRow.Children.Add(earlierSmallButton); earlierRow.Children.Add(earlierLargeButton);
   StackPanel laterRow = new StackPanel { Orientation = Orientation.Horizontal };
   laterSmallButton = MakeButton("延后 0.1 秒", "当前歌曲歌词延后 0.1 秒", delegate { if (adjustTiming != null) adjustTiming(-0.1); });
   laterLargeButton = MakeButton("延后 0.5 秒", "当前歌曲歌词延后 0.5 秒", delegate { if (adjustTiming != null) adjustTiming(-0.5); });
   laterRow.Children.Add(laterSmallButton); laterRow.Children.Add(laterLargeButton);
   StackPanel resetRow = new StackPanel { Orientation = Orientation.Horizontal };
   resetTimingButton = MakeButton("恢复默认", "恢复当前歌曲默认校时", delegate { if (resetTiming != null) resetTiming(); });
   resetRow.Children.Add(resetTimingButton);
   TextBlock syncHint = new TextBlock {
    Text = "仅对当前歌曲生效（范围 ±15 秒，自动保存）",
    Foreground = new SolidColorBrush(Color.FromRgb(88, 91, 101)),
    FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0)
   };
   syncGroup.Children.Add(earlierRow); syncGroup.Children.Add(laterRow); syncGroup.Children.Add(resetRow); syncGroup.Children.Add(syncHint);
   sectionTabs.Items.Add(new TabItem { Header = "显示", Content = displayGroup, ToolTip = "显示设置" });
   sectionTabs.Items.Add(new TabItem { Header = "歌词", Content = lyricsGroup, ToolTip = "歌词来源和状态" });
   sectionTabs.Items.Add(new TabItem { Header = "同步", Content = syncGroup, ToolTip = "歌词同步设置" });
   AutomationProperties.SetName((TabItem)sectionTabs.Items[0], "显示设置页");
   AutomationProperties.SetName((TabItem)sectionTabs.Items[1], "歌词设置页");
   AutomationProperties.SetName((TabItem)sectionTabs.Items[2], "同步设置页");

   PreviewKeyDown += (s, e) => {
    if (e.Key == System.Windows.Input.Key.Escape) {
     RaiseEvent(DismissRequested); e.Handled = true;
    }
   };
  }

  public event EventHandler DismissRequested;
  public event EventHandler AppearanceChanged;
  public bool CanImport { get { return importButton.IsEnabled; } }
  public string TimingStatusText { get { return timingStatusText.Text; } }
  public string DetailText { get { return detailText.Text; } }
  public CheckBox TranslationCheckBox { get { return translationCheckBox; } }
  public CheckBox RomanizationCheckBox { get { return romanizationCheckBox; } }
  public string LanguageStatusText { get { return languageAvailabilityText.Text; } }
  public TextBox ForegroundColorBox { get { return foregroundColor.Box; } }
  public TextBox StrokeColorBox { get { return strokeColor.Box; } }
  internal Border PopupSurface { get { return surface; } }
  internal TabControl SectionTabs { get { return sectionTabs; } }
  internal Button SwitchVersionButton { get { return switchVersionButton; } }
  internal Button EarlierLargeButton { get { return earlierLargeButton; } }
  internal Button EarlierSmallButton { get { return earlierSmallButton; } }
  internal Button LaterSmallButton { get { return laterSmallButton; } }
  internal Button LaterLargeButton { get { return laterLargeButton; } }
  internal Button ResetTimingButton { get { return resetTimingButton; } }

  public LyricsDisplaySettings ReadAppearance() {
   LyricsDisplaySettings result = new LyricsDisplaySettings {
    FontSize = fontSizeSlider.Value,
    FontFamilyName = fontFamilyCombo.SelectedItem as string,
    FontWeightName = fontWeightCombo.SelectedItem as string,
    ForegroundHex = foregroundColor.Hex,
    StrokeHex = strokeColor.Hex,
    BackgroundOpacity = opacitySlider.Value,
    StrokeEnabled = strokeCheckBox.IsChecked == true,
    ShadowEnabled = shadowCheckBox.IsChecked == true,
    AnimateLongLines = animateCheckBox.IsChecked == true,
    ShowTranslation = translationCheckBox.IsChecked == true,
    ShowRomanization = romanizationCheckBox.IsChecked == true,
    CustomColors = LyricsDisplaySettings.NormalizeCustomColors(customColors)
   };
   result.Normalize(); return result;
  }

  public void Apply(LyricsDisplaySettings settings, string source, string detail, string timingSummary, bool canRetry, bool canImport, bool canAdjustTiming, string languageStatus = null, bool canSwitchVersion = false, string switchVersionLabel = null) {
   settings = settings == null ? new LyricsDisplaySettings() : settings.Clone(); settings.Normalize();
   if (!colorDialogOpen && !IsKeyboardFocusWithin && !surface.IsKeyboardFocusWithin) ApplyAppearance(settings);
   sourceText.Text = string.IsNullOrWhiteSpace(source) ? "歌词来源：未选择" : "歌词来源：" + source;
   detailText.Text = string.IsNullOrWhiteSpace(detail) ? "状态：暂无详细信息" : detail;
   timingStatusText.Text = string.IsNullOrWhiteSpace(timingSummary) ? "当前歌曲校时：默认（0.0 秒）" : "当前歌曲校时：" + timingSummary;
   languageAvailabilityText.Text = string.IsNullOrWhiteSpace(languageStatus) ? "翻译/音译：随 Apple Music 原生歌词或双语 LRC 自动识别" : languageStatus;
   retryButton.IsEnabled = canRetry; importButton.IsEnabled = canImport;
   switchVersionButton.IsEnabled = canSwitchVersion;
   switchVersionButton.Content = string.IsNullOrWhiteSpace(switchVersionLabel) ? "换一版歌词" : switchVersionLabel;
   earlierLargeButton.IsEnabled = canAdjustTiming; earlierSmallButton.IsEnabled = canAdjustTiming;
   laterSmallButton.IsEnabled = canAdjustTiming; laterLargeButton.IsEnabled = canAdjustTiming;
   resetTimingButton.IsEnabled = canAdjustTiming;
  }

  public void ApplyAppearance(LyricsDisplaySettings appearance) {
   appearance = appearance == null ? new LyricsDisplaySettings() : appearance.Clone(); appearance.Normalize();
   applyingAppearance = true;
   try {
    fontSizeSlider.Value = appearance.FontSize; opacitySlider.Value = appearance.BackgroundOpacity;
    ApplyColor(foregroundColor, appearance.ForegroundHex); ApplyColor(strokeColor, appearance.StrokeHex);
    customColors = LyricsDisplaySettings.NormalizeCustomColors(appearance.CustomColors);
    strokeCheckBox.IsChecked = appearance.StrokeEnabled; shadowCheckBox.IsChecked = appearance.ShadowEnabled; animateCheckBox.IsChecked = appearance.AnimateLongLines;
    translationCheckBox.IsChecked = appearance.ShowTranslation; romanizationCheckBox.IsChecked = appearance.ShowRomanization;
    if (!fontFamilyCombo.Items.Contains(appearance.FontFamilyName)) {
     var families = new List<string>((IEnumerable<string>)fontFamilyCombo.ItemsSource);
     families.Add(appearance.FontFamilyName);
     fontFamilyCombo.ItemsSource = families;
    }
    SelectItem(fontFamilyCombo, appearance.FontFamilyName); SelectItem(fontWeightCombo, appearance.FontWeightName);
   } finally { applyingAppearance = false; }
  }

  public void FocusFirstControl() { fontSizeSlider.Focus(); }

  StackPanel AddGroup(string title) {
   StackPanel group = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
   TextBlock heading = new TextBlock {
    Text = title, Foreground = new SolidColorBrush(Color.FromRgb(245, 155, 172)), FontWeight = FontWeights.SemiBold,
    Margin = new Thickness(0, 8, 0, 6)
   };
   AutomationProperties.SetName(heading, title + "设置分组");
   group.Children.Add(heading); return group;
  }

  Slider AddSlider(Panel parent, string label, double minimum, double maximum, double value, string accessibleName) {
   Grid row = new Grid { Margin = new Thickness(0, 2, 0, 4) };
   row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
   row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
   row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
   row.Children.Add(new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromRgb(31, 32, 38)), VerticalAlignment = VerticalAlignment.Center });
   Slider slider = new Slider { Minimum = minimum, Maximum = maximum, Value = value, TickFrequency = maximum > 2 ? 1 : 0.1 };
   AutomationProperties.SetName(slider, accessibleName); Grid.SetColumn(slider, 1); row.Children.Add(slider);
   TextBlock valueText = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(88, 91, 101)), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
   valueText.Text = FormatSliderValue(value, maximum); Grid.SetColumn(valueText, 2); row.Children.Add(valueText);
   slider.ValueChanged += (s, e) => { valueText.Text = FormatSliderValue(slider.Value, maximum); RaiseAppearanceChanged(); };
   parent.Children.Add(row); return slider;
  }

  ComboBox AddComboBox(Panel parent, string label, string[] items, string accessibleName) {
   Grid row = new Grid { Margin = new Thickness(0, 2, 0, 4) };
   row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
   row.Children.Add(new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromRgb(31, 32, 38)), VerticalAlignment = VerticalAlignment.Center });
   ComboBox combo = new ComboBox { ItemsSource = items, SelectedIndex = 0, MinHeight = 28, Style = CreateComboBoxStyle(), ItemContainerStyle = CreateComboBoxItemStyle() };
   AutomationProperties.SetName(combo, accessibleName); Grid.SetColumn(combo, 1); row.Children.Add(combo); parent.Children.Add(row);
   combo.SelectionChanged += (s, e) => RaiseAppearanceChanged(); return combo;
  }

  ColorField AddColorBox(Panel parent, string label, string value, string accessibleName) {
   Grid row = new Grid { Margin = new Thickness(0, 2, 0, 4) };
   row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
   row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
   row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
   row.Children.Add(new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromRgb(31, 32, 38)), VerticalAlignment = VerticalAlignment.Center });
   TextBox box = new TextBox { MinHeight = 26, Padding = new Thickness(6, 3, 6, 3), MaxLength = 9, Style = CreateTextBoxStyle() };
   var swatch = new Border {
    Width = 24, Height = 20, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
    BorderBrush = new SolidColorBrush(Color.FromRgb(142, 145, 154)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3)
   };
   AutomationProperties.SetName(box, accessibleName); Grid.SetColumn(box, 1); row.Children.Add(box);
   AutomationProperties.SetName(swatch, "当前" + label); Grid.SetColumn(swatch, 2); row.Children.Add(swatch);
   Button picker = MakeButton("选色", "选择" + label, null);
   picker.MinHeight = 26; picker.Padding = new Thickness(9, 2, 9, 2); picker.Margin = new Thickness(8, 0, 0, 0);
   Grid.SetColumn(picker, 3); row.Children.Add(picker); parent.Children.Add(row);
   var field = new ColorField { Label = label, Box = box, Swatch = swatch, DefaultHex = value };
   ApplyColor(field, value);
   box.TextChanged += (s, e) => CommitColorEdit(field, false);
   box.LostKeyboardFocus += (s, e) => CommitColorEdit(field, true);
   picker.Click += (s, e) => PickColor(field, picker);
   return field;
  }

  void CommitColorEdit(ColorField field, bool finished) {
   if (applyingAppearance || field.Box.Text == field.Hex) return;
   string hex = field.Box.Text.Trim();
   Color color;
   bool hasHexPrefix = hex.StartsWith("#", StringComparison.Ordinal);
   bool rgb = hex.Length == 7 && (finished || field.Hex.Length != 9 || !field.Box.IsKeyboardFocusWithin);
   if (hasHexPrefix && (rgb || hex.Length == 9 || (finished && (hex.Length == 4 || hex.Length == 5))) && TryReadColor(hex, out color)) {
    SetColor(field, hex, color);
    RaiseAppearanceChanged();
   } else if (finished) field.Box.Text = field.Hex;
  }

  void PickColor(ColorField field, Button picker) {
   if (colorDialogOpen) return;
   var source = PresentationSource.FromVisual(picker) as HwndSource;
   if (source == null || source.Handle == IntPtr.Zero) return;
   var owner = new ColorDialogOwner(source.Handle);
   Color current = field.Color;
   var initial = DrawingColor.FromArgb(current.R, current.G, current.B);
   colorDialogOpen = true;
   try {
    DrawingColor? selection = null;
    int[] updatedCustom = customColors;
    var customChoose = CustomColorDialogPicker;
    if (customChoose != null) {
     var resultTuple = customChoose(owner, initial, LyricsDisplaySettings.NormalizeCustomColors(customColors));
     selection = resultTuple == null ? null : resultTuple.Item1;
     updatedCustom = resultTuple == null ? customColors : resultTuple.Item2;
    } else if (ColorDialogPicker != null) {
     selection = ColorDialogPicker(owner, initial);
    } else {
     selection = ShowColorDialog(owner, initial, customColors, "选择" + field.Label, out updatedCustom);
    }
    int[] normalizedUpdated = LyricsDisplaySettings.NormalizeCustomColors(updatedCustom);
    bool customChanged = !LyricsDisplaySettings.CustomColorsEqual(customColors, normalizedUpdated);
    if (customChanged) customColors = normalizedUpdated;
    bool colorChanged = false;
    if (selection.HasValue) {
     var rgb = selection.Value;
     if (rgb.R != current.R || rgb.G != current.G || rgb.B != current.B) {
      Color color = Color.FromArgb(current.A, rgb.R, rgb.G, rgb.B);
      SetColor(field, FormatPickedHex(field.Hex, color), color);
      field.Box.Text = field.Hex;
      colorChanged = true;
     }
    }
    if (colorChanged || customChanged) RaiseAppearanceChanged();
   } finally {
    colorDialogOpen = false;
    if (picker.IsVisible && picker.IsEnabled) picker.Focus();
   }
  }

  static DrawingColor? ShowColorDialog(Forms.IWin32Window owner, DrawingColor initial, int[] existingCustomColors, string title, out int[] updatedCustomColors) {
   Color wpfInitial = Color.FromRgb(initial.R, initial.G, initial.B);
   var dialog = new LyricsColorPickerDialog(wpfInitial, existingCustomColors, title);
   if (owner != null && owner.Handle != IntPtr.Zero) {
    dialog.SetOwnerAndPosition(owner.Handle);
   }
   bool? result = dialog.ShowDialog();
   updatedCustomColors = LyricsDisplaySettings.NormalizeCustomColors(dialog.GetUpdatedCustomColors());
   if (result == true) {
    Color picked = dialog.SelectedColor;
    return DrawingColor.FromArgb(picked.R, picked.G, picked.B);
   }
   return null;
  }

  static void ApplyColor(ColorField field, string value) {
   if (field.Hex == value && field.Box.Text == value) return;
   Color color;
   if (!TryReadColor(value, out color)) {
    value = field.DefaultHex;
    TryReadColor(value, out color);
   }
   SetColor(field, value, color);
   field.Box.Text = value;
  }

  static void SetColor(ColorField field, string hex, Color color) {
   field.Hex = hex;
   field.Color = color;
   field.Swatch.Background = new SolidColorBrush(color);
   field.Swatch.ToolTip = hex;
  }

  static string FormatPickedHex(string original, Color color) {
   original = original.Trim();
   bool isHex = original.StartsWith("#", StringComparison.Ordinal);
   bool hasAlpha = color.A != 255 || (isHex && (original.Length == 5 || original.Length == 9));
   if (isHex && (original.Length == 4 || original.Length == 5) && color.R % 17 == 0 && color.G % 17 == 0 && color.B % 17 == 0) {
    return "#" + (hasAlpha ? (color.A / 17).ToString("X1") : "") + (color.R / 17).ToString("X1") + (color.G / 17).ToString("X1") + (color.B / 17).ToString("X1");
   }
   return "#" + (hasAlpha ? color.A.ToString("X2") : "") + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2");
  }

  CheckBox AddCheckBox(Panel parent, string label, bool value, string accessibleName) {
   CheckBox check = new CheckBox { Content = label, IsChecked = value, Foreground = new SolidColorBrush(Color.FromRgb(31, 32, 38)), Margin = new Thickness(0, 3, 0, 3), Style = CreateCheckBoxStyle() };
   AutomationProperties.SetName(check, accessibleName); check.Checked += (s, e) => RaiseAppearanceChanged(); check.Unchecked += (s, e) => RaiseAppearanceChanged();
   parent.Children.Add(check); return check;
  }

  Button MakeButton(string content, string name, Action action) {
   Button button = new Button { Content = content, ToolTip = name, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(9, 5, 9, 5), MinHeight = 30, Style = CreateButtonStyle() };
   AutomationProperties.SetName(button, name); if (action != null) button.Click += (s, e) => action(); return button;
  }

  Style CreateTabItemStyle() {
   Style style = new Style(typeof(TabItem));
   style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.White));
   style.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(31, 32, 38))));
   style.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(190, 193, 202))));
   style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
   style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 6, 10, 6)));
   Trigger hover = new Trigger { Property = TabItem.IsMouseOverProperty, Value = true };
   hover.SetValue(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(255, 244, 247)));
   hover.SetValue(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(232, 142, 163)));
   style.Triggers.Add(hover);
   Trigger focus = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
   focus.SetValue(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(255, 248, 250)));
   focus.SetValue(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(204, 78, 108)));
   style.Triggers.Add(focus);
   Trigger selected = new Trigger { Property = TabItem.IsSelectedProperty, Value = true };
   selected.SetValue(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(255, 235, 241)));
   selected.SetValue(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(125, 42, 67)));
   selected.SetValue(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(204, 78, 108)));
   style.Triggers.Add(selected);
   Trigger disabled = new Trigger { Property = TabItem.IsEnabledProperty, Value = false };
   disabled.SetValue(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(150, 153, 162)));
   disabled.SetValue(Control.OpacityProperty, 0.58);
   style.Triggers.Add(disabled);
   return style;
  }

  Style CreateComboBoxStyle() {
   Style style = new Style(typeof(ComboBox));
   style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.White));
   style.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(31, 32, 38))));
   style.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(190, 193, 202))));
   style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
   style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 3, 6, 3)));
   Trigger hover = new Trigger { Property = ComboBox.IsMouseOverProperty, Value = true };
   hover.SetValue(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(255, 248, 250)));
   hover.SetValue(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(232, 142, 163)));
   style.Triggers.Add(hover);
   Trigger focus = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
   focus.SetValue(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(255, 248, 250)));
   focus.SetValue(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(204, 78, 108)));
   style.Triggers.Add(focus);
   Trigger open = new Trigger { Property = ComboBox.IsDropDownOpenProperty, Value = true };
   open.SetValue(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(255, 235, 241)));
   open.SetValue(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(204, 78, 108)));
   style.Triggers.Add(open);
   Trigger disabled = new Trigger { Property = ComboBox.IsEnabledProperty, Value = false };
   disabled.SetValue(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(244, 245, 247)));
   disabled.SetValue(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(150, 153, 162)));
   disabled.SetValue(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(210, 212, 218)));
   disabled.SetValue(Control.OpacityProperty, 0.78);
   style.Triggers.Add(disabled);
   return style;
  }

  Style CreateComboBoxItemStyle() {
   Style style = new Style(typeof(ComboBoxItem));
   style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.White));
   style.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(31, 32, 38))));
   style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 5, 8, 5)));
   Trigger hover = new Trigger { Property = ComboBoxItem.IsMouseOverProperty, Value = true };
   hover.SetValue(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(255, 244, 247)));
   hover.SetValue(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(125, 42, 67)));
   style.Triggers.Add(hover);
   Trigger focus = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
   focus.SetValue(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(255, 248, 250)));
   focus.SetValue(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(204, 78, 108)));
   style.Triggers.Add(focus);
   Trigger selected = new Trigger { Property = ComboBoxItem.IsSelectedProperty, Value = true };
   selected.SetValue(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(255, 235, 241)));
   selected.SetValue(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(125, 42, 67)));
   style.Triggers.Add(selected);
   Trigger disabled = new Trigger { Property = ComboBoxItem.IsEnabledProperty, Value = false };
   disabled.SetValue(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(150, 153, 162)));
   disabled.SetValue(Control.OpacityProperty, 0.58);
   style.Triggers.Add(disabled);
   return style;
  }

  Style CreateTextBoxStyle() {
   Style style = new Style(typeof(TextBox));
   style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.White));
   style.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(31, 32, 38))));
   style.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(190, 193, 202))));
   style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
   style.Setters.Add(new Setter(TextBoxBase.SelectionBrushProperty, new SolidColorBrush(Color.FromRgb(238, 151, 174))));
   style.Setters.Add(new Setter(TextBoxBase.SelectionTextBrushProperty, new SolidColorBrush(Color.FromRgb(50, 28, 36))));
   Trigger hover = new Trigger { Property = TextBox.IsMouseOverProperty, Value = true };
   hover.SetValue(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(255, 248, 250)));
   hover.SetValue(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(232, 142, 163)));
   style.Triggers.Add(hover);
   Trigger focus = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
   focus.SetValue(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(255, 248, 250)));
   focus.SetValue(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(204, 78, 108)));
   style.Triggers.Add(focus);
   Trigger selected = new Trigger { Property = TextBoxBase.IsSelectionActiveProperty, Value = true };
   selected.SetValue(TextBoxBase.SelectionBrushProperty, new SolidColorBrush(Color.FromRgb(204, 78, 108)));
   selected.SetValue(TextBoxBase.SelectionTextBrushProperty, Brushes.White);
   style.Triggers.Add(selected);
   Trigger disabled = new Trigger { Property = TextBox.IsEnabledProperty, Value = false };
   disabled.SetValue(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(244, 245, 247)));
   disabled.SetValue(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(150, 153, 162)));
   disabled.SetValue(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(210, 212, 218)));
   disabled.SetValue(Control.OpacityProperty, 0.78);
   style.Triggers.Add(disabled);
   return style;
  }

  Style CreateCheckBoxStyle() {
   Style style = new Style(typeof(CheckBox));
   style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.White));
   style.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(31, 32, 38))));
   style.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(142, 145, 154))));
   style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
   Trigger hover = new Trigger { Property = CheckBox.IsMouseOverProperty, Value = true };
   hover.SetValue(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(255, 244, 247)));
   hover.SetValue(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(232, 142, 163)));
   style.Triggers.Add(hover);
   Trigger focus = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
   focus.SetValue(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(255, 248, 250)));
   focus.SetValue(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(204, 78, 108)));
   style.Triggers.Add(focus);
   Trigger selected = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
   selected.SetValue(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(204, 78, 108)));
   selected.SetValue(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(154, 44, 76)));
   selected.SetValue(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(31, 32, 38)));
   style.Triggers.Add(selected);
   Trigger disabled = new Trigger { Property = CheckBox.IsEnabledProperty, Value = false };
   disabled.SetValue(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(244, 245, 247)));
   disabled.SetValue(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(150, 153, 162)));
   disabled.SetValue(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(210, 212, 218)));
   disabled.SetValue(Control.OpacityProperty, 0.58);
   style.Triggers.Add(disabled);
   return style;
  }

  Style CreateButtonStyle() {
   Style style = new Style(typeof(Button));
   style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(248, 248, 250))));
   style.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(31, 32, 38))));
   style.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(190, 193, 202))));
   style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
   Trigger hover = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
   hover.SetValue(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(255, 244, 247)));
   hover.SetValue(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(232, 142, 163)));
   style.Triggers.Add(hover);
   Trigger focus = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
   focus.SetValue(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(255, 235, 241)));
   focus.SetValue(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(204, 78, 108)));
   style.Triggers.Add(focus);
   Trigger pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
   pressed.SetValue(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(238, 204, 214)));
   pressed.SetValue(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(154, 44, 76)));
   style.Triggers.Add(pressed);
   Trigger disabled = new Trigger { Property = Button.IsEnabledProperty, Value = false };
   disabled.SetValue(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(244, 245, 247)));
   disabled.SetValue(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(150, 153, 162)));
   disabled.SetValue(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(210, 212, 218)));
   disabled.SetValue(Control.OpacityProperty, 0.78);
   style.Triggers.Add(disabled);
   return style;
  }
  void RaiseAppearanceChanged() {
   if (applyingAppearance) return;
   var appearanceHandler = AppearanceChanged; if (appearanceHandler != null) appearanceHandler(this, EventArgs.Empty);
  }
  void RaiseEvent(EventHandler handler) { if (handler != null) handler(this, EventArgs.Empty); }
  static void SelectItem(ComboBox combo, string value) { if (combo.Items.Contains(value)) combo.SelectedItem = value; }
  static bool TryReadColor(string value, out Color color) {
   try {
    object converted = ColorConverter.ConvertFromString(value);
    if (converted is Color) { color = (Color)converted; return true; }
   } catch (Exception) { }
   color = default(Color);
   return false;
  }
  static string FormatSliderValue(double value, double maximum) { return maximum <= 2 ? Math.Round(value * 100).ToString("0") + "%" : Math.Round(value).ToString("0"); }
 }
}
