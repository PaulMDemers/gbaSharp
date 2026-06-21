param(
    [ValidateSet('status', 'ruby-state', 'screenshot', 'press', 'release', 'set', 'clear', 'tap', 'face', 'tile-step', 'warp-tap', 'walk-tile', 'sequence', 'run', 'pause', 'toggle', 'reset', 'step', 'close')]
    [string]$Command = 'status',

    [string]$Keys = '',

    [string]$Sequence = '',

    [string]$OutFile = '',

    [ValidateSet('', 'movement-grid', 'grid', 'center-lens', 'lens', 'coordinate-lens', 'atlas-grid', 'atlas-lens', 'atlas-coordinate-lens')]
    [string]$Overlay = '',

    [string]$AtlasPath = '',

    [int]$Duration = 90,

    [int]$Gap = 120,

    [int]$Timeout = 900,

    [int]$Scale = 4,

    [int]$Tiles = 9,

    [string]$BaseUrl = ''
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($BaseUrl)) {
    $discoveryPath = Join-Path $env:TEMP 'gbaSharp-control.json'
    if (!(Test-Path -LiteralPath $discoveryPath)) {
        throw "Control discovery file not found: $discoveryPath"
    }

    $BaseUrl = (Get-Content -LiteralPath $discoveryPath -Raw | ConvertFrom-Json).baseUrl
}

$BaseUrl = $BaseUrl.TrimEnd('/')

function Invoke-ControlPost([string]$Path) {
    Invoke-RestMethod -Method Post "$BaseUrl$Path"
}

function Save-ControlScreenshot([string]$Url, [string]$Path) {
    $directory = Split-Path -Parent $Path
    if (![string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    try {
        if ($curl -ne $null) {
            & $curl.Source --fail --silent --show-error --max-time 10 --output $Path $Url
            if ($LASTEXITCODE -ne 0) {
                throw "curl.exe exited with code $LASTEXITCODE."
            }
        }
        else {
            Invoke-WebRequest $Url -OutFile $Path -TimeoutSec 10 | Out-Null
        }

        $item = Get-Item -LiteralPath $Path
        if ($item.Length -le 0) {
            throw "Downloaded screenshot is empty: $Path"
        }
    }
    catch {
        if (Test-Path -LiteralPath $Path) {
            Remove-Item -LiteralPath $Path -Force
        }

        throw
    }
}

function Add-MovementGrid([System.Drawing.Bitmap]$Bitmap, [int]$ScaleFactor, [bool]$DenseCoordinates = $false) {
    $centerX = [int]($Bitmap.Width / 2)
    $centerY = [int]($Bitmap.Height / 2)
    $tileSize = 16 * $ScaleFactor
    $tileLeft = $centerX - [int]($tileSize / 2)
    $tileTop = $centerY - [int]($tileSize / 2)
    $graphics = [System.Drawing.Graphics]::FromImage($Bitmap)
    try {
        $gridPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(140, 255, 255, 255), [Math]::Max(1, [int]($ScaleFactor / 2)))
        $centerPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(230, 255, 235, 59), [Math]::Max(1, $ScaleFactor))
        $adjacentPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(210, 0, 229, 255), [Math]::Max(1, $ScaleFactor))
        $coordinatePen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(150, 255, 255, 255), [Math]::Max(1, [int]($ScaleFactor / 2)))
        $crossPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(220, 255, 64, 129), [Math]::Max(1, $ScaleFactor))
        $font = [System.Drawing.Font]::new([System.Drawing.SystemFonts]::DefaultFont.FontFamily, [Math]::Max(8, 8 * $ScaleFactor), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        $coordinateFont = [System.Drawing.Font]::new([System.Drawing.SystemFonts]::DefaultFont.FontFamily, [Math]::Max(7, 5 * $ScaleFactor), [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
        $labelBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(235, 255, 255, 255))
        $shadowBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(200, 0, 0, 0))
        try {
            for ($x = (($tileLeft % $tileSize + $tileSize) % $tileSize) - $tileSize; $x -le $Bitmap.Width; $x += $tileSize) {
                $graphics.DrawLine($gridPen, $x, 0, $x, $Bitmap.Height)
            }
            for ($y = (($tileTop % $tileSize + $tileSize) % $tileSize) - $tileSize; $y -le $Bitmap.Height; $y += $tileSize) {
                $graphics.DrawLine($gridPen, 0, $y, $Bitmap.Width, $y)
            }

            foreach ($tile in @(
                @($tileLeft, ($tileTop - $tileSize), 'U', $adjacentPen),
                @(($tileLeft + $tileSize), $tileTop, 'R', $adjacentPen),
                @($tileLeft, ($tileTop + $tileSize), 'D', $adjacentPen),
                @(($tileLeft - $tileSize), $tileTop, 'L', $adjacentPen),
                @($tileLeft, $tileTop, 'C', $centerPen)
            )) {
                $graphics.DrawRectangle($tile[3], [int]$tile[0], [int]$tile[1], $tileSize, $tileSize)
                $labelX = [float]($tile[0] + ($tileSize / 2) - ($font.Size / 3))
                $labelY = [float]($tile[1] + ($tileSize / 2) - ($font.Size / 2))
                $graphics.DrawString($tile[2], $font, $shadowBrush, $labelX + 1, $labelY + 1)
                $graphics.DrawString($tile[2], $font, $labelBrush, $labelX, $labelY)
            }

            if ($ScaleFactor -gt 1) {
                for ($distance = 2; $distance -le 4; $distance++) {
                    foreach ($tile in @(
                        @($tileLeft, ($tileTop - $tileSize * $distance)),
                        @(($tileLeft + $tileSize * $distance), $tileTop),
                        @($tileLeft, ($tileTop + $tileSize * $distance)),
                        @(($tileLeft - $tileSize * $distance), $tileTop)
                    )) {
                        $graphics.DrawRectangle($coordinatePen, [int]$tile[0], [int]$tile[1], $tileSize, $tileSize)
                    }
                }

                $minDx = [Math]::Floor((0 - $tileLeft) / [double]$tileSize)
                $maxDx = [Math]::Ceiling(($Bitmap.Width - $tileLeft) / [double]$tileSize) - 1
                $minDy = [Math]::Floor((0 - $tileTop) / [double]$tileSize)
                $maxDy = [Math]::Ceiling(($Bitmap.Height - $tileTop) / [double]$tileSize) - 1
                for ($dy = $minDy; $dy -le $maxDy; $dy++) {
                    for ($dx = $minDx; $dx -le $maxDx; $dx++) {
                        if ($dx -eq 0 -and $dy -eq 0) { continue }
                        if (!$DenseCoordinates -and $dx -ne 0 -and $dy -ne 0) { continue }
                        $x = $tileLeft + $dx * $tileSize
                        $y = $tileTop + $dy * $tileSize
                        if ($x + $tileSize -lt 0 -or $y + $tileSize -lt 0 -or $x -gt $Bitmap.Width -or $y -gt $Bitmap.Height) { continue }
                        $dxText = if ($dx -gt 0) { "+$dx" } else { "$dx" }
                        $dyText = if ($dy -gt 0) { "+$dy" } else { "$dy" }
                        $label = "$dxText,$dyText"
                        $graphics.DrawString($label, $coordinateFont, $shadowBrush, [float]($x + 3), [float]($y + 3))
                        $graphics.DrawString($label, $coordinateFont, $labelBrush, [float]($x + 2), [float]($y + 2))
                    }
                }
            }

            $graphics.DrawLine($crossPen, $centerX - $tileSize, $centerY, $centerX + $tileSize, $centerY)
            $graphics.DrawLine($crossPen, $centerX, $centerY - $tileSize, $centerX, $centerY + $tileSize)
        }
        finally {
            $gridPen.Dispose()
            $centerPen.Dispose()
            $adjacentPen.Dispose()
            $coordinatePen.Dispose()
            $crossPen.Dispose()
            $font.Dispose()
            $coordinateFont.Dispose()
            $labelBrush.Dispose()
            $shadowBrush.Dispose()
        }
    }
    finally {
        $graphics.Dispose()
    }
}

function Get-DefaultAtlasPath {
    Join-Path (Get-Location) 'docs\live-atlas\pokemon-ruby.csv'
}

function Get-AtlasColor([string]$TypeName) {
    switch ($TypeName.Trim().ToLowerInvariant()) {
        { $_ -in @('blocker', 'wall', 'tree', 'water', 'counter') } { return [System.Drawing.Color]::FromArgb(244, 67, 54) }
        { $_ -in @('door', 'warp', 'stairs') } { return [System.Drawing.Color]::FromArgb(33, 150, 243) }
        { $_ -in @('interactable', 'sign', 'npc', 'object') } { return [System.Drawing.Color]::FromArgb(255, 152, 0) }
        'ledge' { return [System.Drawing.Color]::FromArgb(156, 39, 176) }
        { $_ -in @('grass', 'passable', 'path') } { return [System.Drawing.Color]::FromArgb(76, 175, 80) }
        default { return [System.Drawing.Color]::FromArgb(255, 235, 59) }
    }
}

function Get-AtlasCompactLabel([string]$Label, [string]$TypeName) {
    $resolved = if ([string]::IsNullOrWhiteSpace($Label)) { $TypeName } else { $Label }
    if ([string]::IsNullOrWhiteSpace($resolved)) {
        return ''
    }

    $parts = $resolved -split '-'
    if ($parts.Count -gt 0) {
        return $parts[$parts.Count - 1]
    }

    $resolved
}

function Read-TileAtlas([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        $Path = Get-DefaultAtlasPath
    }

    if (!(Test-Path -LiteralPath $Path)) {
        return @()
    }

    @(Import-Csv -LiteralPath $Path | Where-Object {
        $_.dx -match '^-?\d+$' -and $_.dy -match '^-?\d+$'
    })
}

function Try-GetAtlasRelativeTile($Entry, $RubyState, [ref]$Dx, [ref]$Dy) {
    $Dx.Value = [int]$Entry.dx
    $Dy.Value = [int]$Entry.dy
    if ($Entry.x -notmatch '^-?\d+$' -or $Entry.y -notmatch '^-?\d+$') {
        return $true
    }

    if ($RubyState -eq $null -or $RubyState.saveBlockPlayer -eq $null) {
        return $true
    }

    $entryMapId = [string]$Entry.mapId
    if (![string]::IsNullOrWhiteSpace($entryMapId) -and $entryMapId -ne $RubyState.saveBlockPlayer.mapId) {
        return $false
    }

    $Dx.Value = [int]$Entry.x - [int]$RubyState.saveBlockPlayer.x
    $Dy.Value = [int]$Entry.y - [int]$RubyState.saveBlockPlayer.y
    return $true
}

function Try-GetAtlasRelativeStandTile($Entry, $RubyState, [ref]$Dx, [ref]$Dy) {
    $Dx.Value = 0
    $Dy.Value = 0
    if ($Entry.standX -notmatch '^-?\d+$' -or $Entry.standY -notmatch '^-?\d+$') {
        return $false
    }

    if ($RubyState -eq $null -or $RubyState.saveBlockPlayer -eq $null) {
        return $false
    }

    $entryMapId = [string]$Entry.mapId
    if (![string]::IsNullOrWhiteSpace($entryMapId) -and $entryMapId -ne $RubyState.saveBlockPlayer.mapId) {
        return $false
    }

    $Dx.Value = [int]$Entry.standX - [int]$RubyState.saveBlockPlayer.x
    $Dy.Value = [int]$Entry.standY - [int]$RubyState.saveBlockPlayer.y
    return $true
}

function Add-TileAtlasOverlay([System.Drawing.Bitmap]$Bitmap, [int]$ScaleFactor, [string]$Path, $RubyState) {
    $entries = Read-TileAtlas $Path
    if ($entries.Count -eq 0) {
        return
    }

    $centerX = [int]($Bitmap.Width / 2)
    $centerY = [int]($Bitmap.Height / 2)
    $tileSize = 16 * $ScaleFactor
    $tileLeft = $centerX - [int]($tileSize / 2)
    $tileTop = $centerY - [int]($tileSize / 2)
    $graphics = [System.Drawing.Graphics]::FromImage($Bitmap)
    try {
        $font = [System.Drawing.Font]::new([System.Drawing.SystemFonts]::DefaultFont.FontFamily, [Math]::Max(7, 5 * $ScaleFactor), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        $actionFont = [System.Drawing.Font]::new([System.Drawing.SystemFonts]::DefaultFont.FontFamily, [Math]::Max(7, 4 * $ScaleFactor), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        $labelBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(245, 255, 255, 255))
        $shadowBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(220, 0, 0, 0))
        $actionBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(245, 232, 255, 232))
        $actionFillBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(50, 0, 200, 83))
        $actionBorderPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(245, 0, 200, 83), [Math]::Max(2, $ScaleFactor))
        try {
            foreach ($entry in $entries) {
                $dx = 0
                $dy = 0
                if (!(Try-GetAtlasRelativeTile $entry $RubyState ([ref]$dx) ([ref]$dy))) { continue }
                $widthTiles = if ($entry.width -match '^\d+$') { [Math]::Max(1, [int]$entry.width) } else { 1 }
                $heightTiles = if ($entry.height -match '^\d+$') { [Math]::Max(1, [int]$entry.height) } else { 1 }
                $x = $tileLeft + $dx * $tileSize
                $y = $tileTop + $dy * $tileSize
                $width = $widthTiles * $tileSize
                $height = $heightTiles * $tileSize
                if ($x + $width -lt 0 -or $y + $height -lt 0 -or $x -gt $Bitmap.Width -or $y -gt $Bitmap.Height) { continue }

                $color = Get-AtlasColor $entry.type
                $fillBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(72, [int]$color.R, [int]$color.G, [int]$color.B))
                $borderPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(235, [int]$color.R, [int]$color.G, [int]$color.B), [Math]::Max(1, $ScaleFactor))
                try {
                    $graphics.FillRectangle($fillBrush, $x, $y, $width, $height)
                    $graphics.DrawRectangle($borderPen, $x, $y, $width, $height)
                    $label = Get-AtlasCompactLabel $entry.label $entry.type
                    if (![string]::IsNullOrWhiteSpace($label) -and $ScaleFactor -gt 1) {
                        $graphics.DrawString($label, $font, $shadowBrush, [float]($x + 4), [float]($y + 4))
                        $graphics.DrawString($label, $font, $labelBrush, [float]($x + 3), [float]($y + 3))
                    }
                }
                finally {
                    $fillBrush.Dispose()
                    $borderPen.Dispose()
                }

                $standDx = 0
                $standDy = 0
                if (Try-GetAtlasRelativeStandTile $entry $RubyState ([ref]$standDx) ([ref]$standDy)) {
                    $standX = $tileLeft + $standDx * $tileSize
                    $standY = $tileTop + $standDy * $tileSize
                    if (!($standX + $tileSize -lt 0 -or $standY + $tileSize -lt 0 -or $standX -gt $Bitmap.Width -or $standY -gt $Bitmap.Height)) {
                        $graphics.FillRectangle($actionFillBrush, $standX, $standY, $tileSize, $tileSize)
                        $graphics.DrawRectangle($actionBorderPen, $standX, $standY, $tileSize, $tileSize)
                        $action = if ([string]::IsNullOrWhiteSpace($entry.action)) { 'stand' } else { $entry.action }
                        if ($ScaleFactor -gt 1) {
                            $graphics.DrawString($action, $actionFont, $shadowBrush, [float]($standX + 4), [float]($standY + $tileSize - (5 * $ScaleFactor) - 3))
                            $graphics.DrawString($action, $actionFont, $actionBrush, [float]($standX + 3), [float]($standY + $tileSize - (5 * $ScaleFactor) - 4))
                        }
                    }
                }
            }
        }
        finally {
            $font.Dispose()
            $actionFont.Dispose()
            $labelBrush.Dispose()
            $shadowBrush.Dispose()
            $actionBrush.Dispose()
            $actionFillBrush.Dispose()
            $actionBorderPen.Dispose()
        }
    }
    finally {
        $graphics.Dispose()
    }
}

function Convert-ScreenshotOverlay([string]$Path, [string]$OverlayName, [int]$ScaleFactor, [int]$TileCount, [string]$AtlasFile, $RubyState) {
    if ([string]::IsNullOrWhiteSpace($OverlayName)) {
        return
    }

    Add-Type -AssemblyName System.Drawing
    $bitmap = [System.Drawing.Bitmap]::new((Resolve-Path -LiteralPath $Path).Path)
    try {
        $output = $bitmap
        $lens = $null
        $isLens = $OverlayName -eq 'center-lens' -or $OverlayName -eq 'lens' -or $OverlayName -eq 'coordinate-lens' -or $OverlayName -eq 'atlas-lens' -or $OverlayName -eq 'atlas-coordinate-lens'
        $isAtlas = $OverlayName -eq 'atlas-grid' -or $OverlayName -eq 'atlas-lens' -or $OverlayName -eq 'atlas-coordinate-lens'
        if ($isLens) {
            if ($TileCount % 2 -eq 0) { $TileCount++ }
            $maxTiles = [Math]::Min(13, [int]([Math]::Min($bitmap.Width, $bitmap.Height) / 16))
            if ($maxTiles % 2 -eq 0) { $maxTiles-- }
            $TileCount = [Math]::Min($maxTiles, [Math]::Max(3, $TileCount))
            $ScaleFactor = [Math]::Min(8, [Math]::Max(1, $ScaleFactor))
            $size = $TileCount * 16
            $left = [Math]::Min($bitmap.Width - $size, [Math]::Max(0, [int]($bitmap.Width / 2) - [int]($size / 2)))
            $top = [Math]::Min($bitmap.Height - $size, [Math]::Max(0, [int]($bitmap.Height / 2) - [int]($size / 2)))
            $lens = [System.Drawing.Bitmap]::new($size * $ScaleFactor, $size * $ScaleFactor, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
            $graphics = [System.Drawing.Graphics]::FromImage($lens)
            try {
                $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
                $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
                $graphics.DrawImage($bitmap, [System.Drawing.Rectangle]::new(0, 0, $lens.Width, $lens.Height), [System.Drawing.Rectangle]::new($left, $top, $size, $size), [System.Drawing.GraphicsUnit]::Pixel)
            }
            finally {
                $graphics.Dispose()
            }
            $output = $lens
        }

        $effectiveScale = $(if ($isLens) { $ScaleFactor } else { 1 })
        Add-MovementGrid $output $effectiveScale ($OverlayName -eq 'coordinate-lens' -or $OverlayName -eq 'atlas-coordinate-lens')
        if ($isAtlas) {
            Add-TileAtlasOverlay $output $effectiveScale $AtlasFile $RubyState
        }

        $tempPath = "$Path.overlay.tmp.png"
        $output.Save($tempPath, [System.Drawing.Imaging.ImageFormat]::Png)
        if ($lens -ne $null) { $lens.Dispose() }
    }
    finally {
        $bitmap.Dispose()
    }

    Move-Item -LiteralPath $tempPath -Destination $Path -Force
}

switch ($Command) {
    'status' {
        Invoke-RestMethod "$BaseUrl/status"
    }
    'ruby-state' {
        Invoke-RestMethod "$BaseUrl/game/ruby/state"
    }
    'screenshot' {
        if ([string]::IsNullOrWhiteSpace($OutFile)) {
            $artifactRoot = Join-Path (Get-Location) 'artifacts'
            New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
            $OutFile = Join-Path $artifactRoot ("desktop-control-{0:yyyyMMdd-HHmmss}.png" -f (Get-Date))
        }

        Save-ControlScreenshot "$BaseUrl/screenshot" $OutFile
        $rubyState = $null
        if ($Overlay -eq 'atlas-grid' -or $Overlay -eq 'atlas-lens' -or $Overlay -eq 'atlas-coordinate-lens') {
            try {
                $rubyState = Invoke-RestMethod "$BaseUrl/game/ruby/state" -TimeoutSec 5
            }
            catch {
                $rubyState = $null
            }
        }

        Convert-ScreenshotOverlay $OutFile $Overlay $Scale $Tiles $AtlasPath $rubyState
        Get-Item -LiteralPath $OutFile
    }
    'tap' {
        if ([string]::IsNullOrWhiteSpace($Keys)) { throw '-Keys is required for tap.' }
        Invoke-ControlPost "/input/tap?keys=$([uri]::EscapeDataString($Keys))&duration=$Duration&delay=$Gap"
    }
    'face' {
        if ([string]::IsNullOrWhiteSpace($Keys)) { throw '-Keys is required for face.' }
        $faceDuration = if ($Duration -eq 90) { 45 } else { $Duration }
        Invoke-ControlPost "/input/face?key=$([uri]::EscapeDataString($Keys))&duration=$faceDuration&delay=$Gap"
    }
    'tile-step' {
        if ([string]::IsNullOrWhiteSpace($Keys)) { throw '-Keys is required for tile-step.' }
        $stepDuration = if ($Duration -eq 90) { 170 } else { $Duration }
        $stepGap = if ($Gap -eq 120) { 250 } else { $Gap }
        Invoke-ControlPost "/input/tile-step?key=$([uri]::EscapeDataString($Keys))&duration=$stepDuration&delay=$stepGap"
    }
    'warp-tap' {
        if ([string]::IsNullOrWhiteSpace($Keys)) { throw '-Keys is required for warp-tap.' }
        $warpDuration = if ($Duration -eq 90) { 85 } else { $Duration }
        $warpGap = if ($Gap -eq 120) { 1200 } else { $Gap }
        Invoke-ControlPost "/input/tap?keys=$([uri]::EscapeDataString($Keys))&duration=$warpDuration&delay=$warpGap"
    }
    'walk-tile' {
        if ([string]::IsNullOrWhiteSpace($Keys)) { throw '-Keys is required for walk-tile.' }
        $stepDuration = if ($Duration -eq 90) { 170 } else { $Duration }
        Invoke-ControlPost "/input/walk-tile?key=$([uri]::EscapeDataString($Keys))&timeout=$Timeout&duration=$stepDuration&delay=$Gap"
    }
    'sequence' {
        if ([string]::IsNullOrWhiteSpace($Sequence)) { throw '-Sequence is required for sequence.' }
        Invoke-ControlPost "/input/sequence?steps=$([uri]::EscapeDataString($Sequence))&duration=$Duration&gap=$Gap"
    }
    'press' {
        if ([string]::IsNullOrWhiteSpace($Keys)) { throw '-Keys is required for press.' }
        Invoke-ControlPost "/input/press?keys=$([uri]::EscapeDataString($Keys))"
    }
    'release' {
        if ([string]::IsNullOrWhiteSpace($Keys)) { throw '-Keys is required for release.' }
        Invoke-ControlPost "/input/release?keys=$([uri]::EscapeDataString($Keys))"
    }
    'set' {
        if ([string]::IsNullOrWhiteSpace($Keys)) { throw '-Keys is required for set.' }
        Invoke-ControlPost "/input/set?keys=$([uri]::EscapeDataString($Keys))"
    }
    'clear' {
        Invoke-ControlPost '/input/clear'
    }
    'run' {
        Invoke-ControlPost '/emulation/run'
    }
    'pause' {
        Invoke-ControlPost '/emulation/pause'
    }
    'toggle' {
        Invoke-ControlPost '/emulation/toggle'
    }
    'reset' {
        Invoke-ControlPost '/emulation/reset'
    }
    'step' {
        Invoke-ControlPost '/emulation/step'
    }
    'close' {
        Invoke-ControlPost '/app/close'
    }
}
