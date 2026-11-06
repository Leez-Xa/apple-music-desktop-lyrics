$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$build = Get-ChildItem -LiteralPath (Join-Path $root 'outputs') -Directory -Filter 'build-*' |
 Where-Object { (Test-Path -LiteralPath (Join-Path $_.FullName 'AppleMusicLyrics.exe')) -and (Test-Path -LiteralPath (Join-Path $_.FullName 'MediaSession.ps1')) } |
 Sort-Object Name -Descending | Select-Object -First 1
if ($null -eq $build) {
 $output = & (Join-Path $PSScriptRoot 'Build.ps1')
 $buildPath = @($output)[-1]
} else { $buildPath = $build.FullName }
Start-Process -FilePath (Join-Path $buildPath 'AppleMusicLyrics.exe') -WorkingDirectory $buildPath -WindowStyle Hidden
