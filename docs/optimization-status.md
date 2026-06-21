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

## Rejected Experiments

- Scheduler next-event cache: passed tests, but the short smoke profile regressed. The existing `PriorityQueue.TryPeek` path is currently preferable.
- Additional branch-heavy memory read helpers: passed tests, but smoke profiling was neutral to negative. Keep future memory work tied to broader curated slices, not only tiny boot ROMs.

## Next Targets

- Profile longer retail gameplay routes in Release mode, especially known slow/progressful rows.
- Optimize renderer paths that are active during normal gameplay, starting with object/background scanline loops and any per-pixel bookkeeping that remains outside debug mode.
- Revisit memory fast paths only with a broad curated benchmark and direct before/after classification checks.
