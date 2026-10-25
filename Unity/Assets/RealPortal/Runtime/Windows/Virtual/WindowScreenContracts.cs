using UnityEngine;
using UnityEngine.Rendering;

namespace RealPortal
{
    // Win.Virtual (FARWORLD-V3 §4.4, the V1 spike): what one window's camera-less render is (VirtualView), one sun cascade
    // for its shadow casters (VirtualCascade), and what the game side gives it (IWindowScreenContent: the far side's static
    // content, its sun, probe and fog). The renderer side (WindowScreen, PortalRenderer.Virtual.cs) knows nothing of
    // Valheim; the plugin implements the content (Plugin\Windows\WindowScreenContent.cs).

    /// <summary>One window's view as a camera at C' would have it, this frame. The plane arrays are the renderer's own
    /// (valid until its next virtual render).</summary>
    public struct VirtualView
    {
        public PortalWindow Window;
        /// <summary>The exit portal (far side): what lies on the camera's side of it is not in the view.</summary>
        public PortalFrame Exit;
        /// <summary>C' = M · eye and its rotation (far side), M the pair (this side → far side).</summary>
        public Vector3 Eye;
        public Quaternion Rotation;
        public Vector3 Forward;
        public Matrix4x4 Pair;
        /// <summary>World → camera (OpenGL convention, -z forward), as Camera.worldToCameraMatrix of a camera at C'.</summary>
        public Matrix4x4 View;
        /// <summary>The game camera's projection with this frame's TAA jitter (OpenGL convention), and the same with the
        /// exit plane as its near plane (rows 0, 1 and 3 equal: x/y rasterisation and jitter are the game camera's).</summary>
        public Matrix4x4 Proj, Oblique;
        /// <summary>The window's rect on screen (0..1, snapped to pixels) and the screen-sized targets' size.</summary>
        public Rect Rect;
        public int Width, Height;
        public float Near, Far, FieldOfView;
        public int CullingMask;
        /// <summary>The rect's frustum (what is drawn), and the whole jittered frustum from C' (what a camera there culls
        /// its sun's shadow casters by), Unity's plane order; the exit plane, positive on the side the window shows.</summary>
        public Plane[] ViewPlanes, FullPlanes;
        public Vector4 ExitPlane;
        public float ShadowDistance;
        /// <summary>The window has a WindowState (the manager drives the frame): the proxies' cross-fades are the slot
        /// camera's on-screen render's (ProxyDraw's view key).</summary>
        public bool HasState;
    }

    /// <summary>One cascade of the sun's shadow atlas for a virtual view (WindowScreenShadows): world → atlas uv and device
    /// depth rows (as unity_WorldToShadow), the atlas viewport it is drawn into (uv: x0, y0, x1, y1) and the sun's forward.
    /// Casts says whether a bounding sphere can put a shadow into it.</summary>
    public struct VirtualCascade
    {
        public int Index;
        public Vector4 RowU, RowV, RowZ;
        public Vector4 Tile;
        public Vector3 LightDir;
        /// <summary>|RowU.xyz|, |RowV.xyz|, |RowZ.xyz|: uv (depth) per metre.</summary>
        public float ScaleU, ScaleV, ScaleZ;
        /// <summary>The device depth at the light's far plane (0 under reversed Z, else 1), and +1 when depth grows toward
        /// the light (reversed Z), -1 when it falls.</summary>
        public float FarDepth, TowardLight;

        public bool Casts(Vector3 c, float r)
        {
            float u = RowU.x * c.x + RowU.y * c.y + RowU.z * c.z + RowU.w;
            float v = RowV.x * c.x + RowV.y * c.y + RowV.z * c.z + RowV.w;
            float ru = r * ScaleU, rv = r * ScaleV;
            if (u + ru < Tile.x || u - ru > Tile.z || v + rv < Tile.y || v - rv > Tile.w)
                return false;
            // Wholly beyond the light's far plane (anything nearer than its near plane is pancaked onto it: it casts).
            float z = RowZ.x * c.x + RowZ.y * c.y + RowZ.z * c.z + RowZ.w;
            return TowardLight > 0f ? z + r * ScaleZ >= FarDepth : z - r * ScaleZ <= FarDepth;
        }
    }

    /// <summary>
    /// What the game side gives a virtual render. Called on the main thread between PortalRenderer.BeforeRender and
    /// AfterRender of the window (its far side set up: environment, area state, far terrain shown), in this order: Begin,
    /// Sun/ShadowMapSize, RecordBody, RecordCasters per cascade, Probe, Fog, End (End also after a failure). Record* add
    /// draws to the given buffer and return how many; nothing may be drawn to a camera.
    /// </summary>
    public interface IWindowScreenContent
    {
        /// <summary>Gather this view's content (cull, LOD). False (with why) declines: the slot camera renders it.</summary>
        bool Begin(in VirtualView v, out string why);
        /// <summary>The light that lights the far side now (null: none), and the size of its shadow map a game camera gets
        /// (px; 0: Unity's own rule cannot be told).</summary>
        Light Sun { get; }
        int ShadowMapSize { get; }
        /// <summary>The G-buffer pass of the body: each material's Deferred pass, with WindowScreen.Ambient's SH in its
        /// block. The header (view, projection, camera builtins, keywords) is set.</summary>
        int RecordBody(CommandBuffer cb, in VirtualView v);
        /// <summary>The ShadowCaster passes of what can cast into cascade `c` (view, projection, unity_LightShadowBias and
        /// _WorldSpaceLightPos0 are set).</summary>
        int RecordCasters(CommandBuffer cb, in VirtualView v, in VirtualCascade c);
        /// <summary>The reflection probe the far side's pixels take besides the default reflection (null: none).</summary>
        ReflectionProbe Probe(in VirtualView v);
        /// <summary>The game's fog for this render, its values from RenderSettings now and its corner rays `corners` (top
        /// left, top right, bottom left, bottom right, world directions); null when the game's fog is off.</summary>
        Material Fog(in VirtualView v, Vector3[] corners, out int pass);
        void End();
    }
}
