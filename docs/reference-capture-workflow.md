# Reference Capture Workflow

This project now has a lightweight path for comparing gbaSharp frames against
external emulator captures from tools such as mGBA or no$gba.

## Files

- `docs/gba-reference-frames.csv` lists the first save-assisted reference targets.
- `docs/gba-longplay-reference-frames.csv` lists the first strict longplay
  reference targets.
- `scripts/new-reference-capture-checklist.py` joins those targets to the
  save-assisted route manifest and writes a capture checklist.
- `scripts/validate-reference-captures.py` checks dropped reference PNGs for
  missing files, unreadable files, incorrect dimensions, and extra untracked
  captures.
- `scripts/compare-reference-frames.py` compares actual gbaSharp frames against
  reference images and can write diff PNGs.
- `scripts/match-mgba-reference-windows.py` captures a gbaSharp frame window
  around a strict route and finds the closest frame to a single mGBA reference.
- `scripts/match-frame-windows.py` compares two captured frame windows
  pairwise, which is useful when input timing can diverge between emulators.
- `scripts/score-reference-regions.py` scores named screen regions so global
  route drift can be separated from layer-specific render drift.
- `scripts/run-reference-dashboard.ps1` runs the checklist, validation,
  comparison, contact sheet, and Markdown summary steps as one command. It can
  optionally refresh the save-assisted deep gameplay suite first.
- Reference images should be placed under `reference-captures/<source>/`.
  These images are local artifacts and are intentionally ignored by git.
- `docs/gba-reference-status.md` records the latest capture intake status.

## Initial Targets

The starter set covers every save-assisted gameplay route:

- `zelda-minish-save-bedroom`: EEPROM bedroom scene.
- `advance-wars-save-map`: Flash64K field map.
- `metroid-fusion-save-gameplay`: SRAM boss-room gameplay.
- `sonic-advance-save-controlled`: controlled Sonic beach gameplay.
- `mario-luigi-save-room`: EEPROM room gameplay.
- `tony-hawk2-save-gameplay`: EEPROM skate tutorial gameplay.
- `pokemon-ruby-save-bedroom`: Flash128K save-loaded Ruby bedroom scene.
- `mario-kart-save-race`: affine racing scene.

The first strict longplay set covers longer exact-match scenes:

- `sonic-advance-external-reference`: short stable Sonic beach scene for external comparison; `sonic-advance-longplay` remains the longer local soak.
- `metroid-fusion-longplay`: extended boss-room/action-platformer scene.
- `doom-external-reference`: short stable no-input id-logo scene for external comparison; `doom-longplay` remains the longer local FPS soak.
- `gta-external-reference`: short stable no-input intro scene for external comparison; `gta-longplay` remains the longer local on-foot soak.
- `wario-land4-longplay`: extended platforming scene.
- `fire-emblem-longplay`: tactical map/grid scene.
- `mario-kart-external-reference`: short stable no-input intro scene for external comparison; `mario-kart-longplay` remains the longer local affine race soak.
- `tony-hawk2-external-reference`: short stable no-input Activision logo scene for external comparison; `tony-hawk2-longplay` remains the longer local skate tutorial soak.
- `castlevania-aria-longplay`: extended castle gameplay scene.
- `castlevania-harmony-longplay`: extended castle gameplay scene.
- `golden-sun-longplay`: extended outdoor RPG scene.
- `pokemon-ruby-external-reference`: short stable no-save intro scene for external comparison; `pokemon-ruby-longplay` remains the longer local save-assisted room soak.
- `mario-luigi-longplay`: extended room scene.
- `mega-man-battle-network-longplay`: extended room scene.
- `fzero-gp-external-reference`: short stable no-input title scene for external comparison; `fzero-gp-longplay` remains the longer local active-race soak.
- `fzero-maximum-longplay`: active Maximum Velocity race scene.
- `warioware-longplay`: active microgame scene.

## Current Status

As of 2026-06-09, the dashboard tooling is ready. The save-assisted set still
needs 8 manual mGBA PNG captures, while the strict longplay external set has 17
valid captures and 17/17 passing bounded pixel comparisons. See
`docs/gba-reference-status.md` for the current capture status and strict
commands.

## Manual Reference Capture

Generate the current capture checklist first:

```powershell
python .\scripts\new-reference-capture-checklist.py
```

For the strict longplay checklist:

```powershell
python .\scripts\new-reference-capture-checklist.py --reference-manifest docs\gba-longplay-reference-frames.csv --route-manifest docs\gba-longplay-strict-routes.csv --csv-output artifacts\longplay-reference-checklist\reference-capture-checklist.csv --markdown-output artifacts\longplay-reference-checklist\reference-capture-checklist.md
```

1. Open the same ROM in the reference emulator.
2. Load the matching save fixture listed in `docs/gba-save-assisted-deep-routes.csv`.
3. Reproduce the input script listed in that row, or use the save state/frame capture
   support in the reference emulator to reach the same scene.
4. Capture a 240x160 PNG at the target frame.
5. Save it to the manifest path, for example:
   `reference-captures/mgba/sonic-advance-save-controlled.png`.

Exact frame parity can be hard when the reference emulator does not support input
scripts directly. For those cases, keep the image at the same visible scene first,
then relax `maxDifferentPixels` or `maxChannelDelta` only after reviewing the diff.

For active racing/action scenes, prefer a window-to-window check before treating a
large pixel diff as a renderer bug. Run `match-mgba-reference-windows.py` first;
if regions disagree about the best frame, capture a matching mGBA window with
`run-mgba-reference-captures.ps1 -FrameStart ... -FrameEnd ... -FrameStride ...`
and compare both windows with `match-frame-windows.py`.

For save-assisted mGBA captures, `run-mgba-reference-captures.ps1` loads the
manifest save fixture as soon as the Lua script initializes and resets once
after the load. This makes save-backed routes behave like gbaSharp's
pre-boot `--save-file` path instead of loading the save after the game has
already initialized its SRAM/Flash state.

Long local gameplay soaks are not always good external pixel targets. If an
active route diverges by player position, timer, or simulation state, keep the
long route as a local exact baseline and add a shorter dedicated reference row
at a stable same-scene frame. `sonic-advance-external-reference` is the current
example: the 30,000-frame Sonic soak remains local coverage, while the 9,000
frame reference is used for mGBA comparison.

## Compare

For the combined dashboard path, run:

```powershell
.\scripts\run-reference-dashboard.ps1
```

Useful strict dashboard mode once all references should be present:

```powershell
.\scripts\run-reference-dashboard.ps1 -StrictReferences
```

Run the current strict longplay external oracle:

```powershell
.\scripts\run-strict-reference-suite.ps1 -OutputRoot artifacts\strict-reference-suite-latest
```

To refresh gbaSharp save-assisted captures before comparing references:

```powershell
.\scripts\run-reference-dashboard.ps1 -RunDeepGameplay -StrictReferences
```

To keep refreshes bounded during local smoke passes:

```powershell
.\scripts\run-reference-dashboard.ps1 -RunDeepGameplay -RouteMaxSecondsCap 120 -AllowDeepGameplayFailures
```

Validate dropped captures first:

```powershell
python .\scripts\validate-reference-captures.py --manifest docs\gba-reference-frames.csv --output artifacts\reference-capture-validation.csv
```

Validate strict longplay captures:

```powershell
python .\scripts\validate-reference-captures.py --manifest docs\gba-longplay-reference-frames.csv --output artifacts\longplay-reference-capture-validation.csv --reference-root reference-captures\mgba\longplay
```

Useful strict intake mode once all reference images should be present:

```powershell
python .\scripts\validate-reference-captures.py --manifest docs\gba-reference-frames.csv --output artifacts\reference-capture-validation.csv --fail-on-missing --fail-on-invalid --fail-on-extra
```

Run:

```powershell
python .\scripts\compare-reference-frames.py --manifest docs\gba-reference-frames.csv --output reference-frame-comparison.csv --write-diffs --contact-sheet artifacts\reference-frame-comparison.png
```

Run strict longplay comparison:

```powershell
python .\scripts\compare-reference-frames.py --manifest docs\gba-longplay-reference-frames.csv --output artifacts\longplay-reference-frame-comparison.csv --write-diffs --contact-sheet artifacts\longplay-reference-frame-comparison.png
```

Summarize strict longplay comparison quality:

```powershell
python .\scripts\summarize-reference-comparison.py --manifest docs\gba-longplay-reference-frames.csv --output artifacts\longplay-reference-summary.csv
```

Useful strict mode once all reference images exist:

```powershell
python .\scripts\compare-reference-frames.py --manifest docs\gba-reference-frames.csv --output reference-frame-comparison.csv --write-diffs --contact-sheet artifacts\reference-frame-comparison.png --fail-on-diff --fail-on-missing
```

Rows with missing reference images are reported as `missing-reference` and do not
fail by default. Rows with mismatched dimensions are reported as `size-mismatch`.
The contact sheet shows actual, reference, and diff columns for fast review.
