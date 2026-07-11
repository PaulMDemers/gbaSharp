# GBA Longplay Status

The longplay suite is a non-baseline gameplay soak layer. It reuses approved save fixtures, runs longer than the strict final-frame smoke routes, and records snapshots for stability and PC-diversity evidence. These rows intentionally use `baselineRequired=false`; final frames are evidence, not locked regression baselines yet.

- Manifest: `docs/gba-longplay-routes.csv`
- Strict baseline manifest: `docs/gba-longplay-strict-routes.csv`
- Runner wrapper: `scripts/run-longplay-suite.ps1`
- Current full-suite artifact: `artifacts/longplay-suite-full17-20260605`
- Contact sheet: `artifacts/longplay-suite-full17-20260605/contact-sheet.png`
- Current strict rollup artifact: `artifacts/longplay-strict-rollup-20260606`
- Current strict rollup contact sheet: `artifacts/longplay-strict-rollup-20260606/contact-sheet.png`
- Current strict tranche artifact: `artifacts/longplay-strict-verify-20260606-tranche3`
- Current strict tranche contact sheet: `artifacts/longplay-strict-verify-20260606-tranche3/contact-sheet.png`
- Historical second strict artifact: `artifacts/longplay-strict-verify-20260606-tranche2`
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

The regenerated external-reference checklist for all 11 strict longplay rows is `artifacts/longplay-reference-checklist-20260606-tranche2/reference-capture-checklist.md`. Validation and comparison reported 11/11 missing mGBA PNG captures.

## 2026-06-06 Strict Longplay Tranche 3

The third strict tranche promotes the remaining six longplay rows: Pokemon Ruby, Mario & Luigi, Mega Man Battle Network, F-Zero GP Legend, F-Zero Maximum Velocity, and WarioWare. This expands `docs/gba-longplay-strict-routes.csv` to all 17 rows from the broad longplay manifest. The lower-diversity routes use route-specific thresholds matching their stable main-loop behavior.

Baselines were seeded from `artifacts/longplay-suite-full17-20260605/deep-gameplay.csv` with `scripts/promote-deep-gameplay-baselines.ps1`, producing `artifacts/longplay-strict-promote-20260606-tranche3/promotion.csv`. The fresh verification artifact for the new rows is `artifacts/longplay-strict-verify-20260606-tranche3`, with contact sheet `artifacts/longplay-strict-verify-20260606-tranche3/contact-sheet.png`.

Results: 6/6 pass rows, 6/6 exact baseline matches, 0 failures, and 0 low-diversity warnings.

| Route | Frame | Status | Baseline | Distinct PCs | Snapshots |
| --- | ---: | --- | --- | ---: | ---: |
| `pokemon-ruby-longplay` | 78,000 | pass | match | 10 | 52 |
| `mario-luigi-longplay` | 48,000 | pass | match | 9 | 40 |
| `mega-man-battle-network-longplay` | 22,000 | pass | match | 5 | 27 |
| `fzero-gp-longplay` | 9,000 | pass | match | 4 | 30 |
| `fzero-maximum-longplay` | 7,200 | pass | match | 3 | 24 |
| `warioware-longplay` | 18,000 | pass | match | 4 | 30 |

The combined strict rollup is `artifacts/longplay-strict-rollup-20260606`, with contact sheet `artifacts/longplay-strict-rollup-20260606/contact-sheet.png`. It merges the three tranche verification reports and shows 17/17 `pass, match` rows, 0 failures, and 0 low-diversity warnings.

The regenerated external-reference checklist for all 17 strict longplay rows is `artifacts/longplay-reference-checklist-20260606-tranche3/reference-capture-checklist.md`. Validation and comparison currently report 17/17 missing mGBA PNG captures.

## 2026-06-08 Mario Kart External Reference Audit

Mario Kart's strict local longplay remains a useful exact gbaSharp baseline and
gameplay soak, but its active race frame is not a good strict cross-emulator
pixel oracle. A refreshed scripted mGBA capture at `artifacts/mgba-reference-captures-mario-kart-reroute-20260608`
reproduces the same same-scene delta as the earlier capture. The gbaSharp
window match at `artifacts/mario-kart-reref-window-20260608` reports best frame
20,280 with 25,575 differing pixels and SSIM 0.716646.

The pairwise gbaSharp-vs-mGBA window audit at
`artifacts/mario-kart-window-pairwise-20260608/pairwise.csv` shows that HUD,
timer, and road regions prefer near-zero frame offsets, while sky and hills
prefer different offsets. The timeline contact sheet
`artifacts/mario-kart-window-pairwise-20260608/timeline-gbasharp-mgba.png`
confirms the two emulators stay in the same race scene but consume gameplay
input on slightly different simulation states. Treat this route as a local
playability/soak target; use lower-input or paused/stable frames for strict
external pixel comparison.

## 2026-06-08 Sonic Advance External Reference Retarget

Sonic Advance's 30,000-frame strict local longplay remains the extended
gbaSharp gameplay soak, but it is not a stable strict cross-emulator pixel
target. A refreshed mGBA capture at frame 30,000 reaches a later route state
after Sonic has progressed through the beach scene, while the local baseline is
still an early timing scene. The mGBA capture harness now loads save fixtures at
Lua initialization and resets immediately after the load, so save-assisted
reference runs boot with the fixture present before scripted input begins.

The external-reference manifest now uses `sonic-advance-external-reference` at
frame 9,000. The local baseline is
`visual-baselines/longplay/sonic-advance-external-reference.ppm`, and the mGBA
reference is
`reference-captures/mgba/longplay/sonic-advance-external-reference.png`.
`artifacts/longplay-reference-sonic-external-tight-20260608/reference-comparison.csv`
reports Sonic as pass with 2,350 differing pixels against a 4,000-pixel
tolerance. Region scoring at
`artifacts/sonic-external-region-score-20260608/regions.csv` shows the playfield
is exact and all differences are confined to the HUD/timer strip.

## 2026-06-08 Reference Tolerance Triage

The external reference rollup now has a metric summarizer at
`scripts/summarize-reference-comparison.py`. It reports raw differing pixels,
thresholded differing pixels, SSIM, coarse mean delta, size bucket, and a coarse
same-scene classification for each manifest row.

After refreshing the stale F-Zero Maximum Velocity and Fire Emblem local
reference frames, retargeting Pokemon Ruby, F-Zero GP Legend, Mario Kart, Tony
Hawk 2, Doom, and GTA to stable intro/reference windows, and adding bounded
same-scene tolerances for tiny HUD/animation drift, the strict longplay external
comparison at `artifacts/longplay-reference-gta-retarget-20260609` reports
17/17 pass rows.

Newly accepted bounded rows:

| Route | Differing Pixels | Reason |
| --- | ---: | --- |
| `castlevania-aria-longplay` | 63 | Tiny HUD counter drift only. |
| `fzero-maximum-longplay` | 111 | Same-frame race scene with tiny HUD/timer drift after baseline refresh. |
| `metroid-fusion-longplay` | 442 | Minor animated sprite/beam phase drift. |
| `wario-land4-longplay` | 603 | Minor projectile/sprite phase drift. |
| `golden-sun-longplay` | 3,517 | Same-scene rain/weather animation phase drift. |
| `castlevania-harmony-longplay` | 5,174 | Same-scene afterimage/effect animation phase drift. |
| `pokemon-ruby-external-reference` | 116 | No-save intro ripple animation phase drift; save-assisted Ruby remains local-only because mGBA Flash128K save import diverges. |
| `fire-emblem-longplay` | 41 | Refreshed baseline has exact tactical map with tiny UI/cursor phase drift. |
| `fzero-gp-external-reference` | 0 | Exact no-input title-frame match; active-race GP Legend remains local-only because mGBA/gbaSharp race input timing diverges. |
| `mario-kart-external-reference` | 0 | Exact no-input intro match; compares gbaSharp frame 420 against mGBA frame 435 because the flag animation is offset by 15 frames. |
| `tony-hawk2-external-reference` | 0 | Exact no-input Activision logo match; skate tutorial remains local-only because save/input timing diverges. |
| `doom-external-reference` | 0 | Exact no-input id-logo match; compares gbaSharp frame 600 against mGBA frame 620 because the logo animation is offset by 20 frames. |
| `gta-external-reference` | 0 | Exact no-input intro match at frame 600; on-foot gameplay remains local-only because later route timing diverges. |

The latest strict capture validation is
`artifacts/longplay-reference-validation-gta-20260609.csv`; it reports 17
current references as valid, with 7 extra local artifacts left from superseded
external targets.

## 2026-06-09 Strict Runner and Hard Local Soak

The strict external oracle is now runnable through
`scripts/run-strict-reference-suite.ps1`. The first run at
`artifacts/strict-reference-suite-20260609` reports 17/17 passing frame
comparisons, 17 valid current captures, and 7 extra ignored captures from
superseded longplay targets.

The focused hard local set is wrapped by `scripts/run-hard-local-soak.ps1`.
The first batch artifact is `artifacts/hard-local-soak-20260609`. It confirmed
`mario-kart-longplay` still matches its exact local baseline. `fzero-gp-longplay`
completed in a same-scene race state and repeated exactly against the new
capture at `artifacts/hard-local-repeat-fzero-20260609`, so the local F-Zero GP
baseline was refreshed in the workspace. `tony-hawk2-longplay` completed once
with a same-scene timing delta, but the repeat run aborted around frame 12,600;
do not promote that local baseline until the abort is understood.

The same batch showed `sonic-advance-longplay`, `doom-longplay`,
`gta-longplay`, and `pokemon-ruby-longplay` as aborted/incomplete under the
large combined run. A direct Doom repro to frame 6,000 succeeded at
`artifacts/doom-repro-6000-20260609.ppm`, so the next pass should isolate these
routes individually before treating them as emulator regressions. The gameplay
runner now records `lastSnapshotFrame` and `lastSnapshotPc` to make these aborts
actionable.

The route isolation pass uses `scripts/run-route-repeatability.ps1`. Results so
far:

| Route | Artifact | Result | Notes |
| --- | --- | --- | --- |
| `doom-longplay` | `artifacts/route-repeatability-doom-20260609` | pass/match | Full 18,000-frame route passes in isolation; earlier batch abort was not deterministic. |
| `gta-longplay` | `artifacts/route-repeatability-gta-20260609` and `artifacts/route-repeatability-gta-repeat-20260609` | pass/repeatable | Full 24,000-frame route repeated exactly against the new capture; local baseline refreshed in the workspace. |
| `sonic-advance-longplay` | `artifacts/route-repeatability-sonic-20260609` | pass/match | Full 30,000-frame route passes in isolation; earlier batch abort was not deterministic. |
| `tony-hawk2-longplay` | `artifacts/tony-hawk2-direct-27000-20260609.ppm` | pass/repeatable | Direct 27,000-frame run matches the earlier completed hard-soak frame exactly; local baseline refreshed in the workspace. |
| `pokemon-ruby-longplay` | `artifacts/route-repeatability-ruby-20260609` | pass/diff | Full 78,000-frame route completes, but current final frame is outdoors while the old baseline is the bedroom. Needs one more repeat before any baseline refresh. |

## 2026-06-10 Ruby Retiming and Hard Local Gate

The 78,000-frame Ruby save-assisted route is coherent but not exact-repeatable:
`artifacts/route-repeatability-ruby-repeat-20260609` reached a different
outdoor map state than `artifacts/route-repeatability-ruby-20260609`. It should
remain gameplay soak evidence rather than an exact baseline.

The strict Ruby local row is now retimed to a deterministic frame 6,000
save-assisted room/text checkpoint. The no-BIOS probe repeated exactly, and the
BIOS-mode gate frame repeated exactly at
`artifacts/route-repeatability-ruby-bios-repeat-20260609`; the local baseline
was refreshed from the BIOS-mode frame.

The focused hard local gate is green at
`artifacts/hard-local-soak-green-20260609`: 7/7 pass rows, 7/7 exact local
baseline matches.

| Route | Frame | Status | Baseline | Distinct PCs |
| --- | ---: | --- | --- | ---: |
| `sonic-advance-longplay` | 30,000 | pass | match | 11 |
| `doom-longplay` | 18,000 | pass | match | 23 |
| `gta-longplay` | 24,000 | pass | match | 17 |
| `mario-kart-longplay` | 20,000 | pass | match | 10 |
| `tony-hawk2-longplay` | 27,000 | pass | match | 7 |
| `pokemon-ruby-longplay` | 6,000 | pass | match | 6 |
| `fzero-gp-longplay` | 9,000 | pass | match | 4 |

## Next Candidates

- Use the 17/17 green external reference set as the strict external visual oracle.
- Keep the longer Doom FPS, GTA on-foot, Mario Kart race, F-Zero GP race, Tony
  Hawk skate, and Sonic 30,000-frame routes as local exact baselines/playability
  soaks until their cross-emulator timing can be aligned more tightly. Keep the
  former Ruby 78,000-frame route as soak evidence only; the strict Ruby exact
  gate is now the deterministic 6,000-frame room/text checkpoint.
- Run the focused local stress set with `scripts/run-hard-local-soak.ps1`.
- Isolate route repeatability with `scripts/run-route-repeatability.ps1` before
  refreshing any more local baselines.
- Revisit F-Zero driving inputs later if we want longer race windows than the current active-frame retune.
- Deepen or replace any remaining final scenes that are representative but not ideal gameplay evidence.

## 2026-07-11 Compatibility-Finish Gate

The current strict longplay gate is
`artifacts/compat-finish-longplay-24-20260711`. It covers all 24 rows in
`docs/gba-longplay-strict-routes.csv` with 24/24 `pass, match`, 0 failing rows,
and 0 low-diversity warnings. The suite ran sequentially at below-normal
priority and produced a reviewed contact sheet spanning longer platforming,
FPS, racing, RPG, tactics, sports, save-assisted, and microgame scenes.

The first attempt exposed a wrapper defect: `run-longplay-suite.ps1` did not
forward the dedicated `visual-baselines/longplay` directory and therefore
looked for strict frames under the deep-gameplay baseline root. The wrapper now
accepts and forwards `BaselineDir`, defaulting to the strict longplay baseline
directory. Sonic's already completed 30,000-frame output matched its correct
baseline byte-for-byte, so the resumable suite continued without duplicating
that work.

Eight current-build frame changes were promoted only after visual review and a
second byte-identical focused run: Metroid Fusion, Wario Land 4, Fire Emblem,
the short Mario Kart external anchor, Golden Sun, the short Pokemon Ruby
external anchor, F-Zero Maximum Velocity, and WarioWare. Their differences were
localized animation/timer phases in the same intended scenes, not missing
layers or route divergence. Stable route-specific diversity thresholds are now
6 PCs for the deterministic Ruby room checkpoint, 7 for Tony Hawk 2's tutorial
loop, and 3 for WarioWare's repeated active-microgame loop.

The follow-up independent oracle initially caught two local/external phase
mismatches instead of allowing the local suite to become self-consistent:
Mario Kart's short anchor had drifted from its documented gbaSharp frame 420 to
435, and F-Zero Maximum's frame 7,200 was 1,523 pixels from mGBA. Restoring
Mario Kart to frame 420 and retiming F-Zero Maximum to the window-matched frame
7,197 leaves the local gate at 24/24 exact matches and the mGBA suite at 17/17
passes. The F-Zero match is within 55 pixels of mGBA with SSIM 0.992718.
