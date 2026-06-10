param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputRoot = "",
    [switch]$SelfContained,
    [switch]$NoRestore,
    [switch]$NoZip
)

$ErrorActionPreference = "Stop"

function Invoke-Checked {
    param(
        [string[]]$Arguments,
        [string]$Description
    )

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $repoRoot
try {
    if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
        $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
        $OutputRoot = "artifacts\desktop-package-$stamp"
    }

    $publishDir = Join-Path $OutputRoot "publish"
    New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

    $publishArgs = @(
        "publish", "src\Gba.Desktop\Gba.Desktop.csproj",
        "-c", $Configuration,
        "-r", $Runtime,
        "-o", $publishDir,
        "-p:PublishSingleFile=false",
        "-p:SelfContained=$($SelfContained.IsPresent.ToString().ToLowerInvariant())"
    )

    if ($NoRestore) {
        $publishArgs += "--no-restore"
    }

    Invoke-Checked -Description "Desktop publish" -Arguments $publishArgs

    $resolvedPublishDir = (Resolve-Path $publishDir).Path
    Write-Host "Desktop publish folder: $resolvedPublishDir"

    if (-not $NoZip) {
        $zipPath = Join-Path $OutputRoot "gbaSharp-desktop-$Runtime.zip"
        if (Test-Path -LiteralPath $zipPath) {
            Remove-Item -LiteralPath $zipPath -Force
        }

        Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath
        Write-Host "Desktop package: $((Resolve-Path $zipPath).Path)"
    }
}
finally {
    Pop-Location
}
