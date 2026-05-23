# Pokemon Ruby Save Progress

Last updated: 2026-05-16

Target ROM for this work is the root `Ruby.gba`:

- Title: `POKEMON RUBY`
- Game code: `AXVE`
- Version: `1`
- Save type: `Flash128K`

The collection milestone index `3568` can point at a collection duplicate/hack path depending on the local ROM set ordering, so Ruby-specific save work should use `romPath=Ruby.gba` in roundtrip manifests.

## Current Findings

- `scripts/visual-input/pokemon-ruby-newgame-probe.input` reaches the professor intro and name-entry screen.
- `scripts/visual-input/pokemon-ruby-newgame-name-confirm.input` accepts a typed/default name and reaches the moving truck intro.
- `scripts/visual-input/pokemon-ruby-truck-move-probe.input` moves the player inside the truck.
- `scripts/visual-input/pokemon-ruby-truck-right-exit.input` now reaches the Littleroot outside/Mom dialogue after the truck exit fade.
- `scripts/visual-input/pokemon-ruby-post-truck-dialogue.input` continues through the first Littleroot dialogue, clears Mom's first in-house reminders, reaches the player's 2F bedroom, sets the wall clock, clears Mom's post-clock dialogue, and performs a manual Start-menu save by frame `120000`.
- CLI debug snapshots now include Ruby script-movement state, real Ruby task-slot parsing, and object-event summaries to speed up room/menu routing.

The root Ruby manual-save route now produces progressed Flash128K output:

- `save-roundtrip-20260516-ruby-manual-save-release/save-roundtrip.csv` reports `progressed-pass`.
- The generated save is `131072` bytes with `56488` non-erased bytes.
- The progressed fixture was promoted to `visual-saves/pokemon-ruby.sav` and `visual-saves/approved/pokemon-ruby-root-bedroom.sav`.
- `visual-snapshots-20260516-ruby-progressed-save/visual-snapshots.csv` refreshes and passes the Ruby save-assisted baseline with the progressed save loaded read-only.
- `scripts/visual-input/pokemon-ruby-continue-to-littleroot.input` loads the progressed save, selects Continue, walks downstairs, clears Mom's Petalburg Gym TV event, exits the house, and reaches Littleroot by frame `46000`.
- `visual-snapshots-20260516-ruby-save-gameplay-tolerant/visual-snapshots.csv` passes the Ruby save-assisted and Continue-to-Littleroot rows. The Littleroot row allows a small pixel tolerance for wandering NPC animation drift.

Save output remains erased (`0xFF`) for these older/generic scripts:

- `pokemon-ruby-roundtrip`
- `pokemon-ruby-root-truck`
- `pokemon-emerald-roundtrip`
- `pokemon-leafgreen-roundtrip`

The Flash command/watch trace shows Ruby touches Game Pak SRAM/Flash early for Flash detection, but no non-erased program data is exported with the current scripts.

## Useful Reports

| Report | Result |
| --- | --- |
| `save-roundtrip-20260515-ruby-root-current/save-roundtrip.csv` | Root Ruby current script, no-progress visual pass |
| `save-roundtrip-20260515-flash128-explore/save-roundtrip.csv` | Ruby/Emerald/LeafGreen no-progress; collection index `1961` was not a Flash128K target in current ordering |
| `save-roundtrip-20260515-ruby-post-truck/save-roundtrip.csv` | Root Ruby post-truck/first-house script, visual pass, still no-progress (`0/131072` changed bytes) |
| `save-roundtrip-20260516-ruby-manual-save-release/save-roundtrip.csv` | Root Ruby manual save, `progressed-pass` (`56488/131072` changed bytes) |
| `visual-snapshots-20260516-ruby-progressed-save/visual-snapshots.csv` | Ruby save-assisted baseline refreshed with progressed Flash128K fixture |
| `visual-snapshots-20260516-ruby-save-gameplay-tolerant/visual-snapshots.csv` | Ruby progressed-save Continue path reaches Littleroot and passes visual verification |
| `ruby-post-truck-script-bedroom-50000.png` | Visual proof that the consolidated Ruby script reaches the 2F bedroom |
| `ruby-manual-save-probe-120000.png` | Visual proof that the manual-save route returns control in the 2F bedroom |
| `ruby-continue-outside-probe-46000.png` | Visual proof that the progressed save exits Brendan's house into Littleroot |

## Next Debug Steps

- Extend the post-load route through the neighbor/rival setup and Professor Birch rescue flow.
- Compare the bedroom, downstairs TV event, and Littleroot checkpoints against external reference captures.
- Keep Emerald/LeafGreen Flash128K save creation as separate follow-up work; their current generic scripts still do not produce progressed saves.
