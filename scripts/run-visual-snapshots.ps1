param(
    [string]$Manifest = "docs\gba-visual-snapshots.csv",
    [string]$RomRoot = "gba_collection",
    [string]$BaselineDir = "visual-baselines",
    [string]$OutputDir = "",
    [string]$Configuration = "Release",
    [int]$MaxItems = 0,
    [int]$SkipItems = 0,
    [int]$ProcessTimeoutSeconds = 900,
    [switch]$UpdateBaselines,
    [switch]$WriteSaveFiles,
    [switch]$Resume
)

$ErrorActionPreference = "Stop"

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

function Invoke-DotnetChecked {
    param(
        [string[]]$Arguments,
        [int]$TimeoutSeconds,
        [string]$Description
    )

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = "dotnet"
    $psi.Arguments = Join-ProcessArguments $Arguments
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true

    $process = [System.Diagnostics.Process]::Start($psi)
    if ($null -eq $process) {
        throw "Failed to start dotnet for $Description"
    }

    try {
        if ($TimeoutSeconds -gt 0 -and -not $process.WaitForExit($TimeoutSeconds * 1000)) {
            try {
                $process.Kill($true)
            }
            catch {
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            }

            throw "$Description exceeded process timeout of ${TimeoutSeconds}s"
        }

        $stdout = $process.StandardOutput.ReadToEnd()
        $stderr = $process.StandardError.ReadToEnd()
        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            Stdout = $stdout.Trim()
            Stderr = $stderr.Trim()
        }
    }
    finally {
        $process.Dispose()
    }
}

function Get-SafeName {
    param([string]$Value)

    $safe = $Value -replace '[^A-Za-z0-9._-]+', '-'
    return $safe.Trim('-')
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $repoRoot
try {
    try {
        (Get-Process -Id $PID).PriorityClass = "BelowNormal"
        Write-Host "Running visual snapshot sweep at BelowNormal process priority."
    }
    catch {
        Write-Warning "Could not lower process priority: $($_.Exception.Message)"
    }

    if ([string]::IsNullOrWhiteSpace($OutputDir)) {
        $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
        $OutputDir = "visual-snapshots-$stamp"
    }

    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
    New-Item -ItemType Directory -Force -Path $BaselineDir | Out-Null
    $actualDir = Join-Path $OutputDir "actual"
    $diffDir = Join-Path $OutputDir "diff"
    New-Item -ItemType Directory -Force -Path $actualDir | Out-Null
    New-Item -ItemType Directory -Force -Path $diffDir | Out-Null

    $roms = @(Get-ChildItem -Path $RomRoot -Recurse -Filter *.gba | Sort-Object FullName)
    $items = @(Import-Csv $Manifest)
    if ($SkipItems -gt 0) {
        $items = @($items | Select-Object -Skip $SkipItems)
    }

    if ($MaxItems -gt 0) {
        $items = @($items | Select-Object -First $MaxItems)
    }

    $reportPath = Join-Path $OutputDir "visual-snapshots.csv"
    if (-not ($Resume -and (Test-Path $reportPath))) {
        "label,phase,index,status,exitCode,stopFrame,maxSteps,maxSeconds,inputScript,saveFile,expectedScene,baseline,actual,diff,message" | Set-Content -LiteralPath $reportPath -Encoding UTF8
    }

    foreach ($item in $items) {
        $label = Get-SafeName $item.label
        $index = if ([string]::IsNullOrWhiteSpace($item.index)) { 0 } else { [int]$item.index }
        if ($item.PSObject.Properties.Name -contains "romPath" -and -not [string]::IsNullOrWhiteSpace($item.romPath)) {
            $rom = (Resolve-Path $item.romPath).Path
        }
        else {
            if ($index -le 0 -or $index -gt $roms.Count) {
                throw "Index $index for $label is outside ROM collection size $($roms.Count)."
            }

            $rom = $roms[$index - 1].FullName
        }
        $baseline = Join-Path $BaselineDir "$label.ppm"
        $actual = Join-Path $actualDir "$label.ppm"
        $diff = Join-Path $diffDir "$label.ppm"
        if ($Resume -and (Test-Path $actual)) {
            Write-Host "Skipping existing visual snapshot $label"
            continue
        }

        $args = @(
            "run", "--project", "src\Gba.Cli", "--configuration", $Configuration, "--no-build", "--",
            "verify-frame", $rom,
            "--stop-frame", "$($item.stopFrame)",
            "--max-steps", "$($item.maxSteps)",
            "--baseline", $baseline,
            "--actual", $actual,
            "--diff", $diff,
            "--max-different-pixels", "$($item.maxDifferentPixels)",
            "--max-channel-delta", "$($item.maxChannelDelta)"
        )

        if ($item.PSObject.Properties.Name -contains "maxSeconds" -and -not [string]::IsNullOrWhiteSpace($item.maxSeconds)) {
            $args += @("--max-seconds", "$($item.maxSeconds)")
        }

        if ($UpdateBaselines) {
            $args += "--write-baseline"
        }

        if (-not [string]::IsNullOrWhiteSpace($item.inputScript)) {
            $args += @("--input-script", $item.inputScript)
        }

        if (-not [string]::IsNullOrWhiteSpace($item.saveFile)) {
            $args += @("--save-file", $item.saveFile)
            if (-not $WriteSaveFiles) {
                $args += "--save-read-only"
            }
        }

        if (-not [string]::IsNullOrWhiteSpace($item.debugLayer)) {
            $args += @("--debug-layer", $item.debugLayer)
        }

        Write-Host "Verifying $label (#$index)"
        $result = Invoke-DotnetChecked -Arguments $args -TimeoutSeconds $ProcessTimeoutSeconds -Description "Visual snapshot $label"
        $status = if ($result.ExitCode -eq 0) { "pass" } elseif ($result.ExitCode -eq 4) { "diff" } else { "fail" }
        $message = (($result.Stdout + " " + $result.Stderr) -replace '\s+', ' ').Trim()
        [pscustomobject]@{
            label = $label
            phase = $item.phase
            index = $index
            status = $status
            exitCode = $result.ExitCode
            stopFrame = $item.stopFrame
            maxSteps = $item.maxSteps
            maxSeconds = if ($item.PSObject.Properties.Name -contains "maxSeconds") { $item.maxSeconds } else { "" }
            inputScript = $item.inputScript
            saveFile = $item.saveFile
            expectedScene = $item.expectedScene
            baseline = $baseline
            actual = $actual
            diff = $diff
            message = $message
        } | Export-Csv -LiteralPath $reportPath -NoTypeInformation -Append

        if ($result.ExitCode -eq 0) {
            Write-Host "  PASS $label"
        }
        elseif ($result.ExitCode -eq 4) {
            Write-Host "  DIFF $label"
        }
        else {
            throw "Visual snapshot $label failed with exit code $($result.ExitCode): $message"
        }
    }

    Write-Host "Visual snapshot report: $((Resolve-Path $reportPath).Path)"
}
finally {
    Pop-Location
}
