$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Write-Output '=== Read-only Windows media session probe ==='
& "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'MediaSession.ps1') -Once
Write-Output '=== Read-only Apple Music accessibility probe (no actions invoked) ==='
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$apple = @(Get-Process -Name AppleMusic -ErrorAction SilentlyContinue)
if ($apple.Count -eq 0) { Write-Output 'Apple Music is not running; native lyrics cannot be verified.'; exit 0 }
foreach ($process in $apple) {
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
    $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $condition)
    foreach ($window in $windows) {
        Write-Output ('Window: ' + $window.Current.Name)
        $lyricsCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, '歌词')
        $lyricsPanes = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $lyricsCondition)
        foreach ($pane in $lyricsPanes) {
            if ($pane.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button) { continue }
            $descendants = $pane.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
            Write-Output ('Native lyrics pane: ' + $pane.Current.ControlType.ProgrammaticName + '; descendant count=' + $descendants.Count)
        }
        $queue = New-Object 'System.Collections.Generic.Queue[System.Windows.Automation.AutomationElement]'
        $queue.Enqueue($window)
        $seen = 0
        $watch = [Diagnostics.Stopwatch]::StartNew()
        while ($queue.Count -gt 0 -and $seen -lt 180 -and $watch.Elapsed.TotalSeconds -lt 12) {
            $element = $queue.Dequeue()
            try {
                $current = $element.Current
                Write-Output ('{0} | {1} | {2}' -f $current.ControlType.ProgrammaticName, $current.AutomationId, $current.Name)
                $children = $element.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
                foreach ($child in $children) { if ($queue.Count -lt 200) { $queue.Enqueue($child) } }
            } catch { Write-Output ('Element unavailable: ' + $_.Exception.Message) }
            $seen++
        }
        Write-Output ('Inspected nodes: ' + $seen + '; this inventory does not establish synchronized lyric timestamps.')
    }
}