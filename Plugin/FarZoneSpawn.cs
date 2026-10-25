using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace RealPortal
{
    // Far.ZoneSpawnSplit (round 3). What a zone spawn is (ZoneSystem.PokeLocalZone -> SpawnZone): the zone's root (the
    // _Zone prefab; its Heightmap builds its render mesh, collision mesh and collider in OnEnable from the base heights
    // HeightmapBuilder made off the main thread) is instantiated; then, only where this game is the server and the zone
    // was never generated (SpawnMode.Full), PlaceLocations (the location due there, if any: every networked piece of
    // it instantiated, a dungeon generated), PlaceVegetation (for every vegetation kind of the zone's biomes a seeded
    // scatter: rays cast down onto the new collider, and every tree, rock, bush and pickable instantiated with its ZDO),
    // PlaceZoneCtrl, and the zone is marked generated; PokeLocalZone then keeps the root as a loaded zone. On hall 20
    // (warm saves: 11 of the 20 far ends stand where nothing was generated yet) a generated zone took about 5 ms (the
    // root alone) and a never-generated one 8-23 ms, the rest being the scatter.
    // The root is one step (its heightmap regenerates inside Instantiate; splitting that needs Heightmap patched). The
    // scatter is not: each vegetation kind seeds the random generator from the world seed, the zone and its own name,
    // and PlaceVegetation restores the state after, so the kinds placed one call each, in the game's order, with the
    // same clear areas, scatter as one call does.
    //
    // What this does: a far zone the game would generate here is begun with its root only (SpawnZone with
    // SpawnMode.Client: the game's own checks that its terrain and its location are ready, then the root); the
    // location with the zone's whole scatter (one part), or else each vegetation kind, and the zone control object
    // follow as parts in later frames, each when the loading budget left covers what it usually takes
    // (RemoteAreas.ZoneParts), and the last part marks the zone
    // generated and hands its root to PokeLocalZone, which keeps it as a loaded zone. Until then the zone is neither
    // generated nor loaded: RemoteAreas waits for it (its objects, its core, its area's readiness) as for any zone not
    // there yet, and what the parts make is kept by RemoteAreas.Track like anything new in a far area.
    // Never half-placed, never placed twice:
    //  - one zone at a time, never within the player's whole simulation distance and one more (his own zones and the
    //    ghost zones the game generates around him), never with another player connected (a part-placed zone could
    //    reach them: their own area, or a far end PortalServer sends them), never where another mod patches SpawnZone,
    //    PlaceLocations, PlaceVegetation or PlaceZoneCtrl (looked for every 10 s): those zones spawn whole, as before;
    //  - the game asking for such a zone (SpawnZone: the player walked into it, ghost zones around a player) has it
    //    finished on the spot first and handed over (the prefix below), so it is generated once;
    //  - a world save (ZNet.WorldSaveStarted, before anything is copied) finishes every zone under way first;
    //  - the switch going off, another player connecting or another mod's patch appearing finishes it at once
    //    (Tend), and so does its area going (no loading area went on with it this frame: Sweep), since what the parts
    //    made would no longer be kept while the rest is placed. A scene that is gone forgets it (its logout save
    //    finished it).
    // Where it differs from one call: between parts the game runs frames. Physics steps, and heightmap rebuilds another
    // poke queued may run before later kinds are placed, whose rays then see that ground and the objects placed so far
    // where they stand then. A location's levelled ground is not among them: its rebuild is queued too, so the game's
    // call scatters on the ground before it, and the location's part places the whole scatter with it. A location
    // registered in, or removed from, the zone while it is under way is taken as if that came just before or after
    // its spawn. A part that throws is logged and counted as placed (the game's call would have left the zone
    // ungenerated, to be generated again over what it had placed).
    [HarmonyPatch]
    internal static class FarZoneSpawn
    {
        sealed class Job
        {
            public Vector2s Zone;
            public Vector3 Pos;
            public GameObject Root;
            public Heightmap Hmap;
            // The location due there when it began (null: none), for the cost of its part.
            public ZoneSystem.ZoneLocation Location;
            // -1: the location with every vegetation kind; 0..Veg.Count-1: that vegetation kind; Veg.Count: the zone
            // control and the hand-over.
            public int Next;
            // The kinds the game would place there (enabled, of the zone's biomes), in its order, as they were then.
            public readonly List<ZoneSystem.ZoneVegetation> Veg = new List<ZoneSystem.ZoneVegetation>();
            // List<ZoneSystem.ClearArea>: the location's clear area for the scatter.
            public object ClearAreas;
            public IList ClearList;
            public int SeenFrame;
            public float BegunAt;
            public bool Handing;
        }

        delegate bool SpawnZoneCall(ZoneSystem zs, Vector2s zoneID, ZoneSystem.SpawnMode mode, out GameObject root);

        // Another mod patching what this calls part by part is looked for this often (real seconds).
        const float ForeignCheckEvery = 10f;
        const string LabOwners = "valheim.realportal.lab";
        // A part waiting for its location's prefab or for a root (real seconds since the zone began) is placed anyway
        // past this, as the game's call would (it loads the prefab on the spot).
        const float StallSeconds = 5f;

        // 0 not looked up yet, 1 ready, -1 not this game's zone spawning (zones spawn whole).
        static int s_init;
        static SpawnZoneCall s_spawnZone;
        static Func<ZoneSystem, Vector2s, bool> s_pokeZone, s_isGenerated;
        static Action<ZoneSystem, Vector2s> s_setGenerated;
        static Func<ZoneSystem, ZoneSystem.ZoneLocation, bool, bool> s_canSpawnLocation;
        static Action<ZoneSystem, Vector2s, Vector3, ZoneSystem.SpawnMode, List<GameObject>> s_placeZoneCtrl;
        static Action<ZoneSystem, Vector2s, Vector3, Transform, Heightmap, object, List<GameObject>> s_placeLocations, s_placeVegetation;
        static Type s_clearList;
        static MethodBase[] s_watched;

        static readonly List<Job> s_jobs = new List<Job>();
        static readonly List<Job> s_pool = new List<Job>();
        static ZoneSystem s_zs;
        // What PlaceVegetation walks for one kind; what Full mode never fills (ghost spawns list their objects there).
        static readonly List<ZoneSystem.ZoneVegetation> s_one = new List<ZoneSystem.ZoneVegetation>(1);
        static readonly List<GameObject> s_spawned = new List<GameObject>();
        // SpawnZone called from here for a zone under way (its root again): the prefix lets it through.
        static bool s_self;
        static bool s_saveHooked;
        static readonly Action s_onSave = OnWorldSave;
        static float s_foreignAt = -1f;
        static bool s_foreign;

        // What each kind of part usually takes (ms, a moving average): vegetation by kind, a location by location,
        // and the zone control with the hand-over.
        static readonly Dictionary<ZoneSystem.ZoneVegetation, float> s_vegMs = new Dictionary<ZoneSystem.ZoneVegetation, float>();
        static readonly Dictionary<ZoneSystem.ZoneLocation, float> s_locationMs = new Dictionary<ZoneSystem.ZoneLocation, float>();
        static float s_finishMs;

        // Lab: zones begun split, zones that stood through their last part, parts (vegetation kinds, locations), ms of
        // all parts and the slowest, zones finished on the spot because the game asked for them, finished at once
        // (switched off, another player, another mod, a world save) or because no loading area went on with them,
        // zones the game generates here that spawned whole with the switch on (near the player, another player, another
        // mod, one under way), the most seconds from begun to standing, and parts that threw.
        internal static int LabBegun, LabStood, LabParts, LabKinds, LabLocations, LabAsked, LabForced, LabLeft, LabKeptWhole, LabErrors;
        internal static double LabPartMs, LabPartMsMax, LabOpenSecondsMax;

        // Zone spawns under way (one at most, the game's own spawns aside).
        internal static int Count => s_jobs.Count;

        internal static bool Holds(Vector2s zone) => Find(zone) != null;

        // The zone of the spawn under way (the oldest, should there be more).
        internal static bool Current(out Vector2s zone)
        {
            zone = s_jobs.Count > 0 ? s_jobs[0].Zone : default;
            return s_jobs.Count > 0;
        }

        // Holds, and the zone's spawn is marked as still wanted this frame (RemoteAreas comes by it in a loading area).
        internal static bool Attend(Vector2s zone)
        {
            Job j = Find(zone);
            if (j == null)
                return false;
            j.SeenFrame = Time.frameCount;
            return true;
        }

        // What the zone's next part usually takes (ms; 0 while not known).
        internal static float NextCost(Vector2s zone)
        {
            Job j = Find(zone);
            float ms = 0f;
            if (j == null)
                return ms;
            if (j.Next < 0)
            {
                if (j.Location != null)
                    s_locationMs.TryGetValue(j.Location, out ms);
            }
            else if (j.Next < j.Veg.Count)
                s_vegMs.TryGetValue(j.Veg[j.Next], out ms);
            else
                ms = s_finishMs;
            return ms;
        }

        // Begins a far zone the game has not loaded (with the switch on, in place of PokeLocalZone): 1 begun (its root
        // stands, the rest follows through Part), -1 not now (its terrain or its location's prefab is not ready;
        // nothing done), Whole: not split, the caller spawns it whole through PokeLocalZone, as without the switch.
        internal const int Whole = -2;
        internal static int Begin(ZoneSystem zs, Vector2s zone)
        {
            int plan = Plan(zs, zone);
            if (plan < 2)
            {
                if (plan == 1)
                    LabKeptWhole++;
                return Whole;
            }
            if (!s_spawnZone(zs, zone, ZoneSystem.SpawnMode.Client, out GameObject root))
                return -1;
            Heightmap hmap = root != null ? root.GetComponentInChildren<Heightmap>() : null;
            if (hmap == null)
            {
                // Not a zone the scatter can stand on (the game's own call would throw): spawned whole after all.
                if (root != null)
                    UnityEngine.Object.Destroy(root);
                LabKeptWhole++;
                return Whole;
            }
            Job j;
            if (s_pool.Count > 0)
            {
                j = s_pool[s_pool.Count - 1];
                s_pool.RemoveAt(s_pool.Count - 1);
            }
            else
            {
                j = new Job();
                j.ClearAreas = Activator.CreateInstance(s_clearList);
                j.ClearList = (IList)j.ClearAreas;
            }
            j.Zone = zone;
            j.Pos = ZoneSystem.GetZonePos(zone);
            j.Root = root;
            j.Hmap = hmap;
            j.ClearList.Clear();
            j.Location = zs.m_locationInstances.TryGetValue(zone, out ZoneSystem.LocationInstance li) && !li.m_placed ? li.m_location : null;
            j.Next = j.Location != null ? -1 : 0;
            j.Veg.Clear();
            // PlaceVegetation's own first test, made once (the zone's corner biomes are its heightmap's from now on).
            foreach (ZoneSystem.ZoneVegetation veg in zs.m_vegetation)
                if (veg.m_enable && hmap.HaveBiome(veg.m_biome))
                    j.Veg.Add(veg);
            j.SeenFrame = Time.frameCount;
            j.BegunAt = Time.realtimeSinceStartup;
            j.Handing = false;
            s_zs = zs;
            s_jobs.Add(j);
            LabBegun++;
            if (!s_saveHooked)
            {
                s_saveHooked = true;
                ZNet.WorldSaveStarted = (Action)Delegate.Combine(ZNet.WorldSaveStarted, s_onSave);
            }
            return 1;
        }

        // The zone's next part: 1 the zone stands (generated and loaded), 0 one part placed, -1 waiting (its location's
        // prefab is loading again; nothing done).
        internal static int Part(Vector2s zone)
        {
            Job j = Find(zone);
            if (j == null)
                return 1;
            if (j.Next < j.Veg.Count)
                return PlacePart(j, false);
            Finish(j, true);
            return 1;
        }

        // Each frame before the loading steps, while a spawn is under way: finished at once where splitting may no
        // longer go on, forgotten with a scene that is gone.
        internal static void Tend(ZoneSystem zs)
        {
            if (s_jobs.Count == 0)
                return;
            if (!ReferenceEquals(zs, s_zs) || s_zs == null)
            {
                Drop();
                return;
            }
            string why = null;
            if (!FarFlags.ZoneSpawnSplit)
                why = "switched off";
            else if (ZNet.instance != null && ZNet.instance.GetPeers().Count > 0)
                why = "another player connected";
            else if (Foreign())
                why = "another mod patches zone spawning";
            if (why != null)
                FinishAll(why);
        }

        // After the loading steps: a spawn no loading area went on with this frame (its area drains) is finished at
        // once; its area no longer keeps what the parts made while the rest is placed.
        internal static void Sweep()
        {
            for (int i = s_jobs.Count - 1; i >= 0; i--)
            {
                if (i >= s_jobs.Count)
                    continue;
                Job j = s_jobs[i];
                if (j.SeenFrame == Time.frameCount)
                    continue;
                LabLeft++;
                Complete(j, true);
            }
        }

        // Lab and the 10 s log line.
        internal static string Describe() =>
            $"zone split: {LabBegun} begun, {LabStood} stood, {s_jobs.Count} under way; {LabParts} parts ({LabKinds} vegetation kinds, " +
            $"{LabLocations} locations), {LabPartMs / Math.Max(1, LabParts):0.00} ms a part, slowest {LabPartMsMax:0.0} ms; finished at once: " +
            $"{LabAsked} asked for by the game, {LabLeft} left by their area, {LabForced} forced; {LabKeptWhole} spawned whole, " +
            $"longest {LabOpenSecondsMax:0.0} s under way, {LabErrors} errors";

        // ---- the parts ------------------------------------------------------------------------------------------

        // The location with every vegetation kind, or the next vegetation kind (PlaceKind). Now: the zone is
        // finished at once, and a location prefab that is loading again is loaded on the spot (SpawnLocation's Load),
        // as in the game's call.
        static int PlacePart(Job j, bool now)
        {
            ZoneSystem zs = s_zs;
            // Not left waiting for long: a zone under way holds every other zone back.
            now |= Time.realtimeSinceStartup - j.BegunAt > StallSeconds;
            if (j.Root == null && !Reroot(j))
            {
                if (!now)
                    return -1;
                // No ground to place on (nothing in the game destroys a root it does not know): the rest is left out,
                // the zone still finished, so it is never left half.
                j.Next = j.Veg.Count;
                return 0;
            }
            long t0 = Stopwatch.GetTimestamp();
            if (j.Next < 0)
            {
                if (!now && zs.m_locationInstances.TryGetValue(j.Zone, out ZoneSystem.LocationInstance li) && !li.m_placed && !s_canSpawnLocation(zs, li.m_location, true))
                    return -1;
                j.Next = 0;
                try
                {
                    s_placeLocations(zs, j.Zone, j.Pos, j.Root.transform, j.Hmap, j.ClearAreas, s_spawned);
                }
                catch (Exception e)
                {
                    Error("its location", e);
                }
                s_spawned.Clear();
                // Its whole scatter with it, before any frame: its TerrainModifiers only queue their heightmap
                // rebuild (PokeHeightmaps, forced delay), which MonoUpdaters.LateUpdate runs, so in the game's call the
                // scatter sees the ground before the location levels it (unless its SnapToGround rebuilt it at once).
                // Placed in later frames it would see that ground levelled, or some of it (Far.FarRegenBudget), and
                // differ for good. Zones without a location stay split by kind.
                while (j.Next < j.Veg.Count)
                    PlaceKind(j);
                if (j.Location != null)
                    Learn(s_locationMs, j.Location, t0);
                LabLocations++;
            }
            else
                Learn(s_vegMs, PlaceKind(j), t0);
            LabParts++;
            return 0;
        }

        // The next vegetation kind (one PlaceVegetation call over that kind alone); the kind placed.
        static ZoneSystem.ZoneVegetation PlaceKind(Job j)
        {
            ZoneSystem zs = s_zs;
            ZoneSystem.ZoneVegetation veg = j.Veg[j.Next++];
            List<ZoneSystem.ZoneVegetation> all = zs.m_vegetation;
            s_one.Add(veg);
            zs.m_vegetation = s_one;
            try
            {
                s_placeVegetation(zs, j.Zone, j.Pos, j.Root.transform, j.Hmap, j.ClearAreas, s_spawned);
            }
            catch (Exception e)
            {
                Error(veg.m_name, e);
            }
            finally
            {
                zs.m_vegetation = all;
                s_one.Clear();
            }
            s_spawned.Clear();
            LabKinds++;
            return veg;
        }

        // The last part, as SpawnZone ends: the zone control object, the zone marked generated, and (handOver) its root
        // kept by PokeLocalZone as a loaded zone. The root is returned (for a caller that keeps it itself).
        static GameObject Finish(Job j, bool handOver)
        {
            ZoneSystem zs = s_zs;
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                s_placeZoneCtrl(zs, j.Zone, j.Pos, ZoneSystem.SpawnMode.Full, s_spawned);
            }
            catch (Exception e)
            {
                Error("its zone control", e);
            }
            s_spawned.Clear();
            s_setGenerated(zs, j.Zone);
            float ms = (float)((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
            s_finishMs = Ema(s_finishMs, ms);
            LabStood++;
            LabOpenSecondsMax = Math.Max(LabOpenSecondsMax, Time.realtimeSinceStartup - j.BegunAt);
            GameObject root = j.Root;
            if (!handOver)
            {
                Remove(j);
                return root;
            }
            bool kept = false;
            j.Handing = true;
            try
            {
                kept = s_pokeZone(zs, j.Zone);
            }
            catch (Exception e)
            {
                kept = true;
                Error("its hand-over", e);
            }
            finally
            {
                j.Handing = false;
                Remove(j);
            }
            // The game had the zone loaded already (a root made behind this one's back): this one goes.
            if (!kept && root != null)
                UnityEngine.Object.Destroy(root);
            return root;
        }

        // Every part left at once, then the last one.
        static GameObject Complete(Job j, bool handOver)
        {
            while (j.Next < j.Veg.Count)
                PlacePart(j, true);
            return Finish(j, handOver);
        }

        static void FinishAll(string why)
        {
            Plugin.Debug($"Far.ZoneSpawnSplit: {s_jobs.Count} far zone spawn(s) finished at once ({why})");
            while (s_jobs.Count > 0)
            {
                LabForced++;
                Complete(s_jobs[s_jobs.Count - 1], true);
            }
        }

        // ZNet.SaveWorld, before ZDOMan.PrepareSave and ZoneSystem.PrepareSave copy anything: no save holds a zone
        // part placed.
        static void OnWorldSave()
        {
            try
            {
                if (s_jobs.Count > 0 && ReferenceEquals(ZoneSystem.instance, s_zs) && s_zs != null)
                    FinishAll("a world save");
            }
            catch (Exception e)
            {
                // Never into the game's save.
                Error("what was left before a world save", e);
            }
        }

        // The game asks for a zone under way (SpawnZone: PokeLocalZone as the player comes into it, CreateGhostZones
        // around a player): all of it is placed now, and SpawnZone answers as the game's own spawn of a zone it
        // generates would. Its own hand-over (PokeLocalZone from Finish) gets the root.
        [HarmonyPrefix, HarmonyPatch(typeof(ZoneSystem), "SpawnZone"), HarmonyPriority(Priority.First)]
        static bool SpawnZoneBefore(ZoneSystem __instance, Vector2s zoneID, ZoneSystem.SpawnMode mode, ref GameObject root, ref bool __result)
        {
            if (s_jobs.Count == 0 || s_self || !ReferenceEquals(__instance, s_zs))
                return true;
            Job j = Find(zoneID);
            if (j == null)
                return true;
            if (j.Handing)
            {
                root = j.Root;
                __result = true;
                return false;
            }
            LabAsked++;
            if (mode == ZoneSystem.SpawnMode.Ghost)
            {
                // Generated, as a ghost spawn leaves it; the root stays a loaded zone (it unloads as any one nobody
                // keeps).
                Complete(j, true);
                root = null;
                __result = true;
                return false;
            }
            // PokeLocalZone: it keeps the root itself.
            root = Complete(j, false);
            __result = true;
            return false;
        }

        // ---- helpers --------------------------------------------------------------------------------------------

        // 2: split; 1: the game generates it here (SpawnMode.Full) but it spawns whole; 0: it only gets its root
        // (generated already, or not the server: SpawnMode.Client), or splitting is not available.
        static int Plan(ZoneSystem zs, Vector2s zone)
        {
            ZNet net = ZNet.instance;
            if (!FarFlags.ZoneSpawnSplit || !Init() || net == null || !net.IsServer() || s_isGenerated(zs, zone))
                return 0;
            if (s_jobs.Count > 0 || net.GetPeers().Count > 0 || zs.m_zonePrefab == null)
                return 1;
            Vector2s me = ZoneSystem.GetZone(net.GetReferencePosition());
            int reach = net.GetSyncedSimulationDistance().TotalSimulationDistance + 1;
            if (Math.Abs(zone.x - me.x) <= reach && Math.Abs(zone.y - me.y) <= reach)
                return 1;
            return Foreign() ? 1 : 2;
        }

        static Job Find(Vector2s zone)
        {
            for (int i = 0; i < s_jobs.Count; i++)
                if (s_jobs[i].Zone == zone)
                    return s_jobs[i];
            return null;
        }

        // The root again, should it be gone (it is no loaded zone of the game's, so nothing of the game unloads it).
        static bool Reroot(Job j)
        {
            s_self = true;
            try
            {
                if (!s_spawnZone(s_zs, j.Zone, ZoneSystem.SpawnMode.Client, out GameObject root) || root == null)
                    return false;
                j.Root = root;
                j.Hmap = root.GetComponentInChildren<Heightmap>();
                return j.Hmap != null;
            }
            finally
            {
                s_self = false;
            }
        }

        static void Remove(Job j)
        {
            s_jobs.Remove(j);
            j.Root = null;
            j.Hmap = null;
            j.Location = null;
            j.Veg.Clear();
            j.ClearList.Clear();
            s_pool.Add(j);
        }

        static void Drop()
        {
            while (s_jobs.Count > 0)
                Remove(s_jobs[s_jobs.Count - 1]);
            s_zs = null;
        }

        static void Learn<T>(Dictionary<T, float> costs, T key, long t0)
        {
            float ms = (float)((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
            costs.TryGetValue(key, out float ema);
            costs[key] = Ema(ema, ms);
            LabPartMs += ms;
            LabPartMsMax = Math.Max(LabPartMsMax, ms);
        }

        static float Ema(float ema, float ms) => ema <= 0f ? ms : ema + (ms - ema) * 0.3f;

        static void Error(string what, Exception e)
        {
            if (LabErrors++ < 5)
                Plugin.Log.LogWarning($"Far.ZoneSpawnSplit: placing {what} threw ({e.GetType().Name}: {e.Message}); taken as placed");
        }

        // Another mod patching what is called part by part (not ours, the engine's or the lab's): zones spawn whole.
        // Logged when that changes.
        static bool Foreign()
        {
            float now = Time.realtimeSinceStartup;
            if (s_foreignAt >= 0f && now - s_foreignAt < ForeignCheckEvery)
                return s_foreign;
            s_foreignAt = now;
            string who = null;
            try
            {
                foreach (MethodBase m in s_watched)
                {
                    HarmonyLib.Patches info = Harmony.GetPatchInfo(m);
                    if (info == null)
                        continue;
                    who = Owner(info.Prefixes) ?? Owner(info.Postfixes) ?? Owner(info.Transpilers) ?? Owner(info.Finalizers) ?? Owner(info.ILManipulators);
                    if (who != null)
                    {
                        who += " patches ZoneSystem." + m.Name;
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                who = "reading Harmony patch info failed (" + e.Message + ")";
            }
            bool foreign = who != null;
            if (foreign && !s_foreign)
                Plugin.Log.LogWarning("Far.ZoneSpawnSplit: " + who + "; far zones spawn whole, as without the switch");
            else if (!foreign && s_foreign)
                Plugin.Log.LogInfo("Far.ZoneSpawnSplit: zone spawning is no longer patched by another mod");
            s_foreign = foreign;
            return foreign;
        }

        static string Owner(IEnumerable<HarmonyLib.Patch> patches)
        {
            if (patches == null)
                return null;
            foreach (HarmonyLib.Patch p in patches)
                if (p.owner != Plugin.Guid && p.owner != Plugin.EngineGuid && (p.owner == null || !p.owner.StartsWith(LabOwners, StringComparison.Ordinal)))
                    return p.owner ?? "?";
            return null;
        }

        // The game's private zone spawning, looked up once; anything not as expected leaves zones to spawn whole.
        static bool Init()
        {
            if (s_init != 0)
                return s_init > 0;
            s_init = -1;
            try
            {
                Type zs = typeof(ZoneSystem);
                Type mode = typeof(ZoneSystem.SpawnMode);
                Type clearArea = AccessTools.Inner(zs, "ClearArea") ?? throw new MissingMemberException("ZoneSystem.ClearArea");
                s_clearList = typeof(List<>).MakeGenericType(clearArea);
                MethodInfo spawn = Need(zs, "SpawnZone", typeof(Vector2s), mode, typeof(GameObject).MakeByRefType());
                MethodInfo locations = Need(zs, "PlaceLocations", typeof(Vector2s), typeof(Vector3), typeof(Transform), typeof(Heightmap), s_clearList, mode, typeof(List<GameObject>));
                MethodInfo vegetation = Need(zs, "PlaceVegetation", typeof(Vector2s), typeof(Vector3), typeof(Transform), typeof(Heightmap), s_clearList, mode, typeof(List<GameObject>));
                MethodInfo zoneCtrl = Need(zs, "PlaceZoneCtrl", typeof(Vector2s), typeof(Vector3), mode, typeof(List<GameObject>));
                s_spawnZone = AccessTools.MethodDelegate<SpawnZoneCall>(spawn);
                s_pokeZone = AccessTools.MethodDelegate<Func<ZoneSystem, Vector2s, bool>>(Need(zs, "PokeLocalZone", typeof(Vector2s)));
                s_isGenerated = AccessTools.MethodDelegate<Func<ZoneSystem, Vector2s, bool>>(Need(zs, "IsZoneGenerated", typeof(Vector2s)));
                s_setGenerated = AccessTools.MethodDelegate<Action<ZoneSystem, Vector2s>>(Need(zs, "SetZoneGenerated", typeof(Vector2s)));
                s_canSpawnLocation = AccessTools.MethodDelegate<Func<ZoneSystem, ZoneSystem.ZoneLocation, bool, bool>>(
                    Need(zs, "PokeCanSpawnLocation", typeof(ZoneSystem.ZoneLocation), typeof(bool)));
                s_placeZoneCtrl = AccessTools.MethodDelegate<Action<ZoneSystem, Vector2s, Vector3, ZoneSystem.SpawnMode, List<GameObject>>>(zoneCtrl);
                s_placeLocations = PlaceCall(locations);
                s_placeVegetation = PlaceCall(vegetation);
                s_watched = new MethodBase[] { spawn, locations, vegetation, zoneCtrl };
                s_init = 1;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Far.ZoneSpawnSplit: the game's zone spawning is not as expected (" + e.Message + "); far zones spawn whole");
            }
            return s_init > 0;
        }

        static MethodInfo Need(Type t, string name, params Type[] args) =>
            AccessTools.DeclaredMethod(t, name, args) ?? throw new MissingMethodException(t.Name, name);

        // PlaceLocations / PlaceVegetation (SpawnMode.Full) with the clear areas passed as an object (their list's type
        // is private to the game).
        static Action<ZoneSystem, Vector2s, Vector3, Transform, Heightmap, object, List<GameObject>> PlaceCall(MethodInfo m)
        {
            var dm = new DynamicMethod("RealPortal_" + m.Name, typeof(void),
                new[] { typeof(ZoneSystem), typeof(Vector2s), typeof(Vector3), typeof(Transform), typeof(Heightmap), typeof(object), typeof(List<GameObject>) },
                typeof(FarZoneSpawn).Module, true);
            ILGenerator il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Ldarg_3);
            il.Emit(OpCodes.Ldarg_S, (byte)4);
            il.Emit(OpCodes.Ldarg_S, (byte)5);
            il.Emit(OpCodes.Castclass, s_clearList);
            il.Emit(OpCodes.Ldc_I4, (int)ZoneSystem.SpawnMode.Full);
            il.Emit(OpCodes.Ldarg_S, (byte)6);
            il.Emit(OpCodes.Call, m);
            il.Emit(OpCodes.Ret);
            return (Action<ZoneSystem, Vector2s, Vector3, Transform, Heightmap, object, List<GameObject>>)dm.CreateDelegate(
                typeof(Action<ZoneSystem, Vector2s, Vector3, Transform, Heightmap, object, List<GameObject>>));
        }
    }
}
