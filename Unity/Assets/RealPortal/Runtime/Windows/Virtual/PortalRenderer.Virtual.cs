using System;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering;

namespace RealPortal
{
    // Win.Virtual (FARWORLD-V3 §4.4, the V1 spike), the renderer's side: a top-level window render (RenderShown) of a window
    // the game side picks (ScreenPick) is drawn by the WindowScreen instead of a camera, at its turn in the frame's window
    // batch (moved last in RenderAll's batch: after the other windows), between the same BeforeRender/AfterRender as a slot
    // render, and handed over exactly as RenderLevel hands a slot render over: a colour texture covering the rect, its linear
    // eye depth (Point, the view depth the composite's MOTION, AUXMRT and DEPTHPATCH passes read) and its G-buffer normals,
    // owned by the window until its next render, so motion vectors, TAA, the fog copy, the sky depth and everything after
    // run unchanged. A window it cannot take this frame (a scaled view, the camera at the exit plane, LDR, a resolution
    // scale, no content), and any exception, renders through RenderLevel (its slot camera) that same frame.
    // Off (the switch, or no content or pick from the game side): RenderShown is RenderLevel, exactly as before.
    public sealed partial class PortalRenderer
    {
        /// <summary>Win.Virtual: what the game side draws a window's far side with (null: no virtual renders).</summary>
        public IWindowScreenContent ScreenContent;
        /// <summary>Win.Virtual: whether this window may be drawn without a camera this frame (null: none).</summary>
        public Func<PortalWindow, bool> ScreenPick;
        WindowScreen _screen;
        Shader _screenShader;
        bool _screenLooked;
        // The view's planes (VirtualView's arrays, reused).
        readonly Plane[] _virtualPlanes = new Plane[6], _virtualFull = new Plane[6];

        // Lab (internal static, Win.Virtual): renders done without a camera, windows declined (rendered by their slot
        // camera instead: the last reason), exceptions (also rendered by their slot camera that frame; the last message),
        // main-thread ms of the last virtual render (all of it, BeforeRender to AfterRender included; the recording plus
        // Graphics.ExecuteCommandBuffer alone; the content's cull and listing, IWindowScreenContent.Begin) and the sums of
        // the first two, draws recorded by the last one and all of them.
        internal static long LabVirtualRenders, LabVirtualDeclined, LabVirtualErrors;
        internal static string LabVirtualWhy = "", LabVirtualError = "";
        internal static double LabVirtualMs, LabVirtualCbMs, LabVirtualBeginMs, LabVirtualMsSum, LabVirtualCbMsSum;
        internal static int LabVirtualDraws;
        internal static long LabVirtualDrawsSum;

        /// <summary>Lab: one line of the virtual path's counters.</summary>
        internal static string DescribeVirtual() =>
            $"virtual windows: {LabVirtualRenders} renders, {LabVirtualDeclined} declined ({LabVirtualWhy}), {LabVirtualErrors} errors ({LabVirtualError}), " +
            $"last {LabVirtualMs:0.000} ms (cull {LabVirtualBeginMs:0.000} ms, buffer {LabVirtualCbMs:0.000} ms, {LabVirtualDraws} draws), mean {(LabVirtualRenders > 0 ? LabVirtualMsSum / LabVirtualRenders : 0):0.000} ms " +
            $"(buffer {(LabVirtualRenders > 0 ? LabVirtualCbMsSum / LabVirtualRenders : 0):0.000} ms, {(LabVirtualRenders > 0 ? (double)LabVirtualDrawsSum / LabVirtualRenders : 0):0.0} draws); " +
            WindowScreenShadows.Describe();

        /// <summary>Where the window screen's shader comes from (realportal.bundle, loaded at the first virtual render; null:
        /// Shader.Find).</summary>
        public Func<Shader> ScreenShaderLoader;

        bool VirtualPicks(PortalWindow w) => WindowFlags.Virtual && ScreenContent != null && ScreenPick != null && ScreenPick(w);

        // The level-0 render of a shown window: without a camera when picked and possible, else RenderLevel.
        RenderTexture RenderTopLevel(PortalWindow w, Rect rect, out Rect texRect)
        {
            if (VirtualPicks(w))
            {
                RenderTexture tex = RenderVirtual(w, rect, out texRect);
                if (tex != null)
                    return tex;
            }
            return RenderLevel(w, _frameEye, _frameRot, rect, 0, null, out texRect);
        }

        // RenderAll: the windows drawn without a camera go last in the batch (their group order otherwise kept).
        void VirtualLast()
        {
            if (!WindowFlags.Virtual || ScreenContent == null || ScreenPick == null)
                return;
            int n = _due.Count, moved = 0;
            for (int i = 0; i < n - moved; i++)
            {
                var d = _due[i];
                if (!ScreenPick(d.w))
                    continue;
                _due.RemoveAt(i);
                _due.Add(d);
                moved++;
                i--;
            }
        }

        // The window screen's frame start: its targets go while the switch is off.
        void VirtualFrame()
        {
            if (!WindowFlags.Virtual && _screen != null && _screen.HasTargets)
                _screen.ReleaseTargets();
        }

        /// <summary>Win.Virtual: the game camera rendered (its onPostRender), lit by `sun`, its sun shadow map `res` px by
        /// the rule: its sun cascades are read for the virtual views (WindowScreenShadows.Capture).</summary>
        public void VirtualMainRendered(Camera main, Light sun, int res)
        {
            if (!WindowFlags.Virtual || _screen == null)
                return;
            try
            {
                _screen.Shadows.Capture(main, sun, res);
            }
            catch (Exception e)
            {
                WindowLog.WarnEvery("PortalRenderer.VirtualCapture", 10f, "Win.Virtual: reading the game camera's sun cascades failed: " + e.Message);
            }
        }

        bool EnsureScreen()
        {
            if (_screen != null)
                return _screen.Ready;
            if (_screenLooked)
                return false;
            _screenLooked = true;
            try
            {
                _screenShader = ScreenShaderLoader?.Invoke();
            }
            catch (Exception e)
            {
                WindowLog.Warn("Win.Virtual: loading the WindowScreen shader failed (" + e.Message + "); windows render through their cameras.");
            }
            Shader shader = _screenShader != null ? _screenShader : Shader.Find("Hidden/RealPortal/WindowScreen");
            if (shader == null)
            {
                WindowLog.Warn("Win.Virtual: no WindowScreen shader (a realportal.bundle built before it); windows render through their cameras.");
                return false;
            }
            _screen = new WindowScreen(shader);
            if (!_screen.Ready)
                WindowLog.Warn("Win.Virtual: the WindowScreen shader is missing passes or unsupported; windows render through their cameras.");
            return _screen.Ready;
        }

        void Decline(string why)
        {
            LabVirtualDeclined++;
            LabVirtualWhy = why;
        }

        // A top-level render of `w` without a camera, or null (declined or failed: RenderLevel renders it). As RenderLevel
        // for a level-0 render: the rect snapped to pixels, the view recorded for the grass and particles (Frusta,
        // ViewFrustum), the cache's camera fields filled, BeforeRender/AfterRender around it; then the textures for
        // RenderShown to own (the colour returned, _lastDepth, _lastNormal).
        RenderTexture RenderVirtual(PortalWindow w, Rect rect, out Rect texRect)
        {
            texRect = PortalMath.SnapToPixels(rect, _texW, _texH);
            _lastDepth = _lastNormal = null;
            if (texRect.width <= 0f || texRect.height <= 0f)
                return null;
            Camera main = _main;
            if (!EnsureScreen())
            {
                Decline("no WindowScreen shader");
                return null;
            }
            if (main == null || !main.allowHDR || main.actualRenderingPath != RenderingPath.DeferredShading)
            {
                Decline("the game camera is not HDR deferred");
                return null;
            }
            if (_texW != main.pixelWidth || _texH != main.pixelHeight)
            {
                Decline("a window resolution scale (Window.Resolution)");
                return null;
            }
            if (Settings.FarClip < main.farClipPlane)
            {
                Decline("a window far clip (Window.FarClip)");
                return null;
            }
            PortalFrame exit = w.Partner.Frame;
            Matrix4x4 m = PortalMath.View(w.Frame, exit, _frameEye, out float viewScale);
            if (Mathf.Abs(viewScale - 1f) > 1e-5f)
            {
                Decline("a scaled view (an exit smaller than the entry)");
                return null;
            }
            Vector3 camPos = m.MultiplyPoint3x4(_frameEye);
            Quaternion camRot = PortalMath.Rotation(m) * _frameRot;
            float near = main.nearClipPlane, far = main.farClipPlane;
            float side = exit.SignedDistance(camPos);
            // The oblique near plane needs the exit plane in front of the near plane (the slot camera handles the crossing).
            if (Mathf.Abs(side) <= near + 0.05f)
            {
                Decline("the eye at the exit plane");
                return null;
            }

            long t0 = Stopwatch.GetTimestamp();
            // A camera at C' (Camera.worldToCameraMatrix's convention: -z forward).
            Matrix4x4 view = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * Matrix4x4.TRS(camPos, camRot, Vector3.one).inverse;
            Matrix4x4 proj = main.nonJitteredProjectionMatrix;
            proj.m02 += 2f * Settings.Jitter.x;
            proj.m12 += 2f * Settings.Jitter.y;
            Vector3 shown = side > 0f ? -exit.Forward : exit.Forward;
            Matrix4x4 oblique = PortalMath.Oblique(proj, PortalMath.CameraSpacePlane(view, exit.Center, shown, 0.01f));
            Matrix4x4 rectVP = PortalMath.Scissor(texRect) * proj * view;
            GeometryUtility.CalculateFrustumPlanes(rectVP, _virtualPlanes);
            GeometryUtility.CalculateFrustumPlanes(proj * view, _virtualFull);
            WindowManager pipe = Pipeline;
            WindowState s = pipe != null ? WindowState.For(w) : null;
            var v = new VirtualView
            {
                Window = w,
                Exit = exit,
                Eye = camPos,
                Rotation = camRot,
                Forward = camRot * Vector3.forward,
                Pair = m,
                View = view,
                Proj = proj,
                Oblique = oblique,
                Rect = texRect,
                Width = _texW,
                Height = _texH,
                Near = near,
                Far = far,
                FieldOfView = main.fieldOfView,
                CullingMask = main.cullingMask,
                ViewPlanes = _virtualPlanes,
                FullPlanes = _virtualFull,
                ExitPlane = new Vector4(shown.x, shown.y, shown.z, -Vector3.Dot(shown, exit.Center)),
                ShadowDistance = QualitySettings.shadowDistance,
                HasState = s != null,
            };

            // The textures the composite reads, as RenderLevel makes a level-0 render's (pooled size classes).
            int tw = Mathf.Max(1, Mathf.RoundToInt(texRect.width * _texW)), th = Mathf.Max(1, Mathf.RoundToInt(texRect.height * _texH));
            RenderTexture color = Temp(tw, th);
            RenderTexture depth = Temp(tw, th, RenderTextureFormat.RFloat);
            RenderTexture normal = Temp(tw, th, RenderTextureFormat.ARGB2101010);
            // Win.PointDepth's texel fetches read it either way; without the switch the slot path's depth is bilinear.
            depth.filterMode = WindowFlags.PointDepth ? FilterMode.Point : FilterMode.Bilinear;

            // The view for the grass and the far particles, as an on-screen render records it.
            Plane[] f = FrustumOf(rectVP);
            Frusta.Add(f);
            ViewPositions.Add(camPos);
            Array.Copy(f, w.ViewFrustum, 6);
            w.ViewPos = camPos;
            w.HasView = true;
            w.DepthScale = 1f;
            if (s != null)
            {
                s.Slot = -1;
                FillVirtualCache(s, w, m, camPos, camRot, texRect, color, rectVP, near, far);
            }

            bool ok = false, beforeCalled = false, began = false;
            long c0 = 0, c1 = 0;
            string why = null;
            try
            {
                Shader.SetGlobalVector(s_viewport, new Vector4(texRect.x, texRect.y, texRect.width, texRect.height));
                beforeCalled = true;
                BeforeRender?.Invoke(w, m, camPos);
                // As RenderLevel: set after BeforeRender, for what runs during the render.
                InWindowRender = true;
                WindowRenderLevel = 0;
                began = true;
                long b0 = Stopwatch.GetTimestamp();
                bool content = ScreenContent.Begin(v, out why);
                LabVirtualBeginMs = (Stopwatch.GetTimestamp() - b0) * s_camTickMs;
                if (!content)
                    why = why ?? "the content declined";
                else
                {
                    c0 = Stopwatch.GetTimestamp();
                    ok = _screen.Render(v, ScreenContent, color, depth, normal, out why);
                    c1 = Stopwatch.GetTimestamp();
                }
            }
            catch (Exception e)
            {
                ok = false;
                LabVirtualErrors++;
                LabVirtualError = e.GetType().Name + ": " + e.Message;
                WindowLog.WarnEvery("PortalRenderer.Virtual", 10f, "Win.Virtual: a window drawn without a camera failed (its slot camera renders it): " + e);
            }
            finally
            {
                if (began)
                {
                    try
                    {
                        ScreenContent.End();
                    }
                    catch (Exception e)
                    {
                        WindowLog.WarnEvery("PortalRenderer.VirtualEnd", 10f, "Win.Virtual: the content's End failed: " + e.Message);
                    }
                }
                InWindowRender = false;
                if (beforeCalled)
                    AfterRender?.Invoke(w, m, camPos);
                Shader.SetGlobalVector(s_viewport, FullViewport);
            }
            if (!ok)
            {
                if (why != null)
                    Decline(why);
                // The view it recorded is the slot render's to record again.
                Frusta.RemoveAt(Frusta.Count - 1);
                ViewPositions.RemoveAt(ViewPositions.Count - 1);
                if (s != null)
                {
                    s.Cache.Frame = -100;
                    s.Invalid |= WindowInvalid.NoCache;
                }
                return null;
            }
            double ms = (Stopwatch.GetTimestamp() - t0) * s_camTickMs, cbMs = (c1 - c0) * s_camTickMs;
            LabVirtualRenders++;
            LabVirtualMs = ms;
            LabVirtualCbMs = cbMs;
            LabVirtualMsSum += ms;
            LabVirtualCbMsSum += cbMs;
            LabVirtualDraws = _screen.LastDraws;
            LabVirtualDrawsSum += _screen.LastDraws;
            RendersLastFrame++;
            _lastDepth = depth;
            _lastNormal = normal;
            return color;
        }

        Plane[] FrustumOf(in Matrix4x4 vp)
        {
            int i = Frusta.Count;
            while (_frustumPool.Count <= i)
                _frustumPool.Add(new Plane[6]);
            GeometryUtility.CalculateFrustumPlanes(vp, _frustumPool[i]);
            return _frustumPool[i];
        }

        // FillCache for a render without a camera: the view-projection that addresses the rect's textures, C' and its forward,
        // the game camera's near and far planes.
        void FillVirtualCache(WindowState s, PortalWindow w, in Matrix4x4 m, Vector3 camPos, Quaternion camRot, Rect rect, RenderTexture target,
                              in Matrix4x4 rectVP, float near, float far)
        {
            WindowCache c = s.Cache;
            TierParams p = s.ParamsNow(Time.frameCount);
            c.VP = rectVP;
            c.MainVP = _frameVP;
            c.Pair = m;
            c.PairBase = PortalMath.Pair(w.Frame, w.Partner.Frame);
            c.ViewScale = 1f;
            c.Eye = camPos;
            c.Forward = camRot * Vector3.forward;
            c.Near = near;
            c.Far = far;
            c.Rect = rect;
            c.Width = target.width;
            c.Height = target.height;
            c.Frame = Time.frameCount;
            c.Time = Time.unscaledTime;
            c.RectPx = s.RectPx > 0 ? s.RectPx : Mathf.RoundToInt(rect.width * rect.height * _main.pixelWidth * _main.pixelHeight);
            c.Tier = p.Tier;
            c.Slot = -1;
            c.ZNear = near;
            c.ZFar = far;
            c.StatsKnown = false;
            if (c.StatsFrame != c.Frame)
                c.StatsIndex = -1;
            c.Ao = p.Ao;
            c.PointLightShadows = p.PointLightShadows;
            c.ShadowDistanceCap = p.ShadowDistanceCap;
            c.ShadowCascadesCap = p.ShadowCascadesCap;
        }

        // Destroy: the window screen's targets and materials.
        void DestroyVirtual()
        {
            _screen?.Dispose();
            _screen = null;
            _screenLooked = false;
        }
    }
}
