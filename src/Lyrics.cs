using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AMLyrics {
 public sealed class MediaSample {
  public TrackSnapshot Track;
  public bool TimelineReliable;
  public string Status = "Waiting";
  public string Message = "等待 Apple Music";
  public DateTime ReceivedAtUtc = DateTime.UtcNow;
 }
 public interface IMediaSource : IDisposable { MediaSample Read(); }
 public interface IMediaSourceLifecycle {
  bool IsPolling { get; }
  void StartPolling();
  void StopPolling();
  void ResetSample();
 }
 public static class PlaybackMath {
  public static bool IsFresh(MediaSample sample, DateTime now) {
   return sample != null && (now - sample.ReceivedAtUtc).TotalSeconds <= 3 && (now - sample.ReceivedAtUtc).TotalSeconds >= -1;
  }
  public static double Position(MediaSample sample, DateTime now) {
   if (sample == null || sample.Track == null) return 0;
   var t = sample.Track;
   double elapsed = Math.Max(0, (now - t.CapturedAtUtc).TotalSeconds);
   double p = t.PositionSeconds;
   if (sample.TimelineReliable && IsFresh(sample, now) && t.IsPlaying) p += Math.Min(elapsed, 3) * t.PlaybackRate;
   return Math.Max(0, Math.Min(t.DurationSeconds, p));
  }
  public static int FindLine(IList<LyricLine> lines, double position) {
   int lo = 0, hi = lines.Count - 1, found = -1;
   while (lo <= hi) {
    int mid = lo + (hi - lo) / 2;
    if (lines[mid].TimestampSeconds <= position) { found = mid; lo = mid + 1; }
    else hi = mid - 1;
   }
   return found;
  }
 }
 public sealed class LyricTiming {
  public const double DefaultLeadSeconds = 1.5;
  public const double MaxRelativeOffsetSeconds = 15.0;
  public const double MinOffsetSeconds = DefaultLeadSeconds - MaxRelativeOffsetSeconds;
  public const double MaxOffsetSeconds = DefaultLeadSeconds + MaxRelativeOffsetSeconds;
  Dictionary<string,double> offsets = new Dictionary<string,double>();
  public double Read(string key) { double value; return key != null && offsets.TryGetValue(key, out value) ? value : DefaultLeadSeconds; }
  public double ReadRelative(string key) { return Math.Round(Read(key) - DefaultLeadSeconds, 1); }
  public double Adjust(string key, double delta) {
   double previous = Read(key);
   if (string.IsNullOrWhiteSpace(key) || !IsFinite(delta)) return previous;
   double value = Math.Round(ClampOffset(previous + delta), 1);
   if (Math.Abs(value - DefaultLeadSeconds) < 0.001) {
    offsets.Remove(key);
    return DefaultLeadSeconds;
   }
   offsets[key] = value; return value;
  }
  public void Reset(string key) { if (key != null) offsets.Remove(key); }
  public Dictionary<string,double> ReadOffsets() { return new Dictionary<string,double>(offsets); }
  public void LoadOffsets(IDictionary<string,double> values) { offsets = NormalizeOffsets(values); }
  internal static Dictionary<string,double> NormalizeOffsets(IDictionary<string,double> values) {
   var normalized = new Dictionary<string,double>();
   if (values == null) return normalized;
   foreach (var entry in values) {
    if (!string.IsNullOrWhiteSpace(entry.Key) && IsFinite(entry.Value)) normalized[entry.Key] = ClampOffset(entry.Value);
   }
   return normalized;
  }
  static bool IsFinite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
  static double ClampOffset(double value) { return Math.Max(MinOffsetSeconds, Math.Min(MaxOffsetSeconds, value)); }
  public static double Position(MediaSample sample, DateTime now, double offset) {
   // Positive offset means show each lyric earlier; negative allows a delayed first line/prelude.
   return PlaybackMath.Position(sample, now) + ClampOffset(offset);
  }
 }
 public static class LrcParser {
  static readonly Regex Stamp = new Regex(@"\[(\d{1,4}):([0-5]?\d)(?:[\.:](\d{1,3}))?\]", RegexOptions.Compiled);
  static readonly Regex WordStamp = new Regex(@"<\d{1,4}:[0-5]?\d(?:[\.:]\d{1,3})?>", RegexOptions.Compiled);
  static readonly Regex Offset = new Regex(@"\[offset:([+-]?\d+)\]", RegexOptions.IgnoreCase);
  static readonly Regex Metadata = new Regex(@"^\[(ar|al|ti|au|by|re|ve|length|offset):.*\]$", RegexOptions.IgnoreCase);
  public static LyricDocument Parse(string text, string source) {
   if (text == null) throw new ArgumentNullException("text");
   var document = new LyricDocument { Source = source };
   var plain = new List<string>();
   double offset = 0;
   foreach (Match match in Offset.Matches(text)) { long value; if (long.TryParse(match.Groups[1].Value, out value)) offset = value / 1000.0; }
   foreach (string original in text.Replace("\r", "").Split('\n')) {
    string line = original.Trim().TrimStart('\uFEFF');
    var stamps = new List<Match>();
    int end = 0;
    foreach (Match match in Stamp.Matches(line)) {
     if (match.Index != end) break;
     stamps.Add(match); end = match.Index + match.Length;
    }
    if (stamps.Count == 0) {
     if (line.Length > 0 && !Metadata.IsMatch(line)) {
      string cleaned = WordStamp.Replace(line, "").Trim();
      if (cleaned.Length > 0) plain.Add(cleaned);
     }
     continue;
    }
    string words = WordStamp.Replace(line.Substring(end), "").Trim();
    foreach (Match stamp in stamps) {
     double minutes = double.Parse(stamp.Groups[1].Value, CultureInfo.InvariantCulture);
     double seconds = double.Parse(stamp.Groups[2].Value, CultureInfo.InvariantCulture);
     string fraction = stamp.Groups[3].Value;
     double f = fraction.Length == 0 ? 0 : double.Parse(fraction, CultureInfo.InvariantCulture) / Math.Pow(10, fraction.Length);
     document.Lines.Add(new LyricLine(Math.Max(0, minutes * 60 + seconds + f - offset), words));
    }
   }
   if (document.Lines.Count > 0) {
    // Same timestamp may represent a translation/romanization, or an occasional overlapping backing-vocal line in monolingual LRCs.
    var groups = document.Lines.OrderBy(l => l.TimestampSeconds)
     .GroupBy(l => l.TimestampSeconds)
     .Select(g => {
      var parts = g.Select(l => l.Text).Distinct().ToList();
      var nonEmpty = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
      return new { Key = g.Key, Parts = parts, NonEmpty = nonEmpty };
     }).ToList();

    int totalNonEmptyGroups = groups.Count(g => g.NonEmpty.Count >= 1);
    int multiLineGroups = groups.Count(g => g.NonEmpty.Count >= 2);
    bool treatAsMultiTrack =
     (totalNonEmptyGroups <= 4 && multiLineGroups >= 1) ||
     (multiLineGroups >= 3 && (double)multiLineGroups / Math.Max(1, totalNonEmptyGroups) >= 0.25);

    document.Lines = groups.Select(g => {
     string joinedText = treatAsMultiTrack
      ? string.Join("\n", g.Parts)
      : (g.Parts.Count > 1 ? string.Join(" / ", g.NonEmpty) : string.Join("\n", g.Parts));
     var item = new LyricLine(g.Key, joinedText);
     if (treatAsMultiTrack) {
      if (g.NonEmpty.Count == 2) item.Translation = g.NonEmpty[1];
      else if (g.NonEmpty.Count >= 3) { item.Romanization = g.NonEmpty[1]; item.Translation = g.NonEmpty[2]; }
     }
     return item;
    }).ToList();
    document.IsSynchronized = true;
   } else {
    document.Lines = plain.Select(s => new LyricLine(0, s)).ToList();
    document.IsSynchronized = false;
   }
   return document;
  }
 }
 public sealed class LocalLyricsProvider : ILyricsProvider {
  readonly Dictionary<string, LyricDocument> documents = new Dictionary<string, LyricDocument>();
  public void Assign(string key, LyricDocument document) { lock (documents) documents[key] = document; }
  public Task<LyricDocument> GetAsync(TrackSnapshot track, CancellationToken token) {
   token.ThrowIfCancellationRequested();
   LyricDocument document;
   lock (documents) documents.TryGetValue(track.Key, out document);
   return Task.FromResult(document);
  }
  public static async Task<LyricDocument> ReadFileAsync(string path, CancellationToken token) {
   return await Task.Run(() => {
    token.ThrowIfCancellationRequested();
    var info = new FileInfo(path);
    if (info.Length > 2 * 1024 * 1024) throw new InvalidDataException("歌词文件超过 2 MB，请选择较小文件。");
    // Strict UTF-8 avoids silently corrupting Chinese legacy-encoded files.
    string text;
    using (var reader = new StreamReader(path, new UTF8Encoding(false, true), true)) text = reader.ReadToEnd();
    token.ThrowIfCancellationRequested();
    return LrcParser.Parse(text, "本地 · " + Path.GetFileName(path));
   }, token).ConfigureAwait(false);
  }
 }
 // Provider order is explicit. A user-selected local override precedes authorized automatic lookup.
 public sealed class LyricsProviders : ILyricsProvider {
  readonly ILyricsProvider[] providers;
  public LyricsProviders(params ILyricsProvider[] providers) { this.providers = providers; }
  public async Task<LyricDocument> GetAsync(TrackSnapshot track, CancellationToken token) {
   foreach (var provider in providers) {
    token.ThrowIfCancellationRequested();
    var document = await provider.GetAsync(track, token).ConfigureAwait(false);
    token.ThrowIfCancellationRequested();
    if (document != null && document.Lines.Count > 0) return document;
   }
   return null;
  }
 }
 public sealed class LyricState {
  public string Key = "";
  public LyricDocument Document;
  public bool Loading;
  public string Error = "";
 }
 public sealed class LyricSession : IDisposable {
  readonly object gate = new object();
  readonly ILyricsProvider provider;
  CancellationTokenSource request;
  int generation;
  LyricState state = new LyricState();
  public LyricSession(ILyricsProvider provider) { this.provider = provider; }
  public LyricState Read() { lock (gate) return state; }
  public void Clear() {
   lock (gate) { generation++; if (request != null) request.Cancel(); state = new LyricState(); }
  }
  public void ReplaceDocument(string trackKey, LyricDocument document) {
   lock (gate) {
    generation++;
    if (request != null) request.Cancel();
    state = new LyricState { Key = trackKey ?? "", Document = document };
   }
  }
  public async Task SelectAsync(TrackSnapshot track) {
   CancellationTokenSource current;
   int version;
   lock (gate) {
    generation++; version = generation;
    if (request != null) request.Cancel();
    current = new CancellationTokenSource(); request = current;
    state = new LyricState { Key = track.Key, Loading = true };
   }
   try {
    var document = await provider.GetAsync(track, current.Token).ConfigureAwait(false);
    lock (gate) {
     if (generation == version && !current.IsCancellationRequested)
      state = new LyricState { Key = track.Key, Document = document };
    }
   } catch (OperationCanceledException) {
   } catch (Exception ex) {
    lock (gate) if (generation == version) state = new LyricState { Key = track.Key, Error = ex.Message };
   } finally {
    lock (gate) { if (request == current) request = null; current.Dispose(); }
   }
  }
  public void Dispose() { Clear(); }
 }
}
