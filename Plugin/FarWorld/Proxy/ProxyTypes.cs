using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace RealPortal
{
    // FarWorld proxies (Far.Proxies; ARCHITECTURE-v2.md §3.5-§3.7, §3.12; FARWORLD-V3.md §3.1-§3.3, §7 V3): the data the
    // drawing part reads. One ProxyInst per far object that is drawn instead of instantiated, in per-sector spans of
    // ProxyStore; what it looks like comes from its template (ProxyTables.Templates / Parts, one per prefab) and its
    // variant (ProxyTables.Variants / VariantParts: which parts its ZDO state shows, with which material and property block).
    // Everything here is main-thread data; the NativeArrays may be read by jobs between a schedule and its Complete within
    // one render callback, never written then.
    //
    // Drawing one area into one camera (the recipe the data is shaped for; ARCHITECTURE-v2.md §3.7, FARWORLD-V3.md §3.2 b):
    //  - spans: FarProxies.CollectSpans(farPortal or AreaSectors, list), only while FarProxies.Drawing; a DistantOnly span
    //    contributes only instances with ProxyFlags.Distant; Bounds culls the span as a whole;
    //  - per instance: skip unless Drawn (Created: its real object is there; InActive: real; Hidden: nothing to draw); the
    //    sphere (Center, Radius) against the view (and the sun-extruded caster volume for ShadowsOnly casters);
    //  - LOD (LODGroup's rule): with ProxyTables.Templates[inst.Template].LodCount > 0, relative height =
    //    inst.LodSize / (|C' - inst.LodRef| * 2 tan(gameFov / 2)) * QualitySettings.lodBias; the level is the first i (from
    //    QualitySettings.maximumLODLevel) with relative height >= Height(i), none below the last (culled); the cross-fade
    //    band from FadeWidth(i) / FadeMode / AnimateCrossFading; LodCount 0: every part;
    //  - parts: ProxyTables.Variants[inst.Variant] gives VariantParts [PartStart, PartStart + PartCount); a part draws at
    //    the chosen level when its LodMask is LodAll or has that level's bit; its matrix is inst.Root *
    //    ProxyTables.Parts[vp.Part].Local, its draw key ProxyTables.Batch(vp.Batch), its block ProxyTables.Mpb(vp.Mpb)
    //    (null: none). Runs split by (batch, block).
    //  - FarProxies.Lab* counters: the drawing part adds its draw calls, instances and cull time.

    // Why an object stays real (FarProxies.LabRealRows counts the registry's rows by it, LabRealTemplates the templates).
    internal enum ProxyReal : byte
    {
        // Drawn as a proxy (or not known yet).
        None,
        // No prefab for its hash, or none in this game.
        NoPrefab,
        // Not persistent (the game destroys it with its instance).
        NotPersistent,
        // A portal (its frame and opening are the window pipeline's).
        Portal,
        // A dungeon interior (far above the ground: Far.DeferInteriors leaves them to the game).
        Interior,
        // Inside a far area's active area (the place a player standing at its far portal would own and simulate).
        ActiveArea,
        // A component nobody reviewed (other mods' too), or logic the game needs as an instance.
        Component,
        // Creatures, fish, birds, items.
        Life,
        // Particles, lights, audio, reflection probes.
        Fx,
        // Animator, Animation, skinned meshes, cloth.
        Animated,
        // Rigidbody, ZSyncTransform, floating.
        Physics,
        // A renderer that cannot be drawn as an instance exactly (line, trail, billboard, sprite, extra vertex streams,
        // probe anchors and proxy volumes).
        Renderer,
        // A transparent, missing or rejected material (FarProxies.RejectMaterial).
        Material,
        // More than one LODGroup, or one that something switches.
        Lod,
        // A component setup the game itself trips over or that only its runtime resolves (partial WearNTear states, a
        // MineRock5 without its LODGroup, two decoders on one object, decoders off the root).
        Structure,
        // Its instance would roll a random state at once and write it to its ZDO (random initial damage, a material
        // variation or random material seed this session would pick as owner).
        RandomInit,
        // Its instance would change the world at once (destroy itself, keep a timer, draw a connection).
        Behaviour,
        // Two MaterialMan containers would set blocks on one renderer: which one wins depends on their update order.
        MpbOverlap,
        // More distinct states than a template keeps (ProxyTemplates.MaxVariants).
        Variants,
        // Its ZDO data could not be read (a bad MineRock5 health package).
        Data,
        Count,
    }

    // A proxy's state bits (ProxyInst.Flags).
    [Flags]
    internal enum ProxyFlags : uint
    {
        None = 0,
        // Its ZDO has a live instance (ZNetScene.AddInstance): the real object shows, the proxy is never drawn (§3.12).
        Created = 1,
        // Inside a far area's active area: it stays real (RemoteAreas creates it), never drawn.
        InActive = 2,
        // Its state shows no part (a picked pickable without its picked subtree, every hit area of a rock gone).
        Hidden = 4,
        // Its ZDO is Distant: a sector an area reaches as Ring draws only these (SectorRole.Ring).
        Distant = 8,
        // Any of these: not drawn.
        NotDrawn = Created | InActive | Hidden,
    }

    // One proxy (112 B, in ProxyStore's NativeArray pages). Root is the instance's transform as ZNetScene.CreateObject
    // would make it (the ZDO's position and rotation, the prefab root's scale or the synced one); a part's world matrix
    // is Root * ProxyPartData.Local. Center/Radius bound every part of its variant over all LODs; LodRef/LodSize are its
    // LODGroup's reference point and size in world space (LodSize 0: no LODGroup, every part always drawn).
    internal struct ProxyInst
    {
        internal Matrix4x4 Root;
        internal Vector3 Center;
        internal float Radius;
        internal Vector3 LodRef;
        internal float LodSize;
        // ProxyTables.Templates index and ProxyTables.Variants index (global).
        internal int Template;
        internal int Variant;
        // ProxyTables.LodSets index of the LOD rule it is drawn by (its variant's), -1: none (every part always drawn).
        internal int LodSet;
        // ProxyFlags.
        internal uint Flags;
        // Its row in the SectorRegistry (the ZDO is SectorRegistry.RowZdo(Row)).
        internal int Row;

        internal bool Drawn => (Flags & (uint)ProxyFlags.NotDrawn) == 0;
    }

    // What one area's render culls in one sector (FarProxies.CollectSpans): instances [Start, Start + Count) of Page.
    // DistantOnly: the area reaches the sector as Ring, so only instances with ProxyFlags.Distant are its (the game lists
    // only Distant objects of its distant ring). Bounds is conservative (every instance's sphere since its last rebuild).
    // Valid until control returns to the game (the next journal drain or FarProxies.Update may move or change it).
    internal struct ProxySpanRef
    {
        internal NativeArray<ProxyInst> Page;
        internal int Start, Count;
        internal uint Sector;
        internal SectorRole Role;
        internal bool DistantOnly;
        internal Bounds Bounds;
    }

    // One draw key: everything a part's renderer decides about its draw but the matrix and the property block. Interned
    // (ProxyTables.Batch(id)); id 0 is never used. RenderParams from it: layer, shadowCastingMode (ShadowsOnly for a
    // caster outside the view), receiveShadows, lightProbeUsage, reflectionProbeUsage, motionVectorMode,
    // renderingLayerMask; the mesh's submesh and the material as they are (the game's own shared assets).
    internal sealed class ProxyBatch
    {
        internal int Id;
        internal Mesh Mesh;
        internal int Submesh;
        internal Material Material;
        internal ShadowCastingMode Shadows;
        internal bool ReceiveShadows;
        internal int Layer;
        internal LightProbeUsage LightProbes;
        internal ReflectionProbeUsage ReflectionProbes;
        internal MotionVectorGenerationMode MotionVectors;
        internal uint RenderingLayerMask;
        internal bool StaticShadowCaster;

        public override string ToString() =>
            $"{((object)Mesh != null ? Mesh.name : "-")}[{Submesh}] {((object)Material != null ? Material.name : "-")} layer {Layer} {Shadows}";
    }

    internal readonly struct ProxyBatchKey : IEquatable<ProxyBatchKey>
    {
        internal readonly Mesh Mesh;
        internal readonly Material Material;
        internal readonly int Submesh, Layer;
        internal readonly ShadowCastingMode Shadows;
        internal readonly bool Receive, StaticShadow;
        internal readonly LightProbeUsage LightProbes;
        internal readonly ReflectionProbeUsage ReflectionProbes;
        internal readonly MotionVectorGenerationMode Motion;
        internal readonly uint RenderingLayers;
        readonly int _hash;

        internal ProxyBatchKey(Mesh mesh, int submesh, Material material, ShadowCastingMode shadows, bool receive, int layer, LightProbeUsage lightProbes,
                               ReflectionProbeUsage reflectionProbes, MotionVectorGenerationMode motion, uint renderingLayers, bool staticShadow)
        {
            Mesh = mesh;
            Submesh = submesh;
            Material = material;
            Shadows = shadows;
            Receive = receive;
            Layer = layer;
            LightProbes = lightProbes;
            ReflectionProbes = reflectionProbes;
            Motion = motion;
            RenderingLayers = renderingLayers;
            StaticShadow = staticShadow;
            unchecked
            {
                int h = (object)mesh != null ? mesh.GetInstanceID() : 0;
                h = h * 31 + ((object)material != null ? material.GetInstanceID() : 0);
                h = h * 31 + submesh;
                h = h * 31 + layer;
                h = h * 31 + (int)shadows;
                h = h * 31 + (receive ? 1 : 0) + (staticShadow ? 2 : 0);
                h = h * 31 + (int)lightProbes;
                h = h * 31 + (int)reflectionProbes;
                h = h * 31 + (int)motion;
                _hash = h * 31 + (int)renderingLayers;
            }
        }

        public bool Equals(ProxyBatchKey o) =>
            ReferenceEquals(Mesh, o.Mesh) && ReferenceEquals(Material, o.Material) && Submesh == o.Submesh && Layer == o.Layer && Shadows == o.Shadows &&
            Receive == o.Receive && StaticShadow == o.StaticShadow && LightProbes == o.LightProbes && ReflectionProbes == o.ReflectionProbes &&
            Motion == o.Motion && RenderingLayers == o.RenderingLayers;

        public override bool Equals(object o) => o is ProxyBatchKey k && Equals(k);
        public override int GetHashCode() => _hash;
    }

    // One value MaterialMan would set on a renderer's block (MaterialMan.SetValue<T>: int, float or Vector4).
    internal struct ProxyMpbValue : IEquatable<ProxyMpbValue>
    {
        internal const byte Float = 0, Vector = 1, Int = 2;
        internal int Id;
        internal byte Kind;
        internal Vector4 Value;

        public bool Equals(ProxyMpbValue o) => Id == o.Id && Kind == o.Kind && Value.Equals(o.Value);
    }

    // A property block as MaterialMan leaves it on a renderer (PropertyContainer.UpdateBlock): exactly the values set on
    // the container covering it, by the setter the game used. Interned (ProxyTables.Mpb(id)); id 0 is no block at all.
    internal sealed class ProxyMpb
    {
        internal int Id;
        internal ProxyMpbValue[] Values;
        internal MaterialPropertyBlock Block;
    }

    // A part of a template: one material slot of one renderer (slot k draws submesh min(k, subMeshCount - 1), as Unity
    // does). Local: the renderer's transform relative to the prefab root; Center/Extents: its submesh bounds in root space.
    // LodMask: bit i set when LOD level i of the template's LODGroup draws it; LodAll when the LODGroup does not hold it
    // (always drawn, as Unity does). Batch: with the prefab's own material (a variant may pick another).
    internal struct ProxyPartData
    {
        internal const int LodAll = 0xFF;
        internal Matrix4x4 Local;
        internal Vector3 Center;
        internal Vector3 Extents;
        internal int Batch;
        internal int LodMask;
        internal int Template;
    }

    // A visible part of a variant: the part (ProxyTables.Parts index), its batch (material chosen) and property block
    // (ProxyTables.Mpb; 0 none), and its LodMask again (so a cull reads one array).
    internal struct ProxyVariantPart
    {
        internal int Part;
        internal int Batch;
        internal int Mpb;
        internal int LodMask;
        // ProxyTables run id of its (Batch, Mpb), set by AddVariantPart: what ProxyDraw's emission sorts parts by.
        internal int Run;
    }

    // One state of a template: VariantParts [PartStart, PartStart + PartCount); Center/Extents bound them in root space
    // (all LODs). PartCount 0: hidden.
    internal struct ProxyVariantData
    {
        internal int Template;
        internal int PartStart, PartCount;
        // ProxyTables.LodSets index: the template's one LOD rule, or (a piece whose state subtrees carry LODGroups with
        // rules of their own) the rule of the one LODGroup whose parts this variant shows; -1: none.
        internal int LodSet;
        internal Vector3 Center;
        internal Vector3 Extents;
    }

    // A template's LOD description as Unity's LODGroup selects (its own fields; the camera's FOV, lodBias and
    // QualitySettings.maximumLODLevel are the drawing part's). LodCount 0: no LODGroup. LodLocal: the LODGroup's
    // transform relative to the prefab root (LodRef and LodSize of an instance are already in world space). Heights and
    // fade widths per level in A (levels 0-3) and B (4-7). FadeMode: LODFadeMode as int.
    internal struct ProxyTemplateData
    {
        internal int Prefab;
        internal int PartStart, PartCount;
        internal int LodCount;
        internal int FadeMode;
        internal bool AnimateCrossFading;
        internal bool LodFadeInOut;
        internal float LodSize;
        internal Vector3 LodLocalRef;
        internal Matrix4x4 LodLocal;
        internal Vector4 HeightsA, HeightsB, FadeA, FadeB;

        internal float Height(int level) => level < 4 ? HeightsA[level] : HeightsB[level - 4];
        internal float FadeWidth(int level) => level < 4 ? FadeA[level] : FadeB[level - 4];
    }

    // The flat tables of every template, variant, batch and property block (FarWorld's whole session of a scene). Grown
    // in place as templates and variants are made (never shrunk until Clear): an index, once handed out, stays valid while
    // Version is the same; a grow moves the NativeArrays (Version moves), so jobs take them per render.
    internal static class ProxyTables
    {
        internal static NativeArray<ProxyTemplateData> Templates;
        // LOD rules (only their LOD fields and LodFadeInOut are read), one per template LODGroup rule.
        internal static NativeArray<ProxyTemplateData> LodSets;
        internal static int LodSetCount;
        internal static NativeArray<ProxyPartData> Parts;
        internal static NativeArray<ProxyVariantData> Variants;
        internal static NativeArray<ProxyVariantPart> VariantParts;
        internal static int TemplateCount, PartCount, VariantCount, VariantPartCount;
        // Moves whenever an array above was reallocated or the tables were cleared.
        internal static int Version;

        static readonly List<ProxyBatch> s_batches = new List<ProxyBatch> { null };
        static readonly Dictionary<ProxyBatchKey, int> s_batchIds = new Dictionary<ProxyBatchKey, int>();
        static readonly List<ProxyMpb> s_mpbs = new List<ProxyMpb> { null };
        static readonly Dictionary<long, int> s_mpbIds = new Dictionary<long, int>();

        internal static int BatchCount => s_batches.Count;
        internal static int MpbCount => s_mpbs.Count;

        // Runs: one per distinct (batch, block) of the variant parts (ProxyVariantPart.Run), dense from 0. BatchDraw per
        // batch id: its layer | shadow mode << 8 (-1: id 0), what the emission job reads of a batch.
        static readonly List<int> s_runBatch = new List<int>(), s_runMpb = new List<int>();
        static readonly Dictionary<long, int> s_runIds = new Dictionary<long, int>();
        internal static NativeArray<int> BatchDraw;
        internal static int RunCount => s_runBatch.Count;
        internal static int RunBatch(int run) => s_runBatch[run];
        internal static int RunMpb(int run) => s_runMpb[run];

        internal static ProxyBatch Batch(int id) => id > 0 && id < s_batches.Count ? s_batches[id] : null;

        // The block for a part (null for id 0: draw without one).
        internal static MaterialPropertyBlock Mpb(int id) => id > 0 && id < s_mpbs.Count ? s_mpbs[id].Block : null;
        internal static ProxyMpb MpbOf(int id) => id > 0 && id < s_mpbs.Count ? s_mpbs[id] : null;

        internal static int InternBatch(in ProxyBatchKey key)
        {
            if (s_batchIds.TryGetValue(key, out int id))
                return id;
            id = s_batches.Count;
            s_batches.Add(new ProxyBatch
            {
                Id = id, Mesh = key.Mesh, Submesh = key.Submesh, Material = key.Material, Shadows = key.Shadows, ReceiveShadows = key.Receive,
                Layer = key.Layer, LightProbes = key.LightProbes, ReflectionProbes = key.ReflectionProbes, MotionVectors = key.Motion,
                RenderingLayerMask = key.RenderingLayers, StaticShadowCaster = key.StaticShadow,
            });
            s_batchIds.Add(key, id);
            Ensure(ref BatchDraw, id + 1);
            BatchDraw[0] = -1;
            BatchDraw[id] = (key.Layer & 0xFF) | ((int)key.Shadows << 8);
            return id;
        }

        // The block holding exactly values[0..count) (sorted by id first); 0 for none. Allocates only for a new one.
        internal static int InternMpb(List<ProxyMpbValue> values)
        {
            int count = values.Count;
            if (count == 0)
                return 0;
            // Insertion sort by id (a handful of values), so one set gives one block whatever order it was set in.
            for (int i = 1; i < count; i++)
            {
                ProxyMpbValue v = values[i];
                int j = i - 1;
                while (j >= 0 && values[j].Id > v.Id)
                {
                    values[j + 1] = values[j];
                    j--;
                }
                values[j + 1] = v;
            }
            long h = 1469598103934665603L;
            unchecked
            {
                for (int i = 0; i < count; i++)
                {
                    ProxyMpbValue v = values[i];
                    h = (h ^ v.Id) * 1099511628211L;
                    h = (h ^ v.Kind) * 1099511628211L;
                    h = (h ^ v.Value.GetHashCode()) * 1099511628211L;
                }
            }
            while (s_mpbIds.TryGetValue(h, out int id))
            {
                ProxyMpb m = s_mpbs[id];
                if (Same(m.Values, values))
                    return id;
                h++;
            }
            var made = new ProxyMpb { Id = s_mpbs.Count, Values = values.ToArray(), Block = new MaterialPropertyBlock() };
            foreach (ProxyMpbValue v in made.Values)
            {
                if (v.Kind == ProxyMpbValue.Int)
                    made.Block.SetInt(v.Id, (int)v.Value.x);
                else if (v.Kind == ProxyMpbValue.Float)
                    made.Block.SetFloat(v.Id, v.Value.x);
                else
                    made.Block.SetVector(v.Id, v.Value);
            }
            s_mpbs.Add(made);
            s_mpbIds.Add(h, made.Id);
            return made.Id;
        }

        static bool Same(ProxyMpbValue[] a, List<ProxyMpbValue> b)
        {
            if (a.Length != b.Count)
                return false;
            for (int i = 0; i < a.Length; i++)
                if (!a[i].Equals(b[i]))
                    return false;
            return true;
        }

        internal static int AddLodSet(in ProxyTemplateData t)
        {
            Ensure(ref LodSets, LodSetCount + 1);
            LodSets[LodSetCount] = t;
            return LodSetCount++;
        }

        internal static int AddTemplate(in ProxyTemplateData t)
        {
            // The cull job takes LodSets whether any rule exists yet or not: it exists with the first template.
            Ensure(ref LodSets, Math.Max(LodSetCount, 1));
            Ensure(ref Templates, TemplateCount + 1);
            Templates[TemplateCount] = t;
            return TemplateCount++;
        }

        internal static void SetTemplate(int i, in ProxyTemplateData t) => Templates[i] = t;

        internal static int AddPart(in ProxyPartData p)
        {
            Ensure(ref Parts, PartCount + 1);
            Parts[PartCount] = p;
            return PartCount++;
        }

        internal static int AddVariant(in ProxyVariantData v)
        {
            Ensure(ref Variants, VariantCount + 1);
            Variants[VariantCount] = v;
            return VariantCount++;
        }

        internal static int AddVariantPart(ProxyVariantPart p)
        {
            long key = ((long)p.Batch << 32) | (uint)p.Mpb;
            if (!s_runIds.TryGetValue(key, out int run))
            {
                run = s_runBatch.Count;
                s_runBatch.Add(p.Batch);
                s_runMpb.Add(p.Mpb);
                s_runIds.Add(key, run);
            }
            p.Run = run;
            Ensure(ref VariantParts, VariantPartCount + 1);
            VariantParts[VariantPartCount] = p;
            return VariantPartCount++;
        }

        static void Ensure<T>(ref NativeArray<T> a, int need) where T : struct
        {
            int cap = a.IsCreated ? a.Length : 0;
            if (need <= cap)
                return;
            int size = Math.Max(256, cap * 2);
            while (size < need)
                size *= 2;
            var grown = new NativeArray<T>(size, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            if (a.IsCreated)
            {
                NativeArray<T>.Copy(a, grown, cap);
                a.Dispose();
            }
            a = grown;
            Version++;
        }

        // Every template, variant, batch and block goes (a new scene: the prefabs may not be the same objects).
        internal static void Clear()
        {
            if (Templates.IsCreated)
                Templates.Dispose();
            if (LodSets.IsCreated)
                LodSets.Dispose();
            LodSetCount = 0;
            if (Parts.IsCreated)
                Parts.Dispose();
            if (Variants.IsCreated)
                Variants.Dispose();
            if (VariantParts.IsCreated)
                VariantParts.Dispose();
            TemplateCount = PartCount = VariantCount = VariantPartCount = 0;
            if (BatchDraw.IsCreated)
                BatchDraw.Dispose();
            s_runBatch.Clear();
            s_runMpb.Clear();
            s_runIds.Clear();
            s_batches.Clear();
            s_batches.Add(null);
            s_batchIds.Clear();
            s_mpbs.Clear();
            s_mpbs.Add(null);
            s_mpbIds.Clear();
            Version++;
        }

        // What the tables hold outside the managed heap (bytes).
        internal static long NativeBytes =>
            (Templates.IsCreated ? (long)Templates.Length * 160 : 0) + (LodSets.IsCreated ? (long)LodSets.Length * 160 : 0) + (Parts.IsCreated ? (long)Parts.Length * 96 : 0) +
            (Variants.IsCreated ? (long)Variants.Length * 36 : 0) + (VariantParts.IsCreated ? (long)VariantParts.Length * 20 : 0) +
            (BatchDraw.IsCreated ? (long)BatchDraw.Length * 4 : 0);
    }
}
