# GBA Release Gate

The release gate is the quickest path from compatibility work to a shippable
build. It wraps the existing focused runners instead of replacing them.

## Profiles

- `smoke`: Release builds, unit tests, a small hero gameplay set, and a short
  audio sanity pass. Use this after ordinary emulator changes.
- `candidate`: Release builds, unit tests, release-critical strict gameplay
  routes, save-assisted routes, and audio smoke. Use this as the fast release
  candidate gate while broader fresh-start route flakiness is still being
  triaged.
- `standard`: Release builds, unit tests, the full strict deep-gameplay suite,
  save-assisted routes, and audio smoke. Use this before merging compatibility
  work.
- `full`: Everything in `standard` plus strict longplay routes. Use this before
  tagging or publishing a build.

## Commands

```powershell
.\scripts\run-release-gate.ps1 -Profile smoke -NormalPriority
.\scripts\run-release-gate.ps1 -Profile candidate -NormalPriority
.\scripts\run-release-gate.ps1 -Profile standard -NormalPriority
.\scripts\run-release-gate.ps1 -Profile full -NormalPriority
```

Run a single suite or chunk range when a full profile is too large for the
current host:

```powershell
.\scripts\run-release-gate.ps1 -Profile standard -OnlySuite deep-current-full -StartChunk 0 -MaxChunks 1 -NormalPriority
.\scripts\run-release-gate.ps1 -Profile standard -OnlySuite save-assisted-current -NormalPriority
.\scripts\run-release-gate.ps1 -Profile standard -OnlySuite audio-current -NormalPriority
.\scripts\run-release-gate.ps1 -Profile full -OnlySuite longplay-current -StartChunk 2 -MaxChunks 1 -NormalPriority
```

Each run writes a timestamped folder under `artifacts\release-gate-*` with a
top-level `release-gate-summary.md`, per-suite logs, and the normal runner
reports/contact sheets.

By default the gate stops after the first required suite failure to avoid
spending hours on later suites after a known blocker. Use `-ContinueOnFailure`
when you explicitly want a broad audit that keeps collecting failures.

## Release Definition

A release candidate should pass:

- Release CLI and desktop builds.
- 301/301 unit tests or newer full test count.
- Current strict deep-gameplay suite with no failures and exact baseline matches.
- Save-assisted strict gameplay suite with no failures and exact baseline matches.
- Audio smoke with no unexpected signal mismatches.
- Full profile longplay suite before publishing a tagged build.

The gate does not prove perfect emulation. It is the fast, repeatable line for
detecting regressions in boot, rendering, input, save-assisted gameplay, audio
signal generation, and longer route stability.

## Current Smoke Result

The first validated smoke run is
`artifacts\release-gate-smoke-20260703-064519\release-gate-summary.md`.
It passed all 4 required suites:

- Release CLI/desktop build and unit tests.
- `sonic-advance-default` and `mario-kart-race` strict gameplay matches.
- `pokemon-ruby-save-bedroom` strict save-assisted match.
- `pokemon-ruby-title` and `sonic-advance-save-controlled` audio signal matches.

## Current Standard Findings

The first standard attempt,
`artifacts\release-gate-standard-20260703-070225`, confirmed the Release build
and 295/295 unit tests, then found one strict deep-gameplay blocker:
`doomii-gameplay` rendered the same valid first-person scene but differed in the
weapon sprite animation phase from the older local baseline. Two focused reruns
at `artifacts\release-gate-followup-doomii-repeat-a-20260703` and
`artifacts\release-gate-followup-doomii-repeat-b-20260703` reproduced the new
frame byte-for-byte, so the local Doom II baseline was refreshed and
`artifacts\release-gate-followup-doomii-strict-20260703/deep-gameplay.csv`
verifies `doomii-gameplay` as `pass, match`.

After the Doom II refresh, the first deep-gameplay chunk was replayed at
`artifacts\release-gate-followup-deep-current-01-05-20260703`. Doom II, Sonic,
and Mario Kart matched, while Doom and GTA aborted under that loaded chunk run;
the focused follow-up `artifacts\release-gate-followup-doom-gta-solo-20260703`
then verified both `doom-gameplay` and `gta-gameplay` as `pass, match`. Treat
those as harness/load-budget noise unless they reproduce in focused solo runs.

The targeted audio standard suite now passes at
`artifacts\release-gate-standard-20260703-112043-564\release-gate-summary.md`:
11/11 rows reached target frames, all 11 signal expectations matched, and the
calibrated release-gate WAV gain is `0.45`. The audio CSV helpers now tolerate
truncated trailing CSV rows by treating missing numeric fields as defaults,
which fixed the Castlevania Aria WAV export failure seen during the first
targeted audio run.

`dump-frame` diagnostics are now available through `--diagnostic-log`, and
`scripts\run-deep-gameplay.ps1` writes per-route command, stdout, stderr, and
diagnostic logs under each run's `logs` directory. The first diagnostic reruns
confirmed useful frame/step/PC breadcrumbs and managed process-exit markers.
With those logs enabled, the candidate save-assisted suite passed at
`artifacts\release-gate-candidate-20260703-205120-887\release-gate-summary.md`:
8/8 rows are strict `pass, match` with 0 low-diversity warnings.

The current full candidate gate is
`artifacts\release-gate-candidate-expanded-20260710\release-gate-summary.md`.
It passes all 4 required suites: Release build/tests with 295/295 tests, 8/8
critical visual routes as strict `pass, match`, 8/8 save-assisted gameplay
routes as strict `pass, match`, and 11/11 audio smoke rows with matching signal
expectations. The complete expanded gate ran for 6,688 seconds with no required
failures.

The first five routes of the broader standard deep-gameplay gate were rerun
one process at a time at
`artifacts\release-gate-standard-20260710-175507-419`. Doom, Doom II, GTA,
Sonic Advance, and Mario Kart are 5/5 strict `pass, match`, with no
low-diversity warnings or abnormal exits. The diagnostic logs show every route
reaching its exact target frame and writing its output before a managed process
exit. Doom and GTA are therefore restored to the release-critical manifest;
the next candidate run includes those recovered routes.

Fresh-start Metroid Fusion was qualified separately at
`artifacts\compat-finish-metroid-fresh-20260710`: it reached frame 42,000 and
matched the strict baseline with 17 snapshots and 11 distinct PCs. Fresh-start
Castlevania Aria of Sorrow reached frame 22,000 normally in two consecutive
runs at `artifacts\compat-finish-aria-fresh-20260710` and
`artifacts\compat-finish-aria-repeat-20260710`. Both Aria runs produced the
same framebuffer hash and valid controllable castle gameplay. The former
baseline represented a different castle room, so it was refreshed to the
repeatable current route state. Metroid and Aria are restored to the
release-critical manifest, bringing it to seven strict visual routes.

Fresh-start Castlevania Harmony of Dissonance was then qualified at
`artifacts\compat-finish-harmony-fresh-20260710`. It reached frame 30,000,
matched its strict baseline, recorded 20 snapshots with 8 distinct PCs, and
exited normally after more than 8.5 billion emulated cycles. Harmony is restored
to the release-critical manifest. This closes the former fresh-start hard-abort
exclusion cluster.

The candidate deep manifest now contains eight strict visual routes that are
reliable on this host: Doom, Doom II, GTA, Sonic Advance, Mario Kart, Metroid
Fusion, Castlevania Aria of Sorrow, and Castlevania Harmony of Dissonance.
Broader visual diversity remains in `standard`/`full`; `candidate` is the
release line paired with save-assisted gameplay and audio smoke.

The current standard deep-gameplay audit is consolidated at
`artifacts\compat-finish-standard-55-rollup-20260711`. It covers all 55
manifest routes with 55/55 strict `pass, match` rows, 0 missing routes, 0
failures, and 0 low-diversity warnings. The audit was run in small sequential
bands and then merged in manifest order to keep host load bounded. The final
band independently verifies Driver 2 Advance, Tomb Raider: The Prophecy,
Madden NFL 2004, Baldur's Gate: Dark Alliance, and Driv3r.

Dragon Ball Z CCG's old local baseline showed a different portrait state. Two
current gbaSharp runs were byte-identical, and an mGBA capture driven by the
same repeated-input script reached the same complete Goku card-hand scene.
The refreshed local baseline then passed an independent strict rerun at
`artifacts\compat-finish-dbz-current-verify-20260711`. This was route-state
drift in the old baseline, not a bitmap renderer regression.

The full-profile longplay requirement is now green at
`artifacts\compat-finish-longplay-24-20260711`: 24/24 strict routes are
`pass, match`, with 0 failures and 0 low-diversity warnings. Every changed
active-scene baseline was visually reviewed and required a second
byte-identical focused run before promotion. Together with the expanded
candidate gate and the 55/55 standard gameplay rollup, all compatibility
layers required by the release checklist now have current passing evidence.

The independent strict mGBA oracle was rerun after the local longplay refresh.
`artifacts\compat-finish-strict-reference-retimed-20260711` validates all 17
required captures and passes 17/17 frame comparisons. Its first pass caught
Mario Kart and F-Zero Maximum phase offsets; those routes were retimed through
documented frame alignment and bounded window matching, then independently
reverified locally before the 17/17 external result was accepted.

The 2026-07-18 HALTCNT accuracy pass adds real CPU sleep states for both BIOS
`HALTCNT` writes and no-BIOS `Halt`/`Stop` SWIs. HALT keeps video, audio,
timers, DMA, and the scheduler running until an enabled interrupt is pending,
independent of IME; STOP freezes emulated system clocks and wakes only for
enabled serial, keypad, or Game Pak interrupts. The full suite is now 301/301.
Focused real-BIOS GTA and Sonic routes remain exact baseline matches at
`artifacts\haltcnt-activity-evidence-20260718`, and a five-game no-BIOS action
slice completes 20/20 phases after the bounded Metal Slug retry.

Because real BIOS code normally sleeps at `0x00000348` between frames, periodic
frame-boundary snapshots can now legitimately report one distinct PC. Snapshot
CSVs therefore include `frameHash`; deep-gameplay reports retain
`distinctPcs`, add `distinctFrames`, and use the larger value as
`activityDiversity`. The focused rollup at
`artifacts\haltcnt-activity-rollup-selected-20260718` has zero warnings while
still showing Sonic's expected 1-PC BIOS sleep loop and 11 distinct rendered
frames.

The follow-up OBJ mosaic/window pass aligns sprite mosaic blocks to the display
grid instead of restarting them at each sprite origin. It also models the
hardware rule that OBJ-window sprites ignore OBJ mosaic and require both OBJ
and OBJ-window master enables. Five focused tests cover offset, flipped, and
affine sprites plus both OBJ-window cases, bringing the suite to 306/306.
`artifacts\ppu-obj-mosaic-regression-20260718` verifies Sonic Advance, Mario
Kart, Zelda Minish Cap, and Powerpuff Girls at their active gameplay checkpoints
with 4/4 exact baseline matches.

The follow-up OBJ composition pass implements the hardware rule that special
effects see only the top-most OBJ pixel. A sprite replacing another sprite no
longer promotes the hidden sprite into the second-target slot, so OBJ-to-OBJ
alpha blending cannot occur and the selected background remains available for
valid OBJ-to-BG blending. Two focused overlap tests bring the suite to 308/308.
The same four strict retail checkpoints remain 4/4 exact matches in
`artifacts\ppu-obj-blend-regression-20260718`.

OBJ rendering now resolves OAM into one scanline plane before composition.
Vertical mosaic is applied while sampling sprites; horizontal mosaic uses the
hardware-style screen-wide latch over that resolved plane. The latch retains a
higher-priority pixel when a lower-priority mosaic sprite enters mid-block,
updates immediately for a higher-priority sprite, and carries opaque pixels
across transparent mosaic texels. Three focused tests bring the suite to
311/311. `artifacts\ppu-obj-latch-regression-20260718` keeps Sonic Advance,
Mario Kart, Zelda Minish Cap, and Powerpuff Girls at 4/4 exact matches.
The separate bitmap-mode anchor in
`artifacts\ppu-obj-latch-doom-regression-20260718` keeps Doom at an exact
frame-9,000 gameplay match with activity diversity 10.

The OBJ overload pass adds per-scanline fetch exhaustion in OAM order. Normal
lines receive 1,210 OBJ cycles; DISPCNT HBlank-free mode receives 954. OAM
scanning, normal and affine width costs, and left clipping all contribute to
the budget. Five focused overload tests bring the suite to 316/316.
`artifacts\ppu-obj-budget-regression-20260724` keeps Sonic Advance, Mario Kart,
Zelda Minish Cap, and Powerpuff Girls at 4/4 exact matches with activity
diversity 11, 12, 14, and 13. The independent bitmap-mode route in
`artifacts\ppu-obj-budget-doom-regression-20260724` keeps Doom at an exact
frame-9,000 match with activity diversity 10.

The source-built diagnostic pair under
`tests\TestRoms\ObjFetchOverload` isolates the fetch boundary. gbaSharp and
mGBA both render the complete index-14 terminal OBJ and omit index 15. MAME
0.288 renders both variants, so it is not used as an oracle for this specific
limit. The far edge of the terminal OBJ is now unit-covered.

The source-built `tests\TestRoms\DispcntLayerLatch` pair confirms DISPCNT BG0
enable timing at scanline boundaries. gbaSharp, mGBA, and MAME 0.288 agree on
rows 79-118 for early-HDraw writes and rows 80-119 for HBlank writes. Two
exact-pixel timing tests bring the suite to 318/318.

The ARM and Thumb CPU groups from Julian Smolka's MIT-licensed `gba-tests`
suite now both report `All tests passed`. Six focused regressions cover the
FIQ r8-r12 bank, Thumb empty-list and base-in-list STM behavior, ARM
register-shift PC reads, test-opcode SPSR restoration, and ARM store-PC
pipeline values, bringing the maintained suite to 324/324. The strict retail
blast-radius run remains 5/5 exact: four routes in
`artifacts\cpu-architecture-regression-4-20260724` plus Doom at frame 9,000 in
`artifacts\cpu-architecture-regression-20260724`. Other external `gba-tests`
groups remain independently classified rather than being included in this CPU
conformance claim.

The external suite's BIOS protection group now also reports `All tests passed`
with the standard 16 KiB World BIOS. ARM prefetch now latches the protected
read word from the pipeline fetch stage at `current instruction + 8`, covering
startup, SWI, IRQ-handler, and post-IRQ behavior. One focused regression brings
the maintained suite to 325/325. The real-BIOS retail blast-radius gate remains
5/5 exact in `artifacts\cpu-bios-latch-regression-5-20260724` and
`artifacts\cpu-bios-latch-doom-20260724`.

The external memory mirror and video byte-write group now reports `All tests
passed`. BG-VRAM byte writes duplicate across the addressed halfword; tiled
and bitmap OBJ VRAM retain their mode-dependent exclusion boundaries, and OAM
byte writes remain ignored. The split regression brings the maintained suite
to 326/326. Strict retail graphics remain 5/5 exact in
`artifacts\memory-vram-byte-regression-4-20260724` and
`artifacts\memory-vram-byte-doom-20260724`.

The external no-save, SRAM, Flash64, and Flash128 groups now report `All tests
passed`. Game Pak save halfword/word reads repeat one byte, wide writes select
one byte lane from the original address, and missing hardware returns `0xFF`
while ignoring writes. Three focused regressions bring the maintained suite to
329/329. The non-erased Sonic SRAM and Pokemon Ruby Flash128K fixtures remain
2/2 exact in `artifacts\save-bus-retail-regression-20260724`; the full
eight-route export/reload smoke completes in
`artifacts\save-bus-roundtrip-smoke-20260724`.

The external NES-derived ARM pipeline and STM/DMA ordering group now reports
`All tests passed`. Separate decode and fetch-stage slots preserve both
prefetched instructions across self-modifying VRAM writes. One focused
regression brings the maintained suite to 330/330; the generated-code GTA
route remains an exact frame-12,000 match in
`artifacts\pipeline-fetch-stage-gta-20260724`.

The external unsafe-access ROM is not a release gate. Its 32 KiB SRAM mirror
check passes after aligning mapping with the existing SRAM export size. Its
remaining unused-ROM test is intentionally left at `002`: the suite's own
README says real hardware fails the expected incrementing-value pattern, while
gbaSharp returns `0xFF` beyond the loaded ROM. The progressed Sonic Advance
SRAM fixture remains exact in `artifacts\sram-32k-mirror-sonic-20260724`.
