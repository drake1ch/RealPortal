using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace RealPortal.Engine
{
    // Plant.SUpdate does its work only while time <= m_updateTime and then sets m_updateTime = time + 10
    // (IL: ldarg.1; ldfld m_updateTime; ble.un.s <body>; ret); Start sets it to now + 10. So a crop is refreshed on
    // every SlowUpdater visit while the visits come less than 10 s apart, and the first longer gap stops it for good:
    // no health or status refresh, no growth, until its zone is loaded again. In the game alone a SlowUpdater cycle
    // (100 objects per frame, then 0.1 s) stays far below 10 s; with far areas loaded (~100k StaticPhysics, ~17 s per
    // cycle at 60 fps) every crop freezes, the player's own too.
    //
    // The intent: Start arming the time 10 s ahead and every run pushing it 10 s ahead reads like an "at most every
    // 10 s" throttle whose comparison got inverted; nobody means a crop to stop forever. A real throttle would change
    // what everyone sees today (a crop refreshes on each visit, about once a second, and grows within a visit of its
    // time), so this keeps today's cadence and removes only the latch: a visit that comes after the window closed
    // counts as an ordinary one. While visits come within 10 s nothing changes at all. m_updateTime is read nowhere
    // else (Start and SUpdate only), and the original body still runs, so other mods' patches on SUpdate,
    // UpdateHealth, GetGrowTime and Grow (Path of Valheiman has several) see the same calls as in the game.
    //
    // Only a real latch is released, so every other visit the game skips stays skipped: a plant whose Start has not
    // run yet (SlowUpdate.Awake lists it at once, Start comes later or never; m_updateTime is still 0 then, and Start
    // always writes Time.time + 10 > 0) and one whose ZNetView is not valid (a placement ghost, or one being torn
    // down; the original checks IsValid first and does nothing). This also keeps Released counting latches only.
    //
    // Real Portal's Far.SlowVisibleNear (Docs\PARITY-PLAN.md P36; SlowUpdaterBudgetModule.KeepNearLatch): the player's
    // own surroundings keep the game's latch. A plant visited by SlowUpdaterBudget's near pass whose last visit was that
    // pass too (or that has had none yet) got there at the game's own pace, so a latch it ran into is the one the game
    // has without far areas (a huge base on a slow machine): it is left to the game (Kept). Latches that came about in
    // the far passes (a far crop the player walked up to) are released as before.
    [EngineModule]
    internal static class PlantLatchModule
    {
        static readonly Feature s_feature = Features.Register("PlantLatch", true,
            "Crops keep updating when SlowUpdater's cycle passes 10 s (far areas loaded); the game stops them for good then.");

        static readonly AccessTools.FieldRef<Plant, float> s_updateTime = AccessTools.FieldRefAccess<Plant, float>("m_updateTime");
        static readonly AccessTools.FieldRef<Plant, ZNetView> s_nview = AccessTools.FieldRefAccess<Plant, ZNetView>("m_nview");

        // Lab: visits that came after the 10 s window, i.e. crops the game would have stopped for good.
        internal static long Released;
        // Lab (P36): latches left to the game in the player's own near pass (KeepNearLatch).
        internal static long Kept;

        static void Install(Harmony harmony)
        {
            if (s_feature == null)
                return;
            harmony.Patch(AccessTools.Method(typeof(Plant), nameof(Plant.SUpdate)),
                prefix: new HarmonyMethod(typeof(PlantLatchModule), nameof(BeforeSUpdate)));
        }

        static void BeforeSUpdate(Plant __instance, float time)
        {
            if (!s_feature.On)
                return;
            ref float until = ref s_updateTime(__instance);
            // The cheap tests first: IsValid is asked only for a plant that is past its window. A missing ZNetView
            // throws here as it throws in the original on this same visit.
            if (time > until && until > 0f && s_nview(__instance).IsValid())
            {
                // P36: the game's own latch in the player's near pass stays.
                if (SlowUpdaterBudgetModule.GameLatch)
                {
                    Kept++;
                    return;
                }
                until = time;
                Released++;
            }
        }
    }

    // SlowUpdater visits a fixed 100 SlowUpdate objects per frame, round-robin over every one loaded, and waits 0.1 s
    // after each pass. Far areas put ~100k StaticPhysics (vegetation, stones) into that list, so the player's own
    // crops and stones are visited about 8x less often than in the game alone (a ~17 s cycle instead of ~2 s): crops
    // grow and show their status late, stones and pickables fall or pop up out of the ground late.
    //
    // With this on, the loop runs two passes side by side, each one the game's own (100 visits per frame, a frame and
    // 0.1 s between passes), and nothing else changes for any object: it still gets SUpdate(time, zone) with the
    // frame's Time.time and the zone of the player's reference position.
    //  - near: the objects in the zones the game itself loads around the player (the synced simulation distance),
    //    taken anew at the start of each pass. Their cycle is what it is without far areas.
    //  - all: the game's list in the game's order, skipping what the current near pass covers. Everything else (the
    //    far areas) keeps exactly the cadence it has today, so their cost does not grow.
    // At most 200 objects per frame instead of 100. Objects are bucketed by zone when they wake (Awake), followed when
    // a pass sees that one moved, and dropped in OnDestroy (an emptied bucket with them); the buckets are kept only
    // while this is on and rebuilt from the game's list when it is switched on. Order between objects in one frame
    // differs from the game's, which is itself arbitrary (it shifts with every swap-remove and with frame timing); no
    // SlowUpdate reads another's state through the order, and the only random draws (Plant.Grow) come in whatever
    // order crops ripen in the game too.
    // The game's coroutine is kept: while this is on, its MoveNext is skipped and does our step instead, and when it
    // is switched off the game's loop resumes where it stood. A mod that transpiles that MoveNext is bypassed while
    // this is on.
    //
    // Docs\PARITY-PLAN.md P36 (Real Portal's Far.SlowVisibleNear, through ExtraNearZones): Real Portal runs an awake far
    // core as a player standing there would have it, and there the game's SlowUpdater would visit that place's objects
    // at its own pace with that place's zone as the reference zone (StaticPhysics settles and falls only inside the
    // active area round it). While the hook is set a third pass runs beside the two, the game's own again: the cores'
    // pass, over the buckets within each core's near reach (NearCore.Radius zones each way of its zone), each object with
    // the zone of the core nearest to it (where cores overlap) as SUpdate's reference zone, and nothing the near pass
    // covers; the 'all' pass skips what it covers too. The near pass is not touched, so the player's own surroundings keep
    // the game's cadence whatever far cores are awake (one shared near pass would stretch it with every core). At most
    // 300 objects per frame then. Unset, the cores' pass is empty and skips nothing: as before.
    [EngineModule]
    public static class SlowUpdaterBudgetModule
    {
        // ---- Real Portal's hooks (P36; main thread, may be set or cleared at any time) ----

        // A far core: its zone, and how far its near objects reach from it (zones, Chebyshev).
        public struct NearCore
        {
            public Vector2s Zone;
            public int Radius;
        }

        // Asked at the start of every cores' pass with an empty list to fill with the awake far cores. Must not touch
        // SlowUpdate objects. Null: no cores' pass. A hook that throws counts as no cores for that pass (logged once).
        public static Action<List<NearCore>> ExtraNearZones;

        // The near pass leaves PlantLatch's release to the game's own latch for a plant whose last visit was that pass too
        // (see PlantLatchModule). Real Portal sets it together with ExtraNearZones.
        public static bool KeepNearLatch;

        // Registered at the end of Install, once every patch is in (see there): null until then, and for good when
        // the install failed, so the patches read it through On.
        static Feature s_feature;

        static bool On
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => s_feature != null && s_feature.On;
        }

        // SlowUpdater.m_updatesPerFrame (a const, inlined in the game) and the wait after each pass.
        const int PerFrame = 100;
        const float Wait = 0.1f;

        sealed class Entry
        {
            public SlowUpdate Obj;
            public Vector2s Zone;
            public int Slot;        // index in its zone bucket
            public int NearPass;    // the near pass that covers it
            public int CorePass;    // the cores' pass that covers it (P36)
            public Vector2s CoreZone;   // ... and its reference zone there
            public byte Last;       // its last visit: 0 none yet, 1 the near pass, 2 another pass (P36's latch)
            public bool Alive;
        }

        sealed class ByReference : IEqualityComparer<SlowUpdate>
        {
            public bool Equals(SlowUpdate a, SlowUpdate b) => ReferenceEquals(a, b);
            public int GetHashCode(SlowUpdate o) => RuntimeHelpers.GetHashCode(o);
        }

        // Replaced by fresh ones when the last object leaves (logout), so their peak size (~100k entries with far
        // areas) is not kept for the rest of the process.
        static Dictionary<SlowUpdate, Entry> s_entries = new Dictionary<SlowUpdate, Entry>(new ByReference());
        static Dictionary<Vector2s, List<Entry>> s_buckets = new Dictionary<Vector2s, List<Entry>>();

        // The near pass: a snapshot of the buckets around the player, walked like the game walks its list.
        static readonly List<Entry> s_near = new List<Entry>();
        static int s_nearAt, s_nearPass;
        static bool s_nearWaiting = true;
        static float s_nearSince = float.NegativeInfinity;

        // The cores' pass (P36): a snapshot of the buckets round the far cores ExtraNearZones names, walked the same way.
        static readonly List<Entry> s_core = new List<Entry>();
        static readonly List<NearCore> s_cores = new List<NearCore>();
        static int s_coreAt, s_corePass;
        static bool s_coreWaiting = true;
        static float s_coreSince = float.NegativeInfinity;
        static bool s_hookWarned;

        // Set round a near-pass visit that keeps the game's plant latch (KeepNearLatch; PlantLatchModule reads it).
        internal static bool GameLatch;

        // The pass over the game's list.
        static int s_allAt;
        static bool s_allWaiting;
        static float s_allSince;

        static int s_frame = -1;
        static FieldInfo s_current;

        // Lab: seconds between the starts of the last two passes of each kind (the time an object waits between two
        // visits), and how many objects the last near pass took; the same for the cores' pass (P36, while it has any).
        internal static float NearCycle, AllCycle, CoreCycle;
        internal static int NearCount, CoreCount;
        static float s_nearStarted = -1f, s_allStarted = -1f, s_coreStarted = -1f;

        static void Install(Harmony harmony)
        {
            MethodInfo moveNext = AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(SlowUpdater), "UpdateLoop"));
            s_current = moveNext == null ? null : AccessTools.Field(moveNext.DeclaringType, "<>2__current");
            if (moveNext == null || s_current == null)
            {
                EnginePlugin.Log.LogWarning("SlowUpdaterBudget: SlowUpdater.UpdateLoop not found as expected, left as it is.");
                return;
            }
            // All or nothing, the loop first: it is the patch that can fail (an iterator's MoveNext), and the
            // bookkeeping alone would only cost (an entry per object, ~100k with far areas, that nothing reads). What
            // went in is taken out again when a later patch fails, and the feature is registered only after the last
            // one, so a failed install is not listed as on (switching it off would not hold: BindConfig, after every
            // Install, sets it from the config again, and an unbound one would write its 'off' there as the default).
            var done = new List<KeyValuePair<MethodBase, MethodInfo>>();
            try
            {
                Patch(harmony, done, moveNext, nameof(LoopStep), true);
                Patch(harmony, done, AccessTools.Method(typeof(SlowUpdate), nameof(SlowUpdate.OnDestroy)), nameof(BeforeDestroy), true);
                Patch(harmony, done, AccessTools.Method(typeof(SlowUpdate), nameof(SlowUpdate.Awake)), nameof(AfterAwake), false);
            }
            catch
            {
                foreach (KeyValuePair<MethodBase, MethodInfo> p in done)
                {
                    try
                    {
                        harmony.Unpatch(p.Key, p.Value);
                    }
                    catch (Exception e)
                    {
                        EnginePlugin.Log.LogError($"SlowUpdaterBudget: {p.Key.DeclaringType?.Name}.{p.Key.Name} could not be unpatched (left inert): {e}");
                    }
                }
                throw;
            }
            s_feature = Features.Register("SlowUpdaterBudget", true,
                "SlowUpdater visits the objects around the player at the game's own pace however many far-area objects are loaded.");
            s_feature.Switched += Switched;
        }

        static void Patch(Harmony harmony, List<KeyValuePair<MethodBase, MethodInfo>> done, MethodBase original, string patch, bool prefix)
        {
            if (original == null)
                throw new MissingMethodException($"SlowUpdaterBudget: a SlowUpdate method for {patch} not found");
            MethodInfo method = AccessTools.DeclaredMethod(typeof(SlowUpdaterBudgetModule), patch);
            var hm = new HarmonyMethod(method);
            if (prefix)
                harmony.Patch(original, prefix: hm);
            else
                harmony.Patch(original, postfix: hm);
            done.Add(new KeyValuePair<MethodBase, MethodInfo>(original, method));
        }

        static void Switched(bool on)
        {
            foreach (Entry e in s_entries.Values)
            {
                e.Alive = false;
                e.Obj = null;
            }
            s_entries.Clear();
            s_buckets.Clear();
            s_near.Clear();
            s_nearAt = 0;
            s_nearPass++;
            s_nearWaiting = true;
            s_nearSince = float.NegativeInfinity;
            s_allAt = 0;
            s_allWaiting = false;
            s_core.Clear();
            s_coreAt = 0;
            s_corePass++;
            s_coreWaiting = true;
            s_coreSince = float.NegativeInfinity;
            GameLatch = false;
            s_nearStarted = s_allStarted = s_coreStarted = -1f;
            NearCycle = AllCycle = CoreCycle = 0f;
            NearCount = CoreCount = 0;
            if (!on)
                return;
            // What woke while this was off: the game's own list holds every live one.
            foreach (SlowUpdate su in SlowUpdate.GetAllInstaces())
                if (su != null)
                    Register(su);
        }

        // ---- the zone buckets ----------------------------------------------------------------------------

        static void AfterAwake(SlowUpdate __instance)
        {
            if (On)
                Register(__instance);
        }

        static void BeforeDestroy(SlowUpdate __instance)
        {
            if (s_entries.Count == 0 || !s_entries.TryGetValue(__instance, out Entry e))
                return;
            s_entries.Remove(__instance);
            Lift(e);
            e.Alive = false;
            e.Obj = null;
            if (s_entries.Count == 0)
                Shrink();
        }

        // The last object left (logout, or a world with nothing of the kind around): every bucket is gone already
        // (Lift drops empty ones), and every entry the near pass still holds is dead, which it would skip anyway.
        static void Shrink()
        {
            s_entries = new Dictionary<SlowUpdate, Entry>(s_entries.Comparer);
            s_buckets = new Dictionary<Vector2s, List<Entry>>();
            s_near.Clear();
            s_near.TrimExcess();
            s_core.Clear();
            s_core.TrimExcess();
        }

        static void Register(SlowUpdate su)
        {
            if (s_entries.ContainsKey(su))
                return;
            var e = new Entry { Obj = su, Alive = true, NearPass = -1, CorePass = -1 };
            s_entries.Add(su, e);
            Place(e, ZoneSystem.GetZone(su.transform.position));
        }

        static void Place(Entry e, Vector2s zone)
        {
            e.Zone = zone;
            if (!s_buckets.TryGetValue(zone, out List<Entry> bucket))
                s_buckets.Add(zone, bucket = new List<Entry>());
            e.Slot = bucket.Count;
            bucket.Add(e);
        }

        static void Lift(Entry e)
        {
            if (!s_buckets.TryGetValue(e.Zone, out List<Entry> bucket))
                return;
            int last = bucket.Count - 1;
            Entry moved = bucket[last];
            bucket[e.Slot] = moved;
            moved.Slot = e.Slot;
            bucket.RemoveAt(last);
            // An emptied zone (unloaded, or its last object moved on) gives its bucket up, or every zone ever visited
            // would keep one at its peak size. Nothing holds on to a bucket: Collect copies what it walks into s_near,
            // and Place makes a new one when something wakes there again.
            if (last == 0)
                s_buckets.Remove(e.Zone);
        }

        // Objects hardly ever move sideways (StaticPhysics only falls); one that does changes bucket when a pass sees it.
        static void Follow(Entry e)
        {
            Vector2s zone = ZoneSystem.GetZone(e.Obj.transform.position);
            if (zone == e.Zone)
                return;
            Lift(e);
            Place(e, zone);
        }

        // ---- the loop ------------------------------------------------------------------------------------

        // Prefix on SlowUpdater.UpdateLoop's MoveNext. The coroutine stays alive and waits a frame (Current = null);
        // the game's own state is left untouched for when this is switched off.
        static bool LoopStep(object __instance, ref bool __result)
        {
            if (!On)
                return true;
            __result = true;
            s_current.SetValue(__instance, null);
            // Once per frame, however many SlowUpdaters there are (the game has one).
            if (Time.frameCount != s_frame)
            {
                s_frame = Time.frameCount;
                Step();
            }
            return false;
        }

        static void Step()
        {
            // The game reads ZNet.instance unguarded here; SlowUpdater lives in the game scene, next to ZNet.
            ZNet znet = ZNet.instance;
            if (znet == null)
                return;
            float time = Time.time;
            Vector2s zone = ZoneSystem.GetZone(znet.GetReferencePosition());
            StepNear(znet, time, zone);
            StepCores(time);
            StepAll(time, zone);
        }

        static void StepNear(ZNet znet, float time, Vector2s zone)
        {
            if (s_nearWaiting)
            {
                if (time - s_nearSince < Wait)
                    return;
                s_nearWaiting = false;
                Collect(zone, znet.GetSyncedSimulationDistance().TotalSimulationDistance);
                if (s_nearStarted >= 0f)
                    NearCycle = time - s_nearStarted;
                s_nearStarted = time;
                NearCount = s_near.Count;
            }
            if (s_nearAt >= s_near.Count)
            {
                // The game: after its last batch it yields a frame, then waits 0.1 s.
                s_nearWaiting = true;
                s_nearSince = time;
                return;
            }
            int visits = 0;
            while (visits < PerFrame && s_nearAt < s_near.Count)
            {
                Entry e = s_near[s_nearAt++];
                if (!e.Alive)
                    continue;       // destroyed since the pass began: the game's live list would not hold it either
                Follow(e);
                visits++;
                // P36: a plant that kept to this pass meets the game's own latch (PlantLatchModule).
                bool keep = KeepNearLatch && e.Last != 2;
                e.Last = 1;
                if (!keep)
                {
                    e.Obj.SUpdate(time, zone);
                    continue;
                }
                GameLatch = true;
                try
                {
                    e.Obj.SUpdate(time, zone);
                }
                finally
                {
                    GameLatch = false;
                }
            }
        }

        // P36: the game's pass over the far cores' objects, as StepNear walks the player's, each with its core's zone.
        static void StepCores(float time)
        {
            if (s_coreWaiting)
            {
                if (time - s_coreSince < Wait)
                    return;
                s_coreWaiting = false;
                CollectCores();
                if (s_core.Count > 0)
                {
                    if (s_coreStarted >= 0f)
                        CoreCycle = time - s_coreStarted;
                    s_coreStarted = time;
                }
                else
                    s_coreStarted = -1f;
                CoreCount = s_core.Count;
            }
            if (s_coreAt >= s_core.Count)
            {
                s_coreWaiting = true;
                s_coreSince = time;
                return;
            }
            int visits = 0;
            while (visits < PerFrame && s_coreAt < s_core.Count)
            {
                Entry e = s_core[s_coreAt++];
                // Destroyed since the pass began, or the player's near pass took it since (he walked up to it).
                if (!e.Alive || e.NearPass == s_nearPass)
                    continue;
                Follow(e);
                visits++;
                e.Last = 2;
                e.Obj.SUpdate(time, e.CoreZone);
            }
        }

        // The buckets within each core's reach, less what the current near pass covers; a zone two cores reach goes with
        // the nearer (the first of equals). Without the hook the pass is empty, and a new pass number means nothing counts
        // as covered by it.
        static void CollectCores()
        {
            s_core.Clear();
            s_coreAt = 0;
            s_corePass++;
            Action<List<NearCore>> hook = ExtraNearZones;
            if (hook == null)
                return;
            s_cores.Clear();
            try
            {
                hook(s_cores);
            }
            catch (Exception ex)
            {
                s_cores.Clear();
                if (!s_hookWarned)
                {
                    s_hookWarned = true;
                    EnginePlugin.Log.LogWarning("SlowUpdaterBudget: ExtraNearZones threw, no cores' pass this time (logged once): " + ex);
                }
            }
            for (int i = 0; i < s_cores.Count; i++)
            {
                NearCore c = s_cores[i];
                int r = Math.Max(0, c.Radius);
                for (int y = c.Zone.y - r; y <= c.Zone.y + r; y++)
                    for (int x = c.Zone.x - r; x <= c.Zone.x + r; x++)
                    {
                        var z = new Vector2s(x, y);
                        if (!s_buckets.TryGetValue(z, out List<Entry> bucket) || NearerCore(i, z))
                            continue;
                        foreach (Entry e in bucket)
                        {
                            if (e.NearPass == s_nearPass)
                                continue;
                            e.CorePass = s_corePass;
                            e.CoreZone = c.Zone;
                            s_core.Add(e);
                        }
                    }
            }
        }

        // Another core reaches the zone and is nearer to it than the i-th (Chebyshev), or as near and listed first.
        static bool NearerCore(int i, Vector2s z)
        {
            int d = Math.Max(Math.Abs(z.x - s_cores[i].Zone.x), Math.Abs(z.y - s_cores[i].Zone.y));
            for (int k = 0; k < s_cores.Count; k++)
            {
                if (k == i)
                    continue;
                NearCore o = s_cores[k];
                int dk = Math.Max(Math.Abs(z.x - o.Zone.x), Math.Abs(z.y - o.Zone.y));
                if (dk <= Math.Max(0, o.Radius) && (dk < d || (dk == d && k < i)))
                    return true;
            }
            return false;
        }

        // The zones the game loads around the player: everything in them this pass, the 'all' pass skips them.
        static void Collect(Vector2s zone, int radius)
        {
            s_near.Clear();
            s_nearAt = 0;
            s_nearPass++;
            for (int y = zone.y - radius; y <= zone.y + radius; y++)
                for (int x = zone.x - radius; x <= zone.x + radius; x++)
                {
                    if (!s_buckets.TryGetValue(new Vector2s(x, y), out List<Entry> bucket))
                        continue;
                    foreach (Entry e in bucket)
                    {
                        e.NearPass = s_nearPass;
                        s_near.Add(e);
                    }
                }
        }

        // SlowUpdater.UpdateLoop step for step: 100 list slots per frame in the game's order; at the end of the list
        // one frame, then 0.1 s, then from the start. Objects of the current near pass (and cores' pass) take their
        // slot but are left to that pass.
        static void StepAll(float time, Vector2s zone)
        {
            List<SlowUpdate> all = SlowUpdate.GetAllInstaces();
            if (s_allWaiting)
            {
                if (time - s_allSince < Wait)
                    return;
                s_allWaiting = false;
                s_allAt = 0;
                if (s_allStarted >= 0f)
                    AllCycle = time - s_allStarted;
                s_allStarted = time;
            }
            if (s_allAt >= all.Count)
            {
                s_allWaiting = true;
                s_allSince = time;
                return;
            }
            for (int i = 0; i < PerFrame && s_allAt < all.Count; i++)
            {
                SlowUpdate su = all[s_allAt++];
                if (s_entries.TryGetValue(su, out Entry e))
                {
                    if (e.NearPass == s_nearPass || e.CorePass == s_corePass)
                        continue;
                    Follow(e);
                    e.Last = 2;
                }
                su.SUpdate(time, zone);
            }
        }
    }
}
