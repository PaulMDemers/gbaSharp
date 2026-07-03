# GBA Release Gate

The release gate is the quickest path from compatibility work to a shippable
build. It wraps the existing focused runners instead of replacing them.

## Profiles

- `smoke`: Release builds, unit tests, a small hero gameplay set, and a short
  audio sanity pass. Use this after ordinary emulator changes.
- `standard`: Release builds, unit tests, the full strict deep-gameplay suite,
  save-assisted routes, and audio smoke. Use this before merging compatibility
  work.
- `full`: Everything in `standard` plus strict longplay routes. Use this before
  tagging or publishing a build.

## Commands

```powershell
.\scripts\run-release-gate.ps1 -Profile smoke -NormalPriority
.\scripts\run-release-gate.ps1 -Profile standard -NormalPriority
.\scripts\run-release-gate.ps1 -Profile full -NormalPriority
```

Each run writes a timestamped folder under `artifacts\release-gate-*` with a
top-level `release-gate-summary.md`, per-suite logs, and the normal runner
reports/contact sheets.

## Release Definition

A release candidate should pass:

- Release CLI and desktop builds.
- 295/295 unit tests or newer full test count.
- Current strict deep-gameplay suite with no failures and exact baseline matches.
- Save-assisted strict gameplay suite with no failures and exact baseline matches.
- Audio smoke with no unexpected signal mismatches.
- Full profile longplay suite before publishing a tagged build.

The gate does not prove perfect emulation. It is the fast, repeatable line for
detecting regressions in boot, rendering, input, save-assisted gameplay, audio
signal generation, and longer route stability.

## Current Smoke Result

The first validated smoke run is
`artifacts\release-gate-smoke-20260703-064519\release-gate-summary.md`.
It passed all 4 required suites:

- Release CLI/desktop build and unit tests.
- `sonic-advance-default` and `mario-kart-race` strict gameplay matches.
- `pokemon-ruby-save-bedroom` strict save-assisted match.
- `pokemon-ruby-title` and `sonic-advance-save-controlled` audio signal matches.
