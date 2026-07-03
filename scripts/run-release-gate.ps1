param(
    [ValidateSet("smoke", "standard", "full")]
    [string]$Profile = "smoke",
    [string]$Suites = "docs\gba-release-gate-suites.csv",
    [string]$OutputRoot = "",
    [int]$ChunkSize = 5,
    [int]$ProcessTimeoutSeconds = 900,
    [int]$RouteMaxSecondsCap = 0,
    [switch]$NoBuild,
    [switch]$SkipTests,
    [switch]$NoContactSheet,
    [switch]$DryRun,
    [switch]$Resume,
    [switch]$ContinueOnFailure,
    [switch]$NormalPriority
)

$ErrorActionPreference = "Stop"

function Convert-ToSafeName {
    param([string]$Value)

    $safe = $Value -replace '[^A-Za-z0-9._-]+', '-'
    return $safe.Trim('-')
}

function Split-Labels {
    param([string]$Labels)

    if ([string]::IsNullOrWhiteSpace($Labels)) {
        return @()
    }

    return @($Labels.Split(";", [StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { $_.Trim() } | Where-Object { $_.Length -gt 0 })
}

function Join-ProcessArguments {
    param([string[]]$Items)

    return ($Items | ForEach-Object {
        if ($_ -match '[\s"]' -or $_.Length -eq 0) {
            '"' + ($_.Replace('"', '\"')) + '"'
        }
        else {
            $_
        }
    }) -join " "
}

function Get-RequiredBool {
    param([object]$Row)

    if ($Row.PSObject.Properties.Name -notcontains "required" -or [string]::IsNullOrWhiteSpace($Row.required)) {
        return $true
    }

    return $Row.required.Equals("true", [StringComparison]::OrdinalIgnoreCase)
}

function Invoke-ExternalStep {
    param(
        [string]$Name,
        [string]$FileName,
        [string[]]$Arguments,
        [string]$LogPath
    )

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $FileName
    $psi.Arguments = Join-ProcessArguments $Arguments
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true

    $process = [System.Diagnostics.Process]::Start($psi)
    if ($null -eq $process) {
        throw "Failed to start $Name."
    }

    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()

    $text = @(
        "## $Name",
        "",
        "Command: $FileName $($Arguments -join ' ')",
        "",
        "Exit code: $($process.ExitCode)",
        "",
        "### stdout",
        "",
        $stdout,
        "",
        "### stderr",
        "",
        $stderr
    )
    $text | Set-Content -LiteralPath $LogPath -Encoding UTF8

    if ($process.ExitCode -ne 0) {
        throw "$Name failed with exit code $($process.ExitCode). See $LogPath"
    }
}

function New-FilteredManifest {
    param(
        [string]$Manifest,
        [string[]]$Labels,
        [string]$Destination
    )

    if ($Labels.Count -eq 0) {
        return $Manifest
    }

    $rows = @(Import-Csv -LiteralPath $Manifest)
    $selected = @($rows | Where-Object { $Labels -contains $_.label })
    if ($selected.Count -ne $Labels.Count) {
        $found = @($selected | ForEach-Object { $_.label })
        $missing = @($Labels | Where-Object { $found -notcontains $_ })
        throw "Manifest $Manifest is missing label(s): $($missing -join ', ')"
    }

    $selected | Export-Csv -LiteralPath $Destination -NoTypeInformation
    return $Destination
}

function Invoke-BuildTest {
    param(
        [string]$SuiteDir,
        [bool]$SkipBuild,
        [bool]$SkipUnitTests
    )

    if (-not $SkipBuild) {
        Invoke-ExternalStep -Name "Build CLI" -FileName "dotnet" -Arguments @("build", "src\Gba.Cli\Gba.Cli.csproj", "-c", "Release") -LogPath (Join-Path $SuiteDir "build-cli.log.md")
        Invoke-ExternalStep -Name "Build desktop" -FileName "dotnet" -Arguments @("build", "src\Gba.Desktop\Gba.Desktop.csproj", "-c", "Release") -LogPath (Join-Path $SuiteDir "build-desktop.log.md")
    }

    if (-not $SkipUnitTests) {
        Invoke-ExternalStep -Name "Run unit tests" -FileName "dotnet" -Arguments @("test", "tests\Gba.Tests\Gba.Tests.csproj", "-c", "Release", "--no-build") -LogPath (Join-Path $SuiteDir "unit-tests.log.md")
    }
}

function Invoke-RunnerSuite {
    param(
        [object]$Suite,
        [string]$SuiteDir,
        [string]$ManifestPath,
        [bool]$SkipBuild
    )

    $runner = [string]$Suite.runner
    $args = @("-NoProfile", "-ExecutionPolicy", "Bypass")

    switch ($runner) {
        "deep-gameplay" {
            $args += @(
                "-File", "scripts\run-deep-gameplay.ps1",
                "-Manifest", $ManifestPath,
                "-OutputDir", $SuiteDir,
                "-ProcessTimeoutSeconds", "$ProcessTimeoutSeconds",
                "-FailOnBaselineDiff"
            )
            if ($RouteMaxSecondsCap -gt 0) { $args += @("-RouteMaxSecondsCap", "$RouteMaxSecondsCap") }
        }
        "deep-gameplay-suite" {
            $args += @(
                "-File", "scripts\run-deep-gameplay-suite.ps1",
                "-Manifest", $ManifestPath,
                "-OutputRoot", $SuiteDir,
                "-ChunkSize", "$ChunkSize",
                "-ProcessTimeoutSeconds", "$ProcessTimeoutSeconds",
                "-FailOnBaselineDiff"
            )
            if ($RouteMaxSecondsCap -gt 0) { $args += @("-RouteMaxSecondsCap", "$RouteMaxSecondsCap") }
            if ($NoContactSheet) { $args += "-NoContactSheet" }
            if ($Resume) { $args += "-Resume" }
        }
        "longplay-suite" {
            $args += @(
                "-File", "scripts\run-longplay-suite.ps1",
                "-Manifest", $ManifestPath,
                "-OutputRoot", $SuiteDir,
                "-ChunkSize", "$ChunkSize",
                "-ProcessTimeoutSeconds", "$ProcessTimeoutSeconds"
            )
            if ($RouteMaxSecondsCap -gt 0) { $args += @("-RouteMaxSecondsCap", "$RouteMaxSecondsCap") }
            if ($NoContactSheet) { $args += "-NoContactSheet" }
            if ($Resume) { $args += "-Resume" }
        }
        "audio-smoke" {
            $args += @(
                "-File", "scripts\run-audio-smoke.ps1",
                "-Manifest", $ManifestPath,
                "-OutputDir", $SuiteDir,
                "-ProcessTimeoutSeconds", "$ProcessTimeoutSeconds",
                "-FailOnSignalMismatch"
            )
            if ($Resume) { $args += "-Resume" }
        }
        default {
            throw "Unknown release gate runner: $runner"
        }
    }

    if ($SkipBuild) {
        $args += "-NoBuild"
    }

    if ($NormalPriority) {
        $args += "-NormalPriority"
    }

    Invoke-ExternalStep -Name $Suite.suite -FileName "powershell" -Arguments $args -LogPath (Join-Path $SuiteDir "runner.log.md")
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $repoRoot
try {
    if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
        $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
        $OutputRoot = "artifacts\release-gate-$Profile-$stamp"
    }

    New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

    $allSuites = @(Import-Csv -LiteralPath $Suites)
    $selectedSuites = @($allSuites | Where-Object { $_.profile -eq $Profile })
    if ($selectedSuites.Count -eq 0) {
        throw "No release gate suites found for profile '$Profile' in $Suites."
    }

    $results = @()
    foreach ($suite in $selectedSuites) {
        $suiteName = Convert-ToSafeName $suite.suite
        $suiteDir = Join-Path $OutputRoot $suiteName
        New-Item -ItemType Directory -Force -Path $suiteDir | Out-Null

        $required = Get-RequiredBool $suite
        $labels = Split-Labels $suite.labels
        $manifestPath = $suite.manifest
        if (-not [string]::IsNullOrWhiteSpace($manifestPath)) {
            $manifestPath = New-FilteredManifest -Manifest $manifestPath -Labels $labels -Destination (Join-Path $suiteDir "selected-manifest.csv")
        }

        $status = "pass"
        $message = ""
        $elapsed = [System.Diagnostics.Stopwatch]::StartNew()

        try {
            Write-Host ""
            Write-Host "== Release gate: $($suite.suite) =="
            if ($DryRun) {
                $message = "Dry run only."
                Write-Host $message
            }
            elseif ($suite.runner -eq "build-test") {
                Invoke-BuildTest -SuiteDir $suiteDir -SkipBuild:([bool]$NoBuild) -SkipUnitTests:([bool]$SkipTests)
            }
            else {
                Invoke-RunnerSuite -Suite $suite -SuiteDir $suiteDir -ManifestPath $manifestPath -SkipBuild:$true
            }
        }
        catch {
            $status = "fail"
            $message = $_.Exception.Message
            if ($_.InvocationInfo -and -not [string]::IsNullOrWhiteSpace($_.InvocationInfo.PositionMessage)) {
                $message = "$message $($_.InvocationInfo.PositionMessage)"
            }

            if (-not [string]::IsNullOrWhiteSpace($_.ScriptStackTrace)) {
                $message = "$message Stack: $($_.ScriptStackTrace)"
            }
            Write-Warning $message
        }
        finally {
            $elapsed.Stop()
        }

        $results += [pscustomobject]@{
            profile = $Profile
            suite = $suite.suite
            runner = $suite.runner
            required = $required
            status = $status
            seconds = [Math]::Round($elapsed.Elapsed.TotalSeconds, 1)
            output = $suiteDir
            manifest = $manifestPath
            labels = $suite.labels
            message = $message
        }

        if ($required -and $status -ne "pass" -and -not $ContinueOnFailure) {
            Write-Warning "Stopping release gate after required suite failure. Pass -ContinueOnFailure to audit remaining suites."
            break
        }
    }

    $resultsCsv = Join-Path $OutputRoot "release-gate-summary.csv"
    $resultsMd = Join-Path $OutputRoot "release-gate-summary.md"
    $results | Export-Csv -LiteralPath $resultsCsv -NoTypeInformation

    $failedRequired = @($results | Where-Object { $_.required -and $_.status -ne "pass" })
    $lines = @(
        "# GBA Release Gate",
        "",
        "- Profile: $Profile",
        "- Suites: $($results.Count)",
        "- Required failures: $($failedRequired.Count)",
        "- Output: $OutputRoot",
        "",
        "## Suites",
        ""
    )

    $lines += ($results | ForEach-Object {
        "- $($_.suite): $($_.status), $($_.seconds)s, output=$($_.output)"
    })

    if ($failedRequired.Count -gt 0) {
        $lines += @("", "## Required Failures", "")
        $lines += ($failedRequired | ForEach-Object {
            "- $($_.suite): $($_.message)"
        })
    }

    $lines | Set-Content -LiteralPath $resultsMd -Encoding UTF8
    Write-Host ""
    Write-Host "Release gate summary: $((Resolve-Path $resultsMd).Path)"

    if ($failedRequired.Count -gt 0) {
        throw "Release gate '$Profile' failed with $($failedRequired.Count) required suite failure(s)."
    }
}
finally {
    Pop-Location
}
