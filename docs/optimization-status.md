# Emulator Optimization Status

Last updated: 2026-06-20.

## Current Profiling Command

Use the helper script for repeatable release-mode compatibility profiling:

```powershell
.\scripts\run-optimization-profile.ps1 -RomRoot . -Indexes '1-3' -Suite boot -StopFrame 600 -MaxSteps 7500000 -MaxSeconds 60 -OutputDir artifacts\optimization-smoke -NoBuild
```

The script builds `src\Gba.Cli\Gba.Cli.csproj` in Release mode unless `-NoBuild` is passed, runs the CLI compatibility profile, and prints average steps/sec, frames/sec, and CPU/bus/scheduler percentages.

## Completed Pass

- Normal rendering now leaves video debug layer/composition/sample buffers disabled unless a CLI command explicitly requests debug output.
- Memory observer notification methods now return immediately when no observers are registered.
- Debug-video unit tests explicitly enable debug rendering before asserting debug buffers.
- PSG advance now fast-forwards sample/frame-sequencer phase when no PSG channel is active and no PSG capture/subscriber is present.
- Normal bitmap scanline rendering skips debug-layer recording calls unless debug rendering is enabled.
- Window/effect checks now fast-return for the common no-window/no-OBJ cases.

## Current Retail Slice

The combined 20-ROM gameplay profile at `artifacts\optimization-curated-gameplay-001-020-combined\profile.csv` covers 80 rows and averaged:

- 4,359,450 steps/sec
- 242.4 frames/sec
- 45.9% CPU, 9.1% bus, 45.0% scheduler/video

The slowest title averages were scheduler/video-heavy: `BEYBLADEGREV`, `GTA`, `ALIENHOMINID`, `BEYBLADE: UL`, and `BOND EON`.

Focused render/audio fast paths improved the intended slow subset (`GTA`, `ALIENHOMINID`, `BEYBLADEGREV`, `BEYBLADE: UL`) from roughly 2.42M to 2.53M steps/sec on the 16-row profile. The broader 15-20 chunk was mixed: several slow rows improved, but `ALIENATORS` regressed on repeated runs. Keep this pass easy to isolate if a larger sweep shows the tradeoff is not worthwhile.

## Rejected Experiments

- Scheduler next-event cache: passed tests, but the short smoke profile regressed. The existing `PriorityQueue.TryPeek` path is currently preferable.
- Additional branch-heavy memory read helpers: passed tests, but smoke profiling was neutral to negative. Keep future memory work tied to broader curated slices, not only tiny boot ROMs.

## Next Targets

- Profile longer retail gameplay routes in Release mode, especially known slow/progressful rows.
- Optimize renderer paths that are active during normal gameplay, starting with object/background scanline loops and per-pixel priority/layer bookkeeping.
- Revisit memory fast paths only with a broad curated benchmark and direct before/after classification checks.
