using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Place = RealPortal.Engine.HeightmapIdleModule.Place;

namespace RealPortal
{
    // Far.DrainCalm (Docs\PORTALS20-PLAN.md, A2). A terrain modifier (levelled ground, a location's flattened patch)
    // that is destroyed has every heightmap it reaches built again a couple of frames later (TerrainModifier.OnDestroy
    // pokes them): right where the zones around stay, as when you walk off and the edge of your area unloads. A far
    // area that unloads takes its zones with it, yet each of its modifiers had the heightmaps around rebuilt first,
    // seconds before they unloaded themselves: 224-322 ms of MonoUpdaters.LateUpdate a tick, ten ticks in a row, after
    // leaving twenty portals. Those pokes are left out (RemoteAreas.UnloadsQuietly says which); the heightmaps keep the
    // modifier's shape until they unload. Off: the game's call, untouched.
    [HarmonyPatch]
    internal static class FarDrain
    {
        // Lab: pokes left out.
        internal static int LabQuietPokes;

        // Only on the way out: Awake pokes with forcedDelay as a modifier comes in, OnDestroy without.
        [HarmonyPrefix, HarmonyPatch(typeof(TerrainModifier), "PokeHeightmaps")]
        static bool PokeHeightmaps(TerrainModifier __instance, bool forcedDelay)
        {
            if (!FarFlags.DrainCalm || forcedDelay)
                return true;
            RemoteAreas areas = PortalSystem.Instance?.Areas;
            if (areas == null || !areas.UnloadsQuietly(__instance.transform.position))
                return true;
            LabQuietPokes++;
            return false;
        }
    }

    // Far.HeightmapDrainSkip and Far.FarRegenBudget (PORTALS20-PLAN.md §7): where a heightmap whose rebuild is queued
    // stands, for the engine's heightmap pass (HeightmapIdleModule.PlaceOf, set while either switch is on: Switched).
    // That pass (MonoUpdaters.LateUpdate) rebuilds exactly the heightmaps a terrain modifier reached as it came in or
    // went (TerrainModifier.PokeHeightmaps, Poke(2); a TerrainComp, a Heightmap or a WaterVolume going pokes nothing).
    // A far area's locations stand in its zones and, as distant objects, in the ring of zones around them out to the
    // simulation distance, where no ground is loaded. Their modifiers reach the loaded heightmaps nearby: as the area
    // loads, tens of rebuilds pile into one pass (30-53 ms); as it unloads, Far.DrainCalm leaves out the pokes of
    // modifiers standing in its zones, but not of the ring's (their own zone is no area's), whose squares reach into the
    // area's outermost zones: the drain ticks' 35-170 ms passes after leaving twenty portals, rebuilding heightmaps that
    // unload a few seconds later. So the place is the heightmap's own zone, whatever poked it:
    //  - Near: within the player's zones and one more (never held back); within the zones of a far area this game
    //    simulates (RemoteAreas.AreaSimulated: its near objects claimed) and one more, since what stands there is owned
    //    here and the game rebuilds poked ground before the next physics step; everything while both switches are off.
    //    Far.VisibleNear (Docs\PARITY-PLAN.md P28): also within the near zones of a far area whose window is live or about
    //    to show, or which loads the half a window will show (RemoteAreas.ShowsZone): a location's ground showed unlevelled
    //    in a window for frames after go-live or a wake, and a half waits for none of its rebuilds to be queued (P4).
    //  - Unloading (Far.HeightmapDrainSkip): a zone a draining area lists, or listed as it drained away (while the zone
    //    is still loaded, for at most GoneSeconds), that no live area reaches (its zones and the ring of its distant
    //    objects) and that lies beyond the player's whole simulation distance and one zone more. Not rebuilt: it unloads
    //    with its zone; should the area or the player take the zone back, it is placed again and rebuilt then.
    //  - Far (Far.FarRegenBudget): anything else; a few rebuilds a frame. Without the switch, Near.
    internal static class FarHeightmaps
    {
        // The game unloads one zone every 0.1 s, once its objects are gone: twenty areas' ~740 zones take some 75 s.
        const float GoneSeconds = 120f;
        // The zones listed again at least this often (those unloaded since are dropped), and whenever areas change.
        const float RefreshSeconds = 1f;

        internal static readonly Func<Vector3, Place> Hook = PlaceOf;

        // Zone (Key) -> when a draining area last listed it; two maps swapped at each refresh.
        static Dictionary<int, float> s_unloading = new Dictionary<int, float>();
        static Dictionary<int, float> s_next = new Dictionary<int, float>();
        // The live areas' portal zones, and how far an area reaches from it (its distant ring included).
        static readonly List<Vector2s> s_live = new List<Vector2s>();
        static int s_liveReach;
        // The player's zones and one more; the player's whole simulation distance and one more; a far area's zones and
        // one more.
        static int s_near, s_whole, s_areaNear;
        static RemoteAreas s_areas;
        static int s_version = -1;
        static float s_refreshAt;
        static int s_meFrame = -1;
        static Vector2s s_me;

        // Lab: answers given (a queued heightmap is asked once a pass, one held as unloading every 0.5 s).
        internal static long LabNear, LabFar, LabUnloading;

        // Either switch moved (WindowFeatures' Bind; also as they register): the hook is set only while one is on, so
        // with both off the engine's pass is the game's, and nothing placed under the old switches is kept (a held
        // Unloading answer would be reused for up to 0.5 s; what is still queued is placed afresh by the next pass).
        internal static void Switched()
        {
            Engine.HeightmapIdleModule.PlaceOf = FarFlags.HeightmapDrainSkip || FarFlags.FarRegenBudget ? Hook : null;
            Engine.HeightmapIdleModule.ForgetPlaces();
        }

        static Place PlaceOf(Vector3 at)
        {
            bool skip = FarFlags.HeightmapDrainSkip, budget = FarFlags.FarRegenBudget;
            ZNet net = ZNet.instance;
            if (!skip && !budget || net == null || ZoneSystem.instance == null)
                return Place.Near;
            int frame = Time.frameCount;
            if (frame != s_meFrame)
            {
                s_meFrame = frame;
                s_me = ZoneSystem.GetZone(net.GetReferencePosition());
            }
            RemoteAreas areas = PortalSystem.Instance?.Areas;
            float now = Time.time;
            if (!ReferenceEquals(areas, s_areas) || areas != null && areas.AreasVersion != s_version || !(now < s_refreshAt))
                Refresh(areas, net, now);
            Vector2s zone = ZoneSystem.GetZone(at);
            int d = Distance(zone, s_me);
            if (d <= s_near)
            {
                LabNear++;
                return Place.Near;
            }
            if (skip && d > s_whole && s_unloading.Count > 0 && s_unloading.ContainsKey(Key(zone)) && !Live(zone))
            {
                LabUnloading++;
                return Place.Unloading;
            }
            if (!budget || Simulated(areas, zone) || (FarFlags.VisibleNear && areas != null && areas.ShowsZone(zone)))
            {
                LabNear++;
                return Place.Near;
            }
            LabFar++;
            return Place.Far;
        }

        static void Refresh(RemoteAreas areas, ZNet net, float now)
        {
            if (!ReferenceEquals(areas, s_areas))
                s_unloading.Clear();    // another world or session: nothing of the last one is held
            s_areas = areas;
            s_version = areas != null ? areas.AreasVersion : -1;
            s_refreshAt = now + RefreshSeconds;
            ZoneSystem zs = ZoneSystem.instance;
            // What areas that drained away listed stays while still loaded (and not for ever: another mod may keep
            // such a zone loaded); what draining areas list now is added.
            s_next.Clear();
            foreach (KeyValuePair<int, float> kv in s_unloading)
                if (now - kv.Value < GoneSeconds && now >= kv.Value && zs.IsZoneLoaded(Unkey(kv.Key)))
                    s_next[kv.Key] = kv.Value;
            s_live.Clear();
            if (areas != null)
                for (int i = 0; i < areas.Count; i++)
                {
                    if (!areas.AreaDraining(i))
                    {
                        s_live.Add(areas.AreaZone(i));
                        continue;
                    }
                    List<Vector2s> zones = areas.AreaZones(i);
                    for (int k = 0; k < zones.Count; k++)
                        s_next[Key(zones[k])] = now;
                }
            Dictionary<int, float> was = s_unloading;
            s_unloading = s_next;
            s_next = was;
            s_next.Clear();
            SimulationDistance mine = net.GetSyncedSimulationDistance();
            s_near = mine.NearSimulationDistance + 1;
            s_whole = mine.TotalSimulationDistance + 1;
            SimulationDistance area = RemoteAreas.AreaDistance();
            s_liveReach = area.TotalSimulationDistance;
            s_areaNear = area.NearSimulationDistance + 1;
        }

        // A live area holds the zone: its own zones, or the ring of its distant objects (that ground can show in its
        // window, and keeps a zone loaded while any of them stands there).
        static bool Live(Vector2s zone)
        {
            for (int i = 0; i < s_live.Count; i++)
                if (Distance(zone, s_live[i]) <= s_liveReach)
                    return true;
            return false;
        }

        // A far area this game simulates holds the zone: its near zones and one more, as the player's own. What stands
        // there is owned here and runs (creatures, items, spawners); the game has a poked heightmap rebuilt before the
        // next physics step, so nothing there is made or moved onto stale ground. Read live (at most one look per area).
        static bool Simulated(RemoteAreas areas, Vector2s zone)
        {
            if (areas == null)
                return false;
            for (int i = 0; i < areas.Count; i++)
                if (areas.AreaSimulated(i) && Distance(zone, areas.AreaZone(i)) <= s_areaNear)
                    return true;
            return false;
        }

        static int Distance(Vector2s a, Vector2s b) => Math.Max(Math.Abs(a.x - b.x), Math.Abs(a.y - b.y));

        static int Key(Vector2s zone) => (zone.x << 16) | (ushort)zone.y;

        static Vector2s Unkey(int key) => new Vector2s((short)(key >> 16), (short)key);
    }
}
