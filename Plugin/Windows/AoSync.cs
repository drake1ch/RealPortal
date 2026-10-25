using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RealPortal
{
    // OWNER: slots (S2, Win.AoTierA, Win.AoTemporal). The game's SSAO (Amplify Occlusion) on the slot cameras: one
    // AmplifyOcclusionEffect per slot camera (added with its first SSAO render, see below); all ~30 fields copied from the
    // game's on creation, on
    // SettingsChanged (GraphicsSettingsChanged, a Win.* switch) and when the game's component changes, never per frame
    // by reflection; per frame only the fields the game changes as it goes (Tint, Intensity, FadeToTint, Downsample,
    // SampleCount, as PortalSystem.SyncOcclusion does today). Per render (Before): enabled = TierParams.Ao (with
    // Win.AoTierA off: whenever the game's SSAO is on, as post-S1); half resolution and no filter for AoHalfRes;
    // FilterEnabled only for AoTemporal on a sticky slot (with motion vectors on that camera), else off (Win.DepthOnly).
    // The _AO_UVToView lens-shift fix (Patches.OcclusionConstants) and the filter prefix (Patches.WindowOcclusionPreRender)
    // must recognise slot cameras (PortalSystem.IsWindowCamera). Docs\S2S3-CONTRACTS.md §7.5.
    //
    // Implementation notes:
    //  - The component is added at a slot camera's first render that wants SSAO (a slot that never shows a window with
    //    SSAO never has one; Win.SlotPrewarm adds it, disabled, ahead of that render, see Prewarm) and switched only
    //    when that changes: enabling and disabling Amplify frees and makes its
    //    textures and command buffers, so the sticky slots (Live windows, NearCached ones with SSAO) keep it on and the
    //    other slots keep it off, with no churn from render to render.
    //  - With Win.AoTierA in force the far side's own SSAO tint and strength are used (EnvMan.SetEnv's values for the
    //    environment FarEnvironment applied for this render; the game's when it is the player's own weather): what the
    //    game camera standing over there would get. The shared camera could not (one copy for all windows); a slot camera
    //    is set right before its render. Lab: LabGameTint keeps the player's (the post-S1 values). Win.FarAoTint
    //    (Docs\PARITY-PLAN.md P32): the far side's always, whatever Win.AoTierA says, and the shared camera's copy too, set
    //    per render (Shared).
    //  - Patches.WindowOcclusionPreRender keeps forcing the filter off on the shared camera only; on a slot camera the
    //    filter is what Before decided.
    internal static class AoSync
    {
        sealed class Slot
        {
            public Camera Cam;
            public AmplifyOcclusionEffect Ao;
            // A full copy from the game's is due (new component, settings changed, another game component, 5 s passed).
            public bool Stale = true;
            public AmplifyOcclusionEffect From;
            public float FullAt;
        }

        static readonly List<Slot> s_slots = new List<Slot>();
        static readonly FieldInfo[] s_fields = typeof(AmplifyOcclusionEffect).GetFields(BindingFlags.Public | BindingFlags.Instance);

        // The game camera's SSAO this frame (FrameSync) and whether it runs.
        static GameCamera s_gameCamera;
        static AmplifyOcclusionEffect s_game;
        static bool s_gameOn;

        // Lab: the player's SSAO tint and strength on every slot camera (post-S1), not the far side's.
        internal static bool LabGameTint;
        // Lab: slot renders with SSAO, without, with its temporal filter; full copies made.
        internal static long LabOn, LabOff, LabTemporal, LabFullCopies;

        static IWindowManager Manager => PortalSystem.Instance != null ? PortalSystem.Instance.Manager : null;

        static Slot Find(Camera cam)
        {
            for (int i = 0; i < s_slots.Count; i++)
                if (ReferenceEquals(s_slots[i].Cam, cam))
                    return s_slots[i];
            return null;
        }

        // OnSlotCameraCreated. The component itself comes with the camera's first render that wants SSAO.
        internal static void Attach(Camera cam)
        {
            if ((object)cam == null || Find(cam) != null)
                return;
            s_slots.Add(new Slot { Cam = cam });
        }

        // OnSlotCameraDestroyed (the component goes with the camera's GameObject).
        internal static void Detach(Camera cam)
        {
            for (int i = s_slots.Count - 1; i >= 0; i--)
                if (ReferenceEquals(s_slots[i].Cam, cam))
                    s_slots.RemoveAt(i);
        }

        // Win.SlotPrewarm (WindowGlue.PrewarmSlots): the slot's component made ahead of its first SSAO render, as Before
        // would make it there (disabled, its camera primed; the full copy stays due), so that render only enables it.
        // Only while the game's SSAO is on (FrameSync this frame): a slot with nothing to prime gets its component at its
        // first SSAO render, as before. False: nothing made.
        internal static bool Prewarm(Camera cam)
        {
            Slot a = Find(cam);
            if (a == null || a.Ao != null || !s_gameOn || s_game == null || cam == null)
                return false;
            a.Ao = cam.gameObject.AddComponent<AmplifyOcclusionEffect>();
            a.Ao.enabled = false;
            a.Stale = true;
            Prime(a.Ao, cam);
            return true;
        }

        // GraphicsSettingsChanged or a Win.* switch flipped: full copy before the next render.
        internal static void SettingsChanged()
        {
            for (int i = 0; i < s_slots.Count; i++)
                s_slots[i].Stale = true;
        }

        // Once per frame (PortalSystem.RenderWindows, not idle): the game's component and whether its SSAO runs (as
        // PortalSystem.SyncOcclusion decides it for the shared camera).
        internal static void FrameSync(GameCamera gameCamera, IWindowManager manager)
        {
            if (gameCamera != s_gameCamera || s_game == null)
            {
                s_gameCamera = gameCamera;
                AmplifyOcclusionEffect game = gameCamera != null ? gameCamera.GetComponent<AmplifyOcclusionEffect>() : null;
                if (game != s_game)
                    SettingsChanged();
                s_game = game;
            }
            s_gameOn = Plugin.AmbientOcclusion.Value && s_game != null && s_game.enabled;
            for (int i = s_slots.Count - 1; i >= 0; i--)
                if (s_slots[i].Cam == null)
                    s_slots.RemoveAt(i);
        }

        // BeforeSlotRender, third handler.
        internal static void Before(WindowState s, Camera cam)
        {
            Slot a = Find(cam);
            if (a == null)
                return;   // the shared camera: PortalSystem.SyncOcclusion's copy
            IWindowManager m = Manager;
            AmplifyOcclusionEffect game = s_game;
            TierParams p = s != null ? s.ParamsNow(Time.frameCount) : TierParams.Legacy;
            bool tierA = m != null && m.Effective(WindowMechanism.AoTierA);
            // Win.AoTierA off (or not in force): every render the game's SSAO is on for, as post-S1.
            bool on = s_gameOn && game != null && (!tierA || p.Ao);
            if (!on)
            {
                if (a.Ao != null && a.Ao.enabled)
                    a.Ao.enabled = false;
                NoMotion(cam);
                LabOff++;
                return;
            }
            if (a.Ao == null)
            {
                a.Ao = cam.gameObject.AddComponent<AmplifyOcclusionEffect>();
                a.Ao.enabled = false;
                a.Stale = true;
                Prime(a.Ao, cam);
            }
            AmplifyOcclusionEffect ao = a.Ao;
            float now = Time.unscaledTime;
            if (a.Stale || a.From != game || now >= a.FullAt)
            {
                // All its settings (boxed, by reflection) only now and then, as SyncOcclusion under Win.LeanFixed.
                foreach (FieldInfo f in s_fields)
                    f.SetValue(ao, f.GetValue(game));
                a.From = game;
                a.Stale = false;
                a.FullAt = now + 5f;
                LabFullCopies++;
            }
            else
            {
                // EnvMan.SetEnv (CameraEffects.SetEnvironmentAOParams) every frame; CameraEffects.SetSSAO.
                ao.Tint = game.Tint;
                ao.Intensity = game.Intensity;
                ao.FadeToTint = game.FadeToTint;
                ao.Downsample = game.Downsample;
                ao.SampleCount = game.SampleCount;
            }
            bool halfRes = tierA && p.AoHalfRes;
            if (halfRes)
                ao.Downsample = true;
            // The temporal filter needs a history of its own: only a sticky slot (the same window frame after frame) with
            // Win.AoTemporal in force. Otherwise off under Win.DepthOnly (as the shared camera), and always at half
            // resolution; without Win.DepthOnly and AoTierA the game's, as post-S1.
            bool sticky = s != null && p.Sticky && s.Slot >= 0 && s.Slot == s.StickySlot;
            bool temporal = !halfRes && sticky && p.AoTemporal && m != null && m.Effective(WindowMechanism.AoTemporal);
            bool filter = temporal ? game.FilterEnabled : !halfRes && !WindowFlags.DepthOnly && game.FilterEnabled;
            if (ao.FilterEnabled != filter)
                ao.FilterEnabled = filter;
            if (filter && ao.ApplyMethod != AmplifyOcclusionEffect.ApplicationMethod.Deferred)
            {
                // Amplify's own Update would add them a frame late (UsingMotionVectors); the filter reads them this render.
                if ((cam.depthTextureMode & DepthTextureMode.MotionVectors) == 0)
                    cam.depthTextureMode |= DepthTextureMode.MotionVectors;
                LabTemporal++;
            }
            else if (!filter)
                NoMotion(cam);
            // The far side's SSAO tint and strength (EnvMan.SetEnv for that place's weather), under Win.AoTierA; with
            // Win.FarAoTint (Docs\PARITY-PLAN.md P32) whatever Win.AoTierA says.
            if ((tierA || WindowFlags.FarAoTint) && !LabGameTint)
            {
                EnvSetup env = FarEnvironment.Applied;
                if (env != null)
                {
                    FarEnvironment.AoOf(env, out Color tint, out float intensity);
                    ao.Tint = tint;
                    ao.FadeToTint = tint;
                    ao.Intensity = intensity;
                }
            }
            if (!ao.enabled)
                ao.enabled = true;
            LabOn++;
        }

        // A new AmplifyOcclusionEffect finds its camera in its first OnPreRender (or Update, which runs from the next frame
        // on) and does nothing else in that render: added right before a render it would leave that one without SSAO, a
        // one-frame pop on the slot's first SSAO render. Its camera is set here instead. (Its Update also moves per-pixel
        // normals to the G-buffer on a deferred camera; the copy from the game's deferred camera has that already.)
        static AccessTools.FieldRef<AmplifyOcclusionEffect, Camera> s_targetCamera;
        static bool s_targetLooked;

        static void Prime(AmplifyOcclusionEffect ao, Camera cam)
        {
            if (!s_targetLooked)
            {
                s_targetLooked = true;
                try
                {
                    FieldInfo f = AccessTools.DeclaredField(typeof(AmplifyOcclusionEffect), "m_targetCamera");
                    if (f != null && !f.IsStatic && f.FieldType == typeof(Camera))
                        s_targetCamera = AccessTools.FieldRefAccess<AmplifyOcclusionEffect, Camera>(f);
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning("AoSync: AmplifyOcclusionEffect.m_targetCamera not found (a slot's first SSAO render may lack it): " + e.Message);
                }
            }
            if (s_targetCamera != null && s_targetCamera(ao) == null)
                s_targetCamera(ao) = cam;
        }

        // Win.DepthOnly: a slot camera renders motion vectors only for its SSAO's temporal filter (Amplify's Update added
        // them while the filter ran, and they would stay).
        static void NoMotion(Camera cam)
        {
            if (WindowFlags.DepthOnly && (cam.depthTextureMode & DepthTextureMode.MotionVectors) != 0)
                cam.depthTextureMode &= ~DepthTextureMode.MotionVectors;
        }

        // AfterSlotRender: nothing to undo (the component is the slot camera's own and is set again before its next render).
        internal static void After(WindowState s, Camera cam)
        {
        }

        // Lab (Win.FarAoTint): renders of the shared camera given the far side's tint or the game's back.
        internal static long LabSharedTints;

        // Win.FarAoTint (Docs\PARITY-PLAN.md P32), the shared window camera's copy (PortalSystem.SyncOcclusion's: nested
        // views, a window without a slot, the legacy path), from PortalSystem.BeforeWindowRender once the far side is set
        // for the render: that place's tint and strength (EnvMan.SetEnv's for FarEnvironment.Applied), or the game's for a
        // view under the player's own weather (a render before may have left another place's). One copy serves every window
        // that camera renders, so it is set per render, as a slot's is in Before.
        internal static void Shared(AmplifyOcclusionEffect ao, AmplifyOcclusionEffect game)
        {
            if (ao == null || LabGameTint || !ao.enabled)
                return;
            EnvSetup env = FarEnvironment.Applied;
            if (env != null)
            {
                FarEnvironment.AoOf(env, out Color tint, out float intensity);
                ao.Tint = tint;
                ao.FadeToTint = tint;
                ao.Intensity = intensity;
            }
            else if (game != null)
            {
                ao.Tint = game.Tint;
                ao.FadeToTint = game.FadeToTint;
                ao.Intensity = game.Intensity;
            }
            LabSharedTints++;
        }

        // PortalSystem.OnDestroy (the components went with the pool's cameras).
        internal static void Clear()
        {
            s_slots.Clear();
            s_gameCamera = null;
            s_game = null;
            s_gameOn = false;
        }
    }
}
