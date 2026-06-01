param(
    [string]$ReferenceManifest = "docs\gba-reference-frames.csv",
    [string]$RouteManifest = "docs\gba-save-assisted-deep-routes.csv",
    [string]$RomRoot = "curated_official_gba",
    [string]$BaselineDir = "visual-baselines\deep-gameplay",
    [string]$OutputRoot = "",
    [int]$ChunkSize = 4,
    [int]$RouteMaxSecondsCap = 0,
    [switch]$RunDeepGameplay,
    [switch]$UpdateBaselines,
    [switch]$StrictReferences,
    [switch]$NoBuild,
    [switch]$NormalPriority
)

$ErrorActionPreference = "Stop"

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

function Invoke-PythonChecked {
    param([string[]]$Arguments, [string]$Description, [bool]$AllowFailure = $false)

    $output = & python @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    foreach ($line in $output) {
        Write-Host $line
    }

    if ($exitCode -ne 0 -and -not $AllowFailure) {
        throw "$Description failed with exit code $exitCode."
    }

    return $exitCode
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $repoRoot
try {
    if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
        $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
        $OutputRoot = "artifacts\reference-dashboard-$stamp"
    }

    New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

    $checklistCsv = Join-Path $OutputRoot "reference-capture-checklist.csv"
    $checklistMd = Join-Path $OutputRoot "reference-capture-checklist.md"
    $validationCsv = Join-Path $OutputRoot "reference-capture-validation.csv"
    $comparisonCsv = Join-Path $OutputRoot "reference-frame-comparison.csv"
    $comparisonPng = Join-Path $OutputRoot "reference-frame-comparison.png"
    $diffDir = Join-Path $OutputRoot "reference-frame-diffs"
    $summaryMd = Join-Path $OutputRoot "summary.md"
    $deepOutput = Join-Path $OutputRoot "deep-gameplay"

    $deepExitCode = 0
    if ($RunDeepGameplay) {
        $suiteParams = @{
            Manifest = $RouteManifest
            RomRoot = $RomRoot
            BaselineDir = $BaselineDir
            OutputRoot = $deepOutput
            ChunkSize = $ChunkSize
            FailOnBaselineDiff = $true
            LowDiversityWarningThreshold = 8
        }

        if ($RouteMaxSecondsCap -gt 0) {
            $suiteParams.RouteMaxSecondsCap = $RouteMaxSecondsCap
        }

        if ($UpdateBaselines) {
            $suiteParams.UpdateBaselines = $true
        }

        if ($NoBuild) {
            $suiteParams.NoBuild = $true
        }

        if ($NormalPriority) {
            $suiteParams.NormalPriority = $true
        }

        try {
            & (Join-Path $PSScriptRoot "run-deep-gameplay-suite.ps1") @suiteParams
        }
        catch {
            $deepExitCode = 1
            Write-Warning "Deep gameplay suite failed: $($_.Exception.Message)"
        }
    }

    Invoke-PythonChecked -Description "Reference capture checklist" -Arguments @(
        (Join-Path $PSScriptRoot "new-reference-capture-checklist.py"),
        "--reference-manifest", $ReferenceManifest,
        "--route-manifest", $RouteManifest,
        "--rom-root", $RomRoot,
        "--csv-output", $checklistCsv,
        "--markdown-output", $checklistMd
    ) | Out-Null

    $validationArgs = @(
        (Join-Path $PSScriptRoot "validate-reference-captures.py"),
        "--manifest", $ReferenceManifest,
        "--output", $validationCsv
    )
    if ($StrictReferences) {
        $validationArgs += @("--fail-on-missing", "--fail-on-invalid", "--fail-on-extra")
    }
    $validationExitCode = Invoke-PythonChecked -Description "Reference capture validation" -Arguments $validationArgs -AllowFailure:$StrictReferences

    $comparisonArgs = @(
        (Join-Path $PSScriptRoot "compare-reference-frames.py"),
        "--manifest", $ReferenceManifest,
        "--output", $comparisonCsv,
        "--diff-dir", $diffDir,
        "--write-diffs",
        "--contact-sheet", $comparisonPng
    )
    if ($StrictReferences) {
        $comparisonArgs += @("--fail-on-diff", "--fail-on-missing")
    }
    $comparisonExitCode = Invoke-PythonChecked -Description "Reference frame comparison" -Arguments $comparisonArgs -AllowFailure:$StrictReferences

    $validationRows = @(Import-Csv -LiteralPath $validationCsv)
    $comparisonRows = @(Import-Csv -LiteralPath $comparisonCsv)
    $deepRows = @()
    $deepSummary = Join-Path $deepOutput "summary.md"
    if (Test-Path -LiteralPath (Join-Path $deepOutput "deep-gameplay.csv")) {
        $deepRows = @(Import-Csv -LiteralPath (Join-Path $deepOutput "deep-gameplay.csv"))
    }

    $lines = @(
        "# Reference Dashboard",
        "",
        "- Reference manifest: $ReferenceManifest",
        "- Route manifest: $RouteManifest",
        "- Output root: $OutputRoot",
        "- Deep gameplay refreshed: $RunDeepGameplay",
        "- Route max-seconds cap: $RouteMaxSecondsCap",
        "- Strict references: $StrictReferences",
        "- Validation exit code: $validationExitCode",
        "- Comparison exit code: $comparisonExitCode",
        "",
        "## Artifacts",
        "",
        "- Capture checklist: $checklistMd",
        "- Capture validation CSV: $validationCsv",
        "- Frame comparison CSV: $comparisonCsv",
        "- Frame comparison contact sheet: $comparisonPng"
    )

    if (Test-Path -LiteralPath $deepSummary) {
        $lines += "- Deep gameplay summary: $deepSummary"
    }

    $lines += @("", "## Capture Validation", "")
    $lines += Join-StatusSummary -Rows $validationRows -Column "status"

    $lines += @("", "## Frame Comparison", "")
    $lines += Join-StatusSummary -Rows $comparisonRows -Column "status"

    if ($RunDeepGameplay) {
        $lines += @("", "## Deep Gameplay", "")
        if ($deepRows.Count -gt 0) {
            $lines += Join-StatusSummary -Rows $deepRows -Column "status"
            $lines += @("", "## Deep Baselines", "")
            $lines += Join-StatusSummary -Rows $deepRows -Column "baselineStatus"
        }
        else {
            $lines += "- unavailable: deep gameplay run did not produce a combined CSV"
        }
    }

    $missing = @($validationRows | Where-Object { $_.status -eq "missing" })
    $invalid = @($validationRows | Where-Object { $_.status -in @("bad-size", "unreadable") })
    $diffs = @($comparisonRows | Where-Object { $_.status -eq "diff" })
    if ($missing.Count -gt 0 -or $invalid.Count -gt 0 -or $diffs.Count -gt 0 -or $deepExitCode -ne 0) {
        $lines += @("", "## Attention", "")
        if ($missing.Count -gt 0) {
            $lines += "- Missing reference captures: $($missing.Count)"
        }
        if ($invalid.Count -gt 0) {
            $lines += "- Invalid reference captures: $($invalid.Count)"
        }
        if ($diffs.Count -gt 0) {
            $lines += "- Reference frame diffs: $($diffs.Count)"
        }
        if ($deepExitCode -ne 0) {
            $lines += "- Deep gameplay refresh failed; inspect $deepOutput"
        }
    }

    $lines | Set-Content -LiteralPath $summaryMd -Encoding UTF8

    Write-Host "Reference dashboard summary: $((Resolve-Path $summaryMd).Path)"

    if ($StrictReferences -and ($validationExitCode -ne 0 -or $comparisonExitCode -ne 0)) {
        throw "Strict reference dashboard failed. See $summaryMd"
    }

    if ($deepExitCode -ne 0) {
        throw "Reference dashboard completed with deep gameplay failure. See $summaryMd"
    }
}
finally {
    Pop-Location
}
