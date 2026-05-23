# GBA A+ Milestone Status

Last updated: 2026-05-23

## Current Compatibility Notes

Sonic Advance's SEGA intro split and gameplay background line artifacts were traced to core scanline DMA timing, not the desktop renderer. HBlank DMA notifications now fire only for visible scanlines, so scanline effect tables are no longer consumed during VBlank before line 0. The Sonic Advance intro and gameplay backgrounds now look correct in manual testing, with regression coverage for visible-only HBlank DMA. DMA3 special/display-start timing is also wired for the display-start window, matching the mGBA timing model for channel 3 special transfers.

The focused crash-cluster rerun `compat-crashcluster-biosguard-final-20260521.csv` is clean: 7 curated stress titles, 28 gameplay rows, 28 boot, 0 crash, and 0 timeout. This includes Legends of Wrestling II, Powerpuff Girls, Muppets, Scooby-Doo Unmasked, and Spy Muppets after the no-BIOS IRQ and BIOS-byte-probe fixes.

The prior high-signal GTA long-input crash is fixed. The bad path looked like a generated-worklist/control-flow issue because the helper at `03000100` later branched into a full-function epilogue and popped `LR=03007DD8`, but the root cause was earlier timing: immediate DMA3 copied the helper before the line-50 VCount/audio service because non-FIFO DMAs were not consuming CPU halt cycles. After all DMA start modes gained halt-cycle accounting, GTA's official aligned real-BIOS `gameplay` suite is 4/4 boot rows with 0 crashes/timeouts.

RAM write observers now coalesce ordinary EWRAM/IWRAM halfword and word writes at their real transfer width. This does not change emulated RAM contents, but it makes DMA and generated-table traces usable. `--stop-on-invalid-pc` also honors `--dump-memory`, and `--disassemble-memory ADDRESS:LENGTH[:arm|thumb]` can decode ARM or Thumb blocks from RAM/ROM at the same stop/snapshot points, so GTA-style crashes can capture generated-code, decoded instructions, and worklist RAM directly at the stop. PC snapshots can now capture deeper stack windows with `--pc-snapshot-stack-words`, and `--poke-frame` can test one-off memory hypotheses without baking ROM-specific behavior into the emulator.

## Probe Compatibility

The current A+ milestone manifest has 50 intended ROM targets:

- Priority 1 top/library titles: 20
- Priority 2 special hardware titles: 4
- Priority 2 technical stress titles: 15
- Priority 3 coverage titles: 11

All intended milestone targets currently pass the compatibility runner's `input` suite at best result level:

- `boot`
- `start-probe`
- `broad-input`

No intended milestone target currently has a best-result crash, static output, or timeout.

## Save Backend Probes

All intended milestone targets also pass save backend probing:

- 46 ROMs with save hardware verify successfully.
- 4 ROMs correctly report `no-save`.
- 0 save probe failures.

Report:

| Report | ROMs | OK | No Save | Failed |
| --- | ---: | ---: | ---: | ---: |
| `save-probe-20260514-a-plus/save-probe.csv` | 50 | 46 | 4 | 0 |

## Gameplay Probes

The compatibility CLI now has a longer `gameplay` suite. It includes the existing `input` phases plus a 1,800-frame `long-input` phase with mixed Start/A/B/D-pad taps.

The full 50-ROM A+ milestone now passes the `gameplay` suite at best-result level:

| Report | ROMs | Rows | Boot | Crash | Timeout | Static |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `compat-gameplay-20260515-a-plus-best.csv` | 50 | 200 | 200 | 0 | 0 | 0 |

Three slow long-input cases needed a 420-second per-phase retry after reaching 1,669-1,714 of 1,800 frames under the normal 300-second cap: `Boktai 2`, `Need for Speed - Most Wanted`, and `F-Zero GP Legend`. All three completed cleanly on retry.

Priority-1 top/library gameplay slices:

| Report | ROMs | Rows | Boot | Crash | Timeout | Static |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `compat-gameplay-20260515-p1-000-003/milestone-best.csv` | 4 | 16 | 16 | 0 | 0 | 0 |
| `compat-gameplay-20260515-p1-004-007/milestone-best.csv` | 4 | 16 | 16 | 0 | 0 | 0 |
| `compat-gameplay-20260515-p1-008-011/milestone-best.csv` | 4 | 16 | 16 | 0 | 0 | 0 |
| `compat-gameplay-20260515-p1-012-015/milestone-best.csv` | 4 | 16 | 16 | 0 | 0 | 0 |
| `compat-gameplay-20260515-p1-016-019/milestone-best.csv` | 4 | 16 | 16 | 0 | 0 | 0 |

Aggregate priority-1 gameplay result: 20 ROMs, 80 phase rows, 80 boot, 0 crash, 0 timeout, 0 static. Some stable menu/gameplay screens classify as `stalled-late`; these are currently treated as non-failing unless paired with a static/crash/timeout status.

Priority-2 and priority-3 gameplay slices:

| Report | ROMs | Rows | Boot | Crash | Timeout | Static |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `compat-gameplay-20260515-p2-hardware/milestone-best.csv` | 4 | 16 | 15 | 0 | 1 | 0 |
| `compat-gameplay-20260515-p2-technical-000-003/milestone-best.csv` | 4 | 16 | 16 | 0 | 0 | 0 |
| `compat-gameplay-20260515-p2-technical-004-007/milestone-best.csv` | 4 | 16 | 16 | 0 | 0 | 0 |
| `compat-gameplay-20260515-p2-technical-008-011/milestone-best.csv` | 4 | 16 | 16 | 0 | 0 | 0 |
| `compat-gameplay-20260515-p2-technical-012-014/milestone-best.csv` | 3 | 12 | 11 | 0 | 1 | 0 |
| `compat-gameplay-20260515-p3-coverage-000-003/milestone-best.csv` | 4 | 16 | 16 | 0 | 0 | 0 |
| `compat-gameplay-20260515-p3-coverage-004-007/milestone-best.csv` | 4 | 16 | 16 | 0 | 0 | 0 |
| `compat-gameplay-20260515-p3-coverage-008-010/milestone-best.csv` | 3 | 12 | 11 | 0 | 1 | 0 |
| `compat-gameplay-20260515-p2-boktai2-retry420/milestone-best.csv` | 1 | 4 | 4 | 0 | 0 | 0 |
| `compat-gameplay-20260515-p2-nfsmw-retry420/milestone-best.csv` | 1 | 4 | 4 | 0 | 0 | 0 |
| `compat-gameplay-20260515-p3-fzero-gp-retry420/milestone-best.csv` | 1 | 4 | 4 | 0 | 0 | 0 |

Earlier representative gameplay probes are also clean at best-result level:

| Report | ROMs | Rows | Boot | Crash | Timeout | Static |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `compat-gameplay-20260514-smoke.csv` | 4 | 16 | 16 | 0 | 0 | 0 |
| `compat-gameplay-20260514-stress-best.csv` | 6 | 24 | 24 | 0 | 0 | 0 |

Aligned real-BIOS curated gameplay sweep after the EEPROM command fix:

| Report | ROMs | Rows | Boot | Crash | Timeout | Static |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `compat-bios-aligned-gameplay-001-025-after-eepromfix-merged-20260521.csv` | 25 | 100 | 99 | 1 | 0 | 0 |
| `compat-bios-aligned-gameplay-001-030-after-eepromfix-merged-20260521.csv` | 30 | 120 | 117 | 1 | 0 | 2 |
| `compat-bios-aligned-gameplay-001-045-after-earlywindow-merged-20260521.csv` | 45 | 180 | 177 | 1 | 0 | 2 |
| `compat-bios-aligned-gameplay-001-075-after-earlywindow-merged-20260521.csv` | 75 | 300 | 297 | 1 | 0 | 2 |

The one crash is `Grand Theft Auto Advance (USA)` (`GTA`, index 4) in generic `long-input`, invalid PC `0x1A3019F4` at frame 1061. The two static rows are Fire Pro Wrestling 1/2 in the 120-frame boot-only phase; the classifier now labels those as `early-window-static`, and both animate under Start/broad/long input. Newly added indexes 46-75 are clean, 120/120 boot rows, including Muppets, Scooby-Doo, Spy Muppets, Crash/Spyro, Boktai-adjacent sensor titles, Harry Potter, and other licensed platform/adventure rows.

Broad retail collection gameplay sweep has started in small chunks to avoid machine pressure:

| Report | ROMs | Rows | Boot | Crash | Timeout | Static |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `compat-retail-other-20260516-0001-0025/compat-all.csv` | 25 | 100 | 100 | 0 | 0 | 0 |
| `compat-retail-other-20260517-0026-0050/compat-all.csv` | 25 | 100 | 100 | 0 | 0 | 0 |
| `compat-retail-other-20260517-0001-0050/compat-all.csv` | 50 | 200 | 200 | 0 | 0 | 0 |
| `compat-retail-other-20260517-0051-0075/compat-all.csv` | 25 | 100 | 100 | 0 | 0 | 0 |
| `compat-retail-other-20260517-0001-0075/compat-all.csv` | 75 | 300 | 300 | 0 | 0 | 0 |
| `compat-retail-other-20260517-0076-0100/compat-all.csv` | 25 | 100 | 100 | 0 | 0 | 0 |
| `compat-retail-other-20260517-0001-0100/compat-all.csv` | 100 | 400 | 400 | 0 | 0 | 0 |
| `compat-retail-other-20260517-0101-0125/compat-all.csv` | 25 | 100 | 97 | 3 | 0 | 0 |
| `compat-retail-other-20260517-0001-0125/compat-all.csv` | 125 | 500 | 497 | 3 | 0 | 0 |
| `compat-retail-other-20260517-0126-0150/compat-all.csv` | 25 | 100 | 100 | 0 | 0 | 0 |
| `compat-retail-other-20260517-0001-0150/compat-all.csv` | 150 | 600 | 597 | 3 | 0 | 0 |
| `compat-retail-other-20260517-0151-0175/compat-all.csv` | 25 | 100 | 100 | 0 | 0 | 0 |
| `compat-retail-other-20260517-0001-0175/compat-all.csv` | 175 | 700 | 697 | 3 | 0 | 0 |
| `compat-retail-other-20260517-0176-0200/compat-all.csv` | 25 | 100 | 100 | 0 | 0 | 0 |
| `compat-retail-other-20260517-0001-0200/compat-all.csv` | 200 | 800 | 797 | 3 | 0 | 0 |
| `compat-retail-other-20260517-0201-0225/compat-all.csv` | 25 | 100 | 98 | 2 | 0 | 0 |
| `compat-retail-other-20260517-0001-0225/compat-all.csv` | 225 | 900 | 895 | 5 | 0 | 0 |
| `compat-retail-other-20260517-0226-0250/compat-all.csv` | 25 | 100 | 100 | 0 | 0 | 0 |
| `compat-retail-other-20260517-0001-0250/compat-all.csv` | 250 | 1,000 | 995 | 5 | 0 | 0 |
| `compat-retail-other-20260517-0251-0275/compat-all.csv` | 25 | 100 | 100 | 0 | 0 | 0 |
| `compat-retail-other-20260517-0001-0275/compat-all.csv` | 275 | 1,100 | 1,095 | 5 | 0 | 0 |
| `compat-retail-other-20260517-0276-0300/compat-all.csv` | 25 | 100 | 100 | 0 | 0 | 0 |
| `compat-retail-other-20260517-0001-0300/compat-all.csv` | 300 | 1,200 | 1,195 | 5 | 0 | 0 |
| `compat-retail-other-20260517-0301-0350/compat-all.csv` | 50 | 200 | 200 | 0 | 0 | 0 |
| `compat-retail-other-20260517-0001-0350/compat-all.csv` | 350 | 1,400 | 1,395 | 5 | 0 | 0 |
| `compat-retail-other-20260517-0351-0450/compat-all.csv` | 100 | 400 | 398 | 2 | 0 | 0 |
| `compat-retail-other-20260517-0001-0450/compat-all.csv` | 450 | 1,800 | 1,793 | 7 | 0 | 0 |
| `compat-retail-other-20260517-0451-0550/compat-all.csv` | 100 | 400 | 400 | 0 | 0 | 0 |
| `compat-retail-other-20260517-0001-0550/compat-all.csv` | 550 | 2,200 | 2,193 | 7 | 0 | 0 |
| `compat-retail-other-20260517-0551-0650/compat-all.csv` | 100 | 400 | 398 | 2 | 0 | 0 |
| `compat-retail-other-20260517-0001-0650/compat-all.csv` | 650 | 2,600 | 2,591 | 9 | 0 | 0 |
| `compat-retail-other-20260518-0651-0750/compat-all.csv` | 100 | 400 | 393 | 7 | 0 | 0 |
| `compat-retail-other-20260518-0751-0850/compat-all.csv` | 100 | 400 | 398 | 2 | 0 | 0 |
| `compat-retail-other-20260518-0001-0850/compat-all.csv` | 850 | 3,400 | 3,382 | 18 | 0 | 0 |
| `compat-retail-full-20260518-0851-3734/cumulative/compat-all.csv` | 3,734 | 14,928 | 14,443 | 483 | 2 | 0 |

This sweep found and fixed a renderer crash in `2 Games in One! - Dr. Mario + Puzzle League (USA)` (`DRMARIO&PONE`, `BZPE`). The game reached the title/menu under `start-probe` but crashed in `broad-input` and `long-input` around frame 725 due to an out-of-range regular-background tile fetch. Regular/affine BG map and tile fetches now wrap inside the 64 KiB BG VRAM window.

The unattended full archive gameplay sweep completed on 2026-05-19 at 02:45:32. Across 3,734 indexed ROM files and 14,928 phase rows, 14,443 rows boot, 483 crash, 2 time out, and 0 classify as static. Failures are dominated by invalid-PC/control-flow rows: 385 invalid-PC classifications, 98 explicit crash classifications, and 2 slow-progress long-input timeouts. The roughest ranges are `2801-2900` with 173 crash rows, `2701-2800` with 64 crash rows, `3301-3400` with 44 crash rows, and `2901-3000` with 35 crash rows. The final grouped review lives in `compat-retail-full-20260518-0851-3734/cumulative/analysis/summary.md`.

The follow-up failure triage lives in `compat-retail-full-20260518-0851-3734/cumulative/triage/summary.md`. It classifies all 485 non-boot rows, including the 2 timeouts: 335 rows are archive noise or special cases such as virtual-console injects, unlicensed ROMs, hacks, or GBA Video entries. The remaining high-signal buckets are 80 invalid-PC control-flow rows, 30 null-PC control-flow rows, 14 bad-dump/odd-header rows, 12 possible CPU/control-flow rows, 10 ARM halfword-transfer/decode rows, 3 likely data-as-Thumb-code rows, and 1 managed runtime bounds crash. The `Grand Theft Auto Advance` managed bounds crash at index `376` was traced to a DMA write that reached the `0x04000800` I/O mirror; shared I/O register peeks/pokes now mirror that address instead of indexing past the I/O register array. After that fix, GTA no longer throws a managed bounds exception and instead exposes the underlying invalid-control-flow/DMA corruption path. Immediate DMA reentrancy is now guarded so DMA-originated writes to another channel's control register cannot start a nested immediate transfer in the middle of the active DMA pass. A focused `KCEJ TP2K4G/S` trace showed that the apparent ARM halfword-transfer kind-3 cluster is generated executable code written through the `0x03008000` IWRAM mirror. The second visit to `0x03000054` executes generated word `E08C62FF`; the decoder now preserves that low-nibble `1111` long-multiply-family quirk while keeping signed byte/halfword load forms in the halfword-transfer decoder. The focused two-ROM boot rerun (`compat-kcej-decode-mask-v3-20260520.csv`) boots both adjacent KCEJ titles with 0 crashes, 0 timeouts, and 0 static rows. The CPU also has a minimal sequential instruction-prefetch model, and the no-BIOS BIOS HLE path now clears selected `IntrWait` mirror flags on return, sets the documented `GetBiosChecksum` side registers, and charges `CpuSet`/`CpuFastSet` source/destination transfer cycles. Next core-bug work should move to focused traces for the remaining retail invalid/null-PC groups such as `Banjo Pilot`, `Riviera`, `Powerpuff Girls`, and `UMUSCLE`.

Earlier named failures remain useful anchors for root-cause work. Index `103`, `Banjo Pilot (USA)` (`BANJO PILOT`, `BAJE`), is a no-BIOS hard failure: gameplay input phases jump to `PC=0` around frame 200, while a direct real-BIOS no-input probe reaches frame 650. Index `208`, `Crash & Spyro Superpack - Spyro - Season of Ice + Crash Bandicoot - The Huge Adventure (USA)` (`CRASHSPYRO4`, `B54E`), is a second no-BIOS/input-timing hard failure: no-input reaches frame 720, but early Start/broad input later enters the game's IRQ dispatcher with a zero VBlank callback and branches to `PC=0` around frame 695. Index `376`, `Grand Theft Auto Advance (USA)` (`GTA`, `BGTE`), is an input-phase hard failure: `boot` and `start-probe` complete, while `broad-input` reaches frame 603 then jumps to invalid PC `0xE1A05444`, and `long-input` reaches frame 648 before an array bounds crash. Index `595`, `Muppets, The - On with the Show! (USA, Europe)` (`MUPPETS SHOW`, `AZME`), is another input-phase hard failure: `boot` and `start-probe` complete, while `broad-input` and `long-input` both reach frame 760 before entering IRQ mode at invalid PC `0xFFF88000`. Index `675`, `Powerpuff Girls, The - Mojo Jojo A-Go-Go (USA)` (`PPG MOJOGOGO`, `APTE`), crashes in `start-probe`, `broad-input`, and `long-input` at invalid PC `0x29F000E0`. Index `703`, `Riviera - The Promised Land (USA)` (`RIVIERA`, `BREE`), crashes in all input phases at invalid PC `0xE1E100E0`. Index `729`, `Scooby-Doo! - Unmasked (USA)` (`SCOOBYUN`, `B25E`), reaches frame 1711 in `long-input` before encountering ARM halfword-transfer store kind `3`, which is suspicious because ARM7TDMI only defines the signed halfword forms for loads. Index `802`, `Spy Muppets - License to Croak (USA)` (`SPY MUPPETS`, `BSSE`), reaches frame 776 in input phases before executing undefined Thumb-range instruction `0xEAE5` in IRQ mode, likely a control-flow symptom rather than a valid missing Thumb opcode. Track GTA, Muppets, Powerpuff Girls, Riviera, Scooby-Doo, Spy Muppets, and the high-density crash ranges from the final archive analysis as the current core-bug queue. A few titles such as `Ace Combat Advance`, `MLB 2K7`, `ATV Thunder Ridge`, `Baldur's Gate - Dark Alliance`, `Beyblade G-Revolution`, `The Bible Game`, `Big Mutha Truckers`, `Boxing Fever`, `Bomberman Tournament`, `Broken Sword`, `Capcom Mini Mix`, `Cars`, `Classic NES Series` entries, `Catwoman`, `Chessmaster`, `CIMA: The Enemy`, `CT Special Forces`, `Dark Arena`, `Defender of the Crown`, `DemiKids`, `Denkiblocks`, `Dexter's Laboratory Chess Challenge`, `Doom II`, `Driver 2 Advance`, `Driver 3`, `Dragon Ball Z Card Game`, `FIFA` entries, `Fear Factor - Unleashed`, `The Fairly OddParents! Clash with the Anti-World`, `Game & Watch Gallery 4`, `Gem Smashers`, `Inspector Gadget`, `Lego Racers 2`, `Madden NFL` entries, `Tomb Raider - Legend`, and `Mary-Kate and Ashley - Girls Night Out` classify as `low-motion`/`stalled-late` under generic input, so they are candidates for manual/game-specific input scripts rather than immediate core fixes.

The no-BIOS boot handoff now initializes the cartridge link register to `0x08000000`, matching the clean-room BIOS handoff pattern used by mGBA. This did not resolve `Banjo Pilot`, but it is a closer reset state for future no-BIOS probes.

`Need for Speed - Most Wanted` needed a longer wall-clock retry for the 1,800-frame phase; it completed cleanly in `compat-gameplay-20260514-nfsmw-retry.csv`.

## Visual Snapshot Probes

The CLI now has a `verify-frame` command and a batch runner for exact framebuffer regression checks. Initial approved baselines live in `visual-baselines` and are driven by `docs/gba-visual-snapshots.csv`.

Initial title-screen, scripted-menu, and save-assisted visual probes pass exactly. Ruby now also has a longer save-backed gameplay row that selects Continue, clears the downstairs TV event, and exits into Littleroot with a small tolerance for wandering NPC animation drift:

| Report | ROMs | Rows | Pass | Diff | Failed |
| --- | ---: | ---: | ---: | ---: | ---: |
| `visual-snapshots-20260515-initial-verify/visual-snapshots.csv` | 5 | 5 | 5 | 0 | 0 |
| `visual-snapshots-20260515-title-plus-scripted/visual-snapshots.csv` | 5 | 10 | 10 | 0 | 0 |
| `visual-snapshots-20260515-title-scripted-save/visual-snapshots.csv` | 5 | 15 | 15 | 0 | 0 |
| `visual-snapshots-20260516-ruby-save-gameplay-tolerant/visual-snapshots.csv` | 1 | 2 | 2 | 0 | 0 |

Save-assisted rows load read-only fixtures from `visual-saves`:

| Fixture | Backend | Size |
| --- | --- | ---: |
| `visual-saves/pokemon-ruby.sav` | Flash128K | 131,072 |
| `visual-saves/sonic-advance.sav` | Flash64K | 65,536 |
| `visual-saves/mario-kart.sav` | Flash64K | 65,536 |
| `visual-saves/metroid-fusion.sav` | SRAM | 32,768 |
| `visual-saves/golden-sun.sav` | Flash64K | 65,536 |

Ruby's fixture is now a progressed in-game Flash128K save. The remaining generic save-assisted fixtures are size-correct load-path coverage until promoted through deeper save scripts.

## Save Roundtrip Probes

`run-save-roundtrip.ps1` now creates scratch saves from scripted runs, measures whether the save differs from erased `0xFF` memory, reloads the save read-only, and verifies a visual baseline.

Representative roundtrip results:

| Report | Rows | Progressed | No Progress | Visual Pass | Diff | Failed |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `save-roundtrip-20260515-initial-verify/save-roundtrip.csv` | 3 | 1 | 2 | 3 | 0 | 0 |
| `save-roundtrip-20260515-explore-verify/save-roundtrip.csv` | 6 | 5 | 1 | 6 | 0 | 0 |
| `save-roundtrip-20260515-consolidated/save-roundtrip.csv` | 8 | 6 | 2 | 8 | 0 | 0 |
| `save-roundtrip-20260516-ruby-manual-save-release/save-roundtrip.csv` | 1 | 1 | 0 | 1 | 0 | 0 |

Progressed backend coverage now includes SRAM, Flash64K, Flash128K, and EEPROM:

| Backend | Progressed Rows |
| --- | ---: |
| SRAM | 1 |
| Flash64K | 2 |
| EEPROM | 3 |
| Flash128K | 1 |

Progressed saves promoted to `visual-saves/approved`:

| Fixture | Backend | Size | Changed Bytes |
| --- | --- | ---: | ---: |
| `visual-saves/approved/metroid-fusion-roundtrip.sav` | SRAM | 32,768 | 192 |
| `visual-saves/approved/pokemon-ruby-root-bedroom.sav` | Flash128K | 131,072 | 56,488 |
| `visual-saves/approved/sonic-advance-roundtrip.sav` | Flash64K | 65,536 | 4,086 |
| `visual-saves/approved/advance-wars-roundtrip.sav` | Flash64K | 65,536 | 1,161 |
| `visual-saves/approved/zelda-minish-roundtrip.sav` | EEPROM | 8,192 | 99 |
| `visual-saves/approved/mario-luigi-roundtrip.sav` | EEPROM | 8,192 | 96 |
| `visual-saves/approved/tony-hawk2-roundtrip.sav` | EEPROM | 8,192 | 32 |

`Pokemon Ruby` now has a true progressed Flash128K manual-save fixture promoted into `visual-saves/pokemon-ruby.sav` and `visual-saves/approved/pokemon-ruby-root-bedroom.sav`. `Mario Kart` still passes read-only visual reload checks but reports `no-progress-pass`, so it needs a deeper save-creation script before it counts as a true progressed in-game save. `Golden Sun` was also explored and did not produce early progressed save data with the current generic script.

Ruby-specific Flash128K work now targets root `Ruby.gba` via the roundtrip manifest `romPath` column. Current Ruby scripts reach the professor intro, name entry, moving truck, the fixed Littleroot transition, the inside of the player's house, the 2F bedroom, clock setup, Mom's post-clock dialogue, and the first manual Start-menu save; notes are in `docs/pokemon-ruby-save-progress.md`.

The Ruby visual manifest now also targets root `Ruby.gba` through an optional `romPath` column. `pokemon-ruby-continue-littleroot` loads the progressed save, selects Continue, clears Mom's forced TV event, and verifies an outdoor Littleroot checkpoint.

Current baseline set:

- `pokemon-ruby-title`
- `sonic-advance-title`
- `mario-kart-title`
- `metroid-fusion-title`
- `golden-sun-title`
- `pokemon-ruby-scripted`
- `sonic-advance-scripted`
- `mario-kart-scripted`
- `metroid-fusion-scripted`
- `golden-sun-scripted`
- `pokemon-ruby-save-assisted`
- `pokemon-ruby-continue-littleroot`
- `sonic-advance-save-assisted`
- `mario-kart-save-assisted`
- `metroid-fusion-save-assisted`
- `golden-sun-save-assisted`

## Reports

| Report | ROMs | Rows | Boot | Crash | Timeout | Static |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `compat-sweep-20260514-a-plus-p1-top8-v2/milestone-best.csv` | 8 | 24 | 24 | 0 | 0 | 0 |
| `compat-sweep-20260514-a-plus-p1-next12/milestone-best.csv` | 12 | 36 | 36 | 0 | 0 | 0 |
| `compat-sweep-20260514-fire-emblem-sacred-stones.csv` | 1 | 3 | 3 | 0 | 0 | 0 |
| `compat-sweep-20260514-a-plus-hardware-v2/milestone-best.csv` | 4 | 12 | 12 | 0 | 0 | 0 |
| `compat-sweep-20260514-a-plus-technical-first8-v2/milestone-best.csv` | 8 | 24 | 24 | 0 | 0 | 0 |
| `compat-sweep-20260514-a-plus-technical-next10/milestone-best.csv` | 7 | 21 | 21 | 0 | 0 | 0 |
| `compat-sweep-20260514-a-plus-coverage-p3/milestone-best.csv` | 11 | 33 | 33 | 0 | 0 | 0 |

The `p1-next12` run was started before the manifest index correction and includes an accidental `Finding Nemo` probe at old index `342`. `Fire Emblem - The Sacred Stones` was rerun separately at corrected index `345` and passes all phases.

## Fixed During This Pass

- Compatibility harness now supports `--frame-step-budget`, which removed false timeout failures on healthy 600/900-frame probes.
- Milestone runner now uses safer small chunks, a less aggressive wall-clock guard, `-SkipItems`, and folded `milestone-best.csv` reports.
- Milestone runner now supports process-tree timeouts for child `dotnet` runs, which prevents orphaned emulator processes during long compatibility sweeps.
- Manifest indexes and game codes were corrected against the CLI's exact ROM ordering and actual cartridge headers.
- ARM/Thumb block data transfers now align transfer addresses to word boundaries. This fixed `Iridion 3D`, which copies generated Thumb code from an unaligned ROM address.
- Neutral/default support exists for Boktai solar GPIO, WarioWare Twisted gyro/rumble GPIO, and Yoshi Topsy-Turvy tilt reads.
- `save-probe` now verifies SRAM, Flash64K, Flash128K, and EEPROM export/load persistence through game-facing bus protocols.
- Cartridge save detection now requires concrete backup library markers such as `EEPROM_V`, `FLASH_V`, `FLASH512_V`, `FLASH1M_V`, and `SRAM_V`, so bare data strings no longer misclassify mixed-marker carts.
- `gameplay` compatibility suite now runs longer mixed-input probes for early gameplay confidence.
- `verify-frame` and `run-visual-snapshots.ps1` now provide exact framebuffer baseline checks with actual/diff image output.
- Visual snapshot rows can now load save files read-only, and `new-visual-save-fixtures.ps1` creates size-correct save fixtures for regression checks.
- `run-save-roundtrip.ps1` now generates scratch saves, detects progressed save data, reloads saves read-only, and verifies visual baselines.
- Save roundtrip coverage now has progressed SRAM, Flash64K, Flash128K, and EEPROM examples.
- Ruby's save-assisted visual fixture now uses a progressed in-game Flash128K save instead of a size-only placeholder.
- `run-visual-snapshots.ps1` now supports optional `romPath` manifest entries, so Ruby visual checks can consistently target the root `Ruby.gba`.
- Added a Ruby progressed-save Continue-to-Littleroot visual checkpoint.
- Single-ROM CLI runs now support long `--max-steps` values beyond the previous 32-bit step ceiling, which is needed for deeper scripted Ruby progression.
- Pokemon Ruby/Sapphire map fades now handle the paused M4A BGM track-mask wait seen when exiting Ruby's opening moving truck.
- Regular and affine background renderers now wrap BG VRAM map/tile fetches instead of indexing past VRAM. This fixes the `Dr. Mario + Puzzle League` broad-input/long-input crash found in the broad retail sweep.
- `compat --error-details` now records full exception stack traces for focused crash reruns, and I/O register peeks/pokes now handle the `0x04000800` mirror used by stray or corrupted DMA writes without throwing managed bounds exceptions. This removes the concrete `Grand Theft Auto Advance` long-input runtime-bounds crash from the triage list; it now fails later as invalid control flow.
- ARM multiply/long-multiply decode masks now distinguish signed byte/halfword post-indexed transfers from true long multiplies. This fixes the `Max Payne`/`Max Advance` long-input invalid-PC crash while retaining the narrow KCEJ generated-code quirk.
- No-BIOS IRQ dispatch now defers pending IRQ delivery while execution is still inside the installed IWRAM handler window after the HLE wrapper has returned. This fixes `Scooby-Doo! - Unmasked` returning to a Thumb resume site in ARM state during long-input testing.
- The no-BIOS external-BIOS open-bus startup seed now starts at zero until explicitly seeded by instruction fetch state. This fixes `Powerpuff Girls - Mojo Jojo A-Go-Go`, where the old `0xE129F000` startup filler leaked through null-ish reads into object data and eventually produced `PC=0x29F000E0`.
- No-BIOS IRQ return now acknowledges the original hardware IF bits after the installed handler has had a chance to inspect them. This fixes the Muppets/Spy Muppets VCount audio-mixer loop: the handler no longer immediately re-enters with stale `IF=0004`, so the copied IWRAM mixer no longer overwrites callback slot `03001B64`. `compat-crashcluster-ifack-20260521.csv` passes Max Payne USA/Europe, Powerpuff Girls, Muppets, Scooby-Doo Unmasked, and Spy Muppets across all four gameplay phases, 24/24 boot rows with no crashes or timeouts.
- Legends of Wrestling II now has a narrow no-BIOS external-BIOS byte-probe guard for address `0x000000C3`. This fixes its null-callback startup path while keeping the global no-BIOS open-bus seed at zero for Powerpuff Girls. `compat-crashcluster-biosguard-final-20260521.csv` passes Max Payne USA/Europe, Legends of Wrestling II, Powerpuff Girls, Muppets, Scooby-Doo Unmasked, and Spy Muppets across all four gameplay phases, 28/28 boot rows with no crashes or timeouts.
- No-BIOS `CpuSet` now advances scratch address registers `r0`/`r1` and leaves the last transferred value in `r3`, while preserving `r2`. This better matches BIOS scratch-register behavior and is covered by unit tests; `compat-crashcluster-cpuset-scratch-20260521.csv` keeps the focused crash cluster clean at 28/28 boot rows.
- Desktop and CLI retail testing now have a cleaner real-BIOS path. The desktop frontend can load a user-provided GBA BIOS from the File menu and auto-detects the local `gba_collection` BIOS path when present. CLI `compare-bios --compare-align-rom-entry` and `run --align-rom-entry` align scripted frame inputs after ROM entry, avoiding false failures caused by BIOS boot animation timing. GTA still exposes the generated-code/control-flow long-input crash under the current aligned real-BIOS compatibility script.
- BIOS-aligned compatibility sweeps can now be run safely through `run-compat-sweep.ps1 -Bios <path> -AlignRomEntry`. The first curated boot smoke (`compat-bios-aligned-curated-boot-smoke-20260521`) exposed a real-BIOS IRQ return bug: Duke Nukem Advance re-executed Thumb `0802B398` after an IRQ, adding one extra `push {r1,r2}` and making `0802B3BE` pop EWRAM data as a return address. The fix uses `LR_irq = Pc + 4` for real-BIOS hardware IRQ entry, matching BIOS handlers that return with `subs pc, lr, #4`, while preserving the existing no-BIOS wrapper behavior.
- BIOS trace formatting now reads real BIOS opcodes when PC is inside BIOS instead of showing stale open-bus values. The CLI also has `--pc-snapshot-csv` for generic register/stack snapshots at `--snapshot-pc` hits, and `--trace-irq` now reports the interrupted return PC instead of the already-entered BIOS vector.
- The post-fix real-BIOS boot smoke (`compat-bios-aligned-curated-boot-smoke-after-irqfix-20260521.csv`) covers curated indexes 1-6 with 6/6 boot, 0 crashes, and 0 timeouts. Duke Nukem Advance reaches frame 120 instead of crashing at frame 5, and Wolfenstein 3D moves from crash to `animated`; Doom, Doom II, GTA, and Metal Slug continue to boot under the aligned BIOS path.
- The broader aligned real-BIOS curated boot sweep now covers indexes 1-200 (`compat-bios-aligned-curated-boot-001-200-after-irqfix-20260521.csv`): 196 boot, 4 static, 0 crashes, and 0 timeouts. Fire Pro Wrestling 1/2 and Chailien animate with Start input in standard reprobes, leaving Aero the Acro-Bat (`AEROACROBATX`, index 95) as the only persistent real-BIOS static case in this slice. Aero's no-BIOS standard probe still produces minimal frame changes, so it is the next focused BIOS-mode startup/video-state target.
- The full 300-title curated aligned real-BIOS boot sweep (`compat-bios-aligned-curated-boot-001-300-after-irqfix-20260521.csv`) is now complete: 295 boot, 4 static, 1 crash, and 0 timeouts. The single crash is Madden NFL 2002 (`MADDEN NFL02`, index 253). Its final unsupported ARM word is paired Thumb code decoded in ARM state; focused snapshots show the root cause earlier, when a real-BIOS IRQ at `08009828` leaves the game with System `sp=03007FA0` instead of the no-BIOS `sp=03006FC4`, so a Thumb epilogue pops zero and branches to BIOS address `0`.
- ARM block data transfer now defers `pc^` CPSR restore until after writeback. This fixes Madden NFL 2002's real-BIOS IRQ return path, where `ldmia sp!, {r0-r12,pc}^` was writing the IRQ stack writeback into the restored System SP bank. The updated focused repro (`compat-madden2002-realbios-irqldmfix-20260521.csv`) boots Madden with 0 crashes, and the rerun 251-300 slice is 50/50 boot with 0 crashes.
- The refreshed 300-title curated aligned real-BIOS boot sweep (`compat-bios-aligned-curated-boot-001-300-after-irqldmfix-20260521.csv`) is now at 296 boot, 4 static, 0 crashes, and 0 timeouts. The original remaining non-boot rows were Fire Pro Wrestling 1/2, Aero the Acro-Bat, and Chailien; focused reprobes showed Fire Pro and Chailien animate with Start input, and Aero is now fixed by the EEPROM command decoder correction below.
- Aero the Acro-Bat's real-BIOS white-screen startup was caused by swapped EEPROM command IDs in the emulator. GBATEK uses `11` for read and `10` for write, while the previous decoder used the reverse; Aero's initial save formatting therefore read idle `1` bits and failed its verify loop. `compat-aero-after-eeprom-fix-20260521.csv` now passes the focused standard suite: 2/2 boot rows, both animated, 0 static, 0 crashes, and 0 timeouts.
- The post-fix EEPROM/save-heavy smoke is clean. `save-probe-all-eeprom-command-fix-20260521.csv` reports 146 requested probes, 125 ok, 21 no-save, and 0 failed. `compat-eeprom-command-fix-slice-20260521.csv` reports 30 real-BIOS ROMs and 60 standard rows, all boot, with 0 static/crash/timeout. The old real-BIOS static trio plus Fire Pro Wrestling 1 now pass focused standard reprobes with a 300-frame boot window and Start input; under the original 120-frame boot-only smoke, Aero and Chailien now show motion while Fire Pro Wrestling 2 remains an early-window false static.
- The first post-EEPROM-fix real-BIOS gameplay slice now covers curated indexes 1-75 with ROM-entry-aligned input timing. `compat-bios-aligned-gameplay-001-075-after-earlywindow-merged-20260521.csv` is 297/300 boot rows; only GTA USA's generic `long-input` phase crashes, and the remaining two static rows are early-window Fire Pro boot-only false statics that animate with input.
- The compatibility classifier now tags short boot-window statics as `early-window-static` while preserving `status=static`. This keeps hard counts stable but separates likely probe-window noise from true persistent static failures.
- ARM `STM` with writeback now stores the updated base value when the base register appears after an earlier register in the transfer list, matching ARM7TDMI behavior. This is covered by a unit test and did not resolve the current GTA long-input trace, which remains a separate generated-code/control-flow target.
- GTA's real-BIOS long-input crash was caused by missing CPU halt cycles for non-FIFO DMA. DMA3 installed the correct helper prologue at `03000100`, but because immediate DMA was effectively free, the install happened before GTA's line-50 VCount/audio service. That service then used real-BIOS `CpuSet` to copy `030001A0 -> 03000100`, stripping the prologue. All DMA start modes now accumulate transfer cycles, the line-50 service runs before the helper install, and the prior GTA long-input script reaches the 360M-step ceiling without an invalid PC.
- GTA now passes the aligned real-BIOS `gameplay` suite directly. `compat-gta-realbios-gameplay-after-dmahalt-20260522.csv` reports 4/4 boot rows with 0 crashes/timeouts, and the action-shooter smoke `compat-bios-aligned-gameplay-001-005-after-dmahalt-20260522.csv` reports 20/20 boot rows across Doom, Doom II, Duke Nukem Advance, GTA, and Metal Slug.
- The post-DMA aligned real-BIOS action-shooter resweep is clean through curated index 15. The 1-15 slices (`compat-bios-aligned-gameplay-001-005-after-dmahalt-20260522.csv`, `006-010`, and `011-015`) total 60/60 boot rows with 0 static, 0 crashes, and 0 timeouts.

## Remaining A+ Work

Probe compatibility is not the same as perfect gameplay. Next milestones:

- Continue the post-crash-cluster triage with the next high-signal retail failures. The Max Payne, Legends of Wrestling II, Powerpuff Girls, Muppets, Scooby-Doo Unmasked, and Spy Muppets focused cluster is now clean. GTA should stay on the no-BIOS HLE startup queue, while real-BIOS desktop/manual testing can move on to broad gameplay checks for Banjo-Pilot, Crash/Spyro, Riviera, and the remaining invalid/null-PC buckets.
- Continue the aligned real-BIOS gameplay sweep in 25- to 50-title chunks across the newly cleaner 1-300 BIOS-aligned boot set, with GTA USA moved back into broad gameplay validation after the DMA halt-cycle fix.
- Add focused performance profiling for slow long-input cases: `Boktai 2`, `Need for Speed - Most Wanted`, and `F-Zero GP Legend`.
- Continue Banjo-Pilot invalid-PC triage. It now detects as EEPROM and passes `save-probe`, but still reaches a frame-200 IWRAM path where loop state at `03007E74` is later consumed as a branch target by `ldmia sp!, {lr}; bx lr`.
- Deepen `Pokemon Ruby` gameplay scripting from Littleroot into the neighbor/rival setup and Professor Birch rescue flow.
- Deepen `Mario Kart` and `Golden Sun` save scripts if they remain desired as representative Flash64K fixtures.
- Deepen the scripted visual snapshots with true save-assisted gameplay scenes and external mGBA/no$gba reference captures.
- Add frontend controls for solar, gyro/tilt, and rumble state.
- Audit audio timing and DMA/FIFO behavior with longer gameplay captures.
