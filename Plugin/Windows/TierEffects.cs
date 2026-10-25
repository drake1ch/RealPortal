using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace RealPortal
{
    // OWNER: sched (S3, Win.ShadowTiers and the §4.5 weather-particle rule), written by the integrator. The tier effects
    // RenderLevel cannot apply in shared code. Before undoes nothing it did not do; After restores exactly what Before
    // changed (also when Render() threw). Main thread, no allocation per render.
    //
    // Point/spot light shadows (TierParams.PointLightShadows false: NearCached and SmallCached under Win.ShadowTiers,
    // SmallCached alone under Win.ShadowTiersSmall; WindowMechanisms.ShadowCaps decides per tier): the
    // lights the game manages (LightLod, which PortalSystem.LightDistance lets light up and cast shadows near far portals)
    // that cast shadows near the window camera lose them for that render; their shadow mode comes back right after. A
    // light's shadow strength is left alone (LightLod fades it), only its mode is switched, which Unity reads per render.
    //
    // Weather particles (TierParams.WeatherParticles false: SmallCached): "only if already running" (ARCHITECTURE-v2 §4.5)
    // is what FarEnvironment.SwapWeather does for every render already: it only shows or hides weather particle systems
    // that exist (the player's own, and far ones KeepFarWeather keeps running), it never starts any for a render. So a
    // SmallCached render needs nothing more here; the flag is read by nobody yet.
    internal static class TierEffects
    {
        // LightLod's own set of every managed light, and its light (private fields; looked up once).
        static readonly AccessTools.FieldRef<LightLod, Light> s_light = Field<Light>("m_light");
        static HashSet<LightLod> s_all;
        static bool s_looked;

        // The lights Before switched off for the render under way, with the mode they had.
        static readonly List<Light> s_off = new List<Light>(16);
        static readonly List<LightShadows> s_mode = new List<LightShadows>(16);

        // This frame's candidates: the enabled point/spot lights of LightLod's set that cast shadows, with their positions,
        // gathered once per frame at the first cached-tier render. m_lights holds every LightLod awakened in every loaded
        // zone (disabled ones too, about 2000 with a large base and far areas), so walking it with its native reads per
        // render would cost thousands of calls a frame; the short list is filtered by distance per render instead. After
        // restores each light's mode right after its render, so the list stays true for the rest of the frame.
        static readonly List<Light> s_cand = new List<Light>(64);
        static readonly List<Vector3> s_candPos = new List<Vector3>(64);
        static int s_candFrame = -1;

        // A light farther than this from the window camera casts no shadow into its view worth a render (LightLod's own
        // shadow reach is 20 m around its reference point).
        const float Reach = 60f;

        // Lab: renders whose point-light shadows were switched off, and lights switched.
        internal static long LabRenders, LabLights;

        static AccessTools.FieldRef<LightLod, T> Field<T>(string name)
        {
            try
            {
                return AccessTools.FieldRefAccess<LightLod, T>(name);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Win.ShadowTiers: LightLod.{name} not found ({e.Message}); point-light shadows stay on in cached windows.");
                return null;
            }
        }

        static HashSet<LightLod> All()
        {
            if (!s_looked)
            {
                s_looked = true;
                try
                {
                    s_all = AccessTools.StaticFieldRefAccess<HashSet<LightLod>>(typeof(LightLod), "m_lights");
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning($"Win.ShadowTiers: LightLod.m_lights not found ({e.Message}); point-light shadows stay on in cached windows.");
                }
            }
            return s_all;
        }

        // BeforeSlotRender, first handler.
        internal static void Before(WindowState s, Camera cam)
        {
            s_off.Clear();
            s_mode.Clear();
            if (s == null || cam == null)
                return;
            TierParams p = s.ParamsNow(Time.frameCount);
            if (p.PointLightShadows)
                return;
            IWindowManager m = PortalSystem.Instance != null ? PortalSystem.Instance.Manager : null;
            // The params say what this render's tier gets; the switch is asked again for that tier (one flipped since Plan).
            if (m == null || !m.ShadowCaps(p.Tier) || s_light == null)
                return;
            Gather();
            if (s_cand.Count == 0)
                return;
            Vector3 eye = cam.transform.position;
            float reach2 = Reach * Reach;
            for (int i = 0; i < s_cand.Count; i++)
            {
                if ((s_candPos[i] - eye).sqrMagnitude > reach2)
                    continue;
                Light l = s_cand[i];
                if (l == null || l.shadows == LightShadows.None)
                    continue;
                s_off.Add(l);
                s_mode.Add(l.shadows);
                l.shadows = LightShadows.None;
            }
            if (s_off.Count > 0)
            {
                LabRenders++;
                LabLights += s_off.Count;
            }
        }

        // Once per frame: the shadow-casting enabled point/spot lights of LightLod's set and where they are.
        static void Gather()
        {
            int frame = Time.frameCount;
            if (s_candFrame == frame)
                return;
            s_candFrame = frame;
            s_cand.Clear();
            s_candPos.Clear();
            HashSet<LightLod> all = All();
            if (all == null || all.Count == 0)
                return;
            foreach (LightLod lod in all)
            {
                if ((object)lod == null || lod == null)
                    continue;
                Light l = s_light(lod);
                if ((object)l == null || l == null || !l.enabled || l.shadows == LightShadows.None)
                    continue;
                if (l.type != LightType.Point && l.type != LightType.Spot)
                    continue;
                s_cand.Add(l);
                s_candPos.Add(l.transform.position);
            }
        }

        // AfterSlotRender, last handler.
        internal static void After(WindowState s, Camera cam)
        {
            for (int i = 0; i < s_off.Count; i++)
            {
                Light l = s_off[i];
                if (l != null && l.shadows == LightShadows.None)
                    l.shadows = s_mode[i];
            }
            s_off.Clear();
            s_mode.Clear();
        }
    }
}
