using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace RealPortal
{
    // The far end of a real portal is kept loaded the way the game keeps the player's own surroundings (zones with
    // their terrain, every object, the distant terrain mesh), so its window shows the real place and walking through
    // needs no loading. Every portal near the player gets an area, the nearest first; everything else costs
    // nothing. Loading and unloading are spread over frames within a time budget. The zones right around the far
    // portal come first: once they stand, the window goes live and the rest streams in behind them.
    // With Far.V2 and Far.Registry the areas take their lists from FarWorld's SectorRegistry and are kept by its sectors
    // (the FarWorld shim at the end); the same objects are made either way.
    internal sealed class RemoteAreas
    {
        sealed class Area
        {
            public ZDOID Portal;
            public Vector3 Center;
            public Vector2s Zone;
            public readonly List<Vector2s> Zones = new List<Vector2s>();
            public readonly List<ZDO> Near = new List<ZDO>();
            public readonly List<ZDO> Distant = new List<ZDO>();
            // Not created yet: terrain changes first, then nearest first (as the game orders them).
            public readonly List<ZDO> Pending = new List<ZDO>();
            public float ListedAt = -10f;
            public float NextPoke;
            // Its objects that move or play effects (particles, creatures, fish, birds), found a few hundred a frame.
            public readonly List<ZNetView> Lively = new List<ZNetView>();
            public readonly List<ZNetView> LivelyNext = new List<ZNetView>();
            public int ScanAt;
            public float ScannedAt = -100f;
            public float WantedAt;
            public float StartedAt;
            public bool Ready;
            // The zones next to the far portal's own (what a window shows up close) and their objects are in.
            public bool CoreReady;
            // When the server said the objects of that place have arrived (multiplayer).
            public float ArrivedAt = -1f;
            public bool Draining;
            public List<ZDO> Keep;
            public FarTerrain Terrain;
            public int Created;
            // Far.DeferInteriors: not-yet-created objects of dungeon interiors left to the game at the last listing.
            public int Deferred;
            // Far.LoadPacer: it spawned a zone since its last listing (what the game generated there is kept only
            // until a listing takes it in).
            public bool ZoneMade;
            // Near objects this game simulates (owns) as it does those around you.
            public readonly HashSet<ZDO> Claimed = new HashSet<ZDO>(ZdoRef.Instance);
            // Far.ClaimLikeGame: its active area (the game's, round the far portal's zone; EnsureActive), the persistent
            // near objects inside it at its last listing (ClaimIn), and how far ClaimStep got through those.
            public ActiveArea Active;
            public readonly List<ZDO> ClaimSet = new List<ZDO>();
            public int ClaimAt;
            // How far its zones reach from the far portal's (zones, Chebyshev), and whether it left the areas (Update's
            // removal, Clear): Far.TrackUntilListed lets go of what came into it then.
            public int ZoneReach;
            public bool Gone;
            // Far.LiveRecheck: its place round the portal stood at some time since it was made (CoreReady or Ready; with
            // Far.SideReady the half its window shows, or both halves).
            public bool Stood;
            // Far.SideReady (Docs\PARITY-PLAN.md P4): the far portal's plane, which splits its place into the half in front of
            // it (+1, along its Forward) and the half behind (-1); the half the nearest window onto it shows (one about to show
            // first) and the half its pending objects and zones were last put in order for (0: not by halves); whether each
            // half stands and when it stood (seconds after StartedAt, -1 not yet); the pending count and listing a failed look
            // through its pending objects saw, per half (looked through again only once either moved); and its zones'
            // heightmaps by place in its square of zones (found once each zone is loaded) for their queued rebuilds.
            public readonly PortalFrame Plane = new PortalFrame();
            public int Shown = 1, SortedHalf;
            public float SortedAt = -10f;
            public bool HalfPlus, HalfMinus;
            // Far.SideReady: the largest reach of its pending objects (ReachOf), added to the zones' half test so an object
            // counted in a half always lies in a zone that loads with that half; without it a big rock or tree by the plane
            // counted in the shown half while its zone was held for the other one, and neither ever stood. And the hold let
            // go when nothing pending was made for StallSeconds while a half did not stand (a backstop; logged).
            public float MaxReach;
            public int StallCount = -1;
            public float StallSince;
            public bool HoldReleased;
            public float HalfPlusAt = -1f, HalfMinusAt = -1f;
            public int CheckedPlus = -1, CheckedMinus = -1;
            public float CheckedPlusListed, CheckedMinusListed;
            public Heightmap[] Maps;
            // Seen this frame (SeeLinks): a window onto it is live, one is about to show (WindowReach.AboutToShow, on screen
            // too), the squared distance of the window that set Shown, and (Far.VisibleNear) its heightmaps rebuild at once.
            public bool LiveLink, About, NearMaps;
            public float SeenAt;
            // Far.FullAreaZones (P38): its zones were listed with the switch on (they are listed again when the reach changes).
            public bool ZonesFull;
            // Far.DupEdge (P15): an edge area (its far portal inside the player's active area: only what the game has not
            // loaded round the player is listed), and the player's zone its lists were cut for.
            public bool Edge;
            public Vector2s EdgeZone;
            // Far.SleepBudget: when it next decides whether to fall asleep (its own phase, once a second), which way it
            // is turning (1 falling asleep, -1 waking, 0 neither), how far falling asleep got through its lively
            // objects, and its lists of what sleeps, kept for the next time.
            public float SightAt;
            public int Turn, TurnAt;
            public List<ParticleSystem> PausedList;
            public List<GameObject> FrozenList;
            // Far.SectorSleep: asleep or falling asleep (its zones are out of the awake set; set from StartSleep until it
            // starts waking), and how far waking got through its zones (frozen objects first, then paused systems).
            public bool Asleep;
            public int WakeAt, WakePhase;
            // FarWorld (Far.V2 with Far.Registry, the shim below): its sectors in the registry (while it does not drain),
            // and the registry's version of them its lists were collected at (-1: not collected from the registry, or not
            // followed by the journal then).
            public AreaSectors Sectors;
            public long ListedVersion = -1L;
            // Far.Proxies: to be listed again (an object proxied before must be made now: FarProxies.RealizeVersion moved).
            public bool ProxyRelist;
        }

        static readonly System.Reflection.FieldInfo s_zones = AccessTools.Field(typeof(ZoneSystem), "m_zones");
        static readonly Func<ZoneSystem, Vector2s, bool> s_pokeZone =
            AccessTools.MethodDelegate<Func<ZoneSystem, Vector2s, bool>>(AccessTools.Method(typeof(ZoneSystem), "PokeLocalZone"));
        static readonly Func<ZNetScene, ZDO, GameObject> s_createObject =
            AccessTools.MethodDelegate<Func<ZNetScene, ZDO, GameObject>>(AccessTools.Method(typeof(ZNetScene), "CreateObject"));

        const float RelistEvery = 0.5f;
        const int DrainPerTick = 400;
        // Far.DrainCalm: the most all draining areas give up together each time the game's removal runs (every sixth
        // tick): each one adds DrainPerTick up to six ticks' worth, so a single one drains as before. Twenty areas
        // giving up 400 each made a tick destroy 8000 objects (50-70 ms of Object.Destroy alone on top of the
        // heightmaps); 100k now go in about 40 calls, 8-9 s.
        const int DrainCalmPerTick = DrainPerTick * 6;
        // Far.SleepBudget: how much wider than the view a window wakes its area (PortalSystem.WakeInSight: 30), and
        // the time a frame for falling asleep and waking.
        const float WakeLead = 45f;
        const double SleepBudgetMs = 1.0;
        // Far.LoadPacer: a loading area not listed for this long is listed first thing in a frame, budget or not:
        // what the game generates in its zones is kept by Track for 3 s only, until a listing takes it in.
        const float RelistOverdue = 1.5f;
        // Far.LoadPacer: a loaded area not listed for this long takes that place in a frame too when no loading one
        // needs it (its listing waits for a frame with budget left, which a long hall load may not leave).
        const float LoadedOverdue = 10f;
        // Far.DeferInteriors: dungeon interiors stand this high above the world (the game places them at about 5000).
        const float InteriorHeight = 3000f;

        readonly List<Area> _areas = new List<Area>();
        readonly Dictionary<ZDOID, Area> _byPortal = new Dictionary<ZDOID, Area>();
        readonly HashSet<ZDO> _bad = new HashSet<ZDO>(ZdoRef.Instance);
        readonly List<(ZDO zdo, float d)> _wanted = new List<(ZDO, float)>();
        readonly List<Area> _order = new List<Area>();
        readonly Stopwatch _clock = new Stopwatch();
        int _selectedFrame = -1;
        float _statNext;
        double _statMs;
        int _statFrames, _statSpawnedZones, _statCreated;
        double _worstMs;
        // Far.LoadPacer: what each kind of loading step usually takes (ms, a moving average), so a step starts only
        // when what is left of the frame's budget covers it; and whether one ran yet this frame (the first may always
        // run, so a step dearer than the whole budget still gets a frame of its own, as before). Objects by prefab,
        // a location's proxy by its location (placing the location is what it costs).
        float _zoneMs, _terrainMs, _relistMs;
        readonly Dictionary<int, float> _prefabMs = new Dictionary<int, float>();
        readonly Dictionary<int, float> _locationMs = new Dictionary<int, float>();
        int _locationProxy;
        bool _stepped;
        int _statHeld;
        // Far.ZoneSpawnSplit: the area that began the zone spawn under way (its parts mark it as having made a zone).
        Area _zoneArea;
        // Far.CoreFirst: loading time spent lately (fading) on areas whose place around the portal is not there yet,
        // and on those filling in behind it.
        double _coreMs, _fillMs;
        // Far.KeepAliveSlice: counts every area made, released, taken back or gone (the sectors kept follow it).
        int _areasVersion;

        internal int Count => _areas.Count;

        // Lab: areas loading or loaded, those whose place right around the portal stands, those wholly loaded, and
        // objects they hold.
        internal void Counts(out int areas, out int core, out int full, out int objects)
        {
            areas = core = full = objects = 0;
            foreach (Area a in _areas)
            {
                if (a.Draining)
                    continue;
                areas++;
                // Far.SideReady: a half standing counts as the place round the portal standing.
                if (a.CoreReady || (FarFlags.SideReady && (a.HalfPlus || a.HalfMinus)))
                    core++;
                if (a.Ready)
                    full++;
                objects += a.Near.Count + a.Distant.Count - a.Pending.Count - a.Deferred;
            }
        }

        // A window to this far portal may go live: its area has everything loaded.
        internal bool IsReady(ZDO farPortal) => farPortal != null && _byPortal.TryGetValue(farPortal.m_uid, out Area a) && Stands(a) && !a.Draining;

        // The place round the far portal stands (its core, or all of it), or with Far.SideReady a half of it: its objects are
        // claimed and looked through for what sleeps from then on.
        static bool Stands(Area a) => a.Ready || a.CoreReady || (FarFlags.SideReady && (a.HalfPlus || a.HalfMinus));

        // Far.SideReady: that half of the place (+1 in front of the far portal, -1 behind) stands.
        static bool HalfStands(Area a, int half) => half >= 0 ? a.HalfPlus : a.HalfMinus;

        // The part a window onto the area waits for: with Far.SideReady the half it shows (its nearest window's), else the
        // place round the far portal.
        static bool FrontStands(Area a) => FarFlags.SideReady ? HalfStands(a, a.Shown) : a.CoreReady;

        // P4.1: the half of its far side this link's window shows to the game camera now (ShownFrom); 0 when it is not seen.
        static int ShownOf(PortalLink link)
        {
            Camera cam = Utils.GetMainCamera();
            return cam != null && link.FrameSet ? ShownFrom(link, EyeOf(link, cam.transform.position)) : 1;
        }

        // The eye a link's window is seen from: the game camera; while the camera is carried through a portal (it stands at
        // that portal's far side, PortalSystem.CameraThroughRecently) the player's own eye (where the arm starts) for a link
        // nearer that than the camera: the one the arm crossed, whose side the camera's place says nothing about.
        static Vector3 EyeOf(PortalLink link, Vector3 cam)
        {
            PortalSystem sys = PortalSystem.Instance;
            Player p = Player.m_localPlayer;
            if (sys == null || !sys.CameraThroughRecently || p == null || p.m_eye == null)
                return cam;
            Vector3 pe = p.m_eye.position, c = link.Frame.Center;
            return (c - pe).sqrMagnitude < (c - cam).sqrMagnitude ? pe : cam;
        }

        // The half of its far side a link's window shows to that eye (WindowReach.ShownHalf), or 0 past the render reach of
        // it (not seen: after a crossing the window behind is a kilometre from the camera, where its side is arbitrary).
        static int ShownFrom(PortalLink link, Vector3 eye)
        {
            PortalFrame f = link.Frame;
            float reach = WindowReach.Render + Mathf.Max(f.HalfSize.x, f.HalfSize.y);
            return (f.Center - eye).sqrMagnitude > reach * reach ? 0 : WindowReach.ShownHalf(link, eye);
        }

        // Far.SideReady: the half a window shows stands; one not seen waits for either half.
        static bool ShownStands(Area a, int shown) => shown == 0 ? a.HalfPlus || a.HalfMinus : HalfStands(a, shown);

        // A link may go live when the place right around its far end is loaded. A pair without an area (the far end
        // lies in the player's own area) goes live at once.
        internal bool CanGoLive(PortalLink link) => MayShow(link, true);

        // Far.LiveRecheck (P27): a live link may stay live, by the same test every frame, without the arrival's pacing of
        // go-lives (that holds windows back from going live, not live ones from staying so).
        internal bool StaysLive(PortalLink link) => MayShow(link, false);

        // Far.LiveRecheck: a link onto this far portal is checked every frame (StaysLive, CanGoLive): its area drains, or
        // stood since it was made (one taken back included), or is new and the place round the portal is not all there
        // (the game's removal took part of it before the area listed it: a crossing's race). A new area round a place the
        // game has loaded whole (its far end was in the player's own area, or he just walked through from there) leaves a
        // link live from before it as it is until it stands, as one without an area: what the window shows is there, with
        // the game's own distant terrain in place of the area's until that is built.
        internal bool Rechecks(ZDO farPortal)
        {
            if (farPortal == null || !_byPortal.TryGetValue(farPortal.m_uid, out Area a))
                return false;
            return a.Draining || a.Stood || !CoreThere(a);
        }

        // The zones round the far portal are loaded and none of their listed objects waits to be made.
        bool CoreThere(Area a)
        {
            ZoneSystem zs = ZoneSystem.instance;
            if (zs == null)
                return false;
            foreach (Vector2s z in a.Zones)
                if (InCore(a, z) && !zs.IsZoneLoaded(z))
                    return false;
            // Pending is sorted core first: the first one left is a core one while any is.
            foreach (ZDO z in a.Pending)
                if (z != null)
                    return !InCore(a, z.GetSector());
            return true;
        }

        bool MayShow(PortalLink link, bool goingLive)
        {
            ZDO far = link.PartnerZdo;
            if (far == null || ZNet.instance == null)
                return false;
            bool mine = ZNetScene.InActiveArea(far.GetPosition(), ZNet.instance.GetReferencePosition());
            // Far.NoDupAreas: a far end inside the player's own area is the game's to load; it gets no area (Select),
            // and one it still had is on its way out. Far.DupEdge (P15): it gets an edge area (what the game has not loaded
            // round the player), which the link waits for as for any other.
            bool edge = FarFlags.NoDupAreas && FarFlags.DupEdge && mine;
            if (FarFlags.NoDupAreas && mine && !edge)
                return true;
            // Far.ArrivalPacer: while arriving, one such window a frame (GoLive).
            if (_byPortal.TryGetValue(far.m_uid, out Area a) && !a.Draining)
            {
                // Far.SideReady (P4.2): the half of the far side this window shows stands (it flips as the camera crosses the
                // entry's plane); that waits for the server's core objects in multiplayer (P26) too. One not seen (past the
                // render reach) waits for either half.
                if (FarFlags.SideReady)
                    return ShownStands(a, ShownOf(link)) && (!goingLive || GoLive());
                return (a.Ready || a.CoreReady) && (!goingLive || GoLive());
            }
            // Far.DupEdge: an edge area on its way (wanted) is waited for; a far end left without one (MaxFarAreas) is live
            // at once, as without the switch.
            if (edge)
                return !Wanted(far);
            return mine;
        }

        bool Wanted(ZDO far)
        {
            for (int i = 0; i < _wanted.Count; i++)
                if (ReferenceEquals(_wanted[i].zdo, far))
                    return true;
            return false;
        }

        internal bool Has(ZDO farPortal) => farPortal != null && _byPortal.TryGetValue(farPortal.m_uid, out Area a) && !a.Draining;

        // How far the area around this far portal has loaded: 0 none (or going), 1 loading, 2 the place around the
        // portal stands (Far.SideReady: a half of it stands), 3 all of it.
        internal int Stage(ZDO farPortal)
        {
            if (farPortal == null || !_byPortal.TryGetValue(farPortal.m_uid, out Area a) || a.Draining)
                return 0;
            if (FarFlags.SideReady)
                return a.Ready ? 3 : a.HalfPlus || a.HalfMinus ? 2 : 1;
            return a.Ready ? 3 : a.CoreReady ? 2 : 1;
        }

        // Lab and log: the state of the area around this far portal.
        internal string Describe(ZDO farPortal)
        {
            if (farPortal == null || !_byPortal.TryGetValue(farPortal.m_uid, out Area a))
                return "no area";
            return $"{(a.Draining ? "draining" : a.Ready ? "ready" : "loading")}, zones {LoadedZones(a)}/{a.Zones.Count}, objects {a.Near.Count + a.Distant.Count - a.Pending.Count - a.Deferred}/{a.Near.Count + a.Distant.Count}, " +
                   $"{Time.time - a.StartedAt:0.0} s" + (_shimReg != null && OnRegistry(a, _shimReg) ? ", on the registry" : "") +
                   (FarFlags.SideReady ? $", halves: front {(a.HalfPlus ? a.HalfPlusAt.ToString("0.0") + " s" : "not yet")}, behind {(a.HalfMinus ? a.HalfMinusAt.ToString("0.0") + " s" : "not yet")} (shown {(a.Shown >= 0 ? "front" : "behind")})" : "") +
                   (a.Edge ? ", edge area" : "");
        }

        // Which far portals get an area this frame: those of the portals near the player, nearest first. Before the
        // player is spawned (the loading screen after joining a world): around the spawn point the game loads.
        void Select()
        {
            if (_selectedFrame == Time.frameCount)
                return;
            _selectedFrame = Time.frameCount;
            // Far.ArrivalPacer: a jump of the reference position is seen here, first thing in a frame (the game's removal
            // pass may ask before Update does), and whether the arrival hold applies this frame.
            bool jumped = Arrive();
            long began = jumped ? Stopwatch.GetTimestamp() : 0L;
            Player p = Player.m_localPlayer;
            _wanted.Clear();
            if (Plugin.Enabled.Value && ZNet.instance != null)
            {
                Vector3 at = p != null ? p.transform.position : ZNet.instance.GetReferencePosition();
                float preload = Plugin.PreloadDistance.Value;
                foreach (PortalLink link in PortalLink.All)
                {
                    ZDO far = link.IsReal ? link.PartnerZdo : null;
                    if (far == null || !far.IsValid())
                        continue;
                    float d = Utils.DistanceXZ(at, link.transform.position);
                    if (d > preload + (Has(far) ? Plugin.UnloadMargin.Value : 0f))
                        continue;
                    // Far.NoDupAreas: a far end inside the player's own area is loaded by the game already; an area
                    // around it listed, kept and relisted the same objects a second time (6.2k for the lab's pair).
                    // Its link goes live at once (CanGoLive). Far.DupEdge (P15): it gets an edge area instead, which lists
                    // only what the game has not loaded round the player (below).
                    if (FarFlags.NoDupAreas && !FarFlags.DupEdge && ZNetScene.InActiveArea(far.GetPosition(), ZNet.instance.GetReferencePosition()))
                        continue;
                    bool dup = false;
                    for (int i = 0; i < _wanted.Count && !dup; i++)
                        dup = _wanted[i].zdo == far;
                    if (!dup)
                        _wanted.Add((far, d));
                }
                _wanted.Sort((x, y) => x.d.CompareTo(y.d));
                int max = Plugin.MaxFarAreas.Value;
                if (max > 0 && _wanted.Count > max)
                    _wanted.RemoveRange(max, _wanted.Count - max);
            }
            float now = Time.time;
            bool crossed = _crossed && FarFlags.CrossAtOnce;
            // Far.DupEdge: the player's zone, and what the game's lists round him let go since the edge areas were cut for
            // the zone before (MoveMine).
            Vector2s mineZone = ZNet.instance != null ? ZoneSystem.GetZone(ZNet.instance.GetReferencePosition()) : default;
            bool mineMoved = MoveMine(mineZone);
            foreach ((ZDO far, float _) in _wanted)
            {
                bool edge = EdgeOf(far);
                if (!_byPortal.TryGetValue(far.m_uid, out Area a))
                {
                    // Far.LoadPacer: made in Update instead, one a frame inside the loading budget (CreateArea).
                    // Far.ArrivalPacer: so while arriving too, pacer or not; but in the frame of a jump the nearest one is
                    // made here at once (AtOnce). Far.CrossAtOnce (P5): in the frame of any crossing (the reference position
                    // moved over CrossDistance), whatever ArrivalPacer says (CrossNearest).
                    if ((FarFlags.LoadPacer || _arriving) && !(FarFlags.CrossAtOnce ? crossed && CrossNearest(far, jumped) : jumped && AtOnce(far)))
                        continue;
                    a = MakeArea(far, now, edge);
                }
                else if (a.Draining)
                {
                    // Far.KeepAliveFast: its lists count again in place of what was left of them to drain.
                    if (_fast)
                        FastRecount(a.Keep ?? s_noZdos, a.Distant, a.Near);
                    a.Draining = false;
                    a.StartedAt = now;
                    a.Keep = null;
                    a.ListedAt = -10f;
                    _areasVersion++;
                    // FarWorld (shim): its sectors back in the registry.
                    Acquire(a);
                }
                // Far.DupEdge (P15): an area whose far portal came into the player's active area or left it is listed again
                // now (an edge area, or a whole one again), and an edge area whenever the player's zone changed: before the
                // game's removal pass goes by the player's new surroundings. One cut for the zone he just left takes in only
                // what his lists let go (EdgeTakeIn), its listing left to the pacer.
                if (a.Edge != edge || (edge && a.EdgeZone != mineZone))
                {
                    if (mineMoved && edge && a.Edge && a.EdgeZone == _mineFrom)
                        EdgeTakeIn(a, now);
                    else
                        Reedge(a, edge);
                }
                a.WantedAt = now;
                if (a.Terrain == null)
                    a.Terrain = TakeSpare(a.Center) ?? new FarTerrain(a.Center);
            }
            int edges = 0;
            foreach (Area a in _areas)
            {
                if (!a.Draining && now - a.WantedAt > Plugin.UnloadDelay.Value)
                    StartDraining(a);
                if (!a.Draining && a.Edge)
                    edges++;
            }
            LabEdgeAreas = edges;
            // Far.CrossAtOnce (lab): the place just left has its area by now (the nearest wanted far portal), and P22: the
            // arriving place's distant terrain stands in for the game's own until that is rebuilt round the player.
            if (crossed)
            {
                LabCrossings++;
                if (_wanted.Count > 0 && Has(_wanted[0].zdo))
                    LabCrossAtOnce++;
                HandOffArrival();
            }
            // Far.ArrivalPacer (lab): what this took in the frame of a jump (ArrivalFrameDone).
            if (jumped)
            {
                _jumpSelectBegan = began;
                _jumpSelectTicks = Stopwatch.GetTimestamp() - began;
            }
        }

        // Far.DupEdge: this far portal gets an edge area (Far.NoDupAreas would give it none: it is inside the player's own
        // active area).
        static bool EdgeOf(ZDO far) =>
            FarFlags.NoDupAreas && FarFlags.DupEdge && ZNet.instance != null && ZNetScene.InActiveArea(far.GetPosition(), ZNet.instance.GetReferencePosition());

        // Far.DupEdge: the area becomes an edge area, or a whole one again, or follows the player's zone: listed now.
        void Reedge(Area a, bool edge)
        {
            bool was = a.Edge;
            a.Edge = edge;
            Relist(a);
            if (was != edge)
                Plugin.Debug($"Remote area for portal {a.Portal}: {(edge ? "an edge area (its far portal is inside the player's active area)" : "a whole area again")}, " +
                             $"{a.Near.Count} near and {a.Distant.Count} distant objects");
        }

        // A new area around this far portal, with its zones and objects listed (Far.DupEdge: an edge area's, without what
        // the game has loaded round the player).
        Area MakeArea(ZDO far, float now, bool edge = false)
        {
            var a = new Area { Portal = far.m_uid, StartedAt = now, Edge = edge };
            a.Center = far.GetPosition();
            a.Zone = ZoneSystem.GetZone(a.Center);
            // Far.SideReady: its halves by the far portal's plane (as measured once its portal is loaded, SeeLinks), and the
            // half its window shows now, which its pending objects are put in order for.
            a.Plane.Center = a.Center;
            a.Plane.Rotation = Quaternion.Euler(0f, far.GetRotation().eulerAngles.y, 0f);
            if (FarFlags.SideReady)
                SeeLinks(a);
            var sw = Stopwatch.StartNew();
            ListZones(a);
            _areas.Add(a);
            _byPortal[a.Portal] = a;
            _areasVersion++;
            // FarWorld (shim): its sectors registered, so its listing comes from the registry.
            Acquire(a);
            Relist(a);
            NoteSlow("new area", sw.Elapsed.TotalMilliseconds);
            Plugin.Debug($"Remote area for portal {far.m_uid} at {a.Center:F0}: {a.Zones.Count} zones, {a.Near.Count} near and {a.Distant.Count} distant objects");
            return a;
        }

        // Far.LoadPacer: the nearest wanted far portal without an area gets one, at most one a frame and inside the
        // frame's loading time (listing its zones and objects is a step like the others): twenty portals coming into
        // reach together made twenty areas in one frame (Select, 34 ms).
        void CreateArea(float now)
        {
            // Far.ArrivalPacer: the one made at once in this frame's Select (a jump) is this frame's.
            if (_atOnceFrame == Time.frameCount)
            {
                _stepped = true;
                return;
            }
            foreach ((ZDO far, float _) in _wanted)
            {
                if (_byPortal.ContainsKey(far.m_uid))
                    continue;
                Area a = MakeArea(far, now, EdgeOf(far));
                a.WantedAt = now;
                // Far.CrossAtOnce (P5.2): the spare distant terrain built round the portal the player stood by, if it is this one.
                a.Terrain = TakeSpare(a.Center) ?? new FarTerrain(a.Center);
                _stepped = true;
                return;
            }
        }

        void StartDraining(Area a)
        {
            a.Draining = true;
            a.Ready = false;
            a.CoreReady = false;
            // Far.SideReady: taken back, its halves stand again only once they are there again (P4, §5.10).
            a.HalfPlus = a.HalfMinus = false;
            a.HalfPlusAt = a.HalfMinusAt = -1f;
            a.CheckedPlus = a.CheckedMinus = -1;
            a.SortedHalf = 0;
            a.NearMaps = false;
            _areasVersion++;
            // What slept wakes as it goes (the game unloads them as it does what you leave). Far.DrainCalm: it goes
            // asleep; waking thousands of objects just to destroy them was part of the drain's ticks. What another
            // area or the player keeps wakes when the area is gone (Update), or as the player arrives (UpdateSight).
            if (!FarFlags.DrainCalm)
                Wake(a);
            // Far.KeepAliveFast: the same objects as its lists, so their counts stand.
            a.Keep = new List<ZDO>(a.Near.Count + a.Distant.Count);
            a.Keep.AddRange(a.Distant);
            a.Keep.AddRange(a.Near);
            // Left as the game leaves what you walk away from: not simulated (unless you are there yourself).
            // Far.ClaimLikeGame: kept while it drains and given up as it leaves the areas (Update), so one taken back still
            // owns its objects.
            if (!FarFlags.ClaimLikeGame)
            {
                foreach (ZDO z in a.Claimed)
                    _release.Add(z);
                a.Claimed.Clear();
            }
            // Far.CrossAtOnce (P22): the distant terrain the game camera shows after a crossing until the game's own stands
            // round the player stays that long (FarTerrain destroys it then).
            if (!FarTerrain.Orphan(a.Terrain))
                a.Terrain?.Destroy();
            a.Terrain = null;
            // FarWorld (shim): its sectors leave the registry (what another area keeps of them stays there); Keep says what
            // goes when, as without it.
            ReleaseSectors(a);
            Plugin.Debug($"Remote area for portal {a.Portal} released, unloading {a.Keep.Count} objects");
        }

        // How far a far area reaches: the game's simulation distance, or fewer zones of everything (FarAreaZones)
        // with the ring of big distant objects (trees, buildings) as wide as the game's. Far.FullAreaZones (Docs\PARITY-PLAN.md
        // P38): always the game's (at simulation distance 4-5 FarAreaZones = 3 left small objects 3-5 zones out missing).
        internal static SimulationDistance AreaDistance()
        {
            SimulationDistance sim = ZNet.instance.GetSyncedSimulationDistance();
            int want = Plugin.FarAreaZones.Value;
            if (FarFlags.FullAreaZones || want <= 0 || want >= sim.NearSimulationDistance || sim.IsClassic)
                return sim;
            return new SimulationDistance(want, sim.TotalSimulationDistance - want);
        }

        // The zones the game would load around a player standing at the far portal.
        void ListZones(Area a)
        {
            SimulationDistance sim = AreaDistance();
            int r = sim.NearSimulationDistance;
            a.ZoneReach = r;
            a.ZonesFull = FarFlags.FullAreaZones;
            ZoneSystem zs = ZoneSystem.instance;
            for (int y = a.Zone.y - r; y <= a.Zone.y + r; y++)
                for (int x = a.Zone.x - r; x <= a.Zone.x + r; x++)
                {
                    var z = new Vector2s(x, y);
                    if (sim.IsClassic || zs.ZonesWithinRadius(a.Zone, z, r))
                        a.Zones.Add(z);
                }
            Vector3 c = ZoneSystem.GetZonePos(a.Zone);
            a.Zones.Sort((p, q) => (ZoneSystem.GetZonePos(p) - c).sqrMagnitude.CompareTo((ZoneSystem.GetZonePos(q) - c).sqrMagnitude));
        }

        static bool InCore(Area a, Vector2s zone) => Math.Abs(zone.x - a.Zone.x) <= 1 && Math.Abs(zone.y - a.Zone.y) <= 1;

        // The objects the game would show around a player there (near ones and the distant ring), and which of them
        // are still to be created. Ours stop being simulated once they are outside the player's own area, as when
        // the game unloads them.
        void Relist(Area a)
        {
            a.ListedAt = Time.time;
            // Far.FullAreaZones (P38), switched while the area stands: its zones are listed again to the reach its objects
            // are listed with now (else they would wait for zones nothing loads, or leave zones loaded with nothing listed).
            if ((FarFlags.FullAreaZones || a.ZonesFull) && a.ZoneReach != AreaDistance().NearSimulationDistance)
            {
                a.Zones.Clear();
                a.Maps = null;
                ListZones(a);
                a.SortedHalf = 0;
                _areasVersion++;
            }
            // FarWorld (shim): the lists come from the registry's rows (the same ZDOs as ZDOMan.FindSectorObjects; a sector
            // the journal does not follow yet is read from the game's tables), and stand as they are while the journal
            // follows every sector of the area and none of them changed since they were collected (only what comes after
            // them below runs then).
            SectorRegistry reg = ShimOf(a);
            long version = reg != null ? ShimVersion(reg, a) : -1L;
            if (reg != null && version >= 0 && version == a.ListedVersion)
                LabShimStood++;
            else
            {
                // Far.KeepAliveFast: what the lists held, so the counts follow only what changed (made again from scratch
                // should the listing throw part way).
                bool recount = _fast && !a.Draining, rebuild = _fastRebuild;
                if (recount)
                {
                    CopyLists(a.Near, a.Distant, _fastOld);
                    _fastRebuild = true;
                }
                a.Near.Clear();
                a.Distant.Clear();
                if (reg == null || !ShimCollect(reg, a))
                {
                    ZDOMan.instance.FindSectorObjects(a.Zone, AreaDistance(), a.Near, a.Distant);
                    version = -1L;
                }
                // Far.DupEdge (P15): an edge area leaves out what the game has loaded round the player (collected afresh at
                // every listing: what it leaves out moves with him).
                if (a.Edge)
                {
                    LeaveOutMine(a);
                    version = -1L;
                }
                a.ListedVersion = version;
                if (recount)
                {
                    _fastRebuild = rebuild;
                    FastRecount(_fastOld, a.Near, a.Distant);
                }
            }
            a.Pending.Clear();
            a.Deferred = 0;
            Vector3 player = ZNet.instance.GetReferencePosition();
            long me = ZDOMan.GetSessionID();
            if (FarFlags.ClaimLikeGame)
                ClaimListed(a, me);
            else
                Claim(a, me);
            AddPending(a, a.Near, player, me);
            AddPending(a, a.Distant, player, me);
            OrderPending(a);
        }

        // The area's pending objects in loading order.
        void OrderPending(Area a)
        {
            // Far.SideReady (P4.3): the core zones' objects, then the half the window shows, then the other half's near
            // zones, then its ring (each by type, then nearest first); its zones the same way.
            if (FarFlags.SideReady)
            {
                OrderForHalf(a);
                return;
            }
            a.SortedHalf = 0;
            // The core zones' objects first (by type, then nearest first, as the game orders them), then the rest.
            Vector3 c = a.Center;
            foreach (ZDO z in a.Pending)
                z.m_tempSortValue = Utils.DistanceSqr(c, z.GetPosition()) + (InCore(a, z.GetSector()) ? 0f : 1e12f);
            a.Pending.Sort((x, y) =>
            {
                bool cx = x.m_tempSortValue < 1e12f, cy = y.m_tempSortValue < 1e12f;
                if (cx != cy)
                    return cx ? -1 : 1;
                return x.Type == y.Type ? x.m_tempSortValue.CompareTo(y.m_tempSortValue) : ((int)y.Type).CompareTo((int)x.Type);
            });
        }

        void AddPending(Area a, List<ZDO> list, Vector3 player, long me)
        {
            // Far.DeferInteriors: dungeon interiors (far above the ground) are not made for a far area on the ground,
            // where no window sees them (DungeonGenerator.Spawn alone took 70-90 ms); the game makes them once the
            // player is there, as it always does. Nor waited for: left pending, the area would never be ready.
            bool deferInteriors = FarFlags.DeferInteriors && a.Center.y < InteriorHeight;
            // Far.Proxies: an object drawn as a proxy is not made (one whose proxy is on its way waits in Pending: CreateSome).
            bool proxies = FarProxies.Proxying;
            foreach (ZDO z in list)
            {
                if (!z.Created && !_bad.Contains(z) && !(proxies && FarProxies.StateOf(z) == ProxyState.Proxy))
                {
                    if (deferInteriors && z.GetPosition().y > InteriorHeight)
                        a.Deferred++;
                    else
                    {
                        a.Pending.Add(z);
                        if (FarFlags.SideReady)
                        {
                            float reach = ReachOf(z);
                            if (reach > a.MaxReach)
                                a.MaxReach = reach;
                        }
                    }
                }
                // Cheapest tests first: most are this area's own, claimed just before.
                if (z.Persistent && z.GetOwner() == me && !a.Claimed.Contains(z) && !ZNetScene.InActiveArea(z.GetPosition(), player) && !IsClaimed(z))
                    _release.Add(z);
            }
        }

        // A loaded far end is a place this game runs, like your own surroundings: its creatures move, hunt and
        // sleep, fires burn, smelters work. It takes the objects of the near zones nobody else runs, as the game
        // does for a player standing there (ZDOMan.ReleaseNearbyZDOS), a few hundred at a time.
        void Claim(Area a, long me)
        {
            if (!Stands(a) || a.Draining)
                return;
            if (!Plugin.LiveFarSide.Value)
            {
                // Switched off: the far end stands still again.
                foreach (ZDO z in a.Claimed)
                    _release.Add(z);
                a.Claimed.Clear();
                return;
            }
            int took = 0;
            foreach (ZDO z in a.Near)
            {
                if (!z.Persistent)
                    continue;
                if (z.GetOwner() == me)
                {
                    a.Claimed.Add(z);
                    _release.Remove(z);
                    continue;
                }
                if (z.HasOwner() || took >= 500)
                    continue;
                z.SetOwner(me);
                a.Claimed.Add(z);
                took++;
            }
        }

        bool IsClaimed(ZDO z)
        {
            // Far.ClaimLikeGame: by where it stands, as the game decides: inside the active area of any area (one that
            // drains keeps its claims until it is gone).
            if (FarFlags.ClaimLikeGame)
                return Plugin.LiveFarSide.Value && InAnyActiveArea(z.GetPosition(), true);
            foreach (Area a in _areas)
                if (!a.Draining && a.Claimed.Contains(z))
                    return true;
            return false;
        }

        readonly HashSet<ZDO> _release = new HashSet<ZDO>(ZdoRef.Instance);
        readonly List<ZDO> _releasing = new List<ZDO>();

        // Ownership of objects left outside the player's area is given up a few hundred a frame: right after a
        // walk through, the whole place behind holds thousands of them.
        void ReleaseSome(double budget)
        {
            if (_release.Count == 0)
                return;
            Vector3 player = ZNet.instance.GetReferencePosition();
            long me = ZDOMan.GetSessionID();
            // Far.ClaimLikeGame: not what an area's active area holds (P3.5).
            bool claimLike = FarFlags.ClaimLikeGame;
            _releasing.Clear();
            foreach (ZDO z in _release)
            {
                _releasing.Add(z);
                if (_releasing.Count >= 300)
                    break;
            }
            foreach (ZDO z in _releasing)
            {
                _release.Remove(z);
                if (z.IsValid() && z.GetOwner() == me && !ZNetScene.InActiveArea(z.GetPosition(), player) && !(claimLike && IsClaimed(z)))
                    z.SetOwner(0L);
                if (_clock.Elapsed.TotalMilliseconds >= budget)
                    break;
            }
        }

        // ---- Far.ClaimLikeGame (Docs\PARITY-PLAN.md P3) ---------------------------------------------------------------
        // Claim above takes the whole near square (5x5 zones in classic), 500 a listing and only once the window is live,
        // while a player standing there owns his active area (3x3): the window showed more life than the place, creatures
        // came in a listing (5-10 s) at a time after go-live, and the game's 2 s release undid claims round the player
        // (pairs 100-300 m apart: creatures blinked and stopped). With the switch:
        //  - The claim set is the game's rule for a player standing at the far portal: the persistent near objects inside
        //    ZNetScene.InActiveArea round its zone (ActiveArea). At a listing the claims become that (ClaimListed): what
        //    left it is given up (ReleaseSome, unless another area's active area or the player's holds it), what is ours in
        //    it joins, what nobody owns there is taken.
        //  - Before the place round the portal counts as standing (CoreReady, so before its window goes live), everything
        //    in its active area nobody owns is taken (ClaimCore), characters first.
        //  - Every frame (ClaimStep), what nobody owns in a standing area's active area is taken: from a slice of what its
        //    last listing found inside it (ClaimChecksPerFrame for all areas together: a single pair's whole set a frame;
        //    what walked in since is owned already, as only what is owned moves) and from what was just made in it or
        //    carried into it (Track). No cap in single player; in multiplayer (a client, or a host with peers) at most
        //    ClaimCapMp a frame, characters first, the rest waiting for the next frames.
        //  - What another player owns is never taken: a real player standing there owns it as in the game.
        //  - An area that drains keeps its claims until it leaves the areas (taken back, it still owns them). What any
        //    area's active area holds is not given up here (IsClaimed), nor by the game's 2 s pass for the local session
        //    (FarOwnership, for areas that do not drain).
        // Off: Claim above, exactly as before. Main thread only; nothing allocated per frame once the lists grew.

        // In multiplayer the most objects taken a frame; the claim-set entries looked at a frame, all areas together (some
        // 2k an area in classic; looking at one reads two flags and, when nobody owns it, its position).
        const int ClaimCapMp = 200;
        const int ClaimChecksPerFrame = 4096;

        struct ClaimEntry
        {
            internal ZDO Zdo;
            internal Area Area;
        }

        // Waiting to be taken (each object once), what Track saw made or carried in since the last step, the drop list of
        // a listing, the frame and AreasVersion the areas' active areas are of, the area ClaimStep goes on with, the frame
        // of the takes left, and which prefabs are characters.
        readonly List<ClaimEntry> _claimWait = new List<ClaimEntry>();
        readonly HashSet<ZDO> _claimQueued = new HashSet<ZDO>(ZdoRef.Instance);
        readonly List<(ZDO zdo, ZDOID id)> _claimNew = new List<(ZDO, ZDOID)>();
        readonly List<ZDO> _claimDrop = new List<ZDO>();
        int _activeFrame = -1, _activeVersion = -1, _claimArea, _claimFrame = -1, _claimLeft;
        ActiveArea _mineActive;
        static readonly Dictionary<int, bool> s_characterPrefab = new Dictionary<int, bool>();

        // Lab: what the areas that do not drain hold claimed now (a sum over them), what was found nobody's in an active
        // area and waits to be taken (multiplayer's cap), and objects taken in all.
        internal static int LabClaimed, LabClaimPending;
        internal static long LabClaimTook;

        // Docs\PARITY-PLAN.md §4 A5 (FarOwnership; for C and D): the point lies in the active area (ZNetScene.InActiveArea
        // round its far portal's zone, the game's rule for a player standing there) of a far area that does not drain,
        // loading or standing.
        internal bool InFarActiveArea(Vector3 at) => ZNet.instance != null && ZoneSystem.instance != null && InAnyActiveArea(at, false);

        // A far area that does not drain exists (FarOwnership: without one the game's pass runs untouched).
        internal bool HasFarActiveArea
        {
            get
            {
                foreach (Area a in _areas)
                    if (!a.Draining)
                        return true;
                return false;
            }
        }

        // Inside the active area of an area (of one that drains too, if asked).
        bool InAnyActiveArea(Vector3 at, bool draining)
        {
            if (_areas.Count == 0)
                return false;
            EnsureActive();
            foreach (Area a in _areas)
                if ((draining || !a.Draining) && Claims(a, at))
                    return true;
            return false;
        }

        // The areas' active areas, made again when the frame or the areas changed (the simulation distance is read then).
        void EnsureActive()
        {
            int frame = Time.frameCount;
            if (frame == _activeFrame && _activeVersion == _areasVersion)
                return;
            ZNet net = ZNet.instance;
            ZoneSystem zs = ZoneSystem.instance;
            if (net == null || zs == null)
                return;
            _activeFrame = frame;
            _activeVersion = _areasVersion;
            SimulationDistance sim = net.GetSyncedSimulationDistance();
            float size = zs.m_zoneSize;
            foreach (Area a in _areas)
                a.Active = ActiveArea.Of(a.Zone, sim, size);
            // Far.DupEdge: the player's own, which edge areas leave out (Claims).
            _mineActive = ActiveArea.Of(ZoneSystem.GetZone(net.GetReferencePosition()), sim, size);
        }

        static bool Claiming(Area a) => !a.Draining && Stands(a);

        // The area's active area holds the point; an edge area's (Far.DupEdge) less the player's own, which the game's pass
        // owns for him (P15). EnsureActive first.
        bool Claims(Area a, Vector3 at) => a.Active.Holds(at) && !(a.Edge && _mineActive.Holds(at));

        // At a listing, in place of Claim: the area's claims are what its active area holds now. What left it (walked out,
        // gone, another player's now) is given up, what is ours in it joins, and what nobody owns there waits to be taken.
        void ClaimListed(Area a, long me)
        {
            if (!Claiming(a))
                return;
            if (!Plugin.LiveFarSide.Value)
            {
                // Switched off: the far end stands still again.
                foreach (ZDO z in a.Claimed)
                    _release.Add(z);
                a.Claimed.Clear();
                a.ClaimSet.Clear();
                return;
            }
            EnsureActive();
            _claimDrop.Clear();
            foreach (ZDO z in a.Claimed)
                if (!z.IsValid() || z.GetOwner() != me || !Claims(a, z.GetPosition()))
                    _claimDrop.Add(z);
            foreach (ZDO z in _claimDrop)
            {
                a.Claimed.Remove(z);
                if (z.IsValid() && z.GetOwner() == me)
                    _release.Add(z);
            }
            _claimDrop.Clear();
            ClaimIn(a);
        }

        // The area's near list against its active area (Claims): what is inside is its claim set (ClaimStep looks through
        // it), ours there joins its claims, nobody's waits to be taken.
        void ClaimIn(Area a)
        {
            List<ZDO> near = a.Near;
            a.ClaimSet.Clear();
            a.ClaimAt = 0;
            for (int i = 0; i < near.Count; i++)
            {
                ZDO z = near[i];
                if (z == null || !z.Persistent || !Claims(a, z.GetPosition()))
                    continue;
                a.ClaimSet.Add(z);
                if (z.IsOwner())
                {
                    a.Claimed.Add(z);
                    _release.Remove(z);
                }
                else if (!z.HasOwner())
                    QueueClaim(z, a);
            }
        }

        // P3.2, right before the place round the far portal counts as standing (CoreReady, so before its window goes live):
        // all of its active area nobody owns is taken, characters first. True once nothing waits (in multiplayer the
        // frame's cap may leave some for the next frames, and it stands only then). LiveFarSide off: nothing to take.
        bool ClaimCore(Area a)
        {
            if (!Plugin.LiveFarSide.Value)
                return true;
            EnsureActive();
            ClaimIn(a);
            TakeClaims();
            return _claimWait.Count == 0;
        }

        // Every frame (Update): what was just made in an area or carried into one, then a slice of the standing areas' claim
        // sets, against their active areas; what waits is taken.
        void ClaimStep()
        {
            if (!FarFlags.ClaimLikeGame || !Plugin.LiveFarSide.Value)
            {
                _claimNew.Clear();
                if (_claimWait.Count > 0)
                {
                    _claimWait.Clear();
                    _claimQueued.Clear();
                }
                LabClaimPending = 0;
                return;
            }
            EnsureActive();
            for (int i = 0; i < _claimNew.Count; i++)
            {
                (ZDO z, ZDOID id) = _claimNew[i];
                if (z.m_uid != id || !z.IsValid() || !z.Persistent || (z.HasOwner() && !z.IsOwner()))
                    continue;
                Vector3 at = z.GetPosition();
                foreach (Area a in _areas)
                {
                    if (!Claiming(a) || !Claims(a, at))
                        continue;
                    if (z.IsOwner())
                        a.Claimed.Add(z);
                    else
                    {
                        QueueClaim(z, a);
                        break;
                    }
                }
            }
            _claimNew.Clear();
            int checks = ClaimChecksPerFrame;
            for (int n = 0; n < _areas.Count && checks > 0; n++)
            {
                if (_claimArea >= _areas.Count)
                    _claimArea = 0;
                Area a = _areas[_claimArea];
                if (Claiming(a))
                {
                    List<ZDO> list = a.ClaimSet;
                    if (a.ClaimAt > list.Count)
                        a.ClaimAt = 0;
                    while (checks > 0 && a.ClaimAt < list.Count)
                    {
                        ZDO z = list[a.ClaimAt++];
                        checks--;
                        if (!z.HasOwner() && z.Persistent && z.IsValid() && Claims(a, z.GetPosition()))
                            QueueClaim(z, a);
                    }
                    // The frame's looks are used up: on from here next frame.
                    if (a.ClaimAt < list.Count)
                        break;
                }
                a.ClaimAt = 0;
                _claimArea++;
            }
            TakeClaims();
            int claimed = 0;
            foreach (Area a in _areas)
                if (!a.Draining)
                    claimed += a.Claimed.Count;
            LabClaimed = claimed;
            LabClaimPending = _claimWait.Count;
        }

        void QueueClaim(ZDO z, Area a)
        {
            if (_claimQueued.Add(z))
                _claimWait.Add(new ClaimEntry { Zdo = z, Area = a });
        }

        // What waits is taken, characters first (unowned they neither show nor move), in multiplayer within the frame's
        // cap; what is not nobody's any more, has left its area's active area, or whose area drains or went, is dropped.
        void TakeClaims()
        {
            if (_claimWait.Count == 0 || ZDOMan.instance == null)
                return;
            int frame = Time.frameCount;
            if (_claimFrame != frame)
            {
                _claimFrame = frame;
                _claimLeft = Multiplayer() ? ClaimCapMp : int.MaxValue;
            }
            EnsureActive();
            long me = ZDOMan.GetSessionID();
            for (int pass = 0; pass < 2 && _claimLeft > 0; pass++)
                for (int i = 0; i < _claimWait.Count && _claimLeft > 0; i++)
                {
                    ClaimEntry e = _claimWait[i];
                    ZDO z = e.Zdo;
                    if (z == null || (pass == 0 && !IsCharacter(z)))
                        continue;
                    Area a = e.Area;
                    if (!z.IsValid() || a.Draining || a.Gone || (z.HasOwner() && !z.IsOwner()) || !Claims(a, z.GetPosition()))
                    {
                        Unqueue(i, z);
                        continue;
                    }
                    if (!z.IsOwner())
                    {
                        z.SetOwner(me);
                        _claimLeft--;
                        LabClaimTook++;
                    }
                    a.Claimed.Add(z);
                    _release.Remove(z);
                    Unqueue(i, z);
                }
            int kept = 0;
            for (int i = 0; i < _claimWait.Count; i++)
                if (_claimWait[i].Zdo != null)
                    _claimWait[kept++] = _claimWait[i];
            _claimWait.RemoveRange(kept, _claimWait.Count - kept);
        }

        void Unqueue(int i, ZDO z)
        {
            _claimWait[i] = default;
            _claimQueued.Remove(z);
        }

        // A client, or a host with peers: the frame's takes are capped.
        static bool Multiplayer()
        {
            ZNet net = ZNet.instance;
            return net != null && (!net.IsServer() || net.GetPeers().Count > 0);
        }

        // The object's prefab has a Character (by prefab, looked up once).
        static bool IsCharacter(ZDO z)
        {
            int prefab = z.GetPrefab();
            if (s_characterPrefab.TryGetValue(prefab, out bool character))
                return character;
            GameObject go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefab) : null;
            if (go == null)
                return false;
            character = go.GetComponent<Character>() != null;
            s_characterPrefab[prefab] = character;
            return character;
        }

        // Lab (Docs\PARITY-PLAN.md §5.6): of the persistent objects the area round this far portal lists near, those inside
        // its active area and how many of them this game owns (characters apart), and those outside every area's active
        // area and the player's that this game owns all the same (0 expected). Walks the list; allocates nothing.
        internal void LabClaimCensus(ZDO farPortal, out int inside, out int ownedInside, out int characters, out int charactersOwned, out int ownedOutside)
        {
            inside = ownedInside = characters = charactersOwned = ownedOutside = 0;
            if (farPortal == null || ZNet.instance == null || ZoneSystem.instance == null || !_byPortal.TryGetValue(farPortal.m_uid, out Area a))
                return;
            EnsureActive();
            Vector3 player = ZNet.instance.GetReferencePosition();
            foreach (ZDO z in a.Near)
            {
                if (z == null || !z.Persistent)
                    continue;
                Vector3 at = z.GetPosition();
                bool own = z.IsOwner();
                if (Claims(a, at))
                {
                    inside++;
                    if (own)
                        ownedInside++;
                    if (IsCharacter(z))
                    {
                        characters++;
                        if (own)
                            charactersOwned++;
                    }
                }
                else if (own && !ZNetScene.InActiveArea(at, player) && !InAnyActiveArea(at, true))
                    ownedOutside++;
            }
        }

        // Every frame, from PortalSystem.Update.
        internal void Update()
        {
            if (ZNet.instance == null || ZoneSystem.instance == null || ZNetScene.instance == null || ZDOMan.instance == null)
            {
                Clear();
                return;
            }
            // FarWorld (shim): on or off the registry as Far.V2 and Far.Registry say.
            ShimSync();
            // Far.Proxies: objects that were proxies must be made (their state, an active area over them, proxies off or
            // dropped): every area is listed again, one a frame (ProxyRelistStep).
            if (FarProxies.RealizeVersion != _proxyRealize)
            {
                _proxyRealize = FarProxies.RealizeVersion;
                foreach (Area a in _areas)
                    a.ProxyRelist = !a.Draining;
            }
            // Far.SectorSleep switched: what the other way put to sleep wakes before anything sleeps or wakes again.
            SyncSleepMode();
            Select();
            // Far.SideReady, Far.VisibleNear: the half each area's nearest window shows, and which windows are live or about
            // to show; an area whose shown half flipped is put in order for it again (P4.3).
            if (FarFlags.SideReady || FarFlags.VisibleNear)
                SeeLinks(null);
            _clock.Restart();
            double budget = Budget();
            // Far.ZoneSpawnSplit: zoneSpawned also when a zone under way went on (no zone begins then); zoneBegun only
            // when one spawned or began (no terrain mesh with it).
            bool zoneSpawned = false, zoneBegun = false, relisted = false;
            ZoneSystem zs = ZoneSystem.instance;
            var zones = (IDictionary)s_zones.GetValue(zs);
            float now = Time.time;
            bool pacer = FarFlags.LoadPacer, coreFirst = FarFlags.CoreFirst, terrainBuilt = false;
            _stepped = false;
            // Far.ZoneSpawnSplit: a zone spawn under way is finished at once where the split may no longer go on
            // (switched off, another player, another mod's patch), and forgotten with a scene that is gone.
            if (FarZoneSpawn.Count > 0)
                FarZoneSpawn.Tend(zs);
            // Far.ArrivalPacer: one new area a frame while arriving, also without the pacer (Select leaves them here).
            if (pacer || _arriving)
                CreateArea(now);
            // Far.CoreFirst: the areas filling in behind a standing core go first this frame while they got less than a
            // quarter of the loading time lately. A share of each frame's budget alone would not hold: a zone or a
            // heavy object overruns the whole frame, and the ones after it get nothing (308408 waited 48 s).
            bool fillFirst = false;
            if (coreFirst)
            {
                _coreMs *= 0.95;
                _fillMs *= 0.95;
                fillFirst = _fillMs < 0.25 * (_coreMs + _fillMs);
            }
            // Loading order: areas whose place around the portal is not there yet first (Far.SideReady: the half their
            // window shows), then those still filling in, each group nearest portal first; every one takes what the ones
            // before it left of the frame's budget.
            _order.Clear();
            for (int pass = 0; pass < 3; pass++)
            {
                int p = fillFirst && pass < 2 ? 1 - pass : pass;
                foreach ((ZDO far, float _) in _wanted)
                    if (_byPortal.TryGetValue(far.m_uid, out Area w) && !w.Draining && (p == 0 ? !FrontStands(w) : p == 1 ? FrontStands(w) && !w.Ready : w.Ready))
                        _order.Add(w);
            }
            foreach (Area a in _areas)
                if (!a.Draining && !_order.Contains(a))
                    _order.Add(a);
            // Far.LoadPacer: of the loading areas only the one listed longest ago is relisted, one a frame and with what
            // the areas before it left of the budget (all of them in one frame took up to 99 ms). One that must not
            // wait (it spawned a zone, or comes back from draining with old lists) and is overdue goes first in the
            // frame instead; the others only stand behind the nearer ones and lose nothing by waiting. A loaded one
            // unlisted for LoadedOverdue takes that place when no loading one needs it.
            Area relist = null, overdue = null, stale = null;
            if (pacer)
            {
                foreach (Area a in _order)
                {
                    if (a.Ready)
                    {
                        if (now - a.ListedAt > LoadedOverdue && (stale == null || a.ListedAt < stale.ListedAt))
                            stale = a;
                        continue;
                    }
                    if (now - a.ListedAt <= RelistEvery)
                        continue;
                    if (relist == null || a.ListedAt < relist.ListedAt)
                        relist = a;
                    if ((a.ZoneMade || a.ListedAt < 0f) && now - a.ListedAt > RelistOverdue && (overdue == null || a.ListedAt < overdue.ListedAt))
                        overdue = a;
                }
                if (overdue == null)
                    overdue = stale;
                if (overdue != null && !_stepped)
                {
                    relisted = true;
                    RelistTimed(overdue);
                }
            }
            // Far.ZoneSpawnSplit: the zone whose spawn is under way goes on with its next parts before the areas, as the
            // frame's zone step (one a frame for all). No zone begins while it is under way, so it is not left to wait
            // behind the objects of the areas before its own, holding their zones back.
            if (FarZoneSpawn.Count > 0 && FarZoneSpawn.Current(out Vector2s going) && (pacer || _clock.Elapsed.TotalMilliseconds < budget))
            {
                Area by = _zoneArea;
                double began = _clock.Elapsed.TotalMilliseconds;
                zoneSpawned = ZoneParts(by, going, budget, pacer);
                // Far.CoreFirst: the loading time of the area that began it.
                if (coreFirst && by != null && !by.Draining && !by.Ready)
                {
                    if (FrontStands(by))
                        _fillMs += _clock.Elapsed.TotalMilliseconds - began;
                    else
                        _coreMs += _clock.Elapsed.TotalMilliseconds - began;
                }
            }
            for (int i = 0; i < _order.Count; i++)
            {
                Area a = _order[i];
                bool focus = !a.Ready;
                // Far.CoreFirst: an area whose core does not stand yet spawns the core's zones only and creates the
                // core's objects only; the rest of it waits until its window is live. Far.SideReady: the core's and those
                // of the half its window shows (InFront), in that order, until that half stands (P4.3).
                bool core = !FrontStands(a), coreOnly = coreFirst && core;
                double began = _clock.Elapsed.TotalMilliseconds;
                // Keep the zones alive (the game drops a zone 4 s after nobody asked for it), every second; a loading
                // area spawns its missing ones, one a frame for all (the terrain is built off the main thread first).
                // Far.LoadPacer: a zone only when the budget left covers its usual cost, and not with a terrain mesh.
                if (focus || now >= a.NextPoke)
                {
                    a.NextPoke = now + 1f + i * 0.05f;
                    foreach (Vector2s z in a.Zones)
                    {
                        if (zones.Contains(z))
                            s_pokeZone(zs, z);
                        // Far.ZoneSpawnSplit: the zone whose spawn is under way (it went on above) is still wanted by a
                        // loading area; no other zone begins before it stands.
                        else if (FarZoneSpawn.Count > 0 && FarZoneSpawn.Attend(z))
                            continue;
                        else if (focus && !zoneSpawned && (!coreOnly || InFront(a, z)) && FarZoneSpawn.Count == 0 && !ArrivalHoldsZone()
                                 && (pacer ? !terrainBuilt && Fits(budget, _zoneMs) : _clock.Elapsed.TotalMilliseconds < budget))
                        {
                            double t0 = _clock.Elapsed.TotalMilliseconds;
                            // Far.ZoneSpawnSplit: a far zone the game generates here is begun with its root only (what
                            // the usual zone cost weighs from then on, as a generated zone's spawn); the rest follows in
                            // parts. Others spawn whole, as without the switch.
                            int begun = FarFlags.ZoneSpawnSplit ? FarZoneSpawn.Begin(zs, z) : FarZoneSpawn.Whole;
                            if (begun == FarZoneSpawn.Whole ? s_pokeZone(zs, z) : begun > 0)
                            {
                                if (begun > 0)
                                    _zoneArea = a;
                                zoneSpawned = true;
                                zoneBegun = true;
                                _statSpawnedZones++;
                                double spawnMs = _clock.Elapsed.TotalMilliseconds - t0;
                                NoteSlow("zone " + z.x + "," + z.y, spawnMs);
                                if (pacer)
                                {
                                    _zoneMs = Ema(_zoneMs, spawnMs);
                                    _stepped = true;
                                    a.ZoneMade = true;
                                }
                            }
                        }
                    }
                }
                // Relisted twice a second while loading, every 5 s once loaded (what appears there meanwhile is
                // tracked as it appears); at most one loaded area a frame.
                if (pacer)
                {
                    // Far.LoadPacer: the loading one picked above when the budget left covers a listing; a loaded one
                    // as before, but not in a frame that relisted already, and also only when the budget left covers
                    // it: it no longer comes on top of a zone or a heavy object (one unlisted for LoadedOverdue takes
                    // the place first in a frame above).
                    if (focus ? a == relist && !relisted && Fits(budget, _relistMs) : !relisted && now - a.ListedAt > 5f && Fits(budget, _relistMs))
                    {
                        relisted = true;
                        RelistTimed(a);
                    }
                }
                else if (focus ? now - a.ListedAt > RelistEvery : !relisted && now - a.ListedAt > 5f)
                {
                    relisted |= !focus;
                    double t0 = _clock.Elapsed.TotalMilliseconds;
                    Relist(a);
                    NoteSlow("relist", _clock.Elapsed.TotalMilliseconds - t0);
                }
                // Far.CoreFirst: the distant terrain (the core stands only with it) before the objects, which otherwise
                // take the whole budget until the area is full, so the core stood only when all of it did.
                if (coreFirst)
                    TerrainStep(a, budget, pacer, zoneBegun, ref terrainBuilt);
                // Loaded areas only pick up what appears in them later (a creature walking in); loading ones build.
                if (focus || a.Pending.Count > 0)
                    CreateSome(a, zs, focus ? budget : Math.Min(budget, 1.0), coreOnly);
                if (!coreFirst)
                    TerrainStep(a, budget, pacer, zoneBegun, ref terrainBuilt);
                if (coreFirst && focus)
                {
                    if (core)
                        _coreMs += _clock.Elapsed.TotalMilliseconds - began;
                    else
                        _fillMs += _clock.Elapsed.TotalMilliseconds - began;
                }
                // Far.ClaimLikeGame: and what nobody owns in its active area is taken first (ClaimCore): in the window's
                // first live frame what lives there shows and moves.
                if (!a.CoreReady && CoreLoaded(a) && (a.Terrain == null || a.Terrain.Built) && (!FarFlags.ClaimLikeGame || ClaimCore(a)))
                {
                    a.CoreReady = true;
                    // Far.SideReady: it stands once the half its window shows does (SetHalf, CheckHalves).
                    if (!FarFlags.SideReady)
                        a.Stood = true;
                    Plugin.Debug($"Remote area for portal {a.Portal}: the place around it stands in {now - a.StartedAt:0.0} s, {a.Created} objects created");
                }
                // Far.LoadPacer: not before what its last zone generated is listed (the overdue listing above takes it
                // within RelistOverdue). Ready now, it would be listed only after 5 s, past Track's 3 s, and the full
                // KeepAlive pass would destroy that zone's objects only to make them again.
                // Far.ClaimLikeGame: not before its place round the portal stands, which waits for its claims (ClaimCore).
                if (!a.Ready && a.Pending.Count == 0 && LoadedZones(a) == a.Zones.Count && (a.Terrain == null || a.Terrain.Built) && CoreLoaded(a)
                    && !(pacer && a.ZoneMade) && (!FarFlags.ClaimLikeGame || a.CoreReady))
                {
                    a.Ready = true;
                    if (!FarFlags.SideReady)
                        a.Stood = true;
                    Plugin.Debug($"Remote area for portal {a.Portal} ready in {now - a.StartedAt:0.0} s: {a.Zones.Count} zones, {a.Created} objects created");
                }
                // Far.SideReady (P4.2): each half stands once all it holds is there and the core is claimed.
                if (FarFlags.SideReady && !(a.HalfPlus && a.HalfMinus))
                    CheckHalves(a, now, pacer);
            }
            // Far.CrossAtOnce (P5.2): the place the player stands in by a live portal has its distant terrain built ahead;
            // P5.3 (P22): the arriving place's stands in for the game's own until that stands round him.
            SpareStep(budget, pacer, zoneBegun, ref terrainBuilt);
            FarTerrain.HandOffStep(ZNet.instance.GetReferencePosition());
            // Far.VisibleNear (P28): the areas whose heightmaps rebuild at once (FarHeightmaps), as they stand now.
            MarkNearMaps();
            // Far.MpParity (P26): a client tells a server running this mod which far ends it watches.
            PortalServer.ClientTick();
            // Far.ZoneSpawnSplit: a zone spawn no loading area went on with this frame (its area drains) is finished at
            // once: nothing keeps what its parts made any more while the rest would be placed.
            if (FarZoneSpawn.Count > 0)
                FarZoneSpawn.Sweep();
            for (int i = _areas.Count - 1; i >= 0; i--)
                if (_areas[i].Draining && (_areas[i].Keep == null || _areas[i].Keep.Count == 0))
                {
                    // Far.DrainCalm left it asleep: what is left of it (kept by the player or another area) wakes.
                    // Far.SectorSleep: what it left asleep in its zones stays so (those of another area asleep are its;
                    // the player's own zones and those of an area awake woke as they joined the awake set; the rest goes
                    // with the game's removal, and SweepSleepers drops it).
                    if (_paused.ContainsKey(_areas[i].Portal) || _frozen.ContainsKey(_areas[i].Portal))
                        Wake(_areas[i]);
                    // Far.ClaimLikeGame: the claims it kept while draining are given up now (ReleaseSome: not what another
                    // area's active area or the player's holds). Empty without the switch (given up as it began to drain).
                    foreach (ZDO z in _areas[i].Claimed)
                        _release.Add(z);
                    _areas[i].Claimed.Clear();
                    _areas[i].ClaimSet.Clear();
                    _areas[i].Gone = true;
                    _byPortal.Remove(_areas[i].Portal);
                    _areas.RemoveAt(i);
                    _areasVersion++;
                }
            // Far.Proxies: one area a frame listed again for what was proxied and must be made now.
            ProxyRelistStep();
            // Far.ClaimLikeGame: what nobody owns in the standing areas' active areas is taken (before any is given up).
            ClaimStep();
            ReleaseSome(budget);
            ScanLively();
            UpdateSight();
            double ms = _clock.Elapsed.TotalMilliseconds;
            CountStats(ms);
        }

        void CreateSome(Area a, ZoneSystem zs, double budget, bool coreOnly)
        {
            if (a.Pending.Count == 0)
                return;
            bool pacer = FarFlags.LoadPacer;
            if (pacer && _locationProxy == 0 && zs.m_locationProxyPrefab != null)
                _locationProxy = zs.m_locationProxyPrefab.name.GetStableHashCode();
            int done = 0;
            bool proxies = FarProxies.Proxying;
            int waiting = 0;
            for (int i = 0; i < a.Pending.Count; i++)
            {
                ZDO z = a.Pending[i];
                if (z.Created || _bad.Contains(z))
                {
                    a.Pending[i] = null;
                    done++;
                    continue;
                }
                // Far.Proxies: drawn as a proxy (not made, and there for readiness), or its proxy is on its way (not made, and
                // still missing: the place does not stand before it is drawn).
                if (proxies)
                {
                    ProxyState ps = FarProxies.StateOf(z);
                    if (ps == ProxyState.Proxy)
                    {
                        a.Pending[i] = null;
                        done++;
                        continue;
                    }
                    // At most ProxyWaitScan of those looked at a frame (their sectors build within FarProxies' budget; readiness
                    // looks at the first pending one only).
                    if (ps == ProxyState.Waiting)
                    {
                        if (++waiting >= ProxyWaitScan)
                            break;
                        continue;
                    }
                }
                if (_clock.Elapsed.TotalMilliseconds >= budget)
                    break;
                Vector2s sector = z.GetSector();
                // Far.CoreFirst: pending is sorted core first, so the first one outside the core ends the core's part.
                // Far.SideReady: core, then the shown half; the first one past them ends that part.
                if (coreOnly && !(FarFlags.SideReady ? Rank(a, z, a.SortedHalf) <= 1 : InCore(a, sector)))
                    break;
                // Near objects stand on their zone's terrain; the distant ones (big trees, buildings) show without it.
                if ((!z.Distant && !zs.IsZoneLoaded(sector)) || !zs.IsZoneReadyForType(sector, z.Type))
                    continue;
                // Far.LoadPacer: an object only when the budget left covers what its kind usually takes; it and those
                // after it wait in order for a later frame (one heavy object, a cliff's collider, took 159 ms).
                Dictionary<int, float> costs = null;
                int kind = 0;
                float cost = 0f;
                if (pacer)
                {
                    kind = z.GetPrefab();
                    costs = _prefabMs;
                    if (_locationProxy != 0 && kind == _locationProxy)
                    {
                        kind = z.GetInt(ZDOVars.s_location);
                        costs = _locationMs;
                    }
                    costs.TryGetValue(kind, out cost);
                    if (!Fits(budget, cost))
                    {
                        _statHeld++;
                        break;
                    }
                }
                double t0 = _clock.Elapsed.TotalMilliseconds;
                GameObject made = s_createObject(ZNetScene.instance, z);
                double madeMs = _clock.Elapsed.TotalMilliseconds - t0;
                NoteSlow(made != null ? made.name : "invalid", madeMs);
                if (pacer)
                {
                    costs[kind] = Ema(cost, madeMs);
                    _stepped = true;
                }
                if (made == null)
                    _bad.Add(z);
                else
                {
                    a.Created++;
                    _statCreated++;
                }
                a.Pending[i] = null;
                done++;
            }
            if (done > 0)
                a.Pending.RemoveAll(z => z == null);
        }

        // One mesh of the area's distant terrain, within the budget. Far.LoadPacer: only when the budget left covers
        // its usual cost, and not in a frame that began or spawned a zone (each is a long step of its own); a part of
        // a zone spawn under way (Far.ZoneSpawnSplit) is admitted through Fits like the mesh, so it holds none back.
        void TerrainStep(Area a, double budget, bool pacer, bool zoneBegun, ref bool built) =>
            BuildTerrain(a.Terrain, budget, pacer, zoneBegun, ref built);

        // The same for any distant terrain (an area's, or Far.CrossAtOnce's spare).
        void BuildTerrain(FarTerrain t, double budget, bool pacer, bool zoneBegun, ref bool built)
        {
            if (t == null || t.Built)
                return;
            if (!pacer)
            {
                if (_clock.Elapsed.TotalMilliseconds < budget)
                    t.BuildStep();
                return;
            }
            if (zoneBegun || !Fits(budget, _terrainMs))
                return;
            double t0 = _clock.Elapsed.TotalMilliseconds;
            if (t.BuildStep())
            {
                _terrainMs = Ema(_terrainMs, _clock.Elapsed.TotalMilliseconds - t0);
                built = true;
                _stepped = true;
            }
        }

        // Far.ZoneSpawnSplit: the next parts of the zone whose spawn is under way (FarZoneSpawn: its location, each
        // vegetation kind, then the zone made to stand), as many as fit: with the pacer each when the budget left covers
        // what that part usually takes (the frame's first step always may), without it while the budget lasts (the first
        // always). True when one was placed. The area that began it (null: gone) is marked as having made a zone.
        bool ZoneParts(Area a, Vector2s z, double budget, bool pacer)
        {
            bool any = false;
            while (FarZoneSpawn.Holds(z))
            {
                if (pacer ? !Fits(budget, FarZoneSpawn.NextCost(z)) : any && _clock.Elapsed.TotalMilliseconds >= budget)
                    break;
                double t0 = _clock.Elapsed.TotalMilliseconds;
                int done = FarZoneSpawn.Part(z);
                if (done < 0)
                    break;
                double ms = _clock.Elapsed.TotalMilliseconds - t0;
                if (ms >= 8.0)
                    NoteSlow("zone part " + z.x + "," + z.y, ms);
                any = true;
                if (pacer)
                {
                    _stepped = true;
                    if (a != null)
                        a.ZoneMade = true;
                }
                if (done > 0)
                {
                    _zoneArea = null;
                    break;
                }
            }
            return any;
        }

        // Far.LoadPacer: a listing as a timed step (a loading area's teaches the usual cost of one).
        void RelistTimed(Area a)
        {
            bool loading = !a.Ready;
            double t0 = _clock.Elapsed.TotalMilliseconds;
            Relist(a);
            double ms = _clock.Elapsed.TotalMilliseconds - t0;
            NoteSlow("relist", ms);
            if (loading)
                _relistMs = Ema(_relistMs, ms);
            a.ZoneMade = false;
            _stepped = true;
        }

        // Far.LoadPacer: a step of this usual cost may start now: what is left of the frame's budget covers it, or it
        // would be the frame's first (the loader checked the budget only before a step, so one step past it overran
        // the frame by its whole cost: 10-17 ms a frame on an 8 ms budget).
        // Far.ArrivalPacer: while arriving not even the first (a zone, a terrain mesh, a heavy object waits for after).
        bool Fits(double budget, float ms) => _clock.Elapsed.TotalMilliseconds + (_stepped || _arriving ? ms : 0f) < budget;

        static float Ema(float ema, double ms) => ema <= 0f ? (float)ms : ema + ((float)ms - ema) * 0.3f;

        // The zones next to the far portal's own are loaded and their objects created.
        bool CoreLoaded(Area a)
        {
            foreach (Vector2s z in a.Zones)
                if (InCore(a, z) && !ZoneSystem.instance.IsZoneLoaded(z))
                    return false;
            if (!Arrived(a))
                return false;
            // Pending is sorted core first: the first one left is a core one while any is.
            foreach (ZDO z in a.Pending)
                if (z != null)
                    return !InCore(a, z.GetSector());
            return true;
        }

        // In multiplayer the objects come from the server (a minute at most, should it never say; Far.MpParity, P26: never
        // without them, the window keeps the vanilla look and waits). What came from the server since the last listing is
        // listed (and so waited for) first. An edge area (Far.DupEdge) does not wait: its far portal stands in the player's
        // own area, whose objects the game's own sends bring, and its core may reach past the sectors the server sends him
        // (the count would never complete, and the portal would stay shut).
        bool Arrived(Area a)
        {
            if (a.Edge)
                return true;
            if (!PortalServer.CoreArrived(a.Zone) && (FarFlags.MpParity || Time.time - a.StartedAt < 60f))
            {
                a.ArrivedAt = -1f;
                return false;
            }
            if (a.ArrivedAt < 0f)
                a.ArrivedAt = Time.time;
            return !(PortalServer.FromServer && a.ListedAt <= a.ArrivedAt);
        }

        // The time per frame for loading: more while the loading screen hides the game anyway (right after joining a
        // world) and while you stand close to a portal whose far end is not there yet (Far.SideReady: the half its window
        // shows), or (Far.SideReady) one whose window is about to show.
        double Budget()
        {
            double budget = Plugin.LoadBudgetMs.Value;
            if (LoadingScreen())
                return Math.Max(budget, 30.0);
            // Far.ArrivalPacer: no more than that while the game loads the place the player jumped to.
            if (_arriving)
                return budget;
            Player p = Player.m_localPlayer;
            if (p != null)
                foreach (PortalLink link in PortalLink.All)
                {
                    ZDO far = link.IsReal ? link.PartnerZdo : null;
                    bool side = FarFlags.SideReady;
                    if (far == null || !_byPortal.TryGetValue(far.m_uid, out Area a) || a.Draining)
                        continue;
                    // Far.SideReady: a window not seen (ShownOf 0) gets no boost.
                    int shown = side ? ShownOf(link) : 0;
                    if (side ? shown == 0 || HalfStands(a, shown) : a.CoreReady)
                        continue;
                    // Far.SideReady (P4.4): also while its window is about to show (the camera turning or moving to it).
                    if ((Utils.DistanceXZ(p.transform.position, link.transform.position) < 40f && (!FarFlags.LoadPacer || FarFlags.PacerBoostAll || Approached(p, link)))
                        || (side && WindowReach.AboutToShow(link)))
                    {
                        budget = Math.Max(budget, Plugin.ActivationBudgetMs.Value);
                        break;
                    }
                }
            return budget;
        }

        // Far.LoadPacer: the player is coming up to this portal (closing in faster than 0.5 m/s) or stands by it (15 m).
        // Only then is its far end urgent: standing among twenty portals 20 m away gave all of them the 8 ms until
        // every one stood, and loading took 10-17 ms of each frame for most of a minute. Far.PacerBoostAll keeps the
        // old rule (the pacer then holds the 8 ms instead of overrunning it), should the far ends come too slowly.
        static bool Approached(Player p, PortalLink link)
        {
            Vector3 to = link.transform.position - p.transform.position;
            to.y = 0f;
            float d = to.magnitude;
            if (d <= 15f)
                return true;
            Vector3 v = p.GetVelocity();
            return (v.x * to.x + v.z * to.z) / d > 0.5f;
        }

        // ---- Far.SideReady (Docs\PARITY-PLAN.md P4) ---------------------------------------------------------------------
        // A window went live once the zones round its far portal stood (Far.CoreFirst), and the rest streamed into the live
        // window: outer zones, the distant ring and unspawned zones popped in, split zones showed bare ground and then their
        // vegetation kind by kind (hall 20: live at 18-23 s, all of it at 99-127 s). A window is two-sided (PortalMath.Pair
        // works for both faces), so which half of the far side it shows depends on the camera's side of the entry. With the
        // switch:
        //  - Halves: the far portal's plane splits its place into the half in front of it (+1, along its Forward) and the half
        //    behind (-1). An object within WindowReach.Slab of the plane past its own reach round its pivot (its prefab's
        //    renderers, a location's exterior radius) counts in both, a zone in each half its square reaches into.
        //  - A half stands (HalfPlus, HalfMinus) once every zone it reaches into is loaded (a zone FarZoneSpawn is splitting is
        //    not, until it stands) with no heightmap rebuild queued, none of its pending objects (near zones and ring) is left
        //    to make, the distant terrain is built, what its last zone generated is listed, the server's core objects are in
        //    (multiplayer, Arrived; Far.MpParity: never without them), and the core's active area is claimed
        //    (Far.ClaimLikeGame). A link goes live, and with Far.LiveRecheck stays live, while the half its window shows stands
        //    (MayShow); a camera crossing to a half not standing yet drops it, and the portal shows the vanilla look until that
        //    half stands. Stage 2 means a half stands.
        //  - Order: the core's objects, then the half the nearest window onto it shows (one about to show first), then the
        //    other half's near zones, then its ring (Rank); its zones the same way, so zone spawning follows. Put in order again
        //    at each listing and when that half flips. Far.CoreFirst holds the rest back until the shown half stands.
        //  - The 8 ms loading budget holds while a portal is approached or its window is about to show, until the half it shows
        //    stands (Budget).
        // Off: live once the place round the far portal stands, the rest streaming in, as before. Main thread only; nothing
        // allocated per frame once the lists grew.

        // An object's reach round its pivot is taken as at most this (a location's exterior radius, a big building).
        const float MaxObjectReach = 64f;

        // What each prefab reaches round its pivot along the ground (its renderers' bounds; -1: a location proxy, whose
        // location's exterior radius counts, kept by location), looked up once each.
        static readonly Dictionary<int, float> s_prefabReach = new Dictionary<int, float>();
        static readonly Dictionary<int, float> s_locationReach = new Dictionary<int, float>();
        static readonly List<Renderer> s_reachRenderers = new List<Renderer>();
        static readonly Vector3[] s_corners = new Vector3[8];
        static Func<ZoneSystem, int, ZoneSystem.ZoneLocation> s_getLocation;
        static bool s_getLocationLooked;
        static int s_proxyHash;

        // Pending objects and zones being put in order (OrderForHalf).
        struct PendingKey
        {
            internal ZDO Zdo;
            internal int Rank;
            internal float D;
        }

        struct ZoneKey
        {
            internal Vector2s Zone;
            internal int Rank;
            internal float D;
        }

        // By rank, then (objects) by type as the game orders them (ZDO.ObjectType from the top), then nearest first.
        sealed class PendingOrder : IComparer<PendingKey>
        {
            internal static readonly PendingOrder Instance = new PendingOrder();
            public int Compare(PendingKey x, PendingKey y)
            {
                if (x.Rank != y.Rank)
                    return x.Rank.CompareTo(y.Rank);
                ZDO.ObjectType tx = x.Zdo.Type, ty = y.Zdo.Type;
                return tx == ty ? x.D.CompareTo(y.D) : ((int)ty).CompareTo((int)tx);
            }
        }

        sealed class ZoneOrder : IComparer<ZoneKey>
        {
            internal static readonly ZoneOrder Instance = new ZoneOrder();
            public int Compare(ZoneKey x, ZoneKey y) => x.Rank != y.Rank ? x.Rank.CompareTo(y.Rank) : x.D.CompareTo(y.D);
        }

        readonly List<PendingKey> _pendingKeys = new List<PendingKey>();
        readonly List<ZoneKey> _zoneKeys = new List<ZoneKey>();

        // Lab: seconds from an area's start to its first half standing (the last area that stood one, and the longest), and
        // halves stood in all.
        internal static float LabHalfReadyAt, LabHalfReadyAtMax;
        internal static int LabHalvesStood;

        // Lab (§5.12, per area): seconds from the start of the area round this far portal to each of its halves standing (the
        // half in front of the far portal, the one behind; -1 not yet); false without an area.
        internal bool LabHalfReady(ZDO farPortal, out float front, out float behind)
        {
            front = behind = -1f;
            if (farPortal == null || !_byPortal.TryGetValue(farPortal.m_uid, out Area a))
                return false;
            front = a.HalfPlusAt;
            behind = a.HalfMinusAt;
            return true;
        }

        // Far.SideReady without Far.LiveRecheck (P4.5): a live link onto this far portal is checked every frame while its area
        // has stood but has a half not standing yet, so a camera crossing to that half's side drops it until it stands.
        internal bool SideRechecks(ZDO farPortal) =>
            FarFlags.SideReady && farPortal != null && _byPortal.TryGetValue(farPortal.m_uid, out Area a) && !a.Draining && a.Stood
            && !(a.HalfPlus && a.HalfMinus);

        // Every frame (Far.SideReady or Far.VisibleNear; for a new area as it is made): each area's far portal plane as measured
        // (its portal loaded and measured), whether a window onto it is live or about to show, and the half the nearest window
        // onto it shows (one about to show before any other; each from the eye that sees it, EyeOf, and none past the render
        // reach of that eye). Then (Far.SideReady) an area whose shown half flipped is put in order for it again (P4.3).
        void SeeLinks(Area only)
        {
            Camera cam = Utils.GetMainCamera();
            Vector3 eye = cam != null ? cam.transform.position : Vector3.zero;
            if (only != null)
                Unsee(only);
            else
                foreach (Area a in _areas)
                    Unsee(a);
            foreach (PortalLink link in PortalLink.All)
            {
                ZDO far = link.PartnerZdo;
                if (far == null)
                    continue;
                Area a;
                if (only != null)
                {
                    if (far.m_uid != only.Portal)
                        continue;
                    a = only;
                }
                else if (!_byPortal.TryGetValue(far.m_uid, out a) || a.Draining)
                    continue;
                PortalFrame measured = link.FarFrame;
                if (measured != null)
                {
                    a.Plane.Center = measured.Center;
                    a.Plane.Rotation = measured.Rotation;
                }
                bool about = WindowReach.AboutToShow(link);
                a.LiveLink |= link.IsActive;
                a.About |= about;
                if (cam == null || !link.FrameSet)
                    continue;
                // The eye this window is seen from (EyeOf); one not seen from it (past the render reach) sets no half.
                Vector3 at = EyeOf(link, eye);
                int shown = ShownFrom(link, at);
                if (shown == 0)
                    continue;
                float d = (link.Frame.Center - at).sqrMagnitude - (about ? 1e9f : 0f);
                if (d < a.SeenAt)
                {
                    a.SeenAt = d;
                    a.Shown = shown;
                }
            }
            if (only != null || !FarFlags.SideReady)
                return;
            // A camera going to and fro across an entry's plane puts it in order at most every RelistEvery.
            float now = Time.time;
            foreach (Area a in _areas)
                if (!a.Draining && a.SortedHalf != a.Shown && !(a.HalfPlus && a.HalfMinus) && (a.SortedHalf == 0 || now - a.SortedAt > RelistEvery))
                    OrderForHalf(a);
        }

        static void Unsee(Area a)
        {
            a.LiveLink = a.About = false;
            a.SeenAt = float.MaxValue;
        }

        // P4.3: the area's pending objects and zones in order for the half its window shows (Rank).
        void OrderForHalf(Area a)
        {
            int shown = a.Shown;
            a.SortedHalf = shown;
            a.SortedAt = Time.time;
            Vector3 c = a.Center;
            List<PendingKey> keys = _pendingKeys;
            keys.Clear();
            foreach (ZDO z in a.Pending)
                if (z != null)
                    keys.Add(new PendingKey { Zdo = z, Rank = Rank(a, z, shown), D = Utils.DistanceSqr(c, z.GetPosition()) });
            keys.Sort(PendingOrder.Instance);
            a.Pending.Clear();
            for (int i = 0; i < keys.Count; i++)
                a.Pending.Add(keys[i].Zdo);
            keys.Clear();
            Vector3 zc = ZoneSystem.GetZonePos(a.Zone);
            _zoneKeys.Clear();
            foreach (Vector2s z in a.Zones)
                _zoneKeys.Add(new ZoneKey { Zone = z, Rank = InCore(a, z) ? 0 : ZoneInHalf(a, z, shown) ? 1 : 2, D = (ZoneSystem.GetZonePos(z) - zc).sqrMagnitude });
            _zoneKeys.Sort(ZoneOrder.Instance);
            a.Zones.Clear();
            for (int i = 0; i < _zoneKeys.Count; i++)
                a.Zones.Add(_zoneKeys[i].Zone);
            _zoneKeys.Clear();
        }

        // P4.3: 0 an object of the core zones, 1 one the shown half holds, 2 one of the other half's near zones, 3 one of its
        // ring (the distant objects past its near zones). Shown 0 (not in order by halves): the core, or the rest.
        int Rank(Area a, ZDO z, int shown)
        {
            Vector2s s = z.GetSector();
            if (InCore(a, s))
                return 0;
            if (shown != 0 && InHalf(a, z, shown))
                return 1;
            return Math.Max(Math.Abs(s.x - a.Zone.x), Math.Abs(s.y - a.Zone.y)) <= a.ZoneReach ? 2 : 3;
        }

        // P4.1: the object counts in that half of the far side (within the plane's slab, past its own reach, in both).
        static bool InHalf(Area a, ZDO z, int half) => WindowReach.InHalf(a.Plane, z.GetPosition(), half, ReachOf(z));

        // A zone's square reaches into that half of the far side (a zone across the plane counts in both).
        static bool ZoneInHalf(Area a, Vector2s zone, int half)
        {
            Vector3 n = a.Plane.Forward;
            float d = a.Plane.SignedDistance(ZoneSystem.GetZonePos(zone));
            float e = ZoneSystem.instance.m_zoneSize * 0.5f * (Mathf.Abs(n.x) + Mathf.Abs(n.z)) + WindowReach.Slab + a.MaxReach;
            return half >= 0 ? d + e > 0f : d - e < 0f;
        }

        // A zone loading areas spawn first (Far.CoreFirst's hold): the core's, and with Far.SideReady those the half the
        // window shows reaches into.
        static bool InFront(Area a, Vector2s zone) => InCore(a, zone) || (FarFlags.SideReady && (a.HoldReleased || ZoneInHalf(a, zone, a.Shown)));

        // From Update, for an area with a half not standing: the half its window shows first, then the other. An edge area
        // (Far.DupEdge) does not wait for its distant terrain: its far portal stands inside the player's active area, where
        // the game's own (round a point within its 256 m step of the camera) is what the game would draw standing there.
        // Lab ("whyshut"): for each area whose half does not stand, which of CheckHalves' conditions holds it back.
        internal string LabWhyNotStanding()
        {
            var sb = new System.Text.StringBuilder();
            ZoneSystem zs = ZoneSystem.instance;
            foreach (Area a in _areas)
            {
                if (a.HalfPlus && a.HalfMinus)
                    continue;
                sb.Append($"\n  area {a.Portal} at {a.Center:F0}: shown {a.Shown}, halves +{a.HalfPlus} -{a.HalfMinus}, stood {a.Stood}, draining {a.Draining}, edge {a.Edge}, " +
                          $"terrain {(a.Terrain == null ? "none" : a.Terrain.Built ? "built" : "NOT BUILT")}, zoneMade {a.ZoneMade}, arrived {Arrived(a)}, " +
                          $"pending {a.Pending.Count}, claim waits {_claimWait.Count}");
                if (zs == null)
                    continue;
                for (int half = 1; half >= -1; half -= 2)
                {
                    if (HalfStands(a, half))
                        continue;
                    int notLoaded = 0, queued = 0, pend = 0;
                    var sample = new System.Text.StringBuilder();
                    foreach (Vector2s z in a.Zones)
                    {
                        if (!ZoneInHalf(a, z, half))
                            continue;
                        if (!zs.IsZoneLoaded(z))
                        {
                            if (notLoaded++ < 4)
                                sample.Append($" unloaded {z.x},{z.y}");
                        }
                        else if (Queued(a, z) && queued++ < 4)
                            sample.Append($" queued {z.x},{z.y}");
                    }
                    foreach (ZDO z in a.Pending)
                        if (z != null && InHalf(a, z, half) && pend++ < 4)
                        {
                            GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(z.GetPrefab()) : null;
                            sample.Append($" pending {(prefab != null ? prefab.name : z.GetPrefab().ToString())}@{z.GetPosition():F0}{(_bad.Contains(z) ? " (bad)" : "")}");
                        }
                    sb.Append($"\n    half {half}: zones not loaded {notLoaded}, heightmap rebuilds queued {queued}, pending objects {pend};{sample}");
                }
            }
            return sb.Length == 0 ? "every area's halves stand" : sb.ToString();
        }

        const float StallSeconds = 15f;

        void CheckHalves(Area a, float now, bool pacer)
        {
            // The backstop: no pending object made for StallSeconds while a half does not stand lets the hold go.
            if (FarFlags.SideReady && !a.HoldReleased && !(a.HalfPlus && a.HalfMinus))
            {
                if (a.Pending.Count != a.StallCount)
                {
                    a.StallCount = a.Pending.Count;
                    a.StallSince = now;
                }
                else if (a.StallCount > 0 && now - a.StallSince > StallSeconds)
                {
                    a.HoldReleased = true;
                    Plugin.Log.LogWarning($"Remote area for portal {a.Portal}: {a.StallCount} objects waited {StallSeconds:0} s with no progress; its other zones load now too");
                }
            }
            // Far.LiveRecheck: its window turned to the half that stood first (SetHalf), so the area stands now.
            if (!a.Stood && HalfStands(a, a.Shown))
                a.Stood = true;
            if ((a.Terrain != null && !a.Terrain.Built && !a.Edge) || (pacer && a.ZoneMade) || !Arrived(a))
                return;
            for (int k = 0; k < 2; k++)
            {
                int half = k == 0 ? a.Shown : -a.Shown;
                if (HalfStands(a, half) || !HalfThere(a, half))
                    continue;
                // Far.ClaimLikeGame (P3.2): what nobody owns in its active area is taken first, so the window's first live
                // frame shows what lives there moving.
                if (FarFlags.ClaimLikeGame && !ClaimCore(a))
                    return;
                SetHalf(a, half, now);
            }
        }

        // Everything of that half is there: the zones it reaches into loaded with no heightmap rebuild queued, and none of
        // its pending objects left.
        bool HalfThere(Area a, int half)
        {
            ZoneSystem zs = ZoneSystem.instance;
            foreach (Vector2s z in a.Zones)
                if (ZoneInHalf(a, z, half) && (!zs.IsZoneLoaded(z) || Queued(a, z)))
                    return false;
            return !PendingIn(a, half);
        }

        // A pending object counts in that half. Pending is in order for SortedHalf (OrderForHalf): while one of the core or
        // of that half is left, the first one left is; while one of the other half's is left, the last one left is. Past
        // those quick answers the list is looked through (one of the shown half found behind the others, having moved since:
        // put in order again), and again only once its count or its listing changed since a look that found one.
        bool PendingIn(Area a, int half)
        {
            List<ZDO> pending = a.Pending;
            ZDO first = null, last = null;
            for (int i = 0; i < pending.Count && first == null; i++)
                first = pending[i];
            if (first == null)
                return false;
            for (int i = pending.Count - 1; i >= 0 && last == null; i--)
                last = pending[i];
            int sorted = a.SortedHalf;
            if (sorted != 0 && (half == sorted ? Rank(a, first, sorted) <= 1 : Rank(a, last, sorted) >= 2))
                return true;
            bool plus = half >= 0;
            if ((plus ? a.CheckedPlus : a.CheckedMinus) == pending.Count && (plus ? a.CheckedPlusListed : a.CheckedMinusListed) == a.ListedAt)
                return true;
            foreach (ZDO z in pending)
            {
                if (z == null || !InHalf(a, z, half))
                    continue;
                if (plus)
                {
                    a.CheckedPlus = pending.Count;
                    a.CheckedPlusListed = a.ListedAt;
                }
                else
                {
                    a.CheckedMinus = pending.Count;
                    a.CheckedMinusListed = a.ListedAt;
                }
                if (half == sorted)
                    OrderForHalf(a);
                return true;
            }
            return false;
        }

        // A loaded zone of the area has a heightmap rebuild queued (Heightmap.HaveQueuedRebuild: a terrain modifier's poke
        // that the game's pass, or the engine's within Far.FarRegenBudget, has not run yet). Its heightmap is found once.
        bool Queued(Area a, Vector2s zone)
        {
            int r = a.ZoneReach, side = 2 * r + 1;
            int dx = zone.x - a.Zone.x + r, dy = zone.y - a.Zone.y + r;
            if (dx < 0 || dy < 0 || dx >= side || dy >= side)
                return false;
            if (a.Maps == null || a.Maps.Length != side * side)
                a.Maps = new Heightmap[side * side];
            int k = dy * side + dx;
            Heightmap h = a.Maps[k];
            if (h == null)
            {
                Vector3 at = ZoneSystem.GetZonePos(zone);
                h = Heightmap.FindHeightmap(at);
                if (h == null || h.IsDistantLod || Utils.DistanceXZ(h.transform.position, at) > 1f)
                    return false;
                a.Maps[k] = h;
            }
            return h.HaveQueuedRebuild();
        }

        void SetHalf(Area a, int half, float now)
        {
            float s = now - a.StartedAt;
            bool first = !(a.HalfPlus || a.HalfMinus);
            if (half >= 0)
            {
                a.HalfPlus = true;
                a.HalfPlusAt = s;
            }
            else
            {
                a.HalfMinus = true;
                a.HalfMinusAt = s;
            }
            // Far.LiveRecheck: it stood once the half its window shows stands (or both do). The other half standing first
            // (the shown one still has a rebuild or an object to come) leaves a link live from before latched, as for a new
            // area, instead of dropping the window behind the player until the shown half stands.
            if (!FarFlags.SideReady || half == a.Shown || (a.HalfPlus && a.HalfMinus))
                a.Stood = true;
            LabHalvesStood++;
            if (first)
            {
                LabHalfReadyAt = s;
                if (s > LabHalfReadyAtMax)
                    LabHalfReadyAtMax = s;
            }
            Plugin.Debug($"Remote area for portal {a.Portal}: the half {(half >= 0 ? "in front of" : "behind")} its far portal stands in {s:0.0} s, {a.Created} objects created");
        }

        // What the object reaches round its pivot along the ground: its prefab's renderers (scaled as its ZDO says, for one
        // reaching past the slab), a location proxy its location's exterior radius; at most MaxObjectReach.
        static float ReachOf(ZDO z)
        {
            int prefab = z.GetPrefab();
            if (!s_prefabReach.TryGetValue(prefab, out float r))
            {
                r = MeasureReach(prefab);
                s_prefabReach[prefab] = r;
            }
            if (r < 0f)
                return LocationReach(z.GetInt(ZDOVars.s_location));
            if (r <= WindowReach.Slab)
                return r;
            Vector3 v = z.GetVec3(ZDOVars.s_scaleHash, Vector3.zero);
            float scale = v != Vector3.zero ? Mathf.Max(v.x, v.z) : z.GetFloat(ZDOVars.s_scaleScalarHash, 1f);
            return Mathf.Min(r * Mathf.Max(scale, 1f), MaxObjectReach);
        }

        static float MeasureReach(int prefab)
        {
            ZoneSystem zs = ZoneSystem.instance;
            if (s_proxyHash == 0 && zs != null && zs.m_locationProxyPrefab != null)
                s_proxyHash = zs.m_locationProxyPrefab.name.GetStableHashCode();
            if (prefab == s_proxyHash && s_proxyHash != 0)
                return -1f;
            GameObject go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefab) : null;
            if (go == null)
                return 0f;
            Matrix4x4 toRoot = go.transform.worldToLocalMatrix;
            float reach = 0f;
            go.GetComponentsInChildren(true, s_reachRenderers);
            foreach (Renderer rd in s_reachRenderers)
            {
                Bounds b;
                if (rd is SkinnedMeshRenderer skinned)
                    b = skinned.localBounds;
                else if (rd is MeshRenderer && rd.TryGetComponent(out MeshFilter mf) && mf.sharedMesh != null)
                    b = mf.sharedMesh.bounds;
                else
                    continue;
                Matrix4x4 m = toRoot * rd.transform.localToWorldMatrix;
                Vector3 lo = b.min, hi = b.max;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 p = m.MultiplyPoint3x4(new Vector3((i & 1) == 0 ? lo.x : hi.x, (i & 2) == 0 ? lo.y : hi.y, (i & 4) == 0 ? lo.z : hi.z));
                    reach = Mathf.Max(reach, Mathf.Sqrt(p.x * p.x + p.z * p.z));
                }
            }
            s_reachRenderers.Clear();
            return Mathf.Min(reach, MaxObjectReach);
        }

        static float LocationReach(int location)
        {
            if (location == 0)
                return 0f;
            if (s_locationReach.TryGetValue(location, out float r))
                return r;
            if (!s_getLocationLooked)
            {
                s_getLocationLooked = true;
                try
                {
                    s_getLocation = AccessTools.MethodDelegate<Func<ZoneSystem, int, ZoneSystem.ZoneLocation>>(
                        AccessTools.Method(typeof(ZoneSystem), "GetLocation", new[] { typeof(int) }));
                }
                catch (Exception e)
                {
                    s_getLocation = null;
                    Plugin.Log.LogWarning("Far.SideReady: locations count by their pivot only (" + e.Message + ")");
                }
            }
            ZoneSystem.ZoneLocation l = s_getLocation != null && ZoneSystem.instance != null ? s_getLocation(ZoneSystem.instance, location) : null;
            r = l != null ? Mathf.Min(Mathf.Max(l.m_exteriorRadius, 0f), MaxObjectReach) : 0f;
            s_locationReach[location] = r;
            return r;
        }

        // Far.VisibleNear (P28): the areas whose heightmaps FarHeightmaps rebuilds at once, as the game does round the player:
        // a window onto them is live (P4 still waits for none of their rebuilds to be queued before a window goes live).
        void MarkNearMaps()
        {
            bool on = FarFlags.VisibleNear;
            foreach (Area a in _areas)
                // Only while a window onto it is live: before that nothing of it shows, and the rebuilds go within
                // Far.FarRegenBudget (at once they cost the frames of a portal's opening 20-45 ms).
                a.NearMaps = on && !a.Draining && a.LiveLink;
        }

        // Far.VisibleNear (FarHeightmaps.PlaceOf): the zone is one of such an area's near zones.
        internal bool ShowsZone(Vector2s zone)
        {
            foreach (Area a in _areas)
                if (a.NearMaps && Math.Max(Math.Abs(zone.x - a.Zone.x), Math.Abs(zone.y - a.Zone.y)) <= a.ZoneReach)
                    return true;
            return false;
        }

        // ---- Far.ArrivalPacer -----------------------------------------------------------------------
        // A jump of the reference position (a teleport, a portal walked through, the lab stepping back into a hall)
        // leaves the game a place to load around the player: it spawns the zones of its active area one every 0.1 s
        // (about 5 ms each) and makes the objects there only once all of them stand (ZNetScene.CreateObjectsSorted).
        // The first frame is the game's own: it unloads what it had around the old place (lab "drain back=yes", hall
        // 20: RemoveObjects over ~11k objects 32-34 ms, Unity carrying out those Object.Destroy calls in
        // Update.ScriptRunDelayedDynamicFrameRate 215-221 ms, the heightmaps their terrain modifiers poked in
        // MonoUpdaters.LateUpdate 73-75 ms with Far.FarRegenBudget, 172 without); the far areas made one area in it
        // (Far.LoadPacer). From there on the far loading ran next to the game's: 8 ms a frame by a portal, a far zone or
        // a heavy object as each frame's first step, windows going live as their cores stood.
        // From a jump until the game's active area stands (at most ArrivalMaxSeconds), and not under the loading screen
        // (nothing is seen there, and loading takes 30 ms a frame), the far loading keeps to the base budget: no step
        // past it, not even a frame's first (Fits), no far zone begins, one new area a frame (also without
        // Far.LoadPacer), one window onto a far area goes live a frame. And in the frame of the jump the nearest wanted
        // far portal gets its area at once, in Select, whoever asks first. With Far.LoadPacer only Update made areas:
        // when the game's removal pass ran first in that frame (on its tick, one in six), nothing kept the place a
        // portal walked through had just left (a crossing caught in Update, after this frame's listing): the pass
        // unloaded it, and the new area made it all again.
        // Off: none of it (jumps are still counted for the lab).

        const float JumpDistance = 200f;
        const float ArrivalMaxSeconds = 4f;

        Vector3 _refAt;
        bool _refKnown;
        // Since when the arrival lasts (-1: none), and whether its hold applies this frame (set by Select).
        float _arrivedAt = -1f;
        bool _arriving;
        // The frame the last jump was seen in, one an area was made at once in, one a window went live in while
        // arriving, one a far zone was held back in; when and how long Select took in the frame of the jump.
        int _jumpFrame = -1, _atOnceFrame = -1, _liveFrame = -1, _zoneHeldFrame = -1;
        long _jumpSelectBegan, _jumpSelectTicks;

        // Lab: jumps seen (with the switch off too), and Real Portal's own time in the frame one was seen (PortalSystem
        // .Update, plus Select when the game's removal pass ran it first; the last, the worst). With the switch:
        // arrivals' seconds in all, the longest, those ended by ArrivalMaxSeconds, frames held, frames a far zone was
        // held back in, window go-lives put off a frame, areas made at once.
        internal static int LabArrivals, LabArrivalTimeouts, LabArrivalAtOnce;
        internal static long LabArrivalFrames, LabArrivalZonesHeld, LabArrivalLiveHeld;
        internal static double LabArrivalSeconds, LabArrivalSecondsMax, LabArrivalFrameMs, LabArrivalFrameMsMax;

        // The same as one line (the lab's report, the verbose log).
        internal static string DescribeArrival() =>
            $"arrival: {LabArrivals} jumps seen, Real Portal's part of the frame {LabArrivalFrameMs:0.0} ms (worst {LabArrivalFrameMsMax:0.0}); " +
            $"held {LabArrivalSeconds:0.0} s in all (longest {LabArrivalSecondsMax:0.0} s, {LabArrivalTimeouts} at {ArrivalMaxSeconds:0} s), {LabArrivalFrames} frames, " +
            $"far zones held back in {LabArrivalZonesHeld}, {LabArrivalLiveHeld} window go-lives put off, {LabArrivalAtOnce} areas made at once";

        internal static bool LoadingScreen() =>
            Hud.instance != null && Hud.instance.m_loadingScreen != null && Hud.instance.m_loadingScreen.gameObject.activeInHierarchy;

        // From Select, once a frame: whether the reference position jumped since the last frame, and whether the hold
        // applies this frame (_arriving).
        bool Arrive()
        {
            _arriving = false;
            _crossed = false;
            ZNet net = ZNet.instance;
            if (net == null)
                return false;
            Vector3 at = net.GetReferencePosition();
            float moved = _refKnown ? Utils.DistanceXZ(at, _refAt) : 0f;
            bool jumped = moved > JumpDistance;
            // Far.CrossAtOnce: any crossing, however short (CrossDistance).
            _crossed = moved > CrossDistance;
            _refAt = at;
            _refKnown = true;
            float now = Time.time;
            if (jumped)
            {
                LabArrivals++;
                _jumpFrame = Time.frameCount;
                if (_arrivedAt >= 0f)
                    EndArrival(now, false, "another jump");
                if (FarFlags.ArrivalPacer)
                {
                    _arrivedAt = now;
                    Plugin.Debug($"Far.ArrivalPacer: f{Time.frameCount} the reference position jumped {moved:0} m; far loading keeps to its base budget " +
                                 $"until the game's active area there stands (at most {ArrivalMaxSeconds:0} s)");
                }
            }
            if (_arrivedAt < 0f)
                return jumped;
            bool late = now - _arrivedAt >= ArrivalMaxSeconds;
            if (!FarFlags.ArrivalPacer || late || ZoneSystem.instance == null || ZoneSystem.instance.IsActiveAreaLoaded())
                EndArrival(now, late, late ? "at the cap" : FarFlags.ArrivalPacer ? "the game's active area stands" : "switched off");
            else if (!LoadingScreen())
            {
                _arriving = true;
                LabArrivalFrames++;
            }
            return jumped;
        }

        void EndArrival(float now, bool late, string why)
        {
            double s = now - _arrivedAt;
            LabArrivalSeconds += s;
            if (s > LabArrivalSecondsMax)
                LabArrivalSecondsMax = s;
            if (late)
                LabArrivalTimeouts++;
            _arrivedAt = -1f;
            Plugin.Debug($"Far.ArrivalPacer: f{Time.frameCount} arrival over after {s:0.00} s ({why})");
        }

        // In the frame of a jump, with the switch on: this far portal is the nearest wanted, and gets its area now.
        bool AtOnce(ZDO far)
        {
            if (!FarFlags.ArrivalPacer || _wanted.Count == 0 || !ReferenceEquals(_wanted[0].zdo, far))
                return false;
            _atOnceFrame = Time.frameCount;
            LabArrivalAtOnce++;
            return true;
        }

        // ---- Far.CrossAtOnce (Docs\PARITY-PLAN.md P5, P22 terrain) --------------------------------------------------------
        // After a crossing the place just left had no area until a frame's Update made one (Far.LoadPacer; at once only after
        // a jump over 200 m with Far.ArrivalPacer): when the game's removal tick came first in that frame, it unloaded the
        // whole place, the portal just used with it, and the area made it all again over seconds. With the switch:
        //  - A crossing is any move of the reference position over CrossDistance in a frame. In its frame the nearest wanted
        //    far portal (the one just walked through from: the player stands by its partner) gets its area in Select,
        //    whichever of the removal pass and Update asks first (CrossNearest), whatever ArrivalPacer says; ArrivalPacer's
        //    own hold keeps its switch and its 200 m.
        //  - While the player stands within SpareReach of a live portal, a spare distant terrain is built round it, the place
        //    he is in (SpareStep, within the loading budget as an area's own); the area made at the crossing takes it
        //    (TakeSpare), so its window has its distant terrain from the first frame.
        //  - The arriving place's distant terrain (the area round the portal he came out at, built while its window was live)
        //    is shown to the game camera in place of the game's own until TerrainLod has rebuilt round him
        //    (FarTerrain.HandOff): a crossing over 1.2 km left the land past the loaded zones missing until then. It is not
        //    destroyed with its area's drain before that.
        // Off: as before. Main thread only; nothing allocated per frame.

        const float CrossDistance = 32f;
        // The spare is built within this distance of a live portal's opening, and dropped beyond SpareKeep; the arriving
        // place's area is the one whose far portal stands within ArriveReach of the player.
        const float SpareReach = 8f, SpareKeep = 12f, ArriveReach = 32f;

        bool _crossed;
        FarTerrain _spare;
        Vector3 _spareAt;

        // Lab: crossings seen with the switch on, and those after which the nearest wanted far portal (the place just left)
        // had its area in the crossing frame (the final test wants them equal); spare distant terrains built and taken by an
        // area made at a crossing.
        internal static long LabCrossings, LabCrossAtOnce;
        internal static int LabSparesBuilt, LabSparesTaken;

        // In the frame of a crossing: this far portal is the nearest wanted, and gets its area now (the frame's one new area).
        bool CrossNearest(ZDO far, bool jumped)
        {
            if (_wanted.Count == 0 || !ReferenceEquals(_wanted[0].zdo, far))
                return false;
            _atOnceFrame = Time.frameCount;
            if (jumped && FarFlags.ArrivalPacer)
                LabArrivalAtOnce++;
            return true;
        }

        // P5.2: the spare distant terrain, if it was built round the far portal at this place (the area made there takes it).
        FarTerrain TakeSpare(Vector3 center)
        {
            if (_spare == null || Utils.DistanceXZ(_spareAt, center) > 1f)
                return null;
            FarTerrain t = _spare;
            _spare = null;
            LabSparesTaken++;
            return t;
        }

        void DropSpare()
        {
            _spare?.Destroy();
            _spare = null;
        }

        // P5.2, once a frame after the areas' steps: by a live portal (within SpareReach of its opening; SpareKeep once its
        // spare is under way), a spare distant terrain is built round it, a mesh at a time within the loading budget, unless
        // an area round that portal has one already (another portal's far end). Dropped as the player walks away.
        void SpareStep(double budget, bool pacer, bool zoneBegun, ref bool built)
        {
            Player p = Player.m_localPlayer;
            if (!FarFlags.CrossAtOnce || p == null || ZNet.instance == null)
            {
                DropSpare();
                return;
            }
            // By the reference position (what Arrive and Select go by): PortalSystem.FixedUpdate may have moved the player
            // through already this frame, before Select sees the crossing and the area round the place he left takes the
            // spare built there.
            Vector3 me = ZNet.instance.GetReferencePosition();
            PortalLink by = null;
            float best = float.MaxValue;
            foreach (PortalLink link in PortalLink.All)
            {
                if (!link.IsActive || !link.FrameSet)
                    continue;
                float reach = _spare != null && Utils.DistanceXZ(_spareAt, link.transform.position) <= 1f ? SpareKeep : SpareReach;
                float d = (link.Frame.Center - me).sqrMagnitude;
                if (d < reach * reach && d < best)
                {
                    best = d;
                    by = link;
                }
            }
            // This portal's own ZDO is its partner's far end (IsActive: they lead to each other).
            ZDO own = by != null && by.Partner != null ? by.Partner.PartnerZdo : null;
            if (own == null || (_byPortal.TryGetValue(own.m_uid, out Area there) && !there.Draining && there.Terrain != null))
            {
                DropSpare();
                return;
            }
            Vector3 at = own.GetPosition();
            if (_spare != null && Utils.DistanceXZ(_spareAt, at) > 1f)
                DropSpare();
            if (_spare == null)
            {
                _spare = new FarTerrain(at);
                _spareAt = at;
                LabSparesBuilt++;
            }
            BuildTerrain(_spare, budget, pacer, zoneBegun, ref built);
        }

        // P22: right after a crossing, the arriving place's distant terrain (the area round the far portal the player came out
        // at) stands in for the game's own, if that is not round him already.
        void HandOffArrival()
        {
            ZNet net = ZNet.instance;
            if (net == null)
                return;
            Vector3 me = net.GetReferencePosition();
            Area there = null;
            float best = ArriveReach;
            foreach (Area a in _areas)
            {
                if (a.Draining || a.Terrain == null || !a.Terrain.Built)
                    continue;
                float d = Utils.DistanceXZ(a.Center, me);
                if (d < best)
                {
                    best = d;
                    there = a;
                }
            }
            if (there != null)
                FarTerrain.HandOff(there.Terrain, me);
        }

        // A far zone that would begin now waits while arriving (counted once a frame).
        bool ArrivalHoldsZone()
        {
            if (!_arriving)
                return false;
            if (_zoneHeldFrame != Time.frameCount)
            {
                _zoneHeldFrame = Time.frameCount;
                LabArrivalZonesHeld++;
            }
            return true;
        }

        // A window onto a far area whose core stands may go live: while arriving, only the first asking in a frame.
        bool GoLive()
        {
            if (!_arriving)
                return true;
            if (_liveFrame != Time.frameCount)
            {
                _liveFrame = Time.frameCount;
                return true;
            }
            LabArrivalLiveHeld++;
            return false;
        }

        // From PortalSystem.Update, at its end (lab): its time, when this is the frame a jump was seen in, plus Select's
        // when the game's removal pass ran it before.
        internal void ArrivalFrameDone(long began)
        {
            if (_jumpFrame != Time.frameCount)
                return;
            long ticks = Stopwatch.GetTimestamp() - began;
            if (_jumpSelectBegan < began)
                ticks += _jumpSelectTicks;
            double ms = ticks * 1000.0 / Stopwatch.Frequency;
            LabArrivalFrameMs = ms;
            if (ms > LabArrivalFrameMsMax)
                LabArrivalFrameMsMax = ms;
        }

        int LoadedZones(Area a)
        {
            int n = 0;
            foreach (Vector2s z in a.Zones)
                if (ZoneSystem.instance.IsZoneLoaded(z))
                    n++;
            return n;
        }

        // What Track keeps (by reference): its id then, when, and the area it came into.
        struct Tracked
        {
            internal ZDOID Id;
            internal float At;
            internal Area Area;
        }

        readonly Dictionary<ZDO, Tracked> _recent = new Dictionary<ZDO, Tracked>(ZdoRef.Instance);

        // Track keeps what it took this long (as before); Far.TrackUntilListed at most this long (a safety: a listing
        // comes within LoadedOverdue).
        const float TrackSeconds = 3f, TrackHoldMax = 30f;

        // Lab: new or arrived objects kept by an area, and those that fell in none.
        internal static int LabTracked, LabUntracked;
        internal bool LabIsRecent(ZDO z) => z != null && _recent.ContainsKey(z);

        // Lab (Far.TrackUntilListed): what Track kept past its 3 s from before at the last keep-alive pass, and the longest
        // it kept one (seconds).
        internal static int LabTrackHeld;
        internal static float LabTrackLongest;

        readonly List<Vector3> _areaPoints = new List<Vector3>();

        // The loaded objects of the far areas within reach of any of these points (only areas around them are looked
        // through). With `draining`, those of areas that drain as well (BaseView: still in the game camera's view until
        // the drain has destroyed them).
        internal void InstancesNear(List<Vector3> points, float reach, List<ZNetView> into, bool draining = false)
        {
            into.Clear();
            ZNetScene scene = ZNetScene.instance;
            if (scene == null)
                return;
            float r2 = reach * reach;
            foreach (Area a in _areas)
            {
                if (a.Draining && !draining)
                    continue;
                // Far.LinkScaling: an area's lively objects are held only against the points of windows onto it, not
                // against every window's (twenty tests an object with twenty shown). The same objects: what lies
                // within reach of a window's point lies in the near zones of that window's own area too.
                if (FarFlags.LinkScaling)
                {
                    _areaPoints.Clear();
                    foreach (Vector3 p in points)
                        if (Utils.DistanceXZ(p, a.Center) < reach + 200f)
                            _areaPoints.Add(p);
                    if (_areaPoints.Count == 0)
                        continue;
                    foreach (ZNetView v in a.Lively)
                    {
                        if (v == null)
                            continue;
                        Vector3 at = v.transform.position;
                        foreach (Vector3 p in _areaPoints)
                            if ((at - p).sqrMagnitude < r2)
                            {
                                into.Add(v);
                                break;
                            }
                    }
                    continue;
                }
                bool near = false;
                foreach (Vector3 p in points)
                    near |= Utils.DistanceXZ(p, a.Center) < reach + 200f;
                if (!near)
                    continue;
                // Only what plays effects at all (the area's lively objects).
                foreach (ZNetView v in a.Lively)
                {
                    if (v == null)
                        continue;
                    Vector3 at = v.transform.position;
                    bool close = false;
                    foreach (Vector3 p in points)
                        close |= (at - p).sqrMagnitude < r2;
                    if (close)
                        into.Add(v);
                }
            }
        }

        // ---- far areas out of sight -----------------------------------------------------------------

        // The lively objects of each loaded area, looked through 600 a frame and again every 30 s.
        // FarWorld (shim): with the engine's instance events live, what is made in an area or carried into one joins its
        // lively objects as it comes (TakeBorn), and the look through each area here, which still finds what walked in from
        // elsewhere, runs every ShimLivelyEvery instead.
        void ScanLively()
        {
            ZNetScene scene = ZNetScene.instance;
            float every = 30f;
            if (_shimReg != null && BornLive())
            {
                every = ShimLivelyEvery;
                TakeBorn(scene);
            }
            int left = 600;
            foreach (Area a in _areas)
            {
                if (a.Draining || !Stands(a) || Time.time - a.ScannedAt < every)
                    continue;
                int total = a.Near.Count + a.Distant.Count;
                while (left > 0 && a.ScanAt < total)
                {
                    ZDO z = a.ScanAt < a.Near.Count ? a.Near[a.ScanAt] : a.Distant[a.ScanAt - a.Near.Count];
                    a.ScanAt++;
                    left--;
                    ZNetView v = scene.FindInstance(z);
                    if (v != null && IsLively(v))
                        a.LivelyNext.Add(v);
                }
                if (a.ScanAt >= total)
                {
                    a.Lively.Clear();
                    a.Lively.AddRange(a.LivelyNext);
                    a.LivelyNext.Clear();
                    a.ScanAt = 0;
                    a.ScannedAt = Time.time;
                }
                if (left <= 0)
                    return;
            }
        }

        static bool IsLively(ZNetView v) =>
            v.GetComponentInChildren<ParticleSystem>(true) != null || v.GetComponent<Character>() != null
            || v.GetComponent<Fish>() != null || v.GetComponent<RandomFlyingBird>() != null;

        readonly Dictionary<ZDOID, List<ParticleSystem>> _paused = new Dictionary<ZDOID, List<ParticleSystem>>();
        readonly Dictionary<ZDOID, float> _watchedAt = new Dictionary<ZDOID, float>();
        float _sightCheckAt;

        // Once a second: the effects (smoke, fire, falling leaves) of a far area nobody can see stop where they are,
        // as the game stops what is out of its camera's view, and go on when its window is in sight again.
        void UpdateSight()
        {
            // Far.SectorSleep: by zone, ahead of the view, with catch-up (UpdateSightSector). The player's own zones are
            // always awake there, which the 200 m rule below stood in for.
            if (_sectorOn)
            {
                UpdateSightSector();
                return;
            }
            UpdateSightLists();
            // The awake set for those who ask (IsAwakeZone), here from the areas' own lists.
            EnsureAwakeSet();
        }

        void UpdateSightLists()
        {
            // Every frame: you are in a sleeping place yourself (just walked through): it wakes now.
            Player me = Player.m_localPlayer;
            if (me != null && _paused.Count > 0)
                foreach (Area a in _areas)
                    if (_paused.ContainsKey(a.Portal) && Utils.DistanceXZ(me.transform.position, a.Center) < 200f)
                    {
                        _watchedAt[a.Portal] = Time.time;
                        Wake(a);
                    }
            // Far.SleepBudget: each area on its own phase, turning a part at a time (UpdateSightSpread).
            if (FarFlags.SleepBudget)
            {
                UpdateSightSpread();
                return;
            }
            if (Time.time < _sightCheckAt)
                return;
            _sightCheckAt = Time.time + 1f;
            foreach (PortalLink link in PortalLink.All)
                if (link.PartnerZdo != null && link.Watched)
                {
                    _watchedAt[link.PartnerZdo.m_uid] = Time.time;
                    // FarWorld (shim): the areas sharing its place stay awake with it (WatchMates).
                    if (_shimReg != null && _byPortal.TryGetValue(link.PartnerZdo.m_uid, out Area w))
                        WatchMates(w, Time.time, false);
                }
            foreach (Area a in _areas)
            {
                if (a.Draining || !Stands(a))
                    continue;
                bool watched = _watchedAt.TryGetValue(a.Portal, out float at) && Time.time - at < 3f;
                bool paused = _paused.ContainsKey(a.Portal);
                if (watched && paused)
                    Wake(a);
                else if (!watched && !paused && a.ScannedAt > 0f)
                    Sleep(a);
            }
        }

        void Sleep(Area a)
        {
            a.Turn = 0;
            var list = new List<ParticleSystem>();
            var frozen = new List<GameObject>();
            // Far.ParticleWindowGate: the far side's particle systems are FarParticles' to pause and run.
            bool particles = !FarFlags.ParticleWindowGate;
            foreach (ZNetView v in a.Lively)
            {
                if (v == null)
                    continue;
                // FarWorld (shim): what walked out of it since it was found is left as it is (ShimHolds).
                if (_shimReg != null && !ShimHolds(a, v))
                    continue;
                if (particles)
                {
                    v.GetComponentsInChildren(false, s_systems);
                    foreach (ParticleSystem ps in s_systems)
                        if (ps.isPlaying)
                        {
                            ps.Pause(false);
                            list.Add(ps);
                        }
                }
                if (Freezable(v, a))
                {
                    v.gameObject.SetActive(false);
                    frozen.Add(v.gameObject);
                }
            }
            _paused[a.Portal] = list;
            _frozen[a.Portal] = frozen;
        }

        // What runs on its own and is not seen while nobody looks: creatures, fish and birds stand still until a
        // window looks there again, as the game leaves a place you walked away from. Not those after something
        // (alerted: they may be coming through), nor those right by the far portal.
        static bool Freezable(ZNetView v, Area a)
        {
            if (!v.gameObject.activeSelf || (v.transform.position - a.Center).sqrMagnitude < 15f * 15f)
                return false;
            if (v.TryGetComponent(out Character c))
            {
                if (c.IsPlayer() || c.IsTamed())
                    return false;
                BaseAI ai = c.GetBaseAI();
                return ai == null || !ai.IsAlerted();
            }
            return v.TryGetComponent(out Fish _) || v.TryGetComponent(out RandomFlyingBird _);
        }

        void Wake(Area a)
        {
            // Far.SectorSleep: every entry of its zones, whichever area put it to sleep.
            if (_sectorOn)
            {
                WakeZones(a);
                return;
            }
            a.Turn = 0;
            if (_frozen.TryGetValue(a.Portal, out List<GameObject> frozen))
            {
                foreach (GameObject g in frozen)
                    if (g != null && !g.activeSelf)
                        g.SetActive(true);
                _frozen.Remove(a.Portal);
            }
            if (!_paused.TryGetValue(a.Portal, out List<ParticleSystem> list))
                return;
            // Only what Sleep paused (none while Far.ParticleWindowGate is on: FarParticles runs them then; what was
            // paused here before it went on goes back as it was, for FarParticles to take from there).
            foreach (ParticleSystem ps in list)
                if (ps != null && ps.isPaused)
                    ps.Play(false);
            _paused.Remove(a.Portal);
        }

        // Right before a window renders the place behind this far portal: everything there is awake.
        internal void WakeFor(ZDO farPortal)
        {
            if (farPortal == null)
                return;
            SyncSleepMode();
            _watchedAt[farPortal.m_uid] = Time.time;
            // Far.SectorSleep: finished now right before a render (WakeForSector).
            if (_sectorOn)
            {
                WakeForSector(farPortal);
                return;
            }
            // FarWorld (shim): the areas sharing its place wake with it (WatchMates), within the same frame's time for
            // turning with SleepBudget.
            if (_shimReg != null && _byPortal.TryGetValue(farPortal.m_uid, out Area own) && !own.Draining && WatchMates(own, Time.time, true)
                && !_paused.ContainsKey(farPortal.m_uid))
            {
                if (FarFlags.SleepBudget)
                    TurnSome(null);
                return;
            }
            if (!_paused.ContainsKey(farPortal.m_uid) || !_byPortal.TryGetValue(farPortal.m_uid, out Area a))
                return;
            // Far.SleepBudget: within this frame's time for turning, this area first; the rest of it over the next
            // frames (a window drawing it meanwhile shows smoke standing still and creatures not back yet, which
            // WakeLead is there to spare).
            if (FarFlags.SleepBudget && !a.Draining)
            {
                a.Turn = -1;
                TurnSome(a);
            }
            else
                Wake(a);
        }

        // Far.ParticleWindowGate switched off: a system the gate held paused in a far area asleep here stays paused, in
        // that sleep's list (Sleep left the area's particles to the gate while it was on), and plays again when the area
        // wakes, as without the gate. False when the area is awake: the gate plays it.
        // Far.SectorSleep: into the registry, in the zone it stands in (false there too when that zone is awake), caught
        // up on waking from pausedAt (Time.time it was paused or last advanced; now when not given).
        internal bool AdoptPaused(ZDOID farPortal, ParticleSystem ps, float pausedAt = -1f)
        {
            SyncSleepMode();
            if (_sectorOn)
                return AdoptPausedSector(farPortal, ps, pausedAt);
            if (!_paused.TryGetValue(farPortal, out List<ParticleSystem> list))
                return false;
            list.Add(ps);
            return true;
        }

        // ---- Far.SleepBudget ------------------------------------------------------------------------

        readonly Stopwatch _turnClock = new Stopwatch();
        int _turnFrame = -1;
        double _turnMs;
        bool _turned;

        // Lab: lively objects turned (put to sleep or woken) a part at a time, and the most turning took in a frame.
        internal static long LabTurned;
        internal static double LabTurnMsMax;

        // The check above put every unwatched area to sleep in the same tick, and a window turning back into view woke
        // its whole area in the frame it was drawn (est. 20-50 ms either way with twenty areas). Here each area decides
        // on its own phase once a second, and falling asleep and waking go a lively object at a time within
        // SleepBudgetMs a frame, waking first. A window coming within WakeLead of the view starts its area waking, so
        // a turn of the head finds what lives there back before the window shows it.
        void UpdateSightSpread()
        {
            float now = Time.time;
            Camera cam = Utils.GetMainCamera();
            Vector3 eye = Vector3.zero, fwd = Vector3.forward;
            // As far as windows render (WindowReach.Render: Window.MaxDistance, or with Win.DistantLive the live range).
            float cos = 1f, reach = WindowReach.Render + 10f;
            if (cam != null)
            {
                eye = cam.transform.position;
                fwd = cam.transform.forward;
                float half = Mathf.Max(cam.fieldOfView, Camera.VerticalToHorizontalFieldOfView(cam.fieldOfView, cam.aspect)) * 0.5f + WakeLead;
                cos = Mathf.Cos(Mathf.Min(half, 179f) * Mathf.Deg2Rad);
            }
            // Every frame: what a window shows, or is about to, stays awake or starts waking now.
            foreach (PortalLink link in PortalLink.All)
            {
                ZDO far = link.PartnerZdo;
                if (far == null || !_byPortal.TryGetValue(far.m_uid, out Area a) || a.Draining)
                    continue;
                bool seen = link.Watched;
                if (!seen && cam != null && link.IsActive)
                {
                    Vector3 to = link.Frame.Center - eye;
                    float d = to.magnitude;
                    seen = d <= reach && (d < 5f || Vector3.Dot(to / d, fwd) > cos);
                }
                if (!seen)
                    continue;
                _watchedAt[a.Portal] = now;
                if (a.Turn >= 0 && _paused.ContainsKey(a.Portal))
                    a.Turn = -1;
                // FarWorld (shim): the areas sharing its place stay awake or start waking with it (WatchMates).
                if (_shimReg != null)
                    WatchMates(a, now, true);
            }
            // Falling asleep: each area at its own time of the second, so twenty do not turn in one frame.
            foreach (Area a in _areas)
            {
                if (a.Draining || !Stands(a))
                    continue;
                if (a.SightAt <= 0f)
                    a.SightAt = now + (a.Portal.GetHashCode() & 1023) / 1024f;
                if (now < a.SightAt)
                    continue;
                a.SightAt += 1f;
                if (a.SightAt <= now)
                    a.SightAt = now + 1f;
                if (_watchedAt.TryGetValue(a.Portal, out float at) && now - at < 3f)
                    continue;
                // Unseen for 3 s: asleep, or back to sleep should it have started waking meanwhile.
                if (a.Turn < 0 || (a.Turn == 0 && !_paused.ContainsKey(a.Portal) && a.ScannedAt > 0f))
                    StartSleep(a);
            }
            TurnSome(null);
        }

        void StartSleep(Area a)
        {
            a.Turn = 1;
            a.TurnAt = 0;
            // Far.SectorSleep: its zones leave the awake set (EnsureAwakeSet, before SleepStepSector runs); the registry
            // holds what it puts to sleep.
            if (_sectorOn)
            {
                a.Asleep = true;
                return;
            }
            // Asleep from now on as far as the rest is concerned (UpdateSight, WakeFor): the lists fill as it goes.
            if (!_paused.ContainsKey(a.Portal))
            {
                List<ParticleSystem> paused = a.PausedList ?? (a.PausedList = new List<ParticleSystem>());
                paused.Clear();
                _paused[a.Portal] = paused;
            }
            if (!_frozen.ContainsKey(a.Portal))
            {
                List<GameObject> frozen = a.FrozenList ?? (a.FrozenList = new List<GameObject>());
                frozen.Clear();
                _frozen[a.Portal] = frozen;
            }
        }

        // Areas turning, a lively object at a time within the frame's SleepBudgetMs (the first step of a frame always
        // runs): first (if given) the one a window is about to draw, then those waking, then those falling asleep.
        void TurnSome(Area first)
        {
            int frame = Time.frameCount;
            if (frame != _turnFrame)
            {
                _turnFrame = frame;
                _turnMs = 0.0;
                _turned = false;
            }
            // Far.SectorSleep: nothing falls asleep in a zone that just became awake (zones joining wake at once, outside
            // the frame's time for turning).
            if (_sectorOn)
                EnsureAwakeSet();
            _turnClock.Restart();
            bool more = first == null || TurnArea(first);
            for (int pass = 0; pass < 2 && more; pass++)
                foreach (Area a in _areas)
                    if (!a.Draining && (pass == 0 ? a.Turn < 0 : a.Turn > 0) && !TurnArea(a))
                    {
                        more = false;
                        break;
                    }
            _turnMs += _turnClock.Elapsed.TotalMilliseconds;
            if (_turnMs > LabTurnMsMax)
                LabTurnMsMax = _turnMs;
        }

        // False once the frame's time is used up.
        bool TurnArea(Area a)
        {
            while (a.Turn != 0)
            {
                if (_turned && _turnMs + _turnClock.Elapsed.TotalMilliseconds >= SleepBudgetMs)
                    return false;
                _turned = true;
                if (a.Turn < 0)
                {
                    if (_sectorOn)
                        WakeStepSector(a);
                    else
                        WakeStep(a);
                }
                else if (_sectorOn)
                    SleepStepSector(a);
                else
                    SleepStep(a);
            }
            return true;
        }

        void WakeStep(Area a)
        {
            // Creatures first: frozen they are not drawn at all, while paused smoke still shows.
            if (_frozen.TryGetValue(a.Portal, out List<GameObject> frozen) && frozen.Count > 0)
            {
                GameObject g = frozen[frozen.Count - 1];
                frozen.RemoveAt(frozen.Count - 1);
                if (g != null && !g.activeSelf)
                    g.SetActive(true);
                LabTurned++;
                return;
            }
            // Only what SleepStep paused (see Wake).
            if (_paused.TryGetValue(a.Portal, out List<ParticleSystem> paused) && paused.Count > 0)
            {
                ParticleSystem ps = paused[paused.Count - 1];
                paused.RemoveAt(paused.Count - 1);
                if (ps != null && ps.isPaused)
                    ps.Play(false);
                LabTurned++;
                return;
            }
            _frozen.Remove(a.Portal);
            _paused.Remove(a.Portal);
            a.Turn = 0;
        }

        void SleepStep(Area a)
        {
            if (a.TurnAt >= a.Lively.Count)
            {
                a.Turn = 0;
                return;
            }
            ZNetView v = a.Lively[a.TurnAt++];
            if (v == null)
                return;
            // FarWorld (shim): what walked out of it since it was found is left as it is (ShimHolds).
            if (_shimReg != null && !ShimHolds(a, v))
                return;
            LabTurned++;
            // Far.ParticleWindowGate: the far side's particle systems are FarParticles' to pause and run.
            if (!FarFlags.ParticleWindowGate && _paused.TryGetValue(a.Portal, out List<ParticleSystem> paused))
            {
                v.GetComponentsInChildren(false, s_systems);
                foreach (ParticleSystem ps in s_systems)
                    if (ps.isPlaying)
                    {
                        ps.Pause(false);
                        paused.Add(ps);
                    }
            }
            if (Freezable(v, a) && _frozen.TryGetValue(a.Portal, out List<GameObject> frozen))
            {
                v.gameObject.SetActive(false);
                frozen.Add(v.gameObject);
            }
        }

        readonly Dictionary<ZDOID, List<GameObject>> _frozen = new Dictionary<ZDOID, List<GameObject>>();
        static readonly List<ParticleSystem> s_systems = new List<ParticleSystem>();

        // ---- Far.SectorSleep (Docs\PARITY-PLAN.md P1) -------------------------------------------------------------
        // The sleep above goes by each area's own list of lively objects. Falling asleep, an area froze and paused what it
        // shares with a watched area or with the player's own surroundings (pairs 200-320 m apart: deer, fish and birds
        // vanished and his torches' smoke stopped 3 s after he looked away; the 200 m rule woke the whole area round him
        // instead); a window turning into view faster than its 1 ms a frame of waking showed creatures missing and smoke
        // standing for frames; and what woke started over (animators from their default state, a sleeping creature out of
        // its pose, smoke from where it stood). With the switch:
        //  - The awake set, by zone: the zones the game loads round the reference position (its near ring and one more; for
        //    distant objects its whole ring and one more), and those of every far area that does not drain and is not
        //    asleep (watched, waking, loading, or not yet unseen for 3 s): its near zones for everything, its ring for its
        //    distant objects, as a player standing at its far portal has them. Nothing falls asleep in it.
        //  - One registry of what sleeps, for all areas: each frozen object and paused system with the zone it fell asleep
        //    in, when, and whether it is a distant object, in buckets by zone. An area waking wakes every entry of its zones
        //    (of its ring: the distant objects'), whichever area put it to sleep. A zone joining the awake set otherwise (the
        //    player moved, an area made, taken back or done waking) wakes its entries at once: they are few.
        //  - Ahead of the view: a window about to show (WindowReach.AboutToShow: on screen, within the view widened by 45
        //    degrees and 0.3 s of the camera's turn rate, or within 30 m) starts its area waking within the frame's 1 ms;
        //    one on screen (WindowReach.OnScreen) whose area still holds anything asleep finishes waking now, whatever it
        //    costs, and so does WakeFor right before a render.
        //  - Caught up on waking: animators keep their state through the freeze (keepAnimatorStateOnDisable, put back as it
        //    was after), a MonsterAI is told again whether it sleeps (the line its Awake runs), and a paused system is
        //    advanced by the time it missed (at most 2 s, as FarParticles' gate) before it plays.
        //  - A character left running (a player, a tame, one alerted) keeps its effects playing: it may walk where
        //    something shows it.
        // Places no window shows sleep as before. Off: the per-area lists above, exactly as before (the awake set is still
        // kept for IsAwakeZone, from those lists). Main thread only; nothing allocated per frame once the lists grew.

        // As FarParticles' gate (CatchUpMax): a looping effect is in its steady state by then.
        const float SleepCatchUpMax = 2f;
        // The awake set's bits per zone: everything awake there, its distant objects awake; the same from areas waking
        // (theirs to wake within the frame's time, so not woken as they join); the bits before a rebuild sit in the high
        // nibble while it runs.
        const byte AwakeAll = 1, AwakeDistant = 2, WakingAll = 4, WakingDistant = 8;
        const byte AllAwake = AwakeAll | WakingAll, AnyAwake = AwakeAll | AwakeDistant | WakingAll | WakingDistant, NowAwake = AwakeAll | AwakeDistant;
        // WakeBucket's parts: frozen objects, paused systems.
        const int FrozenPart = 1, PausedPart = 2;

        // What sleeps of one registry entry: the zone it sleeps in (by ZoneSystem.SectorToIndex), Time.time it fell asleep,
        // whether it is a distant object (awake wherever distant objects are), for a paused system whether its object can
        // move (a body physics runs: an item, a cart, a ship), and for a frozen object the animators under it (in order,
        // the first 64) whose keepAnimatorStateOnDisable the freeze set.
        struct Sleeper
        {
            internal int Zone;
            internal float At;
            internal bool Distant, Mobile;
            internal ulong Animators;
        }

        // The registry's entries in one zone (keys of _frozenAt and _pausedAt; one woken or filed under another zone since
        // is skipped where it is met), the distant objects' apart (an area wakes those alone in its ring); its zone, and its
        // place in _sleeperZones.
        sealed class Sleepers
        {
            internal int Index, Slot;
            internal readonly List<GameObject> Frozen = new List<GameObject>(), FrozenDistant = new List<GameObject>();
            internal readonly List<ParticleSystem> Paused = new List<ParticleSystem>(), PausedDistant = new List<ParticleSystem>();
            internal bool Empty => Frozen.Count == 0 && FrozenDistant.Count == 0 && Paused.Count == 0 && PausedDistant.Count == 0;
        }

        // Unity objects by reference (a destroyed one is still found, to be dropped).
        sealed class ByRef<T> : IEqualityComparer<T> where T : class
        {
            internal static readonly ByRef<T> Instance = new ByRef<T>();
            public bool Equals(T x, T y) => ReferenceEquals(x, y);
            public int GetHashCode(T o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }

        // The sleep in use (FarFlags.SectorSleep as of the last SyncSleepMode).
        bool _sectorOn;
        readonly Dictionary<GameObject, Sleeper> _frozenAt = new Dictionary<GameObject, Sleeper>(ByRef<GameObject>.Instance);
        readonly Dictionary<ParticleSystem, Sleeper> _pausedAt = new Dictionary<ParticleSystem, Sleeper>(ByRef<ParticleSystem>.Instance);
        readonly Dictionary<int, Sleepers> _sleepers = new Dictionary<int, Sleepers>();
        readonly List<Sleepers> _sleeperZones = new List<Sleepers>();
        readonly Stack<Sleepers> _sleeperPool = new Stack<Sleepers>();
        int _sweepAt;
        // The awake set (per zone, by ZoneSystem.SectorToIndex), the zones marked in it (and those of the rebuild before),
        // and what it was built from: the areas' states (AwakeState), AreasVersion, the reference zone, both simulation
        // distances, and the sleep in use.
        byte[] _awakeLv;
        List<int> _awakeMarked = new List<int>(), _awakeMarkedOld = new List<int>();
        readonly List<byte> _awakeStates = new List<byte>();
        int _awakeAreas = -1;
        Vector2s _awakeZone;
        SimulationDistance _awakeSim, _awakeArea;
        bool _awakeSector;
        static readonly List<Behaviour> s_behaviours = new List<Behaviour>();

        // Lab: areas whose waking was finished at once for a window on screen or right before a render (P1.5), and the
        // longest of those (ms); entries woken as their zones joined the awake set; lively objects left awake by it as
        // their area fell asleep.
        internal static long LabSyncWakes, LabJoinWoken, LabSpared;
        internal static double LabSyncWakeMsMax;

        // Lab: what sleeps now (frozen objects, paused systems) and in how many zones.
        internal int LabFrozenNow => _frozenAt.Count;
        internal int LabPausedNow => _pausedAt.Count;
        internal int LabSleepZones => _sleeperZones.Count;

        // Lab (Docs\PARITY-PLAN.md §5.3): what the area round this far portal still holds asleep (the registry's entries of
        // its zones, of its ring the distant objects'); 0 without one. Must be 0 in a window's first rendered frame.
        internal int LabAsleepIn(ZDO farPortal)
        {
            if (farPortal == null || ZNet.instance == null || _sleepers.Count == 0 || !_byPortal.TryGetValue(farPortal.m_uid, out Area a))
                return 0;
            SimulationDistance sim = AreaDistance();
            int near = sim.NearSimulationDistance, r = sim.TotalSimulationDistance, n = (2 * r + 1) * (2 * r + 1), count = 0;
            for (int k = 0; k < n; k++)
            {
                RingOffset(k, out int dx, out int dy, out int ring);
                if (!_sleepers.TryGetValue(ZoneIndex(a.Zone.x + dx, a.Zone.y + dy), out Sleepers b))
                    continue;
                count += Held(_frozenAt, b.FrozenDistant, b.Index) + Held(_pausedAt, b.PausedDistant, b.Index);
                if (ring <= near)
                    count += Held(_frozenAt, b.Frozen, b.Index) + Held(_pausedAt, b.Paused, b.Index);
            }
            return count;
        }

        static int Held<T>(Dictionary<T, Sleeper> at, List<T> list, int zone) where T : UnityEngine.Object
        {
            int n = 0;
            foreach (T o in list)
                if (o != null && at.TryGetValue(o, out Sleeper s) && s.Zone == zone)
                    n++;
            return n;
        }

        // Lab and log: the sleep by zone in one line.
        internal string DescribeSleep() =>
            $"sector sleep: {(_sectorOn ? "on" : "off")}, {_frozenAt.Count} frozen and {_pausedAt.Count} paused in {_sleeperZones.Count} zones; " +
            $"areas woken at once for a window {LabSyncWakes} (longest {LabSyncWakeMsMax:0.00} ms), entries woken as their zones joined {LabJoinWoken}, " +
            $"objects left awake {LabSpared}";

        // Docs\PARITY-PLAN.md §4 A5 (for C and D): everything in this zone is awake now: the zones the game loads round the
        // reference position (its near ring and one more), or the near zones of a far area not asleep (watched, waking,
        // loading, or not yet unseen for 3 s). As of this frame's UpdateSight, with Far.SectorSleep off too.
        internal bool IsAwakeZone(Vector2s zone) => _awakeLv != null && (_awakeLv[ZoneIndex(zone.x, zone.y)] & AllAwake) != 0;

        static int ZoneIndex(int x, int y) => (int)ZoneSystem.SectorToIndex(x, y).Sector;
        static int ZoneIndex(Vector3 at) => (int)ZoneSystem.GetSectorIndex(at).Sector;

        // At every entry (Update, WakeFor, AdoptPaused): Far.SectorSleep switched (or not yet seen on): what the other way
        // put to sleep wakes now (a frame's work, once), and every area starts over awake.
        void SyncSleepMode()
        {
            bool on = FarFlags.SectorSleep;
            if (on == _sectorOn)
                return;
            if (_sectorOn)
                WakeAllSleepers();
            else
                foreach (Area a in _areas)
                    if (_paused.ContainsKey(a.Portal) || _frozen.ContainsKey(a.Portal))
                        Wake(a);
            _sectorOn = on;
            foreach (Area a in _areas)
            {
                a.Turn = 0;
                a.Asleep = false;
                a.WakeAt = 0;
                a.WakePhase = 0;
            }
        }

        // Every frame (UpdateSight): what a window shows or is about to starts waking, what nobody saw for 3 s falls asleep
        // (each area at its own time of the second), the awake set follows, what a window on screen would show asleep
        // wakes now, then the frame's time for turning (Far.SleepBudget; off: an area turns whole at once).
        void UpdateSightSector()
        {
            float now = Time.time;
            bool spread = FarFlags.SleepBudget;
            foreach (PortalLink link in PortalLink.All)
            {
                ZDO far = link.PartnerZdo;
                if (far == null || !_byPortal.TryGetValue(far.m_uid, out Area a) || a.Draining)
                    continue;
                // P1.4: shown, or about to be (the lead follows the camera's turn rate; any window within 30 m).
                if (!link.Watched && !(link.IsActive && WindowReach.AboutToShow(link)))
                    continue;
                _watchedAt[a.Portal] = now;
                if (a.Asleep)
                {
                    if (spread)
                        BeginWake(a);
                    else
                        WakeZones(a);
                }
            }
            foreach (Area a in _areas)
            {
                if (a.Draining || !Stands(a))
                    continue;
                if (a.SightAt <= 0f)
                    a.SightAt = now + (a.Portal.GetHashCode() & 1023) / 1024f;
                if (now < a.SightAt)
                    continue;
                a.SightAt += 1f;
                if (a.SightAt <= now)
                    a.SightAt = now + 1f;
                if (_watchedAt.TryGetValue(a.Portal, out float at) && now - at < 3f)
                    continue;
                // Unseen for 3 s: asleep, or back to sleep should it have started waking meanwhile.
                if (a.Turn < 0 || (a.Turn == 0 && !a.Asleep && a.ScannedAt > 0f))
                {
                    StartSleep(a);
                    // Far.SleepBudget off: all of it at once, its zones out of the awake set first.
                    if (!spread)
                    {
                        EnsureAwakeSet();
                        while (a.Turn > 0)
                            SleepStepSector(a);
                    }
                }
            }
            EnsureAwakeSet();
            // P1.5: a window on screen whose area still holds anything asleep: finished now, whatever it costs.
            foreach (PortalLink link in PortalLink.All)
            {
                ZDO far = link.PartnerZdo;
                if (far != null && link.IsActive && _byPortal.TryGetValue(far.m_uid, out Area a) && (a.Asleep || a.Turn < 0) && WindowReach.OnScreen(link))
                    SyncWake(a);
            }
            if (spread)
                TurnSome(null);
            SweepSleepers(now);
        }

        // Starts waking within the frame's time for turning (TurnSome: waking first), nearest its far portal first.
        void BeginWake(Area a)
        {
            if (a.Turn < 0)
                return;
            a.Turn = -1;
            a.Asleep = false;
            a.WakeAt = 0;
            a.WakePhase = 0;
        }

        // WakeFor: right before a render (a window's, or the lab's in-place reference with that window on screen) what its
        // far side holds asleep wakes now, whatever it costs. Asked otherwise (PortalSystem.WakeInSight: a window coming
        // into sight) it starts waking within the frame's time, as a window about to show does.
        void WakeForSector(ZDO farPortal)
        {
            if (!_byPortal.TryGetValue(farPortal.m_uid, out Area a) || !(a.Asleep || a.Turn < 0))
                return;
            if (WindowTextures.InWindow || PortalRenderer.InWindowRender || OnScreenOnto(farPortal))
                SyncWake(a);
            else if (FarFlags.SleepBudget && !a.Draining)
            {
                BeginWake(a);
                TurnSome(a);
            }
            else
                WakeZones(a);
        }

        // A window onto this far portal is on screen this frame (WindowReach.OnScreen).
        static bool OnScreenOnto(ZDO farPortal)
        {
            foreach (PortalLink link in PortalLink.All)
                if (link.PartnerZdo != null && link.PartnerZdo.m_uid == farPortal.m_uid && WindowReach.OnScreen(link))
                    return true;
            return false;
        }

        // P1.5: the area's waking finished now (counted, and timed for the lab).
        void SyncWake(Area a)
        {
            long t0 = Stopwatch.GetTimestamp();
            WakeZones(a);
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            LabSyncWakes++;
            if (ms > LabSyncWakeMsMax)
                LabSyncWakeMsMax = ms;
        }

        // Every entry of the area's zones woken at once (of its ring: the distant objects'), frozen ones first, whichever
        // area put them to sleep; the area is awake from now on.
        void WakeZones(Area a)
        {
            a.Turn = 0;
            a.Asleep = false;
            a.WakeAt = 0;
            a.WakePhase = 0;
            if (_sleepers.Count == 0 || ZNet.instance == null)
                return;
            SimulationDistance sim = AreaDistance();
            int near = sim.NearSimulationDistance, r = sim.TotalSimulationDistance, n = (2 * r + 1) * (2 * r + 1);
            float now = Time.time;
            for (int part = FrozenPart; part <= PausedPart; part++)
                for (int k = 0; k < n && _sleepers.Count > 0; k++)
                {
                    RingOffset(k, out int dx, out int dy, out int ring);
                    if (_sleepers.TryGetValue(ZoneIndex(a.Zone.x + dx, a.Zone.y + dy), out Sleepers b))
                        WakeBucket(b, ring <= near, part, now);
                }
        }

        // One entry of the area's zones woken (frozen objects in all of them first, then paused systems), nearest its far
        // portal first; awake once none is left.
        void WakeStepSector(Area a)
        {
            if (ZNet.instance != null)
            {
                SimulationDistance sim = AreaDistance();
                int near = sim.NearSimulationDistance, r = sim.TotalSimulationDistance, n = (2 * r + 1) * (2 * r + 1);
                float now = Time.time;
                while (a.WakePhase < 2 && _sleepers.Count > 0)
                {
                    if (a.WakeAt >= n)
                    {
                        a.WakePhase++;
                        a.WakeAt = 0;
                        continue;
                    }
                    RingOffset(a.WakeAt, out int dx, out int dy, out int ring);
                    if (_sleepers.TryGetValue(ZoneIndex(a.Zone.x + dx, a.Zone.y + dy), out Sleepers b) && WakeOne(b, ring <= near, a.WakePhase == 0, now))
                    {
                        LabTurned++;
                        return;
                    }
                    a.WakeAt++;
                }
            }
            a.Turn = 0;
            a.WakeAt = 0;
            a.WakePhase = 0;
        }

        // The k-th zone of a square in rings round its centre (0: the centre, then ring 1's eight, ring 2's sixteen, ...),
        // as offsets, and its ring (the Chebyshev distance).
        static void RingOffset(int k, out int dx, out int dy, out int ring)
        {
            int d = (int)((Math.Sqrt(k) + 1.0) * 0.5);
            while (d > 0 && (2 * d - 1) * (2 * d - 1) > k)
                d--;
            while ((2 * d + 1) * (2 * d + 1) <= k)
                d++;
            ring = d;
            if (d == 0)
            {
                dx = dy = 0;
                return;
            }
            int j = k - (2 * d - 1) * (2 * d - 1), row = 2 * d + 1;
            if (j < row)
            {
                dx = j - d;
                dy = -d;
            }
            else if ((j -= row) < row)
            {
                dx = j - d;
                dy = d;
            }
            else if ((j -= row) < row - 2)
            {
                dx = -d;
                dy = j - d + 1;
            }
            else
            {
                dx = d;
                dy = j - (row - 2) - d + 1;
            }
        }

        // A lively object of an area falling asleep: left as it is where the awake set holds it (its zone, for a distant
        // object its ring too); else its playing systems paused and, if Freezable, the object switched off, each filed in
        // the registry under the zone it stands in.
        void SleepStepSector(Area a)
        {
            if (a.TurnAt >= a.Lively.Count)
            {
                a.Turn = 0;
                return;
            }
            ZNetView v = a.Lively[a.TurnAt++];
            if (v == null)
                return;
            // FarWorld (shim): what walked out of it since it was found is left as it is (ShimHolds).
            if (_shimReg != null && !ShimHolds(a, v))
                return;
            LabTurned++;
            int zone = ZoneIndex(v.transform.position);
            ZDO zdo = v.GetZDO();
            bool distant = zdo != null && zdo.Distant;
            if (AwakeFor(zone, distant))
            {
                LabSpared++;
                return;
            }
            bool freeze = Freezable(v, a);
            float now = Time.time;
            // Far.ParticleWindowGate: the far side's particle systems are FarParticles' to pause and run. A character left
            // running keeps its effects playing.
            if (!FarFlags.ParticleWindowGate && (freeze || !v.TryGetComponent(out Character _)))
            {
                v.GetComponentsInChildren(false, s_systems);
                bool mobile = !freeze && Mobile(v);
                foreach (ParticleSystem ps in s_systems)
                    if (ps.isPlaying)
                    {
                        ps.Pause(false);
                        FilePaused(ps, zone, distant, mobile, now);
                    }
            }
            if (freeze)
            {
                GameObject g = v.gameObject;
                ulong animators = KeepAnimators(g);
                g.SetActive(false);
                // Frozen before and woken by something else meanwhile: what that freeze set is put back too.
                if (_frozenAt.TryGetValue(g, out Sleeper old))
                    animators |= old.Animators;
                _frozenAt[g] = new Sleeper { Zone = zone, At = now, Distant = distant, Animators = animators };
                Sleepers b = BucketOf(zone);
                (distant ? b.FrozenDistant : b.Frozen).Add(g);
            }
        }

        void FilePaused(ParticleSystem ps, int zone, bool distant, bool mobile, float at)
        {
            _pausedAt[ps] = new Sleeper { Zone = zone, At = at, Distant = distant, Mobile = mobile };
            Sleepers b = BucketOf(zone);
            (distant ? b.PausedDistant : b.Paused).Add(ps);
        }

        // Physics runs this object (not frozen: an item, a cart, a ship), so it may carry a paused system elsewhere.
        static bool Mobile(ZNetView v) => v.TryGetComponent(out Rigidbody body) && !body.isKinematic;

        // AdoptPaused: a system FarParticles' gate held paused in an area asleep (or falling asleep) joins the registry
        // under the zone its object stands in, unless that zone is awake (the gate plays it then).
        bool AdoptPausedSector(ZDOID farPortal, ParticleSystem ps, float pausedAt)
        {
            if (ps == null || !_byPortal.TryGetValue(farPortal, out Area a) || !a.Asleep)
                return false;
            ZNetView v = ps.GetComponentInParent<ZNetView>(true);
            ZDO zdo = v != null ? v.GetZDO() : null;
            bool distant = zdo != null && zdo.Distant;
            int zone = ZoneIndex((v != null ? v.transform : ps.transform).position);
            if (AwakeFor(zone, distant))
                return false;
            float now = Time.time;
            FilePaused(ps, zone, distant, v != null && Mobile(v), pausedAt >= 0f && pausedAt <= now ? pausedAt : now);
            return true;
        }

        Sleepers BucketOf(int zone)
        {
            if (_sleepers.TryGetValue(zone, out Sleepers b))
                return b;
            b = _sleeperPool.Count > 0 ? _sleeperPool.Pop() : new Sleepers();
            b.Index = zone;
            b.Slot = _sleeperZones.Count;
            _sleeperZones.Add(b);
            _sleepers[zone] = b;
            return b;
        }

        // An empty bucket back to the pool (the last of _sleeperZones takes its place).
        void ReleaseBucket(Sleepers b)
        {
            _sleepers.Remove(b.Index);
            int last = _sleeperZones.Count - 1;
            Sleepers moved = _sleeperZones[last];
            _sleeperZones[b.Slot] = moved;
            moved.Slot = b.Slot;
            _sleeperZones.RemoveAt(last);
            b.Frozen.Clear();
            b.FrozenDistant.Clear();
            b.Paused.Clear();
            b.PausedDistant.Clear();
            _sleeperPool.Push(b);
        }

        // The zone's entries woken (all: the others too, not only the distant objects'), of the parts asked; how many. The
        // bucket goes once empty.
        int WakeBucket(Sleepers b, bool all, int parts, float now)
        {
            int n = 0;
            if ((parts & FrozenPart) != 0)
            {
                while (PopFrozen(b.FrozenDistant, b.Index))
                    n++;
                if (all)
                    while (PopFrozen(b.Frozen, b.Index))
                        n++;
            }
            if ((parts & PausedPart) != 0)
            {
                while (PopPaused(b.PausedDistant, b.Index, now))
                    n++;
                if (all)
                    while (PopPaused(b.Paused, b.Index, now))
                        n++;
            }
            if (b.Empty)
                ReleaseBucket(b);
            return n;
        }

        // One of the zone's entries woken (a frozen object, or a paused system); false when none is left there.
        bool WakeOne(Sleepers b, bool all, bool frozen, float now)
        {
            bool woke = frozen
                ? PopFrozen(b.FrozenDistant, b.Index) || (all && PopFrozen(b.Frozen, b.Index))
                : PopPaused(b.PausedDistant, b.Index, now) || (all && PopPaused(b.Paused, b.Index, now));
            if (!woke && b.Empty)
                ReleaseBucket(b);
            return woke;
        }

        bool PopFrozen(List<GameObject> list, int zone)
        {
            while (list.Count > 0)
            {
                int last = list.Count - 1;
                GameObject g = list[last];
                list.RemoveAt(last);
                if (!_frozenAt.TryGetValue(g, out Sleeper s) || s.Zone != zone)
                    continue;
                _frozenAt.Remove(g);
                WakeFrozen(g, s);
                return true;
            }
            return false;
        }

        bool PopPaused(List<ParticleSystem> list, int zone, float now)
        {
            while (list.Count > 0)
            {
                int last = list.Count - 1;
                ParticleSystem ps = list[last];
                list.RemoveAt(last);
                if (!_pausedAt.TryGetValue(ps, out Sleeper s) || s.Zone != zone)
                    continue;
                _pausedAt.Remove(ps);
                WakePaused(ps, s, now);
                return true;
            }
            return false;
        }

        // A frozen object back on (not one the game is taking out: its view lost its ZDO), its animators' setting as it
        // was after (their state kept through it), and a MonsterAI told again whether it sleeps (MonsterAI.Awake's line).
        static void WakeFrozen(GameObject g, Sleeper s)
        {
            if (g == null || !g.TryGetComponent(out ZNetView v) || v.GetZDO() == null)
                return;
            if (!g.activeSelf)
                g.SetActive(true);
            RestoreAnimators(g, s.Animators);
            EnsureSleepAccess();
            if (s_aiAnimator == null || !g.TryGetComponent(out MonsterAI ai))
                return;
            try
            {
                ZSyncAnimation anim = s_aiAnimator(ai);
                if (anim != null)
                    anim.SetBool(s_sleepingHash, ai.IsSleeping());
            }
            catch (Exception e)
            {
                if (s_sleepPoseErrors++ < 3)
                    Plugin.Log.LogWarning("Far.SectorSleep: a woken creature's sleeping pose: " + e.Message);
            }
        }

        // A paused system advanced by the time it missed (at most SleepCatchUpMax; not while FarParticles' gate runs the far
        // side's systems) and played: what it shows stands as if it had run on.
        static void WakePaused(ParticleSystem ps, Sleeper s, float now)
        {
            if (ps == null || !ps.isPaused)
                return;
            if (!FarFlags.ParticleWindowGate && now > s.At && ps.gameObject.activeInHierarchy)
                ps.Simulate(Mathf.Min(now - s.At, SleepCatchUpMax), false, false, false);
            ps.Play(false);
        }

        // One zone of the registry a frame (round them all): entries gone with their object dropped, those in a zone that
        // is awake now woken (a backstop: zones joining wake theirs as they join), and a paused system on something that
        // moved (a ship, a cart) filed under the zone it stands in now, or woken there.
        void SweepSleepers(float now)
        {
            if (_sleeperZones.Count == 0)
                return;
            if (_sweepAt >= _sleeperZones.Count)
                _sweepAt = 0;
            Sleepers b = _sleeperZones[_sweepAt];
            int lv = _awakeLv != null ? _awakeLv[b.Index] & 0x0F : 0;
            bool all = (lv & AwakeAll) != 0, any = (lv & NowAwake) != 0;
            SweepFrozen(b.FrozenDistant, b.Index, any);
            SweepFrozen(b.Frozen, b.Index, all);
            SweepPaused(b.PausedDistant, b.Index, true, any, now);
            SweepPaused(b.Paused, b.Index, false, all, now);
            if (b.Empty)
                ReleaseBucket(b);
            else
                _sweepAt++;
        }

        void SweepFrozen(List<GameObject> list, int zone, bool wake)
        {
            int kept = 0;
            for (int i = 0; i < list.Count; i++)
            {
                GameObject g = list[i];
                if (!_frozenAt.TryGetValue(g, out Sleeper s) || s.Zone != zone)
                    continue;
                if (g == null || wake)
                {
                    _frozenAt.Remove(g);
                    WakeFrozen(g, s);
                    continue;
                }
                list[kept++] = g;
            }
            list.RemoveRange(kept, list.Count - kept);
        }

        void SweepPaused(List<ParticleSystem> list, int zone, bool distant, bool wake, float now)
        {
            int kept = 0;
            for (int i = 0; i < list.Count; i++)
            {
                ParticleSystem ps = list[i];
                if (!_pausedAt.TryGetValue(ps, out Sleeper s) || s.Zone != zone)
                    continue;
                if (ps == null)
                {
                    _pausedAt.Remove(ps);
                    continue;
                }
                // Where its object stands now (as it was filed: by the view it belongs to), for one that can move.
                int at = zone;
                if (!wake && s.Mobile)
                {
                    ZNetView owner = ps.GetComponentInParent<ZNetView>(true);
                    at = ZoneIndex(owner != null ? owner.transform.position : ps.transform.position);
                }
                if (wake || (at != zone && AwakeFor(at, distant)))
                {
                    _pausedAt.Remove(ps);
                    WakePaused(ps, s, now);
                    continue;
                }
                if (at != zone)
                {
                    s.Zone = at;
                    _pausedAt[ps] = s;
                    Sleepers to = BucketOf(at);
                    (distant ? to.PausedDistant : to.Paused).Add(ps);
                    continue;
                }
                list[kept++] = ps;
            }
            list.RemoveRange(kept, list.Count - kept);
        }

        // Everything in the registry woken (what is gone skipped) and forgotten.
        void WakeAllSleepers()
        {
            float now = Time.time;
            while (_sleeperZones.Count > 0)
                WakeBucket(_sleeperZones[_sleeperZones.Count - 1], true, FrozenPart | PausedPart, now);
            _frozenAt.Clear();
            _pausedAt.Clear();
            _sweepAt = 0;
        }

        // Clear: the registry woken and forgotten, and the awake set started over.
        void ClearSleepers()
        {
            WakeAllSleepers();
            if (_awakeLv != null)
            {
                foreach (int i in _awakeMarked)
                    _awakeLv[i] = 0;
                foreach (int i in _awakeMarkedOld)
                    _awakeLv[i] = 0;
            }
            _awakeMarked.Clear();
            _awakeMarkedOld.Clear();
            _awakeStates.Clear();
            _awakeAreas = -1;
        }

        // Whether an object in this zone (a distant one or not) is awake there: nothing falls asleep where it is.
        bool AwakeFor(int zone, bool distant) => _awakeLv != null && (_awakeLv[zone] & (distant ? AnyAwake : AllAwake)) != 0;

        // An area for the awake set: 0 draining, 1 asleep or falling asleep, 2 waking, 3 awake (by the sleep in use).
        byte AwakeState(Area a)
        {
            if (a.Draining)
                return 0;
            if (a.Turn < 0)
                return 2;
            return (_sectorOn ? a.Asleep : _paused.ContainsKey(a.Portal)) ? (byte)1 : (byte)3;
        }

        // The awake set as the reference zone, the simulation distances and the areas' states stand now, rebuilt only when
        // one of them changed. With Far.SectorSleep, a zone joining it other than through an area waking wakes its entries
        // (everything, or the distant objects' where only those joined).
        void EnsureAwakeSet()
        {
            ZNet net = ZNet.instance;
            if (net == null || ZoneSystem.instance == null)
                return;
            Vector2s me = ZoneSystem.GetZone(net.GetReferencePosition());
            SimulationDistance sim = net.GetSyncedSimulationDistance(), area = AreaDistance();
            bool changed = _awakeLv == null || _awakeAreas != _areasVersion || me != _awakeZone || !sim.Equals(_awakeSim) || !area.Equals(_awakeArea)
                           || _awakeSector != _sectorOn || _awakeStates.Count != _areas.Count;
            if (_awakeStates.Count > _areas.Count)
                _awakeStates.RemoveRange(_areas.Count, _awakeStates.Count - _areas.Count);
            for (int i = 0; i < _areas.Count; i++)
            {
                byte s = AwakeState(_areas[i]);
                if (i == _awakeStates.Count)
                    _awakeStates.Add(s);
                else if (_awakeStates[i] != s)
                    _awakeStates[i] = s;
                else
                    continue;
                changed = true;
            }
            if (!changed)
                return;
            _awakeAreas = _areasVersion;
            _awakeZone = me;
            _awakeSim = sim;
            _awakeArea = area;
            _awakeSector = _sectorOn;
            if (_awakeLv == null)
                _awakeLv = new byte[SectorCount];
            // What it was goes to the high nibble while the new one is marked.
            foreach (int i in _awakeMarked)
                _awakeLv[i] = (byte)(_awakeLv[i] << 4);
            List<int> before = _awakeMarked;
            _awakeMarked = _awakeMarkedOld;
            _awakeMarkedOld = before;
            _awakeMarked.Clear();
            MarkAwake(me, sim.NearSimulationDistance + 1, AwakeAll);
            MarkAwake(me, sim.TotalSimulationDistance + 1, AwakeDistant);
            for (int i = 0; i < _areas.Count; i++)
            {
                byte s = _awakeStates[i];
                if (s < 2)
                    continue;
                Vector2s z = _areas[i].Zone;
                MarkAwake(z, area.NearSimulationDistance, s == 3 ? AwakeAll : WakingAll);
                MarkAwake(z, area.TotalSimulationDistance, s == 3 ? AwakeDistant : WakingDistant);
            }
            if (_sectorOn && _sleepers.Count > 0)
            {
                float now = Time.time;
                foreach (int i in _awakeMarked)
                {
                    int v = _awakeLv[i], was = v >> 4;
                    bool all = (v & AwakeAll) != 0 && (was & AwakeAll) == 0;
                    bool any = (v & NowAwake) != 0 && (was & NowAwake) == 0;
                    if ((all || any) && _sleepers.TryGetValue(i, out Sleepers b))
                        LabJoinWoken += WakeBucket(b, all, FrozenPart | PausedPart, now);
                }
            }
            foreach (int i in before)
                _awakeLv[i] &= 0x0F;
        }

        // A square of zones round c marked with this bit.
        void MarkAwake(Vector2s c, int r, byte bit)
        {
            for (int y = c.y - r; y <= c.y + r; y++)
                for (int x = c.x - r; x <= c.x + r; x++)
                {
                    int i = ZoneIndex(x, y);
                    byte v = _awakeLv[i];
                    if ((v & 0x0F) == 0)
                        _awakeMarked.Add(i);
                    _awakeLv[i] = (byte)(v | bit);
                }
        }

        // ---- animators through a freeze

        // Made once, on first use: an Animator's keepAnimatorStateOnDisable (UnityEngine.AnimationModule, which the plugin
        // does not reference) read and set through methods over Behaviour, and BaseAI's ZSyncAnimation. A part missing is
        // logged once and left out: the rest runs as without it.
        static Type s_animatorType;
        static Func<Behaviour, bool> s_keepGet;
        static Action<Behaviour, bool> s_keepSet;
        static AccessTools.FieldRef<BaseAI, ZSyncAnimation> s_aiAnimator;
        static int s_sleepingHash, s_sleepPoseErrors;
        static bool s_sleepLooked;

        static void EnsureSleepAccess()
        {
            if (s_sleepLooked)
                return;
            s_sleepLooked = true;
            try
            {
                System.Reflection.FieldInfo field = AccessTools.Field(typeof(Character), "m_animator");
                Type t = field?.FieldType;
                System.Reflection.PropertyInfo p = t == null ? null
                    : AccessTools.Property(t, "keepAnimatorStateOnDisable") ?? AccessTools.Property(t, "keepAnimatorControllerStateOnDisable");
                System.Reflection.MethodInfo get = p?.GetGetMethod(), set = p?.GetSetMethod();
                if (get == null || set == null)
                    throw new MissingMemberException("Animator", "keepAnimatorStateOnDisable");
                var getter = new DynamicMethod("RealPortal_AnimatorKeepGet", typeof(bool), new[] { typeof(Behaviour) }, typeof(RemoteAreas).Module, true);
                ILGenerator il = getter.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Castclass, t);
                il.Emit(OpCodes.Callvirt, get);
                il.Emit(OpCodes.Ret);
                var setter = new DynamicMethod("RealPortal_AnimatorKeepSet", typeof(void), new[] { typeof(Behaviour), typeof(bool) }, typeof(RemoteAreas).Module, true);
                il = setter.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Castclass, t);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Callvirt, set);
                il.Emit(OpCodes.Ret);
                s_keepGet = (Func<Behaviour, bool>)getter.CreateDelegate(typeof(Func<Behaviour, bool>));
                s_keepSet = (Action<Behaviour, bool>)setter.CreateDelegate(typeof(Action<Behaviour, bool>));
                s_animatorType = t;
            }
            catch (Exception e)
            {
                s_keepGet = null;
                s_keepSet = null;
                s_animatorType = null;
                Plugin.Log.LogWarning("Far.SectorSleep: animators restart after a freeze (" + e.Message + ")");
            }
            try
            {
                s_aiAnimator = AccessTools.FieldRefAccess<BaseAI, ZSyncAnimation>("m_animator");
                s_sleepingHash = ZSyncAnimation.GetHash("sleeping");
            }
            catch (Exception e)
            {
                s_aiAnimator = null;
                Plugin.Log.LogWarning("Far.SectorSleep: a woken creature is not told again whether it sleeps (" + e.Message + ")");
            }
        }

        // Before a freeze: every animator under the object keeps its state while it is off. Which of them (in order, the
        // first 64) had not, for RestoreAnimators.
        static ulong KeepAnimators(GameObject g)
        {
            EnsureSleepAccess();
            if (s_keepSet == null)
                return 0UL;
            g.GetComponentsInChildren(false, s_behaviours);
            ulong set = 0UL;
            int n = 0;
            foreach (Behaviour b in s_behaviours)
            {
                if (b == null || b.GetType() != s_animatorType)
                    continue;
                if (n < 64 && !s_keepGet(b))
                {
                    s_keepSet(b, true);
                    set |= 1UL << n;
                }
                n++;
            }
            s_behaviours.Clear();
            return set;
        }

        // After waking (on again, with the same children as when it froze): those animators' setting as it was.
        static void RestoreAnimators(GameObject g, ulong set)
        {
            if (set == 0UL || s_keepSet == null)
                return;
            g.GetComponentsInChildren(false, s_behaviours);
            int n = 0;
            foreach (Behaviour b in s_behaviours)
            {
                if (b == null || b.GetType() != s_animatorType)
                    continue;
                if (n < 64 && (set & (1UL << n)) != 0)
                    s_keepSet(b, false);
                n++;
            }
            s_behaviours.Clear();
        }

        // Lab: the loaded objects of the far areas.
        internal List<GameObject> LabFarObjects()
        {
            var list = new List<GameObject>();
            foreach (Area a in _areas)
            {
                if (a.Draining)
                    continue;
                foreach (List<ZDO> zdos in new[] { a.Near, a.Distant })
                    foreach (ZDO z in zdos)
                    {
                        ZNetView v = ZNetScene.instance.FindInstance(z);
                        if (v != null)
                            list.Add(v.gameObject);
                    }
            }
            return list;
        }

        // Lab: every loaded object of the far areas switched off or on (what they cost by just being there).
        internal int LabSetActive(bool on)
        {
            int n = 0;
            foreach (Area a in _areas)
            {
                if (a.Draining)
                    continue;
                foreach (List<ZDO> list in new[] { a.Near, a.Distant })
                    foreach (ZDO z in list)
                    {
                        ZNetView v = ZNetScene.instance.FindInstance(z);
                        if (v != null && v.gameObject.activeSelf != on)
                        {
                            v.gameObject.SetActive(on);
                            n++;
                        }
                    }
            }
            return n;
        }

        // ZDOs by reference: the game recycles ZDO objects (a destroyed one comes back with a new id, and its hash
        // with it), which would break any set or map holding it by its id.
        sealed class ZdoRef : IEqualityComparer<ZDO>
        {
            internal static readonly ZdoRef Instance = new ZdoRef();
            public bool Equals(ZDO x, ZDO y) => ReferenceEquals(x, y);
            public int GetHashCode(ZDO z) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(z);
        }
        internal string LabListed(ZDO z)
        {
            if (z == null)
                return "-";
            var sb = new System.Text.StringBuilder();
            foreach (Area a in _areas)
                sb.Append($"[{a.Portal} {(a.Draining ? "draining" : "")} near {a.Near.Contains(z)} distant {a.Distant.Contains(z)} listed {Time.time - a.ListedAt:0.0} s ago, sector {z.GetSector().x},{z.GetSector().y} vs {a.Zone.x},{a.Zone.y}] ");
            return sb.ToString();
        }
        readonly List<ZDO> _recentDrop = new List<ZDO>();

        // An object that has just come into being in an area (a creature spawned, an item dropped) or come into one
        // (a creature or an arrow through a portal): kept from then on, not only from the next relist.
        internal void Track(ZDO z)
        {
            if (z == null || _areas.Count == 0)
                return;
            // Far.ClaimLikeGame: looked at by the next ClaimStep (ours or taken, if an area's active area holds it).
            if (FarFlags.ClaimLikeGame)
                _claimNew.Add((z, z.m_uid));
            // FarWorld (shim): an area on the registry is kept by its sectors as they are now (KeepAlive, KeepAliveFast), so
            // nothing is held here until its next listing. What is carried into one through a portal (it has its instance
            // already; a new ZDO's comes through the engine's instance event) is looked at for its lively objects.
            if (_shimReg != null)
            {
                ZNetScene scene = ZNetScene.instance;
                if (_bornHooked && scene != null && scene.HaveInstance(z))
                    Enqueue(z, Time.time);
                return;
            }
            Vector2s zone = ZoneSystem.GetZone(z.GetPosition());
            foreach (Area a in _areas)
                if (!a.Draining && a.Zones.Contains(zone))
                {
                    _recent[z] = new Tracked { Id = z.m_uid, At = Time.time, Area = a };
                    LabTracked++;
                    return;
                }
            LabUntracked++;
        }

        // Still kept as just made or come in (the full keep-alive pass, both faster ones and their checks alike): for
        // TrackSeconds, as before. With Far.TrackUntilListed also after that, until the area it came into is listed again
        // (that listing holds it, if it is still there), drains or goes, TrackHoldMax at most: a loaded area is listed every
        // 5-10 s, and in between the game's removal destroyed it, to be made again at the listing (fresh AI and animation;
        // lost if not saved, a ragdoll with its loot). Not one the game handed out again under another id.
        static bool TrackKeeps(ZDO z, in Tracked t, float now)
        {
            if (!z.IsValid() || z.m_uid != t.Id)
                return false;
            float age = now - t.At;
            if (age <= TrackSeconds)
                return true;
            Area a = t.Area;
            return FarFlags.TrackUntilListed && age <= TrackHoldMax && a != null && !a.Draining && !a.Gone && !(a.ListedAt > t.At);
        }

        // What Track holds, let go of where TrackKeeps says so; what stays goes into `into` (the full pass) or carries the
        // mark (Far.KeepAliveFast). How many stayed.
        int SweepRecent(List<ZDO> into, bool earmark, byte mark)
        {
            if (_recent.Count == 0)
            {
                LabTrackHeld = 0;
                return 0;
            }
            float now = Time.time;
            int kept = 0, held = 0;
            _recentDrop.Clear();
            foreach (var kv in _recent)
            {
                if (!TrackKeeps(kv.Key, kv.Value, now))
                {
                    _recentDrop.Add(kv.Key);
                    continue;
                }
                kept++;
                float age = now - kv.Value.At;
                if (age > TrackSeconds)
                {
                    held++;
                    if (age > LabTrackLongest)
                        LabTrackLongest = age;
                }
                into?.Add(kv.Key);
                if (earmark)
                    kv.Key.TempRemoveEarmark = mark;
            }
            foreach (ZDO z in _recentDrop)
                _recent.Remove(z);
            LabTrackHeld = held;
            return kept;
        }

        // From the ZNetScene.RemoveObjects prefix: the game destroys every object not in these lists, so the areas'
        // objects go in too. A released area gives its objects up a few at a time instead of all in one frame.
        internal void KeepAlive(List<ZDO> near)
        {
            // Far.KeepAliveSlice ran before (switched off, or an area drains): its snapshot of every loaded object goes.
            if (_snap.Count > 0)
                DropSnapshot();
            // Far.KeepAliveFast ran before (switched off, the last area gone, or it declined): its counts go.
            if (_fast)
                DropFast();
            if (_areas.Count == 0 && PortalLink.All.Count == 0)
                return;
            // FarWorld (shim): on or off the registry first (this may come before Update in a frame).
            ShimSync();
            Select();
            // The game hands destroyed ZDOs out again as new ones: one whose id changed is not ours any more (TrackKeeps).
            SweepRecent(near, false, 0);
            bool calm = FarFlags.DrainCalm;
            if (calm)
                DrainCalmly();
            SectorRegistry reg = _shimReg;
            bool bySector = false;
            foreach (Area a in _areas)
            {
                if (a.Draining)
                {
                    if (a.Keep == null)
                        continue;
                    if (!calm)
                        a.Keep.RemoveRange(Math.Max(0, a.Keep.Count - DrainPerTick), Math.Min(DrainPerTick, a.Keep.Count));
                    near.AddRange(a.Keep);
                    continue;
                }
                // FarWorld (shim): an area on the registry is kept by its sectors as they are now, below, each ZDO once
                // however many areas reach it (what came since its listing too, so nothing waits on Track).
                if (reg != null && OnRegistry(a, reg))
                {
                    bySector = true;
                    continue;
                }
                near.AddRange(a.Near);
                near.AddRange(a.Distant);
            }
            if (bySector)
                KeepBySector(reg, near);
        }

        readonly List<Area> _drainOrder = new List<Area>();
        readonly List<float> _drainDistance = new List<float>();

        // Far.DrainCalm: one budget a call for all draining areas instead of DrainPerTick each, the area farthest from
        // the player first: those near it (where you may turn back to) go last.
        void DrainCalmly()
        {
            _drainOrder.Clear();
            _drainDistance.Clear();
            Vector3 me = ZNet.instance != null ? ZNet.instance.GetReferencePosition() : Vector3.zero;
            foreach (Area a in _areas)
            {
                if (!a.Draining || a.Keep == null || a.Keep.Count == 0)
                    continue;
                // Farthest first, by insertion (a handful of areas; nothing allocated).
                float d = Utils.DistanceXZ(me, a.Center);
                int i = _drainOrder.Count;
                while (i > 0 && _drainDistance[i - 1] < d)
                    i--;
                _drainOrder.Insert(i, a);
                _drainDistance.Insert(i, d);
            }
            // DrainPerTick for each draining area up to the cap: a single one drains as it did without the flag.
            int left = Math.Min(DrainCalmPerTick, DrainPerTick * _drainOrder.Count);
            foreach (Area a in _drainOrder)
            {
                int n = Math.Min(left, a.Keep.Count);
                // Far.KeepAliveFast: what leaves Keep no longer counts.
                if (_fast)
                    UnholdRange(a.Keep, a.Keep.Count - n, n);
                a.Keep.RemoveRange(a.Keep.Count - n, n);
                left -= n;
                if (left == 0)
                    break;
            }
        }

        // Far.DrainCalm, from the TerrainModifier.PokeHeightmaps prefix (FarDrain): a terrain modifier destroyed with a
        // draining area leaves the heightmaps it reaches as they are, since they unload with it. Not where anything
        // that stays could show them: next to the player's own zones (a modifier reaches a zone over), nor next to any
        // zone of an area that stays (P45: its whole zone list; tested only round its core before, so its outer ring kept
        // the shape of a modifier unloaded next to it).
        internal bool UnloadsQuietly(Vector3 at)
        {
            if (_areas.Count == 0 || ZNet.instance == null)
                return false;
            Vector2s zone = ZoneSystem.GetZone(at);
            bool draining = false;
            foreach (Area a in _areas)
            {
                if (!a.Draining)
                {
                    if (NextToZones(a, zone))
                        return false;
                }
                else if (!draining)
                    draining = a.Zones.Contains(zone);
            }
            if (!draining)
                return false;
            Vector2s me = ZoneSystem.GetZone(ZNet.instance.GetReferencePosition());
            int r = ZNet.instance.GetSyncedSimulationDistance().NearSimulationDistance + 1;
            return Math.Abs(zone.x - me.x) > r || Math.Abs(zone.y - me.y) > r;
        }

        // The zone is one of the area's or next to one (a modifier reaches a zone over). Its zones lie within ZoneReach of
        // its far portal's zone: what lies farther than one more is not looked through.
        static bool NextToZones(Area a, Vector2s zone)
        {
            int dx = Math.Abs(zone.x - a.Zone.x), dy = Math.Abs(zone.y - a.Zone.y);
            if (dx > a.ZoneReach + 1 || dy > a.ZoneReach + 1)
                return false;
            foreach (Vector2s z in a.Zones)
                if (Math.Abs(zone.x - z.x) <= 1 && Math.Abs(zone.y - z.y) <= 1)
                    return true;
            return false;
        }

        // ---- Far.KeepAliveSlice ---------------------------------------------------------------------
        // The game's removal earmarks every ZDO in its lists (with far areas, the areas' 100k too, added above), then
        // walks every loaded object for those not earmarked: 5-6 ms every sixth tick at twenty portals. Instead, every
        // tick a sixth of a snapshot of the loaded objects is looked at, each by the sector it stands in: one byte per
        // sector says whether that sector's objects are kept (the player's near zones and the far areas', all of them)
        // or its distant ones only (the rings out to the simulation distance), exactly as ZDOMan.FindSectorObjects
        // would list them now; what the game's own lists hold stays as well (checked only when something is to go).
        // What is not kept leaves through the game's own steps for each object it removes (the body of
        // ZNetScene.RemoveObjects' loop, with m_tempRemoved holding them, SpawnBudget's Retire in place of its
        // Object.Destroy), inside the RemoveObjects call, so SectorInstanceIndex's and SpawnBudget's finalizers see the
        // removal as from the game. Unlike the lists (relisted every 0.5-5 s), the sectors are current: an object that
        // walked into an area's sectors since its listing stays, one that walked out goes now. While an area drains,
        // the full pass above runs instead (its Keep list says what goes when).

        static readonly AccessTools.FieldRef<ZNetScene, Dictionary<ZDO, ZNetView>> s_instancesOf =
            AccessTools.FieldRefAccess<ZNetScene, Dictionary<ZDO, ZNetView>>("m_instances");
        static readonly AccessTools.FieldRef<ZNetScene, List<ZNetView>> s_removedOf =
            AccessTools.FieldRefAccess<ZNetScene, List<ZNetView>>("m_tempRemoved");

        // Per sector, by ZoneSystem.SectorToIndex (how ZDOMan files ZDOs): every object kept, or the distant ones.
        const byte KeepAll = 1, KeepDistant = 2;
        const int SectorCount = 512 * 512;
        // A snapshot is looked through in this many ticks (the full pass ran every sixth); the verify runs every Nth.
        const int SliceTicks = 6;
        const int VerifyEvery = 30;

        byte[] _keep;
        readonly List<int> _kept = new List<int>();
        int _keepVersion = -1;
        Vector2s _keepZone;
        SimulationDistance _keepSim, _keepArea;
        readonly List<KeyValuePair<ZDO, ZNetView>> _snap = new List<KeyValuePair<ZDO, ZNetView>>();
        int _snapAt, _snapStep;
        ZNetScene _snapScene;
        readonly List<ZNetView> _victims = new List<ZNetView>();
        readonly List<ZDO> _victimZdos = new List<ZDO>();
        readonly List<ZDO> _verifyNear = new List<ZDO>();
        readonly List<ZDO> _verifyDistant = new List<ZDO>();
        readonly Stopwatch _sliceClock = new Stopwatch();
        int _verifyTick;
        int _statSliceRemoved;
        double _statSliceWorst;

        // Lab: ticks done by slices, ticks left to the full pass (an area draining), errors (the full pass then),
        // objects looked at and taken out, the slowest tick; Far.KeepAliveVerify: checks, objects the game's lists keep that the slices would take
        // out, and objects the slices keep that the game would take out (both must stay 0).
        internal static int LabSliceTicks, LabSliceFull, LabSliceErrors, LabSliceVerifies, LabSliceMissed, LabSliceExtra;
        internal static long LabSliceLooked, LabSliceRemoved;
        internal static double LabSliceMsMax;

        // From the ZNetScene.RemoveObjects prefix with Far.KeepAliveSlice on. True: this tick is done (the game's call
        // is skipped); false: the full pass as without it.
        internal bool KeepAliveSliced(ZNetScene scene, List<ZDO> near, List<ZDO> distant)
        {
            if (scene == null || ZNet.instance == null || ZDOMan.instance == null || ZoneSystem.instance == null)
                return false;
            if (_areas.Count == 0 && PortalLink.All.Count == 0)
                return false;
            // Far.KeepAliveFast ran before: its counts go (the slices keep by sector).
            if (_fast)
                DropFast();
            // FarWorld (shim): on or off the registry (the slices keep by the same sectors either way).
            ShimSync();
            Select();
            bool draining = false;
            foreach (Area a in _areas)
                draining |= a.Draining && a.Keep != null && a.Keep.Count > 0;
            if (_areas.Count == 0 || draining)
            {
                LabSliceFull++;
                return false;
            }
            try
            {
                Slice(scene, near, distant);
                return true;
            }
            catch (Exception e)
            {
                // Whatever went wrong, the game's own pass decides this tick (what was taken out already is out of
                // its table, with m_tempRemoved saying so).
                if (LabSliceErrors++ < 5)
                    Plugin.Log.LogError("Far.KeepAliveSlice: " + e);
                DropSnapshot();
                return false;
            }
        }

        void Slice(ZNetScene scene, List<ZDO> near, List<ZDO> distant)
        {
            _sliceClock.Restart();
            Dictionary<ZDO, ZNetView> instances = s_instancesOf(scene);
            SweepRecent(null, false, 0);
            KeepSectors();
            if (FarFlags.KeepAliveVerify && ++_verifyTick % VerifyEvery == 0)
            {
                // Not counted in the tick's time: a lab check as dear as the full pass.
                _sliceClock.Stop();
                VerifySlice(instances, near, distant);
                _sliceClock.Start();
            }
            if (_snapAt >= _snap.Count || !ReferenceEquals(scene, _snapScene))
            {
                _snap.Clear();
                foreach (KeyValuePair<ZDO, ZNetView> kv in instances)
                    _snap.Add(kv);
                _snapAt = 0;
                _snapStep = Math.Max(1, (_snap.Count + SliceTicks - 1) / SliceTicks);
                _snapScene = scene;
            }
            int end = Math.Min(_snapAt + _snapStep, _snap.Count);
            LabSliceLooked += end - _snapAt;
            _victims.Clear();
            _victimZdos.Clear();
            for (; _snapAt < end; _snapAt++)
            {
                KeyValuePair<ZDO, ZNetView> kv = _snap[_snapAt];
                ZDO z = kv.Key;
                if (Kept(z))
                    continue;
                // Only what the game would take out now: its entry still, alive and holding this ZDO (a view destroyed
                // behind the game's back is left to whoever sweeps those, as the full pass would trip over it).
                ZNetView v = kv.Value;
                if (v is null || !v || !ReferenceEquals(v.GetZDO(), z) || !instances.TryGetValue(z, out ZNetView now) || !ReferenceEquals(now, v) || Recent(z))
                    continue;
                _victims.Add(v);
                _victimZdos.Add(z);
            }
            if (_victims.Count > 0)
            {
                // Whatever the game's own lists hold stays, as in its pass (another mod may have added to them): its
                // earmark, made only when something is to go. A mark left from 256 frames ago does not count.
                byte b = (byte)((uint)Time.frameCount & 0xFFu);
                foreach (ZDO z in _victimZdos)
                    z.TempRemoveEarmark = unchecked((byte)(b - 1));
                Earmark(near, b);
                Earmark(distant, b);
                int kept = 0;
                for (int i = 0; i < _victims.Count; i++)
                    if (_victimZdos[i].TempRemoveEarmark != b)
                    {
                        _victims[kept] = _victims[i];
                        _victimZdos[kept] = _victimZdos[i];
                        kept++;
                    }
                _victims.RemoveRange(kept, _victims.Count - kept);
                _victimZdos.RemoveRange(kept, _victimZdos.Count - kept);
            }
            if (_victims.Count > 0)
            {
                int n = TakeOut(scene, instances);
                LabSliceRemoved += n;
                _statSliceRemoved += n;
            }
            LabSliceTicks++;
            double ms = _sliceClock.Elapsed.TotalMilliseconds;
            if (ms > LabSliceMsMax)
                LabSliceMsMax = ms;
            if (ms > _statSliceWorst)
                _statSliceWorst = ms;
        }

        // The game's removal of each object (ZNetScene.RemoveObjects' loop), for those found in this slice (or, with
        // Far.KeepAliveFast, in this call's walk of the free set). How many went.
        int TakeOut(ZNetScene scene, Dictionary<ZDO, ZNetView> instances)
        {
            List<ZNetView> removed = s_removedOf(scene);
            removed.Clear();
            // One by one: AddRange of a list copies it through a new array first.
            for (int i = 0; i < _victims.Count; i++)
                removed.Add(_victims[i]);
            int n = _victims.Count;
            for (int i = 0; i < removed.Count; i++)
            {
                ZNetView view = removed[i];
                ZDO zdo = view.GetZDO();
                view.ResetZDO();
                Engine.SpawnBudgetModule.Retire(view, zdo);
                if (!zdo.Persistent && zdo.IsOwner())
                    ZDOMan.instance.DestroyZDO(zdo);
                instances.Remove(zdo);
            }
            // Its finalizer reconciles what m_tempRemoved holds when the table's count changed during the call; told
            // here as well, should its prefix have counted after this one ran.
            foreach (ZDO z in _victimZdos)
                Engine.SectorInstanceIndexModule.NotifyChanged(z);
            _victims.Clear();
            _victimZdos.Clear();
            return n;
        }

        bool Kept(ZDO z)
        {
            byte f = _keep[z.GetSectorIndex().Sector];
            return (f & KeepAll) != 0 || (f & KeepDistant) != 0 && z.Distant;
        }

        // Made or come into an area in the last 3 s, or (Far.TrackUntilListed) since its last listing (Track): kept like the
        // lists keep it.
        bool Recent(ZDO z) => _recent.TryGetValue(z, out Tracked t) && TrackKeeps(z, t, Time.time);

        // The sectors kept, made again when the player's zone, the simulation distances or the set of areas changed.
        void KeepSectors()
        {
            Vector2s zone = ZoneSystem.GetZone(ZNet.instance.GetReferencePosition());
            SimulationDistance sim = ZNet.instance.GetSyncedSimulationDistance(), area = AreaDistance();
            if (_keep != null && _keepVersion == _areasVersion && zone == _keepZone && sim.Equals(_keepSim) && area.Equals(_keepArea))
                return;
            if (_keep == null)
                _keep = new byte[SectorCount];
            foreach (int i in _kept)
                _keep[i] = 0;
            _kept.Clear();
            // The game's own lists (ZNetScene.CreateDestroyObjects: around the reference position), then the areas'.
            MarkSectors(zone, sim);
            foreach (Area a in _areas)
                if (!a.Draining)
                    MarkSectors(a.Zone, area);
            _keepVersion = _areasVersion;
            _keepZone = zone;
            _keepSim = sim;
            _keepArea = area;
        }

        // The sectors ZDOMan.FindSectorObjects lists around this one, loop for loop: every object out to the near
        // distance, then distant objects out to the total. Its ring tests the lower row's zone for both rows and the
        // left column's for both columns; so does this.
        void MarkSectors(Vector2s sector, SimulationDistance sim) => MarkSectors(sector, sim, _keep, _kept);

        // The same into any per-sector table (Far.DupEdge: what the game keeps round the player alone), with the sectors
        // marked listed.
        void MarkSectors(Vector2s sector, SimulationDistance sim, byte[] into, List<int> marked)
        {
            ZoneSystem zs = ZoneSystem.instance;
            int near = sim.NearSimulationDistance, total = sim.TotalSimulationDistance;
            bool classic = sim.IsClassic;
            Mark(into, marked, sector.x, sector.y, KeepAll);
            for (int i = 1; i <= near; i++)
            {
                for (int x = sector.x - i; x <= sector.x + i; x++)
                {
                    if (classic || zs.ZonesWithinRadius(sector, new Vector2s(x, sector.y - i), near))
                        Mark(into, marked, x, sector.y - i, KeepAll);
                    if (classic || zs.ZonesWithinRadius(sector, new Vector2s(x, sector.y + i), near))
                        Mark(into, marked, x, sector.y + i, KeepAll);
                }
                for (int y = sector.y - i + 1; y <= sector.y + i - 1; y++)
                {
                    if (classic || zs.ZonesWithinRadius(sector, new Vector2s(sector.x - i, y), near))
                        Mark(into, marked, sector.x - i, y, KeepAll);
                    if (classic || zs.ZonesWithinRadius(sector, new Vector2s(sector.x + i, y), near))
                        Mark(into, marked, sector.x + i, y, KeepAll);
                }
            }
            for (int k = classic ? 1 + near : 1; k <= total; k++)
            {
                for (int x = sector.x - k; x <= sector.x + k; x++)
                    if (classic || zs.ZonesWithinRadius(sector, new Vector2s(x, sector.y - k), total, ghostZone: true))
                    {
                        Mark(into, marked, x, sector.y - k, KeepDistant);
                        Mark(into, marked, x, sector.y + k, KeepDistant);
                    }
                for (int y = sector.y - k + 1; y <= sector.y + k - 1; y++)
                    if (classic || zs.ZonesWithinRadius(sector, new Vector2s(sector.x - k, y), total, ghostZone: true))
                    {
                        Mark(into, marked, sector.x - k, y, KeepDistant);
                        Mark(into, marked, sector.x + k, y, KeepDistant);
                    }
            }
        }

        static void Mark(byte[] into, List<int> marked, int x, int y, byte keep)
        {
            int i = (int)ZoneSystem.SectorToIndex(x, y).Sector;
            if (into[i] == 0)
                marked.Add(i);
            into[i] |= keep;
        }

        // ---- Far.DupEdge (Docs\PARITY-PLAN.md P15) ------------------------------------------------------------------------
        // With Far.NoDupAreas a far portal inside the player's own active area (pairs 0-130 m apart) got no area: the game
        // loads it as his own surroundings, but only out to his rings, so small objects past his near ring were missing in
        // the window, and the creatures of the far portal's active area outside his stood invisible and still. With the
        // switch it gets an edge area (Area.Edge):
        //  - its lists are ZDOMan.FindSectorObjects round the far zone less what the game lists round the player (every
        //    object of his near sectors, the distant ones of his ring: KeepSectors' rule, LeaveOutMine), so nothing is listed
        //    twice;
        //  - it claims its far active area less the player's (Claims), which the game's pass owns for him;
        //  - whenever the player's zone changes it takes in, in Select, before the game's removal pass goes by his new
        //    surroundings, the objects of its square his lists let go (EdgeTakeIn: a few sectors, not a listing of its whole
        //    square, which at about 5 ms each made every zone boundary a hitch near short pairs); its listing follows on the
        //    pacer's schedule. It goes live as any area does (Far.SideReady: by its own halves), without waiting for the
        //    server (Arrived: its far portal stands in his own area);
        //  - when the far portal leaves the player's active area it is listed whole again (a whole area), and the other way.
        // Off: no area for such a far end (Far.NoDupAreas), as before.

        // Lab: edge areas now.
        internal static int LabEdgeAreas;

        // What the game keeps round the player, per sector as KeepSectors marks it, and the player's zone and simulation
        // distance it was made for.
        byte[] _mine;
        readonly List<int> _mineMarked = new List<int>();
        Vector2s _mineZone;
        SimulationDistance _mineSim;
        bool _mineKnown;

        // The edge area's lists less what the game lists round the player now.
        void LeaveOutMine(Area a)
        {
            EnsureMine();
            a.EdgeZone = _mineZone;
            LeaveOut(a.Near);
            LeaveOut(a.Distant);
        }

        void LeaveOut(List<ZDO> list)
        {
            int kept = 0;
            for (int i = 0; i < list.Count; i++)
            {
                ZDO z = list[i];
                if (MineKeeps(_mine[z.GetSectorIndex().Sector], z.Distant))
                    continue;
                list[kept++] = z;
            }
            list.RemoveRange(kept, list.Count - kept);
        }

        // A sector's mark keeps an object of it in the game's lists (every object, or the distant ones).
        static bool MineKeeps(byte f, bool distant) => (f & KeepAll) != 0 || ((f & KeepDistant) != 0 && distant);

        // One sector's objects, as ZDOMan.FindSectorObjects lists its own sector (portals included).
        static readonly SimulationDistance OneSector = new SimulationDistance(0, 0, true);

        // The player's zone the edge areas were cut for before the last move (MoveMine), the sectors whose objects the game's
        // lists let go then with their marks before and after, _mine's marks before the move, an area's square as its
        // listing takes it, and what an edge area took in.
        Vector2s _mineFrom;
        readonly List<(int sector, byte was, byte now)> _mineLost = new List<(int, byte, byte)>();
        readonly List<(int sector, byte was)> _mineOld = new List<(int, byte)>();
        byte[] _square;
        readonly List<int> _squareMarked = new List<int>();
        readonly List<ZDO> _sectorObjects = new List<ZDO>();
        readonly List<ZDO> _takenIn = new List<ZDO>();

        // Lab: edge areas that followed the player's zone by taking in what his lists let go (no listing).
        internal static int LabEdgeTakeIns;

        // From Select: the player's zone moved (his simulation distance the same) since the edge areas were cut (_mine), and
        // an edge area waits for it: _mine is made for his zone now and _mineLost holds the sectors whose objects the game's
        // lists let go. False otherwise (an edge area then lists again whole, Reedge).
        bool MoveMine(Vector2s zone)
        {
            _mineLost.Clear();
            if (!FarFlags.DupEdge || !_mineKnown || ZNet.instance == null || zone == _mineZone)
                return false;
            bool waits = false;
            foreach (Area a in _areas)
                if (a.Edge && !a.Draining && a.EdgeZone == _mineZone)
                {
                    waits = true;
                    break;
                }
            if (!waits || !ZNet.instance.GetSyncedSimulationDistance().Equals(_mineSim))
                return false;
            _mineOld.Clear();
            foreach (int i in _mineMarked)
                _mineOld.Add((i, _mine[i]));
            _mineFrom = _mineZone;
            EnsureMine();
            foreach ((int sector, byte was) in _mineOld)
            {
                byte now = _mine[sector];
                // Every object of it kept before and not now, or none kept now.
                if (((was & KeepAll) != 0 && (now & KeepAll) == 0) || now == 0)
                    _mineLost.Add((sector, was, now));
            }
            _mineOld.Clear();
            return true;
        }

        // Far.DupEdge: an edge area cut for the zone the player just left follows his move: the objects of its square the
        // game's lists round him let go (_mineLost) join its lists as its listing takes them (a near sector's every object, a
        // ring sector's distant ones), counted, made and given up as a listing's are (AddPending). Its listing is due from now
        // (past the 5 s a loaded area waits and the RelistEvery a loading one does: the pacer's next within the frame's
        // budget, one a frame, LoadedOverdue's at the latest); that also lets go of what came into his lists.
        void EdgeTakeIn(Area a, float now)
        {
            a.EdgeZone = _mineZone;
            a.ListedAt = Mathf.Min(a.ListedAt, now - 5f - RelistEvery);
            LabEdgeTakeIns++;
            if (_mineLost.Count == 0)
                return;
            if (_square == null)
                _square = new byte[SectorCount];
            _takenIn.Clear();
            try
            {
                MarkSectors(a.Zone, AreaDistance(), _square, _squareMarked);
                foreach ((int sector, byte was, byte mine) in _mineLost)
                {
                    byte listed = _square[sector];
                    if (listed == 0)
                        continue;
                    _sectorObjects.Clear();
                    ZDOMan.instance.FindSectorObjects(ZoneSystem.IndexToSector((uint)sector), OneSector, _sectorObjects);
                    foreach (ZDO z in _sectorObjects)
                    {
                        bool distant = z.Distant;
                        if (!MineKeeps(was, distant) || MineKeeps(mine, distant))
                            continue;
                        if ((listed & KeepAll) != 0)
                            a.Near.Add(z);
                        else if (distant)
                            a.Distant.Add(z);
                        else
                            continue;
                        // Far.KeepAliveFast: held as a listing's would be.
                        if (_fast)
                            Hold(z);
                        _takenIn.Add(z);
                    }
                }
            }
            finally
            {
                foreach (int i in _squareMarked)
                    _square[i] = 0;
                _squareMarked.Clear();
                _sectorObjects.Clear();
            }
            if (_takenIn.Count > 0)
            {
                int pending = a.Pending.Count;
                AddPending(a, _takenIn, ZNet.instance.GetReferencePosition(), ZDOMan.GetSessionID());
                if (a.Pending.Count > pending)
                    OrderPending(a);
                _takenIn.Clear();
            }
        }

        void EnsureMine()
        {
            Vector2s zone = ZoneSystem.GetZone(ZNet.instance.GetReferencePosition());
            SimulationDistance sim = ZNet.instance.GetSyncedSimulationDistance();
            if (_mineKnown && zone == _mineZone && sim.Equals(_mineSim))
                return;
            if (_mine == null)
                _mine = new byte[SectorCount];
            foreach (int i in _mineMarked)
                _mine[i] = 0;
            _mineMarked.Clear();
            MarkSectors(zone, sim, _mine, _mineMarked);
            _mineZone = zone;
            _mineSim = sim;
            _mineKnown = true;
        }

        void DropSnapshot()
        {
            _snap.Clear();
            _snapAt = 0;
            _snapScene = null;
        }

        // Far.KeepAliveVerify: the full pass's earmark (the game's own lists and every area listed afresh, plus what was
        // just made in them) against what the slices keep (the sectors, what was just made, the game's own lists), over
        // every loaded object. A difference is logged (the first few) and counted.
        void VerifySlice(Dictionary<ZDO, ZNetView> instances, List<ZDO> near, List<ZDO> distant)
        {
            LabSliceVerifies++;
            // Marks of this frame and the two before, never one of a frame to come (the game's next pass would take it
            // for its own): b for the areas' lists and what was just made, own for the game's lists, none for the rest.
            byte b = (byte)((uint)Time.frameCount & 0xFFu);
            byte own = unchecked((byte)(b - 1)), none = unchecked((byte)(b - 2));
            foreach (KeyValuePair<ZDO, ZNetView> kv in instances)
                kv.Key.TempRemoveEarmark = none;
            SimulationDistance sim = AreaDistance();
            foreach (Area a in _areas)
            {
                if (a.Draining)
                    continue;
                _verifyNear.Clear();
                _verifyDistant.Clear();
                ZDOMan.instance.FindSectorObjects(a.Zone, sim, _verifyNear, _verifyDistant);
                Earmark(_verifyNear, b);
                Earmark(_verifyDistant, b);
            }
            _verifyNear.Clear();
            _verifyDistant.Clear();
            foreach (var kv in _recent)
                if (Recent(kv.Key))
                    kv.Key.TempRemoveEarmark = b;
            Earmark(near, own);
            Earmark(distant, own);
            foreach (KeyValuePair<ZDO, ZNetView> kv in instances)
            {
                ZDO z = kv.Key;
                ZNetView v = kv.Value;
                if (v is null || !v || !ReferenceEquals(v.GetZDO(), z))
                    continue;
                byte mark = z.TempRemoveEarmark;
                bool game = mark == b || mark == own, slice = mark == own || Kept(z) || Recent(z);
                if (game == slice)
                    continue;
                if (game)
                    LabSliceMissed++;
                else
                    LabSliceExtra++;
                if (LabSliceMissed + LabSliceExtra <= 20)
                    Plugin.Log.LogWarning($"Far.KeepAliveVerify: {v.name} {z.m_uid} at {z.GetPosition():F0} (sector {z.GetSector().x},{z.GetSector().y}, " +
                                          $"{(z.Distant ? "distant" : "near")}): {(game ? "listed by the game, taken out by the slices" : "not listed by the game, kept by the slices")}");
            }
        }

        static void Earmark(List<ZDO> list, byte b)
        {
            foreach (ZDO z in list)
                z.TempRemoveEarmark = b;
        }

        // ---- Far.KeepAliveFast ----------------------------------------------------------------------
        // The full pass (KeepAlive above, then the game's RemoveObjects) adds the areas' lists to the game's (through a
        // copy of each), earmarks them all and walks every loaded object for those not earmarked: twice over the 90-100k
        // far objects at twenty portals, 5-7 ms every sixth tick, for objects that nearly all stay. Far.KeepAliveSlice
        // spread the walk over six ticks but still looked at every loaded object, each with a table lookup and its
        // sector, on top of a copy of the whole table every sixth tick: more in all (+0.34 ms a frame).
        // Here the far objects are not looked at. Every ZDO the areas' lists hold (an area's Near and Distant, what is
        // left of its Keep while it drains) has a count of how many times they hold it, kept as the lists change: a
        // listing counts what came and what went (Recount), the drain what leaves Keep, an area taken back what its
        // lists hold beyond Keep. Every loaded object they do not hold is in the free set (the player's surroundings,
        // what the game made outside the lists, what a listing or the drain let go): it takes in what the game loads
        // (ZNetScene.AddInstance) and what a count drops to none for. On the ticks the full pass runs, the game's lists
        // and what was just made in an area (Track) are earmarked as there and the free set is walked: what in it is
        // loaded and not earmarked goes the game's way (TakeOut, as with the slices: SectorInstanceIndex and
        // SpawnBudget see the game's removal), what is not loaded any more leaves the set. The same objects go at the
        // same ticks as with the full pass; a call costs the game's own lists, the free set and what is let go,
        // whatever the far areas hold.
        // Marks: everything in the free set carries the mark of the last call (that of the frame before this one after
        // a rebuild, or after 256 frames without a call), from when it came in and from each call, so this call's mark
        // there means listed now. What the lists hold is never looked at by the walk; a listing uses those marks to
        // tell what came and what went.
        // Off, when another mod rewrites RemoveObjects (its decision may not be the game's), or on an error: the full
        // pass, and the counts and the set are dropped (made again from the lists and the game's table when this runs
        // again). Objects whose view was destroyed or lost its ZDO behind the game's back are left as by the slices
        // (the full pass throws on them, before taking anything out or part way through, until a sweep takes them).

        static readonly System.Reflection.MethodInfo s_removeObjects = AccessTools.DeclaredMethod(typeof(ZNetScene), "RemoveObjects");
        static readonly List<ZDO> s_noZdos = new List<ZDO>();
        // Another mod rewriting RemoveObjects is looked for on the first call and then this often (real seconds).
        const float ForeignCheckEvery = 10f;
        const string LabOwners = "valheim.realportal.lab";

        // How many times the areas' lists hold each ZDO (by reference), and the loaded objects they do not hold.
        readonly Dictionary<ZDO, int> _held = new Dictionary<ZDO, int>(ZdoRef.Instance);
        readonly HashSet<ZDO> _free = new HashSet<ZDO>(ZdoRef.Instance);
        // Built (and kept up by the hooks below), to be built again at the next call, for this scene.
        bool _fast, _fastRebuild;
        ZNetScene _fastScene;
        // The mark the free set carries, and the frame of the last call.
        byte _fastMark;
        int _fastFrame;
        readonly List<ZDO> _fastOld = new List<ZDO>();
        readonly List<ZDO> _fastDrop = new List<ZDO>();
        readonly Stopwatch _fastClock = new Stopwatch();
        float _foreignAt = -1f;
        bool _foreign;
        int _fastVerifyTick;
        Dictionary<ZDO, int> _verifyCount;
        int _statFastCalls, _statFastRemoved;
        long _statFastWalked;
        double _statFastMs, _statFastWorst;

        // Lab: calls done this way, free objects walked (all calls, the most in one), objects of the game's lists and
        // just made earmarked, objects taken out, ms (all calls, the slowest; a rebuild counts in its call), rebuilds
        // and the slowest, errors (the full pass then), calls left to the full pass because another mod rewrites
        // RemoveObjects, listings that found an object twice in a list (counted one by one then), and loaded objects
        // of the free set found held (dropped from it; must stay 0). Far.KeepAliveVerify: checks, objects the full
        // pass keeps that this takes out, objects this keeps that the full pass takes out, and counts that differ
        // from the lists (all three must stay 0).
        internal static long LabFastCalls, LabFastWalked, LabFastMarked, LabFastRemoved;
        internal static int LabFastWalkedMax, LabFastBuilds, LabFastErrors, LabFastForeign, LabFastTwice, LabFastStale;
        internal static double LabFastMs, LabFastMsMax, LabFastBuildMsMax;
        internal static int LabFastVerifies, LabFastMissed, LabFastExtra, LabFastCountErrors;

        // Lab: ZDOs the areas' lists hold, and the free set (what a call walks).
        internal int FastHeld => _held.Count;
        internal int FastFree => _free.Count;

        // From the ZNetScene.RemoveObjects prefix with Far.KeepAliveFast on and far areas loaded, on the ticks the full
        // pass runs. True: this tick is done (the game's call is skipped); false: the full pass, as without it.
        internal bool KeepAliveFast(ZNetScene scene, List<ZDO> near, List<ZDO> distant)
        {
            if (scene == null || ZNet.instance == null || ZDOMan.instance == null || ZoneSystem.instance == null)
                return false;
            if (ForeignRemoval())
            {
                LabFastForeign++;
                return false;
            }
            try
            {
                Fast(scene, near, distant);
                return true;
            }
            catch (Exception e)
            {
                // Whatever went wrong, the full pass decides this tick (what was taken out already is out of the game's
                // table, with m_tempRemoved saying so); the counts are made again next time.
                if (LabFastErrors++ < 5)
                    Plugin.Log.LogError("Far.KeepAliveFast: " + e);
                DropFast();
                return false;
            }
        }

        void Fast(ZNetScene scene, List<ZDO> near, List<ZDO> distant)
        {
            _fastClock.Restart();
            Dictionary<ZDO, ZNetView> instances = s_instancesOf(scene);
            if (_snap.Count > 0)
                DropSnapshot();
            // As KeepAlive: areas made, released or taken back, then what draining areas give up this tick (its counts
            // drop with it, or are made below from what is left). FarWorld (shim): on or off the registry before that.
            ShimSync();
            Select();
            if (FarFlags.DrainCalm)
                DrainCalmly();
            else
                foreach (Area a in _areas)
                    if (a.Draining && a.Keep != null)
                    {
                        int n = Math.Min(DrainPerTick, a.Keep.Count);
                        if (_fast)
                            UnholdRange(a.Keep, a.Keep.Count - n, n);
                        a.Keep.RemoveRange(a.Keep.Count - n, n);
                    }
            int frame = Time.frameCount;
            byte b = (byte)((uint)frame & 0xFFu);
            if (!_fast || _fastRebuild || !ReferenceEquals(scene, _fastScene))
                BuildFast(scene, instances, b);
            else if (frame - _fastFrame >= 256 || frame == _fastFrame)
            {
                // The last call's mark could be this call's: the free set takes the frame before's.
                _fastMark = unchecked((byte)(b - 1));
                foreach (ZDO z in _free)
                    z.TempRemoveEarmark = _fastMark;
            }
            // The game's lists, and what was just made in an area (KeepAlive adds those to them).
            int marked = near.Count + distant.Count;
            marked += SweepRecent(null, true, b);
            Earmark(near, b);
            Earmark(distant, b);
            if (FarFlags.KeepAliveVerify && ++_fastVerifyTick % VerifyEvery == 0)
            {
                // Not counted in the call's time: a lab check as dear as the full pass.
                _fastClock.Stop();
                VerifyFast(instances, b);
                _fastClock.Start();
            }
            // The free set: what is loaded and not earmarked goes, what is not loaded any more leaves the set; what
            // stays carries this call's mark from here on.
            _victims.Clear();
            _victimZdos.Clear();
            _fastDrop.Clear();
            int walked = _free.Count;
            SectorRegistry reg = _shimReg;
            foreach (ZDO z in _free)
            {
                if (z.TempRemoveEarmark == b)
                    continue;
                z.TempRemoveEarmark = b;
                if (!instances.TryGetValue(z, out ZNetView v))
                {
                    _fastDrop.Add(z);
                    continue;
                }
                if (_held.ContainsKey(z))
                {
                    // Held after all (never): kept, and out of the set.
                    LabFastStale++;
                    _fastDrop.Add(z);
                    continue;
                }
                // Only what the game would take out now: its entry still, alive and holding this ZDO (as the slices).
                if (v is null || !v || !ReferenceEquals(v.GetZDO(), z))
                    continue;
                // FarWorld (shim), in place of Track: what came into a far area's sectors since its last listing is kept by
                // them (the registry knows it as of now), and stays in the set until a listing holds it.
                if (reg != null && RegistryKeeps(reg, z))
                {
                    LabShimKept++;
                    continue;
                }
                _victims.Add(v);
                _victimZdos.Add(z);
                _fastDrop.Add(z);
            }
            // m_tempRemoved as the game's call leaves it: what this call removed (TakeOut's, or none).
            int removed = 0;
            if (_victims.Count > 0)
                removed = TakeOut(scene, instances);
            else
                s_removedOf(scene).Clear();
            foreach (ZDO z in _fastDrop)
                _free.Remove(z);
            _fastDrop.Clear();
            _fastMark = b;
            _fastFrame = frame;
            double ms = _fastClock.Elapsed.TotalMilliseconds;
            LabFastCalls++;
            LabFastWalked += walked;
            LabFastMarked += marked;
            LabFastRemoved += removed;
            LabFastMs += ms;
            if (walked > LabFastWalkedMax)
                LabFastWalkedMax = walked;
            if (ms > LabFastMsMax)
                LabFastMsMax = ms;
            _statFastCalls++;
            _statFastWalked += walked;
            _statFastRemoved += removed;
            _statFastMs += ms;
            if (ms > _statFastWorst)
                _statFastWorst = ms;
        }

        // The counts and the free set made from the areas' lists and the game's table as they are now.
        void BuildFast(ZNetScene scene, Dictionary<ZDO, ZNetView> instances, byte b)
        {
            long t0 = Stopwatch.GetTimestamp();
            _held.Clear();
            _free.Clear();
            _fast = true;
            _fastRebuild = false;
            _fastScene = scene;
            _fastMark = unchecked((byte)(b - 1));
            foreach (Area a in _areas)
                if (a.Draining)
                    HoldAll(a.Keep);
                else
                {
                    HoldAll(a.Near);
                    HoldAll(a.Distant);
                }
            foreach (KeyValuePair<ZDO, ZNetView> kv in instances)
                if (!_held.ContainsKey(kv.Key))
                {
                    _free.Add(kv.Key);
                    kv.Key.TempRemoveEarmark = _fastMark;
                }
            LabFastBuilds++;
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            if (ms > LabFastBuildMsMax)
                LabFastBuildMsMax = ms;
        }

        void DropFast()
        {
            _fast = false;
            _fastRebuild = false;
            _fastScene = null;
            _held.Clear();
            _free.Clear();
            _fastOld.Clear();
            _fastDrop.Clear();
        }

        // From the ZNetScene.AddInstance postfix: an object the game loads that no list holds joins the free set.
        internal void InstanceAdded(ZNetScene scene, ZDO zdo)
        {
            if (!_fast || zdo == null || !ReferenceEquals(scene, _fastScene) || _held.ContainsKey(zdo))
                return;
            _free.Add(zdo);
            zdo.TempRemoveEarmark = _fastMark;
        }

        void Hold(ZDO z)
        {
            if (z == null)
                return;
            if (_held.TryGetValue(z, out int n))
            {
                _held[z] = n + 1;
                return;
            }
            _held[z] = 1;
            _free.Remove(z);
        }

        void Unhold(ZDO z)
        {
            if (z == null)
                return;
            if (!_held.TryGetValue(z, out int n))
            {
                // The counts are off (never): made again at the next call.
                LabFastCountErrors++;
                _fastRebuild = true;
                return;
            }
            if (n > 1)
            {
                _held[z] = n - 1;
                return;
            }
            _held.Remove(z);
            _free.Add(z);
            z.TempRemoveEarmark = _fastMark;
        }

        void HoldAll(List<ZDO> list)
        {
            if (list == null)
                return;
            for (int i = 0; i < list.Count; i++)
                Hold(list[i]);
        }

        void UnholdAll(List<ZDO> list)
        {
            for (int i = 0; i < list.Count; i++)
                Unhold(list[i]);
        }

        void UnholdRange(List<ZDO> list, int start, int count)
        {
            for (int i = start; i < start + count; i++)
                Unhold(list[i]);
        }

        // A hook outside the removal's call (a listing, an area taken back): on an error the counts go and are made
        // again at the next call.
        void FastRecount(List<ZDO> old, List<ZDO> na, List<ZDO> nb)
        {
            try
            {
                Recount(old, na, nb);
            }
            catch (Exception e)
            {
                if (LabFastErrors++ < 5)
                    Plugin.Log.LogError("Far.KeepAliveFast: " + e);
                DropFast();
            }
        }

        // An area's lists went from old to na and nb (a listing; or a draining area taken back, old its Keep): the
        // counts follow. Marks sort out what both sides hold (most of a listing: nothing to do), what came and what
        // went, three passes over each side and a count only for what changed. Should either side hold an object
        // twice, every object of both is counted one by one instead (raised first, so nothing both hold drops to none
        // on the way).
        void Recount(List<ZDO> old, List<ZDO> na, List<ZDO> nb)
        {
            byte fresh = unchecked((byte)(_fastMark + 101)), gone = unchecked((byte)(_fastMark + 102)), was = unchecked((byte)(_fastMark + 103)),
                 both = unchecked((byte)(_fastMark + 104)), came = unchecked((byte)(_fastMark + 105));
            Earmark(na, fresh);
            Earmark(nb, fresh);
            Earmark(old, gone);
            bool twice = false;
            for (int i = 0; i < old.Count; i++)
            {
                ZDO z = old[i];
                twice |= z.TempRemoveEarmark == was;
                z.TempRemoveEarmark = was;
            }
            twice |= Classify(na, was, both, came);
            twice |= Classify(nb, was, both, came);
            if (twice)
            {
                LabFastTwice++;
                HoldAll(na);
                HoldAll(nb);
                UnholdAll(old);
                return;
            }
            HoldMarked(na, came);
            HoldMarked(nb, came);
            for (int i = 0; i < old.Count; i++)
                if (old[i].TempRemoveEarmark == was)
                    Unhold(old[i]);
        }

        // The new side: what the old side holds too (marked 'was') is in both, the rest came. True: an object twice.
        static bool Classify(List<ZDO> list, byte was, byte both, byte came)
        {
            bool twice = false;
            for (int i = 0; i < list.Count; i++)
            {
                ZDO z = list[i];
                byte k = z.TempRemoveEarmark;
                if (k == both || k == came)
                    twice = true;
                else
                    z.TempRemoveEarmark = k == was ? both : came;
            }
            return twice;
        }

        void HoldMarked(List<ZDO> list, byte came)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i].TempRemoveEarmark == came)
                    Hold(list[i]);
        }

        // One by one: AddRange of a list copies it through a new array first.
        static void CopyLists(List<ZDO> a, List<ZDO> b, List<ZDO> into)
        {
            into.Clear();
            for (int i = 0; i < a.Count; i++)
                into.Add(a[i]);
            for (int i = 0; i < b.Count; i++)
                into.Add(b[i]);
        }

        // Another mod rewriting RemoveObjects (a transpiler or IL manipulator not ours or the lab's): the full pass,
        // which runs its code. Logged when that changes.
        bool ForeignRemoval()
        {
            float now = Time.realtimeSinceStartup;
            if (_foreignAt >= 0f && now - _foreignAt < ForeignCheckEvery)
                return _foreign;
            _foreignAt = now;
            bool foreign;
            string why = "another mod rewrites ZNetScene.RemoveObjects";
            try
            {
                HarmonyLib.Patches info = s_removeObjects != null ? Harmony.GetPatchInfo(s_removeObjects) : null;
                foreign = info != null && (Foreign(info.Transpilers) || Foreign(info.ILManipulators));
            }
            catch (Exception e)
            {
                // Cannot tell who else is there: the full pass.
                foreign = true;
                why = "reading Harmony patch info failed (" + e.Message + ")";
            }
            if (foreign && !_foreign)
                Plugin.Log.LogWarning("Far.KeepAliveFast: " + why + ", the full keep-alive pass runs instead");
            else if (!foreign && _foreign)
                Plugin.Log.LogInfo("Far.KeepAliveFast: ZNetScene.RemoveObjects is no longer rewritten by another mod");
            _foreign = foreign;
            return foreign;
        }

        static bool Foreign(IEnumerable<HarmonyLib.Patch> patches)
        {
            if (patches == null)
                return false;
            foreach (HarmonyLib.Patch p in patches)
                if (p.owner != Plugin.Guid && p.owner != Plugin.EngineGuid && (p.owner == null || !p.owner.StartsWith(LabOwners, StringComparison.Ordinal)))
                    return true;
            return false;
        }

        // Far.KeepAliveVerify: the full pass's decision (the game's lists and what was just made, earmarked above, and
        // every area's lists as they are now) against this one's, over every loaded object; and the counts against the
        // lists. A difference is logged (the first few) and counted; counts that differ are made again at the next call.
        void VerifyFast(Dictionary<ZDO, ZNetView> instances, byte b)
        {
            LabFastVerifies++;
            if (_verifyCount == null)
                _verifyCount = new Dictionary<ZDO, int>(ZdoRef.Instance);
            _verifyCount.Clear();
            foreach (Area a in _areas)
                if (a.Draining)
                    CountInto(a.Keep, _verifyCount);
                else
                {
                    CountInto(a.Near, _verifyCount);
                    CountInto(a.Distant, _verifyCount);
                }
            int counts = 0;
            foreach (KeyValuePair<ZDO, int> kv in _verifyCount)
                if (!_held.TryGetValue(kv.Key, out int n) || n != kv.Value)
                    counts++;
            foreach (KeyValuePair<ZDO, int> kv in _held)
                if (!_verifyCount.ContainsKey(kv.Key))
                    counts++;
            if (counts > 0)
            {
                if (LabFastCountErrors < 20)
                    Plugin.Log.LogWarning($"Far.KeepAliveVerify: {counts} counts differ from the areas' lists ({_held.Count} held, {_verifyCount.Count} listed); made again at the next call");
                LabFastCountErrors += counts;
                _fastRebuild = true;
            }
            SectorRegistry reg = _shimReg;
            foreach (KeyValuePair<ZDO, ZNetView> kv in instances)
            {
                ZDO z = kv.Key;
                ZNetView v = kv.Value;
                // Both leave these (the full pass would throw on them).
                if (v is null || !v || !ReferenceEquals(v.GetZDO(), z))
                    continue;
                bool marked = z.TempRemoveEarmark == b;
                // FarWorld (shim): what the registry keeps now, the full pass lists (KeepAlive) and this one keeps (Fast).
                bool sector = !marked && reg != null && RegistryKeeps(reg, z);
                bool listed = marked || sector || _verifyCount.ContainsKey(z);
                bool kept = marked || sector || !_free.Contains(z) || _held.ContainsKey(z);
                if (listed == kept)
                    continue;
                if (listed)
                    LabFastMissed++;
                else
                    LabFastExtra++;
                if (LabFastMissed + LabFastExtra <= 20)
                    Plugin.Log.LogWarning($"Far.KeepAliveVerify: {v.name} {z.m_uid} at {z.GetPosition():F0} ({(z.Distant ? "distant" : "near")}, " +
                                          $"held {(_held.TryGetValue(z, out int h) ? h : 0)}, free {_free.Contains(z)}): " +
                                          (listed ? "listed, taken out by the fast pass" : "not listed, kept by the fast pass"));
            }
        }

        static void CountInto(List<ZDO> list, Dictionary<ZDO, int> counts)
        {
            if (list == null)
                return;
            foreach (ZDO z in list)
                if (z != null)
                    counts[z] = (counts.TryGetValue(z, out int n) ? n : 0) + 1;
        }

        // The distant terrain mesh of the area around a far portal, drawn only by its windows.
        internal FarTerrain TerrainFor(ZDO farPortal) =>
            farPortal != null && _byPortal.TryGetValue(farPortal.m_uid, out Area a) && !a.Draining ? a.Terrain : null;

        internal void HideTerrains()
        {
            foreach (Area a in _areas)
                a.Terrain?.SetVisible(false);
        }

        readonly Dictionary<string, (int n, double worst)> _slow = new Dictionary<string, (int, double)>();

        // Single steps over 8 ms (one frame's budget blown by one thing), by what they were.
        void NoteSlow(string what, double ms)
        {
            if (ms < 8.0)
                return;
            _slow.TryGetValue(what, out var s);
            _slow[what] = (s.n + 1, Math.Max(s.worst, ms));
        }

        void CountStats(double ms)
        {
            _statFrames++;
            _statMs += ms;
            _worstMs = Math.Max(_worstMs, ms);
            if (Time.unscaledTime < _statNext)
                return;
            _statNext = Time.unscaledTime + 10f;
            if (_areas.Count > 0 && Plugin.Verbose.Value)
            {
                var sb = new System.Text.StringBuilder($"Remote areas: {_areas.Count}, loading {_statMs / Math.Max(1, _statFrames):0.00} ms/frame (worst {_worstMs:0.0}), " +
                                                      $"{_statSpawnedZones} zones and {_statCreated} objects in 10 s");
                foreach (Area a in _areas)
                    sb.Append($"\n  portal {a.Portal}: {(a.Draining ? "draining" : a.Ready ? "ready" : "loading")}, zones {LoadedZones(a)}/{a.Zones.Count}, " +
                              $"objects {a.Near.Count + a.Distant.Count - a.Pending.Count - a.Deferred}/{a.Near.Count + a.Distant.Count}");
                foreach (var kv in _slow)
                    sb.Append($"\n  slow step {kv.Key}: {kv.Value.n} x, worst {kv.Value.worst:0.0} ms");
                if (FarFlags.LoadPacer)
                    sb.Append($"\n  pacer: {_statHeld} objects held for a later frame; usual zone {_zoneMs:0.0} ms, terrain mesh {_terrainMs:0.0} ms, " +
                              $"relist {_relistMs:0.0} ms, {_prefabMs.Count + _locationMs.Count} object kinds timed");
                if (FarFlags.CoreFirst && _coreMs + _fillMs > 0.0)
                    sb.Append($"\n  core first: filling in got {_fillMs / (_coreMs + _fillMs):P0} of the loading time lately");
                if (FarFlags.ZoneSpawnSplit || FarZoneSpawn.LabBegun > 0)
                    sb.Append("\n  ").Append(FarZoneSpawn.Describe());
                if (FarFlags.KeepAliveSlice)
                    sb.Append($"\n  keep-alive slices: {_statSliceRemoved} objects taken out, slowest tick {_statSliceWorst:0.00} ms" +
                              (FarFlags.KeepAliveVerify ? $"; verify: {LabSliceVerifies} checks, {LabSliceMissed} listed but taken out, {LabSliceExtra} kept but not listed" : ""));
                if (FarFlags.KeepAliveFast && _statFastCalls > 0)
                    sb.Append($"\n  keep-alive fast: {_statFastCalls} calls, {_statFastWalked / _statFastCalls} objects walked a call (free {_free.Count}, held {_held.Count}), " +
                              $"{_statFastRemoved} taken out, {_statFastMs / _statFastCalls:0.000} ms a call, slowest {_statFastWorst:0.00} ms" +
                              (FarFlags.KeepAliveVerify ? $"; verify: {LabFastVerifies} checks, {LabFastMissed} listed but taken out, {LabFastExtra} kept but not listed, {LabFastCountErrors} counts off" : ""));
                if (FarFlags.ArrivalPacer || LabArrivals > 0)
                    sb.Append("\n  ").Append(DescribeArrival());
                if (_shimReg != null || LabShimStarts > 0)
                    sb.Append("\n  ").Append(DescribeShim());
                Plugin.Log.LogInfo(sb.ToString());
            }
            _statFastCalls = 0;
            _statFastWalked = 0;
            _statFastRemoved = 0;
            _statFastMs = 0.0;
            _statFastWorst = 0.0;
            _statSliceRemoved = 0;
            _statSliceWorst = 0.0;
            _slow.Clear();
            _statHeld = 0;
            _statFrames = 0;
            _statMs = 0;
            _worstMs = 0;
            _statSpawnedZones = 0;
            _statCreated = 0;
        }

        internal void Clear()
        {
            // FarWorld (shim): off the registry (its sectors given back while it still stands), nothing listed again.
            if (_shimReg != null)
                ShimStop(false);
            UnhookBorn();
            _relistAll = false;
            // Far.CrossAtOnce: no distant terrain stands in for the game's any more (one no area holds goes), nor is a spare kept.
            FarTerrain.EndHandOff();
            DropSpare();
            foreach (Area a in _areas)
            {
                a.Terrain?.Destroy();
                // Far.TrackUntilListed: what came into it is kept no longer than Track's 3 s.
                a.Gone = true;
            }
            // Far.DupEdge: the player's surroundings are made again for the next scene.
            _mineKnown = false;
            LabEdgeAreas = 0;
            _areas.Clear();
            _byPortal.Clear();
            _bad.Clear();
            _release.Clear();
            // Far.ClaimLikeGame: nothing waits to be taken.
            _claimWait.Clear();
            _claimQueued.Clear();
            _claimNew.Clear();
            LabClaimed = LabClaimPending = 0;
            _paused.Clear();
            _frozen.Clear();
            // Far.SectorSleep: what is still there of the registry wakes (nothing is left frozen that something else
            // keeps), and the awake set starts over.
            ClearSleepers();
            _watchedAt.Clear();
            _coreMs = _fillMs = 0.0;
            // Far.ArrivalPacer: the next scene's first reference position is no jump; an arrival under way ends.
            _refKnown = false;
            _arrivedAt = -1f;
            _arriving = false;
            // Far.KeepAliveSlice: nothing of this scene is held or kept. Far.KeepAliveFast: its counts and free set go.
            _areasVersion++;
            DropSnapshot();
            _victims.Clear();
            _victimZdos.Clear();
            DropFast();
        }

        // ---- read only, for FarFreeze (Far.FreezeLists), FarCores (the far navmesh hooks) and FarHeightmaps -------

        // Moves whenever an area is made, released, taken back or gone, and when its zones are listed again (Far.FullAreaZones
        // switched while it stands).
        internal int AreasVersion => _areasVersion;

        // The i-th area (0..Count-1): its far portal, that portal's zone, whether it drains, and the zones it loads
        // (the list itself: not to be changed).
        internal ZDOID AreaPortal(int i) => _areas[i].Portal;
        internal Vector2s AreaZone(int i) => _areas[i].Zone;
        internal bool AreaDraining(int i) => _areas[i].Draining;
        internal List<Vector2s> AreaZones(int i) => _areas[i].Zones;

        // This game runs the i-th area's near objects (Claim took them: Plugin.LiveFarSide, its place around the portal
        // in). Read live: neither CoreReady nor a claim moves AreasVersion. Claimed is emptied as the area drains (with
        // Far.ClaimLikeGame as it leaves the areas; a draining one counts as not simulated here either way), and at its
        // next listing once LiveFarSide is off.
        internal bool AreaSimulated(int i)
        {
            Area a = _areas[i];
            return !a.Draining && Stands(a) && a.Claimed.Count > 0;
        }

        // Far.SleepBudget: the time spent turning areas this frame (TurnSome), out of the time a frame for it.
        internal double TurnedMs => _turnFrame == Time.frameCount ? _turnMs : 0.0;
        internal static double TurnBudgetMs => SleepBudgetMs;

        // ---- Docs\PARITY-PLAN.md §4 A5: for packages C and D (InFarActiveArea and IsAwakeZone are above) -------------------
        // Main thread; nothing allocated (the caller's collections grow once).

        // P14 (C's base view): the zones of the far areas that do not drain which the game does not load round the player (its
        // near zones round the reference position: the square, or outside classic the circle ZoneSystem.CreateLocalZones keeps
        // loaded). `into` is cleared first.
        internal void CollectFarOnlyZones(HashSet<Vector2s> into)
        {
            if (into == null)
                return;
            into.Clear();
            ZNet net = ZNet.instance;
            ZoneSystem zs = ZoneSystem.instance;
            if (net == null || zs == null)
                return;
            Vector2s me = ZoneSystem.GetZone(net.GetReferencePosition());
            SimulationDistance sim = net.GetSyncedSimulationDistance();
            int near = sim.NearSimulationDistance;
            foreach (Area a in _areas)
            {
                if (a.Draining)
                    continue;
                foreach (Vector2s z in a.Zones)
                {
                    int d = Math.Max(Math.Abs(z.x - me.x), Math.Abs(z.y - me.y));
                    if (d <= near && (sim.IsClassic || zs.ZonesWithinRadius(me, z, near)))
                        continue;
                    into.Add(z);
                }
            }
        }

        // P17, P18, P36 (D): the far areas awake now (not draining, not asleep: awake, waking or loading; as of this frame's
        // UpdateSight): its far portal's position and zone, how far its near zones reach (zones, Chebyshev), and whether a
        // window onto it was seen within the last 3 s. `into` is cleared first.
        internal void CollectAwakeCores(List<(Vector3 center, Vector2s zone, int nearRadius, bool watched)> into)
        {
            if (into == null)
                return;
            into.Clear();
            if (ZNet.instance == null)
                return;
            float now = Time.time;
            foreach (Area a in _areas)
            {
                if (AwakeState(a) < 2)
                    continue;
                bool watched = _watchedAt.TryGetValue(a.Portal, out float at) && now - at < 3f;
                into.Add((a.Center, a.Zone, a.ZoneReach, watched));
            }
        }

        // P17, P18, P36 (D): the far area of the far portal at this position (a CollectAwakeCores centre) stands as far as
        // the game's active area round that portal reaches: its place round the portal is in (CoreReady: its zones loaded,
        // no object of it pending, claimed) or all of it (Ready). Both reset as it drains and stay so when it is taken
        // back halfway through, until it stands again; so everything in that active area is there, as round a player.
        internal bool CoreStands(Vector3 farPortal)
        {
            foreach (Area a in _areas)
                if (!a.Draining && (a.Center - farPortal).sqrMagnitude < 1f)
                    return a.CoreReady || a.Ready;
            return false;
        }

        // P16, P17 (D): the far portal (its ZDO) of the far area whose active area holds the point (the game's rule for a
        // player standing at that far portal; of those that do not drain, the nearest; an edge area's less the player's
        // own), or null.
        internal ZDO AreaOf(Vector3 at) =>
            AreaOf(at, out Vector2s _, out Vector3 _, out ZDOID portal) && ZDOMan.instance != null ? ZDOMan.instance.GetZDO(portal) : null;

        // The same, with that area's far portal zone and position (the reference zone a player standing there would have).
        internal bool AreaOf(Vector3 at, out Vector2s zone, out Vector3 center, out ZDOID portal)
        {
            zone = default;
            center = default;
            portal = ZDOID.None;
            if (_areas.Count == 0 || ZNet.instance == null || ZoneSystem.instance == null)
                return false;
            EnsureActive();
            Area best = null;
            float bestD = float.MaxValue;
            foreach (Area a in _areas)
            {
                if (a.Draining || !Claims(a, at))
                    continue;
                float d = Utils.DistanceXZ(a.Center, at);
                if (d < bestD)
                {
                    bestD = d;
                    best = a;
                }
            }
            if (best == null)
                return false;
            zone = best.Zone;
            center = best.Center;
            portal = best.Portal;
            return true;
        }

        // ---- FarWorld proxies (Far.Proxies) ---------------------------------------------------------------------------
        // FarProxies (FarWorld\Proxy) decides which far objects are drawn as proxies instead of made: AddPending leaves out
        // an object whose proxy is built, CreateSome skips it (it counts as there, so readiness stands on it) or waits for
        // its proxy (counted missing until it is built). The lists and the keep-alive passes are as before: a proxied ZDO
        // has no instance for them to keep, and one made before (the switch, the game's own surroundings left behind) is
        // taken out by FarProxies' drain. The boundary is the claims' (Far.ClaimLikeGame): the active area of every area,
        // draining ones too (CollectActiveZones); inside it everything is made. When something proxied must be made after
        // all (FarProxies.RealizeVersion), every area is listed again, one a frame.

        int _proxyRealize;
        // CreateSome looks at no more objects waiting for their proxy than this a frame.
        const int ProxyWaitScan = 4096;

        // The far areas' active areas may have changed when this moves (an area made, taken back, released or gone).
        internal int ActiveAreasVersion => _areasVersion;

        // The zones of every area's far portal (draining ones keep their claims, so their active areas count too).
        internal void CollectActiveZones(List<Vector2s> into)
        {
            foreach (Area a in _areas)
                if (!a.Gone)
                    into.Add(a.Zone);
        }

        // The registry's sectors of the area round this far portal (null: none, it drains, or it lists itself).
        internal AreaSectors SectorsOf(ZDOID farPortal) =>
            _byPortal.TryGetValue(farPortal, out Area a) && !a.Draining && a.Sectors != null && a.Sectors.Live ? a.Sectors : null;

        // One area flagged to list again (RealizeVersion moved), a frame, in the areas' order.
        void ProxyRelistStep()
        {
            for (int i = 0; i < _areas.Count; i++)
            {
                Area a = _areas[i];
                if (!a.ProxyRelist)
                    continue;
                a.ProxyRelist = false;
                if (a.Draining)
                    continue;
                Relist(a);
                return;
            }
        }

        // ---- FarWorld shim (Far.V2 with Far.Registry) -----------------------------------------------------------------
        // FARWORLD-V3.md §7 V2 (ARCHITECTURE-v2.md §7 S4): the far areas make the same objects as before, from the lists of
        // FarWorld's SectorRegistry instead of their own. Each area that does not drain holds its sectors there (Acquire: as
        // ZDOMan.FindSectorObjects lists them around its far portal's zone; areas that overlap share them), and:
        //  - Relist collects its lists from the registry's rows (SectorRegistry.Collect: the same ZDOs; a sector the journal
        //    does not follow yet is read from the game's tables). While the journal follows every sector of the area and
        //    none of them changed since (AreaVersion), the lists stand as they are. What comes after them (pending, claims,
        //    ownership given up) runs as before, on the same pacing: LoadPacer, CoreFirst, DeferInteriors, ZoneSpawnSplit
        //    and the rest work on top unchanged.
        //  - The keep-alive keeps by sector: the full pass appends what the registry keeps, each ZDO once however many
        //    areas reach it (SectorRegistry.CollectKept), and Far.KeepAliveFast keeps a loaded object outside every area's
        //    lists that the registry keeps now (SectorRegistry.Keeps). So Track holds nothing: what is made in an area or
        //    carried into one is in its sectors at once. Draining areas give theirs up from Keep as before (DrainCalm).
        //  - The lively objects follow the engine's instance events (SectorInstanceIndexModule.InstanceAdded): what is made
        //    in an area, or carried into one (Track), joins its lively objects BornDelay after it came. The full look
        //    through each area (ScanLively), which still finds what walked in from elsewhere, runs every ShimLivelyEvery
        //    instead of every 30 s, and from the start whenever the events restart.
        //  - Asleep and awake by place: the areas of one AreaContext (far portals in the same zone or next to it, over the
        //    registry's shared sectors) are seen together (WatchMates), so a window onto one does not show what another
        //    froze; and what walked out of an area is not put to sleep with it (ShimHolds).
        // Off (Far.V2 or Far.Registry off, no registry, or after an error until a Far.* switch is set again or FarWorld
        // makes another registry): the areas list themselves exactly as before. Left while on, every area is listed again
        // at the next entry (what came since its last listing was kept by sector, not by Track). Nothing here writes a ZDO
        // or touches ownership (Claim is as before). Main thread only; nothing allocated per frame once the lists grew.

        // FarWorld (shim): what is made in an area joins its lively objects this long after it came (its attachments, such
        // as an item stand's visual a second after it wakes, are there by then), at most BornPerFrame a frame, with at most
        // BornMax waiting (more: every area is looked through again); the full look runs this seldom.
        const float BornDelay = 2f;
        const int BornPerFrame = 600;
        const int BornMax = 32768;
        const float ShimLivelyEvery = 120f;

        struct BornEntry
        {
            internal ZDO Zdo;
            internal ZDOID Id;
            internal float At;
        }

        // While the areas list from it: the registry, and the simulation distance they register with (AreaDistance).
        SectorRegistry _shimReg;
        SimulationDistance _shimSim;
        // Left after an error, until the switches or the registry differ from then; every area to be listed again.
        bool _shimBroken, _relistAll;
        int _shimBrokenKey;
        float _shimErrorAt;
        // What the engine's instance events made (and Track carried in), in order, and its head; more came than it holds;
        // the events' epoch it follows (0: none yet).
        readonly List<BornEntry> _born = new List<BornEntry>();
        int _bornAt, _bornEpoch;
        bool _bornHooked, _bornOverflow;
        Engine.SectorInstanceIndexModule.InstanceAddedHandler _bornHandler;
        readonly List<Area> _mates = new List<Area>();

        // Lab: listings collected from the registry and those whose lists stood; full keep-alive passes kept by sector,
        // loaded objects the fast pass kept by sector (in place of Track); lively: objects queued (made or carried in),
        // joined to an area, dropped (the queue full), full looks restarted; starts, stops, every area listed again, errors.
        internal static long LabShimCollected, LabShimStood, LabShimKeptPasses, LabShimKept, LabShimBorn, LabShimJoined, LabShimDropped;
        internal static int LabShimRescans, LabShimStarts, LabShimStops, LabShimRelistAll, LabShimErrors;

        // The areas list from FarWorld's registry now.
        internal bool OnFarWorld => _shimReg != null;

        // The i-th area holds its sectors in the registry.
        internal bool AreaOnRegistry(int i) => _shimReg != null && OnRegistry(_areas[i], _shimReg);

        static int ShimKey => (FarFlags.V2 ? 1 : 0) | (FarFlags.Registry ? 2 : 0) | (FarFlags.Journal ? 4 : 0) | (FarWorld.Generation << 3);

        // At every entry (Update, the keep-alive passes): on or off the registry as the switches and FarWorld say. Not
        // from inside a listing or a creation (Track): those use what was decided here.
        void ShimSync()
        {
            try
            {
                if (_shimBroken && ShimKey != _shimBrokenKey)
                    _shimBroken = false;
                SectorRegistry reg = FarWorld.RegistryOn && !_shimBroken && ZNet.instance != null ? FarWorld.Registry : null;
                if (!ReferenceEquals(reg, _shimReg))
                {
                    if (_shimReg != null)
                        ShimStop(reg == null);
                    if (reg != null)
                        ShimStart(reg);
                }
                else if (reg != null)
                    // The simulation distance may change (a server's, Plugin.FarAreaZones): each area registers again with
                    // it at its next listing (ShimOf), as it lists with it from then on without the registry.
                    _shimSim = AreaDistance();
            }
            catch (Exception e)
            {
                ShimFailed(e);
            }
            if (_relistAll)
                RelistAll();
        }

        void ShimStart(SectorRegistry reg)
        {
            _shimReg = reg;
            _shimSim = AreaDistance();
            foreach (Area a in _areas)
                if (!a.Draining)
                    Acquire(a);
            if (_shimReg == null)
                return;
            HookBorn();
            LabShimStarts++;
            Plugin.Log.LogInfo($"RemoteAreas: far areas list from FarWorld's registry ({_areas.Count} area(s), generation {reg.Generation})");
        }

        // listAgain: back to listing on their own (not the scene going, nor another registry following at once).
        void ShimStop(bool listAgain)
        {
            _shimReg = null;
            foreach (Area a in _areas)
            {
                AreaSectors s = a.Sectors;
                a.Sectors = null;
                a.ListedVersion = -1L;
                if (s == null || !s.Live)
                    continue;
                try
                {
                    s.Registry.Release(s);
                }
                catch (Exception e)
                {
                    if (LabShimErrors++ < 5)
                        Plugin.Log.LogError("RemoteAreas (FarWorld): giving the sectors back: " + e);
                }
            }
            UnhookBorn();
            // What came into an area since its last listing was kept by sector, not by Track: every area is listed again,
            // at the next entry (not inside whatever left the registry).
            if (listAgain && _areas.Count > 0)
                _relistAll = true;
            LabShimStops++;
            Plugin.Log.LogInfo("RemoteAreas: far areas list themselves" + (listAgain && _areas.Count > 0 ? $" again ({_areas.Count} area(s) listed at once)" : ""));
        }

        // An exception on the registry's side of a call: logged (in full at most every 10 s), and the areas list themselves
        // until a Far.* switch is set again (or FarWorld makes another registry).
        void ShimFailed(Exception e)
        {
            LabShimErrors++;
            if (Time.unscaledTime >= _shimErrorAt)
            {
                _shimErrorAt = Time.unscaledTime + 10f;
                Plugin.Log.LogError($"RemoteAreas (FarWorld): error {LabShimErrors}, far areas list themselves until a Far.* switch is set again: {e}");
            }
            _shimBroken = true;
            _shimBrokenKey = ShimKey;
            if (_shimReg != null)
                ShimStop(true);
        }

        // Every area listed on its own again, after the registry was left.
        void RelistAll()
        {
            _relistAll = false;
            if (ZDOMan.instance == null || ZNet.instance == null || ZoneSystem.instance == null)
                return;
            int n = 0;
            foreach (Area a in _areas)
                if (!a.Draining)
                {
                    Relist(a);
                    n++;
                }
            LabShimRelistAll++;
            Plugin.Debug($"RemoteAreas: {n} far area(s) listed again on their own");
        }

        // Its sectors registered with the registry the areas list from (nothing without one, or while it drains); its lists
        // are collected afresh at its next listing.
        void Acquire(Area a)
        {
            a.ListedVersion = -1L;
            SectorRegistry reg = _shimReg;
            if (reg == null || a.Draining || (a.Sectors != null && a.Sectors.Live && ReferenceEquals(a.Sectors.Registry, reg)))
                return;
            try
            {
                a.Sectors = reg.Acquire(a.Zone, _shimSim, a);
            }
            catch (Exception e)
            {
                a.Sectors = null;
                ShimFailed(e);
            }
        }

        void ReleaseSectors(Area a)
        {
            AreaSectors s = a.Sectors;
            a.Sectors = null;
            a.ListedVersion = -1L;
            if (s == null || !s.Live)
                return;
            try
            {
                s.Registry.Release(s);
            }
            catch (Exception e)
            {
                ShimFailed(e);
            }
        }

        static bool OnRegistry(Area a, SectorRegistry reg) => a.Sectors != null && a.Sectors.Live && ReferenceEquals(a.Sectors.Registry, reg);

        // The registry this area lists from now (its sectors registered there with the simulation distance in use, again
        // when either changed), or null: it lists itself.
        SectorRegistry ShimOf(Area a)
        {
            SectorRegistry reg = _shimReg;
            if (reg == null || a.Draining)
                return null;
            AreaSectors s = a.Sectors;
            if (s == null || !s.Live || !ReferenceEquals(s.Registry, reg) || !s.Sim.Equals(_shimSim))
            {
                ReleaseSectors(a);
                Acquire(a);
                s = a.Sectors;
            }
            return s != null && s.Live && ReferenceEquals(_shimReg, reg) ? reg : null;
        }

        // The registry's version of the area's sectors while the journal follows every one of them (else -1: collect).
        long ShimVersion(SectorRegistry reg, Area a)
        {
            try
            {
                return reg.Followed(a.Sectors) ? reg.AreaVersion(a.Sectors) : -1L;
            }
            catch (Exception e)
            {
                ShimFailed(e);
                return -1L;
            }
        }

        // The area's lists (empty before) from the registry; false when it cannot give them (then it lists itself).
        bool ShimCollect(SectorRegistry reg, Area a)
        {
            AreaSectors s = a.Sectors;
            if (s == null || !s.Live || !ReferenceEquals(_shimReg, reg))
                return false;
            try
            {
                reg.Collect(s, a.Near, a.Distant);
                LabShimCollected++;
                return true;
            }
            catch (Exception e)
            {
                a.Near.Clear();
                a.Distant.Clear();
                ShimFailed(e);
                return false;
            }
        }

        // The full keep-alive pass: every ZDO the registry keeps (all of a sector some area has whole, the Distant ones of
        // one in rings only), each once. Should the registry fail, the areas' own lists instead.
        void KeepBySector(SectorRegistry reg, List<ZDO> near)
        {
            int start = near.Count;
            try
            {
                reg.CollectKept(near);
                LabShimKeptPasses++;
            }
            catch (Exception e)
            {
                near.RemoveRange(start, near.Count - start);
                ShimFailed(e);
                foreach (Area a in _areas)
                    if (!a.Draining)
                    {
                        near.AddRange(a.Near);
                        near.AddRange(a.Distant);
                    }
            }
        }

        // Whether the registry keeps this ZDO now; on an error it is kept this once and the registry left.
        bool RegistryKeeps(SectorRegistry reg, ZDO z)
        {
            try
            {
                return reg.Keeps(z);
            }
            catch (Exception e)
            {
                ShimFailed(e);
                return true;
            }
        }

        // The area lists this ZDO, as ZDOMan.FindSectorObjects lists around its zone (everything of its near zones, the
        // Distant ones of its ring), by the sector it is in now.
        bool Holds(Area a, ZDO z)
        {
            ZoneSystem zs = ZoneSystem.instance;
            if (z == null || zs == null)
                return false;
            Vector2s s = z.GetSector();
            SimulationDistance sim = _shimSim;
            int cheb = Math.Max(Math.Abs(s.x - a.Zone.x), Math.Abs(s.y - a.Zone.y)), near = sim.NearSimulationDistance;
            if (cheb <= near && (cheb == 0 || sim.IsClassic || zs.ZonesWithinRadius(a.Zone, s, near)))
                return true;
            int total = sim.TotalSimulationDistance;
            return z.Distant && cheb <= total && (sim.IsClassic || zs.ZonesWithinRadius(a.Zone, s, total, ghostZone: true));
        }

        // A lively object found in the area is still in it (one that walked out stays as it is when the area sleeps).
        bool ShimHolds(Area a, ZNetView v) => Holds(a, v.GetZDO());

        // ---- the lively objects by the engine's instance events

        void HookBorn()
        {
            if (_bornHooked)
                return;
            _bornHandler ??= OnBorn;
            Engine.SectorInstanceIndexModule.InstanceAdded += _bornHandler;
            _bornHooked = true;
            _bornEpoch = 0;
        }

        void UnhookBorn()
        {
            if (_bornHooked)
            {
                Engine.SectorInstanceIndexModule.InstanceAdded -= _bornHandler;
                _bornHooked = false;
            }
            _born.Clear();
            _bornAt = 0;
            _bornOverflow = false;
            _bornEpoch = 0;
        }

        // The engine's InstanceAdded, inside ZNetView.Awake: only recorded (looked at BornDelay later, TakeBorn).
        void OnBorn(ZDO zdo, ZNetView view)
        {
            if (_shimReg != null && zdo != null && _areas.Count > 0)
                Enqueue(zdo, Time.time);
        }

        void Enqueue(ZDO z, float at)
        {
            if (_born.Count - _bornAt >= BornMax)
            {
                LabShimDropped++;
                _bornOverflow = true;
                return;
            }
            _born.Add(new BornEntry { Zdo = z, Id = z.m_uid, At = at });
            LabShimBorn++;
        }

        // The events follow the game's table and nothing was missed since they last did: the queue holds what was made.
        // When they (re)start, or more came than the queue holds, every area is looked through again from the start.
        bool BornLive()
        {
            if (!Engine.SectorInstanceIndexModule.InstanceEventsLive)
            {
                _bornEpoch = 0;
                return false;
            }
            int epoch = Engine.SectorInstanceIndexModule.InstanceEpoch;
            if (epoch == _bornEpoch && !_bornOverflow)
                return true;
            _bornEpoch = epoch;
            _bornOverflow = false;
            _born.Clear();
            _bornAt = 0;
            foreach (Area a in _areas)
            {
                a.ScanAt = 0;
                a.LivelyNext.Clear();
                a.ScannedAt = -100f;
            }
            LabShimRescans++;
            return true;
        }

        // What is due (BornDelay old), BornPerFrame a frame: each joins the lively objects of every area that lists it and
        // was looked through already (its first look finds it otherwise), when it is lively.
        void TakeBorn(ZNetScene scene)
        {
            float now = Time.time;
            int left = BornPerFrame;
            while (_bornAt < _born.Count && left > 0)
            {
                BornEntry e = _born[_bornAt];
                if (now - e.At < BornDelay)
                    break;
                _bornAt++;
                left--;
                Join(scene, e.Zdo, e.Id);
            }
            if (_bornAt >= _born.Count)
            {
                _born.Clear();
                _bornAt = 0;
            }
            else if (_bornAt >= 4096 && _bornAt * 2 >= _born.Count)
            {
                _born.RemoveRange(0, _bornAt);
                _bornAt = 0;
            }
        }

        void Join(ZNetScene scene, ZDO z, ZDOID id)
        {
            if (scene == null || z == null || z.m_uid != id)
                return;
            ZNetView v = scene.FindInstance(z);
            if (v == null)
                return;
            int lively = 0;
            foreach (Area a in _areas)
            {
                if (a.Draining || !Stands(a) || (a.ScannedAt < 0f && a.ScanAt == 0) || !Holds(a, z))
                    continue;
                if (lively == 0)
                    lively = IsLively(v) ? 1 : -1;
                if (lively < 0)
                    return;
                a.Lively.Add(v);
                // In the middle of a look: the list it makes holds it too.
                if (a.ScanAt > 0)
                    a.LivelyNext.Add(v);
                LabShimJoined++;
            }
        }

        // ---- asleep and awake by place

        // The other areas (not draining) of this one's AreaContext, into _mates; false when there are none.
        bool Mates(Area a)
        {
            _mates.Clear();
            FarArea f = FarAreas.Get(a.Portal);
            AreaContext c = f != null ? f.Context : null;
            if (c == null || c.Areas.Count < 2)
                return false;
            for (int i = 0; i < c.Areas.Count; i++)
            {
                ZDOID portal = c.Areas[i].Portal;
                if (portal != a.Portal && _byPortal.TryGetValue(portal, out Area m) && !m.Draining)
                    _mates.Add(m);
            }
            return _mates.Count > 0;
        }

        // What a window shows of one area's place the others of its context hold as well (the same objects there): they
        // count as seen with it, and with wake those asleep start waking now (SleepBudget: a part at a time). True when
        // one was set waking or woken.
        bool WatchMates(Area a, float now, bool wake)
        {
            if (!Mates(a))
                return false;
            bool woke = false;
            foreach (Area m in _mates)
            {
                _watchedAt[m.Portal] = now;
                if (!wake || !_paused.ContainsKey(m.Portal))
                    continue;
                if (!FarFlags.SleepBudget)
                {
                    Wake(m);
                    woke = true;
                }
                else if (m.Turn >= 0)
                {
                    m.Turn = -1;
                    woke = true;
                }
            }
            return woke;
        }

        // ---- lab

        // Lab and log: the shim in one line.
        internal string DescribeShim() =>
            $"far world shim: {(_shimReg != null ? $"on (generation {_shimReg.Generation})" : _shimBroken ? "off after an error" : "off")}, " +
            $"listings from the registry {LabShimCollected} (lists stood {LabShimStood}), full keep-alive passes by sector {LabShimKeptPasses}, " +
            $"kept by sector in the fast pass {LabShimKept}; lively: {LabShimBorn} made or carried in, {LabShimJoined} joined, " +
            $"{LabShimDropped} dropped, {LabShimRescans} looks restarted, {_born.Count - _bornAt} waiting; " +
            $"started {LabShimStarts}, stopped {LabShimStops}, every area listed again {LabShimRelistAll}, errors {LabShimErrors}";

        // Lab (the V2 gate's legacy-set test): each area on the registry against ZDOMan.FindSectorObjects now (its vanilla
        // ghosts left out: stale portal-list entries the rows never list, SectorRegistry.LabDropGhosts): the registry's
        // lists for it (SectorRegistry.LabCompare, nothing in flight: must match), its own lists where they stood since they
        // were collected (the registry's version did not move: must match too; otherwise they lag by what came since, as a
        // relisted area's did), and of the game's list there, what is not created though it is to be (not pending, not
        // deferred, not failed to make: 0 for a ready area whose lists are current). One line per area into sb (when given);
        // returns the areas found different. Allocates (lab only).
        internal int LabCompareSets(System.Text.StringBuilder sb, out int compared)
        {
            compared = 0;
            SectorRegistry reg = _shimReg;
            if (reg == null || ZDOMan.instance == null)
                return 0;
            var near = new List<ZDO>();
            var distant = new List<ZDO>();
            var pending = new HashSet<ZDO>(ZdoRef.Instance);
            int different = 0;
            foreach (Area a in _areas)
            {
                if (a.Draining || !OnRegistry(a, reg))
                    continue;
                compared++;
                bool same = reg.LabCompare(a.Sectors, out int nearMissing, out int nearExtra, out int distantMissing, out int distantExtra, out int ghosts);
                long version = reg.Followed(a.Sectors) ? reg.AreaVersion(a.Sectors) : -1L;
                bool current = version >= 0 && version == a.ListedVersion;
                near.Clear();
                distant.Clear();
                ZDOMan.instance.FindSectorObjects(a.Zone, a.Sectors.Sim, near, distant);
                reg.LabDropGhosts(a.Sectors, near);
                int listsMissing = 0, listsExtra = 0;
                if (current)
                {
                    listsMissing = NotIn(a.Near, near) + NotIn(a.Distant, distant);
                    listsExtra = NotIn(near, a.Near) + NotIn(distant, a.Distant);
                }
                pending.Clear();
                foreach (ZDO z in a.Pending)
                    if (z != null)
                        pending.Add(z);
                bool deferInteriors = FarFlags.DeferInteriors && a.Center.y < InteriorHeight;
                int due = Due(near, pending, deferInteriors) + Due(distant, pending, deferInteriors);
                bool ok = same && (!current || (listsMissing + listsExtra == 0 && (!a.Ready || due == 0)));
                if (!ok)
                    different++;
                sb?.Append($"\n  portal {a.Portal} ({(a.Ready ? "ready" : a.CoreReady ? "core" : "loading")}): registry {(same ? "same" : "DIFFERENT")} " +
                           $"(near -{nearMissing} +{nearExtra}, distant -{distantMissing} +{distantExtra}, vanilla ghosts left out {ghosts}); " +
                           $"lists {(current ? $"current, -{listsMissing} +{listsExtra}" : "behind by what came since")}; " +
                           $"{near.Count + distant.Count} listed by the game, {due} not created though due, {a.Pending.Count} pending, {a.Deferred} deferred");
            }
            return different;
        }

        // How many of `of` are not in `inside` (as sets).
        static int NotIn(List<ZDO> inside, List<ZDO> of)
        {
            var set = new HashSet<ZDO>(inside, ZdoRef.Instance);
            int n = 0;
            foreach (ZDO z in of)
                if (!set.Contains(z))
                    n++;
            return n;
        }

        int Due(List<ZDO> list, HashSet<ZDO> pending, bool deferInteriors)
        {
            int n = 0;
            foreach (ZDO z in list)
                if (!z.Created && !_bad.Contains(z) && !pending.Contains(z) && !(deferInteriors && z.GetPosition().y > InteriorHeight))
                    n++;
            return n;
        }

        // Lab (the same-objects test between runs, Far.V2 off against on): of what ZDOMan.FindSectorObjects lists around the
        // i-th area's far portal now, how many there are, how many are created, and an order-free hash of the created ones'
        // ids (equal for two runs that created the same objects there).
        internal void LabCensus(int i, out int listed, out int created, out ulong hash)
        {
            listed = created = 0;
            hash = 0;
            if (ZDOMan.instance == null || ZNet.instance == null || i < 0 || i >= _areas.Count)
                return;
            var near = new List<ZDO>();
            var distant = new List<ZDO>();
            ZDOMan.instance.FindSectorObjects(_areas[i].Zone, AreaDistance(), near, distant);
            listed = near.Count + distant.Count;
            foreach (List<ZDO> list in new[] { near, distant })
                foreach (ZDO z in list)
                {
                    if (!z.Created)
                        continue;
                    created++;
                    ulong k = (ulong)z.m_uid.UserID * 0x9E3779B97F4A7C15UL ^ (ulong)z.m_uid.ID * 0xC2B2AE3D27D4EB4FUL;
                    k ^= k >> 31;
                    hash += k * 0x94D049BB133111EBUL;
                }
        }

        // Lab (the V2 gate's idle area <= 1 MB, an estimate): what the i-th area holds here (its zones, lists, claims, lively
        // objects, sleep and drain lists) plus, while it is on the registry, its share of the registry's rows and records.
        internal long AreaBytes(int i)
        {
            Area a = _areas[i];
            long n = 256 + 4L * a.Zones.Capacity + 28L * a.Claimed.Count
                     + 8L * (a.Near.Capacity + a.Distant.Capacity + a.Pending.Capacity + a.Lively.Capacity + a.LivelyNext.Capacity + a.ClaimSet.Capacity
                             + (a.Keep?.Capacity ?? 0) + (a.PausedList?.Capacity ?? 0) + (a.FrozenList?.Capacity ?? 0));
            SectorRegistry reg = _shimReg;
            if (reg != null && OnRegistry(a, reg))
                n += reg.AreaBytes(a.Sectors);
            return n;
        }
    }
}
