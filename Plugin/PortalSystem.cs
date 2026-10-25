using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.PostProcessing;
using UnityEngine.Rendering;
using UnityStandardAssets.ImageEffects;

namespace RealPortal
{
    // One per game session: renders the windows, moves the local player, dropped items and projectiles across
    // real portals, and carries the third-person camera through an opening.
    public sealed partial class PortalSystem : MonoBehaviour
    {
        internal static PortalSystem Instance;
        // Test hook: windows hidden and not rendered (the portals still pair, effects stay hidden).
        internal static bool Suspended;
        internal int RendersLastFrameForLab => _renderer.RendersLastFrame;
        internal Camera WindowCamera => _renderer.Camera;
        internal PortalRenderer Renderer => _renderer;
        internal int CameraJumpsForLab => _renderer.JumpsSeen;
        internal RenderTexture MotionForLab => _renderer.MotionThisFrame ? _renderer.MotionTexture : null;

        // The game camera's motion vectors fixed up for the windows this frame, for its TAA.
        internal RenderTexture MotionFor(Camera cam) =>
            cam != null && cam == _main && _renderer.MotionThisFrame && !LabTaaOwnMotion ? _renderer.MotionTexture : null;

        // Lab: the game's TAA left with the camera's own motion vectors (before it got ours).
        internal static bool LabTaaOwnMotion;
        // Lab: the TAA jitter the window views got this frame (screen units).
        internal Vector2 JitterForLab => _settings.Jitter;
        // Lab: whole-screen motion vectors on every frame (to check them against the camera's own).
        internal static bool LabMotionScreenAlways;
        internal static bool LabStrictJump;
        // Lab: weather and mist particles stay at the game camera for window renders (as before).
        internal static bool LabFollowersStay;
        // Lab: parts of the far-side setup off, to find what costs a frame.
        // Lab: called right before the windows render and right after (the leak check).
        internal static System.Action LabBeforeWindows, LabAfterWindows;
        internal static bool LabNoGrass, LabNoFarTerrain, LabNoCarry, LabNoPlayerGlobals, LabNoNetRef, LabNoSkyOcclusion, LabNoFitBounds;

        // Lab: the same hiding a window render does, for the game camera standing at the window camera's place.
        internal void LabHideBehindExit(PortalWindow entry, Vector3 camPos)
        {
            ShowBehindExit();
            HideBehindExit(entry, camPos);
        }

        internal void LabShowBehindExit() => ShowBehindExit();
        internal bool JitteredAtPreCull;
        // Lab (Win.ExplicitJitter's check, FARWORLD-V3 §7 V0): frames the game's TAA jittered its camera, and of those the
        // frames the window views could not take that frame's jitter at MainPreCull (the projection not jittered yet, or
        // by another offset than the TAA's of this frame: another jitter index than the screen around the windows), with
        // the latest such frame. Counted on every frame whatever the switches; the lab asserts JitterMisses stays 0.
        internal int JitterTaaFrames, JitterMisses, JitterLastMissFrame = -1;
        static readonly AccessTools.FieldRef<PostProcessingBehaviour, TaaComponent> s_taa =
            AccessTools.FieldRefAccess<PostProcessingBehaviour, TaaComponent>("m_Taa");
        Camera _taaCamera;
        PostProcessingBehaviour _taaPost;
        // Lab: let the game camera fog the window surface too (the behaviour before the fog copy was separate).
        internal static bool LabGameFogOnWindow;
        // Lab: LOD picked with the window rect's own field of view (the behaviour before the correction).
        internal static bool LabOldLod;
        // Lab: reset TAA and motion blur when the camera is carried through a portal (the behaviour before
        // continuous motion vectors).
        internal static bool LabResetTemporal;
        // Lab: TAA and motion blur read the windows as flat surfaces (before they got the depth seen through them).
        internal static bool LabPlaneDepthForEffects;
        // Lab: how much of the game camera's TAA jitter the window views get (1 = all of it).
        internal static float LabJitterScale = 1f;
        // Lab (Docs\RIPPLE-PLAN.md L3): the depth the game's TAA dilates its motion vectors by, put on its material in
        // Patches.TaaMotion: 0 as things are (once another value has been used, the global put back on the material every
        // frame), 1 the depth seen through the windows (ViewDepthFor, else the global), 2 the windows as the flat surfaces
        // they are (PlaneDepthTexture, else the global).
        internal static int LabTaaDepth;
        // Lab (L3): which depth and motion-vector textures the game camera's TAA gets, counted in Patches.TaaMotion into
        // TaaInputs (the TaaIn* indices) while on; off, nothing is read. Global: bound globally as TAA runs (Unity's own
        // bound again since AfterForwardAlpha, or ours); read: what its material samples (its own texture where one was
        // set on it, else the global). Depth over the frames with a view depth, motion over those with fixed-up vectors;
        // the last texture counted "other" of each, for its name.
        internal static bool LabTaaInputs;
        internal const int TaaInFrames = 0, TaaInViewFrames = 1, TaaInGlobalDepthView = 2, TaaInGlobalDepthPlane = 3, TaaInGlobalDepthOther = 4,
            TaaInDepthView = 5, TaaInDepthPlane = 6, TaaInDepthOther = 7, TaaInMotionFrames = 8, TaaInGlobalMotionOurs = 9, TaaInGlobalMotionOther = 10,
            TaaInMotionOurs = 11, TaaInMotionOther = 12, TaaInCount = 13;
        internal readonly int[] TaaInputs = new int[TaaInCount];
        internal Texture TaaInOtherDepth, TaaInOtherMotion;
        // Lab (L5): > 0, the sun's shadow map resolution (Light.shadowCustomResolution) for each window render, the game's
        // put back right after it (over Win.ShadowRes's size, through the same hooks).
        internal static int LabShadowRes;
        Light _shadowResLight;
        int _shadowResWas;

        static readonly AccessTools.FieldRef<Character, Vector3> s_currentVel = AccessTools.FieldRefAccess<Character, Vector3>("m_currentVel");
        static readonly AccessTools.FieldRef<Character, Quaternion> s_lookYaw = AccessTools.FieldRefAccess<Character, Quaternion>("m_lookYaw");
        static readonly AccessTools.FieldRef<Character, Vector3> s_lookDir = AccessTools.FieldRefAccess<Character, Vector3>("m_lookDir");
        static readonly AccessTools.FieldRef<Character, Vector3> s_moveDir = AccessTools.FieldRefAccess<Character, Vector3>("m_moveDir");
        static readonly AccessTools.FieldRef<Character, float> s_maxAirAltitude = AccessTools.FieldRefAccess<Character, float>("m_maxAirAltitude");
        static readonly AccessTools.FieldRef<Character, Vector3> s_lastPos = AccessTools.FieldRefAccess<Character, Vector3>("m_lastPos");
        static readonly AccessTools.FieldRef<Character, Vector3> s_lazyPos = AccessTools.FieldRefAccess<Character, Vector3>("m_lazyPos");
        static readonly AccessTools.FieldRef<Character, Vector3> s_lastGroundPoint = AccessTools.FieldRefAccess<Character, Vector3>("m_lastGroundPoint");
        static readonly AccessTools.FieldRef<Player, Vector3> s_queuedDodgeDir = AccessTools.FieldRefAccess<Player, Vector3>("m_queuedDodgeDir");
        static readonly AccessTools.FieldRef<GameCamera, Vector3> s_camPlayerPos = AccessTools.FieldRefAccess<GameCamera, Vector3>("m_playerPos");
        static readonly AccessTools.FieldRef<GameCamera, Vector3> s_camPlayerVel = AccessTools.FieldRefAccess<GameCamera, Vector3>("m_playerVel");
        static readonly AccessTools.FieldRef<GameCamera, Vector3> s_camBaseOffset = AccessTools.FieldRefAccess<GameCamera, Vector3>("m_currentBaseOffset");
        static readonly AccessTools.FieldRef<GameCamera, Vector3> s_camBaseVel = AccessTools.FieldRefAccess<GameCamera, Vector3>("m_offsetBaseVel");

        internal const float PlayerRefHeight = 1f;

        readonly PortalRenderSettings _settings = new PortalRenderSettings();
        readonly List<PortalWindow> _windows = new List<PortalWindow>();
        readonly List<PortalLink> _active = new List<PortalLink>();
        readonly Collider[] _hits = new Collider[64];
        Dictionary<Rigidbody, Vector3> _itemLast = new Dictionary<Rigidbody, Vector3>();
        Dictionary<Rigidbody, Vector3> _itemNow = new Dictionary<Rigidbody, Vector3>();
        PortalRenderer _renderer;
        Camera _main;
        Vector3 _playerLast;
        Player _playerTracked;
        float _blockedMessageAt;
        bool _loggedCamera;
        float _statNext;
        int _statFrames, _statRenders;
        long _statTicks;
        int _itemMask;

        // Saved pose of the player's visual while a window camera renders it at the far side.
        Transform _movedVisual;
        Vector3 _visualPos, _visualScale;
        Quaternion _visualRot;

        internal IReadOnlyList<PortalLink> Active => Live();

        // The live links, without any the game destroyed since Update listed them (an area being unloaded can
        // take a portal away between Update and the render of the same frame).
        List<PortalLink> Live()
        {
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                PortalLink l = _active[i];
                if (l == null || l.Window == null || l.Partner == null || l.Partner.Window == null)
                    _active.RemoveAt(i);
            }
            return _active;
        }

        // The places around far portals, kept loaded while the player is near their partner.
        internal readonly RemoteAreas Areas = new RemoteAreas();

        internal static void Ensure()
        {
            if (Instance != null)
                return;
            var go = new GameObject("RealPortalSystem");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<PortalSystem>();
        }

        void Awake()
        {
            // BeforePlainRender: the far proxies reach window renders without the pipeline's slot or nested events too.
            _renderer = new PortalRenderer(_settings)
            {
                BeforeRender = BeforeWindowRender, AfterRender = AfterWindowRender, BeforePlainRender = ProxyDraw.BeforePlain,
            };
            _settings.MinNear = PlayerNear;
            _settings.KeepOwnMotion = PlayerRenderers;
            _itemMask = LayerMask.GetMask("item");
            Camera.onPreCull += MainPreCull;
            Camera.onPostRender += MainPostRender;
            GraphicsSettingsManager.GraphicsSettingsChanged += GraphicsSettingsChanged;
            BindRenderOrder();
            // Window Pipeline v2 (Windows\WindowGlue.cs): inert until its parts are implemented.
            InitWindowPipeline();
            // Win.ShieldDome (P24): the dome pass of the window cameras (their onPreCull).
            FarShieldDome.Install();
        }

        void OnDestroy()
        {
            Camera.onPreCull -= MainPreCull;
            Camera.onPostRender -= MainPostRender;
            GraphicsSettingsManager.GraphicsSettingsChanged -= GraphicsSettingsChanged;
            FarEnvironment.Restore();
            FarMist.Hide();
            if (_probeRenderSet)
                ProbeRenderDone();
            DisposeWindowPipeline();
            // The slot cameras went with the manager's pool (which no longer reports them): their fog buffers, the
            // engine's grass exclusions and its GrassMainCameraOnly as it was before us.
            FogCommandBuffer.Clear();
            AoSync.Clear();
            FarGrassSubmit.Clear();
            BaseView.Clear();
            FarShieldDome.Clear();
            // Win.CrossingBodies, Win.FarShafts: bodies put back, the sun masks' buffer and textures.
            PutBack(_crossMoved);
            PutBack(_crossMain);
            BaseView.Crossing.Clear();
            ClearShafts();
            _renderer?.Destroy();
            WindowTextures.Clear();
            // Far.VirtualEnd: portal frames learnt since the last write (the game quits).
            FrameCache.Flush();
            if (Instance == this)
                Instance = null;
        }

        void Update()
        {
            // Far.ArrivalPacer (lab): this frame's own time, kept when it is the one a jump of the reference position is
            // seen in (Areas.ArrivalFrameDone).
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            WindowTextures.InWindow = false;
            WindowTextures.Sweep();
            // FARWORLD V2 (Far.V2; FarWorld\FarWorld.cs): the registry's frame before the far areas use it. Off: one test.
            FarWorld.Update();
            Areas.Update();
            FarFreeze.Update(Areas);
            _active.Clear();
            for (int i = 0; i < PortalLink.All.Count; i++)
            {
                PortalLink link = PortalLink.All[i];
                link.Refresh();
            }
            _byWindow.Clear();
            foreach (PortalLink link in PortalLink.All)
                if (link.IsActive && link.Window != null && link.Partner.Window != null)
                {
                    _active.Add(link);
                    _byWindow[link.Window] = link;
                }
            // FARWORLD V2: far areas, their contexts and readiness (FarWorld\AreaPolicy.cs) from the links as they stand now,
            // before anything crosses this frame. Off: one test.
            FarWorld.UpdateAreas(this);
            // Physics may have carried the player through an opening after the last FixedUpdate check; catch it
            // before the camera is placed, or it looks out of the far side for a frame while you are still here.
            if (Live().Count > 0)
            {
                UpdatePlayer();
                WakeInSight();
                AutoPickupAcross();
            }
            // Far.ParticleWindowGate: far effects paused but on the live window's far side, decided before this frame's
            // particle update (and run down once the switch is off).
            if (FarFlags.ParticleWindowGate || _farParticles.Gating)
                _farParticles.Gate(this);
            Areas.ArrivalFrameDone(began);
        }

        static readonly AccessTools.FieldRef<Player, int> s_autoPickupMask = AccessTools.FieldRefAccess<Player, int>("m_autoPickupMask");
        static readonly System.Reflection.FieldInfo s_autoPickupOn = AccessTools.Field(typeof(Player), "m_enableAutoPickup");
        readonly Collider[] _pickupHits = new Collider[32];

        // The game pulls loose items within reach to you and picks them up; standing at a portal, also those over
        // there within that reach of where you stand as seen through it (not what may not go through).
        void AutoPickupAcross()
        {
            Player p = Player.m_localPlayer;
            if (p == null || !Plugin.Reach.Value || (s_autoPickupOn != null && !(bool)s_autoPickupOn.GetValue(null)) || p.IsTeleporting() || p.IsDead())
                return;
            float range = p.m_autoPickupRange;
            Vector3 at = p.transform.position + Vector3.up;
            foreach (PortalLink link in Live())
            {
                // Far.Readiness3: only from a far side that takes thrown things (CanThrow), as items come through it.
                if (Vector3.Distance(at, link.Frame.Center) > range + Mathf.Max(link.Frame.HalfSize.x, link.Frame.HalfSize.y) || !Readiness.CanThrow(link))
                    continue;
                PortalFrame exit = link.Partner.Frame;
                Vector3 there = PortalMath.Pair(link.Frame, exit).MultiplyPoint3x4(at);
                float side = exit.SignedDistance(there);
                int n = Physics.OverlapSphereNonAlloc(there, range, _pickupHits, s_autoPickupMask(p));
                for (int i = 0; i < n; i++)
                {
                    Rigidbody body = _pickupHits[i].attachedRigidbody;
                    ItemDrop drop = body != null ? body.GetComponent<ItemDrop>() : null;
                    if (drop == null || !drop.m_autoPickup || drop.IsPiece() || !drop.GetComponent<ZNetView>().IsValid())
                        continue;
                    // Only what lies past the exit, where the window shows it.
                    if (exit.SignedDistance(drop.transform.position) * side > 0f || PortalReach.Forbidden(drop.m_itemData, link))
                        continue;
                    if (!drop.CanPickup())
                    {
                        drop.RequestOwn();
                        continue;
                    }
                    if (!p.GetInventory().CanAddItem(drop.m_itemData) || drop.m_itemData.GetWeight() + p.GetInventory().GetTotalWeight() > p.GetMaxCarryWeight())
                        continue;
                    float d = Vector3.Distance(drop.transform.position, there);
                    if (d > range)
                        continue;
                    if (d < 0.3f)
                        p.Pickup(drop.gameObject);
                    else
                        drop.transform.position += (there - drop.transform.position).normalized * 15f * Time.deltaTime;
                }
            }
        }

        // A window about to come into view (in range, and within the camera's view widened by 30 degrees for a
        // quick turn): what sleeps behind it wakes before it is drawn. With Far.ParticleWindowGate that is what
        // RemoteAreas froze (creatures); far effects stay paused and are advanced before each render instead.
        void WakeInSight()
        {
            Camera cam = _main;
            if (cam == null)
                return;
            Vector3 eye = cam.transform.position, fwd = cam.transform.forward;
            float half = Mathf.Max(cam.fieldOfView, Camera.VerticalToHorizontalFieldOfView(cam.fieldOfView, cam.aspect)) * 0.5f + 30f;
            float cos = Mathf.Cos(Mathf.Min(half, 179f) * Mathf.Deg2Rad);
            // As far as windows render (WindowReach.Render: Window.MaxDistance, or with Win.DistantLive the live range).
            float reach = WindowReach.Render + 10f;
            foreach (PortalLink link in _active)
            {
                Vector3 to = link.Frame.Center - eye;
                float d = to.magnitude;
                if (d > reach)
                    continue;
                if (d < 5f || Vector3.Dot(to / d, fwd) > cos)
                    Areas.WakeFor(link.PartnerZdo);
            }
        }

        readonly Dictionary<PortalWindow, PortalLink> _byWindow = new Dictionary<PortalWindow, PortalLink>();
        readonly List<Renderer> _hidden = new List<Renderer>();
        PostProcessingBehaviour _fog;
        PostProcessingProfile _fogProfile;

        // The game's distance fog is a post effect on its camera (PostProcessing v1 Fog, deferred only). The
        // window camera gets its own copy with nothing else enabled; the rest of the post stack runs once, on the
        // game camera, over the whole frame including the window.
        void SyncFog(GameCamera gameCamera)
        {
            PostProcessingBehaviour game = gameCamera.GetComponent<PostProcessingBehaviour>();
            if (_fog == null)
            {
                _fogProfile = ScriptableObject.CreateInstance<PostProcessingProfile>();
                _fog = _renderer.Camera.gameObject.AddComponent<PostProcessingBehaviour>();
                _fog.profile = _fogProfile;
            }
            // The window camera fogs its own view: the game tints fog by the view direction against the sun, and
            // through a portal that direction is turned. The game camera's fog sees the window as right at it.
            bool on = Plugin.Fog.Value && game != null && game.profile != null && game.profile.fog.enabled;
            if (_fogProfile.fog.enabled != on)
                _fogProfile.fog.enabled = on;
            if (_fog.enabled != on)
                _fog.enabled = on;
            if (on)
                _fogProfile.fog.settings = game.profile.fog.settings;
        }

        // NearPlane clipping keeps whatever lies beyond the near plane but still on the camera's side of the exit
        // portal; hide the renderers that are wholly there (a rock or wall right behind the exit) for this render.
        void HideBehindExit(PortalWindow entry, Vector3 camPos)
        {
            if (!_byWindow.TryGetValue(entry.Partner, out PortalLink exit))
                return;
            // The exit's own frame would show inside the window, round its edge (seen from the far side the ring's
            // inner faces, at an angle, or all of a smaller portal's frame in a larger opening): left out of the view.
            // What lies at its floor stays (the stone portal's plinth, walked onto on the way through).
            float floor = exit.Frame.Center.y - exit.Frame.BaseDrop + 0.3f;
            // Win.ExitShadows (P13): read once for the render, so ShowBehindExit undoes what this did.
            bool shadows = WindowFlags.ExitShadows;
            if (shadows && _exitShadowFrame != Time.frameCount)
            {
                _exitShadowFrame = Time.frameCount;
                LabExitShadowCasters = 0;
            }
            foreach (Renderer r in exit.OwnRenderers())
            {
                if (r == null || !r.enabled || r.forceRenderingOff || r.bounds.max.y < floor || (shadows && r.shadowCastingMode == ShadowCastingMode.ShadowsOnly))
                    continue;
                HideFromView(r, shadows);
            }
            if (Plugin.Clip.Value != PortalClip.NearPlane)
                return;
            PortalFrame f = exit.Frame;
            Vector3 n = f.Forward;
            float camSide = Mathf.Sign(f.SignedDistance(camPos));
            foreach (Renderer r in exit.NearbyRenderers())
            {
                if (r == null || !r.enabled || r.forceRenderingOff || !r.gameObject.activeInHierarchy || (shadows && r.shadowCastingMode == ShadowCastingMode.ShadowsOnly))
                    continue;
                Bounds b = r.bounds;
                float reach = Mathf.Abs(n.x) * b.extents.x + Mathf.Abs(n.y) * b.extents.y + Mathf.Abs(n.z) * b.extents.z;
                if (camSide * f.SignedDistance(b.center) <= reach)
                    continue;
                HideFromView(r, shadows);
            }
        }

        // Win.ExitShadows (P13): out of the view but still in the sun's shadow map, as it is standing there (the exit's frame
        // and a wall right behind the exit shade the ground the window shows). Only what casts a shadow is kept as a caster
        // (ShadowsOnly, its own mode back in ShowBehindExit); the rest is switched off as before. A renderer that casts
        // shadows only is left alone: it shows nothing in the view anyway. Off: switched off, shadow and all.
        void HideFromView(Renderer r, bool shadows)
        {
            if (shadows)
            {
                ShadowCastingMode mode = r.shadowCastingMode;
                if (mode == ShadowCastingMode.On || mode == ShadowCastingMode.TwoSided)
                {
                    r.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
                    _shadowOnly.Add((r, mode));
                    LabExitShadowCasters++;
                    return;
                }
            }
            r.forceRenderingOff = true;
            _hidden.Add(r);
        }

        void ShowBehindExit()
        {
            foreach (Renderer r in _hidden)
                if (r != null)
                    r.forceRenderingOff = false;
            _hidden.Clear();
            foreach ((Renderer r, ShadowCastingMode mode) in _shadowOnly)
                if (r != null)
                    r.shadowCastingMode = mode;
            _shadowOnly.Clear();
        }

        // Win.ExitShadows: the renderers this render keeps as shadow casters only, with the mode each had.
        readonly List<(Renderer, ShadowCastingMode)> _shadowOnly = new List<(Renderer, ShadowCastingMode)>();
        int _exitShadowFrame = -1;
        // Lab (Win.ExitShadows): renderers the window renders of this frame left out of the view as shadow casters only.
        internal static int LabExitShadowCasters;

        // The game's sun shafts work in screen space, from where the sun shows on screen and on the colour-graded
        // image. A copy on the window camera saw the image before grading (and drew next to nothing), so the game
        // camera itself draws a second pass for the sun seen through the largest window (Patches.SunShaftsTwice).
        void SyncSunShafts(GameCamera gameCamera)
        {
            SunShafts game = gameCamera.GetComponent<SunShafts>();
            _settings.WindowSunShafts = Plugin.SunShafts.Value && game != null && game.enabled;
        }

        internal void MixWindowShafts(RenderTexture outside, RenderTexture inside, RenderTexture destination) =>
            _renderer.MixInside(outside, inside, destination);

        // For the game camera's sun shafts while its final effects see through the windows: its depth with the
        // windows as their flat surfaces.
        internal RenderTexture PlaneDepthFor(Component effect) =>
            _main != null && effect.gameObject == _main.gameObject && _renderer.ViewDepthThisFrame && _settings.ViewDepthForFinalEffects
                ? _renderer.PlaneDepthTexture : null;

        // For the game camera's depth of field: its depth with the windows at the depth seen through them.
        internal RenderTexture ViewDepthFor(Component effect) =>
            _main != null && effect.gameObject == _main.gameObject && _renderer.ViewDepthThisFrame ? _renderer.ViewDepthTexture : null;

        // Lab (L3, Patches.TaaMotion): the depth LabTaaDepth puts on the TAA material of `cam`; null: the global.
        internal RenderTexture LabTaaDepthFor(Camera cam)
        {
            if (cam == null || cam != _main || !_renderer.ViewDepthThisFrame)
                return null;
            return LabTaaDepth == 1 ? _renderer.ViewDepthTexture : LabTaaDepth == 2 ? _renderer.PlaneDepthTexture : null;
        }

        // Lab (L3, Patches.TaaMotion while LabTaaInputs is on): one TAA frame of `cam`, what was bound globally and what
        // its material reads, against this frame's view depth, plane depth and fixed-up motion vectors. No allocation.
        internal void CountTaaInputs(Camera cam, Texture globalDepth, Texture readDepth, Texture globalMotion, Texture readMotion)
        {
            if (cam == null || cam != _main)
                return;
            int[] c = TaaInputs;
            c[TaaInFrames]++;
            if (_renderer.ViewDepthThisFrame)
            {
                c[TaaInViewFrames]++;
                RenderTexture view = _renderer.ViewDepthTexture, plane = _renderer.PlaneDepthTexture;
                c[globalDepth != null && globalDepth == view ? TaaInGlobalDepthView : globalDepth != null && globalDepth == plane ? TaaInGlobalDepthPlane : TaaInGlobalDepthOther]++;
                c[readDepth != null && readDepth == view ? TaaInDepthView : readDepth != null && readDepth == plane ? TaaInDepthPlane : TaaInDepthOther]++;
                if (globalDepth == null || (globalDepth != view && globalDepth != plane))
                    TaaInOtherDepth = globalDepth;
            }
            if (_renderer.MotionThisFrame)
            {
                c[TaaInMotionFrames]++;
                RenderTexture ours = _renderer.MotionTexture;
                c[globalMotion != null && globalMotion == ours ? TaaInGlobalMotionOurs : TaaInGlobalMotionOther]++;
                c[readMotion != null && readMotion == ours ? TaaInMotionOurs : TaaInMotionOther]++;
                if (globalMotion == null || globalMotion != ours)
                    TaaInOtherMotion = globalMotion;
            }
        }

        // A ray (the depth of field's autofocus) that carries on through the opening of a live portal it meets.
        internal bool RaycastThrough(Vector3 from, Vector3 dir, float max, int mask, out float distance)
        {
            distance = 0f;
            float travelled = 0f;
            PortalLink skip = null;
            for (int hop = 0; hop < 3; hop++)
            {
                float left = max - travelled;
                Vector3 to = from + dir * left;
                PortalLink best = null;
                float bestT = float.MaxValue;
                Vector3 bestHit = default;
                foreach (PortalLink link in Live())
                    if (link != skip && link.Frame.Crosses(from, to, out Vector3 hit, out float t) && t < bestT)
                    {
                        best = link;
                        bestT = t;
                        bestHit = hit;
                    }
                if (best == null)
                {
                    // Nothing in the way but the scene: the game's own cast (also from the far side).
                    bool found = hop == 0 ? Physics.Raycast(from, dir, out RaycastHit h, left, mask)
                                          : Physics.Raycast(from, dir, out h, left, mask, QueryTriggerInteraction.Ignore);
                    distance = travelled + (found ? h.distance : 0f);
                    return found;
                }
                float toPortal = bestT * left;
                // The portals' own trigger volumes are not in the way of what is seen through them.
                if (Physics.Raycast(from, dir, out RaycastHit before, toPortal, mask, QueryTriggerInteraction.Ignore))
                {
                    distance = travelled + before.distance;
                    return true;
                }
                Matrix4x4 m = PortalMath.Pair(best.Frame, best.Partner.Frame);
                from = m.MultiplyPoint3x4(bestHit);
                dir = m.MultiplyVector(dir).normalized;
                travelled += toPortal;
                skip = best.Partner;
            }
            return false;
        }

        // For the game camera's sun shafts: where the sun seen through the largest window shows to it.
        internal bool WindowSun(SunShafts shafts, out Vector3 sunPosition, out RenderTexture skyDepth)
        {
            sunPosition = default;
            skyDepth = _renderer.SkyDepthTexture;
            PortalWindow w = _renderer.LargestWindow;
            if (_main == null || shafts.gameObject != _main.gameObject || !_renderer.SkyDepthThisFrame || w == null || w.Partner == null
                || shafts.sunTransform == null || skyDepth == null)
                return false;
            // The scene behind the exit shows at entry = inverse(pair) * exit.
            sunPosition = PortalMath.Pair(w.Frame, w.Partner.Frame).inverse.MultiplyPoint3x4(shafts.sunTransform.position);
            return true;
        }

        // ---- Win.FarShafts (P33) --------------------------------------------------------------------------------------
        // The game's sun shafts work on its camera's finished image: from where its sun shows on screen, the sky round it is
        // smeared outward (Patches.SunShaftsTwice runs them). Before, a window got this side's shafts over its pixels and, the
        // largest window alone, a second chain from the player's sun carried back through its pair, in the player's colour.
        // Standing there the game draws only the far side's sun, in the far side's light. Now:
        //  - a window's pixels come from the image before this side's shafts (those rays are this side's air);
        //  - each window's sun is the light its own view was last rendered with (FarEnvironment set the far side's light
        //    for that render: its colour, and its direction carried back through the pair to where it shows on screen);
        //  - windows whose sun shows at the same place in the same colour (the same pair rotation and far light) share one
        //    chain, which reads only their sky (the renderer's sky depth under their mask) and is mixed into only their
        //    pixels (their mask, drawn by the windows' MASK pass after the game camera's transparent queue, as the renderer
        //    draws its own);
        //  - a window joins only when its sky can reach its sun (LeanShafts' gate, per window: within the shafts' radius of
        //    that sun on screen, the sun in front of the camera); at most MaxShaftChains chains a frame, the largest windows'
        //    first, and the windows left out keep the image before shafts.
        // Planned right after the window batch (MainPreCull: the windows shown, the camera final), nothing while the game's
        // shafts are off. One chain over every window shown uses the renderer's own mask and sky. Off: the old path.
        struct FarSun
        {
            public Color Color;
            // Toward that light in the far side's world (the game's sun stands opposite its light's forward).
            public Vector3 Toward;
            public int Frame;
        }

        internal sealed class ShaftChain
        {
            // Where its sun stands for the game camera (as far off as the game's), its colour, the place on screen.
            public Vector3 Sun;
            public Color Color;
            public Vector2 At;
            // Its windows' largest share of the screen (the order chains are kept in).
            public float Area;
            // Its windows' mask; null: every window shown is in this chain, and the renderer's mask and sky serve.
            public RenderTexture Mask;
            public readonly List<PortalWindow> Windows = new List<PortalWindow>();
        }

        const int MaxShaftChains = 4;
        // Suns this close on screen (viewport units, under a texel of the shafts' buffers) and in colour are one chain.
        const float SameSunAt = 0.002f, SameSunColor = 0.004f;
        readonly Dictionary<PortalWindow, FarSun> _farSuns = new Dictionary<PortalWindow, FarSun>();
        readonly List<PortalWindow> _farSunsGone = new List<PortalWindow>();
        readonly List<ShaftChain> _chains = new List<ShaftChain>();
        readonly List<RenderTexture> _chainMasks = new List<RenderTexture>();
        readonly CommandBuffer _shaftMasks = new CommandBuffer { name = "RealPortal window sun masks" };
        Camera _shaftMasksOn, _shaftsCamera;
        SunShafts _shaftsOf;
        RenderTexture _ownAllMask;
        Texture _allMask;
        Material _shaftsUtil;
        Shader _shaftsShader;
        Texture2D _nearDepth;
        int _mixPass = -1, _maskPass = -1, _chainCount, _shaftsFrame = -1;
        float _farSunsSweepAt;
        static readonly int s_rpInside = Shader.PropertyToID("_RP_Inside");
        static readonly int s_rpWindowMask = Shader.PropertyToID("_RP_WindowMask");
        // Lab (Win.FarShafts): chains planned for the last frame; masks drawn so far.
        internal static int LabShaftChains;
        internal static long LabShaftMasks;

        // For Patches.SunShaftsTwice: whether this frame's chains are planned for these shafts (the game camera's).
        internal bool FarShaftsPlanned(SunShafts shafts) =>
            _shaftsFrame == Time.frameCount && _main != null && shafts != null && shafts.gameObject == _main.gameObject;

        internal int ShaftChainCount => _chainCount;
        internal ShaftChain ShaftChainAt(int i) => _chains[i];
        // Where every window shown is (they take the image before this side's shafts).
        internal Texture ShaftsAllMask => _allMask;

        // Win.FarShafts: the light this window's own view was lit by (FarEnvironment has set the far side's by now).
        void NoteFarSun(PortalWindow entry)
        {
            EnvMan em = EnvMan.instance;
            Light light = em != null ? em.m_dirLight : null;
            if (light == null)
                return;
            _farSuns[entry] = new FarSun { Color = light.color, Toward = -light.transform.forward, Frame = Time.frameCount };
        }

        void PlanShafts(Camera cam)
        {
            _chainCount = 0;
            _shaftsFrame = -1;
            _allMask = null;
            LabShaftChains = 0;
            if (!WindowFlags.FarShafts)
            {
                DetachShaftMasks();
                if (_farSuns.Count > 0)
                    _farSuns.Clear();
                return;
            }
            _shaftMasks.Clear();
            SweepFarSuns();
            if (!ReferenceEquals(cam, _shaftsCamera) || _shaftsOf == null)
            {
                _shaftsCamera = cam;
                _shaftsOf = cam.GetComponent<SunShafts>();
            }
            SunShafts shafts = _shaftsOf;
            if (shafts == null || !shafts.enabled || shafts.sunTransform == null)
                return;
            PortalWindow first = null;
            int shown = 0;
            for (int i = 0; i < _windows.Count; i++)
                if (_windows[i] != null && _windows[i].RenderedThisFrame)
                {
                    shown++;
                    if (first == null)
                        first = _windows[i];
                }
            if (shown == 0 || !EnsureShaftsUtil(first))
                return;
            AttachShaftMasks(cam);
            // The windows' sky this frame (Plugin.SunShafts: the renderer draws it only for window suns), with its mask.
            bool sky = _renderer.SkyDepthThisFrame && _renderer.SkyDepthTexture != null && _renderer.WindowMaskTexture != null;
            bool drawn = false;
            if (sky)
            {
                _allMask = _renderer.WindowMaskTexture;
                PlanChains(cam, shafts);
                int inChains = 0;
                for (int i = 0; i < _chainCount; i++)
                    inChains += _chains[i].Windows.Count;
                if (_chainCount == 1 && inChains == shown)
                    _chains[0].Mask = null;
                else
                    for (int i = 0; i < _chainCount; i++)
                    {
                        while (_chainMasks.Count <= i)
                            _chainMasks.Add(null);
                        RenderTexture mask = _chainMasks[i] = ScreenMask(_chainMasks[i], cam);
                        DrawMask(mask, _chains[i].Windows);
                        _chains[i].Mask = mask;
                        drawn = true;
                    }
            }
            else
            {
                // No window sun this frame: only the windows' mask, for the image before this side's shafts.
                _ownAllMask = ScreenMask(_ownAllMask, cam);
                _shaftMasks.SetRenderTarget(_ownAllMask);
                _shaftMasks.ClearRenderTarget(false, true, Color.clear);
                for (int i = 0; i < _windows.Count; i++)
                {
                    PortalWindow w = _windows[i];
                    if (w != null && w.RenderedThisFrame)
                        _shaftMasks.DrawRenderer(w.Renderer, w.Material, 0, _maskPass);
                }
                LabShaftMasks++;
                _allMask = _ownAllMask;
                drawn = true;
            }
            if (drawn)
                _shaftMasks.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            _shaftsFrame = Time.frameCount;
            LabShaftChains = _chainCount;
        }

        // The chains of this frame (into _chains[0.._chainCount)), largest windows first, at most MaxShaftChains.
        void PlanChains(Camera cam, SunShafts shafts)
        {
            EnvMan em = EnvMan.instance;
            Light light = em != null ? em.m_dirLight : null;
            Vector3 eye = cam.transform.position;
            Vector3 toSun = shafts.sunTransform.position - eye;
            float dist = toSun.magnitude;
            if (dist < 1e-3f)
                return;
            toSun /= dist;
            // The game's sun stands opposite its light's forward (EnvMan.SetEnv); should it be another object, by the turn
            // between the two.
            Quaternion offset = light != null ? Quaternion.FromToRotation(-light.transform.forward, toSun) : Quaternion.identity;
            Matrix4x4 vp = cam.projectionMatrix * cam.worldToCameraMatrix;
            float reach = shafts.maxRadius + Patches.ShaftsSlack;
            bool gate = WindowFlags.LeanFixed && WindowFlags.LeanShafts;
            List<PortalLink> live = Live();
            int count = 0;
            for (int i = 0; i < live.Count; i++)
            {
                PortalWindow w = live[i].Window;
                if (!w.RenderedThisFrame || w.DepthTexture == null || w.Partner == null)
                    continue;
                Color color;
                Vector3 toward;
                if (_farSuns.TryGetValue(w, out FarSun far))
                {
                    color = far.Color;
                    toward = far.Toward;
                }
                else
                {
                    // Not rendered since the switch went on: the player's light.
                    color = shafts.sunColor;
                    toward = light != null ? -light.transform.forward : toSun;
                }
                // That light as the game camera sees it through the window: carried back through the pair.
                Quaternion back = Quaternion.Inverse(PortalMath.Rotation(PortalMath.Pair(w.Frame, w.Partner.Frame)));
                Vector3 sun = eye + back * (offset * toward) * dist;
                Vector3 at = cam.WorldToViewportPoint(sun);
                // Behind the camera the game's chain adds nothing (its sun colour is zero then).
                if (at.z < 0f)
                    continue;
                if (gate)
                {
                    Rect r = Patches.ScreenRectOf(w.Renderer.bounds, vp);
                    float dx = Mathf.Max(0f, Mathf.Max(r.xMin - at.x, at.x - r.xMax));
                    float dy = Mathf.Max(0f, Mathf.Max(r.yMin - at.y, at.y - r.yMax));
                    if (dx * dx + dy * dy >= reach * reach)
                        continue;
                }
                ShaftChain chain = null;
                for (int k = 0; k < count; k++)
                {
                    ShaftChain c = _chains[k];
                    if (Mathf.Abs(c.At.x - at.x) < SameSunAt && Mathf.Abs(c.At.y - at.y) < SameSunAt && Mathf.Abs(c.Color.r - color.r) < SameSunColor
                        && Mathf.Abs(c.Color.g - color.g) < SameSunColor && Mathf.Abs(c.Color.b - color.b) < SameSunColor)
                    {
                        chain = c;
                        break;
                    }
                }
                if (chain == null)
                {
                    if (_chains.Count <= count)
                        _chains.Add(new ShaftChain());
                    chain = _chains[count++];
                    chain.Sun = sun;
                    chain.Color = color;
                    chain.At = new Vector2(at.x, at.y);
                    chain.Area = 0f;
                    chain.Mask = null;
                    chain.Windows.Clear();
                }
                chain.Windows.Add(w);
                chain.Area = Mathf.Max(chain.Area, w.TextureRect.width * w.TextureRect.height);
            }
            // Largest first (a few chains: insertion order).
            for (int i = 1; i < count; i++)
            {
                ShaftChain c = _chains[i];
                int j = i - 1;
                for (; j >= 0 && _chains[j].Area < c.Area; j--)
                    _chains[j + 1] = _chains[j];
                _chains[j + 1] = c;
            }
            _chainCount = Mathf.Min(count, MaxShaftChains);
        }

        // A chain's windows into its mask (the windows' MASK pass: 1 where the window is what the camera sees).
        void DrawMask(RenderTexture mask, List<PortalWindow> windows)
        {
            _shaftMasks.SetRenderTarget(mask);
            _shaftMasks.ClearRenderTarget(false, true, Color.clear);
            for (int i = 0; i < windows.Count; i++)
                _shaftMasks.DrawRenderer(windows[i].Renderer, windows[i].Material, 0, _maskPass);
            LabShaftMasks++;
        }

        static RenderTexture ScreenMask(RenderTexture rt, Camera cam)
        {
            if (rt != null && rt.width == cam.pixelWidth && rt.height == cam.pixelHeight)
                return rt;
            if (rt != null)
                PortalMath.Kill(rt);
            return new RenderTexture(cam.pixelWidth, cam.pixelHeight, 0, RenderTextureFormat.R8) { name = "RealPortal window sun mask" };
        }

        // The window shader's MIX and MASK passes (by name), on a material of our own for the mixes.
        bool EnsureShaftsUtil(PortalWindow w)
        {
            Shader shader = w != null && w.Material != null ? w.Material.shader : null;
            if (shader == null)
                return false;
            if (_shaftsUtil == null || _shaftsShader != shader)
            {
                if (_shaftsUtil != null)
                    Destroy(_shaftsUtil);
                _shaftsShader = shader;
                _shaftsUtil = new Material(shader) { name = "RealPortal window sun util", hideFlags = HideFlags.DontSave };
                _mixPass = _shaftsUtil.FindPass("MIX");
                _maskPass = _shaftsUtil.FindPass("MASK");
            }
            return _mixPass >= 0 && _maskPass >= 0;
        }

        void AttachShaftMasks(Camera cam)
        {
            if (_shaftMasksOn != null && ReferenceEquals(_shaftMasksOn, cam))
                return;
            DetachShaftMasks();
            cam.AddCommandBuffer(CameraEvent.AfterForwardAlpha, _shaftMasks);
            _shaftMasksOn = cam;
        }

        void DetachShaftMasks()
        {
            if (_shaftMasksOn != null)
                _shaftMasksOn.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha, _shaftMasks);
            _shaftMasksOn = null;
            _shaftMasks.Clear();
        }

        // Windows not rendered for ten seconds or so leave the far suns now and then.
        void SweepFarSuns()
        {
            if (Time.unscaledTime < _farSunsSweepAt)
                return;
            _farSunsSweepAt = Time.unscaledTime + 5f;
            foreach (KeyValuePair<PortalWindow, FarSun> kv in _farSuns)
                if (kv.Value.Frame < Time.frameCount - 600)
                    _farSunsGone.Add(kv.Key);
            for (int i = 0; i < _farSunsGone.Count; i++)
                _farSuns.Remove(_farSunsGone[i]);
            _farSunsGone.Clear();
        }

        // A chain's sky: the renderer's sky depth under its mask, near elsewhere (into a temporary for the caller to
        // release), or the renderer's own when the chain has every window.
        internal Texture ChainSky(ShaftChain c, out RenderTexture temp)
        {
            temp = null;
            RenderTexture sky = _renderer.SkyDepthTexture;
            if (c.Mask == null || sky == null)
                return sky;
            temp = RenderTexture.GetTemporary(sky.width, sky.height, 0, RenderTextureFormat.RFloat);
            temp.filterMode = FilterMode.Point;
            _shaftsUtil.SetTexture(s_rpInside, sky);
            _shaftsUtil.SetTexture(s_rpWindowMask, c.Mask);
            Graphics.Blit(NearDepth(), temp, _shaftsUtil, _mixPass);
            return temp;
        }

        // destination = outside, with the pixels under `mask` taken from inside.
        internal void MixShafts(RenderTexture outside, Texture inside, Texture mask, RenderTexture destination)
        {
            _shaftsUtil.SetTexture(s_rpInside, inside);
            _shaftsUtil.SetTexture(s_rpWindowMask, mask);
            Graphics.Blit(outside, destination, _shaftsUtil, _mixPass);
        }

        // One texel of "near" in the camera's raw depth (what the renderer clears its sky depth to).
        Texture2D NearDepth()
        {
            if (_nearDepth == null)
            {
                _nearDepth = new Texture2D(1, 1, TextureFormat.RFloat, false, true)
                    { name = "RealPortal near depth", filterMode = FilterMode.Point, hideFlags = HideFlags.DontSave };
                _nearDepth.SetPixel(0, 0, new Color(SystemInfo.usesReversedZBuffer ? 1f : 0f, 0f, 0f, 0f));
                _nearDepth.Apply(false, true);
            }
            return _nearDepth;
        }

        // OnDestroy.
        void ClearShafts()
        {
            DetachShaftMasks();
            _shaftMasks.Release();
            for (int i = 0; i < _chainMasks.Count; i++)
                PortalMath.Kill(_chainMasks[i]);
            _chainMasks.Clear();
            PortalMath.Kill(_ownAllMask);
            _ownAllMask = null;
            PortalMath.Kill(_shaftsUtil);
            _shaftsUtil = null;
            PortalMath.Kill(_nearDepth);
            _nearDepth = null;
            _chainCount = 0;
            _shaftsFrame = -1;
            _farSuns.Clear();
        }

        AmplifyOcclusionEffect _occlusion;
        static readonly System.Reflection.FieldInfo[] s_occlusionFields =
            typeof(AmplifyOcclusionEffect).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

        AmplifyOcclusionEffect _occlusionFrom;
        bool _occlusionStale = true, _occlusionNoFilter;
        float _occlusionFullAt;

        // The game's SSAO (Amplify Occlusion, switched and tuned by CameraEffects from the graphics settings) works
        // from the game camera's depth, where the window is a flat surface: the view through it needs its own.
        void SyncOcclusion(GameCamera gameCamera)
        {
            AmplifyOcclusionEffect game = gameCamera.GetComponent<AmplifyOcclusionEffect>();
            bool on = Plugin.AmbientOcclusion.Value && game != null && game.enabled;
            // Win.Margin: the window's border keeps what the SSAO kernel reaches (beyond FadeStart its radius is the
            // fade's).
            _settings.EffectRadius = on ? Mathf.Max(game.Radius, game.FadeEnabled ? game.FadeToRadius : 0f) : 0f;
            if (_occlusion == null)
            {
                if (!on)
                    return;
                _occlusion = _renderer.Camera.gameObject.AddComponent<AmplifyOcclusionEffect>();
                _occlusionStale = true;
            }
            if (on)
            {
                // Win.DepthOnly: the copy without its temporal filter, which would make the window camera render
                // motion vectors again (AmplifyOcclusionEffect.UsingMotionVectors); its history mixes windows anyway.
                bool noFilter = WindowFlags.DepthOnly;
                // Win.LeanFixed: all ~30 settings (by reflection, boxed) only when they may have changed: a new copy or
                // game camera, the graphics settings changed, the filter switch flipped, and every 5 s for anything
                // else; in between the few the game changes as it goes.
                if (!WindowFlags.LeanFixed || _occlusionStale || game != _occlusionFrom || noFilter != _occlusionNoFilter || Time.unscaledTime >= _occlusionFullAt)
                {
                    foreach (System.Reflection.FieldInfo f in s_occlusionFields)
                        f.SetValue(_occlusion, f.GetValue(game));
                    _occlusionFrom = game;
                    _occlusionStale = false;
                    _occlusionFullAt = Time.unscaledTime + 5f;
                }
                else
                {
                    // EnvMan.SetEnv (CameraEffects.SetEnvironmentAOParams) every frame; CameraEffects.SetSSAO.
                    _occlusion.Tint = game.Tint;
                    _occlusion.Intensity = game.Intensity;
                    _occlusion.FadeToTint = game.FadeToTint;
                    _occlusion.Downsample = game.Downsample;
                    _occlusion.SampleCount = game.SampleCount;
                }
                _occlusionNoFilter = noFilter;
                if (noFilter)
                    _occlusion.FilterEnabled = false;
            }
            if (_occlusion.enabled != on)
                _occlusion.enabled = on;
        }

        void GraphicsSettingsChanged() => _occlusionStale = true;

        // Win.EnvGroups: the windows chosen for a frame render grouped by the environment their camera stands in, so
        // the far environment is set once per group (AreaRenderState); what a group sets stays for the next group,
        // which changes only what differs, and EndWindowBatch puts the game's back after the last. The renderer
        // takes the key through PortalRenderSettings.GroupOf and groups only while Win.EnvGroups is on and one of
        // PortalRenderer.BeforeGroup/AfterGroup is set.
        void BindRenderOrder()
        {
            _settings.GroupOf = RenderGroupKey;
            _renderer.BeforeGroup = GroupStarts;
            if (Plugin.Verbose != null)
                Plugin.Debug("Window renders grouped by environment: yes");
        }

        // Lab: groups of window renders (one environment each) so far.
        internal static int LabRenderGroups;

        // Nothing to set up here: the group's first window sets its environment by difference (FarEnvironment).
        static void GroupStarts(PortalWindow first) => LabRenderGroups++;

        // The same key for windows whose view gets the same environment (0: the game's own).
        int RenderGroupKey(PortalWindow w)
        {
            if (!WindowFlags.EnvGroups || !Plugin.FarWeather.Value || _main == null || w == null || w.Partner == null)
                return 0;
            Vector3 eye = _main.transform.position;
            Vector3 camPos = PortalMath.View(w.Frame, w.Partner.Frame, eye, out _).MultiplyPoint3x4(eye);
            EnvSetup env = FarEnvironment.Pick(camPos);
            return env == null ? 0 : (System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(env) & int.MaxValue) | 1;
        }

        // LightLod's distance from its reference point (the player) to a light: also from where the player stands
        // as seen through each live portal near them, so lights at the far side show in the window as in the game.
        internal static float LightDistance(Vector3 reference, Vector3 light)
        {
            float d = Vector3.Distance(reference, light);
            // Called from LightLod's coroutine: anything thrown here would stop that light's updates for good.
            try
            {
                PortalSystem s = Instance;
                if (s == null || Plugin.Enabled == null || !Plugin.Enabled.Value)
                    return d;
                if (FarFlags.LinkScaling)
                {
                    // The nearest by its square, one root taken (the same value: the root keeps the order).
                    List<Vector3> places = s.LightPlaces(reference);
                    float nearest = float.MaxValue;
                    for (int i = 0; i < places.Count; i++)
                        nearest = Mathf.Min(nearest, (places[i] - light).sqrMagnitude);
                    return places.Count > 0 ? Mathf.Min(d, Mathf.Sqrt(nearest)) : d;
                }
                float reach = LightReach;
                foreach (PortalLink link in s._active)
                {
                    if (link == null || link.Partner == null || Vector3.Distance(reference, link.Frame.Center) > reach)
                        continue;
                    Vector3 there = PortalMath.Pair(link.Frame, link.Partner.Frame).MultiplyPoint3x4(reference);
                    d = Mathf.Min(d, Vector3.Distance(there, light));
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"LightDistance: {e.Message}");
            }
            return d;
        }

        // Far.LinkScaling: LightLod asks for every light (hundreds with far areas loaded) from the same reference
        // point, and each asked every live link and worked out the pair's matrix again. The links near that point and
        // where it stands through each are found once a frame (again if the point moved within it).
        readonly List<Vector3> _lightPlaces = new List<Vector3>();
        int _lightFrame = -1;
        Vector3 _lightReference;

        List<Vector3> LightPlaces(Vector3 reference)
        {
            if (_lightFrame == Time.frameCount && reference.Equals(_lightReference))
                return _lightPlaces;
            // Kept only once complete (a throw here leaves it to be made again).
            _lightFrame = -1;
            _lightPlaces.Clear();
            float reach = LightReach;
            foreach (PortalLink link in _active)
            {
                if (link == null || link.Partner == null || Vector3.Distance(reference, link.Frame.Center) > reach)
                    continue;
                _lightPlaces.Add(PortalMath.Pair(link.Frame, link.Partner.Frame).MultiplyPoint3x4(reference));
            }
            _lightFrame = Time.frameCount;
            _lightReference = reference;
            return _lightPlaces;
        }

        // How far from the player a live portal's far side counts for its lights: as far as windows render (WindowReach.Render:
        // Window.MaxDistance, or with Win.DistantLive the live range), and with Win.LightsPerPlace (P39) 20 m more, so a fire
        // by the far point has faded in (a second or two) before its window comes into reach.
        static float LightReach => WindowReach.Render + (WindowFlags.LightsPerPlace ? LightLead : 0f);
        const float LightLead = 20f;

        // ---- Win.LightsPerPlace (P9) ---------------------------------------------------------------------------------
        // The game ranks every enabled point light by its distance to the player once a second (LightLod.UpdateLights), and
        // its limits (lit lights, and the 3 with shadows at the game's default settings) take the first ones. With far
        // places in that one ranking, the torches by a far portal took the player's shadow slots and his lights took the
        // far ones, and both flipped as he walked. Ranked per place instead, as the game ranks them standing in each: the
        // player's (the game's reference point) and the far point of each live portal within LightReach (Pair·reference,
        // LightPlaces). Each light goes to its nearest place, with the game's m_cameraDistanceOuter (distance less a quarter
        // of its light distance), and m_lightPrio is its rank in that place. The game's timer and early outs are kept; the
        // coroutine's distance (LightDistance, the nearest place) is unchanged. With no live portal within reach the one place
        // is the player's: the game's own ranking. At the game's top light settings (no limits) nothing is ranked, as in
        // the game. One sort a second, nothing allocated once the list has grown.
        static readonly AccessTools.FieldRef<HashSet<LightLod>> s_lodLights =
            AccessTools.StaticFieldRefAccess<HashSet<LightLod>>(AccessTools.DeclaredField(typeof(LightLod), "m_lights"));
        static readonly AccessTools.FieldRef<float> s_lodTimer =
            AccessTools.StaticFieldRefAccess<float>(AccessTools.DeclaredField(typeof(LightLod), "m_updateTimer"));
        static readonly AccessTools.FieldRef<LightLod, int> s_lodPrio = AccessTools.FieldRefAccess<LightLod, int>("m_lightPrio");
        static readonly AccessTools.FieldRef<LightLod, float> s_lodOuter = AccessTools.FieldRefAccess<LightLod, float>("m_cameraDistanceOuter");
        static readonly AccessTools.FieldRef<LightLod, Light> s_lodLight = AccessTools.FieldRefAccess<LightLod, Light>("m_light");

        struct RankedLight
        {
            public LightLod Lod;
            public int Place;
            public float Outer;
        }

        // By place, then by outer distance within it (as the game's sort).
        sealed class ByPlace : IComparer<RankedLight>
        {
            internal static readonly ByPlace Instance = new ByPlace();
            public int Compare(RankedLight a, RankedLight b) => a.Place != b.Place ? a.Place.CompareTo(b.Place) : a.Outer.CompareTo(b.Outer);
        }

        readonly List<RankedLight> _rankedLights = new List<RankedLight>();
        float _rankErrorAt;
        // Lab (Win.LightsPerPlace): places the last ranking had (the player's and the far points within reach).
        internal static int LabLightPlaces;

        // From the LightLod.UpdateLights prefix while Win.LightsPerPlace is on. True: done (the game's ranking is skipped);
        // false: the game's ranking runs (no portal system, the mod off).
        internal static bool RankLights(float dt)
        {
            PortalSystem s = Instance;
            if (s == null || Plugin.Enabled == null || !Plugin.Enabled.Value)
                return false;
            // LightLod.UpdateLights' own early outs and its 1 s timer.
            if (LightLod.m_lightLimit < 0 && LightLod.m_shadowLimit < 0)
                return true;
            ref float timer = ref s_lodTimer();
            timer += dt;
            if (timer < 1f)
                return true;
            timer = 0f;
            Camera cam = Utils.GetMainCamera();
            if (cam == null)
                return true;
            try
            {
                s.RankPerPlace(cam);
            }
            catch (System.Exception e)
            {
                // The ranks of the last pass stay; said now and then, not every second.
                if (Time.unscaledTime >= s._rankErrorAt)
                {
                    s._rankErrorAt = Time.unscaledTime + 30f;
                    Plugin.Log.LogWarning($"Win.LightsPerPlace: {e.Message}");
                }
            }
            return true;
        }

        void RankPerPlace(Camera cam)
        {
            // LightLod.GetLightReferencePoint: the camera in free fly or without a player, else the player.
            Player me = Player.m_localPlayer;
            Vector3 reference = GameCamera.InFreeFly() || me == null ? cam.transform.position : me.transform.position;
            List<Vector3> places = LightPlaces(reference);
            _rankedLights.Clear();
            foreach (LightLod l in s_lodLights())
            {
                if (l == null || !l.enabled)
                    continue;
                Light light = s_lodLight(l);
                if (!light || light.type != LightType.Point)
                    continue;
                Vector3 at = l.transform.position;
                float best = (reference - at).sqrMagnitude;
                int place = 0;
                for (int i = 0; i < places.Count; i++)
                {
                    float d = (places[i] - at).sqrMagnitude;
                    if (d < best)
                    {
                        best = d;
                        place = i + 1;
                    }
                }
                float outer = Mathf.Sqrt(best) - l.m_lightDistance * 0.25f;
                s_lodOuter(l) = outer;
                _rankedLights.Add(new RankedLight { Lod = l, Place = place, Outer = outer });
            }
            _rankedLights.Sort(ByPlace.Instance);
            int rank = 0, last = -1;
            for (int i = 0; i < _rankedLights.Count; i++)
            {
                RankedLight r = _rankedLights[i];
                if (r.Place != last)
                {
                    last = r.Place;
                    rank = 0;
                }
                s_lodPrio(r.Lod) = rank++;
            }
            _rankedLights.Clear();
            LabLightPlaces = places.Count + 1;
        }

        // Grass patches (and other instanced clutter) inside a window's view of the last frame.
        // viewPos: the nearest window camera that sees it.
        // Lab: the debug dump on the next frame, as with the debug key.
        internal void LabRequestDump() => _dumpRequested = true;
        internal string LabDumpDir => _dumpDir;

        internal string LabFrusta => $"{_renderer.Frusta.Count} (views at {string.Join(", ", _renderer.ViewPositions.Select(v => v.ToString("F0")))})";

        internal bool InsideWindowView(BoundingSphere sphere, out Vector3 viewPos)
        {
            viewPos = default;
            float best = float.MaxValue;
            // Grass.GateAhead (P29): last frame's views widened by this frame's motion, and the views ahead.
            bool wide = Ahead();
            for (int i = 0; i < _renderer.Frusta.Count; i++)
            {
                bool inside = true;
                if (wide)
                    inside = InsideWide(_renderer.Frusta[i], _renderer.ViewPositions[i], sphere);
                else
                    foreach (Plane p in _renderer.Frusta[i])
                        if (p.GetDistanceToPoint(sphere.position) < -sphere.radius)
                        {
                            inside = false;
                            break;
                        }
                float d = inside ? (_renderer.ViewPositions[i] - sphere.position).sqrMagnitude : float.MaxValue;
                if (d < best)
                {
                    best = d;
                    viewPos = _renderer.ViewPositions[i];
                }
            }
            for (int i = 0; i < _aheadCount; i++)
            {
                if (!InsideWide(_aheadFrusta[i], _aheadViews[i], sphere))
                    continue;
                float d = (_aheadViews[i] - sphere.position).sqrMagnitude;
                if (d < best)
                {
                    best = d;
                    viewPos = _aheadViews[i];
                }
            }
            return best < float.MaxValue;
        }

        // Grass.GateAhead (Docs\PARITY-PLAN.md P29). The views grass is let through for are last frame's, while the camera
        // has moved since: each is widened by the frame's motion, its side planes (through its eye) turned outward by the
        // angle the camera may turn in a frame (WindowReach's turn rate times this frame's time, at least 10 and at most 45
        // degrees) and every plane moved out by how far it may move. A window about to show (WindowReach.AboutToShow) that
        // showed no view last frame gets one ahead: the game camera's view as it stands, carried through the window's pair
        // (the window camera's view and more: the whole screen, its near plane at the camera's), widened the same way. Made
        // once a frame (at the grass pass, from the camera's last pose) and again from its final pose at the window plan
        // (PrepareAhead(true), from FarGrassGate.AfterPlan) for the patches updated there. Off: last frame's views as they
        // were, none ahead.
        const float AheadMinTurn = 10f, AheadMaxTurn = 45f;
        readonly List<Plane[]> _aheadFrusta = new List<Plane[]>();
        readonly List<Vector3> _aheadViews = new List<Vector3>();
        readonly Plane[] _aheadMain = new Plane[6];
        int _aheadCount, _aheadFrame = -1, _aheadVersion;
        float _aheadSin, _aheadMove;
        // Lab: views made ahead of a window's first render so far.
        internal static long LabAheadViews;

        // True with the switch on (this frame's widening and views ahead made); off, there are none.
        bool Ahead()
        {
            if (!FarFlags.GrassGateAhead)
            {
                if (_aheadCount > 0 || _aheadFrame >= 0)
                {
                    _aheadCount = 0;
                    _aheadFrame = -1;
                    _aheadVersion++;
                }
                return false;
            }
            if (_aheadFrame != Time.frameCount)
                PrepareAhead();
            return true;
        }

        // This frame's widening and views ahead; `again`: made anew from the camera as it stands now.
        internal void PrepareAhead(bool again = false)
        {
            int frame = Time.frameCount;
            if (!FarFlags.GrassGateAhead || (_aheadFrame == frame && !again))
                return;
            _aheadFrame = frame;
            _aheadVersion++;
            _aheadCount = 0;
            float dt = Mathf.Max(Time.unscaledDeltaTime, 0f);
            _aheadSin = Mathf.Sin(Mathf.Clamp(WindowReach.TurnRate * dt, AheadMinTurn, AheadMaxTurn) * Mathf.Deg2Rad);
            _aheadMove = WindowReach.Speed * dt;
            Camera main = _main != null ? _main : Utils.GetMainCamera();
            if (main == null)
                return;
            List<PortalLink> live = Live();
            bool any = false;
            for (int i = 0; i < live.Count; i++)
            {
                PortalLink link = live[i];
                PortalWindow w = link.Window;
                // Its view is among last frame's.
                if (w.RenderedAtFrame >= frame - 1 || w.ShownAtFrame >= frame - 1)
                    continue;
                if (!link.FrameSet || !link.Partner.FrameSet || !WindowReach.AboutToShow(link))
                    continue;
                if (!any)
                {
                    GeometryUtility.CalculateFrustumPlanes(main, _aheadMain);
                    any = true;
                }
                Transform t = main.transform;
                Vector3 eye = t.position;
                Matrix4x4 m = PortalMath.View(link.Frame, link.Partner.Frame, eye, out _);
                if (_aheadFrusta.Count == _aheadCount)
                    _aheadFrusta.Add(new Plane[6]);
                Carry(_aheadMain, m, _aheadFrusta[_aheadCount], m.MultiplyPoint3x4(eye + t.forward));
                Vector3 at = m.MultiplyPoint3x4(eye);
                if (_aheadViews.Count == _aheadCount)
                    _aheadViews.Add(at);
                else
                    _aheadViews[_aheadCount] = at;
                _aheadCount++;
                LabAheadViews++;
            }
        }

        // The planes of a view carried through `m` (normals by its inverse transpose), each facing `inside`.
        static void Carry(Plane[] from, in Matrix4x4 m, Plane[] to, Vector3 inside)
        {
            Matrix4x4 inv = m.inverse;
            for (int k = 0; k < 6; k++)
            {
                Vector3 n = from[k].normal;
                Vector3 on = -n * from[k].distance;
                var carried = new Vector3(inv.m00 * n.x + inv.m10 * n.y + inv.m20 * n.z,
                                          inv.m01 * n.x + inv.m11 * n.y + inv.m21 * n.z,
                                          inv.m02 * n.x + inv.m12 * n.y + inv.m22 * n.z);
                var plane = new Plane(carried, m.MultiplyPoint3x4(on));
                to[k] = plane.GetDistanceToPoint(inside) < 0f ? plane.flipped : plane;
            }
        }

        // Inside a view (planes as GeometryUtility.CalculateFrustumPlanes: left, right, bottom, top, near, far) widened by this
        // frame's motion: the side planes, which pass through the eye, turned outward by the angle of sine _aheadSin (a point
        // at distance r from the eye may lie r * sin below them), every plane moved out by _aheadMove.
        bool InsideWide(Plane[] f, Vector3 eye, BoundingSphere sphere)
        {
            Vector3 p = sphere.position;
            float slack = sphere.radius + _aheadMove;
            float side = slack + _aheadSin * (p - eye).magnitude;
            for (int k = 0; k < f.Length; k++)
                if (f[k].GetDistanceToPoint(p) < -(k < 4 ? side : slack))
                    return false;
            return true;
        }

        // Grass.ViewNear: InsideWindowView for a renderer thinned by distance (InstanceRenderer.m_useLod). It draws
        // nothing for a view farther than its LOD's end (lodMax, from `here` as the game measures it), so only the
        // views within that are tested, found through the view positions laid out in 64 m cells (XZ). With twenty
        // portals every grass patch was tested against 15-20 frusta every frame, nearly all kilometres away.
        internal bool InsideWindowViewNear(BoundingSphere sphere, Vector3 here, float lodMax, bool xz, out Vector3 viewPos)
        {
            viewPos = default;
            // Grass.GateAhead (P29): last frame's views widened, and the views ahead (after the renderer's, by index).
            _viewsWide = Ahead();
            int views = _renderer.Frusta.Count + _aheadCount;
            if (views == 0 || lodMax <= 0f)
                return false;
            IndexViews();
            float best = float.MaxValue;
            int x0 = Mathf.FloorToInt((here.x - lodMax) / ViewCell), x1 = Mathf.FloorToInt((here.x + lodMax) / ViewCell);
            int z0 = Mathf.FloorToInt((here.z - lodMax) / ViewCell), z1 = Mathf.FloorToInt((here.z + lodMax) / ViewCell);
            // A long LOD (not grass) would look through many cells: every view, with the same distance test.
            if ((long)(x1 - x0 + 1) * (z1 - z0 + 1) > 16)
            {
                for (int i = 0; i < views; i++)
                    NearestView(i, sphere, here, lodMax * lodMax, xz, ref best, ref viewPos);
                return best < float.MaxValue;
            }
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                    if (_viewCells.TryGetValue(ViewCellKey(x, z), out int i))
                        for (; i >= 0; i = _viewNext[i])
                            NearestView(i, sphere, here, lodMax * lodMax, xz, ref best, ref viewPos);
            return best < float.MaxValue;
        }

        // View i, if it is within the LOD's reach and its frustum holds the sphere, when nearer than the best so far
        // (the same measure as InsideWindowView).
        void NearestView(int i, BoundingSphere sphere, Vector3 here, float reachSq, bool xz, ref float best, ref Vector3 viewPos)
        {
            int own = _renderer.ViewPositions.Count;
            Vector3 v = i < own ? _renderer.ViewPositions[i] : _aheadViews[i - own];
            float dx = v.x - here.x, dz = v.z - here.z;
            float lodDistSq = xz ? dx * dx + dz * dz : (v - here).sqrMagnitude;
            if (lodDistSq >= reachSq)
                return;
            Plane[] f = i < own ? _renderer.Frusta[i] : _aheadFrusta[i - own];
            if (_viewsWide)
            {
                if (!InsideWide(f, v, sphere))
                    return;
            }
            else
                for (int k = 0; k < f.Length; k++)
                    if (f[k].GetDistanceToPoint(sphere.position) < -sphere.radius)
                        return;
            float d = (v - sphere.position).sqrMagnitude;
            if (d < best)
            {
                best = d;
                viewPos = v;
            }
        }

        const float ViewCell = 64f;
        // Each cell's first view, and the next view in the same cell (-1 ends it).
        readonly Dictionary<long, int> _viewCells = new Dictionary<long, int>();
        int[] _viewNext = new int[32];
        int _viewsFrame = -1, _viewsCount = -1, _viewsAhead = -1;
        Vector3 _viewsFirst, _viewsLast;
        // Grass.GateAhead: the views tested by NearestView are widened (InsideWindowViewNear's call).
        bool _viewsWide;

        static long ViewCellKey(int x, int z) => ((long)x << 32) | (uint)z;

        // The views are last frame's (the renderer lists them as the windows render, before the next frame's
        // updates): laid out again once a frame, or when the list changed since. Grass.GateAhead: the views ahead after
        // them (again when they were made anew).
        void IndexViews()
        {
            List<Vector3> views = _renderer.ViewPositions;
            int own = views.Count, n = own + _aheadCount;
            if (_viewsFrame == Time.frameCount && _viewsCount == n && _viewsAhead == _aheadVersion
                && (own == 0 || (views[0].Equals(_viewsFirst) && views[own - 1].Equals(_viewsLast))))
                return;
            _viewsFrame = Time.frameCount;
            _viewsCount = n;
            _viewsAhead = _aheadVersion;
            _viewsFirst = own > 0 ? views[0] : default;
            _viewsLast = own > 0 ? views[own - 1] : default;
            _viewCells.Clear();
            if (_viewNext.Length < n)
                _viewNext = new int[Mathf.NextPowerOfTwo(n)];
            for (int i = 0; i < n; i++)
            {
                Vector3 v = i < own ? views[i] : _aheadViews[i - own];
                long key = ViewCellKey(Mathf.FloorToInt(v.x / ViewCell), Mathf.FloorToInt(v.z / ViewCell));
                _viewNext[i] = _viewCells.TryGetValue(key, out int head) ? head : -1;
                _viewCells[key] = i;
            }
        }

        readonly List<Renderer> _playerRenderers = new List<Renderer>();

        IEnumerable<Renderer> PlayerRenderers()
        {
            _playerRenderers.Clear();
            Player p = Player.m_localPlayer;
            GameObject visual = p != null ? p.GetVisual() : null;
            if (visual != null)
                visual.GetComponentsInChildren(false, _playerRenderers);
            return _playerRenderers;
        }

        // While the body passes the exit plane, cut it there: the near plane goes to the exit plane at the body.
        float PlayerNear(PortalWindow entry, Vector3 camPos, Matrix4x4 view)
        {
            Player p = Player.m_localPlayer;
            if (p == null || !_byWindow.TryGetValue(entry.Partner, out PortalLink exitLink) || !_byWindow.TryGetValue(entry, out PortalLink entryLink))
                return 0f;
            // Win.CrossNearPlane (P44): the near plane at the body cuts the whole view, and with it whatever lies near the far
            // portal closer than the body (an opening seen at an angle). The part of the body (or of its copy) on the
            // camera's side of the exit plane is the part the game camera draws itself, right over those pixels of the
            // window: the real body while it stands in the entry, the visual it carries back while the camera stands behind
            // the portal (MainPreCull, ShowPlayerThroughPortal). So with the copy shown, the view keeps its near plane at the
            // opening and nothing is cut; without it only the real body past the exit, which nothing on this side draws over,
            // still raises it (no copy is drawn then). Off: the near plane goes to the body or the copy, whichever is there.
            bool split = WindowFlags.CrossNearPlane;
            if (split && Plugin.ShowPlayer.Value)
                return 0f;
            PortalFrame exit = exitLink.Frame;
            Vector3 body = p.transform.position + Vector3.up * PlayerRefHeight;
            float near = NearAt(body, exit, camPos, view, 0f);
            if (split)
                return near;
            // The real body near the exit, or its copy drawn there while it stands in the entry.
            Vector3 copy = PortalMath.Pair(entryLink.Frame, exit).MultiplyPoint3x4(body);
            // Both, without an array made for them on every render.
            near = NearAt(copy, exit, camPos, view, near);
            // Win.CrossingBodies (P23): the other bodies in either opening, by the same rule.
            return WindowFlags.CrossingBodies ? CrossingNear(entryLink, exitLink, camPos, view, near) : near;
        }

        static float NearAt(Vector3 at, PortalFrame exit, Vector3 camPos, Matrix4x4 view, float near)
        {
            float d = exit.SignedDistance(at);
            Vector3 local = Quaternion.Inverse(exit.Rotation) * (at - exit.Center);
            if (Mathf.Abs(d) > 0.8f || !exit.ContainsLocal(local, 0.3f) || Mathf.Sign(d) == Mathf.Sign(exit.SignedDistance(camPos)))
                return near;
            Vector3 onPlane = at - exit.Forward * d;
            return Mathf.Max(near, -view.MultiplyPoint3x4(onPlane).z - 0.05f);
        }

        GameCamera _gameCamera;
        Vector3 _lastCamAt;
        // Far.ArriveOnCross: whether the camera stood through a portal at the last check, and the frame you last crossed.
        bool _arriveThrough;
        int _playerCrossedFrame = -10;
        int _lastWindowRenderFrame = -1000;
        int _windowsFrame = -1;
        float _crossFade = -1f;
        float _errorLogAt;

        // A LOD group's cross-fade state is shared by all cameras; with a window camera choosing another LOD
        // than the game camera every frame, the fade restarts back and forth and far objects blink. While windows
        // render, switch LODs instantly; the game's fade time comes back when they stop (with Win.LodFadeWindowsOnly,
        // right after each frame's window renders, before the game camera culls).
        void SetLodCrossFade(bool windows)
        {
            if (windows && _crossFade < 0f)
            {
                _crossFade = LODGroup.crossFadeAnimationDuration;
                LODGroup.crossFadeAnimationDuration = 0.001f;
            }
            else if (!windows && _crossFade >= 0f)
            {
                LODGroup.crossFadeAnimationDuration = _crossFade;
                _crossFade = -1f;
            }
        }

        // The game's own LOD cross-fade time, also while the windows have it instant (FarProxies' proxies fade as the game).
        internal float GameCrossFadeDuration => _crossFade >= 0f ? _crossFade : LODGroup.crossFadeAnimationDuration;

        // From GameCamera.LateUpdate: which camera is the game's this frame.
        internal void SetGameCamera(GameCamera gameCamera)
        {
            if (gameCamera == null && _gameCamera != null)
            {
                _windows.Clear();
                ResetWindowPipeline();
                ReleaseWindowSlots();
                _renderer.RenderAll(null, _windows);
                SetLodCrossFade(false);
                // Win.FarShafts: no masks of windows from before left on the camera.
                DetachShaftMasks();
                _shaftsFrame = -1;
            }
            _gameCamera = gameCamera;
            if (gameCamera != null && _main == null)
                _main = gameCamera.GetComponent<Camera>();
        }

        // Right before the game camera culls: every LateUpdate has run, so the views match the frame it draws.
        internal void RenderWindows(GameCamera gameCamera)
        {
            if (Suspended)
            {
                _windows.Clear();
                ResetWindowPipeline();
                _renderer.RenderAll(null, _windows);
                return;
            }
            if (_main == null)
                _main = gameCamera.GetComponent<Camera>();
            if (_main == null)
                return;
            LogCameraOnce(gameCamera);
            _settings.Resolution = Plugin.Resolution.Value;
            // Win.DistantLive (P2): as far as a far end stays loaded; off, Window.MaxDistance (WindowReach.Render).
            _settings.MaxDistance = WindowReach.Render;
            _settings.FarClip = Plugin.FarClip.Value > 0f ? Plugin.FarClip.Value : float.MaxValue;
            _settings.ShadowDistance = Plugin.ShadowDistance.Value;
            _settings.LodBias = Plugin.LodBias.Value;
            _settings.Recursion = Plugin.Recursion.Value;
            _settings.ClearFlags = Plugin.Sky.Value;
            _settings.RenderingPath = Plugin.RenderPath.Value;
            _settings.Clip = Plugin.Clip.Value;
            _settings.VirtualDepth = Plugin.VirtualDepth.Value;
            _settings.FullFrame = Plugin.FullFrame.Value;
            _settings.Margin = Plugin.Margin.Value;
            _settings.RefreshSmallLess = Plugin.RefreshSmallLess.Value;
            _settings.MaxRendersPerFrame = Plugin.MaxWindowRenders.Value;
            _settings.LodMatchesGame = !LabOldLod;
            _settings.ViewDepthForFinalEffects = !LabPlaneDepthForEffects;
            _settings.MotionScreenAlways = LabMotionScreenAlways;
            _settings.StrictJump = LabStrictJump;
            // The game's fog and SSAO leave the window alone whenever the window camera does them itself.
            _settings.FogSeesNearWindow = (Plugin.Fog.Value || Plugin.AmbientOcclusion.Value) && !LabGameFogOnWindow;
            PostProcessingBehaviour post = gameCamera.GetComponent<PostProcessingBehaviour>();
            _settings.EffectsSeeWindowAsSky = post == null || post.profile == null || post.profile.fog.settings.excludeSkybox;
            _settings.MotionVectors = Plugin.MotionVectors.Value;
            // Win.LiveShare: the count scheduler's 8 m rule from the player's eyes too (the third-person camera stands
            // metres behind them).
            Player viewer = WindowFlags.LiveShare ? Player.m_localPlayer : null;
            _settings.HasViewer = viewer != null;
            if (viewer != null)
                _settings.Viewer = viewer.GetEyePoint();
            List<PortalLink> live = Live();
            // Win.LeanFixed: with no live portal nothing renders through the window camera, so its fog, sun shafts
            // and SSAO copies are left as they are, and the far-side upkeep below runs only until it has run down.
            // (WindowFlags.LeanIdle: the lab's switch for this part alone, as for the renderer's idle path.)
            bool idle = WindowFlags.LeanFixed && WindowFlags.LeanIdle && live.Count == 0;
            if (!idle)
            {
                SyncFog(gameCamera);
                SyncSunShafts(gameCamera);
                SyncOcclusion(gameCamera);
                // Slot cameras' SSAO and fog (S2), and their grass: the engine's GrassMainCameraOnly and the exclusions
                // follow Grass.Targeted (after MonoUpdaters.Update, before any slot camera renders).
                SyncSlotEffects(gameCamera);
            }
            _windows.Clear();
            foreach (PortalLink link in live)
                _windows.Add(link.Window);
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (Plugin.FarWeather.Value)
            {
                _farPlaces.Clear();
                foreach (PortalLink link in live)
                    _farPlaces.Add((link.Partner.Frame.Center, PortalMath.Pair(link.Frame, link.Partner.Frame)));
                if (!idle || FarEnvironment.HasFarState)
                    FarEnvironment.KeepFarWeather(_farPlaces);
                // Far.MistWarmSpread: the far mist warms up while its area loads, before any window onto it is live.
                if (!idle || FarMist.Count > 0 || FarFlags.MistWarmSpread)
                    FarMist.Keep(_farPlaces);
            }
            _sky.Sweep();
            FitParticleBounds(_main.transform.position);
            // Win.CrossingBodies (P23): the bodies in a live opening, for this frame's renders and the game camera.
            ListCrossing();
            if (_windows.Count > 0)
                LabBeforeWindows?.Invoke();
            // Win.LodFadeWindowsOnly (P10): LOD switches are instant for the window renders alone; the game's fade time is
            // back before the game camera culls, so the base view cross-fades as in the game. Read once for the frame.
            bool fadeAround = WindowFlags.LodFadeWindowsOnly;
            if (fadeAround && _windows.Count > 0)
                SetLodCrossFade(true);
            // Win.EnvGroups: from here to the end of the last window the far environment and its mist stay set
            // from one window to the next (each changing only what differs); they go back right after.
            _windowBatch = true;
            try
            {
                // WindowManager.RenderChosen (PortalRenderer.RenderAll while nothing of S2/S3 is effective).
                RenderChosenWindows();
            }
            finally
            {
                _windowBatch = false;
                EndWindowBatch();
                if (fadeAround)
                    SetLodCrossFade(false);
            }
            if (_renderer.RendersLastFrame > 0)
                LabAfterWindows?.Invoke();
            if (Plugin.FarWeather.Value)
            {
                _shownLinks.Clear();
                foreach (PortalLink link in Live())
                    if (link.Window.RenderedThisFrame)
                        _shownLinks.Add(link);
                if (!idle || _reflections.Count > 0)
                    _reflections.Update(this, _shownLinks);
                _shownPoints.Clear();
                foreach (PortalLink link in _shownLinks)
                    _shownPoints.Add(FarPoint(link));
                if (!idle || _farParticles.Count > 0)
                    _farParticles.Update(_shownPoints, _main.transform.position);
            }
            CountStats(System.Diagnostics.Stopwatch.GetTimestamp() - t0);
            // The game camera has come into another place (with you, or back from looking through a portal): the
            // game picks its weather by the camera, so from this very frame it is that place's, as the window
            // showed it, not blending in over a couple of seconds.
            // Far.ArriveOnCross (P40): only on a crossing, you through an opening on this frame or the last (MovePlayer), or
            // the camera carried through one or back (CameraThroughFrame); a vanilla teleport, a respawn or a hitch leaves the
            // game to blend as it does.
            Vector3 camAt = _main.transform.position;
            bool through = CameraThroughFrame == Time.frameCount;
            bool crossing = !FarFlags.ArriveOnCross || through != _arriveThrough || Time.frameCount - _playerCrossedFrame <= 1;
            _arriveThrough = through;
            if ((camAt - _lastCamAt).sqrMagnitude > 25f && Plugin.FarWeather.Value && crossing)
                FarEnvironment.Arrived(camAt);
            _lastCamAt = camAt;
            // Win.Tiers: cached windows leave frames without any window render, which must not switch the game's LOD
            // cross-fade back and forth: it stays instant while windows rendered within the last half second or so. So do
            // windows the count scheduler leaves hidden behind something (WindowManager.OccludedThisFrame, Win.Occlusion).
            // Win.StaleTaa: a frame that only shows earlier renders (stale or resolved, every window off its cadence) counts as
            // a window frame too, or the count scheduler would switch the cross-fade at its 2nd/3rd-frame cadence.
            bool windowFrame = _renderer.RendersLastFrame > 0 || (WindowFlags.StaleTaa && AnyWindowShown());
            if (windowFrame)
                _lastWindowRenderFrame = Time.frameCount;
            // Win.LodFadeWindowsOnly: put back already, right after the batch (no hold).
            if (!fadeAround)
                SetLodCrossFade(windowFrame
                                || (_manager != null && (_manager.WantsPlan || _manager.OccludedThisFrame) && Time.frameCount - _lastWindowRenderFrame <= 30));
            TraceFrame();
            if (_dumpFrames > 0 && _dumpDir != null)
                DebugDump.LiveFrame(_main, _windows, _dumpDir, 4 - _dumpFrames--);
            if (_dumpRequested)
            {
                _dumpRequested = false;
                _dumpDir = DebugDump.Write(_renderer, _main, _windows);
                if (_dumpDir != null)
                {
                    DebugDump.LiveFrame(_main, _windows, _dumpDir, 0);
                    _dumpFrames = 3;
                }
            }
        }

        // Some window shows a view this frame (fresh, stale or resolved).
        bool AnyWindowShown()
        {
            for (int i = 0; i < _windows.Count; i++)
                if (_windows[i].RenderedThisFrame)
                    return true;
            return false;
        }

        readonly List<(Vector3, Matrix4x4)> _farPlaces = new List<(Vector3, Matrix4x4)>();

        // The windows of this frame are rendering (RenderAll): with Win.EnvGroups what a window's far side sets that
        // the next may share stays set until EndWindowBatch.
        bool _windowBatch;
        bool Grouped => _windowBatch && WindowFlags.EnvGroups;

        // After the last window of the frame: the game's environment, mist and particle bounds back for its camera.
        // (Without Win.EnvGroups each window has put them back already, and this changes nothing.)
        void EndWindowBatch()
        {
            // Win.CrossingBodies: no body stays carried past the batch (a render that threw before AfterWindowRender).
            if (_crossMoved.Count > 0)
                PutBack(_crossMoved);
            // Nor does an exit's piece stay out of view or a caster only (Win.ExitShadows), nor a particle sized for a
            // window (Win.ParticleSize).
            ShowBehindExit();
            if (_sizeChanged.Count > 0)
                RestoreParticleSizes();
            RenderingWindow = null;
            // Win.EnvOncePerArea: the far terrain the last renders kept, and the game's own back.
            KeepTerrain(null);
            _setUpArea = null;
            FarEnvironment.Restore();
            FarMist.Hide();
            if (WindowFlags.EnvGroups && _main != null)
                FitParticleBounds(_main.transform.position);
        }

        // Every 10 s while windows render: CPU time spent on them and how many window renders per frame.
        void CountStats(long ticks)
        {
            _statFrames++;
            _statTicks += ticks;
            _statRenders += _renderer.RendersLastFrame;
            if (Time.unscaledTime < _statNext)
                return;
            if (_statRenders > 0 && Plugin.Verbose.Value)
                Plugin.Log.LogInfo($"Windows: {_statTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / _statFrames:0.00} ms CPU/frame, " +
                                   $"{(float)_statRenders / _statFrames:0.0} renders/frame, {_statFrames / 10f:0} fps, {_active.Count} live portals");
            _statNext = Time.unscaledTime + 10f;
            _statFrames = 0;
            _statTicks = 0;
            _statRenders = 0;
        }

        // ---- Window Pipeline v2 (S3): cached views the far side has made wrong at once -------------------------------
        //
        // A window that is not rendered this frame shows its cache. The scheduler bounds how stale that gets (error model,
        // age caps), but some changes over there make the cache wrong at once, whatever its age, and only the game side
        // sees them (WindowInvalid; the scheduler then re-renders it as a must, error +∞):
        //  - EnvSwitch: the weather the view gets (that place's with FarWeather, or yours) is another one than last frame;
        //  - Lightning: a flash of a storm over there (or of yours, in a view of your own weather) is lit, and the frame
        //    after the last one (the light goes again);
        //  - SpanChange: the far area's loading stage changed (RemoteAreas.Stage: terrain and objects came or went).
        // Told to the manager before it plans (Patches.GameCameraLateUpdate, the pose final). Only while the tiers plan:
        // the count scheduler (Win.Tiers off) re-renders by cadence and does not read WindowState.Invalid.
        sealed class ViewWatch
        {
            // The weather: that place's (Far) or yours, by name (FarEnvironment.GameEnvName); Known once seen.
            public string Env;
            public bool Far, Known;
            public bool Flash;
            public int Stage = -1;
            public int Frame;
        }

        readonly Dictionary<PortalWindow, ViewWatch> _viewWatch = new Dictionary<PortalWindow, ViewWatch>();
        readonly List<PortalWindow> _viewWatchDrop = new List<PortalWindow>();
        float _viewWatchSweepAt;
        // Lab: windows invalidated from here so far.
        internal static int LabViewInvalidations;

        internal void InvalidateCachedViews(Camera main)
        {
            IWindowManager m = _manager;
            if (m == null || !m.WantsPlan || main == null || Suspended || Plugin.Enabled == null || !Plugin.Enabled.Value)
            {
                // Started over when planning resumes, so nothing from before reads as a change.
                if (_viewWatch.Count > 0)
                    _viewWatch.Clear();
                return;
            }
            int frame = Time.frameCount;
            Vector3 eye = main.transform.position;
            bool farWeather = Plugin.FarWeather.Value;
            List<PortalLink> live = Live();
            for (int i = 0; i < live.Count; i++)
            {
                PortalLink link = live[i];
                PortalWindow w = link.Window;
                // Paired a frame late when the partner's window was made after this link's Refresh (nothing renders it).
                if (w.Partner == null)
                    continue;
                if (!_viewWatch.TryGetValue(w, out ViewWatch v))
                    _viewWatch[w] = v = new ViewWatch();
                v.Frame = frame;
                WindowInvalid why = WindowInvalid.None;
                // The weather the window camera's place gets now (RenderGroupKey's pick).
                EnvSetup far = null;
                if (farWeather)
                {
                    Vector3 camPos = PortalMath.View(w.Frame, w.Partner.Frame, eye, out _).MultiplyPoint3x4(eye);
                    far = FarEnvironment.Pick(camPos);
                }
                bool isFar = far != null;
                string env = isFar ? far.m_name : FarEnvironment.GameEnvName;
                if (v.Known && (isFar != v.Far || !string.Equals(env, v.Env, System.StringComparison.Ordinal)))
                    why |= WindowInvalid.EnvSwitch;
                v.Env = env;
                v.Far = isFar;
                v.Known = true;
                bool flash = far != null ? FarEnvironment.FarFlashing(far) : FarEnvironment.OwnFlashing;
                if (flash || v.Flash)
                    why |= WindowInvalid.Lightning;
                v.Flash = flash;
                int stage = Areas.Stage(link.PartnerZdo);
                if (v.Stage >= 0 && stage != v.Stage)
                    why |= WindowInvalid.SpanChange;
                v.Stage = stage;
                if (why != WindowInvalid.None)
                {
                    m.Invalidate(w, why);
                    LabViewInvalidations++;
                }
            }
            // Windows gone for a second are forgotten (checked every few seconds).
            if (Time.unscaledTime < _viewWatchSweepAt)
                return;
            _viewWatchSweepAt = Time.unscaledTime + 5f;
            _viewWatchDrop.Clear();
            foreach (KeyValuePair<PortalWindow, ViewWatch> kv in _viewWatch)
                if (kv.Value.Frame < frame - 60)
                    _viewWatchDrop.Add(kv.Key);
            foreach (PortalWindow w in _viewWatchDrop)
                _viewWatch.Remove(w);
        }

        void LogCameraOnce(GameCamera gameCamera)
        {
            if (_loggedCamera)
                return;
            _loggedCamera = true;
            Camera sky = gameCamera.m_skyCamera;
            Plugin.Debug($"Main camera: clear {_main.clearFlags}, path {_main.actualRenderingPath}, HDR {_main.allowHDR}, depth {_main.depth}, " +
                         $"mask {_main.cullingMask:X8}, near {_main.nearClipPlane}, far {_main.farClipPlane}; sky camera: " +
                         (sky != null ? $"clear {sky.clearFlags}, depth {sky.depth}, mask {sky.cullingMask:X8}, far {sky.farClipPlane}" : "none"));
            Player p = Player.m_localPlayer;
            if (p != null && p.TryGetComponent(out Rigidbody body))
                Plugin.Debug($"Player body interpolation: {body.interpolation}");
            if (!Plugin.Verbose.Value)
                return;
            // What the game camera does on its own, so the window camera can be made to match.
            var sb = new System.Text.StringBuilder("Game camera setup:");
            sb.Append($"\n  target {(_main.targetTexture != null ? $"{_main.targetTexture.width}x{_main.targetTexture.height} {_main.targetTexture.format}" : "screen")}, depth modes {_main.depthTextureMode}");
            foreach (Behaviour b in _main.GetComponents<Behaviour>())
                sb.Append($"\n  component {b.GetType().FullName} enabled {b.enabled}");
            foreach (CameraEvent evt in System.Enum.GetValues(typeof(CameraEvent)))
                foreach (CommandBuffer cb in _main.GetCommandBuffers(evt))
                    sb.Append($"\n  command buffer {evt}: {cb.name}");
            PostProcessingBehaviour post = _main.GetComponent<PostProcessingBehaviour>();
            if (post != null && post.profile != null)
            {
                PostProcessingProfile pr = post.profile;
                sb.Append($"\n  post profile {pr.name}: fog {pr.fog.enabled} (exclude sky {pr.fog.settings.excludeSkybox}), AO {pr.ambientOcclusion.enabled}, AA {pr.antialiasing.enabled} {pr.antialiasing.settings.method}, " +
                          $"SSR {pr.screenSpaceReflection.enabled}, DOF {pr.depthOfField.enabled}, motion blur {pr.motionBlur.enabled}, eye adaptation {pr.eyeAdaptation.enabled}, " +
                          $"bloom {pr.bloom.enabled}, color grading {pr.colorGrading.enabled}, user LUT {pr.userLut.enabled}, CA {pr.chromaticAberration.enabled}, grain {pr.grain.enabled}, vignette {pr.vignette.enabled}, dithering {pr.dithering.enabled}");
            }
            sb.Append($"\n  RenderSettings fog {RenderSettings.fog} {RenderSettings.fogMode} density {RenderSettings.fogDensity} start {RenderSettings.fogStartDistance} end {RenderSettings.fogEndDistance} colour {RenderSettings.fogColor}");
            sb.Append($"\n  skybox {(RenderSettings.skybox != null ? RenderSettings.skybox.shader.name : "none")}, ambient {RenderSettings.ambientMode}");
            foreach (BuiltinShaderType t in new[] { BuiltinShaderType.DeferredShading, BuiltinShaderType.DeferredReflections, BuiltinShaderType.ScreenSpaceShadows, BuiltinShaderType.DepthNormals, BuiltinShaderType.MotionVectors })
            {
                Shader custom = GraphicsSettings.GetCustomShader(t);
                sb.Append($"\n  builtin {t}: {GraphicsSettings.GetShaderMode(t)} {(custom != null ? custom.name : "")}");
            }
            Camera skyCam = gameCamera.m_skyCamera;
            if (skyCam != null)
                sb.Append($"\n  sky camera enabled {skyCam.enabled}, target {(skyCam.targetTexture != null ? skyCam.targetTexture.name : "screen")}, path {skyCam.actualRenderingPath}, near {skyCam.nearClipPlane}, components " +
                          string.Join(", ", skyCam.GetComponents<Behaviour>().Select(b => b.GetType().Name + (b.enabled ? "" : "(off)"))));
            foreach (Camera c in Camera.allCameras)
                sb.Append($"\n  active camera {c.name} depth {c.depth} mask {c.cullingMask:X8} clear {c.clearFlags} target {(c.targetTexture != null ? c.targetTexture.name : "screen")}");
            Plugin.Log.LogInfo(sb.ToString());
        }

        readonly List<FollowPlayer> _followers = new List<FollowPlayer>();
        // From the FollowPlayer.LateUpdate postfix: the followers the game runs.
        internal static readonly HashSet<FollowPlayer> SeenFollowers = new HashSet<FollowPlayer>();
        readonly List<Vector3> _followerPos = new List<Vector3>();
        float _followersAt = -1f;

        // Player space -> the space of the game camera this frame (its world, when its arm went through a portal).
        Matrix4x4 PlayerToCamera => CameraThroughFrame == Time.frameCount ? CameraMatrix : Matrix4x4.identity;

        // The game keeps its weather and mist around the camera and the player (FollowPlayer), always on the
        // player's side here. For a view in another world (a window, or the game camera standing past a portal)
        // they go where they would be for a camera standing there: the followers are placed around that view and
        // their particles, simulated in a space of their own, are carried by the player -> view matrix.
        // toView: player space -> the view's world.
        void MoveFollowers(Matrix4x4 toView, Vector3 camPos)
        {
            ListFollowers();
            _followerPos.Clear();
            Player p = Player.m_localPlayer;
            Vector3 player = p != null ? toView.MultiplyPoint3x4(p.transform.position) : camPos;
            foreach (FollowPlayer f in _followers)
            {
                if (f == null)
                {
                    _followerPos.Add(default);
                    continue;
                }
                Vector3 was = f.transform.position;
                _followerPos.Add(was);
                Vector3 to = f.m_follow == FollowPlayer.Type.Camera || GameCamera.InFreeFly() ? camPos
                    : f.m_follow == FollowPlayer.Type.Player ? player : (camPos + player) * 0.5f;
                if (f.m_lockYPos)
                    to.y = was.y;
                if (to.y > f.m_maxYPos)
                    to.y = f.m_maxYPos;
                f.transform.position = to;
                if (_spaces.TryGetValue(f, out Transform space) && space != null)
                {
                    space.position = to - was;
                    _particleSpaceMoved = true;
                }
            }
            FitParticleBounds(camPos);
        }

        void ListFollowers()
        {
            if (Time.unscaledTime < _followersAt)
                return;
            _followersAt = Time.unscaledTime + 2f;
            _followers.Clear();
            // Those that ran their LateUpdate lately (a scene-wide search costs a lot with far areas loaded).
            SeenFollowers.RemoveWhere(f => f == null || !f.isActiveAndEnabled);
            _followers.AddRange(SeenFollowers);
            foreach (FollowPlayer f in _followers)
                ParticlesOf(f);
        }

        readonly Dictionary<FollowPlayer, Transform> _spaces = new Dictionary<FollowPlayer, Transform>();
        bool _particleSpaceMoved;

        // Each follower's world-space particles are simulated in a space of its own instead: at the origin while
        // the game simulates, so nothing changes for it, and shifted by the follower's move while a view in another
        // world renders. Shifted, not turned: rain, snow and mist fall and drift with the one wind of the whole
        // world, so on the far side they keep the directions they have here.
        Transform SpaceOf(FollowPlayer f)
        {
            if (_spaces.TryGetValue(f, out Transform t) && t != null)
                return t;
            var go = new GameObject("RealPortalParticleSpace " + f.name);
            DontDestroyOnLoad(go);
            _spaces[f] = go.transform;
            return go.transform;
        }

        ParticleSystem[] ParticlesOf(FollowPlayer f)
        {
            if (_followerParticles.TryGetValue(f, out ParticleSystem[] systems) && systems != null)
                return systems;
            _followerParticles[f] = systems = f.GetComponentsInChildren<ParticleSystem>(true);
            Transform space = SpaceOf(f);
            foreach (ParticleSystem ps in systems)
            {
                ParticleSystem.MainModule main = ps.main;
                if (main.simulationSpace != ParticleSystemSimulationSpace.World)
                    continue;
                main.simulationSpace = ParticleSystemSimulationSpace.Custom;
                main.customSimulationSpace = space;
                if (ps.TryGetComponent(out ParticleSystemRenderer r))
                {
                    _spaceRenderers.Add(r);
                    _boundsDirty = true;
                }
            }
            return systems;
        }

        readonly List<ParticleSystemRenderer> _spaceRenderers = new List<ParticleSystemRenderer>();
        bool _boundsFitted;
        // Win.EnvGroups: where and on which frame the bounds were last put around, and whether renderers came since.
        Vector3 _boundsAt;
        int _boundsFrame = -1;
        bool _boundsDirty = true;

        // Unity keeps a particle system's culling bounds from where it last saw its space: once a window's render
        // has moved them to the far side, the game camera would cull the rain and mist around itself. They always
        // hang around the camera that draws them, so each view gets bounds around itself.
        void FitParticleBounds(Vector3 camPos)
        {
            if (LabNoFitBounds)
            {
                // Back to Unity's own bounds, so the lab can see the game without this.
                if (_boundsFitted)
                    foreach (ParticleSystemRenderer r in _spaceRenderers)
                        if (r != null)
                            r.ResetBounds();
                _boundsFitted = false;
                return;
            }
            // Win.EnvGroups: once per view, not again for the same place in the same frame (the game camera's before
            // the windows and after the last when no window moved them); between windows the next one fits its own.
            if (WindowFlags.EnvGroups && _boundsFitted && !_boundsDirty && _boundsFrame == Time.frameCount
                && camPos.x == _boundsAt.x && camPos.y == _boundsAt.y && camPos.z == _boundsAt.z)
                return;
            _boundsFitted = true;
            _boundsAt = camPos;
            _boundsFrame = Time.frameCount;
            _boundsDirty = false;
            var b = new Bounds(camPos, new Vector3(6000f, 6000f, 6000f));
            for (int i = _spaceRenderers.Count - 1; i >= 0; i--)
            {
                ParticleSystemRenderer r = _spaceRenderers[i];
                if (r == null)
                    _spaceRenderers.RemoveAt(i);
                else
                    r.bounds = b;
            }
        }

        static bool InParticleSpace(ParticleSystem ps, Transform space)
        {
            ParticleSystem.MainModule main = ps.main;
            return main.simulationSpace == ParticleSystemSimulationSpace.Custom && main.customSimulationSpace == space;
        }

        void ResetParticleSpace()
        {
            if (!_particleSpaceMoved)
                return;
            foreach (Transform space in _spaces.Values)
                if (space != null)
                    space.position = Vector3.zero;
            _particleSpaceMoved = false;
            // Win.EnvGroups: between windows the next one fits its own (EndWindowBatch fits the game camera's).
            if (_main != null && !Grouped)
                FitParticleBounds(_main.transform.position);
        }

        readonly Dictionary<FollowPlayer, ParticleSystem[]> _followerParticles = new Dictionary<FollowPlayer, ParticleSystem[]>();
        readonly List<(ParticleSystem ps, float rate, int frame)> _distanceEmitters = new List<(ParticleSystem, float, int)>();
        ParticleSystem.Particle[] _particles = new ParticleSystem.Particle[1024];

        // From the FollowPlayer.LateUpdate postfix. When the weather and mist particles jump with the player or the
        // camera through a far portal, the ones already in the air jump along (rain keeps falling around you), and
        // emitters that spawn by distance travelled skip this one move: across kilometres they would try to fill
        // the whole way in one frame and stall it for a second.
        internal void CarryParticles(FollowPlayer f, Vector3 before, Vector3 after)
        {
            Vector3 delta = after - before;
            for (int i = _distanceEmitters.Count - 1; i >= 0; i--)
                if (_distanceEmitters[i].ps == null || _distanceEmitters[i].frame < Time.frameCount - 1)
                {
                    if (_distanceEmitters[i].ps != null)
                    {
                        ParticleSystem.EmissionModule e = _distanceEmitters[i].ps.emission;
                        e.rateOverDistanceMultiplier = _distanceEmitters[i].rate;
                    }
                    _distanceEmitters.RemoveAt(i);
                }
            if (delta.sqrMagnitude < 30f * 30f || LabNoCarry)
                return;
            ParticleSystem[] systems = ParticlesOf(f);
            Transform space = SpaceOf(f);
            // Shifted with the follower, as the window showed them (see SpaceOf): the same rain, falling the same way.
            foreach (ParticleSystem ps in systems)
            {
                if (ps == null || !ps.gameObject.activeInHierarchy || !InParticleSpace(ps, space))
                    continue;
                ParticleSystem.EmissionModule e = ps.emission;
                if (e.rateOverDistanceMultiplier > 0f)
                {
                    _distanceEmitters.Add((ps, e.rateOverDistanceMultiplier, Time.frameCount));
                    e.rateOverDistanceMultiplier = 0f;
                }
                if (_particles.Length < ps.main.maxParticles)
                    _particles = new ParticleSystem.Particle[ps.main.maxParticles];
                int n = ps.GetParticles(_particles);
                for (int i = 0; i < n; i++)
                    _particles[i].position += delta;
                ps.SetParticles(_particles, n);
            }
        }

        void RestoreFollowers()
        {
            for (int i = 0; i < _followers.Count && i < _followerPos.Count; i++)
                if (_followers[i] != null)
                    _followers[i].transform.position = _followerPos[i];
            _followerPos.Clear();
            ResetParticleSpace();
        }

        static readonly int s_netRefPos = Shader.PropertyToID("_NetRefPos");
        Vector4 _netRefPos;
        FarTerrain _shownTerrain;

        // What the game sets up around the player, set up around the far side for this render: the reference
        // position its distant terrain and water hide around, its own distant terrain mesh, and its weather.
        // probe: for a reflection probe's face (no sky picture is drawn inside it).
        void ShowFarSide(PortalWindow entry, Matrix4x4 m, Vector3 camPos, bool probe = false)
        {
            _netRefPos = Shader.GetGlobalVector(s_netRefPos);
            if (ZNet.instance != null && !LabNoNetRef)
                Shader.SetGlobalVector(s_netRefPos, (m * PlayerToCamera).MultiplyPoint3x4(ZNet.instance.GetReferencePosition()));
            MovePlayerGlobals(m * PlayerToCamera);
            Player sp = Player.m_localPlayer;
            bool linked = _byWindow.TryGetValue(entry, out PortalLink link);
            if (sp != null && !LabNoSkyOcclusion)
                _sky.Show(SkyView(entry), (m * PlayerToCamera).MultiplyPoint3x4(sp.transform.position), !probe,
                    WindowFlags.SkyPerArea && linked && link != null ? Areas.Stage(link.PartnerZdo) : 0);
            // Win.EnvOncePerArea: among the frame's window renders (Win.EnvGroups keeps the environment and mist from one
            // to the next) the far area's own part, its distant terrain, weather and mist, is set up by the first of its
            // renders in a row and left in place for the next ones; a render of another area, or EndWindowBatch, takes it
            // down. Only a window's own on-screen render (the renderer stores its camera position on the window right
            // before BeforeRender, as FarParticles.ViewOf tests it): a nested level (Recursion) renders first, from M²·eye,
            // and its weather and mist there are not the window's, so it sets up for itself and the window after it too.
            // Each far portal has its own area, so what the switch saves is the distant-terrain toggles (the game's stays
            // hidden from one render to the next) and the area's wake once a frame, not the per-render FarEnvironment/
            // FarMist cost (FARWORLD-V3 §7 V0's −0.27 ms is that cost).
            ZDO area = linked && link != null ? link.PartnerZdo : null;
            bool once = Grouped && WindowFlags.EnvOncePerArea && !probe && area != null && entry != null && entry.HasView
                        && entry.ViewPos == camPos;
            if (once && ReferenceEquals(area, _setUpArea))
                return;
            _setUpArea = once ? area : null;
            if (once)
            {
                FarTerrain t = Areas.TerrainFor(area);
                KeepTerrain(t != null && t.Built && !LabNoFarTerrain ? t : null);
            }
            else
            {
                // A render that sets up for itself alone: what renders before it kept goes first.
                KeepTerrain(null);
                _shownTerrain = linked ? Areas.TerrainFor(link.PartnerZdo) : null;
                if (_shownTerrain != null && _shownTerrain.Built && !LabNoFarTerrain)
                {
                    _shownTerrain.SetVisible(true);
                    FarTerrain.SetMainVisible(false);
                }
                else
                    _shownTerrain = null;
            }
            if (Plugin.FarWeather.Value)
            {
                FarEnvironment.Apply(camPos);
                FarMist.Show(camPos);
            }
        }

        // Win.EnvOncePerArea: the far area whose part the renders so far in this batch left set up (null: none), the
        // distant terrain kept shown for it, and whether the game's own distant terrain is hidden for that.
        ZDO _setUpArea;
        FarTerrain _keptTerrain;
        bool _gameTerrainHidden;

        // `t` (null: none) is the far distant terrain the next renders show in place of the game's: the one kept before
        // goes, and the game's own is hidden while one is kept, shown again with KeepTerrain(null).
        void KeepTerrain(FarTerrain t)
        {
            if (_keptTerrain != null && _keptTerrain != t)
                _keptTerrain.SetVisible(false);
            _keptTerrain = t;
            if (t != null)
                t.SetVisible(true);
            if (_gameTerrainHidden != (t != null))
            {
                _gameTerrainHidden = t != null;
                FarTerrain.SetMainVisible(!_gameTerrainHidden);
            }
        }

        // Win.EnvOncePerArea: the far areas woken this frame (by their first render; Areas.WakeFor once a frame each).
        readonly List<ZDO> _wokenAreas = new List<ZDO>();
        int _wokenFrame = -1;

        bool FirstWakeThisFrame(ZDO far)
        {
            if (_wokenFrame != Time.frameCount)
            {
                _wokenFrame = Time.frameCount;
                _wokenAreas.Clear();
            }
            for (int i = 0; i < _wokenAreas.Count; i++)
                if (ReferenceEquals(_wokenAreas[i], far))
                    return false;
            _wokenAreas.Add(far);
            return true;
        }

        static readonly int s_playerPosition = Shader.PropertyToID("_PlayerPosition");
        static readonly int s_playerOldPosition = Shader.PropertyToID("_PlayerOldPosition");
        Vector4 _playerPosition, _playerOldPosition;
        bool _playerGlobalsMoved;

        // Shaders that work around the player (the sea's waves near you, grass bending away) get the player carried
        // into the view's world.
        void MovePlayerGlobals(Matrix4x4 toView)
        {
            if (_playerGlobalsMoved)
                return;
            _playerPosition = Shader.GetGlobalVector(s_playerPosition);
            _playerOldPosition = Shader.GetGlobalVector(s_playerOldPosition);
            _playerGlobalsMoved = true;
            if (Player.m_localPlayer == null || LabNoPlayerGlobals)
                return;
            Shader.SetGlobalVector(s_playerPosition, toView.MultiplyPoint3x4(_playerPosition));
            Shader.SetGlobalVector(s_playerOldPosition, toView.MultiplyPoint3x4(_playerOldPosition));
        }

        void RestorePlayerGlobals()
        {
            if (!_playerGlobalsMoved)
                return;
            _playerGlobalsMoved = false;
            Shader.SetGlobalVector(s_playerPosition, _playerPosition);
            Shader.SetGlobalVector(s_playerOldPosition, _playerOldPosition);
        }

        readonly FarSkyOcclusion _sky = new FarSkyOcclusion();
        static readonly object s_mainSkyView = new object();

        // What a sky picture is kept for: the window (Win.SkyPerArea off), or the far area it looks into, i.e. the
        // far portal's link (a probe for that window and any other window onto that portal share it).
        object SkyView(PortalWindow entry)
        {
            if (WindowFlags.SkyPerArea && entry != null && entry.Partner != null && _byWindow.TryGetValue(entry.Partner, out PortalLink far) && far != null)
                return far;
            return entry;
        }

        void HideFarSide()
        {
            _sky.Hide();
            RestorePlayerGlobals();
            // Win.EnvGroups: between windows the far environment and its mist stay for the next one (EndWindowBatch).
            if (!Grouped)
            {
                FarEnvironment.Restore();
                FarMist.Hide();
            }
            if (_shownTerrain != null)
            {
                _shownTerrain.SetVisible(false);
                FarTerrain.SetMainVisible(true);
                _shownTerrain = null;
            }
            Shader.SetGlobalVector(s_netRefPos, _netRefPos);
        }

        static readonly AccessTools.FieldRef<ClutterSystem, ClutterSystem.Quality> s_clutterQuality =
            AccessTools.FieldRefAccess<ClutterSystem, ClutterSystem.Quality>("m_quality");

        // From the ClutterSystem.LateUpdate postfix. The game grows grass only around the player; around the point
        // a window looks at (the player carried through the portal) it grows the same way, one patch a frame, from
        // before the window comes into view. It is then already there when you walk through.
        internal void GrowGrass(ClutterSystem clutter)
        {
            Player p = Player.m_localPlayer;
            if (LabNoGrass || p == null || Live().Count == 0 || s_clutterQuality(clutter) == ClutterSystem.Quality.Off || !RenderGroupSystem.IsGroupActive(RenderGroup.Overworld))
                return;
            Vector3 at = p.transform.position;
            // As far as windows render (WindowReach.Render), plus the grass's own reach round the far point.
            float reach = WindowReach.Render + clutter.m_distance;
            // Grass.Ahead (P11, P22): every live window's grass kept, new grass for windows about to show as well.
            if (FarFlags.GrassAhead)
            {
                GrowGrassAhead(clutter, at, reach, FarFlags.GrassGrowBudget);
                return;
            }
            if (FarFlags.GrassGrowBudget)
            {
                GrowGrassShared(clutter, at, reach);
                return;
            }
            foreach (PortalLink link in Live())
            {
                // Only behind windows in sight or about to be (the grass of a dozen far places costs as much as
                // the game's own several times over).
                if ((link.transform.position - at).sqrMagnitude > reach * reach || !link.Watched)
                    continue;
                // Grass that stands only needs touching now and then (the game drops a patch left 2 s untouched);
                // a window whose grass is still growing gets a new patch every frame.
                if (_grassNext.TryGetValue(link, out float next) && Time.time < next)
                    continue;
                Vector3 center = PortalMath.Pair(link.Frame, link.Partner.Frame).MultiplyPoint3x4(at);
                if (Heightmap.HaveQueuedRebuild(center, clutter.m_distance))
                    continue;
                bool grew = GeneratePatchesPast(clutter, center, link.Partner.Frame);
                _grassNext[link] = grew ? 0f : Time.time + 0.5f;
            }
            if (_grassNext.Count > 64)
                _grassNext.Clear();
        }

        readonly Dictionary<PortalLink, float> _grassNext = new Dictionary<PortalLink, float>();

        // Grass.GrowBudget: a new patch is 1-2 ms of ground rays and instancing, and with a dozen windows growing at
        // once one each a frame made frames of 30-40 ms. New patches are shared out instead: at most two a frame over
        // all watched windows (whatever their tier), in turns from the window after the last one served, the second
        // only while the first left time. A window not served still has its standing patches touched every 0.5 s,
        // so the game does not drop them (it does after 2 s untouched).
        const int GrassPerFrame = 2;
        const double GrassBudgetMs = 1.5;
        static readonly System.Diagnostics.Stopwatch s_grassWatch = new System.Diagnostics.Stopwatch();
        readonly Dictionary<PortalLink, float> _grassTouch = new Dictionary<PortalLink, float>();
        int _grassTurn;
        // Lab: patches grown under Grass.GrowBudget so far.
        internal static int LabGrassGrown;

        void GrowGrassShared(ClutterSystem clutter, Vector3 at, float reach)
        {
            List<PortalLink> live = Live();
            int count = live.Count, made = 0, start = _grassTurn % count;
            float now = Time.time;
            s_grassWatch.Restart();
            for (int k = 0; k < count; k++)
            {
                int i = (start + k) % count;
                PortalLink link = live[i];
                if ((link.transform.position - at).sqrMagnitude > reach * reach || !link.Watched)
                    continue;
                bool due = !_grassNext.TryGetValue(link, out float next) || now >= next;
                bool grow = due && made < GrassPerFrame && (made == 0 || s_grassWatch.Elapsed.TotalMilliseconds < GrassBudgetMs);
                if (!grow && _grassTouch.TryGetValue(link, out float touch) && now < touch)
                    continue;
                Vector3 center = PortalMath.Pair(link.Frame, link.Partner.Frame).MultiplyPoint3x4(at);
                if (Heightmap.HaveQueuedRebuild(center, clutter.m_distance))
                    continue;
                bool grew = GeneratePatchesPast(clutter, center, link.Partner.Frame, !grow);
                _grassTouch[link] = now + 0.5f;
                if (!grow)
                    continue;
                _grassNext[link] = grew ? 0f : now + 0.5f;
                if (grew)
                {
                    made++;
                    LabGrassGrown++;
                    // The next frame starts after this one.
                    _grassTurn = i + 1;
                }
            }
            if (_grassNext.Count > 64)
                _grassNext.Clear();
            if (_grassTouch.Count > 64)
                _grassTouch.Clear();
        }

        // Grass.Ahead (Docs\PARITY-PLAN.md P11, with P22's grass). The game drops a patch left 2 s untouched, and only watched
        // windows were served: on first sight, and on every look back after a few seconds, a window showed bare ground and
        // the grass grew back patch by patch. Now the standing patches of every live window within reach are touched every
        // 0.5 s (kept; a few dictionary lookups), watched or not. New patches grow for the windows that are watched,
        // approached (within 8 m, part of Watched) or about to show (WindowReach.AboutToShow): those about to show first,
        // then the nearest. Grass.GrowBudget's two a frame and 1.5 ms (budget) count new patches only. While the player
        // stands within 8 m of a portal the full circle round its exit grows and is kept: the half behind the far portal,
        // which no window shows, is where he stands once through (after the half the window shows).
        const float GrassByPortal = 8f;
        // Touch-only passes a frame at most (a patch lives 2 s untouched; due ones wait a frame, which also spreads them).
        const int GrassTouchesPerFrame = 4;
        PortalLink[] _growOrder = new PortalLink[16];
        float[] _growDist = new float[16];
        bool[] _growAhead = new bool[16];
        // Lab: touch passes for windows kept only by Grass.Ahead (neither watched nor about to show), and patches it grew
        // that the watched rule would not have (for a window about to show, or in the half behind an exit).
        internal static long LabGrassTouched, LabGrassAheadGrown;

        void GrowGrassAhead(ClutterSystem clutter, Vector3 at, float reach, bool budget)
        {
            List<PortalLink> live = Live();
            float now = Time.time;
            int n = 0, touches = 0;
            for (int i = 0; i < live.Count; i++)
            {
                PortalLink link = live[i];
                float d2 = (link.transform.position - at).sqrMagnitude;
                if (d2 > reach * reach)
                    continue;
                bool ahead = WindowReach.AboutToShow(link);
                if (!ahead && !link.Watched)
                {
                    // Kept, not grown.
                    if ((_grassTouch.TryGetValue(link, out float touchAt) && now < touchAt) || touches >= GrassTouchesPerFrame)
                        continue;
                    touches++;
                    Vector3 kept = PortalMath.Pair(link.Frame, link.Partner.Frame).MultiplyPoint3x4(at);
                    if (Heightmap.HaveQueuedRebuild(kept, clutter.m_distance))
                        continue;
                    GeneratePatchesPast(clutter, kept, link.Partner.Frame, true);
                    _grassTouch[link] = now + 0.5f;
                    LabGrassTouched++;
                    continue;
                }
                // Into the growth order: about to show first, then by distance.
                if (n == _growOrder.Length)
                {
                    System.Array.Resize(ref _growOrder, n * 2);
                    System.Array.Resize(ref _growDist, n * 2);
                    System.Array.Resize(ref _growAhead, n * 2);
                }
                int k = n++;
                for (; k > 0 && ((ahead && !_growAhead[k - 1]) || (ahead == _growAhead[k - 1] && d2 < _growDist[k - 1])); k--)
                {
                    _growOrder[k] = _growOrder[k - 1];
                    _growDist[k] = _growDist[k - 1];
                    _growAhead[k] = _growAhead[k - 1];
                }
                _growOrder[k] = link;
                _growDist[k] = d2;
                _growAhead[k] = ahead;
            }
            int made = 0;
            s_grassWatch.Restart();
            for (int k = 0; k < n; k++)
            {
                PortalLink link = _growOrder[k];
                _growOrder[k] = null;
                bool due = !_grassNext.TryGetValue(link, out float next) || now >= next;
                bool grow = due && (!budget || (made < GrassPerFrame && (made == 0 || s_grassWatch.Elapsed.TotalMilliseconds < GrassBudgetMs)));
                if (!grow && _grassTouch.TryGetValue(link, out float touch) && now < touch)
                    continue;
                Vector3 center = PortalMath.Pair(link.Frame, link.Partner.Frame).MultiplyPoint3x4(at);
                if (Heightmap.HaveQueuedRebuild(center, clutter.m_distance))
                    continue;
                // P22: by the portal, the full circle round its exit.
                bool full = (at - link.Frame.Center).sqrMagnitude < GrassByPortal * GrassByPortal;
                bool grew = GeneratePatchesRound(clutter, center, link.Partner.Frame, !grow, full, out bool behind);
                _grassTouch[link] = now + 0.5f;
                if (!grow)
                    continue;
                _grassNext[link] = grew ? 0f : now + 0.5f;
                if (grew)
                {
                    made++;
                    LabGrassGrown++;
                    if (behind || !link.Watched)
                        LabGrassAheadGrown++;
                }
            }
            if (_grassNext.Count > 64)
                _grassNext.Clear();
            if (_grassTouch.Count > 64)
                _grassTouch.Clear();
        }

        // Grass.Ahead (P22): GeneratePatchesPast, and with `full` the patches behind the exit too, which are touched and get
        // a new one when the half past the exit made none (the game's nearest-first order within each half). behind: the
        // patch made lies behind the exit.
        static bool GeneratePatchesRound(ClutterSystem clutter, Vector3 center, PortalFrame exit, bool touchOnly, bool full, out bool behind)
        {
            behind = false;
            bool grew = GeneratePatchesPast(clutter, center, exit, touchOnly);
            if (!full)
                return grew;
            bool generated = touchOnly || grew;
            float size = clutter.m_grassPatchSize;
            Vector2Int c = clutter.GetVegPatch(center);
            float side = -Mathf.Sign(exit.SignedDistance(center));
            int n = Mathf.CeilToInt((clutter.m_distance - size / 2f) / size);
            for (int r = 0; r <= n; r++)
                for (int x = c.x - r; x <= c.x + r; x++)
                    for (int y = c.y - r; y <= c.y + r; y++)
                    {
                        if (Mathf.Max(Mathf.Abs(x - c.x), Mathf.Abs(y - c.y)) != r)
                            continue;
                        var p = new Vector2Int(x, y);
                        // The patches GeneratePatchesPast left out.
                        if (side * exit.SignedDistance(clutter.GetVegPatchCenter(p)) >= -size * 0.75f)
                            continue;
                        s_generatePatch(clutter, center, p, ref generated, false);
                    }
            behind = generated && !touchOnly && !grew;
            return grew || behind;
        }

        delegate void GeneratePatchFn(ClutterSystem clutter, Vector3 camPos, Vector2Int p, ref bool generated, bool rebuildAll);
        static readonly GeneratePatchFn s_generatePatch =
            AccessTools.MethodDelegate<GeneratePatchFn>(AccessTools.Method(typeof(ClutterSystem), "GeneratePatch"));

        // The game's GeneratePatches round a point, but only the patches past the exit's plane: a window never shows
        // what lies behind the far portal (on the side it is looked through from), so that half is left out.
        // True when a patch was made (more may follow). touchOnly: the patches standing there are kept, none is made
        // (the game makes no new patch once one was made in the pass).
        static bool GeneratePatchesPast(ClutterSystem clutter, Vector3 center, PortalFrame exit, bool touchOnly = false)
        {
            bool generated = touchOnly;
            float size = clutter.m_grassPatchSize;
            Vector2Int c = clutter.GetVegPatch(center);
            // The side of the exit the view looks into: away from where the player stands mapped through.
            float side = -Mathf.Sign(exit.SignedDistance(center));
            int n = Mathf.CeilToInt((clutter.m_distance - size / 2f) / size);
            for (int r = 0; r <= n; r++)
                for (int x = c.x - r; x <= c.x + r; x++)
                    for (int y = c.y - r; y <= c.y + r; y++)
                    {
                        if (Mathf.Max(Mathf.Abs(x - c.x), Mathf.Abs(y - c.y)) != r)
                            continue;
                        var p = new Vector2Int(x, y);
                        // Patches wholly behind the exit (their centre more than a patch's half-diagonal behind it).
                        if (side * exit.SignedDistance(clutter.GetVegPatchCenter(p)) < -size * 0.75f)
                            continue;
                        s_generatePatch(clutter, center, p, ref generated, false);
                    }
            return generated && !touchOnly;
        }

        // Win.ShieldDome (FarShieldDome): the window whose view a window camera renders now, from BeforeWindowRender to
        // AfterWindowRender; else null.
        internal PortalWindow RenderingWindow { get; private set; }

        // FarProxies (ProxyDraw): the far portal whose area a window shows.
        internal bool WindowFarPortal(PortalWindow w, out ZDOID far)
        {
            far = ZDOID.None;
            if (w == null || !_byWindow.TryGetValue(w, out PortalLink link) || link == null || link.PartnerZdo == null)
                return false;
            far = link.PartnerZdo.m_uid;
            return true;
        }

        // Far.WeatherParity (FarEnvironment.ViewPlace): the far place of the window whose view renders now, its far
        // portal's centre (the key of the far weather's places); false outside a window render (a probe face).
        internal bool RenderingPlace(out Vector3 place)
        {
            place = default;
            PortalWindow w = RenderingWindow;
            if (w == null || !_byWindow.TryGetValue(w, out PortalLink link))
                return false;
            PortalFrame f = link.Partner != null ? link.Partner.Frame : link.FarFrame;
            if (f == null)
                return false;
            place = f.Center;
            return true;
        }

        void BeforeWindowRender(PortalWindow entry, Matrix4x4 m, Vector3 camPos)
        {
            // Win.ShadowRes (Docs\RIPPLE-PLAN.md §4.4), or the lab's LabShadowRes (L5, which wins): the sun's shadow map at
            // that size for this render (AfterWindowRender puts the game's back; after a render that threw before it,
            // MainPreCull does once the windows are done).
            if (LabShadowRes > 0 || WindowFlags.ShadowRes)
                ShadowResBefore();
            _movedVisual = null;
            // Win.ShieldDome (P24): whose view the window camera renders next (its dome pass, FarShieldDome).
            RenderingWindow = entry;
            if (_crossMoved.Count > 0)
                PutBack(_crossMoved);
            // What an earlier render left hidden or sized (it threw before AfterWindowRender) back first, so this render
            // reads the game's own modes and sizes.
            if (_hidden.Count > 0 || _shadowOnly.Count > 0)
                ShowBehindExit();
            if (_sizeChanged.Count > 0)
                RestoreParticleSizes();
            WindowTextures.InWindow = true;
            if (_byWindow.TryGetValue(entry, out PortalLink shown))
            {
                // Win.EnvOncePerArea: by the area's first render of the frame.
                if (!WindowFlags.EnvOncePerArea || FirstWakeThisFrame(shown.PartnerZdo))
                    Areas.WakeFor(shown.PartnerZdo);
                // Far.ParticleWindowGate: the far side's paused effects advanced by the time they missed, for this render
                // (Far.ParticleCatchupCull: those in or near its view from camPos).
                if (FarFlags.ParticleWindowGate)
                    _farParticles.BeforeRender(shown, camPos);
            }
            HideBehindExit(entry, camPos);
            ShowFarSide(entry, m, camPos);
            // Win.FarShafts (P33): the light the window's own view is lit by, for its sun on the game camera's image.
            if (WindowFlags.FarShafts && entry.HasView && entry.ViewPos == camPos)
                NoteFarSun(entry);
            // Win.FarAoTint (P32): the shared window camera's SSAO copy takes this view's far tint and strength (a slot
            // camera's is set by AoSync.Before).
            if (WindowFlags.FarAoTint)
                AoSync.Shared(_occlusion, _occlusionFrom);
            if (!LabFollowersStay)
                MoveFollowers(m * PlayerToCamera, camPos);
            // Win.ParticleSize (P41): the far side's particles keep their size on screen in a small window.
            if (WindowFlags.ParticleSize)
                SizeParticles(shown, camPos, !LabFollowersStay);
            // Win.CrossingBodies (P23): the bodies in this window's opening come out of the far one in its view.
            if (_crossing.Count > 0)
                CopyCrossing(entry, m, camPos);
            Player p = Player.m_localPlayer;
            if (!Plugin.ShowPlayer.Value || p == null)
                return;
            // Only while the player is at this window, so the copy appears coming out of the far side.
            Vector3 at = p.transform.position + Vector3.up * PlayerRefHeight;
            float dEntry = Vector3.Distance(at, entry.Frame.Center), dExit = Vector3.Distance(at, entry.Partner.Frame.Center);
            if (dEntry > 4f || dEntry > dExit)
                return;
            GameObject visual = p.GetVisual();
            if (visual == null)
                return;
            _movedVisual = visual.transform;
            _visualPos = _movedVisual.position;
            _visualRot = _movedVisual.rotation;
            _visualScale = _movedVisual.localScale;
            // As CopyCrossing: by `m` for the window's own view; by the pair for a deeper level's or a nested render's
            // (Win.NestedLive: `m` carries the main world through the window it is seen in too; the copy comes out of this
            // window's exit).
            bool own = entry.HasView && entry.ViewPos == camPos;
            Matrix4x4 carry = own ? m : PortalMath.Pair(entry.Frame, entry.Partner.Frame);
            _movedVisual.SetPositionAndRotation(carry.MultiplyPoint3x4(_visualPos), PortalMath.Rotation(carry) * _visualRot);
            // A view scaled to fit a smaller exit (PortalMath.View) shows the copy scaled as well, or it would not
            // line up with the player on this side.
            _movedVisual.localScale = _visualScale * carry.GetColumn(0).magnitude;
        }

        readonly FarReflections _reflections = new FarReflections();
        readonly FarParticles _farParticles = new FarParticles();
        readonly PortalAudio _audio = new PortalAudio();
        internal PortalAudio LabAudio => _audio;
        readonly List<Vector3> _shownPoints = new List<Vector3>();
        internal int LabFarParticles => _farParticles.Count;
        readonly List<PortalLink> _shownLinks = new List<PortalLink>();
        bool _probeShowing;
        internal int LabReflectionProbes => _reflections.Count;

        // Where the player stands carried through this link (the far side's view of things around you).
        internal Vector3 FarPoint(PortalLink link)
        {
            Player p = Player.m_localPlayer;
            Matrix4x4 m = PortalMath.Pair(link.Frame, link.Partner.Frame) * PlayerToCamera;
            return p != null ? m.MultiplyPoint3x4(p.transform.position) : link.Partner.Frame.Center;
        }

        // A far reflection probe rendering (FarReflections): the far side set up as for that link's window.
        internal void ShowFarForProbe(PortalLink link, Vector3 camPos)
        {
            Matrix4x4 m = PortalMath.Pair(link.Frame, link.Partner.Frame);
            ShowFarSide(link.Window, m, camPos, probe: true);
            MoveFollowers(m * PlayerToCamera, camPos);
        }

        internal void HideFarForProbe()
        {
            RestoreFollowers();
            HideFarSide();
        }

        void AfterWindowRender(PortalWindow entry, Matrix4x4 m, Vector3 camPos)
        {
            if (_shadowResLight != null)
                ShadowResAfter();
            WindowTextures.InWindow = false;
            if (_sizeChanged.Count > 0)
                RestoreParticleSizes();
            ShowBehindExit();
            RestoreFollowers();
            HideFarSide();
            RenderingWindow = null;
            if (_crossMoved.Count > 0)
                PutBack(_crossMoved);
            if (_movedVisual == null)
                return;
            _movedVisual.SetPositionAndRotation(_visualPos, _visualRot);
            _movedVisual.localScale = _visualScale;
            _movedVisual = null;
        }

        // ---- Win.ParticleSize (P41) ----------------------------------------------------------------------------------
        // Unity clamps every particle to maxParticleSize of its camera's viewport. A window camera's viewport is the window's
        // rect, rect.height of the screen (the renderer's _RP_Viewport, set right before BeforeRender), so in a small window a
        // large close particle (smoke by a fire, a puff of mist, snow at the lens) was clamped to that share of the window,
        // where standing there the game clamps it to that share of the screen. For the render the particles it shows get
        // maxParticleSize / rect.height, and theirs back right after it (AfterWindowRender): those of the far side's lively
        // objects that could reach that size from this view (by the bounds their system had when listed, with room for what
        // grew or moved since), and the weather and mist that follow the view (MoveFollowers). A far side's particle
        // renderers are listed from RemoteAreas.InstancesNear once a second, at most two far sides a frame (one waiting
        // renders with its last list); an object's renderers are looked up once. Off: the game's sizes, relative to the window.
        sealed class SizeList
        {
            internal readonly List<SizeEntry> Entries = new List<SizeEntry>();
            internal float ListedAt = float.NegativeInfinity;
        }

        struct SizeEntry
        {
            public ParticleSystemRenderer Renderer;
            public float Size, Radius;
            public Vector3 Center;
        }

        const float SizeListSeconds = 1f;
        const int SizeListsPerFrame = 2;
        // Room round a system's listed bounds: what its particles grow by, and what it moves in a second (a running creature).
        const float SizeGrow = 2f, SizeMove = 10f;
        static readonly int s_viewport = Shader.PropertyToID(PortalRenderer.ViewportProp);
        static readonly ParticleSystemRenderer[] s_noRenderers = new ParticleSystemRenderer[0];
        static readonly List<ParticleSystemRenderer> s_renderersOf = new List<ParticleSystemRenderer>();
        readonly Dictionary<PortalLink, SizeList> _sizeLists = new Dictionary<PortalLink, SizeList>();
        readonly Dictionary<ZNetView, ParticleSystemRenderer[]> _sizeRenderers = new Dictionary<ZNetView, ParticleSystemRenderer[]>();
        readonly List<(ParticleSystemRenderer, float)> _sizeChanged = new List<(ParticleSystemRenderer, float)>();
        readonly List<(ParticleSystemRenderer, float)> _followerSizes = new List<(ParticleSystemRenderer, float)>();
        readonly List<Vector3> _sizePoints = new List<Vector3>();
        readonly List<ZNetView> _sizeViews = new List<ZNetView>();
        float _followerSizesAt = float.NaN;
        int _sizeListFrame = -1, _sizeListsInFrame;
        // Lab (Win.ParticleSize): particle renderers given the window's size so far.
        internal static long LabParticlesSized;

        void SizeParticles(PortalLink shown, Vector3 camPos, bool followers)
        {
            float h = Shader.GetGlobalVector(s_viewport).w;
            if (!(h > 0f) || h >= 0.999f || _main == null)
                return;
            float k = 1f / h;
            if (shown != null && shown.Partner != null)
            {
                // The window's height at a distance of 1 (world units).
                float perMetre = Mathf.Tan(_main.fieldOfView * 0.5f * Mathf.Deg2Rad) * h * 2f;
                List<SizeEntry> list = SizeListOf(shown).Entries;
                for (int i = 0; i < list.Count; i++)
                {
                    SizeEntry e = list[i];
                    // No particle of it is larger than its grown bounds, nor nearer than they reach: past the distance at
                    // which that size is maxParticleSize of the window, the clamp cannot act.
                    float radius = e.Radius * SizeGrow + SizeMove;
                    float d = (e.Center - camPos).magnitude - radius;
                    if (d > 0f && 2f * radius < e.Size * d * perMetre)
                        continue;
                    ParticleSystemRenderer r = e.Renderer;
                    if (r == null)
                        continue;
                    r.maxParticleSize = e.Size * k;
                    _sizeChanged.Add((r, e.Size));
                }
            }
            if (!followers)
                return;
            // The weather and mist round this view (MoveFollowers put them there), all close to its camera.
            if (!(_followerSizesAt == _followersAt))
            {
                _followerSizesAt = _followersAt;
                _followerSizes.Clear();
                foreach (FollowPlayer f in _followers)
                {
                    if (f == null || !_followerParticles.TryGetValue(f, out ParticleSystem[] systems) || systems == null)
                        continue;
                    foreach (ParticleSystem ps in systems)
                        if (ps != null && ps.TryGetComponent(out ParticleSystemRenderer r))
                            _followerSizes.Add((r, r.maxParticleSize));
                }
            }
            foreach ((ParticleSystemRenderer r, float size) in _followerSizes)
                if (r != null)
                {
                    r.maxParticleSize = size * k;
                    _sizeChanged.Add((r, size));
                }
        }

        // Last to first, as PutBack: a renderer sized twice keeps the size it had before the first.
        void RestoreParticleSizes()
        {
            LabParticlesSized += _sizeChanged.Count;
            for (int i = _sizeChanged.Count - 1; i >= 0; i--)
            {
                (ParticleSystemRenderer r, float size) = _sizeChanged[i];
                if (r != null)
                    r.maxParticleSize = size;
            }
            _sizeChanged.Clear();
        }

        // The far side's particle renderers for this link's window, listed again once a second (within the frame's share).
        SizeList SizeListOf(PortalLink link)
        {
            if (!_sizeLists.TryGetValue(link, out SizeList list))
            {
                // Links gone since are forgotten all at once now and then.
                if (_sizeLists.Count > 64)
                    _sizeLists.Clear();
                _sizeLists[link] = list = new SizeList();
            }
            float now = Time.time;
            if (now - list.ListedAt < SizeListSeconds)
                return list;
            int frame = Time.frameCount;
            if (_sizeListFrame != frame)
            {
                _sizeListFrame = frame;
                _sizeListsInFrame = 0;
            }
            if (_sizeListsInFrame >= SizeListsPerFrame)
                return list;
            _sizeListsInFrame++;
            list.ListedAt = now;
            ListSizes(link, list);
            return list;
        }

        // The particle renderers of the far area's lively objects (and of any other area's within its reach), with the size
        // each has now (none is changed between renders) and the bounds of its particles.
        void ListSizes(PortalLink link, SizeList list)
        {
            list.Entries.Clear();
            _sizePoints.Clear();
            _sizePoints.Add(link.Partner.Frame.Center);
            Areas.InstancesNear(_sizePoints, SizeReach(), _sizeViews);
            for (int i = 0; i < _sizeViews.Count; i++)
            {
                ZNetView v = _sizeViews[i];
                if (v == null)
                    continue;
                if (!_sizeRenderers.TryGetValue(v, out ParticleSystemRenderer[] renderers))
                {
                    if (_sizeRenderers.Count > 20000)
                        _sizeRenderers.Clear();
                    v.GetComponentsInChildren(true, s_renderersOf);
                    renderers = s_renderersOf.Count > 0 ? s_renderersOf.ToArray() : s_noRenderers;
                    s_renderersOf.Clear();
                    _sizeRenderers[v] = renderers;
                }
                foreach (ParticleSystemRenderer r in renderers)
                {
                    if (r == null)
                        continue;
                    Bounds b = r.bounds;
                    Vector3 extents = b.extents;
                    // A system with no particles (an unlit fire) is placed by its object until it has some.
                    bool empty = extents.x == 0f && extents.y == 0f && extents.z == 0f;
                    list.Entries.Add(new SizeEntry
                    {
                        Renderer = r,
                        Size = r.maxParticleSize,
                        Center = empty ? r.transform.position : b.center,
                        Radius = empty ? SizeMove : extents.magnitude
                    });
                }
            }
            _sizeViews.Clear();
        }

        // As far as a far area's objects reach from its portal (its near zones and the distant ring, as FarParticles
        // measures it), with room for hills.
        static float SizeReach()
        {
            SimulationDistance sim = RemoteAreas.AreaDistance();
            const float zone = ZoneSystem.c_ZoneSize, halfDiagonal = ZoneSystem.c_ZoneSize * 0.70711f;
            int r = Mathf.Max(sim.TotalSimulationDistance, 1);
            return (sim.IsClassic ? (r + 1) * zone * 1.41422f : (r + 0.8f) * zone + 2f * halfDiagonal) + 32f;
        }

        // Win.ShadowRes and the lab's LabShadowRes (L5): the game's value is taken only when none is held (a render that
        // threw before AfterWindowRender left ours on the light: the next render keeps the game's from before it, and
        // MainPreCull puts it back before the game camera renders).
        void ShadowResBefore()
        {
            Light sun = EnvMan.instance != null ? EnvMan.instance.m_dirLight : null;
            if (sun == null)
                return;
            if (_shadowResLight == null)
            {
                _shadowResLight = sun;
                _shadowResWas = sun.shadowCustomResolution;
            }
            int res = LabShadowRes > 0 ? LabShadowRes : GameShadowRes(sun, _shadowResWas);
            if (res > 0)
                sun.shadowCustomResolution = res;
        }

        void ShadowResAfter()
        {
            _shadowResLight.shadowCustomResolution = _shadowResWas;
            _shadowResLight = null;
        }

        // Win.ShadowRes: the size of the sun's shadow map Unity gives the game camera itself. A custom resolution the game
        // (or another mod) put on the light is what the game camera gets too; otherwise the built-in rule: NextPowerOfTwo of
        // 1.9 x the larger side of its viewport in pixels, halved for each quality step below Very High, at most 4096 (2048
        // with less than 512 MB of video memory), so High at 1080p is 2048 (Docs\RIPPLE-FINDINGS.md). The rule is Unity's
        // documented one, not read from its code: the lab's shadowsplits reports the atlas each camera really got, and
        // LabShadowRes forces another size where they differ. Kept per viewport size and quality (most renders only compare).
        int GameShadowRes(Light sun, int custom)
        {
            if (custom > 0)
                return custom;
            if (_main == null)
                return 0;
            int px = Mathf.Max(_main.pixelWidth, _main.pixelHeight);
            int quality = sun.shadowResolution == LightShadowResolution.FromQualitySettings
                ? (int)QualitySettings.shadowResolution : (int)sun.shadowResolution;
            if (px == _shadowResPx && quality == _shadowResQuality)
                return _shadowResGame;
            int size = Mathf.NextPowerOfTwo(Mathf.Max(1, (int)(px * 1.9f))) >> (3 - Mathf.Clamp(quality, 0, 3));
            int max = SystemInfo.graphicsMemorySize >= 512 ? 4096 : 2048;
            _shadowResGame = Mathf.Clamp(size, 16, max);
            _shadowResPx = px;
            _shadowResQuality = quality;
            Plugin.Log.LogInfo($"Win.ShadowRes: the sun's shadow map at {_shadowResGame} px in window renders (game camera {_main.pixelWidth}x{_main.pixelHeight}, shadow quality {quality})");
            return _shadowResGame;
        }

        int _shadowResPx = -1, _shadowResQuality = -1, _shadowResGame;

        bool _dumpRequested;
        string _dumpDir;
        int _dumpFrames;
        int _traceFrames;
        bool _lastThrough;
        internal int CameraThroughFrame = -1;
        // The camera arm reached through a portal on this frame or the last (fixed updates come before the camera's).
        internal bool CameraThroughRecently => CameraThroughFrame >= Time.frameCount - 2;
        // Player space -> camera space while the camera arm passes through a portal.
        internal Matrix4x4 CameraMatrix = Matrix4x4.identity;
        Transform _mainMovedVisual;
        Vector3 _mainVisualPos;
        Quaternion _mainVisualRot;

        // While the camera looks at you through a portal, the game camera draws your body on its own side: the half
        // still in the opening shows directly and the rest through the window (the window camera sees the real one).
        void MainPreCull(Camera cam)
        {
            if (cam != null && cam.cameraType == CameraType.Reflection)
            {
                // Win.VanillaLook (P43): a reflection probe's face shows every window as the game's empty ring (the window
                // shader discards), not a crop of a view rendered for the game camera or the fallback; back at its
                // onPostRender.
                if (WindowFlags.VanillaLook)
                {
                    Shader.SetGlobalFloat(s_probeRender, 1f);
                    _probeRenderSet = true;
                }
                _probeShowing = _reflections.PreCull(this, cam);
                return;
            }
            if (cam == null || cam != _main || _gameCamera == null)
                return;
            // Never the game camera with the probes' flag (a probe face that raised no onPostRender).
            if (_probeRenderSet)
                ProbeRenderDone();
            // Last frame's game camera may not have reported back.
            MainPostRender(_main);
            Matrix4x4 jp = cam.projectionMatrix, jn = cam.nonJitteredProjectionMatrix;
            JitteredAtPreCull = jp.m02 != jn.m02 || jp.m12 != jn.m12;
            CheckJitter(cam, jp, jn);
            // The window views render here, before the game camera culls: rendering other cameras after it has
            // culled (onPreRender) breaks its cull results once two windows render in a frame. Every LateUpdate
            // has run, and the post stack has already jittered the projection for TAA.
            if (_windowsFrame != Time.frameCount)
            {
                _windowsFrame = Time.frameCount;
                _settings.Jitter = new Vector2((jp.m02 - jn.m02) * 0.5f, (jp.m12 - jn.m12) * 0.5f) * LabJitterScale;
                try
                {
                    RenderWindows(_gameCamera);
                }
                catch (System.Exception e)
                {
                    // Never break the game camera over a window; say it now and then, not every frame.
                    if (Time.unscaledTime >= _errorLogAt)
                    {
                        _errorLogAt = Time.unscaledTime + 10f;
                        Plugin.Log.LogError("Window rendering failed: " + e);
                    }
                }
                // Win.ShadowRes / LabShadowRes: never the game camera with a window's shadow map size (a window render that
                // threw before AfterWindowRender).
                if (_shadowResLight != null)
                    ShadowResAfter();
                // Win.FarShafts (P33): the game camera's sun shafts for the windows it shows (their masks after its
                // transparent queue). Off: nothing planned, its buffer taken off.
                try
                {
                    PlanShafts(cam);
                }
                catch (System.Exception e)
                {
                    _chainCount = 0;
                    _shaftsFrame = -1;
                    _shaftMasks.Clear();
                    if (Time.unscaledTime >= _errorLogAt)
                    {
                        _errorLogAt = Time.unscaledTime + 10f;
                        Plugin.Log.LogError("Win.FarShafts: planning the window suns failed: " + e);
                    }
                }
            }
            // Win.CrossingBodies (P23): the bodies the game camera sees come through a window toward it (named before
            // BaseView, which leaves them in its view; carried after the sounds have been placed by where they are), until
            // its onPostRender.
            if (_crossing.Count > 0)
                CrossingToGame(cam);
            // Win.BaseHidesFar (P14.1): what the game did not load leaves the game camera's view until its onPostRender (not
            // while it looks at a far side: through a portal, or carried there for the lab's in-place reference).
            BaseView.Hide(this, cam, Suspended || CameraThroughFrame == Time.frameCount);
            _audio.Update(this, cam);
            if (_crossToGame.Count > 0)
                CarryToGame();
            // Suspended (lab reference): the camera has been put back on the player's side.
            if (CameraThroughFrame != Time.frameCount || Suspended)
                return;
            // Far.Proxies: the far side it stands on shows its proxied objects to it as well.
            ProxyDraw.GameCameraThrough(cam);
            // The game camera stands on the far side: the distant terrain and water hide around the player as if
            // they stood there (the reference position carried through the portal).
            if (ZNet.instance != null)
            {
                _mainNetRefPos = Shader.GetGlobalVector(s_netRefPos);
                _mainNetRefMoved = true;
                Shader.SetGlobalVector(s_netRefPos, CameraMatrix.MultiplyPoint3x4(ZNet.instance.GetReferencePosition()));
            }
            MovePlayerGlobals(CameraMatrix);
            if (Player.m_localPlayer != null && !LabNoSkyOcclusion)
                _sky.Show(s_mainSkyView, CameraMatrix.MultiplyPoint3x4(Player.m_localPlayer.transform.position));
            // The weather itself is the game's: it picks it by where its camera stands, so while the camera looks
            // back at your base its rain keeps falling there. Its particles, simulated around you, go around it.
            if (!LabFollowersStay)
            {
                MoveFollowers(CameraMatrix, cam.transform.position);
                _mainFollowersMoved = true;
            }
            if (Plugin.FarWeather.Value)
                FarMist.Show(cam.transform.position);
            if (!Plugin.ShowPlayer.Value)
                return;
            Player p = Player.m_localPlayer;
            GameObject visual = p != null ? p.GetVisual() : null;
            if (visual == null)
                return;
            _mainMovedVisual = visual.transform;
            _mainVisualPos = _mainMovedVisual.position;
            _mainVisualRot = _mainMovedVisual.rotation;
            _mainMovedVisual.SetPositionAndRotation(CameraMatrix.MultiplyPoint3x4(_mainVisualPos), PortalMath.Rotation(CameraMatrix) * _mainVisualRot);
        }

        // Win.ExplicitJitter's check: the game camera's TAA (PostProcessingBehaviour.OnPreCull, TaaComponent.SetProjectionMatrix)
        // has jittered this frame's projection before the windows read it here, by the offset it chose this frame
        // (jitterVector, screen units; the windows take (jp - jn) / 2, the same quantity). The offsets are compared, not
        // JitteredAtPreCull alone: the first of the TAA's 8 Halton samples is (0, 0), an unjittered projection that frame.
        // Main thread, no allocation.
        void CheckJitter(Camera cam, in Matrix4x4 jp, in Matrix4x4 jn)
        {
            if (!ReferenceEquals(cam, _taaCamera) || _taaPost == null)
            {
                _taaCamera = cam;
                _taaPost = cam.GetComponent<PostProcessingBehaviour>();
            }
            PostProcessingBehaviour post = _taaPost;
            if (post == null || !post.enabled || post.profile == null || post.profile.debugViews.willInterrupt)
                return;
            TaaComponent taa = s_taa(post);
            if (taa == null || taa.model == null || !taa.active)
                return;
            JitterTaaFrames++;
            Vector2 d = new Vector2((jp.m02 - jn.m02) * 0.5f, (jp.m12 - jn.m12) * 0.5f) - taa.jitterVector;
            if (d.sqrMagnitude < 1e-10f)
                return;
            JitterMisses++;
            JitterLastMissFrame = Time.frameCount;
            // Once a session, for a player's log: something (another mod) jitters or resets the game camera after this.
            if (JitterMisses == 60)
                Plugin.Log.LogWarning("The game's antialiasing jitter was not yet this frame's when the portal windows rendered " +
                                      "(60 frames so far): windows may shimmer or trail when the camera turns.");
        }

        Vector4 _mainNetRefPos;
        bool _mainNetRefMoved, _mainFollowersMoved, _mainEnvApplied;

        // Win.VanillaLook (P43): the window shader's flag for a reflection probe's render (the shader's half is the
        // renderer's), and whether it is up.
        static readonly int s_probeRender = Shader.PropertyToID("_RP_ProbeRender");
        bool _probeRenderSet;

        void ProbeRenderDone()
        {
            Shader.SetGlobalFloat(s_probeRender, 0f);
            _probeRenderSet = false;
        }

        void MainPostRender(Camera cam)
        {
            if (cam != null && cam.cameraType == CameraType.Reflection)
            {
                if (_probeShowing)
                    _reflections.PostRender(this, cam);
                _probeShowing = false;
                if (_probeRenderSet)
                    ProbeRenderDone();
                return;
            }
            if (cam != _main)
                return;
            // Win.Virtual: the sun cascades Unity fitted for this render, read before anything else renders.
            CaptureForVirtual(cam);
            BaseView.Show();
            // Win.CrossingBodies: the bodies the game camera drew carried through an opening, back where they are.
            if (_crossMain.Count > 0)
                PutBack(_crossMain);
            _crossToGame.Clear();
            BaseView.Crossing.Clear();
            if (_mainNetRefMoved)
            {
                _mainNetRefMoved = false;
                Shader.SetGlobalVector(s_netRefPos, _mainNetRefPos);
            }
            if (_mainFollowersMoved)
            {
                _mainFollowersMoved = false;
                RestoreFollowers();
            }
            RestorePlayerGlobals();
            _sky.Hide();
            FarMist.Hide();
            if (_mainEnvApplied)
            {
                _mainEnvApplied = false;
                FarEnvironment.Restore();
            }
            if (_mainMovedVisual == null)
                return;
            _mainMovedVisual.SetPositionAndRotation(_mainVisualPos, _mainVisualRot);
            _mainMovedVisual = null;
        }

        // Detail for the 3 frames before and 8 frames after each pass, to find a frame that jumps.
        struct FrameNote
        {
            public int Frame, Rendered, Windows;
            public Vector3 Player, Camera, CameraRot, Eye;
            public bool Through;
        }

        readonly FrameNote[] _recent = new FrameNote[3];

        void TraceFrame()
        {
            bool through = CameraThroughFrame == Time.frameCount;
            // The jump gets continuous motion vectors (PortalRenderer.UpdateMotion), so TAA keeps its history; a
            // reset made far, thin things (trees at the draw distance) blink for a frame.
            if (through != _lastThrough && LabResetTemporal && _main.TryGetComponent(out PostProcessingBehaviour post))
                post.ResetTemporalEffects();
            if (through != _lastThrough && Plugin.Verbose.Value)
                Plugin.Log.LogInfo($"Camera f{Time.frameCount}: {(through ? "now behind you through the portal" : "back on your side")} at {_main.transform.position:F2}" +
                                   (_renderer.LastJumpFrame == Time.frameCount
                                       ? $"; jump {_renderer.LastJumpLength:F2} m, off the portal pair by {_renderer.LastJumpMiss:F2} m"
                                       : "; no jump of the camera this frame"));
            _lastThrough = through;
            Player p = Player.m_localPlayer;
            var note = new FrameNote
            {
                Frame = Time.frameCount,
                Player = p != null ? p.transform.position : Vector3.zero,
                Eye = p != null ? p.m_eye.rotation.eulerAngles : Vector3.zero,
                Camera = _main.transform.position,
                CameraRot = _main.transform.rotation.eulerAngles,
                Through = through,
                Windows = _windows.Count
            };
            foreach (PortalWindow w in _windows)
                if (w.RenderedThisFrame)
                    note.Rendered++;
            if (_traceFrames == 8)
                foreach (FrameNote old in _recent.Where(n => n.Frame > 0).OrderBy(n => n.Frame))
                    LogNote("before", old);
            _recent[Time.frameCount % _recent.Length] = note;
            if (_traceFrames <= 0 || !Plugin.Verbose.Value)
                return;
            _traceFrames--;
            LogNote("after", note);
        }

        static void LogNote(string when, FrameNote n) =>
            Plugin.Log.LogInfo($"Trace {when} f{n.Frame}: player {n.Player:F2}, eye rot {n.Eye:F1}, camera {n.Camera:F2} rot {n.CameraRot:F1}, " +
                               $"through portal {n.Through}, windows rendered {n.Rendered}/{n.Windows}");
        Rigidbody _interpolationBody;
        RigidbodyInterpolation _interpolationMode;

        void LateUpdate()
        {
            if (Plugin.DumpKey.Value.IsDown())
                _dumpRequested = true;
        }

        // A teleported interpolated body would be drawn halfway between the two portals for one frame
        // (and the camera with it). Interpolation is off for the frame of the jump and comes back here.
        void RestoreInterpolation()
        {
            if (_interpolationBody != null)
                _interpolationBody.interpolation = _interpolationMode;
            _interpolationBody = null;
        }

        void SuspendInterpolation(Rigidbody body)
        {
            RestoreInterpolation();
            if (body.interpolation == RigidbodyInterpolation.None)
                return;
            _interpolationBody = body;
            _interpolationMode = body.interpolation;
            body.interpolation = RigidbodyInterpolation.None;
        }

        void FixedUpdate()
        {
            RestoreInterpolation();
            UpdateWalls();
            if (Live().Count == 0)
            {
                _playerTracked = null;
                _itemLast.Clear();
                return;
            }
            UpdatePlayer();
            if (Plugin.Items.Value)
                UpdateItems();
            if (Plugin.WildCrossing.Value || Api.Mechanics.IsOn(Api.Mechanics.FriendlyCross))
                UpdateCreatures();
        }

        // ---- creatures ---------------------------------------------------------------------------------

        readonly Dictionary<Character, Vector3> _creatureLast = new Dictionary<Character, Vector3>();
        readonly Dictionary<Character, Vector3> _creatureNow = new Dictionary<Character, Vector3>();
        static readonly AccessTools.FieldRef<BaseAI, List<Vector3>> s_aiPath = AccessTools.FieldRefAccess<BaseAI, List<Vector3>>("m_path");
        static readonly AccessTools.FieldRef<BaseAI, float> s_aiFindPathTime = AccessTools.FieldRefAccess<BaseAI, float>("m_lastFindPathTime");
        static readonly AccessTools.FieldRef<BaseAI, Vector3> s_aiFindPathTarget = AccessTools.FieldRefAccess<BaseAI, Vector3>("m_lastFindPathTarget");

        // Creatures this game simulates walk through the openings like you do (a wolf after you, a tamed boar after
        // you, a deer running from you).
        void UpdateCreatures()
        {
            _creatureNow.Clear();
            // Far.LinkScaling: the live links checked over once, not twice for every creature this game simulates (each
            // pass looks at every link's window and partner).
            List<PortalLink> live = FarFlags.LinkScaling ? Live() : null;
            foreach (Character c in Character.GetAllCharacters())
            {
                if (c == null || c.IsPlayer() || c.IsDead() || c.IsAttached() || !c.TryGetComponent(out ZNetView nview) || !nview.IsValid() || !nview.IsOwner()
                    || !c.TryGetComponent(out Rigidbody body))
                    continue;
                if (!FarLife.MayCross(c))
                    continue;
                Vector3 now = c.GetCenterPoint();
                PortalLink near = null;
                foreach (PortalLink link in live ?? Live())
                    if ((link.Frame.Center - now).sqrMagnitude < 36f)
                    {
                        near = link;
                        break;
                    }
                if (near == null)
                    continue;
                if (_creatureLast.TryGetValue(c, out Vector3 last) && (now - last).sqrMagnitude < 25f)
                    foreach (PortalLink link in live ?? Live())
                        if (link.Frame.Crosses(last, now, out _, out _))
                        {
                            // Far.Readiness3: only into a far side that takes creatures (CanThrow); otherwise it stays on this
                            // side, as its wall stops it.
                            if (Readiness.CanThrow(link))
                            {
                                MoveCreature(c, body, link);
                                now = c.GetCenterPoint();
                            }
                            else
                                Readiness.LabCreaturesHeld++;
                            break;
                        }
                _creatureNow[c] = now;
            }
            _creatureLast.Clear();
            foreach (var kv in _creatureNow)
                _creatureLast[kv.Key] = kv.Value;
        }

        void MoveCreature(Character c, Rigidbody body, PortalLink from)
        {
            Matrix4x4 m = PortalMath.Pair(from.Frame, from.Partner.Frame);
            Quaternion r = PortalMath.Rotation(m);
            Vector3 oldPos = body.position, newPos = m.MultiplyPoint3x4(oldPos);
            body.position = newPos;
            body.rotation = r * body.rotation;
            c.transform.SetPositionAndRotation(newPos, body.rotation);
            body.linearVelocity = r * body.linearVelocity;
            body.angularVelocity = r * body.angularVelocity;
            s_currentVel(c) = r * s_currentVel(c);
            s_lookYaw(c) = r * s_lookYaw(c);
            s_lookDir(c) = r * s_lookDir(c);
            s_moveDir(c) = r * s_moveDir(c);
            s_maxAirAltitude(c) += newPos.y - oldPos.y;
            s_lastPos(c) = m.MultiplyPoint3x4(s_lastPos(c));
            s_lazyPos(c) = m.MultiplyPoint3x4(s_lazyPos(c));
            s_lastGroundPoint(c) = m.MultiplyPoint3x4(s_lastGroundPoint(c));
            // Its path led to the opening: a new one from here.
            BaseAI ai = c.GetBaseAI();
            if (ai != null)
            {
                s_aiPath(ai).Clear();
                s_aiFindPathTime(ai) = -999f;
                s_aiFindPathTarget(ai) = new Vector3(-999999f, -999999f, -999999f);
            }
            if (c.TryGetComponent(out ZNetView nview) && nview.IsValid())
            {
                nview.GetZDO().SetPosition(newPos);
                Areas.Track(nview.GetZDO());
                PortalServer.Crossed(nview.GetZDO());
                CrossedNotify(nview);
            }
            ApplyWalls(c);
            Physics.SyncTransforms();
            // FARWORLD V2: its far area is hot (AreaPolicy), and who stands by which portal is listed again (FarLife).
            FarWorld.OnCrossed(from, false);
            FarLife.Moved();
            LabCreaturesThrough++;
            Plugin.Debug($"{c.name} through {from.name} -> {from.Partner.name}: {oldPos:F1} -> {newPos:F1}");
        }

        // Lab: creatures that went through, and the local player's walks through.
        internal static int LabCreaturesThrough, LabPlayerCrossings;

        // P48: a crossing moves a loaded networked object in a jump, which the engine's SectorInstanceIndex would otherwise
        // see only at its next filing (within about a second): from now until its next upkeep every answer looks at it
        // where it is (NotifyMoved; nothing while that feature is off). AreaIndex is not told: an effect area on a body that
        // crosses (a character, an item, a projectile) is on a Rigidbody, which it never pins and tests by its current
        // bounds on every query, and a character's crossing syncs the physics transforms at once (MovePlayer, MoveCreature).
        static void CrossedNotify(ZNetView view) => Engine.SectorInstanceIndexModule.NotifyMoved(view);

        // ---- Win.CrossingBodies (P23) ---------------------------------------------------------------------------------
        // A body in an opening was cut there: its part past the plane is behind the window's surface, and the window's view
        // of the far side has nothing in its place until the body's centre crosses and it jumps over (MoveCreature, or a
        // peer's own crossing), when the part still on this side goes instead. As the player's copy does for you
        // (ShowPlayerThroughPortal), every other body straddling a live opening X (a creature, another player, a ship, a
        // cart: its box reaching past the plane on both sides, its centre over the opening) is drawn once more where the
        // pair carries it, X's side -> its partner P's:
        //  - in the renders of X's window, whose view is P's side: it comes out of P there, as the rest of it goes in here;
        //  - for the game camera while it stands nearer P than X and P's window shows (the body seen through P, from the
        //    side it is coming to or has just left): the part on the camera's side shows in front of P's window, the rest
        //    goes behind its surface, by the view P's window shows (scaled as that view is, for a smaller far opening).
        // What moves is everything under the body's root, never the root itself (a write to a Rigidbody's own transform
        // reaches its physics pose at the next sync), for that render only: back right after it (AfterWindowRender) and at
        // the game camera's onPostRender (MainPostRender). With Win.CrossNearPlane off the near plane goes to such a body
        // too, as to yours (PlayerNear). Listed once a frame before the windows render; a body's parts and box once in
        // BodyRelist. Off: nothing is listed or moved.
        sealed class Body
        {
            public Transform Root;
            public Transform[] Parts = s_noParts;
            // Its box in the root's space (its renderers' bounds when listed).
            public Vector3 Center, Extents;
            public int Children = -1;
            public float ListedAt;
        }

        struct Crossing
        {
            public Body Body;
            // The live link whose opening it straddles (X), and its box's centre this frame.
            public PortalLink Link;
            public Vector3 Center;
        }

        static readonly Transform[] s_noParts = new Transform[0];
        static readonly AccessTools.FieldRef<List<Vagon>> s_carts =
            AccessTools.StaticFieldRefAccess<List<Vagon>>(AccessTools.Field(typeof(Vagon), "m_instances"));
        // How far a body's root may stand from an opening's centre, past the opening's size, and still reach into it.
        const float CreatureReach = 8f, VehicleReach = 24f;
        // Past its box on either side of the plane (limbs at its edge), and round the opening for its centre.
        const float StraddleSlack = 0.05f, OverMargin = 0.1f;
        const float BodyRelist = 10f, CrossCell = 32f;
        readonly Dictionary<long, int> _crossCells = new Dictionary<long, int>();
        int[] _crossNodeLink = new int[64], _crossNodeNext = new int[64];
        readonly Dictionary<Transform, Body> _crossBodies = new Dictionary<Transform, Body>();
        readonly List<Transform> _crossGone = new List<Transform>();
        readonly List<Crossing> _crossing = new List<Crossing>();
        readonly List<Renderer> _crossRenderers = new List<Renderer>();
        // Parts carried for a window render, and for the game camera, with the pose each had.
        readonly List<(Transform, Vector3, Quaternion, Vector3)> _crossMoved = new List<(Transform, Vector3, Quaternion, Vector3)>();
        readonly List<(Transform, Vector3, Quaternion, Vector3)> _crossMain = new List<(Transform, Vector3, Quaternion, Vector3)>();
        // This frame's bodies for the game camera, with the matrix each goes by; the game camera's frustum they are tested by.
        readonly List<(Body, Matrix4x4)> _crossToGame = new List<(Body, Matrix4x4)>();
        static readonly Plane[] s_crossPlanes = new Plane[6];
        int _crossingFrame = -1;
        float _crossSweepAt;
        // Lab (Win.CrossingBodies): copies drawn so far (a body carried for a window render or for the game camera), and
        // the bodies in an opening at the last listing.
        internal static long LabCrossingCopies;
        internal static int LabCrossingBodies;

        // Once a frame, before the windows render: the bodies straddling a live opening.
        void ListCrossing()
        {
            _crossing.Clear();
            _crossingFrame = Time.frameCount;
            LabCrossingBodies = 0;
            if (!WindowFlags.CrossingBodies)
            {
                if (_crossBodies.Count > 0)
                    _crossBodies.Clear();
                return;
            }
            List<PortalLink> live = Live();
            if (live.Count == 0)
                return;
            float now = Time.unscaledTime;
            // The openings laid out in cells (XZ), each where a creature's root may stand and reach into it: every loaded
            // character is looked up once, not against every live opening (forty in a hall of twenty).
            _crossCells.Clear();
            int nodes = 0;
            for (int k = 0; k < live.Count; k++)
            {
                PortalFrame f = live[k].Frame;
                float r = CreatureReach + Mathf.Max(f.HalfSize.x, f.HalfSize.y);
                int x0 = Mathf.FloorToInt((f.Center.x - r) / CrossCell), x1 = Mathf.FloorToInt((f.Center.x + r) / CrossCell);
                int z0 = Mathf.FloorToInt((f.Center.z - r) / CrossCell), z1 = Mathf.FloorToInt((f.Center.z + r) / CrossCell);
                for (int x = x0; x <= x1; x++)
                    for (int z = z0; z <= z1; z++)
                    {
                        if (nodes == _crossNodeLink.Length)
                        {
                            System.Array.Resize(ref _crossNodeLink, nodes * 2);
                            System.Array.Resize(ref _crossNodeNext, nodes * 2);
                        }
                        long key = ViewCellKey(x, z);
                        _crossNodeLink[nodes] = k;
                        _crossNodeNext[nodes] = _crossCells.TryGetValue(key, out int head) ? head : -1;
                        _crossCells[key] = nodes++;
                    }
            }
            Player me = Player.m_localPlayer;
            List<Character> all = Character.GetAllCharacters();
            for (int i = 0; i < all.Count; i++)
            {
                Character c = all[i];
                // You have your own copy (ShowPlayerThroughPortal).
                if (c == null || c == me)
                    continue;
                Transform root = c.transform;
                Vector3 at = root.position;
                if (!_crossCells.TryGetValue(ViewCellKey(Mathf.FloorToInt(at.x / CrossCell), Mathf.FloorToInt(at.z / CrossCell)), out int node)
                    || c.IsDead() || !c.gameObject.activeInHierarchy)
                    continue;
                Body b = null;
                for (; node >= 0; node = _crossNodeNext[node])
                    if (Consider(root, live[_crossNodeLink[node]], CreatureReach, now, ref b))
                        break;
            }
            // Ships and carts are few: each against every opening.
            List<IMonoUpdater> ships = Ship.Instances;
            for (int i = 0; i < ships.Count; i++)
                if (ships[i] is Ship ship && ship != null)
                    ConsiderAll(ship.transform, live, now);
            List<Vagon> carts = s_carts();
            if (carts != null)
                for (int i = 0; i < carts.Count; i++)
                {
                    Vagon cart = carts[i];
                    if (cart != null && cart.isActiveAndEnabled)
                        ConsiderAll(cart.transform, live, now);
                }
            LabCrossingBodies = _crossing.Count;
            if (now < _crossSweepAt)
                return;
            // Bodies gone, or long out of every opening, leave the cache now and then.
            _crossSweepAt = now + BodyRelist;
            foreach (KeyValuePair<Transform, Body> kv in _crossBodies)
                if (kv.Key == null || now - kv.Value.ListedAt > 3f * BodyRelist)
                    _crossGone.Add(kv.Key);
            for (int i = 0; i < _crossGone.Count; i++)
                _crossBodies.Remove(_crossGone[i]);
            _crossGone.Clear();
        }

        // The first live opening this ship or cart straddles, if any.
        void ConsiderAll(Transform root, List<PortalLink> live, float now)
        {
            Body b = null;
            for (int k = 0; k < live.Count; k++)
                if (Consider(root, live[k], VehicleReach, now, ref b))
                    return;
        }

        // Whether the body straddles this live opening (then listed); its parts and box looked up once into `b`.
        bool Consider(Transform root, PortalLink link, float reach, float now, ref Body b)
        {
            PortalFrame f = link.Frame;
            float r = reach + Mathf.Max(f.HalfSize.x, f.HalfSize.y);
            if ((f.Center - root.position).sqrMagnitude > r * r)
                return false;
            if (b == null)
                b = BodyOf(root, now);
            Vector3 center = root.TransformPoint(b.Center);
            if (!Straddles(f, root, b, center))
                return false;
            _crossing.Add(new Crossing { Body = b, Link = link, Center = center });
            return true;
        }

        // Its box reaches past the plane on both sides, and its centre lies over the opening.
        static bool Straddles(PortalFrame f, Transform root, Body b, Vector3 center)
        {
            Vector3 n = f.Forward, e = b.Extents;
            // The box's half depth along the plane's normal (its axes are the root's, scaled).
            float depth = Mathf.Abs(Vector3.Dot(root.TransformVector(new Vector3(e.x, 0f, 0f)), n))
                          + Mathf.Abs(Vector3.Dot(root.TransformVector(new Vector3(0f, e.y, 0f)), n))
                          + Mathf.Abs(Vector3.Dot(root.TransformVector(new Vector3(0f, 0f, e.z)), n));
            if (Mathf.Abs(f.SignedDistance(center)) > depth + StraddleSlack)
                return false;
            Vector3 local = Quaternion.Inverse(f.Rotation) * (center - f.Center);
            return f.ContainsLocal(new Vector2(local.x, local.y), OverMargin);
        }

        // A body's parts (its root's children) and box, looked up again after BodyRelist or when its children change.
        Body BodyOf(Transform root, float now)
        {
            int children = root.childCount;
            if (_crossBodies.TryGetValue(root, out Body b) && b.Children == children && now - b.ListedAt < BodyRelist)
                return b;
            if (b == null)
            {
                if (_crossBodies.Count > 4096)
                    _crossBodies.Clear();
                b = new Body { Root = root };
                _crossBodies[root] = b;
            }
            b.ListedAt = now;
            b.Children = children;
            if (b.Parts.Length != children)
                b.Parts = children > 0 ? new Transform[children] : s_noParts;
            for (int i = 0; i < children; i++)
                b.Parts[i] = root.GetChild(i);
            root.GetComponentsInChildren(false, _crossRenderers);
            Matrix4x4 toLocal = root.worldToLocalMatrix;
            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            for (int i = 0; i < _crossRenderers.Count; i++)
            {
                Renderer r = _crossRenderers[i];
                // Effects reach far past the body (and move with the world, not with it).
                if (r == null || !r.enabled || r is ParticleSystemRenderer || r is LineRenderer || r is TrailRenderer)
                    continue;
                Bounds w = r.bounds;
                Vector3 c = w.center, x = w.extents;
                for (int k = 0; k < 8; k++)
                {
                    Vector3 p = toLocal.MultiplyPoint3x4(new Vector3(c.x + ((k & 1) != 0 ? x.x : -x.x), c.y + ((k & 2) != 0 ? x.y : -x.y),
                                                                     c.z + ((k & 4) != 0 ? x.z : -x.z)));
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
            }
            _crossRenderers.Clear();
            if (min.x > max.x)
            {
                // Nothing drawn yet: half a metre round its root.
                b.Center = Vector3.zero;
                b.Extents = new Vector3(0.5f, 0.5f, 0.5f);
            }
            else
            {
                b.Center = (min + max) * 0.5f;
                b.Extents = (max - min) * 0.5f;
            }
            return b;
        }

        // The bodies in `entry`'s opening, carried for this render of its window: by `m` for its own view (with its view
        // scale), by the pair for a deeper level's (the opening then stands in the view of the level above).
        void CopyCrossing(PortalWindow entry, Matrix4x4 m, Vector3 camPos)
        {
            if (_crossingFrame != Time.frameCount || entry.Partner == null)
                return;
            bool own = entry.HasView && entry.ViewPos == camPos, paired = false;
            Matrix4x4 pair = Matrix4x4.identity;
            for (int i = 0; i < _crossing.Count; i++)
            {
                Crossing c = _crossing[i];
                if (c.Link == null || c.Link.Window != entry)
                    continue;
                if (!own && !paired)
                {
                    pair = PortalMath.Pair(entry.Frame, entry.Partner.Frame);
                    paired = true;
                }
                MoveBody(c.Body, own ? m : pair, _crossMoved);
            }
        }

        // For the game camera: the bodies in an opening it sees through the partner's window, while it stands nearer that
        // partner, each with the inverse of the view that window shows (CarryToGame moves them). A body the game camera sees
        // where it stands (its box at X in the camera's frustum: two portals in one hall, a distant window with X in plain
        // sight) stays there, as the game shows it; only its copy in front of the partner is missing then.
        void CrossingToGame(Camera cam)
        {
            _crossToGame.Clear();
            if (_crossingFrame != Time.frameCount)
                return;
            Vector3 eye = cam.transform.position;
            bool planes = false;
            for (int i = 0; i < _crossing.Count; i++)
            {
                Crossing c = _crossing[i];
                PortalLink x = c.Link, p = x != null ? x.Partner : null;
                if (p == null || p.Window == null || !p.Window.RenderedThisFrame || !_byWindow.ContainsKey(p.Window)
                    || (eye - p.Frame.Center).sqrMagnitude >= (eye - x.Frame.Center).sqrMagnitude)
                    continue;
                Body b = c.Body;
                Transform root = b.Root;
                if (root == null)
                    continue;
                if (!planes)
                {
                    GeometryUtility.CalculateFrustumPlanes(cam, s_crossPlanes);
                    planes = true;
                }
                Vector3 ax = root.TransformVector(b.Extents.x, 0f, 0f), ay = root.TransformVector(0f, b.Extents.y, 0f),
                        az = root.TransformVector(0f, 0f, b.Extents.z);
                var extents = new Vector3(Mathf.Abs(ax.x) + Mathf.Abs(ay.x) + Mathf.Abs(az.x), Mathf.Abs(ax.y) + Mathf.Abs(ay.y) + Mathf.Abs(az.y),
                    Mathf.Abs(ax.z) + Mathf.Abs(ay.z) + Mathf.Abs(az.z));
                if (GeometryUtility.TestPlanesAABB(s_crossPlanes, new Bounds(root.TransformPoint(b.Center), extents * 2f)))
                    continue;
                _crossToGame.Add((c.Body, PortalMath.View(p.Frame, x.Frame, eye, out _).inverse));
                BaseView.Crossing.Add(c.Body.Root);
            }
        }

        void CarryToGame()
        {
            for (int i = 0; i < _crossToGame.Count; i++)
                MoveBody(_crossToGame[i].Item1, _crossToGame[i].Item2, _crossMain);
            _crossToGame.Clear();
        }

        // Everything under the body's root carried by `m` (scaled by its scale), each part's pose kept in `into`.
        static void MoveBody(Body b, Matrix4x4 m, List<(Transform, Vector3, Quaternion, Vector3)> into)
        {
            Quaternion r = PortalMath.Rotation(m);
            float s = m.GetColumn(0).magnitude;
            bool scaled = Mathf.Abs(s - 1f) > 1e-4f;
            Transform[] parts = b.Parts;
            for (int i = 0; i < parts.Length; i++)
            {
                Transform t = parts[i];
                // Gone, or moved to another parent since listed.
                if (t == null || t.parent != b.Root)
                    continue;
                Vector3 pos = t.position, scale = t.localScale;
                Quaternion rot = t.rotation;
                into.Add((t, pos, rot, scale));
                t.SetPositionAndRotation(m.MultiplyPoint3x4(pos), r * rot);
                if (scaled)
                    t.localScale = scale * s;
            }
            LabCrossingCopies++;
        }

        static void PutBack(List<(Transform, Vector3, Quaternion, Vector3)> moved)
        {
            for (int i = moved.Count - 1; i >= 0; i--)
            {
                (Transform t, Vector3 pos, Quaternion rot, Vector3 scale) = moved[i];
                if (t == null)
                    continue;
                t.SetPositionAndRotation(pos, rot);
                if (t.localScale != scale)
                    t.localScale = scale;
            }
            moved.Clear();
        }

        // Win.CrossingBodies with Win.CrossNearPlane off: the near plane goes to the bodies in this opening (their copies
        // at the exit) and in the exit's, as PlayerNear takes yours.
        float CrossingNear(PortalLink entryLink, PortalLink exitLink, Vector3 camPos, Matrix4x4 view, float near)
        {
            if (_crossingFrame != Time.frameCount || _crossing.Count == 0)
                return near;
            PortalFrame exit = exitLink.Frame;
            for (int i = 0; i < _crossing.Count; i++)
            {
                Crossing c = _crossing[i];
                if (c.Link == entryLink)
                    near = NearAt(PortalMath.Pair(entryLink.Frame, exit).MultiplyPoint3x4(c.Center), exit, camPos, view, near);
                else if (c.Link == exitLink)
                    near = NearAt(c.Center, exit, camPos, view, near);
            }
            return near;
        }

        // ---- player -------------------------------------------------------------------------------------

        void UpdatePlayer()
        {
            Player p = Player.m_localPlayer;
            if (p == null || !p.TryGetComponent(out Rigidbody body))
            {
                _playerTracked = null;
                return;
            }
            Vector3 now = body.position + Vector3.up * PlayerRefHeight;
            if (_playerTracked != p || (now - _playerLast).sqrMagnitude > 25f || p.IsTeleporting() || p.IsDead() || p.IsAttached())
            {
                _playerTracked = p;
                _playerLast = now;
                return;
            }
            foreach (PortalLink link in Live())
            {
                if (!link.Frame.Crosses(_playerLast, now, out _, out _))
                    continue;
                if (CanPlayerPass(p, link))
                {
                    MovePlayer(p, body, link);
                    now = body.position + Vector3.up * PlayerRefHeight;
                    Api.Mechanics.Crossed(p, link.Portal, link.Partner.Portal);
                }
                break;
            }
            _playerLast = now;
        }

        bool CanPlayerPass(Player p, PortalLink link)
        {
            if (PlayerMayPass(p, link))
                return true;
            if (!Readiness.CanWalk(link))
                Readiness.LabPlayerHeld++;
            SayWhyBlocked(p, link);
            return false;
        }

        // What keeps a player from stepping through (as the vanilla teleport checks), or null.
        static string Blocked(Player p, PortalLink link)
        {
            ZoneSystem zs = ZoneSystem.instance;
            if (zs != null && zs.GetGlobalKey(GlobalKeys.NoPortals))
                return "$msg_blocked";
            if (zs != null && zs.GetGlobalKey(GlobalKeys.NoBossPortals)
                && ((RandEventSystem.instance != null && RandEventSystem.instance.GetBossEvent() != null)
                    || (zs.GetGlobalKey(GlobalKeys.activeBosses, out float bosses) && bosses > 0f)))
                return "$msg_blockedbyboss";
            if (Plugin.RespectItemRestrictions.Value && !p.IsTeleportable(link.Portal != null && link.Portal.m_allowAllItems))
                return "$msg_noteleport";
            return null;
        }

        // Whether this player may step through this live portal now. Another mod may decide (ore it lets through).
        // Far.Readiness3: and only once its far side is walkable (CanWalk; other mods are not asked before that).
        internal bool PlayerMayPass(Player p, PortalLink link) =>
            Readiness.CanWalk(link) && Api.Mechanics.AskCross(p, link.Portal, link.Partner != null ? link.Partner.Portal : null, Blocked(p, link) == null);

        // The game's message for a closed portal, at most every 2 s. A far side not walkable yet (Far.Readiness3) says
        // nothing: the swirl that shows someone is stopped stands in the opening meanwhile.
        internal void SayWhyBlocked(Player p, PortalLink link)
        {
            if (!Readiness.CanWalk(link) || PlayerMayPass(p, link) || Time.time - _blockedMessageAt < 2f)
                return;
            _blockedMessageAt = Time.time;
            p.Message(MessageHud.MessageType.Center, Blocked(p, link) ?? "$msg_noteleport");
        }

        // ---- walls ---------------------------------------------------------------------------------------

        // Each portal's wall (PortalLink.Wall) stops every character, except those that may pass now: the local
        // player through a live portal it may use (and, for a moment after stepping out, the exit's wall), creatures
        // allowed to walk through (FarLife.MayCross). Only characters near a portal are looked at.
        readonly Dictionary<Character, Collider[]> _bodies = new Dictionary<Character, Collider[]>();
        readonly Dictionary<(Collider wall, Collider body), bool> _ignored = new Dictionary<(Collider, Collider), bool>();
        readonly List<(Collider, Collider)> _ignoredDrop = new List<(Collider, Collider)>();
        PortalLink _exitGrace;
        float _exitGraceUntil, _ignoredClearAt;

        // Every physics step, before anything crosses.
        void UpdateWalls()
        {
            // Unity forgets an ignored pair when either collider is switched off and on: set them all anew now and then.
            if (Time.time >= _ignoredClearAt)
            {
                _ignoredClearAt = Time.time + 1f;
                _ignored.Clear();
            }
            foreach (Character c in Character.GetAllCharacters())
                if (c != null)
                    ApplyWalls(c);
            ApplyItemWalls();
        }

        void ApplyWalls(Character c)
        {
            Vector3 at = c.transform.position;
            foreach (PortalLink link in PortalLink.All)
            {
                Collider wall = link.Wall;
                if (wall == null || (link.Frame.Center - at).sqrMagnitude > 10f * 10f)
                    continue;
                // Far.Readiness3: you once the far side is walkable (PlayerMayPass; the exit's grace whatever its own),
                // other players likewise, creatures once it takes them (CanThrow).
                bool pass;
                if (c == Player.m_localPlayer)
                    pass = link.IsActive && ((link == _exitGrace && Time.time < _exitGraceUntil) || PlayerMayPass((Player)c, link));
                else if (c.IsPlayer())
                    pass = link.IsActive && Readiness.CanWalk(link);
                else
                    pass = link.IsActive && Readiness.CanThrow(link) && FarLife.MayCross(c);
                foreach (Collider body in BodyOf(c))
                {
                    var key = (wall, body);
                    if (_ignored.TryGetValue(key, out bool was) && was == pass)
                        continue;
                    Physics.IgnoreCollision(wall, body, pass);
                    _ignored[key] = pass;
                }
            }
        }

        // Dropped items by a portal's wall: those that may go through (as the vanilla teleport would let them be
        // carried) fly through it, the rest bounce off it like off any wall.
        readonly Collider[] _itemHits = new Collider[64];

        void ApplyItemWalls()
        {
            if (FarFlags.LinkScaling)
            {
                ApplyItemWallsNear();
                return;
            }
            foreach (PortalLink link in PortalLink.All)
            {
                Collider wall = link.Wall;
                if (wall == null)
                    continue;
                ApplyItemWall(link, wall);
            }
        }

        void ApplyItemWall(PortalLink link, Collider wall)
        {
            PortalFrame f = link.Frame;
            int n = Physics.OverlapBoxNonAlloc(f.Center, new Vector3(f.HalfSize.x + 1f, f.HalfSize.y + 1f, 3f), _itemHits, f.Rotation, _itemMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                Collider body = _itemHits[i];
                if (body == null || body.isTrigger)
                    continue;
                ItemDrop drop = body.attachedRigidbody != null ? body.attachedRigidbody.GetComponent<ItemDrop>() : body.GetComponentInParent<ItemDrop>();
                if (drop == null)
                    continue;
                // Far.Readiness3: once the far side takes thrown things (CanThrow); until then the item bounces off.
                bool pass = link.IsActive && Readiness.CanThrow(link) && ItemMayPass(drop, link);
                var key = (wall, body);
                if (_ignored.TryGetValue(key, out bool was) && was == pass)
                    continue;
                Physics.IgnoreCollision(wall, body, pass);
                _ignored[key] = pass;
            }
        }

        // Far.LinkScaling: the box query by every portal's wall every physics step (forty walls in a hall of twenty)
        // nearly always finds nothing, and was a quarter of a millisecond. The game lists every dropped item
        // (ItemDrop.s_instances, from its Awake to its OnDestroy); only walls with one near enough for its colliders
        // to reach the box are queried, which finds the same items. The walls are laid out in 32 m cells (XZ) for it.
        static readonly AccessTools.FieldRef<List<ItemDrop>> s_itemDrops =
            AccessTools.StaticFieldRefAccess<List<ItemDrop>>(AccessTools.Field(typeof(ItemDrop), "s_instances"));
        const float WallCell = 32f;
        // Past the query box: how far an item's colliders reach from its root, with what it moves in a step.
        const float ItemReach = 4f;
        readonly List<PortalLink> _wallLinks = new List<PortalLink>();
        readonly List<Collider> _wallColliders = new List<Collider>();
        readonly List<Vector4> _wallReach = new List<Vector4>();
        readonly Dictionary<long, int> _wallCells = new Dictionary<long, int>();
        int[] _wallNodeOf = new int[64], _wallNodeNext = new int[64];
        bool[] _wallNear = new bool[64];
        // Lab: wall box queries made under Far.LinkScaling so far.
        internal static long LabItemWallQueries;

        void ApplyItemWallsNear()
        {
            List<ItemDrop> drops = s_itemDrops();
            if (drops == null || drops.Count == 0)
                return;
            _wallLinks.Clear();
            _wallColliders.Clear();
            _wallReach.Clear();
            _wallCells.Clear();
            int nodes = 0;
            foreach (PortalLink link in PortalLink.All)
            {
                Collider wall = link.Wall;
                if (wall == null)
                    continue;
                PortalFrame f = link.Frame;
                float hx = f.HalfSize.x + 1f, hy = f.HalfSize.y + 1f;
                float reach = Mathf.Sqrt(hx * hx + hy * hy + 3f * 3f) + ItemReach;
                int w = _wallLinks.Count;
                _wallLinks.Add(link);
                _wallColliders.Add(wall);
                _wallReach.Add(new Vector4(f.Center.x, f.Center.y, f.Center.z, reach * reach));
                int x0 = Mathf.FloorToInt((f.Center.x - reach) / WallCell), x1 = Mathf.FloorToInt((f.Center.x + reach) / WallCell);
                int z0 = Mathf.FloorToInt((f.Center.z - reach) / WallCell), z1 = Mathf.FloorToInt((f.Center.z + reach) / WallCell);
                for (int x = x0; x <= x1; x++)
                    for (int z = z0; z <= z1; z++)
                    {
                        if (nodes == _wallNodeOf.Length)
                        {
                            System.Array.Resize(ref _wallNodeOf, nodes * 2);
                            System.Array.Resize(ref _wallNodeNext, nodes * 2);
                        }
                        long key = ViewCellKey(x, z);
                        _wallNodeOf[nodes] = w;
                        _wallNodeNext[nodes] = _wallCells.TryGetValue(key, out int head) ? head : -1;
                        _wallCells[key] = nodes++;
                    }
            }
            int walls = _wallLinks.Count;
            if (walls == 0)
                return;
            if (_wallNear.Length < walls)
                _wallNear = new bool[Mathf.NextPowerOfTwo(walls)];
            System.Array.Clear(_wallNear, 0, walls);
            for (int d = 0; d < drops.Count; d++)
            {
                ItemDrop drop = drops[d];
                if (drop == null)
                    continue;
                Vector3 at = drop.transform.position;
                if (!_wallCells.TryGetValue(ViewCellKey(Mathf.FloorToInt(at.x / WallCell), Mathf.FloorToInt(at.z / WallCell)), out int node))
                    continue;
                for (; node >= 0; node = _wallNodeNext[node])
                {
                    int w = _wallNodeOf[node];
                    Vector4 r = _wallReach[w];
                    float dx = at.x - r.x, dy = at.y - r.y, dz = at.z - r.z;
                    if (dx * dx + dy * dy + dz * dz <= r.w)
                        _wallNear[w] = true;
                }
            }
            for (int w = 0; w < walls; w++)
                if (_wallNear[w])
                {
                    LabItemWallQueries++;
                    ApplyItemWall(_wallLinks[w], _wallColliders[w]);
                }
        }

        static bool ItemMayPass(ItemDrop drop, PortalLink link) =>
            !Plugin.RespectItemRestrictions.Value || drop.m_itemData == null || drop.m_itemData.m_shared.m_teleportable
            || (link.Portal != null && link.Portal.m_allowAllItems)
            || (ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(GlobalKeys.TeleportAll));

        Collider[] BodyOf(Character c)
        {
            if (!_bodies.TryGetValue(c, out Collider[] cols) || cols.Length == 0 || cols[0] == null)
            {
                // The character's own solid colliders (its capsule), not hit boxes or weapons in its children.
                cols = c.GetComponents<Collider>().Where(x => !x.isTrigger).ToArray();
                if (_bodies.Count > 2000)
                    _bodies.Clear();
                _bodies[c] = cols;
            }
            return cols;
        }

        void MovePlayer(Player p, Rigidbody body, PortalLink from)
        {
            Matrix4x4 m = PortalMath.Pair(from.Frame, from.Partner.Frame);
            Quaternion r = PortalMath.Rotation(m);
            Transform t = p.transform;
            Vector3 oldPos = body.position;
            Vector3 newPos = m.MultiplyPoint3x4(oldPos);
            Quaternion newRot = r * body.rotation;

            SuspendInterpolation(body);
            body.position = newPos;
            body.rotation = newRot;
            t.SetPositionAndRotation(newPos, newRot);
            body.linearVelocity = r * body.linearVelocity;
            body.angularVelocity = r * body.angularVelocity;

            Character c = p;
            s_currentVel(c) = r * s_currentVel(c);
            s_lookYaw(c) = r * s_lookYaw(c);
            s_lookDir(c) = r * s_lookDir(c);
            s_moveDir(c) = r * s_moveDir(c);
            s_maxAirAltitude(c) += newPos.y - oldPos.y;
            s_lastPos(c) = m.MultiplyPoint3x4(s_lastPos(c));
            s_lazyPos(c) = m.MultiplyPoint3x4(s_lazyPos(c));
            s_lastGroundPoint(c) = m.MultiplyPoint3x4(s_lastGroundPoint(c));
            s_queuedDodgeDir(p) = r * s_queuedDodgeDir(p);
            p.SetMouseLook(Vector2.zero);
            // Cloth simulates in world space: without a reset the cape stretches across the jump for a frame
            // (the vanilla teleport resets it too).
            foreach (Cloth cloth in p.GetComponentsInChildren<Cloth>())
                if (cloth.enabled)
                {
                    cloth.enabled = false;
                    cloth.enabled = true;
                }

            GameCamera cam = GameCamera.instance;
            if (cam != null)
            {
                s_camPlayerPos(cam) = m.MultiplyPoint3x4(s_camPlayerPos(cam));
                s_camPlayerVel(cam) = r * s_camPlayerVel(cam);
                s_camBaseOffset(cam) = r * s_camBaseOffset(cam);
                s_camBaseVel(cam) = r * s_camBaseVel(cam);
            }
            // Out of the exit's opening, whatever that portal's own rules for entering it.
            _exitGrace = from.Partner;
            _exitGraceUntil = Time.time + 1.5f;
            LabPlayerCrossings++;
            // Far.ArriveOnCross: the far weather takes over with the camera's jump that follows.
            _playerCrossedFrame = Time.frameCount;
            if (p.TryGetComponent(out ZNetView pview) && pview.IsValid())
            {
                PortalServer.Crossed(pview.GetZDO());
                CrossedNotify(pview);
            }
            // FARWORLD V2 (ARCHITECTURE-v2.md §3.13): the area just entered is hot, and the side just left, now far, is
            // walkable from the exit at once (FarWorld.OnCrossed); who stands by which portal is listed again (FarLife).
            FarWorld.OnCrossed(from, true);
            FarLife.Moved();
            ApplyWalls(p);
            Physics.SyncTransforms();
            _traceFrames = 8;
            Plugin.Debug($"f{Time.frameCount} Player through {from.name} -> {from.Partner.name}: {oldPos} -> {newPos}, speed {body.linearVelocity.magnitude:0.0}, interpolation {_interpolationMode}");
        }

        // ---- dropped items -------------------------------------------------------------------------------

        void UpdateItems()
        {
            _itemNow.Clear();
            foreach (PortalLink link in Live())
            {
                PortalFrame f = link.Frame;
                var half = new Vector3(f.HalfSize.x + 0.5f, f.HalfSize.y + 0.5f, 3f);
                int n = Physics.OverlapBoxNonAlloc(f.Center, half, _hits, f.Rotation, _itemMask, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < n; i++)
                {
                    Rigidbody body = _hits[i].attachedRigidbody;
                    if (body == null || _itemNow.ContainsKey(body) || !body.TryGetComponent(out ItemDrop drop))
                        continue;
                    ZNetView nview = drop.GetComponent<ZNetView>();
                    if (nview == null || !nview.IsValid() || !nview.IsOwner())
                        continue;
                    Vector3 now = body.position;
                    if (_itemLast.TryGetValue(body, out Vector3 last) && (now - last).sqrMagnitude < 25f)
                        now = TryMoveItem(body, drop, last, now);
                    _itemNow[body] = now;
                }
            }
            (_itemLast, _itemNow) = (_itemNow, _itemLast);
        }

        Vector3 TryMoveItem(Rigidbody body, ItemDrop drop, Vector3 last, Vector3 now)
        {
            foreach (PortalLink link in Live())
            {
                if (!link.Frame.Crosses(last, now, out _, out _))
                    continue;
                // Moving through, not lying on the plane (at the bottom of an opening) and wobbling across it.
                if (body.linearVelocity.sqrMagnitude < 0.5f * 0.5f || !ItemMayPass(drop, link))
                    return now;
                // Far.Readiness3: only into a far side that takes thrown things (CanThrow; its wall stops it meanwhile).
                if (!Readiness.CanThrow(link))
                {
                    Readiness.LabItemsHeld++;
                    return now;
                }
                Matrix4x4 m = PortalMath.Pair(link.Frame, link.Partner.Frame);
                Quaternion r = PortalMath.Rotation(m);
                Vector3 pos = m.MultiplyPoint3x4(body.position);
                Quaternion rot = r * body.rotation;
                body.position = pos;
                body.rotation = rot;
                body.transform.SetPositionAndRotation(pos, rot);
                body.linearVelocity = r * body.linearVelocity;
                body.angularVelocity = r * body.angularVelocity;
                if (drop.TryGetComponent(out ZNetView nview) && nview.IsValid())
                {
                    nview.GetZDO().SetPosition(pos);
                    Areas.Track(nview.GetZDO());
                    PortalServer.Crossed(nview.GetZDO());
                    CrossedNotify(nview);
                }
                // Out of the exit's opening before the next physics step, whatever that portal's own rules for entering.
                Collider exitWall = link.Partner.Wall;
                if (exitWall != null)
                    foreach (Collider col in body.GetComponentsInChildren<Collider>())
                        if (!col.isTrigger)
                        {
                            Physics.IgnoreCollision(exitWall, col, true);
                            _ignored[(exitWall, col)] = true;
                        }
                FarWorld.OnCrossed(link, false);
                Plugin.Debug($"Item {drop.name} through {link.name} -> {link.Partner.name}");
                return pos;
            }
            return now;
        }

        // ---- projectiles (called from the Projectile.FixedUpdate prefix) ----------------------------------

        internal bool TryMoveProjectile(Transform t, ref Vector3 vel, Vector3 next, out Matrix4x4 m)
        {
            m = Matrix4x4.identity;
            Vector3 from = t.position;
            foreach (PortalLink link in Live())
            {
                if (!link.Frame.Crosses(from, next, out Vector3 hit, out _))
                    continue;
                // Far.Readiness3: a far side that does not take thrown things yet (CanThrow) is not reached: the projectile
                // goes on on this side, as the wall is not there for projectiles.
                if (!Readiness.CanThrow(link))
                {
                    Readiness.LabProjectilesHeld++;
                    continue;
                }
                m = PortalMath.Pair(link.Frame, link.Partner.Frame);
                Quaternion r = PortalMath.Rotation(m);
                vel = r * vel;
                // Start just past the exit plane: the game's hit ray reaches back half a step from the position.
                float step = vel.magnitude * Time.fixedDeltaTime;
                t.SetPositionAndRotation(m.MultiplyPoint3x4(hit) + vel.normalized * Mathf.Min(step * 0.5f, 1f), r * t.rotation);
                if (t.TryGetComponent(out ZNetView nview) && nview.IsValid())
                {
                    nview.GetZDO().SetPosition(t.position);
                    Areas.Track(nview.GetZDO());
                    PortalServer.Crossed(nview.GetZDO());
                    CrossedNotify(nview);
                }
                FarWorld.OnCrossed(link, false);
                Plugin.Debug($"Projectile {t.name} through {link.name} -> {link.Partner.name}");
                return true;
            }
            return false;
        }

        // ---- camera ------------------------------------------------------------------------------------

        // Follows the camera arm player -> offset eye -> camera through any openings it passes.
        // Returns false when the arm crosses no portal (the game's own camera code then runs).
        // Lab: the last camera arm traced (body, eye, end) and the frame.
        internal Vector3 LabArmStart, LabArmEye, LabArmEnd;
        internal int LabArmFrame;

        internal bool TraceCamera(Vector3 start, Vector3 eye, Vector3 end, float radius, int mask,
                                  out Vector3 camPos, out Quaternion turn, out Vector3 armStart)
        {
            LabArmStart = start;
            LabArmEye = eye;
            LabArmEnd = end;
            LabArmFrame = Time.frameCount;
            Matrix4x4 total = Matrix4x4.identity;
            Vector3 p = start;
            PortalLink skip = null;
            bool crossed = false, onArm = false;
            armStart = eye;
            camPos = end;
            turn = Quaternion.identity;
            Vector3 target = eye;
            for (int leg = 0; leg < 2; leg++)
            {
                Vector3 goal = leg == 0 ? eye : end;
                for (int guard = 0; guard < 3; guard++)
                {
                    target = total.MultiplyPoint3x4(goal);
                    PortalLink best = null;
                    float bestT = 2f;
                    Vector3 bestHit = default;
                    foreach (PortalLink link in Live())
                    {
                        // Only through the opening: an arm over the rim was already turned through it (RedirectArm).
                        if (link == skip || !link.Frame.Crosses(p, target, out Vector3 hit, out float t) || t >= bestT)
                            continue;
                        best = link;
                        bestT = t;
                        bestHit = hit;
                    }
                    if (best == null)
                        break;
                    best.HideFrameFromCamera();
                    best.Partner.HideFrameFromCamera();
                    if (onArm && Blocked(p, bestHit, radius, mask, out camPos))
                    {
                        turn = PortalMath.Rotation(total);
                        CameraMatrix = total;
                        if (!crossed)
                            RestoreFrames();
                        return crossed;
                    }
                    crossed = true;
                    Matrix4x4 m = PortalMath.Pair(best.Frame, best.Partner.Frame);
                    total = m * total;
                    p = m.MultiplyPoint3x4(bestHit);
                    skip = best.Partner;
                    if (onArm)
                        armStart = p;
                }
                if (onArm && crossed && Blocked(p, target, radius, mask, out camPos))
                {
                    turn = PortalMath.Rotation(total);
                    CameraMatrix = total;
                    return true;
                }
                if (leg == 0)
                {
                    armStart = target;
                    onArm = true;
                }
                p = target;
                skip = null;
            }
            camPos = target;
            turn = PortalMath.Rotation(total);
            CameraMatrix = total;
            if (!crossed)
                RestoreFrames();
            return crossed;
        }

        // The game's own camera code runs next: the rings are solid for it.
        void RestoreFrames()
        {
            foreach (PortalLink link in Live())
                link.RestoreFrameAfterCamera();
        }

        // Clearance: well inside the rim, so the wood stays out of the picture as the camera passes it.
        const float SteerDistance = 2.5f, SteerClearance = 0.5f;

        // The camera may only pass a portal's plane through the opening. Beyond the plane the camera stands in the
        // other world, and only the window shows the player's side: an arm passing through the ring would put the
        // camera there looking at the player past the window, with the wrong place all around. So an arm that
        // would pass the plane through the ring (or the air just around it) is turned to pass well inside the rim,
        // and on the player's side the camera is eased into the opening as it nears the plane; both meet at the plane.
        readonly List<PortalLink> _steered = new List<PortalLink>();

        // Where a point of a portal's plane would be pulled to (inside the opening by the clearance), when it lies
        // over the ring or just around it (within the ring's reach plus the arm's width).
        static bool PullInside(PortalLink link, Vector2 p, float radius, out Vector2 q)
        {
            PortalFrame f = link.Frame;
            q = p;
            float ax = f.HalfSize.x - SteerClearance, ay = f.HalfSize.y - SteerClearance;
            if (ax <= 0f || ay <= 0f)
                return false;
            float k;
            if (f.Shape != null)
            {
                float rim = f.ShapeRadius(Mathf.Atan2(p.y, p.x)) * f.HalfSize.x - SteerClearance;
                if (rim <= 0f)
                    return false;
                k = p.magnitude / rim;
            }
            else
                k = f.Round ? Mathf.Sqrt(p.x * p.x / (ax * ax) + p.y * p.y / (ay * ay))
                            : Mathf.Max(Mathf.Abs(p.x) / ax, Mathf.Abs(p.y) / ay);
            if (k <= 1f)
                return false;
            q = p / k;
            return (p - q).magnitude < link.FrameMargin + radius + SteerClearance;
        }

        // How far a point inside the opening is from its edge (radially, for the round one).
        static float DistanceToRim(PortalFrame f, Vector2 p)
        {
            if (f.Shape != null)
                return f.ShapeRadius(Mathf.Atan2(p.y, p.x)) * f.HalfSize.x - p.magnitude;
            if (!f.Round)
                return Mathf.Min(f.HalfSize.x - Mathf.Abs(p.x), f.HalfSize.y - Mathf.Abs(p.y));
            float k = Mathf.Sqrt(p.x * p.x / (f.HalfSize.x * f.HalfSize.x) + p.y * p.y / (f.HalfSize.y * f.HalfSize.y));
            return k > 1e-4f ? p.magnitude * (1f / k - 1f) : Mathf.Min(f.HalfSize.x, f.HalfSize.y);
        }

        // The arm (eye -> camera) crossing a portal's plane over its ring: turned about the eye to cross inside it.
        internal Vector3 RedirectArm(Vector3 eye, Vector3 end, float radius)
        {
            foreach (PortalLink link in Live())
            {
                PortalFrame f = link.Frame;
                float de = f.SignedDistance(eye), dn = f.SignedDistance(end);
                if ((de > 0f) == (dn > 0f) || Mathf.Approximately(de, dn))
                    continue;
                Vector3 hit = Vector3.Lerp(eye, end, de / (de - dn));
                Vector3 local = Quaternion.Inverse(f.Rotation) * (hit - f.Center);
                var p = new Vector2(local.x, local.y);
                if (!PullInside(link, p, radius, out Vector2 q))
                    continue;
                // Through the ring: always turned. Through the opening already, only near the rim: there it only
                // matters as the camera itself nears the plane (to meet SteerCamera), and turning it while the
                // crossing is next to the eye would swing the whole arm for a few centimetres.
                if (f.ContainsLocal(p))
                {
                    // Fully at the rim (where the ring starts and it is always turned), none well inside.
                    float toRim = Mathf.Clamp01(1f - DistanceToRim(f, p) / SteerClearance);
                    float near = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Abs(dn) / SteerDistance);
                    float amount = Mathf.Max(near, Mathf.SmoothStep(0f, 1f, toRim));
                    if (amount <= 0f)
                        continue;
                    q = Vector2.Lerp(p, q, amount);
                }
                Vector3 through = f.Center + f.Rotation * new Vector3(q.x, q.y, 0f);
                // Near the plane as deep past it as the camera would have been (there this meets SteerCamera);
                // farther back the arm keeps its length.
                float length = (end - eye).magnitude, toPlane = Mathf.Max((through - eye).magnitude, 0.01f);
                Vector3 keepDepth = eye + (through - eye) * Mathf.Min((de - dn) / de, length / toPlane);
                Vector3 keepLength = eye + (through - eye) * (length / toPlane);
                return Vector3.Lerp(keepDepth, keepLength, Mathf.SmoothStep(0f, 1f, Mathf.Abs(dn) / SteerDistance));
            }
            return end;
        }

        // The camera on the player's side of a portal, near its plane and over its ring: eased into the opening,
        // fully at the plane, where the turned arm (RedirectArm) puts it once it is past.
        internal Vector3 SteerCamera(Vector3 eye, Vector3 end, float radius)
        {
            _steered.Clear();
            foreach (PortalLink link in Live())
            {
                PortalFrame f = link.Frame;
                if ((f.SignedDistance(eye) > 0f) != (f.SignedDistance(end) > 0f))
                    continue;
                Vector3 local = Quaternion.Inverse(f.Rotation) * (end - f.Center);
                float near = 1f - Mathf.Abs(local.z) / SteerDistance;
                if (near <= 0f || !PullInside(link, new Vector2(local.x, local.y), radius, out Vector2 q))
                    continue;
                Vector2 d = (q - new Vector2(local.x, local.y)) * Mathf.SmoothStep(0f, 1f, near);
                end += f.Rotation * new Vector3(d.x, d.y, 0f);
                _steered.Add(link);
            }
            return end;
        }

        // The camera eased toward an opening but its arm crossing no portal: placed here rather than by the game,
        // which would put it back over the rim. The steering ring does not stop it, everything else does.
        internal bool CameraBeside(Vector3 eye, Vector3 end, float radius, int mask, out Vector3 camPos)
        {
            camPos = end;
            if (_steered.Count == 0)
                return false;
            foreach (PortalLink link in _steered)
                link.HideFrameFromCamera();
            Blocked(eye, end, radius, mask, out camPos);
            return true;
        }

        static bool Blocked(Vector3 from, Vector3 to, float radius, int mask, out Vector3 stop)
        {
            Vector3 d = to - from;
            float len = d.magnitude;
            stop = to;
            if (len < 0.01f || !Physics.SphereCast(from, radius, d / len, out RaycastHit hit, len, mask, QueryTriggerInteraction.Ignore))
                return false;
            stop = from + d / len * Mathf.Max(hit.distance - 0.05f, 0f);
            return true;
        }

        internal void Clear()
        {
            FarEnvironment.Restore();
            AreaRenderState.Clear();
            _active.Clear();
            _windows.Clear();
            _reflections.Clear();
            _farParticles.Clear();
            _audio.Clear();
            FarMist.Clear();
            _itemLast.Clear();
            _itemNow.Clear();
            _playerTracked = null;
            _main = null;
            _loggedCamera = false;
            _viewWatch.Clear();
            // FARWORLD V2: the far areas, their readiness and the registry of this world go with it.
            FarWorld.ClearAreas();
            FarWorld.Clear();
            Areas.Clear();
            _sky.Clear();
        }
    }
}
