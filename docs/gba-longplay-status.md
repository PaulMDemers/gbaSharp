# GBA Longplay Status

The longplay suite is a non-baseline gameplay soak layer. It reuses approved save fixtures, runs longer than the strict final-frame smoke routes, and records snapshots for stability and PC-diversity evidence. These rows intentionally use `baselineRequired=false`; final frames are evidence, not locked regression baselines yet.

- Manifest: `docs/gba-longplay-routes.csv`
- Strict baseline manifest: `docs/gba-longplay-strict-routes.csv`
- Runner wrapper: `scripts/run-longplay-suite.ps1`
- Current full-suite artifact: `artifacts/longplay-suite-full17-20260605`
- Contact sheet: `artifacts/longplay-suite-full17-20260605/contact-sheet.png`
- Current strict tranche artifact: `artifacts/longplay-strict-verify-20260606-tranche2`
- Current strict tranche contact sheet: `artifacts/longplay-strict-verify-20260606-tranche2/contact-sheet.png`
- Historical first strict artifact: `artifacts/longplay-strict-verify-20260606`
- Historical 15-route full-suite artifact: `artifacts/longplay-suite-full15-20260605`
- Historical 7-route full-suite artifact: `artifacts/longplay-suite-full7-20260604`
- Historical first rollup artifact: `artifacts/longplay-post-audio-smoke-rollup`

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

## 2026-06-04 Full Suite

The first fresh full-suite run is `artifacts/longplay-suite-full7-20260604`. It covers both chunks through `scripts/run-longplay-suite.ps1`, writes a combined CSV and contact sheet, and verifies the 7-route longplay manifest with 7/7 pass rows, 0 failures, and 0 low-diversity warnings.

The first attempt completed chunk 1 and most of chunk 2 before the outer shell timeout interrupted the Ruby route. The runner scripts now support `-Resume`, and route child processes are polled with a parent-side timeout and final cleanup guard. Rerunning with `-Resume` skipped completed final frames, completed Ruby at frame 78,000, and rebuilt the full 7-row summary/contact sheet.

| Route | Frame | Status | Distinct PCs | Snapshots | Notes |
| --- | ---: | --- | ---: | ---: | --- |
| `sonic-advance-longplay` | 30,000 | pass | 11 | 50 | Active beach gameplay timing/rendering soak. |
| `mario-kart-longplay` | 20,000 | pass | 10 | 33 | Active race scene; final frame includes the in-game ghost-data overlay. |
| `metroid-fusion-longplay` | 56,000 | pass | 17 | 35 | Boss-room/action-platformer soak. |
| `tony-hawk2-longplay` | 27,000 | pass | 9 | 30 | Extended skate tutorial movement/trick soak. |
| `castlevania-aria-longplay` | 32,000 | pass | 10 | 32 | Fresh-start castle movement/combat soak. |
| `castlevania-harmony-longplay` | 34,000 | pass | 10 | 34 | Fresh-start castle movement/combat soak. |
| `pokemon-ruby-longplay` | 78,000 | pass | 11 | 52 | Flash128K save-assisted room/text/map soak. |

## 2026-06-04 Next Batch Smoke

The next-batch rollup is `artifacts/longplay-nextbatch-rollup-20260604`, with contact sheet `artifacts/longplay-nextbatch-rollup-20260604/contact-sheet.png`. It adds eight non-Pokemon routes and reports 8/8 pass rows with 0 route-threshold diversity warnings after tuning Doom to the verified 18,000-frame window.

This batch is compatibility evidence, not baseline-promotion material yet. Doom, Golden Sun, and Wario Land 4 land in strong active gameplay scenes. GTA, Mario & Luigi, and Mega Man Battle Network are useful longer runtime/dialogue/room stability checks, but their final scenes still need tighter route scripting before promotion. The two F-Zero routes prove longer affine-race runtime stability, but the current scripts end in failure/loss states and should be revisited for stronger driven-race evidence.

| Route | Frame | Status | Distinct PCs | Snapshots | Notes |
| --- | ---: | --- | ---: | ---: | --- |
| `doom-longplay` | 18,000 | pass | 23 | 30 | First-person gameplay; 26,000-frame attempt reached frame 19,794 before the 2,400-second budget. |
| `gta-longplay` | 30,000 | pass | 16 | 30 | Former DMA/generated-code crash anchor; final frame lands in story dialogue; superseded by the active-gameplay retune below. |
| `golden-sun-longplay` | 52,000 | pass | 18 | 40 | Outdoor RPG gameplay scene. |
| `mario-luigi-longplay` | 66,000 | pass | 10 | 44 | Save-assisted EEPROM route; final frame lands on the find-Mario prompt; superseded by the room-scene retune below. |
| `mega-man-battle-network-longplay` | 32,000 | pass | 5 | 32 | Stable low-diversity room/dialogue route; superseded by the room-scene retune below. |
| `wario-land4-longplay` | 30,000 | pass | 19 | 30 | Active platforming scene. |
| `fzero-gp-longplay` | 22,000 | pass | 2 | 36 | Longer race runtime but final frame is a mission-failed state; superseded by the active-race retune below. |
| `fzero-maximum-longplay` | 24,000 | pass | 3 | 40 | Longer race runtime but final frame is a loss state; superseded by the active-race retune below. |

## 2026-06-04 F-Zero Retune

Candidate sweeps for the two F-Zero longplay rows showed better visual evidence before the previous final frames. `artifacts/fzero-gp-longplay-candidates-20260604/contact-sheet.png` shows GP Legend still in active racing at frames 9,000 and 12,000, then mission-failed by frame 15,000. `artifacts/fzero-maximum-early-candidates-20260604/contact-sheet.png` shows Maximum Velocity in an active race/checkpoint window at frame 7,200, then loss-state frames by 8,200 and later. A B-button acceleration probe under `artifacts/fzero-maximum-bprobe-candidates-20260604` did not improve the route.

The manifest now retunes `fzero-gp-longplay` to frame 9,000 and `fzero-maximum-longplay` to frame 7,200. These are shorter than the first next-batch smoke windows, but they are stronger active-play evidence and avoid locking in failure/loss-screen final frames.

The verification artifact is `artifacts/fzero-retuned-longplay-20260604`, with contact sheet `artifacts/fzero-retuned-longplay-20260604/contact-sheet.png`.

| Route | Frame | Status | Distinct PCs | Snapshots | Notes |
| --- | ---: | --- | ---: | ---: | --- |
| `fzero-gp-longplay` | 9,000 | pass | 4 | 30 | Active GP Legend race frame, before mission failure. |
| `fzero-maximum-longplay` | 7,200 | pass | 3 | 24 | Active Maximum Velocity race/checkpoint frame, before loss state. |

## 2026-06-04 GTA Retune

The candidate sweep `artifacts/gta-longplay-candidates-20260604/contact-sheet.png` shows GTA in active top-down gameplay at frames 12,000, 16,000, and 24,000, then story-dialogue screens by frames 28,000 and 30,000. The manifest now retunes `gta-longplay` to frame 24,000 so it remains a longer former-crash-anchor run while ending on active gameplay evidence.

The verification artifact is `artifacts/gta-retuned-longplay-20260604`, with contact sheet `artifacts/gta-retuned-longplay-20260604/contact-sheet.png`.

| Route | Frame | Status | Distinct PCs | Snapshots | Notes |
| --- | ---: | --- | ---: | ---: | --- |
| `gta-longplay` | 24,000 | pass | 14 | 30 | Active top-down gameplay scene before story dialogue. |

## 2026-06-04 Mario & Luigi Retune

The candidate sweep `artifacts/mario-luigi-longplay-candidates-20260604/contact-sheet.png` shows the previous route reaching the find-Mario prompt at frames 36,000 and 58,000, with a clean room scene at frame 48,000. The manifest now retunes `mario-luigi-longplay` to frame 48,000 so the save-assisted EEPROM route ends on room/exploration evidence rather than a dialogue prompt.

The verification artifact is `artifacts/mario-luigi-retuned-longplay-20260604`, with contact sheet `artifacts/mario-luigi-retuned-longplay-20260604/contact-sheet.png`.

| Route | Frame | Status | Distinct PCs | Snapshots | Notes |
| --- | ---: | --- | ---: | ---: | --- |
| `mario-luigi-longplay` | 48,000 | pass | 9 | 40 | Clean room scene before the find-Mario prompt. |

## 2026-06-04 Mega Man Battle Network Retune

The candidate sweep `artifacts/mmbn-longplay-candidates-20260604/contact-sheet.png` shows clean room/control frames at 18,000 and 22,000, then PET dialogue prompt frames by 26,000 and later. The manifest now retunes `mega-man-battle-network-longplay` to frame 22,000 so the stable low-diversity route ends on room evidence instead of dialogue.

The verification artifact is `artifacts/mmbn-retuned-longplay-20260604`, with contact sheet `artifacts/mmbn-retuned-longplay-20260604/contact-sheet.png`.

| Route | Frame | Status | Distinct PCs | Snapshots | Notes |
| --- | ---: | --- | ---: | ---: | --- |
| `mega-man-battle-network-longplay` | 22,000 | pass | 5 | 27 | Clean room scene before the PET dialogue prompt. |

## 2026-06-05 Full 15-Route Suite

The current full-suite artifact is `artifacts/longplay-suite-full15-20260605`, with contact sheet `artifacts/longplay-suite-full15-20260605/contact-sheet.png`. It verifies the full 15-route longplay manifest after the F-Zero, GTA, Mario & Luigi, and Mega Man Battle Network final-frame retunes.

The suite ran in five chunks of three routes and was then rebuilt with `-Resume` to produce a combined report. Results: 15/15 pass rows, 0 failures, 0 low-diversity warnings, and 15/15 `baselineStatus=missing` because these remain non-baseline soak routes.

| Route | Frame | Status | Distinct PCs | Snapshots |
| --- | ---: | --- | ---: | ---: |
| `sonic-advance-longplay` | 30,000 | pass | 11 | 50 |
| `mario-kart-longplay` | 20,000 | pass | 10 | 33 |
| `metroid-fusion-longplay` | 56,000 | pass | 17 | 35 |
| `tony-hawk2-longplay` | 27,000 | pass | 9 | 30 |
| `castlevania-aria-longplay` | 32,000 | pass | 10 | 32 |
| `castlevania-harmony-longplay` | 34,000 | pass | 10 | 34 |
| `pokemon-ruby-longplay` | 78,000 | pass | 11 | 52 |
| `doom-longplay` | 18,000 | pass | 23 | 30 |
| `gta-longplay` | 24,000 | pass | 14 | 30 |
| `golden-sun-longplay` | 52,000 | pass | 18 | 40 |
| `mario-luigi-longplay` | 48,000 | pass | 9 | 40 |
| `mega-man-battle-network-longplay` | 22,000 | pass | 5 | 27 |
| `wario-land4-longplay` | 30,000 | pass | 19 | 30 |
| `fzero-gp-longplay` | 9,000 | pass | 4 | 30 |
| `fzero-maximum-longplay` | 7,200 | pass | 3 | 24 |

## 2026-06-05 Fire Emblem Route

The first post-15-suite expansion route adds `fire-emblem-longplay` for `Fire Emblem (USA, Australia)`. The initial candidate sweep `artifacts/fire-emblem-longplay-candidates-20260605/contact-sheet.png` stalled around name/personal-info setup, so the late setup inputs were revised to finish personal information and advance into the tutorial map. The follow-up candidate sheet `artifacts/fire-emblem-longplay-candidates-v2-20260605/contact-sheet.png` showed usable tactical-map frames, with frame 36,000 selected before the later tutorial prompt.

The verification artifact is `artifacts/fire-emblem-longplay-verify-20260605`, with contact sheet `artifacts/fire-emblem-longplay-verify-20260605/contact-sheet.png`.

| Route | Frame | Status | Distinct PCs | Snapshots | Notes |
| --- | ---: | --- | ---: | ---: | --- |
| `fire-emblem-longplay` | 36,000 | pass | 9 | 40 | Fresh-start tactical map/grid scene with units visible. |

## 2026-06-05 WarioWare Route

The second post-15-suite expansion route adds `warioware-longplay` for `WarioWare, Inc. - Mega Microgame$! (USA)`. The candidate sweep `artifacts/warioware-longplay-candidates-20260605/contact-sheet.png` reached fast microgame flow, but later frames fell into retry/exit screens. The manifest therefore targets frame 18,000, an active microgame window before the retry state.

The verification artifact is `artifacts/warioware-longplay-verify-20260605`, with contact sheet `artifacts/warioware-longplay-verify-20260605/contact-sheet.png`.

| Route | Frame | Status | Distinct PCs | Snapshots | Notes |
| --- | ---: | --- | ---: | ---: | --- |
| `warioware-longplay` | 18,000 | pass | 4 | 30 | Fast low-diversity microgame state before retry screens. |

## 2026-06-05 Full 17-Route Suite

The current full-suite artifact is `artifacts/longplay-suite-full17-20260605`, with contact sheet `artifacts/longplay-suite-full17-20260605/contact-sheet.png`. It verifies the expanded 17-route longplay manifest after adding Fire Emblem and WarioWare to the previous full-suite set.

The suite ran in six bounded chunks and was then rebuilt with `-Resume` to produce a combined report. Results: 17/17 pass rows, 0 failures, 0 low-diversity warnings, and 17/17 `baselineStatus=missing` because these remain non-baseline soak routes. Contact-sheet inspection shows coherent active or representative scenes across the route set, including Sonic's timing-sensitive gameplay, F-Zero active-race frames, Fire Emblem's tactical map, and WarioWare's microgame state.

| Route | Frame | Status | Distinct PCs | Snapshots |
| --- | ---: | --- | ---: | ---: |
| `sonic-advance-longplay` | 30,000 | pass | 11 | 50 |
| `mario-kart-longplay` | 20,000 | pass | 10 | 33 |
| `metroid-fusion-longplay` | 56,000 | pass | 17 | 35 |
| `tony-hawk2-longplay` | 27,000 | pass | 9 | 30 |
| `castlevania-aria-longplay` | 32,000 | pass | 10 | 32 |
| `castlevania-harmony-longplay` | 34,000 | pass | 10 | 34 |
| `pokemon-ruby-longplay` | 78,000 | pass | 11 | 52 |
| `doom-longplay` | 18,000 | pass | 23 | 30 |
| `gta-longplay` | 24,000 | pass | 14 | 30 |
| `golden-sun-longplay` | 52,000 | pass | 18 | 40 |
| `mario-luigi-longplay` | 48,000 | pass | 9 | 40 |
| `mega-man-battle-network-longplay` | 22,000 | pass | 5 | 27 |
| `wario-land4-longplay` | 30,000 | pass | 19 | 30 |
| `fzero-gp-longplay` | 9,000 | pass | 4 | 30 |
| `fzero-maximum-longplay` | 7,200 | pass | 3 | 24 |
| `fire-emblem-longplay` | 36,000 | pass | 9 | 40 |
| `warioware-longplay` | 18,000 | pass | 4 | 30 |

## 2026-06-06 Strict Longplay Baselines

The first strict longplay subset is tracked in `docs/gba-longplay-strict-routes.csv`. It promotes six high-signal rows from the full 17-route soak into exact local baseline checks under `visual-baselines/longplay`: Sonic Advance, Metroid Fusion, Doom, GTA, Wario Land 4, and Fire Emblem. These rows were selected for useful final scenes, route diversity, and compatibility risk coverage; broader soak rows remain non-baseline in `docs/gba-longplay-routes.csv`.

Baselines were seeded from `artifacts/longplay-suite-full17-20260605/deep-gameplay.csv` with `scripts/promote-deep-gameplay-baselines.ps1`, producing `artifacts/longplay-strict-promote-20260606/promotion.csv`. A fresh strict verification then ran through `scripts/run-deep-gameplay-suite.ps1` with `-FailOnBaselineDiff`; the verification artifact is `artifacts/longplay-strict-verify-20260606`, with contact sheet `artifacts/longplay-strict-verify-20260606/contact-sheet.png`.

Results: 6/6 pass rows, 6/6 exact baseline matches, 0 failures, and 0 low-diversity warnings.

| Route | Frame | Status | Baseline | Distinct PCs | Snapshots |
| --- | ---: | --- | --- | ---: | ---: |
| `sonic-advance-longplay` | 30,000 | pass | match | 11 | 50 |
| `metroid-fusion-longplay` | 56,000 | pass | match | 17 | 35 |
| `doom-longplay` | 18,000 | pass | match | 23 | 30 |
| `gta-longplay` | 24,000 | pass | match | 14 | 30 |
| `wario-land4-longplay` | 30,000 | pass | match | 19 | 30 |
| `fire-emblem-longplay` | 36,000 | pass | match | 9 | 40 |

The matching external-reference manifest is `docs/gba-longplay-reference-frames.csv`. The first generated checklist lives at `artifacts/longplay-reference-checklist-20260606/reference-capture-checklist.md`; validation and comparison reported 6/6 missing mGBA PNG captures, which was expected until external captures are added under `reference-captures/mgba/longplay`.

## 2026-06-06 Strict Longplay Tranche 2

The second strict tranche expands `docs/gba-longplay-strict-routes.csv` from 6 to 11 rows by adding Mario Kart, Tony Hawk 2, Castlevania Aria, Castlevania Harmony, and Golden Sun. These rows were selected from the full 17-route suite because their final frames are visually useful and their snapshot diversity stays above the route thresholds.

Baselines were seeded from `artifacts/longplay-suite-full17-20260605/deep-gameplay.csv` with `scripts/promote-deep-gameplay-baselines.ps1`, producing `artifacts/longplay-strict-promote-20260606-tranche2/promotion.csv`. The fresh verification artifact for the new rows is `artifacts/longplay-strict-verify-20260606-tranche2`, with contact sheet `artifacts/longplay-strict-verify-20260606-tranche2/contact-sheet.png`.

Results: 5/5 pass rows, 5/5 exact baseline matches, 0 failures, and 0 low-diversity warnings. Combined strict longplay coverage is now 11 local exact-match rows across the first strict artifact and tranche 2.

| Route | Frame | Status | Baseline | Distinct PCs | Snapshots |
| --- | ---: | --- | --- | ---: | ---: |
| `mario-kart-longplay` | 20,000 | pass | match | 10 | 33 |
| `tony-hawk2-longplay` | 27,000 | pass | match | 9 | 30 |
| `castlevania-aria-longplay` | 32,000 | pass | match | 10 | 32 |
| `castlevania-harmony-longplay` | 34,000 | pass | match | 10 | 34 |
| `golden-sun-longplay` | 52,000 | pass | match | 18 | 40 |

The regenerated external-reference checklist for all 11 strict longplay rows is `artifacts/longplay-reference-checklist-20260606-tranche2/reference-capture-checklist.md`. Validation and comparison currently report 11/11 missing mGBA PNG captures.

## Next Candidates

- Review the remaining non-strict rows for a third tranche: Pokemon Ruby, Mario & Luigi, Mega Man Battle Network, WarioWare, and the two F-Zero routes need stricter final-scene judgment before promotion.
- Revisit F-Zero driving inputs later if we want longer race windows than the current active-frame retune.
- Capture mGBA/no$gba references for the 11 strict longplay rows and run strict pixel comparison.
- Deepen or replace any remaining final scenes that are representative but not ideal gameplay evidence.
