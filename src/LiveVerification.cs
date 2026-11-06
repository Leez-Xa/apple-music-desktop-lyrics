using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
namespace AMLyrics {
 public sealed partial class LyricsWindow {
  internal async Task LiveCheckAsync(string folder, Guid target, Action<bool,string> check) {
   if (target != Guid.Empty) {
    bool onCurrent;
    Guid actual = DesktopPlacement.Locate(this, out onCurrent);
    check(actual == target, "Real window resides on requested desktop");
    check(!ShowActivated, "Verification window is configured not to activate");
    File.WriteAllText(Path.Combine(folder, "desktop-start.txt"), "Target=" + target + "; Window=" + actual + "; OnCurrentDesktop=" + onCurrent);
   }
   var watch = Stopwatch.StartNew();
   MediaSample sample = null;
   while (watch.Elapsed.TotalSeconds < 12) {
    sample = media.Read();
    if (sample.Status == "Error") throw new InvalidOperationException(sample.Message);
    if (sample.Track != null && sample.TimelineReliable && PlaybackMath.IsFresh(sample, DateTime.UtcNow)) break;
    await Task.Delay(100);
   }
   if (sample == null || sample.Track == null || !sample.TimelineReliable) throw new InvalidOperationException("No real Apple Music track with a reliable timeline is available.");
   check(sample.Track.IsAppleMusic, "Real Apple Music source selected");
   check(sample.Track.IsPlaying, "Real playing state observed without sending playback commands");
   Refresh(); await pendingSelection; Refresh();
   check(title.Text.Contains(sample.Track.Title), "Real metadata reaches the window");
   var document = lyrics.Read().Document;
   if (document == null || !document.IsSynchronized || document.Lines.Count < 2) throw new InvalidOperationException("Automatic lookup did not produce real synchronized lyrics: " + lyrics.Read().Error);
   check(document.Source.StartsWith("LRCLIB", StringComparison.Ordinal), "Real synchronized lyrics fetched automatically from LRCLIB without importing files");
   check(document.Lines.Count > 1, "Remote lyrics contain real timed lines");
   string key = sample.Track.Key;
   SavePreview(Path.Combine(folder, "real-playback-preview.png"));
   int updates = 0, lineChanges = 0, lineChecks = 0;
   bool switched = false;
   string previousLine = current.Text;
   DateTime received = sample.ReceivedAtUtc;
   var serializer = new JavaScriptSerializer();
   using (var trace = new StreamWriter(Path.Combine(folder, "real-playback-trace.jsonl"), false, new UTF8Encoding(false))) {
    watch.Restart();
    while (watch.Elapsed.TotalSeconds < 20) {
     sample = media.Read();
     if (sample.Track == null || sample.Status != "Connected" || !PlaybackMath.IsFresh(sample, DateTime.UtcNow)) throw new InvalidOperationException("Real media connection interrupted during observation.");
     Refresh();
     if (sample.Track.Key != key) {
      await pendingSelection; Refresh();
      check(lyrics.Read().Document != document && lyrics.Read().Key == sample.Track.Key, "Natural track switch selects new lyrics without reusing the previous song");
      switched = true; break;
     }
     if (received != sample.ReceivedAtUtc) { updates++; received = sample.ReceivedAtUtc; }
     if (!sample.TimelineReliable) throw new InvalidOperationException("Real timeline became unavailable during observation.");
     double offset = timing.Read(sample.Track.Key);
     double position = LyricTiming.Position(sample, DateTime.UtcNow, offset);
     int expectedIndex = PlaybackMath.FindLine(document.Lines, position);
     int actualIndex = displayedLineIndex;
     // Refresh and this assertion can straddle a one-second marker by a few milliseconds.
     if (actualIndex < -1 || actualIndex >= document.Lines.Count || Math.Abs(actualIndex - expectedIndex) > 1) throw new InvalidOperationException("Displayed lyric line does not follow the real playback position.");
     lineChecks++;
     if (current.Text != previousLine) { lineChanges++; previousLine = current.Text; }
     trace.WriteLine(serializer.Serialize(new {
      CapturedAtUtc = sample.Track.CapturedAtUtc, Title = sample.Track.Title,
      PositionSeconds = sample.Track.PositionSeconds, DisplayPositionSeconds = position,
      OffsetSeconds = offset, SampleAgeMilliseconds = (DateTime.UtcNow - sample.ReceivedAtUtc).TotalMilliseconds,
      IsPlaying = sample.Track.IsPlaying, TimelineReliable = sample.TimelineReliable,
      Source = document.Source, DisplayedLine = current.Text, ExpectedIndex = expectedIndex, ActualIndex = actualIndex
     }));
     trace.Flush();
     await Task.Delay(100);
    }
   }
   check(updates >= 2, "Real media heartbeat refreshes throughout observation");
   check(lineChecks >= 10, "Real playback position is checked against displayed lines repeatedly");
   check(lineChanges >= 1 || switched, "Real lyrics advance with playback or naturally switch tracks");
   if (!switched) SavePreview(Path.Combine(folder, "real-playback-final.png"));
   if (target != Guid.Empty) {
    bool onCurrent;
    Guid actual = DesktopPlacement.Locate(this, out onCurrent);
    check(actual == target, "Window stays on requested desktop throughout observation");
    File.WriteAllText(Path.Combine(folder, "desktop-end.txt"), "Target=" + target + "; Window=" + actual + "; OnCurrentDesktop=" + onCurrent);
   }
  }
 }
}
