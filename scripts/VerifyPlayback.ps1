param([Parameter(Mandatory=$true)][string]$BuildDirectory, [int]$DesktopNumber = 2)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$folder = Join-Path $root ('work\live-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
$null = New-Item -ItemType Directory -Path $folder
$desktopKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops'
$settings = Get-ItemProperty -LiteralPath $desktopKey
$bytes = [byte[]]$settings.VirtualDesktopIDs
$offset = ($DesktopNumber - 1) * 16
if ($DesktopNumber -lt 1 -or $offset + 16 -gt $bytes.Length) { throw 'Requested desktop does not exist; no desktop will be created automatically.' }
$chunk = New-Object byte[] 16
[Array]::Copy($bytes, $offset, $chunk, 0, 16)
$desktop = [Guid]::new($chunk)
$before = if ($null -ne $settings.CurrentVirtualDesktop) { [Guid]::new([byte[]]$settings.CurrentVirtualDesktop).ToString() } else { 'unknown' }
$app = Start-Process -FilePath (Join-Path $BuildDirectory 'AppleMusicLyrics.exe') -ArgumentList @('--live-check', ('"' + $folder + '"'), '--desktop', $desktop.ToString()) -PassThru -WindowStyle Hidden
try {
 if (-not $app.WaitForExit(65000)) { throw 'Real playback verification timed out.' }
 $code = $app.ExitCode
 foreach ($name in @('live-check.txt', 'live-check-error.txt', 'startup-error.txt')) {
  $file = Join-Path $folder $name
  if (Test-Path -LiteralPath $file) { Get-Content -LiteralPath $file }
 }
 $afterSettings = Get-ItemProperty -LiteralPath $desktopKey
 $after = if ($null -ne $afterSettings.CurrentVirtualDesktop) { [Guid]::new([byte[]]$afterSettings.CurrentVirtualDesktop).ToString() } else { 'unknown' }
 @("Target desktop: $DesktopNumber ($desktop)", "Current before: $before", "Current after: $after", "Exit code: $code") | Set-Content -LiteralPath (Join-Path $folder 'desktop-and-exit.txt') -Encoding UTF8
 Write-Output ('LIVE_OUTPUT=' + $folder)
 if ($code -ne 0) { throw 'Real playback verification failed; see recorded details.' }
} finally { if (-not $app.HasExited) { $app.Kill() } }
