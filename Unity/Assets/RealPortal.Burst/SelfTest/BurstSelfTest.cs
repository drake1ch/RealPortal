using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Random = System.Random;

namespace RealPortal.Burst.SelfTest
{
    // The checks Editor/BurstLibraryBuild.cs runs twice: in the editor (Burst's JIT code, safety checks on) before the
    // player build, and inside the built player (the AOT native library that ships, x64 SSE2/AVX2, no safety checks)
    // before anything is copied to Build\. This assembly is not shipped: only RealPortal.Burst.dll and the native
    // library leave the player build, and the engine never runs this assembly's initialiser.
    public static class BurstSelfTest
    {
        // Player: RealPortalBurst.exe -batchmode -nographics -realportal-burst-selftest <result file> writes "ok" or
        // "failed" on the first line of the file, then what Run logged, and quits with 0 or 1.
        public const string Argument = "-realportal-burst-selftest";
        public const string FailPrefix = "FAIL ";

        // Every check passed: Burst compilation is on, ProbeJob and every job of BurstJobs.JobNames ran as Burst code,
        // the probe's layout equals the assembly's, the cull equals a plain C# reference on 21 random sets, and on
        // boundary spheres (tangent to a plane, one float step either side, and float-versus-double rounding cases) it
        // gives the answers known in advance. Answers the number of failed checks; log gets one line per check,
        // failures start with FailPrefix. Main thread.
        public static int Run(Action<string> log)
        {
            int failures = 0;
            void Fail(string message)
            {
                failures++;
                log(FailPrefix + message);
            }

            bool enabled = Unity.Burst.BurstCompiler.IsEnabled;
            int[] probe = BurstJobs.Probe();
            int[] expected = BurstJobs.ExpectedLayout();
            log($"probe: {Mode(probe[0])} (BurstCompiler.IsEnabled {enabled}); layout {Layout(probe.Skip(1))}, expected {Layout(expected)}");
            if (!enabled)
                Fail("Burst compilation is off (Jobs > Burst > Enable Compilation, or a disable flag): nothing here would test Burst code");
            if (probe[0] != BurstJobs.ModeBurst)
                Fail("the probe did not run as Burst code");
            if (!probe.Skip(1).SequenceEqual(expected))
            {
                // The jobs below must not run on a layout nobody vouched for.
                Fail("the probe's layout differs from the assembly's");
                return failures;
            }

            int[] jobs = BurstJobs.ProbeJobs();
            log("jobs: " + string.Join(", ", BurstJobs.JobNames.Select((name, i) => $"{name} {Mode(i < jobs.Length ? jobs[i] : 0)}")));
            for (int i = 0; i < BurstJobs.JobNames.Length; i++)
                if (i >= jobs.Length || jobs[i] != BurstJobs.ModeBurst)
                    Fail($"{BurstJobs.JobNames[i]} did not run as Burst code where it is scheduled");

            var rng = new Random(12345);
            int differ = 0;
            foreach (int n in new[] { 0, 1, 63, 64, 65, 1000, 20000 })
                foreach (int batch in new[] { 1, 7, 64 })
                {
                    string problem = CullCase(rng, n, batch);
                    if (problem != null)
                    {
                        differ++;
                        Fail(problem);
                    }
                }
            log(differ == 0 ? "cull: all 21 random cases equal the reference" : $"cull: {differ} of 21 random case(s) differ");

            // Random spheres almost never come within a float step of a plane; these do, with answers fixed in advance.
            int boundaryFailures = 0;
            foreach (string problem in BoundaryCases(log))
            {
                boundaryFailures++;
                Fail(problem);
            }
            if (boundaryFailures == 0)
                log("boundary: tangent and rounding spheres give the known answers in batches of 1, 7 and 64");
            return failures;
        }

        // Signed plane distance as the jobs compute it: rounded to float after every operation. Mono keeps float
        // arithmetic in double unless each result is cast back, so the plain expression is not a reference for Burst.
        static float Distance(float4 p, float4 s) =>
            (float)((float)((float)((float)(p.x * s.x) + (float)(p.y * s.y)) + (float)(p.z * s.z)) + p.w);

        // The same expression without the casts, only to log what this runtime makes of it.
        static float PlainDistance(float4 p, float4 s) => p.x * s.x + p.y * s.y + p.z * s.z + p.w;

        static bool Visible(float4[] planes, float4 s, bool plain)
        {
            for (int p = 0; p < 6; p++)
                if ((plain ? PlainDistance(planes[p], s) : Distance(planes[p], s)) < -s.w)
                    return false;
            return true;
        }

        // Problems found; log gets what the plain (uncast) expression does on this runtime.
        static List<string> BoundaryCases(Action<string> log)
        {
            var problems = new List<string>();

            // Rounding: plane 0: -a*a rounds to -w in float, distance 0 (visible), -2^-24 in double (culled); plane 1:
            // 1 + t*t + t*t rounds to 1 in float, distance -2^-23 (culled), 0 in double (visible); planes 2-5 pass
            // everything. a = 1+2^-12, w = 1+2^-11, t = 2^-12, u = 1+2^-23, all exact. Spheres: A, B, inside, x3.
            const float a = 1f + 1f / 4096f, w = 1f + 1f / 2048f, t = 1f / 4096f, u = 1f + 1f / 8388608f;
            var pass = new float4(0f, 0f, 0f, 1f);
            float4[] rounding = { new float4(-a, 0f, 0f, w), new float4(1f, t, t, -u), pass, pass, pass, pass };
            var edge = new float4[9];
            for (int i = 0; i < edge.Length; i += 3)
            {
                edge[i] = new float4(a, 0f, 0f, 0f);
                edge[i + 1] = new float4(1f, t, t, 0f);
                edge[i + 2] = new float4(0f, 0f, 0f, 2f);
            }
            int[] roundingAnswer = { 0, 2, 3, 5, 6, 8 };
            Expect("rounding", rounding, edge, roundingAnswer, problems);
            int[] plain = Enumerable.Range(0, edge.Length).Where(i => Visible(rounding, edge[i], true)).ToArray();
            log($"boundary: the uncast C# expression keeps {string.Join(",", plain)} on this runtime, " +
                $"float rounding (Burst, the casts) keeps {string.Join(",", roundingAnswer)}");

            // Tangent: centres outside exactly one plane of a fixed frustum, radius = their distance to it (visible),
            // one float step less (culled), one more (visible).
            var frustum = new Plane[6];
            Matrix4x4 view = Matrix4x4.TRS(new Vector3(0f, 10f, 0f), Quaternion.Euler(10f, 75f, 0f), Vector3.one).inverse;
            GeometryUtility.CalculateFrustumPlanes(Matrix4x4.Perspective(60f, 16f / 9f, 0.3f, 500f) * view, frustum);
            var framed = new float4[6];
            for (int i = 0; i < 6; i++)
                framed[i] = new float4(frustum[i].normal.x, frustum[i].normal.y, frustum[i].normal.z, frustum[i].distance);
            var rng = new Random(24601);
            var tangent = new List<float4>();
            var tangentAnswer = new List<int>();
            for (int tries = 0; tangent.Count < 300 && tries < 100000; tries++)
            {
                var c = new float4((float)(rng.NextDouble() * 1200 - 600), (float)(rng.NextDouble() * 200 - 50),
                    (float)(rng.NextDouble() * 1200 - 600), 0f);
                int outside = -1, outsideCount = 0;
                for (int k = 0; k < 6; k++)
                    if (Distance(framed[k], c) < 0f)
                    {
                        outside = k;
                        outsideCount++;
                    }
                if (outsideCount != 1)
                    continue;
                float radius = -Distance(framed[outside], c);
                if (radius < 1e-3f)
                    continue;
                tangentAnswer.Add(tangent.Count);
                tangent.Add(new float4(c.xyz, radius));
                tangent.Add(new float4(c.xyz, math.asfloat(math.asint(radius) - 1)));
                tangentAnswer.Add(tangent.Count);
                tangent.Add(new float4(c.xyz, math.asfloat(math.asint(radius) + 1)));
            }
            if (tangent.Count < 300)
                problems.Add($"tangent: only {tangent.Count / 3} centres outside exactly one plane were found");
            Expect("tangent", framed, tangent.ToArray(), tangentAnswer.ToArray(), problems);
            return problems;
        }

        // The cull, in batches of 1, 7 and 64, must keep exactly answer; so must the reference (it checks the casts).
        static void Expect(string name, float4[] planeValues, float4[] sphereValues, int[] answer, List<string> problems)
        {
            int n = sphereValues.Length;
            int[] reference = Enumerable.Range(0, n).Where(i => Visible(planeValues, sphereValues[i], false)).ToArray();
            if (!reference.SequenceEqual(answer))
                problems.Add($"{name}: the C# reference keeps {reference.Length} spheres, the known answer {answer.Length}: Distance's casts do not round to float here");
            var spheres = new NativeArray<float4>(sphereValues, Allocator.TempJob);
            var planes = new NativeArray<float4>(planeValues, Allocator.TempJob);
            var visible = new NativeArray<int>(n, Allocator.TempJob);
            var total = new NativeArray<int>(1, Allocator.TempJob);
            try
            {
                foreach (int batch in new[] { 1, 7, 64 })
                {
                    var counts = new NativeArray<int>(BurstJobs.BatchCount(n, batch), Allocator.TempJob);
                    try
                    {
                        BurstJobs.ScheduleCull(spheres, planes, visible, counts, total, batch, default).Complete();
                    }
                    finally
                    {
                        counts.Dispose();
                    }
                    int[] got = visible.GetSubArray(0, total[0]).ToArray();
                    if (!got.SequenceEqual(answer))
                    {
                        int at = 0;
                        while (at < got.Length && at < answer.Length && got[at] == answer[at])
                            at++;
                        problems.Add($"{name} batch={batch}: the job keeps {got.Length} spheres, the known answer {answer.Length}; " +
                            $"first difference at {at}: job {(at < got.Length ? got[at].ToString() : "-")}, answer {(at < answer.Length ? answer[at].ToString() : "-")}");
                    }
                }
            }
            finally
            {
                spheres.Dispose();
                planes.Dispose();
                visible.Dispose();
                total.Dispose();
            }
        }

        static string Mode(int mode) => mode == BurstJobs.ModeBurst ? "Burst" : mode == BurstJobs.ModeManaged ? "managed" : "unknown " + mode;

        static string Layout(IEnumerable<int> slots) => string.Join("/", slots.Reverse().SkipWhile(v => v == 0).Reverse());

        // null when the cull of n random spheres in batches of batch equals the reference.
        static string CullCase(Random rng, int n, int batch)
        {
            var planesManaged = new Plane[6];
            Matrix4x4 proj = Matrix4x4.Perspective(60f, 16f / 9f, 0.3f, 500f);
            Matrix4x4 view = Matrix4x4.TRS(new Vector3((float)rng.NextDouble() * 50f, 10f, 0f),
                Quaternion.Euler(10f, (float)rng.NextDouble() * 360f, 0f), Vector3.one).inverse;
            GeometryUtility.CalculateFrustumPlanes(proj * view, planesManaged);

            var spheres = new NativeArray<float4>(n, Allocator.TempJob);
            var planes = new NativeArray<float4>(6, Allocator.TempJob);
            var visible = new NativeArray<int>(n, Allocator.TempJob);
            var counts = new NativeArray<int>(BurstJobs.BatchCount(n, batch), Allocator.TempJob);
            var total = new NativeArray<int>(1, Allocator.TempJob);
            try
            {
                for (int i = 0; i < 6; i++)
                    planes[i] = new float4(planesManaged[i].normal.x, planesManaged[i].normal.y, planesManaged[i].normal.z, planesManaged[i].distance);
                for (int i = 0; i < n; i++)
                    spheres[i] = new float4((float)(rng.NextDouble() * 1200 - 600), (float)(rng.NextDouble() * 200 - 50),
                        (float)(rng.NextDouble() * 1200 - 600), (float)(rng.NextDouble() * 20));
                BurstJobs.ScheduleCull(spheres, planes, visible, counts, total, batch, default).Complete();

                // Reference: the same test in plain C# (float rounding, as the jobs), and Unity's own plane distance as a
                // sanity check of the convention (random spheres never come within its rounding of a plane).
                int expected = 0, unityDisagrees = 0;
                var reference = new int[n];
                for (int i = 0; i < n; i++)
                {
                    float4 s = spheres[i];
                    bool inside = true, insideUnity = true;
                    for (int p = 0; p < 6; p++)
                    {
                        if (Distance(planes[p], s) < -s.w)
                            inside = false;
                        if (planesManaged[p].GetDistanceToPoint(new Vector3(s.x, s.y, s.z)) < -s.w)
                            insideUnity = false;
                    }
                    if (inside != insideUnity)
                        unityDisagrees++;
                    if (inside)
                        reference[expected++] = i;
                }
                bool same = total[0] == expected;
                for (int i = 0; same && i < expected; i++)
                    same = visible[i] == reference[i];
                if (same && unityDisagrees == 0)
                    return null;
                return $"cull n={n} batch={batch}: job {total[0]} visible, reference {expected}, " +
                    $"order {(same ? "same" : "differs")}, Plane.GetDistanceToPoint disagrees on {unityDisagrees}";
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

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void RunInPlayer()
        {
            if (Application.isEditor)
                return;
            string[] args = Environment.GetCommandLineArgs();
            int at = Array.IndexOf(args, Argument);
            if (at < 0 || at + 1 >= args.Length)
                return;
            var text = new StringBuilder();
            int failures;
            try
            {
                failures = Run(line => text.Append(line).Append('\n'));
            }
            catch (Exception e)
            {
                failures = 1;
                text.Append(FailPrefix).Append(e).Append('\n');
            }
            try
            {
                File.WriteAllText(args[at + 1], (failures == 0 ? "ok" : "failed") + "\n" + text);
            }
            catch (Exception e)
            {
                Debug.LogError("[BurstSelfTest] could not write " + args[at + 1] + ": " + e.Message);
                failures = Math.Max(failures, 1);
            }
            Application.Quit(failures == 0 ? 0 : 1);
        }
    }
}
