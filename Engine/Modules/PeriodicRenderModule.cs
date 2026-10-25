using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace RealPortal.Engine
{
    // Three cameras the game renders on a clock, whatever happened since:
    //
    // 1. DepthCamera (DepthCameraSkip, on). InvokeRepeating("RenderDepth", 1, 1) puts its camera 50 m above the player,
    //    rounded to 1 m, forces QualitySettings.lodBias = 10 (every LODGroup at LOD0) and calls
    //    RenderWithShader(m_depthShader, "RenderType"): a full cull of every loaded renderer plus a draw of everything
    //    under it, once a second, then sets _SkyAlphaTexture/_SkyAlphaPosition (rain, snow and wetness under roofs and
    //    canopies). A player standing in a base or a forest re-renders the identical picture every second (a 1 Hz
    //    spike, est. 1-6 ms in bases and forests). With this on, a call renders only when something it would draw may
    //    have changed:
    //     - the rounded position, the camera (mask, size, aspect, far plane) or the texture changed, or the texture lost
    //       its contents (IsCreated false);
    //     - an occluder event inside its footprint (the camera's orthographic square plus ObjectReach) in the last
    //       SettleSeconds: a networked object woke (ZNetView.Awake: ZNetScene loading it, a piece placed, a log spawned)
    //       or went (ZNetView.OnDestroy: destroyed, felled, unloaded), or a heightmap's geometry was poked
    //       (Heightmap.Poke without paintOnly: digging, raising, a TerrainComp change). The settle window renders the
    //       next few calls too, so asynchronous completions (HeightmapBuilder, objects instantiating over frames) are
    //       caught;
    //     - a moving object in the footprint, on a layer the camera draws, moved or turned (ZSyncTransform.Instances:
    //       players, creatures, ships, carts, drops, logs, ragdolls; positions quantized to 0.25 m, rotations to 1/64).
    //       A character's turn does not count, only its moves: an idle player turns its body to the look yaw
    //       (Character.UpdateRotation uses m_lookYaw while m_moveDir is zero), so looking around would force a render
    //       every second, and so would tamed animals idling in a base; a standing body seen from 50 m above covers
    //       nearly the same texels whichever way it faces (DepthCameraVerify measures what that leaves stale);
    //     - MaxAgeSeconds since the last render (what no event reports: swaying canopies, doors and hatches, a
    //       MineRock5 area mined away, damaged-state models, grass reset by building).
    //    A skipped call does everything else RenderDepth does (camera position, the two shader globals, the same values
    //    since the position is the same), so anything that changed those globals in between (Real Portal's window swap
    //    that failed to restore) is put back as by the game. Rain under a roof that no event reported can be wrong for
    //    up to MaxAgeSeconds. ZdoJournal is not used: subscribing arms it for every ZDO write in the world, and object
    //    wake/destroy in the footprint is what matters here (a loaded object is not a ZDO event).
    //    Patch: prefix on DepthCamera.RenderDepth at Priority.VeryLow (after other mods' normal prefixes, before
    //    GrassMainCameraOnly's Priority.Last prefix, which reads __runOriginal and hands no grass to a skipped render);
    //    a prefix that already skipped the render is respected. Another mod's transpiler or IL manipulator on
    //    RenderDepth (checked every 5 s) makes it step aside: every call renders. DepthCameraVerify (lab, off): every
    //    VerifyEverySkips-th skip renders the picture the game would have drawn into a scratch texture and reads both
    //    back (AsyncGPUReadback): VerifyChecks, VerifyIdentical, VerifyDiffTexels/VerifyTexels and VerifyMaxFraction
    //    say how stale the kept picture was (the acceptance test: tiny fractions, from swaying canopies).
    //    An exception in the decision (or in Verify) never costs the game its render: it is logged once, counted
    //    (DepthErrors, VerifyErrors), the module forgets what it rendered, and the call renders as the game's.
    //    Real Portal: FarSkyOcclusion renders its own pictures with its own camera (CopyFrom the game's), at its own
    //    pace, and swaps the globals around window renders (Push/Hide restore what was set); it reads m_texture's size
    //    and m_updateInterval only. Nothing there needs the game's camera to render every second.
    //
    // 2. ReflectionUpdate (ReflectionGate, on). Every m_interval (3 s) Update calls UpdateReflection: the other of two
    //    2 km box probes moves to the reference position and RenderProbe() renders 6 cube faces of the whole scene,
    //    then Update widens it over m_transitionDuration. With this on, the timer's call (only that one: EnvMan's
    //    SetForceEnvironment calls UpdateReflection itself and is left alone) renders only when the picture would
    //    change: the first two renders of an instance; MaxAgeSeconds (15 s) since the last; an environment transition
    //    (EnvMan.m_nextEnv) or another environment; the sun turned more than SunDegrees; the sun's colour x intensity,
    //    ambient or fog colour changed beyond ColorTolerance, fog density by 5 %, _Wet or _AuroraStrength by 0.05; the
    //    probe point moved ProbeMoveMeters. Otherwise the timer is held at max(m_interval, m_transitionDuration): the
    //    call stays due (re-asked every EvalSeconds) and the cross-fade that follows in Update finds its finished state
    //    (current probe 2000 m at importance 1, the other 2001 m at 0), as the game has it right before its next render.
    //    A due render that would land in the frame where DepthCamera's invoke fires and renders is put off by a frame
    //    (at most twice in a row): the two spikes never add up. Reflections lag the time of day by at most ~15 s of game
    //    time. All of the gate's clocks are game time (Time.time), as the game's own timer is: a render through the gate
    //    always comes at least m_interval of game time after the last, never more often than the game's. While the game
    //    is paused (Time.deltaTime 0: the game's timer stands still and renders nothing) the held call decides nothing
    //    and renders nothing; the first running frame decides.
    //    Patch: a transpiler (Priority.Last) swaps exactly one call of UpdateReflection in ReflectionUpdate.Update for
    //    TimedReflection, which calls the game's UpdateReflection (so patches on it run) or holds; found 0 or 2+ times
    //    (another mod, a game update), the method is left as it is (logged, ReflectionGateActive false). Another mod's
    //    transpiler or IL manipulator on Update makes the gate step aside, as DepthCameraSkip does: one the transpiler
    //    sees leaves the method as it is; one added later (checked every 5 s) makes TimedReflection call
    //    UpdateReflection exactly where the game's code did, every time, never touching m_updateTimer. An exception in
    //    the decision is logged once, counted (ReflectionErrors) and the call renders as the game's. A postfix on
    //    UpdateReflection records what each render (from any caller) saw.
    //    ReflectionTimeSlicing (lab, off): the game's two probes render one face per frame (IndividualFaces) instead of
    //    their prefab mode; the original mode is restored when switched off.
    //    Real Portal: FarReflections makes its own probes (copying the game probe's settings) and paces them by
    //    m_interval; it reads the game probes' position and importance to drop its own when the game's has followed the
    //    player over there. Holding the timer changes neither.
    //
    // 3. The sky camera (SkyCameraOff, OFF). GameCamera.m_skyCamera (depth -2, mask layer 19 'skybox', near 100, far
    //    100 km, deferred, HDR, clear Skybox) renders every frame before the game camera, which also clears with Skybox
    //    and has layer 19 in its own mask (far 20 km): the sky camera's colour looks overwritten, so its whole pass
    //    (a cull of every renderer, G-buffer, lighting, resolve) would be wasted. Unproven: the game camera may render
    //    through UpscaledFrameBuffer, and layer-19 objects between 20 and 100 km would only show through it. Off until a
    //    screenshot diff (day, night, aurora, Mistlands, Ashlands, Yggdrasil) shows no difference; on, it only sets
    //    m_skyCamera.enabled = false (GameCamera keeps syncing its FOV, harmless) and gives it back when switched off
    //    (only if it is still the camera this module disabled). SkyCameraDescribe() prints both cameras for the lab.
    //
    // Nothing here is replicated: these are local pictures; no ZDO, RPC or gameplay code reads them.
    [EngineModule]
    public static class PeriodicRenderModule
    {
        public static readonly Feature DepthSkipFeature = Features.Register("DepthCameraSkip", false,
            "The sky-occlusion depth picture (DepthCamera, re-rendered every second at LOD0) is rendered only when the player moved, " +
            "an object appeared, went or moved under it, the terrain there changed, or 10 s passed. Rain under a roof no event " +
            "reported can lag up to 10 s.");
        public static readonly Feature DepthVerifyFeature = Features.Register("DepthCameraVerify", false,
            "Lab: with DepthCameraSkip, every 5th skipped picture is also rendered aside and compared with the kept one on the GPU readback.");
        public static readonly Feature ReflectionGateFeature = Features.Register("ReflectionGate", false,
            "The reflection probe's 3 s re-render is skipped while the light, weather, sun angle and probe point have not changed " +
            "(at most 15 s), and never lands in the frame of a depth-picture render.");
        public static readonly Feature ReflectionSliceFeature = Features.Register("ReflectionTimeSlicing", false,
            "Lab: the game's reflection probes render one cube face per frame instead of their prefab mode.");
        public static readonly Feature SkyCameraOffFeature = Features.Register("SkyCameraOff", false,
            "Disables the game's sky camera (a second full camera pass every frame whose output the game camera's Skybox clear " +
            "appears to overwrite). Off until a screenshot diff proves it invisible.");

        // ---- tuning -----------------------------------------------------------------------------------------------

        const float MaxAgeSeconds = 10f, SettleSeconds = 3f, ObjectReach = 16f, PerspectiveReachCap = 500f;
        const int VerifyEverySkips = 5;
        const float ReflectionMaxAge = 15f, SunDegrees = 3f, ColorTolerance = 0.03f, FogTolerance = 0.05f;
        const float WeatherTolerance = 0.05f, ProbeMoveMeters = 16f, EvalSeconds = 0.25f;
        const int MaxDeferStreak = 2;
        const float PatchCheckSeconds = 5f;

        // ---- lab counters (read by reflection; cumulative ones cleared by ResetCounters) --------------------------

        public static bool DepthInstalled, DepthStoodDown, ReflectionInstalled, ReflectionGateActive, SkyInstalled;
        public static long DepthCalls, DepthRenders, DepthSkips, OccluderEvents;
        public static long DepthFirst, DepthMoved, DepthCameraChanged, DepthAge, DepthOccluder, DepthMovers;
        public static int MoversInFootprint;
        public static float DepthFootprintHalf;
        public static long VerifyChecks, VerifyIdentical, VerifyDiffTexels, VerifyTexels, VerifyUnsupported, VerifyErrors;
        public static double VerifyMaxFraction;
        public static long ReflectionDue, ReflectionRenders, ReflectionSkips, ReflectionDeferred, ReflectionPausedHolds;
        public static long ReflFirst, ReflAge, ReflTransition, ReflEnv, ReflSun, ReflLight, ReflWeather, ReflMoved, ReflNoEnv;
        public static long DepthErrors, ReflectionErrors;
        public static bool SkyCameraDisabled;

        public static void ResetCounters()
        {
            DepthCalls = DepthRenders = DepthSkips = OccluderEvents = 0;
            DepthFirst = DepthMoved = DepthCameraChanged = DepthAge = DepthOccluder = DepthMovers = 0;
            VerifyChecks = VerifyIdentical = VerifyDiffTexels = VerifyTexels = VerifyUnsupported = VerifyErrors = 0;
            VerifyMaxFraction = 0;
            ReflectionDue = ReflectionRenders = ReflectionSkips = ReflectionDeferred = ReflectionPausedHolds = 0;
            ReflFirst = ReflAge = ReflTransition = ReflEnv = ReflSun = ReflLight = ReflWeather = ReflMoved = ReflNoEnv = 0;
            DepthErrors = ReflectionErrors = 0;
        }

        public static string Report() =>
            $"depth camera {(!DepthInstalled ? "not installed" : !DepthSkipFeature.On ? "off" : DepthStoodDown ? "stepped aside (another mod rewrites RenderDepth)" : "on")}: " +
            $"calls {DepthCalls}, renders {DepthRenders} (first {DepthFirst}, moved {DepthMoved}, camera {DepthCameraChanged}, age {DepthAge}, " +
            $"occluder {DepthOccluder}, movers {DepthMovers}), skips {DepthSkips}, occluder events {OccluderEvents}, movers in footprint {MoversInFootprint}, " +
            $"footprint half {DepthFootprintHalf:F0} m, errors {DepthErrors} | verify {VerifyChecks} (identical {VerifyIdentical}, texels {VerifyDiffTexels}/{VerifyTexels}, " +
            $"worst {VerifyMaxFraction:P3}, unsupported {VerifyUnsupported}, errors {VerifyErrors}) | " +
            $"reflection {(!ReflectionInstalled ? "not installed" : !ReflectionGateFeature.On ? "off" : s_reflectionForeign ? "stepped aside (another mod rewrites Update)" : !ReflectionGateActive ? "inert (call not found or Update rewritten)" : "on")}: " +
            $"due {ReflectionDue}, renders {ReflectionRenders} (first {ReflFirst}, age {ReflAge}, transition {ReflTransition}, env {ReflEnv}, sun {ReflSun}, " +
            $"light {ReflLight}, weather {ReflWeather}, moved {ReflMoved}, no env {ReflNoEnv}), skips {ReflectionSkips}, deferred {ReflectionDeferred}, " +
            $"held while paused {ReflectionPausedHolds}, errors {ReflectionErrors}, " +
            $"time slicing {(ReflectionSliceFeature.On ? "individual faces" : "prefab")} | sky camera {(!SkyInstalled ? "not installed" : SkyCameraDisabled ? "disabled" : "as the game")}";

        static bool Ours(string owner) =>
            owner == EnginePlugin.Guid || owner != null && owner.StartsWith("valheim.realportal.lab", StringComparison.Ordinal);

        // The owner of another mod's transpiler or IL manipulator on the method (null: none). Our own and the lab's do
        // not count.
        static string ForeignRewriter(Patches info)
        {
            if (info == null)
                return null;
            string who = null;
            foreach (Patch p in info.Transpilers)
                if (!Ours(p.owner))
                    who = p.owner;
            foreach (Patch p in info.ILManipulators)
                if (!Ours(p.owner))
                    who = p.owner;
            return who;
        }

        static void Install(Harmony harmony)
        {
            // Three independent parts: one that cannot install leaves the others working.
            Exception first = null;
            try
            {
                InstallDepth(harmony);
            }
            catch (Exception e)
            {
                first = e;
                EnginePlugin.Log?.LogError($"PeriodicRender: DepthCameraSkip not installed: {e}");
            }
            try
            {
                InstallReflection(harmony);
            }
            catch (Exception e)
            {
                first ??= e;
                EnginePlugin.Log?.LogError($"PeriodicRender: ReflectionGate not installed: {e}");
            }
            try
            {
                InstallSky(harmony);
            }
            catch (Exception e)
            {
                first ??= e;
                EnginePlugin.Log?.LogError($"PeriodicRender: SkyCameraOff not installed: {e}");
            }
            if (!DepthInstalled && !ReflectionInstalled && !SkyInstalled && first != null)
                throw first;
        }

        // ==== 1. DepthCamera ========================================================================================

        // Shader ids in a class of their own: its initializer runs at the first render, not at Install (which also runs
        // outside Unity in the offline harness, where Shader.PropertyToID has no native side).
        static class Ids
        {
            internal static readonly int SkyAlphaTexture = Shader.PropertyToID("_SkyAlphaTexture");
            internal static readonly int SkyAlphaPosition = Shader.PropertyToID("_SkyAlphaPosition");
            internal static readonly int Wet = Shader.PropertyToID("_Wet");
            internal static readonly int Aurora = Shader.PropertyToID("_AuroraStrength");
        }

        static MethodInfo s_renderDepth;
        static AccessTools.FieldRef<DepthCamera, Camera> s_cameraOf;

        // What the last render was taken with.
        static bool s_haveRender;
        static Vector3 s_lastPos;
        static Camera s_lastCam;
        static RenderTexture s_lastTex;
        static int s_lastMask;
        static float s_lastOrtho, s_lastAspect, s_lastFar, s_lastRenderAt, s_depthOffset;
        static bool s_lastOrthographic;
        static long s_moverSig;
        // The footprint events are tested against (valid only while skipping is possible).
        static bool s_footprintValid;
        static Vector3 s_center;
        static float s_half;
        static float s_dirtyUntil = -1f;
        static float s_nextPatchCheck;
        // For ReflectionGate's timing: the game time of the last RenderDepth invoke and the interval.
        static bool s_haveInvoke;
        static float s_lastInvokeGame, s_invokeInterval = 1f;
        static int s_lastDepthRenderFrame = -1;

        static void InstallDepth(Harmony harmony)
        {
            s_renderDepth = AccessTools.DeclaredMethod(typeof(DepthCamera), "RenderDepth");
            MethodInfo awake = AccessTools.DeclaredMethod(typeof(ZNetView), "Awake");
            MethodInfo destroy = AccessTools.DeclaredMethod(typeof(ZNetView), "OnDestroy");
            MethodInfo poke = AccessTools.DeclaredMethod(typeof(Heightmap), nameof(Heightmap.Poke), new[] { typeof(int), typeof(bool) });
            FieldInfo camera = AccessTools.DeclaredField(typeof(DepthCamera), "m_camera");
            if (s_renderDepth == null || awake == null || destroy == null || poke == null || camera == null || camera.FieldType != typeof(Camera))
                throw new MissingMemberException("DepthCamera.RenderDepth/m_camera, ZNetView.Awake/OnDestroy or Heightmap.Poke(int, bool) not found (game update?)");
            s_cameraOf = AccessTools.FieldRefAccess<DepthCamera, Camera>(camera);
            var done = new List<KeyValuePair<MethodBase, MethodInfo>>();
            try
            {
                Patch(harmony, done, s_renderDepth, nameof(BeforeRenderDepth), true, Priority.VeryLow);
                Patch(harmony, done, awake, nameof(AfterViewAwake), false, Priority.Last);
                Patch(harmony, done, destroy, nameof(BeforeViewDestroy), true, Priority.Last);
                Patch(harmony, done, poke, nameof(AfterPoke), false, Priority.Last);
            }
            catch
            {
                Unpatch(harmony, done);
                throw;
            }
            DepthInstalled = true;
            DepthSkipFeature.Switched += _ => ForgetDepth();
        }

        static void Patch(Harmony harmony, List<KeyValuePair<MethodBase, MethodInfo>> done, MethodBase original, string name, bool prefix, int priority)
        {
            MethodInfo method = AccessTools.DeclaredMethod(typeof(PeriodicRenderModule), name);
            var hm = new HarmonyMethod(method) { priority = priority };
            // Listed before the attempt: a patch whose method failed to build stays registered with Harmony and must be
            // taken back too.
            done.Add(new KeyValuePair<MethodBase, MethodInfo>(original, method));
            if (prefix)
                harmony.Patch(original, prefix: hm);
            else
                harmony.Patch(original, postfix: hm);
        }

        static void Unpatch(Harmony harmony, List<KeyValuePair<MethodBase, MethodInfo>> done)
        {
            foreach (KeyValuePair<MethodBase, MethodInfo> p in done)
            {
                try
                {
                    harmony.Unpatch(p.Key, p.Value);
                }
                catch (Exception e)
                {
                    EnginePlugin.Log?.LogWarning($"PeriodicRender: could not take back {p.Value.Name} on {p.Key.Name} (left inert): {e.Message}");
                }
            }
        }

        // The next call renders whatever it finds (switching, a world change, stepping aside).
        static void ForgetDepth()
        {
            s_haveRender = false;
            s_footprintValid = false;
            s_lastCam = null;
            s_lastTex = null;
        }

        static int s_depthErrorsLogged;

        static bool BeforeRenderDepth(DepthCamera __instance, bool __runOriginal)
        {
            if (!__runOriginal)
                return true;    // another prefix skipped it already; true does not bring it back
            try
            {
                return DecideDepth(__instance);
            }
            catch (Exception e)
            {
                // Harmony would carry the exception out of RenderDepth and the game's render with it, every second.
                DepthErrors++;
                if (s_depthErrorsLogged++ < 5)
                    EnginePlugin.Log?.LogError($"DepthCameraSkip: deciding failed (this call renders as the game's): {e}");
                ForgetDepth();
                s_lastDepthRenderFrame = Time.frameCount;
                return true;
            }
        }

        static bool DecideDepth(DepthCamera __instance)
        {
            DepthCalls++;
            s_haveInvoke = true;
            s_lastInvokeGame = Time.time;
            s_invokeInterval = __instance.m_updateInterval;
            s_depthOffset = __instance.m_offset;
            if (!DepthSkipFeature.On)
            {
                if (s_haveRender)
                    ForgetDepth();
                s_lastDepthRenderFrame = Time.frameCount;
                return true;
            }
            float now = Time.unscaledTime;
            if (now >= s_nextPatchCheck)
            {
                s_nextPatchCheck = now + PatchCheckSeconds;
                CheckForeign();
            }
            if (DepthStoodDown)
            {
                s_lastDepthRenderFrame = Time.frameCount;
                return true;
            }
            // As RenderDepth: nothing at all without a game camera.
            Camera main = Utils.GetMainCamera();
            if (main == null)
                return true;
            Camera cam = s_cameraOf(__instance);
            RenderTexture tex = __instance.m_texture;
            if (cam == null || tex == null)
            {
                ForgetDepth();
                s_lastDepthRenderFrame = Time.frameCount;
                return true;
            }
            Vector3 pos = TakenAt(main, __instance.m_offset);
            float half = FootprintHalf(cam);
            long sig = MoverSignature(pos, half, cam.cullingMask);
            string why = WhyRender(cam, tex, pos, sig, now);
            if (why != null)
            {
                s_haveRender = true;
                s_lastPos = pos;
                s_lastCam = cam;
                s_lastTex = tex;
                s_lastMask = cam.cullingMask;
                s_lastOrtho = cam.orthographicSize;
                s_lastAspect = cam.aspect;
                s_lastFar = cam.farClipPlane;
                s_lastOrthographic = cam.orthographic;
                s_lastRenderAt = now;
                s_moverSig = sig;
                s_center = pos;
                s_half = half;
                DepthFootprintHalf = half;
                s_footprintValid = true;
                s_lastDepthRenderFrame = Time.frameCount;
                DepthRenders++;
                return true;
            }
            // Everything RenderDepth leaves behind, without the render: the same position and globals as the last time.
            __instance.transform.position = pos;
            Shader.SetGlobalTexture(Ids.SkyAlphaTexture, tex);
            Shader.SetGlobalVector(Ids.SkyAlphaPosition, __instance.transform.position);
            DepthSkips++;
            if (DepthVerifyFeature.On)
            {
                // A lab check: its failure is counted, the skip stands (the globals are already as the game sets them).
                try
                {
                    Verify(__instance, cam, tex);
                }
                catch (Exception e)
                {
                    VerifyErrors++;
                    s_verifyBusy = false;
                    s_verifyOld = s_verifyNew = null;
                    if (s_depthErrorsLogged++ < 5)
                        EnginePlugin.Log?.LogError($"DepthCameraVerify: the check failed: {e}");
                }
            }
            return false;
        }

        // RenderDepth's own position.
        static Vector3 TakenAt(Camera main, float offset)
        {
            Vector3 position = (Player.m_localPlayer ? Player.m_localPlayer.transform.position : main.transform.position) + Vector3.up * offset;
            position.x = Mathf.Round(position.x);
            position.y = Mathf.Round(position.y);
            position.z = Mathf.Round(position.z);
            return position;
        }

        // Half the side of the ground square the picture covers (an object reaching in from outside is ObjectReach).
        static float FootprintHalf(Camera cam)
        {
            // Not looking straight down: the square is not where it is assumed to be; every event counts.
            if (Vector3.Dot(cam.transform.forward, Vector3.down) < 0.99f)
                return 1e7f;
            if (cam.orthographic)
                return cam.orthographicSize * Mathf.Max(1f, cam.aspect);
            return Mathf.Min(PerspectiveReachCap, Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * cam.farClipPlane * Mathf.Max(1f, cam.aspect));
        }

        static string WhyRender(Camera cam, RenderTexture tex, Vector3 pos, long sig, float now)
        {
            if (!s_haveRender)
            {
                DepthFirst++;
                return "first";
            }
            if (!ReferenceEquals(cam, s_lastCam) || !ReferenceEquals(tex, s_lastTex) || !tex.IsCreated() || cam.cullingMask != s_lastMask
                || cam.orthographic != s_lastOrthographic || cam.orthographicSize != s_lastOrtho || cam.aspect != s_lastAspect || cam.farClipPlane != s_lastFar)
            {
                DepthCameraChanged++;
                return "camera";
            }
            if (pos.x != s_lastPos.x || pos.y != s_lastPos.y || pos.z != s_lastPos.z)
            {
                DepthMoved++;
                return "moved";
            }
            if (now - s_lastRenderAt >= MaxAgeSeconds)
            {
                DepthAge++;
                return "age";
            }
            if (now < s_dirtyUntil)
            {
                DepthOccluder++;
                return "occluder";
            }
            if (sig != s_moverSig)
            {
                DepthMovers++;
                return "movers";
            }
            return null;
        }

        // Order-independent hash of every moving networked object in the footprint on a layer the camera draws:
        // (identity, position to 0.25 m, rotation to 1/64 except for characters, whose turning on the spot follows the
        // mouse; see the header).
        static long MoverSignature(Vector3 center, float half, int mask)
        {
            List<IMonoUpdater> list = ZSyncTransform.Instances;
            float reach = half + ObjectReach;
            long sum = 0;
            int n = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (!(list[i] is ZSyncTransform zs) || zs == null)
                    continue;
                Transform t = zs.transform;
                Vector3 p = t.position;
                if (Mathf.Abs(p.x - center.x) > reach || Mathf.Abs(p.z - center.z) > reach)
                    continue;
                if ((mask & (1 << zs.gameObject.layer)) == 0)
                    continue;
                ulong h = (ulong)RuntimeHelpers.GetHashCode(zs) * 0x9E3779B97F4A7C15UL;
                h = Mix(h, Mathf.RoundToInt(p.x * 4f));
                h = Mix(h, Mathf.RoundToInt(p.y * 4f));
                h = Mix(h, Mathf.RoundToInt(p.z * 4f));
                // Ships, carts, logs, drops, ragdolls: their turn changes what they cover. A character's does not
                // count (an idle player turns to the look yaw with every mouse move).
                if (!zs.TryGetComponent(out Character _))
                {
                    Quaternion q = t.rotation;
                    h = Mix(h, Mathf.RoundToInt(q.x * 64f));
                    h = Mix(h, Mathf.RoundToInt(q.y * 64f));
                    h = Mix(h, Mathf.RoundToInt(q.z * 64f));
                    h = Mix(h, Mathf.RoundToInt(q.w * 64f));
                }
                sum += (long)h;
                n++;
            }
            MoversInFootprint = n;
            return sum ^ ((long)n << 48);
        }

        static ulong Mix(ulong h, int v)
        {
            h ^= (uint)v;
            h *= 0x100000001B3UL;
            return h ^ (h >> 29);
        }

        static void Touch(Vector3 p, float extra)
        {
            float reach = s_half + ObjectReach + extra;
            if (Mathf.Abs(p.x - s_center.x) > reach || Mathf.Abs(p.z - s_center.z) > reach)
                return;
            s_dirtyUntil = Time.unscaledTime + SettleSeconds;
            OccluderEvents++;
        }

        static void AfterViewAwake(ZNetView __instance)
        {
            if (s_footprintValid && DepthSkipFeature.On && __instance != null)
                Touch(__instance.transform.position, 0f);
        }

        static void BeforeViewDestroy(ZNetView __instance)
        {
            if (s_footprintValid && DepthSkipFeature.On)
                Touch(__instance.transform.position, 0f);
        }

        static void AfterPoke(Heightmap __instance, bool paintOnly)
        {
            if (s_footprintValid && DepthSkipFeature.On && !paintOnly)
                Touch(__instance.transform.position, __instance.m_width * __instance.m_scale);
        }

        // Another mod rewrites RenderDepth: skipping it would skip their code too, so every call renders.
        static void CheckForeign()
        {
            string who = ForeignRewriter(Harmony.GetPatchInfo(s_renderDepth));
            bool aside = who != null;
            if (aside == DepthStoodDown)
                return;
            DepthStoodDown = aside;
            ForgetDepth();
            EnginePlugin.Log?.LogWarning(aside
                ? $"DepthCameraSkip: {who} rewrites DepthCamera.RenderDepth; every call renders while it does"
                : "DepthCameraSkip: no other mod rewrites DepthCamera.RenderDepth any more; skipping again");
        }

        // ---- DepthCameraVerify ----

        static RenderTexture s_verifyRt;
        static int s_verifySkips;
        static bool s_verifyBusy, s_verifyFailed;
        static byte[] s_verifyOld, s_verifyNew;
        static int s_verifyW, s_verifyH;

        static void Verify(DepthCamera dc, Camera cam, RenderTexture tex)
        {
            if (s_verifyBusy || ++s_verifySkips % VerifyEverySkips != 0)
                return;
            if (!SystemInfo.supportsAsyncGPUReadback || tex.format == RenderTextureFormat.Depth || tex.format == RenderTextureFormat.Shadowmap
                || !ReferenceEquals(cam.targetTexture, tex))
            {
                VerifyUnsupported++;
                return;
            }
            if (s_verifyRt == null || s_verifyRt.width != tex.width || s_verifyRt.height != tex.height || s_verifyRt.format != tex.format)
            {
                if (s_verifyRt != null)
                {
                    s_verifyRt.Release();
                    UnityEngine.Object.Destroy(s_verifyRt);
                }
                s_verifyRt = new RenderTexture(tex.descriptor) { name = "RealPortalEngineDepthVerify" };
            }
            // The picture RenderDepth would draw now, into the scratch texture.
            float lodBias = QualitySettings.lodBias;
            try
            {
                cam.targetTexture = s_verifyRt;
                QualitySettings.lodBias = 10f;
                cam.RenderWithShader(dc.m_depthShader, "RenderType");
            }
            finally
            {
                QualitySettings.lodBias = lodBias;
                cam.targetTexture = tex;
            }
            s_verifyBusy = true;
            s_verifyFailed = false;
            s_verifyOld = s_verifyNew = null;
            AsyncGPUReadback.Request(tex, 0, OnVerifyOld);
            AsyncGPUReadback.Request(s_verifyRt, 0, OnVerifyNew);
        }

        static void OnVerifyOld(AsyncGPUReadbackRequest r)
        {
            s_verifyOld = Copy(r);
            Compare();
        }

        static void OnVerifyNew(AsyncGPUReadbackRequest r)
        {
            s_verifyNew = Copy(r);
            Compare();
        }

        static byte[] Copy(AsyncGPUReadbackRequest r)
        {
            if (r.hasError)
            {
                s_verifyFailed = true;
                return new byte[0];
            }
            Unity.Collections.NativeArray<byte> data = r.GetData<byte>();
            var copy = new byte[data.Length];
            data.CopyTo(copy);
            s_verifyW = r.width;
            s_verifyH = r.height;
            return copy;
        }

        static void Compare()
        {
            if (s_verifyOld == null || s_verifyNew == null)
                return;
            s_verifyBusy = false;
            byte[] a = s_verifyOld, b = s_verifyNew;
            s_verifyOld = s_verifyNew = null;
            int texels = s_verifyW * s_verifyH;
            if (s_verifyFailed || a.Length != b.Length || a.Length == 0 || texels <= 0 || a.Length % texels != 0)
            {
                VerifyErrors++;
                return;
            }
            int bpp = a.Length / texels;
            long diff = 0;
            for (int t = 0, o = 0; t < texels; t++, o += bpp)
                for (int k = 0; k < bpp; k++)
                    if (a[o + k] != b[o + k])
                    {
                        diff++;
                        break;
                    }
            VerifyChecks++;
            VerifyTexels += texels;
            VerifyDiffTexels += diff;
            if (diff == 0)
                VerifyIdentical++;
            double fraction = (double)diff / texels;
            if (fraction > VerifyMaxFraction)
                VerifyMaxFraction = fraction;
        }

        // ==== 2. ReflectionUpdate ===================================================================================

        static MethodInfo s_timed;
        static AccessTools.FieldRef<ReflectionUpdate, float> s_timerOf;
        static AccessTools.FieldRef<EnvMan, EnvSetup> s_nextEnvOf;
        static readonly Dictionary<string, int> s_reported = new Dictionary<string, int>();

        struct Print
        {
            public bool Valid;
            public string Env;
            public Vector3 Sun, Probe;
            public Color SunColor, Ambient, Fog;
            public float FogDensity, Wet, Aurora;
        }

        static ReflectionUpdate s_lastReflection;
        static int s_reflectionRenders, s_deferStreak;
        // Game time (Time.time), like the game's own m_updateTimer.
        static float s_lastReflectionAt, s_holdUntil;
        static MethodInfo s_reflectionUpdate;
        // Another mod rewrites ReflectionUpdate.Update (seen by the periodic check): every timer call goes straight
        // through, as the game's code had it.
        static bool s_reflectionForeign, s_reflectionCallFound;
        static float s_nextReflectionCheck;
        static int s_reflectionErrorsLogged;
        static Print s_print;
        static readonly Dictionary<ReflectionProbe, ReflectionProbeTimeSlicingMode> s_slicing = new Dictionary<ReflectionProbe, ReflectionProbeTimeSlicingMode>();

        static void InstallReflection(Harmony harmony)
        {
            MethodInfo update = AccessTools.DeclaredMethod(typeof(ReflectionUpdate), "Update");
            MethodInfo updateReflection = AccessTools.DeclaredMethod(typeof(ReflectionUpdate), nameof(ReflectionUpdate.UpdateReflection), Type.EmptyTypes);
            MethodInfo start = AccessTools.DeclaredMethod(typeof(ReflectionUpdate), "Start");
            FieldInfo timer = AccessTools.DeclaredField(typeof(ReflectionUpdate), "m_updateTimer");
            FieldInfo nextEnv = AccessTools.DeclaredField(typeof(EnvMan), "m_nextEnv");
            s_timed = AccessTools.DeclaredMethod(typeof(PeriodicRenderModule), nameof(TimedReflection));
            if (update == null || updateReflection == null || start == null || timer == null || timer.FieldType != typeof(float)
                || nextEnv == null || nextEnv.FieldType != typeof(EnvSetup) || s_timed == null)
                throw new MissingMemberException("ReflectionUpdate.Update/UpdateReflection/Start/m_updateTimer or EnvMan.m_nextEnv not found (game update?)");
            s_timerOf = AccessTools.FieldRefAccess<ReflectionUpdate, float>(timer);
            s_nextEnvOf = AccessTools.FieldRefAccess<EnvMan, EnvSetup>(nextEnv);
            s_reflectionUpdate = update;
            var done = new List<KeyValuePair<MethodBase, MethodInfo>>();
            try
            {
                Patch(harmony, done, updateReflection, nameof(AfterUpdateReflection), false, Priority.Last);
                Patch(harmony, done, start, nameof(AfterReflectionStart), false, Priority.Last);
                MethodInfo transpiler = AccessTools.DeclaredMethod(typeof(PeriodicRenderModule), nameof(UpdateTranspiler));
                done.Add(new KeyValuePair<MethodBase, MethodInfo>(update, transpiler));
                harmony.Patch(update, transpiler: new HarmonyMethod(transpiler) { priority = Priority.Last });
            }
            catch
            {
                Unpatch(harmony, done);
                throw;
            }
            ReflectionInstalled = true;
            ReflectionGateFeature.Switched += _ => s_holdUntil = 0f;
            ReflectionSliceFeature.Switched += on =>
            {
                if (ReflectionUpdate.instance != null)
                    ApplySlicing(ReflectionUpdate.instance, on);
            };
        }

        // Exactly the game's one call of UpdateReflection in Update becomes TimedReflection (same stack: the instance).
        // Harmony reruns this whenever any mod patches Update; the result is logged when it changes. Another mod's
        // transpiler or IL manipulator already on Update (as far as Harmony shows it here) leaves the method as it is:
        // timing logic of theirs around the call would be overwritten by Hold. One added after this ran is caught by the
        // periodic check in TimedReflection instead.
        static IEnumerable<CodeInstruction> UpdateTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var code = new List<CodeInstruction>(instructions);
            int found = 0, at = -1;
            for (int i = 0; i < code.Count; i++)
                if ((code[i].opcode == OpCodes.Call || code[i].opcode == OpCodes.Callvirt) && code[i].operand is MethodInfo m
                    && m.Name == nameof(ReflectionUpdate.UpdateReflection) && m.DeclaringType == typeof(ReflectionUpdate) && m.GetParameters().Length == 0)
                {
                    found++;
                    at = i;
                }
            string who = null;
            try
            {
                who = ForeignRewriter(Harmony.GetPatchInfo(original ?? s_reflectionUpdate));
            }
            catch (Exception e)
            {
                EnginePlugin.Log?.LogWarning($"ReflectionGate: could not read the patches on ReflectionUpdate.Update ({e.Message}); the periodic check decides");
            }
            s_reflectionCallFound = found == 1;
            ReflectionGateActive = found == 1 && who == null;
            int state = who != null ? -1 : found;
            if (!s_reported.TryGetValue("update", out int last) || last != state)
            {
                s_reported["update"] = state;
                if (who != null)
                    EnginePlugin.Log?.LogWarning($"ReflectionGate: {who} rewrites ReflectionUpdate.Update; left as it is, the gate is inert");
                else if (found == 1)
                    EnginePlugin.Log?.LogInfo("ReflectionGate: ReflectionUpdate.Update's timer call goes through the gate");
                else
                    EnginePlugin.Log?.LogWarning($"ReflectionGate: {found} calls of UpdateReflection in ReflectionUpdate.Update where the game has 1 " +
                                                 "(another mod or a game update); left as it is, the gate is inert");
            }
            if (ReflectionGateActive)
            {
                // Same instruction object: its labels and blocks stay.
                code[at].opcode = OpCodes.Call;
                code[at].operand = s_timed;
            }
            return code;
        }

        // The timer's call (the game reset m_updateTimer to 0 right before it). Renders exactly as that call did, or
        // holds the timer; whatever goes wrong in deciding, it renders.
        static void TimedReflection(ReflectionUpdate r)
        {
            bool render = true;
            if (ReflectionGateFeature.On && (object)r != null)
            {
                try
                {
                    render = DecideReflection(r);
                }
                catch (Exception e)
                {
                    // Harmony would carry the exception out of Update after m_updateTimer = 0: no render and no
                    // cross-fade, every 3 s.
                    ReflectionErrors++;
                    s_holdUntil = 0f;
                    render = true;
                    if (s_reflectionErrorsLogged++ < 5)
                        EnginePlugin.Log?.LogError($"ReflectionGate: deciding failed (this call renders as the game's): {e}");
                }
            }
            if (render)
                r.UpdateReflection();
        }

        // True: render now. False: the timer is held (Hold), the call comes again next frame.
        static bool DecideReflection(ReflectionUpdate r)
        {
            float wall = Time.unscaledTime;
            if (wall >= s_nextReflectionCheck)
            {
                s_nextReflectionCheck = wall + PatchCheckSeconds;
                CheckReflectionForeign();
            }
            if (s_reflectionForeign)
                return true;
            // The game is paused: its own timer stands still (m_updateTimer += 0) and renders nothing; only a timer
            // this gate held gets here. Nothing is decided until the game runs.
            if (Time.deltaTime <= 0f)
            {
                ReflectionPausedHolds++;
                Hold(r);
                return false;
            }
            float now = Time.time;
            if (now < s_holdUntil && ReferenceEquals(r, s_lastReflection))
            {
                Hold(r);
                return false;
            }
            ReflectionDue++;
            string why = WhyReflect(r, now);
            if (why != null)
            {
                if (s_deferStreak < MaxDeferStreak && DepthRendersThisFrame())
                {
                    // A frame later: never in the same frame as a depth-picture render.
                    s_deferStreak++;
                    ReflectionDeferred++;
                    s_holdUntil = 0f;
                    Hold(r);
                    return false;
                }
                s_deferStreak = 0;
                return true;
            }
            s_deferStreak = 0;
            ReflectionSkips++;
            s_holdUntil = now + EvalSeconds;
            Hold(r);
            return false;
        }

        // Another mod rewrites Update since the transpiler ran: the swapped call renders every time, as the game's
        // (it stands exactly where the game's call stood).
        static void CheckReflectionForeign()
        {
            string who = ForeignRewriter(Harmony.GetPatchInfo(s_reflectionUpdate));
            bool foreign = who != null;
            if (foreign == s_reflectionForeign)
                return;
            s_reflectionForeign = foreign;
            s_holdUntil = 0f;
            ReflectionGateActive = s_reflectionCallFound && !foreign;
            EnginePlugin.Log?.LogWarning(foreign
                ? $"ReflectionGate: {who} rewrites ReflectionUpdate.Update; every timer call renders while it does"
                : "ReflectionGate: no other mod rewrites ReflectionUpdate.Update any more; gating again");
        }

        // Still due next frame; the cross-fade after this call in Update sees its finished state (f = 1).
        static void Hold(ReflectionUpdate r) => s_timerOf(r) = Mathf.Max(r.m_interval, r.m_transitionDuration) + 0.001f;

        static string WhyReflect(ReflectionUpdate r, float now)
        {
            if (!ReferenceEquals(r, s_lastReflection) || s_reflectionRenders < 2 || !s_print.Valid)
            {
                ReflFirst++;
                return "first";
            }
            if (now - s_lastReflectionAt >= Mathf.Max(ReflectionMaxAge, 3f * r.m_interval))
            {
                ReflAge++;
                return "age";
            }
            EnvMan env = EnvMan.instance;
            if (env == null || ZNet.instance == null || env.m_dirLight == null)
            {
                ReflNoEnv++;
                return "no env";
            }
            if (s_nextEnvOf(env) != null)
            {
                ReflTransition++;
                return "transition";
            }
            Print p = Capture(r);
            Print q = s_print;
            if (!string.Equals(p.Env, q.Env, StringComparison.Ordinal))
            {
                ReflEnv++;
                return "env";
            }
            if (Vector3.Angle(p.Sun, q.Sun) > SunDegrees)
            {
                ReflSun++;
                return "sun";
            }
            if (Differs(p.SunColor, q.SunColor) || Differs(p.Ambient, q.Ambient) || Differs(p.Fog, q.Fog)
                || Mathf.Abs(p.FogDensity - q.FogDensity) > FogTolerance * Mathf.Max(p.FogDensity, q.FogDensity) + 1e-5f)
            {
                ReflLight++;
                return "light";
            }
            if (Mathf.Abs(p.Wet - q.Wet) > WeatherTolerance || Mathf.Abs(p.Aurora - q.Aurora) > WeatherTolerance)
            {
                ReflWeather++;
                return "weather";
            }
            if ((p.Probe - q.Probe).sqrMagnitude > ProbeMoveMeters * ProbeMoveMeters)
            {
                ReflMoved++;
                return "moved";
            }
            return null;
        }

        static bool Differs(Color a, Color b) =>
            Mathf.Abs(a.r - b.r) > ColorTolerance * (1f + Mathf.Max(Mathf.Abs(a.r), Mathf.Abs(b.r)))
            || Mathf.Abs(a.g - b.g) > ColorTolerance * (1f + Mathf.Max(Mathf.Abs(a.g), Mathf.Abs(b.g)))
            || Mathf.Abs(a.b - b.b) > ColorTolerance * (1f + Mathf.Max(Mathf.Abs(a.b), Mathf.Abs(b.b)));

        static Print Capture(ReflectionUpdate r)
        {
            EnvMan env = EnvMan.instance;
            ZNet net = ZNet.instance;
            if (env == null || net == null || env.m_dirLight == null)
                return default;
            Light sun = env.m_dirLight;
            EnvSetup current = env.GetCurrentEnvironment();
            return new Print
            {
                Valid = true,
                Env = current != null ? current.m_name : null,
                Sun = sun.transform.forward,
                SunColor = sun.color * sun.intensity,
                Ambient = RenderSettings.ambientLight,
                Fog = RenderSettings.fogColor,
                FogDensity = RenderSettings.fogDensity,
                Wet = Shader.GetGlobalFloat(Ids.Wet),
                Aurora = Shader.GetGlobalFloat(Ids.Aurora),
                Probe = net.GetReferencePosition() + Vector3.up * r.m_reflectionHeight,
            };
        }

        // Every render, from the timer or from EnvMan: what it was taken with.
        static void AfterUpdateReflection(ReflectionUpdate __instance)
        {
            if (!ReferenceEquals(__instance, s_lastReflection))
            {
                s_lastReflection = __instance;
                s_reflectionRenders = 0;
            }
            s_reflectionRenders++;
            s_lastReflectionAt = Time.time;
            s_holdUntil = 0f;
            ReflectionRenders++;
            try
            {
                s_print = Capture(__instance);
            }
            catch (Exception e)
            {
                s_print = default;
                EnginePlugin.Log?.LogWarning($"ReflectionGate: reading the light failed (renders as the game): {e.Message}");
            }
        }

        // Whether RenderDepth's invoke fires in this frame (Update runs before invokes) and will render.
        static bool DepthRendersThisFrame()
        {
            if (s_lastDepthRenderFrame == Time.frameCount)
                return true;
            if (!s_haveInvoke || Time.time + 1e-4f < s_lastInvokeGame + s_invokeInterval)
                return false;
            if (!DepthInstalled || !DepthSkipFeature.On || DepthStoodDown || !s_haveRender)
                return true;
            float now = Time.unscaledTime;
            if (now < s_dirtyUntil || now - s_lastRenderAt >= MaxAgeSeconds)
                return true;
            Camera main = Utils.GetMainCamera();
            if (main == null)
                return false;
            Vector3 pos = TakenAt(main, s_depthOffset);
            return pos.x != s_lastPos.x || pos.y != s_lastPos.y || pos.z != s_lastPos.z;
        }

        static void AfterReflectionStart(ReflectionUpdate __instance)
        {
            if (ReflectionSliceFeature.On)
                ApplySlicing(__instance, true);
        }

        static void ApplySlicing(ReflectionUpdate r, bool on)
        {
            ApplySlicing(r.m_probe1, on);
            ApplySlicing(r.m_probe2, on);
        }

        static void ApplySlicing(ReflectionProbe probe, bool on)
        {
            if (probe == null)
                return;
            if (on)
            {
                if (!s_slicing.ContainsKey(probe))
                    s_slicing[probe] = probe.timeSlicingMode;
                probe.timeSlicingMode = ReflectionProbeTimeSlicingMode.IndividualFaces;
            }
            else if (s_slicing.TryGetValue(probe, out ReflectionProbeTimeSlicingMode mode))
            {
                probe.timeSlicingMode = mode;
                s_slicing.Remove(probe);
            }
        }

        // ==== 3. The sky camera =====================================================================================

        static Camera s_skyDisabled;
        static bool s_skyLogged;

        static void InstallSky(Harmony harmony)
        {
            MethodInfo awake = AccessTools.DeclaredMethod(typeof(GameCamera), "Awake");
            if (awake == null || AccessTools.DeclaredField(typeof(GameCamera), nameof(GameCamera.m_skyCamera)) == null)
                throw new MissingMemberException("GameCamera.Awake or GameCamera.m_skyCamera not found (game update?)");
            harmony.Patch(awake, postfix: new HarmonyMethod(typeof(PeriodicRenderModule), nameof(AfterGameCameraAwake)) { priority = Priority.Last });
            SkyInstalled = true;
            SkyCameraOffFeature.Switched += on => ApplySky(GameCamera.instance, on);
        }

        static void AfterGameCameraAwake(GameCamera __instance)
        {
            if (SkyCameraOffFeature.On)
                ApplySky(__instance, true);
        }

        static void ApplySky(GameCamera gc, bool off)
        {
            Camera sky = gc != null ? gc.m_skyCamera : null;
            if (off)
            {
                if (sky != null && sky.enabled)
                {
                    if (!s_skyLogged)
                    {
                        s_skyLogged = true;
                        EnginePlugin.Log?.LogInfo("SkyCameraOff: " + SkyCameraDescribe());
                    }
                    sky.enabled = false;
                    s_skyDisabled = sky;
                }
            }
            else if (s_skyDisabled != null)
            {
                // Only the camera this module disabled, and only if nobody enabled it since.
                if (!s_skyDisabled.enabled)
                    s_skyDisabled.enabled = true;
                s_skyDisabled = null;
            }
            SkyCameraDisabled = s_skyDisabled != null && !s_skyDisabled.enabled;
        }

        // Lab: both cameras as they are now (for the screenshot-diff scenario).
        public static string SkyCameraDescribe()
        {
            GameCamera gc = GameCamera.instance;
            Camera main = Utils.GetMainCamera(), sky = gc != null ? gc.m_skyCamera : null;
            string Describe(Camera c) => c == null ? "none" :
                $"'{c.name}' enabled {c.enabled}, depth {c.depth}, clear {c.clearFlags}, mask 0x{c.cullingMask:X8}, near {c.nearClipPlane}, far {c.farClipPlane}, " +
                $"path {c.actualRenderingPath}, HDR {c.allowHDR}, target {(c.targetTexture != null ? c.targetTexture.name : "screen")}";
            return $"sky camera {Describe(sky)}; game camera {Describe(main)}";
        }
    }
}
