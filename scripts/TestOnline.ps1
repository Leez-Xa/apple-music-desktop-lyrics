$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$folder = Join-Path $root ('work\online-tests-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
$null = New-Item -ItemType Directory -Path $folder
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$exe = Join-Path $folder 'OnlineTests.exe'
& (Join-Path $framework 'csc.exe') /nologo /target:exe /platform:x64 /utf8output ('/reference:' + (Join-Path $framework 'System.Net.Http.dll')) ('/reference:' + (Join-Path $framework 'System.Web.Extensions.dll')) ('/out:' + $exe) (Join-Path $root 'src\Contracts.cs') (Join-Path $root 'src\Lyrics.cs') (Join-Path $root 'src\LrclibLyrics.cs') (Join-Path $root 'tests\OnlineTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Online provider test compilation failed.' }
$log = & $exe
$code = $LASTEXITCODE
$log | Set-Content -LiteralPath (Join-Path $folder 'online-tests.txt') -Encoding UTF8
$log | Write-Output
Write-Output ('ONLINE_TEST_OUTPUT=' + $folder)
if ($code -ne 0) { throw 'Online provider tests failed.' }
