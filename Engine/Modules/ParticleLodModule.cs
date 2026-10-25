using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace RealPortal.Engine
{
    // Unity's PreLateUpdate.ParticleSystemBeginUpdateAll (and PostLateUpdate.ParticleSystemEndUpdateAll) walks every
    // live ParticleSystem every frame on the main thread, whether a camera sees it or not: Unity's own cullingMode only
    // decides what the worker threads simulate, not that walk. Measured: ~0.47 us (BeginUpdateAll) + ~0.1 us
    // (EndUpdateAll) per live system per frame; in far areas 8068 systems cost 3.5 + 0.7 ms while facing away, and
    // pausing 7669 of them took BeginUpdateAll to 0.4 ms and the frame from 43.7 to 38.8 ms (Lab report 195653:540,
    // 550, 1110, 1120-1121). A paused system leaves that walk. In a big base the looping ambient systems (fires,
    // torches, braziers, pickables, portals: 3-5 systems per source) are the bulk, and most of them are behind the
    // camera at any moment.
    //
    // ParticleLod pauses LOOPING systems of networked objects that no camera can see, and plays them again as soon as
    // one may:
    //  - which systems: the ParticleSystems of every ZNetView object (ZNetView.Awake: objects made by ZNetScene and
    //    pieces the local player places alike), classified once per prefab. Only systems with main.loop; never a
    //    one-shot (hit sparks, blood, puffs: they end by themselves and stopAction may destroy their object), never one
    //    with a Lights module (its lights reach surfaces the camera does see), a Trigger module (scripts listen), a
    //    stopAction, ParticleMist (AI mist checks read it), and nothing on an object whose root has a Rigidbody,
    //    Character, Projectile or ItemDrop (it moves: a paused sphere would go stale). Sub-emitter systems follow their
    //    parent (paused and played with it, only if they were playing). Non-networked effects (EffectList.Create) are
    //    not registered: they are mostly one-shots, and Real Portal's and Path of Valheiman's postfixes there use them;
    //  - "can see", any of:
    //     - the game camera's frustum of the PREVIOUS frame (the decision is taken in Update, before BeginUpdateAll;
    //       the camera moves in LateUpdate), widened by WidenDegrees and Margin metres so normal turning never shows a
    //       frozen system, unless the system is sub-pixel (ParticleLodSubPixel: its largest particle, at the nearest
    //       point of its sphere, covers less than SubPixelPixels on screen);
    //     - another camera rendered it (see "other cameras" below);
    //     - the hook IsSeenByOtherCamera(bounds) says a camera will;
    //     - its cullingMode is AlwaysSimulate while its prefab's is not (Real Portal's FarParticles switches the
    //       systems near the far end of a window it shows: they are wanted alive). The baseline is the prefab's, so a
    //       switch made before this module got to the object counts too;
    //  - other cameras: a Camera.onPreCull callback records every render of a Game camera other than the game camera:
    //    its culling frustum (cullingMatrix, what Unity culls that very render with) and its mask. That covers Real
    //    Portal's windows wherever they look (PortalRenderer renders each window with Camera.Render and a cullingMatrix
    //    cut to the window's rectangle on screen): the far area behind a window, a window onto a portal in the
    //    player's own area (no RemoteArea, no FarParticles there), and the near side shown through a far window while
    //    CameraThrough has put the game camera on the far side. It covers other mods' cameras too. The renders of each
    //    camera's latest frame count for OtherHoldSeconds, widened like the game camera's (WidenDegrees as an angle
    //    around that camera, plus Margin), only for systems on a layer in that camera's mask. Not viewers: reflection
    //    probe cameras (cameraType Reflection), and the data pictures drawn with a replacement shader about once a
    //    second (a camera with the game's DepthCamera, Real Portal's RealPortalSkyOcclusion copy of it, any orthographic
    //    camera looking straight down): counted, they would play and pause everything under them every second. A
    //    camera is known only once it rendered: when a window comes into view, the paused systems it shows are one
    //    frame old in that one frame and play from the next Update on, from that same state (no jump);
    //  - pausing: a running, emitting system that has not been seen for HoldSeconds is paused
    //    (ParticleSystem.Pause(false)), checked round-robin about every RunningVisitSeconds, at most PauseBudget per
    //    frame. A system that plays but no longer emits (Stop(..., StopEmitting): its last particles fade) is never
    //    paused: playing it again would restart the emission its owner stopped. Its sphere (the renderer's bounds when
    //    paused: exactly the frozen particles that could be drawn, plus how far new ones reach) goes into a 32 m cell;
    //  - playing again: every frame, each cell holding paused systems is tested (frustum, size, other cameras, hook)
    //    and, if it may be seen, each of its systems; a seen one gets Play(false) in this same Update, so it simulates
    //    in this frame's BeginUpdateAll (at most ResumeBudget per frame). ParticleLodCatchUp (lab, off) first advances it
    //    by the time it missed (Simulate, at most CatchUpSeconds): a looping system's frozen state is already a
    //    steady-state snapshot, so this is only a lab arm. A paused system whose object went inactive draws nothing: it
    //    leaves the per-frame test and is looked at about once a second until it is active again;
    //  - only what this module paused is ever played again, and only while it is still paused: a system someone else
    //    paused (Real Portal's RemoteAreas sleep pauses what plays in a far area nobody watches) is left alone, and Real
    //    Portal never plays ours (isPlaying is false while paused, so its sleep does not list them). One someone played
    //    or stopped since is simply running again for us, and the sub-emitters this module paused with it are played as
    //    Resume would;
    //  - no game camera for NoViewFrames frames (a loading screen, a cinematic with its own camera): every paused system
    //    is played again, nothing is paused, and dead entries still go. On a dedicated server (no graphics device, or
    //    ZNet.IsDedicated) nothing is registered.
    //
    // What is not exact (this is a visual LOD; nothing here is replicated, the ParticleSystem RNG is not
    // UnityEngine.Random, and no ZDO, RPC or gameplay state is read from particles except by the excluded kinds):
    //  - a snap-turn larger than the widening, a window coming into view, or a frame where more than ResumeBudget
    //    systems come into view, shows some systems frozen for one frame (LateResumes counts game-camera ones that were
    //    already inside the unwidened view when played, and were paused for being out of view, not for their size);
    //  - a sub-pixel system on screen is frozen, not hidden; its particles keep being drawn as they were;
    //  - a paused system resumes from its frozen state (not from where it would be now), unless ParticleLodCatchUp;
    //  - decal and collision side effects of paused systems stop (ParticleDecal: cosmetic).
    // Off: every system this module paused and that is still paused is played again at once, the registry is dropped,
    // and every hook (the camera callback included) returns after one flag test: the game runs as without the module.
    [EngineModule]
    public static class ParticleLodModule
    {
        public static readonly Feature Feature = Features.Register("ParticleLod", false,
            "Looping particle systems (fires, torches, pickables, portals) that no camera can see are paused and played again " +
            "as soon as one may (the game camera's widened view, any other camera's render such as a Real Portal window, or " +
            "IsSeenByOtherCamera). Saves ~0.5 us per hidden system per frame. Off: nothing is paused.");
        public static readonly Feature SubPixelFeature = Features.Register("ParticleLodSubPixel", false,
            "With ParticleLod: a looping system whose largest particle covers less than a pixel counts as unseen even inside " +
            "the view (frozen, still drawn). Off: only the frustum counts.");
        public static readonly Feature CatchUpFeature = Features.Register("ParticleLodCatchUp", false,
            "Lab: a system ParticleLod plays again is first advanced by the time it missed (Simulate, at most 1 s; main-thread cost).");

        // ---- the window hook ------------------------------------------------------------------------------------

        // Optional (Real Portal or any mod with extra cameras): true when the world-space bounds may be seen by a camera
        // other than the game camera this frame or the next. Cameras that rendered are already counted (see "other
        // cameras" above); this adds what a camera WILL show before it has rendered it (a window about to come into
        // view). Main thread. Called for running systems no camera saw (each about every RunningVisitSeconds), and every
        // frame for each 32 m cell of paused systems nothing else saw, then for that cell's systems when the cell is
        // seen: keep it cheap (a few frustum tests). An exception counts as "seen" (HookErrors) and never stops the module.
        public static Func<Bounds, bool> IsSeenByOtherCamera;

        // ---- tuning -----------------------------------------------------------------------------------------------

        const float HoldSeconds = 0.5f;             // unseen this long before a pause
        const float RunningVisitSeconds = 0.25f;    // each running system is looked at about this often
        const float PausedVisitSeconds = 1f;        // each paused one (death, someone else's Play, movement)
        const float MaxVisitDt = 0.1f;              // a long frame (a hitch, a load) does not pile up visits
        const float WidenDegrees = 20f;             // added to the view's vertical and horizontal angle (10 each side)
        const float SinHalfWiden = 0.17364818f;     // sin(WidenDegrees / 2): the same widening for other cameras
        const float Margin = 4f;                    // metres added to every sphere and cell
        const float SubPixelPixels = 1f;
        const float MoveMeters = 0.5f, StillSeconds = 2f;
        const int PauseBudget = 256, ResumeBudget = 1024;
        const float CellSize = 32f;
        const double ScanBudgetMs = 0.5;
        const float CatchUpSeconds = 1f;
        const float MaxReach = 40f;
        const int MaxFollowers = 64;
        const float OtherHoldSeconds = 0.25f;       // a camera's latest renders count this long (>= RunningVisitSeconds)
        const int MaxOtherCameras = 32, MaxViewsPerCamera = 16;
        const int NoViewFrames = 3;
        const int SweepBudget = 256;
        const int GiveUpErrors = 200;               // step failures since switched on
        const string RealPortalSkyCamera = "RealPortalSkyOcclusion";

        // ---- lab counters (read by reflection; cumulative ones cleared by ResetCounters) --------------------------

        public static int Registered, Running, Paused, Cells, PendingObjects, PrefabsKnown, OtherCameras, OtherViews;
        public static bool GaveUp, Headless;
        public static long Pauses, PausesSubPixel, Resumes, LateResumes, ResumesDeferred, ResumeFailures, NoViewResumes;
        public static long Movers, Dropped, ForeignSeen, KeptAlwaysSimulate, KeptStopping, SeenByOther, HookCalls, HookErrors, CellTests;
        public static long ScannedObjects, RegisteredTotal, InstanceMismatches, Errors, OtherRenders, OtherOverflows;
        public static int MaxResumesPerFrame, MaxPausesPerFrame;
        public static long Steps;
        public static double TotalStepMs, MaxStepMs;
        // Candidates registered, by their prefab's cullingMode (Automatic, PauseAndCatchup, Pause, AlwaysSimulate).
        public static readonly long[] ByCullingMode = new long[4];
        // Systems not taken, by reason, counted per prefab classification.
        public static long ExcludedOneShot, ExcludedLights, ExcludedTrigger, ExcludedStopAction, ExcludedMist, ExcludedMovingRoot, Followers;
        // Every camera seen rendering besides the game camera: "name" (viewer) or "name(not a viewer)".
        public static string OtherCameraNames = "";

        public static void ResetCounters()
        {
            Pauses = PausesSubPixel = Resumes = LateResumes = ResumesDeferred = ResumeFailures = NoViewResumes = 0;
            Movers = Dropped = ForeignSeen = KeptAlwaysSimulate = KeptStopping = SeenByOther = HookCalls = HookErrors = CellTests = 0;
            ScannedObjects = RegisteredTotal = InstanceMismatches = Errors = OtherRenders = OtherOverflows = 0;
            MaxResumesPerFrame = MaxPausesPerFrame = 0;
            Steps = 0;
            TotalStepMs = MaxStepMs = 0;
        }

        public static string Report() =>
            $"particle lod {(!s_installed ? "not installed" : !Feature.On ? "off" : GaveUp ? "gave up (errors)" : Headless ? "inert (no graphics)" : "on")}: " +
            $"{Registered} systems ({Running} running, {Paused} paused in {Cells} cells), pending objects {PendingObjects}, prefabs {PrefabsKnown} | " +
            $"pauses {Pauses} (sub-pixel {PausesSubPixel}, max {MaxPausesPerFrame}/frame), resumes {Resumes} (late {LateResumes}, deferred {ResumesDeferred}, " +
            $"failed {ResumeFailures}, no view {NoViewResumes}, max {MaxResumesPerFrame}/frame) | movers {Movers}, dropped {Dropped}, " +
            $"foreign-paused seen {ForeignSeen}, kept (AlwaysSimulate) {KeptAlwaysSimulate}, kept (stopping) {KeptStopping} | other cameras {OtherCameras} " +
            $"({OtherViews} views, renders {OtherRenders}, overflows {OtherOverflows}, seen {SeenByOther}; {OtherCameraNames}), hook {(IsSeenByOtherCamera != null ? "set" : "none")} " +
            $"(calls {HookCalls}, errors {HookErrors}), cell tests {CellTests} | " +
            $"culling modes auto/catchup/pause/always {ByCullingMode[0]}/{ByCullingMode[1]}/{ByCullingMode[2]}/{ByCullingMode[3]}; " +
            $"excluded one-shot {ExcludedOneShot}, lights {ExcludedLights}, trigger {ExcludedTrigger}, stopAction {ExcludedStopAction}, " +
            $"mist {ExcludedMist}, moving root {ExcludedMovingRoot}; followers {Followers} | scanned {ScannedObjects}, mismatches {InstanceMismatches}, " +
            $"errors {Errors} (since on {s_stepErrors}) | step {(Steps > 0 ? TotalStepMs / Steps : 0):F3} ms avg, max {MaxStepMs:F2} ms";

        // ---- state ------------------------------------------------------------------------------------------------

        // What a prefab's ParticleSystems are, in GetComponentsInChildren(true) order (an instance has the same order).
        // Sizes and reaches are relative to the root (its own scale left out): the instance's scale is applied once, in
        // Register.
        sealed class PrefabInfo
        {
            public int Count;
            public int[] Candidates;
            public float[] Size;            // largest particle at root scale 1; +inf: never sub-pixel
            public float[] Reach;           // how far from the emitter particles get at root scale 1
            public bool[] Hierarchy;        // scalingMode Hierarchy: particles shrink and grow with the root's scale
            public ParticleSystemCullingMode[] Culling;     // as the prefab has it (the keep-alive baseline)
            public int[][] FollowersOf;     // per candidate: indices of its sub-emitter systems (or null)
        }

        sealed class Entry
        {
            public ParticleSystem Ps;
            public ParticleSystemRenderer Renderer;
            public Transform Tr;
            public ParticleSystem[] Followers;
            public ulong FollowersPaused;
            public ParticleSystemCullingMode Culling;
            public Vector3 Center, LastPos;
            public float Radius, Size, Reach;
            public float LastSeen, StillSince, PausedAt, PausedAtGame;
            public int LayerBit;
            public bool IsPaused;           // paused by this module
            public bool SubPixelPause;      // paused only for its size on screen (inside the view)
            public bool Inactive;           // paused, and its object went inactive: out of the cells
            public int Slot;                // index in s_running or s_paused
            public Cell Cell;
            public int CellSlot;
        }

        sealed class Cell
        {
            public long Key;
            public int X, Z;
            public int Slot;                // index in s_cells
            public readonly List<Entry> Members = new List<Entry>();
            public float MinY, MaxY, MaxRadius, MaxSize;
            public int Layers;
            public bool Dirty;
        }

        // One render of another camera: the planes it culled with.
        sealed class OtherView
        {
            public readonly float[] P = new float[24];
            public Vector3 Pos;
            public int Mask;
            public bool Ortho;
        }

        sealed class OtherCamera
        {
            public Camera Cam;
            public string Name;
            public bool Viewer;             // false: a reflection or data camera, never counted
            public int Frame = -1;          // the frame of Views
            public float At;
            public int Count;
            public bool Overflow;           // more renders in that frame than kept: everything counts as seen
            public readonly OtherView[] Views = new OtherView[MaxViewsPerCamera];
        }

        sealed class ByReference<T> : IEqualityComparer<T> where T : class
        {
            public bool Equals(T a, T b) => ReferenceEquals(a, b);
            public int GetHashCode(T o) => RuntimeHelpers.GetHashCode(o);
        }

        static bool s_installed;
        static AccessTools.FieldRef<ZNetScene, Dictionary<ZDO, ZNetView>> s_instancesOf;
        static readonly Dictionary<int, PrefabInfo> s_prefabs = new Dictionary<int, PrefabInfo>();
        static readonly PrefabInfo s_none = new PrefabInfo { Count = -1, Candidates = new int[0] };
        static Dictionary<ParticleSystem, Entry> s_byPs = new Dictionary<ParticleSystem, Entry>(new ByReference<ParticleSystem>());
        static List<Entry> s_running = new List<Entry>(), s_paused = new List<Entry>();
        static readonly List<Cell> s_cells = new List<Cell>();
        static readonly Dictionary<long, Cell> s_cellByKey = new Dictionary<long, Cell>();
        static List<ZNetView> s_pending = new List<ZNetView>();
        static int s_pendingAt, s_runCursor, s_pausedCursor;
        static float s_runCarry, s_pausedCarry;
        static readonly List<ParticleSystem> s_tmp = new List<ParticleSystem>();
        static int s_frame = -1;
        static bool s_rebuild;
        static int s_errorsLogged, s_stepErrors, s_noView;
        static int s_headless;      // 0 unknown, 1 headless, 2 has a graphics device (ZNet not looked at yet), 3 not headless
        static int s_layerUnion;    // layers of every system registered so far
        static readonly Type s_mistType = typeof(ParticleMist);

        // This frame's view (from the game camera as it stood after last frame's LateUpdate).
        static bool s_viewValid;
        static readonly Plane[] s_planes = new Plane[6];
        static readonly float[] s_wide = new float[24], s_tight = new float[24];
        static Vector3 s_camPos;
        static float s_theta;           // radians per SubPixelPixels at screen centre; 0: no size test

        // Other cameras (recorded during rendering, read in the next Update).
        static bool s_subscribed;
        static readonly List<OtherCamera> s_otherCams = new List<OtherCamera>();
        static readonly List<OtherView> s_live = new List<OtherView>();
        static readonly Plane[] s_otherPlanes = new Plane[6];
        static bool s_liveAll;
        static float s_tooManyAt = -100f;
        static bool s_namesDirty;

        // ---- install ----------------------------------------------------------------------------------------------

        static void Install(Harmony harmony)
        {
            var awake = AccessTools.DeclaredMethod(typeof(ZNetView), "Awake");
            var update = AccessTools.DeclaredMethod(typeof(MonoUpdaters), "Update");
            var sceneDestroy = AccessTools.DeclaredMethod(typeof(ZNetScene), "OnDestroy");
            var instances = AccessTools.DeclaredField(typeof(ZNetScene), "m_instances");
            if (awake == null || update == null || sceneDestroy == null || instances == null || instances.FieldType != typeof(Dictionary<ZDO, ZNetView>))
                throw new MissingMemberException("ParticleLod: ZNetView.Awake, MonoUpdaters.Update, ZNetScene.OnDestroy or ZNetScene.m_instances not found (game update?)");
            s_instancesOf = AccessTools.FieldRefAccess<ZNetScene, Dictionary<ZDO, ZNetView>>(instances);
            // Observing postfixes, last, so they see what other mods' postfixes left; the step runs after the game's
            // own MonoUpdaters.Update (still in the Update phase, before PreLateUpdate.ParticleSystemBeginUpdateAll).
            // All or nothing: the hooks read s_installed, and what went in is taken back when a later patch fails (a
            // registration hook without the step would only queue objects nobody reads).
            var done = new List<KeyValuePair<MethodBase, MethodInfo>>();
            try
            {
                Postfix(harmony, done, update, nameof(AfterMonoUpdatersUpdate));
                Postfix(harmony, done, sceneDestroy, nameof(AfterSceneDestroy));
                Postfix(harmony, done, awake, nameof(AfterViewAwake));
            }
            catch
            {
                foreach (KeyValuePair<MethodBase, MethodInfo> p in done)
                {
                    try
                    {
                        harmony.Unpatch(p.Key, p.Value);
                    }
                    catch (Exception e)
                    {
                        EnginePlugin.Log?.LogWarning($"ParticleLod: could not take back {p.Value.Name} on {p.Key.Name} (left inert): {e.Message}");
                    }
                }
                throw;
            }
            s_installed = true;
            // Installed at plugin start, before any ZNetView exists: ZNetView.Awake sees every object from here on, so
            // only a later switch-on has to collect what woke while it was off. The camera callback is added by the
            // first step (Unity's side is running by then).
            Feature.Switched += OnSwitched;
        }

        static void Postfix(Harmony harmony, List<KeyValuePair<MethodBase, MethodInfo>> done, MethodBase original, string name)
        {
            MethodInfo method = AccessTools.DeclaredMethod(typeof(ParticleLodModule), name);
            // Listed before the attempt: a patch that failed to build stays registered with Harmony.
            done.Add(new KeyValuePair<MethodBase, MethodInfo>(original, method));
            harmony.Patch(original, postfix: new HarmonyMethod(method) { priority = Priority.Last });
        }

        static void OnSwitched(bool on)
        {
            if (!s_installed)
                return;
            if (on)
            {
                // A fresh start: the give-up count is per switch-on (Errors stays the cumulative lab counter).
                GaveUp = false;
                s_errorsLogged = 0;
                s_stepErrors = 0;
                s_noView = 0;
                s_rebuild = true;
            }
            else
                Release(true);
        }

        // ---- hooks ------------------------------------------------------------------------------------------------

        static void AfterViewAwake(ZNetView __instance)
        {
            // A dedicated server queues nothing (s_headless is decided by the first step).
            if (!Feature.On || GaveUp || !s_installed || s_headless == 1)
                return;
            s_pending.Add(__instance);
        }

        // The game scene goes (logout): every object goes with it, nothing is played again.
        static void AfterSceneDestroy()
        {
            Release(false);
            s_prefabs.Clear();
            PrefabsKnown = 0;
        }

        static void AfterMonoUpdatersUpdate()
        {
            if (!Feature.On || GaveUp || !s_installed)
                return;
            // Once per frame, however many MonoUpdaters there are (the game has one).
            int frame = Time.frameCount;
            if (frame == s_frame)
                return;
            s_frame = frame;
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                Step();
            }
            catch (Exception e)
            {
                Errors++;
                s_stepErrors++;
                if (s_errorsLogged++ < 10)
                    EnginePlugin.Log?.LogError($"ParticleLod: step failed: {e}");
                if (s_stepErrors > GiveUpErrors)
                {
                    GaveUp = true;
                    EnginePlugin.Log?.LogError("ParticleLod: too many errors, every paused system is played again and the module stops until switched off and on");
                    Release(true);
                }
            }
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            Steps++;
            TotalStepMs += ms;
            if (ms > MaxStepMs)
                MaxStepMs = ms;
        }

        // Every camera render (Camera.onPreCull, during rendering): another camera's view is recorded.
        static void OnAnyPreCull(Camera cam)
        {
            if (!Feature.On || GaveUp || !s_installed || (object)cam == null)
                return;
            try
            {
                Record(cam);
            }
            catch (Exception e)
            {
                Errors++;
                if (s_errorsLogged++ < 10)
                    EnginePlugin.Log?.LogError($"ParticleLod: recording a camera render failed: {e}");
            }
        }

        // ---- the step ---------------------------------------------------------------------------------------------

        static void Step()
        {
            if (IsHeadless())
            {
                // Nothing is drawn: nothing to decide, and nothing is kept.
                if (s_byPs.Count > 0 || s_pending.Count > 0)
                    Release(false);
                Gauges();
                return;
            }
            if (!s_subscribed)
            {
                s_subscribed = true;
                Camera.onPreCull += OnAnyPreCull;
            }
            if (s_rebuild)
            {
                s_rebuild = false;
                Rebuild();
            }
            float now = Time.unscaledTime;
            Scan();
            s_viewValid = PrepareView();
            if (!s_viewValid)
            {
                // No game camera (a loading screen, a cinematic: CinematicsManager disables the game camera and renders
                // its own): nothing is paused, and after a few frames everything paused is played again (the camera
                // that renders now may show it). Destroyed systems still leave the registry.
                if (++s_noView >= NoViewFrames)
                    PlayBackPaused(now);
                SweepDead();
                Gauges();
                return;
            }
            s_noView = 0;
            PrepareOthers(now);
            int resumed = ResumeSeen(now);
            VisitPaused(now);
            int paused = VisitRunning(now);
            if (resumed > MaxResumesPerFrame)
                MaxResumesPerFrame = resumed;
            if (paused > MaxPausesPerFrame)
                MaxPausesPerFrame = paused;
            Gauges();
        }

        // A dedicated server (or any process without a graphics device): no camera ever renders a particle.
        static bool IsHeadless()
        {
            if (s_headless == 0)
                s_headless = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null ? 1 : 2;
            if (s_headless == 2)
            {
                ZNet net = ZNet.instance;
                if (net != null)
                    s_headless = net.IsDedicated() ? 1 : 3;
            }
            Headless = s_headless == 1;
            return Headless;
        }

        static void Gauges()
        {
            Registered = s_byPs.Count;
            Running = s_running.Count;
            Paused = s_paused.Count;
            Cells = s_cells.Count;
            PendingObjects = s_pending.Count - s_pendingAt;
            PrefabsKnown = s_prefabs.Count;
            if (s_namesDirty)
            {
                s_namesDirty = false;
                var names = new List<string>();
                foreach (OtherCamera oc in s_otherCams)
                    names.Add(oc.Viewer ? oc.Name : oc.Name + "(not a viewer)");
                OtherCameraNames = string.Join(", ", names);
            }
        }

        // Switched on (or at start): every networked object already in the scene is looked at, over the next frames.
        static void Rebuild()
        {
            ZNetScene scene = ZNetScene.instance;
            if (scene == null)
                return;
            foreach (ZNetView view in s_instancesOf(scene).Values)
                if ((object)view != null)
                    s_pending.Add(view);
        }

        // Objects that woke since: their looping systems join, within ScanBudgetMs per frame. The clock is read after
        // every classification (it marshals every module's curves: hundreds of microseconds for a big prefab) and every
        // 16 objects otherwise.
        static void Scan()
        {
            if (s_pendingAt >= s_pending.Count)
            {
                if (s_pending.Count > 0)
                {
                    s_pending.Clear();
                    s_pendingAt = 0;
                    // After a far area or a world loaded (tens of thousands), the list's peak is not kept.
                    if (s_pending.Capacity > 4096)
                        s_pending = new List<ZNetView>();
                }
                return;
            }
            long until = Stopwatch.GetTimestamp() + (long)(ScanBudgetMs * Stopwatch.Frequency / 1000.0);
            ZNetScene scene = ZNetScene.instance;
            while (s_pendingAt < s_pending.Count)
            {
                ZNetView view = s_pending[s_pendingAt];
                s_pending[s_pendingAt++] = null;
                if (view == null || scene == null)
                    continue;
                ZDO zdo = view.GetZDO();
                if (zdo == null)
                    continue;
                ScannedObjects++;
                PrefabInfo info = InfoFor(scene, zdo.GetPrefab(), out bool classified);
                if (info.Candidates.Length > 0 && Register(view, info))
                    classified = true;
                if ((classified || (s_pendingAt & 15) == 0) && Stopwatch.GetTimestamp() > until)
                    break;
            }
        }

        static PrefabInfo InfoFor(ZNetScene scene, int hash, out bool classified)
        {
            classified = false;
            if (s_prefabs.TryGetValue(hash, out PrefabInfo info))
                return info;
            GameObject prefab = scene.GetPrefab(hash);
            info = prefab != null ? Classify(prefab, true) : s_none;
            classified = prefab != null;
            s_prefabs[hash] = info;
            return info;
        }

        // Returns whether the object had to be classified on its own (the scan budget is read then).
        static bool Register(ZNetView view, PrefabInfo info)
        {
            bool classified = false;
            view.GetComponentsInChildren(true, s_tmp);
            try
            {
                if (s_tmp.Count != info.Count)
                {
                    // Something added or removed systems at runtime (an item on a stand, a mod): classified as it is,
                    // for this object only (its cullingMode baseline is then the instance's own).
                    InstanceMismatches++;
                    classified = true;
                    info = Classify(view.gameObject, false);
                    view.GetComponentsInChildren(true, s_tmp);
                    if (s_tmp.Count != info.Count)
                        return true;
                }
                Vector3 s = view.transform.lossyScale;
                float scale = Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));
                if (scale <= 0f || float.IsNaN(scale) || float.IsInfinity(scale))
                    scale = 1f;
                float now = Time.unscaledTime;
                for (int c = 0; c < info.Candidates.Length; c++)
                {
                    ParticleSystem ps = s_tmp[info.Candidates[c]];
                    if (ps == null || s_byPs.ContainsKey(ps))
                        continue;
                    // The root's scale, once (Classify left it out). Only Hierarchy scaling shrinks particles with it;
                    // otherwise they are never assumed smaller than at scale 1.
                    float k = info.Hierarchy[c] ? scale : Mathf.Max(1f, scale);
                    var e = new Entry
                    {
                        Ps = ps,
                        Tr = ps.transform,
                        Size = info.Size[c] * k,
                        Reach = Mathf.Min(info.Reach[c] * k, MaxReach),
                        Culling = info.Culling[c],
                        LayerBit = 1 << ps.gameObject.layer,
                        LastSeen = now,
                        StillSince = now - StillSeconds,
                    };
                    ps.TryGetComponent(out e.Renderer);
                    ByCullingMode[Mathf.Clamp((int)e.Culling, 0, 3)]++;
                    s_layerUnion |= e.LayerBit;
                    int[] followers = info.FollowersOf[c];
                    if (followers != null)
                    {
                        e.Followers = new ParticleSystem[Math.Min(followers.Length, MaxFollowers)];
                        for (int f = 0; f < e.Followers.Length; f++)
                            e.Followers[f] = s_tmp[followers[f]];
                    }
                    e.LastPos = e.Tr.position;
                    e.Center = e.LastPos;
                    e.Radius = e.Reach;
                    s_byPs.Add(ps, e);
                    e.Slot = s_running.Count;
                    s_running.Add(e);
                    RegisteredTotal++;
                }
            }
            finally
            {
                s_tmp.Clear();
            }
            return classified;
        }

        // ---- classification ---------------------------------------------------------------------------------------

        // A prefab (or, for a mismatched instance, the object itself). Reads module settings, which marshals curves:
        // once per prefab, inside the scan budget.
        static PrefabInfo Classify(GameObject root, bool count)
        {
            var systems = new List<ParticleSystem>();
            root.GetComponentsInChildren(true, systems);
            var info = new PrefabInfo { Count = systems.Count };
            if (systems.Count == 0 || root.GetComponent<Rigidbody>() != null || root.GetComponent<Character>() != null
                || root.GetComponent<Projectile>() != null || root.GetComponent<ItemDrop>() != null)
            {
                if (systems.Count > 0 && count)
                    ExcludedMovingRoot += systems.Count;
                info.Candidates = new int[0];
                return info;
            }
            // Scales relative to the root: Register applies the instance's own scale (the root's, with whatever it is
            // parented to) once. A prefab asset's root keeps its asset scale in lossyScale, and a live instance's root has
            // the instance scale in it: both are divided out here.
            Vector3 rs = root.transform.lossyScale;
            float rootScale = Mathf.Max(Mathf.Abs(rs.x), Mathf.Max(Mathf.Abs(rs.y), Mathf.Abs(rs.z)));
            if (rootScale <= 0f || float.IsNaN(rootScale) || float.IsInfinity(rootScale))
                rootScale = 1f;
            // Sub-emitter systems belong to their parent.
            var followerOf = new Dictionary<ParticleSystem, int>(new ByReference<ParticleSystem>());
            for (int i = 0; i < systems.Count; i++)
            {
                ParticleSystem.SubEmittersModule sub = systems[i].subEmitters;
                if (!sub.enabled)
                    continue;
                for (int k = 0; k < sub.subEmittersCount; k++)
                {
                    ParticleSystem child = sub.GetSubEmitterSystem(k);
                    if (child != null && !ReferenceEquals(child, systems[i]) && !followerOf.ContainsKey(child))
                        followerOf.Add(child, i);
                }
            }
            var candidates = new List<int>();
            var sizes = new List<float>();
            var reaches = new List<float>();
            var hierarchy = new List<bool>();
            var culling = new List<ParticleSystemCullingMode>();
            var followers = new List<int[]>();
            for (int i = 0; i < systems.Count; i++)
            {
                ParticleSystem ps = systems[i];
                if (followerOf.ContainsKey(ps))
                {
                    if (count)
                        Followers++;
                    continue;
                }
                ParticleSystem.MainModule main = ps.main;
                string why = null;
                if (!main.loop)
                    why = "oneshot";
                else if (ps.lights.enabled)
                    why = "lights";
                else if (ps.trigger.enabled)
                    why = "trigger";
                else if (main.stopAction != ParticleSystemStopAction.None)
                    why = "stop";
                else if (ps.GetComponent(s_mistType) != null)
                    why = "mist";
                if (why != null)
                {
                    if (count)
                        CountExcluded(why);
                    continue;
                }
                candidates.Add(i);
                ps.TryGetComponent(out ParticleSystemRenderer renderer);
                Vector3 ls = ps.transform.lossyScale;
                float local = Mathf.Max(Mathf.Abs(ls.x), Mathf.Max(Mathf.Abs(ls.y), Mathf.Abs(ls.z))) / rootScale;
                if (local <= 0f || float.IsNaN(local) || float.IsInfinity(local))
                    local = 1f;
                // Particles scale with the hierarchy only in Hierarchy mode; otherwise never assume they shrink.
                bool scales = main.scalingMode == ParticleSystemScalingMode.Hierarchy;
                float k = scales ? local : Mathf.Max(1f, local);
                sizes.Add(FeatureSize(ps, main, renderer) * k);
                reaches.Add(ReachOf(ps, main) * k);
                hierarchy.Add(scales);
                culling.Add(main.cullingMode);
                List<int> mine = null;
                foreach (KeyValuePair<ParticleSystem, int> kv in followerOf)
                    if (kv.Value == i)
                    {
                        int at = systems.IndexOf(kv.Key);
                        if (at >= 0)
                            (mine ??= new List<int>()).Add(at);
                    }
                followers.Add(mine?.ToArray());
            }
            info.Candidates = candidates.ToArray();
            info.Size = sizes.ToArray();
            info.Reach = reaches.ToArray();
            info.Hierarchy = hierarchy.ToArray();
            info.Culling = culling.ToArray();
            info.FollowersOf = followers.ToArray();
            return info;
        }

        static void CountExcluded(string why)
        {
            switch (why)
            {
                case "oneshot": ExcludedOneShot++; break;
                case "lights": ExcludedLights++; break;
                case "trigger": ExcludedTrigger++; break;
                case "stop": ExcludedStopAction++; break;
                default: ExcludedMist++; break;
            }
        }

        // The largest particle in world units at scale 1, for the sub-pixel test. +inf when it cannot be bounded
        // (trails, a minimum screen size, particles sized by script): such a system is never "too small".
        static float FeatureSize(ParticleSystem ps, ParticleSystem.MainModule main, ParticleSystemRenderer renderer)
        {
            if (ps.trails.enabled || renderer == null || renderer.minParticleSize > 0f)
                return float.PositiveInfinity;
            float size = main.startSize3D
                ? Mathf.Max(MaxOf(main.startSizeX), Mathf.Max(MaxOf(main.startSizeY), MaxOf(main.startSizeZ)))
                : MaxOf(main.startSize);
            ParticleSystem.SizeOverLifetimeModule sol = ps.sizeOverLifetime;
            if (sol.enabled)
                size *= sol.separateAxes ? Mathf.Max(MaxOf(sol.x), Mathf.Max(MaxOf(sol.y), MaxOf(sol.z))) : MaxOf(sol.size);
            switch (renderer.renderMode)
            {
                case ParticleSystemRenderMode.Stretch:
                    size = size * Mathf.Max(1f, Mathf.Abs(renderer.lengthScale)) + Mathf.Abs(renderer.velocityScale) * MaxOf(main.startSpeed);
                    break;
                case ParticleSystemRenderMode.Mesh:
                    Mesh mesh = renderer.mesh;
                    if (mesh == null)
                        return float.PositiveInfinity;
                    Vector3 b = mesh.bounds.size;
                    size *= Mathf.Max(1f, Mathf.Max(b.x, Mathf.Max(b.y, b.z)));
                    break;
            }
            return size > 0f && !float.IsNaN(size) ? size : float.PositiveInfinity;
        }

        // How far from the emitter its particles can get at scale 1, for the sphere while it has none.
        static float ReachOf(ParticleSystem ps, ParticleSystem.MainModule main)
        {
            float life = MaxOf(main.startLifetime);
            float reach = MaxOf(main.startSpeed) * life + 0.5f * 9.81f * MaxOf(main.gravityModifier) * life * life + MaxOf(main.startSize);
            ParticleSystem.ShapeModule shape = ps.shape;
            if (shape.enabled)
            {
                Vector3 sc = shape.scale;
                reach += Mathf.Max(shape.radius, Mathf.Max(Mathf.Abs(sc.x), Mathf.Max(Mathf.Abs(sc.y), Mathf.Abs(sc.z))));
            }
            if (float.IsNaN(reach) || reach < 1f)
                reach = 1f;
            return Mathf.Min(reach, MaxReach);
        }

        static float MaxOf(ParticleSystem.MinMaxCurve c)
        {
            switch (c.mode)
            {
                case ParticleSystemCurveMode.Constant:
                    return Mathf.Abs(c.constant);
                case ParticleSystemCurveMode.TwoConstants:
                    return Mathf.Max(Mathf.Abs(c.constantMin), Mathf.Abs(c.constantMax));
                case ParticleSystemCurveMode.Curve:
                    // Tangents can overshoot the keys a little.
                    return 1.25f * Mathf.Abs(c.curveMultiplier) * MaxKey(c.curve);
                default:
                    return 1.25f * Mathf.Abs(c.curveMultiplier) * Mathf.Max(MaxKey(c.curveMin), MaxKey(c.curveMax));
            }
        }

        static float MaxKey(AnimationCurve curve)
        {
            if (curve == null || curve.length == 0)
                return 1f;
            float m = 0f;
            for (int i = 0; i < curve.length; i++)
                m = Mathf.Max(m, Mathf.Abs(curve[i].value));
            return m;
        }

        // ---- the view ---------------------------------------------------------------------------------------------

        static bool PrepareView()
        {
            Camera cam = Utils.GetMainCamera();
            if (cam == null || ZNet.instance == null)
                return false;
            s_camPos = cam.transform.position;
            Matrix4x4 view = cam.worldToCameraMatrix;
            GeometryUtility.CalculateFrustumPlanes(cam.projectionMatrix * view, s_planes);
            CopyPlanes(s_planes, s_tight);
            if (cam.orthographic)
                CopyPlanes(s_planes, s_wide);
            else
            {
                float halfV = cam.fieldOfView * 0.5f * Mathf.Deg2Rad;
                float halfH = Mathf.Atan(Mathf.Tan(halfV) * cam.aspect);
                float widen = WidenDegrees * 0.5f * Mathf.Deg2Rad, cap = 85f * Mathf.Deg2Rad;
                float v2 = Mathf.Min(halfV + widen, cap), h2 = Mathf.Min(halfH + widen, cap);
                Matrix4x4 wide = Matrix4x4.Perspective(2f * v2 * Mathf.Rad2Deg, Mathf.Tan(h2) / Mathf.Tan(v2), cam.nearClipPlane, cam.farClipPlane);
                GeometryUtility.CalculateFrustumPlanes(wide * view, s_planes);
                CopyPlanes(s_planes, s_wide);
            }
            s_theta = 0f;
            if (SubPixelFeature.On && !cam.orthographic && cam.pixelHeight > 0)
                s_theta = SubPixelPixels * 2f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / cam.pixelHeight;
            return true;
        }

        static void CopyPlanes(Plane[] from, float[] into)
        {
            for (int i = 0; i < 6; i++)
            {
                Vector3 n = from[i].normal;
                into[i * 4] = n.x;
                into[i * 4 + 1] = n.y;
                into[i * 4 + 2] = n.z;
                into[i * 4 + 3] = from[i].distance;
            }
        }

        // Inside (or touching) all six planes, normals pointing in.
        static bool SphereIn(float[] p, Vector3 c, float r)
        {
            for (int i = 0; i < 24; i += 4)
                if (p[i] * c.x + p[i + 1] * c.y + p[i + 2] * c.z + p[i + 3] < -r)
                    return false;
            return true;
        }

        // The box reaches within `widen` metres of the inside of all six planes.
        static bool BoxIn(float[] p, Vector3 min, Vector3 max, float widen)
        {
            for (int i = 0; i < 24; i += 4)
            {
                float x = p[i] >= 0f ? max.x : min.x, y = p[i + 1] >= 0f ? max.y : min.y, z = p[i + 2] >= 0f ? max.z : min.z;
                if (p[i] * x + p[i + 1] * y + p[i + 2] * z + p[i + 3] < -widen)
                    return false;
            }
            return true;
        }

        // Its largest particle, at the nearest point of the sphere, is under SubPixelPixels.
        static bool TooSmall(Vector3 c, float r, float size)
        {
            if (s_theta <= 0f || float.IsInfinity(size))
                return false;
            float d = Vector3.Distance(c, s_camPos) - r;
            return d > 0f && size < s_theta * d;
        }

        // ---- other cameras ----------------------------------------------------------------------------------------

        static void Record(Camera cam)
        {
            OtherCamera oc = null;
            for (int i = 0; i < s_otherCams.Count; i++)
                if (ReferenceEquals(s_otherCams[i].Cam, cam))
                {
                    oc = s_otherCams[i];
                    break;
                }
            if (oc != null && !oc.Viewer)
                return;
            // Draws none of the layers the registered systems are on (the game's sky camera: layer 19 only).
            int mask = cam.cullingMask;
            if ((mask & s_layerUnion) == 0)
                return;
            if (ReferenceEquals(cam, Utils.GetMainCamera()))
                return;
            float now = Time.unscaledTime;
            if (oc == null)
            {
                if (s_otherCams.Count >= MaxOtherCameras)
                {
                    // Not kept track of: everything counts as seen while such renders go on.
                    s_tooManyAt = now;
                    OtherOverflows++;
                    return;
                }
                oc = new OtherCamera { Cam = cam, Name = cam.name, Viewer = IsViewer(cam) };
                s_otherCams.Add(oc);
                s_namesDirty = true;
                if (!oc.Viewer)
                    return;
            }
            // A picture of the ground from straight above (a sky-occlusion depth picture of any mod), not a view.
            if (cam.orthographic && Vector3.Dot(cam.transform.forward, Vector3.down) > 0.99f)
                return;
            int frame = Time.frameCount;
            if (oc.Frame != frame)
            {
                // Only the latest frame's renders of a camera count: a window that is no longer drawn stops counting.
                oc.Frame = frame;
                oc.Count = 0;
                oc.Overflow = false;
            }
            oc.At = now;
            OtherRenders++;
            if (oc.Count >= MaxViewsPerCamera)
            {
                if (!oc.Overflow)
                    OtherOverflows++;
                oc.Overflow = true;
                return;
            }
            OtherView v = oc.Views[oc.Count] ??= new OtherView();
            // What Unity culls this render with (PortalRenderer sets a cullingMatrix cut to the window's rectangle).
            GeometryUtility.CalculateFrustumPlanes(cam.cullingMatrix, s_otherPlanes);
            CopyPlanes(s_otherPlanes, v.P);
            v.Pos = cam.transform.position;
            v.Mask = mask;
            v.Ortho = cam.orthographic;
            oc.Count++;
        }

        // Reflection-probe cameras and the replacement-shader data pictures (DepthCamera, Real Portal's copy of it)
        // render the whole surroundings about once a second: they are not views anybody watches.
        static bool IsViewer(Camera cam) =>
            cam.cameraType == CameraType.Game && cam.GetComponent<DepthCamera>() == null
            && !string.Equals(cam.name, RealPortalSkyCamera, StringComparison.Ordinal);

        // The views of the other cameras that count for this step.
        static void PrepareOthers(float now)
        {
            s_live.Clear();
            s_liveAll = now - s_tooManyAt <= OtherHoldSeconds;
            int cameras = 0;
            for (int i = s_otherCams.Count - 1; i >= 0; i--)
            {
                OtherCamera oc = s_otherCams[i];
                if (oc.Cam == null)
                {
                    s_otherCams.RemoveAt(i);
                    s_namesDirty = true;
                    continue;
                }
                if (!oc.Viewer || oc.Count == 0 || now - oc.At > OtherHoldSeconds)
                    continue;
                cameras++;
                if (oc.Overflow)
                    s_liveAll = true;
                for (int k = 0; k < oc.Count; k++)
                    s_live.Add(oc.Views[k]);
            }
            OtherCameras = cameras;
            OtherViews = s_live.Count;
        }

        // Another camera saw the sphere in its latest renders (widened), or the hook says one will.
        static bool OtherSees(Vector3 c, float r, int layerBit)
        {
            if (s_liveAll)
            {
                SeenByOther++;
                return true;
            }
            for (int i = 0; i < s_live.Count; i++)
            {
                OtherView v = s_live[i];
                if ((v.Mask & layerBit) == 0)
                    continue;
                float w = v.Ortho ? r : r + Vector3.Distance(c, v.Pos) * SinHalfWiden;
                if (SphereIn(v.P, c, w))
                {
                    SeenByOther++;
                    return true;
                }
            }
            return HookSees(c, r);
        }

        // The same for a cell's box (layers: the union of its members').
        static bool OtherBox(Vector3 min, Vector3 max, int layers)
        {
            if (s_liveAll)
                return true;
            if (s_live.Count > 0)
            {
                Vector3 center = (min + max) * 0.5f;
                float half = (max - min).magnitude * 0.5f;
                for (int i = 0; i < s_live.Count; i++)
                {
                    OtherView v = s_live[i];
                    if ((v.Mask & layers) == 0)
                        continue;
                    float w = v.Ortho ? 0f : (Vector3.Distance(center, v.Pos) + half) * SinHalfWiden;
                    if (BoxIn(v.P, min, max, w))
                        return true;
                }
            }
            if (IsSeenByOtherCamera == null)
                return false;
            HookCalls++;
            return HookBox(min, max);
        }

        static bool HookSees(Vector3 c, float r)
        {
            Func<Bounds, bool> hook = IsSeenByOtherCamera;
            if (hook == null)
                return false;
            HookCalls++;
            try
            {
                return hook(new Bounds(c, new Vector3(2f * r, 2f * r, 2f * r)));
            }
            catch (Exception e)
            {
                // Seen (nothing is paused on a failing answer); never part of the give-up count.
                HookErrors++;
                if (s_errorsLogged++ < 10)
                    EnginePlugin.Log?.LogError($"ParticleLod: IsSeenByOtherCamera threw: {e}");
                return true;
            }
        }

        static bool HookBox(Vector3 min, Vector3 max)
        {
            try
            {
                var b = new Bounds();
                b.SetMinMax(min, max);
                return IsSeenByOtherCamera?.Invoke(b) ?? false;
            }
            catch (Exception e)
            {
                HookErrors++;
                if (s_errorsLogged++ < 10)
                    EnginePlugin.Log?.LogError($"ParticleLod: IsSeenByOtherCamera threw: {e}");
                return true;
            }
        }

        // ---- running systems: pause what nobody saw for a while ---------------------------------------------------

        static int VisitRunning(float now)
        {
            int count = s_running.Count;
            if (count == 0)
                return 0;
            // Each entry about every RunningVisitSeconds; never more than once per frame, and a long frame does not
            // leave a backlog of visits for the frames after it.
            s_runCarry += count * Mathf.Min(Time.unscaledDeltaTime, MaxVisitDt) / RunningVisitSeconds;
            int visits = Math.Min(Mathf.Clamp((int)s_runCarry, 8, 2048), count);
            s_runCarry = Mathf.Clamp(s_runCarry - visits, 0f, count);
            int paused = 0;
            for (int n = 0; n < visits && s_running.Count > 0; n++)
            {
                if (s_runCursor >= s_running.Count)
                    s_runCursor = 0;
                Entry e = s_running[s_runCursor];
                if (e.Ps == null)
                {
                    Drop(e);
                    continue;
                }
                s_runCursor++;
                ParticleSystem ps = e.Ps;
                // Paused by someone else (Real Portal's far-area sleep, a mod) or not playing: not ours to decide.
                if (ps.isPaused)
                {
                    ForeignSeen++;
                    e.LastSeen = now;
                    continue;
                }
                if (!ps.isPlaying)
                {
                    e.LastSeen = now;
                    continue;
                }
                // Stopping (Stop(..., StopEmitting): still playing while its last particles fade): a pause and a
                // Play(false) later would start the emission its owner stopped.
                if (!ps.isEmitting)
                {
                    KeptStopping++;
                    e.LastSeen = now;
                    continue;
                }
                Vector3 pos = e.Tr.position;
                if ((pos - e.LastPos).sqrMagnitude > MoveMeters * MoveMeters)
                {
                    e.LastPos = pos;
                    e.StillSince = now;
                    Movers++;
                }
                Refresh(e, pos);
                if (Wanted(e, now, out bool subPixel))
                {
                    e.LastSeen = now;
                    continue;
                }
                if (now - e.LastSeen < HoldSeconds || paused >= PauseBudget)
                    continue;
                // An inactive object is not simulated anyway: nothing to save, and it could not be played again.
                if (!ps.gameObject.activeInHierarchy)
                {
                    e.LastSeen = now;
                    continue;
                }
                if (Pause(e, now, subPixel))
                {
                    paused++;
                    if (subPixel)
                        PausesSubPixel++;
                }
            }
            return paused;
        }

        // The sphere of a running system: its particles now (the renderer's bounds) and how far new ones reach.
        static void Refresh(Entry e, Vector3 pos)
        {
            ParticleSystemRenderer r = e.Renderer;
            if (r != null)
            {
                Bounds b = r.bounds;
                float ext = b.extents.magnitude;
                if (ext > 1e-3f && !float.IsNaN(ext) && !float.IsInfinity(ext))
                {
                    e.Center = b.center;
                    e.Radius = Mathf.Max(ext, Vector3.Distance(b.center, pos) + e.Reach);
                    return;
                }
            }
            e.Center = pos;
            e.Radius = e.Reach;
        }

        // Somebody may see it, or wants it running. subPixel: the only reason it is unseen is its size on screen.
        static bool Wanted(Entry e, float now, out bool subPixel)
        {
            subPixel = false;
            if (now - e.StillSince < StillSeconds)
                return true;
            if (e.Culling != ParticleSystemCullingMode.AlwaysSimulate && e.Ps.main.cullingMode == ParticleSystemCullingMode.AlwaysSimulate)
            {
                KeptAlwaysSimulate++;
                return true;
            }
            float r = e.Radius + Margin;
            if (SphereIn(s_wide, e.Center, r))
            {
                if (!TooSmall(e.Center, e.Radius, e.Size))
                    return true;
                subPixel = true;
            }
            if (OtherSees(e.Center, r, e.LayerBit))
            {
                subPixel = false;
                return true;
            }
            return false;
        }

        static bool Pause(Entry e, float now, bool subPixel)
        {
            ParticleSystem ps = e.Ps;
            ps.Pause(false);
            if (!ps.isPaused)
                return false;
            ulong mask = 0;
            ParticleSystem[] followers = e.Followers;
            if (followers != null)
                for (int i = 0; i < followers.Length; i++)
                {
                    ParticleSystem f = followers[i];
                    if (f != null && f.isPlaying && !f.isPaused)
                    {
                        f.Pause(false);
                        if (f.isPaused)
                            mask |= 1UL << i;
                    }
                }
            e.FollowersPaused = mask;
            e.IsPaused = true;
            e.SubPixelPause = subPixel;
            e.Inactive = false;
            e.PausedAt = now;
            e.PausedAtGame = Time.time;
            Move(e, s_running, s_paused);
            AddToCell(e);
            Pauses++;
            return true;
        }

        // ---- paused systems: play again what may be seen --------------------------------------------------------

        static int ResumeSeen(float now)
        {
            int resumed = 0;
            bool others = s_liveAll || s_live.Count > 0 || IsSeenByOtherCamera != null;
            for (int ci = s_cells.Count - 1; ci >= 0; ci--)
            {
                if (ci >= s_cells.Count)
                    continue;
                Cell cell = s_cells[ci];
                if (cell.Dirty)
                    Recompute(cell);
                CellTests++;
                Vector3 min = new Vector3(cell.X * CellSize - cell.MaxRadius - Margin, cell.MinY - cell.MaxRadius - Margin, cell.Z * CellSize - cell.MaxRadius - Margin);
                Vector3 max = new Vector3((cell.X + 1) * CellSize + cell.MaxRadius + Margin, cell.MaxY + cell.MaxRadius + Margin, (cell.Z + 1) * CellSize + cell.MaxRadius + Margin);
                bool main = BoxIn(s_wide, min, max, 0f) && !BoxTooSmall(min, max, cell.MaxSize);
                // The other cameras' (and the hook's) answer for the whole cell, asked at most once per cell and frame:
                // -1 not asked yet.
                int other = -1;
                if (!main)
                {
                    if (!others)
                        continue;
                    other = OtherBox(min, max, cell.Layers) ? 1 : 0;
                    if (other == 0)
                        continue;
                }
                List<Entry> members = cell.Members;
                for (int i = members.Count - 1; i >= 0; i--)
                {
                    if (i >= members.Count)
                        continue;
                    Entry e = members[i];
                    if (e.Ps == null)
                    {
                        Drop(e);
                        continue;
                    }
                    float r = e.Radius + Margin;
                    bool seenMain = main && SphereIn(s_wide, e.Center, r) && !TooSmall(e.Center, e.Radius, e.Size);
                    bool seen = seenMain;
                    if (!seen && others)
                    {
                        if (other < 0)
                            other = OtherBox(min, max, cell.Layers) ? 1 : 0;
                        seen = other == 1 && OtherSees(e.Center, r, e.LayerBit);
                    }
                    if (!seen)
                        continue;
                    if (resumed >= ResumeBudget)
                    {
                        ResumesDeferred++;
                        continue;
                    }
                    // Already inside the game camera's unwidened view, and paused for being out of view (not for its
                    // size alone): it may have been drawn frozen last frame.
                    bool late = seenMain && !e.SubPixelPause && SphereIn(s_tight, e.Center, e.Radius);
                    if (Resume(e, now))
                    {
                        resumed++;
                        if (late)
                            LateResumes++;
                    }
                }
            }
            return resumed;
        }

        static bool BoxTooSmall(Vector3 min, Vector3 max, float size)
        {
            if (s_theta <= 0f || float.IsInfinity(size))
                return false;
            float dx = Mathf.Max(0f, Mathf.Max(min.x - s_camPos.x, s_camPos.x - max.x));
            float dy = Mathf.Max(0f, Mathf.Max(min.y - s_camPos.y, s_camPos.y - max.y));
            float dz = Mathf.Max(0f, Mathf.Max(min.z - s_camPos.z, s_camPos.z - max.z));
            float d = Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
            return d > 0f && size < s_theta * d;
        }

        // Plays it again if it is still paused (only what this module paused: e is in s_paused only then).
        static bool Resume(Entry e, float now)
        {
            ParticleSystem ps = e.Ps;
            bool played = false;
            if (ps != null && ps.isPaused)
            {
                // An inactive object draws nothing: it stays paused (ours), out of the per-frame cell test, until
                // VisitPaused finds it active again.
                if (!ps.gameObject.activeInHierarchy)
                {
                    if (!e.Inactive)
                    {
                        e.Inactive = true;
                        ResumeFailures++;
                        RemoveFromCell(e);
                    }
                    return false;
                }
                float missed = Time.time - e.PausedAtGame;
                if (CatchUpFeature.On && missed > 0.05f)
                    ps.Simulate(Mathf.Min(missed, CatchUpSeconds), false, false, true);
                ps.Play(false);
                played = true;
            }
            PlayFollowers(e);
            Unpaused(e, now);
            if (played)
                Resumes++;
            return played;
        }

        // The sub-emitters this module paused with e, where still paused.
        static void PlayFollowers(Entry e)
        {
            ParticleSystem[] followers = e.Followers;
            if (followers == null || e.FollowersPaused == 0)
                return;
            for (int i = 0; i < followers.Length; i++)
            {
                ParticleSystem f = followers[i];
                if ((e.FollowersPaused & (1UL << i)) != 0 && f != null && f.isPaused)
                    f.Play(false);
            }
        }

        // e is running again (played by this module or someone else): back to the running list.
        static void Unpaused(Entry e, float now)
        {
            e.FollowersPaused = 0;
            e.IsPaused = false;
            e.SubPixelPause = false;
            e.Inactive = false;
            e.LastSeen = now;
            RemoveFromCell(e);
            Move(e, s_paused, s_running);
        }

        // About once a second each: gone, played or stopped by someone else, active again, moving, or wanted by Real
        // Portal.
        static void VisitPaused(float now)
        {
            int count = s_paused.Count;
            if (count == 0)
                return;
            s_pausedCarry += count * Mathf.Min(Time.unscaledDeltaTime, MaxVisitDt) / PausedVisitSeconds;
            int visits = Math.Min(Mathf.Clamp((int)s_pausedCarry, 4, 1024), count);
            s_pausedCarry = Mathf.Clamp(s_pausedCarry - visits, 0f, count);
            for (int n = 0; n < visits && s_paused.Count > 0; n++)
            {
                if (s_pausedCursor >= s_paused.Count)
                    s_pausedCursor = 0;
                Entry e = s_paused[s_pausedCursor];
                ParticleSystem ps = e.Ps;
                if (ps == null)
                {
                    Drop(e);
                    continue;
                }
                s_pausedCursor++;
                if (!ps.isPaused)
                {
                    // Someone played or stopped it: running again. The sub-emitters this module paused are played as
                    // Resume would (a Play(false) of the parent, or a sub-emitter outside its hierarchy, does not reach
                    // them; one stopped with its parent is no longer paused and is left as it is).
                    PlayFollowers(e);
                    Unpaused(e, now);
                    continue;
                }
                if (e.Inactive)
                {
                    if (!ps.gameObject.activeInHierarchy)
                        continue;
                    // Active again: back into the per-frame test (its frozen particles are where they were).
                    e.Inactive = false;
                    AddToCell(e);
                }
                Vector3 pos = e.Tr.position;
                bool moved = (pos - e.LastPos).sqrMagnitude > MoveMeters * MoveMeters;
                if (moved)
                {
                    e.LastPos = pos;
                    e.StillSince = now;
                    Movers++;
                }
                bool wanted = moved
                    || (e.Culling != ParticleSystemCullingMode.AlwaysSimulate && ps.main.cullingMode == ParticleSystemCullingMode.AlwaysSimulate);
                if (wanted)
                    Resume(e, now);
            }
        }

        // No game camera for a while: what this module paused plays again (within ResumeBudget per frame); dead
        // entries go.
        static void PlayBackPaused(float now)
        {
            int budget = ResumeBudget;
            for (int i = s_paused.Count - 1; i >= 0 && budget > 0; i--)
            {
                if (i >= s_paused.Count)
                    continue;
                Entry e = s_paused[i];
                if (e.Ps == null)
                {
                    Drop(e);
                    continue;
                }
                if (Resume(e, now))
                {
                    NoViewResumes++;
                    budget--;
                }
            }
        }

        // Without a view the visits do not run: destroyed systems still leave the registry, a few per frame.
        static void SweepDead()
        {
            for (int n = 0; n < SweepBudget && s_running.Count > 0; n++)
            {
                if (s_runCursor >= s_running.Count)
                    s_runCursor = 0;
                Entry e = s_running[s_runCursor];
                if (e.Ps == null)
                    Drop(e);
                else
                    s_runCursor++;
            }
        }

        // ---- bookkeeping ------------------------------------------------------------------------------------------

        static void Move(Entry e, List<Entry> from, List<Entry> to)
        {
            int slot = e.Slot, last = from.Count - 1;
            if (slot <= last && ReferenceEquals(from[slot], e))
            {
                Entry moved = from[last];
                from[slot] = moved;
                moved.Slot = slot;
                from.RemoveAt(last);
            }
            e.Slot = to.Count;
            to.Add(e);
        }

        static void Drop(Entry e)
        {
            List<Entry> list = e.IsPaused ? s_paused : s_running;
            int slot = e.Slot, last = list.Count - 1;
            if (slot <= last && ReferenceEquals(list[slot], e))
            {
                Entry moved = list[last];
                list[slot] = moved;
                moved.Slot = slot;
                list.RemoveAt(last);
            }
            RemoveFromCell(e);
            s_byPs.Remove(e.Ps);
            e.Ps = null;
            e.Followers = null;
            Dropped++;
        }

        static long CellKey(int x, int z) => ((long)x << 32) ^ (uint)z;

        static void AddToCell(Entry e)
        {
            int x = Mathf.FloorToInt(e.Center.x / CellSize), z = Mathf.FloorToInt(e.Center.z / CellSize);
            long key = CellKey(x, z);
            if (!s_cellByKey.TryGetValue(key, out Cell cell))
            {
                cell = new Cell { Key = key, X = x, Z = z, Slot = s_cells.Count, MinY = float.MaxValue, MaxY = float.MinValue };
                s_cellByKey.Add(key, cell);
                s_cells.Add(cell);
            }
            e.Cell = cell;
            e.CellSlot = cell.Members.Count;
            cell.Members.Add(e);
            cell.MinY = Mathf.Min(cell.MinY, e.Center.y);
            cell.MaxY = Mathf.Max(cell.MaxY, e.Center.y);
            cell.MaxRadius = Mathf.Max(cell.MaxRadius, e.Radius);
            cell.MaxSize = Mathf.Max(cell.MaxSize, e.Size);
            cell.Layers |= e.LayerBit;
        }

        static void RemoveFromCell(Entry e)
        {
            Cell cell = e.Cell;
            if (cell == null)
                return;
            e.Cell = null;
            List<Entry> members = cell.Members;
            int slot = e.CellSlot, last = members.Count - 1;
            if (slot <= last && ReferenceEquals(members[slot], e))
            {
                Entry moved = members[last];
                members[slot] = moved;
                moved.CellSlot = slot;
                members.RemoveAt(last);
            }
            if (members.Count > 0)
            {
                cell.Dirty = true;
                return;
            }
            s_cellByKey.Remove(cell.Key);
            int cs = cell.Slot, lastCell = s_cells.Count - 1;
            if (cs <= lastCell && ReferenceEquals(s_cells[cs], cell))
            {
                Cell movedCell = s_cells[lastCell];
                s_cells[cs] = movedCell;
                movedCell.Slot = cs;
                s_cells.RemoveAt(lastCell);
            }
        }

        // A member left: the cell's box shrinks back to what its members need.
        static void Recompute(Cell cell)
        {
            cell.Dirty = false;
            cell.MinY = float.MaxValue;
            cell.MaxY = float.MinValue;
            cell.MaxRadius = 0f;
            cell.MaxSize = 0f;
            cell.Layers = 0;
            foreach (Entry e in cell.Members)
            {
                cell.MinY = Mathf.Min(cell.MinY, e.Center.y);
                cell.MaxY = Mathf.Max(cell.MaxY, e.Center.y);
                cell.MaxRadius = Mathf.Max(cell.MaxRadius, e.Radius);
                cell.MaxSize = Mathf.Max(cell.MaxSize, e.Size);
                cell.Layers |= e.LayerBit;
            }
        }

        // Off, given up or the scene going: forget everything. playAgain: what this module paused and is still paused
        // is played now (switching off must leave nothing frozen).
        static void Release(bool playAgain)
        {
            if (playAgain)
            {
                float now = Time.unscaledTime;
                foreach (Entry e in s_paused)
                {
                    try
                    {
                        if (e.Ps != null && e.Ps.isPaused)
                            e.Ps.Play(false);
                        PlayFollowers(e);
                    }
                    catch (Exception ex)
                    {
                        Errors++;
                        if (s_errorsLogged++ < 10)
                            EnginePlugin.Log?.LogError($"ParticleLod: playing a system again failed: {ex}");
                    }
                    e.LastSeen = now;
                }
            }
            s_byPs = new Dictionary<ParticleSystem, Entry>(s_byPs.Comparer);
            s_running = new List<Entry>();
            s_paused = new List<Entry>();
            s_cells.Clear();
            s_cellByKey.Clear();
            s_pending = new List<ZNetView>();
            s_pendingAt = s_runCursor = s_pausedCursor = 0;
            s_runCarry = s_pausedCarry = 0f;
            s_live.Clear();
            s_liveAll = false;
            Gauges();
        }
    }
}
