# GBA A+ Compatibility Milestone

This milestone set combines widely loved/high-profile GBA games with technically demanding carts. The target list lives in `docs/gba-a-plus-milestone.csv`.

## Goals

For each milestone ROM:

- Boot reliably.
- Reach title/menu.
- Accept input and transition beyond title/menu.
- Run long probes without crash, reset, static output, or runaway timeout.
- Save/load correctly where applicable.
- Render without obvious corruption.
- Eventually validate audio and special cartridge features.

## Priority Bands

- Priority 1: famous/core library titles.
- Priority 2: hardware and technical stress cases.
- Priority 3: broader coverage and polish.

## Known Special Cases

- `Boktai` and `Boktai 2` need solar sensor/RTC support for A+ behavior.
- `WarioWare: Twisted!` and `Yoshi Topsy-Turvy` need gyro/tilt input for A+ behavior.
- `Drill Dozer` uses rumble hardware; gameplay can run without it, but A+ should expose it.
- The collection appears to have clean Ruby via Virtual Console plus a root `Ruby.gba`, but FireRed/LeafGreen/Emerald matches in the collection are hacks or VC/hack variants. Keep user-provided clean ROMs in the root as authoritative when available.

Initial neutral/default support exists for Boktai solar GPIO, WarioWare gyro/rumble GPIO, and Yoshi tilt SRAM-range reads. A+ still needs frontend controls and game-specific visual/playthrough validation for these carts.

## Runner

Use the milestone runner for safe batches:

```powershell
.\scripts\run-milestone.ps1 -Priority 1 -MaxItems 8 -Suite input -OutputDir compat-sweep-a-plus-p1
```

Continue through a priority band in safe slices:

```powershell
.\scripts\run-milestone.ps1 -Priority 1 -SkipItems 8 -MaxItems 12 -Suite input -OutputDir compat-sweep-a-plus-p1-next12
```

Run longer gameplay-style probes in very small slices:

```powershell
.\scripts\run-milestone.ps1 -Priority 1 -MaxItems 4 -Suite gameplay -OutputDir compat-gameplay-p1-smoke -NoCapture
```

The runner uses the existing safe compatibility command, low priority, small batches, and optional timeout retry.
It also passes `--frame-step-budget` so longer 600/900-frame probes are not misreported as compatibility failures just because they naturally need more CPU instructions than a 120-frame boot probe.
Default chunks are intentionally small and the per-phase wall-clock guard is 180 seconds, which avoids false negatives on slower-but-healthy games such as `Advance Wars`.

Reports:

- `milestone-all.csv`: first-pass rows.
- `milestone-timeout-retries.csv`: optional retry rows for ROMs that timed out.
- `milestone-best.csv`: first-pass rows with retry rows folded in by ROM index and phase.
