using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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
   Refresh();
  }
  void RetryLyrics() {
   var sample = media.Read();
   if (sample.Track == null || !PlaybackMath.IsFresh(sample, DateTime.UtcNow)) return;
   notice = ""; attempts = 1; retryAt = DateTime.UtcNow.AddSeconds(30);
   if (online != null) online.Invalidate(sample.Track);
   pendingSelection = lyrics.SelectAsync(sample.Track); Refresh();
  }
  void Empty(string heading, string detail, string state) { displayedLineIndex = -1; current.Text = heading; next.Text = detail; status.Text = state; source.Text = "自动歌词 · LRCLIB"; }
  internal void Refresh() {
   if (closing) return;
   next.ToolTip = null;
   var sample = media.Read();
   bool fresh = PlaybackMath.IsFresh(sample, DateTime.UtcNow);
   import.IsEnabled = !importing && fresh && sample.Track != null;
   retry.IsEnabled = fresh && sample.Track != null && !lyrics.Read().Loading;
   timingButton.IsEnabled = fresh && sample.Track != null && lyrics.Read().Document != null && lyrics.Read().Document.IsSynchronized;
   timingButton.Content = "校时";
   if (!fresh || sample.Status != "Connected" || sample.Track == null) {
    if (selectedKey.Length > 0) { selectedKey = ""; lyrics.Clear(); }
    title.Text = "Apple Music 桌面歌词";
    if (!fresh) Empty("媒体连接中断", "正在重新连接；确认 Apple Music 仍在运行", "等待连接");
    else if (sample.Status == "Error") { Empty("媒体读取失败", "稍后自动重试；详情见提示", "获取失败"); next.ToolTip = sample.Message; }
    else Empty("等待 Apple Music", "播放歌曲后，将自动获取并同步显示歌词", "等待播放");
    return;
   }
   var track = sample.Track;
   title.Text = track.Title + (string.IsNullOrWhiteSpace(track.Artist) ? "" : "  /  " + track.Artist);
   title.ToolTip = title.Text + "\n拖动移动窗口";
   if (selectedKey != track.Key || (selectedDuration <= 0 && track.DurationSeconds > 0)) {
    selectedKey = track.Key; selectedDuration = track.DurationSeconds; notice = "";
    attempts = 1; retryAt = DateTime.UtcNow.AddSeconds(30);
    pendingSelection = lyrics.SelectAsync(track);
   }
   var state = lyrics.Read();
   if (online != null && !state.Loading && state.Document == null && DateTime.UtcNow >= retryAt && attempts < 3) {
    attempts++; retryAt = DateTime.UtcNow.AddSeconds(60);
    pendingSelection = lyrics.SelectAsync(track); state = lyrics.Read();
   }
   if (state.Loading) { Empty("正在自动获取歌词", "正在匹配当前歌曲的同步歌词", "自动匹配中"); return; }
   if (state.Error.Length > 0) { Empty("暂时无法获取歌词", state.Error + " 可点击重试。", "稍后自动重试"); next.ToolTip = state.Error; return; }
   if (notice.Length > 0 && noticeKey == track.Key) { Empty("歌词导入未完成", notice, "请重试导入"); return; }
   var doc = state.Document;
   if (doc == null || doc.Lines.Count == 0) { Empty("未找到匹配歌词", "已自动查询；可以重试，或使用本地备用歌词", "已连接 Apple Music"); return; }
   source.Text = doc.Source; source.ToolTip = doc.Source;
   if (!doc.IsSynchronized) {
    current.Text = doc.Lines[0].Text; next.Text = doc.Lines.Count > 1 ? string.Join("\n", doc.Lines.GetRange(1, doc.Lines.Count - 1).ConvertAll(l => l.Text)) : "此文件没有时间戳";
    status.Text = "仅有非同步歌词"; return;
   }
   if (!sample.TimelineReliable) {
    current.Text = "时间轴不可用"; next.Text = "已加载同步歌词，等待可靠播放位置"; status.Text = "同步已暂停"; return;
   }
   double offset = timing.Read(track.Key);
   double position = LyricTiming.Position(sample, DateTime.UtcNow, offset);
   int index = PlaybackMath.FindLine(doc.Lines, position);
   if (index != displayedLineIndex) lyricScroll.ScrollToTop();
   if (index != displayedLineIndex && SystemParameters.ClientAreaAnimation) {
    lyricShift.BeginAnimation(TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(7, 0, TimeSpan.FromMilliseconds(180)));
   }
   displayedLineIndex = index;
   current.Text = index < 0 ? "前奏" : (doc.Lines[index].Text.Length == 0 ? "间奏" : doc.Lines[index].Text);
   next.Text = index + 1 < doc.Lines.Count ? doc.Lines[index + 1].Text : "";
   status.Text = track.IsPlaying ? "同步中" : "已暂停";
   if (Math.Abs(offset) > 0.001) {
    string label = (offset > 0 ? "提前 " : "延后 ") + Math.Abs(offset).ToString("0.0") + " 秒";
    timingButton.Content = label;
   }
  }
  internal void SavePreview(string path) {
   UpdateLayout();
   var image = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth), (int)Math.Ceiling(ActualHeight), 96, 96, PixelFormats.Pbgra32);
   image.Render(this); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
   using (var stream = File.Create(path)) encoder.Save(stream);
  }
  internal async Task SmokeAsync(string folder, Action<bool,string> check) {
   check(Topmost && MinWidth == 440 && MinHeight == 150, "Window: topmost and minimum size");
   check(InputBindings.Count == 11, "Window: keyboard shortcuts registered");
   check(System.Windows.Automation.AutomationProperties.GetName(import).Length > 0, "Window: import has accessible name");
   check(current.Text == "等待 Apple Music", "Window: disconnected state");
   var fake = media as TestMediaSource;
   if (fake == null) throw new InvalidOperationException("Smoke test requires test media source.");
   fake.Sample = TestMediaSource.Connected("界面验证 · 中文与长歌词", 12);
   Refresh(); await pendingSelection; Refresh();
   check(current.Text == "未找到匹配歌词" && import.IsEnabled, "Window: connected without lyrics");
   local.Assign(fake.Sample.Track.Key, LrcParser.Parse("[00:00.00]这是一句用于界面验证的中文歌词\n[00:10.00]在其他窗口工作时，也能读清每一句\n[00:20.00]下一句将在这里安静地出现", "界面测试 · 本地 LRC"));
   await lyrics.SelectAsync(fake.Sample.Track); Refresh();
   check(current.Text.Contains("在其他窗口"), "Window: synchronized current line");
   SavePreview(Path.Combine(folder, "lyrics-preview.png"));
   ResizeLyrics(100); check(current.FontSize == 48, "Window: maximum font size");
   Width = 440; Height = 150; UpdateLayout(); SavePreview(Path.Combine(folder, "lyrics-compact-preview.png"));
   check(((FrameworkElement)Content).ActualWidth > 0 && import.ActualWidth >= 30 && import.IsVisible, "Window: minimum-size layout and import control");
   ResizeLyrics(-100); check(current.FontSize == 16, "Window: minimum font size");
   fake.Sample.Track.IsPlaying = false; fake.Sample.Track.PositionSeconds = 18.4;
   ChangeTiming(0.2, false);
   check(displayedLineIndex == 2 && timingButton.Content.ToString().Contains("提前"), "Window: positive calibration advances the timed line");
   ChangeTiming(0, true);
   check(displayedLineIndex == 1 && timing.Read(fake.Sample.Track.Key) == 1.5 && timingButton.Content.ToString().Contains("1.5"), "Window: reset restores the 1.5-second default advance");
   fake.Sample.TimelineReliable = false; Refresh(); check(current.Text == "时间轴不可用", "Window: missing timeline state");
   fake.Sample.TimelineReliable = true; fake.Sample.Track.IsPlaying = false; Refresh(); check(status.Text == "已暂停", "Window: paused state");
   local.Assign(fake.Sample.Track.Key, LrcParser.Parse("没有时间戳的中文歌词\n第二行完整可读", "文本测试"));
   await lyrics.SelectAsync(fake.Sample.Track); Refresh(); check(status.Text == "仅有非同步歌词", "Window: unsynchronized text is labeled");
   fake.Sample = new MediaSample { Status = "Error", Message = "Test error" }; Refresh(); check(current.Text == "媒体读取失败", "Window: media error state");
   fake.Sample = new MediaSample { ReceivedAtUtc = DateTime.UtcNow.AddSeconds(-5) }; Refresh(); check(current.Text == "媒体连接中断", "Window: stale media state");
   HideWindow(); check(!IsVisible, "Window: hide"); RestoreWindow(); check(IsVisible, "Window: restore");
  }
 }
 public sealed class TestMediaSource : IMediaSource {
  public MediaSample Sample = new MediaSample();
  public MediaSample Read() { return Sample; }
  public void Dispose() {}
  public static MediaSample Connected(string title, double position) {
   return new MediaSample { Status = "Connected", TimelineReliable = true, Track = new TrackSnapshot {
    SourceAppId = "AppleMusic.Test", IsAppleMusic = true, Title = title, Artist = "测试数据", PositionSeconds = position,
    DurationSeconds = 180, IsPlaying = true, CapturedAtUtc = DateTime.UtcNow
   } };
  }
 }
}
