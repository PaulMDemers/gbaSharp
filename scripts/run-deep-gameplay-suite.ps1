param(
    [string]$Manifest = "docs\gba-deep-gameplay-routes.csv",
    [string]$RomRoot = "curated_official_gba",
    [string]$BaselineDir = "visual-baselines\deep-gameplay",
    [string]$OutputRoot = "",
    [int]$ChunkSize = 5,
    [int]$StartChunk = 0,
    [int]$MaxChunks = 0,
    [int]$ProcessTimeoutSeconds = 900,
    [int]$ContactSheetColumns = 5,
    [int]$ContactSheetScale = 2,
    [int]$LowDiversityWarningThreshold = 8,
    [switch]$UpdateBaselines,
    [switch]$FailOnBaselineDiff,
    [switch]$NoBuild,
    [switch]$NoContactSheet,
    [switch]$IncludeNonPassContactSheet,
    [switch]$NormalPriority
)

$ErrorActionPreference = "Stop"

if ($ChunkSize -le 0) {
    throw "ChunkSize must be greater than zero."
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $repoRoot
try {
    if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
        $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
        $OutputRoot = "deep-gameplay-suite-$stamp"
    }

    New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

    $items = @(Import-Csv -LiteralPath $Manifest)
    if ($items.Count -eq 0) {
        throw "No routes found in $Manifest."
    }

    $totalChunks = [int][Math]::Ceiling($items.Count / $ChunkSize)
    $endChunk = $totalChunks - 1
    if ($MaxChunks -gt 0) {
        $endChunk = [Math]::Min($endChunk, $StartChunk + $MaxChunks - 1)
    }

    $combinedPath = Join-Path $OutputRoot "deep-gameplay.csv"
    if (Test-Path -LiteralPath $combinedPath) {
        Remove-Item -LiteralPath $combinedPath -Force
    }

    $chunkRows = @()
    for ($chunk = $StartChunk; $chunk -le $endChunk; $chunk++) {
        $skip = $chunk * $ChunkSize
        $count = [Math]::Min($ChunkSize, $items.Count - $skip)
        if ($count -le 0) {
            break
        }

        $chunkName = "chunk-{0:D2}-{1:D2}" -f ($skip + 1), ($skip + $count)
        $chunkOutput = Join-Path $OutputRoot $chunkName
        $runnerParams = @{
            Manifest = $Manifest
            RomRoot = $RomRoot
            BaselineDir = $BaselineDir
            OutputDir = $chunkOutput
            SkipItems = $skip
            MaxItems = $count
            ProcessTimeoutSeconds = $ProcessTimeoutSeconds
        }

        if ($UpdateBaselines) {
            $runnerParams.UpdateBaselines = $true
        }

        if ($FailOnBaselineDiff) {
            $runnerParams.FailOnBaselineDiff = $true
        }

        if ($NoBuild -or $chunk -gt $StartChunk) {
            $runnerParams.NoBuild = $true
        }

        if ($NormalPriority) {
            $runnerParams.NormalPriority = $true
        }

        Write-Host "Running deep gameplay suite $chunkName"
        & (Join-Path $PSScriptRoot "run-deep-gameplay.ps1") @runnerParams

        $chunkCsv = Join-Path $chunkOutput "deep-gameplay.csv"
        if (-not (Test-Path -LiteralPath $chunkCsv)) {
            throw "Expected chunk report missing: $chunkCsv"
        }

        $rows = @(Import-Csv -LiteralPath $chunkCsv)
        $chunkRows += $rows | ForEach-Object {
            $_ | Add-Member -NotePropertyName chunk -NotePropertyValue $chunkName -PassThru
        }
    }

    if ($chunkRows.Count -eq 0) {
        throw "No chunk rows were produced."
    }

    $chunkRows | Export-Csv -LiteralPath $combinedPath -NoTypeInformation

    $chunkRows |
        Group-Object status, baselineStatus |
        Sort-Object @{ Expression = "Count"; Descending = $true }, Name |
        Select-Object Count, Name |
        Export-Csv -LiteralPath (Join-Path $OutputRoot "summary-status-baseline.csv") -NoTypeInformation

    $chunkRows |
        Sort-Object {[int]$_.distinctPcs}, label |
        Select-Object label, status, baselineStatus, distinctPcs, snapshotRows, expectedScene, chunk |
        Export-Csv -LiteralPath (Join-Path $OutputRoot "summary-low-diversity.csv") -NoTypeInformation

    $badRows = @($chunkRows | Where-Object { $_.status -ne "pass" -or ($_.baselineRequired -ne "False" -and $_.baselineStatus -notin @("match", "updated")) })
    $lowDiversityRows = @()
    if ($LowDiversityWarningThreshold -gt 0) {
        $lowDiversityRows = @($chunkRows | Where-Object { [int]$_.distinctPcs -lt $LowDiversityWarningThreshold })
    }
    $summaryPath = Join-Path $OutputRoot "summary.md"
    $firstChunk = $StartChunk + 1
    $lastChunk = $endChunk + 1
    $lines = @(
        "# Deep Gameplay Suite",
        "",
        "- Manifest: $Manifest",
        "- Routes: $($chunkRows.Count)",
        "- Chunks: $firstChunk-$lastChunk of $totalChunks",
        "- Failing rows: $($badRows.Count)",
        "- Low-diversity warning threshold: $LowDiversityWarningThreshold distinct PCs",
        "- Low-diversity warnings: $($lowDiversityRows.Count)",
        "",
        "## Status",
        ""
    )

    $lines += ($chunkRows | Group-Object status, baselineStatus | Sort-Object @{ Expression = "Count"; Descending = $true }, Name | ForEach-Object {
        "- $($_.Name): $($_.Count)"
    })

    $lines += @("", "## Lowest Distinct PC Counts", "")
    $lines += ($chunkRows | Sort-Object {[int]$_.distinctPcs}, label | Select-Object -First 10 | ForEach-Object {
        "- $($_.label): distinctPcs=$($_.distinctPcs), snapshots=$($_.snapshotRows), status=$($_.status), baseline=$($_.baselineStatus)"
    })

    if ($lowDiversityRows.Count -gt 0) {
        $lines += @("", "## Low-Diversity Warnings", "")
        $lines += ($lowDiversityRows | Sort-Object {[int]$_.distinctPcs}, label | ForEach-Object {
            "- $($_.label): distinctPcs=$($_.distinctPcs), snapshots=$($_.snapshotRows), scene=$($_.expectedScene)"
        })
    }

    if ($badRows.Count -gt 0) {
        $lines += @("", "## Failures", "")
        $lines += ($badRows | Select-Object -First 20 | ForEach-Object {
            "- $($_.label): status=$($_.status), baseline=$($_.baselineStatus), message=$($_.message)"
        })
    }

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

        if ($IncludeNonPassContactSheet) {
            $contactSheetArgs += "--include-nonpass"
        }

        try {
            & python @contactSheetArgs
            Write-Host "Deep gameplay contact sheet: $((Resolve-Path $contactSheetPath).Path)"
        }
        catch {
            Write-Warning "Could not create contact sheet: $($_.Exception.Message)"
        }
    }

    Write-Host "Deep gameplay suite report: $((Resolve-Path $combinedPath).Path)"
    Write-Host "Deep gameplay suite summary: $((Resolve-Path $summaryPath).Path)"

    if ($FailOnBaselineDiff -and $badRows.Count -gt 0) {
        throw "Deep gameplay suite verification failed for $($badRows.Count) row(s)."
    }
}
finally {
    Pop-Location
}
