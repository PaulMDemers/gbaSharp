param(
    [Parameter(Mandatory = $true)]
    [string]$Rom,
    [Parameter(Mandatory = $true)]
    [string]$OutputPng,
    [double]$Seconds = 10,
    [int]$FrameIndex = -1,
    [string]$MamePath = "",
    [string]$MameRomPath = ".research\tools\mame\roms",
    [string]$AviPath = "",
    [switch]$KeepAvi
)

$ErrorActionPreference = "Stop"

function Resolve-RequiredPath {
    param([string]$Path, [string]$Description)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "$Description not found: $Path"
    }

    return (Resolve-Path -LiteralPath $Path).Path
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
    if ([string]::IsNullOrWhiteSpace($MamePath)) {
        $MamePath = Find-LocalMame
    }

    $mameFullPath = Resolve-RequiredPath -Path $MamePath -Description "MAME executable"
    $outputFullPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPng)
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $outputFullPath) | Out-Null

    if ([string]::IsNullOrWhiteSpace($AviPath)) {
        $aviName = [IO.Path]::GetFileNameWithoutExtension($outputFullPath) + ".avi"
        $aviDirectory = [IO.Path]::GetDirectoryName($outputFullPath)
        if ([string]::IsNullOrWhiteSpace($aviDirectory)) {
            $aviDirectory = "."
        }

        $AviPath = Join-Path $aviDirectory $aviName
    }

    $aviFullPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($AviPath)
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $aviFullPath) | Out-Null
    if (Test-Path -LiteralPath $aviFullPath) {
        Remove-Item -LiteralPath $aviFullPath -Force
    }

    $mameArgs = @(
        "gba",
        "-cart", $romFullPath,
        "-rompath", $MameRomPath,
        "-seconds_to_run", $Seconds.ToString([Globalization.CultureInfo]::InvariantCulture),
        "-aviwrite", $aviFullPath,
        "-sound", "none",
        "-nothrottle",
        "-window",
        "-nomaximize",
        "-resolution", "240x160"
    )

    & $mameFullPath @mameArgs
    if ($LASTEXITCODE -ne 0) {
        throw "MAME exited with $LASTEXITCODE."
    }

    & python scripts\extract-mame-avi-frame.py $aviFullPath $outputFullPath --frame-index $FrameIndex
    if ($LASTEXITCODE -ne 0) {
        throw "AVI frame extraction exited with $LASTEXITCODE."
    }

    if (-not $KeepAvi) {
        Remove-Item -LiteralPath $aviFullPath -Force
    }

    Write-Host "MAME frame: $outputFullPath"
}
finally {
    Pop-Location
}
