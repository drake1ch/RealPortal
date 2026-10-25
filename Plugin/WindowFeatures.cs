using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace RealPortal
{
    // Config Window.Quality (Docs\FARWORLD-V3.md §7 V0): a preset over the window switches (WindowFeatures.BindQuality).
    public enum WindowQuality
    {
        // Every visible window live, as a continuation of the game (1.0.3's count scheduler with V0's TAA fixes); since
        // preset 3 also the parity switches that cost time (Docs\PARITY-PLAN.md §3) and the ripple fixes.
        Continuation,
        // Today's v2 tiers: small and far windows keep their last view for some frames (cheaper with many portals, but
        // their picture lags and trails when the camera turns).
        Performance,
    }

    // The window pipeline's switches (Runtime\WindowFlags, plain fields the shared renderer reads) as engine features:
    // the lab's "feat Win.<Name> on|off" and the config (section "Engine") reach them. Registered once, in
    // Plugin.Awake; every one defaults to on except Win.AoTemporal, Win.RestoreFromWindow, Win.AoTierA and
    // Win.ShadowTiers, and off is the behaviour from before it. The last two change the light of a cached view: in the
    // lab (tierab, 24.10) a NearCached window came out 20-44/255 brighter than Live with either on (snow slopes lost
    // their terrain shadow past 80 m, AO went), so the view jumped at every tier change; with both off every tier
    // matched Live within its own noise, at the same window CPU time.
    // Win.Tiers defaults to off since FARWORLD-V3 V0 (the ghost run, 25.10: cached tiers trail under the game's TAA), and
    // Window.Quality (Plugin.cs) is a preset over it and the V0 switches (BindQuality).
    internal static class WindowFeatures
    {
        static bool s_registered;

        // Window.Quality's switches and their value under Continuation; Performance sets each the other way round, which
        // is the pipeline from before V0 (Win.Tiers on, none of the V0 parts). Docs\FARWORLD-V3.md §7 V0, Appendix A.
        static readonly (string Name, bool Continuation)[] s_quality =
        {
            ("Win.Tiers", false),
            ("Win.PointDepth", true),
            ("Win.ExplicitJitter", true),
            ("Win.LiveFill", true),
            ("Win.EnvOncePerArea", true),
            ("Win.CaptureOnce", true),
            ("Far.InViewStep", true),
            // Preset 2 (25.10, the user's rule: a window shows exactly what the game shows): far particle systems and the
            // far Mistlands mist run by Unity itself, every frame, as in place, not paused and stepped by hand before a
            // render (which ran them slower than the game's whenever a cap or budget cut in).
            ("Far.ParticleWindowGate", false),
            ("Far.MistTickShown", false),
            // Preset 3 (25.10, Docs\PARITY-PLAN.md §3): the parity switches that cost frame or load time (a window goes live
            // when the half it shows stands, far areas as wide as the game's, distant and nested windows, every window on
            // screen every frame) and the engine's two the base game needs with far areas loaded (the player's pieces worn
            // at the game's pace, his navmesh tiles first), set by their names; and the ripple fixes for windows under the
            // game's TAA (Docs\RIPPLE-PLAN.md §4). Far.Spawning is not here (D3: off whatever the preset).
            ("Far.SideReady", true),
            ("Far.FullAreaZones", true),
            ("Win.DistantLive", true),
            ("Win.NestedLive", true),
            ("Win.AllLive", true),
            ("WearNTearActiveSectors", true),
            ("FarNavmeshCap", true),
            ("Win.TaaViewDepth", true),
            ("Win.BlurInputs", true),
            ("Win.ShadowRes", true),
            ("Win.StaleTaa", true),
            // Preset 4 (25.10, FarWorld V3): the far sides from FarWorld's registry, their static objects outside the far
            // active area drawn as exact proxies (Far.Proxies) instead of instantiated; Performance keeps them real (V2 off).
            ("Far.V2", true),
            ("Far.Proxies", true),
        };

        // Bumped whenever s_quality changes: a config whose preset was applied at an older version gets it once more.
        const string PresetVersion = "4";

        // The engine's own switches in s_quality are registered by their modules' static initializers, which run as the
        // engine plugin installs its modules (its Awake, before Real Portal's: a hard dependency). Run here as well, so
        // Features.Set finds them whatever touched the module first; a module the engine left out still registers its
        // switch, which then does nothing.
        static readonly Type[] s_engineModules = { typeof(Engine.WearNTearActiveModule), typeof(Engine.FarNavmeshCapModule) };

        // Window.Quality sets its switches when it changes, never on its own otherwise: whether it changed since they were
        // last set is kept in `applied` (a key of section Debug in the engine's config, beside the switches: it survives a
        // restart, and an engine config reset to its defaults resets it too). A switch flipped alone afterwards, in section
        // Engine or by the lab's "feat", stays as it is until Quality is changed again; a config from before Window.Quality
        // or a reset one (applied empty) gets the preset once. After Register (the switches must exist).
        internal static void BindQuality(ConfigEntry<WindowQuality> quality, ConfigEntry<string> applied)
        {
            if (quality == null || applied == null)
                return;
            foreach (Type t in s_engineModules)
                try
                {
                    System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(t.TypeHandle);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Window.Quality: the engine's {t.Name} did not start: {(e.InnerException ?? e).Message}");
                }
            if (applied.Value != Applied(quality.Value))
                ApplyQuality(quality.Value, applied);
            quality.SettingChanged += (_, __) => ApplyQuality(quality.Value, applied);
        }

        static string Applied(WindowQuality q) => q + "/" + PresetVersion;

        static void ApplyQuality(WindowQuality q, ConfigEntry<string> applied)
        {
            bool continuation = q == WindowQuality.Continuation;
            foreach ((string name, bool on) in s_quality)
                if (!Features.Set(name, continuation ? on : !on))
                    Plugin.Log.LogWarning($"Window.Quality: no engine switch {name}");
            applied.Value = Applied(q);
            Plugin.Log.LogInfo($"Window.Quality {q}: " + (continuation
                ? "windows live every frame (Win.Tiers off, the V0 parts on), far particles and mist run by the game every frame, " +
                  "the parity and ripple switches and FarWorld proxies of preset " + PresetVersion + " on"
                : "windows in tiers with cached views (Win.Tiers on, the V0 parts off), far particles and mist stepped only for renders, " +
                  "the parity and ripple switches and FarWorld proxies of preset " + PresetVersion + " off"));
        }

        // The two switches a dedicated server reads too (RegisterServer).
        const string MpParityText =
            "Multiplayer far ends: the watched ones first, then the nearest, up to Distance.MultiplayerFarEnds (32) a player; what changes at a watched one sent " +
            "with the priority of the player's own surroundings; the far core counted as arrived once all its objects came or their count held still for 1 s; " +
            "no window live without the core's objects; stale owners handed over by a modded server. Off: the first 8 far ends, the core at 90 % less 3, " +
            "live on bare terrain after 60 s.";
        const string FullAreaZonesText =
            "Far areas load everything as far out as the game does around the player (the simulation distance), whatever Distance.FarAreaZones says. " +
            "Off: everything within FarAreaZones zones only, so at simulation distance 4-5 small objects 3-5 zones out are missing (Window.Quality=Continuation turns it on).";

        // A dedicated server (no graphics: Plugin.Awake stops before Register; from PortalServer.Install): only the switches
        // its far-end service reads (Far.MpParity; Far.FullAreaZones through RemoteAreas.AreaDistance), under the same names,
        // descriptions and defaults as on a client.
        internal static void RegisterServer()
        {
            if (s_registered)
                return;
            s_registered = true;
            Bind("Far.MpParity", v => FarFlags.MpParity = v, MpParityText);
            Bind("Far.FullAreaZones", v => FarFlags.FullAreaZones = v, FullAreaZonesText);
        }

        internal static void Register()
        {
            if (s_registered)
                return;
            s_registered = true;
            Bind("Win.DepthOnly", v => WindowFlags.DepthOnly = v,
                "Window camera: the depth texture only (no depth+normals, no motion vectors; its SSAO without the temporal filter).");
            Bind("Win.FallbackFlag", v => WindowFlags.FallbackFlag = v,
                "Inside a window render the other windows show their fallback through one global shader flag, not per-window material writes.");
            Bind("Win.LayerCull", v => WindowFlags.LayerCull = v,
                "Object layers culled beyond the distance the game loads objects, on the window cameras and (while far areas are loaded, not in free fly) the game camera: other far areas are not drawn.");
            Bind("Win.LeanFixed", v => WindowFlags.LeanFixed = v,
                "The game camera's fixed window passes only when they do something (depth copy, motion vectors, second sun shafts chain).");
            Bind("Win.RTPool", v => WindowFlags.RTPool = v,
                "Window view textures in 32 px size classes, kept between frames with an idle expiry and a budget (oldest classes go first).");
            Bind("Win.Margin", v => WindowFlags.Margin = v,
                "The border rendered around a window's rect is at most a quarter of the rect, but not less than the SSAO reaches (min(Margin, max(0.25 x rect, SSAO radius))), in 32 px steps.");
            Bind("Win.EnvGroups", v => WindowFlags.EnvGroups = v,
                "One far-side state per far area and frame, applied once per group of window renders onto that area.");
            Bind("Win.SkyPerArea", v => WindowFlags.SkyPerArea = v,
                "Sky occlusion of the far side kept per far area (not per window), re-rendered when something under it changes, as the area loads, or every 30 s; at most one a frame.");
            Bind("Win.ProbePerArea", v => WindowFlags.ProbePerArea = v,
                "One far reflection probe per far area, refreshed on change or every 30 s, never faster than the per-link pace, one drawing at a time.");

            // Stages S2 and S3 (Docs\S2S3-CONTRACTS.md). Each takes effect only once the part behind it is in place
            // (WindowMechanisms.Effective); until then, and with the switch off, the window renders as after S1.
            Bind("Win.CameraSlots", v => WindowFlags.CameraSlots = v,
                "One window camera per render slot, set up once and rendering at most once a frame (sticky for large windows), instead of one camera copied from the game camera for every window.");
            Bind("Win.OwnFog", v => WindowFlags.OwnFog = v,
                "Slot cameras fog their view with their own command buffer (the game's fog, the far side's values) instead of a post-processing stack each.");
            Bind("Win.AoTierA", v => WindowFlags.AoTierA = v,
                "The window camera's SSAO only for large or near windows (and half-resolution for middling ones), not for every window render. Off by default: cached views come out brighter than live ones.",
                false);
            Bind("Win.AoTemporal", v => WindowFlags.AoTemporal = v,
                "SSAO with its temporal filter on the sticky camera of a large window (its own history; costs motion vectors). Off by default until the lab's flicker test.",
                false);
            Bind("Grass.Targeted", v => WindowFlags.GrassTargeted = v,
                "Slot cameras get the game camera's grass culled by their own view right before they render, instead of every grass patch handed to every camera (needs the engine's GrassMainCameraOnly).");
            Bind("Win.Tiers", v => WindowFlags.Tiers = v,
                "Windows in tiers (live, cached near, cached small, closed, hidden) planned once the camera has moved, instead of refreshing small windows every 2nd or 3rd frame. " +
                "Off by default: cached views trail when the camera turns under the game's antialiasing (Window.Quality=Performance turns it on).",
                false);
            Bind("Win.Budget", v => WindowFlags.Budget = v,
                "Window renders within a main-thread millisecond budget and a GPU budget (needs Win.Tiers), not only a count per frame.");
            Bind("Win.ErrorModel", v => WindowFlags.ErrorModel = v,
                "A cached window view is renewed when its error on screen (parallax, age, size) reaches a pixel or two, within the age caps (needs Win.Tiers).");
            Bind("Win.Resolve", v => WindowFlags.Resolve = v,
                "A cached window view is reprojected by its depth onto the window's current place on screen, instead of laid flat onto the opening.");
            Bind("Win.Occlusion", v => WindowFlags.Occlusion = v,
                "Visible pixels of each window counted on the GPU: windows behind walls or other windows stop rendering (results two frames late).");
            Bind("Win.CompositeMRT", v => WindowFlags.CompositeMRT = v,
                "The game camera's window passes (fog depth, view depth, sky, mask, ids) in one multi-target draw per window instead of one draw per pass.");
            Bind("Win.ShadowTiers", v => WindowFlags.ShadowTiers = v,
                "Shadow distance and cascades limited and point-light shadows off for cached windows (live windows keep the game's). Off by default: far slopes lose their shadow in cached views.",
                false);
            Bind("Win.RestoreFromWindow", v => WindowFlags.RestoreFromWindow = v,
                "Window pixels restored after the game's opaque effects from the window's own view, without a full-screen copy. Off by default until the lab's parity check.",
                false);

            // Twenty portals at once (Docs\PORTALS20-PLAN.md, steps A and B). On by default: those that passed their lab
            // gates on hall 20 (24.10). Off: Win.CacheBudget/Win.Prewarm (no effect measured), Far.KeepAliveSlice (slower),
            // Far.FreezeLists (0.3-0.45 ms for creatures standing still in the windows), the lab-only and pacer variants.
            Bind("Win.MandatoryCap", v => WindowFlags.MandatoryCap = v,
                "Window renders owed outside the budget (first show, camera jump, urgent) at most two a frame, the rest queued by size and distance.", true);
            Bind("Win.SlotPrewarm", v => WindowFlags.SlotPrewarm = v,
                "Slot cameras with their AO and fog made before the first window needs them and kept while portals are live.", true);
            Bind("Win.CacheBudget", v => WindowFlags.CacheBudget = v,
                "Cached views of hidden windows kept by least-recent use within a memory budget instead of dropped after 10 s.", false);
            Bind("Win.Prewarm", v => WindowFlags.Prewarm = v,
                "Windows just outside the view with no cached view rendered ahead, one a frame.", false);
            Bind("Far.MistWarmSpread", v => FarFlags.MistWarmSpread = v,
                "The far Mistlands mist warmed up over frames within a budget as the area loads, not in one frame when its window goes live.", true);
            Bind("Far.MistTickShown", v => FarFlags.MistTickShown = v,
                "Far mist copies advanced only before their window renders, by the time gone since.", true);
            Bind("Far.DrainCalm", v => FarFlags.DrainCalm = v,
                "Far areas unload without terrain modifiers re-poking heightmaps of zones that unload too, one shared destroy budget, nearest last.", true);
            Bind("Far.ParticleWindowGate", v => FarFlags.ParticleWindowGate = v,
                "Far particle systems paused except in the live window's area and the area of a window rendering this frame (advanced before that render).", true);
            Bind("Far.DeferInteriors", v => FarFlags.DeferInteriors = v,
                "Dungeon interiors (high above the far side) not created for a far area; the game makes them once the player is there.", true);
            Bind("Far.LoadPacer", v => FarFlags.LoadPacer = v,
                "Far loading steps only when the frame's budget left covers their usual cost; one new area a frame; relists spread.", true);
            Bind("Grass.GrowBudget", v => FarFlags.GrassGrowBudget = v,
                "Grass patches for windows grown one or two a frame over all watched portals.", true);
            Bind("Grass.ViewNear", v => FarFlags.GrassViewNear = v,
                "Grass let through for window views tested against the window frusta near it only (a 64 m grid).", true);
            Bind("Far.KeepAliveSlice", v => FarFlags.KeepAliveSlice = v,
                "Far objects kept alive by sector, a sixth of the scene checked per tick, instead of every instance every tick.", false);
            Bind("Far.KeepAliveVerify", v => FarFlags.KeepAliveVerify = v,
                "Lab: Far.KeepAliveSlice's result compared with the full check every Nth tick.", false);
            Bind("Far.NoDupAreas", v => FarFlags.NoDupAreas = v,
                "No far area for a far portal that lies in the player's own active area.", true);
            Bind("Far.CoreFirst", v => FarFlags.CoreFirst = v,
                "A far area loads the zones around its portal first and the window goes live then; the rest follows with a share of the budget.", true);
            Bind("Far.LinkScaling", v => FarFlags.LinkScaling = v,
                "Per-portal work (light distance, item walls, creatures, audio, wind) limited to the portals near what it concerns.", true);
            Bind("Far.FreezeLists", v => FarFlags.FreezeLists = v,
                "Only one far area simulates (the one being crossed, aimed at or near); the others' creatures, sync and timers stand still, ownership kept.", false);
            Bind("Far.SleepBudget", v => FarFlags.SleepBudget = v,
                "Far areas fall asleep and wake in parts within a budget, each on its own phase, waking ahead of the view.", true);
            Bind("Far.PacerBoostAll", v => FarFlags.PacerBoostAll = v,
                "With Far.LoadPacer: the 8 ms loading budget near every portal within 40 m whose far end is not there yet, as without the pacer, not only for the one walked up to.", false);
            Bind("Far.FreezeVerify", v => FarFlags.FreezeVerify = v,
                "Lab: with Far.FreezeLists, every update list it hands over again is checked to be the game's list less what stands still.", false);
            // Round 2 (PORTALS20-PLAN.md §7). On after their lab gates on hall 20 (24.10). Off: Far.ParticleCatchupCull (orbit
            // showed more hitches, not significant either way), Win.ShadowTiersSmall (tierab 25.10: a SmallCached window onto a
            // forest 7.4/255 past Live's own noise, for no measurable time).
            Bind("Win.ShadowTiersSmall", v => WindowFlags.ShadowTiersSmall = v,
                "Shadow caps (distance, cascades, point-light shadows) for SmallCached windows only; NearCached keeps the game's shadows.", false);
            Bind("Win.ShadowCost", v => WindowFlags.ShadowCost = v,
                "The window cost model counts each window's shadow casters from its last render, so shadow-heavy renders are spread over frames.", true);
            // Both set or clear the engine's heightmap hook (FarHeightmaps.Switched).
            Bind("Far.HeightmapDrainSkip", v => { FarFlags.HeightmapDrainSkip = v; FarHeightmaps.Switched(); },
                "Heightmaps of far zones that are unloading are not rebuilt (they are about to go).", true);
            Bind("Far.FarRegenBudget", v => { FarFlags.FarRegenBudget = v; FarHeightmaps.Switched(); },
                "Far heightmap rebuilds within a per-frame budget (the player's own area first and never held back).", true);
            Bind("Far.GrassGate", v => FarFlags.GrassGate = v,
                "Grass instance renderers of far areas updated only in frames where a window onto their area renders (or the game camera sees them).", true);
            Bind("Far.ParticleCatchupCull", v => FarFlags.ParticleCatchupCull = v,
                "Before a window renders, only the far particle systems in or near its view are advanced.", false);
            // Round 3. On after their lab gates (25.10): Far.KeepAliveFast (0 differences from the full pass under
            // Far.KeepAliveVerify), Far.ZoneSpawnSplit (world generation identical by zdocensus). Off: Far.AiStaggerFar (no
            // effect: far AI runs at 20 Hz in the fixed step), Far.ArrivalPacer (first frame not better, windows later).
            Bind("Far.KeepAliveFast", v => FarFlags.KeepAliveFast = v,
                "ZNetScene.RemoveObjects walks only the instances outside far areas; far areas' objects are kept and unloaded by RemoteAreas.", true);
            Bind("Far.AiStaggerFar", v => FarFlags.AiStaggerFar = v,
                "Far creatures not in the live window's area think every few frames instead of every frame.", false);
            Bind("Far.ZoneSpawnSplit", v => FarFlags.ZoneSpawnSplit = v,
                "A far zone's spawn split over frames (terrain first, then what grows and stands on it) within the loading budget.", true);
            Bind("Far.ArrivalPacer", v => FarFlags.ArrivalPacer = v,
                "After the player arrives far from where he was (teleport, portal), far areas and windows are brought back over frames, not all in the first one.", false);
            // FARWORLD-V3 V0, fresh windows (Docs\FARWORLD-V3.md §7 V0, Appendix A): on, as Window.Quality=Continuation
            // sets them; Performance turns them off.
            Bind("Win.PointDepth", v => WindowFlags.PointDepth = v,
                "The depth seen through a window read pixel by pixel (no blending of neighbours), so motion vectors and depth effects are exact along silhouettes.", true);
            Bind("Win.ExplicitJitter", v => WindowFlags.ExplicitJitter = v,
                "Window renders get the game's antialiasing jitter as an explicit projection beside the unjittered one, transparent objects unjittered (as the game camera), not through the lens shift.", true);
            Bind("Win.LiveFill", v => WindowFlags.LiveFill = v,
                "Without Win.Tiers, windows due every 2nd or 3rd frame render every frame while the frame has time to spare, largest first.", true);
            Bind("Win.EnvOncePerArea", v => WindowFlags.EnvOncePerArea = v,
                "The far side's weather, mist and distant terrain set up once for the window renders of one far area in a row (kept between them; needs Win.EnvGroups), and the area woken once a frame.", true);
            Bind("Win.CaptureOnce", v => WindowFlags.CaptureOnce = v,
                "The buffers that capture a window view's depth and normals attached to each window camera once, not added and removed around every render.", true);
            Bind("Far.InViewStep", v => FarFlags.InViewStep = v,
                "Far particle systems in the windows rendered this frame advance every frame (up to a count), not caught up in turns within a millisecond.", true);
            // FARWORLD-V3 V2, the FarWorld foundation with objects still real (Docs\FARWORLD-V3.md §7 V2, Appendix A;
            // ARCHITECTURE-v2.md §7 S4). Far.V2 is the master and stays off until its lab gate passes (off: far areas as
            // before, nothing of FarWorld made). Its parts are on and act only with it; each set brings FarWorld up or
            // down at its next frame (FarWorld.Switched). Far.V2 is a slow toggle: settle the objects after it.
            Bind("Far.V2", v => { FarFlags.V2 = v; FarWorld.Switched(); },
                "FarWorld: what the far areas hold is kept per sector from the world's change journal (no relists), areas into one place share it, and portals open by readiness. Off by default until its lab gate.",
                false);
            Bind("Far.Journal", v => { FarFlags.Journal = v; FarWorld.Switched(); },
                "With Far.V2: the far sectors follow the engine's ZDO change journal (needs the engine's ZdoJournal); off, they are read from the game's lists whenever asked, as a relist.");
            Bind("Far.Registry", v => { FarFlags.Registry = v; FarWorld.Switched(); },
                "With Far.V2: far content kept per sector with reference counts, so far areas that overlap list each sector once; off, every far area lists its own zones.");
            Bind("Far.Readiness3", v => { FarFlags.Readiness3 = v; FarWorld.Switched(); },
                "With Far.V2: a window shows, takes thrown things and lets you walk through at three separate readiness steps of its far side, not at one.");
            Bind("Far.VirtualEnd", v => { FarFlags.VirtualEnd = v; FarWorld.Switched(); },
                "With Far.V2: a far portal that is not loaded is stood in for by its measured frame and its saved place, so its window can be set up before it loads.");
            // The ripple fixes (Docs\RIPPLE-PLAN.md §4): off until their lab lines (ghostab's ripple gate, flowMust) pass at
            // -Gfx max with TAA on. Window.Quality=Continuation turns Win.TaaViewDepth, Win.BlurInputs, Win.ShadowRes and
            // Win.StaleTaa on (preset 3, 25.10); Win.ShadowFullCull and Win.NearSteps wait for the final test; Win.LiveShare
            // is the mechanism Win.AllLive (parity P8) takes with its share at 0.
            Bind("Win.TaaViewDepth", v => WindowFlags.TaaViewDepth = v,
                "The game's TAA gets the depth seen through the windows on its own material every frame, so it follows the motion of what a window shows at its edges, not of the window's flat surface.",
                false);
            Bind("Win.BlurInputs", v => WindowFlags.BlurInputs = v,
                "The motion vectors and depth seen through the windows bound again first at the game camera's BeforeImageEffects, so its motion blur reads them and not its own; the depth also reaches the effects after it (depth of field, and TAA's dilation when its material has no depth of its own).",
                false);
            Bind("Win.ShadowRes", v => WindowFlags.ShadowRes = v,
                "The sun's shadow map during a window render is as large as the game camera's, not sized by the window's texture (no sharpness pops as a window grows or shrinks; costs GPU per window).",
                false);
            Bind("Win.ShadowFullCull", v => WindowFlags.ShadowFullCull = v,
                "A window camera culls by the game camera's whole frustum from its place, its projection still the window's (whether the sun's cascades then hold still; costs main-thread time for large windows).",
                false);
            Bind("Win.NearSteps", v => WindowFlags.NearSteps = v,
                "The window camera's near plane at the opening moves in 2 % steps, not every frame (its frustum and shadow fit keep their shape between steps).",
                false);
            Bind("Win.LiveShare", v => WindowFlags.LiveShare = v,
                "Without Win.Tiers, windows from 1.5 % of the screen render every frame whatever time the frame has left, and windows within 8 m of the player too (not only of the camera).",
                false);
            Bind("Win.StaleTaa", v => WindowFlags.StaleTaa = v,
                "A window shown from an earlier render keeps that render's antialiasing offset and the motion of its opening, and the LOD cross-fade stays instant through such frames.",
                false);
            Bind("Win.FullFrameHero", v => WindowFlags.FullFrameHero = v,
                "Without Win.Tiers, the largest window renders every frame over the whole screen, with the game camera's own frustum, shadow cascades and shadow map size (costs about a full scene render).",
                false);

            // Parity (Docs\PARITY-PLAN.md §2, §3): a window shows exactly what the game would show standing or looking there,
            // and the base game looks and behaves as without the mod. Each is on by default except Far.Spawning (D3); off is
            // the behaviour from before it. Window.Quality=Continuation turns those that cost time on (Far.SideReady,
            // Far.FullAreaZones, Win.DistantLive, Win.NestedLive, Win.AllLive; preset 3), Performance off.
            // Far areas, ownership, loading and crossing (P1, P3-P6, P15, P26-P28, P38).
            Bind("Far.SectorSleep", v => FarFlags.SectorSleep = v,
                "Far areas sleep and wake by zone: nothing in the zones the game loads around the player, or in a watched, waking or nearby area, ever sleeps; " +
                "a window about to come into view (the camera's turn rate counted) wakes its area ahead, one on screen finishes waking before it renders, and " +
                "what wakes is caught up (animator states kept, particles advanced up to 2 s). Off: each area sleeps by its own object list (what it shares " +
                "with a watched place or the player's surroundings too, beyond 200 m of the player), wakes within 1 ms a frame, animators restart.");
            Bind("Far.ClaimLikeGame", v => FarFlags.ClaimLikeGame = v,
                "Far objects are owned (simulated) as by a player standing at the far portal: those in its active area, claimed before the window goes live " +
                "(characters first) and every frame after, and kept while the area drains (the game's 2 s release keeps them only while the area does not " +
                "drain); in multiplayer at most 200 a frame and never one another player owns. Off: the whole near square, 500 per relisting once live, " +
                "released and taken again by the game's pass (creatures blink and stop).");
            Bind("Far.SideReady", v => FarFlags.SideReady = v,
                "A window goes live only once the half of its far side it shows stands: every object there made, every zone spawned, no heightmap rebuild " +
                "queued; that half loads first, and the link drops back while the camera is on a side not ready. Off: live once the zones around the far " +
                "portal stand, the rest streaming into the live window (Window.Quality=Continuation turns it on).");
            Bind("Far.CrossAtOnce", v => FarFlags.CrossAtOnce = v,
                "After walking through, the far area of the place just left is made in the same frame (the game's removal cannot unload it first), its " +
                "distant terrain built ahead while you stand by the portal, and the arriving place's distant terrain kept until the game's own has rebuilt. " +
                "Off: the area waits for a frame under Far.LoadPacer (the place may unload and rebuild over seconds), at once only after jumps over 200 m with Far.ArrivalPacer.");
            Bind("Far.TrackUntilListed", v => FarFlags.TrackUntilListed = v,
                "Something made in or carried into a loaded far area (thrown, followed, felled, born) is kept until its area is next listed (30 s at most). " +
                "Off: kept 3 s, then destroyed by the game's removal and made again at the next listing (or lost if not saved).");
            Bind("Far.DupEdge", v => FarFlags.DupEdge = v,
                "A far portal inside the player's own active area gets an edge area: the far side's sectors outside the zones the game loads for the player, " +
                "with the far active area less the player's owned. Off: no area (Far.NoDupAreas), so small objects past the player's near ring are missing in " +
                "the window and creatures in the far active area stand invisible.");
            Bind("Far.MpParity", v => FarFlags.MpParity = v, MpParityText);
            Bind("Far.LiveRecheck", v => FarFlags.LiveRecheck = v,
                "A live link whose far end has an area is checked every frame and drops back while that area drains or the half it shows is not ready. " +
                "Off: once live it stays live while both portals are loaded (a half-unloaded place shows).");
            Bind("Far.VisibleNear", v => FarFlags.VisibleNear = v,
                "Far heightmaps in areas whose window is live or about to show, or that load the half a window will show, rebuild at once as the player's own. " +
                "Off: within Far.FarRegenBudget (ground under locations shows unlevelled for frames).");
            Bind("Far.FullAreaZones", v => FarFlags.FullAreaZones = v, FullAreaZonesText);
            // The windows (the renderer's; P2, P7, P8, P25, P42, P43).
            Bind("Win.DistantLive", v => WindowFlags.DistantLive = v,
                "Windows render as far as their far end stays loaded (PreloadDistance + UnloadExtraDistance) and down to a few pixels on screen; every reach " +
                "that went by Window.MaxDistance (wake, lights, grass, culling) follows. Off: not beyond Window.MaxDistance or under MinScreenFraction of the " +
                "screen, where the fallback shows (Window.Quality=Continuation turns it on).");
            Bind("Win.VanillaLook", v => WindowFlags.VanillaLook = v,
                "A window that is not rendered (out of reach, too small, seen through another window, in a reflection) looks as the game's portal does: an empty ring. " +
                "Off: a glowing blue disc.");
            Bind("Win.NestedLive", v => WindowFlags.NestedLive = v,
                "Windows seen through a window render too when their own far side is loaded (largest first, at most 4 a frame and 3 ms). Off: Window.Recursion " +
                "decides, and at 0 they show the fallback (Window.Quality=Continuation turns it on).");
            Bind("Win.AllLive", v => WindowFlags.AllLive = v,
                "Without Win.Tiers every window on screen renders every frame: no every 2nd/3rd frame for small windows, no MaxRendersPerFrame hold, no time cap " +
                "for them (Win.LiveShare with its share at 0). Off: RefreshSmallWindowsLess, MaxRendersPerFrame and Win.LiveFill's cap as set " +
                "(Window.Quality=Continuation turns it on).");
            Bind("Win.OcclusionStatic", v => WindowFlags.OcclusionStatic = v,
                "With Win.Occlusion, a window behind something still renders while a character (you too), ship, door or moving body stands between the camera " +
                "and it. Off: hidden by the camera's stillness alone (its old view shows for frames when something moving uncovers it).");
            Bind("Win.TransparentDepth", v => WindowFlags.TransparentDepth = v,
                "Transparent effects of this side at a window (soft particles, mist, water) read the depth seen through it, with ambient occlusion a few pixels " +
                "past its edge. Off: they meet the window as a flat surface.");
            // The game side: lights, LOD, grass, the base view, crossing visuals (P9-P11, P13, P14, P23, P24, P29, P32, P33,
            // P37, P39-P41, P44, P47).
            Bind("Win.LightsPerPlace", v => WindowFlags.LightsPerPlace = v,
                "The game's light limits (point lights, their shadows) ranked per place: around the player and around each live window's far point within reach, " +
                "and far lights mapped as far as windows render. Off: one ranking over all places (far torches take the player's shadows), far lights to Window.MaxDistance.");
            Bind("Win.LodFadeWindowsOnly", v => WindowFlags.LodFadeWindowsOnly = v,
                "LOD switches are instant only during the window renders; the game camera keeps the game's cross-fade. Off: instant for the whole game while windows render.");
            Bind("Grass.Ahead", v => FarFlags.GrassAhead = v,
                "Far grass kept for every live window, new patches grown first for windows about to show, and the full circle round the exit while you stand " +
                "by a portal. Off: kept and grown for watched windows only (bare ground grows back patch by patch on a look back).");
            Bind("Win.ExitShadows", v => WindowFlags.ExitShadows = v,
                "What a window render hides behind the exit portal still casts its shadow onto the visible side. Off: hidden with its shadow (sunlit ground where the place has shade).");
            Bind("Win.BaseHidesFar", v => WindowFlags.BaseHidesFar = v,
                "The game camera draws only what the game itself loaded: far areas' heightmaps, water, lights, effects and objects beyond the player's rings are " +
                "hidden from it, window grass reaches window cameras only, and Win.LayerCull follows the ground's real relief. Off: far areas show from the " +
                "base view as far as Win.LayerCull lets them (its height allowance a fixed 400 m).");
            Bind("Win.CrossingBodies", v => WindowFlags.CrossingBodies = v,
                "Creatures, other players, ships and carts in an opening show on both sides, cut at the opening, as you do. Off: only you; the others are cut " +
                "at the opening and jump across.");
            Bind("Win.ShieldDome", v => WindowFlags.ShieldDome = v,
                "Shield-generator domes of the far side show in their windows, and domes on this side stop at the window surface. Off: far domes never show; " +
                "near ones are drawn over the windows.");
            Bind("Grass.GateAhead", v => FarFlags.GrassGateAhead = v,
                "Far grass also for windows about to show, tested against last frame's views widened by the camera's motion (with Far.GrassGate and for " +
                "window-only grass). Off: last frame's windows and views only (a window coming into view misses grass for a frame).");
            Bind("Grass.TargetedEase", v => FarFlags.GrassTargetedEase = v,
                "With Grass.Targeted, a window's grass density eases as the game's does. Off: it changes at once.");
            Bind("Win.FarAoTint", v => WindowFlags.FarAoTint = v,
                "Ambient occlusion in a window takes the far side's tint and strength. Off: only with Win.AoTierA, else the player's.");
            Bind("Win.FarShafts", v => WindowFlags.FarShafts = v,
                "Sun shafts in windows in the far sun's colour, for every window whose sky can reach its sun (one chain per pair rotation), and no near shafts " +
                "over window pixels. Off: the largest window only, in the player's sun colour, near shafts drawn over the windows.");
            Bind("Far.KeepAliveEveryTick", v => FarFlags.KeepAliveEveryTick = v,
                "Far.KeepAliveFast runs on every tick of the game's removal, so objects at the edge of the player's ring go when the game removes them. Off: " +
                "every 6th tick (about 0.17 s late).");
            Bind("Far.ArriveOnCross", v => FarFlags.ArriveOnCross = v,
                "The far side's weather takes over at once only when you cross a portal. Off: on any camera jump over 5 m (a vanilla teleport, a respawn, a hitch).");
            Bind("Win.ParticleSize", v => WindowFlags.ParticleSize = v,
                "Far particles keep their size on screen in small windows. Off: large close particles shrink in small windows.");
            Bind("Win.CrossNearPlane", v => WindowFlags.CrossNearPlane = v,
                "While your copy shows (ShowPlayerThroughPortal) the view keeps its near plane at the opening; without it only your real body past the exit " +
                "raises it. Off: the whole view's near plane is pushed to your body (things near the far portal cut for a moment).");
            // The far side's effects, weather, and the game's own gates there (P12, P16, P17, P20, P21, P30, P31, P34-P36,
            // P46). Far.Spawning is off by default (D3, the user's decision 25.10) and not in Window.Quality.
            Bind("Far.ParticlesByView", v => FarFlags.ParticlesByView = v,
                "Far particle systems in a window's view simulate at any distance from the far point, from the frame the window shows or is about to; near the " +
                "game camera only those in its view whose objects the game itself has there are left to the game's culling. Off: only within 120 m of a " +
                "shown far point, the list rebuilt every 2 s.");
            Bind("Far.LocalWeatherSim", v => FarFlags.LocalWeatherSim = v,
                "Far fires, cinders, slipping characters, windmills, sails, banners and waves go by the far side's own weather and wind. Off: by the player's.");
            Bind("Far.Viewer", v => FarFlags.Viewer = v,
                "The game's distance tests for smoke puffs, foot placement, footstep dust and cloth count each window's place as a viewer (such puffs " +
                "neither count against your own nor show in your own view), monsters there look for targets every 2 s as near a player, and far pieces " +
                "wear and settle as in an active area once that place stands, their rain, roof and snow tests by that place's weather. Off: they go by the " +
                "player alone (none of that at a far side).");
            Bind("Far.Spawning", v => FarFlags.Spawning = v,
                "With Far.Viewer, the game's spawners (spawn system, creature spawners, spawn areas, loot spawners) also run at a watched far side, within the " +
                "game's caps. Off by default: what lives there lives on, new spawns only where you are.",
                false);
            Bind("Far.MistParity", v => FarFlags.MistParity = v,
                "Far Mistlands mist warmed in the order windows will show it, finished before a window shows a cold copy, advanced in real time while shown, " +
                "caught up when the game takes it over, with your Wisplight's clear bubble at the far side. Off: warmed by distance and shown all at once, " +
                "topped up faster than real time while shown, no bubble.");
            Bind("Far.WeatherParity", v => FarFlags.WeatherParity = v,
                "The far side's weather as the game would have it there: shelter tested at the far point, changes blended over the game's transition, rain " +
                "starting to fall, environment overrides of the far point, wetness from the weather before, aurora per far environment, one weather copy per " +
                "place. Off: your shelter and forced environment, changes that snap, rain shown already fallen.");
            Bind("Far.ProbeLive", v => FarFlags.ProbeLive = v,
                "A far area's reflection probe is drawn before its window goes live, cross-faded as the game's, redrawn every 3 s while on screen and kept " +
                "inside the far area. Off: drawn after go-live, paced per link, redraws that snap.");
            Bind("Win.SkyLive", v => WindowFlags.SkyLive = v,
                "The far sky-cover picture (what keeps rain off under roofs) of an area whose window is on screen is redrawn every second and when a vehicle " +
                "under it moves, and handed to the game after a crossing. Off: every 30 s.");
            Bind("Far.SlowVisibleNear", v => FarFlags.SlowVisibleNear = v,
                "The game's slow updates (crops, falling stones) visit awake far cores at the game's near pace, and around you plants keep the game's own " +
                "pause. Off: far crops ripen late, far stones never fall, and plants around you grow where the game pauses them.");
            // FARWORLD-V3 V3, far content out of Unity's registry (Docs\FARWORLD-V3.md §7 V3; ARCHITECTURE-v2.md §3.5-§3.7, §3.12,
            // §7 S5/S7): off until its lab gate. Acts only with Far.V2, Far.Registry and Far.Journal (FarProxies). A slow toggle:
            // on, the proxied instances drain a few a frame; off, they are made again while the proxies stay drawn.
            Bind("Far.Proxies", v => { FarFlags.Proxies = v; FarProxies.Switched(); FarWorld.Switched(); },
                "With Far.V2: far objects proven static (pieces, trees, rocks, plants and the like with nothing but reviewed components) that lie outside " +
                "every far area's active area are not instantiated; windows draw them with the game's own meshes, materials and LODs, exactly as their " +
                "real objects. Inside an active area, and whatever cannot be drawn exactly, everything stays real. Off: every far object is real, as before.",
                false);
            // FARWORLD-V3 V1, the virtual-view spike (Docs\FARWORLD-V3.md §4.4-§4.6): lab only, off by default and in no preset.
            Bind("Win.Virtual", v => WindowFlags.Virtual = v,
                "Lab spike: the window PortalSystem.LabVirtualWindow names (-1: every window it can take) is drawn without a camera: its far " +
                "side's static content (proxies, terrain, the far core's static meshes) by one command buffer into screen-sized G-buffer " +
                "targets, lit by the game's own deferred shading and sun shadows, and shown as its slot camera's render would be. Transparents, " +
                "particles, water, grass, creatures, point lights and the window's AO are not drawn yet. Off: every window renders through its camera.",
                false);
        }

        static void Bind(string name, Action<bool> set, string description, bool defaultOn = true)
        {
            Feature f = Features.Register(name, defaultOn, description);
            set(f.On);
            // Parts that set things up once (slot cameras, SSAO copies, grass exclusions) hear about every switch.
            f.Switched += on =>
            {
                set(on);
                WindowFlags.RaiseChanged();
            };
        }
    }

    // Win.LayerCull. Built-in rendering culls every renderer in the scene for every camera out to its far clip
    // (20 km here): the game camera drew the far areas 1.5-9 km away, and each window camera other far areas in its
    // view's direction. Object layers are cut (spherically) at the farthest point the game ever creates objects around
    // the camera, so nothing it would show itself is lost; terrain, water, sky and effects keep the far clip.
    // The game camera is cut only while far areas are loaded (without them nothing stands beyond what the game loads,
    // and the cut would save nothing) and never in free fly (the camera anywhere, away from the point the game loads
    // around). Its radius grows with how far the camera stands from that point and how high the point is above the
    // ground (the Valkyrie's flight at 500 m, a dungeon), so what the game loaded below or away from it stays drawn.
    // Win.BaseHidesFar (Docs\PARITY-PLAN.md P14.3): the height allowance is the ground's real relief over the ring the
    // game loads (Relief: the lowest and highest ground there, measured from the point, plus ReliefMargin), for the game
    // camera round the reference point and for the window camera round every far end within the window reach; a fixed
    // 400 m let the game camera draw far areas' objects past the vanilla loading edge, out to ~690 m.
    internal static class WindowLayerCull
    {
        static readonly string[] s_names = { "piece", "piece_nonsolid", "static_solid", "Default_small", "item", "vehicle" };
        const string CharacterPrefix = "character";
        // Objects stand at most this far above or below the ground under the point the game loads around (mountains
        // seen from the shore).
        const float VerticalReach = 400f;
        // Win.BaseHidesFar: what stands above the generated ground (trees, a raised or dug terrain, a roof) on top of its
        // relief; and the window camera's radius rounded up to this step (far ends coming and going move it).
        const float ReliefMargin = 32f, WindowStep = 16f;
        // The camera off the reference point (third-person arm, camera carried through a portal).
        const float CameraReach = 16f;
        // The game camera's radius is set this much above what it needs, so a camera or a reference point that moves a
        // little (a jump, the arm zooming) does not rewrite it every frame; it shrinks only by more than this.
        const float Slack = 32f;

        static int s_mask = -1;
        static readonly float[] s_game = new float[32], s_window = new float[32];
        // The game camera, its own distances (what the window camera's are made from), and which halves are set.
        static Camera s_cam;
        static float[] s_original;
        static bool s_originalSpherical, s_gameOn, s_windowOn;
        static float s_flat, s_groundY, s_radius, s_windowRadius, s_checkAt, s_errorAt, s_loggedRadius, s_loggedWindow;
        // Win.BaseHidesFar: the lowest and highest ground of the ring round the reference point (and the point that ring was
        // measured round, kept while a ring round a point moved by less than Relief.Grid fills), and the window camera's
        // height allowance over the far ends; whether each is known.
        static float s_baseMin, s_baseMax, s_farVertical;
        static Vector3 s_baseAt;
        static bool s_baseKnown, s_farKnown;

        // Lab: a fixed cull radius for the game camera in metres (0: from the simulation distance).
        internal static float LabRadius;
        internal static float Radius => s_gameOn ? s_radius : 0f;
        internal static float WindowRadius => s_windowOn ? s_windowRadius : 0f;
        internal static string Layers { get; private set; } = "";

        // From the GameCamera.LateUpdate postfix, every frame (cheap unless something changed).
        internal static void Update(Camera main)
        {
            try
            {
                // The camera's pose, turn rate and frustum for this frame (WindowReach), once its pose is final.
                WindowReach.Sample(main);
                bool want = WindowFlags.LayerCull && main != null && Plugin.Enabled != null && Plugin.Enabled.Value
                            && ZNet.instance != null && PortalSystem.Instance != null;
                if (!want || (s_cam != null && main != s_cam))
                    Off();
                if (!want)
                    return;
                float now = Time.unscaledTime;
                bool check = now >= s_checkAt;
                if (check)
                {
                    s_checkAt = now + 1f;
                    if (s_mask < 0)
                        FindLayers();
                }
                if (s_mask == 0)
                    return;
                Vector3 refPos = ZNet.instance.GetReferencePosition();
                if (s_cam == null)
                {
                    // The game camera's own distances (the game never sets any), kept while it is this camera.
                    s_cam = main;
                    s_original = main.layerCullDistances;
                    s_originalSpherical = main.layerCullSpherical;
                    check = true;
                }
                if (check)
                {
                    s_flat = FlatReach();
                    s_groundY = GroundY(refPos);
                }
                // Win.BaseHidesFar: the relief of the rings, every frame (a lookup per ring once it is measured), so a far end
                // coming within reach or a reference point moving to another grid cell counts at once.
                bool relief = WindowFlags.BaseHidesFar && LabRadius <= 0f;
                if (relief)
                {
                    BaseRelief(refPos);
                    s_farKnown = FarRelief(refPos, out s_farVertical);
                }

                // The window camera stands up to the window reach (WindowReach.Render: Window.MaxDistance, or as far as a far
                // end stays loaded with Win.DistantLive; plus the frame) from the far portal its area is loaded around, at
                // the ground there.
                float windowVertical = relief && s_farKnown ? s_farVertical : VerticalReach;
                float windowRadius = (LabRadius > 0f ? LabRadius : Reach(s_flat, windowVertical) + CameraReach) + WindowReach.Render + 8f;
                if (relief)
                    windowRadius = Mathf.Ceil(windowRadius / WindowStep) * WindowStep;
                if (!s_windowOn || windowRadius != s_windowRadius)
                {
                    Fill(s_window, windowRadius);
                    PortalRenderer.WindowLayerCull = s_window;
                    bool log = !s_windowOn || !relief || Mathf.Abs(windowRadius - s_loggedWindow) > 0.25f * s_loggedWindow;
                    s_windowOn = true;
                    s_windowRadius = windowRadius;
                    if (log)
                    {
                        Plugin.Log.LogInfo($"Win.LayerCull: {Layers} culled beyond {windowRadius:0} m on the window camera" +
                                           (relief ? $" (ground relief {(s_farKnown ? $"{s_farVertical:0} m" : "not measured yet")})" : ""));
                        s_loggedWindow = windowRadius;
                    }
                }

                PortalSystem sys = PortalSystem.Instance;
                if (sys.Areas.Count == 0 || GameCamera.InFreeFly())
                {
                    GameOff(sys.Areas.Count == 0 ? "no far area loaded" : "free fly");
                    return;
                }
                // How far the game camera stands from the point the game loads around (while it is carried through a
                // portal that point is carried with it), and how high that point is above the ground.
                float off = sys.CameraThroughRecently ? CameraReach : Mathf.Max(CameraReach, Vector3.Distance(main.transform.position, refPos));
                float lift = Mathf.Abs(refPos.y - s_groundY);
                // Win.BaseHidesFar: the ground's relief from the reference point (its lift above the ground included).
                float vertical = relief && s_baseKnown
                    ? Mathf.Max(Mathf.Max(s_baseMax - refPos.y, refPos.y - s_baseMin), 0f) + ReliefMargin
                    : VerticalReach + lift;
                float need = LabRadius > 0f ? LabRadius : Reach(s_flat, vertical) + off;
                float target = LabRadius > 0f ? LabRadius : need + Slack;
                bool set = !s_gameOn || need > s_radius || (LabRadius > 0f ? s_radius != target : check && target < s_radius - Slack);
                if (!set)
                    return;
                Fill(s_game, target);
                main.layerCullDistances = s_game;
                main.layerCullSpherical = true;
                if (!s_gameOn || Mathf.Abs(target - s_loggedRadius) > 0.25f * s_loggedRadius)
                {
                    Plugin.Log.LogInfo($"Win.LayerCull: culled beyond {target:0} m on the game camera" +
                                       (relief && s_baseKnown ? $" (height allowance {vertical:0} m from the ground's relief, camera {off:0} m from the reference point)"
                                        : lift > 1f || off > CameraReach ? $" (reference point {lift:0} m above the ground, camera {off:0} m from it)" : ""));
                    s_loggedRadius = target;
                }
                s_radius = target;
                s_gameOn = true;
            }
            catch (Exception e)
            {
                // Never break the game camera over this.
                if (Time.unscaledTime >= s_errorAt)
                {
                    s_errorAt = Time.unscaledTime + 10f;
                    Plugin.Log.LogError("Win.LayerCull: " + e);
                }
            }
        }

        // Our layers cut at `radius` (or nearer, where the game camera already cuts them), the rest as the game has them.
        static void Fill(float[] into, float radius)
        {
            for (int i = 0; i < 32; i++)
            {
                float orig = s_original != null && i < s_original.Length ? s_original[i] : 0f;
                into[i] = (s_mask & (1 << i)) != 0 ? (orig > 0f ? Mathf.Min(orig, radius) : radius) : orig;
            }
        }

        // The game camera as it was (the window camera keeps its cut).
        static void GameOff(string why)
        {
            if (!s_gameOn)
                return;
            s_gameOn = false;
            if (s_cam != null && s_original != null)
            {
                s_cam.layerCullDistances = s_original;
                s_cam.layerCullSpherical = s_originalSpherical;
            }
            Plugin.Log.LogInfo($"Win.LayerCull: off on the game camera ({why})");
        }

        // The game camera as it was, and the window camera as copied from it.
        internal static void Off()
        {
            PortalRenderer.WindowLayerCull = null;
            s_windowOn = false;
            GameOff("switched off");
            s_cam = null;
            s_original = null;
        }

        // The farthest, along the ground, the game ever has an object from the point it loads around
        // (ZDOMan.FindSectorObjects with ZoneSystem.ZonesWithinRadius): the distant ring reaches
        // TotalSimulationDistance zones out, a square of zones when classic, otherwise zone centres within (r + 0.8)
        // zones; plus the object's place in its zone and the point's in its own.
        static float FlatReach()
        {
            SimulationDistance sim = ZNet.instance.GetSyncedSimulationDistance();
            const float zone = ZoneSystem.c_ZoneSize, halfDiagonal = ZoneSystem.c_ZoneSize * 0.70711f;
            int r = Mathf.Max(sim.TotalSimulationDistance, 1);
            return sim.IsClassic ? (r + 1) * zone * 1.41422f : (r + 0.8f) * zone + 2f * halfDiagonal;
        }

        static float Reach(float flat, float vertical) => Mathf.Sqrt(flat * flat + vertical * vertical);

        // The ground under the point the game loads around: its terrain, or the generated height where that is not
        // loaded yet.
        static float GroundY(Vector3 p)
        {
            if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(p, out float h))
                return h;
            if (WorldGenerator.instance != null)
                return WorldGenerator.instance.GetHeight(p.x, p.z);
            return p.y;
        }

        // Win.BaseHidesFar: the relief round the reference point. While a newer ring fills, the last one stays in force if
        // it was measured round a point less than Relief.Grid away (its rings reach a grid step past the loading ring, so
        // it covers the new one); farther (a teleport, a fast flight) the fixed allowance holds until the new one is in.
        static void BaseRelief(Vector3 refPos)
        {
            if (Relief.Range(refPos, s_flat, out float min, out float max))
            {
                s_baseMin = min;
                s_baseMax = max;
                s_baseAt = refPos;
                s_baseKnown = true;
                return;
            }
            if (s_baseKnown && Utils.DistanceXZ(refPos, s_baseAt) > Relief.Grid)
                s_baseKnown = false;
        }

        // Win.BaseHidesFar: the height allowance every window camera needs: over the far ends of the real links within the
        // window reach of the reference point (live, or soon), the relief of the ring the game would load round each far
        // portal, measured from it. False while a ring is not known yet, or with no such link (the fixed allowance holds
        // meanwhile).
        static bool FarRelief(Vector3 refPos, out float vertical)
        {
            vertical = 0f;
            bool known = true, any = false;
            float reach = WindowReach.Render + 20f;
            foreach (PortalLink link in PortalLink.All)
            {
                ZDO far = link.IsReal ? link.PartnerZdo : null;
                if (far == null || Utils.DistanceXZ(link.transform.position, refPos) > reach)
                    continue;
                any = true;
                Vector3 c = far.GetPosition();
                if (!Relief.Range(c, s_flat, out float min, out float max))
                {
                    known = false;
                    continue;
                }
                vertical = Mathf.Max(vertical, Mathf.Max(Mathf.Max(max - c.y, c.y - min), 0f) + ReliefMargin);
            }
            return known && any;
        }

        // The lowest and highest generated ground (WorldGenerator, what the terrain is made from; digging and raising stay
        // within ReliefMargin) over a square of grid points round a point: the ring the game loads there, a grid step
        // wider. A height costs microseconds: points are sampled within a count a frame and kept, and each ring is walked
        // once, carrying on where the count stopped it (a ring round a point that moved knows most of its points already).
        static class Relief
        {
            // Half a zone: a zone-sized grid can miss a mountain top between its points by tens of metres.
            internal const float Grid = 32f;
            const int SamplesPerFrame = 64;
            // Past these the caches start over (a long journey).
            const int MaxPoints = 1 << 16, MaxRings = 256;

            sealed class Ring
            {
                public int X0, Z0, Side, Next;
                public float Min = float.MaxValue, Max = float.MinValue;
            }

            static readonly Dictionary<long, float> s_heights = new Dictionary<long, float>(4096);
            static readonly Dictionary<long, Ring> s_rings = new Dictionary<long, Ring>();
            static int s_frame = -1, s_left;

            // False while some of the ring's points are not sampled yet (more are sampled now, within this frame's count).
            internal static bool Range(Vector3 center, float radius, out float min, out float max)
            {
                min = max = center.y;
                WorldGenerator gen = WorldGenerator.instance;
                if (gen == null)
                    return false;
                if (s_frame != Time.frameCount)
                {
                    s_frame = Time.frameCount;
                    s_left = SamplesPerFrame;
                }
                // The points from span steps below the point's cell to span + 1 above: at least radius + Grid each way.
                int span = Mathf.CeilToInt(radius / Grid) + 1;
                int cx = Mathf.FloorToInt(center.x / Grid), cz = Mathf.FloorToInt(center.z / Grid);
                long key = ((long)(cx & 0xFFFFF) << 40) | ((long)(cz & 0xFFFFF) << 20) | (uint)(span & 0xFFFFF);
                if (!s_rings.TryGetValue(key, out Ring ring))
                {
                    if (s_rings.Count >= MaxRings)
                        s_rings.Clear();
                    ring = new Ring { X0 = cx - span, Z0 = cz - span, Side = 2 * span + 2 };
                    s_rings.Add(key, ring);
                }
                int count = ring.Side * ring.Side;
                while (ring.Next < count)
                {
                    int x = ring.X0 + ring.Next % ring.Side, z = ring.Z0 + ring.Next / ring.Side;
                    long point = ((long)x << 32) | (uint)z;
                    if (!s_heights.TryGetValue(point, out float h))
                    {
                        if (s_left <= 0)
                            break;
                        s_left--;
                        if (s_heights.Count >= MaxPoints)
                            s_heights.Clear();
                        h = gen.GetHeight(x * Grid, z * Grid);
                        s_heights.Add(point, h);
                    }
                    if (h < ring.Min)
                        ring.Min = h;
                    if (h > ring.Max)
                        ring.Max = h;
                    ring.Next++;
                }
                if (ring.Next < count)
                    return false;
                min = ring.Min;
                max = ring.Max;
                return true;
            }
        }

        static void FindLayers()
        {
            s_mask = 0;
            var found = new List<string>();
            var missing = new List<string>();
            foreach (string n in s_names)
            {
                int l = LayerMask.NameToLayer(n);
                if (l < 0)
                {
                    missing.Add(n);
                    continue;
                }
                s_mask |= 1 << l;
                found.Add($"{n}({l})");
            }
            bool character = false;
            for (int l = 0; l < 32; l++)
            {
                string n = LayerMask.LayerToName(l);
                if (string.IsNullOrEmpty(n) || !n.StartsWith(CharacterPrefix, StringComparison.Ordinal) || (s_mask & (1 << l)) != 0)
                    continue;
                s_mask |= 1 << l;
                found.Add($"{n}({l})");
                character = true;
            }
            if (!character)
                missing.Add(CharacterPrefix + "*");
            Layers = string.Join(", ", found);
            Plugin.Log.LogInfo($"Win.LayerCull layers: {(found.Count > 0 ? Layers : "none")}" +
                               (missing.Count > 0 ? $"; not in this game: {string.Join(", ", missing)}" : ""));
        }
    }
}
