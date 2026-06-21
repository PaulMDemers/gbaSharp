# Live Gameplay Playbook

This note captures the rules learned during the June 2026 Pokemon Ruby live
playtesting pass through new game, starter selection, Route 101, Oldale, and
early Route 103. It is meant for Codex-driven live play with the desktop control
server, not for fixed-frame scripted verification.

## Operating Rules

- Launch the desktop app in Release mode for any meaningful gameplay pass.
- Treat screenshots as authoritative. Do not infer facing, position, or map
  state from memory if the latest frame says otherwise.
- Prefer one input, then observe. Use batches only for known dialogue paging,
  known battle-menu defaults, or already-mapped routes.
- In dialogue-heavy games, assume extra `A` presses can reopen or advance into a
  different state. Clear each text box with a single `A`, then capture again.
- For keyboard/name screens, `Start` only moves focus to `OK`; `A` confirms.
  Do not treat `Start` alone as confirmation.
- Door and object interactions require exact tile alignment. Target the center
  of the door/object, face the target, then press `A`.
- For NPCs, account for sprite height. The interaction tile is based on the NPC
  map tile, not the top of the visible sprite.
- NPCs can generally be spoken to from any adjacent side. Pick the side that
  requires the least risky movement, then face the NPC and press `A`.
- Use `face` before `A` when interacting. A short direction tap is safer than a
  movement-length hold.
- Use `walk-tile` for Pokemon Ruby/Sapphire map movement when the state probe is
  available. Use `tile-step` as the fallback, and verify after every step near
  doors, ledges, NPCs, furniture, or signs.
- Use `enter:Direction` in `scripts/invoke-live-route.ps1` for walk-in doors
  where the player must take a real tile step into the threshold. It waits for a
  delayed map transition after the walk. Use `warp-tap` only once the player is
  already aligned on a stair or immediate warp trigger.
- When passing through a town, route to the Pokemon Center and heal before
  proceeding unless the current objective explicitly depends on skipping it.
- If a `tile-step` does not move, do not immediately increase duration. First
  decide whether the target tile is blocked, the game is in dialogue, or the
  player is mid-transition.
- If dialogue persists after trying to skip through it, assume it may be a short
  sequence of only one or two boxes. Stop mashing, press `A` once, then capture
  again before deciding whether it is stuck.
- Avoid long direction holds in grid RPGs. They overshoot and turn one routing
  mistake into several.
- Use `center-lens` for normal movement and `coordinate-lens` only when planning
  around furniture, ledges, or unclear multi-tile structures.
- Treat atlas overlays as hints, not as a substitute for reading the screenshot.
  Structure bounds, the tile where the player should stand, and the action to
  send can differ. Ruby bedroom stairs are the canonical example: the stair
  structure is north of the player and the correct action from below is
  `walk Up`; do not route toward the yellow wall opening beside it.
- Do not trust old `-Tiles 13` square-lens captures. A 13-tile lens is 208 pixels
  high and cannot fit the 160-pixel GBA frame; this caused grid/image desync
  beginning with `120-neighbor-upstairs-wide-lens.png`. New square lenses clamp
  to 9 tiles.
- Preserve a trail of screenshots with numbered filenames. They are the route
  log and the bug evidence.
- Treat route confusion as tooling debt, not emulator evidence. Emulator bugs
  need symptoms such as crashes, hangs, resets, corrupted rendering, bad script
  state, impossible collisions, or broken battle/menu logic.

## Solved Or Passing Problems

- Release desktop launch plus opt-in local control server is usable for live
  gameplay. Start it with `--control-server` or `Tools > Local Control Server`.
- Screenshots can be captured fast enough for observe-input-observe play.
- `center-lens` and `coordinate-lens` materially improve tile reasoning.
- Short `face` plus `A` is reliable for object and NPC interactions when tile
  alignment is correct.
- Ruby no longer resets from the title/menu flow during normal input.
- New game, player naming, and starter naming work.
- The keyboard `Start` then `A` pattern is understood and repeatable.
- Moving truck, Littleroot, home, clock, TV, May house, Birch rescue, lab, Route
  101, Oldale, Pokemon Center, Route 101/103 wild grass, and battle transitions
  all progressed without crash or reset.
- Starter selection, starter confirmation, gift receipt, and nickname prompt
  work.
- Battle flow works across scripted starter battle and multiple wild battles:
  intro, menu, move selection, damage, HP changes, EXP, level-up stat display,
  fainting, and return-to-map transitions.
- The `RUN` battle action works and returns to overworld correctly.
- Pokemon Center healing works through dialogue, confirmation, heal sequence,
  final text, and return to control.
- Route labels, fade transitions, overworld scroll, NPC/object layering, text
  boxes, and battle UI all looked visually stable in this pass.

## Ruby Route Notes

- Birch/neighbor house 2F May setup from the upstairs stair tile:
  start at `map 1.3 x=1 y=2`, walk `Down`, `Right` five tiles, `Up`,
  bump/face `Right`, then press `A`. This talks to May from her left side. A
  direct interaction from `x=6 y=2` while facing up hits the region map instead;
  the region map opens a full-screen map that exits with `B`.
- After May leaves, return to the stairs from `map 1.3 x=6 y=2` with `Left`
  five tiles, then `Up` to transition back downstairs.
- Leaving May/Birch house from `map 1.2 x=2 y=8` uses `Down` and exits to
  Littleroot near `map 0.9 x=14 y=9`.
- Littleroot north route after May: from outside May's house, walk left six
  tiles to `x=8 y=9`, north to `x=8 y=2`, right two tiles to the north-path NPC,
  talk with `A`, clear the two-line "someone shouting" gate dialogue, then go
  right one tile and north to Route 101.
- Route 101 entry reaches `map 0.16 x=11 y=19`; the first rescue textbox is not
  a route label and must be cleared with `A`. The script moves the player to
  `x=11 y=15`.
- Birch's bag is at about `map 0.16 x=7 y=14`. From the rescue control point
  `x=11 y=15`, walk `Left Left Left Up`, bump/face `Left`, then press `A`.
- In the bag starter layout, the bottom ball selects Torchic. Confirm `YES`,
  then use battle defaults: `A` for FIGHT and `A` for the first move. In this
  run two attacks won the Poochyena battle.
- In the lab, decline the starter nickname by moving the YES/NO prompt down to
  `NO` and pressing `A`. Birch then asks whether to go see May; `YES` is already
  selected.
- Lab exit uses the right-hand threshold: from `map 1.4 x=6 y=12`, walk
  `Right`, then `Down` to transition outside near `map 0.9 x=7 y=16`.
- Littleroot-to-Route101 after the lab: from `map 0.9 x=10 y=2`, do not walk
  into the NPC at `x=10 y=1`; route `Right Up Up warp:Up` to enter Route 101 at
  `map 0.16 x=11 y=19`.
- Route 101 south-to-north solved route from entry:
  `Up Up Up Up Up Left Left Left Left Up Up Up Up Up Up Right Right Right Down Down Right Right Right Up Right Right Right Up Up Up Left Left Up Left Up Up warp:Up`.
  This exits to Oldale Town at `map 0.10 x=11 y=19`. Expect wild battles in the
  grass around `x=13 y=10` and `x=15 y=4`; after the battle intro, advance text,
  ensure the cursor is on `FIGHT` (use `Up` if it is on the lower-left command),
  then press `A` for Fight and `A` for the first move.
- Oldale Pokemon Center door is left of the red sign, not centered on the sign.
  From the Oldale south entry at `map 0.10 x=11 y=19`, walk `Left` five tiles to
  `x=6 y=19`, `Up Up` to `x=6 y=17`, then `enter:Up` to enter the Center.
- Inside Oldale Pokemon Center, from entry `map 2.2 x=7 y=8`, walk
  `Up Up Up Left Left Left face:Up tap:A` to talk to the nurse. Advance text
  and accept the heal with `A`.
- Route 103 retreat-to-heal route from the right-side grass checkpoint
  `map 0.18 x=14 y=11`: walk `Down Down Left Left Left Left`, then `Down` to
  `y=19`, and one more `Down` transitions to Oldale at `map 0.10 x=10 y=0`.
  From there route down through town to `x=10 y=17`, walk `Left Left Left Left`
  to the Pokemon Center door stand tile `x=6 y=17`, then `Up` into the Center.
  If a verified walk reports false once while the screenshot still shows an open
  tile and the game is not in dialogue/battle, retry a single time before
  detouring; Oldale produced occasional one-step verification false negatives.
- Wild battles on Route 103 are risky while Torchic is low on HP. Prefer retreat
  to Oldale and heal before attempting May. For grinding, stay close enough to
  the Center that a failed run or low HP can be recovered immediately.

## Current Pain Points

- Human-visible sprites are not enough for exact pathing. NPCs and objects need
  map-tile anchors.
- Ledges, signs, doors, tree trunks, water, and counters are easy to misread from
  screenshots alone.
- The overlay grid is screen-relative and player-centered, but it does not know
  which tiles are passable.
- We do not currently have a durable record of discovered map structures such as
  doors, stairs, counters, ledges, grass, NPC blockers, and sign tiles.
- We lack a route planner that can convert "go to May" into map steps with
  blocked-tile avoidance.
- We lack a state detector for "text box active", "battle menu active",
  "overworld control active", "fade in progress", and "transition complete".
- Movement primitives are time-based. They are good enough for one-tile steps
  but do not confirm that the player actually changed map tile.

## Tooling Wishlist

1. Tile Atlas

   Store per-game/per-map observations in a simple data file:

   ```text
   game,mapId,label,x,y,width,height,type,notes
   ruby,littleroot,brendan-house-door,5,8,1,1,door,center tile enters house
   ruby,oldale,pokemon-center-door,?, ?,1,1,door,requires exact center
   ruby,route103,ledge-north-wall,?, ?,?,1,ledge,blocks north movement
   ```

   The first version can be manually authored from screenshots. Later versions
   can be populated from game memory or ROM map data.

2. Live Map Notes

   Add a helper command that lets us tag the current player-centered tile:

   ```powershell
   .\scripts\record-live-tile.ps1 -Label oldale-pc-door -Dx 0 -Dy -1 -Type door
   ```

   The command should save the current screenshot path, frame, relative tile
   coordinate, and free-form notes.

   This now exists for relative tile annotations and writes to
   `docs/live-atlas/pokemon-ruby.csv` by default.

3. Passability Overlay

   Extend overlays to draw known atlas structures:

   - green for passable target tiles
   - red for blockers
   - blue for doors/warps
   - orange for interactables
   - arrows for one-way ledges

   `atlas-grid`, `atlas-lens`, and `atlas-coordinate-lens` now render the
   relative entries from the live atlas. Absolute map-coordinate overlays still
   need the Ruby state probe.

4. State Classifier

   Add lightweight screenshot classifiers for:

   - text box active
   - yes/no prompt active
   - battle main menu
   - move menu
   - name keyboard
   - fade/transition
   - overworld control

   This can start as simple region/color/template checks before any heavier
   computer vision.

5. Map Coordinate Probe

   Pokemon Ruby already has CLI memory scanners for player/object summaries.
   Expose a desktop control endpoint for current map/player state when a known
   game profile is active:

   ```text
   GET /game/ruby/state
   ```

   Useful fields: map group/number, local x/y, facing, movement lock, text/script
   state, object events, and player object slot. This is the biggest pathing
   unlock because screenshots no longer have to infer exact coordinates.

   `GET /game/ruby/state` now exposes Ruby/Sapphire saveblock player position,
   player object/facing, object-event summaries, task summary, movement-task
   bytes, selected Ruby vars, and raw script/map fields. This is enough to make
   atlas notes map-aware; movement lock and a general state classifier are still
   follow-up work.

6. Verified Tile Step

   Add a higher-level movement command:

   ```text
   POST /input/walk-tile?key=Up&timeout=800
   ```

   It should press the direction until the game-state coordinate changes by one
   tile, then release. If game-state coordinates are unavailable, it should fall
   back to current timed `tile-step`.

   This now exists as `POST /input/walk-tile` and
   `.\scripts\invoke-desktop-control.ps1 walk-tile -Keys Up`. It verifies
   movement from Ruby/Sapphire saveblock coordinates, treats map changes as
   `verificationType=map-transition`, and returns the before/after Ruby state in
   the response.

7. Route Planner

   Once atlas plus current coordinates exist, use A* over the known tile graph.
   The planner should avoid blockers, handle one-way ledges, and stop at the
   correct adjacent tile/facing for interactions.

   Before full A*, `scripts/invoke-live-route.ps1` can run short known paths as
   `walk-tile` batches and write a per-step coordinate journal. Use it when the
   route is known but still needs screenshots or repeatability evidence.

8. Gameplay Journal

   Persist each live action as structured data:

   ```text
   index,time,statusBefore,input,statusAfter,screenshot,notes
   ```

   This lets us replay decisions, promote stable route segments into fixed input
   scripts, and separate navigation mistakes from emulator defects.

## Pathing Approach

Start with a hybrid process:

1. Use screenshots and `coordinate-lens` to manually mark local structures.
2. Build an atlas around one route at a time: Littleroot, Route 101, Oldale,
   Route 103.
3. Add game-state coordinate extraction for Ruby so each screenshot has real
   map coordinates.
4. Convert live discoveries into stable route segments: "Oldale PC door to
   Route 103 entrance", "Route 101 south-to-north", "centered door entry".
5. Use A* only over known tiles. If the planner reaches unknown space, stop,
   capture, annotate, and expand the atlas.
6. For interactions, plan to an adjacent tile and facing direction, not to the
   object sprite.
7. Promote a route segment only after it succeeds twice from the same checkpoint.

This keeps the live loop honest: screenshots remain the ground truth for what is
visible, game memory gives exact coordinates when available, and the atlas turns
our corrections into durable route knowledge instead of repeated rediscovery.
