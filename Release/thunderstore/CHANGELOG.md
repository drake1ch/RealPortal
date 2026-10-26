# Changelog

## 1.0.5

- **Faster windows.** Each window's far side costs less to draw: its static objects (trees, rocks, buildings drawn from the game's own meshes) are now sorted out on several threads at once, and as compiled Burst code with the library that comes with the mod, about a third of the time they took per window. The picture is the same; if the library cannot load, the same work runs as plain code.
- Real Portal Engine 0.1.1 (in the package; this version needs it).

## 1.0.4

- **Windows look exactly like the game.** Every window is refreshed every frame, and what it shows matches the place itself: creatures are present and moving in every window, far fires, smoke and Mistlands mist run at the game's own rate, far weather and wind (also from under a roof), grass from the first frame, lights and their shadows per place, reflections, shield domes and sun shafts in the far side's light. A far side you have not looked at for a while is woken up before you look again, so nothing is frozen or missing.
- **Fixed: shimmer in windows with antialiasing (TAA) while the camera turns**: the window's shadow map now has the game's own size, TAA and motion blur read what lies behind the opening, and small windows are no longer drawn from an older frame.
- **The far side is lighter.** Its static objects (trees, rocks, buildings) beyond the place round the far portal are drawn from the game's own meshes and materials instead of being loaded as game objects: the same picture, about a third fewer objects while a portal is open. Round the far portal everything is real and alive.
- **Smoother opening.** A portal no longer drops the frame rate while it opens (new setting Distance/ActivationBudgetMs, 3), and its window opens only once the side it shows is all there, instead of things popping in.
- **New light defaults** (new installs): a portal opens when you come up to it and closes as you walk off (Distance/PreloadDistance 5, UnloadExtraDistance 1, UnloadDelay 0), one at a time (MaxFarAreas 1). Existing settings are kept; see the README to switch.
- Distant windows show the far side instead of a blue disc; a portal seen inside a window shows its own far side when it is loaded; where no window is drawn the portal looks as in vanilla.
- The window closes as soon as its far end is unloaded (it used to stay open while the far end emptied).
- Keys of older versions (the `[Render]` section) are removed from the config file by itself: they were not read any more, and editing them did nothing.
- The package now includes Real Portal Engine (a helper plugin), its Burst library and a preloader patcher (`BepInEx/patchers`).
- New setting Window/PortalEffects: `Window` (default, as before) shows the swirl, sparks and light inside the frame only while the portal does not let someone through, so the view stays clear; `Vanilla` shows them as the game does, whenever a player who may pass stands close.

## 1.0.3

- PreloadDistance can now go down to 5 m (was at least 70).
- New settings: Distance/UnloadExtraDistance (how much farther than PreloadDistance a far end stays loaded, 30 m), Distance/UnloadDelay (seconds it stays loaded after you leave, 5) and Distance/MaxFarAreas (at most this many far ends at once, the nearest portals; 0 = no limit). For portal rooms.
- Sound from the other side now comes from the portal it goes through, as loud as the whole way through it is long, so you can tell which portal it is (up close, looking through, it still comes from where it shows in the window).

## 1.0.2

- Fixed (1.0.1): two portals of the same kind had their view scaled slightly, so your own body seen through the window while walking through looked doubled.
- Fixed: walking up to a larger portal paired with a smaller one, your body seen through the window was the wrong size.

## 1.0.1

- Fixed: grass on the far side vanished from a window once you stood more than a few metres from the portal (windows refreshed every other frame lost their grass).
- Fixed: portals of different sizes (the stone portal paired with a wooden one) showed the far side around the smaller frame and under the ground. Pairs now meet at the floor you walk on; looking through a larger opening onto a smaller portal, the view is scaled so the whole opening shows what lies inside the smaller one (back to 1:1 as you come up to it and walk through), and the part of the larger opening the smaller one has no room for is a wall.
- Fixed: the exit portal's own frame no longer shows inside the window.

## 1.0.0

- First release: live windows, walking through, every portal, items/projectiles/creatures, vanilla rules as walls, reach through, sound, multiplayer.
