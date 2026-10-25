using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace RealPortal.Engine
{
    // Navmesh tile builds (E5 "FarNavmeshCap" in Docs/ARCHITECTURE-v2.md).
    //
    // How the game builds its navmesh (Pathfinding, this version):
    //  - Tiles are 32 x 32 m columns per agent type (key x, y, AgentType), made on demand by GetNavTile when someone
    //    pokes them: GetPath and HavePath poke the 3x3 tiles around both ends (PokeArea), FindValidPoint the one tile
    //    (PokePoint); a poke only sets m_pokeTime = Time.time. BaseAI (FindPath at most once a second per creature,
    //    HavePath), SpawnAbility and other mods' AI (Path of Valheiman) are the callers. A creature whose own tiles do
    //    not exist yet cannot snap to the navmesh: GetPath fails and BaseAI.MoveTo stops it.
    //  - Update: while no async build is running, every 0.1 s UpdatePathfinding = Buildtiles + TimeoutTiles, then
    //    DestroyQueuedNavmeshData.
    //  - Buildtiles: if a build exists, UpdateAsyncBuild finishes it once done (AddNavMeshData, RebuildLinks: two
    //    edges of 32 downward RaycastNonAlloc each) and returns. Otherwise it picks the tile with the largest
    //    m_pokeTime - m_buildTime above m_updateInterval (5 s), the first in dictionary order on a tie, BuildTile's it
    //    (CollectSources over a 32 x 6000 x 32 box, one or two passes, on the main thread, then
    //    UpdateNavMeshDataAsync) and stamps m_buildTime = Time.time. A never-built tile (m_buildTime -1000) outranks
    //    every rebuild; a tile that stays poked is rebuilt every 5 s or more. One build at a time and at least two
    //    ticks per build: about 5 builds a second at most, shared by every tile anyone asks for.
    //  - TimeoutTiles drops one tile per tick that nobody poked for m_tileTimeout (30 s).
    //  (m_queuedAreas is never used; there is no UpdateTile or GetLoadedNavmeshTile in this version.)
    // Far portal areas whose creatures run on this client (Real Portal's live far sides) poke their own tiles into that
    // same ranking. With a few of them the builder never catches up, the player's own tiles wait their turn behind
    // far ones, and every far build costs CollectSources and link raycasts on the main thread.
    //
    // What this does. A transpiler (Priority.Last) puts one call right after Buildtiles' selection loop,
    // tile = Choose(this, tile); the loop, BuildTile, the m_buildTime stamp, UpdateAsyncBuild and TimeoutTiles stay
    // the game's. Off, Choose hands back the game's pick. On, each due tile (the game's own test, m_pokeTime -
    // m_buildTime > m_updateInterval) is new (never built) or a rebuild, and of one of three places:
    //  - Near: the zone of the tile's centre is within NearZones (3) zones each way of the zone of ZNet's reference
    //    position (the local player, or what a Tracker sets). Creatures that run on this peer are owned by it, and the
    //    server hands out ownership only inside ZNetScene.InActiveArea of the peer's reference position (1 to 1.5
    //    zones from its zone's centre), whatever the simulation distance; the rest covers their 3x3 pokes (48 m) and
    //    short paths.
    //  - Far: outside that, and Real Portal's hooks place the tile in a far core: IsFarCore says so and FarClusterOf
    //    names its cluster (one per core). Capped: at most one build per FarClusterSeconds (2 s) per cluster. The cap
    //    needs both hooks: while either is unset (or resting after a throw) no tile is Far. (One cluster for every far
    //    tile would hold all live cores together to one build per 2 s, far worse for their creatures than the game; a
    //    fixed grid of the world would cut one core into several caps.)
    //  - Mid: any other tile outside; all of them while the hooks are unset. Never capped.
    // A tile is placed by where it lies, not by who asked for it (a poke only stamps the tile): the hooks must answer
    // for every tile a core's creatures ask for, the core grown by two zones each way (see IsFarCore). A hook that
    // answers for the core's own zones only leaves the tiles its creatures poke past the edge Mid: uncapped, and the
    // new ones built ahead of the player's rebuilds.
    // They are built in this order, within each rank by the game's own rule (largest value first, the first in
    // dictionary order on a tie):
    //   1. Near new;  2. outside new (Mid, and Far whose cluster may build);  3. Near rebuilds;
    //   4. outside rebuilds (Mid, and Far whose cluster may build).
    //  - Share: while due Near rebuilds keep the builder busy, the first outside rebuild (rank 4) still goes first every
    //    OutsideShareSeconds (4 s), so no outside tile that stays requested waits forever behind the player's rebuilds.
    //    No new tile is ever put behind it (new tiles outside wait only while the player's own new tiles are due,
    //    which the game too builds before any rebuild).
    // So the player's new tiles always come first and creatures outside get their first tiles right after them (the
    // game too builds new tiles before any rebuild; a far core gets at most one per 2 s), so they can move. The
    // player's rebuilds lose at most one build per 2 s per far cluster with work, one per 4 s to the share, and what
    // new Mid tiles take (as in the game).
    // Cheap where nothing is far: when the game's pick is a Near tile, it is kept without looking at the other tiles if
    // it is new, or if it is a rebuild and the share is not due (or had nothing to take a moment ago). A new
    // tile's value is its poke time + 1000 s, a rebuild's at most the time since its build, and a tile nobody pokes for
    // 30 s is dropped: so the game's pick is new whenever any new tile is due. A new pick is then the first largest Near
    // new tile; a rebuild pick means no new tile is due, and it is the first largest Near rebuild.
    //
    // Equivalence. The game's code still builds one tile at a time with its own BuildTile and stamps it; pokes,
    // timeouts, links and the removal queues are untouched, so which tiles exist and when they time out is the game's,
    // and a tile's navmesh is what BuildTile makes of the colliders standing when it is built: only that moment moves.
    // Every tile that stays requested is still built: as soon as nothing above it is due (a Far one once its cluster's
    // turn comes, its rebuilds after its new tiles, as in the game), and outside rebuilds through the share otherwise.
    // Nothing is sent over the network, no ZDO is written, ownership is not touched.
    // In the plain game (no far areas, Real Portal's hooks unset) creatures stand in the owned area and their pokes
    // reach a tile further, so every due tile is Near and the game's pick is kept. A path target more than about 3.5
    // zones from the player's zone (a HuntPlayer creature after another player up to 200 m away, in multiplayer) makes
    // Mid tiles: built after the player's tiles of the same kind, never capped.
    //
    // Deviations when tiles outside are due (deliberate, ARCHITECTURE-v2 §3.11 "AI containment"):
    //  - the player's tiles are built before outside ones of the same kind (new, rebuild), instead of interleaved by age;
    //  - while the player's rebuilds keep the builder busy, outside tiles are rebuilt once per 4 s between them all, so
    //    navmesh outside sees new pieces, doors and felled trees later; after a look at all tiles found no outside
    //    rebuild the share could take (due, and not in a capped far cluster), the share waits up to QuietSeconds (1 s)
    //    longer (no longer than until such a capped cluster may build again);
    //  - far tiles (only once Real Portal sets IsFarCore and FarClusterOf): a far cluster with N new tiles needs about
    //    N x 2 s for all of them, and its rebuilds wait as long (while its creatures keep asking for new tiles faster
    //    than one per 2 s, its navmesh does not see changes there at all); a far tile asked for briefly and never again
    //    can time out (30 s without a poke) before its cluster's turn, and is then never built (nothing asks for it any
    //    more by then); a 0.1 s tick in which every due tile is a capped far one builds nothing (the game would have
    //    built one).
    //
    // Default off (§5.3: a flag's default is set once its stage passes): Real Portal sets IsFarCore and FarClusterOf
    // (RealPortal.FarCores, PORTALS20-PLAN A12: a far area's 3x3-zone core grown by two zones each way, one cluster per
    // area, renewed through HooksChanged as areas come and go), but S7's gates (spawnfar, walkinto, tamed wolf follows,
    // Pathfinding.Update) and A12's A/B have not run with it. Real Portal's Window.Quality=Continuation turns it on
    // (preset 3, Docs\PARITY-PLAN.md P19), and since P19 its hooks answer only for far areas that sleep: an awake one
    // runs its creatures as a player standing there would, so its tiles rank Mid, uncapped, as a player's own would be
    // there (renewed through HooksChanged as areas fall asleep and wake). Switched on without both hooks (another build
    // of Real Portal, or none) it caps nothing: it only builds the player's tiles before outside ones of the same kind.
    //
    // Switching: there is no state beyond the clusters' last builds, the share clock, the hooks' kept answers and their
    // error marks, all cleared on every switch; a build in flight finishes in the game's code either way. Off: Choose
    // is one field read. On a dedicated server (no local player) the game's pick is always kept.
    //
    // Compatibility: Real Portal does not patch Pathfinding; Path of Valheiman only calls its public API. Another mod's
    // transpiler or IL manipulator on Buildtiles makes the module keep the game's pick, as switched off; lab patches
    // (valheim.realportal.lab*) do not count. That is looked at on the first pick after any patch or unpatch of
    // Buildtiles (Harmony runs InsertChoice again then), and every ForeignCheckFrames frames. If the selection's IL is
    // not the expected shape, nothing is inserted and the module does nothing (logged). Three exceptions inside the
    // selection keep the game's pick until the next switch (logged).
    [EngineModule]
    public static class FarNavmeshCapModule
    {
        public static readonly Feature Feature = Features.Register("FarNavmeshCap", false,
            "Navmesh tiles (Pathfinding.Buildtiles): the player's own new tiles first, then new tiles elsewhere, then the player's rebuilds, " +
            "then rebuilds elsewhere; tiles of Real Portal's far cores at most once per 2 s per core (only while Real Portal sets IsFarCore " +
            "and FarClusterOf, which it does for its sleeping far areas: the core grown by two zones; awake ones are not capped; without the hooks " +
            "only the order changes). Off by default in the engine, Real Portal's Window.Quality=Continuation turns it on. Off: the game's order.");

        // ---- Hooks for Real Portal (main thread; may be set or cleared at any time) ----

        // Whether a navmesh tile outside the player's area belongs to a far core (a place whose creatures Real Portal
        // runs): with FarClusterOf also set, those are capped. Asked with the tile's centre (x and z; y is the game's
        // 2500) for due tiles outside the player's area.
        // A tile is placed by where it lies, not by who poked it, so this must be true for every tile the core's
        // creatures ask for, not only for the core's own zones: the core grown by at least two zones each way (a 3x3-zone
        // core: the 7x7 zones around its centre zone). A creature's path pokes the 3x3 tiles around itself and around its
        // target, up to 48 m past each, and its random-move and flee targets lie some tens of metres away (BaseAI
        // m_randomMoveRange, m_fleeRange, 25 m by default). Answering for the core alone leaves the tiles its creatures
        // poke past the edge Mid: uncapped, and the new ones built ahead of the player's rebuilds.
        // The answer is kept per tile column (every agent type there) for HookCacheSeconds, or until the hook is replaced
        // or HooksChanged is called, so a few hundred calls per 5 s at worst. Never call into Pathfinding from it (its
        // tile table is being walked). Null: no tile is far, every tile outside the player's area is Mid (after the
        // player's tiles of the same kind, never capped). A tile inside the player's area is the player's whatever this
        // says. A hook that throws is taken as null until it is replaced, HooksChanged is called or HookRetrySeconds have
        // passed (HookErrors; logged once per hook).
        public static Func<Vector3, bool> IsFarCore;

        // The cap's cluster of a far tile: the index of the far core it belongs to, the same over the core's whole grown
        // area (where two grown areas overlap, either core's), so the cap holds per core. Asked, kept and guarded as
        // IsFarCore, for far tiles only. The cap needs it: null (or throwing) makes every tile Mid, as with IsFarCore
        // unset.
        public static Func<Vector3, int> FarClusterOf;

        // Real Portal calls this when its far cores change: the hooks are asked again (and retried if they threw).
        public static void HooksChanged()
        {
            s_hookGeneration++;
            s_brokenIsFar = null;
            s_brokenClusterOf = null;
        }

        // ---- Tuning (the lab may change these at run time) ----

        // At most one far build per cluster this often.
        public static float FarClusterSeconds = 2f;
        // While due Near rebuilds keep the builder busy, one outside rebuild gets a build this often.
        public static float OutsideShareSeconds = 4f;
        // Zones each way of the reference position's zone whose tiles are the player's (at least 1).
        public static int NearZones = 3;
        // How long the hooks' answers are kept (0: asked for every tile at every look).
        public static float HookCacheSeconds = 5f;

        // After a look at all tiles found no outside rebuild the share could take, the game's Near pick is kept this long
        // without looking.
        const float QuietSeconds = 1f;
        // A hook that threw is asked again after this long.
        const float HookRetrySeconds = 10f;
        const int ForeignCheckFrames = 1000;
        const int MaxErrors = 3;
        const string LabOwners = "valheim.realportal.lab";

        // ---- Lab counters (read by reflection; cumulative ones are cleared by ResetCounters) ----
        public static bool Patched;                         // the call is in Buildtiles
        public static long Picks;                           // selections while on (the game had a tile to build)
        public static long Scans, ScannedTiles;             // selections that looked at every tile, and the tiles seen
        public static double TotalScanUs, MaxScanUs;
        public static long NearBuilds, NearNewBuilds;       // Near tiles built (never built before)
        public static long NearRebuilds;                    // Near tiles rebuilt; how long past due (s) they were:
        public static double TotalNearOverdueS, MaxNearOverdueS;
        public static long MidBuilds, MidNewBuilds, FarBuilds, FarNewBuilds;
        public static long ShareBuilds;                     // outside rebuilds built ahead of due Near rebuilds
        public static long Overridden;                      // a different tile than the game's pick was built
        public static long Deferred;                        // nothing built: every due tile was a capped far one
        public static long CappedSeen;                      // capped far due tiles, summed over scans
        public static long HookCalls;                       // IsFarCore and FarClusterOf calls
        public static bool CapHooks;                        // the last scan: both hooks set and usable, so the cap held
        public static int TileCount, DueNew, DueNear, DueMid, DueFar, CappedFar, ActiveClusters, KeptAnswers;   // the last scan
        public static long HookErrors, Errors;

        public static bool Active => Patched && Feature.On && !s_aside && !s_foreign;

        public static void ResetCounters()
        {
            Picks = Scans = ScannedTiles = 0;
            TotalScanUs = MaxScanUs = 0;
            NearBuilds = NearNewBuilds = NearRebuilds = 0;
            TotalNearOverdueS = MaxNearOverdueS = 0;
            MidBuilds = MidNewBuilds = FarBuilds = FarNewBuilds = ShareBuilds = Overridden = Deferred = CappedSeen = 0;
            HookCalls = HookErrors = Errors = 0;
        }

        public static string Report()
        {
            string state = !Patched ? "not inserted" : !Feature.On ? "off" : s_foreign ? "stepped aside (another mod rewrites Buildtiles)"
                : s_aside ? "stepped aside (errors)" : "on";
            return $"far navmesh cap {state}: picks {Picks}, scans {Scans} ({(Scans > 0 ? TotalScanUs / Scans : 0):F1} us avg, max {MaxScanUs:F1}, " +
                $"{(Scans > 0 ? (double)ScannedTiles / Scans : 0):F0} tiles) | near {NearBuilds} (new {NearNewBuilds}, rebuilt {NearRebuilds} " +
                $"{(NearRebuilds > 0 ? TotalNearOverdueS / NearRebuilds : 0):F2} s past due avg, max {MaxNearOverdueS:F2}), mid {MidBuilds} (new {MidNewBuilds}), " +
                $"far {FarBuilds} (new {FarNewBuilds}), share {ShareBuilds}, overridden {Overridden}, deferred {Deferred}, capped seen {CappedSeen} | " +
                $"last scan: {(CapHooks ? "hooks set" : "hooks unset, no cap")}, tiles {TileCount}, due new {DueNew}, near {DueNear} mid {DueMid} " +
                $"far {DueFar} (capped {CappedFar}), clusters {ActiveClusters}, " +
                $"kept answers {KeptAnswers} | hook calls {HookCalls}, hook errors {HookErrors}, errors {Errors}";
        }

        // ---- State ----

        static MethodInfo s_buildtiles, s_buildTile, s_choose;
        static Type s_tileType;
        // Far clusters -> when their last build started (Time.time).
        static readonly Dictionary<long, float> s_clusterBuilt = new Dictionary<long, float>();
        static readonly List<long> s_expired = new List<long>();
        // When an outside tile was last rebuilt (the share's clock).
        static float s_lastOutsideRebuild = float.NegativeInfinity;
        static float s_quietUntil = float.NegativeInfinity;
        static int s_errors;
        static bool s_aside;
        static bool s_foreign;
        static int s_foreignCheckedAt = -1;

        // The hooks' answers per tile column, and what they were asked of.
        struct Answer
        {
            public bool Far;
            public long Cluster;
        }

        static readonly Dictionary<long, Answer> s_answers = new Dictionary<long, Answer>();
        static Func<Vector3, bool> s_answersIsFar;
        static Func<Vector3, int> s_answersClusterOf;
        static int s_answersGeneration;
        static float s_answersSince;
        static int s_hookGeneration;
        // A hook that threw, and when it last did.
        static Delegate s_brokenIsFar, s_brokenClusterOf;
        static float s_brokenIsFarAt, s_brokenClusterOfAt;

        static void Install(Harmony harmony)
        {
            Type pathfinding = typeof(Pathfinding);
            // Everything is looked up and bound before anything is patched: a game update that renamed one of them
            // leaves the module out whole.
            s_tileType = AccessTools.Inner(pathfinding, "NavMeshTile") ?? throw new MissingMemberException(nameof(Pathfinding), "NavMeshTile");
            s_buildtiles = AccessTools.DeclaredMethod(pathfinding, "Buildtiles", Type.EmptyTypes) ?? throw new MissingMethodException(nameof(Pathfinding), "Buildtiles");
            s_buildTile = AccessTools.DeclaredMethod(pathfinding, "BuildTile", new[] { s_tileType }) ?? throw new MissingMethodException(nameof(Pathfinding), "BuildTile");
            // The field accessors for the private tile type; throws if a field is missing or of another type.
            typeof(Picker<>).MakeGenericType(s_tileType).GetMethod("Init", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            s_choose = AccessTools.DeclaredMethod(typeof(FarNavmeshCapModule), nameof(Choose)).MakeGenericMethod(s_tileType);

            Feature.Switched += _ => Reset();
            harmony.Patch(s_buildtiles, transpiler: new HarmonyMethod(typeof(FarNavmeshCapModule), nameof(InsertChoice)) { priority = Priority.Last });
            if (!Patched)
                EnginePlugin.Log?.LogWarning("FarNavmeshCap: Pathfinding.Buildtiles is not of the expected shape (game update or another mod), left as it is.");
        }

        static void Reset()
        {
            s_clusterBuilt.Clear();
            s_lastOutsideRebuild = float.NegativeInfinity;
            s_quietUntil = float.NegativeInfinity;
            s_errors = 0;
            s_aside = false;
            s_foreignCheckedAt = -1;
            s_answers.Clear();
            s_answersIsFar = null;
            s_answersClusterOf = null;
            s_brokenIsFar = null;
            s_brokenClusterOf = null;
        }

        // After the selection loop the game tests its pick and builds it:
        //     L: ldloc t; brfalse END; ldarg.0; ldloc t; call BuildTile(t); ...
        // becomes
        //     L: ldarg.0; ldloc t; call Choose(Pathfinding, t); dup; stloc t; brfalse END; ldarg.0; ldloc t; call BuildTile(t); ...
        // The loop exit's instruction is edited in place, so it keeps its labels and blocks (Harmony's Label type does
        // not resolve against netstandard here, see AllocFixesModule); the rest of the method is the game's.
        static IEnumerable<CodeInstruction> InsertChoice(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            // Harmony runs this whenever anyone patches or unpatches Buildtiles: the next pick looks at who else is there
            // (the patch is registered once this returns).
            s_foreignCheckedAt = -1;
            var code = new List<CodeInstruction>(instructions);
            int at = -1, found = 0;
            for (int i = 4; i < code.Count; i++)
            {
                if (!(code[i].opcode == OpCodes.Call || code[i].opcode == OpCodes.Callvirt) || !(code[i].operand is MethodInfo m)
                    || m.Name != s_buildTile.Name || m.DeclaringType != s_buildTile.DeclaringType)
                    continue;
                CodeInstruction test = code[i - 4], branch = code[i - 3], self = code[i - 2], arg = code[i - 1];
                int local = LocalIndex(test);
                if (local >= 0 && local == LocalIndex(arg) && (branch.opcode == OpCodes.Brfalse || branch.opcode == OpCodes.Brfalse_S)
                    && self.opcode == OpCodes.Ldarg_0 && LocalType(original, test, local) == s_tileType)
                {
                    at = i - 4;
                    found++;
                }
            }
            if (found != 1)
            {
                // A later run that does not find the site leaves the method without the call, and the counters say so.
                Patched = false;
                EnginePlugin.Log?.LogWarning($"FarNavmeshCap: {found} build sites of the expected shape in Pathfinding.Buildtiles (expected 1).");
                return code;
            }
            CodeInstruction pickTest = code[at];
            var load = new CodeInstruction(pickTest.opcode, pickTest.operand);
            CodeInstruction store = StoreTo(pickTest);
            pickTest.opcode = OpCodes.Ldarg_0;
            pickTest.operand = null;
            code.InsertRange(at + 1, new[]
            {
                load,
                new CodeInstruction(OpCodes.Call, s_choose),
                new CodeInstruction(OpCodes.Dup),
                store,
            });
            Patched = true;
            return code;
        }

        static int LocalIndex(CodeInstruction ci)
        {
            OpCode op = ci.opcode;
            if (op == OpCodes.Ldloc_0) return 0;
            if (op == OpCodes.Ldloc_1) return 1;
            if (op == OpCodes.Ldloc_2) return 2;
            if (op == OpCodes.Ldloc_3) return 3;
            if (op == OpCodes.Ldloc_S || op == OpCodes.Ldloc)
            {
                if (ci.operand is LocalBuilder lb) return lb.LocalIndex;
                if (ci.operand is byte b) return b;
                if (ci.operand is short s) return s;
                if (ci.operand is int n) return n;
            }
            return -1;
        }

        static Type LocalType(MethodBase original, CodeInstruction ci, int index)
        {
            if (ci.operand is LocalBuilder lb)
                return lb.LocalType;
            IList<LocalVariableInfo> locals = original?.GetMethodBody()?.LocalVariables;
            return locals != null && index < locals.Count ? locals[index].LocalType : null;
        }

        static CodeInstruction StoreTo(CodeInstruction load)
        {
            OpCode op = load.opcode;
            if (op == OpCodes.Ldloc_0) return new CodeInstruction(OpCodes.Stloc_0);
            if (op == OpCodes.Ldloc_1) return new CodeInstruction(OpCodes.Stloc_1);
            if (op == OpCodes.Ldloc_2) return new CodeInstruction(OpCodes.Stloc_2);
            if (op == OpCodes.Ldloc_3) return new CodeInstruction(OpCodes.Stloc_3);
            return new CodeInstruction(op == OpCodes.Ldloc_S ? OpCodes.Stloc_S : OpCodes.Stloc, load.operand);
        }

        // Called by the patched Buildtiles with the game's pick (null: nothing is due); returns the tile to build.
        static T Choose<T>(Pathfinding pf, T pick) where T : class
        {
            if (!Feature.On || pick == null || s_aside)
                return pick;
            try
            {
                return ForeignPatched() ? pick : Picker<T>.Choose(pf, pick);
            }
            catch (Exception e)
            {
                Errors++;
                if (++s_errors >= MaxErrors)
                {
                    s_aside = true;
                    EnginePlugin.Log?.LogError($"FarNavmeshCap: {s_errors} errors, the game's order is kept until the feature is switched again. Last: {e}");
                }
                else
                    EnginePlugin.Log?.LogWarning($"FarNavmeshCap: error, the game's pick is kept: {e}");
                return pick;
            }
        }

        // The tile selection, typed by the game's private tile class.
        internal static class Picker<T> where T : class
        {
            internal static AccessTools.FieldRef<Pathfinding, Dictionary<Vector3Int, T>> Tiles;
            internal static AccessTools.FieldRef<T, float> Poke, Built;
            internal static AccessTools.FieldRef<T, Vector3> Center;

            static void Init()
            {
                Tiles = AccessTools.FieldRefAccess<Pathfinding, Dictionary<Vector3Int, T>>("m_tiles");
                Poke = AccessTools.FieldRefAccess<T, float>("m_pokeTime");
                Built = AccessTools.FieldRefAccess<T, float>("m_buildTime");
                Center = AccessTools.FieldRefAccess<T, Vector3>("m_center");
            }

            internal static T Choose(Pathfinding pf, T pick)
            {
                ZNet net = ZNet.instance;
                // A dedicated server has no local player whose area could come first: the game's order.
                if ((object)net == null || (object)pf == null || net.IsDedicated())
                    return pick;
                // The game's own "this peer's area": its reference position (the local player, or what a Tracker sets).
                Vector2s me = ZoneSystem.GetZone(net.GetReferencePosition());
                return Decide(Tiles(pf), pf.m_updateInterval, pick, Now(), me, Math.Max(1, NearZones));
            }

            // Pure (no Unity calls): what to build now, given the game's pick among the tiles due.
            internal static T Decide(Dictionary<Vector3Int, T> tiles, float interval, T pick, float now, Vector2s me, int reach)
            {
                Picks++;
                if (IsNear(Center(pick), me, reach))
                {
                    // The game's pick is the first tile with the largest value among all due tiles, hence also among
                    // any class it is in. New: no tile ranks above Near new. A rebuild: no new tile is due (one would
                    // have outranked it), so only the share (an outside rebuild due) could come first.
                    if (Built(pick) < 0f || now - s_lastOutsideRebuild < OutsideShareSeconds || now < s_quietUntil)
                    {
                        CountNear(pick, now, interval);
                        return pick;
                    }
                }
                return Scan(tiles, interval, pick, now, me, reach);
            }

            static T Scan(Dictionary<Vector3Int, T> tiles, float interval, T pick, float now, Vector2s me, int reach)
            {
                long started = Stopwatch.GetTimestamp();
                Func<Vector3, bool> isFar = Usable(IsFarCore, s_brokenIsFar, s_brokenIsFarAt, now);
                Func<Vector3, int> clusterOf = isFar == null ? null : Usable(FarClusterOf, s_brokenClusterOf, s_brokenClusterOfAt, now);
                // The cap needs both hooks: without a cluster per core every tile outside is Mid.
                if (clusterOf == null)
                    isFar = null;
                bool keep = isFar != null && HookCacheSeconds > 0f;
                if (keep && (!ReferenceEquals(isFar, s_answersIsFar) || !ReferenceEquals(clusterOf, s_answersClusterOf)
                    || s_answersGeneration != s_hookGeneration || !(now - s_answersSince < HookCacheSeconds)))
                {
                    s_answers.Clear();
                    s_answersIsFar = isFar;
                    s_answersClusterOf = clusterOf;
                    s_answersGeneration = s_hookGeneration;
                    s_answersSince = now;
                }
                float cap = FarClusterSeconds;
                // The best tile of each rank: Near new, outside new, Near rebuild, outside rebuild (Far ones only while
                // their cluster may build).
                T nearNew = null, outNew = null, nearOld = null, outOld = null;
                float nearNewValue = 0f, outNewValue = 0f, nearOldValue = 0f, outOldValue = 0f;
                bool outNewFar = false, outOldFar = false;
                long outNewCluster = 0, outOldCluster = 0;
                int dueNew = 0, dueNear = 0, dueMid = 0, dueFar = 0, capped = 0, outsideOld = 0;
                // When the first capped far cluster with a rebuild due may build again (the quiet window ends there).
                float capEnds = float.PositiveInfinity;
                // The game's walk: same table, same order, same test, same "first largest" rule, per rank.
                foreach (KeyValuePair<Vector3Int, T> kv in tiles)
                {
                    T tile = kv.Value;
                    float value = Poke(tile) - Built(tile);
                    if (!(value > interval))
                        continue;
                    bool fresh = Built(tile) < 0f;
                    if (fresh)
                        dueNew++;
                    Vector3 c = Center(tile);
                    Vector2s zone = ZoneSystem.GetZone(c);
                    if (Math.Abs(zone.x - me.x) <= reach && Math.Abs(zone.y - me.y) <= reach)
                    {
                        dueNear++;
                        if (fresh)
                        {
                            if (nearNew == null || value > nearNewValue)
                            {
                                nearNew = tile;
                                nearNewValue = value;
                            }
                        }
                        else if (nearOld == null || value > nearOldValue)
                        {
                            nearOld = tile;
                            nearOldValue = value;
                        }
                        continue;
                    }
                    Answer answer = default;
                    if (isFar != null)
                    {
                        long column = ((long)kv.Key.x << 32) | (uint)kv.Key.y;
                        if (!keep || !s_answers.TryGetValue(column, out answer))
                        {
                            // A hook that throws is left out for the rest of this look; its answer is not kept.
                            if (Ask(ref isFar, ref clusterOf, c, now, out answer) && keep)
                                s_answers[column] = answer;
                            else
                                keep = false;
                        }
                    }
                    if (answer.Far)
                    {
                        dueFar++;
                        if (s_clusterBuilt.TryGetValue(answer.Cluster, out float last) && now - last < cap)
                        {
                            capped++;
                            if (!fresh && last + cap < capEnds)
                                capEnds = last + cap;
                            continue;
                        }
                    }
                    else
                        dueMid++;
                    if (fresh)
                    {
                        if (outNew == null || value > outNewValue)
                        {
                            outNew = tile;
                            outNewValue = value;
                            outNewFar = answer.Far;
                            outNewCluster = answer.Cluster;
                        }
                        continue;
                    }
                    // Only rebuilds the share could take count: a look that finds none starts the quiet window.
                    outsideOld++;
                    if (outOld == null || value > outOldValue)
                    {
                        outOld = tile;
                        outOldValue = value;
                        outOldFar = answer.Far;
                        outOldCluster = answer.Cluster;
                    }
                }

                T chosen;
                bool share = false;
                if (nearNew != null)
                    chosen = nearNew;
                else if (outNew != null)
                    chosen = outNew;
                else if (nearOld != null)
                {
                    // The share: the first outside rebuild, ahead of the player's once per OutsideShareSeconds.
                    share = outOld != null && now - s_lastOutsideRebuild >= OutsideShareSeconds;
                    chosen = share ? outOld : nearOld;
                }
                else
                    chosen = outOld;

                if (chosen == null)
                    Deferred++;
                else if (ReferenceEquals(chosen, nearNew) || ReferenceEquals(chosen, nearOld))
                    CountNear(chosen, now, interval);
                else
                {
                    bool fresh = ReferenceEquals(chosen, outNew);
                    if (!fresh)
                        s_lastOutsideRebuild = now;
                    if (share)
                        ShareBuilds++;
                    if (fresh ? outNewFar : outOldFar)
                    {
                        s_clusterBuilt[fresh ? outNewCluster : outOldCluster] = now;
                        FarBuilds++;
                        if (fresh)
                            FarNewBuilds++;
                    }
                    else
                    {
                        MidBuilds++;
                        if (fresh)
                            MidNewBuilds++;
                    }
                }
                if (chosen != null && !ReferenceEquals(chosen, pick))
                    Overridden++;
                if (outsideOld == 0)
                    s_quietUntil = Math.Min(now + QuietSeconds, capEnds);
                Expire(now, cap);

                Scans++;
                ScannedTiles += tiles.Count;
                TileCount = tiles.Count;
                CapHooks = isFar != null;
                DueNew = dueNew;
                DueNear = dueNear;
                DueMid = dueMid;
                DueFar = dueFar;
                CappedFar = capped;
                CappedSeen += capped;
                ActiveClusters = s_clusterBuilt.Count;
                KeptAnswers = s_answers.Count;
                double us = (Stopwatch.GetTimestamp() - started) * 1e6 / Stopwatch.Frequency;
                TotalScanUs += us;
                if (us > MaxScanUs)
                    MaxScanUs = us;
                return chosen;
            }

            static void CountNear(T tile, float now, float interval)
            {
                NearBuilds++;
                float built = Built(tile);
                if (built < 0f)
                {
                    NearNewBuilds++;
                    return;
                }
                // Past due since about built + interval, for a tile poked all along.
                double overdue = Math.Max(0.0, now - built - interval);
                NearRebuilds++;
                TotalNearOverdueS += overdue;
                if (overdue > MaxNearOverdueS)
                    MaxNearOverdueS = overdue;
            }
        }

        static bool IsNear(Vector3 centre, Vector2s me, int reach)
        {
            Vector2s zone = ZoneSystem.GetZone(centre);
            return Math.Abs(zone.x - me.x) <= reach && Math.Abs(zone.y - me.y) <= reach;
        }

        // A hook, or null while it is the one that threw last and HookRetrySeconds have not passed.
        static D Usable<D>(D hook, Delegate broken, float brokenAt, float now) where D : class
            => hook != null && ReferenceEquals(hook, broken) && now - brokenAt < HookRetrySeconds ? null : hook;

        // One tile column's answers (both hooks set). False when a hook threw: it is then marked, both are left out (null)
        // for the rest of the look, and the tile counts as with the hooks unset (Mid).
        static bool Ask(ref Func<Vector3, bool> isFar, ref Func<Vector3, int> clusterOf, Vector3 centre, float now, out Answer answer)
        {
            answer = default;
            HookCalls++;
            try
            {
                if (!isFar(centre))
                    return true;
            }
            catch (Exception e)
            {
                Broke(ref s_brokenIsFar, ref s_brokenIsFarAt, isFar, now, "IsFarCore", "tiles outside count as not in a far core", e);
                isFar = null;
                clusterOf = null;
                return false;
            }
            HookCalls++;
            try
            {
                answer.Cluster = clusterOf(centre);
                answer.Far = true;
                return true;
            }
            catch (Exception e)
            {
                Broke(ref s_brokenClusterOf, ref s_brokenClusterOfAt, clusterOf, now, "FarClusterOf", "no tile counts as in a far core", e);
                isFar = null;
                clusterOf = null;
                return false;
            }
        }

        static void Broke(ref Delegate broken, ref float at, Delegate hook, float now, string name, string meaning, Exception e)
        {
            HookErrors++;
            if (!ReferenceEquals(broken, hook))
                EnginePlugin.Log?.LogWarning($"FarNavmeshCap: {name} threw, {meaning} until it is replaced (asked again every {HookRetrySeconds:F0} s): {e}");
            broken = hook;
            at = now;
        }

        // Clusters whose cap has run out are forgotten (only a few are ever held).
        static void Expire(float now, float cap)
        {
            if (s_clusterBuilt.Count == 0)
                return;
            s_expired.Clear();
            foreach (KeyValuePair<long, float> kv in s_clusterBuilt)
                if (!(now - kv.Value < cap))
                    s_expired.Add(kv.Key);
            foreach (long k in s_expired)
                s_clusterBuilt.Remove(k);
        }

        // The game's clock, read in one place (the out-of-game check runs the selection on a clock of its own).
        [MethodImpl(MethodImplOptions.NoInlining)]
        static float Now() => Time.time;

        [MethodImpl(MethodImplOptions.NoInlining)]
        static int Frame() => Time.frameCount;

        // Another mod rewriting Buildtiles (its selection may not be the game's any more): keep its pick. Looked at on
        // the first pick after any patch or unpatch of Buildtiles, and then every ForeignCheckFrames frames.
        static bool ForeignPatched()
        {
            int frame = Frame();
            if (s_foreignCheckedAt >= 0 && frame >= s_foreignCheckedAt && frame - s_foreignCheckedAt < ForeignCheckFrames)
                return s_foreign;
            s_foreignCheckedAt = frame;
            bool foreign;
            string why = "another mod rewrites Pathfinding.Buildtiles";
            try
            {
                HarmonyLib.Patches info = Harmony.GetPatchInfo(s_buildtiles);
                foreign = info != null && (Foreign(info.Transpilers) || Foreign(info.ILManipulators));
            }
            catch (Exception e)
            {
                // Cannot tell who else is there: behave as the game.
                foreign = true;
                why = "reading Harmony patch info failed (" + e.Message + ")";
            }
            if (foreign && !s_foreign)
                EnginePlugin.Log?.LogWarning("FarNavmeshCap: " + why + ", the game's pick is kept");
            else if (!foreign && s_foreign)
                EnginePlugin.Log?.LogInfo("FarNavmeshCap: Pathfinding.Buildtiles is no longer rewritten by another mod, cap active again");
            s_foreign = foreign;
            return foreign;
        }

        static bool Foreign(IEnumerable<HarmonyLib.Patch> patches)
        {
            if (patches == null)
                return false;
            foreach (HarmonyLib.Patch p in patches)
                if (p.owner != EnginePlugin.Guid && (p.owner == null || !p.owner.StartsWith(LabOwners, StringComparison.Ordinal)))
                    return true;
            return false;
        }
    }
}
