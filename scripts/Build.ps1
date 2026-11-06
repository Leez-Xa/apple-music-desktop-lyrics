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
$references = @('System.dll','System.Core.dll','System.Net.Http.dll','System.Xaml.dll','System.Web.Extensions.dll','System.Drawing.dll','System.Windows.Forms.dll','WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll')
$arguments = @('/nologo','/target:winexe','/platform:x64','/optimize+','/warn:4','/utf8output',('/out:' + (Join-Path $OutputDirectory 'AppleMusicLyrics.exe')))
foreach ($reference in $references) {
 $path = Join-Path $framework $reference
 if (-not (Test-Path -LiteralPath $path)) { $path = Join-Path (Join-Path $framework 'WPF') $reference }
 $arguments += '/reference:' + $path
}
$arguments += @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Application compilation failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'MediaSession.ps1') -Destination (Join-Path $OutputDirectory 'MediaSession.ps1') -ErrorAction Stop
Write-Output $OutputDirectory
