# GBA Reference Capture Status

Last checked: 2026-06-15

The external reference dashboard tooling is ready. The save-assisted mGBA capture
pass at `artifacts/save-assisted-mgba-captures-20260615` generated all 8
reference PNGs, and the consolidated save-assisted reference run at
`artifacts/save-assisted-reference-suite-20260615` validates them as 8 `ok`
captures. Strict longplay reference validation at
`artifacts/longplay-reference-validation-gta-20260609.csv` reports all 17
manifest rows as valid, with seven extra local artifacts left from older targets.

The latest strict longplay external oracle run is
`artifacts/strict-reference-suite-visual-green-20260612`. Capture validation
reported 17 `ok` rows plus 7 extra local captures, and frame comparison
reported 17/17 `pass`.

## Current Result

- Save-assisted reference targets: 8
- Present save-assisted reference captures: 8
- Strict longplay reference targets: 17
- Present strict longplay reference captures: 17
- Missing save-assisted reference captures: 0
- Invalid reference captures: 0
- Save-assisted local deep gameplay: 8/8 `pass, match` with 0 low-diversity
  warnings in `artifacts/save-assisted-deep-gameplay-rollup-20260615`
- Broader deep gameplay: 40/40 `pass, match` with 0 low-diversity warnings in
  `artifacts/deep-gameplay-40-route-rollup-20260616`
- Save-assisted mGBA direct frame comparison: 2 `pass`, 6 `diff`; window
  matching resolves those six as 2 `exact`, 3 `ballpark-minor-delta`, and 1
  `renderer-or-route-delta` in
  `artifacts/save-assisted-reference-suite-20260615`
- Strict longplay pixel comparisons ready: 17 current references, with 17/17 passing the current bounded comparison
- Visual smoke: all 17 local `docs/gba-visual-snapshots.csv` rows pass with the current ignored baselines and manifest settings. This covers title screens, scripted menu paths, five short save-assisted checks, and the two long Ruby save gameplay routes.
- Audio smoke: 11/11 routes reached target frames in `artifacts/audio-smoke-full-expectation-gain045-20260613`; signal triage reports 10 `ok` routes, the expected `silent` Sonic title route, 0 signal expectation mismatches, and 0 clipped samples at `-WavGain 0.45`.

The 2026-06-12 Metroid Fusion title triage used `artifacts/metroid-title-reference-20260612`.
The mGBA/gbaSharp window comparison found exact full-frame matches in the same
starfield sequence, so the earlier Metroid title diff was a stale local baseline
rather than a renderer regression. The Metroid scripted row was retimed from a
black transition at frame 1800 to the visible space-intro frame 2070. The final
exact first-ten smoke report is
`artifacts/visual-first10-exact-post-scripted-refresh-20260612/visual-snapshots.csv`.
Ruby's longer Littleroot save route passes at frame 46000 after raising the local
ignored row wall-clock cap from 720s to 900s; the earlier 720s run timed out at
frame 45769 rather than failing emulation. The passing report is
`artifacts/visual-ruby-littleroot-900s-20260612/visual-snapshots.csv`.
Ruby's May-room save route passes at frame 65000 after raising the local ignored
row wall-clock cap from 900s to 1500s; the 900s run timed out on-track outside
in Littleroot at frame 44755. The passing report is
`artifacts/visual-ruby-may-room-1500s-20260612/visual-snapshots.csv`.
The 2026-06-15 save-assisted refresh promoted stale exact baselines for Advance
Wars, Metroid Fusion, Tony Hawk 2, Pokemon Ruby, and Mario Kart after confirming
they were scene-consistent current outputs. Metroid's promoted save-assisted
frame is an exact mGBA match at frame 30000. The remaining Ruby mGBA
save-assisted reference lands in a Birch/dialog path even though mGBA reports
`loadSaveFile=true`; treat that row as a reference-emulator save-load follow-up
rather than a renderer regression until the Flash128K import path is isolated.

Highest current bounded deltas from the latest strict run:

| Label | Different Pixels | Allowed |
| --- | ---: | ---: |
| `castlevania-harmony-longplay` | 5174 | 5500 |
| `golden-sun-longplay` | 3517 | 4000 |
| `sonic-advance-external-reference` | 2350 | 4000 |
| `wario-land4-longplay` | 603 | 800 |
| `metroid-fusion-longplay` | 442 | 600 |
| `warioware-longplay` | 324 | 500 |
| `pokemon-ruby-external-reference` | 116 | 150 |
| `fzero-maximum-longplay` | 111 | 200 |

## Current Save-Assisted mGBA Captures

The save-assisted manifest has valid 240x160 PNG captures at these paths:

| Label | Reference PNG |
| --- | --- |
| `zelda-minish-save-bedroom` | `reference-captures/mgba/zelda-minish-save-bedroom.png` |
| `advance-wars-save-map` | `reference-captures/mgba/advance-wars-save-map.png` |
| `metroid-fusion-save-gameplay` | `reference-captures/mgba/metroid-fusion-save-gameplay.png` |
| `sonic-advance-save-controlled` | `reference-captures/mgba/sonic-advance-save-controlled.png` |
| `mario-luigi-save-room` | `reference-captures/mgba/mario-luigi-save-room.png` |
| `tony-hawk2-save-gameplay` | `reference-captures/mgba/tony-hawk2-save-gameplay.png` |
| `pokemon-ruby-save-bedroom` | `reference-captures/mgba/pokemon-ruby-save-bedroom.png` |
| `mario-kart-save-race` | `reference-captures/mgba/mario-kart-save-race.png` |

## Current Strict Longplay mGBA Captures

The current strict longplay manifest has valid 240x160 PNG captures at these paths:

| Label | Reference PNG |
| --- | --- |
| `sonic-advance-external-reference` | `reference-captures/mgba/longplay/sonic-advance-external-reference.png` |
| `metroid-fusion-longplay` | `reference-captures/mgba/longplay/metroid-fusion-longplay.png` |
| `doom-external-reference` | `reference-captures/mgba/longplay/doom-external-reference.png` |
| `gta-external-reference` | `reference-captures/mgba/longplay/gta-external-reference.png` |
| `wario-land4-longplay` | `reference-captures/mgba/longplay/wario-land4-longplay.png` |
| `fire-emblem-longplay` | `reference-captures/mgba/longplay/fire-emblem-longplay.png` |
| `mario-kart-external-reference` | `reference-captures/mgba/longplay/mario-kart-external-reference.png` |
| `tony-hawk2-external-reference` | `reference-captures/mgba/longplay/tony-hawk2-external-reference.png` |
| `castlevania-aria-longplay` | `reference-captures/mgba/longplay/castlevania-aria-longplay.png` |
| `castlevania-harmony-longplay` | `reference-captures/mgba/longplay/castlevania-harmony-longplay.png` |
| `golden-sun-longplay` | `reference-captures/mgba/longplay/golden-sun-longplay.png` |
| `pokemon-ruby-external-reference` | `reference-captures/mgba/longplay/pokemon-ruby-external-reference.png` |
| `mario-luigi-longplay` | `reference-captures/mgba/longplay/mario-luigi-longplay.png` |
| `mega-man-battle-network-longplay` | `reference-captures/mgba/longplay/mega-man-battle-network-longplay.png` |
| `fzero-gp-external-reference` | `reference-captures/mgba/longplay/fzero-gp-external-reference.png` |
| `fzero-maximum-longplay` | `reference-captures/mgba/longplay/fzero-maximum-longplay.png` |
| `warioware-longplay` | `reference-captures/mgba/longplay/warioware-longplay.png` |

## Useful Commands

Generate the current checklist:

```powershell
.\scripts\run-reference-dashboard.ps1 -OutputRoot artifacts\reference-dashboard-post-audio-check -NoBuild
```

Run the save-assisted reference wrapper, including mGBA capture if desired:

```powershell
.\scripts\run-save-assisted-reference-suite.ps1 -OutputRoot artifacts\save-assisted-reference-suite-latest -CaptureMgba -ForceCapture
```

After captures are present, run strict validation and comparison:

```powershell
.\scripts\run-reference-dashboard.ps1 -OutputRoot artifacts\reference-dashboard-post-audio-strict -StrictReferences -NoBuild
```

Run the current strict longplay external oracle:

```powershell
.\scripts\run-strict-reference-suite.ps1 -OutputRoot artifacts\strict-reference-suite-latest
```

Generate and compare the strict longplay checklist:

```powershell
python .\scripts\new-reference-capture-checklist.py --reference-manifest docs\gba-longplay-reference-frames.csv --route-manifest docs\gba-longplay-strict-routes.csv --csv-output artifacts\longplay-reference-checklist\reference-capture-checklist.csv --markdown-output artifacts\longplay-reference-checklist\reference-capture-checklist.md
python .\scripts\validate-reference-captures.py --manifest docs\gba-longplay-reference-frames.csv --output artifacts\longplay-reference-checklist\reference-capture-validation.csv --reference-root reference-captures\mgba\longplay --fail-on-missing --fail-on-invalid --fail-on-extra
python .\scripts\compare-reference-frames.py --manifest docs\gba-longplay-reference-frames.csv --output artifacts\longplay-reference-checklist\reference-frame-comparison.csv --write-diffs --contact-sheet artifacts\longplay-reference-checklist\reference-frame-comparison.png --fail-on-diff --fail-on-missing
```

To refresh save-assisted gbaSharp frames before comparing:

```powershell
.\scripts\run-reference-dashboard.ps1 -OutputRoot artifacts\reference-dashboard-post-audio-refresh-strict -RunDeepGameplay -StrictReferences
```
