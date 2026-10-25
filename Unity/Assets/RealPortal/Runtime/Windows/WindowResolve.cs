using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace RealPortal
{
    // OWNER: gpu (S3). Depth reprojection of stale windows (§4.7) with Shaders\WindowResolve.shader: one draw per stale
    // window over its current rect, 12-step screen-space march against the cache's linear depth, 3 bisection steps,
    // disocclusion = far end (alpha 0). Outputs from WindowRTPool, owned for one frame (BeginFrame gives them back).
    // Contract: IWindowResolve in WindowContracts.cs; shader inputs: Docs\S2S3-CONTRACTS.md §8.2.
    // Ready = false (also for an old bundle without WindowResolve.shader, or a shader without its RESOLVE pass): stale
    // windows keep the flat reprojection (_RP_Reproject).
    //
    // The C# side. Every input of §8.2 goes into a MaterialPropertyBlock (it overrides whatever the shader declares, as
    // property or global), then one command buffer per window, executed right away (Graphics.ExecuteCommandBuffer at
    // RenderChosen, before the game camera culls): SetRenderTarget(MRT: colour, depth[, normal]) and DrawMesh of a
    // clip-space full-screen triangle, vertices (-1,-1,0), (-1,3,0), (3,-1,0), with identity matrices, as PostProcessing
    // v2 draws its full-screen passes: the vertex shader writes float4(v.vertex.xy, 0, 1) and takes its uv from
    // v.vertex.xy * 0.5 + 0.5 (flipped in y where UNITY_UV_STARTS_AT_TOP). The pass always writes three targets, so three
    // are always bound (a fragment output without a target is not defined on every API); without a cache normal the third
    // is a scratch texture that goes back to the pool with the others and is not shown (ResolvedNormal stays null).
    // The output covers the window's opening rect now (its pixels are all the composite samples), in 32 px classes of the
    // window texture size, at the tier's resolution.
    public sealed partial class WindowResolve : IWindowResolve
    {
        readonly WindowPipelineSettings _settings;
        readonly Shader _shader;
        readonly Material _mat;
        readonly bool _ok;
        bool _disposed;
        readonly MaterialPropertyBlock _props = new MaterialPropertyBlock();
        readonly CommandBuffer _cb = new CommandBuffer { name = "RealPortal window resolve" };
        readonly RenderTargetIdentifier[] _mrt3 = new RenderTargetIdentifier[3];
        // This frame's outputs (back to WindowRTPool at the next BeginFrame) and the states that hold them.
        readonly List<RenderTexture> _out = new List<RenderTexture>(16);
        readonly List<WindowState> _resolved = new List<WindowState>(16);
        static Mesh s_triangle;
        int _count, _countFrame = -100;
        bool _failedLogged;

        /// <summary>PortalRenderSettings.Resolution (the manager sets it each frame): the window textures are this × the screen.</summary>
        internal float Resolution = 1f;

        public WindowResolve(WindowPipelineSettings settings, Shader shader)
        {
            _settings = settings ?? new WindowPipelineSettings();
            _shader = shader;
            try
            {
                if (shader != null && shader.isSupported && SystemInfo.supportedRenderTargetCount >= 3)
                {
                    _mat = new Material(shader) { name = "RealPortalResolve", hideFlags = HideFlags.HideAndDontSave };
                    _ok = _mat.FindPass(WindowPasses.ResolvePassName) >= 0;
                    if (!_ok)
                        WindowLog.Warn("WindowResolve: the shader has no RESOLVE pass (Win.Resolve stays off).");
                }
            }
            catch (Exception e)
            {
                _ok = false;
                WindowLog.Warn("WindowResolve: shader unusable (Win.Resolve stays off): " + e.Message);
            }
        }

        public bool Ready => _ok && !_disposed && _mat != null;
        public int ResolvesLastFrame => _countFrame >= Time.frameCount - 1 ? _count : 0;

        static Mesh Triangle()
        {
            if (s_triangle != null)
                return s_triangle;
            s_triangle = new Mesh { name = "RealPortalFullScreenTriangle", hideFlags = HideFlags.HideAndDontSave };
            s_triangle.vertices = new[] { new Vector3(-1f, -1f, 0f), new Vector3(-1f, 3f, 0f), new Vector3(3f, -1f, 0f) };
            s_triangle.triangles = new[] { 0, 1, 2 };
            s_triangle.bounds = new Bounds(Vector3.zero, Vector3.one * 1e5f);
            s_triangle.UploadMeshData(true);
            return s_triangle;
        }

        public void BeginFrame()
        {
            for (int i = 0; i < _resolved.Count; i++)
            {
                WindowState s = _resolved[i];
                s.ResolvedColor = s.ResolvedDepth = s.ResolvedNormal = null;
            }
            _resolved.Clear();
            for (int i = 0; i < _out.Count; i++)
                WindowRTPool.Return(_out[i]);
            _out.Clear();
            if (_countFrame != Time.frameCount)
            {
                _countFrame = Time.frameCount;
                _count = 0;
            }
        }

        static int Ceil16(float px) => Mathf.Max(16, (Mathf.CeilToInt(px - 1e-3f) + 15) / 16 * 16);

        public bool Resolve(WindowState s, in WindowFrameContext ctx)
        {
            if (!Ready || s == null || !s.Cache.Valid || ctx.Main == null)
                return false;
            PortalWindow w = s.Window;
            if (w == null || w.Partner == null)
                return false;
            WindowCache c = s.Cache;
            Camera main = ctx.Main;
            int texW = Mathf.Max(16, Mathf.RoundToInt(main.pixelWidth * Resolution));
            int texH = Mathf.Max(16, Mathf.RoundToInt(main.pixelHeight * Resolution));
            Rect open = s.OpeningRect.width > 0f && s.OpeningRect.height > 0f ? s.OpeningRect : s.Rect;
            Rect r = PortalRenderer.QuantizeOnScreen(open, texW, texH, 32);
            if (r.width <= 0f || r.height <= 0f)
                return false;
            float scale = s.ParamsFrame == ctx.Frame ? Mathf.Clamp(s.Params.ResolutionScale, 0.1f, 1f) : 1f;
            int ow = Mathf.Approximately(scale, 1f) ? Mathf.Max(1, Mathf.RoundToInt(r.width * texW)) : Ceil16(r.width * texW * scale);
            int oh = Mathf.Approximately(scale, 1f) ? Mathf.Max(1, Mathf.RoundToInt(r.height * texH)) : Ceil16(r.height * texH * scale);

            RenderTexture color = null, depth = null, normal = null;
            bool hasNormal = false;
            try
            {
                color = WindowRTPool.Get(ow, oh, 0, main.allowHDR ? RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default);
                depth = WindowRTPool.Get(ow, oh, 0, RenderTextureFormat.RFloat);
                // Win.PointDepth: the resolved view depth is read by the composite passes texel by texel, as a fresh one
                // (the pool's RFloat textures may come back from a fresh render either way, so it is set both ways).
                depth.filterMode = WindowFlags.PointDepth ? FilterMode.Point : FilterMode.Bilinear;
                hasNormal = c.Normal != null && c.Normal.IsCreated();
                normal = WindowRTPool.Get(ow, oh, 0, RenderTextureFormat.ARGB2101010);

                // The main camera's ray now (with this frame's jitter, as the fresh renders have it), carried to the far side.
                PortalFrame exit = w.Partner.Frame;
                Matrix4x4 m = PortalMath.View(w.Frame, exit, ctx.Eye, out _);
                Matrix4x4 pj = main.nonJitteredProjectionMatrix;
                var proj = new Vector4(pj.m00, pj.m11, pj.m02 + 2f * ctx.Jitter.x, pj.m12 + 2f * ctx.Jitter.y);
                Vector3 eye = m.MultiplyPoint3x4(ctx.Eye);
                Vector3 fwd = m.MultiplyVector(ctx.Forward).normalized;
                Vector3 n = exit.Forward;

                _props.Clear();
                _props.SetTexture(WindowShaderIds.CacheColor, c.Color);
                _props.SetTexture(WindowShaderIds.CacheDepth, c.Depth);
                if (hasNormal)
                    _props.SetTexture(WindowShaderIds.CacheNormal, c.Normal);
                _props.SetFloat(WindowShaderIds.HasCacheNormal, hasNormal ? 1f : 0f);
                _props.SetMatrix(WindowShaderIds.CacheVP, c.VP);
                _props.SetVector(WindowShaderIds.CacheEye, c.Eye);
                _props.SetVector(WindowShaderIds.CacheFwd, c.Forward);
                _props.SetVector(WindowShaderIds.CacheZ, new Vector4(c.ZNear, c.ZFar, c.Near, c.Far));
                _props.SetMatrix(WindowShaderIds.PairMatrix, m);
                _props.SetMatrix(WindowShaderIds.MainCamToWorld, main.cameraToWorldMatrix);
                _props.SetVector(WindowShaderIds.MainProj, proj);
                _props.SetVector(WindowShaderIds.ResolveRect, new Vector4(r.x, r.y, r.width, r.height));
                _props.SetVector(WindowShaderIds.ResolveEye, eye);
                _props.SetVector(WindowShaderIds.ResolveFwd, fwd);
                _props.SetVector(WindowShaderIds.ExitPlane, new Vector4(n.x, n.y, n.z, -Vector3.Dot(n, exit.Center)));
                _props.SetVector(WindowShaderIds.ResolveParams,
                    new Vector4(_settings.ResolveSteps, _settings.ResolveRefine, _settings.ResolveThickness, 0.98f * c.Far));

                _cb.Clear();
                _mrt3[0] = color;
                _mrt3[1] = depth;
                _mrt3[2] = normal;
                // No depth buffer: the colour target has none, so none is bound (the pass neither tests nor writes depth).
                _cb.SetRenderTarget(_mrt3, color);
                _cb.DrawMesh(Triangle(), Matrix4x4.identity, _mat, 0, WindowPasses.ResolvePass, _props);
                Graphics.ExecuteCommandBuffer(_cb);
            }
            catch (Exception e)
            {
                WindowRTPool.Return(color);
                WindowRTPool.Return(depth);
                WindowRTPool.Return(normal);
                if (!_failedLogged)
                {
                    _failedLogged = true;
                    WindowLog.Warn("WindowResolve failed (the window shows its flat reprojection): " + e);
                }
                return false;
            }
            _out.Add(color);
            _out.Add(depth);
            _out.Add(normal);
            s.ResolvedColor = color;
            s.ResolvedDepth = depth;
            // Without cache normals the window keeps its face normal (as the cache would show it): the scratch target is
            // not bound to the window.
            s.ResolvedNormal = hasNormal ? normal : null;
            s.ResolvedRect = r;
            s.ResolvedFrame = ctx.Frame;
            _resolved.Add(s);
            if (_countFrame != ctx.Frame)
            {
                _countFrame = ctx.Frame;
                _count = 0;
            }
            _count++;
            return true;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            BeginFrame();
            _disposed = true;
            _cb.Release();
            if (_mat != null)
                PortalMath.Kill(_mat);
        }
    }
}
