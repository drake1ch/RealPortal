using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace RealPortal
{
    [HarmonyPatch]
    internal static class Patches
    {
        static readonly AccessTools.FieldRef<GameCamera, float> s_distance = AccessTools.FieldRefAccess<GameCamera, float>("m_distance");
        static readonly System.Func<GameCamera, Vector3> s_offsetedEyePos =
            AccessTools.MethodDelegate<System.Func<GameCamera, Vector3>>(AccessTools.Method(typeof(GameCamera), "GetOffsetedEyePos"));
        static readonly System.Action<GameCamera, Vector3, Vector3, float> s_updateNearClipping =
            AccessTools.MethodDelegate<System.Action<GameCamera, Vector3, Vector3, float>>(AccessTools.Method(typeof(GameCamera), "UpdateNearClipping"));

        [HarmonyPostfix, HarmonyPatch(typeof(TeleportWorld), "Awake")]
        static void TeleportWorldAwake(TeleportWorld __instance)
        {
            if (Player.IsPlacementGhost(__instance.gameObject) || (ZNet.instance != null && ZNet.instance.IsDedicated()))
                return;
            PortalSystem.Ensure();
            __instance.gameObject.AddComponent<PortalLink>();
        }

        // A portal with a partner is walked through, never teleported: while its far end loads, or while you may not
        // pass, its wall stops you (and says why, as the game would). Runs first, so later prefixes (Path of
        // Valheiman's portal web and gates) see __runOriginal == false and step aside. Portals without a partner
        // and one-way gates keep the game's teleport.
        [HarmonyPrefix, HarmonyPatch(typeof(TeleportWorld), nameof(TeleportWorld.Teleport)), HarmonyPriority(Priority.First)]
        static bool TeleportWorldTeleport(TeleportWorld __instance, Player player)
        {
            PortalLink link = __instance.GetComponent<PortalLink>();
            if (link == null || link.Vanilla)
                return true;
            if (link.IsActive && player == Player.m_localPlayer && PortalSystem.Instance != null)
                PortalSystem.Instance.SayWhyBlocked(player, link);
            return false;
        }

        // Point lights (fires, torches) switch on by their distance to the player, and the light limit keeps the
        // nearest; through a window the player is also at the far side, so the lights there light up as well.
        [HarmonyTranspiler, HarmonyPatch(typeof(LightLod), nameof(LightLod.UpdateLights))]
        static System.Collections.Generic.IEnumerable<CodeInstruction> LightLodUpdateLights(System.Collections.Generic.IEnumerable<CodeInstruction> code) =>
            ReplaceDistance(code);

        [HarmonyTranspiler, HarmonyPatch(typeof(LightLod), "UpdateLoop", MethodType.Enumerator)]
        static System.Collections.Generic.IEnumerable<CodeInstruction> LightLodUpdateLoop(System.Collections.Generic.IEnumerable<CodeInstruction> code) =>
            ReplaceDistance(code);

        // Win.LightsPerPlace (P9): the light limits ranked per place (the player's, and each live portal's far point) in
        // place of the game's one ranking (PortalSystem.RankLights keeps its timer and early outs). Off, or without the
        // portal system: the game's ranking with the distance above.
        [HarmonyPrefix, HarmonyPatch(typeof(LightLod), nameof(LightLod.UpdateLights))]
        static bool LightLodRankPerPlace(float dt) => !WindowFlags.LightsPerPlace || !PortalSystem.RankLights(dt);

        static System.Collections.Generic.IEnumerable<CodeInstruction> ReplaceDistance(System.Collections.Generic.IEnumerable<CodeInstruction> code)
        {
            System.Reflection.MethodInfo distance = AccessTools.Method(typeof(Vector3), nameof(Vector3.Distance));
            System.Reflection.MethodInfo ours = AccessTools.Method(typeof(PortalSystem), nameof(PortalSystem.LightDistance));
            foreach (CodeInstruction ci in code)
            {
                if (ci.Calls(distance))
                    ci.operand = ours;
                yield return ci;
            }
        }

        static readonly System.Action<UnityStandardAssets.ImageEffects.SunShafts, RenderTexture, RenderTexture> s_shaftsRender =
            AccessTools.MethodDelegate<System.Action<UnityStandardAssets.ImageEffects.SunShafts, RenderTexture, RenderTexture>>(
                AccessTools.Method(typeof(UnityStandardAssets.ImageEffects.SunShafts), "OnRenderImage"));
        static bool s_inShafts;
        static Transform s_windowSun;
        static Material s_windowShaftsMat, s_planeShaftsMat;
        static readonly AccessTools.FieldRef<UnityStandardAssets.ImageEffects.SunShafts, Material> s_shaftsMat =
            AccessTools.FieldRefAccess<UnityStandardAssets.ImageEffects.SunShafts, Material>("sunShaftsMaterial");

        // The game camera's sun shafts, twice while a window shows sky: first from the real sun (the window is an
        // opaque surface to it), then from the sun seen through the window, with only the window's sky as sky.
        // Both run where the game's own pass runs, on the colour-graded image, so the shafts look the same.
        [HarmonyPrefix, HarmonyPatch(typeof(UnityStandardAssets.ImageEffects.SunShafts), "OnRenderImage")]
        static bool SunShaftsTwice(UnityStandardAssets.ImageEffects.SunShafts __instance, RenderTexture source, RenderTexture destination)
        {
            PortalSystem sys = PortalSystem.Instance;
            if (s_inShafts || sys == null)
                return true;
            // Win.FarShafts (P33): each window's own sun, in its own light, over the image before this side's shafts
            // (PortalSystem.PlanShafts planned the chains right after the window batch).
            if (WindowFlags.FarShafts && sys.FarShaftsPlanned(__instance))
                return FarShafts(sys, __instance, source, destination);
            bool twice = sys.WindowSun(__instance, out Vector3 sun, out RenderTexture sky);
            // LeanFixed: the second chain only adds shafts that start from window sky within the shafts' radius of
            // that sun on screen, and none for a sun behind the camera; otherwise it changes nothing and is skipped.
            if (twice && WindowFlags.LeanFixed && WindowFlags.LeanShafts && !ShaftsReachWindows(sys, __instance, sun))
                twice = false;
            // While the camera's depth texture has the windows at the depth seen through them, the real sun's pass
            // reads the windows as the flat surfaces they are: a window's sky is not that sun's sky.
            RenderTexture plane = sys.PlaneDepthFor(__instance);
            if (!twice && plane == null)
                return true;
            Transform realSun = __instance.sunTransform;
            Material realMat = s_shaftsMat(__instance);
            RenderTexture mid = null, inside = null;
            s_inShafts = true;
            try
            {
                if (plane != null && __instance.sunShaftsShader != null)
                {
                    if (s_planeShaftsMat == null || s_planeShaftsMat.shader != __instance.sunShaftsShader)
                        s_planeShaftsMat = new Material(__instance.sunShaftsShader) { hideFlags = HideFlags.DontSave, name = "RealPortal sun shafts, windows flat" };
                    s_planeShaftsMat.SetTexture("_CameraDepthTexture", plane);
                    s_shaftsMat(__instance) = s_planeShaftsMat;
                }
                if (!twice)
                {
                    s_shaftsRender(__instance, source, destination);
                    return false;
                }
                mid = RenderTexture.GetTemporary(source.descriptor);
                s_shaftsRender(__instance, source, mid);
                if (s_windowSun == null)
                {
                    var go = new GameObject("RealPortal sun through window");
                    Object.DontDestroyOnLoad(go);
                    s_windowSun = go.transform;
                }
                s_windowSun.position = sun;
                __instance.sunTransform = s_windowSun;
                // Its own material, whose depth texture is the windows' sky (the shared one keeps the camera's).
                if (s_windowShaftsMat == null || s_windowShaftsMat.shader != __instance.sunShaftsShader)
                    s_windowShaftsMat = new Material(__instance.sunShaftsShader) { hideFlags = HideFlags.DontSave, name = "RealPortal window sun shafts" };
                s_windowShaftsMat.SetTexture("_CameraDepthTexture", sky);
                s_shaftsMat(__instance) = s_windowShaftsMat;
                // That sun's light scatters in the air beyond the portal: its shafts show only in the windows.
                inside = RenderTexture.GetTemporary(source.descriptor);
                s_shaftsRender(__instance, mid, inside);
                PortalSystem.Instance.MixWindowShafts(mid, inside, destination);
            }
            finally
            {
                __instance.sunTransform = realSun;
                s_shaftsMat(__instance) = realMat;
                s_inShafts = false;
                if (mid != null)
                    RenderTexture.ReleaseTemporary(mid);
                if (inside != null)
                    RenderTexture.ReleaseTemporary(inside);
            }
            return false;
        }

        // Win.FarShafts (P33): this side's shafts from the real sun over the image (the windows opaque to them, as before);
        // the windows' pixels taken from the image before them; then each chain (PortalSystem.ShaftChain: the far light
        // seen through some windows, at its place on screen, in its colour) run over the image before shafts with only
        // those windows' sky as sky, and mixed into only their pixels. One chain over every window shown reads the
        // renderer's own sky and mask, as the old second pass did.
        static bool FarShafts(PortalSystem sys, UnityStandardAssets.ImageEffects.SunShafts shafts, RenderTexture source, RenderTexture destination)
        {
            RenderTexture plane = sys.PlaneDepthFor(shafts);
            Texture all = sys.ShaftsAllMask;
            if (all == null)
                return true;
            Transform realSun = shafts.sunTransform;
            Material realMat = s_shaftsMat(shafts);
            Color realColor = shafts.sunColor;
            RenderTexture mid = null, cur = null, inside = null, sky = null;
            s_inShafts = true;
            try
            {
                if (plane != null && shafts.sunShaftsShader != null)
                {
                    if (s_planeShaftsMat == null || s_planeShaftsMat.shader != shafts.sunShaftsShader)
                        s_planeShaftsMat = new Material(shafts.sunShaftsShader) { hideFlags = HideFlags.DontSave, name = "RealPortal sun shafts, windows flat" };
                    s_planeShaftsMat.SetTexture("_CameraDepthTexture", plane);
                    s_shaftsMat(shafts) = s_planeShaftsMat;
                }
                int chains = sys.ShaftChainCount;
                // The real sun behind the camera adds nothing anywhere (the game's chain gets no colour then): with no
                // window sun either, the image is the same in the windows and around them.
                if (chains == 0 && SunBehind(shafts, realSun))
                {
                    s_shaftsRender(shafts, source, destination);
                    return false;
                }
                mid = RenderTexture.GetTemporary(source.descriptor);
                s_shaftsRender(shafts, source, mid);
                s_shaftsMat(shafts) = realMat;
                if (chains == 0)
                {
                    sys.MixShafts(mid, source, all, destination);
                    return false;
                }
                PortalSystem.ShaftChain first = sys.ShaftChainAt(0);
                if (chains == 1 && first.Mask == null)
                {
                    inside = RenderTexture.GetTemporary(source.descriptor);
                    WindowChain(shafts, first, sys.ChainSky(first, out sky), source, inside);
                    sys.MixShafts(mid, inside, all, destination);
                    return false;
                }
                cur = RenderTexture.GetTemporary(source.descriptor);
                sys.MixShafts(mid, source, all, cur);
                for (int i = 0; i < chains; i++)
                {
                    PortalSystem.ShaftChain c = sys.ShaftChainAt(i);
                    inside = RenderTexture.GetTemporary(source.descriptor);
                    WindowChain(shafts, c, sys.ChainSky(c, out sky), source, inside);
                    if (sky != null)
                    {
                        RenderTexture.ReleaseTemporary(sky);
                        sky = null;
                    }
                    RenderTexture next = i == chains - 1 ? destination : RenderTexture.GetTemporary(source.descriptor);
                    sys.MixShafts(cur, inside, c.Mask, next);
                    RenderTexture.ReleaseTemporary(inside);
                    inside = null;
                    RenderTexture.ReleaseTemporary(cur);
                    cur = next != destination ? next : null;
                }
            }
            finally
            {
                shafts.sunTransform = realSun;
                s_shaftsMat(shafts) = realMat;
                shafts.sunColor = realColor;
                s_inShafts = false;
                if (mid != null)
                    RenderTexture.ReleaseTemporary(mid);
                if (cur != null)
                    RenderTexture.ReleaseTemporary(cur);
                if (inside != null)
                    RenderTexture.ReleaseTemporary(inside);
                if (sky != null)
                    RenderTexture.ReleaseTemporary(sky);
            }
            return false;
        }

        // Where SunShafts.OnRenderImage puts its sun (its camera's viewport point of sunTransform): behind the camera.
        static bool SunBehind(UnityStandardAssets.ImageEffects.SunShafts shafts, Transform sun)
        {
            if (sun == null)
                return false;
            if (shafts != s_shaftsOwner)
            {
                s_shaftsOwner = shafts;
                s_shaftsCamera = shafts.GetComponent<Camera>();
            }
            return s_shaftsCamera != null && s_shaftsCamera.WorldToViewportPoint(sun.position).z < 0f;
        }

        // One chain's shafts over `source` into `into`: from its sun, in its colour, with `sky` as the only sky.
        static void WindowChain(UnityStandardAssets.ImageEffects.SunShafts shafts, PortalSystem.ShaftChain c, Texture sky, RenderTexture source, RenderTexture into)
        {
            if (s_windowSun == null)
            {
                var go = new GameObject("RealPortal sun through window");
                Object.DontDestroyOnLoad(go);
                s_windowSun = go.transform;
            }
            s_windowSun.position = c.Sun;
            shafts.sunTransform = s_windowSun;
            if (s_windowShaftsMat == null || s_windowShaftsMat.shader != shafts.sunShaftsShader)
                s_windowShaftsMat = new Material(shafts.sunShaftsShader) { hideFlags = HideFlags.DontSave, name = "RealPortal window sun shafts" };
            s_windowShaftsMat.SetTexture("_CameraDepthTexture", sky);
            s_shaftsMat(shafts) = s_windowShaftsMat;
            shafts.sunColor = c.Color;
            s_shaftsRender(shafts, source, into);
        }

        static UnityStandardAssets.ImageEffects.SunShafts s_shaftsOwner;
        static Camera s_shaftsCamera;
        // The sun's screen radius (SunShafts.maxRadius, in viewport units as its shader measures) plus a texel of its
        // downsampled buffers.
        internal const float ShaftsSlack = 0.02f;

        // Whether the second sun shafts chain (from `sun`, the sun seen through the largest window) can put anything
        // into a window. Its source is the windows' sky (SkyDepthTexture: every shown window with a view depth) within
        // maxRadius of that sun's viewport point; a sun behind the camera gets no colour. The windows' screen rects
        // come from their renderers' bounds this frame (a stale window's view is laid where it stands now).
        static bool ShaftsReachWindows(PortalSystem sys, UnityStandardAssets.ImageEffects.SunShafts shafts, Vector3 sun)
        {
            if (shafts != s_shaftsOwner)
            {
                s_shaftsOwner = shafts;
                s_shaftsCamera = shafts.GetComponent<Camera>();
            }
            Camera cam = s_shaftsCamera;
            if (cam == null)
                return true;
            Vector3 at = cam.WorldToViewportPoint(sun);
            if (at.z < 0f)
                return false;
            float reach = shafts.maxRadius + ShaftsSlack;
            Matrix4x4 vp = cam.projectionMatrix * cam.worldToCameraMatrix;
            IReadOnlyList<PortalLink> links = sys.Active;
            for (int i = 0; i < links.Count; i++)
            {
                PortalWindow w = links[i].Window;
                if (w == null || !w.RenderedThisFrame || w.DepthTexture == null)
                    continue;
                Rect r = ScreenRectOf(w.Renderer.bounds, vp);
                float dx = Mathf.Max(0f, Mathf.Max(r.xMin - at.x, at.x - r.xMax));
                float dy = Mathf.Max(0f, Mathf.Max(r.yMin - at.y, at.y - r.yMax));
                if (dx * dx + dy * dy < reach * reach)
                    return true;
            }
            return false;
        }

        // Viewport rect of a world box; the whole screen when the box reaches behind the camera.
        internal static Rect ScreenRectOf(Bounds b, Matrix4x4 vp)
        {
            Vector3 c = b.center, e = b.extents;
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                var p = new Vector4(c.x + ((i & 1) != 0 ? e.x : -e.x), c.y + ((i & 2) != 0 ? e.y : -e.y), c.z + ((i & 4) != 0 ? e.z : -e.z), 1f);
                Vector4 h = vp * p;
                if (h.w <= 1e-4f)
                    return new Rect(0f, 0f, 1f, 1f);
                float x = h.x / h.w * 0.5f + 0.5f, y = h.y / h.w * 0.5f + 0.5f;
                x0 = Mathf.Min(x0, x);
                x1 = Mathf.Max(x1, x);
                y0 = Mathf.Min(y0, y);
                y1 = Mathf.Max(y1, y);
            }
            return Rect.MinMaxRect(x0, y0, x1, y1);
        }

        // Win.DepthOnly: the SSAO copy on the window camera runs without its temporal filter. The filter needs the
        // camera's motion vectors (the window camera no longer renders them), and with one camera for all windows its
        // history and last view-projection were another window's anyway. Set right before each window render, so
        // whatever PortalSystem copies from the game's SSAO, Amplify only ever builds with the filter off.
        // The shared camera only: a slot camera's filter is set by AoSync right before its render (on only for a sticky
        // slot under Win.AoTemporal, with its own history and motion vectors), and is left as AoSync set it.
        [HarmonyPrefix, HarmonyPatch(typeof(AmplifyOcclusionEffect), "OnPreRender")]
        static void WindowOcclusionPreRender(AmplifyOcclusionEffect __instance)
        {
            if (!PortalRenderer.InWindowRender || !WindowFlags.DepthOnly || !__instance.FilterEnabled)
                return;
            Camera wc = PortalSystem.Instance != null ? PortalSystem.Instance.WindowCamera : null;
            if (wc != null && __instance.gameObject == wc.gameObject)
                __instance.FilterEnabled = false;
        }

        static readonly AccessTools.FieldRef<UnityStandardAssets.ImageEffects.DepthOfField, Material> s_dofMat =
            AccessTools.FieldRefAccess<UnityStandardAssets.ImageEffects.DepthOfField, Material>("dofHdrMaterial");
        static Material s_windowDofMat;
        static Material s_realDofMat;

        // The game's depth of field reads the camera's depth, where a window is the flat portal plane: everything
        // seen through it would blur as if it stood there. For its pass it gets a material of its own whose depth
        // texture has the windows at the depth seen through them.
        [HarmonyPrefix, HarmonyPatch(typeof(UnityStandardAssets.ImageEffects.DepthOfField), "OnRenderImage")]
        static void DepthOfFieldBefore(UnityStandardAssets.ImageEffects.DepthOfField __instance)
        {
            s_realDofMat = null;
            RenderTexture depth = PortalSystem.Instance != null ? PortalSystem.Instance.ViewDepthFor(__instance) : null;
            if (depth == null || __instance.dofHdrShader == null)
                return;
            if (s_windowDofMat == null || s_windowDofMat.shader != __instance.dofHdrShader)
                s_windowDofMat = new Material(__instance.dofHdrShader) { hideFlags = HideFlags.DontSave, name = "RealPortal depth of field through windows" };
            s_windowDofMat.SetTexture("_CameraDepthTexture", depth);
            s_realDofMat = s_dofMat(__instance);
            s_dofMat(__instance) = s_windowDofMat;
        }

        [HarmonyFinalizer, HarmonyPatch(typeof(UnityStandardAssets.ImageEffects.DepthOfField), "OnRenderImage")]
        static void DepthOfFieldAfter(UnityStandardAssets.ImageEffects.DepthOfField __instance)
        {
            if (s_realDofMat == null)
                return;
            s_dofMat(__instance) = s_realDofMat;
            s_realDofMat = null;
        }

        static readonly AccessTools.FieldRef<CameraEffects, UnityStandardAssets.ImageEffects.DepthOfField> s_effectsDof =
            AccessTools.FieldRefAccess<CameraEffects, UnityStandardAssets.ImageEffects.DepthOfField>("m_dof");

        // The depth of field's autofocus casts straight ahead from the camera; looking into (or carried through) a
        // portal, it would focus on the portal instead of what is seen through it, and refocus with a jolt when
        // the camera comes back to the player's side. The same cast, carried on through live portals.
        [HarmonyPrefix, HarmonyPatch(typeof(CameraEffects), "UpdateDOF")]
        static bool CameraEffectsUpdateDof(CameraEffects __instance)
        {
            PortalSystem sys = PortalSystem.Instance;
            UnityStandardAssets.ImageEffects.DepthOfField dof = s_effectsDof(__instance);
            if (sys == null || sys.Active.Count == 0 || dof == null || !dof.enabled || !__instance.m_dofAutoFocus)
                return true;
            Transform t = __instance.transform;
            float focus = __instance.m_dofMaxDistance;
            if (sys.RaycastThrough(t.position, t.forward, __instance.m_dofMaxDistance, __instance.m_dofRayMask.value, out float hit))
                focus = hit;
            Player p = Player.m_localPlayer;
            if ((p == null || p.GetControlledShip() != null) && focus < __instance.m_dofMinDistanceShip)
                focus = __instance.m_dofMinDistanceShip;
            if (focus < __instance.m_dofMinDistance)
                focus = __instance.m_dofMinDistance;
            dof.focalLength = Mathf.Lerp(dof.focalLength, focus, 0.2f);
            return false;
        }

        static readonly AccessTools.FieldRef<UnityEngine.PostProcessing.PostProcessingComponentBase, UnityEngine.PostProcessing.PostProcessingContext> s_ppContext =
            AccessTools.FieldRefAccess<UnityEngine.PostProcessing.PostProcessingComponentBase, UnityEngine.PostProcessing.PostProcessingContext>("context");
        static readonly int s_cameraMotionVectors = Shader.PropertyToID("_CameraMotionVectorsTexture");

        // The game's TAA runs in OnRenderImage, where the camera's own motion vectors are bound again: its material
        // gets the ones fixed up for the windows (and for the frame the camera is carried through a portal), or
        // the view through a window jitters and the switch to the game's own view shakes.
        [HarmonyPrefix, HarmonyPatch(typeof(UnityEngine.PostProcessing.TaaComponent), nameof(UnityEngine.PostProcessing.TaaComponent.Render))]
        static void TaaMotion(UnityEngine.PostProcessing.TaaComponent __instance)
        {
            UnityEngine.PostProcessing.PostProcessingContext ctx = s_ppContext(__instance);
            if (ctx == null || ctx.materialFactory == null)
                return;
            Material taa = ctx.materialFactory.Get("Hidden/Post FX/Temporal Anti-aliasing");
            RenderTexture fixedUp = PortalSystem.Instance != null ? PortalSystem.Instance.MotionFor(ctx.camera) : null;
            // Once set on the material it stays: without ours this frame, the camera's own (as bound globally).
            taa.SetTexture(s_cameraMotionVectors, fixedUp != null ? fixedUp : Shader.GetGlobalTexture(s_cameraMotionVectors));
            // The depth it dilates its motion vectors by (Win.TaaViewDepth), and the lab's look at what it reads
            // (Docs\RIPPLE-PLAN.md L3): the game camera's TAA only, and only once something wants either.
            if ((WindowFlags.TaaViewDepth || PortalSystem.LabTaaDepth != 0 || s_taaDepthSet || PortalSystem.LabTaaInputs) && PortalSystem.Instance != null
                && ctx.camera == PortalSystem.Instance.MainForWindows)
                TaaDepthAndInputs(taa, ctx.camera, fixedUp);
        }

        static readonly int s_cameraDepthTexture = Shader.PropertyToID("_CameraDepthTexture");
        // The depth texture last put on the game camera's TAA material (TaaDepth), on which frame, and whether one ever was.
        static Texture s_taaDepth;
        static int s_taaDepthFrame = -1;
        static bool s_taaDepthSet;

        static void TaaDepthAndInputs(Material taa, Camera cam, RenderTexture fixedUp)
        {
            // Win.TaaViewDepth (RIPPLE-PLAN.md §4.2): the depth seen through the windows, so the closest-depth search picks
            // the motion of what a window shows, not always the same neighbour on its flat surface; a frame without it, the
            // global as before. Set every frame (the material keeps what it was given).
            if (WindowFlags.TaaViewDepth)
            {
                RenderTexture view = PortalSystem.Instance.ViewDepthFor(cam);
                TaaDepth(taa, view != null ? view : Shader.GetGlobalTexture(s_cameraDepthTexture));
            }
            // Lab (L3): the depth PortalSystem.LabTaaDepth chooses, over whatever was put there before.
            if (PortalSystem.LabTaaDepth != 0)
                LabTaaDepthBind(taa, cam);
            // A depth put on the material once stays there: on a frame nothing put one (the lab switch back at 0), the
            // global again, as the material read before.
            if (s_taaDepthSet && s_taaDepthFrame != Time.frameCount)
                TaaDepth(taa, Shader.GetGlobalTexture(s_cameraDepthTexture));
            // Lab (L3): what TAA gets, counted while PortalSystem.LabTaaInputs is on.
            if (PortalSystem.LabTaaInputs)
                LabTaaCount(cam, fixedUp);
        }

        // A depth texture for the game camera's TAA material (its _CameraDepthTexture, which it dilates its motion vectors
        // by), from TaaDepthAndInputs ahead of the lab's (which then wins). A frame nobody calls it, the global goes back
        // on. The L3 counters read it as TAA's depth.
        internal static void TaaDepth(Material taa, Texture depth)
        {
            if (depth == null)
                return;
            taa.SetTexture(s_cameraDepthTexture, depth);
            s_taaDepth = depth;
            s_taaDepthFrame = Time.frameCount;
            s_taaDepthSet = true;
        }

        // Lab (L3): PortalSystem.LabTaaDepth's depth on the TAA material, the global where it has none this frame.
        static void LabTaaDepthBind(Material taa, Camera cam)
        {
            RenderTexture chosen = PortalSystem.Instance != null ? PortalSystem.Instance.LabTaaDepthFor(cam) : null;
            TaaDepth(taa, chosen != null ? chosen : Shader.GetGlobalTexture(s_cameraDepthTexture));
        }

        // Lab (L3): this TAA frame's inputs, bound globally and as its material reads them (no allocation).
        static void LabTaaCount(Camera cam, RenderTexture fixedUp)
        {
            PortalSystem sys = PortalSystem.Instance;
            if (sys == null)
                return;
            Texture depth = Shader.GetGlobalTexture(s_cameraDepthTexture), motion = Shader.GetGlobalTexture(s_cameraMotionVectors);
            sys.CountTaaInputs(cam, depth, s_taaDepthSet ? s_taaDepth : depth, motion, fixedUp != null ? fixedUp : motion);
        }

        // Weather, mist and cloud layers that follow the camera: while the camera arm reaches through a portal the
        // picture is the view through it, taken from the camera's place on the player's side; they follow that
        // place, or they would sit by the portal's far end and jump when the camera comes back.
        [HarmonyPrefix, HarmonyPatch(typeof(FollowPlayer), "LateUpdate")]
        static void FollowPlayerBefore(FollowPlayer __instance, out Vector3 __state)
        {
            __state = __instance.transform.position;
            PortalSystem.SeenFollowers.Add(__instance);
        }

        [HarmonyPostfix, HarmonyPatch(typeof(ParticleMist), "Update")]
        static void ParticleMistSeen(ParticleMist __instance) => FarMist.Seen.Add(__instance);

        [HarmonyPostfix, HarmonyPatch(typeof(FollowPlayer), "LateUpdate")]
        static void FollowPlayerAfter(FollowPlayer __instance, Vector3 __state)
        {
            PortalSystem sys = PortalSystem.Instance;
            if (sys == null)
                return;
            FollowPlayerThroughPortal(sys, __instance);
            FarEnvironment.PlaceAway(__instance);
            sys.CarryParticles(__instance, __state, __instance.transform.position);
        }

        static void FollowPlayerThroughPortal(PortalSystem sys, FollowPlayer __instance)
        {
            Camera cam = Utils.GetMainCamera();
            if (cam == null || Player.m_localPlayer == null || __instance.m_follow == FollowPlayer.Type.Player
                || sys.CameraThroughFrame < Time.frameCount - 1 || PortalSystem.LabFollowersStay)
                return;
            Vector3 camera = sys.CameraMatrix.inverse.MultiplyPoint3x4(cam.transform.position);
            Vector3 to = __instance.m_follow == FollowPlayer.Type.Camera || GameCamera.InFreeFly() ? camera
                : (camera + Player.m_localPlayer.transform.position) * 0.5f;
            Transform t = __instance.transform;
            if (__instance.m_lockYPos)
                to.y = t.position.y;
            if (to.y > __instance.m_maxYPos)
                to.y = __instance.m_maxYPos;
            t.position = to;
        }

        static readonly int s_aoUVToView = Shader.PropertyToID("_AO_UVToView");
        // Which way the lens shift enters Amplify's screen -> view mapping: measured in the lab, both axes reversed
        // (its view space has the camera looking down +z).
        internal static Vector2 LabAOShiftSign = new Vector2(-1f, -1f);

        // Amplify Occlusion rebuilds view positions from the field of view alone, as if the frustum were centred;
        // the window camera's is shifted to the window's rect (and by the TAA jitter). Use its real projection.
        // Every window camera: the shared one and each slot camera (Win.CameraSlots), all lens-shifted the same way.
        [HarmonyPostfix, HarmonyPatch(typeof(AmplifyOcclusion.AmplifyOcclusionCommon), nameof(AmplifyOcclusion.AmplifyOcclusionCommon.UpdateGlobalShaderConstants))]
        static void OcclusionConstants(UnityEngine.Rendering.CommandBuffer cb, Camera aCamera)
        {
            if (PortalSystem.Instance == null || !PortalSystem.Instance.IsWindowCamera(aCamera))
                return;
            Matrix4x4 p = aCamera.projectionMatrix;
            float sx = LabAOShiftSign.x, sy = LabAOShiftSign.y;
            cb.SetGlobalVector(s_aoUVToView, new Vector4(2f / p.m00, 2f / p.m11, (sx * p.m02 - 1f) / p.m00, (sy * p.m12 - 1f) / p.m11));
        }

        static readonly AccessTools.FieldRef<TeleportWorld, bool> s_hadTarget = AccessTools.FieldRefAccess<TeleportWorld, bool>("m_hadTarget");
        static readonly AccessTools.FieldRef<TeleportWorld, ZNetView> s_portalView = AccessTools.FieldRefAccess<TeleportWorld, ZNetView>("m_nview");

        // The portal's own look follows the real portal, not the vanilla connection: the "connected" burst and the
        // glowing frame come when it goes live (its far end loaded). The swirl that fills an open vanilla portal as you
        // come near fills this one, by Window/PortalEffects, only when someone may not pass (with ore, say: it stands
        // over the window then, with the wall behind it), or as in the game when a player who may pass stands close.
        [HarmonyPrefix, HarmonyPatch(typeof(TeleportWorld), "UpdatePortal")]
        static bool TeleportWorldUpdatePortal(TeleportWorld __instance)
        {
            PortalLink link = __instance.GetComponent<PortalLink>();
            if (link == null || link.Vanilla)
                return true;
            ZNetView view = s_portalView(__instance);
            if (view == null || !view.IsValid() || __instance.m_proximityRoot == null)
                return false;
            bool active = link.IsActive;
            if (active && !s_hadTarget(__instance))
                __instance.m_connected.Create(__instance.transform.position, __instance.transform.rotation);
            s_hadTarget(__instance) = active;
            bool shown = Plugin.PortalEffects.Value == PortalEffectsMode.Vanilla
                ? active && ClosestMayPass(__instance, link)
                : active && PortalSystem.Instance != null && SomeoneStopped(__instance, link);
            if (__instance.m_target_found != null)
                __instance.m_target_found.SetActive(shown);
            return false;
        }

        // The game's own test (the closest player within activation range may teleport), plus this mod's rules for
        // you yourself (NoPortals, a boss, another mod's say), which decide whether the wall lets you through.
        static bool ClosestMayPass(TeleportWorld portal, PortalLink link)
        {
            Player closest = Player.GetClosestPlayer(portal.m_proximityRoot.position, portal.m_activationRange);
            if (closest == null || !closest.IsTeleportable(portal.m_allowAllItems))
                return false;
            return closest != Player.m_localPlayer || PortalSystem.Instance == null || PortalSystem.Instance.PlayerMayPass(closest, link);
        }

        // You or a creature close to the opening (either side) that its wall stops.
        static bool SomeoneStopped(TeleportWorld portal, PortalLink link)
        {
            float range = portal.m_activationRange + 1f;
            Vector3 c = link.Frame.Center;
            foreach (Character ch in Character.GetAllCharacters())
            {
                if (ch == null || ch.IsDead() || (ch.transform.position - c).sqrMagnitude > range * range)
                    continue;
                if (ch == Player.m_localPlayer)
                {
                    if (!PortalSystem.Instance.PlayerMayPass((Player)ch, link))
                        return true;
                }
                else if (!ch.IsPlayer() && !FarLife.MayCross(ch))
                    return true;
            }
            return false;
        }

        // The windows render right before the game camera does (Camera.onPreCull), after every LateUpdate has
        // posed the scene; here only note which camera that is.
        [HarmonyPostfix, HarmonyPatch(typeof(GameCamera), "LateUpdate")]
        static void GameCameraLateUpdate(GameCamera __instance)
        {
            if (PortalSystem.Instance != null)
                PortalSystem.Instance.SetGameCamera(Plugin.Enabled.Value ? __instance : null);
            // Win.LayerCull on the game camera (and the distances the window camera gets).
            if (__instance != s_gameCamera)
            {
                s_gameCamera = __instance;
                s_gameCameraCamera = __instance.GetComponent<Camera>();
            }
            WindowLayerCull.Update(s_gameCameraCamera);
            // Window Pipeline v2 (S3): the pose is final here, so the window renders of this frame are planned now and
            // rendered at the game camera's onPreCull (returns at once while Win.Tiers is not effective). What the far
            // side made wrong in the cached views (weather switch, lightning, the area's load stage) is told first, so
            // this plan already renders those windows.
            if (PortalSystem.Instance != null)
            {
                PortalSystem.Instance.InvalidateCachedViews(s_gameCameraCamera);
                PortalSystem.Instance.PlanWindows(__instance);
            }
        }

        static GameCamera s_gameCamera;
        static Camera s_gameCameraCamera;

        // Grass and other instanced clutter is drawn only where the game camera looks; keep it where a window
        // looks too, or the far side shows bare ground. (Patching Utils.InsideMainCamera itself does nothing:
        // the JIT inlines that small method into this caller.)
        // State: how far the LOD distances were moved (NaN: frustum culling was not touched).
        // Lab: grass renderers let through for a window's view so far.
        internal static int LabGrassThroughWindow, LabGrassDrawn;
        // Lab: those let through in one frame (LabGrassFrame).
        internal static int LabGrassFrame = -1, LabGrassInFrame;
        internal static string LabGrassSample = "";

        [HarmonyPrefix, HarmonyPatch(typeof(InstanceRenderer), nameof(InstanceRenderer.CustomUpdate))]
        static bool InstanceRendererUpdate(InstanceRenderer __instance, BoundingSphere ___m_bounds, bool ___m_dirtyBounds, ref float deltaTime, out float __state)
        {
            __state = float.NaN;
            PortalSystem sys = PortalSystem.Instance;
            if (sys == null)
                return true;
            // Far.GrassGate: a far area's patch is updated only in frames in which a window onto that area renders, or
            // the game camera could draw it (FarGrassGate); a skipped one is neither tested nor drawn.
            if (FarFlags.GrassGate && FarGrassGate.Holds(__instance, in ___m_bounds, ___m_dirtyBounds, ref deltaTime))
                return false;
            // Win.BaseHidesFar (P14.2), with Grass.Targeted in force: the game camera's update stays the game's (none for a
            // far patch), and the window's patches go to the window cameras alone (FarGrassSubmit).
            if (WindowFlags.BaseHidesFar && FarGrassSubmit.SlotOnly)
                return FarGrassSubmit.SlotOnlyUpdate(sys, __instance, ___m_bounds, ___m_dirtyBounds, ref __state);
            if (!__instance.m_frustumCull || ___m_dirtyBounds)
                return true;
            // Grass.ViewNear: grass thinned by distance only asks the window views within its LOD's reach.
            Vector3 viewPos;
            bool inside = FarFlags.GrassViewNear && __instance.m_useLod
                ? sys.InsideWindowViewNear(___m_bounds, __instance.transform.position, __instance.m_lodMaxDistance, __instance.m_useXZLodDistance, out viewPos)
                : sys.InsideWindowView(___m_bounds, out viewPos);
            if (!inside)
                return true;
            __instance.m_frustumCull = false;
            __state = 0f;
            LabGrassThroughWindow++;
            if (LabGrassFrame != Time.frameCount)
            {
                LabGrassFrame = Time.frameCount;
                LabGrassInFrame = 0;
            }
            LabGrassInFrame++;
            // Thin the grass by the nearest camera that sees it: a window's, when the game camera is farther away
            // on the other side of the portal. (Being inside the game camera's frustum says nothing: grass
            // kilometres ahead of it is inside too, and gets no blades at that distance.)
            Camera main = Utils.GetMainCamera();
            if (!__instance.m_useLod || main == null)
                return true;
            Vector3 here = __instance.transform.position;
            float mainDist = __instance.m_useXZLodDistance ? Utils.DistanceXZ(main.transform.position, here) : Vector3.Distance(main.transform.position, here);
            float viewDist = __instance.m_useXZLodDistance ? Utils.DistanceXZ(viewPos, here) : Vector3.Distance(viewPos, here);
            if (viewDist >= mainDist)
                return true;
            __state = mainDist - viewDist;
            __instance.m_lodMinDistance += __state;
            __instance.m_lodMaxDistance += __state;
            return true;
        }

        [HarmonyPostfix, HarmonyPatch(typeof(InstanceRenderer), nameof(InstanceRenderer.CustomUpdate))]
        static void InstanceRendererUpdateDone(InstanceRenderer __instance, float __state, float ___m_lodCount, bool ___m_useLod, int ___m_instanceCount)
        {
            if (float.IsNaN(__state))
                return;
            // Win.BaseHidesFar: the game's own update of a window's patch; whether it drew it for the game camera.
            if (float.IsNegativeInfinity(__state))
            {
                FarGrassSubmit.Drawn(__instance, ___m_instanceCount > 0 && (!___m_useLod || (int)___m_lodCount > 0));
                return;
            }
            if (!___m_useLod || ___m_lodCount >= 1f)
                LabGrassDrawn++;
            else
                LabGrassSample = $"lod {___m_lodCount:0.0} of {__instance.m_lodMinDistance:0.0}..{__instance.m_lodMaxDistance:0.0} shifted {__state:0.0}, " +
                                 $"main {Utils.DistanceXZ(Utils.GetMainCamera().transform.position, __instance.transform.position):0.0} m";
            __instance.m_frustumCull = true;
            __instance.m_lodMinDistance -= __state;
            __instance.m_lodMaxDistance -= __state;
        }

        // Far.GrassGate: after GameCameraLateUpdate has made this frame's window plan (Priority.Last: the postfix above runs
        // first), the far grass the Update pass held back for places that plan renders runs its update, before any camera
        // of the frame renders (FarGrassGate.AfterPlan).
        [HarmonyPostfix, HarmonyPatch(typeof(GameCamera), "LateUpdate"), HarmonyPriority(Priority.Last)]
        static void GrassGateAfterPlan() => FarGrassGate.AfterPlan();

        // Same arm as the game (offset eye -> back along the view), but followed through real portals.
        [HarmonyPrefix, HarmonyPatch(typeof(GameCamera), "GetCameraPosition")]
        static bool GameCameraPosition(GameCamera __instance, float dt, ref Vector3 pos, ref Quaternion rot)
        {
            PortalSystem sys = PortalSystem.Instance;
            Player p = Player.m_localPlayer;
            if (sys == null || sys.Active.Count == 0 || !Plugin.CameraThrough.Value || p == null || p.InIntro())
                return true;
            Vector3 eye = s_offsetedEyePos(__instance);
            float dist = s_distance(__instance);
            Vector3 back = -p.m_eye.transform.forward;
            if (__instance.m_smoothYTilt)
                dist = Mathf.Lerp(dist, 1.5f, Utils.SmoothStep(0f, -0.5f, back.y));
            Vector3 start = p.transform.position + Vector3.up * PortalSystem.PlayerRefHeight;
            float width = __instance.m_raycastWidth;
            int mask = __instance.m_blockCameraMask.value;
            Vector3 end = sys.SteerCamera(eye, sys.RedirectArm(eye, eye + back * dist, width), width);
            if (!sys.TraceCamera(start, eye, end, width, mask, out Vector3 camPos, out Quaternion turn, out Vector3 armStart))
            {
                if (!sys.CameraBeside(eye, end, width, mask, out camPos))
                    return true;
                s_updateNearClipping(__instance, eye, camPos, dt);
                pos = camPos;
                rot = p.m_eye.transform.rotation;
                return false;
            }
            s_updateNearClipping(__instance, armStart, camPos, dt);
            sys.CameraThroughFrame = Time.frameCount;
            pos = camPos;
            rot = turn * p.m_eye.transform.rotation;
            return false;
        }

        // Around the whole camera placement (the game's collision code and ours): portals near the player
        // do not block the camera.
        [HarmonyPrefix, HarmonyPatch(typeof(GameCamera), "GetCameraPosition"), HarmonyPriority(Priority.First)]
        static void GameCameraPositionHide()
        {
            PortalSystem sys = PortalSystem.Instance;
            Player p = Player.m_localPlayer;
            if (sys == null || p == null)
                return;
            Vector3 at = p.transform.position;
            foreach (PortalLink link in sys.Active)
                if ((link.transform.position - at).sqrMagnitude < 15f * 15f)
                    link.HideFromCamera();
        }

        [HarmonyFinalizer, HarmonyPatch(typeof(GameCamera), "GetCameraPosition")]
        static void GameCameraPositionRestore()
        {
            PortalSystem sys = PortalSystem.Instance;
            if (sys == null)
                return;
            foreach (PortalLink link in sys.Active)
                link.RestoreAfterCamera();
        }

        [HarmonyPrefix, HarmonyPatch(typeof(Projectile), "FixedUpdate")]
        static void ProjectileFixedUpdate(Projectile __instance, ZNetView ___m_nview, bool ___m_didHit, ref Vector3 ___m_vel,
                                          ref bool ___m_haveStartPoint)
        {
            PortalSystem sys = PortalSystem.Instance;
            if (sys == null || sys.Active.Count == 0 || !Plugin.Projectiles.Value || ___m_didHit
                || ___m_nview == null || !___m_nview.IsValid() || !___m_nview.IsOwner())
                return;
            // Predict this step the way Projectile.FixedUpdate moves (gravity, then quadratic drag).
            float dt = Time.fixedDeltaTime;
            Vector3 v = ___m_vel + Vector3.down * (__instance.m_gravity * dt);
            v += Mathf.Pow(v.magnitude, 2f) * __instance.m_drag * dt * -v.normalized;
            Transform t = __instance.transform;
            if (!sys.TryMoveProjectile(t, ref ___m_vel, t.position + v * dt, out Matrix4x4 m))
                return;
            // Otherwise this step's hit ray would run from the spawn point across the world.
            ___m_haveStartPoint = false;
            __instance.m_startPoint = m.MultiplyPoint3x4(__instance.m_startPoint);
            foreach (TrailRenderer trail in __instance.GetComponentsInChildren<TrailRenderer>())
                trail.Clear();
        }

        [HarmonyPostfix, HarmonyPatch(typeof(ZNetScene), "OnDestroy")]
        static void ZNetSceneDestroy()
        {
            PortalSystem.Instance?.Clear();
        }

        // The game destroys every object it did not list around the player this tick: the far portals' areas
        // are listed too.
        // With far areas loaded that check runs over every object they hold, tens of thousands, many times a second:
        // then it runs every sixth time (what is to go goes a fraction of a second later).
        // Far.KeepAliveFast: on those same ticks, only the loaded objects no far area lists are looked at, and the same
        // ones go (RemoteAreas.KeepAliveFast); the full pass below should it decline.
        // Far.KeepAliveSlice: a sixth of the loaded objects looked at every tick, by the sector each stands in, and what
        // is not kept taken out the game's way (RemoteAreas.KeepAliveSliced); the full pass below while an area drains.
        // FarWorld (Far.V2 with Far.Registry): the far areas are kept by the registry's sectors as they are now, each
        // object once however many areas reach it (the full pass: SectorRegistry.CollectKept; the fast pass: what came
        // into an area since its listing), in place of their relisted lists and Track; the same ticks either way.
        // Far.KeepAliveEveryTick (P37): the fast pass on every tick, so what leaves the player's ring goes on the game's
        // own tick; the full pass it may decline to stays at every sixth. While an area drains the sixth tick stays too:
        // each call gives up a tick's share of a drain (Far.DrainCalm's budget is six ticks' worth), and on every tick the
        // drain would run six times as fast, its destroys in every tick instead of every sixth.
        [HarmonyPrefix, HarmonyPatch(typeof(ZNetScene), "RemoveObjects")]
        static bool ZNetSceneRemoveObjects(ZNetScene __instance, List<ZDO> currentNearObjects, List<ZDO> currentDistantObjects)
        {
            RemoteAreas areas = PortalSystem.Instance?.Areas;
            if (areas == null)
                return true;
            if (FarFlags.KeepAliveFast && areas.Count > 0)
            {
                bool sixth = ++s_removeTick % 6 == 0;
                if (!sixth && (!FarFlags.KeepAliveEveryTick || Draining(areas)))
                    return false;
                if (areas.KeepAliveFast(__instance, currentNearObjects, currentDistantObjects))
                    return false;
                if (!sixth)
                    return false;
                areas.KeepAlive(currentNearObjects);
                return true;
            }
            if (FarFlags.KeepAliveSlice && areas.KeepAliveSliced(__instance, currentNearObjects, currentDistantObjects))
                return false;
            if (areas.Count > 0 && ++s_removeTick % 6 != 0)
                return false;
            areas.KeepAlive(currentNearObjects);
            return true;
        }

        static int s_removeTick;

        // Far.KeepAliveEveryTick: some far area gives its objects up (RemoteAreas.Counts leaves draining ones out).
        static bool Draining(RemoteAreas areas)
        {
            areas.Counts(out int standing, out _, out _, out _);
            return standing < areas.Count;
        }

        // Far.KeepAliveFast: every object the game loads, for the set of those no far area lists (all its removal
        // looks at). Nothing while it is off.
        [HarmonyPostfix, HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.AddInstance))]
        static void ZNetSceneAddInstance(ZNetScene __instance, ZDO zdo) => PortalSystem.Instance?.Areas.InstanceAdded(__instance, zdo);

        // The time-of-day weights the game lit the world with, for a window's own weather.
        // Getting wet and cold, and the weather's sound, by where you stand (FarEnvironment.PlayerWeather).
        [HarmonyPostfix, HarmonyPatch(typeof(EnvMan), "FixedUpdate")]
        static void EnvManFixedUpdate() => FarEnvironment.PlayerFlags();

        [HarmonyPostfix, HarmonyPatch(typeof(EnvMan), nameof(EnvMan.GetCurrentBiome))]
        static void CurrentBiome(ref Heightmap.Biome __result) => FarEnvironment.PlayerBiome(ref __result);

        [HarmonyPrefix, HarmonyPatch(typeof(AudioMan), nameof(AudioMan.QueueAmbientLoop))]
        static bool AmbientLoop(AudioMan __instance, ref AudioClip clip, ref float vol) => FarEnvironment.AmbientLoop(__instance, ref clip, ref vol);

        [HarmonyPrefix, HarmonyPatch(typeof(AudioMan), nameof(AudioMan.StopAmbientLoop))]
        static bool AmbientStop(AudioMan __instance) => FarEnvironment.AmbientStop(__instance);

        // Weather particles running for a window only fly with that place's wind.
        [HarmonyPostfix, HarmonyPatch(typeof(GlobalWind), "UpdateWind")]
        static void GlobalWindUpdated(GlobalWind __instance) => FarEnvironment.FarWind(__instance);

        // Win.SkyPerArea: a far area's sky picture is redrawn when something it shows appears or goes under it (a roof
        // built or a tree felled over there, objects still arriving from the server).
        [HarmonyPostfix, HarmonyPatch(typeof(ZNetScene), "CreateObject")]
        static void ObjectCreated(GameObject __result) => FarSkyOcclusion.Changed(__result);

        [HarmonyPrefix, HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Destroy), new[] { typeof(GameObject) })]
        static void ObjectDestroyed(GameObject go) => FarSkyOcclusion.Changed(go);

        [HarmonyPrefix, HarmonyPatch(typeof(ZNetScene), "OnZDODestroyed")]
        static void ZdoDestroyed(ZNetScene __instance, ZDO zdo)
        {
            if (!FarSkyOcclusion.Watching || zdo == null)
                return;
            ZNetView view = __instance.FindInstance(zdo);
            if (view != null)
                FarSkyOcclusion.Changed(view.gameObject);
        }

        // Objects made in a loaded far area are kept from the start (RemoteAreas.Track; on FarWorld's registry its sectors
        // keep them, and Track only notes what is carried in for the area's lively objects).
        [HarmonyPostfix, HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.CreateNewZDO), new[] { typeof(Vector3), typeof(int) })]
        static void ZdoMade(ZDO __result) => PortalSystem.Instance?.Areas.Track(__result);

        // Every sound effect, for its twin heard through a portal (PortalAudio).
        [HarmonyPostfix, HarmonyPatch(typeof(ZSFX), nameof(ZSFX.Awake))]
        static void SoundMade(ZSFX __instance) => PortalAudio.Register(__instance);

        [HarmonyPrefix, HarmonyPatch(typeof(ZSFX), "OnDestroy")]
        static void SoundGone(ZSFX __instance) => PortalAudio.Unregister(__instance);

        // A storm's lightning flash, known so windows onto calmer places leave it out.
        [HarmonyPrefix, HarmonyPatch(typeof(Thunder), "DoFlash")]
        static void ThunderFlashBefore() => FarEnvironment.InStrike = true;

        [HarmonyPostfix, HarmonyPatch(typeof(Thunder), "DoFlash")]
        static void ThunderFlashAfter() => FarEnvironment.InStrike = false;

        [HarmonyPostfix, HarmonyPatch(typeof(EffectList), nameof(EffectList.Create))]
        static void EffectsCreated(GameObject[] __result)
        {
            FarEnvironment.Struck(__result);
            FarParticles.Made(__result);
        }

        [HarmonyPrefix, HarmonyPatch(typeof(EnvMan), "SetEnv")]
        static void EnvManSetEnvBefore(EnvSetup env) => FarEnvironment.Setting = env;

        [HarmonyPostfix, HarmonyPatch(typeof(EnvMan), "SetEnv")]
        static void EnvManSetEnv(float dayInt, float nightInt, float morningInt, float eveningInt)
        {
            FarEnvironment.Setting = null;
            FarEnvironment.Capture(dayInt, nightInt, morningInt, eveningInt);
        }

        // The game switching its weather's particles on or off: ones already running for a window are kept as they
        // are (see FarEnvironment.KeepFarWeather).
        [HarmonyPrefix, HarmonyPatch(typeof(EnvMan), "SetParticleArrayEnabled")]
        static bool EnvManParticles(EnvMan __instance, GameObject[] psystems, bool enabled) => FarEnvironment.GameSwitchesWeather(__instance, psystems, enabled);

        [HarmonyPostfix, HarmonyPatch(typeof(ClutterSystem), "LateUpdate")]
        static void ClutterSystemLateUpdate(ClutterSystem __instance)
        {
            PortalSystem.Instance?.GrowGrass(__instance);
        }
    }
}
