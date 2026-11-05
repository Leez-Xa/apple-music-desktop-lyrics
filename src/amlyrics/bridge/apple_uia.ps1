param([Int64]$UiaRootHandle=0)
# Read only. Fixed, evidence-backed transport IDs; no UI actions or whole-tree text.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class AppleUiaNative {
 [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
}
'@
function Emit($value) { [Console]::Out.WriteLine(($value | ConvertTo-Json -Depth 8 -Compress)) }
function Find-Id($parent, [string]$id) {
 $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
 return $parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
try {
 $process = @(Get-Process -Name AppleMusic -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Sort-Object Id) | Select-Object -First 1
 if ($null -eq $process) {
  Emit ([ordered]@{version=1; status='unavailable'; reason='apple_music_window_not_found'})
  exit 0
 }
 $path = $process.Path
 $version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($path).FileVersion
 $package = [System.IO.Path]::GetFileName([System.IO.Path]::GetDirectoryName($path))
 if ($version -ne '1.7.0.155' -or $package -ne 'AppleInc.AppleMusicWin_1.1792.39682.0_x64__nzyj5cx40ttqa') {
  Emit ([ordered]@{version=1; status='unsupported'; reason='unverified_client_version'; file_version=$version; package_directory=$package})
  exit 0
 }
 $uiaHandle=$process.MainWindowHandle
 if($UiaRootHandle -ne 0){$uiaHandle=[IntPtr]$UiaRootHandle}
 $root = [System.Windows.Automation.AutomationElement]::FromHandle($uiaHandle)
 if ($root.Current.ProcessId -ne $process.Id) { throw 'Unexpected automation process identity' }
 $bar = Find-Id $root 'TransportBar'
 if ($null -eq $bar) { throw 'Evidence-backed TransportBar not found' }
 $controls = @()
 foreach ($id in @('TransportControl_PlayPauseStop','LCDScrubber','LyricsToggleButton','PlayQueueToggleButton')) {
  $element = Find-Id $bar $id
  if ($null -eq $element) { continue }
  $current = $element.Current
  $rect = $current.BoundingRectangle
  $record = [ordered]@{
   automation_id=$current.AutomationId; framework_id=$current.FrameworkId
   control_type=$current.ControlType.ProgrammaticName; enabled=$current.IsEnabled
   offscreen=$current.IsOffscreen; rect=@($rect.X,$rect.Y,$rect.Width,$rect.Height)
  }
  if ($id -eq 'TransportControl_PlayPauseStop') { $record['name'] = $current.Name }
  if ($id -eq 'LCDScrubber') {
   $range = $element.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).Current
   $record['range'] = [ordered]@{value=$range.Value; minimum=$range.Minimum; maximum=$range.Maximum; is_read_only=$range.IsReadOnly}
  }
  if ($id -eq 'LyricsToggleButton' -or $id -eq 'PlayQueueToggleButton') {
   $record['toggle'] = [string]$element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState
  }
  $controls += $record
 }
 Emit ([ordered]@{
  version=1; status='observed'; observed_at=[DateTimeOffset]::UtcNow.ToString('o')
  process_id=$process.Id; file_version=$version; package_directory=$package
  root_class=$root.Current.ClassName; root_framework=$root.Current.FrameworkId
  window_handle=$process.MainWindowHandle.ToInt64(); minimized=[AppleUiaNative]::IsIconic($process.MainWindowHandle)
  transport_framework=$bar.Current.FrameworkId; controls=$controls
  lyrics_timeline='not_observed'; mode='read_only'
 })
} catch {
 [Console]::Error.WriteLine('UI Automation observation failed: ' + $_.Exception.GetType().Name)
 exit 1
}
