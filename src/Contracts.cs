using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
namespace AMLyrics {
 public sealed class TrackSnapshot {
  public string Id = "";
  public string Title = "";
  public string Artist = "";
  public string Album = "";
  public string SourceAppId = "";
  public double PositionSeconds;
  public double DurationSeconds;
  public double PlaybackRate = 1;
  public DateTime CapturedAtUtc = DateTime.UtcNow;
  public bool IsPlaying;
  public bool IsAppleMusic;
  public string Key { get { return SourceAppId + "|" + Title + "|" + Artist + "|" + Album; } }
 }
 public sealed class LyricLine {
  public double TimestampSeconds;
  public string Text;
  public LyricLine(double seconds, string text) { TimestampSeconds = seconds; Text = text; }
 }
 public sealed class LyricDocument {
  public List<LyricLine> Lines = new List<LyricLine>();
  public string Source = "";
  public bool IsSynchronized;
 }
 public interface ILyricsProvider {
  Task<LyricDocument> GetAsync(TrackSnapshot track, CancellationToken cancellationToken);
 }
}
