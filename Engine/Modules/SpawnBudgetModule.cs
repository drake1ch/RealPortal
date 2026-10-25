using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace RealPortal.Engine
{
    // Object streaming spikes. 30 times a second (every frame below 30 fps) ZNetScene.CreateDestroyObjects lists the
    // ZDOs around the reference position and
    //  - CreateObjectsSorted copies every near ZDO that has no instance yet, sorts all of them (type first, then
    //    distance) and instantiates max(pending / 100, 10) of them (100 on the loading screen) with no time limit:
    //    arriving at or walking into a 10-30k-piece base, or a seamless Real Portal crossing (no loading screen to hide
    //    it), gives 100-300 Instantiates per pass for seconds, est. 15-90 ms frames, plus the full sort of every
    //    pending ZDO on every pass. Distant objects (CreateDistantObjects) only come when that pass created few near;
    //  - RemoveObjects destroys every instance that left the lists in one call: leaving such a base is one frame with
    //    thousands of Object.Destroy (each piece's OnDestroy also does an O(n) delegate removal on its heightmap).
    //
    // SpawnBudget (creation). Prefixes (Priority.Last) on CreateObjectsSorted and CreateDistantObjects replace the two
    // bodies while it runs:
    //  - every pass still walks the game's near list and writes ZDO.m_tempSortValue for every ZDO without an instance
    //    exactly as the game does, but keeps only the best K of them in a bounded heap (partial selection, the game's
    //    own order: higher ZDO.Type first, then nearer), sorts those K, and creates them in that order under a
    //    time budget per frame (BudgetMs 3 ms; ArrivalBudgetMs 8 ms while something within ArrivalRadius 32 m of the
    //    reference position is still missing; LoadingBudgetMs 50 ms on the loading screen, which is the game's own test:
    //    no local player, or the local player teleporting, i.e. waiting for IsAreaReady). K follows the measured
    //    cost per object so one pass's selection lasts until the next one;
    //  - frames between passes (above 30 fps) go on creating from that selection under the same per-frame budget, so
    //    the work is spread over every frame instead of piled on every other or every fourth one. Each entry is checked
    //    again before it is made (same ZDO id, still valid, still without an instance, zone ready for its type: the
    //    game's per-object test, made when it visits the object), and nothing is made between passes once the
    //    reference zone changed, the active area is not loaded, or no pass ran for 0.25 s (another mod skipping the
    //    pass): the next pass decides from fresh lists;
    //  - distant objects (in the game's list order, as the game) come once the near selection held every near
    //    object still to make and all of it is done; the game's own rule only differs in small trickles (one distant
    //    object per pass alongside a small near backlog);
    //  - a ZDO whose prefab this game does not have still goes through CreateObject every pass as in the game (the
    //    game logs it; on the server the game takes it over and destroys it, and so does this), but outside the
    //    selection and outside the frame's budget, so on a client that lacks a mod such ZDOs can neither fill the
    //    selection nor use up the time, and cannot starve the rest;
    //  - CreateObject itself is the game's (through its patches: Path of Valheiman's prefix and finalizer, Real
    //    Portal's postfix), its exceptions propagate out of the pass exactly as the game's would (Path of Valheiman's
    //    CreateDestroyObjects finalizer handles them); between passes they are caught, logged and the object is left
    //    to the next pass.
    // The set of objects the game ends up with is the game's: every near ZDO with a known prefab whose zone is ready
    // for its type (all of them once the active area is loaded) and every distant one; only when each appears changes.
    // SpawnBudgetVerify checks that set after settling.
    //
    // DespawnBudget (removal). A transpiler (Priority.Last) on RemoveObjects swaps one call, the
    // Object.Destroy(view.gameObject) of each removed view, for Retire(view, zdo). Everything else in that loop stays
    // the game's and stays synchronous: the view's ResetZDO, DestroyZDO of a non-persistent owned ZDO, the removal from
    // ZNetScene.m_instances (so Real Portal's KeepAlive prefix, Path of Valheiman's sweep and SectorInstanceIndex's
    // finalizer see exactly the game's table). Retire hands the GameObject to a queue; a finalizer on RemoveObjects
    // deactivates the whole batch before the call returns (so nothing of it simulates, collides, renders or plays
    // past this frame; its OnDisable callbacks run here, after the game's loop instead of at the end of the frame)
    // and takes it out of what the game's OnDestroy would take it out of at the end of this frame:
    //  - the registries the game's sweeps walk: WearNTear.s_allInstances (WearNTearUpdater, 5-100 visits a frame),
    //    SlowUpdate's list (SlowUpdater, 100 a frame), Piece.s_allPieces and s_allComfortPieces. The same swap-remove
    //    their OnDestroy does, with the index left at -1 so OnDestroy skips that part later. Waiting pieces would
    //    otherwise take sweep slots: a 20k-piece queue next to 5k live pieces stretches the wear sweep about five
    //    times (UpdateWear's snow/event damage and support checks of the player's own pieces that much rarer), and
    //    Piece.CheckClusteredBuildPieceStats (every 20th placement, written to the profile) would count them. The
    //    lists now hold after this frame exactly what the game's hold. The engine's WearNTearActive files are told the
    //    same way (its OnDestroy hook, called early), as their active set reaches past the loaded area, and so are
    //    SlowUpdaterBudget's zone buckets (its SlowUpdate.OnDestroy hook, called early): its near pass walks those
    //    buckets, not the game's list, and would otherwise still visit a waiting object;
    //  - Invoke timers: deactivation stops coroutines but not Invoke/InvokeRepeating, which only Destroy (or
    //    CancelInvoke) ends. Components whose type starts InvokeRepeating (found once per type from its IL: Sign,
    //    Fermenter, Pickable, MineRock5, Container, Fireplace, MaterialVariation, ...; StaticPhysics only while it
    //    falls, its one timer) get CancelInvoke(), so no timer of a waiting object runs on a view without a ZDO
    //    (Sign.UpdateText and Fermenter.SlowUpdate would throw every 2 s, others would ray-cast or allocate). One-shot
    //    Invokes (WearNTear's 0.2 s highlight reset) are left: at most one harmless call. Lab: SpawnBudgetVerify
    //    counts queued objects still invoking; LabDeactivateMaxMs shows what the cancels add to the removal frame.
    // The queue is destroyed oldest first with DestroyImmediate under a per-frame budget, so OnDestroy and the native
    // teardown are spread: DestroyBudgetMs 2 ms (50 ms on the loading screen), x4 once the oldest waits 2 s, and more
    // (up to DrainCatchUpMaxMs 16 ms) when at the measured cost per object the backlog would not be gone before the
    // oldest waits DrainAllAge 10 s. An entry that has waited that long is destroyed whatever the budget, but only
    // entries that old, and never more in one frame than the largest batch a single RemoveObjects call has handed over
    // this session, less what the game destroys in that frame anyway: no frame destroys more than the game's worst
    // removal frame did. While such overdue entries remain, new removals are destroyed the game's way (end of frame),
    // so inflow above what the queue can absorb is the game's behaviour instead of a growing pile. Waits run on a clock
    // that advances at most MaxClockStep (0.5 s) per frame, so a stall (a long save on the host) does not make the
    // whole queue overdue at once. A ZDO that comes back while its old GameObject still waits has that GameObject
    // destroyed first (prefix on CreateObject), so the two never coexist.
    // Not deferred (destroyed at once, the game's call): a GameObject already inactive when it is removed (another mod
    // froze it, as Real Portal's sleeping far areas do, and may set it active again later: in the game it would be
    // gone by then); prefabs with a Character (AI targets and players stay in Character.s_characters until
    // OnDestroy), anything that shapes terrain or loads content (TerrainModifier, TerrainComp, LocationProxy,
    // DungeonGenerator), registries the owner's simulation consults until OnDestroy without a validity check
    // (EffectArea, CraftingStation, Fire, ShieldGenerator, spawners, WispSpawner (LuredWisp's owner-side
    // GetBestSpawner), ItemDrop's count, Vine's duplicate check), and ZDO types Prioritized and Terrain. What is
    // deferred (pieces, trees, rocks, pickables) waits deactivated, without its ZDO, out of the game's table and the
    // registries above: the game's own code already treats such a view as gone (m_nview.IsValid() is false), as it
    // does for the rest of the frame in which the game destroys it. Residual: a reference someone holds to it stays
    // non-null until it is destroyed, where the game's turns null at the end of the frame. The one found on an
    // owner-side path is MonsterAI.m_targetStatic (every piece is a StaticTarget): a monster at the edge of the active
    // area may keep heading for a piece that just left it until its next target update (2-6 s) or the drain.
    //
    // Stand-down: any other mod's patch on CreateObjectsSorted / CreateDistantObjects (their bodies are replaced), or
    // another mod's transpiler on RemoveObjects (its IL is not the game's any more), puts that half back to the game's
    // code (checked every 5 s; logged). Path of Valheiman's CreateDestroyObjects prefixes (ServerTick) and finalizer,
    // its CreateObject prefix/finalizer and IsAreaReady postfix, Real Portal's KeepAlive prefix on RemoveObjects (which
    // skips 5 calls of 6 with far areas loaded; nothing is queued then) and SectorInstanceIndex's hooks are untouched.
    // Off: the game's code exactly (the prefixes return true, Retire is the game's Object.Destroy); switching
    // DespawnBudget off destroys what still waits (end of frame), switching SpawnBudget off drops the selection.
    // Client- and host-local: no ZDO write, RPC or ownership changes (the server-side destruction of unknown-prefab
    // ZDOs is the game's, with its log line).
    [EngineModule]
    public static class SpawnBudgetModule
    {
        static readonly Feature s_spawn = Features.Register("SpawnBudget", false,
            "Objects coming into range are made nearest-first (the game's order) under a time budget per frame instead " +
            "of max(pending/100, 10) per 30 Hz pass after a full sort (ZNetScene.CreateObjectsSorted/CreateDistantObjects).");

        static readonly Feature s_despawn = Features.Register("DespawnBudget", false,
            "Objects going out of range leave the game's table at once, as in the game, but are deactivated and then " +
            "destroyed a few per frame instead of all in one frame (ZNetScene.RemoveObjects).");

        static readonly Feature s_verify = Features.Register("SpawnBudgetVerify", false,
            "Lab: once a second checks every near/distant ZDO the game would instantiate has an instance after " +
            "settling, none waits too long, and queued removals are deactivated and never coexist with a new " +
            "instance; counts and logs differences.");

        // ---- tuning (public so the lab can set them by reflection) ---------------------------------------------

        // Creation, milliseconds of CreateObject per frame: normally; while something within ArrivalRadius of the
        // reference position is still missing (a seamless crossing into a base: the floor under the player first); on
        // the loading screen (frames are hidden there, so as fast as possible without freezing the network).
        public static float BudgetMs = 3f;
        public static float ArrivalBudgetMs = 8f;
        public static float ArrivalRadius = 32f;
        public static float LoadingBudgetMs = 50f;
        // Removal, milliseconds of DestroyImmediate per frame (deactivation happens in the removal call itself); the
        // most the budget is raised to so the backlog is gone before its oldest entry is overdue.
        public static float DestroyBudgetMs = 2f;
        public static float DestroyLoadingBudgetMs = 50f;
        public static float DrainCatchUpMaxMs = 16f;

        // At least this many CreateObject calls / destroys per frame, whatever they cost, so the work always moves.
        const int MinCreatePerFrame = 1;
        const int MinDestroyPerFrame = 4;
        // Bounds of the selection size K.
        const int MinSelect = 32;
        const int MaxSelect = 16384;
        // Between passes nothing is made once the last pass is older than this (someone skips the pass).
        const float PassStale = 0.25f;
        const float PatchCheckSeconds = 5f;
        // Removal queue escalation (age of the oldest waiting GameObject, seconds of the drain clock): x4 budget; then
        // destroyed whatever the budget (only entries that old, capped per frame, see Drain).
        const float DrainHurryAge = 2f;
        const float DrainHurryFactor = 4f;
        const float DrainAllAge = 10f;
        // The drain clock advances by the real time between frames, at most this much per frame (a stall is not a wait).
        const float MaxClockStep = 0.5f;
        // Verify: queued objects whose components are looked at (timers, registries) per check.
        const int VerifyDeepPerCheck = 256;
        // Verify: period, how long the creator must have been idle before "missing" counts, and how long a ZDO may
        // stay eligible without an instance before it counts as starved (a 20k-piece base settles in ~30 s at 3 ms).
        const float VerifyPeriod = 1f;
        const float VerifyIdle = 1f;
        const float StarveSeconds = 120f;
        const int LogCap = 20;

        // ---- the game's parts ---------------------------------------------------------------------------------

        static MethodInfo s_sortedMethod, s_distantMethod, s_removeMethod;
        static Func<ZNetScene, ZDO, GameObject> s_createObject;
        static AccessTools.FieldRef<ZoneSystem, Dictionary<Vector2s, List<ZDO>>> s_loadingOf;

        static readonly MethodInfo s_getGameObject = AccessTools.PropertyGetter(typeof(Component), nameof(Component.gameObject));
        static readonly MethodInfo s_objectDestroy = AccessTools.Method(typeof(UnityEngine.Object), nameof(UnityEngine.Object.Destroy), new[] { typeof(UnityEngine.Object) });
        static readonly MethodInfo s_getZdo = AccessTools.Method(typeof(ZNetView), nameof(ZNetView.GetZDO));
        static readonly MethodInfo s_retire = AccessTools.Method(typeof(SpawnBudgetModule), nameof(Retire));
        // CodeInstruction.labels is read by reflection: its element type (Label) is not resolvable from netstandard2.1
        // (see AllocFixesModule).
        static readonly FieldInfo s_labels = AccessTools.Field(typeof(CodeInstruction), "labels");

        static bool s_installed;
        // RemoveObjects' Destroy call was found and swapped in the latest transpiler run.
        static bool s_transpiled;
        // Another mod patches what this replaces (see the header); internal error: the game's code for the session
        // (switching the feature off and on tries again).
        static bool s_createAside, s_destroyAside, s_createGaveUp, s_destroyGaveUp;
        static float s_nextPatchCheck;

        // ---- lab counters ---------------------------------------------------------------------------------------

        // Creation: passes handled; objects made (in a pass, between passes, distant); CreateObject calls that returned
        // nothing (unknown prefab; destroyed on the server as the game does); entries dropped because their ZDO changed
        // or got an instance meanwhile; entries skipped because their zone was not ready for their type; passes whose
        // selection could not hold every pending object; selections that ran dry before the next pass with budget left;
        // exceptions from CreateObject between passes; frames where creation went over its budget.
        public static long LabPasses, LabCreated, LabCreatedInPass, LabCreatedBetween, LabDistantCreated;
        public static long LabNull, LabInvalidDestroyed, LabStale, LabNotReady, LabOverflowPasses, LabRanDry, LabCreateErrors, LabFramesOverBudget;
        // Milliseconds: all CreateObject time, the slowest single object, the most in one frame, the running mean per
        // object; the gather/selection pass (last and slowest).
        public static double LabCreateMs, LabCreateMaxMs, LabFrameCreateMaxMs, LabAvgCreateMs = 0.25, LabGatherMs, LabGatherMaxMs;
        // Milliseconds of CreateObject on unknown-prefab ZDOs (the game's per-object path, not charged to the budget).
        public static double LabInvalidMs;
        // Gauges at the last pass: near objects still to make, distant ones, the selection size and what is left of it;
        // the biggest backlog seen; seconds from a backlog appearing to nothing left to make (last, slowest).
        public static int LabPendingNow, LabDistantPendingNow, LabSelectK, LabSelectedLeft, LabBacklogPeak;
        public static double LabLastSettleSeconds, LabMaxSettleSeconds;
        // Removal: GameObjects queued; destroyed at once while the queue is on (not deferrable, already inactive, or
        // the queue behind); of those, already inactive when removed, and destroyed at once because overdue entries
        // remained; destroyed from the queue (of those, overdue ones destroyed past the budget); destroyed early because
        // their ZDO came back; flushes (switch off, stand-down, scene end); registry entries taken out early (WearNTear,
        // SlowUpdate, Piece lists); components whose timers were cancelled.
        public static long LabRetired, LabSyncDestroyed, LabRetireInactive, LabBehindSync, LabDrained, LabForced, LabRevived, LabFlushes;
        public static long LabUnlisted, LabTimersCancelled;
        // Milliseconds: deactivation (all, the most in one RemoveObjects call; includes unlisting and timer cancels),
        // queue destroys (all, most in a frame), the running mean per destroyed object, the budget of the last frame.
        public static double LabDeactivateMs, LabDeactivateMaxMs, LabDrainMs, LabDrainFrameMaxMs, LabAvgDestroyMs = 0.1, LabDrainBudgetMs;
        // Queue gauges: waiting now, the oldest wait now and the longest seen (seconds of the drain clock); the most
        // objects one RemoveObjects call handed to Retire (the forced-destroy cap per frame), the most forced in a frame.
        public static int LabQueuedNow, LabBatchMax, LabForcedFrameMax;
        public static double LabOldestWaitNow, LabWaitMax;
        // Verify: checks; eligible ZDOs without an instance after the creator said it was done (must stay 0); eligible
        // ZDOs left without an instance for StarveSeconds (must stay 0); ZDOs whose Created flag disagrees with the
        // game's table (the game's or another mod's state, informative); queued GameObjects found active (destroyed
        // then), still holding a ZDO, or next to a new instance of their ZDO (must stay 0); queued GameObjects with a
        // component still invoking (must stay 0 but for a one-shot Invoke of a type that has no InvokeRepeating) or
        // still in a registry above (must stay 0 while the registries were found).
        public static long LabVerifyChecks, LabVerifyMissing, LabVerifyStarved, LabVerifyFlag, LabVerifyZombie, LabVerifyTimers, LabVerifyListed;

        public static string State =>
            !s_installed ? "not installed" :
            $"create {(!s_spawn.On ? "off" : s_createGaveUp ? "gave up" : s_createAside ? "stepped aside" : "on")}, " +
            $"remove {(!s_despawn.On ? "off" : !s_transpiled ? "not inserted" : s_destroyGaveUp ? "gave up" : s_destroyAside ? "stepped aside" : "on")}";

        public static string LabStats() =>
            $"spawn budget: {State}; passes {LabPasses}, made {LabCreated} ({LabCreatedInPass} in passes, {LabCreatedBetween} between, " +
            $"{LabDistantCreated} distant), nothing made {LabNull} (destroyed as invalid {LabInvalidDestroyed}), stale {LabStale}, " +
            $"zone not ready {LabNotReady}, overflow passes {LabOverflowPasses}, ran dry {LabRanDry}, errors {LabCreateErrors}; " +
            $"pending {LabPendingNow} near + {LabDistantPendingNow} distant (peak {LabBacklogPeak}), K {LabSelectK} ({LabSelectedLeft} left); " +
            $"create {LabCreateMs:0.0} ms total, {LabAvgCreateMs:0.000} ms/object, slowest {LabCreateMaxMs:0.00} ms, most in a frame " +
            $"{LabFrameCreateMaxMs:0.00} ms, frames over budget {LabFramesOverBudget}; unknown prefabs {LabInvalidMs:0.0} ms; " +
            $"gather {LabGatherMs:0.000} ms (max {LabGatherMaxMs:0.000}); " +
            $"settle {LabLastSettleSeconds:0.0} s (max {LabMaxSettleSeconds:0.0}); removal: queued {LabRetired}, at once {LabSyncDestroyed} " +
            $"(inactive {LabRetireInactive}, queue behind {LabBehindSync}), destroyed from queue {LabDrained} (overdue {LabForced}, " +
            $"most in a frame {LabForcedFrameMax}, cap {LabBatchMax}), revived {LabRevived}, flushes {LabFlushes}, unlisted {LabUnlisted}, " +
            $"timers cancelled {LabTimersCancelled}, waiting {LabQueuedNow} (oldest {LabOldestWaitNow:0.0} s, max {LabWaitMax:0.0} s), " +
            $"deactivate {LabDeactivateMs:0.0} ms (max {LabDeactivateMaxMs:0.0} in a call), destroy {LabDrainMs:0.0} ms " +
            $"(max {LabDrainFrameMaxMs:0.0} in a frame, {LabAvgDestroyMs:0.000} ms/object, budget {LabDrainBudgetMs:0.0} ms)" +
            (s_verify.On ? $"; verify: checks {LabVerifyChecks}, missing {LabVerifyMissing}, starved {LabVerifyStarved}, " +
                           $"flag {LabVerifyFlag}, zombie {LabVerifyZombie}, timers {LabVerifyTimers}, listed {LabVerifyListed}" : "");

        // ---- install --------------------------------------------------------------------------------------------

        static void Install(Harmony harmony)
        {
            Type scene = typeof(ZNetScene);
            // Everything is looked up before anything is patched: a game update that renamed one of them leaves the
            // module out whole.
            s_sortedMethod = Need(AccessTools.DeclaredMethod(scene, "CreateObjectsSorted"), "CreateObjectsSorted");
            s_distantMethod = Need(AccessTools.DeclaredMethod(scene, "CreateDistantObjects"), "CreateDistantObjects");
            s_removeMethod = Need(AccessTools.DeclaredMethod(scene, "RemoveObjects"), "RemoveObjects");
            MethodInfo createObject = Need(AccessTools.DeclaredMethod(scene, "CreateObject", new[] { typeof(ZDO) }), "CreateObject");
            MethodInfo update = Need(AccessTools.DeclaredMethod(scene, "Update"), "Update");
            MethodInfo shutdown = Need(AccessTools.DeclaredMethod(scene, nameof(ZNetScene.Shutdown)), "Shutdown");
            MethodInfo onDestroy = Need(AccessTools.DeclaredMethod(scene, "OnDestroy"), "OnDestroy");
            ExpectParams(s_sortedMethod, typeof(List<ZDO>), typeof(int), typeof(int).MakeByRefType());
            ExpectParams(s_distantMethod, typeof(List<ZDO>), typeof(int), typeof(int).MakeByRefType());
            ExpectParams(s_removeMethod, typeof(List<ZDO>), typeof(List<ZDO>));
            if (createObject.ReturnType != typeof(GameObject))
                throw new MissingMethodException(nameof(ZNetScene), "CreateObject returns " + createObject.ReturnType);
            if (s_getGameObject == null || s_objectDestroy == null || s_getZdo == null || s_retire == null)
                throw new MissingMethodException("SpawnBudget: Component.gameObject, Object.Destroy, ZNetView.GetZDO or Retire not found");
            s_createObject = AccessTools.MethodDelegate<Func<ZNetScene, ZDO, GameObject>>(createObject);
            try
            {
                s_loadingOf = AccessTools.FieldRefAccess<ZoneSystem, Dictionary<Vector2s, List<ZDO>>>("m_loadingObjectsInZones");
            }
            catch (Exception e)
            {
                // Only an optimization: without it every candidate asks IsZoneReadyForType, as the game's loop does.
                s_loadingOf = null;
                EnginePlugin.Log?.LogWarning("SpawnBudget: ZoneSystem.m_loadingObjectsInZones not found, zone readiness is asked per object: " + e.Message);
            }

            // Registered first, so no failure below leaves a switch that does not clean up.
            s_spawn.Switched += on =>
            {
                ResetCreation();
                if (on)
                    s_createGaveUp = false;
            };
            s_despawn.Switched += on =>
            {
                if (!on)
                    FlushAll();
                else
                    s_destroyGaveUp = false;
            };
            // A check switched on starts from nothing: first-seen times kept from an earlier run (while off, nothing
            // prunes them) would count ZDOs pending again now as starved.
            s_verify.Switched += on =>
            {
                s_firstSeen.Clear();
                s_seen.Clear();
                s_starvedReported.Clear();
                s_nextVerify = 0f;
            };
            FindRegistries();

            Type t = typeof(SpawnBudgetModule);
            HarmonyMethod Own(string name, int priority = -1)
            {
                var m = new HarmonyMethod(t, name);
                if (priority >= 0)
                    m.priority = priority;
                return m;
            }
            var attempted = new List<KeyValuePair<MethodBase, MethodInfo>>();
            void Patch(MethodBase original, HarmonyMethod prefix = null, HarmonyMethod postfix = null, HarmonyMethod transpiler = null, HarmonyMethod finalizer = null)
            {
                foreach (HarmonyMethod m in new[] { prefix, postfix, transpiler, finalizer })
                    if (m != null)
                        attempted.Add(new KeyValuePair<MethodBase, MethodInfo>(original, m.method));
                // At most three named HarmonyMethods per call: with all four, HarmonyX binds its obsolete overload.
                if (finalizer == null)
                    harmony.Patch(original, prefix: prefix, postfix: postfix, transpiler: transpiler);
                else if (transpiler == null)
                    harmony.Patch(original, prefix: prefix, postfix: postfix, finalizer: finalizer);
                else
                {
                    harmony.Patch(original, prefix: prefix, postfix: postfix, transpiler: transpiler);
                    harmony.Patch(original, finalizer: finalizer);
                }
            }
            try
            {
                // Last: other mods' prefixes run first and may skip the call (then this does nothing).
                Patch(s_sortedMethod, prefix: Own(nameof(NearPrefix), Priority.Last));
                Patch(s_distantMethod, prefix: Own(nameof(DistantPrefix), Priority.Last), postfix: Own(nameof(DistantPostfix), Priority.Last));
                Patch(s_removeMethod, transpiler: Own(nameof(RemoveObjectsTranspiler), Priority.Last), finalizer: Own(nameof(RemoveObjectsFinalizer)));
                // First: an old GameObject of this ZDO is gone before anything else sees the new one being made.
                Patch(createObject, prefix: Own(nameof(CreateObjectPrefix), Priority.First));
                // Last: after other mods' postfixes (Path of Valheiman's Driver) have seen this frame's objects.
                Patch(update, postfix: Own(nameof(UpdatePostfix), Priority.Last));
                Patch(shutdown, postfix: Own(nameof(SceneEndPostfix)));
                Patch(onDestroy, postfix: Own(nameof(SceneEndPostfix)));
            }
            catch
            {
                // Out whole: take back our own patch methods (the Harmony instance is shared with the other modules).
                foreach (KeyValuePair<MethodBase, MethodInfo> p in attempted)
                {
                    try
                    {
                        harmony.Unpatch(p.Key, p.Value);
                    }
                    catch (Exception e)
                    {
                        EnginePlugin.Log?.LogWarning($"SpawnBudget: could not take back {p.Value.Name} on {p.Key.Name}: {e.Message}");
                    }
                }
                throw;
            }
            s_installed = true;
            CheckForeign(Time.realtimeSinceStartup);
        }

        static MethodInfo Need(MethodInfo m, string name) =>
            m ?? throw new MissingMethodException(nameof(ZNetScene), name);

        static void ExpectParams(MethodInfo m, params Type[] types)
        {
            ParameterInfo[] ps = m.GetParameters();
            bool ok = ps.Length == types.Length;
            for (int i = 0; ok && i < ps.Length; i++)
                ok = ps[i].ParameterType == types[i];
            if (!ok)
                throw new MissingMethodException(nameof(ZNetScene), m.Name + " has a different signature");
        }

        // ---- who else is here -----------------------------------------------------------------------------------

        static void MaybeCheckForeign()
        {
            float now = Time.realtimeSinceStartup;
            if (now >= s_nextPatchCheck)
                CheckForeign(now);
        }

        static void CheckForeign(float now)
        {
            s_nextPatchCheck = now + PatchCheckSeconds;
            string createWho, removeWho;
            try
            {
                // Creation replaces both bodies: any other patch on them would be bypassed.
                createWho = Foreign(s_sortedMethod, any: true) ?? Foreign(s_distantMethod, any: true);
                // Removal swaps one call: only a rewrite of the same IL matters.
                removeWho = Foreign(s_removeMethod, any: false);
            }
            catch (Exception e)
            {
                // Cannot tell who else is there: behave as the game.
                createWho = removeWho = "unknown (reading Harmony patch info failed: " + e.Message + ")";
            }
            bool createAside = createWho != null;
            if (createAside != s_createAside)
            {
                s_createAside = createAside;
                ResetCreation();
                if (createAside)
                    EnginePlugin.Log?.LogWarning($"SpawnBudget: {createWho} patches ZNetScene.CreateObjectsSorted/CreateDistantObjects; the game's creation runs while it does");
                else
                    EnginePlugin.Log?.LogInfo("SpawnBudget: no other mod patches the creation methods any more; budgeted creation again");
            }
            bool removeAside = removeWho != null;
            if (removeAside != s_destroyAside)
            {
                s_destroyAside = removeAside;
                if (removeAside)
                {
                    FlushAll();
                    EnginePlugin.Log?.LogWarning($"SpawnBudget: {removeWho} rewrites ZNetScene.RemoveObjects; removed objects are destroyed as in the game while it does");
                }
                else
                    EnginePlugin.Log?.LogInfo("SpawnBudget: no other mod rewrites RemoveObjects any more; budgeted removal again");
            }
        }

        // The owner of another mod's patch on the method: any kind, or only rewrites (transpilers, IL manipulators).
        static string Foreign(MethodBase method, bool any)
        {
            HarmonyLib.Patches info = Harmony.GetPatchInfo(method);
            if (info == null)
                return null;
            foreach (HarmonyLib.Patch p in info.Transpilers)
                if (!Ours(p.owner))
                    return p.owner;
            // HarmonyX's IL manipulators (read by reflection: plain Harmony has none).
            if (s_ilManipulators?.GetValue(info) is IEnumerable<HarmonyLib.Patch> manipulators)
                foreach (HarmonyLib.Patch p in manipulators)
                    if (!Ours(p.owner))
                        return p.owner;
            if (!any)
                return null;
            foreach (HarmonyLib.Patch p in info.Prefixes)
                if (!Ours(p.owner))
                    return p.owner;
            foreach (HarmonyLib.Patch p in info.Postfixes)
                if (!Ours(p.owner))
                    return p.owner;
            foreach (HarmonyLib.Patch p in info.Finalizers)
                if (!Ours(p.owner))
                    return p.owner;
            return null;
        }

        // A readonly field in HarmonyX.
        static readonly FieldInfo s_ilManipulators = AccessTools.Field(typeof(HarmonyLib.Patches), "ILManipulators");

        // The engine itself and the lab's timers.
        static bool Ours(string owner) =>
            owner == EnginePlugin.Guid || owner != null && owner.StartsWith("valheim.realportal.lab", StringComparison.Ordinal);

        static bool CreationActive()
        {
            if (!s_installed || !s_spawn.On || s_createGaveUp)
                return false;
            MaybeCheckForeign();
            return !s_createAside;
        }

        static bool RemovalActive()
        {
            if (!s_installed || !s_transpiled || !s_despawn.On || s_destroyGaveUp)
                return false;
            MaybeCheckForeign();
            return !s_destroyAside;
        }

        // The game's InLoadingScreen: no local player yet (login, respawn) or the local player teleporting (it waits
        // in UpdateTeleport for IsAreaReady at the target).
        static bool Loading()
        {
            Player p = Player.m_localPlayer;
            return p == null || p.IsTeleporting();
        }

        static double Ms(long t0) => (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        // ---- creation state -------------------------------------------------------------------------------------

        // One candidate: the ZDO with the id it had when listed (the game recycles ZDO objects under new ids), its
        // distance and type at that pass.
        struct Cand
        {
            public ZDO Zdo;
            public ZDOID Uid;
            public float D;
            public int Type;
        }

        // The game's ZDOCompare: higher type first, then nearer (Utils.CompareFloats). Equal keys have no defined order
        // in the game either (List.Sort is unstable).
        sealed class BestFirst : IComparer<Cand>
        {
            internal static readonly BestFirst Instance = new BestFirst();
            public int Compare(Cand x, Cand y) => x.Type != y.Type ? y.Type.CompareTo(x.Type) : Utils.CompareFloats(x.D, y.D);
        }

        // True when a comes strictly before b in the game's order.
        static bool Before(ref Cand a, ref Cand b) => a.Type != b.Type ? a.Type > b.Type : a.D < b.D;

        static ZNetScene s_scene;
        // The selection: while gathering, a heap whose root is the candidate that would be made last (so a better one
        // replaces it); then sorted best first and consumed from s_readyAt.
        static Cand[] s_sel = new Cand[256];
        static int s_selCount, s_selCap, s_readyAt;
        // More near candidates than the selection held (the rest wait for a later pass; distant ones too).
        static bool s_overflow;
        // The last pass found the active area not loaded: the game makes no near object then (distant ones it does).
        static bool s_nearSkipped;
        // Unknown-prefab near ZDOs of this pass (CreateObject is called for them outside the selection).
        static readonly List<ZDO> s_invalid = new List<ZDO>();
        // Distant candidates in the game's list order.
        static ZDO[] s_far = new ZDO[256];
        static ZDOID[] s_farUid = new ZDOID[256];
        static int s_farCount, s_farAt;
        static bool s_farGathered;
        // Pass bookkeeping: frame and real time of the last pass, the reference zone then, near/distant objects still to
        // make with a known prefab, the nearest of those (squared), the near list (for Verify).
        static int s_passFrame = -1;
        static float s_passAt = -100f;
        static Vector2s s_passZone;
        static int s_pendingNear, s_pendingFar;
        static float s_nearestSqr = float.MaxValue;
        static bool s_loading;
        static List<ZDO> s_lastNear;
        static int s_lastNearFrame = -1;
        // Selection size scale: grows when the selection ran dry before the next pass with budget left.
        static double s_kScale = 1.5;
        static bool s_ranDry;
        // This frame: CreateObject milliseconds and calls (shared by the pass and the frames between).
        static int s_frame = -1, s_overFrame = -1;
        static double s_frameMs;
        static int s_frameCalls;
        // Set around the game's CreateObject: an exception escaping while it is set came from the game (or a mod's
        // patch on CreateObject), not from this module.
        static bool s_inGame;
        // Settling: since when there is something to make / nothing to make (real time, -1: not).
        static float s_busySince = -1f, s_idleSince = -1f;
        static int s_errorsLogged;

        static void ResetCreation()
        {
            Array.Clear(s_sel, 0, s_sel.Length);
            Array.Clear(s_far, 0, s_far.Length);
            s_selCount = s_readyAt = 0;
            s_overflow = false;
            s_nearSkipped = false;
            s_farCount = s_farAt = 0;
            s_farGathered = false;
            s_invalid.Clear();
            s_passAt = -100f;
            s_pendingNear = s_pendingFar = 0;
            s_nearestSqr = float.MaxValue;
            s_busySince = s_idleSince = -1f;
            s_lastNear = null;
            s_lastNearFrame = -1;
        }

        static void GiveUpCreation(Exception e)
        {
            s_createGaveUp = true;
            ResetCreation();
            EnginePlugin.Log?.LogError("SpawnBudget: creation failed, the game's creation runs for the rest of this session " +
                                       "(switch SpawnBudget off and on to retry): " + e);
        }

        static void FrameStart()
        {
            int f = Time.frameCount;
            if (f == s_frame)
                return;
            s_frame = f;
            s_frameMs = 0.0;
            s_frameCalls = 0;
        }

        static float Budget() =>
            s_loading ? LoadingBudgetMs : s_nearestSqr <= ArrivalRadius * ArrivalRadius ? ArrivalBudgetMs : BudgetMs;

        static bool HaveTime(float budget) => s_frameCalls < MinCreatePerFrame || s_frameMs < budget;

        // ---- creation: the pass --------------------------------------------------------------------------------

        // Replaces CreateObjectsSorted: gathers, selects and makes near objects. 'created' gets what was made here,
        // raised past the game's limit while near objects remain (the game's CreateDistantObjects then makes none).
        static bool NearPrefix(ZNetScene __instance, List<ZDO> currentNearObjects, int maxCreatedPerFrame, ref int created, bool __runOriginal)
        {
            if (!__runOriginal)
                return true;
            // For Verify, also while off (it then checks the game's own creation).
            s_lastNear = currentNearObjects;
            s_lastNearFrame = Time.frameCount;
            if (!CreationActive())
                return true;
            ZoneSystem zs = ZoneSystem.instance;
            ZNet net = ZNet.instance;
            if (zs == null || net == null || currentNearObjects == null || __instance == null)
                return true;
            s_inGame = false;
            try
            {
                FrameStart();
                s_scene = __instance;
                s_passFrame = Time.frameCount;
                s_passAt = Time.realtimeSinceStartup;
                s_passZone = ZoneSystem.GetZone(net.GetReferencePosition());
                s_loading = Loading();
                s_farGathered = false;
                s_farCount = s_farAt = 0;
                s_pendingFar = 0;
                LabPasses++;
                s_kScale = s_ranDry ? Math.Min(s_kScale * 2.0, 32.0) : Math.Max(1.5, s_kScale * 0.9);
                s_ranDry = false;
                if (!zs.IsActiveAreaLoaded())
                {
                    // The game returns here without making anything near.
                    s_nearSkipped = true;
                    Array.Clear(s_sel, 0, s_selCount);
                    s_selCount = s_readyAt = 0;
                    s_overflow = false;
                    s_pendingNear = 0;
                    s_nearestSqr = float.MaxValue;
                    return false;
                }
                s_nearSkipped = false;
                Gather(__instance, zs, net, currentNearObjects);
                long made0 = LabCreated;
                // Unknown prefabs: the game's per-object path (zone readiness, CreateObject, server destroy). Not charged
                // to the frame's budget: they make nothing, and each costs the game's log line (more with Path of
                // Valheiman's prefix), so on a client lacking a mod a few dozen of them would use up the budget before
                // the selection is reached, and below 30 fps (a pass every frame) nothing real would ever be made. The
                // game makes its max(pending/100, 10) valid objects per pass whatever they cost; so does this budget.
                long invalidT0 = System.Diagnostics.Stopwatch.GetTimestamp();
                for (int i = 0; i < s_invalid.Count; i++)
                {
                    ZDO z = s_invalid[i];
                    if (zs.IsZoneReadyForType(z.GetSector(), z.Type))
                        CreateOne(__instance, z, near: true, inPass: true, charge: false);
                }
                if (s_invalid.Count > 0)
                    LabInvalidMs += Ms(invalidT0);
                s_invalid.Clear();
                CreateAll(__instance, inPass: true, nearAllowed: true, farAllowed: false);
                created = (int)(LabCreated - made0);
                if (!NearDone())
                    created = Math.Max(created, maxCreatedPerFrame + 1);
                return false;
            }
            catch (Exception e) when (!s_inGame)
            {
                GiveUpCreation(e);
                return true;
            }
        }

        // Replaces CreateDistantObjects: once every near object to make is made, lists the distant ones (the game's
        // order) and makes them under the same budget; then the settling bookkeeping.
        static bool DistantPrefix(ZNetScene __instance, List<ZDO> objects, int maxCreatedPerFrame, ref int created, bool __runOriginal)
        {
            if (!__runOriginal || !CreationActive() || s_passFrame != Time.frameCount || !ReferenceEquals(__instance, s_scene) || objects == null)
                return true;
            s_inGame = false;
            try
            {
                if (!s_overflow)
                    GatherFar(__instance, objects);
                CreateAll(__instance, inPass: true, nearAllowed: true, farAllowed: true);
                Settle(Time.realtimeSinceStartup);
                return false;
            }
            catch (Exception e) when (!s_inGame)
            {
                GiveUpCreation(e);
                return true;
            }
        }

        static void DistantPostfix(ZNetScene __instance, List<ZDO> objects)
        {
            if (!s_installed || !s_verify.On)
                return;
            try
            {
                Verify(__instance, s_lastNearFrame == Time.frameCount ? s_lastNear : null, objects, Time.realtimeSinceStartup);
            }
            catch (Exception e)
            {
                EnginePlugin.Log?.LogWarning("SpawnBudgetVerify: check failed: " + e.Message);
            }
        }

        static bool NearDone() => s_readyAt >= s_selCount && !s_overflow;

        static int SelectCap()
        {
            // As many as can be made before the next pass, with margin: the most a frame may spend, times the frames
            // per pass, over the measured cost per object.
            double avg = Math.Max(LabAvgCreateMs, 0.01);
            float perFrame = s_loading ? LoadingBudgetMs : Math.Max(BudgetMs, ArrivalBudgetMs);
            float dt = Math.Max(Time.deltaTime, 0.001f);
            int frames = Mathf.Clamp(Mathf.CeilToInt((1f / 30f) / dt), 1, 16);
            double k = perFrame * frames / avg * s_kScale + 16.0;
            return (int)Math.Min(Math.Max(k, MinSelect), MaxSelect);
        }

        static void Gather(ZNetScene scene, ZoneSystem zs, ZNet net, List<ZDO> near)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Vector3 refPos = net.GetReferencePosition();
            Dictionary<Vector2s, List<ZDO>> loading = s_loadingOf != null ? s_loadingOf(zs) : null;
            // IsZoneReadyForType answers true at once while no zone is loading objects; only then is it skipped here.
            bool askReady = loading == null || loading.Count > 0;
            int cap = SelectCap();
            if (s_sel.Length < cap)
                s_sel = new Cand[Math.Max(cap, s_sel.Length * 2)];
            else
                Array.Clear(s_sel, 0, s_selCount);
            s_selCap = cap;
            s_selCount = 0;
            s_readyAt = 0;
            s_overflow = false;
            s_invalid.Clear();
            int pending = 0;
            float nearest = float.MaxValue;
            for (int i = 0; i < near.Count; i++)
            {
                ZDO z = near[i];
                if (z.Created)
                    continue;
                // The game's write, kept: m_tempSortValue is the game's scratch field, and the game writes it for every
                // ZDO it considers here.
                float d = Utils.DistanceSqr(refPos, z.GetPosition());
                z.m_tempSortValue = d;
                int prefab = z.GetPrefab();
                if (prefab == 0 || scene.GetPrefab(prefab) == null)
                {
                    s_invalid.Add(z);
                    continue;
                }
                // Not makeable yet (a location or dungeon in its zone is still loading heavier objects): left for a later
                // pass, as the game's loop skips it; checked again when it is made.
                if (askReady && !zs.IsZoneReadyForType(z.GetSector(), z.Type))
                {
                    LabNotReady++;
                    continue;
                }
                pending++;
                if (d < nearest)
                    nearest = d;
                var c = new Cand { Zdo = z, Uid = z.m_uid, D = d, Type = (int)z.Type };
                Offer(ref c);
            }
            if (s_overflow)
                LabOverflowPasses++;
            Array.Sort(s_sel, 0, s_selCount, BestFirst.Instance);
            s_pendingNear = pending;
            s_nearestSqr = nearest;
            LabPendingNow = pending;
            LabSelectK = cap;
            double ms = Ms(t0);
            LabGatherMs = ms;
            if (ms > LabGatherMaxMs)
                LabGatherMaxMs = ms;
        }

        // Bounded heap: keeps the s_selCap candidates that come first in the game's order.
        static void Offer(ref Cand c)
        {
            if (s_selCount < s_selCap)
            {
                int i = s_selCount++;
                s_sel[i] = c;
                while (i > 0)
                {
                    int p = (i - 1) >> 1;
                    if (!Before(ref s_sel[p], ref s_sel[i]))
                        break;
                    Cand tmp = s_sel[p];
                    s_sel[p] = s_sel[i];
                    s_sel[i] = tmp;
                    i = p;
                }
                return;
            }
            s_overflow = true;
            if (!Before(ref c, ref s_sel[0]))
                return;
            s_sel[0] = c;
            int n = s_selCount, k = 0;
            while (true)
            {
                int l = 2 * k + 1;
                if (l >= n)
                    break;
                int r = l + 1;
                int later = r < n && Before(ref s_sel[l], ref s_sel[r]) ? r : l;
                if (!Before(ref s_sel[k], ref s_sel[later]))
                    break;
                Cand tmp = s_sel[k];
                s_sel[k] = s_sel[later];
                s_sel[later] = tmp;
                k = later;
            }
        }

        static void GatherFar(ZNetScene scene, List<ZDO> far)
        {
            s_farCount = s_farAt = 0;
            int pending = 0;
            for (int i = 0; i < far.Count; i++)
            {
                ZDO z = far[i];
                if (z.Created)
                    continue;
                if (s_farCount == s_far.Length)
                {
                    Array.Resize(ref s_far, s_far.Length * 2);
                    Array.Resize(ref s_farUid, s_far.Length);
                }
                s_far[s_farCount] = z;
                s_farUid[s_farCount] = z.m_uid;
                s_farCount++;
                int prefab = z.GetPrefab();
                if (prefab != 0 && scene.GetPrefab(prefab) != null)
                    pending++;
            }
            s_farGathered = true;
            s_pendingFar = pending;
            LabDistantPendingNow = pending;
        }

        // Makes near objects from the selection, then (when allowed and every near one is done) distant ones, until
        // the frame's budget is spent.
        static void CreateAll(ZNetScene scene, bool inPass, bool nearAllowed, bool farAllowed)
        {
            float budget = Budget();
            ZoneSystem zs = ZoneSystem.instance;
            if (!s_nearSkipped)
            {
                if (!nearAllowed && s_readyAt < s_selCount)
                    return;
                while (s_readyAt < s_selCount)
                {
                    if (!HaveTime(budget))
                    {
                        Gauges();
                        return;
                    }
                    Cand c = s_sel[s_readyAt];
                    s_sel[s_readyAt].Zdo = null;
                    s_readyAt++;
                    ZDO z = c.Zdo;
                    if (z.m_uid != c.Uid || !z.IsValid() || z.Created)
                    {
                        LabStale++;
                        continue;
                    }
                    // The game's per-object test, made when it reaches the object.
                    if (!zs.IsZoneReadyForType(z.GetSector(), z.Type))
                    {
                        LabNotReady++;
                        continue;
                    }
                    CreateOne(scene, z, near: true, inPass);
                }
                if (s_overflow)
                {
                    // More near objects wait than were selected: the next pass selects them (and only then distant ones).
                    if (HaveTime(budget))
                    {
                        s_ranDry = true;
                        LabRanDry++;
                    }
                    Gauges();
                    return;
                }
            }
            if (farAllowed && s_farGathered)
            {
                while (s_farAt < s_farCount)
                {
                    if (!HaveTime(budget))
                        break;
                    ZDO z = s_far[s_farAt];
                    ZDOID uid = s_farUid[s_farAt];
                    s_far[s_farAt] = null;
                    s_farAt++;
                    if (z.m_uid != uid || !z.IsValid() || z.Created)
                    {
                        LabStale++;
                        continue;
                    }
                    CreateOne(scene, z, near: false, inPass);
                }
            }
            Gauges();
        }

        static void Gauges()
        {
            LabSelectedLeft = s_selCount - s_readyAt;
            if (s_frameMs > LabFrameCreateMaxMs)
                LabFrameCreateMaxMs = s_frameMs;
        }

        // The game's per-object body: CreateObject, and on the server the takeover and destruction of a ZDO that made
        // nothing (with the game's log line). 'charge': counted against the frame's budget (not for unknown prefabs).
        static void CreateOne(ZNetScene scene, ZDO z, bool near, bool inPass, bool charge = true)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            s_inGame = true;
            GameObject go = s_createObject(scene, z);
            s_inGame = false;
            double ms = Ms(t0);
            if (charge)
            {
                s_frameMs += ms;
                s_frameCalls++;
                // One object can overshoot (the budget is checked before each one): frames that went 50% over.
                if (s_frameMs > Budget() * 1.5f && s_overFrame != s_frame)
                {
                    s_overFrame = s_frame;
                    LabFramesOverBudget++;
                }
            }
            if (go != null)
            {
                LabCreated++;
                if (inPass)
                    LabCreatedInPass++;
                else
                    LabCreatedBetween++;
                if (!near)
                    LabDistantCreated++;
                LabCreateMs += ms;
                if (ms > LabCreateMaxMs)
                    LabCreateMaxMs = ms;
                LabAvgCreateMs = LabAvgCreateMs * 0.95 + ms * 0.05;
                return;
            }
            LabNull++;
            ZNet net = ZNet.instance;
            if (net != null && net.IsServer())
            {
                z.SetOwner(ZDOMan.GetSessionID());
                if (near)
                    ZLog.Log("Destroyed invalid prefab ZDO:" + z.m_uid.ToString());
                else
                    ZLog.Log("Destroyed invalid predab ZDO:" + z.m_uid.ToString() + "  prefab hash:" + z.GetPrefab().ToString());
                ZDOMan.instance.DestroyZDO(z);
                LabInvalidDestroyed++;
            }
        }

        // ---- creation: between passes --------------------------------------------------------------------------

        static void Between(ZNetScene scene, float now)
        {
            if (!ReferenceEquals(scene, s_scene) || now - s_passAt > PassStale)
                return;
            if (s_readyAt >= s_selCount && (!s_farGathered || s_farAt >= s_farCount))
                return;
            ZNet net = ZNet.instance;
            ZoneSystem zs = ZoneSystem.instance;
            if (net == null || zs == null)
                return;
            // The player crossed into another zone: the next pass lists what is near now.
            if (ZoneSystem.GetZone(net.GetReferencePosition()) != s_passZone)
                return;
            FrameStart();
            s_loading = Loading();
            bool nearAllowed = s_nearSkipped || zs.IsActiveAreaLoaded();
            s_inGame = false;
            try
            {
                CreateAll(scene, inPass: false, nearAllowed, farAllowed: true);
            }
            catch (Exception e) when (s_inGame)
            {
                // From the game's CreateObject (or a patch on it): the object is left to the next pass, where the same
                // exception would leave the pass as in the game.
                s_inGame = false;
                LabCreateErrors++;
                if (s_errorsLogged++ < LogCap)
                    EnginePlugin.Log?.LogWarning("SpawnBudget: CreateObject threw between passes, left to the next pass: " + e);
            }
        }

        // ---- settling and verify --------------------------------------------------------------------------------

        static void Settle(float now)
        {
            bool busy = s_nearSkipped || s_pendingNear > 0 || !s_farGathered || s_pendingFar > 0;
            int backlog = s_pendingNear + s_pendingFar;
            if (backlog > LabBacklogPeak)
                LabBacklogPeak = backlog;
            if (busy)
            {
                s_idleSince = -1f;
                if (s_busySince < 0f)
                    s_busySince = now;
                return;
            }
            if (s_idleSince < 0f)
                s_idleSince = now;
            if (s_busySince >= 0f)
            {
                LabLastSettleSeconds = now - s_busySince;
                if (LabLastSettleSeconds > LabMaxSettleSeconds)
                    LabMaxSettleSeconds = LabLastSettleSeconds;
                s_busySince = -1f;
            }
        }

        static float s_nextVerify;
        static readonly Dictionary<ZDOID, float> s_firstSeen = new Dictionary<ZDOID, float>();
        static readonly HashSet<ZDOID> s_seen = new HashSet<ZDOID>();
        static readonly HashSet<ZDOID> s_starvedReported = new HashSet<ZDOID>();
        static readonly List<ZDOID> s_drop = new List<ZDOID>();

        // The game's eventual set: every near ZDO with a known prefab whose zone is ready for its type (when the active
        // area is loaded) and every distant one with a known prefab ends up with an instance. Checked against the
        // game's table (HaveInstance), independent of the selection's bookkeeping.
        static void Verify(ZNetScene scene, List<ZDO> near, List<ZDO> far, float now)
        {
            if (now < s_nextVerify || scene == null)
                return;
            s_nextVerify = now + VerifyPeriod;
            ZoneSystem zs = ZoneSystem.instance;
            if (zs == null)
                return;
            LabVerifyChecks++;
            // "Missing" only counts while the creator says it is done (it runs, and nothing was left at the last pass).
            bool idle = CreationActive() && s_idleSince >= 0f && now - s_idleSince >= VerifyIdle;
            s_seen.Clear();
            if (near != null && zs.IsActiveAreaLoaded())
                for (int i = 0; i < near.Count; i++)
                    VerifyOne(scene, zs, near[i], true, idle, now);
            if (far != null)
                for (int i = 0; i < far.Count; i++)
                    VerifyOne(scene, zs, far[i], false, idle, now);
            s_drop.Clear();
            foreach (KeyValuePair<ZDOID, float> kv in s_firstSeen)
                if (!s_seen.Contains(kv.Key))
                    s_drop.Add(kv.Key);
            foreach (ZDOID id in s_drop)
                s_firstSeen.Remove(id);
            VerifyQueue(scene);
        }

        static void VerifyOne(ZNetScene scene, ZoneSystem zs, ZDO z, bool near, bool idle, float now)
        {
            int prefab = z.GetPrefab();
            if (prefab == 0 || scene.GetPrefab(prefab) == null)
                return;
            if (near && !zs.IsZoneReadyForType(z.GetSector(), z.Type))
                return;
            bool have = scene.HaveInstance(z);
            if (z.Created != have)
            {
                // Created without an entry: a view destroyed behind the game's back and swept from the table; the game
                // will not make it again either. Informative only.
                LabVerifyFlag++;
                if (LabVerifyFlag <= LogCap)
                    EnginePlugin.Log?.LogWarning($"SpawnBudgetVerify: {z.m_uid} ({prefab.StringFromHash()}) Created={z.Created} but in the table={have}");
            }
            if (have || z.Created)
                return;
            ZDOID id = z.m_uid;
            s_seen.Add(id);
            if (!s_firstSeen.TryGetValue(id, out float since))
                s_firstSeen[id] = now;
            else if (now - since > StarveSeconds && s_starvedReported.Add(id))
            {
                LabVerifyStarved++;
                if (LabVerifyStarved <= LogCap)
                    EnginePlugin.Log?.LogWarning($"SpawnBudgetVerify: {id} ({prefab.StringFromHash()}, {(near ? "near" : "distant")}) " +
                                                 $"has waited {now - since:0} s for an instance. {LabStats()}");
            }
            if (idle)
            {
                LabVerifyMissing++;
                if (LabVerifyMissing <= LogCap)
                    EnginePlugin.Log?.LogWarning($"SpawnBudgetVerify: {id} ({prefab.StringFromHash()}, {(near ? "near" : "distant")}) " +
                                                 $"has no instance although nothing is left to make. {LabStats()}");
            }
        }

        static void VerifyQueue(ZNetScene scene)
        {
            int deep = 0;
            foreach (Zombie z in s_zombies)
            {
                if (z.Done || !z.Go)
                    continue;
                string bad = null;
                bool active = z.Go.activeSelf;
                if (active)
                    bad = "is active (again), destroyed now as the game would have it";
                else if (z.View && z.View.GetZDO() != null)
                    bad = "holds a ZDO again";
                else if (z.Zdo != null && z.Zdo.m_uid == z.Uid && scene.HaveInstance(z.Zdo))
                    bad = "waits next to a new instance of its ZDO";
                if (bad != null)
                {
                    LabVerifyZombie++;
                    if (LabVerifyZombie <= LogCap)
                        EnginePlugin.Log?.LogWarning($"SpawnBudgetVerify: queued removal {z.Go.name} ({z.Uid}) {bad}");
                    if (active)
                    {
                        // Someone set a queued object active again (Retire does not queue what was inactive already,
                        // which is how a mod that froze it would wake it): in the game it is gone, so it goes now.
                        // Skipped by the queue from here on (the foreach only reads the queue).
                        GameObject go = z.Go;
                        Unmap(z);
                        z.Done = true;
                        z.Go = null;
                        z.View = null;
                        s_zombieCount--;
                        UnityEngine.Object.DestroyImmediate(go);
                        continue;
                    }
                }
                if (deep++ < VerifyDeepPerCheck)
                    VerifyParts(z);
            }
        }

        static readonly List<MonoBehaviour> s_verifyParts = new List<MonoBehaviour>();

        // A queued object's components: none still invoking (its timers were cancelled), none still listed in a
        // registry the game's sweeps walk (taken out at deactivation).
        static void VerifyParts(Zombie z)
        {
            bool invoking = false, listed = false;
            string what = null;
            z.Go.GetComponentsInChildren(true, s_verifyParts);
            try
            {
                foreach (MonoBehaviour mb in s_verifyParts)
                {
                    if (!mb)
                        continue;
                    if (!invoking && mb.IsInvoking())
                    {
                        invoking = true;
                        what = mb.GetType().Name;
                    }
                    if (!listed && s_registries && (mb is WearNTear w && s_wearIndex(w) != -1 || mb is Piece p && s_pieceIndex(p) >= 0 ||
                                                    mb is SlowUpdate s && s_slowIndex(s) != -1))
                    {
                        listed = true;
                        what = mb.GetType().Name;
                    }
                }
            }
            finally
            {
                s_verifyParts.Clear();
            }
            if (invoking)
            {
                LabVerifyTimers++;
                if (LabVerifyTimers <= LogCap)
                    EnginePlugin.Log?.LogWarning($"SpawnBudgetVerify: queued removal {z.Go.name} ({z.Uid}) has a timer still running ({what})");
            }
            if (listed)
            {
                LabVerifyListed++;
                if (LabVerifyListed <= LogCap)
                    EnginePlugin.Log?.LogWarning($"SpawnBudgetVerify: queued removal {z.Go.name} ({z.Uid}) is still in a registry ({what})");
            }
        }

        // ---- removal ----------------------------------------------------------------------------------------------

        // A GameObject the game took out of its table, waiting to be destroyed.
        sealed class Zombie
        {
            public GameObject Go;
            public ZNetView View;
            public ZDO Zdo;
            public ZDOID Uid;
            // The drain clock when it was queued.
            public double At;
            // Destroyed early (its ZDO came back, or Verify found it active again): skipped by the queue.
            public bool Done;
        }

        sealed class ByRef<T> : IEqualityComparer<T> where T : class
        {
            internal static readonly ByRef<T> Instance = new ByRef<T>();
            public bool Equals(T x, T y) => ReferenceEquals(x, y);
            public int GetHashCode(T o) => RuntimeHelpers.GetHashCode(o);
        }

        // Per prefab: may its instances wait, and what the deactivation does for them besides SetActive(false).
        [Flags]
        enum Kind : byte
        {
            // Destroyed at once, the game's call.
            Keep = 0,
            Defer = 1,
            // One WearNTear / Piece / SlowUpdate on the root: taken out of its registry (TryGetComponent).
            Wear = 2,
            Piece = 4,
            Slow = 8,
            // Registry components below the root (or more than one of a kind), or components whose type starts
            // InvokeRepeating: every component of the instance is looked at.
            Deep = 16,
        }

        struct Retiring
        {
            public GameObject Go;
            public Kind Kind;
        }

        static readonly Queue<Zombie> s_zombies = new Queue<Zombie>();
        // The game's table compares ZDOs by id; one live ZDO object per id, and recycled objects are told apart by Uid.
        static readonly Dictionary<ZDO, Zombie> s_zombieByZdo = new Dictionary<ZDO, Zombie>(ByRef<ZDO>.Instance);
        static readonly Stack<Zombie> s_free = new Stack<Zombie>();
        // Queued by the current RemoveObjects call, deactivated by its finalizer.
        static readonly List<Retiring> s_retiring = new List<Retiring>();
        static int s_zombieCount;
        // Per prefab hash (see the header).
        static readonly Dictionary<int, Kind> s_kinds = new Dictionary<int, Kind>();
        static readonly List<MonoBehaviour> s_components = new List<MonoBehaviour>();
        // Objects the current RemoveObjects call handed to Retire (queued or not), and the most one call handed over
        // this session: no frame force-destroys more than that (minus what the game destroys in that frame anyway).
        static int s_batchNow, s_batchMax;
        // Objects Retire left to the game's Object.Destroy in frame s_syncFrame (they go at the end of that frame).
        static int s_syncFrame = -1, s_syncCount;
        // The drain clock: real seconds between frames, at most MaxClockStep each; this frame's step; real time of the
        // last step (-1: none yet).
        static double s_clock;
        static float s_clockStep, s_clockReal = -1f;
        // After the last drain, overdue entries remained (the cap stopped it): removals go the game's way meanwhile.
        static bool s_behind;
        static int s_takeOutErrors;

        // RemoveObjects: "callvirt Component.get_gameObject; call Object.Destroy(Object)" on the removed view becomes
        // "ldloc <the view's ZDO, stored right after its GetZDO>; call Retire(ZNetView, ZDO)". Exactly one such site, a
        // ZDO local before it and no jump into the Destroy call, or the method is left as it is.
        static IEnumerable<CodeInstruction> RemoveObjectsTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var code = new List<CodeInstruction>(instructions);
            int site = -1, sites = 0;
            for (int i = 0; i + 1 < code.Count; i++)
                if (code[i].Calls(s_getGameObject) && code[i + 1].Calls(s_objectDestroy))
                {
                    site = i;
                    sites++;
                }
            int store = -1;
            if (sites == 1)
                for (int i = site - 1; i >= 1; i--)
                    if (IsStloc(code[i]) && code[i - 1].Calls(s_getZdo))
                    {
                        store = i;
                        break;
                    }
            OpCode load = OpCodes.Nop;
            bool ok = sites == 1 && store >= 0 && Unlabeled(code[site + 1]) && LoadFor(code[store], out load);
            if (ok)
            {
                // Edited in place: the get_gameObject instruction keeps its labels and blocks.
                code[site].opcode = load;
                code[site].operand = code[store].operand;
                code[site + 1].opcode = OpCodes.Call;
                code[site + 1].operand = s_retire;
            }
            if (ok != s_transpiled || !s_reportedTranspile)
            {
                s_reportedTranspile = true;
                if (ok)
                    EnginePlugin.Log?.LogInfo("SpawnBudget: RemoveObjects' Destroy call goes through the removal queue");
                else
                    EnginePlugin.Log?.LogWarning($"SpawnBudget: RemoveObjects not as expected ({sites} Destroy sites, ZDO local {(store >= 0 ? "found" : "not found")}; " +
                                                 "changed by another mod or an update?), removal stays the game's");
            }
            s_transpiled = ok;
            return code;
        }

        static bool s_reportedTranspile;

        static bool Unlabeled(CodeInstruction ci) => !(s_labels?.GetValue(ci) is System.Collections.ICollection labels) || labels.Count == 0;

        static bool IsStloc(CodeInstruction ci)
        {
            OpCode op = ci.opcode;
            return op == OpCodes.Stloc_0 || op == OpCodes.Stloc_1 || op == OpCodes.Stloc_2 || op == OpCodes.Stloc_3
                   || op == OpCodes.Stloc_S || op == OpCodes.Stloc;
        }

        static bool LoadFor(CodeInstruction store, out OpCode load)
        {
            OpCode op = store.opcode;
            if (op == OpCodes.Stloc_0) load = OpCodes.Ldloc_0;
            else if (op == OpCodes.Stloc_1) load = OpCodes.Ldloc_1;
            else if (op == OpCodes.Stloc_2) load = OpCodes.Ldloc_2;
            else if (op == OpCodes.Stloc_3) load = OpCodes.Ldloc_3;
            else if (op == OpCodes.Stloc_S) load = OpCodes.Ldloc_S;
            else if (op == OpCodes.Stloc) load = OpCodes.Ldloc;
            else
            {
                load = OpCodes.Nop;
                return false;
            }
            return true;
        }

        // Called by RemoveObjects (transpiled) for each removed view, after the game's ResetZDO and before its
        // DestroyZDO / m_instances.Remove. Off, stepped aside, not deferrable, already inactive or the queue behind:
        // the game's Object.Destroy.
        public static void Retire(ZNetView view, ZDO zdo)
        {
            // Read first, as the game does: on a view destroyed behind the game's back this throws the same exception
            // at the same point of the loop.
            GameObject go = view.gameObject;
            bool active = RemovalActive();
            if (active)
            {
                s_batchNow++;
                if (zdo != null)
                {
                    try
                    {
                        Kind kind = KindOf(zdo);
                        if ((kind & Kind.Defer) != 0)
                        {
                            if (!go.activeSelf)
                            {
                                // Deactivated by someone else (Real Portal's sleeping far areas freeze birds and fish
                                // this way), who may set it active again later: in the game it is gone by then.
                                LabRetireInactive++;
                            }
                            else if (s_behind)
                            {
                                // Overdue entries are still waiting: the queue cannot take more right now.
                                LabBehindSync++;
                            }
                            else
                            {
                                Zombie z = s_free.Count > 0 ? s_free.Pop() : new Zombie();
                                z.Go = go;
                                z.View = view;
                                z.Zdo = zdo;
                                z.Uid = zdo.m_uid;
                                z.At = s_clock;
                                z.Done = false;
                                s_zombies.Enqueue(z);
                                s_zombieByZdo[zdo] = z;
                                s_retiring.Add(new Retiring { Go = go, Kind = kind });
                                s_zombieCount++;
                                LabRetired++;
                                return;
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        GiveUpRemoval(e);
                    }
                }
                LabSyncDestroyed++;
                int frame = Time.frameCount;
                if (frame != s_syncFrame)
                {
                    s_syncFrame = frame;
                    s_syncCount = 0;
                }
                s_syncCount++;
            }
            UnityEngine.Object.Destroy(go);
        }

        static Kind KindOf(ZDO zdo)
        {
            ZDO.ObjectType type = zdo.Type;
            if (type == ZDO.ObjectType.Prioritized || type == ZDO.ObjectType.Terrain)
                return Kind.Keep;
            int hash = zdo.GetPrefab();
            if (!s_kinds.TryGetValue(hash, out Kind kind))
            {
                kind = ComputeKind(hash);
                s_kinds[hash] = kind;
            }
            return kind;
        }

        // From the prefab, once per prefab: deferrable when no component's list is read by the game's (owner-side)
        // code until OnDestroy without a validity check, and nothing shapes terrain or loads content; then what its
        // deactivation has to take care of.
        static Kind ComputeKind(int hash)
        {
            ZNetScene scene = ZNetScene.instance;
            GameObject prefab = scene != null ? scene.GetPrefab(hash) : null;
            if (prefab == null)
                return Kind.Keep;
            Transform root = prefab.transform;
            Kind kind = Kind.Defer;
            s_components.Clear();
            prefab.GetComponentsInChildren(true, s_components);
            try
            {
                foreach (MonoBehaviour mb in s_components)
                {
                    // A missing script.
                    if (!mb)
                        continue;
                    // ItemDrop: the owner auto-stacks items once ItemDrop.s_instances holds over 200
                    // (ItemDrop.SlowUpdate), so waiting items would change when that starts. Vine: a new vine destroys
                    // itself (ZNetScene.Destroy, a ZDO destruction) when s_allVines holds another one at its spot.
                    // WispSpawner: LuredWisp.UpdateTarget (owner) takes the best of WispSpawner.s_spawners (Start to
                    // OnDestroy) without a validity check; a waiting one would keep luring a wisp the game sends off
                    // to despawn (its ZDO destroyed).
                    if (mb is Character || mb is TerrainModifier || mb is TerrainComp || mb is LocationProxy || mb is DungeonGenerator ||
                        mb is EffectArea || mb is CraftingStation || mb is Fire || mb is ShieldGenerator || mb is SpawnSystem ||
                        mb is CreatureSpawner || mb is SpawnArea || mb is TriggerSpawner || mb is ItemDrop || mb is Vine ||
                        mb is WispSpawner)
                        return Kind.Keep;
                    bool onRoot = mb.transform == root;
                    if (mb is WearNTear)
                        kind |= Registry(kind, Kind.Wear, onRoot);
                    else if (mb is Piece)
                        kind |= Registry(kind, Kind.Piece, onRoot);
                    else if (mb is SlowUpdate)
                        kind |= Registry(kind, Kind.Slow, onRoot);
                    // StaticPhysics (most vegetation and stones) has a timer only while it falls, and says so: the
                    // root path looks at it (CancelFall), so it alone does not cost every instance the full walk.
                    Type type = mb.GetType();
                    if (type != typeof(StaticPhysics) && UsesTimers(type))
                        kind |= Kind.Deep;
                }
            }
            finally
            {
                s_components.Clear();
            }
            return kind;
        }

        // The flag for a registry component: the cheap root lookup for the first one on the root, the full walk else.
        static Kind Registry(Kind kind, Kind flag, bool onRoot) => onRoot && (kind & flag) == 0 ? flag : Kind.Deep;

        // After RemoveObjects (also when it threw part way, or another prefix skipped it: then nothing was queued):
        // the batch it queued is deactivated, unlisted and its timers cancelled before the call returns.
        static void RemoveObjectsFinalizer()
        {
            if (s_batchNow > s_batchMax)
                LabBatchMax = s_batchMax = s_batchNow;
            s_batchNow = 0;
            if (s_retiring.Count == 0)
                return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                for (int i = 0; i < s_retiring.Count; i++)
                {
                    Retiring r = s_retiring[i];
                    GameObject go = r.Go;
                    if (!go)
                        continue;
                    try
                    {
                        go.SetActive(false);
                        TakeOut(go, r.Kind);
                    }
                    catch (Exception e)
                    {
                        // This one may stay listed or keep a timer until it is destroyed; the rest goes on.
                        if (s_takeOutErrors++ < LogCap)
                            EnginePlugin.Log?.LogError($"SpawnBudget: deactivating removed object {go.name} failed: {e}");
                    }
                }
            }
            finally
            {
                s_retiring.Clear();
            }
            double ms = Ms(t0);
            LabDeactivateMs += ms;
            if (ms > LabDeactivateMaxMs)
                LabDeactivateMaxMs = ms;
        }

        // What the game's OnDestroy would do at the end of this frame that matters while the object waits: out of the
        // registries the game's sweeps walk, timers stopped (see the header).
        static void TakeOut(GameObject go, Kind kind)
        {
            if ((kind & Kind.Deep) != 0)
            {
                go.GetComponentsInChildren(true, s_components);
                try
                {
                    foreach (MonoBehaviour mb in s_components)
                    {
                        if (!mb)
                            continue;
                        if (s_registries)
                        {
                            if (mb is WearNTear w)
                                UnlistWear(w);
                            else if (mb is Piece p)
                                UnlistPiece(p);
                            else if (mb is SlowUpdate s)
                                UnlistSlow(s);
                        }
                        Type type = mb.GetType();
                        if (type == typeof(StaticPhysics))
                            CancelFall(mb);
                        else if (UsesTimers(type))
                        {
                            mb.CancelInvoke();
                            LabTimersCancelled++;
                        }
                    }
                }
                finally
                {
                    s_components.Clear();
                }
                return;
            }
            if ((kind & Kind.Wear) != 0 && s_registries && go.TryGetComponent(out WearNTear wear))
                UnlistWear(wear);
            if ((kind & Kind.Piece) != 0 && s_registries && go.TryGetComponent(out Piece piece))
                UnlistPiece(piece);
            if ((kind & Kind.Slow) != 0 && go.TryGetComponent(out SlowUpdate slow))
            {
                if (s_registries)
                    UnlistSlow(slow);
                CancelFall(slow);
            }
        }

        // StaticPhysics.Fall starts InvokeRepeating("FallUpdate") with m_falling = true; the landing clears both. Its
        // only timer, so it is running exactly while IsFalling.
        static void CancelFall(MonoBehaviour mb)
        {
            if (mb.GetType() == typeof(StaticPhysics) && ((StaticPhysics)mb).IsFalling)
            {
                mb.CancelInvoke();
                LabTimersCancelled++;
            }
        }

        // ---- registries -------------------------------------------------------------------------------------------

        // The game's own index fields and lists (private); found at install, the lists themselves read on first use (a
        // static read runs the class constructor, which is left to the game's own first use).
        static bool s_registries;
        static AccessTools.FieldRef<WearNTear, int> s_wearIndex;
        static AccessTools.FieldRef<SlowUpdate, int> s_slowIndex;
        static AccessTools.FieldRef<Piece, int> s_pieceIndex;
        static FieldInfo s_allPiecesField, s_comfortPiecesField;
        static List<Piece> s_allPieces;
        static HashSet<Piece> s_comfortPieces;
        // The engine's WearNTearActive: its OnDestroy hook, which drops a piece from its sector files.
        static Action<WearNTear> s_wearActiveDrop;
        // The engine's SlowUpdaterBudget: its SlowUpdate.OnDestroy hook, which drops an object from its zone buckets
        // (idempotent: an object it does not hold is ignored, so the real OnDestroy later finds nothing to drop).
        static Action<SlowUpdate> s_slowBudgetDrop;

        static void FindRegistries()
        {
            try
            {
                s_wearIndex = AccessTools.FieldRefAccess<WearNTear, int>("m_myIndex");
                s_slowIndex = AccessTools.FieldRefAccess<SlowUpdate, int>("m_myIndex");
                s_pieceIndex = AccessTools.FieldRefAccess<Piece, int>("m_myListIndex");
                s_allPiecesField = AccessTools.Field(typeof(Piece), "s_allPieces");
                s_comfortPiecesField = AccessTools.Field(typeof(Piece), "s_allComfortPieces");
                s_registries = s_allPiecesField != null && s_allPiecesField.IsStatic && s_allPiecesField.FieldType == typeof(List<Piece>) &&
                               s_comfortPiecesField != null && s_comfortPiecesField.IsStatic && s_comfortPiecesField.FieldType == typeof(HashSet<Piece>);
            }
            catch (Exception e)
            {
                s_registries = false;
                EnginePlugin.Log?.LogWarning("SpawnBudget: " + e.Message);
            }
            if (!s_registries)
                EnginePlugin.Log?.LogWarning("SpawnBudget: the WearNTear/SlowUpdate/Piece registries are not as expected; waiting objects stay " +
                                             "listed until destroyed (the wear and slow-update sweeps take longer while pieces wait)");
            Type budget = typeof(SpawnBudgetModule).Assembly.GetType("RealPortal.Engine.SlowUpdaterBudgetModule");
            if (budget != null)
            {
                try
                {
                    MethodInfo drop = AccessTools.DeclaredMethod(budget, "BeforeDestroy", new[] { typeof(SlowUpdate) });
                    if (drop != null && drop.IsStatic && drop.ReturnType == typeof(void))
                        s_slowBudgetDrop = AccessTools.MethodDelegate<Action<SlowUpdate>>(drop);
                }
                catch (Exception e)
                {
                    s_slowBudgetDrop = null;
                    EnginePlugin.Log?.LogWarning("SpawnBudget: " + e.Message);
                }
                if (s_slowBudgetDrop == null)
                    EnginePlugin.Log?.LogWarning("SpawnBudget: SlowUpdaterBudget's OnDestroy hook not found; while SlowUpdaterBudget is on, " +
                                                 "its near pass may still visit waiting objects until they are destroyed");
            }
            Type active = typeof(SpawnBudgetModule).Assembly.GetType("RealPortal.Engine.WearNTearActiveModule");
            if (active == null)
                return;
            try
            {
                MethodInfo drop = AccessTools.DeclaredMethod(active, "DestroyPrefix", new[] { typeof(WearNTear) });
                if (drop != null && drop.IsStatic && drop.ReturnType == typeof(void))
                    s_wearActiveDrop = AccessTools.MethodDelegate<Action<WearNTear>>(drop);
            }
            catch (Exception e)
            {
                s_wearActiveDrop = null;
                EnginePlugin.Log?.LogWarning("SpawnBudget: " + e.Message);
            }
            if (s_wearActiveDrop == null)
                EnginePlugin.Log?.LogWarning("SpawnBudget: WearNTearActive's OnDestroy hook not found; while WearNTearActiveSectors is on, " +
                                             "waiting pieces stay in its files until destroyed");
        }

        static void UnlistWear(WearNTear w)
        {
            if (Unlist(WearNTear.GetAllInstances(), w, s_wearIndex))
                LabUnlisted++;
            // Its files hold only what the game's list held (it does nothing for a piece it never filed); the real
            // OnDestroy later finds nothing to drop.
            s_wearActiveDrop?.Invoke(w);
        }

        static void UnlistSlow(SlowUpdate s)
        {
            if (Unlist(SlowUpdate.GetAllInstaces(), s, s_slowIndex))
                LabUnlisted++;
            // SlowUpdaterBudget's near pass walks its own zone buckets; without this a waiting object (deactivated, no
            // ZDO) would keep a visit slot there until its real OnDestroy (Plant.SUpdate returns on !IsValid, and
            // StaticPhysics only moves its transform outside the active area, so it is a wasted slot, not a write).
            s_slowBudgetDrop?.Invoke(s);
        }

        static void UnlistPiece(Piece p)
        {
            if (s_allPieces == null)
            {
                s_allPieces = (List<Piece>)s_allPiecesField.GetValue(null);
                s_comfortPieces = (HashSet<Piece>)s_comfortPiecesField.GetValue(null);
            }
            if (Unlist(s_allPieces, p, s_pieceIndex))
                LabUnlisted++;
            // Piece.OnDestroy: "if (m_comfort > 0) s_allComfortPieces.Remove(this)"; a second Remove is a no-op.
            if (p.m_comfort > 0)
                s_comfortPieces.Remove(p);
        }

        // The swap-remove of the registry's own OnDestroy, done now:
        //   list[i] = list[last]; list[i].index = i; list.RemoveAt(last);
        // and the index left at -1, which that OnDestroy reads as "not listed" (WearNTear and SlowUpdate test != -1,
        // Piece >= 0). An entry that is not where its index says (not listed, or another mod reordered the list) is left
        // to its OnDestroy, as in the game.
        internal static bool Unlist<T>(List<T> list, T item, AccessTools.FieldRef<T, int> indexOf) where T : class
        {
            ref int index = ref indexOf(item);
            int i = index, last = list.Count - 1;
            if (i < 0 || i > last || !ReferenceEquals(list[i], item))
                return false;
            T moved = list[last];
            list[i] = moved;
            indexOf(moved) = i;
            list.RemoveAt(last);
            index = -1;
            return true;
        }

        // ---- timers -------------------------------------------------------------------------------------------------

        // Per component type: does it (or a base class below MonoBehaviour) call MonoBehaviour.InvokeRepeating? Found
        // from the IL once per type; a type only some instances of which start a timer still counts (the CancelInvoke
        // of one that has none costs a little, nothing else). One-shot Invoke is not looked for (see the header).
        static readonly Dictionary<Type, bool> s_timerTypes = new Dictionary<Type, bool>();
        // Per module: call tokens already resolved (true: InvokeRepeating).
        static readonly Dictionary<Module, Dictionary<int, bool>> s_timerTokens = new Dictionary<Module, Dictionary<int, bool>>();
        static int s_timerScanErrors;

        const BindingFlags DeclaredMembers = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static |
                                             BindingFlags.Public | BindingFlags.NonPublic;

        internal static bool UsesTimers(Type t)
        {
            if (t == null || t == typeof(MonoBehaviour) || t == typeof(object))
                return false;
            if (s_timerTypes.TryGetValue(t, out bool uses))
                return uses;
            uses = DeclaresTimer(t) || UsesTimers(t.BaseType);
            s_timerTypes[t] = uses;
            return uses;
        }

        static bool DeclaresTimer(Type t)
        {
            try
            {
                foreach (MethodInfo m in t.GetMethods(DeclaredMembers))
                    if (CallsInvokeRepeating(m))
                        return true;
                foreach (ConstructorInfo c in t.GetConstructors(DeclaredMembers))
                    if (CallsInvokeRepeating(c))
                        return true;
                return false;
            }
            catch (Exception e)
            {
                // Unreadable: taken to have timers (that costs a CancelInvoke per instance, nothing else).
                if (s_timerScanErrors++ < LogCap)
                    EnginePlugin.Log?.LogWarning($"SpawnBudget: could not read {t.FullName}'s IL, its timers are cancelled on removal: {e.Message}");
                return true;
            }
        }

        // Every call/callvirt whose token is a method of this module (MethodDef) or a reference to another's
        // (MemberRef). Scanned byte by byte: a stray 0x28/0x6F inside an operand gives a token that does not resolve or
        // resolves to something else, never a missed call.
        static bool CallsInvokeRepeating(MethodBase m)
        {
            if (m.IsAbstract)
                return false;
            byte[] il = m.GetMethodBody()?.GetILAsByteArray();
            if (il == null)
                return false;
            Module module = m.Module;
            for (int i = 0; i + 4 < il.Length; i++)
            {
                byte op = il[i];
                if (op != 0x28 && op != 0x6F)
                    continue;
                int token = il[i + 1] | il[i + 2] << 8 | il[i + 3] << 16 | il[i + 4] << 24;
                int table = (token >> 24) & 0xFF;
                if ((table == 0x0A || table == 0x06) && IsInvokeRepeating(module, token))
                    return true;
            }
            return false;
        }

        static bool IsInvokeRepeating(Module module, int token)
        {
            if (!s_timerTokens.TryGetValue(module, out Dictionary<int, bool> known))
                s_timerTokens[module] = known = new Dictionary<int, bool>();
            if (known.TryGetValue(token, out bool yes))
                return yes;
            yes = false;
            try
            {
                MethodBase target = module.ResolveMethod(token);
                yes = target != null && target.DeclaringType == typeof(MonoBehaviour) && target.Name == nameof(MonoBehaviour.InvokeRepeating);
            }
            catch (Exception)
            {
                // Not a method without a generic context, or a stray byte: not the one looked for.
            }
            known[token] = yes;
            return yes;
        }

        // ---- the queue ----------------------------------------------------------------------------------------------

        // The ZDO is being made again while its old GameObject still waits: that one goes first.
        static void CreateObjectPrefix(ZDO zdo)
        {
            if (s_zombieByZdo.Count == 0 || zdo == null)
                return;
            try
            {
                if (!s_zombieByZdo.TryGetValue(zdo, out Zombie z) || z.Uid != zdo.m_uid)
                    return;
                s_zombieByZdo.Remove(zdo);
                z.Done = true;
                s_zombieCount--;
                LabRevived++;
                if (z.Go)
                    UnityEngine.Object.DestroyImmediate(z.Go);
                z.Go = null;
                z.View = null;
            }
            catch (Exception e)
            {
                EnginePlugin.Log?.LogError("SpawnBudget: destroying the old object of a returning ZDO failed: " + e);
            }
        }

        static void Free(Zombie z)
        {
            z.Go = null;
            z.View = null;
            z.Zdo = null;
            z.Done = false;
            s_free.Push(z);
        }

        static void Unmap(Zombie z)
        {
            if (z.Zdo != null && s_zombieByZdo.TryGetValue(z.Zdo, out Zombie mapped) && ReferenceEquals(mapped, z))
                s_zombieByZdo.Remove(z.Zdo);
        }

        // The drain clock: real time between frames, at most MaxClockStep per frame.
        static void Tick(float now)
        {
            float step = s_clockReal < 0f ? 0f : Mathf.Clamp(now - s_clockReal, 0f, MaxClockStep);
            s_clockReal = now;
            s_clockStep = step;
            s_clock += step;
        }

        // The frame's destroy budget: the base (x4 once the oldest waits DrainHurryAge), raised to the rate that
        // clears everything waiting before the oldest is overdue at the measured cost per object (on the safe side:
        // younger entries have longer), up to DrainCatchUpMaxMs; that cap once the oldest is overdue.
        internal static double DrainBudget(bool loading, double wait, int queued, double avgMs, double step)
        {
            double budget = loading ? DestroyLoadingBudgetMs : DestroyBudgetMs;
            if (wait >= DrainHurryAge)
                budget *= DrainHurryFactor;
            if (queued <= 0)
                return budget;
            double left = DrainAllAge - wait;
            double need = left <= 0.0 ? DrainCatchUpMaxMs : queued * avgMs / Math.Max(1.0, left / Math.Max(step, 1.0 / 240.0));
            return Math.Max(budget, Math.Min(need, DrainCatchUpMaxMs));
        }

        // Oldest first under the frame's budget; past the budget only entries that waited DrainAllAge, and only while
        // this frame's destroys (these and the game's own of this frame) stay within the largest batch one
        // RemoveObjects call handed over: no frame destroys more than the game's worst removal frame did.
        static void Drain()
        {
            while (s_zombies.Count > 0 && s_zombies.Peek().Done)
                Free(s_zombies.Dequeue());
            if (s_zombies.Count == 0)
            {
                s_behind = false;
                LabQueuedNow = 0;
                LabOldestWaitNow = 0.0;
                return;
            }
            double wait = s_clock - s_zombies.Peek().At;
            if (wait > LabWaitMax)
                LabWaitMax = wait;
            double budget = DrainBudget(Loading(), wait, s_zombieCount, LabAvgDestroyMs, s_clockStep);
            LabDrainBudgetMs = budget;
            int sync = s_syncFrame == Time.frameCount ? s_syncCount : 0;
            int cap = Math.Max(s_batchMax, MinDestroyPerFrame);
            double spent = 0.0;
            int n = 0, forced = 0;
            while (s_zombies.Count > 0)
            {
                Zombie z = s_zombies.Peek();
                if (z.Done)
                {
                    Free(s_zombies.Dequeue());
                    continue;
                }
                bool pastBudget = n >= MinDestroyPerFrame && spent >= budget;
                // The queue is in order of At: once the head is not overdue, nothing behind it is.
                if (pastBudget && (s_clock - z.At < DrainAllAge || n + sync >= cap))
                    break;
                s_zombies.Dequeue();
                Unmap(z);
                s_zombieCount--;
                GameObject go = z.Go;
                Free(z);
                if (!go)
                    continue;
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                UnityEngine.Object.DestroyImmediate(go);
                double ms = Ms(t0);
                spent += ms;
                n++;
                if (pastBudget)
                    forced++;
                LabDrained++;
                LabAvgDestroyMs = LabAvgDestroyMs * 0.98 + ms * 0.02;
            }
            LabDrainMs += spent;
            if (spent > LabDrainFrameMaxMs)
                LabDrainFrameMaxMs = spent;
            LabForced += forced;
            if (forced > LabForcedFrameMax)
                LabForcedFrameMax = forced;
            while (s_zombies.Count > 0 && s_zombies.Peek().Done)
                Free(s_zombies.Dequeue());
            double oldest = s_zombies.Count > 0 ? s_clock - s_zombies.Peek().At : 0.0;
            s_behind = oldest >= DrainAllAge;
            LabQueuedNow = s_zombieCount;
            LabOldestWaitNow = oldest;
        }

        // Everything still waiting is destroyed the game's way (end of this frame).
        static void FlushAll()
        {
            s_behind = false;
            if (s_zombies.Count == 0 && s_retiring.Count == 0)
                return;
            LabFlushes++;
            while (s_zombies.Count > 0)
            {
                Zombie z = s_zombies.Dequeue();
                if (!z.Done && z.Go)
                    UnityEngine.Object.Destroy(z.Go);
                Free(z);
            }
            s_zombieByZdo.Clear();
            s_retiring.Clear();
            s_zombieCount = 0;
            LabQueuedNow = 0;
            LabOldestWaitNow = 0.0;
        }

        static void GiveUpRemoval(Exception e)
        {
            s_destroyGaveUp = true;
            EnginePlugin.Log?.LogError("SpawnBudget: removal queue failed, removed objects are destroyed as in the game for the rest of " +
                                       "this session (switch DespawnBudget off and on to retry): " + e);
            // What is queued so far stays queued until the finalizer and the next frame: FlushAll would also drop the
            // batch this RemoveObjects call is building. The next Update flushes.
            s_flushPending = true;
        }

        static bool s_flushPending;

        // ---- frame ------------------------------------------------------------------------------------------------

        static void UpdatePostfix(ZNetScene __instance)
        {
            if (!s_installed)
                return;
            float now = Time.realtimeSinceStartup;
            try
            {
                Tick(now);
                MaybeCheckForeign();
                if (s_flushPending || (s_zombies.Count > 0 && !RemovalActive() && s_retiring.Count == 0))
                {
                    s_flushPending = false;
                    FlushAll();
                }
                Drain();
            }
            catch (Exception e)
            {
                s_destroyGaveUp = true;
                EnginePlugin.Log?.LogError("SpawnBudget: removal queue failed, destroyed as in the game from now on: " + e);
                try
                {
                    FlushAll();
                }
                catch (Exception again)
                {
                    EnginePlugin.Log?.LogError("SpawnBudget: flushing the removal queue failed too: " + again);
                }
            }
            if (!CreationActive())
                return;
            try
            {
                Between(__instance, now);
            }
            catch (Exception e)
            {
                GiveUpCreation(e);
            }
        }

        // ZNetScene.Shutdown and OnDestroy: the world is going away.
        static void SceneEndPostfix(ZNetScene __instance)
        {
            if (!s_installed)
                return;
            try
            {
                FlushAll();
                ResetCreation();
                s_scene = null;
                s_kinds.Clear();
                s_firstSeen.Clear();
                s_starvedReported.Clear();
                // The next world's batches set the forced-destroy cap anew.
                s_batchNow = s_batchMax = 0;
                s_syncFrame = -1;
                s_clockReal = -1f;
            }
            catch (Exception e)
            {
                EnginePlugin.Log?.LogWarning("SpawnBudget: cleanup at scene end failed: " + e.Message);
            }
        }
    }
}
