using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace RealPortal.Engine
{
    // Garbage that grows with the number of creatures, swings and effects (critic rank 1). With far areas the managed
    // heap is ~0.9 GB and every collection this garbage brings forward marks all of it (a full one measured 53-88 ms),
    // so a horde that makes MB/s of short-lived objects turns into GC spikes. One switch per mechanism:
    //  - AllocCollision: Physics.reuseCollisionCallbacks = true (Unity then hands every OnCollision* callback one reused
    //    Collision instead of 2 new Collisions + 2 ContactPoint[] per touching pair per physics step), and the game's
    //    three .contacts readers (Character.OnCollisionStay, ImpactEffect.OnCollisionEnter, FloatingTerrain.
    //    OnCollisionStay) read the same contacts into pooled arrays; with reuse on, .contacts would allocate again.
    //  - AllocNavCorners: Pathfinding.GetPath copies the path corners with GetCornersNonAlloc into pooled arrays instead
    //    of NavMeshPath.corners (one new Vector3[] per path).
    //  - AllocAoe: Projectile.DoAOE's Physics.OverlapSphere reuses arrays (re-entrancy safe, see the AoE section).
    //  - AllocBoneCache (lab, off until AllocVerify confirms it): Utils.FindChild at the per-event call sites (attack
    //    origin, effect child transforms, animation-event joints) answers from a cache instead of a depth-first walk
    //    that allocates a Stack and one name string per visited transform. Not strictly exact (see its section), and
    //    its answer feeds owner-side state (projectile and area-attack origins, effect parents), so it follows the
    //    AllocProjectileSphere rule: off by default until a lab run with equipment swaps (raid, horde) shows
    //    BoneVerifyMismatches == 0.
    //  - AllocTrail: MeleeWeaponTrail rebuilds its mesh every physics step from 4 List.ToArray copies; those become
    //    pooled arrays of the same length (the game overwrites every element before the mesh setters copy them), its
    //    list resizes stop allocating an Enumerable.Repeat iterator, and the Catmull-Rom ToList()s fill reused lists.
    // The cached HUD strings live in UiAllocModule (AllocHudText). The per-swing List/HashSet of Attack.DoMeleeAttack is
    // deliberately left alone: Path of Valheiman has two transpilers on that method (MeleeAim, MultiHitTargets) and
    // RealPortal a prefix, the swing's Damage calls can run RPC_Damage synchronously (a nested swing would need a
    // depth-safe pool), and it saves ~150 B per swing.
    //
    // Conventions (as AllocFixes): every game-code patch is a transpiler at Priority.Last that swaps single calls for
    // ours and leaves the rest of the method; ours computes what the game computed and calls the game's own API while
    // its switch is off. A method that also carries another mod's transpiler or IL manipulator is left as it is (the
    // check runs inside our transpiler, which Harmony re-runs on every patch of the method, so it holds whichever mod
    // patched first). "AllocVerify" (shared with AllocFixes; lab, off) runs the game's own call next to each
    // replacement, uses the game's answer and counts differences in VerifyMismatches.
    // A transpiler of ours never throws (Il.Guard) and is not registered when a game method it swaps is missing
    // (Il.Ready): HarmonyX adds a transpiler to the method's live patch list before it builds the new body and does
    // not take it out when that fails, so a throwing transpiler would run - and throw - again inside every later
    // Patch()/Unpatch() of that method by any mod (RealPortal's EffectList.Create postfix, Path of Valheiman's
    // EffectList/ImpactEffect/EnemyHud/InventoryGrid patches, SeneaL-UI's runtime re-patching of InventoryGrid.UpdateGui).
    //
    // Multiplayer: nothing here touches ZDOs, RPCs, ownership or the random generator. Every replacement returns the
    // same values in the same order, so owner-side simulation (ground contact, AoE targets, path corners) is what the
    // game computes. Two exceptions, both named: the Physics flag (global, see AllocCollision) and AllocBoneCache
    // (attack origin and effect parents; a stale cache answer is possible in the cases its section lists, which is why
    // it is a lab switch, off by default).
    [EngineModule]
    public static class CrowdAllocModule
    {
        static readonly Feature s_collision = Features.Register("AllocCollision", true,
            "Physics.reuseCollisionCallbacks on (one reused Collision per callback instead of 2 Collisions + 2 contact arrays per touching pair per step) and the game's .contacts readers read into pooled arrays.");
        static readonly Feature s_corners = Features.Register("AllocNavCorners", true,
            "Pathfinding.GetPath copies path corners with GetCornersNonAlloc into pooled arrays instead of a new array per path.");
        static readonly Feature s_aoe = Features.Register("AllocAoe", true,
            "Projectile.DoAOE's OverlapSphere reuses arrays (one pool per nesting depth).");
        static readonly Feature s_bones = Features.Register("AllocBoneCache", false,
            "Lab, off until AllocVerify confirms it: Utils.FindChild for attack origins, effect child transforms and animation-event joints answers from a cache checked against the hierarchy's transform count. Not exact if a hierarchy is rearranged without changing its transform count.");
        static readonly Feature s_trail = Features.Register("AllocTrail", true,
            "MeleeWeaponTrail rebuilds its mesh from pooled arrays and lists instead of 4 ToArray copies, 4 Repeat iterators and 2 ToList per step.");
        static readonly Feature s_verify = Features.Register("AllocVerify", false,
            "Lab: also runs the game's own call next to each Alloc* replacement, uses its answer and logs any difference. Slower.");

        // ---- Lab counters (the lab's 'counters' command reads public static numbers by reflection) -----------------
        // Contact reads served from a pooled array, and those left to the game's .contacts (more than 64 contacts).
        public static long ContactReads, ContactFallbacks;
        // Physics.reuseCollisionCallbacks as found at plugin start, and as set now.
        public static bool ReuseCollisionCallbacksAtStart, ReuseCollisionCallbacksNow;
        // Corner copies made (one per path), reads answered from that copy, and paths left to the game (buffer full).
        public static long CornerCopies, CornerRepeats, CornerFallbacks;
        // DoAOE queries served from a pool, nested ones among them, and those left to the game (buffer full, too deep).
        public static long AoeQueries, AoeNested, AoeFallbacks;
        // FindChild answers from the cache, walks made (first lookup or hierarchy changed), entries now.
        public static long BoneHits, BoneWalks;
        public static int BoneEntries;
        // Verify mode, FindChild only (also counted in VerifyChecks/VerifyMismatches, which mix all mechanisms): the
        // acceptance number for turning AllocBoneCache on by default is BoneVerifyMismatches == 0.
        public static long BoneVerifyChecks, BoneVerifyMismatches;
        // Trail arrays handed out from the pools, and those copied the game's way (longer than the pool keeps).
        public static long TrailArrays, TrailArrayFallbacks, TrailLists;
        // Verify mode: comparisons made, and differences the game would act on (acceptance: VerifyMismatches == 0).
        public static long VerifyChecks, VerifyMismatches;
        // Call sites swapped, and methods left as they are because another mod transpiles them (or the IL differs).
        public static int SitesPatched, SitesLeft;

        static MethodInfo Ours(string name) => AccessTools.DeclaredMethod(typeof(CrowdAllocModule), name);

        static void Install(Harmony harmony)
        {
            InstallCollision(harmony);
            InstallCorners(harmony);
            InstallAoe(harmony);
            if (Il.Ready("CrowdAlloc", "AllocBoneCache", ("Utils.FindChild(Transform, string, IterativeSearchType)", s_findChild)))
            {
                Il.Transpile(harmony, "CrowdAlloc", typeof(Attack), "GetAttackOrigin", Ours(nameof(BoneSites)));
                Il.Transpile(harmony, "CrowdAlloc", typeof(EffectList), "Create", Ours(nameof(BoneSites)));
                Il.Transpile(harmony, "CrowdAlloc", typeof(AnimationEffect), "Effect", Ours(nameof(BoneSites)));
                Il.Transpile(harmony, "CrowdAlloc", typeof(AnimationEffect), "Attach", Ours(nameof(BoneSites)));
            }
            Il.Transpile(harmony, "CrowdAlloc", typeof(MeleeWeaponTrail), "CustomFixedUpdate", Ours(nameof(TrailSites)));
            // Lookups cached while the switch was off were not kept current; start fresh.
            s_bones.Switched += on => { s_boneCache.Clear(); BoneEntries = 0; };
        }

        static void Mismatch(string site, string detail)
        {
            VerifyMismatches++;
            if (VerifyMismatches <= 50)
                EnginePlugin.Log.LogWarning($"AllocVerify: {site} differs from the game's own call: {detail}");
        }

        // =============================================================================================================
        // ---- AllocCollision -----------------------------------------------------------------------------------------
        //
        // Unity (UnityEngine.PhysicsModule, Physics.ReportContacts / GetCollisionToReport): for every reported pair and
        // every Enter/Stay/Exit, each side gets 'new Collision(header, pair, flipped)', whose constructor eagerly
        // extracts a new ContactPoint[pair.m_NbPoints] - also for the side that has no handler. Character has
        // OnCollisionStay, so every pair with a character body reports on every physics step it touches anything:
        // ~380-670 B per pair per step, ~5 MB/s for a clump of 100 - IF the game runs with reuseCollisionCallbacks off,
        // which is not confirmed yet (see the start value below); with it already on, that share is zero and the gain
        // is only the readers' pooled arrays. With reuseCollisionCallbacks on, both sides get the
        // one static Collision, re-pointed at the pair (Collision.Reuse) with no contact array; .contacts then extracts
        // lazily into a NEW array on each Collision it is read on, so the readers must not use it.
        //
        // Who could notice the flag: only code that keeps a Collision past its callback (it would see the next pair).
        // Audited: the game (Aoe, Character, Fish, FloatingTerrain, FloatingTerrainDummy, ImpactEffect, TestCollision -
        // all read collider/gameObject/contacts/relativeVelocity inside the callback; m_lowestContactCollider keeps the
        // Collider, not the Collision); Path of Valheiman (one handler, LootFilter.Landing: reads c.collider inside the
        // callback; its ImpactEffect.OnCollisionEnter prefixes/postfixes do not take the Collision); RealPortal
        // Plugin (no handler); the lab (HitLog: contactCount/GetContact(0)/collider inside the callback); Jotunn,
        // SeneaL-UI/API, KnifeThrowAnim, FarmGrid, IntroSkip, PoVLink (no OnCollision* and no contacts API in their
        // metadata). Callbacks run one after another on the main thread and none of these handlers steps physics, so a
        // handler never sees the shared Collision change under it. What the handlers read is the same: collider, body,
        // relativeVelocity and impulse come from the same ContactPairHeader/ContactPair, and the contacts come from the
        // same ContactPair.ExtractContactsArray (eager in the constructor, lazy in GetContacts) over the same native
        // buffer, which stays valid for the whole ReportContacts pass. No ZDO, RPC or random draw depends on it.
        //
        // Off restores the value found at plugin start (ReuseCollisionCallbacksAtStart). That value is UNKNOWN until
        // read in the game: the globalgamemanagers decode (Docs\general\physics.md, PhysicsManager offset 196 = 0) has no
        // type tree (critic.md asks for a runtime print), and the offline harness only reads its stubbed natives
        // (always false), so neither counts. The first game log line "AllocCollision: Physics.reuseCollisionCallbacks
        // was X at start", or the lab counter ReuseCollisionCallbacksAtStart, settles it and with it the collision
        // share of this module's MB/s estimate. Switching is safe at any time: the flag is read per pair, and the
        // readers below are exact with either value.

        static readonly MethodInfo s_getContacts = AccessTools.PropertyGetter(typeof(Collision), nameof(Collision.contacts));

        static void InstallCollision(Harmony harmony)
        {
            try
            {
                ReuseCollisionCallbacksAtStart = Physics.reuseCollisionCallbacks;
                EnginePlugin.Log.LogInfo($"AllocCollision: Physics.reuseCollisionCallbacks was {ReuseCollisionCallbacksAtStart} at start.");
                ApplyCollisionFlag(s_collision.On);
                s_collision.Switched += ApplyCollisionFlag;
            }
            catch (Exception e)
            {
                EnginePlugin.Log.LogError($"AllocCollision: cannot read or set Physics.reuseCollisionCallbacks, left as it is: {e}");
            }
            // The readers are swapped even if the flag could not be set: they are exact either way. (Without them the
            // flag still pays: each read then allocates one lazily extracted array instead of 2 Collisions + 2 arrays.)
            if (!Il.Ready("CrowdAlloc", "AllocCollision's contact readers", ("Collision.contacts getter", s_getContacts)))
                return;
            Il.Transpile(harmony, "CrowdAlloc", typeof(Character), "OnCollisionStay", Ours(nameof(ContactReaders)));
            Il.Transpile(harmony, "CrowdAlloc", typeof(ImpactEffect), "OnCollisionEnter", Ours(nameof(ContactReaders)));
            Il.Transpile(harmony, "CrowdAlloc", typeof(FloatingTerrain), "OnCollisionStay", Ours(nameof(ContactReaders)));
        }

        static void ApplyCollisionFlag(bool on)
        {
            Physics.reuseCollisionCallbacks = on || ReuseCollisionCallbacksAtStart;
            ReuseCollisionCallbacksNow = Physics.reuseCollisionCallbacks;
        }

        // Character.OnCollisionStay reads .contacts once (owner only, after its early returns), ImpactEffect twice
        // (Length == 0, then [0]), FloatingTerrain once ([0].normal).
        static IEnumerable<CodeInstruction> ContactReaders(IEnumerable<CodeInstruction> code, MethodBase original)
        {
            int expected = original?.DeclaringType == typeof(ImpactEffect) ? 2 : 1;
            return Il.SwapAll(code, original, "CrowdAlloc", "Collision.contacts -> pooled GetContacts",
                              ci => ci.Calls(s_getContacts), _ => Ours(nameof(Contacts)), expected);
        }

        // Exact-length arrays, one per contact count (PhysX reports a handful per pair). Every reader uses the array only
        // inside its own callback, before anything else can run a collision callback, and walks it by Length; each
        // call refills it, so even the two reads in ImpactEffect each get freshly extracted contacts.
        static readonly ContactPoint[][] s_contactArrays = new ContactPoint[65][];

        static ContactPoint[] Contacts(Collision collision)
        {
            if (!s_collision.On)
                return collision.contacts;
            int n = collision.contactCount;   // a null collision throws here as .contacts would
            if ((uint)n >= (uint)s_contactArrays.Length)
            {
                ContactFallbacks++;
                return collision.contacts;
            }
            ContactPoint[] contacts = s_contactArrays[n] ?? (s_contactArrays[n] = new ContactPoint[n]);
            // Same extraction as the eager constructor or the lazy getter: ContactPair.ExtractContactsArray(array,
            // flipped) when the Collision has no array yet, a copy of that array when it has one (reuse off, or an
            // earlier handler on the same object read .contacts). The array holds exactly n, so all n are written.
            if (n > 0)
                collision.GetContacts(contacts);
            ContactReads++;
            if (s_verify.On)
                VerifyContacts(collision, contacts);
            return contacts;
        }

        static void VerifyContacts(Collision collision, ContactPoint[] ours)
        {
            VerifyChecks++;
            ContactPoint[] game = collision.contacts;
            bool same = game.Length == ours.Length;
            for (int i = 0; same && i < ours.Length; i++)
            {
                ContactPoint a = ours[i], b = game[i];
                same = a.point.Equals(b.point) && a.normal.Equals(b.normal) && a.impulse.Equals(b.impulse) && a.separation.Equals(b.separation)
                       && (object)a.thisCollider == b.thisCollider && (object)a.otherCollider == b.otherCollider;
            }
            if (!same)
            {
                Mismatch("Collision contacts", $"{ours.Length} vs {game.Length} contacts or values differ");
                Array.Copy(game, ours, Math.Min(game.Length, ours.Length));   // the reader then uses the game's values
            }
        }

        // =============================================================================================================
        // ---- AllocNavCorners ----------------------------------------------------------------------------------------
        //
        // Pathfinding.GetPath: NavMesh.CalculatePath(from, to, filter, m_path), then up to 4 reads of m_path.corners
        // (corners[0], corners[Length - 1] and corners.Length for a partial path; AddRange(corners) for the result).
        // In this Unity (6000.0) NavMeshPath.corners builds a new Vector3[] once per path and keeps it in m_Corners until
        // the next CalculatePath (which calls ClearCorners), so the game allocates one array per path request.
        // Ours copies the corners with GetCornersNonAlloc into a reused buffer, then into a pooled array of exactly that
        // length (the game indexes Length - 1 and AddRange copies the whole array), and answers every further read
        // until the next CalculatePath with that same array - the game's own "once per CalculatePath" rule, kept by
        // swapping GetPath's CalculatePath call for one that forgets the copy first. GetPath only reads the array
        // (IsUnderTerrain takes elements by value, List.AddRange copies), and nothing in between runs another GetPath
        // (and if it did, its CalculatePath would make the next read copy again, as ClearCorners does in the game).
        // Exactness rests on GetCornersNonAlloc and the corners getter returning the same native corners (both are
        // NavMeshPathScriptBindings over the same path); AllocVerify compares every copy with the game's getter. A full
        // buffer may have cut corners off, so that path is answered by the game's getter and the buffer grows.
        // NavMesh types live in UnityEngine.AIModule, which the engine does not reference, so the helpers are generic
        // over the path and filter types and are bound by reflection at install.

        static Type s_navPathType, s_navFilterType;

        static void InstallCorners(Harmony harmony)
        {
            try
            {
                // Through the game's own field, so UnityEngine.AIModule is resolved the way the game resolves it.
                s_navPathType = AccessTools.Field(typeof(Pathfinding), "m_path")?.FieldType;
                s_navFilterType = s_navPathType?.Assembly.GetType("UnityEngine.AI.NavMeshQueryFilter");
                bool ready = s_navPathType != null && s_navFilterType != null
                             && (bool)Ours(nameof(PrepareNav)).MakeGenericMethod(s_navFilterType, s_navPathType).Invoke(null, null);
                if (!ready)
                {
                    EnginePlugin.Log.LogWarning("AllocNavCorners: NavMeshPath.GetCornersNonAlloc / corners / NavMesh.CalculatePath not found; paths stay as in the game.");
                    return;
                }
            }
            catch (Exception e)
            {
                EnginePlugin.Log.LogError($"AllocNavCorners: not installed: {e}");
                return;
            }
            Il.Transpile(harmony, "CrowdAlloc", typeof(Pathfinding), "GetPath", Ours(nameof(PathSites)));
        }

        static bool PrepareNav<TFilter, TPath>() where TFilter : struct where TPath : class =>
            NavApi<TPath>.Corners != null && NavApi<TPath>.NonAlloc != null && NavCalc<TFilter, TPath>.Call != null;

        static T Bind<T>(MethodInfo method) where T : Delegate
        {
            try
            {
                return method == null ? null : (T)Delegate.CreateDelegate(typeof(T), method, false);
            }
            catch (Exception)
            {
                return null;
            }
        }

        static class NavApi<TPath> where TPath : class
        {
            internal static readonly Func<TPath, Vector3[]> Corners =
                Bind<Func<TPath, Vector3[]>>(AccessTools.PropertyGetter(typeof(TPath), "corners"));
            internal static readonly Func<TPath, Vector3[], int> NonAlloc =
                Bind<Func<TPath, Vector3[], int>>(AccessTools.Method(typeof(TPath), "GetCornersNonAlloc", new[] { typeof(Vector3[]) }));
        }

        static class NavCalc<TFilter, TPath> where TFilter : struct where TPath : class
        {
            internal static readonly Func<Vector3, Vector3, TFilter, TPath, bool> Call =
                Bind<Func<Vector3, Vector3, TFilter, TPath, bool>>(AccessTools.Method(typeof(TPath).Assembly.GetType("UnityEngine.AI.NavMesh"), "CalculatePath",
                                                                                      new[] { typeof(Vector3), typeof(Vector3), typeof(TFilter), typeof(TPath) }));
        }

        // Both edits or none: 4 corners reads and the one CalculatePath(from, to, filter, path) they follow. With another
        // count (a game update or another mod) the "same path until the next CalculatePath" rule is not ours to assume.
        static IEnumerable<CodeInstruction> PathSites(IEnumerable<CodeInstruction> code, MethodBase original) =>
            Il.Guard(code, original, "CrowdAlloc", list => PathSitesIn(list, original));

        static List<CodeInstruction> PathSitesIn(List<CodeInstruction> list, MethodBase original)
        {
            string foreign = Il.ForeignTranspiler(original);
            if (foreign != null)
            {
                Il.Report("CrowdAlloc", original, $"left as it is: transpiled by {foreign}", -1);
                return list;
            }
            int corners = 0, calcs = 0;
            foreach (CodeInstruction ci in list)
            {
                if (IsCornersGetter(ci))
                    corners++;
                else if (IsCalculatePath(ci))
                    calcs++;
            }
            if (corners != 4 || calcs != 1)
            {
                Il.Report("CrowdAlloc", original, $"NavMeshPath.corners x{corners}, CalculatePath x{calcs} where the game has 4 and 1", 0);
                return list;
            }
            MethodInfo cornersOurs = Ours(nameof(PathCorners)).MakeGenericMethod(s_navPathType);
            MethodInfo calcOurs = Ours(nameof(CalculatePathFresh)).MakeGenericMethod(s_navFilterType, s_navPathType);
            foreach (CodeInstruction ci in list)
            {
                if (IsCornersGetter(ci))
                    Il.Set(ci, OpCodes.Call, cornersOurs);
                else if (IsCalculatePath(ci))
                    Il.Set(ci, OpCodes.Call, calcOurs);
            }
            Il.Report("CrowdAlloc", original, "NavMeshPath.corners -> GetCornersNonAlloc copy (and its CalculatePath)", 5, 5);
            return list;
        }

        static bool IsCornersGetter(CodeInstruction ci) =>
            (ci.opcode == OpCodes.Callvirt || ci.opcode == OpCodes.Call) && ci.operand is MethodInfo m && m.Name == "get_corners" && m.DeclaringType == s_navPathType;

        static bool IsCalculatePath(CodeInstruction ci)
        {
            if ((ci.opcode != OpCodes.Call && ci.opcode != OpCodes.Callvirt) || !(ci.operand is MethodInfo m) || m.Name != "CalculatePath"
                || m.DeclaringType?.FullName != "UnityEngine.AI.NavMesh")
                return false;
            ParameterInfo[] p = m.GetParameters();
            return p.Length == 4 && p[0].ParameterType == typeof(Vector3) && p[1].ParameterType == typeof(Vector3)
                   && p[2].ParameterType == s_navFilterType && p[3].ParameterType == s_navPathType && m.ReturnType == typeof(bool);
        }

        // The corners of the path last calculated through GetPath, while they are still that path's (main thread only).
        static object s_cornersOf;
        static Vector3[] s_cornersCopy;
        static Vector3[] s_cornerBuffer = new Vector3[256];
        static readonly Vector3[][] s_cornerArrays = new Vector3[128][];
        const int MaxCornerBuffer = 4096;

        static bool CalculatePathFresh<TFilter, TPath>(Vector3 from, Vector3 to, TFilter filter, TPath path) where TFilter : struct where TPath : class
        {
            // NavMesh.CalculatePath starts with path.ClearCorners(): whatever was read before belongs to the old path.
            s_cornersOf = null;
            s_cornersCopy = null;
            return NavCalc<TFilter, TPath>.Call(from, to, filter, path);
        }

        static Vector3[] PathCorners<TPath>(TPath path) where TPath : class
        {
            if (!s_corners.On)
                return NavApi<TPath>.Corners(path);
            if (s_cornersCopy != null && ReferenceEquals(s_cornersOf, path))
            {
                CornerRepeats++;
                return s_cornersCopy;
            }
            int n = NavApi<TPath>.NonAlloc(path, s_cornerBuffer);   // a null path throws here as the getter would
            Vector3[] result;
            if (n >= s_cornerBuffer.Length)
            {
                // Possibly cut off: the game's getter answers (and keeps its array in m_Corners as in the game).
                CornerFallbacks++;
                result = NavApi<TPath>.Corners(path);
                int size = s_cornerBuffer.Length;
                while (size <= result.Length && size < MaxCornerBuffer)
                    size *= 2;
                if (size > s_cornerBuffer.Length)
                    s_cornerBuffer = new Vector3[size];
            }
            else
            {
                result = n < s_cornerArrays.Length ? s_cornerArrays[n] ?? (s_cornerArrays[n] = new Vector3[n]) : new Vector3[n];
                Array.Copy(s_cornerBuffer, result, n);
                CornerCopies++;
                if (s_verify.On)
                    result = VerifyCorners(result, NavApi<TPath>.Corners(path));
            }
            s_cornersOf = path;
            s_cornersCopy = result;
            return result;
        }

        static Vector3[] VerifyCorners(Vector3[] ours, Vector3[] game)
        {
            VerifyChecks++;
            bool same = ours.Length == game.Length;
            for (int i = 0; same && i < ours.Length; i++)
                same = ours[i].Equals(game[i]);
            if (same)
                return ours;
            Mismatch("Pathfinding.GetPath corners", $"{ours.Length} vs {game.Length} corners or values differ");
            return game;
        }

        // =============================================================================================================
        // ---- AllocAoe -----------------------------------------------------------------------------------------------
        //
        // Projectile.DoAOE: Physics.OverlapSphere(hitPoint, m_aoe, s_rayMaskSolids, UseGlobal), then a walk over the array
        // in order (the first collider of each GameObject wins s_hitSet, and its ClosestPointOnBounds is the hit point),
        // damaging each target. Damage on a locally owned target runs RPC_Damage synchronously, so a death, a mod's
        // on-hit explosion or similar could start another DoAOE while this one still walks its array. The pool is
        // therefore per nesting depth, counted by a prefix and a finalizer on DoAOE (a pool is reused only after the
        // DoAOE at that depth has returned); deeper than the pools reach, the game's own call answers.
        // The array is the same query into a reused buffer copied into an exact-length array (the game walks
        // array.Length): OverlapSphere and OverlapSphereNonAlloc take the same arguments to the same PhysX overlap
        // (AllocFixes, AllocOverlap); same colliders in the same order is what AllocVerify checks. On a full buffer the
        // game's call answers and the buffer grows.

        static readonly MethodInfo s_overlap4 = AccessTools.Method(typeof(Physics), nameof(Physics.OverlapSphere),
                                                                   new[] { typeof(Vector3), typeof(float), typeof(int), typeof(QueryTriggerInteraction) });

        static void InstallAoe(Harmony harmony)
        {
            if (!Il.Ready("CrowdAlloc", "AllocAoe", ("Physics.OverlapSphere(Vector3, float, int, QueryTriggerInteraction)", s_overlap4)))
                return;
            MethodInfo doAoe = AccessTools.DeclaredMethod(typeof(Projectile), "DoAOE");
            if (doAoe == null)
            {
                EnginePlugin.Log.LogWarning("CrowdAlloc: Projectile.DoAOE not found, left as it is.");
                return;
            }
            MethodInfo prefix = AccessTools.DeclaredMethod(typeof(AoeDepth), nameof(AoeDepth.Prefix));
            MethodInfo finalizer = AccessTools.DeclaredMethod(typeof(AoeDepth), nameof(AoeDepth.Finalizer));
            try
            {
                // The depth count first: without it the replacement below always answers with the game's call.
                harmony.Patch(doAoe, prefix: new HarmonyMethod(prefix), finalizer: new HarmonyMethod(finalizer));
            }
            catch (Exception e)
            {
                EnginePlugin.Log.LogError($"CrowdAlloc: Projectile.DoAOE depth count not patched, AoE stays as in the game: {e}");
                Il.Rollback(harmony, doAoe, "CrowdAlloc", prefix, finalizer);
                return;
            }
            Il.Transpile(harmony, "CrowdAlloc", typeof(Projectile), "DoAOE", Ours(nameof(AoeSites)));
        }

        // __state pairs each finalizer with its own prefix: if another prefix throws before ours, nothing is counted
        // (and nothing is released: Depth then still names an outer DoAOE that may be walking its array).
        static class AoeDepth
        {
            internal static int Depth;

            internal static void Prefix(out bool __state)
            {
                Depth++;
                __state = true;
            }

            internal static void Finalizer(bool __state)
            {
                if (!__state)
                    return;
                // The DoAOE at this depth has returned, so nothing reads the array its pool handed out any more: let go
                // of its colliders now rather than at the next AoE this deep (depths 2-4 are rarely reached again).
                int depth = Depth;
                if (depth >= 1 && depth <= s_aoePools.Length)
                    s_aoePools[depth - 1].Release();
                Depth--;
            }
        }

        static IEnumerable<CodeInstruction> AoeSites(IEnumerable<CodeInstruction> code, MethodBase original) =>
            Il.SwapAll(code, original, "CrowdAlloc", "OverlapSphere -> pooled NonAlloc", ci => ci.Calls(s_overlap4), _ => Ours(nameof(AoeOverlap)), 1);

        static readonly ColliderPool[] s_aoePools = { new ColliderPool(), new ColliderPool(), new ColliderPool(), new ColliderPool() };

        static Collider[] AoeOverlap(Vector3 position, float radius, int layerMask, QueryTriggerInteraction triggers)
        {
            int depth = AoeDepth.Depth;
            if (!s_aoe.On || depth < 1 || depth > s_aoePools.Length)
            {
                if (s_aoe.On)
                    AoeFallbacks++;
                return Physics.OverlapSphere(position, radius, layerMask, triggers);
            }
            if (depth > 1)
                AoeNested++;
            return s_aoePools[depth - 1].OverlapSphere(position, radius, layerMask, triggers);
        }

        sealed class ColliderPool
        {
            const int Kept = 128;
            const int MaxBuffer = 2048;

            Collider[] _buffer = new Collider[Kept];
            readonly Collider[][] _exact = new Collider[Kept][];
            Collider[] _handedOut;

            // Between DoAOE calls a pool keeps no collider alive: the scratch buffer is cleared right after each copy, and
            // the exact-length array handed to a DoAOE is cleared by Release when that DoAOE returns (AoeDepth.Finalizer).
            // Never earlier: the DoAOE walks it after this returns, and nested DoAOEs use the pools of deeper levels.
            internal void Release()
            {
                if (_handedOut != null)
                {
                    Array.Clear(_handedOut, 0, _handedOut.Length);
                    _handedOut = null;
                }
            }

            internal Collider[] OverlapSphere(Vector3 position, float radius, int layerMask, QueryTriggerInteraction triggers)
            {
                // Normally already released by the finalizer of the DoAOE that got it; kept as a second line.
                Release();
                int n = Physics.OverlapSphereNonAlloc(position, radius, _buffer, layerMask, triggers);
                if (n >= _buffer.Length)
                {
                    AoeFallbacks++;
                    Collider[] game = Physics.OverlapSphere(position, radius, layerMask, triggers);
                    int size = _buffer.Length;
                    while (size <= game.Length && size < MaxBuffer)
                        size *= 2;
                    if (size > _buffer.Length)
                        _buffer = new Collider[Math.Min(size, MaxBuffer)];
                    else
                        Array.Clear(_buffer, 0, _buffer.Length);
                    return game;
                }
                AoeQueries++;
                Collider[] result;
                if (n == 0)
                    result = Array.Empty<Collider>();   // the game only reads Length of an empty result
                else if (n < Kept)
                    result = _handedOut = _exact[n] ?? (_exact[n] = new Collider[n]);
                else
                    result = new Collider[n];
                Array.Copy(_buffer, result, n);
                Array.Clear(_buffer, 0, n);
                if (s_verify.On)
                    return Verify(result, Physics.OverlapSphere(position, radius, layerMask, triggers));
                return result;
            }

            static Collider[] Verify(Collider[] ours, Collider[] game)
            {
                VerifyChecks++;
                bool same = ours.Length == game.Length;
                for (int i = 0; same && i < ours.Length; i++)
                    same = (object)ours[i] == game[i];
                if (same)
                    return ours;
                Mismatch("Projectile.DoAOE OverlapSphere", $"{ours.Length} vs {game.Length} colliders, or another order");
                return game;
            }
        }

        // =============================================================================================================
        // ---- AllocBoneCache -----------------------------------------------------------------------------------------
        //
        // Utils.FindChild(root, name) (DepthFirst, the only kind these sites use) walks the whole hierarchy under root
        // in pre-order, reading Transform.name - a new string per node - and allocating a Stack, and returns the first
        // transform (root excluded) whose name equals 'name', or null. Hot callers: Attack.GetAttackOrigin (2-4 times
        // per attack: start, trigger, melee, projectile), EffectList.Create for entries with m_childTransform,
        // AnimationEffect.Effect/Attach on every animation event naming a joint, on every client. Rigs have 80-200
        // transforms: 10-30 us and 3-10 KB per call.
        //
        // Ours keeps (root, name) -> (answer, root.hierarchyCount at that walk) and answers from it while
        //  - root.hierarchyCount is unchanged (the number of transforms in root's whole root hierarchy: any transform
        //    created under it, attached to it, detached, or destroyed - at the end of the frame - changes it), and
        //  - a found transform is still alive and still under root (IsChildOf), or the answer was null.
        // Otherwise it walks the game's way and stores the new answer. Bit-exact unless, between two lookups, the
        // hierarchy changes in a way that keeps its transform count AND makes another transform the first one named
        // 'name': a rename (Transform.name set at runtime), a sibling reorder (SetSiblingIndex & co.), or a reparent
        // inside the same root hierarchy, or an attach and a detach of equally many transforms where the attached part
        // holds a transform with that name earlier in pre-order while the cached one stays. The game does none of this
        // to character, creature or effect hierarchies (its runtime renames: CookingStation items, the placement ghost,
        // dungeon rooms, UI; its sibling reorders: UI and InstantiatePrefab, which also adds transforms); equipment and
        // animation attachments add or remove transforms (count changes; Destroy also kills the cached answer). A mod
        // could; AllocVerify walks next to every cached answer and counts any difference (BoneVerifyMismatches). A
        // destroyed or null root, BreadthFirst and a null name go to the game's code unchanged (same exceptions, same
        // results).
        // hierarchyCount covers root's whole top-level hierarchy, so the equal-count cases above are possible in
        // play: a reparent from elsewhere in the same character (outside root's subtree) into it, or an equipment swap
        // where the old item's transforms are destroyed and an item with as many transforms is attached between two
        // lookups - a cached null, or a cached hit that stays alive while the new item holds an earlier match, is then
        // returned without a walk. The owner would spawn the projectile, AoE or effect from another joint than the
        // game. Hence a lab switch, off by default, until a raid/horde run with equipment swaps under AllocVerify
        // shows BoneVerifyMismatches == 0.

        static readonly MethodInfo s_findChild = AccessTools.Method(typeof(Utils), nameof(Utils.FindChild),
                                                                    new[] { typeof(Transform), typeof(string), typeof(Utils.IterativeSearchType) });

        static IEnumerable<CodeInstruction> BoneSites(IEnumerable<CodeInstruction> code, MethodBase original) =>
            Il.SwapAll(code, original, "CrowdAlloc", "Utils.FindChild -> cached", ci => ci.Calls(s_findChild), _ => Ours(nameof(FindChildCached)), 1);

        readonly struct BoneKey : IEquatable<BoneKey>
        {
            readonly Transform _root;
            readonly string _name;

            internal BoneKey(Transform root, string name)
            {
                _root = root;
                _name = name;
            }

            public bool Equals(BoneKey other) => (object)_root == other._root && string.Equals(_name, other._name);
            public override bool Equals(object obj) => obj is BoneKey other && Equals(other);
            public override int GetHashCode() => RuntimeHelpers.GetHashCode(_root) * 397 ^ _name.GetHashCode();
        }

        readonly struct BoneAnswer
        {
            internal readonly Transform Found;
            internal readonly int HierarchyCount;

            internal BoneAnswer(Transform found, int hierarchyCount)
            {
                Found = found;
                HierarchyCount = hierarchyCount;
            }
        }

        // Roots come and go with creatures; the whole cache is dropped when it gets this big (each entry is re-learned by
        // one walk), which also drops entries of destroyed roots.
        const int MaxBoneEntries = 8192;
        static readonly Dictionary<BoneKey, BoneAnswer> s_boneCache = new Dictionary<BoneKey, BoneAnswer>(512);

        static Transform FindChildCached(Transform root, string name, Utils.IterativeSearchType searchType)
        {
            if (!s_bones.On || searchType != Utils.IterativeSearchType.DepthFirst || name == null || (object)root == null || !root)
                return Utils.FindChild(root, name, searchType);
            int count = root.hierarchyCount;
            var key = new BoneKey(root, name);
            if (s_boneCache.TryGetValue(key, out BoneAnswer answer) && answer.HierarchyCount == count)
            {
                Transform found = answer.Found;
                if ((object)found == null || (found && found.IsChildOf(root)))
                {
                    BoneHits++;
                    if (s_verify.On)
                        return VerifyBone(key, root, name, found, count);
                    return found;
                }
            }
            BoneWalks++;
            Transform walked = Utils.FindChild(root, name, searchType);
            if (s_boneCache.Count >= MaxBoneEntries && !s_boneCache.ContainsKey(key))
                s_boneCache.Clear();
            s_boneCache[key] = new BoneAnswer(walked, count);
            BoneEntries = s_boneCache.Count;
            return walked;
        }

        static Transform VerifyBone(BoneKey key, Transform root, string name, Transform cached, int count)
        {
            VerifyChecks++;
            BoneVerifyChecks++;
            Transform game = Utils.FindChild(root, name, Utils.IterativeSearchType.DepthFirst);
            if ((object)game == cached)
                return cached;
            BoneVerifyMismatches++;
            Mismatch("Utils.FindChild", $"'{name}' under {root.name}: cached {(cached ? cached.name : "null")}, walk {(game ? game.name : "null")}");
            s_boneCache[key] = new BoneAnswer(game, count);
            return game;
        }

        // =============================================================================================================
        // ---- AllocTrail ---------------------------------------------------------------------------------------------
        //
        // MeleeWeaponTrail.CustomFixedUpdate, every physics step while the trail has 2+ smoothed points (P of them):
        //   m_newVertices/m_newUV/m_newColors/m_newTriangles.Resize(2P, 2P, 2P, 6(P-1));   (Enumerable.Repeat on growth)
        //   array = m_newVertices.ToArray(); array2 = m_newUV.ToArray(); array3 = m_newColors.ToArray();
        //   array4 = m_newTriangles.ToArray();                                            (4 new arrays, 3-6 KB)
        //   for k in 0..P-1: array[2k], array[2k+1], array3[2k], array3[2k+1], array2[2k].x/.y, array2[2k+1].x/.y and,
        //                    for k > 0, array4[6(k-1) .. 6(k-1)+5] are written;
        //   m_trailMesh.Clear(); vertices = array; colors = array3; uv = array2; triangles = array4;
        // So every element of the four arrays is written before the setters read them (vertices/colors/uv: 2P entries,
        // both halves of each Vector2; triangles: 6(P-1) entries), and the setters copy them into the mesh (no managed
        // array is kept). What the lists held is never seen: they are only sized. Ours hands out a pooled array of the
        // same length (List.Count, as ToArray) shared by all trails - CustomFixedUpdate runs one trail at a time and
        // nothing in it calls another trail - and leaves the four setters, so the mesh gets the same calls with
        // arrays of the same length and contents.
        // Resize: shrinking is the same RemoveRange; growing sets Capacity the same way and appends default(T) (what
        // new T() is for these structs) with Add instead of AddRange(Enumerable.Repeat(...)): same Count, Capacity and
        // contents, no iterator.
        // The Catmull-Rom smoothing, on each new sample: m_smoothTipList = tips.ToList(); m_smoothBaseList =
        // bases.ToList(); then a loop reads both lists. Ours fills one reused list per call site with the same
        // enumeration (same elements, same order) and stores that list in the field. Those fields are read only in that
        // loop, right after the assignment (the whole assembly; no mod names them), so trails sharing the lists see
        // the same values. The NewCatmullRom iterators themselves still allocate (assembly_utils code, left alone).

        static IEnumerable<CodeInstruction> TrailSites(IEnumerable<CodeInstruction> code, MethodBase original) =>
            Il.Guard(code, original, "CrowdAlloc", list => TrailSitesIn(list, original));

        static List<CodeInstruction> TrailSitesIn(List<CodeInstruction> list, MethodBase original)
        {
            string foreign = Il.ForeignTranspiler(original);
            if (foreign != null)
            {
                Il.Report("CrowdAlloc", original, $"left as it is: transpiled by {foreign}", -1);
                return list;
            }
            // Each group is swapped only with the game's exact count of calls.
            Il.SwapGroup(list, original, "CrowdAlloc", "List.ToArray -> pooled exact-length arrays", IsListToArray,
                         ci => Ours(nameof(TrailArray)).MakeGenericMethod(((MethodInfo)ci.operand).DeclaringType.GetGenericArguments()[0]), 4);
            Il.SwapGroup(list, original, "CrowdAlloc", "ListExtra.Resize -> no iterator", IsListResize,
                         ci => Ours(nameof(TrailResize)).MakeGenericMethod(((MethodInfo)ci.operand).GetGenericArguments()[0]), 4);
            int toList = 0;
            Il.SwapGroup(list, original, "CrowdAlloc", "Catmull-Rom ToList -> reused lists", IsVector3ToList,
                         ci => Ours(toList++ == 0 ? nameof(SmoothTips) : nameof(SmoothBases)), 2);
            return list;
        }

        static bool IsListToArray(CodeInstruction ci) =>
            (ci.opcode == OpCodes.Callvirt || ci.opcode == OpCodes.Call) && ci.operand is MethodInfo m && m.Name == "ToArray"
            && m.DeclaringType != null && m.DeclaringType.IsGenericType && m.DeclaringType.GetGenericTypeDefinition() == typeof(List<>)
            && m.DeclaringType.GetGenericArguments()[0].IsValueType;

        static bool IsListResize(CodeInstruction ci) =>
            ci.opcode == OpCodes.Call && ci.operand is MethodInfo m && m.Name == "Resize" && m.DeclaringType == typeof(ListExtra)
            && m.IsGenericMethod && m.GetParameters().Length == 2 && m.GetGenericArguments()[0].IsValueType;

        static bool IsVector3ToList(CodeInstruction ci) =>
            ci.opcode == OpCodes.Call && ci.operand is MethodInfo m && m.Name == "ToList" && m.DeclaringType == typeof(Enumerable)
            && m.IsGenericMethod && m.GetGenericArguments()[0] == typeof(Vector3);

        static T[] TrailArray<T>(List<T> list) where T : struct
        {
            if (!s_trail.On)
                return list.ToArray();
            T[] array = TrailPool<T>.Get(list.Count);   // a null list throws here as ToArray would
            if (array == null)
            {
                TrailArrayFallbacks++;
                return list.ToArray();
            }
            TrailArrays++;
            return array;
        }

        static void TrailResize<T>(List<T> list, int size) where T : struct
        {
            if (!s_trail.On)
            {
                list.Resize(size);
                return;
            }
            int count = list.Count;
            if (size < count)
            {
                list.RemoveRange(size, count - size);
            }
            else if (size > count)
            {
                if (size > list.Capacity)
                    list.Capacity = size;
                for (int i = count; i < size; i++)
                    list.Add(default);
            }
        }

        static readonly List<Vector3> s_smoothTips = new List<Vector3>(32);
        static readonly List<Vector3> s_smoothBases = new List<Vector3>(32);

        static List<Vector3> SmoothTips(IEnumerable<Vector3> points) => Smoothed(points, s_smoothTips);

        static List<Vector3> SmoothBases(IEnumerable<Vector3> points) => Smoothed(points, s_smoothBases);

        static List<Vector3> Smoothed(IEnumerable<Vector3> points, List<Vector3> into)
        {
            if (!s_trail.On || points == null)
                return points.ToList();   // null throws ArgumentNullException as in the game
            into.Clear();
            foreach (Vector3 p in points)
                into.Add(p);
            TrailLists++;
            return into;
        }

        // Exact-length arrays per element type and length; trails hold a few dozen points, so few lengths are in use.
        // Bounded: lengths up to MaxLength, and at most Budget elements kept per type (then the pool starts over).
        static class TrailPool<T> where T : struct
        {
            const int MaxLength = 2048;
            const int Budget = 1 << 16;
            static readonly T[][] s_byLength = new T[MaxLength][];
            static int s_kept;

            internal static T[] Get(int length)
            {
                if ((uint)length >= MaxLength)
                    return null;
                T[] array = s_byLength[length];
                if (array != null)
                    return array;
                if (s_kept + length > Budget)
                {
                    Array.Clear(s_byLength, 0, s_byLength.Length);
                    s_kept = 0;
                }
                array = new T[length];
                s_byLength[length] = array;
                s_kept += length;
                return array;
            }
        }

        // =============================================================================================================
        // ---- IL helpers (shared with UiAllocModule) -----------------------------------------------------------------
        //
        // Harmony's CodeMatcher and CodeInstruction.labels mention System.Reflection.Emit types (ILGenerator, Label) that
        // the netstandard2.1 reference set cannot resolve, so instructions are matched on a plain list and edited in
        // place (an edited instruction keeps its labels and blocks), as in AllocFixes.
        internal static class Il
        {
            static readonly FieldInfo s_labels = AccessTools.Field(typeof(CodeInstruction), "labels");

            internal static bool Unlabeled(CodeInstruction ci) => !(s_labels?.GetValue(ci) is ICollection labels) || labels.Count == 0;

            internal static void Set(CodeInstruction ci, OpCode opcode, object operand)
            {
                ci.opcode = opcode;
                ci.operand = operand;
            }

            internal static bool IsValueLoad(CodeInstruction ci)
            {
                OpCode op = ci.opcode;
                return op == OpCodes.Ldloc_0 || op == OpCodes.Ldloc_1 || op == OpCodes.Ldloc_2 || op == OpCodes.Ldloc_3
                       || op == OpCodes.Ldloc_S || op == OpCodes.Ldloc
                       || op == OpCodes.Ldarg_0 || op == OpCodes.Ldarg_1 || op == OpCodes.Ldarg_2 || op == OpCodes.Ldarg_3
                       || op == OpCodes.Ldarg_S || op == OpCodes.Ldarg;
            }

            // Index of the first run of instructions from 'start' on that matches the pattern, or -1.
            internal static int Find(List<CodeInstruction> code, int start, params Func<CodeInstruction, bool>[] pattern)
            {
                for (int i = Math.Max(0, start); i + pattern.Length <= code.Count; i++)
                {
                    int k = 0;
                    while (k < pattern.Length && pattern[k](code[i + k]))
                        k++;
                    if (k == pattern.Length)
                        return i;
                }
                return -1;
            }

            // Owners of transpilers and IL manipulators that are neither the engine's nor the lab's (its timers are
            // prefixes/postfixes, but any 'valheim.realportal.lab*' patch is ours to coexist with).
            internal static string ForeignTranspiler(MethodBase original)
            {
                Patches info = original == null ? null : Harmony.GetPatchInfo(original);
                if (info == null)
                    return null;
                foreach (Patch p in info.Transpilers)
                    if (!Ours(p.owner))
                        return p.owner;
                foreach (Patch p in info.ILManipulators)
                    if (!Ours(p.owner))
                        return p.owner;
                return null;
            }

            internal static bool Ours(string owner) =>
                owner == EnginePlugin.Guid || (owner != null && owner.StartsWith("valheim.realportal.lab", StringComparison.Ordinal));

            internal static void Transpile(Harmony harmony, string module, Type type, string method, MethodInfo transpiler)
            {
                string where = type.Name + "." + method;
                MethodInfo original = null;
                try
                {
                    original = AccessTools.DeclaredMethod(type, method);
                    if (original == null)
                    {
                        EnginePlugin.Log.LogWarning($"{module}: {where} not found, left as it is.");
                        return;
                    }
                    harmony.Patch(original, transpiler: new HarmonyMethod(transpiler) { priority = Priority.Last });
                }
                catch (Exception e)
                {
                    EnginePlugin.Log.LogError($"{module}: {where} not patched: {e}");
                    // Our transpiler cannot throw (Guard), but the new body can still fail to build; HarmonyX keeps the
                    // transpiler in the method's patch list anyway, so take it out again rather than have it rerun on
                    // every other mod's patch of this method.
                    if (original != null)
                        Rollback(harmony, original, module, transpiler);
                }
            }

            // HarmonyX's PatchProcessor.Patch adds the patches to the method's live PatchInfo (ToPatchInfo returns the
            // shared instance) before UpdateWrapper and never removes them when that throws. Unpatch removes ours and
            // rebuilds the method from the patches that remain; if that fails too, there is nothing more to do here.
            internal static void Rollback(Harmony harmony, MethodBase original, string module, params MethodInfo[] patches)
            {
                foreach (MethodInfo patch in patches)
                {
                    if (patch == null)
                        continue;
                    try
                    {
                        harmony.Unpatch(original, patch);
                    }
                    catch (Exception e)
                    {
                        EnginePlugin.Log.LogError($"{module}: could not take {patch.Name} off {original.DeclaringType?.Name}.{original.Name} again: {e.Message}");
                    }
                }
            }

            // A transpiler of ours matches calls with CodeInstruction.Calls(method), which throws on a null method. A
            // game method an edit swaps (bound by reflection, so a game update can make it null) is checked here before
            // its transpilers are registered; when one is missing the mechanism is not installed and the game's code
            // runs. Returns false (and logs) when any of 'needs' is null.
            internal static bool Ready(string module, string what, params (string Name, MethodInfo Method)[] needs)
            {
                bool ready = true;
                foreach ((string Name, MethodInfo Method) need in needs)
                {
                    if (need.Method != null)
                        continue;
                    ready = false;
                    EnginePlugin.Log.LogWarning($"{module}: {what} not installed: {need.Name} not found (changed by a game update?); the game's code runs.");
                }
                return ready;
            }

            // Runs a transpiler body so that it never throws back into Harmony (see the module header: HarmonyX would
            // keep a throwing transpiler registered and rerun it inside every later Patch()/Unpatch() of the method by
            // any mod, making those fail). The body edits instructions in place; if it throws, every instruction gets
            // its opcode and operand back, the method is reported as left (-1) and the code is returned as received.
            static readonly HashSet<string> s_failed = new HashSet<string>();

            internal static IEnumerable<CodeInstruction> Guard(IEnumerable<CodeInstruction> code, MethodBase original, string module,
                                                               Func<List<CodeInstruction>, List<CodeInstruction>> body)
            {
                var list = new List<CodeInstruction>(code);
                var opcodes = new OpCode[list.Count];
                var operands = new object[list.Count];
                for (int i = 0; i < list.Count; i++)
                {
                    opcodes[i] = list[i].opcode;
                    operands[i] = list[i].operand;
                }
                try
                {
                    return body(list);
                }
                catch (Exception e)
                {
                    for (int i = 0; i < list.Count; i++)
                        Set(list[i], opcodes[i], operands[i]);
                    try
                    {
                        string where = original != null ? original.DeclaringType?.Name + "." + original.Name : "?";
                        if (s_failed.Add(module + ": " + where + ": " + e.GetType().FullName))
                            EnginePlugin.Log.LogError($"{module}: {where}: the edit failed and the method is left as it is: {e}");
                        Report(module, original, $"left as it is: the edit failed ({e.GetType().Name})", -1);
                    }
                    catch (Exception)
                    {
                        // Logging must not undo the point of the guard.
                    }
                    return list;
                }
            }

            // Harmony re-runs every transpiler of a method whenever any mod patches it, so a result is logged when it
            // changes, not on every rerun. n == -1: left because of another mod's transpiler.
            static readonly Dictionary<string, int> s_reported = new Dictionary<string, int>();
            // Per module, method and edit, the latest outcome: call sites swapped (> 0) or left as they are (-1). A
            // method left because of another mod's transpiler counts once, under "(left)".
            static readonly Dictionary<string, KeyValuePair<string, int>> s_outcomes = new Dictionary<string, KeyValuePair<string, int>>();

            internal static void Report(string module, MethodBase original, string what, int n, int expected = 0)
            {
                string where = original != null ? original.DeclaringType?.Name + "." + original.Name : "?";
                string key = module + ": " + where + ": " + what;
                string method = module + ": " + where + ": ";
                if (n < 0)
                {
                    foreach (string k in new List<string>(s_outcomes.Keys))
                        if (k.StartsWith(method, StringComparison.Ordinal))
                            s_outcomes.Remove(k);
                    s_outcomes[method + "(left)"] = new KeyValuePair<string, int>(module, -1);
                }
                else
                {
                    s_outcomes.Remove(method + "(left)");
                    bool swapped = n > 0 && (expected <= 0 || n == expected);
                    s_outcomes[key] = new KeyValuePair<string, int>(module, swapped ? n : -1);
                }
                Tally();
                if (s_reported.TryGetValue(key, out int last) && last == n)
                    return;
                s_reported[key] = n;
                if (n < 0)
                    EnginePlugin.Log.LogWarning($"{module}: {where}: {what}.");
                else if (n == 0)
                    EnginePlugin.Log.LogWarning($"{module}: {where}: {what}: not found (changed by another mod or an update?), left as it is.");
                else if (expected > 0 && n != expected)
                    EnginePlugin.Log.LogWarning($"{module}: {where}: {what}: {n} calls where the game has {expected}; left as it is.");
                else
                    EnginePlugin.Log.LogInfo($"{module}: {where}: {what} x{n}");
            }

            static void Tally()
            {
                int crowdPatched = 0, crowdLeft = 0, uiPatched = 0, uiLeft = 0;
                foreach (KeyValuePair<string, int> o in s_outcomes.Values)
                {
                    bool crowd = o.Key == "CrowdAlloc";
                    if (o.Value > 0)
                    {
                        if (crowd) crowdPatched += o.Value;
                        else uiPatched += o.Value;
                    }
                    else if (crowd)
                    {
                        crowdLeft++;
                    }
                    else
                    {
                        uiLeft++;
                    }
                }
                SitesPatched = crowdPatched;
                SitesLeft = crowdLeft;
                UiAllocModule.SitesPatched = uiPatched;
                UiAllocModule.SitesLeft = uiLeft;
            }

            // Swaps every call matching 'match' for 'to(call)', only if there are exactly 'expected' of them (a pool or a
            // memo serves the game's known call sites; an extra call from an update or a mod may break that). The whole
            // method is left as it is when another mod transpiles it. Guarded: never throws.
            internal static IEnumerable<CodeInstruction> SwapAll(IEnumerable<CodeInstruction> code, MethodBase original, string module, string what,
                                                                 Func<CodeInstruction, bool> match, Func<CodeInstruction, MethodInfo> to, int expected) =>
                Guard(code, original, module, list =>
                {
                    string foreign = ForeignTranspiler(original);
                    if (foreign != null)
                    {
                        Report(module, original, $"left as it is: transpiled by {foreign}", -1);
                        return list;
                    }
                    SwapGroup(list, original, module, what, match, to, expected);
                    return list;
                });

            internal static int SwapGroup(List<CodeInstruction> list, MethodBase original, string module, string what,
                                          Func<CodeInstruction, bool> match, Func<CodeInstruction, MethodInfo> to, int expected)
            {
                int n = 0;
                foreach (CodeInstruction ci in list)
                    if (match(ci))
                        n++;
                if (n == expected)
                {
                    // Build every replacement before editing, so a failure (logged by Harmony's caller) edits nothing.
                    var targets = new List<KeyValuePair<CodeInstruction, MethodInfo>>(n);
                    foreach (CodeInstruction ci in list)
                        if (match(ci))
                            targets.Add(new KeyValuePair<CodeInstruction, MethodInfo>(ci, to(ci)));
                    foreach (KeyValuePair<CodeInstruction, MethodInfo> t in targets)
                        Set(t.Key, OpCodes.Call, t.Value);
                }
                Report(module, original, what, n, expected);
                return n == expected ? n : 0;
            }
        }
    }
}
