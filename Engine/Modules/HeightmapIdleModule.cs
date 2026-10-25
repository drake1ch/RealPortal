using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace RealPortal.Engine
{
    // Every enabled Heightmap is called twice a frame to find out it has nothing to do: Unity's LateUpdate (work only
    // when m_doLateUpdate == 1) and MonoUpdaters' CustomLateUpdate over Heightmap.Instances (work only when it is 2).
    // Both values are set only by Poke(delayed) and cleared by Regenerate, so almost every frame every call is empty;
    // with far areas loaded that is ~1300 heightmaps.
    //
    // HeightmapIdle (on by default) gives MonoUpdaters only the heightmaps that have work. Poke(2) puts its heightmap
    // on a pending set; in MonoUpdaters.LateUpdate, the Heightmap pass gets an empty list when nothing is pending, and
    // otherwise the heightmaps of Heightmap.Instances whose m_doLateUpdate is 2, in Instances order. Those are exactly
    // the ones whose CustomLateUpdate would do something, at the same point of the same frame, in the same order, through
    // their own CustomLateUpdate. Heightmap.Instances itself is untouched (ApplySettingsOnAll and other mods use it).
    // Notes:
    //  - A heightmap enabled while its value is 2 (a distant LOD one; others regenerate in OnEnable) is caught by an
    //    OnEnable postfix. A mod that writes m_doLateUpdate = 2 straight into the field, without Poke (nothing in the
    //    game or Real Portal does), is caught by a sweep every 2 s, so its regeneration may come up to 2 s late.
    //  - A Poke(2) made from inside this very pass (from a Regenerate: nothing in the game does it) waits for the next
    //    frame, where the game might have taken it in the same pass.
    //  - When CustomLateUpdate carries patches of another mod (that may expect a call every frame), the pass gets the
    //    whole list as in the game. Patches of the Real Portal lab (timers) do not count.
    //
    // HeightmapPark (off by default, experimental) also stops Unity's LateUpdate on idle heightmaps: they are disabled
    // (enabled = false) while m_doLateUpdate != 1, with the OnEnable/OnDisable bodies skipped for these switches, so for
    // the game they stay enabled: still in Heightmap.Instances, still subscribed to graphics settings, no Regenerate
    // from OnEnable. Poke(1) switches its heightmap back on at once, so Unity calls its LateUpdate in the same frame as
    // in the game when the poke comes before the LateUpdate phase (the game's pokes with delay 1 come from terrain
    // operations, in Update). Destroying a parked heightmap runs its OnDisable first, as Unity would have.
    // What the game cannot tell apart is why it stays off by default:
    //  - heightmap.enabled / isActiveAndEnabled read false while parked (nothing in the game or Real Portal reads them);
    //  - a mod that sets enabled = false on a parked heightmap gets no OnDisable, and the next Poke(1) switches it back
    //    on; one that deactivates its GameObject gets its OnDisable late, when the heightmap is next unparked (a
    //    Poke(1), Park switched off) or destroyed, and if it reactivates it before that, neither the OnDisable nor the
    //    OnEnable (with its Regenerate) the game would have run;
    //  - a Poke(1) made during the LateUpdate phase is regenerated next frame (the game: this frame if the heightmap's
    //    own LateUpdate had not run yet); a heightmap switched back on runs its LateUpdate after the others;
    //  - m_doLateUpdate = 1 written straight into the field is noticed within 0.5 s.
    // Parking is refused while another mod patches Heightmap.OnEnable, OnDisable or LateUpdate.
    //
    // Far heightmaps (Real Portal's hook PlaceOf; its switches Far.FarRegenBudget and Far.HeightmapDrainSkip). The only
    // rebuilds this pass runs are the ones TerrainModifier.PokeHeightmaps queues (Poke(2): every modifier's Awake, and
    // its OnDestroy unless m_triggerOnPlaced), one for each heightmap the modifier's square reaches. Real Portal's far
    // areas bring in and take out thousands of modifiers (locations' levelled patches) away from the player: tens of
    // rebuilds in one pass while they load, and while they unload, rebuilds of heightmaps whose zones unload seconds
    // later (passes of 30-170 ms). With PlaceOf set, every heightmap the pass would rebuild is first placed by its centre:
    //  - Near (the player's own area; also whatever the hook leaves to the game, and everything while it is unset or
    //    has thrown): rebuilt in the pass, as above.
    //  - Far: rebuilt right after the game's pass, the longest queued first: the first one always, the next ones while
    //    the whole pass has taken less than FarBudgetMs. The rest stay queued for the next frame.
    //  - Unloading (only places that are going away hold its zone): stays queued and is not rebuilt; it goes with its
    //    zone. Should its zone be taken back, it is placed again (Near or Far) and rebuilt then.
    // A held heightmap stays queued (m_doLateUpdate 2), so what asks HaveQueuedRebuild (the grass) still waits for it.
    // Heightmap.ForceGenerateAll rebuilds on the spot what has to be right at once: when SnapToGround.SnappAll calls it
    // (a location or dungeon just placed), every heightmap in a snapper's zone or next to it, held or not, and Far ones
    // elsewhere keep waiting for the budget (a snapper takes the height straight below it); from anywhere else (a ship
    // or cart coming in) every Far one, as the game would have done earlier. Unloading ones are kept out of it (left at
    // 0 for the call) unless a snapper stands there: nothing is placed on ground that only unloading places hold. The
    // hook is asked by the filtered pass only (HeightmapIdle on, no other mod's patch on CustomLateUpdate); otherwise
    // the game's pass rebuilds every queued heightmap, held ones included, the next time it runs.
    [EngineModule]
    public static class HeightmapIdleModule
    {
        internal static readonly Feature Idle = Features.Register("HeightmapIdle", true,
            "MonoUpdaters calls CustomLateUpdate only on heightmaps that have a queued rebuild instead of on every loaded heightmap every frame.");
        internal static readonly Feature Park = Features.Register("HeightmapPark", false,
            "Experimental: disable idle heightmap components so Unity stops calling their LateUpdate (heightmap.enabled reads false while idle).");

        const float SweepSeconds = 2f;
        const float ParkSweepSeconds = 0.5f;
        const float ForeignCheckSeconds = 10f;
        const int ParksPerFrame = 128;
        const string LabOwners = "valheim.realportal.lab";

        static List<IMonoUpdater> s_instances;
        static MethodInfo s_customLateUpdate, s_lateUpdate, s_onEnable, s_onDisable;
        static Action<Heightmap> s_callOnDisable;

        // HeightmapIdle
        static readonly HashSet<Heightmap> s_pending = new HashSet<Heightmap>(new ByReference());
        static readonly List<IMonoUpdater> s_due = new List<IMonoUpdater>();
        static readonly List<IMonoUpdater> s_none = new List<IMonoUpdater>();
        static bool s_filtering;
        static float s_nextSweep;

        // HeightmapPark
        static readonly HashSet<Heightmap> s_parked = new HashSet<Heightmap>(new ByReference());
        static readonly List<Heightmap> s_toPark = new List<Heightmap>();
        static readonly List<Heightmap> s_temp = new List<Heightmap>();
        static bool s_toggling;
        static float s_nextParkSweep;

        static bool s_foreignLate, s_foreignToggle;
        static float s_nextForeignCheck;
        // The pass (which drains s_toPark) is patched in; set at the end of Install.
        static bool s_installed;

        // ---- Far heightmaps: Real Portal's hook (main thread; may be set or cleared at any time) ----

        // Where a heightmap whose rebuild is queued stands.
        public enum Place { Near, Far, Unloading }

        // Asked with the heightmap's centre, for every heightmap the filtered pass would rebuild (each pass while it stays
        // queued; one placed Unloading again after UnloadingRecheckSeconds) and for the queued ones when
        // ForceGenerateAll runs. Null: every one is Near. A hook that throws is taken as null until it is replaced or
        // HookRetrySeconds have passed (HookErrors; logged once per hook).
        public static Func<Vector3, Place> PlaceOf;

        // The time of the whole heightmap pass (ms) past which no further Far heightmap is rebuilt in that frame; the
        // first one always is.
        public static double FarBudgetMs = 2.0;

        const float HookRetrySeconds = 10f;
        // A heightmap placed Unloading is placed again this long after (not every pass: a drain holds hundreds).
        const float UnloadingRecheckSeconds = 0.5f;

        // Far, the longest queued first (s_farIn: the same ones); placed Far by this pass; placed Unloading by the last
        // pass, with when it was asked (two maps swapped each pass); kept out of the ForceGenerateAll call running now.
        static readonly List<Heightmap> s_farOrder = new List<Heightmap>();
        static readonly HashSet<Heightmap> s_farIn = new HashSet<Heightmap>(new ByReference());
        static readonly HashSet<Heightmap> s_farSeen = new HashSet<Heightmap>(new ByReference());
        static Dictionary<Heightmap, float> s_unloading = new Dictionary<Heightmap, float>(new ByReference());
        static Dictionary<Heightmap, float> s_unloadingNext = new Dictionary<Heightmap, float>(new ByReference());
        static readonly List<Heightmap> s_hidden = new List<Heightmap>();
        static int s_forceDepth;
        // SnapToGround.m_allSnappers; the zones of the snappers SnappAll is snapping now (ZoneKey).
        static AccessTools.FieldRef<List<SnapToGround>> s_snappers;
        static readonly HashSet<int> s_snapZones = new HashSet<int>();
        static bool s_snapping;
        static readonly System.Diagnostics.Stopwatch s_passClock = new System.Diagnostics.Stopwatch();
        // The heightmap pass running now has work (the game's part or Far ones), and how many the game's part rebuilds.
        static bool s_passOpen;
        static int s_passCount;
        static Func<Vector3, Place> s_brokenHook;
        static float s_brokenAt;

        // For the lab.
        public static int Pending => s_pending.Count;
        public static int Parked => s_parked.Count;
        // Cumulative: rebuilt by the game's pass; Far ones rebuilt after it; Far ones left for a later frame and
        // Unloading ones not rebuilt, both summed over passes; Unloading ones destroyed still queued (rebuilds saved);
        // Unloading ones kept out of ForceGenerateAll, and Far ones away from the snappers left to the budget in it;
        // the passes that had work (ms: total, slowest) and their Far part.
        public static long PassRebuilds, FarRebuilds, FarHeld, UnloadingHeld, UnloadedUnbuilt, ForceHidden, ForceDeferred;
        public static double PassMs, PassMsMax, FarMs, FarMsMax;
        public static long HookCalls, HookErrors;
        // The last pass: Far ones still queued, Unloading ones held.
        public static int FarQueued => s_farOrder.Count;
        public static int UnloadingQueued => s_unloading.Count;
        public static string Describe() =>
            $"idle filtering {s_filtering} (pending {s_pending.Count}, foreign CustomLateUpdate patches {s_foreignLate}), parked {s_parked.Count} (queued {s_toPark.Count}, foreign toggle patches {s_foreignToggle}); " +
            $"far hook {(PlaceOf != null ? "set" : "unset")}: rebuilt in the pass {PassRebuilds}, far {FarRebuilds} ({FarMs:0} ms, slowest {FarMsMax:0.0}), far held {FarHeld} (queued now {s_farOrder.Count}), " +
            $"unloading held {UnloadingHeld} (now {s_unloading.Count}, destroyed unbuilt {UnloadedUnbuilt}), kept out of ForceGenerateAll {ForceHidden} unloading and {ForceDeferred} far, " +
            $"slowest pass {PassMsMax:0.0} ms, hook errors {HookErrors}";

        static void Install(Harmony harmony)
        {
            s_instances = Heightmap.Instances;
            Type self = typeof(HeightmapIdleModule);
            s_customLateUpdate = AccessTools.Method(typeof(Heightmap), nameof(Heightmap.CustomLateUpdate));
            s_lateUpdate = AccessTools.Method(typeof(Heightmap), "LateUpdate");
            s_onEnable = AccessTools.Method(typeof(Heightmap), "OnEnable");
            s_onDisable = AccessTools.Method(typeof(Heightmap), "OnDisable");
            // The hooks that feed the pass go in first. If one of them cannot be patched, Install stops before the pass
            // is, and the module really is left out (as the log says) instead of filtering with nothing feeding it.
            harmony.Patch(AccessTools.Method(typeof(Heightmap), nameof(Heightmap.Poke)), postfix: new HarmonyMethod(self, nameof(Poked)));
            harmony.Patch(s_onEnable, prefix: new HarmonyMethod(self, nameof(EnableBefore)) { priority = Priority.First },
                postfix: new HarmonyMethod(self, nameof(EnableAfter)));
            harmony.Patch(s_onDisable, prefix: new HarmonyMethod(self, nameof(DisableBefore)) { priority = Priority.First });
            harmony.Patch(AccessTools.Method(typeof(Heightmap), "OnDestroy"), prefix: new HarmonyMethod(self, nameof(DestroyBefore)) { priority = Priority.First });
            // Through the patched method, so a parked heightmap's destruction runs OnDisable with every patch on it.
            s_callOnDisable = AccessTools.MethodDelegate<Action<Heightmap>>(s_onDisable);
            // Last, and first among its prefixes: the list is swapped before any other prefix of this shared helper
            // looks at it. The postfix rebuilds the Far heightmaps after the game's pass.
            harmony.Patch(AccessTools.Method(typeof(MonoUpdatersExtra), nameof(MonoUpdatersExtra.CustomLateUpdate)),
                prefix: new HarmonyMethod(self, nameof(LateUpdatePass)) { priority = Priority.First },
                postfix: new HarmonyMethod(self, nameof(LateUpdatePassDone)));
            s_installed = true;
            // Optional: without them ForceGenerateAll rebuilds every held heightmap, as the game.
            try
            {
                harmony.Patch(AccessTools.Method(typeof(Heightmap), nameof(Heightmap.ForceGenerateAll)),
                    prefix: new HarmonyMethod(self, nameof(ForceBefore)), finalizer: new HarmonyMethod(self, nameof(ForceAfter)));
                s_snappers = AccessTools.StaticFieldRefAccess<List<SnapToGround>>(AccessTools.Field(typeof(SnapToGround), "m_allSnappers"));
                harmony.Patch(AccessTools.Method(typeof(SnapToGround), nameof(SnapToGround.SnappAll)),
                    prefix: new HarmonyMethod(self, nameof(SnapBefore)), finalizer: new HarmonyMethod(self, nameof(SnapAfter)));
            }
            catch (Exception e)
            {
                s_snappers = null;
                EnginePlugin.Log?.LogWarning("HeightmapIdle: Heightmap.ForceGenerateAll or SnapToGround.SnappAll not patched, they rebuild held far heightmaps as well: " + e.Message);
            }
            Idle.Switched += _ => s_nextForeignCheck = 0f;
            Park.Switched += on =>
            {
                s_nextForeignCheck = 0f;
                if (on)
                    QueueAll();
                else
                    UnparkAll();
            };
        }

        // MonoUpdatersExtra.CustomLateUpdate(container, source, ...): for the Heightmap pass of MonoUpdaters.LateUpdate.
        static void LateUpdatePass(ref List<IMonoUpdater> source)
        {
            // Set again below for the heightmap pass with work; any other pass clears what a pass that threw left.
            s_passOpen = false;
            if (!ReferenceEquals(source, s_instances))
                return;
            s_passClock.Restart();
            float now = Time.unscaledTime;
            if (now >= s_nextForeignCheck)
            {
                s_nextForeignCheck = now + ForeignCheckSeconds;
                bool foreignToggle;
                try
                {
                    s_foreignLate = Foreign(s_customLateUpdate, true);
                    foreignToggle = Foreign(s_onEnable, false) || Foreign(s_onDisable, false) || Foreign(s_lateUpdate, true);
                }
                catch (Exception e)
                {
                    // Cannot tell who else is there: behave as the game.
                    EnginePlugin.Log?.LogWarning("HeightmapIdle: reading Harmony patch info failed, idle heightmaps are called as in the game: " + e.Message);
                    s_foreignLate = foreignToggle = true;
                }
                if (s_foreignToggle && !foreignToggle && Park.On)
                    QueueAll();     // everything was switched back on while the other patches were there
                s_foreignToggle = foreignToggle;
            }
            if (s_parked.Count > 0 || s_toPark.Count > 0)
                ParkStep(now);
            bool filter = Idle.On && !s_foreignLate;
            if (filter != s_filtering)
            {
                // Pokes are only collected while filtering: start from what is queued now. Held ones stay queued, so
                // the game's pass takes them now, or this one again as it starts.
                s_filtering = filter;
                s_pending.Clear();
                ForgetHeld();
                if (filter)
                    Sweep();
            }
            if (filter)
            {
                List<IMonoUpdater> due = Due(now);
                source = due;
                s_passCount = due.Count;
                s_passOpen = due.Count > 0 || s_farOrder.Count > 0;
            }
        }

        // After the pass: the Far heightmaps within the budget (unless another prefix skipped the pass), and the lab's
        // timing of passes with work.
        static void LateUpdatePassDone(float deltaTime, bool __runOriginal)
        {
            if (!s_passOpen)
                return;
            s_passOpen = false;
            if (__runOriginal)
            {
                PassRebuilds += s_passCount;
                if (s_farOrder.Count > 0)
                    RunFar(deltaTime);
            }
            double ms = s_passClock.Elapsed.TotalMilliseconds;
            PassMs += ms;
            if (ms > PassMsMax)
                PassMsMax = ms;
        }

        // ---- HeightmapIdle -------------------------------------------------------------------------------

        static List<IMonoUpdater> Due(float now)
        {
            if (now >= s_nextSweep)
            {
                s_nextSweep = now + SweepSeconds;
                Sweep();
            }
            if (s_pending.Count == 0)
            {
                if (s_farOrder.Count > 0 || s_unloading.Count > 0)
                    ForgetHeld();
                return s_none;
            }
            bool any = false;
            foreach (Heightmap h in s_pending)
                if (h.m_doLateUpdate == 2)
                {
                    any = true;
                    break;
                }
            if (!any)
            {
                s_pending.Clear();
                ForgetHeld();
                return s_none;
            }
            // In Instances order, the ones the game's pass would act on (duplicates included, as there); with PlaceOf
            // set, the Near ones of them, the Far ones queued behind it and the Unloading ones left out. All of them are
            // kept pending until a later pass sees them cleared (CustomLateUpdate can be skipped or throw).
            Func<Vector3, Place> placeOf = Hook(now);
            s_due.Clear();
            s_pending.Clear();
            s_farSeen.Clear();
            s_unloadingNext.Clear();
            List<IMonoUpdater> all = s_instances;
            for (int i = 0; i < all.Count; i++)
            {
                if (!(all[i] is Heightmap h) || h.m_doLateUpdate != 2)
                    continue;
                s_pending.Add(h);
                Place place = Place.Near;
                float asked = now;
                if (placeOf != null)
                {
                    if (s_unloading.TryGetValue(h, out float before) && now >= before && now - before < UnloadingRecheckSeconds)
                    {
                        place = Place.Unloading;
                        asked = before;
                    }
                    else
                        place = Ask(ref placeOf, h, now);
                }
                if (place == Place.Far)
                {
                    s_farSeen.Add(h);
                    if (s_farIn.Add(h))
                        s_farOrder.Add(h);
                }
                else if (place == Place.Unloading)
                    s_unloadingNext[h] = asked;
                else
                    s_due.Add(h);
            }
            Dictionary<Heightmap, float> was = s_unloading;
            s_unloading = s_unloadingNext;
            s_unloadingNext = was;
            s_unloadingNext.Clear();
            // The Far queue keeps its order; what is no longer queued, no longer Far or gone leaves it.
            if (s_farOrder.Count > 0)
            {
                int kept = 0;
                for (int i = 0; i < s_farOrder.Count; i++)
                {
                    Heightmap h = s_farOrder[i];
                    if (s_farSeen.Contains(h))
                        s_farOrder[kept++] = h;
                    else
                        s_farIn.Remove(h);
                }
                s_farOrder.RemoveRange(kept, s_farOrder.Count - kept);
            }
            UnloadingHeld += s_unloading.Count;
            return s_due;
        }

        static void Sweep()
        {
            List<IMonoUpdater> all = s_instances;
            for (int i = 0; i < all.Count; i++)
                if (all[i] is Heightmap h && h.m_doLateUpdate == 2)
                    s_pending.Add(h);
        }

        static void Poked(Heightmap __instance)
        {
            int state = __instance.m_doLateUpdate;
            if (state == 2)
            {
                if (s_filtering)
                    s_pending.Add(__instance);
            }
            else if (state == 1 && s_parked.Count > 0 && s_parked.Contains(__instance))
            {
                Unpark(__instance);
                s_toPark.Add(__instance);   // parked again once its LateUpdate has regenerated it
            }
        }

        // ---- Far heightmaps ------------------------------------------------------------------------------

        // The Far queue from its head: the first one always, then while the pass so far (the game's part included)
        // is under FarBudgetMs. Through each heightmap's own CustomLateUpdate, as the game's pass would call it.
        static void RunFar(float deltaTime)
        {
            double start = s_passClock.Elapsed.TotalMilliseconds, budget = FarBudgetMs;
            int at = 0, ran = 0;
            try
            {
                for (; at < s_farOrder.Count; at++)
                {
                    Heightmap h = s_farOrder[at];
                    if (h.m_doLateUpdate != 2 || !h)
                        continue;   // rebuilt meanwhile, or gone
                    if (ran > 0 && !(s_passClock.Elapsed.TotalMilliseconds < budget))
                        break;
                    ran++;
                    h.CustomLateUpdate(deltaTime);
                }
            }
            finally
            {
                // Everything before `at` is rebuilt (or was already). One whose CustomLateUpdate threw stays first and
                // leaves with the next pass, no longer queued (as in the game's pass, the exception goes on up).
                for (int i = 0; i < at; i++)
                    s_farIn.Remove(s_farOrder[i]);
                s_farOrder.RemoveRange(0, at);
                FarRebuilds += ran;
                for (int i = 0; i < s_farOrder.Count; i++)
                    if (s_farOrder[i].m_doLateUpdate == 2)
                        FarHeld++;
                double ms = s_passClock.Elapsed.TotalMilliseconds - start;
                FarMs += ms;
                if (ms > FarMsMax)
                    FarMsMax = ms;
            }
        }

        static void ForgetHeld()
        {
            s_farOrder.Clear();
            s_farIn.Clear();
            s_farSeen.Clear();
            s_unloading.Clear();
        }

        // Real Portal's switches moved: no place given before is kept (an Unloading one is otherwise reused for up to
        // UnloadingRecheckSeconds without asking the hook). What is queued stays pending and is placed afresh by the
        // next pass. Main thread, outside the pass.
        public static void ForgetPlaces() => ForgetHeld();

        // The hook, or null while it is the one that threw last and HookRetrySeconds have not passed.
        static Func<Vector3, Place> Hook(float now)
        {
            Func<Vector3, Place> hook = PlaceOf;
            return hook != null && ReferenceEquals(hook, s_brokenHook) && now - s_brokenAt < HookRetrySeconds ? null : hook;
        }

        // Where the hook places a heightmap. One that throws places it Near, and is left out (hook = null) for the
        // rest of this look and until it is replaced or HookRetrySeconds have passed.
        static Place Ask(ref Func<Vector3, Place> hook, Heightmap h, float now)
        {
            HookCalls++;
            try
            {
                Place place = hook(h.transform.position);
                return place == Place.Far || place == Place.Unloading ? place : Place.Near;
            }
            catch (Exception e)
            {
                HookErrors++;
                if (!ReferenceEquals(s_brokenHook, hook))
                    EnginePlugin.Log?.LogWarning($"HeightmapIdle: PlaceOf threw, far heightmaps are rebuilt as in the game until it is replaced (asked again every {HookRetrySeconds:F0} s): {e}");
                s_brokenHook = hook;
                s_brokenAt = now;
                hook = null;
                return Place.Near;
            }
        }

        // Heightmap.ForceGenerateAll rebuilds every queued heightmap on the spot: the Unloading ones are left at 0 for
        // the call and queued again after it (the finalizer; an inner call leaves them to the outer one). Called by
        // SnapToGround.SnappAll (after a location or dungeon is placed), whose snappers each take the height straight
        // below them (ZoneSystem.GetGroundHeight, a ray down onto the terrain): there the Far ones away from every
        // snapper's zone and the zones around it wait for the budget as well, and what lies under a snapper is rebuilt,
        // whatever its place. A ship or cart coming in (or anything else) gets every Far one rebuilt, as in the game.
        static void ForceBefore()
        {
            if (++s_forceDepth != 1 || !s_filtering || s_pending.Count == 0)
                return;
            float now = Time.unscaledTime;
            Func<Vector3, Place> placeOf = Hook(now);
            if (placeOf == null)
                return;
            bool snapping = s_snapping;
            foreach (Heightmap h in s_pending)
            {
                if (placeOf == null)
                    break;
                if (h.m_doLateUpdate != 2 || !h)
                    continue;
                Place place = Ask(ref placeOf, h, now);
                if (place == Place.Near || snapping && UnderSnapper(h))
                    continue;
                if (place == Place.Unloading)
                    ForceHidden++;
                else if (snapping)
                    ForceDeferred++;
                else
                    continue;
                s_hidden.Add(h);
            }
            for (int i = 0; i < s_hidden.Count; i++)
                s_hidden[i].m_doLateUpdate = 0;
        }

        static Exception ForceAfter(Exception __exception)
        {
            if (--s_forceDepth > 0)
                return __exception;
            s_forceDepth = 0;
            for (int i = 0; i < s_hidden.Count; i++)
                if (s_hidden[i].m_doLateUpdate == 0)
                    s_hidden[i].m_doLateUpdate = 2;
            s_hidden.Clear();
            return __exception;
        }

        // SnapToGround.SnappAll: the zones of the snappers it is about to snap (every one in the game's list), while
        // anything is queued. One that is gone (not taken out of the list) leaves the call to the game's ForceGenerateAll.
        static void SnapBefore()
        {
            s_snapping = false;
            s_snapZones.Clear();
            if (!s_filtering || s_pending.Count == 0 || s_snappers == null || PlaceOf == null)
                return;
            List<SnapToGround> all = s_snappers();
            if (all == null || all.Count == 0)
                return;
            for (int i = 0; i < all.Count; i++)
            {
                SnapToGround s = all[i];
                if (!s)
                {
                    s_snapZones.Clear();
                    return;
                }
                s_snapZones.Add(ZoneKey(ZoneSystem.GetZone(s.transform.position)));
            }
            s_snapping = true;
        }

        static Exception SnapAfter(Exception __exception)
        {
            s_snapping = false;
            s_snapZones.Clear();
            return __exception;
        }

        // A snapper stands in the heightmap's zone or one next to it. A heightmap other than a zone's (a distant LOD
        // one: never queued by the game) counts as under one.
        static bool UnderSnapper(Heightmap h)
        {
            if (h.IsDistantLod || h.m_width * h.m_scale > 64.5f)
                return true;
            Vector2s zone = ZoneSystem.GetZone(h.transform.position);
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    if (s_snapZones.Contains(ZoneKey(new Vector2s(zone.x + dx, zone.y + dy))))
                        return true;
            return false;
        }

        static int ZoneKey(Vector2s zone) => (zone.x << 16) | (ushort)zone.y;

        // ---- HeightmapPark -------------------------------------------------------------------------------

        static bool EnableBefore(Heightmap __instance)
        {
            if (s_toggling)
                return false;   // our own switch back on: the game never saw it disabled
            if (s_parked.Count > 0 && s_parked.Remove(__instance))
            {
                // Someone else set enabled = true on a parked heightmap: for the game it already was enabled, a no-op.
                s_toPark.Add(__instance);
                return false;
            }
            return true;
        }

        static void EnableAfter(Heightmap __instance, bool __runOriginal)
        {
            if (!__runOriginal || s_toggling)
                return;
            if (s_filtering && __instance.m_doLateUpdate == 2)
                s_pending.Add(__instance);
            // Without the pass nothing would ever take them off the queue.
            if (Park.On && s_installed)
                s_toPark.Add(__instance);
        }

        static bool DisableBefore() => !s_toggling;

        // Unity calls OnDisable on an enabled component before OnDestroy; for a parked one the game expects that too
        // (out of Heightmap.Instances, unsubscribed from graphics settings).
        static void DestroyBefore(Heightmap __instance)
        {
            // Far heightmaps (lab): one held as Unloading goes without the rebuild.
            if (s_unloading.Count > 0 && s_unloading.Remove(__instance) && __instance.m_doLateUpdate == 2)
                UnloadedUnbuilt++;
            if (s_parked.Count > 0 && s_parked.Remove(__instance))
                s_callOnDisable(__instance);
        }

        static void ParkStep(float now)
        {
            if (s_parked.Count > 0 && now >= s_nextParkSweep)
            {
                // m_doLateUpdate = 1 written without Poke.
                s_nextParkSweep = now + ParkSweepSeconds;
                s_temp.Clear();
                foreach (Heightmap h in s_parked)
                    if (h.m_doLateUpdate == 1)
                        s_temp.Add(h);
                foreach (Heightmap h in s_temp)
                {
                    Unpark(h);
                    s_toPark.Add(h);
                }
            }
            if (!Park.On || s_foreignToggle)
            {
                if (s_parked.Count > 0)
                    UnparkAll();
                s_toPark.Clear();
                return;
            }
            int budget = ParksPerFrame;
            for (int i = s_toPark.Count - 1; i >= 0 && budget > 0; i--)
            {
                Heightmap h = s_toPark[i];
                if (h != null && !s_parked.Contains(h))
                {
                    if (h.m_doLateUpdate == 1)
                        continue;   // its own LateUpdate still has to regenerate it
                    // Only what the game has enabled and active: parking anything else would hide an OnDisable/OnEnable.
                    if (h.isActiveAndEnabled)
                    {
                        ParkOne(h);
                        budget--;
                    }
                }
                int last = s_toPark.Count - 1;
                s_toPark[i] = s_toPark[last];
                s_toPark.RemoveAt(last);
            }
        }

        static void ParkOne(Heightmap h)
        {
            s_toggling = true;
            try
            {
                h.enabled = false;
            }
            finally
            {
                s_toggling = false;
            }
            s_parked.Add(h);
        }

        static void Unpark(Heightmap h)
        {
            if (!s_parked.Remove(h) || !h)
                return;
            if (!h.gameObject.activeInHierarchy)
            {
                // Its GameObject was deactivated while parked. The game ran OnDisable at that moment (out of Instances,
                // unsubscribed from graphics settings); Unity did not, the component being off. Run it now, through the
                // patched method, so the game's state is its own again: switched back on below while inactive, it gets
                // the whole original OnEnable (Instances, subscription, Regenerate) when reactivated, and nothing when
                // destroyed while inactive, as in the game. Without this it would stay listed (twice once reactivated)
                // and subscribed after its destruction, since it is no longer parked for DestroyBefore.
                try
                {
                    s_callOnDisable(h);
                }
                catch (Exception e)
                {
                    // Unity logs an exception thrown by OnDisable and carries on; so does this.
                    EnginePlugin.Log?.LogError("HeightmapPark: OnDisable of a deactivated parked heightmap failed: " + e);
                }
            }
            s_toggling = true;
            try
            {
                h.enabled = true;
            }
            finally
            {
                s_toggling = false;
            }
        }

        static void UnparkAll()
        {
            s_temp.Clear();
            s_temp.AddRange(s_parked);
            foreach (Heightmap h in s_temp)
                Unpark(h);
            s_parked.Clear();
            s_toPark.Clear();
        }

        // Heightmap.Instances is what the game has enabled (OnEnable adds, OnDisable removes).
        static void QueueAll()
        {
            List<IMonoUpdater> all = s_instances;
            for (int i = 0; i < all.Count; i++)
                if (all[i] is Heightmap h)
                    s_toPark.Add(h);
        }

        // ---- helpers -------------------------------------------------------------------------------------

        // Another mod's patch on the method (ours and the lab's timers aside).
        static bool Foreign(MethodBase method, bool transpilersToo)
        {
            if (method == null)
                return false;
            HarmonyLib.Patches info = Harmony.GetPatchInfo(method);
            if (info == null)
                return false;
            return Foreign(info.Prefixes) || Foreign(info.Postfixes) || Foreign(info.Finalizers)
                || transpilersToo && (Foreign(info.Transpilers) || Foreign(info.ILManipulators));
        }

        static bool Foreign(IEnumerable<HarmonyLib.Patch> patches)
        {
            if (patches == null)
                return false;
            foreach (HarmonyLib.Patch p in patches)
            {
                if (p.PatchMethod?.DeclaringType == typeof(HeightmapIdleModule))
                    continue;
                if (p.owner != null && p.owner.StartsWith(LabOwners, StringComparison.Ordinal))
                    continue;
                return true;
            }
            return false;
        }

        sealed class ByReference : IEqualityComparer<Heightmap>
        {
            public bool Equals(Heightmap a, Heightmap b) => ReferenceEquals(a, b);
            public int GetHashCode(Heightmap h) => RuntimeHelpers.GetHashCode(h);
        }
    }
}
