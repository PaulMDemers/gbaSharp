param(
    [string]$Manifest = "docs\gba-a-plus-milestone.csv",
    [int]$Priority = 0,
    [string]$Category = "",
    [int]$SkipItems = 0,
    [int]$MaxItems = 0,
    [ValidateSet("single", "boot", "standard", "input", "gameplay")]
    [string]$Suite = "input",
    [string]$OutputDir = "",
    [int]$ChunkSize = 2,
    [int]$MaxSteps = 100000000,
    [int]$FrameStepBudget = 150000,
    [int]$MaxSeconds = 180,
    [int]$RetryTimeoutSteps = 150000000,
    [int]$RetryTimeoutSeconds = 240,
    [int]$RetryChunkSize = 2,
    [int]$ProcessTimeoutSeconds = 0,
    [int]$RetryProcessTimeoutSeconds = 0,
    [switch]$Resume,
    [switch]$NoCapture
)

$ErrorActionPreference = "Stop"

function Merge-CsvReports {
    param(
        [string]$SourceDir,
        [string]$OutputPath
    )

    $first = $true
    Remove-Item -LiteralPath $OutputPath -ErrorAction SilentlyContinue
    foreach ($csv in Get-ChildItem -Path $SourceDir -Filter "*.csv" | Where-Object { $_.Name -notlike "*-summary.csv" } | Sort-Object Name) {
        if ($first) {
            Get-Content -LiteralPath $csv.FullName | Add-Content -LiteralPath $OutputPath
            $first = $false
        }
        else {
            Get-Content -LiteralPath $csv.FullName | Select-Object -Skip 1 | Add-Content -LiteralPath $OutputPath
        }
    }
}

function Write-BestReport {
    param(
        [string]$BasePath,
        [string]$RetryPath,
        [string]$OutputPath
    )

    $rows = @(Import-Csv $BasePath)
    if (Test-Path $RetryPath) {
        $retryRows = @(Import-Csv $RetryPath)
        $retryByKey = @{}
        foreach ($row in $retryRows) {
            $retryByKey["$($row.index)|$($row.phase)"] = $row
        }

        $rows = @($rows | ForEach-Object {
            $key = "$($_.index)|$($_.phase)"
            if ($retryByKey.ContainsKey($key)) {
                $retryByKey[$key]
            }
            else {
                $_
            }
        })
    }

    $rows | Export-Csv -LiteralPath $OutputPath -NoTypeInformation
}

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
    try {
        (Get-Process -Id $PID).PriorityClass = "BelowNormal"
        Write-Host "Running milestone sweep at BelowNormal process priority."
    }
    catch {
        Write-Warning "Could not lower process priority: $($_.Exception.Message)"
    }

    if ([string]::IsNullOrWhiteSpace($OutputDir)) {
        $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
        $OutputDir = "compat-milestone-$stamp"
    }

    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
    $chunkDir = Join-Path $OutputDir "chunks"
    New-Item -ItemType Directory -Force -Path $chunkDir | Out-Null

    $items = @(Import-Csv $Manifest | Where-Object { -not [string]::IsNullOrWhiteSpace($_.index) })
    if ($Priority -gt 0) {
        $items = @($items | Where-Object { [int]$_.priority -eq $Priority })
    }

    if (-not [string]::IsNullOrWhiteSpace($Category)) {
        $items = @($items | Where-Object { $_.category -eq $Category })
    }

    if ($SkipItems -gt 0) {
        $items = @($items | Select-Object -Skip $SkipItems)
    }

    if ($MaxItems -gt 0) {
        $items = @($items | Select-Object -First $MaxItems)
    }

    if ($items.Count -eq 0) {
        throw "No milestone rows matched the requested filters."
    }

    for ($offset = 0; $offset -lt $items.Count; $offset += $ChunkSize) {
        $slice = @($items[$offset..([Math]::Min($offset + $ChunkSize - 1, $items.Count - 1))])
        $indexes = ($slice | ForEach-Object { $_.index }) -join ","
        $chunkName = "milestone-$($offset.ToString('D3'))-$(([Math]::Min($offset + $ChunkSize - 1, $items.Count - 1)).ToString('D3'))"
        $chunkCsv = Join-Path $chunkDir "$chunkName.csv"
        $chunkSummary = Join-Path $chunkDir "$chunkName-summary.csv"

        if ($Resume -and (Test-Path $chunkCsv)) {
            Write-Host "Skipping existing milestone chunk $indexes"
            continue
        }

        $args = @("run", "--project", "src\Gba.Cli", "--no-build", "--", "compat", "gba_collection", "--suite", $Suite, "--indexes", $indexes, "--max-steps", "$MaxSteps", "--frame-step-budget", "$FrameStepBudget", "--max-seconds", "$MaxSeconds", "--output", $chunkCsv, "--summary-output", $chunkSummary)
        if (-not $NoCapture) {
            $args += @("--capture-dir", (Join-Path $OutputDir "captures"), "--capture-statuses", "crash,static,no-video,timeout")
        }

        Write-Host "Running milestone indexes $indexes"
        [void](Invoke-DotnetChecked -Arguments $args -TimeoutSeconds $ProcessTimeoutSeconds -AllowedExitCodes @(0, 4) -Description "Milestone chunk $indexes")

        Start-Sleep -Seconds 2
    }

    $all = Join-Path $OutputDir "milestone-all.csv"
    $summary = Join-Path $OutputDir "milestone-summary.csv"
    Merge-CsvReports $chunkDir $all
    [void](Invoke-DotnetChecked -Arguments @("run", "--project", "src\Gba.Cli", "--no-build", "--", "compat-summary", $all, $summary) -TimeoutSeconds 120 -AllowedExitCodes @(0) -Description "Summary")

    $timeouts = @(Import-Csv $all | Where-Object { $_.status -eq "timeout" } | ForEach-Object { [int]$_.index } | Sort-Object -Unique)
    $retryAll = Join-Path $OutputDir "milestone-timeout-retries.csv"
    if ($RetryTimeoutSteps -gt 0 -and $timeouts.Count -gt 0) {
        $retryDir = Join-Path $OutputDir "timeout-retries"
        New-Item -ItemType Directory -Force -Path $retryDir | Out-Null
        for ($offset = 0; $offset -lt $timeouts.Count; $offset += $RetryChunkSize) {
            $slice = @($timeouts[$offset..([Math]::Min($offset + $RetryChunkSize - 1, $timeouts.Count - 1))])
            $indexes = $slice -join ","
            $retryCsv = Join-Path $retryDir "retry-$($slice[0].ToString('D5'))-$($slice[-1].ToString('D5')).csv"
            $retrySummary = $retryCsv.Replace(".csv", "-summary.csv")
            if ($Resume -and (Test-Path $retryCsv)) {
                Write-Host "Skipping existing retry chunk $indexes"
                continue
            }

            Write-Host "Retrying milestone timeouts $indexes"
            [void](Invoke-DotnetChecked -Arguments @("run", "--project", "src\Gba.Cli", "--no-build", "--", "compat", "gba_collection", "--suite", $Suite, "--indexes", $indexes, "--max-steps", "$RetryTimeoutSteps", "--frame-step-budget", "$FrameStepBudget", "--max-seconds", "$RetryTimeoutSeconds", "--output", $retryCsv, "--summary-output", $retrySummary) -TimeoutSeconds $RetryProcessTimeoutSeconds -AllowedExitCodes @(0, 4) -Description "Milestone retry $indexes")

            Start-Sleep -Seconds 2
        }

        $retrySummaryAll = Join-Path $OutputDir "milestone-timeout-retries-summary.csv"
        Merge-CsvReports $retryDir $retryAll
        [void](Invoke-DotnetChecked -Arguments @("run", "--project", "src\Gba.Cli", "--no-build", "--", "compat-summary", $retryAll, $retrySummaryAll) -TimeoutSeconds 120 -AllowedExitCodes @(0) -Description "Retry summary")
    }

    $best = Join-Path $OutputDir "milestone-best.csv"
    $bestSummary = Join-Path $OutputDir "milestone-best-summary.csv"
    Write-BestReport $all $retryAll $best
    [void](Invoke-DotnetChecked -Arguments @("run", "--project", "src\Gba.Cli", "--no-build", "--", "compat-summary", $best, $bestSummary) -TimeoutSeconds 120 -AllowedExitCodes @(0) -Description "Best summary")

    Write-Host "Milestone report: $((Resolve-Path $all).Path)"
    Write-Host "Milestone summary: $((Resolve-Path $summary).Path)"
    Write-Host "Milestone best report: $((Resolve-Path $best).Path)"
    Write-Host "Milestone best summary: $((Resolve-Path $bestSummary).Path)"
}
finally {
    Pop-Location
}
