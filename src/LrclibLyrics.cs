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
   return Regex.Replace(ChineseScript(text ?? "", false).Normalize(NormalizationForm.FormKC), @"[\p{P}\p{Z}\s]", "").ToUpperInvariant();
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
 public sealed class LrclibLyricsProvider : ILyricsProvider, IDisposable {
  sealed class Cached { public LyricDocument Document; public DateTime Expires; }
  readonly HttpClient client;
  readonly object gate = new object();
  readonly Dictionary<string,Cached> cache = new Dictionary<string,Cached>();
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
  public static LrclibRecord SelectRecord(IEnumerable<LrclibRecord> candidates, LyricsMetadata metadata) {
   return candidates.Where(r => r != null && !r.instrumental &&
      LyricsMetadata.Normalize(r.trackName) == LyricsMetadata.Normalize(metadata.Title) &&
      LyricsMetadata.NormalizeArtist(r.artistName) == LyricsMetadata.NormalizeArtist(metadata.Artist) &&
      metadata.Duration > 0 && !double.IsNaN(r.duration) && Math.Abs(r.duration - metadata.Duration) <= 3 &&
      (!string.IsNullOrWhiteSpace(r.syncedLyrics) || !string.IsNullOrWhiteSpace(r.plainLyrics)))
    .OrderByDescending(r => !string.IsNullOrWhiteSpace(r.syncedLyrics))
    .ThenByDescending(r => metadata.Album.Length > 0 && LyricsMetadata.Normalize(r.albumName) == LyricsMetadata.Normalize(metadata.Album))
    .ThenBy(r => Math.Abs(r.duration - metadata.Duration)).FirstOrDefault();
  }
  public async Task<LyricDocument> GetAsync(TrackSnapshot track, CancellationToken token) {
   token.ThrowIfCancellationRequested();
   var metadata = LyricsMetadata.FromTrack(track);
   if (metadata.Title.Length == 0 || metadata.Artist.Length == 0 || metadata.Duration <= 0) return null;
   string key = track.Key + "|" + Math.Round(metadata.Duration).ToString(CultureInfo.InvariantCulture);
   lock (gate) {
    Cached item;
    if (cache.TryGetValue(key, out item) && item.Expires > DateTime.UtcNow) return item.Document;
   }
   var serializer = new JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024 };
   string parameters = "track_name=" + Escape(metadata.Title) + "&artist_name=" + Escape(metadata.Artist);
   LrclibRecord record = null;
   if (metadata.Album.Length > 0) {
    string exact = await FetchAsync("/api/get?" + parameters + "&album_name=" + Escape(metadata.Album) + "&duration=" + Math.Round(metadata.Duration).ToString(CultureInfo.InvariantCulture), token).ConfigureAwait(false);
    if (exact != null) record = SelectRecord(new[] { serializer.Deserialize<LrclibRecord>(exact) }, metadata);
   }
   if (record == null || string.IsNullOrWhiteSpace(record.syncedLyrics)) {
    string search = await FetchAsync("/api/search?" + parameters, token).ConfigureAwait(false);
    if (search != null) record = SelectRecord(serializer.Deserialize<LrclibRecord[]>(search) ?? new LrclibRecord[0], metadata) ?? record;
   }
   if (record == null || string.IsNullOrWhiteSpace(record.syncedLyrics)) {
    string traditional = "track_name=" + Escape(LyricsMetadata.ChineseScript(metadata.Title, true)) + "&artist_name=" + Escape(LyricsMetadata.ChineseScript(metadata.Artist, true));
    if (traditional != parameters) {
     string search = await FetchAsync("/api/search?" + traditional, token).ConfigureAwait(false);
     if (search != null) record = SelectRecord(serializer.Deserialize<LrclibRecord[]>(search) ?? new LrclibRecord[0], metadata) ?? record;
    }
   }
   token.ThrowIfCancellationRequested();
   LyricDocument document = null;
   if (record != null) {
    string text = !string.IsNullOrWhiteSpace(record.syncedLyrics) ? record.syncedLyrics : record.plainLyrics;
    document = LrcParser.Parse(text, "LRCLIB · " + record.trackName + " / " + record.artistName);
    if (document.Lines.Count == 0) document = null;
   }
   lock (gate) {
    if (cache.Count >= 128) cache.Clear();
    cache[key] = new Cached { Document = document, Expires = DateTime.UtcNow.AddMinutes(document == null ? 2 : 720) };
   }
   return document;
  }
  public void Invalidate(TrackSnapshot track) {
   string key = track.Key + "|" + Math.Round(track.DurationSeconds).ToString(CultureInfo.InvariantCulture);
   lock (gate) cache.Remove(key);
  }
  public void Dispose() { client.Dispose(); }
 }
}
