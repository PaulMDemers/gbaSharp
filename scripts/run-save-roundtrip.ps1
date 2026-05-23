param(
    [string]$Manifest = "docs\gba-save-roundtrip.csv",
    [string]$RomRoot = "gba_collection",
    [string]$BaselineDir = "visual-baselines",
    [string]$OutputDir = "",
    [string]$Configuration = "Release",
    [int]$MaxItems = 0,
    [int]$SkipItems = 0,
    [int]$ProcessTimeoutSeconds = 1200,
    [switch]$UpdateBaselines,
    [switch]$RequireProgress,
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
        [int[]]$AllowedExitCodes,
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
        if ($AllowedExitCodes -notcontains $process.ExitCode) {
            throw "$Description failed with exit code $($process.ExitCode): $stdout $stderr"
        }

        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            Message = (($stdout + " " + $stderr) -replace '\s+', ' ').Trim()
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

function Get-SaveStats {
    param([string]$Path)

    if (-not (Test-Path $Path)) {
        return [pscustomobject]@{
            Size = 0
            ChangedBytes = 0
            ZeroBytes = 0
            ErasedBytes = 0
            UniqueBytes = 0
            Sha256 = ""
        }
    }

    $bytes = [System.IO.File]::ReadAllBytes((Resolve-Path $Path).Path)
    $changed = 0
    $zero = 0
    $erased = 0
    $seen = New-Object 'bool[]' 256
    foreach ($byte in $bytes) {
        if ($byte -ne 0xFF) { $changed++ }
        if ($byte -eq 0) { $zero++ }
        if ($byte -eq 0xFF) { $erased++ }
        $seen[$byte] = $true
    }

    $unique = 0
    foreach ($value in $seen) {
        if ($value) { $unique++ }
    }

    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $sha = $sha256.ComputeHash($bytes)
        $shaText = [BitConverter]::ToString($sha).Replace("-", "").ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }

    return [pscustomobject]@{
        Size = $bytes.Length
        ChangedBytes = $changed
        ZeroBytes = $zero
        ErasedBytes = $erased
        UniqueBytes = $unique
        Sha256 = $shaText
    }
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $repoRoot
try {
    try {
        (Get-Process -Id $PID).PriorityClass = "BelowNormal"
        Write-Host "Running save roundtrip sweep at BelowNormal process priority."
    }
    catch {
        Write-Warning "Could not lower process priority: $($_.Exception.Message)"
    }

    if ([string]::IsNullOrWhiteSpace($OutputDir)) {
        $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
        $OutputDir = "save-roundtrip-$stamp"
    }

    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
    New-Item -ItemType Directory -Force -Path $BaselineDir | Out-Null
    $saveDir = Join-Path $OutputDir "saves"
    $actualDir = Join-Path $OutputDir "actual"
    $diffDir = Join-Path $OutputDir "diff"
    New-Item -ItemType Directory -Force -Path $saveDir | Out-Null
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

    $reportPath = Join-Path $OutputDir "save-roundtrip.csv"
    if (-not ($Resume -and (Test-Path $reportPath))) {
        "label,index,saveType,status,visualStatus,saveSize,changedBytes,zeroBytes,erasedBytes,uniqueBytes,saveHash,createExitCode,verifyExitCode,savePath,baseline,actual,diff,expectedScene,message" | Set-Content -LiteralPath $reportPath -Encoding UTF8
    }

    foreach ($item in $items) {
        $label = Get-SafeName $item.label
        $index = if ([string]::IsNullOrWhiteSpace($item.index)) { 0 } else { [int]$item.index }
        if (-not [string]::IsNullOrWhiteSpace($item.romPath)) {
            $rom = (Resolve-Path $item.romPath).Path
        }
        else {
            if ($index -le 0 -or $index -gt $roms.Count) {
                throw "Index $index for $label is outside ROM collection size $($roms.Count)."
            }

            $rom = $roms[$index - 1].FullName
        }
        $savePath = Join-Path $saveDir "$label.sav"
        $baseline = Join-Path $BaselineDir "$label.ppm"
        $actual = Join-Path $actualDir "$label.ppm"
        $diff = Join-Path $diffDir "$label.ppm"
        if ($Resume -and (Test-Path $actual)) {
            Write-Host "Skipping existing roundtrip $label"
            continue
        }

        $createArgs = @(
            "run", "--project", "src\Gba.Cli", "--configuration", $Configuration, "--no-build", "--",
            "run", $rom,
            "--stop-frame", "$($item.createStopFrame)",
            "--max-steps", "$($item.createMaxSteps)",
            "--save-file", $savePath
        )
        if (-not [string]::IsNullOrWhiteSpace($item.createInputScript)) {
            $createArgs += @("--input-script", $item.createInputScript)
        }

        Write-Host "Creating save $label (#$index)"
        $create = Invoke-DotnetChecked -Arguments $createArgs -TimeoutSeconds $ProcessTimeoutSeconds -AllowedExitCodes @(0) -Description "Create save $label"
        $stats = Get-SaveStats $savePath
        $progressed = $stats.ChangedBytes -gt 0

        $verifyArgs = @(
            "run", "--project", "src\Gba.Cli", "--configuration", $Configuration, "--no-build", "--",
            "verify-frame", $rom,
            "--stop-frame", "$($item.verifyStopFrame)",
            "--max-steps", "$($item.verifyMaxSteps)",
            "--save-file", $savePath,
            "--save-read-only",
            "--baseline", $baseline,
            "--actual", $actual,
            "--diff", $diff,
            "--max-different-pixels", "$($item.maxDifferentPixels)",
            "--max-channel-delta", "$($item.maxChannelDelta)"
        )
        if ($UpdateBaselines) {
            $verifyArgs += "--write-baseline"
        }

        if (-not [string]::IsNullOrWhiteSpace($item.verifyInputScript)) {
            $verifyArgs += @("--input-script", $item.verifyInputScript)
        }

        Write-Host "Verifying loaded save $label"
        $verify = Invoke-DotnetChecked -Arguments $verifyArgs -TimeoutSeconds $ProcessTimeoutSeconds -AllowedExitCodes @(0, 4) -Description "Verify save $label"
        $visualStatus = if ($verify.ExitCode -eq 0) { "pass" } else { "diff" }
        $status = if ($progressed -and $visualStatus -eq "pass") {
            "progressed-pass"
        }
        elseif ($progressed) {
            "progressed-$visualStatus"
        }
        elseif ($visualStatus -eq "pass") {
            "no-progress-pass"
        }
        else {
            "no-progress-$visualStatus"
        }

        [pscustomobject]@{
            label = $label
            index = $index
            saveType = $item.saveType
            status = $status
            visualStatus = $visualStatus
            saveSize = $stats.Size
            changedBytes = $stats.ChangedBytes
            zeroBytes = $stats.ZeroBytes
            erasedBytes = $stats.ErasedBytes
            uniqueBytes = $stats.UniqueBytes
            saveHash = $stats.Sha256
            createExitCode = $create.ExitCode
            verifyExitCode = $verify.ExitCode
            savePath = $savePath
            baseline = $baseline
            actual = $actual
            diff = $diff
            expectedScene = $item.expectedScene
            message = ($create.Message + " | " + $verify.Message)
        } | Export-Csv -LiteralPath $reportPath -NoTypeInformation -Append

        Write-Host "  $status changed=$($stats.ChangedBytes)/$($stats.Size)"
        if ($RequireProgress -and -not $progressed) {
            throw "Save roundtrip $label did not produce non-erased save data."
        }
    }

    Write-Host "Save roundtrip report: $((Resolve-Path $reportPath).Path)"
}
finally {
    Pop-Location
}
