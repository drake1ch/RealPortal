using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace RealPortal.Engine
{
    // Grass. InstanceRenderer.CustomUpdate (every grass and clutter patch of ClutterSystem, every frame from
    // MonoUpdaters.Update) culls a patch against the game camera's frustum, thins it by its distance to the game camera
    // and submits it with Graphics.DrawMeshInstanced(mesh, 0, material, matrices, count, null, shadows, true, layer):
    // the overload without a camera argument, which passes camera null. Such a batch is drawn by every camera that
    // renders in that frame with the InstanceRenderer layer (29) in its culling mask, and DrawMeshInstanced is not culled
    // by any camera's view. So every Real Portal window camera draws every patch the game camera sees (plus those Real
    // Portal's InstanceRendererUpdate prefix lets through for its windows), and each window pays the per-draw cost of all
    // of them (hundreds to ~1.6k batches) and the GPU cost of the blades.
    //
    // With GrassMainCameraOnly on, both calls (the thinned one and the plain one) pass the game camera instead of null
    // for clutter that casts no shadow: Utils.GetMainCamera(), the per-frame value CustomUpdate itself culled and thinned
    // by, so only that camera draws it. Clutter whose m_shadowCasting is not Off keeps the game's null-camera draw: every
    // camera draws it as in the game, shadow maps included, so no picture depends on whether a camera-targeted draw
    // reaches that camera's shadow maps (P3(iii), unverified; the lab's P1 has a branch for "casts none"). It is not
    // recorded (not in FrameBatch, no Submitted): it reaches every camera already, and nobody may submit it again.
    // Every other camera that renders later in that frame gets this frame's recorded batches submitted again, aimed at
    // it, from Camera.onPreCull, unless a mod excluded it (ExcludeCamera). This is opt-out, so On draws grass in the same
    // cameras and frames as Off, for the game and for every mod that does not know this module: Path of Valheiman's
    // portrait scenery camera ("Portrait scenery camera", the game camera's mask without layer 30, rendered by its
    // equipment portrait every 0.1 s while the inventory is open, config "Equipment portrait surroundings", on by
    // default; a camera rendered before the Update pass gets nothing either way), Real Portal's
    // legacy shared window camera and its sky-occlusion camera, screenshot and capture mods. What a camera is handed:
    //  - only the batches its culling mask takes: the game's sky camera (layer 19 only), UpscaledFrameBuffer's clear
    //    camera (mask 0) and PoV's portrait camera (layer 30 only) get none;
    //  - not those whose culling sphere (m_bounds, what CustomUpdate tests against the game camera) is outside that
    //    camera's frustum: the game's own test (Utils.InsideMainCamera) for that camera, so the image is the same apart
    //    from the wind-bend tolerance the game already accepts for its own camera. Not for the depth camera and the
    //    reflection-probe cameras, which get every batch in their mask, as in the game: whether Unity's probe camera
    //    holds the face's matrices at onPreCull, and raises it once per face, is unverified (P3(iv)), and both render
    //    rarely (the depth picture once a second, a probe every few seconds), so the test would save little there. Lab
    //    switch GrassResubmitFrustum;
    //  - its batches again at every render (GrassResubmitEachRender, on by default, P3(ii) below): a camera that renders
    //    again in the frame (probe faces, the shared window camera) may have none left from its first render. With it off
    //    (once the lab shows that a draw submitted from onPreCull stays queued for that camera's later renders) at most
    //    one copy of each batch per camera per frame: a later render gets only what its new view or mask takes that it
    //    did not get yet.
    // The game's own cameras:
    //  - the game camera (mask everything): gets each batch as before (with GrassResubmitEachRender on, the default, again
    //    for its second and later renders in a frame);
    //  - CinematicsManager's camera, which renders while the game camera is disabled: Camera.main is then either that
    //    camera (tagged MainCamera), which gets the batch, or null, and then CustomUpdate draws nothing in the game too;
    //  - DepthCamera, the top-down picture of what covers the sky (_SkyAlphaTexture, which darkens what stands under
    //    trees and roofs). It renders once a second with RenderWithShader(m_depthShader, "RenderType") from
    //    InvokeRepeating, after the Update pass that submitted the grass. It gets this frame's batches in its mask, without
    //    the view test, and Unity's replacement rule then decides what it draws, as in the game. Where from: its own
    //    onPreCull, inside the render like every other camera, once a RenderDepth call has shown that RenderWithShader
    //    raises onPreCull (the first onPreCull after our prefix on RenderDepth is then the depth camera's: nothing renders
    //    in between); until then, and for good if it does not, our prefix on RenderDepth, right before the render (an
    //    onPreCull of that render then adds nothing). Its first render logs its mask and which clutter materials its
    //    replacement shader has a subshader for (DepthCameraMask, DepthGrassMaterials of DepthMaterialsSeen): whether the
    //    game's picture has grass at all;
    //  - realtime reflection probes (ReflectionUpdate every 3 s, and Real Portal's far probes): Unity's probe camera is
    //    internal; onPreCull (cameraType Reflection) hands it the batches like any other camera, but without the view
    //    test. Its masks and the game probes' settings (time slicing, culling mask) are logged once.
    //
    // Real Portal's main view through CameraThrough: the game view's _SkyAlphaTexture is then the picture of Real
    // Portal's own RealPortalSkyOcclusion camera (FarSkyOcclusion.Show(s_mainSkyView), RenderWithShader from
    // PortalSystem.MainPreCull), as it is for every window. Off, it draws the null-camera grass. On, it gets the batches
    // from onPreCull only if RenderWithShader raises onPreCull: DepthRaisesPreCull says so for the game's depth camera. If
    // DepthPreCulls stays 0 while DepthCalls rises, it does not, and with this on the main view's (and the windows') sky
    // occlusion lose the grass the game camera drew, until Real Portal calls SubmitFrameTo(_cam) right before its
    // RenderWithShader in FarSkyOcclusion.Show (at least for s_mainSkyView). The lab's pixel-parity check covers that
    // picture under CameraThrough, not only the windows.
    //
    // The saving comes once Real Portal (stage S2) excludes its window-slot cameras and submits window grass itself (per
    // window, culled by its view, thinned by its distance). Until then On draws the same images at a higher main-thread
    // cost (the re-submissions: Real Portal's window cameras still get grass, one re-submission per window render), hence
    // off by default. The lab switch GrassExcludeOthers measures that ceiling now: with it on, only the game's own cameras
    // (the game camera, its depth camera, reflection probes) get grass, and every other camera, Real Portal's windows and
    // sky-occlusion camera among them, gets none, as if excluded. That is not the same images (windows without grass):
    // pixel parity is a separate run with it off.
    //
    // For Real Portal (S2) and other mods:
    //  - TargetedThisFrame: what this frame's CustomUpdate draws of clutter that casts no shadow did (the game camera
    //    only, or the null camera; shadow casters always take the null camera). A consumer deciding after
    //    MonoUpdaters.Update whether to submit window grass itself uses this, not Active: the switch can flip between the
    //    pass and its cameras' renders, and Active then says what the next frame's draws will do;
    //  - Active: on, and both of our patches are in place as Harmony has them now;
    //  - ExcludeCamera(camera): that camera gets nothing from here (Real Portal's window-slot cameras, which get window
    //    grass from Real Portal instead). The sky-occlusion camera may stay in (it then gets the game's grass, as Off);
    //  - Submitted and FrameBatch(i)/FrameBatchCount/FrameCamera: what the game camera alone was given this frame
    //    (renderer, mesh, material, the renderer's own matrices and the count after thinning, culling sphere); clutter
    //    that casts shadows is not there (every camera draws it already). This is enough to submit a batch again per
    //    window camera (Graphics.RenderMeshInstanced with RenderParams.camera, right before that camera's Render()),
    //    culled by the window's frustum;
    //  - GetInstances and ViewCount: a renderer's private instances and the game's thinning for another viewpoint,
    //    for grass the game camera does not draw (far-point grass, LOD counts from a window camera). Not from inside
    //    Submitted (see there);
    //  - SubmitFrameTo(camera): give a camera exactly the game camera's grass now, as the depth camera gets it before a
    //    render that raises no onPreCull (for a camera rendered without onPreCull, or an excluded one this once).
    // Real Portal's InstanceRendererUpdate prefix (window-visible patches let past the frustum test, thinned by the
    // window's distance) still runs with this on: its patches reach the game camera (which drew them before too, mostly
    // out of sight) and, handed on, the cameras nobody excluded (so the legacy window camera keeps them). Real Portal
    // should skip that prefix once it excludes its window cameras, and submit window grass itself. While it runs it
    // leaves a renderer in a temporary state inside CustomUpdate (see Submitted).
    //
    // Off: the replacement calls the game's own overload with the same arguments, nothing is recorded and no camera
    // callback is installed, so the game's draw is unchanged (the depth camera's prefix finds no batches and returns).
    // Switching takes effect from the next draw. Batches drawn for the game camera before switching off are still
    // handed to the cameras rendering later in that frame, and the camera callback removes itself on the next frame.
    // Switching on during a frame: the batches drawn before were null-camera ones (every camera), so nothing is drawn
    // twice or missed.
    //
    // The transpiler (Priority.Last) swaps exactly the game's two calls of that overload for ours, which takes the same
    // arguments plus the renderer (ldarg.0). With any other count, or a jump landing on one of them (another mod's
    // transpiler, a game update), it leaves the method as the game's and logs an error. At install that leaves the
    // module out (EnginePlugin reports it). Later, when another mod's patch makes Harmony rebuild the method, it makes
    // the feature inert (Active false). Harmony rebuilds the method without running our transpiler when another mod
    // removes every patch (HarmonyX's harmony.UnpatchAll() with no id, Unpatch(method, All)), so Patched also asks
    // Harmony whether our transpiler and the depth camera's prefix are still there (at most once a second, and after any
    // feature switch); with either gone the draws take the game's path and Active reads false. One case stays unseen: a
    // foreign patch whose build fails after our transpiler ran leaves s_patched as that run set it, not as the method
    // stands (TargetedThisFrame still follows the draws that actually happen). Other mods' prefixes and postfixes on
    // CustomUpdate (Real Portal's) are unaffected.
    //
    // Assumptions for the lab (Architecture P3, numbered as there):
    //  (i) a camera-targeted draw is drawn by that camera only (else the re-submissions would add a second copy to every
    //      later camera);
    //  (ii) what a camera-targeted draw does after the render it was submitted for. The lab's P3 submits before Render()
    //      ('ii twice', 'ii leak', 'ii persist'); this module submits from inside the render, in onPreCull, so a case at
    //      its own timing decides GrassResubmitEachRender: a Camera.onPreCull handler submits a marker to the camera being
    //      rendered (at its first render only), the camera renders twice in that frame and once in the next, and the
    //      marker's coverage is read each time. Until that case has run, GrassResubmitEachRender stays on: every render
    //      gets its batches again, so a render that consumes its draws loses nothing. If they do stay queued, a camera's
    //      k-th render in a frame draws up to k copies: the same pixels for opaque and alpha-tested clutter (a second
    //      identical draw passes the depth test with the same values; a blended clutter material, if there is one, would
    //      show it), at that much more cost (the shared window camera rendering several windows; probe faces, where
    //      MaxReflectionPreCullsPerFrame > 1, the game's probes' time-slicing mode logged). Marker in the second render:
    //      switch GrassResubmitEachRender off, one copy per camera per frame. Missing grass is the failure that shows,
    //      copies are not, hence that default.
    //      And a gate for the whole module, not only for the slot rule: no draw may outlive its frame. If a render in the
    //      next frame shows the marker without a new submission ('ii persist' for a draw given before Render(), the case
    //      above for one given in onPreCull), the cameras rendered by hand (the depth camera, Real Portal's cameras, any
    //      camera given SubmitFrameTo) would keep earlier frames' batches and add more at every render: stale grass in
    //      _SkyAlphaTexture, a queue that grows all session, which Off never does. GrassMainCameraOnly must then stay off.
    //      If only draws given before Render() outlive it, it can go on once nothing is given ahead any more: the depth
    //      camera's prefix not even at the first call (onPreCull serves it from the second on when DepthRaisesPreCull), and
    //      no SubmitFrameTo (the game's null-camera draw on the frames RenderDepth fires is no way out: InvokeRepeating's
    //      frame is not known at the Update pass). 'ii leak' (a draw for a camera that did not render shows at its next
    //      render) matters only to a SubmitFrameTo caller that does not render after it, which it must not do;
    //  (iii) not assumed: clutter that casts shadows keeps the game's null-camera draw (above), so whether a camera-
    //      targeted draw reaches that camera's shadow maps changes no picture. ShadowDraws counts those draws;
    //  (iv) a draw submitted from onPreCull reaches that render, so probes keep grass. Whether onPreCull sees the probe
    //      camera at all is ReflectionPreCulls (every reflection-camera onPreCull, before any filter, ReflectionCameraSeen
    //      once there was one). ReflectionRenders counts only the probe renders that got at least one batch: 0 by design
    //      when the logged probe mask leaves out layer 29. Probe cameras get no view test (their matrices at onPreCull, and
    //      whether it comes once per face, are part of this question). That the draw reaches the render only the cubemap
    //      pixel check shows;
    //  (v) RenderWithShader draws it with the replacement shader, so the sky-occlusion picture keeps grass. DepthRenders
    //      counts depth renders that got at least one batch (0 by design when the logged mask leaves out layer 29);
    //      DepthCalls all RenderDepth calls while there were batches (about one per second);
    //  (vi) the camera's culling mask applies (the re-submissions leave out what a mask excludes, so the game's result
    //      holds either way).
    // Cost of the re-submissions (main thread): SubmitMs in all, MaxFrameSubmitMs in the worst frame, ResubmitFrames.
    // The batches keep a reference to the renderer's matrix array, not a copy (64 KB per full patch, every frame). The
    // game never rewrites a live patch after its draw (ClutterSystem fills only new renderers, and destruction is
    // deferred to the frame's end). A mod that rewrites one between Update and a later render would show its new
    // instances there (the game: the old ones).
    [EngineModule]
    public static class GrassCameraModule
    {
        public static readonly Feature Feature = Features.Register("GrassMainCameraOnly", false,
            "Grass and other instanced clutter (InstanceRenderer) that casts no shadow is drawn for the game camera only, and handed again, " +
            "by each camera's view and culling mask, to every other camera that renders, except the cameras a mod excludes. Off by default: " +
            "the saving comes once Real Portal excludes its window cameras and submits window grass itself; until then it costs more than it saves.");

        // Lab, P3(ii): see ResubmitEachRender. On until the lab's case at this module's timing shows the copies.
        public static readonly Feature ResubmitFeature = Features.Register("GrassResubmitEachRender", true,
            "Lab (GrassMainCameraOnly): every render of a camera gets its grass again (safe whether or not one Render() consumes a " +
            "camera-targeted draw); off once the lab shows such a draw stays queued for the camera's later renders in the frame.");

        // Lab: the view test on re-submissions (see Submit), to A/B its cost and the pixel parity.
        public static readonly Feature FrustumFeature = Features.Register("GrassResubmitFrustum", true,
            "Lab (GrassMainCameraOnly): grass handed to other cameras leaves out patches outside that camera's view.");

        // Lab: the saving Real Portal's own window grass (S2) can reach, measured now. Only the game's own cameras (the game
        // camera, its depth camera, reflection-probe cameras) get grass from onPreCull; every other camera gets none, as if
        // excluded (ExcludedRenders). Not pixel parity: Real Portal's windows show no grass while it is on.
        public static readonly Feature ExcludeOthersFeature = Features.Register("GrassExcludeOthers", false,
            "Lab (GrassMainCameraOnly): only the game's own cameras (game, depth, reflection probes) get grass; every other camera, Real " +
            "Portal's windows among them, gets none. Measures the saving of Real Portal's own window grass (S2), not the same images.");

        // One InstanceRenderer draw as the game camera got it.
        public struct Batch
        {
            public InstanceRenderer Renderer;
            public Mesh Mesh;
            public int Submesh;
            public Material Material;
            // The renderer's own array (not a copy): read [0, Count) during this frame only, never write it.
            public Matrix4x4[] Matrices;
            // After the game camera's thinning (m_lodCount) when the renderer uses LOD.
            public int Count;
            public MaterialPropertyBlock Properties;
            // Always Off: clutter that casts shadows keeps the game's null-camera draw and is not recorded.
            public ShadowCastingMode Shadows;
            public bool ReceiveShadows;
            public int Layer;
            // The renderer's culling sphere (m_bounds) at the draw; valid when BoundsValid (the game updates it only
            // for renderers with m_frustumCull).
            public BoundingSphere Bounds;
            public bool BoundsValid;
        }

        public delegate void BatchHandler(in Batch batch);

        // Raised right after the game camera got a batch (clutter that casts no shadow), only while the feature is on.
        // Main thread, inside InstanceRenderer.CustomUpdate (MonoUpdaters.Update), so between other mods' prefixes and
        // postfixes on it. While Real Portal's legacy InstanceRendererUpdate prefix runs, a window-visible patch is in that
        // prefix's temporary state here: m_frustumCull false, and m_lodMinDistance/m_lodMaxDistance moved out by the main
        // camera's distance minus the window's (up to hundreds of metres). A handler must not read those fields or call
        // ViewCount(renderer, pos), which reads them: it records what it needs and sizes per-window grass after
        // MonoUpdaters.Update (FrameBatch(i) has every batch of the frame, and the postfix has put the fields back by
        // then), or passes distances it knows to ViewCount's other overload.
        public static event BatchHandler Submitted;

        // P3(ii), see "Assumptions for the lab". On (the default until the lab's case at this module's timing has run):
        // every render of every camera gets its batches again from onPreCull (the game camera's second and later renders
        // in a frame included), and SubmitFrameTo submits on every call: right if a Render() consumes a camera-targeted
        // draw. Off: such a draw is taken to be drawn by every Render() of its camera in the frame, so a camera gets each
        // batch once per frame (a probe rendering all faces in one frame, or two probes sharing Unity's probe camera,
        // share it). The wrong value draws later renders without grass (off) or with copies of it (on: the same pixels
        // for opaque and alpha-tested clutter, more cost). Lab switch GrassResubmitEachRender (`feat`), so both can be
        // measured in one session.
        public static bool ResubmitEachRender
        {
            get => ResubmitFeature.On;
            set => ResubmitFeature.Switch(value);
        }

        // Lab: draws given to the game camera only and their instances; draws that took the game's null camera while on
        // (no game camera, which CustomUpdate itself rules out, or one of our patches no longer in place); draws of
        // clutter that casts shadows, left to the game's null camera by design; batches submitted again to other cameras;
        // renders that got at least one of them, by camera (0 by design for a camera whose mask leaves out layer 29 and,
        // with GrassResubmitEachRender off, for a render that sees nothing it did not get yet); the game camera's renders
        // after its first in a frame (re-submitted to only with GrassResubmitEachRender on); batches left out by a
        // camera's mask (where it takes some layers of the frame's batches) and by its view; renders of excluded cameras
        // (ExcludeCamera, and every other camera under GrassExcludeOthers); exceptions from Submitted handlers or
        // re-submission; batches in the last finished frame and the most seen.
        public static long MainDraws, MainInstances, FallbackDraws, ShadowDraws, Resubmitted, DepthRenders, ReflectionRenders, OtherRenders,
                           MainRerenders, SkippedByMask, SkippedByView, ExcludedRenders, HookErrors;
        public static int LastFrameBatches, MaxFrameBatches;

        // Lab, counted before any filter: every onPreCull of a reflection camera (P3(iv): whether onPreCull sees probe
        // cameras at all) and the most of them in one frame (P3(ii)); RenderDepth calls while there were batches; onPreCull
        // events of the game's depth camera (0 while DepthCalls rises: RenderWithShader does not raise onPreCull, so a
        // mod's RenderWithShader camera, Real Portal's sky occlusion, needs SubmitFrameTo). DepthRaisesPreCull: a
        // RenderDepth call's own render raised onPreCull (the first onPreCull after our prefix was the depth camera's),
        // so the depth camera is handed its batches there from then on, not by the prefix.
        public static long ReflectionPreCulls, DepthCalls, DepthPreCulls;
        public static int MaxReflectionPreCullsPerFrame;
        public static bool DepthRaisesPreCull;

        // Lab: the main-thread cost of the re-submissions (Stopwatch ticks): in all, in the worst frame, and the frames
        // that had any.
        public static long SubmitTicks, MaxFrameSubmitTicks, ResubmitFrames;
        public static double SubmitMs => SubmitTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        public static double MaxFrameSubmitMs => MaxFrameSubmitTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        // What the game's other cameras do with grass (also logged): the depth camera's culling mask from its first render
        // with batches (logged again when it or the shader changes), and how many of the instanced clutter materials it
        // was handed so far its replacement shader has a subshader for (each logged when first seen); the reflection
        // camera's last culling mask. The masks are valid once the camera was seen.
        public static bool DepthCameraSeen, ReflectionCameraSeen;
        public static int DepthCameraMask, DepthMaterialsSeen, DepthGrassMaterials, ReflectionCameraMask;

        // The module is in, its transpiler made the swap the last time it ran, and Harmony still has our transpiler and
        // the depth camera's prefix in place (looked at once a second and after any feature switch).
        public static bool Patched => s_installed && s_patched && HooksInPlace();
        // Grass that casts no shadow reaches the game camera only and is handed on to the cameras nobody excluded: what the
        // next frame's draws will do. What this frame's draws did is TargetedThisFrame.
        public static bool Active => Feature.On && Patched;

        // What this frame's grass pass did, for a consumer that decides after MonoUpdaters.Update and before its cameras
        // render whether to submit grass itself: true when this frame's CustomUpdate draws of clutter that casts no shadow
        // went to the game camera only (an excluded camera then has none of them unless someone submits them: FrameBatch),
        // false when they went out with the null camera (every camera gets them, as in the game). Clutter that casts
        // shadows goes out with the null camera either way. With no grass draw yet this frame it is Active, except in a
        // frame in which the switch flipped (the pass may have run either way, and there is nothing to draw twice: false).
        // A switch in the middle of the pass, which only a Submitted handler could do, reads true. Main thread.
        public static bool TargetedThisFrame
        {
            get
            {
                if (!s_installed)
                    return false;
                int frame = Time.frameCount;
                if (s_targetedFrame == frame)
                    return true;
                return s_switchedFrame != frame && Active;
            }
        }

        // The game's two calls in CustomUpdate (IL_0130 thinned, IL_015c plain), both this overload.
        const int GameSites = 2;
        const string RenderTypeTag = "RenderType";

        // Nothing in a static initializer may throw (a TypeInitializationException would reach every caller of the
        // public members, Real Portal's per-frame reads among them): AccessTools.Method and Field return null when
        // something is missing, and Install checks.
        static readonly MethodInfo s_drawGame = AccessTools.Method(typeof(Graphics), nameof(Graphics.DrawMeshInstanced), new[]
        {
            typeof(Mesh), typeof(int), typeof(Material), typeof(Matrix4x4[]), typeof(int), typeof(MaterialPropertyBlock),
            typeof(ShadowCastingMode), typeof(bool), typeof(int),
        });
        static readonly MethodInfo s_drawOurs = AccessTools.DeclaredMethod(typeof(GrassCameraModule), nameof(Draw));

        // Set by Install (null while the module is out).
        static MethodBase s_target, s_renderDepth;
        static AccessTools.FieldRef<InstanceRenderer, Matrix4x4[]> s_instancesOf;
        static AccessTools.FieldRef<InstanceRenderer, int> s_countOf;
        static AccessTools.FieldRef<InstanceRenderer, BoundingSphere> s_boundsOf;
        static AccessTools.FieldRef<InstanceRenderer, bool> s_dirtyBoundsOf;
        static AccessTools.FieldRef<DepthCamera, Camera> s_depthCameraOf;

        // Every patch went in (a failed install leaves whatever could not be taken back inert), and the transpiler made the
        // swap the last time Harmony ran it.
        static bool s_installed, s_patched;
        static bool s_subscribed;

        // This frame's batches (s_frame; stale ones are cleared before reuse, so dead patches are not kept alive), the
        // layers they are on, and the game camera they went to.
        static Batch[] s_batches = new Batch[256];
        static int s_count;
        static int s_frame = -1;
        static int s_frameLayers;
        static Camera s_main;
        // The game's depth camera, once its prefix ran (to tell its onPreCull apart), and s_preCulls at that prefix's
        // last run (-1: never).
        static Camera s_depthCam;
        static long s_depthCallAt = -1;
        // Every onPreCull while the callback is in (a sequence number: "the next onPreCull after X" is s_preCulls at X + 1).
        static long s_preCulls;
        // The frame of the last draw that went to the game camera only, and of the last switch of the feature.
        static int s_targetedFrame = -1, s_switchedFrame = -1;
        static readonly List<Camera> s_excluded = new List<Camera>();

        static void Install(Harmony harmony)
        {
            // Everything is looked up before anything is patched: a game update that renamed one of them leaves the
            // module out whole (the public members then read false, 0 or null) instead of half-installed (grass for the
            // game camera only, but none for the depth camera).
            MethodInfo target = AccessTools.DeclaredMethod(typeof(InstanceRenderer), nameof(InstanceRenderer.CustomUpdate), new[] { typeof(float), typeof(float) });
            MethodInfo renderDepth = AccessTools.DeclaredMethod(typeof(DepthCamera), "RenderDepth");
            MethodInfo transpiler = AccessTools.DeclaredMethod(typeof(GrassCameraModule), nameof(CustomUpdateTranspiler));
            MethodInfo prefix = AccessTools.DeclaredMethod(typeof(GrassCameraModule), nameof(BeforeRenderDepth));
            if (target == null || renderDepth == null || s_drawGame == null || s_drawOurs == null)
                throw new MissingMethodException("InstanceRenderer.CustomUpdate, DepthCamera.RenderDepth or Graphics.DrawMeshInstanced(Mesh, int, " +
                                                 "Material, Matrix4x4[], int, MaterialPropertyBlock, ShadowCastingMode, bool, int) not found (game or Unity update?)");
            Type ir = typeof(InstanceRenderer);
            var instancesOf = AccessTools.FieldRefAccess<InstanceRenderer, Matrix4x4[]>(Field(ir, "m_instances", typeof(Matrix4x4[])));
            var countOf = AccessTools.FieldRefAccess<InstanceRenderer, int>(Field(ir, "m_instanceCount", typeof(int)));
            var boundsOf = AccessTools.FieldRefAccess<InstanceRenderer, BoundingSphere>(Field(ir, "m_bounds", typeof(BoundingSphere)));
            var dirtyBoundsOf = AccessTools.FieldRefAccess<InstanceRenderer, bool>(Field(ir, "m_dirtyBounds", typeof(bool)));
            var depthCameraOf = AccessTools.FieldRefAccess<DepthCamera, Camera>(Field(typeof(DepthCamera), "m_camera", typeof(Camera)));
            // Ours takes the game's arguments plus the renderer: checked here so a mistake here cannot reach the IL.
            ParameterInfo[] game = s_drawGame.GetParameters(), ours = s_drawOurs.GetParameters();
            bool same = ours.Length == game.Length + 1 && ours[game.Length].ParameterType == typeof(InstanceRenderer) && s_drawOurs.ReturnType == s_drawGame.ReturnType;
            for (int i = 0; same && i < game.Length; i++)
                same = ours[i].ParameterType == game[i].ParameterType;
            if (!same)
                throw new InvalidOperationException("the replacement's signature does not match Graphics.DrawMeshInstanced");
            s_instancesOf = instancesOf;
            s_countOf = countOf;
            s_boundsOf = boundsOf;
            s_dirtyBoundsOf = dirtyBoundsOf;
            s_depthCameraOf = depthCameraOf;
            s_target = target;
            s_renderDepth = renderDepth;

            // The depth camera's prefix first: it does nothing until the transpiler records batches.
            var attempted = new List<KeyValuePair<MethodBase, MethodInfo>>();
            try
            {
                attempted.Add(new KeyValuePair<MethodBase, MethodInfo>(renderDepth, prefix));
                // Last, so another mod's prefix that skips the render has run (__runOriginal) before we hand it grass.
                harmony.Patch(renderDepth, prefix: new HarmonyMethod(prefix) { priority = Priority.Last });
                attempted.Add(new KeyValuePair<MethodBase, MethodInfo>(target, transpiler));
                harmony.Patch(target, transpiler: new HarmonyMethod(transpiler) { priority = Priority.Last });
                if (!s_patched)
                    throw new InvalidOperationException($"InstanceRenderer.CustomUpdate {s_reported}");
            }
            catch
            {
                // Out whole, as the plugin reports: take back what went in (only our own patch methods, since the Harmony
                // instance is shared with the other engine modules). The transpiler, if it stays, has left the method as
                // the game's, and with s_installed false Draw is the game's call.
                s_patched = false;
                foreach (KeyValuePair<MethodBase, MethodInfo> p in attempted)
                {
                    try
                    {
                        harmony.Unpatch(p.Key, p.Value);
                    }
                    catch (Exception e)
                    {
                        EnginePlugin.Log?.LogWarning($"GrassMainCameraOnly: could not take back {p.Value.Name} on {p.Key.Name}: {e.Message}");
                    }
                }
                throw;
            }

            s_installed = true;
            s_hooksChecked = false;
            Feature.Switched += OnSwitched;
            Features.Changed += OnFeatureChanged;
            if (Feature.On)
                OnSwitched(true);
        }

        static FieldInfo Field(Type type, string name, Type fieldType)
        {
            FieldInfo f = AccessTools.DeclaredField(type, name);
            if (f == null || f.IsStatic || f.FieldType != fieldType)
                throw new MissingFieldException(type.Name, name);
            return f;
        }

        static void OnSwitched(bool on)
        {
            s_switchedFrame = Time.frameCount;
            if (!on)
                return;   // the callback stays until this frame's batches have been handed out, then leaves (PreCull)
            Subscribe();
            if (!Patched)
                EnginePlugin.Log?.LogWarning($"GrassMainCameraOnly: switched on, but it does nothing: InstanceRenderer.CustomUpdate {s_reported}" +
                                             (s_hooksInPlace ? "" : "; our patches are no longer in place"));
        }

        // Any switch (another mod's patching often comes with one) looks at Harmony again on the next read.
        static void OnFeatureChanged(Feature f) => s_hooksChecked = false;

        static void Subscribe()
        {
            if (s_subscribed)
                return;
            Camera.onPreCull += PreCull;
            s_subscribed = true;
        }

        static void Unsubscribe()
        {
            if (!s_subscribed)
                return;
            Camera.onPreCull -= PreCull;
            s_subscribed = false;
        }

        // ---- the transpiler -----------------------------------------------------------------------------------

        // Harmony runs every transpiler of a method again whenever any mod patches it, so the result is logged only when
        // it changes.
        static string s_reported = "not patched yet";

        static void Note(string result, bool error)
        {
            if (result == s_reported)
                return;
            s_reported = result;
            if (error)
                EnginePlugin.Log?.LogError("GrassMainCameraOnly: InstanceRenderer.CustomUpdate " + result);
            else
                EnginePlugin.Log?.LogInfo("GrassMainCameraOnly: InstanceRenderer.CustomUpdate " + result);
        }

        // Labels and exception blocks are read by reflection: their types (Label, ExceptionBlock) are kept out of
        // the netstandard2.1 build, as in AllocFixesModule.
        static readonly FieldInfo s_labels = AccessTools.Field(typeof(CodeInstruction), "labels");
        static readonly FieldInfo s_blocks = AccessTools.Field(typeof(CodeInstruction), "blocks");

        static bool Plain(CodeInstruction ci) =>
            (!(s_labels?.GetValue(ci) is System.Collections.ICollection labels) || labels.Count == 0)
            && (!(s_blocks?.GetValue(ci) is System.Collections.ICollection blocks) || blocks.Count == 0);

        //   ...arguments...; call Graphics.DrawMeshInstanced(9 arguments)
        // becomes
        //   ...arguments...; ldarg.0; call GrassCameraModule.Draw(9 arguments, InstanceRenderer)
        // for both of the game's calls, or for neither.
        static IEnumerable<CodeInstruction> CustomUpdateTranspiler(IEnumerable<CodeInstruction> code)
        {
            // Harmony is building the method: its record of the patches is read again once the build is done.
            s_hooksChecked = false;
            var list = new List<CodeInstruction>(code);
            var sites = new List<int>();
            bool jumpedTo = false;
            for (int i = 0; i < list.Count; i++)
                if (list[i].Calls(s_drawGame))
                {
                    sites.Add(i);
                    // ldarg.0 goes in front of the call: a jump landing on the call would skip it.
                    jumpedTo |= !Plain(list[i]);
                }
            if (sites.Count != GameSites || jumpedTo)
            {
                s_patched = false;
                Note((sites.Count != GameSites
                           ? $"has {sites.Count} calls of Graphics.DrawMeshInstanced(mesh, submesh, material, matrices, count, properties, shadows, receive, layer) where the game has {GameSites}"
                           : "has a jump landing on its Graphics.DrawMeshInstanced call")
                       + " (changed by another mod or a game update?); left as the game's, GrassMainCameraOnly does nothing", true);
                return list;
            }
            for (int k = sites.Count - 1; k >= 0; k--)
            {
                CodeInstruction call = list[sites[k]];
                call.opcode = OpCodes.Call;
                call.operand = s_drawOurs;
                list.Insert(sites[k], new CodeInstruction(OpCodes.Ldarg_0));
            }
            s_patched = true;
            Note($"x{GameSites}: grass draws go through GrassMainCameraOnly", false);
            return list;
        }

        // Our transpiler and the depth camera's prefix, as Harmony has them now. Another mod's UnpatchAll() (no id) or
        // Unpatch(method, All) has Harmony rebuild the method without running our transpiler, so s_patched alone would
        // keep what it was. Read at most once a second (from the first grass draw of a frame and from Patched), and again
        // after any feature switch or transpiler run.
        static bool s_hooksInPlace = true, s_hooksChecked;
        static int s_hooksCheckedAt;

        static bool HooksInPlace()
        {
            if (!s_installed)
                return false;
            int now = Environment.TickCount;
            if (s_hooksChecked && unchecked(now - s_hooksCheckedAt) < 1000)
                return s_hooksInPlace;
            s_hooksChecked = true;
            s_hooksCheckedAt = now;
            bool inPlace;
            string why;
            try
            {
                bool transpiler = Ours(Harmony.GetPatchInfo(s_target)?.Transpilers, nameof(CustomUpdateTranspiler));
                bool prefix = Ours(Harmony.GetPatchInfo(s_renderDepth)?.Prefixes, nameof(BeforeRenderDepth));
                inPlace = transpiler && prefix;
                why = !transpiler ? "our transpiler is no longer on InstanceRenderer.CustomUpdate" : "our prefix is no longer on DepthCamera.RenderDepth";
            }
            catch (Exception e)
            {
                // Cannot tell: behave as the game.
                inPlace = false;
                why = "reading Harmony's patch info failed (" + e.Message + ")";
            }
            if (inPlace != s_hooksInPlace)
            {
                if (inPlace)
                    EnginePlugin.Log?.LogInfo("GrassMainCameraOnly: our patches are in place again");
                else
                    EnginePlugin.Log?.LogWarning($"GrassMainCameraOnly: {why} (another mod unpatched it?); the grass draws take the game's path, GrassMainCameraOnly does nothing");
            }
            s_hooksInPlace = inPlace;
            return inPlace;
        }

        static bool Ours(IEnumerable<HarmonyLib.Patch> patches, string name)
        {
            if (patches == null)
                return false;
            foreach (HarmonyLib.Patch p in patches)
            {
                MethodInfo m = p.PatchMethod;
                if (p.owner == EnginePlugin.Guid && m != null && m.DeclaringType == typeof(GrassCameraModule) && m.Name == name)
                    return true;
            }
            return false;
        }

        // ---- the draw -----------------------------------------------------------------------------------------

        // Graphics.DrawMeshInstanced as CustomUpdate calls it, plus the renderer.
        static void Draw(Mesh mesh, int submeshIndex, Material material, Matrix4x4[] matrices, int count, MaterialPropertyBlock properties,
                         ShadowCastingMode castShadows, bool receiveShadows, int layer, InstanceRenderer renderer)
        {
            if (!Feature.On || !s_installed)
            {
                Graphics.DrawMeshInstanced(mesh, submeshIndex, material, matrices, count, properties, castShadows, receiveShadows, layer);
                return;
            }
            if (castShadows != ShadowCastingMode.Off)
            {
                // Clutter that casts shadows: the game's draw, for every camera and its shadow maps (P3(iii) unverified),
                // not recorded (every camera has it already).
                ShadowDraws++;
                Graphics.DrawMeshInstanced(mesh, submeshIndex, material, matrices, count, properties, castShadows, receiveShadows, layer);
                return;
            }
            int frame = Time.frameCount;
            if (frame != s_frame)
                NewFrame(frame);
            Camera main = s_main;
            if (main == null || !s_hooksInPlace)
            {
                // No game camera, or the depth camera's prefix is gone, which then would not get its grass: the game's draw.
                FallbackDraws++;
                Graphics.DrawMeshInstanced(mesh, submeshIndex, material, matrices, count, properties, castShadows, receiveShadows, layer);
                return;
            }
            // The overload with a camera: the same validation and the same defaults (BlendProbes, no proxy volume).
            Graphics.DrawMeshInstanced(mesh, submeshIndex, material, matrices, count, properties, castShadows, receiveShadows, layer, main);
            s_targetedFrame = frame;
            MainDraws++;
            MainInstances += count;

            if (s_count == s_batches.Length)
                Array.Resize(ref s_batches, s_count * 2);
            ref Batch b = ref s_batches[s_count++];
            b.Renderer = renderer;
            b.Mesh = mesh;
            b.Submesh = submeshIndex;
            b.Material = material;
            b.Matrices = matrices;
            b.Count = count;
            b.Properties = properties;
            b.Shadows = castShadows;
            b.ReceiveShadows = receiveShadows;
            b.Layer = layer;
            b.Bounds = s_boundsOf(renderer);
            b.BoundsValid = !s_dirtyBoundsOf(renderer);
            s_frameLayers |= (uint)layer < 32u ? 1 << layer : -1;
            if (s_count > MaxFrameBatches)
                MaxFrameBatches = s_count;

            BatchHandler handler = Submitted;
            if (handler != null)
            {
                try
                {
                    handler(in b);
                }
                catch (Exception e)
                {
                    HookError("a Submitted handler", e);
                }
            }
        }

        // The first draw of a frame: the camera is the game's per-frame one (the same CustomUpdate culled by).
        static void NewFrame(int frame)
        {
            ClearBatches();
            s_frame = frame;
            s_main = Utils.GetMainCamera();
            s_mainRendersInFrame = 0;
            HooksInPlace();
            for (int i = s_excluded.Count - 1; i >= 0; i--)
                if (s_excluded[i] == null)
                    s_excluded.RemoveAt(i);
        }

        static void ClearBatches()
        {
            ResetServed();
            s_frameLayers = 0;
            if (s_count == 0)
                return;
            LastFrameBatches = s_count;
            Array.Clear(s_batches, 0, s_count);
            s_count = 0;
        }

        static bool HaveFrameBatches => s_count > 0 && s_frame == Time.frameCount;

        // ---- the other cameras --------------------------------------------------------------------------------

        // What a camera already got this frame: one bit per batch (GrassResubmitEachRender off), and, when it was given its
        // batches ahead of a render (the depth camera's prefix, SubmitFrameTo), s_preCulls at that moment (-1: not, or
        // used up). With GrassResubmitEachRender on, the next onPreCull, if it is this camera's, is that render's and adds
        // none; any later one is another render's (the one given ahead raised no onPreCull) and gets its batches.
        sealed class Served
        {
            public Camera Camera;
            public ulong[] Sent = new ulong[8];
            public int Words;
            public long AheadAt = -1;
        }

        static readonly List<Served> s_served = new List<Served>();
        static int s_servedCount;

        static Served Record(Camera cam, bool create)
        {
            for (int i = 0; i < s_servedCount; i++)
                if (ReferenceEquals(s_served[i].Camera, cam))
                    return s_served[i];
            if (!create)
                return null;
            if (s_servedCount == s_served.Count)
                s_served.Add(new Served());
            Served s = s_served[s_servedCount++];
            s.Camera = cam;
            s.Words = 0;
            s.AheadAt = -1;
            return s;
        }

        static void ResetServed()
        {
            for (int i = 0; i < s_servedCount; i++)
                s_served[i].Camera = null;
            s_servedCount = 0;
        }

        // The game's sky-occlusion picture: rendered from InvokeRepeating after MonoUpdaters.Update, so in the game it
        // draws the batches submitted earlier in its frame (and none when it renders in a frame without them).
        static void BeforeRenderDepth(DepthCamera __instance, bool __runOriginal)
        {
            if (!__runOriginal || !s_installed || !HaveFrameBatches)
                return;
            Camera cam = s_depthCameraOf(__instance);
            // As RenderDepth itself, which renders nothing without a game camera.
            if (cam == null || Utils.GetMainCamera() == null)
                return;
            s_depthCam = cam;
            s_depthCallAt = s_preCulls;
            DepthCalls++;
            if (IsExcluded(cam))
                return;
            Shader replacement = __instance.m_depthShader;
            NoteDepth(cam, replacement);
            NoteMaterials(replacement);
            // Its RenderWithShader raises onPreCull (seen at an earlier call): PreCull hands it the batches inside the
            // render, as every other camera, and nothing is queued for it before.
            if (DepthRaisesPreCull)
                return;
            // Not seen (yet): now. No view test: RenderDepth puts the camera above the player only after this prefix. If
            // the render does raise onPreCull, that one adds nothing (Served.AheadAt, or the bits) and tells PreCull.
            if (Submit(cam, false, true) > 0)
                DepthRenders++;
        }

        // Before every camera culls, while the feature is on (and on the frame it was switched off): every camera but the
        // game camera and the excluded ones gets this frame's batches.
        static void PreCull(Camera cam)
        {
            long seq = ++s_preCulls;
            int frame = Time.frameCount;
            if (s_frame != frame)
            {
                // No grass draw yet this frame: last frame's batches are let go (dead patches are not kept alive), and once
                // the feature is off the callback leaves.
                ClearBatches();
                if (!Feature.On)
                {
                    Unsubscribe();
                    return;
                }
            }
            if ((object)cam == null)
                return;
            bool reflection = cam.cameraType == CameraType.Reflection;
            bool depth = !reflection && ReferenceEquals(cam, s_depthCam);
            if (reflection)
                NoteReflection(cam, frame);
            else if (depth)
            {
                DepthPreCulls++;
                // The first onPreCull after our prefix on RenderDepth, and the depth camera's: its RenderWithShader's (the
                // method renders nothing else in between).
                if (s_depthCallAt == seq - 1 && !DepthRaisesPreCull)
                {
                    DepthRaisesPreCull = true;
                    EnginePlugin.Log?.LogInfo("GrassMainCameraOnly: the game's DepthCamera raises onPreCull in RenderWithShader; it is handed its grass there from now on");
                }
            }
            if (s_count == 0 || s_frame != frame)
                return;
            if (ReferenceEquals(cam, s_main))
            {
                MainRender(cam);
                return;
            }
            // Lab (GrassExcludeOthers): the game's own cameras only.
            if (IsExcluded(cam) || (ExcludeOthersFeature.On && !reflection && !depth))
            {
                ExcludedRenders++;
                return;
            }
            if (ResubmitFeature.On)
            {
                Served given = Record(cam, false);
                if (given != null && given.AheadAt >= 0)
                {
                    // Given its batches right before a render (the depth camera's prefix, SubmitFrameTo): this is that
                    // render only if nothing else raised onPreCull since.
                    bool thatRender = given.AheadAt == seq - 1;
                    given.AheadAt = -1;
                    if (thatRender)
                        return;
                }
            }
            // No view test for the depth camera (every batch in its mask, as in the game) nor for probe cameras (P3(iv)).
            int n = Submit(cam, FrustumFeature.On && !reflection && !depth, false);
            if (n > 0)
            {
                if (reflection)
                    ReflectionRenders++;
                else if (depth)
                    DepthRenders++;
                else
                    OtherRenders++;
            }
        }

        // The game camera got this frame's batches from CustomUpdate itself. Its renders after that in the frame (a
        // screenshot or capture mod's Camera.main.Render(), a multi-pass mod) are counted; with GrassResubmitEachRender
        // on (the default) the second and later get them again, in case the first took them (with it off they are taken
        // to be still queued for them).
        static int s_mainRendersInFrame;

        static void MainRender(Camera cam)
        {
            if (++s_mainRendersInFrame < 2)
                return;
            MainRerenders++;
            if (ResubmitFeature.On)
                Submit(cam, FrustumFeature.On, false);
        }

        static bool IsExcluded(Camera cam)
        {
            for (int i = 0; i < s_excluded.Count; i++)
                if (ReferenceEquals(s_excluded[i], cam))
                    return true;
            return false;
        }

        // This frame's batches for one more camera: those its culling mask takes and, with view, not those whose culling
        // sphere is outside its frustum (the game's own test, Utils.InsideMainCamera, for this camera: DrawMeshInstanced
        // is not culled by Unity, so a batch outside the view adds only cost; every recorded batch casts no shadow, and the
        // check stays). With GrassResubmitEachRender off a camera gets each batch once per frame: what it got is taken to
        // stay queued for its later renders, so only what it did not get yet goes. ahead: given before a render (the
        // depth camera's prefix, SubmitFrameTo), so with GrassResubmitEachRender on the next onPreCull, if it is this
        // camera's (that render's), adds no second copy.
        static readonly Plane[] s_planes = new Plane[6];
        static int s_submitFrame = -1, s_resubmitFrame = -1;
        static long s_submitFrameTicks;

        static int Submit(Camera cam, bool view, bool ahead)
        {
            int mask = cam.cullingMask;
            if ((mask & s_frameLayers) == 0)
                return 0;   // the sky camera, the clear camera: nothing of this frame is in their mask
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            bool persist = !ResubmitFeature.On;
            Served given = persist || ahead ? Record(cam, true) : null;
            ulong[] sent = null;
            if (persist)
            {
                int words = (s_count + 63) >> 6;
                if (given.Sent.Length < words)
                    Array.Resize(ref given.Sent, Math.Max(words, given.Sent.Length * 2));
                if (given.Words < words)
                {
                    Array.Clear(given.Sent, given.Words, words - given.Words);
                    given.Words = words;
                }
                sent = given.Sent;
            }
            if (ahead)
                given.AheadAt = s_preCulls;
            if (view)
                GeometryUtility.CalculateFrustumPlanes(cam, s_planes);
            int n = 0;
            for (int i = 0; i < s_count; i++)
            {
                ulong bit = 1UL << (i & 63);
                if (persist && (sent[i >> 6] & bit) != 0)
                    continue;
                ref Batch b = ref s_batches[i];
                if (b.Mesh == null || b.Material == null)
                    continue;
                if ((uint)b.Layer < 32u && (mask & (1 << b.Layer)) == 0)
                {
                    SkippedByMask++;
                    continue;
                }
                if (view && b.BoundsValid && b.Shadows == ShadowCastingMode.Off && Outside(in b.Bounds))
                {
                    SkippedByView++;
                    continue;
                }
                if (persist)
                    sent[i >> 6] |= bit;
                try
                {
                    Graphics.DrawMeshInstanced(b.Mesh, b.Submesh, b.Material, b.Matrices, b.Count, b.Properties, b.Shadows, b.ReceiveShadows, b.Layer, cam);
                    n++;
                }
                catch (Exception e)
                {
                    HookError("submitting again to " + cam.name, e);
                }
            }
            long ticks = System.Diagnostics.Stopwatch.GetTimestamp() - start;
            SubmitTicks += ticks;
            if (s_submitFrame != s_frame)
            {
                s_submitFrame = s_frame;
                s_submitFrameTicks = 0;
            }
            s_submitFrameTicks += ticks;
            if (s_submitFrameTicks > MaxFrameSubmitTicks)
                MaxFrameSubmitTicks = s_submitFrameTicks;
            if (n > 0)
            {
                Resubmitted += n;
                if (s_resubmitFrame != s_frame)
                {
                    s_resubmitFrame = s_frame;
                    ResubmitFrames++;
                }
            }
            return n;
        }

        // Utils.InsideMainCamera(BoundingSphere), negated, on the planes of the camera being served.
        static bool Outside(in BoundingSphere sphere)
        {
            for (int i = 0; i < s_planes.Length; i++)
                if (s_planes[i].GetDistanceToPoint(sphere.position) < -sphere.radius)
                    return true;
            return false;
        }

        // ---- lab notes on the game's cameras ------------------------------------------------------------------

        // Unity's replacement rendering by tag: an object is drawn with the first subshader of the replacement whose tag
        // has the object's value, and not at all when there is none. Only for the log and DepthGrassMaterials: the depth
        // camera is handed every batch in its mask and Unity decides at the render, as in the game. Per material, looked at
        // again when its shader changes (a RenderType override or a maximumLOD change on the same shader is not seen, which
        // can only leave the log stale, never the picture).
        struct Verdict
        {
            public Shader Shader;
            public bool Draws;
        }

        static Shader s_replacementSeen;
        static readonly Dictionary<Material, Verdict> s_replacementDraws = new Dictionary<Material, Verdict>();

        static void NoteMaterials(Shader replacement)
        {
            if (replacement == null)
                return;
            if (!ReferenceEquals(replacement, s_replacementSeen) || s_replacementDraws.Count >= 1024)
            {
                // A new shader, or (materials made at runtime) a cache grown past any real clutter set: start over.
                s_replacementDraws.Clear();
                s_replacementSeen = replacement;
                DepthMaterialsSeen = DepthGrassMaterials = 0;
            }
            Material last = null;
            for (int i = 0; i < s_count; i++)
            {
                Material material = s_batches[i].Material;
                if (ReferenceEquals(material, last) || material == null)
                    continue;
                last = material;
                NoteMaterial(replacement, material);
            }
        }

        static void NoteMaterial(Shader replacement, Material material)
        {
            Shader shader = material.shader;
            bool known = s_replacementDraws.TryGetValue(material, out Verdict v);
            if (known && ReferenceEquals(v.Shader, shader))
                return;
            if (known)
            {
                DepthMaterialsSeen--;
                if (v.Draws)
                    DepthGrassMaterials--;
            }
            string active = "?", found = "?";
            bool draws;
            try
            {
                active = material.GetTag(RenderTypeTag, false);
                found = material.GetTag(RenderTypeTag, true);
                draws = HasSubshaderFor(replacement, active) || HasSubshaderFor(replacement, found);
            }
            catch (Exception e)
            {
                HookError("reading the RenderType of " + material.name, e);
                draws = true;
            }
            s_replacementDraws[material] = new Verdict { Shader = shader, Draws = draws };
            DepthMaterialsSeen++;
            if (draws)
                DepthGrassMaterials++;
            if (DepthMaterialsSeen <= 64)
                EnginePlugin.Log?.LogInfo($"GrassMainCameraOnly: DepthCamera, material '{material.name}' (shader '{(shader != null ? shader.name : "none")}', " +
                                          $"RenderType '{active}'{(found != active ? $" / '{found}' through fallbacks" : "")}): " +
                                          (draws ? "the replacement shader has a subshader for it, so the game's sky-occlusion picture has it"
                                                 : "the replacement shader has no subshader for it, never in the picture"));
        }

        static bool HasSubshaderFor(Shader replacement, string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;
            var tag = new ShaderTagId(RenderTypeTag);
            for (int i = 0, n = replacement.subshaderCount; i < n; i++)
                if (replacement.FindSubshaderTagValue(i, tag).name == value)
                    return true;
            return false;
        }

        // Lab: the game's depth camera, logged at its first render with batches and whenever its mask or shader changes
        // (the materials are logged by NoteMaterial as they come).
        static Shader s_depthShaderNoted;

        static void NoteDepth(Camera cam, Shader replacement)
        {
            int mask = cam.cullingMask;
            if (DepthCameraSeen && mask == DepthCameraMask && ReferenceEquals(replacement, s_depthShaderNoted))
                return;
            DepthCameraSeen = true;
            DepthCameraMask = mask;
            s_depthShaderNoted = replacement;
            int layer = s_batches[0].Layer;
            bool inMask = (uint)layer < 32u && (mask & (1 << layer)) != 0;
            string tags = "";
            try
            {
                if (replacement != null)
                {
                    var parts = new List<string>();
                    var tag = new ShaderTagId(RenderTypeTag);
                    for (int i = 0, n = replacement.subshaderCount; i < n; i++)
                        parts.Add(replacement.FindSubshaderTagValue(i, tag).name);
                    tags = string.Join(", ", parts.ToArray());
                }
            }
            catch (Exception e)
            {
                tags = "unreadable: " + e.Message;
            }
            EnginePlugin.Log?.LogInfo($"GrassMainCameraOnly: the game's DepthCamera '{cam.name}': culling mask 0x{mask:X8}, InstanceRenderer layer {layer} " +
                                      (inMask ? "in it" : "not in it (the sky-occlusion picture never had grass; DepthRenders stays 0)") +
                                      $"; replacement shader '{(replacement != null ? replacement.name : "none")}', subshader RenderTypes [{tags}]");
        }

        // Lab: reflection cameras. Every onPreCull is counted (and the most in one frame); each culling mask is logged the
        // first time it is seen (Unity's probe camera may take each probe's mask in turn), and the game's probes once.
        static int s_reflectionFrame = -1, s_reflectionsInFrame;
        static readonly List<int> s_reflectionMasks = new List<int>();
        static bool s_probesLogged;
        static int s_instanceLayer = -2;

        static void NoteReflection(Camera cam, int frame)
        {
            ReflectionPreCulls++;
            if (s_reflectionFrame != frame)
            {
                s_reflectionFrame = frame;
                s_reflectionsInFrame = 0;
            }
            if (++s_reflectionsInFrame > MaxReflectionPreCullsPerFrame)
                MaxReflectionPreCullsPerFrame = s_reflectionsInFrame;
            if (!s_probesLogged)
                LogGameProbes();
            int mask = cam.cullingMask;
            ReflectionCameraSeen = true;
            ReflectionCameraMask = mask;
            if (s_reflectionMasks.Contains(mask) || s_reflectionMasks.Count >= 16)
                return;
            s_reflectionMasks.Add(mask);
            if (s_instanceLayer == -2)
                s_instanceLayer = LayerMask.NameToLayer("InstanceRenderer");
            int layer = s_instanceLayer;
            bool inMask = (uint)layer < 32u && (mask & (1 << layer)) != 0;
            EnginePlugin.Log?.LogInfo($"GrassMainCameraOnly: reflection camera '{cam.name}' culling mask 0x{mask:X8}: InstanceRenderer layer {layer} " +
                                      (inMask ? "in it, these probe renders draw grass" : "not in it, these probe renders never drew grass (ReflectionRenders 0 for them)"));
        }

        static void LogGameProbes()
        {
            ReflectionUpdate update = ReflectionUpdate.instance;
            if (update == null)
                return;
            s_probesLogged = true;
            try
            {
                EnginePlugin.Log?.LogInfo($"GrassMainCameraOnly: the game's reflection probes: {DescribeProbe(update.m_probe1)}; {DescribeProbe(update.m_probe2)}");
            }
            catch (Exception e)
            {
                HookError("reading the game's reflection probes", e);
            }
        }

        static string DescribeProbe(ReflectionProbe probe) =>
            probe == null ? "none" : $"'{probe.name}' {probe.mode}/{probe.refreshMode}, time slicing {probe.timeSlicingMode}, culling mask 0x{probe.cullingMask:X8}";

        static void HookError(string where, Exception e)
        {
            HookErrors++;
            if (HookErrors <= 10)
                EnginePlugin.Log?.LogError($"GrassMainCameraOnly: {where} failed: {e}");
        }

        // ---- API ----------------------------------------------------------------------------------------------

        // Batches the game camera got this frame (0 while off, while the module is out, or before CustomUpdate has run
        // this frame).
        public static int FrameBatchCount => HaveFrameBatches ? s_count : 0;

        public static ref readonly Batch FrameBatch(int index)
        {
            if ((uint)index >= (uint)FrameBatchCount)
                throw new ArgumentOutOfRangeException(nameof(index));
            return ref s_batches[index];
        }

        // The camera this frame's batches went to (Utils.GetMainCamera()), or null when there are none.
        public static Camera FrameCamera => HaveFrameBatches ? s_main : null;

        // Gives this camera the game camera's grass for this frame now (what its culling mask takes; no view test, since
        // the caller may still move it), as the depth camera gets it before a render that raises no onPreCull: for a
        // camera that renders without raising onPreCull (RenderWithShader, if DepthPreCulls stays 0), or an excluded one
        // that should get it this once. Call it after MonoUpdaters.Update and right before the camera renders, and only
        // when that render follows (P3(ii) 'leak': a draw for a camera that did not render may show at its next render).
        // With GrassResubmitEachRender on (the default) it submits on every call, and the next onPreCull, if it is this
        // camera's, is taken as that render's and adds none. So a render of the same camera that comes right after one
        // that raised no onPreCull, with no other camera's onPreCull in between, gets nothing from onPreCull either: call
        // SubmitFrameTo before every render of such a camera. With it off a camera gets each batch once per frame (later
        // calls, and onPreCull, add only what it did not get). 0 for the game camera (CustomUpdate gave it its batches).
        // Returns the batches submitted. Main thread.
        public static int SubmitFrameTo(Camera camera)
        {
            if (!s_installed || camera == null || !HaveFrameBatches || ReferenceEquals(camera, s_main))
                return 0;
            int n = Submit(camera, false, true);
            if (n > 0)
                OtherRenders++;
            return n;
        }

        // While the feature is on, this camera gets nothing from here (onPreCull, the depth camera's prefix): for a mod
        // that submits that camera's grass itself (Real Portal's window-slot cameras from S2). Kept until removed or
        // destroyed. The game camera cannot be excluded (it gets its grass from CustomUpdate). Main thread.
        public static void ExcludeCamera(Camera camera)
        {
            if (camera == null || IsExcluded(camera))
                return;
            s_excluded.Add(camera);
        }

        public static void RemoveExcludedCamera(Camera camera)
        {
            for (int i = s_excluded.Count - 1; i >= 0; i--)
                if (ReferenceEquals(s_excluded[i], camera))
                    s_excluded.RemoveAt(i);
        }

        // A renderer's own instances (private in the game): the array and how many of it are in use. Read only. Null and 0
        // while the module is out.
        public static void GetInstances(InstanceRenderer renderer, out Matrix4x4[] matrices, out int count)
        {
            if (!s_installed || renderer == null)
            {
                matrices = null;
                count = 0;
                return;
            }
            matrices = s_instancesOf(renderer);
            count = s_countOf(renderer);
        }

        // How many of a renderer's instances the game would draw for a camera at viewPos: CustomUpdate's thinning target
        // without its per-frame smoothing (m_lodCount follows the game camera only). It reads the renderer's LOD distances,
        // so not from inside Submitted while Real Portal's legacy prefix may have moved them (use the overload there). 0
        // while the module is out.
        public static int ViewCount(InstanceRenderer renderer, Vector3 viewPos) =>
            !s_installed || renderer == null ? 0 : ViewCount(renderer, viewPos, renderer.m_lodMinDistance, renderer.m_lodMaxDistance);

        // The same with the LOD distances given (the renderer's own, as ClutterSystem set them).
        public static int ViewCount(InstanceRenderer renderer, Vector3 viewPos, float lodMinDistance, float lodMaxDistance)
        {
            if (!s_installed || renderer == null)
                return 0;
            int count = s_countOf(renderer);
            if (!renderer.m_useLod)
                return count;
            Vector3 here = renderer.transform.position;
            float d = renderer.m_useXZLodDistance ? Utils.DistanceXZ(viewPos, here) : Vector3.Distance(viewPos, here);
            int target = (int)((1f - Utils.LerpStep(lodMinDistance, lodMaxDistance, d)) * count);
            return Mathf.Clamp(target, 0, count);
        }

        public static string Report() =>
            $"GrassMainCameraOnly {(Feature.On ? "on" : "off")}, {(Patched ? "patched" : "NOT patched")}; game camera only: {MainDraws} draws, " +
            $"{MainInstances} instances; shadow casters left to every camera {ShadowDraws}; batches last frame {LastFrameBatches}, max {MaxFrameBatches}; " +
            $"submitted again: {Resubmitted} (depth renders {DepthRenders} of {DepthCalls} calls, depth onPreCull {DepthPreCulls}" +
            $"{(DepthRaisesPreCull ? ", served there" : "")}; reflection {ReflectionRenders} of {ReflectionPreCulls} onPreCull, " +
            $"max {MaxReflectionPreCullsPerFrame}/frame; other {OtherRenders}; game camera again {MainRerenders}; left out by mask {SkippedByMask}, by view {SkippedByView}; " +
            $"excluded renders {ExcludedRenders}; {SubmitMs:0.0} ms in {ResubmitFrames} frames, worst frame {MaxFrameSubmitMs:0.00} ms" +
            $"{(ResubmitEachRender ? "; every render" : "; once per camera per frame")}{(FrustumFeature.On ? "" : "; no view test")}" +
            $"{(ExcludeOthersFeature.On ? "; game cameras only (lab)" : "")}); fallback {FallbackDraws}; hook errors {HookErrors}; " +
            $"excluded cameras {s_excluded.Count}; " +
            $"depth camera {(DepthCameraSeen ? $"mask 0x{DepthCameraMask:X8}, a subshader for {DepthGrassMaterials} of {DepthMaterialsSeen} materials" : "unseen")}; " +
            $"reflection camera {(ReflectionCameraSeen ? $"mask 0x{ReflectionCameraMask:X8}" : "unseen")}";
    }
}
