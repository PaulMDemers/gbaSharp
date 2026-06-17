# Post-Audio Deep Gameplay Rollup

This rollup consolidates the post-audio deep gameplay refresh artifacts after
the shared audio mixer, balance, route-threshold, save-assisted, and first two
gameplay-depth expansion passes.

- Rollup artifact: `artifacts/deep-gameplay-40-route-rollup-20260616`
- Combined CSV: `artifacts/deep-gameplay-40-route-rollup-20260616/deep-gameplay.csv`
- Contact sheet: `artifacts/deep-gameplay-40-route-rollup-20260616/contact-sheet.png`
- Manifest: `docs/gba-deep-gameplay-routes.csv`

## Summary

- Manifest routes: 40
- Covered routes: 40
- Missing routes: 0
- Failing rows: 0
- Status/baseline: 40 `pass, match`
- Low-diversity warnings: 0
- Source reports: 4 rollup inputs: the prior 36-route rollup, the KOF/Fire
  Emblem verification batch, and single-route FFTA/GT Advance verifications

The rollup was generated with `scripts/new-deep-gameplay-rollup.ps1` using warnings-as-errors. This means every route has post-audio evidence, every required baseline matches exactly, and every low-distinct-PC route is either above the global threshold or has a route-specific `minDistinctPcs` value.

## 2026-06-16 Expansion Routes

The first gameplay-depth expansion added six non-Pokemon/Sonic routes:

| Route | Coverage |
| --- | --- |
| `metal-slug-advance-gameplay` | Sprite-heavy run-and-gun mission gameplay |
| `wolfenstein3d-gameplay` | Additional first-person renderer/input coverage |
| `mega-man-zero-gameplay` | High-speed platform combat scene |
| `street-fighter-alpha3-gameplay` | Fighting-game sprite/windowing scene |
| `puyo-pop-gameplay` | Puzzle board/drop interaction scene |
| `contra-advance-gameplay` | Additional side-scrolling run-and-gun scene |

The second expansion added four more routes:

| Route | Coverage |
| --- | --- |
| `king-of-fighters-ex2-gameplay` | Active fighting scene |
| `fire-emblem-sacred-stones-gameplay` | In-engine tactical RPG story scene |
| `ff-tactics-advance-gameplay` | In-engine town/dialogue scene |
| `gt-advance2-race` | Active rally racing scene |

## Calibrated Low-Diversity Routes

These routes are intentionally below the global 8-PC warning threshold because repeat runs showed stable tight route loops:

| Route | Distinct PCs | Threshold | Snapshots |
| --- | ---: | ---: | ---: |
| `kirby-nightmare-gameplay` | 3 | 3 | 50 |
| `ff-tactics-advance-gameplay` | 4 | 4 | 25 |
| `fzero-maximum-race` | 4 | 4 | 15 |
| `mega-man-zero-gameplay` | 4 | 4 | 20 |
| `metal-slug-advance-gameplay` | 4 | 4 | 16 |
| `scooby-unmasked-action` | 4 | 4 | 40 |
| `super-mario-advance2-map` | 4 | 4 | 40 |
| `banjo-pilot-race` | 5 | 5 | 40 |
| `mega-man-battle-network-room` | 5 | 5 | 15 |
| `powerpuff-mojo-action` | 5 | 5 | 10 |
| `tony-hawk-sk8land-gameplay` | 6 | 6 | 10 |
| `crash-ntranced-level` | 7 | 7 | 15 |
| `fzero-gp-race` | 7 | 7 | 40 |
| `sonic-advance-default` | 7 | 7 | 12 |
| `spy-muppets-action` | 7 | 7 | 10 |
| `zelda-minish-bedroom` | 7 | 7 | 15 |

## Notes

- `metroid-fusion-gameplay` was refreshed as a solo long route and reaches frame 42,000 with 11 distinct PCs.
- `castlevania-circle-gameplay` now uses a 2,400-second manifest wall-clock budget after loaded-host runs reached frame 37,658/38,000 before the previous 1,800-second cap.
- `zelda-minish-bedroom` was filled after the initial rollup audit revealed the 6-8 route gap; repeat runs confirm its 7-PC room-loop profile.
- The generated 40-route contact sheet visually covers all current final frames
  in manifest order.
