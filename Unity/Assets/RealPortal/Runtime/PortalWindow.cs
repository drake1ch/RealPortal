using UnityEngine;
using UnityEngine.Rendering;

namespace RealPortal
{
    // The visible surface of one portal. Its mesh is a prism of the opening outline: the front cap sits on the
    // portal plane and the rest extends along local -Z. Normally it is squashed flat; when the viewer's near plane
    // would cut the plane it is stretched away from the viewer so the near plane stays inside the window.
    public sealed partial class PortalWindow
    {
        public const string TextureProp = "_MainTex";
        public const string ActiveProp = "_Active";

        public readonly PortalFrame Frame;
        public readonly GameObject Root;
        public readonly MeshRenderer Renderer;
        public readonly Material Material;
        public PortalWindow Partner;
        public bool Enabled = true;
        // Unique per window (from 1), also on its material (_RP_WindowId): inside a window render the window whose
        // id is _RP_RenderingWindowId keeps its own picture, every other one shows the fallback (WindowFlags.FallbackFlag),
        // except those in the render's nested table (_RP_NestedIds, WindowFlags.NestedLive), which show their level-1 view.
        public readonly int Id;
        static int s_nextId;
        public const string WindowIdProp = "_RP_WindowId";

        internal RenderTexture Texture;      // this frame's view (a temporary owned by the renderer)
        internal Rect TextureRect;           // the screen rect that view covers
        internal RenderTexture DepthTexture; // its linear eye depth (for the game camera's depth effects)
        internal RenderTexture NormalTexture;// its G-buffer normals, in the far side's frame
        internal Matrix4x4 NormalRotation = Matrix4x4.identity; // far side -> this side
        internal bool RenderedThisFrame;
        // Shown this frame from an earlier render (a small or far window refreshed every few frames): the view is
        // laid onto the opening as it was seen then (ViewVP: the game camera's view-projection at that render).
        internal bool Stale;
        internal Matrix4x4 ViewVP;
        // The view's depth to this side's metres (1 / the view scale, see PortalMath.View) at that render.
        internal float DepthScale = 1f;
        internal int RenderedAtFrame = -100, ShownAtFrame = -100;
        // Lab (PortalRenderer.LabDistantRenders): the frame it was last let render only by WindowFlags.DistantLive.
        internal int DistantAtFrame = -100;
        // WindowFlags.NestedLive: the frame it was last rendered at level 1 inside another window's view (the pixel floor
        // keeps such a window down to PortalRenderer.FloorKeep of it the next frame).
        internal int NestedAtFrame = -100;
        // The culling frustum and position of its last on-screen view (grass is kept for it between renders).
        internal readonly Plane[] ViewFrustum = new Plane[6];
        internal Vector3 ViewPos;
        internal bool HasView;
        // The textures of its last render, kept for the frames it is shown again.
        internal readonly System.Collections.Generic.List<RenderTexture> Owned = new System.Collections.Generic.List<RenderTexture>();
        // Time.time of the last render of this window's view (what is seen through it is wanted while recent).
        public float LastRendered = -100f;
        internal float Depth = Flat;

        const float Flat = 0.002f;
        static Mesh s_round, s_square;
        static readonly int s_tex = Shader.PropertyToID(TextureProp);
        static readonly int s_active = Shader.PropertyToID(ActiveProp);
        static readonly int s_texRect = Shader.PropertyToID("_RP_TexRect");
        static readonly int s_depth = Shader.PropertyToID("_RP_ViewDepth");
        static readonly int s_normal = Shader.PropertyToID("_RP_ViewNormal");
        static readonly int s_hasDepth = Shader.PropertyToID("_RP_HasDepth");
        static readonly int s_hasNormal = Shader.PropertyToID("_RP_HasNormal");
        static readonly int s_normalRot = Shader.PropertyToID("_RP_NormalRot");
        static readonly int s_reproject = Shader.PropertyToID("_RP_Reproject");
        static readonly int s_reprojVP = Shader.PropertyToID("_RP_ReprojVP");
        static readonly int s_depthScale = Shader.PropertyToID("_RP_DepthScale");
        static readonly int s_windowId = Shader.PropertyToID(WindowIdProp);
        static readonly int s_parity = Shader.PropertyToID("_RP_Parity");

        public PortalWindow(PortalFrame frame, Transform parent, Material material, int layer)
        {
            Frame = frame;
            Id = ++s_nextId;
            Root = new GameObject("RealPortalWindow") { layer = layer };
            Root.transform.SetParent(parent, false);
            if (frame.Shape != null && frame.Shape.Length >= 3)
            {
                _ownMesh = Prism(frame.Shape);
                Root.AddComponent<MeshFilter>().sharedMesh = _ownMesh;
            }
            else
                Root.AddComponent<MeshFilter>().sharedMesh = frame.Round ? Round() : Square();
            Renderer = Root.AddComponent<MeshRenderer>();
            Renderer.shadowCastingMode = ShadowCastingMode.Off;
            // Only the camera moves the view; with the virtual depth that gives TAA and motion blur the right motion.
            Renderer.motionVectorGenerationMode = MotionVectorGenerationMode.Camera;
            Renderer.allowOcclusionWhenDynamic = false;
            Renderer.receiveShadows = false;
            Renderer.lightProbeUsage = LightProbeUsage.Off;
            Renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            Material = new Material(material);
            Renderer.sharedMaterial = Material;
            // Ids stay well inside a float's exact integers (2^24).
            Material.SetFloat(s_windowId, Id);
            SetDepth(Flat);
            ShowFallback();
        }

        // Whether this window's shader reads the global fallback flag (a bundle built before it existed does not;
        // then every window's material is switched to its fallback for each render, as before).
        internal bool ShaderHasFallbackFlag => Material.HasProperty(s_windowId);

        // Whether this window's shader has the parity passes (Docs\PARITY-PLAN.md P7, P42, P43: the nested window table, the
        // fog copy's mode 3, the reflection probes' discard), marked by its _RP_Parity property (a bundle from before them
        // has none).
        internal bool ShaderHasParity => Material.HasProperty(s_parity);

        public void Destroy()
        {
            ReleaseOwned();
            PortalMath.Kill(Root);
            PortalMath.Kill(Material);
            PortalMath.Kill(_ownMesh);
        }

        Mesh _ownMesh;

        // Follow the frame (it may move with the portal) and keep the scale in sync with its size.
        internal void SyncTransform()
        {
            Transform t = Root.transform;
            t.SetPositionAndRotation(Frame.Center, Frame.Rotation);
            Vector3 lossy = t.parent != null ? t.parent.lossyScale : Vector3.one;
            t.localScale = new Vector3(Frame.HalfSize.x / lossy.x, Frame.HalfSize.y / lossy.y, Depth / lossy.z);
        }

        // depth > 0 extends behind the front face (local -Z), depth < 0 in front of it.
        internal void SetDepth(float depth)
        {
            Depth = Mathf.Abs(depth) < Flat ? Flat : depth;
            SyncTransform();
        }

        internal float FarDepth => -Depth;

        public void ShowTexture(Texture tex) => ShowTexture(tex, new Rect(0f, 0f, 1f, 1f));

        // rect: the part of the screen (0..1) that the texture covers.
        public void ShowTexture(Texture tex, Rect rect, Texture depth = null, Texture normal = null)
        {
            Material.SetTexture(s_tex, tex);
            Material.SetVector(s_texRect, new Vector4(rect.x, rect.y, rect.width, rect.height));
            Material.SetFloat(s_active, 1f);
            Material.SetFloat(s_hasDepth, depth != null ? 1f : 0f);
            Material.SetFloat(s_depthScale, DepthScale);
            if (depth != null)
                Material.SetTexture(s_depth, depth);
            Material.SetFloat(s_hasNormal, normal != null ? 1f : 0f);
            if (normal != null)
            {
                Material.SetTexture(s_normal, normal);
                Material.SetMatrix(s_normalRot, NormalRotation);
            }
            Material.SetFloat(s_reproject, 0f);
        }

        // An earlier view shown again: where each point of the opening was on screen when it was rendered.
        void Reproject(bool on)
        {
            Material.SetFloat(s_reproject, on ? 1f : 0f);
            if (on)
                Material.SetMatrix(s_reprojVP, ViewVP);
        }

        internal void ReleaseOwned()
        {
            for (int i = 0; i < Owned.Count; i++)
                if (Owned[i] != null)
                    WindowRTPool.Return(Owned[i]);
            Owned.Clear();
            Texture = null;
            DepthTexture = NormalTexture = null;
            // The pipeline's cache (WindowState.Cache) referred to these textures.
            State?.OnCacheReleased();
        }

        // Nothing of its own to show: _Active 0, the shader's fallback (with WindowFlags.VanillaLook the window is not drawn
        // at all, the game's empty ring; otherwise the procedural disc).
        public void ShowFallback() => Material.SetFloat(s_active, 0f);

        // Test hook: when set, every window shows this texture flat (used to find window pixels on screen).
        public static Texture DebugSolid;

        // What this frame gave it: its view (true) or the fallback (false).
        public bool ShowOwn()
        {
            if (DebugSolid != null)
            {
                ShowTexture(DebugSolid);
                return true;
            }
            if (RenderedThisFrame && Texture != null)
            {
                ShowTexture(Texture, TextureRect, DepthTexture, NormalTexture);
                Reproject(Stale);
                return true;
            }
            ShowFallback();
            return false;
        }

        static Mesh Round() => s_round != null ? s_round : (s_round = Prism(48, true));
        static Mesh Square() => s_square != null ? s_square : (s_square = Prism(4, false));

        // Unit outline (radius 1 or half-size 1) at z = 0 and z = -1, both caps plus the side walls.
        static Mesh Prism(int n, bool round)
        {
            var ring = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                if (round)
                {
                    float a = Mathf.PI * 2f * i / n;
                    ring[i] = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                }
                else
                {
                    ring[i] = new Vector3(i == 0 || i == 3 ? -1f : 1f, i < 2 ? -1f : 1f, 0f);
                }
            }
            return Prism(ring, round ? "RealPortalRound" : "RealPortalSquare");
        }

        // An opening of its own form (star-shaped around the centre, counter-clockwise).
        static Mesh Prism(Vector2[] shape)
        {
            var ring = new Vector3[shape.Length];
            for (int i = 0; i < shape.Length; i++)
                ring[i] = new Vector3(shape[i].x, shape[i].y, 0f);
            return Prism(ring, "RealPortalShaped");
        }

        static Mesh Prism(Vector3[] ring, string name)
        {
            int n = ring.Length;
            var v = new Vector3[n * 2 + 2];
            for (int i = 0; i < n; i++)
            {
                v[i] = ring[i];
                v[n + i] = ring[i] + Vector3.back;
            }
            v[2 * n] = Vector3.zero;
            v[2 * n + 1] = Vector3.back;
            var tris = new int[n * 12];
            int k = 0;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                tris[k++] = 2 * n; tris[k++] = j; tris[k++] = i;
                tris[k++] = 2 * n + 1; tris[k++] = n + i; tris[k++] = n + j;
                tris[k++] = i; tris[k++] = j; tris[k++] = n + j;
                tris[k++] = i; tris[k++] = n + j; tris[k++] = n + i;
            }
            var mesh = new Mesh { name = name, vertices = v, triangles = tris };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
