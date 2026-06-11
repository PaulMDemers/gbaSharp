param(
    [Parameter(Mandatory = $true)]
    [string]$Rom,
    [string]$Bios = "",
    [string]$OutputRoot = "",
    [int]$StopFrame = 180,
    [long]$MaxSteps = 50000000,
    [double]$MaxSeconds = 60,
    [int]$SampleRate = 44100,
    [double]$Gain = 0.5,
    [string]$ReferenceWav = "",
    [string]$MgbaReferenceWav = "",
    [string]$MgbaPath = "",
    [switch]$OpenMgba,
    [string]$MamePath = "",
    [double]$MameSeconds = 0,
    [string[]]$ExtraMameArgs = @(),
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"

function Invoke-Checked {
    param(
        [string]$Description,
        [string]$FilePath,
        [string[]]$Arguments
    )

    Write-Host ""
    Write-Host "== $Description =="
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

function Resolve-RequiredPath {
    param(
        [string]$Path,
        [string]$Description
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "$Description not found: $Path"
    }

    return (Resolve-Path -LiteralPath $Path).Path
}

function Find-LocalMgba {
    $candidates = @(
        ".research\tools\mgba\extracted\mGBA-0.10.5-win64\mGBA.exe",
        ".research\tools\mgba\extracted\mGBA-0.10.5-win64\mgba-sdl.exe"
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    $command = Get-Command mGBA, mgba-sdl -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) {
        return $command.Source
    }

    return ""
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $repoRoot
try {
    $romFullPath = Resolve-RequiredPath -Path $Rom -Description "ROM"
    $biosFullPath = ""
    if (-not [string]::IsNullOrWhiteSpace($Bios)) {
        $biosFullPath = Resolve-RequiredPath -Path $Bios -Description "BIOS"
    }

    if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
        $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
        $OutputRoot = "artifacts\audio-accuracy-$stamp"
    }

    New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
    $baseName = [IO.Path]::GetFileNameWithoutExtension($romFullPath)
    $gbaSharpWav = Join-Path $OutputRoot "$baseName-gbasharp.wav"
    $gbaSharpFrame = Join-Path $OutputRoot "$baseName-gbasharp-frame.ppm"
    $comparisonCsv = Join-Path $OutputRoot "$baseName-audio-comparison.csv"
    $comparisonMd = Join-Path $OutputRoot "$baseName-audio-comparison.md"

    if (-not $NoBuild) {
        Invoke-Checked -Description "Build CLI" -FilePath "dotnet" -Arguments @("build", "src\Gba.Cli\Gba.Cli.csproj", "-c", "Release")
    }

    if ($OpenMgba) {
        $resolvedMgbaPath = $MgbaPath
        if ([string]::IsNullOrWhiteSpace($resolvedMgbaPath)) {
            $resolvedMgbaPath = Find-LocalMgba
        }

        if ([string]::IsNullOrWhiteSpace($resolvedMgbaPath)) {
            throw "mGBA executable not found. Pass -MgbaPath or install mGBA on PATH."
        }

        $resolvedMgbaPath = Resolve-RequiredPath -Path $resolvedMgbaPath -Description "mGBA executable"
        $mgbaArgs = @()
        if ($biosFullPath) {
            $mgbaArgs += @("-b", $biosFullPath)
        }

        $mgbaArgs += $romFullPath
        Write-Host ""
        Write-Host "== Open mGBA =="
        Write-Host "Record a WAV/PCM reference from mGBA, then rerun this script with -MgbaReferenceWav path\to\reference.wav."
        Start-Process -FilePath $resolvedMgbaPath -ArgumentList $mgbaArgs
    }

    $runArgs = @("run", "--project", "src\Gba.Cli", "-c", "Release")
    if ($NoBuild) {
        $runArgs += "--no-build"
    }

    $runArgs += "--"
    if ($biosFullPath) {
        $runArgs += @("--bios", $biosFullPath)
    }

    $runArgs += @(
        "dump-frame", $romFullPath,
        "--align-rom-entry",
        "--stop-frame", $StopFrame.ToString(),
        "--max-steps", $MaxSteps.ToString(),
        "--max-seconds", $MaxSeconds.ToString([Globalization.CultureInfo]::InvariantCulture),
        "--output", $gbaSharpFrame,
        "--audio-wav", $gbaSharpWav,
        "--audio-sample-rate", $SampleRate.ToString(),
        "--audio-gain", $Gain.ToString([Globalization.CultureInfo]::InvariantCulture)
    )

    Invoke-Checked -Description "Capture gbaSharp audio" -FilePath "dotnet" -Arguments $runArgs

    $referenceFullPath = ""
    if (-not [string]::IsNullOrWhiteSpace($MgbaReferenceWav)) {
        $referenceFullPath = Resolve-RequiredPath -Path $MgbaReferenceWav -Description "mGBA reference WAV"
    }
    elseif (-not [string]::IsNullOrWhiteSpace($ReferenceWav)) {
        $referenceFullPath = Resolve-RequiredPath -Path $ReferenceWav -Description "Reference WAV"
    }
    elseif (-not [string]::IsNullOrWhiteSpace($MamePath)) {
        $mameFullPath = Resolve-RequiredPath -Path $MamePath -Description "MAME executable"
        $referenceFullPath = Join-Path $OutputRoot "$baseName-mame.wav"
        $mameArgs = @("gba", "-cart", $romFullPath, "-wavwrite", $referenceFullPath)
        if ($MameSeconds -gt 0) {
            $mameArgs += @("-seconds_to_run", $MameSeconds.ToString([Globalization.CultureInfo]::InvariantCulture))
        }

        if ($ExtraMameArgs.Count -gt 0) {
            $mameArgs += $ExtraMameArgs
        }

        Invoke-Checked -Description "Capture MAME reference audio" -FilePath $mameFullPath -Arguments $mameArgs
    }

    if ($referenceFullPath) {
        Invoke-Checked -Description "Compare audio" -FilePath "python" -Arguments @(
            "scripts\compare-audio.py",
            $referenceFullPath,
            $gbaSharpWav,
            "--output-csv", $comparisonCsv,
            "--output-md", $comparisonMd
        )
        Write-Host "Audio comparison: $((Resolve-Path -LiteralPath $comparisonMd).Path)"
    }
    else {
        Write-Host ""
        Write-Host "No reference WAV or MAME path provided; wrote gbaSharp WAV only."
    }

    Write-Host "gbaSharp WAV: $((Resolve-Path -LiteralPath $gbaSharpWav).Path)"
}
finally {
    Pop-Location
}
