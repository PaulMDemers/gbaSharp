param(
    [string]$Manifest = "docs\gba-visual-snapshots.csv",
    [string]$RomRoot = "gba_collection",
    [string]$OutputDir = "visual-saves",
    [int]$MaxItems = 0,
    [int]$SkipItems = 0,
    [int]$StopFrame = 1,
    [int]$MaxSteps = 5000000,
    [int]$ProcessTimeoutSeconds = 300,
    [switch]$Overwrite
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
        if ($process.ExitCode -ne 0 -and $process.ExitCode -ne 5) {
            throw "$Description failed with exit code $($process.ExitCode): $stdout $stderr"
        }

        return (($stdout + " " + $stderr) -replace '\s+', ' ').Trim()
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
        Write-Host "Generating visual save fixtures at BelowNormal process priority."
    }
    catch {
        Write-Warning "Could not lower process priority: $($_.Exception.Message)"
    }

    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
    $roms = @(Get-ChildItem -Path $RomRoot -Recurse -Filter *.gba | Sort-Object FullName)
    $items = @(Import-Csv $Manifest | Group-Object index | ForEach-Object { $_.Group[0] })
    if ($SkipItems -gt 0) {
        $items = @($items | Select-Object -Skip $SkipItems)
    }

    if ($MaxItems -gt 0) {
        $items = @($items | Select-Object -First $MaxItems)
    }

    $reportPath = Join-Path $OutputDir "visual-save-fixtures.csv"
    "label,index,status,size,path,message" | Set-Content -LiteralPath $reportPath -Encoding UTF8

    foreach ($item in $items) {
        $label = Get-SafeName $item.label
        $index = [int]$item.index
        if ($index -le 0 -or $index -gt $roms.Count) {
            throw "Index $index for $label is outside ROM collection size $($roms.Count)."
        }

        $baseLabel = ($label -replace '-title$', '') -replace '-scripted$', ''
        $savePath = Join-Path $OutputDir "$baseLabel.sav"
        if ((Test-Path $savePath) -and -not $Overwrite) {
            $size = (Get-Item $savePath).Length
            Write-Host "Keeping existing save fixture $savePath"
            [pscustomobject]@{ label = $baseLabel; index = $index; status = "kept"; size = $size; path = $savePath; message = "" } | Export-Csv -LiteralPath $reportPath -NoTypeInformation -Append
            continue
        }

        $rom = $roms[$index - 1].FullName
        $args = @(
            "run", "--project", "src\Gba.Cli", "--no-build", "--",
            "run", $rom,
            "--stop-frame", "$StopFrame",
            "--max-steps", "$MaxSteps",
            "--save-file", $savePath
        )

        Write-Host "Creating save fixture $baseLabel (#$index)"
        $message = Invoke-DotnetChecked -Arguments $args -TimeoutSeconds $ProcessTimeoutSeconds -Description "Save fixture $baseLabel"
        $size = if (Test-Path $savePath) { (Get-Item $savePath).Length } else { 0 }
        $status = if ($size -gt 0) { "created" } else { "no-save" }
        [pscustomobject]@{ label = $baseLabel; index = $index; status = $status; size = $size; path = $savePath; message = $message } | Export-Csv -LiteralPath $reportPath -NoTypeInformation -Append
    }

    Write-Host "Visual save fixture report: $((Resolve-Path $reportPath).Path)"
}
finally {
    Pop-Location
}
