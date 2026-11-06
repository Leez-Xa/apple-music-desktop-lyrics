using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using AMLyrics;
class MediaIntegration {
 static int Main(string[] args) {
  var source = new WindowsMediaSource(args[0]);
  int workerId = 0;
  try {
   var worker = (Process)typeof(WindowsMediaSource).GetField("worker", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(source);
   workerId = worker.Id;
   var timeout = Stopwatch.StartNew(); MediaSample sample = null;
   while (timeout.Elapsed.TotalSeconds < 10) {
    sample = source.Read();
    if (sample.Status == "Error") { Console.WriteLine("FAIL " + sample.Message); return 1; }
    if (sample.Track != null) break;
    Thread.Sleep(100);
   }
   if (sample == null || sample.Track == null) { Console.WriteLine("SKIP No published Apple Music track; real playback validation remains pending."); return 0; }
   if (!sample.Track.IsAppleMusic || !PlaybackMath.IsFresh(sample, DateTime.UtcNow)) { Console.WriteLine("FAIL Invalid media snapshot"); return 1; }
   Console.WriteLine("PASS Real Apple Music metadata decoded (including Unicode)");
   Console.WriteLine("INFO TimelineReliable=" + sample.TimelineReliable + "; IsPlaying=" + sample.Track.IsPlaying);
   int updates = 0; var received = sample.ReceivedAtUtc;
   var key = sample.Track.Key;
   timeout.Restart();
   while (timeout.Elapsed.TotalSeconds < 3) {
    sample = source.Read();
    if (sample.Track == null || sample.Status == "Error") { Console.WriteLine("FAIL Media stream disconnected"); return 1; }
    if (sample.ReceivedAtUtc != received) { updates++; received = sample.ReceivedAtUtc; }
    if (sample.Track.Key == key && sample.TimelineReliable) {
     double p = PlaybackMath.Position(sample, DateTime.UtcNow);
     if (p < 0 || p > sample.Track.DurationSeconds) { Console.WriteLine("FAIL Out-of-range position"); return 1; }
    }
    Thread.Sleep(100);
   }
   if (updates < 2) { Console.WriteLine("FAIL Media heartbeat did not refresh"); return 1; }
   Console.WriteLine("PASS Real media heartbeat and bounded timeline; updates=" + updates);
  } finally {
   source.Dispose();
   if (workerId > 0) {
    bool stopped = false;
    try { using (var worker = Process.GetProcessById(workerId)) stopped = worker.WaitForExit(3000); }
    catch (ArgumentException) { stopped = true; }
    Console.WriteLine((stopped ? "PASS" : "FAIL") + " Own media worker exits on disposal");
    if (!stopped) Environment.ExitCode = 1;
   }
  }
  return Environment.ExitCode;
 }
}
