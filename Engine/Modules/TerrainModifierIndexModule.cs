using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace RealPortal.Engine
{
    // Terrain modifiers (the levelled, smoothed and painted patches that pieces and locations carry) meet heightmaps in
    // two places, and the game pairs them by scanning everything:
    //  - Heightmap.ApplyModifiers, on every heightmap rebuild, walks ALL loaded modifiers (TerrainModifier.s_instances)
    //    and applies those whose square reaches the heightmap;
    //  - TerrainModifier.PokeHeightmaps, on every modifier's Awake and OnDestroy, walks ALL heightmaps
    //    (Heightmap.s_heightmaps) and pokes those it reaches.
    // With far areas loaded that is ~1,500 modifiers x ~1,300 heightmaps, nearly all of it distance checks that fail,
    // each one four engine calls for the two transform positions.
    //
    // Both lists are mirrored in a grid of 64 m cells centred on the zones, and the game's two loops get a short list in
    // place of the whole one: only the items whose square can reach the asking heightmap or modifier, in the order they
    // have in the game's list. The game's loop still runs over it and still makes its own exact test
    // (enabled && TerrainVSModifier), so what is applied or poked, and in which order, is the same as without the index:
    //  - The short list is a superset of the hits. An item is filed in every cell its square touches, and the asker
    //    looks in every cell its own square touches; one side grows by 1 m and the other shrinks by 0.25 m (so a zone's
    //    heightmap asks or is filed in exactly one cell). The 0.75 m left over covers the float rounding of the game's
    //    comparison (x + reach against centre - half-size), at most 0.125 m with positions and reaches within 1,000 km;
    //    beyond that, or NaN, an item is offered to every lookup and such an asker gets the whole list (for a NaN the
    //    game's test passes everything, so that is exact too). Modifiers are filed whether enabled or not, so the
    //    loop's own enabled check sees every candidate of the square.
    //  - Order: modifiers are applied in s_instances order after the game's sort, and that sort is unstable (equal keys,
    //    e.g. two modifiers of one location at the same spot, can land either way). So the game's own call still sorts
    //    the list exactly when it would, and the index only copies the result: each item remembers its position in the
    //    list ("rank") at the last sync. Every candidate's rank is checked against the list before use (list[rank] is
    //    that item) and anything stale resyncs, so the short list is always in the list's own order.
    // The game's lists stay the source of truth. Membership is re-read from them whenever a hook saw them change
    // (Awake, OnDestroy, IsDistantLod) or their count or last item changed. A destroyed modifier or heightmap is also
    // dropped from the mirror in its own OnDestroy, so the mirror never keeps a finished world alive (logout, world
    // change, far areas unloading) while it waits for its next lookup. Whatever the index cannot answer exactly (a
    // destroyed object or a duplicate in the list, a list another mod substituted) gets the whole list, as the game.
    // TerrainComp (the newer per-zone terrain edits) is applied after the loop and is not touched.
    //
    // Other mods. The game's call that fetches the whole list stays where it is, and the short list is made from what it
    // returns ("call GetAll...(); ldarg.0; call Ours(list, this)"), so other mods' transpilers still find that call, and
    // with the feature off the list passes through unchanged. Our transpiler runs last and redirects only a loop it can
    // vouch for: the list goes straight into the foreach, TerrainVSModifier is called exactly once, and a failed test
    // goes straight to the next item. Any other shape (another mod rewrote the loop or the test) is left as it is and
    // logged. A Harmony patch on TerrainVSModifier itself, or another mod's transpiler sorted after ours (it rewrites
    // the loop after we checked it), makes the lookups hand over the whole list again. Not detectable: MonoMod hooks
    // (MMHOOK On./IL. hooks) are invisible to Harmony; a mod that changes the pairing test that way needs this off.
    //
    // Movement. Pieces and locations never move an enabled modifier (the build ghost's copy is disabled and destroyed
    // in the frame it is made), and Real Portal moves neither modifiers nor zone heightmaps, but nothing forbids a mod
    // to. Every filed position and reach is compared with the live one at the first lookup of each frame, and again at
    // the first lookup from MonoUpdaters' heightmap pass on (the delayed rebuilds that PokeHeightmaps asks for run
    // there), and re-filed if it changed; anything once seen moving is then rechecked on every lookup. The gap left:
    // an enabled modifier that first moves (or changes reach) after one of those checks, when a heightmap its new
    // square reaches is rebuilt before the next check (a Poke(0) right after the move, a heightmap being enabled, or
    // Unity's LateUpdate rebuild after a Poke(1) if that comes before the pass). That heightmap is built without the
    // modifier and stays so until its next rebuild: the next check re-files the modifier but does not rebuild what was
    // already built. We do not repair it with a poke of our own: that would rebuild heightmaps the game does not (in
    // the game a modifier moved without a poke shows up only once something pokes there), and a modifier that keeps
    // moving would rebuild them every frame. Accepted, since nothing in the game or Real Portal moves an enabled one.
    [EngineModule]
    public static class TerrainModifierIndexModule
    {
        static readonly Feature s_feature = Features.Register("TerrainModifierIndex", true,
            "Heightmap rebuilds visit only the terrain modifiers that reach them, and a new or removed modifier only the heightmaps it reaches (spatial grid), instead of all of them. Same result and order as the game.");
        static readonly Feature s_verify = Features.Register("TerrainModifierIndexVerify", false,
            "Debug: also run the game's full scan beside every indexed lookup and log any difference. Slow; changes nothing in game.");

        // The grid. A zone heightmap (64 m, centred on a multiple of 64) is exactly one cell.
        const double CellSize = 64.0;
        // Beyond this the float spacing of positions (0.0625 m at 1,000 km, 0.125 m for sums up to 2,000 km) starts to
        // eat the slack.
        const float Limit = 1e6f;
        // A square wider than this many cells (2 km) is offered to every lookup instead of being filed.
        const int MaxSpan = 32;
        // One side of every pairing grows, the other shrinks; together 0.75 m of slack (see above).
        const float Grow = 1f;
        const float Shrink = 0.25f;
        // After this many unexpected failures an index stops answering until the feature is switched: its lookups get
        // the whole list, as the game. A failure that comes back on every call would otherwise rebuild the index from
        // scratch before each throw, far slower than the game's plain scan.
        const int MaxFailures = 3;
        // The pass of MonoUpdaters.LateUpdate over Heightmap.Instances: the delayed rebuilds (Poke(2)) run there.
        const string HeightmapPass = "MonoUpdaters.LateUpdate.Heightmap";

        // Modifiers are filed by the square the game tests (GetRadius() + 4, TerrainVSModifier) grown by 1 m; a
        // heightmap asks with its own square shrunk by 0.25 m. Heightmaps are filed shrunk; a modifier asks grown.
        static readonly SpatialIndex<TerrainModifier> s_modifiers = new SpatialIndex<TerrainModifier>(ModifierReach, Grow, -Shrink);
        static readonly SpatialIndex<Heightmap> s_heightmaps = new SpatialIndex<Heightmap>(HeightmapReach, -Shrink, Grow);

        static FieldInfo s_modifierListField;
        static FieldInfo s_heightmapListField;
        static List<TerrainModifier> s_modifierList;   // TerrainModifier.s_instances
        static List<Heightmap> s_heightmapList;        // Heightmap.s_heightmaps

        static MethodInfo s_apply, s_poke;
        static bool s_applyRedirected, s_pokeRedirected;
        static MethodInfo s_terrainTest;   // Heightmap.TerrainVSModifier
        // Other mods' patches that take the index out of a loop (see CheckForeign).
        static bool s_testPatched, s_applyTranspiledAfter, s_pokeTranspiledAfter;
        static int s_foreignCheckedAt = -1;

        // The short list handed to the game's loop must not be refilled while that loop still runs, so there is one per
        // nesting level of the patched method (our prefix counts it, our postfix uncounts it). The loops never nest
        // in practice (their bodies do terrain maths and Poke), this only keeps a refill impossible.
        static int s_applyDepth, s_pokeDepth;
        static int s_applyFrame = -1, s_pokeFrame = -1;
        static readonly List<List<TerrainModifier>> s_applyLists = new List<List<TerrainModifier>>();
        static readonly List<List<Heightmap>> s_pokeLists = new List<List<Heightmap>>();

        static long s_verified, s_mismatches;

        static float ModifierReach(TerrainModifier m) => m.GetRadius() + 4f;
        static float HeightmapReach(Heightmap h) => (float)h.m_width * h.m_scale * 0.5f;

        static void Install(Harmony harmony)
        {
            s_modifierListField = AccessTools.Field(typeof(TerrainModifier), "s_instances") ?? throw new MissingFieldException("TerrainModifier", "s_instances");
            s_heightmapListField = AccessTools.Field(typeof(Heightmap), "s_heightmaps") ?? throw new MissingFieldException("Heightmap", "s_heightmaps");
            s_apply = AccessTools.Method(typeof(Heightmap), "ApplyModifiers") ?? throw new MissingMethodException("Heightmap", "ApplyModifiers");
            s_poke = AccessTools.Method(typeof(TerrainModifier), "PokeHeightmaps") ?? throw new MissingMethodException("TerrainModifier", "PokeHeightmaps");
            s_terrainTest = AccessTools.Method(typeof(Heightmap), nameof(Heightmap.TerrainVSModifier)) ?? throw new MissingMethodException("Heightmap", "TerrainVSModifier");

            // Membership hooks first (if one cannot be patched, the lookups are never installed): they flag the mirror
            // for a resync from the game's list at the next lookup, and OnDestroy lets go of the object at once.
            harmony.Patch(AccessTools.Method(typeof(TerrainModifier), "Awake"), postfix: Own(nameof(ModifiersChanged)));
            harmony.Patch(AccessTools.Method(typeof(TerrainModifier), "OnDestroy"), postfix: Own(nameof(ModifierDestroyed)));
            harmony.Patch(AccessTools.Method(typeof(TerrainModifier), nameof(TerrainModifier.RemoveAll)), postfix: Own(nameof(ModifiersCleared)));
            harmony.Patch(AccessTools.Method(typeof(Heightmap), "Awake"), postfix: Own(nameof(HeightmapsChanged)));
            harmony.Patch(AccessTools.Method(typeof(Heightmap), "OnDestroy"), postfix: Own(nameof(HeightmapDestroyed)));
            harmony.Patch(AccessTools.PropertySetter(typeof(Heightmap), nameof(Heightmap.IsDistantLod)), postfix: Own(nameof(HeightmapsChanged)));

            // Nothing is filed while off (no sweeps either), so switching on starts from a fresh read of the lists;
            // switching off lets go of everything. Either way an index that gave up after failures gets another chance.
            s_feature.Switched += _ =>
            {
                ResetIndexes();
                s_modifiers.Failures = 0;
                s_heightmaps.Failures = 0;
            };

            // Our transpilers only add a filter after the game's "get the whole list" call, and run last, so that other
            // mods' transpilers still see the method as the game wrote it. The prefix/postfix pair only counts nesting
            // and empties our short lists afterwards (no try/catch is added around the game's method).
            harmony.Patch(s_apply, prefix: Own(nameof(ApplyEnter)), postfix: Own(nameof(ApplyLeave)), transpiler: Own(nameof(ApplyModifiersTranspiler), Priority.Last));
            harmony.Patch(s_poke, prefix: Own(nameof(PokeEnter)), postfix: Own(nameof(PokeLeave)), transpiler: Own(nameof(PokeHeightmapsTranspiler), Priority.Last));

            if (!s_applyRedirected || !s_pokeRedirected)
                EnginePlugin.Log.LogWarning($"TerrainModifierIndex: ApplyModifiers {(s_applyRedirected ? "indexed" : "left to the game")}, PokeHeightmaps {(s_pokeRedirected ? "indexed" : "left to the game")}");

            // Optional: without it, a modifier that first moves in Update is re-filed at the next frame's first lookup
            // instead of before this frame's delayed rebuilds.
            try
            {
                harmony.Patch(AccessTools.Method(typeof(MonoUpdatersExtra), nameof(MonoUpdatersExtra.CustomLateUpdate)), prefix: Own(nameof(UpdaterPass)));
            }
            catch (Exception e)
            {
                EnginePlugin.Log.LogWarning($"TerrainModifierIndex: no recheck before MonoUpdaters' heightmap pass: {e.Message}");
            }
        }

        static HarmonyMethod Own(string name, int priority = -1)
        {
            var m = new HarmonyMethod(AccessTools.Method(typeof(TerrainModifierIndexModule), name));
            if (priority >= 0)
                m.priority = priority;
            return m;
        }

        static void ResetIndexes()
        {
            s_modifiers.Reset();
            s_heightmaps.Reset();
        }

        static void ModifiersChanged() => s_modifiers.MarkDirty();
        static void HeightmapsChanged() => s_heightmaps.MarkDirty();

        // The game has just taken it out of its list. Let go of it now rather than at the next lookup: the heightmap
        // mirror is only asked when a modifier appears or goes, which after a logout may not happen before the next
        // world, and the destroyed shells would keep their heights, build data and listeners alive until then.
        static void ModifierDestroyed(TerrainModifier __instance)
        {
            try
            {
                s_modifiers.Forget(__instance);
            }
            catch (Exception e)
            {
                Fail(s_modifiers, "TerrainModifier.OnDestroy", e);
            }
        }

        static void HeightmapDestroyed(Heightmap __instance)
        {
            try
            {
                s_heightmaps.Forget(__instance);
            }
            catch (Exception e)
            {
                Fail(s_heightmaps, "Heightmap.OnDestroy", e);
            }
        }

        // The game's list is empty now, so nothing filed is in it any more.
        static void ModifiersCleared() => s_modifiers.Reset();

        // MonoUpdatersExtra.CustomLateUpdate, called for each pass of MonoUpdaters.LateUpdate. Before the heightmap pass
        // the next lookup rechecks every filed modifier again, so one that first moved in Update after the frame's first
        // lookup is filed at its new square before the delayed rebuilds run. Costs one more sweep (about one game scan)
        // in a frame that has lookups both before and during the pass.
        static void UpdaterPass(string profileScope)
        {
            if (profileScope == HeightmapPass)
                s_modifiers.Rearm();
        }

        // Our prefix counts the nesting level and hands it to our postfix (__state), which sets the count back to what
        // it was before the call. If the game's method throws, the postfix is skipped and the level stays counted: that
        // only makes later calls take a deeper list (never one in use), and the first call of the next frame starts
        // from zero again, since a call never spans frames. If another mod's prefix keeps ours from running, __state
        // is 0 and the postfix does nothing. When the outermost call ends no loop of ours is running, so the short lists
        // are emptied: they hold no modifier or heightmap between calls.
        static void ApplyEnter(out int __state)
        {
            int frame = Time.frameCount;
            if (frame != s_applyFrame)
            {
                s_applyFrame = frame;
                s_applyDepth = 0;
            }
            __state = ++s_applyDepth;
        }

        static void ApplyLeave(int __state)
        {
            if (__state <= 0)
                return;
            s_applyDepth = __state - 1;
            if (s_applyDepth == 0)
                Empty(s_applyLists);
        }

        static void PokeEnter(out int __state)
        {
            int frame = Time.frameCount;
            if (frame != s_pokeFrame)
            {
                s_pokeFrame = frame;
                s_pokeDepth = 0;
            }
            __state = ++s_pokeDepth;
        }

        static void PokeLeave(int __state)
        {
            if (__state <= 0)
                return;
            s_pokeDepth = __state - 1;
            if (s_pokeDepth == 0)
                Empty(s_pokeLists);
        }

        static void Empty<T>(List<List<T>> pool)
        {
            for (int i = 0; i < pool.Count; i++)
                if (pool[i].Count > 0)
                    pool[i].Clear();
        }

        static IEnumerable<CodeInstruction> ApplyModifiersTranspiler(IEnumerable<CodeInstruction> code) =>
            Redirect(code, AccessTools.Method(typeof(TerrainModifier), nameof(TerrainModifier.GetAllInstances)),
                AccessTools.Method(typeof(TerrainModifierIndexModule), nameof(ModifiersFor)),
                typeof(List<TerrainModifier>), typeof(List<TerrainModifier>.Enumerator), "Heightmap.ApplyModifiers", ref s_applyRedirected);

        static IEnumerable<CodeInstruction> PokeHeightmapsTranspiler(IEnumerable<CodeInstruction> code) =>
            Redirect(code, AccessTools.Method(typeof(Heightmap), nameof(Heightmap.GetAllHeightmaps)),
                AccessTools.Method(typeof(TerrainModifierIndexModule), nameof(HeightmapsFor)),
                typeof(List<Heightmap>), typeof(List<Heightmap>.Enumerator), "TerrainModifier.PokeHeightmaps", ref s_pokeRedirected);

        // "call GetAll...()" gets "ldarg.0; call Ours(list, this)" after it: the same stack result, the game's call and
        // the rest of the method untouched. Only for the loop the index can stand in for (see Shape); anything else is
        // left as it is.
        static List<CodeInstruction> Redirect(IEnumerable<CodeInstruction> code, MethodInfo getter, MethodInfo filter, Type listType,
            Type enumeratorType, string where, ref bool done)
        {
            var list = new List<CodeInstruction>(code);
            string problem = Shape(list, getter, listType, enumeratorType, out int at);
            if (problem != null)
            {
                done = false;
                EnginePlugin.Log?.LogWarning($"TerrainModifierIndex: {where} {problem}; another mod changed it, left as it is");
                return list;
            }
            list.InsertRange(at + 1, new[] { new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Call, filter) });
            done = true;
            return list;
        }

        // The index hands the loop the candidates of the square that TerrainVSModifier tests, so the loop must be the
        // game's: the getter's list goes straight into the foreach (no other code gets hold of it), TerrainVSModifier is
        // called once, and when it fails the loop goes straight on to the next item (nothing else can let an item
        // through). The enabled check of ApplyModifiers is not required: the index offers disabled modifiers too, so
        // whatever the loop checks besides the square sees every candidate. Returns what is wrong, or null.
        static string Shape(List<CodeInstruction> list, MethodInfo getter, Type listType, Type enumeratorType, out int at)
        {
            at = -1;
            int hits = 0;
            for (int i = 0; i < list.Count; i++)
                if (list[i].Calls(getter))
                {
                    at = i;
                    hits++;
                }
            if (hits != 1)
                return $"calls {getter.Name} {hits} times (expected once)";
            // ApplyModifiers sets its two height arrays to null in between ("ldnull; stloc"): that leaves the list alone.
            int next = at + 1;
            while (next + 1 < list.Count && list[next].opcode == OpCodes.Ldnull && IsStore(list[next + 1]))
                next += 2;
            if (next >= list.Count || !IsCall(list[next], listType, "GetEnumerator"))
                return $"does not walk the list of {getter.Name} straight away";

            MethodInfo test = s_terrainTest ?? AccessTools.Method(typeof(Heightmap), nameof(Heightmap.TerrainVSModifier));
            int t = -1;
            hits = 0;
            for (int i = 0; i < list.Count; i++)
                if (list[i].Calls(test))
                {
                    t = i;
                    hits++;
                }
            if (hits != 1)
                return $"calls {test.Name} {hits} times (expected once)";
            CodeInstruction branch = t + 1 < list.Count ? list[t + 1] : null;
            if (branch == null || (branch.opcode != OpCodes.Brfalse && branch.opcode != OpCodes.Brfalse_S)
                || branch.operand == null || !IsNextItem(list, branch.operand, enumeratorType))
                return $"does not go on to the next item when {test.Name} fails";
            return null;
        }

        // Harmony's Label and LocalBuilder types do not resolve against netstandard here (see AllocFixesModule), so
        // labels are read through reflection and compared boxed, and stores are recognised by opcode.
        static readonly FieldInfo s_labels = AccessTools.Field(typeof(CodeInstruction), "labels");

        // The label is the foreach's step: "ldloca enumerator; call Enumerator.MoveNext()".
        static bool IsNextItem(List<CodeInstruction> list, object label, Type enumeratorType)
        {
            for (int i = 0; i + 1 < list.Count; i++)
                if (s_labels?.GetValue(list[i]) is System.Collections.IList labels && labels.Contains(label))
                    return (list[i].opcode == OpCodes.Ldloca || list[i].opcode == OpCodes.Ldloca_S) && IsCall(list[i + 1], enumeratorType, "MoveNext");
            return false;
        }

        static bool IsStore(CodeInstruction ci) =>
            ci.opcode == OpCodes.Stloc || ci.opcode == OpCodes.Stloc_S || ci.opcode == OpCodes.Stloc_0
            || ci.opcode == OpCodes.Stloc_1 || ci.opcode == OpCodes.Stloc_2 || ci.opcode == OpCodes.Stloc_3;

        static bool IsCall(CodeInstruction ci, Type declaringType, string name) =>
            (ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) && ci.operand is MethodInfo m && m.Name == name && m.DeclaringType == declaringType;

        // In Heightmap.ApplyModifiers, right after the game's TerrainModifier.GetAllInstances() (which sorts the list
        // when due, exactly where the game does): returns the list the loop walks.
        static List<TerrainModifier> ModifiersFor(List<TerrainModifier> all, Heightmap hmap)
        {
            if (!s_feature.On || s_modifiers.Latched)
                return all;
            try
            {
                if (s_modifierList == null)
                    s_modifierList = (List<TerrainModifier>)s_modifierListField.GetValue(null);
                if (!ReferenceEquals(all, s_modifierList))
                    return all;
                CheckForeign();
                if (s_testPatched || s_applyTranspiledAfter)
                    return all;
                Vector3 c = hmap.transform.position;
                List<TerrainModifier> near = Scratch(s_applyLists, s_applyDepth);
                if (!s_modifiers.Query(all, c.x, c.z, HeightmapReach(hmap), near))
                    return all;
                if (s_verify.On)
                    VerifyApply(hmap, all, near);
                return near;
            }
            catch (Exception e)
            {
                Fail(s_modifiers, "ApplyModifiers", e);
                return all;
            }
        }

        // In TerrainModifier.PokeHeightmaps, right after the game's Heightmap.GetAllHeightmaps().
        static List<Heightmap> HeightmapsFor(List<Heightmap> all, TerrainModifier modifier)
        {
            if (!s_feature.On || s_heightmaps.Latched)
                return all;
            try
            {
                if (s_heightmapList == null)
                    s_heightmapList = (List<Heightmap>)s_heightmapListField.GetValue(null);
                if (!ReferenceEquals(all, s_heightmapList))
                    return all;
                CheckForeign();
                if (s_testPatched || s_pokeTranspiledAfter)
                    return all;
                Vector3 p = modifier.transform.position;
                List<Heightmap> near = Scratch(s_pokeLists, s_pokeDepth);
                if (!s_heightmaps.Query(all, p.x, p.z, ModifierReach(modifier), near))
                    return all;
                if (s_verify.On)
                    VerifyPoke(modifier, all, near);
                return near;
            }
            catch (Exception e)
            {
                Fail(s_heightmaps, "PokeHeightmaps", e);
                return all;
            }
        }

        // The index stands in for the game's square test (TerrainVSModifier) inside loops our transpiler checked. If
        // another mod patches that test, its reach is no longer the square filed here; if another mod's transpiler on
        // one of the two methods is sorted after ours, it rewrites the loop after we checked it. Either way that loop
        // gets the whole list again. Checked at the first lookup and then every 1000 frames (mods patch while loading,
        // long before a world is up).
        static void CheckForeign()
        {
            int frame = Time.frameCount;
            if (s_foreignCheckedAt >= 0 && frame >= s_foreignCheckedAt && frame - s_foreignCheckedAt < 1000)
                return;
            s_foreignCheckedAt = frame;
            bool test = false;
            HarmonyLib.Patches info = Harmony.GetPatchInfo(s_terrainTest);
            if (info != null)
                foreach (string owner in info.Owners)
                    if (owner != EnginePlugin.Guid)
                        test = true;
            bool apply = TranspiledAfterUs(s_apply);
            bool poke = TranspiledAfterUs(s_poke);
            if (test && !s_testPatched)
                EnginePlugin.Log.LogWarning("TerrainModifierIndex: Heightmap.TerrainVSModifier is patched by another mod, the game's full scans are used");
            if (apply && !s_applyTranspiledAfter)
                EnginePlugin.Log.LogWarning("TerrainModifierIndex: another mod's transpiler on Heightmap.ApplyModifiers runs after ours, the game's full scan is used there");
            if (poke && !s_pokeTranspiledAfter)
                EnginePlugin.Log.LogWarning("TerrainModifierIndex: another mod's transpiler on TerrainModifier.PokeHeightmaps runs after ours, the game's full scan is used there");
            s_testPatched = test;
            s_applyTranspiledAfter = apply;
            s_pokeTranspiledAfter = poke;
        }

        // Harmony's order: before/after lists first, then higher priority first, then the earlier patch first.
        static bool TranspiledAfterUs(MethodBase method)
        {
            HarmonyLib.Patches info = Harmony.GetPatchInfo(method);
            if (info == null)
                return false;
            Patch ours = null;
            foreach (Patch p in info.Transpilers)
                if (p.owner == EnginePlugin.Guid)
                {
                    ours = p;
                    break;
                }
            if (ours == null)
                return false;   // not redirected: the loop is the game's (or another mod's) as it stands
            foreach (Patch p in info.Transpilers)
            {
                if (p.owner == EnginePlugin.Guid)
                    continue;
                bool before = p.before != null && Array.IndexOf(p.before, EnginePlugin.Guid) >= 0;
                bool after = p.after != null && Array.IndexOf(p.after, EnginePlugin.Guid) >= 0;
                if (after || (!before && (p.priority < ours.priority || (p.priority == ours.priority && p.index > ours.index))))
                    return true;
            }
            return false;
        }

        static List<T> Scratch<T>(List<List<T>> pool, int depth)
        {
            if (depth <= 0 || depth > 8)
                return new List<T>();   // called outside our prefix, or absurdly deep: a list nobody else holds
            while (pool.Count < depth)
                pool.Add(new List<T>());
            return pool[depth - 1];
        }

        // Something unexpected: the game gets its whole list (exactly its own behaviour) and that index starts over.
        // The other index has nothing to do with it and keeps what it has.
        static void Fail<T>(SpatialIndex<T> index, string where, Exception e) where T : Component
        {
            index.Reset();
            int n = ++index.Failures;
            if (n <= MaxFailures)
                EnginePlugin.Log.LogError($"TerrainModifierIndex: {where} failed, the game's full list is used and the index starts over: {e}");
            if (n == MaxFailures)
                EnginePlugin.Log.LogWarning($"TerrainModifierIndex: {n} failures, the {(ReferenceEquals(index, s_modifiers) ? "modifier" : "heightmap")} lookups use the game's full scan until the feature is switched");
        }

        // Kept out of the lookups so that their lambdas (closures over the asker) are only allocated while verifying.
        static void VerifyApply(Heightmap hmap, List<TerrainModifier> all, List<TerrainModifier> near) =>
            Verify(all, near, m => m.enabled && hmap.TerrainVSModifier(m), "ApplyModifiers", hmap);

        static void VerifyPoke(TerrainModifier modifier, List<Heightmap> all, List<Heightmap> near) =>
            Verify(all, near, h => h.TerrainVSModifier(modifier), "PokeHeightmaps", modifier);

        // The game's loop picks (with its own test) from the whole list and from the short one: the picks must be the
        // same items in the same order.
        static void Verify<T>(List<T> all, List<T> near, Func<T, bool> pick, string where, Component asker) where T : Component
        {
            try
            {
                int j = 0, hits = 0;
                bool same = true;
                for (int i = 0; i < near.Count && same; i++)
                {
                    if (!pick(near[i]))
                        continue;
                    while (j < all.Count && !pick(all[j]))
                        j++;
                    same = j < all.Count && ReferenceEquals(all[j], near[i]);
                    j++;
                    hits++;
                }
                if (same)
                {
                    while (j < all.Count && !pick(all[j]))
                        j++;
                    same = j >= all.Count;   // no hit of the whole list left out
                }
                s_verified++;
                if (!same && s_mismatches++ < 20)
                    EnginePlugin.Log.LogError($"TerrainModifierIndex verify: {where} for {asker.name} at {asker.transform.position} differs " +
                        $"(short list {near.Count} of {all.Count}, {hits} picks matched before the difference)");
            }
            catch (Exception e)
            {
                EnginePlugin.Log.LogWarning($"TerrainModifierIndex verify: {e.Message}");
            }
        }

        // For the lab (by reflection or the console): lookups, average short list vs whole list, fallbacks, resyncs, moves.
        public static string Stats() =>
            $"TerrainModifierIndex {(s_feature.On ? "on" : "off")}: modifiers [{s_modifiers.Describe()}]; heightmaps [{s_heightmaps.Describe()}]; " +
            $"verify {s_verified} checked, {s_mismatches} differ; redirects apply={s_applyRedirected} poke={s_pokeRedirected}; " +
            $"foreign test={s_testPatched} apply={s_applyTranspiledAfter} poke={s_pokeTranspiledAfter}";

        public static void ResetStats()
        {
            s_modifiers.ResetStats();
            s_heightmaps.ResetStats();
            s_verified = s_mismatches = 0;
        }

        // A mirror of one of the game's lists, filed in grid cells by each item's square (x, z) ± reach.
        sealed class SpatialIndex<T> where T : Component
        {
            sealed class Entry
            {
                internal T Item;
                internal Transform Tr;
                internal float X, Z, Reach;             // what it is filed by
                internal int MinX, MinZ, MaxX, MaxZ;    // its cells
                internal bool Everywhere;               // no usable square: offered to every lookup
                internal bool Mover;                    // seen moving once: rechecked on every lookup
                internal int Slot;                      // its index in _all
                internal int Rank;                      // its index in the game's list at the last sync
                internal int Seen, Mark;                // stamps of the last sync / lookup that met it
            }

            sealed class ByRank : IComparer<Entry>
            {
                public int Compare(Entry a, Entry b) => a.Rank.CompareTo(b.Rank);
            }

            // Unity objects compare by instance id and may be destroyed; the mirror only ever needs the same reference.
            sealed class ByReference : IEqualityComparer<T>
            {
                public bool Equals(T a, T b) => ReferenceEquals(a, b);
                public int GetHashCode(T o) => RuntimeHelpers.GetHashCode(o);
            }

            static readonly ByRank s_byRank = new ByRank();

            readonly Func<T, float> _reach;
            readonly float _fileGrow, _askGrow;
            readonly Dictionary<T, Entry> _byItem = new Dictionary<T, Entry>(new ByReference());
            readonly List<Entry> _all = new List<Entry>();
            readonly Dictionary<long, List<Entry>> _cells = new Dictionary<long, List<Entry>>();
            readonly Stack<List<Entry>> _spareCells = new Stack<List<Entry>>();
            readonly List<Entry> _everywhere = new List<Entry>();
            readonly List<Entry> _movers = new List<Entry>();
            readonly List<Entry> _found = new List<Entry>();
            bool _dirty = true;
            int _listCount = -1;
            T _listLast;
            int _sweptFrame = -1;
            int _seen, _mark;
            long _lookups, _offered, _listed, _fallbacks, _syncs, _moves, _forgotten;

            // Unexpected failures since the feature was last switched; at MaxFailures the lookups stop using the index.
            internal int Failures;
            internal bool Latched => Failures >= MaxFailures;

            internal SpatialIndex(Func<T, float> reach, float fileGrow, float askGrow)
            {
                _reach = reach;
                _fileGrow = fileGrow;
                _askGrow = askGrow;
            }

            internal void MarkDirty() => _dirty = true;

            // The next lookup rechecks every filed position and reach, even if this frame's sweep was done.
            internal void Rearm() => _sweptFrame = -1;

            internal void Reset()
            {
                _byItem.Clear();
                _all.Clear();
                _everywhere.Clear();
                _movers.Clear();
                _found.Clear();
                foreach (List<Entry> cell in _cells.Values)
                {
                    cell.Clear();
                    _spareCells.Push(cell);
                }
                _cells.Clear();
                _dirty = true;
                _listCount = -1;
                _listLast = null;
                _sweptFrame = -1;
            }

            // The item is being destroyed and has left the game's list: drop it now (the next lookup resyncs anyway).
            internal void Forget(T item)
            {
                _dirty = true;
                if ((object)item == null)
                    return;
                if (ReferenceEquals(item, _listLast))
                    _listLast = null;
                if (_byItem.TryGetValue(item, out Entry e))
                {
                    Remove(e);
                    _forgotten++;
                }
            }

            internal void ResetStats() => _lookups = _offered = _listed = _fallbacks = _syncs = _moves = _forgotten = 0;

            internal string Describe()
            {
                double n = Math.Max(1, _lookups - _fallbacks);
                return $"{_all.Count} filed in {_cells.Count} cells, {_lookups} lookups, {_offered / n:0.0} offered of {_listed / n:0} listed, " +
                    $"{_fallbacks} fallbacks, {_syncs} syncs, {_moves} moves ({_movers.Count} movers, {_everywhere.Count} everywhere), " +
                    $"{_forgotten} dropped at destroy, {Failures} failures{(Latched ? " (gave up)" : "")}";
            }

            // Fills result with the list's items whose square may reach (cx, cz) ± reach, in the list's order.
            // false: no exact answer here; the caller hands the game the whole list.
            internal bool Query(List<T> list, float cx, float cz, float reach, List<T> result)
            {
                result.Clear();
                _lookups++;
                if (!Cells(cx, cz, reach, _askGrow, out int minX, out int minZ, out int maxX, out int maxZ))
                {
                    _fallbacks++;
                    return false;   // the game's test passes everything for a NaN asker: it gets the whole list
                }
                // Hooks catch every change the game makes; the count and the last item (the game only appends) catch
                // most changes made behind their back.
                bool changed = _dirty || list.Count != _listCount || (list.Count > 0 && !ReferenceEquals(list[list.Count - 1], _listLast));
                if (changed && !Sync(list))
                {
                    _fallbacks++;
                    return false;
                }
                int frame = Time.frameCount;
                if (frame != _sweptFrame)
                {
                    _sweptFrame = frame;
                    Sweep(_all);
                }
                else if (_movers.Count > 0)
                    Sweep(_movers);
                Collect(minX, minZ, maxX, maxZ);
                if (!InListOrder(list))
                {
                    // The list was reordered or changed without a hook seeing it (another caller's sort, say): re-read
                    // it and look again.
                    bool again = Sync(list);
                    if (again)
                    {
                        Collect(minX, minZ, maxX, maxZ);
                        again = InListOrder(list);
                    }
                    if (!again)
                    {
                        _found.Clear();
                        _fallbacks++;
                        return false;
                    }
                }
                SortByRank();
                for (int i = 0; i < _found.Count; i++)
                    result.Add(_found[i].Item);
                _found.Clear();   // holds nothing between lookups
                _offered += result.Count;
                _listed += list.Count;
                return true;
            }

            // Re-reads membership and ranks from the game's list. false: the list holds something the game's own loop
            // would trip on (a destroyed object) or would visit twice; then the game gets the whole list.
            bool Sync(List<T> list)
            {
                _syncs++;
                int seen = ++_seen;
                for (int i = 0; i < list.Count; i++)
                {
                    T item = list[i];
                    UnityEngine.Object alive = item;
                    if (alive == null)
                        return Unsynced();
                    if (_byItem.TryGetValue(item, out Entry e))
                    {
                        if (e.Seen == seen)
                            return Unsynced();
                    }
                    else
                        e = Add(item);
                    e.Seen = seen;
                    e.Rank = i;
                }
                if (_all.Count != list.Count)
                    for (int i = _all.Count - 1; i >= 0; i--)
                        if (_all[i].Seen != seen)
                            Remove(_all[i]);
                _dirty = false;
                _listCount = list.Count;
                _listLast = list.Count > 0 ? list[list.Count - 1] : null;
                return true;
            }

            bool Unsynced()
            {
                _dirty = true;
                return false;
            }

            Entry Add(T item)
            {
                var e = new Entry { Item = item, Tr = item.transform, Slot = _all.Count };
                Vector3 p = e.Tr.position;
                e.X = p.x;
                e.Z = p.z;
                e.Reach = _reach(item);
                _all.Add(e);
                _byItem.Add(item, e);
                File(e);
                return e;
            }

            void Remove(Entry e)
            {
                Unfile(e);
                int last = _all.Count - 1;
                Entry moved = _all[last];
                _all[e.Slot] = moved;
                moved.Slot = e.Slot;
                _all.RemoveAt(last);
                _byItem.Remove(e.Item);
                if (e.Mover)
                    _movers.Remove(e);
            }

            void File(Entry e)
            {
                if (!Cells(e.X, e.Z, e.Reach, _fileGrow, out e.MinX, out e.MinZ, out e.MaxX, out e.MaxZ)
                    || e.MaxX - e.MinX >= MaxSpan || e.MaxZ - e.MinZ >= MaxSpan)
                {
                    e.Everywhere = true;
                    _everywhere.Add(e);
                    return;
                }
                e.Everywhere = false;
                for (int x = e.MinX; x <= e.MaxX; x++)
                    for (int z = e.MinZ; z <= e.MaxZ; z++)
                    {
                        long key = Key(x, z);
                        if (!_cells.TryGetValue(key, out List<Entry> cell))
                        {
                            cell = _spareCells.Count > 0 ? _spareCells.Pop() : new List<Entry>();
                            _cells.Add(key, cell);
                        }
                        cell.Add(e);
                    }
            }

            void Unfile(Entry e)
            {
                if (e.Everywhere)
                {
                    _everywhere.Remove(e);
                    return;
                }
                for (int x = e.MinX; x <= e.MaxX; x++)
                    for (int z = e.MinZ; z <= e.MaxZ; z++)
                    {
                        long key = Key(x, z);
                        if (!_cells.TryGetValue(key, out List<Entry> cell))
                            continue;
                        int i = cell.IndexOf(e);
                        if (i < 0)
                            continue;
                        int last = cell.Count - 1;
                        cell[i] = cell[last];
                        cell.RemoveAt(last);
                        if (last == 0)
                        {
                            _cells.Remove(key);
                            _spareCells.Push(cell);
                        }
                    }
            }

            // Re-files whatever moved or changed reach since it was filed.
            void Sweep(List<Entry> entries)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    Entry e = entries[i];
                    UnityEngine.Object alive = e.Tr;
                    if (alive == null)
                        continue;   // destroyed: its OnDestroy dropped it or flagged a resync that will
                    Vector3 p = e.Tr.position;
                    float reach = _reach(e.Item);
                    if (Same(p.x, e.X) && Same(p.z, e.Z) && Same(reach, e.Reach))
                        continue;
                    Unfile(e);
                    e.X = p.x;
                    e.Z = p.z;
                    e.Reach = reach;
                    File(e);
                    _moves++;
                    if (!e.Mover)
                    {
                        e.Mover = true;
                        _movers.Add(e);
                    }
                }
            }

            void Collect(int minX, int minZ, int maxX, int maxZ)
            {
                _found.Clear();
                int mark = ++_mark;
                for (int i = 0; i < _everywhere.Count; i++)
                {
                    _everywhere[i].Mark = mark;
                    _found.Add(_everywhere[i]);
                }
                long span = (long)(maxX - minX + 1) * (maxZ - minZ + 1);
                if (span > _cells.Count)
                {
                    // A big asker (the distant terrain meshes are 800 m): walking the entries costs less than its cells.
                    for (int i = 0; i < _all.Count; i++)
                    {
                        Entry e = _all[i];
                        if (!e.Everywhere && e.MinX <= maxX && e.MaxX >= minX && e.MinZ <= maxZ && e.MaxZ >= minZ)
                            _found.Add(e);
                    }
                    return;
                }
                for (int x = minX; x <= maxX; x++)
                    for (int z = minZ; z <= maxZ; z++)
                    {
                        if (!_cells.TryGetValue(Key(x, z), out List<Entry> cell))
                            continue;
                        for (int i = 0; i < cell.Count; i++)
                        {
                            Entry e = cell[i];
                            if (e.Mark == mark)
                                continue;
                            e.Mark = mark;
                            _found.Add(e);
                        }
                    }
            }

            // Every candidate still sits at its rank in the game's list: then rank order is the list's order.
            bool InListOrder(List<T> list)
            {
                for (int i = 0; i < _found.Count; i++)
                {
                    Entry e = _found[i];
                    if (e.Rank >= list.Count || !ReferenceEquals(list[e.Rank], e.Item))
                        return false;
                }
                return true;
            }

            // Ranks are distinct, so any sort gives the one order.
            void SortByRank()
            {
                int n = _found.Count;
                if (n > 24)
                {
                    _found.Sort(s_byRank);
                    return;
                }
                for (int i = 1; i < n; i++)
                {
                    Entry e = _found[i];
                    int j = i - 1;
                    while (j >= 0 && _found[j].Rank > e.Rank)
                    {
                        _found[j + 1] = _found[j];
                        j--;
                    }
                    _found[j + 1] = e;
                }
            }

            // The cells a square (x, z) ± (reach + grow) touches. false for no usable square: NaN, infinite, or so far
            // out that float spacing nears the slack.
            static bool Cells(float x, float z, float reach, float grow, out int minX, out int minZ, out int maxX, out int maxZ)
            {
                minX = minZ = maxX = maxZ = 0;
                if (!(Math.Abs(x) <= Limit && Math.Abs(z) <= Limit && Math.Abs(reach) <= Limit))
                    return false;
                // A negative half-size (only if someone sets one) is a point; the game's test then needs the centres
                // even closer, so the slack still holds.
                double r = Math.Max(0.0, (double)reach + grow);
                minX = Cell(x - r);
                maxX = Cell(x + r);
                minZ = Cell(z - r);
                maxZ = Cell(z + r);
                return true;
            }

            // Cell k spans [64k - 32, 64k + 32): a zone heightmap shrunk by 0.25 m falls in exactly one.
            static int Cell(double v) => (int)Math.Floor((v + CellSize * 0.5) / CellSize);

            static long Key(int x, int z) => ((long)x << 32) | (uint)z;

            static bool Same(float a, float b) => a == b || (float.IsNaN(a) && float.IsNaN(b));
        }
    }
}
