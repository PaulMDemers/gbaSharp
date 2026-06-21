param(
    [string]$BaseUrl = '',
    [int]$TargetY = 0,
    [int]$MaxProbes = 80,
    [int]$Timeout = 900,
    [int]$Gap = 180,
    [int]$MinX = 0,
    [int]$MaxX = 30,
    [int]$MinY = 0,
    [int]$MaxY = 30,
    [string]$OutDir = ''
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $OutDir = Join-Path (Get-Location) 'artifacts\live-routes\explore'
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$journalPath = Join-Path $OutDir ("explore-{0:yyyyMMdd-HHmmss}.csv" -f (Get-Date))

$control = Join-Path $PSScriptRoot 'invoke-desktop-control.ps1'
$directions = @('Up', 'Right', 'Left', 'Down')
$opposite = @{
    Up = 'Down'
    Down = 'Up'
    Left = 'Right'
    Right = 'Left'
}
$dx = @{
    Left = -1
    Right = 1
    Up = 0
    Down = 0
}
$dy = @{
    Left = 0
    Right = 0
    Up = -1
    Down = 1
}

$visited = New-Object 'System.Collections.Generic.HashSet[string]'
$blocked = New-Object 'System.Collections.Generic.HashSet[string]'
$rows = New-Object 'System.Collections.Generic.List[object]'
$script:probeCount = 0
$script:found = $false

function Get-RubyPosition {
    $state = & $control ruby-state -BaseUrl $BaseUrl
    if ($state.saveBlockPlayer -eq $null) {
        throw 'Ruby state did not include SaveBlockPlayer.'
    }

    [pscustomobject]@{
        map = [string]$state.saveBlockPlayer.mapId
        x = [int]$state.saveBlockPlayer.x
        y = [int]$state.saveBlockPlayer.y
    }
}

function Get-Key([object]$Position) {
    '{0}:{1}:{2}' -f $Position.map, $Position.x, $Position.y
}

function Add-Row([string]$Action, [string]$Direction, [object]$Before, [object]$After, [bool]$Moved, [string]$Note) {
    $rows.Add([pscustomobject]@{
        index = $rows.Count + 1
        action = $Action
        direction = $Direction
        beforeMap = $Before.map
        beforeX = $Before.x
        beforeY = $Before.y
        afterMap = $After.map
        afterX = $After.x
        afterY = $After.y
        moved = $Moved
        note = $Note
    })
}

function Invoke-Walk([string]$Direction) {
    $before = Get-RubyPosition
    & $control walk-tile -Keys $Direction -Timeout $Timeout -Gap $Gap -BaseUrl $BaseUrl | Out-Null
    $after = Get-RubyPosition
    $moved = $before.map -ne $after.map -or $before.x -ne $after.x -or $before.y -ne $after.y
    Add-Row 'probe' $Direction $before $after $moved ''
    $script:probeCount++
    $after
}

function Invoke-Backtrack([string]$Direction, [object]$Expected) {
    $before = Get-RubyPosition
    & $control walk-tile -Keys $Direction -Timeout $Timeout -Gap $Gap -BaseUrl $BaseUrl | Out-Null
    $after = Get-RubyPosition
    $moved = $before.map -ne $after.map -or $before.x -ne $after.x -or $before.y -ne $after.y
    $note = if ($after.map -eq $Expected.map -and $after.x -eq $Expected.x -and $after.y -eq $Expected.y) { 'ok' } else { 'unexpected' }
    Add-Row 'backtrack' $Direction $before $after $moved $note
    $script:probeCount++
    $after
}

function Search-From([object]$Position) {
    if ($script:found -or $script:probeCount -ge $MaxProbes) {
        return
    }

    $visited.Add((Get-Key $Position)) | Out-Null
    if ($Position.y -le $TargetY) {
        $script:found = $true
        return
    }

    foreach ($direction in $directions) {
        if ($script:found -or $script:probeCount -ge $MaxProbes) {
            return
        }

        $nextX = $Position.x + [int]$dx[$direction]
        $nextY = $Position.y + [int]$dy[$direction]
        if ($nextX -lt $MinX -or $nextX -gt $MaxX -or $nextY -lt $MinY -or $nextY -gt $MaxY) {
            continue
        }

        $edgeKey = '{0}:{1}:{2}->{3}' -f $Position.map, $Position.x, $Position.y, $direction
        if ($blocked.Contains($edgeKey)) {
            continue
        }

        $after = Invoke-Walk $direction
        if ($after.map -ne $Position.map) {
            $script:found = $true
            return
        }

        if ($after.x -eq $Position.x -and $after.y -eq $Position.y) {
            $blocked.Add($edgeKey) | Out-Null
            continue
        }

        $afterKey = Get-Key $after
        if (!$visited.Contains($afterKey)) {
            Search-From $after
            if ($script:found) {
                return
            }
        }

        Invoke-Backtrack $opposite[$direction] $Position | Out-Null
    }
}

$start = Get-RubyPosition
Search-From $start

$rows | Export-Csv -NoTypeInformation -Path $journalPath
[pscustomobject]@{
    found = $script:found
    probes = $script:probeCount
    journal = $journalPath
    start = Get-Key $start
    current = Get-Key (Get-RubyPosition)
}
