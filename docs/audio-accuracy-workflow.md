# Audio Accuracy Workflow

This workflow compares gbaSharp audio against an external emulator or hardware
capture without assuming sample-perfect identity. It is intended for regression
testing, timing drift checks, and focused audio bug reduction.

## Capture gbaSharp Audio

The CLI can write a deterministic 16-bit stereo PCM WAV while running a normal
`run` or `dump-frame` probe:

```powershell
dotnet run --project src\Gba.Cli -c Release -- dump-frame game.gba --stop-frame 1800 --audio-wav artifacts\audio\game-gbasharp.wav
```

Useful options:

- `--audio-sample-rate 44100`
- `--audio-gain 0.5`
- `--audio-csv direct.csv`
- `--psg-csv psg.csv`

The WAV path records mixed direct-sound FIFO output plus PSG output on the same
emulated cycle timeline. The CSV paths remain useful when reducing whether a
problem is in FIFO timing, PSG generation, or final mixing.

For the common workflow, use the wrapper script:

```powershell
.\scripts\run-audio-accuracy.ps1 -Rom game.gba -Bios path\to\gba_bios.bin -StopFrame 1800
```

With an existing reference WAV:

```powershell
.\scripts\run-audio-accuracy.ps1 -Rom game.gba -Bios path\to\gba_bios.bin -ReferenceWav artifacts\audio\game-reference.wav
```

With an mGBA reference WAV:

```powershell
.\scripts\run-audio-accuracy.ps1 -Rom game.gba -Bios path\to\gba_bios.bin -MgbaReferenceWav artifacts\audio\game-mgba.wav
```

To open the local mGBA build for manual reference capture:

```powershell
.\scripts\run-audio-accuracy.ps1 -Rom game.gba -Bios path\to\gba_bios.bin -OpenMgba
```

## Batch Audio Routes

Run a manifest-driven suite:

```powershell
.\scripts\run-audio-accuracy-suite.ps1 -Manifest docs\gba-audio-smoke-routes.csv -Bios path\to\gba_bios.bin
```

By default, the suite looks for mGBA reference WAVs at:

```text
reference-captures\mgba\audio\<label>.wav
```

If a reference WAV exists for a row, the suite captures gbaSharp audio and runs
`compare-audio.py`. If no reference WAV exists, it still captures gbaSharp audio
and records `hasMgbaReference=false` in `audio-accuracy-suite.csv`.

Useful suite options:

- `-Labels pokemon-ruby-title,sonic-advance-title`
- `-Limit 3`
- `-ListOnly`
- `-MgbaReferenceRoot reference-captures\mgba\audio`
- `-CompareTrimLeadingSilence 1024`
- `-CompareRemoveDc`
- `-NoBuild`

Manifest columns follow the existing audio smoke route shape:

| Column | Purpose |
| --- | --- |
| `label` | Stable route id; also used for `<label>.wav` mGBA references. |
| `romPath` | ROM path to run. |
| `inputScript` | Optional gbaSharp frame-input script. |
| `saveFile` | Optional save file to preload. |
| `saveReadOnly` | Keep the save from being overwritten. |
| `stopFrame` | Frame to stop and capture audio through. |
| `maxSteps` | CPU step cap. |
| `maxSeconds` | Wall-clock cap. |
| `keys` | Optional held keys. |
| `alignRomEntry` | Whether to align after BIOS handoff before route timing. |
| `notes` | Human-readable route purpose. |

## Capture mGBA Reference Audio

mGBA should be the primary practical reference for GBA audio. The local mGBA
command-line frontend exposes ROM, BIOS, config, and debugging options, but it
does not expose a direct `--wavwrite`-style audio capture flag. The mGBA Lua
scripting API can run frames, set keys, save screenshots/states, and inspect
memory/registers, but it does not expose mixed audio samples.

For now, capture mGBA reference audio from mGBA's recording UI or another
lossless system capture path, save it as a 16-bit PCM WAV, then pass it as
`-MgbaReferenceWav`.

## Optional Secondary Reference

MAME is installed locally under `.research\tools\mame\mame0288`. The GBA BIOS is
expected at `.research\tools\mame\roms\gba\gba.bin`, which mirrors MAME's `gba`
BIOS set lookup. The wrapper auto-discovers this install and passes
`-rompath .research\tools\mame\roms`.

Example MAME shape:

```powershell
mame gba -cart path\to\game.gba -wavwrite artifacts\audio\game-mame.wav
```

MAME is useful as an independent secondary reference, but it is not the primary
path for this project right now. Exact MAME invocation may vary depending on the
local MAME version, BIOS setup, and software-list configuration.

Useful MAME options for automation include `-seconds_to_run`/`-str`, which stops
after a fixed amount of emulated time, and `-wavwrite`, which writes final mixed
audio.

MAME currently writes 48 kHz WAV output by default, so use `-SampleRate 48000`
for gbaSharp captures when comparing directly:

```powershell
.\scripts\run-audio-accuracy.ps1 -Rom Ruby.gba -Bios path\to\gba_bios.bin -MameSeconds 5 -SampleRate 48000
```

Be careful with route alignment: MAME starts from power-on, while many gbaSharp
routes use `--align-rom-entry` before frame counting. For fair MAME comparison,
prefer short power-on/no-input routes or pass `-NoAlignRomEntry` and match the
same elapsed duration.

## 2026-06-11 MAME Smoke Findings

The MAME pipeline is working end to end. The local smoke run used MAME 0.288,
the local GBA BIOS set at `.research\tools\mame\roms\gba\gba.bin`, 48 kHz WAV
capture, and power-on/no-input title routes.

Working single-route shape:

```powershell
.\scripts\run-audio-accuracy.ps1 -Rom Ruby.gba -Bios path\to\gba_bios.bin -NoAlignRomEntry -StopFrame 0 -MameSeconds 5 -SampleRate 48000
```

Working two-route suite smoke:

```powershell
.\scripts\run-audio-accuracy-suite.ps1 -Limit 2 -Bios path\to\gba_bios.bin -UseMame -SampleRate 48000
```

Current result: duration now matches closely, but early startup audio does not.
MAME's first audible sample appears around 0.404s into the power-on capture,
while gbaSharp's first audible sample appears around 1.17s for the same Ruby
window. The two-route MAME suite also showed very low correlation for
`sonic-advance-title` and `pokemon-ruby-title`, with RMSE around 2338 and
duration delta around 18ms. Treat this as a real BIOS/startup audio timing or
PSG/mixer gap to investigate, not as a route-tooling failure.

A follow-up trace showed Timer 0 and direct-sound FIFO clocking by frame 5, but
the FIFO data remains zero until frame 70 in Ruby. No PSG samples are emitted
through frame 90, and the only channel-register writes in that window are BIOS
sound reset/setup writes. That means MAME's early 0.404s audible region is
likely power-on DAC/SOUNDBIAS/transient behavior, while gbaSharp's first 1.17s
region is the first nonzero FIFO program audio. Use raw power-on comparisons
when investigating startup analog/DAC behavior, and use trimmed comparisons for
title/gameplay audio triage:

```powershell
.\scripts\run-audio-accuracy.ps1 -Rom Ruby.gba -Bios path\to\gba_bios.bin -NoAlignRomEntry -StopFrame 0 -MameSeconds 5 -SampleRate 48000 -CompareTrimLeadingSilence 1024 -CompareMaxShiftMs 1500 -CompareStride 64
```

DMA source previews make the startup issue sharper: Ruby's BIOS boot configures
FIFO DMA early, and the DMA read cursor streams through zero-filled IWRAM until
it eventually reaches stack/pointer-looking data around frame 70. Separate write
traces show nonzero byte writes into the same IWRAM sound-buffer region around
frame 22, close to MAME's first audible window, but gbaSharp's DMA cursor has
already advanced past those addresses by then. The next core investigation is
therefore FIFO DMA/timer/BIOS timing interaction, not WAV capture alignment.

## Compare WAVs

Use the tolerance-oriented comparator:

```powershell
python scripts\compare-audio.py artifacts\audio\game-reference.wav artifacts\audio\game-gbasharp.wav --output-md artifacts\audio\game-audio.md --output-csv artifacts\audio\game-audio.csv
```

The comparator reports:

- duration drift,
- best alignment shift,
- left/right RMS, peak, clipping, and balance,
- channel correlation,
- MAE/RMSE/max absolute error after alignment.

Useful comparator controls:

- `--trim-leading-silence 1024` ignores startup silence or low-level transients
  before alignment.
- `--trim-padding-ms 50` keeps context before the first retained sample.
- `--remove-dc` removes per-channel means before metric calculation.
- `--stride 64 --max-shift-ms 1500` performs wider alignment searches quickly.

Treat these as triage signals rather than a single pass/fail value. Good audio
can still differ at the sample level because emulator mixers, filters, and
startup alignment differ. For automated gates, start with broad thresholds such
as low duration drift, high correlation after alignment, no clipping, and stable
RMS/channel balance.

## Suggested Test Set

Use a mix of synthetic tests and commercial anchors:

- PSG register tests for square, wave, noise, envelope, sweep, and panning.
- Direct Sound FIFO and timer/DMA timing tests.
- Pokemon Ruby, Sonic Advance, Mario Kart, Golden Sun, Metroid Fusion,
  WarioWare, and Castlevania anchors.
- Real hardware line-out/headphone captures for final confidence where possible.
