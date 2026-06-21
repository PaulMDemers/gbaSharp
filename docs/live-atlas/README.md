# Live Gameplay Atlas

This folder stores manually discovered structures for live gameplay routing.
Rows in `pokemon-ruby.csv` are drawn by the desktop control overlays when using
`atlas-grid`, `atlas-lens`, or `atlas-coordinate-lens`.

The current atlas uses player-relative tile offsets:

- `mapId`: the real game map id when known, such as `0.9`.
- `mapLabel`: human map name, such as `littleroot`.
- `dx`: tiles right of the player center at recording time; negative values are
  left. When `x/y` and matching `mapId` are available, overlays recompute `dx`
  from the current player position.
- `dy`: tiles below the player center at recording time; negative values are
  above. When `x/y` and matching `mapId` are available, overlays recompute `dy`
  from the current player position.
- `width` and `height`: structure size in 16x16 map tiles.
- `type`: color category, such as `door`, `warp`, `blocker`, `npc`,
  `interactable`, `ledge`, `grass`, or `passable`.
- `standX` and `standY`: optional absolute tile where the player should stand
  before interacting with the structure.
- `action`: optional live-play hint for the standing tile, such as `walk Up` or
  `face Up, A`.

Structure bounds and action tiles are deliberately separate. A stair or door can
occupy more than one visual tile, and the correct play action may be to stand
below it and walk into its center rather than aiming at a label or side tile.
Atlas overlays draw structure bounds as translucent colored boxes and draw
standing/action hints as green boxes.

Example:

```powershell
.\scripts\record-live-tile.ps1 -Label oldale-pc-door -Dx 0 -Dy -1 -Type door -MapId oldale -StandDx 0 -StandDy 0 -Action 'walk Up'
.\scripts\invoke-desktop-control.ps1 screenshot -Overlay atlas-coordinate-lens -OutFile .\artifacts\oldale-atlas.png
```

For Pokemon Ruby/Sapphire, `record-live-tile.ps1` fills `mapId`, `x`, `y`,
`playerFacing`, `playerX`, and `playerY` from `GET /game/ruby/state` when the
desktop app is running. If you pass `-MapId littleroot`, that value is stored as
`mapLabel` while the real map id still comes from game memory. Other games can
still use relative `dx`/`dy` notes until they get their own state probes.
