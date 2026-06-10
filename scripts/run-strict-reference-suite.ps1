param(
    [string]$Manifest = "docs\gba-longplay-reference-frames.csv",
    [string]$ReferenceRoot = "reference-captures\mgba\longplay",
    [string]$OutputRoot = "",
    [switch]$FailOnExtra,
    [switch]$NoDiffImages
)

$ErrorActionPreference = "Stop"

function Invoke-PythonChecked {
    param(
        [string[]]$Arguments,
        [string]$Description
    )

    $output = & python @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    foreach ($line in $output) {
        Write-Host $line
    }

    if ($exitCode -ne 0) {
        throw "$Description failed with exit code $exitCode."
    }
}

function Join-StatusSummary {
    param(
        [object[]]$Rows,
        [string]$Column
    )

    if ($Rows.Count -eq 0) {
        return @("- none")
    }

    return @($Rows |
        Group-Object -Property $Column |
        Sort-Object @{ Expression = "Count"; Descending = $true }, Name |
        ForEach-Object { "- $($_.Name): $($_.Count)" })
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $repoRoot
try {
    if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
        $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
        $OutputRoot = "artifacts\strict-reference-suite-$stamp"
    }

    New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

    $validationCsv = Join-Path $OutputRoot "reference-capture-validation.csv"
    $comparisonCsv = Join-Path $OutputRoot "reference-frame-comparison.csv"
    $summaryCsv = Join-Path $OutputRoot "reference-frame-summary.csv"
    $contactSheet = Join-Path $OutputRoot "reference-frame-comparison.png"
    $diffDir = Join-Path $OutputRoot "reference-frame-diffs"
    $summaryMd = Join-Path $OutputRoot "summary.md"

    $validationArgs = @(
        (Join-Path $PSScriptRoot "validate-reference-captures.py"),
        "--manifest", $Manifest,
        "--output", $validationCsv,
        "--reference-root", $ReferenceRoot,
        "--fail-on-missing",
        "--fail-on-invalid"
    )
    if ($FailOnExtra) {
        $validationArgs += "--fail-on-extra"
    }

    Invoke-PythonChecked -Description "Strict reference capture validation" -Arguments $validationArgs

    $comparisonArgs = @(
        (Join-Path $PSScriptRoot "compare-reference-frames.py"),
        "--manifest", $Manifest,
        "--output", $comparisonCsv,
        "--diff-dir", $diffDir,
        "--contact-sheet", $contactSheet,
        "--fail-on-diff",
        "--fail-on-missing"
    )
    if (-not $NoDiffImages) {
        $comparisonArgs += "--write-diffs"
    }

    Invoke-PythonChecked -Description "Strict reference frame comparison" -Arguments $comparisonArgs

    Invoke-PythonChecked -Description "Strict reference summary" -Arguments @(
        (Join-Path $PSScriptRoot "summarize-reference-comparison.py"),
        "--manifest", $Manifest,
        "--output", $summaryCsv
    )

    $validationRows = @(Import-Csv -LiteralPath $validationCsv)
    $comparisonRows = @(Import-Csv -LiteralPath $comparisonCsv)
    $summaryRows = @(Import-Csv -LiteralPath $summaryCsv)
    $extras = @($validationRows | Where-Object { $_.status -eq "extra" })

    $lines = @(
        "# Strict Reference Suite",
        "",
        "- Manifest: $Manifest",
        "- Reference root: $ReferenceRoot",
        "- Output root: $OutputRoot",
        "- Fail on extra captures: $FailOnExtra",
        "",
        "## Artifacts",
        "",
        "- Capture validation CSV: $validationCsv",
        "- Frame comparison CSV: $comparisonCsv",
        "- Frame comparison summary CSV: $summaryCsv",
        "- Frame comparison contact sheet: $contactSheet",
        "",
        "## Capture Validation",
        ""
    )

    $lines += Join-StatusSummary -Rows $validationRows -Column "status"
    $lines += @("", "## Frame Comparison", "")
    $lines += Join-StatusSummary -Rows $comparisonRows -Column "status"
    $lines += @("", "## Highest Pixel Deltas Within Tolerance", "")
    $lines += @($summaryRows |
        Sort-Object { [int]$_.differentPixels } -Descending |
        Select-Object -First 8 |
        ForEach-Object { "- $($_.label): status=$($_.status), differentPixels=$($_.differentPixels), allowed=$($_.allowedDifferentPixels)" })

    if ($extras.Count -gt 0) {
        $lines += @("", "## Extra Captures", "")
        $lines += @($extras | Select-Object -First 20 | ForEach-Object {
            "- $($_.label): $($_.referenceImage)"
        })
    }

    $lines | Set-Content -LiteralPath $summaryMd -Encoding UTF8

    Write-Host "Strict reference suite summary: $((Resolve-Path $summaryMd).Path)"
}
finally {
    Pop-Location
}
