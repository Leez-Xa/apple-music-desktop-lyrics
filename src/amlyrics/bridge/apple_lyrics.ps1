param([Parameter(Mandatory=$true)][Int64]$UiaRootHandle,[Parameter(Mandatory=$true)][int]$TargetProcessId)
# Read only: fixed verified package and fixed TimeBasedLyrics/TransportBar roots.
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
function Find-Id($parent,[string]$id) {
 if($null -eq $parent){return $null}
 $condition=New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty,$id)
 return $parent.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)
}
function Bounds($element) {
 $r=$element.Current.BoundingRectangle
 if($r.IsEmpty -or [double]::IsInfinity($r.X) -or [double]::IsInfinity($r.Y)){return $null}
 return @($r.X,$r.Y,$r.Width,$r.Height)
}
try {
 $process=Get-Process -Id $TargetProcessId
 $version=[Diagnostics.FileVersionInfo]::GetVersionInfo($process.Path).FileVersion
 $package=[IO.Path]::GetFileName([IO.Path]::GetDirectoryName($process.Path))
 if($version -ne '1.7.0.155' -or $package -ne 'AppleInc.AppleMusicWin_1.1792.39682.0_x64__nzyj5cx40ttqa'){throw 'Unverified client version'}
 $root=[Windows.Automation.AutomationElement]::FromHandle([IntPtr]$UiaRootHandle)
 if($root.Current.ProcessId -ne $TargetProcessId){throw 'Unexpected UIA process identity'}
 $bar=Find-Id $root 'TransportBar'
 $lcd=Find-Id $bar 'LCD'
 $panel=Find-Id $root 'TimeBasedLyrics'
 $metadata=@()
 if($null -ne $lcd){
  $condition=New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty,'ScrollingText')
  foreach($element in $lcd.FindAll([Windows.Automation.TreeScope]::Descendants,$condition)){
   if($element.Current.IsPassword){continue}
   $name=[string]$element.Current.Name
   if($name.Length -gt 512){throw 'Metadata exceeds limit'}
   $metadata+=$name
   if($metadata.Count -ge 2){break}
  }
 }
 $controls=@()
 if($null -ne $bar){
  foreach($child in $bar.FindAll([Windows.Automation.TreeScope]::Children,[Windows.Automation.Condition]::TrueCondition)){
   $rect=Bounds $child
   if($null -ne $rect){$controls += [ordered]@{automation_id=$child.Current.AutomationId; rect=$rect}}
  }
 }
 $lines=@()
 if($null -ne $panel -and $metadata.Count -eq 2){
  $index=0
  foreach($child in $panel.FindAll([Windows.Automation.TreeScope]::Children,[Windows.Automation.Condition]::TrueCondition)){
   if($child.Current.ControlType -ne [Windows.Automation.ControlType]::Button){continue}
   if($child.Current.IsPassword){continue}
   $text=[string]$child.Current.Name
   if($text.Length -gt 1024){throw 'Lyric line exceeds limit'}
   $lines += [ordered]@{index=$index;text=$text;rect=(Bounds $child)}
   $index++
   if($index -ge 300){throw 'Lyric line count exceeds limit'}
  }
 }
 $title=''; $subtitle=''
 if($metadata.Count -gt 0){$title=$metadata[0]}
 if($metadata.Count -gt 1){$subtitle=$metadata[1]}
 $status='unavailable';$reason='lyrics_panel_unavailable'
 if($null -ne $panel -and $metadata.Count -eq 2 -and $lines.Count -gt 0){$status='observed';$reason='timing_not_exposed'}
 if($metadata.Count -ne 2){$reason='playback_metadata_unavailable'}
 if($status -eq 'unavailable'){$title='';$subtitle='';$lines=@()}
 $out=[ordered]@{
  version=1;status=$status;reason=$reason;observed_at=[DateTimeOffset]::UtcNow.ToString('o')
  title=$title;subtitle=$subtitle;lines=$lines;timing_status='not_exposed';current_index=$null
  process_id=$TargetProcessId;uia_root_handle=$UiaRootHandle;file_version=$version;lyrics_rect=$null
  layout=[ordered]@{transport_rect=$null;controls=$controls}
 }
 if($null -ne $bar){$out.layout.transport_rect=Bounds $bar}
 if($null -ne $panel){$out.lyrics_rect=Bounds $panel}
 [Console]::Out.WriteLine(($out|ConvertTo-Json -Depth 8 -Compress))
} catch {
 # Do not leak lyric text, account values or provider exception strings.
 [Console]::Error.WriteLine('Read-only Apple lyric probe failed: '+$_.Exception.GetType().Name)
 exit 1
}
