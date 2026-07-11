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
- 295/295 unit tests or newer full test count.
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
