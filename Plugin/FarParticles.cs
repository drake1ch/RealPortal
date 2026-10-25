using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace RealPortal
{
    // Many effects on things pause while no camera sees them (falling leaves off the Mistlands trees, embers): Unity
    // counts only the game camera's view for that, so seen only through a window they would never start. Near the
    // far end of a window that is being shown they keep simulating, and get their own way back when that ends.
    // Far.ParticlesByView: those a window's view shows or is about to, at any distance (ByView below).
    internal sealed class FarParticles
    {
        readonly List<ZNetView> _near = new List<ZNetView>();

        readonly Dictionary<ParticleSystem, ParticleSystemCullingMode> _changed = new Dictionary<ParticleSystem, ParticleSystemCullingMode>();
        readonly Dictionary<ZNetView, ParticleSystem[]> _systems = new Dictionary<ZNetView, ParticleSystem[]>();
        readonly HashSet<ParticleSystem> _wanted = new HashSet<ParticleSystem>();
        readonly List<ParticleSystem> _drop = new List<ParticleSystem>();
        readonly List<Vector3> _points = new List<Vector3>();
        float _nextAt, _shownAt = -100f;

        const float Reach = 120f;

        // Lab: how many run on.
        internal int Count => _changed.Count;

        // Every frame after the windows rendered, with the far points they showed.
        internal void Update(List<Vector3> shown, Vector3 camera)
        {
            // Far.ParticlesByView (Docs\PARITY-PLAN.md P12): by what the windows show and are about to, not by distance
            // from their far points.
            if (FarFlags.ParticlesByView)
            {
                ByView(camera);
                return;
            }
            if (_viewLinks.Count > 0 || _heldAt.Count > 0)
                DropByView();
            // Far.ParticleWindowGate: only the live window's far side simulates unseen; the other far sides are paused
            // and advanced right before their windows render (Gate, BeforeRender).
            if (FarFlags.ParticleWindowGate)
                shown = _livePoints;
            if (shown.Count > 0)
            {
                _shownAt = Time.time;
                _points.Clear();
                _points.AddRange(shown);
            }
            if (Time.time < _nextAt)
                return;
            _nextAt = Time.time + 2f;
            _wanted.Clear();
            PortalSystem sys = PortalSystem.Instance;
            if (sys != null && Time.time - _shownAt < 10f)
                sys.Areas.InstancesNear(_points, Reach, _near);
            else
                _near.Clear();
            foreach (ZNetView view in _near)
            {
                // The game camera is close: it sees them itself.
                if (view == null || (view.transform.position - camera).sqrMagnitude < Reach * Reach)
                    continue;
                if (!_systems.TryGetValue(view, out ParticleSystem[] systems))
                    _systems[view] = systems = view.GetComponentsInChildren<ParticleSystem>(true);
                foreach (ParticleSystem ps in systems)
                    if (ps != null && (_changed.ContainsKey(ps) || ps.main.cullingMode != ParticleSystemCullingMode.AlwaysSimulate))
                        _wanted.Add(ps);
            }
            foreach (ParticleSystem ps in _wanted)
                if (!_changed.ContainsKey(ps))
                {
                    ParticleSystem.MainModule main = ps.main;
                    _changed[ps] = main.cullingMode;
                    main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
                }
            _drop.Clear();
            foreach (KeyValuePair<ParticleSystem, ParticleSystemCullingMode> kv in _changed)
                if (kv.Key == null || !_wanted.Contains(kv.Key))
                    _drop.Add(kv.Key);
            foreach (ParticleSystem ps in _drop)
            {
                if (ps != null)
                {
                    ParticleSystem.MainModule main = ps.main;
                    main.cullingMode = _changed[ps];
                }
                _changed.Remove(ps);
            }
            if (_systems.Count > 20000)
                _systems.Clear();
        }

        // From the EffectList.Create postfix: an effect made by the far end of a live portal (blood from a hit, the
        // puff of a body turning into loot) plays even though only a window sees it.
        internal static void Made(GameObject[] made)
        {
            // Far.ParticlesByView: plays when a window shows the place it is made in or is about to (the same test as the
            // far side's own systems).
            if (FarFlags.ParticlesByView)
            {
                s_byView?.MadeByView(made);
                return;
            }
            PortalSystem sys = PortalSystem.Instance;
            Camera cam = Utils.GetMainCamera();
            if (made == null || made.Length == 0 || sys == null || cam == null)
                return;
            IReadOnlyList<PortalLink> live = sys.Active;
            if (live.Count == 0)
                return;
            foreach (GameObject go in made)
            {
                if (go == null)
                    continue;
                Vector3 at = go.transform.position;
                if ((at - cam.transform.position).sqrMagnitude < Reach * Reach)
                    continue;
                bool far = false;
                foreach (PortalLink link in live)
                    if (link != null && link.Partner != null && (at - link.Partner.Frame.Center).sqrMagnitude < Reach * Reach)
                    {
                        far = true;
                        break;
                    }
                if (!far)
                    continue;
                // Into a list kept for it: no array made per effect.
                go.GetComponentsInChildren(true, s_made);
                foreach (ParticleSystem ps in s_made)
                {
                    ParticleSystem.MainModule main = ps.main;
                    main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
                }
                s_made.Clear();
            }
        }

        static readonly List<ParticleSystem> s_made = new List<ParticleSystem>();

        internal void Clear()
        {
            foreach (KeyValuePair<ParticleSystem, ParticleSystemCullingMode> kv in _changed)
                if (kv.Key != null)
                {
                    ParticleSystem.MainModule main = kv.Key.main;
                    main.cullingMode = kv.Value;
                }
            _changed.Clear();
            _systems.Clear();
            _renderers.Clear();
            DropByView();
            if (s_byView == this)
                s_byView = null;
            Release();
        }

        // ---- Far.ParticlesByView ------------------------------------------------------------------------------------

        // Docs\PARITY-PLAN.md P12. A far system simulates (AlwaysSimulate) while a window shows it or is about to, at any
        // distance from the far point: for each live window, the lively objects of the far side within the area's own
        // reach whose systems' bounds meet the window's last view (its frustum, widened by ViewSlackDegrees and CullMargin
        // for the motion until the next look, and only the half of the far side it shows). A window about to show that
        // has not rendered gets the whole half it shows (WindowReach.ShownHalf) within that reach. Windows are looked at
        // in the frame they come into view or become about to, again when their view moved, turned or switched halves, and
        // every 2 s otherwise; a system goes back to its own culling 10 s after the last look that wanted it. A system
        // near the game camera (Reach) is left to the game's culling only when it lies in the game camera's frustum and the
        // game itself has its object there: that camera sees it itself. Whether Unity counts the windows' own cameras for
        // its culling does not matter here.
        sealed class LinkView
        {
            internal PortalLink Link;
            // Rendered this frame or about to show at the last look (false: neither, so the next one is a fresh look).
            internal bool Covered;
            // The last look: when, by the window's own view (else by the half it shows), that view's position, the game
            // camera's direction, and the half.
            internal float ListedAt = -100f;
            internal bool ByView;
            internal Vector3 ListedEye, ListedFwd;
            internal int ListedHalf;
            internal int SeenFrame;
        }

        readonly Dictionary<PortalLink, LinkView> _viewLinks = new Dictionary<PortalLink, LinkView>();
        readonly List<LinkView> _viewList = new List<LinkView>();
        // When a system switched here was last wanted by a look.
        readonly Dictionary<ParticleSystem, float> _heldAt = new Dictionary<ParticleSystem, float>();
        // Each lively object's particle renderers, in the order of _systems (looked up once).
        readonly Dictionary<ZNetView, ParticleSystemRenderer[]> _renderers = new Dictionary<ZNetView, ParticleSystemRenderer[]>();
        readonly List<Vector3> _viewPoints = new List<Vector3>();
        readonly List<ZNetView> _viewNear = new List<ZNetView>();
        readonly Plane[] _camPlanes = new Plane[6];
        Vector3 _camPos, _camFwd;
        bool _hasCam;
        int _camFrame = -1;
        // The player's zone and the synced simulation distance at the last sample (GameHas).
        Vector2s _meZone;
        int _nearZones, _totalZones;
        bool _classic, _hasZone;
        float _viewSweepAt, _viewReach = 600f;
        // The one PortalSystem keeps (for Made).
        static FarParticles s_byView;

        // A system goes back to its own culling this long after the last look that wanted it.
        const float HoldSeconds = 10f;
        // Every window in view is looked at again at least this often, and when its view moved or the game camera turned
        // this much since; at most MaxRelistsPerFrame such looks a frame (a window just come into view always), taken in
        // turn from _relistFrom. A window rendered or shown within ViewFrames frames is looked at by its last view.
        const float ViewRelistSeconds = 2f, ViewMoveRelist = 4f, ViewTurnRelistDegrees = 15f;
        const int MaxRelistsPerFrame = 2, ViewFrames = 4;
        int _relistFrom;
        // How far past a window's last view a system still counts as in it (the angle its edges may move by before the
        // next look; CullMargin is added too), and the sphere taken round a new effect.
        const float ViewSlackDegrees = 10f, MadeRadius = 4f;
        static readonly float s_viewSlack = Mathf.Tan(ViewSlackDegrees * Mathf.Deg2Rad);
        static readonly float s_turnRelistCos = Mathf.Cos(ViewTurnRelistDegrees * Mathf.Deg2Rad);

        // Lab: far systems switched to AlwaysSimulate by Far.ParticlesByView (cumulative, Made's included).
        internal static long LabParticlesByView;

        void ByView(Vector3 camera)
        {
            s_byView = this;
            float now = Time.time;
            int frame = Time.frameCount;
            // The camera's final pose of this frame (an effect made earlier in the frame sampled the one before LateUpdate).
            _camFrame = -1;
            SampleCamera(camera, frame);
            PortalSystem sys = PortalSystem.Instance;
            if (sys != null && ZNet.instance != null)
            {
                _viewReach = AreaReach();
                IReadOnlyList<PortalLink> live = sys.Active;
                int relists = 0;
                // The budgeted looks start where the last frame's ended, so every window gets its turn.
                int n = live.Count, start = n > 0 ? _relistFrom % n : 0, next = start;
                for (int j = 0; j < n; j++)
                {
                    int i = start + j < n ? start + j : start + j - n;
                    PortalLink link = live[i];
                    PortalWindow w = link != null ? link.Window : null;
                    if (w == null || link.PartnerZdo == null)
                        continue;
                    if (!_viewLinks.TryGetValue(link, out LinkView v))
                    {
                        v = new LinkView { Link = link };
                        _viewLinks[link] = v;
                        _viewList.Add(v);
                    }
                    v.SeenFrame = frame;
                    // Its last view while it has one shown (a window that renders every few frames is shown from an
                    // earlier render between them): by that view, not by the half on the frames it does not render.
                    bool rendered = w.HasView && (w.RenderedThisFrame || w.RenderedAtFrame >= frame - ViewFrames || w.ShownAtFrame >= frame - ViewFrames);
                    if (!rendered && !WindowReach.AboutToShow(link))
                    {
                        v.Covered = false;
                        continue;
                    }
                    bool fresh = !v.Covered;
                    v.Covered = true;
                    int half = _hasCam ? WindowReach.ShownHalf(link, _camPos) : 1;
                    bool due = fresh;
                    if (!due && relists < MaxRelistsPerFrame)
                    {
                        // A view newly there is looked at by it; one gone keeps the last look until the next is due.
                        due = (rendered && !v.ByView) || half != v.ListedHalf || now - v.ListedAt >= ViewRelistSeconds
                              || Vector3.Dot(_camFwd, v.ListedFwd) < s_turnRelistCos
                              || (rendered && (w.ViewPos - v.ListedEye).sqrMagnitude > ViewMoveRelist * ViewMoveRelist);
                        if (due)
                        {
                            relists++;
                            next = i + 1;
                        }
                    }
                    if (due)
                        ListByView(sys, v, w, rendered, half, now);
                }
                _relistFrom = next;
            }
            // Windows no longer live: their next look is a fresh one; long gone ones are forgotten.
            for (int i = _viewList.Count - 1; i >= 0; i--)
            {
                LinkView v = _viewList[i];
                if (v.SeenFrame == frame)
                    continue;
                v.Covered = false;
                if (v.Link == null || frame - v.SeenFrame > 600)
                {
                    _viewLinks.Remove(v.Link);
                    int last = _viewList.Count - 1;
                    _viewList[i] = _viewList[last];
                    _viewList.RemoveAt(last);
                }
            }
            if (now >= _viewSweepAt)
            {
                _viewSweepAt = now + ViewRelistSeconds;
                SweepByView(now);
            }
        }

        // The game camera's pose and frustum, and the player's zone and the synced simulation distance, once a frame (the
        // exemption near it).
        void SampleCamera(Vector3 camera, int frame)
        {
            if (_camFrame == frame)
                return;
            _camFrame = frame;
            ZNet net = ZNet.instance;
            _hasZone = net != null && ZoneSystem.instance != null;
            if (_hasZone)
            {
                _meZone = ZoneSystem.GetZone(net.GetReferencePosition());
                SimulationDistance sim = net.GetSyncedSimulationDistance();
                _nearZones = sim.NearSimulationDistance;
                _totalZones = sim.TotalSimulationDistance;
                _classic = sim.IsClassic;
            }
            Camera cam = Utils.GetMainCamera();
            _hasCam = cam != null;
            if (!_hasCam)
                return;
            GeometryUtility.CalculateFrustumPlanes(cam, _camPlanes);
            _camPos = camera;
            _camFwd = cam.transform.forward;
        }

        // One look at what a window shows: its far side's lively objects within the area's reach of the far portal, their
        // systems tested against its view (byView) or the half it shows.
        void ListByView(PortalSystem sys, LinkView v, PortalWindow w, bool byView, int half, float now)
        {
            PortalLink link = v.Link;
            v.ListedAt = now;
            v.ByView = byView;
            v.ListedHalf = half;
            v.ListedEye = w.ViewPos;
            v.ListedFwd = _camFwd;
            PortalFrame far = link.FarFrame;
            _viewPoints.Clear();
            _viewPoints.Add(far != null ? far.Center : link.PartnerZdo.GetPosition());
            sys.Areas.InstancesNear(_viewPoints, _viewReach, _viewNear);
            Plane[] view = byView ? w.ViewFrustum : null;
            Vector3 apex = w.ViewPos;
            for (int i = 0; i < _viewNear.Count; i++)
            {
                ZNetView obj = _viewNear[i];
                if (obj == null)
                    continue;
                ZDO zdo = obj.GetZDO();
                bool gameHas = GameHas(obj.transform.position, zdo != null ? zdo.Distant : obj.m_distant);
                ParticleSystem[] systems = SystemsOf(obj, out ParticleSystemRenderer[] renderers);
                for (int k = 0; k < systems.Length; k++)
                {
                    ParticleSystem ps = systems[k];
                    if (ps == null)
                        continue;
                    Sphere(ps, renderers[k], out Vector3 center, out float radius);
                    float r = radius + CullMargin;
                    if (far != null && !WindowReach.InHalf(far, center, half, r))
                        continue;
                    if (view != null && OutsideWidened(view, apex, center, r))
                        continue;
                    if (SeenByGameCamera(center, radius, gameHas))
                        continue;
                    Hold(ps, now);
                }
            }
            _viewNear.Clear();
        }

        // An object's systems (all of them, inactive ones too: a fire lit later plays at once) and their renderers.
        ParticleSystem[] SystemsOf(ZNetView view, out ParticleSystemRenderer[] renderers)
        {
            if (!_systems.TryGetValue(view, out ParticleSystem[] systems))
                _systems[view] = systems = view.GetComponentsInChildren<ParticleSystem>(true);
            if (!_renderers.TryGetValue(view, out renderers) || renderers.Length != systems.Length)
            {
                renderers = new ParticleSystemRenderer[systems.Length];
                for (int k = 0; k < systems.Length; k++)
                    renderers[k] = systems[k] != null ? systems[k].GetComponent<ParticleSystemRenderer>() : null;
                _renderers[view] = renderers;
            }
            return systems;
        }

        // A sphere round what a system shows: its particles' bounds and its own position (where new ones come from); its
        // position alone while it is inactive or has no particles.
        static void Sphere(ParticleSystem ps, ParticleSystemRenderer r, out Vector3 center, out float radius)
        {
            Vector3 at = ps.transform.position;
            if (r != null && ps.gameObject.activeInHierarchy)
            {
                Bounds b = r.bounds;
                Vector3 extents = b.extents;
                if (extents.x > 0f || extents.y > 0f || extents.z > 0f)
                {
                    center = b.center;
                    radius = Mathf.Max(extents.magnitude, Vector3.Distance(at, b.center));
                    return;
                }
            }
            center = at;
            radius = 0f;
        }

        // Outside a window's last view: past one of its sides by more than the radius plus the slack angle at that distance
        // from the view's position, or behind its near plane (the opening) by more than the radius.
        static bool OutsideWidened(Plane[] view, Vector3 apex, Vector3 center, float radius)
        {
            float slack = radius + s_viewSlack * Vector3.Distance(apex, center);
            for (int k = 0; k < 4; k++)
                if (view[k].GetDistanceToPoint(center) < -slack)
                    return true;
            return view[4].GetDistanceToPoint(center) < -radius;
        }

        // Near the game camera and inside its frustum, of an object the game itself has there (`gameHas`): it sees the
        // system itself, the game's culling decides. One the game would not have is not in its view (Win.BaseHidesFar
        // hides it from the game camera), so it is held as any other the window shows.
        bool SeenByGameCamera(Vector3 center, float radius, bool gameHas) =>
            gameHas && _hasCam && (center - _camPos).sqrMagnitude < Reach * Reach && !Outside(_camPlanes, center, radius);

        // The game has what lies here (ZDOMan.FindSectorObjects round the player, BaseView's rule): every object of his near
        // zones (the square when classic, else the zones within his near radius), the distant ones of his ring too.
        bool GameHas(Vector3 at, bool distant)
        {
            if (!_hasZone)
                return true;
            Vector2s z = ZoneSystem.GetZone(at);
            int d = Mathf.Max(Mathf.Abs(z.x - _meZone.x), Mathf.Abs(z.y - _meZone.y));
            if (d <= _nearZones && (_classic || d == 0 || ZoneSystem.instance.ZonesWithinRadius(_meZone, z, _nearZones)))
                return true;
            return distant && d <= _totalZones && (_classic || d == 0 || ZoneSystem.instance.ZonesWithinRadius(_meZone, z, _totalZones, ghostZone: true));
        }

        void Hold(ParticleSystem ps, float now)
        {
            if (!_changed.ContainsKey(ps))
            {
                ParticleSystem.MainModule main = ps.main;
                // Simulating always by itself: nothing to switch, nothing to give back.
                if (main.cullingMode == ParticleSystemCullingMode.AlwaysSimulate)
                    return;
                _changed[ps] = main.cullingMode;
                main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
                LabParticlesByView++;
            }
            _heldAt[ps] = now;
        }

        // Every 2 s: systems no look wanted for HoldSeconds (or gone) get their own culling back.
        void SweepByView(float now)
        {
            _drop.Clear();
            foreach (KeyValuePair<ParticleSystem, ParticleSystemCullingMode> kv in _changed)
                if (kv.Key == null || !_heldAt.TryGetValue(kv.Key, out float at) || now - at > HoldSeconds)
                    _drop.Add(kv.Key);
            foreach (ParticleSystem ps in _drop)
            {
                if (ps != null)
                {
                    ParticleSystem.MainModule main = ps.main;
                    main.cullingMode = _changed[ps];
                }
                _changed.Remove(ps);
                _heldAt.Remove(ps);
            }
            _drop.Clear();
            // Entries of systems switched back elsewhere (Clear, the old path) are not kept.
            if (_heldAt.Count > _changed.Count)
            {
                foreach (KeyValuePair<ParticleSystem, float> kv in _heldAt)
                    if (kv.Key == null || !_changed.ContainsKey(kv.Key))
                        _drop.Add(kv.Key);
                foreach (ParticleSystem ps in _drop)
                    _heldAt.Remove(ps);
                _drop.Clear();
            }
            if (_systems.Count > 20000 || _renderers.Count > 20000)
            {
                _systems.Clear();
                _renderers.Clear();
            }
        }

        // The switch off: the windows' state goes; what was switched stays in _changed for the old path to give back.
        void DropByView()
        {
            _viewLinks.Clear();
            _viewList.Clear();
            _heldAt.Clear();
        }

        // An effect made (EffectList.Create) inside the far side of a window that shows it or is about to, by the same
        // test as the far side's own systems at that window's last look; never given back (as before: effects end).
        void MadeByView(GameObject[] made)
        {
            if (made == null || made.Length == 0 || _viewList.Count == 0)
                return;
            int frame = Time.frameCount;
            Camera cam = Utils.GetMainCamera();
            if (cam != null)
                SampleCamera(cam.transform.position, frame);
            float reach2 = _viewReach * _viewReach;
            foreach (GameObject go in made)
            {
                if (go == null)
                    continue;
                Vector3 at = go.transform.position;
                bool wanted = false;
                for (int i = 0; i < _viewList.Count && !wanted; i++)
                {
                    LinkView v = _viewList[i];
                    PortalLink link = v.Link;
                    PortalWindow w = link != null ? link.Window : null;
                    PortalFrame far = link != null ? link.FarFrame : null;
                    if (!v.Covered || w == null || far == null || (at - far.Center).sqrMagnitude > reach2)
                        continue;
                    float r = MadeRadius + CullMargin;
                    if (!WindowReach.InHalf(far, at, v.ListedHalf, r))
                        continue;
                    if (v.ByView && w.HasView && OutsideWidened(w.ViewFrustum, w.ViewPos, at, r))
                        continue;
                    wanted = true;
                }
                if (!wanted || SeenByGameCamera(at, MadeRadius, GameHas(at, false)))
                    continue;
                go.GetComponentsInChildren(true, s_made);
                foreach (ParticleSystem ps in s_made)
                {
                    ParticleSystem.MainModule main = ps.main;
                    if (main.cullingMode == ParticleSystemCullingMode.AlwaysSimulate)
                        continue;
                    main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
                    LabParticlesByView++;
                }
                s_made.Clear();
            }
        }

        // ---- Far.ParticleWindowGate ---------------------------------------------------------------------------------

        // A playing system costs every frame whether anything sees it or not (Unity's BeginUpdateAll walks each one on
        // the main thread, about 0.5 us apiece), and twenty far areas hold some 11.6k. With the switch on they are
        // paused everywhere but on the far side of the live window (at most one), which plays as before, and in the
        // place you stand in. The far side of a window that renders is advanced right before that render by the time
        // it missed (at most 2 s), drawn, and stays paused. Only what plays and emits is paused, and only what the gate
        // holds is played again: a system its owner stopped never restarts. RemoteAreas leaves far particles to the
        // gate while it is on (its sleep only freezes creatures then), so a window seen or about to be (WakeInSight,
        // Watched) no longer keeps them running. Each system is paused, advanced and played on its own (children
        // included, never withChildren): a child its owner stopped is not touched, and none is advanced twice.
        sealed class Gated
        {
            internal ParticleSystem Ps;
            // Time.time it was paused or last advanced, and the frame of that advance (once a frame, whichever
            // windows show it).
            internal float At;
            internal int Frame = -1;
            internal float ListedAt;
            // Held paused by the gate, still to be advanced and played by it (also one found paused already: a far
            // area asleep from before the switch, whose list RemoteAreas drops while the gate is on).
            internal bool Mine;
            // In a dungeon interior high above its place: no window shows it, so it is never advanced.
            internal bool Interior;
            // Far.ParticleCatchupCull: a sphere round its particles and its own position after its last advance (paused,
            // they stay where they are), Radius < 0 until then; its renderer, looked up once.
            internal Vector3 Center;
            internal float Radius = -1f;
            internal ParticleSystemRenderer Renderer;
            internal bool RendererLooked;
        }

        // The far side around one far portal (its RemoteAreas area): what the gate holds paused there, for the
        // renders of the windows onto it.
        sealed class GatedArea
        {
            internal ZDOID Portal;
            internal Vector3 Center;
            internal readonly List<Gated> Systems = new List<Gated>();
            // Where the last catch-up stopped for the budget (the next starts there, so every system gets its turn).
            internal int Cursor;
            internal float ListedAt = -50f, SeenAt;
            // Plays as before: the live window's far side, or the place you stand in.
            internal bool Running;
            // Not listed yet: what RemoteAreas' sleep paused before the switch is taken over (played if it runs).
            internal bool Fresh = true;
        }

        readonly Dictionary<ZDOID, GatedArea> _areas = new Dictionary<ZDOID, GatedArea>();
        readonly List<GatedArea> _areaList = new List<GatedArea>();
        readonly Dictionary<ParticleSystem, Gated> _gated = new Dictionary<ParticleSystem, Gated>();
        readonly List<Gated> _all = new List<Gated>();
        readonly List<Vector3> _gatePoints = new List<Vector3>();
        readonly List<ZNetView> _gateNear = new List<ZNetView>();
        readonly List<Vector3> _running = new List<Vector3>();
        readonly List<Vector3> _livePoints = new List<Vector3>();
        PortalLink _live;
        static PortalLink s_live;
        float _areasAt, _reach = 600f;
        int _sweep, _spentFrame = -1;
        long _spentTicks;

        // Advanced by at most this much time (a looping effect is in its steady state by then).
        const float CatchUpMax = 2f;
        // Main-thread time for the catch-ups of all window renders of a frame (the first system of a render always).
        const double CatchUpBudgetMs = 1.0;
        // Without window tiers (no manager): the nearest window this close is the live one, as the count scheduler
        // renders it every frame.
        const float LiveDistance = 8f;
        // As RemoteAreas.UpdateSight: a far place you stand in (just walked through) plays.
        const float PlayerNear = 200f;
        // Dungeon interiors stand about 5000 m up, above 3000 m (RemoteAreas.InteriorHeight).
        const float InteriorHeight = 3000f, InteriorY = 5000f;
        const float RelistSeconds = 1f, StaleSeconds = 5f;
        const int SweepPerFrame = 256;
        // Far.ParticleCatchupCull: how far outside a render's view a system's sphere still counts as in it (what drifts
        // in as it is advanced), and how long one may wait outside every view before a render of its far side advances it
        // anyway (its sphere measured again: an effect on something that moved).
        const float CullMargin = 10f, CullRefreshSeconds = 5f;

        // Lab: far systems the gate holds paused, its far areas, the live window (PortalSystem keeps the one gate).
        static FarParticles s_gate;
        internal static int LabGatePaused
        {
            get
            {
                int n = 0;
                if (s_gate != null)
                    foreach (Gated g in s_gate._all)
                        if (g.Mine)
                            n++;
                return n;
            }
        }
        internal static int LabGateAreas => s_gate != null ? s_gate._areaList.Count : 0;
        internal static PortalLink LabGateLive => s_live;
        // Lab: the catch-ups before window renders (cumulative): systems advanced, renders that advanced any, renders the
        // budget cut short (the rest waits for a later render, its time still owed), frames with any catch-up; their
        // main-thread ms in all (per frame: the difference over a measurement divided by its frames) and the worst frame.
        internal static long LabCatchUpSystems, LabCatchUpRenders, LabCatchUpCut, LabCatchUpFrames;
        // Lab (Far.ParticleCatchupCull, Far.InViewStep): systems a render left to a later one, outside its view (cumulative).
        internal static long LabCatchUpCulled;
        // Lab (Far.InViewStep, counted in the catch-up figures above too): systems stepped in view, renders that reached
        // InViewStepMax or InViewFrameBudgetMs (cumulative); the cap itself (systems a render steps at most) and the
        // frame's backstop (main-thread ms of all its catch-ups past which a render steps one system only; lab-tunable).
        internal static long LabInViewSteps, LabInViewCut;
        internal static int InViewStepMax = 128;
        internal static double InViewFrameBudgetMs = 2.5;
        internal static double LabCatchUpMs;
        internal static float LabCatchUpMaxMs;

        // Something to run down (areas known or systems held), also once the switch is off.
        internal bool Gating => _all.Count > 0 || _areaList.Count > 0;

        // From PortalSystem.Update every frame while Far.ParticleWindowGate is on or the gate holds anything: before
        // this frame's particle update, so what is played or paused here counts at once.
        internal void Gate(PortalSystem sys)
        {
            if (!FarFlags.ParticleWindowGate)
            {
                Release();
                return;
            }
            s_gate = this;
            float now = Time.time;
            int frame = Time.frameCount;
            CloseCatchUpFrame(frame);
            if (now >= _areasAt && ZNet.instance != null)
            {
                _areasAt = now + 1f;
                FindAreas(sys, now);
            }
            PickLive(sys, frame);
            // Which far sides play as before. One that starts or stops is listed again at once, so what it holds plays
            // or pauses from this frame's particle update; otherwise one area a frame, the longest unlisted first.
            Player me = Player.m_localPlayer;
            ZDO liveFar = _live != null ? _live.PartnerZdo : null;
            _running.Clear();
            for (int i = 0; i < _areaList.Count; i++)
            {
                GatedArea a = _areaList[i];
                bool run = (liveFar != null && a.Portal == liveFar.m_uid)
                           || (me != null && Utils.DistanceXZ(me.transform.position, a.Center) < PlayerNear);
                if (run != a.Running)
                {
                    a.Running = run;
                    a.ListedAt = float.NegativeInfinity;
                }
                if (run)
                    _running.Add(a.Center);
            }
            GatedArea due = null;
            for (int i = 0; i < _areaList.Count; i++)
            {
                GatedArea a = _areaList[i];
                if (float.IsNegativeInfinity(a.ListedAt))
                    List(a, sys, now);
                else if (now - a.ListedAt >= RelistSeconds && (due == null || a.ListedAt < due.ListedAt))
                    due = a;
            }
            if (due != null)
                List(due, sys, now);
            Sweep(now);
        }

        // The far portals with an area (loading or loaded), each a far side of its own.
        void FindAreas(PortalSystem sys, float now)
        {
            _reach = AreaReach();
            for (int i = 0; i < PortalLink.All.Count; i++)
            {
                PortalLink link = PortalLink.All[i];
                ZDO far = link != null && link.IsReal ? link.PartnerZdo : null;
                if (far == null || !far.IsValid() || !sys.Areas.Has(far))
                    continue;
                if (!_areas.TryGetValue(far.m_uid, out GatedArea a))
                {
                    a = new GatedArea { Portal = far.m_uid };
                    _areas[far.m_uid] = a;
                    _areaList.Add(a);
                }
                a.Center = far.GetPosition();
                a.SeenAt = now;
            }
            // An area going away: what it held is let go by the sweep, unless another area lists it.
            for (int i = _areaList.Count - 1; i >= 0; i--)
                if (_areaList[i].SeenAt < now)
                {
                    _areas.Remove(_areaList[i].Portal);
                    _areaList.RemoveAt(i);
                }
            // The systems by object are shared with Update, which may not run (no far weather): kept bounded here too.
            if (_systems.Count > 20000)
                _systems.Clear();
        }

        // How far a far area's objects reach from its portal (RemoteAreas.AreaDistance: its near zones and the distant
        // ring, as Win.LayerCull measures the game's own), with room for hills.
        static float AreaReach()
        {
            SimulationDistance sim = RemoteAreas.AreaDistance();
            const float zone = ZoneSystem.c_ZoneSize, halfDiagonal = ZoneSystem.c_ZoneSize * 0.70711f;
            int r = Mathf.Max(sim.TotalSimulationDistance, 1);
            return (sim.IsClassic ? (r + 1) * zone * 1.41422f : (r + 0.8f) * zone + 2f * halfDiagonal) + 32f;
        }

        // The live window (at most one): one the scheduler has Live (the one being crossed first, then the nearest) and
        // that rendered just now; without tiers the nearest within LiveDistance. The one live so far keeps its place
        // while it may, so two near windows do not trade their far sides back and forth, unless another is crossed.
        void PickLive(PortalSystem sys, int frame)
        {
            Camera cam = Utils.GetMainCamera();
            Vector3 eye = cam != null ? cam.transform.position : Vector3.zero;
            IReadOnlyList<PortalLink> live = sys.Active;
            PortalLink best = null;
            bool bestCrossing = false, kept = false, keptCrossing = false;
            float bestDist = float.MaxValue;
            for (int i = 0; i < live.Count; i++)
            {
                PortalLink link = live[i];
                PortalWindow w = link != null ? link.Window : null;
                if (w == null || link.PartnerZdo == null || w.RenderedAtFrame < frame - 2)
                    continue;
                WindowState s = w.State;
                bool crossing = s != null && s.Crossing;
                float d = cam != null ? Vector3.Distance(eye, link.Frame.Center) : 0f;
                if (s != null ? s.Tier != WindowTier.Live && !crossing : d > LiveDistance)
                    continue;
                if (link == _live)
                {
                    kept = true;
                    keptCrossing = crossing;
                }
                if (best == null || (crossing && !bestCrossing) || (crossing == bestCrossing && d < bestDist))
                {
                    best = link;
                    bestCrossing = crossing;
                    bestDist = d;
                }
            }
            if (kept && (keptCrossing || !bestCrossing))
                best = _live;
            _live = best;
            s_live = best;
            _livePoints.Clear();
            if (best != null)
                _livePoints.Add(sys.FarPoint(best));
        }

        // The systems of one far side, from its area's lively objects within reach of its portal (and of the dungeon
        // interiors above it): in a place that plays, what the gate held plays again; elsewhere what plays and emits is
        // paused. At its first listing what is paused already (RemoteAreas' sleep from before the switch) is taken
        // over too; later a system paused by someone else (the engine's ParticleLod) is left to them.
        void List(GatedArea a, PortalSystem sys, float now)
        {
            a.ListedAt = now;
            a.Systems.Clear();
            if (ZNet.instance == null)
                return;
            bool fresh = a.Fresh;
            a.Fresh = false;
            _gatePoints.Clear();
            _gatePoints.Add(a.Center);
            bool high = a.Center.y < InteriorHeight;
            if (high)
                _gatePoints.Add(new Vector3(a.Center.x, InteriorY, a.Center.z));
            sys.Areas.InstancesNear(_gatePoints, _reach, _gateNear);
            Vector2s zone = ZoneSystem.GetZone(ZNet.instance.GetReferencePosition());
            float r2 = _reach * _reach;
            for (int i = 0; i < _gateNear.Count; i++)
            {
                ZNetView view = _gateNear[i];
                if (view == null)
                    continue;
                Vector3 at = view.transform.position;
                bool interior = high && at.y > InteriorHeight;
                // The game's own area (a far side overlapping where you are) is the game camera's to see; and a far
                // side that plays covers what lies within its reach, whichever area listed it.
                bool run = ZNetScene.InActiveArea(at, zone);
                for (int c = 0; c < _running.Count && !run && !interior; c++)
                    run = (at - _running[c]).sqrMagnitude < r2;
                if (!_systems.TryGetValue(view, out ParticleSystem[] systems))
                    _systems[view] = systems = view.GetComponentsInChildren<ParticleSystem>(true);
                for (int k = 0; k < systems.Length; k++)
                {
                    ParticleSystem ps = systems[k];
                    if (ps == null)
                        continue;
                    _gated.TryGetValue(ps, out Gated g);
                    if (run)
                    {
                        // Goes on from where it stood.
                        if (g != null && g.Mine)
                        {
                            if (ps.isPaused)
                                ps.Play(false);
                            g.Mine = false;
                        }
                        else if (fresh && ps.isPaused)
                            ps.Play(false);
                        continue;
                    }
                    if (g != null && g.Mine)
                    {
                        if (!ps.isPaused)
                        {
                            if (!ps.isPlaying || !ps.isEmitting)
                            {
                                // Stopped by its owner since: its own again.
                                g.Mine = false;
                                continue;
                            }
                            // Played again by its owner (lit, switched back on): paused again from here.
                            ps.Pause(false);
                            g.At = now;
                            // Its particles moved while it played (Far.ParticleCatchupCull measures it again).
                            g.Radius = -1f;
                        }
                    }
                    else
                    {
                        if (ps.isPlaying && ps.isEmitting)
                            ps.Pause(false);
                        else if (!fresh || !ps.isPaused)
                            continue;
                        if (g == null)
                        {
                            g = new Gated { Ps = ps };
                            _gated[ps] = g;
                            _all.Add(g);
                        }
                        g.Mine = true;
                        g.At = now;
                        g.Frame = -1;
                        g.Radius = -1f;
                    }
                    g.ListedAt = now;
                    g.Interior = interior;
                    a.Systems.Add(g);
                }
            }
        }

        // From PortalSystem.BeforeWindowRender: the paused systems of the far side this window shows, advanced by the
        // time since they were last (Simulate leaves them paused). One budget for every render of the frame; a render
        // advances at least one system, and where the budget ran out the next render of that far side goes on.
        // Far.ParticleCatchupCull: only those in or near this render's view (camPos: its camera on the far side); the
        // others keep what they are owed for a render that sees them.
        internal void BeforeRender(PortalLink link, Vector3 camPos)
        {
            ZDO far = link != null ? link.PartnerZdo : null;
            if (far == null || !_areas.TryGetValue(far.m_uid, out GatedArea a) || a.Running || a.Systems.Count == 0)
                return;
            int frame = Time.frameCount;
            CloseCatchUpFrame(frame);
            if (_spentFrame != frame)
            {
                _spentFrame = frame;
                _spentTicks = 0;
                LabCatchUpFrames++;
            }
            float now = Time.time;
            // Far.InViewStep: the window's own on-screen view steps what it sees (a nested view, whose camera is elsewhere,
            // catches up as before).
            if (FarFlags.InViewStep)
            {
                Plane[] seen = ViewOf(link, camPos);
                if (seen != null)
                {
                    StepInView(a, seen, frame, now);
                    return;
                }
            }
            bool cull = FarFlags.ParticleCatchupCull;
            Plane[] view = cull ? ViewOf(link, camPos) : null;
            long budget = (long)(CatchUpBudgetMs * System.Diagnostics.Stopwatch.Frequency / 1000.0);
            List<Gated> list = a.Systems;
            int n = list.Count, i = a.Cursor < n ? a.Cursor : 0, done = 0;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp(), t = t0;
            for (int k = 0; k < n; k++)
            {
                Gated g = list[i];
                ParticleSystem ps = g.Ps;
                if (!g.Mine || g.Interior || g.Frame == frame || ps == null)
                {
                    i = i + 1 < n ? i + 1 : 0;
                    continue;
                }
                // Measured, not waiting too long, and its sphere outside this view: left for a render that sees it.
                if (view != null && g.Radius >= 0f && now - g.At < CullRefreshSeconds && Outside(view, g.Center, g.Radius + CullMargin))
                {
                    i = i + 1 < n ? i + 1 : 0;
                    LabCatchUpCulled++;
                    continue;
                }
                if (done > 0 && _spentTicks + (t - t0) >= budget)
                {
                    LabCatchUpCut++;
                    break;
                }
                i = i + 1 < n ? i + 1 : 0;
                g.Frame = frame;
                float dt = now - g.At;
                g.At = now;
                // Played or stopped by its owner since (the next listing sees to it), or switched off with its object.
                if (dt <= 0f || !ps.isPaused || !ps.gameObject.activeInHierarchy)
                    continue;
                ps.Simulate(Mathf.Min(dt, CatchUpMax), false, false, false);
                if (cull)
                    Measure(g, ps);
                done++;
                t = System.Diagnostics.Stopwatch.GetTimestamp();
            }
            a.Cursor = i;
            long spent = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            _spentTicks += spent;
            LabCatchUpMs += spent * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (done > 0)
            {
                LabCatchUpRenders++;
                LabCatchUpSystems += done;
            }
        }

        // Far.InViewStep (FARWORLD-V3 §7 V0): with live windows a far side renders every frame, and what its view shows
        // must move at the game's frame rate, not by the turns a shared millisecond of catch-up gives it. The paused
        // systems in or near this render's view (as Far.ParticleCatchupCull tells them: their sphere after their last
        // step, CullMargin around the view; one never measured, or out of every view for CullRefreshSeconds, counts as in
        // it) step by the time since their last step, at most once a frame, without the shared millisecond. Those out of
        // view wait, owed at most CatchUpMax, for a render that sees them. At most InViewStepMax a render, and once the
        // frame's catch-ups (every render's) took InViewFrameBudgetMs a render steps one system and no more (a backstop
        // for many live windows at once): past either the rest step at its next render from where this one stopped (the
        // area's cursor), each in turn, still owed their time.
        void StepInView(GatedArea a, Plane[] view, int frame, float now)
        {
            List<Gated> list = a.Systems;
            int n = list.Count, i = a.Cursor < n ? a.Cursor : 0, done = 0, max = Mathf.Max(1, InViewStepMax);
            long budget = (long)(InViewFrameBudgetMs * System.Diagnostics.Stopwatch.Frequency / 1000.0);
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp(), t = t0;
            for (int k = 0; k < n; k++)
            {
                Gated g = list[i];
                ParticleSystem ps = g.Ps;
                if (!g.Mine || g.Interior || g.Frame == frame || ps == null)
                {
                    i = i + 1 < n ? i + 1 : 0;
                    continue;
                }
                if (g.Radius >= 0f && now - g.At < CullRefreshSeconds && Outside(view, g.Center, g.Radius + CullMargin))
                {
                    i = i + 1 < n ? i + 1 : 0;
                    LabCatchUpCulled++;
                    continue;
                }
                if (done >= max || (done > 0 && _spentTicks + (t - t0) >= budget))
                {
                    LabCatchUpCut++;
                    LabInViewCut++;
                    break;
                }
                i = i + 1 < n ? i + 1 : 0;
                g.Frame = frame;
                float dt = now - g.At;
                g.At = now;
                // Played or stopped by its owner since (the next listing sees to it), or switched off with its object.
                if (dt <= 0f || !ps.isPaused || !ps.gameObject.activeInHierarchy)
                    continue;
                ps.Simulate(Mathf.Min(dt, CatchUpMax), false, false, false);
                // Its sphere for the next render's test (also with Far.ParticleCatchupCull off).
                Measure(g, ps);
                done++;
                t = System.Diagnostics.Stopwatch.GetTimestamp();
            }
            a.Cursor = i;
            long spent = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            _spentTicks += spent;
            LabCatchUpMs += spent * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (done > 0)
            {
                LabCatchUpRenders++;
                LabCatchUpSystems += done;
                LabInViewSteps += done;
            }
        }

        // Far.ParticleCatchupCull: the view of the render under way, when it is the window's own on-screen view (the
        // renderer stores its frustum and camera position on the window right before BeforeRender); null for a nested view
        // (Recursion), whose camera is elsewhere: everything is advanced, as without the switch.
        static Plane[] ViewOf(PortalLink link, Vector3 camPos)
        {
            PortalWindow w = link.Window;
            if (w == null || !w.HasView)
                return null;
            Vector3 v = w.ViewPos;
            return v.x == camPos.x && v.y == camPos.y && v.z == camPos.z ? w.ViewFrustum : null;
        }

        // The sides and the near plane only (the far plane is the far clip, kilometres off; an oblique projection tilts it).
        static bool Outside(Plane[] view, Vector3 center, float radius)
        {
            for (int k = 0; k < 5; k++)
                if (view[k].GetDistanceToPoint(center) < -radius)
                    return true;
            return false;
        }

        // A sphere round what the system shows after an advance: its renderer's bounds (its particles) and its own position
        // (where new ones come from); just the position without a renderer or particles.
        static void Measure(Gated g, ParticleSystem ps)
        {
            if (!g.RendererLooked)
            {
                g.RendererLooked = true;
                g.Renderer = ps.GetComponent<ParticleSystemRenderer>();
            }
            Vector3 at = ps.transform.position;
            ParticleSystemRenderer r = g.Renderer;
            if (r != null)
            {
                Bounds b = r.bounds;
                Vector3 extents = b.extents;
                if (extents.x > 0f || extents.y > 0f || extents.z > 0f)
                {
                    g.Center = b.center;
                    g.Radius = Mathf.Max(extents.magnitude, Vector3.Distance(at, b.center));
                    return;
                }
            }
            g.Center = at;
            g.Radius = 0f;
        }

        // The worst frame of catch-ups, once that frame is over.
        void CloseCatchUpFrame(int frame)
        {
            if (_spentFrame < 0 || _spentFrame == frame)
                return;
            float ms = (float)(_spentTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            if (ms > LabCatchUpMaxMs)
                LabCatchUpMaxMs = ms;
            _spentFrame = -1;
            _spentTicks = 0;
        }

        // A few hundred a frame: entries gone with their object, let go, or no longer listed by any far side (its area
        // unloads, it moved off). What the gate still held then plays again.
        void Sweep(float now)
        {
            for (int k = 0; k < SweepPerFrame && _all.Count > 0; k++)
            {
                if (_sweep >= _all.Count)
                    _sweep = 0;
                Gated g = _all[_sweep];
                ParticleSystem ps = g.Ps;
                bool alive = ps != null;
                if (alive && g.Mine && now - g.ListedAt < StaleSeconds)
                {
                    _sweep++;
                    continue;
                }
                if (alive && g.Mine && ps.isPaused)
                    ps.Play(false);
                g.Mine = false;
                _gated.Remove(ps);
                int last = _all.Count - 1;
                _all[_sweep] = _all[last];
                _all.RemoveAt(last);
            }
        }

        // The switch off (or the world left): every system the gate holds plays again (those it took over from
        // RemoteAreas too, whose sleep dropped them while the gate was on), unless RemoteAreas has its far side asleep,
        // and it holds nothing. RemoteAreas' sleep takes the far particles back from here.
        void Release()
        {
            // A far side RemoteAreas has asleep keeps what the gate held there paused, handed to that sleep: played
            // here, it would run until a window looked there again (UpdateSight sleeps only an area not asleep yet).
            RemoteAreas areas = PortalSystem.Instance != null ? PortalSystem.Instance.Areas : null;
            if (areas != null)
                foreach (GatedArea a in _areaList)
                    if (!a.Running)
                        foreach (Gated g in a.Systems)
                            if (g.Mine && g.Ps != null && g.Ps.isPaused && areas.AdoptPaused(a.Portal, g.Ps))
                                g.Mine = false;
            foreach (Gated g in _all)
                if (g.Mine && g.Ps != null && g.Ps.isPaused)
                    g.Ps.Play(false);
            _all.Clear();
            _gated.Clear();
            _areas.Clear();
            _areaList.Clear();
            _livePoints.Clear();
            _live = null;
            s_live = null;
            _sweep = 0;
            _areasAt = 0f;
        }
    }
}
