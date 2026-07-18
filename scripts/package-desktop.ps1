param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputRoot = "",
    [string]$Version = "",
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
    if ([string]::IsNullOrWhiteSpace($Version)) {
        [xml]$buildProps = Get-Content -LiteralPath "Directory.Build.props"
        $Version = [string]$buildProps.Project.PropertyGroup.VersionPrefix
    }

    if ([string]::IsNullOrWhiteSpace($Version)) {
        throw "Unable to determine the package version. Pass -Version explicitly or set VersionPrefix in Directory.Build.props."
    }

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
    Copy-Item -LiteralPath "README.md" -Destination $publishDir
    Copy-Item -LiteralPath "LICENSE" -Destination $publishDir

    @(
        "gbaSharp $Version",
        "Runtime: $Runtime",
        "Configuration: $Configuration",
        "Self-contained: $($SelfContained.IsPresent)"
    ) | Set-Content -LiteralPath (Join-Path $publishDir "release-info.txt") -Encoding UTF8

    Write-Host "Desktop publish folder: $resolvedPublishDir"

    if (-not $NoZip) {
        $zipPath = Join-Path $OutputRoot "gbaSharp-$Version-desktop-$Runtime.zip"
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
