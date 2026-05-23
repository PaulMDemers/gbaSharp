# Official ROM Curation Test Results

Generated: 2026-05-19

## Collection

- Source folder: `curated_official_gba`
- Manifest: `docs/official-rom-curation.csv`
- ROMs: 300
- Selection policy: official retail directories only; hacks, translations, homebrew, prototypes/betas, unlicensed, virtual-console injects, tools/service carts, and GB/GBC material are excluded.

## Boot Sweep

Report folder: `compat-curated-official-20260519-boot`

| Rows | Boot | Crash | Timeout | Static |
| ---: | ---: | ---: | ---: | ---: |
| 300 | 144 | 0 | 156 | 0 |

The boot sweep used a 60-second wall-clock cap per ROM. The timeout rows are `slow-progress-timeout` rows, not managed crashes or static screens. Many reached 35-110 frames before the wall-clock cap, so this result is mostly a performance/budget signal for the curated stress set rather than a hard compatibility failure count.

## Partial Gameplay Sweep

Report folder: `compat-curated-official-20260519-gameplay`

Completed through curated indexes 1-300. The generic sweep runner now supports process-tree timeouts, `-StartIndex`, `-MergeOnly`, `-FrameStepBudget`, phase-aware de-duplicated merging, and synthetic `process-timeout` rows for chunk-level stalls that interrupt a ROM before all requested phases complete.

| Rows | Boot | Crash | Timeout | Static |
| ---: | ---: | ---: | ---: | ---: |
| 1,200 | 447 | 10 | 743 | 0 |

The gameplay sweep used the full `gameplay` suite and a 180-second wall-clock cap per phase. This was too expensive for the full curated set in one interactive pass; the timeout rows are again dominated by slow-progress budget exits.

Crash rows in the completed gameplay slice:

| Index | Phase | Classification | PC | Title | Game Code |
| ---: | --- | --- | --- | --- | --- |
| 4 | `broad-input` | `invalid-pc` | `0xE1A05444` | `GTA` | `BGTE` |
| 4 | `long-input` | `crash` | `0x0803341A` | `GTA` | `BGTE` |
| 58 | `broad-input` | `invalid-pc` | `0x00000000` | `CRASHSPYRO4` | `B54E` |
| 58 | `long-input` | `invalid-pc` | `0x00000000` | `CRASHSPYRO4` | `B54E` |
| 213 | `start-probe` | `invalid-pc` | `0xE1E100E0` | `RIVIERA` | `BREE` |
| 213 | `broad-input` | `invalid-pc` | `0xE1E100E0` | `RIVIERA` | `BREE` |
| 213 | `long-input` | `invalid-pc` | `0xE1E100E0` | `RIVIERA` | `BREE` |
| 221 | `start-probe` | `invalid-pc` | `0x00E1E1E0` | `RIVIERA` | `BREJ` |
| 221 | `broad-input` | `invalid-pc` | `0x00E1E1E0` | `RIVIERA` | `BREJ` |
| 221 | `long-input` | `invalid-pc` | `0x00E1E1E0` | `RIVIERA` | `BREJ` |

These crash groups are already known from the full archive triage and remain good high-signal compatibility targets.

Post-fix focused rerun:

| Report | Rows | Boot | Crash | Timeout | Notes |
| --- | ---: | ---: | ---: | ---: | --- |
| `compat-riviera-lz77-fix.csv` | 8 | 8 | 0 | 0 | USA and Japan Riviera entries now pass all gameplay phases after the HLE LZ77 invalid-header guard. |
| `compat-gta-dma-fix.csv` | 4 | 4 | 0 | 0 | GTA now passes all gameplay phases after immediate DMA starts were limited to enable rising edges. |
| `compat-crashspyro-register-reset-fix.csv` | 4 | 4 | 0 | 0 | Crash & Spyro Superpack now passes all gameplay phases after BIOS `RegisterRamReset` HLE gained sound/other IO reset handling. |
| `compat-known-crash-fixes-20260520.csv` | 16 | 16 | 0 | 0 | GTA, Crash/Spyro, and Riviera USA/Japan all remain clean in one combined focused gameplay rerun. |
| `compat-process-timeout-reprobe-20260520.csv` | 8 | 8 | 0 | 0 | The previous DEBITIRUHIKA and YUGIOHWCT06 process-timeout rows complete successfully when rerun outside the interrupted chunk. |

The full curated gameplay report above still contains the pre-fix Riviera, GTA, and Crash/Spyro crash rows until the curated set is rerun or patched with replacement rows.

Best-known overlay after focused reruns:

| Report | Rows | Boot | Crash | Timeout | Static |
| --- | ---: | ---: | ---: | ---: | ---: |
| `compat-curated-official-20260520-best-known.csv` | 1,200 | 462 | 0 | 738 | 0 |

The remaining timeout rows in the best-known overlay are all `slow-progress-timeout`, which means the frame buffer was still changing but the interpreter did not finish the requested phase within the current step/time budgets.

Performance profile slice:

| Report | Rows | Avg Steps/Sec | Avg Frames/Sec | CPU | Bus | Scheduler/Video |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `compat-profile-slow-slice-20260520-profile.csv` | 48 | 2,767,254 | 90.83 | 65.6% | 8.4% | 26.1% |
| `compat-profile-slow-slice-20260520-fastread-profile.csv` | 48 | 3,521,665 | 117.18 | 56.4% | 11.0% | 32.7% |
| `compat-profile-slow-slice-20260520-hashunroll-profile.csv` | 48 | 3,752,341 | 124.81 | 57.7% | 10.6% | 31.7% |

The aligned memory read optimization improved the profiled slice by roughly 27% without changing the compatibility classifications. The framebuffer hash unroll brings the total gain to roughly 36% over the original profile. Contra Advance and Doom II are still proportionally scheduler/video-heavy, so later optimization work should keep both CPU throughput and video/render cost visible.

Post-performance curated resweep:

| Report | Scope | Rows | Timeout to Boot | Boot to Timeout | New Crash Rows | Notes |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| `compat-curated-001-038-postperf-complete-20260520.csv` | Completed rows from indexes 1-38 | 152 | 108 | 6 | 2 | Higher frame-step budgets plus the faster build convert many old slow-progress rows to boots. Beyblade rows still hit wall-clock timeout, and Max Payne/Max Advance expose long-input invalid-PC crashes. |
| `compat-maxpayne-long-crash-recheck-20260520.csv` | Indexes 12 and 17 | 8 | n/a | n/a | 2 | Both Max Payne regional rows reproducibly crash at `PC=0x000000FC` in long-input at frame 1440. |
| `compat-maxpayne-decodefix-v3-20260520.csv` | Indexes 12 and 17 | 8 | n/a | n/a | 0 | Tightening the ARM multiply/long-multiply decode masks fixes the Max Payne long-input invalid-PC crash while preserving the KCEJ generated-code long-multiply quirk; both Max Payne regional rows now pass all gameplay phases. |
| `compat-curated-001-038-decodefix-20260520.csv` + `compat-index027-decodefix-20260520.csv` + `compat-curated-028-038-decodefix-20260520.csv` | Completed rows from indexes 1-38 | 152 | n/a | n/a | 0 | Post-decode-fix action/fighting slice: 149 boot rows, 3 Beyblade slow-progress timeout rows, and no crashes. |
| `compat-curated-001-025-decodemask-v3-20260520.csv` + `compat-curated-026-050-decodemask-v3-20260520.csv` + `compat-curated-051-075-decodemask-v3-20260520.csv` | Curated indexes 1-75 | 300 | n/a | n/a | 8 | Post-v3 validation: 289 boot rows, 3 known Bond slow-progress timeout rows, and 8 crash rows clustered in Powerpuff Girls, Muppets, Scooby-Doo Unmasked, and Spy Muppets. |
| `compat-scoobyun-handlerguard-v2-20260520.csv` | Scooby-Doo Unmasked, curated index 50 | 4 | n/a | n/a | 0 | Deferring pending no-BIOS IRQs while execution is still inside the installed IWRAM IRQ handler window fixes the long-input `PC=0x68B3F190` ARM/Thumb context-loss crash. |
| `compat-curated-crashcluster-handlerguard-20260520.csv` | Curated crash-cluster indexes 44, 47, 50, 51 | 16 | n/a | n/a | 6 | Scooby is fully clean and Spy Muppets broad-input now boots. Powerpuff Girls is unchanged; Muppets/Spy long-input now expose later control-flow/function-pointer failures rather than the Scooby no-BIOS context-restore signature. |
| `compat-ppg-biosopenbus-zero-experiment-20260520.csv` | Powerpuff Girls - Mojo Jojo A-Go-Go, curated index 44 | 4 | n/a | n/a | 0 | Zeroing the initial no-BIOS external-BIOS open-bus seed fixes the `PC=0x29F000E0` crash caused by the startup filler value leaking through null-ish reads into object data. |
| `compat-crashcluster-biosopenbus-zero-20260520.csv` | Curated indexes 12, 17, 44, 47, 50, 51 | 24 | n/a | n/a | 3 | Max Payne, Powerpuff Girls, and Scooby remain clean. Remaining crash rows are Muppets broad/long and Spy Muppets long, all in IRQ-mode ARM/Thumb control-flow signatures. |
| `compat-crashcluster-hleintrwait-block-20260520.csv` | Curated indexes 12, 17, 44, 47, 50, 51 partial | 21 completed before manual stop | n/a | n/a | 2 | HLE `IntrWait` now blocks post-SWI code until the requested BIOS interrupt flag is observed. Max Payne and Scooby stayed clean in the completed rows, but Muppets broad/long still crash in the same copied-IWRAM mixer path; focused traces now identify this as Timer0/sound IRQ state corrupting callback slot `03001B64`. |
| `compat-runner-release-smoke-20260520` | Index 58 via `run-compat-sweep.ps1` | 4 | n/a | n/a | 0 | Verifies the sweep runner now executes the Release build when using `--no-build`; Crash/Spyro remains clean. |
| `compat-crashcluster-ifack-20260521.csv` | Curated indexes 12, 17, 44, 47, 50, 51 | 24 | n/a | n/a | 0 | No-BIOS IRQ return now clears the original hardware IF bits after the installed handler returns. Max Payne USA/Europe, Powerpuff Girls, Muppets, Scooby-Doo Unmasked, and Spy Muppets all pass boot/start/broad/long gameplay phases. |
| `compat-crashcluster-biosguard-final-20260521.csv` | Curated indexes 12, 17, 31, 44, 47, 50, 51 | 28 | n/a | n/a | 0 | Adds Legends of Wrestling II to the clean crash-cluster set without regressing Powerpuff Girls: no-BIOS external-BIOS open bus remains zero globally, with a narrow nonzero byte response for Legends' `0x000000C3` startup probe. |
| `compat-crashcluster-cpuset-scratch-20260521.csv` | Curated indexes 12, 17, 31, 44, 47, 50, 51 | 28 | n/a | n/a | 0 | Keeps the focused crash-cluster set clean after no-BIOS `CpuSet` began advancing scratch address registers `r0`/`r1` and returning the last transferred value in `r3`, while preserving `r2`. |
| `compat-gta-watchcoalesce-20260521.csv` | Curated index 4 | 4 | n/a | n/a | 1 | RAM write observers now report normal RAM halfword/word writes as coalesced transfers, giving cleaner traces without changing GTA behavior: boot/start/broad still pass, long-input still crashes at `PC=03008018`. |
| `compat-spymuppets-watchcoalesce-remaining-20260521.csv` | Curated index 51 | 4 | n/a | n/a | 0 | Focused rerun for the crash-cluster rows left after the parent timeout; Spy Muppets remains clean across all gameplay phases. |

Follow-up Muppets traces showed the frame-1288 long-input failure was stale VCount IF re-entry, not a missing Thumb opcode. `03000020` in the copied mixer stored sample byte `01` to `03001B64`; the next branch table load treated that slot as function pointer `03000001`. After no-BIOS IRQ return acknowledges the original hardware IF bits, late-frame traces show separate VBlank/VCount entries returning with `IF=0000` and the long-input row completes.

Scooby-Doo Unmasked (`SCOOBYUN`, curated index 50) is fixed by the no-BIOS IWRAM handler-window guard. Its previous long-input row reached frame 1711 before jumping to `PC=0x68B3F190`; focused traces showed the normal resume site `0x08006600` executed in Thumb state through frames 1700-1710, then frame 1711 returned there in ARM state after IRQ activity inside the IWRAM handler/context restore path. The guard keeps pending IRQs latched until the installed handler window has finished, while preserving intentional nested no-BIOS IRQ behavior covered by unit tests.

Powerpuff Girls - Mojo Jojo A-Go-Go (`PPG MOJOGOGO`, curated index 44) is fixed by changing the initial no-BIOS external-BIOS open-bus seed from `0xE129F000` to `0`. The old seed matched a common post-BIOS filler, but in no-BIOS HLE mode it leaked through null-ish BIOS reads into object setup: a `CpuFastSet`/object-processing path eventually shifted `0xE129F000` into `0x84A7C000`, read unmapped `0xFFFFFFFF`, built `0x29F000E1`, and branched through `BX r3` to invalid `PC=0x29F000E0`. Explicitly seeded BIOS open-bus values are still honored by `MemoryBus.SetBiosOpenBus`.

Legends of Wrestling II (`LEGENDS OF W`, curated index 31) failed all four no-BIOS gameplay phases by branching through a null callback after reading byte `0x000000C3` from the inaccessible BIOS region. The focused guard now returns the post-startup nonzero byte this title expects while leaving the broader no-BIOS open-bus seed at zero for Powerpuff Girls.

Grand Theft Auto Advance (`GTA`, curated index 4) still fails long-input, but the current failure is now pinned to the generated worklist and copied-IWRAM helper rather than the final unsupported word. A `CpuSet` path copies the ROM helper fragment into `03000100`, a later `CpuSet` shifts the fragment at `030001A0` down to `03000100`, and the `0806BD10` `bx r2` veneer calls it. The fragment sees equal `B3B3B3B3` sentinel/worklist values and branches to the original full-function epilogue at `030002DC`; because this fragment was entered without the full prologue, the epilogue pops `lr=03007DD8` and execution falls through stack/IWRAM data until `0x0D200000` is fetched at `03008014`.

The coalesced watch trace `gta-coalesced-watch-20260521.txt` confirms the frame-603 callback table intentionally installs `03000100` at `0201CE98` and `0201CE90`. At frame 1061, the generated producer writes the failing first worklist pair as full words (`03002B00=20000003`, `03002B04=B3B3B3B3`), so the next GTA pass should focus on the producer inputs/calling convention rather than treating the watcher byte noise as corruption.

Process-timeout rows in the completed gameplay slice:

| Index | Phase | Classification | PC | Title | Game Code |
| ---: | --- | --- | --- | --- | --- |
| 229 | `broad-input` | `process-timeout` | `0x0800281C` | `DEBITIRUHIKA` | `AL4E` |
| 229 | `long-input` | `process-timeout` | `0x0800281C` | `DEBITIRUHIKA` | `AL4E` |
| 288 | `start-probe` | `process-timeout` | `0x080F4BBA` | `YUGIOHWCT06` | `BY6P` |
| 288 | `broad-input` | `process-timeout` | `0x080F4BBA` | `YUGIOHWCT06` | `BY6P` |
| 288 | `long-input` | `process-timeout` | `0x080F4BBA` | `YUGIOHWCT06` | `BY6P` |

The `process-timeout` classification means the outer PowerShell process-tree guard interrupted the CLI before that phase could produce its own managed result. It is a runner safety signal, not yet an emulator crash classification.

## Interpretation

- The curated set is intentionally harder than the previous 50-ROM A+ milestone. It includes slow/stress titles, known emulator-bug anchors, regional variants, and special hardware games.
- No full-collection boot crashes were observed.
- The main broad signal from this pass is performance/budget pressure: a 60-second boot cap and 180-second gameplay phase cap are too low for several stress titles on the current interpreter.
- The next targeted pass should focus on grouped failure triage now that the base curated gameplay sweep is complete. Timeout retries should be a separate pass.
