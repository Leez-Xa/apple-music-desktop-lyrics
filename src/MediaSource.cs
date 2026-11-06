using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace AMLyrics {
 public sealed class WindowsMediaSource : IMediaSource {
  readonly object gate = new object();
  readonly string script;
  Process worker;
  MediaSample latest = new MediaSample();
  DateTime started;
  bool disposed;
  public WindowsMediaSource(string script) { this.script = script; Start(); }
  void Start() {
   started = DateTime.UtcNow;
   try {
    var process = new Process();
    process.StartInfo = new ProcessStartInfo {
     FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
     Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\" -ParentProcessId " + Process.GetCurrentProcess().Id,
     UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
     StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
    };
    process.OutputDataReceived += (s,e) => { if (e.Data != null) Accept(process, e.Data); };
    process.ErrorDataReceived += (s,e) => {
     if (!string.IsNullOrWhiteSpace(e.Data)) lock (gate) {
      if (worker == process && !disposed) latest = new MediaSample { Status = "Error", Message = "媒体读取失败：" + e.Data };
     }
    };
    worker = process;
    process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
   } catch (Exception ex) {
    var failed = worker; worker = null;
    if (failed != null) failed.Dispose();
    latest = new MediaSample { Status = "Error", Message = "无法启动媒体读取：" + ex.Message };
   }
  }
  static string Text(Dictionary<string, object> data, string key) { object value; return data.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : ""; }
  static double Number(Dictionary<string, object> data, string key) { double value; return double.TryParse(Text(data,key), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : 0; }
  static bool Flag(Dictionary<string, object> data, string key) { return Text(data,key).Equals("True", StringComparison.OrdinalIgnoreCase); }
  void Accept(Process process, string json) {
   try {
    var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
    var sample = new MediaSample { Status = Text(data,"Status"), Message = Text(data,"Message"), TimelineReliable = Flag(data,"TimelineReliable") };
    if (sample.Status == "Connected") {
     DateTime captured;
     if (!DateTime.TryParse(Text(data,"CapturedAtUtc"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out captured)) {
      captured = DateTime.UtcNow; sample.TimelineReliable = false;
     }
     string appId = Text(data,"SourceAppId");
     sample.Track = new TrackSnapshot {
      Title = Text(data,"Title"), Artist = Text(data,"Artist"), Album = Text(data,"Album"), SourceAppId = appId,
      PositionSeconds = Number(data,"PositionSeconds"), DurationSeconds = Number(data,"DurationSeconds"),
      PlaybackRate = Number(data,"PlaybackRate"), CapturedAtUtc = captured, IsPlaying = Flag(data,"IsPlaying"),
      IsAppleMusic = appId.IndexOf("AppleMusic", StringComparison.OrdinalIgnoreCase) >= 0
     };
     if (!sample.Track.IsAppleMusic || string.IsNullOrWhiteSpace(sample.Track.Title)) {
      sample.Track = null; sample.Status = "Waiting"; sample.Message = "等待 Apple Music 播放歌曲";
     }
    }
    lock (gate) if (!disposed && worker == process) latest = sample;
   } catch (Exception ex) {
    lock (gate) if (!disposed && worker == process) latest = new MediaSample { Status = "Error", Message = "媒体数据无法解析：" + ex.Message };
   }
  }
  public MediaSample Read() {
   lock (gate) {
    if (!disposed && (DateTime.UtcNow - started).TotalSeconds > 12 &&
        (worker == null || worker.HasExited || (DateTime.UtcNow - latest.ReceivedAtUtc).TotalSeconds > 12)) {
     Stop(); Start();
    }
    return latest;
   }
  }
  void Stop() {
   var process = worker; worker = null;
   if (process == null) return;
   try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) {} catch (System.ComponentModel.Win32Exception) {}
   process.Dispose();
  }
  public void Dispose() { lock (gate) { disposed = true; Stop(); } }
 }
}