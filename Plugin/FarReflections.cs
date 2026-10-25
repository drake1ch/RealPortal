using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace RealPortal
{
    // What water, wet stone and metal reflect comes from two probes the game renders around the player every few
    // seconds (ReflectionUpdate), 2 km across. A far window looks at a place outside them, where everything fell
    // back to the default sky: the sea over there mirrored a sunny sky in the rain. Each far window gets a probe of
    // its own over there, set up the way the game sets up its own, rendered with that place's weather and its view
    // (the same as a window's render), and drawn into by nothing near you.
    // Win.ProbePerArea off: one probe per shown link, redrawn at the game's pace (slower the more there are). On: one
    // probe per far area (shown links whose far points lie within 64 m share it), redrawn when the weather there
    // changes, the sun has moved 2 degrees, the point it stands at has moved 20 m, or after 30 s; but never sooner
    // than the per-link pace allows (the game's interval x half the number of probes, so all of them together start
    // no more redraws than that), never-drawn ones first, and only one probe drawing at a time.
    // Far.ProbeLive (with Win.ProbePerArea): drawn before the window goes live, two per area cross-faded as the game's,
    // every 3 s while on screen, the box kept inside the far area (UpdateAreasLive).
    internal sealed class FarReflections
    {
        sealed class Probe
        {
            public ReflectionProbe P;
            public PortalLink Link;
            public float NextAt, ShownAt;
            public int RenderId = -1;
            public bool Ready;
        }

        sealed class AreaProbe
        {
            public ReflectionProbe P;
            // The link whose far side it shows (its renders are set up as for that link's window).
            public PortalLink Link;
            public float LinkShownAt;
            // Where it would stand now, and what it was last drawn with.
            public Vector3 At, DrawnAt;
            public float ShownAt, DrawnTime;
            public object Env;
            public bool Blending;
            public float Day;
            public int RenderId = -1;
            public bool Ready;
            // Far.ProbeLive: the second probe of the game's pair, which of the two shows (0: P, 1: P2) and which draws
            // (-1: none; it is the other one once one has been drawn); when that draw started (the fade counts from there,
            // as the game's), whether the one shown has grown to its full box, whether its draw had the link live, and
            // whether any window onto it has rendered yet. Per frame: wanted (live, or about to show and standing) and
            // on screen.
            public ReflectionProbe P2;
            public int Shows, Drawing = -1, Draws, MadeFrame;
            public float DrawStart, WantedAt;
            public bool Grown = true, DrawnLive, Rendered, Live, OnScreen;
        }

        const float AreaRadius = 64f, MoveRedraw = 20f, MaxAge = 30f, SunRedraw = 2f / 360f;

        readonly Dictionary<PortalLink, Probe> _probes = new Dictionary<PortalLink, Probe>();
        readonly List<PortalLink> _drop = new List<PortalLink>();
        readonly List<AreaProbe> _areas = new List<AreaProbe>();
        Probe _rendering;
        AreaProbe _renderingArea;

        // Lab: how many probes there are and the last one rendered.
        internal int Count => _probes.Count + _areas.Count;
        internal static string LabLast = "";
        internal static int LabRedraws;
        // Lab (Far.ProbeLive): far-area probes whose first picture was done before any window onto them rendered
        // (cumulative).
        internal static long LabProbeEarly;

        // The one PortalSystem keeps, the frame it last looked at its probes, and whether its per-area probes are the
        // Far.ProbeLive kind (two each; made again when the switch changes).
        static FarReflections s_instance;
        static bool s_hooked;
        int _updatedFrame = -1;
        bool _areasLive;
        static readonly List<PortalLink> s_none = new List<PortalLink>();

        internal FarReflections()
        {
            s_instance = this;
            // Far.ProbeLive: a probe is drawn before its window goes live, also while no window is live yet (PortalSystem
            // leaves its far-side upkeep alone then): the game camera's pre-cull looks at the probes in a frame Update has
            // not run in.
            if (!s_hooked)
            {
                s_hooked = true;
                Camera.onPreCull += IdlePreCull;
            }
        }

        static void IdlePreCull(Camera cam)
        {
            // As PortalSystem's own call: only with far weather (the probes draw the far side's), never while suspended.
            if (!FarFlags.ProbeLive || !WindowFlags.ProbePerArea || cam == null || cam.cameraType != CameraType.Game
                || !Plugin.FarWeather.Value || PortalSystem.Suspended)
                return;
            FarReflections r = s_instance;
            PortalSystem sys = PortalSystem.Instance;
            if (r == null || sys == null || r._updatedFrame == Time.frameCount || cam != Utils.GetMainCamera())
                return;
            r.Update(sys, s_none);
        }

        // Win.Virtual (WindowScreenContent): the probes kept here (both of a Far.ProbeLive pair), added to `into`.
        internal void ProbesInto(List<ReflectionProbe> into)
        {
            foreach (Probe p in _probes.Values)
                if (p.P != null)
                    into.Add(p.P);
            for (int i = 0; i < _areas.Count; i++)
            {
                AreaProbe a = _areas[i];
                if (a.P != null)
                    into.Add(a.P);
                if (a.P2 != null)
                    into.Add(a.P2);
            }
        }

        // Every frame after the windows render, with the links whose windows rendered.
        internal void Update(PortalSystem sys, List<PortalLink> shown)
        {
            ReflectionUpdate game = ReflectionUpdate.instance;
            if (game == null || game.m_probe1 == null)
                return;
            if (WindowFlags.ProbePerArea)
            {
                DropLinks();
                // Far.ProbeLive looks at every link itself (live, or about to show), once a frame.
                bool live = FarFlags.ProbeLive;
                if (live && _updatedFrame == Time.frameCount)
                    return;
                _updatedFrame = Time.frameCount;
                if (live != _areasLive)
                {
                    DropAreas();
                    _areasLive = live;
                }
                if (live)
                    UpdateAreasLive(sys, game);
                else
                    UpdateAreas(sys, shown, game);
                return;
            }
            DropAreas();
            float now = Time.time;
            Player player = Player.m_localPlayer;
            foreach (PortalLink link in shown)
            {
                if (!_probes.TryGetValue(link, out Probe p))
                    _probes[link] = p = new Probe { P = Create(game.m_probe1), Link = link };
                p.ShownAt = now;
            }
            _drop.Clear();
            bool renderedOne = false;
            foreach (var kv in _probes)
            {
                Probe p = kv.Value;
                if (p.P == null || kv.Key == null || !kv.Key.IsActive || now - p.ShownAt > 10f)
                {
                    _drop.Add(kv.Key);
                    continue;
                }
                Vector3 at = sys.FarPoint(kv.Key) + Vector3.up * game.m_reflectionHeight;
                // You are over there now and the game's own probe has followed: it reflects that place itself.
                if (Near(game.m_probe1, at) || Near(game.m_probe2, at))
                {
                    _drop.Add(kv.Key);
                    continue;
                }
                if (!p.Ready && p.RenderId >= 0 && p.P.IsFinishedRendering(p.RenderId))
                {
                    p.Ready = true;
                    Size(p.P, player);
                }
                // One probe starts a render per frame, at the game's pace.
                if (renderedOne || now < p.NextAt || (p.RenderId >= 0 && !p.P.IsFinishedRendering(p.RenderId)))
                    continue;
                renderedOne = true;
                // With many windows in sight their probes take turns: each is redrawn less often (reflections
                // change slowly: the sky, the weather), and the frame never pays for more than about two at once.
                p.NextAt = now + game.m_interval * Mathf.Max(1f, _probes.Count * 0.5f);
                p.P.transform.position = at;
                if (p.Ready)
                    Size(p.P, player);
                p.RenderId = p.P.RenderProbe();
            }
            foreach (PortalLink link in _drop)
            {
                if (_probes.TryGetValue(link, out Probe p) && p.P != null)
                    Object.Destroy(p.P.gameObject);
                _probes.Remove(link);
            }
        }

        // ---- Win.ProbePerArea ---------------------------------------------------------------------------

        void UpdateAreas(PortalSystem sys, List<PortalLink> shown, ReflectionUpdate game)
        {
            float now = Time.time, height = game.m_reflectionHeight;
            Player player = Player.m_localPlayer;
            foreach (AreaProbe a in _areas)
                if (Valid(a.Link))
                    a.At = sys.FarPoint(a.Link) + Vector3.up * height;
            foreach (PortalLink link in shown)
            {
                if (!Valid(link))
                    continue;
                Vector3 at = sys.FarPoint(link) + Vector3.up * height;
                // A far point the game's own probe covers (a far end's link looking back at your side): no area, which
                // the removal below would destroy in the same frame.
                if (Near(game.m_probe1, at) || Near(game.m_probe2, at))
                    continue;
                AreaProbe a = null;
                float best = AreaRadius * AreaRadius;
                foreach (AreaProbe o in _areas)
                {
                    float d = (o.At - at).sqrMagnitude;
                    if (o.Link == link || (Valid(o.Link) && d < best))
                    {
                        a = o;
                        if (o.Link == link)
                            break;
                        best = d;
                    }
                }
                if (a == null)
                {
                    a = new AreaProbe { P = Create(game.m_probe1), Link = link, At = at };
                    _areas.Add(a);
                }
                // Another window onto the same place: its link takes over once the one it had is out of sight, so
                // the probe follows a far side that is still shown (never while a redraw runs).
                if (a.Link != link && now - a.LinkShownAt > 1f && !Busy(a))
                {
                    a.Link = link;
                    a.At = at;
                }
                if (a.Link == link)
                    a.LinkShownAt = now;
                a.ShownAt = now;
            }
            bool drawing = false;
            for (int i = _areas.Count - 1; i >= 0; i--)
            {
                AreaProbe a = _areas[i];
                bool gone = a.P == null || !Valid(a.Link) || now - a.ShownAt > 10f;
                // You are over there now and the game's own probe has followed: it reflects that place itself.
                if (gone || Near(game.m_probe1, a.At) || Near(game.m_probe2, a.At))
                {
                    if (a.P != null)
                        Object.Destroy(a.P.gameObject);
                    _areas.RemoveAt(i);
                    continue;
                }
                bool busy = Busy(a);
                drawing |= busy;
                if (!a.Ready && a.RenderId >= 0 && !busy)
                {
                    a.Ready = true;
                    Size(a.P, player);
                }
            }
            // One probe draws at a time (its faces over the next frames): the frame never pays for more than one.
            if (drawing)
                return;
            // The one waiting longest of those due.
            AreaProbe next = null;
            float pace = game.m_interval * Mathf.Max(1f, _areas.Count * 0.5f);
            foreach (AreaProbe a in _areas)
                if ((next == null || Before(a, next)) && Due(a, now, pace))
                    next = a;
            if (next == null)
                return;
            next.Env = FarEnvironment.EnvAt(next.At, out next.Blending);
            next.Day = FarEnvironment.DayFraction;
            next.DrawnAt = next.At;
            next.DrawnTime = now;
            next.P.transform.position = next.At;
            if (next.Ready)
                Size(next.P, player);
            next.RenderId = next.P.RenderProbe();
            LabRedraws++;
        }

        static bool Valid(PortalLink link) => link != null && link.IsActive && link.Partner != null;

        // Its faces can be set up as for its window (also while the link goes down mid-redraw).
        static bool Showable(PortalLink link) => link != null && link.Partner != null && link.Window != null;

        // Never drawn first, then the one drawn longest ago.
        static bool Before(AreaProbe a, AreaProbe b) =>
            (a.RenderId < 0) != (b.RenderId < 0) ? a.RenderId < 0 : a.DrawnTime < b.DrawnTime;

        static bool Busy(AreaProbe a) => a.P != null && a.RenderId >= 0 && !a.P.IsFinishedRendering(a.RenderId);

        // pace: the least age for a redraw (the game's interval, times half the number of probes when there are many).
        static bool Due(AreaProbe a, float now, float pace)
        {
            if (a.RenderId < 0)
                return true;
            float age = now - a.DrawnTime;
            if (age < pace)
                return false;
            if (age >= MaxAge || (a.At - a.DrawnAt).sqrMagnitude > MoveRedraw * MoveRedraw)
                return true;
            // The weather there changed (or was, or is, in the middle of changing).
            object env = FarEnvironment.EnvAt(a.At, out bool blending);
            if (env != a.Env || blending || a.Blending)
                return true;
            float d = Mathf.Abs(FarEnvironment.DayFraction - a.Day);
            return Mathf.Min(d, 1f - d) > SunRedraw;
        }

        // ---- Far.ProbeLive (Docs\PARITY-PLAN.md P30) -----------------------------------------------------------------

        // A far area's probe pair is made and drawn as soon as a link onto it is live, or about to show (WindowReach) with
        // the half it shows standing, so the window's first frame reflects that place; it stays while a link onto it is
        // live or about to show (10 s past the last). Like the game's ReflectionUpdate, each area has two probes: the one
        // not shown draws, then grows from its centre over the game's transition while the other stays behind it (the
        // first picture shows whole at once: what it replaces was not that place's). Probes of windows on screen are
        // redrawn every ReflectionUpdate.m_interval (3 s, the game's); the others at the old pace (changes, at most every
        // interval x half the number of probes, at least every 30 s). One draws at a time. The box is fitted every frame:
        // never past the far area (its distant ring round the far portal), never within 100 m of you along an axis (the
        // box is axis-aligned), never larger than the game's.
        const float Unwanted = 10f, PlayerClear = 100f, MinHalf = 16f, GameHalf = 1000f, GameHeight = 1000f;

        void UpdateAreasLive(PortalSystem sys, ReflectionUpdate game)
        {
            float now = Time.time, height = game.m_reflectionHeight;
            foreach (AreaProbe a in _areas)
            {
                a.Live = false;
                a.OnScreen = false;
                if (Showable(a.Link))
                    a.At = sys.FarPoint(a.Link) + Vector3.up * height;
            }
            Camera cam = Utils.GetMainCamera();
            Vector3 eye = cam != null ? cam.transform.position : Vector3.zero;
            float reach = WindowReach.Render;
            int frame = Time.frameCount;
            foreach (PortalLink link in PortalLink.All)
            {
                if (!Showable(link) || link.Vanilla || cam == null)
                    continue;
                // Live within the windows' reach (not the far ends' own links, which look back at your side), or about
                // to show with the half it shows standing.
                bool live = link.IsActive;
                float r = reach + Mathf.Max(link.Frame.HalfSize.x, link.Frame.HalfSize.y);
                bool ahead = WindowReach.AboutToShow(link);
                if (!(live && (ahead || (link.Frame.Center - eye).sqrMagnitude <= r * r))
                    && !(ahead && sys.Areas.Stage(link.PartnerZdo) >= 2))
                    continue;
                Vector3 at = sys.FarPoint(link) + Vector3.up * height;
                // A far point the game's own probe covers (a far end's link looking back at your side): no area, which
                // the removal below would destroy in the same frame (two probes made and destroyed every frame).
                if (Near(game.m_probe1, at) || Near(game.m_probe2, at))
                    continue;
                AreaProbe a = null;
                float best = AreaRadius * AreaRadius;
                foreach (AreaProbe o in _areas)
                {
                    float d = (o.At - at).sqrMagnitude;
                    if (o.Link == link || (Showable(o.Link) && d < best))
                    {
                        a = o;
                        if (o.Link == link)
                            break;
                        best = d;
                    }
                }
                if (a == null)
                {
                    a = new AreaProbe { P = Create(game.m_probe1), P2 = Create(game.m_probe1), Link = link, At = at, MadeFrame = frame };
                    _areas.Add(a);
                }
                // Another window onto the same place takes over once the one it had is no longer wanted (never while a
                // draw runs).
                if (a.Link != link && now - a.LinkShownAt > 1f && !BusyLive(a))
                {
                    a.Link = link;
                    a.At = at;
                }
                if (a.Link == link)
                    a.LinkShownAt = now;
                a.WantedAt = now;
                a.Live |= live;
                a.OnScreen |= live && WindowReach.OnScreen(link);
                a.Rendered |= link.Window.RenderedAtFrame >= a.MadeFrame;
            }
            bool drawing = false;
            for (int i = _areas.Count - 1; i >= 0; i--)
            {
                AreaProbe a = _areas[i];
                bool gone = a.P == null || a.P2 == null || !Showable(a.Link) || now - a.WantedAt > Unwanted;
                // You are over there now and the game's own probe has followed: it reflects that place itself.
                if (gone || Near(game.m_probe1, a.At) || Near(game.m_probe2, a.At))
                {
                    DestroyArea(a);
                    _areas.RemoveAt(i);
                    continue;
                }
                if (a.Drawing >= 0)
                {
                    if (BusyLive(a))
                        drawing = true;
                    else
                        Drawn(a);
                }
            }
            FitAreas(game, now);
            // One probe draws at a time (its faces over the next frames): the frame never pays for more than one.
            if (drawing)
                return;
            AreaProbe next = null;
            float pace = game.m_interval * Mathf.Max(1f, _areas.Count * 0.5f);
            foreach (AreaProbe a in _areas)
                if ((next == null || BeforeLive(a, next)) && DueLive(a, now, pace, game.m_interval))
                    next = a;
            if (next == null)
                return;
            // The first picture into the probe that will show it; a redraw into the other one.
            int k = next.Ready ? 1 - next.Shows : next.Shows;
            ReflectionProbe p = k == 0 ? next.P : next.P2;
            next.Env = FarEnvironment.EnvAt(next.At, out next.Blending);
            next.Day = FarEnvironment.DayFraction;
            next.DrawnAt = next.At;
            next.DrawnTime = now;
            next.DrawStart = now;
            next.DrawnLive = next.Live;
            p.transform.position = next.At;
            // Nothing takes it while it draws: the one shown stays in place until this one is done.
            p.size = Vector3.one * 0.01f;
            next.Drawing = k;
            next.RenderId = p.RenderProbe();
            LabRedraws++;
        }

        // A draw is done: the probe that drew shows from now on, growing from its centre (the first picture whole at once).
        void Drawn(AreaProbe a)
        {
            bool first = !a.Ready;
            a.Shows = a.Drawing;
            a.Drawing = -1;
            a.Ready = true;
            a.Grown = first;
            a.Draws++;
            if (first && !a.Rendered)
                LabProbeEarly++;
        }

        static ReflectionProbe ProbeOf(AreaProbe a, int k) => k == 0 ? a.P : a.P2;

        static bool BusyLive(AreaProbe a)
        {
            ReflectionProbe p = a.Drawing >= 0 ? ProbeOf(a, a.Drawing) : null;
            return p != null && a.RenderId >= 0 && !p.IsFinishedRendering(a.RenderId);
        }

        // Never drawn first, then one drawn before its link went live, then those on screen, then the one drawn longest ago.
        static bool BeforeLive(AreaProbe a, AreaProbe b)
        {
            if (a.Ready != b.Ready)
                return !a.Ready;
            bool aStale = a.Live && !a.DrawnLive, bStale = b.Live && !b.DrawnLive;
            if (aStale != bStale)
                return aStale;
            if (a.OnScreen != b.OnScreen)
                return a.OnScreen;
            return a.DrawnTime < b.DrawnTime;
        }

        static bool DueLive(AreaProbe a, float now, float pace, float interval)
        {
            if (!a.Ready || (a.Live && !a.DrawnLive))
                return true;
            // On screen: at the game's own interval, whatever changed.
            if (a.OnScreen)
                return now - a.DrawnTime >= interval;
            return Due(a, now, pace);
        }

        // Every frame: each probe's box as large as it may be where it stands, the one shown grown as far as its fade has
        // come, the other just behind it (as the game's 2001 behind 2000), the one drawing out of the way.
        void FitAreas(ReflectionUpdate game, float now)
        {
            Player player = Player.m_localPlayer;
            SimulationDistance sim = ZNet.instance != null ? RemoteAreas.AreaDistance() : default;
            float areaHalf = (Mathf.Max(sim.TotalSimulationDistance, 1) + 0.5f) * ZoneSystem.c_ZoneSize;
            foreach (AreaProbe a in _areas)
            {
                Vector3 far = a.Link.PartnerZdo != null ? a.Link.PartnerZdo.GetPosition() : a.Link.Partner.Frame.Center;
                for (int k = 0; k < 2; k++)
                {
                    ReflectionProbe p = ProbeOf(a, k);
                    // Drawing, or never drawn into yet (the other one before the second picture).
                    if (k == a.Drawing || !a.Ready || (k != a.Shows && a.Draws < 2))
                    {
                        p.size = Vector3.one * 0.01f;
                        p.importance = 2;
                        continue;
                    }
                    float half = Half(p.transform.position, far, areaHalf, player);
                    if (k == a.Shows)
                    {
                        float f = 1f;
                        if (!a.Grown)
                        {
                            f = Mathf.Clamp01((now - a.DrawStart) / Mathf.Max(game.m_transitionDuration, 0.01f));
                            f = Mathf.Pow(f, game.m_power);
                            a.Grown = f >= 1f;
                        }
                        p.size = new Vector3(2f * half * f, GameHeight * f, 2f * half * f);
                        p.importance = 3;
                    }
                    else
                    {
                        p.size = new Vector3(2f * half + 1f, GameHeight + 1f, 2f * half + 1f);
                        p.importance = 2;
                    }
                }
            }
        }

        // Half the box's width for a probe standing at `at`: inside the far area round the far portal `far` (axis-aligned,
        // so along each axis), at least PlayerClear short of you along the nearer axis, at most the game's.
        static float Half(Vector3 at, Vector3 far, float areaHalf, Player player)
        {
            float half = Mathf.Min(GameHalf, areaHalf - Mathf.Max(Mathf.Abs(at.x - far.x), Mathf.Abs(at.z - far.z)));
            if (player != null)
            {
                Vector3 me = player.transform.position;
                half = Mathf.Min(half, Mathf.Max(Mathf.Abs(at.x - me.x), Mathf.Abs(at.z - me.z)) - PlayerClear);
            }
            return Mathf.Max(half, MinHalf);
        }

        static void DestroyArea(AreaProbe a)
        {
            if (a.P != null)
                Object.Destroy(a.P.gameObject);
            if (a.P2 != null)
                Object.Destroy(a.P2.gameObject);
        }

        void DropLinks()
        {
            if (_probes.Count == 0)
                return;
            foreach (Probe p in _probes.Values)
                if (p.P != null)
                    Object.Destroy(p.P.gameObject);
            _probes.Clear();
            _rendering = null;
        }

        void DropAreas()
        {
            if (_areas.Count == 0)
                return;
            foreach (AreaProbe a in _areas)
                DestroyArea(a);
            _areas.Clear();
            _renderingArea = null;
        }

        // ---- both -------------------------------------------------------------------------------------

        // The game's current probe (the one it shows, importance 1) has been rendered over there.
        static bool Near(ReflectionProbe game, Vector3 at) => game != null && game.importance > 0 && (game.transform.position - at).sqrMagnitude < 100f * 100f;

        static ReflectionProbe Create(ReflectionProbe like)
        {
            var go = new GameObject("RealPortalReflection");
            Object.DontDestroyOnLoad(go);
            ReflectionProbe p = go.AddComponent<ReflectionProbe>();
            p.mode = ReflectionProbeMode.Realtime;
            p.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
            // A face per frame and the blur after: no frame pays for the whole probe.
            p.timeSlicingMode = ReflectionProbeTimeSlicingMode.IndividualFaces;
            p.resolution = like.resolution;
            p.hdr = like.hdr;
            p.cullingMask = like.cullingMask;
            p.nearClipPlane = like.nearClipPlane;
            p.farClipPlane = like.farClipPlane;
            p.clearFlags = like.clearFlags;
            p.backgroundColor = like.backgroundColor;
            p.intensity = like.intensity;
            p.boxProjection = like.boxProjection;
            p.blendDistance = like.blendDistance;
            p.shadowDistance = like.shadowDistance;
            p.importance = 2;
            // Nothing takes it until its first render is done.
            p.size = Vector3.one * 0.01f;
            return p;
        }

        // As wide as the game's own, but never reaching the player's side.
        static void Size(ReflectionProbe p, Player player)
        {
            float d = player != null ? Vector3.Distance(player.transform.position, p.transform.position) : 2000f;
            float w = Mathf.Clamp(2f * (d - 100f), 100f, 2000f);
            p.size = new Vector3(w, 1000f, w);
        }

        // From Camera.onPreCull / onPostRender: a reflection camera rendering one of these probes gets the far
        // side set up as for a window.
        internal bool PreCull(PortalSystem sys, Camera cam)
        {
            foreach (AreaProbe a in _areas)
                if (Showable(a.Link) && (At(a.P, cam) || At(a.P2, cam)))
                {
                    _renderingArea = a;
                    sys.ShowFarForProbe(a.Link, cam.transform.position);
                    LabLast = $"{a.Link.name} at {cam.transform.position:F0} weather {FarEnvironment.LabApplied ?? "the game's"}";
                    return true;
                }
            foreach (Probe p in _probes.Values)
                if (p.P != null && p.Link != null && (cam.transform.position - p.P.transform.position).sqrMagnitude < 0.25f)
                {
                    _rendering = p;
                    sys.ShowFarForProbe(p.Link, cam.transform.position);
                    LabLast = $"{p.Link.name} at {cam.transform.position:F0} weather {FarEnvironment.LabApplied ?? "the game's"}";
                    return true;
                }
            return false;
        }

        // A reflection camera renders this probe's faces (it stands where the probe does).
        static bool At(ReflectionProbe p, Camera cam) => p != null && (cam.transform.position - p.transform.position).sqrMagnitude < 0.25f;

        internal void PostRender(PortalSystem sys, Camera cam)
        {
            if (_rendering == null && _renderingArea == null)
                return;
            _rendering = null;
            _renderingArea = null;
            sys.HideFarForProbe();
        }

        internal void Clear()
        {
            foreach (Probe p in _probes.Values)
                if (p.P != null)
                    Object.Destroy(p.P.gameObject);
            _probes.Clear();
            _rendering = null;
            DropAreas();
        }
    }
}
