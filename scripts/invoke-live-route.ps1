param(
    [Parameter(Mandatory = $true)]
    [string]$Steps,

    [string]$OutDir = '',

    [string]$BaseUrl = '',

    [int]$Timeout = 900,

    [int]$Gap = 180,

    [int]$ActionGap = 500,

    [int]$ActionTimeout = 1800,

    [switch]$CaptureEachStep,

[string]$AtlasPath = '',

[switch]$DryRun
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $OutDir = Join-Path (Get-Location) 'artifacts\live-routes'
}

if ([string]::IsNullOrWhiteSpace($AtlasPath)) {
    $AtlasPath = Join-Path (Get-Location) 'docs\live-atlas\pokemon-ruby.csv'
}

function Test-Direction([string]$Value) {
    $Value -in @('Up', 'Down', 'Left', 'Right')
}

function Test-Key([string]$Value) {
    $Value -in @('A', 'B', 'L', 'R', 'Start', 'Select', 'Up', 'Down', 'Left', 'Right')
}

function Convert-RouteToken([string]$Token) {
    $value = $Token.Trim()
    if ([string]::IsNullOrWhiteSpace($value)) {
        return $null
    }

    $parts = $value -split ':', 2
    if ($parts.Count -eq 1) {
        if (!(Test-Direction $value)) {
            throw "Invalid route step '$value'. Use Up/Down/Left/Right or action:key, such as tap:Up."
        }

        return [pscustomobject]@{
            command = 'walk-tile'
            key = $value
            source = $Token
        }
    }

    $command = $parts[0].Trim().ToLowerInvariant()
    $key = $parts[1].Trim()
    switch ($command) {
        { $_ -in @('walk', 'walk-tile', 'move') } {
            if (!(Test-Direction $key)) { throw "walk-tile requires a direction in '$Token'." }
            $resolvedCommand = 'walk-tile'
        }
        { $_ -in @('enter', 'door', 'walk-door') } {
            if (!(Test-Direction $key)) { throw "enter requires a direction in '$Token'." }
            $resolvedCommand = 'enter'
        }
        { $_ -in @('step', 'tile-step') } {
            if (!(Test-Direction $key)) { throw "tile-step requires a direction in '$Token'." }
            $resolvedCommand = 'tile-step'
        }
        'face' {
            if (!(Test-Direction $key)) { throw "face requires a direction in '$Token'." }
            $resolvedCommand = 'face'
        }
        'tap' {
            if (!(Test-Key $key)) { throw "tap requires a valid key in '$Token'." }
            $resolvedCommand = 'tap'
        }
        { $_ -in @('warp', 'warp-tap') } {
            if (!(Test-Key $key)) { throw "warp-tap requires a valid key in '$Token'." }
            $resolvedCommand = 'warp-tap'
        }
        default {
            throw "Invalid route command '$command' in '$Token'. Use walk, enter, step, face, tap, or warp."
        }
    }

    [pscustomobject]@{
        command = $resolvedCommand
        key = $key
        source = $Token
    }
}

$routeSteps = @(
    $Steps -split '[,\s]+' |
        Where-Object { ![string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { Convert-RouteToken $_ }
)

if ($routeSteps.Count -eq 0) {
    throw '-Steps must contain at least one direction.'
}

if ($DryRun) {
    $routeSteps | Select-Object @{ name = 'index'; expression = { [array]::IndexOf($routeSteps, $_) + 1 } }, source, command, key
    return
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$journalPath = Join-Path $OutDir ("route-{0:yyyyMMdd-HHmmss}.csv" -f (Get-Date))
$rows = New-Object System.Collections.Generic.List[object]

for ($i = 0; $i -lt $routeSteps.Count; $i++) {
    $step = $routeSteps[$i]
    $before = & (Join-Path $PSScriptRoot 'invoke-desktop-control.ps1') ruby-state -BaseUrl $BaseUrl
    switch ($step.command) {
        'walk-tile' {
            $result = & (Join-Path $PSScriptRoot 'invoke-desktop-control.ps1') walk-tile -Keys $step.key -Timeout $Timeout -Gap $Gap -BaseUrl $BaseUrl
        }
        'enter' {
            $result = & (Join-Path $PSScriptRoot 'invoke-desktop-control.ps1') walk-tile -Keys $step.key -Timeout $Timeout -Gap $Gap -BaseUrl $BaseUrl
        }
        'tile-step' {
            $result = & (Join-Path $PSScriptRoot 'invoke-desktop-control.ps1') tile-step -Keys $step.key -Gap $ActionGap -BaseUrl $BaseUrl
        }
        'face' {
            $result = & (Join-Path $PSScriptRoot 'invoke-desktop-control.ps1') face -Keys $step.key -Gap $ActionGap -BaseUrl $BaseUrl
        }
        'tap' {
            $result = & (Join-Path $PSScriptRoot 'invoke-desktop-control.ps1') tap -Keys $step.key -Gap $ActionGap -BaseUrl $BaseUrl
        }
        'warp-tap' {
            $result = & (Join-Path $PSScriptRoot 'invoke-desktop-control.ps1') warp-tap -Keys $step.key -Gap $ActionGap -BaseUrl $BaseUrl
        }
        default {
            throw "Unsupported route command '$($step.command)'."
        }
    }

    $after = & (Join-Path $PSScriptRoot 'invoke-desktop-control.ps1') ruby-state -BaseUrl $BaseUrl
    if ($step.command -ne 'walk-tile' -and $before.saveBlockPlayer -ne $null -and $after.saveBlockPlayer -ne $null) {
        $deadline = (Get-Date).AddMilliseconds($ActionTimeout)
        while ((Get-Date) -lt $deadline) {
            if ($after.saveBlockPlayer.mapId -ne $before.saveBlockPlayer.mapId) {
                break
            }

            Start-Sleep -Milliseconds 75
            $after = & (Join-Path $PSScriptRoot 'invoke-desktop-control.ps1') ruby-state -BaseUrl $BaseUrl
        }
    }

    $screenshot = ''
    $beforeMap = if ($before.saveBlockPlayer -ne $null) { [string]$before.saveBlockPlayer.mapId } else { '' }
    $beforeX = if ($before.saveBlockPlayer -ne $null) { [string]$before.saveBlockPlayer.x } else { '' }
    $beforeY = if ($before.saveBlockPlayer -ne $null) { [string]$before.saveBlockPlayer.y } else { '' }
    $afterMap = if ($after.saveBlockPlayer -ne $null) { [string]$after.saveBlockPlayer.mapId } else { '' }
    $afterX = if ($after.saveBlockPlayer -ne $null) { [string]$after.saveBlockPlayer.x } else { '' }
    $afterY = if ($after.saveBlockPlayer -ne $null) { [string]$after.saveBlockPlayer.y } else { '' }
    $verified = $result.verified
    $verificationType = $result.verificationType
    $reason = $result.reason

    if (($verified -eq $null -or $verified -eq $false) -and $beforeMap -ne '' -and $afterMap -ne '') {
        if ($beforeMap -ne $afterMap) {
            $verified = $true
            $verificationType = 'map-transition'
            $reason = ''
        }
        elseif ($beforeX -ne $afterX -or $beforeY -ne $afterY) {
            $verified = $true
            $verificationType = 'coordinate'
            $reason = ''
        }
    }

    if ($CaptureEachStep) {
        $screenshotName = "step-{0:D3}-{1}-{2}.png" -f ($i + 1), $step.command, $step.key
        $screenshot = Join-Path $OutDir $screenshotName
        & (Join-Path $PSScriptRoot 'invoke-desktop-control.ps1') screenshot `
            -BaseUrl $BaseUrl `
            -OutFile $screenshot `
            -Overlay atlas-coordinate-lens `
            -AtlasPath $AtlasPath | Out-Null
    }

    $rows.Add([pscustomobject]@{
        index = $i + 1
        source = $step.source
        command = $step.command
        key = $step.key
        direction = if (Test-Direction $step.key) { $step.key } else { '' }
        verified = $verified
        verificationType = $verificationType
        reason = $reason
        beforeMap = $beforeMap
        beforeX = $beforeX
        beforeY = $beforeY
        afterMap = $afterMap
        afterX = $afterX
        afterY = $afterY
        facing = if ($after.playerObject -ne $null) { $after.playerObject.facingName } else { '' }
        screenshot = $screenshot
    }) | Out-Null
}

$rows | Export-Csv -LiteralPath $journalPath -NoTypeInformation
Get-Item -LiteralPath $journalPath
