using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Forms = System.Windows.Forms;

namespace AMLyrics {
 /// <summary>
 /// Manages the 16-slot custom color palette state and HSV/Win32 COLORREF conversions.
 /// </summary>
 public sealed class CustomColorPaletteState {
  public const int SlotCount = 16;
  public const int EmptyColorRef = 0x00FFFFFF;

  readonly int[] slots;

  public int SelectedSlotIndex { get; private set; }

  public CustomColorPaletteState(int[] initialCustomColors) {
   slots = new int[SlotCount];
   int[] normalized = LyricsDisplaySettings.NormalizeCustomColors(initialCustomColors);
   for (int i = 0; i < SlotCount; i++) {
    slots[i] = i < normalized.Length ? (normalized[i] & 0x00FFFFFF) : EmptyColorRef;
   }
   SelectedSlotIndex = FindFirstEmptySlot(slots);
  }

  public static bool IsEmptySlot(int colorRef) {
   return (colorRef & 0x00FFFFFF) == EmptyColorRef;
  }

  public static int ToColorRef(byte r, byte g, byte b) {
   return r | (g << 8) | (b << 16);
  }

  public static Color FromColorRef(int colorRef) {
   return Color.FromRgb(
    (byte)(colorRef & 0xFF),
    (byte)((colorRef >> 8) & 0xFF),
    (byte)((colorRef >> 16) & 0xFF));
  }

  public static int FindFirstEmptySlot(int[] slots) {
   for (int i = 0; i < SlotCount; i++) {
    if (slots == null || i >= slots.Length || IsEmptySlot(slots[i])) {
     return i;
    }
   }
   return 0;
  }

  public static int FindNextEmptySlot(int[] slots, int afterIndex) {
   int baseIndex = ((afterIndex % SlotCount) + SlotCount) % SlotCount;
   for (int offset = 1; offset <= SlotCount; offset++) {
    int index = (baseIndex + offset) % SlotCount;
    if (slots == null || index >= slots.Length || IsEmptySlot(slots[index])) {
     return index;
    }
   }
   return (baseIndex + 1) % SlotCount;
  }

  public static void RgbToHsv(byte r, byte g, byte b, out double h, out double s, out double v) {
   double rf = r / 255.0;
   double gf = g / 255.0;
   double bf = b / 255.0;
   double max = Math.Max(rf, Math.Max(gf, bf));
   double min = Math.Min(rf, Math.Min(gf, bf));
   double delta = max - min;

   v = max;
   s = max <= 0.0 ? 0.0 : delta / max;

   if (delta <= 0.0) {
    h = 0.0;
    return;
   }

   if (max == rf) {
    h = 60.0 * ((gf - bf) / delta);
   } else if (max == gf) {
    h = 60.0 * (((bf - rf) / delta) + 2.0);
   } else {
    h = 60.0 * (((rf - gf) / delta) + 4.0);
   }

   if (h < 0.0) h += 360.0;
   if (h >= 360.0) h %= 360.0;
  }

  public static Color HsvToRgb(double h, double s, double v) {
   if (double.IsNaN(h) || double.IsInfinity(h)) h = 0.0;
   h = ((h % 360.0) + 360.0) % 360.0;
   s = Clamp01(s);
   v = Clamp01(v);

   double c = v * s;
   double hh = h / 60.0;
   double x = c * (1.0 - Math.Abs((hh % 2.0) - 1.0));
   double m = v - c;

   double r1;
   double g1;
   double b1;
   int sector = (int)Math.Floor(hh);
   switch (sector) {
    case 0:
     r1 = c; g1 = x; b1 = 0.0;
     break;
    case 1:
     r1 = x; g1 = c; b1 = 0.0;
     break;
    case 2:
     r1 = 0.0; g1 = c; b1 = x;
     break;
    case 3:
     r1 = 0.0; g1 = x; b1 = c;
     break;
    case 4:
     r1 = x; g1 = 0.0; b1 = c;
     break;
    default:
     r1 = c; g1 = 0.0; b1 = x;
     break;
   }

   byte r = (byte)Math.Round(Clamp01(r1 + m) * 255.0);
   byte g = (byte)Math.Round(Clamp01(g1 + m) * 255.0);
   byte b = (byte)Math.Round(Clamp01(b1 + m) * 255.0);
   return Color.FromRgb(r, g, b);
  }

  public bool IsSlotEmpty(int index) {
   ValidateIndex(index);
   return IsEmptySlot(slots[index]);
  }

  public int GetSlotColorRef(int index) {
   ValidateIndex(index);
   return slots[index];
  }

  public int[] GetSlots() {
   return (int[])slots.Clone();
  }

  public int[] GetNormalizedCustomColors() {
   return LyricsDisplaySettings.NormalizeCustomColors(slots);
  }

  public bool SelectSlot(int index, out Color loadedColor) {
   ValidateIndex(index);
   SelectedSlotIndex = index;
   int colorRef = slots[index];
   if (IsEmptySlot(colorRef)) {
    loadedColor = default(Color);
    return false;
   }
   loadedColor = FromColorRef(colorRef);
   return true;
  }

  public bool SaveColorToSelectedSlot(Color color, out int savedIndex) {
   savedIndex = SelectedSlotIndex;
   int colorRef = ToColorRef(color.R, color.G, color.B);
   if (IsEmptySlot(colorRef)) {
    return false;
   }
   slots[savedIndex] = colorRef;
   SelectedSlotIndex = FindNextEmptySlot(slots, savedIndex);
   return true;
  }

  public bool ClearSelectedSlot() {
   if (IsEmptySlot(slots[SelectedSlotIndex])) {
    return false;
   }
   slots[SelectedSlotIndex] = EmptyColorRef;
   return true;
  }

  static void ValidateIndex(int index) {
   if (index < 0 || index >= SlotCount) {
    throw new ArgumentOutOfRangeException("index");
   }
  }

  static double Clamp01(double value) {
   if (double.IsNaN(value)) return 0.0;
   return Math.Max(0.0, Math.Min(1.0, value));
  }
 }

 /// <summary>
 /// Custom WPF color picker dialog with explicit 16-slot selection and clear visual selection frames.
 /// </summary>
 public sealed class LyricsColorPickerDialog : Window {
  const double SvCanvasWidth = 208.0;
  const double SvCanvasHeight = 172.0;
  const double HueBarWidth = 22.0;
  const double HueBarHeight = 172.0;

  static readonly Color[] BasicPalette = new Color[] {
   // Row 1: Neutrals and lyric ink presets
   Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0xFF, 0xF7, 0xFA), Color.FromRgb(0xF3, 0xF4, 0xF6), Color.FromRgb(0xD1, 0xD5, 0xDB),
   Color.FromRgb(0x9C, 0xA3, 0xAF), Color.FromRgb(0x4B, 0x55, 0x63), Color.FromRgb(0x0F, 0x0F, 0x12), Color.FromRgb(0x00, 0x00, 0x00),
   // Row 2: Rose, red, coral, amber, gold
   Color.FromRgb(0xFF, 0xE4, 0xEC), Color.FromRgb(0xFF, 0x9E, 0xB5), Color.FromRgb(0xFF, 0x4D, 0x6D), Color.FromRgb(0xE1, 0x1D, 0x48),
   Color.FromRgb(0xFF, 0x7A, 0x59), Color.FromRgb(0xFB, 0x92, 0x3C), Color.FromRgb(0xFB, 0xBF, 0x24), Color.FromRgb(0xFD, 0xE0, 0x47),
   // Row 3: Lime, green, teal, cyan, sky
   Color.FromRgb(0xD9, 0xF9, 0x9D), Color.FromRgb(0x86, 0xEF, 0xAC), Color.FromRgb(0x22, 0xC5, 0x5E), Color.FromRgb(0x15, 0x80, 0x3D),
   Color.FromRgb(0x5E, 0xEA, 0xD4), Color.FromRgb(0x06, 0xB6, 0xD4), Color.FromRgb(0x38, 0xBD, 0xF8), Color.FromRgb(0x02, 0x84, 0xC7),
   // Row 4: Blue, indigo, violet, magenta
   Color.FromRgb(0x93, 0xC5, 0xFD), Color.FromRgb(0x3B, 0x82, 0xF6), Color.FromRgb(0x1D, 0x4E, 0xD8), Color.FromRgb(0xA5, 0xB4, 0xFC),
   Color.FromRgb(0x8B, 0x5C, 0xF6), Color.FromRgb(0xC0, 0x84, 0xFC), Color.FromRgb(0xE8, 0x79, 0xF9), Color.FromRgb(0xEC, 0x48, 0x99)
  };

  static readonly Brush TextPrimaryBrush = CreateFrozenBrush(31, 32, 38);
  static readonly Brush TextSecondaryBrush = CreateFrozenBrush(88, 91, 101);
  static readonly Brush NoticeAccentBrush = CreateFrozenBrush(154, 44, 76);
  static readonly Brush SubtleBorderBrush = CreateFrozenBrush(190, 193, 202);
  static readonly Brush SwatchInnerBorderBrush = CreateFrozenBrush(184, 188, 200);
  static readonly Brush EmptySlotBorderBrush = CreateFrozenBrush(207, 211, 220);
  static readonly Brush EmptySlotFillBrush = CreateFrozenBrush(245, 246, 250);
  static readonly Brush EmptyPlusBrush = CreateFrozenBrush(154, 160, 174);

  readonly CustomColorPaletteState paletteState;
  readonly Color initialRgb;
  readonly Border[] basicSwatchFrames;
  readonly Border[] basicSwatchInners;
  readonly Border[] customSlotFrames;
  readonly Border[] customSlotInners;
  readonly TextBlock[] customSlotPlusLabels;
  readonly TextBlock customSlotStatusText;
  readonly Button saveCustomColorButton;
  readonly Button clearCustomColorButton;
  readonly Border svBaseColorLayer;
  readonly Grid svSurface;
  readonly Grid hueSurface;
  readonly Ellipse svOuterCursor;
  readonly Ellipse svInnerCursor;
  readonly Border hueIndicator;
  readonly Border originalPreviewSwatch;
  readonly Border currentPreviewSwatch;
  readonly TextBox hexBox;
  readonly TextBox redBox;
  readonly TextBox greenBox;
  readonly TextBox blueBox;

  double currentHue;
  double currentSaturation;
  double currentValue;
  bool updatingInputs;
  bool draggingSv;
  bool draggingHue;
  IntPtr ownerWindowHandle;

  public Color SelectedColor { get; private set; }
  public int SelectedCustomSlotIndex { get { return paletteState.SelectedSlotIndex; } }

  internal Border[] CustomSlotFrames { get { return customSlotFrames; } }
  internal Button SaveCustomColorButton { get { return saveCustomColorButton; } }
  internal Button ClearCustomColorButton { get { return clearCustomColorButton; } }
  internal TextBlock CustomSlotStatusText { get { return customSlotStatusText; } }

  public LyricsColorPickerDialog(Color initialColor, int[] existingCustomColors, string title = "选择颜色") {
   string effectiveTitle = string.IsNullOrWhiteSpace(title) ? "选择颜色" : title;
   Title = effectiveTitle;
   WindowStyle = WindowStyle.SingleBorderWindow;
   ResizeMode = ResizeMode.NoResize;
   ShowInTaskbar = false;
   Topmost = true;
   SizeToContent = SizeToContent.WidthAndHeight;
   WindowStartupLocation = WindowStartupLocation.CenterScreen;
   Background = Brushes.White;
   UseLayoutRounding = true;
   SnapsToDevicePixels = true;
   FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI, Microsoft YaHei");
   AutomationProperties.SetName(this, effectiveTitle);

   paletteState = new CustomColorPaletteState(existingCustomColors);
   initialRgb = Color.FromRgb(initialColor.R, initialColor.G, initialColor.B);
   SelectedColor = initialRgb;
   CustomColorPaletteState.RgbToHsv(initialRgb.R, initialRgb.G, initialRgb.B, out currentHue, out currentSaturation, out currentValue);

   basicSwatchFrames = new Border[BasicPalette.Length];
   basicSwatchInners = new Border[BasicPalette.Length];
   customSlotFrames = new Border[CustomColorPaletteState.SlotCount];
   customSlotInners = new Border[CustomColorPaletteState.SlotCount];
   customSlotPlusLabels = new TextBlock[CustomColorPaletteState.SlotCount];

   Style buttonStyle = CreateButtonStyle();
   Style textBoxStyle = CreateTextBoxStyle();

   Grid root = new Grid { Margin = new Thickness(14) };
   root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
   root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

   Grid columns = new Grid();
   columns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
   columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
   columns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

   // Left column: Basic colors + Custom colors
   StackPanel leftColumn = new StackPanel { Orientation = Orientation.Vertical };
   leftColumn.Children.Add(MakeSectionTitle("基本颜色", new Thickness(0, 0, 0, 6)));

   UniformGrid basicGrid = new UniformGrid { Rows = 4, Columns = 8 };
   for (int i = 0; i < BasicPalette.Length; i++) {
    Border frame = CreateBasicSwatchCell(i);
    basicGrid.Children.Add(frame);
   }
   leftColumn.Children.Add(basicGrid);

   leftColumn.Children.Add(MakeSectionTitle("自定义颜色", new Thickness(0, 12, 0, 2)));
   customSlotStatusText = new TextBlock {
    Foreground = TextSecondaryBrush,
    FontSize = 11.5,
    Margin = new Thickness(0, 0, 0, 6),
    TextTrimming = TextTrimming.CharacterEllipsis
   };
   AutomationProperties.SetName(customSlotStatusText, "自定义颜色槽状态");
   leftColumn.Children.Add(customSlotStatusText);

   UniformGrid customGrid = new UniformGrid { Rows = 2, Columns = 8 };
   for (int i = 0; i < CustomColorPaletteState.SlotCount; i++) {
    Border frame = CreateCustomSlotCell(i);
    customGrid.Children.Add(frame);
   }
   leftColumn.Children.Add(customGrid);

   StackPanel customActions = new StackPanel {
    Orientation = Orientation.Horizontal,
    Margin = new Thickness(0, 8, 0, 0)
   };
   saveCustomColorButton = MakeDialogButton("保存到选中格", "将当前颜色保存到选中的自定义颜色格", buttonStyle, delegate {
    ClickSaveCustomColor();
   });
   saveCustomColorButton.Margin = new Thickness(0, 0, 8, 0);
   clearCustomColorButton = MakeDialogButton("清除此格", "清除当前选中的自定义颜色格", buttonStyle, delegate {
    ClickClearCustomColor();
   });
   clearCustomColorButton.Margin = new Thickness(0);
   customActions.Children.Add(saveCustomColorButton);
   customActions.Children.Add(clearCustomColorButton);
   leftColumn.Children.Add(customActions);

   Grid.SetColumn(leftColumn, 0);
   columns.Children.Add(leftColumn);

   // Right column: 2D SV + Hue bar + Preview + HEX/RGB
   StackPanel rightColumn = new StackPanel { Orientation = Orientation.Vertical };

   StackPanel pickerArea = new StackPanel { Orientation = Orientation.Horizontal };
   Border svBorder = BuildSvSelector(out svSurface, out svBaseColorLayer, out svOuterCursor, out svInnerCursor);
   Border hueBorder = BuildHueBar(out hueSurface, out hueIndicator);
   hueBorder.Margin = new Thickness(10, 0, 0, 0);
   pickerArea.Children.Add(svBorder);
   pickerArea.Children.Add(hueBorder);
   rightColumn.Children.Add(pickerArea);

   Grid previewAndInputs = new Grid { Margin = new Thickness(0, 10, 0, 0) };
   previewAndInputs.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
   previewAndInputs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
   previewAndInputs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

   // Original / Current comparison
   StackPanel previewPanel = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Top };
   StackPanel swatchesRow = new StackPanel { Orientation = Orientation.Horizontal };

   StackPanel origCol = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 6, 0) };
   originalPreviewSwatch = new Border {
    Width = 38, Height = 30,
    Background = new SolidColorBrush(initialRgb),
    BorderBrush = SwatchInnerBorderBrush, BorderThickness = new Thickness(1),
    CornerRadius = new CornerRadius(3),
    Cursor = Cursors.Hand,
    Focusable = true,
    ToolTip = string.Format("原色 {0}（点击还原）", FormatHex(initialRgb))
   };
   AutomationProperties.SetName(originalPreviewSwatch, "原色，点击还原初始颜色");
   originalPreviewSwatch.MouseLeftButtonDown += (s, e) => {
    RestoreInitialColor();
    e.Handled = true;
   };
   originalPreviewSwatch.KeyDown += (s, e) => {
    if (e.Key == Key.Enter || e.Key == Key.Space) {
     RestoreInitialColor();
     e.Handled = true;
    }
   };
   origCol.Children.Add(originalPreviewSwatch);
   origCol.Children.Add(new TextBlock {
    Text = "原色", Foreground = TextSecondaryBrush, FontSize = 11,
    HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0)
   });

   StackPanel currCol = new StackPanel { Orientation = Orientation.Vertical };
   currentPreviewSwatch = new Border {
    Width = 38, Height = 30,
    BorderBrush = SwatchInnerBorderBrush, BorderThickness = new Thickness(1),
    CornerRadius = new CornerRadius(3)
   };
   AutomationProperties.SetName(currentPreviewSwatch, "当前颜色预览");
   currCol.Children.Add(currentPreviewSwatch);
   currCol.Children.Add(new TextBlock {
    Text = "当前", Foreground = TextSecondaryBrush, FontSize = 11,
    HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0)
   });

   swatchesRow.Children.Add(origCol);
   swatchesRow.Children.Add(currCol);
   previewPanel.Children.Add(swatchesRow);

   Grid.SetColumn(previewPanel, 0);
   previewAndInputs.Children.Add(previewPanel);

   // HEX + RGB inputs
   Grid inputGrid = new Grid { VerticalAlignment = VerticalAlignment.Top };
   inputGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
   inputGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
   inputGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

   hexBox = CreateInputBox(7, 96, textBoxStyle, "十六进制颜色代码");
   redBox = CreateInputBox(3, 44, textBoxStyle, "红色分量 0 到 255");
   greenBox = CreateInputBox(3, 44, textBoxStyle, "绿色分量 0 到 255");
   blueBox = CreateInputBox(3, 44, textBoxStyle, "蓝色分量 0 到 255");

   StackPanel hexRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
   hexRow.Children.Add(new TextBlock {
    Text = "HEX", Foreground = TextSecondaryBrush, FontSize = 11.5,
    Width = 28, VerticalAlignment = VerticalAlignment.Center
   });
   hexRow.Children.Add(hexBox);
   Grid.SetRow(hexRow, 0);
   inputGrid.Children.Add(hexRow);

   StackPanel rgbRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
   rgbRow.Children.Add(MakeChannelPair("R", redBox));
   rgbRow.Children.Add(MakeChannelPair("G", greenBox));
   StackPanel bPair = MakeChannelPair("B", blueBox);
   bPair.Margin = new Thickness(0);
   rgbRow.Children.Add(bPair);
   Grid.SetRow(rgbRow, 2);
   inputGrid.Children.Add(rgbRow);

   Grid.SetColumn(inputGrid, 2);
   previewAndInputs.Children.Add(inputGrid);

   rightColumn.Children.Add(previewAndInputs);
   Grid.SetColumn(rightColumn, 2);
   columns.Children.Add(rightColumn);

   Grid.SetRow(columns, 0);
   root.Children.Add(columns);

   // Bottom bar: OK / Cancel
   StackPanel footer = new StackPanel {
    Orientation = Orientation.Horizontal,
    HorizontalAlignment = HorizontalAlignment.Right,
    Margin = new Thickness(0, 14, 0, 0)
   };
   Button okButton = MakeDialogButton("确定", "确认选用当前颜色", CreatePrimaryButtonStyle(), delegate {
    CommitFocusedInputBeforeAccept();
    DialogResult = true;
    Close();
   });
   okButton.IsDefault = true;
   okButton.MinWidth = 76;
   okButton.Margin = new Thickness(0, 0, 8, 0);

   Button cancelButton = MakeDialogButton("取消", "取消选色并关闭", buttonStyle, delegate {
    DialogResult = false;
    Close();
   });
   cancelButton.IsCancel = true;
   cancelButton.MinWidth = 76;
   cancelButton.Margin = new Thickness(0);

   footer.Children.Add(okButton);
   footer.Children.Add(cancelButton);
   Grid.SetRow(footer, 1);
   root.Children.Add(footer);

   Content = root;

   WireInputEvents();
   Loaded += (s, e) => {
    if (ownerWindowHandle != IntPtr.Zero) {
     PositionRelativeToOwnerScreen(ownerWindowHandle);
    }
   };

   SyncAllFromHsv(true);
   RefreshCustomSlotVisuals(null);
  }

  public int[] GetUpdatedCustomColors() {
   return paletteState.GetNormalizedCustomColors();
  }

  internal void SetOwnerAndPosition(IntPtr ownerHandle) {
   ownerWindowHandle = ownerHandle;
   try {
    new WindowInteropHelper(this).Owner = ownerHandle;
   } catch {
   }
   if (ownerHandle == IntPtr.Zero) {
    WindowStartupLocation = WindowStartupLocation.CenterScreen;
    return;
   }
   PositionRelativeToOwnerScreen(ownerHandle);
  }

  internal void SetCurrentColor(Color color) {
   Color rgb = Color.FromRgb(color.R, color.G, color.B);
   double h;
   double s;
   double v;
   CustomColorPaletteState.RgbToHsv(rgb.R, rgb.G, rgb.B, out h, out s, out v);
   if (s > 0.0) {
    currentHue = h;
   }
   currentSaturation = s;
   currentValue = v;
   SelectedColor = rgb;
   SyncUiAfterColorChange(rgb, true, true);
  }

  internal void ClickCustomSlot(int index) {
   Color loaded;
   if (paletteState.SelectSlot(index, out loaded)) {
    SetCurrentColor(loaded);
   }
   RefreshCustomSlotVisuals(null);
  }

  internal void ClickSaveCustomColor() {
   int savedIndex;
   if (!paletteState.SaveColorToSelectedSlot(SelectedColor, out savedIndex)) {
    RefreshCustomSlotVisuals("纯白已在基本颜色中");
    return;
   }
   RefreshCustomSlotVisuals(null);
  }

  internal void ClickClearCustomColor() {
   paletteState.ClearSelectedSlot();
   RefreshCustomSlotVisuals(null);
  }

  void RestoreInitialColor() {
   SetCurrentColor(initialRgb);
  }

  void PositionRelativeToOwnerScreen(IntPtr ownerHandle) {
   try {
    Forms.Screen screen = Forms.Screen.FromHandle(ownerHandle);
    System.Drawing.Rectangle workingArea = screen.WorkingArea;
    double scaleX;
    double scaleY;
    if (!TryGetDpiScale(ownerHandle, out scaleX, out scaleY)) {
     scaleX = 1.0;
     scaleY = 1.0;
    }
    Rect workAreaDip = new Rect(
     workingArea.Left / scaleX,
     workingArea.Top / scaleY,
     workingArea.Width / scaleX,
     workingArea.Height / scaleY);
    if (ActualWidth <= 0 || ActualHeight <= 0) {
     Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
    }
    double width = ActualWidth > 0 ? ActualWidth : (DesiredSize.Width > 0 ? DesiredSize.Width : 540.0);
    double height = ActualHeight > 0 ? ActualHeight : (DesiredSize.Height > 0 ? DesiredSize.Height : 340.0);
    double desiredLeft = workAreaDip.Left + (workAreaDip.Width - width) / 2.0;
    double desiredTop = workAreaDip.Top + (workAreaDip.Height - height) / 2.0;
    Rect visible = LyricsWindowGeometry.KeepVisible(new Rect(desiredLeft, desiredTop, width, height), workAreaDip, 8.0);
    WindowStartupLocation = WindowStartupLocation.Manual;
    Left = visible.Left;
    Top = visible.Top;
   } catch {
    WindowStartupLocation = WindowStartupLocation.CenterScreen;
   }
  }

  bool TryGetDpiScale(IntPtr ownerHandle, out double scaleX, out double scaleY) {
   HwndSource ownerSource = null;
   try {
    ownerSource = HwndSource.FromHwnd(ownerHandle);
   } catch {
   }
   if (ownerSource != null && ownerSource.CompositionTarget != null) {
    Matrix toDevice = ownerSource.CompositionTarget.TransformToDevice;
    if (toDevice.M11 > 0 && toDevice.M22 > 0) {
     scaleX = toDevice.M11;
     scaleY = toDevice.M22;
     return true;
    }
   }

   PresentationSource selfSource = PresentationSource.FromVisual(this);
   if (selfSource != null && selfSource.CompositionTarget != null) {
    Matrix toDevice = selfSource.CompositionTarget.TransformToDevice;
    if (toDevice.M11 > 0 && toDevice.M22 > 0) {
     scaleX = toDevice.M11;
     scaleY = toDevice.M22;
     return true;
    }
   }

   try {
    using (System.Drawing.Graphics g = System.Drawing.Graphics.FromHwnd(ownerHandle)) {
     if (g.DpiX > 0 && g.DpiY > 0) {
      scaleX = g.DpiX / 96.0;
      scaleY = g.DpiY / 96.0;
      return true;
     }
    }
   } catch {
   }

   scaleX = 1.0;
   scaleY = 1.0;
   return false;
  }

  Border CreateBasicSwatchCell(int index) {
   Color swatchColor = BasicPalette[index];
   string hex = FormatHex(swatchColor);
   Border inner = new Border {
    Width = 20,
    Height = 20,
    CornerRadius = new CornerRadius(2),
    BorderThickness = new Thickness(1),
    BorderBrush = SwatchInnerBorderBrush,
    Background = new SolidColorBrush(swatchColor)
   };
   Border frame = new Border {
    BorderThickness = new Thickness(2),
    Padding = new Thickness(1),
    CornerRadius = new CornerRadius(3),
    Margin = new Thickness(2),
    Background = Brushes.Transparent,
    BorderBrush = Brushes.Transparent,
    Focusable = true,
    Cursor = Cursors.Hand,
    ToolTip = hex,
    Child = inner
   };
   AutomationProperties.SetName(frame, "基本颜色 " + hex);

   int capturedIndex = index;
   frame.MouseLeftButtonDown += (s, e) => {
    frame.Focus();
    SetCurrentColor(BasicPalette[capturedIndex]);
    e.Handled = true;
   };
   frame.KeyDown += (s, e) => {
    if (e.Key == Key.Enter || e.Key == Key.Space) {
     SetCurrentColor(BasicPalette[capturedIndex]);
     e.Handled = true;
    }
   };

   basicSwatchFrames[index] = frame;
   basicSwatchInners[index] = inner;
   return frame;
  }

  Border CreateCustomSlotCell(int index) {
   TextBlock plusLabel = new TextBlock {
    Text = "+",
    FontSize = 13,
    FontWeight = FontWeights.Medium,
    Foreground = EmptyPlusBrush,
    HorizontalAlignment = HorizontalAlignment.Center,
    VerticalAlignment = VerticalAlignment.Center,
    Margin = new Thickness(0, -1, 0, 0),
    IsHitTestVisible = false
   };
   Border inner = new Border {
    Width = 20,
    Height = 20,
    CornerRadius = new CornerRadius(2),
    BorderThickness = new Thickness(1),
    Child = plusLabel
   };
   Border frame = new Border {
    BorderThickness = new Thickness(2),
    Padding = new Thickness(1),
    CornerRadius = new CornerRadius(3),
    Margin = new Thickness(2),
    Background = Brushes.Transparent,
    Focusable = true,
    Cursor = Cursors.Hand,
    Child = inner
   };

   int capturedIndex = index;
   frame.MouseLeftButtonDown += (s, e) => {
    frame.Focus();
    ClickCustomSlot(capturedIndex);
    e.Handled = true;
   };
   frame.KeyDown += (s, e) => {
    if (e.Key == Key.Enter || e.Key == Key.Space) {
     ClickCustomSlot(capturedIndex);
     e.Handled = true;
    }
   };

   customSlotFrames[index] = frame;
   customSlotInners[index] = inner;
   customSlotPlusLabels[index] = plusLabel;
   return frame;
  }

  Border BuildSvSelector(out Grid surface, out Border baseLayer, out Ellipse outerCursor, out Ellipse innerCursor) {
   surface = new Grid {
    Width = SvCanvasWidth,
    Height = SvCanvasHeight,
    ClipToBounds = true,
    Cursor = Cursors.Cross,
    Background = Brushes.Transparent
   };
   AutomationProperties.SetName(surface, "饱和度与明度调色区域");

   baseLayer = new Border { Background = Brushes.Red, IsHitTestVisible = false };
   Border saturationLayer = new Border {
    IsHitTestVisible = false,
    Background = new LinearGradientBrush(
     Colors.White,
     Color.FromArgb(0, 255, 255, 255),
     new Point(0, 0),
     new Point(1, 0))
   };
   Border valueLayer = new Border {
    IsHitTestVisible = false,
    Background = new LinearGradientBrush(
     Color.FromArgb(0, 0, 0, 0),
     Colors.Black,
     new Point(0, 0),
     new Point(0, 1))
   };

   Canvas overlay = new Canvas { IsHitTestVisible = false };
   outerCursor = new Ellipse {
    Width = 12,
    Height = 12,
    Stroke = Brushes.Black,
    StrokeThickness = 1.5,
    Fill = Brushes.Transparent
   };
   innerCursor = new Ellipse {
    Width = 10,
    Height = 10,
    Stroke = Brushes.White,
    StrokeThickness = 1.5,
    Fill = Brushes.Transparent
   };
   overlay.Children.Add(outerCursor);
   overlay.Children.Add(innerCursor);

   surface.Children.Add(baseLayer);
   surface.Children.Add(saturationLayer);
   surface.Children.Add(valueLayer);
   surface.Children.Add(overlay);

   surface.MouseLeftButtonDown += OnSvMouseDown;
   surface.MouseMove += OnSvMouseMove;
   surface.MouseLeftButtonUp += OnSvMouseUp;
   surface.LostMouseCapture += (s, e) => { draggingSv = false; };

   return new Border {
    BorderBrush = SwatchInnerBorderBrush,
    BorderThickness = new Thickness(1),
    CornerRadius = new CornerRadius(2),
    Child = surface
   };
  }

  Border BuildHueBar(out Grid surface, out Border indicator) {
   surface = new Grid {
    Width = HueBarWidth,
    Height = HueBarHeight,
    ClipToBounds = true,
    Cursor = Cursors.SizeNS
   };
   AutomationProperties.SetName(surface, "色相滑块条");

   LinearGradientBrush rainbow = new LinearGradientBrush {
    StartPoint = new Point(0, 0),
    EndPoint = new Point(0, 1)
   };
   rainbow.GradientStops.Add(new GradientStop(Color.FromRgb(255, 0, 0), 0.0 / 6.0));
   rainbow.GradientStops.Add(new GradientStop(Color.FromRgb(255, 255, 0), 1.0 / 6.0));
   rainbow.GradientStops.Add(new GradientStop(Color.FromRgb(0, 255, 0), 2.0 / 6.0));
   rainbow.GradientStops.Add(new GradientStop(Color.FromRgb(0, 255, 255), 3.0 / 6.0));
   rainbow.GradientStops.Add(new GradientStop(Color.FromRgb(0, 0, 255), 4.0 / 6.0));
   rainbow.GradientStops.Add(new GradientStop(Color.FromRgb(255, 0, 255), 5.0 / 6.0));
   rainbow.GradientStops.Add(new GradientStop(Color.FromRgb(255, 0, 0), 1.0));
   surface.Background = rainbow;

   Canvas indicatorOverlay = new Canvas { IsHitTestVisible = false };
   indicator = new Border {
    Width = HueBarWidth,
    Height = 5,
    BorderBrush = Brushes.Black,
    BorderThickness = new Thickness(1),
    Background = Brushes.White
   };
   indicatorOverlay.Children.Add(indicator);
   surface.Children.Add(indicatorOverlay);

   surface.MouseLeftButtonDown += OnHueMouseDown;
   surface.MouseMove += OnHueMouseMove;
   surface.MouseLeftButtonUp += OnHueMouseUp;
   surface.LostMouseCapture += (s, e) => { draggingHue = false; };

   return new Border {
    BorderBrush = SwatchInnerBorderBrush,
    BorderThickness = new Thickness(1),
    CornerRadius = new CornerRadius(2),
    Child = surface
   };
  }

  void OnSvMouseDown(object sender, MouseButtonEventArgs e) {
   draggingSv = true;
   svSurface.CaptureMouse();
   UpdateSvFromPoint(e.GetPosition(svSurface));
   e.Handled = true;
  }

  void OnSvMouseMove(object sender, MouseEventArgs e) {
   if (!draggingSv) return;
   UpdateSvFromPoint(e.GetPosition(svSurface));
  }

  void OnSvMouseUp(object sender, MouseButtonEventArgs e) {
   if (!draggingSv) return;
   UpdateSvFromPoint(e.GetPosition(svSurface));
   draggingSv = false;
   if (svSurface.IsMouseCaptured) {
    svSurface.ReleaseMouseCapture();
   }
   e.Handled = true;
  }

  void UpdateSvFromPoint(Point point) {
   double s = Clamp(point.X / SvCanvasWidth, 0.0, 1.0);
   double v = Clamp(1.0 - (point.Y / SvCanvasHeight), 0.0, 1.0);
   currentSaturation = s;
   currentValue = v;
   SyncAllFromHsv(true);
  }

  void OnHueMouseDown(object sender, MouseButtonEventArgs e) {
   draggingHue = true;
   hueSurface.CaptureMouse();
   UpdateHueFromPoint(e.GetPosition(hueSurface));
   e.Handled = true;
  }

  void OnHueMouseMove(object sender, MouseEventArgs e) {
   if (!draggingHue) return;
   UpdateHueFromPoint(e.GetPosition(hueSurface));
  }

  void OnHueMouseUp(object sender, MouseButtonEventArgs e) {
   if (!draggingHue) return;
   UpdateHueFromPoint(e.GetPosition(hueSurface));
   draggingHue = false;
   if (hueSurface.IsMouseCaptured) {
    hueSurface.ReleaseMouseCapture();
   }
   e.Handled = true;
  }

  void UpdateHueFromPoint(Point point) {
   double ratio = Clamp(point.Y / HueBarHeight, 0.0, 0.9999);
   currentHue = ratio * 360.0;
   SyncAllFromHsv(true);
  }

  void SyncAllFromHsv(bool updateTextBoxes) {
   Color rgb = CustomColorPaletteState.HsvToRgb(currentHue, currentSaturation, currentValue);
   SelectedColor = rgb;
   SyncUiAfterColorChange(rgb, updateTextBoxes, updateTextBoxes);
  }

  void SyncUiAfterColorChange(Color rgb, bool updateHexBox, bool updateRgbBoxes) {
   updatingInputs = true;
   try {
    Color pureHue = CustomColorPaletteState.HsvToRgb(currentHue, 1.0, 1.0);
    svBaseColorLayer.Background = new SolidColorBrush(pureHue);

    double cursorX = currentSaturation * SvCanvasWidth;
    double cursorY = (1.0 - currentValue) * SvCanvasHeight;
    Canvas.SetLeft(svOuterCursor, cursorX - 6.0);
    Canvas.SetTop(svOuterCursor, cursorY - 6.0);
    Canvas.SetLeft(svInnerCursor, cursorX - 5.0);
    Canvas.SetTop(svInnerCursor, cursorY - 5.0);

    double hueY = Clamp(currentHue / 360.0, 0.0, 1.0) * HueBarHeight;
    Canvas.SetLeft(hueIndicator, 0.0);
    Canvas.SetTop(hueIndicator, Clamp(hueY - 2.5, 0.0, HueBarHeight - 5.0));

    currentPreviewSwatch.Background = new SolidColorBrush(rgb);
    currentPreviewSwatch.ToolTip = "当前颜色 " + FormatHex(rgb);

    if (updateHexBox) {
     hexBox.Text = FormatHex(rgb);
    }
    if (updateRgbBoxes) {
     redBox.Text = rgb.R.ToString(CultureInfo.InvariantCulture);
     greenBox.Text = rgb.G.ToString(CultureInfo.InvariantCulture);
     blueBox.Text = rgb.B.ToString(CultureInfo.InvariantCulture);
    }

    UpdateBasicSwatchHighlights(rgb);
   } finally {
    updatingInputs = false;
   }
  }

  void UpdateBasicSwatchHighlights(Color current) {
   for (int i = 0; i < BasicPalette.Length; i++) {
    Color c = BasicPalette[i];
    bool matched = c.R == current.R && c.G == current.G && c.B == current.B;
    basicSwatchFrames[i].BorderBrush = matched ? Brushes.Black : Brushes.Transparent;
    basicSwatchInners[i].BorderBrush = matched ? Brushes.White : SwatchInnerBorderBrush;
   }
  }

  void RefreshCustomSlotVisuals(string overrideNotice) {
   int selectedIndex = paletteState.SelectedSlotIndex;
   for (int i = 0; i < CustomColorPaletteState.SlotCount; i++) {
    bool isSelected = i == selectedIndex;
    bool isEmpty = paletteState.IsSlotEmpty(i);
    Color slotColor = isEmpty ? default(Color) : CustomColorPaletteState.FromColorRef(paletteState.GetSlotColorRef(i));

    customSlotFrames[i].BorderBrush = isSelected ? Brushes.Black : Brushes.Transparent;
    if (isSelected) {
     customSlotInners[i].BorderBrush = Brushes.White;
    } else {
     customSlotInners[i].BorderBrush = isEmpty ? EmptySlotBorderBrush : SwatchInnerBorderBrush;
    }

    if (isEmpty) {
     customSlotInners[i].Background = EmptySlotFillBrush;
     customSlotPlusLabels[i].Visibility = Visibility.Visible;
     string emptyTip = string.Format("第 {0} 格（暂无保存，点击选中作为保存目标）", i + 1);
     customSlotFrames[i].ToolTip = emptyTip;
     AutomationProperties.SetName(customSlotFrames[i], string.Format("自定义颜色第 {0} 格，暂无保存", i + 1));
    } else {
     string hex = FormatHex(slotColor);
     customSlotInners[i].Background = new SolidColorBrush(slotColor);
     customSlotPlusLabels[i].Visibility = Visibility.Collapsed;
     string filledTip = string.Format("第 {0} 格（{1}，点击选用并选中此格）", i + 1, hex);
     customSlotFrames[i].ToolTip = filledTip;
     AutomationProperties.SetName(customSlotFrames[i], string.Format("自定义颜色第 {0} 格，{1}", i + 1, hex));
    }
   }

   clearCustomColorButton.IsEnabled = !paletteState.IsSlotEmpty(selectedIndex);

   if (!string.IsNullOrEmpty(overrideNotice)) {
    customSlotStatusText.Text = overrideNotice;
    customSlotStatusText.Foreground = NoticeAccentBrush;
    return;
   }

   customSlotStatusText.Foreground = TextSecondaryBrush;
   if (paletteState.IsSlotEmpty(selectedIndex)) {
    customSlotStatusText.Text = string.Format("已选第 {0} 格 · 暂无保存", selectedIndex + 1);
   } else {
    Color selectedColor = CustomColorPaletteState.FromColorRef(paletteState.GetSlotColorRef(selectedIndex));
    customSlotStatusText.Text = string.Format("已选第 {0} 格 · {1}", selectedIndex + 1, FormatHex(selectedColor));
   }
  }

  void WireInputEvents() {
   hexBox.TextChanged += (s, e) => OnHexTextChanged(false);
   hexBox.LostKeyboardFocus += (s, e) => OnHexTextChanged(true);

   redBox.TextChanged += (s, e) => OnRgbTextChanged(false);
   greenBox.TextChanged += (s, e) => OnRgbTextChanged(false);
   blueBox.TextChanged += (s, e) => OnRgbTextChanged(false);

   redBox.LostKeyboardFocus += (s, e) => OnRgbTextChanged(true);
   greenBox.LostKeyboardFocus += (s, e) => OnRgbTextChanged(true);
   blueBox.LostKeyboardFocus += (s, e) => OnRgbTextChanged(true);
  }

  void CommitFocusedInputBeforeAccept() {
   if (hexBox.IsKeyboardFocusWithin) {
    OnHexTextChanged(true);
   } else if (redBox.IsKeyboardFocusWithin || greenBox.IsKeyboardFocusWithin || blueBox.IsKeyboardFocusWithin) {
    OnRgbTextChanged(true);
   }
  }

  void OnHexTextChanged(bool finished) {
   if (updatingInputs) return;
   string raw = (hexBox.Text ?? "").Trim();
   Color parsed;
   if (TryParseHex(raw, finished, out parsed)) {
    double h;
    double s;
    double v;
    CustomColorPaletteState.RgbToHsv(parsed.R, parsed.G, parsed.B, out h, out s, out v);
    if (s > 0.0) {
     currentHue = h;
    }
    currentSaturation = s;
    currentValue = v;
    SelectedColor = parsed;
    SyncUiAfterColorChange(parsed, finished, true);
   } else if (finished) {
    updatingInputs = true;
    try {
     hexBox.Text = FormatHex(SelectedColor);
    } finally {
     updatingInputs = false;
    }
   }
  }

  void OnRgbTextChanged(bool finished) {
   if (updatingInputs) return;
   byte r;
   byte g;
   byte b;
   if (TryParseByte(redBox.Text, out r) && TryParseByte(greenBox.Text, out g) && TryParseByte(blueBox.Text, out b)) {
    Color parsed = Color.FromRgb(r, g, b);
    double h;
    double s;
    double v;
    CustomColorPaletteState.RgbToHsv(r, g, b, out h, out s, out v);
    if (s > 0.0) {
     currentHue = h;
    }
    currentSaturation = s;
    currentValue = v;
    SelectedColor = parsed;
    SyncUiAfterColorChange(parsed, true, finished);
   } else if (finished) {
    updatingInputs = true;
    try {
     redBox.Text = SelectedColor.R.ToString(CultureInfo.InvariantCulture);
     greenBox.Text = SelectedColor.G.ToString(CultureInfo.InvariantCulture);
     blueBox.Text = SelectedColor.B.ToString(CultureInfo.InvariantCulture);
    } finally {
     updatingInputs = false;
    }
   }
  }

  static bool TryParseHex(string raw, bool allowShort, out Color color) {
   color = default(Color);
   if (string.IsNullOrEmpty(raw)) return false;
   string digits = raw.StartsWith("#", StringComparison.Ordinal) ? raw.Substring(1) : raw;
   if (allowShort && digits.Length == 3) {
    byte r4;
    byte g4;
    byte b4;
    if (byte.TryParse(digits.Substring(0, 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r4) &&
        byte.TryParse(digits.Substring(1, 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g4) &&
        byte.TryParse(digits.Substring(2, 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b4)) {
     color = Color.FromRgb((byte)(r4 * 17), (byte)(g4 * 17), (byte)(b4 * 17));
     return true;
    }
    return false;
   }
   if (digits.Length != 6) return false;
   if (!allowShort && !raw.StartsWith("#", StringComparison.Ordinal)) return false;
   byte r;
   byte g;
   byte b;
   if (byte.TryParse(digits.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r) &&
       byte.TryParse(digits.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g) &&
       byte.TryParse(digits.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b)) {
    color = Color.FromRgb(r, g, b);
    return true;
   }
   return false;
  }

  static bool TryParseByte(string text, out byte value) {
   value = 0;
   if (string.IsNullOrWhiteSpace(text)) return false;
   int parsed;
   if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) return false;
   if (parsed < 0 || parsed > 255) return false;
   value = (byte)parsed;
   return true;
  }

  static string FormatHex(Color color) {
   return string.Format("#{0:X2}{1:X2}{2:X2}", color.R, color.G, color.B);
  }

  static double Clamp(double value, double min, double max) {
   return Math.Max(min, Math.Min(max, value));
  }

  static TextBlock MakeSectionTitle(string text, Thickness margin) {
   TextBlock block = new TextBlock {
    Text = text,
    Foreground = TextPrimaryBrush,
    FontSize = 12.5,
    FontWeight = FontWeights.SemiBold,
    Margin = margin
   };
   AutomationProperties.SetName(block, text);
   return block;
  }

  static StackPanel MakeChannelPair(string label, TextBox box) {
   StackPanel pair = new StackPanel {
    Orientation = Orientation.Horizontal,
    Margin = new Thickness(0, 0, 6, 0),
    VerticalAlignment = VerticalAlignment.Center
   };
   pair.Children.Add(new TextBlock {
    Text = label,
    Foreground = TextSecondaryBrush,
    FontSize = 11.5,
    Width = 13,
    VerticalAlignment = VerticalAlignment.Center
   });
   pair.Children.Add(box);
   return pair;
  }

  static TextBox CreateInputBox(int maxLength, double width, Style style, string accessibleName) {
   TextBox box = new TextBox {
    Width = width,
    MinHeight = 25,
    Padding = new Thickness(5, 2, 5, 2),
    MaxLength = maxLength,
    Style = style,
    VerticalContentAlignment = VerticalAlignment.Center
   };
   AutomationProperties.SetName(box, accessibleName);
   return box;
  }

  static Button MakeDialogButton(string content, string accessibleName, Style style, Action onClick) {
   Button button = new Button {
    Content = content,
    ToolTip = accessibleName,
    Padding = new Thickness(10, 4, 10, 4),
    MinHeight = 28,
    Style = style
   };
   AutomationProperties.SetName(button, accessibleName);
   if (onClick != null) {
    button.Click += (s, e) => onClick();
   }
   return button;
  }

  static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b) {
   SolidColorBrush brush = new SolidColorBrush(Color.FromRgb(r, g, b));
   brush.Freeze();
   return brush;
  }

  static Style CreateTextBoxStyle() {
   Style style = new Style(typeof(TextBox));
   style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.White));
   style.Setters.Add(new Setter(Control.ForegroundProperty, TextPrimaryBrush));
   style.Setters.Add(new Setter(Control.BorderBrushProperty, SubtleBorderBrush));
   style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
   style.Setters.Add(new Setter(TextBoxBase.SelectionBrushProperty, CreateFrozenBrush(238, 151, 174)));
   style.Setters.Add(new Setter(TextBoxBase.SelectionTextBrushProperty, CreateFrozenBrush(50, 28, 36)));

   Trigger hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
   hover.SetValue(Control.BackgroundProperty, CreateFrozenBrush(255, 248, 250));
   hover.SetValue(Control.BorderBrushProperty, CreateFrozenBrush(232, 142, 163));
   style.Triggers.Add(hover);

   Trigger focus = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
   focus.SetValue(Control.BackgroundProperty, CreateFrozenBrush(255, 248, 250));
   focus.SetValue(Control.BorderBrushProperty, CreateFrozenBrush(204, 78, 108));
   style.Triggers.Add(focus);

   Trigger selected = new Trigger { Property = TextBoxBase.IsSelectionActiveProperty, Value = true };
   selected.SetValue(TextBoxBase.SelectionBrushProperty, CreateFrozenBrush(204, 78, 108));
   selected.SetValue(TextBoxBase.SelectionTextBrushProperty, Brushes.White);
   style.Triggers.Add(selected);

   return style;
  }

  static Style CreateButtonStyle() {
   Style style = new Style(typeof(Button));
   style.Setters.Add(new Setter(Control.BackgroundProperty, CreateFrozenBrush(248, 248, 250)));
   style.Setters.Add(new Setter(Control.ForegroundProperty, TextPrimaryBrush));
   style.Setters.Add(new Setter(Control.BorderBrushProperty, SubtleBorderBrush));
   style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
   AddCommonButtonTriggers(style);
   return style;
  }

  static Style CreatePrimaryButtonStyle() {
   Style style = new Style(typeof(Button));
   style.Setters.Add(new Setter(Control.BackgroundProperty, CreateFrozenBrush(255, 235, 241)));
   style.Setters.Add(new Setter(Control.ForegroundProperty, CreateFrozenBrush(125, 42, 67)));
   style.Setters.Add(new Setter(Control.BorderBrushProperty, CreateFrozenBrush(204, 78, 108)));
   style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
   AddCommonButtonTriggers(style);
   return style;
  }

  static void AddCommonButtonTriggers(Style style) {
   Trigger hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
   hover.SetValue(Control.BackgroundProperty, CreateFrozenBrush(255, 244, 247));
   hover.SetValue(Control.BorderBrushProperty, CreateFrozenBrush(232, 142, 163));
   style.Triggers.Add(hover);

   Trigger focus = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
   focus.SetValue(Control.BackgroundProperty, CreateFrozenBrush(255, 235, 241));
   focus.SetValue(Control.BorderBrushProperty, CreateFrozenBrush(204, 78, 108));
   style.Triggers.Add(focus);

   Trigger pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
   pressed.SetValue(Control.BackgroundProperty, CreateFrozenBrush(238, 204, 214));
   pressed.SetValue(Control.BorderBrushProperty, NoticeAccentBrush);
   style.Triggers.Add(pressed);

   Trigger disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
   disabled.SetValue(Control.BackgroundProperty, CreateFrozenBrush(244, 245, 247));
   disabled.SetValue(Control.ForegroundProperty, CreateFrozenBrush(150, 153, 162));
   disabled.SetValue(Control.BorderBrushProperty, CreateFrozenBrush(210, 212, 218));
   disabled.SetValue(Control.OpacityProperty, 0.78);
   style.Triggers.Add(disabled);
  }
 }
}
