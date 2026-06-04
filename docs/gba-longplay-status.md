# GBA Longplay Status

The longplay suite is a non-baseline gameplay soak layer. It reuses approved save fixtures, runs longer than the strict final-frame smoke routes, and records snapshots for stability and PC-diversity evidence. These rows intentionally use `baselineRequired=false`; final frames are evidence, not locked regression baselines yet.

- Manifest: `docs/gba-longplay-routes.csv`
- Runner wrapper: `scripts/run-longplay-suite.ps1`
- First rollup artifact: `artifacts/longplay-post-audio-smoke-rollup`
- Contact sheet: `artifacts/longplay-post-audio-smoke-rollup/contact-sheet.png`

## 2026-06-03 Post-Audio Smoke

| Route | Frame | Status | Distinct PCs | Snapshots | Notes |
| --- | ---: | --- | ---: | ---: | --- |
| `sonic-advance-longplay` | 30,000 | pass | 11 | 50 | Extended beach gameplay soak for scanline/HBlank timing and scrolling stability. |
| `mario-kart-longplay` | 20,000 | pass | 10 | 33 | Extended race soak; final frame can include the in-game ghost-data overlay, so keep as stability evidence for now. |
| `metroid-fusion-longplay` | 56,000 | pass | 17 | 35 | Extended boss-room/action-platformer soak using the approved SRAM fixture. |
| `tony-hawk2-longplay` | 27,000 | pass | 9 | 30 | Extended skate tutorial movement/trick soak. |

## 2026-06-03 Expanded Smoke

The expanded smoke adds fresh-start Castlevania routes plus a Flash128K Ruby save-assisted route. The latest stitched artifact is `artifacts/longplay-post-audio-expanded-rollup`, with a contact sheet at `artifacts/longplay-post-audio-expanded-rollup/contact-sheet.png`.

| Route | Frame | Status | Distinct PCs | Snapshots | Notes |
| --- | ---: | --- | ---: | ---: | --- |
| `sonic-advance-longplay` | 30,000 | pass | 11 | 50 | Reused from the first smoke rollup. |
| `mario-kart-longplay` | 20,000 | pass | 10 | 33 | Reused from the first smoke rollup; final frame can include the in-game ghost-data overlay. |
| `metroid-fusion-longplay` | 56,000 | pass | 17 | 35 | Reused from the first smoke rollup. |
| `tony-hawk2-longplay` | 27,000 | pass | 9 | 30 | Reused from the first smoke rollup. |
| `castlevania-aria-longplay` | 32,000 | pass | 10 | 32 | Fresh-start castle movement/combat soak. |
| `castlevania-harmony-longplay` | 34,000 | pass | 10 | 34 | Fresh-start castle movement/combat soak; route budget widened to 3,600 seconds for loaded hosts. |
| `pokemon-ruby-longplay` | 78,000 | pass | 10 | 52 | Flash128K save-assisted map/text/room soak using the approved Ruby save fixture. |

## Next Candidates

- Promote selected longplay frames to baselines only after the final scenes are stable and visually useful.
- Add a dedicated longplay rollup script if stitched historical evidence becomes more useful than rerunning the full manifest.
- Promote or revise final scenes only after longer soak routes prove visually stable and useful enough for baselines.
