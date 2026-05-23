param(
    [Parameter(Mandatory = $true)]
    [string]$ProfilePath,

    [string]$OutputDir = "",

    [int]$Top = 20
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $ProfilePath)) {
    throw "Profile CSV not found: $ProfilePath"
}

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $baseName = [IO.Path]::GetFileNameWithoutExtension($ProfilePath)
    $OutputDir = Join-Path ([IO.Path]::GetDirectoryName((Resolve-Path -LiteralPath $ProfilePath).Path)) "$baseName-analysis"
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$culture = [Globalization.CultureInfo]::InvariantCulture
$rows = Import-Csv -LiteralPath $ProfilePath | ForEach-Object {
    [pscustomobject]@{
        index = [int]$_.index
        phase = $_.phase
        status = $_.status
        classification = $_.classification
        frames = [int]$_.frames
        steps = [int]$_.steps
        cycles = [long]$_.cycles
        wallMs = [double]::Parse($_.wallMs, $culture)
        stepsPerSecond = [double]::Parse($_.stepsPerSecond, $culture)
        framesPerSecond = [double]::Parse($_.framesPerSecond, $culture)
        cpuMs = [double]::Parse($_.cpuMs, $culture)
        busMs = [double]::Parse($_.busMs, $culture)
        schedulerMs = [double]::Parse($_.schedulerMs, $culture)
        cpuPct = [double]::Parse($_.cpuPct, $culture)
        busPct = [double]::Parse($_.busPct, $culture)
        schedulerPct = [double]::Parse($_.schedulerPct, $culture)
        title = $_.title
        gameCode = $_.gameCode
        path = $_.path
    }
}

if ($rows.Count -eq 0) {
    throw "Profile CSV has no rows: $ProfilePath"
}

function Average($items, [scriptblock]$selector) {
    $values = @($items | ForEach-Object { & $selector $_ })
    if ($values.Count -eq 0) { return 0 }
    return ($values | Measure-Object -Average).Average
}

$byTitle = $rows |
    Group-Object title, gameCode |
    ForEach-Object {
        $group = $_.Group
        [pscustomobject]@{
            title = $group[0].title
            gameCode = $group[0].gameCode
            rows = $group.Count
            avgStepsPerSecond = [math]::Round((Average $group { param($row) $row.stepsPerSecond }), 0)
            avgFramesPerSecond = [math]::Round((Average $group { param($row) $row.framesPerSecond }), 2)
            avgCpuPct = [math]::Round((Average $group { param($row) $row.cpuPct }), 1)
            avgBusPct = [math]::Round((Average $group { param($row) $row.busPct }), 1)
            avgSchedulerPct = [math]::Round((Average $group { param($row) $row.schedulerPct }), 1)
            timeouts = @($group | Where-Object status -eq "timeout").Count
            path = $group[0].path
        }
    } |
    Sort-Object avgStepsPerSecond

$byTitle | Export-Csv -NoTypeInformation -Path (Join-Path $OutputDir "by-title.csv")
$rows | Sort-Object schedulerPct -Descending | Select-Object -First $Top | Export-Csv -NoTypeInformation -Path (Join-Path $OutputDir "top-scheduler.csv")
$rows | Sort-Object cpuPct -Descending | Select-Object -First $Top | Export-Csv -NoTypeInformation -Path (Join-Path $OutputDir "top-cpu.csv")
$rows | Sort-Object framesPerSecond | Select-Object -First $Top | Export-Csv -NoTypeInformation -Path (Join-Path $OutputDir "slowest-fps.csv")

$avgCpu = [math]::Round((Average $rows { param($row) $row.cpuPct }), 1)
$avgBus = [math]::Round((Average $rows { param($row) $row.busPct }), 1)
$avgScheduler = [math]::Round((Average $rows { param($row) $row.schedulerPct }), 1)
$avgSteps = [math]::Round((Average $rows { param($row) $row.stepsPerSecond }), 0)
$avgFrames = [math]::Round((Average $rows { param($row) $row.framesPerSecond }), 2)
$resolvedProfilePath = (Resolve-Path -LiteralPath $ProfilePath).Path

$summary = @"
# Compatibility Profile Summary

- Source: $resolvedProfilePath
- Rows: $($rows.Count)
- Average steps/sec: $avgSteps
- Average frames/sec: $avgFrames
- Average CPU share: $avgCpu%
- Average bus share: $avgBus%
- Average scheduler/video share: $avgScheduler%

## Slowest Titles By Steps/Sec

| Title | Game Code | Rows | Avg Steps/Sec | Avg Frames/Sec | CPU % | Bus % | Scheduler % | Timeouts |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
"@

foreach ($row in $byTitle | Select-Object -First $Top) {
    $summary += "`n| $($row.title) | $($row.gameCode) | $($row.rows) | $($row.avgStepsPerSecond) | $($row.avgFramesPerSecond) | $($row.avgCpuPct) | $($row.avgBusPct) | $($row.avgSchedulerPct) | $($row.timeouts) |"
}

$summary | Set-Content -Path (Join-Path $OutputDir "summary.md") -Encoding UTF8
Write-Host "Profile analysis: $(Resolve-Path -LiteralPath $OutputDir)"
