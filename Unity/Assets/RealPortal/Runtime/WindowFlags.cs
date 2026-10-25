namespace RealPortal
{
    // Switches of the window pipeline (stages S1-S3), read on hot paths as plain fields. This code is shared with the
    // Unity test project, which has neither Valheim nor the engine plugin: in the game the plugin binds each one to
    // its engine feature (Plugin\WindowFeatures.cs: "Win.<Name>", flipped by the lab's "feat" command and the
    // config). Every switch off gives exactly the behaviour from before it existed.
    public static class WindowFlags
    {
        // Win.DepthOnly: the window camera asks for the depth texture only, not the game camera's depth+normals and
        // motion vectors (nothing reads them in a window render; the game's SSAO copy runs without its temporal
        // filter, whose motion vectors would come from another window anyway).
        public static bool DepthOnly = true;
        // Win.FallbackFlag: inside a window render the other windows show their fallback through one global shader
        // flag (_RP_InWindowRender, _RP_RenderingWindowId), not by rewriting every window's material per render.
        public static bool FallbackFlag = true;
        // Win.LayerCull: object layers are culled (spherical) beyond the distance the game ever loads objects, on the
        // window camera and on the game camera, so neither draws other far areas kilometres away.
        public static bool LayerCull = true;
        // Win.LeanFixed: the game camera's fixed per-frame passes only when they do something (no depth copy without
        // VirtualDepth, motion vectors copied only with a window or a jump, the second sun shafts chain only when the
        // sun seen through a window can reach one, nothing at all without windows).
        public static bool LeanFixed = true;
        // Win.RTPool: window view textures in 32 px size classes, kept between frames with an idle expiry (the top-level
        // views; deeper recursion levels keep Unity's temporary textures).
        public static bool RTPool = true;
        // Win.Margin: the border around a window's rect is at most a quarter of the rect (small windows no longer
        // render several times their own pixels), but never less than the window camera's SSAO reaches
        // (PortalRenderSettings.EffectRadius), in 32 px size steps.
        public static bool Margin = true;
        // Read by the far-environment side (PortalSystem, FarEnvironment, FarSkyOcclusion, FarReflections).
        public static bool EnvGroups = true;
        public static bool SkyPerArea = true;
        public static bool ProbePerArea = true;

        // Lab: the parts of LeanFixed one by one (each also needs LeanFixed). LeanIdle covers both idle paths: the
        // renderer only taking down its buffers and PortalSystem skipping its fog, sun shafts and SSAO syncs.
        public static bool LeanPatchCopy = true, LeanMotion = true, LeanShafts = true, LeanIdle = true;

        // ---- Stage S2 (Docs\S2S3-CONTRACTS.md §3). A switch alone does nothing: S2/S3 code tests
        // WindowMechanisms.Effective, which also needs the parts behind it implemented and their resources present.
        // Win.CameraSlots: one camera per render slot, set up once, each rendering at most once a frame (sticky for Live
        // windows); off: the one shared camera copied from the game camera for every render.
        public static bool CameraSlots = true;
        // Win.OwnFog: a slot camera fogs its view with its own command buffer at AfterImageEffectsOpaque (the game's fog
        // material, the far environment's values); off: a fog-only PostProcessingBehaviour on it, as on the shared camera.
        public static bool OwnFog = true;
        // Win.AoTierA: the window camera's SSAO only for Live windows (and NearCached ones from 2 % of the screen, at half
        // resolution); off: on every window render the game's SSAO is on for.
        public static bool AoTierA = false;
        // Win.AoTemporal: SSAO with its temporal filter on sticky Live slots (their own history and motion vectors; the
        // lab's P17 decides); off: without the filter (Win.DepthOnly).
        public static bool AoTemporal = false;
        // Grass.Targeted: slot cameras are left out of the engine's grass hand-on (GrassCameraModule.ExcludeCamera) and get
        // the game camera's grass batches culled by their own view, right before their render; off: grass reaches them as
        // it reaches any camera.
        public static bool GrassTargeted = true;

        // ---- Stage S3.
        // Win.Tiers: windows in tiers (Live, near cached, small cached, closed, hidden), planned after the camera moved
        // (GameCamera.LateUpdate); off: the count scheduler (RefreshSmallWindowsLess, MaxRendersPerFrame), as in 1.0.3.
        // Off by default since FARWORLD-V3 V0: cached views ghost under the game's TAA (Window.Quality=Performance: on).
        public static bool Tiers = false;
        // Win.Budget: window renders within a main-thread ms budget and a GPU budget; off: only the count cap and the caps.
        public static bool Budget = true;
        // Win.ErrorModel: a cached view is renewed by its on-screen error (parallax, time, resolution); off: every 2nd/3rd frame.
        public static bool ErrorModel = true;
        // Win.Resolve: a stale view is reprojected by its depth (ray march) onto the window's current rect; off: laid flat
        // onto the opening as it was seen then.
        public static bool Resolve = true;
        // Win.Occlusion: visible pixels per window counted on the GPU (read back 2 frames later); windows hidden behind
        // walls or other windows stop rendering; off: frustum and rect only.
        public static bool Occlusion = true;
        // Win.CompositeMRT: the game camera's window passes in one multi-target draw per window; off: one draw per pass.
        public static bool CompositeMRT = true;
        // Win.ShadowTiers: shadow distance, cascades and point-light shadows by tier; off: the game's for every render.
        public static bool ShadowTiers = false;
        // Win.RestoreFromWindow: the window's pixels are put back after the game's opaque effects from the window's own
        // view, without the full-screen copy; off: from the copy (parity check pending).
        public static bool RestoreFromWindow = false;

        // Twenty portals at once (Docs\PORTALS20-PLAN.md); on by default once their lab gate passed.
        // Win.MandatoryCap (A6): renders the planner owes outside the budget (first show, camera jump, urgent) at most two a
        // frame, the rest queued by size and distance; off: all of them in the frame they come due.
        public static bool MandatoryCap = true;
        // Win.SlotPrewarm (A11): every slot camera (with its AO and fog) made before the first window needs it, and kept
        // while portals are live; off: made on first use, destroyed after 30 s idle.
        public static bool SlotPrewarm = true;
        // Win.CacheBudget (B3): hidden windows' cached views kept by least-recent use within a memory budget; off: dropped
        // after 10 s hidden.
        public static bool CacheBudget = false;
        // Win.Prewarm (B3): windows just outside the view cone with no cached view rendered ahead, one a frame; off: rendered
        // when they come into view.
        public static bool Prewarm = false;
        // Win.ShadowTiersSmall (round 2): Win.ShadowTiers' caps for SmallCached windows only (their shadow caps measured
        // invisible; NearCached keeps the game's shadows).
        public static bool ShadowTiersSmall = false;
        // Win.ShadowCost (round 2): the cost model counts a window's shadow casters from its last render, so the budget
        // spreads shadow-heavy renders instead of stacking them in one frame.
        public static bool ShadowCost = true;

        // ---- FARWORLD-V3 stage V0, fresh windows (Docs\FARWORLD-V3.md §7 V0, Appendix A). Window.Quality=Continuation
        // turns these on (and Win.Tiers off), Performance turns them off (today's v2 tiers); each can be flipped alone too.
        // Win.PointDepth: the view depth read by point fetches (Load) from Point-filtered view-depth textures, so motion
        // vectors, the depth patch and the aux passes are exact at silhouettes; off: bilinear reads of bilinear textures.
        public static bool PointDepth = true;
        // Win.ExplicitJitter: a window render's TAA jitter as an explicit jittered projectionMatrix beside the unjittered
        // nonJitteredProjectionMatrix, transparents unjittered (as the game camera's TaaComponent does); off: the jitter
        // added to the lens shift (transparents jittered too).
        public static bool ExplicitJitter = true;
        // Win.LiveFill: with the count scheduler (Win.Tiers off) windows due every 2nd or 3rd frame render every frame
        // while the main thread has headroom, largest first; off: their cadence and MaxRendersPerFrame alone.
        public static bool LiveFill = true;
        // Win.EnvOncePerArea: the far side a window render needs (environment, mist, distant terrain) set up once for the
        // renders of one far area in a row and kept between them (with EnvGroups), the area woken once a frame; off: set
        // up and put back around every render. Read by the far-environment side (PortalSystem).
        public static bool EnvOncePerArea = true;
        // Win.CaptureOnce: the view depth and normal capture buffers attached to each window camera once and only
        // recorded per render (empty between renders); off: added before every render and removed after it.
        public static bool CaptureOnce = true;

        // ---- The ripple fixes (Docs\RIPPLE-PLAN.md §4): each off by default until its lab line passes.
        // Win.TaaViewDepth: the game camera's TAA material gets the depth seen through the windows as its _CameraDepthTexture
        // every frame (Patches.TaaMotion), so its closest-depth dilation picks what the window shows; off: whatever is
        // bound globally when TAA runs (Unity's own depth, the windows as flat surfaces).
        public static bool TaaViewDepth = false;
        // Win.BlurInputs: a buffer first at the game camera's BeforeImageEffects binds the fixed-up motion vectors and the
        // view depth again, ahead of the game's motion blur, and for the OnRenderImage effects after it (depth of field, and
        // TAA's dilation when its material has no depth of its own); off: what Unity binds there.
        public static bool BlurInputs = false;
        // Win.ShadowRes: the sun's shadow map is the size Unity gives the game camera during each window render (put back
        // after it); off: the size Unity picks for the window's own texture (2-4x smaller, in power-of-two steps).
        public static bool ShadowRes = false;
        // Win.ShadowFullCull: an on-screen view's camera culls by the game camera's whole jittered frustum from its place,
        // its projection staying the rect's (P47: whether the sun's cascades then follow the full frustum); off: the rect's.
        public static bool ShadowFullCull = false;
        // Win.NearSteps: the window camera's near plane at the opening floored to 2 % steps, so the frustum keeps its shape
        // for runs of frames; off: the opening's nearest depth every frame.
        public static bool NearSteps = false;
        // Win.LiveShare: without Win.Tiers, windows from 1.5 % of the screen render every frame whatever LiveFill's
        // headroom, and the 8 m rule is measured from the player too, not only from the camera; off: from 5 % or 8 m from
        // the camera.
        public static bool LiveShare = false;
        // Win.StaleTaa: a window shown flat from an earlier render is laid onto the opening by that render's jittered
        // view-projection (with Clip = NearPlane, the only clip whose renders carry the jitter), keeps the opening's own
        // motion vectors, and the LOD cross-fade stays instant through frames that only show earlier renders; off: the
        // unjittered one, the far scene's vectors, the cross-fade by fresh renders.
        public static bool StaleTaa = false;
        // Win.FullFrameHero: without Win.Tiers, the largest window renders every frame over the whole screen (the game
        // camera's symmetric frustum from its place: its shadow cascades and atlas size), as FullFrame does for every
        // window; off: over its rect.
        public static bool FullFrameHero = false;

        // ---- Parity (Docs\PARITY-PLAN.md §2, §3): a window shows exactly what the game would show standing there, and the
        // base game stays as without the mod. Each on by default; off is the behaviour from before it. The renderer's:
        // Win.DistantLive (P2): windows render as far as their far end stays loaded and down to a pixel floor
        // (Settings.MinPixels); off: not beyond Settings.MaxDistance or under MinScreenFraction (the fallback shows).
        public static bool DistantLive = true;
        // Win.VanillaLook (P2, P7, P43): a window not rendered (out of reach, too small, nested, in a reflection probe) is
        // discarded by the shader, the game's empty ring; off: the procedural blue fallback disc.
        public static bool VanillaLook = true;
        // Win.NestedLive (P7): windows seen through a window render at level 1 when their own far side is loaded; off:
        // Settings.Recursion alone (0: the fallback).
        public static bool NestedLive = true;
        // Win.AllLive (P8): without Win.Tiers every on-screen window above the pixel floor renders every frame (Win.LiveShare
        // with its share at 0, no MaxRendersPerFrame hold, no LiveFill cap); off: the cadence and caps as set.
        public static bool AllLive = true;
        // Win.OcclusionStatic (P25): Win.Occlusion hides a window only while nothing that moves (a character, ship, door,
        // rigid body) stands between the camera and it; off: by the camera's stillness alone.
        public static bool OcclusionStatic = true;
        // Win.TransparentDepth (P42): this side's transparents at a window read the view depth as _CameraDepthTexture, with
        // an AO band outside the mask; off: the window is a flat surface to them.
        public static bool TransparentDepth = true;
        // The game side's (PortalSystem, Patches, the far environment): Win.LightsPerPlace (P9, P39), light limits ranked per
        // place; Win.LodFadeWindowsOnly (P10), the instant LOD fade only round the window batch; Win.ExitShadows (P13), what
        // hides behind the exit keeps casting shadows; Win.BaseHidesFar (P14), the game camera draws only what the game
        // loaded (and Win.LayerCull follows the ground relief); Win.CrossingBodies (P23), every body in an opening shown on
        // both sides; Win.ShieldDome (P24), shield domes in windows; Win.FarAoTint (P32), the far side's AO tint always;
        // Win.FarShafts (P33), sun shafts per pair rotation in the far colour; Win.ParticleSize (P41), far particles keep
        // their screen size in small windows; Win.CrossNearPlane (P44), only the player's copy raises the near plane;
        // Win.SkyLive (P31), the far sky-cover picture every second while on screen.
        public static bool LightsPerPlace = true, LodFadeWindowsOnly = true, ExitShadows = true, BaseHidesFar = true,
            CrossingBodies = true, ShieldDome = true, FarAoTint = true, FarShafts = true, ParticleSize = true,
            CrossNearPlane = true, SkyLive = true;

        // ---- FARWORLD-V3 stage V1, the virtual-view spike (Docs\FARWORLD-V3.md §4.4-§4.6; off by default, lab only).
        // Win.Virtual: a window the game side picks (PortalSystem.LabVirtualWindow) is drawn without a camera: its far side's
        // static content goes into screen-sized G-buffer targets by one command buffer at the game camera's MainPreCull
        // (Windows\Virtual\WindowScreen.cs), lit there by the game's own deferred shading with the sun's cascades as Unity
        // fits them for a camera at C', and handed to the composite as a slot camera's render would be; any failure renders
        // it with its slot camera that frame. Off: every window renders through its camera, as before.
        public static bool Virtual = false;

        // Raised (main thread) after any window switch flipped, so parts that set things up once (slot cameras, AO copies,
        // grass exclusions) do it again. The plugin raises it (WindowFeatures); the Unity tests may call RaiseChanged.
        public static event System.Action Changed;

        public static void RaiseChanged() => Changed?.Invoke();
    }
}
