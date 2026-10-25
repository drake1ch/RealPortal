using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RealPortal.Burst;
using RealPortal.Burst.SelfTest;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RealPortal.EditorTools
{
    // Builds the Burst jobs of Assets/RealPortal.Burst into the two files the engine's BurstBootstrap module loads
    // (Burst's modding route: a player build, then its managed dll and lib_burst_generated.dll side by side).
    //  1. self-test in the editor (RealPortal.Burst.SelfTest): Burst compilation must be on, the probe and every job
    //     must run as Burst code, and the cull must equal a plain C# reference; any failure fails the build;
    //  2. a Windows x64 Mono player, not development, with an empty scene, into Unity\BurstBuild; it must not contain
    //     the Unity.Collections package (the native library would then carry the game's Collections code too);
    //  3. the same self-test inside that player (RealPortalBurst.exe -batchmode -nographics), so the AOT native library
    //     that ships is what is tested, not only the editor's JIT code;
    //  4. copies <build>\*_Data\Managed\RealPortal.Burst.dll and <build>\*_Data\Plugins\x86_64\lib_burst_generated.dll
    //     to Build\RealPortal.Burst.dll and Build\RealPortal.Burst_win_x86_64.dll, and writes Build\RealPortal.Burst.json
    //     (versions, the sizes of the player's Unity.Burst.dll and UnityEngine.CoreModule.dll, sizes and SHA-256 of
    //     both files), which the engine requires and checks before it loads anything.
    // Player settings it changes for the build (scripting backend, managed stripping) are put back afterwards.
    // Batch mode: Unity.exe -batchmode -quit -projectPath Unity -executeMethod RealPortal.EditorTools.BurstLibraryBuild.Build
    // (exit code 0 on success, 1 otherwise).
    public static class BurstLibraryBuild
    {
        public const string ManagedName = "RealPortal.Burst.dll";
        public const string NativeName = "RealPortal.Burst_win_x86_64.dll";
        public const string ManifestName = "RealPortal.Burst.json";
        const string ScenePath = "Assets/RealPortal.Burst/BurstBuild.unity";
        const string PlayerName = "RealPortalBurst";
        const int PlayerTestTimeoutMs = 120000;

        static string ProjectDir => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        static string PlayerDir => Path.Combine(ProjectDir, "BurstBuild");
        static string OutDir => Path.GetFullPath(Path.Combine(ProjectDir, "..", "Build"));

        static void Log(string message) => Debug.Log("[BurstLibraryBuild] " + message);
        static void Fail(string message) => Debug.LogError("[BurstLibraryBuild] " + message);

        [MenuItem("Real Portal/Build Burst library")]
        public static void Build()
        {
            bool ok = false;
            try
            {
                ok = Run();
            }
            catch (Exception e)
            {
                Fail(e.ToString());
            }
            Log(ok ? "RESULT: ok" : "RESULT: failed");
            if (Application.isBatchMode)
                EditorApplication.Exit(ok ? 0 : 1);
        }

        static bool Run()
        {
            string versions = Versions();
            Log(versions);
            if (!SelfTest())
                return false;

            if (!EnsureScene())
                return false;
            NamedBuildTarget standalone = NamedBuildTarget.Standalone;
            ScriptingImplementation backend = PlayerSettings.GetScriptingBackend(standalone);
            ManagedStrippingLevel stripping = PlayerSettings.GetManagedStrippingLevel(standalone);
            string exe = Path.Combine(PlayerDir, PlayerName + ".exe");
            BuildReport report;
            try
            {
                // Valheim is a Mono player: the managed dll must be plain IL, and nothing in it may be stripped (no
                // scene references it). Set only when different, so a project already there keeps its settings file.
                if (backend != ScriptingImplementation.Mono2x)
                    PlayerSettings.SetScriptingBackend(standalone, ScriptingImplementation.Mono2x);
                if (stripping != ManagedStrippingLevel.Disabled)
                    PlayerSettings.SetManagedStrippingLevel(standalone, ManagedStrippingLevel.Disabled);
                if (Directory.Exists(PlayerDir))
                    FileUtil.DeleteFileOrDirectory(PlayerDir);
                Directory.CreateDirectory(PlayerDir);
                var options = new BuildPlayerOptions
                {
                    scenes = new[] { ScenePath },
                    locationPathName = exe,
                    target = BuildTarget.StandaloneWindows64,
                    targetGroup = BuildTargetGroup.Standalone,
                    options = BuildOptions.None,
                };
                Log($"building the player into {PlayerDir}");
                report = BuildPipeline.BuildPlayer(options);
            }
            finally
            {
                if (PlayerSettings.GetScriptingBackend(standalone) != backend)
                    PlayerSettings.SetScriptingBackend(standalone, backend);
                if (PlayerSettings.GetManagedStrippingLevel(standalone) != stripping)
                    PlayerSettings.SetManagedStrippingLevel(standalone, stripping);
            }

            BuildSummary summary = report.summary;
            Log($"player build: {summary.result}, {summary.totalErrors} error(s), {summary.totalWarnings} warning(s), {summary.totalTime.TotalSeconds:F0} s");
            if (summary.result != BuildResult.Succeeded)
            {
                foreach (BuildStep step in report.steps)
                    foreach (BuildStepMessage m in step.messages)
                        if (m.type == LogType.Error || m.type == LogType.Exception)
                            Fail($"{step.name}: {m.content}");
                return false;
            }

            string dataDir = Directory.GetDirectories(PlayerDir, "*_Data").SingleOrDefault();
            if (dataDir == null)
            {
                Fail($"no *_Data folder in {PlayerDir}");
                return false;
            }
            string managedDir = Path.Combine(dataDir, "Managed");
            string managed = Path.Combine(managedDir, ManagedName);
            string native = Path.Combine(dataDir, "Plugins", "x86_64", "lib_burst_generated.dll");
            string playerBurst = Path.Combine(managedDir, "Unity.Burst.dll"), playerCore = Path.Combine(managedDir, "UnityEngine.CoreModule.dll");
            if (!File.Exists(managed))
            {
                Fail($"{managed} was not built (is the RealPortal.Burst assembly in the project?)");
                return false;
            }
            if (!File.Exists(native))
            {
                Fail($"{native} was not built: Burst AOT compiled nothing (Burst AOT settings for Windows, " +
                    "Enable Burst Compilation; see Burst warnings above)");
                return false;
            }
            if (!File.Exists(playerBurst) || !File.Exists(playerCore))
            {
                Fail($"{playerBurst} or {playerCore} is missing from the player build");
                return false;
            }
            if (File.Exists(Path.Combine(managedDir, "Unity.Collections.dll")))
            {
                // Burst compiles every [BurstCompile] job and function of the build into the one library: with the
                // package in, the library would also serve the game's own Collections code (README-burst.md, risk 1).
                Fail("the player build contains Unity.Collections (com.unity.collections is in Packages\\manifest.json " +
                    "or pulled in by another package): remove it, RealPortal.Burst must not need it");
                return false;
            }
            if (!PlayerSelfTest(exe))
                return false;

            Directory.CreateDirectory(OutDir);
            string managedOut = Path.Combine(OutDir, ManagedName);
            string nativeOut = Path.Combine(OutDir, NativeName);
            try
            {
                File.Copy(managed, managedOut, true);
                File.Copy(native, nativeOut, true);
            }
            catch (IOException e)
            {
                // The game (or the lab) holds them while it runs: both are loaded for the session.
                Fail($"could not copy into {OutDir} (is the game running with them loaded?): {e.Message}");
                return false;
            }
            string managedHash = Sha256(managedOut), nativeHash = Sha256(nativeOut);
            File.WriteAllText(Path.Combine(OutDir, ManifestName),
                Manifest(managedOut, nativeOut, managedHash, nativeHash, new FileInfo(playerBurst).Length, new FileInfo(playerCore).Length),
                new UTF8Encoding(false));
            Log($"{managedOut}: {new FileInfo(managedOut).Length} bytes, sha256 {managedHash}");
            Log($"{nativeOut}: {new FileInfo(nativeOut).Length} bytes, sha256 {nativeHash}");
            Log($"{Path.Combine(OutDir, ManifestName)} written");
            return true;
        }

        // ---- versions -----------------------------------------------------------------------------------

        static string PackageVersion(string name) =>
            UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages().FirstOrDefault(p => p.name == name)?.version ?? "missing";

        static string Versions()
        {
            var sb = new StringBuilder();
            sb.Append($"Unity {Application.unityVersion}; com.unity.burst {PackageVersion("com.unity.burst")}, ");
            sb.Append($"com.unity.mathematics {PackageVersion("com.unity.mathematics")}, ");
            sb.Append($"com.unity.collections {PackageVersion("com.unity.collections")} (must be missing); RealPortal.Burst ApiVersion {BurstJobs.ApiVersion}");
            string aot = Path.Combine(ProjectDir, "ProjectSettings", "BurstAotSettings_StandaloneWindows.json");
            sb.Append(File.Exists(aot) ? "; AOT settings: " + File.ReadAllText(aot).Replace("\r", "").Replace("\n", " ") : "; AOT settings: defaults");
            return sb.ToString();
        }

        // Flat JSON the engine reads field by field (BurstBootstrapModule.CheckManifest): keep every value on its own
        // line, strings without quotes or backslashes in them.
        static string Manifest(string managed, string native, string managedHash, string nativeHash, long burstBytes, long coreBytes)
        {
            string Q(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            string N(long v) => v.ToString(CultureInfo.InvariantCulture);
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append($"  \"unity\": {Q(Application.unityVersion)},\n");
            sb.Append($"  \"burst\": {Q(PackageVersion("com.unity.burst"))},\n");
            sb.Append($"  \"mathematics\": {Q(PackageVersion("com.unity.mathematics"))},\n");
            sb.Append($"  \"apiVersion\": {N(BurstJobs.ApiVersion)},\n");
            sb.Append($"  \"built\": {Q(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))},\n");
            // The game's own copies of these must have the same sizes: Unity.Burst.dll has no version in it, and the
            // two differ between builds only in their MVIDs.
            sb.Append($"  \"unityBurstBytes\": {N(burstBytes)},\n");
            sb.Append($"  \"coreModuleBytes\": {N(coreBytes)},\n");
            sb.Append($"  \"managedBytes\": {N(new FileInfo(managed).Length)},\n");
            sb.Append($"  \"nativeBytes\": {N(new FileInfo(native).Length)},\n");
            sb.Append($"  \"managedSha256\": {Q(managedHash)},\n");
            sb.Append($"  \"nativeSha256\": {Q(nativeHash)}\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (FileStream f = File.OpenRead(path))
                return string.Concat(sha.ComputeHash(f).Select(b => b.ToString("x2")));
        }

        // The build scene: created once when missing. NewScene(Single) replaces the open scenes, so the user saves
        // them first (cancel stops the build) and they are opened again afterwards.
        static bool EnsureScene()
        {
            if (File.Exists(Path.Combine(ProjectDir, ScenePath)))
                return true;
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                Fail($"cancelled: {ScenePath} has to be created, and the open scenes were not saved");
                return false;
            }
            SceneSetup[] open = EditorSceneManager.GetSceneManagerSetup();
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            bool saved = EditorSceneManager.SaveScene(scene, ScenePath);
            // Scenes never saved (untitled) cannot be reopened; the user chose not to save them.
            if (open.Length > 0 && open.All(s => !string.IsNullOrEmpty(s.path)))
                EditorSceneManager.RestoreSceneManagerSetup(open);
            if (!saved)
                throw new IOException("could not save " + ScenePath);
            AssetDatabase.Refresh();
            return true;
        }

        // ---- self-tests -----------------------------------------------------------------------------------

        static void Report(string prefix, string line)
        {
            if (line.StartsWith(BurstSelfTest.FailPrefix, StringComparison.Ordinal))
                Fail(prefix + line.Substring(BurstSelfTest.FailPrefix.Length));
            else
                Log(prefix + line);
        }

        // In the editor (Burst compiles the jobs here just in time).
        static bool SelfTest()
        {
            int failures = BurstSelfTest.Run(line => Report("editor ", line));
            if (failures > 0)
                Fail($"editor self-test: {failures} check(s) failed");
            return failures == 0;
        }

        // In the built player: the native library that will ship, against the same reference.
        static bool PlayerSelfTest(string exe)
        {
            string result = Path.Combine(PlayerDir, "selftest.txt"), log = Path.Combine(PlayerDir, "selftest.log");
            if (File.Exists(result))
                File.Delete(result);
            var start = new System.Diagnostics.ProcessStartInfo(exe,
                $"-batchmode -nographics {BurstSelfTest.Argument} \"{result}\" -logFile \"{log}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = PlayerDir,
            };
            int exit;
            using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(start))
            {
                if (!p.WaitForExit(PlayerTestTimeoutMs))
                {
                    try
                    {
                        p.Kill();
                    }
                    catch (Exception)
                    {
                        // It may have exited meanwhile.
                    }
                    Fail($"player self-test: no answer in {PlayerTestTimeoutMs / 1000} s (see {log})");
                    return false;
                }
                exit = p.ExitCode;
            }
            if (!File.Exists(result))
            {
                Fail($"player self-test: {exe} exited with {exit} and wrote no {result} (see {log})");
                return false;
            }
            string[] lines = File.ReadAllLines(result);
            foreach (string line in lines.Skip(1))
                if (line.Length > 0)
                    Report("player ", line);
            bool ok = lines.Length > 0 && lines[0] == "ok" && exit == 0;
            if (!ok)
                Fail($"player self-test failed (exit code {exit}; {log})");
            return ok;
        }
    }
}
