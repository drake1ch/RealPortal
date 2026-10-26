# Real Portal

Portals you can **see through** and **walk through**, like in *Portal*. The opening shows the real place on the other side, live, and you step into it with no loading screen and no teleport.

## Features

- **A live window.** The opening shows the other end exactly as you would see it standing there: the same light, shadows, fog, weather and wind, ambient occlusion, sun shafts, reflections, grass, particles and creatures, refreshed every frame. Look around and the view moves with you.
- **Walk through it.** No loading screen and no fade. You keep your speed and direction, and the camera passes through smoothly.
- **The far side is really there.** The place behind a portal near you is loaded the way the game loads your own surroundings. Its creatures move, fires burn and smelters work, even before you arrive.
- **Every portal.** The wooden portal, the stone portal (Ashlands) and modded portals (e.g. the Inscription Gate of Path of Valheiman). No special tag and no limit on the number of portals.
- **Things pass too.** Dropped and thrown items, arrows and other projectiles, and creatures (each configurable).
- **Vanilla rules are kept.** If something may not pass (ore and other forbidden items, boss rules, creatures you set to stay), the opening becomes an invisible wall and shows the vanilla swirl, on both sides.
- **Reach through.** Open a chest, pick up an item, use a workbench or build a piece on the other side, as if it stood where you see it.
- **Creatures notice you through it.** They see, hear and hit through the opening, and walk through it to reach you (each configurable).
- **Sound passes through**, heard from the portal it comes through.
- **Multiplayer.** A dedicated server sends each player the far side of the portals near them and generates places nobody has visited yet. Others see you jump through, not slide across the map.

## Installation

Install with r2modman / Thunderstore Mod Manager. By hand: put `RealPortal.dll`, `realportal.bundle`, `RealPortalEngine.dll` and the three `RealPortal.Burst*` files together into `BepInEx/plugins/RealPortal/`, and `RealPortal.Preloader.dll` into `BepInEx/patchers/`.

The package also brings **Real Portal Engine**, a helper plugin with the optimizations the windows rely on (and a few for the game itself). Each of them can be switched off in `BepInEx/config/valheim.realportal.engine.cfg`.

**Multiplayer:** install it on the server and on every player. A player on a server without the mod still gets windows, showing only what their game already knows about the other side.

## Performance

- **Light by default.** A portal opens when you come up to it (`PreloadDistance` 5 m, a few seconds) and closes as you walk off, and only the nearest one is open at a time (`MaxFarAreas` 1). With these settings a portal costs about as much as one more view of the game while you stand by it, and nothing once you have left it.
- **The far side is lighter.** Its static objects (trees, rocks, buildings) beyond the place round the far portal are drawn straight from the game's own meshes and materials instead of being loaded as game objects: the same picture, about a third fewer objects in your game while a portal is open. Sorting them for each window runs on several threads at once, as compiled Burst code. Round the far portal everything is real and alive.
- **Smooth opening.** Loading a far end is spread over frames (`ActivationBudgetMs`), so the frame rate holds while a portal opens.
- **Want windows from afar or many portals at once?** Raise `PreloadDistance` (120 was the 1.0.3 default) and `MaxFarAreas` (0 = no limit). Each open far end then costs about as much as your own surroundings, and each window on screen about as much as one more view of the game.

## Configuration

`BepInEx/config/valheim.realportal.cfg`. The most useful settings:

| Setting | Default | What it does |
|---|---|---|
| `General/Enabled` | true | Turns real portals on or off (off: vanilla portals). |
| `General/RespectItemRestrictions` | true | Forbidden items stop you at the opening, as in vanilla. |
| `General/Items`, `General/Projectiles` | true | Items and projectiles pass through. |
| `General/ReachThroughPortals` | true | Interact and build through the opening. |
| `General/Sound` | true | Sound passes through. |
| `Window/Quality` | Continuation | `Continuation`: every window shows exactly what the game would, every frame. `Performance`: the older cheaper mode (small windows refreshed less often, far particles stepped only when shown). |
| `Window/Resolution` | 1 | Window resolution (lower = faster). |
| `Window/PortalEffects` | Window | The swirl, sparks and light inside the frame. `Window`: only while the portal does not let someone through, so the view stays clear. `Vanilla`: as in the game, whenever a player who may pass stands close. |
| `Distance/PreloadDistance` | 5 | Far ends of portals closer than this are loaded (the portal opens). |
| `Distance/UnloadExtraDistance` | 1 | A far end stays loaded until you are this much farther than PreloadDistance. |
| `Distance/UnloadDelay` | 0 | Seconds a far end stays loaded after you leave its range. |
| `Distance/MaxFarAreas` | 1 | At most this many far ends at once, the nearest portals (0 = no limit). |
| `Distance/ActivationBudgetMs` | 3 | Time per frame for loading a far end while its portal opens: lower keeps the frame rate up, higher opens it sooner. |
| `Creatures/*` | true | Creatures see, hear, walk and hit through portals; the far side lives. |

**Upgrading from 1.0.3:** your saved settings stay as they were (120 m, no limit). To get the new light defaults, set the four Distance values above, or delete `valheim.realportal.cfg` to have it written again with the defaults. Keys of older versions (the `[Render]` section) are removed from the file by itself.

## Compatibility

- **Path of Valheiman:** supported, including its Inscription Gate and Beast Portal. Mods can use the `RealPortal.Api` class (force a mechanic, `CanPlayerCross`, `PlayerCrossed`).
- **OttoBifrost:** its preview is turned off on real portals.
- Other mods that change how portals teleport may conflict.

## Known limitations

- A portal standing right against a wall cannot be entered: your body hits the wall before you reach the opening.
- While you stand inside the opening, the half of your body still on the entry side is not drawn.
- Over a network the far side arrives more slowly than in single player: the place around the far portal in seconds, the whole area (thousands of objects) in tens of seconds.

## Credits

Parts of the approach to reaching through portals and to multiplayer were inspired by **Ravengates**. No code was taken from it.
