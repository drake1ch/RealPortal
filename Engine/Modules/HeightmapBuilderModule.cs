using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace RealPortal.Engine
{
    // The game builds the base heights of every zone and distant-terrain heightmap off the main thread
    // (HeightmapBuilder), but in a way that falls apart once hundreds of zones load at once:
    //  - one FIFO thread that sleeps 10 ms after every item;
    //  - only 16 finished results are kept, the oldest evicted, so a result IsTerrainReady has just reported can be
    //    gone when its caller asks for it a moment later, and the request then goes to the back of the queue;
    //  - RequestTerrainSync spins on the main thread until its item is built, i.e. behind the whole backlog.
    // With far areas loading that backlog is hundreds of builds long: the 0.2-15.7 s Heightmap.Regenerate freezes.
    //
    // This module routes the three entry points (IsTerrainReady, RequestTerrain, RequestTerrainSync) to its own
    // scheduler: worker threads blocked on a semaphore, a queue with lanes (synchronous requests, the player's own
    // zones, then work someone is polling right now, then the rest in the order it was asked for), a table of finished
    // results that keeps every result until it is taken (or nobody took it for ReadyTtlSeconds), and a synchronous
    // path that never waits behind the queue: a finished result is taken, an item a worker is building is waited for
    // (that item only), anything else is built right there on the calling thread.
    // Workers build at most SoftCap results nobody was told about yet ahead of the callers; past that they only take
    // what is being polled right now (backpressure). They do not build further and throw results away: the far-area
    // loaders poll in the order they queued, so the oldest unreported result is exactly the one they take next, and
    // evicting it had the workers rebuilding the same zones over and over during a big load.
    //
    // Equivalence: every result is still produced by the game's own private HeightmapBuilder.Build on the same
    // HMBuildData inputs, called through its (patched) entry point, so the data is bit-identical and other mods'
    // patches on Build still run. Results are matched the way HMBuildData.IsEqual matches them (approximate Vector3 ==,
    // exact width/scale/lod, the same WorldGenerator), and every result goes to exactly one taker, as in the game.
    // Build's call tree (WorldGenerator.GetBiomeSector/GetBiome/GetBiomeHeight and the per-biome height functions,
    // DUtils, FastNoise, Mathf.PerlinNoise) only reads state written before the generator is published; it never
    // reaches the static s_cachedBiomes/s_cachedBiomeAreas (only GetBiome(Vector2s)/GetBiomeArea(Vector2s) use them),
    // and uses no UnityEngine.Random. The one shared mutable thing is the single-entry river cache, which is correct
    // under its ReaderWriterLockSlim but thrashes with several builders; workers use a per-thread copy of it instead.
    // When another mod patches anything in that call tree, its code was written for the game's single builder
    // thread: builds then stay on one worker and synchronous requests wait for it instead of building inline.
    // Other mods' patches are looked at again every PatchCheckSeconds (and on the first request after a world
    // starts), so patches applied late, say when a server's config syncs, are picked up; finished results and builds
    // in hand are then dropped and rebuilt on demand. A mod that rewrites one of the three entry points themselves (a
    // transpiler, which our skipping prefix would leave unused) makes the module step aside as if switched off.
    //
    // Where it differs from the game, deliberately and harmlessly: which result is handed out, and in which order work
    // is built, only ever depends on timing, never on content (both are what Build makes of the same inputs); queued
    // work nobody has asked about for 30 s is dropped (asking again queues it again); a result built before the world's
    // biome table existed is rebuilt rather than handed out after it exists; a Build that throws is logged and retried
    // on the calling thread (where the game's builder thread would die and RequestTerrainSync spin forever). A patch
    // another mod adds to the generator mid-game is seen up to PatchCheckSeconds late; the game's builder lags the same
    // way, handing out what its ready list holds from before the patch.
    //
    // Switching off: new requests go to the game's builder at once. Queued work is dropped; results already reported
    // ready, and builds in hand, stay takeable through these prefixes until taken or expired, so a caller that was
    // told "ready" just before the switch still gets its result. Pins made through the lane API are let go of (a
    // pinned result stays as one reported ready just then), so nothing of ours outlasts the game's own expiry.
    // Switching on: results the game's builder reported ready before the switch are taken from its own list.
    //
    // Lanes (HeightmapLanes): a public API for other plugins (Real Portal's far world) on the same scheduler. Request
    // asks for a build on one of five lanes, IsReady and TryTake look at and take the result, Pin keeps it until taken,
    // Release lets go of it. Order of the queues, fastest first: synchronous requests; the player's zones (the Player
    // lane is that same queue); what the game polls right now (Wanted); the FarCore, FarVisual and FarTerrain lanes; the
    // game's backlog (Normal); the Prefetch lane. So nothing a game caller is waiting for waits behind lane work: only
    // the game's build-ahead does, behind work somebody asked for. Each far lane has a build-ahead window like the
    // game's (LaneSoftCap results not reported to their caller yet, counted together with those of the faster far
    // lanes, so a slow lane's unclaimed results never hold up a faster one). Lane work polled again goes first within
    // its lane and past the window, as Wanted does for the game: a caller that polls all its keys every frame waits for
    // all of them, and only the held cap bounds it. Lane work cannot crowd the game's window, nor the game's work a
    // lane's. Held cap: a far lane waits while the far lanes' callers hold its share of finished results untaken
    // (reported or not; see LaneHeldCap): LaneHardCap for FarCore, one window (LaneSoftCap) less for each slower lane.
    // So lane work alone never fills the table to HardCap (where the game's own far work would stop too), whatever the
    // slower lanes hold a faster one keeps at least a window of room, and FarCore's work polled right now (what a
    // crossing waits for) is held back by nothing but the table's own room.
    // A pinned result is never evicted or expired before the pin runs out (at most MaxPins at once, well under HardCap,
    // so eviction always finds another one); it still goes when the world changes, when it was built before the world's
    // biome table existed or other mods' patches on the generator changed since (it may be wrong: asked again, it is
    // rebuilt), and, not built yet, when the scheduler is switched off. Lane work the module drops on its own like that
    // (or evicts, or whose build threw) is remembered with its lane and pin, and the next IsReady, TryTake or Pin on it
    // queues it again, so a caller waiting with any of them is never stranded. With no lane calls, the game's requests
    // are scheduled exactly as before. HeightmapLanes switched off: lane work still queued is dropped and pins are let
    // go of (finished results stay, takeable as reported ones until they expire), so from then on the schedule is the
    // game's alone. HeightmapLanes off, or the scheduler not taking requests (HeightmapBuilder off, stepped aside, not
    // started): the API polls the game's IsTerrainReady, and takes with its RequestTerrain, as a game caller does.
    // Pin(key, 0) and Release work in every mode. A key whose centre or scale is not a finite number, or whose width is
    // not positive, is refused (the table could never find a NaN again, nor drop it).
    //
    // Nothing here touches Unity objects off the main thread, sends anything over the network or writes ZDOs.
    [EngineModule]
    public static class HeightmapBuilderModule
    {
        public static readonly Feature Feature = Features.Register("HeightmapBuilder", true,
            "Terrain heights are built by several threads, finished results are kept until taken, and a synchronous " +
            "request never waits behind the queue (no multi-second freezes while many zones load). Off: the game's builder.");

        public static readonly Feature ParallelFeature = Features.Register("HeightmapBuilderParallel", true,
            "With HeightmapBuilder on: build on several threads, and build synchronous requests on the calling thread. " +
            "Off: one worker thread, as in the game (still without its sleeps, evictions and spin).");

        public static readonly Feature LanesFeature = Features.Register("HeightmapLanes", true,
            "With HeightmapBuilder on: other plugins (Real Portal's far world) queue terrain builds on priority lanes " +
            "(player, far core, far visual, far terrain, prefetch) and pin finished results until they take them. " +
            "Off: their requests poll IsTerrainReady like the game's own callers.");

        // ---- Lab counters (read by reflection; cumulative ones are cleared by ResetCounters) ----
        public static int Workers;            // threads started
        public static int ActiveWorkers;      // threads allowed to build (1 when not parallel)
        public static bool TreeSafe = true;   // no other mod patches Build's call tree
        public static int QueueLength;        // requests waiting for a worker
        public static int Building;           // on a worker or the calling thread right now
        public static int Ready;              // finished results held
        public static int Ahead;              // of those, not reported ready to anyone yet (built ahead of the game's callers)
        public static long Builds;            // finished on workers
        public static long InlineBuilds;      // built on the thread that called RequestTerrainSync
        public static long SyncCalls, SyncHits, SyncWaits;
        public static double MaxSyncWaitMs, TotalSyncMs;   // time spent inside RequestTerrainSync (wait or inline build)
        public static double MaxBuildMs, TotalBuildMs;     // per build, workers and inline
        public static long Taken, Evicted, Expired, DroppedStale, Superseded, StaleBiomes, VanillaTakeovers, Errors, WaitTimeouts;
        public static long WantedBuilds;      // taken from the Wanted lane (polled right now)
        public static long HeldBack;          // a worker went idle because only work past the build-ahead window was left

        // Lane API, one slot per Lane. Queued: work Request put on that lane (new, or moved up from a slower lane).
        // Built: finished while on it. Taken: handed out (TryTake, or the game's RequestTerrain/RequestTerrainSync).
        // Expired: gone untaken (pin and ReadyTtlSeconds ran out, nobody asked for StaleQueuedSeconds, evicted, world or
        // generator patches changed, scheduler or lanes switched off, build threw; not Release). Queued again when asked
        // about (see Revive), such work counts as queued anew. Wait: from the first Request on a lane to finished.
        public static readonly long[] LaneQueued = new long[LaneCount], LaneBuilt = new long[LaneCount],
            LaneTaken = new long[LaneCount], LaneExpired = new long[LaneCount];
        public static readonly double[] LaneMaxWaitMs = new double[LaneCount], LaneTotalWaitMs = new double[LaneCount];
        public static readonly int[] LaneAhead = new int[LaneCount];   // finished, not reported to anyone yet (the lane's window)
        public static int LanesAhead;         // sum of LaneAhead
        public static int LanesReady;         // finished results held for the far lanes (FarCore..Prefetch), reported or not
        public static int Pins;               // results pinned through Pin
        public static long PinsRefused;       // Pin calls refused with MaxPins pinned
        public static long LaneFallbacks;     // lane calls that went the game's way (HeightmapLanes or HeightmapBuilder off)
        public static long Released;          // entries dropped by Release
        public static long Requeued;          // lane work the module had dropped, queued again when asked about (see Revive)

        // The per-lane counters again as scalars: the lab reads public static numbers, not arrays.
        public static long LanePlayerQueued => LaneQueued[0];
        public static long LanePlayerBuilt => LaneBuilt[0];
        public static long LanePlayerTaken => LaneTaken[0];
        public static long LanePlayerExpired => LaneExpired[0];
        public static int LanePlayerAhead => LaneAhead[0];
        public static double LanePlayerWaitAvgMs => WaitAvgMs(0);
        public static double LanePlayerWaitMaxMs => LaneMaxWaitMs[0];
        public static long LaneFarCoreQueued => LaneQueued[1];
        public static long LaneFarCoreBuilt => LaneBuilt[1];
        public static long LaneFarCoreTaken => LaneTaken[1];
        public static long LaneFarCoreExpired => LaneExpired[1];
        public static int LaneFarCoreAhead => LaneAhead[1];
        public static double LaneFarCoreWaitAvgMs => WaitAvgMs(1);
        public static double LaneFarCoreWaitMaxMs => LaneMaxWaitMs[1];
        public static long LaneFarVisualQueued => LaneQueued[2];
        public static long LaneFarVisualBuilt => LaneBuilt[2];
        public static long LaneFarVisualTaken => LaneTaken[2];
        public static long LaneFarVisualExpired => LaneExpired[2];
        public static int LaneFarVisualAhead => LaneAhead[2];
        public static double LaneFarVisualWaitAvgMs => WaitAvgMs(2);
        public static double LaneFarVisualWaitMaxMs => LaneMaxWaitMs[2];
        public static long LaneFarTerrainQueued => LaneQueued[3];
        public static long LaneFarTerrainBuilt => LaneBuilt[3];
        public static long LaneFarTerrainTaken => LaneTaken[3];
        public static long LaneFarTerrainExpired => LaneExpired[3];
        public static int LaneFarTerrainAhead => LaneAhead[3];
        public static double LaneFarTerrainWaitAvgMs => WaitAvgMs(3);
        public static double LaneFarTerrainWaitMaxMs => LaneMaxWaitMs[3];
        public static long LanePrefetchQueued => LaneQueued[4];
        public static long LanePrefetchBuilt => LaneBuilt[4];
        public static long LanePrefetchTaken => LaneTaken[4];
        public static long LanePrefetchExpired => LaneExpired[4];
        public static int LanePrefetchAhead => LaneAhead[4];
        public static double LanePrefetchWaitAvgMs => WaitAvgMs(4);
        public static double LanePrefetchWaitMaxMs => LaneMaxWaitMs[4];

        static double WaitAvgMs(int l) => LaneBuilt[l] > 0 ? LaneTotalWaitMs[l] / LaneBuilt[l] : 0;

        public static void ResetCounters()
        {
            lock (s_lock)
            {
                Builds = InlineBuilds = SyncCalls = SyncHits = SyncWaits = 0;
                MaxSyncWaitMs = TotalSyncMs = MaxBuildMs = TotalBuildMs = 0;
                Taken = Evicted = Expired = DroppedStale = Superseded = StaleBiomes = VanillaTakeovers = Errors = WaitTimeouts = 0;
                WantedBuilds = HeldBack = 0;
                Array.Clear(LaneQueued, 0, LaneCount);
                Array.Clear(LaneBuilt, 0, LaneCount);
                Array.Clear(LaneTaken, 0, LaneCount);
                Array.Clear(LaneExpired, 0, LaneCount);
                Array.Clear(LaneMaxWaitMs, 0, LaneCount);
                Array.Clear(LaneTotalWaitMs, 0, LaneCount);
                PinsRefused = LaneFallbacks = Released = Requeued = 0;
            }
        }

        // The lane section is appended once anything used the lane API, so the report reads as before otherwise.
        public static string Report()
        {
            lock (s_lock)
            {
                string report = $"heightmap builder {(Feature.On ? s_stepAside ? "stepped aside (other mods rewrite its entry points)" : "on" : "off")}: " +
                    $"workers {ActiveWorkers}/{Workers}{(TreeSafe ? "" : " (other mods patch the generator)")}, " +
                    $"queue {QueueLength}, building {Building}, ready {Ready} (ahead {Ahead}/{SoftCap}) | builds {Builds} ({(Builds + InlineBuilds > 0 ? TotalBuildMs / (Builds + InlineBuilds) : 0):F1} ms avg, max {MaxBuildMs:F1}), " +
                    $"inline {InlineBuilds}, wanted {WantedBuilds}, held back {HeldBack} | sync {SyncCalls}: ready {SyncHits}, waited {SyncWaits}, max {MaxSyncWaitMs:F1} ms, total {TotalSyncMs:F0} ms | " +
                    $"taken {Taken}, evicted {Evicted}, expired {Expired}, stale {DroppedStale}, superseded {Superseded}, biome-stale {StaleBiomes}, from game {VanillaTakeovers}, errors {Errors}, wait timeouts {WaitTimeouts}";
                return LanesUsed() ? report + " | " + LaneReport() : report;
            }
        }

        // Per lane: queued/built/taken/expired, window, wait avg/max.
        public static string LaneReport()
        {
            lock (s_lock)
            {
                var sb = new StringBuilder($"lanes {(LanesFeature.On ? "on" : "off")}:");
                for (int l = 0; l < LaneCount; l++)
                    sb.Append($" {(Lane)l} {LaneQueued[l]}/{LaneBuilt[l]}/{LaneTaken[l]}/{LaneExpired[l]} ahead {LaneAhead[l]}" +
                        $"{(l == (int)Lane.Player ? "" : "/" + LaneSoftCap + " held cap " + LaneHeldCap(l))} wait {WaitAvgMs(l):F0}/{LaneMaxWaitMs[l]:F0} ms;");
                sb.Append($" held {LanesReady}/{LaneHardCap}, pinned {Pins}/{MaxPins} (refused {PinsRefused}), released {Released}, requeued {Requeued}, fallbacks {LaneFallbacks}");
                return sb.ToString();
            }
        }

        static bool LanesUsed()
        {
            for (int l = 0; l < LaneCount; l++)
                if (LaneQueued[l] != 0 || LaneBuilt[l] != 0 || LaneTaken[l] != 0 || LaneExpired[l] != 0 || LaneAhead[l] != 0)
                    return true;
            return Pins != 0 || PinsRefused != 0 || LaneFallbacks != 0 || Released != 0 || Requeued != 0;
        }

        // A result reported ready stays at least this long (or until taken): the caller asks for it right after.
        const double PinSeconds = 60;
        // Finished results nobody takes: dropped this long after they were built or last reported ready.
        const double ReadyTtlSeconds = 120;
        // Results nobody was told about yet that workers build ahead of the callers (~85 KB each). A far area takes about
        // one zone a frame, so this is a few seconds of lead; past it workers only take work being polled right now.
        const int SoftCap = 128;
        // Above this finished results are evicted (only a runaway caller that is told "ready" and never takes gets here).
        const int HardCap = 1024;
        // A queued item polled within this long is wanted now (see DequeueNext). Callers poll every frame what they are
        // waiting for, so this only has to outlast a slow frame.
        const double WantedSeconds = 0.5;
        // How often other mods' patches are looked at again (see LookAtOtherMods).
        const double PatchCheckSeconds = 2;
        // Queued work nobody has asked about for this long is dropped (the player or the area moved on); asking
        // again queues it again.
        const double StaleQueuedSeconds = 30;
        // Zones (not distant LOD) closer than this to the player jump ahead of the far areas' backlog.
        const float NearRadius = 160f;
        // A synchronous request waits this long for the worker building its item (a build takes ~5-60 ms), then
        // builds it itself; the worker's copy is then dropped. One-worker mode waits longer: there the point is to
        // never run two builds at once, so only a hung worker is overtaken.
        const int WaitInlineMs = 500;
        const int WaitOneWorkerMs = 10000;
        // A far lane's build-ahead window: its results not reported to their caller yet, counted together with those of
        // the faster far lanes (a lane result is ~85-130 KB; the four windows hold at most 4x this between them). Work
        // polled again is not held back by it (see DequeueNext): only by the lane's share of LaneHardCap.
        const int LaneSoftCap = 64;
        // Finished far-lane results held at most, reported or not: past it the far lanes wait until their callers take
        // some (or Release them), so lane work alone never fills the table to HardCap, where the game's own far work
        // would stop too. FarCore builds up to it, each slower lane up to one window less (see LaneHeldCap); FarCore's
        // work polled right now goes past it while the table has room for the game's window (see CoreWantedRoom).
        const int LaneHardCap = 512;
        // Results pinned through Pin at once (well under HardCap: eviction always finds one that is not pinned), and the
        // longest pin.
        const int MaxPins = 256;
        const float MaxPinSeconds = 600f;
        // Lane work the module dropped on its own, remembered at most (see s_dropped): past it the memory starts over.
        const int MaxDropped = 4096;

        // Queues, fastest first. Urgent: a synchronous request in one-worker mode. Near: zones close to the player, and
        // the Player lane. Wanted: Normal work polled again recently. Then the far lanes, each with its work polled again
        // recently (...W) ahead of the rest of it. Normal: the game's other work, in the order first asked for. Prefetch
        // last.
        const int Urgent = 0, Near = 1, Wanted = 2, CoreW = 3, Core = 4, VisualW = 5, Visual = 6, TerrainW = 7, Terrain = 8,
            Normal = 9, PrefetchW = 10, Prefetch = 11, QueueCount = 12;

        // The lane API's lanes, fastest first (strictly: see DequeueNext). Player is the game's near-player queue.
        public enum Lane { Player, FarCore, FarVisual, FarTerrain, Prefetch }

        const int LaneCount = 5;
        // Per Lane: its queue, and where its work goes when polled again (the Player lane is never held back).
        static readonly int[] s_laneQueue = { Near, Core, Visual, Terrain, Prefetch };
        static readonly int[] s_laneWanted = { Near, CoreW, VisualW, TerrainW, PrefetchW };

        // A far lane builds while the far lanes together hold fewer finished results than this (LanesReady): 512 for
        // FarCore, 448, 384 and 320 for the slower ones. So results a slower lane holds (reported or pinned and not
        // taken, a warm-up that polls and never takes) leave a faster one at least LaneSoftCap of room, and all of them
        // together stay within LaneHardCap.
        static int LaneHeldCap(int l) => LaneHardCap - (l - (int)Lane.FarCore) * LaneSoftCap;

        // FarCore's work polled right now (the data a core zone, the player's next crossing target, waits for) is held
        // back by no other lane's results: only by the table's own room, which keeps space for the game's build-ahead
        // window (HardCap - SoftCap). Bounded by what the FarCore caller polls; the slower lanes wait meanwhile.
        static bool CoreWantedRoom() => Ready < HardCap - SoftCap;

        enum State { Queued, Building, Ready, Dead }

        // A terrain request: HMBuildData.IsEqual's fields. Equals is exact (for the table); Matches is the game's own
        // comparison.
        public readonly struct Key : IEquatable<Key>
        {
            public readonly Vector3 Center;
            public readonly int Width;
            public readonly float Scale;
            public readonly bool DistantLod;
            public readonly WorldGenerator WorldGen;

            public Key(Vector3 center, int width, float scale, bool distantLod, WorldGenerator worldGen)
            {
                Center = center;
                Width = width;
                Scale = scale;
                DistantLod = distantLod;
                WorldGen = worldGen;
            }

            // What ZoneSystem.SpawnZone asks for a zone (the zone prefab's heightmap at the zone's centre), so a result
            // requested with it is the one SpawnZone's IsTerrainReady finds. Main thread; default (nothing) without a
            // ZoneSystem.
            public static Key ForZone(Vector2s zone)
            {
                Heightmap hm = ZonePrefabHeightmap();
                return hm == null ? default : new Key(ZoneSystem.GetZonePos(zone), hm.m_width, hm.m_scale, hm.IsDistantLod, WorldGenerator.instance);
            }

            // What Heightmap.Regenerate asks for this heightmap where it stands now. Main thread.
            public static Key ForHeightmap(Heightmap hm) =>
                new Key(hm.transform.position, hm.m_width, hm.m_scale, hm.IsDistantLod, WorldGenerator.instance);

            public bool Equals(Key o) =>
                Center.x == o.Center.x && Center.y == o.Center.y && Center.z == o.Center.z && Width == o.Width &&
                Scale == o.Scale && DistantLod == o.DistantLod && ReferenceEquals(WorldGen, o.WorldGen);

            public override bool Equals(object obj) => obj is Key k && Equals(k);

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = H(Center.x);
                    h = h * 31 + H(Center.z);
                    h = h * 31 + H(Center.y);
                    h = h * 31 + Width;
                    h = h * 31 + H(Scale);
                    h = h * 2 + (DistantLod ? 1 : 0);
                    return h * 31 + RuntimeHelpers.GetHashCode(WorldGen);
                }
            }

            // 0 and -0 are equal, so they must hash alike.
            static int H(float f) => f == 0f ? 0 : f.GetHashCode();

            // Exactly HMBuildData.IsEqual: Vector3 == is "closer than 1e-5", scale compares as float ==.
            public bool Matches(in Key o) =>
                Center == o.Center && Width == o.Width && Scale == o.Scale && DistantLod == o.DistantLod && WorldGen == o.WorldGen;
        }

        sealed class Entry
        {
            public Key Key;
            public State State = State.Dead;
            public HeightmapBuilder.HMBuildData Data;
            public long LastAsked;     // last poll or request: staleness of queued work
            public long LastTouched;   // finished or last reported: LRU of finished results
            public long PinnedUntil;
            public bool Reported;      // reported ready, asked for synchronously or pinned: in no build-ahead window
            // The fastest queue it has a reference in, and a bit per queue it has one in. A queued item keeps every
            // reference it was given: one polled again that went cold falls back to the fastest place it still has (its
            // place in Normal or its lane's queue, see NextWanted).
            public int Lane = QueueCount;
            public int Refs;
            // Lane API: the fastest lane it was requested on (-1: only the game asked for it), when first requested on
            // one, and until when Pin keeps it (0: not pinned).
            public int ApiLane = -1;
            public long ApiAsked;
            public long ApiPinnedUntil;
            public ManualResetEventSlim Done;   // created when someone waits for this item
            // The world's biome table the result was built against (see BiomesSame).
            public AltBiomeWorldData Biomes;
            public bool BiomesReady;
            // Its place in s_cells (see FindNear), while in s_entries.
            public long Cell;
            public Entry CellNext;
        }

        // What s_dropped keeps of dropped lane work: its lane, and until when it was pinned (0: not pinned).
        readonly struct Dropped
        {
            public readonly int Lane;
            public readonly long PinnedUntil;

            public Dropped(int lane, long pinnedUntil)
            {
                Lane = lane;
                PinnedUntil = pinnedUntil;
            }
        }

        delegate HeightmapBuilder.HMBuildData RequestTerrainFn(HeightmapBuilder self, Vector3 center, int width, float scale, bool distantLod, WorldGenerator worldGen);
        delegate void GetWeightFn(WorldGenerator self, WorldGenerator.RiverPoint[] points, float wx, float wy, out float weight, out float width);

        static MethodInfo s_buildMethod;
        static Action<HeightmapBuilder, HeightmapBuilder.HMBuildData> s_build;
        static RequestTerrainFn s_requestTerrain;
        static GetWeightFn s_getWeight;
        static AccessTools.FieldRef<WorldGenerator, Dictionary<Vector2i, WorldGenerator.RiverPoint[]>> s_riverPoints;
        static AccessTools.FieldRef<HeightmapBuilder, List<HeightmapBuilder.HMBuildData>> s_gameReady;
        static AccessTools.FieldRef<HeightmapBuilder, object> s_gameLock;

        static readonly object s_lock = new object();
        static readonly Dictionary<Key, Entry> s_entries = new Dictionary<Key, Entry>();
        // The same entries by the (x, z) cell of their centre, chained through Entry.CellNext (see FindNear).
        static readonly Dictionary<long, Entry> s_cells = new Dictionary<long, Entry>();
        // Lane work the module dropped on its own while its caller may still be waiting for it (switched off or stepped
        // aside, other mods' generator patches changed, built before the biome table, evicted, its build threw), by key:
        // its lane and pin, so IsReady, TryTake and Pin, which never queue unknown work, queue it again (see Revive). Not
        // work nobody asked about (stale, expired), taken or released. Cleared when the world changes.
        static readonly Dictionary<Key, Dropped> s_dropped = new Dictionary<Key, Dropped>();
        static readonly Queue<Entry>[] s_lanes = NewQueues();
        static readonly List<Entry> s_temp = new List<Entry>();
        // One permit per request that entered the queue; a worker takes one per item it looks for.
        static readonly SemaphoreSlim s_signal = new SemaphoreSlim(0);
        // Set while every worker may build; workers past the first park on it otherwise.
        static readonly ManualResetEventSlim s_gate = new ManualResetEventSlim(false);
        static readonly double s_msPerTick = 1000.0 / Stopwatch.Frequency;
        static readonly long s_pinTicks = Ticks(PinSeconds), s_readyTtl = Ticks(ReadyTtlSeconds), s_staleQueued = Ticks(StaleQueuedSeconds),
            s_wantedTicks = Ticks(WantedSeconds), s_patchCheckTicks = Ticks(PatchCheckSeconds);

        static HeightmapBuilder s_builder;
        static Thread[] s_threads;
        static Entry[] s_current;          // what each worker is building (under s_lock)
        static int s_idleWaiters;
        // Permits workers kept back when only held-back work was left; handed out again as room is made (under s_lock).
        static int s_owed;
        static volatile int s_count;       // s_entries.Count, read without the lock as a hint
        static volatile int s_limit = 1;
        static volatile bool s_inline;
        static volatile bool s_stop;
        // The game's builder only: until Install completes, after Dispose, or when the workers could not start.
        static volatile bool s_inactive = true;
        // Another mod rewrites an entry point: behave as if switched off (see LookAtOtherMods).
        static volatile bool s_stepAside;
        static volatile bool s_riverLocal;
        static long s_nextSweep;
        // When the first pin made through Pin runs out, or earlier (a pin that went or was set anew leaves it early;
        // Sweep sets it exactly): Pin at MaxPins sweeps only past it.
        static long s_pinsRunOut = long.MaxValue;
        static string s_pendingLog;
        // What the last look at other mods' patches found (main thread); null until the first look.
        static long s_nextPatchCheck;
        static string s_treeSig, s_entrySig = "", s_afterSig = "";

        [ThreadStatic] static bool t_worker;
        [ThreadStatic] static bool t_inSync;
        [ThreadStatic] static WorldGenerator t_riverGen;
        [ThreadStatic] static Vector2i t_riverGrid;
        [ThreadStatic] static WorldGenerator.RiverPoint[] t_riverPoints;

        // The game quit (Dispose): the lane API answers "not ready" without asking the game's builder, which is gone.
        static volatile bool s_disposed;
        // The zone prefab's heightmap, for Key.ForZone (main thread).
        static GameObject s_zonePrefab;
        static Heightmap s_zoneHeightmap;

        static long Ticks(double seconds) => (long)(seconds * Stopwatch.Frequency);

        static Queue<Entry>[] NewQueues()
        {
            var queues = new Queue<Entry>[QueueCount];
            for (int i = 0; i < QueueCount; i++)
                queues[i] = new Queue<Entry>();
            return queues;
        }

        static Heightmap ZonePrefabHeightmap()
        {
            ZoneSystem zs = ZoneSystem.instance;
            GameObject prefab = zs == null ? null : zs.m_zonePrefab;
            if (prefab == null)
                return null;
            if (!ReferenceEquals(prefab, s_zonePrefab) || s_zoneHeightmap == null)
            {
                s_zoneHeightmap = prefab.GetComponentInChildren<Heightmap>();
                s_zonePrefab = prefab;
            }
            return s_zoneHeightmap;
        }

        static void Install(Harmony harmony)
        {
            Type hb = typeof(HeightmapBuilder);
            s_buildMethod = AccessTools.Method(hb, "Build");
            MethodInfo request = AccessTools.Method(hb, "RequestTerrain");
            // First, before anything below can throw: with the module left out the lane API polls the game's
            // IsTerrainReady, and TryTake takes what that reports through this. Made before the patches, it still
            // enters the patched code (as s_build does).
            if (request != null)
                s_requestTerrain = AccessTools.MethodDelegate<RequestTerrainFn>(request);
            MethodInfo getWeight = AccessTools.Method(typeof(WorldGenerator), "GetWeight");
            if (s_buildMethod == null || request == null || getWeight == null)
                throw new MissingMethodException("HeightmapBuilder.Build/RequestTerrain or WorldGenerator.GetWeight not found");
            // Calls through these enter the methods' own (patched) code, so every Harmony patch on them still runs.
            s_build = AccessTools.MethodDelegate<Action<HeightmapBuilder, HeightmapBuilder.HMBuildData>>(s_buildMethod);
            s_getWeight = AccessTools.MethodDelegate<GetWeightFn>(getWeight);
            s_riverPoints = AccessTools.FieldRefAccess<WorldGenerator, Dictionary<Vector2i, WorldGenerator.RiverPoint[]>>("m_riverPoints");
            s_gameReady = AccessTools.FieldRefAccess<HeightmapBuilder, List<HeightmapBuilder.HMBuildData>>("m_ready");
            s_gameLock = AccessTools.FieldRefAccess<HeightmapBuilder, object>("m_lock");

            // Last, so a mod that replaces these itself runs first and we step aside (__runOriginal). One that rewrites
            // their bodies instead (transpiler) is found by LookAtOtherMods, and the module then steps aside entirely.
            Patch(harmony, AccessTools.Method(hb, nameof(HeightmapBuilder.IsTerrainReady)), nameof(IsTerrainReadyPrefix), Priority.Last);
            Patch(harmony, request, nameof(RequestTerrainPrefix), Priority.Last);
            Patch(harmony, AccessTools.Method(hb, nameof(HeightmapBuilder.RequestTerrainSync)), nameof(RequestTerrainSyncPrefix), Priority.Last);
            Patch(harmony, AccessTools.Method(typeof(WorldGenerator), "GetRiverWeight"), nameof(GetRiverWeightPrefix), Priority.Last);
            Patch(harmony, AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.Initialize)), nameof(InitializePrefix), Priority.Normal);
            harmony.Patch(AccessTools.Method(hb, nameof(HeightmapBuilder.Dispose)),
                postfix: new HarmonyMethod(typeof(HeightmapBuilderModule), nameof(DisposePostfix)));

            Feature.Switched += OnSwitched;
            ParallelFeature.Switched += _ => ApplyWorkerLimit();
            LanesFeature.Switched += OnLanesSwitched;
            s_inactive = false;
        }

        static void Patch(Harmony harmony, MethodInfo target, string prefix, int priority)
        {
            if (target == null)
                throw new MissingMethodException("patch target for " + prefix + " not found");
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(HeightmapBuilderModule), prefix) { priority = priority });
        }

        // ---------------------------------------------------------------- entry points (main thread)

        // Ready: true (and the result is pinned until taken). Unknown: queued. Off: only our own finished results
        // are answered here (they are takeable through RequestTerrain); everything else goes the game's way.
        static bool IsTerrainReadyPrefix(HeightmapBuilder __instance, Vector3 center, int width, float scale, bool distantLod,
            WorldGenerator worldGen, ref bool __result, bool __runOriginal)
        {
            if (!__runOriginal || worldGen == null || s_inactive)
                return true;
            bool on = Feature.On;
            if (!on && s_count == 0)
                return true;
            long now = Stopwatch.GetTimestamp();
            if (on && !EnsureStarted(__instance, now))
                return true;
            on &= !s_stepAside;
            if (!on && s_count == 0)
                return true;
            var key = new Key(center, width, scale, distantLod, worldGen);
            int lane = on ? LaneFor(key) : Normal;
            lock (s_lock)
            {
                if (now >= s_nextSweep)
                    Sweep(now);
                Entry e = Find(key, now);
                if (e != null && e.State == State.Ready)
                {
                    Pin(e, now);
                    e.LastTouched = now;
                    __result = true;
                    return false;
                }
                if (!on)
                    return true;
                if (e == null)
                {
                    e = Add(key, now);
                    Enqueue(e, lane);
                }
                else
                {
                    e.LastAsked = now;
                    // Polled again: the caller is waiting for it now, so it goes ahead of work queued and not asked
                    // about since (see DequeueNext); to Near when the player came close.
                    if (e.State == State.Queued)
                        Enqueue(e, lane == Normal ? Wanted : lane);
                }
            }
            __result = false;
            return false;
        }

        // The game's non-blocking take: a finished result is handed over (and forgotten), anything else is queued.
        // Called by RequestTerrainSync below as the game's own loop calls it, so other mods' patches on it still see
        // every request.
        static bool RequestTerrainPrefix(HeightmapBuilder __instance, Vector3 center, int width, float scale, bool distantLod,
            WorldGenerator worldGen, ref HeightmapBuilder.HMBuildData __result, bool __runOriginal)
        {
            if (!__runOriginal || worldGen == null || s_inactive)
                return true;
            bool on = Feature.On;
            if (!on && s_count == 0)
                return true;
            long now = Stopwatch.GetTimestamp();
            if (on && !EnsureStarted(__instance, now))
                return true;
            on &= !s_stepAside;
            if (!on && s_count == 0)
                return true;
            var key = new Key(center, width, scale, distantLod, worldGen);
            int lane = on && !t_inSync ? LaneFor(key) : Normal;
            lock (s_lock)
            {
                Entry e = Find(key, now);
                if (e != null && e.State == State.Ready)
                {
                    __result = Take(e);
                    return false;
                }
                if (!on)
                    return true;
                if (e != null)
                {
                    e.LastAsked = now;
                    // Polled directly (not from RequestTerrainSync, which decides itself): as IsTerrainReady.
                    if (!t_inSync && e.State == State.Queued)
                        Enqueue(e, lane == Normal ? Wanted : lane);
                }
                else if (!t_inSync)   // RequestTerrainSync decides itself how this one gets built
                    Enqueue(Add(key, now), lane);
            }
            __result = null;
            return false;
        }

        static bool RequestTerrainSyncPrefix(HeightmapBuilder __instance, Vector3 center, int width, float scale, bool distantLod,
            WorldGenerator worldGen, ref HeightmapBuilder.HMBuildData __result, bool __runOriginal)
        {
            if (!__runOriginal || worldGen == null || s_inactive)
                return true;
            var key = new Key(center, width, scale, distantLod, worldGen);
            if (Feature.On && !EnsureStarted(__instance, Stopwatch.GetTimestamp()))
                return true;
            if (!Feature.On || s_stepAside)
            {
                // Off (or stepped aside): what is still ours (finished, or in a worker's hands) is served from here; the
                // rest the game builds.
                if (s_count == 0 || !WaitIfOurs(key))
                    return true;
                __result = s_requestTerrain(__instance, center, width, scale, distantLod, worldGen);
                return __result == null;
            }
            long t0 = Stopwatch.GetTimestamp();
            SyncCalls++;
            HeightmapBuilder.HMBuildData data = null;
            t_inSync = true;
            try
            {
                for (int i = 0; i < 3 && data == null; i++)
                {
                    data = s_requestTerrain(__instance, center, width, scale, distantLod, worldGen);
                    if (data != null)
                    {
                        if (i == 0)
                            SyncHits++;
                    }
                    else
                        data = MakeReady(__instance, key);   // non-null only for a result built outside the table
                }
                // Still nothing: another mod's prefix keeps RequestTerrain from reaching ours (the game would spin
                // forever here). Build it directly.
                if (data == null)
                    data = BuildInline(key, null);
            }
            finally
            {
                t_inSync = false;
            }
            double ms = (Stopwatch.GetTimestamp() - t0) * s_msPerTick;
            TotalSyncMs += ms;
            if (ms > MaxSyncWaitMs)
                MaxSyncWaitMs = ms;
            __result = data;
            return false;
        }

        // Makes the item takeable by the next RequestTerrain: a queued or unknown item is built right here (several
        // workers mode) or sent to the front of the queue (one worker mode); an item a worker is building is waited
        // for, that item only. Returns a result only when it could not go through the table.
        static HeightmapBuilder.HMBuildData MakeReady(HeightmapBuilder hb, in Key key)
        {
            // Reported ready by the game's builder just before the feature was switched on: take it from there.
            HeightmapBuilder.HMBuildData game = TakeFromGame(hb, key);
            if (game != null)
                return game;
            long now = Stopwatch.GetTimestamp();
            Entry e;
            ManualResetEventSlim done = null;
            bool build = false;
            bool inline = s_inline;
            lock (s_lock)
            {
                e = Find(key, now);
                if (e != null && e.State == State.Ready)
                    return null;
                if (e == null)
                    e = Add(key, now);
                Pin(e, now);
                e.LastAsked = now;
                if (e.State == State.Building)
                    done = e.Done ??= new ManualResetEventSlim(false);
                else if (inline)
                {
                    // Out of the queue (the stale queue reference is skipped by the workers) and onto this thread.
                    SetState(e, State.Building);
                    build = true;
                }
                else
                {
                    Enqueue(e, Urgent);
                    done = e.Done ??= new ManualResetEventSlim(false);
                }
            }
            if (build)
                return BuildInline(key, e);
            SyncWaits++;
            if (done.Wait(inline ? WaitInlineMs : WaitOneWorkerMs))
                return null;
            // The worker did not finish one build in that time (hung or starved): build it here rather than freeze.
            if (++WaitTimeouts <= 3)
                EnginePlugin.Log?.LogWarning($"[HeightmapBuilder] waited {(inline ? WaitInlineMs : WaitOneWorkerMs)} ms for a terrain build at {key.Center}, building it on the main thread");
            return BuildInline(key, e);
        }

        // Builds on the calling thread. With an entry, the result completes it (unless a worker got there first) and
        // the caller takes it through RequestTerrain; returns the result only when there is no entry to complete.
        static HeightmapBuilder.HMBuildData BuildInline(in Key key, Entry e)
        {
            long t0 = Stopwatch.GetTimestamp();
            HeightmapBuilder.HMBuildData data;
            AltBiomeWorldData biomes;
            bool biomesReady;
            try
            {
                data = BuildData(key, out biomes, out biomesReady);
            }
            catch
            {
                if (e != null)
                    lock (s_lock)
                    {
                        if (e.State == State.Building)
                            Drop(e);
                    }
                throw;
            }
            long t1 = Stopwatch.GetTimestamp();
            lock (s_lock)
            {
                InlineBuilds++;
                CountBuild(t1 - t0);
                if (e == null)
                    return data;
                if (e.State == State.Building || e.State == State.Queued)
                {
                    Complete(e, data, biomes, biomesReady, t1);
                    return null;
                }
                if (e.State == State.Ready)
                    return null;   // a worker finished it meanwhile: take that one
                return data;       // dropped meanwhile (world change): hand this one over directly
            }
        }

        // Feature off: wait for an item a worker still has in hand. True when there is a finished result of ours.
        static bool WaitIfOurs(in Key key)
        {
            Entry e;
            ManualResetEventSlim done;
            lock (s_lock)
            {
                e = Find(key, Stopwatch.GetTimestamp());
                if (e == null || e.State == State.Queued || e.State == State.Dead)
                    return false;
                if (e.State == State.Ready)
                    return true;
                done = e.Done ??= new ManualResetEventSlim(false);
            }
            done.Wait(WaitInlineMs);
            lock (s_lock)
                return e.State == State.Ready;
        }

        // The game's builder keeps its own finished results while the feature is on (nothing takes them); one it
        // reported ready before the switch is still the right data.
        static HeightmapBuilder.HMBuildData TakeFromGame(HeightmapBuilder hb, in Key key)
        {
            List<HeightmapBuilder.HMBuildData> ready = s_gameReady(hb);
            object gameLock = s_gameLock(hb);
            if (ready == null || gameLock == null || ready.Count == 0)
                return null;
            lock (gameLock)
            {
                for (int i = 0; i < ready.Count; i++)
                    if (ready[i].IsEqual(key.Center, key.Width, key.Scale, key.DistantLod, key.WorldGen))
                    {
                        HeightmapBuilder.HMBuildData data = ready[i];
                        ready.RemoveAt(i);
                        VanillaTakeovers++;
                        return data;
                    }
            }
            return null;
        }

        static int LaneFor(in Key key)
        {
            if (key.DistantLod)
                return Normal;
            ZNet znet = ZNet.instance;
            if ((object)znet == null)
                return Normal;
            Vector3 p = znet.GetReferencePosition();
            float dx = key.Center.x - p.x, dz = key.Center.z - p.z;
            return dx * dx + dz * dz < NearRadius * NearRadius ? Near : Normal;
        }

        // ---------------------------------------------------------------- lane API (main thread, like the game's callers)

        // Asks for a build on a lane. True when it is finished: it is then reported ready and kept PinSeconds, exactly as
        // IsTerrainReady does, for the caller to take (TryTake, or a Heightmap's Regenerate, or SpawnZone). Otherwise it
        // is queued on that lane, or moved up to it from a slower one (never down); asked again while queued, it counts
        // as polled and goes first within its lane, past the lane's window, as the game's polls do (so poll what is
        // needed now, not everything ever requested). Work nobody asks about (Request, IsReady, TryTake, Pin, or the
        // game's own requests) for StaleQueuedSeconds is dropped unless pinned; Release drops it at once. A key for
        // another generator than the current one (a world that ended) is refused. Callers waiting for a result poll
        // with this (a dictionary lookup, a few cells for a key not in the table): a result dropped meanwhile (world or
        // generator patches changed, scheduler or lanes switched, evicted unpinned) is then queued again. IsReady,
        // TryTake and Pin never queue work nobody asked for, but queue again lane work the module itself dropped (as
        // this does, on its old lane and with what was left of its pin, see Revive).
        // HeightmapLanes off, or the scheduler not taking requests (HeightmapBuilder off, stepped aside, not started):
        // the game's IsTerrainReady, i.e. what a game caller polling it gets.
        public static bool Request(in Key key, Lane lane)
        {
            int l = (int)lane;
            if ((uint)l >= LaneCount)
                throw new ArgumentOutOfRangeException(nameof(lane));
            if (!Current(key))
                return false;
            long now = Stopwatch.GetTimestamp();
            if (!LanesActive(now))
                return Poll(key);
            lock (s_lock)
            {
                if (now >= s_nextSweep)
                    Sweep(now);
                Entry e = Ask(key, l, now);
                // Dropped by the module meanwhile (or just now, built before the biome table): its old lane and pin back.
                if (s_dropped.Count > 0)
                    Revive(key, now);
                return e.State == State.Ready;
            }
        }

        // Request under s_lock: the entry, finished (then reported ready, as by IsTerrainReady), or queued on lane l or
        // moved up to it (never down); asked again while queued, it counts as polled.
        static Entry Ask(in Key key, int l, long now)
        {
            Entry e = Find(key, now);
            if (e != null && e.State == State.Ready)
            {
                Pin(e, now);
                e.LastTouched = now;
                ToLane(e, l, now);
                return e;
            }
            if (e == null)
            {
                e = Add(key, now);
                ToLane(e, l, now);
                Enqueue(e, s_laneQueue[l]);
                LaneQueued[l]++;
                return e;
            }
            e.LastAsked = now;
            if (ToLane(e, l, now) && e.State == State.Queued)
            {
                // Its place in this lane, in order, besides the one it has in a slower lane or the game's queue.
                Enqueue(e, s_laneQueue[l]);
                LaneQueued[l]++;
            }
            if (e.State == State.Queued)
                Enqueue(e, s_laneWanted[e.ApiLane]);
            return e;
        }

        // Lane work the module dropped on its own (s_dropped), asked about again by a caller that still waits for it:
        // queued again on its lane, as Request does, and pinned again for what was left of its pin (room permitting). The
        // caller then finds it as if it had never gone. False for any other key (under s_lock).
        static bool Revive(in Key key, long now)
        {
            if (s_dropped.Count == 0 || !s_dropped.TryGetValue(key, out Dropped d))
                return false;
            Requeued++;
            Entry e = Ask(key, d.Lane, now);
            s_dropped.Remove(key);
            if (d.PinnedUntil > now && e.ApiPinnedUntil == 0 && Pins < MaxPins)
                PinUntil(e, d.PinnedUntil, now);
            return true;
        }

        // True when the build is finished; it is then reported ready, as by Request. Never queues work nobody asked for
        // (unknown work stays unknown, and a key is unknown again once its result is taken), but queues again lane work
        // the module itself dropped (see Revive); queued work is kept from going stale and, when it was requested on a
        // lane, counts as polled. Off: the game's IsTerrainReady, which does queue unknown work.
        public static bool IsReady(in Key key)
        {
            if (!Current(key))
                return false;
            long now = Stopwatch.GetTimestamp();
            if (!LanesActive(now))
                return Poll(key);
            lock (s_lock)
            {
                if (now >= s_nextSweep)
                    Sweep(now);
                Entry e = FindAsked(key, now);
                if (e == null)
                    return false;
                if (e.State == State.Ready)
                {
                    Pin(e, now);
                    e.LastTouched = now;
                    return true;
                }
                Polled(e, now);
                return false;
            }
        }

        // Keeps the result for this key until it is taken, for the next `seconds` (at most MaxPinSeconds): meanwhile it
        // is never evicted or expired, and not built yet it never goes stale. A pinned result is claimed: it no longer
        // counts as built ahead. Again on a pinned key: the pin is set anew; seconds <= 0 lets go of it, in every mode
        // (true when the key is known). False (nothing pinned) for an unknown key (Request first), with MaxPins results
        // pinned already, or with HeightmapLanes off (then what holds a result is IsTerrainReady's own PinSeconds).
        // Lane work the module dropped on its own is queued again first (see Revive).
        public static bool Pin(in Key key, float seconds)
        {
            if (!Current(key))
                return false;
            long now = Stopwatch.GetTimestamp();
            if (!(seconds > 0f))
                lock (s_lock)
                {
                    // Letting go needs no scheduler: a caller can always clean up, whatever was switched meanwhile. Work
                    // the module dropped (Find included) is not pinned again when it comes back.
                    Entry r = Find(key, now);
                    bool dropped = false;
                    if (s_dropped.Count > 0 && s_dropped.TryGetValue(key, out Dropped d))
                    {
                        s_dropped[key] = new Dropped(d.Lane, 0);
                        dropped = true;
                    }
                    if (r == null)
                        return dropped;
                    if (r.ApiPinnedUntil != 0)
                        Unpin(r);
                    return true;
                }
            if (!LanesActive(now))
                return false;
            lock (s_lock)
            {
                // Before Find, so it cannot drop the entry being pinned (stale queued work, a result past its TTL)
                // after it was found. Pins that ran out are only let go of there: at MaxPins it runs early, but only
                // once one has run out (s_pinsRunOut), so a refused Pin costs no scan of the table.
                if (now >= s_nextSweep || (Pins >= MaxPins && now >= s_pinsRunOut))
                    Sweep(now);
                Entry e = FindAsked(key, now);
                if (e == null)
                    return false;
                if (e.ApiPinnedUntil == 0 && Pins >= MaxPins)
                {
                    PinsRefused++;
                    return false;
                }
                PinUntil(e, now + Ticks(Math.Min(seconds, MaxPinSeconds)), now);
                return true;
            }
        }

        // Pins e through the lane API until `until` (room checked by the caller; under s_lock).
        static void PinUntil(Entry e, long until, long now)
        {
            if (e.ApiPinnedUntil == 0)
                Pins++;
            e.ApiPinnedUntil = until;
            if (until < s_pinsRunOut)
                s_pinsRunOut = until;
            Pin(e, now);
            e.LastAsked = now;
            if (e.State == State.Ready)
                e.LastTouched = now;
        }

        // Takes a finished result: it is then the caller's alone, and gone from the table (a Heightmap asking for the
        // same key later has it built again, as in the game). False when it is not finished; nothing is queued then
        // (Request does that) unless the module itself dropped it (see Revive), but queued work counts as polled. Off:
        // the game's IsTerrainReady and, when that says ready, its RequestTerrain.
        public static bool TryTake(in Key key, out HeightmapBuilder.HMBuildData data)
        {
            data = null;
            if (!Current(key))
                return false;
            long now = Stopwatch.GetTimestamp();
            if (!LanesActive(now))
            {
                if (!Poll(key) || s_requestTerrain == null)
                    return false;
                data = s_requestTerrain(HeightmapBuilder.instance, key.Center, key.Width, key.Scale, key.DistantLod, key.WorldGen);
                if (data != null && s_dropped.Count > 0)
                    lock (s_lock)
                        s_dropped.Remove(key);   // taken the game's way: nothing to queue again later
                return data != null;
            }
            lock (s_lock)
            {
                Entry e = FindAsked(key, now);
                if (e == null)
                    return false;
                if (e.State == State.Ready)
                {
                    data = Take(e);
                    return true;
                }
                Polled(e, now);
                return false;
            }
        }

        // Lets go of a key the lane API asked for, whatever its state: unpinned, and its queued work, build in hand or
        // finished result dropped at once, so it holds no lane slot (LaneHardCap) and no memory; for a caller that moved
        // on (a teleport, an area unloaded) instead of leaving it to expire. True when something was dropped (or work the
        // module had dropped forgotten); false for an unknown key or one only the game asked for (left alone). Asked
        // again, it is built again; a game caller that was told "ready" for the same key has it built again by
        // RequestTerrainSync. Works in every mode.
        public static bool Release(in Key key)
        {
            if (!Current(key))
                return false;
            lock (s_lock)
            {
                Entry e = Find(key, Stopwatch.GetTimestamp());
                bool forgot = s_dropped.Count > 0 && s_dropped.Remove(key);   // after Find, which may drop it too
                if (e == null || e.ApiLane < 0)
                    return forgot;
                Released++;
                Kill(e, true);
                return true;
            }
        }

        // Only keys for the generator in use: the game asks with WorldGenerator.instance, and builds for one whose river
        // tables Initialize has cleared would be wrong. And only finite numbers (a centre from an undefined anchor): a
        // NaN never equals itself, so the table could neither find such a key again nor drop it. The game never asks
        // for such a key.
        static bool Current(in Key key) =>
            (object)key.WorldGen != null && ReferenceEquals(key.WorldGen, WorldGenerator.instance) && key.Width > 0 &&
            Finite(key.Center.x) && Finite(key.Center.y) && Finite(key.Center.z) && Finite(key.Scale);

        static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);

        // Whether lane calls go through the scheduler now (main thread, outside s_lock).
        static bool LanesActive(long now)
        {
            if (!LanesFeature.On || !Feature.On || s_inactive || s_stepAside)
                return false;
            HeightmapBuilder hb = HeightmapBuilder.instance;
            // EnsureStarted may find a mod rewriting the entry points and step aside.
            return hb != null && EnsureStarted(hb, now) && !s_stepAside;
        }

        // The game's way: what a caller polling IsTerrainReady gets (through every patch on it, ours included).
        static bool Poll(in Key key)
        {
            if (s_disposed)
                return false;
            LaneFallbacks++;
            HeightmapBuilder hb = HeightmapBuilder.instance;
            return hb != null && hb.IsTerrainReady(key.Center, key.Width, key.Scale, key.DistantLod, key.WorldGen);
        }

        // ---------------------------------------------------------------- table (all under s_lock)

        // Exact key first; then the game's own approximate match, so a caller whose position went through a
        // transform still finds what a poll at the raw position queued (only reached for a key not in the table).
        static Entry Find(in Key key, long now)
        {
            if (!s_entries.TryGetValue(key, out Entry e) && (e = FindNear(key)) == null)
                return null;
            if (e.State == State.Ready && !BiomesSame(e))
            {
                StaleBiomes++;
                Drop(e);
                return null;
            }
            return e;
        }

        // Find for the lane API's IsReady, TryTake and Pin: lane work the module dropped on its own (just now as well,
        // built before the biome table) is queued again first (see Revive).
        static Entry FindAsked(in Key key, long now)
        {
            Entry e = Find(key, now);
            return s_dropped.Count > 0 && Revive(key, now) ? Find(key, now) : e;
        }

        // The approximate match without looking at every entry (an unknown key is asked for on every new request, and
        // by every IsReady/TryTake after a take): entries are filed by the cell of their centre's x and z, 1/1024 m
        // wide and centred on multiples of that (a zone centre is mid-cell). Vector3 == matches closer than 1e-5 m, so
        // a match lies in the key's cell or, within MatchSlack of the cell's edge, the next one: at most four cells
        // are looked in, usually one, and Matches decides as before.
        const double CellsPerMetre = 1024, MatchSlack = 2e-5;

        static Entry FindNear(in Key key)
        {
            double x = key.Center.x, z = key.Center.z;
            long x0 = Cell(x - MatchSlack), x1 = Cell(x + MatchSlack), z0 = Cell(z - MatchSlack), z1 = Cell(z + MatchSlack);
            return InCell(x0, z0, key) ?? (z1 != z0 ? InCell(x0, z1, key) : null) ??
                (x1 != x0 ? InCell(x1, z0, key) ?? (z1 != z0 ? InCell(x1, z1, key) : null) : null);
        }

        static Entry InCell(long cx, long cz, in Key key)
        {
            if (s_cells.TryGetValue(CellKey(cx, cz), out Entry c))
                for (; c != null; c = c.CellNext)
                    if (c.Key.Matches(key))
                        return c;
            return null;
        }

        // Defined for any position (one too far out for a long, or NaN, goes to cell 0: Matches still decides).
        static long Cell(double v)
        {
            double c = Math.Floor(v * CellsPerMetre + 0.5);
            return c > -9e18 && c < 9e18 ? (long)c : 0;
        }

        // Unique within 2000 km of the origin; past that, cells only share a chain.
        static long CellKey(long cx, long cz) => (cx << 32) ^ (cz & 0xffffffffL);

        static void Link(Entry e)
        {
            e.Cell = CellKey(Cell(e.Key.Center.x), Cell(e.Key.Center.z));
            s_cells.TryGetValue(e.Cell, out e.CellNext);
            s_cells[e.Cell] = e;
        }

        static void Unlink(Entry e)
        {
            if (!s_cells.TryGetValue(e.Cell, out Entry c))
                return;
            if (c == e)
            {
                if (e.CellNext == null)
                    s_cells.Remove(e.Cell);
                else
                    s_cells[e.Cell] = e.CellNext;
            }
            else
            {
                for (; c.CellNext != null; c = c.CellNext)
                    if (c.CellNext == e)
                    {
                        c.CellNext = e.CellNext;
                        break;
                    }
            }
            e.CellNext = null;
        }

        // The corner biomes of a result come from the world's biome table, which a new world gets after its
        // generator (AltBiomeWorldData.VerifyBiomeData). A result built before that is not handed out afterwards:
        // the game could hand out either one depending on its eviction timing; we always hand out the current one.
        static bool BiomesSame(Entry e)
        {
            AltBiomeWorldData now = e.Key.WorldGen.m_world?.m_biomeData;
            return now == e.Biomes && (now != null && now.IsReady) == e.BiomesReady;
        }

        static Entry Add(in Key key, long now)
        {
            var e = new Entry { Key = key, LastAsked = now, LastTouched = now };
            s_entries[key] = e;
            Link(e);
            s_count = s_entries.Count;
            return e;
        }

        static void SetState(Entry e, State s)
        {
            bool leftReady = e.State == State.Ready;
            switch (e.State)
            {
                case State.Queued: QueueLength--; break;
                case State.Building: Building--; break;
                case State.Ready:
                    Ready--;
                    if (e.ApiLane > (int)Lane.Player)
                        LanesReady--;
                    if (!e.Reported)
                        CountAhead(e, -1);
                    break;
            }
            e.State = s;
            switch (s)
            {
                case State.Queued: QueueLength++; break;
                case State.Building: Building++; break;
                case State.Ready:
                    Ready++;
                    if (e.ApiLane > (int)Lane.Player)
                        LanesReady++;
                    if (!e.Reported)
                        CountAhead(e, 1);
                    break;
            }
            if (leftReady)
                Room();
        }

        // A finished result nobody was told about yet is built ahead: of the game's callers (Ahead), or of the lane it
        // was requested on.
        static void CountAhead(Entry e, int n)
        {
            if (e.ApiLane < 0)
                Ahead += n;
            else
            {
                LaneAhead[e.ApiLane] += n;
                LanesAhead += n;
            }
        }

        // Reported ready (or asked for synchronously, or pinned through the lane API): kept PinSeconds for the caller,
        // who takes it next, and no longer counted as built ahead.
        static void Pin(Entry e, long now)
        {
            e.PinnedUntil = now + s_pinTicks;
            if (e.Reported)
                return;
            e.Reported = true;
            if (e.State == State.Ready)
            {
                CountAhead(e, -1);
                Room();
            }
        }

        static void Unpin(Entry e)
        {
            e.ApiPinnedUntil = 0;
            Pins--;
        }

        // Requested on lane l: it belongs to the fastest lane it was requested on. A finished result nobody was told
        // about yet moves to that lane's window. True when the lane changed.
        static bool ToLane(Entry e, int l, long now)
        {
            if (e.ApiLane >= 0 && e.ApiLane <= l)
                return false;
            bool ready = e.State == State.Ready, ahead = ready && !e.Reported;
            if (ahead)
                CountAhead(e, -1);
            if (ready && e.ApiLane > (int)Lane.Player)
                LanesReady--;
            if (e.ApiLane < 0)
                e.ApiAsked = now;
            e.ApiLane = l;
            if (ready && l > (int)Lane.Player)
                LanesReady++;
            if (ahead)
                CountAhead(e, 1);
            if (ready)
                Room();
            return true;
        }

        // Asked about through the lane API without being queued anew: kept from going stale and, requested on a lane,
        // polled again (see NextWanted).
        static void Polled(Entry e, long now)
        {
            e.LastAsked = now;
            if (e.State == State.Queued && e.ApiLane >= 0)
                Enqueue(e, s_laneWanted[e.ApiLane]);
        }

        // A worker that found only held-back work kept its permit back (s_owed): one goes out again for each result that
        // leaves the build-ahead window, so a worker builds the next item in its place.
        static void Room()
        {
            if (s_owed > 0 && Ready < HardCap && (Ahead < SoftCap || s_lanes[Wanted].Count > 0 || LaneRoom()))
                WakeOne();
        }

        // Held-back far-lane work a worker could take now: polled again, or with room in its window. False with no lane
        // work queued, so the game's work is woken for exactly as before.
        static bool LaneRoom()
        {
            int ahead = 0;
            for (int l = (int)Lane.FarCore; l < LaneCount; l++)
            {
                // Held back here, and so on every slower lane; FarCore's polled work only by the table (CoreWantedRoom).
                if (LanesReady >= LaneHeldCap(l))
                    return l == (int)Lane.FarCore && s_lanes[CoreW].Count > 0 && CoreWantedRoom();
                ahead += LaneAhead[l];
                if (s_lanes[s_laneWanted[l]].Count > 0 || (ahead < LaneSoftCap && s_lanes[s_laneQueue[l]].Count > 0))
                    return true;
            }
            return false;
        }

        static void WakeOne()
        {
            if (s_owed == 0)
                return;
            s_owed--;
            s_signal.Release();
        }

        // A new request gets one semaphore permit. Moving a queued one to a faster lane only adds another queue
        // reference; whichever is dequeued first takes it, the others are skipped (the entry is no longer Queued). The
        // move may make it takeable while the workers hold back (polled again, Near, Urgent): one is woken for it.
        static void Enqueue(Entry e, int lane)
        {
            bool fresh = e.State != State.Queued;
            if (!fresh && lane >= e.Lane)
                return;
            if (fresh)
                SetState(e, State.Queued);
            e.Lane = lane;
            e.Refs |= 1 << lane;
            s_lanes[lane].Enqueue(e);
            if (fresh)
                s_signal.Release();
            else
                WakeOne();
        }

        // Urgent and Near always go first. Then what callers are polling right now (Wanted), then the rest in order
        // (Normal), but the rest only while fewer than SoftCap finished results wait for a caller to ask for them: a
        // caller that polls in order (a far area's zones, one a frame) is kept a window ahead and no further, instead of
        // the workers racing through the whole backlog and the table evicting what that caller takes next. The Wanted
        // lane is what keeps the others (the player's distant terrain, ghost zones, another area waiting for its next
        // zone) from waiting behind that window.
        // The lane API's far lanes go between Wanted and Normal (Prefetch after Normal), strictly in order, each the same
        // way: its work polled again first (past the window, as Wanted), then the rest in order while its window has
        // room. A lane's window counts its own unreported results and those of the faster far lanes, so whatever a
        // slower lane left unclaimed never holds a faster lane back, while all of them together stay within
        // 4 x LaneSoftCap. And a lane does not go while the far lanes' callers hold its share of LaneHardCap in finished
        // results untaken (see LaneHeldCap), again lower for the slower lanes; FarCore's polled work goes past its share
        // while the table has room (see CoreWantedRoom).
        static Entry DequeueNext(long now)
        {
            Entry e = Next(s_lanes[Urgent], now) ?? Next(s_lanes[Near], now);
            if (e != null || Ready >= HardCap)
                return e;
            if ((e = NextWanted(Wanted, now)) != null)
            {
                WantedBuilds++;
                return e;
            }
            int ahead = 0;
            if ((e = NextOnLane(Lane.FarCore, now, ref ahead)) != null || (e = NextOnLane(Lane.FarVisual, now, ref ahead)) != null ||
                (e = NextOnLane(Lane.FarTerrain, now, ref ahead)) != null)
                return e;
            if (Ahead < SoftCap && (e = Next(s_lanes[Normal], now)) != null)
                return e;
            return NextOnLane(Lane.Prefetch, now, ref ahead);
        }

        static Entry NextOnLane(Lane lane, long now, ref int ahead)
        {
            int l = (int)lane;
            ahead += LaneAhead[l];
            if (LanesReady >= LaneHeldCap(l))
                return lane == Lane.FarCore && CoreWantedRoom() ? NextWanted(CoreW, now) : null;
            Entry e = NextWanted(s_laneWanted[l], now);
            return e != null || ahead >= LaneSoftCap ? e : Next(s_lanes[s_laneQueue[l]], now);
        }

        // Work polled again: what was polled within WantedSeconds goes now. The rest is not polled any more (a caller
        // polling in order found an earlier item ready, and stopped there): it waits at the fastest place it still has,
        // where its first reference is (Normal, or its lane's queue).
        static Entry NextWanted(int q, long now)
        {
            Queue<Entry> wanted = s_lanes[q];
            while (wanted.Count > 0)
            {
                Entry e = wanted.Dequeue();
                if (e.State != State.Queued)
                    continue;
                if (now - e.LastAsked <= s_wantedTicks)
                    return e;
                e.Refs &= ~(1 << q);
                if (e.Refs == 0)
                {
                    // Not reached (a first reference never goes to a polled-again queue); kept queued all the same.
                    int back = e.ApiLane >= 0 ? s_laneQueue[e.ApiLane] : Normal;
                    e.Refs = 1 << back;
                    s_lanes[back].Enqueue(e);
                }
                if (e.Lane == q)
                    for (e.Lane = 0; (e.Refs & (1 << e.Lane)) == 0; e.Lane++) { }
            }
            return null;
        }

        static Entry Next(Queue<Entry> q, long now)
        {
            while (q.Count > 0)
            {
                Entry e = q.Dequeue();
                if (e.State != State.Queued)
                    continue;
                // Pinned work waits for its turn however long it takes.
                if (e.Lane != Urgent && now - e.LastAsked > s_staleQueued && e.ApiPinnedUntil <= now)
                {
                    DroppedStale++;
                    Kill(e);
                    continue;
                }
                return e;
            }
            return null;
        }

        static HeightmapBuilder.HMBuildData Take(Entry e)
        {
            HeightmapBuilder.HMBuildData data = e.Data;
            Taken++;
            if (e.ApiLane >= 0)
                LaneTaken[e.ApiLane]++;
            if (s_dropped.Count > 0)
                s_dropped.Remove(e.Key);   // dropped once, asked for again by the game meanwhile, and now taken
            Kill(e, true);
            return data;
        }

        static void Complete(Entry e, HeightmapBuilder.HMBuildData data, AltBiomeWorldData biomes, bool biomesReady, long now)
        {
            e.Data = data;
            e.Biomes = biomes;
            e.BiomesReady = biomesReady;
            e.LastTouched = now;
            if (e.ApiLane >= 0)
            {
                int l = e.ApiLane;
                double ms = (now - e.ApiAsked) * s_msPerTick;
                LaneBuilt[l]++;
                LaneTotalWaitMs[l] += ms;
                if (ms > LaneMaxWaitMs[l])
                    LaneMaxWaitMs[l] = ms;
            }
            SetState(e, State.Ready);
            e.Done?.Set();
            if (Ready > HardCap)
                Evict(now);
        }

        // taken: handed out, or let go of by Release (not counted as expired).
        static void Kill(Entry e, bool taken = false)
        {
            if (e.State == State.Dead)
                return;
            if (s_entries.TryGetValue(e.Key, out Entry cur) && cur == e)
                s_entries.Remove(e.Key);
            Unlink(e);   // always: FindNear never finds a dead entry
            s_count = s_entries.Count;
            SetState(e, State.Dead);
            e.Data = null;
            e.Done?.Set();
            if (e.ApiLane >= 0 && !taken)
                LaneExpired[e.ApiLane]++;
            if (e.ApiPinnedUntil != 0)
                Unpin(e);
        }

        // Killed by the module on its own while its caller may still wait for it (see s_dropped): lane work is
        // remembered with its lane and pin, and comes back when asked about (Revive). Everything else is just killed.
        static void Drop(Entry e)
        {
            if (e.State == State.Dead)
                return;
            if (e.ApiLane >= 0)
            {
                if (s_dropped.Count >= MaxDropped)
                    s_dropped.Clear();
                s_dropped[e.Key] = new Dropped(e.ApiLane, e.ApiPinnedUntil);
            }
            Kill(e);
        }

        // A pin made through the lane API let go of by the module (switched off): a finished result stays as one
        // reported ready just now (PinSeconds, then ReadyTtlSeconds unasked), takeable as any reported one.
        static void LetGo(Entry e, long now)
        {
            Unpin(e);
            if (e.State == State.Ready)
                Pin(e, now);
        }

        // Above HardCap only. Workers stop building ahead long before (see DequeueNext), so this is a caller that is
        // told "ready" over and over and never takes: reported results go first, least recently reported first. Only
        // then unreported ones, newest first, since a caller polling in order reaches those last: the game's, then the
        // lanes', slowest lane first. Never one pinned through the lane API: there are at most MaxPins of those, so with
        // more than HardCap results there is always another one. The game kept 16 and evicted whatever was oldest,
        // reported or not.
        static void Evict(long now)
        {
            while (Ready > HardCap)
            {
                Entry oldestReported = null, newestAhead = null, laneAhead = null;
                foreach (Entry c in s_entries.Values)
                {
                    if (c.State != State.Ready || c.ApiPinnedUntil > now)
                        continue;
                    if (c.Reported)
                    {
                        if (oldestReported == null || c.LastTouched < oldestReported.LastTouched)
                            oldestReported = c;
                    }
                    else if (c.ApiLane < 0)
                    {
                        if (newestAhead == null || c.LastTouched > newestAhead.LastTouched)
                            newestAhead = c;
                    }
                    else if (laneAhead == null || c.ApiLane > laneAhead.ApiLane ||
                        (c.ApiLane == laneAhead.ApiLane && c.LastTouched > laneAhead.LastTouched))
                        laneAhead = c;
                }
                Entry victim = oldestReported ?? newestAhead ?? laneAhead;
                if (victim == null)
                    return;
                Evicted++;
                Drop(victim);
            }
        }

        // Once a second: finished results unasked for ReadyTtlSeconds, and queued work nobody asks for any more; neither
        // while pinned through the lane API (pins that ran out are let go of here).
        static void Sweep(long now)
        {
            s_nextSweep = now + Stopwatch.Frequency;
            s_temp.Clear();
            long runOut = long.MaxValue;
            foreach (Entry e in s_entries.Values)
            {
                if (e.ApiPinnedUntil != 0 && e.ApiPinnedUntil <= now)
                    Unpin(e);
                if (e.ApiPinnedUntil != 0)
                {
                    if (e.ApiPinnedUntil < runOut)
                        runOut = e.ApiPinnedUntil;
                }
                else if (e.State == State.Ready ? e.PinnedUntil <= now && now - e.LastTouched > s_readyTtl
                    : e.State == State.Queued && e.Lane != Urgent && now - e.LastAsked > s_staleQueued)
                    s_temp.Add(e);
            }
            s_pinsRunOut = runOut;
            foreach (Entry e in s_temp)
            {
                if (e.State == State.Ready)
                    Expired++;
                else
                    DroppedStale++;
                Kill(e);
            }
            s_temp.Clear();
        }

        static void CountBuild(long ticks)
        {
            double ms = ticks * s_msPerTick;
            TotalBuildMs += ms;
            if (ms > MaxBuildMs)
                MaxBuildMs = ms;
        }

        // ---------------------------------------------------------------- building

        static HeightmapBuilder.HMBuildData BuildData(in Key key, out AltBiomeWorldData biomes, out bool biomesReady)
        {
            biomes = key.WorldGen.m_world?.m_biomeData;
            biomesReady = biomes != null && biomes.IsReady;
            var data = new HeightmapBuilder.HMBuildData(key.Center, key.Width, key.Scale, key.DistantLod, key.WorldGen);
            s_build(s_builder, data);
            return data;
        }

        // False when the workers could not start (the caller then goes the game's way). Main thread, outside s_lock.
        static bool EnsureStarted(HeightmapBuilder hb, long now)
        {
            if (s_threads != null)
            {
                if (s_pendingLog != null)
                    FlushLog();
                // Not from inside RequestTerrainSync: it already looked, and dropping results under it only costs a rebuild.
                if (now >= s_nextPatchCheck && !t_inSync)
                    LookAtOtherMods(now);
                return true;
            }
            try
            {
                s_builder = hb;
                LookAtOtherMods(now);
                int n = Math.Max(1, Math.Min(6, Environment.ProcessorCount - 2));
                s_current = new Entry[n];
                var threads = new Thread[n];
                for (int i = 0; i < n; i++)
                {
                    threads[i] = new Thread(Worker) { IsBackground = true, Name = "RealPortal terrain builder " + i };
                    threads[i].Start(i);
                }
                s_threads = threads;
                Workers = n;
                ApplyWorkerLimit();
                EnginePlugin.Log?.LogInfo($"[HeightmapBuilder] {n} worker threads, {ActiveWorkers} building" +
                    (s_riverLocal ? "" : ", shared river cache"));
                return true;
            }
            catch (Exception e)
            {
                // Nothing is ours yet: the game's builder carries on for the rest of the session.
                s_inactive = true;
                EnginePlugin.Log?.LogError("[HeightmapBuilder] could not start, the game's builder is used: " + e);
                return false;
            }
        }

        static void ApplyWorkerLimit()
        {
            if (s_threads == null)
                return;
            // Parallel also means synchronous requests are built on the calling thread (even with one worker).
            bool parallel = ParallelFeature.On && TreeSafe;
            // Reset the gate before lowering the limit (and raise the limit before setting it), so a parked worker
            // never finds itself both over the limit and let through.
            if (parallel)
            {
                s_limit = s_threads.Length;
                s_inline = true;
                s_gate.Set();
            }
            else
            {
                s_gate.Reset();
                s_inline = false;
                s_limit = 1;
            }
            ActiveWorkers = s_limit;
        }

        static void Worker(object arg)
        {
            int index = (int)arg;
            t_worker = true;
            for (;;)
            {
                s_signal.Wait();
                if (s_stop)
                    return;
                if (index >= s_limit)
                {
                    // One-worker mode: give the permit back for worker 0 and park until parallel builds are back.
                    s_signal.Release();
                    s_gate.Wait();
                    continue;
                }
                Entry e = null;
                try
                {
                    lock (s_lock)
                    {
                        e = DequeueNext(Stopwatch.GetTimestamp());
                        if (e == null)
                        {
                            // Only held-back work is left (see DequeueNext): its permit is kept for when there is room
                            // (Room, Enqueue). Permits beyond the queued count were for work since dropped or built inline.
                            if (QueueLength > s_owed)
                            {
                                s_owed++;
                                HeldBack++;
                            }
                            continue;
                        }
                        SetState(e, State.Building);
                        s_current[index] = e;
                    }
                    HeightmapBuilder.HMBuildData data = null;
                    AltBiomeWorldData biomes = null;
                    bool biomesReady = false;
                    Exception error = null;
                    t_riverGen = null;   // the river cache lives for one build (see GetRiverWeightPrefix)
                    long t0 = Stopwatch.GetTimestamp();
                    try
                    {
                        data = BuildData(e.Key, out biomes, out biomesReady);
                    }
                    catch (Exception ex)
                    {
                        error = ex;
                    }
                    long t1 = Stopwatch.GetTimestamp();
                    long errors = 0;
                    lock (s_lock)
                    {
                        s_current[index] = null;
                        if (s_idleWaiters > 0)
                            Monitor.PulseAll(s_lock);
                        Builds++;
                        CountBuild(t1 - t0);
                        if (error != null)
                        {
                            errors = ++Errors;
                            if (e.State == State.Building)
                                Drop(e);
                        }
                        else if (e.State == State.Building)
                            Complete(e, data, biomes, biomesReady, t1);
                        else
                            Superseded++;   // dropped (world change) or built meanwhile by a waiter that gave up
                        // This build held a place in the build-ahead window. Its result did not take it up when it was
                        // already asked for (a synchronous request waited for it) or was dropped: pass the place on.
                        Room();
                    }
                    // Logged from the main thread; a generator that keeps failing is only counted after a few.
                    if (error != null && errors <= 5)
                        Interlocked.CompareExchange(ref s_pendingLog, $"terrain build at {e.Key.Center} failed: {error}", null);
                }
                catch (Exception ex)
                {
                    // Keep the worker alive whatever happens; whoever waits for this item is woken.
                    lock (s_lock)
                    {
                        s_current[index] = null;
                        if (s_idleWaiters > 0)
                            Monitor.PulseAll(s_lock);
                        if (e != null && e.State == State.Building)
                            Drop(e);
                        Room();
                    }
                    Interlocked.CompareExchange(ref s_pendingLog, "terrain worker: " + ex, null);
                }
            }
        }

        static void FlushLog()
        {
            string msg = Interlocked.Exchange(ref s_pendingLog, null);
            if (msg != null)
                EnginePlugin.Log?.LogWarning("[HeightmapBuilder] " + msg);
        }

        // ---------------------------------------------------------------- river cache

        // The game caches the last river grid cell in one shared field pair behind a ReaderWriterLockSlim. Correct with
        // several builders, but they work on different zones and overwrite it on nearly every sample, taking the write
        // lock each time. On a worker the same lookup goes through a per-thread copy of that cache: the same table
        // (m_riverPoints, filled before the generator is published) and the same GetWeight, so the same result.
        // The main thread and the game's own builder thread keep the game's code.
        static bool GetRiverWeightPrefix(WorldGenerator __instance, float wx, float wy, ref float weight, ref float width, bool __runOriginal)
        {
            if (!t_worker || !s_riverLocal || !__runOriginal)
                return true;
            Vector2i grid = __instance.GetRiverGrid(wx, wy);
            if (t_riverGen != __instance || grid != t_riverGrid)
            {
                s_riverPoints(__instance).TryGetValue(grid, out WorldGenerator.RiverPoint[] points);
                t_riverGen = __instance;
                t_riverGrid = grid;
                t_riverPoints = points;
            }
            if (t_riverPoints != null)
                s_getWeight(__instance, t_riverPoints, wx, wy, out weight, out width);
            else
            {
                weight = 0f;
                width = 0f;
            }
            return false;
        }

        // ---------------------------------------------------------------- other mods

        // Build's call tree, by method name on WorldGenerator (every overload, to be safe).
        static readonly HashSet<string> s_treeMethods = new HashSet<string>
        {
            "GetBiomeSector", "GetBiome", "GetBiomeHeight", "GetBaseHeight", "AddRivers", "GetRiverWeight", "GetRiverGrid",
            "GetWeight", "GetMarshHeight", "GetMeadowsHeight", "GetForestHeight", "GetMistlandsHeight", "GetPlainsHeight",
            "GetMenuHeight", "GetAshlandsHeight", "GetSnowMountainHeight", "BaseHeightTilt", "GetOceanHeight",
            "GetDeepNorthHeight", "CreateAshlandsGap", "CreateDeepNorthGap", "IsAshlands", "IsDeepnorth", "WorldAngle",
            "GetHeightMultiplier",
        };

        // Patches known to be safe on several threads at once. OttoBifrost (1.4.2) only times Build into Interlocked
        // counters.
        static readonly HashSet<string> s_safeOwners = new HashSet<string> { "potto007.OttoBifrost" };

        // Looked at when the workers start, when a world starts (WorldGenerator.Initialize, and again on the first request
        // after it, past ZoneSystem.Start and Game.Start), and every PatchCheckSeconds while requests come in, so patches
        // other mods apply late (a server's config sync, a world-generation mod re-patching) are seen too. Main thread,
        // outside s_lock. One pass over Harmony's list of patched methods: ~20 us even with thousands of them.
        //  - A world-generation mod's patches were written for the game's one builder thread: with any of them in Build's
        //    call tree, builds stay on one worker and synchronous requests wait for it, so no two builds run at once (as
        //    in the game). Judged per patch method, so other engine modules (same Harmony id) count as well. A foreign
        //    patch on the river lookup also keeps the game's river cache (our prefix would skip its transpiler).
        //  - A transpiler or IL manipulator on IsTerrainReady/RequestTerrain/RequestTerrainSync changes code our prefixes
        //    skip: the module steps aside as if switched off, so that code runs.
        //  - A prefix another mod adds there that runs after ours sees a request already answered; harmless if it checks
        //    __runOriginal, which we cannot tell, so it is only logged.
        static void LookAtOtherMods(long now)
        {
            s_nextPatchCheck = now + s_patchCheckTicks;
            var tree = new List<string>();
            var rewrites = new List<string>();
            var after = new List<string>();
            bool river = ScanPatches(tree, rewrites, after);

            string treeSig = string.Join(", ", tree) + (river ? " [river]" : "");
            if (treeSig != s_treeSig)
            {
                bool first = s_treeSig == null, wasSafe = TreeSafe, wasRiverLocal = s_riverLocal;
                s_treeSig = treeSig;
                // Before anything else: from the next river lookup on, workers go through the game's (patched) code.
                s_riverLocal = !river;
                TreeSafe = tree.Count == 0;
                // Results built while the patches were different may have missed them, or run them on several threads:
                // finished ones and builds in hand are dropped (asked again, they are rebuilt); queued work stays.
                if (!first)
                    DropBuilt();
                ApplyWorkerLimit();
                if (!TreeSafe)
                    EnginePlugin.Log?.LogWarning("[HeightmapBuilder] other mods patch terrain generation, building on one thread: " + string.Join(", ", tree));
                else if (!wasSafe)
                    EnginePlugin.Log?.LogInfo($"[HeightmapBuilder] no other mod patches terrain generation any more, building on {ActiveWorkers} threads");
                if (!first && river && wasRiverLocal)
                    EnginePlugin.Log?.LogInfo("[HeightmapBuilder] another mod patches the river lookup, workers use the game's river cache");
            }

            string entrySig = string.Join(", ", rewrites);
            if (entrySig != s_entrySig)
            {
                s_entrySig = entrySig;
                if (rewrites.Count > 0)
                {
                    EnginePlugin.Log?.LogWarning("[HeightmapBuilder] other mods rewrite the terrain requests, the game's builder is used: " + entrySig);
                    if (!s_stepAside)
                    {
                        s_stepAside = true;
                        if (Feature.On)
                            Deactivate();
                    }
                }
                else if (s_stepAside)
                {
                    s_stepAside = false;
                    EnginePlugin.Log?.LogInfo("[HeightmapBuilder] no other mod rewrites the terrain requests any more, building them here again");
                }
            }

            string afterSig = string.Join(", ", after);
            if (afterSig != s_afterSig)
            {
                s_afterSig = afterSig;
                if (after.Count > 0)
                    EnginePlugin.Log?.LogWarning("[HeightmapBuilder] prefixes of other mods run after ours on the terrain requests " +
                        "(they see the request already answered, and override it unless they check __runOriginal): " + afterSig);
            }
        }

        // Returns whether another mod patches the river lookup.
        static bool ScanPatches(List<string> tree, List<string> rewrites, List<string> after)
        {
            bool river = false;
            try
            {
                foreach (MethodBase m in Harmony.GetAllPatchedMethods())
                {
                    Type t = m.DeclaringType;
                    bool entry = t == typeof(HeightmapBuilder) &&
                        (m.Name == nameof(HeightmapBuilder.IsTerrainReady) || m.Name == "RequestTerrain" || m.Name == nameof(HeightmapBuilder.RequestTerrainSync));
                    bool inTree = m == s_buildMethod || t == typeof(FastNoise) || t == typeof(DUtils) ||
                        (t == typeof(WorldGenerator) && s_treeMethods.Contains(m.Name)) ||
                        (t == typeof(AltBiomeWorldData) && m.Name == nameof(AltBiomeWorldData.WorldSpaceToMapSpace));
                    if (!inTree && !entry)
                        continue;
                    Patches info = Harmony.GetPatchInfo(m);
                    if (info == null)
                        continue;
                    if (entry)
                    {
                        ScanEntry(m, info, rewrites, after);
                        continue;
                    }
                    var all = new List<HarmonyLib.Patch>();
                    all.AddRange(info.Prefixes);
                    all.AddRange(info.Postfixes);
                    all.AddRange(info.Transpilers);
                    all.AddRange(info.Finalizers);
                    all.AddRange(info.ILManipulators);
                    foreach (HarmonyLib.Patch p in all)
                    {
                        if (Ours(p))
                            continue;
                        if (t == typeof(WorldGenerator) && (m.Name == "GetRiverWeight" || m.Name == "GetWeight" || m.Name == "GetRiverGrid"))
                            river = true;
                        if (!s_safeOwners.Contains(p.owner))
                            tree.Add(Describe(p, m));
                    }
                }
            }
            catch (Exception e)
            {
                // Unknown: the safe side (one worker, the game's river cache), but no stepping aside on a guess.
                tree.Add("patch scan failed: " + e.Message);
                river = true;
            }
            return river;
        }

        static void ScanEntry(MethodBase m, Patches info, List<string> rewrites, List<string> after)
        {
            foreach (HarmonyLib.Patch p in info.Transpilers)
                if (!Ours(p))
                    rewrites.Add(Describe(p, m));
            foreach (HarmonyLib.Patch p in info.ILManipulators)
                if (!Ours(p))
                    rewrites.Add(Describe(p, m));
            if (info.Prefixes.Count < 2)
                return;
            // Harmony's own order (priority, then registration, then before/after constraints).
            var prefixes = new HarmonyLib.Patch[info.Prefixes.Count];
            info.Prefixes.CopyTo(prefixes, 0);
            List<MethodInfo> sorted;
            try
            {
                sorted = PatchProcessor.GetSortedPatchMethods(m, prefixes);
            }
            catch (Exception e)
            {
                after.Add("prefix order on " + m.Name + " unknown: " + e.Message);
                return;
            }
            bool past = false;
            foreach (MethodInfo pm in sorted)
            {
                if (pm.DeclaringType == typeof(HeightmapBuilderModule))
                    past = true;
                else if (past)
                    foreach (HarmonyLib.Patch p in prefixes)
                        if (p.PatchMethod == pm)
                            after.Add(Describe(p, m));
            }
        }

        static bool Ours(HarmonyLib.Patch p) => p.PatchMethod?.DeclaringType == typeof(HeightmapBuilderModule);

        static string Describe(HarmonyLib.Patch p, MethodBase m) =>
            p.owner + " (" + p.PatchMethod?.DeclaringType?.Name + "." + p.PatchMethod?.Name + ") -> " + m.DeclaringType?.Name + "." + m.Name;

        // Results built while other mods' patches were different: finished ones and builds in hand (whose worker then
        // discards its result) go; a caller told "ready" gets it rebuilt by RequestTerrainSync. Queued work stays.
        static void DropBuilt()
        {
            lock (s_lock)
            {
                s_temp.Clear();
                foreach (Entry e in s_entries.Values)
                    if (e.State == State.Ready || e.State == State.Building)
                        s_temp.Add(e);
                foreach (Entry e in s_temp)
                    Drop(e);
                s_temp.Clear();
            }
        }

        // ---------------------------------------------------------------- lifecycle

        static void OnSwitched(bool on)
        {
            if (!on)
                Deactivate();
        }

        // Off (or stepping aside): queued work is dropped (the game's builder takes new requests), pinned or not, and so
        // are finished results nobody was told about. Results reported ready stay takeable until taken or expired, and
        // so do pinned ones, as reported just now: their pins are let go of, so nothing of ours is held longer than a
        // reported result of the game's. Builds in hand finish into the table. Dropped lane work comes back when its
        // caller asks again after switching on (see Revive).
        static void Deactivate()
        {
            long now = Stopwatch.GetTimestamp();
            lock (s_lock)
            {
                s_temp.Clear();
                foreach (Entry e in s_entries.Values)
                {
                    if (e.State == State.Queued || (e.State == State.Ready && e.PinnedUntil <= now && e.ApiPinnedUntil <= now))
                        s_temp.Add(e);
                    else if (e.ApiPinnedUntil != 0)
                        LetGo(e, now);
                }
                foreach (Entry e in s_temp)
                    Drop(e);
                s_temp.Clear();
                foreach (Queue<Entry> q in s_lanes)
                    q.Clear();
                s_owed = 0;   // nothing queued is left to keep permits for
            }
        }

        // HeightmapLanes off: lane calls go the game's way from now on, so nothing they asked for before keeps a lane's
        // priority or a pin (an A/B's "off" half is the game's schedule at once). Queued lane work is dropped (asked
        // again through IsTerrainReady, it is queued as the game's; asked again through the lane API after switching on,
        // it is back on its lane, see Revive), except what a synchronous request waits for; pins are let go of, and
        // finished results stay, takeable as reported ones until they expire. Builds in hand finish.
        static void OnLanesSwitched(bool on)
        {
            if (on)
                return;
            long now = Stopwatch.GetTimestamp();
            lock (s_lock)
            {
                s_temp.Clear();
                foreach (Entry e in s_entries.Values)
                {
                    if (e.State == State.Queued && e.ApiLane >= 0 && e.Lane != Urgent)
                        s_temp.Add(e);
                    else if (e.ApiPinnedUntil != 0)
                        LetGo(e, now);
                }
                foreach (Entry e in s_temp)
                    Drop(e);
                s_temp.Clear();
            }
        }

        // A new generator replaces the old one, and Initialize first clears the old one's river tables
        // (CleanCachedRiverData) -- which a worker may be reading right then (a Dictionary read during Clear can throw
        // or loop forever). Everything held was built for an old generator and can never be asked for again (requests
        // pass WorldGenerator.instance): drop it all, and let the builds in hand on the generator being cleared finish
        // first (one build at most, on the loading screen). Also the moment to look for late patches by other mods.
        static void InitializePrefix()
        {
            if (s_threads == null)
                return;
            WorldGenerator old = WorldGenerator.instance;
            lock (s_lock)
            {
                DropAll();
                if (old != null)
                {
                    long deadline = Stopwatch.GetTimestamp() + Ticks(5);
                    s_idleWaiters++;
                    try
                    {
                        while (BuildingWith(old))
                        {
                            int left = (int)((deadline - Stopwatch.GetTimestamp()) * s_msPerTick);
                            if (left <= 0 || !Monitor.Wait(s_lock, left))
                                break;
                        }
                    }
                    finally
                    {
                        s_idleWaiters--;
                    }
                }
            }
            LookAtOtherMods(Stopwatch.GetTimestamp());
            ApplyWorkerLimit();
            // And again on the first request of the new world: on a server the generator comes first (ZNet.Awake), and
            // mods that patch at ZoneSystem.Start or Game.Start do so after it.
            s_nextPatchCheck = 0;
        }

        static bool BuildingWith(WorldGenerator gen)
        {
            foreach (Entry e in s_current)
                if (e != null && e.Key.WorldGen == gen)
                    return true;
            return false;
        }

        // The game quits (FejdStartup/Game.OnApplicationQuit): stop the workers after the build in hand. They are
        // background threads, so a stuck one cannot hold the process either.
        static void DisposePostfix()
        {
            s_disposed = true;
            s_inactive = true;
            Thread[] threads = s_threads;
            if (threads == null)
                return;
            s_stop = true;
            s_gate.Set();
            s_signal.Release(threads.Length);
            foreach (Thread t in threads)
                t.Join(200);
            lock (s_lock)
                DropAll();
        }

        // Everything held goes (world change, quit; under s_lock). The table and the pin count are then set back to
        // empty whatever was left over, so nothing a bug kept can outlive the world.
        static void DropAll()
        {
            s_temp.Clear();
            s_temp.AddRange(s_entries.Values);
            foreach (Entry e in s_temp)
                Kill(e);
            s_temp.Clear();
            s_entries.Clear();
            s_cells.Clear();
            s_dropped.Clear();   // keys of the old generator: Current refuses them anyway
            s_count = 0;
            foreach (Queue<Entry> q in s_lanes)
                q.Clear();
            s_owed = 0;
            Pins = 0;
            s_pinsRunOut = long.MaxValue;
        }
    }
}
