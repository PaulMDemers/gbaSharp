param(
    [string]$RomRoot = "gba_collection",
    [string]$OutputRoot = "",
    [int]$StartIndex = 851,
    [int]$EndIndex = 0,
    [string]$SeedReport = "compat-retail-other-20260518-0001-0850\compat-all.csv",
    [int]$BlockSize = 100,
    [int]$ChunkSize = 5,
    [Int64]$MaxSteps = 300000000,
    [int]$FrameStepBudget = 150000,
    [int]$MaxSeconds = 180,
    [string]$CaptureStatuses = "crash,static,no-video,timeout",
    [switch]$NoCapture,
    [switch]$Resume,
    [switch]$NormalPriority,
    [int]$PauseSeconds = 1,
    [int]$MaxBlocks = 0,
    [switch]$PlanOnly,
    [switch]$RunTestsAtEnd
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$cliDll = Join-Path $repoRoot "src\Gba.Cli\bin\Release\net10.0\Gba.Cli.dll"

function Write-Log {
    param(
        [string]$Message,
        [string]$LogPath = ""
    )

    $line = "[{0}] {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Message
    Write-Host $line
    if (-not [string]::IsNullOrWhiteSpace($LogPath)) {
        Add-Content -LiteralPath $LogPath -Value $line
    }
}

function Get-GbaRomCount {
    param([string]$Root)

    $count = 0
    $rootPath = [System.IO.Path]::GetFullPath($Root)
    foreach ($path in [System.IO.Directory]::EnumerateFiles($rootPath, "*.gba", [System.IO.SearchOption]::AllDirectories)) {
        $count++
    }

    return $count
}

function Merge-CompatReports {
    param(
        [string[]]$Sources,
        [string]$OutputPath
    )

    $first = $true
    Remove-Item -LiteralPath $OutputPath -ErrorAction SilentlyContinue
    foreach ($source in $Sources) {
        if ([string]::IsNullOrWhiteSpace($source) -or -not (Test-Path -LiteralPath $source)) {
            continue
        }

        if ($first) {
            Get-Content -LiteralPath $source | Add-Content -LiteralPath $OutputPath
            $first = $false
        }
        else {
            Get-Content -LiteralPath $source | Select-Object -Skip 1 | Add-Content -LiteralPath $OutputPath
        }
    }
}

function Get-CompatCounts {
    param([string]$ReportPath)

    $rows = @(Import-Csv -LiteralPath $ReportPath)
    $boot = @($rows | Where-Object { $_.status -eq "boot" }).Count
    $crash = @($rows | Where-Object { $_.status -eq "crash" }).Count
    $timeout = @($rows | Where-Object { $_.status -eq "timeout" }).Count
    $static = @($rows | Where-Object { $_.status -eq "static" }).Count
    [pscustomobject]@{
        Rows = $rows.Count
        Boot = $boot
        Crash = $crash
        Timeout = $timeout
        Static = $static
    }
}

function Write-FailureReports {
    param(
        [string]$ReportPath,
        [string]$FailureCsv,
        [string]$FailureMarkdown,
        [string]$Title
    )

    $rows = @(Import-Csv -LiteralPath $ReportPath)
    $failures = @($rows | Where-Object { $_.status -ne "boot" })
    $failures |
        Select-Object index,phase,status,classification,frames,pc,cpsr,mode,thumb,title,gameCode,saveType,error,path,capture |
        Export-Csv -LiteralPath $FailureCsv -NoTypeInformation

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# $Title")
    $lines.Add("")
    $lines.Add("Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
    $lines.Add("")
    if ($failures.Count -eq 0) {
        $lines.Add("No non-boot rows.")
    }
    else {
        $lines.Add("| Index | Phase | Status | Classification | Frames | PC | Title | Code | Error |")
        $lines.Add("| ---: | --- | --- | --- | ---: | --- | --- | --- | --- |")
        foreach ($row in $failures) {
            $errorText = ([string]$row.error).Replace("|", "\|")
            $titleText = ([string]$row.title).Replace("|", "\|")
            $lines.Add("| $($row.index) | $($row.phase) | $($row.status) | $($row.classification) | $($row.frames) | $($row.pc) | $titleText | $($row.gameCode) | $errorText |")
        }
    }

    Set-Content -LiteralPath $FailureMarkdown -Value $lines
}

function Invoke-CompatSummary {
    param(
        [string]$ReportPath,
        [string]$SummaryPath
    )

    & dotnet $cliDll compat-summary $ReportPath $SummaryPath | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "compat-summary failed for $ReportPath with exit code $LASTEXITCODE"
    }
}

Push-Location $repoRoot
try {
    if (-not (Test-Path -LiteralPath $cliDll)) {
        throw "Release CLI not found at $cliDll. Build Release before running the sweep."
    }

    if (-not $NormalPriority) {
        try {
            (Get-Process -Id $PID).PriorityClass = "BelowNormal"
        }
        catch {
            Write-Warning "Could not lower process priority: $($_.Exception.Message)"
        }
    }

    if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
        $OutputRoot = "compat-retail-full-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
    }

    if ($EndIndex -le 0) {
        $EndIndex = Get-GbaRomCount -Root $RomRoot
    }

    if ($StartIndex -lt 1 -or $EndIndex -lt $StartIndex) {
        throw "Invalid sweep range: StartIndex=$StartIndex EndIndex=$EndIndex"
    }

    New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
    $blockRoot = Join-Path $OutputRoot "blocks"
    $cumulativeRoot = Join-Path $OutputRoot "cumulative"
    New-Item -ItemType Directory -Force -Path $blockRoot | Out-Null
    New-Item -ItemType Directory -Force -Path $cumulativeRoot | Out-Null
    $logPath = Join-Path $OutputRoot "run.log"
    $statePath = Join-Path $OutputRoot "state.json"

    Write-Log "Full archive gameplay sweep configured: range $StartIndex-$EndIndex, block size $BlockSize, chunk size $ChunkSize." $logPath
    Write-Log "Output root: $OutputRoot" $logPath
    if (-not [string]::IsNullOrWhiteSpace($SeedReport) -and (Test-Path -LiteralPath $SeedReport)) {
        Write-Log "Seed report: $SeedReport" $logPath
    }
    else {
        Write-Log "No seed report will be merged." $logPath
        $SeedReport = ""
    }

    if ($PlanOnly) {
        Write-Log "PlanOnly set; exiting before running chunks." $logPath
        return
    }

    "RUNNING" | Set-Content -LiteralPath (Join-Path $OutputRoot "status.txt")
    $blocksRun = 0

    for ($blockStart = $StartIndex; $blockStart -le $EndIndex; $blockStart += $BlockSize) {
        if ($MaxBlocks -gt 0 -and $blocksRun -ge $MaxBlocks) {
            Write-Log "Stopping after $blocksRun block(s) because -MaxBlocks $MaxBlocks was requested." $logPath
            break
        }

        $blockEnd = [Math]::Min($blockStart + $BlockSize - 1, $EndIndex)
        $blockName = "compat-{0:d4}-{1:d4}" -f $blockStart, $blockEnd
        $blockDir = Join-Path $blockRoot $blockName
        New-Item -ItemType Directory -Force -Path $blockDir | Out-Null
        Write-Log "Starting block $blockStart-$blockEnd." $logPath

        for ($chunkStart = $blockStart; $chunkStart -le $blockEnd; $chunkStart += $ChunkSize) {
            $chunkEnd = [Math]::Min($chunkStart + $ChunkSize - 1, $blockEnd)
            $range = "$chunkStart-$chunkEnd"
            $chunkCsv = Join-Path $blockDir "compat-$range.csv"
            $chunkSummary = Join-Path $blockDir "compat-$range-summary.csv"
            $captureDir = Join-Path $blockDir "captures-$range"

            if ($Resume -and (Test-Path -LiteralPath $chunkCsv)) {
                Write-Log "Skipping existing chunk $range." $logPath
                continue
            }

            $args = @(
                $cliDll,
                "compat",
                $RomRoot,
                "--suite", "gameplay",
                "--indexes", $range,
                "--max-steps", "$MaxSteps",
                "--frame-step-budget", "$FrameStepBudget",
                "--max-seconds", "$MaxSeconds",
                "--output", $chunkCsv,
                "--summary-output", $chunkSummary
            )

            if (-not $NoCapture) {
                $args += @("--capture-dir", $captureDir, "--capture-statuses", $CaptureStatuses)
            }

            Write-Log "Running chunk $range." $logPath
            & dotnet @args
            $exit = $LASTEXITCODE
            if ($exit -ne 0 -and $exit -ne 4) {
                "FAILED" | Set-Content -LiteralPath (Join-Path $OutputRoot "status.txt")
                throw "Chunk $range failed with exit code $exit"
            }

            if ($PauseSeconds -gt 0) {
                Start-Sleep -Seconds $PauseSeconds
            }
        }

        $blockReport = Join-Path $blockDir "compat-all.csv"
        $blockSummary = Join-Path $blockDir "compat-summary.csv"
        $chunkReports = @(Get-ChildItem -Path $blockDir -Filter "compat-*.csv" |
            Where-Object { $_.Name -notlike "*summary*" -and $_.Name -ne "compat-all.csv" } |
            Sort-Object Name |
            ForEach-Object { $_.FullName })

        Merge-CompatReports -Sources $chunkReports -OutputPath $blockReport
        Invoke-CompatSummary -ReportPath $blockReport -SummaryPath $blockSummary
        Write-FailureReports -ReportPath $blockReport -FailureCsv (Join-Path $blockDir "failures.csv") -FailureMarkdown (Join-Path $blockDir "failures.md") -Title "Block $blockStart-$blockEnd Failures"
        $blockCounts = Get-CompatCounts -ReportPath $blockReport
        Write-Log "Completed block ${blockStart}-${blockEnd}: $($blockCounts.Boot)/$($blockCounts.Rows) boot, $($blockCounts.Crash) crash, $($blockCounts.Timeout) timeout, $($blockCounts.Static) static." $logPath

        $cumulativeReport = Join-Path $cumulativeRoot "compat-all.csv"
        $cumulativeSummary = Join-Path $cumulativeRoot "compat-summary.csv"
        $blockReports = @(Get-ChildItem -Path $blockRoot -Recurse -Filter "compat-all.csv" |
            Sort-Object FullName |
            ForEach-Object { $_.FullName })
        $sources = @()
        if (-not [string]::IsNullOrWhiteSpace($SeedReport)) {
            $sources += $SeedReport
        }

        $sources += $blockReports
        Merge-CompatReports -Sources $sources -OutputPath $cumulativeReport
        Invoke-CompatSummary -ReportPath $cumulativeReport -SummaryPath $cumulativeSummary
        Write-FailureReports -ReportPath $cumulativeReport -FailureCsv (Join-Path $cumulativeRoot "failures.csv") -FailureMarkdown (Join-Path $cumulativeRoot "failures.md") -Title "Cumulative Failures"
        $cumulativeCounts = Get-CompatCounts -ReportPath $cumulativeReport

        [pscustomobject]@{
            UpdatedAt = (Get-Date).ToString("o")
            Status = "RUNNING"
            OutputRoot = (Resolve-Path $OutputRoot).Path
            LastCompletedBlock = "$blockStart-$blockEnd"
            RangeStart = $StartIndex
            RangeEnd = $EndIndex
            CumulativeRows = $cumulativeCounts.Rows
            CumulativeBoot = $cumulativeCounts.Boot
            CumulativeCrash = $cumulativeCounts.Crash
            CumulativeTimeout = $cumulativeCounts.Timeout
            CumulativeStatic = $cumulativeCounts.Static
            NextIndex = $blockEnd + 1
        } | ConvertTo-Json | Set-Content -LiteralPath $statePath

        Write-Log "Cumulative: $($cumulativeCounts.Boot)/$($cumulativeCounts.Rows) boot, $($cumulativeCounts.Crash) crash, $($cumulativeCounts.Timeout) timeout, $($cumulativeCounts.Static) static." $logPath
        $blocksRun++
    }

    $finalReport = Join-Path $cumulativeRoot "compat-all.csv"
    if (Test-Path -LiteralPath $finalReport) {
        $finalCounts = Get-CompatCounts -ReportPath $finalReport
        [pscustomobject]@{
            UpdatedAt = (Get-Date).ToString("o")
            Status = "COMPLETE"
            OutputRoot = (Resolve-Path $OutputRoot).Path
            RangeStart = $StartIndex
            RangeEnd = $EndIndex
            CumulativeRows = $finalCounts.Rows
            CumulativeBoot = $finalCounts.Boot
            CumulativeCrash = $finalCounts.Crash
            CumulativeTimeout = $finalCounts.Timeout
            CumulativeStatic = $finalCounts.Static
            Report = (Resolve-Path $finalReport).Path
            Summary = (Resolve-Path (Join-Path $cumulativeRoot "compat-summary.csv")).Path
            Failures = (Resolve-Path (Join-Path $cumulativeRoot "failures.csv")).Path
        } | ConvertTo-Json | Set-Content -LiteralPath $statePath
    }

    if ($RunTestsAtEnd) {
        Write-Log "Running release tests." $logPath
        & dotnet test gbaSharp.slnx -c Release --no-build -v minimal
        if ($LASTEXITCODE -ne 0) {
            throw "Release tests failed with exit code $LASTEXITCODE"
        }
    }

    "COMPLETE" | Set-Content -LiteralPath (Join-Path $OutputRoot "status.txt")
    Write-Log "Sweep complete." $logPath
}
catch {
    if (-not [string]::IsNullOrWhiteSpace($OutputRoot) -and (Test-Path -LiteralPath $OutputRoot)) {
        "FAILED" | Set-Content -LiteralPath (Join-Path $OutputRoot "status.txt")
    }

    Write-Error $_
    exit 1
}
finally {
    Pop-Location
}
