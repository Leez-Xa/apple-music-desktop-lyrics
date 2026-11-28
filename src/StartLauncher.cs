using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace AMLyrics {
 public static class StartLauncher {
  [STAThread]
  public static int Main(string[] args) {
   bool printTarget = args != null && args.Any(a => string.Equals(a, "--print-target", StringComparison.OrdinalIgnoreCase));

   string projectRoot = FindProjectRoot(AppDomain.CurrentDomain.BaseDirectory);
   if (projectRoot == null) {
    if (!printTarget) {
     MessageBox.Show("未能定位项目根目录，请将 Start.exe 放在项目根目录或构建目录内运行。", "Apple Music 桌面歌词", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
    return 1;
   }

   string outputsDir = Path.Combine(projectRoot, "outputs");
   string buildDir = FindLatestBuildDir(outputsDir);

   if (printTarget) {
    return string.IsNullOrEmpty(buildDir) ? 1 : 0;
   }

   if (string.IsNullOrEmpty(buildDir)) {
    string buildScript = Path.Combine(Path.Combine(projectRoot, "scripts"), "Build.ps1");
    if (!File.Exists(buildScript) || !RunBuildScriptHidden(buildScript, projectRoot)) {
     MessageBox.Show("未找到可用构建且自动构建失败，请检查本机 .NET Framework 4.x 环境或运行 scripts\\Build.ps1 查看详情。", "Apple Music 桌面歌词", MessageBoxButtons.OK, MessageBoxIcon.Error);
     return 1;
    }
    buildDir = FindLatestBuildDir(outputsDir);
    if (string.IsNullOrEmpty(buildDir)) {
     MessageBox.Show("构建完成后仍未找到 AppleMusicLyrics.exe。", "Apple Music 桌面歌词", MessageBoxButtons.OK, MessageBoxIcon.Error);
     return 1;
    }
   }

   string watchExe = Path.Combine(buildDir, "AppleMusicLyricsWatch.exe");
   bool watcherWasRunning = false;
   if (File.Exists(watchExe)) {
    watcherWasRunning = RunToolHidden(watchExe, "--status", buildDir, 5000) == 0;
    RunToolHidden(watchExe, "--stop --include-host", buildDir, 5000);
   }

   WaitForOrKillProjectProcesses(outputsDir, 2000);

   string hostExe = Path.Combine(buildDir, "AppleMusicLyrics.exe");
   if (!StartProcessSilent(hostExe, buildDir, false)) {
    MessageBox.Show("无法启动桌面歌词主程序：" + hostExe, "Apple Music 桌面歌词", MessageBoxButtons.OK, MessageBoxIcon.Error);
    return 1;
   }

   if (watcherWasRunning && File.Exists(watchExe)) {
    Thread.Sleep(250);
    StartProcessSilent(watchExe, buildDir, true);
   }

   return 0;
  }

  static string FindProjectRoot(string baseDirectory) {
   for (var directory = new DirectoryInfo(baseDirectory); directory != null; directory = directory.Parent) {
    string candidate = directory.FullName;
    if (Directory.Exists(Path.Combine(candidate, "scripts")) &&
        (Directory.Exists(Path.Combine(candidate, "src")) || Directory.Exists(Path.Combine(candidate, "outputs")))) {
     return candidate;
    }
   }
   return null;
  }

  static string FindLatestBuildDir(string outputsDir) {
   if (!Directory.Exists(outputsDir)) return null;
   return Directory.GetDirectories(outputsDir, "build-*")
    .Where(dir =>
     File.Exists(Path.Combine(dir, "AppleMusicLyrics.exe")) &&
     File.Exists(Path.Combine(dir, "MediaSession.ps1")))
    .OrderByDescending(dir => File.GetLastWriteTimeUtc(Path.Combine(dir, "AppleMusicLyrics.exe")))
    .FirstOrDefault();
  }

  static bool RunBuildScriptHidden(string buildScript, string workingDirectory) {
   string psExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
   if (!File.Exists(psExe)) psExe = "powershell.exe";
   return RunToolHidden(psExe, "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + buildScript + "\"", workingDirectory, 60000) == 0;
  }

  static int RunToolHidden(string exePath, string arguments, string workingDirectory, int timeoutMs) {
   var psi = new ProcessStartInfo {
    FileName = exePath,
    Arguments = arguments,
    WorkingDirectory = workingDirectory,
    UseShellExecute = false,
    CreateNoWindow = true
   };
   try {
    using (var proc = Process.Start(psi)) {
     if (proc == null || !proc.WaitForExit(timeoutMs)) return -1;
     return proc.ExitCode;
    }
   } catch {
    return -1;
   }
  }

  static bool StartProcessSilent(string exePath, string workingDirectory, bool createNoWindow) {
   var psi = new ProcessStartInfo {
    FileName = exePath,
    WorkingDirectory = workingDirectory,
    UseShellExecute = false,
    CreateNoWindow = createNoWindow
   };
   try {
    using (var proc = Process.Start(psi)) {
     return proc != null;
    }
   } catch {
    return false;
   }
  }

  static void WaitForOrKillProjectProcesses(string outputsDir, int timeoutMs) {
   if (!Directory.Exists(outputsDir)) return;
   string outputsPrefix = outputsDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
   DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

   while (true) {
    bool anyAlive = false;
    bool timedOut = DateTime.UtcNow >= deadline;
    Process[] all = Process.GetProcesses();
    try {
     for (int i = 0; i < all.Length; i++) {
      Process proc = all[i];
      if (!IsProjectOutputProcess(proc, outputsPrefix)) continue;
      anyAlive = true;
      if (timedOut) {
       try { proc.Kill(); } catch { }
      }
     }
    } finally {
     for (int i = 0; i < all.Length; i++) {
      try { all[i].Dispose(); } catch { }
     }
    }

    if (!anyAlive) break;
    if (timedOut) {
     Thread.Sleep(150);
     break;
    }
    Thread.Sleep(100);
   }
  }

  static bool IsProjectOutputProcess(Process proc, string outputsPrefix) {
   if (proc == null) return false;
   try {
    if (proc.HasExited) return false;
    string name = proc.ProcessName ?? "";
    if (!name.StartsWith("AppleMusicLyrics", StringComparison.OrdinalIgnoreCase)) return false;
    string exePath = proc.MainModule != null ? proc.MainModule.FileName : null;
    return !string.IsNullOrEmpty(exePath) &&
           exePath.StartsWith(outputsPrefix, StringComparison.OrdinalIgnoreCase);
   } catch {
    return false;
   }
  }
 }
}
