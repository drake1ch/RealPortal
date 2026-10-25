using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace RealPortal
{
    // Visual templates and classification (ARCHITECTURE-v2.md §3.5): what one prefab draws, read from the prefab asset only
    // (ZNetScene.GetPrefab; nothing is instantiated), made lazily per prefab hash (ProxyTemplates.Get, or queued with
    // Request and made within FarProxies' budget). The user's rule decides the classes: a proxy must draw exactly what its
    // real object would, so a prefab is proxied only when every component it can ever wake with is one whose effect on the
    // picture was reviewed and is reproduced from the ZDO alone (the whitelist in Review); anything else stays real, with
    // the reason counted (ProxyReal): other mods' components, life, effects, lights and audio, animation, physics, signs,
    // item and armor stands, ships, carts, doors, portals, cloth, transparent materials, nested LODGroups.
    //
    // A template is the prefab walked once:
    //  - parts: one per material slot of every MeshRenderer that can ever be visible (slot k draws submesh
    //    min(k, subMeshCount - 1), as Unity does), with its transform relative to the root, its bounds and its draw key;
    //  - toggles: the GameObjects and renderer switches a decoder sets from the ZDO (WearNTear's new/worn/broken and snow
    //    objects, Pickable's picked subtree, MineRock's areas and base model, MineRock5's hit areas, a chest's open and
    //    closed objects). A part shows when every toggle on its path is on; a part under an object inactive in the prefab
    //    that nothing switches, or of a disabled renderer, never shows and is left out;
    //  - forced states the game sets on every instance (CraftingStation's area marker and in-use object off once it has
    //    started; MineRock5's hit-area renderers off and its combined renderer on the root in their place, LOD0 replaced);
    //  - MaterialMan containers (the GameObject a component sets values on: WearNTear's root for _SnowLevel,
    //    RandomMaterialValues' own) and which parts each covers (its GetComponentsInChildren<MeshRenderer>(true));
    //  - the one LODGroup (none, or more than one: real) with each part's LOD levels;
    //  - the decoders' fields (Decode).
    // Variants (ARCHITECTURE-v2.md §3.6): Decode turns a ZDO's state into the visible parts with their batch (material
    // variation applied) and property block (exactly the values MaterialMan would leave on that renderer), interned per
    // template, so equal states share one ProxyTables.Variants entry. Main thread only.
    internal static class ProxyTemplates
    {
        // A template keeps at most this many distinct states (MineRock5 hit-area masks): past it a state stays real.
        internal const int MaxVariants = 4096;
        // Dungeon interiors stand this high above the world (Far.DeferInteriors' line).
        internal const float InteriorHeight = 3000f;

        internal sealed class TRenderer
        {
            internal Mesh Mesh;
            internal ShadowCastingMode Shadows;
            internal bool Receive, StaticShadow;
            internal int Layer;
            internal LightProbeUsage LightProbes;
            internal ReflectionProbeUsage ReflectionProbes;
            internal MotionVectorGenerationMode Motion;
            internal uint RenderingLayers;
            internal int Go;
            // MineRock5's combined renderer (one per hit area here): no MaterialVariation reaches it.
            internal bool Combined;
        }

        internal struct TPart
        {
            internal int Renderer;
            internal int Slot;
            internal int Submesh;
            internal Material Material;
            internal int ReqStart, ReqCount;
            internal int Containers;
            internal int Global;
            internal Vector3 Center, Extents;
        }

        internal sealed class WearDef
        {
            internal int New = -1, Worn = -1, Broken = -1, Snow = -1, SnowWorn = -1, SnowBroken = -1;
            internal bool HasStates, HasSnow;
            internal float Health;
        }

        internal sealed class PickDef
        {
            internal int Hide = -1;
            internal bool DefaultPicked;
            internal float RespawnMinutes;
        }

        internal sealed class RockDef
        {
            internal int[] Areas;
            internal int[] Keys;
            internal int Base = -1;
            internal int[] MeshStart, MeshToggles;
            internal float Health;
        }

        internal sealed class Rock5Def
        {
            internal int[] Areas;
            internal float Health;
        }

        internal sealed class MatVarDef
        {
            internal int Renderer, Slot, Key;
            internal Material[] Options;
        }

        internal sealed class RmvDef
        {
            internal int Container;
            internal RandomMaterialValues Component;
            // Values per seed (System.Random per property, as the game: computed once per seed seen).
            internal readonly Dictionary<int, ProxyMpbValue[]> BySeed = new Dictionary<int, ProxyMpbValue[]>();
        }

        internal sealed class ChestDef
        {
            internal int Open = -1, Closed = -1;
        }

        internal sealed class VisualTemplate
        {
            // ProxyTables.Templates index (-1: real, nothing in the tables).
            internal int Id = -1;
            internal int Prefab;
            internal GameObject Asset;
            internal string Name;
            internal ProxyReal Real;
            internal string Why;
            internal Vector3 RootScale;
            internal bool SyncScale;
            internal int ToggleCount;
            internal bool[] ToggleDefault;
            internal int[] Req;
            internal TPart[] Parts;
            internal Matrix4x4[] PartLocals;
            internal int[] PartLods;
            internal TRenderer[] Renderers;
            internal int ContainerCount;
            internal bool RootContainer;
            internal WearDef Wear;
            internal PickDef Pick;
            internal RockDef Rock;
            internal Rock5Def Rock5;
            internal MatVarDef[] MatVars;
            internal RmvDef[] Rmvs;
            internal ChestDef Chest;
            internal bool Tree;
            internal float TreeHealth;
            // Particle subtrees carried apart (FarProxies' carriers): the prefab's GameObjects and their place relative to
            // the root; null when none.
            internal GameObject[] FxSources;
            // Per carried subtree, the LODGroup that switches its particle renderers (null: none), copied onto the carrier.
            internal FxLod[] FxLods;
            // Lab: why a particle subtree could not be carried.
            internal string FxWhy;
            internal Matrix4x4[] FxLocals;
            // ProxyTables.LodSets index of its one LOD rule (HasLod), -1 otherwise.
            internal int LodSet = -1;
            // Several LODGroups with rules of their own (a piece's state subtrees): each group's LodSets index, and per part
            // (template-local) its group (-1: in none); a variant shows the parts of one group at most. Null otherwise.
            internal LODGroup[] Groups;
            internal int[] GroupSets;
            internal int[] PartGroups;
            internal bool HasLod;
            // The LODGroup whose rule the template uses (its levels, fades and mode; any others are the same).
            internal LODGroup LodGroup;
            internal Matrix4x4 LodLocal;
            internal Vector3 LodLocalRef;
            internal float LodSize;
            internal readonly List<int> Variants = new List<int>();
            internal readonly Dictionary<long, int> VariantByHash = new Dictionary<long, int>();
            // Rows decoded with it now (lab).
            internal int Rows;

            internal bool Proxyable => Real == ProxyReal.None;
        }

        static readonly Dictionary<int, VisualTemplate> s_byPrefab = new Dictionary<int, VisualTemplate>();
        static readonly List<VisualTemplate> s_byId = new List<VisualTemplate>();
        static readonly HashSet<int> s_requested = new HashSet<int>();
        static readonly List<int> s_requests = new List<int>();
        static readonly HashSet<Material> s_rejected = new HashSet<Material>();
        static ZNetScene s_scene;
        static readonly int s_matSeedKey = "RandMatSeed".GetStableHashCode();

        // Lab: templates made (all, proxyable), the slowest make (ms), templates real by reason, and the components that
        // made templates real as Component by type name (what to review next).
        internal static int LabMade, LabProxyable;
        internal static double LabMakeMsMax;
        internal static readonly int[] LabRealTemplates = new int[(int)ProxyReal.Count];
        internal static readonly Dictionary<string, int> LabUnknownComponents = new Dictionary<string, int>();
        // Lab: the prefabs kept real for a reason other than an unreviewed component, with the detail (a few per reason).
        internal static readonly List<string> LabRealExamples = new List<string>();

        // Moves whenever a material is rejected: rows decoded before may have to be decoded again.
        internal static int RejectVersion;

        internal static int Count => s_byPrefab.Count;
        internal static int Pending => s_requests.Count;

        // The template of a prefab (made now when it has none). Null only without a scene.
        internal static VisualTemplate Get(int prefab)
        {
            if (s_byPrefab.TryGetValue(prefab, out VisualTemplate t))
                return t;
            ZNetScene scene = ZNetScene.instance;
            if (scene == null)
                return null;
            Sync(scene);
            return Make(prefab, scene);
        }

        // The template if it is made, else null (and it is queued: FarProxies makes queued ones within its budget).
        internal static VisualTemplate Peek(int prefab, bool request = true)
        {
            if (s_byPrefab.TryGetValue(prefab, out VisualTemplate t))
                return t;
            if (request && s_requested.Add(prefab))
                s_requests.Add(prefab);
            return null;
        }

        internal static VisualTemplate ById(int id) => id >= 0 && id < s_byId.Count ? s_byId[id] : null;

        // Makes queued templates until the budget (ms) is spent (at least one). Returns how many were made.
        internal static int MakeQueued(double budgetMs)
        {
            if (s_requests.Count == 0)
                return 0;
            ZNetScene scene = ZNetScene.instance;
            if (scene == null)
                return 0;
            Sync(scene);
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            double limit = budgetMs * System.Diagnostics.Stopwatch.Frequency / 1000.0;
            int made = 0;
            int i = 0;
            for (; i < s_requests.Count; i++)
            {
                if (made > 0 && System.Diagnostics.Stopwatch.GetTimestamp() - t0 > limit)
                    break;
                int prefab = s_requests[i];
                s_requested.Remove(prefab);
                if (s_byPrefab.ContainsKey(prefab))
                    continue;
                Make(prefab, scene);
                made++;
            }
            s_requests.RemoveRange(0, i);
            return made;
        }

        // The drawing part found a material it cannot draw instanced exactly (P1 failed for both rungs): every part with it
        // makes its state real from now on (Decode says Material), and rows are decoded again (RejectVersion).
        internal static void RejectMaterial(Material m)
        {
            if ((object)m != null && s_rejected.Add(m))
                RejectVersion++;
        }

        internal static bool Rejected(Material m) => s_rejected.Count > 0 && (object)m != null && s_rejected.Contains(m);

        // A new scene: the prefabs may not be the same objects, everything is made again (and the tables cleared).
        static void Sync(ZNetScene scene)
        {
            if (ReferenceEquals(scene, s_scene))
                return;
            Clear();
            s_scene = scene;
        }

        internal static void Clear()
        {
            s_byPrefab.Clear();
            s_byId.Clear();
            s_requested.Clear();
            s_requests.Clear();
            s_rejected.Clear();
            s_scene = null;
            Array.Clear(LabRealTemplates, 0, LabRealTemplates.Length);
            LabUnknownComponents.Clear();
            LabRealExamples.Clear();
            LabMade = LabProxyable = 0;
            ProxyTables.Clear();
            RejectVersion++;
        }

        // ---- making a template -------------------------------------------------------------------------------

        // Scratch of one make (templates are made one at a time, on the main thread).
        static readonly List<Transform> s_transforms = new List<Transform>();
        static readonly Dictionary<Transform, int> s_index = new Dictionary<Transform, int>();
        static readonly List<Component> s_components = new List<Component>();
        static readonly Dictionary<GameObject, int> s_goToggles = new Dictionary<GameObject, int>();
        static readonly Dictionary<Renderer, int> s_rendererToggles = new Dictionary<Renderer, int>();
        static readonly HashSet<GameObject> s_forcedOff = new HashSet<GameObject>();
        static readonly HashSet<Renderer> s_forcedDisabled = new HashSet<Renderer>();
        static readonly Dictionary<GameObject, int> s_toggleOwner = new Dictionary<GameObject, int>();
        static readonly List<bool> s_toggleDefaults = new List<bool>();
        static readonly List<int> s_req = new List<int>();
        static readonly List<TPart> s_parts = new List<TPart>();
        static readonly List<TRenderer> s_renderers = new List<TRenderer>();
        static readonly Dictionary<Renderer, int> s_lodMask = new Dictionary<Renderer, int>();
        static readonly List<GameObject> s_containerGos = new List<GameObject>();

        static VisualTemplate Make(int prefab, ZNetScene scene)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            GameObject asset = scene.GetPrefab(prefab);
            var t = new VisualTemplate { Prefab = prefab, Asset = asset, Name = asset != null ? asset.name : prefab.ToString() };
            try
            {
                Walk(t);
            }
            catch (Exception e)
            {
                // Whatever the prefab holds that this does not expect: real.
                SetReal(t, ProxyReal.Structure, "template: " + e.GetType().Name + " " + e.Message);
            }
            finally
            {
                ClearScratch();
            }
            if (t.Proxyable)
            {
                try
                {
                    Publish(t);
                }
                catch (Exception e)
                {
                    // Parts it put into the tables stay unused; the template ids stay in step with the tables.
                    if (s_byId.Count > ProxyTables.TemplateCount)
                        s_byId.RemoveAt(s_byId.Count - 1);
                    t.Id = -1;
                    SetReal(t, ProxyReal.Structure, "publish: " + e.GetType().Name + " " + e.Message);
                }
            }
            s_byPrefab[prefab] = t;
            LabMade++;
            if (t.Proxyable)
                LabProxyable++;
            else
            {
                LabRealTemplates[(int)t.Real]++;
                if (t.Real == ProxyReal.Component && t.Why != null)
                {
                    LabUnknownComponents.TryGetValue(t.Why, out int n);
                    LabUnknownComponents[t.Why] = n + 1;
                }
                else if (LabRealExamples.Count < 60)
                    LabRealExamples.Add($"{t.Name}: {t.Real} ({t.Why}{(t.FxWhy != null ? "; " + t.FxWhy : "")})");
            }
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (ms > LabMakeMsMax)
                LabMakeMsMax = ms;
            return t;
        }

        static void ClearScratch()
        {
            s_transforms.Clear();
            s_index.Clear();
            s_components.Clear();
            s_goToggles.Clear();
            s_rendererToggles.Clear();
            s_forcedOff.Clear();
            s_forcedDisabled.Clear();
            s_toggleOwner.Clear();
            s_toggleDefaults.Clear();
            s_req.Clear();
            s_parts.Clear();
            s_renderers.Clear();
            s_lodMask.Clear();
            s_containerGos.Clear();
            s_partLocal.Clear();
            s_partLod.Clear();
            s_partGroup.Clear();
            s_rock5Areas.Clear();
        }

        static void SetReal(VisualTemplate t, ProxyReal why, string detail)
        {
            if (t.Real != ProxyReal.None)
                return;
            t.Real = why;
            t.Why = detail;
        }

        // A toggle for a GameObject a decoder (owner) sets; one object set by two decoders: their order decides, real.
        static int GoToggle(VisualTemplate t, GameObject go, int owner)
        {
            if (go == null)
                return -1;
            if (s_goToggles.TryGetValue(go, out int id))
            {
                if (s_toggleOwner.TryGetValue(go, out int was) && was != owner)
                    SetReal(t, ProxyReal.Structure, "two decoders set " + go.name);
                return id;
            }
            id = s_toggleDefaults.Count;
            s_toggleDefaults.Add(go.activeSelf);
            s_goToggles.Add(go, id);
            s_toggleOwner[go] = owner;
            return id;
        }

        static int RendererToggle(Renderer r)
        {
            if (s_rendererToggles.TryGetValue(r, out int id))
                return id;
            id = s_toggleDefaults.Count;
            s_toggleDefaults.Add(r.enabled);
            s_rendererToggles.Add(r, id);
            return id;
        }

        const int OwnerWear = 1, OwnerPick = 2, OwnerRock = 3, OwnerRock5 = 4, OwnerChest = 5;

        static void Walk(VisualTemplate t)
        {
            GameObject asset = t.Asset;
            if (asset == null)
            {
                SetReal(t, ProxyReal.NoPrefab, "no prefab");
                return;
            }
            Transform root = asset.transform;
            if (!asset.activeSelf)
            {
                SetReal(t, ProxyReal.Structure, "inactive root");
                return;
            }
            t.RootScale = root.localScale;
            ZNetView nview = asset.GetComponent<ZNetView>();
            if (nview == null)
            {
                SetReal(t, ProxyReal.Structure, "no ZNetView on the root");
                return;
            }
            if (!nview.m_persistent)
            {
                SetReal(t, ProxyReal.NotPersistent, "not persistent");
                return;
            }
            t.SyncScale = nview.m_syncInitialScale;
            if (asset.GetComponent<TeleportWorld>() != null)
            {
                SetReal(t, ProxyReal.Portal, "portal");
                return;
            }
            root.GetComponentsInChildren(true, s_transforms);
            for (int i = 0; i < s_transforms.Count; i++)
                s_index[s_transforms[i]] = i;
            Matrix4x4 rootInv = root.worldToLocalMatrix;

            // Decoders (on the root; one elsewhere is not what the game's prefabs do: real).
            Decoders(t, asset);
            if (!t.Proxyable)
                return;

            // What can ever wake: a GameObject whose path from the root is active or switched by a decoder, and not one the
            // game switches off for good. Components there are reviewed; parts there are made.
            int n = s_transforms.Count;
            var canWake = new bool[n];
            var parent = new int[n];
            for (int i = 0; i < n; i++)
            {
                Transform tr = s_transforms[i];
                parent[i] = tr == root ? -1 : s_index.TryGetValue(tr.parent, out int p) ? p : -1;
                GameObject go = tr.gameObject;
                bool self = (go.activeSelf || s_goToggles.ContainsKey(go)) && !s_forcedOff.Contains(go);
                canWake[i] = self && (parent[i] < 0 || canWake[parent[i]]);
            }
            // Particle carriers: a subtree below the root that holds only particle systems (Transform, ParticleSystem,
            // ParticleSystemRenderer, GlobalWind), always awake, switched by one LODGroup at most (copied onto the carrier), runs as a copy of its own where the real
            // object's would stand (FarProxies), simulated by the game as the real one is. Its components are not reviewed.
            var carried = new bool[n];
            List<GameObject> fxSources = null;
            List<Matrix4x4> fxLocals = null;
            List<FxLod> fxLods = null;
            LODGroup[] allGroups = asset.GetComponentsInChildren<LODGroup>(true);
            foreach (ParticleSystem ps in asset.GetComponentsInChildren<ParticleSystem>(true))
            {
                int pi = s_index[ps.transform];
                if (!canWake[pi] || carried[pi] || ps.transform == root)
                    continue;
                Transform top = ps.transform;
                while (top.parent != null && top.parent != root && OnlyParticles(top.parent))
                    top = top.parent;
                FxLod fxLod = null;
                string no = !OnlyParticles(top) ? "not only particles under " + top.name + ": " + NotParticle(top)
                    : !AlwaysAwake(s_index[top], parent) ? top.name + " switched"
                    : !LodOf(top, allGroups, rootInv, out fxLod) ? top.name + " in two LODGroups" : null;
                if (no != null)
                {
                    t.FxWhy = no;
                    continue;
                }
                foreach (Transform c in top.GetComponentsInChildren<Transform>(true))
                    carried[s_index[c]] = true;
                (fxSources ??= new List<GameObject>()).Add(top.gameObject);
                (fxLocals ??= new List<Matrix4x4>()).Add(rootInv * top.localToWorldMatrix);
                (fxLods ??= new List<FxLod>()).Add(fxLod);
            }
            t.FxSources = fxSources?.ToArray();
            t.FxLocals = fxLocals?.ToArray();
            t.FxLods = fxLods?.ToArray();
            for (int i = 0; i < n && t.Proxyable; i++)
            {
                if (!canWake[i] || carried[i])
                    continue;
                s_transforms[i].GetComponents(s_components);
                for (int c = 0; c < s_components.Count; c++)
                {
                    ProxyReal why = Review(s_components[c], s_transforms[i] == root, out string detail);
                    if (why != ProxyReal.None)
                    {
                        SetReal(t, why, detail);
                        break;
                    }
                }
            }
            if (!t.Proxyable)
                return;

            // LODGroups. A piece carries one per state subtree (new, worn, broken), a pickable one on the part picking hides:
            // they make one LOD rule for the template when every group that can wake picks its level from the same reference
            // point and size with the same levels, fade widths and fade mode (then which of them runs does not change the
            // level) and a switched group's renderers lie under it (off with it); otherwise the object stays real.
            LODGroup lodGroup = null;
            LOD[] lods = null;
            Vector3 lodRef = default;
            float lodSize = 0f;
            bool uniform = true;
            var groupList = new List<LODGroup>();
            s_lodGroupOf.Clear();
            foreach (LODGroup g in asset.GetComponentsInChildren<LODGroup>(true))
            {
                int gi = s_index[g.transform];
                if (!canWake[gi])
                    continue;
                if (!g.enabled)
                {
                    SetReal(t, ProxyReal.Lod, "LODGroup off");
                    return;
                }
                // SpeedTree's fade (a morph within a level, the last one dithered) is not replicated by ProxyDraw.
                if (g.fadeMode == LODFadeMode.SpeedTree)
                {
                    SetReal(t, ProxyReal.Lod, "SpeedTree LOD fade");
                    return;
                }
                LOD[] gl = g.GetLODs();
                if (gl.Length > 8)
                {
                    SetReal(t, ProxyReal.Lod, gl.Length + " LOD levels");
                    return;
                }
                Matrix4x4 gm = rootInv * g.transform.localToWorldMatrix;
                Vector3 r = gm.MultiplyPoint3x4(g.localReferencePoint);
                float s = g.size * Mathf.Max(gm.GetColumn(0).magnitude, Mathf.Max(gm.GetColumn(1).magnitude, gm.GetColumn(2).magnitude));
                if (lodGroup == null)
                {
                    lodGroup = g;
                    lods = gl;
                    lodRef = r;
                    lodSize = s;
                }
                else if (!SameLods(lods, gl) || g.fadeMode != lodGroup.fadeMode || g.animateCrossFading != lodGroup.animateCrossFading ||
                         (r - lodRef).sqrMagnitude > 1e-6f || Mathf.Abs(s - lodSize) > 1e-4f)
                    uniform = false;
                if (!AlwaysAwake(gi, parent) && !RenderersUnder(g, gl))
                {
                    SetReal(t, ProxyReal.Lod, "switched LODGroup with renderers outside it");
                    return;
                }
                int gIndex = groupList.Count;
                groupList.Add(g);
                for (int level = 0; level < gl.Length; level++)
                {
                    Renderer[] rs = gl[level].renderers;
                    if (rs == null)
                        continue;
                    foreach (Renderer rr in rs)
                        if (rr != null)
                        {
                            s_lodMask.TryGetValue(rr, out int mask);
                            s_lodMask[rr] = mask | (1 << level);
                            // A renderer two groups switch has no one rule: the variants that show it stay real.
                            s_lodGroupOf[rr] = s_lodGroupOf.TryGetValue(rr, out int had) && had != gIndex ? -2 : gIndex;
                        }
                }
            }
            if (lodGroup != null && !uniform)
            {
                // Each group's own rule; a variant picks the one whose parts it shows (Decode).
                t.Groups = groupList.ToArray();
                t.HasLod = false;
            }
            else if (lodGroup != null)
            {
                t.HasLod = lods.Length > 0;
                t.LodGroup = lodGroup;
                t.LodLocal = rootInv * lodGroup.transform.localToWorldMatrix;
                t.LodLocalRef = lodGroup.localReferencePoint;
                t.LodSize = lodGroup.size;
            }

            // MineRock5 puts its hit areas into one combined renderer on the root in LOD0 (UpdateMesh, SetLODs): LOD0
            // becomes that renderer and the extras, the hit-area renderers go off.
            int combinedMask = 0;
            if (t.Rock5 != null)
            {
                if (lodGroup == null || lodGroup.transform != root || !t.HasLod)
                {
                    SetReal(t, ProxyReal.Structure, "MineRock5 without its LODGroup");
                    return;
                }
                combinedMask = Rock5Lod0(t, asset, root);
                if (!t.Proxyable)
                    return;
            }

            // MaterialMan containers: the root's (WearNTear's _SnowLevel) and each RandomMaterialValues' GameObject; one
            // GameObject is one container whoever sets on it.
            WearNTear wear = asset.GetComponent<WearNTear>();
            if (wear != null && wear.m_snow != null)
            {
                s_containerGos.Add(asset);
                t.RootContainer = true;
            }
            RandomMaterialValues[] rmvs = asset.GetComponentsInChildren<RandomMaterialValues>(true);
            var rmvDefs = new List<RmvDef>();
            foreach (RandomMaterialValues rmv in rmvs)
            {
                if (!canWake[s_index[rmv.transform]] || !rmv.enabled)
                    continue;
                int c = s_containerGos.IndexOf(rmv.gameObject);
                if (c < 0)
                {
                    c = s_containerGos.Count;
                    s_containerGos.Add(rmv.gameObject);
                }
                rmvDefs.Add(new RmvDef { Container = c, Component = rmv });
            }
            if (s_containerGos.Count > 30)
            {
                SetReal(t, ProxyReal.Structure, s_containerGos.Count + " material containers");
                return;
            }
            t.ContainerCount = s_containerGos.Count;
            t.Rmvs = rmvDefs.Count > 0 ? rmvDefs.ToArray() : null;

            // Parts: every MeshRenderer that can wake, one per material slot.
            for (int i = 0; i < n && t.Proxyable; i++)
            {
                if (!canWake[i])
                    continue;
                Transform tr = s_transforms[i];
                MeshRenderer mr = tr.GetComponent<MeshRenderer>();
                if (mr == null || s_forcedDisabled.Contains(mr))
                    continue;
                int rendererToggle = s_rendererToggles.TryGetValue(mr, out int rt) ? rt : -1;
                if (rendererToggle < 0 && !mr.enabled)
                    continue;
                if (mr.forceRenderingOff)
                    continue;
                MeshFilter mf = tr.GetComponent<MeshFilter>();
                Mesh mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null || mesh.subMeshCount == 0)
                    continue;
                if (mr.additionalVertexStreams != null || mr.enlightenVertexStream != null)
                {
                    SetReal(t, ProxyReal.Renderer, "extra vertex streams on " + tr.name);
                    break;
                }
                if (mr.probeAnchor != null || mr.lightProbeProxyVolumeOverride != null || mr.lightProbeUsage == LightProbeUsage.UseProxyVolume)
                {
                    SetReal(t, ProxyReal.Renderer, "probe anchor or proxy volume on " + tr.name);
                    break;
                }
                Material[] mats = mr.sharedMaterials;
                if (mats.Length == 0)
                    continue;
                var r = new TRenderer
                {
                    Mesh = mesh, Shadows = mr.shadowCastingMode, Receive = mr.receiveShadows, StaticShadow = mr.staticShadowCaster, Layer = tr.gameObject.layer,
                    LightProbes = mr.lightProbeUsage, ReflectionProbes = mr.reflectionProbeUsage, Motion = mr.motionVectorGenerationMode,
                    RenderingLayers = mr.renderingLayerMask, Go = i,
                };
                int ri = s_renderers.Count;
                s_renderers.Add(r);
                int lodMask = lodGroup == null ? ProxyPartData.LodAll : s_lodMask.TryGetValue(mr, out int lm) ? lm : ProxyPartData.LodAll;
                s_partGroupNow = t.Groups != null && s_lodGroupOf.TryGetValue(mr, out int pg) ? pg : -1;
                Matrix4x4 local = rootInv * tr.localToWorldMatrix;
                // Unity flips the winding of a mirrored renderer (negative determinant) per renderer; one instanced draw
                // cannot (ProxyDraw), so such a prefab stays real.
                if (local.determinant * t.RootScale.x * t.RootScale.y * t.RootScale.z < 0f)
                {
                    SetReal(t, ProxyReal.Renderer, "mirrored (negative scale) " + tr.name);
                    break;
                }
                for (int k = 0; k < mats.Length; k++)
                {
                    if (!Opaque(t, mats[k], tr.name))
                        break;
                    AddPart(t, ri, k, Math.Min(k, mesh.subMeshCount - 1), mats[k], i, parent, rendererToggle, local, lodMask);
                }
            }
            if (!t.Proxyable)
                return;
            if (t.Rock5 != null)
                Rock5Parts(t, asset, root, rootInv, parent, combinedMask);
            if (!t.Proxyable)
                return;

            // MaterialVariation: the material of one slot of its renderer, from the ZDO.
            MaterialVariation[] mvs = asset.GetComponentsInChildren<MaterialVariation>(true);
            var mvDefs = new List<MatVarDef>();
            foreach (MaterialVariation mv in mvs)
            {
                // Its Awake starts CheckMaterial whether it is enabled or not (Invoke runs on a disabled behaviour).
                if (!canWake[s_index[mv.transform]])
                    continue;
                // MineRock5 sorts its hit areas' submeshes by material identity at every UpdateMesh, which the instance
                // copies a variation makes would change.
                if (t.Rock5 != null)
                {
                    SetReal(t, ProxyReal.Structure, "MaterialVariation on a MineRock5");
                    return;
                }
                if (mv.GetComponent<SkinnedMeshRenderer>() != null)
                {
                    SetReal(t, ProxyReal.Animated, "MaterialVariation on a skinned renderer");
                    return;
                }
                MeshRenderer mr = mv.GetComponent<MeshRenderer>();
                int ri = -1;
                for (int i = 0; i < s_renderers.Count; i++)
                    if (!s_renderers[i].Combined && s_transforms[s_renderers[i].Go] == mv.transform)
                        ri = i;
                if (mr == null || mv.m_materialIndex < 0 || mv.m_materialIndex >= mr.sharedMaterials.Length)
                {
                    SetReal(t, ProxyReal.Structure, "MaterialVariation without its slot on " + mv.name);
                    return;
                }
                var options = new Material[mv.m_materials.Count];
                for (int i = 0; i < options.Length; i++)
                {
                    options[i] = mv.m_materials[i].m_material;
                    if (options[i] != null && options[i].renderQueue >= (int)RenderQueue.GeometryLast + 1)
                    {
                        SetReal(t, ProxyReal.Material, "transparent variation " + options[i].name);
                        return;
                    }
                    if (options[i] != null && !ProxyDraw.Instanceable(options[i]))
                    {
                        SetReal(t, ProxyReal.Material, "not instanceable variation " + options[i].name);
                        return;
                    }
                }
                // A renderer that never shows (disabled, under a switched-off object) needs nothing.
                if (ri >= 0)
                    mvDefs.Add(new MatVarDef { Renderer = ri, Slot = mv.m_materialIndex, Key = ("MatVar" + mv.m_materialIndex).GetStableHashCode(), Options = options });
            }
            t.MatVars = mvDefs.Count > 0 ? mvDefs.ToArray() : null;

            // Which containers cover each part (MaterialMan's GetComponentsInChildren<MeshRenderer>(true) of its object).
            if (t.ContainerCount > 0)
                for (int p = 0; p < s_parts.Count; p++)
                {
                    TPart part = s_parts[p];
                    int go = part.Renderer >= 0 ? s_renderers[part.Renderer].Go : 0;
                    int mask = 0;
                    for (int c = 0; c < s_containerGos.Count; c++)
                    {
                        int cg = s_index[s_containerGos[c].transform];
                        for (int a = go; a >= 0; a = parent[a])
                            if (a == cg)
                            {
                                mask |= 1 << c;
                                break;
                            }
                    }
                    part.Containers = mask;
                    s_parts[p] = part;
                }

            t.ToggleCount = s_toggleDefaults.Count;
            t.ToggleDefault = s_toggleDefaults.ToArray();
            t.Req = s_req.ToArray();
            t.Parts = s_parts.ToArray();
            t.PartLocals = s_partLocal.ToArray();
            t.PartLods = s_partLod.ToArray();
            t.PartGroups = t.Groups != null ? s_partGroup.ToArray() : null;
            t.Renderers = s_renderers.ToArray();
        }

        // The object at index i wakes in every instance and nothing switches it (nor any object above it).
        // Only particle systems in the subtree (what a carrier copy holds), with the game's GlobalWind that blows them (it acts
        // on its own GameObject's system from the game's wind, so a copy does what the real one does).
        static bool OnlyParticles(Transform top)
        {
            foreach (Component c in top.GetComponentsInChildren<Component>(true))
                if (!(c is Transform) && !(c is ParticleSystem) && !(c is ParticleSystemRenderer) && !(c is GlobalWind))
                    return false;
            return true;
        }

        // Lab: the first component of the subtree that is not a particle system's.
        static string NotParticle(Transform top)
        {
            foreach (Component c in top.GetComponentsInChildren<Component>(true))
                if (!(c is Transform) && !(c is ParticleSystem) && !(c is ParticleSystemRenderer) && !(c is GlobalWind))
                    return c == null ? "missing script" : c.GetType().Name + " on " + c.name;
            return "-";
        }

        // The LODGroup switching the subtree's particle renderers, as the carrier's own copy needs it: per renderer of the
        // subtree (GetComponentsInChildren order, which a copy keeps) its level bits, and the group's rule and place
        // relative to the root. Null when none switches them; false when two groups do.
        internal sealed class FxLod
        {
            internal int[] Masks;
            internal float[] Heights, Fades;
            internal LODFadeMode FadeMode;
            internal bool Animate;
            internal Matrix4x4 GroupLocal;
            internal Vector3 RefLocal;
            internal float Size;
        }

        static bool LodOf(Transform top, LODGroup[] groups, Matrix4x4 rootInv, out FxLod lod)
        {
            lod = null;
            Renderer[] rs = top.GetComponentsInChildren<Renderer>(true);
            LODGroup found = null;
            int[] masks = new int[rs.Length];
            foreach (LODGroup g in groups)
            {
                LOD[] ls = g.GetLODs();
                for (int level = 0; level < ls.Length; level++)
                    if (ls[level].renderers != null)
                        foreach (Renderer r in ls[level].renderers)
                        {
                            int i = r != null ? Array.IndexOf(rs, r) : -1;
                            if (i < 0)
                                continue;
                            if (found != null && found != g)
                                return false;
                            found = g;
                            masks[i] |= 1 << level;
                        }
            }
            if (found == null)
                return true;
            LOD[] gl = found.GetLODs();
            lod = new FxLod
            {
                Masks = masks,
                Heights = new float[gl.Length],
                Fades = new float[gl.Length],
                FadeMode = found.fadeMode,
                Animate = found.animateCrossFading,
                GroupLocal = rootInv * found.transform.localToWorldMatrix,
                RefLocal = found.localReferencePoint,
                Size = found.size,
            };
            for (int i = 0; i < gl.Length; i++)
            {
                lod.Heights[i] = gl[i].screenRelativeTransitionHeight;
                lod.Fades[i] = gl[i].fadeTransitionWidth;
            }
            return true;
        }

        // The levels pick alike: the same count, transition heights and fade widths.
        static bool SameLods(LOD[] a, LOD[] b)
        {
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
                if (Mathf.Abs(a[i].screenRelativeTransitionHeight - b[i].screenRelativeTransitionHeight) > 1e-6f ||
                    Mathf.Abs(a[i].fadeTransitionWidth - b[i].fadeTransitionWidth) > 1e-6f)
                    return false;
            return true;
        }

        // Every renderer the group switches lies in its own subtree.
        static bool RenderersUnder(LODGroup g, LOD[] lods)
        {
            foreach (LOD l in lods)
                if (l.renderers != null)
                    foreach (Renderer r in l.renderers)
                        if (r != null && !r.transform.IsChildOf(g.transform))
                            return false;
            return true;
        }

        static bool AlwaysAwake(int i, int[] parent)
        {
            for (int a = i; a >= 0; a = parent[a])
            {
                GameObject go = s_transforms[a].gameObject;
                if (!go.activeSelf || s_goToggles.ContainsKey(go) || s_forcedOff.Contains(go))
                    return false;
            }
            return true;
        }

        static bool Opaque(VisualTemplate t, Material m, string where)
        {
            if (m == null || m.shader == null)
            {
                SetReal(t, ProxyReal.Material, "missing material on " + where);
                return false;
            }
            // Transparent queues sort per object: instanced draws would not keep the game's order.
            if (m.renderQueue > (int)RenderQueue.GeometryLast)
            {
                SetReal(t, ProxyReal.Material, "transparent " + m.name + " on " + where);
                return false;
            }
            // Drawn instanced (ARCHITECTURE-v2.md §3.7, rungs 1-2): the material with instancing on, or a copy with it on
            // when its shader has instanced variants (ProxyDraw); neither: real.
            if (!ProxyDraw.Instanceable(m))
            {
                SetReal(t, ProxyReal.Material, "not instanceable " + m.name + " on " + where);
                return false;
            }
            return true;
        }

        // A part of renderer ri, slot k, at the transform index go: its requirements are the toggles on its path (and its
        // renderer's switch); a path through an object inactive in the prefab that nothing switches never shows.
        static void AddPart(VisualTemplate t, int ri, int slot, int submesh, Material m, int go, int[] parent, int rendererToggle, in Matrix4x4 local, int lodMask)
        {
            int reqStart = s_req.Count;
            if (rendererToggle >= 0)
                s_req.Add(rendererToggle);
            for (int a = go; a >= 0; a = parent[a])
            {
                GameObject g = s_transforms[a].gameObject;
                if (s_goToggles.TryGetValue(g, out int id))
                {
                    if (s_req.IndexOf(id, reqStart) < 0)
                        s_req.Add(id);
                }
                else if (!g.activeSelf || s_forcedOff.Contains(g))
                {
                    s_req.RemoveRange(reqStart, s_req.Count - reqStart);
                    return;
                }
            }
            Mesh mesh = s_renderers[ri].Mesh;
            Bounds b = FarPage.WorldBounds(mesh.GetSubMesh(submesh).bounds, local);
            var part = new TPart
            {
                Renderer = ri, Slot = slot, Submesh = submesh, Material = m, ReqStart = reqStart, ReqCount = s_req.Count - reqStart,
                Center = b.center, Extents = b.extents,
            };
            s_parts.Add(part);
            s_partLocal.Add(local);
            s_partLod.Add(lodMask);
            s_partGroup.Add(s_partGroupNow);
        }

        static readonly List<Matrix4x4> s_partLocal = new List<Matrix4x4>();
        static readonly List<int> s_partLod = new List<int>();
        // Per part the LODGroup index (VisualTemplate.Groups) the renderer being added belongs to (-1 none, -2 two).
        static readonly List<int> s_partGroup = new List<int>();
        static readonly Dictionary<Renderer, int> s_lodGroupOf = new Dictionary<Renderer, int>();
        static int s_partGroupNow = -1;

        // The tables get the template's parts (with the prefab's own materials) and its LOD description.
        static void Publish(VisualTemplate t)
        {
            var data = new ProxyTemplateData
            {
                Prefab = t.Prefab,
                LodCount = 0,
                LodLocal = Matrix4x4.identity,
            };
            if (t.HasLod)
            {
                LODGroup g = t.LodGroup;
                LOD[] lods = g.GetLODs();
                data.LodCount = lods.Length;
                data.FadeMode = (int)g.fadeMode;
                data.AnimateCrossFading = g.animateCrossFading;
                data.LodSize = t.LodSize;
                data.LodLocalRef = t.LodLocalRef;
                data.LodLocal = t.LodLocal;
                for (int i = 0; i < lods.Length; i++)
                {
                    if (i < 4)
                    {
                        data.HeightsA[i] = lods[i].screenRelativeTransitionHeight;
                        data.FadeA[i] = lods[i].fadeTransitionWidth;
                    }
                    else
                    {
                        data.HeightsB[i - 4] = lods[i].screenRelativeTransitionHeight;
                        data.FadeB[i - 4] = lods[i].fadeTransitionWidth;
                    }
                }
            }
            data.LodFadeInOut = t.Asset.GetComponentInChildren<LodFadeInOut>(true) != null;
            if (t.HasLod)
                t.LodSet = ProxyTables.AddLodSet(data);
            if (t.Groups != null)
            {
                t.GroupSets = new int[t.Groups.Length];
                Matrix4x4 rootInv = t.Asset.transform.worldToLocalMatrix;
                for (int gi = 0; gi < t.Groups.Length; gi++)
                {
                    LODGroup g = t.Groups[gi];
                    LOD[] gl = g.GetLODs();
                    var set = new ProxyTemplateData
                    {
                        Prefab = t.Prefab,
                        LodCount = gl.Length,
                        FadeMode = (int)g.fadeMode,
                        AnimateCrossFading = g.animateCrossFading,
                        LodSize = g.size,
                        LodLocalRef = g.localReferencePoint,
                        LodLocal = rootInv * g.transform.localToWorldMatrix,
                        LodFadeInOut = data.LodFadeInOut,
                    };
                    for (int i = 0; i < gl.Length; i++)
                    {
                        if (i < 4)
                        {
                            set.HeightsA[i] = gl[i].screenRelativeTransitionHeight;
                            set.FadeA[i] = gl[i].fadeTransitionWidth;
                        }
                        else
                        {
                            set.HeightsB[i - 4] = gl[i].screenRelativeTransitionHeight;
                            set.FadeB[i - 4] = gl[i].fadeTransitionWidth;
                        }
                    }
                    t.GroupSets[gi] = ProxyTables.AddLodSet(set);
                }
            }
            t.Id = s_byId.Count;
            s_byId.Add(t);
            data.PartStart = ProxyTables.PartCount;
            data.PartCount = t.Parts.Length;
            for (int p = 0; p < t.Parts.Length; p++)
            {
                TPart part = t.Parts[p];
                TRenderer r = t.Renderers[part.Renderer];
                int batch = ProxyTables.InternBatch(Key(r, part.Submesh, part.Material));
                part.Global = ProxyTables.AddPart(new ProxyPartData
                {
                    Local = t.PartLocals[p], Center = part.Center, Extents = part.Extents, Batch = batch, LodMask = t.PartLods[p], Template = t.Id,
                });
                t.Parts[p] = part;
            }
            int id = ProxyTables.AddTemplate(data);
            if (id != t.Id)
                throw new InvalidOperationException("template tables out of step");
        }

        static ProxyBatchKey Key(TRenderer r, int submesh, Material m) =>
            new ProxyBatchKey(r.Mesh, submesh, m, r.Shadows, r.Receive, r.Layer, r.LightProbes, r.ReflectionProbes, r.Motion, r.RenderingLayers, r.StaticShadow);

        // ---- the whitelist -----------------------------------------------------------------------------------

        // Whether a component can be on a proxied prefab: its effect on the picture, at instantiation and while the object
        // is not simulated (outside every active area nobody owns it, or its owner is far), is none or is what a decoder
        // reproduces from the ZDO. Everything not listed stays real (other mods' components among them).
        static ProxyReal Review(Component c, bool onRoot, out string detail)
        {
            detail = null;
            if (c == null)
            {
                detail = "missing script";
                return ProxyReal.Component;
            }
            switch (c)
            {
                case Transform _:
                case MeshFilter _:
                case MeshRenderer _:
                case LODGroup _:
                case BoxCollider _:
                case SphereCollider _:
                case CapsuleCollider _:
                case MeshCollider _:
                case Piece _:
                case MaterialVariation _:
                case RandomMaterialValues _:
                case LodFadeInOut _:
                case StaticPhysics _:
                case ItemStyle _:
                case HoverText _:
                case Chair _:
                case Ladder _:
                case Bed _:
                case CircleProjector _:
                // Both act only when a real instance is destroyed or damaged, which a proxied far object never is.
                case DropOnDestroyed _:
                case SpawnOnDamaged _:
                    return ProxyReal.None;
                case ZNetView _:
                case WearNTear _:
                case Pickable _:
                case MineRock _:
                case MineRock5 _:
                case TreeBase _:
                case CraftingStation _:
                case Container _:
                    if (onRoot)
                        return ProxyReal.None;
                    detail = c.GetType().Name + " off the root";
                    return ProxyReal.Structure;
                case Destructible d:
                    if (d.m_ttl > 0f)
                    {
                        detail = "Destructible with a time to live";
                        return ProxyReal.Behaviour;
                    }
                    return onRoot ? ProxyReal.None : ProxyReal.Structure;
                case StationExtension se:
                    if (se.m_continousConnection)
                    {
                        detail = "StationExtension drawing its connection";
                        return ProxyReal.Behaviour;
                    }
                    return ProxyReal.None;
                case TeleportWorld _:
                    detail = "portal";
                    return ProxyReal.Portal;
                case Character _:
                case BaseAI _:
                case ItemDrop _:
                case Fish _:
                case RandomFlyingBird _:
                    detail = c.GetType().Name;
                    return ProxyReal.Life;
                case ParticleSystem _:
                case ParticleSystemRenderer _:
                case Light _:
                case LightFlicker _:
                case LightLod _:
                case AudioSource _:
                case ZSFX _:
                case ReflectionProbe _:
                    detail = c.GetType().Name;
                    return ProxyReal.Fx;
                case SkinnedMeshRenderer _:
                case Cloth _:
                    detail = c.GetType().Name;
                    return ProxyReal.Animated;
                case Rigidbody _:
                case ZSyncTransform _:
                case Floating _:
                    detail = c.GetType().Name;
                    return ProxyReal.Physics;
                case Renderer _:
                    detail = c.GetType().Name;
                    return ProxyReal.Renderer;
            }
            // Unity's navmesh obstacle (AIModule, not referenced here): it carves where agents walk, which only happens in
            // active areas (real there).
            string full = c.GetType().FullName;
            if (full == "UnityEngine.AI.NavMeshObstacle")
                return ProxyReal.None;
            // Animator and Animation (AnimationModule, not referenced here either).
            if (full == "UnityEngine.Animator" || full == "UnityEngine.Animation")
            {
                detail = full;
                return ProxyReal.Animated;
            }
            detail = full;
            return ProxyReal.Component;
        }

        // ---- decoders' fields --------------------------------------------------------------------------------

        static void Decoders(VisualTemplate t, GameObject asset)
        {
            WearNTear wear = asset.GetComponent<WearNTear>();
            if (wear != null)
            {
                var w = new WearDef { Health = wear.m_health };
                int states = (wear.m_new != null ? 1 : 0) + (wear.m_worn != null ? 1 : 0) + (wear.m_broken != null ? 1 : 0);
                if (states != 0 && states != 3)
                {
                    // SetHealthVisual reaches a null one at the first state change (an exception in Awake).
                    SetReal(t, ProxyReal.Structure, "WearNTear with part of its states");
                    return;
                }
                if (wear.m_new == asset || wear.m_worn == asset || wear.m_broken == asset)
                {
                    SetReal(t, ProxyReal.Structure, "WearNTear state on the root");
                    return;
                }
                w.HasStates = states == 3;
                if (w.HasStates)
                {
                    w.New = GoToggle(t, wear.m_new, OwnerWear);
                    w.Worn = GoToggle(t, wear.m_worn, OwnerWear);
                    w.Broken = GoToggle(t, wear.m_broken, OwnerWear);
                }
                w.HasSnow = wear.m_snow != null;
                if (wear.m_snow != null)
                    w.Snow = GoToggle(t, wear.m_snow.gameObject, OwnerWear);
                if (wear.m_snowWorn != null)
                    w.SnowWorn = GoToggle(t, wear.m_snowWorn.gameObject, OwnerWear);
                if (wear.m_snowBroken != null)
                    w.SnowBroken = GoToggle(t, wear.m_snowBroken.gameObject, OwnerWear);
                t.Wear = w;
            }
            Pickable pick = asset.GetComponent<Pickable>();
            if (pick != null)
            {
                t.Pick = new PickDef { DefaultPicked = pick.m_defaultPicked, RespawnMinutes = pick.m_respawnTimeMinutes };
                if (pick.m_hideWhenPicked != null)
                {
                    if (pick.m_hideWhenPicked == asset)
                    {
                        SetReal(t, ProxyReal.Structure, "Pickable hides its root");
                        return;
                    }
                    t.Pick.Hide = GoToggle(t, pick.m_hideWhenPicked, OwnerPick);
                }
            }
            MineRock rock = asset.GetComponent<MineRock>();
            if (rock != null)
                RockFields(t, asset, rock);
            if (!t.Proxyable)
                return;
            MineRock5 rock5 = asset.GetComponent<MineRock5>();
            if (rock5 != null)
            {
                if (rock != null || wear != null || pick != null)
                {
                    SetReal(t, ProxyReal.Structure, "MineRock5 with other decoders");
                    return;
                }
                Rock5Fields(t, asset, rock5);
            }
            if (!t.Proxyable)
                return;
            TreeBase tree = asset.GetComponent<TreeBase>();
            if (tree != null)
            {
                t.Tree = true;
                t.TreeHealth = tree.m_health;
            }
            CraftingStation station = asset.GetComponent<CraftingStation>();
            if (station != null)
            {
                // Start switches the area marker off; the in-use object shows only for a second after use; a station
                // that needs fire shows its fire object by what burns around it (runtime).
                if (station.m_areaMarker != null)
                    s_forcedOff.Add(station.m_areaMarker);
                if (station.m_inUseObject != null)
                    s_forcedOff.Add(station.m_inUseObject);
                if (station.m_craftRequireFire && station.m_haveFireObject != null)
                {
                    SetReal(t, ProxyReal.Behaviour, "CraftingStation showing fire");
                    return;
                }
            }
            Container chest = asset.GetComponent<Container>();
            if (chest != null)
            {
                if (chest.m_rootObjectOverride != null)
                {
                    SetReal(t, ProxyReal.Structure, "Container with a root override");
                    return;
                }
                if (chest.m_autoDestroyEmpty)
                {
                    SetReal(t, ProxyReal.Behaviour, "Container destroying itself when empty");
                    return;
                }
                if (chest.m_open != null || chest.m_closed != null)
                    t.Chest = new ChestDef { Open = GoToggle(t, chest.m_open, OwnerChest), Closed = GoToggle(t, chest.m_closed, OwnerChest) };
            }
        }

        // MineRock.Start: its hit areas are the colliders under m_areaRoot (or the root) on objects active then; with a
        // base model, each area's own MeshRenderers too.
        static void RockFields(VisualTemplate t, GameObject asset, MineRock rock)
        {
            Transform from = rock.m_areaRoot != null ? rock.m_areaRoot.transform : asset.transform;
            var areas = new List<Collider>();
            foreach (Collider c in from.GetComponentsInChildren<Collider>(true))
                if (ActiveInPrefab(c.transform, asset.transform))
                    areas.Add(c);
            var def = new RockDef { Areas = new int[areas.Count], Keys = new int[areas.Count], Health = rock.m_health };
            var meshStart = new List<int>();
            var meshToggles = new List<int>();
            for (int i = 0; i < areas.Count; i++)
            {
                if (areas[i].gameObject == asset)
                {
                    SetReal(t, ProxyReal.Structure, "MineRock area on the root");
                    return;
                }
                def.Areas[i] = GoToggle(t, areas[i].gameObject, OwnerRock);
                def.Keys[i] = ("Health" + i).GetStableHashCode();
                meshStart.Add(meshToggles.Count);
                if (rock.m_baseModel != null)
                    foreach (MeshRenderer mr in areas[i].GetComponents<MeshRenderer>())
                        meshToggles.Add(RendererToggle(mr));
            }
            meshStart.Add(meshToggles.Count);
            if (rock.m_baseModel != null)
            {
                if (rock.m_baseModel == asset)
                {
                    SetReal(t, ProxyReal.Structure, "MineRock base model on the root");
                    return;
                }
                def.Base = GoToggle(t, rock.m_baseModel, OwnerRock);
            }
            def.MeshStart = meshStart.ToArray();
            def.MeshToggles = meshToggles.ToArray();
            t.Rock = def;
        }

        // MineRock5.Awake: its hit areas are the colliders on objects active then, each with its MeshFilter and
        // MeshRenderer (the game reads both); an alive one's collider object is on, a dead one's off.
        static readonly List<Collider> s_rock5Areas = new List<Collider>();

        static void Rock5Fields(VisualTemplate t, GameObject asset, MineRock5 rock)
        {
            s_rock5Areas.Clear();
            foreach (Collider c in asset.GetComponentsInChildren<Collider>(true))
                if (ActiveInPrefab(c.transform, asset.transform))
                    s_rock5Areas.Add(c);
            if (s_rock5Areas.Count == 0)
            {
                SetReal(t, ProxyReal.Structure, "MineRock5 without hit areas");
                return;
            }
            var def = new Rock5Def { Areas = new int[s_rock5Areas.Count], Health = rock.m_health };
            for (int i = 0; i < s_rock5Areas.Count; i++)
            {
                Collider c = s_rock5Areas[i];
                MeshFilter mf = c.GetComponent<MeshFilter>();
                MeshRenderer mr = c.GetComponent<MeshRenderer>();
                if (mf == null || mr == null || mf.sharedMesh == null || c.gameObject == asset)
                {
                    SetReal(t, ProxyReal.Structure, "MineRock5 hit area without its mesh");
                    return;
                }
                def.Areas[i] = GoToggle(t, c.gameObject, OwnerRock5);
                s_forcedDisabled.Add(mr);
            }
            t.Rock5 = def;
        }

        // The LOD0 renderers after MineRock5.UpdateMesh: the extras (renderers under each hit area's children, active then)
        // and the combined renderer. Returns the LOD mask the combined parts get (LOD0 alone).
        static int Rock5Lod0(VisualTemplate t, GameObject asset, Transform root)
        {
            foreach (Renderer r in new List<Renderer>(s_lodMask.Keys))
                s_lodMask[r] &= ~1;
            for (int i = 0; i < s_rock5Areas.Count; i++)
            {
                Transform at = s_rock5Areas[i].transform;
                for (int c = 0; c < at.childCount; c++)
                    foreach (Renderer r in at.GetChild(c).GetComponentsInChildren<Renderer>(true))
                        if (ActiveInPrefab(r.transform, root))
                        {
                            s_lodMask.TryGetValue(r, out int mask);
                            s_lodMask[r] = mask | 1;
                        }
            }
            // A renderer the new LODs no longer hold is no longer the LODGroup's: always drawn, as Unity does.
            foreach (Renderer r in new List<Renderer>(s_lodMask.Keys))
                if (s_lodMask[r] == 0)
                    s_lodMask.Remove(r);
            return 1;
        }

        // The combined renderer's parts: for each hit area (shown while it is alive), each submesh into the combined
        // mesh's first submesh when its material is the first material of the hit area with the most materials, else into
        // the second; the combined renderer's slot k draws submesh min(k, 1) with that material array's k-th material, with
        // a new MeshRenderer's settings on the root's layer.
        static void Rock5Parts(VisualTemplate t, GameObject asset, Transform root, Matrix4x4 rootInv, int[] parent, int lodMask)
        {
            Material[] array = null;
            foreach (Collider c in s_rock5Areas)
            {
                Material[] m = c.GetComponent<MeshRenderer>().sharedMaterials;
                if (array == null || m.Length > array.Length)
                    array = m;
            }
            if (array == null || array.Length == 0)
                return;
            for (int k = 0; k < array.Length; k++)
                if (!Opaque(t, array[k], "MineRock5 combined"))
                    return;
            Material first = array[0];
            var combined = new TRenderer
            {
                Mesh = null, Shadows = ShadowCastingMode.On, Receive = true, StaticShadow = false, Layer = asset.layer,
                LightProbes = LightProbeUsage.BlendProbes, ReflectionProbes = ReflectionProbeUsage.BlendProbes,
                Motion = MotionVectorGenerationMode.Object, RenderingLayers = 1u, Go = 0,
            };
            for (int i = 0; i < s_rock5Areas.Count; i++)
            {
                Collider c = s_rock5Areas[i];
                Mesh mesh = c.GetComponent<MeshFilter>().sharedMesh;
                Material[] own = c.GetComponent<MeshRenderer>().sharedMaterials;
                if (own.Length < mesh.subMeshCount)
                {
                    SetReal(t, ProxyReal.Structure, "MineRock5 hit area with fewer materials than submeshes");
                    return;
                }
                int ri = s_renderers.Count;
                s_renderers.Add(new TRenderer
                {
                    Mesh = mesh, Shadows = combined.Shadows, Receive = combined.Receive, StaticShadow = combined.StaticShadow, Layer = combined.Layer,
                    LightProbes = combined.LightProbes, ReflectionProbes = combined.ReflectionProbes, Motion = combined.Motion,
                    RenderingLayers = combined.RenderingLayers, Go = 0, Combined = true,
                });
                Matrix4x4 local = rootInv * c.transform.localToWorldMatrix;
                int area = t.Rock5.Areas[i];
                for (int j = 0; j < mesh.subMeshCount; j++)
                {
                    int group = own[j] == first ? 0 : 1;
                    for (int k = 0; k < array.Length; k++)
                    {
                        if (Math.Min(k, 1) != group)
                            continue;
                        int reqStart = s_req.Count;
                        s_req.Add(area);
                        Bounds b = FarPage.WorldBounds(mesh.GetSubMesh(j).bounds, local);
                        s_parts.Add(new TPart
                        {
                            Renderer = ri, Slot = -1, Submesh = j, Material = array[k], ReqStart = reqStart, ReqCount = 1, Center = b.center, Extents = b.extents,
                        });
                        s_partLocal.Add(local);
                        s_partLod.Add(lodMask);
                        s_partGroup.Add(-1);
                    }
                }
            }
        }

        // Active in the prefab from the object up to the root (what GetComponentsInChildren without inactive ones finds).
        static bool ActiveInPrefab(Transform tr, Transform root)
        {
            for (Transform a = tr; a != null; a = a.parent)
            {
                if (!a.gameObject.activeSelf)
                    return false;
                if (a == root)
                    return true;
            }
            return true;
        }

        // ---- decoding --------------------------------------------------------------------------------------------

        // Scratch of one decode.
        static bool[] s_toggles = new bool[64];
        static Material[] s_partMats = new Material[256];
        static readonly List<ProxyMpbValue>[] s_containerValues = MakeContainerLists();
        static readonly int[] s_containerMpb = new int[32];
        static readonly List<ProxyVariantPart> s_variant = new List<ProxyVariantPart>(256);
        static readonly byte[] s_base64 = new byte[1024];
        static byte[] s_base64Big;
        static float[] s_rock5Health = new float[64];

        static List<ProxyMpbValue>[] MakeContainerLists()
        {
            var lists = new List<ProxyMpbValue>[32];
            for (int i = 0; i < lists.Length; i++)
                lists[i] = new List<ProxyMpbValue>(8);
            return lists;
        }

        // The variant (ProxyTables.Variants index) a ZDO in this state shows, as an instance made now would show it once
        // settled (its Awake, Start and first checks run); ProxyReal.None on success, else why it stays real.
        internal static ProxyReal Decode(VisualTemplate t, ZDO zdo, out int variant)
        {
            variant = -1;
            if (!t.Proxyable)
                return t.Real;
            Game game = Game.instance;
            if (game == null)
                return ProxyReal.Data;
            int toggles = t.ToggleCount;
            if (s_toggles.Length < toggles)
                s_toggles = new bool[Math.Max(toggles, s_toggles.Length * 2)];
            Array.Copy(t.ToggleDefault, s_toggles, toggles);
            int parts = t.Parts.Length;
            if (s_partMats.Length < parts)
                s_partMats = new Material[Math.Max(parts, s_partMats.Length * 2)];
            for (int p = 0; p < parts; p++)
                s_partMats[p] = t.Parts[p].Material;
            for (int c = 0; c < t.ContainerCount; c++)
                s_containerValues[c].Clear();

            ProxyReal why;
            if (t.Wear != null && (why = DecodeWear(t, zdo, game)) != ProxyReal.None)
                return why;
            if (t.Pick != null && (why = DecodePick(t, zdo)) != ProxyReal.None)
                return why;
            if (t.Rock != null)
                DecodeRock(t, zdo, game);
            if (t.Rock5 != null && (why = DecodeRock5(t, zdo, game)) != ProxyReal.None)
                return why;
            if (t.Tree)
            {
                float h = t.TreeHealth + (float)Game.m_worldLevel * t.TreeHealth * game.m_worldLevelMineHPMultiplier;
                // TreeBase.Awake: its owner destroys a tree without health.
                if (zdo.IsOwner() && zdo.GetFloat(ZDOVars.s_health, h) <= 0f)
                    return ProxyReal.Behaviour;
            }
            if (t.Chest != null)
            {
                // Container.UpdateUseVisual: an owner's instance shows it closed (m_inUse is false when made), anyone
                // else's as the ZDO says.
                bool open = !zdo.IsOwner() && zdo.GetInt(ZDOVars.s_inUse) == 1;
                if (t.Chest.Open >= 0)
                    s_toggles[t.Chest.Open] = open;
                if (t.Chest.Closed >= 0)
                    s_toggles[t.Chest.Closed] = !open;
            }
            if (t.MatVars != null && (why = DecodeMatVars(t, zdo)) != ProxyReal.None)
                return why;
            if (t.Rmvs != null && (why = DecodeRmvs(t, zdo)) != ProxyReal.None)
                return why;

            // Property blocks: one per container that holds values; a part two such containers cover gets either's block
            // by their update order: real.
            for (int c = 0; c < t.ContainerCount; c++)
                s_containerMpb[c] = s_containerValues[c].Count > 0 ? ProxyTables.InternMpb(s_containerValues[c]) : 0;

            s_variant.Clear();
            long hash = 1469598103934665603L;
            int group = -1;
            for (int p = 0; p < parts; p++)
            {
                TPart part = t.Parts[p];
                bool shown = true;
                for (int q = 0; q < part.ReqCount && shown; q++)
                    shown = s_toggles[t.Req[part.ReqStart + q]];
                if (!shown)
                    continue;
                // Several LODGroups with rules of their own: the parts shown belong to one of them at most.
                if (t.PartGroups != null)
                {
                    int g = t.PartGroups[p];
                    if (g == -2 || (g >= 0 && group >= 0 && g != group))
                        return ProxyReal.Lod;
                    if (g >= 0)
                        group = g;
                }
                Material m = s_partMats[p];
                if (m == null)
                    return ProxyReal.Material;
                if (Rejected(m))
                    return ProxyReal.Material;
                int batch = ReferenceEquals(m, part.Material) ? ProxyTables.Parts[part.Global].Batch : ProxyTables.InternBatch(Key(t.Renderers[part.Renderer], part.Submesh, m));
                int mpb = 0;
                if (part.Containers != 0)
                    for (int c = 0; c < t.ContainerCount; c++)
                    {
                        if ((part.Containers & (1 << c)) == 0 || s_containerMpb[c] == 0)
                            continue;
                        if (mpb != 0)
                            return ProxyReal.MpbOverlap;
                        mpb = s_containerMpb[c];
                    }
                s_variant.Add(new ProxyVariantPart { Part = part.Global, Batch = batch, Mpb = mpb, LodMask = ProxyTables.Parts[part.Global].LodMask });
                unchecked
                {
                    hash = (hash ^ p) * 1099511628211L;
                    hash = (hash ^ batch) * 1099511628211L;
                    hash = (hash ^ mpb) * 1099511628211L;
                }
            }
            return Intern(t, hash, t.GroupSets != null ? (group >= 0 ? t.GroupSets[group] : -1) : t.LodSet, out variant);
        }

        static ProxyReal Intern(VisualTemplate t, long hash, int lodSet, out int variant)
        {
            long h = hash;
            while (t.VariantByHash.TryGetValue(h, out int local))
            {
                int id = t.Variants[local];
                if (SameVariant(id))
                {
                    variant = id;
                    return ProxyReal.None;
                }
                h++;
            }
            variant = -1;
            if (t.Variants.Count >= MaxVariants)
                return ProxyReal.Variants;
            var data = new ProxyVariantData { Template = t.Id, PartStart = ProxyTables.VariantPartCount, PartCount = s_variant.Count, LodSet = lodSet };
            bool any = false;
            Bounds b = default;
            for (int i = 0; i < s_variant.Count; i++)
            {
                ProxyVariantPart vp = s_variant[i];
                ProxyTables.AddVariantPart(vp);
                ProxyPartData pd = ProxyTables.Parts[vp.Part];
                var pb = new Bounds(pd.Center, pd.Extents * 2f);
                if (any)
                    b.Encapsulate(pb);
                else
                {
                    b = pb;
                    any = true;
                }
            }
            data.Center = b.center;
            data.Extents = b.extents;
            variant = ProxyTables.AddVariant(data);
            t.VariantByHash.Add(h, t.Variants.Count);
            t.Variants.Add(variant);
            return ProxyReal.None;
        }

        static bool SameVariant(int id)
        {
            ProxyVariantData v = ProxyTables.Variants[id];
            if (v.PartCount != s_variant.Count)
                return false;
            for (int i = 0; i < s_variant.Count; i++)
            {
                ProxyVariantPart a = ProxyTables.VariantParts[v.PartStart + i], b = s_variant[i];
                if (a.Part != b.Part || a.Batch != b.Batch || a.Mpb != b.Mpb)
                    return false;
            }
            return true;
        }

        static void SetToggle(int id, bool on)
        {
            if (id >= 0)
                s_toggles[id] = on;
        }

        // WearNTear.Awake: health from the ZDO against the piece's health raised by the world level (read before the raise,
        // as the game does), SetHealthVisual's sequence, the snow objects off, then UpdateSnowVisual with the ZDO's snow and
        // its _SnowLevel on the root's container.
        static ProxyReal DecodeWear(VisualTemplate t, ZDO zdo, Game game)
        {
            WearDef w = t.Wear;
            float baseHealth = w.Health;
            float num = zdo.GetFloat(ZDOVars.s_health, baseHealth);
            // WearNTear.m_randomInitialDamage is set only while a location spawns its objects (ZoneSystem.SpawnLocation).
            if (num.Equals(baseHealth) && WearNTear.m_randomInitialDamage)
                return ProxyReal.RandomInit;
            float health = baseHealth;
            if (Game.m_worldLevel > 0)
                health += (float)Game.m_worldLevel * game.m_worldLevelPieceHPMultiplier * health;
            float pct = Mathf.Clamp01(num / health);
            if (w.HasStates)
            {
                if (pct > 0.75f)
                {
                    if (w.Worn != w.New)
                        SetToggle(w.Worn, false);
                    if (w.Broken != w.New)
                        SetToggle(w.Broken, false);
                    SetToggle(w.New, true);
                }
                else if (pct > 0.25f)
                {
                    if (w.New != w.Worn)
                        SetToggle(w.New, false);
                    if (w.Broken != w.Worn)
                        SetToggle(w.Broken, false);
                    SetToggle(w.Worn, true);
                }
                else
                {
                    if (w.New != w.Broken)
                        SetToggle(w.New, false);
                    if (w.Worn != w.Broken)
                        SetToggle(w.Worn, false);
                    SetToggle(w.Broken, true);
                }
            }
            SetToggle(w.Snow, false);
            SetToggle(w.SnowWorn, false);
            SetToggle(w.SnowBroken, false);
            if (w.HasSnow)
            {
                float snow = zdo.GetFloat(ZDOVars.s_snow);
                bool broken = pct <= 0.25f && w.SnowBroken >= 0;
                bool worn = pct <= 0.75f && w.SnowWorn >= 0 && !broken;
                SetToggle(w.Snow, !worn && !broken && snow > 0.25f);
                SetToggle(w.SnowWorn, worn && !broken && snow > 0.25f);
                SetToggle(w.SnowBroken, !worn && broken && snow > 0.25f);
                if (snow > 0.25f && t.RootContainer)
                    SetValue(0, s_snowLevel, ProxyMpbValue.Float, new Vector4(Mathf.Clamp01(Utils.Remap(snow, 0.25f, 1f, 0f, 1f)), 0f, 0f, 0f));
            }
            return ProxyReal.None;
        }

        static readonly int s_snowLevel = Shader.PropertyToID("_SnowLevel");

        // MaterialMan.PropertyContainer.SetValue: a value set again replaces the one before.
        static void SetValue(int container, int id, byte kind, Vector4 value)
        {
            List<ProxyMpbValue> list = s_containerValues[container];
            for (int i = 0; i < list.Count; i++)
                if (list[i].Id == id)
                {
                    list[i] = new ProxyMpbValue { Id = id, Kind = kind, Value = value };
                    return;
                }
            list.Add(new ProxyMpbValue { Id = id, Kind = kind, Value = value });
        }

        // Pickable.Awake: the picked subtree shows while not picked and enabled. One without it, never respawning and
        // picked, destroys itself (claiming it first): real, the game's to do.
        static ProxyReal DecodePick(VisualTemplate t, ZDO zdo)
        {
            PickDef p = t.Pick;
            bool picked = zdo.GetBool(ZDOVars.s_picked, p.DefaultPicked);
            bool enabled = zdo.GetBool(ZDOVars.s_enabled, true);
            if (p.Hide >= 0)
                SetToggle(p.Hide, !picked && enabled);
            else if (p.RespawnMinutes <= 0f && zdo.GetBool(ZDOVars.s_picked))
                return ProxyReal.Behaviour;
            return ProxyReal.None;
        }

        // MineRock.UpdateVisability (1-2 s after Start, then every 10 s): an area with health shows; with a base model, the
        // base model shows while no area is gone, the areas' own meshes once one is.
        static void DecodeRock(VisualTemplate t, ZDO zdo, Game game)
        {
            RockDef r = t.Rock;
            float health = r.Health + (float)Game.m_worldLevel * r.Health * game.m_worldLevelMineHPMultiplier;
            bool gone = false;
            for (int i = 0; i < r.Areas.Length; i++)
            {
                bool alive = zdo.GetFloat(r.Keys[i], health) > 0f;
                SetToggle(r.Areas[i], alive);
                gone |= !alive;
            }
            if (r.Base < 0)
                return;
            SetToggle(r.Base, !gone);
            for (int i = 0; i < r.MeshToggles.Length; i++)
                SetToggle(r.MeshToggles[i], gone);
        }

        // MineRock5.LoadHealth: the health package (base64 of a ZPackage: count, then each area's float); an area not in
        // it keeps its health (raised by the world level). UpdateMesh: an alive area's collider object on, a dead one's off.
        static ProxyReal DecodeRock5(VisualTemplate t, ZDO zdo, Game game)
        {
            Rock5Def r = t.Rock5;
            int areas = r.Areas.Length;
            if (s_rock5Health.Length < areas)
                s_rock5Health = new float[Math.Max(areas, s_rock5Health.Length * 2)];
            float health = r.Health + (float)Game.m_worldLevel * r.Health * game.m_worldLevelMineHPMultiplier;
            for (int i = 0; i < areas; i++)
                s_rock5Health[i] = health;
            string s = zdo.GetString(ZDOVars.s_health);
            if (s.Length > 0)
            {
                int need = (s.Length / 4 + 1) * 3;
                byte[] buffer = need <= s_base64.Length ? s_base64 : s_base64Big != null && s_base64Big.Length >= need ? s_base64Big : (s_base64Big = new byte[need * 2]);
                if (!Convert.TryFromBase64String(s, new Span<byte>(buffer), out int bytes) || bytes < 4)
                    return ProxyReal.Data;
                int count = BitConverter.ToInt32(buffer, 0);
                if (count < 0 || 4 + (long)count * 4 > bytes)
                    return ProxyReal.Data;
                for (int i = 0; i < count; i++)
                    if (i < areas)
                        s_rock5Health[i] = BitConverter.ToSingle(buffer, 4 + i * 4);
            }
            for (int i = 0; i < areas; i++)
                SetToggle(r.Areas[i], s_rock5Health[i] > 0f);
            return ProxyReal.None;
        }

        // MaterialVariation.CheckMaterial: "MatVar{index}" picks the slot's material; unset, nobody's instance changes it but
        // the owner's, which rolls one.
        static ProxyReal DecodeMatVars(VisualTemplate t, ZDO zdo)
        {
            foreach (MatVarDef mv in t.MatVars)
            {
                int v = zdo.GetInt(mv.Key, -1);
                if (v < 0)
                {
                    if (zdo.IsOwner())
                        return ProxyReal.RandomInit;
                    continue;
                }
                if (v >= mv.Options.Length)
                    return ProxyReal.Structure;
                Material m = mv.Options[v];
                for (int p = 0; p < t.Parts.Length; p++)
                    if (t.Parts[p].Renderer == mv.Renderer && t.Parts[p].Slot == mv.Slot)
                        s_partMats[p] = m;
            }
            return ProxyReal.None;
        }

        // RandomMaterialValues.CheckMaterial: with a seed in the ZDO, each vector property i gets GetValue(seed + i) on
        // the component's container; without one, nobody's instance sets any but the owner's, which rolls a seed.
        static ProxyReal DecodeRmvs(VisualTemplate t, ZDO zdo)
        {
            int seed = zdo.GetInt(s_matSeedKey, -1);
            if (seed < 0)
                return zdo.IsOwner() ? ProxyReal.RandomInit : ProxyReal.None;
            foreach (RmvDef r in t.Rmvs)
            {
                if (!r.BySeed.TryGetValue(seed, out ProxyMpbValue[] values))
                {
                    var made = new List<ProxyMpbValue>();
                    List<RandomMaterialValues.VectorVariationProperty> props = r.Component.m_vectorProperties;
                    for (int i = 0; i < props.Count; i++)
                    {
                        Vector4 value = props[i].GetValue(seed + i);
                        foreach (string name in props[i].m_propertyNames)
                            made.Add(new ProxyMpbValue { Id = Shader.PropertyToID(name), Kind = ProxyMpbValue.Vector, Value = value });
                    }
                    values = made.ToArray();
                    if (r.BySeed.Count < 4096)
                        r.BySeed[seed] = values;
                }
                foreach (ProxyMpbValue v in values)
                    SetValue(r.Container, v.Id, v.Kind, v.Value);
            }
            return ProxyReal.None;
        }

        // The prediction before a decode: whether a ZDO of this prefab may be proxied at all (its template is proxyable).
        internal static bool MayProxy(VisualTemplate t) => t != null && t.Proxyable;

        // The instance's root as ZNetScene.CreateObject and ZNetView.Awake make it: the ZDO's position and rotation, the
        // prefab root's scale or (m_syncInitialScale) the ZDO's.
        internal static Matrix4x4 RootOf(VisualTemplate t, ZDO zdo)
        {
            Vector3 scale = t.RootScale;
            if (t.SyncScale)
            {
                Vector3 vec = zdo.GetVec3(ZDOVars.s_scaleHash, Vector3.zero);
                if (vec != Vector3.zero)
                    scale = vec;
                else
                {
                    float f = zdo.GetFloat(ZDOVars.s_scaleScalarHash, scale.x);
                    if (!scale.x.Equals(f))
                        scale = new Vector3(f, f, f);
                }
            }
            return Matrix4x4.TRS(zdo.GetPosition(), zdo.GetRotation(), scale);
        }

        // Lab: one line per template class and the most common unreviewed components.
        internal static string Describe()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"templates {s_byPrefab.Count} (proxyable {LabProxyable}, queued {s_requests.Count}, slowest make {LabMakeMsMax:0.00} ms), batches {ProxyTables.BatchCount - 1}, " +
                      $"blocks {ProxyTables.MpbCount - 1}, variants {ProxyTables.VariantCount}, rejected materials {s_rejected.Count}; real templates:");
            for (int i = 1; i < (int)ProxyReal.Count; i++)
                if (LabRealTemplates[i] > 0)
                    sb.Append(' ').Append((ProxyReal)i).Append(' ').Append(LabRealTemplates[i]);
            if (LabUnknownComponents.Count > 0)
            {
                sb.Append("; unreviewed components:");
                int shown = 0;
                foreach (KeyValuePair<string, int> kv in LabUnknownComponents)
                {
                    if (shown++ >= 12)
                        break;
                    sb.Append(' ').Append(kv.Key).Append(" x").Append(kv.Value);
                }
            }
            if (LabRealExamples.Count > 0)
                sb.Append("; kept real: ").Append(string.Join("; ", LabRealExamples));
            return sb.ToString();
        }
    }
}
