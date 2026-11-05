param(
    [ValidateRange(50, 60000)][int]$PollIntervalMs = 500,
    [ValidateRange(100, 60000)][int]$AsyncTimeoutMs = 5000,
    [switch]$Once
)

# Windows PowerShell 5.1, system WinRT projection only. No player commands.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$utf8 = New-Object System.Text.UTF8Encoding($false)
[Console]::OutputEncoding = $utf8
$OutputEncoding = $utf8

function Write-JsonLine($Value) {
    [Console]::Out.WriteLine(($Value | ConvertTo-Json -Depth 10 -Compress))
    [Console]::Out.Flush()
}

function Write-BridgeError([string]$Message) {
    $record = [ordered]@{
        version = 1
        type = 'error'
        observed_at = [DateTimeOffset]::UtcNow.ToString('o')
        message = $Message
    }
    [Console]::Error.WriteLine(($record | ConvertTo-Json -Compress))
    [Console]::Error.Flush()
}

function Test-AppleMusicSource([string]$Source) {
    # Pinned to the observed installed Apple Music package; never fall back to
    # the system's current player or a title/artist substring match.
    return [string]::Equals(
        $Source, 'AppleInc.AppleMusicWin_nzyj5cx40ttqa!App',
        [StringComparison]::OrdinalIgnoreCase)
}

function Wait-WinRt($Operation, [Type]$ResultType) {
    $task = $script:asTask.MakeGenericMethod($ResultType).Invoke($null, @($Operation))
    if (-not $task.Wait($AsyncTimeoutMs)) {
        try { $Operation.Cancel() } catch { }
        throw "WinRT operation timed out after $AsyncTimeoutMs ms ($($ResultType.Name))"
    }
    return $task.GetAwaiter().GetResult()
}

function Read-Snapshot($Manager) {
    $current = $Manager.GetCurrentSession()
    $currentSource = $null
    if ($null -ne $current) { $currentSource = [string]$current.SourceAppUserModelId }
    $rows = @()
    $selected = $null
    foreach ($session in @($Manager.GetSessions())) {
        $source = [string]$session.SourceAppUserModelId
        $isApple = Test-AppleMusicSource $source
        $isCurrent = [string]::Equals($source, $currentSource, [StringComparison]::OrdinalIgnoreCase)
        $rows += [ordered]@{
            source_app = $source
            is_apple_music = $isApple
            is_current = $isCurrent
        }
        if ($isApple -and ($null -eq $selected -or $isCurrent)) { $selected = $session }
    }
    $snapshot = [ordered]@{
        version = 1
        type = 'snapshot'
        status = 'disconnected'
        reason = 'apple_music_session_not_found'
        observed_at = $null
        sessions = $rows
        manager_current_source = $currentSource
        selected_source = $null
        media = $null
        timeline = $null
        playback = $null
    }
    if ($null -ne $selected) {
        $media = Wait-WinRt ($selected.TryGetMediaPropertiesAsync()) $script:mediaType
        $timeline = $selected.GetTimelineProperties()
        $playback = $selected.GetPlaybackInfo()
        $rate = $null
        if ($null -ne $playback.PlaybackRate) { $rate = [double]$playback.PlaybackRate }
        $snapshot.status = 'connected'
        $snapshot.reason = $null
        $snapshot.selected_source = [string]$selected.SourceAppUserModelId
        $snapshot.media = [ordered]@{
            title = [string]$media.Title
            artist = [string]$media.Artist
            album = [string]$media.AlbumTitle
        }
        $snapshot.timeline = [ordered]@{
            last_updated_time = $timeline.LastUpdatedTime.ToString('o')
            position_ms = $timeline.Position.TotalMilliseconds
            start_ms = $timeline.StartTime.TotalMilliseconds
            end_ms = $timeline.EndTime.TotalMilliseconds
            min_seek_ms = $timeline.MinSeekTime.TotalMilliseconds
            max_seek_ms = $timeline.MaxSeekTime.TotalMilliseconds
        }
        $snapshot.playback = [ordered]@{
            status = [string]$playback.PlaybackStatus
            rate = $rate
        }
    }
    $snapshot.observed_at = [DateTimeOffset]::UtcNow.ToString('o')
    return $snapshot
}

try {
    Add-Type -AssemblyName System.Runtime.WindowsRuntime
    $script:managerType = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager, Windows.Media.Control, ContentType=WindowsRuntime]
    $script:mediaType = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties, Windows.Media.Control, ContentType=WindowsRuntime]
    $script:asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
        $_.Name -eq 'AsTask' -and $_.IsGenericMethod -and
        $_.GetGenericArguments().Length -eq 1 -and $_.GetParameters().Length -eq 1 -and
        $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
    } | Select-Object -First 1
    if ($null -eq $script:asTask) { throw 'System.Runtime.WindowsRuntime AsTask projection is unavailable' }
    $manager = $null
    do {
        try {
            if ($null -eq $manager) {
                $manager = Wait-WinRt ($script:managerType::RequestAsync()) $script:managerType
            }
            Write-JsonLine (Read-Snapshot $manager)
        } catch {
            $message = $_.Exception.ToString()
            Write-BridgeError $message
            Write-JsonLine ([ordered]@{
                version = 1; type = 'snapshot'; status = 'error'; reason = 'smtc_read_failed'
                observed_at = [DateTimeOffset]::UtcNow.ToString('o'); message = $message
                sessions = @(); manager_current_source = $null; selected_source = $null
                media = $null; timeline = $null; playback = $null
            })
            if ($Once) { exit 1 }
            $manager = $null
        }
        if (-not $Once) { Start-Sleep -Milliseconds $PollIntervalMs }
    } while (-not $Once)
} catch {
    Write-BridgeError ($_.Exception.ToString())
    exit 1
}
