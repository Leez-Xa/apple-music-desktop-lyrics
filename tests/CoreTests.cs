using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AMLyrics;
class CoreTests {
 static int failures, total;
 static void Check(bool ok, string name) { total++; Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failures++; }
 static void Near(double actual, double expected, string name) { Check(Math.Abs(actual - expected) < 0.001, name); }
 static TrackSnapshot Track(string name) { return new TrackSnapshot { SourceAppId = "AppleMusic.Test", Title = name }; }
 static int Main(string[] args) { try { Run(args).GetAwaiter().GetResult(); } catch (Exception ex) { Console.WriteLine(ex); return 2; } Console.WriteLine(total + " checks; " + failures + " failed."); return failures == 0 ? 0 : 1; }
 static async Task Run(string[] args) {
  var doc = LrcParser.Parse("[ti:测试]\n[00:12.30]第二句\n[00:02.005]第一句\n[00:12.30]翻译\n[00:12.30]第二句\n[00:20][00:30]重复句", "test");
  Check(doc.IsSynchronized && doc.Lines.Count == 4, "Parser: multiple stamps and duplicate groups");
  Near(doc.Lines[0].TimestampSeconds, 2.005, "Parser: milliseconds and sorted timestamps");
  Check(doc.Lines[1].Text == "第二句\n翻译", "Parser: stable translations and exact duplicate removal");
  Near(doc.Lines[3].TimestampSeconds, 30, "Parser: multiple stamps on one line");
  Near(LrcParser.Parse("[offset:+500]\n[00:01.2]x", "").Lines[0].TimestampSeconds, 0.7, "Parser: positive offset advances lyrics");
  Near(LrcParser.Parse("[offset:+2500]\n[00:01.2]x", "").Lines[0].TimestampSeconds, 0, "Parser: advanced timestamp clamps to zero");
  Near(LrcParser.Parse("[offset:-500]\n[00:01.2]x", "").Lines[0].TimestampSeconds, 1.7, "Parser: negative offset delays lyrics");
  Check(!LrcParser.Parse("歌词里的 [00:10] 只是文字", "").IsSynchronized, "Parser: embedded tag does not fabricate sync");
  Check(LrcParser.Parse("[00:01]文字里的 [00:20] 保留", "").Lines.Count == 1, "Parser: tags in lyric text are preserved");
  Check(LrcParser.Parse("[00:60]bad\n[00:99]bad", "").IsSynchronized == false, "Parser: invalid seconds are not synchronized");
  Check(!LrcParser.Parse("[ar:歌手]\n普通歌词\n下一行", "").IsSynchronized, "Parser: plain text stays unsynchronized");
  Check(LrcParser.Parse("[ar:歌手]\n\n", "").Lines.Count == 0, "Parser: metadata-only document is empty");
  Check(LrcParser.Parse("[00:00]\n[00:01]x", "").Lines[0].Text == "", "Parser: blank timed line preserves instrumental break");
  Check(PlaybackMath.FindLine(doc.Lines, 0) == -1, "Timeline: before first line");
  Check(PlaybackMath.FindLine(doc.Lines, 12.3) == 1, "Timeline: exact timestamp boundary");
  Check(PlaybackMath.FindLine(doc.Lines, 10000) == 3, "Timeline: after last line");
  Check(PlaybackMath.FindLine(new LyricLine[0], 1) == -1, "Timeline: empty document");
  var now = DateTime.UtcNow;
  var sample = new MediaSample { Status = "Connected", ReceivedAtUtc = now, TimelineReliable = true, Track = Track("clock") };
  sample.Track.DurationSeconds = 180; sample.Track.PositionSeconds = 20; sample.Track.CapturedAtUtc = now; sample.Track.IsPlaying = true;
  Near(PlaybackMath.Position(sample, now.AddSeconds(1.25)), 21.25, "Clock: interpolate confirmed playing position");
  sample.Track.IsPlaying = false;
  Near(PlaybackMath.Position(sample, now.AddSeconds(1)), 20, "Clock: pause freezes position");
  sample.Track.PositionSeconds = 5;
  Near(PlaybackMath.Position(sample, now.AddSeconds(1)), 5, "Clock: backward seek uses latest position");
  sample.Track.PositionSeconds = 130;
  Near(PlaybackMath.Position(sample, now.AddSeconds(1)), 130, "Clock: forward seek uses latest position");
  sample.Track.IsPlaying = true; sample.Track.PlaybackRate = 2;
  Near(PlaybackMath.Position(sample, now.AddSeconds(1)), 132, "Clock: playback rate respected");
  sample.TimelineReliable = false;
  Near(PlaybackMath.Position(sample, now.AddSeconds(1)), 130, "Clock: missing timeline never invents progress");
  sample.TimelineReliable = true;
  Check(!PlaybackMath.IsFresh(sample, now.AddSeconds(4)), "Clock: stale snapshot rejected");
  Near(PlaybackMath.Position(sample, now.AddSeconds(4)), 130, "Clock: stale position is not extrapolated");
  sample.Track.PositionSeconds = 179.5;
  Near(PlaybackMath.Position(sample, now.AddSeconds(1)), 180, "Clock: duration clamp");
  var timing = new LyricTiming();
  Near(timing.Read("song-a"), 0, "Calibration: no default lead is guessed");
  Near(timing.Adjust("song-a", 0.2), 0.2, "Calibration: 200ms positive advance");
  Near(timing.Read("song-b"), 0, "Calibration: another song stays unchanged");
  Near(timing.Adjust("song-a", -0.4), -0.2, "Calibration: negative correction delays lyrics");
  Near(timing.Adjust("song-a", 100), 5, "Calibration: positive limit");
  Near(timing.Adjust("song-a", -100), -5, "Calibration: negative limit");
  timing.Reset("song-a"); Near(timing.Read("song-a"), 0, "Calibration: reset preserves original LRC timestamps");
  sample.Track.IsPlaying = false; sample.Track.PositionSeconds = 10.125;
  Near(LyricTiming.Position(sample, now, 0.2), 10.325, "Calibration: precise fractional advance with paused clock");
  sample.Track.PositionSeconds = 0;
  Check(LyricTiming.Position(sample, now, -0.2) < 0, "Calibration: delayed first line keeps a prelude");
  var provider = new ControlledProvider();
  using (var session = new LyricSession(provider)) {
   Task old = session.SelectAsync(Track("old"));
   Check(session.Read().Loading, "Requests: loading state");
   Task recent = session.SelectAsync(Track("new"));
   Check(provider.OldToken.IsCancellationRequested, "Requests: track switch cancels old request");
   provider.New.SetResult(LrcParser.Parse("[00:00]new", "new")); await recent;
   provider.Old.SetResult(LrcParser.Parse("[00:00]old", "old")); await old;
   Check(session.Read().Document.Source == "new", "Requests: late old response cannot replace new lyrics");
   session.Clear(); Check(session.Read().Document == null && session.Read().Key == "", "Requests: disconnect clears lyrics");
  }
  var pending = new ControlledProvider();
  using (var session = new LyricSession(pending)) {
   var task = session.SelectAsync(Track("old")); session.Clear(); pending.Old.SetResult(doc); await task;
   Check(session.Read().Document == null, "Requests: late response after disconnect is ignored");
  }
  using (var session = new LyricSession(new FailingProvider())) {
   await session.SelectAsync(Track("bad")); Check(session.Read().Error == "expected failure", "Requests: provider error is explicit");
  }
  var local = new LocalLyricsProvider(); var track = Track("local"); local.Assign(track.Key, doc);
  Check(await local.GetAsync(track, CancellationToken.None) == doc, "Local: imported lyric assigned to matching track");
  Check(await local.GetAsync(Track("other"), CancellationToken.None) == null, "Local: different track cannot reuse lyrics");
  var chain = new LyricsProviders(new NullProvider(), local);
  Check(await chain.GetAsync(track, CancellationToken.None) == doc, "Providers: unavailable native provider falls through to local");
  var cancelled = new CancellationTokenSource(); cancelled.Cancel();
  try { await local.GetAsync(track, cancelled.Token); Check(false, "Local: cancellation"); }
  catch (OperationCanceledException) { Check(true, "Local: cancellation"); } cancelled.Dispose();
  if (args.Length > 0) {
   var utf = await LocalLyricsProvider.ReadFileAsync(Path.Combine(args[0], "utf8.lrc"), CancellationToken.None);
   Check(utf.Lines[0].Text == "中文歌词", "Files: UTF-8 Chinese preserved");
   var utf16 = await LocalLyricsProvider.ReadFileAsync(Path.Combine(args[0], "utf16.lrc"), CancellationToken.None);
   Check(utf16.Lines[0].Text == "中文歌词", "Files: Unicode BOM preserved");
   try { await LocalLyricsProvider.ReadFileAsync(Path.Combine(args[0], "invalid.lrc"), CancellationToken.None); Check(false, "Files: invalid UTF-8 rejected"); }
   catch (System.Text.DecoderFallbackException) { Check(true, "Files: invalid UTF-8 rejected"); }
   try { await LocalLyricsProvider.ReadFileAsync(Path.Combine(args[0], "oversized.lrc"), CancellationToken.None); Check(false, "Files: oversized import rejected"); }
   catch (InvalidDataException) { Check(true, "Files: oversized import rejected"); }
  }
 }
 class ControlledProvider : ILyricsProvider {
  public TaskCompletionSource<LyricDocument> Old = new TaskCompletionSource<LyricDocument>();
  public TaskCompletionSource<LyricDocument> New = new TaskCompletionSource<LyricDocument>();
  public CancellationToken OldToken;
  public Task<LyricDocument> GetAsync(TrackSnapshot t, CancellationToken token) { if (t.Title == "old") { OldToken = token; return Old.Task; } return New.Task; }
 }
 class FailingProvider : ILyricsProvider { public Task<LyricDocument> GetAsync(TrackSnapshot t, CancellationToken token) { throw new IOException("expected failure"); } }
 class NullProvider : ILyricsProvider { public Task<LyricDocument> GetAsync(TrackSnapshot t, CancellationToken token) { return Task.FromResult<LyricDocument>(null); } }
}
