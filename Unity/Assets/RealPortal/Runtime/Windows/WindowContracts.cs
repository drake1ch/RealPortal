using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace RealPortal
{
    // =====================================================================================================================
    // Window Pipeline v2, stages S2 and S3: the contracts.
    //
    // Docs\ARCHITECTURE-v2.md §4 is the design and Docs\S2S3-CONTRACTS.md the rules around it (owners, frame timeline,
    // shader and compute names, probe rules, lab gates). This file is the vocabulary the four implementers share:
    // enums, the per-window state, value types, settings, shader ids and one interface per component. It is frozen for
    // the stage: an owner who needs more adds it in his own files (every type here that an owner may extend is partial,
    // so a private field goes into a partial declaration in his file; an interface he implements may grow members the
    // same way, which nobody else may then depend on).
    //
    // Shared with the Unity test project: nothing here (or anywhere under Runtime) may reference Valheim, BepInEx or the
    // engine plugin. Everything runs on the main thread; nothing here is thread-safe.
    //
    // Inert by construction: every component reports Ready = false until its owner implements it, and a mechanism is
    // effective only when its WindowFlags switch is on AND the parts it needs are Ready (WindowMechanisms.Effective).
    // Not effective = exactly the post-S1 path. That is also how an old bundle (no WindowResolve.shader, no
    // WindowCount.compute, no new PortalWindow passes) switches the GPU mechanisms off: their parts are not Ready.
    // =====================================================================================================================

    /// <summary>
    /// Window tiers (ARCHITECTURE-v2 §2.2). The order of Live, NearCached and SmallCached is the order of cost (cheaper
    /// downward). Numeric values index <see cref="WindowPipelineSettings.Effects"/> and <see cref="WindowFrameStats.RendersByTier"/>.
    /// </summary>
    public enum WindowTier
    {
        /// <summary>W-A. Rendered every frame with full effects on a sticky slot camera (never shown stale).</summary>
        Live = 0,
        /// <summary>W-B. Depth-reprojected cache; re-rendered on error ≥ 1 px or its age cap.</summary>
        NearCached = 1,
        /// <summary>W-C. Half resolution cache; re-rendered on error ≥ 2 px or its age cap.</summary>
        SmallCached = 2,
        /// <summary>W-D. Reserved for S8 (panorama of the far area). Never assigned in S2/S3: where §2.2 says W-D, S3
        /// uses SmallCached while the legacy MinScreenFraction/MaxDistance test would have rendered the window, else Closed.</summary>
        Panorama = 3,
        /// <summary>W-E. The vanilla swirl (fallback): not CanShow, beyond MaxDistance, or below MinScreenFraction (with
        /// Win.DistantLive: under the pixel floor, PortalRenderSettings.MinPixels). With Win.VanillaLook the shader draws
        /// nothing there, the game's empty ring.</summary>
        Closed = 4,
        /// <summary>Not drawn: outside the main frustum, or 0 visible px in N consecutive readbacks. Cache kept for
        /// <see cref="WindowPipelineSettings.HiddenKeepCacheSeconds"/>.</summary>
        Hidden = 5,
    }

    /// <summary>What the main camera shows in a window this frame (decided by the manager, bound by PortalWindow.ShowOwn).</summary>
    public enum WindowShow : byte
    {
        /// <summary>Not drawn by the main camera this frame (outside its frustum or not enabled); nothing is bound.</summary>
        Off,
        /// <summary>The swirl (_Active = 0): Closed, or nothing to show yet.</summary>
        Fallback,
        /// <summary>This frame's render.</summary>
        Fresh,
        /// <summary>An older render, depth-reprojected by WindowResolve onto this frame's rect (Win.Resolve).</summary>
        Resolved,
        /// <summary>An older render laid flat onto the opening as it was seen then (_RP_Reproject, the post-S1 stale path).</summary>
        Reprojected,
    }

    /// <summary>Why a cached view may not be shown as it is (error = ∞, ARCHITECTURE-v2 §4.6 "Invalidators").</summary>
    [Flags]
    public enum WindowInvalid
    {
        None = 0,
        /// <summary>No cache (never rendered, released, or its textures lost).</summary>
        NoCache = 1 << 0,
        /// <summary>First frame it can show (became CanShow, entered the frustum, or appeared).</summary>
        FirstShow = 1 << 1,
        /// <summary>The far environment switched (plugin: AreaRenderState/FarEnvironment).</summary>
        EnvSwitch = 1 << 2,
        /// <summary>Lightning flash in the far environment (plugin).</summary>
        Lightning = 1 << 3,
        /// <summary>Objects or terrain changed in a sector the cache shows (plugin; on RemoteAreas: area load stage change).</summary>
        SpanChange = 1 << 4,
        /// <summary>A tier change that adds content (to Live, or AO/shadows on).</summary>
        TierContent = 1 << 5,
        /// <summary>The main camera jumped (carried through a portal, teleport): no cached view lines up.</summary>
        CameraJump = 1 << 6,
        /// <summary>The pair matrix changed since the render (a portal moved).</summary>
        PairMoved = 1 << 7,
        /// <summary>The rect grew by more than <see cref="WindowPipelineSettings.RegrowForce"/> since the render.</summary>
        Resized = 1 << 8,
        /// <summary>More than <see cref="WindowPipelineSettings.DisocclusionForce"/> of its visible pixels were disoccluded.</summary>
        Disoccluded = 1 << 9,
        /// <summary>Visible again after being Hidden by occlusion.</summary>
        Reappeared = 1 << 10,
        /// <summary>Forced by a lab hook.</summary>
        Lab = 1 << 15,
    }

    /// <summary>Why the scheduler chose to render a window this frame (lab statistics; first matching reason).</summary>
    public enum WindowChoice : byte
    {
        None,
        Live,
        Crossing,
        FirstShow,
        Invalid,
        HardCap,
        DynamicCap,
        Error,
        Guard,
        Reappeared,
        Legacy,
        Lab,
    }

    /// <summary>How a window's render rect gets its border for screen-space effects (§4.5 "Margin").</summary>
    public enum WindowMargin : byte
    {
        /// <summary>As post-S1: PortalRenderSettings.Margin with the Win.Margin rule (quarter rect, AO reach) and 32 px classes.</summary>
        Legacy,
        /// <summary><see cref="TierParams.MarginOfRect"/> × the rect's own size on each side.</summary>
        OfRect,
        /// <summary>No border (no AO in this render).</summary>
        None,
    }

    /// <summary>
    /// The S2/S3 mechanisms, each behind its WindowFlags switch. <see cref="WindowMechanisms.Effective"/> says whether one
    /// is in force this frame (switch on and every part it needs Ready).
    /// </summary>
    public enum WindowMechanism
    {
        CameraSlots,
        OwnFog,
        AoTierA,
        AoTemporal,
        GrassTargeted,
        Tiers,
        Budget,
        ErrorModel,
        Resolve,
        Occlusion,
        CompositeMRT,
        ShadowTiers,
        RestoreFromWindow,
        /// <summary>Round 2: Win.ShadowTiers' caps for SmallCached only (<see cref="WindowMechanisms.ShadowCaps"/>).</summary>
        ShadowTiersSmall,
    }

    /// <summary>
    /// The effect set of one window render (§4.5). Written by the scheduler (owner sched) into <see cref="WindowState.Params"/>
    /// before the render; applied by PortalRenderer.RenderLevel (owner slots: resolution, shadow distance and cascades,
    /// LOD bias) and by the plugin's BeforeSlotRender handlers (AO: AoSync, point-light shadows and weather particles:
    /// TierEffects). A field at its <see cref="Legacy"/> value means "as post-S1".
    /// </summary>
    public struct TierParams
    {
        /// <summary>The tier these parameters were made for.</summary>
        public WindowTier Tier;
        /// <summary>× PortalRenderSettings.Resolution (and × the GPU budget's dynamic factor for Live/NearCached). 1 = as today.</summary>
        public float ResolutionScale;
        public WindowMargin Margin;
        /// <summary>For <see cref="WindowMargin.OfRect"/>: fraction of the rect's width/height added on each side.</summary>
        public float MarginOfRect;
        /// <summary>Upper bound for QualitySettings.shadowDistance during the render, in metres; &lt; 0: no cap.</summary>
        public float ShadowDistanceCap;
        /// <summary>Upper bound for QualitySettings.shadowCascades during the render; &lt; 0: no cap.</summary>
        public int ShadowCascadesCap;
        /// <summary>Point/spot light shadows during the render (false: the plugin turns them off around it).</summary>
        public bool PointLightShadows;
        /// <summary>The window camera's SSAO (Amplify) runs in this render.</summary>
        public bool Ao;
        /// <summary>SSAO at half resolution without its filter (NearCached with AO).</summary>
        public bool AoHalfRes;
        /// <summary>SSAO with its temporal filter (Win.AoTemporal, sticky Live slots only; needs motion vectors on that slot).</summary>
        public bool AoTemporal;
        /// <summary>× the LOD bias the renderer computes (rect height, view scale). 1 = as today.</summary>
        public float LodBiasScale;
        /// <summary>Far weather particles in this render (false: only those of the area already running, §4.5 W-C).</summary>
        public bool WeatherParticles;
        /// <summary>The window keeps its slot camera across frames (AO history, previous VP); the pool does not give it away.</summary>
        public bool Sticky;

        /// <summary>The post-S1 render: no tier effect at all. What RenderLevel uses when a window has no state or no plan.</summary>
        public static readonly TierParams Legacy = new TierParams
        {
            Tier = WindowTier.Live,
            ResolutionScale = 1f,
            Margin = WindowMargin.Legacy,
            MarginOfRect = 0f,
            ShadowDistanceCap = -1f,
            ShadowCascadesCap = -1,
            PointLightShadows = true,
            Ao = true,
            AoHalfRes = false,
            AoTemporal = false,
            LodBiasScale = 1f,
            WeatherParticles = true,
            Sticky = false,
        };
    }

    /// <summary>Refresh rules of a cached tier (§4.6 "Caps").</summary>
    public struct TierCaps
    {
        /// <summary>Re-render when the error model's E (px at the main screen) reaches this.</summary>
        public float ThresholdPx;
        /// <summary>Hard cap: the view is past it when it is at least AgeCapFrames frames AND AgeCapMs milliseconds old
        /// (both; at 60 fps they coincide, above 60 fps the ms cap binds, below it the frame cap). Past the hard cap the
        /// window is mandatory (at most <see cref="WindowPipelineSettings.MaxOverBudgetRenders"/> such renders over budget per frame).</summary>
        public int AgeCapFrames;
        public float AgeCapMs;
        /// <summary>Hard cap in frames while the cache shows dynamic content (LifeInView &gt; 0 or WeatherActive).</summary>
        public int DynamicCapFrames;
        /// <summary>With Win.ErrorModel off: re-render every this many frames (the post-S1 cadence: 2 for W-B, 3 for W-C).</summary>
        public int LegacyEvery;
    }

    /// <summary>
    /// One full render of a window: its textures and the camera it was seen with. Lives in <see cref="WindowState.Cache"/>.
    /// The textures are owned by the PortalWindow (they are in PortalWindow.Owned, given back by PortalWindow.ReleaseOwned,
    /// which clears this through <see cref="WindowState.OnCacheReleased"/>). Writers: RenderLevel (owner slots) fills the
    /// camera fields at every level-0 render of a window that has a state; the frame loop (owner sched) sets the three
    /// texture references when it takes ownership (after ReleaseOwned); WindowVisibility.Collect (owner gpu) fills
    /// ZNear/ZFar/WaterFrac/StatsKnown two frames later.
    /// </summary>
    public sealed partial class WindowCache
    {
        /// <summary>Colour as rendered (HDR when the game is), covering <see cref="Rect"/>.</summary>
        public RenderTexture Color;
        /// <summary>Linear eye depth of the window camera (far-side metres along <see cref="Forward"/>), RFloat today (R16F allowed).</summary>
        public RenderTexture Depth;
        /// <summary>GBuffer2 normals of the view (far side frame), or null (VirtualDepth off, forward window camera).</summary>
        public RenderTexture Normal;
        /// <summary>Screen rect (0..1 of the main camera) the textures cover, snapped to screen pixels.</summary>
        public Rect Rect;
        /// <summary>Texture size in pixels.</summary>
        public int Width, Height;
        /// <summary>Far-side world → cache clip space as rendered (lens shift and TAA jitter included):
        /// slotCam.projectionMatrix × slotCam.worldToCameraMatrix taken right before Render(). OpenGL convention (not
        /// GL.GetGPUProjectionMatrix): uv = clip.xy / clip.w × 0.5 + 0.5 addresses the cache textures directly.</summary>
        public Matrix4x4 VP;
        /// <summary>The main camera's non-jittered view-projection at the render (flat reprojection, = PortalWindow.ViewVP;
        /// under Win.StaleTaa ViewVP also has that render's TAA jitter).</summary>
        public Matrix4x4 MainVP;
        /// <summary>This side → far side at the render (PortalMath.View for the eye then; Pair when the view is not scaled).</summary>
        public Matrix4x4 Pair;
        /// <summary>The view scale then (PortalWindow.DepthScale = 1 / ViewScale).</summary>
        public float ViewScale = 1f;
        /// <summary>Window camera position and unit forward at the render (far side).</summary>
        public Vector3 Eye, Forward;
        /// <summary>Window camera near/far clip at the render.</summary>
        public float Near, Far;
        /// <summary>Nearest and farthest non-sky depth seen (far-side metres) from the depth-stats readback; Near and Far
        /// until <see cref="StatsKnown"/> (the conservative bound for E_par).</summary>
        public float ZNear, ZFar;
        /// <summary>Fraction of the view's pixels seen through water (stats readback; 0 until known).</summary>
        public float WaterFrac;
        public bool StatsKnown;
        /// <summary>Slot in this frame's depth-stats buffer (-1: none requested) and the frame it was requested in.</summary>
        public int StatsIndex = -1, StatsFrame = -100;
        /// <summary>Time.frameCount and Time.unscaledTime of the render.</summary>
        public int Frame = -100;
        public float Time = -100f;
        /// <summary>Rect area in main-screen pixels at the render (E_res).</summary>
        public int RectPx;
        /// <summary>Tier and slot it was rendered with (slot -1: the legacy shared camera).</summary>
        public WindowTier Tier;
        public int Slot = -1;

        /// <summary>Colour and depth are present and created (a texture whose contents the GPU lost is not valid).</summary>
        public bool Valid => Color != null && Depth != null && Color.IsCreated() && Depth.IsCreated();

        /// <summary>Drops the texture references (the owner gave them back) and marks the stats unknown.</summary>
        public void ClearTextures()
        {
            Color = Depth = Normal = null;
            StatsKnown = false;
            StatsIndex = -1;
        }
    }

    /// <summary>
    /// Everything the pipeline keeps per PortalWindow (one each, made on first use by <see cref="For"/> and kept on
    /// PortalWindow.State). Plain fields, main thread only. The comment on each group names its writer; everybody else
    /// only reads. Docs\S2S3-CONTRACTS.md has the full writer/reader table.
    /// </summary>
    public sealed partial class WindowState
    {
        public readonly PortalWindow Window;

        public WindowState(PortalWindow window)
        {
            Window = window;
        }

        /// <summary>The window's state, made on first use. Never null for a non-null window.</summary>
        public static WindowState For(PortalWindow w) => w.State ?? (w.State = new WindowState(w));

        // ---- Tier (writer: scheduler, or the legacy loop's derived tier when Win.Tiers is not effective) -----------------
        /// <summary>Current tier after hysteresis and dwell.</summary>
        public WindowTier Tier = WindowTier.Hidden;
        /// <summary>The tier the raw rules give this frame (before hysteresis/dwell).</summary>
        public WindowTier WantTier = WindowTier.Hidden;
        /// <summary>Tier before the last change, and Time.unscaledTime of that change (dwell).</summary>
        public WindowTier PrevTier = WindowTier.Hidden;
        public float TierSince = -100f;
        /// <summary>Effect set of this frame's render (valid when <see cref="ParamsFrame"/> is this frame; else use TierParams.Legacy).</summary>
        public TierParams Params = TierParams.Legacy;
        public int ParamsFrame = -100;

        /// <summary>The params for a render this frame: <see cref="Params"/> when set this frame, else TierParams.Legacy.</summary>
        public TierParams ParamsNow(int frame) => ParamsFrame == frame ? Params : TierParams.Legacy;

        // ---- Slot (writer: WindowCameraPool.Acquire / Release) ---------------------------------------------------------
        /// <summary>Slot camera index of this frame's render (-1: none, or the legacy shared camera).</summary>
        public int Slot = -1;
        /// <summary>The slot it had last (handed back to it while free; protected while Params.Sticky).</summary>
        public int StickySlot = -1;

        // ---- Geometry this frame (writer: manager's Plan, or the legacy loop) -----------------------------------------
        /// <summary>Render rect (0..1, margin and size class applied).</summary>
        public Rect Rect;
        /// <summary>The opening's own projected rect (0..1, no margin), clipped to the screen.</summary>
        public Rect OpeningRect;
        /// <summary>The opening's whole projected width and height (0..1 units, not clipped; the manager's Plan only):
        /// Win.DistantLive's pixel floor.</summary>
        public Vector2 OpeningSize;
        /// <summary>Screen share used for tiering: VisiblePx / screen px when a fresh readback exists, else OpeningRect's area.</summary>
        public float Share;
        /// <summary>Distance from the main camera's eye to the opening's centre (m).</summary>
        public float Distance;
        public bool InFrustum, InRange, CanShow, Crossing;
        /// <summary>Render group (far environment / AreaContext) for ordering; PortalRenderSettings.GroupOf.</summary>
        public int Group;
        /// <summary>OpeningRect area in main-screen pixels.</summary>
        public int RectPx;

        // ---- Cache (writers: see WindowCache) ----------------------------------------------------------------------------
        public readonly WindowCache Cache = new WindowCache();
        // The names of ARCHITECTURE-v2 §4.1, read-only views of Cache.
        public RenderTexture CacheColor => Cache.Color;
        public RenderTexture CacheDepth => Cache.Depth;
        public Matrix4x4 CacheVP => Cache.VP;
        public Vector3 CacheEye => Cache.Eye;
        public Vector3 CacheFwd => Cache.Forward;
        public float ZNear => Cache.ZNear;
        public float ZFar => Cache.ZFar;
        public int RenderedFrame => Cache.Frame;
        public float RenderedTime => Cache.Time;

        // ---- Readback (writer: WindowVisibility) -------------------------------------------------------------------------
        /// <summary>Visibility id 1..255 (0: none assigned; then frustum + rect only). Written to the material as _RP_VisId.</summary>
        public int VisId;
        /// <summary>Visible pixels of the window in the main camera at <see cref="VisibleFrame"/> (-1: unknown).</summary>
        public int VisiblePx = -1;
        public int VisibleFrame = -100;
        /// <summary>Consecutive readbacks with 0 visible pixels.</summary>
        public int ZeroReadbacks;
        /// <summary>Visible pixels that the resolve marked disoccluded at <see cref="VisibleFrame"/>.</summary>
        public int DisoccludedPx;

        // ---- Cost (writer: WindowCostModel) ------------------------------------------------------------------------------
        /// <summary>EMA of the main-thread ms of this window's renders at its current tier (submit + Render + capture).</summary>
        public float CpuEma;
        /// <summary>Estimated GPU ms of a render at its current tier and size.</summary>
        public float GpuEst;

        // ---- Error (writer: scheduler) -----------------------------------------------------------------------------------
        /// <summary>E = E_par + E_time + E_res in main-screen px (∞ while <see cref="Invalid"/> is not None).</summary>
        public float ErrorPx;
        public float EPar, ETime, ERes;
        /// <summary>E at the last render decision (lab).</summary>
        public float LastErrorPx;
        /// <summary>Reasons the cache may not be shown as is; cleared by a render (writer: manager; plugin through
        /// IWindowManager.Invalidate).</summary>
        public WindowInvalid Invalid = WindowInvalid.NoCache | WindowInvalid.FirstShow;

        // ---- Decision this frame (writer: scheduler / manager) ----------------------------------------------------------
        public bool Chosen;
        public WindowChoice Reason;
        /// <summary>p = sqrt(visiblePx) · E / costEstimate.</summary>
        public float Priority;
        public WindowShow Show = WindowShow.Off;
        /// <summary>Frame of the last Plan that saw this window.</summary>
        public int PlannedFrame = -100;

        // ---- Resolve (writer: WindowResolve; textures it owns for one frame) ------------------------------------------
        public RenderTexture ResolvedColor, ResolvedDepth, ResolvedNormal;
        /// <summary>Screen rect (0..1) the resolved textures cover (this frame's Rect, snapped).</summary>
        public Rect ResolvedRect;
        public int ResolvedFrame = -100;

        // ---- Occlusion bookkeeping (writer: manager) ---------------------------------------------------------------------
        /// <summary>Time.unscaledTime it became Hidden by occlusion (-1: not hidden) and of its last guard render.</summary>
        public float HiddenSince = -1f;
        public float LastGuard = -100f;

        // ---- Far-side facts (writer: manager, from IWindowWorld) --------------------------------------------------------
        /// <summary>Moving creatures/effects of the far side inside the cache frustum (E_time).</summary>
        public int LifeInView;
        /// <summary>The far environment has precipitation (E_time, dynamic cap).</summary>
        public bool WeatherActive;

        // ---- Lab (writer: IWindowManager lab hooks) ---------------------------------------------------------------------
        /// <summary>Forced tier (null: off).</summary>
        public WindowTier? LabTier;
        /// <summary>Forced render cadence (0: off; 1: every frame; k+1: one render then k frames stale) and its start frame.</summary>
        public int LabEvery;
        public int LabFrom;

        /// <summary>Age of the cache in frames and in ms at (frame, now); int.MaxValue / +∞ without a cache.</summary>
        public int AgeFrames(int frame) => Cache.Valid ? frame - Cache.Frame : int.MaxValue;
        public float AgeMs(float now) => Cache.Valid ? (now - Cache.Time) * 1000f : float.PositiveInfinity;

        /// <summary>Called by PortalWindow.ReleaseOwned: the cache textures went back to the pool.</summary>
        public void OnCacheReleased()
        {
            Cache.ClearTextures();
            Invalid |= WindowInvalid.NoCache;
        }

        /// <summary>Called when the window goes away (manager): drops what refers to it.</summary>
        public void OnWindowGone()
        {
            Cache.ClearTextures();
            ResolvedColor = ResolvedDepth = ResolvedNormal = null;
            Slot = StickySlot = -1;
            VisId = 0;
        }
    }

    /// <summary>The CPU and GPU budget of one frame (§4.6 "Budgets").</summary>
    public struct WindowBudget
    {
        /// <summary>B_cpu: main-thread ms for window renders this frame (+∞ without Win.Budget).</summary>
        public float CpuMs;
        /// <summary>B_gpu: estimated GPU ms for window renders this frame (+∞ when unknown or without Win.Budget).</summary>
        public float GpuMs;
        /// <summary>Dynamic resolution factor for Live/NearCached (GpuBudget, 0.6..1).</summary>
        public float ResolutionFactor;
        /// <summary>Count cap (PortalRenderSettings.MaxRendersPerFrame override; 0: none).</summary>
        public int MaxRenders;
        /// <summary>Renders past their hard caps allowed over budget (1).</summary>
        public int OverBudget;
        /// <summary>GPU-bound: NearCached windows are treated as SmallCached (§4.6).</summary>
        public bool DemoteNear;
    }

    /// <summary>The main camera as Plan and RenderChosen see it this frame.</summary>
    public struct WindowFrameContext
    {
        public int Frame;
        /// <summary>Time.unscaledTime and Time.unscaledDeltaTime (ages and EMAs use unscaled time).</summary>
        public float Now, DeltaTime;
        public Camera Main;
        public Vector3 Eye, Forward;
        public Quaternion Rotation;
        /// <summary>main.worldToCameraMatrix and the non-jittered view-projection the renderer uses
        /// (Matrix4x4.Perspective(fov, aspect, near, far) × view, as PortalRenderer.RenderAll).</summary>
        public Matrix4x4 View, VP;
        /// <summary>PortalRenderSettings.Jitter (screen units); zero at Plan (TAA jitters at onPreCull).</summary>
        public Vector2 Jitter;
        public int ScreenW, ScreenH;
        /// <summary>f_px = ScreenH / (2 tan(fov/2)).</summary>
        public float FocalPx;
        /// <summary>The camera moved more than 2 m since last frame (carried through a portal, teleport).</summary>
        public bool Jumped;
        public WindowBudget Budget;

        /// <summary>Fills the camera part from `main` (Budget and Jumped are the caller's).</summary>
        public static WindowFrameContext From(Camera main, Vector2 jitter)
        {
            var c = new WindowFrameContext
            {
                Frame = UnityEngine.Time.frameCount,
                Now = UnityEngine.Time.unscaledTime,
                DeltaTime = UnityEngine.Time.unscaledDeltaTime,
                Main = main,
                Jitter = jitter,
            };
            if (main == null)
                return c;
            Transform t = main.transform;
            c.Eye = t.position;
            c.Forward = t.forward;
            c.Rotation = t.rotation;
            c.View = main.worldToCameraMatrix;
            c.VP = Matrix4x4.Perspective(main.fieldOfView, main.aspect, main.nearClipPlane, main.farClipPlane) * c.View;
            c.ScreenW = main.pixelWidth;
            c.ScreenH = main.pixelHeight;
            c.FocalPx = c.ScreenH / (2f * Mathf.Tan(main.fieldOfView * 0.5f * Mathf.Deg2Rad));
            return c;
        }
    }

    /// <summary>
    /// Settings of the pipeline with the defaults of ARCHITECTURE-v2 §2.2, §4.5, §4.6 and Appendix B. The plugin binds the
    /// [Window] config keys onto these (owner sched); the Unity test project uses the defaults. Read every frame, so a
    /// change takes effect on the next Plan.
    /// </summary>
    public sealed partial class WindowPipelineSettings
    {
        // Selection (§2.2). Share = visible px share (or the opening rect's area).
        public float TierA_Share = 0.05f, TierB_Share = 0.01f, TierC_Share = 0.0015f;
        /// <summary>Closer than this (m) a window is Live.</summary>
        public float LiveDistance = 8f;
        /// <summary>At most this many Live windows (crossing first, then nearest, then largest).</summary>
        public int LiveMax = 2;
        /// <summary>From this distance (m) a window is at best SmallCached ("or 30–60 m").</summary>
        public float SmallFromDistance = 30f;
        /// <summary>±20 % on the share thresholds and 0.5 s between changes (promotions to Live by distance/crossing are immediate).</summary>
        public float Hysteresis = 0.2f, DwellSeconds = 0.5f;
        /// <summary>NearCached gets half-res AO only from this share (and only with GPU headroom).</summary>
        public float AoNearMinShare = 0.02f;

        // Caps (§4.6).
        public TierCaps NearCaps = new TierCaps { ThresholdPx = 1f, AgeCapFrames = 3, AgeCapMs = 50f, DynamicCapFrames = 2, LegacyEvery = 2 };
        public TierCaps SmallCaps = new TierCaps { ThresholdPx = 2f, AgeCapFrames = 8, AgeCapMs = 133f, DynamicCapFrames = 4, LegacyEvery = 3 };
        /// <summary>Renders past their hard caps allowed over budget per frame.</summary>
        public int MaxOverBudgetRenders = 1;

        // Budgets (§4.6). 0 = automatic.
        public float CpuBudgetMs = 0f, CpuMaxMs = 4.5f, CpuMinMs = 1.5f, CpuMarginMs = 0.5f;
        public float GpuBudgetMs = 0f;
        /// <summary>Fallback GPU controller: mean wait for the last presentation above this (ms) = GPU-bound.</summary>
        public float GpuBoundWaitMs = 1f;
        /// <summary>B_gpu shrinks by this fraction per second while GPU-bound; dynamic resolution steps ×DynResStep down to DynResFloor.</summary>
        public float GpuDecayPerSecond = 0.15f, DynResStep = 0.85f, DynResFloor = 0.6f;
        /// <summary>Count cap over the ms budget (config MaxRendersPerFrame; 0: none).</summary>
        public int MaxRendersPerFrame = 0;
        /// <summary>EMA time constant (s) of the frame-minus-windows time in B_cpu.</summary>
        public float FrameEmaSeconds = 0.5f;

        // Resolve (§4.7).
        public int ResolveSteps = 12, ResolveRefine = 3;
        /// <summary>Relative depth tolerance of a ray crossing in the march.</summary>
        public float ResolveThickness = 0.02f;
        /// <summary>Disoccluded share of the visible pixels that forces a render.</summary>
        public float DisocclusionForce = 0.15f;

        // Occlusion (§4.8).
        public int HiddenAfterZeroReadbacks = 2;
        public float GuardEverySeconds = 2f, HiddenKeepCacheSeconds = 10f;
        /// <summary>A rect that grew by more than this fraction renders regardless of the readback.</summary>
        public float RegrowForce = 0.3f;
        /// <summary>A readback older than this many frames is not used for Share or Hidden.</summary>
        public int ReadbackMaxAge = 4;

        // Slot cameras (§4.3).
        /// <summary>Most slot cameras (made on demand; the legacy shared camera takes any overflow).</summary>
        public int SlotCapacity = 16;

        // Re-plan at onPreCull (§4.2): the pose moved more than this since Plan.
        public float ReplanMeters = 0.01f, ReplanDegrees = 0.1f;

        /// <summary>
        /// Effect sets by tier (§4.5), indexed by (int)WindowTier. The scheduler copies one, then applies the switches
        /// (Win.ShadowTiers off: no shadow caps, point lights as today, except SmallCached's under Win.ShadowTiersSmall;
        /// Win.AoTierA off: AO as today; Win.Tiers off: resolution and margin as today) and the frame's dynamic resolution
        /// factor.
        /// </summary>
        public TierParams[] Effects =
        {
            // Live
            new TierParams
            {
                Tier = WindowTier.Live, ResolutionScale = 1f, Margin = WindowMargin.Legacy, ShadowDistanceCap = -1f, ShadowCascadesCap = -1,
                PointLightShadows = true, Ao = true, LodBiasScale = 1f, WeatherParticles = true, Sticky = true,
            },
            // NearCached (AO decided per window: share >= AoNearMinShare and GPU headroom; Sticky follows Ao; a render with
            // AO gets the Legacy margin, the 4 % below is for renders without it)
            new TierParams
            {
                Tier = WindowTier.NearCached, ResolutionScale = 0.75f, Margin = WindowMargin.OfRect, MarginOfRect = 0.04f, ShadowDistanceCap = 80f,
                ShadowCascadesCap = 2, PointLightShadows = false, Ao = false, AoHalfRes = true, LodBiasScale = 1f, WeatherParticles = true, Sticky = false,
            },
            // SmallCached
            new TierParams
            {
                Tier = WindowTier.SmallCached, ResolutionScale = 0.5f, Margin = WindowMargin.None, ShadowDistanceCap = 40f, ShadowCascadesCap = 1,
                PointLightShadows = false, Ao = false, LodBiasScale = 0.75f, WeatherParticles = false, Sticky = false,
            },
            // Panorama (reserved), Closed, Hidden: never rendered with these
            TierParams.Legacy,
            TierParams.Legacy,
            TierParams.Legacy,
        };

        public TierCaps CapsOf(WindowTier tier) => tier == WindowTier.SmallCached ? SmallCaps : NearCaps;
    }

    /// <summary>Per-frame counters for the lab (writer: manager; reset at the start of each RenderChosen).</summary>
    public sealed partial class WindowFrameStats
    {
        public int Frame;
        public int Windows, InFrustum, Shown;
        /// <summary>Renders this frame by tier (index (int)WindowTier).</summary>
        public readonly int[] RendersByTier = new int[6];
        public int Renders, Mandatory, OverBudget, Guard, HardCap, DynamicCap, Disoccluded, Reappeared, Replans;
        /// <summary>Shown this frame by kind.</summary>
        public int Fresh, Resolved, Reprojected, Fallback, Hidden, Closed;
        /// <summary>Live windows not shown Fresh this frame (S3 gate "W-A never stale": must stay 0).</summary>
        public int LiveStale;
        /// <summary>Slot cameras used, renders that got their sticky slot back, renders on the legacy shared camera.</summary>
        public int SlotsUsed, StickyHits, LegacyCameraRenders;
        public float PlanMs, WindowCpuMs, CpuBudgetMs, GpuBudgetMs, ResolutionFactor, GpuWaitMs;
        /// <summary>Frames between the readback used this frame and its request.</summary>
        public int ReadbackLatency;
        public int TierChanges;
        public WindowInvalid InvalidSeen;

        public void Reset(int frame)
        {
            Frame = frame;
            Windows = InFrustum = Shown = 0;
            Array.Clear(RendersByTier, 0, RendersByTier.Length);
            Renders = Mandatory = OverBudget = Guard = HardCap = DynamicCap = Disoccluded = Reappeared = Replans = 0;
            Fresh = Resolved = Reprojected = Fallback = Hidden = Closed = 0;
            LiveStale = 0;
            SlotsUsed = StickyHits = LegacyCameraRenders = 0;
            PlanMs = WindowCpuMs = 0f;
            ReadbackLatency = 0;
            TierChanges = 0;
            InvalidSeen = WindowInvalid.None;
        }
    }

    /// <summary>
    /// GPU assets from realportal.bundle (owner gpu loads them in the plugin, WindowAssetLoader; the Unity test project
    /// from the AssetDatabase). A null field = an old bundle: the parts that need it report Ready = false.
    /// </summary>
    public sealed class WindowAssets
    {
        /// <summary>Asset names in the bundle (RealPortalBuild adds the files; LoadAsset by these names).</summary>
        public const string WindowAsset = "PortalWindow", ResolveAsset = "WindowResolve", CountAsset = "WindowCount";

        /// <summary>RealPortal/Window (PortalWindow.shader), the shader of every window material.</summary>
        public Shader Window;
        /// <summary>Hidden/RealPortal/WindowResolve.</summary>
        public Shader Resolve;
        /// <summary>WindowCount.compute (kernels CSClear, CSCount, CSStatsClear, CSDepthStats).</summary>
        public ComputeShader Count;
    }

    /// <summary>Shader property names of the contract (Docs\S2S3-CONTRACTS.md §8), as ids. Existing names are listed too
    /// so all owners use one table.</summary>
    public static class WindowShaderIds
    {
        // PortalWindow.shader, per material (existing).
        public static readonly int MainTex = Shader.PropertyToID("_MainTex");
        public static readonly int Active = Shader.PropertyToID("_Active");
        public static readonly int TexRect = Shader.PropertyToID("_RP_TexRect");
        public static readonly int ViewDepth = Shader.PropertyToID("_RP_ViewDepth");
        public static readonly int DepthScale = Shader.PropertyToID("_RP_DepthScale");
        public static readonly int ViewNormal = Shader.PropertyToID("_RP_ViewNormal");
        public static readonly int HasDepth = Shader.PropertyToID("_RP_HasDepth");
        public static readonly int HasNormal = Shader.PropertyToID("_RP_HasNormal");
        public static readonly int NormalRot = Shader.PropertyToID("_RP_NormalRot");
        public static readonly int Reproject = Shader.PropertyToID("_RP_Reproject");
        public static readonly int ReprojVP = Shader.PropertyToID("_RP_ReprojVP");
        public static readonly int WindowId = Shader.PropertyToID("_RP_WindowId");
        // PortalWindow.shader, per material (new, S3).
        /// <summary>float 0..255: the window's visibility id (WindowState.VisId); 0 = not counted.</summary>
        public static readonly int VisId = Shader.PropertyToID("_RP_VisId");
        /// <summary>float 0/1: _MainTex/_RP_ViewDepth are a resolve output; _MainTex.a = 0 marks disoccluded pixels.</summary>
        public static readonly int Resolved = Shader.PropertyToID("_RP_Resolved");
        // PortalWindow.shader, globals (existing).
        public static readonly int Viewport = Shader.PropertyToID("_RP_Viewport");
        public static readonly int InWindowRender = Shader.PropertyToID("_RP_InWindowRender");
        public static readonly int RenderingWindowId = Shader.PropertyToID("_RP_RenderingWindowId");
        public static readonly int PatchNear = Shader.PropertyToID("_RP_PatchNear");
        // PortalWindow.shader, globals (new, S3).
        /// <summary>float: the fog-copy target's mode in AUXMRT (1 = window right at the camera, 2 = window as sky), as _RP_PatchNear for DEPTHPATCH.</summary>
        public static readonly int FogPatchMode = Shader.PropertyToID("_RP_FogPatchMode");

        // WindowResolve.shader.
        public static readonly int CacheColor = Shader.PropertyToID("_RP_CacheColor");
        public static readonly int CacheDepth = Shader.PropertyToID("_RP_CacheDepth");
        public static readonly int CacheNormal = Shader.PropertyToID("_RP_CacheNormal");
        public static readonly int HasCacheNormal = Shader.PropertyToID("_RP_HasCacheNormal");
        public static readonly int CacheVP = Shader.PropertyToID("_RP_CacheVP");
        public static readonly int CacheEye = Shader.PropertyToID("_RP_CacheEye");
        public static readonly int CacheFwd = Shader.PropertyToID("_RP_CacheFwd");
        public static readonly int CacheZ = Shader.PropertyToID("_RP_CacheZ");
        public static readonly int PairMatrix = Shader.PropertyToID("_RP_PairMatrix");
        public static readonly int MainCamToWorld = Shader.PropertyToID("_RP_MainCamToWorld");
        public static readonly int MainProj = Shader.PropertyToID("_RP_MainProj");
        public static readonly int ResolveRect = Shader.PropertyToID("_RP_ResolveRect");
        public static readonly int ResolveEye = Shader.PropertyToID("_RP_ResolveEye");
        public static readonly int ResolveFwd = Shader.PropertyToID("_RP_ResolveFwd");
        public static readonly int ExitPlane = Shader.PropertyToID("_RP_ExitPlane");
        public static readonly int ResolveParams = Shader.PropertyToID("_RP_ResolveParams");

        // WindowCount.compute.
        public static readonly int IdTex = Shader.PropertyToID("_RP_IdTex");
        public static readonly int IdSize = Shader.PropertyToID("_RP_IdSize");
        public static readonly int Counts = Shader.PropertyToID("_RP_Counts");
        public static readonly int StatsDepth = Shader.PropertyToID("_RP_StatsDepth");
        public static readonly int StatsSize = Shader.PropertyToID("_RP_StatsSize");
        public static readonly int StatsParams = Shader.PropertyToID("_RP_StatsParams");
        public static readonly int StatsCamToWorld = Shader.PropertyToID("_RP_StatsCamToWorld");
        public static readonly int StatsProj = Shader.PropertyToID("_RP_StatsProj");
        public static readonly int Stats = Shader.PropertyToID("_RP_Stats");
    }

    /// <summary>Pass indices and names (PortalWindow.shader passes are append-only: never reorder), kernel names and
    /// buffer sizes of the GPU contract.</summary>
    public static class WindowPasses
    {
        // PortalWindow.shader (existing).
        public const int Forward = 0, Deferred = 1, DepthPatch = 2, LinearDepth = 3, CopyDepth = 4, Motion = 5, MotionScreen = 6,
                         SkyDepth = 7, Mask = 8, Mix = 9, Solid = 10, Restore = 11;
        // PortalWindow.shader (new, S3). Presence is checked by name (Material.FindPass), so an old bundle reads -1.
        public const int AuxBase = 12, AuxMrt = 13, WindowIdPass = 14, RestoreView = 15;
        public const string AuxBaseName = "AUXBASE", AuxMrtName = "AUXMRT", WindowIdName = "WINDOWID", RestoreViewName = "RESTOREVIEW";
        /// <summary>AUXMRT targets in order: fog-copy depth, view depth, sky depth, window mask, id.</summary>
        public const int AuxTargets = 5;

        // WindowResolve.shader.
        public const string ResolveShaderName = "Hidden/RealPortal/WindowResolve";
        public const int ResolvePass = 0;
        public const string ResolvePassName = "RESOLVE";

        // WindowCount.compute.
        public const string KernelClear = "CSClear", KernelCount = "CSCount", KernelStatsClear = "CSStatsClear", KernelDepthStats = "CSDepthStats";
        /// <summary>_RP_Counts: [0..255] visible px per id, [256..511] disoccluded visible px per id.</summary>
        public const int CountEntries = 512;
        /// <summary>_RP_Stats: 4 uints per render (asuint(min non-sky depth), asuint(max non-sky depth), water px, valid px), at
        /// most StatsSlots renders a frame; CSStatsClear sets every entry to (asuint(FLT_MAX), 0, 0, 0) before the first render.</summary>
        public const int StatsSlots = 32, StatsStride = 4;
        /// <summary>Id texture value: bits 0..7 the window's VisId, bit 15 set where the shown pixel was disoccluded.</summary>
        public const int IdDisoccludedBit = 0x8000;
        /// <summary>Readback ring depth (P11: results after 2 frames).</summary>
        public const int ReadbackRing = 3;
    }

    /// <summary>Logging for the shared code (the plugin points Sink at its BepInEx log; the default is Unity's console).</summary>
    public static class WindowLog
    {
        public static Action<string> Sink;
        static readonly Dictionary<string, float> s_next = new Dictionary<string, float>();

        public static void Warn(string message)
        {
            if (Sink != null)
                Sink(message);
            else
                Debug.LogWarning("[RealPortal] " + message);
        }

        /// <summary>At most once per `seconds` per key (errors that may repeat every frame).</summary>
        public static void WarnEvery(string key, float seconds, string message)
        {
            float now = Time.unscaledTime;
            if (s_next.TryGetValue(key, out float next) && now < next)
                return;
            s_next[key] = now + seconds;
            Warn(message);
        }
    }

    // =================================================================================================================
    // Components. One owner each (Docs\S2S3-CONTRACTS.md §2). The concrete classes live in the files of the code map
    // (Runtime\Windows\*.cs) and are made by WindowManager.CreateDefault.
    // =================================================================================================================

    /// <summary>A component of the pipeline.</summary>
    public interface IWindowPart : IDisposable
    {
        /// <summary>
        /// This part can run here: implemented, and its resources (shader, kernel, platform support) present. False makes
        /// every mechanism that needs it inert (the post-S1 path). Cheap; may change at runtime (resources lost/found).
        /// </summary>
        bool Ready { get; }
    }

    /// <summary>
    /// Facts only the game side knows (Valheim, RemoteAreas), given to the shared code. Implemented by the plugin
    /// (Plugin\Windows\WindowWorld.cs, owner sched); the Unity tests pass a stub. Main thread; called during Plan and
    /// RenderChosen, so no allocation and no Unity object creation.
    /// </summary>
    public partial interface IWindowWorld
    {
        /// <summary>Render group (same key as PortalRenderSettings.GroupOf: one per far environment; 0 = the game's own).</summary>
        int GroupOf(PortalWindow w);
        /// <summary>The far side may be shown (the link is live and its far area core-ready); false → Closed.</summary>
        bool CanShow(PortalWindow w);
        /// <summary>The player's body or the camera arm is in or passing through this opening (mandatory Live).</summary>
        bool Crossing(PortalWindow w);
        /// <summary>Moving creatures and effects of the far side inside the frustum the cache was rendered with.</summary>
        int LifeInView(WindowState s);
        /// <summary>The far environment of this window has precipitation.</summary>
        bool WeatherActive(PortalWindow w);
        /// <summary>World water level (m) of the far side (depth stats: what is seen through water).</summary>
        float WaterLevel { get; }
        /// <summary>T_target: the frame time the game aims for (its FPS limit, or 16.7 ms uncapped).</summary>
        float TargetFrameMs { get; }
    }

    /// <summary>
    /// Replaces the PortalRenderer.RenderAll loop (owner sched). Owns one WindowState per PortalWindow, plans at
    /// GameCamera.LateUpdate and renders at the game camera's onPreCull. With nothing effective it is a pass-through to
    /// PortalRenderer.RenderAll (bit-exact post-S1).
    /// Per frame (Docs\S2S3-CONTRACTS.md §4): Plan(main) [LateUpdate postfix] → RenderChosen(main) [MainPreCull] →
    /// readbacks two frames later, applied by the next Plans.
    /// </summary>
    public partial interface IWindowManager : IDisposable
    {
        PortalRenderer Renderer { get; }
        WindowPipelineSettings Settings { get; }
        /// <summary>The plugin's live window list: the same List instance every frame, refilled by the plugin before Plan
        /// and before RenderChosen (PortalSystem.Live()). Windows may appear or vanish between the two.</summary>
        IList<PortalWindow> Windows { get; set; }
        /// <summary>Game-side facts (null in tests: conservative defaults).</summary>
        IWindowWorld World { get; set; }

        IWindowCameraPool Pool { get; }
        IWindowScheduler Scheduler { get; }
        IWindowCostModel Cost { get; }
        IGpuBudget Gpu { get; }
        IWindowVisibility Visibility { get; }
        IWindowResolve Resolver { get; }
        IWindowComposite Composite { get; }

        /// <summary>The manager's own frame loop is implemented (false: RenderChosen is RenderAll).</summary>
        bool Ready { get; }
        /// <summary>Plan has work this frame (Win.Tiers effective). False: Plan returns at once and RenderChosen runs the
        /// legacy count scheduler.</summary>
        bool WantsPlan { get; }

        /// <summary>
        /// From the GameCamera.LateUpdate postfix (pose final, before TAA jitter; plugin: PortalSystem.PlanWindows).
        /// Collects readbacks (Visibility.Collect), samples the GPU budget, computes rect/share/tier/error for every
        /// window and chooses this frame's renders in render order (grouped by IWindowWorld.GroupOf). No rendering, no
        /// camera-targeted draws (P3(ii): a draw queued for a camera that then does not render leaks into its next frame).
        /// </summary>
        void Plan(Camera main);

        /// <summary>
        /// From the game camera's onPreCull, where PortalRenderer.RenderAll was called (plugin: PortalSystem.RenderWindows),
        /// with PortalRenderSettings.Jitter set. Re-plans geometry if the pose moved more than ReplanMeters/ReplanDegrees
        /// since Plan (or Plan did not run this frame), renders the chosen windows (BeforeSlotRender → Render →
        /// AfterSlotRender per render), resolves stale ones, binds what each window shows, and builds the main camera's
        /// window passes (composite, visibility). Must leave the renderer exactly as RenderAll does when nothing is effective.
        /// </summary>
        void RenderChosen(Camera main);

        /// <summary>This frame's renders in the order they ran (after RenderChosen) or will run (after Plan).</summary>
        IReadOnlyList<WindowState> Chosen { get; }
        /// <summary>One state per window in Windows (order of Windows) as of the last Plan/RenderChosen.</summary>
        IReadOnlyList<WindowState> States { get; }
        WindowState StateOf(PortalWindow w);

        /// <summary>The cached view of `w` may not be shown as is (plugin: env switch, lightning, span change).</summary>
        void Invalidate(PortalWindow w, WindowInvalid why);
        void InvalidateAll(WindowInvalid why);
        /// <summary>Graphics settings, FOV, HDR or rendering path changed, or a Win.* switch flipped: slot cameras are set up
        /// again before their next render, AO settings re-synced.</summary>
        void SettingsChanged();
        /// <summary>The game camera went away, the plugin suspended or was disabled: drop the plan, readbacks in flight and
        /// resolved textures; keep cameras and caches (PortalRenderer.RenderAll(null, …) still tears down its own part).</summary>
        void Reset();

        /// <summary>Raised by RenderLevel for every level-0 window render (slot or legacy camera; target == null) right
        /// before Render(): the camera has its final transform, projection, target and culling mask, the far environment is
        /// applied (PortalRenderer.BeforeRender ran), and nothing between this and Render() can skip the render. Handlers in
        /// subscription order; the camera-targeted draws (grass) must subscribe last. A throwing handler is logged and skipped.</summary>
        event Action<WindowState, Camera> BeforeSlotRender;
        /// <summary>Raised after that Render() (also when it threw), before PortalRenderer.AfterRender; handlers undo what
        /// their BeforeSlotRender did.</summary>
        event Action<WindowState, Camera> AfterSlotRender;
        /// <summary>A slot camera was made (set up once; add per-camera components and command buffers here).</summary>
        event Action<Camera> OnSlotCameraCreated;
        /// <summary>A slot camera is about to be destroyed (remove what OnSlotCameraCreated added; ungrass-exclude it).</summary>
        event Action<Camera> OnSlotCameraDestroyed;

        /// <summary>Only PortalRenderer.RenderLevel calls these (owner slots), at the points the events describe.</summary>
        void RaiseBeforeSlotRender(WindowState s, Camera cam);
        void RaiseAfterSlotRender(WindowState s, Camera cam);

        /// <summary>Counters of the last frame.</summary>
        WindowFrameStats Stats { get; }

        // ---- Lab hooks (RealPortalLab; deterministic, override the scheduler) --------------------------------------------
        /// <summary>Force a tier for `w` (null window: all; null tier: off).</summary>
        void LabForceTier(PortalWindow w, WindowTier? tier);
        /// <summary>Render cadence for `w` (null: all): 0 off, 1 every frame (fresh), k+1 one render then k frames stale
        /// (resolved or reprojected per Win.Resolve), counted from this call.</summary>
        void LabCadence(PortalWindow w, int every);
        /// <summary>A window's tier changed (state, from, to), raised during Plan/RenderChosen.</summary>
        event Action<WindowState, WindowTier, WindowTier> TierChanged;
        /// <summary>A window's WindowShow changed (state, from, to), raised when RenderChosen binds it.</summary>
        event Action<WindowState, WindowShow, WindowShow> ShowChanged;
        /// <summary>RenderChosen finished (after the composite was built).</summary>
        event Action<WindowFrameStats> FrameDone;
    }

    /// <summary>Tiers, error and choice (owner sched). Pure CPU, no Unity objects; called by the manager in Plan.</summary>
    public partial interface IWindowScheduler : IWindowPart
    {
        /// <summary>Raw tier (§2.2) → WantTier, then hysteresis ±Hysteresis and DwellSeconds → Tier/PrevTier/TierSince, and
        /// Params = ParamsFor(Tier) (ParamsFrame = ctx.Frame). Honors LabTier.</summary>
        void Classify(WindowState s, in WindowFrameContext ctx);
        /// <summary>E (§4.6) in main-screen px into s.ErrorPx/EPar/ETime/ERes; +∞ while s.Invalid != None. With Win.ErrorModel
        /// off: +∞ when the cache is at least Caps.LegacyEvery frames old, else 0.</summary>
        float Error(WindowState s, in WindowFrameContext ctx);
        /// <summary>The frame's choice (§4.6 "Per frame"): sets Chosen/Reason/Priority/Show on every state and fills `chosen`
        /// in render order (by group, then the order of their first member). Mandatory first; then priority within
        /// ctx.Budget; then up to Budget.OverBudget renders past hard caps.</summary>
        void Choose(IReadOnlyList<WindowState> states, in WindowFrameContext ctx, List<WindowState> chosen);
        /// <summary>The effect set of a render at `tier` for `s` (§4.5 table, the switches, the dynamic resolution factor).</summary>
        TierParams ParamsFor(WindowTier tier, WindowState s, in WindowFrameContext ctx);
    }

    /// <summary>Cost estimates and B_cpu (owner sched).</summary>
    public partial interface IWindowCostModel : IWindowPart
    {
        /// <summary>Main-thread ms of a render of `s` at `tier` (its CpuEma at that tier, else the tier's mean, else a prior).</summary>
        float CpuEstimateMs(WindowState s, WindowTier tier);
        /// <summary>GPU ms: g0[tier] + gpx[tier]·pixels + gshadow·cascades + gao·aoPixels (P9 coefficients; AoTemporal charged to Live).</summary>
        float GpuEstimateMs(WindowState s, WindowTier tier, int pixels);
        /// <summary>After each render: Stopwatch ms around submit + Render + capture (the frame loop measures it).</summary>
        void RecordRender(WindowState s, WindowTier tier, double cpuMs, int pixels);
        /// <summary>Once per frame (at Plan, for the previous frame): whole frame ms and the window renders' ms.</summary>
        void RecordFrame(double frameMs, double windowCpuMs);
        /// <summary>B_cpu = clamp(T_target − EMA(frame − windowCpu) − CpuMarginMs, CpuMinMs, CpuMaxMs), or CpuBudgetMs when set.</summary>
        float CpuBudgetMs(float targetFrameMs);
    }

    /// <summary>
    /// B_gpu (owner sched). FrameTimingManager is off in the shipped player (P8), so the default source is the fallback
    /// controller: Stopwatch around PlayerLoop TimeUpdate.WaitForLastPresentationAndUpdateTime (inserted by Install).
    /// </summary>
    public partial interface IGpuBudget : IWindowPart
    {
        /// <summary>FrameTimingManager.IsFeatureEnabled() (P8: false in the shipped player).</summary>
        bool FrameTimingAvailable { get; }
        /// <summary>Insert/remove the PlayerLoop timing hooks (main thread; idempotent).</summary>
        void Install();
        void Uninstall();
        /// <summary>Once per frame at Plan: read the source, step the controller.</summary>
        void Sample(int frame);
        /// <summary>B_gpu in ms (+∞ when there is no signal).</summary>
        float BudgetMs { get; }
        /// <summary>Dynamic resolution factor for Live/NearCached (DynResFloor..1; recovers slowly).</summary>
        float ResolutionFactor { get; }
        bool GpuBound { get; }
        /// <summary>Mean wait for the last presentation (ms), the controller's input.</summary>
        float WaitMsEma { get; }
    }

    /// <summary>
    /// One Camera per render slot (owner slots, §4.3). Each slot camera renders at most once per frame; made on demand up
    /// to SlotCapacity, set up once (on creation, SettingsChanged, a change of the game camera's HDR, rendering path,
    /// clear flags or depth modes), never CopyFrom per frame. The per-render changes (transform, LensShift projection
    /// with near = NearToOpening, targetTexture, jitter, culling mask, layer cull distances) are RenderLevel's.
    /// </summary>
    public partial interface IWindowCameraPool : IWindowPart
    {
        int Capacity { get; }
        /// <summary>Slot cameras made so far.</summary>
        int Count { get; }
        Camera CameraAt(int slot);
        /// <summary>The slot of a pool camera, else -1. Never throws (the plugin asks for any camera).</summary>
        int SlotOf(Camera cam);
        bool IsSlotCamera(Camera cam);
        /// <summary>
        /// A camera for a level-0 render of `s` this frame that has not rendered this frame: s.StickySlot when free, else
        /// the least recently used free slot that no sticky window holds, else a new one (≤ Capacity), else the least
        /// recently used non-sticky slot of a window that is not rendering this frame. Sets s.Slot (and s.StickySlot);
        /// marks the slot as rendering this frame. Null when none is left: the caller renders on the legacy shared camera
        /// (s.Slot = -1).
        /// </summary>
        Camera Acquire(WindowState s, Camera main, PortalRenderSettings settings);
        /// <summary>The window is gone: its sticky slot becomes free.</summary>
        void Release(WindowState s);
        /// <summary>Set the slots up again before their next render.</summary>
        void SettingsChanged();
        /// <summary>Statistics of this frame.</summary>
        int AcquiredThisFrame { get; }
        int StickyHits { get; }
        event Action<Camera> Created;
        event Action<Camera> Destroyed;
    }

    /// <summary>
    /// Window occlusion and depth statistics (owner gpu, §4.8): the id mask (written by the composite) counted per window
    /// by WindowCount.compute, plus each render's depth min/max and water share, read back asynchronously (P11: 2 frames).
    /// Requests go into command buffers (CommandBuffer.RequestAsyncReadback); the callback only copies the data into a
    /// ring entry; Collect applies the entries in frame order at a defined point (Plan).
    /// </summary>
    public partial interface IWindowVisibility : IWindowPart
    {
        /// <summary>Observed frames between request and result (2 expected).</summary>
        int LatencyFrames { get; }
        /// <summary>Assign s.VisId (1..255, stable while the window lives; 0 when none is left).</summary>
        void Register(WindowState s);
        void Unregister(WindowState s);
        /// <summary>Once per frame (manager: in Plan, or at the start of RenderChosen without Plan): apply finished results to
        /// the states (VisiblePx, VisibleFrame, ZeroReadbacks, DisoccludedPx; Cache.ZNear/ZFar/WaterFrac/StatsKnown for the
        /// cache whose Frame/StatsIndex match). Results for windows no longer in `states` are dropped.</summary>
        void Collect(int frame, IReadOnlyList<WindowState> states);
        /// <summary>From RenderLevel (owner slots) while it builds a level-0 render's capture command buffer
        /// (CameraEvent.AfterForwardAlpha, after the LINEARDEPTH blit into `linearDepth`): append the CSDepthStats dispatch.
        /// Sets s.Cache.StatsIndex/StatsFrame; returns the index or -1 (not Ready, slots full).</summary>
        int AppendDepthStats(CommandBuffer capture, WindowState s, RenderTargetIdentifier linearDepth, int width, int height, Camera cam);
        /// <summary>Once per frame after all window renders (owner gpu, from the composite build): record into `afterEverything`
        /// (the game camera's CameraEvent.AfterEverything buffer) the count dispatch over `idTexture` (when counts are wanted)
        /// and the readbacks of the counts and of this frame's depth stats.</summary>
        void Schedule(CommandBuffer afterEverything, RenderTargetIdentifier idTexture, int width, int height, bool counts);
        /// <summary>Drop results in flight (Reset).</summary>
        void Reset();
    }

    /// <summary>
    /// Depth reprojection of stale windows (owner gpu, §4.7). One draw per stale window over its current rect writes the
    /// resolved textures (colour with alpha 0 where disoccluded, linear depth, normals) that the unchanged composite passes
    /// then read. Executes immediately (Graphics.ExecuteCommandBuffer) at RenderChosen, before the game camera renders.
    /// </summary>
    public partial interface IWindowResolve : IWindowPart
    {
        /// <summary>Start of RenderChosen: last frame's resolved textures go back to WindowRTPool.</summary>
        void BeginFrame();
        /// <summary>Resolve `s` onto s.Rect for this frame's camera. Needs s.Cache.Valid. Writes s.ResolvedColor/Depth/Normal,
        /// ResolvedRect, ResolvedFrame. False (nothing written) when not Ready or there is no valid cache.</summary>
        bool Resolve(WindowState s, in WindowFrameContext ctx);
        int ResolvesLastFrame { get; }
    }

    /// <summary>
    /// The game camera's window passes (owner gpu, §4.11). Built inside PortalRenderer's composite region (UpdatePatch,
    /// UpdateSky, UpdateMotion); this interface exposes what other owners and the lab read.
    /// </summary>
    public partial interface IWindowComposite : IWindowPart
    {
        // Ready (IWindowPart) = the MRT passes exist (AUXBASE/AUXMRT) and the device has at least 5 render targets.

        /// <summary>The WINDOWID pass exists (occlusion without MRT).</summary>
        bool ReadyIds { get; }
        /// <summary>The RESTOREVIEW pass exists (Win.RestoreFromWindow).</summary>
        bool ReadyRestoreFromWindow { get; }
        /// <summary>The id texture (R16_UInt, the game camera's pixel size) of the current frame, or null.</summary>
        RenderTexture IdTexture { get; }
        /// <summary>Window draws added to the game camera last frame (lab: 4 per window with MRT, 7 without).</summary>
        int DrawsLastFrame { get; }
    }

    /// <summary>The set of components a WindowManager is built from (WindowManager.CreateDefault makes the real ones;
    /// tests may pass fakes).</summary>
    public sealed class WindowParts
    {
        public IWindowCameraPool Pool;
        public IWindowScheduler Scheduler;
        public IWindowCostModel Cost;
        public IGpuBudget Gpu;
        public IWindowVisibility Visibility;
        public IWindowResolve Resolver;
        public IWindowComposite Composite;
    }

    /// <summary>Which mechanisms are in force. Every owner tests these, never the raw WindowFlags, for S2/S3 behaviour.</summary>
    public static class WindowMechanisms
    {
        static bool IsReady(IWindowPart p) => p != null && p.Ready;

        /// <summary>
        /// The switch is on and every part the mechanism needs is Ready (Docs\S2S3-CONTRACTS.md §3 has the table, with the
        /// extra conditions the plugin adds for OwnFog and GrassTargeted). False for a null manager (the Unity tests).
        /// </summary>
        public static bool Effective(this IWindowManager m, WindowMechanism x)
        {
            if (m == null)
                return false;
            switch (x)
            {
                case WindowMechanism.CameraSlots:
                    return WindowFlags.CameraSlots && IsReady(m.Pool);
                case WindowMechanism.OwnFog:
                    return WindowFlags.OwnFog;
                case WindowMechanism.AoTierA:
                    return WindowFlags.AoTierA && m.Effective(WindowMechanism.CameraSlots);
                case WindowMechanism.AoTemporal:
                    return WindowFlags.AoTemporal && m.Effective(WindowMechanism.AoTierA);
                case WindowMechanism.GrassTargeted:
                    return WindowFlags.GrassTargeted && m.Effective(WindowMechanism.CameraSlots);
                case WindowMechanism.Tiers:
                    return WindowFlags.Tiers && m.Ready && IsReady(m.Scheduler) && IsReady(m.Cost);
                case WindowMechanism.Budget:
                    return WindowFlags.Budget && m.Effective(WindowMechanism.Tiers);
                case WindowMechanism.ErrorModel:
                    return WindowFlags.ErrorModel && m.Effective(WindowMechanism.Tiers);
                case WindowMechanism.Resolve:
                    return WindowFlags.Resolve && m.Ready && IsReady(m.Resolver);
                case WindowMechanism.Occlusion:
                    return WindowFlags.Occlusion && m.Ready && IsReady(m.Visibility) && m.Composite != null && m.Composite.ReadyIds;
                case WindowMechanism.CompositeMRT:
                    return WindowFlags.CompositeMRT && IsReady(m.Composite);
                case WindowMechanism.ShadowTiers:
                    return WindowFlags.ShadowTiers && m.Ready && IsReady(m.Scheduler);
                case WindowMechanism.RestoreFromWindow:
                    return WindowFlags.RestoreFromWindow && m.Composite != null && m.Composite.ReadyRestoreFromWindow;
                case WindowMechanism.ShadowTiersSmall:
                    return WindowFlags.ShadowTiersSmall && m.Ready && IsReady(m.Scheduler);
                default:
                    return false;
            }
        }

        /// <summary>
        /// The shadow caps of `tier` (TierParams.ShadowDistanceCap, ShadowCascadesCap, PointLightShadows false) are in force:
        /// Win.ShadowTiers for every tier, Win.ShadowTiersSmall for SmallCached alone (the one tier whose capped view the lab
        /// could not tell from Live's; NearCached keeps the game's shadows). The one per-tier decision the scheduler
        /// (ParamsFor), the cost model and the plugin's point-light handler (TierEffects) take.
        /// </summary>
        public static bool ShadowCaps(this IWindowManager m, WindowTier tier) =>
            m.Effective(WindowMechanism.ShadowTiers) || (tier == WindowTier.SmallCached && m.Effective(WindowMechanism.ShadowTiersSmall));
    }

    // Seams on the existing shared classes (the rest of each class is in its own file).
    public sealed partial class PortalWindow
    {
        /// <summary>The pipeline's state of this window (WindowState.For makes it); null until the pipeline first sees it.</summary>
        internal WindowState State;
    }

    public sealed partial class PortalRenderer
    {
        /// <summary>The window manager driving this renderer (set by the WindowManager constructor); null in the Unity tests
        /// and before the plugin made one. RenderLevel reaches the slot cameras and the slot events through it.</summary>
        public IWindowManager Manager { get; internal set; }

        // Called first in Destroy(), in this order. Each owner implements his in his own partial file
        // (Runtime\Windows\PortalRenderer.Plan.cs / .Composite.cs / .Slots.cs); unimplemented ones compile to nothing.
        partial void DestroyPlan();
        partial void DestroyComposite();
        partial void DestroySlots();
    }
}
