# gbaSharp

`gbaSharp` is a Game Boy Advance emulator written in C#. The project currently
focuses on compatibility tooling, retail-ROM validation, and a WinForms desktop
frontend for hands-on testing.

## Current Status

- Current release line: `0.1.0` preview under the MIT license.
- Core tests: 308 passing.
- Curated official boot sweep: 300/300 titles boot in the best-known aligned
  real-BIOS pass.
- Standard deep gameplay gate: 55/55 strict baseline matches.
- Strict longplay gate: 24/24 baseline matches.
- Independent mGBA visual oracle: 17/17 passing comparisons.
- Release candidate gate: 8/8 critical gameplay routes, 8/8 save-assisted
  routes, and 11/11 audio signal checks pass.

This evidence supports a broadly compatible preview, not a claim of perfect or
cycle-accurate emulation. See [ROADMAP.md](ROADMAP.md) and
[docs/gba-release-gate.md](docs/gba-release-gate.md) for current caveats and
release criteria.

## Requirements

- Windows for the desktop frontend.
- .NET 10 SDK.
- A legally obtained GBA BIOS for real-BIOS testing.
- Local `.gba` ROMs. ROMs and generated captures are intentionally not tracked.

## Build And Test

```powershell
dotnet build src\Gba.Desktop\Gba.Desktop.csproj -c Release
dotnet test tests\Gba.Tests\Gba.Tests.csproj -c Release --no-restore
```

Or run the default local verification gate:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-local-verification.ps1 -NoRestore
```

Use `-IncludeStrictReference` and `-IncludeHardSoak` when the local reference
captures, baselines, BIOS, and curated ROM collection are present and you want a
heavier release-style pass.

Create a desktop publish folder and zip under `artifacts`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\package-desktop.ps1
```

The versioned package includes the executable, runtime files, README, and MIT
license. Pass `-SelfContained` to build a package that does not require a
separate .NET runtime installation.

Run the mGBA-first audio route suite:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-audio-accuracy-suite.ps1 -Manifest docs\gba-audio-smoke-routes.csv
```

Run a MAME-backed audio comparison when the local MAME tool is installed under
`.research\tools\mame`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-audio-accuracy.ps1 -Rom Ruby.gba -Bios path\to\gba_bios.bin -MameSeconds 5 -SampleRate 48000
```

## Desktop Frontend

Launch the WinForms frontend:

```powershell
dotnet run --project src\Gba.Desktop -c Release
```

Open a ROM at startup:

```powershell
dotnet run --project src\Gba.Desktop -c Release -- path\to\game.gba
```

The frontend supports:

- BIOS selection and persisted BIOS preference.
- ROM open dialog, drag/drop loading, and recent ROMs.
- Run, pause, reset, single-frame step, and speed controls.
- Audio enable/disable.
- Save export and autosave to the ROM-adjacent `.sav` path.
- PNG screenshots.
- Status display with title, game code, BIOS state, FPS, frame count, and speed.

Default keyboard mapping:

| GBA | Keyboard |
| --- | --- |
| A | `Z` |
| B | `X` |
| L | `A` |
| R | `S` |
| Start | `Enter` |
| Select | `Backspace` or `Shift` |
| D-pad | Arrow keys |

Useful shortcuts:

| Action | Shortcut |
| --- | --- |
| Open ROM | `Ctrl+O` |
| Open BIOS | `Ctrl+B` |
| Write save | `Ctrl+S` |
| Run/Pause | `Space` |
| Step one frame | `Ctrl+F` |
| Reset | `F5` |
| Save screenshot | `F9` |

Settings are stored under `%APPDATA%\gbaSharp\desktop-settings.json`.

## CLI Tooling

The CLI is used for compatibility sweeps, frame captures, visual baselines,
input-scripted runs, profiling, and reference comparisons:

```powershell
dotnet run --project src\Gba.Cli -c Release -- compat curated_official_gba --suite boot --bios path\to\gba_bios.bin
```

The scripts in [scripts](scripts) wrap common workflows such as strict reference
suite runs, mGBA reference capture comparison, route repeatability, and hard
local soaks.

## Documentation Map

- [Current roadmap](ROADMAP.md)
- [Implementation plan](docs/implementation-plan.md)
- [Release gate](docs/gba-release-gate.md)
- [Compatibility sweeps](docs/compatibility-sweeps.md)
- [Audio accuracy workflow](docs/audio-accuracy-workflow.md)
- [Reference capture workflow](docs/reference-capture-workflow.md)
- [Current reference status](docs/gba-reference-status.md)
- [Longplay status](docs/gba-longplay-status.md)
- [Emulator development playbook](docs/emulator-playbook/README.md)
