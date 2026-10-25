using System.Collections.Generic;
using UnityEngine;
using UnityEngine.PostProcessing;
using UnityEngine.Rendering;

namespace RealPortal
{
    // OWNER: slots (S2, Win.OwnFog). A slot camera fogs its own view with a command buffer at
    // CameraEvent.AfterImageEffectsOpaque, the event of the game's FogComponent (FogComponent.GetCameraEvent), so the order
    // against Amplify (BeforeImageEffectsOpaque) is the game's. It replicates FogComponent.PopulateCommandBuffer: material
    // "Hidden/Post FX/Fog" (one per slot camera), keywords FOG_LINEAR/FOG_EXP/FOG_EXP2 from RenderSettings.fogMode,
    // _FogColor (linear when the colour space is), _Density, _Start, _End from RenderSettings as the far side has them at
    // BeforeSlotRender (FarEnvironment/AreaRenderState applied them in PortalRenderer.BeforeRender), _TopLeft.._BottomRight
    // from the slot camera's ViewportPointToRay (its lens-shifted projection is final at BeforeSlotRender), pass
    // excludeSkybox ? 1 : 0 from the game's fog settings; a temporary without a depth buffer, no OnRenderImage copy.
    // Only while the game's fog is on (profile fog enabled, RenderSettings.fog, deferred) and Plugin.Fog. With Win.OwnFog
    // off (or not effective) a slot camera gets a fog-only PostProcessingBehaviour like the shared camera
    // (PortalSystem.SyncFog), so off is the post-S1 fog. Docs\S2S3-CONTRACTS.md §7.4.
    //
    // What a slot camera is set up with is decided right before each of its renders (Before), not at creation: the
    // switch, the game's fog and the camera's rendering path may all change while the camera lives. The command buffer
    // itself is recorded once per (pass, HDR) and sized by the camera (-1: its pixel size), so only the material's values
    // change from render to render; the buffer reads them when it runs, inside that render. Win.SlotPrewarm makes the
    // material and records the buffer ahead (Prewarm); attaching it stays Before's decision.
    // The shared (legacy) window camera is never touched here: it keeps PortalSystem.SyncFog's behaviour.
    internal static class FogCommandBuffer
    {
        const string ShaderName = "Hidden/Post FX/Fog";
        const CameraEvent Event = CameraEvent.AfterImageEffectsOpaque;

        static Shader s_shader;
        static bool s_looked;

        // The fog material was found (the same Shader.Find the game's PostProcessing MaterialFactory uses). Without it the
        // slot cameras fog with a PostProcessingBehaviour, as with Win.OwnFog off.
        internal static bool Supported
        {
            get
            {
                if (!s_looked)
                {
                    s_looked = true;
                    s_shader = Shader.Find(ShaderName);
                    if (s_shader == null)
                        Plugin.Log.LogWarning($"Win.OwnFog: shader '{ShaderName}' not found; slot cameras fog with a post-processing stack as before.");
                }
                return s_shader != null;
            }
        }

        sealed class Slot
        {
            public Camera Cam;
            public CommandBuffer Cb;
            public Material Mat;
            // Cb is on the camera (at Event).
            public bool Attached;
            // What Cb was recorded for (-1: not yet).
            public int RecordedPass = -1;
            public bool RecordedHdr;
            // The fog keyword set on Mat.
            public FogMode Mode;
            public bool HasMode;
            // Win.OwnFog off: the fog-only post stack on this camera.
            public PostProcessingBehaviour Post;
        }

        static readonly List<Slot> s_slots = new List<Slot>();

        // The game's fog this frame (FrameSync): on (Plugin.Fog and its profile's fog) and its settings.
        static bool s_gameOn;
        static FogModel.Settings s_gameSettings = FogModel.Settings.defaultSettings;
        static GameCamera s_gameCamera;
        static PostProcessingBehaviour s_gamePost;
        // The fog-only profile every slot camera's post stack shares (Win.OwnFog off).
        static PostProcessingProfile s_profile;

        static readonly int s_temp = Shader.PropertyToID("_RP_SlotFogTemp");
        static readonly int s_fogColor = Shader.PropertyToID("_FogColor");
        static readonly int s_density = Shader.PropertyToID("_Density");
        static readonly int s_start = Shader.PropertyToID("_Start");
        static readonly int s_end = Shader.PropertyToID("_End");
        static readonly int s_topLeft = Shader.PropertyToID("_TopLeft");
        static readonly int s_topRight = Shader.PropertyToID("_TopRight");
        static readonly int s_bottomLeft = Shader.PropertyToID("_BottomLeft");
        static readonly int s_bottomRight = Shader.PropertyToID("_BottomRight");

        // Lab: slot renders fogged by our buffer, by a post stack (Win.OwnFog off), and left without fog (the game's off).
        internal static long LabOwn, LabPost, LabNone;

        static IWindowManager Manager => PortalSystem.Instance != null ? PortalSystem.Instance.Manager : null;

        static Slot Find(Camera cam)
        {
            for (int i = 0; i < s_slots.Count; i++)
                if (ReferenceEquals(s_slots[i].Cam, cam))
                    return s_slots[i];
            return null;
        }

        // OnSlotCameraCreated. The mode (buffer or post stack) is chosen at the camera's first render.
        internal static void Attach(Camera cam)
        {
            if ((object)cam == null || Find(cam) != null)
                return;
            s_slots.Add(new Slot { Cam = cam });
        }

        // OnSlotCameraDestroyed.
        internal static void Detach(Camera cam)
        {
            for (int i = s_slots.Count - 1; i >= 0; i--)
                if (ReferenceEquals(s_slots[i].Cam, cam))
                {
                    Release(s_slots[i]);
                    s_slots.RemoveAt(i);
                }
        }

        // Once per frame (PortalSystem.RenderWindows, not idle): the game's fog settings (enabled, excludeSkybox) for all
        // slots, as SyncFog reads them for the shared camera.
        internal static void FrameSync(GameCamera gameCamera, IWindowManager manager)
        {
            if (gameCamera != s_gameCamera || s_gamePost == null)
            {
                s_gameCamera = gameCamera;
                s_gamePost = gameCamera != null ? gameCamera.GetComponent<PostProcessingBehaviour>() : null;
            }
            PostProcessingBehaviour game = s_gamePost;
            s_gameOn = Plugin.Fog.Value && game != null && game.profile != null && game.profile.fog.enabled;
            if (s_gameOn)
                s_gameSettings = game.profile.fog.settings;
            if (s_profile != null)
            {
                // The shared fog-only profile follows the game's, as PortalSystem.SyncFog does for the shared camera.
                if (s_profile.fog.enabled != s_gameOn)
                    s_profile.fog.enabled = s_gameOn;
                if (s_gameOn)
                    s_profile.fog.settings = s_gameSettings;
            }
            // Cameras destroyed without OnSlotCameraDestroyed (the pool went with the manager).
            for (int i = s_slots.Count - 1; i >= 0; i--)
                if (s_slots[i].Cam == null)
                {
                    Release(s_slots[i]);
                    s_slots.RemoveAt(i);
                }
        }

        // BeforeSlotRender, second handler: this render's fog values into the slot's material.
        internal static void Before(WindowState s, Camera cam)
        {
            Slot f = Find(cam);
            if (f == null)
                return;   // the shared camera: PortalSystem.SyncFog's post stack
            IWindowManager m = Manager;
            if (m != null && m.Effective(WindowMechanism.OwnFog) && Supported)
            {
                PostOff(f);
                // FogComponent.active: its model enabled (s_gameOn), RenderSettings.fog, a G-buffer.
                if (s_gameOn && RenderSettings.fog && cam.actualRenderingPath == RenderingPath.DeferredShading)
                {
                    Fill(f, cam);
                    LabOwn++;
                }
                else
                {
                    BufferOff(f);
                    LabNone++;
                }
                return;
            }
            // Win.OwnFog off (or no fog material): the post-S1 way, a fog-only post stack on the camera.
            BufferOff(f);
            PostOn(f, cam);
            if (s_gameOn)
                LabPost++;
            else
                LabNone++;
        }

        // AfterSlotRender: nothing to undo (everything set is the slot camera's own and is set again before its next render).
        internal static void After(WindowState s, Camera cam)
        {
        }

        // Win.SlotPrewarm (WindowGlue.PrewarmSlots): the slot's material and command buffer made and recorded ahead of its
        // first fogged render (not attached: Before attaches it when that render is fogged), only in the mode and with the
        // fog that render would have (Win.OwnFog in force, the game's fog on). False: nothing made.
        internal static bool Prewarm(Camera cam)
        {
            Slot f = Find(cam);
            if (f == null || cam == null || !s_gameOn)
                return false;
            IWindowManager m = Manager;
            if (m == null || !m.Effective(WindowMechanism.OwnFog) || !Supported)
                return false;
            int pass = s_gameSettings.excludeSkybox ? 1 : 0;
            if (f.Mat != null && f.Cb != null && f.RecordedPass == pass && f.RecordedHdr == cam.allowHDR)
                return false;
            MakeMaterial(f);
            Record(f, f.Mat, pass, cam.allowHDR);
            return true;
        }

        static void MakeMaterial(Slot f)
        {
            if (f.Mat == null)
            {
                f.Mat = new Material(s_shader) { name = "RealPortal slot fog", hideFlags = HideFlags.DontSave };
                f.HasMode = false;
                f.RecordedPass = -1;
            }
        }

        // FogComponent.PopulateCommandBuffer for this camera and this render.
        static void Fill(Slot f, Camera cam)
        {
            MakeMaterial(f);
            Material mat = f.Mat;
            FogMode mode = RenderSettings.fogMode;
            if (!f.HasMode || f.Mode != mode)
            {
                // FogComponent clears every keyword and enables one; only these three are ever set on this material.
                mat.DisableKeyword("FOG_LINEAR");
                mat.DisableKeyword("FOG_EXP");
                mat.DisableKeyword("FOG_EXP2");
                switch (mode)
                {
                    case FogMode.Linear:
                        mat.EnableKeyword("FOG_LINEAR");
                        break;
                    case FogMode.Exponential:
                        mat.EnableKeyword("FOG_EXP");
                        break;
                    case FogMode.ExponentialSquared:
                        mat.EnableKeyword("FOG_EXP2");
                        break;
                }
                f.Mode = mode;
                f.HasMode = true;
            }
            Color color = RenderSettings.fogColor;
            mat.SetColor(s_fogColor, QualitySettings.activeColorSpace == ColorSpace.Linear ? color.linear : color);
            mat.SetFloat(s_density, RenderSettings.fogDensity);
            mat.SetFloat(s_start, RenderSettings.fogStartDistance);
            mat.SetFloat(s_end, RenderSettings.fogEndDistance);
            // The view's corner rays through the window's lens-shifted (and jittered) projection: the fog's sun tint
            // follows the direction each pixel looks in over there.
            mat.SetVector(s_topLeft, cam.ViewportPointToRay(new Vector3(0f, 1f, 0f)).direction);
            mat.SetVector(s_topRight, cam.ViewportPointToRay(new Vector3(1f, 1f, 0f)).direction);
            mat.SetVector(s_bottomLeft, cam.ViewportPointToRay(new Vector3(0f, 0f, 0f)).direction);
            mat.SetVector(s_bottomRight, cam.ViewportPointToRay(new Vector3(1f, 0f, 0f)).direction);

            int pass = s_gameSettings.excludeSkybox ? 1 : 0;
            bool hdr = cam.allowHDR;
            Record(f, mat, pass, hdr);
            if (!f.Attached)
            {
                cam.AddCommandBuffer(Event, f.Cb);
                f.Attached = true;
            }
            // FogComponent.GetCameraFlags: the fog reads the camera's depth texture.
            if ((cam.depthTextureMode & DepthTextureMode.Depth) == 0)
                cam.depthTextureMode |= DepthTextureMode.Depth;
        }

        // The slot's buffer, recorded again only when the pass or HDR it was recorded for changed.
        static void Record(Slot f, Material mat, int pass, bool hdr)
        {
            if (f.Cb == null)
                f.Cb = new CommandBuffer { name = "RealPortal slot fog" };
            if (f.RecordedPass != pass || f.RecordedHdr != hdr)
            {
                // The game's: copy the image out and fog it back in. The temporary needs no depth buffer (the fog shader
                // reads _CameraDepthTexture), and -1 sizes it to the camera's target, which changes from render to render.
                CommandBuffer cb = f.Cb;
                cb.Clear();
                cb.GetTemporaryRT(s_temp, -1, -1, 0, FilterMode.Bilinear, hdr ? RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default);
                cb.Blit(BuiltinRenderTextureType.CameraTarget, s_temp);
                cb.Blit(s_temp, BuiltinRenderTextureType.CameraTarget, mat, pass);
                cb.ReleaseTemporaryRT(s_temp);
                f.RecordedPass = pass;
                f.RecordedHdr = hdr;
            }
        }

        static void BufferOff(Slot f)
        {
            if (!f.Attached)
                return;
            f.Attached = false;
            if (f.Cam != null && f.Cb != null)
                f.Cam.RemoveCommandBuffer(Event, f.Cb);
        }

        // Win.OwnFog off: PortalSystem.SyncFog's setup on this camera (the profile follows the game's in FrameSync).
        static void PostOn(Slot f, Camera cam)
        {
            if (s_profile == null)
            {
                s_profile = ScriptableObject.CreateInstance<PostProcessingProfile>();
                s_profile.hideFlags = HideFlags.DontSave;
                s_profile.fog.enabled = s_gameOn;
                if (s_gameOn)
                    s_profile.fog.settings = s_gameSettings;
            }
            if (f.Post == null)
            {
                f.Post = cam.gameObject.AddComponent<PostProcessingBehaviour>();
                f.Post.profile = s_profile;
            }
            if (f.Post.enabled != s_gameOn)
                f.Post.enabled = s_gameOn;
        }

        // Win.OwnFog on: no post stack on a slot camera. Disabled at once (its buffers come off before this render; a
        // destroyed component would still run in it) and destroyed.
        static void PostOff(Slot f)
        {
            if (f.Post == null)
                return;
            f.Post.enabled = false;
            Object.Destroy(f.Post);
            f.Post = null;
        }

        static void Release(Slot f)
        {
            BufferOff(f);
            if (f.Cb != null)
            {
                f.Cb.Release();
                f.Cb = null;
            }
            if (f.Mat != null)
            {
                Object.Destroy(f.Mat);
                f.Mat = null;
            }
            if (f.Post != null)
            {
                Object.Destroy(f.Post);
                f.Post = null;
            }
        }

        // PortalSystem.OnDestroy: every slot's buffer and material (the cameras went with the manager's pool).
        internal static void Clear()
        {
            for (int i = 0; i < s_slots.Count; i++)
                Release(s_slots[i]);
            s_slots.Clear();
            if (s_profile != null)
            {
                Object.Destroy(s_profile);
                s_profile = null;
            }
            if (s_virtual != null)
            {
                Object.Destroy(s_virtual);
                s_virtual = null;
            }
            s_gameCamera = null;
            s_gamePost = null;
        }

        // ---- Win.Virtual (WindowScreen): a window drawn without a camera ----------------------------------------------
        // The game's fog material for it, set as Fill sets a slot's (FogComponent.PopulateCommandBuffer's values: the
        // keyword of RenderSettings.fogMode, _FogColor, _Density, _Start, _End as the far side has them now) with the rect's
        // corner rays `corners` (top left, top right, bottom left, bottom right; world directions from C'), and its pass
        // (excludeSkybox); null while the game's fog is off (Plugin.Fog, its profile's fog, RenderSettings.fog). WindowScreen
        // blits with it as FogComponent's buffer does, over the window's textures with their standard depth bound.
        static Material s_virtual;
        static FogMode s_virtualMode;
        static bool s_virtualHasMode;

        internal static Material Virtual(Vector3[] corners, out int pass)
        {
            pass = 0;
            if (!s_gameOn || !RenderSettings.fog || !Supported)
                return null;
            if (s_virtual == null)
            {
                s_virtual = new Material(s_shader) { name = "RealPortal virtual window fog", hideFlags = HideFlags.DontSave };
                s_virtualHasMode = false;
            }
            Material mat = s_virtual;
            FogMode mode = RenderSettings.fogMode;
            if (!s_virtualHasMode || s_virtualMode != mode)
            {
                mat.DisableKeyword("FOG_LINEAR");
                mat.DisableKeyword("FOG_EXP");
                mat.DisableKeyword("FOG_EXP2");
                switch (mode)
                {
                    case FogMode.Linear:
                        mat.EnableKeyword("FOG_LINEAR");
                        break;
                    case FogMode.Exponential:
                        mat.EnableKeyword("FOG_EXP");
                        break;
                    case FogMode.ExponentialSquared:
                        mat.EnableKeyword("FOG_EXP2");
                        break;
                }
                s_virtualMode = mode;
                s_virtualHasMode = true;
            }
            Color color = RenderSettings.fogColor;
            mat.SetColor(s_fogColor, QualitySettings.activeColorSpace == ColorSpace.Linear ? color.linear : color);
            mat.SetFloat(s_density, RenderSettings.fogDensity);
            mat.SetFloat(s_start, RenderSettings.fogStartDistance);
            mat.SetFloat(s_end, RenderSettings.fogEndDistance);
            mat.SetVector(s_topLeft, corners[0]);
            mat.SetVector(s_topRight, corners[1]);
            mat.SetVector(s_bottomLeft, corners[2]);
            mat.SetVector(s_bottomRight, corners[3]);
            pass = s_gameSettings.excludeSkybox ? 1 : 0;
            return mat;
        }
    }
}
