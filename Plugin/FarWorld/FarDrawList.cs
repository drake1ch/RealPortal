using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace RealPortal
{
    // FarDrawList (Docs\FARWORLD-V3.md §3.2 a, §3.3): one renderer-neutral description of the far content to draw, per
    // AreaContext. The same runs are to feed the hero slot (Graphics.RenderMeshInstanced with RenderParams.camera set to
    // the slot), virtual views (CommandBuffer.DrawMeshInstanced with the material's DEFERRED pass), FarShadowCache (its
    // ShadowCaster pass) and the sky-occlusion and probe captures. V2 has the types only: nothing fills, culls or draws
    // them yet (V3: ProxyStore and the decoders fill the pages from journal events, WindowCullBatchJob culls the chunks).
    //
    //  - Run: {mesh, submesh, material, pass index (DEFERRED or ShadowCaster), LOD, shadow mode, MPB bucket, the
    //    instances' matrices as a NativeSlice, AABB}. Runs are cut from pages; nothing is copied to make them.
    //  - Pages: persistent per-AreaContext instance pages keyed by (material, mesh, submesh, LOD, pass, shadow mode, MPB
    //    bucket), changed in place as the journal reports an instance added, moved or gone (Add, Set, RemoveAt), never
    //    rebuilt every frame. Each instance keeps its owner's row beside its matrix, so a swap-remove can tell the owner
    //    of the instance that moved into the hole.
    //  - Chunks: a page is cut into runs of its material's real instance cap (P44 explains P5: the Beech LOD3
    //    Custom/Vegetation card drew 56 of 511 cells, exactly 7 of 64 columns), at most 1023 (what one instanced draw
    //    takes). Each chunk keeps a conservative AABB.
    //  - Per-object material state (_SnowLevel, RandomMaterialValues' seed, _TakingAshlandsDamage) is the MPB bucket of
    //    the key: one run per distinct value, which is exact, until P43 shows instanced property arrays work.
    //    MaterialVariation picks another material, so it is the key's material.
    //  - Grass: spans of FarGrass placement (UnityXorshift, P12), thinned against C'.
    //  - Real renderers (live-core objects, RealOnly ones within RealOnlyRadius): listed by reference for DrawRenderer
    //    (P35) or the hero slot.
    // Main thread only (the NativeArrays may be read by jobs between a schedule and its Complete, never written then).

    // Which of the material's passes a run draws with.
    internal enum FarPass : byte
    {
        Deferred = 0,
        ShadowCaster = 1,
    }

    internal static class FarDrawLimits
    {
        // What one DrawMeshInstanced / RenderMeshInstanced takes.
        internal const int HardMaxInstances = 1023;
        // A page's first capacity; it doubles from there.
        internal const int MinPageCapacity = 16;

        // The pass names the game's materials carry for each FarPass (Material.FindPass).
        internal static string PassName(FarPass pass) => pass == FarPass.ShadowCaster ? "ShadowCaster" : "DEFERRED";
    }

    // The key of a page and of the runs cut from it. Mesh and material are compared by reference (the game's shared
    // assets); the hash is taken once, from their instance ids.
    internal readonly struct FarRunKey : IEquatable<FarRunKey>
    {
        internal readonly Mesh Mesh;
        internal readonly Material Material;
        internal readonly int Submesh;
        internal readonly byte Lod;
        internal readonly FarPass Pass;
        internal readonly ShadowCastingMode Shadow;
        // FarMpbBuckets: 0 is the material's own values.
        internal readonly int MpbBucket;
        readonly int _hash;

        internal FarRunKey(Mesh mesh, int submesh, Material material, FarPass pass, byte lod, ShadowCastingMode shadow, int mpbBucket)
        {
            Mesh = mesh;
            Material = material;
            Submesh = submesh;
            Lod = lod;
            Pass = pass;
            Shadow = shadow;
            MpbBucket = mpbBucket;
            unchecked
            {
                int h = (object)mesh != null ? mesh.GetInstanceID() : 0;
                h = h * 31 + ((object)material != null ? material.GetInstanceID() : 0);
                h = h * 31 + submesh;
                h = h * 31 + lod;
                h = h * 31 + (int)pass;
                h = h * 31 + (int)shadow;
                _hash = h * 31 + mpbBucket;
            }
        }

        public bool Equals(FarRunKey o) =>
            ReferenceEquals(Mesh, o.Mesh) && ReferenceEquals(Material, o.Material) && Submesh == o.Submesh && Lod == o.Lod &&
            Pass == o.Pass && Shadow == o.Shadow && MpbBucket == o.MpbBucket;

        public override bool Equals(object o) => o is FarRunKey k && Equals(k);

        public override int GetHashCode() => _hash;

        public override string ToString() =>
            $"{((object)Mesh != null ? Mesh.name : "-")}[{Submesh}] {((object)Material != null ? Material.name : "-")} {Pass} lod {Lod} {Shadow} mpb {MpbBucket}";
    }

    // One draw: a chunk of a page. Valid until its page changes (FarPage.Version).
    internal struct FarRun
    {
        internal Mesh Mesh;
        internal int Submesh;
        internal Material Material;
        // The material's pass for Pass (FarPage.PassIndex), -1 when it has none (not drawn).
        internal int PassIndex;
        internal FarPass Pass;
        internal byte Lod;
        internal ShadowCastingMode Shadow;
        internal int MpbBucket;
        internal NativeSlice<Matrix4x4> Matrices;
        // World-space, conservative (what was added since the last FarPage.RecomputeBounds is in it; what left may be).
        internal Bounds Bounds;
        // The page's version this run was cut at.
        internal int Version;

        internal int Count => Matrices.Length;
    }

    // The instances of one key in one AreaContext: matrices in a persistent NativeArray (for the NativeArray and
    // NativeSlice overloads, without a copy), the owner's row beside each, and a conservative AABB per chunk.
    internal sealed class FarPage : IDisposable
    {
        internal readonly FarRunKey Key;
        // The material's pass for Key.Pass (Material.FindPass), resolved once; -1 when it has none.
        internal readonly int PassIndex;
        // The instances per chunk: the material's real instance cap (P44), 1..HardMaxInstances.
        internal readonly int ChunkSize;
        // Moves on every change: runs cut before it are void.
        internal int Version;

        NativeArray<Matrix4x4> _matrices;
        int[] _owners;
        Bounds[] _chunkBounds;
        bool[] _chunkHasBounds;
        int _count;
        readonly Bounds _local;

        internal FarPage(in FarRunKey key, int chunkSize = FarDrawLimits.HardMaxInstances)
        {
            Key = key;
            ChunkSize = Mathf.Clamp(chunkSize, 1, FarDrawLimits.HardMaxInstances);
            PassIndex = (object)key.Material != null && key.Material != null ? key.Material.FindPass(FarDrawLimits.PassName(key.Pass)) : -1;
            Mesh mesh = key.Mesh;
            _local = (object)mesh != null && mesh != null
                ? (key.Submesh >= 0 && key.Submesh < mesh.subMeshCount ? mesh.GetSubMesh(key.Submesh).bounds : mesh.bounds)
                : new Bounds(Vector3.zero, Vector3.zero);
            _owners = Array.Empty<int>();
            _chunkBounds = Array.Empty<Bounds>();
            _chunkHasBounds = Array.Empty<bool>();
        }

        internal int Count => _count;
        internal int Capacity => _owners.Length;
        internal int ChunkCount => (_count + ChunkSize - 1) / ChunkSize;
        // What this page holds outside the managed heap (the matrices), and in it (owners and bounds), in bytes.
        internal long NativeBytes => _matrices.IsCreated ? (long)_matrices.Length * 64 : 0;
        internal long ManagedBytes => (long)_owners.Length * 4 + (long)_chunkBounds.Length * 25;

        internal Matrix4x4 Matrix(int index) => _matrices[index];
        internal int Owner(int index) => _owners[index];

        // Appends an instance; returns its index (the owner keeps it for Set and RemoveAt, and is told when it moves).
        internal int Add(in Matrix4x4 m, int owner)
        {
            if (_count == _owners.Length)
                Grow();
            int i = _count++;
            _matrices[i] = m;
            _owners[i] = owner;
            Encapsulate(i / ChunkSize, m);
            Version++;
            return i;
        }

        internal void Set(int index, in Matrix4x4 m)
        {
            if ((uint)index >= (uint)_count)
                throw new ArgumentOutOfRangeException(nameof(index));
            _matrices[index] = m;
            Encapsulate(index / ChunkSize, m);
            Version++;
        }

        // Swap-remove: the last instance moves into index. Returns the owner of the one that moved (to be told its new
        // index), -1 when none did (index was the last).
        internal int RemoveAt(int index)
        {
            if ((uint)index >= (uint)_count)
                throw new ArgumentOutOfRangeException(nameof(index));
            int last = --_count;
            int moved = -1;
            if (index != last)
            {
                Matrix4x4 m = _matrices[last];
                _matrices[index] = m;
                _owners[index] = moved = _owners[last];
                Encapsulate(index / ChunkSize, m);
            }
            _owners[last] = -1;
            int chunks = ChunkCount;
            if (chunks < _chunkHasBounds.Length && _count % ChunkSize == 0)
                _chunkHasBounds[chunks] = false;
            Version++;
            return moved;
        }

        internal void Clear()
        {
            for (int i = 0; i < _count; i++)
                _owners[i] = -1;
            Array.Clear(_chunkHasBounds, 0, _chunkHasBounds.Length);
            _count = 0;
            Version++;
        }

        // The chunk-th run of this page (0..ChunkCount-1).
        internal void Chunk(int chunk, out FarRun run)
        {
            int start = chunk * ChunkSize;
            int n = Mathf.Min(ChunkSize, _count - start);
            if (chunk < 0 || n <= 0)
                throw new ArgumentOutOfRangeException(nameof(chunk));
            run = new FarRun
            {
                Mesh = Key.Mesh,
                Submesh = Key.Submesh,
                Material = Key.Material,
                PassIndex = PassIndex,
                Pass = Key.Pass,
                Lod = Key.Lod,
                Shadow = Key.Shadow,
                MpbBucket = Key.MpbBucket,
                Matrices = new NativeSlice<Matrix4x4>(_matrices, start, n),
                Bounds = _chunkBounds[chunk],
                Version = Version,
            };
        }

        // Tight chunk bounds again (after many moves or removals; O(Count)).
        internal void RecomputeBounds()
        {
            Array.Clear(_chunkHasBounds, 0, _chunkHasBounds.Length);
            for (int i = 0; i < _count; i++)
                Encapsulate(i / ChunkSize, _matrices[i]);
            Version++;
        }

        void Grow()
        {
            int cap = Math.Max(FarDrawLimits.MinPageCapacity, _owners.Length * 2);
            var matrices = new NativeArray<Matrix4x4>(cap, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            if (_matrices.IsCreated)
            {
                NativeArray<Matrix4x4>.Copy(_matrices, matrices, _count);
                _matrices.Dispose();
            }
            _matrices = matrices;
            Array.Resize(ref _owners, cap);
            for (int i = _count; i < cap; i++)
                _owners[i] = -1;
            int chunks = (cap + ChunkSize - 1) / ChunkSize;
            if (chunks > _chunkBounds.Length)
            {
                Array.Resize(ref _chunkBounds, chunks);
                Array.Resize(ref _chunkHasBounds, chunks);
            }
        }

        void Encapsulate(int chunk, in Matrix4x4 m)
        {
            Bounds b = WorldBounds(_local, m);
            if (_chunkHasBounds[chunk])
                _chunkBounds[chunk].Encapsulate(b);
            else
            {
                _chunkBounds[chunk] = b;
                _chunkHasBounds[chunk] = true;
            }
        }

        // The world AABB of local bounds under an affine matrix (centre moved, extents through |M|).
        internal static Bounds WorldBounds(in Bounds local, in Matrix4x4 m)
        {
            Vector3 c = m.MultiplyPoint3x4(local.center);
            Vector3 e = local.extents;
            var ext = new Vector3(
                Mathf.Abs(m.m00) * e.x + Mathf.Abs(m.m01) * e.y + Mathf.Abs(m.m02) * e.z,
                Mathf.Abs(m.m10) * e.x + Mathf.Abs(m.m11) * e.y + Mathf.Abs(m.m12) * e.z,
                Mathf.Abs(m.m20) * e.x + Mathf.Abs(m.m21) * e.y + Mathf.Abs(m.m22) * e.z);
            return new Bounds(c, ext * 2f);
        }

        public void Dispose()
        {
            if (_matrices.IsCreated)
                _matrices.Dispose();
            _owners = Array.Empty<int>();
            _chunkBounds = Array.Empty<Bounds>();
            _chunkHasBounds = Array.Empty<bool>();
            _count = 0;
            Version++;
        }
    }

    // Per-object material state that splits runs (§3.3), until P43 decides whether instanced property arrays carry it:
    //  - SnowLevel: WearNTear.UpdateSnowVisual's _SnowLevel (WearNTear.cs:772-791), -1 when not set;
    //  - RandomSeed: RandomMaterialValues' seed (ZDO "RandMatSeed"; RandomMaterialValues.cs:24-90), its vector
    //    properties follow from it; -1 when none;
    //  - AshlandsDamage: WearNTear's _TakingAshlandsDamage.
    internal struct FarMaterialState : IEquatable<FarMaterialState>
    {
        internal float SnowLevel;
        internal int RandomSeed;
        internal bool AshlandsDamage;

        internal static readonly FarMaterialState None = new FarMaterialState { SnowLevel = -1f, RandomSeed = -1 };

        public bool Equals(FarMaterialState o) =>
            SnowLevel.Equals(o.SnowLevel) && RandomSeed == o.RandomSeed && AshlandsDamage == o.AshlandsDamage;

        public override bool Equals(object o) => o is FarMaterialState s && Equals(s);

        public override int GetHashCode()
        {
            unchecked
            {
                return (SnowLevel.GetHashCode() * 31 + RandomSeed) * 31 + (AshlandsDamage ? 1 : 0);
            }
        }
    }

    // MPB buckets: one id per distinct FarMaterialState in an AreaContext's list (bucket 0: None, the material's own
    // values). Ids are never reused while the list lives.
    internal sealed class FarMpbBuckets
    {
        readonly Dictionary<FarMaterialState, int> _ids = new Dictionary<FarMaterialState, int>();
        readonly List<FarMaterialState> _states = new List<FarMaterialState>();

        internal FarMpbBuckets()
        {
            _ids.Add(FarMaterialState.None, 0);
            _states.Add(FarMaterialState.None);
        }

        internal int Count => _states.Count;

        internal int Of(in FarMaterialState state)
        {
            if (_ids.TryGetValue(state, out int id))
                return id;
            id = _states.Count;
            _ids.Add(state, id);
            _states.Add(state);
            return id;
        }

        internal FarMaterialState State(int bucket) => bucket >= 0 && bucket < _states.Count ? _states[bucket] : FarMaterialState.None;
    }

    // One patch of FarGrass placement (ARCHITECTURE-v2.md §3.17; P12: the game's UnityXorshift sequence), thinned
    // against C' (the far point the window looks from).
    internal struct FarGrassSpan
    {
        internal Vector2s Zone;
        internal Vector3 Center;
        internal float Radius;
        // The area's grass kind (its ClutterSystem entry) and the patch's placement seed.
        internal int Prototype;
        internal int Seed;
        // The share of the patch's instances kept after thinning (0..1).
        internal float Density;
        internal Bounds Bounds;
    }

    // A real renderer drawn into a window as it is (live-core objects, RealOnly ones): by DrawRenderer (P35) or only by
    // the hero slot (skinned and animated ones until P35 says otherwise).
    internal struct FarRealRenderer
    {
        internal Renderer Renderer;
        // Submeshes to draw (bit per submesh), -1 for all.
        internal int SubmeshMask;
        internal bool HeroOnly;
    }

    // The far content of one AreaContext: its pages, grass spans, real renderers and MPB buckets. Built and changed in
    // place by FarWorld's data layer (V3); read by every renderer of that context.
    internal sealed class FarDrawList : IDisposable
    {
        readonly Dictionary<FarRunKey, FarPage> _pages = new Dictionary<FarRunKey, FarPage>();
        // The same pages in the order they were made, to walk without allocating.
        readonly List<FarPage> _order = new List<FarPage>();
        internal readonly List<FarGrassSpan> Grass = new List<FarGrassSpan>();
        internal readonly List<FarRealRenderer> Real = new List<FarRealRenderer>();
        internal readonly FarMpbBuckets Buckets = new FarMpbBuckets();
        // Moves whenever a page is made or dropped, or the grass or real lists are changed through this class.
        internal int Version;

        internal int PageCount => _order.Count;
        internal FarPage PageAt(int i) => _order[i];

        internal int InstanceCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _order.Count; i++)
                    n += _order[i].Count;
                return n;
            }
        }

        internal long NativeBytes
        {
            get
            {
                long n = 0;
                for (int i = 0; i < _order.Count; i++)
                    n += _order[i].NativeBytes;
                return n;
            }
        }

        // The page of a key; made (empty) when create is set and there is none, otherwise null.
        internal FarPage Page(in FarRunKey key, bool create, int chunkSize = FarDrawLimits.HardMaxInstances)
        {
            if (_pages.TryGetValue(key, out FarPage page) || !create)
                return page;
            page = new FarPage(key, chunkSize);
            _pages.Add(key, page);
            _order.Add(page);
            Version++;
            return page;
        }

        // Drops the pages left empty (their native memory goes).
        internal int DropEmpty()
        {
            int dropped = 0;
            for (int i = _order.Count - 1; i >= 0; i--)
            {
                FarPage p = _order[i];
                if (p.Count > 0)
                    continue;
                _pages.Remove(p.Key);
                _order.RemoveAt(i);
                p.Dispose();
                dropped++;
            }
            if (dropped > 0)
                Version++;
            return dropped;
        }

        // Appends a run for every chunk of every page of this pass that holds instances and has the pass.
        internal void GetRuns(FarPass pass, List<FarRun> into)
        {
            for (int i = 0; i < _order.Count; i++)
            {
                FarPage p = _order[i];
                if (p.Key.Pass != pass || p.Count == 0 || p.PassIndex < 0)
                    continue;
                int chunks = p.ChunkCount;
                for (int c = 0; c < chunks; c++)
                {
                    p.Chunk(c, out FarRun run);
                    into.Add(run);
                }
            }
        }

        internal void SetGrass(List<FarGrassSpan> spans)
        {
            Grass.Clear();
            if (spans != null)
                Grass.AddRange(spans);
            Version++;
        }

        internal void SetReal(List<FarRealRenderer> renderers)
        {
            Real.Clear();
            if (renderers != null)
                Real.AddRange(renderers);
            Version++;
        }

        public void Dispose()
        {
            for (int i = 0; i < _order.Count; i++)
                _order[i].Dispose();
            _order.Clear();
            _pages.Clear();
            Grass.Clear();
            Real.Clear();
            Version++;
        }
    }
}
