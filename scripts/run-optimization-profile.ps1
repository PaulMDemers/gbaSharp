param(
    [string]$RomRoot = ".",
    [string]$OutputDir = "",
    [string]$Indexes = "1-3",
    [ValidateSet("boot", "standard", "input", "gameplay", "single")]
    [string]$Suite = "boot",
    [int]$StopFrame = 120,
    [int]$MaxSteps = 1500000,
    [int]$MaxSeconds = 30,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutputDir = Join-Path (Get-Location) "artifacts\optimization-profile-$stamp"
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$reportPath = Join-Path $OutputDir "compat.csv"
$summaryPath = Join-Path $OutputDir "summary.csv"
$profilePath = Join-Path $OutputDir "profile.csv"

if (-not $NoBuild) {
    dotnet build .\src\Gba.Cli\Gba.Cli.csproj -c Release
}

$arguments = @(
    'run',
    '--project', '.\src\Gba.Cli\Gba.Cli.csproj',
    '-c', 'Release'
)

if ($NoBuild) {
    $arguments += '--no-build'
}

$arguments += @(
    '--',
    'compat', $RomRoot,
    '--indexes', $Indexes,
    '--suite', $Suite,
    '--stop-frame', $StopFrame,
    '--max-steps', $MaxSteps,
    '--max-seconds', $MaxSeconds,
    '--output', $reportPath,
    '--summary-output', $summaryPath,
    '--profile-output', $profilePath
)

dotnet @arguments

$rows = Import-Csv -LiteralPath $profilePath
$count = @($rows).Count
if ($count -eq 0) {
    Write-Output "No profile rows written: $profilePath"
    exit 0
}

$avgSteps = ($rows | Measure-Object -Property stepsPerSecond -Average).Average
$avgFrames = ($rows | Measure-Object -Property framesPerSecond -Average).Average
$avgCpu = ($rows | Measure-Object -Property cpuPct -Average).Average
$avgBus = ($rows | Measure-Object -Property busPct -Average).Average
$avgScheduler = ($rows | Measure-Object -Property schedulerPct -Average).Average

[pscustomobject]@{
    Rows = $count
    AverageStepsPerSecond = [math]::Round($avgSteps, 0)
    AverageFramesPerSecond = [math]::Round($avgFrames, 2)
    AverageCpuPercent = [math]::Round($avgCpu, 1)
    AverageBusPercent = [math]::Round($avgBus, 1)
    AverageSchedulerPercent = [math]::Round($avgScheduler, 1)
    Report = (Resolve-Path -LiteralPath $reportPath).Path
    Summary = (Resolve-Path -LiteralPath $summaryPath).Path
    Profile = (Resolve-Path -LiteralPath $profilePath).Path
} | Format-List
