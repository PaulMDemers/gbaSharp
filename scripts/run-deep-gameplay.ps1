param(
    [string]$Manifest = "docs\gba-deep-gameplay-routes.csv",
    [string]$RomRoot = "curated_official_gba",
    [string]$BaselineDir = "visual-baselines\deep-gameplay",
    [string]$OutputDir = "",
    [string]$Bios = "",
    [string]$Configuration = "Release",
    [int]$MaxItems = 0,
    [int]$SkipItems = 0,
    [int]$ProcessTimeoutSeconds = 900,
    [switch]$NoBuild,
    [switch]$NoAlignRomEntry,
    [switch]$UpdateBaselines,
    [switch]$FailOnBaselineDiff,
    [switch]$Append,
    [switch]$Resume,
    [switch]$NormalPriority
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

            return [pscustomobject]@{
                ExitCode = 124
                Stdout = ""
                Stderr = "$Description exceeded process timeout of ${TimeoutSeconds}s"
            }
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

function Get-FrameFromOutput {
    param([string]$Text)

    if ($Text -match 'frame=([0-9,]+)') {
        return [int](($Matches[1]).Replace(",", ""))
    }

    return 0
}

function Get-PathOrEmpty {
    param([object]$Item, [string]$Property)

    if ($Item.PSObject.Properties.Name -contains $Property) {
        return [string]$Item.$Property
    }

    return ""
}

function Get-FileHashOrEmpty {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return ""
    }

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Test-FilesEqual {
    param([string]$Expected, [string]$Actual)

    if (-not (Test-Path -LiteralPath $Expected) -or -not (Test-Path -LiteralPath $Actual)) {
        return $false
    }

    $expectedHash = Get-FileHashOrEmpty $Expected
    $actualHash = Get-FileHashOrEmpty $Actual
    return $expectedHash -eq $actualHash
}

function Write-GroupCsv {
    param(
        [object[]]$InputRows,
        [string[]]$Properties,
        [string]$Path
    )

    $InputRows |
        Group-Object -Property $Properties |
        Sort-Object @{ Expression = "Count"; Descending = $true }, Name |
        ForEach-Object {
            $parts = $_.Name -split ", "
            $object = [ordered]@{ count = $_.Count }
            for ($i = 0; $i -lt $Properties.Count; $i++) {
                $object[$Properties[$i]] = if ($i -lt $parts.Count) { $parts[$i] } else { "" }
            }

            [pscustomobject]$object
        } |
        Export-Csv -LiteralPath $Path -NoTypeInformation
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $repoRoot
try {
    if (-not $NormalPriority) {
        try {
            (Get-Process -Id $PID).PriorityClass = "BelowNormal"
            Write-Host "Running deep gameplay sweep at BelowNormal process priority."
        }
        catch {
            Write-Warning "Could not lower process priority: $($_.Exception.Message)"
        }
    }

    if (-not $NoBuild) {
        $buildResult = Invoke-DotnetChecked -Arguments @("build", "src\Gba.Cli\Gba.Cli.csproj", "-c", $Configuration) -TimeoutSeconds 180 -Description "Build Gba.Cli"
        if ($buildResult.ExitCode -ne 0) {
            throw "Build Gba.Cli failed with exit code $($buildResult.ExitCode): $($buildResult.Stdout) $($buildResult.Stderr)"
        }
    }

    if ([string]::IsNullOrWhiteSpace($OutputDir)) {
        $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
        $OutputDir = "deep-gameplay-$stamp"
    }

    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
    New-Item -ItemType Directory -Force -Path $BaselineDir | Out-Null
    $frameDir = Join-Path $OutputDir "frames"
    $snapshotDir = Join-Path $OutputDir "snapshots"
    New-Item -ItemType Directory -Force -Path $frameDir | Out-Null
    New-Item -ItemType Directory -Force -Path $snapshotDir | Out-Null

    $roms = @(Get-ChildItem -Path $RomRoot -Recurse -Filter *.gba | Sort-Object FullName)
    if ($roms.Count -eq 0) {
        throw "No .gba files found under $RomRoot."
    }

    $items = @(Import-Csv -LiteralPath $Manifest)
    if ($SkipItems -gt 0) {
        $items = @($items | Select-Object -Skip $SkipItems)
    }

    if ($MaxItems -gt 0) {
        $items = @($items | Select-Object -First $MaxItems)
    }

    $reportPath = Join-Path $OutputDir "deep-gameplay.csv"
    if (-not (($Resume -or $Append) -and (Test-Path $reportPath))) {
        "label,status,baselineStatus,exitCode,index,romPath,stopFrame,observedFrame,maxSteps,maxSeconds,inputScript,saveFile,snapshotRows,distinctPcs,finalPpm,baselinePpm,snapshotCsv,actualHash,baselineHash,expectedScene,message" | Set-Content -LiteralPath $reportPath -Encoding UTF8
    }

    foreach ($item in $items) {
        $label = Get-SafeName $item.label
        $index = if ([string]::IsNullOrWhiteSpace($item.index)) { 0 } else { [int]$item.index }
        $romPath = Get-PathOrEmpty $item "romPath"
        if (-not [string]::IsNullOrWhiteSpace($romPath)) {
            $rom = (Resolve-Path $romPath).Path
        }
        else {
            if ($index -le 0 -or $index -gt $roms.Count) {
                throw "Index $index for $label is outside ROM collection size $($roms.Count)."
            }

            $rom = $roms[$index - 1].FullName
        }

        $finalPpm = Join-Path $frameDir "$label.ppm"
        $baselinePpm = Join-Path $BaselineDir "$label.ppm"
        $snapshotCsv = Join-Path $snapshotDir "$label.csv"
        if ($Resume -and (Test-Path $finalPpm)) {
            Write-Host "Skipping existing deep gameplay route $label"
            continue
        }

        $args = @(
            "run", "--project", "src\Gba.Cli", "--configuration", $Configuration, "--no-build", "--",
            "dump-frame", $rom,
            "--stop-frame", "$($item.stopFrame)",
            "--max-steps", "$($item.maxSteps)",
            "--output", $finalPpm,
            "--snapshot-csv", $snapshotCsv,
            "--snapshot-frames", "$($item.snapshotFrames)"
        )

        if (-not $NoAlignRomEntry) {
            $args += "--align-rom-entry"
        }

        if (-not [string]::IsNullOrWhiteSpace($Bios)) {
            $args += @("--bios", $Bios)
        }

        if (-not [string]::IsNullOrWhiteSpace($item.maxSeconds)) {
            $args += @("--max-seconds", "$($item.maxSeconds)")
        }

        if (-not [string]::IsNullOrWhiteSpace($item.inputScript)) {
            $args += @("--input-script", $item.inputScript)
        }

        if (-not [string]::IsNullOrWhiteSpace($item.saveFile)) {
            $args += @("--save-file", $item.saveFile)
            if (([string]$item.saveReadOnly).Equals("true", [StringComparison]::OrdinalIgnoreCase)) {
                $args += "--save-read-only"
            }
        }

        Write-Host "Running deep gameplay route $label (#$index)"
        $result = Invoke-DotnetChecked -Arguments $args -TimeoutSeconds $ProcessTimeoutSeconds -Description "Deep gameplay $label"
        $message = (($result.Stdout + " " + $result.Stderr) -replace '\s+', ' ').Trim()
        $observedFrame = Get-FrameFromOutput $message
        $snapshotRows = 0
        $distinctPcs = 0
        if (Test-Path -LiteralPath $snapshotCsv) {
            $snapshots = @(Import-Csv -LiteralPath $snapshotCsv)
            $snapshotRows = $snapshots.Count
            $distinctPcs = @($snapshots | Select-Object -ExpandProperty pc -Unique).Count
        }

        $status = if ($result.ExitCode -eq 124) {
            "process-timeout"
        }
        elseif ($result.ExitCode -eq 5) {
            "wall-timeout"
        }
        elseif ($result.ExitCode -eq 6) {
            "invalid-pc"
        }
        elseif ($result.ExitCode -ne 0) {
            "fail"
        }
        elseif ($observedFrame -lt [int]$item.stopFrame) {
            "incomplete"
        }
        else {
            "pass"
        }

        $baselineStatus = "skipped"
        if ($status -eq "pass") {
            if ($UpdateBaselines) {
                Copy-Item -LiteralPath $finalPpm -Destination $baselinePpm -Force
                $baselineStatus = "updated"
            }
            elseif (Test-Path -LiteralPath $baselinePpm) {
                $baselineStatus = if (Test-FilesEqual -Expected $baselinePpm -Actual $finalPpm) { "match" } else { "diff" }
            }
            else {
                $baselineStatus = "missing"
            }
        }

        $actualHash = Get-FileHashOrEmpty $finalPpm
        $baselineHash = Get-FileHashOrEmpty $baselinePpm

        [pscustomobject]@{
            label = $label
            status = $status
            baselineStatus = $baselineStatus
            exitCode = $result.ExitCode
            index = $index
            romPath = $rom
            stopFrame = $item.stopFrame
            observedFrame = $observedFrame
            maxSteps = $item.maxSteps
            maxSeconds = $item.maxSeconds
            inputScript = $item.inputScript
            saveFile = $item.saveFile
            snapshotRows = $snapshotRows
            distinctPcs = $distinctPcs
            finalPpm = $finalPpm
            baselinePpm = $baselinePpm
            snapshotCsv = $snapshotCsv
            actualHash = $actualHash
            baselineHash = $baselineHash
            expectedScene = $item.expectedScene
            message = $message
        } | Export-Csv -LiteralPath $reportPath -NoTypeInformation -Append

        Write-Host "  $status baseline=$baselineStatus frame=$observedFrame snapshots=$snapshotRows distinctPcs=$distinctPcs"
    }

    $rows = @(Import-Csv -LiteralPath $reportPath)
    Write-GroupCsv -InputRows $rows -Properties @("status") -Path (Join-Path $OutputDir "summary-status.csv")
    Write-GroupCsv -InputRows $rows -Properties @("baselineStatus") -Path (Join-Path $OutputDir "summary-baseline-status.csv")
    Write-GroupCsv -InputRows $rows -Properties @("status", "expectedScene") -Path (Join-Path $OutputDir "summary-status-scene.csv")

    if ($FailOnBaselineDiff) {
        $badBaselines = @($rows | Where-Object { $_.baselineStatus -in @("diff", "missing") })
        if ($badBaselines.Count -gt 0) {
            $labels = ($badBaselines | Select-Object -First 10 -ExpandProperty label) -join ", "
            throw "Baseline verification failed for $($badBaselines.Count) route(s): $labels"
        }
    }

    Write-Host "Deep gameplay report: $((Resolve-Path $reportPath).Path)"
}
finally {
    Pop-Location
}
