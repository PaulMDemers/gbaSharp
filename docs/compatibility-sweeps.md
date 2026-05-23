# Compatibility Sweeps

The CLI has three compatibility-oriented commands:

```powershell
dotnet run --project src\Gba.Cli -- compat gba_collection --suite standard --output report.csv --summary-output summary.csv
dotnet run --project src\Gba.Cli -- compat-summary report.csv summary.csv
dotnet run --project src\Gba.Cli -- save-probe gba_collection --indexes 3568,563 --output save-probe.csv --summary-output save-probe-summary.csv
dotnet run --project src\Gba.Cli -- verify-frame game.gba --stop-frame 600 --baseline visual-baselines\game.ppm --actual actual.ppm --diff diff.ppm
```

`compat` writes one detail row per ROM per phase. Useful suites:

- `boot`: one boot/title probe.
- `standard`: boot plus repeated Start taps.
- `input`: boot, Start taps, and a broader A/Right input probe.
- `gameplay`: `input` plus a longer 1,800-frame mixed-input probe.

The report records status, classification, frame activity, timing rates (`framesPerMillionSteps`, `cyclesPerFrame`), final PC/state, video registers, object counts, ROM header fields, and optional capture paths.

Use `--profile-output` on performance-focused compatibility runs. This enables profiled stepping and writes a second CSV with wall time, steps/sec, frames/sec, and CPU/bus/scheduler percentages:

```powershell
dotnet run --project src\Gba.Cli -c Release --no-build -- compat curated_official_gba --suite gameplay --indexes 2,3,5 --max-steps 5000000 --frame-step-budget 0 --profile-output compat-profile.csv
.\scripts\analyze-compat-profile.ps1 -ProfilePath compat-profile.csv
```

The first slow-slice profile (`compat-profile-slow-slice-20260520-profile.csv`) covered 12 curated stress titles and 48 gameplay phases. It averaged about 2.77M steps/sec with time split across CPU 65.6%, bus 8.4%, and scheduler/video 26.1%; Contra Advance and Doom II were the scheduler-heavy outliers, while the rest were mostly CPU-dispatch bound.

After adding fast aligned `Read16`/`Read32` paths for normal memory, the same slice (`compat-profile-slow-slice-20260520-fastread-profile.csv`) averaged about 3.52M steps/sec, a roughly 27% throughput gain. The fast path keeps bytewise fallbacks for BIOS open bus, EEPROM/flash, cartridge GPIO/tilt hardware, ROM past-end reads, and the Ruby/Sapphire audio guard addresses.

After unrolling the compat framebuffer FNV hash loop, the same slice (`compat-profile-slow-slice-20260520-hashunroll-profile.csv`) averaged about 3.75M steps/sec. That is roughly 36% above the original profile and 7% above the fast-read-only profile, with the same compatibility classifications.

By default `compat` budgets up to 150,000 CPU steps per requested frame, so loop-heavy but correctly advancing games are not misreported as `max-steps<Nf` timeouts under the default 5M step cap. Pass `--frame-step-budget 0` for a deliberately strict/fast smoke run, or provide a larger `--max-steps`/`--frame-step-budget` pair for long manual probes.

Use `--error-details` on focused compatibility reruns when the compact `error` column is not enough. It stores the full exception type and stack trace in the CSV for crash rows, which is useful for reducing grouped archive failures to exact source lines without making every full-library report huge.

The `compat-kcej-decode-mask-v3-20260520.csv` focused rerun covers the KCEJ generated-code failure around `0x03000054`. The ARM7TDMI decoder must accept valid multiply/long-multiply encodings and the observed generated-code low-nibble `1111` long-multiply quirk while still routing signed byte/halfword load forms through the halfword-transfer decoder.

The `compat-maxpayne-decodefix-v3-20260520.csv` focused rerun covers the Max Payne and Max Payne Advance long-input invalid-PC failure. The crash was caused by `LDRSB r3, [r1], #1` being misdecoded as a long multiply because the multiply masks ignored bits 6-5. Both Max Payne regional rows now complete all gameplay phases.

`--trace-irq` honors `--trace-frames`, which keeps late-frame IRQ probes usable for long-running retail failures. This was added during the Scooby-Doo Unmasked trace, where early boot IRQs otherwise exhaust the trace limit before the failing frame.

The `compat-riviera-lz77-fix.csv` focused rerun covers the Riviera USA/Japan invalid-PC failure. The crash was caused by HLE `LZ77UnCompWram` accepting an invalid/open-bus header and decompressing until the destination wrapped into IWRAM stack memory. The no-BIOS HLE path now ignores LZ77 headers whose type byte is not `0x10`.

The `compat-gta-dma-fix.csv` focused rerun covers the Grand Theft Auto Advance broad/long-input invalid-PC failure. The crash was caused by restarting an already enabled sound FIFO DMA channel as an immediate DMA when its control word was rewritten. DMA immediate transfers now start only on the enable rising edge.

The `compat-crashspyro-register-reset-fix.csv` focused rerun covers the Crash & Spyro Superpack broad/long-input invalid-PC failure. The crash was caused by the no-BIOS `RegisterRamReset` HLE preserving stale interrupt/timer/DMA IO state during the embedded-game handoff; sound and other IO reset flags now clear the relevant registers and notify timer/DMA observers.

The `compat-process-timeout-reprobe-20260520.csv` focused rerun covers the prior DEBITIRUHIKA and YUGIOHWCT06 process-timeout rows. Both titles complete all gameplay phases when rerun outside the interrupted chunk, so those rows are runner artifacts rather than current emulator crashes.

For focused crash debugging with `run`, combine `--trace-tail` with targeted watch ranges instead of enabling full tracing:

```powershell
dotnet run --project src\Gba.Cli -c Release -- run gba_collection\game.gba --max-steps 5000000 --trace-tail 80 --watch-write-range 03008040:03008080
dotnet run --project src\Gba.Cli -c Release -- run gba_collection\game.gba --max-steps 5000000 --stop-pc 03000054 --stop-pc-hit 2 --trace-frames 15:16 --watch-write-range 03008040:03008080 --watch-limit 100 --dump-memory 03000040:40 --dump-memory 03008040:40
dotnet run --project src\Gba.Cli -c Release -- run gba_collection\game.gba --max-steps 5000000 --snapshot-pc 030003D8 --snapshot-pc-limit 4 --dump-memory 030003C0:70
```

If the run reaches `--stop-pc` at the requested hit count, the CLI prints requested dumps, registers, and state before returning success. If it crashes, it prints the buffered final instructions, registers, requested memory dumps, and state before returning exit code `4`. This keeps exception traces compact enough for retail failure triage.
Use `--watch-limit` to cap noisy watch streams after frame and address filters are applied; `0` keeps the previous unlimited behavior. Use `--snapshot-pc` when a routine needs several non-stopping register and memory captures at the same PC.

## Visual Snapshots

`verify-frame` runs a ROM with the normal debug/input options, captures the selected framebuffer, compares it to a binary PPM baseline, and can emit both the actual frame and a magenta-on-dark diff image. By default it expects an exact match:

```powershell
dotnet run --project src\Gba.Cli -- verify-frame gba_collection\game.gba --stop-frame 600 --max-steps 90000000 --baseline visual-baselines\game.ppm --actual actual.ppm --diff diff.ppm
```

Use `--write-baseline` only when intentionally approving a new baseline:

```powershell
dotnet run --project src\Gba.Cli -- verify-frame gba_collection\game.gba --stop-frame 600 --max-steps 90000000 --baseline visual-baselines\game.ppm --write-baseline
```

The batch runner uses `docs\gba-visual-snapshots.csv` and resolves ROM indexes against the same sorted `gba_collection` ordering used by compatibility sweeps:

```powershell
.\scripts\run-visual-snapshots.ps1 -OutputDir visual-snapshots-run
.\scripts\run-visual-snapshots.ps1 -OutputDir visual-snapshots-baselines -UpdateBaselines
```

Each run writes `visual-snapshots.csv`, actual PPMs, and diff PPMs.

Manifest rows can include `phase`, `inputScript`, `saveFile`, and `expectedScene`. `inputScript` uses the same frame input script syntax as the CLI, so a visual row can drive title/menu input before capturing a later frame.

Rows with `saveFile` are loaded read-only by default from the batch runner. Pass `-WriteSaveFiles` only when intentionally regenerating save data.

Generate size-correct visual save fixtures with:

```powershell
.\scripts\new-visual-save-fixtures.ps1 -Overwrite
```

The generated fixtures live in `visual-saves` and are useful for proving save-load plumbing in visual checks. They are not a substitute for progressed in-game saves.

For true save roundtrip work, use `docs\gba-save-roundtrip.csv` with:

```powershell
.\scripts\run-save-roundtrip.ps1 -OutputDir save-roundtrip-run
.\scripts\run-save-roundtrip.ps1 -OutputDir save-roundtrip-baselines -UpdateBaselines
.\scripts\run-save-roundtrip.ps1 -OutputDir save-roundtrip-required -RequireProgress
```

The roundtrip runner creates a scratch save from a scripted run, records how many bytes differ from erased `0xFF` save memory, then reloads the scratch save read-only and verifies a visual baseline. Rows with `no-progress-*` are useful load-path checks but still need deeper input scripts before they count as true progressed in-game saves.

Manifest rows may optionally use `romPath` to target a specific ROM file instead of resolving by collection index. Ruby-specific save work uses this to target the root `Ruby.gba`.

For the full library, use the chunked runner:

```powershell
.\scripts\run-compat-sweep.ps1 -RomRoot gba_collection -Suite boot -ChunkSize 10 -MaxChunks 5 -Resume
```

The runner creates chunk CSVs, merges them into `compat-all.csv`, and writes `compat-summary.csv`. It treats emulator crashes/timeouts as data, so chunk exit code `4` does not stop the sweep. Use `-Resume` to continue an interrupted run without repeating completed chunks.

Defaults are intentionally desktop-safe: `boot` suite, 10-ROM chunks, a short pause between chunks, and below-normal process priority. Increase `-ChunkSize`, `-MaxSteps`, or switch to `-Suite standard` only when the machine is free for a longer batch. Use `-MaxChunks` for short controlled passes.

For long or slow gameplay batches, use process-tree guardrails and small chunks:

```powershell
.\scripts\run-compat-sweep.ps1 -RomRoot curated_official_gba -Suite gameplay -StartIndex 215 -ChunkSize 1 -MaxSeconds 180 -ProcessTimeoutSeconds 700 -RetryTimeoutSteps 0 -NoCapture -Resume
.\scripts\run-compat-sweep.ps1 -RomRoot curated_official_gba -OutputDir compat-curated-official-20260519-gameplay -MergeOnly
```

`-ProcessTimeoutSeconds` and `-RetryProcessTimeoutSeconds` kill stalled child `dotnet` process trees instead of leaving emulator runs behind. If a process timeout interrupts a chunk after only some phases were written, the runner appends `process-timeout` rows for the missing phases so the merged matrix stays complete and the next resumed pass can move forward. `-MergeOnly` rebuilds merged reports from completed chunks. Merging is keyed by `index|phase`, so changing chunk sizes during a resumed run does not double-count overlapping rows.

The sweep runner defaults to `Release` when invoking `dotnet run --no-build`. Override with `-Configuration Debug` only when intentionally testing Debug artifacts; otherwise stale Debug builds can produce false regressions against fixed Release behavior.

The `compat-runner-release-smoke-20260520` smoke run verifies the corrected runner configuration against curated index 58. Crash/Spyro remains clean through all four gameplay phases when invoked through the script.

To keep normal chunks light while still resolving near-miss timeouts, enable timeout retries:

```powershell
.\scripts\run-compat-sweep.ps1 -RomRoot gba_collection -MaxChunks 5 -RetryTimeoutSteps 25000000 -RetryChunkSize 3 -Resume
```

This writes the original `compat-all.csv`, a retry-only `compat-timeout-retries.csv`, and `compat-best.csv` where successful retry rows replace original timeout rows.

Timeout classifications distinguish "still moving" from "stuck":

- `slow-progress-timeout`: video is changing but the normal budget was too small.
- `very-slow-timeout`: video is changing, but at a very low frame rate per CPU step.
- `stalled-timeout`: video appeared but stopped changing.
- `no-video-timeout`: no frame was produced before the budget expired.
- `process-timeout`: the outer sweep runner killed a stalled CLI process before the phase returned its own managed result.

Failure captures are enabled by default for `crash`, `static`, `no-video`, and `timeout`. Disable them with `-NoCapture`.

For the long retail gameplay archive sweep, use the dedicated unattended runner:

```powershell
.\scripts\run-full-archive-gameplay-sweep.ps1 -StartIndex 851 -EndIndex 3734 -OutputRoot compat-retail-full-run -Resume
```

This runner uses the current retail gameplay parameters (`gameplay`, 5-ROM internal chunks, 100-ROM blocks, `--frame-step-budget 150000`, 300M max steps, and 180 seconds per phase), treats emulator crash exit code `4` as data, and writes resumable state after every 100-ROM block. It merges the existing `1-850` seed report by default, then writes:

- `blocks/compat-XXXX-YYYY/compat-all.csv`
- `blocks/compat-XXXX-YYYY/failures.csv`
- `cumulative/compat-all.csv`
- `cumulative/compat-summary.csv`
- `cumulative/failures.csv`
- `cumulative/failures.md`
- `state.json`
- `status.txt`

Use `-PlanOnly` to verify the range without running, `-MaxBlocks 1` for a smoke pass, and `-NoCapture` if disk pressure matters more than failure screenshots.

After or during a full archive run, generate grouped review tables with:

```powershell
.\scripts\analyze-full-archive-sweep.ps1 -ReportPath compat-retail-full-run\cumulative\compat-all.csv
```

The analyzer writes `analysis/summary.md` plus CSVs grouped by ROM, error, phase, classification, save type, and index block. This is the preferred starting point for choosing the next core-bug target after the archive sweep completes.

## Recent Focused Compatibility Fixes

- `compat-maxpayne-decodefix-v3-20260520.csv`: Max Payne USA/Europe both pass all four gameplay phases after tightening ARM multiply/long-multiply decode masks while preserving the known KCEJ generated-code quirk.
- `compat-kcej-handlerguard-collection-20260520.csv`: full-archive KCEJ boot anchors 2503/2504 still boot after the decode and no-BIOS IRQ changes.
- `compat-scoobyun-handlerguard-v2-20260520.csv`: Scooby-Doo Unmasked now passes all four gameplay phases. The fix defers no-BIOS IRQ delivery while the CPU is executing inside the installed IWRAM IRQ handler window after the HLE wrapper has already returned.
- `compat-curated-crashcluster-handlerguard-20260520.csv`: focused rerun of curated crash-cluster indexes 44/47/50/51 shows Scooby fixed and Spy Muppets broad-input improved to boot; remaining crashes are Powerpuff Girls invalid-PC, Muppets IWRAM ARM/Thumb function-pointer state, and Spy Muppets long-input control flow.
- `compat-ppg-biosopenbus-zero-experiment-20260520.csv`: Powerpuff Girls - Mojo Jojo A-Go-Go now passes all four gameplay phases after the initial no-BIOS external-BIOS open-bus seed was changed to zero.
- `compat-crashcluster-biosopenbus-zero-20260520.csv`: combined Max Payne, Powerpuff, Scooby, and Muppets crash-cluster rerun now has 21/24 boot rows and 3 remaining crashes, down from the prior 6 crash rows.
- Muppets follow-up traces narrowed the remaining broad/long crash to the VCount-driven sound/mixer path, not a missing Thumb opcode. The old broad-input frame-767 failure was fixed by keeping no-BIOS `IntrWait` blocked while still allowing IRQ handlers to run. The later long-input failure reached frame 1288: the copied IWRAM mixer updated `030015C4` as its output cursor, then a stale pending VCount IF bit immediately re-entered the handler and started another full `0x130` sample mix before the prior buffer window had wrapped, so the mixer wrote byte `01` over callback slot `03001B64` and branched through `03000001`. No-BIOS IRQ now exposes BIOS-style `LR_irq=00000138`, SWI/DMA/IRQ tracing honors `--trace-frames`, and HLE waits are scheduler-event-aware instead of fixed 1024-cycle chunks.
- `compat-crashcluster-ifack-20260521.csv`: Max Payne USA/Europe, Powerpuff Girls, Muppets, Scooby-Doo Unmasked, and Spy Muppets now pass all four gameplay phases, 24/24 boot rows with 0 crashes and 0 timeouts. The fix is no-BIOS IRQ post-handler hardware IF acknowledgement: the wrapper preserves pending IF bits for the installed handler to inspect, then clears those original hardware IF bits on return. This stops Muppets/Spy Muppets from immediately re-entering the same VCount IRQ and rerunning the mixer while the prior mix window is still active.
- `compat-crashcluster-biosguard-final-20260521.csv`: Max Payne USA/Europe, Legends of Wrestling II, Powerpuff Girls, Muppets, Scooby-Doo Unmasked, and Spy Muppets pass all four gameplay phases, 28/28 boot rows with 0 crashes and 0 timeouts. Legends needed a narrow no-BIOS external-BIOS byte guard for its `0x000000C3` startup probe; the global no-BIOS BIOS open-bus seed remains zero so Powerpuff Girls stays clean.
- `compat-crashcluster-cpuset-scratch-20260521.csv`: the same 7-title crash cluster remains clean, 28/28 boot rows with 0 crashes and 0 timeouts, after no-BIOS `CpuSet` began advancing BIOS scratch address registers `r0`/`r1` and leaving the last copied/fill value in `r3`.
- GTA long-input remains the next focused target. The current trace (`gta-03001780-trace-20260521.txt`) shows the failure is not a missing `0x0D200000` ARM opcode: a self-modifying `CpuSet` path copies a ROM fragment from `08067A54` to `03000100`, later copies the fragment at `030001A0` down to `03000100`, then calls it through the `0806BD10` `bx r2` veneer. The fragment compares equal sentinel/worklist values (`B3B3B3B3`) and branches to the original full-function epilogue at `030002DC`, which pops `lr=03007DD8` from the caller stack and falls through stack/IWRAM data until the unsupported word is fetched at `03008014`.
- `compat-gta-watchcoalesce-20260521.csv`: GTA is unchanged after RAM write observers were coalesced to report normal halfword/word writes at their transfer width. Boot, start-probe, and broad-input still boot; long-input still crashes after the copied helper returns through stack/IWRAM data. The cleaner watch trace (`gta-coalesced-watch-20260521.txt`) confirms the callback table intentionally installs `03000100` at frame 603 and the bad frame-1061 worklist entry is written as full words by the generated producer: `03002B00=20000003`, `03002B04=B3B3B3B3`.
- `compat-crashcluster-watchcoalesce-20260521.csv` completed 26/28 crash-cluster rows before its parent command timeout left child workers running; all completed rows were `boot`. The remaining Spy Muppets focused rerun (`compat-spymuppets-watchcoalesce-remaining-20260521.csv`) is clean, 4/4 boot rows with 0 crashes and 0 timeouts.
- `compare-bios` now compares sampled no-BIOS and real-BIOS frame state, with `--compare-align-rom-entry` resetting frame/input timing after the BIOS hands off to ROM. Earlier aligned GTA comparison windows were useful for separating BIOS handoff timing from no-BIOS HLE state, but the current aligned real-BIOS compatibility long-input script still reproduces the GTA control-flow failure at frame 1061.
- `gta-realbios-worklist-watch-20260522.txt` and `gta-realbios-producer-entry-snapshots-20260522.csv` narrow the current aligned real-BIOS GTA long-input crash to the generated producer at `03001740`/`03001780`. At frame 1061, the producer enters with `r12=B3B3B3B3`, writes `03002B00=20000003` and `03002B04=B3B3B3B3`, then the dispatcher calls helper `03000100`. The helper stores the same sentinel into its state slot, later compares `r1` and the reloaded state value as equal, branches to copied epilogue `030002DC`, pops `lr=03007DD8`, and falls through stack/IWRAM data to invalid PC `1A3019F4`. Removing the late Down/Left taps does not avoid the crash; extending only the broad-input script did not reproduce the invalid PC before hitting the run step ceiling.
- `--stop-on-invalid-pc` now honors `--dump-memory`, so focused crash runs can capture relevant RAM directly at the invalid-PC stop. `--disassemble-memory ADDRESS:LENGTH` also dumps ARM disassembly from RAM at the same stop/snapshot points. `gta-realbios-invalid-dump-after-cli-fix-20260522.txt` verifies raw dumps for `03001740` and `03002B00`; `gta-realbios-disasm-producer-20260522.txt` and `gta-realbios-disasm-helper-20260522.txt` decode the producer/helper blocks.
- GTA follow-up traces after the ARM disassembly tooling confirm the helper failure in decoded form: `030001F8 beq 0x030002DC`, `030002DC add sp, sp, #0x40`, `030002E0 ldmia sp!, {...,lr}`, then `030002E4 bx lr` with `lr=03007DD8`. `gta-realbios-helper-stack-snapshots-20260522.txt` shows the prepared restore slot already contains `03007DD8` before the pop. ARM `STM` base-in-list writeback was corrected and unit-covered during this pass, but `gta-realbios-after-stm-base-fix-20260522.txt` shows the GTA long-input path is unchanged.
- The next GTA pass added Thumb memory disassembly (`--disassemble-memory ADDRESS:LENGTH:thumb`) and deeper PC snapshot stack capture (`--pc-snapshot-stack-words`). `gta-realbios-thumb-caller-disasm-20260522.txt` decodes the Thumb dispatcher at `0800DB64`: it loads callback table base `0201CE90`, reads `0201CE90=03000100`, and calls through the `0806BD10` `bx r2` veneer. `gta-helper-gameplay-stackwords-20260522.csv` captures the failing helper entry with `sp=03007D38` and `sp74=03007DD8`, matching the later popped `lr`. A pre-crash probe (`gta-helper-early-stackwords-20260522.csv`) shows no earlier calls to `03000100` before frame 700, so this helper entry is first exercised in the late generated-worklist path.
- `gta-callback-table-install-watch-20260522.txt` confirms the callback table is intentionally populated at frame 603: `0201CE90` and `0201CE98` both become `03000100`, while adjacent entries point at wrapped/full prologue helpers such as `030006D4`. This makes the remaining GTA bug look less like table corruption and more like a caller/stack-context mismatch or an upstream generated-worklist/state issue that makes the internal `03000100` entry take its common full-function epilogue with a stale restore frame.
- `--trace-tail` now honors `--trace-frames`, avoiding expensive all-instruction formatting when collecting focused late-frame tails.
- `--poke-frame FRAME:ADDRESS:VALUE[:8|16|32]` was added as a focused diagnostic hook. `gta-stackslot-producer-caller-watch-20260522.txt` shows the bad restore slot `03007DAC` being reused normally in frame 1060 and last receiving `03007DD8` from ROM-side stack activity before the generated helper consumes it. The decisive experiment is `gta-poke-ce90-wrapper-late-watch-v2-20260522.txt`: forcing only `0201CE90` from raw helper `03000100` to wrapped helper `030006D4` at frame 1061 avoids the invalid-PC crash and runs to the step ceiling. That confirms the late GTA crash is specifically the raw/no-prologue helper path being called with work items that take the shared full-function epilogue, not a general stack write corruption.
- The next GTA pass found the prologue loss was a DMA timing bug. The ROM source at `08067A54` starts correctly with `E92D5FFF; E24DD040`, and DMA3 copies it to `03000100` at frame 603 (`gta-fresh-dma-trace-20260522.txt`). Before the fix, only FIFO DMAs consumed CPU halt cycles, so the CPU reached the helper-install DMA too early; the line-50 VCount/audio service then ran afterward and real-BIOS `CpuSet` at `00000B76` copied `030001A0 -> 03000100`, stripping the prologue. DMA now accumulates halt cycles for all start modes. `gta-frame603-after-dma-cycle-fix-20260522.txt` shows the VCount service running before the helper install, and `gta-long-script-after-dma-cycle-fix-20260522.txt` runs the previous long-input crash script to the 360M-step ceiling with no invalid PC.
- The post-fix aligned real-BIOS gameplay repro is clean for GTA. `compat-gta-realbios-gameplay-after-dmahalt-20260522.csv` passes all four phases, including the previously crashing `long-input` row. The nearby action-shooter regression slice `compat-bios-aligned-gameplay-001-005-after-dmahalt-20260522.csv` is also clean: Doom, Doom II, Duke Nukem Advance, GTA, and Metal Slug total 20/20 boot rows with 0 static, 0 crashes, and 0 timeouts.
- The post-DMA real-BIOS action-shooter resweep now covers curated indexes 1-15. `compat-bios-aligned-gameplay-006-010-after-dmahalt-20260522.csv` and `compat-bios-aligned-gameplay-011-015-after-dmahalt-20260522.csv` add another 40/40 clean rows, so the combined 1-15 set is 60/60 boot rows with 0 static, 0 crashes, and 0 timeouts. This includes the prior GTA and Max Payne long-input failure anchors.
- `compat-bios-aligned-curated-boot-smoke-20260521` was the first safe chunked real-BIOS compatibility sample using `run-compat-sweep.ps1 -AlignRomEntry`. It covered curated indexes 1-6 boot-only: 4 boot, 2 crash. The crashes formed a real-BIOS IRQ return cluster: Duke Nukem Advance re-executed Thumb `0802B398` (`push {r1,r2}`) after an IRQ, creating an 8-byte stack-depth error before `0802B3BE`; Wolfenstein 3D later fell through EWRAM data as ARM.
- The real-BIOS IRQ return fix sets `LR_irq` to `Pc + 4` for hardware IRQ entry while preserving the existing no-BIOS wrapper behavior. That matches BIOS handlers that return with `subs pc, lr, #4`; the prior Thumb path used `Pc + 2`, so returning from an interrupt resumed one Thumb instruction too early. The CLI also now reports the interrupted return PC in `--trace-irq` and can write generic `--pc-snapshot-csv` rows for `--snapshot-pc` probes.
- `compat-bios-aligned-curated-boot-smoke-after-irqfix-20260521.csv` reran curated indexes 1-6 after the IRQ fix: 6/6 boot, 0 crashes, 0 timeouts. Duke Nukem Advance now reaches frame 120 under the boot probe instead of crashing at frame 5; Wolfenstein 3D is classified `animated` instead of crashing. Doom, Doom II, GTA, and Metal Slug still boot under the aligned real-BIOS path, with some `stalled-late` classifications driven by the generic no-input probe rather than a crash.
- `compat-bios-aligned-curated-boot-001-200-after-irqfix-20260521.csv` extends the aligned real-BIOS boot sweep through the first 200 curated titles: 196 boot, 4 static, 0 crashes, and 0 timeouts. Focused standard reprobes now show Fire Pro Wrestling 1/2, Aero the Acro-Bat, and Chailien animate when given a longer 300-frame boot window and/or Start input, so these are no longer treated as startup blockers.
- `compat-bios-aligned-curated-boot-001-300-after-irqfix-20260521.csv` completes the 300-title curated aligned real-BIOS boot sweep: 295 boot, 4 static, 1 crash, and 0 timeouts. The only crash is Madden NFL 2002 (`MADDEN NFL02`, index 253), which is not a missing ARM opcode despite ending on `0x1C187110` decoded as ARM at `08009898`. The focused traces (`madden2002-pc-realbios-20260521.csv`, `madden2002-pc-nobios-20260521.csv`) show the real-BIOS path takes an IRQ at `08009828`, then the installed IWRAM IRQ handler/BIOs return path leaves the game with System `sp=03007FA0` instead of the no-BIOS `sp=03006FC4`. The later Thumb epilogue pops zero and branches to BIOS address `0`, eventually returning into Thumb ROM bytes in ARM state. Treat Madden as a BIOS IRQ-handler stack/context target.
- `compat-bios-aligned-curated-boot-001-300-after-irqldmfix-20260521.csv` reruns the first 300 curated titles after fixing ARM `LDM ... pc^` writeback ordering: 296 boot, 4 static, 0 crashes, and 0 timeouts. Madden NFL 2002 now boots under the same aligned real-BIOS probe. The root cause was the BIOS/game IRQ return path executing `ldmia sp!, {r0-r12,pc}^`; restoring CPSR before writeback wrote the IRQ stack writeback into the restored System SP bank. Block data transfer now defers the `pc^` CPSR restore until after writeback, preserving the interrupted System stack.
- Aero the Acro-Bat follow-up reports: `compat-aero-realbios-standard-20260521.csv`, `compat-aero-realbios-input-20260521.csv`, and `compat-aero-realbios-long-3600-20260521.csv` initially stayed on a white frame. Decoded EEPROM traces showed Aero sending `11` read commands and `10` write commands, matching GBATEK, while the emulator had those command IDs reversed. After fixing the EEPROM decoder and updating the DMA/unit coverage, `compat-aero-after-eeprom-fix-20260521.csv` passes the focused real-BIOS standard suite: 2/2 boot rows, both animated, 0 static, 0 crashes, and 0 timeouts.
- `save-probe-all-eeprom-command-fix-20260521.csv` requested the EEPROM-heavy probe set after the command decoder correction and produced 146 save probes: 125 ok, 21 no-save, and 0 failed. Runtime save detection in that request included 70 EEPROM, 35 SRAM, 15 Flash64K, 5 Flash128K, and 21 None rows, which is a useful reminder that the CLI's folder index order is authoritative for probe output.
- `compat-eeprom-command-fix-slice-20260521.csv` covers a 30-title real-BIOS EEPROM/save-heavy standard slice after the command fix. It produced 60/60 boot rows, 0 static, 0 crashes, and 0 timeouts; classifications were 42 animated, 14 stalled-late, and 4 low-motion.
- `compat-bios-static-reprobe-after-eeprom-fix-20260521.csv` reruns the old static trio Fire Pro Wrestling 2, Aero, and Chailien with a 300-frame boot window plus Start probe: 6/6 boot, 0 static, 0 crashes, 0 timeouts. Fire Pro Wrestling 1 was rerun separately in `compat-bios-static-reprobe-fp1-after-eeprom-fix-20260521.csv` and also passes both standard phases. Under the original 120-frame boot-only smoke (`compat-bios-static-reprobe-120-after-eeprom-fix-20260521.csv`), Aero and Chailien now show frame changes while Fire Pro Wrestling 2 still needs a longer window or input.
- `compat-bios-aligned-gameplay-001-025-after-eepromfix-merged-20260521.csv` starts the post-EEPROM-fix curated real-BIOS gameplay sweep. It covers indexes 1-25 with ROM-entry-aligned input timing: 100 phase rows, 99 boot, 1 crash, 0 static, and 0 timeouts. The only failure is Grand Theft Auto Advance USA (`GTA`, index 4) in the generic `long-input` phase at frame 1061 with invalid PC `0x1A3019F4`; the trace (`gta-realbios-generic-longinput-invalid-tail-20260521.txt`) matches the generated-code/worklist sentinel path seen in earlier GTA triage rather than a missing opcode. Indexes 21-25 were clean in `compat-bios-aligned-gameplay-021-025-after-eepromfix-20260521.csv` with 20/20 boot rows.
- `compat-bios-aligned-gameplay-001-030-after-eepromfix-merged-20260521.csv` extends that aligned real-BIOS gameplay sweep through index 30: 120 phase rows, 117 boot, 1 crash, 2 static, and 0 timeouts. The added 26-30 slice has no crashes; the only static rows are Fire Pro Wrestling 1/2 in the 120-frame boot-only phase, and both titles animate under Start/broad/long input.
- The compatibility classifier now labels short 120-frame boot-only statics as `early-window-static` instead of plain `static` classification. The row `status` remains `static`, so hard counts are preserved, but triage can distinguish likely false early-window probes from persistent no-video/static failures. `compat-bios-aligned-gameplay-027-028-earlywindow-smoke-20260521.csv` verifies this against Fire Pro Wrestling 1/2.
- `compat-bios-aligned-gameplay-001-045-after-earlywindow-merged-20260521.csv` extends the aligned real-BIOS gameplay sweep through index 45: 180 phase rows, 177 boot, 1 crash, 2 static, and 0 timeouts. The added 31-45 slices are clean, 60/60 boot rows, including WarioWare Twisted, Yoshi Topsy-Turvy, Powerpuff Girls, and Scooby-Doo movie rows.
- `compat-bios-aligned-gameplay-001-075-after-earlywindow-merged-20260521.csv` extends the aligned real-BIOS gameplay sweep through index 75: 300 phase rows, 297 boot, 1 crash, 2 static, and 0 timeouts. The added 46-75 slices are clean, 120/120 boot rows. This real-BIOS path now clears the old Muppets/Scooby/Crash/Spyro hard-failure anchors; remaining generic-probe concerns in this range are GTA's `long-input` generated-code/control-flow crash and low-motion/stalled-late classifications from title/menu screens that need game-specific scripts before being treated as core bugs.

Then triage the non-boot rows into emulator-bug and archive-noise buckets:

```powershell
.\scripts\triage-full-archive-failures.ps1 -FailuresPath compat-retail-full-run\cumulative\failures.csv
```

The triage pass writes `triage/summary.md`, `triage/triaged-failures.csv`, and grouped CSVs by bucket, archive class, error, and ROM. Use this before choosing fixes from a full-library run, because virtual-console injects, unlicensed ROMs, hacks, and GBA Video entries can otherwise dominate the failure counts.

For the curated A+ milestone manifest, use the milestone runner. Small chunks and explicit process timeouts are preferred for longer `gameplay` runs:

```powershell
.\scripts\run-milestone.ps1 -Priority 2 -Category technical -ChunkSize 1 -Suite gameplay -MaxSeconds 300 -RetryTimeoutSteps 0 -ProcessTimeoutSeconds 1500 -NoCapture -Resume
```

`-ProcessTimeoutSeconds` guards each child `dotnet` run and kills its process tree if it exceeds the limit. This is useful when a desktop sweep is interrupted or a very slow ROM phase risks leaving an emulator process running in the background.

## Save Probes

`save-probe` exercises the detected save backend for each ROM and verifies that exported save data can be loaded into a fresh bus:

- `SRAM`: writes/readbacks sampled SRAM bytes.
- `FLASH512`/`FLASH1M`: programs sampled Flash bytes through the unlock/program command path, including both banks for 128 KiB Flash.
- `EEPROM`: writes/readbacks two 64-bit EEPROM blocks through the serial protocol, using 6-bit or 14-bit addresses based on ROM size.
- `None`: records `no-save`.

Run the A+ milestone save probe with:

```powershell
.\scripts\run-save-probe.ps1 -OutputDir save-probe-a-plus
```

Filter it the same way as milestone sweeps:

```powershell
.\scripts\run-save-probe.ps1 -Priority 1 -MaxItems 8 -OutputDir save-probe-p1-top8
```
