param([int]$Samples=3,[int]$IntervalMs=1000,[Int64]$RootHandle=0)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
function Find-Id($parent,[string]$id) {
 $c=New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty,$id)
 return $parent.FindFirst([Windows.Automation.TreeScope]::Descendants,$c)
}
$process=Get-Process -Name AppleMusic | Select-Object -First 1
if ($process.Path -notlike '*AppleInc.AppleMusicWin_1.1792.39682.0_x64__nzyj5cx40ttqa\AppleMusic.exe') { throw 'Unverified client' }
$sha=[Security.Cryptography.SHA256]::Create()
try {
 for($sample=0;$sample -lt $Samples;$sample++) {
  $handle=$process.MainWindowHandle
  if($RootHandle -ne 0){$handle=[IntPtr]$RootHandle}
  $root=[Windows.Automation.AutomationElement]::FromHandle($handle)
  if($root.Current.ProcessId -ne $process.Id){throw "Unexpected UIA process"}
  $bar=Find-Id $root 'TransportBar'
  $lcd=Find-Id $bar 'LCD'
  $metadata=@()
  $labelCondition=New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty,'ScrollingText')
  foreach($label in $lcd.FindAll([Windows.Automation.TreeScope]::Descendants,$labelCondition)) { $metadata += $label.Current.Name }
  $scrub=Find-Id $lcd 'LCDScrubber'
  $range=$scrub.GetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern).Current
  $panel=Find-Id $root 'TimeBasedLyrics'
  $rows=@()
  if($null -ne $panel) {
   $scroll=$panel.GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern).Current
   $children=$panel.FindAll([Windows.Automation.TreeScope]::Children,[Windows.Automation.Condition]::TrueCondition)
   $index=0
   foreach($child in $children) {
    $cur=$child.Current
    $text=[string]$cur.Name
    $hash=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($text)))).Replace('-','')
    $props=[ordered]@{}
    foreach($prop in $child.GetSupportedProperties()) {
     if($prop.ProgrammaticName -match 'NameProperty|BoundingRectangleProperty|RuntimeIdProperty|ProcessIdProperty|NativeWindowHandleProperty') {continue}
     $value=$child.GetCurrentPropertyValue($prop,$true)
     if($value -eq [Windows.Automation.AutomationElement]::NotSupported) {continue}
     if($value -is [string]) { if($value.Length -gt 120){$value=$value.Substring(0,120)}; $props[$prop.ProgrammaticName]=$value }
     elseif($value -is [ValueType]) { $props[$prop.ProgrammaticName]=[string]$value }
    }
    $rect=$cur.BoundingRectangle
    $bounds=$null
    if(-not $rect.IsEmpty){$bounds=@($rect.X,$rect.Y,$rect.Width,$rect.Height)}
    $rows += [ordered]@{index=$index; name_length=$text.Length; text_sha256=$hash; automation_id=$cur.AutomationId; class_name=$cur.ClassName; enabled=$cur.IsEnabled; offscreen=$cur.IsOffscreen; rect=$bounds; properties=$props; patterns=@($child.GetSupportedPatterns() | ForEach-Object ProgrammaticName)}
    $index++
    if($index -ge 120){break}
   }
  }
  $out=[ordered]@{schema='apple-lyrics-property-probe/v1'; observed_at=[DateTimeOffset]::UtcNow.ToString('o'); process_id=$process.Id; metadata=$metadata; position_raw=$range.Value; end_raw=$range.Maximum; panel_present=($null -ne $panel); scroll_percent=$null; rows=$rows; no_ui_actions=$true; lyrics_text_persisted=$false}
  if($null -ne $panel){$out.scroll_percent=$scroll.VerticalScrollPercent}
  [Console]::Out.WriteLine(($out|ConvertTo-Json -Depth 8 -Compress)); [Console]::Out.Flush()
  if($sample+1 -lt $Samples){Start-Sleep -Milliseconds $IntervalMs}
 }
} finally { $sha.Dispose() }
