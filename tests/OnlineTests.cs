using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using AMLyrics;
class OnlineTests {
 static int failures, count;
 static void Check(bool ok, string name) { count++; Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failures++; }
 static int Main() { try { Run().GetAwaiter().GetResult(); } catch (Exception ex) { Console.WriteLine(ex); return 2; } Console.WriteLine(count + " online checks; " + failures + " failed."); return failures == 0 ? 0 : 1; }
 static TrackSnapshot Track() { return new TrackSnapshot { Title = "Original Song", Artist = "Original Artist — Original Album", DurationSeconds = 100 }; }
 static LrclibRecord Record() { return new LrclibRecord { id = 1, trackName = "Original Song", artistName = "Original Artist", albumName = "Original Album", duration = 100, syncedLyrics = "[00:00]Original test line one\n[00:05]Original test line two" }; }
 static HttpResponseMessage Json(object value) { return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new JavaScriptSerializer().Serialize(value), System.Text.Encoding.UTF8, "application/json") }; }
 static async Task Run() {
  var metadata = LyricsMetadata.FromTrack(Track());
  Check(metadata.Artist == "Original Artist" && metadata.Album == "Original Album", "Metadata: splits Apple Music combined artist and album");
  var track = Track(); track.Album = "Explicit Album";
  Check(LyricsMetadata.FromTrack(track).Artist == track.Artist, "Metadata: preserves explicit album metadata");
  Check(LyricsMetadata.ChineseScript("孫燕姿", false) == "孙燕姿", "Chinese: traditional to simplified without invalid Unicode");
  Check(LyricsMetadata.ChineseScript("我怀念的", true) == "我懷念的", "Chinese: simplified to traditional");
  Check(LyricsMetadata.Normalize("Ａ Song！") == LyricsMetadata.Normalize("a song"), "Matching: width, punctuation and case normalize");
  Check(LyricsMetadata.NormalizeArtist("孫燕姿 (Yanzi Sun)") == LyricsMetadata.NormalizeArtist("孙燕姿"), "Matching: Chinese romanized artist alias");
  var wrong = Record(); wrong.duration = 130;
  Check(LrclibLyricsProvider.SelectRecord(new[] { wrong }, metadata) == null, "Matching: wrong-duration live version rejected");
  wrong = Record(); wrong.artistName = "Cover Artist";
  Check(LrclibLyricsProvider.SelectRecord(new[] { wrong }, metadata) == null, "Matching: cover artist rejected");
  wrong = Record(); wrong.trackName = "Another Song";
  Check(LrclibLyricsProvider.SelectRecord(new[] { wrong }, metadata) == null, "Matching: wrong title rejected");
  wrong = Record(); wrong.instrumental = true;
  Check(LrclibLyricsProvider.SelectRecord(new[] { wrong }, metadata) == null, "Matching: instrumental record not fabricated into lyrics");
  var plain = Record(); plain.syncedLyrics = null; plain.plainLyrics = "Original untimed test line";
  var synchronized = Record(); synchronized.albumName = "Alternate Album";
  Check(LrclibLyricsProvider.SelectRecord(new[] { plain, synchronized }, metadata) == synchronized, "Matching: synchronized lyrics preferred to untimed text");
  var exactAlbum = Record();
  Check(LrclibLyricsProvider.SelectRecord(new[] { synchronized, exactAlbum }, metadata) == exactAlbum, "Matching: matching album breaks ties");
  var handler = new Handler((request, token) => {
   Check(request.RequestUri.Scheme == "https" && request.RequestUri.Host == "lrclib.net" && request.Method == HttpMethod.Get, "Privacy: HTTPS GET only to approved LRCLIB host");
   Check(request.Headers.UserAgent.ToString().Contains("AMDesktopLyrics"), "HTTP: identified application user agent");
   Check(!request.Headers.Contains("Authorization") && !request.Headers.Contains("Cookie"), "Privacy: no account, token or cookie headers");
   string query = Uri.UnescapeDataString(request.RequestUri.Query);
   Check(query.Contains("artist_name=Original Artist") && !query.Contains("Original Artist —"), "HTTP: sends normalized artist metadata");
   return Task.FromResult(Json(Record()));
  });
  using (var provider = new LrclibLyricsProvider(handler)) {
   var document = await provider.GetAsync(Track(), CancellationToken.None);
   Check(document.IsSynchronized && document.Lines.Count == 2 && document.Source.StartsWith("LRCLIB"), "Provider: automatic remote LRC parses to real timed document");
   var cached = await provider.GetAsync(Track(), CancellationToken.None);
   Check(cached == document && handler.Count == 1, "Cache: successful track does not request again");
  }
  handler = new Handler((request, token) => Task.FromResult(request.RequestUri.AbsolutePath == "/api/get" ? new HttpResponseMessage(HttpStatusCode.NotFound) : Json(new[] { Record() })));
  using (var provider = new LrclibLyricsProvider(handler)) {
   Check((await provider.GetAsync(Track(), CancellationToken.None)).IsSynchronized && handler.Count == 2, "Provider: exact miss falls back to search");
  }
  handler = new Handler((request, token) => {
   string query = Uri.UnescapeDataString(request.RequestUri.Query);
   if (request.RequestUri.AbsolutePath == "/api/get") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
   if (query.Contains("我懷念的")) return Task.FromResult(Json(new[] { new LrclibRecord { trackName = "我懷念的", artistName = "孫燕姿 (Yanzi Sun)", albumName = "逆光", duration = 289, syncedLyrics = "[00:00]原创测试甲\n[00:02]原创测试乙" } }));
   return Task.FromResult(Json(new LrclibRecord[0]));
  });
  using (var provider = new LrclibLyricsProvider(handler)) {
   var song = new TrackSnapshot { Title = "我怀念的", Artist = "孙燕姿 — 逆光", DurationSeconds = 289 };
   Check((await provider.GetAsync(song, CancellationToken.None)).IsSynchronized && handler.Count == 3, "Provider: simplified miss finds traditional title and artist alias");
  }
  handler = new Handler((request, token) => Task.FromResult(request.RequestUri.AbsolutePath == "/api/get" ? new HttpResponseMessage(HttpStatusCode.NotFound) : Json(new LrclibRecord[0])));
  using (var provider = new LrclibLyricsProvider(handler)) {
   Check(await provider.GetAsync(Track(), CancellationToken.None) == null, "Provider: no match is an explicit empty result");
   int requests = handler.Count; await provider.GetAsync(Track(), CancellationToken.None);
   Check(handler.Count == requests, "Cache: temporary no-match cache prevents repeated searches");
   provider.Invalidate(Track()); await provider.GetAsync(Track(), CancellationToken.None);
   Check(handler.Count > requests, "Cache: explicit retry invalidates a cached miss");
  }
  handler = new Handler((request, token) => Task.FromResult(Json(plain)));
  using (var provider = new LrclibLyricsProvider(handler)) {
   // Return the correct wire shape on each endpoint.
   handler.Callback = (request, token) => Task.FromResult(request.RequestUri.AbsolutePath == "/api/get" ? Json(plain) : Json(new[] { plain }));
   Check(!(await provider.GetAsync(Track(), CancellationToken.None)).IsSynchronized, "Provider: plain lyrics remain explicitly unsynchronized");
  }
  handler = new Handler((request, token) => { throw new TaskCanceledException("timeout"); });
  using (var provider = new LrclibLyricsProvider(handler)) {
   try { await provider.GetAsync(Track(), CancellationToken.None); Check(false, "Timeout: produces recoverable error"); }
   catch (TimeoutException) { Check(true, "Timeout: produces recoverable error"); }
  }
  handler = new Handler(async (request, token) => { await Task.Delay(5000, token); return Json(Record()); });
  using (var provider = new LrclibLyricsProvider(handler)) using (var cancel = new CancellationTokenSource(80)) {
   try { await provider.GetAsync(Track(), cancel.Token); Check(false, "Cancellation: track switch cancels pending HTTP"); }
   catch (OperationCanceledException) { Check(true, "Cancellation: track switch cancels pending HTTP"); }
  }
   var candA = new LrclibRecord { id = 101, trackName = "Original Song", artistName = "Original Artist", albumName = "Original Album", duration = 100, syncedLyrics = "[00:01]Version A" };
   var candB = new LrclibRecord { id = 202, trackName = "Original Song", artistName = "Original Artist", albumName = "Original Album", duration = 100.2, syncedLyrics = "[00:01]Version B\n[00:05]Line two" };
   var candC = new LrclibRecord { id = 303, trackName = "Original Song", artistName = "Original Artist", albumName = "Other Album", duration = 100, syncedLyrics = "[00:01]Version C" };
   var ranked = LrclibLyricsProvider.SelectCandidates(new[] { candC, candA, candB, candB }, metadata);
   Check(ranked.Count == 3 && ranked[0].id == 101 && ranked[1].id == 202 && ranked[2].id == 303,
    "Candidates: deduplicates by id and ranks exact album + closest duration first");
   handler = new Handler((request, token) => Task.FromResult(
    request.RequestUri.AbsolutePath == "/api/get"
     ? Json(candA)
     : Json(new[] { candC, candB, candA })));
   using (var provider = new LrclibLyricsProvider(handler)) {
    var firstDoc = await provider.GetAsync(Track(), CancellationToken.None);
    var stateBefore = provider.ReadCandidateState(Track());
    Check(firstDoc.Lines[0].Text == "Version A" && handler.Count == 1 && !stateBefore.HasSearchedAll && stateBefore.TotalCount == 1,
     "Candidates: initial GetAsync uses single /api/get request");
    var secondDoc = await provider.SwitchCandidateAsync(Track(), CancellationToken.None);
    var stateAfter = provider.ReadCandidateState(Track());
    Check(secondDoc.Lines[0].Text == "Version B" && stateAfter.HasSearchedAll && stateAfter.TotalCount == 3 && stateAfter.CurrentIndex == 1 && secondDoc.Source.Contains("版本 2/3"),
     "Candidates: SwitchCandidateAsync lazily loads /api/search and advances to version 2/3");
    var thirdDoc = await provider.SwitchCandidateAsync(Track(), CancellationToken.None);
    Check(thirdDoc.Lines[0].Text == "Version C" && thirdDoc.Source.Contains("版本 3/3"),
     "Candidates: SwitchCandidateAsync cycles to version 3/3");
    var wrappedDoc = await provider.SwitchCandidateAsync(Track(), CancellationToken.None);
    Check(wrappedDoc.Lines[0].Text == "Version A" && wrappedDoc.Source.Contains("版本 1/3"),
     "Candidates: SwitchCandidateAsync wraps around to version 1/3");
    var savedIds = provider.ReadPreferredIds();
    Check(savedIds.ContainsKey(Track().Key) && savedIds[Track().Key] == 101,
     "Candidates: records preferred record id per track");
    provider.SetPreferredId(Track().Key, 202);
    provider.Invalidate(Track());
    var restoredDoc = await provider.GetAsync(Track(), CancellationToken.None);
    Check(restoredDoc.Lines[0].Text == "Version B" && restoredDoc.Source.Contains("版本 2/3"),
     "Candidates: preferred record id selects chosen version after cache invalidation or restart");
   }
   handler = new Handler((request, token) => Task.FromResult(
    request.RequestUri.AbsolutePath == "/api/get"
     ? Json(candA)
     : Json(new[] { candA })));
   using (var provider = new LrclibLyricsProvider(handler)) {
    await provider.GetAsync(Track(), CancellationToken.None);
    var singleDoc = await provider.SwitchCandidateAsync(Track(), CancellationToken.None);
    var singleState = provider.ReadCandidateState(Track());
    Check(singleDoc != null && singleDoc.Lines[0].Text == "Version A" && singleState.HasSearchedAll && singleState.TotalCount == 1,
     "Candidates: single-candidate track remains stable after SwitchCandidateAsync");
   }
  handler = new Handler((request, token) => {
   var response = new HttpResponseMessage((HttpStatusCode)429);
   response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
   return Task.FromResult(response);
  });
  using (var provider = new LrclibLyricsProvider(handler)) {
   for (int attempt = 0; attempt < 2; attempt++) {
    try { await provider.GetAsync(Track(), CancellationToken.None); Check(false, "Rate limit: enforced"); }
    catch (InvalidOperationException) { Check(true, "Rate limit: enforced"); }
   }
   Check(handler.Count == 1, "Rate limit: Retry-After blocks a second request");
  }
 }
 class Handler : HttpMessageHandler {
  public Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> Callback;
  public int Count;
  public Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> callback) { Callback = callback; }
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Count++; return Callback(request,token); }
 }
}
