using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
namespace AMLyrics {
 public static class Program {
  [STAThread]
  public static int Main(string[] args) {
   bool smoke = args.Length > 0 && args[0] == "--ui-smoke";
   bool live = args.Length > 0 && args[0] == "--live-check";
   bool verification = smoke || live;
   int desktopIndex = Array.IndexOf(args, "--desktop");
   Guid desktop = Guid.Empty;
   if (desktopIndex >= 0 && (desktopIndex + 1 >= args.Length || !Guid.TryParse(args[desktopIndex + 1], out desktop))) return 2;
   string directory = AppDomain.CurrentDomain.BaseDirectory;
   bool first;
   using (var mutex = new Mutex(true, verification ? null : @"Local\AMDesktopLyrics.SingleInstance", out first)) {
    if (!first && !verification) {
     try { using (var existing = EventWaitHandle.OpenExisting(@"Local\AMDesktopLyrics.Show")) existing.Set(); }
     catch (WaitHandleCannotBeOpenedException) { MessageBox.Show("桌面歌词正在启动，请稍后重试。", "桌面歌词"); }
     return 0;
    }
    using (var show = new EventWaitHandle(false, EventResetMode.AutoReset, verification ? null : @"Local\AMDesktopLyrics.Show"))
    using (var stop = new EventWaitHandle(false, EventResetMode.ManualReset)) {
     var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
     IMediaSource media = smoke ? (IMediaSource)new TestMediaSource() : new WindowsMediaSource(Path.Combine(directory, "MediaSession.ps1"));
     var window = new LyricsWindow(media, new LocalLyricsProvider(), verification);
     int result = 0;
     Task listener = null;
     if (!verification) listener = Task.Run(() => {
      while (WaitHandle.WaitAny(new WaitHandle[] { show, stop }) == 0) {
       if (!app.Dispatcher.HasShutdownStarted) app.Dispatcher.BeginInvoke(new Action(window.RestoreWindow));
      }
     });
     else {
      string folder = args.Length > 1 ? Path.GetFullPath(args[1]) : directory;
      window.ShowActivated = false; window.ShowInTaskbar = false;
      window.Loaded += async (s,e) => {
       await Task.Yield();
       try {
        Directory.CreateDirectory(folder);
        using (var log = new StreamWriter(Path.Combine(folder, live ? "live-check.txt" : "ui-smoke.txt"), false, System.Text.Encoding.UTF8)) {
         Action<bool,string> check = (ok, name) => { log.WriteLine((ok ? "PASS " : "FAIL ") + name); log.Flush(); if (!ok) result = 1; };
         if (live) await window.LiveCheckAsync(folder, desktop, check);
         else {
          await window.SmokeAsync(folder, check);
          if (desktop != Guid.Empty) {
           bool onCurrent;
           check(DesktopPlacement.Locate(window, out onCurrent) == desktop && !onCurrent, "Smoke test stays on requested desktop without switching");
          }
         }
        }
       } catch (Exception ex) { File.WriteAllText(Path.Combine(folder,live ? "live-check-error.txt" : "ui-smoke-error.txt"), ex.ToString()); result = 1; }
       finally { window.Close(); }
      };
     }
     try {
      if (desktopIndex >= 0) {
       window.ShowActivated = false;
       window.WindowState = WindowState.Minimized;
       window.Show();
       DesktopPlacement.MoveOwnWindow(window, desktop);
       window.WindowState = WindowState.Normal;
      }
      app.Run(window);
     } catch (Exception ex) {
      result = 1;
      if (verification && args.Length > 1) {
       string failureFolder = Path.GetFullPath(args[1]);
       Directory.CreateDirectory(failureFolder);
       File.WriteAllText(Path.Combine(failureFolder, "startup-error.txt"), ex.ToString());
      }
      window.Close();
     }
     finally { stop.Set(); if (listener != null) listener.Wait(2000); media.Dispose(); }
     return result;
    }
   }
  }
}
}
