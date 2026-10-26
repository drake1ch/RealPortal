using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;

namespace RealPortal.Burst
{
    // FarWorld's proxy cull and emission for one window render, as Burst code: the same two jobs as the plugin's
    // ProxyDraw.ProxyCullJob and ProxyDraw.EmitJob (Plugin\FarWorld\Proxy\ProxyDraw.cs), body for body, on mirrors of
    // the plugin's tables. This assembly cannot see the plugin's types, so the plugin hands its arrays over as bytes
    // (NativeArray.Reinterpret) and BurstJobs.ScheduleViewCull / ScheduleViewEmit read them back as the mirrors below;
    // ViewSizes() tells the plugin their sizes, and it uses these jobs only when every one equals its own type's.
    // Change a mirror with its plugin type, and a job body with the plugin's job: the plugin compares the two on its
    // first culls and keeps its own jobs when they disagree.
    //
    // Each job is an IJobParallelFor over the ranges of one page (one sector's instances each); the jobs of every page
    // of a render run side by side, each range writing its own parts of the outputs. RangeBase < 0 is the probe
    // (BurstJobs.ProbeJobs): index i writes ModeBurst to Room[i] / Filled[i], or ModeManaged when it ran as plain C#.

    // ProxyInst.
    public struct ViewInst
    {
        public Matrix4x4 Root;
        public Vector3 Center;
        public float Radius;
        public Vector3 LodRef;
        public float LodSize;
        public int Template;
        public int Variant;
        public int LodSet;
        public uint Flags;
        public int Row;
    }

    // ProxyDraw.CullRange.
    public struct ViewRange
    {
        public int Start, Count, Out, DistantOnly;
    }

    // ProxyDraw.CullOut.
    public struct ViewOut
    {
        public byte Vis, Lod, Prev, Pad;
        public float Fade;
    }

    // ProxyTemplateData.
    public struct ViewTemplate
    {
        public int Prefab;
        public int PartStart, PartCount;
        public int LodCount;
        public int FadeMode;
        public bool AnimateCrossFading;
        public bool LodFadeInOut;
        public float LodSize;
        public Vector3 LodLocalRef;
        public Matrix4x4 LodLocal;
        public Vector4 HeightsA, HeightsB, FadeA, FadeB;

        public float Height(int level) => level < 4 ? HeightsA[level] : HeightsB[level - 4];
        public float FadeWidth(int level) => level < 4 ? FadeA[level] : FadeB[level - 4];
    }

    // ProxyVariantData.
    public struct ViewVariant
    {
        public int Template;
        public int PartStart, PartCount;
        public int LodSet;
        public Vector3 Center;
        public Vector3 Extents;
    }

    // ProxyVariantPart.
    public struct ViewVariantPart
    {
        public int Part;
        public int Batch;
        public int Mpb;
        public int LodMask;
        public int Run;
    }

    // ProxyPartData.
    public struct ViewPart
    {
        public Matrix4x4 Local;
        public Vector3 Center;
        public Vector3 Extents;
        public int Batch;
        public int LodMask;
        public int Template;
    }

    static class ViewConst
    {
        // ProxyFlags, ProxyDraw's level codes and row flags, ProxyPartData.LodAll, LODFadeMode.CrossFade, ShadowCastingMode.
        public const uint Created = 1, Distant = 8;
        public const byte NoLevel = 254, Culled = 255;
        public const byte RowFading = 1, RowBorn = 2;
        public const int LodAll = 0xFF, CrossFade = 1;
        public const int ShadowsOff = 0, TwoSided = 2, ShadowsOnly = 3;
    }

    // ProxyDraw.ProxyCullJob: planes [0, 6) the view, 6 the exit (positive where the window shows), [7, 7 + CasterPlanes)
    // the caster volume.
    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
    public struct ProxyViewCullJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<ViewInst> Page;
        [ReadOnly] public NativeArray<ViewRange> Ranges;
        [ReadOnly] public NativeArray<ViewTemplate> Templates;
        [ReadOnly] public NativeArray<ViewTemplate> LodSets;
        [ReadOnly] public NativeArray<ViewVariant> Variants;
        [ReadOnly] public NativeArray<byte> RowFlags;
        [ReadOnly] public NativeArray<Vector4> Planes;
        [NativeDisableParallelForRestriction, NativeDisableContainerSafetyRestriction] public NativeArray<ViewOut> Out;
        [NativeDisableParallelForRestriction, NativeDisableContainerSafetyRestriction] public NativeArray<int> Room;
        public int RangeBase, CasterPlanes, MaxLod;
        public Vector3 Cam, PrevCam, Light;
        public float K, PrevK, Margin;
        public bool HasPrev, Animated, Shadows, CameraSideCasts;
        public uint Skip;

        public void Execute(int index)
        {
            if (RangeBase < 0)
            {
                Room[index] = BurstJobs.ModeBurst;
                MarkManaged(index);
                return;
            }
            ViewRange r = Ranges[RangeBase + index];
            uint skip = Skip;
            int room = 0;
            for (int i = 0; i < r.Count; i++)
            {
                ViewInst inst = Page[r.Start + i];
                var o = new ViewOut();
                uint f = inst.Flags;
                if ((f & skip) == 0 && (r.DistantOnly == 0 || (f & ViewConst.Distant) != 0))
                {
                    Vector3 c = inst.Center;
                    float rad = inst.Radius + Margin;
                    Vector4 e = Planes[6];
                    byte vis;
                    if (e.x * c.x + e.y * c.y + e.z * c.z + e.w < -rad)
                        vis = CameraSideCasts && Sweep(7, CasterPlanes, c, rad, Light) ? (byte)3 : (byte)0;
                    else if (Inside(c, rad))
                        vis = 1;
                    else
                        vis = Shadows && Sweep(7, CasterPlanes, c, rad, Light) ? (byte)2 : (byte)0;
                    o.Vis = vis;
                    if (vis != 0)
                    {
                        room += Variants[inst.Variant].PartCount;
                        ViewTemplate td = inst.LodSet >= 0 ? LodSets[inst.LodSet] : Templates[inst.Template];
                        if (td.LodCount == 0)
                            o.Lod = o.Prev = ViewConst.NoLevel;
                        else
                        {
                            o.Lod = Select(in td, inst.LodSize, Vector3.Distance(Cam, inst.LodRef), K, out o.Fade);
                            o.Prev = HasPrev ? Select(in td, inst.LodSize, Vector3.Distance(PrevCam, inst.LodRef), PrevK, out _) : o.Lod;
                            o.Pad = Special(in td, in o, (uint)inst.Row < (uint)RowFlags.Length ? RowFlags[inst.Row] : (byte)0) ? (byte)1 : (byte)0;
                        }
                    }
                }
                Out[r.Out + i] = o;
            }
            Room[RangeBase + index] = room;
        }

        [BurstDiscard]
        void MarkManaged(int index) => Room[index] = BurstJobs.ModeManaged;

        bool Special(in ViewTemplate td, in ViewOut o, byte rowFlags)
        {
            if (td.LodFadeInOut && (rowFlags & ViewConst.RowBorn) != 0)
                return true;
            if (td.FadeMode == ViewConst.CrossFade && td.AnimateCrossFading && Animated)
                return (rowFlags & ViewConst.RowFading) != 0 || o.Prev != o.Lod;
            return td.FadeMode == ViewConst.CrossFade && o.Lod != ViewConst.Culled && o.Fade > 0f;
        }

        bool Inside(Vector3 c, float rad)
        {
            for (int i = 0; i < 6; i++)
            {
                Vector4 p = Planes[i];
                if (p.x * c.x + p.y * c.y + p.z * c.z + p.w < -rad)
                    return false;
            }
            return true;
        }

        // ProxyDraw.Sweep.
        bool Sweep(int from, int count, Vector3 c, float r, Vector3 ray)
        {
            float t0 = 0f, t1 = 1f;
            for (int i = from; i < from + count; i++)
            {
                Vector4 p = Planes[i];
                float a = p.x * c.x + p.y * c.y + p.z * c.z + p.w + r;
                float b = p.x * ray.x + p.y * ray.y + p.z * ray.z;
                if (b == 0f)
                {
                    if (a < 0f)
                        return false;
                    continue;
                }
                float t = -a / b;
                if (b > 0f)
                {
                    if (t > t0)
                        t0 = t;
                }
                else if (t < t1)
                    t1 = t;
                if (t0 > t1)
                    return false;
            }
            return true;
        }

        // ProxyCullJob.Select.
        byte Select(in ViewTemplate td, float size, float d, float k, out float fade)
        {
            fade = 0f;
            float rel = d > 1e-6f ? size * k / d : float.MaxValue;
            int n = td.LodCount;
            for (int i = (MaxLod < n - 1 ? MaxLod : n - 1); i < n; i++)
            {
                float h = td.Height(i);
                if (rel < h)
                    continue;
                if (td.FadeMode == ViewConst.CrossFade && !td.AnimateCrossFading)
                {
                    float upper = i == 0 ? 1f : td.Height(i - 1);
                    float zone = td.FadeWidth(i) * (upper - h);
                    if (zone > 0f && rel < h + zone)
                        fade = (rel - h) / zone;
                }
                return (byte)i;
            }
            return ViewConst.Culled;
        }
    }

    // ProxyDraw.EmitJob.
    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
    public struct ProxyViewEmitJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<ViewInst> Page;
        [ReadOnly] public NativeArray<ViewRange> Ranges;
        [ReadOnly] public NativeArray<ViewOut> Cull;
        [ReadOnly] public NativeArray<ViewVariant> Variants;
        [ReadOnly] public NativeArray<ViewVariantPart> VariantParts;
        [ReadOnly] public NativeArray<ViewPart> Parts;
        [ReadOnly] public NativeArray<int> BatchDraw;
        [ReadOnly] public NativeArray<int> Base;
        [NativeDisableParallelForRestriction, NativeDisableContainerSafetyRestriction] public NativeArray<int> Key;
        [NativeDisableParallelForRestriction, NativeDisableContainerSafetyRestriction] public NativeArray<Matrix4x4> M;
        [NativeDisableParallelForRestriction, NativeDisableContainerSafetyRestriction] public NativeArray<Vector4> S;
        [NativeDisableParallelForRestriction, NativeDisableContainerSafetyRestriction] public NativeArray<int> Filled;
        [NativeDisableParallelForRestriction, NativeDisableContainerSafetyRestriction] public NativeArray<int> Visible;
        [NativeDisableParallelForRestriction, NativeDisableContainerSafetyRestriction] public NativeArray<int> Casters;
        [NativeDisableParallelForRestriction, NativeDisableContainerSafetyRestriction] public NativeArray<int> SpecialCount;
        [NativeDisableParallelForRestriction, NativeDisableContainerSafetyRestriction] public NativeArray<int> Special;
        [NativeDisableParallelForRestriction, NativeDisableContainerSafetyRestriction] public NativeArray<int> RowDrawn;
        public int RangeBase, Mask, Frame;
        public bool ObliqueClip;

        public void Execute(int index)
        {
            if (RangeBase < 0)
            {
                Filled[index] = BurstJobs.ModeBurst;
                MarkManaged(index);
                return;
            }
            int ri = RangeBase + index;
            ViewRange r = Ranges[ri];
            int at = Base[ri], filled = 0, visible = 0, casters = 0, special = 0;
            for (int i = 0; i < r.Count; i++)
            {
                ViewOut o = Cull[r.Out + i];
                if (o.Vis == 0)
                    continue;
                if (o.Vis == 1)
                    visible++;
                else
                    casters++;
                if (o.Pad != 0)
                {
                    Special[r.Out + special++] = i;
                    continue;
                }
                ViewInst inst = Page[r.Start + i];
                ViewVariant v = Variants[inst.Variant];
                var sphere = new Vector4(inst.Center.x, inst.Center.y, inst.Center.z, inst.Radius);
                bool any = false;
                for (int k = v.PartStart, end = v.PartStart + v.PartCount; k < end; k++)
                {
                    ViewVariantPart vp = VariantParts[k];
                    if (o.Lod != ViewConst.NoLevel && vp.LodMask != ViewConst.LodAll && !(o.Lod < 8 && (vp.LodMask & (1 << o.Lod)) != 0))
                        continue;
                    int bd = (uint)vp.Batch < (uint)BatchDraw.Length ? BatchDraw[vp.Batch] : -1;
                    if (bd < 0)
                        continue;
                    int layer = bd & 0xFF, shadows = bd >> 8;
                    if (layer >= 32 || (Mask & (1 << layer)) == 0)
                        continue;
                    int mode;
                    if (o.Vis == 1)
                        mode = shadows;
                    else if (shadows == ViewConst.ShadowsOff)
                        continue;
                    else
                        mode = shadows == ViewConst.TwoSided && (o.Vis == 2 || ObliqueClip) ? ViewConst.TwoSided : ViewConst.ShadowsOnly;
                    int e = at + filled++;
                    Key[e] = vp.Run * 4 + mode;
                    M[e] = inst.Root * Parts[vp.Part].Local;
                    S[e] = sphere;
                    any = true;
                }
                if (any && o.Vis == 1 && (inst.Flags & ViewConst.Created) == 0 && (uint)inst.Row < (uint)RowDrawn.Length)
                    RowDrawn[inst.Row] = Frame;
            }
            Filled[ri] = filled;
            Visible[ri] = visible;
            Casters[ri] = casters;
            SpecialCount[ri] = special;
        }

        [BurstDiscard]
        void MarkManaged(int index) => Filled[index] = BurstJobs.ModeManaged;
    }
}
