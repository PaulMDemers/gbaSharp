# GBA Reference Capture Status

Last checked: 2026-06-03

The external reference dashboard tooling is ready, but the current reference set is blocked on capture intake. The non-strict dashboard run at `artifacts/reference-dashboard-post-audio-check` generated a capture checklist, validation CSV, comparison CSV, and placeholder comparison contact sheet.

## Current Result

- Reference targets: 8
- Present reference captures: 0
- Missing reference captures: 8
- Invalid reference captures: 0
- Pixel comparisons ready: 0

## Missing mGBA Captures

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

## Useful Commands

Generate the current checklist:

```powershell
.\scripts\run-reference-dashboard.ps1 -OutputRoot artifacts\reference-dashboard-post-audio-check -NoBuild
```

After captures are present, run strict validation and comparison:

```powershell
.\scripts\run-reference-dashboard.ps1 -OutputRoot artifacts\reference-dashboard-post-audio-strict -StrictReferences -NoBuild
```

To refresh save-assisted gbaSharp frames before comparing:

```powershell
.\scripts\run-reference-dashboard.ps1 -OutputRoot artifacts\reference-dashboard-post-audio-refresh-strict -RunDeepGameplay -StrictReferences
```
