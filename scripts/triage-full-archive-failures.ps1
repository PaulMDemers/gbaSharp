param(
    [string]$FailuresPath = "compat-retail-full-20260518-0851-3734\cumulative\failures.csv",
    [string]$OutputDir = "",
    [int]$Top = 40
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $repoRoot
try {
    if (-not (Test-Path -LiteralPath $FailuresPath)) {
        throw "Failures CSV not found: $FailuresPath"
    }

    if ([string]::IsNullOrWhiteSpace($OutputDir)) {
        $OutputDir = Join-Path (Split-Path -Parent $FailuresPath) "triage"
    }

    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
    $rows = @(Import-Csv -LiteralPath $FailuresPath)

    function Test-ValidGameCode {
        param([string]$GameCode)

        return $GameCode -match '^[A-Z0-9]{4}$'
    }

    function Get-ArchiveClass {
        param([object]$Row)

        $path = [string]$Row.path
        if ($path -match '\\1 USA - ') { return "retail-usa" }
        if ($path -match '\\2 Europe - ') { return "retail-europe" }
        if ($path -match '\\2 Japan - ' -or $path -match '\\3 Japan - ') { return "retail-japan" }
        if ($path -match '\\2 GBA Video\\') { return "gba-video" }
        if ($path -match '\\2 Unlicensed - ' -or $path -match '\(Unl\)') { return "unlicensed" }
        if ($path -match '\\2 Virtual Console\\') { return "virtual-console-inject" }
        if ($path -match 'Tools & Service Test Carts') { return "tools-or-service" }
        if ($path -match '\\Homebrew\\' -or $path -match '\(Homebrew\)') { return "homebrew" }
        if ($path -match '\\Hack' -or $path -match '\(Hack') { return "hack" }
        if ($path -match '\\Demo' -or $path -match '\(Demo') { return "demo-or-sample" }
        return "other"
    }

    function Get-Triage {
        param([object]$Row)

        $title = [string]$Row.title
        $gameCode = [string]$Row.gameCode
        $path = [string]$Row.path
        $errorText = [string]$Row.error
        $pc = [string]$Row.pc
        $archiveClass = Get-ArchiveClass $Row
        $validGameCode = Test-ValidGameCode $gameCode

        if ($archiveClass -in @("unlicensed", "virtual-console-inject", "tools-or-service", "homebrew", "hack", "demo-or-sample", "gba-video")) {
            return [pscustomobject]@{
                Bucket = "archive-noise-or-special-case"
                Priority = "low"
                Reason = "Nonstandard archive class: $archiveClass"
            }
        }

        if ($title -match 'YJencrypted' -or -not $validGameCode -or [string]::IsNullOrWhiteSpace($gameCode) -or $gameCode -eq "???") {
            return [pscustomobject]@{
                Bucket = "bad-dump-or-odd-header"
                Priority = "low"
                Reason = "Suspicious header/title/gameCode"
            }
        }

        if ($Row.status -eq "timeout" -or $errorText -match 'max-steps') {
            return [pscustomobject]@{
                Bucket = "performance-timeout"
                Priority = "medium"
                Reason = "Timed out while still progressing"
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

        if ($errorText -match 'Thumb instruction 0xE[89A-F][0-9A-F]{2} is not implemented') {
            return [pscustomobject]@{
                Bucket = "likely-data-as-thumb-code"
                Priority = "medium"
                Reason = "Thumb 0xE8xx+ is undefined on ARM7TDMI; likely bad branch target"
            }
        }

        if ($errorText -match 'ARM instruction 0xE[E-F][0-9A-F]{6} is not implemented') {
            return [pscustomobject]@{
                Bucket = "likely-data-as-arm-code"
                Priority = "medium"
                Reason = "ARM coprocessor/undefined range on ARM7TDMI; likely bad branch target"
            }
        }

        if ($errorText -match 'Thumb instruction .* not implemented' -or $errorText -match 'ARM instruction .* not implemented') {
            return [pscustomobject]@{
                Bucket = "possible-cpu-gap-or-control-flow"
                Priority = "medium"
                Reason = "Unhandled instruction reached"
            }
        }

        if ($Row.classification -eq "invalid-pc" -and $pc -eq "0x00000000") {
            return [pscustomobject]@{
                Bucket = "null-pc-control-flow"
                Priority = "high"
                Reason = "Branched to zero"
            }
        }

        if ($Row.classification -eq "invalid-pc") {
            return [pscustomobject]@{
                Bucket = "invalid-pc-control-flow"
                Priority = "high"
                Reason = "PC left executable memory"
            }
        }

        return [pscustomobject]@{
            Bucket = "unclassified-crash"
            Priority = "medium"
            Reason = "Crash row did not match a specific heuristic"
        }
    }

    $triaged = foreach ($row in $rows) {
        $archiveClass = Get-ArchiveClass $row
        $triage = Get-Triage $row
        [pscustomobject]@{
            index = $row.index
            phase = $row.phase
            status = $row.status
            classification = $row.classification
            archiveClass = $archiveClass
            bucket = $triage.Bucket
            priority = $triage.Priority
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

    $triagedPath = Join-Path $OutputDir "triaged-failures.csv"
    $triaged | Export-Csv -LiteralPath $triagedPath -NoTypeInformation

    function Write-Group {
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

    Write-Group -InputRows $triaged -Properties @("bucket") -Path (Join-Path $OutputDir "by-bucket.csv")
    Write-Group -InputRows $triaged -Properties @("priority", "bucket") -Path (Join-Path $OutputDir "by-priority-bucket.csv")
    Write-Group -InputRows $triaged -Properties @("archiveClass", "bucket") -Path (Join-Path $OutputDir "by-archive-class-bucket.csv")
    Write-Group -InputRows $triaged -Properties @("bucket", "error") -Path (Join-Path $OutputDir "by-bucket-error.csv")
    Write-Group -InputRows $triaged -Properties @("bucket", "index", "title", "gameCode") -Path (Join-Path $OutputDir "by-bucket-rom.csv")

    $recommended = @($triaged |
        Where-Object { $_.priority -eq "high" -and $_.bucket -ne "archive-noise-or-special-case" -and $_.bucket -ne "bad-dump-or-odd-header" } |
        Group-Object bucket, error |
        Sort-Object @{ Expression = "Count"; Descending = $true }, Name |
        Select-Object -First $Top)

    $summary = New-Object System.Collections.Generic.List[string]
    $summary.Add("# Full Archive Failure Triage")
    $summary.Add("")
    $summary.Add("Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
    $summary.Add("")
    $summary.Add('Failures: `' + $FailuresPath + '`')
    $summary.Add("")
    $summary.Add("## Bucket Mix")
    $summary.Add("")
    $summary.Add("| Bucket | Rows |")
    $summary.Add("| --- | ---: |")
    foreach ($row in @(Import-Csv -LiteralPath (Join-Path $OutputDir "by-bucket.csv"))) {
        $summary.Add("| $($row.bucket) | $($row.count) |")
    }

    $summary.Add("")
    $summary.Add("## Archive Class Mix")
    $summary.Add("")
    $summary.Add("| Archive Class | Bucket | Rows |")
    $summary.Add("| --- | --- | ---: |")
    foreach ($row in @(Import-Csv -LiteralPath (Join-Path $OutputDir "by-archive-class-bucket.csv"))) {
        $summary.Add("| $($row.archiveClass) | $($row.bucket) | $($row.count) |")
    }

    $summary.Add("")
    $summary.Add("## Recommended High-Priority Clusters")
    $summary.Add("")
    $summary.Add("| Rows | Bucket | Error |")
    $summary.Add("| ---: | --- | --- |")
    foreach ($group in $recommended) {
        $parts = $group.Name -split ", ", 2
        $bucket = $parts[0]
        $errorText = if ($parts.Count -gt 1) { $parts[1] } else { "" }
        if ([string]::IsNullOrWhiteSpace($errorText)) { $errorText = "(blank)" }
        $summary.Add("| $($group.Count) | $bucket | $($errorText.Replace('|', '\|')) |")
    }

    $summary.Add("")
    $summary.Add("## Suggested Order")
    $summary.Add("")
    $summary.Add("1. Re-test high-priority retail/control-flow clusters with focused traces before changing CPU decode.")
    $summary.Add('2. Start with `runtime-bounds-crash` and `cpu-decode-or-control-flow`, because they contain concrete emulator-side symptoms.')
    $summary.Add('3. Treat `likely-data-as-thumb-code` and `likely-data-as-arm-code` as branch-target corruption until a trace proves a valid instruction gap.')
    $summary.Add('4. Defer `archive-noise-or-special-case` and `bad-dump-or-odd-header` until real retail regressions are exhausted.')

    Set-Content -LiteralPath (Join-Path $OutputDir "summary.md") -Value $summary

    Write-Host "Triaged rows: $($triaged.Count)"
    Write-Host "Output: $((Resolve-Path $OutputDir).Path)"
}
finally {
    Pop-Location
}
