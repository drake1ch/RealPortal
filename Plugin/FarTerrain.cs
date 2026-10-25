using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RealPortal
{
    // The game draws the land beyond the loaded zones with one coarse mesh around the camera (TerrainLod, 2.4 km
    // square). Around a far portal the same mesh is built once (the portal does not move) and drawn only while its
    // window renders, in place of the one around the player.
    internal sealed class FarTerrain
    {
        static readonly FieldInfo s_size = AccessTools.Field(typeof(TerrainLod), "m_terrainSize");
        static readonly FieldInfo s_regions = AccessTools.Field(typeof(TerrainLod), "m_regionsPerAxis");
        static readonly FieldInfo s_vertex = AccessTools.Field(typeof(TerrainLod), "m_vertexDistance");
        static readonly FieldInfo s_material = AccessTools.Field(typeof(TerrainLod), "m_material");

        static TerrainLod s_main;
        static readonly List<Renderer> s_mainRenderers = new List<Renderer>();
        static float s_mainListedAt = -10f;

        readonly GameObject _root;
        readonly List<Heightmap> _maps = new List<Heightmap>();
        readonly List<Heightmap> _waiting = new List<Heightmap>();
        readonly List<Renderer> _renderers = new List<Renderer>();
        bool _visible = true;

        internal FarTerrain(Vector3 anchor)
        {
            TerrainLod main = MainLod();
            if (main == null)
                return;
            float size = (float)s_size.GetValue(main);
            int regions = (int)s_regions.GetValue(main);
            float vertex = (float)s_vertex.GetValue(main);
            var material = (Material)s_material.GetValue(main);
            // The same layout as TerrainLod.CreateMeshes / RebuildHeightmap, centred on the portal.
            float scale = Mathf.Round(vertex);
            int width = Mathf.RoundToInt(size / regions / scale);
            var point = new Vector3(Mathf.Round(anchor.x / vertex) * vertex, 0f, Mathf.Round(anchor.z / vertex) * vertex);
            _root = new GameObject("RealPortalFarTerrain");
            for (int i = 0; i < regions; i++)
                for (int j = 0; j < regions; j++)
                {
                    var offset = new Vector3((i * 2f - regions + 1f) * size * 0.5f / regions, 0f, (j * 2f - regions + 1f) * size * 0.5f / regions);
                    var go = new GameObject("lodMesh");
                    go.transform.position = point + offset;
                    go.transform.SetParent(_root.transform);
                    Heightmap map = go.AddComponent<Heightmap>();
                    map.m_scale = scale;
                    map.m_width = width;
                    map.m_material = material;
                    map.IsDistantLod = true;
                    map.enabled = true;
                    _maps.Add(map);
                    _waiting.Add(map);
                }
            _root.GetComponentsInChildren(true, _renderers);
            SetVisible(false);
        }

        internal bool Built => _root != null && _waiting.Count == 0;

        // One mesh a frame, once its heights are ready (the game builds them off the main thread).
        internal bool BuildStep()
        {
            for (int i = 0; i < _waiting.Count; i++)
            {
                Heightmap map = _waiting[i];
                if (map == null || HeightmapBuilder.instance.IsTerrainReady(map.transform.position, map.m_width, map.m_scale, true, WorldGenerator.instance))
                {
                    _waiting.RemoveAt(i);
                    if (map == null)
                        return false;
                    map.Regenerate();
                    return true;
                }
            }
            return false;
        }

        internal void SetVisible(bool visible)
        {
            if (_root == null || visible == _visible)
                return;
            foreach (Renderer r in _renderers)
                if (r != null)
                    r.forceRenderingOff = !visible;
            _visible = visible;
        }

        internal void Destroy()
        {
            if (_root != null)
                Object.Destroy(_root);
        }

        static TerrainLod MainLod()
        {
            if (s_main == null)
                s_main = Object.FindFirstObjectByType<TerrainLod>();
            return s_main;
        }

        // The game's own distant terrain, hidden while a far window draws its own in its place.
        internal static void SetMainVisible(bool visible)
        {
            TerrainLod main = MainLod();
            if (main == null)
                return;
            if (Time.unscaledTime - s_mainListedAt > 2f)
            {
                s_mainListedAt = Time.unscaledTime;
                s_mainRenderers.Clear();
                main.GetComponentsInChildren(false, s_mainRenderers);
            }
            foreach (Renderer r in s_mainRenderers)
                if (r != null)
                    r.forceRenderingOff = !visible;
        }

        // ---- Far.CrossAtOnce (Docs\PARITY-PLAN.md P5.3, P22 terrain) --------------------------------------------------------
        // After a crossing the game's TerrainLod stands round the place left until it has rebuilt round the camera: once the
        // camera is 256 m from its last point, and only when the heights of all its meshes are ready, frames later. Past 1.2 km
        // the land beyond the loaded zones was missing until then. Meanwhile the arriving place's distant terrain (its area's,
        // built while its window was live: the same layout, round its portal) is shown to the game camera in place of the
        // game's (HandOff): right before the game camera culls (after the window renders, which keep seeing the game's own)
        // it is shown and the game's hidden, and the other way round once it has rendered; not while the camera stands on the
        // far side of a portal (it looks at the place left then). It ends once TerrainLod's meshes stand round the reference
        // position (MainStands), after HandOffMax at most, or with the scene (EndHandOff). Should its area drain meanwhile, it
        // is destroyed only then (Orphan). Off: nothing of it runs. Main thread only; nothing allocated per frame.

        const float HandOffMax = 20f;
        static readonly AccessTools.FieldRef<TerrainLod, Vector3> s_lastPoint = AccessTools.FieldRefAccess<TerrainLod, Vector3>("m_lastPoint");
        static readonly AccessTools.FieldRef<TerrainLod, float> s_stepDistance = AccessTools.FieldRefAccess<TerrainLod, float>("m_updateStepDistance");

        // The distant terrain standing in, whether its area has gone (it is destroyed as it ends), whether it is shown to the
        // game camera now, since when it stands in, and whether the camera hooks are in.
        static FarTerrain s_handOff;
        static bool s_handOffOrphan, s_shownToMain, s_hooked;
        static float s_handOffAt;
        static int s_shownFrame;

        // Lab: hand-offs begun, and frames the game camera drew the arriving place's distant terrain in place of its own.
        internal static int LabHandOffs;
        internal static long LabHandOffFrames;

        // `t` (built) stands in for the game's distant terrain until that stands round `at` (the reference position), unless
        // it does already (a crossing too short for TerrainLod to move).
        internal static void HandOff(FarTerrain t, Vector3 at)
        {
            if (t == null || !t.Built)
                return;
            if (ReferenceEquals(t, s_handOff))
            {
                s_handOffAt = Time.time;
                return;
            }
            EndHandOff();
            if (MainStands(at))
                return;
            s_handOff = t;
            s_handOffOrphan = false;
            s_handOffAt = Time.time;
            LabHandOffs++;
            // After PortalSystem's (registered as the first portal loaded): the window renders come first in the main
            // camera's pre-cull.
            if (!s_hooked)
            {
                s_hooked = true;
                Camera.onPreCull += HandOffPreCull;
                Camera.onPostRender += HandOffPostRender;
            }
        }

        // Its area drains (RemoteAreas.StartDraining): true when `t` stands in now, and is destroyed as that ends instead.
        internal static bool Orphan(FarTerrain t)
        {
            if (t == null || !ReferenceEquals(t, s_handOff))
                return false;
            s_handOffOrphan = true;
            return true;
        }

        internal static void EndHandOff()
        {
            FarTerrain t = s_handOff;
            if (t == null)
                return;
            HideFromMain();
            s_handOff = null;
            if (s_handOffOrphan)
                t.Destroy();
            s_handOffOrphan = false;
        }

        // Every frame (RemoteAreas.Update): it ends once the game's own stands round `at`, or at HandOffMax.
        internal static void HandOffStep(Vector3 at)
        {
            FarTerrain t = s_handOff;
            if (t == null)
                return;
            if (!FarFlags.CrossAtOnce || t._root == null || Time.time - s_handOffAt > HandOffMax || MainStands(at))
                EndHandOff();
        }

        // TerrainLod's meshes stand round `at`: its last point within its update step of it, and the meshes moved there
        // (RebuildAllHeightmaps leaves their middle at the last point).
        static bool MainStands(Vector3 at)
        {
            TerrainLod main = MainLod();
            if (main == null)
                return true;
            Vector3 last = s_lastPoint(main);
            if (Utils.DistanceXZ(at, last) > s_stepDistance(main))
                return false;
            Transform root = main.transform;
            Vector3 sum = Vector3.zero;
            int n = 0;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform mesh = root.GetChild(i);
                if (!mesh.TryGetComponent(out Heightmap _))
                    continue;
                sum += mesh.position;
                n++;
            }
            return n == 0 || Utils.DistanceXZ(sum / n, last) < 1f;
        }

        static void HandOffPreCull(Camera cam)
        {
            FarTerrain t = s_handOff;
            if (t == null || cam == null || cam != Utils.GetMainCamera())
                return;
            // Last frame's game camera may not have reported back.
            if (s_shownToMain && s_shownFrame != Time.frameCount)
                HideFromMain();
            if (s_shownToMain)
                return;
            PortalSystem sys = PortalSystem.Instance;
            if (PortalSystem.Suspended || (sys != null && sys.CameraThroughFrame == Time.frameCount))
                return;
            s_shownToMain = true;
            s_shownFrame = Time.frameCount;
            t.SetVisible(true);
            SetMainVisible(false);
            LabHandOffFrames++;
        }

        static void HandOffPostRender(Camera cam)
        {
            if (s_shownToMain && cam != null && cam == Utils.GetMainCamera())
                HideFromMain();
        }

        static void HideFromMain()
        {
            if (!s_shownToMain)
                return;
            s_shownToMain = false;
            s_handOff?.SetVisible(false);
            SetMainVisible(true);
        }
    }
}
