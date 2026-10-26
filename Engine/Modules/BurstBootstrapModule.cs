using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace RealPortal.Engine
{
    // E8 (ARCHITECTURE-v2 §5.2): Burst-compiled jobs for a mod. The game ships Burst (its own lib_burst_generated.dll,
    // used by Unity.Collections and MagicaCloth), and Burst's modding route loads more native code next to it:
    // BurstRuntime.LoadAdditionalLibrary(path). Our jobs live in RealPortal.Burst.dll (Unity\Assets\RealPortal.Burst,
    // built with Unity\Assets\RealPortal\Editor\BurstLibraryBuild.cs), and their native code in
    // RealPortal.Burst_win_x86_64.dll, the lib_burst_generated.dll of the same player build.
    //
    // Activation is deferred: nothing is looked for, read or loaded at startup. It runs once per session, on the main
    // thread, on the first Ensure(), ScheduleCull or LabCullBench while the switch is on, so by then the game's own
    // startup initialisers (Unity.Burst's and Unity.Collections' [RuntimeInitializeOnLoadMethod]s, MagicaCloth's job
    // registration, all in AfterAssembliesLoaded, which is also where BepInEx runs this plugin's Awake) have bound the
    // game's code to the game's library, and a session that never asks for Burst never loads it. Every check that can
    // refuse the pair runs before anything is loaded:
    //  - the two files are looked for next to this plugin, then in %REALPORTAL_BURST_DIR% (made absolute), then, in lab
    //    runs only (%RP_LAB_ROOT% set by the bench), in the dev build folder;
    //  - RealPortal.Burst.json must be there: its apiVersion must be this engine's ApiVersion, its Unity version the
    //    game's, the sizes of the build's Unity.Burst.dll and UnityEngine.CoreModule.dll the sizes of the game's (the
    //    Burst package and the job producers our native code was compiled against), and both files must have the
    //    sizes and SHA-256 it lists (a pair from one build);
    //  - RealPortal.Burst must not be loaded already from another file, and BurstCompiler.IsEnabled must be true.
    // Then the native library is loaded, the managed assembly after it (Burst resolves a job's native code when the
    // job is first initialised, so the library must be in before that), the assembly's [RuntimeInitializeOnLoadMethod]
    // initialisers are run by hand (Unity runs them only for assemblies it loads at startup; for our job types they
    // only create job reflection data, which IJob and IJobFor would also create on first schedule), ProbeJob runs once
    // on this thread, and once its layout matched, BurstJobs.ProbeJobs runs every real job the way it is scheduled
    // (worker threads, IJobFor's parallel producer), and last the known-answer check culls fixed sets both ways (Burst
    // and the managed twins below). Burst code is used only when the probe ran as Burst AND reports the same ApiVersion
    // and job sizes as the managed assembly AND every job ran as Burst AND the Burst cull answered exactly as the twins:
    // Burst finds a job's native code by name, so native code from another build of the jobs would read the wrong
    // memory, and a job body changed on one side only would make the switch change what is culled.
    //
    // Nothing in the game is patched. Once loaded, the library and the assembly stay loaded for the session (a native
    // library cannot be unloaded), also when a check after the load refused them; everything else is a plain field:
    //  - BurstActive = switch on && activation passed every check. Code that has a job in both forms calls Ensure()
    //    once (main thread, at a loading point: activation costs tens of ms), then schedules the Burst job only while
    //    BurstActive is true, the managed twin otherwise;
    //  - ScheduleCull is that choice for the proxy cull skeleton: RealPortal.Burst's ProxyCullJob + ProxyCompactJob
    //    with Burst, ManagedCullJob + ManagedCompactJob below without. Same float operations in the same order, each
    //    rounded to float (Burst in strict float mode; the twin casts every step, see ManagedCullJob.Distance), same
    //    output layout; activation refuses a pair whose cull does not answer the known-answer sets as the twins do.
    // Off: nothing is loaded and nothing runs, the game is untouched. Switched on later: activation at the next
    // Ensure() or ScheduleCull. Switched off after activation: BurstActive false, new culls run managed; jobs already
    // scheduled finish as they were.
    //
    // What a loaded library shares with the game: RealPortal.Burst uses no Unity.Collections type and its player build
    // has no Collections package (BurstLibraryBuild refuses one that has), so the library holds our jobs plus the
    // three helper functions every Burst build carries (BurstCompilerHelper.IsBurstEnabled, X86's CSR trampolines),
    // which the game bound to its own library at startup, long before activation. See README-burst.md.
    [EngineModule]
    public static class BurstBootstrapModule
    {
        static readonly Feature s_feature = Features.Register("BurstBootstrap", true,
            "Lets Real Portal's jobs run as Burst code (RealPortal.Burst, loaded next to the game's Burst code when a job first asks for it); off: they run as managed code and nothing is loaded.");

        public const string ManagedFile = "RealPortal.Burst.dll";
        public const string NativeFile = "RealPortal.Burst_win_x86_64.dll";
        public const string ManifestFile = "RealPortal.Burst.json";
        // RealPortal.Burst.BurstJobs.ApiVersion of the jobs this engine knows how to call.
        public const int ApiVersion = 3;
        public const string DirVariable = "REALPORTAL_BURST_DIR";
        // Development fallback, only in lab runs (the bench sets RP_LAB_ROOT and deploys only the engine dll): where
        // BurstLibraryBuild copies the files. Never looked at on a player's machine.
        public const string DevDir = @"C:\Users\Danylo\Desktop\RealPortal\Build";
        public const string LabVariable = "RP_LAB_ROOT";
        public const int DefaultBatchSize = 64;

        // Burst code may be used: the switch is on and activation passed every check. Read on hot paths.
        public static bool BurstActive;

        // Lab: what activation found (Report() puts it in one line).
        public static string Status = "not started";
        public static string FoundIn;
        // From RealPortal.Burst.json: when the pair was built, and the hashes checked against it (they pin the build in
        // the log).
        public static string Built, ManagedSha256, NativeSha256;
        public static bool LibraryLoaded, AssemblyLoaded, LayoutMatches;
        // Probe: 0 not run, 1 Burst ran it, 2 managed code ran it, -1 it failed.
        public static int ProbeMode;
        public static int[] ProbeLayout, ExpectedLayout;
        // BurstJobs.ProbeJobs(): the same modes for each job of JobNames, as scheduled.
        public static string[] JobNames;
        public static int[] JobModes;
        // BurstCompiler.IsEnabled as read during activation (read only once the files passed their checks).
        public static bool BurstCompilerChecked, BurstCompilerEnabled;
        public static int Initializers;
        public static double ActivationMs;
        // The known-answer check (KnownAnswer): culls compared (each one Burst and managed), spheres in its sets, its
        // share of ActivationMs (mostly the managed twins' first use), and its outcome.
        public static int KnownAnswerCulls, KnownAnswerSpheres;
        public static double KnownAnswerMs;
        public static string KnownAnswerResult = "not run";
        // Lab: culls scheduled each way, and spheres sent.
        public static long CullsBurst, CullsManaged, CullSpheres;

        public delegate JobHandle CullScheduler(NativeArray<float4> spheres, NativeArray<float4> planes, NativeArray<int> visible,
            NativeArray<int> batchCounts, NativeArray<int> visibleCount, int batchSize, JobHandle dependsOn);

        static CullScheduler s_burstCull;

        // RealPortal.Burst's view jobs (the plugin's proxy cull and emission as Burst code, ProxyViewJobs.cs: their
        // parameters there) and the sizes of the plugin types they mirror (BurstJobs.ViewSizes); null until activation
        // passed every check. The plugin schedules them only while BurstActive, after comparing them with its own jobs.
        public delegate JobHandle ViewCullScheduler(NativeArray<byte> page, NativeArray<byte> ranges, NativeArray<byte> templates, NativeArray<byte> lodSets,
            NativeArray<byte> variants, NativeArray<byte> rowFlags, NativeArray<Vector4> planes, NativeArray<byte> outs, NativeArray<int> room,
            int rangeBase, int rangeCount, int casterPlanes, int maxLod, uint skip, Vector4 cam, Vector4 prevCam, Vector4 light, int flags,
            JobHandle dependsOn);

        public delegate JobHandle ViewEmitScheduler(NativeArray<byte> page, NativeArray<byte> ranges, NativeArray<byte> cull, NativeArray<byte> variants,
            NativeArray<byte> variantParts, NativeArray<byte> parts, NativeArray<int> batchDraw, NativeArray<int> bases, NativeArray<int> key,
            NativeArray<Matrix4x4> m, NativeArray<Vector4> s, NativeArray<int> filled, NativeArray<int> visible, NativeArray<int> casters,
            NativeArray<int> specialCount, NativeArray<int> special, NativeArray<int> rowDrawn, int rangeBase, int rangeCount, int mask, int frame,
            bool obliqueClip, JobHandle dependsOn);

        public static ViewCullScheduler ViewCull;
        public static ViewEmitScheduler ViewEmit;
        public static int[] ViewSizes;
        // Activation ran (whatever it found), and whether it found everything usable.
        static bool s_activated, s_usable;
        static int s_mainThread;

        static void Install(Harmony harmony)
        {
            s_mainThread = Thread.CurrentThread.ManagedThreadId;
            s_feature.Switched += _ => Refresh();
            // The plugin binds the switches to its config after every Install; this one is bound now, so that an
            // Ensure() from anywhere already sees the player's setting. Install runs inside the plugin's Awake, by
            // which time BepInEx has added the plugin component to its manager object.
            ConfigFile config = Chainloader.ManagerObject != null ? Chainloader.ManagerObject.GetComponent<EnginePlugin>()?.Config : null;
            if (config != null)
                s_feature.Bind(config);
            Status = s_feature.On ? "not activated yet (on the first Ensure(), ScheduleCull or LabCullBench)" : "off (nothing loaded)";
        }

        // Activates if that has not happened yet and the switch is on (main thread only), then answers BurstActive.
        public static bool Ensure()
        {
            if (!s_activated && s_feature.On && Thread.CurrentThread.ManagedThreadId == s_mainThread)
                Activate();
            return BurstActive;
        }

        static void Refresh()
        {
            BurstActive = s_feature.On && s_usable;
            if (!s_activated)
                Status = s_feature.On ? "not activated yet (on the first Ensure(), ScheduleCull or LabCullBench)" : "off (nothing loaded)";
        }

        static void Activate()
        {
            s_activated = true;
            var sw = Stopwatch.StartNew();
            try
            {
                s_usable = Load();
            }
            catch (Exception e)
            {
                s_usable = false;
                Status = "failed: " + (e.InnerException ?? e).Message;
                EnginePlugin.Log?.LogError("BurstBootstrap: activation failed, jobs stay managed: " + (e.InnerException ?? e));
            }
            ActivationMs = sw.Elapsed.TotalMilliseconds;
            Refresh();
            if (s_usable)
                EnginePlugin.Log?.LogInfo("BurstBootstrap: " + Report());
            else
                EnginePlugin.Log?.LogWarning("BurstBootstrap: Burst not used, jobs run managed. " + Report());
        }

        static bool Load()
        {
            string dir = FindDirectory();
            if (dir == null)
            {
                Status = $"no {ManagedFile} + {NativeFile} next to the plugin or in %{DirVariable}%" +
                    (Environment.GetEnvironmentVariable(LabVariable) != null ? " or " + DevDir : "");
                return false;
            }
            FoundIn = dir;
            string managed = Path.Combine(dir, ManagedFile), native = Path.Combine(dir, NativeFile);
            Type runtime = Type.GetType("Unity.Burst.BurstRuntime, Unity.Burst");
            Type compiler = Type.GetType("Unity.Burst.BurstCompiler, Unity.Burst");
            MethodInfo loadLibrary = runtime?.GetMethod("LoadAdditionalLibrary", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            if (loadLibrary == null)
            {
                Status = "Unity.Burst.BurstRuntime.LoadAdditionalLibrary not found in the game's Unity.Burst";
                return false;
            }
            if (!CheckManifest(dir, managed, native, runtime.Assembly))
                return false;

            Assembly asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "RealPortal.Burst");
            if (asm != null && !string.Equals(Location(asm), Path.GetFullPath(managed), StringComparison.OrdinalIgnoreCase))
            {
                // Its code is not the file the manifest vouched for, so nothing pairs it with our native library.
                Status = $"RealPortal.Burst is already loaded from {Location(asm) ?? "memory"}, not from {managed}";
                return false;
            }

            BurstCompilerEnabled = compiler?.GetProperty("IsEnabled", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is bool enabled && enabled;
            BurstCompilerChecked = true;
            if (!BurstCompilerEnabled)
            {
                // Burst is off for the whole player (-burst-disable-compilation): our code would never run anyway.
                Status = "Burst is disabled in this player (BurstCompiler.IsEnabled false)";
                return false;
            }

            LibraryLoaded = loadLibrary.Invoke(null, new object[] { native }) is bool loaded && loaded;
            if (!LibraryLoaded)
            {
                Status = "BurstRuntime.LoadAdditionalLibrary returned false for " + native;
                return false;
            }

            if (asm == null)
                asm = Assembly.LoadFrom(managed);
            AssemblyLoaded = true;
            Initializers = RunInitializers(asm);

            Type jobs = asm.GetType("RealPortal.Burst.BurstJobs", true);
            object api = jobs.GetField("ApiVersion", BindingFlags.Public | BindingFlags.Static)?.GetRawConstantValue();
            if (!(api is int apiVersion) || apiVersion != ApiVersion)
            {
                Status = $"{ManagedFile} has ApiVersion {api ?? "?"}, this engine calls {ApiVersion}";
                return false;
            }

            ExpectedLayout = (int[])jobs.GetMethod("ExpectedLayout", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
            int[] probe;
            try
            {
                probe = (int[])jobs.GetMethod("Probe", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
            }
            catch
            {
                ProbeMode = -1;
                throw;
            }
            ProbeMode = probe[0];
            ProbeLayout = probe.Skip(1).ToArray();
            LayoutMatches = ProbeLayout.SequenceEqual(ExpectedLayout);
            if (ProbeMode != 1)
            {
                Status = "the probe ran as managed code: the library holds no Burst code for these jobs " +
                    "(built with another Unity or Burst version, or for another assembly)";
                return false;
            }
            if (!LayoutMatches)
            {
                Status = $"the native library was built from other job sources (layout {Layout(ProbeLayout)}, " +
                    $"the assembly has {Layout(ExpectedLayout)}): the two files are from different builds";
                return false;
            }

            // Only now, with the layout vouched for: the real jobs, scheduled as ScheduleCull schedules them.
            JobNames = (string[])jobs.GetField("JobNames", BindingFlags.Public | BindingFlags.Static).GetValue(null);
            JobModes = (int[])jobs.GetMethod("ProbeJobs", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
            for (int i = 0; i < JobNames.Length; i++)
                if (i >= JobModes.Length || JobModes[i] != 1)
                {
                    Status = $"{JobNames[i]} ran as managed code where it is scheduled although the probe ran as Burst: " +
                        "the library has no Burst code for it, or the game's job producer for it no longer matches";
                    return false;
                }

            var burstCull = (CullScheduler)Delegate.CreateDelegate(typeof(CullScheduler),
                jobs.GetMethod("ScheduleCull", BindingFlags.Public | BindingFlags.Static), true);
            // Last: the Burst cull must answer as this engine's twins. Nothing above ties the pair's job bodies to them.
            var known = Stopwatch.StartNew();
            string differs = KnownAnswer(burstCull);
            KnownAnswerMs = known.Elapsed.TotalMilliseconds;
            KnownAnswerResult = differs == null ? $"equal ({KnownAnswerCulls} culls, {KnownAnswerSpheres} spheres, {KnownAnswerMs:F1} ms)" : "DIFFER: " + differs;
            if (differs != null)
            {
                Status = $"the Burst cull disagrees with this engine's managed twin on the known-answer set ({differs}): " +
                    "the pair was built from other job bodies than this engine's twins; rebuild it from the sources this engine was built with";
                return false;
            }
            s_burstCull = burstCull;
            ViewCull = (ViewCullScheduler)Delegate.CreateDelegate(typeof(ViewCullScheduler),
                jobs.GetMethod("ScheduleViewCull", BindingFlags.Public | BindingFlags.Static), true);
            ViewEmit = (ViewEmitScheduler)Delegate.CreateDelegate(typeof(ViewEmitScheduler),
                jobs.GetMethod("ScheduleViewEmit", BindingFlags.Public | BindingFlags.Static), true);
            ViewSizes = (int[])jobs.GetMethod("ViewSizes", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
            Status = "Burst code runs";
            return true;
        }

        // Full path of the file an assembly was loaded from; null for one loaded from bytes.
        static string Location(Assembly asm)
        {
            try
            {
                return string.IsNullOrEmpty(asm.Location) ? null : Path.GetFullPath(asm.Location);
            }
            catch (Exception)
            {
                return null;
            }
        }

        static string FindDirectory()
        {
            string own = Location(typeof(BurstBootstrapModule).Assembly);
            own = own != null ? Path.GetDirectoryName(own) : null;
            string env = Environment.GetEnvironmentVariable(DirVariable);
            if (!string.IsNullOrEmpty(env))
            {
                try
                {
                    // LoadAdditionalLibrary and Assembly.LoadFrom resolve a relative path differently.
                    env = Path.GetFullPath(env);
                }
                catch (Exception e)
                {
                    EnginePlugin.Log?.LogWarning($"BurstBootstrap: %{DirVariable}% '{env}' is not a usable path ({e.Message}), ignored");
                    env = null;
                }
            }
            string dev = Environment.GetEnvironmentVariable(LabVariable) != null ? DevDir : null;
            foreach (string dir in new[] { own, env, dev })
            {
                if (string.IsNullOrEmpty(dir))
                    continue;
                bool m = File.Exists(Path.Combine(dir, ManagedFile)), n = File.Exists(Path.Combine(dir, NativeFile));
                if (m && n)
                    return dir;
                if (m || n)
                    EnginePlugin.Log?.LogWarning($"BurstBootstrap: {dir} holds only {(m ? ManagedFile : NativeFile)}; both are needed, looking further");
            }
            return null;
        }

        // RealPortal.Burst.json, written by BurstLibraryBuild next to the pair, ties the two files to each other (one
        // build) and to the runtime they were built for. Required: without it nothing shows that the native code is the
        // managed assembly's (native code from another build of the same fields passes the probe) or that the game
        // still runs the Unity and Burst they were compiled against (a game update keeps job names, and so Burst's
        // lookup hashes, while struct layouts and producers may change). It says nothing about this engine's twins:
        // whether the build's job bodies answer as they do is the known-answer check's. Everything here is checked
        // before anything is loaded.
        static bool CheckManifest(string dir, string managed, string native, Assembly burst)
        {
            string path = Path.Combine(dir, ManifestFile);
            if (!File.Exists(path))
            {
                Status = $"no {ManifestFile} next to the pair: it is what shows that the two files are one build for this game (copy all three together)";
                return false;
            }
            string json = File.ReadAllText(path);
            Built = Field(json, "built");
            string unity = Field(json, "unity"), api = Field(json, "apiVersion");
            EnginePlugin.Log?.LogInfo($"BurstBootstrap: {path}: Unity {unity}, Burst {Field(json, "burst")}, Mathematics {Field(json, "mathematics")}, " +
                $"ApiVersion {api}, built {Built}, sha256 {Field(json, "managedSha256")} / {Field(json, "nativeSha256")}; the game runs Unity {Application.unityVersion}");
            if (api != ApiVersion.ToString(CultureInfo.InvariantCulture))
            {
                Status = $"the pair is ApiVersion {(api.Length > 0 ? api : "?")}, this engine calls {ApiVersion}: rebuild it from the sources this engine was built with";
                return false;
            }
            if (unity != Application.unityVersion)
            {
                Status = $"the pair was built for Unity {(unity.Length > 0 ? unity : "?")}, the game runs {Application.unityVersion}: rebuild it in that editor";
                return false;
            }
            long burstBytes = FileBytes(Location(burst)), coreBytes = FileBytes(Location(typeof(JobHandle).Assembly));
            if (Field(json, "unityBurstBytes") != burstBytes.ToString(CultureInfo.InvariantCulture))
            {
                Status = $"the pair was built with another Burst package (its Unity.Burst.dll has {Field(json, "unityBurstBytes")} bytes, the game's {burstBytes}): rebuild it with the game's Burst";
                return false;
            }
            if (Field(json, "coreModuleBytes") != coreBytes.ToString(CultureInfo.InvariantCulture))
            {
                Status = $"the pair was built against another UnityEngine.CoreModule ({Field(json, "coreModuleBytes")} bytes, the game's {coreBytes}): rebuild it in the game's Unity";
                return false;
            }
            string wantManaged = Field(json, "managedSha256"), wantNative = Field(json, "nativeSha256");
            if (wantManaged.Length == 0 || wantNative.Length == 0 ||
                Field(json, "managedBytes") != FileBytes(managed).ToString(CultureInfo.InvariantCulture) ||
                Field(json, "nativeBytes") != FileBytes(native).ToString(CultureInfo.InvariantCulture))
            {
                Status = $"{ManagedFile} or {NativeFile} is not what {ManifestFile} lists: not one build (copy all three together)";
                return false;
            }
            ManagedSha256 = Sha256(managed);
            NativeSha256 = Sha256(native);
            if (!string.Equals(ManagedSha256, wantManaged, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(NativeSha256, wantNative, StringComparison.OrdinalIgnoreCase))
            {
                Status = $"{ManagedFile} or {NativeFile} differs from what {ManifestFile} lists: not one build (copy all three together)";
                return false;
            }
            return true;
        }

        // The value of "name" in the flat JSON BurstLibraryBuild writes (one field per line, strings without escapes):
        // a string's contents or a number's text, "" when absent.
        static string Field(string json, string name)
        {
            string key = "\"" + name + "\"";
            int at = json.IndexOf(key, StringComparison.Ordinal);
            if (at < 0)
                return "";
            int i = json.IndexOf(':', at + key.Length);
            if (i < 0)
                return "";
            i++;
            while (i < json.Length && char.IsWhiteSpace(json[i]))
                i++;
            if (i < json.Length && json[i] == '"')
            {
                int close = json.IndexOf('"', i + 1);
                return close < 0 ? "" : json.Substring(i + 1, close - i - 1);
            }
            int end = i;
            while (end < json.Length && json[end] != ',' && json[end] != '}' && !char.IsWhiteSpace(json[end]))
                end++;
            return json.Substring(i, end - i);
        }

        static long FileBytes(string path)
        {
            try
            {
                return path != null && File.Exists(path) ? new FileInfo(path).Length : -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        static string Sha256(string path)
        {
            // SHA256Managed directly: SHA256.Create() goes through CryptoConfig's name lookup first.
            using (var sha = new SHA256Managed())
            using (FileStream f = File.OpenRead(path))
                return string.Concat(sha.ComputeHash(f).Select(b => b.ToString("x2")));
        }

        // What Unity would have run at startup for an assembly it loaded itself: job reflection registration
        // (__JobReflectionRegistrationOutput__*.EarlyInit) and Burst direct-call setup ($BurstDirectCallInitializer).
        static int RunInitializers(Assembly asm)
        {
            int ran = 0;
            Type[] types;
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types.Where(t => t != null).ToArray();
            }
            foreach (Type t in types)
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (m.GetParameters().Length != 0 || m.GetCustomAttributes(typeof(RuntimeInitializeOnLoadMethodAttribute), false).Length == 0)
                        continue;
                    try
                    {
                        m.Invoke(null, null);
                        ran++;
                    }
                    catch (Exception e)
                    {
                        // Lazy initialisation on first schedule covers what this would have set up.
                        EnginePlugin.Log?.LogWarning($"BurstBootstrap: initialiser {t.FullName}.{m.Name} threw: {(e.InnerException ?? e).Message}");
                    }
                }
            return ran;
        }

        public static string Report() =>
            $"{(BurstActive ? "active" : "inactive")} ({Status}); switch {(s_feature.On ? "on" : "off")}, " +
            $"files {(FoundIn ?? "not found")}, built {Built ?? "-"}, sha256 {Short(ManagedSha256)} / {Short(NativeSha256)}, " +
            $"BurstCompiler.IsEnabled {(BurstCompilerChecked ? BurstCompilerEnabled.ToString() : "not read")}, " +
            $"library {(LibraryLoaded ? "loaded" : "not loaded")}, " +
            $"assembly {(AssemblyLoaded ? "loaded" : "not loaded")}, initialisers {Initializers}, probe " +
            $"{Mode(ProbeMode)}, layout {Layout(ProbeLayout)} vs {Layout(ExpectedLayout)}, jobs {Jobs()}, " +
            $"known answer {KnownAnswerResult}, {ActivationMs:F1} ms; culls Burst {CullsBurst}, managed {CullsManaged}, spheres {CullSpheres}";

        static string Mode(int mode) => mode == 1 ? "Burst" : mode == 2 ? "managed" : mode == 0 ? "not run" : mode == -1 ? "failed" : "unknown " + mode;

        static string Jobs() => JobNames == null || JobModes == null ? "not run" :
            string.Join(", ", JobNames.Select((name, i) => name + " " + Mode(i < JobModes.Length ? JobModes[i] : 0)));

        static string Short(string sha) => sha == null ? "-" : sha.Substring(0, Math.Min(16, sha.Length));

        // Probe slots without the unused zeros at the end.
        static string Layout(int[] slots) => slots == null ? "-" : string.Join("/", slots.Reverse().SkipWhile(v => v == 0).Reverse());

        // ---- the proxy cull skeleton (ARCHITECTURE-v2 §3.7 step 2) ----------------------------------------------

        // ceil(length / batchSize) without overflow; batchSize > 0.
        public static int BatchCount(int length, int batchSize) => length / batchSize + (length % batchSize != 0 ? 1 : 0);

        // Planes as the cull takes them: (normal, distance), normals pointing inside (GeometryUtility.CalculateFrustumPlanes).
        public static void WritePlanes(Plane[] planes, NativeArray<float4> into)
        {
            if (planes == null || planes.Length != 6 || into.Length != 6)
                throw new ArgumentException("6 planes into 6 slots");
            for (int i = 0; i < 6; i++)
                into[i] = new float4(planes[i].normal.x, planes[i].normal.y, planes[i].normal.z, planes[i].distance);
        }

        // Frustum cull of bounding spheres (xyz centre, w radius): after the returned handle completes,
        // visible[0 .. visibleCount[0]) holds the index of every sphere with dot(n, c) + d >= -r for all six planes,
        // ascending. visible: spheres.Length (or more); batchCounts: BatchCount(spheres.Length, batchSize);
        // visibleCount: 1. The caller owns the arrays and must not touch them before completion. Main thread only.
        // Burst while BurstActive, the managed twin otherwise; both give the same indices. The jobs are handed to the
        // worker threads at once (JobHandle.ScheduleBatchedJobs), so they run while the caller does other work.
        public static JobHandle ScheduleCull(NativeArray<float4> spheres, NativeArray<float4> planes, NativeArray<int> visible,
            NativeArray<int> batchCounts, NativeArray<int> visibleCount, int batchSize = DefaultBatchSize, JobHandle dependsOn = default)
        {
            CheckCullArguments(spheres.Length, planes.Length, visible.Length, batchCounts.Length, visibleCount.Length, batchSize);
            // Activation, if no one called Ensure() earlier (it then costs this frame its tens of ms).
            if (!s_activated)
                Ensure();
            CullSpheres += spheres.Length;
            CullScheduler burst = s_burstCull;
            JobHandle handle;
            if (BurstActive && burst != null)
            {
                CullsBurst++;
                handle = burst(spheres, planes, visible, batchCounts, visibleCount, batchSize, dependsOn);
            }
            else
            {
                CullsManaged++;
                handle = ScheduleManagedCull(spheres, planes, visible, batchCounts, visibleCount, batchSize, dependsOn);
            }
            JobHandle.ScheduleBatchedJobs();
            return handle;
        }

        static JobHandle ScheduleManagedCull(NativeArray<float4> spheres, NativeArray<float4> planes, NativeArray<int> visible,
            NativeArray<int> batchCounts, NativeArray<int> visibleCount, int batchSize, JobHandle dependsOn)
        {
            var cull = new ManagedCullJob
            {
                Spheres = spheres,
                Planes = planes,
                Visible = visible,
                BatchCounts = batchCounts,
                BatchSize = batchSize,
            };
            JobHandle culled = cull.ScheduleParallel(batchCounts.Length, 1, dependsOn);
            var compact = new ManagedCompactJob
            {
                Visible = visible,
                BatchCounts = batchCounts,
                BatchSize = batchSize,
                VisibleCount = visibleCount,
            };
            return compact.Schedule(culled);
        }

        // Lab: the same random spheres (1.2 km square around a 60° frustum) culled reps times by each path there is,
        // each cull scheduled and completed on the spot; answers the mean ms of each and whether their indices agree.
        // Main thread. Activates like Ensure(); runs Burst whenever activation succeeded, whatever the switch says;
        // not in the counters.
        public static string LabCullBench(int count = 20000, int reps = 20, int batchSize = DefaultBatchSize)
        {
            if (Thread.CurrentThread.ManagedThreadId != s_mainThread)
                return "main thread only";
            if (count < 0 || reps <= 0 || batchSize <= 0)
                return "count >= 0, reps > 0, batchSize > 0";
            Ensure();
            var frustum = new Plane[6];
            Matrix4x4 view = Matrix4x4.TRS(new Vector3(0f, 10f, 0f), Quaternion.Euler(10f, 30f, 0f), Vector3.one).inverse;
            GeometryUtility.CalculateFrustumPlanes(Matrix4x4.Perspective(60f, 16f / 9f, 0.3f, 500f) * view, frustum);
            var rng = new System.Random(12345);
            NativeArray<float4> spheres = default, planes = default;
            NativeArray<int> visible = default, counts = default, total = default;
            try
            {
                spheres = new NativeArray<float4>(count, Allocator.Persistent);
                planes = new NativeArray<float4>(6, Allocator.Persistent);
                visible = new NativeArray<int>(count, Allocator.Persistent);
                counts = new NativeArray<int>(BatchCount(count, batchSize), Allocator.Persistent);
                total = new NativeArray<int>(1, Allocator.Persistent);
                WritePlanes(frustum, planes);
                for (int i = 0; i < count; i++)
                    spheres[i] = new float4((float)(rng.NextDouble() * 1200 - 600), (float)(rng.NextDouble() * 200 - 50),
                        (float)(rng.NextDouble() * 1200 - 600), (float)(rng.NextDouble() * 20));
                double Measure(CullScheduler schedule, out int[] result)
                {
                    // The first one also creates the job's data (and finds its Burst code).
                    schedule(spheres, planes, visible, counts, total, batchSize, default).Complete();
                    var sw = Stopwatch.StartNew();
                    for (int r = 0; r < reps; r++)
                        schedule(spheres, planes, visible, counts, total, batchSize, default).Complete();
                    double ms = sw.Elapsed.TotalMilliseconds / reps;
                    result = visible.GetSubArray(0, total[0]).ToArray();
                    return ms;
                }
                double managedMs = Measure(ScheduleManagedCull, out int[] managed);
                string text = $"cull bench {count} spheres, batch {batchSize}, {reps} reps: managed {managedMs:F3} ms ({managed.Length} visible)";
                if (s_usable && s_burstCull != null)
                {
                    double burstMs = Measure(s_burstCull, out int[] burst);
                    text += $", Burst {burstMs:F3} ms ({burst.Length} visible), {managedMs / Math.Max(burstMs, 1e-6):F1}x, " +
                        $"indices {(burst.SequenceEqual(managed) ? "equal" : "DIFFER")}";
                }
                else
                    text += ", no Burst: " + Status;
                return text;
            }
            finally
            {
                if (spheres.IsCreated)
                    spheres.Dispose();
                if (planes.IsCreated)
                    planes.Dispose();
                if (visible.IsCreated)
                    visible.Dispose();
                if (counts.IsCreated)
                    counts.Dispose();
                if (total.IsCreated)
                    total.Dispose();
            }
        }

        // Same checks as RealPortal.Burst.BurstJobs.CheckCullArguments.
        static void CheckCullArguments(int spheres, int planes, int visible, int batchCounts, int visibleCount, int batchSize)
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

        // Known answer, the last check of activation: the Burst cull and the managed twins cull the same fixed sets, each
        // in batches of 1, 7 and 64, and must return the same visible indices. The manifest only shows that the two files
        // are one build, and the probe and ProbeJobs look at sizes and at which code ran: nothing else ties that build's
        // job bodies to the twins of this engine, and a body changed on one side only (one more test, <= for <) would
        // make the switch change what is culled. The sets:
        //  - a fixed frustum with seeded random spheres, and spheres outside exactly one of its planes whose radius is
        //    the centre's distance to it (tangent: visible), one float step less (culled) and one more (visible), so
        //    the comparison itself decides them;
        //  - a plane set on which a distance rounded to float and one kept in double give opposite answers (see
        //    ManagedCullJob.Distance).
        // Null when every cull agreed, else the first difference. Main thread; a fraction of a millisecond each way.
        static readonly int[] s_knownAnswerBatches = { 1, 7, 64 };
        const int KnownAnswerSeed = 24601, KnownAnswerRandom = 201, KnownAnswerTangent = 100;

        static string KnownAnswer(CullScheduler burst)
        {
            KnownAnswerCulls = KnownAnswerSpheres = 0;
            var frustum = new Plane[6];
            Matrix4x4 view = Matrix4x4.TRS(new Vector3(0f, 10f, 0f), Quaternion.Euler(10f, 75f, 0f), Vector3.one).inverse;
            GeometryUtility.CalculateFrustumPlanes(Matrix4x4.Perspective(60f, 16f / 9f, 0.3f, 500f) * view, frustum);
            var framed = new float4[6];
            for (int i = 0; i < 6; i++)
                framed[i] = new float4(frustum[i].normal.x, frustum[i].normal.y, frustum[i].normal.z, frustum[i].distance);
            var rng = new System.Random(KnownAnswerSeed);
            var spheres = new float4[KnownAnswerRandom + 3 * KnownAnswerTangent];
            int n = 0;
            for (int i = 0; i < KnownAnswerRandom; i++)
                spheres[n++] = new float4((float)(rng.NextDouble() * 1200 - 600), (float)(rng.NextDouble() * 200 - 50),
                    (float)(rng.NextDouble() * 1200 - 600), (float)(rng.NextDouble() * 20));
            for (int tries = 0; n < spheres.Length && tries < 100000; tries++)
            {
                var c = new float4((float)(rng.NextDouble() * 1200 - 600), (float)(rng.NextDouble() * 200 - 50),
                    (float)(rng.NextDouble() * 1200 - 600), 0f);
                int outside = -1, outsideCount = 0;
                for (int k = 0; k < 6; k++)
                    if (ManagedCullJob.Distance(framed[k], c) < 0f)
                    {
                        outside = k;
                        outsideCount++;
                    }
                if (outsideCount != 1)
                    continue;
                // Exact negation, so Distance == -radius in the job; well clear of denormals.
                float radius = -ManagedCullJob.Distance(framed[outside], c);
                if (radius < 1e-3f)
                    continue;
                spheres[n++] = new float4(c.xyz, radius);
                spheres[n++] = new float4(c.xyz, math.asfloat(math.asint(radius) - 1));
                spheres[n++] = new float4(c.xyz, math.asfloat(math.asint(radius) + 1));
            }
            string differs = KnownAnswerSet(burst, "frustum", framed, spheres, n);
            if (differs != null)
                return differs;

            // Plane 0: -a*a rounds to -w in float, so the distance is 0 (visible) where double has -2^-24 (culled).
            // Plane 1: 1 + t*t + t*t rounds to 1 in float, so the distance is -2^-23 (culled) where double has 0
            // (visible). Planes 2-5 pass everything. a = 1+2^-12, w = 1+2^-11, t = 2^-12, u = 1+2^-23, all exact.
            const float a = 1f + 1f / 4096f, w = 1f + 1f / 2048f, t = 1f / 4096f, u = 1f + 1f / 8388608f;
            float4 pass = new float4(0f, 0f, 0f, 1f);
            float4[] rounding = { new float4(-a, 0f, 0f, w), new float4(1f, t, t, -u), pass, pass, pass, pass };
            float4[] edge = new float4[9];
            for (int i = 0; i < edge.Length; i += 3)
            {
                edge[i] = new float4(a, 0f, 0f, 0f);
                edge[i + 1] = new float4(1f, t, t, 0f);
                edge[i + 2] = new float4(0f, 0f, 0f, 2f);
            }
            return KnownAnswerSet(burst, "rounding", rounding, edge, edge.Length);
        }

        static string KnownAnswerSet(CullScheduler burst, string name, float4[] planeValues, float4[] sphereValues, int count)
        {
            NativeArray<float4> spheres = default, planes = default;
            NativeArray<int> visibleBurst = default, visibleManaged = default, countsBurst = default, countsManaged = default,
                totalBurst = default, totalManaged = default;
            try
            {
                spheres = new NativeArray<float4>(count, Allocator.TempJob);
                planes = new NativeArray<float4>(6, Allocator.TempJob);
                visibleBurst = new NativeArray<int>(count, Allocator.TempJob);
                visibleManaged = new NativeArray<int>(count, Allocator.TempJob);
                // Batches of 1 need the most counts; each cull takes the front it needs.
                countsBurst = new NativeArray<int>(count, Allocator.TempJob);
                countsManaged = new NativeArray<int>(count, Allocator.TempJob);
                totalBurst = new NativeArray<int>(1, Allocator.TempJob);
                totalManaged = new NativeArray<int>(1, Allocator.TempJob);
                for (int i = 0; i < count; i++)
                    spheres[i] = sphereValues[i];
                for (int i = 0; i < 6; i++)
                    planes[i] = planeValues[i];
                KnownAnswerSpheres += count;
                foreach (int batch in s_knownAnswerBatches)
                {
                    int batches = BatchCount(count, batch);
                    burst(spheres, planes, visibleBurst, countsBurst.GetSubArray(0, batches), totalBurst, batch, default).Complete();
                    ScheduleManagedCull(spheres, planes, visibleManaged, countsManaged.GetSubArray(0, batches), totalManaged, batch, default).Complete();
                    KnownAnswerCulls++;
                    if (totalBurst[0] != totalManaged[0])
                        return $"{name} set, batch {batch}: Burst {totalBurst[0]} visible, managed {totalManaged[0]}";
                    for (int i = 0; i < totalBurst[0]; i++)
                        if (visibleBurst[i] != visibleManaged[i])
                            return $"{name} set, batch {batch}: visible[{i}] is sphere {visibleBurst[i]} with Burst, {visibleManaged[i]} managed";
                }
                return null;
            }
            finally
            {
                if (spheres.IsCreated)
                    spheres.Dispose();
                if (planes.IsCreated)
                    planes.Dispose();
                if (visibleBurst.IsCreated)
                    visibleBurst.Dispose();
                if (visibleManaged.IsCreated)
                    visibleManaged.Dispose();
                if (countsBurst.IsCreated)
                    countsBurst.Dispose();
                if (countsManaged.IsCreated)
                    countsManaged.Dispose();
                if (totalBurst.IsCreated)
                    totalBurst.Dispose();
                if (totalManaged.IsCreated)
                    totalManaged.Dispose();
            }
        }

        // Managed twins of RealPortal.Burst.ProxyCullJob / ProxyCompactJob: keep the code identical (operation order
        // and the casts in Distance included), so Burst on and off give the same indices; the known-answer check at
        // activation refuses a pair whose cull does not. They leave out only the Burst jobs' probe branch
        // (BatchSize <= 0), which a cull never takes.
        struct ManagedCullJob : IJobFor
        {
            [ReadOnly] public NativeArray<float4> Spheres;
            [ReadOnly] public NativeArray<float4> Planes;
            [NativeDisableParallelForRestriction] public NativeArray<int> Visible;
            public NativeArray<int> BatchCounts;
            public int BatchSize;

            public void Execute(int batch)
            {
                float4 p0 = Planes[0], p1 = Planes[1], p2 = Planes[2], p3 = Planes[3], p4 = Planes[4], p5 = Planes[5];
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

            // Rounded to float after every operation, as Burst computes it (strict float mode). Unity's Mono keeps float
            // arithmetic in double unless each result is cast back (conv.r4), and then a sphere within a float step of
            // a plane could be culled one way and not the other (README-burst.md, «Плавающая точка»). Rounding the exact
            // double product or sum to float gives the float result (53 >= 2*24 + 2).
            internal static float Distance(float4 p, float4 s) =>
                (float)((float)((float)((float)(p.x * s.x) + (float)(p.y * s.y)) + (float)(p.z * s.z)) + p.w);
        }

        struct ManagedCompactJob : IJob
        {
            public NativeArray<int> Visible;
            [ReadOnly] public NativeArray<int> BatchCounts;
            public NativeArray<int> VisibleCount;
            public int BatchSize;

            public void Execute()
            {
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
        }
    }
}
