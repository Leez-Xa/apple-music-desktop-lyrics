using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace AMLyrics {
 public sealed class LyricsLinePresentation {
  public readonly string Text;
  public readonly double FontSize;
  public readonly LyricsLongLineMode Mode;
  public readonly double Overflow;
  public readonly TimeSpan MotionDuration;

  public LyricsLinePresentation(string text, double fontSize, LyricsLongLineMode mode, double overflow, TimeSpan motionDuration) {
   Text = text ?? ""; FontSize = fontSize; Mode = mode;
   Overflow = overflow; MotionDuration = motionDuration;
  }
 }

 internal struct LyricsLineVerticalMetrics {
  public readonly double Height;
  public readonly double DrawingOffsetY;

  public LyricsLineVerticalMetrics(double height, double drawingOffsetY) {
   Height = height;
   DrawingOffsetY = drawingOffsetY;
  }
 }

 public static class LyricsLineLayoutEngine {
  internal const double StrokeThickness = 1.35;
  internal const double ShadowOffsetY = 1.0;

  public static LyricsLinePresentation Layout(string text, double availableWidth, double requestedFontSize, bool animate, Func<string, double, double> measure) {
   if (measure == null) throw new ArgumentNullException("measure");
   text = text ?? "";
   availableWidth = Math.Max(1, availableWidth);
   double fontSize = LyricsLayoutMetrics.ClampTrackFontSize(requestedFontSize);
   if (text.Length == 0) return new LyricsLinePresentation(text, fontSize, LyricsLongLineMode.Fit, 0, TimeSpan.Zero);

   double measured = Math.Max(0, measure(text, fontSize));
   if (measured <= availableWidth) return new LyricsLinePresentation(text, fontSize, LyricsLongLineMode.Fit, 0, TimeSpan.Zero);

   double overflow = Math.Max(0, measured - availableWidth);
   if (!animate) return new LyricsLinePresentation(text, fontSize, LyricsLongLineMode.Ellipsis, overflow, TimeSpan.Zero);
   double seconds = Math.Max(2.5, Math.Min(12.0, overflow / 35.0 + 2.5));
   return new LyricsLinePresentation(text, fontSize, LyricsLongLineMode.Marquee, overflow, TimeSpan.FromSeconds(seconds));
  }

  internal static FormattedText CreateWpfFormattedText(string text, double fontSize, Typeface typeface, double pixelsPerDip) {
   return new FormattedText(text ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, fontSize, Brushes.White, pixelsPerDip);
  }

  internal static LyricsLineVerticalMetrics MeasureVertical(FormattedText formatted, Geometry glyphs, bool strokeEnabled, bool shadowEnabled, double dpiScaleY) {
   double top = 0;
   double bottom = formatted.Height;
   Rect glyphBounds = glyphs.Bounds;
   if (!glyphBounds.IsEmpty) {
    double strokeRadius = strokeEnabled ? StrokeThickness / 2 : 0;
    top = Math.Min(top, glyphBounds.Top - strokeRadius);
    bottom = Math.Max(bottom, glyphBounds.Bottom + strokeRadius);
    if (shadowEnabled) {
     top = Math.Min(top, glyphBounds.Top + ShadowOffsetY);
     bottom = Math.Max(bottom, glyphBounds.Bottom + ShadowOffsetY);
    }
   }

   double topPixels = Math.Floor(top * dpiScaleY) - 1;
   double bottomPixels = Math.Ceiling(bottom * dpiScaleY) + 1;
   return new LyricsLineVerticalMetrics((bottomPixels - topPixels) / dpiScaleY, -topPixels / dpiScaleY);
  }

  static double Clamp(double value, double minimum, double maximum) {
   return Math.Max(minimum, Math.Min(maximum, value));
  }
 }

 public static class LyricsLayoutMetrics {
  public const double MinimumWidth = 420;
  public const double MaximumWidth = 1400;
  public const double DefaultWidth = 760;
  public const double MinimumHeight = 64;
  public const double MaximumHeight = 260;
  public const double MaximumThreeTrackHeight = 340;
  public const double DefaultFontSize = 30;
  public const double MinimumFontSize = 20;
  public const double MaximumFontSize = 60;
  public const double MinimumSecondaryFontSize = 14;
  public const double HorizontalPadding = 22;
  public const double TrackGap = 4;
  public const double CompactTrackGap = 2;
  public const double NextLineScale = 0.68;
  public const double SecondaryThreeTrackScale = 0.60;

  public static double ClampWidth(double width) { return Clamp(width, MinimumWidth, MaximumWidth); }
  public static double ClampFontSize(double size) { return Clamp(size, MinimumFontSize, MaximumFontSize); }
  public static double ClampTrackFontSize(double size) { return Clamp(size, MinimumSecondaryFontSize, MaximumFontSize); }
  public static double NextLineFontSize(double currentSize) { return Math.Max(MinimumSecondaryFontSize, ClampFontSize(currentSize) * NextLineScale); }
  public static double SecondaryThreeTrackFontSize(double currentSize) {
   double clamped = ClampFontSize(currentSize);
   double scaled = Math.Max(MinimumSecondaryFontSize, clamped * SecondaryThreeTrackScale);
   return clamped > MinimumSecondaryFontSize ? Math.Min(clamped - 1, scaled) : MinimumSecondaryFontSize;
  }
  public static double ComputeVerticalResizeFontSize(double startFontSize, double startHeight, double deltaY, bool topEdge) {
   double safeStartSize = ClampFontSize(startFontSize);
   if (startHeight <= 0) return safeStartSize;
   double targetHeight = topEdge ? startHeight - deltaY : startHeight + deltaY;
   return ClampFontSize(Math.Round(safeStartSize * (targetHeight / startHeight), 1));
  }

  static double Clamp(double value, double minimum, double maximum) {
   return Math.Max(minimum, Math.Min(maximum, value));
  }
 }
}
