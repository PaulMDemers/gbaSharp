param(
    [Parameter(Mandatory = $true)]
    [string]$BaselineProfile,
    [Parameter(Mandatory = $true)]
    [string]$CandidateProfile,
    [string]$OutputPath = "",
    [string[]]$KeyColumns = @("index", "phase")
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $BaselineProfile)) {
    throw "Baseline profile not found: $BaselineProfile"
}

if (-not (Test-Path -LiteralPath $CandidateProfile)) {
    throw "Candidate profile not found: $CandidateProfile"
}

$baselineRows = Import-Csv -LiteralPath $BaselineProfile
$candidateRows = Import-Csv -LiteralPath $CandidateProfile
$keySeparator = [char]31

function Get-Key($row) {
    ($KeyColumns | ForEach-Object { [string]$row.$_ }) -join $keySeparator
}

function Get-Double($row, [string]$name) {
    $value = $row.$name
    if ([string]::IsNullOrWhiteSpace($value)) {
        return 0.0
    }

    return [double]::Parse($value, [Globalization.CultureInfo]::InvariantCulture)
}

$baselineByKey = @{}
foreach ($row in $baselineRows) {
    $baselineByKey[(Get-Key $row)] = $row
}

$comparisons = foreach ($candidate in $candidateRows) {
    $key = Get-Key $candidate
    if (-not $baselineByKey.ContainsKey($key)) {
        continue
    }

    $baseline = $baselineByKey[$key]
    $baselineSteps = Get-Double $baseline "stepsPerSecond"
    $candidateSteps = Get-Double $candidate "stepsPerSecond"
    $baselineFps = Get-Double $baseline "framesPerSecond"
    $candidateFps = Get-Double $candidate "framesPerSecond"
    $stepChange = if ($baselineSteps -eq 0) { 0 } else { (($candidateSteps / $baselineSteps) - 1) * 100 }
    $fpsChange = if ($baselineFps -eq 0) { 0 } else { (($candidateFps / $baselineFps) - 1) * 100 }

    [pscustomobject]@{
        Key = ($key -split [regex]::Escape([string]$keySeparator)) -join "|"
        Index = $candidate.index
        Phase = $candidate.phase
        Title = $candidate.title
        GameCode = $candidate.gameCode
        BaselineStepsPerSecond = [math]::Round($baselineSteps, 0)
        CandidateStepsPerSecond = [math]::Round($candidateSteps, 0)
        StepChangePercent = [math]::Round($stepChange, 2)
        BaselineFramesPerSecond = [math]::Round($baselineFps, 2)
        CandidateFramesPerSecond = [math]::Round($candidateFps, 2)
        FrameChangePercent = [math]::Round($fpsChange, 2)
        BaselineSchedulerPercent = Get-Double $baseline "schedulerPct"
        CandidateSchedulerPercent = Get-Double $candidate "schedulerPct"
    }
}

$comparisonRows = @($comparisons)
if ($comparisonRows.Count -eq 0) {
    Write-Output "No matching rows found."
    exit 0
}

if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $fullOutputPath = [IO.Path]::GetFullPath($OutputPath)
    $outputDirectory = [IO.Path]::GetDirectoryName($fullOutputPath)
    if ([string]::IsNullOrWhiteSpace($outputDirectory)) {
        $outputDirectory = "."
    }

    [IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
    $comparisonRows | Sort-Object StepChangePercent | Export-Csv -LiteralPath $fullOutputPath -NoTypeInformation
}

$avgBaselineSteps = ($comparisonRows | Measure-Object -Property BaselineStepsPerSecond -Average).Average
$avgCandidateSteps = ($comparisonRows | Measure-Object -Property CandidateStepsPerSecond -Average).Average
$avgStepChange = if ($avgBaselineSteps -eq 0) { 0 } else { (($avgCandidateSteps / $avgBaselineSteps) - 1) * 100 }
$regressions = @($comparisonRows | Where-Object { $_.StepChangePercent -lt -2 })
$improvements = @($comparisonRows | Where-Object { $_.StepChangePercent -gt 2 })

[pscustomobject]@{
    MatchedRows = $comparisonRows.Count
    AverageBaselineStepsPerSecond = [math]::Round($avgBaselineSteps, 0)
    AverageCandidateStepsPerSecond = [math]::Round($avgCandidateSteps, 0)
    AverageStepChangePercent = [math]::Round($avgStepChange, 2)
    RegressionsOver2Percent = $regressions.Count
    ImprovementsOver2Percent = $improvements.Count
    WorstRegression = ($comparisonRows | Sort-Object StepChangePercent | Select-Object -First 1).StepChangePercent
    BestImprovement = ($comparisonRows | Sort-Object StepChangePercent -Descending | Select-Object -First 1).StepChangePercent
    Output = if ([string]::IsNullOrWhiteSpace($OutputPath)) { "" } else { [IO.Path]::GetFullPath($OutputPath) }
} | Format-List

Write-Output "Worst rows:"
$comparisonRows | Sort-Object StepChangePercent | Select-Object -First 8 Index, Phase, Title, BaselineStepsPerSecond, CandidateStepsPerSecond, StepChangePercent | Format-Table -AutoSize

Write-Output "Best rows:"
$comparisonRows | Sort-Object StepChangePercent -Descending | Select-Object -First 8 Index, Phase, Title, BaselineStepsPerSecond, CandidateStepsPerSecond, StepChangePercent | Format-Table -AutoSize
