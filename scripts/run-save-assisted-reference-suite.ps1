param(
    [string]$ReferenceManifest = "docs\gba-reference-frames.csv",
    [string]$RouteManifest = "docs\gba-save-assisted-deep-routes.csv",
    [string]$RomRoot = "curated_official_gba",
    [string]$ReferenceRoot = "reference-captures\mgba",
    [string]$BaselineDir = "visual-baselines\deep-gameplay",
    [string]$OutputRoot = "",
    [int]$Window = 1200,
    [int]$Stride = 60,
    [int]$ChunkSize = 4,
    [int]$CaptureTimeoutSeconds = 2400,
    [switch]$CaptureMgba,
    [switch]$ForceCapture,
    [switch]$RunDeepGameplay,
    [switch]$NoBuild,
    [switch]$NormalPriority
)

$ErrorActionPreference = "Stop"

function Invoke-PythonChecked {
    param([string[]]$Arguments, [string]$Description)

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
    param([object[]]$Rows, [string]$Column)

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
        $OutputRoot = "artifacts\save-assisted-reference-suite-$stamp"
    }

    New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

    $checklistCsv = Join-Path $OutputRoot "reference-capture-checklist.csv"
    $checklistMd = Join-Path $OutputRoot "reference-capture-checklist.md"
    $validationCsv = Join-Path $OutputRoot "reference-capture-validation.csv"
    $comparisonCsv = Join-Path $OutputRoot "reference-frame-comparison.csv"
    $comparisonPng = Join-Path $OutputRoot "reference-frame-comparison.png"
    $diffDir = Join-Path $OutputRoot "reference-frame-diffs"
    $windowDir = Join-Path $OutputRoot "window-match"
    $summaryMd = Join-Path $OutputRoot "summary.md"

    if ($CaptureMgba) {
        $captureParams = @{
            Routes = $RouteManifest
            RomRoot = $RomRoot
            ReferenceRoot = $ReferenceRoot
            OutputRoot = (Join-Path $OutputRoot "mgba-captures")
            TimeoutSeconds = $CaptureTimeoutSeconds
        }
        if ($ForceCapture) {
            $captureParams.Force = $true
        }

        & (Join-Path $PSScriptRoot "run-mgba-reference-captures.ps1") @captureParams
    }

    if ($RunDeepGameplay) {
        $deepParams = @{
            Manifest = $RouteManifest
            RomRoot = $RomRoot
            BaselineDir = $BaselineDir
            OutputRoot = (Join-Path $OutputRoot "deep-gameplay")
            ChunkSize = $ChunkSize
            FailOnBaselineDiff = $true
            LowDiversityWarningThreshold = 8
        }
        if ($NoBuild) {
            $deepParams.NoBuild = $true
        }
        if ($NormalPriority) {
            $deepParams.NormalPriority = $true
        }

        & (Join-Path $PSScriptRoot "run-deep-gameplay-suite.ps1") @deepParams
    }

    Invoke-PythonChecked -Description "Save-assisted reference checklist" -Arguments @(
        (Join-Path $PSScriptRoot "new-reference-capture-checklist.py"),
        "--reference-manifest", $ReferenceManifest,
        "--route-manifest", $RouteManifest,
        "--rom-root", $RomRoot,
        "--csv-output", $checklistCsv,
        "--markdown-output", $checklistMd
    )

    Invoke-PythonChecked -Description "Save-assisted reference validation" -Arguments @(
        (Join-Path $PSScriptRoot "validate-reference-captures.py"),
        "--manifest", $ReferenceManifest,
        "--output", $validationCsv,
        "--reference-root", $ReferenceRoot
    )

    Invoke-PythonChecked -Description "Save-assisted reference comparison" -Arguments @(
        (Join-Path $PSScriptRoot "compare-reference-frames.py"),
        "--manifest", $ReferenceManifest,
        "--output", $comparisonCsv,
        "--diff-dir", $diffDir,
        "--write-diffs",
        "--contact-sheet", $comparisonPng
    )

    $windowArgs = @(
        (Join-Path $PSScriptRoot "match-mgba-reference-windows.py"),
        "--routes", $RouteManifest,
        "--references", $ReferenceManifest,
        "--comparison", $comparisonCsv,
        "--rom-root", $RomRoot,
        "--output-dir", $windowDir,
        "--window", "$Window",
        "--stride", "$Stride"
    )
    if ($NoBuild) {
        $windowArgs += "--no-build"
    }

    Invoke-PythonChecked -Description "Save-assisted window matching" -Arguments $windowArgs

    $validationRows = @(Import-Csv -LiteralPath $validationCsv)
    $comparisonRows = @(Import-Csv -LiteralPath $comparisonCsv)
    $windowCsv = Join-Path $windowDir "window-match.csv"
    $windowRows = if (Test-Path -LiteralPath $windowCsv) { @(Import-Csv -LiteralPath $windowCsv) } else { @() }

    $lines = @(
        "# Save-Assisted Reference Suite",
        "",
        "- Reference manifest: $ReferenceManifest",
        "- Route manifest: $RouteManifest",
        "- Reference root: $ReferenceRoot",
        "- Output root: $OutputRoot",
        "- Captured mGBA references: $CaptureMgba",
        "- Refreshed gbaSharp deep gameplay: $RunDeepGameplay",
        "- Window matching: +/-$Window frames, stride $Stride",
        "",
        "## Artifacts",
        "",
        "- Capture checklist: $checklistMd",
        "- Capture validation CSV: $validationCsv",
        "- Frame comparison CSV: $comparisonCsv",
        "- Frame comparison contact sheet: $comparisonPng",
        "- Window match CSV: $windowCsv",
        "- Window match contact sheet: $(Join-Path $windowDir "window-match.png")",
        "",
        "## Capture Validation",
        ""
    )
    $lines += Join-StatusSummary -Rows $validationRows -Column "status"
    $lines += @("", "## Direct Frame Comparison", "")
    $lines += Join-StatusSummary -Rows $comparisonRows -Column "status"
    $lines += @("", "## Window Match Classification", "")
    $lines += Join-StatusSummary -Rows $windowRows -Column "classification"

    if ($windowRows.Count -gt 0) {
        $lines += @("", "## Window Match Details", "")
        $lines += ($windowRows | Sort-Object label | ForEach-Object {
            "- $($_.label): $($_.classification), bestFrame=$($_.bestFrame), offset=$($_.frameOffset), diffPixels=$($_.bestDifferentPixels), ssim=$($_.bestStructuralSimilarity)"
        })
    }

    $lines | Set-Content -LiteralPath $summaryMd -Encoding UTF8
    Write-Host "Save-assisted reference suite summary: $((Resolve-Path $summaryMd).Path)"
}
finally {
    Pop-Location
}
