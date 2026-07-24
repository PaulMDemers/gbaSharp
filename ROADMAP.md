# gbaSharp Roadmap

Last updated: 2026-07-24

## Current Release Line

gbaSharp `0.1.0` is a Windows preview release candidate. The deterministic core,
CLI tooling, and WinForms frontend support broad single-player retail testing
with real-BIOS and no-BIOS execution paths.

Current release evidence:

- 329/329 unit tests pass.
- The ARM and Thumb groups in the MIT-licensed jsmolka `gba-tests` suite both
  report `All tests passed`.
- The suite's real-BIOS protection group also reports `All tests passed`.
- The memory mirror and video byte-write group reports `All tests passed`.
- The no-save, SRAM, Flash64, and Flash128 groups report `All tests passed`.
- The curated official real-BIOS set has no active high-priority crash target.
- 55/55 standard gameplay routes match strict local baselines.
- 24/24 strict longplay routes match local baselines.
- 17/17 independent mGBA visual comparisons pass within reviewed tolerances.
- 8/8 release-critical and 8/8 save-assisted gameplay routes pass.
- 11/11 audio smoke routes meet their expected signal classifications.

These gates establish broad compatibility and regression confidence. They do
not prove cycle-perfect behavior, complete peripheral support, link play, or
perfect audio against hardware.

## Release Milestone

Before publishing `0.1.0`:

- Build and smoke-test the versioned Windows package.
- Run the full release gate against the release commit.
- Confirm the package contains no ROM, BIOS, save, or generated test artifacts.
- Publish the committed MIT license and current compatibility caveats.

## Accuracy Milestone

- Keep the new `HALTCNT` HALT/STOP implementation covered as timing work
  evolves. HALT advances hardware while the CPU sleeps and wakes on any enabled
  interrupt; STOP freezes system clocks and accepts the documented serial,
  keypad, and Game Pak wake sources.
- Expand video edge-case coverage for windows, affine backgrounds, object
  composition, and scanline timing. OBJ mosaic is now display-grid aligned for
  regular, flipped, and affine sprites, and OBJ-window mosaic/master-enable
  behavior is covered. OBJ composition now resolves one top-most sprite before
  special effects, preventing invalid OBJ-to-OBJ alpha blending while retaining
  the background as the second target. Horizontal OBJ mosaic now latches over
  the resolved sprite plane, including priority changes and transparent pixels.
  Per-scanline OBJ fetch limits now follow OAM order with the documented 1,210
  cycle budget, the 954-cycle HBlank-free budget, affine cost, and clipped OBJ
  cost. A source-built overload pair and mGBA captures confirm that the terminal
  OBJ completes while the next OAM entry is omitted. A second source-built
  diagnostic confirms DISPCNT background-enable sampling at HDraw/HBlank
  boundaries against mGBA and MAME. Other mid-scanline register changes remain
  focused accuracy targets.
- Continue the Ruby title-audio timing comparison against MAME or hardware.
- Audit Game Pak prefetch, bus contention, RTC behavior, and remaining open-bus
  approximations when a focused test or retail route exposes a discrepancy.
- Continue classifying the environment-sensitive jsmolka NES and unsafe-access
  groups independently from the now-green ARM, Thumb, real-BIOS, memory, and
  save conformance groups.

## Peripheral Milestone

- Add desktop controls for solar, gyro, tilt, and rumble state.
- Add dedicated Boktai, WarioWare Twisted, Yoshi Topsy-Turvy, and Drill Dozer
  gameplay routes.
- Treat peer link-cable and wireless play as unsupported until multi-instance
  communication has its own deterministic tests and user-facing transport.

## Gameplay Depth Milestone

- Deepen optional CIMA, DemiKids, and Tomb Raider Legend routes.
- Prefer routes that add new hardware, save, renderer, or timing coverage over
  increasing the manifest count with equivalent scenes.
- Keep the current 55-route, 24-route, and 17-capture gates green as the core
  changes.

## Product Milestone

- Add versioned save states and configurable input mapping.
- Decide whether debugger, memory viewer, and trace UI belong in the desktop
  application or remain CLI-first development tools.
- Add automated packaging and release artifacts to CI after the preview format
  is stable.
