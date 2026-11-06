param([switch]$Once, [int]$ParentProcessId = 0)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager,Windows.Media.Control,ContentType=WindowsRuntime]
$null = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties,Windows.Media.Control,ContentType=WindowsRuntime]
$asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.IsGenericMethod -and $_.GetGenericArguments().Count -eq 1 -and
    $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
} | Select-Object -First 1
function Await-Operation($operation, [Type]$resultType) {
    $task = $asTask.MakeGenericMethod($resultType).Invoke($null, @($operation))
    if (-not $task.Wait(4000)) { throw 'Windows media request timed out.' }
    return $task.Result
}
function Get-PlaybackPosition([double]$Position, [double]$Duration, [double]$Age, [bool]$Playing, [double]$Rate, [bool]$Reliable) {
    # Both arguments must remain doubles: Max(0, age) selects an integer overload in Windows PowerShell.
    if ($Playing -and $Reliable) { $Position += [Math]::Max(0.0, $Age) * $Rate }
    return [Math]::Max(0.0, [Math]::Min($Duration, $Position))
}
$manager = $null
$cachedProperties = $null
$cachedSource = ''
$metadataReadAt = [DateTime]::MinValue
$previousPosition = 0.0
$previousDuration = 0.0
$owner = $null
if ($ParentProcessId -gt 0) { $owner = [Diagnostics.Process]::GetProcessById($ParentProcessId) }
while ($true) {
    if ($null -ne $owner -and $owner.HasExited) { break }
    try {
        if ($null -eq $manager) {
            $manager = Await-Operation ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager]::RequestAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager])
        }
        $sessions = @($manager.GetSessions())
        $apple = @($sessions | Where-Object { $_.SourceAppUserModelId -match '(?i)AppleMusic' })
        $session = $apple | Where-Object { $_.GetPlaybackInfo().PlaybackStatus.ToString() -eq 'Playing' } | Select-Object -First 1
        if ($null -eq $session) { $session = $apple | Select-Object -First 1 }
        if ($null -eq $session) {
            $result = @{ Status = 'Waiting'; Message = '等待 Apple Music'; Sources = @($sessions | ForEach-Object { $_.SourceAppUserModelId }) }
        } else {
            $earlyTimeline = $session.GetTimelineProperties()
            $source = $session.SourceAppUserModelId
            $newTrackBoundary = $earlyTimeline.Position.TotalSeconds -lt 0.5 -and $previousPosition -gt 2.0
            $durationChanged = [Math]::Abs($earlyTimeline.EndTime.TotalSeconds - $previousDuration) -gt 0.5
            if ($null -eq $cachedProperties -or $cachedSource -ne $source -or $newTrackBoundary -or $durationChanged -or ([DateTime]::UtcNow - $metadataReadAt).TotalMilliseconds -ge 500.0) {
                $cachedProperties = Await-Operation ($session.TryGetMediaPropertiesAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties])
                $cachedSource = $source
                $metadataReadAt = [DateTime]::UtcNow
            }
            $properties = $cachedProperties
            $playback = $session.GetPlaybackInfo()
            $timeline = $session.GetTimelineProperties()
            $now = [DateTimeOffset]::UtcNow
            $age = ($now - $timeline.LastUpdatedTime).TotalSeconds
            $duration = [double]$timeline.EndTime.TotalSeconds
            $position = [double]$timeline.Position.TotalSeconds
            $previousPosition = $position
            $previousDuration = $duration
            $playing = $playback.PlaybackStatus.ToString() -eq 'Playing'
            $rate = 1.0
            if ($null -ne $playback.PlaybackRate) { $rate = [double]$playback.PlaybackRate }
            $reliable = $duration -gt 0 -and $position -ge 0 -and $position -le ($duration + 1) -and $age -ge -1 -and $timeline.LastUpdatedTime.Year -gt 2000
            $position = Get-PlaybackPosition $position $duration $age $playing $rate $reliable
            $result = @{
                Status = 'Connected'; SourceAppId = $session.SourceAppUserModelId
                Title = $properties.Title; Artist = $properties.Artist; Album = $properties.AlbumTitle
                IsPlaying = $playing; PlaybackRate = $rate; PositionSeconds = $position
                DurationSeconds = $duration; TimelineReliable = $reliable
                TimelineUpdatedUtc = $timeline.LastUpdatedTime.ToString('o'); CapturedAtUtc = $now.ToString('o')
                PlaybackStatus = $playback.PlaybackStatus.ToString(); Sources = @($sessions | ForEach-Object { $_.SourceAppUserModelId })
            }
        }
        [Console]::WriteLine(($result | ConvertTo-Json -Compress -Depth 4))
    } catch {
        [Console]::WriteLine((@{ Status = 'Error'; Message = $_.Exception.Message } | ConvertTo-Json -Compress))
        $manager = $null
        $cachedProperties = $null
    }
    if ($Once) { break }
    Start-Sleep -Milliseconds 150
}