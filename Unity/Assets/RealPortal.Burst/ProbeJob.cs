using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

namespace RealPortal.Burst
{
    // Tells whether Burst code runs: under Burst the [BurstDiscard] call is compiled out and Result[0] stays
    // ModeBurst; run as plain C# it overwrites it with ModeManaged. The rest is what the running code was compiled
    // with (see BurstJobs): from a native library of another build these differ from this assembly's. It says only
    // that this IJob ran as Burst on the calling thread; BurstJobs.ProbeJobs then checks each real job the way it is
    // scheduled.
    //
    // Never change this struct's fields: it is the one job that runs before the layout check.
    [BurstCompile(CompileSynchronously = true)]
    public struct ProbeJob : IJob
    {
        public NativeArray<int> Result;

        public void Execute()
        {
            Put(0, BurstJobs.ModeBurst);
            MarkManaged();
            Put(1, BurstJobs.ApiVersion);
            Put(2, UnsafeUtility.SizeOf<ProxyCullJob>());
            Put(3, UnsafeUtility.SizeOf<ProxyCompactJob>());
            Put(4, UnsafeUtility.SizeOf<ProxyViewCullJob>());
            Put(5, UnsafeUtility.SizeOf<ProxyViewEmitJob>());
        }

        // Within the caller's array only: the caller may come from another build with fewer slots.
        void Put(int index, int value)
        {
            if (index < Result.Length)
                Result[index] = value;
        }

        [BurstDiscard]
        void MarkManaged() => Put(0, BurstJobs.ModeManaged);
    }
}
