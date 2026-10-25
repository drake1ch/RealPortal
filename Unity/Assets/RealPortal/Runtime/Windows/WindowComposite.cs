using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace RealPortal
{
    // OWNER: gpu (S3). The game camera's window passes (§4.11): with Win.CompositeMRT one AUXBASE blit per frame and one
    // AUXMRT draw per window replace the DEPTHPATCH x2, SKYDEPTH and MASK draws and their base copies; the id mask for
    // Win.Occlusion (AUXMRT target 4, or the WINDOWID pass without MRT); RESTOREVIEW instead of the full-screen
    // effectsSave copy with Win.RestoreFromWindow. The passes are built in PortalRenderer's composite region (UpdatePatch,
    // UpdateSky, UpdateMotion, owner gpu); this class holds the targets and the pass checks, and records the new passes
    // into the command buffers that region gives it.
    // Contract: IWindowComposite in WindowContracts.cs; passes and targets: Docs\S2S3-CONTRACTS.md §8.1, §9.
    //
    // Ready only when the window shader has the S3 passes (found by name: a bundle built before them reads -1) and the
    // properties _RP_VisId/_RP_Resolved, and the device can render the targets (5 MRTs, R16_UInt): an old bundle or a
    // device without them leaves Win.CompositeMRT / Win.Occlusion / Win.RestoreFromWindow on the post-S1 passes.
    //
    // How the composite region uses it (PortalRenderer, per frame at onPreCull, with `comp = Manager.Composite as
    // WindowComposite` and the mechanism effective):
    //   UpdatePatch, CompositeMRT: instead of the COPYDEPTH blits into _RP_PatchedDepth/_RP_FogDepth and the DEPTHPATCH
    //     draws, `comp.RecordAux(_patch, main, windows, EffectsSeeWindowAsSky ? 2 : 1)` (3 under Win.TransparentDepth), then
    //     `_patch.SetRenderTarget(CameraTarget)` as today. The four depth targets then stand for:
    //       FogDepth   = the fog copy (_fogStart binds it as _CameraDepthTexture; _effectsStart too with VirtualDepth);
    //       ViewDepth  = _RP_PatchedDepth with VirtualDepth (bound at the end of _patch, and _fogEnd's real depth), and
    //                    ViewDepthTexture (DOF; _CameraDepthTexture of the final effects). Without VirtualDepth the real
    //                    depth for _fogEnd/_effectsStart is the camera's own (BuiltinRenderTextureType.Depth, as the
    //                    LeanPatchCopy path), because ViewDepth carries the views whenever a window has a view depth;
    //       SkyDepth   = SkyDepthTexture, WindowMask = WindowMaskTexture (UpdateSky's clears and draws go away).
    //     The aux pass must run whenever any of fog copy, virtual depth, sky depth, view depth or ids is wanted (the old
    //     _patch only for the first two). PlaneDepthTexture keeps its COPYDEPTH in _sky (it is whatever depth the final
    //     effects start with: the fog copy with VirtualDepth, else the real depth). _patchRelease releases nothing of these
    //     (they persist). The targets belong to this class: PortalRenderer.Destroy must not Kill them through its
    //     SkyDepthTexture/ViewDepthTexture/WindowMaskTexture properties (null those in DestroyComposite, and do not let
    //     ScreenDepth reuse or Kill an aliased target when the mechanism goes off).
    //   UpdatePatch, Occlusion without CompositeMRT: `comp.RecordIds(_patch, main, windows)` at the same event.
    //   UpdatePatch, RestoreFromWindow: no _effectsSave; _effectsRestore = SetRenderTarget(CameraTarget) +
    //     `comp.RecordRestoreView(_effectsRestore, windows)` (no _RP_BeforeEffects to release).
    //   IdTexture is non-null only in a frame RecordAux or RecordIds ran (what Visibility.Schedule counts after
    //   everything); `comp.CountDraws(n)` adds every other window draw put on the game camera (its G-buffer draw, MOTION,
    //   RESTORE, and the post-S1 passes when not MRT) to DrawsLastFrame. RecordAux/RecordIds also set each drawn window's
    //   _RP_VisId and _RP_Resolved (resolved = the bound texture is this frame's WindowState.ResolvedColor).
    public sealed partial class WindowComposite : IWindowComposite
    {
        readonly WindowPipelineSettings _settings;
        readonly Shader _windowShader;
        // An instance of the window shader for the full-screen AUXBASE pass (the per-window passes draw with each
        // window's own material, whose properties say what that window shows).
        Material _base;
        int _passAuxBase = -1, _passAuxMrt = -1, _passIds = -1, _passRestoreView = -1;
        bool _mrtOk, _idsOk, _restoreOk, _broken;
        // T0 fog copy, T1 view depth, T2 sky depth (RFloat, raw like the camera's depth), T3 window mask (R8), T4 ids.
        RenderTexture _fog, _view, _sky, _mask, _ids;
        readonly RenderTargetIdentifier[] _aux = new RenderTargetIdentifier[WindowPasses.AuxTargets];
        int _width, _height;
        int _idsFrame = -100, _drawsFrame = -100, _draws;
        readonly Plane[] _planes = new Plane[6];
        // This frame's draw order (see Collect).
        readonly List<PortalWindow> _seen = new List<PortalWindow>();
        static Mesh s_triangle;

        public WindowComposite(WindowPipelineSettings settings, Shader windowShader)
        {
            _settings = settings;
            _windowShader = windowShader;
            Probe();
        }

        public bool Ready => _mrtOk && !_broken;
        public bool ReadyIds => _idsOk && !_broken;
        public bool ReadyRestoreFromWindow => _restoreOk;

        /// <summary>The id texture (R16_UInt) when it was written this frame (AUXMRT target 4 or WINDOWID), else null.</summary>
        public RenderTexture IdTexture => _idsFrame == Time.frameCount ? _ids : null;

        /// <summary>Window draws added to the game camera in the most recent frame that built its composite (this frame
        /// once it has): the new passes recorded here plus what the composite region reports through CountDraws.</summary>
        public int DrawsLastFrame => _drawsFrame >= Time.frameCount - 1 ? _draws : 0;

        /// <summary>T0: the fog copy (the camera's depth, windows as sky or right at the camera where they show a view).</summary>
        public RenderTexture FogDepth => _fog;
        /// <summary>T1: the camera's depth with each window's pixels at the depth seen through it (VirtualDepth and the
        /// final effects' view depth).</summary>
        public RenderTexture ViewDepth => _view;
        /// <summary>T2: "near" except where a window's view shows sky (the second sun shafts pass).</summary>
        public RenderTexture SkyDepth => _sky;
        /// <summary>T3: 1 where a window that shows a view is what the camera sees.</summary>
        public RenderTexture WindowMask => _mask;

        /// <summary>A full-screen triangle in clip space for CommandBuffer.DrawMesh with an identity matrix (AUXBASE, and
        /// WindowResolve's RESOLVE pass): unlike Blit it can draw into several targets at once.</summary>
        public static Mesh FullScreenTriangle
        {
            get
            {
                if (s_triangle == null)
                {
                    s_triangle = new Mesh { name = "RealPortal full-screen triangle", hideFlags = HideFlags.HideAndDontSave };
                    s_triangle.vertices = new[] { new Vector3(-1f, -1f, 0f), new Vector3(-1f, 3f, 0f), new Vector3(3f, -1f, 0f) };
                    s_triangle.triangles = new[] { 0, 1, 2 };
                    // Never culled by a command buffer, but keep the bounds honest for anything that looks.
                    s_triangle.bounds = new Bounds(Vector3.zero, new Vector3(8f, 8f, 2f));
                    s_triangle.UploadMeshData(true);
                }
                return s_triangle;
            }
        }

        // What the shader and the device offer, once (the window shader does not change while the plugin runs).
        void Probe()
        {
            // No window shader (the Unity tests without assets) or no graphics device (batch mode): nothing to report,
            // nothing ready.
            if (_windowShader == null || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                return;
            if (!_windowShader.isSupported)
            {
                WindowLog.Warn("Window composite: the window shader is not supported here; Win.CompositeMRT, Win.Occlusion and Win.RestoreFromWindow stay off.");
                return;
            }
            _base = new Material(_windowShader) { name = "RealPortal composite", hideFlags = HideFlags.HideAndDontSave };
            bool props = _base.HasProperty(WindowShaderIds.VisId) && _base.HasProperty(WindowShaderIds.Resolved);
            _passAuxBase = _base.FindPass(WindowPasses.AuxBaseName);
            _passAuxMrt = _base.FindPass(WindowPasses.AuxMrtName);
            _passIds = _base.FindPass(WindowPasses.WindowIdName);
            _passRestoreView = _base.FindPass(WindowPasses.RestoreViewName);
            bool idFormat = SystemInfo.IsFormatSupported(GraphicsFormat.R16_UInt, GraphicsFormatUsage.Render);
            bool depthFormats = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RFloat)
                                && SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.R8);
            int mrt = SystemInfo.supportedRenderTargetCount;
            bool passes = _passAuxBase >= 0 && _passAuxMrt >= 0;
            _mrtOk = props && passes && idFormat && depthFormats && mrt >= WindowPasses.AuxTargets;
            _idsOk = props && _passIds >= 0 && idFormat;
            _restoreOk = _passRestoreView >= 0;
            if (_mrtOk && _idsOk && _restoreOk)
                return;
            // One line naming what stays off and why (an old bundle is the usual reason).
            var why = new List<string>();
            if (!props || !passes || _passIds < 0 || !_restoreOk)
                why.Add("the window shader lacks the S3 passes (realportal.bundle built before them)");
            if (!idFormat)
                why.Add("no R16_UInt render target");
            if (!depthFormats)
                why.Add("no RFloat/R8 render target");
            if (mrt < WindowPasses.AuxTargets)
                why.Add($"{mrt} render targets at once (5 needed)");
            var off = new List<string>();
            if (!_mrtOk)
                off.Add("Win.CompositeMRT");
            if (!_idsOk)
                off.Add("Win.Occlusion");
            if (!_restoreOk)
                off.Add("Win.RestoreFromWindow");
            WindowLog.Warn($"Window composite: {string.Join(", ", why)}; {string.Join(", ", off)} stay on the post-S1 passes.");
        }

        /// <summary>
        /// Win.CompositeMRT. Records into `cb` (the game camera's patch buffer: CameraEvent.BeforeLighting deferred,
        /// AfterDepthTexture forward, where _CameraDepthTexture is the real depth) one AUXBASE pass over the five aux
        /// targets and one AUXMRT draw per window the camera may see, and sets _RP_FogPatchMode (1: the fog copy has the
        /// windows right at the camera, 2: as sky, 3: at the depth seen through them, Win.TransparentDepth; what
        /// _RP_PatchNear was for the fog copy). Leaves the aux targets bound:
        /// the caller binds CameraTarget again (as _patch does). Returns the windows drawn, or -1 when not Ready (nothing
        /// recorded: use the post-S1 passes).
        /// </summary>
        public int RecordAux(CommandBuffer cb, Camera main, IList<PortalWindow> windows, float fogPatchMode)
        {
            if (cb == null || main == null || windows == null || !Ready || !Ensure(main.pixelWidth, main.pixelHeight, true))
                return -1;
            Collect(main, windows, false);
            cb.SetGlobalFloat(WindowShaderIds.FogPatchMode, fogPatchMode);
            // No depth buffer: the first target has none, so none is bound (the passes neither test nor write depth).
            cb.SetRenderTarget(_aux, _aux[0]);
            cb.DrawMesh(FullScreenTriangle, Matrix4x4.identity, _base, 0, _passAuxBase);
            for (int i = 0; i < _seen.Count; i++)
            {
                PortalWindow w = _seen[i];
                SetIds(w);
                cb.DrawRenderer(w.Renderer, w.Material, 0, _passAuxMrt);
            }
            _idsFrame = Time.frameCount;
            CountDraws(_seen.Count);
            return _seen.Count;
        }

        /// <summary>
        /// Win.Occlusion without Win.CompositeMRT. Records into `cb` (same event as RecordAux) a clear of the id texture and
        /// one WINDOWID draw per window the camera may see that has a visibility id. Leaves the id texture bound. Returns
        /// the windows drawn, or -1 when not ReadyIds.
        /// </summary>
        public int RecordIds(CommandBuffer cb, Camera main, IList<PortalWindow> windows)
        {
            if (cb == null || main == null || windows == null || !ReadyIds || !Ensure(main.pixelWidth, main.pixelHeight, false))
                return -1;
            Collect(main, windows, true);
            cb.SetRenderTarget(_ids);
            cb.ClearRenderTarget(false, true, Color.clear);
            for (int i = 0; i < _seen.Count; i++)
            {
                PortalWindow w = _seen[i];
                SetIds(w);
                cb.DrawRenderer(w.Renderer, w.Material, 0, _passIds);
            }
            _idsFrame = Time.frameCount;
            CountDraws(_seen.Count);
            return _seen.Count;
        }

        /// <summary>
        /// Win.RestoreFromWindow. Records into `cb` (CameraEvent.BeforeForwardAlpha, the caller has bound CameraTarget) one
        /// RESTOREVIEW draw per window that shows a view this frame, the set RESTORE drew. Returns the draws, or -1 when
        /// not ReadyRestoreFromWindow.
        /// </summary>
        public int RecordRestoreView(CommandBuffer cb, IList<PortalWindow> windows)
        {
            if (cb == null || windows == null || !ReadyRestoreFromWindow)
                return -1;
            int n = 0;
            for (int i = 0; i < windows.Count; i++)
            {
                PortalWindow w = windows[i];
                if (!w.RenderedThisFrame)
                    continue;
                cb.DrawRenderer(w.Renderer, w.Material, 0, _passRestoreView);
                n++;
            }
            CountDraws(n);
            return n;
        }

        /// <summary>
        /// The targets for this frame at the game camera's size, made or remade now (all five for the MRT pass, else the id
        /// texture alone), so the composite region knows before it records which textures its buffers bind (they change
        /// identity on a resize). False when not Ready for that use or a target could not be made (then never again).
        /// </summary>
        internal bool Prepare(Camera main, bool all)
        {
            if (main == null || (all ? !Ready : !ReadyIds))
                return false;
            return Ensure(main.pixelWidth, main.pixelHeight, all);
        }

        /// <summary>Adds `n` window draws on the game camera to this frame's DrawsLastFrame (the composite region reports
        /// the G-buffer draw, MOTION, RESTORE, and the post-S1 passes it still records).</summary>
        public void CountDraws(int n)
        {
            int frame = Time.frameCount;
            if (_drawsFrame != frame)
            {
                _drawsFrame = frame;
                _draws = 0;
            }
            _draws += n;
        }

        // The windows the game camera may see this frame (in its frustum, enabled, with a partner: shown, fallback or
        // hidden by occlusion alike, so a hidden one is seen again), in draw order: those without a view first, which
        // in AUXMRT write only their id and the AUXBASE values again, then the shown ones in list order as the post-S1
        // passes drew them, so where two windows overlap on screen the depth targets end as they did. Without a
        // visibility id a window without a view has nothing to write; with idsOnly, neither has a shown one.
        void Collect(Camera main, IList<PortalWindow> windows, bool idsOnly)
        {
            _seen.Clear();
            GeometryUtility.CalculateFrustumPlanes(main, _planes);
            for (int i = 0; i < windows.Count; i++)
            {
                PortalWindow w = windows[i];
                if (!w.RenderedThisFrame && VisIdOf(w) != 0 && MaySee(w))
                    _seen.Add(w);
            }
            for (int i = 0; i < windows.Count; i++)
            {
                PortalWindow w = windows[i];
                if (w.RenderedThisFrame && w.Renderer != null && (!idsOnly || VisIdOf(w) != 0))
                    _seen.Add(w);
            }
        }

        bool MaySee(PortalWindow w) =>
            w.Enabled && w.Partner != null && w.Partner.Enabled && w.Renderer != null && w.Renderer.enabled
            && w.Root.activeInHierarchy && GeometryUtility.TestPlanesAABB(_planes, w.Renderer.bounds);

        static int VisIdOf(PortalWindow w) => w.State != null ? w.State.VisId : 0;

        // The window's id for the counts, and whether what it shows is a resolve output (bit 15 then marks the pixels the
        // resolve could not find in the cache). Read by AUXMRT/WINDOWID when the game camera renders.
        static void SetIds(PortalWindow w)
        {
            WindowState s = w.State;
            bool resolved = s != null && s.ResolvedFrame == Time.frameCount && w.Texture != null && w.Texture == s.ResolvedColor;
            w.Material.SetFloat(WindowShaderIds.VisId, s != null ? Mathf.Clamp(s.VisId, 0, 255) : 0);
            w.Material.SetFloat(WindowShaderIds.Resolved, resolved ? 1f : 0f);
        }

        // The targets at the game camera's pixel size (all five for MRT, else the id texture alone). Any that cannot be
        // made turns the composite off for good (logged once): the post-S1 passes carry on.
        bool Ensure(int width, int height, bool all)
        {
            if (_broken || width <= 0 || height <= 0)
                return false;
            if (width != _width || height != _height)
            {
                ReleaseTargets();
                _width = width;
                _height = height;
            }
            bool ok = true;
            if (_ids == null)
            {
                var desc = new RenderTextureDescriptor(width, height, GraphicsFormat.R16_UInt, 0) { msaaSamples = 1, useMipMap = false, autoGenerateMips = false };
                ok &= Make(ref _ids, new RenderTexture(desc), FilterMode.Point, "RealPortal window ids");
            }
            if (all && (_fog == null || _view == null || _sky == null || _mask == null))
            {
                // Made exactly as the post-S1 textures they stand for (same constructor, so the same colour-space
                // defaults; point-filtered depth, as ScreenDepth and the temporaries).
                if (_fog == null)
                    ok &= Make(ref _fog, new RenderTexture(width, height, 0, RenderTextureFormat.RFloat), FilterMode.Point, "RealPortal fog depth");
                if (_view == null)
                    ok &= Make(ref _view, new RenderTexture(width, height, 0, RenderTextureFormat.RFloat), FilterMode.Point, "RealPortal depth through windows");
                if (_sky == null)
                    ok &= Make(ref _sky, new RenderTexture(width, height, 0, RenderTextureFormat.RFloat), FilterMode.Point, "RealPortal window sky");
                // Bilinear as the post-S1 mask: MIX blends the window shafts in by it.
                if (_mask == null)
                    ok &= Make(ref _mask, new RenderTexture(width, height, 0, RenderTextureFormat.R8), FilterMode.Bilinear, "RealPortal window mask");
                if (ok)
                {
                    _aux[0] = _fog;
                    _aux[1] = _view;
                    _aux[2] = _sky;
                    _aux[3] = _mask;
                    _aux[4] = _ids;
                }
            }
            if (!ok)
            {
                _broken = true;
                ReleaseTargets();
                WindowLog.Warn("Window composite: its render targets could not be created; Win.CompositeMRT and Win.Occlusion stay on the post-S1 passes.");
            }
            return ok;
        }

        static bool Make(ref RenderTexture rt, RenderTexture made, FilterMode filter, string name)
        {
            rt = made;
            rt.name = name;
            rt.filterMode = filter;
            if (rt.Create())
                return true;
            PortalMath.Kill(rt);
            rt = null;
            return false;
        }

        void ReleaseTargets()
        {
            PortalMath.Kill(_fog);
            PortalMath.Kill(_view);
            PortalMath.Kill(_sky);
            PortalMath.Kill(_mask);
            PortalMath.Kill(_ids);
            _fog = _view = _sky = _mask = _ids = null;
            for (int i = 0; i < _aux.Length; i++)
                _aux[i] = default;
            _width = _height = 0;
            _idsFrame = -100;
        }

        public void Dispose()
        {
            ReleaseTargets();
            PortalMath.Kill(_base);
            _base = null;
            _mrtOk = _idsOk = _restoreOk = false;
            _seen.Clear();
        }
    }
}
