# GBA Route Candidate Notes

Last updated: 2026-06-29

This file tracks useful but unpromoted route probes so compatibility work does not
repeat the same blind input experiments.

| Title | Current Signal | Best Artifact | Blocker | Next Input Hypothesis |
| --- | --- | --- | --- | --- |
| DemiKids Light | Boots and advances through opening story with 6 distinct PCs at frames 3,600 and 9,000. | `artifacts/route-probes-20260629/madden-demikids-run/contact-sheet.png` | Still in opening story/cinematic text; the 18,000-frame row was interrupted before producing a useful final frame. | Continue slower A/Start dialogue advances and add directional movement only after a visible map/control scene appears. |
| LEGO Racers 2 | Reaches the customization screen with 6-7 distinct PCs. | `artifacts/route-probes-20260629/lego-dbz-run/contact-sheet.png` | The route has not yet confirmed through customization into an actual race; the aggressive START-check retune aborted. | Use a screen-reviewed sequence: select/confirm the green START check, then wait for the next menu before sending race defaults. |
| Madden NFL 2004 | Boots and reaches team/rules setup with 8-13 distinct PCs. | `artifacts/route-probes-20260629/madden2004-field-run/contact-sheet.png` | Current route reaches team select and game-rules setup, not active field play. | Add a setup-specific route: confirm team select, confirm rules screen, then wait for kickoff before sending play controls. |
| Driver 2 Advance | Promoted as `driver2-gameplay`: active 3D mission scene with HUD/minimap, traffic, and 10 distinct PCs at frame 9,000. | `artifacts/route-probes-20260629/driver2-run/contact-sheet.png` | Covered in the deep-gameplay manifest; not a remaining blocker. | Optional future work is a longer mission soak or a route that enters a vehicle. |
| Lara Croft Tomb Raider - Legend | Official USA ROM exists in the broad archive, not the curated folder. | Not yet probed. | Needs a fresh route manifest using explicit `romPath`. | Try action-adventure defaults, then directional movement/jump/action after setup. |
| Baldur's Gate - Dark Alliance | Official USA ROM exists in the broad archive, not the curated folder. | Not yet probed. | Needs a fresh route manifest using explicit `romPath`. | Try RPG defaults, then slow A dialogue advances and movement once in-engine. |
