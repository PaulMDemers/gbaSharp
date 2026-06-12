param(
    [string]$Manifest = "docs\gba-visual-snapshots.csv",
    [string]$RomRoot = "gba_collection",
    [string]$BaselineDir = "visual-baselines",
    [string]$OutputDir = "",
    [string]$Configuration = "Release",
    [int]$MaxItems = 0,
    [int]$SkipItems = 0,
    [int]$ProcessTimeoutSeconds = 900,
    [int]$PhaseWindowFrames = 0,
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

function Get-ResultMessage {
    param($Result)

    return (($Result.Stdout + " " + $Result.Stderr) -replace '\s+', ' ').Trim()
}

function Get-FrameMetrics {
    param([string]$Message)

    if ($Message -match 'differentPixels=(\d+)\s+maxDelta=(\d+)\s+totalDelta=(\d+)') {
        return [pscustomobject]@{
            DifferentPixels = [int]$Matches[1]
            MaxDelta = [int]$Matches[2]
            TotalDelta = [long]$Matches[3]
        }
    }

    return [pscustomobject]@{
        DifferentPixels = 0
        MaxDelta = 0
        TotalDelta = 0L
    }
}

function New-VerifyFrameArguments {
    param(
        $Item,
        [string]$Rom,
        [int]$StopFrame,
        [string]$Baseline,
        [string]$Actual,
        [string]$Diff
    )

    $args = @(
        "run", "--project", "src\Gba.Cli", "--configuration", $Configuration, "--no-build", "--",
        "verify-frame", $Rom,
        "--stop-frame", "$StopFrame",
        "--max-steps", "$($Item.maxSteps)",
        "--baseline", $Baseline,
        "--actual", $Actual,
        "--diff", $Diff,
        "--max-different-pixels", "$($Item.maxDifferentPixels)",
        "--max-channel-delta", "$($Item.maxChannelDelta)"
    )

    if ($Item.PSObject.Properties.Name -contains "maxSeconds" -and -not [string]::IsNullOrWhiteSpace($Item.maxSeconds)) {
        $args += @("--max-seconds", "$($Item.maxSeconds)")
    }

    if ($PhaseWindowFrames -gt 0 -and -not $UpdateBaselines) {
        $args += @("--phase-window-frames", "$PhaseWindowFrames")
    }

    if ($UpdateBaselines) {
        $args += "--write-baseline"
    }

    if (-not [string]::IsNullOrWhiteSpace($Item.inputScript)) {
        $args += @("--input-script", $Item.inputScript)
    }

    if (-not [string]::IsNullOrWhiteSpace($Item.saveFile)) {
        $args += @("--save-file", $Item.saveFile)
        if (-not $WriteSaveFiles) {
            $args += "--save-read-only"
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($Item.debugLayer)) {
        $args += @("--debug-layer", $Item.debugLayer)
    }

    return $args
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
        "label,phase,index,status,exitCode,requestedStopFrame,matchedStopFrame,frameOffset,maxSteps,maxSeconds,inputScript,saveFile,expectedScene,differentPixels,maxChannelDeltaObserved,totalChannelDelta,baseline,actual,diff,message" | Set-Content -LiteralPath $reportPath -Encoding UTF8
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

        $requestedStopFrame = [int]$item.stopFrame
        $args = New-VerifyFrameArguments -Item $item -Rom $rom -StopFrame $requestedStopFrame -Baseline $baseline -Actual $actual -Diff $diff
        Write-Host "Verifying $label (#$index)"
        $result = Invoke-DotnetChecked -Arguments $args -TimeoutSeconds $ProcessTimeoutSeconds -Description "Visual snapshot $label"
        $message = Get-ResultMessage $result
        $metrics = Get-FrameMetrics $message
        $status = if ($result.ExitCode -eq 0) { "pass" } elseif ($result.ExitCode -eq 4) { "diff" } else { "fail" }
        $matchedStopFrame = $requestedStopFrame
        if ($message -match 'matchedFrame=(-?\d+)\s+frameOffset=(-?\d+)') {
            $matchedStopFrame = [int]$Matches[1]
        }

        if ($result.ExitCode -eq 0 -and $message -match 'verify-frame PHASE-PASS') {
            $status = "phase-pass"
        }

        [pscustomobject]@{
            label = $label
            phase = $item.phase
            index = $index
            status = $status
            exitCode = $result.ExitCode
            requestedStopFrame = $requestedStopFrame
            matchedStopFrame = $matchedStopFrame
            frameOffset = $matchedStopFrame - $requestedStopFrame
            maxSteps = $item.maxSteps
            maxSeconds = if ($item.PSObject.Properties.Name -contains "maxSeconds") { $item.maxSeconds } else { "" }
            inputScript = $item.inputScript
            saveFile = $item.saveFile
            expectedScene = $item.expectedScene
            differentPixels = $metrics.DifferentPixels
            maxChannelDeltaObserved = $metrics.MaxDelta
            totalChannelDelta = $metrics.TotalDelta
            baseline = $baseline
            actual = $actual
            diff = $diff
            message = $message
        } | Export-Csv -LiteralPath $reportPath -NoTypeInformation -Append

        if ($result.ExitCode -eq 0) {
            Write-Host "  $($status.ToUpperInvariant()) $label"
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
