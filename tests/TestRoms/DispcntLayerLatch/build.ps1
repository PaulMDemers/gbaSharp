param(
    [string]$DevkitPro = $env:DEVKITPRO
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($DevkitPro) -or -not (Test-Path -LiteralPath $DevkitPro)) {
    $DevkitPro = "C:\devkitPro"
}

$gcc = Join-Path $DevkitPro "devkitARM\bin\arm-none-eabi-gcc.exe"
$objcopy = Join-Path $DevkitPro "devkitARM\bin\arm-none-eabi-objcopy.exe"
$gbafix = Join-Path $DevkitPro "tools\bin\gbafix.exe"
$specs = Join-Path $DevkitPro "devkitARM\arm-none-eabi\lib\gba.specs"

foreach ($tool in @($gcc, $objcopy, $gbafix, $specs)) {
    if (-not (Test-Path -LiteralPath $tool)) {
        throw "Required devkitARM file not found: $tool"
    }
}

function Invoke-Checked {
    param(
        [string]$FilePath,
        [string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath exited with code $LASTEXITCODE."
    }
}

function Build-Variant {
    param(
        [string]$Name,
        [int]$WriteDuringHDraw,
        [string]$Title
    )

    $elf = "$Name.elf"
    $rom = "$Name.gba"
    $compilerArguments = @(
        "-mthumb",
        "-mthumb-interwork",
        "-mcpu=arm7tdmi",
        "-O2",
        "-ffreestanding",
        "-DWRITE_DURING_HDRAW=$WriteDuringHDraw",
        "-specs=$specs",
        "source\main.c",
        "-o", $elf
    )

    Invoke-Checked $gcc $compilerArguments
    Invoke-Checked $objcopy @("-O", "binary", $elf, $rom)
    Invoke-Checked $gbafix @($rom, "-t$Title", "-cC0DX", "-m01")
}

Push-Location $PSScriptRoot
try {
    Build-Variant "dispcnt-layer-latch" 0 "DISPCNTLATCH"
    Build-Variant "dispcnt-layer-latch-hdraw" 1 "DISPCNTHDRAW"
}
finally {
    Pop-Location
}
