param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root ('outputs\build-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw "Output already exists. Choose a new build folder: $OutputDirectory" }
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler is unavailable; see docs/README.md.' }
$null = New-Item -ItemType Directory -Path $OutputDirectory
$references = @('System.dll','System.Core.dll','System.Net.Http.dll','System.Xaml.dll','System.Web.Extensions.dll','System.Drawing.dll','System.Windows.Forms.dll','WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll','UIAutomationClient.dll','UIAutomationTypes.dll')
$arguments = @('/nologo','/target:winexe','/platform:x64','/optimize+','/warn:4','/utf8output',('/out:' + (Join-Path $OutputDirectory 'AppleMusicLyrics.exe')))
foreach ($reference in $references) {
 $path = Join-Path $framework $reference
 if (-not (Test-Path -LiteralPath $path)) { $path = Join-Path (Join-Path $framework 'WPF') $reference }
 $arguments += '/reference:' + $path
}
$sources = @(
 'AppleMusicNativeLyrics.cs', 'Contracts.cs', 'DesktopPlacement.cs', 'HostIpc.cs', 'KoreanRomanizer.cs',
 'LiveVerification.cs', 'LrclibLyrics.cs', 'Lyrics.cs', 'LyricsColorPickerDialog.cs', 'LyricsHitTestBridge.cs', 'LyricsLayout.cs',
 'LyricsOverlayChrome.cs', 'LyricsOverlayHover.cs', 'LyricsPreferencesStore.cs', 'LyricsSettingsPanel.cs',
 'LyricsTrackResolver.cs', 'LyricsUiContracts.cs', 'LyricsWindow.cs', 'MediaSource.cs',
 'ProcessJobGuard.cs', 'Program.cs', 'WindowBehavior.cs'
)
foreach ($source in $sources) {
 $sourcePath = Join-Path (Join-Path $root 'src') $source
 if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) { throw "Missing source file: $sourcePath" }
 $arguments += $sourcePath
}
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Application compilation failed.' }

$watchArguments = @('/nologo','/target:winexe','/platform:x64','/optimize+','/warn:4','/utf8output',('/out:' + (Join-Path $OutputDirectory 'AppleMusicLyricsWatch.exe')))
foreach ($reference in @('System.dll','System.Core.dll')) {
 $watchArguments += '/reference:' + (Join-Path $framework $reference)
}
foreach ($source in @('HostIpc.cs', 'AppleMusicMonitor.cs', 'StandbyWatcher.cs')) {
 $sourcePath = Join-Path (Join-Path $root 'src') $source
 if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) { throw "Missing watcher source file: $sourcePath" }
 $watchArguments += $sourcePath
}
& $compiler @watchArguments
if ($LASTEXITCODE -ne 0) { throw 'Standby watcher compilation failed.' }

$launcherSource = Join-Path (Join-Path $root 'src') 'StandbyLauncher.cs'
if (-not (Test-Path -LiteralPath $launcherSource -PathType Leaf)) { throw "Missing launcher source file: $launcherSource" }
$launcherArguments = @('/nologo','/target:winexe','/platform:x64','/optimize+','/warn:4','/utf8output',('/out:' + (Join-Path $OutputDirectory 'AppleMusicLyricsStandby.exe')))
foreach ($reference in @('System.dll','System.Core.dll')) {
 $launcherArguments += '/reference:' + (Join-Path $framework $reference)
}
$launcherArguments += $launcherSource
& $compiler @launcherArguments
if ($LASTEXITCODE -ne 0) { throw 'Standby launcher compilation failed.' }

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'MediaSession.ps1') -Destination (Join-Path $OutputDirectory 'MediaSession.ps1') -ErrorAction Stop
Write-Output $OutputDirectory
