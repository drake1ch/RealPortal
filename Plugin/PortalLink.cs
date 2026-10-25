using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace RealPortal
{
    // Added to every TeleportWorld (the wooden and stone portals, modded ones). Finds its partner (the vanilla
    // connection, or the inscribed link of a Path of Valheiman gate), keeps the opening geometry and the window in
    // sync, and stands an invisible wall in the opening for whoever may not pass.
    public sealed class PortalLink : MonoBehaviour
    {
        internal static readonly List<PortalLink> All = new List<PortalLink>();
        static readonly int s_woodPortal = "portal_wood".GetStableHashCode();

        internal readonly PortalFrame Frame = new PortalFrame();
        internal PortalWindow Window;
        internal PortalLink Partner;
        internal TeleportWorld Portal;

        ZNetView _nview;
        bool _real;
        float _nextCheck;
        bool _geometryLogged;
        bool _frameSet;
        Vector3 _framePos;
        Quaternion _frameRot;
        MonoBehaviour _ottoWindow;
        bool _ottoSearched;
        bool _ottoWasEnabled;

        internal bool IsReal => _real;

        // The partner's ZDO, loaded or not (the game knows every ZDO of the world).
        internal ZDO PartnerZdo;

        // A one-way link (a gate inscribed to a portal that leads elsewhere): the far portal's own window shows
        // another place, so this one teleports as before.
        internal bool OneWay;

        bool Linked => _real && !OneWay && Partner != null && Partner._real && Partner.isActiveAndEnabled && Partner.Partner == this;

        // What lies behind this live portal is wanted in full now: its window was seen in the last seconds, or you
        // are close enough to step through or to turn and see it.
        internal bool Watched
        {
            get
            {
                if (!IsActive)
                    return false;
                if (Window != null && Time.time - Window.LastRendered < 3f)
                    return true;
                Player p = Player.m_localPlayer;
                return p != null && (p.transform.position - Frame.Center).sqrMagnitude < 8f * 8f;
            }
        }

        // The game's own teleport runs here: no partner at all (Path of Valheiman's portal map still opens), a
        // one-way link, or the mod switched off.
        internal bool Vanilla => !_real || OneWay || PartnerZdo == null;

        // Set once the far end has everything around it loaded, and kept while both ends stay loaded: right after
        // you walk through, the side you came from is far now and its area is still being listed.
        bool _live;

        // Both ends real, loaded and live: the window shows the other side and walking through is seamless.
        internal bool IsActive => _live && Linked && Partner._live;

        // FARWORLD V2 (Docs\FARWORLD-V3.md §7 V2): the far area this link leads into, set by AreaPolicy every frame under
        // Far.V2 (null otherwise, or before it has seen this link).
        internal FarArea FarArea;

        // Far.Readiness3 (FarWorld\Readiness.cs): the window shows (CanShow, which is IsActive: going live is RemoteAreas'
        // CanGoLive as before), the far side takes thrown things and creatures (CanThrow), you may walk in (CanWalk, with
        // PortalSystem.PlayerMayPass's rules besides). With the switch off all three are IsActive.
        internal bool CanShow => IsActive;
        internal bool CanThrow => IsActive && Readiness.CanThrow(this);
        internal bool CanWalk => IsActive && Readiness.CanWalk(this);

        internal bool FrameSet => _frameSet;

        // The far end's opening: the partner's while it is loaded and measured, else (Far.VirtualEnd) its stand-in from
        // FrameCache at the far portal's saved pose, else null.
        internal PortalFrame FarFrame => Partner != null && Partner._frameSet ? Partner.Frame : FarWorld.VirtualEndOn ? VirtualFrame : null;

        PortalFrame VirtualFrame
        {
            get
            {
                FarArea a = FarAreas.Of(this);
                return a != null && a.End.Known ? a.End.Frame : null;
            }
        }

        void Awake()
        {
            Portal = GetComponent<TeleportWorld>();
            _nview = GetComponent<ZNetView>();
        }

        void OnEnable() => All.Add(this);

        void OnDisable()
        {
            All.Remove(this);
            Partner = null;
            _live = false;
            SetWindowVisible(false);
            if (_wall != null)
                _wall.gameObject.SetActive(false);
        }

        // ---- the wall ----------------------------------------------------------------------------------

        // A thin plate in the opening on the game's "blocker" layer: characters and dropped items bump into it, while
        // cameras, attacks, arrows and creatures' sight go through (none of them look at that layer). Whoever and
        // whatever may pass gets it ignored for their own colliders (PortalSystem.UpdateWalls). Not a child of the
        // portal: the portal's own colliders are searched for the frame's form.
        MeshCollider _wall;
        Mesh _wallMesh;
        bool _wallBuilt;
        // Looked up on first use: the development player refuses LayerMask lookups while a MonoBehaviour is being made.
        static int s_wallLayer = -2;
        internal static int WallLayer => s_wallLayer != -2 ? s_wallLayer : s_wallLayer = LayerMask.NameToLayer("blocker");

        internal Collider Wall => _wall != null && _wall.gameObject.activeSelf ? _wall : null;

        void UpdateWall()
        {
            bool want = _real && !Vanilla && _frameSet && WallLayer >= 0;
            if (!want)
            {
                if (_wall != null && _wall.gameObject.activeSelf)
                    _wall.gameObject.SetActive(false);
                return;
            }
            if (_wall == null)
            {
                var go = new GameObject("RealPortalWall") { layer = WallLayer };
                _wall = go.AddComponent<MeshCollider>();
                _wallBuilt = false;
            }
            if (!_wallBuilt)
            {
                // The opening's outline, 10 cm thick, as a convex collider: it fills the opening and nothing round it.
                const int n = 32;
                var pts = new Vector3[n];
                Frame.Outline(pts, n, 0f);
                var v = new Vector3[n * 2];
                Quaternion back = Quaternion.Inverse(Frame.Rotation);
                for (int i = 0; i < n; i++)
                {
                    Vector3 local = back * (pts[i] - Frame.Center);
                    v[i] = new Vector3(local.x, local.y, -0.05f);
                    v[n + i] = new Vector3(local.x, local.y, 0.05f);
                }
                if (_wallMesh == null)
                    _wallMesh = new Mesh { name = "RealPortalWall" };
                _wallMesh.Clear();
                _wallMesh.vertices = v;
                var tris = new int[(n - 2) * 6];
                for (int i = 0, k = 0; i < n - 2; i++)
                {
                    tris[k++] = 0; tris[k++] = i + 1; tris[k++] = i + 2;
                    tris[k++] = n; tris[k++] = n + i + 2; tris[k++] = n + i + 1;
                }
                _wallMesh.triangles = tris;
                _wall.sharedMesh = null;
                _wall.convex = true;
                _wall.sharedMesh = _wallMesh;
                _wallBuilt = true;
            }
            Transform t = _wall.transform;
            if (t.position != Frame.Center || t.rotation != Frame.Rotation)
                t.SetPositionAndRotation(Frame.Center, Frame.Rotation);
            if (!_wall.gameObject.activeSelf)
                _wall.gameObject.SetActive(true);
        }

        // ---- openings of different sizes --------------------------------------------------------------

        // What the clip below was worked out against: the partner, or (Far.VirtualEnd) its stand-in's frame; under
        // Far.VirtualEnd that far opening as it was then is kept in _clipStood.
        object _clipPartner;
        PortalFrame _clipStood;
        Vector3 _clipAt, _clipFarAt;
        Quaternion _clipRot, _clipFarRot;
        float _clipDrop, _clipFarDrop;

        // Lab (Far.VirtualEnd): clips worked out against a far portal's stand-in, and those kept as it came in (the same
        // opening, nothing measured again).
        internal static int LabClipsVirtual, LabClipsKept;

        // Where the partner's opening (carried through the pair) is smaller than this one, only the overlap goes
        // through (Frame.Clip): a stone arch paired with a wooden ring shows the ring's view in the middle of the
        // arch, and the rest of the arch is an ordinary opening onto this side with a wall in it.
        // Far.VirtualEnd: while the far portal is not loaded (or not measured yet), against its stand-in, so this is set up
        // before it loads and not worked out in the frame the window goes live.
        void UpdateClip()
        {
            PortalLink p = Partner;
            PortalFrame g = p != null && p._frameSet ? p.Frame : FarWorld.VirtualEndOn ? VirtualFrame : null;
            object source = p != null && p._frameSet ? (object)p : g;
            if (g == null || !_frameSet)
            {
                Frame.Clip = null;
                Frame.ViewFit = 1f;
                _clipPartner = null;
                return;
            }
            PortalFrame f = Frame;
            if (ReferenceEquals(source, _clipPartner) && f.Center == _clipAt && f.Rotation == _clipRot && f.BaseDrop == _clipDrop
                && g.Center == _clipFarAt && g.Rotation == _clipFarRot && g.BaseDrop == _clipFarDrop)
                return;
            bool real = ReferenceEquals(source, p);
            // Far.VirtualEnd: the far end went from its stand-in to the portal that came in (or back, as it unloads) with the
            // same opening: the clip worked out against the one holds for the other.
            if (FarWorld.VirtualEndOn && _clipPartner != null && _clipStood != null && f.Center == _clipAt && f.Rotation == _clipRot && f.BaseDrop == _clipDrop
                && VirtualEnd.SameOpening(_clipStood, g))
            {
                _clipPartner = source;
                _clipFarAt = g.Center;
                _clipFarRot = g.Rotation;
                _clipFarDrop = g.BaseDrop;
                LabClipsKept++;
                return;
            }
            if (!real)
                LabClipsVirtual++;
            if (FarWorld.VirtualEndOn)
                VirtualEnd.CopyOpening(g, _clipStood ??= new PortalFrame());
            _clipPartner = source;
            _clipAt = f.Center;
            _clipRot = f.Rotation;
            _clipDrop = f.BaseDrop;
            _clipFarAt = g.Center;
            _clipFarRot = g.Rotation;
            _clipFarDrop = g.BaseDrop;
            Frame.Clip = PortalFrame.Overlap(f, g);
            PortalFrame.FitView(f, g, out f.ViewFit, out f.ViewLift);
            _rimBuilt = false;
            string farName = real ? p.name : "the stand-in of its far portal";
            if (f.ViewFit < 0.999f)
                Plugin.Debug($"Portal {name}: the view onto {farName} is scaled by {f.ViewFit:0.00} from {PortalMath.ViewScaleTo} m away (lift {f.ViewLift:0.00} m)");
            if (Frame.Clip != null)
            {
                float min = Frame.Clip.Min(), max = Frame.Clip.Max();
                Plugin.Debug($"Portal {name}: its partner {farName}'s opening is smaller, only the overlap goes through ({min:0.00}..{max:0.00} m from the centre)");
            }
        }

        // The part of the opening outside the overlap, 10 cm thick, on the wall's layer: nothing walks or is thrown
        // through it (it would come out inside the far frame or under the ground there).
        MeshCollider _rim;
        Mesh _rimMesh;
        bool _rimBuilt;

        void UpdateRim()
        {
            bool want = _real && !Vanilla && _frameSet && Frame.Clip != null && IsActive && WallLayer >= 0;
            if (!want)
            {
                if (_rim != null && _rim.gameObject.activeSelf)
                    _rim.gameObject.SetActive(false);
                return;
            }
            if (_rim == null)
            {
                var go = new GameObject("RealPortalRim") { layer = WallLayer };
                _rim = go.AddComponent<MeshCollider>();
                _rimBuilt = false;
            }
            if (!_rimBuilt)
            {
                float[] clip = Frame.Clip;
                int n = clip.Length;
                var v = new List<Vector3>();
                var tris = new List<int>();
                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    float ai = Mathf.PI * 2f * i / n, aj = Mathf.PI * 2f * j / n;
                    Vector2 di = new Vector2(Mathf.Cos(ai), Mathf.Sin(ai)), dj = new Vector2(Mathf.Cos(aj), Mathf.Sin(aj));
                    float oi = OpeningRadius(di), oj = OpeningRadius(dj);
                    if (oi - clip[i] < 0.02f && oj - clip[j] < 0.02f)
                        continue;
                    foreach (float z in new[] { -0.05f, 0.05f })
                    {
                        int b = v.Count;
                        v.Add(di * clip[i]);
                        v.Add(dj * clip[j]);
                        v.Add(dj * oj);
                        v.Add(di * oi);
                        for (int k = b; k < b + 4; k++)
                            v[k] = new Vector3(v[k].x, v[k].y, z);
                        // Both faces.
                        tris.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3, b, b + 2, b + 1, b, b + 3, b + 2 });
                    }
                }
                if (_rimMesh == null)
                    _rimMesh = new Mesh { name = "RealPortalRim" };
                _rimMesh.Clear();
                _rimMesh.SetVertices(v);
                _rimMesh.SetTriangles(tris, 0);
                _rim.sharedMesh = null;
                _rim.convex = false;
                _rim.sharedMesh = _rimMesh;
                _rimBuilt = true;
            }
            Transform t = _rim.transform;
            if (t.position != Frame.Center || t.rotation != Frame.Rotation)
                t.SetPositionAndRotation(Frame.Center, Frame.Rotation);
            if (!_rim.gameObject.activeSelf)
                _rim.gameObject.SetActive(true);
        }

        // How far the opening's own outline reaches in a direction (local metres).
        float OpeningRadius(Vector2 d)
        {
            float lo = 0f, hi = Mathf.Max(Frame.HalfSize.x, Frame.HalfSize.y) * 1.5f + 0.1f;
            for (int k = 0; k < 20; k++)
            {
                float mid = (lo + hi) * 0.5f;
                if (Frame.InOpening(d * mid))
                    lo = mid;
                else
                    hi = mid;
            }
            return lo;
        }

        Renderer[] _own;
        float _ownAt = -10f;

        // This portal's own renderers (model, effects), not its window; refreshed every few seconds.
        internal Renderer[] OwnRenderers()
        {
            if (_own != null && Time.time - _ownAt < 5f)
                return _own;
            _ownAt = Time.time;
            Transform window = Window != null ? Window.Root.transform : null;
            _own = GetComponentsInChildren<Renderer>(true).Where(r => window == null || !r.transform.IsChildOf(window)).ToArray();
            return _own;
        }

        readonly List<Renderer> _nearby = new List<Renderer>();
        readonly HashSet<GameObject> _nearbyRoots = new HashSet<GameObject>();
        static readonly Collider[] s_overlap = new Collider[256];
        float _nearbyAt = -10f;

        // Renderers of the objects around this portal (refreshed every second), for hiding the ones that stand
        // between a window camera and this portal when it is the exit. Terrain is left out: its chunks reach
        // across the portal anyway.
        internal List<Renderer> NearbyRenderers()
        {
            if (Time.time - _nearbyAt < 1f)
                return _nearby;
            _nearbyAt = Time.time;
            _nearby.Clear();
            _nearbyRoots.Clear();
            int n = Physics.OverlapSphereNonAlloc(Frame.Center, 8f, s_overlap, ~0, QueryTriggerInteraction.Collide);
            for (int i = 0; i < n; i++)
            {
                Collider c = s_overlap[i];
                if (c == null || c.GetComponent<Heightmap>() != null)
                    continue;
                ZNetView owner = c.GetComponentInParent<ZNetView>();
                GameObject root = owner != null ? owner.gameObject : c.gameObject;
                if (!_nearbyRoots.Add(root))
                    continue;
                foreach (Renderer r in root.GetComponentsInChildren<Renderer>())
                    if (r.gameObject.name != "RealPortalWindow")
                        _nearby.Add(r);
            }
            return _nearby;
        }

        const int IgnoreRaycastLayer = 2;
        GameObject[] _colliderObjects;
        int[] _colliderLayers;
        bool _camHidden;

        // The camera's sphere casts would hit the teleport trigger (queries hit triggers) filling the opening
        // while the camera arm passes through the ring, pulling the camera onto the player. Hide it from them.
        internal void HideFromCamera()
        {
            if (_camHidden)
                return;
            if (_colliderObjects == null)
            {
                var objects = new List<GameObject>();
                // Only the teleport trigger: the wooden frame stays solid for the camera like any post, otherwise
                // a camera arm passing just beside the opening ends up on the far side of the portal.
                foreach (Collider c in GetComponentsInChildren<Collider>(true))
                    if (c.isTrigger && !objects.Contains(c.gameObject))
                        objects.Add(c.gameObject);
                _colliderObjects = objects.ToArray();
                _colliderLayers = new int[_colliderObjects.Length];
            }
            for (int i = 0; i < _colliderObjects.Length; i++)
            {
                GameObject go = _colliderObjects[i];
                if (go == null)
                    continue;
                _colliderLayers[i] = go.layer;
                go.layer = IgnoreRaycastLayer;
            }
            _camHidden = true;
        }

        internal void RestoreAfterCamera()
        {
            RestoreFrameAfterCamera();
            if (!_camHidden)
                return;
            for (int i = 0; i < _colliderObjects.Length; i++)
                if (_colliderObjects[i] != null)
                    _colliderObjects[i].layer = _colliderLayers[i];
            _camHidden = false;
        }

        GameObject[] _frameObjects;
        int[] _frameLayers;
        bool _frameHidden;
        float _frameMargin = -1f;

        // How far the wooden ring reaches past the opening (upper half; the base below stands on the ground).
        internal float FrameMargin
        {
            get
            {
                if (_frameMargin < 0f)
                    _frameMargin = MeasureFrame();
                return _frameMargin;
            }
        }

        float MeasureFrame()
        {
            float margin = 0.1f;
            Collider[] solid = GetComponentsInChildren<Collider>().Where(c => !c.isTrigger && c.enabled).ToArray();
            float reach = Mathf.Max(Frame.HalfSize.x, Frame.HalfSize.y) + 2f;
            for (int i = 0; i <= 12; i++)
            {
                float a = Mathf.PI * i / 12f;
                Vector3 dir = Frame.Rotation * new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                float edge = new Vector2(Mathf.Cos(a) * Frame.HalfSize.x, Mathf.Sin(a) * Frame.HalfSize.y).magnitude;
                // From outside in: the first wood met is the outer edge of the ring at this angle.
                var ray = new Ray(Frame.Center + dir * reach, -dir);
                float first = float.MaxValue;
                foreach (Collider c in solid)
                    if (c.Raycast(ray, out RaycastHit hit, reach) && hit.distance < first)
                        first = hit.distance;
                if (first < reach)
                    margin = Mathf.Max(margin, reach - first - edge);
            }
            margin = Mathf.Min(margin, 0.6f);
            Plugin.Log.LogInfo($"portal frame reaches {margin:F2} m past the opening");
            return margin;
        }

        // While the camera arm goes through this opening (or through the ring around it), the ring does not stop
        // it: otherwise the arm sliding over the rim as the player walks away snaps the camera onto the player.
        internal void HideFrameFromCamera()
        {
            if (_frameHidden)
                return;
            if (_frameObjects == null)
            {
                var objects = new List<GameObject>();
                foreach (Collider c in GetComponentsInChildren<Collider>(true))
                    if (!c.isTrigger && !objects.Contains(c.gameObject))
                        objects.Add(c.gameObject);
                _frameObjects = objects.ToArray();
                _frameLayers = new int[_frameObjects.Length];
            }
            for (int i = 0; i < _frameObjects.Length; i++)
            {
                GameObject go = _frameObjects[i];
                if (go == null)
                    continue;
                _frameLayers[i] = go.layer;
                go.layer = IgnoreRaycastLayer;
            }
            _frameHidden = true;
        }

        internal void RestoreFrameAfterCamera()
        {
            if (!_frameHidden)
                return;
            for (int i = 0; i < _frameObjects.Length; i++)
                if (_frameObjects[i] != null)
                    _frameObjects[i].layer = _frameLayers[i];
            _frameHidden = false;
        }

        void OnDestroy()
        {
            Window?.Destroy();
            Window = null;
            if (_wall != null)
                Destroy(_wall.gameObject);
            if (_wallMesh != null)
                Destroy(_wallMesh);
            if (_rim != null)
                Destroy(_rim.gameObject);
            if (_rimMesh != null)
                Destroy(_rimMesh);
        }

        // Called by PortalSystem every frame before rendering.
        internal void Refresh()
        {
            if (Time.time >= _nextCheck)
            {
                _nextCheck = Time.time + 0.5f;
                Check();
            }
            if (_real)
            {
                UpdateFrame();
                // Far.VirtualEnd: this prefab's opening, kept for far portals of it that are not loaded (once per measurement).
                if (!_frameLearned && _frameSet && FarWorld.VirtualEndOn)
                    LearnFrame();
            }
            UpdateClip();
            UpdateWall();
            UpdateRim();
            bool linked = Linked;
            // CanShow (Far.Readiness3 too): going live is RemoteAreas' CanGoLive as before; with objects still real (V2) its far
            // end is known whenever it is linked (the partner real and measured).
            RemoteAreas areas = PortalSystem.Instance != null ? PortalSystem.Instance.Areas : null;
            // Far.LiveRecheck (Docs\PARITY-PLAN.md P27): a link whose far end has an area is live only while that area may
            // show, checked every frame: one that drains, or is taken back and loads again, drops it (the portal shows the
            // vanilla look until it stands) instead of showing a half-unloaded place with invisible creatures. A link without
            // one (its far end in the player's own area), or with a new one round a place the game has loaded whole
            // (RemoteAreas.Rechecks), keeps the latch. Far.SideReady (P4.5): the half of its far side the window shows must
            // stand too, so a camera crossing to a half not standing yet drops it; without Far.LiveRecheck only that is
            // checked every frame (RemoteAreas.SideRechecks).
            if (linked && areas != null && (FarFlags.LiveRecheck ? areas.Rechecks(PartnerZdo) : areas.SideRechecks(PartnerZdo)))
                _live = _live ? areas.StaysLive(this) : areas.CanGoLive(this);
            else
                _live = linked && (_live || (areas != null && areas.CanGoLive(this)));
            bool active = IsActive;
            if (active)
            {
                if (Window == null)
                    Window = new PortalWindow(Frame, transform, Plugin.WindowMaterial, ModelLayer());
                Window.Partner = Partner.Window;
            }
            SetWindowVisible(active && !PortalSystem.Suspended);
            SetOttoWindow(!active);
        }

        void Check()
        {
            ZDO zdo = _nview != null && _nview.IsValid() ? _nview.GetZDO() : null;
            _real = Plugin.Enabled.Value && zdo != null;
            Partner = null;
            PartnerZdo = null;
            OneWay = false;
            if (!_real || ZDOMan.instance == null || ZNetScene.instance == null)
                return;
            ZDO far = PartnerOf(zdo);
            if (far == null)
                PortalServer.AskForPartner(zdo);
            PartnerZdo = far;
            ZNetView view = far != null ? ZNetScene.instance.FindInstance(far) : null;
            Partner = view != null ? view.GetComponent<PortalLink>() : null;
            if (Partner != null && !Partner._real)
                Partner.Check();
            // Both ends must lead to each other for a window (a gate may be inscribed to a portal paired elsewhere).
            OneWay = far != null && PartnerOf(far) != zdo;
            LogState(zdo, far, view);
        }

        // ---- partners ----------------------------------------------------------------------------------

        // Path of Valheiman's Inscription Gate (a wooden portal copy) leads to the portal standing at the place
        // written on it, before any connection by tag; a portal a gate leads to and with no connection of its own
        // leads back to it. Those places are read from the ZDOs, as that mod writes them.
        static readonly int s_goldGate = "PT_GoldlightGate".GetStableHashCode();

        internal static ZDO PartnerOf(ZDO zdo)
        {
            if (zdo == null || ZDOMan.instance == null)
                return null;
            if (zdo.GetPrefab() == s_goldGate && zdo.GetBool("pt_gold_has"))
                return PortalAt(zdo.GetVec3("pt_gold_pos", Vector3.zero), zdo);
            ZDOID id = zdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal);
            if (id != ZDOID.None)
                return ZDOMan.instance.GetZDO(id);
            if (zdo.GetPrefab() != s_goldGate && zdo.GetBool("pt_gold_back_has"))
                return PortalAt(zdo.GetVec3("pt_gold_back_pos", Vector3.zero), zdo);
            return null;
        }

        // Every portal ZDO of the world, by sector (the game's own list, read directly: that mod leaves gates without
        // a name out of GetPortalList).
        static readonly System.Reflection.FieldInfo s_portalObjects = HarmonyLib.AccessTools.Field(typeof(ZDOMan), "m_portalObjects");

        static ZDO PortalAt(Vector3 pos, ZDO not)
        {
            if (!(s_portalObjects?.GetValue(ZDOMan.instance) is System.Collections.IDictionary bySector))
                return null;
            foreach (object list in bySector.Values)
                if (list is List<ZDO> zdos)
                    foreach (ZDO z in zdos)
                        if (z != not && z.IsValid() && (z.GetPosition() - pos).sqrMagnitude < 0.25f)
                            return z;
            return null;
        }

        string _lastState;

        // One line whenever the pairing state of a portal changes.
        void LogState(ZDO zdo, ZDO far, ZNetView view)
        {
            string state = $"tag '{zdo.GetString(ZDOVars.s_tag)}', far ZDO {(far != null ? far.m_uid + " at " + Vector3.Distance(transform.position, far.GetPosition()).ToString("0") + " m" : "none")}" +
                           $", far instance {(view != null ? "loaded" : "not loaded")}" +
                           $", partner points back {Partner != null && Partner.Partner == this}, one-way {OneWay}, active {IsActive}";
            if (state == _lastState)
                return;
            _lastState = state;
            Plugin.Debug($"Portal {zdo.m_uid} ({name}): {state}");
        }

        void SetWindowVisible(bool visible)
        {
            if (Window == null)
                return;
            Window.Enabled = visible;
            if (Window.Root.activeSelf != visible)
                Window.Root.SetActive(visible);
        }

        int ModelLayer() => Portal != null && Portal.m_model != null ? Portal.m_model.gameObject.layer : gameObject.layer;

        // The opening, measured once (and again if the portal moves): the wooden portal and copies of it (the same
        // model) are a disc; any other frame is measured by rays from the middle of its teleport trigger out to its
        // own solid colliders (the stone portal's arch, modded frames); failing that, the trigger's box.
        void UpdateFrame()
        {
            Transform root = transform;
            if (_frameSet && root.position == _framePos && root.rotation == _frameRot)
                return;
            _frameSet = true;
            _framePos = root.position;
            _frameRot = root.rotation;
            _frameMargin = -1f;
            _wallBuilt = false;
            Frame.Rotation = Quaternion.Euler(0f, root.eulerAngles.y, 0f);
            Frame.Shape = null;
            string source;
            if (WoodLike())
            {
                Transform model = Portal.m_model != null ? Portal.m_model.transform : root;
                Frame.Center = model.TransformPoint(new Vector3(0f, Plugin.WoodCenterHeight.Value, 0f));
                float r = Plugin.WoodDiameter.Value * 0.5f;
                Frame.HalfSize = new Vector2(r, r);
                Frame.Round = true;
                source = "wood disc";
            }
            else if (MeasureShape(root, out source))
            {
            }
            else if (!TriggerBox(root, out source))
            {
                Frame.Center = root.position + Vector3.up * 1.5f;
                Frame.HalfSize = new Vector2(1.2f, 1.5f);
                Frame.Round = false;
                source = "default box";
            }
            Frame.BaseDrop = Frame.Center.y - FloorHeight(root);
            _frameSource = source;
            _frameLearned = false;
            if (!_geometryLogged)
            {
                _geometryLogged = true;
                Vector3 local = root.InverseTransformPoint(Frame.Center);
                Plugin.Debug($"Portal {name} ({source}): centre local {local}, half size {Frame.HalfSize}, round {Frame.Round}, centre {Frame.BaseDrop:0.00} m above the floor, " +
                             $"partner {(Partner != null ? Partner.name : "none")} at {Vector3.Distance(root.position, Partner != null ? Partner.transform.position : root.position):0.0} m");
            }
        }

        // How the opening was last measured (UpdateFrame), and whether FrameCache has had it since.
        string _frameSource;
        bool _frameLearned;

        // Far.VirtualEnd: the measured opening handed to FrameCache under this portal's prefab.
        void LearnFrame()
        {
            _frameLearned = true;
            ZDO zdo = _nview != null && _nview.IsValid() ? _nview.GetZDO() : null;
            if (zdo != null)
                FrameCache.Learn(zdo.GetPrefab(), Utils.GetPrefabName(gameObject), transform.position, transform.rotation, Frame, _frameSource);
        }

        // What you walk on through the opening: the ground the portal stands on, or the frame's own step when it
        // has one (the stone portal's plinth), straight under the opening's centre.
        float FloorHeight(Transform root)
        {
            float floor = root.position.y;
            var down = new Ray(Frame.Center, Vector3.down);
            float reach = Frame.Center.y - floor + 0.5f;
            foreach (Collider c in GetComponentsInChildren<Collider>(true))
                if (!c.isTrigger && c.enabled && c.gameObject.activeInHierarchy && c.Raycast(down, out RaycastHit hit, reach) && hit.point.y > floor)
                    floor = hit.point.y;
            return floor;
        }

        static Mesh s_woodMesh;

        // The wooden portal, or a copy of it with the same model (Path of Valheiman's Inscription Gate).
        bool WoodLike()
        {
            ZDO zdo = _nview != null ? _nview.GetZDO() : null;
            if (zdo != null && zdo.GetPrefab() == s_woodPortal)
                return true;
            if (s_woodMesh == null && ZNetScene.instance != null)
            {
                GameObject wood = ZNetScene.instance.GetPrefab("portal_wood");
                TeleportWorld tw = wood != null ? wood.GetComponent<TeleportWorld>() : null;
                s_woodMesh = tw != null && tw.m_model != null ? tw.m_model.GetComponent<MeshFilter>()?.sharedMesh : null;
            }
            Mesh mine = Portal != null && Portal.m_model != null ? Portal.m_model.GetComponent<MeshFilter>()?.sharedMesh : null;
            return mine != null && mine == s_woodMesh;
        }

        const int ShapePoints = 48;

        // Rays in the portal's plane from the middle of its teleport trigger out to the frame's solid colliders, one
        // per angle step: the inner edge of the frame all round. Directions that meet nothing (an open bottom) take
        // their neighbours' distance. The window reaches 5 cm under the frame so no gap shows along the edge.
        bool MeasureShape(Transform root, out string source)
        {
            source = null;
            var trigger = GetComponentInChildren<TeleportWorldTrigger>(true);
            Collider tc = trigger != null ? trigger.GetComponent<Collider>() : null;
            if (tc == null)
                return false;
            Vector3 c = tc is BoxCollider box ? box.transform.TransformPoint(box.center) : tc.bounds.center;
            Collider[] solid = GetComponentsInChildren<Collider>(true).Where(x => !x.isTrigger && x.enabled && x.gameObject.activeInHierarchy).ToArray();
            if (solid.Length == 0)
                return false;
            var r = new float[ShapePoints];
            int hits = 0;
            for (int i = 0; i < ShapePoints; i++)
            {
                float a = Mathf.PI * 2f * i / ShapePoints;
                var ray = new Ray(c, Frame.Rotation * new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f));
                float best = float.MaxValue;
                foreach (Collider col in solid)
                    if (col.Raycast(ray, out RaycastHit hit, 8f) && hit.distance < best)
                        best = hit.distance;
                r[i] = best < float.MaxValue ? best : -1f;
                if (r[i] > 0f)
                    hits++;
            }
            if (hits < ShapePoints * 0.6f)
                return false;
            for (int i = 0; i < ShapePoints; i++)
                if (r[i] < 0f)
                {
                    // Nearest measured neighbours on both sides.
                    float before = -1f, after = -1f;
                    for (int k = 1; k < ShapePoints && before < 0f; k++)
                        before = r[(i - k + ShapePoints) % ShapePoints];
                    for (int k = 1; k < ShapePoints && after < 0f; k++)
                        after = r[(i + k) % ShapePoints];
                    r[i] = -Mathf.Max(before, after);
                }
            float max = 0f;
            for (int i = 0; i < ShapePoints; i++)
            {
                r[i] = Mathf.Abs(r[i]) + 0.05f;
                max = Mathf.Max(max, r[i]);
            }
            Frame.Center = c;
            Frame.HalfSize = new Vector2(max, max);
            Frame.Round = true;
            var shape = new Vector2[ShapePoints];
            for (int i = 0; i < ShapePoints; i++)
            {
                float a = Mathf.PI * 2f * i / ShapePoints;
                shape[i] = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * (r[i] / max);
            }
            Frame.Shape = shape;
            source = $"measured outline ({hits}/{ShapePoints} rays met the frame; left {r[ShapePoints / 2]:0.00} m, right {r[0]:0.00} m, up {r[ShapePoints / 4]:0.00} m, down {r[3 * ShapePoints / 4]:0.00} m)";
            return true;
        }

        bool TriggerBox(Transform root, out string source)
        {
            source = null;
            var trigger = GetComponentInChildren<TeleportWorldTrigger>(true);
            Collider col = trigger != null ? trigger.GetComponent<Collider>() : null;
            if (col == null)
                return false;
            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            if (col is BoxCollider box)
            {
                Vector3 h = box.size * 0.5f;
                for (int i = 0; i < 8; i++)
                {
                    var c = new Vector3((i & 1) == 0 ? -h.x : h.x, (i & 2) == 0 ? -h.y : h.y, (i & 4) == 0 ? -h.z : h.z);
                    Vector3 p = root.InverseTransformPoint(box.transform.TransformPoint(box.center + c));
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
                source = "trigger box";
            }
            else
            {
                Bounds b = col.bounds;
                min = root.InverseTransformPoint(b.min);
                max = root.InverseTransformPoint(b.max);
                source = "trigger bounds";
            }
            Vector3 centre = (min + max) * 0.5f;
            Frame.Center = root.TransformPoint(new Vector3(centre.x, centre.y, centre.z));
            Frame.HalfSize = new Vector2(Mathf.Abs(max.x - min.x) * 0.5f, Mathf.Abs(max.y - min.y) * 0.5f);
            Frame.Round = false;
            return Frame.HalfSize.x > 0.2f && Frame.HalfSize.y > 0.2f;
        }

        // OttoBifrost draws its own preview disc in the same opening; switch it off while this portal is real.
        void SetOttoWindow(bool enabled)
        {
            if (_ottoWindow == null)
            {
                if (enabled || _ottoSearched)
                    return;
                _ottoSearched = true;
                foreach (MonoBehaviour mb in GetComponents<MonoBehaviour>())
                {
                    System.Type t = mb.GetType();
                    if (t.Name == "PortalWindow" && t.Namespace != null && t.Namespace.StartsWith("OttoBifrost"))
                    {
                        _ottoWindow = mb;
                        _ottoWasEnabled = mb.enabled;
                        break;
                    }
                }
                if (_ottoWindow == null)
                    return;
            }
            bool want = enabled && _ottoWasEnabled;
            if (_ottoWindow.enabled != want)
                _ottoWindow.enabled = want;
            if (!enabled)
            {
                Transform model = Portal != null && Portal.m_model != null ? Portal.m_model.transform : transform;
                foreach (Transform child in model)
                    if (child.name == "OttoBifrostWindow" && child.TryGetComponent(out MeshRenderer r) && r.enabled)
                        r.enabled = false;
            }
        }
    }
}
