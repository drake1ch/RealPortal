using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace RealPortal
{
    // Win.ShieldDome (Docs\PARITY-PLAN.md P24). The game draws shield-generator domes as a screen-space pass of its own
    // camera (ShieldDomeImageEffect.OnRenderImage, the first of its image effects, after the transparent queue): a ray march
    // from the camera through every pixel, up to the scene's depth and the pass's draw distance, over every dome the game
    // has registered (ShieldGenerator.Update -> SetShieldData), this side's and the far areas' alike. Standing there it
    // differed twice:
    //  - a far dome never showed in its window: a window camera has no such pass, and the game camera's march at a window's
    //    pixels runs in its own world;
    //  - a near dome behind a window was drawn over it: with the depth seen through the windows bound for its final effects
    //    (PortalRenderer.ViewDepthTexture: TAA, motion blur, depth of field), the game camera's march went on past the
    //    window's surface and met the dome there.
    // Now the game camera's pass reads PlaneDepthTexture (its depth with the windows as the flat surfaces they are) through
    // a copy of its material for that call (as Patches.SunShaftsTwice does for the real sun): a near dome stops at the
    // window. And every window render gets the same pass at the same point of its chain (a command buffer at
    // BeforeImageEffects: after its fog, SSAO and transparent queue, before any image effect), with the game's material
    // settings, the corner rays of the window camera's own lens-shifted, jittered projection (as the window fog takes them)
    // and the domes its march meets as the game's would from there. The march starts at the camera, which for a window
    // stands behind the far portal (Pair·eye), where nothing is in the view up to the opening; the game's shader cannot
    // start it at the opening, so a dome whose surface lies between the camera and some part of the opening (the camera on
    // one side of it, part of the opening on the other) is left out of that render, rather than drawn over the whole
    // window. Domes past the draw distance are left out too (the march never reaches them). Only while the game has domes
    // (its effect enabled) and a window renders. Main thread; nothing allocated per frame once the buffers are made.
    // Off: no pass in the windows, and the game camera's reads the depth bound for its final effects, as before.
    [HarmonyPatch]
    internal static class FarShieldDome
    {
        static readonly AccessTools.FieldRef<ShieldDomeImageEffect, Material> s_material =
            AccessTools.FieldRefAccess<ShieldDomeImageEffect, Material>("m_effectMaterial");
        static readonly AccessTools.FieldRef<ShieldDomeImageEffect, ShieldDomeImageEffect.ShieldDome[]> s_domes =
            AccessTools.FieldRefAccess<ShieldDomeImageEffect, ShieldDomeImageEffect.ShieldDome[]>("m_shieldDomes");
        static readonly AccessTools.FieldRef<ShieldDomeImageEffect, Texture2D> s_gradient =
            AccessTools.FieldRefAccess<ShieldDomeImageEffect, Texture2D>("m_gradientTex");
        static readonly AccessTools.FieldRef<ShieldDomeImageEffect, int> s_stride =
            AccessTools.FieldRefAccess<ShieldDomeImageEffect, int>("m_ShieldDomeStride");

        // The game's names (ShieldDomeImageEffect.Uniforms).
        static readonly int s_topLeft = Shader.PropertyToID("_TopLeft");
        static readonly int s_topRight = Shader.PropertyToID("_TopRight");
        static readonly int s_bottomLeft = Shader.PropertyToID("_BottomLeft");
        static readonly int s_bottomRight = Shader.PropertyToID("_BottomRight");
        static readonly int s_smoothing = Shader.PropertyToID("_Smoothing");
        static readonly int s_depthFade = Shader.PropertyToID("_DepthFade");
        static readonly int s_edgeGlow = Shader.PropertyToID("_EdgeGlow");
        static readonly int s_drawDistance = Shader.PropertyToID("_DrawDistance");
        static readonly int s_domeBuffer = Shader.PropertyToID("_DomeBuffer");
        static readonly int s_domeCount = Shader.PropertyToID("_DomeCount");
        static readonly int s_maxSteps = Shader.PropertyToID("_MaxSteps");
        static readonly int s_surfaceDistance = Shader.PropertyToID("_SurfaceDistance");
        static readonly int s_normalBias = Shader.PropertyToID("_NormalBias");
        static readonly int s_refractStrength = Shader.PropertyToID("_RefractStrength");
        static readonly int s_colorGradient = Shader.PropertyToID("_ShieldColorGradient");
        static readonly int s_shieldTime = Shader.PropertyToID("_ShieldTime");
        static readonly int s_noise = Shader.PropertyToID("_NoiseTexture");
        static readonly int s_curlNoise = Shader.PropertyToID("_CurlNoiseTexture");
        static readonly int s_noiseSize = Shader.PropertyToID("_NoiseSize");
        static readonly int s_curlSize = Shader.PropertyToID("_CurlNoiseSize");
        static readonly int s_curlStrength = Shader.PropertyToID("_CurlNoiseStrength");
        static readonly int s_cameraDepth = Shader.PropertyToID("_CameraDepthTexture");
        static readonly int s_temp = Shader.PropertyToID("_RP_DomeTemp");

        const CameraEvent Event = CameraEvent.BeforeImageEffects;
        // Past the domes' own radius: their smooth union (ShieldGenerator.SDFSmoothMin) swells a surface by up to the
        // effect's smoothing where two meet, and a little more for the march's surface distance.
        const float SurfaceSlack = 0.25f;

        // Lab: game camera passes given the windows' flat depth; window renders given the pass; domes left out of a window
        // render because the stretch from its camera to the opening crosses their surface.
        internal static long LabPlaneDepth, LabWindowPasses, LabDomesLeftOut;

        // ---- the game camera ----------------------------------------------------------------------------------------

        static Material s_planeMat, s_gameMat;
        static ShieldDomeImageEffect s_swappedOn;

        // For this call only, the game's material swapped for a copy whose depth is the windows' flat one (the call sets
        // every value it uses on whichever material the field holds, the dome buffer included).
        [HarmonyPrefix, HarmonyPatch(typeof(ShieldDomeImageEffect), "OnRenderImage")]
        static void GameBefore(ShieldDomeImageEffect __instance)
        {
            s_swappedOn = null;
            if (!WindowFlags.ShieldDome)
                return;
            PortalSystem sys = PortalSystem.Instance;
            RenderTexture plane = sys != null ? sys.PlaneDepthFor(__instance) : null;
            Material mat = plane != null ? s_material(__instance) : null;
            if (mat == null)
                return;
            if (s_planeMat == null || s_planeMat.shader != mat.shader)
            {
                if (s_planeMat != null)
                    Object.Destroy(s_planeMat);
                s_planeMat = new Material(mat) { hideFlags = HideFlags.DontSave, name = "RealPortal shield domes, windows flat" };
            }
            s_planeMat.SetTexture(s_cameraDepth, plane);
            s_gameMat = mat;
            s_material(__instance) = s_planeMat;
            s_swappedOn = __instance;
            LabPlaneDepth++;
        }

        [HarmonyFinalizer, HarmonyPatch(typeof(ShieldDomeImageEffect), "OnRenderImage")]
        static void GameAfter(ShieldDomeImageEffect __instance)
        {
            if (s_swappedOn == null || s_swappedOn != __instance)
                return;
            s_material(__instance) = s_gameMat;
            s_swappedOn = null;
            s_gameMat = null;
        }

        // ---- the window cameras -------------------------------------------------------------------------------------

        sealed class Slot
        {
            public Camera Cam;
            // The buffer (s_cb[Hdr]) is on the camera at Event.
            public bool Attached, Hdr;
        }

        static readonly List<Slot> s_slots = new List<Slot>();
        static readonly CommandBuffer[] s_cb = new CommandBuffer[2];
        static Material s_windowMat;
        static ComputeBuffer s_buffer;
        static ShieldDomeImageEffect.ShieldDome[] s_stage = new ShieldDomeImageEffect.ShieldDome[0];
        static Camera s_gameCamera;
        static ShieldDomeImageEffect s_game;
        static bool s_installed;
        static float s_sweepAt;

        // PortalSystem.Awake / OnDestroy.
        internal static void Install()
        {
            if (s_installed)
                return;
            s_installed = true;
            Camera.onPreCull += PreCull;
        }

        internal static void Clear()
        {
            if (s_installed)
            {
                s_installed = false;
                Camera.onPreCull -= PreCull;
            }
            for (int i = 0; i < s_slots.Count; i++)
                Off(s_slots[i]);
            s_slots.Clear();
            for (int i = 0; i < s_cb.Length; i++)
                if (s_cb[i] != null)
                {
                    s_cb[i].Release();
                    s_cb[i] = null;
                }
            if (s_buffer != null)
            {
                s_buffer.Release();
                s_buffer = null;
            }
            if (s_windowMat != null)
            {
                Object.Destroy(s_windowMat);
                s_windowMat = null;
            }
            if (s_planeMat != null)
            {
                Object.Destroy(s_planeMat);
                s_planeMat = null;
            }
            s_gameCamera = null;
            s_game = null;
        }

        // Every camera's onPreCull; acts only for a window camera inside a window render (its projection is final then).
        static void PreCull(Camera cam)
        {
            if (!PortalRenderer.InWindowRender || cam == null)
                return;
            if (!WindowFlags.ShieldDome)
            {
                // Off: whatever this switch put on the window cameras comes off.
                for (int i = 0; i < s_slots.Count; i++)
                    Off(s_slots[i]);
                return;
            }
            PortalSystem sys = PortalSystem.Instance;
            if (sys == null || !sys.IsWindowCamera(cam))
                return;
            Slot slot = SlotOf(cam);
            try
            {
                int n = Pick(sys, cam, out ShieldDomeImageEffect game);
                if (n == 0)
                {
                    Off(slot);
                    return;
                }
                Fill(game, cam, n);
                On(slot, cam);
                LabWindowPasses++;
            }
            catch (System.Exception e)
            {
                Off(slot);
                WindowLog.WarnEvery("FarShieldDome", 10f, "Win.ShieldDome: the window's dome pass failed: " + e.Message);
            }
        }

        static Slot SlotOf(Camera cam)
        {
            for (int i = 0; i < s_slots.Count; i++)
                if (ReferenceEquals(s_slots[i].Cam, cam))
                    return s_slots[i];
            // Cameras gone (slots the pool destroyed) leave the list now and then.
            if (Time.unscaledTime >= s_sweepAt)
            {
                s_sweepAt = Time.unscaledTime + 10f;
                for (int i = s_slots.Count - 1; i >= 0; i--)
                    if (s_slots[i].Cam == null)
                        s_slots.RemoveAt(i);
            }
            var slot = new Slot { Cam = cam };
            s_slots.Add(slot);
            return slot;
        }

        // The game's effect on its camera (looked up again when the game camera changes).
        static ShieldDomeImageEffect GameEffect(PortalSystem sys)
        {
            Camera main = sys.MainForWindows;
            if (!ReferenceEquals(main, s_gameCamera) || (s_game == null && main != null && Time.frameCount % 60 == 0))
            {
                s_gameCamera = main;
                s_game = main != null ? main.GetComponent<ShieldDomeImageEffect>() : null;
            }
            return s_game;
        }

        // The domes this render's march meets as the game's would standing there, into s_stage; their count.
        static int Pick(PortalSystem sys, Camera cam, out ShieldDomeImageEffect game)
        {
            game = GameEffect(sys);
            // Disabled while the game has no dome (ShieldDomeImageEffect.ToggleActiveImageEffect).
            if (game == null || !game.isActiveAndEnabled)
                return 0;
            ShieldDomeImageEffect.ShieldDome[] domes = s_domes(game);
            PortalWindow w = sys.RenderingWindow;
            if (domes == null || domes.Length == 0 || w == null || w.Partner == null)
                return 0;
            if (s_stage.Length < domes.Length)
                s_stage = new ShieldDomeImageEffect.ShieldDome[Mathf.NextPowerOfTwo(domes.Length)];
            PortalFrame exit = w.Partner.Frame;
            Vector3 eye = cam.transform.position;
            // Every point of the opening lies within this of its centre.
            float open = exit.HalfSize.magnitude;
            float slack = game.m_smoothing + SurfaceSlack;
            float draw = game.m_drawDistance;
            int n = 0;
            for (int i = 0; i < domes.Length; i++)
            {
                ShieldDomeImageEffect.ShieldDome d = domes[i];
                if (!(d.radius > 0f) || Vector3.Distance(eye, d.position) - d.radius - slack > draw)
                    continue;
                if (!OneSide(eye, exit.Center, open, d.position, d.radius, slack))
                {
                    LabDomesLeftOut++;
                    continue;
                }
                s_stage[n++] = d;
            }
            return n;
        }

        // Whether the stretch from the camera to the opening (every segment from `eye` to a point within `open` of
        // `center`) stays on one side of a dome's surface: wholly inside it, or clear of it.
        static bool OneSide(Vector3 eye, Vector3 center, float open, Vector3 c, float r, float slack)
        {
            float inner = r - slack;
            if (inner > 0f && (eye - c).sqrMagnitude < inner * inner)
                return Vector3.Distance(center, c) + open < inner;
            // From outside: the dome stays farther from the camera-to-centre segment than the opening is wide.
            return DistanceToSegment(c, eye, center) - open > r + slack;
        }

        static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float len = ab.sqrMagnitude;
            float t = len > 1e-8f ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / len) : 0f;
            return Vector3.Distance(p, a + ab * t);
        }

        // ShieldDomeImageEffect.OnRenderImage's values for this render, with this camera's rays and the domes picked.
        static void Fill(ShieldDomeImageEffect game, Camera cam, int n)
        {
            Material gameMat = s_material(game);
            if (s_windowMat == null || (gameMat != null && s_windowMat.shader != gameMat.shader))
            {
                if (gameMat == null)
                    throw new System.InvalidOperationException("the game's dome material is missing");
                if (s_windowMat != null)
                    Object.Destroy(s_windowMat);
                s_windowMat = new Material(gameMat) { hideFlags = HideFlags.DontSave, name = "RealPortal window shield domes" };
                // The recorded buffers name the material: recorded again with the new one.
                for (int i = 0; i < s_cb.Length; i++)
                    s_cb[i]?.Clear();
                s_recorded[0] = s_recorded[1] = false;
            }
            Material mat = s_windowMat;
            // The view's corner rays through the window's lens-shifted (and jittered) projection.
            mat.SetVector(s_topLeft, cam.ViewportPointToRay(new Vector3(0f, 1f, 0f)).direction);
            mat.SetVector(s_topRight, cam.ViewportPointToRay(new Vector3(1f, 1f, 0f)).direction);
            mat.SetVector(s_bottomLeft, cam.ViewportPointToRay(new Vector3(0f, 0f, 0f)).direction);
            mat.SetVector(s_bottomRight, cam.ViewportPointToRay(new Vector3(1f, 0f, 0f)).direction);
            mat.SetFloat(s_smoothing, game.m_smoothing);
            mat.SetFloat(s_depthFade, game.m_depthFadeDistance);
            mat.SetFloat(s_edgeGlow, game.m_edgeGlowDistance);
            mat.SetFloat(s_drawDistance, game.m_drawDistance);
            mat.SetTexture(s_colorGradient, s_gradient(game));
            mat.SetFloat(s_shieldTime, Time.time);
            mat.SetTexture(s_noise, game.m_noiseTexture);
            mat.SetTexture(s_curlNoise, game.m_curlNoiseTexture);
            mat.SetFloat(s_noiseSize, game.m_noiseSize);
            mat.SetFloat(s_curlSize, game.m_curlSize);
            mat.SetFloat(s_curlStrength, game.m_curlStrength);
            GraphicsSettingsManager settings = GraphicsSettingsManager.Instance;
            mat.SetInt(s_maxSteps, 32 + (settings != null ? settings.ActiveSettings.m_lod : 0) * 32);
            mat.SetFloat(s_surfaceDistance, game.m_surfaceDistance);
            mat.SetFloat(s_normalBias, game.m_normalBias);
            mat.SetFloat(s_refractStrength, game.m_refractStrength);
            // The domes of this render (a buffer as large as the most there have been; its count is _DomeCount).
            int stride = s_stride(game);
            if (s_buffer == null || s_buffer.count < n || s_buffer.stride != stride)
            {
                s_buffer?.Release();
                s_buffer = new ComputeBuffer(Mathf.Max(n, s_stage.Length), stride, ComputeBufferType.Structured);
            }
            s_buffer.SetData(s_stage, 0, 0, n);
            mat.SetBuffer(s_domeBuffer, s_buffer);
            mat.SetInt(s_domeCount, n);
        }

        static readonly bool[] s_recorded = new bool[2];

        // The game's Blit(src, dest, material, 0) at this point of the window camera's chain, recorded once per HDR mode
        // (the temporary is sized to the camera's target, which changes from render to render).
        static void On(Slot slot, Camera cam)
        {
            bool hdr = cam.allowHDR;
            if (slot.Attached && slot.Hdr != hdr)
                Off(slot);
            int k = hdr ? 1 : 0;
            if (s_cb[k] == null)
                s_cb[k] = new CommandBuffer { name = "RealPortal window shield domes" };
            if (!s_recorded[k])
            {
                CommandBuffer cb = s_cb[k];
                cb.Clear();
                cb.GetTemporaryRT(s_temp, -1, -1, 0, FilterMode.Bilinear, hdr ? RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default);
                cb.Blit(BuiltinRenderTextureType.CameraTarget, s_temp);
                cb.Blit(s_temp, BuiltinRenderTextureType.CameraTarget, s_windowMat, 0);
                cb.ReleaseTemporaryRT(s_temp);
                s_recorded[k] = true;
            }
            if (!slot.Attached)
            {
                cam.AddCommandBuffer(Event, s_cb[k]);
                slot.Attached = true;
                slot.Hdr = hdr;
            }
            // The march reads the camera's depth texture.
            if ((cam.depthTextureMode & DepthTextureMode.Depth) == 0)
                cam.depthTextureMode |= DepthTextureMode.Depth;
        }

        static void Off(Slot slot)
        {
            if (!slot.Attached)
                return;
            slot.Attached = false;
            CommandBuffer cb = s_cb[slot.Hdr ? 1 : 0];
            if (slot.Cam != null && cb != null)
                slot.Cam.RemoveCommandBuffer(Event, cb);
        }
    }
}
