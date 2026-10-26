using System;
using System.Collections.Generic;
using UnityEngine;

namespace RealPortal
{
    // Win.Virtual (FARWORLD-V3 §4.4, the V1 spike) on the game side: the renderer's content (WindowScreenContent), which
    // windows it may draw without a camera (LabVirtualWindow), the WindowScreen shader from realportal.bundle, the game
    // camera's sun cascades read after each of its renders (WindowScreenShadows.Capture), and the far side's reflection
    // probe. With the switch off nothing here runs but the wiring at start-up.
    public sealed partial class PortalSystem
    {
        // Lab (Win.Virtual): -1, every window the virtual path can take is drawn without a camera; else only the window at
        // this index of this frame's live windows (the list the renderer gets).
        internal static int LabVirtualWindow = -1;

        WindowScreenContent _screenContent;
        readonly List<ReflectionProbe> _virtualProbes = new List<ReflectionProbe>(8);

        // From InitWindowPipeline, after the renderer exists. The shader is loaded at the first virtual render only.
        void InitWindowScreen()
        {
            _screenContent = new WindowScreenContent(this);
            _renderer.ScreenContent = _screenContent;
            _renderer.ScreenPick = VirtualPick;
            _renderer.ScreenShaderLoader = () => Plugin.Bundle != null ? Plugin.Bundle.LoadAsset<Shader>("WindowScreen") : null;
        }

        bool VirtualPick(PortalWindow w)
        {
            int at = LabVirtualWindow;
            if (at < 0)
                return true;
            return at < _windows.Count && ReferenceEquals(_windows[at], w);
        }

        // From MainPostRender (the game camera rendered): its sun cascades, for the virtual views of the next frame.
        void CaptureForVirtual(Camera cam)
        {
            if (!WindowFlags.Virtual || cam == null)
                return;
            Light sun = EnvMan.instance != null ? EnvMan.instance.m_dirLight : null;
            _renderer.VirtualMainRendered(cam, sun, VirtualShadowRes(sun));
        }

        // The size of the sun's shadow map the game camera gets (Win.ShadowRes's rule; the game's own custom resolution also
        // while a window render holds another on the light).
        internal int VirtualShadowRes(Light sun) =>
            sun == null ? 0 : GameShadowRes(sun, _shadowResLight == sun ? _shadowResWas : sun.shadowCustomResolution);

        // The reflection probe at a far point (C'): of the far areas' probes and the game's own pair, those whose box holds
        // it, the most important, then the smallest (the one Unity's deferred reflections draw last there).
        internal ReflectionProbe VirtualProbeAt(Vector3 at)
        {
            _virtualProbes.Clear();
            _reflections.ProbesInto(_virtualProbes);
            ReflectionUpdate game = ReflectionUpdate.instance;
            if (game != null)
            {
                if (game.m_probe1 != null)
                    _virtualProbes.Add(game.m_probe1);
                if (game.m_probe2 != null)
                    _virtualProbes.Add(game.m_probe2);
            }
            ReflectionProbe best = null;
            float bestVolume = float.MaxValue;
            for (int i = 0; i < _virtualProbes.Count; i++)
            {
                ReflectionProbe p = _virtualProbes[i];
                if (p == null || !p.isActiveAndEnabled || p.texture == null)
                    continue;
                Bounds b = p.bounds;
                if (!b.Contains(at))
                    continue;
                float volume = b.size.x * b.size.y * b.size.z;
                if (best == null || p.importance > best.importance || (p.importance == best.importance && volume < bestVolume))
                {
                    best = p;
                    bestVolume = volume;
                }
            }
            _virtualProbes.Clear();
            return best;
        }

        // Lab: the virtual path's counters in one line.
        internal static string DescribeVirtual() =>
            PortalRenderer.DescribeVirtual() + "; " + WindowScreen.DescribeProf() + "; " + ProxyDraw.DescribeProf() + BenchLine();

        static string BenchLine() =>
            $"; content: {WindowScreenContent.LabRenders} renders, last {WindowScreenContent.LabMaps} heightmaps and {WindowScreenContent.LabCore} core renderers, " +
            $"{WindowScreenContent.LabCoreObjects} core objects listed, {WindowScreenContent.LabSkipped} materials left out; proxies: {ProxyDraw.LabVirtualViews} views, " +
            $"{ProxyDraw.LabVirtualBodyDraws} G-buffer and {ProxyDraw.LabVirtualCasterDraws} caster draws, {ProxyDraw.LabVirtualNoPass} runs without the pass";
    }
}
