using System;
using UnityEngine;

namespace RealPortal
{
    // Window Pipeline v2 on the game side: the composition root (the WindowManager over PortalSystem's renderer) and its
    // wiring into PortalSystem (Awake, OnDestroy, RenderWindows, the teardowns) and Patches.GameCameraLateUpdate.
    // OWNER: sched (written at the contracts stage; slots, gpu and lab only fill the handlers subscribed here, in their
    // own files). Subscription order is part of the contract (Docs\S2S3-CONTRACTS.md §7.2): tier effects, fog, SSAO, then
    // the camera-targeted draws (grass) last, right before Render(); AfterSlotRender in reverse.
    // While no part is Ready everything here is a pass-through: RenderChosenWindows is PortalRenderer.RenderAll.
    public sealed partial class PortalSystem
    {
        WindowManager _manager;
        readonly WindowPipelineSettings _pipeline = new WindowPipelineSettings();
        float _pipelineErrorAt;

        // The lab and the plugin's patches reach the pipeline here (null when it could not be made).
        internal IWindowManager Manager => _manager;
        internal WindowPipelineSettings PipelineSettings => _pipeline;

        // Whether `cam` renders window views: the shared (legacy) window camera or a slot camera. Use this, not
        // `cam == WindowCamera`, wherever a window camera is recognised (Amplify patches, lab tools).
        internal bool IsWindowCamera(Camera cam) =>
            cam != null && (cam == _renderer.Camera || (_manager != null && _manager.Pool != null && _manager.Pool.IsSlotCamera(cam)));

        // For WindowWorld (the scheduler's game-side facts): the game camera, the render group key, a live link's window.
        internal Camera MainForWindows => _main;
        internal int WindowGroupKey(PortalWindow w) => RenderGroupKey(w);
        internal bool IsLiveWindow(PortalWindow w) =>
            w != null && _byWindow.TryGetValue(w, out PortalLink link) && link != null && link.Partner != null && link.Partner.Window != null;

        // The [Window] config keys of the pipeline (Plugin.cs; Docs\S2S3-CONTRACTS.md §6 "Config keys"), copied before every
        // Plan and RenderChosen (a few fields; the settings are read every frame, so an edit applies on the next frame).
        // MaxRendersPerFrame is the existing key: the legacy count scheduler's cap (PortalRenderSettings, Win.Tiers off)
        // and, with the tiers, a count cap over the ms budget (renders that must happen are never capped).
        void ApplyPipelineConfig()
        {
            WindowPipelineSettings s = _pipeline;
            if (Plugin.CpuBudgetMs == null)
                return;
            s.CpuBudgetMs = Mathf.Max(0f, Plugin.CpuBudgetMs.Value);
            s.GpuBudgetMs = Mathf.Max(0f, Plugin.GpuBudgetMs.Value);
            s.TierA_Share = Plugin.TierAShare.Value;
            s.TierB_Share = Plugin.TierBShare.Value;
            s.TierC_Share = Plugin.TierCShare.Value;
            s.MaxRendersPerFrame = Mathf.Max(0, Plugin.MaxWindowRenders.Value);
        }

        // From Awake, after the renderer exists.
        void InitWindowPipeline()
        {
            try
            {
                WindowLog.Sink = msg => Plugin.Log.LogWarning(msg);
                WindowAssets assets = WindowAssetLoader.Load(Plugin.Bundle, Plugin.WindowMaterial);
                _manager = WindowManager.CreateDefault(_renderer, _pipeline, assets);
                _manager.Windows = _windows;
                _manager.World = new WindowWorld(this);

                _manager.OnSlotCameraCreated += FogCommandBuffer.Attach;
                _manager.OnSlotCameraCreated += AoSync.Attach;
                _manager.OnSlotCameraCreated += FarGrassSubmit.Attach;
                _manager.OnSlotCameraDestroyed += FarGrassSubmit.Detach;
                _manager.OnSlotCameraDestroyed += AoSync.Detach;
                _manager.OnSlotCameraDestroyed += FogCommandBuffer.Detach;

                // Order matters: the camera-targeted draws go last, nothing may run between them and Render() (P3(ii)).
                _manager.BeforeSlotRender += TierEffects.Before;
                _manager.BeforeSlotRender += FogCommandBuffer.Before;
                _manager.BeforeSlotRender += AoSync.Before;
                _manager.BeforeSlotRender += FarGrassSubmit.Before;
                // Far.Proxies: the far area's proxies, camera-targeted too (PortalRenderer.BeforePlainRender takes the
                // renders without these events; PortalSystem.Awake sets it).
                _manager.BeforeSlotRender += ProxyDraw.Before;
                _manager.AfterSlotRender += FarGrassSubmit.After;
                _manager.AfterSlotRender += AoSync.After;
                _manager.AfterSlotRender += FogCommandBuffer.After;
                _manager.AfterSlotRender += TierEffects.After;
                // A nested view (Recursion) of a cached-tier render gets that tier's point-light shadows too, and (last, as
                // in BeforeSlotRender: P3(ii)) Win.BaseHidesFar's noted grass patches it shows.
                _renderer.BeforeNestedRender += TierEffects.Before;
                _renderer.BeforeNestedRender += FarGrassSubmit.BeforeNested;
                _renderer.BeforeNestedRender += ProxyDraw.BeforeNested;
                _renderer.AfterNestedRender += TierEffects.After;

                GraphicsSettingsManager.GraphicsSettingsChanged += WindowSettingsChanged;
                WindowFlags.Changed += WindowSettingsChanged;
            }
            catch (Exception e)
            {
                // Never lose the windows over this: without a manager RenderWindows calls RenderAll as before.
                Plugin.Log.LogError("Window pipeline v2 could not be set up (windows render as before): " + e);
                _manager = null;
            }
            // Win.Virtual (Windows\WindowScreenGlue.cs): the content and pick of windows drawn without a camera (inert while off).
            InitWindowScreen();
            // The engine's GrassMainCameraOnly as Grass.Targeted wants it from the start (RenderWindows syncs it only while
            // a link is live): an earlier session that switched it on and ended without teardown left it on.
            try
            {
                FarGrassSubmit.FrameSync(_manager);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Grass.Targeted: syncing the engine's GrassMainCameraOnly failed: " + e.Message);
            }
        }

        // From OnDestroy, before the renderer goes.
        void DisposeWindowPipeline()
        {
            GraphicsSettingsManager.GraphicsSettingsChanged -= WindowSettingsChanged;
            WindowFlags.Changed -= WindowSettingsChanged;
            if (_manager == null)
                return;
            try
            {
                _manager.Dispose();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Window pipeline v2 dispose: " + e);
            }
            _manager = null;
        }

        void WindowSettingsChanged()
        {
            _manager?.SettingsChanged();
            AoSync.SettingsChanged();
            ProxyDraw.SettingsChanged();
        }

        // From Patches.GameCameraLateUpdate (every frame, after WindowLayerCull): the pose is final, TAA has not jittered yet.
        internal void PlanWindows(GameCamera gameCamera)
        {
            if (_manager == null || !_manager.WantsPlan || Suspended || gameCamera == null || Plugin.Enabled == null || !Plugin.Enabled.Value)
                return;
            if (_main == null)
                _main = gameCamera.GetComponent<Camera>();
            if (_main == null)
                return;
            try
            {
                ApplyPipelineConfig();
                // The same list RenderWindows fills at onPreCull (links may still go away before then).
                _windows.Clear();
                foreach (PortalLink link in Live())
                    _windows.Add(link.Window);
                _manager.Plan(_main);
            }
            catch (Exception e)
            {
                if (Time.unscaledTime >= _pipelineErrorAt)
                {
                    _pipelineErrorAt = Time.unscaledTime + 10f;
                    Plugin.Log.LogError("Window planning failed: " + e);
                }
            }
        }

        // From RenderWindows, where RenderAll was called (inside the window batch; _windows filled, Jitter set).
        void RenderChosenWindows()
        {
            if (_manager != null)
            {
                ApplyPipelineConfig();
                _manager.RenderChosen(_main);
            }
            else
                _renderer.RenderAll(_main, _windows);
        }

        // From the teardowns next to RenderAll(null, …): the game camera went away or the plugin is suspended.
        void ResetWindowPipeline()
        {
            _manager?.Reset();
        }

        // From SetGameCamera(null): the game camera is gone (logout to the menu, the plugin switched off), and with it what
        // the slot cameras were set up from; nothing renders through them until a new one comes, so they go now.
        void ReleaseWindowSlots()
        {
            _manager?.ReleaseSlots();
        }

        // From RenderWindows next to SyncOcclusion (not while idle): per-frame copies onto the slot cameras, and the
        // engine's GrassMainCameraOnly with the slot cameras' grass exclusions following Grass.Targeted (after
        // MonoUpdaters.Update has recorded the game camera's grass, before any slot camera renders).
        void SyncSlotEffects(GameCamera gameCamera)
        {
            FarGrassSubmit.FrameSync(_manager);
            if (_manager == null)
                return;
            AoSync.FrameSync(gameCamera, _manager);
            FogCommandBuffer.FrameSync(gameCamera, _manager);
            // After the syncs: the prewarm reads whether the game's SSAO and fog are on this frame.
            PrewarmSlots();
        }

        // Win.SlotPrewarm (A11): the slot cameras the live portals will render with, each with its SSAO component and its
        // fog buffer, made from the first live link on (its far side is usually still loading), before a window needs
        // one: made at a window's first render they cost that frame 67-80 ms (the first window of a session, or the first
        // after the 30 s idle sweep, which the manager stretches to the pool's LiveIdleSeconds while links are live).
        // One piece a frame (a camera, a component or a buffer), so the prewarm is no hitch of its own; once everything
        // is there it looks again once a second (a slot swept or destroyed, more links live, the game's SSAO or fog on).
        float _prewarmCheckAt;
        // Lab: cameras, SSAO components and fog buffers made ahead.
        internal static int LabPrewarmCameras, LabPrewarmAo, LabPrewarmFog;

        void PrewarmSlots()
        {
            if (!WindowFlags.SlotPrewarm || _main == null || !(_manager.Pool is WindowCameraPool pool)
                || !_manager.Effective(WindowMechanism.CameraSlots))
                return;
            int live = Live().Count;
            if (live == 0 || Time.unscaledTime < _prewarmCheckAt)
                return;
            // A slot for every window (each keeps its own while the pool has enough) and a spare, up to the capacity:
            // with a hall of twenty that is every slot, with one portal two cameras instead of sixteen.
            int want = Mathf.Min(pool.Capacity, live + 1);
            if (pool.Prewarm(want, _main, _renderer.Settings) != null)
            {
                LabPrewarmCameras++;
                return;
            }
            for (int i = 0; i < want; i++)
            {
                Camera cam = pool.CameraAt(i);
                if (cam == null)
                    continue;
                if (AoSync.Prewarm(cam))
                {
                    LabPrewarmAo++;
                    return;
                }
                if (FogCommandBuffer.Prewarm(cam))
                {
                    LabPrewarmFog++;
                    return;
                }
            }
            _prewarmCheckAt = Time.unscaledTime + 1f;
        }
    }
}
