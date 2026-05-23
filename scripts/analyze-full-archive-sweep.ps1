param(
    [string]$ReportPath = "compat-retail-full-20260518-0851-3734\cumulative\compat-all.csv",
    [string]$OutputDir = "",
    [int]$Top = 40
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $repoRoot
try {
    if (-not (Test-Path -LiteralPath $ReportPath)) {
        throw "Report not found: $ReportPath"
    }

    if ([string]::IsNullOrWhiteSpace($OutputDir)) {
        $OutputDir = Join-Path (Split-Path -Parent $ReportPath) "analysis"
    }

    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

    $rows = @(Import-Csv -LiteralPath $ReportPath)
    $failures = @($rows | Where-Object { $_.status -ne "boot" })

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

    Write-GroupCsv -InputRows $failures -Properties @("index", "title", "gameCode") -Path (Join-Path $OutputDir "failures-by-rom.csv")
    Write-GroupCsv -InputRows $failures -Properties @("error") -Path (Join-Path $OutputDir "failures-by-error.csv")
    Write-GroupCsv -InputRows $failures -Properties @("classification") -Path (Join-Path $OutputDir "failures-by-classification.csv")
    Write-GroupCsv -InputRows $failures -Properties @("phase") -Path (Join-Path $OutputDir "failures-by-phase.csv")
    Write-GroupCsv -InputRows $failures -Properties @("saveType", "status") -Path (Join-Path $OutputDir "status-by-save-type.csv")

    $blockRows = $rows |
        ForEach-Object {
            $index = [int]$_.index
            $blockStart = [int](1 + ([Math]::Floor(($index - 1) / 100) * 100))
            $blockEnd = $blockStart + 99
            [pscustomobject]@{
                block = "{0:d4}-{1:d4}" -f $blockStart, $blockEnd
                status = $_.status
            }
        }

    $blockRows |
        Group-Object block, status |
        Sort-Object Name |
        ForEach-Object {
            $parts = $_.Name -split ", "
            [pscustomobject]@{
                block = $parts[0]
                status = $parts[1]
                count = $_.Count
            }
        } |
        Export-Csv -LiteralPath (Join-Path $OutputDir "status-by-index-block.csv") -NoTypeInformation

    $bootCount = @($rows | Where-Object { $_.status -eq "boot" }).Count
    $crashCount = @($rows | Where-Object { $_.status -eq "crash" }).Count
    $timeoutCount = @($rows | Where-Object { $_.status -eq "timeout" }).Count
    $staticCount = @($rows | Where-Object { $_.status -eq "static" }).Count

    $topRoms = @(Import-Csv -LiteralPath (Join-Path $OutputDir "failures-by-rom.csv") | Select-Object -First $Top)
    $topErrors = @(Import-Csv -LiteralPath (Join-Path $OutputDir "failures-by-error.csv") | Select-Object -First $Top)
    $byPhase = @(Import-Csv -LiteralPath (Join-Path $OutputDir "failures-by-phase.csv"))
    $byClass = @(Import-Csv -LiteralPath (Join-Path $OutputDir "failures-by-classification.csv"))

    $summary = New-Object System.Collections.Generic.List[string]
    $summary.Add("# Full Archive Sweep Analysis")
    $summary.Add("")
    $summary.Add("Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
    $summary.Add("")
    $summary.Add('Report: `' + $ReportPath + '`')
    $summary.Add("")
    $summary.Add("## Totals")
    $summary.Add("")
    $summary.Add("| Rows | Boot | Crash | Timeout | Static |")
    $summary.Add("| ---: | ---: | ---: | ---: | ---: |")
    $summary.Add("| $($rows.Count) | $bootCount | $crashCount | $timeoutCount | $staticCount |")
    $summary.Add("")
    $summary.Add("## Failure Phase Mix")
    $summary.Add("")
    $summary.Add("| Phase | Count |")
    $summary.Add("| --- | ---: |")
    foreach ($row in $byPhase) {
        $summary.Add("| $($row.phase) | $($row.count) |")
    }

    $summary.Add("")
    $summary.Add("## Failure Classification Mix")
    $summary.Add("")
    $summary.Add("| Classification | Count |")
    $summary.Add("| --- | ---: |")
    foreach ($row in $byClass) {
        $summary.Add("| $($row.classification) | $($row.count) |")
    }

    $summary.Add("")
    $summary.Add("## Top Failing ROMs")
    $summary.Add("")
    $summary.Add("| Count | Index | Title | Code |")
    $summary.Add("| ---: | ---: | --- | --- |")
    foreach ($row in $topRoms) {
        $title = ([string]$row.title).Replace("|", "\|")
        $summary.Add("| $($row.count) | $($row.index) | $title | $($row.gameCode) |")
    }

    $summary.Add("")
    $summary.Add("## Top Failure Errors")
    $summary.Add("")
    $summary.Add("| Count | Error |")
    $summary.Add("| ---: | --- |")
    foreach ($row in $topErrors) {
        $error = if ([string]::IsNullOrWhiteSpace($row.error)) { "(blank)" } else { ([string]$row.error).Replace("|", "\|") }
        $summary.Add("| $($row.count) | $error |")
    }

    $summaryPath = Join-Path $OutputDir "summary.md"
    Set-Content -LiteralPath $summaryPath -Value $summary

    Write-Host "Rows: $($rows.Count)"
    Write-Host "Boot: $bootCount"
    Write-Host "Crash: $crashCount"
    Write-Host "Timeout: $timeoutCount"
    Write-Host "Static: $staticCount"
    Write-Host "Analysis: $((Resolve-Path $OutputDir).Path)"
}
finally {
    Pop-Location
}
