using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace RealPortal.Engine
{
    // ZSyncTransform does its full sync work for every synced object every frame (owner: OwnerSync in LateUpdate) and
    // every physics step (others' objects: ClientSync), also for the ones that are asleep and unchanged: item hoards,
    // parked carts, everything in far areas. The lab measured 0.85-2.5 us per object per frame in OwnerSync. For such an
    // object the game's code reads a dozen native properties and writes nothing (or writes values that are already
    // there). These prefixes (Priority.Last, after every other prefix; honouring __runOriginal) recognise exactly that
    // state with fewer reads and skip the body; anything they are not sure about runs the game's code.
    //
    // SyncIdleOwner - OwnerSync is skipped when all of these hold (each is something the game's code reads):
    //  - m_wasOwner and the ZDO is owned (no ownership flip: the flip path teleports to the ZDO);
    //  - not m_characterParentSync (players and creatures: the relative-position path), and for a body-less object no
    //    Projectile (its velocity comes from the projectile);
    //  - transform.hasChanged is false, so the rotation and scale blocks do nothing;
    //  - transform.position.y >= -5000 (the fell-out-of-world rescue), read as the game reads it;
    //  - with m_syncPosition: the position and velocity the game would read (body or transform) Equal m_positionCached
    //    and m_velocityCached, so the game writes neither (Vector3.Equals, the game's own test);
    //  - with a body and m_syncBodyVelocity: the ZDO already holds body.linearVelocity and body.angularVelocity under
    //    body_vel / body_avel (the game's ZDO.Set compares the stored value with Equals and does nothing when equal);
    //  - with a body: body.useGravity already equals m_useGravity. The game writes it again every frame; writing the
    //    value a flag already has does not change it, and PhysX documents eDISABLE_GRAVITY as not waking the actor.
    //    This is the one claim about native code; SyncIdleVerify checks it (sleep state before/after the game's run).
    //  Under these conditions the game's run leaves every field, the ZDO (revision included) and the transform as they
    //  were. The only difference: the game re-assigns m_positionCached/m_velocityCached with values that are Equal
    //  (they may differ in the sign of a zero), which nothing can tell apart (they are only ever compared with Equals).
    //  Nothing replicated changes: no ZDO write is skipped, because the game makes none in this state.
    //
    // SyncIdleClient - ClientSync of an object this game does not own: someone else's (multiplayer client, or the host
    // for clients' objects) or nobody's. The second case happens in single player too: ZDOMan.ReleaseNearbyZDOS gives
    // up ownership outside the player's active area, and Real Portal claims only the near zones of a far area
    // (RemoteAreas.Claim), so the rest of a far area (all of it with LiveFarSide off) runs ClientSync's full body every
    // physics step. For an owned object (everything near a single player) the prefix makes one call that reads the
    // ZDO's owner and returns, before reading anything else, and the postfix gets a null state.
    // The game's code is a deterministic function of: the ZDO (position, rotation, revision, owner, and through the
    // revision its extra data), the transform's position and rotation, m_targetPosTimer, m_posRevision, the body's
    // sleep/gravity state and dt. When one full run of it wrote nothing (the game's own no-op tests held:
    // Distance(transform, ZDO position + velocity * timer) <= 0.001 and, with m_syncRotation, Angle(transform, ZDO
    // rotation) <= 0.001; revision not bumped, timer already at its 2 s cap or no owner, body asleep with gravity off
    // afterwards), the next run with identical inputs writes nothing either. The no-op tests are the game's branches,
    // not "the transform kept its value": far from the origin (|x| or |z| >= 8192, a float step of 0.00098) a 0.2 Lerp
    // step across a gap of two float steps rounds back to where it was, so the game writes the same position, calls
    // Physics.SyncTransforms() and wakes and re-sleeps the body on every step, forever; such an object is never
    // recorded. A postfix records those inputs for such a run; later runs whose inputs Equal the record are skipped,
    // doing only what the game's run would still change: m_lastUpdateFrame = frame, m_haveTempRelPos = false and the
    // timer step
    // (timer += dt, then Min(timer, 2), which stays 2). Excluded (always the game's code): kinematic bodies,
    // m_characterParentSync, m_syncScale (the game rewrites localScale every run), no m_syncPosition, and
    // m_syncBodyVelocity objects whose ZDO says they move (|v| or |av| > 0.01; the "small" ones take the game's
    // Sleep() branch, which on a sleeping body changes nothing). Inputs the record does not hold change the revision:
    // every ZDO.Set* bumps it, and so does every received update; a position set without a bump (InternalSetPosition on
    // a non-owner) and the rotation are compared directly. A mod writing ZDOExtraData directly without a revision bump
    // would be missed (no installed mod does; SyncIdleVerify would show it). Real Portal's ClientSync postfix (portal
    // crossings, read from a counter whose change bumps the revision) still runs on every call, skipped or not.
    // The records live in a side table, dropped in OnDisable and when a switch changes.
    //
    // Both stand down while another mod transpiles OwnerSync or ClientSync (checked every 10 s), since skipping the
    // body would skip its change. SyncIdleVerify (lab, off) runs the game's code instead on every 16th skip and
    // compares what it did with "nothing" (SyncIdleOwner: revision, caches, hasChanged, gravity, sleep state;
    // SyncIdleClient: transform, the game's no-op tests, timer, revision, sleep, gravity); mismatches are counted and
    // logged.
    [EngineModule]
    public static class SyncIdleModule
    {
        static readonly Feature s_owner = Features.Register("SyncIdleOwner", true,
            "ZSyncTransform.OwnerSync is skipped for owned objects that are provably unchanged (same owner, transform unchanged, position/velocity equal to what was last written).");
        static readonly Feature s_client = Features.Register("SyncIdleClient", true,
            "Multiplayer: ZSyncTransform.ClientSync is skipped for others' objects whose inputs equal those of a previous run that moved nothing.");
        static readonly Feature s_verify = Features.Register("SyncIdleVerify", false,
            "Lab: every 16th SyncIdle skip runs the game's code instead and checks that it changed nothing.");

        // Lab counters.
        public static long OwnerSkipped, ClientSkipped, ClientRecorded, VerifyChecks, VerifyMismatches;
        public static int ClientRecords => s_idle.Count;
        public static bool OwnerStoodDown => s_ownerForeign;
        public static bool ClientStoodDown => s_clientForeign;

        const string LabOwners = "valheim.realportal.lab";
        const float ForeignCheckSeconds = 10f;

        static readonly AccessTools.FieldRef<ZSyncTransform, uint> s_posRevision = AccessTools.FieldRefAccess<ZSyncTransform, uint>("m_posRevision");
        static readonly AccessTools.FieldRef<ZSyncTransform, float> s_targetPosTimer = AccessTools.FieldRefAccess<ZSyncTransform, float>("m_targetPosTimer");
        static readonly AccessTools.FieldRef<ZSyncTransform, int> s_lastUpdateFrame = AccessTools.FieldRefAccess<ZSyncTransform, int>("m_lastUpdateFrame");
        static readonly AccessTools.FieldRef<ZSyncTransform, bool> s_wasOwner = AccessTools.FieldRefAccess<ZSyncTransform, bool>("m_wasOwner");
        static readonly AccessTools.FieldRef<ZSyncTransform, Vector3> s_positionCached = AccessTools.FieldRefAccess<ZSyncTransform, Vector3>("m_positionCached");
        static readonly AccessTools.FieldRef<ZSyncTransform, Vector3> s_velocityCached = AccessTools.FieldRefAccess<ZSyncTransform, Vector3>("m_velocityCached");
        static readonly AccessTools.FieldRef<ZSyncTransform, Rigidbody> s_body = AccessTools.FieldRefAccess<ZSyncTransform, Rigidbody>("m_body");
        static readonly AccessTools.FieldRef<ZSyncTransform, ZNetView> s_nview = AccessTools.FieldRefAccess<ZSyncTransform, ZNetView>("m_nview");
        static readonly AccessTools.FieldRef<ZSyncTransform, bool> s_isKinematicBody = AccessTools.FieldRefAccess<ZSyncTransform, bool>("m_isKinematicBody");
        static readonly AccessTools.FieldRef<ZSyncTransform, bool> s_haveTempRelPos = AccessTools.FieldRefAccess<ZSyncTransform, bool>("m_haveTempRelPos");
        static readonly AccessTools.FieldRef<ZDO, Vector3> s_zdoRotation = AccessTools.FieldRefAccess<ZDO, Vector3>("m_rotation");

        static MethodInfo s_ownerSync, s_clientSync;
        static bool s_ownerForeign, s_clientForeign;
        static float s_nextForeignCheck;
        static int s_countdown;
        static int s_sample;

        const byte None = 0, ClientCandidate = 1, ClientVerify = 2, OwnerVerify = 3;

        // Harmony's __state: what the prefix saw, for the postfix (candidate recording or verification). Null on the
        // common paths (a skip, or an object this does not handle), so those pay for one reference, not a struct copy;
        // the few that are needed come from a small pool.
        public sealed class State
        {
            internal byte Mode;
            internal ZSyncTransform Inst;   // the client postfix's instance (it takes only __state)
            internal ZDO Zdo;
            internal uint Revision;
            internal bool HasOwner;
            internal int Frame;
            internal float Timer;
            internal Vector3 Pos, Vel;
            internal Quaternion Rot;
            internal bool Gravity, Sleeping, HasBody;
        }

        static readonly Stack<State> s_pool = new Stack<State>();

        static State Rent(byte mode)
        {
            State s = s_pool.Count != 0 ? s_pool.Pop() : new State();
            s.Mode = mode;
            return s;
        }

        static void Return(State s)
        {
            s.Zdo = null;
            s.Inst = null;
            s.Mode = None;
            if (s_pool.Count < 64)
                s_pool.Push(s);
        }

        sealed class ClientIdle
        {
            internal ZDO Zdo;
            internal uint Revision;
            internal bool HasOwner;
            internal Vector3 ZdoPosition, ZdoRotation, Position;
            internal Quaternion Rotation;
        }

        sealed class ByReference : IEqualityComparer<ZSyncTransform>
        {
            public bool Equals(ZSyncTransform a, ZSyncTransform b) => ReferenceEquals(a, b);
            public int GetHashCode(ZSyncTransform o) => RuntimeHelpers.GetHashCode(o);
        }

        static readonly Dictionary<ZSyncTransform, ClientIdle> s_idle = new Dictionary<ZSyncTransform, ClientIdle>(new ByReference());

        static void Install(Harmony harmony)
        {
            s_ownerSync = AccessTools.DeclaredMethod(typeof(ZSyncTransform), "OwnerSync");
            s_clientSync = AccessTools.DeclaredMethod(typeof(ZSyncTransform), "ClientSync", new[] { typeof(float) });
            MethodInfo onDisable = AccessTools.DeclaredMethod(typeof(ZSyncTransform), "OnDisable");
            if (s_ownerSync == null || s_clientSync == null || onDisable == null)
                throw new MissingMethodException("SyncIdle: ZSyncTransform.OwnerSync/ClientSync/OnDisable not found (game update?)");
            Type self = typeof(SyncIdleModule);
            // The record cleanup first: without it the client records could outlive their objects.
            harmony.Patch(onDisable, postfix: new HarmonyMethod(self, nameof(Disabled)));
            harmony.Patch(s_ownerSync, prefix: new HarmonyMethod(self, nameof(OwnerBefore)) { priority = Priority.Last },
                postfix: new HarmonyMethod(self, nameof(OwnerAfter)) { priority = Priority.Last });
            harmony.Patch(s_clientSync, prefix: new HarmonyMethod(self, nameof(ClientBefore)) { priority = Priority.Last },
                postfix: new HarmonyMethod(self, nameof(ClientAfter)) { priority = Priority.Last });
            s_owner.Switched += _ => s_nextForeignCheck = 0f;
            s_client.Switched += _ =>
            {
                s_idle.Clear();
                s_nextForeignCheck = 0f;
            };
            s_verify.Switched += _ => s_sample = 0;
        }

        // ---- owner --------------------------------------------------------------------------------------------------

        static bool OwnerBefore(ZSyncTransform __instance, bool __runOriginal, out State __state, ZNetView ___m_nview, Rigidbody ___m_body,
            Projectile ___m_projectile, bool ___m_wasOwner, bool ___m_syncPosition, bool ___m_syncBodyVelocity, bool ___m_characterParentSync,
            bool ___m_useGravity, Vector3 ___m_positionCached, Vector3 ___m_velocityCached)
        {
            __state = null;
            if (!s_owner.On || !__runOriginal || !___m_wasOwner || ___m_characterParentSync)
                return true;
            if (--s_countdown <= 0)
                CheckForeign();
            if (s_ownerForeign)
                return true;
            ZDO zdo = ___m_nview.GetZDO();
            if (zdo == null || !zdo.IsOwner())
                return true;
            Transform t = __instance.transform;
            if (t.hasChanged)
                return true;
            Vector3 tpos = t.position;
            if (tpos.y < -5000f)
                return true;
            bool body = ___m_body;
            if (___m_syncPosition)
            {
                Vector3 pos, vel;
                if (body)
                {
                    pos = ___m_body.position;
                    vel = ___m_body.linearVelocity;
                }
                else
                {
                    if (___m_projectile != null)
                        return true;
                    pos = tpos;
                    vel = Vector3.zero;
                }
                if (!___m_positionCached.Equals(pos) || !___m_velocityCached.Equals(vel))
                    return true;
            }
            if (body)
            {
                if (___m_syncBodyVelocity)
                {
                    if (!ZDOExtraData.GetVec3(zdo.m_uid, ZDOVars.s_bodyVelHash, out Vector3 stored) || !stored.Equals(___m_body.linearVelocity))
                        return true;
                    if (!ZDOExtraData.GetVec3(zdo.m_uid, ZDOVars.s_bodyAVelHash, out stored) || !stored.Equals(___m_body.angularVelocity))
                        return true;
                }
                if (___m_body.useGravity != ___m_useGravity)
                    return true;
            }
            if (s_verify.On && (++s_sample & 15) == 0)
            {
                __state = Rent(OwnerVerify);
                __state.Zdo = zdo;
                __state.Revision = zdo.DataRevision;
                __state.Pos = ___m_positionCached;
                __state.Vel = ___m_velocityCached;
                __state.HasBody = body;
                if (body)
                {
                    __state.Gravity = ___m_body.useGravity;
                    __state.Sleeping = ___m_body.IsSleeping();
                }
                return true;
            }
            OwnerSkipped++;
            return false;
        }

        static void OwnerAfter(ZSyncTransform __instance, State __state)
        {
            if (__state == null)
                return;
            try
            {
                VerifyOwner(__instance, __state);
            }
            finally
            {
                Return(__state);
            }
        }

        static void VerifyOwner(ZSyncTransform __instance, State __state)
        {
            VerifyChecks++;
            string bad = null;
            if (__state.Zdo.DataRevision != __state.Revision)
                bad = "the game wrote the ZDO";
            else if (!s_wasOwner(__instance))
                bad = "ownership changed";
            else if (__instance.transform.hasChanged)
                bad = "transform.hasChanged left true";
            else if (!s_positionCached(__instance).Equals(__state.Pos) || !s_velocityCached(__instance).Equals(__state.Vel))
                bad = "cached position/velocity changed";
            else if (__state.HasBody)
            {
                Rigidbody body = s_body(__instance);
                if (body.useGravity != __state.Gravity)
                    bad = "useGravity changed";
                else if (body.IsSleeping() != __state.Sleeping)
                    bad = "the useGravity write changed the sleep state";
            }
            if (bad != null)
                Mismatch(__instance, "owner", bad);
        }

        // ---- client -------------------------------------------------------------------------------------------------

        // Runs for every ZSyncTransform on every physics step, owned or not, so it takes only what the owner test needs
        // (one injected field) and reads the rest through field refs after the game's own early return (IsOwner).
        static bool ClientBefore(ZSyncTransform __instance, float dt, bool __runOriginal, out State __state, ZNetView ___m_nview)
        {
            __state = null;
            if (!s_client.On || !__runOriginal)
                return true;
            ZDO zdo = ___m_nview.GetZDO();
            if (zdo == null || zdo.IsOwner())
                return true;
            if (!__instance.m_syncPosition || __instance.m_characterParentSync || __instance.m_syncScale || s_isKinematicBody(__instance))
                return true;
            uint revision = zdo.DataRevision;
            if (revision != s_posRevision(__instance))
                return true;    // a new revision: the game's code (it resets the timer)
            bool hasOwner = zdo.HasOwner();
            ref float timer = ref s_targetPosTimer(__instance);
            if (hasOwner && timer != 2f)
                return true;    // still interpolating along the velocity
            int frame = Time.frameCount;
            ref int lastUpdateFrame = ref s_lastUpdateFrame(__instance);
            if (lastUpdateFrame == frame)
                return true;    // the game's early return
            if (--s_countdown <= 0)
                CheckForeign();
            if (s_clientForeign)
                return true;

            Transform t = __instance.transform;
            Vector3 tpos = t.position;
            Quaternion trot = t.rotation;
            Rigidbody body;
            if (s_idle.TryGetValue(__instance, out ClientIdle rec) && ReferenceEquals(rec.Zdo, zdo) && rec.Revision == revision && rec.HasOwner == hasOwner
                && rec.ZdoPosition.Equals(zdo.GetPosition()) && rec.ZdoRotation.Equals(s_zdoRotation(zdo))
                && rec.Position.Equals(tpos) && rec.Rotation.Equals(trot)
                && (!(body = s_body(__instance)) || !body.useGravity && body.IsSleeping()))
            {
                if (s_verify.On && (++s_sample & 15) == 0)
                {
                    __state = Rent(ClientVerify);
                    __state.Timer = timer;
                    Fill(__state, __instance, zdo, revision, hasOwner, frame, tpos, trot);
                    return true;
                }
                lastUpdateFrame = frame;
                s_haveTempRelPos(__instance) = false;
                if (hasOwner)
                {
                    timer += dt;
                    timer = Mathf.Min(timer, 2f);
                }
                ClientSkipped++;
                return false;
            }
            __state = Rent(ClientCandidate);
            Fill(__state, __instance, zdo, revision, hasOwner, frame, tpos, trot);
            return true;
        }

        static void Fill(State s, ZSyncTransform inst, ZDO zdo, uint revision, bool hasOwner, int frame, Vector3 tpos, Quaternion trot)
        {
            s.Inst = inst;
            s.Zdo = zdo;
            s.Revision = revision;
            s.HasOwner = hasOwner;
            s.Frame = frame;
            s.Pos = tpos;
            s.Rot = trot;
        }

        // Only __state: Harmony calls this for every object on every step, and for owned ones the state is null.
        static void ClientAfter(State __state)
        {
            if (__state == null)
                return;
            try
            {
                if (__state.Mode == ClientVerify)
                    VerifyClient(__state.Inst, __state);
                else
                    Record(__state.Inst, __state);
            }
            finally
            {
                Return(__state);
            }
        }

        // The game's own tests for writing the transform in a non-parented ClientSync (SyncPosition's distance test
        // against the ZDO position plus velocity * timer, and the rotation test), on the values it reads. True when
        // neither branch writes.
        static bool GameWritesNothing(ZSyncTransform inst, ZDO zdo, bool hasOwner, Vector3 tpos, Quaternion trot)
        {
            Vector3 position = zdo.GetPosition();
            if (hasOwner)
            {
                Vector3 vel = zdo.GetVec3(ZDOVars.s_velHash, Vector3.zero);
                position += vel * s_targetPosTimer(inst);
            }
            if (Vector3.Distance(tpos, position) > 0.001f)
                return false;
            return !inst.m_syncRotation || !(Quaternion.Angle(trot, zdo.GetRotation()) > 0.001f);
        }

        static void Record(ZSyncTransform __instance, State __state)
        {
            // A full run that started idle-looking: record it if it wrote nothing.
            ZDO zdo = __state.Zdo;
            if (s_lastUpdateFrame(__instance) != __state.Frame || zdo.IsOwner() || zdo.DataRevision != __state.Revision
                || s_posRevision(__instance) != __state.Revision || zdo.HasOwner() != __state.HasOwner
                || __state.HasOwner && s_targetPosTimer(__instance) != 2f)
                return;
            Transform t = __instance.transform;
            Vector3 tpos = t.position;
            Quaternion trot = t.rotation;
            if (!tpos.Equals(__state.Pos) || !trot.Equals(__state.Rot))
                return;     // it moved (converging, or someone moved it): not idle yet
            // Unchanged values are not enough: the game may have written the same value (a Lerp step that rounds back),
            // with Physics.SyncTransforms() and a wake/Sleep of the body. Only its own no-op branches count.
            if (!GameWritesNothing(__instance, zdo, __state.HasOwner, tpos, trot))
                return;
            Rigidbody body = s_body(__instance);
            if (body)
            {
                if (body.useGravity || !body.IsSleeping())
                    return;
                if (__state.HasOwner && SyncsBodyVelocity(__instance))
                {
                    // The game's branch for these: velocities from the ZDO (moving), or Sleep() when both are small.
                    if (zdo.GetVec3(ZDOVars.s_bodyVelHash, Vector3.zero).magnitude > 0.01f || zdo.GetVec3(ZDOVars.s_bodyAVelHash, Vector3.zero).magnitude > 0.01f)
                        return;
                }
            }
            if (!s_idle.TryGetValue(__instance, out ClientIdle rec))
            {
                rec = new ClientIdle();
                s_idle.Add(__instance, rec);
            }
            rec.Zdo = zdo;
            rec.Revision = __state.Revision;
            rec.HasOwner = __state.HasOwner;
            rec.ZdoPosition = zdo.GetPosition();
            rec.ZdoRotation = s_zdoRotation(zdo);
            rec.Position = tpos;
            rec.Rotation = trot;
            ClientRecorded++;
        }

        static bool SyncsBodyVelocity(ZSyncTransform z) => z.m_syncBodyVelocity;

        static void VerifyClient(ZSyncTransform inst, State s)
        {
            VerifyChecks++;
            string bad = null;
            Transform t = inst.transform;
            if (!t.position.Equals(s.Pos) || !t.rotation.Equals(s.Rot))
                bad = "the game moved the transform";
            else if (!GameWritesNothing(inst, s.Zdo, s.HasOwner, s.Pos, s.Rot))
                bad = "the game wrote the transform (same value)";
            else if (s_lastUpdateFrame(inst) != s.Frame)
                bad = "m_lastUpdateFrame differs";
            else if (s_posRevision(inst) != s.Revision || s.Zdo.DataRevision != s.Revision)
                bad = "revision changed";
            else if (s_targetPosTimer(inst) != (s.HasOwner ? 2f : s.Timer))
                bad = "timer differs";   // the skip leaves it at its cap (with an owner) or untouched (without)
            else
            {
                Rigidbody body = s_body(inst);
                if (body && (body.useGravity || !body.IsSleeping()))
                    bad = "body woke or gravity on";
            }
            if (bad != null)
            {
                s_idle.Remove(inst);
                Mismatch(inst, "client", bad);
            }
        }

        static void Disabled(ZSyncTransform __instance)
        {
            if (s_idle.Count != 0)
                s_idle.Remove(__instance);
        }

        // ---- shared -------------------------------------------------------------------------------------------------

        static void Mismatch(ZSyncTransform inst, string side, string what)
        {
            VerifyMismatches++;
            if (VerifyMismatches <= 20)
                EnginePlugin.Log?.LogWarning($"SyncIdleVerify: {side} {(inst ? inst.name : "?")}: {what}");
        }

        static void CheckForeign()
        {
            s_countdown = 1024;
            float now = Time.unscaledTime;
            if (now < s_nextForeignCheck)
                return;
            s_nextForeignCheck = now + ForeignCheckSeconds;
            bool owner, client;
            try
            {
                owner = ForeignTranspiler(s_ownerSync);
                client = ForeignTranspiler(s_clientSync);
            }
            catch (Exception e)
            {
                EnginePlugin.Log?.LogWarning("SyncIdle: reading Harmony patch info failed, the game's code runs: " + e.Message);
                owner = client = true;
            }
            if (owner != s_ownerForeign)
                EnginePlugin.Log?.LogInfo(owner ? "SyncIdleOwner: another mod transpiles ZSyncTransform.OwnerSync, standing down." : "SyncIdleOwner: active.");
            if (client != s_clientForeign)
            {
                EnginePlugin.Log?.LogInfo(client ? "SyncIdleClient: another mod transpiles ZSyncTransform.ClientSync, standing down." : "SyncIdleClient: active.");
                s_idle.Clear();
            }
            s_ownerForeign = owner;
            s_clientForeign = client;
        }

        static bool ForeignTranspiler(MethodBase method)
        {
            HarmonyLib.Patches info = Harmony.GetPatchInfo(method);
            return info != null && (Foreign(info.Transpilers) || Foreign(info.ILManipulators));
        }

        static bool Foreign(IEnumerable<HarmonyLib.Patch> patches)
        {
            if (patches == null)
                return false;
            foreach (HarmonyLib.Patch p in patches)
            {
                if (p.owner == EnginePlugin.Guid || p.owner != null && p.owner.StartsWith(LabOwners, StringComparison.Ordinal))
                    continue;
                return true;
            }
            return false;
        }

        public static string Describe() =>
            $"owner skipped {OwnerSkipped}{(s_ownerForeign ? " (stood down)" : "")}; client skipped {ClientSkipped}, recorded {ClientRecorded}, records {s_idle.Count}"
            + $"{(s_clientForeign ? " (stood down)" : "")}; verify {VerifyChecks} checks, {VerifyMismatches} mismatches";
    }
}
