using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace RealPortal.Burst
{
    // The entry points the engine's BurstBootstrap module calls (by reflection: this assembly is loaded late, from
    // next to the plugin, and the engine does not reference it). Self-contained: only Unity's CoreModule (NativeArray,
    // IJob, IJobFor), Burst and Mathematics, which the game ships; no Valheim types and no Unity.Collections package.
    //
    // The two files the engine loads, RealPortal.Burst.dll and RealPortal.Burst_win_x86_64.dll, must come from the
    // same player build (Editor/BurstLibraryBuild.cs). Burst finds a job's native code by the job's name, not its
    // layout, so native code built for other fields would read the wrong memory. The probe catches that: it reports
    // ApiVersion and the job sizes as compiled into the native code, and the engine compares them with what this
    // managed assembly says (ExpectedLayout) before it uses any job here. Bump ApiVersion whenever a job's fields,
    // its job interface or an entry point here change (and BurstBootstrapModule.ApiVersion with it). A changed job body
    // with the same fields passes all that: the engine's known-answer check (the cull here against its managed twin)
    // is what refuses it, so change the twin with the body.
    public static class BurstJobs
    {
        // 2: ProxyCullJob is an IJobFor over batches; ProbeJobs. 3: ProxyViewCullJob and ProxyViewEmitJob (ScheduleViewCull,
        // ScheduleViewEmit, ViewSizes).
        public const int ApiVersion = 3;

        // Probe()[0] and ProbeJobs()[i]: which code ran.
        public const int ModeBurst = 1;
        public const int ModeManaged = 2;

        // Probe slots. Fixed for good: a probe compiled from newer sources may fill more of them (ProbeJob writes
        // only what fits), and an older one leaves the new ones 0, so either way the layout check below fails.
        public const int ProbeLength = 16;

        // { mode, ApiVersion, sizeof(ProxyCullJob), sizeof(ProxyCompactJob), 0... } as seen by the code that ran: the
        // native library's when Burst ran it, this assembly's otherwise. Runs on the calling (main) thread.
        public static int[] Probe()
        {
            var result = new NativeArray<int>(ProbeLength, Allocator.TempJob);
            try
            {
                new ProbeJob { Result = result }.Run();
                return result.ToArray();
            }
            finally
            {
                result.Dispose();
            }
        }

        // What Probe()[1..] must be, from this managed assembly, before any other job here may run with Burst:
        // { ApiVersion, sizeof(ProxyCullJob), sizeof(ProxyCompactJob), 0... }. A new job adds its size here and in
        // ProbeJob.Execute.
        public static int[] ExpectedLayout()
        {
            var layout = new int[ProbeLength - 1];
            layout[0] = ApiVersion;
            layout[1] = UnsafeUtility.SizeOf<ProxyCullJob>();
            layout[2] = UnsafeUtility.SizeOf<ProxyCompactJob>();
            layout[3] = UnsafeUtility.SizeOf<ProxyViewCullJob>();
            layout[4] = UnsafeUtility.SizeOf<ProxyViewEmitJob>();
            return layout;
        }

        // The jobs ProbeJobs() reports on, in its order. A new job adds its name here and its probe to ProbeJobs.
        public static readonly string[] JobNames = { nameof(ProxyCullJob), nameof(ProxyCompactJob), nameof(ProxyViewCullJob), nameof(ProxyViewEmitJob) };

        // The sizes of the plugin types' mirrors (ProxyViewJobs.cs): ViewInst, ViewRange, ViewOut, ViewTemplate,
        // ViewVariant, ViewVariantPart, ViewPart. The plugin uses the view jobs only when each equals its own type's.
        public static int[] ViewSizes() => new[]
        {
            UnsafeUtility.SizeOf<ViewInst>(), UnsafeUtility.SizeOf<ViewRange>(), UnsafeUtility.SizeOf<ViewOut>(), UnsafeUtility.SizeOf<ViewTemplate>(),
            UnsafeUtility.SizeOf<ViewVariant>(), UnsafeUtility.SizeOf<ViewVariantPart>(), UnsafeUtility.SizeOf<ViewPart>(),
        };

        public const int ProbeBatches = 16;

        // Which code each job of JobNames runs, run the way ScheduleCull runs it: ProxyCullJob.ScheduleParallel over
        // ProbeBatches indices and ProxyCompactJob.Schedule, both on worker threads and completed here. A job is
        // ModeBurst only when every index of it ran as Burst code: ProbeJob (an IJob run on this thread) says nothing
        // about another job type or another job interface's entry (a native library missing it, or a CoreModule whose
        // producer no longer matches it, runs the job as plain C#). Call it only after Probe()'s layout matched: unlike
        // ProbeJob, these are the real jobs. Main thread.
        public static int[] ProbeJobs()
        {
            var spheres = new NativeArray<float4>(1, Allocator.TempJob);
            var planes = new NativeArray<float4>(6, Allocator.TempJob);
            var visible = new NativeArray<int>(1, Allocator.TempJob);
            var counts = new NativeArray<int>(ProbeBatches, Allocator.TempJob);
            var total = new NativeArray<int>(1, Allocator.TempJob);
            try
            {
                new ProxyCullJob { Spheres = spheres, Planes = planes, Visible = visible, BatchCounts = counts, BatchSize = 0 }
                    .ScheduleParallel(ProbeBatches, 1, default).Complete();
                int cull = ModeBurst;
                for (int b = 0; b < ProbeBatches && cull == ModeBurst; b++)
                    cull = counts[b];
                new ProxyCompactJob { Visible = visible, BatchCounts = counts, VisibleCount = total, BatchSize = 0 }.Schedule().Complete();
                return new[] { cull, total[0], ProbeViewCull(), ProbeViewEmit() };
            }
            finally
            {
                spheres.Dispose();
                planes.Dispose();
                visible.Dispose();
                counts.Dispose();
                total.Dispose();
            }
        }

        // The view jobs in probe mode (RangeBase -1) over ProbeBatches indices, scheduled as ScheduleView* schedules them;
        // every other array one element long (the job reads none of them then).
        static int ProbeViewCull()
        {
            var modes = new NativeArray<int>(ProbeBatches, Allocator.TempJob);
            var inst = new NativeArray<ViewInst>(1, Allocator.TempJob);
            var ranges = new NativeArray<ViewRange>(1, Allocator.TempJob);
            var templates = new NativeArray<ViewTemplate>(1, Allocator.TempJob);
            var variants = new NativeArray<ViewVariant>(1, Allocator.TempJob);
            var rowFlags = new NativeArray<byte>(1, Allocator.TempJob);
            var planes = new NativeArray<Vector4>(1, Allocator.TempJob);
            var outs = new NativeArray<ViewOut>(1, Allocator.TempJob);
            try
            {
                new ProxyViewCullJob
                {
                    Page = inst, Ranges = ranges, Templates = templates, LodSets = templates, Variants = variants, RowFlags = rowFlags, Planes = planes,
                    Out = outs, Room = modes, RangeBase = -1,
                }.Schedule(ProbeBatches, 1).Complete();
                return AllBurst(modes);
            }
            finally
            {
                modes.Dispose();
                inst.Dispose();
                ranges.Dispose();
                templates.Dispose();
                variants.Dispose();
                rowFlags.Dispose();
                planes.Dispose();
                outs.Dispose();
            }
        }

        static int ProbeViewEmit()
        {
            var modes = new NativeArray<int>(ProbeBatches, Allocator.TempJob);
            var inst = new NativeArray<ViewInst>(1, Allocator.TempJob);
            var ranges = new NativeArray<ViewRange>(1, Allocator.TempJob);
            var outs = new NativeArray<ViewOut>(1, Allocator.TempJob);
            var variants = new NativeArray<ViewVariant>(1, Allocator.TempJob);
            var variantParts = new NativeArray<ViewVariantPart>(1, Allocator.TempJob);
            var parts = new NativeArray<ViewPart>(1, Allocator.TempJob);
            var ints = new NativeArray<int>(1, Allocator.TempJob);
            var m = new NativeArray<Matrix4x4>(1, Allocator.TempJob);
            var s = new NativeArray<Vector4>(1, Allocator.TempJob);
            try
            {
                new ProxyViewEmitJob
                {
                    Page = inst, Ranges = ranges, Cull = outs, Variants = variants, VariantParts = variantParts, Parts = parts, BatchDraw = ints, Base = ints,
                    Key = ints, M = m, S = s, Filled = modes, Visible = ints, Casters = ints, SpecialCount = ints, Special = ints, RowDrawn = ints, RangeBase = -1,
                }.Schedule(ProbeBatches, 1).Complete();
                return AllBurst(modes);
            }
            finally
            {
                modes.Dispose();
                inst.Dispose();
                ranges.Dispose();
                outs.Dispose();
                variants.Dispose();
                variantParts.Dispose();
                parts.Dispose();
                ints.Dispose();
                m.Dispose();
                s.Dispose();
            }
        }

        static int AllBurst(NativeArray<int> modes)
        {
            for (int i = 0; i < modes.Length; i++)
                if (modes[i] != ModeBurst)
                    return modes[i];
            return ModeBurst;
        }

        // ProxyViewCullJob over `rangeCount` ranges from `rangeBase` of one page (the plugin's ProxyDraw.ProxyCullJob, see
        // ProxyViewJobs.cs). The plugin's arrays as bytes: page ProxyInst, ranges CullRange, templates and lodSets
        // ProxyTemplateData, variants ProxyVariantData, outs CullOut. cam = (C', K), prevCam = (previous C', previous K),
        // light = (the caster sweep, Margin); flags: 1 HasPrev, 2 Animated, 4 Shadows, 8 CameraSideCasts. Main thread; the
        // arrays belong to the caller until the handle completes.
        public static JobHandle ScheduleViewCull(NativeArray<byte> page, NativeArray<byte> ranges, NativeArray<byte> templates, NativeArray<byte> lodSets,
            NativeArray<byte> variants, NativeArray<byte> rowFlags, NativeArray<Vector4> planes, NativeArray<byte> outs, NativeArray<int> room,
            int rangeBase, int rangeCount, int casterPlanes, int maxLod, uint skip, Vector4 cam, Vector4 prevCam, Vector4 light, int flags,
            JobHandle dependsOn)
        {
            return new ProxyViewCullJob
            {
                Page = page.Reinterpret<ViewInst>(1),
                Ranges = ranges.Reinterpret<ViewRange>(1),
                Templates = templates.Reinterpret<ViewTemplate>(1),
                LodSets = lodSets.Reinterpret<ViewTemplate>(1),
                Variants = variants.Reinterpret<ViewVariant>(1),
                RowFlags = rowFlags,
                Planes = planes,
                Out = outs.Reinterpret<ViewOut>(1),
                Room = room,
                RangeBase = rangeBase,
                CasterPlanes = casterPlanes,
                MaxLod = maxLod,
                Skip = skip,
                Cam = new Vector3(cam.x, cam.y, cam.z),
                K = cam.w,
                PrevCam = new Vector3(prevCam.x, prevCam.y, prevCam.z),
                PrevK = prevCam.w,
                Light = new Vector3(light.x, light.y, light.z),
                Margin = light.w,
                HasPrev = (flags & 1) != 0,
                Animated = (flags & 2) != 0,
                Shadows = (flags & 4) != 0,
                CameraSideCasts = (flags & 8) != 0,
            }.Schedule(rangeCount, 1, dependsOn);
        }

        // ProxyViewEmitJob over `rangeCount` ranges from `rangeBase` of one page (the plugin's ProxyDraw.EmitJob). As bytes:
        // page ProxyInst, ranges CullRange, cull CullOut, variants ProxyVariantData, variantParts ProxyVariantPart, parts
        // ProxyPartData. Main thread; the arrays belong to the caller until the handle completes.
        public static JobHandle ScheduleViewEmit(NativeArray<byte> page, NativeArray<byte> ranges, NativeArray<byte> cull, NativeArray<byte> variants,
            NativeArray<byte> variantParts, NativeArray<byte> parts, NativeArray<int> batchDraw, NativeArray<int> bases, NativeArray<int> key,
            NativeArray<Matrix4x4> m, NativeArray<Vector4> s, NativeArray<int> filled, NativeArray<int> visible, NativeArray<int> casters,
            NativeArray<int> specialCount, NativeArray<int> special, NativeArray<int> rowDrawn, int rangeBase, int rangeCount, int mask, int frame,
            bool obliqueClip, JobHandle dependsOn)
        {
            return new ProxyViewEmitJob
            {
                Page = page.Reinterpret<ViewInst>(1),
                Ranges = ranges.Reinterpret<ViewRange>(1),
                Cull = cull.Reinterpret<ViewOut>(1),
                Variants = variants.Reinterpret<ViewVariant>(1),
                VariantParts = variantParts.Reinterpret<ViewVariantPart>(1),
                Parts = parts.Reinterpret<ViewPart>(1),
                BatchDraw = batchDraw,
                Base = bases,
                Key = key,
                M = m,
                S = s,
                Filled = filled,
                Visible = visible,
                Casters = casters,
                SpecialCount = specialCount,
                Special = special,
                RowDrawn = rowDrawn,
                RangeBase = rangeBase,
                Mask = mask,
                Frame = frame,
                ObliqueClip = obliqueClip,
            }.Schedule(rangeCount, 1, dependsOn);
        }

        // ceil(length / batchSize) without overflow; batchSize > 0.
        public static int BatchCount(int length, int batchSize) => length / batchSize + (length % batchSize != 0 ? 1 : 0);

        // Frustum cull of bounding spheres, in two jobs:
        //  - ProxyCullJob (IJobFor over batches, parallel): batch b covers batchSize spheres, writes the indices of its
        //    visible spheres, ascending, to visible[b*batchSize ...] and their number to batchCounts[b];
        //  - ProxyCompactJob: packs those runs to the front of visible, so after completion
        //    visible[0 .. visibleCount[0]) holds every visible index in ascending order.
        // spheres: xyz centre, w radius (world space). planes: 6 planes, xyz normal pointing inside, w distance
        // (UnityEngine.Plane as (normal, distance); GeometryUtility.CalculateFrustumPlanes gives these); a sphere is
        // visible when dot(n, c) + d >= -r for all six. visible: spheres.Length; batchCounts:
        // BatchCount(spheres.Length, batchSize); visibleCount: 1. The arrays belong to the caller until the returned
        // handle completes. Schedule from the main thread; the caller kicks the batch (JobHandle.ScheduleBatchedJobs).
        public static JobHandle ScheduleCull(NativeArray<float4> spheres, NativeArray<float4> planes, NativeArray<int> visible,
            NativeArray<int> batchCounts, NativeArray<int> visibleCount, int batchSize, JobHandle dependsOn)
        {
            CheckCullArguments(spheres.Length, planes.Length, visible.Length, batchCounts.Length, visibleCount.Length, batchSize);
            var cull = new ProxyCullJob
            {
                Spheres = spheres,
                Planes = planes,
                Visible = visible,
                BatchCounts = batchCounts,
                BatchSize = batchSize,
            };
            JobHandle culled = cull.ScheduleParallel(batchCounts.Length, 1, dependsOn);
            var compact = new ProxyCompactJob
            {
                Visible = visible,
                BatchCounts = batchCounts,
                BatchSize = batchSize,
                VisibleCount = visibleCount,
            };
            return compact.Schedule(culled);
        }

        public static void CheckCullArguments(int spheres, int planes, int visible, int batchCounts, int visibleCount, int batchSize)
        {
            if (batchSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(batchSize), batchSize, "must be positive");
            if (planes != 6)
                throw new ArgumentException($"planes must hold 6 planes, has {planes}");
            if (visible < spheres)
                throw new ArgumentException($"visible must hold {spheres} indices, has {visible}");
            if (batchCounts != BatchCount(spheres, batchSize))
                throw new ArgumentException($"batchCounts must hold {BatchCount(spheres, batchSize)} counts, has {batchCounts}");
            if (visibleCount < 1)
                throw new ArgumentException("visibleCount must hold 1 value");
        }
    }
}
