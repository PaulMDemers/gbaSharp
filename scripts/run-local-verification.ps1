param(
    [switch]$NoRestore,
    [switch]$SkipDesktop,
    [switch]$IncludeStrictReference,
    [switch]$IncludeHardSoak,
    [switch]$NormalPriority
)

$ErrorActionPreference = "Stop"

function Invoke-CheckedStep {
    param(
        [string]$Name,
        [scriptblock]$Command
    )

    Write-Host ""
    Write-Host "== $Name =="
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE."
    }
}

function Test-PathPresent {
    param([string]$Path)

    return Test-Path -LiteralPath $Path
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $repoRoot
try {
    $buildArgs = @("build", "src\Gba.Cli\Gba.Cli.csproj", "-c", "Release")
    $desktopBuildArgs = @("build", "src\Gba.Desktop\Gba.Desktop.csproj", "-c", "Release")
    $testArgs = @("test", "tests\Gba.Tests\Gba.Tests.csproj", "-c", "Release")

    if ($NoRestore) {
        $buildArgs += "--no-restore"
        $desktopBuildArgs += "--no-restore"
        $testArgs += "--no-restore"
    }

    Invoke-CheckedStep "Build CLI" { dotnet @buildArgs }

    if (-not $SkipDesktop) {
        Invoke-CheckedStep "Build desktop frontend" { dotnet @desktopBuildArgs }
    }

    Invoke-CheckedStep "Run core tests" { dotnet @testArgs }

    if ($IncludeStrictReference) {
        $manifest = "docs\gba-longplay-reference-frames.csv"
        $referenceRoot = "reference-captures\mgba\longplay"
        if ((Test-PathPresent $manifest) -and (Test-PathPresent $referenceRoot)) {
            Invoke-CheckedStep "Run strict reference suite" {
                powershell -NoProfile -ExecutionPolicy Bypass -File "scripts\run-strict-reference-suite.ps1" `
                    -Manifest $manifest `
                    -ReferenceRoot $referenceRoot
            }
        }
        else {
            Write-Host ""
            Write-Host "== Run strict reference suite =="
            Write-Host "Skipped because $manifest or $referenceRoot is missing."
        }
    }

    if ($IncludeHardSoak) {
        $manifest = "docs\gba-longplay-strict-routes.csv"
        $romRoot = "curated_official_gba"
        $baselineDir = "visual-baselines\longplay"
        if ((Test-PathPresent $manifest) -and (Test-PathPresent $romRoot) -and (Test-PathPresent $baselineDir)) {
            $hardSoakArgs = @(
                "-NoProfile",
                "-ExecutionPolicy", "Bypass",
                "-File", "scripts\run-hard-local-soak.ps1",
                "-NoBuild"
            )
            if ($NormalPriority) {
                $hardSoakArgs += "-NormalPriority"
            }

            Invoke-CheckedStep "Run hard local soak" { powershell @hardSoakArgs }
        }
        else {
            Write-Host ""
            Write-Host "== Run hard local soak =="
            Write-Host "Skipped because $manifest, $romRoot, or $baselineDir is missing."
        }
    }

    Write-Host ""
    Write-Host "Local verification completed."
}
finally {
    Pop-Location
}
