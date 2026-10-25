using UnityEngine;

namespace RealPortal
{
    // OWNER: slots (S2). A slot camera's setup (the code map's WindowCamera.cs; the type is WindowSlot, see
    // WindowCameraPool.cs). The shared window camera is CopyFrom(game camera) for every frame; a slot camera is set up once
    // from the game camera and again only when something it was set up from changed (compared at every Acquire: HDR,
    // rendering path, clear flags, depth modes, the renderer's path/clear overrides, Win.DepthOnly, motion vectors for
    // AoTemporal, or SettingsChanged). Per render RenderLevel sets only the transform, the LensShift projection with its
    // jitter, the target and the culling mask; layer cull distances are assigned here, only when they changed.
    public sealed partial class WindowSlot
    {
        // What the camera was set up from (0: not set up).
        internal int Version;
        bool _hdr, _depthOnly, _motion;
        RenderingPath _mainPath, _forcedPath;
        CameraClearFlags _clear;
        DepthTextureMode _mainDepth;
        // Layer cull distances as last assigned (Camera.layerCullDistances allocates on every read).
        readonly float[] _cull = new float[32];
        bool _cullOn;

        /// <summary>The camera exists: made and set up (then Created raised) when it was missing or destroyed.</summary>
        internal bool Ensure(WindowCameraPool pool, Camera main, PortalRenderSettings settings, in TierParams p, int version)
        {
            if (Camera != null)
                return true;
            // Destroyed by someone else: no longer counted.
            if (!ReferenceEquals(Camera, null))
                pool.ForgetDestroyed();
            var go = new GameObject("RealPortalSlot" + Index);
            if (Application.isPlaying)
                Object.DontDestroyOnLoad(go);
            Camera = go.AddComponent<Camera>();
            Camera.enabled = false;
            Version = 0;
            _cullOn = false;
            RenderedFrame = -100;
            // Created's handlers (fog command buffer, Amplify copy, grass exclusion) get a camera set up as the game's.
            Apply(main, settings, p, version);
            pool.RaiseCreated(Camera);
            return Camera != null;
        }

        /// <summary>Setup (when needed) and the per-render layer cull distances, before a render of a window with `p`.</summary>
        internal void Apply(Camera main, PortalRenderSettings settings, in TierParams p, int version)
        {
            bool depthOnly = WindowFlags.DepthOnly;
            // Motion vectors only for AO's temporal filter, on a slot that stays with its window (its history is that window's).
            bool motion = p.AoTemporal && p.Sticky;
            RenderingPath forced = settings != null ? settings.RenderingPath : RenderingPath.UsePlayerSettings;
            CameraClearFlags clear = settings != null ? settings.ClearFlags : CameraClearFlags.Skybox;
            if (Version != version || _hdr != main.allowHDR || _mainPath != main.actualRenderingPath || _forcedPath != forced
                || _clear != clear || _mainDepth != main.depthTextureMode || _depthOnly != depthOnly || _motion != motion)
                Setup(main, forced, clear, depthOnly, motion, version);
            ApplyLayerCull(main);
        }

        void Setup(Camera main, RenderingPath forced, CameraClearFlags clear, bool depthOnly, bool motion, int version)
        {
            Camera cam = Camera;
            cam.enabled = false;
            cam.orthographic = false;
            cam.stereoTargetEye = StereoTargetEyeMask.None;
            cam.renderingPath = forced != RenderingPath.UsePlayerSettings ? forced : main.renderingPath;
            cam.allowHDR = main.allowHDR;
            cam.allowMSAA = false;
            cam.allowDynamicResolution = false;
            cam.useOcclusionCulling = false;
            cam.clearFlags = clear;
            cam.backgroundColor = main.backgroundColor;
            cam.fieldOfView = main.fieldOfView;
            cam.opaqueSortMode = main.opaqueSortMode;
            cam.transparencySortMode = main.transparencySortMode;
            cam.transparencySortAxis = main.transparencySortAxis;
            cam.cullingMask = main.cullingMask;
            cam.targetTexture = null;
            cam.rect = new Rect(0f, 0f, 1f, 1f);
            // The depth texture only, as PortalRenderer.SetupCamera gives the shared camera (Win.DepthOnly): nothing in a
            // window render reads depth+normals in deferred (P13), and motion vectors only AO's temporal filter. A forward
            // window camera keeps depth+normals: its SSAO copy takes the normals from them.
            DepthTextureMode mode;
            if (depthOnly)
            {
                mode = DepthTextureMode.Depth;
                if (cam.actualRenderingPath != RenderingPath.DeferredShading)
                    mode |= main.depthTextureMode & DepthTextureMode.DepthNormals;
            }
            else
                mode = main.depthTextureMode | DepthTextureMode.Depth;
            if (motion)
                mode |= DepthTextureMode.MotionVectors;
            cam.depthTextureMode = mode;

            Version = version;
            _hdr = main.allowHDR;
            _mainPath = main.actualRenderingPath;
            _forcedPath = forced;
            _clear = clear;
            _mainDepth = main.depthTextureMode;
            _depthOnly = depthOnly;
            _motion = motion;
            // The layer culling is assigned again after a setup (another mode may have left it).
            _cullOn = false;
            ApplyLayerCull(main, force: true);
        }

        // Objects only out to where the game loads them (Win.LayerCull, PortalRenderer.WindowLayerCull, spherical), as on
        // the shared camera; without it the game camera's own distances, read once when it goes off.
        void ApplyLayerCull(Camera main, bool force = false)
        {
            Camera cam = Camera;
            float[] cull = WindowFlags.LayerCull ? PortalRenderer.WindowLayerCull : null;
            if (cull != null)
            {
                bool same = _cullOn && !force && cull.Length >= 32;
                for (int i = 0; same && i < 32; i++)
                    same = _cull[i] == cull[i];
                if (same)
                    return;
                cam.layerCullDistances = cull;
                cam.layerCullSpherical = true;
                for (int i = 0; i < 32; i++)
                    _cull[i] = i < cull.Length ? cull[i] : 0f;
                _cullOn = true;
            }
            else if (_cullOn || force)
            {
                cam.layerCullDistances = main.layerCullDistances;
                cam.layerCullSpherical = main.layerCullSpherical;
                _cullOn = false;
            }
        }
    }
}
