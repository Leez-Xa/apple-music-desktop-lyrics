$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'scripts\MediaSession.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw 'Media script syntax error.' }
$definition = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-PlaybackPosition' }, $true)
if ($null -eq $definition) { throw 'Playback position function was not found.' }
# Extract the actual pure function so this regression check never opens a media session.
Invoke-Expression $definition.Extent.Text
function Assert-Near([double]$Actual, [double]$Expected, [string]$Name) {
 if ([Math]::Abs($Actual - $Expected) -gt 0.000001) { throw "$Name : got $Actual, expected $Expected" }
 Write-Output ('PASS ' + $Name)
}
Assert-Near (Get-PlaybackPosition 10.125 200.0 0.35 $true 1.0 $true) 10.475 'PowerShell preserves subsecond position and age'
Assert-Near (Get-PlaybackPosition 10.125 200.0 0.75 $false 1.0 $true) 10.125 'Paused clock preserves exact fraction'
Assert-Near (Get-PlaybackPosition 10.125 200.0 0.75 $true 1.0 $false) 10.125 'Unreliable clock is not extrapolated'
Assert-Near (Get-PlaybackPosition 10.125 200.0 -0.1 $true 1.0 $true) 10.125 'Small future timestamp does not rewind'
Assert-Near (Get-PlaybackPosition 10.125 200.0 0.35 $true 2.0 $true) 10.825 'Fractional interpolation respects playback rate'
Assert-Near (Get-PlaybackPosition 199.875 200.0 0.35 $true 1.0 $true) 200.0 'Position clamps at duration'
Assert-Near (Get-PlaybackPosition -1.25 200.0 0.0 $false 1.0 $true) 0.0 'Position clamps below zero'
