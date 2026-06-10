param(
    [string]$Manifest = "docs\gba-longplay-strict-routes.csv",
    [string]$RomRoot = "curated_official_gba",
    [string]$BaselineDir = "visual-baselines\longplay",
    [string]$OutputRoot = "",
    [string[]]$Labels = @(
        "sonic-advance-longplay",
        "doom-longplay",
        "gta-longplay",
        "mario-kart-longplay",
        "fzero-gp-longplay",
        "tony-hawk2-longplay",
        "pokemon-ruby-longplay"
    ),
    [int]$ProcessTimeoutSeconds = 2400,
    [int]$RouteMaxSecondsCap = 0,
    [switch]$NoBuild,
    [switch]$NoBios,
    [switch]$UpdateBaselines,
    [switch]$AllowBaselineDiffs,
    [switch]$ListOnly,
    [switch]$NormalPriority
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $repoRoot
try {
    if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
        $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
        $OutputRoot = "artifacts\hard-local-soak-$stamp"
    }

    $runnerParams = @{
        Manifest = $Manifest
        RomRoot = $RomRoot
        BaselineDir = $BaselineDir
        OutputRoot = $OutputRoot
        Labels = $Labels
        ProcessTimeoutSeconds = $ProcessTimeoutSeconds
    }

    if ($RouteMaxSecondsCap -gt 0) {
        $runnerParams.RouteMaxSecondsCap = $RouteMaxSecondsCap
    }

    if ($NoBuild) {
        $runnerParams.NoBuild = $true
    }

    if ($NoBios) {
        $runnerParams.NoBios = $true
    }

    if ($UpdateBaselines) {
        $runnerParams.UpdateBaselines = $true
    }

    if ($ListOnly) {
        $runnerParams.ListOnly = $true
    }

    if (-not $AllowBaselineDiffs) {
        $runnerParams.FailOnBaselineDiff = $true
    }

    if ($NormalPriority) {
        $runnerParams.NormalPriority = $true
    }

    & (Join-Path $PSScriptRoot "run-deep-gameplay.ps1") @runnerParams
}
finally {
    Pop-Location
}
