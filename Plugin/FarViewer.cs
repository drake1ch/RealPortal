using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace RealPortal
{
    // Far.Viewer (Docs\PARITY-PLAN.md P17, with P18 and P36). The game gates much of what a place does by how far the
    // player is: smoke puffs rise from chimneys and fires within 64 m of him (SmokeSpawner.Spawn), feet are set on the
    // ground within 64 m of the camera (CharacterAnimEvent.UpdateFootIK), footstep dust within 20 m of it
    // (FootStep.UpdateFootstep), an armor stand's cloth moves within 10 m of him (ArmorStand.Update), and a monster looks
    // for targets every 2 s with a player within 50 m, else every 6 s (MonsterAI.UpdateTarget). Pieces wear (rain, snow,
    // support: a beam without it falls) and stones settle and fall only inside the active area round the player
    // (WearNTear.UpdateWear, StaticPhysics.SUpdate: ZNetScene.OutsideActiveArea). A far side has no player, so a window
    // showed none of that. Here each window's place counts as a viewer as well:
    //  - the view positions: where each window view was rendered from last frame (PortalRenderer.ViewPositions), and
    //    Pair·eye of the windows about to show (WindowReach.AboutToShow) that did not render then; taken once a frame;
    //  - a visual gate's distance is the nearer of the game's own (to the player or the camera) and the nearest view
    //    position's; the monster's 50 m test counts a view position as a player; a smoke puff only a view position let
    //    through is that place's, not the game's: it keeps out of the game's count of puffs (a cap of 100 of its own, so it
    //    never fades the player's early) and out of the game camera's view (BaseView);
    //  - a piece or stone inside the active area of an awake far area (RemoteAreas.CollectAwakeCores: watched, waking or
    //    loading, once its place round the far portal stands, RemoteAreas.CoreStands; the game's rule round its far portal's
    //    zone, FarOwnership.ActiveArea) is inside an active area, as it is for a player standing at that far portal, and a
    //    far piece's rain, roof and snow tests go by that place's weather (FarWet, FarEnvironment.LocalWeather).
    // Each is a call-site transpiler: the game's own call is routed here, asked first, and nothing else in the method
    // changes; ZNetScene.OutsideActiveArea itself is not patched (the removal and ownership code keep the game's answer).
    // The engine's WearNTearActiveSectors gets the active-area sectors of the awake far areas (P18), so with it on their
    // pieces wear at the game's ~1 s cycle; SlowUpdaterBudget gets the awake far areas for its cores' pass while
    // Far.SlowVisibleNear is on (P36: far crops and stones at the game's pace, the player's own plants keep the game's
    // latch).
    // Far.Spawning (D3: off by default and not in Window.Quality, the user's decision 25.10) adds the game's spawners at a
    // watched far side: the spawn system of the zone the local player would stand in there (Pair·position through a
    // watched window) runs with him moved there for that call only (FarLife's pattern), with that place's random event
    // and environment; creature spawners, spawn areas and loot spawners count a viewer at a view position in their range
    // tests, for points inside the active area of an awake far area whose window is watched. The game's own caps and
    // ownership tests apply: only what this game owns (claimed as by a player there, never another player's) spawns.
    // Off (either switch, or the mod): every routed call answers as the game's, nothing is registered or hooked.
    [HarmonyPatch]
    internal static class FarViewer
    {
        // A view position this near counts in LabViewerGateHits (the widest of the visual gates).
        const float GateReach = 64f;

        // ---- lab counters (internal static, for the lab) ----
        // Distance tests of the game's visual and AI gates a window's place answered (a view position nearer than the
        // game's own viewer, within 64 m; or one that met the monster's 50 m), cumulative.
        internal static long LabViewerGateHits;
        // Far.Spawning: spawns at a far side for its viewer (the spawn system round the moved player, and spawners only a
        // view position triggered), cumulative.
        internal static long LabFarSpawns;
        // Pieces and stones counted inside an active area by an awake far area (cumulative); view positions, awake far
        // areas, sectors registered with the engine's WearNTearActive and places the spawn system runs for, now.
        internal static long LabActiveAreaHits;
        internal static int LabViews, LabAwakeCores, LabWearSectors, LabSpawnPlaces;

        // Far.Viewer (with the mod) and Far.Spawning, as of this frame's Update.
        static bool s_on, s_spawning;

        // This frame's view positions.
        static readonly List<Vector3> s_views = new List<Vector3>();
        // The awake far areas, and (with Far.Viewer, same order) their active areas and whether a window onto them was
        // seen within 3 s.
        static readonly List<(Vector3 center, Vector2s zone, int nearRadius, bool watched)> s_cores = new List<(Vector3, Vector2s, int, bool)>();
        static readonly List<ActiveArea> s_active = new List<ActiveArea>();
        static readonly List<bool> s_watched = new List<bool>();

        // P18: the sectors registered with the engine (each once), and those wanted this frame, by WearNTearActive's
        // packing of a sector (Vector2s' own hash folds x and y onto each other).
        static readonly Dictionary<int, Vector2s> s_registered = new Dictionary<int, Vector2s>(), s_want = new Dictionary<int, Vector2s>();
        static readonly List<int> s_unwanted = new List<int>();

        // P36: SlowUpdaterBudget's hook, set while Far.SlowVisibleNear is on.
        static readonly Action<List<Engine.SlowUpdaterBudgetModule.NearCore>> s_fillNear = FillNear;
        static bool s_nearHooked;

        // Far.Spawning: where the local player would stand at a watched far side, one per zone, with the far portal of its
        // area (whose weather that place goes by).
        struct SpawnPlace
        {
            internal Vector2s Zone;
            internal Vector3 At, Center;
        }

        static readonly List<SpawnPlace> s_places = new List<SpawnPlace>();
        // Inside a spawn system's update for one of them: where the player stood (local to his parent), and the far portal;
        // the random event that was active before the place's was set.
        static bool s_inSpawn, s_eventSet;
        static Vector3 s_playerLocal, s_spawnCenter;
        static RandomEvent s_eventWas;
        // A spawner's range test met by a view position only (until that spawner's update ends).
        static bool s_gate;

        // SmokeSpawner.Spawn's player distance (the game's m_minPlayerDistance).
        const float SmokeReach = 64f;
        // The smoke puffs only a view position let through (far puffs), the newest last; for BaseView, which hides them
        // from the game camera. A spawn going on whose player test only a view position met, and the game's newest puff
        // before it.
        internal static readonly HashSet<Smoke> FarPuffs = new HashSet<Smoke>();
        static readonly List<Smoke> s_farOrder = new List<Smoke>();
        static bool s_smokeByView;
        static Smoke s_smokeLast;
        static readonly AccessTools.FieldRef<List<Smoke>> s_smokeList =
            AccessTools.StaticFieldRefAccess<List<Smoke>>(AccessTools.Field(typeof(Smoke), "s_smoke"));
        static readonly AccessTools.FieldRef<Smoke, bool> s_smokeAdded = AccessTools.FieldRefAccess<Smoke, bool>("m_added");
        // The game's private parts the spawning needs, looked up on first use; a game update that renamed one leaves the
        // spawning out (the rest of Far.Viewer runs).
        static AccessTools.FieldRef<SpawnSystem, ZNetView> s_spawnView;
        static AccessTools.FieldRef<RandEventSystem, RandomEvent> s_activeEvent, s_randomEvent, s_forcedEvent;
        static bool s_spawnReady, s_spawnBroken;

        // The game's calls routed here.
        static readonly MethodInfo s_distance = AccessTools.Method(typeof(Vector3), nameof(Vector3.Distance));
        static readonly MethodInfo s_inRange = AccessTools.Method(typeof(Player), nameof(Player.IsPlayerInRange), new[] { typeof(Vector3), typeof(float) });
        static readonly MethodInfo s_inRangeNoise = AccessTools.Method(typeof(Player), nameof(Player.IsPlayerInRange), new[] { typeof(Vector3), typeof(float), typeof(float) });
        static readonly MethodInfo s_outsideHere = AccessTools.Method(typeof(ZNetScene), nameof(ZNetScene.OutsideActiveArea), new[] { typeof(Vector3) });
        static readonly MethodInfo s_outsideOf = AccessTools.Method(typeof(ZNetScene), nameof(ZNetScene.OutsideActiveArea), new[] { typeof(Vector3), typeof(Vector2s) });
        static readonly MethodInfo s_isWet = AccessTools.Method(typeof(EnvMan), nameof(EnvMan.IsWet), Type.EmptyTypes);
        static readonly MethodInfo s_snowBuildup = AccessTools.Method(typeof(EnvMan), nameof(EnvMan.GetSnowBuildup), Type.EmptyTypes);
        static readonly MethodInfo s_isEnvironment = AccessTools.Method(typeof(EnvMan), nameof(EnvMan.IsEnvironment), new[] { typeof(List<string>) });
        static readonly MethodInfo s_totalSmoke = AccessTools.Method(typeof(Smoke), nameof(Smoke.GetTotalSmoke));
        static readonly MethodInfo s_fadeOldest = AccessTools.Method(typeof(Smoke), nameof(Smoke.FadeOldest));

        // ---- the frame (FarFreeze.Update, after RemoteAreas.Update) ------------------------------------------------

        internal static void Update(RemoteAreas areas)
        {
            bool enabled = Plugin.Enabled.Value;
            s_on = FarFlags.Viewer && enabled;
            s_spawning = s_on && FarFlags.Spawning;
            bool near = FarFlags.SlowVisibleNear && enabled;
            if (s_on || near)
            {
                areas.CollectAwakeCores(s_cores);
                Standing(areas);
            }
            else
                s_cores.Clear();
            LabAwakeCores = s_cores.Count;
            Actives();
            Views();
            Sectors();
            HookNear(near);
            Places();
            Puffs();
        }

        // Leaving the world (FarFreeze.Clear): what was registered and hooked goes; every routed call answers as the game's.
        internal static void Clear()
        {
            s_on = s_spawning = false;
            s_views.Clear();
            s_cores.Clear();
            s_active.Clear();
            s_watched.Clear();
            s_places.Clear();
            s_farOrder.Clear();
            FarPuffs.Clear();
            s_smokeByView = false;
            s_smokeLast = null;
            Sectors();
            HookNear(false);
            LabViews = LabAwakeCores = LabSpawnPlaces = 0;
        }

        // Only the awake far areas that stand round their far portal (RemoteAreas.CoreStands) count, for the active areas,
        // the engine's wear sectors and its cores' pass: one still loading, or taken back halfway through a drain, has old
        // pieces whose neighbours are not there (yet, or again), and wear, support and settling would break or drop them for
        // good (ZDO and all). In the game what lies in the active area round a player is always loaded.
        static void Standing(RemoteAreas areas)
        {
            int kept = 0;
            for (int i = 0; i < s_cores.Count; i++)
            {
                if (!areas.CoreStands(s_cores[i].center))
                    continue;
                s_cores[kept++] = s_cores[i];
            }
            s_cores.RemoveRange(kept, s_cores.Count - kept);
        }

        // The awake far areas' active areas, with the synced simulation distance of this frame.
        static void Actives()
        {
            s_active.Clear();
            s_watched.Clear();
            ZNet net = ZNet.instance;
            ZoneSystem zs = ZoneSystem.instance;
            if (!s_on || s_cores.Count == 0 || net == null || zs == null)
                return;
            SimulationDistance sim = net.GetSyncedSimulationDistance();
            float size = zs.m_zoneSize;
            foreach (var c in s_cores)
            {
                s_active.Add(ActiveArea.Of(c.zone, sim, size));
                s_watched.Add(c.watched);
            }
        }

        // The windows rendered or shown last frame (their views are in the renderer's list until this frame's renders),
        // and those about to show that did not, at Pair·eye.
        static void Views()
        {
            s_views.Clear();
            PortalSystem sys = PortalSystem.Instance;
            PortalRenderer renderer = sys != null ? sys.Renderer : null;
            if (!s_on || renderer == null)
            {
                LabViews = 0;
                return;
            }
            // Element by element: List.AddRange copies through a temporary array.
            List<Vector3> rendered = renderer.ViewPositions;
            for (int i = 0; i < rendered.Count; i++)
                s_views.Add(rendered[i]);
            Camera cam = Utils.GetMainCamera();
            if (cam != null)
            {
                Vector3 eye = cam.transform.position;
                int frame = Time.frameCount;
                List<PortalLink> links = PortalLink.All;
                for (int i = 0; i < links.Count; i++)
                {
                    PortalLink link = links[i];
                    PortalLink far = link.Partner;
                    if (far == null || !far.FrameSet)
                        continue;
                    PortalWindow w = link.Window;
                    if (w != null && (w.RenderedAtFrame >= frame - 1 || w.ShownAtFrame >= frame - 1))
                        continue;
                    if (WindowReach.AboutToShow(link))
                        s_views.Add(PortalMath.Pair(link.Frame, far.Frame).MultiplyPoint3x4(eye));
                }
            }
            LabViews = s_views.Count;
        }

        // ---- P18: the engine's wear updater --------------------------------------------------------------------

        // The sectors of each awake far area's active area (the 3x3 zones round its far portal's zone: the active area
        // reaches 1.5 zones from that zone's centre), registered with WearNTearActive each once while Far.Viewer is on,
        // and removed as areas fall asleep, drain or go. Only changes reach the engine (it refreshes its set on each).
        static void Sectors()
        {
            s_want.Clear();
            if (s_on)
                foreach (var c in s_cores)
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            var z = new Vector2s(c.zone.x + dx, c.zone.y + dy);
                            s_want[Key(z)] = z;
                        }
            s_unwanted.Clear();
            foreach (KeyValuePair<int, Vector2s> kv in s_registered)
                if (!s_want.ContainsKey(kv.Key))
                    s_unwanted.Add(kv.Key);
            foreach (int k in s_unwanted)
            {
                Engine.WearNTearActive.RemoveSector(s_registered[k]);
                s_registered.Remove(k);
            }
            foreach (KeyValuePair<int, Vector2s> kv in s_want)
                if (!s_registered.ContainsKey(kv.Key))
                {
                    Engine.WearNTearActive.AddSector(kv.Value);
                    s_registered.Add(kv.Key, kv.Value);
                }
            LabWearSectors = s_registered.Count;
        }

        static int Key(Vector2s z) => (z.x << 16) | (ushort)z.y;

        // ---- P36: the engine's slow updates --------------------------------------------------------------------

        static void HookNear(bool on)
        {
            if (on == s_nearHooked)
                return;
            s_nearHooked = on;
            Engine.SlowUpdaterBudgetModule.ExtraNearZones = on ? s_fillNear : null;
            Engine.SlowUpdaterBudgetModule.KeepNearLatch = on;
        }

        // Asked by the engine at the start of each cores' pass: every awake far area, its zone and its near reach.
        static void FillNear(List<Engine.SlowUpdaterBudgetModule.NearCore> into)
        {
            foreach (var c in s_cores)
                into.Add(new Engine.SlowUpdaterBudgetModule.NearCore { Zone = c.zone, Radius = c.nearRadius });
        }

        // ---- the visual and AI gates ---------------------------------------------------------------------------

        // SmokeSpawner.Spawn: its 64 m to the player (SmokeFromViewer), its global count and its fade of the oldest puff
        // (SmokeTotal, SmokeFadeOldest).
        [HarmonyTranspiler, HarmonyPatch(typeof(SmokeSpawner), "Spawn")]
        static IEnumerable<CodeInstruction> SmokeGate(IEnumerable<CodeInstruction> code, MethodBase original) =>
            Route(Route(Route(code, original, s_distance, nameof(SmokeFromViewer), 1), original, s_totalSmoke, nameof(SmokeTotal)),
                original, s_fadeOldest, nameof(SmokeFadeOldest));

        // The puff a view position alone let through, noted once the spawn is done (Smoke.Awake appends each new one to the
        // game's list of puffs).
        [HarmonyFinalizer, HarmonyPatch(typeof(SmokeSpawner), "Spawn")]
        static void SmokeSpawned()
        {
            if (!s_smokeByView)
                return;
            s_smokeByView = false;
            List<Smoke> all = s_smokeList();
            Smoke made = all != null && all.Count > 0 ? all[all.Count - 1] : null;
            if (made != null && !ReferenceEquals(made, s_smokeLast) && FarPuffs.Add(made))
                s_farOrder.Add(made);
            s_smokeLast = null;
        }

        // ArmorStand.Update: the game's Vector3.Distance(player, position), or the nearest view position's distance when
        // that is nearer.
        [HarmonyTranspiler, HarmonyPatch(typeof(ArmorStand), "Update")]
        static IEnumerable<CodeInstruction> ClothGate(IEnumerable<CodeInstruction> code, MethodBase original) =>
            Route(code, original, s_distance, nameof(FromViewer), 1);

        // CharacterAnimEvent.UpdateFootIK and FootStep.UpdateFootstep: Vector3.Distance(position, camera) (the first
        // distance in each, the camera gate), or the nearest view position's.
        [HarmonyTranspiler, HarmonyPatch(typeof(CharacterAnimEvent), "UpdateFootIK")]
        static IEnumerable<CodeInstruction> FootIKGate(IEnumerable<CodeInstruction> code, MethodBase original) =>
            Route(code, original, s_distance, nameof(ToViewer), 1);

        [HarmonyTranspiler, HarmonyPatch(typeof(FootStep), "UpdateFootstep")]
        static IEnumerable<CodeInstruction> FootstepGate(IEnumerable<CodeInstruction> code, MethodBase original) =>
            Route(code, original, s_distance, nameof(ToViewer), 1);

        // MonsterAI.UpdateTarget: its target refresh's player within 50 m.
        [HarmonyTranspiler, HarmonyPatch(typeof(MonsterAI), "UpdateTarget")]
        static IEnumerable<CodeInstruction> TargetGate(IEnumerable<CodeInstruction> code, MethodBase original) =>
            Route(code, original, s_inRange, nameof(AiPlayerInRange));

        static float FromViewer(Vector3 viewer, Vector3 at) => Nearest(at, Vector3.Distance(viewer, at));

        static float ToViewer(Vector3 at, Vector3 viewer) => Nearest(at, Vector3.Distance(at, viewer));

        // SmokeSpawner.Spawn's player test: the game's distance, or a view position's when only that is within 64 m. Such
        // a puff is the window's place's, not the game's: noted as a far puff (SmokeSpawned), hidden from the game camera
        // (BaseView: the game alone shows no smoke there) and counted apart from the game's puffs.
        static float SmokeFromViewer(Vector3 viewer, Vector3 at)
        {
            float d = Vector3.Distance(viewer, at);
            s_smokeByView = false;
            if (d <= SmokeReach || !s_on)
                return d;
            float v = Nearest(at, d);
            if (v <= SmokeReach)
            {
                s_smokeByView = true;
                List<Smoke> all = s_smokeList();
                s_smokeLast = all != null && all.Count > 0 ? all[all.Count - 1] : null;
            }
            return v;
        }

        // Smoke.GetTotalSmoke() in SmokeSpawner.Spawn: the game caps its puffs at 100, fading the oldest. Far puffs are not
        // in the count the game's own spawns see, and a far spawn sees the far puffs' count, under a cap of their own: a far
        // chimney seen through a window never fades yours early.
        static int SmokeTotal()
        {
            int total = Smoke.GetTotalSmoke();
            if (s_farOrder.Count == 0)
                return s_smokeByView ? 0 : total;
            int far = 0;
            for (int i = 0; i < s_farOrder.Count; i++)
            {
                Smoke s = s_farOrder[i];
                if (s != null && s_smokeAdded(s))
                    far++;
            }
            return s_smokeByView ? far : total - far;
        }

        // Smoke.FadeOldest() in SmokeSpawner.Spawn: of the same kind as the spawn (far or the game's), the oldest.
        static void SmokeFadeOldest()
        {
            if (s_smokeByView)
            {
                for (int i = 0; i < s_farOrder.Count; i++)
                {
                    Smoke s = s_farOrder[i];
                    if (s != null && s_smokeAdded(s))
                    {
                        s.StartFadeOut();
                        return;
                    }
                }
                return;
            }
            List<Smoke> all = s_farOrder.Count > 0 ? s_smokeList() : null;
            if (all == null)
            {
                Smoke.FadeOldest();
                return;
            }
            for (int i = 0; i < all.Count; i++)
                if (all[i] != null && !FarPuffs.Contains(all[i]))
                {
                    all[i].StartFadeOut();
                    return;
                }
        }

        // Far puffs faded out and gone leave the set; with Far.Viewer off all of them (what is left counts and shows as the
        // game's, as before).
        static void Puffs()
        {
            if (s_farOrder.Count == 0)
                return;
            if (!s_on)
            {
                s_farOrder.Clear();
                FarPuffs.Clear();
                return;
            }
            int kept = 0;
            for (int i = 0; i < s_farOrder.Count; i++)
            {
                Smoke s = s_farOrder[i];
                if (s == null)
                {
                    FarPuffs.Remove(s);
                    continue;
                }
                s_farOrder[kept++] = s;
            }
            s_farOrder.RemoveRange(kept, s_farOrder.Count - kept);
        }

        static float Nearest(Vector3 at, float d)
        {
            int n = s_views.Count;
            if (!s_on || n == 0)
                return d;
            float best = d * d;
            bool nearer = false;
            for (int i = 0; i < n; i++)
            {
                float s = (s_views[i] - at).sqrMagnitude;
                if (s < best)
                {
                    best = s;
                    nearer = true;
                }
            }
            if (!nearer)
                return d;
            if (best < GateReach * GateReach)
                LabViewerGateHits++;
            return Mathf.Sqrt(best);
        }

        static bool AiPlayerInRange(Vector3 point, float range)
        {
            if (Player.IsPlayerInRange(point, range))
                return true;
            if (!s_on || !ViewWithin(point, range))
                return false;
            LabViewerGateHits++;
            return true;
        }

        // A view position within range of the point (Player.IsPlayerInRange's test, the distance below the range).
        static bool ViewWithin(Vector3 point, float range)
        {
            float r2 = range * range;
            for (int i = 0; i < s_views.Count; i++)
                if ((s_views[i] - point).sqrMagnitude < r2)
                    return true;
            return false;
        }

        // ---- an active area at awake far sides -----------------------------------------------------------------

        // WearNTear.UpdateWear: the active-area test, and the rain and snow tests by the piece's place (all three are one
        // call each in this game version). UpdateCover: its rain test, which decides whether the roof is looked for.
        [HarmonyTranspiler, HarmonyPatch(typeof(WearNTear), nameof(WearNTear.UpdateWear))]
        static IEnumerable<CodeInstruction> WearGate(IEnumerable<CodeInstruction> code, MethodBase original) =>
            RouteWithThis(RouteWithThis(Route(code, original, s_outsideHere, nameof(WearOutside)), original, s_isWet, nameof(WearIsWet)),
                original, s_snowBuildup, nameof(WearSnow));

        [HarmonyTranspiler, HarmonyPatch(typeof(WearNTear), nameof(WearNTear.UpdateCover))]
        static IEnumerable<CodeInstruction> CoverGate(IEnumerable<CodeInstruction> code, MethodBase original) =>
            RouteWithThis(code, original, s_isWet, nameof(WearIsWet));

        // StaticPhysics.SUpdate: settling and falling only inside the active area round the reference zone.
        [HarmonyTranspiler, HarmonyPatch(typeof(StaticPhysics), nameof(StaticPhysics.SUpdate))]
        static IEnumerable<CodeInstruction> SettleGate(IEnumerable<CodeInstruction> code, MethodBase original) =>
            Route(code, original, s_outsideOf, nameof(StaticOutside));

        static bool InAwakeActiveArea(Vector3 point)
        {
            for (int i = 0; i < s_active.Count; i++)
                if (s_active[i].Holds(point))
                    return true;
            return false;
        }

        static bool InWatchedActiveArea(Vector3 point)
        {
            for (int i = 0; i < s_active.Count; i++)
                if (s_watched[i] && s_active[i].Holds(point))
                    return true;
            return false;
        }

        // ZNetScene.instance.OutsideActiveArea(position), unless an awake far area's active area holds it.
        static bool WearOutside(ZNetScene scene, Vector3 point)
        {
            bool outside = scene.OutsideActiveArea(point);
            if (!outside || !s_on || !InAwakeActiveArea(point))
                return outside;
            LabActiveAreaHits++;
            return false;
        }

        // ZNetScene.OutsideActiveArea(position, referenceZone), unless an awake far area's active area holds it (SlowUpdater's
        // reference zone is the player's; SlowUpdaterBudget's cores' pass gives a far object its core's already).
        static bool StaticOutside(Vector3 point, Vector2s zone)
        {
            bool outside = ZNetScene.OutsideActiveArea(point, zone);
            if (!outside || !s_on || !InAwakeActiveArea(point))
                return outside;
            LabActiveAreaHits++;
            return false;
        }

        // EnvMan.IsWet() for the piece: its far place's rain (FarWet: the far area it stands in, when that place's rain
        // differs from yours; your own active area is always yours).
        static bool WearIsWet(WearNTear w)
        {
            if (!s_on)
                return EnvMan.IsWet();
            FarWet.Enter(w.transform.position);
            try
            {
                return EnvMan.IsWet();
            }
            finally
            {
                FarWet.Exit();
            }
        }

        // EnvMan.instance.GetSnowBuildup() for the piece: the snow its far place's weather brings (Far.LocalWeatherSim,
        // with Distance.FarWeather, as FarWet goes by it), its area's far portal's; your own active area is yours.
        static float WearSnow(EnvMan em, WearNTear w)
        {
            if (!s_on || !FarFlags.LocalWeatherSim || Plugin.FarWeather == null || !Plugin.FarWeather.Value)
                return em.GetSnowBuildup();
            PortalSystem sys = PortalSystem.Instance;
            ZNet net = ZNet.instance;
            Vector3 at = w.transform.position;
            if (sys == null || net == null || ZNetScene.InActiveArea(at, ZoneSystem.GetZone(net.GetReferencePosition()))
                || !sys.Areas.AreaOf(at, out Vector2s _, out Vector3 center, out ZDOID _))
                return em.GetSnowBuildup();
            EnvSetup env = FarEnvironment.LocalWeather(center);
            return env != null ? env.m_snowBuildup : em.GetSnowBuildup();
        }

        // ---- Far.Spawning ----------------------------------------------------------------------------------------

        static bool SpawnReady()
        {
            if (s_spawnReady || s_spawnBroken)
                return s_spawnReady;
            try
            {
                s_spawnView = AccessTools.FieldRefAccess<SpawnSystem, ZNetView>("m_nview");
                s_activeEvent = AccessTools.FieldRefAccess<RandEventSystem, RandomEvent>("m_activeEvent");
                s_randomEvent = AccessTools.FieldRefAccess<RandEventSystem, RandomEvent>("m_randomEvent");
                s_forcedEvent = AccessTools.FieldRefAccess<RandEventSystem, RandomEvent>("m_forcedEvent");
                s_spawnReady = true;
            }
            catch (Exception e)
            {
                s_spawnBroken = true;
                Plugin.Log.LogError("Far.Spawning: not set up (game update?), nothing spawns at far sides: " + e);
            }
            return s_spawnReady;
        }

        // Where the local player would stand at each watched far side: Pair·position through a live window seen within
        // 3 s (or approached) onto an awake far area whose window was seen within 3 s, when that lies in the far area's
        // active area and not in his own (the game spawns round him there already). One per zone.
        static void Places()
        {
            s_places.Clear();
            LabSpawnPlaces = 0;
            Player p = Player.m_localPlayer;
            ZNet net = ZNet.instance;
            if (!s_spawning || p == null || net == null || s_active.Count != s_cores.Count || !SpawnReady())
                return;
            Vector3 at = p.transform.position;
            Vector2s mine = ZoneSystem.GetZone(net.GetReferencePosition());
            List<PortalLink> links = PortalLink.All;
            for (int i = 0; i < links.Count; i++)
            {
                PortalLink link = links[i];
                PortalLink far = link.Partner;
                ZDO farZdo = link.PartnerZdo;
                if (link.Vanilla || far == null || farZdo == null || !link.FrameSet || !far.FrameSet || !link.Watched)
                    continue;
                int core = CoreOf(farZdo.GetPosition());
                if (core < 0 || !s_watched[core])
                    continue;
                Vector3 there = PortalMath.Pair(link.Frame, far.Frame).MultiplyPoint3x4(at);
                if (!s_active[core].Holds(there) || ZNetScene.InActiveArea(there, mine))
                    continue;
                Vector2s zone = ZoneSystem.GetZone(there);
                bool known = false;
                foreach (SpawnPlace sp in s_places)
                    if (sp.Zone == zone)
                    {
                        known = true;
                        break;
                    }
                if (!known)
                    s_places.Add(new SpawnPlace { Zone = zone, At = there, Center = s_cores[core].center });
            }
            LabSpawnPlaces = s_places.Count;
        }

        // The awake far area of this far portal (its centre is the far portal's position), or -1.
        static int CoreOf(Vector3 farPortal)
        {
            for (int i = 0; i < s_cores.Count; i++)
                if ((s_cores[i].center - farPortal).sqrMagnitude < 1f)
                    return i;
            return -1;
        }

        // SpawnSystem.UpdateSpawning (every second, each zone's): the zone of such a place runs it with the local player
        // there, as the game runs it for the zone he stands in. Only the zone's owner does anything (the game's own test,
        // asked first so nobody is moved for nothing).
        [HarmonyPrefix, HarmonyPatch(typeof(SpawnSystem), "UpdateSpawning")]
        static void SpawnSystemIn(SpawnSystem __instance, out bool __state)
        {
            __state = false;
            if (s_places.Count == 0 || !s_spawning || s_inSpawn)
                return;
            Player p = Player.m_localPlayer;
            ZNetView view = s_spawnView(__instance);
            if (p == null || view == null || !view.IsValid() || !view.IsOwner())
                return;
            Vector2s zone = ZoneSystem.GetZone(__instance.transform.position);
            foreach (SpawnPlace sp in s_places)
            {
                if (sp.Zone != zone)
                    continue;
                Transform t = p.transform;
                s_playerLocal = t.localPosition;
                s_spawnCenter = sp.Center;
                t.position = sp.At;
                s_inSpawn = true;
                EventAt(sp.At);
                __state = true;
                return;
            }
        }

        [HarmonyFinalizer, HarmonyPatch(typeof(SpawnSystem), "UpdateSpawning")]
        static void SpawnSystemOut(bool __state)
        {
            if (!__state)
                return;
            s_inSpawn = false;
            Player p = Player.m_localPlayer;
            if (p != null)
                p.transform.localPosition = s_playerLocal;
            EventBack();
        }

        // RandEventSystem.UpdateActiveEvent's rule at that place (the event a player standing there would have active),
        // for GetCurrentSpawners during the call; the player's own is put back after.
        static void EventAt(Vector3 at)
        {
            s_eventSet = false;
            RandEventSystem res = RandEventSystem.instance;
            if (res == null)
                return;
            RandomEvent random = s_randomEvent(res);
            RandomEvent there = s_forcedEvent(res)
                                ?? (random != null && at.y <= 3000f && Utils.DistanceXZ(at, random.m_pos) < random.m_eventRange ? random : null);
            RandomEvent active = s_activeEvent(res);
            if (ReferenceEquals(there, active))
                return;
            s_eventWas = active;
            s_activeEvent(res) = there;
            s_eventSet = true;
        }

        static void EventBack()
        {
            if (!s_eventSet)
                return;
            s_eventSet = false;
            RandEventSystem res = RandEventSystem.instance;
            if (res != null)
                s_activeEvent(res) = s_eventWas;
            s_eventWas = null;
        }

        [HarmonyPrefix, HarmonyPatch(typeof(SpawnSystem), "Spawn")]
        static void SpawnSystemSpawns()
        {
            if (s_inSpawn && !SpawnSystem.m_nospawn)
                LabFarSpawns++;
        }

        // SpawnSystem.UpdateSpawnList: a spawn that needs certain weather looks at that place's (FarEnvironment.LocalWeather
        // of its area's far portal; null is yours).
        [HarmonyTranspiler, HarmonyPatch(typeof(SpawnSystem), "UpdateSpawnList")]
        static IEnumerable<CodeInstruction> SpawnListWeather(IEnumerable<CodeInstruction> code, MethodBase original) =>
            Route(code, original, s_isEnvironment, nameof(SpawnEnvironment));

        static bool SpawnEnvironment(EnvMan em, List<string> names)
        {
            if (!s_inSpawn)
                return em.IsEnvironment(names);
            EnvSetup env = FarEnvironment.LocalWeather(s_spawnCenter);
            return env != null ? names.Contains(env.m_name) : em.IsEnvironment(names);
        }

        // CreatureSpawner.UpdateSpawner, SpawnArea.UpdateSpawn, LootSpawner.UpdateSpawner: their player range tests (and
        // the spawn area's active-area test) count the viewer.
        [HarmonyTranspiler, HarmonyPatch(typeof(CreatureSpawner), "UpdateSpawner")]
        static IEnumerable<CodeInstruction> CreatureSpawnerGate(IEnumerable<CodeInstruction> code, MethodBase original) =>
            Route(Route(code, original, s_inRangeNoise, nameof(SpawnerInRangeNoise)), original, s_inRange, nameof(SpawnerInRange));

        [HarmonyTranspiler, HarmonyPatch(typeof(SpawnArea), "UpdateSpawn")]
        static IEnumerable<CodeInstruction> SpawnAreaGate(IEnumerable<CodeInstruction> code, MethodBase original) =>
            Route(Route(code, original, s_outsideHere, nameof(SpawnAreaOutside)), original, s_inRange, nameof(SpawnerInRange));

        [HarmonyTranspiler, HarmonyPatch(typeof(LootSpawner), "UpdateSpawner")]
        static IEnumerable<CodeInstruction> LootSpawnerGate(IEnumerable<CodeInstruction> code, MethodBase original) =>
            Route(code, original, s_inRange, nameof(SpawnerInRange));

        // Player.IsPlayerInRange(point, range), or with Far.Spawning a viewer at a view position within range of a point
        // inside a watched awake far area's active area.
        static bool SpawnerInRange(Vector3 point, float range)
        {
            if (Player.IsPlayerInRange(point, range))
                return true;
            if (!s_spawning || !InWatchedActiveArea(point) || !ViewWithin(point, range))
                return false;
            s_gate = true;
            return true;
        }

        // The same with a noise trigger: the viewer is you, looking through the window, so it is your noise.
        static bool SpawnerInRangeNoise(Vector3 point, float range, float minNoise)
        {
            if (Player.IsPlayerInRange(point, range, minNoise))
                return true;
            if (!s_spawning || !InWatchedActiveArea(point) || !ViewWithin(point, range))
                return false;
            Player p = Player.m_localPlayer;
            float noise = p != null ? p.GetNoiseRange() : 0f;
            if (!(range <= noise) || !(noise >= minNoise))
                return false;
            s_gate = true;
            return true;
        }

        // ZNetScene.instance.OutsideActiveArea(position), unless (Far.Spawning) a watched awake far area's active area holds it.
        static bool SpawnAreaOutside(ZNetScene scene, Vector3 point) =>
            scene.OutsideActiveArea(point) && !(s_spawning && InWatchedActiveArea(point));

        // LabFarSpawns: what those spawners make while their update's range test was met by a view position only.
        [HarmonyPostfix, HarmonyPatch(typeof(CreatureSpawner), "Spawn")]
        static void CreatureSpawned(ZNetView __result)
        {
            if (s_gate && __result != null)
                LabFarSpawns++;
        }

        [HarmonyPostfix, HarmonyPatch(typeof(SpawnArea), "SpawnOne")]
        static void AreaSpawned(bool __result)
        {
            if (s_gate && __result)
                LabFarSpawns++;
        }

        [HarmonyFinalizer, HarmonyPatch(typeof(CreatureSpawner), "UpdateSpawner")]
        static void CreatureSpawnerDone() => s_gate = false;

        [HarmonyFinalizer, HarmonyPatch(typeof(SpawnArea), "UpdateSpawn")]
        static void SpawnAreaDone() => s_gate = false;

        // The loot spawner's range test is its last: met, the loot is dropped right after it.
        [HarmonyFinalizer, HarmonyPatch(typeof(LootSpawner), "UpdateSpawner")]
        static void LootSpawnerDone()
        {
            if (s_gate)
                LabFarSpawns++;
            s_gate = false;
        }

        // ---- routing -----------------------------------------------------------------------------------------

        // The method's calls to `call` (the first `count`, 0: all) go to FarViewer's `ours` instead: a static method that
        // takes the same stack (the instance first, for an instance method) and asks the game's call first. A method with
        // no such call (a game update, or another mod's rewrite before ours) keeps the game's test, logged.
        static List<CodeInstruction> Route(IEnumerable<CodeInstruction> code, MethodBase original, MethodInfo call, string ours, int count = 0)
        {
            List<CodeInstruction> list = code as List<CodeInstruction> ?? new List<CodeInstruction>(code);
            MethodInfo to = AccessTools.DeclaredMethod(typeof(FarViewer), ours);
            int n = 0;
            if (call != null && to != null)
                foreach (CodeInstruction ci in list)
                    if ((count == 0 || n < count) && ci.Calls(call))
                    {
                        ci.opcode = OpCodes.Call;
                        ci.operand = to;
                        n++;
                    }
            if (n == 0)
                Missing(original, call);
            return list;
        }

        // The same for a replacement that also takes the method's `this` after the call's own arguments: the call's place
        // becomes the load of `this` (it keeps its labels and blocks, so a jump to the call lands on the load) and the new
        // call follows it. Instance methods only.
        static List<CodeInstruction> RouteWithThis(IEnumerable<CodeInstruction> code, MethodBase original, MethodInfo call, string ours)
        {
            List<CodeInstruction> list = code as List<CodeInstruction> ?? new List<CodeInstruction>(code);
            MethodInfo to = AccessTools.DeclaredMethod(typeof(FarViewer), ours);
            int n = 0;
            if (call != null && to != null && original != null && !original.IsStatic)
                for (int i = 0; i < list.Count; i++)
                    if (list[i].Calls(call))
                    {
                        list[i].opcode = OpCodes.Ldarg_0;
                        list[i].operand = null;
                        list.Insert(++i, new CodeInstruction(OpCodes.Call, to));
                        n++;
                    }
            if (n == 0)
                Missing(original, call);
            return list;
        }

        static void Missing(MethodBase original, MethodInfo call) =>
            Plugin.Log.LogWarning($"Far.Viewer: {original?.DeclaringType?.Name}.{original?.Name} has no call to " +
                                  $"{call?.DeclaringType?.Name}.{call?.Name} (game update, or another mod rewrote it); that test stays the game's.");
    }
}
