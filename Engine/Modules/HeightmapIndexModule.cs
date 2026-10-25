using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace RealPortal.Engine
{
    // "Which heightmap is under this point" walks every loaded heightmap: Heightmap.FindHeightmap and its radius
    // overload loop over Heightmap.s_heightmaps and call IsPointInside (a transform read) on each. Through them (and
    // FindBiome, GetHeight, AtMaxLevelDepth, FindBiomeClutter, HaveQueuedRebuild) that is O(loaded zones) per piece
    // (WearNTear.Start), plant, fireplace, beehive, tree log, spawner, terrain op and TerrainComp, per frame for the
    // grass (ClutterSystem and Real Portal's far grass via HaveQueuedRebuild) and for EnvMan in the Ashlands and Deep
    // North. With far areas loaded there are ~1300 heightmaps instead of ~200. (TerrainModifier.PokeHeightmaps, the
    // other per-object walk over the list, is TerrainModifierIndexModule's.)
    //
    // This keeps a grid index of the same list, one cell per zone (64 m, centred on ZoneSystem.GetZonePos like the
    // zone heightmaps), and answers from the cells the query can touch. Equivalence with the game:
    //  - The index mirrors s_heightmaps exactly. Its maintenance watches the list itself around the three places the
    //    game changes it (Awake, OnDestroy, the IsDistantLod setter): one element appended -> append, one removed ->
    //    remove the first occurrence (List.Remove). Anything else, or a count that no longer matches at query time
    //    (another mod touching the list, an exception inside Awake), rebuilds it from the list.
    //  - Every element carries a stamp in list order (appends get increasing stamps, removals keep the order of the
    //    rest), so "the first match in list order" is "the matching candidate with the smallest stamp".
    //  - The cells only narrow the candidates: every candidate is still decided by IsPointInside. A heightmap is filed
    //    in a cell only if it is centred on a grid node (within 0.25 m) and at most 64.5 m wide, and a query looks at
    //    every cell whose node lies within radius + 33 m, so no heightmap the game's own IsPointInside would accept is
    //    ever left out. Everything else (other sizes and offsets from mods or locations, TerrainLod/FarTerrain meshes
    //    in the instant before they become distant LOD, destroyed ones) sits in a small fallback list that every query
    //    scans in list order.
    //  - That narrowing assumes the game's own square. Another mod's patch on IsPointInside may accept more, and a
    //    transpiler on either FindHeightmap lives in the loop the index replaces: while there is either (the Real
    //    Portal lab's timers aside), the game's own loop answers. Checked at the first query and then every 1000
    //    frames. Other mods' prefixes and postfixes on FindHeightmap keep working: this prefix runs last and leaves a
    //    call another prefix skipped alone, and postfixes run either way.
    //  - Heightmaps are not expected to move or resize while listed (zone heightmaps never do; TerrainLod moves only
    //    distant LOD ones, which are not listed). In case a mod does, each query re-checks the geometry of two filed
    //    heightmaps round-robin and refiles any that changed.
    //  - Queries the index cannot narrow (NaN or huge coordinates, a radius covering more than 256 cells, a null output
    //    list) run the game's own loop.
    // Off: every patch passes through and the index is dropped; it is rebuilt from the list on the first query after
    // switching back on.
    //
    // VisualOnly rank (ARCHITECTURE-v2 §3.8 step 5, FARWORLD-V3 §3.2 h). Real Portal marks the zone heightmaps it makes
    // for windows only (VisualZones: no collider, drawn into windows) with SetVisualOnly, before their Awake lists them.
    // While a listed heightmap is marked, the index ranks marked ones below the rest: FindHeightmap(point) answers the
    // first unmarked heightmap in list order that contains the point, and the first marked one only when there is none;
    // the radius overload leaves out a marked heightmap whose zone cell also holds an unmarked one, so TerrainOp,
    // TerrainComp's neighbour grid and HaveQueuedRebuild see one heightmap per zone (the real one while a VisualZone waits
    // to be replaced by it). TerrainComp.Awake therefore binds to a VisualZone only where nothing real stands. A marked
    // heightmap's mark goes with its OnDestroy, and every TerrainComp still bound to it is bound again to what
    // FindHeightmap answers then, its neighbour grid dropped (RebindTerrainComps). With nothing marked every answer is the
    // one described above, by the same code. The rank lives in the index only (VisualOnlyRanked): off, with a foreign
    // patch, or for a query the index cannot narrow, the game's loop answers in plain list order.
    [EngineModule]
    public static class HeightmapIndexModule
    {
        internal static readonly Feature Feature = Features.Register("HeightmapIndex", true,
            "Find the heightmap under a point (pieces, plants, grass, terrain ops and modifiers) through a per-zone index instead of scanning every loaded heightmap.");

        const double CellSize = 64.0;          // ZoneSystem.c_ZoneSize: zone heightmaps are centred on multiples of it
        const double HalfCell = 32.0;
        const double AlignTolerance = 0.25;    // how far off a grid node, or how much wider than a zone, a filed heightmap may be
        const double Margin = 1.0;             // > 2 * AlignTolerance + float rounding of IsPointInside (< 0.25 m below 2e6)
        const double MaxCoordinate = 1e6;      // beyond this (or NaN) the game's own loop answers (worlds are ~10 km)
        const int MaxCells = 256;
        const int ChecksPerQuery = 2;
        const int ForeignCheckFrames = 1000;
        const string LabOwners = "valheim.realportal.lab";

        sealed class Entry
        {
            public Heightmap Map;
            public long Stamp;                 // position in s_heightmaps: smaller = earlier
            public Entry NextOfMap;            // the same heightmap listed again (List.Add twice), in list order
            public bool InGrid;
            public long Cell;
            public Entry NextInCell;           // stamp order
            public int GridSlot;
            public float X, Z, Half;           // geometry when filed
            public bool Visual;                // marked VisualOnly (s_visualOnly)
        }

        // The game's list (a static readonly field, so the same object for the whole session).
        static List<Heightmap> s_heightmaps;
        static readonly Dictionary<Heightmap, Entry> s_byMap = new Dictionary<Heightmap, Entry>(new ByReference());
        static readonly Dictionary<long, Entry> s_cells = new Dictionary<long, Entry>(new CellKeys());
        static readonly List<Entry> s_grid = new List<Entry>();        // filed entries, for the round-robin check
        static readonly List<Entry> s_fallback = new List<Entry>();    // stamp order
        static readonly List<List<Entry>> s_spare = new List<List<Entry>>();
        static int s_count;
        static long s_nextStamp;
        static bool s_valid;
        static int s_checkCursor;
        static bool s_reportedFallback;
        static MethodInfo s_isPointInside, s_findOne, s_findAll;
        static bool s_foreign;
        static int s_foreignCheckedAt = -1;
        static bool s_installed;
        // VisualOnly marks, kept apart from the entries (they are set before Awake lists a heightmap and outlive a drop
        // of the index), and how many listed entries carry one: the ranked paths run only while that is above 0.
        static readonly HashSet<Heightmap> s_visualOnly = new HashSet<Heightmap>(new ByReference());
        static int s_visualListed;
        // TerrainComp's private parts, looked up the first time a marked heightmap goes (null when not found).
        static List<TerrainComp> s_terrainComps;
        static AccessTools.FieldRef<TerrainComp, Heightmap> s_hmapOf;
        static AccessTools.FieldRef<TerrainComp, TerrainComp[,]> s_neighborGridOf;
        static bool s_terrainLooked;

        // For the lab.
        public static int Rebuilds { get; private set; }
        public static int Refiled { get; private set; }
        public static long Answered { get; private set; }
        // Answers given with the VisualOnly rank (something listed was marked), and TerrainComps bound again.
        public static long RankedAnswers { get; private set; }
        public static int Rebound { get; private set; }
        public static string Describe() =>
            $"heightmaps {s_count} (grid {s_grid.Count}, fallback {s_fallback.Count}, cells {s_cells.Count}), valid {s_valid}, rebuilds {Rebuilds}, refiled {Refiled}, answered {Answered}, foreign patches {s_foreign}; " +
            $"visual-only {s_visualOnly.Count} marked ({s_visualListed} listed), ranked answers {RankedAnswers}, terrain comps rebound {Rebound}";

        // ---- VisualOnly rank -----------------------------------------------------------------------------

        // Marks (or unmarks) a heightmap as VisualOnly: ranked below every unmarked one (see the header). Main thread. Any
        // time, before its Awake too; the mark goes when the heightmap is destroyed.
        public static void SetVisualOnly(Heightmap map, bool visualOnly)
        {
            if (ReferenceEquals(map, null))
                return;
            if (visualOnly ? !s_visualOnly.Add(map) : !s_visualOnly.Remove(map))
                return;
            if (!s_byMap.TryGetValue(map, out Entry e))
                return;
            for (; e != null; e = e.NextOfMap)
                if (e.Visual != visualOnly)
                {
                    e.Visual = visualOnly;
                    s_visualListed += visualOnly ? 1 : -1;
                }
        }

        public static bool IsVisualOnly(Heightmap map) => !ReferenceEquals(map, null) && s_visualOnly.Count > 0 && s_visualOnly.Contains(map);

        public static int VisualOnlyCount => s_visualOnly.Count;

        // The index answers with the rank right now (installed, on, no foreign patch at the last check). When false the
        // game's loop answers in list order, marked heightmaps included as any other.
        public static bool VisualOnlyRanked => s_installed && Feature.On && !s_foreign;

        // Every TerrainComp bound to `from` (its m_hmap) is bound to what FindHeightmap answers for its position now, and
        // its neighbour grid is dropped so the game looks it up again. One that nothing else covers keeps its binding
        // (TerrainComp reads m_hmap without null checks past Awake). Called for a marked heightmap's OnDestroy, after the
        // game took it off the list. Returns how many were bound again. Main thread.
        public static int RebindTerrainComps(Heightmap from)
        {
            if (ReferenceEquals(from, null) || !TerrainParts())
                return 0;
            int n = 0;
            for (int i = 0; i < s_terrainComps.Count; i++)
            {
                TerrainComp tc = s_terrainComps[i];
                if (!tc || !ReferenceEquals(s_hmapOf(tc), from))
                    continue;
                Heightmap now = Heightmap.FindHeightmap(tc.transform.position);
                if (ReferenceEquals(now, null) || ReferenceEquals(now, from) || !now)
                    continue;
                s_hmapOf(tc) = now;
                s_neighborGridOf(tc) = null;
                n++;
            }
            Rebound += n;
            return n;
        }

        static bool TerrainParts()
        {
            if (!s_terrainLooked)
            {
                s_terrainLooked = true;
                try
                {
                    FieldInfo list = AccessTools.DeclaredField(typeof(TerrainComp), "s_instances");
                    FieldInfo hmap = AccessTools.DeclaredField(typeof(TerrainComp), "m_hmap");
                    FieldInfo grid = AccessTools.DeclaredField(typeof(TerrainComp), "m_neighborGrid");
                    if (list != null && hmap != null && grid != null && list.IsStatic && list.FieldType == typeof(List<TerrainComp>)
                        && hmap.FieldType == typeof(Heightmap) && grid.FieldType == typeof(TerrainComp[,]))
                    {
                        s_terrainComps = (List<TerrainComp>)list.GetValue(null);
                        s_hmapOf = AccessTools.FieldRefAccess<TerrainComp, Heightmap>(hmap);
                        s_neighborGridOf = AccessTools.FieldRefAccess<TerrainComp, TerrainComp[,]>(grid);
                    }
                }
                catch (Exception e)
                {
                    s_terrainComps = null;
                    EnginePlugin.Log?.LogWarning("HeightmapIndex: TerrainComp's fields could not be read: " + e.Message);
                }
                if (s_terrainComps == null)
                    EnginePlugin.Log?.LogWarning("HeightmapIndex: TerrainComp.s_instances/m_hmap/m_neighborGrid not as expected; " +
                        "TerrainComps bound to a destroyed VisualOnly heightmap are not bound again");
            }
            return s_terrainComps != null;
        }

        static void Install(Harmony harmony)
        {
            s_heightmaps = (List<Heightmap>)AccessTools.Field(typeof(Heightmap), "s_heightmaps").GetValue(null);
            Type self = typeof(HeightmapIndexModule);
            var before = new HarmonyMethod(self, nameof(CountBefore));
            var after = new HarmonyMethod(self, nameof(ListChanged));
            harmony.Patch(AccessTools.Method(typeof(Heightmap), "Awake"), prefix: before, postfix: after);
            harmony.Patch(AccessTools.Method(typeof(Heightmap), "OnDestroy"), prefix: before, postfix: new HarmonyMethod(self, nameof(Destroyed)));
            harmony.Patch(AccessTools.PropertySetter(typeof(Heightmap), nameof(Heightmap.IsDistantLod)), prefix: before, postfix: after);
            s_isPointInside = AccessTools.Method(typeof(Heightmap), nameof(Heightmap.IsPointInside));
            s_findOne = AccessTools.Method(typeof(Heightmap), nameof(Heightmap.FindHeightmap), new[] { typeof(Vector3) });
            s_findAll = AccessTools.Method(typeof(Heightmap), nameof(Heightmap.FindHeightmap), new[] { typeof(Vector3), typeof(float), typeof(List<Heightmap>) });
            // Last among prefixes: a mod that answers the lookup itself (and skips the original) keeps its answer.
            harmony.Patch(s_findOne, prefix: new HarmonyMethod(self, nameof(FindOne)) { priority = Priority.Last });
            harmony.Patch(s_findAll, prefix: new HarmonyMethod(self, nameof(FindAll)) { priority = Priority.Last });
            Feature.Switched += _ => Drop();
            s_installed = true;
        }

        // ---- keeping up with s_heightmaps ------------------------------------------------------------------

        static void CountBefore(out int __state) => __state = s_heightmaps.Count;

        static void ListChanged(Heightmap __instance, int __state)
        {
            if (!s_valid)
                return;
            int now = s_heightmaps.Count;
            if (now == __state)
                return;
            if (now == __state + 1 && ReferenceEquals(s_heightmaps[now - 1], __instance))
                Add(__instance);
            else if (now == __state - 1 && s_byMap.ContainsKey(__instance))
                Remove(__instance);
            else
                s_valid = false;
        }

        // OnDestroy: the list as above, then a marked heightmap's mark goes, and the TerrainComps bound to it are bound
        // to what is under them now (the game took it off the list before this postfix runs).
        static void Destroyed(Heightmap __instance, int __state)
        {
            ListChanged(__instance, __state);
            if (s_visualOnly.Count == 0 || !s_visualOnly.Contains(__instance))
                return;
            SetVisualOnly(__instance, false);
            try
            {
                RebindTerrainComps(__instance);
            }
            catch (Exception e)
            {
                EnginePlugin.Log?.LogWarning("HeightmapIndex: binding TerrainComps again after a VisualOnly heightmap went failed: " + e.Message);
            }
        }

        static void Drop()
        {
            s_valid = false;
            s_byMap.Clear();
            s_cells.Clear();
            s_grid.Clear();
            s_fallback.Clear();
            s_count = 0;
            s_visualListed = 0;
        }

        static void Rebuild()
        {
            Drop();
            // A mark on a heightmap destroyed before it was ever active (no OnDestroy then) goes here.
            if (s_visualOnly.Count > 0)
                s_visualOnly.RemoveWhere(m => !m);
            s_nextStamp = 0;
            s_checkCursor = 0;
            for (int i = 0; i < s_heightmaps.Count; i++)
                Add(s_heightmaps[i]);
            s_valid = true;
            Rebuilds++;
            if (!s_reportedFallback && s_fallback.Count > 16)
            {
                s_reportedFallback = true;
                EnginePlugin.Log?.LogInfo($"HeightmapIndex: {s_fallback.Count} of {s_count} heightmaps are off the zone grid and are scanned linearly");
            }
        }

        static void Add(Heightmap map)
        {
            var e = new Entry { Map = map, Stamp = ++s_nextStamp, GridSlot = -1 };
            if (s_visualOnly.Count > 0 && !ReferenceEquals(map, null) && s_visualOnly.Contains(map))
            {
                e.Visual = true;
                s_visualListed++;
            }
            if (!ReferenceEquals(map, null))
            {
                if (s_byMap.TryGetValue(map, out Entry first))
                {
                    while (first.NextOfMap != null)
                        first = first.NextOfMap;
                    first.NextOfMap = e;
                }
                else
                    s_byMap.Add(map, e);
            }
            File(e);
            s_count++;
        }

        // List.Remove takes out the first occurrence.
        static void Remove(Heightmap map)
        {
            Entry first = s_byMap[map];
            if (first.NextOfMap != null)
                s_byMap[map] = first.NextOfMap;
            else
                s_byMap.Remove(map);
            Unfile(first);
            s_count--;
            if (first.Visual)
                s_visualListed--;
        }

        static void File(Entry e)
        {
            e.InGrid = false;
            Heightmap map = e.Map;
            if (!ReferenceEquals(map, null) && map)
            {
                Vector3 p = map.transform.position;
                // The same half size IsPointInside uses.
                float half = (float)((double)map.m_width * (double)map.m_scale * 0.5);
                e.X = p.x;
                e.Z = p.z;
                e.Half = half;
                double gx = Math.Round(p.x / CellSize);
                double gz = Math.Round(p.z / CellSize);
                if (half >= 0f && half <= HalfCell + AlignTolerance && Math.Abs(gx) < MaxCoordinate / CellSize && Math.Abs(gz) < MaxCoordinate / CellSize
                    && Math.Abs(p.x - gx * CellSize) <= AlignTolerance && Math.Abs(p.z - gz * CellSize) <= AlignTolerance)
                {
                    e.InGrid = true;
                    e.Cell = Key((int)gx, (int)gz);
                    InsertInCell(e);
                    e.GridSlot = s_grid.Count;
                    s_grid.Add(e);
                    return;
                }
            }
            int i = s_fallback.Count;
            while (i > 0 && s_fallback[i - 1].Stamp > e.Stamp)
                i--;
            s_fallback.Insert(i, e);
        }

        static void Unfile(Entry e)
        {
            if (!e.InGrid)
            {
                s_fallback.Remove(e);
                return;
            }
            Entry head = s_cells[e.Cell];
            if (head == e)
            {
                if (e.NextInCell == null)
                    s_cells.Remove(e.Cell);
                else
                    s_cells[e.Cell] = e.NextInCell;
            }
            else
            {
                Entry prev = head;
                while (prev.NextInCell != e)
                    prev = prev.NextInCell;
                prev.NextInCell = e.NextInCell;
            }
            e.NextInCell = null;
            int last = s_grid.Count - 1;
            Entry moved = s_grid[last];
            s_grid[e.GridSlot] = moved;
            moved.GridSlot = e.GridSlot;
            s_grid.RemoveAt(last);
            e.GridSlot = -1;
            e.InGrid = false;
        }

        static void InsertInCell(Entry e)
        {
            if (!s_cells.TryGetValue(e.Cell, out Entry head) || head.Stamp > e.Stamp)
            {
                e.NextInCell = head;
                s_cells[e.Cell] = e;
                return;
            }
            Entry prev = head;
            while (prev.NextInCell != null && prev.NextInCell.Stamp < e.Stamp)
                prev = prev.NextInCell;
            e.NextInCell = prev.NextInCell;
            prev.NextInCell = e;
        }

        // Before a query: in step with the list, and a couple of filed heightmaps re-checked for having moved.
        static bool Prepare()
        {
            if (s_heightmaps == null)
                return false;
            if (!s_valid || s_count != s_heightmaps.Count)
                Rebuild();
            for (int i = 0; i < ChecksPerQuery && s_grid.Count > 0; i++)
            {
                if (s_checkCursor >= s_grid.Count)
                    s_checkCursor = 0;
                Entry e = s_grid[s_checkCursor++];
                Heightmap map = e.Map;
                if (!map)
                    continue;   // destroyed but still listed: the game's loop would trip over it as well
                Vector3 p = map.transform.position;
                float half = (float)((double)map.m_width * (double)map.m_scale * 0.5);
                if (p.x != e.X || p.z != e.Z || half != e.Half)
                {
                    Unfile(e);
                    File(e);
                    Refiled++;
                }
            }
            return true;
        }

        // Another mod's patch on IsPointInside (it may accept more than the square the cells are cut by), or a transpiler
        // on either FindHeightmap (it acts only when the game's loop runs): the game's loop answers while there is one.
        // Checked at the first query and then every 1000 frames (mods patch while loading, long before a world is up).
        static bool ForeignPatched()
        {
            int frame = Time.frameCount;
            if (s_foreignCheckedAt >= 0 && frame >= s_foreignCheckedAt && frame - s_foreignCheckedAt < ForeignCheckFrames)
                return s_foreign;
            s_foreignCheckedAt = frame;
            bool foreign;
            string why;
            try
            {
                HarmonyLib.Patches test = Harmony.GetPatchInfo(s_isPointInside);
                foreign = test != null && (Foreign(test.Prefixes) || Foreign(test.Postfixes) || Foreign(test.Finalizers)
                        || Foreign(test.Transpilers) || Foreign(test.ILManipulators))
                    || LoopReplaced(s_findOne) || LoopReplaced(s_findAll);
                why = "Heightmap.IsPointInside or FindHeightmap is patched by another mod";
            }
            catch (Exception e)
            {
                // Cannot tell who else is there: behave as the game.
                foreign = true;
                why = "reading Harmony patch info failed (" + e.Message + ")";
            }
            if (foreign && !s_foreign)
                EnginePlugin.Log?.LogWarning("HeightmapIndex: " + why + ", the game's own loop answers");
            else if (!foreign && s_foreign)
                s_valid = false;    // not queried meanwhile, so no heightmap was re-checked for moving: start over
            s_foreign = foreign;
            return foreign;
        }

        static bool LoopReplaced(MethodBase method)
        {
            HarmonyLib.Patches info = Harmony.GetPatchInfo(method);
            return info != null && (Foreign(info.Transpilers) || Foreign(info.ILManipulators));
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

        // The cells whose node can hold a heightmap reaching within `reach` of (x, z).
        static bool Cells(float x, float z, float reach, out int x0, out int x1, out int z0, out int z1)
        {
            x0 = x1 = z0 = z1 = 0;
            double r = reach;
            if (!(Math.Abs(x) < MaxCoordinate && Math.Abs(z) < MaxCoordinate && r < MaxCoordinate))
                return false;
            if (r < 0)
                r = 0;          // a negative radius only shrinks what IsPointInside accepts
            double ext = r + HalfCell + Margin;
            x0 = (int)Math.Ceiling((x - ext) / CellSize);
            x1 = (int)Math.Floor((x + ext) / CellSize);
            z0 = (int)Math.Ceiling((z - ext) / CellSize);
            z1 = (int)Math.Floor((z + ext) / CellSize);
            return (long)(x1 - x0 + 1) * (z1 - z0 + 1) <= MaxCells;
        }

        // ---- lookups -------------------------------------------------------------------------------------

        // Heightmap.FindHeightmap(point): the first heightmap in list order that contains the point.
        static bool FindOne(Vector3 point, ref Heightmap __result, bool __runOriginal)
        {
            if (!__runOriginal || !Feature.On || ForeignPatched() || !Prepare() || !Cells(point.x, point.z, 0f, out int x0, out int x1, out int z0, out int z1))
                return true;
            if (s_visualListed > 0)
            {
                __result = FirstRanked(point, x0, x1, z0, z1)?.Map;
                Answered++;
                RankedAnswers++;
                return false;
            }
            Entry best = null;
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                {
                    if (!s_cells.TryGetValue(Key(x, z), out Entry e))
                        continue;
                    for (; e != null && (best == null || e.Stamp < best.Stamp); e = e.NextInCell)
                        if (e.Map.IsPointInside(point, 0f))
                        {
                            best = e;
                            break;
                        }
                }
            for (int i = 0; i < s_fallback.Count; i++)
            {
                Entry f = s_fallback[i];
                if (best != null && f.Stamp > best.Stamp)
                    break;
                if (f.Map.IsPointInside(point, 0f))
                {
                    best = f;
                    break;
                }
            }
            __result = best?.Map;
            Answered++;
            return false;
        }

        // Heightmap.FindHeightmap(point, radius, list): appends every heightmap the circle's square touches, in list
        // order (TerrainComp's neighbour grid keeps the last one written, so the order matters).
        static bool FindAll(Vector3 point, float radius, List<Heightmap> heightmaps, bool __runOriginal)
        {
            if (!__runOriginal || !Feature.On || heightmaps == null || ForeignPatched() || !Prepare() || !Cells(point.x, point.z, radius, out int x0, out int x1, out int z0, out int z1))
                return true;
            List<Entry> found = Rent();
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                    if (s_cells.TryGetValue(Key(x, z), out Entry e))
                        for (; e != null; e = e.NextInCell)
                            if (e.Map.IsPointInside(point, radius))
                                found.Add(e);
            for (int i = 0; i < s_fallback.Count; i++)
                if (s_fallback[i].Map.IsPointInside(point, radius))
                    found.Add(s_fallback[i]);
            SortByStamp(found);
            if (s_visualListed > 0)
            {
                DropOutranked(found);
                RankedAnswers++;
            }
            for (int i = 0; i < found.Count; i++)
                heightmaps.Add(found[i].Map);
            Return(found);
            Answered++;
            return false;
        }

        // FindOne with the VisualOnly rank: the first unmarked heightmap in list order that contains the point, else the
        // first marked one. IsPointInside only reads, so testing a few more than the game's loop would changes nothing.
        static Entry FirstRanked(Vector3 point, int x0, int x1, int z0, int z1)
        {
            Entry real = null, visual = null;
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                {
                    if (!s_cells.TryGetValue(Key(x, z), out Entry e))
                        continue;
                    for (; e != null && (real == null || e.Stamp < real.Stamp); e = e.NextInCell)
                    {
                        if (e.Visual)
                        {
                            if (real == null && (visual == null || e.Stamp < visual.Stamp) && e.Map.IsPointInside(point, 0f))
                                visual = e;
                            continue;
                        }
                        if (e.Map.IsPointInside(point, 0f))
                        {
                            real = e;
                            break;
                        }
                    }
                }
            for (int i = 0; i < s_fallback.Count; i++)
            {
                Entry f = s_fallback[i];
                if (real != null && f.Stamp > real.Stamp)
                    break;
                if (f.Visual)
                {
                    if (real == null && (visual == null || f.Stamp < visual.Stamp) && f.Map.IsPointInside(point, 0f))
                        visual = f;
                    continue;
                }
                if (f.Map.IsPointInside(point, 0f))
                {
                    real = f;
                    break;
                }
            }
            return real ?? visual;
        }

        // FindAll with the VisualOnly rank: a marked heightmap filed in a zone cell that also holds an unmarked one stands
        // for nothing (the zone has its real heightmap). Order kept. Marked ones off the grid are never left out.
        static void DropOutranked(List<Entry> found)
        {
            int kept = 0;
            for (int i = 0; i < found.Count; i++)
            {
                Entry e = found[i];
                if (e.Visual && e.InGrid && RealInCell(e.Cell))
                    continue;
                found[kept++] = e;
            }
            found.RemoveRange(kept, found.Count - kept);
        }

        static bool RealInCell(long cell)
        {
            if (!s_cells.TryGetValue(cell, out Entry e))
                return false;
            for (; e != null; e = e.NextInCell)
                if (!e.Visual)
                    return true;
            return false;
        }

        // ---- helpers -------------------------------------------------------------------------------------

        static long Key(int x, int z) => ((long)x << 32) | (uint)z;

        // A handful of entries at most: insertion sort.
        static void SortByStamp(List<Entry> list)
        {
            for (int i = 1; i < list.Count; i++)
            {
                Entry e = list[i];
                int j = i - 1;
                while (j >= 0 && list[j].Stamp > e.Stamp)
                {
                    list[j + 1] = list[j];
                    j--;
                }
                list[j + 1] = e;
            }
        }

        static List<Entry> Rent()
        {
            int n = s_spare.Count;
            if (n == 0)
                return new List<Entry>();
            List<Entry> list = s_spare[n - 1];
            s_spare.RemoveAt(n - 1);
            return list;
        }

        static void Return(List<Entry> list)
        {
            list.Clear();
            s_spare.Add(list);
        }

        sealed class ByReference : IEqualityComparer<Heightmap>
        {
            public bool Equals(Heightmap a, Heightmap b) => ReferenceEquals(a, b);
            public int GetHashCode(Heightmap h) => RuntimeHelpers.GetHashCode(h);
        }

        // long's own hash (low ^ high) collides along diagonals of the grid.
        sealed class CellKeys : IEqualityComparer<long>
        {
            public bool Equals(long a, long b) => a == b;
            public int GetHashCode(long k) => (int)(((ulong)k * 0x9E3779B97F4A7C15UL) >> 32);
        }
    }
}
