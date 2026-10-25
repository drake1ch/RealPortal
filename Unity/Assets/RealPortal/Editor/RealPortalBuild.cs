using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace RealPortal.EditorTools
{
    // Packs the window shaders into realportal.bundle for the BepInEx plugin.
    // Batch mode: Unity.exe -batchmode -quit -projectPath Unity -executeMethod RealPortal.EditorTools.RealPortalBuild.Build
    public static class RealPortalBuild
    {
        public const string BundleName = "realportal.bundle";
        public const string ShaderPath = "Assets/RealPortal/Shaders/PortalWindow.shader";
        // Window Pipeline v2 (S3): the depth reprojection of cached views (Win.Resolve) and the occlusion counts and
        // depth statistics (Win.Occlusion). The plugin loads them by file name (WindowAssets.ResolveAsset/CountAsset);
        // a bundle without them leaves those switches off.
        public const string ResolveShaderPath = "Assets/RealPortal/Shaders/WindowResolve.shader";
        public const string CountComputePath = "Assets/RealPortal/Shaders/WindowCount.compute";
        // FARWORLD-V3 V1 (Win.Virtual): the window screen's helper passes, loaded by file name ("WindowScreen"); a bundle
        // without it leaves every window on its camera.
        public const string ScreenShaderPath = "Assets/RealPortal/Shaders/WindowScreen.shader";

        public static string BuildDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "Build"));

        [MenuItem("Real Portal/Build bundle")]
        public static void Build()
        {
            // Compile the shaders (and the compute shader) for every API Valheim can run on (-force-vulkan, -force-d3d12).
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64,
                new[] { GraphicsDeviceType.Direct3D11, GraphicsDeviceType.Direct3D12, GraphicsDeviceType.Vulkan });

            string[] assets = { ShaderPath, ResolveShaderPath, CountComputePath, ScreenShaderPath };
            // A shader that does not compile still goes into the bundle (as an error shader the plugin cannot use):
            // say so here rather than in the game.
            foreach (string path in assets)
            {
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
                if (shader != null && ShaderUtil.ShaderHasError(shader))
                    Debug.LogError($"[RealPortal] {path} has compile errors");
                var compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
                if (compute != null && ShaderUtil.GetComputeShaderMessageCount(compute) > 0)
                    foreach (ShaderMessage m in ShaderUtil.GetComputeShaderMessages(compute))
                        if (m.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error)
                            Debug.LogError($"[RealPortal] {path}: {m.message} ({m.platform}, line {m.line})");
            }

            Directory.CreateDirectory(BuildDir);
            var build = new AssetBundleBuild { assetBundleName = BundleName, assetNames = assets };
            BuildPipeline.BuildAssetBundles(BuildDir, new[] { build },
                BuildAssetBundleOptions.ForceRebuildAssetBundle, BuildTarget.StandaloneWindows64);
            Debug.Log($"[RealPortal] Bundle built: {Path.Combine(BuildDir, BundleName)}");
        }
    }
}
