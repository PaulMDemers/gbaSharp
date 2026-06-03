# Reference Capture Workflow

This project now has a lightweight path for comparing gbaSharp frames against
external emulator captures from tools such as mGBA or no$gba.

## Files

- `docs/gba-reference-frames.csv` lists the first reference targets.
- `scripts/new-reference-capture-checklist.py` joins those targets to the
  save-assisted route manifest and writes a capture checklist.
- `scripts/validate-reference-captures.py` checks dropped reference PNGs for
  missing files, unreadable files, incorrect dimensions, and extra untracked
  captures.
- `scripts/compare-reference-frames.py` compares actual gbaSharp frames against
  reference images and can write diff PNGs.
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

## Current Status

As of 2026-06-03, the dashboard tooling is ready and the post-audio check
reports 8 missing mGBA PNG captures. See `docs/gba-reference-status.md` for the
current missing file list and strict dashboard commands.

## Manual Reference Capture

Generate the current capture checklist first:

```powershell
python .\scripts\new-reference-capture-checklist.py
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

## Compare

For the combined dashboard path, run:

```powershell
.\scripts\run-reference-dashboard.ps1
```

Useful strict dashboard mode once all references should be present:

```powershell
.\scripts\run-reference-dashboard.ps1 -StrictReferences
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

Useful strict intake mode once all reference images should be present:

```powershell
python .\scripts\validate-reference-captures.py --manifest docs\gba-reference-frames.csv --output artifacts\reference-capture-validation.csv --fail-on-missing --fail-on-invalid --fail-on-extra
```

Run:

```powershell
python .\scripts\compare-reference-frames.py --manifest docs\gba-reference-frames.csv --output reference-frame-comparison.csv --write-diffs --contact-sheet artifacts\reference-frame-comparison.png
```

Useful strict mode once all reference images exist:

```powershell
python .\scripts\compare-reference-frames.py --manifest docs\gba-reference-frames.csv --output reference-frame-comparison.csv --write-diffs --contact-sheet artifacts\reference-frame-comparison.png --fail-on-diff --fail-on-missing
```

Rows with missing reference images are reported as `missing-reference` and do not
fail by default. Rows with mismatched dimensions are reported as `size-mismatch`.
The contact sheet shows actual, reference, and diff columns for fast review.
