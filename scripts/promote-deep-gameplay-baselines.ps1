param(
    [string]$Manifest = "docs\gba-deep-gameplay-routes.csv",
    [string]$SourceReport = "",
    [string]$BaselineDir = "visual-baselines\deep-gameplay",
    [string]$OutputCsv = "",
    [switch]$Force
)

$ErrorActionPreference = "Stop"

function Get-FileHashOrEmpty {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return ""
    }

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

if ([string]::IsNullOrWhiteSpace($SourceReport)) {
    throw "SourceReport is required."
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $repoRoot
try {
    if (-not (Test-Path -LiteralPath $Manifest)) {
        throw "Manifest not found: $Manifest"
    }

    if (-not (Test-Path -LiteralPath $SourceReport)) {
        throw "Source report not found: $SourceReport"
    }

    New-Item -ItemType Directory -Force -Path $BaselineDir | Out-Null

    $manifestRows = @(Import-Csv -LiteralPath $Manifest)
    if ($manifestRows.Count -eq 0) {
        throw "No rows found in manifest: $Manifest"
    }

    $reportRows = @(Import-Csv -LiteralPath $SourceReport)
    if ($reportRows.Count -eq 0) {
        throw "No rows found in source report: $SourceReport"
    }

    $reportByLabel = @{}
    foreach ($row in $reportRows) {
        $reportByLabel[[string]$row.label] = $row
    }

    $results = @()
    foreach ($item in $manifestRows) {
        $label = [string]$item.label
        if (-not $reportByLabel.ContainsKey($label)) {
            throw "No source report row found for label: $label"
        }

        $sourceRow = $reportByLabel[$label]
        if ($sourceRow.status -ne "pass") {
            throw "Source row for $label is not pass: $($sourceRow.status)"
        }

        $sourceFrame = [string]$sourceRow.finalPpm
        if (-not (Test-Path -LiteralPath $sourceFrame)) {
            throw "Source final frame missing for $label`: $sourceFrame"
        }

        $destinationFrame = Join-Path $BaselineDir "$label.ppm"
        $sourceHash = Get-FileHashOrEmpty $sourceFrame
        $existingHash = Get-FileHashOrEmpty $destinationFrame
        if ((-not $Force) -and (Test-Path -LiteralPath $destinationFrame) -and $existingHash -ne $sourceHash) {
            throw "Baseline already exists with a different hash for $label. Use -Force to overwrite."
        }

        if ($Force -or (-not (Test-Path -LiteralPath $destinationFrame))) {
            Copy-Item -LiteralPath $sourceFrame -Destination $destinationFrame -Force:$Force
        }

        $destinationHash = Get-FileHashOrEmpty $destinationFrame
        $results += [pscustomobject]@{
            label = $label
            status = if ($destinationHash -eq $sourceHash) { "promoted" } else { "hash-mismatch" }
            sourceFrame = $sourceFrame
            baselineFrame = $destinationFrame
            sourceHash = $sourceHash
            baselineHash = $destinationHash
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($OutputCsv)) {
        $outputDir = Split-Path -Parent $OutputCsv
        if (-not [string]::IsNullOrWhiteSpace($outputDir)) {
            New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
        }

        $results | Export-Csv -LiteralPath $OutputCsv -NoTypeInformation
    }

    $badRows = @($results | Where-Object { $_.status -ne "promoted" })
    Write-Host "Promoted $($results.Count) baseline frame(s) to $BaselineDir"
    if (-not [string]::IsNullOrWhiteSpace($OutputCsv)) {
        Write-Host "Promotion report: $((Resolve-Path $OutputCsv).Path)"
    }

    if ($badRows.Count -gt 0) {
        throw "Baseline promotion produced $($badRows.Count) bad row(s)."
    }
}
finally {
    Pop-Location
}
