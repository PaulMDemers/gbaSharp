param(
    [string]$Manifest = "docs\gba-longplay-routes.csv",
    [string]$RomRoot = "curated_official_gba",
    [string]$OutputRoot = "",
    [int]$ChunkSize = 4,
    [int]$StartChunk = 0,
    [int]$MaxChunks = 0,
    [int]$ProcessTimeoutSeconds = 2400,
    [int]$RouteMaxSecondsCap = 0,
    [int]$ContactSheetColumns = 4,
    [int]$ContactSheetScale = 3,
    [int]$LowDiversityWarningThreshold = 8,
    [switch]$NoBuild,
    [switch]$NoBios,
    [switch]$NoContactSheet,
    [switch]$Resume,
    [switch]$NormalPriority
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $repoRoot
try {
    if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
        $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
        $OutputRoot = "artifacts\longplay-suite-$stamp"
    }

    $params = @{
        Manifest = $Manifest
        RomRoot = $RomRoot
        OutputRoot = $OutputRoot
        ChunkSize = $ChunkSize
        StartChunk = $StartChunk
        ProcessTimeoutSeconds = $ProcessTimeoutSeconds
        LowDiversityWarningThreshold = $LowDiversityWarningThreshold
        ContactSheetColumns = $ContactSheetColumns
        ContactSheetScale = $ContactSheetScale
        FailOnBaselineDiff = $true
    }

    if ($MaxChunks -gt 0) {
        $params.MaxChunks = $MaxChunks
    }

    if ($RouteMaxSecondsCap -gt 0) {
        $params.RouteMaxSecondsCap = $RouteMaxSecondsCap
    }

    if ($NoBuild) {
        $params.NoBuild = $true
    }

    if ($NoBios) {
        $params.NoBios = $true
    }

    if ($NoContactSheet) {
        $params.NoContactSheet = $true
    }

    if ($Resume) {
        $params.Resume = $true
    }

    if ($NormalPriority) {
        $params.NormalPriority = $true
    }

    & (Join-Path $PSScriptRoot "run-deep-gameplay-suite.ps1") @params
}
finally {
    Pop-Location
}
