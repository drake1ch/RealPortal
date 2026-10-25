using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace RealPortal
{
    // Win.BaseHidesFar (Docs\PARITY-PLAN.md P14.1). The far areas are loaded for the windows, and the game camera drew them
    // too: looking toward a far area 150 m to 1.5 km away, the base view showed its full heightmaps over TerrainLod, its
    // water, fires and lit torches the game would not have loaded, and its objects out past the vanilla loading edge
    // (Win.LayerCull cuts object layers at the farthest the game ever loads round the camera, not by what it loaded). Now
    // the game camera sees only what the game loaded. Right before it culls (PortalSystem.MainPreCull, after the window
    // batch) and back at its onPostRender, in the zones of far areas the game does not load round the player (those of
    // areas that drain too, and each until the game has unloaded it):
    //  - their Heightmap and WaterVolume renderers are hidden (forceRenderingOff): the game's TerrainLod and distant water
    //    show there, as in the game;
    //  - the LightLod lights of objects the game would not have are switched off, whatever the view (they light what is in
    //    it);
    //  - the particle renderers of their lively objects (RemoteAreas.InstancesNear) the game would not have are hidden;
    //  - within the game camera's layer-cull radius (WindowLayerCull.Radius; beyond it their object layers are cut anyway),
    //    the renderers of the objects the game would not have: near objects outside the player's near zones, distant ones
    //    outside his distant ring (ZDOMan.FindSectorObjects' rule, by which ZNetScene creates them).
    // And the game's own non-instanced clutter grown round a far point (children of ClutterSystem's grass root past the
    // circle it grows round the player); instanced grass is FarGrassSubmit's (P14.2). And the smoke puffs only a window's
    // place made (Far.Viewer's far puffs, FarViewer.FarPuffs).
    // Only what the camera can draw is touched: a zone in its view or within its shadow distance (it may cast into the
    // view), and of an object in a LOD group the level the camera picks (and its neighbours, for the cross-fade), none
    // when the camera's LOD culls the group. A renderer or light already off stays as it is, and only what was switched
    // here is switched back. Lists: the zones whenever the areas, the player's zone or the simulation distance change, and
    // every second; the ground, lights, lively objects every second (at once after a change), the clutter a slice a frame;
    // a zone's objects (within the radius) every 2 s and with the areas' version, within 0.25 ms a frame, nearest in view
    // first, each object's renderers and LOD groups looked up once (again after 10 s). Not in free fly (as Win.LayerCull),
    // and not while the camera looks at a far side (through a portal, or carried there by the lab's in-place reference
    // while the windows are suspended). Off: nothing is touched.
    internal static class BaseView
    {
        // Lab: renderers and lights hidden from the game camera at its last render; far-only zones now, those in its view;
        // objects listed so far.
        internal static int LabBaseHidden, LabZones, LabZonesInView;
        internal static long LabListed;

        // Zones indexed as FarGrassGate does (ZoneSystem.SectorToIndex without its fold of the world's outside onto 0).
        const int ZoneSpan = 512;
        // What stands in a zone reaches this far past its edges (crowns, buildings), and this high over its ground.
        const float ZoneMargin = 16f, AboveGround = 96f, BelowGround = 32f;
        // The listing: objects a frame at least, its time a frame, how often a zone's objects and an object's renderers are
        // looked up again, clutter children looked at a frame.
        const int ListAtLeast = 8, ClutterPerFrame = 256;
        const double ListBudgetMs = 0.25;
        const float ZoneRelist = 2f, ViewRelist = 10f;
        // A LOD group counts as drawn down to this share of its last level's threshold (Unity's measure, the cross-fade).
        const float LodMargin = 0.75f;
        const int Culled = -100;

        sealed class Zone
        {
            public Vector2s Id;
            public int Index, Mark;
            // The ground's height range (its renderers' bounds), once listed.
            public float MinY, MaxY;
            public bool HasGround;
            public readonly List<Renderer> Ground = new List<Renderer>();
            // The objects in force, and those being listed (swapped in when done), from a snapshot of its sector.
            public List<Item> Items = new List<Item>(), Next = new List<Item>();
            public readonly List<ZDO> Snapshot = new List<ZDO>();
            public int Cursor = -1, ListedVersion = int.MinValue;
            public float ListedAt = float.NegativeInfinity;
            // This frame: the game has its near objects (never, for a far-only zone) or its distant ones; the game camera
            // may draw it; within the cull radius; its distance from the camera.
            public bool Near, Ring, InView, Close;
            public float Distance;

            public void Reset(Vector2s id, int index)
            {
                Id = id;
                Index = index;
                HasGround = false;
                Cursor = -1;
                ListedVersion = int.MinValue;
                ListedAt = float.NegativeInfinity;
            }

            public void Clear()
            {
                Ground.Clear();
                Items.Clear();
                Next.Clear();
                Snapshot.Clear();
                Cursor = -1;
            }
        }

        struct Item
        {
            public Parts Parts;
            public bool Distant;
        }

        // An object's renderers, each with its LOD group and level (-1: in none, drawn whenever the object is), the groups'
        // reference points, world sizes and transition heights; for a moving one (a character, a body) where it is now.
        sealed class Parts
        {
            public ZNetView View;
            public Transform Root;
            public Renderer[] Renderers;
            public sbyte[] Level;
            public short[] Group;
            public Vector3[] GroupAt;
            public float[] GroupSize;
            public float[][] Heights;
            public bool Mover;
            public float ListedAt;
        }

        struct Lively
        {
            public ParticleSystemRenderer[] Renderers;
            public Transform Root;
            public int Index;
            public bool Distant;
        }

        // Win.CrossingBodies (PortalSystem.CrossingToGame, P23): the roots of the bodies the game camera draws carried
        // through an opening this frame (its own copy of a body over there, as the window beside it shows the rest): never
        // hidden here, wherever the body itself stands. Filled before Hide, emptied after Show.
        internal static readonly HashSet<Transform> Crossing = new HashSet<Transform>();

        struct LightEntry
        {
            public Light Light;
            public int Index;
            public bool Distant;
        }

        static int[] s_zoneOf;
        static readonly List<Zone> s_zones = new List<Zone>();
        static readonly Stack<Zone> s_pool = new Stack<Zone>();
        static readonly HashSet<Vector2s> s_set = new HashSet<Vector2s>();
        static int s_mark, s_version = int.MinValue, s_near = -1, s_total = -1;
        static bool s_classic;
        static Vector2s s_me;
        static float s_zonesAt, s_listsAt, s_sweepAt, s_errorAt;
        static readonly List<LightEntry> s_lights = new List<LightEntry>();
        static readonly List<Lively> s_lively = new List<Lively>();
        static List<Renderer> s_clutter = new List<Renderer>(), s_clutterNext = new List<Renderer>();
        static int s_clutterCursor;
        // Where the player stood at the last clutter slice.
        static Vector3 s_clutterFrom;
        static bool s_clutterHas;
        static readonly Dictionary<ZNetView, Parts> s_parts = new Dictionary<ZNetView, Parts>();
        static readonly Dictionary<ZNetView, ParticleSystemRenderer[]> s_particlesOf = new Dictionary<ZNetView, ParticleSystemRenderer[]>();
        static readonly Dictionary<LightLod, bool> s_lightDistant = new Dictionary<LightLod, bool>();
        // Switched off here, to switch back.
        static readonly List<Renderer> s_hidden = new List<Renderer>();
        static readonly List<Light> s_off = new List<Light>();
        static readonly Plane[] s_planes = new Plane[6];
        static int[] s_level = new int[16];
        static readonly List<Renderer> s_renderers = new List<Renderer>();
        static readonly List<LODGroup> s_groups = new List<LODGroup>();
        static readonly List<ParticleSystemRenderer> s_particles = new List<ParticleSystemRenderer>();
        static readonly List<ZNetView> s_views = new List<ZNetView>();
        static readonly List<Vector3> s_points = new List<Vector3>();
        static readonly Dictionary<Renderer, int> s_lodOf = new Dictionary<Renderer, int>();
        static readonly List<ZNetView> s_gone = new List<ZNetView>();
        static readonly ParticleSystemRenderer[] s_noParticles = new ParticleSystemRenderer[0];
        static readonly System.Diagnostics.Stopwatch s_watch = new System.Diagnostics.Stopwatch();

        static readonly AccessTools.FieldRef<ZDOMan, List<ZDO>[]> s_objectsBySector =
            AccessTools.FieldRefAccess<ZDOMan, List<ZDO>[]>("m_objectsBySector");
        static readonly AccessTools.FieldRef<Heightmap, MeshRenderer> s_heightmapRenderer =
            AccessTools.FieldRefAccess<Heightmap, MeshRenderer>("m_meshRenderer");
        static readonly AccessTools.FieldRef<HashSet<LightLod>> s_lodLights =
            AccessTools.StaticFieldRefAccess<HashSet<LightLod>>(AccessTools.Field(typeof(LightLod), "m_lights"));
        static readonly AccessTools.FieldRef<LightLod, Light> s_lodLight = AccessTools.FieldRefAccess<LightLod, Light>("m_light");
        static readonly AccessTools.FieldRef<ClutterSystem, GameObject> s_grassRoot = AccessTools.FieldRefAccess<ClutterSystem, GameObject>("m_grassRoot");
        static readonly AccessTools.FieldRef<SmokeRenderer, Dictionary<Vector3Int, List<Smoke>>> s_smokeChunks =
            AccessTools.FieldRefAccess<SmokeRenderer, Dictionary<Vector3Int, List<Smoke>>>("m_chunkedSmoke");
        static readonly AccessTools.FieldRef<SmokeRenderer, Dictionary<Vector3Int, ParticleSystem>> s_smokeSystems =
            AccessTools.FieldRefAccess<SmokeRenderer, Dictionary<Vector3Int, ParticleSystem>>("m_chunkedParticleSystems");

        // PortalSystem.MainPreCull, the game camera, after the window batch: what the game did not load goes from its view
        // (back at Show). Not while `through` (the camera looks at a far side this frame).
        internal static void Hide(PortalSystem sys, Camera cam, bool through)
        {
            if (s_hidden.Count > 0 || s_off.Count > 0)
                Show();
            LabBaseHidden = 0;
            if (!WindowFlags.BaseHidesFar || sys == null || cam == null || Plugin.Enabled == null || !Plugin.Enabled.Value || ZNet.instance == null
                || ZoneSystem.instance == null || ZDOMan.instance == null || ZNetScene.instance == null || GameCamera.InFreeFly()
                || (sys.Areas.Count == 0 && s_zones.Count == 0))
            {
                if (s_zones.Count > 0 || s_clutter.Count > 0)
                    Forget();
                return;
            }
            if (through)
                return;
            try
            {
                RemoteAreas areas = sys.Areas;
                float now = Time.unscaledTime;
                Zones(areas, now);
                if (now >= s_listsAt)
                {
                    s_listsAt = now + 1f;
                    ListGround();
                    ListLights();
                    ListLively(areas);
                }
                ListClutter();
                if (now >= s_sweepAt)
                {
                    s_sweepAt = now + 10f;
                    Sweep();
                }
                View(cam);
                ListObjects(areas.AreasVersion, now);
                Apply(cam);
                LabBaseHidden = s_hidden.Count + s_off.Count;
            }
            catch (System.Exception e)
            {
                Show();
                if (Time.unscaledTime >= s_errorAt)
                {
                    s_errorAt = Time.unscaledTime + 10f;
                    Plugin.Log.LogWarning("Win.BaseHidesFar: hiding the far areas from the game camera failed: " + e);
                }
            }
        }

        // The game camera's onPostRender (PortalSystem.MainPostRender; also before the next window batch, should it not
        // have come): everything switched off here back on.
        internal static void Show()
        {
            for (int i = 0; i < s_hidden.Count; i++)
            {
                Renderer r = s_hidden[i];
                if (r != null)
                    r.forceRenderingOff = false;
            }
            s_hidden.Clear();
            for (int i = 0; i < s_off.Count; i++)
            {
                Light l = s_off[i];
                if (l != null)
                    l.enabled = true;
            }
            s_off.Clear();
        }

        // PortalSystem.OnDestroy.
        internal static void Clear()
        {
            Forget();
            s_parts.Clear();
            s_particlesOf.Clear();
            s_lightDistant.Clear();
        }

        // No far area, the switch off, free fly: everything back, the lists empty.
        static void Forget()
        {
            Show();
            for (int i = s_zones.Count - 1; i >= 0; i--)
                Drop(i);
            s_lights.Clear();
            s_lively.Clear();
            s_clutter.Clear();
            s_clutterNext.Clear();
            s_clutterCursor = 0;
            s_clutterHas = false;
            s_version = int.MinValue;
            s_listsAt = 0f;
            LabZones = LabZonesInView = 0;
        }

        static int Index(int x, int y)
        {
            uint ux = (uint)(x + ZoneSpan / 2), uy = (uint)(y + ZoneSpan / 2);
            return ux < (uint)ZoneSpan && uy < (uint)ZoneSpan ? (int)(uy * (uint)ZoneSpan + ux) : -1;
        }

        static Zone ZoneAt(int index)
        {
            int at = index >= 0 && s_zoneOf != null ? s_zoneOf[index] - 1 : -1;
            return at >= 0 ? s_zones[at] : null;
        }

        static Zone ZoneAt(Vector3 point)
        {
            Vector2s z = ZoneSystem.GetZone(point);
            return ZoneAt(Index(z.x, z.y));
        }

        // The far-only zones as they stand: again when the areas, the player's zone or the simulation distance changed (the
        // lists at once then), else every second.
        static void Zones(RemoteAreas areas, float now)
        {
            ZNet net = ZNet.instance;
            Vector2s me = ZoneSystem.GetZone(net.GetReferencePosition());
            SimulationDistance sim = net.GetSyncedSimulationDistance();
            bool changed = areas.AreasVersion != s_version || me != s_me || sim.NearSimulationDistance != s_near
                           || sim.TotalSimulationDistance != s_total || sim.IsClassic != s_classic;
            if (!changed && now < s_zonesAt)
                return;
            s_zonesAt = now + 1f;
            if (changed)
                s_listsAt = 0f;
            s_version = areas.AreasVersion;
            s_me = me;
            s_near = sim.NearSimulationDistance;
            s_total = sim.TotalSimulationDistance;
            s_classic = sim.IsClassic;
            // Every area's zones, those that drain too (RemoteAreas.CollectFarOnlyZones leaves them out): a drain takes
            // seconds and the game unloads its zones one by one after it, and all that time they stand in the view.
            s_set.Clear();
            for (int i = 0; i < areas.Count; i++)
            {
                List<Vector2s> zones = areas.AreaZones(i);
                for (int j = 0; j < zones.Count; j++)
                    if (!NearHas(zones[j]))
                        s_set.Add(zones[j]);
            }
            if (s_zoneOf == null)
                s_zoneOf = new int[ZoneSpan * ZoneSpan];
            s_mark++;
            foreach (Vector2s z in s_set)
            {
                int k = Index(z.x, z.y);
                if (k < 0)
                    continue;
                Zone zone = ZoneAt(k);
                if (zone == null)
                {
                    zone = s_pool.Count > 0 ? s_pool.Pop() : new Zone();
                    zone.Reset(z, k);
                    s_zones.Add(zone);
                    s_zoneOf[k] = s_zones.Count;
                }
                zone.Mark = s_mark;
                zone.Near = NearHas(z);
                zone.Ring = RingHas(z);
            }
            s_set.Clear();
            // A zone no area has any more stays until the game unloads it (or it becomes one of the player's near zones).
            ZoneSystem zs = ZoneSystem.instance;
            for (int i = s_zones.Count - 1; i >= 0; i--)
            {
                Zone zone = s_zones[i];
                if (zone.Mark == s_mark)
                    continue;
                if (zs.IsZoneLoaded(zone.Id) && !NearHas(zone.Id))
                {
                    zone.Mark = s_mark;
                    zone.Near = false;
                    zone.Ring = RingHas(zone.Id);
                    continue;
                }
                Drop(i);
            }
            LabZones = s_zones.Count;
        }

        static void Drop(int i)
        {
            Zone zone = s_zones[i];
            s_zoneOf[zone.Index] = 0;
            int last = s_zones.Count - 1;
            if (i != last)
            {
                Zone moved = s_zones[last];
                s_zones[i] = moved;
                s_zoneOf[moved.Index] = i + 1;
            }
            s_zones.RemoveAt(last);
            zone.Clear();
            s_pool.Push(zone);
        }

        // ZDOMan.FindSectorObjects round the player's zone: every object of his near zones (the square when classic, else
        // the zones within his near radius), the distant ones of his ring (the square to the total distance when classic,
        // else the zones within it and a ghost zone's margin).
        static bool NearHas(Vector2s z)
        {
            int d = Mathf.Max(Mathf.Abs(z.x - s_me.x), Mathf.Abs(z.y - s_me.y));
            return d <= s_near && (s_classic || d == 0 || ZoneSystem.instance.ZonesWithinRadius(s_me, z, s_near));
        }

        static bool RingHas(Vector2s z)
        {
            int d = Mathf.Max(Mathf.Abs(z.x - s_me.x), Mathf.Abs(z.y - s_me.y));
            return d <= s_total && (s_classic || d == 0 || ZoneSystem.instance.ZonesWithinRadius(s_me, z, s_total, ghostZone: true));
        }

        static bool GameHas(Vector2s z, bool distant) => NearHas(z) || (distant && RingHas(z));

        // The far-only zones' heightmaps and water surfaces, with the ground's height range.
        static void ListGround()
        {
            for (int i = 0; i < s_zones.Count; i++)
            {
                Zone z = s_zones[i];
                z.Ground.Clear();
                z.HasGround = false;
            }
            List<Heightmap> maps = Heightmap.GetAllHeightmaps();
            for (int i = 0; i < maps.Count; i++)
            {
                Heightmap h = maps[i];
                if (h == null)
                    continue;
                Zone z = ZoneAt(h.transform.position);
                MeshRenderer r = z != null ? s_heightmapRenderer(h) : null;
                if (r == null)
                    continue;
                z.Ground.Add(r);
                Bounds b = r.bounds;
                Spans(z, b.min.y, b.max.y);
            }
            List<WaterVolume> waters = WaterVolume.Instances;
            for (int i = 0; i < waters.Count; i++)
            {
                WaterVolume w = waters[i];
                if (w == null || w.m_waterSurface == null)
                    continue;
                Zone z = ZoneAt(w.transform.position);
                if (z == null)
                    continue;
                z.Ground.Add(w.m_waterSurface);
                float y = w.m_waterSurface.transform.position.y;
                Spans(z, y, y);
            }
        }

        static void Spans(Zone z, float min, float max)
        {
            if (!z.HasGround)
            {
                z.HasGround = true;
                z.MinY = min;
                z.MaxY = max;
                return;
            }
            z.MinY = Mathf.Min(z.MinY, min);
            z.MaxY = Mathf.Max(z.MaxY, max);
        }

        // The LightLod lights in far-only zones, and whether each one's object is a distant one (looked up once).
        static void ListLights()
        {
            s_lights.Clear();
            HashSet<LightLod> all = s_lodLights();
            if (all == null)
                return;
            if (s_lightDistant.Count > 4096)
                s_lightDistant.Clear();
            foreach (LightLod l in all)
            {
                if (l == null)
                    continue;
                Vector2s z = ZoneSystem.GetZone(l.transform.position);
                int k = Index(z.x, z.y);
                if (ZoneAt(k) == null)
                    continue;
                Light light = s_lodLight(l);
                if (light == null)
                    continue;
                if (!s_lightDistant.TryGetValue(l, out bool distant))
                {
                    ZNetView v = l.GetComponentInParent<ZNetView>();
                    distant = v != null && v.m_distant;
                    s_lightDistant[l] = distant;
                }
                s_lights.Add(new LightEntry { Light = light, Index = k, Distant = distant });
            }
        }

        // The far areas' lively objects in far-only zones (those of areas that drain too, until they are gone), with their
        // particle renderers (looked up once per object).
        static void ListLively(RemoteAreas areas)
        {
            s_lively.Clear();
            s_points.Clear();
            float reach = 0f;
            for (int i = 0; i < areas.Count; i++)
            {
                Vector2s c = areas.AreaZone(i);
                s_points.Add(ZoneSystem.GetZonePos(c));
                List<Vector2s> zones = areas.AreaZones(i);
                int far = 0;
                for (int j = 0; j < zones.Count; j++)
                    far = Mathf.Max(far, Mathf.Max(Mathf.Abs(zones[j].x - c.x), Mathf.Abs(zones[j].y - c.y)));
                // Every corner of its farthest zone.
                reach = Mathf.Max(reach, (far + 1) * ZoneSystem.c_ZoneSize * 1.4143f);
            }
            if (s_points.Count == 0)
                return;
            if (s_particlesOf.Count > 20000)
                s_particlesOf.Clear();
            areas.InstancesNear(s_points, reach, s_views, draining: true);
            for (int i = 0; i < s_views.Count; i++)
            {
                ZNetView v = s_views[i];
                if (v == null)
                    continue;
                Vector2s z = ZoneSystem.GetZone(v.transform.position);
                int k = Index(z.x, z.y);
                if (ZoneAt(k) == null)
                    continue;
                if (!s_particlesOf.TryGetValue(v, out ParticleSystemRenderer[] rs))
                {
                    v.GetComponentsInChildren(true, s_particles);
                    rs = s_particles.Count > 0 ? s_particles.ToArray() : s_noParticles;
                    s_particles.Clear();
                    s_particlesOf[v] = rs;
                }
                if (rs.Length == 0)
                    continue;
                ZDO zdo = v.GetZDO();
                s_lively.Add(new Lively { Renderers = rs, Root = v.transform, Index = k, Distant = zdo != null ? zdo.Distant : v.m_distant });
            }
            s_views.Clear();
        }

        // The game's non-instanced clutter round far points (ClutterSystem makes it only round the player: past that circle
        // and a patch's reach it is not the game's), a slice of the grass root's children a frame, swapped in once round.
        static void ListClutter()
        {
            ClutterSystem clutter = ClutterSystem.instance;
            GameObject root = clutter != null ? s_grassRoot(clutter) : null;
            Player p = Player.m_localPlayer;
            if (root == null || p == null)
            {
                s_clutter.Clear();
                s_clutterNext.Clear();
                s_clutterCursor = 0;
                s_clutterHas = false;
                return;
            }
            Transform t = root.transform;
            int count = t.childCount;
            Vector3 center = p.transform.position;
            float reach = clutter.m_distance + clutter.m_grassPatchSize * 0.75f;
            // A crossing (the player moved more than a patch since the last frame): what was grown round the arrival point
            // for the window is now his own, and leaves the list at once instead of after a full round; the round starts over.
            bool moved = s_clutterHas && Utils.DistanceXZ(center, s_clutterFrom) > clutter.m_grassPatchSize;
            s_clutterFrom = center;
            s_clutterHas = true;
            if (moved)
            {
                int kept = 0;
                for (int i = 0; i < s_clutter.Count; i++)
                {
                    Renderer r = s_clutter[i];
                    if (r == null || Utils.DistanceXZ(r.transform.position, center) <= reach)
                        continue;
                    s_clutter[kept++] = r;
                }
                s_clutter.RemoveRange(kept, s_clutter.Count - kept);
                s_clutterNext.Clear();
                s_clutterCursor = 0;
            }
            int end = Mathf.Min(count, s_clutterCursor + ClutterPerFrame);
            for (int i = s_clutterCursor; i < end; i++)
            {
                Transform child = t.GetChild(i);
                if (child.TryGetComponent(out InstanceRenderer _) || Utils.DistanceXZ(child.position, center) <= reach)
                    continue;
                child.GetComponentsInChildren(true, s_renderers);
                s_clutterNext.AddRange(s_renderers);
                s_renderers.Clear();
            }
            s_clutterCursor = end;
            if (end < count)
                return;
            List<Renderer> done = s_clutterNext;
            s_clutterNext = s_clutter;
            s_clutter = done;
            s_clutterNext.Clear();
            s_clutterCursor = 0;
        }

        // Objects of views gone (their object destroyed) leave the caches now and then.
        static void Sweep()
        {
            if (s_parts.Count > 4096)
            {
                foreach (KeyValuePair<ZNetView, Parts> e in s_parts)
                    if (e.Key == null)
                        s_gone.Add(e.Key);
                for (int i = 0; i < s_gone.Count; i++)
                    s_parts.Remove(s_gone[i]);
                s_gone.Clear();
            }
            if (s_parts.Count > 60000)
                s_parts.Clear();
        }

        // Per zone this frame: in the game camera's view or within its shadow distance, within its cull radius.
        static void View(Camera cam)
        {
            GeometryUtility.CalculateFrustumPlanes(cam, s_planes);
            Vector3 eye = cam.transform.position;
            float shadow = QualitySettings.shadowDistance;
            float radius = WindowLayerCull.Radius > 0f ? WindowLayerCull.Radius : float.MaxValue;
            const float half = ZoneSystem.c_ZoneSizeHalf + ZoneMargin;
            int inView = 0;
            for (int i = 0; i < s_zones.Count; i++)
            {
                Zone z = s_zones[i];
                Vector3 c = ZoneSystem.GetZonePos(z.Id);
                // Without its ground listed yet: any height a world has.
                float minY = z.HasGround ? z.MinY - BelowGround : -1000f, maxY = z.HasGround ? z.MaxY + AboveGround : 3000f;
                var min = new Vector3(c.x - half, minY, c.z - half);
                var max = new Vector3(c.x + half, maxY, c.z + half);
                Vector3 nearest = Vector3.Max(min, Vector3.Min(max, eye));
                float d = Vector3.Distance(eye, nearest);
                z.Distance = d;
                z.InView = d < shadow || InFrustum(min, max);
                z.Close = d < radius;
                if (z.InView)
                    inView++;
            }
            LabZonesInView = inView;
        }

        static bool InFrustum(Vector3 min, Vector3 max)
        {
            for (int i = 0; i < s_planes.Length; i++)
            {
                Vector3 n = s_planes[i].normal;
                var p = new Vector3(n.x >= 0f ? max.x : min.x, n.y >= 0f ? max.y : min.y, n.z >= 0f ? max.z : min.z);
                if (Vector3.Dot(n, p) + s_planes[i].distance < 0f)
                    return false;
            }
            return true;
        }

        // The objects of the zones within the cull radius, within the frame's time: a zone being listed first, then the
        // nearest due one in view, then the nearest due one.
        static void ListObjects(int version, float now)
        {
            s_watch.Restart();
            int done = 0;
            List<ZDO>[] sectors = s_objectsBySector(ZDOMan.instance);
            ZNetScene scene = ZNetScene.instance;
            while (true)
            {
                Zone z = NextToList(version, now);
                if (z == null)
                    return;
                if (z.Cursor < 0)
                {
                    z.Snapshot.Clear();
                    z.Next.Clear();
                    List<ZDO> list = sectors != null && z.Index < sectors.Length ? sectors[z.Index] : null;
                    if (list != null)
                        z.Snapshot.AddRange(list);
                    z.Cursor = 0;
                }
                while (z.Cursor < z.Snapshot.Count)
                {
                    if (done >= ListAtLeast && s_watch.Elapsed.TotalMilliseconds >= ListBudgetMs)
                        return;
                    ZDO zdo = z.Snapshot[z.Cursor++];
                    done++;
                    // A ZDO freed since the snapshot (and maybe used again elsewhere) is not this zone's.
                    if (zdo == null || zdo.GetSector() != z.Id)
                        continue;
                    ZNetView v = scene.FindInstance(zdo);
                    if (v == null)
                        continue;
                    Parts p = PartsOf(v, now);
                    if (p.Renderers.Length == 0)
                        continue;
                    z.Next.Add(new Item { Parts = p, Distant = zdo.Distant });
                    LabListed++;
                }
                List<Item> items = z.Items;
                z.Items = z.Next;
                z.Next = items;
                z.Next.Clear();
                z.Snapshot.Clear();
                z.Cursor = -1;
                z.ListedVersion = version;
                z.ListedAt = now;
            }
        }

        static Zone NextToList(int version, float now)
        {
            Zone best = null;
            int bestRank = int.MaxValue;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < s_zones.Count; i++)
            {
                Zone z = s_zones[i];
                int rank;
                if (z.Cursor >= 0)
                    rank = 0;
                else if (!z.Close)
                    continue;
                else if (z.ListedVersion != version || now - z.ListedAt >= ZoneRelist)
                    rank = z.InView ? 1 : 2;
                else
                    continue;
                if (rank < bestRank || (rank == bestRank && z.Distance < bestDistance))
                {
                    best = z;
                    bestRank = rank;
                    bestDistance = z.Distance;
                }
            }
            return best;
        }

        static Parts PartsOf(ZNetView v, float now)
        {
            if (s_parts.TryGetValue(v, out Parts p) && now - p.ListedAt < ViewRelist)
                return p;
            if (p == null)
            {
                p = new Parts { View = v };
                s_parts[v] = p;
            }
            p.ListedAt = now;
            Fill(p, v);
            return p;
        }

        // An object's renderers, and for one that stands still its LOD groups: each renderer's group and level (the lowest
        // it is in), each group's reference point and world size (LODGroup's, by the largest scale) and its transition
        // heights. A moving one's are all drawn whenever it is. A still one's LOD groups do not change: looked up again with
        // the same renderers, its groups, levels and heights stand as they were (LODGroup.GetLODs allocates its tables on
        // every call).
        static void Fill(Parts p, ZNetView v)
        {
            v.GetComponentsInChildren(true, s_renderers);
            int n = s_renderers.Count;
            bool mover = v.TryGetComponent(out Character _) || v.TryGetComponent(out Rigidbody _);
            if (!mover && !p.Mover && p.Renderers != null && SameRenderers(p.Renderers, s_renderers))
            {
                s_renderers.Clear();
                return;
            }
            if (p.Renderers == null || p.Renderers.Length != n)
            {
                p.Renderers = new Renderer[n];
                p.Level = new sbyte[n];
                p.Group = new short[n];
            }
            s_renderers.CopyTo(p.Renderers);
            s_renderers.Clear();
            p.Mover = mover;
            p.Root = p.Mover ? v.transform : null;
            for (int i = 0; i < n; i++)
            {
                p.Level[i] = -1;
                p.Group[i] = -1;
            }
            int groups = 0;
            if (!p.Mover && n > 0)
            {
                v.GetComponentsInChildren(true, s_groups);
                for (int g = 0; g < s_groups.Count && groups < short.MaxValue; g++)
                {
                    LODGroup group = s_groups[g];
                    if (group == null || !group.enabled)
                        continue;
                    LOD[] lods = group.GetLODs();
                    if (lods.Length == 0)
                        continue;
                    if (p.GroupAt == null || p.GroupAt.Length <= groups)
                    {
                        System.Array.Resize(ref p.GroupAt, groups + 1);
                        System.Array.Resize(ref p.GroupSize, groups + 1);
                        System.Array.Resize(ref p.Heights, groups + 1);
                    }
                    Transform t = group.transform;
                    Vector3 scale = t.lossyScale;
                    p.GroupAt[groups] = t.TransformPoint(group.localReferencePoint);
                    p.GroupSize[groups] = group.size * Mathf.Max(Mathf.Abs(scale.x), Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
                    float[] heights = p.Heights[groups] != null && p.Heights[groups].Length == lods.Length ? p.Heights[groups] : new float[lods.Length];
                    for (int level = 0; level < lods.Length && level < sbyte.MaxValue; level++)
                    {
                        heights[level] = lods[level].screenRelativeTransitionHeight;
                        Renderer[] rs = lods[level].renderers;
                        if (rs == null)
                            continue;
                        for (int k = 0; k < rs.Length; k++)
                            if (rs[k] != null && !s_lodOf.ContainsKey(rs[k]))
                                s_lodOf[rs[k]] = (groups << 8) | level;
                    }
                    p.Heights[groups] = heights;
                    groups++;
                }
                s_groups.Clear();
                if (s_lodOf.Count > 0)
                {
                    for (int i = 0; i < n; i++)
                        if (p.Renderers[i] != null && s_lodOf.TryGetValue(p.Renderers[i], out int code))
                        {
                            p.Group[i] = (short)(code >> 8);
                            p.Level[i] = (sbyte)(code & 0xFF);
                        }
                    s_lodOf.Clear();
                }
            }
            if (p.GroupAt != null && p.GroupAt.Length != groups)
            {
                System.Array.Resize(ref p.GroupAt, groups);
                System.Array.Resize(ref p.GroupSize, groups);
                System.Array.Resize(ref p.Heights, groups);
            }
        }

        static bool SameRenderers(Renderer[] had, List<Renderer> now)
        {
            if (had.Length != now.Count)
                return false;
            for (int i = 0; i < had.Length; i++)
                if (!ReferenceEquals(had[i], now[i]))
                    return false;
            return true;
        }

        // This frame's hiding: ground and objects of zones the camera may draw, lights wherever, lively objects' particles
        // in view, far clutter.
        static void Apply(Camera cam)
        {
            Vector3 eye = cam.transform.position;
            float tanHalf = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float bias = QualitySettings.lodBias;
            int lowest = QualitySettings.maximumLODLevel;
            for (int i = 0; i < s_zones.Count; i++)
            {
                Zone z = s_zones[i];
                if (z.Near || !z.InView)
                    continue;
                for (int k = 0; k < z.Ground.Count; k++)
                    Off(z.Ground[k]);
                if (z.Close)
                    HideObjects(z, eye, tanHalf, bias, lowest);
            }
            for (int i = 0; i < s_lights.Count; i++)
            {
                LightEntry e = s_lights[i];
                Zone z = ZoneAt(e.Index);
                if (z == null || z.Near || (e.Distant && z.Ring))
                    continue;
                Light l = e.Light;
                if (l != null && l.enabled)
                {
                    l.enabled = false;
                    s_off.Add(l);
                }
            }
            for (int i = 0; i < s_lively.Count; i++)
            {
                Lively e = s_lively[i];
                Zone z = ZoneAt(e.Index);
                if (z == null || !z.InView || z.Near || (e.Distant && z.Ring) || (Crossing.Count > 0 && e.Root != null && Crossing.Contains(e.Root)))
                    continue;
                ParticleSystemRenderer[] rs = e.Renderers;
                for (int k = 0; k < rs.Length; k++)
                    Off(rs[k]);
            }
            for (int i = 0; i < s_clutter.Count; i++)
                Off(s_clutter[i]);
            if (FarViewer.FarPuffs.Count > 0)
                HidePuffs();
        }

        // Far.Viewer's far puffs (smoke only a window's place made: no player within the game's 64 m, so the game alone
        // shows none there). SmokeRenderer draws the puffs of each 10 m chunk with one particle system: a chunk of far puffs
        // only is hidden (one with a puff of the game's too stays, the game's puff shows).
        static void HidePuffs()
        {
            SmokeRenderer sr = SmokeRenderer.Instance;
            Dictionary<Vector3Int, List<Smoke>> chunks = sr != null ? s_smokeChunks(sr) : null;
            Dictionary<Vector3Int, ParticleSystem> systems = sr != null ? s_smokeSystems(sr) : null;
            if (chunks == null || systems == null)
                return;
            HashSet<Smoke> far = FarViewer.FarPuffs;
            foreach (KeyValuePair<Vector3Int, List<Smoke>> e in chunks)
            {
                List<Smoke> puffs = e.Value;
                if (puffs == null || puffs.Count == 0)
                    continue;
                bool all = true;
                for (int i = 0; i < puffs.Count && all; i++)
                    all = far.Contains(puffs[i]);
                if (all && systems.TryGetValue(e.Key, out ParticleSystem ps) && ps != null && ps.TryGetComponent(out ParticleSystemRenderer r))
                    Off(r);
            }
        }

        static void HideObjects(Zone z, Vector3 eye, float tanHalf, float bias, int lowest)
        {
            List<Item> items = z.Items;
            for (int i = 0; i < items.Count; i++)
            {
                Item item = items[i];
                Parts p = item.Parts;
                if (p.View == null)
                    continue;
                Renderer[] rs = p.Renderers;
                if (p.Mover)
                {
                    // Where it is now: one that walked into what the game loads round the player is the game's (and one the
                    // game camera draws carried through an opening this frame is in its view as such).
                    if (p.Root == null || (Crossing.Count > 0 && Crossing.Contains(p.Root)) || GameHas(ZoneSystem.GetZone(p.Root.position), item.Distant))
                        continue;
                    for (int k = 0; k < rs.Length; k++)
                        Off(rs[k]);
                    continue;
                }
                if (item.Distant && z.Ring)
                    continue;
                int groups = p.GroupAt != null ? p.GroupAt.Length : 0;
                if (s_level.Length < groups)
                    s_level = new int[Mathf.NextPowerOfTwo(groups)];
                for (int g = 0; g < groups; g++)
                    s_level[g] = Pick(p, g, eye, tanHalf, bias, lowest);
                for (int k = 0; k < rs.Length; k++)
                {
                    int g = p.Group[k];
                    if (g >= 0)
                    {
                        int level = s_level[g];
                        if (level == Culled || Mathf.Abs(p.Level[k] - level) > 1)
                            continue;
                    }
                    Off(rs[k]);
                }
            }
        }

        // The LOD level the camera picks for a group (LODGroup: the object's height on screen, by its world size at its
        // reference point, times the LOD bias, against each level's transition height; QualitySettings.maximumLODLevel the
        // lowest index), or Culled below its last level (with LodMargin's allowance).
        static int Pick(Parts p, int g, Vector3 eye, float tanHalf, float bias, int lowest)
        {
            float[] heights = p.Heights[g];
            int count = heights.Length;
            float d = Vector3.Distance(eye, p.GroupAt[g]);
            float relative = d > 0.001f && tanHalf > 0f ? p.GroupSize[g] / (2f * d * tanHalf) * bias : float.MaxValue;
            for (int level = Mathf.Clamp(lowest, 0, count - 1); level < count; level++)
                if (relative >= heights[level])
                    return level;
            return relative >= heights[count - 1] * LodMargin ? count - 1 : Culled;
        }

        static void Off(Renderer r)
        {
            if (r == null || r.forceRenderingOff)
                return;
            r.forceRenderingOff = true;
            s_hidden.Add(r);
        }
    }
}
