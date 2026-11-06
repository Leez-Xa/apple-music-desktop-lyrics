param([string]$BuildDirectory, [int]$DesktopNumber = 0)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $BuildDirectory) { $buildOutput = & (Join-Path $PSScriptRoot 'Build.ps1'); $BuildDirectory = @($buildOutput)[-1] }
$testDirectory = Join-Path $root ('work\tests-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
$null = New-Item -ItemType Directory -Path $testDirectory
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testExe = Join-Path $testDirectory 'CoreTests.exe'
& $compiler /nologo /target:exe /platform:x64 /utf8output ('/out:' + $testExe) (Join-Path $root 'src\Contracts.cs') (Join-Path $root 'src\Lyrics.cs') (Join-Path $root 'tests\CoreTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
[IO.File]::WriteAllText((Join-Path $testDirectory 'utf8.lrc'), "[00:00]中文歌词", (New-Object Text.UTF8Encoding($false)))
[IO.File]::WriteAllText((Join-Path $testDirectory 'utf16.lrc'), "[00:00]中文歌词", [Text.Encoding]::Unicode)
[IO.File]::WriteAllBytes((Join-Path $testDirectory 'invalid.lrc'), [byte[]]@(0xff,0x80,0x81))
[IO.File]::WriteAllBytes((Join-Path $testDirectory 'oversized.lrc'), (New-Object byte[] (2 * 1024 * 1024 + 1)))
$log = & $testExe $testDirectory
$coreExit = $LASTEXITCODE
$log | Set-Content -LiteralPath (Join-Path $testDirectory 'core-tests.txt') -Encoding UTF8
$log | Write-Output
$uiArguments = @('--ui-smoke', ('"' + $testDirectory + '"'))
if ($DesktopNumber -gt 0) {
 $settings = Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops'
 $bytes = [byte[]]$settings.VirtualDesktopIDs
 $offset = ($DesktopNumber - 1) * 16
 if ($offset + 16 -gt $bytes.Length) { throw 'Requested desktop does not exist.' }
 $chunk = New-Object byte[] 16
 [Array]::Copy($bytes, $offset, $chunk, 0, 16)
 $uiArguments += @('--desktop', ([Guid]::new($chunk)).ToString())
}
$ui = Start-Process -FilePath (Join-Path $BuildDirectory 'AppleMusicLyrics.exe') -ArgumentList $uiArguments -PassThru -WindowStyle Hidden
if (-not $ui.WaitForExit(25000)) { $ui.Kill(); throw 'Window smoke test timed out.' }
if (Test-Path -LiteralPath (Join-Path $testDirectory 'ui-smoke.txt')) { Get-Content -LiteralPath (Join-Path $testDirectory 'ui-smoke.txt') }
if (Test-Path -LiteralPath (Join-Path $testDirectory 'ui-smoke-error.txt')) { Get-Content -LiteralPath (Join-Path $testDirectory 'ui-smoke-error.txt') }
if (Test-Path -LiteralPath (Join-Path $testDirectory 'startup-error.txt')) { Get-Content -LiteralPath (Join-Path $testDirectory 'startup-error.txt') }
Write-Output ('TEST_OUTPUT=' + $testDirectory)
if ($coreExit -ne 0 -or $ui.ExitCode -ne 0) { throw 'One or more checks failed.' }
