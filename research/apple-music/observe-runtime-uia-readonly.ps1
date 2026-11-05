#requires -Version 5.1
<#
.SYNOPSIS
Read-only Apple Music process, user32 window and .NET UIAutomation observation.
.DESCRIPTION
Run under 64-bit Windows PowerShell 5.1. No UI actions, injection, network,
credentials, ValuePattern values, TextPattern text, screenshots or desktop UIA root.
All files are created with FileMode.CreateNew below this script's evidence directory.
Inventory and UIA are separate so a blocked UIA provider cannot lose native evidence.
Use a caller-side deadline for UIA; MaxSeconds is cooperative between provider calls.
.EXAMPLE
powershell.exe -NoProfile -NonInteractive -File .\observe-runtime-uia-readonly.ps1 -Phase Inventory -TargetProcessId 20072
.EXAMPLE
powershell.exe -NoProfile -NonInteractive -STA -File .\observe-runtime-uia-readonly.ps1 -Phase Uia -TargetProcessId 20072
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidateSet('Inventory','Uia')][string]$Phase,
    [int]$TargetProcessId = 0,
    [ValidateRange(1,2000)][int]$MaxNodes = 600,
    [ValidateRange(1,60)][int]$MaxDepth = 32,
    [ValidateRange(1,120)][int]$MaxSeconds = 35
)
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$started = [DateTimeOffset]::Now
$runId = $started.ToString('yyyyMMddTHHmmss.fffffffzzz').Replace(':','') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$evidenceDir = Join-Path $PSScriptRoot 'evidence'
[void][IO.Directory]::CreateDirectory($evidenceDir)
function Write-NewJson([string]$Suffix, $Value) {
    $destination = Join-Path $evidenceDir ($runId + '-' + $Phase.ToLowerInvariant() + '-' + $Suffix + '.json')
    $json = $Value | ConvertTo-Json -Depth 50
    $stream = [IO.File]::Open($destination,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    try { $writer = New-Object IO.StreamWriter($stream,(New-Object Text.UTF8Encoding($false))); $writer.Write($json); $writer.Flush() }
    finally { if ($writer) { $writer.Dispose() } else { $stream.Dispose() } }
    Write-Output $destination
}
function Safe-Label([string]$Value,[int]$Limit=96) {
    if ($null -eq $Value) { return $null }
    $value2 = [regex]::Replace($Value,'[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}','[email-redacted]')
    $value2 = [regex]::Replace($value2,'(?i)\b(?:bearer\s+\S+|(?:token|password|passwd|secret|cookie|authorization)\s*[:=]\s*\S+)','[sensitive-redacted]')
    if ($value2.Length -gt $Limit) { return ($value2.Substring(0,$Limit) + '[truncated]') }
    return $value2
}
function Error-Record($Exception,[string]$Stage) {
    return [ordered]@{ stage=$Stage; type=$Exception.GetType().FullName; hresult=$Exception.HResult; message=(Safe-Label $Exception.Message 240) }
}
$errors = New-Object 'Collections.Generic.List[object]'
$policy = [ordered]@{
    read_only=$true; target='AppleMusic process and verified descendants only'; no_ui_actions=$true
    no_foreground_activation=$true; no_desktop_uia_root=$true; no_network=$true
    no_account_values=$true; no_textpattern_text=$true; no_valuepattern_values=$true
    text_document_names='suppressed; no lyric transcription'; edit_password_names='suppressed'
    label_limit=96; per_control_errors='captured; inaccessible properties are not evidence of absence'
}
$header = [ordered]@{
    schema='apple-music-runtime-observation/v1'; observed_at=$started.ToString('o'); operation=('passive-' + $Phase.ToLowerInvariant())
    source='Windows PowerShell 5.1/.NET UIAutomation/user32/process modules'; classification='observed'
    collector=[ordered]@{ powershell=$PSVersionTable.PSVersion.ToString(); edition=$PSVersionTable.PSEdition; is_64bit=[Environment]::Is64BitProcess; apartment=[Threading.Thread]::CurrentThread.ApartmentState.ToString(); process_id=$PID; script_sha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash }
    policy=$policy
}
if ($PSVersionTable.PSVersion.Major -ne 5 -or -not [Environment]::Is64BitProcess) { throw 'Use 64-bit Windows PowerShell 5.1.' }
if ($TargetProcessId -gt 0) { $target = Get-Process -Id $TargetProcessId -ErrorAction Stop } else {
    $matches = @(Get-Process -Name AppleMusic -ErrorAction SilentlyContinue)
    if ($matches.Count -ne 1) { throw 'Expected exactly one AppleMusic process; pass a verified TargetProcessId.' }
    $target = $matches[0]
}
if ($target.ProcessName -ne 'AppleMusic') { throw 'Selected PID is not AppleMusic; refusing to inspect another process.' }
$targetPath = $target.Path
if ([IO.Path]::GetFileName($targetPath) -ne 'AppleMusic.exe') { throw 'Unable to verify AppleMusic executable path.' }
$header.target = [ordered]@{ process_id=$target.Id; name=$target.ProcessName; executable=$targetPath; start_time=$target.StartTime.ToString('o'); file_version=$target.MainModule.FileVersionInfo.FileVersion; product_version=$target.MainModule.FileVersionInfo.ProductVersion; package_directory=[IO.Path]::GetFileName([IO.Path]::GetDirectoryName($targetPath)) }
$allowedIds = New-Object 'Collections.Generic.List[int]'
$allowedIds.Add($target.Id)
$descendants = New-Object 'Collections.Generic.List[object]'
$frontier = @($target.Id)
for ($generation=1; $generation -le 4 -and $frontier.Count -gt 0; $generation++) {
    $next = @()
    foreach ($parentId in $frontier) {
        try {
            $children = @(Get-CimInstance Win32_Process -Filter ('ParentProcessId = ' + $parentId) | Where-Object { $_.CreationDate -ge $target.StartTime })
            foreach ($child in $children) {
                $childId = [int]$child.ProcessId
                if (-not $allowedIds.Contains($childId)) {
                    $allowedIds.Add($childId); $next += $childId
                    $descendants.Add([ordered]@{ process_id=$childId; parent_process_id=[int]$child.ParentProcessId; name=$child.Name; executable=$child.ExecutablePath; generation=$generation; creation_time=$child.CreationDate.ToString('o'); command_line='not read' })
                }
            }
        } catch { $errors.Add((Error-Record $_.Exception 'descendant-process-query')) }
    }
    $frontier = @($next)
}
$header.descendants = @($descendants.ToArray())
Write-NewJson 'started' $header
# Compile only this observer's P/Invoke declarations into the observer process.
# CodeDOM scratch is restricted to a newly created, regenerable research/work folder.
$buildDir = Join-Path $PSScriptRoot ('work\' + $runId + '-native-compiler')
[void][IO.Directory]::CreateDirectory($buildDir)
$nativeSource = @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class ReadOnlyAppleWindows {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    public class WindowRow {
        public long Handle; public long Parent; public uint ProcessId; public uint ThreadId;
        public string ClassName; public string Title; public bool Visible; public bool Minimized;
        public bool TopLevel; public int Left, Top, Right, Bottom;
    }
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr p, EnumProc f, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint f);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out Rect r);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    public static bool IsTargetForeground(int[] ids) { uint p; GetWindowThreadProcessId(GetForegroundWindow(), out p); return Array.IndexOf(ids,(int)p)>=0; }
    public static WindowRow[] Snapshot(int[] ids) {
        List<WindowRow> rows = new List<WindowRow>(); HashSet<long> seen = new HashSet<long>();
        EnumProc collect = delegate(IntPtr h, IntPtr ignored) {
            uint p; uint t=GetWindowThreadProcessId(h,out p);
            if (Array.IndexOf(ids,(int)p)<0 || !seen.Add(h.ToInt64())) return true;
            StringBuilder c = new StringBuilder(256); GetClassNameW(h,c,c.Capacity);
            // Child window text can contain lyrics/input; collect only top-level labels.
            bool top = GetAncestor(h,2)==h; StringBuilder s = new StringBuilder(100);
            if (top) GetWindowTextW(h,s,s.Capacity);
            Rect r; GetWindowRect(h,out r);
            rows.Add(new WindowRow {Handle=h.ToInt64(), Parent=GetAncestor(h,1).ToInt64(), ProcessId=p,ThreadId=t,ClassName=c.ToString(),Title=s.ToString(),Visible=IsWindowVisible(h),Minimized=IsIconic(h),TopLevel=top,Left=r.Left,Top=r.Top,Right=r.Right,Bottom=r.Bottom});
            return true;
        };
        // Enumerate handles globally; inspect labels/classes only after PID ownership passes.
        EnumWindows(delegate(IntPtr h, IntPtr ignored) { uint p; GetWindowThreadProcessId(h,out p); if (Array.IndexOf(ids,(int)p)>=0) { collect(h,IntPtr.Zero); EnumChildWindows(h,collect,IntPtr.Zero); } return true; },IntPtr.Zero);
        return rows.ToArray();
    }
}
"@
try {
    $compiler = New-Object Microsoft.CSharp.CSharpCodeProvider
    $parameters = New-Object CodeDom.Compiler.CompilerParameters
    $parameters.GenerateInMemory = $true
    $parameters.GenerateExecutable = $false
    $parameters.IncludeDebugInformation = $false
    $parameters.TempFiles = New-Object CodeDom.Compiler.TempFileCollection($buildDir,$false)
    [void]$parameters.ReferencedAssemblies.Add('System.dll')
    $compiled = $compiler.CompileAssemblyFromSource($parameters,$nativeSource)
    if ($compiled.Errors.HasErrors) { throw (($compiled.Errors | ForEach-Object { $_.ToString() }) -join '; ') }
    $null = $compiled.CompiledAssembly
} finally { if ($compiler) { $compiler.Dispose() } }
$windows = @([ReadOnlyAppleWindows]::Snapshot($allowedIds.ToArray()) | ForEach-Object {
    [ordered]@{ handle=('0x{0:X}' -f $_.Handle); handle_decimal=$_.Handle; parent_handle=('0x{0:X}' -f $_.Parent); process_id=$_.ProcessId; thread_id=$_.ThreadId; class_name=$_.ClassName; title=(Safe-Label $_.Title); top_level=$_.TopLevel; visible=$_.Visible; minimized=$_.Minimized; rect=@($_.Left,$_.Top,$_.Right,$_.Bottom) }
})
$header.windows = $windows
$header.target_foreground_at_snapshot = [ReadOnlyAppleWindows]::IsTargetForeground($allowedIds.ToArray())
if ($Phase -eq 'Inventory') {
    $moduleSets = New-Object 'Collections.Generic.List[object]'
    foreach ($processId in $allowedIds.ToArray()) {
        $moduleRows = New-Object 'Collections.Generic.List[object]'
        try {
            $proc = Get-Process -Id $processId
            foreach ($module in $proc.Modules) {
                $moduleRows.Add([ordered]@{ name=$module.ModuleName; path=$module.FileName; file_version=$module.FileVersionInfo.FileVersion; product_version=$module.FileVersionInfo.ProductVersion })
            }
        } catch { $errors.Add((Error-Record $_.Exception ('module-enumeration-' + $processId))) }
        $moduleSets.Add([ordered]@{ process_id=$processId; modules=@($moduleRows.ToArray()) })
    }
    $header.module_sets = @($moduleSets.ToArray())
    $header.errors = @($errors.ToArray())
    $header.completed_at = [DateTimeOffset]::Now.ToString('o')
    Write-NewJson 'snapshot' $header
    exit 0
}
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName WindowsBase
$nodes = New-Object 'Collections.Generic.List[object]'
$rootResults = New-Object 'Collections.Generic.List[object]'
$limits = [ordered]@{ max_nodes=$MaxNodes; max_depth=$MaxDepth; cooperative_seconds=$MaxSeconds; caller_watchdog_required=$true; node_limit_hit=$false; depth_limit_hit=$false; time_limit_hit=$false }
$clock = [Diagnostics.Stopwatch]::StartNew()
$walker = [Windows.Automation.TreeWalker]::RawViewWalker
function Read-UiaNode($Element,[int]$ParentIndex,[int]$Depth,[string]$RootHandle) {
    if ($nodes.Count -ge $MaxNodes) { $limits.node_limit_hit=$true; return }
    if ($clock.Elapsed.TotalSeconds -ge $MaxSeconds) { $limits.time_limit_hit=$true; return }
    if ($Depth -gt $MaxDepth) { $limits.depth_limit_hit=$true; return }
    $nodeErrors = New-Object 'Collections.Generic.List[object]'
    try {
        $info = $Element.Current
        $ownerId = [int]$info.ProcessId
        # Foreign provider elements are pruned before any name or other UI content is read.
        if (-not $allowedIds.Contains($ownerId)) { $errors.Add([ordered]@{ stage='uia-owner-filter'; foreign_process_id=$ownerId; action='pruned-before-content'; root_handle=$RootHandle }); return }
        $type = $info.ControlType.ProgrammaticName
        $automationId = $info.AutomationId
        $className = $info.ClassName
        $isSensitive = $info.IsPassword -or $type -in @('ControlType.Edit','ControlType.Document','ControlType.Text') -or ($automationId + ' ' + $className) -match '(?i)lyric|password|credential|account|email|cookie|token|webarea|webcontent|webview'
        if ($isSensitive) { $name=$null; $nameStatus='suppressed-text-or-sensitive-context' }
        else { $name = Safe-Label $info.Name; $nameStatus='bounded-label' }
        $patternNames = @()
        try { $patternNames = @($Element.GetSupportedPatterns() | ForEach-Object { $_.ProgrammaticName }) }
        catch { $nodeErrors.Add((Error-Record $_.Exception 'supported-patterns')) }
        $state = [ordered]@{}
        foreach ($patternName in $patternNames) {
            # State reads only; do not invoke controls or retrieve text/value payloads.
            try {
                switch ($patternName) {
                    'TogglePatternIdentifiers.Pattern' { $p=$Element.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern); $state.toggle=$p.Current.ToggleState.ToString() }
                    'SelectionItemPatternIdentifiers.Pattern' { $p=$Element.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern); $state.selected=$p.Current.IsSelected }
                    'RangeValuePatternIdentifiers.Pattern' { $p=$Element.GetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern); $state.range=[ordered]@{ value=$p.Current.Value; minimum=$p.Current.Minimum; maximum=$p.Current.Maximum; is_read_only=$p.Current.IsReadOnly } }
                    'ExpandCollapsePatternIdentifiers.Pattern' { $p=$Element.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern); $state.expand_collapse=$p.Current.ExpandCollapseState.ToString() }
                }
            } catch { $nodeErrors.Add((Error-Record $_.Exception ('pattern-state-' + $patternName))) }
        }
        $rect = $info.BoundingRectangle
        $index = $nodes.Count
        $nodes.Add([ordered]@{ index=$index; parent_index=$ParentIndex; depth=$Depth; root_handle=$RootHandle; process_id=$ownerId; name=$name; name_status=$nameStatus; automation_id=$automationId; framework_id=$info.FrameworkId; control_type=$type; class_name=$className; native_window_handle=$info.NativeWindowHandle; runtime_id=@($Element.GetRuntimeId()); enabled=$info.IsEnabled; offscreen=$info.IsOffscreen; has_keyboard_focus=$info.HasKeyboardFocus; keyboard_focusable=$info.IsKeyboardFocusable; is_password=$info.IsPassword; rect=@($rect.X,$rect.Y,$rect.Width,$rect.Height); patterns=$patternNames; readonly_state=$state; errors=@($nodeErrors.ToArray()) })
        if ($Depth -ge $MaxDepth) { $limits.depth_limit_hit=$true; return }
        try { $child=$walker.GetFirstChild($Element) }
        catch { $errors.Add((Error-Record $_.Exception ('uia-first-child-' + $index))); return }
        while ($null -ne $child) {
            Read-UiaNode $child $index ($Depth+1) $RootHandle
            if ($limits.node_limit_hit -or $limits.time_limit_hit) { return }
            try { $child=$walker.GetNextSibling($child) }
            catch { $errors.Add((Error-Record $_.Exception ('uia-next-sibling-' + $index))); return }
        }
    } catch { $errors.Add((Error-Record $_.Exception ('uia-node-depth-' + $Depth))) }
}
foreach ($window in @($windows | Where-Object { $_.top_level })) {
    if ($clock.Elapsed.TotalSeconds -ge $MaxSeconds) { $limits.time_limit_hit=$true; break }
    $before = $nodes.Count
    try {
        $element = [Windows.Automation.AutomationElement]::FromHandle([IntPtr]$window.handle_decimal)
        if ($null -eq $element) { throw 'UIAutomation returned null from target window handle.' }
        Read-UiaNode $element -1 0 $window.handle
        $rootResults.Add([ordered]@{ handle=$window.handle; visible=$window.visible; nodes_added=($nodes.Count-$before); status='traversal-returned-check-errors-and-limits' })
    } catch { $errors.Add((Error-Record $_.Exception ('uia-from-handle-' + $window.handle))); $rootResults.Add([ordered]@{ handle=$window.handle; nodes_added=0; status='failed' }) }
}
$header.uia_roots = @($rootResults.ToArray())
$header.uia_nodes = @($nodes.ToArray())
$header.limits = $limits
$header.errors = @($errors.ToArray())
$header.completed_at = [DateTimeOffset]::Now.ToString('o')
$header.elapsed_uia_seconds = $clock.Elapsed.TotalSeconds
$header.playback_observation = 'No playback action or SMTC query; infer only cautiously from accessible controls if present.'
Write-NewJson 'snapshot' $header