param(
    [string]$Manifest = "docs\gba-deep-gameplay-routes.csv",
    [string]$RomRoot = "curated_official_gba",
    [string]$BaselineDir = "visual-baselines\deep-gameplay",
    [Alias("OutputRoot")]
    [string]$OutputDir = "",
    [string]$Bios = "",
    [string]$Configuration = "Release",
    [string[]]$Labels = @(),
    [int]$MaxItems = 0,
    [int]$SkipItems = 0,
    [int]$ProcessTimeoutSeconds = 900,
    [int]$RouteMaxSecondsCap = 0,
    [switch]$NoBuild,
    [switch]$NoBios,
    [switch]$NoAlignRomEntry,
    [switch]$UpdateBaselines,
    [switch]$FailOnBaselineDiff,
    [switch]$Append,
    [switch]$Resume,
    [switch]$ListOnly,
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
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $timer = [System.Diagnostics.Stopwatch]::StartNew()
        while (-not $process.HasExited) {
            if ($TimeoutSeconds -gt 0 -and $timer.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
                try {
                    $process.Kill($true)
                    $process.WaitForExit(5000) | Out-Null
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

            Start-Sleep -Milliseconds 500
        }

        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            Stdout = $stdout.Trim()
            Stderr = $stderr.Trim()
        }
    }
    finally {
        if (-not $process.HasExited) {
            try {
                $process.Kill($true)
                $process.WaitForExit(5000) | Out-Null
            }
            catch {
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            }
        }

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

function Get-BoolOrDefault {
    param([object]$Item, [string]$Property, [bool]$Default)

    if ($Item.PSObject.Properties.Name -notcontains $Property -or [string]::IsNullOrWhiteSpace($Item.$Property)) {
        return $Default
    }

    return ([string]$Item.$Property).Equals("true", [StringComparison]::OrdinalIgnoreCase)
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

    $cliDll = Get-ChildItem -Path (Join-Path "src\Gba.Cli\bin" $Configuration) -Recurse -Filter "Gba.Cli.dll" |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($null -eq $cliDll) {
        throw "Could not find built Gba.Cli.dll under src\Gba.Cli\bin\$Configuration. Run without -NoBuild once to build it."
    }

    if ($NoBios) {
        $Bios = ""
        Write-Host "Running without a BIOS."
    }
    elseif ([string]::IsNullOrWhiteSpace($Bios)) {
        $defaultBios = "gba_collection\Massive GBA - EverDrive GBA 2022-08-08\5 Tools & Service Test Carts\BIOS\[BIOS] Game Boy Advance (World).bin"
        if (Test-Path -LiteralPath $defaultBios) {
            $Bios = $defaultBios
            Write-Host "Using discovered real BIOS: $Bios"
        }
        elseif ($FailOnBaselineDiff) {
            Write-Warning "Running strict baseline verification without a BIOS. Real-BIOS baselines will not match no-BIOS output."
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
    $logDir = Join-Path $OutputDir "logs"
    New-Item -ItemType Directory -Force -Path $frameDir | Out-Null
    New-Item -ItemType Directory -Force -Path $snapshotDir | Out-Null
    New-Item -ItemType Directory -Force -Path $logDir | Out-Null

    $roms = @(Get-ChildItem -Path $RomRoot -Recurse -Filter *.gba | Sort-Object FullName)
    if ($roms.Count -eq 0) {
        throw "No .gba files found under $RomRoot."
    }

    $items = @(Import-Csv -LiteralPath $Manifest)
    if ($Labels.Count -gt 0) {
        $selectedLabels = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($labelGroup in $Labels) {
            foreach ($label in $labelGroup.Split(",", [StringSplitOptions]::RemoveEmptyEntries)) {
                [void]$selectedLabels.Add($label.Trim())
            }
        }

        $items = @($items | Where-Object { $selectedLabels.Contains([string]$_.label) })
        if ($items.Count -eq 0) {
            throw "No routes matched -Labels: $($selectedLabels -join ', ')"
        }
    }

    if ($SkipItems -gt 0) {
        $items = @($items | Select-Object -Skip $SkipItems)
    }

    if ($MaxItems -gt 0) {
        $items = @($items | Select-Object -First $MaxItems)
    }

    if ($items.Count -eq 0) {
        throw "No routes selected from $Manifest."
    }

    $selectedRoutesPath = Join-Path $OutputDir "selected-routes.csv"
    $items | Export-Csv -LiteralPath $selectedRoutesPath -NoTypeInformation
    if ($ListOnly) {
        Write-Host "Selected $($items.Count) route(s)."
        Write-Host "Selected routes: $((Resolve-Path $selectedRoutesPath).Path)"
        return
    }

    $reportColumns = @(
        "label", "status", "baselineStatus", "targetScene", "baselineRequired", "minDistinctPcs", "exitCode", "index", "romPath",
        "stopFrame", "observedFrame", "lastSnapshotFrame", "lastSnapshotPc", "maxSteps", "maxSeconds", "inputScript", "saveFile",
        "snapshotRows", "distinctPcs", "distinctFrames", "activityDiversity", "finalPpm", "baselinePpm", "snapshotCsv", "actualHash",
        "baselineHash", "expectedScene", "message"
    )
    $reportPath = Join-Path $OutputDir "deep-gameplay.csv"
    if (-not (($Resume -or $Append) -and (Test-Path $reportPath))) {
        ($reportColumns -join ",") | Set-Content -LiteralPath $reportPath -Encoding UTF8
    }
    elseif ((Get-Content -LiteralPath $reportPath -TotalCount 1) -notmatch '(^|,)activityDiversity(,|$)') {
        $existingRows = @(Import-Csv -LiteralPath $reportPath)
        foreach ($row in $existingRows) {
            $row | Add-Member -NotePropertyName distinctFrames -NotePropertyValue "" -Force
            $row | Add-Member -NotePropertyName activityDiversity -NotePropertyValue $row.distinctPcs -Force
        }

        if ($existingRows.Count -gt 0) {
            $existingRows | Select-Object $reportColumns | Export-Csv -LiteralPath $reportPath -NoTypeInformation
        }
        else {
            ($reportColumns -join ",") | Set-Content -LiteralPath $reportPath -Encoding UTF8
        }
    }

    foreach ($item in $items) {
        $label = Get-SafeName $item.label
        $targetScene = Get-PathOrEmpty $item "targetScene"
        $baselineRequired = Get-BoolOrDefault -Item $item -Property "baselineRequired" -Default $true
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
        $stdoutLog = Join-Path $logDir "$label.stdout.log"
        $stderrLog = Join-Path $logDir "$label.stderr.log"
        $commandLog = Join-Path $logDir "$label.command.txt"
        $diagnosticLog = Join-Path $logDir "$label.diagnostic.log"
        $effectiveMaxSeconds = Get-PathOrEmpty $item "maxSeconds"
        if ($RouteMaxSecondsCap -gt 0) {
            if ([string]::IsNullOrWhiteSpace($effectiveMaxSeconds)) {
                $effectiveMaxSeconds = "$RouteMaxSecondsCap"
            }
            else {
                $effectiveMaxSeconds = "$([Math]::Min([int]$effectiveMaxSeconds, $RouteMaxSecondsCap))"
            }
        }

        if ($Resume -and (Test-Path $finalPpm)) {
            Write-Host "Skipping existing deep gameplay route $label"
            continue
        }

        $args = @(
            $cliDll.FullName,
            "dump-frame", $rom,
            "--stop-frame", "$($item.stopFrame)",
            "--max-steps", "$($item.maxSteps)",
            "--output", $finalPpm,
            "--snapshot-csv", $snapshotCsv,
            "--snapshot-frames", "$($item.snapshotFrames)",
            "--diagnostic-log", $diagnosticLog
        )

        if (-not $NoAlignRomEntry) {
            $args += "--align-rom-entry"
        }

        if (-not [string]::IsNullOrWhiteSpace($Bios)) {
            $args += @("--bios", $Bios)
        }

        if (-not [string]::IsNullOrWhiteSpace($effectiveMaxSeconds)) {
            $args += @("--max-seconds", "$effectiveMaxSeconds")
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

        $routeTimeoutSeconds = $ProcessTimeoutSeconds
        if (-not [string]::IsNullOrWhiteSpace($effectiveMaxSeconds)) {
            $routeMaxSeconds = [int]$effectiveMaxSeconds
            $routeTimeoutSeconds = [Math]::Max($routeTimeoutSeconds, $routeMaxSeconds + 60)
        }

        Write-Host "Running deep gameplay route $label (#$index)"
        @(
            "dotnet $(Join-ProcessArguments $args)",
            "",
            "TimeoutSeconds=$routeTimeoutSeconds",
            "Started=$([DateTimeOffset]::Now.ToString('O'))"
        ) | Set-Content -LiteralPath $commandLog -Encoding UTF8
        $result = Invoke-DotnetChecked -Arguments $args -TimeoutSeconds $routeTimeoutSeconds -Description "Deep gameplay $label"
        $result.Stdout | Set-Content -LiteralPath $stdoutLog -Encoding UTF8
        $result.Stderr | Set-Content -LiteralPath $stderrLog -Encoding UTF8
        $message = (($result.Stdout + " " + $result.Stderr) -replace '\s+', ' ').Trim()
        $observedFrame = Get-FrameFromOutput $message
        $snapshotRows = 0
        $distinctPcs = 0
        $distinctFrames = 0
        $activityDiversity = 0
        $lastSnapshotFrame = ""
        $lastSnapshotPc = ""
        if (Test-Path -LiteralPath $snapshotCsv) {
            $snapshots = @(Import-Csv -LiteralPath $snapshotCsv)
            $snapshotRows = $snapshots.Count
            $distinctPcs = @($snapshots | Select-Object -ExpandProperty pc -Unique).Count
            if ($snapshotRows -gt 0 -and $snapshots[0].PSObject.Properties.Name -contains "frameHash") {
                $distinctFrames = @($snapshots | Select-Object -ExpandProperty frameHash -Unique).Count
            }
            $activityDiversity = [Math]::Max($distinctPcs, $distinctFrames)
            if ($snapshotRows -gt 0) {
                $lastSnapshot = $snapshots[-1]
                $lastSnapshotFrame = $lastSnapshot.frame
                $lastSnapshotPc = $lastSnapshot.pc
            }
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
        elseif ($result.ExitCode -eq -1) {
            "aborted"
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
            targetScene = $targetScene
            baselineRequired = $baselineRequired
            minDistinctPcs = Get-PathOrEmpty $item "minDistinctPcs"
            exitCode = $result.ExitCode
            index = $index
            romPath = $rom
            stopFrame = $item.stopFrame
            observedFrame = $observedFrame
            lastSnapshotFrame = $lastSnapshotFrame
            lastSnapshotPc = $lastSnapshotPc
            maxSteps = $item.maxSteps
            maxSeconds = $effectiveMaxSeconds
            inputScript = $item.inputScript
            saveFile = $item.saveFile
            snapshotRows = $snapshotRows
            distinctPcs = $distinctPcs
            distinctFrames = $distinctFrames
            activityDiversity = $activityDiversity
            finalPpm = $finalPpm
            baselinePpm = $baselinePpm
            snapshotCsv = $snapshotCsv
            actualHash = $actualHash
            baselineHash = $baselineHash
            expectedScene = $item.expectedScene
            message = $message
        } | Export-Csv -LiteralPath $reportPath -NoTypeInformation -Append

        Write-Host "  $status baseline=$baselineStatus frame=$observedFrame snapshots=$snapshotRows activity=$activityDiversity (pcs=$distinctPcs frames=$distinctFrames)"
    }

    $rows = @(Import-Csv -LiteralPath $reportPath)
    Write-GroupCsv -InputRows $rows -Properties @("status") -Path (Join-Path $OutputDir "summary-status.csv")
    Write-GroupCsv -InputRows $rows -Properties @("baselineStatus") -Path (Join-Path $OutputDir "summary-baseline-status.csv")
    Write-GroupCsv -InputRows $rows -Properties @("targetScene", "status") -Path (Join-Path $OutputDir "summary-target-scene-status.csv")
    Write-GroupCsv -InputRows $rows -Properties @("status", "expectedScene") -Path (Join-Path $OutputDir "summary-status-scene.csv")

    if ($FailOnBaselineDiff) {
        $badBaselines = @($rows | Where-Object { $_.baselineRequired -ne "False" -and $_.baselineStatus -in @("diff", "missing") })
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
