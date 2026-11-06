$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$apple = Get-Process -Name AppleMusic -ErrorAction Stop | Select-Object -First 1
$rootElement = [System.Windows.Automation.AutomationElement]::FromHandle($apple.MainWindowHandle)
Write-Output ('Window handle: ' + $apple.MainWindowHandle)
$queue = New-Object 'System.Collections.Generic.Queue[System.Windows.Automation.AutomationElement]'
$queue.Enqueue($rootElement)
$walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
$watch = [Diagnostics.Stopwatch]::StartNew()
$count = 0
$lyricsHits = 0
while ($queue.Count -gt 0 -and $count -lt 1800 -and $watch.Elapsed.TotalSeconds -lt 15) {
 $element = $queue.Dequeue()
 try {
  $c = $element.Current
  if ($c.Name -match '歌词|Lyrics' -or $c.AutomationId -match 'Lyric') {
   $lyricsHits++
   Write-Output ('LYRICS NODE: ' + $c.ControlType.ProgrammaticName + ' | ' + $c.AutomationId + ' | ' + $c.Name)
   Write-Output ('Patterns: ' + (($element.GetSupportedPatterns() | ForEach-Object { $_.ProgrammaticName }) -join ','))
   $descendants = $element.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
   Write-Output ('Control descendants: ' + $descendants.Count)
  }
  $child = $walker.GetFirstChild($element)
  $siblings = 0
  while ($null -ne $child -and $queue.Count -lt 1800 -and $siblings -lt 300) {
   $queue.Enqueue($child)
   $child = $walker.GetNextSibling($child)
   $siblings++
  }
 } catch { Write-Output ('Read unavailable: ' + $_.Exception.Message) }
 $count++
}
Write-Output ('Raw UIA nodes inspected: ' + $count + '; lyric-related nodes: ' + $lyricsHits)
