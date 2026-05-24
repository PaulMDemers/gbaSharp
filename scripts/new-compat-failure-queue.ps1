param(
    [Parameter(Mandatory = $true)]
    [string[]]$ReportPath,
    [string]$OutputDir = "compat-current-failure-queue",
    [int]$Top = 40,
    [switch]$IncludeSupersededFailures,
    [switch]$IncludeBootOnlyEarlyStatic
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $repoRoot
try {
    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

    function Get-ArchiveClass {
        param([object]$Row)

        $path = [string]$Row.path
        $report = [string]$Row.report

        if ($report -match 'curated' -or $path -match '^(action|action-shooter|adventure|arcade|fighting|platformer|puzzle|racing|rpg|sports|strategy)\\') {
            return "curated-official"
        }

        if ($path -match '\\1 USA - ') { return "retail-usa" }
        if ($path -match '\\2 Europe - ') { return "retail-europe" }
        if ($path -match '\\2 Japan - ' -or $path -match '\\3 Japan - ') { return "retail-japan" }
        if ($path -match '\\2 Other Regions - ') { return "retail-other-region" }
        if ($path -match '\\4 Beta, Prototypes, Revisions\\Revisions\\') { return "revision" }
        if ($path -match '\\4 Beta, Prototypes, Revisions\\Samples\\') { return "sample" }
        if ($path -match '\\4 Beta, Prototypes, Revisions\\Prototypes\\' -or $path -match '\(Proto') { return "prototype" }
        if ($path -match '\\2 GBA Video\\') { return "gba-video" }
        if ($path -match '\\2 Unlicensed - ' -or $path -match '\(Unl\)') { return "unlicensed" }
        if ($path -match '\\2 Virtual Console\\') { return "virtual-console-inject" }
        if ($path -match 'Tools & Service Test Carts') { return "tools-or-service" }
        if ($path -match '\\Homebrew\\' -or $path -match '\(Homebrew\)') { return "homebrew" }
        if ($path -match '\\Hack' -or $path -match '\(Hack') { return "hack" }
        if ($path -match '\\Demo' -or $path -match '\(Demo') { return "demo-or-sample" }
        return "other"
    }

    function Get-RomKey {
        param([object]$Row)

        $path = [string]$Row.path
        if (-not [string]::IsNullOrWhiteSpace($path)) {
            return $path.ToLowerInvariant()
        }

        return ("{0}|{1}|{2}" -f $Row.index, $Row.title, $Row.gameCode).ToLowerInvariant()
    }

    function Get-RomPhaseKey {
        param([object]$Row)

        return "{0}|{1}" -f (Get-RomKey $Row), ([string]$Row.phase).ToLowerInvariant()
    }

    function Test-ArchiveNoise {
        param([string]$ArchiveClass)

        return $ArchiveClass -in @(
            "gba-video",
            "unlicensed",
            "virtual-console-inject",
            "tools-or-service",
            "homebrew",
            "hack",
            "demo-or-sample",
            "sample",
            "prototype"
        )
    }

    function Get-Triage {
        param(
            [object]$Row,
            [bool]$HasBootForRom
        )

        $archiveClass = [string]$Row.archiveClass
        $status = [string]$Row.status
        $classification = [string]$Row.classification
        $errorText = [string]$Row.error
        $pc = [string]$Row.pc
        $title = [string]$Row.title
        $gameCode = [string]$Row.gameCode

        if (Test-ArchiveNoise $archiveClass) {
            return [pscustomobject]@{
                Bucket = "archive-noise-or-special-case"
                Priority = "ignore"
                Reason = "Nonstandard archive class: $archiveClass"
            }
        }

        if ($title -match 'YJencrypted' -or [string]::IsNullOrWhiteSpace($gameCode) -or $gameCode -eq "???" -or $gameCode -notmatch '^[A-Z0-9]{4}$') {
            return [pscustomobject]@{
                Bucket = "bad-dump-or-odd-header"
                Priority = "low"
                Reason = "Suspicious header/title/gameCode"
            }
        }

        if ($status -eq "static" -and $classification -eq "early-window-static" -and $HasBootForRom -and -not $IncludeBootOnlyEarlyStatic) {
            return [pscustomobject]@{
                Bucket = "boot-only-early-window-static"
                Priority = "low"
                Reason = "Other phase for the same ROM boots; likely early-window/classifier artifact"
            }
        }

        if ($status -eq "timeout" -or $errorText -match 'max-steps') {
            return [pscustomobject]@{
                Bucket = "performance-timeout"
                Priority = "medium"
                Reason = "Timed out before reaching the boot classifier"
            }
        }

        if ($errorText -match 'Index was outside the bounds') {
            return [pscustomobject]@{
                Bucket = "runtime-bounds-crash"
                Priority = "high"
                Reason = "Managed bounds exception"
            }
        }

        if ($errorText -match 'ARM halfword transfer kind 3 is not supported for store') {
            return [pscustomobject]@{
                Bucket = "cpu-decode-or-control-flow"
                Priority = "high"
                Reason = "Reached undefined ARM signed-halfword store form"
            }
        }

        if ($classification -eq "invalid-pc" -and $pc -eq "0x00000000") {
            return [pscustomobject]@{
                Bucket = "null-pc-control-flow"
                Priority = "high"
                Reason = "Branched to zero"
            }
        }

        if ($classification -eq "invalid-pc") {
            return [pscustomobject]@{
                Bucket = "invalid-pc-control-flow"
                Priority = "high"
                Reason = "PC left executable memory"
            }
        }

        if ($errorText -match 'Thumb instruction .* not implemented' -or $errorText -match 'ARM instruction .* not implemented') {
            return [pscustomobject]@{
                Bucket = "possible-cpu-gap-or-control-flow"
                Priority = "medium"
                Reason = "Unhandled instruction reached"
            }
        }

        if ($status -eq "static") {
            return [pscustomobject]@{
                Bucket = "static-or-render-stall"
                Priority = "medium"
                Reason = "Did not crash, but classifier did not observe expected progress"
            }
        }

        return [pscustomobject]@{
            Bucket = "unclassified-nonboot"
            Priority = "medium"
            Reason = "Non-boot row did not match a specific heuristic"
        }
    }

    function Write-GroupCsv {
        param(
            [object[]]$InputRows,
            [string[]]$Properties,
            [string]$Path
        )

        $InputRows |
            Group-Object -Property $Properties |
            Sort-Object @{ Expression = "Count"; Descending = $true }, Name |
            ForEach-Object {
                $parts = $_.Name -split ", "
                $object = [ordered]@{ count = $_.Count }
                for ($i = 0; $i -lt $Properties.Count; $i++) {
                    $object[$Properties[$i]] = if ($i -lt $parts.Count) { $parts[$i] } else { "" }
                }

                [pscustomobject]$object
            } |
            Export-Csv -LiteralPath $Path -NoTypeInformation
    }

    $rows = New-Object System.Collections.Generic.List[object]
    $loadedReports = New-Object System.Collections.Generic.List[string]
    foreach ($path in $ReportPath) {
        if (-not (Test-Path -LiteralPath $path)) {
            Write-Warning "Skipping missing report: $path"
            continue
        }

        $loadedReports.Add($path)
        foreach ($row in @(Import-Csv -LiteralPath $path)) {
            $rows.Add([pscustomobject]@{
                report = $path
                index = $row.index
                phase = $row.phase
                status = $row.status
                classification = $row.classification
                frames = $row.frames
                pc = $row.pc
                title = $row.title
                gameCode = $row.gameCode
                saveType = $row.saveType
                error = $row.error
                path = $row.path
                capture = $row.capture
            })
        }
    }

    $bootByRom = @{}
    $bootByRomPhase = @{}
    foreach ($row in @($rows | Where-Object { $_.status -eq "boot" })) {
        $bootByRom[(Get-RomKey $row)] = $true
        $bootByRomPhase[(Get-RomPhaseKey $row)] = $true
    }

    $allFailures = @($rows | Where-Object { $_.status -ne "boot" })
    $supersededFailures = @($allFailures | Where-Object { $bootByRomPhase.ContainsKey((Get-RomPhaseKey $_)) })
    if ($IncludeSupersededFailures) {
        $failures = $allFailures
    }
    else {
        $failures = @($allFailures | Where-Object { -not $bootByRomPhase.ContainsKey((Get-RomPhaseKey $_)) })
    }

    $queue = foreach ($row in $failures) {
        $archiveClass = Get-ArchiveClass $row
        Add-Member -InputObject $row -NotePropertyName archiveClass -NotePropertyValue $archiveClass -Force
        $hasBootForRom = $bootByRom.ContainsKey((Get-RomKey $row))
        $triage = Get-Triage -Row $row -HasBootForRom $hasBootForRom

        [pscustomobject]@{
            report = $row.report
            index = $row.index
            phase = $row.phase
            status = $row.status
            classification = $row.classification
            archiveClass = $archiveClass
            priority = $triage.Priority
            bucket = $triage.Bucket
            reason = $triage.Reason
            frames = $row.frames
            pc = $row.pc
            title = $row.title
            gameCode = $row.gameCode
            saveType = $row.saveType
            error = $row.error
            path = $row.path
            capture = $row.capture
        }
    }

    $priorityRank = @{ high = 0; medium = 1; low = 2; ignore = 3 }
    $queue = @($queue | Sort-Object @{ Expression = { $priorityRank[[string]$_.priority] } }, bucket, index, phase)
    $queuePath = Join-Path $OutputDir "failure-queue.csv"
    $queue | Export-Csv -LiteralPath $queuePath -NoTypeInformation

    Write-GroupCsv -InputRows $queue -Properties @("priority", "bucket") -Path (Join-Path $OutputDir "by-priority-bucket.csv")
    Write-GroupCsv -InputRows $queue -Properties @("archiveClass", "bucket") -Path (Join-Path $OutputDir "by-archive-class-bucket.csv")
    Write-GroupCsv -InputRows $queue -Properties @("index", "title", "gameCode", "archiveClass", "priority", "bucket") -Path (Join-Path $OutputDir "by-rom.csv")
    Write-GroupCsv -InputRows $queue -Properties @("status", "classification") -Path (Join-Path $OutputDir "by-status.csv")

    $nonIgnored = @($queue | Where-Object { $_.priority -ne "ignore" })
    $recommended = @($nonIgnored |
        Where-Object { $_.priority -in @("high", "medium") } |
        Select-Object -First $Top)

    $summary = New-Object System.Collections.Generic.List[string]
    $summary.Add("# Compatibility Failure Queue")
    $summary.Add("")
    $summary.Add("Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
    $summary.Add("")
    $summary.Add("Reports: $($loadedReports.Count)")
    $summary.Add("")
    $summary.Add("## Totals")
    $summary.Add("")
    $summary.Add("| Rows | Boot | Non-Boot | Superseded Non-Boot | Queued | Actionable | Ignored Noise |")
    $summary.Add("| ---: | ---: | ---: | ---: | ---: | ---: | ---: |")
    $summary.Add("| $($rows.Count) | $(@($rows | Where-Object { $_.status -eq 'boot' }).Count) | $($allFailures.Count) | $($supersededFailures.Count) | $($queue.Count) | $($nonIgnored.Count) | $(@($queue | Where-Object { $_.priority -eq 'ignore' }).Count) |")
    $summary.Add("")
    $summary.Add("## Priority Buckets")
    $summary.Add("")
    $summary.Add("| Priority | Bucket | Rows |")
    $summary.Add("| --- | --- | ---: |")
    foreach ($row in @(Import-Csv -LiteralPath (Join-Path $OutputDir "by-priority-bucket.csv"))) {
        $summary.Add("| $($row.priority) | $($row.bucket) | $($row.count) |")
    }

    $summary.Add("")
    $summary.Add("## Archive Class Buckets")
    $summary.Add("")
    $summary.Add("| Archive Class | Bucket | Rows |")
    $summary.Add("| --- | --- | ---: |")
    foreach ($row in @(Import-Csv -LiteralPath (Join-Path $OutputDir "by-archive-class-bucket.csv"))) {
        $summary.Add("| $($row.archiveClass) | $($row.bucket) | $($row.count) |")
    }

    $summary.Add("")
    $summary.Add("## Recommended Targets")
    $summary.Add("")
    $summary.Add("| Priority | Index | Phase | Status | Bucket | Title | Code |")
    $summary.Add("| --- | ---: | --- | --- | --- | --- | --- |")
    foreach ($row in $recommended) {
        $title = ([string]$row.title).Replace("|", "\|")
        $summary.Add("| $($row.priority) | $($row.index) | $($row.phase) | $($row.status) | $($row.bucket) | $title | $($row.gameCode) |")
    }

    $summaryPath = Join-Path $OutputDir "summary.md"
    Set-Content -LiteralPath $summaryPath -Value $summary

    Write-Host "Rows: $($rows.Count)"
    Write-Host "Boot: $(@($rows | Where-Object { $_.status -eq 'boot' }).Count)"
    Write-Host "NonBoot: $($allFailures.Count)"
    Write-Host "SupersededNonBoot: $($supersededFailures.Count)"
    Write-Host "Queued: $($queue.Count)"
    Write-Host "Actionable: $($nonIgnored.Count)"
    Write-Host "IgnoredNoise: $(@($queue | Where-Object { $_.priority -eq 'ignore' }).Count)"
    Write-Host "Queue: $((Resolve-Path $OutputDir).Path)"
}
finally {
    Pop-Location
}
