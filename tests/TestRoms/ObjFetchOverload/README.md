# OBJ Fetch Overload Diagnostic

This source builds a small GBA ROM for investigating the terminal object at the
per-scanline fetch limit. It enables HBlank OAM access, places fourteen
transparent 64x64 regular objects first in OAM, and places a visible 64x64
eight-color object at index 14.

The first fourteen objects leave index 14 with slightly less fetch time than a
complete 64-pixel row requires. Its eight-pixel color bands make whole, partial,
and absent rendering easy to distinguish in emulator or hardware captures.
Build with `TARGET_INDEX=15` to place one more transparent object before the
strip; a renderer enforcing the entry cutoff should then omit the strip.

Build both variants with devkitARM:

```powershell
.\tests\TestRoms\ObjFetchOverload\build.ps1
```

Generated `.elf`, `.gba`, and capture files are intentionally not tracked.

After building, capture mGBA with:

```powershell
.\scripts\run-mgba-reference-captures.ps1 `
  -Routes tests\TestRoms\ObjFetchOverload\reference-route.routes `
  -RomRoot tests\TestRoms\ObjFetchOverload `
  -ReferenceRoot artifacts\obj-fetch-overload-reference\mgba `
  -OutputRoot artifacts\obj-fetch-overload-reference\mgba-run `
  -Force
```

Reference results on 2026-07-24:

- gbaSharp and mGBA render the complete index-14 strip, including x=143.
- gbaSharp and mGBA omit the index-15 strip.
- MAME 0.288 renders both strips and therefore is not a timing oracle for this
  particular OBJ budget limit.
