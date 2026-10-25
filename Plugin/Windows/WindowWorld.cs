using System.Collections.Generic;
using UnityEngine;

namespace RealPortal
{
    // OWNER: sched (S3), written by the integrator. What only the game side knows, for the scheduler (IWindowWorld in
    // WindowContracts.cs; Docs\S2S3-CONTRACTS.md §6). Called during Plan and RenderChosen for every window: nothing here
    // allocates or makes Unity objects, and the costlier answers (the far weather, the creatures in view) are kept for the
    // frame or a few frames.
    internal sealed partial class WindowWorld : IWindowWorld
    {
        readonly PortalSystem _system;
        readonly Plane[] _planes = new Plane[6];

        // A creature farther than this from the cache's camera moves too little on screen to count (and the far areas'
        // creatures are few within it).
        const float LifeRange = 60f;
        // Slower than this (m/s) it stands (idle animation is not what the age error is about).
        const float LifeSpeed = 0.25f;

        // TargetFrameMs: read again once a second (Screen.currentResolution is a native call); with vSync, the multiple of
        // the refresh interval the game achieves.
        float _targetMs = 16.7f, _targetAt = -1f;
        int _multiple = 1;

        // The far side's moving characters of this frame (position, read once per frame for all windows).
        Vector3[] _moving = new Vector3[64];
        int _movingCount, _movingFrame = -1;

        internal WindowWorld(PortalSystem system)
        {
            _system = system;
        }

        // Same key as PortalSystem.RenderGroupKey (PortalRenderSettings.GroupOf): the manager uses the renderer's GroupOf
        // when the plugin set one, which it always does; this is the same answer for anyone asking here.
        public int GroupOf(PortalWindow w) => _system.WindowGroupKey(w);

        // The window is drawn today whenever its link is live (PortalSystem.Live: both ends exist and the far area is
        // ready enough to go live, RemoteAreas.CanGoLive) and both openings are enabled; the manager already tests the
        // latter (PortalRenderer.PrepareWindow) and only live links' windows are in its list. So any window it asks about
        // can show: keeping today's visible set exactly (a stricter far-area test here would show the swirl where the
        // post-S1 renderer shows the view).
        public bool CanShow(PortalWindow w) => w != null && w.Partner != null && _system.IsLiveWindow(w);

        // The player's body (the reference point the plugin uses for crossing, PlayerRefHeight above the feet) at the
        // opening, or the game camera itself right at it (the camera arm reaching through, the near plane cutting the
        // window): the view through it must never be stale or hidden then.
        public bool Crossing(PortalWindow w)
        {
            if (w == null)
                return false;
            PortalFrame f = w.Frame;
            Player p = Player.m_localPlayer;
            if (p != null && Near(f, p.transform.position + Vector3.up * PortalSystem.PlayerRefHeight, 1.2f, 0.5f))
                return true;
            Camera main = _system.MainForWindows;
            return main != null && Near(f, main.transform.position, 1f, 0.5f);
        }

        // Within `depth` of the opening's plane and over the opening (with `margin` around its outline).
        static bool Near(PortalFrame f, Vector3 at, float depth, float margin)
        {
            if (Mathf.Abs(f.SignedDistance(at)) > depth)
                return false;
            Vector3 local = Quaternion.Inverse(f.Rotation) * (at - f.Center);
            return f.ContainsLocal(local, margin);
        }

        // Moving characters of the far side inside the frustum the cache was rendered with (its VP, far-side world), near
        // enough to that camera to move visibly. The manager asks only for windows in the frustum; the characters are
        // read once per frame (Moving), so a window costs a few tests against the moving ones, not native reads of every
        // loaded character (far areas load hundreds).
        public int LifeInView(WindowState s)
        {
            if (s == null || !s.Cache.Valid)
                return 0;
            int count = Moving();
            if (count == 0)
                return 0;
            WindowCache c = s.Cache;
            GeometryUtility.CalculateFrustumPlanes(c.VP, _planes);
            float range2 = LifeRange * LifeRange;
            int n = 0;
            for (int i = 0; i < count; i++)
            {
                Vector3 at = _moving[i];
                if ((at - c.Eye).sqrMagnitude > range2)
                    continue;
                if (!Inside(at + Vector3.up, 1.5f))
                    continue;
                n++;
            }
            return n;
        }

        // This frame's characters moving faster than LifeSpeed, into _moving (Character's own list, no allocation).
        int Moving()
        {
            int frame = Time.frameCount;
            if (_movingFrame == frame)
                return _movingCount;
            _movingFrame = frame;
            _movingCount = 0;
            List<Character> all = Character.GetAllCharacters();
            if (all == null)
                return 0;
            for (int i = 0; i < all.Count; i++)
            {
                Character ch = all[i];
                if ((object)ch == null || ch == null)
                    continue;
                if (ch.GetVelocity().sqrMagnitude < LifeSpeed * LifeSpeed)
                    continue;
                if (_movingCount == _moving.Length)
                    System.Array.Resize(ref _moving, _moving.Length * 2);
                _moving[_movingCount++] = ch.transform.position;
            }
            return _movingCount;
        }

        bool Inside(Vector3 p, float radius)
        {
            for (int i = 0; i < _planes.Length; i++)
                if (_planes[i].GetDistanceToPoint(p) < -radius)
                    return false;
            return true;
        }

        // The weather the window shows (that place's with FarWeather, else the player's own) has precipitation or other
        // weather particles: they move in the view whatever the camera does (E_time, the dynamic cap).
        public bool WeatherActive(PortalWindow w)
        {
            if (w == null || w.Partner == null)
                return false;
            EnvSetup env = null;
            Camera main = _system.MainForWindows;
            if (Plugin.FarWeather.Value && main != null)
            {
                Vector3 eye = main.transform.position;
                Vector3 camPos = PortalMath.View(w.Frame, w.Partner.Frame, eye, out _).MultiplyPoint3x4(eye);
                env = FarEnvironment.Pick(camPos);
            }
            if (env == null)
                env = EnvMan.instance != null ? EnvMan.instance.GetCurrentEnvironment() : null;
            return env != null && (env.m_isWet || (env.m_psystems != null && env.m_psystems.Length > 0));
        }

        // ZoneSystem.instance.m_waterLevel (30 in the vanilla world).
        public float WaterLevel => ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;

        // The game's frame pacing (PresentManager): with vSync the display rate over the vSync count, else its frame
        // limit (Application.targetFrameRate), else uncapped (16.7 ms, ARCHITECTURE-v2 §4.6). Never a target the game
        // demonstrably misses: B_cpu would sit at its minimum (less than one render) and every frame would read as slow to
        // the GPU controller. What a frame needs is the main thread's frame without the window renders (the cost model's
        // EMA, the presentation wait left out) plus the least the windows get (CpuMinMs) and the margin. With vSync the
        // game presents at a whole multiple of the refresh interval, so the target is the smallest multiple that holds that
        // (a 144 Hz display under a game making 70-90 fps: 13.9 ms, not 6.9 ms); it steps back down only with 10 % to spare.
        // A frame limit the game cannot reach leaves it uncapped: 16.7 ms, or the limit if that is longer.
        public float TargetFrameMs
        {
            get
            {
                float now = Time.unscaledTime;
                if (now < _targetAt)
                    return _targetMs;
                _targetAt = now + 1f;
                float interval = 1000f / 60f;
                bool vsync = false, limited = false;
                int count = QualitySettings.vSyncCount;
                if (count > 0)
                {
                    double hz = Screen.currentResolution.refreshRateRatio.value;
                    if (hz > 1.0)
                    {
                        interval = (float)(1000.0 * count / hz);
                        vsync = true;
                    }
                }
                else if (Application.targetFrameRate > 0)
                {
                    interval = 1000f / Application.targetFrameRate;
                    limited = true;
                }
                interval = Mathf.Clamp(interval, 2f, 100f);
                float ms = interval;
                float need = NeedMs();
                if (vsync)
                {
                    int k = need > 0f ? Mathf.Max(1, Mathf.CeilToInt(need / interval - 1e-3f)) : 1;
                    if (k < _multiple && need > (_multiple - 1) * interval * 0.9f)
                        k = _multiple;
                    _multiple = Mathf.Clamp(k, 1, Mathf.Max(1, (int)(100f / interval)));
                    ms = interval * _multiple;
                }
                else
                {
                    _multiple = 1;
                    if (limited && need > interval * 1.05f)
                        ms = Mathf.Max(interval, 1000f / 60f);
                }
                _targetMs = Mathf.Clamp(ms, 2f, 100f);
                return _targetMs;
            }
        }

        // The least frame (ms) the game makes with the window renders at their minimum budget; -1 before anything was
        // measured (the cost model's EMA of the frame without the window renders, fed at every Plan).
        float NeedMs()
        {
            IWindowManager m = _system.Manager;
            if (m == null || !(m.Cost is WindowCostModel cost) || cost.RestEmaMs < 0f)
                return -1f;
            WindowPipelineSettings st = m.Settings;
            return cost.RestEmaMs + st.CpuMarginMs + st.CpuMinMs;
        }
    }
}
