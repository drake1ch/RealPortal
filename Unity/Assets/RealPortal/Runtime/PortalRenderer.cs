using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace RealPortal
{
    // How the window camera drops what lies between it and the exit portal.
    public enum PortalClip
    {
        // Near plane just short of the opening on an ordinary (lens-shifted) projection. The game's deferred
        // lighting, shadows, fog and depth effects all work as for the game camera; something standing right
        // behind the exit portal but outside the near plane is hidden by the caller (BeforeRender).
        NearPlane,
        // Lengyel oblique near plane on the portal plane: exact clipping, but the skewed depth breaks deferred
        // lighting, screen-space shadows and every effect that reads depth (fog, water, soft particles).
        Oblique
    }

    public sealed class PortalRenderSettings
    {
        public float Resolution = 1f;        // render-texture size relative to the screen
        public float MaxDistance = 60f;      // windows farther than this from the camera are not rendered
        public float FarClip = float.MaxValue; // draw distance of the window camera (capped by the main camera's)
        public float ShadowDistance = -1f;   // shadow distance while the window camera renders (<0: keep)
        public float LodBias = 1f;           // multiplier for QualitySettings.lodBias while rendering
        public bool LodMatchesGame = true;   // correct the LOD bias for the window rect's narrower field of view
        public int Recursion;                // 0: a window seen through a window shows the fallback; 1: one more level
        // WindowFlags.NestedLive (Docs\PARITY-PLAN.md P7): the other live windows a window's view shows render at level 1,
        // largest first, at most NestedPerFrame a frame and while their renders so far this frame took under NestedMs of
        // main thread (and at most PortalRenderer.NestedTable in one view); the rest show the vanilla look there.
        public int NestedPerFrame = 4;
        public float NestedMs = 3f;
        public float MinScreenFraction = 0.0005f;
        // WindowFlags.DistantLive (Docs\PARITY-PLAN.md P2): instead of MinScreenFraction, a window renders while its opening
        // is at least this many pixels tall and wide on the main camera's screen.
        public float MinPixels = 3f;
        // Lab (PortalRenderer.LabDistantRenders): a render counts as distant beyond this distance (m) from the camera (the
        // [Window] MaxDistance default that bounded windows before WindowFlags.DistantLive), or under MinScreenFraction.
        public float DistantFrom = 60f;
        // Small and far windows refreshed every few frames, shown in between from their last render laid onto the
        // opening (as it was seen then). Windows covering at least RefreshAlwaysShare of the screen, or close to
        // the camera, render every frame; at most MaxRendersPerFrame renders a frame (0: no limit) beyond those.
        public bool RefreshSmallLess = true;
        public float RefreshAlwaysShare = 0.05f;
        public int MaxRendersPerFrame = 0;
        // WindowFlags.LiveShare: where the player's eyes are this frame (HasViewer), set by the game side; a window within
        // 8 m of them renders every frame as one within 8 m of the camera does.
        public Vector3 Viewer;
        public bool HasViewer;
        public CameraClearFlags ClearFlags = CameraClearFlags.Skybox;
        // UsePlayerSettings: whatever the main camera uses.
        public RenderingPath RenderingPath = RenderingPath.UsePlayerSettings;
        public PortalClip Clip = PortalClip.NearPlane;
        // Give the main camera the depth of what is seen through each window (its fog, SSAO, DOF, sun shafts,
        // TAA and motion blur then treat the view as real scenery).
        public bool VirtualDepth = true;
        // Optional extra near-plane distance for a window render (entry window, camera position, world->camera):
        // e.g. to cut the player's body exactly at the exit plane while it is passing through.
        public Func<PortalWindow, Vector3, Matrix4x4, float> MinNear;
        // While the game's opaque image effects run (its fog and SSAO), the window reads as sky (or, without
        // EffectsSeeWindowAsSky, as right at the camera): the window camera applies those effects to its view
        // itself, with its own view directions (the game tints fog by where the sun is).
        public bool FogSeesNearWindow = true;
        // Sky is skipped by both the game's fog (when it excludes the skybox) and its SSAO.
        public bool EffectsSeeWindowAsSky = true;
        // Write the motion of what is seen through each window into the game camera's motion vectors (TAA and
        // motion blur would otherwise move the view like the window's own surface, right in front of the camera).
        public bool MotionVectors = true;
        // Mark where each window shows sky (SkyDepthId), for sun shafts drawn by the game camera from the sun seen
        // through the window: after the game's colour grading, exactly like its own shafts.
        public bool WindowSunShafts = true;
        // Fill ViewDepthTexture (the depth seen through the windows) for the game's depth of field.
        public bool ViewDepthForEffects = true;
        // Also make ViewDepthTexture the camera's depth texture for its final image effects (TAA, motion blur).
        public bool ViewDepthForFinalEffects = true;
        // Diagnostics: compute the whole-screen motion vectors (as on a jump through a portal) on every frame.
        public bool MotionScreenAlways;
        // Diagnostics: the earlier, strict test for a jump through a portal (within 1 m).
        public bool StrictJump;
        // Renderers that move along with the main camera (the player): on the frame the camera is carried through
        // a portal they keep their own motion vectors.
        public Func<IEnumerable<Renderer>> KeepOwnMotion;
        // Sub-pixel offset of the main camera's projection this frame (TAA jitter), in screen units (0..1). The
        // views shown on screen get the same offset, or TAA un-jitters content that was never jittered and fine
        // detail in the window shimmers.
        public Vector2 Jitter;
        // Render each window view over the whole screen instead of just its rect: costs more, but screen-space
        // effects of the window camera (sun shafts, fog rays) then work exactly as for the game camera.
        public bool FullFrame;
        // Extra margin around a window's rect (fraction of the screen height, each side): screen-space effects of
        // the window camera (ambient occlusion, blur) need pixels around what they shade. With WindowFlags.Margin
        // at most a quarter of the rect's own width and height, but never less than EffectRadius reaches on screen.
        public float Margin;
        // World radius (m) of the window camera's widest screen-space kernel (its SSAO); 0: it has none. With
        // WindowFlags.Margin the margin keeps at least that radius as seen at the nearest depth through the window,
        // so the effect along the window's inner edge samples what was rendered, as with the full margin.
        public float EffectRadius;
        // WindowFlags.EnvGroups: the group a window's view belongs to (its far area). Renders of one group run back
        // to back, between PortalRenderer.BeforeGroup and AfterGroup. Null: every render is a group of its own.
        public Func<PortalWindow, int> GroupOf;
    }

    // Renders every visible window from the matching viewpoint behind its partner (portal culling):
    // only portals in front of the camera and within range are drawn, each through a projection covering
    // just the window's screen rect (scissor), at the chosen resolution and draw distance.
    // Each view goes into its own temporary texture the size of that rect, drawn with the full viewport:
    // a sub-rect viewport (camera.rect) misaligns the depth texture that the game's soft particles, fog
    // planes and water read, which drew mist over everything in the window.
    public sealed partial class PortalRenderer
    {
        public const string ViewportProp = "_RP_Viewport";

        public readonly PortalRenderSettings Settings;
        // Called around each window render with the entry window, the entry->exit matrix and the camera position. For a
        // window rendered inside another window's view (WindowFlags.NestedLive) the matrix is the main camera's world to
        // that render's: its pair times the view's matrix it is seen in.
        public Action<PortalWindow, Matrix4x4, Vector3> BeforeRender, AfterRender;
        // WindowFlags.EnvGroups (only while it is on and one of them is set): the renders of this frame come in
        // groups (Settings.GroupOf, e.g. one far area), each run back to back; BeforeGroup gets the group's first
        // window before its first render (before that render's BeforeRender), AfterGroup its last window after its
        // last render (after AfterRender), also when a render throws. RenderDebug makes one group of its window.
        public Action<PortalWindow> BeforeGroup, AfterGroup;
        // True only while the window camera renders (inside Camera.Render): the game's SSAO allocates then.
        public static bool InWindowRender { get; private set; }
        // The recursion level of that render (0: the view shown on screen, in 32 px size classes; deeper levels
        // have sizes of their own every frame and use Unity's temporary textures, as before WindowFlags.RTPool).
        public static int WindowRenderLevel { get; private set; }
        // WindowFlags.LayerCull: per-layer cull distances (32, spherical) for the window camera, set by the game
        // side; null leaves the camera as copied from the game camera.
        public static float[] WindowLayerCull;
        public int RendersLastFrame { get; private set; }
        // Culling frustum of every window view rendered this frame (what the game should keep alive and drawn).
        public readonly List<Plane[]> Frusta = new List<Plane[]>();
        // Where each of those views was rendered from (same order as Frusta).
        public readonly List<Vector3> ViewPositions = new List<Vector3>();
        // Lab: windows not rendered this frame leave no view for the grass (as before that was fixed).
        internal static bool LabNoStaleViews;
        // Lab (Docs\PARITY-PLAN.md P2): on-screen renders since start of windows only WindowFlags.DistantLive lets render
        // (beyond Settings.DistantFrom or under MinScreenFraction), and the windows the main camera has in view this frame
        // that show the vanilla look (WindowFlags.VanillaLook: drawn as nothing, the game's empty ring).
        internal static long LabDistantRenders;
        internal static int LabVanillaLook;
        // Lab (Docs\PARITY-PLAN.md P7): windows seen through a window rendered at level 1 (WindowFlags.NestedLive) since
        // start, and those a view showed that were not (over Settings.NestedPerFrame or NestedMs, past the shader's table,
        // or a render that failed): the vanilla look there.
        internal static long LabNestedRenders, LabNestedSkipped;
        readonly Camera _cam;
        readonly List<RenderTexture> _temps = new List<RenderTexture>();
        readonly List<(PortalWindow w, Rect rect, float share, int every, int age)> _todo = new List<(PortalWindow, Rect, float, int, int)>();
        // This frame's renders in the order they run (grouped when WindowFlags.EnvGroups), and a scratch copy.
        readonly List<(PortalWindow w, Rect rect, int group)> _due = new List<(PortalWindow, Rect, int)>();
        readonly List<(PortalWindow w, Rect rect, int group)> _dueSorted = new List<(PortalWindow, Rect, int)>();
        // Win.LiveFill: this frame's windows off their cadence that may render anyway (indices into _todo), largest first.
        readonly List<(int k, float share)> _fill = new List<(int, float)>();
        // Most overdue (by how many refreshes it is behind) and largest first.
        static readonly Comparison<(PortalWindow w, Rect rect, float share, int every, int age)> s_todoOrder = (a, b) =>
        {
            int pa = a.every == 1 ? 0 : 1, pb = b.every == 1 ? 0 : 1;
            if (pa != pb)
                return pa.CompareTo(pb);
            float da = a.age / (float)a.every, db = b.age / (float)b.every;
            return da != db ? db.CompareTo(da) : b.share.CompareTo(a.share);
        };
        static readonly int s_inWindowRender = Shader.PropertyToID("_RP_InWindowRender");
        static readonly int s_renderingWindowId = Shader.PropertyToID("_RP_RenderingWindowId");
        // Win.PointDepth: the global the window shader reads, and what it was last set to.
        static readonly int s_pointDepth = Shader.PropertyToID("_RP_PointDepth");
        static bool s_pointDepthOn;
        // Win.VanillaLook: the same for the shader's discard of a window that shows nothing of its own.
        static readonly int s_fallbackMode = Shader.PropertyToID("_RP_FallbackMode");
        static bool s_fallbackModeOn;
        // Win.NestedLive (P7): the window shader's table of the windows a level-0 render shows at level 1 (_RP_NestedIds,
        // 0 = an empty entry; each entry's view and the screen rect it covers), set for just that render.
        internal const int NestedTable = 4;
        static readonly int s_nestedIds = Shader.PropertyToID("_RP_NestedIds");
        static readonly int[] s_nestedTex =
        {
            Shader.PropertyToID("_RP_NestedTex0"), Shader.PropertyToID("_RP_NestedTex1"),
            Shader.PropertyToID("_RP_NestedTex2"), Shader.PropertyToID("_RP_NestedTex3"),
        };
        static readonly int[] s_nestedRect =
        {
            Shader.PropertyToID("_RP_NestedRect0"), Shader.PropertyToID("_RP_NestedRect1"),
            Shader.PropertyToID("_RP_NestedRect2"), Shader.PropertyToID("_RP_NestedRect3"),
        };
        // This view's table (RenderNested fills it, RenderLevel sets it around the render), the candidates it was chosen
        // from (largest first) and the level-0 view's culling planes.
        Vector4 _nestedIds;
        readonly RenderTexture[] _nestedViews = new RenderTexture[NestedTable];
        readonly Vector4[] _nestedRects = new Vector4[NestedTable];
        readonly PortalWindow[] _nestedWins = new PortalWindow[NestedTable];
        // The table's windows (and the entry's own deeper level) put back after the level-0 view's opaque effects
        // (RestoreNested), and the window shader's RESTOREVIEW pass for it (-1: a bundle without it), found per shader.
        readonly CommandBuffer _nestedRestore = new CommandBuffer { name = "RealPortal nested windows without the view's effects" };
        Shader _restoreShader;
        int _restorePass = -1;
        readonly List<(PortalWindow w, Rect rect, float share)> _nestedCandidates = new List<(PortalWindow, Rect, float)>(8);
        readonly Plane[] _nestedPlanes = new Plane[6];
        // This frame's nested renders so far and their main-thread time (Settings.NestedPerFrame, NestedMs), and how far past
        // NestedMs a window rendered nested last frame keeps its turn (RenderNested).
        int _nestedFrame = -100, _nestedCount;
        double _nestedMs;
        const float NestedKeep = 1.5f;
        // A bundle with the parity passes (the nested table, the fog copy's mode 3, the probe discard; ShaderHasParity).
        Shader _parityShader;
        bool _parityShaderOk;
        // Win.ExplicitJitter: some window camera had its projection set explicitly (with nonJitteredProjectionMatrix);
        // every other render from then on sets its unjittered projection too (RenderLevel).
        bool _explicitJitter;
        Shader _flagShader;
        bool _flagShaderOk, _layerCullSet;
        static readonly Rect FullRect = new Rect(0f, 0f, 1f, 1f);
        readonly Vector3[] _pts = new Vector3[64];
        readonly Plane[] _planes = new Plane[6];
        static readonly int s_viewport = Shader.PropertyToID(ViewportProp);
        static readonly Vector4 FullViewport = new Vector4(0f, 0f, 1f, 1f);
        IList<PortalWindow> _windows;
        Camera _main;
        int _texW, _texH;
        readonly CommandBuffer _captureDepth = new CommandBuffer { name = "RealPortal view depth" };
        readonly CommandBuffer _captureNormal = new CommandBuffer { name = "RealPortal view normals" };
        // Win.CaptureOnce: the cameras both capture buffers are on (slot cameras, the shared one), and whether the buffers
        // hold this render's recording (cleared after it, or before the next render if that one never got to its finally).
        readonly List<Camera> _captureOn = new List<Camera>(8);
        bool _captureRecorded;
        readonly CommandBuffer _patch = new CommandBuffer { name = "RealPortal virtual depth" };
        readonly CommandBuffer _patchRelease = new CommandBuffer { name = "RealPortal virtual depth release" };
        readonly CommandBuffer _fogStart = new CommandBuffer { name = "RealPortal fog depth" };
        readonly CommandBuffer _fogEnd = new CommandBuffer { name = "RealPortal fog depth end" };
        readonly CommandBuffer _effectsStart = new CommandBuffer { name = "RealPortal image effects depth" };
        bool _fogPatched, _depthPatched, _leanPatched;
        // Win.TransparentDepth: _fogEnd binds the fog copy (with the view depths) for the transparent objects.
        bool _alphaPatched;
        // Adds `cb` at `evt` ahead of the buffers already there (they run in the order they were added).
        static void AddFirst(Camera cam, CameraEvent evt, CommandBuffer cb)
        {
            CommandBuffer[] others = cam.GetCommandBuffers(evt);
            foreach (CommandBuffer o in others)
                cam.RemoveCommandBuffer(evt, o);
            cam.AddCommandBuffer(evt, cb);
            foreach (CommandBuffer o in others)
                cam.AddCommandBuffer(evt, o);
        }
        readonly CommandBuffer _motion = new CommandBuffer { name = "RealPortal motion vectors" };
        Camera _motionOn;
        Matrix4x4 _prevVP;
        Vector3 _prevEye;
        bool _hasPrev;
        static readonly int s_curVP = Shader.PropertyToID("_RP_CurVP");
        static readonly int s_prevVP = Shader.PropertyToID("_RP_PrevVP");
        static readonly int s_camToWorld = Shader.PropertyToID("_RP_CamToWorld");
        static readonly int s_proj = Shader.PropertyToID("_RP_Proj");
        const int PassMotion = 5, PassMotionScreen = 6, PassSkyDepth = 7, PassMask = 8, PassMix = 9, PassSolid = 10, PassRestore = 11;
        RenderTexture _keepMotion;
        static readonly int s_cameraMotion = Shader.PropertyToID("_CameraMotionVectorsTexture");
        // The camera's motion vectors as its image effects get them this frame (diagnostics read it too).
        public RenderTexture MotionTexture { get; private set; }
        public bool MotionThisFrame { get; private set; }
        static readonly int s_keepMotion = Shader.PropertyToID("_RP_KeepMotion");
        readonly CommandBuffer _sky = new CommandBuffer { name = "RealPortal sky seen through windows" };
        Camera _skyOn;
        // A depth texture (same encoding as the camera's) that is sky only where a window shows sky, filled before
        // the main camera's image effects: what the second sun shafts pass reads.
        public RenderTexture SkyDepthTexture { get; private set; }
        public bool SkyDepthThisFrame { get; private set; }
        // 1 where a window is what the main camera sees (filled together with SkyDepthTexture).
        public RenderTexture WindowMaskTexture { get; private set; }
        static readonly int s_inside = Shader.PropertyToID("_RP_Inside");
        static readonly int s_windowMask = Shader.PropertyToID("_RP_WindowMask");

        // destination = outside, with the windows' pixels taken from inside.
        public void MixInside(RenderTexture outside, RenderTexture inside, RenderTexture destination)
        {
            _util.SetTexture(s_inside, inside);
            _util.SetTexture(s_windowMask, WindowMaskTexture);
            Graphics.Blit(outside, destination, _util, PassMix);
        }
        // The camera's depth with the windows at the depth seen through them (raw, like the camera's), filled
        // before the main camera's image effects: what its depth of field reads.
        public RenderTexture ViewDepthTexture { get; private set; }
        // The camera's depth as rendered (windows as their flat surfaces), copied at the same time.
        public RenderTexture PlaneDepthTexture { get; private set; }
        public bool ViewDepthThisFrame { get; private set; }
        // Camera jumps through a portal recognised so far (diagnostics).
        public int JumpsSeen { get; private set; }
        // The last frame the main camera moved more than 2 m at once: how far, and how far from where a portal pair
        // carries last frame's camera (diagnostics).
        public int LastJumpFrame { get; private set; } = -1;
        public float LastJumpLength { get; private set; }
        public float LastJumpMiss { get; private set; }
        // The window that covers the most of the screen this frame (for the sun seen through it), or null.
        public PortalWindow LargestWindow { get; private set; }
        readonly CommandBuffer _effectsSave = new CommandBuffer { name = "RealPortal before opaque effects" };
        readonly CommandBuffer _effectsRestore = new CommandBuffer { name = "RealPortal window without game effects" };
        static readonly int s_beforeEffects = Shader.PropertyToID("_RP_BeforeEffects");
        readonly List<Plane[]> _frustumPool = new List<Plane[]>();
        Camera _patchedOn;
        CameraEvent _patchEvent;
        Material _util;
        RenderTexture _lastDepth, _lastNormal;
        static readonly int s_patched = Shader.PropertyToID("_RP_PatchedDepth");
        static readonly int s_fogDepth = Shader.PropertyToID("_RP_FogDepth");
        static readonly int s_patchNear = Shader.PropertyToID("_RP_PatchNear");
        static readonly int s_cameraDepth = Shader.PropertyToID("_CameraDepthTexture");
        const int PassDepthPatch = 2, PassLinearDepth = 3, PassCopyDepth = 4;
        public PortalRenderer(PortalRenderSettings settings)
        {
            Settings = settings;
            var go = new GameObject("RealPortalCamera");
            if (Application.isPlaying)
                UnityEngine.Object.DontDestroyOnLoad(go);
            _cam = go.AddComponent<Camera>();
            _cam.enabled = false;
            Shader.SetGlobalVector(s_viewport, FullViewport);
        }

        public Camera Camera => _cam;

        public void Destroy()
        {
            // Window Pipeline v2 owners' cleanup (partial methods in their PortalRenderer.*.cs files; none: no code).
            DestroyPlan();
            DestroyComposite();
            DestroyVirtual();
            DetachCapture();
            DestroySlots();
            Unpatch();
            if (_motionOn != null)
                _motionOn.RemoveCommandBuffer(EffectsPrepEvent, _motion);
            _motionOn = null;
            if (_skyOn != null)
                _skyOn.RemoveCommandBuffer(EffectsPrepEvent, _sky);
            _skyOn = null;
            RemoveBlurInputs();
            if (SkyDepthTexture != null)
                PortalMath.Kill(SkyDepthTexture);
            if (ViewDepthTexture != null)
                PortalMath.Kill(ViewDepthTexture);
            if (PlaneDepthTexture != null)
                PortalMath.Kill(PlaneDepthTexture);
            if (WindowMaskTexture != null)
                PortalMath.Kill(WindowMaskTexture);
            if (_keepMotion != null)
                PortalMath.Kill(_keepMotion);
            if (MotionTexture != null)
                PortalMath.Kill(MotionTexture);
            ReleaseTemps();
            WindowRTPool.Clear();
            Shader.SetGlobalFloat(s_inWindowRender, 0f);
            Shader.SetGlobalFloat(s_renderingWindowId, 0f);
            Shader.SetGlobalFloat(s_pointDepth, 0f);
            s_pointDepthOn = false;
            Shader.SetGlobalFloat(s_fallbackMode, 0f);
            s_fallbackModeOn = false;
            Shader.SetGlobalVector(s_nestedIds, Vector4.zero);
            if (_util != null)
                PortalMath.Kill(_util);
            if (_cam != null)
                PortalMath.Kill(_cam.gameObject);
        }

        // Call once per frame after the main camera has its final pose, before it renders.
        public void RenderAll(Camera main, IList<PortalWindow> windows)
        {
            if (!BeginFrame(main, windows))
                return;
            // Window Pipeline v2 without Win.Tiers: the manager follows this scheduler's choices (tiers derived from the
            // cadence, caches, resolve). Null (the Unity tests, nothing of S2/S3 in force): exactly as post-S1.
            WindowManager hook = Hook;
            Matrix4x4 vp = _frameVP;
            Vector3 eye = _frameEye;
            bool jumped = _frameJumped;
            int frame = _frameNo;

            // Win.FullFrameHero (Docs\RIPPLE-PLAN.md §4.5): the largest window as last frame chose it (PickHero) renders over
            // the whole screen and every frame, as every window does under FullFrame.
            PortalWindow hero = WindowFlags.FullFrameHero ? _hero : null;
            // Win.LiveShare (§4.6): every frame from 1.5 % of the screen (the every-3rd-frame share), whatever LiveFill's
            // headroom; the 8 m rule from the player's eyes too. Win.AllLive (Docs\PARITY-PLAN.md P8): LiveShare with its share
            // at 0, whether LiveShare is on or not: every window that passes the tests below renders every frame, so none
            // is held by MaxRendersPerFrame or waits for LiveFill (both only take windows off the every-frame cadence).
            float always = WindowFlags.AllLive ? 0f : WindowFlags.LiveShare ? Settings.RefreshAlwaysShare * 0.3f : Settings.RefreshAlwaysShare;
            bool fromViewer = WindowFlags.LiveShare && Settings.HasViewer;
            // Win.DistantLive (P2): the pixel floor instead of MinScreenFraction (the reach is Settings.MaxDistance, which
            // the game side sets to the far end's live range under the switch).
            bool floor = WindowFlags.DistantLive;
            _todo.Clear();
            for (int i = 0; i < windows.Count; i++)
            {
                PortalWindow w = windows[i];
                if (!PrepareWindow(w, main))
                    continue;
                float dist = Vector3.Distance(eye, w.Frame.Center);
                float half = Mathf.Max(w.Frame.HalfSize.x, w.Frame.HalfSize.y);
                if (dist > Settings.MaxDistance + half)
                    continue;
                if (!GeometryUtility.TestPlanesAABB(_planes, w.Renderer.bounds))
                    continue;
                if (!WindowRect(w, vp, out Rect rect, out Vector2 extent)
                    || (floor ? !AbovePixelFloor(w, extent, main, frame) : rect.width * rect.height < Settings.MinScreenFraction))
                    continue;
                float share = rect.width * rect.height;
                // Lab: what only Win.DistantLive lets render (LabDistantRenders counts its renders).
                if (floor && (dist > Settings.DistantFrom + half || share < Settings.MinScreenFraction))
                    w.DistantAtFrame = frame;
                Rect opening = rect;
                rect = RenderRect(w, rect, WindowMargin.Legacy, 0f, main);
                // How often it needs a new view: every frame when large or near (or nothing to show yet), every
                // second frame when middling, every third when small.
                int every = 1;
                bool kept = w.Texture != null && w.ShownAtFrame >= frame - 1 && w.RenderedAtFrame >= frame - 6;
                float near = fromViewer ? Mathf.Min(dist, Vector3.Distance(Settings.Viewer, w.Frame.Center)) : dist;
                if (Settings.RefreshSmallLess && kept && !jumped && !Settings.FullFrame && w != hero && near > 8f && share < always)
                    every = share < Settings.RefreshAlwaysShare * 0.3f ? 3 : 2;
                _todo.Add((w, rect, share, every, frame - w.RenderedAtFrame));
                hook?.LegacyCandidate(w, opening, rect, share, dist, every);
            }
            if (WindowFlags.FullFrameHero)
                PickHero();
            else
                _hero = null;
            // Most overdue and largest first; within the budget the rest show their last view (the ones due every
            // frame always render).
            _todo.Sort(s_todoOrder);
            bool grouped = WindowFlags.EnvGroups && (BeforeGroup != null || AfterGroup != null);
            _due.Clear();
            int renders = 0;
            // Win.LiveFill (FARWORLD-V3 §7 V0): a window off its cadence this frame (due every 2nd or 3rd frame, or held
            // back by MaxRendersPerFrame) waits in _fill instead of showing its last view, and renders after all while the
            // manager's main-thread headroom holds its estimated cost on top of what is due anyway (fillMs), largest first.
            // Without the manager (the Unity tests) or with Win.Tiers, as before.
            bool fill = hook != null && WindowFlags.LiveFill && !WindowFlags.Tiers;
            float fillMs = 0f;
            _fill.Clear();
            for (int k = 0; k < _todo.Count; k++)
            {
                var t = _todo[k];
                PortalWindow w = t.w;
                bool due = t.every == 1 || t.age >= t.every;
                if (due && t.every > 1 && Settings.MaxRendersPerFrame > 0 && renders >= Settings.MaxRendersPerFrame && t.age < 6)
                    due = false;
                if (hook != null)
                    due = hook.LegacyDue(w, due);
                w.ShownAtFrame = frame;
                if (!due)
                {
                    // Win.LiveFill: decided once the windows due by themselves are known (hidden ones never wait here).
                    if (fill && t.every > 1 && hook.LiveFillCandidate(w))
                    {
                        AddFill(k, t.share);
                        continue;
                    }
                    // Its last view: resolved onto its rect now (Win.Resolve, through the manager), or laid onto the
                    // opening as it was seen then.
                    if (hook == null || !hook.LegacyStale(w))
                        ShowStale(w, true);
                    continue;
                }
                if (fill)
                    fillMs += hook.LiveFillCostMs(w);
                Rect r = Settings.FullFrame || w == hero ? FullRect : t.rect;
                // What RenderLevel draws nothing for (a rect that snaps to no pixels) does not count.
                Rect snapped = PortalMath.SnapToPixels(r, _texW, _texH);
                if (snapped.width > 0f && snapped.height > 0f)
                    renders++;
                _due.Add((w, r, grouped && Settings.GroupOf != null ? Settings.GroupOf(w) : k));
            }
            if (fill)
            {
                // The headroom holds all of the frame's renders, the due ones first; the renders off their cadence alone
                // are also held to the manager's cap on them (B_cpu's CpuMaxMs, or [Window] CpuBudgetMs).
                float budget = hook.LiveFillBudgetMs, dueMs = fillMs, extraMax = hook.LiveFillExtraMaxMs;
                for (int i = 0; i < _fill.Count; i++)
                {
                    int k = _fill[i].k;
                    var t = _todo[k];
                    PortalWindow w = t.w;
                    float ms = hook.LiveFillCostMs(w);
                    if (fillMs + ms <= budget && fillMs - dueMs + ms <= extraMax)
                    {
                        fillMs += ms;
                        hook.LiveFilled(w);
                        _due.Add((w, t.rect, grouped && Settings.GroupOf != null ? Settings.GroupOf(w) : k));
                        continue;
                    }
                    hook.LiveFillDenied(w, ms);
                    if (!hook.LegacyStale(w))
                        ShowStale(w, true);
                }
                hook.LiveFillDone(fillMs);
            }
            if (grouped && Settings.GroupOf != null)
                GroupDue();
            // Win.Virtual: the windows drawn without a camera after the others (nothing moves with the switch off).
            VirtualLast();
            PortalWindow openGroup = null;
            try
            {
                for (int i = 0; i < _due.Count; i++)
                {
                    PortalWindow w = _due[i].w;
                    if (grouped && (i == 0 || _due[i].group != _due[i - 1].group))
                    {
                        openGroup = w;
                        BeforeGroup?.Invoke(w);
                    }
                    long t0 = hook != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
                    RenderTexture tex = RenderShown(w, _due[i].rect);
                    hook?.LegacyRendered(w, tex, t0);
                    if (grouped && (i == _due.Count - 1 || _due[i].group != _due[i + 1].group))
                    {
                        openGroup = null;
                        AfterGroup?.Invoke(w);
                    }
                }
            }
            finally
            {
                if (openGroup != null)
                    AfterGroup?.Invoke(openGroup);
            }
            EndFrame(main, windows, true);
        }

        void Own(PortalWindow w, RenderTexture rt)
        {
            if (rt != null && _temps.Remove(rt))
                w.Owned.Add(rt);
        }

        // Renders of one group back to back, the groups in the order of their first render (the budget has already
        // picked which windows render; only the order changes, and within a group it stays as it was).
        void GroupDue()
        {
            _dueSorted.Clear();
            for (int i = 0; i < _due.Count; i++)
            {
                int g = _due[i].group;
                bool placed = false;
                for (int j = 0; j < i && !placed; j++)
                    placed = _due[j].group == g;
                if (placed)
                    continue;
                for (int j = i; j < _due.Count; j++)
                    if (_due[j].group == g)
                        _dueSorted.Add(_due[j]);
            }
            _due.Clear();
            _due.AddRange(_dueSorted);
        }

        // Win.FullFrameHero: the window rendered over the whole screen, chosen from this frame's candidates for the next
        // frame: the largest share of the screen, but the one chosen keeps it until another is a quarter larger (two windows
        // of about the same size would trade it back and forth, each trade a pop of their shadows).
        PortalWindow _hero;

        void PickHero()
        {
            int best = -1, current = -1;
            for (int k = 0; k < _todo.Count; k++)
            {
                if (best < 0 || _todo[k].share > _todo[best].share)
                    best = k;
                if (_todo[k].w == _hero)
                    current = k;
            }
            if (best < 0)
                _hero = null;
            else if (current < 0 || _todo[current].share * 1.25f < _todo[best].share)
                _hero = _todo[best].w;
        }

        // Win.LiveFill: into _fill by share, the largest first (among equals in the order they came; a few windows, no sort).
        void AddFill(int k, float share)
        {
            int at = _fill.Count;
            while (at > 0 && _fill[at - 1].share < share)
                at--;
            _fill.Insert(at, (k, share));
        }

        // The game's motion blur is a command buffer at BeforeImageEffects (PostProcessing v1), and it stays ahead
        // of any buffer added there later: what it reads (motion vectors, depth) is fixed up for the windows one
        // event earlier, after the transparent objects. There, in the order depth, sky and view depth, motion.
        const CameraEvent EffectsPrepEvent = CameraEvent.AfterForwardAlpha;

        readonly List<CommandBuffer> _ours = new List<CommandBuffer>(3);
        CommandBuffer[] _prepBuffers;
        Camera _prepCamera;
        int _prepCount = -1, _prepChanges = -1;
        // Counts every add or removal of our buffers on the game camera.
        int _cbChanges;

        void OrderEffectsPrep(Camera main)
        {
            // Nothing added or taken away on the camera since the last check: still in order (LeanFixed; reading the
            // buffers back allocates).
            if (WindowFlags.LeanFixed && main == _prepCamera && main.commandBufferCount == _prepCount && _cbChanges == _prepChanges)
                return;
            CommandBuffer[] all = main.GetCommandBuffers(EffectsPrepEvent);
            if (_prepBuffers == null)
                _prepBuffers = new[] { _effectsStart, _sky, _motion };
            _ours.Clear();
            foreach (CommandBuffer cb in _prepBuffers)
                if (System.Array.IndexOf(all, cb) >= 0)
                    _ours.Add(cb);
            bool inOrder = true;
            for (int i = 0; i < _ours.Count; i++)
                inOrder &= all[i] == _ours[i];
            if (!inOrder)
            {
                foreach (CommandBuffer cb in all)
                    main.RemoveCommandBuffer(EffectsPrepEvent, cb);
                foreach (CommandBuffer cb in _ours)
                    main.AddCommandBuffer(EffectsPrepEvent, cb);
                foreach (CommandBuffer cb in all)
                    if (!_ours.Contains(cb))
                        main.AddCommandBuffer(EffectsPrepEvent, cb);
            }
            _prepCamera = main;
            _prepCount = main.commandBufferCount;
            _prepChanges = _cbChanges;
        }

        // Win.BlurInputs (Docs\RIPPLE-PLAN.md §4.3): Unity binds the camera's own textures again as image effects start, so
        // what EffectsPrepEvent bound may never reach the game's motion blur (a buffer at BeforeImageEffects): this one,
        // first there, binds the fixed-up motion vectors and the view depth again right ahead of it. On the camera from the
        // first frame it has something to bind until the switch goes off, the camera changes or the windows go (recorded
        // empty on a frame without either, so it is not taken off and put back with the windows' cadence). The view depth
        // stays bound for the OnRenderImage effects after motion blur too: depth of field, and TAA's dilation unless its
        // material has a depth of its own (Win.TaaViewDepth, LabTaaDepth), so a lab line on this switch pins TAA's depth
        // in both arms.
        readonly CommandBuffer _blurInputs = new CommandBuffer { name = "RealPortal motion blur inputs" };
        Camera _blurOn;

        void UpdateBlurInputs(Camera main)
        {
            bool motion = MotionThisFrame && MotionTexture != null;
            bool depth = ViewDepthThisFrame && Settings.ViewDepthForFinalEffects && ViewDepthTexture != null;
            if (_blurOn != null && (_blurOn != main || !WindowFlags.BlurInputs))
                RemoveBlurInputs();
            if (!WindowFlags.BlurInputs || main == null || (_blurOn == null && !motion && !depth))
                return;
            _blurInputs.Clear();
            if (motion)
                _blurInputs.SetGlobalTexture(s_cameraMotion, MotionTexture);
            if (depth)
                _blurInputs.SetGlobalTexture(s_cameraDepth, ViewDepthTexture);
            if (_blurOn == null)
            {
                AddFirst(main, CameraEvent.BeforeImageEffects, _blurInputs);
                _blurOn = main;
                _cbChanges++;
            }
        }

        void RemoveBlurInputs()
        {
            if (_blurOn == null)
                return;
            _blurOn.RemoveCommandBuffer(CameraEvent.BeforeImageEffects, _blurInputs);
            _blurOn = null;
            _blurInputs.Clear();
            _cbChanges++;
        }

        static RenderTexture ScreenDepth(RenderTexture rt, Camera main, string name)
        {
            if (rt != null && rt.width == main.pixelWidth && rt.height == main.pixelHeight)
                return rt;
            if (rt != null)
                PortalMath.Kill(rt);
            return new RenderTexture(main.pixelWidth, main.pixelHeight, 0, RenderTextureFormat.RFloat) { name = name, filterMode = FilterMode.Point };
        }

        // Depth textures for the game camera's final image effects that need to see through the windows: the sky
        // in them (second sun shafts pass) and the depth of what they show (depth of field).
        void UpdateSky(Camera main, IList<PortalWindow> windows)
        {
            LargestWindow = null;
            float area = 0f;
            // Window Pipeline v2 with Win.Resolve: a resolved window's texture covers its opening only, a fresh one its
            // tier's margin too, so the texture rect would rank a window differently on the frames it renders and on those
            // it is resolved, and the window sun (the second sun-shafts chain) would jump between two windows with the
            // render cadence. The opening's own rect this frame ranks them alike either way. Under Win.FullFrameHero the
            // hero's texture is the whole screen, so the texture rect would always rank it first.
            bool byOpening = Pipeline != null && (Pipeline.Effective(WindowMechanism.Resolve) || WindowFlags.FullFrameHero);
            int frame = Time.frameCount;
            for (int iw = 0; iw < windows.Count; iw++)
            {
                PortalWindow w = windows[iw];
                if (!w.RenderedThisFrame || w.DepthTexture == null)
                    continue;
                Rect r = w.TextureRect;
                WindowState st = byOpening ? w.State : null;
                if (st != null && st.PlannedFrame == frame && st.OpeningRect.width > 0f)
                    r = st.OpeningRect;
                if (r.width * r.height > area)
                {
                    area = r.width * r.height;
                    LargestWindow = w;
                }
            }
            bool sky = Settings.WindowSunShafts && LargestWindow != null;
            bool view = Settings.ViewDepthForEffects && LargestWindow != null;
            if (sky || view)
                EnsureUtil(LargestWindow);
            sky &= _util != null;
            view &= _util != null;
            if (_skyOn != null && (_skyOn != main || !(sky || view)))
            {
                _skyOn.RemoveCommandBuffer(EffectsPrepEvent, _sky);
                _skyOn = null;
                _cbChanges++;
            }
            SkyDepthThisFrame = sky;
            ViewDepthThisFrame = view;
            // Win.CompositeMRT (UpdatePatch recorded the AUX pass this frame): the sky depth, the mask and the view depth
            // are the composite's targets, written before lighting by the same draw per window; only the plane depth is
            // still copied here. The mask is the composite's whenever the pass ran (the lab reads it under the mechanism).
            WindowComposite aux = _auxThisFrame ? DrivingComposite : null;
            if (aux != null && aux.WindowMask != null)
            {
                WindowMaskTexture = aux.WindowMask;
                _maskAlias = true;
            }
            if (!sky && !view)
                return;
            _sky.Clear();
            int draws = 0;
            if (sky && aux != null && aux.SkyDepth != null && aux.WindowMask != null)
            {
                SkyDepthTexture = aux.SkyDepth;
                _skyAlias = true;
            }
            else if (sky)
            {
                // The renderer's own textures again: never make or Kill the composite's.
                DropSkyAlias();
                DropMaskAlias();
                SkyDepthTexture = ScreenDepth(SkyDepthTexture, main, "RealPortal window sky");
                float nearRaw = SystemInfo.usesReversedZBuffer ? 1f : 0f;
                _sky.SetRenderTarget(SkyDepthTexture);
                _sky.ClearRenderTarget(false, true, new Color(nearRaw, 0f, 0f, 0f));
                for (int iw = 0; iw < windows.Count; iw++)
                {
                    PortalWindow w = windows[iw];
                    if (w.RenderedThisFrame && w.DepthTexture != null)
                    {
                        _sky.DrawRenderer(w.Renderer, w.Material, 0, PassSkyDepth);
                        draws++;
                    }
                }
                // Where the windows are: the shafts of the sun seen through them stay inside.
                if (WindowMaskTexture == null || WindowMaskTexture.width != main.pixelWidth || WindowMaskTexture.height != main.pixelHeight)
                {
                    if (WindowMaskTexture != null)
                        PortalMath.Kill(WindowMaskTexture);
                    WindowMaskTexture = new RenderTexture(main.pixelWidth, main.pixelHeight, 0, RenderTextureFormat.R8) { name = "RealPortal window mask" };
                }
                _sky.SetRenderTarget(WindowMaskTexture);
                _sky.ClearRenderTarget(false, true, Color.clear);
                for (int iw = 0; iw < windows.Count; iw++)
                {
                    PortalWindow w = windows[iw];
                    if (w.RenderedThisFrame)
                    {
                        _sky.DrawRenderer(w.Renderer, w.Material, 0, PassMask);
                        draws++;
                    }
                }
            }
            if (view)
            {
                // The camera's depth as it is (the windows as flat surfaces), and with each window's pixels at the
                // depth seen through it.
                PlaneDepthTexture = ScreenDepth(PlaneDepthTexture, main, "RealPortal depth with windows");
                _sky.Blit(BuiltinRenderTextureType.CameraTarget, PlaneDepthTexture, _util, PassCopyDepth);
                if (aux != null && aux.ViewDepth != null)
                {
                    ViewDepthTexture = aux.ViewDepth;
                    _viewAlias = true;
                }
                else
                {
                    DropViewAlias();
                    ViewDepthTexture = ScreenDepth(ViewDepthTexture, main, "RealPortal depth through windows");
                    _sky.Blit(BuiltinRenderTextureType.CameraTarget, ViewDepthTexture, _util, PassCopyDepth);
                    _sky.SetGlobalFloat(s_patchNear, 0f);
                    _sky.SetRenderTarget(ViewDepthTexture);
                    for (int iw = 0; iw < windows.Count; iw++)
                    {
                        PortalWindow w = windows[iw];
                        if (w.RenderedThisFrame && w.DepthTexture != null)
                        {
                            _sky.DrawRenderer(w.Renderer, w.Material, 0, PassDepthPatch);
                            draws++;
                        }
                    }
                }
                // The final image effects (TAA, motion blur) then treat what is seen through a window by its real
                // depth: with the window's own they blur and smear it as a surface right in front of the camera.
                // Sun shafts get PlaneDepthTexture (a window's sky is not the sky of the real sun).
                if (Settings.ViewDepthForFinalEffects)
                    _sky.SetGlobalTexture(s_cameraDepth, ViewDepthTexture);
            }
            CountCompositeDraws(draws);
            _sky.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            if (_skyOn == null)
            {
                main.AddCommandBuffer(EffectsPrepEvent, _sky);
                _skyOn = main;
                _cbChanges++;
            }
        }

        // The game camera's motion vectors over each window, from the view depth seen through it: TAA and motion
        // blur then move the view like the far scenery it shows, not like a surface right in front of the camera.
        void UpdateMotion(Camera main, IList<PortalWindow> windows)
        {
            Matrix4x4 vp = main.nonJitteredProjectionMatrix * main.worldToCameraMatrix;
            Vector3 eye = main.transform.position;
            bool hasMotion = Settings.MotionVectors && (main.depthTextureMode & DepthTextureMode.MotionVectors) != 0;
            MotionThisFrame = false;
            bool jump = false;
            if (_hasPrev && (eye - _prevEye).sqrMagnitude > 4f)
            {
                // Carried through a portal (the camera arm going through one, or back): the whole screen gets the
                // motion it would have without the jump, from last frame's view carried along, and TAA keeps its
                // history. (Resetting it instead made far, thin things blink for a frame.)
                // The pair that carries last frame's camera nearest to this one. The camera may also have moved
                // on its own in that frame (pulled in by the frame, the arm getting shorter): that part is ordinary
                // motion, and tolerated up to a quarter of the jump.
                Matrix4x4 best = Matrix4x4.identity;
                float bestMiss = float.MaxValue;
                for (int iw = 0; iw < windows.Count; iw++)
                {
                    PortalWindow w = windows[iw];
                    if (w.Partner == null)
                        continue;
                    Matrix4x4 m = PortalMath.Pair(w.Frame, w.Partner.Frame);
                    float miss = (m.MultiplyPoint3x4(_prevEye) - eye).magnitude;
                    if (miss < bestMiss)
                    {
                        bestMiss = miss;
                        best = m;
                    }
                }
                float jumpLength = (eye - _prevEye).magnitude;
                LastJumpFrame = Time.frameCount;
                LastJumpLength = jumpLength;
                LastJumpMiss = bestMiss;
                if (bestMiss < (Settings.StrictJump ? 1f : Mathf.Max(1.5f, jumpLength * 0.25f)))
                {
                    _prevVP = _prevVP * best.inverse;
                    jump = hasMotion;
                    JumpsSeen++;
                }
                // Any other jump (a teleport) has no motion to blur.
                if (!jump)
                    _prevVP = vp;
            }
            else if (!_hasPrev)
                _prevVP = vp;
            bool windowsShown = false;
            if (hasMotion)
                for (int iw = 0; iw < windows.Count; iw++)
                {
                    PortalWindow w = windows[iw];
                    windowsShown |= w.RenderedThisFrame && w.DepthTexture != null;
                }
            // Diagnostics: the whole-screen vectors every frame (no jump), to compare with the camera's own.
            if (Settings.MotionScreenAlways && hasMotion && _hasPrev)
                jump = true;
            if ((windowsShown || jump) && windows.Count > 0)
                EnsureUtil(windows[0]);
            windowsShown &= _util != null;
            jump &= _util != null;
            // Every frame the camera has motion vectors: the copy is what its TAA reads (Patches.TaaMotion), also
            // when no window shows. LeanFixed: only when there is something to fix up (a window shown, a jump);
            // otherwise TAA and motion blur read the camera's own vectors (TaaMotion falls back to the global).
            bool any = hasMotion;
            if (WindowFlags.LeanFixed && WindowFlags.LeanMotion)
                any = hasMotion && (windowsShown || jump);
            if (_motionOn != null && (_motionOn != main || !any))
            {
                _motionOn.RemoveCommandBuffer(EffectsPrepEvent, _motion);
                _motionOn = null;
                _cbChanges++;
            }
            if (any)
            {
                Matrix4x4 p = main.nonJitteredProjectionMatrix;
                _motion.Clear();
                _motion.SetGlobalMatrix(s_curVP, vp);
                _motion.SetGlobalMatrix(s_prevVP, _prevVP);
                _motion.SetGlobalMatrix(s_camToWorld, main.cameraToWorldMatrix);
                _motion.SetGlobalVector(s_proj, new Vector4(p.m00, p.m11, p.m02, p.m12));
                // The game's vectors, fixed up in a texture of our own that then becomes _CameraMotionVectorsTexture
                // for its image effects (written into its own motion vector target, its motion blur never saw them).
                if (MotionTexture == null || MotionTexture.width != main.pixelWidth || MotionTexture.height != main.pixelHeight)
                {
                    if (MotionTexture != null)
                        PortalMath.Kill(MotionTexture);
                    MotionTexture = new RenderTexture(main.pixelWidth, main.pixelHeight, 0, RenderTextureFormat.RGHalf) { name = "RealPortal motion vectors", filterMode = FilterMode.Point };
                }
                _motion.Blit(BuiltinRenderTextureType.MotionVectors, MotionTexture);
                if (jump)
                {
                    // What moves along with the camera (the player) keeps the game's own vectors: its last frame
                    // was drawn on the camera's old side too, so those are already continuous.
                    if (_keepMotion == null || _keepMotion.width != main.pixelWidth || _keepMotion.height != main.pixelHeight)
                    {
                        if (_keepMotion != null)
                            PortalMath.Kill(_keepMotion);
                        _keepMotion = new RenderTexture(main.pixelWidth, main.pixelHeight, 0, RenderTextureFormat.R8) { name = "RealPortal keep motion" };
                    }
                    _motion.SetRenderTarget(_keepMotion);
                    _motion.ClearRenderTarget(false, true, Color.clear);
                    if (Settings.KeepOwnMotion != null)
                        foreach (Renderer r in Settings.KeepOwnMotion())
                            if ((r is MeshRenderer || r is SkinnedMeshRenderer) && r.enabled && r.gameObject.activeInHierarchy)
                                for (int s = 0; s < r.sharedMaterials.Length; s++)
                                    _motion.DrawRenderer(r, _util, s, PassSolid);
                    _motion.SetGlobalTexture(s_keepMotion, _keepMotion);
                    _motion.Blit(BuiltinRenderTextureType.CameraTarget, MotionTexture, _util, PassMotionScreen);
                }
                if (windowsShown)
                {
                    _motion.SetRenderTarget(MotionTexture);
                    int draws = 0;
                    for (int iw = 0; iw < windows.Count; iw++)
                    {
                        PortalWindow w = windows[iw];
                        if (MotionThrough(w))
                        {
                            _motion.DrawRenderer(w.Renderer, w.Material, 0, PassMotion);
                            draws++;
                        }
                    }
                    CountCompositeDraws(draws);
                }
                _motion.SetGlobalTexture(s_cameraMotion, MotionTexture);
                _motion.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
                MotionThisFrame = true;
                if (_motionOn == null)
                {
                    main.AddCommandBuffer(EffectsPrepEvent, _motion);
                    _motionOn = main;
                    _cbChanges++;
                }
            }
            _prevVP = vp;
            _prevEye = eye;
            _hasPrev = true;
        }

        // A window whose pixels get the motion of what it shows (MOTION). Win.StaleTaa: not one shown flat from an earlier
        // render, whose picture is pinned to the opening and so moves as the camera's own vectors for its surface say (the
        // copy is still made while one shows, so the buffer stays on as before).
        static bool MotionThrough(PortalWindow w) =>
            w.RenderedThisFrame && w.DepthTexture != null && !(WindowFlags.StaleTaa && w.Stale);

        // On the main camera, right after its depth texture exists: a copy of it in which the window pixels hold
        // the depth seen through them, made the camera's _CameraDepthTexture for everything after. The z-buffer
        // keeps the portal plane, so whatever stands behind the entry portal still stays hidden behind the window.
        // Without VirtualDepth only the effects copy is made (the window as sky, so the game's fog and SSAO leave it
        // to the window camera, which applies them to its own view); everything else keeps reading the real depth.
        void UpdatePatch(Camera main, IList<PortalWindow> windows)
        {
            bool rendered = false, depths = false;
            for (int iw = 0; iw < windows.Count; iw++)
            {
                PortalWindow w = windows[iw];
                rendered |= w.RenderedThisFrame;
                depths |= w.RenderedThisFrame && w.DepthTexture != null;
            }
            bool virtualDepth = Settings.VirtualDepth && depths;
            bool fog = Settings.FogSeesNearWindow && rendered;
            if ((virtualDepth || fog) && windows.Count > 0)
                EnsureUtil(windows[0]);
            // What the post-S1 patch is for: the fog copy and the virtual depth.
            bool core = (virtualDepth || fog) && _util != null;
            if (!core)
                virtualDepth = fog = false;
            // Win.TransparentDepth (Docs\PARITY-PLAN.md P42): the fog copy holds each window's pixels at the depth seen through
            // it (never nearer than the window's own surface; as sky for a window without a view depth) instead of as sky, and
            // it is also what this side's transparent objects read as _CameraDepthTexture (bound at BeforeForwardAlpha by
            // _fogEnd): soft particles, mist and water at the opening fade against what the window shows, not against its
            // plane, while the z-buffer still holds the plane, so nothing behind the window draws over it. Inside the windows
            // the game's opaque effects are put back as before (RESTORE), and its fog is per pixel, so what changes outside
            // them is the game's ambient occlusion: a band of a few pixels round each window now sees the far scenery there,
            // as it sees anything beyond a doorway. With VirtualDepth the transparents read the view depth already, and the
            // final effects get the fog copy, so the switch leaves that path alone; it needs the fog copy (FogSeesNearWindow)
            // and a bundle whose window shader has the mode (ShaderHasParity).
            bool alphaDepth = WindowFlags.TransparentDepth && fog && depths && !virtualDepth && ShaderHasParity(windows[0]);
            float fogMode = alphaDepth ? 3f : Settings.EffectsSeeWindowAsSky ? 2f : 1f;
            // Window Pipeline v2 (S3, Docs\S2S3-CONTRACTS.md §9), only while the manager drives the frame. Win.CompositeMRT:
            // one AUXBASE pass and one AUXMRT draw per window write the fog copy, the view depth, the sky depth, the mask
            // and the ids at once, whenever any of them is wanted this frame (UpdateSky's sky and view depth come from
            // there too). Win.Occlusion without it: the ids alone (WINDOWID). Neither: exactly the post-S1 patch.
            WindowComposite comp = DrivingComposite;
            // Ids only while some window may be seen (shown, or in the frustum with an id: a hidden one is counted to be
            // seen again); none in view: no full-screen AUXBASE for them, no count dispatch and no readback.
            bool ids = comp != null && Pipeline.Effective(WindowMechanism.Occlusion) && AnyWindowInView(windows);
            bool skyWanted = Settings.WindowSunShafts && depths, viewWanted = Settings.ViewDepthForEffects && depths;
            bool mrt = comp != null && Pipeline.Effective(WindowMechanism.CompositeMRT) && (core || skyWanted || viewWanted || ids)
                       && comp.Prepare(main, true);
            bool idsOnly = !mrt && ids && comp.Prepare(main, false);
            bool restoreView = fog && comp != null && Pipeline.Effective(WindowMechanism.RestoreFromWindow);
            _auxThisFrame = false;
            _idsDrawnFrame = ids && (mrt || idsOnly) ? Time.frameCount : -100;
            bool any = core || mrt || idsOnly;
            // LeanFixed: without the views in it the copy of the real depth is the camera's own depth texture; what
            // comes after its fog gets that one back instead of a copy made every frame.
            bool lean = WindowFlags.LeanFixed && WindowFlags.LeanPatchCopy && !virtualDepth;
            CameraEvent evt = main.actualRenderingPath == RenderingPath.DeferredShading ? CameraEvent.BeforeLighting : CameraEvent.AfterDepthTexture;
            // The fog and image-effect buffers bind the composite's targets by reference: a new target (the screen size
            // changed) needs them recorded again, as a change of mode does.
            bool auxMoved = mrt && (_auxFogPatched != comp.FogDepth || _auxViewPatched != comp.ViewDepth);
            if (_patchedOn != null && (_patchedOn != main || _patchEvent != evt || !any || _fogPatched != fog || _depthPatched != virtualDepth || _leanPatched != lean
                                       || _mrtPatched != mrt || _idsPatched != idsOnly || _restoreViewPatched != restoreView || auxMoved
                                       || _alphaPatched != alphaDepth))
                Unpatch();
            if (!any)
                return;
            _patch.Clear();
            int draws = 0;
            if (mrt)
            {
                // The five targets in one pass (the depth copies and DEPTHPATCH draws below are what it replaces). It
                // reads the real depth, so it comes before the virtual depth is bound.
                // (RecordAux, RecordIds and RecordRestoreView count their own draws into the composite's DrawsLastFrame.)
                comp.RecordAux(_patch, main, windows, fogMode);
                _auxThisFrame = true;
                _patch.SetGlobalFloat(s_patchNear, 0f);
                if (virtualDepth)
                    _patch.SetGlobalTexture(s_cameraDepth, comp.ViewDepth);
            }
            else
            {
                if (core)
                {
                    // The real depth (patched with the views when VirtualDepth is on): what the camera reads after its fog.
                    if (!lean)
                    {
                        _patch.GetTemporaryRT(s_patched, -1, -1, 0, FilterMode.Point, RenderTextureFormat.RFloat);
                        _patch.Blit(BuiltinRenderTextureType.CameraTarget, s_patched, _util, PassCopyDepth);
                    }
                    _patch.SetGlobalFloat(s_patchNear, 0f);
                    if (virtualDepth)
                    {
                        _patch.SetRenderTarget(s_patched);
                        for (int iw = 0; iw < windows.Count; iw++)
                        {
                            PortalWindow w = windows[iw];
                            if (w.RenderedThisFrame && w.DepthTexture != null)
                            {
                                _patch.DrawRenderer(w.Renderer, w.Material, 0, PassDepthPatch);
                                draws++;
                            }
                        }
                    }
                    if (fog)
                    {
                        _patch.GetTemporaryRT(s_fogDepth, -1, -1, 0, FilterMode.Point, RenderTextureFormat.RFloat);
                        _patch.Blit(BuiltinRenderTextureType.CameraTarget, s_fogDepth, _util, PassCopyDepth);
                        _patch.SetGlobalFloat(s_patchNear, fogMode);
                        _patch.SetRenderTarget(s_fogDepth);
                        for (int iw = 0; iw < windows.Count; iw++)
                        {
                            PortalWindow w = windows[iw];
                            if (w.RenderedThisFrame)
                            {
                                _patch.DrawRenderer(w.Renderer, w.Material, 0, PassDepthPatch);
                                draws++;
                            }
                        }
                        _patch.SetGlobalFloat(s_patchNear, 0f);
                    }
                }
                // The ids for the visibility counts, against the real depth (before the virtual depth is bound).
                if (idsOnly)
                    comp.RecordIds(_patch, main, windows);
                if (virtualDepth)
                    _patch.SetGlobalTexture(s_cameraDepth, s_patched);
            }
            // Forward rendering carries on into whatever target is bound after a command buffer.
            _patch.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            if (fog)
            {
                _effectsRestore.Clear();
                _effectsRestore.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
                if (restoreView)
                {
                    // Win.RestoreFromWindow: the window's pixels shaded again from its own view (RESTOREVIEW), no copy of
                    // the whole screen before the opaque effects.
                    comp.RecordRestoreView(_effectsRestore, windows);
                }
                else
                {
                    // The game's SSAO does not go by the depth copy (close up it darkens the window in a big blot):
                    // the window's pixels are put back as they were before the opaque image effects.
                    _effectsSave.Clear();
                    _effectsSave.GetTemporaryRT(s_beforeEffects, -1, -1, 0, FilterMode.Point,
                        main.allowHDR ? RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default);
                    _effectsSave.Blit(BuiltinRenderTextureType.CameraTarget, s_beforeEffects);
                    _effectsSave.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
                    for (int iw = 0; iw < windows.Count; iw++)
                    {
                        PortalWindow w = windows[iw];
                        if (w.RenderedThisFrame)
                        {
                            _effectsRestore.DrawRenderer(w.Renderer, w.Material, 0, PassRestore);
                            draws++;
                        }
                    }
                    _effectsRestore.ReleaseTemporaryRT(s_beforeEffects);
                }
            }
            CountCompositeDraws(draws);
            if (_patchedOn == null)
            {
                main.depthTextureMode |= DepthTextureMode.Depth;
                main.AddCommandBuffer(evt, _patch);
                if (fog)
                {
                    // With the MRT pass the fog copy and the patched depth are the composite's (persistent) targets
                    // instead of this frame's temporaries.
                    RenderTargetIdentifier fogDepth = mrt ? new RenderTargetIdentifier(comp.FogDepth) : new RenderTargetIdentifier(s_fogDepth);
                    _fogStart.Clear();
                    _fogStart.SetGlobalTexture(s_cameraDepth, fogDepth);
                    RenderTargetIdentifier realDepth = mrt
                        ? (virtualDepth ? new RenderTargetIdentifier(comp.ViewDepth) : new RenderTargetIdentifier(BuiltinRenderTextureType.Depth))
                        : lean ? new RenderTargetIdentifier(BuiltinRenderTextureType.Depth) : new RenderTargetIdentifier(s_patched);
                    _fogEnd.Clear();
                    // Win.TransparentDepth: the transparent objects get the fog copy (the view depths) instead; _effectsStart
                    // binds the real depth again for what comes after them.
                    _fogEnd.SetGlobalTexture(s_cameraDepth, alphaDepth ? fogDepth : realDepth);
                    // Unity binds the camera's own depth texture again as the opaque image effects start, so the
                    // copy goes first at their events, ahead of the SSAO and fog buffers added there before.
                    AddFirst(main, CameraEvent.BeforeImageEffectsOpaque, _fogStart);
                    AddFirst(main, CameraEvent.AfterImageEffectsOpaque, _fogStart);
                    if (!restoreView)
                        AddFirst(main, CameraEvent.BeforeImageEffectsOpaque, _effectsSave);
                    AddFirst(main, CameraEvent.BeforeForwardAlpha, _effectsRestore);
                    main.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, _fogEnd);
                    // The final image effects (sun shafts, TAA, bloom) see the window as the surface it is:
                    // with the far depth they would treat the sky in it as their own sky and shine through it.
                    _effectsStart.Clear();
                    _effectsStart.SetGlobalTexture(s_cameraDepth, virtualDepth ? fogDepth : realDepth);
                    main.AddCommandBuffer(EffectsPrepEvent, _effectsStart);
                    _cbChanges++;
                }
                _patchRelease.Clear();
                // Only this frame's temporaries (the composite's targets persist).
                if (!mrt && core && !lean)
                    _patchRelease.ReleaseTemporaryRT(s_patched);
                if (!mrt && fog)
                    _patchRelease.ReleaseTemporaryRT(s_fogDepth);
                main.AddCommandBuffer(CameraEvent.AfterEverything, _patchRelease);
                _patchedOn = main;
                _patchEvent = evt;
                _fogPatched = fog;
                _depthPatched = virtualDepth;
                _leanPatched = lean;
                _mrtPatched = mrt;
                _idsPatched = idsOnly;
                _restoreViewPatched = restoreView;
                _alphaPatched = alphaDepth;
                _auxFogPatched = mrt ? comp.FogDepth : null;
                _auxViewPatched = mrt ? comp.ViewDepth : null;
            }
        }

        void Unpatch()
        {
            if (_patchedOn != null)
            {
                _patchedOn.RemoveCommandBuffer(_patchEvent, _patch);
                _patchedOn.RemoveCommandBuffer(CameraEvent.AfterEverything, _patchRelease);
                if (_fogPatched)
                {
                    _patchedOn.RemoveCommandBuffer(CameraEvent.BeforeImageEffectsOpaque, _fogStart);
                    _patchedOn.RemoveCommandBuffer(CameraEvent.AfterImageEffectsOpaque, _fogStart);
                    _patchedOn.RemoveCommandBuffer(CameraEvent.BeforeImageEffectsOpaque, _effectsSave);
                    _patchedOn.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, _effectsRestore);
                    _patchedOn.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, _fogEnd);
                    _patchedOn.RemoveCommandBuffer(EffectsPrepEvent, _effectsStart);
                }
                _cbChanges++;
            }
            _patchedOn = null;
        }

        // Diagnostics: the full-screen view through `w` into `target`, optionally limited to some layers and
        // cleared to a flat colour so each layer's contribution can be told apart.
        public void RenderDebug(Camera main, IList<PortalWindow> windows, PortalWindow w, RenderTexture target, int mask, Color? clear,
                                Rect? rect = null, float? shadowDistance = null)
        {
            _main = main;
            _windows = windows;
            SetupCamera(main);
            _texW = target.width;
            _texH = target.height;
            _maskOverride = mask;
            _clearOverride = clear;
            _shadowOverride = shadowDistance;
            bool grouped = WindowFlags.EnvGroups && (BeforeGroup != null || AfterGroup != null);
            try
            {
                if (rect.HasValue)
                {
                    // Same scissor as the live window: the texture covers the rect at the live resolution.
                    _texW = Mathf.RoundToInt(target.width / rect.Value.width);
                    _texH = Mathf.RoundToInt(target.height / rect.Value.height);
                }
                if (grouped)
                    BeforeGroup?.Invoke(w);
                RenderLevel(w, main.transform.position, main.transform.rotation, rect ?? FullRect, 0, target, out _);
            }
            finally
            {
                if (grouped)
                    AfterGroup?.Invoke(w);
                _maskOverride = -1;
                _clearOverride = null;
                _shadowOverride = null;
                for (int iw = 0; iw < windows.Count; iw++)
                {
                    PortalWindow o = windows[iw];
                    o.ShowOwn();
                }
            }
        }

        int _maskOverride = -1;
        Color? _clearOverride;
        float? _shadowOverride;

        // Where the camera for the window `w` stands when the viewer is at eye/rot (for diagnostics).
        public static Vector3 ViewPosition(PortalWindow w, Vector3 eye) =>
            PortalMath.View(w.Frame, w.Partner.Frame, eye, out _).MultiplyPoint3x4(eye);

        void SetupCamera(Camera main)
        {
            _cam.CopyFrom(main);
            _cam.enabled = false;
            _cam.allowMSAA = false;
            _cam.useOcclusionCulling = false;
            _cam.clearFlags = Settings.ClearFlags;
            if (Settings.RenderingPath != RenderingPath.UsePlayerSettings)
                _cam.renderingPath = Settings.RenderingPath;
            if (WindowFlags.DepthOnly)
            {
                // Only the depth texture (fog, water, soft particles and the view depth capture read it). The game
                // camera's depth+normals have no reader in a window render (no game shader samples
                // _CameraDepthNormalsTexture; deferred SSAO takes the G-buffer normals), and its motion vectors none
                // either once the SSAO copy runs without its temporal filter (Patches.WindowOcclusionPreRender). A
                // forward window camera keeps depth+normals: its SSAO copy then takes the normals from them.
                DepthTextureMode mode = DepthTextureMode.Depth;
                if (_cam.actualRenderingPath != RenderingPath.DeferredShading)
                    mode |= main.depthTextureMode & DepthTextureMode.DepthNormals;
                _cam.depthTextureMode = mode;
            }
            else
                // Same depth textures as the game camera (it asks for depth+normals, which some of its shaders read).
                _cam.depthTextureMode = main.depthTextureMode | DepthTextureMode.Depth;
            // Objects only out to where the game loads them (WindowFlags.LayerCull): not other far areas kilometres
            // away in the view's direction. Terrain, water, sky and effects keep the far clip.
            float[] cull = WindowFlags.LayerCull ? WindowLayerCull : null;
            if (cull != null)
            {
                _cam.layerCullDistances = cull;
                _cam.layerCullSpherical = true;
                _layerCullSet = true;
            }
            else if (_layerCullSet)
            {
                // Back to what the copy of the game camera gives.
                _cam.layerCullDistances = main.layerCullDistances;
                _cam.layerCullSpherical = main.layerCullSpherical;
                _layerCullSet = false;
            }
            _cam.targetTexture = null;
            _texW = Mathf.Max(16, Mathf.RoundToInt(main.pixelWidth * Settings.Resolution));
            _texH = Mathf.Max(16, Mathf.RoundToInt(main.pixelHeight * Settings.Resolution));
        }

        // Stretch the window away from the viewer when the main camera's near plane could cut its surface.
        void UpdateDepth(PortalWindow w, Camera main)
        {
            Vector3 eye = main.transform.position;
            float d = w.Frame.SignedDistance(eye);
            float near = main.nearClipPlane;
            float halfH = near * Mathf.Tan(main.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float reach = Mathf.Sqrt(near * near + halfH * halfH * (1f + main.aspect * main.aspect)) + 0.05f;
            Vector3 local = Quaternion.Inverse(w.Frame.Rotation) * (eye - w.Frame.Center);
            if (Mathf.Abs(d) < reach && w.Frame.ContainsLocal(local, reach))
                w.SetDepth(d >= 0f ? reach * 2f : -reach * 2f);
            else
                w.SetDepth(0f);
        }

        bool WindowRect(PortalWindow w, Matrix4x4 vp, out Rect rect) => WindowRect(w, vp, out rect, out _);

        // `extent`: the opening's whole width and height on screen (screen units, not cut by the screen's edges).
        bool WindowRect(PortalWindow w, Matrix4x4 vp, out Rect rect, out Vector2 extent)
        {
            int n = w.Frame.Round ? 24 : 4;
            w.Frame.Outline(_pts, n, 0f);
            int count = n;
            if (Mathf.Abs(w.Depth) > 0.01f)
            {
                for (int i = 0; i < n; i++)
                    _pts[n + i] = _pts[i] - w.Frame.Forward * w.Depth;
                count = n * 2;
            }
            return PortalMath.ScreenRect(vp, _pts, count, out rect, out extent);
        }

        // Win.DistantLive (Docs\PARITY-PLAN.md P2): below this a window shows the vanilla look; one that rendered last frame
        // keeps rendering down to FloorKeep of it, so a window right at the floor does not blink between its view and the
        // empty ring as the camera sways.
        const float FloorKeep = 0.67f;

        /// <summary>Win.DistantLive: the opening of `w` is at least Settings.MinPixels tall and wide on the main camera's
        /// screen, by its whole extent (`extent`, screen units: a large window sliding in at the screen's edge renders from
        /// its first pixel, as the game would show the place beyond it there).</summary>
        internal bool AbovePixelFloor(PortalWindow w, Vector2 extent, Camera main, int frame)
        {
            float min = Settings.MinPixels * (w.RenderedAtFrame >= frame - 1 ? FloorKeep : 1f);
            return extent.x * main.pixelWidth >= min && extent.y * main.pixelHeight >= min;
        }

        // Renders what is seen through `w` by a viewer at eye/rot, restricted to `rect` (screen units), into
        // `target` or a new temporary texture covering just that rect. `texRect` is the screen rect it covers.
        // `carried` (a nested window's render, WindowFlags.NestedLive): the matrix that carried the main camera's world to
        // the view this window is seen in; BeforeRender and AfterRender then get this window's pair times it, the main
        // camera's world to this render's (the player, his weather and the reference position carried through both).
        // `classed`: a deeper level's rect in the nested size classes (the entry's own under Win.NestedLive, NestedClass), whose
        // texture comes from the pool as a nested window's does.
        RenderTexture RenderLevel(PortalWindow w, Vector3 eye, Quaternion rot, Rect rect, int level, RenderTexture target, out Rect texRect,
                                  Matrix4x4? carried = null, bool classed = false)
        {
            // Only the views the main camera shows feed its depth (virtual depth, motion vectors) and normals.
            bool onScreen = level == 0 && target == null;
            bool capture = onScreen && (Settings.VirtualDepth || Settings.MotionVectors || Settings.WindowSunShafts || Settings.ViewDepthForEffects);
            bool captureNormal = onScreen && Settings.VirtualDepth;
            _lastDepth = _lastNormal = null;
            PortalFrame exit = w.Partner.Frame;
            float viewScale = 1f;
            Matrix4x4 m = level == 0 ? PortalMath.View(w.Frame, exit, eye, out viewScale) : PortalMath.Pair(w.Frame, exit);
            Vector3 camPos = m.MultiplyPoint3x4(eye);
            Quaternion camRot = PortalMath.Rotation(m) * rot;
            rect = texRect = PortalMath.SnapToPixels(rect, _texW, _texH);
            if (rect.width <= 0f || rect.height <= 0f)
                return null;

            // Window Pipeline v2 (Docs\S2S3-CONTRACTS.md §7.2): an on-screen render while the manager drives the frame has
            // the window's state: its tier's effect set, a slot camera of its own (Win.CameraSlots), its cache filled right
            // before Render() and the slot events around it. Deeper levels and RenderDebug keep the shared camera; a deeper
            // level of an on-screen render with a state takes that render's effect set (resolution, shadow caps, LOD bias;
            // point-light shadows through BeforeNestedRender), so a cached tier's nested view costs what its tier allows.
            WindowManager pipe = onScreen ? Pipeline : null;
            WindowState s = pipe != null ? WindowState.For(w) : null;
            WindowState nestedOf = s == null && level > 0 ? _nestedOwner : null;
            TierParams p = s != null ? s.ParamsNow(Time.frameCount) : nestedOf != null ? _nestedParams : TierParams.Legacy;
            // Win.ShadowCost: the cost model's shadow proxy is the camera's Render() alone, this render's and its deeper
            // levels' (timed below), without the wake, catch-up and effects around them (WindowCostModel.RecordRender).
            if (s != null && WindowFlags.ShadowCost)
            {
                s.CamMs = 0f;
                s.CamFrame = -100;
            }

            // Win.NestedLive (Docs\PARITY-PLAN.md P7) applies to this view: a level-0 render, with the fallback flag (the
            // nested table is part of that test) and a bundle that has the table.
            bool nestedPath = level == 0 && WindowFlags.NestedLive && WindowFlags.FallbackFlag && ShaderHasFlag(w) && ShaderHasParity(w);
            // Level 1: through the exit the camera may see the entry window again; render that first. Win.NestedLive: the
            // entry seen in its own view is a window seen through a window like the others, so its deeper level renders at
            // least one level deep (not by Settings.Recursion alone), in the nested renders' size classes, counted and
            // timed with them and skipped as they are once this frame's nested renders are spent (RenderNested).
            RenderTexture deep = null;
            Rect deepRect = default;
            if (level < (nestedPath ? Mathf.Max(Settings.Recursion, 1) : Settings.Recursion))
            {
                Matrix4x4 innerView = Matrix4x4.TRS(camPos, camRot, new Vector3(1f, 1f, -1f)).inverse;
                Matrix4x4 innerVp = Perspective(_main, Mathf.Min(_main.farClipPlane, Settings.FarClip)) * innerView;
                int n = w.Frame.Round ? 24 : 4;
                w.Frame.Outline(_pts, n, 0f);
                // Only when the entry lies beyond the exit plane, i.e. in the part the camera actually draws.
                if (exit.SignedDistance(w.Frame.Center) * exit.SignedDistance(camPos) < 0f
                    && PortalMath.ScreenRect(innerVp, _pts, n, out Rect inner))
                {
                    inner = PortalMath.Intersect(inner, rect);
                    int frame = Time.frameCount;
                    if (nestedPath)
                        NestedFrameStart(frame);
                    if (inner.width > 0f && nestedPath && !NestedFits(w, frame))
                        LabNestedSkipped++;
                    else if (inner.width > 0f)
                    {
                        if (nestedPath)
                            inner = NestedClass(inner, rect);
                        long t0 = nestedPath ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
                        WindowState ownerBefore = _nestedOwner;
                        TierParams paramsBefore = _nestedParams;
                        if (s != null)
                        {
                            _nestedOwner = s;
                            _nestedParams = p;
                        }
                        try
                        {
                            deep = RenderLevel(w, camPos, camRot, inner, level + 1, null, out deepRect, null, nestedPath);
                        }
                        finally
                        {
                            _nestedOwner = ownerBefore;
                            _nestedParams = paramsBefore;
                            if (nestedPath)
                            {
                                _nestedCount++;
                                _nestedMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * s_camTickMs;
                            }
                        }
                        if (nestedPath && deep != null)
                        {
                            w.NestedAtFrame = frame;
                            LabNestedRenders++;
                        }
                    }
                }
            }
            // Win.NestedLive (P7): the other live windows this view shows render at level 1 first too (the entry itself as
            // above), into the shader's window table for this render.
            int nested = 0;
            if (nestedPath)
                nested = RenderNested(w, camPos, camRot, rect, m, s, p);

            Camera cam = _cam;
            if (s != null)
            {
                s.Slot = -1;
                Camera slot = pipe.Pool != null && pipe.Effective(WindowMechanism.CameraSlots) ? pipe.Pool.Acquire(s, _main, Settings) : null;
                if (slot != null)
                {
                    cam = slot;
                    // Set up once by the pool (layer culling included); the culling mask the game may change at any time.
                    cam.cullingMask = _main.cullingMask;
                }
                else
                    pipe.Stats.LegacyCameraRenders++;
            }

            cam.transform.SetPositionAndRotation(camPos, camRot);
            Matrix4x4 view = cam.worldToCameraMatrix;
            float far = Mathf.Min(_main.farClipPlane, Settings.FarClip);
            bool jittered = false;
            if (Settings.Clip == PortalClip.Oblique)
            {
                Matrix4x4 proj = Perspective(_main, far);
                Matrix4x4 obl = proj;
                float side = exit.SignedDistance(camPos);
                // Closer than the near plane the oblique plane only skews depth for nothing.
                if (Mathf.Abs(side) > _main.nearClipPlane + 0.05f)
                {
                    Vector3 visible = side > 0f ? -exit.Forward : exit.Forward;
                    obl = PortalMath.Oblique(proj, PortalMath.CameraSpacePlane(view, exit.Center, visible, 0.01f));
                }
                Matrix4x4 scissor = PortalMath.Scissor(rect);
                cam.usePhysicalProperties = false;
                cam.projectionMatrix = scissor * obl;
                cam.cullingMatrix = scissor * proj * view;
            }
            else
            {
                float near = NearToOpening(exit, view, _main.nearClipPlane);
                // Win.NearSteps (Docs\RIPPLE-PLAN.md §4.5b): the opening's depth floored to 2 % steps.
                if (WindowFlags.NearSteps)
                    near = NearStep(near, _main.nearClipPlane);
                if (Settings.MinNear != null)
                    near = Mathf.Max(near, Settings.MinNear(w, camPos, view));
                LensShift(cam, _main, rect, Mathf.Min(near, far * 0.5f), far);
                if (level == 0)
                {
                    if (WindowFlags.ExplicitJitter)
                        jittered = JitterExplicitly(cam, rect);
                    else
                        cam.lensShift += new Vector2(Settings.Jitter.x / rect.width, Settings.Jitter.y / rect.height);
                    // Win.ShadowFullCull (§4.5a), or the lab's P47 switch (L5): culled by the game camera's whole frustum
                    // from here.
                    if (WindowFlags.ShadowFullCull || LabShadowCullFull)
                        cam.cullingMatrix = FullFrustum(cam.nearClipPlane, cam.farClipPlane) * view;
                }
            }
            // A camera that had an explicit jitter before: this render's unjittered projection is its projection (Unity
            // documents no reset of nonJitteredProjectionMatrix), and its transparent objects use the projection again.
            if (!jittered && _explicitJitter)
            {
                cam.nonJitteredProjectionMatrix = cam.projectionMatrix;
                cam.useJitteredProjectionMatrixForTransparentRendering = true;
            }
            // Only the top level's sizes are size classes (a deeper level's rect is cut to what it shows), and (Win.NestedLive)
            // a nested window's and the entry's own deeper level's, grown to 32 px classes (NestedClass): at their exact
            // sizes, which change in most frames as the player moves or turns, each was a new HDR texture almost every frame.
            bool pooled = level == 0 || carried.HasValue || classed;
            if (target == null)
            {
                int tw = Mathf.Max(1, Mathf.RoundToInt(rect.width * _texW)), th = Mathf.Max(1, Mathf.RoundToInt(rect.height * _texH));
                // A cheaper tier's resolution, in 16 px steps (the rect is in 32 px classes, so its sizes stay few).
                if (p.ResolutionScale > 0f && Mathf.Abs(p.ResolutionScale - 1f) > 1e-3f)
                {
                    tw = Steps16(tw * p.ResolutionScale);
                    th = Steps16(th * p.ResolutionScale);
                }
                target = Temp(tw, th, null, pooled);
            }
            cam.targetTexture = target;
            cam.rect = FullRect;
            if (_maskOverride != -1)
                cam.cullingMask = _maskOverride;
            if (_clearOverride.HasValue)
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = _clearOverride.Value;
            }

            // Inside this render: other windows show their fallback, the exit is hidden (it lies on the clip
            // plane), and the entry shows the deeper level when there is one. With the fallback flag the window
            // shader learns that from two globals set for just this render (below), instead of every window's
            // material being switched for every render; the windows rendered nested (Win.NestedLive) from the table set
            // with them.
            bool flag = WindowFlags.FallbackFlag && ShaderHasFlag(w);
            if (!flag)
                for (int iw = 0; iw < _windows.Count; iw++)
                    _windows[iw].ShowFallback();
            if (deep != null)
                w.ShowTexture(deep, deepRect);
            // What this render changes is read here and put back by the finally below, whatever throws on the way (the
            // plugin's BeforeRender does a lot: waking, catch-up, copies): the exit's renderer, the quality settings and the
            // viewport. Thrown before the try, the exit stayed hidden for good, and the render around a nested one
            // (RenderNested goes on after a failed one) took the nested lodBias for the game's and put that back.
            bool exitWasOn = w.Partner.Renderer.enabled;
            float shadow = QualitySettings.shadowDistance, lod = QualitySettings.lodBias;
            int cascades = QualitySettings.shadowCascades;
            RenderTexture depthRT = null, normalRT = null;
            // Win.CaptureOnce: the capture buffers stay on every camera they were put on and hold something only during a
            // render that captures (recorded here, cleared after it), so any other render of such a camera (a deeper
            // level, RenderDebug, a render that threw before its finally) runs them empty. Read once for this render.
            bool captureOnce = WindowFlags.CaptureOnce;
            Matrix4x4 hooked = carried.HasValue ? m * carried.Value : m;
            // BeforeRender was called (AfterRender then is too, by the finally), the slot or nested handlers were raised, and
            // the camera the nested restore went on (Win.NestedLive, RestoreNested).
            bool beforeCalled = false, raised = false, nestedRaised = false;
            Camera restoreOn = null;
            // Win.ShadowCost: whose camera ms this Render() adds to (a deeper level's go to the on-screen render it is in).
            WindowState timed = WindowFlags.ShadowCost ? s ?? nestedOf : null;
            try
            {
                w.Partner.Renderer.enabled = false;

                if (LabCascades >= 0)
                    QualitySettings.shadowCascades = LabCascades;
                else if (p.ShadowCascadesCap >= 0 && p.ShadowCascadesCap < cascades)
                    QualitySettings.shadowCascades = p.ShadowCascadesCap;
                if (_shadowOverride.HasValue)
                    QualitySettings.shadowDistance = _shadowOverride.Value;
                else if (Settings.ShadowDistance >= 0f || p.ShadowDistanceCap >= 0f)
                {
                    // The renderer's own cap, then the tier's (Win.ShadowTiers).
                    float d = Settings.ShadowDistance >= 0f ? Mathf.Min(shadow, Settings.ShadowDistance) : shadow;
                    if (p.ShadowDistanceCap >= 0f)
                        d = Mathf.Min(d, p.ShadowDistanceCap);
                    QualitySettings.shadowDistance = d;
                }
                // LOD picks by the camera's field of view; the lens-shifted camera's is only the window rect's share of
                // the game's (tan of the half angle scales with the rect height), so objects would read as nearer
                // and switch LOD at other distances than in the game.
                float lodScale = Settings.Clip == PortalClip.Oblique || !Settings.LodMatchesGame ? 1f : rect.height;
                // A scaled view (through a smaller exit) shows things bigger than their distance says; a cheaper tier
                // may lower the detail further (LodBiasScale, 1 = as the game).
                QualitySettings.lodBias = lod * Settings.LodBias * lodScale / viewScale * p.LodBiasScale;
                if (onScreen)
                    w.DepthScale = 1f / viewScale;
                Shader.SetGlobalVector(s_viewport, new Vector4(rect.x, rect.y, rect.width, rect.height));
                if (!captureOnce && _captureOn.Count > 0)
                    DetachCapture();
                if (_captureRecorded)
                    ClearCapture();
                if (capture)
                {
                    EnsureUtil(w);
                    depthRT = Temp(target.width, target.height, RenderTextureFormat.RFloat, pooled);
                    _captureDepth.Clear();
                    _captureDepth.Blit(BuiltinRenderTextureType.CameraTarget, depthRT, _util, PassLinearDepth);
                    // Its depth range and water share for the error model (their only reader), read back two frames later.
                    if (s != null && pipe.Visibility != null && pipe.Visibility.Ready && pipe.Effective(WindowMechanism.ErrorModel))
                        pipe.Visibility.AppendDepthStats(_captureDepth, s, depthRT, target.width, target.height, cam);
                    if (captureOnce)
                    {
                        // The normals buffer is on the camera too: recorded below when this render captures them, else empty.
                        _captureNormal.Clear();
                        _captureRecorded = true;
                        AttachCapture(cam);
                    }
                    else
                        cam.AddCommandBuffer(CameraEvent.AfterForwardAlpha, _captureDepth);
                    if (captureNormal && cam.actualRenderingPath == RenderingPath.DeferredShading)
                    {
                        normalRT = Temp(target.width, target.height, RenderTextureFormat.ARGB2101010, pooled);
                        _captureNormal.Clear();
                        _captureNormal.Blit(BuiltinRenderTextureType.GBuffer2, normalRT);
                        if (!captureOnce)
                            cam.AddCommandBuffer(CameraEvent.AfterGBuffer, _captureNormal);
                    }
                }
                // The views on screen: instanced clutter (grass) culls itself against the game camera only, and is
                // let through for these.
                if (onScreen)
                {
                    Plane[] f = Frustum(cam);
                    Frusta.Add(f);
                    ViewPositions.Add(camPos);
                    System.Array.Copy(f, w.ViewFrustum, 6);
                    w.ViewPos = camPos;
                    w.HasView = true;
                }
                else if (carried.HasValue)
                {
                    // Win.NestedLive: a nested window's view is drawn this frame too (grass let through, far effects by view),
                    // but it is not the window's own on-screen view (ViewFrustum/ViewPos stay that one's, which the game side
                    // tells on-screen renders by).
                    Frusta.Add(Frustum(cam));
                    ViewPositions.Add(camPos);
                }
                if (s != null)
                    FillCache(s, w, cam, m, viewScale, camPos, camRot, rect, target, p);
                beforeCalled = true;
                BeforeRender?.Invoke(w, hooked, camPos);
                // Win.NestedLive: the nested windows' views (and the entry's own deeper level) put back over this view's
                // opaque fog and AO, which their own cameras applied already (RestoreNested).
                if (nestedPath && (nested > 0 || deep != null))
                    restoreOn = RestoreNested(cam, w, nested, deep != null);
                if (flag)
                {
                    Shader.SetGlobalFloat(s_inWindowRender, 1f);
                    Shader.SetGlobalFloat(s_renderingWindowId, deep != null ? w.Id : 0f);
                    if (nested > 0)
                        SetNestedTable(nested);
                }
                InWindowRender = true;
                WindowRenderLevel = level;
                // Last before Render(): nothing between the handlers (the camera-targeted draws last of all) and the
                // camera's only render of the frame can skip it (P3(ii)).
                if (s != null)
                {
                    raised = true;
                    pipe.RaiseBeforeSlotRender(s, cam);
                }
                else if (nestedOf != null)
                {
                    nestedRaised = true;
                    RaiseNested(BeforeNestedRender, nestedOf, cam, "BeforeNestedRender");
                }
                else if (BeforePlainRender != null)
                {
                    try
                    {
                        BeforePlainRender(w, cam);
                    }
                    catch (Exception e)
                    {
                        WindowLog.WarnEvery("PortalRenderer.BeforePlainRender", 10f, "BeforePlainRender handler failed: " + e);
                    }
                }
                long c0 = timed != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
                cam.Render();
                if (timed != null)
                {
                    timed.CamMs += (float)((System.Diagnostics.Stopwatch.GetTimestamp() - c0) * s_camTickMs);
                    timed.CamFrame = Time.frameCount;
                }
                RendersLastFrame++;
            }
            catch (Exception)
            {
                if (s != null)
                {
                    // The cache's camera fields describe a render that did not happen: renew it as soon as possible.
                    s.Cache.Frame = -100;
                    s.Invalid |= WindowInvalid.NoCache;
                    if (raised)
                        WindowLog.WarnEvery("PortalRenderer.SlotRender", 10f,
                            "A window render failed after BeforeSlotRender: draws queued for that camera show in its next render (P3(ii)).");
                }
                throw;
            }
            finally
            {
                InWindowRender = false;
                WindowRenderLevel = 0;
                if (flag)
                {
                    Shader.SetGlobalFloat(s_inWindowRender, 0f);
                    Shader.SetGlobalFloat(s_renderingWindowId, 0f);
                    if (nested > 0)
                        Shader.SetGlobalVector(s_nestedIds, Vector4.zero);
                }
                if (capture)
                {
                    if (captureOnce)
                        ClearCapture();
                    else
                    {
                        cam.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha, _captureDepth);
                        if (normalRT != null)
                            cam.RemoveCommandBuffer(CameraEvent.AfterGBuffer, _captureNormal);
                    }
                    _lastDepth = depthRT;
                    _lastNormal = normalRT;
                }
                if (restoreOn != null)
                    restoreOn.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, _nestedRestore);
                if (raised)
                    pipe.RaiseAfterSlotRender(s, cam);
                if (nestedRaised)
                    RaiseNested(AfterNestedRender, nestedOf, cam, "AfterNestedRender");
                if (beforeCalled)
                    AfterRender?.Invoke(w, hooked, camPos);
                QualitySettings.shadowDistance = shadow;
                QualitySettings.shadowCascades = cascades;
                QualitySettings.lodBias = lod;
                w.Partner.Renderer.enabled = exitWasOn;
                cam.ResetCullingMatrix();
                cam.ResetProjectionMatrix();
                cam.targetTexture = null;
                Shader.SetGlobalVector(s_viewport, FullViewport);
            }
            return target;
        }

        static int Steps16(float px) => Mathf.Max(16, (Mathf.CeilToInt(px - 1e-3f) + 15) / 16 * 16);

        // Win.CaptureOnce: both capture buffers put on `cam` the first time it renders a capture (a slot camera that was
        // destroyed took them with it: its entry goes). Adding and removing a camera's buffers around every render costs
        // main-thread time (FARWORLD-V3 §4.3: capture CBs attached once).
        void AttachCapture(Camera cam)
        {
            for (int i = 0; i < _captureOn.Count; i++)
                if (_captureOn[i] == cam)
                    return;
            for (int i = _captureOn.Count - 1; i >= 0; i--)
                if (_captureOn[i] == null)
                    _captureOn.RemoveAt(i);
            cam.AddCommandBuffer(CameraEvent.AfterForwardAlpha, _captureDepth);
            cam.AddCommandBuffer(CameraEvent.AfterGBuffer, _captureNormal);
            _captureOn.Add(cam);
        }

        // Win.CaptureOnce: the buffers empty again once their render is done.
        void ClearCapture()
        {
            _captureDepth.Clear();
            _captureNormal.Clear();
            _captureRecorded = false;
        }

        // Win.CaptureOnce off, or the renderer going: the buffers off every camera they were put on.
        void DetachCapture()
        {
            for (int i = 0; i < _captureOn.Count; i++)
            {
                Camera c = _captureOn[i];
                if (c == null)
                    continue;
                c.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha, _captureDepth);
                c.RemoveCommandBuffer(CameraEvent.AfterGBuffer, _captureNormal);
            }
            _captureOn.Clear();
            ClearCapture();
        }

        // Lab: shadow cascades for the window renders (-1: the game's).
        public static int LabCascades = -1;

        // Lab (Docs\RIPPLE-PLAN.md L5, P47): an on-screen view's camera culls by the game camera's whole frustum from its
        // place (this frame's TAA jitter, the view's near and far planes) while its projection stays the rect's: whether
        // Unity fits the sun's cascades to the culling matrix or to the projection. Reset with the culling matrix after
        // the render. WindowFlags.ShadowFullCull does the same as a switch of the pipeline.
        public static bool LabShadowCullFull;

        // The game camera's jittered projection (its field of view and aspect, this frame's TAA jitter) with these planes.
        Matrix4x4 FullFrustum(float near, float far)
        {
            Matrix4x4 p = Matrix4x4.Perspective(_main.fieldOfView, _main.aspect, near, far);
            p.m02 += 2f * Settings.Jitter.x;
            p.m12 += 2f * Settings.Jitter.y;
            return p;
        }

        // Win.NearSteps: `near` floored to the next power of 1.02 below it (steps 2 % of the depth apart, the same for every
        // render), never below the game camera's near plane. The step lets up to 2 % of the depth in front of the opening
        // into the view (what stands right behind the exit can show as a sliver).
        static readonly float s_nearStepLog = Mathf.Log(1.02f);

        static float NearStep(float near, float minNear)
        {
            if (!(near > 0f))
                return near;
            return Mathf.Max(minNear, Mathf.Pow(1.02f, Mathf.Floor(Mathf.Log(near) / s_nearStepLog)));
        }

        bool ShaderHasFlag(PortalWindow w)
        {
            Shader s = w.Material.shader;
            if (s != _flagShader)
            {
                _flagShader = s;
                _flagShaderOk = w.ShaderHasFallbackFlag;
            }
            return _flagShaderOk;
        }

        // A bundle whose window shader has the parity passes (Docs\PARITY-PLAN.md P7, P42, P43): the nested window table, the
        // fog copy's mode 3 and the reflection probes' discard. With an older one Win.NestedLive and Win.TransparentDepth do
        // nothing (its windows would show neither).
        bool ShaderHasParity(PortalWindow w)
        {
            Shader s = w.Material.shader;
            if (s != _parityShader)
            {
                _parityShader = s;
                _parityShaderOk = w.ShaderHasParity;
            }
            return _parityShaderOk;
        }

        /// <summary>
        /// Win.NestedLive (Docs\PARITY-PLAN.md P7): the other live windows the level-0 view through `w` shows (its camera at
        /// camPos/camRot, drawing `rect` of its frame; `m` carries the main camera's world to that view's), rendered at level
        /// 1 from Pair(window, its partner)·camPos over their rect in that frame, as the game camera standing there would
        /// render them: beyond the exit's plane, within Settings.MaxDistance of that camera and above the pixel floor
        /// (Win.DistantLive; else MinScreenFraction). The largest first, at most Settings.NestedPerFrame a frame, while this
        /// frame's nested renders so far took under Settings.NestedMs of main thread, and NestedTable in this view. Fills
        /// the table RenderLevel sets for the level-0 render and returns its length; a window left out shows the vanilla
        /// look there (LabNestedSkipped). `owner`/`ownerParams`: the level-0 render's state and effect set, which its deeper
        /// renders take, as the entry's own deeper level (Settings.Recursion) does.
        /// </summary>
        int RenderNested(PortalWindow w, Vector3 camPos, Quaternion camRot, Rect rect, Matrix4x4 m, WindowState owner, TierParams ownerParams)
        {
            _nestedIds = Vector4.zero;
            IList<PortalWindow> windows = _windows;
            if (windows == null || windows.Count < 2)
                return 0;
            int frame = Time.frameCount;
            NestedFrameStart(frame);
            PortalFrame exit = w.Partner.Frame;
            float side = exit.SignedDistance(camPos);
            // The level-0 camera's whole frame (the main camera's field of view from camPos), and the part this render draws.
            Matrix4x4 innerView = Matrix4x4.TRS(camPos, camRot, new Vector3(1f, 1f, -1f)).inverse;
            Matrix4x4 innerVp = Perspective(_main, Mathf.Min(_main.farClipPlane, Settings.FarClip)) * innerView;
            GeometryUtility.CalculateFrustumPlanes(PortalMath.Scissor(rect) * innerVp, _nestedPlanes);
            bool floor = WindowFlags.DistantLive;
            _nestedCandidates.Clear();
            for (int i = 0; i < windows.Count; i++)
            {
                PortalWindow o = windows[i];
                // Live (enabled, its partner too: the game side lists only windows whose far side is there) and drawn.
                if (o == null || o == w || o == w.Partner || !o.Enabled || o.Partner == null || !o.Partner.Enabled
                    || !o.Renderer.enabled || !o.Root.activeInHierarchy)
                    continue;
                if (exit.SignedDistance(o.Frame.Center) * side >= 0f)
                    continue;
                float half = Mathf.Max(o.Frame.HalfSize.x, o.Frame.HalfSize.y);
                if (Vector3.Distance(camPos, o.Frame.Center) > Settings.MaxDistance + half
                    || !GeometryUtility.TestPlanesAABB(_nestedPlanes, o.Renderer.bounds))
                    continue;
                int n = o.Frame.Round ? 24 : 4;
                o.Frame.Outline(_pts, n, 0f);
                if (!PortalMath.ScreenRect(innerVp, _pts, n, out Rect inner, out Vector2 extent))
                    continue;
                // The floor on the main camera's screen (the level-0 frame has its field of view and size); one rendered
                // last frame keeps rendering down to FloorKeep of it, as an on-screen window does.
                if (floor)
                {
                    float min = Settings.MinPixels * (o.NestedAtFrame >= frame - 1 ? FloorKeep : 1f);
                    if (extent.x * _main.pixelWidth < min || extent.y * _main.pixelHeight < min)
                        continue;
                }
                inner = PortalMath.Intersect(inner, rect);
                float share = inner.width * inner.height;
                if (share <= 0f || (!floor && share < Settings.MinScreenFraction))
                    continue;
                // Largest first (among equals in the order they came; a few windows, no sort).
                int at = _nestedCandidates.Count;
                while (at > 0 && _nestedCandidates[at - 1].share < share)
                    at--;
                _nestedCandidates.Insert(at, (o, inner, share));
            }
            if (_nestedCandidates.Count == 0)
                return 0;
            int table = 0;
            for (int k = 0; k < _nestedCandidates.Count; k++)
            {
                var c = _nestedCandidates[k];
                if (table >= NestedTable || !NestedFits(c.w, frame))
                {
                    LabNestedSkipped++;
                    continue;
                }
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                WindowState ownerBefore = _nestedOwner;
                TierParams paramsBefore = _nestedParams;
                if (owner != null)
                {
                    _nestedOwner = owner;
                    _nestedParams = ownerParams;
                }
                RenderTexture view = null;
                Rect viewRect = default;
                try
                {
                    // In the nested size classes (NestedClass), so its texture comes from the pool in a few sizes.
                    view = RenderLevel(c.w, camPos, camRot, NestedClass(c.rect, rect), 1, null, out viewRect, m);
                }
                catch (Exception e)
                {
                    // Its render put back what it changed (RenderLevel reads the exit's renderer, the quality settings and
                    // the viewport before its try, and its finally restores them whatever threw): the entry's view goes on
                    // without it.
                    WindowLog.WarnEvery("PortalRenderer.Nested", 10f, "A nested window render failed (that window shows the vanilla look): " + e);
                    view = null;
                }
                finally
                {
                    _nestedOwner = ownerBefore;
                    _nestedParams = paramsBefore;
                    _nestedCount++;
                    _nestedMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * s_camTickMs;
                }
                if (view == null)
                {
                    LabNestedSkipped++;
                    continue;
                }
                c.w.NestedAtFrame = frame;
                _nestedIds[table] = c.w.Id;
                _nestedWins[table] = c.w;
                _nestedViews[table] = view;
                _nestedRects[table] = new Vector4(viewRect.x, viewRect.y, viewRect.width, viewRect.height);
                table++;
                LabNestedRenders++;
            }
            return table;
        }

        // Win.NestedLive: the table RenderNested filled, for the window shader during this level-0 render (RenderLevel clears
        // its ids after it).
        void SetNestedTable(int count)
        {
            for (int i = 0; i < count; i++)
            {
                Shader.SetGlobalTexture(s_nestedTex[i], _nestedViews[i]);
                Shader.SetGlobalVector(s_nestedRect[i], _nestedRects[i]);
            }
            Shader.SetGlobalVector(s_nestedIds, _nestedIds);
        }

        // Win.NestedLive: this frame's nested renders so far (Settings.NestedPerFrame, NestedMs) start at none in its first.
        void NestedFrameStart(int frame)
        {
            if (_nestedFrame == frame)
                return;
            _nestedFrame = frame;
            _nestedCount = 0;
            _nestedMs = 0.0;
        }

        // Win.NestedLive: one more nested render of `o` fits this frame's (Settings.NestedPerFrame, NestedMs). One rendered
        // nested last frame keeps its render up to NestedKeep of the time budget, so a window at the budget's edge does not
        // blink between its view and the vanilla look as the renders' times vary.
        bool NestedFits(PortalWindow o, int frame) =>
            _nestedCount < Settings.NestedPerFrame && _nestedMs < Settings.NestedMs * (o.NestedAtFrame >= frame - 1 ? NestedKeep : 1f);

        // Win.NestedLive: a nested view's rect (screen units) grown to 32 px classes about its centre, as the level-0 rects'
        // classes, and moved, not cut, to stay inside the level-0 rect `within` it is drawn in (at most all of it), on whole
        // pixels. The level-0 render's table rect (_RP_NestedRect, from the rect it rendered) describes the grown rect, so the
        // mapping stays exact; the view costs at most 31 px more each way.
        Rect NestedClass(Rect r, Rect within)
        {
            int x0 = Mathf.RoundToInt(within.xMin * _texW), x1 = Mathf.RoundToInt(within.xMax * _texW);
            int y0 = Mathf.RoundToInt(within.yMin * _texH), y1 = Mathf.RoundToInt(within.yMax * _texH);
            SnapSpan(r.xMin, r.xMax, x0, x1, _texW, 32, out float lx, out float hx);
            SnapSpan(r.yMin, r.yMax, y0, y1, _texH, 32, out float ly, out float hy);
            return Rect.MinMaxRect(lx, ly, hx, hy);
        }

        // Win.NestedLive (P7): a nested window's view was fogged and shaded (its SSAO) by its own camera already, and was then
        // drawn into this level-0 view, whose camera's opaque effects (the slot's fog copy, or the shared camera's
        // PostProcessing fog, and its SSAO) ran over it once more: the stretch from this camera to the opening fogged twice, an
        // AO blot on one close by. As the game camera's Win.RestoreFromWindow does for the windows on screen, its pixels are
        // shaded again from its view (the shader's RESTOREVIEW: nestedView, where this view sees the window by its own depth)
        // after those effects (CameraEvent.BeforeForwardAlpha), for every table entry and the entry's own deeper level.
        // Returns the camera the buffer went on (RenderLevel's finally takes it off), or null (a bundle without the pass).
        Camera RestoreNested(Camera cam, PortalWindow w, int nested, bool entry)
        {
            Shader sh = w.Material.shader;
            if (sh != _restoreShader)
            {
                _restoreShader = sh;
                _restorePass = w.Material.FindPass(WindowPasses.RestoreViewName);
            }
            if (_restorePass < 0)
                return null;
            _nestedRestore.Clear();
            _nestedRestore.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            for (int i = 0; i < nested; i++)
            {
                PortalWindow o = _nestedWins[i];
                if (o != null && o.Renderer != null)
                    _nestedRestore.DrawRenderer(o.Renderer, o.Material, 0, _restorePass);
            }
            if (entry)
                _nestedRestore.DrawRenderer(w.Renderer, w.Material, 0, _restorePass);
            cam.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, _nestedRestore);
            return cam;
        }

        static Rect Quantize(Rect r, int texW, int texH, int step)
        {
            int w = Mathf.CeilToInt(r.width * texW / step) * step, h = Mathf.CeilToInt(r.height * texH / step) * step;
            int x = Mathf.RoundToInt(r.center.x * texW - w * 0.5f), y = Mathf.RoundToInt(r.center.y * texH - h * 0.5f);
            return new Rect((float)x / texW, (float)y / texH, (float)w / texW, (float)h / texH);
        }

        // The rect (on screen) grown to whole multiples of `step` texture pixels about its centre and moved, not cut,
        // to stay on the screen: a window at the screen's edge keeps a size class too (at most the whole screen).
        internal static Rect QuantizeOnScreen(Rect r, int texW, int texH, int step)
        {
            SnapSpan(r.xMin, r.xMax, texW, step, out float x0, out float x1);
            SnapSpan(r.yMin, r.yMax, texH, step, out float y0, out float y1);
            return Rect.MinMaxRect(x0, y0, x1, y1);
        }

        static void SnapSpan(float a, float b, int size, int step, out float lo, out float hi) => SnapSpan(a, b, 0, size, size, step, out lo, out hi);

        // The same within pixels min..max of the `size` pixels (Win.NestedLive: a nested view inside its level-0 rect).
        static void SnapSpan(float a, float b, int min, int max, int size, int step, out float lo, out float hi)
        {
            const float eps = 1e-3f;
            int p0 = Mathf.Clamp(Mathf.FloorToInt(a * size + eps), min, max), p1 = Mathf.Clamp(Mathf.CeilToInt(b * size - eps), min, max);
            int span = p1 - p0, n = (span + step - 1) / step * step;
            if (span <= 0)
            {
                lo = hi = (float)p0 / size;
                return;
            }
            if (n >= max - min)
            {
                lo = (float)min / size;
                hi = (float)max / size;
                return;
            }
            // Still covering p0..p1: grown evenly, then pushed back inside the bounds.
            int start = Mathf.Clamp(p0 - (n - span) / 2, min, max - n);
            lo = (float)start / size;
            hi = (float)(start + n) / size;
        }

        static Matrix4x4 Perspective(Camera cam, float far) =>
            Matrix4x4.Perspective(cam.fieldOfView, cam.aspect, cam.nearClipPlane, far);

        static readonly Vector3[] s_openingPts = new Vector3[32];

        // Nearest depth (along the view) of the exit opening: everything seen through it lies farther away, so a
        // near plane there drops most of what stands between the camera and the exit portal (main thread only).
        public static float NearToOpening(PortalFrame exit, Matrix4x4 worldToCamera, float minNear)
        {
            const int n = 32;
            Vector3[] pts = s_openingPts;
            exit.Outline(pts, n, 0f);
            float nearest = float.MaxValue;
            for (int i = 0; i < n; i++)
                nearest = Mathf.Min(nearest, -worldToCamera.MultiplyPoint3x4(pts[i]).z);
            // The outline polygon is circumscribed, so its depth can only undershoot the real rim.
            return Mathf.Max(minNear, nearest - 0.02f);
        }

        // An ordinary perspective whose frustum is the part of the main camera's frustum behind `rect`,
        // made with the physical camera's lens shift so Unity itself knows the projection (deferred lighting,
        // shadow cascades, image effects that rebuild rays from the camera all stay right).
        public static void LensShift(Camera cam, Camera main, Rect rect, float near, float far)
        {
            cam.ResetProjectionMatrix();
            cam.ResetCullingMatrix();
            cam.usePhysicalProperties = true;
            cam.gateFit = Camera.GateFitMode.None;
            const float focal = 50f;
            float h = 2f * focal * Mathf.Tan(main.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float wFull = h * main.aspect;
            cam.focalLength = focal;
            cam.sensorSize = new Vector2(wFull * rect.width, h * rect.height);
            cam.lensShift = new Vector2((rect.center.x - 0.5f) / rect.width, (rect.center.y - 0.5f) / rect.height);
            cam.nearClipPlane = near;
            cam.farClipPlane = far;
        }

        // Win.ExplicitJitter (FARWORLD-V3 §4.3): the game camera's TAA jitter on a lens-shifted window camera as its own
        // TaaComponent puts it on the game camera (SetProjectionMatrix): the lens-shift projection as it is becomes
        // nonJitteredProjectionMatrix (Unity's motion vectors use it), the same with this frame's jitter (Settings.Jitter,
        // read off the game camera's projection at its onPreCull) projectionMatrix, and transparent objects render
        // unjittered, as the game camera's do. The jittered matrix is the one the lens shift gave with the jitter added to
        // it (a lens shift of s puts 2s into m02/m12; Jitter is the game camera's offset in screen units, the rect's share
        // of the screen scales it), so the window's pixels keep this frame's jitter index exactly as before.
        bool JitterExplicitly(Camera cam, Rect rect)
        {
            Matrix4x4 plain = cam.projectionMatrix;
            Matrix4x4 jittered = plain;
            jittered.m02 += 2f * Settings.Jitter.x / rect.width;
            jittered.m12 += 2f * Settings.Jitter.y / rect.height;
            cam.nonJitteredProjectionMatrix = plain;
            cam.projectionMatrix = jittered;
            cam.useJitteredProjectionMatrixForTransparentRendering = false;
            _explicitJitter = true;
            return true;
        }

        Plane[] Frustum(Camera cam)
        {
            int i = Frusta.Count;
            while (_frustumPool.Count <= i)
                _frustumPool.Add(new Plane[6]);
            GeometryUtility.CalculateFrustumPlanes(cam, _frustumPool[i]);
            return _frustumPool[i];
        }

        void EnsureUtil(PortalWindow w)
        {
            if (_util == null)
                _util = new Material(w.Material.shader) { name = "RealPortalUtil" };
        }

        // pooled: a size class of the window texture pool (WindowFlags.RTPool); otherwise one of Unity's temporaries.
        RenderTexture Temp(int width, int height, RenderTextureFormat? format = null, bool pooled = true)
        {
            RenderTextureFormat f = format ?? (_main.allowHDR ? RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default);
            int depth = format.HasValue ? 0 : 24;
            RenderTexture rt = WindowFlags.RTPool && pooled ? WindowRTPool.Get(width, height, depth, f) : RenderTexture.GetTemporary(width, height, depth, f);
            // Win.PointDepth: the view depth (the only RFloat view texture) read texel by texel by the composite passes.
            rt.filterMode = WindowFlags.PointDepth && format == RenderTextureFormat.RFloat ? FilterMode.Point : FilterMode.Bilinear;
            rt.wrapMode = TextureWrapMode.Clamp;
            _temps.Add(rt);
            return rt;
        }

        void ReleaseTemps()
        {
            for (int i = 0; i < _temps.Count; i++)
                WindowRTPool.Return(_temps[i]);
            _temps.Clear();
        }

        // ---- Window Pipeline v2: the pieces of RenderAll the WindowManager drives (Docs\S2S3-CONTRACTS.md §4, §7.2) ----
        // RenderAll is BeginFrame, its count scheduler, RenderShown/ShowStale per window and EndFrame; with Win.Tiers the
        // manager calls the same pieces with its own choice, so both paths render, own, bind and composite alike.

        /// <summary>The manager driving this frame's renders (set by WindowManager.RenderChosen while any S2/S3 mechanism is
        /// in force): RenderLevel gives each on-screen render its state, tier effects, slot camera, cache fill and slot
        /// events. Null: RenderLevel as post-S1.</summary>
        internal WindowManager Pipeline;
        /// <summary>Set by the manager around RenderAll while Win.Tiers is not in force: RenderAll reports its choices.</summary>
        internal WindowManager Hook;
        /// <summary>Around a deeper level's render (Settings.Recursion) that belongs to an on-screen render with a window
        /// state, right before and after its Render(), with that state and the shared camera: the tier effects the plugin
        /// applies around the on-screen render (point-light shadows) apply to it too. Never raised without the manager.
        /// Handlers undo in After what they did in Before; a throwing one is logged and skipped.</summary>
        public Action<WindowState, Camera> BeforeNestedRender, AfterNestedRender;
        /// <summary>Right before the Render() of a window render that raises neither BeforeSlotRender nor
        /// BeforeNestedRender (no manager driving the frame, a deeper level of such a render, RenderDebug), with the window
        /// rendered and the camera: camera-targeted draws (the far proxies) reach those renders too. Last before Render()
        /// (P3(ii)); a throwing handler is logged and skipped.</summary>
        public Action<PortalWindow, Camera> BeforePlainRender;
        // The on-screen render whose deeper levels are being rendered, and its effect set (RenderLevel).
        WindowState _nestedOwner;
        TierParams _nestedParams = TierParams.Legacy;
        // Win.ShadowCost: ms per Stopwatch tick, for the camera ms RenderLevel times.
        static readonly double s_camTickMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        static void RaiseNested(Action<WindowState, Camera> handlers, WindowState s, Camera cam, string what)
        {
            if (handlers == null)
                return;
            try
            {
                handlers(s, cam);
            }
            catch (Exception e)
            {
                WindowLog.WarnEvery("PortalRenderer." + what, 10f, what + " handler failed: " + e);
            }
        }
        // The frame as BeginFrame saw the main camera.
        Matrix4x4 _frameVP;
        Vector3 _frameEye;
        Quaternion _frameRot = Quaternion.identity;
        bool _frameJumped;
        int _frameNo = -100;

        /// <summary>This frame's culling planes of the main camera (valid after BeginFrame returned true).</summary>
        internal Plane[] FramePlanes => _planes;
        /// <summary>This frame's non-jittered view-projection of the main camera (after BeginFrame).</summary>
        internal Matrix4x4 FrameVP => _frameVP;

        /// <summary>
        /// The head of a frame (what RenderAll does before choosing): last frame's views released, the window camera set
        /// up, the main camera's frame recorded. False when the frame is already done: no camera (everything taken down) or
        /// nothing that could show (only the game camera's passes taken down).
        /// </summary>
        internal bool BeginFrame(Camera main, IList<PortalWindow> windows)
        {
            _main = main;
            _windows = windows;
            RendersLastFrame = 0;
            // Last frame's views have been drawn by now.
            ReleaseTemps();
            WindowRTPool.Sweep();
            // Win.Virtual: the window screen's targets go while the switch is off.
            VirtualFrame();
            Shader.SetGlobalVector(s_viewport, FullViewport);
            // Win.PointDepth: the window shader's point fetches of the view depth (a global, set when the switch flips; never
            // set it is 0, the bilinear reads).
            if (WindowFlags.PointDepth != s_pointDepthOn)
            {
                s_pointDepthOn = WindowFlags.PointDepth;
                Shader.SetGlobalFloat(s_pointDepth, s_pointDepthOn ? 1f : 0f);
            }
            // Win.VanillaLook (Docs\PARITY-PLAN.md P2, P7): the window shader draws nothing where a window shows nothing of its
            // own (on the game camera and inside window renders), the game's empty ring; never set it is 0, the fallback
            // disc. A bundle from before it has no such global and keeps the disc.
            if (WindowFlags.VanillaLook != s_fallbackModeOn)
            {
                s_fallbackModeOn = WindowFlags.VanillaLook;
                Shader.SetGlobalFloat(s_fallbackMode, s_fallbackModeOn ? 1f : 0f);
            }
            LabVanillaLook = 0;
            Frusta.Clear();
            ViewPositions.Clear();
            for (int i = 0; i < windows.Count; i++)
                windows[i].RenderedThisFrame = false;
            if (main == null)
            {
                Unpatch();
                if (_motionOn != null)
                    _motionOn.RemoveCommandBuffer(EffectsPrepEvent, _motion);
                _motionOn = null;
                _hasPrev = false;
                if (_skyOn != null)
                {
                    _skyOn.RemoveCommandBuffer(EffectsPrepEvent, _sky);
                }
                _skyOn = null;
                RemoveBlurInputs();
                _hero = null;
                SkyDepthThisFrame = false;
                ViewDepthThisFrame = false;
                LargestWindow = null;
                return false;
            }
            // Nothing that could show: only take down what earlier frames set up on the game camera (no camera copy,
            // no motion vector copy).
            if (windows.Count == 0 && WindowFlags.LeanFixed && WindowFlags.LeanIdle)
            {
                UpdatePatch(main, windows);
                UpdateMotion(main, windows);
                UpdateSky(main, windows);
                RemoveBlurInputs();
                _hero = null;
                return false;
            }

            SetupCamera(main);
            Matrix4x4 view = main.worldToCameraMatrix;
            Matrix4x4 proj = Perspective(main, main.farClipPlane);
            _frameVP = proj * view;
            GeometryUtility.CalculateFrustumPlanes(_frameVP, _planes);
            _frameEye = main.transform.position;
            _frameRot = main.transform.rotation;
            // The camera was carried through a portal (or jumped): no earlier view lines up any more.
            _frameJumped = !_hasPrev || (_frameEye - _prevEye).sqrMagnitude > 4f;
            _frameNo = Time.frameCount;
            return true;
        }

        /// <summary>A window's per-frame preparation before its rect is measured: not stale until shown so, and its prism
        /// stretched where the near plane would cut it. False: not enabled or no enabled partner (nothing to show).</summary>
        internal bool PrepareWindow(PortalWindow w, Camera main)
        {
            w.Stale = false;
            if (!w.Enabled || w.Partner == null || !w.Partner.Enabled)
            {
                w.SetDepth(0f);
                return false;
            }
            UpdateDepth(w, main);
            return true;
        }

        /// <summary>The opening's screen rect (0..1, clipped) under `vp`; false when nothing of it is in front of the camera.</summary>
        internal bool ScreenRectOf(PortalWindow w, in Matrix4x4 vp, out Rect rect) => WindowRect(w, vp, out rect);

        /// <summary>The same, with the opening's whole extent on screen (0..1 units, not clipped; Win.DistantLive's floor).</summary>
        internal bool ScreenRectOf(PortalWindow w, in Matrix4x4 vp, out Rect rect, out Vector2 extent) => WindowRect(w, vp, out rect, out extent);

        /// <summary>The window texture size for `main` (the screen × Resolution), as SetupCamera makes it.</summary>
        internal void TexSize(Camera main, out int width, out int height)
        {
            width = Mathf.Max(16, Mathf.RoundToInt(main.pixelWidth * Settings.Resolution));
            height = Mathf.Max(16, Mathf.RoundToInt(main.pixelHeight * Settings.Resolution));
        }

        /// <summary>
        /// The render rect of an opening rect: its border for screen-space effects (Legacy: PortalRenderSettings.Margin with
        /// the Win.Margin rule, as post-S1; OfRect: `marginOfRect` of the rect's own size each side; None) and its size class.
        /// </summary>
        internal Rect RenderRect(PortalWindow w, Rect rect, WindowMargin margin, float marginOfRect, Camera main)
        {
            bool marginRule = WindowFlags.Margin, sizeClasses = WindowFlags.RTPool;
            if (margin == WindowMargin.Legacy && Settings.Margin > 0f)
            {
                float my = Settings.Margin, mx = my / main.aspect;
                // A border of 5% of the screen around a window a tenth of the screen high is most of what the
                // camera renders: at most a quarter of the rect's own size, but not less than the window camera's
                // SSAO reaches at the nearest depth seen through the window (the opening itself), or the AO along
                // its inner edge would sample past the rendered border and differ from the full margin's.
                if (marginRule)
                {
                    float reach = 0f;
                    if (Settings.EffectRadius > 0f)
                    {
                        // Screen height (viewport units) of one metre at one metre's depth.
                        float unitHeight = 0.5f / Mathf.Tan(main.fieldOfView * 0.5f * Mathf.Deg2Rad);
                        Transform t = main.transform;
                        float nearest = Vector3.Dot(w.Frame.Center - t.position, t.forward) - Mathf.Max(w.Frame.HalfSize.x, w.Frame.HalfSize.y);
                        reach = Settings.EffectRadius * unitHeight / Mathf.Max(nearest, main.nearClipPlane);
                    }
                    my = Mathf.Min(my, Mathf.Max(0.25f * rect.height, reach));
                    mx = Mathf.Min(mx, Mathf.Max(0.25f * rect.width, reach / main.aspect));
                }
                rect = PortalMath.Intersect(new Rect(rect.x - mx, rect.y - my, rect.width + 2f * mx, rect.height + 2f * my), FullRect);
            }
            else if (margin == WindowMargin.OfRect && marginOfRect > 0f)
            {
                float mx = marginOfRect * rect.width, my = marginOfRect * rect.height;
                rect = PortalMath.Intersect(new Rect(rect.x - mx, rect.y - my, rect.width + 2f * mx, rect.height + 2f * my), FullRect);
            }
            // Sizes in steps of 16 px (32 with the margin rule or the texture pool): effects that keep history per
            // camera (ambient occlusion) start over whenever the size changes, which a rect that grows and shrinks
            // by a pixel would do every frame, and every new size is a new set of render textures. With the pool
            // a rect at the screen's edge is moved onto the screen instead of cut, so it keeps its size class.
            int step = marginRule || sizeClasses ? 32 : 16;
            TexSize(main, out int texW, out int texH);
            return sizeClasses ? QuantizeOnScreen(rect, texW, texH, step) : PortalMath.Intersect(Quantize(rect, texW, texH, step), FullRect);
        }

        /// <summary>A level-0 render of `w` over `rect` (after BeginFrame), taken into the window's ownership and shown
        /// fresh; null when the rect snaps to no pixels.</summary>
        internal RenderTexture RenderShown(PortalWindow w, Rect rect)
        {
            // Win.Virtual (PortalRenderer.Virtual.cs): a picked window drawn without a camera; otherwise (and always with the
            // switch off) RenderLevel.
            RenderTexture tex = RenderTopLevel(w, rect, out Rect texRect);
            if (tex != null)
            {
                // These textures now belong to the window until its next render. Its earlier ones go back to the pool
                // (which clears its cache), but the depth-stats slot this render just got stays its own.
                WindowState s = w.State;
                int statsIndex = s != null ? s.Cache.StatsIndex : -1, statsFrame = s != null ? s.Cache.StatsFrame : -100;
                w.ReleaseOwned();
                if (s != null && statsFrame == _frameNo)
                {
                    s.Cache.StatsIndex = statsIndex;
                    s.Cache.StatsFrame = statsFrame;
                }
                Own(w, tex);
                Own(w, _lastDepth);
                Own(w, _lastNormal);
                w.Texture = tex;
                w.TextureRect = texRect;
                w.DepthTexture = _lastDepth;
                w.NormalTexture = _lastNormal;
                w.NormalRotation = Matrix4x4.Rotate(PortalMath.Rotation(PortalMath.Pair(w.Partner.Frame, w.Frame)));
                // Win.StaleTaa (Docs\RIPPLE-PLAN.md §4.6): the view-projection with this render's TAA jitter, which its
                // texture carries, so a flat stale display lays it onto the opening without that offset (up to 0.66 px,
                // another at every refresh) and the game camera's jitter of the frame it shows in is the only one. Only with
                // Clip = NearPlane: RenderLevel jitters level 0 there alone (an Oblique render carries none), and
                // Settings.Jitter is zero without TAA.
                w.ViewVP = WindowFlags.StaleTaa && Settings.Clip != PortalClip.Oblique ? Jittered(_frameVP, Settings.Jitter) : _frameVP;
                w.RenderedAtFrame = _frameNo;
                w.RenderedThisFrame = true;
                w.LastRendered = Time.time;
                if (w.DistantAtFrame == _frameNo)
                    LabDistantRenders++;
            }
            return tex;
        }

        // `vp` (a perspective projection, last row (0, 0, -1, 0), times a view) with the TAA jitter `jitter` (screen units)
        // put on the projection as TaaComponent puts it (m02/m12 += 2 x jitter): that added column times the view is, in
        // rows 0 and 1, -2 x jitter times vp's last row.
        static Matrix4x4 Jittered(Matrix4x4 vp, Vector2 jitter)
        {
            float jx = 2f * jitter.x, jy = 2f * jitter.y;
            vp.m00 -= jx * vp.m30;
            vp.m01 -= jx * vp.m31;
            vp.m02 -= jx * vp.m32;
            vp.m03 -= jx * vp.m33;
            vp.m10 -= jy * vp.m30;
            vp.m11 -= jy * vp.m31;
            vp.m12 -= jy * vp.m32;
            vp.m13 -= jy * vp.m33;
            return vp;
        }

        /// <summary>Win.Prewarm (the manager): RenderShown of `w`, a window out of the main camera's view, as if the camera at
        /// this frame's eye looked along `rot` (`vp`: that pose's non-jittered view-projection, `rect`: the window's render
        /// rect in it). Owned and cached as any render, with that pose as the view it was seen from (ViewVP, the cache's
        /// MainVP), so it reprojects onto wherever the window is once in view; nothing of it shows this frame.</summary>
        internal RenderTexture RenderAhead(PortalWindow w, Quaternion rot, in Matrix4x4 vp, Rect rect)
        {
            Quaternion frameRot = _frameRot;
            Matrix4x4 frameVP = _frameVP;
            _frameRot = rot;
            _frameVP = vp;
            RenderTexture tex;
            try
            {
                tex = RenderShown(w, rect);
            }
            finally
            {
                _frameRot = frameRot;
                _frameVP = frameVP;
            }
            // Not on screen: no composite pass, effects copy or fallback draw for it this frame.
            w.RenderedThisFrame = false;
            return tex;
        }

        /// <summary>`w` shows an earlier render this frame: laid onto the opening as it was seen then (flat), or bound to a
        /// resolve output already (not flat). Its grass stays for the frame it renders next (the game culls it by last
        /// frame's views).</summary>
        internal void ShowStale(PortalWindow w, bool flat)
        {
            w.Stale = flat;
            w.RenderedThisFrame = true;
            w.LastRendered = Time.time;
            if (w.HasView && !LabNoStaleViews)
            {
                Frusta.Add(w.ViewFrustum);
                ViewPositions.Add(w.ViewPos);
            }
        }

        /// <summary>The tail of a frame: (releaseUnseen) textures of windows out of sight for 30 frames back to the pool, what
        /// each window shows bound, and the game camera's window passes built.</summary>
        internal void EndFrame(Camera main, IList<PortalWindow> windows, bool releaseUnseen)
        {
            // Windows out of sight give their textures back.
            if (releaseUnseen)
                for (int i = 0; i < windows.Count; i++)
                    if (windows[i].Owned.Count > 0 && windows[i].ShownAtFrame < _frameNo - 30)
                        windows[i].ReleaseOwned();
            Pipeline?.BeforeComposite(main, windows);
            int shown = 0, vanilla = 0;
            bool look = WindowFlags.VanillaLook;
            for (int i = 0; i < windows.Count; i++)
            {
                PortalWindow w = windows[i];
                // Lab: a window drawn by the game camera in its view that shows nothing of its own (the vanilla look).
                if (!w.ShowOwn() && look && w.Renderer.enabled && w.Root.activeInHierarchy
                    && GeometryUtility.TestPlanesAABB(_planes, w.Renderer.bounds))
                    vanilla++;
                if (w.RenderedThisFrame)
                    shown++;
            }
            LabVanillaLook = vanilla;
            // Each shown window's own draw in the game camera's G-buffer (lab: WindowComposite.DrawsLastFrame).
            CountCompositeDraws(shown);
            Shader.SetGlobalVector(s_viewport, FullViewport);
            UpdatePatch(main, windows);
            UpdateMotion(main, windows);
            UpdateSky(main, windows);
            UpdateBlurInputs(main);
            OrderEffectsPrep(main);
        }

        // The cache's camera fields, right before Render() (Docs\S2S3-CONTRACTS.md §5): the camera exactly as it renders.
        void FillCache(WindowState s, PortalWindow w, Camera cam, in Matrix4x4 m, float viewScale, Vector3 camPos, Quaternion camRot,
                       Rect rect, RenderTexture target, in TierParams p)
        {
            int frame = Time.frameCount;
            WindowCache c = s.Cache;
            c.VP = cam.projectionMatrix * cam.worldToCameraMatrix;
            c.MainVP = _frameVP;
            c.Pair = m;
            c.PairBase = PortalMath.Pair(w.Frame, w.Partner.Frame);
            c.ViewScale = viewScale;
            c.Eye = camPos;
            c.Forward = camRot * Vector3.forward;
            c.Near = cam.nearClipPlane;
            c.Far = cam.farClipPlane;
            c.Rect = rect;
            c.Width = target.width;
            c.Height = target.height;
            c.Frame = frame;
            c.Time = Time.unscaledTime;
            c.RectPx = s.RectPx > 0 ? s.RectPx : Mathf.RoundToInt(rect.width * rect.height * _main.pixelWidth * _main.pixelHeight);
            c.Tier = p.Tier;
            c.Slot = s.Slot;
            c.ZNear = c.Near;
            c.ZFar = c.Far;
            c.StatsKnown = false;
            // No depth-stats slot this render (WindowVisibility not ready or full): never take an older render's result.
            if (c.StatsFrame != frame)
                c.StatsIndex = -1;
            c.Ao = p.Ao;
            c.PointLightShadows = p.PointLightShadows;
            c.ShadowDistanceCap = p.ShadowDistanceCap;
            c.ShadowCascadesCap = p.ShadowCascadesCap;
        }

        // The manager is gone with the renderer: nothing may point at it any more.
        partial void DestroyPlan()
        {
            Pipeline = null;
            Hook = null;
        }
    }

    // Render textures for the window views (WindowFlags.RTPool), kept between frames by size and format. With the
    // rects quantized to 32 px, a window that grows, shrinks or moves to the screen's edge finds a texture an earlier
    // size left instead of having one made (Unity's temporary pool frees what was not used for a few frames, and
    // windows change size all the time). A size class nobody asked for in IdleSeconds is freed (a large one, of
    // LargeBytes or more, after LargeIdleSeconds), and at most MaxFreeBytes wait unused: over that, the classes used
    // longest ago give theirs up first, and the class a texture comes back to keeps one whatever it costs (a window
    // larger than the budget must not make a new texture every frame). The game's SSAO textures for top-level window
    // renders come here too (WindowTextures), by name.
    public static class WindowRTPool
    {
        public struct Key : IEquatable<Key>
        {
            public int W, H, Depth, Aa;
            public RenderTextureFormat Format;
            public FilterMode Filter;
            public bool Mips;
            // Null for view textures.
            public string Name;

            public bool Equals(Key o) =>
                W == o.W && H == o.H && Depth == o.Depth && Aa == o.Aa && Format == o.Format && Filter == o.Filter && Mips == o.Mips
                && string.Equals(Name, o.Name);

            public override bool Equals(object o) => o is Key k && Equals(k);

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = W;
                    h = h * 397 ^ H;
                    h = h * 397 ^ Depth;
                    h = h * 397 ^ (int)Format;
                    h = h * 397 ^ Aa;
                    h = h * 397 ^ (int)Filter;
                    h = h * 397 ^ (Mips ? 1 : 0);
                    return Name != null ? h * 397 ^ Name.GetHashCode() : h;
                }
            }
        }

        struct Entry
        {
            public RenderTexture Rt;
            public long Bytes;
        }

        sealed class Free
        {
            public readonly List<Entry> List = new List<Entry>(2);
            public float UsedAt;
            public long Bytes;
        }

        public static float IdleSeconds = 10f, LargeIdleSeconds = 2f;
        public static long MaxFreeBytes = 128L << 20, LargeBytes = 16L << 20;
        // Diagnostics (Evicted: size classes freed to stay within MaxFreeBytes).
        public static int Made, Reused, Freed, Evicted;
        public static long FreeBytes => s_freeBytes;
        public static int ViewsOut => s_views.Count;

        static readonly Dictionary<Key, Free> s_free = new Dictionary<Key, Free>();
        // The view textures the pool made that are out (with the key they go back under).
        static readonly Dictionary<RenderTexture, Key> s_views = new Dictionary<RenderTexture, Key>();
        static readonly List<Key> s_drop = new List<Key>();
        static float s_sweepAt;
        static long s_freeBytes;
        // After Clear: what comes back is freed, until the pool is used again.
        static bool s_draining;

        // Realtime: also right in the editor's test scene, where the frame does not advance.
        static float Now => Time.realtimeSinceStartup;

        // A view texture: colour (with a 24-bit depth buffer) or one of the captures.
        public static RenderTexture Get(int width, int height, int depth, RenderTextureFormat format)
        {
            s_draining = false;
            var key = new Key { W = width, H = height, Depth = depth, Aa = 1, Format = format, Filter = FilterMode.Bilinear };
            RenderTexture rt = Take(key, false);
            if (rt == null)
            {
                rt = new RenderTexture(width, height, depth, format)
                {
                    name = "RealPortal view " + width + "x" + height,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                };
                Made++;
            }
            s_views[rt] = key;
            return rt;
        }

        // A texture from Get back into the pool; anything else is a temporary one and goes back to Unity's.
        public static void Return(RenderTexture rt)
        {
            if (rt == null)
                return;
            if (!s_views.TryGetValue(rt, out Key key))
            {
                RenderTexture.ReleaseTemporary(rt);
                return;
            }
            s_views.Remove(rt);
            if (!Keep(rt, key, 8))
                Destroy(rt);
        }

        // A free texture of this kind, or null. requireCreated: one whose contents the GPU lost is freed, not given.
        public static RenderTexture Take(Key key, bool requireCreated)
        {
            if (!s_free.TryGetValue(key, out Free f))
                s_free[key] = f = new Free();
            f.UsedAt = Now;
            while (f.List.Count > 0)
            {
                Entry e = f.List[f.List.Count - 1];
                f.List.RemoveAt(f.List.Count - 1);
                s_freeBytes -= e.Bytes;
                f.Bytes -= e.Bytes;
                if (e.Rt == null)
                    continue;
                if (requireCreated && !e.Rt.IsCreated())
                {
                    Destroy(e.Rt);
                    continue;
                }
                Reused++;
                return e.Rt;
            }
            return null;
        }

        // Puts `rt` into the pool under `key`; false (the caller frees it) when `max` of that kind already wait, or
        // one does and the free textures would exceed MaxFreeBytes even after the other classes, the ones used longest
        // ago first, gave theirs up.
        public static bool Keep(RenderTexture rt, Key key, int max)
        {
            if (rt == null || s_draining)
                return false;
            if (!s_free.TryGetValue(key, out Free f))
                s_free[key] = f = new Free();
            long bytes = Bytes(rt);
            if (f.List.Count >= max)
                return false;
            if (s_freeBytes + bytes > MaxFreeBytes)
            {
                MakeRoom(bytes, f);
                if (s_freeBytes + bytes > MaxFreeBytes && f.List.Count > 0)
                    return false;
            }
            f.List.Add(new Entry { Rt = rt, Bytes = bytes });
            f.UsedAt = Now;
            f.Bytes += bytes;
            s_freeBytes += bytes;
            return true;
        }

        // Frees whole size classes, the one used longest ago first (never `keep`), until `bytes` more fit.
        static void MakeRoom(long bytes, Free keep)
        {
            while (s_freeBytes + bytes > MaxFreeBytes)
            {
                Free oldest = null;
                foreach (Free f in s_free.Values)
                    if (f != keep && f.List.Count > 0 && (oldest == null || f.UsedAt < oldest.UsedAt))
                        oldest = f;
                if (oldest == null)
                    return;
                FreeAll(oldest);
                Evicted++;
            }
        }

        static void FreeAll(Free f)
        {
            for (int j = 0; j < f.List.Count; j++)
            {
                s_freeBytes -= f.List[j].Bytes;
                Destroy(f.List[j].Rt);
            }
            f.List.Clear();
            f.Bytes = 0;
        }

        // Once a second: size classes nobody asked for in IdleSeconds are freed.
        public static void Sweep()
        {
            float now = Now;
            if (now < s_sweepAt && now > s_sweepAt - 2f)
                return;
            s_sweepAt = now + 1f;
            s_drop.Clear();
            foreach (KeyValuePair<Key, Free> kv in s_free)
            {
                float idle = now - kv.Value.UsedAt;
                if (idle > IdleSeconds || (idle > LargeIdleSeconds && kv.Value.Bytes >= LargeBytes))
                    s_drop.Add(kv.Key);
            }
            for (int i = 0; i < s_drop.Count; i++)
            {
                FreeAll(s_free[s_drop[i]]);
                s_free.Remove(s_drop[i]);
            }
        }

        // Everything free goes; textures still out are freed when they come back.
        public static void Clear()
        {
            foreach (Free f in s_free.Values)
                for (int j = 0; j < f.List.Count; j++)
                    Destroy(f.List[j].Rt);
            s_free.Clear();
            s_freeBytes = 0;
            s_draining = true;
        }

        static void Destroy(RenderTexture rt)
        {
            if (rt == null)
                return;
            rt.Release();
            PortalMath.Kill(rt);
            Freed++;
        }

        // Also the window manager's cache budget (Win.CacheBudget).
        internal static long Bytes(RenderTexture rt)
        {
            int px;
            switch (rt.format)
            {
                case RenderTextureFormat.ARGBFloat:
                    px = 16;
                    break;
                case RenderTextureFormat.ARGBHalf:
                case RenderTextureFormat.DefaultHDR:
                case RenderTextureFormat.RGFloat:
                case RenderTextureFormat.ARGB64:
                    px = 8;
                    break;
                case RenderTextureFormat.RHalf:
                case RenderTextureFormat.R16:
                case RenderTextureFormat.RG16:
                    px = 2;
                    break;
                case RenderTextureFormat.R8:
                    px = 1;
                    break;
                default:
                    px = 4;
                    break;
            }
            if (rt.depth > 0)
                px += 4;
            return (long)rt.width * rt.height * px;
        }
    }
}
