# DISPCNT Layer-Latch Diagnostic

This source builds two repeating GBA test ROMs for measuring background-enable
latency. BG0 is a solid green tile layer over a black backdrop. The HBlank
variant enables BG0 during HBlank on line 79 and disables it during HBlank on
line 119. The HDraw variant performs the same writes just after those scanlines
begin. The first and last green scanlines reveal how DISPCNT layer enables are
latched.

Build with devkitARM:

```powershell
.\tests\TestRoms\DispcntLayerLatch\build.ps1
```

Generated `.elf`, `.gba`, save, and capture files are intentionally not tracked.

Reference results on 2026-07-24:

- gbaSharp, mGBA, and MAME 0.288 render rows 80-119 for the HBlank
  variant.
- gbaSharp, mGBA, and MAME 0.288 render rows 79-118 for the early-HDraw
  variant.
- These results confirm scanline-level sampling for this diagnostic: an HDraw
  write affects the current line, while an HBlank write affects the next line.
