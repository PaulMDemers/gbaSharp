# gbaSharp implementation plan

## Goal

Build a Game Boy Advance emulator in C# with a clean, testable core and a minimal desktop frontend. The first target is correctness on homebrew/test ROMs, then commercial compatibility, then convenience features such as save states, debugging, and visual tools.

## Research baseline

Primary hardware references:

- GBATEK: comprehensive GBA/NDS technical reference. Use it as the authority for memory map, I/O registers, BIOS calls, DMA, timers, PPU, sound, cartridge behavior, interrupts, and edge cases.
- gbadev/gbadoc: readable reference for the GBA memory layout and memory-mapped I/O register tables.
- Tonc: practical GBA programming guide. Use it to understand how real software uses backgrounds, sprites, palettes, DMA, interrupts, and display modes.

Emulator references:

- mGBA: mature C emulator with strong accuracy, portability, debugger/tooling, save detection, and a built-in BIOS option. Useful for expected behavior, test ROMs, debugger comparison, and feature priorities.
- NanoBoyAdvance: high-accuracy C++17 emulator focused on cycle-accurate CPU, DMA, timers, PPU, and Game Pak prefetch. Useful for understanding which timing details matter for hard games.
- no$gba: emulator/debugger and the source of GBATEK. Useful as an external behavior oracle when debugging ROMs.

Test references:

- ARMWrestler for ARM/Thumb CPU validation.
- gba-suite and FuzzARM for CPU edge cases.
- mGBA test suite for hardware behavior.
- AGS aging cartridge tests as a longer-term accuracy milestone.
- Small homebrew ROMs from Tonc or custom ROMs built with devkitARM for focused subsystem tests.

## Hardware model summary

The GBA is built around an ARM7TDMI CPU running at about 16.78 MHz with both 32-bit ARM and 16-bit Thumb instruction sets. The emulator needs exact enough behavior for:

- CPU modes, banked registers, CPSR/SPSR, exceptions, IRQ handling, ARM/Thumb decode, pipeline effects, instruction prefetch, and memory access timing.
- Memory regions: BIOS, EWRAM, IWRAM, I/O, palette RAM, VRAM, OAM, Game Pak ROM, and cartridge save memory.
- PPU: 240x160 output, scanline timing, VBlank/HBlank/VCount, BG modes 0-5, affine backgrounds, windows, blending, mosaic, sprites, priorities, and forced blank.
- DMA: 4 channels, immediate/HBlank/VBlank/special timing, repeats, address control, IRQs, and interaction with CPU/timers/sound FIFOs.
- Timers: 4 timers, prescalers, cascade mode, overflow IRQs, and timer-driven audio.
- Interrupts: IE/IF/IME, BIOS IRQ vector behavior, HALT/STOP interactions.
- Audio: PSG channels, direct sound FIFOs A/B, DMA-fed samples, mixer, bias, sample scheduling.
- Cartridge: ROM wait states, prefetch, EEPROM/SRAM/Flash saves, GPIO devices such as RTC and solar sensor later.

## Architecture

Use a headless emulator core with narrow frontend interfaces.

- `Gba.Core`: CPU, memory bus, scheduler, PPU, APU, DMA, timers, interrupts, cartridge, save memory.
- `Gba.Tests`: CPU unit tests, bus tests, scheduler tests, test ROM runner, golden traces.
- `Gba.Cli`: load ROM, run test ROMs, dump frames/audio/traces, compare against expected output.
- `Gba.Desktop`: minimal UI after the core can boot and render frames. Prefer a simple cross-platform stack such as SDL2-CS, Silk.NET, or Avalonia plus a pixel buffer.

Core interfaces:

- `IHostVideo`: receives 240x160 RGBA frames.
- `IHostAudio`: receives interleaved PCM samples.
- `IInputSource`: reports active-low GBA buttons.
- `IStorage`: loads/saves SRAM/Flash/EEPROM data.
- `IClock` or deterministic scheduler: keeps emulated time independent from wall-clock time.

## Implementation phases

### 0. Project skeleton

- Create a .NET solution with core, tests, CLI, and optional desktop projects.
- Add formatting/analyzer settings.
- Add ROM loading and cartridge header parsing.
- Add binary fixtures folder ignored from git for BIOS and commercial ROMs.
- Add a CI-friendly test ROM runner that accepts locally supplied ROM paths without committing copyrighted ROMs or BIOS files.

Exit criteria:

- `dotnet test` runs.
- CLI can identify a `.gba` file and print header metadata.

### 1. Memory bus and scheduler

- Implement address decoding and mirrors for BIOS, EWRAM, IWRAM, I/O, palette RAM, VRAM, OAM, ROM, and save memory.
- Implement 8/16/32-bit reads and writes with GBA alignment/rotation semantics.
- Add region-specific access timing hooks, even if initial timings are approximate.
- Build an event scheduler keyed by master cycles for PPU, DMA, timers, and audio.

Exit criteria:

- Unit tests cover mirrors, alignment, unmapped reads, and basic ROM/RAM access.
- Scheduler can deterministically advance events.

### 2. ARM7TDMI CPU

- Implement CPU state: registers, banked modes, CPSR/SPSR, ARM/Thumb state, exceptions.
- Implement ARM decode/execute first enough to run simple BIOS-less test loops.
- Implement Thumb decode/execute.
- Implement multiply, block transfers, PSR transfers, branches/exchange, software interrupts, undefined instruction behavior.
- Model pipeline-visible PC behavior and basic instruction timing.
- Add trace logging compatible with no$gba/mGBA style debugging.

Exit criteria:

- Pass ARMWrestler, then gba-suite/FuzzARM CPU tests.
- Golden trace tests for representative ARM and Thumb instruction groups.

### 3. BIOS strategy

- Support loading a real 16 KiB GBA BIOS from user-provided path.
- Implement BIOS protection/open-bus behavior according to tests.
- Later, optionally add high-level emulation for common SWIs or an open replacement BIOS, but do not depend on copyrighted BIOS distribution.

Exit criteria:

- BIOS-backed boot can jump into a ROM.
- SWI tests for decompression, CpuSet/CpuFastSet, Div, Sqrt, ArcTan, and IRQ handling pass.

### 4. Interrupts, timers, DMA

- Implement IE/IF/IME and exception entry.
- Implement VBlank/HBlank/VCount IRQ sources once PPU timing exists.
- Implement four timers with prescaler, cascade, reload, and IRQ.
- Implement four DMA channels with address modes, repeat, transfer sizes, start timing, priority, and IRQ.

Exit criteria:

- Timer and DMA test ROMs pass.
- DMA-driven VRAM copies visibly affect rendered output.

### 5. PPU scanline renderer

- Implement scanline timing: 160 visible lines, VBlank lines, HBlank periods, DISPSTAT/VCOUNT behavior.
- Render modes 3, 4, and 5 first for fast visible progress.
- Implement palette RAM conversion and frame output.
- Implement tiled backgrounds for modes 0, 1, 2.
- Implement sprites/OAM, affine sprites, priorities, windows, mosaic, alpha blending, brightness effects.
- Add PPU debug dumps for background layers, tiles, palettes, and OAM.

Exit criteria:

- Tonc demos and simple homebrew render correctly.
- Known commercial title intro screens reach recognizable output.
- PPU register and timing tests pass progressively.

### 6. Input and frontend

- Implement KEYINPUT/KEYCNT active-low input behavior and keypad IRQ.
- Build a minimal desktop window that displays the framebuffer, maps keyboard/gamepad input, and throttles to native frame timing.
- Add ROM open, pause/resume, reset, and screenshot.

Exit criteria:

- Playable interaction in simple games/homebrew.
- CLI remains deterministic and frontend-free.

### 7. Cartridge saves

- Implement SRAM.
- Implement Flash 64 KiB and 128 KiB command protocols.
- Implement EEPROM 512 B and 8 KiB serial protocol.
- Add save type detection by database/header heuristics, with user override.

Exit criteria:

- Save/load works across representative games for SRAM, Flash, and EEPROM.
- Save files are persisted without corrupting size/type.

### 8. Audio

- Implement PSG channels inherited from Game Boy hardware.
- Implement Direct Sound FIFO A/B.
- Implement sound DMA, timer-driven sample production, sound bias, mixing, and resampling to host sample rate.
- Add audio sync and buffering in the frontend.

Exit criteria:

- Audio test ROMs produce expected channel behavior.
- Commercial games have stable, non-crackling audio at normal speed.

### 9. Accuracy pass

- Improve cycle timing for CPU memory accesses, DMA contention, ROM wait states, prefetch, and PPU/DMA interactions.
- Model open bus and invalid memory behavior where tests/games require it.
- Add HALT/STOP behavior.
- Add RTC/GPIO and solar sensor support as compatibility extensions.

Exit criteria:

- mGBA test suite coverage is high.
- Known edge-case games boot and play.
- Compatibility matrix is maintained with test evidence.

### 10. Tooling and quality-of-life

- Save states with versioned serialization.
- Debugger: disassembly, breakpoints, memory viewer, register view, trace export.
- Frame advance, rewind later if architecture supports snapshots efficiently.
- Configurable color correction, scaling, and input mapping.

Exit criteria:

- Emulator is usable for development/debugging, not just playing.
- Save state format has compatibility/version checks.

## C# design notes

- Prefer simple arrays and spans for memory regions. Avoid per-access allocations.
- Keep CPU instruction handlers allocation-free and branch-conscious.
- Use source-generated or table-driven decode only if profiling proves it helps; clear handwritten decode is fine initially.
- Represent cycle counts as `long`.
- Keep all emulated hardware deterministic and single-threaded at first. Audio/video output can buffer on host side, but core state should advance from one scheduler.
- Make save states explicit: serialize core state structs and memory arrays, not arbitrary object graphs.
- Use `BenchmarkDotNet` only after correctness milestones; early micro-optimization will blur the design.

## Milestones

1. Header parser and memory bus tests.
2. CPU passes instruction test ROMs.
3. BIOS boot reaches ROM entry.
4. Mode 3/4/5 framebuffer demos render.
5. DMA/timer/IRQ tests pass.
6. First simple commercial game reaches title screen.
7. Saves persist.
8. Audio works.
9. Broader compatibility and accuracy pass.
10. Debugger/tooling.

## Risks

- CPU and memory timing are the hardest early correctness risk.
- PPU behavior has many priority/window/blending edge cases that should be test-driven.
- Save type detection can silently corrupt saves if guessed wrong; provide overrides and conservative file handling.
- BIOS distribution is legally sensitive; only support user-provided BIOS or clean-room/open alternatives.
- Cycle accuracy can fight performance in C# if the bus abstraction is too object-heavy; profile before locking APIs.

## Recommended first implementation slice

Start with:

1. Solution/project skeleton.
2. Cartridge header parser.
3. Memory bus with region arrays and read/write tests.
4. CPU state plus a tiny ARM/Thumb execution loop.
5. CLI test-runner scaffolding.

This gives us a vertical path into test ROMs without needing a UI, audio, or full PPU yet.

## Current implementation status

Completed first slice:

- .NET solution with `Gba.Core`, `Gba.Cli`, and `Gba.Tests`.
- Cartridge loading and GBA header parsing, including fixed-value and complement-check validation.
- Memory bus with BIOS, EWRAM, IWRAM, I/O, palette, VRAM, OAM, Game Pak ROM, and SRAM regions.
- Region mirroring for EWRAM, IWRAM, palette, VRAM, OAM, SRAM, and ROM.
- 8/16/32-bit memory access with unaligned word-read rotation.
- Deterministic cycle scheduler with insertion-order handling for same-cycle events.
- Initial ARM7TDMI state/reset/fetch model.
- Growing ARM/Thumb execution layer: ARM branch/BL/BX, multiply/MLA, data-processing operations with immediate and shifted-register operands, single/halfword/signed/block data transfers, SWI entry, Thumb ALU/immediate ops, branches/BL, high-register ops, stack ops, load/store, signed loads, load/store multiple, and SWI entry.
- Basic exception entry for SWI and pending IRQ, with initial `IE`/`IF`/`IME` register plumbing.
- Named I/O register properties for `DISPCNT`, `DISPSTAT`, `VCOUNT`, `IE`, `IF`, and `IME`.
- Banked SP/LR storage for privileged modes, with Supervisor/IRQ mode switching.
- ARM PSR transfer support: `MRS`, `MSR CPSR`, `MSR SPSR`.
- Initial exception-return behavior for data-processing instructions that write PC with the `S` bit in privileged modes.
- Minimal video timing unit with 228 scanlines, 1232 cycles per line, HDraw/HBlank phases, `VCOUNT`, `DISPSTAT` VBlank/HBlank/VCount flags, and VBlank/HBlank/VCount interrupt requests.
- Interrupt flag acknowledgement behavior for `IF` writes.
- `GbaSystem` now owns `VideoController` and advances scheduled video events during CPU steps.
- Timer unit for `TM0-TM3` with reload values, enable control, prescalers, overflow reload, cascade mode, timer IRQ requests, and visible counter synchronization on register reads.
- Bus I/O read/write observers for hardware units that need to react to memory-mapped register access.
- DMA controller for channels 0-3 with source/destination/count/control registers, immediate/VBlank/HBlank start timing, halfword/word transfers, address control modes, repeat handling, enable clearing, and DMA IRQ requests.
- Video timing now notifies DMA at VBlank and HBlank start.
- CLI `test-rom` command with `--max-steps`, `--trace`, `--success-pc`, and `--failure-pc`.
- Framebuffer output for bitmap display modes 3, 4, and 5, rendered at VBlank start into a 240x160 RGBA8888 buffer.
- Mode 4 and 5 frame-page selection support.
- Keypad input controller with active-low `KEYINPUT`, `KEYCNT` OR/AND interrupt modes, and keypad IRQ requests.
- CLI `dump-frame` command that runs a ROM for a fixed number of steps and writes the current framebuffer as a binary PPM image.
- Local Armwrestler smoke testing now reaches and renders the ROM menu. Fixes made while bringing it up: Thumb `BIC`, Thumb PC-relative literal/address pipeline correction, Thumb `BL` return link correction, and `DISPSTAT` read-only status bit preservation.
- CLI scripted input support via repeated `--key-event STEP:KEYS` arguments, for example `--key-event 500000:A --key-event 510000:none`.
- CLI that loads a ROM and prints cartridge metadata plus initial CPU PC.
- CLI `run` mode with optional instruction trace and max-step limit.
- Unit tests for cartridge headers, bus behavior, scheduler behavior, and initial CPU execution.

Next implementation slice:

1. Add real test-ROM fixtures/configuration for ARMWrestler/gba-suite conventions.
2. Investigate Armwrestler menu input polling; scripted key pulses are delivered, but the current ROM still stays on the menu.
3. Start tiled background rendering for modes 0/1/2.
4. Add sprite/OAM rendering basics.
5. Improve DMA edge cases: special timing, FIFO DMA, bus timing, prohibited regions, and exact count/address behavior.
