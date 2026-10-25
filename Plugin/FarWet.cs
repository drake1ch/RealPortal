using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace RealPortal
{
    // Far.LocalWeatherSim (Docs\PARITY-PLAN.md P16, P35). The game has one weather for the whole world: EnvMan.IsWet and
    // the wind's strength are those of the camera's place, and every loaded object goes by them. A far area simulates its
    // objects as you standing at its far portal would, so its fires burned low under your rain and high under its own
    // rain shown in the same window, its windmills, sails and banners went by your wind while its trees bent with its
    // own, and its ships rode waves your wind made. Here the game's own tests of an object inside a far area (the active
    // area of a far portal whose weather differs from yours, as RemoteAreas.AreaOf finds it; never your own active area)
    // run with that place's weather: its rain (EnvMan.IsWet) and its wind's strength (rescaled as FarEnvironment rescales
    // it for that place's view; the wind blows the same way everywhere), set round the call and put back right after it.
    // A far point's waves (WaterVolume.CalcWave) come from that place's wind, as its window draws them. A place goes by the
    // weather at its far portal, as everything around you goes by yours; which places differ is looked at once a second.
    // Off: everything by your weather.
    [HarmonyPatch]
    internal static class FarWet
    {
        sealed class Place
        {
            // The far portal (the area's centre, as RemoteAreas gives it), the weather its objects go by (a blend's mix while
            // one runs there, FarEnvironment.LocalWeather), and whether its rain or its wind's range differs from yours.
            public Vector3 Center;
            public EnvSetup Env;
            public bool Wet, Wind;
        }

        // The places that differ now are the first s_count; s_windCount of them differ in wind. Looked at again once a second
        // and whenever the far areas change.
        static readonly List<Place> s_places = new List<Place>();
        static readonly List<(Vector3 center, Vector2s zone, int nearRadius, bool watched)> s_cores = new List<(Vector3, Vector2s, int, bool)>();
        static int s_count, s_windCount, s_version = int.MinValue, s_lookFrame = -1;
        static float s_refreshAt, s_reach = 160f;

        // Your weather, once a frame (GetCurrentEnvironment looks a forced one up by name).
        static EnvSetup s_own;
        static int s_ownFrame = -1;

        // The tests set to a far place's weather, innermost last, with what goes back after each (a test can run inside
        // another: Cinder.FixedUpdate hits and asks Cinder.CanBurn). Every prefix pushes and every finalizer pops.
        struct Saved
        {
            public bool On, Wet;
            public Vector4 Wind;
        }

        static readonly Saved[] s_stack = new Saved[32];
        static int s_top, s_depth;

        // Lab: the game's weather tests and wave heights of far objects answered with their place's weather (cumulative).
        internal static long LabFarWetChecks;

        // Inside a test set to a far place's weather (FarEnvironment.FarWind leaves such a GlobalWind alone).
        internal static bool InContext => s_depth > 0;

        static readonly AccessTools.FieldRef<bool> s_isWet = AccessTools.StaticFieldRefAccess<bool>(AccessTools.Field(typeof(EnvMan), "s_isWet"));
        static readonly AccessTools.FieldRef<EnvMan, Vector4> s_wind = AccessTools.FieldRefAccess<EnvMan, Vector4>("m_wind");
        static readonly AccessTools.FieldRef<Vector4> s_wave1 = AccessTools.StaticFieldRefAccess<Vector4>(AccessTools.Field(typeof(WaterVolume), "s_globalWind1"));
        static readonly AccessTools.FieldRef<Vector4> s_wave2 = AccessTools.StaticFieldRefAccess<Vector4>(AccessTools.Field(typeof(WaterVolume), "s_globalWind2"));
        static readonly AccessTools.FieldRef<float> s_waveAlpha = AccessTools.StaticFieldRefAccess<float>(AccessTools.Field(typeof(WaterVolume), "s_globalWindAlpha"));
        static readonly Func<WaterVolume, Vector3, float, Vector4, float, float, float, float> s_calcWave =
            AccessTools.MethodDelegate<Func<WaterVolume, Vector3, float, Vector4, float, float, float, float>>(AccessTools.Method(typeof(WaterVolume), "CalcWave",
                new[] { typeof(Vector3), typeof(float), typeof(Vector4), typeof(float), typeof(float), typeof(float) }));
        static readonly AccessTools.FieldRef<Character, bool> s_slipping = AccessTools.FieldRefAccess<Character, bool>("m_slipping");

        // The far places whose weather differs from yours: the awake far areas (RemoteAreas.CollectAwakeCores; asleep ones
        // run nothing), each by the weather at its far portal. Once a second, or at once when the areas changed.
        static void Refresh()
        {
            // Looked at once a frame at most (the tests come many a frame), and never inside a test set to a far place's
            // weather (EnvMan.IsWet is that place's there, not yours).
            int frame = Time.frameCount;
            if (frame == s_lookFrame || s_depth > 0)
                return;
            s_lookFrame = frame;
            PortalSystem sys = PortalSystem.Instance;
            int version = sys != null ? sys.Areas.AreasVersion : -1;
            float now = Time.time;
            if (now < s_refreshAt && version == s_version)
                return;
            s_refreshAt = now + 1f;
            s_version = version;
            s_count = 0;
            s_windCount = 0;
            EnvMan em = EnvMan.instance;
            // With Distance.FarWeather off the windows show your weather everywhere, and so do the far objects.
            if (sys == null || em == null || ZoneSystem.instance == null || Plugin.FarWeather == null || !Plugin.FarWeather.Value)
                return;
            // An active area reaches 1.5 zones from its zone's centre (1.75 as a circle), the far portal up to half a zone
            // off that centre.
            s_reach = ZoneSystem.instance.m_zoneSize * 2.5f;
            sys.Areas.CollectAwakeCores(s_cores);
            EnvSetup own = em.GetCurrentEnvironment();
            bool ownWet = EnvMan.IsWet();
            foreach (var core in s_cores)
            {
                Vector3 center = core.center;
                EnvSetup env = FarEnvironment.LocalWeather(center);
                if (env == null)
                    continue;
                bool wet = env.m_isWet != ownWet;
                bool wind = own != null && !em.m_debugWind && (env.m_windMin != own.m_windMin || env.m_windMax != own.m_windMax);
                if (!wet && !wind)
                    continue;
                if (s_count == s_places.Count)
                    s_places.Add(new Place());
                Place p = s_places[s_count++];
                p.Center = center;
                p.Env = env;
                p.Wet = wet;
                p.Wind = wind;
                if (wind)
                    s_windCount++;
            }
        }

        // The far place whose weather a point goes by, when that differs from yours (wind: only a place whose wind
        // differs); false: yours. Most points are near no such place and end at the first loop.
        static bool PlaceOf(Vector3 at, bool wind, out Place place)
        {
            place = null;
            Refresh();
            if (s_count == 0 || (wind && s_windCount == 0))
                return false;
            bool near = false;
            for (int i = 0; i < s_count && !near; i++)
            {
                Place p = s_places[i];
                near = (!wind || p.Wind) && Mathf.Abs(at.x - p.Center.x) <= s_reach && Mathf.Abs(at.z - p.Center.z) <= s_reach;
            }
            if (!near)
                return false;
            PortalSystem sys = PortalSystem.Instance;
            ZNet net = ZNet.instance;
            if (sys == null || net == null || ZoneSystem.instance == null)
                return false;
            // Your own active area goes by your weather, whichever far area's it is as well.
            if (ZNetScene.InActiveArea(at, ZoneSystem.GetZone(net.GetReferencePosition())))
                return false;
            if (!sys.Areas.AreaOf(at, out Vector2s _, out Vector3 center, out ZDOID _))
                return false;
            for (int i = 0; i < s_count; i++)
            {
                Place p = s_places[i];
                if ((p.Center - center).sqrMagnitude < 0.01f && (!wind || p.Wind))
                {
                    place = p;
                    return true;
                }
            }
            return false;
        }

        static EnvSetup Own(EnvMan em)
        {
            int frame = Time.frameCount;
            if (frame != s_ownFrame)
            {
                s_ownFrame = frame;
                s_own = em.GetCurrentEnvironment();
            }
            return s_own;
        }

        // Before a test of an object at `at`: that far place's rain and wind, if it is in one whose weather differs.
        static void Push(Vector3 at, bool test = true)
        {
            if (s_top >= s_stack.Length)
            {
                // Never this deep; nothing is set, and the pop matches.
                s_top++;
                return;
            }
            int k = s_top++;
            s_stack[k].On = false;
            EnvMan em = EnvMan.instance;
            if (!test || !FarFlags.LocalWeatherSim || em == null || !PlaceOf(at, false, out Place p))
                return;
            Vector4 wind = s_wind(em);
            s_stack[k].On = true;
            s_stack[k].Wet = s_isWet();
            s_stack[k].Wind = wind;
            s_depth++;
            s_isWet() = p.Env.m_isWet;
            EnvSetup own = p.Wind && !em.m_debugWind ? Own(em) : null;
            if (own != null)
            {
                wind.w = FarEnvironment.Rescale(wind.w, own, p.Env);
                s_wind(em) = wind;
            }
            LabFarWetChecks++;
        }

        // For other far-side code (Far.Viewer round the game's wear and roof tests of far pieces): the game's weather tests
        // made between Enter(position) and Exit() go by the far place the position is in, as the patches below do. Every
        // Enter needs its Exit (in a finally), main thread only.
        internal static void Enter(Vector3 at) => Push(at);

        internal static void Exit() => Pop();

        // After it: yours back.
        static void Pop()
        {
            if (s_top == 0)
                return;
            int k = --s_top;
            if (k >= s_stack.Length || !s_stack[k].On)
                return;
            s_stack[k].On = false;
            s_depth--;
            s_isWet() = s_stack[k].Wet;
            EnvMan em = EnvMan.instance;
            if (em != null)
                s_wind(em) = s_stack[k].Wind;
        }

        // ---- the game's tests ----

        // Fireplace.CheckWet (every 4 s, every fireplace): low flames in the wind (0.8 and up) without cover, or in the rain
        // without a roof.
        [HarmonyPrefix, HarmonyPatch(typeof(Fireplace), "CheckWet")]
        static void FireplaceIn(Fireplace __instance) => Push(__instance.transform.position);

        [HarmonyFinalizer, HarmonyPatch(typeof(Fireplace), "CheckWet")]
        static void FireplaceOut() => Pop();

        // Fire.UpdateFire: a fire on the ground without a roof goes out in the rain.
        [HarmonyPrefix, HarmonyPatch(typeof(Fire), "UpdateFire")]
        static void FireIn(Fire __instance) => Push(__instance.transform.position);

        [HarmonyFinalizer, HarmonyPatch(typeof(Fire), "UpdateFire")]
        static void FireOut() => Pop();

        // Cinder: blown by the wind as it is made and as it flies, and no grass catches fire in the rain (CanBurn).
        [HarmonyPrefix, HarmonyPatch(typeof(Cinder), "Awake")]
        static void CinderMadeIn(Cinder __instance) => Push(__instance.transform.position);

        [HarmonyFinalizer, HarmonyPatch(typeof(Cinder), "Awake")]
        static void CinderMadeOut() => Pop();

        [HarmonyPrefix, HarmonyPatch(typeof(Cinder), "FixedUpdate")]
        static void CinderIn(Cinder __instance) => Push(__instance.transform.position);

        [HarmonyFinalizer, HarmonyPatch(typeof(Cinder), "FixedUpdate")]
        static void CinderOut() => Pop();

        [HarmonyPrefix, HarmonyPatch(typeof(Cinder), nameof(Cinder.CanBurn))]
        static void CanBurnIn(Vector3 point) => Push(point);

        [HarmonyFinalizer, HarmonyPatch(typeof(Cinder), nameof(Cinder.CanBurn))]
        static void CanBurnOut() => Pop();

        // Character.UpdateWalking: one slipping on ice is pushed by the wind (looked at only for one that slipped last
        // step: the flag is set at the start of the call).
        [HarmonyPrefix, HarmonyPatch(typeof(Character), "UpdateWalking")]
        static void WalkIn(Character __instance) => Push(__instance.transform.position, s_slipping(__instance));

        [HarmonyFinalizer, HarmonyPatch(typeof(Character), "UpdateWalking")]
        static void WalkOut() => Pop();

        // Windmill.GetPowerOutput: how fast it turns and grinds (its Update and the Smelter ask it).
        [HarmonyPrefix, HarmonyPatch(typeof(Windmill), nameof(Windmill.GetPowerOutput))]
        static void MillIn(Windmill __instance) => Push(__instance.transform.position);

        [HarmonyFinalizer, HarmonyPatch(typeof(Windmill), nameof(Windmill.GetPowerOutput))]
        static void MillOut() => Pop();

        // Ship.CustomFixedUpdate: its sail's force and the mast's turn.
        [HarmonyPrefix, HarmonyPatch(typeof(Ship), nameof(Ship.CustomFixedUpdate))]
        static void ShipIn(Ship __instance) => Push(__instance.transform.position);

        [HarmonyFinalizer, HarmonyPatch(typeof(Ship), nameof(Ship.CustomFixedUpdate))]
        static void ShipOut() => Pop();

        // GlobalWind.UpdateWind: banners and cloth, wind-driven particles (smoke, leaves, embers) and what turns to the wind.
        // A weather object running for a window inside such a place gets its wind here too (FarEnvironment.FarWind then
        // leaves it alone).
        [HarmonyPrefix, HarmonyPatch(typeof(GlobalWind), "UpdateWind")]
        static void GlobalWindIn(GlobalWind __instance) => Push(__instance.transform.position);

        [HarmonyFinalizer, HarmonyPatch(typeof(GlobalWind), "UpdateWind")]
        static void GlobalWindOut() => Pop();

        // WearNTear is not patched here: the engine's WearNTearActiveSectors takes any patch on WearNTear.UpdateWear for
        // another mod's and changes its cadence for it. Far pieces reach UpdateWear's wet test with Far.Viewer, whose
        // call-site transpiler (which the engine counts as Real Portal's own) runs those tests inside Enter/Exit.

        // WaterVolume.CalcWave (every wave height asked of the water: floating ships and items, swimming, fish): at a far
        // point of a place whose wind differs, the waves of that place's wind, as its window's water draws them (its
        // _GlobalWind1/2 strengths rescaled by FarEnvironment).
        [HarmonyPrefix, HarmonyPatch(typeof(WaterVolume), nameof(WaterVolume.CalcWave), new[] { typeof(Vector3), typeof(float), typeof(float), typeof(float), typeof(float) })]
        static bool Wave(WaterVolume __instance, Vector3 worldPos, float depth, float waterTime, float waveFactor, float waveFactorBig, ref float __result)
        {
            if (!FarFlags.LocalWeatherSim || s_calcWave == null)
                return true;
            EnvMan em = EnvMan.instance;
            if (em == null || em.m_debugWind || !PlaceOf(worldPos, true, out Place p))
                return true;
            EnvSetup own = Own(em);
            if (own == null)
                return true;
            Vector4 w1 = s_wave1(), w2 = s_wave2();
            w1.w = FarEnvironment.Rescale(w1.w, own, p.Env);
            w2.w = FarEnvironment.Rescale(w2.w, own, p.Env);
            float alpha = s_waveAlpha();
            float a = s_calcWave(__instance, worldPos, depth, w1, waterTime, waveFactor, waveFactorBig);
            __result = alpha == 0f ? a : Mathf.LerpUnclamped(a, s_calcWave(__instance, worldPos, depth, w2, waterTime, waveFactor, waveFactorBig), alpha);
            LabFarWetChecks++;
            return false;
        }
    }
}
