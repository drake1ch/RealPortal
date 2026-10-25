using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace RealPortal.Engine
{
    // AreaIndex (on by default). EffectArea.IsPointInsideArea(p, type, radius) and EffectArea.GetBaseValue(p, radius) run
    // Physics.OverlapSphereNonAlloc on the character_trigger layer and GetComponent<EffectArea> on every collider hit,
    // and return the first hit whose (first) EffectArea has the type (or count the PlayerBase ones). Fire-afraid AIs ask
    // on every AI tick (BaseAI.AvoidFire: IsPointInsideArea(position, Fire, 3) and more), items, spawners, beds, cooking
    // stations and the player ask every second or two, and almost every answer is "none": no fire next to a creature,
    // no base next to a spawn point. In a base each such query also pays a GetComponent per PlayerBase sphere it is in.
    //
    // This keeps a 32 m grid (x/z) of every loaded EffectArea and answers only the "none" case: when no indexed area of
    // the asked type can touch the query sphere, the answer is null (0), which is what the game's query returns then;
    // whenever one could, the game's own method runs unchanged and its answer is used (so which area is returned, in
    // PhysX order, and the 128-hit cap stay the game's). Exactness rests on the index being a superset:
    //  - every EffectArea is registered from its Awake (postfix: Unity runs Awake for every component of an active
    //    object, disabled ones included, so every area PhysX can report has had it), dropped in OnDestroy (prefix),
    //    and the game's own lists (EffectArea.GetAllAreas, EffectArea.Instances) are swept for anything else;
    //  - an area is tested through the colliders of its own GameObject (GetComponent<EffectArea> on a hit collider
    //    looks only there) by their world bounds, expanded by a margin, against the query sphere expanded by a float
    //    margin; the type is read live (m_type is public), and an area that is not first on its object is still a
    //    candidate (conservative);
    //  - nothing is read from an area in its Awake (a load burst stays cheap): it is sampled at the next upkeep (once a
    //    frame, from the first query, at most 128 samples), and until then any query of its type goes to the game;
    //  - an area is "pinned" in the grid only after two samples 1 s apart agree (same transform matrix, same collider
    //    bounds, never seen on a Rigidbody); one whose colliders are out of the physics scene while its object stays
    //    active (disabled colliders) at a pose where its bounds were seen is pinned with those. Pending areas and areas
    //    on rigidbodies (ships, carts, creatures, items) are tested on every query by their current bounds plus 8 m
    //    (motion since PhysX last synced transforms). Too many of those in the scene (a fresh load of hundreds of
    //    pieces) and the query just goes to the game. "On a Rigidbody" is remembered per area once seen, and is looked
    //    up through the hierarchy for a sample out of the scene (Collider.attachedRigidbody is null for a disabled
    //    collider or an inactive object), so a creature frozen by SetActive(false) is never pinned where it stood;
    //  - an area whose object is inactive (and whose EffectArea component is enabled) is "dormant": none of its
    //    colliders is in the physics scene, so no query can report it, and it costs nothing per query. The only way
    //    back into the scene is activating the object, which runs its EffectArea.OnEnable; a postfix there makes it
    //    pending and unsampled again (every query of its type goes to the game until it is resampled). This is the
    //    common case of fireplaces: Fireplace.Start turns off the PlayerBase object of every fire not placed by a
    //    player (villages, camps) and Fireplace.UpdateState turns off the Fire/Heat object of every unlit fire;
    //  - a collider can change shape at a fixed pose: the game itself sets the radius of a crafting station's
    //    m_effectAreaCollider (its PlayerBase sphere) in CraftingStation.GetExtensions when the number of extensions
    //    changes (build range 20 m + 4 m per extension). That method is watched (radius read before and after a call
    //    that reaches its reshape branch, or every call while another mod patches it) and a change re-pends every area
    //    on that collider's object at once, unsampled.
    // Pinned areas and dormant ones are still re-checked round-robin, as a safety net for what the game never does
    // (code moving a piece without a Rigidbody, a mod reshaping a collider, a component disabled while its object is
    // inactive): at least every 4 s of query time, faster while queries are answered from the index (each "none" answer
    // buys a few re-checks, at most 64 a frame); a pinned area found changed goes back to "pending". Until that re-check
    // a query near such an area's new shape could miss it, and an unpinned area teleported more than 8 m could be
    // missed until the next physics step. Colliders added to an area's object after it was sampled are picked up at
    // its next full sample (pinned: every 8th re-check). AreaIndexVerify runs the game's query after every "none"
    // answer and counts any disagreement (VerifyMismatches) to prove this in the lab.
    // Lab check for the station case: a workbench with 2-4 extensions, reload, equip the hammer (Hud.UpdateBuild walks
    // every station's GetStationBuildRange), then AreaIndexVerify with item and spawn probes 22-30 m from the bench:
    // Reshapes counts the radius changes seen, VerifyMismatches must stay 0.
    // Off: the prefixes return at once and the game's methods run; the registry is still kept (cheap), and switching
    // on re-checks every area before any of it is trusted. Stands down (the game's query) while another mod patches
    // IsPointInsideArea or GetBaseValue: nothing is queued or re-checked meanwhile, and every area is re-checked again
    // when it ends. The Burning radius-0.25 case of IsPointInsideArea is the game's bounds-list walk already and is
    // left to it.
    //
    // StealthSystem.GetLightLevel is NOT replaced, deliberately: the game sums the lights of a snapshot taken by
    // Object.FindObjectsOfType<Light>() at most once a second (sorted by instance id; it includes disabled Light
    // components and keeps lights deactivated since, misses lights created since) and reads each light's position, type
    // and range live on every call. No registry can reproduce that set without the same native search (a Light has no
    // script hook), the float sum depends on that order, and an exact spatial filter of the snapshot would need a
    // per-light motion/type/range audit costing about what the loop costs (2 calls a second, only while crouching).
    // Instead the call is timed (observe-only prefix/postfix) so the lab can see what the refresh costs with far areas
    // (StealthRefreshMs vs StealthPlainMs) before anyone trades exactness for it.
    [EngineModule]
    public static class AreaIndexModule
    {
        internal static readonly Feature Index = Features.Register("AreaIndex", true,
            "EffectArea.IsPointInsideArea/GetBaseValue answer 'no area here' from a grid of the loaded effect areas instead of a physics overlap plus GetComponent per hit; any possible hit still runs the game's query.");
        internal static readonly Feature Verify = Features.Register("AreaIndexVerify", false,
            "Lab: after every 'no area' answer of AreaIndex the game's query runs too, its answer is used and any difference is counted and logged.");

        const float CellSize = 32f;
        const float StableSeconds = 1f;
        const float LooseResampleSeconds = 10f;
        const float LooseMargin = 8f;
        const int PendingPerFrame = 128;
        const int AuditPerFrame = 64;            // most pinned re-checks in one upkeep
        const float AuditCycleSeconds = 4f;      // every pinned (and dormant) area re-checked at least this often
        const int AuditPerAnswer = 4;            // re-checks each "none" answer buys
        const int DormantAuditPerFrame = 32;
        const int SweepPerFrame = 16;
        const int UnpinnedScanLimit = 256;
        const int UnpinnedMatchLimit = 16;
        const int MaxCellsPerArea = 64;
        const float ForeignCheckSeconds = 10f;
        const float ExtensionPeriod = 2f;        // CraftingStation.GetExtensions reshapes once m_updateExtensionTimer >= 2
        const string LabOwners = "valheim.realportal.lab";

        const int Pending = 0, Pinned = 1, Loose = 2, Dormant = 3;

        // ---- lab counters (public static: read by the lab's 'counters' command) -------------------------------
        public static long Queries;              // IsPointInsideArea/GetBaseValue calls looked at while on
        public static long Proven;               // answered "none" without the physics query
        public static long Fallbacks;            // went to the game's query (a candidate was near, or limits)
        public static long Unpins;               // pinned areas found changed by the audit
        public static long Audits;               // pinned areas re-checked
        public static long Wakes;                // areas made pending again by OnEnable, a reshape or the dormant audit
        public static long Reshapes;             // CraftingStation.GetExtensions changed its effect area collider's radius
        public static long LateRegistered;       // areas found by the sweeps or in a game answer, not by Awake
        public static long VerifyChecks;
        public static long VerifyMismatches;
        public static int Areas => s_recs.Count;
        public static int PinnedAreas => s_pinned.Count;
        public static int UnpinnedAreas => s_unpinned.Count;
        public static int DormantAreas => s_dormant.Count;
        public static int Cells => s_cells.Count;
        public static bool ForeignPatched => s_foreign;
        public static bool ExtensionsGated => s_extGated;
        // StealthSystem.GetLightLevel, observed only.
        public static long StealthCalls;
        public static long StealthRefreshCalls;
        public static int StealthLights;
        public static double StealthRefreshMs => StealthRefreshCalls > 0 ? s_stealthRefreshTicks * 1000.0 / Stopwatch.Frequency / StealthRefreshCalls : 0;
        public static double StealthPlainMs => StealthCalls - StealthRefreshCalls > 0 ? s_stealthPlainTicks * 1000.0 / Stopwatch.Frequency / (StealthCalls - StealthRefreshCalls) : 0;
        public static double StealthMaxMs => s_stealthMaxTicks * 1000.0 / Stopwatch.Frequency;

        public static string Describe() =>
            $"AreaIndex: on {Index.On}, foreign {s_foreign}, areas {s_recs.Count} (pinned {s_pinned.Count}, unpinned {s_unpinned.Count}, dormant {s_dormant.Count}, huge {s_huge.Count}), cells {s_cells.Count}, "
            + $"queries {Queries}, none answered {Proven}, to the game {Fallbacks}, audits {Audits}, unpins {Unpins}, wakes {Wakes}, reshapes {Reshapes} (gated {s_extGated}), late {LateRegistered}, verify {VerifyChecks}/{VerifyMismatches} mismatches; "
            + $"stealth light: calls {StealthCalls}, refreshes {StealthRefreshCalls}, lights {StealthLights}, refresh {StealthRefreshMs:F3} ms, plain {StealthPlainMs:F3} ms, max {StealthMaxMs:F3} ms";

        sealed class Rec
        {
            internal EffectArea Area;
            internal Transform Tr;
            internal GameObject Go;              // set at the first sample (not read in Awake)
            internal Collider[] Cols = Array.Empty<Collider>();
            internal int State;
            internal int Slot = -1;              // index in s_pinned (Pinned), s_unpinned (Pending, Loose) or s_dormant (Dormant)
            internal bool Sampled;               // false until the first sample: tested as "may hit" until then
            internal bool EverRigid;             // seen on a Rigidbody once: never pinned again
            internal Matrix4x4 Pose;             // last sample
            internal Bounds Raw;                 // last sample, union of the colliders' bounds
            internal bool RawEmpty;
            internal Matrix4x4 GoodPose;         // pose of the last non-empty sample
            internal Bounds GoodRaw;
            internal bool HasGood;
            internal Bounds PinRaw;              // Pinned: the bounds it was pinned with
            internal Bounds Box;                 // Pinned: PinRaw plus margin
            internal readonly List<long> Keys = new List<long>();
            internal bool Huge;
            internal int AuditCount;
            internal float Due;
            internal int Stamp;                  // queue entries with an older stamp are stale
            internal bool Dead;
        }

        struct Queued
        {
            internal Rec Rec;
            internal int Stamp;
        }

        static readonly Dictionary<EffectArea, Rec> s_recs = new Dictionary<EffectArea, Rec>(ByReference.Instance);
        static readonly Dictionary<long, List<Rec>> s_cells = new Dictionary<long, List<Rec>>();
        static readonly List<Rec> s_pinned = new List<Rec>();
        static readonly List<Rec> s_unpinned = new List<Rec>();
        static readonly List<Rec> s_dormant = new List<Rec>();
        static readonly List<Rec> s_huge = new List<Rec>();
        // Samples to take: the first one of a new (or re-checked) area at the next upkeep, the second one StableSeconds
        // after the first, and a rigidbody area's re-check every LooseResampleSeconds. Each queue's due times only grow.
        // Filled only while s_seeded (on, not stood down, not broken): see StandDown.
        static readonly Queue<Queued> s_firstQ = new Queue<Queued>();
        static readonly Queue<Queued> s_stableQ = new Queue<Queued>();
        static readonly Queue<Queued> s_looseQ = new Queue<Queued>();
        static readonly List<Collider> s_cols = new List<Collider>();
        static readonly List<EffectArea> s_areaBuf = new List<EffectArea>();
        static readonly Stack<List<Rec>> s_freeLists = new Stack<List<Rec>>();
        static int s_frame = -1, s_auditAt, s_dormantAt, s_sweepAt, s_sweepAllAt, s_answered;
        static float s_lastUpkeep, s_auditCredit, s_dormantCredit;
        static bool s_seeded, s_foreign, s_installed, s_broken, s_canDorm;
        static bool s_extGated = true;
        static float s_nextForeignCheck;
        static MethodInfo s_inside, s_baseValue, s_getExtensions;
        static AccessTools.FieldRef<CraftingStation, float> s_extTimer;
        static int s_mismatchLogs;

        static AccessTools.FieldRef<StealthSystem, float> s_lightUpdate;
        static AccessTools.FieldRef<StealthSystem, Light[]> s_lights;
        static long s_stealthRefreshTicks, s_stealthPlainTicks, s_stealthMaxTicks;

        static void Install(Harmony harmony)
        {
            Type self = typeof(AreaIndexModule);
            s_inside = AccessTools.DeclaredMethod(typeof(EffectArea), nameof(EffectArea.IsPointInsideArea));
            s_baseValue = AccessTools.DeclaredMethod(typeof(EffectArea), nameof(EffectArea.GetBaseValue));
            MethodInfo awake = AccessTools.DeclaredMethod(typeof(EffectArea), "Awake");
            MethodInfo destroy = AccessTools.DeclaredMethod(typeof(EffectArea), "OnDestroy");
            MethodInfo enable = AccessTools.DeclaredMethod(typeof(EffectArea), "OnEnable");
            s_getExtensions = AccessTools.DeclaredMethod(typeof(CraftingStation), "GetExtensions");
            try
            {
                if (AccessTools.DeclaredField(typeof(CraftingStation), "m_updateExtensionTimer") is FieldInfo f && f.FieldType == typeof(float))
                    s_extTimer = AccessTools.FieldRefAccess<CraftingStation, float>("m_updateExtensionTimer");
            }
            catch (Exception)
            {
                s_extTimer = null;
            }
            if (s_inside == null || s_baseValue == null || awake == null || destroy == null)
                EnginePlugin.Log.LogWarning("AreaIndex: an EffectArea method is missing (game update?), effect area queries stay as in the game.");
            else if (s_getExtensions == null || s_extTimer == null)
                // The game reshapes station areas there; without watching it the index cannot be kept exact.
                EnginePlugin.Log.LogWarning("AreaIndex: CraftingStation.GetExtensions or m_updateExtensionTimer is missing (game update?), effect area queries stay as in the game.");
            else
            {
                // Registry and the shape watch first: if they cannot be fed, the queries are not patched.
                harmony.Patch(awake, postfix: new HarmonyMethod(self, nameof(AwakeAfter)));
                harmony.Patch(destroy, prefix: new HarmonyMethod(self, nameof(DestroyBefore)) { priority = Priority.First });
                // Ours around everything else on the method, so a reshape by another patch there is seen too.
                harmony.Patch(s_getExtensions, prefix: new HarmonyMethod(self, nameof(ExtensionsBefore)) { priority = Priority.First },
                    postfix: new HarmonyMethod(self, nameof(ExtensionsAfter)) { priority = Priority.Last });
                // Without the OnEnable wake nothing may go dormant (it would never come back); everything else stands.
                if (enable != null)
                {
                    try
                    {
                        harmony.Patch(enable, postfix: new HarmonyMethod(self, nameof(EnableAfter)));
                        s_canDorm = true;
                    }
                    catch (Exception e)
                    {
                        EnginePlugin.Log.LogWarning("AreaIndex: EffectArea.OnEnable not patched, inactive areas are tested like pending ones: " + e.Message);
                    }
                }
                harmony.Patch(s_inside, prefix: new HarmonyMethod(self, nameof(InsideBefore)), postfix: new HarmonyMethod(self, nameof(InsideAfter)));
                harmony.Patch(s_baseValue, prefix: new HarmonyMethod(self, nameof(BaseValueBefore)), postfix: new HarmonyMethod(self, nameof(BaseValueAfter)));
                s_installed = true;
                // Nothing indexed while off is trusted after switching on: every area is sampled again (at the first
                // query); switching off drops the queues so nothing piles up meanwhile.
                Index.Switched += _ => StandDown();
            }

            // StealthSystem.GetLightLevel: timing only.
            try
            {
                MethodInfo light = AccessTools.DeclaredMethod(typeof(StealthSystem), nameof(StealthSystem.GetLightLevel));
                s_lightUpdate = AccessTools.FieldRefAccess<StealthSystem, float>("m_lastLightListUpdate");
                s_lights = AccessTools.FieldRefAccess<StealthSystem, Light[]>("m_allLights");
                if (light != null)
                    harmony.Patch(light, prefix: new HarmonyMethod(self, nameof(LightBefore)) { priority = Priority.First },
                        postfix: new HarmonyMethod(self, nameof(LightAfter)) { priority = Priority.Last });
            }
            catch (Exception e)
            {
                EnginePlugin.Log.LogWarning("AreaIndex: StealthSystem.GetLightLevel timing not installed: " + e.Message);
            }
        }

        // ---- registry ---------------------------------------------------------------------------------------

        static void AwakeAfter(EffectArea __instance)
        {
            if (s_broken)
                return;
            try
            {
                Register(__instance, false);
            }
            catch (Exception e)
            {
                Break(e);
            }
        }

        static void DestroyBefore(EffectArea __instance)
        {
            if (s_broken)
                return;
            try
            {
                if (!ReferenceEquals(__instance, null) && s_recs.TryGetValue(__instance, out Rec r))
                    Drop(r);
            }
            catch (Exception e)
            {
                Break(e);
            }
        }

        // Activation of the area's object (or of the component): its colliders may be back in the physics scene with
        // any shape at any place, so it is re-checked before any query is answered without it.
        static void EnableAfter(EffectArea __instance)
        {
            if (!s_seeded || s_broken)
                return;
            try
            {
                if (!ReferenceEquals(__instance, null) && s_recs.TryGetValue(__instance, out Rec r))
                    Wake(r);
            }
            catch (Exception e)
            {
                Break(e);
            }
        }

        // CraftingStation.GetExtensions: m_effectAreaCollider's radius before the call, when the call can reshape it.
        // NaN: nothing to compare.
        static void ExtensionsBefore(CraftingStation __instance, out float __state)
        {
            __state = float.NaN;
            if (!s_seeded || s_broken)
                return;
            try
            {
                // The game's method reshapes only in its 'm_updateExtensionTimer >= 2' branch; while another mod patches
                // it, every call is watched.
                if (s_extGated && s_extTimer(__instance) < ExtensionPeriod)
                    return;
                __state = RadiusOf(__instance.m_effectAreaCollider);
            }
            catch (Exception e)
            {
                Break(e);
            }
        }

        static void ExtensionsAfter(CraftingStation __instance, float __state)
        {
            if (float.IsNaN(__state) || !s_seeded || s_broken)
                return;
            try
            {
                Collider c = __instance.m_effectAreaCollider;
                if (c == null || RadiusOf(c).Equals(__state))
                    return;
                Reshapes++;
                s_areaBuf.Clear();
                c.GetComponents(s_areaBuf);
                for (int i = 0; i < s_areaBuf.Count; i++)
                {
                    EffectArea a = s_areaBuf[i];
                    if (s_recs.TryGetValue(a, out Rec r))
                        Wake(r);
                    else
                        Register(a, true);
                }
                s_areaBuf.Clear();
            }
            catch (Exception e)
            {
                s_areaBuf.Clear();
                Break(e);
            }
        }

        // The radius GetExtensions sets (sphere or capsule), NaN for anything else.
        static float RadiusOf(Collider c)
        {
            if (c == null)
                return float.NaN;
            if (c is SphereCollider sphere)
                return sphere.radius;
            if (c is CapsuleCollider capsule)
                return capsule.radius;
            return float.NaN;
        }

        // Our own bookkeeping failed: the index is not trusted again this session, every query goes to the game,
        // and the game's own methods are never disturbed by it. Everything it holds is let go.
        static void Break(Exception e)
        {
            if (s_broken)
                return;
            s_broken = true;
            s_seeded = false;
            EnginePlugin.Log?.LogError("AreaIndex: index failed, effect area queries go to the game for the rest of the session: " + e);
            s_firstQ.Clear();
            s_stableQ.Clear();
            s_looseQ.Clear();
            s_recs.Clear();
            s_cells.Clear();
            s_pinned.Clear();
            s_unpinned.Clear();
            s_dormant.Clear();
            s_huge.Clear();
            s_freeLists.Clear();
        }

        // Off or stood down: no sample is queued or taken meanwhile (Register queues only while seeded), and the next
        // usable query re-checks every area (Reseed) before any of it is trusted again.
        static void StandDown()
        {
            s_seeded = false;
            s_firstQ.Clear();
            s_stableQ.Clear();
            s_looseQ.Clear();
        }

        static void Register(EffectArea area, bool late)
        {
            if (area == null || s_recs.ContainsKey(area))
                return;
            // Nothing is read from the area here (Awake of a load burst stays cheap): it is sampled at the next upkeep,
            // and until then any query of its type goes to the game. While off or stood down it is only known;
            // the next Reseed queues everything.
            var r = new Rec { Area = area, Tr = area.transform, State = Pending };
            s_recs.Add(area, r);
            Add(s_unpinned, r);
            if (late)
                LateRegistered++;
            if (s_seeded)
                Queue(s_firstQ, r, 0f);
        }

        static void Drop(Rec r)
        {
            r.Dead = true;
            s_recs.Remove(r.Area);
            if (r.State == Pinned)
                Unindex(r);
            else if (r.State == Dormant)
                RemoveAt(s_dormant, r);
            else
                RemoveAt(s_unpinned, r);
        }

        // Back to pending and unsampled (every query of its type goes to the game until its next sample).
        static void Wake(Rec r)
        {
            if (!s_seeded || r.Dead)
                return;
            if (r.State == Pending && !r.Sampled)
                return;             // its first sample is queued already
            if (r.State == Pinned)
            {
                Unindex(r);
                Add(s_unpinned, r);
            }
            else if (r.State == Dormant)
            {
                RemoveAt(s_dormant, r);
                Add(s_unpinned, r);
            }
            r.State = Pending;
            r.Sampled = false;
            Wakes++;
            Queue(s_firstQ, r, 0f);
        }

        // Out of the lists the queries look at: see CanDorm. The caller has taken it out of its list.
        static void Sleep(Rec r)
        {
            r.State = Dormant;
            r.Stamp++;              // queued samples of it are void
            Add(s_dormant, r);
        }

        // Its object is inactive, so none of its colliders (all on that object) is in the physics scene, and the only
        // way back is activating the object, which runs this enabled component's OnEnable (EnableAfter). Areas whose
        // colliders are merely disabled, or whose component is disabled (no OnEnable on activation), stay pending.
        static bool CanDorm(Rec r) => s_canDorm && !ReferenceEquals(r.Go, null) && !r.Go.activeInHierarchy && r.Area.enabled;

        // A pinned area the audit found changed: back to the live-tested set, with that sample as its first one.
        static void Repend(Rec r, Matrix4x4 pose, Bounds raw, bool empty, float now)
        {
            Unindex(r);
            Add(s_unpinned, r);
            Note(r, pose, raw, empty);
            Queue(s_stableQ, r, now + StableSeconds);
        }

        static void Note(Rec r, Matrix4x4 pose, Bounds raw, bool empty)
        {
            r.Sampled = true;
            r.Pose = pose;
            r.Raw = raw;
            r.RawEmpty = empty;
            if (!empty)
            {
                r.GoodPose = pose;
                r.GoodRaw = raw;
                r.HasGood = true;
            }
        }

        static void Queue(Queue<Queued> queue, Rec r, float due)
        {
            r.Due = due;
            r.Stamp++;
            queue.Enqueue(new Queued { Rec = r, Stamp = r.Stamp });
        }

        // The area object's colliders (refreshed), its transform matrix, the union of their world bounds (empty when
        // no collider is in the physics scene: disabled or inactive) and whether any is on a Rigidbody.
        static void Sample(Rec r, out Matrix4x4 pose, out Bounds raw, out bool empty, out bool rigid)
        {
            if (ReferenceEquals(r.Go, null))
                r.Go = r.Area.gameObject;
            s_cols.Clear();
            r.Area.GetComponents(s_cols);
            if (!SameColliders(r.Cols, s_cols))
                r.Cols = s_cols.ToArray();
            s_cols.Clear();
            pose = r.Tr.localToWorldMatrix;
            raw = default;
            empty = true;
            rigid = false;
            Collider[] cols = r.Cols;
            for (int i = 0; i < cols.Length; i++)
            {
                Collider c = cols[i];
                if (c == null)
                    continue;
                if (c.attachedRigidbody != null)
                    rigid = true;
                Bounds b = c.bounds;
                if (IsEmpty(b))
                    continue;
                if (empty)
                {
                    raw = b;
                    empty = false;
                }
                else
                    raw.Encapsulate(b);
            }
        }

        // The pinned audit's sample: the known colliders' bounds and the matrix only (a pinned area was never on a
        // Rigidbody, and one that becomes attached to one moves, which the matrix shows).
        static void Peek(Rec r, out Matrix4x4 pose, out Bounds raw, out bool empty)
        {
            pose = r.Tr.localToWorldMatrix;
            raw = default;
            empty = true;
            Collider[] cols = r.Cols;
            for (int i = 0; i < cols.Length; i++)
            {
                Collider c = cols[i];
                if (c == null)
                    continue;
                Bounds b = c.bounds;
                if (IsEmpty(b))
                    continue;
                if (empty)
                {
                    raw = b;
                    empty = false;
                }
                else
                    raw.Encapsulate(b);
            }
        }

        // attachedRigidbody is null for a collider out of the physics scene; the hierarchy still says where it attaches.
        static bool RigidOutOfScene(Rec r) => r.Tr.GetComponentInParent<Rigidbody>(true) != null;

        static bool SameColliders(Collider[] have, List<Collider> now)
        {
            if (have.Length != now.Count)
                return false;
            for (int i = 0; i < have.Length; i++)
                if (!ReferenceEquals(have[i], now[i]))
                    return false;
            return true;
        }

        // Unity's bounds of a collider that is not in the physics scene.
        static bool IsEmpty(Bounds b) => b.extents.x == 0f && b.extents.y == 0f && b.extents.z == 0f && b.center.x == 0f && b.center.y == 0f && b.center.z == 0f;

        // ---- upkeep, once per frame from the first query ----------------------------------------------------

        static void Maintain()
        {
            int frame = Time.frameCount;
            if (frame == s_frame)
                return;
            s_frame = frame;
            float now = Time.unscaledTime;
            if (!s_seeded)
                Reseed(now);

            // Samples that are due, within a budget (nothing is due most frames: three Peeks).
            int budget = PendingPerFrame;
            Drain(s_firstQ, now, ref budget);
            Drain(s_stableQ, now, ref budget);
            Drain(s_looseQ, now, ref budget);

            // Re-check budget: a slow floor (every pinned and dormant area within AuditCycleSeconds of query time) plus
            // a few per "none" answer since the last upkeep, so frames whose queries all go to the game pay little.
            float dt = now - s_lastUpkeep;
            s_lastUpkeep = now;
            if (!(dt >= 0f))
                dt = 0f;
            else if (dt > 1f)
                dt = 1f;
            s_auditCredit = Math.Min(AuditPerFrame, s_auditCredit + s_pinned.Count * dt / AuditCycleSeconds + AuditPerAnswer * s_answered);
            s_dormantCredit = Math.Min(DormantAuditPerFrame, s_dormantCredit + s_dormant.Count * dt / AuditCycleSeconds);
            s_answered = 0;
            AuditPinned(now);
            AuditDormant();

            // The game's own lists: anything not registered through Awake (an Awake hidden by a subclass, areas
            // made before this plugin) is picked up.
            Sweep(EffectArea.Instances, ref s_sweepAt);
            SweepAll(EffectArea.GetAllAreas(), ref s_sweepAllAt);
        }

        static void AuditPinned(float now)
        {
            int n = Math.Min((int)s_auditCredit, s_pinned.Count);
            s_auditCredit -= n;
            for (; n > 0; n--)
            {
                if (s_auditAt >= s_pinned.Count)
                    s_auditAt = 0;
                Rec r = s_pinned[s_auditAt++];
                if (r.Area == null)
                {
                    Drop(r);
                    s_auditAt--;        // the list moved one down at this index
                    continue;
                }
                Audits++;
                Matrix4x4 pose;
                Bounds raw;
                bool empty, rigid = false;
                // Every 8th re-check also refreshes the collider list (colliders added to the object since).
                if ((++r.AuditCount & 7) == 0)
                    Sample(r, out pose, out raw, out empty, out rigid);
                else
                    Peek(r, out pose, out raw, out empty);
                // Same matrix and (when in the scene) the same bounds: still where it was pinned. Out of the scene at the
                // same matrix it keeps its box (back in, it is there again; a different shape is caught on that
                // re-check), unless its object went inactive: then it sleeps until OnEnable.
                if (!rigid && pose.Equals(r.Pose) && (empty || raw.Equals(r.PinRaw)))
                {
                    if (empty && CanDorm(r))
                    {
                        Unindex(r);
                        Sleep(r);
                        s_auditAt--;
                    }
                    continue;
                }
                Unpins++;
                Repend(r, pose, raw, empty, now);
                s_auditAt--;            // the list moved one down at this index
            }
        }

        // Safety net for what OnEnable does not see (the component disabled while its object is inactive, then the
        // object activated): such an area is made pending again.
        static void AuditDormant()
        {
            int n = Math.Min((int)s_dormantCredit, s_dormant.Count);
            s_dormantCredit -= n;
            for (; n > 0; n--)
            {
                if (s_dormantAt >= s_dormant.Count)
                    s_dormantAt = 0;
                Rec r = s_dormant[s_dormantAt++];
                if (r.Area == null)
                {
                    Drop(r);
                    s_dormantAt--;
                    continue;
                }
                if (!CanDorm(r))
                {
                    Wake(r);
                    s_dormantAt--;
                }
            }
        }

        static void Drain(Queue<Queued> queue, float now, ref int budget)
        {
            while (queue.Count > 0 && budget > 0)
            {
                Queued q = queue.Peek();
                if (q.Rec.Dead || q.Stamp != q.Rec.Stamp)
                {
                    queue.Dequeue();
                    continue;
                }
                if (q.Rec.Due > now)
                    break;
                queue.Dequeue();
                budget--;
                Resample(q.Rec, now);
            }
        }

        // Pinned once two samples StableSeconds apart agree (never on a Rigidbody, same matrix, same bounds).
        static void Resample(Rec r, float now)
        {
            if (r.Area == null)
            {
                Drop(r);
                return;
            }
            Sample(r, out Matrix4x4 pose, out Bounds raw, out bool empty, out bool rigid);
            bool stable = r.Sampled && pose.Equals(r.Pose) && (empty ? r.RawEmpty : !r.RawEmpty && raw.Equals(r.Raw));
            Note(r, pose, raw, empty);
            if (empty && CanDorm(r))
            {
                RemoveAt(s_unpinned, r);
                Sleep(r);
                return;
            }
            if (!rigid && empty && !r.EverRigid && RigidOutOfScene(r))
                rigid = true;
            if (rigid)
                r.EverRigid = true;
            if (r.EverRigid)
            {
                r.State = Loose;
                Queue(s_looseQ, r, now + LooseResampleSeconds);
                return;
            }
            if (stable && !empty)
                Pin(r, raw);
            else if (stable && r.HasGood && r.GoodPose.Equals(pose))
                Pin(r, r.GoodRaw);      // colliders off now (object active), but its shape at this pose is known
            else
            {
                r.State = Pending;
                Queue(s_stableQ, r, now + StableSeconds);
            }
        }

        static void Pin(Rec r, Bounds raw)
        {
            RemoveAt(s_unpinned, r);
            r.State = Pinned;
            r.PinRaw = raw;
            r.AuditCount = 0;
            Bounds box = raw;
            box.Expand(0.1f + 0.004f * raw.extents.magnitude);   // Expand takes the full size: +0.05 m and 0.2% each side
            r.Box = box;
            Add(s_pinned, r);
            int x0 = CellOf(box.min.x), x1 = CellOf(box.max.x), z0 = CellOf(box.min.z), z1 = CellOf(box.max.z);
            if ((long)(x1 - x0 + 1) * (z1 - z0 + 1) > MaxCellsPerArea)
            {
                r.Huge = true;
                s_huge.Add(r);
                return;
            }
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                {
                    long key = Key(x, z);
                    if (!s_cells.TryGetValue(key, out List<Rec> list))
                    {
                        list = s_freeLists.Count > 0 ? s_freeLists.Pop() : new List<Rec>();
                        s_cells.Add(key, list);
                    }
                    list.Add(r);
                    r.Keys.Add(key);
                }
        }

        static void Unindex(Rec r)
        {
            RemoveAt(s_pinned, r);
            if (r.Huge)
            {
                s_huge.Remove(r);
                r.Huge = false;
            }
            foreach (long key in r.Keys)
                if (s_cells.TryGetValue(key, out List<Rec> list))
                {
                    list.Remove(r);
                    if (list.Count == 0)
                    {
                        s_cells.Remove(key);
                        s_freeLists.Push(list);
                    }
                }
            r.Keys.Clear();
            r.State = Pending;
        }

        // Every area is sampled afresh (spread over the next frames) before any of it is trusted again.
        static void Reseed(float now)
        {
            s_seeded = true;
            s_firstQ.Clear();
            s_stableQ.Clear();
            s_looseQ.Clear();
            s_auditCredit = 0f;
            s_dormantCredit = 0f;
            s_answered = 0;
            s_lastUpkeep = now;
            foreach (EffectArea a in EffectArea.GetAllAreas())
                Register(a, false);
            foreach (IMonoUpdater u in EffectArea.Instances)
                if (u is EffectArea a)
                    Register(a, false);
            var all = new List<Rec>(s_recs.Values);
            foreach (Rec r in all)
            {
                if (r.Area == null)
                {
                    Drop(r);
                    continue;
                }
                if (r.State == Pinned)
                {
                    Unindex(r);
                    Add(s_unpinned, r);
                }
                else if (r.State == Dormant)
                {
                    RemoveAt(s_dormant, r);
                    Add(s_unpinned, r);
                }
                r.State = Pending;
                r.Sampled = false;
                Queue(s_firstQ, r, 0f);
            }
        }

        static void Sweep(List<IMonoUpdater> list, ref int at)
        {
            for (int n = Math.Min(SweepPerFrame, list.Count); n > 0; n--)
            {
                if (at >= list.Count)
                    at = 0;
                if (list[at++] is EffectArea a && !ReferenceEquals(a, null) && !s_recs.ContainsKey(a))
                    Register(a, true);
            }
        }

        static void SweepAll(List<EffectArea> list, ref int at)
        {
            for (int n = Math.Min(SweepPerFrame, list.Count); n > 0; n--)
            {
                if (at >= list.Count)
                    at = 0;
                EffectArea a = list[at++];
                if (!ReferenceEquals(a, null) && !s_recs.ContainsKey(a))
                    Register(a, true);
            }
        }

        // ---- the query ---------------------------------------------------------------------------------------

        // True when an indexed area of the type could be among the query's hits (or the index cannot tell).
        static bool MayHit(Vector3 p, float radius, EffectArea.Type type)
        {
            // Float margin for PhysX's own sphere tests at large coordinates.
            float reach = radius + 0.02f + 2e-6f * (Math.Abs(p.x) + Math.Abs(p.y) + Math.Abs(p.z));
            int x0 = CellOf(p.x - reach), x1 = CellOf(p.x + reach), z0 = CellOf(p.z - reach), z1 = CellOf(p.z + reach);
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                    if (s_cells.TryGetValue(Key(x, z), out List<Rec> list))
                        for (int i = 0; i < list.Count; i++)
                        {
                            Rec r = list[i];
                            if ((r.Area.m_type & type) != 0 && Hits(r.Box, p, reach))
                                return true;
                        }
            for (int i = 0; i < s_huge.Count; i++)
            {
                Rec r = s_huge[i];
                if ((r.Area.m_type & type) != 0 && Hits(r.Box, p, reach))
                    return true;
            }
            // Dormant areas are not looked at: none of their colliders is in the physics scene.
            if (s_unpinned.Count > UnpinnedScanLimit)
                return true;
            int live = 0;
            for (int i = 0; i < s_unpinned.Count; i++)
            {
                Rec r = s_unpinned[i];
                if ((r.Area.m_type & type) == 0)
                    continue;
                if (!r.Sampled)
                    return true;
                // Only areas in the physics scene now count toward the limit: one whose colliders are all out of it
                // (disabled) cannot be among the hits.
                bool counted = false;
                Collider[] cols = r.Cols;
                for (int c = 0; c < cols.Length; c++)
                {
                    Collider col = cols[c];
                    if (col == null)
                        continue;
                    Bounds b = col.bounds;
                    if (IsEmpty(b))
                        continue;
                    if (!counted)
                    {
                        counted = true;
                        if (++live > UnpinnedMatchLimit)
                            return true;
                    }
                    if (Hits(b, p, reach + LooseMargin))
                        return true;
                }
            }
            return false;
        }

        static bool Hits(Bounds box, Vector3 p, float reach)
        {
            Vector3 min = box.min, max = box.max;
            float dx = p.x < min.x ? min.x - p.x : p.x > max.x ? p.x - max.x : 0f;
            float dy = p.y < min.y ? min.y - p.y : p.y > max.y ? p.y - max.y : 0f;
            float dz = p.z < min.z ? min.z - p.z : p.z > max.z ? p.z - max.z : 0f;
            return dx * dx + dy * dy + dz * dz <= reach * reach;
        }

        // Whether this query may be answered from the index at all.
        static bool Usable(Vector3 p, float radius)
        {
            if (!s_installed || s_broken || !(radius >= 0f) || float.IsInfinity(radius) || float.IsNaN(p.x + p.y + p.z) || float.IsInfinity(p.x + p.y + p.z))
                return false;
            float now = Time.unscaledTime;
            if (now >= s_nextForeignCheck)
                CheckForeign(now);
            return !s_foreign;
        }

        static bool InsideBefore(Vector3 p, EffectArea.Type type, float radius, ref EffectArea __result, out bool __state)
        {
            __state = false;
            if (!Index.On || type == EffectArea.Type.Burning && radius.Equals(0.25f) || !Usable(p, radius))
                return true;
            Queries++;
            try
            {
                Maintain();
                if (MayHit(p, radius, type))
                {
                    Fallbacks++;
                    return true;
                }
            }
            catch (Exception e)
            {
                Break(e);
                return true;
            }
            s_answered++;
            if (Verify.On)
            {
                __state = true;     // the game answers; the postfix checks that it says none
                return true;
            }
            Proven++;
            __result = null;
            return false;
        }

        static void InsideAfter(EffectArea __result, bool __state, Vector3 p, EffectArea.Type type, float radius)
        {
            if (__state)
            {
                VerifyChecks++;
                if (!ReferenceEquals(__result, null))
                    Mismatch($"IsPointInsideArea({p}, {type}, {radius}) returned '{__result}' where the index found none");
            }
            // A game answer the index does not know (should not happen): learn it.
            if (!ReferenceEquals(__result, null) && Index.On && !s_broken && __result != null && !s_recs.ContainsKey(__result))
            {
                try
                {
                    Register(__result, true);
                }
                catch (Exception e)
                {
                    Break(e);
                }
            }
        }

        static bool BaseValueBefore(Vector3 p, float radius, ref int __result, out bool __state)
        {
            __state = false;
            if (!Index.On || !Usable(p, radius))
                return true;
            Queries++;
            try
            {
                Maintain();
                if (MayHit(p, radius, EffectArea.Type.PlayerBase))
                {
                    Fallbacks++;
                    return true;
                }
            }
            catch (Exception e)
            {
                Break(e);
                return true;
            }
            s_answered++;
            if (Verify.On)
            {
                __state = true;
                return true;
            }
            Proven++;
            __result = 0;
            return false;
        }

        static void BaseValueAfter(int __result, bool __state, Vector3 p, float radius)
        {
            if (!__state)
                return;
            VerifyChecks++;
            if (__result != 0)
                Mismatch($"GetBaseValue({p}, {radius}) returned {__result} where the index found none");
        }

        static void Mismatch(string what)
        {
            VerifyMismatches++;
            if (s_mismatchLogs++ < 20)
                EnginePlugin.Log?.LogWarning("AreaIndexVerify: " + what);
        }

        static void CheckForeign(float now)
        {
            s_nextForeignCheck = now + ForeignCheckSeconds;
            bool foreign, gated;
            try
            {
                foreign = Foreign(Harmony.GetPatchInfo(s_inside)) || Foreign(Harmony.GetPatchInfo(s_baseValue));
                gated = s_getExtensions != null && !Foreign(Harmony.GetPatchInfo(s_getExtensions));
            }
            catch (Exception)
            {
                foreign = true;
                gated = false;
            }
            if (foreign != s_foreign)
                EnginePlugin.Log?.LogWarning(foreign
                    ? "AreaIndex: another mod patches EffectArea.IsPointInsideArea or GetBaseValue; queries go to the game."
                    : "AreaIndex: no foreign patches any more; every area is re-checked before it is used.");
            // Nothing is queued or re-checked while stood down; coming back, everything is sampled again (s_seeded).
            if (foreign && !s_foreign)
                StandDown();
            s_foreign = foreign;
            if (gated != s_extGated)
                EnginePlugin.Log?.LogInfo(gated
                    ? "AreaIndex: CraftingStation.GetExtensions is the game's again; its reshape branch is watched."
                    : "AreaIndex: another mod patches CraftingStation.GetExtensions; every call of it is watched for a reshape.");
            s_extGated = gated;
        }

        static bool Foreign(HarmonyLib.Patches info)
        {
            if (info == null)
                return false;
            return Foreign(info.Prefixes) || Foreign(info.Postfixes) || Foreign(info.Transpilers) || Foreign(info.Finalizers) || Foreign(info.ILManipulators);
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

        // ---- StealthSystem.GetLightLevel timing -------------------------------------------------------------

        static void LightBefore(StealthSystem __instance, out long __state)
        {
            s_stampBefore = s_lightUpdate != null && __instance != null ? s_lightUpdate(__instance) : 0f;
            __state = Stopwatch.GetTimestamp();
        }

        static void LightAfter(StealthSystem __instance, long __state)
        {
            if (__instance == null)
                return;
            long ticks = Stopwatch.GetTimestamp() - __state;
            StealthCalls++;
            if (ticks > s_stealthMaxTicks)
                s_stealthMaxTicks = ticks;
            Light[] lights = s_lights?.Invoke(__instance);
            if (lights != null)
                StealthLights = lights.Length;
            // The game refreshed its light list in this call (m_lastLightListUpdate moved).
            if (s_lightUpdate != null && s_lightUpdate(__instance) != s_stampBefore)
            {
                StealthRefreshCalls++;
                s_stealthRefreshTicks += ticks;
            }
            else
                s_stealthPlainTicks += ticks;
        }

        static float s_stampBefore;

        // ---- small helpers -----------------------------------------------------------------------------------

        static int CellOf(float v) => (int)Math.Floor(v / CellSize);
        static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;

        static void Add(List<Rec> list, Rec r)
        {
            r.Slot = list.Count;
            list.Add(r);
        }

        static void RemoveAt(List<Rec> list, Rec r)
        {
            int i = r.Slot;
            if (i < 0 || i >= list.Count || !ReferenceEquals(list[i], r))
                return;
            int last = list.Count - 1;
            if (i != last)
            {
                list[i] = list[last];
                list[i].Slot = i;
            }
            list.RemoveAt(last);
            r.Slot = -1;
        }

        sealed class ByReference : IEqualityComparer<EffectArea>
        {
            internal static readonly ByReference Instance = new ByReference();
            public bool Equals(EffectArea a, EffectArea b) => ReferenceEquals(a, b);
            public int GetHashCode(EffectArea a) => RuntimeHelpers.GetHashCode(a);
        }
    }
}
