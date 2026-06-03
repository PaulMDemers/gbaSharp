# GBA Longplay Status

The longplay suite is a non-baseline gameplay soak layer. It reuses approved save fixtures, runs longer than the strict final-frame smoke routes, and records snapshots for stability and PC-diversity evidence. These rows intentionally use `baselineRequired=false`; final frames are evidence, not locked regression baselines yet.

- Manifest: `docs/gba-longplay-routes.csv`
- First rollup artifact: `artifacts/longplay-post-audio-smoke-rollup`
- Contact sheet: `artifacts/longplay-post-audio-smoke-rollup/contact-sheet.png`

## 2026-06-03 Post-Audio Smoke

| Route | Frame | Status | Distinct PCs | Snapshots | Notes |
| --- | ---: | --- | ---: | ---: | --- |
| `sonic-advance-longplay` | 30,000 | pass | 11 | 50 | Extended beach gameplay soak for scanline/HBlank timing and scrolling stability. |
| `mario-kart-longplay` | 20,000 | pass | 10 | 33 | Extended race soak; final frame can include the in-game ghost-data overlay, so keep as stability evidence for now. |
| `metroid-fusion-longplay` | 56,000 | pass | 17 | 35 | Extended boss-room/action-platformer soak using the approved SRAM fixture. |
| `tony-hawk2-longplay` | 27,000 | pass | 9 | 30 | Extended skate tutorial movement/trick soak. |

## Next Candidates

- Promote selected longplay frames to baselines only after the final scenes are stable and visually useful.
- Add Castlevania and Pokemon Ruby longplay rows next; both have enough route/save infrastructure to be useful soak targets.
- Add a wrapper script if the manifest grows beyond a handful of rows and needs rollup/contact-sheet generation every run.
