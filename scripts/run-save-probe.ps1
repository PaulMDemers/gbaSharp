param(
    [string]$Manifest = "docs\gba-a-plus-milestone.csv",
    [int]$Priority = 0,
    [string]$Category = "",
    [int]$SkipItems = 0,
    [int]$MaxItems = 0,
    [string]$OutputDir = "",
    [switch]$Resume
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $repoRoot
try {
    try {
        (Get-Process -Id $PID).PriorityClass = "BelowNormal"
        Write-Host "Running save probe at BelowNormal process priority."
    }
    catch {
        Write-Warning "Could not lower process priority: $($_.Exception.Message)"
    }

    if ([string]::IsNullOrWhiteSpace($OutputDir)) {
        $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
        $OutputDir = "save-probe-$stamp"
    }

    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
    $report = Join-Path $OutputDir "save-probe.csv"
    $summary = Join-Path $OutputDir "save-probe-summary.csv"

    if ($Resume -and (Test-Path $report)) {
        Write-Host "Save probe report already exists: $((Resolve-Path $report).Path)"
        return
    }

    $items = @(Import-Csv $Manifest | Where-Object { -not [string]::IsNullOrWhiteSpace($_.index) })
    if ($Priority -gt 0) {
        $items = @($items | Where-Object { [int]$_.priority -eq $Priority })
    }

    if (-not [string]::IsNullOrWhiteSpace($Category)) {
        $items = @($items | Where-Object { $_.category -eq $Category })
    }

    if ($SkipItems -gt 0) {
        $items = @($items | Select-Object -Skip $SkipItems)
    }

    if ($MaxItems -gt 0) {
        $items = @($items | Select-Object -First $MaxItems)
    }

    if ($items.Count -eq 0) {
        throw "No milestone rows matched the requested filters."
    }

    $indexes = ($items | ForEach-Object { $_.index }) -join ","
    & dotnet run --project src\Gba.Cli --no-build -- save-probe gba_collection --indexes $indexes --output $report --summary-output $summary
    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 4) {
        throw "Save probe failed with exit code $LASTEXITCODE"
    }

    Write-Host "Save probe report: $((Resolve-Path $report).Path)"
    Write-Host "Save probe summary: $((Resolve-Path $summary).Path)"
}
finally {
    Pop-Location
}
