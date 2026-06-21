param(
    [Parameter(Mandatory = $true)]
    [string]$Label,

    [Parameter(Mandatory = $true)]
    [int]$Dx,

    [Parameter(Mandatory = $true)]
    [int]$Dy,

    [string]$Type = 'interactable',

    [int]$Width = 1,

    [int]$Height = 1,

    [string]$Notes = '',

    [Nullable[int]]$StandDx = $null,

    [Nullable[int]]$StandDy = $null,

    [string]$Action = '',

    [string]$Game = 'ruby',

    [string]$MapId = 'unknown',

    [string]$AtlasPath = '',

    [string]$ScreenshotDir = '',

    [string]$BaseUrl = '',

    [switch]$NoScreenshot
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($AtlasPath)) {
    $AtlasPath = Join-Path (Get-Location) 'docs\live-atlas\pokemon-ruby.csv'
}

if ([string]::IsNullOrWhiteSpace($ScreenshotDir)) {
    $ScreenshotDir = Join-Path (Get-Location) 'artifacts\live-atlas'
}

if ($Width -lt 1) { throw '-Width must be at least 1.' }
if ($Height -lt 1) { throw '-Height must be at least 1.' }

$atlasDirectory = Split-Path -Parent $AtlasPath
if (![string]::IsNullOrWhiteSpace($atlasDirectory)) {
    New-Item -ItemType Directory -Force -Path $atlasDirectory | Out-Null
}

if ([string]::IsNullOrWhiteSpace($BaseUrl)) {
    $discoveryPath = Join-Path $env:TEMP 'gbaSharp-control.json'
    if (Test-Path -LiteralPath $discoveryPath) {
        $BaseUrl = (Get-Content -LiteralPath $discoveryPath -Raw | ConvertFrom-Json).baseUrl
    }
}

$status = $null
$rubyState = $null
$screenshotPath = ''
if (![string]::IsNullOrWhiteSpace($BaseUrl)) {
    $BaseUrl = $BaseUrl.TrimEnd('/')
    $status = Invoke-RestMethod "$BaseUrl/status"
    try {
        $rubyState = Invoke-RestMethod "$BaseUrl/game/ruby/state"
    }
    catch {
        $rubyState = $null
    }

    if (!$NoScreenshot) {
        New-Item -ItemType Directory -Force -Path $ScreenshotDir | Out-Null
        $safeLabel = ($Label -replace '[^A-Za-z0-9._-]', '-').Trim('-')
        if ([string]::IsNullOrWhiteSpace($safeLabel)) { $safeLabel = 'tile' }
        $screenshotPath = Join-Path $ScreenshotDir ("{0:yyyyMMdd-HHmmss}-{1}.png" -f (Get-Date), $safeLabel)
    }
}

$playerState = if ($rubyState -ne $null -and $rubyState.saveBlockPlayer -ne $null) { $rubyState.saveBlockPlayer } else { $null }
$playerObject = if ($rubyState -ne $null -and $rubyState.playerObject -ne $null) { $rubyState.playerObject } else { $null }
$mapLabel = if ($MapId -ne 'unknown' -and ![string]::IsNullOrWhiteSpace($MapId)) { $MapId } else { '' }
$resolvedMapId = if ($playerState -ne $null) { $playerState.mapId } else { $MapId }
if ([string]::IsNullOrWhiteSpace($resolvedMapId) -and $playerState -ne $null) {
    $resolvedMapId = $playerState.mapId
}

$absoluteX = ''
$absoluteY = ''
$standX = ''
$standY = ''
if ($playerState -ne $null) {
    $absoluteX = $playerState.x + $Dx
    $absoluteY = $playerState.y + $Dy
    if ($StandDx -ne $null) {
        $standX = $playerState.x + [int]$StandDx
    }
    if ($StandDy -ne $null) {
        $standY = $playerState.y + [int]$StandDy
    }
}

$row = [pscustomobject]@{
    game = $Game
    mapId = $resolvedMapId
    mapLabel = $mapLabel
    label = $Label
    dx = $Dx
    dy = $Dy
    x = $absoluteX
    y = $absoluteY
    width = $Width
    height = $Height
    type = $Type
    notes = $Notes
    screenshot = $screenshotPath
    romName = if ($status -ne $null) { $status.romName } else { '' }
    emulatedFrames = if ($status -ne $null) { $status.emulatedFrames } else { '' }
    playerFacing = if ($playerObject -ne $null) { $playerObject.facingName } else { '' }
    playerX = if ($playerState -ne $null) { $playerState.x } else { '' }
    playerY = if ($playerState -ne $null) { $playerState.y } else { '' }
    recordedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    standX = $standX
    standY = $standY
    action = $Action
}

if (Test-Path -LiteralPath $AtlasPath) {
    $row | Export-Csv -LiteralPath $AtlasPath -NoTypeInformation -Append -Force
}
else {
    $row | Export-Csv -LiteralPath $AtlasPath -NoTypeInformation
}

if (![string]::IsNullOrWhiteSpace($BaseUrl) -and !$NoScreenshot) {
    & (Join-Path $PSScriptRoot 'invoke-desktop-control.ps1') screenshot `
        -BaseUrl $BaseUrl `
        -OutFile $screenshotPath `
        -Overlay atlas-coordinate-lens `
        -AtlasPath $AtlasPath | Out-Null
}

$row
