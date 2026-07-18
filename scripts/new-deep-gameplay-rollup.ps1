param(
    [string]$Manifest = "docs\gba-deep-gameplay-routes.csv",
    [Parameter(Mandatory = $true)]
    [string[]]$Reports,
    [string]$OutputRoot = "",
    [int]$LowDiversityWarningThreshold = 8,
    [int]$ContactSheetColumns = 5,
    [int]$ContactSheetScale = 2,
    [switch]$NoContactSheet,
    [switch]$FailOnWarnings
)

$ErrorActionPreference = "Stop"

function Get-IntOrDefault {
    param([string]$Value, [int]$Default = 0)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $Default
    }

    return [int]$Value
}

function Test-BaselineOk {
    param([object]$Row)

    if ($Row.baselineRequired -eq "False") {
        return $true
    }

    return $Row.baselineStatus -in @("match", "updated")
}

function Get-RouteThreshold {
    param([object]$Row, [int]$DefaultThreshold)

    if ($manifestByLabel.ContainsKey($Row.label)) {
        $manifestThreshold = [string]$manifestByLabel[$Row.label].Row.minDistinctPcs
        if (-not [string]::IsNullOrWhiteSpace($manifestThreshold)) {
            return [int]$manifestThreshold
        }
    }

    if ($Row.PSObject.Properties.Name -contains "minDistinctPcs" -and -not [string]::IsNullOrWhiteSpace($Row.minDistinctPcs)) {
        return [int]$Row.minDistinctPcs
    }

    return $DefaultThreshold
}

function Get-ActivityDiversity {
    param([object]$Row)

    if ($Row.PSObject.Properties.Name -contains "activityDiversity" -and -not [string]::IsNullOrWhiteSpace($Row.activityDiversity)) {
        return [int]$Row.activityDiversity
    }

    return Get-IntOrDefault $Row.distinctPcs
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $repoRoot
try {
    if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
        $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
        $OutputRoot = "deep-gameplay-rollup-$stamp"
    }

    New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

    $manifestRows = @(Import-Csv -LiteralPath $Manifest)
    if ($manifestRows.Count -eq 0) {
        throw "No routes found in $Manifest."
    }

    $manifestByLabel = @{}
    for ($i = 0; $i -lt $manifestRows.Count; $i++) {
        $manifestByLabel[$manifestRows[$i].label] = [pscustomobject]@{
            Order = $i
            Row = $manifestRows[$i]
        }
    }

    $latestRows = @{}
    for ($reportIndex = 0; $reportIndex -lt $Reports.Count; $reportIndex++) {
        $reportPath = $Reports[$reportIndex]
        if (-not (Test-Path -LiteralPath $reportPath)) {
            throw "Report not found: $reportPath"
        }

        $rows = @(Import-Csv -LiteralPath $reportPath)
        foreach ($row in $rows) {
            if ([string]::IsNullOrWhiteSpace($row.label)) {
                continue
            }

            $row | Add-Member -NotePropertyName sourceReport -NotePropertyValue $reportPath -Force
            $row | Add-Member -NotePropertyName sourceOrder -NotePropertyValue $reportIndex -Force
            if ($manifestByLabel.ContainsKey($row.label)) {
                $row | Add-Member -NotePropertyName manifestOrder -NotePropertyValue $manifestByLabel[$row.label].Order -Force
            }
            else {
                $row | Add-Member -NotePropertyName manifestOrder -NotePropertyValue ([int]::MaxValue) -Force
            }

            $latestRows[$row.label] = $row
        }
    }

    $missingLabels = @($manifestRows | Where-Object { -not $latestRows.ContainsKey($_.label) } | ForEach-Object { $_.label })
    $combinedRows = @($manifestRows | ForEach-Object { $latestRows[$_.label] } | Where-Object { $null -ne $_ })

    $combinedPath = Join-Path $OutputRoot "deep-gameplay.csv"
    $combinedRows | Export-Csv -LiteralPath $combinedPath -NoTypeInformation

    $combinedRows |
        Group-Object status, baselineStatus |
        Sort-Object @{ Expression = "Count"; Descending = $true }, Name |
        Select-Object Count, Name |
        Export-Csv -LiteralPath (Join-Path $OutputRoot "summary-status-baseline.csv") -NoTypeInformation

    $combinedRows |
        Sort-Object @{ Expression = { Get-ActivityDiversity $_ }; Ascending = $true }, label |
        Select-Object label, status, baselineStatus, activityDiversity, distinctPcs, distinctFrames, minDistinctPcs, snapshotRows, expectedScene, sourceReport |
        Export-Csv -LiteralPath (Join-Path $OutputRoot "summary-low-diversity.csv") -NoTypeInformation

    $badRows = @($combinedRows | Where-Object { $_.status -ne "pass" -or -not (Test-BaselineOk $_) })
    $lowDiversityRows = @()
    if ($LowDiversityWarningThreshold -gt 0) {
        $lowDiversityRows = @($combinedRows | Where-Object {
            $threshold = Get-RouteThreshold -Row $_ -DefaultThreshold $LowDiversityWarningThreshold
            (Get-ActivityDiversity $_) -lt $threshold
        })
    }

    $sourceCounts = @($combinedRows | Group-Object sourceReport | Sort-Object Name)
    $summaryPath = Join-Path $OutputRoot "summary.md"
    $lines = @(
        "# Deep Gameplay Rollup",
        "",
        "- Manifest: $Manifest",
        "- Manifest routes: $($manifestRows.Count)",
        "- Reports: $($Reports.Count)",
        "- Covered routes: $($combinedRows.Count)",
        "- Missing routes: $($missingLabels.Count)",
        "- Failing rows: $($badRows.Count)",
        "- Low-activity warning threshold: $LowDiversityWarningThreshold distinct PCs or frames",
        "- Low-diversity warnings: $($lowDiversityRows.Count)",
        "",
        "## Status",
        ""
    )

    $lines += ($combinedRows | Group-Object status, baselineStatus | Sort-Object @{ Expression = "Count"; Descending = $true }, Name | ForEach-Object {
        "- $($_.Name): $($_.Count)"
    })

    $lines += @("", "## Lowest Activity Diversity", "")
    $lines += ($combinedRows | Sort-Object @{ Expression = { Get-ActivityDiversity $_ }; Ascending = $true }, label | Select-Object -First 12 | ForEach-Object {
        $threshold = Get-RouteThreshold -Row $_ -DefaultThreshold $LowDiversityWarningThreshold

        "- $($_.label): activity=$(Get-ActivityDiversity $_), distinctPcs=$($_.distinctPcs), distinctFrames=$($_.distinctFrames), threshold=$threshold, snapshots=$($_.snapshotRows), baseline=$($_.baselineStatus)"
    })

    if ($missingLabels.Count -gt 0) {
        $lines += @("", "## Missing Routes", "")
        $lines += ($missingLabels | ForEach-Object { "- $_" })
    }

    if ($lowDiversityRows.Count -gt 0) {
        $lines += @("", "## Low-Diversity Warnings", "")
        $lines += ($lowDiversityRows | Sort-Object @{ Expression = { Get-ActivityDiversity $_ }; Ascending = $true }, label | ForEach-Object {
            $threshold = Get-RouteThreshold -Row $_ -DefaultThreshold $LowDiversityWarningThreshold

            "- $($_.label): activity=$(Get-ActivityDiversity $_), distinctPcs=$($_.distinctPcs), distinctFrames=$($_.distinctFrames), threshold=$threshold, snapshots=$($_.snapshotRows), scene=$($_.expectedScene)"
        })
    }

    if ($badRows.Count -gt 0) {
        $lines += @("", "## Failures", "")
        $lines += ($badRows | Select-Object -First 20 | ForEach-Object {
            "- $($_.label): status=$($_.status), baseline=$($_.baselineStatus), source=$($_.sourceReport), message=$($_.message)"
        })
    }

    $lines += @("", "## Source Reports", "")
    $lines += ($sourceCounts | ForEach-Object { "- $($_.Name): $($_.Count) route(s)" })

    $lines | Set-Content -LiteralPath $summaryPath -Encoding UTF8

    if (-not $NoContactSheet) {
        $contactSheetPath = Join-Path $OutputRoot "contact-sheet.png"
        $contactSheetArgs = @(
            (Join-Path $PSScriptRoot "new-deep-gameplay-contact-sheet.py"),
            $combinedPath,
            "--output", $contactSheetPath,
            "--columns", "$ContactSheetColumns",
            "--scale", "$ContactSheetScale"
        )

        try {
            & python @contactSheetArgs
            Write-Host "Deep gameplay rollup contact sheet: $((Resolve-Path $contactSheetPath).Path)"
        }
        catch {
            Write-Warning "Could not create contact sheet: $($_.Exception.Message)"
        }
    }

    Write-Host "Deep gameplay rollup report: $((Resolve-Path $combinedPath).Path)"
    Write-Host "Deep gameplay rollup summary: $((Resolve-Path $summaryPath).Path)"

    if ($FailOnWarnings -and ($missingLabels.Count -gt 0 -or $badRows.Count -gt 0 -or $lowDiversityRows.Count -gt 0)) {
        throw "Deep gameplay rollup completed with warnings."
    }
}
finally {
    Pop-Location
}
