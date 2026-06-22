# Emulator Optimization Status

Last updated: 2026-06-21.

## Current Profiling Command

Use the helper script for repeatable release-mode compatibility profiling:

```powershell
.\scripts\run-optimization-profile.ps1 -RomRoot . -Indexes '1-3' -Suite boot -StopFrame 600 -MaxSteps 7500000 -MaxSeconds 60 -OutputDir artifacts\optimization-smoke -NoBuild
```

The script builds `src\Gba.Cli\Gba.Cli.csproj` in Release mode unless `-NoBuild` is passed, runs the CLI compatibility profile, and prints average steps/sec, frames/sec, and CPU/bus/scheduler percentages.

Add `-VideoProfile` when diagnosing renderer cost by scanline/layer:

```powershell
.\scripts\run-optimization-profile.ps1 -RomRoot curated_official_gba -Indexes '4' -Suite gameplay -StopFrame 300 -MaxSteps 20000000 -MaxSeconds 60 -OutputDir artifacts\optimization-video-breakdown-smoke -NoBuild -VideoProfile
```

Video profiling appends scanline totals and regular BG, affine BG, sprite, blend, bitmap, OBJ-window, and unaccounted render timings to `profile.csv`. Because it uses per-scanline stopwatch sampling, keep it opt-in and do not compare its speed numbers against normal non-video-profile runs.

Compare candidate profiles against a baseline before keeping hot-path changes:

```powershell
.\scripts\compare-optimization-profiles.ps1 -BaselineProfile artifacts\optimization-render-fastpaths-focus\profile.csv -CandidateProfile artifacts\optimization-semitrans-row-focus\profile.csv -OutputPath artifacts\optimization-compare.csv
```

When possible, compare candidate and baseline profiles from the same machine/session. The old artifact baselines are useful for trend context, but clean same-session A/B runs are the authority when system load changes.

## Completed Pass

- Normal rendering now leaves video debug layer/composition/sample buffers disabled unless a CLI command explicitly requests debug output.
- Memory observer notification methods now return immediately when no observers are registered.
- Debug-video unit tests explicitly enable debug rendering before asserting debug buffers.
- PSG advance now fast-forwards sample/frame-sequencer phase when no PSG channel is active and no PSG capture/subscriber is present.
- Normal bitmap scanline rendering skips debug-layer recording calls unless debug rendering is enabled.
- Window/effect checks now fast-return for the common no-window/no-OBJ cases.
- CLI optimization profiles can now collect optional video render breakdowns without changing the normal render hot path.
- Regular background scanline rendering now uses power-of-two masks/shifts and a non-mosaic fast path instead of paying modulo/mosaic arithmetic per pixel.

## Current Retail Slice

The combined 20-ROM gameplay profile at `artifacts\optimization-curated-gameplay-001-020-combined\profile.csv` covers 80 rows and averaged:

- 4,359,450 steps/sec
- 242.4 frames/sec
- 45.9% CPU, 9.1% bus, 45.0% scheduler/video

The slowest title averages were scheduler/video-heavy: `BEYBLADEGREV`, `GTA`, `ALIENHOMINID`, `BEYBLADE: UL`, and `BOND EON`.

Focused render/audio fast paths improved the intended slow subset (`GTA`, `ALIENHOMINID`, `BEYBLADEGREV`, `BEYBLADE: UL`) from roughly 2.42M to 2.53M steps/sec on the 16-row profile. The broader 15-20 chunk was mixed: several slow rows improved, but `ALIENATORS` regressed on repeated runs. Keep this pass easy to isolate if a larger sweep shows the tradeoff is not worthwhile.

The regular-BG mask/non-mosaic scanline pass improved the current 16-row slow focus profile from 990,710 to 1,653,225 steps/sec, with all 16 rows improved. On the opt-in video-profile version of the same focus slice, aggregate regular-BG time dropped from 29.2s to 19.2s and aggregate scanline time dropped from 43.2s to 29.0s.

## Rejected Experiments

- Scheduler next-event cache: passed tests, but the short smoke profile regressed. The existing `PriorityQueue.TryPeek` path is currently preferable.
- Additional branch-heavy memory read helpers: passed tests, but smoke profiling was neutral to negative. Keep future memory work tied to broader curated slices, not only tiny boot ROMs.
- Cached window-state threading through scanline draw loops: passed tests, but regressed the four-title scheduler-heavy focus slice by roughly 15%.
- Semi-transparent OBJ row flag: passed tests, but regressed the same focus slice by roughly 2.8%. The direct row scan remains preferable for now.
- Regular-background screen-entry cache: passed tests, but regressed the focused render-heavy profile and was backed out. The added state did not pay for itself.
- Lazy palette conversion cache: passed tests and helped one GTA long-input row, but regressed most rows in the same single-ROM focus check, so it was backed out.
- Sprite scanline priority buckets: passed tests, but regressed the 16-row slow focus profile from 1.65M to 0.99M steps/sec. The extra stack setup and reparse cost outweighed the saved OAM scans.
- Non-affine sprite source fast path: passed tests, but regressed the same 16-row focus profile to 0.82M steps/sec. The current `GetObjectSourcePixel` shape appears more JIT-friendly than the branch split.
- Blend effects window fast path: passed tests, but regressed the 16-row focus profile from 1.65M to 1.24M steps/sec. The extra branch shape was worse than the existing helper call pattern.

## Next Targets

- Profile longer retail gameplay routes in Release mode, especially known slow/progressful rows.
- Use `-VideoProfile` on targeted slow games to pick renderer work by measured buckets. The first GTA smoke showed regular background rendering as the dominant cost, with affine/sprite costs becoming relevant during later phases.
- Optimize renderer paths that are active during normal gameplay, starting with per-pixel priority/layer bookkeeping or larger algorithmic changes. Small branch-shape changes in sprite/blend code need especially strict same-session A/B checks because several have caused large JIT regressions.
- Revisit memory fast paths only with a broad curated benchmark and direct before/after classification checks.
