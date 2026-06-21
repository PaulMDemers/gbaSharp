# Desktop control server

The desktop frontend can start a localhost-only control server for live
automation while the WinForms app is running: capture the current frame, query
status, and press/release GBA buttons without relying on OS window focus.

The server is opt-in. Start it from `Tools > Local Control Server` or pass
`--control-server` on the command line. Normal desktop launches keep the server
off so a local input/screenshot endpoint is not exposed accidentally.

Default URL:

```powershell
http://127.0.0.1:8765
```

If port `8765` is busy, the app tries the next available port up to `8796`. The
selected endpoint is also written to:

```powershell
$env:TEMP\gbaSharp-control.json
```

Launch options:

```powershell
dotnet run --project .\src\Gba.Desktop\Gba.Desktop.csproj -- .\ruby.gba
dotnet run --project .\src\Gba.Desktop\Gba.Desktop.csproj -- --control-server .\ruby.gba
dotnet run --project .\src\Gba.Desktop\Gba.Desktop.csproj -- --control-server --control-port 8877 .\ruby.gba
dotnet run --project .\src\Gba.Desktop\Gba.Desktop.csproj -- --no-control-server .\ruby.gba
```

Endpoints:

```text
GET  /status
GET  /game/ruby/state
GET  /screenshot
GET  /screenshot?overlay=movement-grid
GET  /screenshot?overlay=center-lens&scale=4&tiles=9
GET  /screenshot?overlay=coordinate-lens&scale=4&tiles=9
GET  /screenshot?overlay=atlas-lens&atlas=docs/live-atlas/pokemon-ruby.csv&scale=4&tiles=9
GET  /screenshot?overlay=atlas-coordinate-lens&atlas=docs/live-atlas/pokemon-ruby.csv&scale=4&tiles=9
POST /input/tap?keys=A&duration=90&delay=120
POST /input/face?key=Up&duration=45&delay=120
POST /input/tile-step?key=Right&duration=170&delay=250
POST /input/tap?keys=Up&duration=85&delay=1200
POST /input/walk-tile?key=Right&timeout=900&delay=180
POST /input/sequence?steps=Right:150:120,Up:150:120,A:80&gap=120
POST /input/press?keys=A,Right
POST /input/release?keys=A
POST /input/set?keys=A,Right
POST /input/clear
POST /emulation/run
POST /emulation/pause
POST /emulation/toggle
POST /emulation/reset
POST /emulation/step
POST /app/close
```

Keys use the `GbaKey` names: `A`, `B`, `Select`, `Start`, `Right`, `Left`, `Up`,
`Down`, `R`, and `L`. Multiple keys can be separated with commas, plus signs,
spaces, pipes, or semicolons.

Examples:

```powershell
$base = (Get-Content "$env:TEMP\gbaSharp-control.json" | ConvertFrom-Json).baseUrl
Invoke-RestMethod "$base/status"
Invoke-RestMethod "$base/game/ruby/state"
Invoke-WebRequest "$base/screenshot" -OutFile .\desktop-frame.png
Invoke-RestMethod -Method Post "$base/input/press?keys=Start"
Start-Sleep -Milliseconds 120
Invoke-RestMethod -Method Post "$base/input/release?keys=Start"
Invoke-RestMethod -Method Post "$base/input/set?keys=A,Right"
Invoke-RestMethod -Method Post "$base/input/clear"
Invoke-RestMethod -Method Post "$base/app/close"
```

The helper script wraps the same endpoints and reads the discovery file by
default:

```powershell
.\scripts\invoke-desktop-control.ps1 status
.\scripts\invoke-desktop-control.ps1 ruby-state
.\scripts\invoke-desktop-control.ps1 press -Keys Start
Start-Sleep -Milliseconds 120
.\scripts\invoke-desktop-control.ps1 release -Keys Start
.\scripts\invoke-desktop-control.ps1 screenshot -OutFile .\desktop-frame.png
.\scripts\invoke-desktop-control.ps1 screenshot -Overlay movement-grid -OutFile .\desktop-grid.png
.\scripts\invoke-desktop-control.ps1 screenshot -Overlay center-lens -OutFile .\desktop-lens.png
.\scripts\invoke-desktop-control.ps1 screenshot -Overlay coordinate-lens -OutFile .\desktop-dense-lens.png
.\scripts\invoke-desktop-control.ps1 screenshot -Overlay atlas-coordinate-lens -OutFile .\desktop-atlas-lens.png
.\scripts\record-live-tile.ps1 -Label oldale-pc-door -Dx 0 -Dy -1 -Type door -MapId oldale
.\scripts\invoke-desktop-control.ps1 tap -Keys A -Duration 80 -Gap 150
.\scripts\invoke-desktop-control.ps1 face -Keys Up
.\scripts\invoke-desktop-control.ps1 tile-step -Keys Right
.\scripts\invoke-desktop-control.ps1 warp-tap -Keys Up
.\scripts\invoke-desktop-control.ps1 walk-tile -Keys Right
.\scripts\invoke-desktop-control.ps1 sequence -Sequence 'Right:150:120,Up:150:120,A:80' -Gap 120
.\scripts\invoke-desktop-control.ps1 clear
.\scripts\invoke-live-route.ps1 -Steps 'Up Up Left Down' -CaptureEachStep
```

`movement-grid` draws the 16x16 movement-tile grid over the full 240x160
frame. `center-lens` crops the player-centered tile neighborhood, scales it
with nearest-neighbor sampling, and draws the same grid plus the player tile,
interaction-adjacent tiles, a four-tile cross, and axis-relative coordinate
labels such as `+2,0` or `0,-3`. `coordinate-lens` uses the same view but labels
every visible tile for slower route analysis. Square lens captures clamp to the
largest odd tile count that fits the GBA frame; on 240x160 output, requests above
9 tiles are reduced to 9 to avoid source-crop desync. The helper also draws
these overlays locally after downloading a screenshot, so the overlay commands
work against older already-running desktop instances.

`atlas-grid`, `atlas-lens`, and `atlas-coordinate-lens` draw known structure
records from `docs/live-atlas/pokemon-ruby.csv` by default. Use
`record-live-tile.ps1` to append player-relative tile notes while playing; for
example, `-Dx 0 -Dy -1 -Type door` marks the tile immediately above the player as
a door/warp target. When Pokemon Ruby/Sapphire state is available, the recorder
also fills the current map id, absolute x/y estimate, player x/y, and facing
from `/game/ruby/state`.

For live Pokemon Ruby/Sapphire navigation, prefer `walk-tile` for one intended
map step; it uses `/game/ruby/state` to release the direction when player
coordinates change, and falls back to timed movement if coordinates are
unavailable. Map changes also count as verified movement, with
`verificationType=map-transition`. For other games, use `tile-step`. Use `face`
before `tap -Keys A` for interaction. These endpoints return before/after state
so scripts can log exactly which input was sent and when it settled.

Use `warp-tap` for stairs, doors, and other immediate map-transition tiles after
the player is already aligned on the trigger. It sends a short timed direction
tap and releases through the transition; this avoids `walk-tile` holding the
direction long enough to step back through a destination stair/warp.

The helper script prefers `curl.exe` for screenshot downloads on Windows and
removes partial zero-byte files on failure before applying local overlays.

For known short paths, `invoke-live-route.ps1` sends route tokens, records
before/after Ruby coordinates for each step, and can capture an atlas coordinate
lens after every move. Bare directions use verified `walk-tile`; action tokens
can mix in exact pulses such as `face:Up`, `tap:A`, `tap:Up`, `warp:A`, or
`step:Right`. For example, Ruby bedroom stairs can be encoded as
`Right Right tap:Up`.
Route journals infer `coordinate` or `map-transition` verification for action
tokens when the Ruby state changes. Action tokens keep polling for
`-ActionTimeout` after the input because stairs and doors can first move onto a
trigger tile and only then report the map transition. `-Gap` controls verified
movement spacing, while `-ActionGap` defaults to a slightly longer settle window
for face/tap/warp pulses.

See `docs/live-gameplay-playbook.md` for the current live-play rules, solved
problems, and pathing/tooling roadmap.
