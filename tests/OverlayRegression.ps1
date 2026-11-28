[CmdletBinding()]
param(
 [Parameter(Mandatory = $true)][string]$EvidenceDirectory,
 [string]$BuildDirectory
)

$ErrorActionPreference = 'Stop'

function Read-OverlayPng([string]$Path) {
 $stream = [IO.File]::OpenRead($Path)
 try {
  $decoder = [System.Windows.Media.Imaging.PngBitmapDecoder]::new(
   $stream, [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
   [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
  $image = [System.Windows.Media.Imaging.FormatConvertedBitmap]::new(
   $decoder.Frames[0], [System.Windows.Media.PixelFormats]::Pbgra32, $null, 0)
  $stride = $image.PixelWidth * 4
  $pixels = [byte[]]::new($stride * $image.PixelHeight)
  $image.CopyPixels($pixels, $stride, 0)
  [pscustomobject]@{ Width = $image.PixelWidth; Height = $image.PixelHeight; Pixels = $pixels }
 } finally {
  $stream.Dispose()
 }
}

function Get-LyricBands($Image) {
 $pixels = $Image.Pixels
 $band = $null
 for ($row = 0; $row -lt $Image.Height; $row++) {
  $rowStart = $row * $Image.Width * 4
  $hasInk = $false
  for ($column = 0; $column -lt $Image.Width; $column++) {
   $offset = $rowStart + $column * 4
   if ($pixels[$offset + 3] -eq 0) { continue }
   $hasInk = $true
   if ($null -eq $band) {
    $band = [pscustomobject]@{ Top = $row; Bottom = $row; Left = $column; Right = $column; FillBottom = -1 }
   }
   if ($column -lt $band.Left) { $band.Left = $column }
   if ($column -gt $band.Right) { $band.Right = $column }
   if ($pixels[$offset] -ge 145 -or $pixels[$offset + 1] -ge 145 -or $pixels[$offset + 2] -ge 145) {
    $band.FillBottom = $row
   }
  }
  if ($hasInk) { $band.Bottom = $row }
  elseif ($null -ne $band) { $band; $band = $null }
 }
 if ($null -ne $band) { $band }
}

function Assert-HoverToolbar($Toolbar, [string]$Label) {
 $width = $Toolbar.Width
 if ($width -ne 108) { throw "${Label}: expected a 108px-wide popup-root image, got ${width}px." }
 $pixels = $Toolbar.Pixels
 $buttons = 0
 $firstColumn = -1
 $lastColumn = -1
 $hasPreviousColumn = $false
 for ($column = 0; $column -lt $width; $column++) {
  $hasColumn = $false
  for ($row = 0; $row -lt $Toolbar.Height; $row++) {
   if ($pixels[($row * $width + $column) * 4 + 3] -ne 0) { $hasColumn = $true; break }
  }
  if ($hasColumn) {
   if ($firstColumn -lt 0) { $firstColumn = $column }
   $lastColumn = $column
   if (-not $hasPreviousColumn) { $buttons++ }
  }
  $hasPreviousColumn = $hasColumn
 }
 if ($buttons -ne 3) { throw "${Label}: expected three separate visible toolbar buttons, found $buttons." }
 $rightClearance = $width - 1 - $lastColumn
 if ($firstColumn -lt 1 -or $rightClearance -lt 1 -or [math]::Abs($firstColumn - $rightClearance) -gt 2) {
  throw "${Label}: toolbar buttons are clipped or not centered in the popup image."
 }
}

function Assert-HoverComposite($Idle, $Hover, $Bands, [string]$Label) {
 $width = $Idle.Width
 $idlePixels = $Idle.Pixels
 $hoverPixels = $Hover.Pixels
 $left = 14
 $right = $width - 15
 for ($row = 0; $row -lt $Idle.Height; $row++) {
  $rowStart = $row * $width * 4
  $backdrop = $rowStart + $left * 4
  $farSide = $rowStart + $right * 4
  if ($idlePixels[$backdrop + 3] -ne 0 -or $idlePixels[$farSide + 3] -ne 0) {
   throw "${Label}: idle overlay is not transparent beside the lyrics at row $row."
  }
  if ($row -le $Bands[1].Bottom + 2 -and ($hoverPixels[$backdrop + 3] -eq 0 -or $hoverPixels[$farSide + 3] -eq 0)) {
   throw "${Label}: hover backdrop is missing beside the lyrics at row $row."
  }
  for ($column = $left + 1; $column -le $right; $column++) {
   $offset = $rowStart + $column * 4
   $remaining = (255 - $idlePixels[$offset + 3]) / 255.0
   for ($channel = 0; $channel -lt 4; $channel++) {
    $expected = $idlePixels[$offset + $channel] + $hoverPixels[$backdrop + $channel] * $remaining
    if ([math]::Abs($hoverPixels[$offset + $channel] - $expected) -gt 6) {
     throw "${Label}: hover changes the lyric render or adds unexpected content at ($column, $row)."
    }
   }
  }
 }
}

function Test-OverlayPair($Idle, $Hover, [int]$ExpectedWidth, [string]$Label) {
 if ($Idle.Width -ne $ExpectedWidth -or $Idle.Height -lt 64 -or $Idle.Height -gt 260 -or
     $Hover.Width -ne $Idle.Width -or $Hover.Height -ne $Idle.Height) {
  throw "${Label}: expected ${ExpectedWidth}px width, height within 64-260px, and matching idle/hover dimensions; got $($Idle.Width)x$($Idle.Height) and $($Hover.Width)x$($Hover.Height)."
 }
 $bands = @(Get-LyricBands $Idle)
 if ($bands.Count -ne 2) { throw "${Label}: expected exactly two nontransparent lyric bands in idle, found $($bands.Count)." }
 foreach ($band in $bands) {
  $center = ($band.Left + $band.Right) / 2.0
  if ($band.Left -le 14 -or $band.Right -ge ($Idle.Width - 15) -or $band.Top -lt 1 -or
      $band.Bottom -ge ($Idle.Height - 4) -or [math]::Abs($center - ($Idle.Width - 1) / 2.0) -gt 12) {
   throw "${Label}: lyric band is not centered or has insufficient window-edge clearance: x=$($band.Left)..$($band.Right), y=$($band.Top)..$($band.Bottom)."
  }
  if ($band.FillBottom -lt $band.Top -or $band.FillBottom -ge $band.Bottom) {
   throw "${Label}: expected visible foreground and a lower ink fringe in the band at y=$($band.Top)..$($band.Bottom)."
  }
 }
 if ($bands[1].Top - $bands[0].Bottom -lt 3 -or
     ($bands[0].Bottom - $bands[0].Top) -le ($bands[1].Bottom - $bands[1].Top)) {
  throw "${Label}: lyric bands need a clear gap and a taller current line."
 }

 Assert-HoverComposite $Idle $Hover $bands $Label
 [pscustomobject]@{ Width = $Idle.Width; Height = $Idle.Height; Bands = $bands }
}

$evidence = Get-Item -LiteralPath $EvidenceDirectory
if (-not $evidence.PSIsContainer) { throw 'EvidenceDirectory must be a directory.' }
Write-Output "EVIDENCE $($evidence.FullName)"
if ($BuildDirectory) {
 $build = Get-Item -LiteralPath $BuildDirectory
 if (-not $build.PSIsContainer) { throw 'BuildDirectory must be a directory.' }
 $executable = Get-Item -LiteralPath (Join-Path $build.FullName 'AppleMusicLyrics.exe')
 if ($executable.PSIsContainer) { throw 'AppleMusicLyrics.exe must be a file.' }
 $hash = Get-FileHash -LiteralPath $executable.FullName -Algorithm SHA256
 Write-Output "BUILD $($executable.FullName) SHA256=$($hash.Hash) (evidence provenance is recorded separately)"
}

foreach ($errorLog in @('ui-smoke-error.txt', 'startup-error.txt')) {
 if (Test-Path -LiteralPath (Join-Path $evidence.FullName $errorLog)) { throw "Smoke reported an error; see $errorLog." }
}
$smokeLog = Join-Path $evidence.FullName 'ui-smoke.txt'
if (Test-Path -LiteralPath $smokeLog -PathType Leaf) {
 $smokeChecks = @(Get-Content -LiteralPath $smokeLog)
 $passed = @($smokeChecks -match '^PASS ').Count
 if ($passed -eq 0 -or ($smokeChecks -match '^FAIL ')) { throw 'Smoke log is empty or reports failed checks.' }
 Write-Output "PASS accompanying smoke log ($passed checks)"
} else {
 Write-Output 'NOT CHECKED accompanying smoke log (ui-smoke.txt not present)'
}

$null = Add-Type -AssemblyName WindowsBase, PresentationCore
$results = @{}
foreach ($scenario in @(@{ Name = 'default'; Width = 760 }, @{ Name = 'max-narrow'; Width = 420 })) {
 $name = $scenario.Name
 $idle = Read-OverlayPng (Join-Path $evidence.FullName "lyrics-$name-idle.png")
 $hover = Read-OverlayPng (Join-Path $evidence.FullName "lyrics-$name-hover.png")
 $toolbar = Read-OverlayPng (Join-Path $evidence.FullName "lyrics-$name-toolbar.png")
 $result = Test-OverlayPair $idle $hover $scenario.Width $name
 Assert-HoverToolbar $toolbar $name
 $results[$name] = $result
 $locations = @($result.Bands | ForEach-Object { "x=$($_.Left)..$($_.Right) y=$($_.Top)..$($_.Bottom)" }) -join '; '
 Write-Output "PASS $name $($result.Width)x$($result.Height): two centered lyric bands ($locations), transparent idle, hover backdrop only in main image, unchanged lyric pixels, three separate popup buttons"
}

$default = $results['default']
$maximum = $results['max-narrow']
if ($maximum.Height -le $default.Height) { throw 'Maximum-size overlay did not grow vertically.' }
for ($track = 0; $track -lt 2; $track++) {
 $defaultBand = $default.Bands[$track]
 $maxBand = $maximum.Bands[$track]
 if (($maxBand.Right - $maxBand.Left + 1) -lt 1.2 * ($defaultBand.Right - $defaultBand.Left + 1) -or
     ($maxBand.Bottom - $maxBand.Top) -le ($defaultBand.Bottom - $defaultBand.Top)) {
  throw "Maximum-size lyric track $($track + 1) did not grow as expected for the same fixture text."
 }
}
Write-Output 'PASS maximum font enlarges both lyric bands and increases the window height'
Write-Output 'NOT CHECKED by PNGs: exact text/appearance settings, internal track clipping, popup screen placement, real pointer/keyboard behavior, lock, and desktop composition.'
