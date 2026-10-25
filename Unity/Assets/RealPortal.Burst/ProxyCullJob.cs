using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace RealPortal.Burst
{
    // Skeleton of FarWorld's proxy cull (ARCHITECTURE-v2 §3.7 step 2): bounding spheres against the six frustum
    // planes. The LOD pick, the exit-plane rule, the sun caster volume and the emitted (batch, matrix) pairs come with
    // S5; the shape (64 instances per batch, ascending output per batch) is the one they will keep.
    //
    // IJobFor (CoreModule, as §3.1 has it) over batch indices: Execute(b) covers spheres [b*BatchSize, (b+1)*BatchSize)
    // and writes the indices of the visible ones, ascending, to Visible from b*BatchSize on, and how many to
    // BatchCounts[b]. Nothing in this assembly uses the Unity.Collections package, so the player build that makes the
    // native library has no Collections in it and the library holds only these jobs and Unity.Burst's own helpers.
    //
    // BatchSize <= 0 is the probe (BurstJobs.ProbeJobs), never a cull (CheckCullArguments refuses it): batch b then
    // writes ModeBurst to BatchCounts[b], or ModeManaged when it ran as plain C#.
    //
    // Strict float mode: no reassociation and no fused multiply-add, so the test is the same sequence of float
    // operations as the managed twin (BurstBootstrapModule.ManagedCullJob in the engine); keep the two identical, the
    // casts in Distance included. The engine refuses a build whose cull answers its known-answer sets differently from
    // its twin, so a changed body ships with the matching twin (and a rebuilt pair), or Burst stays off.
    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
    public struct ProxyCullJob : IJobFor
    {
        [ReadOnly] public NativeArray<float4> Spheres;
        [ReadOnly] public NativeArray<float4> Planes;
        // Batch b writes Visible[b*BatchSize ...], outside the index b the job system hands it.
        [NativeDisableParallelForRestriction] public NativeArray<int> Visible;
        public NativeArray<int> BatchCounts;
        public int BatchSize;

        public void Execute(int batch)
        {
            if (BatchSize <= 0)
            {
                BatchCounts[batch] = BurstJobs.ModeBurst;
                MarkManaged(batch);
                return;
            }
            float4 p0 = Planes[0], p1 = Planes[1], p2 = Planes[2], p3 = Planes[3], p4 = Planes[4], p5 = Planes[5];
            // b*BatchSize < Spheres.Length for every b the job gets, so neither line can overflow.
            int start = batch * BatchSize;
            int stop = start + math.min(BatchSize, Spheres.Length - start);
            int n = 0;
            for (int i = start; i < stop; i++)
            {
                float4 s = Spheres[i];
                float r = -s.w;
                if (Distance(p0, s) < r || Distance(p1, s) < r || Distance(p2, s) < r ||
                    Distance(p3, s) < r || Distance(p4, s) < r || Distance(p5, s) < r)
                    continue;
                Visible[start + n] = i;
                n++;
            }
            BatchCounts[batch] = n;
        }

        [BurstDiscard]
        void MarkManaged(int batch) => BatchCounts[batch] = BurstJobs.ModeManaged;

        // Signed distance of the sphere's centre to the plane, as UnityEngine.Plane.GetDistanceToPoint, rounded to float
        // after every operation. Burst computes in float anyway (the casts compile to nothing); Mono keeps float
        // arithmetic in double unless each result is cast back, so the casts are what make the managed twin and the
        // self-test's reference give Burst's answer for a sphere within a float step of a plane.
        static float Distance(float4 p, float4 s) =>
            (float)((float)((float)((float)(p.x * s.x) + (float)(p.y * s.y)) + (float)(p.z * s.z)) + p.w);
    }

    // Packs the per-batch runs ProxyCullJob left in Visible to its front: Visible[0 .. VisibleCount[0]) then holds
    // every visible index, ascending. A run never moves forward (it starts at or after where it lands), so packing in
    // place is safe. BatchSize <= 0 is the probe: VisibleCount[0] = ModeBurst, or ModeManaged as plain C#.
    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
    public struct ProxyCompactJob : IJob
    {
        public NativeArray<int> Visible;
        [ReadOnly] public NativeArray<int> BatchCounts;
        public NativeArray<int> VisibleCount;
        public int BatchSize;

        public void Execute()
        {
            if (BatchSize <= 0)
            {
                VisibleCount[0] = BurstJobs.ModeBurst;
                MarkManaged();
                return;
            }
            int written = 0;
            for (int b = 0; b < BatchCounts.Length; b++)
            {
                int from = b * BatchSize;
                int n = BatchCounts[b];
                if (from != written)
                    for (int i = 0; i < n; i++)
                        Visible[written + i] = Visible[from + i];
                written += n;
            }
            VisibleCount[0] = written;
        }

        [BurstDiscard]
        void MarkManaged() => VisibleCount[0] = BurstJobs.ModeManaged;
    }
}
