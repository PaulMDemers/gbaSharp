param(
    [string]$Manifest = "docs\gba-longplay-strict-routes.csv",
    [string]$RomRoot = "curated_official_gba",
    [string]$BaselineDir = "visual-baselines\longplay",
    [string]$OutputRoot = "",
    [string[]]$Labels = @(),
    [int]$Repetitions = 2,
    [int]$ProcessTimeoutSeconds = 2400,
    [int]$RouteMaxSecondsCap = 0,
    [switch]$NoBuild,
    [switch]$NoBios,
    [switch]$AllowBaselineDiffs,
    [switch]$ListOnly,
    [switch]$NormalPriority
)

$ErrorActionPreference = "Stop"

function Get-SafeName([string]$Value) {
    return ($Value -replace '[^A-Za-z0-9._-]+', '-').Trim('-')
}

if ($Repetitions -le 0) {
    throw "Repetitions must be greater than zero."
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $repoRoot
try {
    if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
        $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
        $OutputRoot = "artifacts\route-repeatability-$stamp"
    }

    New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

    $routes = @(Import-Csv -LiteralPath $Manifest)
    if ($Labels.Count -gt 0) {
        $selectedLabels = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($labelGroup in $Labels) {
            foreach ($label in $labelGroup.Split(",", [StringSplitOptions]::RemoveEmptyEntries)) {
                [void]$selectedLabels.Add($label.Trim())
            }
        }

        $routes = @($routes | Where-Object { $selectedLabels.Contains([string]$_.label) })
        if ($routes.Count -eq 0) {
            throw "No routes matched -Labels: $($selectedLabels -join ', ')"
        }
    }

    $selectedRoutesPath = Join-Path $OutputRoot "selected-routes.csv"
    $routes | Export-Csv -LiteralPath $selectedRoutesPath -NoTypeInformation
    if ($ListOnly) {
        Write-Host "Selected $($routes.Count) route(s)."
        Write-Host "Selected routes: $((Resolve-Path $selectedRoutesPath).Path)"
        return
    }

    $rollup = [System.Collections.Generic.List[object]]::new()
    $firstRun = $true
    foreach ($route in $routes) {
        $label = [string]$route.label
        $safeLabel = Get-SafeName $label
        for ($repetition = 1; $repetition -le $Repetitions; $repetition++) {
            $runName = "{0}-r{1:D2}" -f $safeLabel, $repetition
            $runOutput = Join-Path $OutputRoot $runName
            $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
            $runnerStatus = "ok"
            $runnerMessage = ""

            $runnerParams = @{
                Manifest = $Manifest
                RomRoot = $RomRoot
                BaselineDir = $BaselineDir
                OutputRoot = $runOutput
                Labels = @($label)
                ProcessTimeoutSeconds = $ProcessTimeoutSeconds
            }

            if ($RouteMaxSecondsCap -gt 0) {
                $runnerParams.RouteMaxSecondsCap = $RouteMaxSecondsCap
            }

            if ($NoBuild -or -not $firstRun) {
                $runnerParams.NoBuild = $true
            }

            if ($NoBios) {
                $runnerParams.NoBios = $true
            }

            if (-not $AllowBaselineDiffs) {
                $runnerParams.FailOnBaselineDiff = $true
            }

            if ($NormalPriority) {
                $runnerParams.NormalPriority = $true
            }

            Write-Host "Repeatability $label repetition $repetition/$Repetitions"
            try {
                & (Join-Path $PSScriptRoot "run-deep-gameplay.ps1") @runnerParams
            }
            catch {
                $runnerStatus = "error"
                $runnerMessage = $_.Exception.Message
                Write-Warning "Repeatability run failed for $label repetition ${repetition}: $runnerMessage"
            }
            finally {
                $stopwatch.Stop()
                $firstRun = $false
            }

            $reportPath = Join-Path $runOutput "deep-gameplay.csv"
            if (Test-Path -LiteralPath $reportPath) {
                foreach ($row in @(Import-Csv -LiteralPath $reportPath)) {
                    $row | Add-Member -NotePropertyName repetition -NotePropertyValue $repetition -Force
                    $row | Add-Member -NotePropertyName runOutput -NotePropertyValue $runOutput -Force
                    $row | Add-Member -NotePropertyName runnerStatus -NotePropertyValue $runnerStatus -Force
                    $row | Add-Member -NotePropertyName runnerMessage -NotePropertyValue $runnerMessage -Force
                    $row | Add-Member -NotePropertyName elapsedSeconds -NotePropertyValue ([Math]::Round($stopwatch.Elapsed.TotalSeconds, 1)) -Force
                    $rollup.Add($row) | Out-Null
                }
            }
            else {
                $rollup.Add([pscustomobject]@{
                    label = $label
                    status = "missing-report"
                    baselineStatus = "skipped"
                    targetScene = [string]$route.targetScene
                    baselineRequired = [string]$route.baselineRequired
                    minDistinctPcs = [string]$route.minDistinctPcs
                    exitCode = ""
                    index = [string]$route.index
                    romPath = ""
                    stopFrame = [string]$route.stopFrame
                    observedFrame = ""
                    lastSnapshotFrame = ""
                    lastSnapshotPc = ""
                    maxSteps = [string]$route.maxSteps
                    maxSeconds = [string]$route.maxSeconds
                    inputScript = [string]$route.inputScript
                    saveFile = [string]$route.saveFile
                    snapshotRows = ""
                    distinctPcs = ""
                    finalPpm = ""
                    baselinePpm = ""
                    snapshotCsv = ""
                    actualHash = ""
                    baselineHash = ""
                    expectedScene = [string]$route.expectedScene
                    message = ""
                    repetition = $repetition
                    runOutput = $runOutput
                    runnerStatus = $runnerStatus
                    runnerMessage = $runnerMessage
                    elapsedSeconds = [Math]::Round($stopwatch.Elapsed.TotalSeconds, 1)
                }) | Out-Null
            }
        }
    }

    $rollupPath = Join-Path $OutputRoot "repeatability.csv"
    $rollup | Export-Csv -LiteralPath $rollupPath -NoTypeInformation

    $statusPath = Join-Path $OutputRoot "summary-status.csv"
    $rollup |
        Group-Object label, status, baselineStatus, runnerStatus |
        Sort-Object Name |
        Select-Object Count, Name |
        Export-Csv -LiteralPath $statusPath -NoTypeInformation

    Write-Host "Repeatability report: $((Resolve-Path $rollupPath).Path)"
    Write-Host "Repeatability status summary: $((Resolve-Path $statusPath).Path)"
}
finally {
    Pop-Location
}
