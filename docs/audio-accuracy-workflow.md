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

With a local MAME executable:

```powershell
.\scripts\run-audio-accuracy.ps1 -Rom game.gba -Bios path\to\gba_bios.bin -MamePath C:\mame\mame.exe -MameSeconds 30
```

## Capture Reference Audio

mGBA should be the primary practical reference for GBA audio. MAME is useful as
an independent secondary reference; MAME has a GBA driver and can write final
mixed audio with `-wavwrite`.

Example MAME shape:

```powershell
mame gba -cart path\to\game.gba -wavwrite artifacts\audio\game-mame.wav
```

Exact MAME invocation may vary depending on the local MAME version, BIOS setup,
and software-list configuration.

Useful MAME options for automation include `-seconds_to_run`/`-str`, which stops
after a fixed amount of emulated time, and `-wavwrite`, which writes final mixed
audio.

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
