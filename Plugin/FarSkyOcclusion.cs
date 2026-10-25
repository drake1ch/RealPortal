using System.Collections.Generic;
using UnityEngine;

namespace RealPortal
{
    // Shaders darken what stands under trees and roofs by a top-down depth picture of the sky above the player
    // (DepthCamera, redrawn every second: _SkyAlphaTexture at _SkyAlphaPosition). For a window the same picture is
    // taken above the far side and swapped in for its render.
    // Win.SkyPerArea off: one picture per window, redrawn once a second (windows that came into view together redraw
    // on the same frame). On: one per far area (the far portal a window looks out of), drawn when first needed and
    // then only when the point it is taken around has moved, when something it would show appeared or went under it
    // (a roof built, a tree felled, objects still arriving: Changed, from ZNetScene), while the area may still be
    // filling in (every 2.5 s for 40 s after it was first shown and again after each loading stage of the far area)
    // and every 30 s; never more often than the game redraws its own, at most one redraw a frame for all of them;
    // pictures nobody used for 30 s go. Win.SkyLive (with it): a picture a window on screen shows is redrawn every second
    // as the game's, and when a ship or cart under it moves; after a crossing the picture above where you now stand is
    // handed to the game's own sky camera until it redraws.
    internal sealed class FarSkyOcclusion
    {
        static readonly int s_texture = Shader.PropertyToID("_SkyAlphaTexture");
        static readonly int s_position = Shader.PropertyToID("_SkyAlphaPosition");

        sealed class View
        {
            public RenderTexture Texture;
            public Vector3 Position;
            public float RenderedAt = -10f;
            public float UsedAt;
        }

        sealed class AreaView
        {
            public RenderTexture Texture;
            public Vector3 Position;
            public bool Rendered;
            // Something it would show appeared or went under it since it was drawn.
            public bool Dirty;
            // The far area's loading stage it last saw (RemoteAreas.Stage).
            public int Stage;
            public float RenderedAt, CreatedAt, UsedAt;
            // Win.SkyLive: the vehicles under it when it was drawn, and where they stood; Dirty came from one of them.
            public readonly List<(Vehicle vehicle, Vector3 at)> Vehicles = new List<(Vehicle, Vector3)>();
            public bool VehicleDirty;
        }

        // Win.SkyLive: a ship or cart (they move, and keep rain off what is under them), and whether anything of it is
        // drawn into the sky picture (a layer the game's sky camera shows; -1 until looked at).
        sealed class Vehicle
        {
            public Transform T;
            public int OnLayers = -1;
        }

        // While a far area may still be filling in (objects keep arriving for a few seconds alone, up to ~40 s from a
        // server) its picture is redrawn every 2.5 s; after that every 30 s, or when the point moves.
        const float WarmSeconds = 40f, WarmRefresh = 2.5f, Refresh = 30f, Unused = 30f;
        // How far past the picture's edge an object can reach into it (a tree's crown, a roof's overhang).
        const float ObjectReach = 16f;

        readonly Dictionary<object, View> _views = new Dictionary<object, View>();
        readonly Dictionary<object, AreaView> _areas = new Dictionary<object, AreaView>();
        readonly List<object> _drop = new List<object>();
        DepthCamera _game;
        Camera _gameCam, _cam;
        Texture _savedTexture;
        Vector4 _savedPosition;
        bool _swapped;
        int _renderedFrame = -1;
        float _sweepAt;

        // Lab: sky pictures drawn so far, and how many are kept.
        internal static int LabRenders;
        // Lab (Win.SkyLive): far-area pictures redrawn while it is on (cumulative), of those the ones a moving vehicle
        // asked for, and pictures handed to the game's own sky camera after a crossing.
        internal static long LabSkyRedraws, LabSkyVehicleRedraws, LabSkyHandOffs;
        // The one PortalSystem keeps (for Changed).
        static FarSkyOcclusion s_instance;

        internal FarSkyOcclusion() => s_instance = this;
        internal int Count => _views.Count + _areas.Count;

        bool Ready()
        {
            if (_game == null)
            {
                _game = Object.FindFirstObjectByType<DepthCamera>();
                _gameCam = _game != null ? _game.GetComponent<Camera>() : null;
            }
            if (_gameCam == null || _game.m_depthShader == null || _game.m_texture == null)
                return false;
            if (_cam == null)
            {
                var go = new GameObject("RealPortalSkyOcclusion");
                Object.DontDestroyOnLoad(go);
                _cam = go.AddComponent<Camera>();
                _cam.enabled = false;
            }
            return true;
        }

        // Before a window renders: the picture above where its player stands.
        // view: what the picture is kept for (with Win.SkyPerArea the far area: the far portal's link).
        // mayRender: false where no picture may be drawn (inside a reflection probe's face): the one there is used.
        // stage: the loading stage of the far area it shows (RemoteAreas.Stage; Win.SkyPerArea only).
        internal void Show(object view, Vector3 player, bool mayRender = true, int stage = 0)
        {
            if (WindowFlags.SkyPerArea)
                ShowArea(view, player, mayRender, stage);
            else
                ShowPerWindow(view, player);
        }

        void ShowPerWindow(object view, Vector3 player)
        {
            if (!Ready())
                return;
            if (!_views.TryGetValue(view, out View v))
                _views[view] = v = new View();
            v.UsedAt = Time.time;
            RenderTexture game = _game.m_texture;
            if (v.Texture == null || v.Texture.width != game.width || v.Texture.height != game.height)
            {
                if (v.Texture != null)
                    PortalMath.Kill(v.Texture);
                v.Texture = new RenderTexture(game.descriptor) { name = "RealPortalSkyAlpha" };
                v.RenderedAt = -10f;
            }
            if (Time.time - v.RenderedAt >= _game.m_updateInterval)
            {
                v.RenderedAt = Time.time;
                v.Position = Render(v.Texture, player);
            }
            Swap(v.Texture, v.Position);
        }

        void ShowArea(object view, Vector3 player, bool mayRender, int stage)
        {
            if (!Ready())
            {
                Push(null, default);
                return;
            }
            float now = Time.time;
            if (!_areas.TryGetValue(view, out AreaView v))
                _areas[view] = v = new AreaView { CreatedAt = now, Stage = stage };
            v.UsedAt = now;
            // The far area got further with loading (the place around the portal stands, all of it stands): what
            // arrives from here on may still be filling in, as at first.
            if (v.Stage != stage)
            {
                v.Stage = stage;
                v.CreatedAt = now;
                v.Dirty = true;
            }
            RenderTexture game = _game.m_texture;
            if (v.Texture == null || v.Texture.width != game.width || v.Texture.height != game.height)
            {
                if (v.Texture != null)
                    PortalMath.Kill(v.Texture);
                v.Texture = new RenderTexture(game.descriptor) { name = "RealPortalSkyAlpha" };
                v.Rendered = false;
            }
            if (mayRender)
            {
                // A first picture is drawn at once (the window would show the game's, of another place); a redraw
                // waits for a frame without one.
                if (!v.Rendered)
                    RenderArea(v, player, now);
                else if (_renderedFrame != Time.frameCount && Due(v, player, now, WindowFlags.SkyLive && OnScreen(view)))
                {
                    if (WindowFlags.SkyLive)
                    {
                        LabSkyRedraws++;
                        if (v.VehicleDirty)
                            LabSkyVehicleRedraws++;
                    }
                    RenderArea(v, player, now);
                }
            }
            // Pushed either way, so every Hide gives back what its own Show found.
            Push(v.Rendered ? v.Texture : null, v.Position);
        }

        // Win.SkyLive: the window onto this far area (the far portal's link: its partner is the one you look through) is
        // on screen.
        static bool OnScreen(object view) => view is PortalLink far && far.Partner != null && WindowReach.OnScreen(far.Partner);

        // onScreen (Win.SkyLive): a window shows it on screen, so it is redrawn as often as the game redraws its own.
        bool Due(AreaView v, Vector3 player, float now, bool onScreen)
        {
            float age = now - v.RenderedAt;
            // Never more often than the game redraws its own.
            if (age < _game.m_updateInterval)
                return false;
            if (v.Dirty || onScreen)
                return true;
            // Moved far enough that the picture would be taken elsewhere.
            float span = _gameCam.orthographic ? _gameCam.orthographicSize : 50f;
            float move = Mathf.Max(2f, 0.1f * span);
            if ((Center(player) - v.Position).sqrMagnitude > move * move)
                return true;
            return age >= (now - v.CreatedAt < WarmSeconds ? WarmRefresh : Refresh);
        }

        void RenderArea(AreaView v, Vector3 player, float now)
        {
            v.Position = Render(v.Texture, player);
            v.Rendered = true;
            v.Dirty = false;
            v.VehicleDirty = false;
            v.RenderedAt = now;
            _renderedFrame = Time.frameCount;
            // Win.SkyLive: what stands under it now, for the moves that make it stale.
            v.Vehicles.Clear();
            if (WindowFlags.SkyLive)
                foreach (Vehicle x in s_vehicles)
                    if (x.T != null && Shows(x) && Under(v, x.T.position))
                        v.Vehicles.Add((x, x.T.position));
        }

        // ---- Win.SkyLive (Docs\PARITY-PLAN.md P31, P22) --------------------------------------------------------------

        // Ships and carts that exist (from ZNetScene's creations, and a search once when the switch goes on).
        static readonly List<Vehicle> s_vehicles = new List<Vehicle>();
        static readonly List<Renderer> s_vehicleRenderers = new List<Renderer>();
        static bool s_vehiclesFound;
        // A vehicle that moved this far under a picture makes it stale (it keeps rain off what is under it).
        const float VehicleMove = 1f;

        // A new object that is a vehicle joins the list (Changed sees it created and again destroyed; a destroyed one
        // drops out once Unity has it gone).
        static void NoteVehicle(GameObject go)
        {
            if (go.GetComponent<Ship>() == null && go.GetComponent<Vagon>() == null)
                return;
            Transform t = go.transform;
            foreach (Vehicle x in s_vehicles)
                if (x.T == t)
                    return;
            s_vehicles.Add(new Vehicle { T = t });
        }

        // Those there before the list was kept (once per switch-on: a scene search).
        static void FindVehicles()
        {
            s_vehiclesFound = true;
            foreach (Ship ship in Object.FindObjectsByType<Ship>(FindObjectsSortMode.None))
                NoteVehicle(ship.gameObject);
            foreach (Vagon cart in Object.FindObjectsByType<Vagon>(FindObjectsSortMode.None))
                NoteVehicle(cart.gameObject);
        }

        // Anything of it on a layer the game's sky camera draws (looked at once).
        bool Shows(Vehicle x)
        {
            if (x.OnLayers < 0)
            {
                int mask = _gameCam.cullingMask;
                bool on = (mask & (1 << x.T.gameObject.layer)) != 0;
                x.T.GetComponentsInChildren(true, s_vehicleRenderers);
                foreach (Renderer r in s_vehicleRenderers)
                    on |= r != null && (mask & (1 << r.gameObject.layer)) != 0;
                s_vehicleRenderers.Clear();
                x.OnLayers = on ? 1 : 0;
            }
            return x.OnLayers > 0;
        }

        // Within a picture taken at its position, or near enough to reach into it (as Changed measures).
        bool Under(AreaView v, Vector3 p)
        {
            float span = _gameCam.orthographic ? _gameCam.orthographicSize * Mathf.Max(1f, _gameCam.aspect) : 50f;
            float reach = span + ObjectReach;
            return Mathf.Abs(p.x - v.Position.x) <= reach && Mathf.Abs(p.z - v.Position.z) <= reach;
        }

        // Once a frame: a picture under which a vehicle moved more than VehicleMove, came or went is stale (redrawn at
        // the game's interval, at most one a frame, the next time a window shows it).
        void CheckVehicles()
        {
            for (int i = s_vehicles.Count - 1; i >= 0; i--)
                if (s_vehicles[i].T == null)
                {
                    s_vehicles[i] = s_vehicles[s_vehicles.Count - 1];
                    s_vehicles.RemoveAt(s_vehicles.Count - 1);
                }
            if (_gameCam == null || _areas.Count == 0)
                return;
            if (!s_vehiclesFound)
                FindVehicles();
            foreach (AreaView v in _areas.Values)
            {
                if (!v.Rendered || v.Dirty)
                    continue;
                bool stale = false;
                foreach ((Vehicle x, Vector3 at) in v.Vehicles)
                    if (x.T == null || !x.T.gameObject.activeInHierarchy || (x.T.position - at).sqrMagnitude > VehicleMove * VehicleMove)
                    {
                        stale = true;
                        break;
                    }
                for (int i = 0; i < s_vehicles.Count && !stale; i++)
                {
                    Vehicle x = s_vehicles[i];
                    if (!x.T.gameObject.activeInHierarchy || !Shows(x) || !Under(v, x.T.position))
                        continue;
                    stale = true;
                    foreach ((Vehicle y, Vector3 _) in v.Vehicles)
                        if (y == x)
                        {
                            stale = false;
                            break;
                        }
                }
                if (stale)
                {
                    v.Dirty = true;
                    v.VehicleDirty = true;
                }
            }
        }

        // P22: after a crossing the game's own sky picture is of the place you left until its next redraw (up to
        // DepthCamera.m_updateInterval). The picture a window took above where you now stand goes to the game's sky
        // camera in the crossing frame (its texture and position), for the game camera and every view after it; the game
        // draws its own over it at its next turn.
        Vector3 _playerAt;
        bool _playerKnown;
        // A move of the player this long within a frame is a crossing (or a teleport: then no picture is near).
        const float CrossJump = 8f;

        void HandOffOnCrossing()
        {
            Player p = Player.m_localPlayer;
            if (p == null)
            {
                _playerKnown = false;
                return;
            }
            Vector3 at = p.transform.position;
            bool jumped = _playerKnown && (at - _playerAt).sqrMagnitude > CrossJump * CrossJump;
            _playerAt = at;
            _playerKnown = true;
            if (!jumped || !Ready())
                return;
            // The picture taken nearest where the game would take its own now, within a quarter of its span (at least 8 m:
            // you moved on a little since the last window render took it).
            Vector3 want = Center(at);
            float span = _gameCam.orthographic ? _gameCam.orthographicSize : 50f;
            float best = Mathf.Max(8f, 0.25f * span);
            best *= best;
            RenderTexture texture = null;
            Vector3 position = default;
            foreach (AreaView v in _areas.Values)
                if (v.Rendered && v.Texture != null && (v.Position - want).sqrMagnitude < best)
                {
                    best = (v.Position - want).sqrMagnitude;
                    texture = v.Texture;
                    position = v.Position;
                }
            foreach (View v in _views.Values)
                if (v.Texture != null && v.RenderedAt >= 0f && (v.Position - want).sqrMagnitude < best)
                {
                    best = (v.Position - want).sqrMagnitude;
                    texture = v.Texture;
                    position = v.Position;
                }
            RenderTexture game = _game.m_texture;
            if (texture == null || game == null || texture.width != game.width || texture.height != game.height
                || !texture.IsCreated() || !game.IsCreated())
                return;
            if (game.format != texture.format || SystemInfo.copyTextureSupport == UnityEngine.Rendering.CopyTextureSupport.None)
                return;
            Graphics.CopyTexture(texture, game);
            Shader.SetGlobalTexture(s_texture, game);
            Shader.SetGlobalVector(s_position, position);
            LabSkyHandOffs++;
        }

        // Win.SkyPerArea: an object appeared or is going (ZNetScene creates or destroys it). A picture it stands in
        // (on a layer the game's sky picture shows) is redrawn on its next turn.
        internal static void Changed(GameObject go)
        {
            FarSkyOcclusion s = s_instance;
            // Win.SkyLive: ships and carts are kept track of (pictures under one that moves are redrawn).
            if (s != null && go != null && WindowFlags.SkyLive && WindowFlags.SkyPerArea && s_vehiclesFound)
                NoteVehicle(go);
            if (s == null || s._areas.Count == 0 || go == null || !WindowFlags.SkyPerArea || s._gameCam == null)
                return;
            if ((s._gameCam.cullingMask & (1 << go.layer)) == 0)
                return;
            Vector3 p = go.transform.position;
            float span = s._gameCam.orthographic ? s._gameCam.orthographicSize * Mathf.Max(1f, s._gameCam.aspect) : 50f;
            float reach = span + ObjectReach;
            foreach (AreaView v in s._areas.Values)
                if (v.Rendered && !v.Dirty && Mathf.Abs(p.x - v.Position.x) <= reach && Mathf.Abs(p.z - v.Position.z) <= reach)
                    v.Dirty = true;
        }

        // Whether Changed has anything to look at (spares the destroy hook its instance lookup).
        internal static bool Watching => s_instance != null && s_instance._areas.Count > 0 && WindowFlags.SkyPerArea;

        Vector3 Center(Vector3 player)
        {
            Vector3 at = player + Vector3.up * _game.m_offset;
            return new Vector3(Mathf.Round(at.x), Mathf.Round(at.y), Mathf.Round(at.z));
        }

        // As DepthCamera.RenderDepth, from above the far side. Returns where the picture was taken.
        Vector3 Render(RenderTexture target, Vector3 player)
        {
            Vector3 at = Center(player);
            _cam.CopyFrom(_gameCam);
            _cam.enabled = false;
            _cam.targetTexture = target;
            _cam.transform.SetPositionAndRotation(at, _gameCam.transform.rotation);
            float lodBias = QualitySettings.lodBias;
            QualitySettings.lodBias = 10f;
            _cam.RenderWithShader(_game.m_depthShader, "RenderType");
            QualitySettings.lodBias = lodBias;
            LabRenders++;
            return at;
        }

        // Win.SkyPerArea off, as before: what is set now is kept for Hide.
        void Swap(Texture texture, Vector3 position)
        {
            _savedTexture = Shader.GetGlobalTexture(s_texture);
            _savedPosition = Shader.GetGlobalVector(s_position);
            _swapped = true;
            Shader.SetGlobalTexture(s_texture, texture);
            Shader.SetGlobalVector(s_position, position);
        }

        // Win.SkyPerArea on: a probe face drawn while the game camera stands past a portal (its picture swapped in)
        // gives back that picture, not the game's.
        readonly List<(Texture texture, Vector4 position)> _saved = new List<(Texture, Vector4)>();

        // texture null: nothing of its own to show (the one set stays).
        void Push(Texture texture, Vector3 position)
        {
            // Shows without their Hide (never in a frame as it runs now) do not pile up.
            if (_saved.Count >= 8)
                _saved.Clear();
            _saved.Add((Shader.GetGlobalTexture(s_texture), Shader.GetGlobalVector(s_position)));
            if (texture == null)
                return;
            Shader.SetGlobalTexture(s_texture, texture);
            Shader.SetGlobalVector(s_position, position);
        }

        internal void Hide()
        {
            if (_saved.Count > 0)
            {
                (Texture texture, Vector4 position) = _saved[_saved.Count - 1];
                _saved.RemoveAt(_saved.Count - 1);
                Shader.SetGlobalTexture(s_texture, texture);
                Shader.SetGlobalVector(s_position, position);
                return;
            }
            if (!_swapped)
                return;
            _swapped = false;
            Shader.SetGlobalTexture(s_texture, _savedTexture);
            Shader.SetGlobalVector(s_position, _savedPosition);
        }

        // Once a frame: pictures nobody has used for a while go (their windows, areas or links are gone).
        internal void Sweep()
        {
            // Win.SkyLive: vehicles moving under far pictures, and the hand-off after a crossing.
            if (WindowFlags.SkyLive)
            {
                HandOffOnCrossing();
                if (WindowFlags.SkyPerArea)
                    CheckVehicles();
            }
            else
            {
                _playerKnown = false;
                if (s_vehiclesFound)
                {
                    s_vehiclesFound = false;
                    s_vehicles.Clear();
                }
            }
            float now = Time.time;
            if (now < _sweepAt)
                return;
            _sweepAt = now + 5f;
            _drop.Clear();
            foreach (var kv in _views)
                if (now - kv.Value.UsedAt > Unused * 2f)
                    _drop.Add(kv.Key);
            foreach (object key in _drop)
            {
                if (_views[key].Texture != null)
                    PortalMath.Kill(_views[key].Texture);
                _views.Remove(key);
            }
            _drop.Clear();
            foreach (var kv in _areas)
                if (now - kv.Value.UsedAt > Unused)
                    _drop.Add(kv.Key);
            foreach (object key in _drop)
            {
                if (_areas[key].Texture != null)
                    PortalMath.Kill(_areas[key].Texture);
                _areas.Remove(key);
            }
            _drop.Clear();
        }

        internal void Clear()
        {
            _playerKnown = false;
            s_vehiclesFound = false;
            s_vehicles.Clear();
            Hide();
            foreach (View v in _views.Values)
                if (v.Texture != null)
                    PortalMath.Kill(v.Texture);
            _views.Clear();
            foreach (AreaView v in _areas.Values)
                if (v.Texture != null)
                    PortalMath.Kill(v.Texture);
            _areas.Clear();
        }
    }
}
