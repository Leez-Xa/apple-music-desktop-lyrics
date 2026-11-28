using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace AMLyrics {
 public sealed class LyricsMetadata {
  public string Title, Artist, Album;
  public double Duration;
  public static LyricsMetadata FromTrack(TrackSnapshot track) {
   string artist = track.Artist ?? "", album = track.Album ?? "";
   // Apple Music for Windows publishes "Artist — Album" in the Artist property on this machine.
   int separator = artist.LastIndexOf(" — ", StringComparison.Ordinal);
   if (album.Length == 0 && separator > 0) { album = artist.Substring(separator + 3).Trim(); artist = artist.Substring(0, separator).Trim(); }
   return new LyricsMetadata { Title = (track.Title ?? "").Trim(), Artist = artist.Trim(), Album = album.Trim(), Duration = track.DurationSeconds };
  }
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
  static extern int LCMapStringEx(string locale, uint flags, string source, int sourceLength, StringBuilder destination, int capacity, IntPtr version, IntPtr reserved, IntPtr sort);
  public static string ChineseScript(string text, bool traditional) {
   if (string.IsNullOrEmpty(text)) return text ?? "";
   var output = new StringBuilder(text.Length * 3 + 8);
   int size = LCMapStringEx("zh-CN", traditional ? 0x04000000u : 0x02000000u, text, -1, output, output.Capacity, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
   return size > 0 ? output.ToString() : text;
  }
  public static string NormalizeArtist(string value) {
   string artist = ChineseScript(value, false);
   // Remove a romanized alias after a CJK artist name, preserving collaboration names.
   if (Regex.IsMatch(artist, @"[\u3400-\u9FFF]")) {
    artist = Regex.Replace(artist, @"\s*[（(](?!feat\b|with\b)[A-Za-z][A-Za-z .\-]*[)）]\s*$", "", RegexOptions.IgnoreCase);
   }
   return Normalize(artist);
  }
  public static string Normalize(string text) {
   return Regex.Replace(ChineseScript(text ?? "", false).Normalize(NormalizationForm.FormKC), @"[\p{P}\p{Z}\p{Cf}\s]", "").ToUpperInvariant();
  }
 }
 public sealed class LrclibRecord {
  public int id { get; set; }
  public string trackName { get; set; }
  public string artistName { get; set; }
  public string albumName { get; set; }
  public double duration { get; set; }
  public bool instrumental { get; set; }
  public string syncedLyrics { get; set; }
  public string plainLyrics { get; set; }
 }
 public sealed class LrclibCandidateState {
  public int CurrentIndex;
  public int TotalCount;
  public int CurrentRecordId;
  public bool HasSearchedAll;
 }
 public sealed class LrclibLyricsProvider : ILyricsProvider, IDisposable {
  sealed class Cached {
   public LyricDocument Document;
   public DateTime Expires;
   public List<LrclibRecord> Candidates = new List<LrclibRecord>();
   public int SelectedIndex;
   public bool SearchedAll;
  }
  public const int MaxCandidates = 3;
  static readonly Regex FractionalStampLine = new Regex(@"^\s*\[\d{1,4}:[0-5]?\d[\.:]\d{1,3}\]", RegexOptions.Compiled);
  readonly HttpClient client;
  readonly object gate = new object();
  readonly Dictionary<string,Cached> cache = new Dictionary<string,Cached>();
  readonly Dictionary<string,int> preferredIds = new Dictionary<string,int>();
  static readonly object rateGate = new object();
  static readonly Random backoffRandom = new Random();
  static readonly SemaphoreSlim requests = new SemaphoreSlim(1,1);
  static DateTime blockedUntil = DateTime.MinValue, lastRequest = DateTime.MinValue;
  public LrclibLyricsProvider() : this(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false }) {}
  public LrclibLyricsProvider(HttpMessageHandler handler) {
   ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
   client = new HttpClient(handler, true) { Timeout = TimeSpan.FromSeconds(12), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
   client.DefaultRequestHeaders.UserAgent.ParseAdd("AMDesktopLyrics/0.2 (personal Windows client; https://lrclib.net/docs)");
   client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
  }
  static string Escape(string text) { return Uri.EscapeDataString(text ?? ""); }
  async Task<string> FetchAsync(string relative, CancellationToken token) {
   await requests.WaitAsync(token).ConfigureAwait(false);
   try {
    DateTime until;
    lock (rateGate) until = blockedUntil;
    if (DateTime.UtcNow < until) throw new InvalidOperationException("歌词服务暂时限流，请稍后重试。");
    double delay;
    lock (rateGate) delay = 400 - (DateTime.UtcNow - lastRequest).TotalMilliseconds;
    if (delay > 0) await Task.Delay((int)Math.Ceiling(delay), token).ConfigureAwait(false);
    lock (rateGate) lastRequest = DateTime.UtcNow;
    using (var response = await client.GetAsync("https://lrclib.net" + relative, token).ConfigureAwait(false)) {
     if ((int)response.StatusCode == 429) {
      double seconds = 60;
      var retry = response.Headers.RetryAfter;
      if (retry != null && retry.Delta.HasValue) seconds = Math.Max(1, retry.Delta.Value.TotalSeconds);
      else if (retry != null && retry.Date.HasValue) seconds = Math.Max(1, (retry.Date.Value.UtcDateTime - DateTime.UtcNow).TotalSeconds);
      lock (rateGate) {
       DateTime now = DateTime.UtcNow;
       double wait = seconds + Math.Max(1, seconds * backoffRandom.NextDouble() * 0.2);
       blockedUntil = now.AddSeconds(Math.Min(wait, (DateTime.MaxValue - now).TotalSeconds - 1));
      }
      throw new InvalidOperationException("歌词服务暂时限流，稍后会自动重试。");
     }
     if (response.StatusCode == HttpStatusCode.NotFound) return null;
     if (!response.IsSuccessStatusCode) throw new HttpRequestException("歌词服务返回错误（" + (int)response.StatusCode + "），稍后重试。");
     return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
    }
   } catch (OperationCanceledException) {
    token.ThrowIfCancellationRequested();
    throw new TimeoutException("歌词查询超时，将稍后重试。");
   } catch (HttpRequestException ex) {
    throw new HttpRequestException("暂时连接不上歌词服务，请检查网络或稍后重试。", ex);
   } finally { requests.Release(); }
  }
  sealed class CandidateEntry {
   public LrclibRecord Record;
   public bool IsSynced;
   public int TimedLines;
   public bool HasFractional;
   public string Fingerprint;
  }
  public static List<LrclibRecord> SelectCandidates(IEnumerable<LrclibRecord> candidates, LyricsMetadata metadata) {
   var result = new List<LrclibRecord>();
   if (candidates == null || metadata == null) return result;
   string targetTitle = LyricsMetadata.Normalize(metadata.Title);
   string targetArtist = LyricsMetadata.NormalizeArtist(metadata.Artist);
   string targetAlbum = LyricsMetadata.Normalize(metadata.Album);
   var entries = new List<CandidateEntry>();
   foreach (var r in candidates) {
    if (r == null || r.instrumental ||
     LyricsMetadata.Normalize(r.trackName) != targetTitle ||
     LyricsMetadata.NormalizeArtist(r.artistName) != targetArtist ||
     metadata.Duration <= 0 || double.IsNaN(r.duration) || Math.Abs(r.duration - metadata.Duration) > 3 ||
     (string.IsNullOrWhiteSpace(r.syncedLyrics) && string.IsNullOrWhiteSpace(r.plainLyrics))) continue;
    string raw = !string.IsNullOrWhiteSpace(r.syncedLyrics) ? r.syncedLyrics : (r.plainLyrics ?? "");
    var parsed = LrcParser.Parse(raw, "");
    if (parsed == null || parsed.Lines == null || parsed.Lines.Count == 0) continue;
    bool isSynced = !string.IsNullOrWhiteSpace(r.syncedLyrics) && parsed.IsSynchronized;
    entries.Add(new CandidateEntry {
     Record = r,
     IsSynced = isSynced,
     TimedLines = isSynced ? parsed.Lines.Count : 0,
     HasFractional = isSynced && HasFractionalTimestamps(r.syncedLyrics),
     Fingerprint = BuildLyricsFingerprint(parsed)
    });
   }
   int bestSyncedLines = entries.Where(e => e.IsSynced).Select(e => e.TimedLines).DefaultIfEmpty(0).Max();
   int minSyncedLines = bestSyncedLines >= 8 ? Math.Max(4, bestSyncedLines / 4) : 1;
   var ordered = entries
    .Where(e => bestSyncedLines > 0 ? (e.IsSynced && e.TimedLines >= minSyncedLines) : true)
    .OrderByDescending(e => e.IsSynced)
    .ThenByDescending(e => metadata.Album.Length > 0 && LyricsMetadata.Normalize(e.Record.albumName) == targetAlbum)
    .ThenBy(e => Math.Abs(e.Record.duration - metadata.Duration))
    .ThenByDescending(e => e.TimedLines)
    .ThenByDescending(e => e.HasFractional)
    .ThenBy(e => e.Record.id > 0 ? e.Record.id : int.MaxValue);
   var seenIds = new HashSet<string>(StringComparer.Ordinal);
   var seenContent = new HashSet<string>(StringComparer.Ordinal);
   foreach (var entry in ordered) {
    var record = entry.Record;
    string dedupeKey = record.id > 0
     ? "ID:" + record.id.ToString(CultureInfo.InvariantCulture)
     : "RAW:" + (record.albumName ?? "") + "|" + Math.Round(record.duration, 2).ToString(CultureInfo.InvariantCulture) + "|" + (record.syncedLyrics ?? "") + "|" + (record.plainLyrics ?? "");
    if (!seenIds.Add(dedupeKey)) continue;
    if (entry.Fingerprint.Length > 0 && !seenContent.Add(entry.Fingerprint)) continue;
    result.Add(record);
    if (result.Count >= MaxCandidates) break;
   }
   return result;
  }
  public static LrclibRecord SelectRecord(IEnumerable<LrclibRecord> candidates, LyricsMetadata metadata) {
   return SelectCandidates(candidates, metadata).FirstOrDefault();
  }
  static bool HasFractionalTimestamps(string syncedLyrics) {
   if (string.IsNullOrWhiteSpace(syncedLyrics)) return false;
   foreach (string line in syncedLyrics.Replace("\r", "").Split('\n')) {
    if (FractionalStampLine.IsMatch(line)) return true;
   }
   return false;
  }
  static string BuildLyricsFingerprint(LyricDocument doc) {
   if (doc == null || doc.Lines == null || doc.Lines.Count == 0) return "";
   var parts = new List<string>(doc.Lines.Count);
   foreach (var line in doc.Lines) {
    if (line == null) continue;
    string text = (line.Text ?? "").Trim();
    string trans = (line.Translation ?? "").Trim();
    string rom = (line.Romanization ?? "").Trim();
    if (doc.IsSynchronized) {
     parts.Add(Math.Round(line.TimestampSeconds, 2).ToString("F2", CultureInfo.InvariantCulture) + "|" + text + "|" + trans + "|" + rom);
    } else if (text.Length > 0) {
     parts.Add(text);
    }
   }
   return string.Join("\n", parts);
  }
  static List<LrclibRecord> FilterSwitchableCandidates(List<LrclibRecord> ordered) {
   if (ordered == null || ordered.Count == 0) return new List<LrclibRecord>();
   bool hasSynced = ordered.Any(r => r != null && !string.IsNullOrWhiteSpace(r.syncedLyrics));
   if (!hasSynced) return new List<LrclibRecord>(ordered);
   return ordered.Where(r => r != null && !string.IsNullOrWhiteSpace(r.syncedLyrics)).ToList();
  }
  static string BuildCacheKey(TrackSnapshot track, double duration) {
   return (track == null ? "" : track.Key) + "|" + Math.Round(duration).ToString(CultureInfo.InvariantCulture);
  }
  static LyricDocument BuildDocument(LrclibRecord record, int index, int totalCount) {
   if (record == null) return null;
   string text = !string.IsNullOrWhiteSpace(record.syncedLyrics) ? record.syncedLyrics : record.plainLyrics;
   string source = "LRCLIB · " + record.trackName + " / " + record.artistName;
   if (totalCount > 1) source += string.Format(CultureInfo.InvariantCulture, "（版本 {0}/{1}）", index + 1, totalCount);
   var document = LrcParser.Parse(text, source);
   return document.Lines.Count == 0 ? null : document;
  }
  static bool RecordsEqual(LrclibRecord a, LrclibRecord b) {
   if (ReferenceEquals(a, b)) return true;
   if (a == null || b == null) return false;
   if (a.id > 0 && b.id > 0) return a.id == b.id;
   return string.Equals(a.trackName, b.trackName, StringComparison.Ordinal) &&
    string.Equals(a.artistName, b.artistName, StringComparison.Ordinal) &&
    string.Equals(a.albumName, b.albumName, StringComparison.Ordinal) &&
    Math.Abs(a.duration - b.duration) < 0.01 &&
    string.Equals(a.syncedLyrics, b.syncedLyrics, StringComparison.Ordinal) &&
    string.Equals(a.plainLyrics, b.plainLyrics, StringComparison.Ordinal);
  }
  public void LoadPreferredIds(IDictionary<string,int> values) {
   lock (gate) {
    preferredIds.Clear();
    if (values == null) return;
    foreach (var entry in values) {
     if (!string.IsNullOrWhiteSpace(entry.Key) && entry.Value > 0) preferredIds[entry.Key] = entry.Value;
    }
   }
  }
  public Dictionary<string,int> ReadPreferredIds() {
   lock (gate) return new Dictionary<string,int>(preferredIds);
  }
  public void SetPreferredId(string trackKey, int recordId) {
   if (string.IsNullOrWhiteSpace(trackKey)) return;
   lock (gate) {
    if (recordId > 0) preferredIds[trackKey] = recordId;
    else preferredIds.Remove(trackKey);
   }
  }
  public void ClearPreferredId(string trackKey) {
   if (string.IsNullOrWhiteSpace(trackKey)) return;
   lock (gate) preferredIds.Remove(trackKey);
  }
  public LrclibCandidateState ReadCandidateState(TrackSnapshot track) {
   if (track == null) return new LrclibCandidateState();
   var metadata = LyricsMetadata.FromTrack(track);
   if (metadata.Duration <= 0) return new LrclibCandidateState();
   string key = BuildCacheKey(track, metadata.Duration);
   lock (gate) {
    Cached item;
    if (!cache.TryGetValue(key, out item) || item.Expires <= DateTime.UtcNow) return new LrclibCandidateState();
    int total = item.Candidates == null ? 0 : item.Candidates.Count;
    int index = total > 0 && item.SelectedIndex >= 0 && item.SelectedIndex < total ? item.SelectedIndex : 0;
    int recordId = total > 0 && item.Candidates[index] != null ? item.Candidates[index].id : 0;
    return new LrclibCandidateState {
     CurrentIndex = index,
     TotalCount = total,
     CurrentRecordId = recordId,
     HasSearchedAll = item.SearchedAll
    };
   }
  }
  async Task<List<LrclibRecord>> SearchAllCandidatesAsync(LyricsMetadata metadata, IEnumerable<LrclibRecord> existing, CancellationToken token, int preferredId = 0) {
   var serializer = new JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024 };
   var collected = new List<LrclibRecord>();
   if (existing != null) collected.AddRange(existing);
   string parameters = "track_name=" + Escape(metadata.Title) + "&artist_name=" + Escape(metadata.Artist);
   string search = await FetchAsync("/api/search?" + parameters, token).ConfigureAwait(false);
   if (search != null) collected.AddRange(serializer.Deserialize<LrclibRecord[]>(search) ?? new LrclibRecord[0]);
   var primary = FilterSwitchableCandidates(SelectCandidates(collected, metadata));
   bool hasEnoughSynced = primary.Count >= MaxCandidates &&
    primary.All(r => r != null && !string.IsNullOrWhiteSpace(r.syncedLyrics)) &&
    (preferredId <= 0 || primary.Any(r => r != null && r.id == preferredId));
   if (hasEnoughSynced) return primary;
   string traditional = "track_name=" + Escape(LyricsMetadata.ChineseScript(metadata.Title, true)) + "&artist_name=" + Escape(LyricsMetadata.ChineseScript(metadata.Artist, true));
   if (traditional != parameters) {
    string tradSearch = await FetchAsync("/api/search?" + traditional, token).ConfigureAwait(false);
    if (tradSearch != null) collected.AddRange(serializer.Deserialize<LrclibRecord[]>(tradSearch) ?? new LrclibRecord[0]);
   }
   return FilterSwitchableCandidates(SelectCandidates(collected, metadata));
  }
  public async Task<LyricDocument> GetAsync(TrackSnapshot track, CancellationToken token) {
   token.ThrowIfCancellationRequested();
   var metadata = LyricsMetadata.FromTrack(track);
   if (metadata.Title.Length == 0 || metadata.Artist.Length == 0 || metadata.Duration <= 0) return null;
   string key = BuildCacheKey(track, metadata.Duration);
   int preferredId = 0;
   lock (gate) {
    Cached item;
    if (cache.TryGetValue(key, out item) && item.Expires > DateTime.UtcNow) return item.Document;
    preferredIds.TryGetValue(track.Key, out preferredId);
   }
   var serializer = new JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024 };
   var candidates = new List<LrclibRecord>();
   bool searchedAll = false;
   if (preferredId <= 0 && metadata.Album.Length > 0) {
    string parameters = "track_name=" + Escape(metadata.Title) + "&artist_name=" + Escape(metadata.Artist);
    string exact = await FetchAsync("/api/get?" + parameters + "&album_name=" + Escape(metadata.Album) + "&duration=" + Math.Round(metadata.Duration).ToString(CultureInfo.InvariantCulture), token).ConfigureAwait(false);
    if (exact != null) {
     var exactRecord = SelectRecord(new[] { serializer.Deserialize<LrclibRecord>(exact) }, metadata);
     if (exactRecord != null) candidates.Add(exactRecord);
    }
   }
   if (preferredId > 0 || candidates.Count == 0 || string.IsNullOrWhiteSpace(candidates[0].syncedLyrics)) {
    candidates = await SearchAllCandidatesAsync(metadata, candidates, token, preferredId).ConfigureAwait(false);
    searchedAll = true;
   }
   token.ThrowIfCancellationRequested();
   int selectedIndex = 0;
   if (preferredId > 0 && candidates.Count > 0) {
    int foundIndex = candidates.FindIndex(r => r != null && r.id == preferredId);
    if (foundIndex >= 0) selectedIndex = foundIndex;
   }
   LyricDocument document = candidates.Count > 0 ? BuildDocument(candidates[selectedIndex], selectedIndex, candidates.Count) : null;
   if (document == null) {
    candidates = new List<LrclibRecord>();
    selectedIndex = 0;
   }
   lock (gate) {
    if (cache.Count >= 128) cache.Clear();
    cache[key] = new Cached {
     Document = document,
     Expires = DateTime.UtcNow.AddMinutes(document == null ? 2 : 720),
     Candidates = candidates,
     SelectedIndex = selectedIndex,
     SearchedAll = searchedAll
    };
   }
   return document;
  }
  public async Task<LyricDocument> SwitchCandidateAsync(TrackSnapshot track, CancellationToken token) {
   token.ThrowIfCancellationRequested();
   if (track == null) return null;
   var metadata = LyricsMetadata.FromTrack(track);
   if (metadata.Title.Length == 0 || metadata.Artist.Length == 0 || metadata.Duration <= 0) return null;
   string key = BuildCacheKey(track, metadata.Duration);
   List<LrclibRecord> existingCandidates = null;
   int currentIndex = 0;
   bool searchedAll = false;
   LyricDocument currentDocument = null;
   lock (gate) {
    Cached item;
    if (cache.TryGetValue(key, out item) && item.Expires > DateTime.UtcNow) {
     existingCandidates = item.Candidates != null ? new List<LrclibRecord>(item.Candidates) : new List<LrclibRecord>();
     currentIndex = item.SelectedIndex;
     searchedAll = item.SearchedAll;
     currentDocument = item.Document;
    }
   }
   LrclibRecord currentRecord = existingCandidates != null && currentIndex >= 0 && currentIndex < existingCandidates.Count
    ? existingCandidates[currentIndex]
    : null;
   List<LrclibRecord> candidates = existingCandidates ?? new List<LrclibRecord>();
   bool matchedExisting = true;
   if (!searchedAll) {
    candidates = await SearchAllCandidatesAsync(metadata, existingCandidates, token).ConfigureAwait(false);
    searchedAll = true;
    if (currentRecord != null && candidates.Count > 0) {
     int matched = candidates.FindIndex(r => RecordsEqual(r, currentRecord));
     matchedExisting = matched >= 0;
     currentIndex = matchedExisting ? matched : 0;
    } else {
     matchedExisting = false;
     currentIndex = 0;
    }
   }
   token.ThrowIfCancellationRequested();
   if (candidates.Count == 0) {
    lock (gate) {
     if (cache.Count >= 128) cache.Clear();
     cache[key] = new Cached {
      Document = null,
      Expires = DateTime.UtcNow.AddMinutes(2),
      Candidates = candidates,
      SelectedIndex = 0,
      SearchedAll = true
     };
    }
    return null;
   }
   int nextIndex = !matchedExisting ? 0 : (candidates.Count > 1 ? (currentIndex + 1) % candidates.Count : 0);
   LrclibRecord nextRecord = candidates[nextIndex];
   LyricDocument nextDoc = BuildDocument(nextRecord, nextIndex, candidates.Count) ?? currentDocument;
   lock (gate) {
    if (candidates.Count > 1 && nextRecord != null && nextRecord.id > 0) {
     preferredIds[track.Key] = nextRecord.id;
    }
    if (cache.Count >= 128) cache.Clear();
    cache[key] = new Cached {
     Document = nextDoc,
     Expires = DateTime.UtcNow.AddMinutes(nextDoc == null ? 2 : 720),
     Candidates = candidates,
     SelectedIndex = nextIndex,
     SearchedAll = searchedAll
    };
   }
   return nextDoc;
  }
  public void Invalidate(TrackSnapshot track) {
   string key = BuildCacheKey(track, track.DurationSeconds);
   lock (gate) cache.Remove(key);
  }
  public void Dispose() { client.Dispose(); }
 }
}
