# GBA Reference Capture Status

Last checked: 2026-06-06

The external reference dashboard tooling is ready, but the current reference sets are blocked on capture intake. The non-strict save-assisted dashboard run at `artifacts/reference-dashboard-post-audio-check` generated a capture checklist, validation CSV, comparison CSV, and placeholder comparison contact sheet. The strict longplay reference check at `artifacts/longplay-reference-checklist-20260606` generated the matching checklist, validation CSV, comparison CSV, and placeholder comparison contact sheet.

## Current Result

- Save-assisted reference targets: 8
- Strict longplay reference targets: 17
- Present reference captures: 0
- Missing reference captures: 25
- Invalid reference captures: 0
- Pixel comparisons ready: 0

## Missing Save-Assisted mGBA Captures

Place 240x160 PNG captures at these paths:

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

## Missing Strict Longplay mGBA Captures

Place 240x160 PNG captures at these paths:

| Label | Reference PNG |
| --- | --- |
| `sonic-advance-longplay` | `reference-captures/mgba/longplay/sonic-advance-longplay.png` |
| `metroid-fusion-longplay` | `reference-captures/mgba/longplay/metroid-fusion-longplay.png` |
| `doom-longplay` | `reference-captures/mgba/longplay/doom-longplay.png` |
| `gta-longplay` | `reference-captures/mgba/longplay/gta-longplay.png` |
| `wario-land4-longplay` | `reference-captures/mgba/longplay/wario-land4-longplay.png` |
| `fire-emblem-longplay` | `reference-captures/mgba/longplay/fire-emblem-longplay.png` |
| `mario-kart-longplay` | `reference-captures/mgba/longplay/mario-kart-longplay.png` |
| `tony-hawk2-longplay` | `reference-captures/mgba/longplay/tony-hawk2-longplay.png` |
| `castlevania-aria-longplay` | `reference-captures/mgba/longplay/castlevania-aria-longplay.png` |
| `castlevania-harmony-longplay` | `reference-captures/mgba/longplay/castlevania-harmony-longplay.png` |
| `golden-sun-longplay` | `reference-captures/mgba/longplay/golden-sun-longplay.png` |
| `pokemon-ruby-longplay` | `reference-captures/mgba/longplay/pokemon-ruby-longplay.png` |
| `mario-luigi-longplay` | `reference-captures/mgba/longplay/mario-luigi-longplay.png` |
| `mega-man-battle-network-longplay` | `reference-captures/mgba/longplay/mega-man-battle-network-longplay.png` |
| `fzero-gp-longplay` | `reference-captures/mgba/longplay/fzero-gp-longplay.png` |
| `fzero-maximum-longplay` | `reference-captures/mgba/longplay/fzero-maximum-longplay.png` |
| `warioware-longplay` | `reference-captures/mgba/longplay/warioware-longplay.png` |

## Useful Commands

Generate the current checklist:

```powershell
.\scripts\run-reference-dashboard.ps1 -OutputRoot artifacts\reference-dashboard-post-audio-check -NoBuild
```

After captures are present, run strict validation and comparison:

```powershell
.\scripts\run-reference-dashboard.ps1 -OutputRoot artifacts\reference-dashboard-post-audio-strict -StrictReferences -NoBuild
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
