param(
    [string]$Routes = "docs\gba-longplay-strict-routes.csv",
    [string]$RomRoot = "curated_official_gba",
    [string]$ReferenceRoot = "reference-captures\mgba\longplay",
    [string]$OutputRoot = "",
    [string]$MgbaPath = "",
    [string[]]$Labels = @(),
    [int]$TimeoutSeconds = 1800,
    [int]$FrameStart = 0,
    [int]$FrameEnd = 0,
    [int]$FrameStride = 0,
    [switch]$Force
)

$ErrorActionPreference = "Stop"

function Resolve-RepoPath([string]$Path) {
    if ([IO.Path]::IsPathRooted($Path)) {
        $candidate = $Path
    } else {
        $candidate = Join-Path $repoRoot $Path
    }
    return (Resolve-Path -LiteralPath $candidate).Path
}

function ConvertTo-LuaPath([string]$Path) {
    return $Path.Replace("\", "/")
}

function Quote-ProcessArg([string]$Value) {
    return '"' + $Value.Replace('"', '\"') + '"'
}

function Find-Mgba {
    if (-not [string]::IsNullOrWhiteSpace($MgbaPath)) {
        return Resolve-RepoPath $MgbaPath
    }

    $candidate = Get-ChildItem -LiteralPath (Join-Path $repoRoot ".research\tools\mgba\dev-extracted") -Recurse -Filter "mGBA.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending |
        Select-Object -First 1

    if (-not $candidate) {
        throw "Could not find mGBA.exe. Download/extract a scripting-enabled dev build, or pass -MgbaPath."
    }

    return $candidate.FullName
}

function Get-KeyMask([string]$Keys) {
    if ([string]::IsNullOrWhiteSpace($Keys) -or $Keys.Trim().Equals("none", [StringComparison]::OrdinalIgnoreCase)) {
        return 0
    }

    $bits = @{
        "A" = 0
        "B" = 1
        "Select" = 2
        "Start" = 3
        "Right" = 4
        "Left" = 5
        "Up" = 6
        "Down" = 7
        "R" = 8
        "L" = 9
    }

    $mask = 0
    foreach ($key in $Keys.Split(",", [StringSplitOptions]::RemoveEmptyEntries)) {
        $name = $key.Trim()
        if (-not $bits.ContainsKey($name)) {
            throw "Unknown input key '$name'."
        }
        $mask = $mask -bor (1 -shl $bits[$name])
    }
    return $mask
}

function Add-KeyEvent([System.Collections.Generic.List[object]]$Events, [int]$Frame, [int]$Keys, [ref]$Sequence) {
    $Events.Add([pscustomobject]@{ Frame = $Frame; Keys = $Keys; Sequence = $Sequence.Value }) | Out-Null
    $Sequence.Value++
}

function Read-InputEvents([string]$Path) {
    $events = [System.Collections.Generic.List[object]]::new()
    $cursor = 0
    $sequence = 0

    foreach ($rawLine in Get-Content -LiteralPath $Path) {
        $line = ($rawLine -replace "#.*$", "").Trim()
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        $parts = $line -split "\s+"
        $command = $parts[0].ToLowerInvariant()

        switch ($command) {
            "at" {
                if ($parts.Count -lt 3) { throw "Invalid input line '$line' in $Path." }
                $frame = [int]$parts[1]
                $keys = Get-KeyMask $parts[2]
                $duration = if ($parts.Count -ge 4) { [int]$parts[3] } else { 1 }
                Add-KeyEvent $events $frame $keys ([ref]$sequence)
                if ($duration -gt 0) {
                    Add-KeyEvent $events ($frame + $duration) 0 ([ref]$sequence)
                }
            }
            "tap" {
                if ($parts.Count -lt 2) { throw "Invalid input line '$line' in $Path." }
                $parsedFrame = 0
                $firstIsFrame = [int]::TryParse($parts[1], [ref]$parsedFrame)
                if ($firstIsFrame) {
                    if ($parts.Count -lt 3) { throw "Invalid input line '$line' in $Path." }
                    $frame = $parsedFrame
                    $keysText = $parts[2]
                    $duration = if ($parts.Count -ge 4) { [int]$parts[3] } else { 4 }
                } else {
                    $frame = $cursor
                    $keysText = $parts[1]
                    $duration = if ($parts.Count -ge 3) { [int]$parts[2] } else { 4 }
                    $cursor += $duration
                }
                Add-KeyEvent $events $frame (Get-KeyMask $keysText) ([ref]$sequence)
                Add-KeyEvent $events ($frame + $duration) 0 ([ref]$sequence)
            }
            "press" {
                if ($parts.Count -lt 3) { throw "Invalid input line '$line' in $Path." }
                Add-KeyEvent $events ([int]$parts[1]) (Get-KeyMask $parts[2]) ([ref]$sequence)
            }
            "release" {
                if ($parts.Count -lt 2) { throw "Invalid input line '$line' in $Path." }
                Add-KeyEvent $events ([int]$parts[1]) 0 ([ref]$sequence)
            }
            "wait" {
                if ($parts.Count -lt 2) { throw "Invalid input line '$line' in $Path." }
                $cursor += [int]$parts[1]
            }
            default {
                throw "Unknown input command '$($parts[0])' in $Path."
            }
        }
    }

    return @($events | Sort-Object Frame, Sequence)
}

function Write-CaptureLua(
    [string]$Path,
    [object[]]$Events,
    [int]$StopFrame,
    [string]$ImagePath,
    [string]$LogPath,
    [string]$SavePath
) {
    $eventLines = foreach ($event in $Events) {
        "  { frame = $($event.Frame), keys = $($event.Keys) },"
    }

    $saveLua = if ([string]::IsNullOrWhiteSpace($SavePath)) {
        ""
    } else {
        "local save_path = [=[$(ConvertTo-LuaPath $SavePath)]=]"
    }

    $lua = @(
        "local stop_frame = $StopFrame"
        "local image_path = [=[$(ConvertTo-LuaPath $ImagePath)]=]"
        "local log_path = [=[$(ConvertTo-LuaPath $LogPath)]=]"
        $saveLua
        "local events = {"
        $eventLines
        "}"
        "local event_index = 1"
        "local current_keys = 0"
        "local captured = false"
        "local save_loaded = false"
        ""
        "local function log(message)"
        "  local f = io.open(log_path, 'a')"
        "  if f then f:write(message .. '\n'); f:close() end"
        "end"
        ""
        "if save_path then"
        "  save_loaded = emu:loadSaveFile(save_path, true)"
        "  log('loadSaveFile=' .. tostring(save_loaded))"
        "  if save_loaded then"
        "    emu:reset()"
        "    log('resetAfterSaveLoad=true')"
        "  end"
        "end"
        ""
        "callbacks:add('start', function()"
        "  log('start')"
        "end)"
        ""
        "callbacks:add('keysRead', function()"
        "  local frame = emu:currentFrame()"
        "  while event_index <= #events and events[event_index].frame <= frame do"
        "    current_keys = events[event_index].keys"
        "    event_index = event_index + 1"
        "  end"
        "  emu:setKeys(current_keys)"
        "end)"
        ""
        "callbacks:add('frame', function()"
        "  local frame = emu:currentFrame()"
        "  if (not captured) and frame >= stop_frame then"
        "    captured = true"
        "    log('capture frame ' .. tostring(frame))"
        "    emu:screenshot(image_path)"
        "    os.exit(0)"
        "  end"
        "end)"
    ) | Where-Object { $_ -ne $null }

    [IO.File]::WriteAllLines($Path, $lua, [Text.Encoding]::ASCII)
}

function Write-WindowCaptureLua(
    [string]$Path,
    [object[]]$Events,
    [object[]]$Frames,
    [string]$LogPath,
    [string]$SavePath
) {
    $eventLines = foreach ($event in $Events) {
        "  { frame = $($event.Frame), keys = $($event.Keys) },"
    }
    $frameLines = foreach ($frame in $Frames) {
        "  { frame = $($frame.Frame), image = [=[$(ConvertTo-LuaPath $frame.ImagePath)]=] },"
    }

    $saveLua = if ([string]::IsNullOrWhiteSpace($SavePath)) {
        ""
    } else {
        "local save_path = [=[$(ConvertTo-LuaPath $SavePath)]=]"
    }

    $lua = @(
        "local log_path = [=[$(ConvertTo-LuaPath $LogPath)]=]"
        $saveLua
        "local events = {"
        $eventLines
        "}"
        "local captures = {"
        $frameLines
        "}"
        "local event_index = 1"
        "local capture_index = 1"
        "local current_keys = 0"
        "local save_loaded = false"
        ""
        "local function log(message)"
        "  local f = io.open(log_path, 'a')"
        "  if f then f:write(message .. '\n'); f:close() end"
        "end"
        ""
        "if save_path then"
        "  save_loaded = emu:loadSaveFile(save_path, true)"
        "  log('loadSaveFile=' .. tostring(save_loaded))"
        "  if save_loaded then"
        "    emu:reset()"
        "    log('resetAfterSaveLoad=true')"
        "  end"
        "end"
        ""
        "callbacks:add('keysRead', function()"
        "  local frame = emu:currentFrame()"
        "  while event_index <= #events and events[event_index].frame <= frame do"
        "    current_keys = events[event_index].keys"
        "    event_index = event_index + 1"
        "  end"
        "  emu:setKeys(current_keys)"
        "end)"
        ""
        "callbacks:add('frame', function()"
        "  local frame = emu:currentFrame()"
        "  while capture_index <= #captures and frame >= captures[capture_index].frame do"
        "    log('capture frame ' .. tostring(frame) .. ' target ' .. tostring(captures[capture_index].frame))"
        "    emu:screenshot(captures[capture_index].image)"
        "    capture_index = capture_index + 1"
        "  end"
        "  if capture_index > #captures then"
        "    os.exit(0)"
        "  end"
        "end)"
    ) | Where-Object { $_ -ne $null }

    [IO.File]::WriteAllLines($Path, $lua, [Text.Encoding]::ASCII)
}

function Resolve-RomPath([object]$Row, [object[]]$Roms) {
    if (-not [string]::IsNullOrWhiteSpace($Row.romPath)) {
        return Resolve-RepoPath $Row.romPath
    }

    $index = [int]$Row.index
    if ($index -lt 1 -or $index -gt $Roms.Count) {
        throw "Route '$($Row.label)' has ROM index $index outside 1..$($Roms.Count)."
    }

    return $Roms[$index - 1].FullName
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Push-Location $repoRoot
try {
    if ($TimeoutSeconds -le 0) {
        throw "TimeoutSeconds must be greater than zero."
    }
    $windowMode = $FrameStride -gt 0 -or $FrameStart -gt 0 -or $FrameEnd -gt 0
    if ($windowMode) {
        if ($FrameStride -le 0) {
            throw "FrameStride must be greater than zero when using FrameStart or FrameEnd."
        }
        if ($FrameEnd -le 0) {
            throw "FrameEnd must be greater than zero in window mode."
        }
        if ($FrameStart -le 0) {
            $FrameStart = $FrameEnd
        }
        if ($FrameStart -gt $FrameEnd) {
            throw "FrameStart must be less than or equal to FrameEnd."
        }
    }

    if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
        $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
        $OutputRoot = "artifacts\mgba-reference-captures-$stamp"
    }

    $mgba = Find-Mgba
    $routeRows = @(Import-Csv -LiteralPath (Resolve-RepoPath $Routes))
    if ($Labels.Count -gt 0) {
        $wanted = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($label in $Labels) {
            [void]$wanted.Add($label)
        }
        $routeRows = @($routeRows | Where-Object { $wanted.Contains($_.label) })
    }
    if ($routeRows.Count -eq 0) {
        throw "No routes selected from $Routes."
    }

    $roms = @(Get-ChildItem -LiteralPath (Resolve-RepoPath $RomRoot) -Recurse -Filter "*.gba" | Sort-Object FullName)
    if ([IO.Path]::IsPathRooted($OutputRoot)) {
        $outputDir = $OutputRoot
    } else {
        $outputDir = Join-Path $repoRoot $OutputRoot
    }

    if ([IO.Path]::IsPathRooted($ReferenceRoot)) {
        $referenceDir = $ReferenceRoot
    } else {
        $referenceDir = Join-Path $repoRoot $ReferenceRoot
    }
    $scriptDir = Join-Path $outputDir "lua"
    New-Item -ItemType Directory -Force -Path $outputDir, $referenceDir, $scriptDir | Out-Null

    $report = [System.Collections.Generic.List[object]]::new()
    foreach ($row in $routeRows) {
        $label = $row.label
        $imagePath = Join-Path $referenceDir "$label.png"
        $luaPath = Join-Path $scriptDir "$label.lua"
        $stdoutPath = Join-Path $outputDir "$label.stdout.txt"
        $stderrPath = Join-Path $outputDir "$label.stderr.txt"
        $logPath = Join-Path $outputDir "$label.lua.txt"

        if (-not $windowMode -and (Test-Path -LiteralPath $imagePath) -and -not $Force) {
            Write-Host "Skipping $label; reference already exists."
            $report.Add([pscustomobject]@{
                label = $label
                status = "skipped"
                exitCode = ""
                timedOut = "false"
                frame = $row.stopFrame
                rom = ""
                referenceImage = $imagePath
                message = "reference already exists"
            }) | Out-Null
            continue
        }

        if (-not $windowMode) {
            Remove-Item -LiteralPath $imagePath -Force -ErrorAction SilentlyContinue
        }
        Remove-Item -LiteralPath $stdoutPath, $stderrPath, $logPath -Force -ErrorAction SilentlyContinue

        $romPath = Resolve-RomPath $row $roms
        $inputPath = Resolve-RepoPath $row.inputScript
        $savePath = ""
        if (-not [string]::IsNullOrWhiteSpace($row.saveFile)) {
            $savePath = Resolve-RepoPath $row.saveFile
        }

        $events = Read-InputEvents $inputPath
        if ($windowMode) {
            $frameDir = Join-Path (Join-Path $outputDir "frames") $label
            New-Item -ItemType Directory -Force -Path $frameDir | Out-Null
            Remove-Item -LiteralPath (Join-Path $frameDir "*.png") -Force -ErrorAction SilentlyContinue
            $captures = [System.Collections.Generic.List[object]]::new()
            for ($frame = $FrameStart; $frame -le $FrameEnd; $frame += $FrameStride) {
                $capturePath = Join-Path $frameDir ("{0}-f-{1:D5}.png" -f $label, $frame)
                $captures.Add([pscustomobject]@{ Frame = $frame; ImagePath = $capturePath }) | Out-Null
            }
            Write-WindowCaptureLua $luaPath $events @($captures) $logPath $savePath
        } else {
            Write-CaptureLua $luaPath $events ([int]$row.stopFrame) $imagePath $logPath $savePath
        }

        if ($windowMode) {
            Write-Host "Capturing $label mGBA window frames $FrameStart..$FrameEnd stride $FrameStride"
        } else {
            Write-Host "Capturing $label through frame $($row.stopFrame)"
        }
        $args = @(
            "-1",
            "-C", "autoload=false",
            "-C", "useBios=false",
            "-C", "skipBios=true",
            "-C", "audioSync=false",
            "-C", "videoSync=false",
            "-C", "interframeBlending=false",
            "-C", "shader=",
            "-C", "fpsTarget=10000",
            "--script", $luaPath,
            $romPath
        )
        $argumentString = ($args | ForEach-Object { Quote-ProcessArg $_ }) -join " "

        $process = Start-Process -FilePath $mgba -ArgumentList $argumentString -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
        $timedOut = -not $process.WaitForExit($TimeoutSeconds * 1000)
        if ($timedOut) {
            $process.Kill()
            $process.WaitForExit()
        }
        $process.Refresh()
        $exitCode = try { $process.ExitCode } catch { "" }

        if ($windowMode) {
            $capturedCount = @(Get-ChildItem -LiteralPath $frameDir -Filter "*.png" -ErrorAction SilentlyContinue).Count
            $expectedCount = $captures.Count
            $exists = $capturedCount -eq $expectedCount
            $status = if ($timedOut) { "timeout" } elseif ($exists) { "captured" } else { "partial" }
            $message = "$capturedCount/$expectedCount window frames"
            $reportedImage = $frameDir
            $reportedFrame = "$FrameStart-$FrameEnd/$FrameStride"
        } else {
            $exists = Test-Path -LiteralPath $imagePath
            $status = if ($timedOut) { "timeout" } elseif ($exists) { "captured" } else { "failed" }
            $message = if ($exists) { "ok" } else { "reference image missing" }
            $reportedImage = $imagePath
            $reportedFrame = $row.stopFrame
        }

        $report.Add([pscustomobject]@{
            label = $label
            status = $status
            exitCode = $exitCode
            timedOut = $timedOut.ToString().ToLowerInvariant()
            frame = $reportedFrame
            rom = $romPath
            referenceImage = $reportedImage
            message = $message
        }) | Out-Null
    }

    $reportPath = Join-Path $outputDir "mgba-reference-captures.csv"
    $report | Export-Csv -LiteralPath $reportPath -NoTypeInformation
    $report | Group-Object status | Sort-Object Name | ForEach-Object {
        Write-Host ("{0}: {1}" -f $_.Name, $_.Count)
    }
    Write-Host "Report: $reportPath"
}
finally {
    Pop-Location
}
