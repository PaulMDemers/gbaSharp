param(
    [string]$Manifest = "docs\gba-audio-smoke-routes.csv",
    [string]$OutputDir = "",
    [string]$Bios = "",
    [string]$Configuration = "Release",
    [int]$MaxItems = 0,
    [int]$SkipItems = 0,
    [int]$DefaultStopFrame = 300,
    [long]$DefaultMaxSteps = 500000000,
    [int]$DefaultMaxSeconds = 180,
    [int]$ProcessTimeoutSeconds = 240,
    [double]$WavGain = 0.5,
    [switch]$NoBuild,
    [switch]$NoAlignRomEntry,
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

function Invoke-CheckedProcess {
    param(
        [string]$FileName,
        [string[]]$Arguments,
        [int]$TimeoutSeconds,
        [string]$Description
    )

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $FileName
    $psi.Arguments = Join-ProcessArguments $Arguments
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true

    $process = [System.Diagnostics.Process]::Start($psi)
    if ($null -eq $process) {
        throw "Failed to start $Description"
    }

    try {
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
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

        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
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

function Get-StringOrDefault {
    param([object]$Item, [string]$Property, [string]$Default)

    if ($Item.PSObject.Properties.Name -notcontains $Property -or [string]::IsNullOrWhiteSpace($Item.$Property)) {
        return $Default
    }

    return [string]$Item.$Property
}

function Get-IntOrDefault {
    param([object]$Item, [string]$Property, [int]$Default)

    $value = Get-StringOrDefault -Item $Item -Property $Property -Default ""
    if ([string]::IsNullOrWhiteSpace($value)) {
        return $Default
    }

    return [int]$value
}

function Get-LongOrDefault {
    param([object]$Item, [string]$Property, [long]$Default)

    $value = Get-StringOrDefault -Item $Item -Property $Property -Default ""
    if ([string]::IsNullOrWhiteSpace($value)) {
        return $Default
    }

    return [long]$value
}

function Get-BoolOrDefault {
    param([object]$Item, [string]$Property, [bool]$Default)

    $value = Get-StringOrDefault -Item $Item -Property $Property -Default ""
    if ([string]::IsNullOrWhiteSpace($value)) {
        return $Default
    }

    return $value.Equals("true", [StringComparison]::OrdinalIgnoreCase)
}

function Get-FrameFromOutput {
    param([string]$Text)

    if ($Text -match 'frame=([0-9,]+)') {
        return [int](($Matches[1]).Replace(",", ""))
    }

    return 0
}

function Get-CyclesFromOutput {
    param([string]$Text)

    if ($Text -match 'cycles=([0-9,]+)') {
        return [long](($Matches[1]).Replace(",", ""))
    }

    return 0
}

function Get-CsvDataRows {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return 0
    }

    return [Math]::Max(0, @((Get-Content -LiteralPath $Path) | Select-Object -Skip 1).Count)
}

function Get-WavFramesFromOutput {
    param([string]$Text)

    if ($Text -match 'Wrote\s+([0-9,]+)\s+stereo frames') {
        return [long](($Matches[1]).Replace(",", ""))
    }

    return 0
}

function Get-WavSecondsFromOutput {
    param([string]$Text)

    if ($Text -match '\(([0-9.]+)s\)') {
        return [double]$Matches[1]
    }

    return 0.0
}

function Get-WavPcmMetrics {
    param([string]$Path)

    $empty = [pscustomobject]@{
        PeakPercent = 0.0
        RmsPercent = 0.0
        ClippedSamples = 0
    }

    if (-not (Test-Path -LiteralPath $Path)) {
        return $empty
    }

    $bytes = [System.IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $Path))
    if ($bytes.Length -lt 44) {
        return $empty
    }

    $riff = [System.Text.Encoding]::ASCII.GetString($bytes, 0, 4)
    $wave = [System.Text.Encoding]::ASCII.GetString($bytes, 8, 4)
    if ($riff -ne "RIFF" -or $wave -ne "WAVE") {
        return $empty
    }

    $dataOffset = -1
    $dataLength = 0
    $offset = 12
    while ($offset + 8 -le $bytes.Length) {
        $chunkId = [System.Text.Encoding]::ASCII.GetString($bytes, $offset, 4)
        $chunkLength = [BitConverter]::ToUInt32($bytes, $offset + 4)
        $chunkDataOffset = $offset + 8
        if ($chunkId -eq "data") {
            $dataOffset = $chunkDataOffset
            $dataLength = [Math]::Min([int]$chunkLength, $bytes.Length - $chunkDataOffset)
            break
        }

        $offset = $chunkDataOffset + [int]$chunkLength
        if (($chunkLength % 2) -ne 0) {
            $offset++
        }
    }

    if ($dataOffset -lt 0 -or $dataLength -lt 2) {
        return $empty
    }

    $sampleCount = [int]($dataLength / 2)
    $peak = 0
    $clipped = 0
    $sumSquares = 0.0
    for ($sampleIndex = 0; $sampleIndex -lt $sampleCount; $sampleIndex++) {
        $sample = [BitConverter]::ToInt16($bytes, $dataOffset + ($sampleIndex * 2))
        $absolute = [Math]::Abs([int]$sample)
        if ($absolute -gt $peak) {
            $peak = $absolute
        }

        if ($sample -eq 32767 -or $sample -eq -32768) {
            $clipped++
        }

        $value = [double]$sample
        $sumSquares += $value * $value
    }

    $rms = if ($sampleCount -gt 0) { [Math]::Sqrt($sumSquares / $sampleCount) } else { 0.0 }
    return [pscustomobject]@{
        PeakPercent = [Math]::Round(($peak / 32768.0) * 100.0, 3)
        RmsPercent = [Math]::Round(($rms / 32768.0) * 100.0, 3)
        ClippedSamples = $clipped
    }
}

function Get-AudioSignalStatus {
    param($Metrics)

    if ($Metrics.ClippedSamples -gt 0) {
        return "clipped"
    }

    if ($Metrics.PeakPercent -le 0) {
        return "silent"
    }

    return "ok"
}

function Write-MarkdownReport {
    param(
        [object[]]$Rows,
        [string]$Path,
        [string]$ManifestPath
    )

    $passed = @($Rows | Where-Object { $_.status -eq "pass" }).Count
    $failed = @($Rows | Where-Object { $_.status -ne "pass" }).Count
    $signalGroups = @($Rows | Group-Object signalStatus | Sort-Object Name)
    $signalSummary = if ($signalGroups.Count -gt 0) {
        ($signalGroups | ForEach-Object { "$($_.Name): $($_.Count)" }) -join ", "
    }
    else {
        "none"
    }

    $lines = @(
        "# GBA Audio Smoke Report",
        "",
        "- Manifest: ``$ManifestPath``",
        "- Rows: $($Rows.Count)",
        "- Pass: $passed",
        "- Non-pass: $failed",
        "- Signal status: $signalSummary",
        "",
        "| Label | Status | Signal | Frame | Direct Samples | PSG Samples | WAV Seconds | Peak % | RMS % | Clipped | Mixed WAV |",
        "| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |"
    )

    foreach ($row in $Rows) {
        $wav = if ([string]::IsNullOrWhiteSpace($row.mixedWav)) { "" } else { $row.mixedWav }
        $wavSeconds = if ($row.PSObject.Properties.Name -contains "wavSeconds") { "{0:N3}" -f [double]$row.wavSeconds } else { "0.000" }
        $wavPeak = if ($row.PSObject.Properties.Name -contains "wavPeakPercent") { "{0:N3}" -f [double]$row.wavPeakPercent } else { "0.000" }
        $wavRms = if ($row.PSObject.Properties.Name -contains "wavRmsPercent") { "{0:N3}" -f [double]$row.wavRmsPercent } else { "0.000" }
        $wavClipped = if ($row.PSObject.Properties.Name -contains "wavClippedSamples") { [long]$row.wavClippedSamples } else { 0 }
        $signal = if ($row.PSObject.Properties.Name -contains "signalStatus") { $row.signalStatus } else { "" }
        $lines += "| $($row.label) | $($row.status) | $signal | $($row.observedFrame) | $($row.directSamples) | $($row.psgSamples) | $wavSeconds | $wavPeak | $wavRms | $wavClipped | $wav |"
    }

    $lines | Set-Content -LiteralPath $Path -Encoding UTF8
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $repoRoot
try {
    if (-not $NormalPriority) {
        try {
            (Get-Process -Id $PID).PriorityClass = "BelowNormal"
            Write-Host "Running audio smoke at BelowNormal process priority."
        }
        catch {
            Write-Warning "Could not lower process priority: $($_.Exception.Message)"
        }
    }

    if (-not $NoBuild) {
        $buildResult = Invoke-CheckedProcess -FileName "dotnet" -Arguments @("build", "src\Gba.Cli\Gba.Cli.csproj", "-c", $Configuration) -TimeoutSeconds 180 -Description "Build Gba.Cli"
        if ($buildResult.ExitCode -ne 0) {
            throw "Build failed with exit code $($buildResult.ExitCode): $($buildResult.Stdout) $($buildResult.Stderr)"
        }
    }

    $cliDll = Get-ChildItem -Path (Join-Path "src\Gba.Cli\bin" $Configuration) -Recurse -Filter "Gba.Cli.dll" |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($null -eq $cliDll) {
        throw "Could not find built Gba.Cli.dll under src\Gba.Cli\bin\$Configuration. Run without -NoBuild once to build it."
    }

    if ([string]::IsNullOrWhiteSpace($OutputDir)) {
        $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
        $OutputDir = "audio-smoke-$stamp"
    }

    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
    $frameDir = Join-Path $OutputDir "frames"
    $csvDir = Join-Path $OutputDir "csv"
    $summaryDir = Join-Path $OutputDir "summaries"
    $wavDir = Join-Path $OutputDir "wav"
    $logDir = Join-Path $OutputDir "logs"
    foreach ($dir in @($frameDir, $csvDir, $summaryDir, $wavDir, $logDir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }

    $items = @(Import-Csv -LiteralPath $Manifest)
    if ($SkipItems -gt 0) {
        $items = @($items | Select-Object -Skip $SkipItems)
    }

    if ($MaxItems -gt 0) {
        $items = @($items | Select-Object -First $MaxItems)
    }

    $results = New-Object System.Collections.Generic.List[object]
    foreach ($item in $items) {
        $label = Get-SafeName (Get-StringOrDefault -Item $item -Property "label" -Default "audio-smoke")
        $romPath = Get-StringOrDefault -Item $item -Property "romPath" -Default ""
        if ([string]::IsNullOrWhiteSpace($romPath)) {
            $results.Add([pscustomobject]@{
                label = $label
                status = "missing-rom"
                signalStatus = "missing"
                exitCode = -1
                observedFrame = 0
                cycles = 0
                directSamples = 0
                psgSamples = 0
                framePpm = ""
                directCsv = ""
                psgCsv = ""
                directSummary = ""
                psgSummary = ""
                wavFrames = 0
                wavSeconds = 0
                wavPeakPercent = 0
                wavRmsPercent = 0
                wavClippedSamples = 0
                mixedWav = ""
                romPath = ""
                message = "Manifest row has no romPath"
            })
            continue
        }

        $resolvedRom = Resolve-Path -LiteralPath $romPath -ErrorAction SilentlyContinue
        if ($null -eq $resolvedRom) {
            $results.Add([pscustomobject]@{
                label = $label
                status = "missing-rom"
                signalStatus = "missing"
                exitCode = -1
                observedFrame = 0
                cycles = 0
                directSamples = 0
                psgSamples = 0
                framePpm = ""
                directCsv = ""
                psgCsv = ""
                directSummary = ""
                psgSummary = ""
                wavFrames = 0
                wavSeconds = 0
                wavPeakPercent = 0
                wavRmsPercent = 0
                wavClippedSamples = 0
                mixedWav = ""
                romPath = $romPath
                message = "ROM not found"
            })
            continue
        }

        $framePpm = Join-Path $frameDir "$label.ppm"
        $directCsv = Join-Path $csvDir "$label-direct.csv"
        $psgCsv = Join-Path $csvDir "$label-psg.csv"
        $directSummary = Join-Path $summaryDir "$label-direct.md"
        $psgSummary = Join-Path $summaryDir "$label-psg.md"
        $mixedWav = Join-Path $wavDir "$label-mixed.wav"
        $stdoutLog = Join-Path $logDir "$label.stdout.txt"
        $stderrLog = Join-Path $logDir "$label.stderr.txt"

        if ($Resume -and (Test-Path -LiteralPath $mixedWav)) {
            Write-Host "Skipping existing audio smoke $label"
            continue
        }

        $stopFrame = Get-IntOrDefault -Item $item -Property "stopFrame" -Default $DefaultStopFrame
        $maxSteps = Get-LongOrDefault -Item $item -Property "maxSteps" -Default $DefaultMaxSteps
        $maxSeconds = Get-IntOrDefault -Item $item -Property "maxSeconds" -Default $DefaultMaxSeconds
        $alignRomEntry = Get-BoolOrDefault -Item $item -Property "alignRomEntry" -Default $true

        $args = @(
            $cliDll.FullName,
            "dump-frame", $resolvedRom.Path,
            "--stop-frame", "$stopFrame",
            "--max-steps", "$maxSteps",
            "--max-seconds", "$maxSeconds",
            "--output", $framePpm,
            "--audio-csv", $directCsv,
            "--psg-csv", $psgCsv
        )

        if (-not $NoAlignRomEntry -and $alignRomEntry) {
            $args += "--align-rom-entry"
        }

        if (-not [string]::IsNullOrWhiteSpace($Bios)) {
            $args += @("--bios", $Bios)
        }

        $keys = Get-StringOrDefault -Item $item -Property "keys" -Default ""
        if (-not [string]::IsNullOrWhiteSpace($keys)) {
            $args += @("--keys", $keys)
        }

        $inputScript = Get-StringOrDefault -Item $item -Property "inputScript" -Default ""
        if (-not [string]::IsNullOrWhiteSpace($inputScript)) {
            $args += @("--input-script", $inputScript)
        }

        $saveFile = Get-StringOrDefault -Item $item -Property "saveFile" -Default ""
        if (-not [string]::IsNullOrWhiteSpace($saveFile)) {
            $args += @("--save-file", $saveFile)
            if (Get-BoolOrDefault -Item $item -Property "saveReadOnly" -Default $false) {
                $args += "--save-read-only"
            }
        }

        Write-Host "Running audio smoke $label"
        $result = Invoke-CheckedProcess -FileName "dotnet" -Arguments $args -TimeoutSeconds $ProcessTimeoutSeconds -Description "Audio smoke $label"
        $result.Stdout | Set-Content -LiteralPath $stdoutLog -Encoding UTF8
        $result.Stderr | Set-Content -LiteralPath $stderrLog -Encoding UTF8
        $message = (($result.Stdout + " " + $result.Stderr) -replace '\s+', ' ').Trim()
        $observedFrame = Get-FrameFromOutput $message
        $cycles = Get-CyclesFromOutput $message

        $directSamples = Get-CsvDataRows $directCsv
        $psgSamples = Get-CsvDataRows $psgCsv
        if (Test-Path -LiteralPath $directCsv) {
            $summaryResult = Invoke-CheckedProcess -FileName "python" -Arguments @("scripts\analyze-audio-csv.py", $directCsv, "--output", $directSummary) -TimeoutSeconds 60 -Description "Analyze direct audio $label"
            if ($summaryResult.ExitCode -ne 0) {
                $message = "$message direct-summary-error: $($summaryResult.Stderr)"
            }
        }

        if (Test-Path -LiteralPath $psgCsv) {
            $summaryResult = Invoke-CheckedProcess -FileName "python" -Arguments @("scripts\analyze-audio-csv.py", $psgCsv, "--output", $psgSummary) -TimeoutSeconds 60 -Description "Analyze PSG audio $label"
            if ($summaryResult.ExitCode -ne 0) {
                $message = "$message psg-summary-error: $($summaryResult.Stderr)"
            }
        }

        if (Test-Path -LiteralPath $directCsv) {
            $wavResult = Invoke-CheckedProcess -FileName "python" -Arguments @("scripts\audio-csv-to-wav.py", $directCsv, $mixedWav, "--mix", $psgCsv, "--gain", $WavGain.ToString([Globalization.CultureInfo]::InvariantCulture)) -TimeoutSeconds 120 -Description "Export mixed audio $label"
        }
        elseif (Test-Path -LiteralPath $psgCsv) {
            $wavResult = Invoke-CheckedProcess -FileName "python" -Arguments @("scripts\audio-csv-to-wav.py", $psgCsv, $mixedWav, "--gain", $WavGain.ToString([Globalization.CultureInfo]::InvariantCulture)) -TimeoutSeconds 120 -Description "Export PSG audio $label"
        }
        else {
            $wavResult = [pscustomobject]@{ ExitCode = -1; Stdout = ""; Stderr = "No audio CSV files written" }
        }

        if ($wavResult.ExitCode -ne 0) {
            $message = "$message wav-error: $($wavResult.Stderr)"
        }

        $wavFrames = Get-WavFramesFromOutput $wavResult.Stdout
        $wavSeconds = Get-WavSecondsFromOutput $wavResult.Stdout
        $wavMetrics = Get-WavPcmMetrics $mixedWav
        $signalStatus = Get-AudioSignalStatus $wavMetrics

        $status = if ($result.ExitCode -eq 124) {
            "process-timeout"
        }
        elseif ($result.ExitCode -ne 0) {
            "fail"
        }
        elseif ($observedFrame -lt $stopFrame) {
            "incomplete"
        }
        else {
            "pass"
        }

        $results.Add([pscustomobject]@{
            label = $label
            status = $status
            signalStatus = $signalStatus
            exitCode = $result.ExitCode
            observedFrame = $observedFrame
            cycles = $cycles
            directSamples = $directSamples
            psgSamples = $psgSamples
            framePpm = $framePpm
            directCsv = $directCsv
            psgCsv = $psgCsv
            directSummary = $directSummary
            psgSummary = $psgSummary
            wavFrames = $wavFrames
            wavSeconds = $wavSeconds
            wavPeakPercent = $wavMetrics.PeakPercent
            wavRmsPercent = $wavMetrics.RmsPercent
            wavClippedSamples = $wavMetrics.ClippedSamples
            mixedWav = if (Test-Path -LiteralPath $mixedWav) { $mixedWav } else { "" }
            romPath = $resolvedRom.Path
            message = $message
        })
    }

    $reportCsv = Join-Path $OutputDir "audio-smoke.csv"
    $reportMd = Join-Path $OutputDir "summary.md"
    $results | Export-Csv -LiteralPath $reportCsv -NoTypeInformation
    Write-MarkdownReport -Rows @($results.ToArray()) -Path $reportMd -ManifestPath $Manifest
    Write-Host "Wrote audio smoke report to $reportCsv"
    Write-Host "Wrote audio smoke summary to $reportMd"
}
finally {
    Pop-Location
}
