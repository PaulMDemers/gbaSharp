param(
    [string]$Manifest = "docs\gba-audio-smoke-routes.csv",
    [string]$Bios = "",
    [string]$OutputRoot = "",
    [string]$MgbaReferenceRoot = "reference-captures\mgba\audio",
    [string[]]$Labels = @(),
    [int]$Limit = 0,
    [int]$SampleRate = 44100,
    [double]$Gain = 0.5,
    [switch]$UseMame,
    [string]$MamePath = "",
    [string]$MameRomPath = ".research\tools\mame\roms",
    [double]$MameSeconds = 0,
    [string[]]$ExtraMameArgs = @(),
    [double]$CompareMaxShiftMs = 250,
    [int]$CompareStride = 16,
    [int]$CompareTrimLeadingSilence = 0,
    [double]$CompareTrimPaddingMs = 50,
    [switch]$CompareRemoveDc,
    [switch]$ListOnly,
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"

function Resolve-OptionalPath {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return ""
    }

    if (Test-Path -LiteralPath $Path) {
        return (Resolve-Path -LiteralPath $Path).Path
    }

    return $Path
}

function Test-Truthy {
    param([string]$Value)

    return $Value -match '^(1|true|yes|y)$'
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $repoRoot
try {
    if (-not (Test-Path -LiteralPath $Manifest)) {
        throw "Manifest not found: $Manifest"
    }

    if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
        $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
        $OutputRoot = "artifacts\audio-accuracy-suite-$stamp"
    }

    New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
    $summaryPath = Join-Path $OutputRoot "audio-accuracy-suite.csv"
    $rows = @(Import-Csv -LiteralPath $Manifest)
    if ($Labels.Count -gt 0) {
        $wanted = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($label in $Labels) {
            [void]$wanted.Add($label)
        }

        $rows = @($rows | Where-Object { $wanted.Contains($_.label) })
    }

    if ($Limit -gt 0) {
        $rows = @($rows | Select-Object -First $Limit)
    }

    if ($ListOnly) {
        $rows | Select-Object label, romPath, inputScript, stopFrame, maxSteps, maxSeconds, alignRomEntry, notes | Format-Table -AutoSize
        return
    }

    if (-not $NoBuild) {
        & dotnet build src\Gba.Cli\Gba.Cli.csproj -c Release
        if ($LASTEXITCODE -ne 0) {
            throw "CLI build failed with exit code $LASTEXITCODE."
        }
    }

    $summary = New-Object System.Collections.Generic.List[object]
    foreach ($row in $rows) {
        $label = $row.label
        if ([string]::IsNullOrWhiteSpace($label)) {
            continue
        }

        $routeOutput = Join-Path $OutputRoot $label
        New-Item -ItemType Directory -Force -Path $routeOutput | Out-Null
        $referenceWav = Join-Path $MgbaReferenceRoot "$label.wav"
        $hasReference = Test-Path -LiteralPath $referenceWav
        $align = Test-Truthy $row.alignRomEntry

        $args = @(
            "-NoProfile",
            "-ExecutionPolicy", "Bypass",
            "-File", "scripts\run-audio-accuracy.ps1",
            "-Rom", (Resolve-OptionalPath $row.romPath),
            "-OutputRoot", $routeOutput,
            "-StopFrame", $row.stopFrame,
            "-MaxSteps", $row.maxSteps,
            "-MaxSeconds", $row.maxSeconds,
            "-SampleRate", $SampleRate,
            "-Gain", $Gain,
            "-CompareMaxShiftMs", $CompareMaxShiftMs,
            "-CompareStride", $CompareStride,
            "-CompareTrimLeadingSilence", $CompareTrimLeadingSilence,
            "-CompareTrimPaddingMs", $CompareTrimPaddingMs,
            "-NoBuild"
        )

        if ($CompareRemoveDc) {
            $args += "-CompareRemoveDc"
        }

        if (-not $align) {
            $args += "-NoAlignRomEntry"
        }

        if (-not [string]::IsNullOrWhiteSpace($Bios)) {
            $args += @("-Bios", $Bios)
        }

        if (-not [string]::IsNullOrWhiteSpace($row.inputScript)) {
            $args += @("-InputScript", $row.inputScript)
        }

        if (-not [string]::IsNullOrWhiteSpace($row.saveFile)) {
            $args += @("-SaveFile", $row.saveFile)
        }

        if (Test-Truthy $row.saveReadOnly) {
            $args += "-SaveReadOnly"
        }

        if (-not [string]::IsNullOrWhiteSpace($row.keys)) {
            $args += @("-Keys", $row.keys)
        }

        if ($hasReference) {
            $args += @("-MgbaReferenceWav", $referenceWav)
        }
        elseif ($UseMame) {
            if (-not [string]::IsNullOrWhiteSpace($MamePath)) {
                $args += @("-MamePath", $MamePath)
            }

            if (-not [string]::IsNullOrWhiteSpace($MameRomPath)) {
                $args += @("-MameRomPath", $MameRomPath)
            }

            $routeMameSeconds = $MameSeconds
            if ($routeMameSeconds -le 0 -and $row.stopFrame) {
                $routeMameSeconds = [double]$row.stopFrame / 59.7275
            }

            if ($routeMameSeconds -gt 0) {
                $args += @("-MameSeconds", $routeMameSeconds.ToString([Globalization.CultureInfo]::InvariantCulture))
            }

            foreach ($extra in $ExtraMameArgs) {
                $args += @("-ExtraMameArgs", $extra)
            }
        }

        $status = "pass"
        $errorMessage = ""
        Write-Host ""
        Write-Host "## Audio route: $label"
        try {
            & powershell @args
            if ($LASTEXITCODE -ne 0) {
                $status = "fail"
                $errorMessage = "run-audio-accuracy exited with $LASTEXITCODE"
            }
        }
        catch {
            $status = "fail"
            $errorMessage = $_.Exception.Message
        }

        $comparisonCsv = Get-ChildItem -LiteralPath $routeOutput -Filter "*-audio-comparison.csv" -File -ErrorAction SilentlyContinue | Select-Object -First 1
        $comparisonMd = Get-ChildItem -LiteralPath $routeOutput -Filter "*-audio-comparison.md" -File -ErrorAction SilentlyContinue | Select-Object -First 1
        $gbaSharpWav = Get-ChildItem -LiteralPath $routeOutput -Filter "*-gbasharp.wav" -File -ErrorAction SilentlyContinue | Select-Object -First 1
        $comparison = $null
        if ($comparisonCsv) {
            $comparison = Import-Csv -LiteralPath $comparisonCsv.FullName | Select-Object -First 1
        }

        $summary.Add([pscustomobject]@{
            label = $label
            status = $status
            hasMgbaReference = $hasReference
            usedMameReference = (-not $hasReference -and $UseMame)
            gbaSharpWav = if ($gbaSharpWav) { $gbaSharpWav.FullName } else { "" }
            mgbaReferenceWav = if ($hasReference) { (Resolve-Path -LiteralPath $referenceWav).Path } else { "" }
            comparisonMd = if ($comparisonMd) { $comparisonMd.FullName } else { "" }
            overallCorrelation = if ($comparison) { $comparison.overallCorrelation } else { "" }
            overallRmse = if ($comparison) { $comparison.overallRmse } else { "" }
            durationDeltaSeconds = if ($comparison) { $comparison.durationDeltaSeconds } else { "" }
            referenceFirstNonSilent64Seconds = if ($comparison) { $comparison.referenceFirstNonSilent64Seconds } else { "" }
            actualFirstNonSilent64Seconds = if ($comparison) { $comparison.actualFirstNonSilent64Seconds } else { "" }
            referenceTrimSamples = if ($comparison) { $comparison.referenceTrimSamples } else { "" }
            actualTrimSamples = if ($comparison) { $comparison.actualTrimSamples } else { "" }
            alignmentShiftMs = if ($comparison) { $comparison.alignmentShiftMs } else { "" }
            error = $errorMessage
        })
    }

    $summary | Export-Csv -LiteralPath $summaryPath -NoTypeInformation
    Write-Host ""
    Write-Host "Audio suite summary: $((Resolve-Path -LiteralPath $summaryPath).Path)"
}
finally {
    Pop-Location
}
