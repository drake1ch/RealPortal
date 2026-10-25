using System;
using System.Collections.Generic;
using HarmonyLib;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace RealPortal
{
    // Win.Virtual (FARWORLD-V3 §4.4 A2 body, the V1 spike): the game side of a window drawn without a camera
    // (PortalRenderer.Virtual.cs, WindowScreen). Called between PortalSystem.BeforeWindowRender and AfterWindowRender of the
    // window, so everything is as its slot camera would find it: the far environment and area state set, the far terrain
    // shown and the game's own hidden, what stands behind the exit hidden or left casting (Win.ExitShadows). What it draws,
    // static content only:
    //  - the far area's proxies (ProxyDraw.CollectVirtual: the slot render's cull, LOD, cross-fade and runs from C', the
    //    proxies of objects whose instance stands included: a proxy is its object's exact picture), instanced;
    //  - heightmaps (Heightmap.GetAllHeightmaps: the far area's zones, the far terrain, whatever else is shown) in view,
    //    each with its own block (_LodHideDistance) and the far ambient;
    //  - the far core's static objects that have no proxy (FarProxies' reason: a component, a portal, a structure, random
    //    state, a renderer or material, several LOD groups and the like; not life, animation or physics): their MeshRenderers
    //    at the LOD level Unity picks from C' (the game camera's field of view, the game's lodBias and maximumLODLevel),
    //    listed per far area from the registry's rows a slice a frame (all at once the first time).
    // The G-buffer pass of each is its material's Deferred pass with its block and the far ambient; the shadow casters are
    // the same draws with the ShadowCaster pass, per cascade those that can cast into it. Transparent and forward-only
    // materials, particles, water, grass, characters and other moving objects are not drawn (LabSkipped counts materials
    // left out). Main thread; nothing allocated per frame once the lists have grown (listing a new object allocates).
    internal sealed class WindowScreenContent : IWindowScreenContent
    {
        readonly PortalSystem _sys;
        ZDOID _far;
        bool _proxies;
        int _frame = -1;

        static readonly AccessTools.FieldRef<Heightmap, MeshRenderer> s_mapRenderer = AccessTools.FieldRefAccess<Heightmap, MeshRenderer>("m_meshRenderer");
        static readonly AccessTools.FieldRef<Heightmap, MeshFilter> s_mapFilter = AccessTools.FieldRefAccess<Heightmap, MeshFilter>("m_meshFilter");
        static readonly MaterialPropertyBlock s_block = new MaterialPropertyBlock();

        // This render's drawables (heightmaps and core renderers): in view (G-buffer) and casting (shadow casters).
        struct Drawable
        {
            public MeshRenderer Renderer;
            public Mesh Mesh;
            public Material[] Materials;
            public Material Material;
            public Vector3 Center;
            public float Radius;
            public bool InView, Casts;
        }

        readonly List<Drawable> _draw = new List<Drawable>(256);

        // The sun's caster volume of this view, as ProxyDraw culls a slot render's casters: the rect's frustum sides and
        // near plane, the shadow distance along the view, the exit plane. Only the window's pixels read the shadows, so a
        // caster whose shadow, swept along the sun, cannot reach that volume is left out (0 planes: no sun shadows).
        static NativeArray<Vector4> s_casterPlanes;
        static int s_casterCount;
        static Vector3 s_casterRay;
        // How far toward the sun a caster may stand (ProxyDraw.CasterReach).
        const float CasterReach = 1000f;

        // Lab: renders, heightmaps and core renderers drawn in view (last render), core objects listed (all areas),
        // materials left out (transparent, no Deferred pass).
        internal static long LabRenders;
        internal static int LabMaps, LabCore, LabCoreObjects;
        internal static long LabSkipped;

        internal WindowScreenContent(PortalSystem sys)
        {
            _sys = sys;
        }

        public bool Begin(in VirtualView v, out string why)
        {
            why = null;
            if (!FarProxies.Drawing)
            {
                // Without proxies every far object is real and the core list (rows with no proxy) does not cover them.
                why = "Far.Proxies not drawing";
                return false;
            }
            if (!_sys.WindowFarPortal(v.Window, out _far))
            {
                why = "no far portal for the window";
                return false;
            }
            _frame = Time.frameCount;
            _draw.Clear();
            _proxies = ProxyDraw.CollectVirtual(v, _far);
            float k = QualitySettings.lodBias / (2f * Mathf.Tan(v.FieldOfView * 0.5f * Mathf.Deg2Rad));
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            CasterVolume(v);
            CollectMaps(v);
            int maps = _draw.Count;
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            Core.Collect(_sys, _far, v, k, _draw);
            long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
            WindowScreen.LabProf[3] += (t1 - t0) * WindowScreen.TickMs;
            WindowScreen.LabProf[4] += (t2 - t1) * WindowScreen.TickMs;
            LabMaps = maps;
            LabCore = _draw.Count - maps;
            LabRenders++;
            return true;
        }

        public Light Sun => EnvMan.instance != null ? EnvMan.instance.m_dirLight : null;

        public int ShadowMapSize => _sys.VirtualShadowRes(Sun);

        public int RecordBody(CommandBuffer cb, in VirtualView v)
        {
            int draws = _proxies ? ProxyDraw.RecordVirtualBody(cb) : 0;
            for (int i = 0; i < _draw.Count; i++)
            {
                Drawable d = _draw[i];
                if (d.InView)
                    draws += Draw(cb, d, false);
            }
            return draws;
        }

        public int RecordCasters(CommandBuffer cb, in VirtualView v, in VirtualCascade c)
        {
            int draws = _proxies ? ProxyDraw.RecordVirtualCasters(cb, c) : 0;
            for (int i = 0; i < _draw.Count; i++)
            {
                Drawable d = _draw[i];
                if (d.Casts && c.Casts(d.Center, d.Radius))
                    draws += Draw(cb, d, true);
            }
            return draws;
        }

        public ReflectionProbe Probe(in VirtualView v) => _sys.VirtualProbeAt(v.Eye);

        public Material Fog(in VirtualView v, Vector3[] corners, out int pass) => FogCommandBuffer.Virtual(corners, out pass);

        public void End()
        {
            _draw.Clear();
        }

        // Every material slot of a drawable (slot k draws submesh min(k, subMeshCount - 1), as Unity does) with the pass, its
        // renderer's block, and for the G-buffer the far ambient.
        static int Draw(CommandBuffer cb, in Drawable d, bool caster)
        {
            MeshRenderer r = d.Renderer;
            if (r == null || d.Mesh == null)
                return 0;
            int slots = d.Materials != null ? d.Materials.Length : 1, subs = d.Mesh.subMeshCount, draws = 0;
            Matrix4x4 m = r.localToWorldMatrix;
            for (int k = 0; k < slots; k++)
            {
                Material mat = d.Materials != null ? d.Materials[k] : d.Material;
                if (mat == null)
                    continue;
                if (mat.renderQueue > 2500)
                {
                    // Transparent: Phase C.
                    if (!caster)
                        LabSkipped++;
                    continue;
                }
                Vector2Int passes = VirtualPasses.Of(mat);
                int pass = caster ? passes.y : passes.x;
                if (pass < 0)
                {
                    if (!caster)
                        LabSkipped++;
                    continue;
                }
                s_block.Clear();
                if (r.HasPropertyBlock())
                    r.GetPropertyBlock(s_block);
                if (!caster)
                    WindowScreen.AddAmbient(s_block, 0);
                cb.DrawMesh(d.Mesh, m, mat, Mathf.Min(k, subs - 1), pass, s_block);
                draws++;
            }
            return draws;
        }

        void CasterVolume(in VirtualView v)
        {
            s_casterCount = 0;
            Light sun = Sun;
            if (!WindowScreenShadows.Casting(sun))
                return;
            if (!s_casterPlanes.IsCreated)
                s_casterPlanes = new NativeArray<Vector4>(7, Allocator.Persistent);
            Plane[] p = v.ViewPlanes;
            for (int i = 0; i < 5; i++)
                s_casterPlanes[i] = new Vector4(p[i].normal.x, p[i].normal.y, p[i].normal.z, p[i].distance);
            Vector3 f = v.Forward;
            s_casterPlanes[5] = new Vector4(-f.x, -f.y, -f.z, Vector3.Dot(f, v.Eye) + v.ShadowDistance);
            s_casterPlanes[6] = v.ExitPlane;
            s_casterRay = sun.transform.forward * CasterReach;
            s_casterCount = 7;
        }

        // Whether a sphere can shade the window's pixels (ShadowCastingMode aside).
        static bool CastsIntoView(Vector3 c, float r) => s_casterCount > 0 && ProxyDraw.Sweep(s_casterPlanes, 0, s_casterCount, c, r, s_casterRay);

        // ---- heightmaps -----------------------------------------------------------------------------------------------

        void CollectMaps(in VirtualView v)
        {
            List<Heightmap> all = Heightmap.GetAllHeightmaps();
            for (int i = 0; i < all.Count; i++)
            {
                Heightmap h = all[i];
                if (h == null || !h.isActiveAndEnabled)
                    continue;
                MeshRenderer r = s_mapRenderer(h);
                MeshFilter f = s_mapFilter(h);
                if (r == null || f == null || !r.enabled || r.forceRenderingOff || f.sharedMesh == null || (v.CullingMask & (1 << r.gameObject.layer)) == 0)
                    continue;
                Bounds b = r.bounds;
                float rad = b.extents.magnitude;
                bool inView = r.shadowCastingMode != ShadowCastingMode.ShadowsOnly && InView(v, b.center, b.extents, rad);
                bool casts = r.shadowCastingMode != ShadowCastingMode.Off && CastsIntoView(b.center, rad);
                if (!inView && !casts)
                    continue;
                _draw.Add(new Drawable { Renderer = r, Mesh = f.sharedMesh, Material = r.sharedMaterial, Center = b.center, Radius = rad, InView = inView, Casts = casts });
            }
        }

        // An AABB in the rect's frustum and not wholly on the camera's side of the exit portal.
        internal static bool InView(in VirtualView v, Vector3 c, Vector3 ext, float rad)
        {
            Vector4 e = v.ExitPlane;
            if (e.x * c.x + e.y * c.y + e.z * c.z + e.w < -rad)
                return false;
            Plane[] planes = v.ViewPlanes;
            for (int i = 0; i < 6; i++)
            {
                Vector3 n = planes[i].normal;
                float reach = Mathf.Abs(n.x) * ext.x + Mathf.Abs(n.y) * ext.y + Mathf.Abs(n.z) * ext.z;
                if (Vector3.Dot(n, c) + planes[i].distance < -reach)
                    return false;
            }
            return true;
        }

        // ---- the far core's static objects without a proxy -----------------------------------------------------------

        static class Core
        {
            // One object: its MeshRenderers (with their meshes and materials as listed), LOD groups and the levels each
            // renderer is in; the object's sphere (its renderers' bounds when listed, grown) for a first test.
            sealed class Obj
            {
                internal ZNetView View;
                internal int Row, Seen;
                internal Vector3 Center;
                internal float Radius;
                internal MeshRenderer[] Renderers;
                internal Mesh[] Meshes;
                internal Material[][] Materials;
                internal int[] Group;
                internal int[] Levels;
                internal LODGroup[] Groups;
                internal float[][] Heights;
                internal int[] Level;
            }

            sealed class Area
            {
                internal ZDOID Far;
                internal readonly Dictionary<int, Obj> ByRow = new Dictionary<int, Obj>();
                internal readonly List<Obj> List = new List<Obj>();
                internal int Sector, RowAt, Pass = 1;
                internal bool Listed;
                internal float UsedAt;
            }

            static readonly List<Area> s_areas = new List<Area>();
            static readonly List<MeshRenderer> s_renderers = new List<MeshRenderer>();
            static readonly List<LODGroup> s_groups = new List<LODGroup>();
            static readonly List<Obj> s_drop = new List<Obj>();
            static readonly System.Diagnostics.Stopwatch s_watch = new System.Diagnostics.Stopwatch();
            // Listing per frame: this long at most (the first full pass of an area: FirstMs).
            const double SliceMs = 0.15, FirstMs = 20.0;
            const float DropSeconds = 30f;

            static Area AreaOf(ZDOID far)
            {
                for (int i = 0; i < s_areas.Count; i++)
                    if (s_areas[i].Far == far)
                        return s_areas[i];
                var a = new Area { Far = far };
                s_areas.Add(a);
                return a;
            }

            internal static void Collect(PortalSystem sys, ZDOID far, in VirtualView v, float k, List<Drawable> into)
            {
                Area a = AreaOf(far);
                a.UsedAt = Time.unscaledTime;
                // Areas no virtual render used for a while go.
                for (int i = s_areas.Count - 1; i >= 0; i--)
                    if (Time.unscaledTime - s_areas[i].UsedAt > DropSeconds)
                        s_areas.RemoveAt(i);
                List(sys, a);
                int maxLod = QualitySettings.maximumLODLevel;
                for (int i = 0; i < a.List.Count; i++)
                {
                    Obj o = a.List[i];
                    if (o.View == null || !o.View.isActiveAndEnabled)
                        continue;
                    // The object's sphere: in view, or near enough to cast into the view's shadow reach (the cascades test
                    // each caster).
                    float reach = 2f * v.ShadowDistance + o.Radius;
                    bool near = (o.Center - v.Eye).sqrMagnitude < reach * reach;
                    if (!near && !InView(v, o.Center, Vector3.one * o.Radius, o.Radius))
                        continue;
                    for (int g = 0; g < o.Groups.Length; g++)
                        o.Level[g] = LevelOf(o, g, v.Eye, k, maxLod);
                    for (int j = 0; j < o.Renderers.Length; j++)
                    {
                        MeshRenderer r = o.Renderers[j];
                        if (r == null || !r.enabled || r.forceRenderingOff || !r.gameObject.activeInHierarchy || o.Meshes[j] == null
                            || (v.CullingMask & (1 << r.gameObject.layer)) == 0)
                            continue;
                        int g = o.Group[j];
                        if (g >= 0 && o.Level[g] != -2 && (o.Level[g] < 0 || (o.Levels[j] & (1 << o.Level[g])) == 0))
                            continue;
                        Bounds b = r.bounds;
                        float rad = b.extents.magnitude;
                        bool inView = r.shadowCastingMode != ShadowCastingMode.ShadowsOnly && InView(v, b.center, b.extents, rad);
                        bool casts = r.shadowCastingMode != ShadowCastingMode.Off && CastsIntoView(b.center, rad);
                        if (!inView && !casts)
                            continue;
                        into.Add(new Drawable { Renderer = r, Mesh = o.Meshes[j], Materials = o.Materials[j], Center = b.center, Radius = rad, InView = inView, Casts = casts });
                    }
                }
                int n = 0;
                for (int i = 0; i < s_areas.Count; i++)
                    n += s_areas[i].List.Count;
                LabCoreObjects = n;
            }

            // LODGroup's rule (as ProxyDraw's Select): the first level from maximumLODLevel whose transition height the
            // relative height reaches; -1 below the last (culled). A disabled group shows every level (-2: no test).
            static int LevelOf(Obj o, int g, Vector3 eye, float k, int maxLod)
            {
                LODGroup group = o.Groups[g];
                if (group == null || !group.enabled)
                    return -2;
                Transform t = group.transform;
                Vector3 s = t.lossyScale;
                float size = group.size * Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));
                float d = Vector3.Distance(eye, t.TransformPoint(group.localReferencePoint));
                float rel = d > 1e-6f ? size * k / d : float.MaxValue;
                float[] h = o.Heights[g];
                for (int i = Mathf.Min(maxLod, h.Length - 1); i < h.Length; i++)
                    if (rel >= h[i])
                        return i;
                return -1;
            }

            // The registry rows of the area that stand real without a proxy and are static, a slice a frame; an object not
            // met again over a whole pass goes.
            static void List(PortalSystem sys, Area a)
            {
                SectorRegistry reg = FarProxies.Registry;
                AreaSectors sectors = sys.Areas.SectorsOf(a.Far);
                ZNetScene scene = ZNetScene.instance;
                if (reg == null || sectors == null || scene == null)
                    return;
                int count = sectors.NearRecs.Length + sectors.RingRecs.Length;
                if (count == 0)
                    return;
                double budget = a.Listed ? SliceMs : FirstMs;
                s_watch.Restart();
                int guard = 0;
                while (s_watch.Elapsed.TotalMilliseconds < budget && guard++ < count + 1)
                {
                    if (a.Sector >= count)
                    {
                        // A pass is over: what it did not meet goes.
                        for (int i = a.List.Count - 1; i >= 0; i--)
                        {
                            Obj o = a.List[i];
                            if (o.Seen != a.Pass || o.View == null)
                            {
                                a.ByRow.Remove(o.Row);
                                a.List.RemoveAt(i);
                            }
                        }
                        a.Pass++;
                        a.Sector = 0;
                        a.RowAt = 0;
                        if (!a.Listed)
                        {
                            a.Listed = true;
                            return;
                        }
                        budget = SliceMs;
                    }
                    int slot = a.Sector < sectors.NearRecs.Length ? sectors.NearRecs[a.Sector] : sectors.RingRecs[a.Sector - sectors.NearRecs.Length];
                    SectorRec rec = reg.RecAt(slot);
                    if (rec == null || !rec.Built)
                    {
                        a.Sector++;
                        a.RowAt = 0;
                        continue;
                    }
                    for (; a.RowAt < rec.Count; a.RowAt++)
                    {
                        if ((a.RowAt & 63) == 63 && s_watch.Elapsed.TotalMilliseconds >= budget)
                            break;
                        int row = rec.Rows[a.RowAt];
                        if (!reg.RowLive(row) || !reg.RowCreated(row) || !FarProxies.ReasonOf(row, out ProxyReal why) || !Static(why))
                            continue;
                        ZNetView view = scene.FindInstance(reg.RowZdo(row));
                        if (view == null)
                            continue;
                        if (a.ByRow.TryGetValue(row, out Obj o) && o.View == view)
                        {
                            o.Seen = a.Pass;
                            continue;
                        }
                        if (o != null)
                        {
                            a.ByRow.Remove(row);
                            a.List.Remove(o);
                        }
                        o = Make(view, row);
                        if (o == null)
                            continue;
                        o.Seen = a.Pass;
                        a.ByRow[row] = o;
                        a.List.Add(o);
                    }
                    if (a.RowAt >= rec.Count)
                    {
                        a.Sector++;
                        a.RowAt = 0;
                    }
                }
            }

            // Real without a proxy, and nothing that moves on its own (life, animation, physics) nor nothing to draw.
            static bool Static(ProxyReal why) =>
                why != ProxyReal.None && why != ProxyReal.ActiveArea && why != ProxyReal.Life && why != ProxyReal.Animated && why != ProxyReal.Physics
                && why != ProxyReal.NoPrefab;

            // An object's MeshRenderers (inactive ones too: their parts switch with its state), their LOD groups and levels.
            static Obj Make(ZNetView view, int row)
            {
                s_renderers.Clear();
                view.GetComponentsInChildren(true, s_renderers);
                if (s_renderers.Count == 0)
                    return null;
                s_groups.Clear();
                view.GetComponentsInChildren(true, s_groups);
                int n = s_renderers.Count;
                var o = new Obj
                {
                    View = view,
                    Row = row,
                    Renderers = s_renderers.ToArray(),
                    Meshes = new Mesh[n],
                    Materials = new Material[n][],
                    Group = new int[n],
                    Levels = new int[n],
                    Groups = s_groups.ToArray(),
                    Heights = new float[s_groups.Count][],
                    Level = new int[s_groups.Count],
                };
                bool any = false;
                Bounds all = default;
                for (int j = 0; j < n; j++)
                {
                    MeshRenderer r = o.Renderers[j];
                    MeshFilter f = r.GetComponent<MeshFilter>();
                    o.Meshes[j] = f != null ? f.sharedMesh : null;
                    o.Materials[j] = r.sharedMaterials;
                    o.Group[j] = -1;
                    if (r.gameObject.activeInHierarchy)
                    {
                        if (!any)
                            all = r.bounds;
                        else
                            all.Encapsulate(r.bounds);
                        any = true;
                    }
                }
                for (int g = 0; g < o.Groups.Length; g++)
                {
                    LOD[] lods = o.Groups[g].GetLODs();
                    o.Heights[g] = new float[lods.Length];
                    for (int l = 0; l < lods.Length; l++)
                    {
                        o.Heights[g][l] = lods[l].screenRelativeTransitionHeight;
                        Renderer[] rs = lods[l].renderers;
                        if (rs == null)
                            continue;
                        foreach (Renderer lr in rs)
                        {
                            int j = Array.IndexOf(o.Renderers, lr as MeshRenderer);
                            if (j < 0)
                                continue;
                            o.Group[j] = g;
                            if (l < 31)
                                o.Levels[j] |= 1 << l;
                        }
                    }
                }
                Vector3 at = view.transform.position;
                o.Center = any ? all.center : at;
                // Grown: parts of another state show later, the object may sway.
                o.Radius = (any ? all.extents.magnitude : 8f) + 4f;
                return o;
            }
        }
    }

    // Win.Virtual: a material's Deferred and ShadowCaster passes (-1: none), found once per material: by the LightMode tag
    // (the game's shaders keep it upper-cased, and its key may read back empty: both spellings and the value's case
    // ignored), else by the pass name (a surface shader's generated DEFERRED / ShadowCaster).
    internal static class VirtualPasses
    {
        static readonly Dictionary<Material, Vector2Int> s_cache = new Dictionary<Material, Vector2Int>();
        static readonly ShaderTagId s_lightMode = new ShaderTagId("LightMode"), s_lightModeUpper = new ShaderTagId("LIGHTMODE");

        internal static Vector2Int Of(Material m)
        {
            if (s_cache.TryGetValue(m, out Vector2Int p))
                return p;
            p = new Vector2Int(-1, -1);
            Shader sh = m.shader;
            if (sh != null)
            {
                for (int i = 0; i < m.passCount; i++)
                {
                    ShaderTagId v = sh.FindPassTagValue(i, s_lightMode);
                    if (string.IsNullOrEmpty(v.name))
                        v = sh.FindPassTagValue(i, s_lightModeUpper);
                    string tag = v.name, name = m.GetPassName(i);
                    if (p.x < 0 && (string.Equals(tag, "Deferred", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(tag) && string.Equals(name, "DEFERRED", StringComparison.OrdinalIgnoreCase)))
                        p.x = i;
                    if (p.y < 0 && (string.Equals(tag, "ShadowCaster", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(tag) && string.Equals(name, "ShadowCaster", StringComparison.OrdinalIgnoreCase)))
                        p.y = i;
                }
            }
            s_cache[m] = p;
            return p;
        }

        internal static void Clear() => s_cache.Clear();
    }
}
