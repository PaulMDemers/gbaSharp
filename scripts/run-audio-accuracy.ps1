param(
    [Parameter(Mandatory = $true)]
    [string]$Rom,
    [string]$Bios = "",
    [string]$OutputRoot = "",
    [int]$StopFrame = 180,
    [long]$MaxSteps = 50000000,
    [double]$MaxSeconds = 60,
    [string]$InputScript = "",
    [string]$SaveFile = "",
    [switch]$SaveReadOnly,
    [string]$Keys = "",
    [switch]$NoAlignRomEntry,
    [int]$SampleRate = 44100,
    [double]$Gain = 0.5,
    [switch]$AudioPadFromStart,
    [string]$ReferenceWav = "",
    [string]$MgbaReferenceWav = "",
    [string]$MgbaPath = "",
    [switch]$OpenMgba,
    [string]$MamePath = "",
    [string]$MameRomPath = ".research\tools\mame\roms",
    [double]$MameSeconds = 0,
    [string[]]$ExtraMameArgs = @(),
    [double]$CompareMaxShiftMs = 250,
    [int]$CompareStride = 16,
    [int]$CompareTrimLeadingSilence = 0,
    [double]$CompareTrimPaddingMs = 50,
    [switch]$CompareRemoveDc,
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

function Find-LocalMame {
    $candidates = @(
        ".research\tools\mame\mame0288\mame.exe",
        ".research\tools\mame\mame.exe"
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    $command = Get-Command mame, mame64 -ErrorAction SilentlyContinue | Select-Object -First 1
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
    $inputScriptFullPath = ""
    if (-not [string]::IsNullOrWhiteSpace($InputScript)) {
        $inputScriptFullPath = Resolve-RequiredPath -Path $InputScript -Description "Input script"
    }
    $saveFileFullPath = ""
    if (-not [string]::IsNullOrWhiteSpace($SaveFile)) {
        $saveFileFullPath = Resolve-RequiredPath -Path $SaveFile -Description "Save file"
    }

    if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
        $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
        $OutputRoot = "artifacts\audio-accuracy-$stamp"
    }

    New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
    $derivedStopFrame = $false
    if ($StopFrame -eq 0 -and $MameSeconds -gt 0) {
        $StopFrame = [Math]::Max(1, [int][Math]::Round($MameSeconds * 59.7275))
        $derivedStopFrame = $true
    }

    if ($derivedStopFrame -and $MaxSteps -eq 50000000) {
        $MaxSteps = [Math]::Max($MaxSteps, [long]$StopFrame * 350000L)
    }

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

    $shouldUseMame = [string]::IsNullOrWhiteSpace($MgbaReferenceWav) -and [string]::IsNullOrWhiteSpace($ReferenceWav)
    if ($shouldUseMame -and [string]::IsNullOrWhiteSpace($MamePath)) {
        $MamePath = Find-LocalMame
    }

    $runArgs += @(
        "dump-frame", $romFullPath,
        "--stop-frame", $StopFrame.ToString(),
        "--max-steps", $MaxSteps.ToString(),
        "--max-seconds", $MaxSeconds.ToString([Globalization.CultureInfo]::InvariantCulture),
        "--output", $gbaSharpFrame,
        "--audio-wav", $gbaSharpWav,
        "--audio-sample-rate", $SampleRate.ToString(),
        "--audio-gain", $Gain.ToString([Globalization.CultureInfo]::InvariantCulture)
    )

    if (-not $NoAlignRomEntry) {
        $runArgs += "--align-rom-entry"
    }

    if ($AudioPadFromStart -or (-not [string]::IsNullOrWhiteSpace($MamePath))) {
        $runArgs += "--audio-pad-from-start"
    }

    if ($inputScriptFullPath) {
        $runArgs += @("--input-script", $inputScriptFullPath)
    }

    if ($saveFileFullPath) {
        $runArgs += @("--save-file", $saveFileFullPath)
    }

    if ($SaveReadOnly) {
        $runArgs += "--save-read-only"
    }

    if (-not [string]::IsNullOrWhiteSpace($Keys)) {
        $runArgs += @("--keys", $Keys)
    }

    Invoke-Checked -Description "Capture gbaSharp audio" -FilePath "dotnet" -Arguments $runArgs

    $referenceFullPath = ""
    if (-not [string]::IsNullOrWhiteSpace($MgbaReferenceWav)) {
        $referenceFullPath = Resolve-RequiredPath -Path $MgbaReferenceWav -Description "mGBA reference WAV"
    }
    elseif (-not [string]::IsNullOrWhiteSpace($ReferenceWav)) {
        $referenceFullPath = Resolve-RequiredPath -Path $ReferenceWav -Description "Reference WAV"
    }
    if (-not $referenceFullPath -and -not [string]::IsNullOrWhiteSpace($MamePath)) {
        $mameFullPath = Resolve-RequiredPath -Path $MamePath -Description "MAME executable"
        $referenceFullPath = Join-Path $OutputRoot "$baseName-mame.wav"
        $mameArgs = @("gba", "-cart", $romFullPath, "-wavwrite", $referenceFullPath)
        if (-not [string]::IsNullOrWhiteSpace($MameRomPath)) {
            $mameArgs += @("-rompath", $MameRomPath)
        }

        if ($MameSeconds -gt 0) {
            $mameArgs += @("-seconds_to_run", $MameSeconds.ToString([Globalization.CultureInfo]::InvariantCulture))
        }

        if ($ExtraMameArgs.Count -gt 0) {
            $mameArgs += $ExtraMameArgs
        }

        Invoke-Checked -Description "Capture MAME reference audio" -FilePath $mameFullPath -Arguments $mameArgs
    }

    if ($referenceFullPath) {
        $compareArgs = @(
            "scripts\compare-audio.py",
            $referenceFullPath,
            $gbaSharpWav,
            "--output-csv", $comparisonCsv,
            "--output-md", $comparisonMd,
            "--max-shift-ms", $CompareMaxShiftMs.ToString([Globalization.CultureInfo]::InvariantCulture),
            "--stride", $CompareStride.ToString(),
            "--trim-leading-silence", $CompareTrimLeadingSilence.ToString(),
            "--trim-padding-ms", $CompareTrimPaddingMs.ToString([Globalization.CultureInfo]::InvariantCulture)
        )

        if ($CompareRemoveDc) {
            $compareArgs += "--remove-dc"
        }

        Invoke-Checked -Description "Compare audio" -FilePath "python" -Arguments $compareArgs
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
