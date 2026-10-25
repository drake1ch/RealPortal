using System;
using UnityEngine;

namespace RealPortal
{
    // OWNER: gpu (S3). The window pipeline's GPU assets from realportal.bundle: WindowAssets.ResolveAsset (Shader
    // "Hidden/RealPortal/WindowResolve") and WindowAssets.CountAsset (ComputeShader WindowCount), loaded as
    // Plugin.LoadMaterial loads PortalWindow. A missing or unsupported one (an old bundle, a device without compute) stays
    // null and is logged once; the parts that need it then report Ready = false and their mechanisms stay off
    // (Docs\S2S3-CONTRACTS.md §8.4). The S3 passes of PortalWindow.shader itself are checked by WindowComposite (by pass
    // name), so a bundle built before them leaves Win.CompositeMRT, Win.Occlusion and Win.RestoreFromWindow off too.
    internal static class WindowAssetLoader
    {
        internal static WindowAssets Load(AssetBundle bundle, Material windowMaterial)
        {
            var assets = new WindowAssets
            {
                Window = windowMaterial != null ? windowMaterial.shader : null,
            };
            if (bundle == null)
            {
                Plugin.Log.LogWarning("Window pipeline: no realportal.bundle loaded; Win.Resolve and Win.Occlusion stay off (windows render as after S1).");
                return assets;
            }
            assets.Resolve = LoadResolve(bundle);
            assets.Count = LoadCount(bundle);
            return assets;
        }

        static Shader LoadResolve(AssetBundle bundle)
        {
            Shader shader = null;
            try
            {
                shader = bundle.LoadAsset<Shader>(WindowAssets.ResolveAsset);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Window pipeline: loading '{WindowAssets.ResolveAsset}' failed ({e.Message}); Win.Resolve stays off (stale windows are laid flat as after S1).");
                return null;
            }
            if (shader == null)
            {
                Plugin.Log.LogWarning($"Window pipeline: '{WindowAssets.ResolveAsset}' is not in realportal.bundle (built before S3); Win.Resolve stays off (stale windows are laid flat as after S1).");
                return null;
            }
            if (!shader.isSupported)
            {
                Plugin.Log.LogWarning($"Window pipeline: '{WindowAssets.ResolveAsset}' is not supported on this device; Win.Resolve stays off.");
                return null;
            }
            return shader;
        }

        static ComputeShader LoadCount(AssetBundle bundle)
        {
            ComputeShader cs = null;
            try
            {
                cs = bundle.LoadAsset<ComputeShader>(WindowAssets.CountAsset);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Window pipeline: loading '{WindowAssets.CountAsset}' failed ({e.Message}); Win.Occlusion and the depth statistics stay off.");
                return null;
            }
            if (cs == null)
            {
                Plugin.Log.LogWarning($"Window pipeline: '{WindowAssets.CountAsset}' is not in realportal.bundle (built before S3); Win.Occlusion and the depth statistics of Win.ErrorModel stay off.");
                return null;
            }
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback)
            {
                Plugin.Log.LogWarning("Window pipeline: no compute shaders or no async GPU readback on this device; Win.Occlusion and the depth statistics stay off.");
                return null;
            }
            string missing = null;
            foreach (string k in new[] { WindowPasses.KernelClear, WindowPasses.KernelCount, WindowPasses.KernelStatsClear, WindowPasses.KernelDepthStats })
                if (!cs.HasKernel(k))
                    missing = missing == null ? k : missing + ", " + k;
            if (missing != null)
            {
                Plugin.Log.LogWarning($"Window pipeline: '{WindowAssets.CountAsset}' lacks kernel(s) {missing}; Win.Occlusion and the depth statistics stay off.");
                return null;
            }
            return cs;
        }
    }
}
