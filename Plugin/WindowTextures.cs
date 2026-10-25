using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace RealPortal
{
    // The game's SSAO (Amplify Occlusion) on the window camera keeps its textures at the size of the view it last
    // drew, and makes them anew whenever the size changes: with several windows of different sizes that was every
    // window every frame (8 ms a frame for nine windows, plus the GPU's allocations). While a window renders, the
    // textures it gives up wait here by size and come back for the next view of that size. What it gets is the same
    // as before (new textures clear its history just as now), only without the allocations.
    // With Win.RTPool those of the top-level views (32 px size classes) wait in the window texture pool (WindowRTPool,
    // by name and size, its idle expiry and budget) instead of the stacks here; those of deeper levels (recursion: a
    // size of their own every frame) keep using the stacks, as before. One camera for all windows needs this hook.
    // Win.CameraSlots: a slot camera's SSAO (AoSync) keeps its textures from render to render (a sticky slot shows one
    // window, in 32 px size classes), so it allocates only when its window's size class changes or its SSAO goes on or
    // off; those few allocations pass through here too (window renders are Active) and are pooled like the shared
    // camera's, which is what they want. Nothing here needs to tell the cameras apart.
    [HarmonyPatch]
    internal static class WindowTextures
    {
        struct Key
        {
            public string Name;
            public int W, H, Aa;
            public RenderTextureFormat Format;
            public FilterMode Filter;
            public bool Mips;
        }

        static readonly Dictionary<Key, Stack<RenderTexture>> s_pool = new Dictionary<Key, Stack<RenderTexture>>();
        static readonly Dictionary<Key, float> s_usedAt = new Dictionary<Key, float>();
        static readonly List<Key> s_drop = new List<Key>();
        static float s_sweepAt;

        // Set by PortalSystem around each window render (the renderer's own InWindowRender covers the camera render
        // itself, where the SSAO allocates).
        internal static bool InWindow;

        static bool Active => InWindow || PortalRenderer.InWindowRender;

        // Lab: textures taken from the pool and made.
        internal static int LabReused, LabMade;

        // Win.RTPool: the SSAO textures made or taken during a top-level window render. Only these go back to the
        // window texture pool (whatever render releases them); the rest go to the stacks here.
        static readonly HashSet<RenderTexture> s_pooled = new HashSet<RenderTexture>();
        static bool s_makingPooled;
        static readonly List<RenderTexture> s_gone = new List<RenderTexture>();

        static WindowRTPool.Key PoolKey(string name, int width, int height, int antiAliasing, RenderTextureFormat format, FilterMode filter, bool mips) =>
            new WindowRTPool.Key
            {
                Name = name ?? "", W = width, H = height, Depth = 0, Aa = Mathf.Max(antiAliasing, 1), Format = format, Filter = filter, Mips = mips,
            };

        [HarmonyPrefix, HarmonyPatch(typeof(AmplifyOcclusion.AmplifyOcclusionCommon), nameof(AmplifyOcclusion.AmplifyOcclusionCommon.SafeAllocateRT))]
        static bool Allocate(string name, int width, int height, RenderTextureFormat format, FilterMode filterMode, int antiAliasing, bool aUseMipMap, ref RenderTexture __result)
        {
            s_makingPooled = false;
            if (!Active)
                return true;
            if (WindowFlags.RTPool && PortalRenderer.WindowRenderLevel == 0)
            {
                RenderTexture got = WindowRTPool.Take(PoolKey(name, Mathf.Clamp(width, 1, 65536), Mathf.Clamp(height, 1, 65536), antiAliasing, format, filterMode, aUseMipMap), true);
                if (got != null)
                {
                    __result = got;
                    s_pooled.Add(got);
                    LabReused++;
                    return false;
                }
                LabMade++;
                s_makingPooled = true;
                return true;
            }
            var key = new Key { Name = name, W = Mathf.Clamp(width, 1, 65536), H = Mathf.Clamp(height, 1, 65536), Aa = Mathf.Max(antiAliasing, 1), Format = format, Filter = filterMode, Mips = aUseMipMap };
            s_usedAt[key] = Time.unscaledTime;
            if (s_pool.TryGetValue(key, out Stack<RenderTexture> stack))
                while (stack.Count > 0)
                {
                    RenderTexture rt = stack.Pop();
                    if (rt != null && rt.IsCreated())
                    {
                        __result = rt;
                        LabReused++;
                        return false;
                    }
                }
            LabMade++;
            return true;
        }

        // The one the game just made for a top-level window render belongs to the window texture pool too.
        [HarmonyPostfix, HarmonyPatch(typeof(AmplifyOcclusion.AmplifyOcclusionCommon), nameof(AmplifyOcclusion.AmplifyOcclusionCommon.SafeAllocateRT))]
        static void Allocated(RenderTexture __result)
        {
            if (!s_makingPooled)
                return;
            s_makingPooled = false;
            if (__result != null)
                s_pooled.Add(__result);
        }

        [HarmonyPrefix, HarmonyPatch(typeof(AmplifyOcclusion.AmplifyOcclusionCommon), nameof(AmplifyOcclusion.AmplifyOcclusionCommon.SafeReleaseRT))]
        static bool Release(ref RenderTexture rt)
        {
            if (rt == null)
                return true;
            bool pooled = s_pooled.Remove(rt);
            if (!Active)
                return true;
            if (WindowFlags.RTPool && pooled)
            {
                if (!WindowRTPool.Keep(rt, PoolKey(rt.name, rt.width, rt.height, rt.antiAliasing, rt.format, rt.filterMode, rt.useMipMap), 2))
                    return true;
                rt = null;
                return false;
            }
            var key = new Key { Name = rt.name, W = rt.width, H = rt.height, Aa = rt.antiAliasing, Format = rt.format, Filter = rt.filterMode, Mips = rt.useMipMap };
            if (!s_pool.TryGetValue(key, out Stack<RenderTexture> stack))
                s_pool[key] = stack = new Stack<RenderTexture>();
            if (stack.Count >= 2)
                return true;
            stack.Push(rt);
            s_usedAt[key] = Time.unscaledTime;
            rt = null;
            return false;
        }

        // Once a second: sizes no window has used for two seconds go.
        internal static void Sweep()
        {
            if (Time.unscaledTime < s_sweepAt)
                return;
            s_sweepAt = Time.unscaledTime + 1f;
            s_drop.Clear();
            foreach (var kv in s_usedAt)
                if (Time.unscaledTime - kv.Value > 2f)
                    s_drop.Add(kv.Key);
            foreach (Key k in s_drop)
            {
                s_usedAt.Remove(k);
                if (s_pool.TryGetValue(k, out Stack<RenderTexture> stack))
                {
                    foreach (RenderTexture rt in stack)
                        Kill(rt);
                    s_pool.Remove(k);
                }
            }
            // Textures the game freed itself (outside a window render) are no longer the pool's.
            s_gone.Clear();
            foreach (RenderTexture rt in s_pooled)
                if (rt == null)
                    s_gone.Add(rt);
            foreach (RenderTexture rt in s_gone)
                s_pooled.Remove(rt);
            // The shared pool sweeps itself as windows render; this keeps it going while none do.
            WindowRTPool.Sweep();
        }

        internal static void Clear()
        {
            foreach (Stack<RenderTexture> stack in s_pool.Values)
                foreach (RenderTexture rt in stack)
                    Kill(rt);
            s_pool.Clear();
            s_usedAt.Clear();
            s_pooled.Clear();
            WindowRTPool.Clear();
        }

        static void Kill(RenderTexture rt)
        {
            if (rt == null)
                return;
            rt.Release();
            Object.Destroy(rt);
        }
    }
}
