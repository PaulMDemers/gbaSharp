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
$include = Join-Path $DevkitPro "libgba\include"
$library = Join-Path $DevkitPro "libgba\lib"

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
        [int]$TargetIndex,
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
        "-DTARGET_INDEX=$TargetIndex",
        "-specs=$specs",
        "-I$include",
        "-L$library",
        "source\main.c",
        "-lgba",
        "-o", $elf
    )

    Invoke-Checked $gcc $compilerArguments
    Invoke-Checked $objcopy @("-O", "binary", $elf, $rom)
    Invoke-Checked $gbafix @($rom, "-t$Title", "-cC0DX", "-m01")
}

Push-Location $PSScriptRoot
try {
    Build-Variant "obj-fetch-overload" 14 "OBJOVERLOAD"
    Build-Variant "obj-fetch-overload-late" 15 "OBJLATE"
}
finally {
    Pop-Location
}
