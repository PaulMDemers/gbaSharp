param(
    [string]$RomRoot = "gba_collection",
    [string]$OutputDir = "",
    [ValidateSet("single", "boot", "standard", "input", "gameplay")]
    [string]$Suite = "boot",
    [int]$ChunkSize = 10,
    [int]$MaxSteps = 5000000,
    [int]$FrameStepBudget = 150000,
    [int]$MaxSeconds = 30,
    [int]$StartIndex = 1,
    [int]$MaxChunks = 0,
    [int]$PauseSeconds = 2,
    [int]$RetryTimeoutSteps = 0,
    [int]$RetryTimeoutSeconds = 90,
    [int]$RetryChunkSize = 5,
    [int]$ProcessTimeoutSeconds = 0,
    [int]$RetryProcessTimeoutSeconds = 0,
    [string]$CaptureStatuses = "crash,static,no-video,timeout",
    [switch]$NoCapture,
    [switch]$Resume,
    [switch]$MergeOnly,
    [switch]$NormalPriority,
    [string]$Bios = "",
    [switch]$AlignRomEntry,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

function Invoke-DotnetChecked {
    param(
        [string[]]$Arguments,
        [int]$TimeoutSeconds,
        [int[]]$AllowedExitCodes,
        [string]$Description
    )

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

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = "dotnet"
    $psi.Arguments = Join-ProcessArguments $Arguments
    $psi.UseShellExecute = $false

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

        $process.WaitForExit()
        if ($AllowedExitCodes -notcontains $process.ExitCode) {
            throw "$Description failed with exit code $($process.ExitCode)"
        }

        return $process.ExitCode
    }
    finally {
        $process.Dispose()
    }
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $repoRoot
try {
    if (-not $NormalPriority) {
        try {
            (Get-Process -Id $PID).PriorityClass = "BelowNormal"
            Write-Host "Running sweep at BelowNormal process priority."
        }
        catch {
            Write-Warning "Could not lower process priority: $($_.Exception.Message)"
        }
    }

    if ([string]::IsNullOrWhiteSpace($OutputDir)) {
        $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
        $OutputDir = "compat-sweep-$stamp"
    }

    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
    $chunkDir = Join-Path $OutputDir "chunks"
    New-Item -ItemType Directory -Force -Path $chunkDir | Out-Null

    $romFiles = @(Get-ChildItem -Path $RomRoot -Recurse -File -Filter *.gba | Sort-Object FullName)
    $romCount = $romFiles.Count
    if ($romCount -eq 0) {
        throw "No .gba files found under $RomRoot"
    }

    $commonArgs = @("run", "--project", "src\Gba.Cli", "--configuration", $Configuration, "--no-build", "--", "compat", $RomRoot, "--suite", $Suite, "--max-steps", "$MaxSteps", "--frame-step-budget", "$FrameStepBudget", "--max-seconds", "$MaxSeconds")
    if (-not [string]::IsNullOrWhiteSpace($Bios)) {
        $commonArgs += @("--bios", $Bios)
    }
    if ($AlignRomEntry) {
        $commonArgs += @("--align-rom-entry")
    }

    function Merge-CompatReports {
        param(
            [string]$SourceDir,
            [string]$OutputPath
        )

        Remove-Item -LiteralPath $OutputPath -ErrorAction SilentlyContinue

        $rowsByKey = @{}
        $phaseOrder = @{
            "boot" = 0
            "start-probe" = 1
            "broad-input" = 2
            "long-input" = 3
        }

        foreach ($csv in Get-ChildItem -Path $SourceDir -Filter "*.csv" | Where-Object { $_.Name -notlike "*-summary.csv" } | Sort-Object LastWriteTime, Name) {
            foreach ($row in Import-Csv -LiteralPath $csv.FullName) {
                $key = "$($row.index)|$($row.phase)"
                $rowsByKey[$key] = $row
            }
        }

        if ($rowsByKey.Count -eq 0) {
            return
        }

        $rowsByKey.Values |
            Sort-Object @{ Expression = { [int]$_.index } }, @{ Expression = { if ($phaseOrder.ContainsKey($_.phase)) { $phaseOrder[$_.phase] } else { 99 } } } |
            Export-Csv -LiteralPath $OutputPath -NoTypeInformation
    }

    function Run-Summary {
        param(
            [string]$ReportPath,
            [string]$SummaryPath
        )

        if (-not (Test-Path $ReportPath)) {
            Write-Warning "No report exists at $ReportPath; skipping summary."
            return
        }

        [void](Invoke-DotnetChecked -Arguments @("run", "--project", "src\Gba.Cli", "--configuration", $Configuration, "--no-build", "--", "compat-summary", $ReportPath, $SummaryPath) -TimeoutSeconds 120 -AllowedExitCodes @(0) -Description "Summary $ReportPath")
    }

    function Run-CompatIndexes {
        param(
            [int[]]$Indexes,
            [string]$DestinationDir,
            [string]$NamePrefix,
            [int]$Steps,
            [int]$Seconds,
            [int]$IndexesPerChunk
        )

        New-Item -ItemType Directory -Force -Path $DestinationDir | Out-Null
        for ($offset = 0; $offset -lt $Indexes.Count; $offset += $IndexesPerChunk) {
            $slice = $Indexes[$offset..([Math]::Min($offset + $IndexesPerChunk - 1, $Indexes.Count - 1))]
            $indexText = $slice -join ","
            $chunkName = "$NamePrefix-$($slice[0].ToString('D5'))-$($slice[-1].ToString('D5'))"
            $chunkCsv = Join-Path $DestinationDir "$chunkName.csv"
            $chunkSummary = Join-Path $DestinationDir "$chunkName-summary.csv"

            if ($Resume -and (Test-Path $chunkCsv)) {
                Write-Host "Skipping existing retry chunk $indexText"
                continue
            }

            $args = @("run", "--project", "src\Gba.Cli", "--configuration", $Configuration, "--no-build", "--", "compat", $RomRoot, "--suite", $Suite, "--indexes", $indexText, "--max-steps", "$Steps", "--frame-step-budget", "$FrameStepBudget", "--max-seconds", "$Seconds", "--output", $chunkCsv, "--summary-output", $chunkSummary)
            if (-not [string]::IsNullOrWhiteSpace($Bios)) {
                $args += @("--bios", $Bios)
            }
            if ($AlignRomEntry) {
                $args += @("--align-rom-entry")
            }

            if (-not $NoCapture) {
                $args += @("--capture-dir", (Join-Path $OutputDir "captures-retry"), "--capture-statuses", $CaptureStatuses)
            }

            Write-Host "Retrying timeout indexes $indexText with $Steps max steps"
            [void](Invoke-DotnetChecked -Arguments $args -TimeoutSeconds $RetryProcessTimeoutSeconds -AllowedExitCodes @(0, 4) -Description "Retry chunk $indexText")

            if ($PauseSeconds -gt 0) {
                Start-Sleep -Seconds $PauseSeconds
            }
        }
    }

    function ConvertTo-CsvField {
        param(
            [AllowNull()]
            [object]$Value
        )

        $text = if ($null -eq $Value) { "" } else { [string]$Value }
        if ($text.IndexOfAny([char[]]@(',', '"', "`r", "`n")) -lt 0) {
            return $text
        }

        return '"' + $text.Replace('"', '""') + '"'
    }

    function Get-RelativePathText {
        param(
            [string]$BasePath,
            [string]$TargetPath
        )

        $baseFullPath = [System.IO.Path]::GetFullPath($BasePath)
        if (-not $baseFullPath.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
            $baseFullPath += [System.IO.Path]::DirectorySeparatorChar
        }

        $targetFullPath = [System.IO.Path]::GetFullPath($TargetPath)
        $baseUri = [System.Uri]::new($baseFullPath)
        $targetUri = [System.Uri]::new($targetFullPath)
        return [System.Uri]::UnescapeDataString($baseUri.MakeRelativeUri($targetUri).ToString()).Replace("/", [System.IO.Path]::DirectorySeparatorChar)
    }

    function Get-ExpectedPhaseNames {
        switch ($Suite) {
            "single" { return @("single") }
            "boot" { return @("boot") }
            "standard" { return @("boot", "start-probe") }
            "input" { return @("boot", "start-probe", "broad-input") }
            "gameplay" { return @("boot", "start-probe", "broad-input", "long-input") }
            default { return @() }
        }
    }

    function Complete-ChunkWithProcessTimeoutRows {
        param(
            [string]$ChunkCsv,
            [int]$Start,
            [int]$End,
            [string]$Reason
        )

        $headers = @("index", "phase", "status", "classification", "frames", "steps", "cycles", "framesPerMillionSteps", "cyclesPerFrame", "distinctFrames", "changedFrames", "lastChangedFrame", "staticTailFrames", "firstHash", "lastHash", "pc", "cpsr", "mode", "thumb", "dispcnt", "dispstat", "vcount", "ie", "if", "ime", "activeObjects", "hiddenObjects", "title", "gameCode", "saveType", "romSize", "error", "capture", "path")
        $expectedPhases = Get-ExpectedPhaseNames
        $rows = @()
        if (Test-Path $ChunkCsv) {
            $rows = @(Import-Csv -LiteralPath $ChunkCsv)
            $firstLine = Get-Content -LiteralPath $ChunkCsv -TotalCount 1
            if (-not [string]::IsNullOrWhiteSpace($firstLine)) {
                $headers = $firstLine.Split(",")
            }
        }
        else {
            $parent = Split-Path -Parent $ChunkCsv
            if (-not [string]::IsNullOrWhiteSpace($parent)) {
                New-Item -ItemType Directory -Force -Path $parent | Out-Null
            }
        }

        $rowsByKey = @{}
        foreach ($row in $rows) {
            $rowsByKey["$($row.index)|$($row.phase)"] = $row
        }

        $linesToAppend = New-Object System.Collections.Generic.List[string]
        if (-not (Test-Path $ChunkCsv)) {
            $linesToAppend.Add(($headers -join ","))
        }

        for ($index = $Start; $index -le $End; $index++) {
            $existingForIndex = @($rows | Where-Object { [int]$_.index -eq $index } | Select-Object -First 1)
            $rom = $romFiles[$index - 1]
            $relativePath = if ($null -ne $rom) {
                Get-RelativePathText -BasePath (Resolve-Path $RomRoot).Path -TargetPath $rom.FullName
            }
            else {
                ""
            }

            foreach ($phase in $expectedPhases) {
                if ($rowsByKey.ContainsKey("$index|$phase")) {
                    continue
                }

                $values = @{}
                foreach ($header in $headers) {
                    $values[$header] = ""
                }

                $values["index"] = "$index"
                $values["phase"] = $phase
                $values["status"] = "timeout"
                $values["classification"] = "process-timeout"
                $values["frames"] = "0"
                $values["steps"] = "0"
                $values["cycles"] = "0"
                $values["framesPerMillionSteps"] = "0"
                $values["cyclesPerFrame"] = "0"
                $values["distinctFrames"] = "0"
                $values["changedFrames"] = "0"
                $values["lastChangedFrame"] = "0"
                $values["staticTailFrames"] = "0"
                $values["pc"] = if ($existingForIndex.Count -gt 0) { $existingForIndex[0].pc } else { "0x00000000" }
                $values["cpsr"] = if ($existingForIndex.Count -gt 0) { $existingForIndex[0].cpsr } else { "" }
                $values["mode"] = if ($existingForIndex.Count -gt 0) { $existingForIndex[0].mode } else { "" }
                $values["thumb"] = if ($existingForIndex.Count -gt 0) { $existingForIndex[0].thumb } else { "" }
                $values["title"] = if ($existingForIndex.Count -gt 0) { $existingForIndex[0].title } else { "" }
                $values["gameCode"] = if ($existingForIndex.Count -gt 0) { $existingForIndex[0].gameCode } else { "" }
                $values["saveType"] = if ($existingForIndex.Count -gt 0) { $existingForIndex[0].saveType } else { "" }
                $values["romSize"] = if ($existingForIndex.Count -gt 0) { $existingForIndex[0].romSize } elseif ($null -ne $rom) { "$($rom.Length)" } else { "0" }
                $values["error"] = $Reason
                $values["path"] = if ($existingForIndex.Count -gt 0) { $existingForIndex[0].path } else { $relativePath }

                $linesToAppend.Add(($headers | ForEach-Object { ConvertTo-CsvField $values[$_] }) -join ",")
            }
        }

        if ($linesToAppend.Count -gt 0) {
            Add-Content -LiteralPath $ChunkCsv -Value $linesToAppend
        }
    }

    function Test-ChunkComplete {
        param(
            [string]$ChunkCsv,
            [int]$Start,
            [int]$End
        )

        if (-not (Test-Path $ChunkCsv)) {
            return $false
        }

        $expectedPhases = Get-ExpectedPhaseNames
        $expectedRows = ($End - $Start + 1) * $expectedPhases.Count
        if ((Get-Item -LiteralPath $ChunkCsv).Length -eq 0) {
            return $false
        }

        $actualRows = @(Import-Csv -LiteralPath $ChunkCsv).Count
        return $actualRows -ge $expectedRows
    }

    $chunksRun = 0
    if ($MergeOnly) {
        $merged = Join-Path $OutputDir "compat-all.csv"
        $summary = Join-Path $OutputDir "compat-summary.csv"
        Merge-CompatReports -SourceDir $chunkDir -OutputPath $merged
        Run-Summary -ReportPath $merged -SummaryPath $summary
        Write-Host "Merged report: $((Resolve-Path $merged).Path)"
        Write-Host "Summary: $((Resolve-Path $summary).Path)"
        return
    }

    if ($StartIndex -lt 1 -or $StartIndex -gt $romCount) {
        throw "StartIndex must be between 1 and $romCount."
    }

    for ($start = $StartIndex; $start -le $romCount; $start += $ChunkSize) {
        $end = [Math]::Min($start + $ChunkSize - 1, $romCount)
        $chunkName = "compat-$($start.ToString('D5'))-$($end.ToString('D5'))"
        $chunkCsv = Join-Path $chunkDir "$chunkName.csv"
        $chunkSummary = Join-Path $chunkDir "$chunkName-summary.csv"
        $captureDir = Join-Path $OutputDir "captures"

        if ($Resume -and (Test-ChunkComplete -ChunkCsv $chunkCsv -Start $start -End $end)) {
            Write-Host "Skipping existing chunk $start-$end"
            continue
        }

        if ($Resume -and (Test-Path $chunkCsv)) {
            Write-Warning "Completing partial chunk $start-$end with process-timeout rows before resuming."
            Complete-ChunkWithProcessTimeoutRows -ChunkCsv $chunkCsv -Start $start -End $end -Reason "compat chunk was interrupted by a process timeout before all phases completed"
            continue
        }

        if ($MaxChunks -gt 0 -and $chunksRun -ge $MaxChunks) {
            Write-Host "Stopping after $chunksRun chunk(s) because -MaxChunks $MaxChunks was requested."
            break
        }

        $args = $commonArgs + @("--start-index", "$start", "--limit", "$($end - $start + 1)", "--output", $chunkCsv, "--summary-output", $chunkSummary)
        if (-not $NoCapture) {
            $args += @("--capture-dir", $captureDir, "--capture-statuses", $CaptureStatuses)
        }

        Write-Host "Running chunk $start-$end of $romCount ($Suite)"
        try {
            [void](Invoke-DotnetChecked -Arguments $args -TimeoutSeconds $ProcessTimeoutSeconds -AllowedExitCodes @(0, 4) -Description "Chunk $start-$end")
        }
        catch {
            Write-Warning $_.Exception.Message
            Complete-ChunkWithProcessTimeoutRows -ChunkCsv $chunkCsv -Start $start -End $end -Reason $_.Exception.Message
        }

        $chunksRun++
        if ($PauseSeconds -gt 0) {
            Start-Sleep -Seconds $PauseSeconds
        }
    }

    $merged = Join-Path $OutputDir "compat-all.csv"
    $summary = Join-Path $OutputDir "compat-summary.csv"
    Merge-CompatReports -SourceDir $chunkDir -OutputPath $merged
    Run-Summary -ReportPath $merged -SummaryPath $summary

    if ($RetryTimeoutSteps -gt 0 -and (Test-Path $merged)) {
        $timeoutIndexes = @(Import-Csv $merged | Where-Object { $_.status -eq "timeout" } | ForEach-Object { [int]$_.index } | Sort-Object -Unique)
        if ($timeoutIndexes.Count -gt 0) {
            $retryDir = Join-Path $OutputDir "timeout-retries"
            Run-CompatIndexes -Indexes $timeoutIndexes -DestinationDir $retryDir -NamePrefix "retry" -Steps $RetryTimeoutSteps -Seconds $RetryTimeoutSeconds -IndexesPerChunk $RetryChunkSize

            $retryMerged = Join-Path $OutputDir "compat-timeout-retries.csv"
            $retrySummary = Join-Path $OutputDir "compat-timeout-retries-summary.csv"
            Merge-CompatReports -SourceDir $retryDir -OutputPath $retryMerged
            Run-Summary -ReportPath $retryMerged -SummaryPath $retrySummary

            $best = Join-Path $OutputDir "compat-best.csv"
            $bestSummary = Join-Path $OutputDir "compat-best-summary.csv"
            $retryByIndex = @{}
            foreach ($row in Import-Csv $retryMerged) {
                $retryByIndex["$($row.index)|$($row.phase)"] = $row
            }

            $baseRows = @(Import-Csv $merged)
            $header = (Get-Content -LiteralPath $merged -First 1)
            Set-Content -LiteralPath $best -Value $header
            foreach ($row in $baseRows) {
                $chosen = $row
                $retryKey = "$($row.index)|$($row.phase)"
                if ($row.status -eq "timeout" -and $retryByIndex.ContainsKey($retryKey)) {
                    $chosen = $retryByIndex[$retryKey]
                }

                $values = $header.Split(",") | ForEach-Object { ConvertTo-CsvField $chosen.PSObject.Properties[$_].Value }
                Add-Content -LiteralPath $best -Value ($values -join ",")
            }

            Run-Summary -ReportPath $best -SummaryPath $bestSummary
        }
        else {
            Write-Host "No timeout rows found; skipping timeout retry pass."
        }
    }

    Write-Host "Merged report: $((Resolve-Path $merged).Path)"
    Write-Host "Summary: $((Resolve-Path $summary).Path)"
}
finally {
    Pop-Location
}
