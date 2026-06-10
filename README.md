# gbaSharp

`gbaSharp` is a Game Boy Advance emulator written in C#. The project currently
focuses on compatibility tooling, retail-ROM validation, and a WinForms desktop
frontend for hands-on testing.

## Current Status

- Core tests: 281 passing.
- Curated official boot sweep: 300/300 titles boot in the best-known aligned
  real-BIOS pass.
- Targeted input slice: 84/84 boot/start/input probes pass in the best-known
  overlay.
- Deep gameplay confidence is strongest around the Pokemon, Sonic Advance, Mario
  Kart, Metroid, Castlevania, Wario, Golden Sun, and Advance Wars routes already
  covered by the route and reference tooling.

See [docs/compatibility-sweeps.md](docs/compatibility-sweeps.md) and
[docs/gba-longplay-status.md](docs/gba-longplay-status.md) for the detailed
compatibility history and current caveats.

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

- [Implementation plan](docs/implementation-plan.md)
- [Compatibility sweeps](docs/compatibility-sweeps.md)
- [Reference capture workflow](docs/reference-capture-workflow.md)
- [Current reference status](docs/gba-reference-status.md)
- [Longplay status](docs/gba-longplay-status.md)
- [Emulator development playbook](docs/emulator-playbook/README.md)
