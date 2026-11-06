param([Parameter(Mandatory=$true)][string]$BuildDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$folder = Join-Path $root ('work\integration-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
$null = New-Item -ItemType Directory -Path $folder
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework = Split-Path -Parent $compiler
$exe = Join-Path $folder 'MediaIntegration.exe'
& $compiler /nologo /target:exe /platform:x64 /utf8output ('/reference:' + (Join-Path $framework 'System.Web.Extensions.dll')) ('/out:' + $exe) (Join-Path $root 'src\Contracts.cs') (Join-Path $root 'src\Lyrics.cs') (Join-Path $root 'src\MediaSource.cs') (Join-Path $root 'tests\MediaIntegration.cs')
if ($LASTEXITCODE -ne 0) { throw 'Integration test compilation failed.' }
$log = & $exe (Join-Path $BuildDirectory 'MediaSession.ps1')
$code = $LASTEXITCODE
$log | Set-Content -LiteralPath (Join-Path $folder 'media-integration.txt') -Encoding UTF8
$log | Write-Output
if ($code -ne 0) { throw 'Media integration failed.' }
# Do not interact with an instance that was already running before this test.
if (Get-Process -Name AppleMusicLyrics -ErrorAction SilentlyContinue) { Write-Output 'SKIP An existing lyrics instance is running; preserving it.'; Write-Output ('INTEGRATION_OUTPUT=' + $folder); exit 0 }
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$app = $null
$uiLog = New-Object 'System.Collections.Generic.List[string]'
function Find-Window([int]$processId) {
 $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $processId)
 return [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
}
function Find-Button($window, [string]$name) {
 $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
 return $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
try {
 $app = Start-Process -FilePath (Join-Path $BuildDirectory 'AppleMusicLyrics.exe') -PassThru -WindowStyle Hidden
 $watch = [Diagnostics.Stopwatch]::StartNew()
 $window = $null
 while ($watch.Elapsed.TotalSeconds -lt 8) {
  if ($app.HasExited) { throw 'Application exited before its window opened.' }
  $window = Find-Window $app.Id
  if ($null -ne $window) { break }
  Start-Sleep -Milliseconds 100
 }
 if ($null -eq $window) { throw 'Window was not published to accessibility.' }
 $uiLog.Add('PASS Real application launches and exposes an accessible window')
 $hide = Find-Button $window '隐藏窗口 · Esc；托盘或 Ctrl+Alt+L 恢复'
 if ($null -eq $hide) { throw 'Accessible hide button was not found.' }
 $watch.Restart()
 $connected = $false
 while ($watch.Elapsed.TotalSeconds -lt 8) {
  $texts = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))
  if (@($texts | Where-Object { $_.Current.Name -eq '已连接 Apple Music' }).Count -gt 0) { $connected = $true; break }
  Start-Sleep -Milliseconds 100
 }
 if ($connected) { $uiLog.Add('PASS Real application displays connected Apple Music state') }
 else { $uiLog.Add('SKIP Connected UI state not observed; playback may be unavailable') }
 $hide.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
 $watch.Restart()
 while ($null -ne (Find-Window $app.Id) -and $watch.Elapsed.TotalSeconds -lt 3) { Start-Sleep -Milliseconds 50 }
 if ($null -ne (Find-Window $app.Id)) { throw 'Hide action did not hide the window.' }
 $uiLog.Add('PASS Accessible hide action hides own window')
 $second = Start-Process -FilePath (Join-Path $BuildDirectory 'AppleMusicLyrics.exe') -PassThru -WindowStyle Hidden
 if (-not $second.WaitForExit(5000) -or $second.ExitCode -ne 0) { throw 'Second launch did not signal the original instance.' }
 $watch.Restart(); $window = $null
 while ($watch.Elapsed.TotalSeconds -lt 4) { $window = Find-Window $app.Id; if ($null -ne $window) { break }; Start-Sleep -Milliseconds 50 }
 if ($null -eq $window) { throw 'Second launch did not restore the original window.' }
 $uiLog.Add('PASS Single-instance relaunch restores hidden window')
 $exit = Find-Button $window '退出桌面歌词 · Ctrl+Q'
 $exit.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
 if (-not $app.WaitForExit(5000) -or $app.ExitCode -ne 0) { throw 'Application did not exit cleanly.' }
 $uiLog.Add('PASS Accessible exit closes application cleanly')
} finally {
 if ($null -ne $app -and -not $app.HasExited) { $app.Kill() }
 $uiLog | Set-Content -LiteralPath (Join-Path $folder 'launch-integration.txt') -Encoding UTF8
 $uiLog | Write-Output
 Write-Output ('INTEGRATION_OUTPUT=' + $folder)
}
