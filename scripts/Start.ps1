$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$outputsDir = Join-Path $root 'outputs'
$build = $null
if (Test-Path -LiteralPath $outputsDir -PathType Container) {
 $build = Get-ChildItem -LiteralPath $outputsDir -Directory -Filter 'build-*' |
  Where-Object { (Test-Path -LiteralPath (Join-Path $_.FullName 'AppleMusicLyrics.exe') -PathType Leaf) -and (Test-Path -LiteralPath (Join-Path $_.FullName 'MediaSession.ps1') -PathType Leaf) } |
  Sort-Object { (Get-Item -LiteralPath (Join-Path $_.FullName 'AppleMusicLyrics.exe')).LastWriteTimeUtc } -Descending |
  Select-Object -First 1
}
if ($null -eq $build) {
 $output = & (Join-Path $PSScriptRoot 'Build.ps1')
 $buildPath = @($output)[-1]
} else { $buildPath = $build.FullName }

function Invoke-WatchTool([string]$ExePath, [string]$Arguments) {
 $psi = New-Object System.Diagnostics.ProcessStartInfo
 $psi.FileName = $ExePath
 $psi.Arguments = $Arguments
 $psi.WorkingDirectory = Split-Path -Parent $ExePath
 $psi.UseShellExecute = $false
 $psi.CreateNoWindow = $true
 $proc = [System.Diagnostics.Process]::Start($psi)
 try {
  if ($null -eq $proc -or -not $proc.WaitForExit(5000)) { return -1 }
  return $proc.ExitCode
 } finally {
  if ($null -ne $proc) { $proc.Dispose() }
 }
}

$watchExe = Join-Path $buildPath 'AppleMusicLyricsWatch.exe'
$watcherWasRunning = $false
if (Test-Path -LiteralPath $watchExe -PathType Leaf) {
 $watcherWasRunning = (Invoke-WatchTool -ExePath $watchExe -Arguments '--status') -eq 0
 $null = Invoke-WatchTool -ExePath $watchExe -Arguments '--stop --include-host'
}

$outputsPrefix = $outputsDir.TrimEnd('\') + '\'
$deadline = [DateTime]::UtcNow.AddSeconds(2)
while ($true) {
 $remaining = @(Get-Process -Name 'AppleMusicLyrics*' -ErrorAction SilentlyContinue | Where-Object {
  try {
   $_.Path -and $_.Path.StartsWith($outputsPrefix, [StringComparison]::OrdinalIgnoreCase)
  } catch { $false }
 })
 if ($remaining.Count -eq 0) { break }
 if ([DateTime]::UtcNow -ge $deadline) {
  foreach ($proc in $remaining) {
   try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch {}
  }
  Start-Sleep -Milliseconds 150
  break
 }
 Start-Sleep -Milliseconds 100
}

Start-Process -FilePath (Join-Path $buildPath 'AppleMusicLyrics.exe') -WorkingDirectory $buildPath

if ($watcherWasRunning -and (Test-Path -LiteralPath $watchExe -PathType Leaf)) {
 Start-Sleep -Milliseconds 250
 Start-Process -FilePath $watchExe -WorkingDirectory $buildPath
}
