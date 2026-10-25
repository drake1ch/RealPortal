using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace RealPortal
{
    // Weather and light differ between biomes (fog colour and density, sun colour and angle, ambient light, clouds).
    // For a window's render they are set the way EnvMan would set them for a camera standing at the window camera's
    // place, at the same time of day, and put back right after.
    internal static class FarEnvironment
    {
        static readonly AccessTools.FieldRef<EnvMan, EnvSetup> s_current = AccessTools.FieldRefAccess<EnvMan, EnvSetup>("m_currentEnv");
        static readonly AccessTools.FieldRef<EnvMan, EnvSetup> s_next = AccessTools.FieldRefAccess<EnvMan, EnvSetup>("m_nextEnv");
        static readonly AccessTools.FieldRef<EnvMan, string> s_forceEnv = AccessTools.FieldRefAccess<EnvMan, string>("m_forceEnv");
        static readonly AccessTools.FieldRef<EnvMan, float> s_smoothDay = AccessTools.FieldRefAccess<EnvMan, float>("m_smoothDayFraction");
        static readonly Func<EnvMan, List<EnvEntry>, EnvSetup> s_select =
            AccessTools.MethodDelegate<Func<EnvMan, List<EnvEntry>, EnvSetup>>(AccessTools.Method(typeof(EnvMan), "SelectWeightedEnvironment"));
        static readonly Func<EnvMan, string, EnvSetup> s_getEnv =
            AccessTools.MethodDelegate<Func<EnvMan, string, EnvSetup>>(AccessTools.Method(typeof(EnvMan), "GetEnv"));

        static readonly int s_skyboxSunDir = Shader.PropertyToID("_SkyboxSunDir");
        static readonly int s_sunDir = Shader.PropertyToID("_SunDir");
        static readonly int s_sunFogColor = Shader.PropertyToID("_SunFogColor");
        static readonly int s_sunColor = Shader.PropertyToID("_SunColor");
        static readonly int s_ambientColor = Shader.PropertyToID("_AmbientColor");
        static readonly int s_wet = Shader.PropertyToID("_Wet");
        static readonly int s_aurora = Shader.PropertyToID("_AuroraStrength");
        static readonly int s_rain = Shader.PropertyToID("_Rain");
        static readonly int s_opacity = Shader.PropertyToID("_Opacity");

        // The day, night, morning and evening weights EnvMan used last (from the SetEnv postfix).
        static float s_day, s_night, s_morning, s_evening;
        static bool s_have;

        // Saved state while a window renders with another environment.
        static bool s_applied;
        static Quaternion s_lightRot;
        static float s_lightIntensity;
        static Color s_lightColor, s_fogColor, s_ambient, s_sunFog, s_sunCol, s_ambientGlobal;
        static float s_fogDensity, s_wetValue, s_auroraValue, s_rainValue, s_opacityValue;
        static Vector4 s_skyDir, s_sunDirValue;

        static long s_cachePeriod = -1;
        static readonly Dictionary<Vector2s, EnvSetup> s_cache = new Dictionary<Vector2s, EnvSetup>();

        internal static void Capture(float day, float night, float morning, float evening)
        {
            s_day = day;
            s_night = night;
            s_morning = morning;
            s_evening = evening;
            s_have = true;
        }

        // The environment the game would pick for a camera at this point now: its biome and the current weather
        // period (seeded the same way). Null when it is the player's own anyway, or a global override applies.
        // Far.WeatherParity: as EnvMan.GetEnvironmentOverride would for a player standing there: the debug environment and
        // the intro are everywhere's, but your forced environment (an EnvZone you stand in) is yours alone; the overrides of
        // a live window's far point (its EnvZone, the random or persistent event there) come from its place, and a place
        // blending into another weather is never the game's own until its blend ends.
        internal static EnvSetup Pick(Vector3 pos) => Pick(pos, true);

        // places: the overrides and blends of the live windows' places count (false: the point alone, as for you); `view`:
        // the place a view renders (ViewPlace), else the one nearest the point.
        static EnvSetup Pick(Vector3 pos, bool places, Place view = null)
        {
            EnvMan em = EnvMan.instance;
            if (em == null || WorldGenerator.instance == null || ZNet.instance == null || em.m_environmentDuration <= 0)
                return null;
            // Lab: the far side's weather set apart from the player's.
            if (!string.IsNullOrEmpty(LabFarEnv))
            {
                EnvSetup forced = s_getEnv(em, LabFarEnv);
                EnvSetup now = s_current(em);
                return forced != null && (now == null || now.m_name != forced.m_name) ? forced : null;
            }
            bool parity = FarFlags.WeatherParity;
            if (!parity)
            {
                if (!string.IsNullOrEmpty(em.m_debugEnv) || !string.IsNullOrEmpty(s_forceEnv(em)))
                    return null;
            }
            else if (!string.IsNullOrEmpty(em.m_debugEnv) || (Player.m_localPlayer != null && Player.m_localPlayer.InIntro()))
                return null;
            long period = (long)ZNet.instance.GetTimeSeconds() / em.m_environmentDuration;
            EnvSetup env = ZonePick(em, pos, period);
            if (!parity)
            {
                EnvSetup current = s_current(em);
                if (env == null || (current != null && current.m_name == env.m_name && s_next(em) == null))
                    return null;
                return env;
            }
            Place pl = places && s_placeStates > 0 ? view ?? PlaceNear(pos) : null;
            if (pl != null && pl.Override != null)
                env = pl.Override;
            if (env == null)
                return null;
            if (pl != null && pl.From != null && pl.Target == env)
                return env;
            // The game's own: what it renders (your forced environment while you stand in one; no blend under way).
            EnvSetup game = GameEnv(em, out bool yoursForced);
            if (game != null && game.m_name == env.m_name && (yoursForced || s_next(em) == null))
                return null;
            return env;
        }

        // The pick for the point's zone in this period (kept per zone for the period).
        static EnvSetup ZonePick(EnvMan em, Vector3 pos, long period)
        {
            Vector2s zone = ZoneSystem.GetZone(pos);
            if (period != s_cachePeriod)
            {
                s_cachePeriod = period;
                s_cache.Clear();
            }
            if (!s_cache.TryGetValue(zone, out EnvSetup env))
                s_cache[zone] = env = PickNow(em, pos, period);
            return env;
        }

        // The environment the game renders now: the forced one while you stand in a forcing EnvZone (looked up once per
        // name), else its current one.
        static string s_forcedName;
        static EnvSetup s_forcedEnv;

        static EnvSetup GameEnv(EnvMan em, out bool forced)
        {
            string name = s_forceEnv(em);
            forced = !string.IsNullOrEmpty(name);
            if (!forced)
                return s_current(em);
            if (!ReferenceEquals(name, s_forcedName))
            {
                s_forcedName = name;
                s_forcedEnv = s_getEnv(em, name);
            }
            return s_forcedEnv;
        }

        // Lab: what was picked last, and for which biome and period.
        internal static string LabLastPick = "";
        internal static string LabFarEnv;

        static EnvSetup PickNow(EnvMan em, Vector3 pos, long period)
        {
            EnvSetup picked = PickNowInner(em, pos, period);
            LabLastPick = $"{(picked != null ? picked.m_name : "none")} for {WorldGenerator.instance.GetBiomeSector(pos).Biome} period {period}";
            return picked;
        }

        static EnvSetup PickNowInner(EnvMan em, Vector3 pos, long period)
        {
            BiomeSector biome = WorldGenerator.instance.GetBiomeSector(pos);
            foreach (AltBiome alt in biome.AltBiomes)
                if (!string.IsNullOrEmpty(alt.m_forceEnvironment))
                    return s_getEnv(em, alt.m_forceEnvironment);
            // As EnvMan.UpdateEnvironment (including its use of y for the deep north test).
            bool ash = WorldGenerator.IsAshlands(pos.x, pos.z);
            bool deep = WorldGenerator.IsDeepnorth(pos.x, pos.y);
            UnityEngine.Random.State state = UnityEngine.Random.state;
            UnityEngine.Random.InitState((int)period);
            EnvSetup pick = null;
            List<EnvEntry> envs = em.GetAvailableEnvironments(biome);
            if (envs != null && envs.Count > 0)
            {
                pick = s_select(em, envs);
                foreach (EnvEntry e in envs)
                {
                    if (e.m_ashlandsOverride && ash)
                        pick = e.m_env;
                    if (e.m_deepnorthOverride && deep)
                        pick = e.m_env;
                }
            }
            UnityEngine.Random.state = state;
            return pick;
        }

        // Before a window renders: the environment of the place its camera stands in.
        internal static void Apply(Vector3 camPos)
        {
            if (WindowFlags.EnvGroups && !s_applied)
                ApplyGrouped(camPos);
            else
                ApplyOne(camPos);
        }

        // Win.EnvGroups off: everything set for this render and put back right after it (Restore).
        static void ApplyOne(Vector3 camPos)
        {
            EnvMan em = EnvMan.instance;
            if (s_applied || !s_have || em == null || em.m_dirLight == null)
                return;
            // Far.WeatherParity: the place the view looks at (its override, blend, shelter, weather objects and storm).
            Place pl = FarFlags.WeatherParity ? ViewPlace(camPos) : null;
            EnvSetup env = Pick(camPos, true, pl);
            if (env == null && (pl == null || !PlaceDiffers(em, pl)))
                return;
            Light light = em.m_dirLight;
            s_applied = true;
            s_lightRot = light.transform.rotation;
            s_lightIntensity = light.intensity;
            s_lightColor = light.color;
            s_fogColor = RenderSettings.fogColor;
            s_fogDensity = RenderSettings.fogDensity;
            s_ambient = RenderSettings.ambientLight;
            s_skyDir = Shader.GetGlobalVector(s_skyboxSunDir);
            s_sunDirValue = Shader.GetGlobalVector(s_sunDir);
            s_sunFog = Shader.GetGlobalColor(s_sunFogColor);
            s_sunCol = Shader.GetGlobalColor(s_sunColor);
            s_ambientGlobal = Shader.GetGlobalColor(s_ambientColor);
            s_wetValue = Shader.GetGlobalFloat(s_wet);
            s_auroraValue = Shader.GetGlobalFloat(s_aurora);
            Material clouds = em.m_clouds != null ? em.m_clouds.sharedMaterial : null;
            if (clouds != null)
            {
                s_rainValue = clouds.GetFloat(s_rain);
                s_opacityValue = clouds.GetFloat(s_opacity);
            }
            bool parity = FarFlags.WeatherParity;
            if (env == null)
            {
                // Far.WeatherParity: your weather there, with its particles as that place has them.
                SwapPlace(em, null, pl);
                return;
            }
            // Far.WeatherParity: a place blending into its weather renders the blend (EnvMan's interpolated environment).
            EnvSetup look = parity ? LookOf(pl, env) : env;
            Set(em, look, WetAt(pl != null ? pl.At : camPos, env));
            if (parity)
                Aurora(pl, env);
            LabApplied = env.m_name;
            Applied = look;
            SetWind(em, look);
            if (parity)
                SwapPlace(em, env, pl);
            else
                SwapWeather(em, env);
        }

        // ---- Win.EnvGroups: one set of values per environment, set by difference, put back after the last window

        // The weather whose particles and lightning are swapped in right now (null: none, the game's own).
        static EnvSetup s_swapEnv;
        static float s_labSetAt;

        // Lab: windows onto another environment than the window before (their weather particles swapped anew).
        internal static int LabSwaps;

        // The environment for this view set by what differs from what the window before left set; the game's comes
        // back with Restore after the last window (PortalSystem), or right after a probe face.
        static void ApplyGrouped(Vector3 camPos)
        {
            EnvMan em = EnvMan.instance;
            if (!s_have || em == null || em.m_dirLight == null)
                return;
            bool parity = FarFlags.WeatherParity;
            Place pl = parity ? ViewPlace(camPos) : null;
            EnvSetup env = Pick(camPos, true, pl);
            if (env == null)
            {
                // The game's own weather there: whatever an earlier window set goes back for this one.
                AreaRenderState.ToGame(em);
                AuroraBack();
                // Far.WeatherParity: its particles as that place has them (its shelter, its own weather objects).
                if (pl != null && PlaceDiffers(em, pl))
                {
                    if (!s_placeSwapped || s_swapPlace != pl || s_swapKeyEnv != null)
                    {
                        FarMist.Hide();
                        if (s_swapEnv != null || s_placeSwapped)
                            UndoSwap();
                        s_swapEnv = null;
                        SwapPlace(em, null, pl);
                        s_placeSwapped = true;
                        s_swapPlace = pl;
                        s_swapKeyEnv = null;
                        LabSwaps++;
                    }
                }
                else if (s_swapEnv != null || s_placeSwapped)
                {
                    FarMist.Hide();
                    UndoSwap();
                    s_swapEnv = null;
                    s_placeSwapped = false;
                    s_swapPlace = null;
                    s_swapKeyEnv = null;
                }
                LabApplied = null;
                Applied = null;
                return;
            }
            if (!AreaRenderState.Open)
                AreaRenderState.Begin(em);
            float wet = WetAt(pl != null ? pl.At : camPos, env);
            AreaRenderState.Values v;
            // Far.WeatherParity: a place blending into its weather renders the blend, its values made anew each time.
            EnvSetup look = parity ? LookOf(pl, env) : env;
            if (look != env)
                Compute(em, look, wet, out v);
            else if (!AreaRenderState.Find(env, wet, out v))
            {
                Compute(em, env, wet, out v);
                AreaRenderState.Keep(env, wet, v);
            }
            AreaRenderState.Apply(em, v);
            LabApplied = env.m_name;
            Applied = look;
            if (parity)
            {
                Aurora(pl, env);
                if (!s_placeSwapped || s_swapPlace != pl || s_swapKeyEnv != env)
                {
                    FarMist.Hide();
                    if (s_swapEnv != null || s_placeSwapped)
                        UndoSwap();
                    s_swapEnv = null;
                    SwapPlace(em, env, pl);
                    s_placeSwapped = true;
                    s_swapPlace = pl;
                    s_swapKeyEnv = env;
                    LabSwaps++;
                }
            }
            else if (s_swapEnv != env)
            {
                // The mist goes on after the weather (as for a single window), so it is taken off first and shown
                // again by the caller (FarMist.Show) over the new weather.
                FarMist.Hide();
                if (s_swapEnv != null)
                    UndoSwap();
                SwapWeather(em, env);
                s_swapEnv = env;
                LabSwaps++;
            }
            // Lab: what a window set (formatting it cost a quarter of a millisecond a frame on every window).
            if (Time.unscaledTime >= s_labSetAt)
            {
                s_labSetAt = Time.unscaledTime + 1f;
                LabLastSet = LabState(em);
            }
        }

        // What Set and SetWind write for this environment, computed without touching anything (the same formulas).
        static void Compute(EnvMan em, EnvSetup env, float wet, out AreaRenderState.Values v)
        {
            v = AreaRenderState.Game;
            float day = s_day, night = s_night, morning = s_morning, evening = s_evening;
            Quaternion rot = Quaternion.Euler(-90f + env.m_sunAngle, 0f, 0f) * Quaternion.Euler(0f, -90f, 0f) *
                             Quaternion.Euler(-90f + 360f * s_smoothDay(em), 0f, 0f);
            Vector3 skyDir = -(rot * Vector3.forward);
            v.LightIntensity = env.m_lightIntensityDay * day + env.m_lightIntensityNight * night;
            if (night > 0f)
                rot *= Quaternion.Euler(180f, 0f, 0f);
            v.LightRot = rot;
            Color sun = env.m_sunColorNight * night;
            if (day > 0f)
                sun += env.m_sunColorDay * day + env.m_sunColorMorning * morning + env.m_sunColorEvening * evening;
            v.LightColor = sun;
            Color fog = env.m_fogColorNight * night + env.m_fogColorDay * day + env.m_fogColorMorning * morning + env.m_fogColorEvening * evening;
            v.FogColor = fog;
            Color sunFog = env.m_fogColorSunNight * night;
            if (day > 0f)
                sunFog += env.m_fogColorSunDay * day + env.m_fogColorSunMorning * morning + env.m_fogColorSunEvening * evening;
            sunFog = Color.Lerp(fog, sunFog, Mathf.Clamp01(Mathf.Max(night, day) * 3f));
            v.FogDensity = env.m_fogDensityNight * night + env.m_fogDensityDay * day + env.m_fogDensityMorning * morning + env.m_fogDensityEvening * evening;
            v.Ambient = Color.Lerp(env.m_ambColorNight, env.m_ambColorDay, day);
            if (em.m_clouds != null && em.m_clouds.sharedMaterial != null)
            {
                v.CloudRain = env.m_rainCloudAlpha;
                v.CloudOpacity = env.m_cloudOpacityDay * day + env.m_cloudOpacityEvening * evening + env.m_cloudOpacityMorning * morning + env.m_cloudOpacityNight * night;
            }
            v.SkyDir = skyDir;
            v.SunDir = -(rot * Vector3.forward);
            v.SunFog = sunFog;
            v.SunCol = v.LightColor * v.LightIntensity;
            v.AmbientGlobal = v.Ambient;
            v.Wet = wet;
            v.Aurora = env.m_auroraIntensityDay * day + env.m_auroraIntensityEvening * evening + env.m_auroraIntensityMorning * morning + env.m_auroraIntensityNight * night;
            // As SetWind: the strength that place's weather gives, at the same point of its range.
            EnvSetup own = em.GetCurrentEnvironment();
            if (own == null || em.m_debugWind || (own.m_windMin == env.m_windMin && own.m_windMax == env.m_windMax))
                return;
            Vector4 w1 = v.Wind1, w2 = v.Wind2;
            w1.w = Rescale(w1.w, own, env);
            w2.w = Rescale(w2.w, own, env);
            v.Wind1 = w1;
            v.Wind2 = w2;
            float was = em.GetWindIntensity();
            if (was > 1e-4f)
                v.WindForce = v.WindForce * (Rescale(was, own, env) / was);
        }

        // The weather particles and lightning put back as the game has them.
        static void UndoSwap()
        {
            foreach (Renderer r in s_hidden)
                if (r != null)
                    r.forceRenderingOff = false;
            s_hidden.Clear();
            foreach (Renderer r in s_shown)
                if (r != null)
                    r.forceRenderingOff = true;
            s_shown.Clear();
            foreach (Light l in s_offLights)
                if (l != null)
                    l.enabled = true;
            s_offLights.Clear();
            foreach (Light l in s_onLights)
                if (l != null)
                    l.enabled = false;
            s_onLights.Clear();
        }

        // After the last window (or a probe face): the game's own environment back.
        static void EndGrouped()
        {
            if (!AreaRenderState.Open && s_swapEnv == null && !s_placeSwapped && !s_auroraSet)
                return;
            AreaRenderState.End(EnvMan.instance);
            AuroraBack();
            if (s_swapEnv != null || s_placeSwapped)
            {
                UndoSwap();
                s_swapEnv = null;
                s_placeSwapped = false;
                s_swapPlace = null;
                s_swapKeyEnv = null;
            }
            LabApplied = null;
            Applied = null;
        }

        // Far weather, storms or wetness still kept (KeepFarWeather has something left to run down; Far.WeatherParity:
        // places too).
        internal static bool HasFarState => s_shadow.Count > 0 || s_storms.Count > 0 || s_farWet.Count > 0 || s_places.Count > 0;

        // How far the day has gone (EnvMan's smoothed day fraction, 0..1).
        internal static float DayFraction => EnvMan.instance != null ? s_smoothDay(EnvMan.instance) : 0f;

        // The environment a view at pos renders with (that place's, or the game's own) and whether the game is
        // blending between two (its look then changes from frame to frame).
        internal static object EnvAt(Vector3 pos, out bool blending)
        {
            blending = false;
            EnvMan em = EnvMan.instance;
            if (em == null)
                return null;
            EnvSetup far = Pick(pos);
            if (far != null)
            {
                // Far.WeatherParity: that place blends into its weather.
                if (FarFlags.WeatherParity && s_placeStates > 0)
                    blending = LookOf(PlaceNear(pos), far) != far;
                return far;
            }
            blending = s_next(em) != null;
            return (object)s_current(em) ?? em;
        }

        static readonly AccessTools.FieldRef<EnvMan, GameObject[]> s_currentPSystems = AccessTools.FieldRefAccess<EnvMan, GameObject[]>("m_currentPSystems");
        static readonly AccessTools.FieldRef<EnvMan, float> s_instantTimer = AccessTools.FieldRefAccess<EnvMan, float>("m_forceInstantEnvSwitchTimer");
        static readonly List<Renderer> s_hidden = new List<Renderer>();
        static readonly List<Renderer> s_shown = new List<Renderer>();

        // The weather's particles for this render: yours hidden where that place has none of them (rain, snow, ash),
        // and that place's own shown (they run hidden alongside yours, see KeepFarWeather).
        static void SwapWeather(EnvMan em, EnvSetup env)
        {
            ShowStorms(env);
            GameObject[] own = s_currentPSystems(em);
            if (own != null && own != env.m_psystems)
                foreach (GameObject go in own)
                    if (go != null && (env.m_psystems == null || Array.IndexOf(env.m_psystems, go) < 0))
                        foreach (Renderer r in RenderersOf(go))
                            if (r != null && !r.forceRenderingOff)
                            {
                                r.forceRenderingOff = true;
                                s_hidden.Add(r);
                            }
            if (env.m_psystems != null)
                foreach (GameObject go in env.m_psystems)
                    if (go != null && s_shadow.ContainsKey(go))
                        foreach (Renderer r in RenderersOf(go))
                            if (r != null && r.forceRenderingOff)
                            {
                                r.forceRenderingOff = false;
                                s_shown.Add(r);
                            }
            // Your storm's lightning does not flash over there.
            GameObject ownObject = s_envObject(em);
            if (ownObject == null || ownObject == env.m_envObject)
                return;
            HideOwnStorm(ownObject);
        }

        static void HideOwnStorm(GameObject ownObject)
        {
            Hide(RenderersOf(ownObject));
            s_flashes.RemoveAll(f => f == null);
            foreach (GameObject flash in s_flashes)
            {
                if (WindowFlags.EnvGroups)
                {
                    // The same parts as below, looked up once per flash.
                    Flash parts = PartsOf(flash);
                    foreach (Light l in parts.Lights)
                        if (l != null && l.enabled && l.gameObject.activeInHierarchy)
                        {
                            l.enabled = false;
                            s_offLights.Add(l);
                        }
                    foreach (Renderer r in parts.Renderers)
                        if (r != null && !r.forceRenderingOff && r.gameObject.activeInHierarchy)
                        {
                            r.forceRenderingOff = true;
                            s_hidden.Add(r);
                        }
                    continue;
                }
                foreach (Light l in flash.GetComponentsInChildren<Light>())
                    if (l.enabled)
                    {
                        l.enabled = false;
                        s_offLights.Add(l);
                    }
                Hide(flash.GetComponentsInChildren<Renderer>());
            }
        }

        sealed class Flash
        {
            public Light[] Lights;
            public Renderer[] Renderers;
        }

        // A lightning flash's lights and renderers (inactive ones too; skipped where used, as GetComponentsInChildren
        // would leave them out).
        static readonly Dictionary<GameObject, Flash> s_flashParts = new Dictionary<GameObject, Flash>();
        static readonly List<GameObject> s_flashDrop = new List<GameObject>();

        static Flash PartsOf(GameObject flash)
        {
            if (s_flashParts.TryGetValue(flash, out Flash f))
                return f;
            if (s_flashParts.Count >= 64)
            {
                s_flashDrop.Clear();
                foreach (GameObject go in s_flashParts.Keys)
                    if (go == null)
                        s_flashDrop.Add(go);
                foreach (GameObject go in s_flashDrop)
                    s_flashParts.Remove(go);
                if (s_flashParts.Count >= 64)
                    s_flashParts.Clear();
            }
            s_flashParts[flash] = f = new Flash { Lights = flash.GetComponentsInChildren<Light>(true), Renderers = flash.GetComponentsInChildren<Renderer>(true) };
            return f;
        }

        static void Hide(Renderer[] renderers)
        {
            foreach (Renderer r in renderers)
                if (r != null && !r.forceRenderingOff)
                {
                    r.forceRenderingOff = true;
                    s_hidden.Add(r);
                }
        }

        static readonly AccessTools.FieldRef<EnvMan, GameObject> s_envObject = AccessTools.FieldRefAccess<EnvMan, GameObject>("m_currentEnvObject");
        static readonly List<GameObject> s_flashes = new List<GameObject>();
        static readonly List<Light> s_offLights = new List<Light>();

        // Set while Thunder strikes, so the flash it makes is known (from the EffectList.Create postfix).
        internal static bool InStrike;

        internal static void Struck(GameObject[] made)
        {
            if (!InStrike || made == null)
                return;
            s_flashes.RemoveAll(f => f == null);
            s_flashes.AddRange(made);
        }

        static readonly Dictionary<GameObject, Renderer[]> s_renderers = new Dictionary<GameObject, Renderer[]>();

        static Renderer[] RenderersOf(GameObject go)
        {
            if (!s_renderers.TryGetValue(go, out Renderer[] r))
                s_renderers[go] = r = go.GetComponentsInChildren<Renderer>(true);
            return r;
        }

        static readonly int s_wind1 = Shader.PropertyToID("_GlobalWind1");
        static readonly int s_wind2 = Shader.PropertyToID("_GlobalWind2");
        static readonly int s_windForce = Shader.PropertyToID("_GlobalWindForce");
        static Vector4 s_wind1Value, s_wind2Value, s_windForceValue;
        static bool s_windSet;

        // The wind blows the same way everywhere, but how hard depends on the weather (trees and grass bending, the
        // sea's waves): as hard as that place's weather would make it, at the same point of its range as yours is.
        static void SetWind(EnvMan em, EnvSetup env)
        {
            EnvSetup own = em.GetCurrentEnvironment();
            if (own == null || em.m_debugWind || (own.m_windMin == env.m_windMin && own.m_windMax == env.m_windMax))
                return;
            s_windSet = true;
            s_wind1Value = Shader.GetGlobalVector(s_wind1);
            s_wind2Value = Shader.GetGlobalVector(s_wind2);
            s_windForceValue = Shader.GetGlobalVector(s_windForce);
            Vector4 w1 = s_wind1Value, w2 = s_wind2Value;
            w1.w = Rescale(w1.w, own, env);
            w2.w = Rescale(w2.w, own, env);
            Shader.SetGlobalVector(s_wind1, w1);
            Shader.SetGlobalVector(s_wind2, w2);
            float was = em.GetWindIntensity();
            if (was > 1e-4f)
                Shader.SetGlobalVector(s_windForce, s_windForceValue * (Rescale(was, own, env) / was));
        }

        // Within what the game allows its wind (EnvMan.SetTargetWind clamps it to 0.05..1).
        internal static float Rescale(float w, EnvSetup from, EnvSetup to) =>
            Mathf.Clamp(Mathf.Lerp(to.m_windMin, to.m_windMax, Mathf.InverseLerp(from.m_windMin, from.m_windMax, w)), 0.05f, 1f);

        // ---- the far side's own weather particles --------------------------------------------------------

        sealed class Shadow
        {
            public float Until;
            // The place it is for: its weather, and player space -> there.
            public EnvSetup Env;
            public Matrix4x4 ToFar;
            public Vector3 Place;
            // When the object follows the player itself (rain), it is simulated over there.
            public FollowPlayer Follower;
            // Mist that places itself by the ground and biome around it (MistEmitter, ParticleMist) runs over there
            // too, its particles in world space meanwhile.
            public readonly List<Mover> Movers = new List<Mover>();
            // Far.WeatherParity: the object running (the weather's own, or a clone of it when another place runs that one),
            // the weather's own object, the place it runs for and its particle systems; whether it is wanted this frame,
            // and whether it stops falling (emission off, what has fallen left to land) until FadeUntil at the latest.
            public GameObject Go, Original;
            public Place Owner;
            public ParticleSystem[] Systems;
            public bool Clone, Wanted, Fading;
            public float FadeUntil;
        }

        sealed class Mover
        {
            public Transform T;
            public FollowPlayer Follower;
            public Vector3 Local;
            public readonly List<(ParticleSystem ps, ParticleSystemSimulationSpace space, Transform custom)> Systems = new List<(ParticleSystem, ParticleSystemSimulationSpace, Transform)>();
        }

        static void FindMovers(GameObject go, Shadow s)
        {
            if (s.Follower != null)
                return;
            var spots = new List<(Transform t, ParticleSystem own)>();
            foreach (MistEmitter m in go.GetComponentsInChildren<MistEmitter>(true))
                spots.Add((m.transform, m.m_psystem));
            foreach (ParticleMist m in go.GetComponentsInChildren<ParticleMist>(true))
                spots.Add((m.transform, m.GetComponent<ParticleSystem>()));
            foreach ((Transform t, ParticleSystem own) in spots)
            {
                FollowPlayer f = t.GetComponentInParent<FollowPlayer>();
                if (f == null)
                    continue;
                var mover = new Mover { T = t, Follower = f, Local = t.localPosition };
                var systems = new HashSet<ParticleSystem>(t.GetComponentsInChildren<ParticleSystem>(true));
                if (own != null)
                    systems.Add(own);
                foreach (ParticleSystem ps in systems)
                {
                    ParticleSystem.MainModule main = ps.main;
                    mover.Systems.Add((ps, main.simulationSpace, main.customSimulationSpace));
                    main.simulationSpace = ParticleSystemSimulationSpace.World;
                }
                s.Movers.Add(mover);
            }
        }

        static void ReleaseMovers(Shadow s)
        {
            foreach (Mover m in s.Movers)
            {
                if (m.T != null)
                    m.T.localPosition = m.Local;
                foreach ((ParticleSystem ps, ParticleSystemSimulationSpace space, Transform custom) in m.Systems)
                    if (ps != null)
                    {
                        ParticleSystem.MainModule main = ps.main;
                        main.simulationSpace = space;
                        main.customSimulationSpace = custom;
                    }
            }
            s.Movers.Clear();
        }

        static readonly Dictionary<GameObject, Shadow> s_shadow = new Dictionary<GameObject, Shadow>();
        static readonly Dictionary<GameObject, (EnvSetup env, Matrix4x4 toFar, Vector3 place)> s_wanted = new Dictionary<GameObject, (EnvSetup, Matrix4x4, Vector3)>();
        static readonly List<GameObject> s_drop = new List<GameObject>();

        // Every frame, with the places the live windows look at (and player space -> each). The game runs only your
        // weather's particle systems; the ones a place over there has and yours has not (its rain, its mist) run as
        // well, drawn only in the windows onto such a place. One that follows the player on its own (the rain) runs
        // over there, so its drops splash on that ground and it is already falling around you when you arrive;
        // the others run around you like yours and are carried to each window like yours. They start, filled in at
        // once, before such a window comes into view, and get that place's wind.
        internal static void KeepFarWeather(List<(Vector3 place, Matrix4x4 toFar)> places)
        {
            EnvMan em = EnvMan.instance;
            if (em == null)
                return;
            // Far.WeatherParity switched: what the other way runs stops, and this one starts from nothing.
            bool parity = FarFlags.WeatherParity;
            if (parity != s_parityRuns)
            {
                ResetWeather(em);
                s_parityRuns = parity;
            }
            if (parity)
            {
                KeepPlaces(em, places);
                return;
            }
            GameObject[] own = s_currentPSystems(em);
            s_wanted.Clear();
            foreach ((Vector3 p, Matrix4x4 toFar) in places)
            {
                EnvSetup env = Pick(p);
                if (env == null)
                    continue;
                WantStorm(em, env, toFar);
                if (env.m_psystems == null)
                    continue;
                foreach (GameObject go in env.m_psystems)
                    if (go != null && (own == null || Array.IndexOf(own, go) < 0) && !s_wanted.ContainsKey(go))
                        s_wanted[go] = (env, toFar, p);
            }
            float now = Time.time;
            foreach (var kv in s_wanted)
            {
                if (!s_shadow.TryGetValue(kv.Key, out Shadow s))
                {
                    s = new Shadow { Env = kv.Value.env, ToFar = kv.Value.toFar, Place = kv.Value.place, Follower = kv.Key.GetComponent<FollowPlayer>() };
                    s_shadow[kv.Key] = s;
                    StartShadow(kv.Key, s, true);
                }
                s.Env = kv.Value.env;
                s.ToFar = kv.Value.toFar;
                s.Place = kv.Value.place;
                s.Until = now + 3f;
            }
            UpdateStorms(em, now);
            TrackWet(em, places);
            // Ones no window has wanted for a while stop; ones the game runs as yours now are its own again.
            s_drop.Clear();
            foreach (var kv in s_shadow)
                if (kv.Key == null || (own != null && Array.IndexOf(own, kv.Key) >= 0) || now > kv.Value.Until)
                    s_drop.Add(kv.Key);
            foreach (GameObject go in s_drop)
            {
                if (go != null)
                    StopShadow(go, own != null && Array.IndexOf(own, go) >= 0);
                s_shadow.Remove(go);
            }
        }

        // Hidden from the game camera; started and filled in as the game does on an instant switch, unless it runs
        // already (the game's until a moment ago).
        static void StartShadow(GameObject go, Shadow s, bool start)
        {
            s.Until = Time.time + 3f;
            foreach (Renderer r in RenderersOf(go))
                if (r != null)
                    r.forceRenderingOff = true;
            FindMovers(go, s);
            if (s.Follower != null)
                PlaceAway(s.Follower);
            foreach (Mover m in s.Movers)
                PlaceAway(m.Follower);
            if (start)
                SwitchLikeGame(go, true, true);
            foreach (GlobalWind gw in go.GetComponentsInChildren<GlobalWind>())
                FarWind(gw);
        }

        // From the FollowPlayer.LateUpdate postfix: a weather object that runs for a window only is placed where the
        // game would place it for you standing over there.
        internal static void PlaceAway(FollowPlayer f)
        {
            foreach (Shadow ms in s_shadow.Values)
                foreach (Mover m in ms.Movers)
                    if (m.Follower == f && m.T != null)
                    {
                        Vector3 natural = m.T.parent != null ? m.T.parent.TransformPoint(m.Local) : m.Local;
                        m.T.position = AlreadyThere(ms, natural) ? natural : ms.ToFar.MultiplyPoint3x4(natural);
                    }
            if (!s_shadow.TryGetValue(f.gameObject, out Shadow s) || s.Follower != f)
                return;
            Transform t = f.transform;
            // You have stepped through already (the camera not yet): the game put it over there itself.
            if (AlreadyThere(s, t.position))
                return;
            Vector3 to = s.ToFar.MultiplyPoint3x4(t.position);
            // The height it keeps is already over there's.
            if (f.m_lockYPos)
                to.y = t.position.y;
            if (to.y > f.m_maxYPos)
                to.y = f.m_maxYPos;
            t.position = to;
        }

        static bool AlreadyThere(Shadow s, Vector3 p) => (p - s.Place).sqrMagnitude < (s.ToFar.MultiplyPoint3x4(p) - s.Place).sqrMagnitude;

        // Far.LinkScaling: each GlobalWind's object and its parents up to the root (weather objects are not moved to
        // other parents). Cleared when it grows large (far areas come and go).
        static readonly Dictionary<GlobalWind, GameObject[]> s_windChain = new Dictionary<GlobalWind, GameObject[]>();
        static readonly List<GameObject> s_chainScratch = new List<GameObject>();

        // From the GlobalWind.UpdateWind postfix: the weather particles running for a window fly with that place's
        // wind (the game gives every system yours).
        internal static void FarWind(GlobalWind gw)
        {
            // Far.LocalWeatherSim: the game's own update has just run with the wind of the far place it stands in (FarWet).
            if (FarWet.InContext)
                return;
            Shadow s = null;
            if (FarFlags.LinkScaling)
            {
                // Far.LinkScaling: every GlobalWind in the loaded far areas (banners, cloth, smoke) comes through here,
                // some every frame, and each walked up its parents looking for a weather shadow. None to find while no
                // window runs another place's weather; otherwise its parents are looked up once and kept.
                if (s_shadow.Count == 0)
                    return;
                if (!s_windChain.TryGetValue(gw, out GameObject[] chain))
                {
                    s_chainScratch.Clear();
                    for (Transform t = gw.transform; t != null; t = t.parent)
                        s_chainScratch.Add(t.gameObject);
                    chain = s_chainScratch.ToArray();
                    if (s_windChain.Count >= 16384)
                        s_windChain.Clear();
                    s_windChain[gw] = chain;
                }
                for (int i = 0; i < chain.Length && s == null; i++)
                    s_shadow.TryGetValue(chain[i], out s);
            }
            else
                for (Transform t = gw.transform; t != null && s == null; t = t.parent)
                    s_shadow.TryGetValue(t.gameObject, out s);
            EnvMan em = EnvMan.instance;
            EnvSetup own = em != null ? em.GetCurrentEnvironment() : null;
            if (s?.Env == null || own == null || em.m_debugWind || !gw.TryGetComponent(out ParticleSystem ps) || !ps.emission.enabled)
                return;
            float was = em.GetWindIntensity(), now = Rescale(was, own, s.Env);
            Vector3 force = em.GetWindForce() * (was > 1e-4f ? now / was : 1f) * gw.m_multiplier;
            if (gw.m_particleVelocity)
            {
                ParticleSystem.VelocityOverLifetimeModule v = ps.velocityOverLifetime;
                v.space = ParticleSystemSimulationSpace.World;
                v.x = force.x;
                v.z = force.z;
            }
            if (gw.m_particleForce)
            {
                ParticleSystem.ForceOverLifetimeModule f = ps.forceOverLifetime;
                f.space = ParticleSystemSimulationSpace.World;
                f.x = force.x;
                f.z = force.z;
            }
            if (gw.m_particleEmission)
            {
                ParticleSystem.EmissionModule e = ps.emission;
                e.rateOverTimeMultiplier = Mathf.Lerp(gw.m_particleEmissionMin, gw.m_particleEmissionMax, now);
            }
        }

        // ---- how wet the ground is over there --------------------------------------------------------------

        static readonly AccessTools.FieldRef<EnvMan, float> s_wetDuration = AccessTools.FieldRefAccess<EnvMan, float>("m_wetTransitionDuration");
        static readonly List<(Vector3 place, float wet, float seenAt)> s_farWet = new List<(Vector3, float, float)>();

        // The game soaks or dries the ground over a while when its weather changes. Each far place keeps its own
        // the same way from when a window first looks at it, so where the rain has just stopped it is still wet.
        static void TrackWet(EnvMan em, List<(Vector3 place, Matrix4x4 toFar)> places)
        {
            float now = Time.time, step = Time.deltaTime / Mathf.Max(0.01f, s_wetDuration(em));
            bool parity = FarFlags.WeatherParity;
            foreach ((Vector3 place, Matrix4x4 _) in places)
            {
                EnvSetup far = Pick(place);
                EnvSetup env = far ?? em.GetCurrentEnvironment();
                if (env == null)
                    continue;
                // Far.WeatherParity: during a blend the ground soaks or dries toward the weather before it, as the game's
                // interpolated environment keeps its rain until the blend ends.
                float target = (parity && far != null ? LookOf(PlaceNear(place), far) : env).m_isWet ? 1f : 0f;
                int i = -1;
                for (int k = 0; k < s_farWet.Count && i < 0; k++)
                    if ((s_farWet[k].place - place).sqrMagnitude < 1f)
                        i = k;
                // First seen: as wet as yours if the weather there is yours, else as that weather makes it (Far.WeatherParity:
                // as the weather before this period left it, soaked or dried since the period began).
                float wet = i >= 0 ? s_farWet[i].wet : far == null ? Shader.GetGlobalFloat(s_wet) : parity ? WetStart(em, place, target) : target;
                wet = Mathf.MoveTowards(wet, target, step);
                if (i >= 0)
                    s_farWet[i] = (place, wet, now);
                else
                    s_farWet.Add((place, wet, now));
            }
            for (int k = s_farWet.Count - 1; k >= 0; k--)
                if (now - s_farWet[k].seenAt > 60f)
                    s_farWet.RemoveAt(k);
        }

        // The wetness kept for the place nearest to pos, or what the weather makes it.
        static float WetAt(Vector3 pos, EnvSetup env)
        {
            float best = 400f * 400f, wet = env.m_isWet ? 1f : 0f;
            foreach ((Vector3 place, float w, float _) in s_farWet)
            {
                float d = (place - pos).sqrMagnitude;
                if (d < best)
                {
                    best = d;
                    wet = w;
                }
            }
            return wet;
        }

        // From the EnvMan.GetCurrentBiome postfix: the biome its sounds and music go by, where you stand while the
        // camera looks back through a portal.
        internal static void PlayerBiome(ref Heightmap.Biome biome)
        {
            PortalSystem sys = PortalSystem.Instance;
            Player p = Player.m_localPlayer;
            if (sys == null || p == null || WorldGenerator.instance == null || !sys.CameraThroughRecently)
                return;
            biome = WorldGenerator.instance.GetBiome(p.transform.position);
        }

        // ---- weather where you stand ----------------------------------------------------------------------

        static readonly System.Reflection.FieldInfo s_isWet = AccessTools.Field(typeof(EnvMan), "s_isWet");
        static readonly System.Reflection.FieldInfo s_isCold = AccessTools.Field(typeof(EnvMan), "s_isCold");
        static readonly System.Reflection.FieldInfo s_isFreezing = AccessTools.Field(typeof(EnvMan), "s_isFreezing");

        // The game takes its weather from where the camera is. While the camera arm still reaches back through a
        // portal (you have just stepped through, or you turn to look back), that is the other place's: you would stay
        // dry in the rain you stand in and hear the rain you have left. The picture keeps the camera's weather (as
        // the game does); getting wet, cold and freezing, and the weather's sound, follow where you stand.
        // Null when that is the camera's weather anyway.
        internal static EnvSetup PlayerWeather()
        {
            PortalSystem sys = PortalSystem.Instance;
            Player p = Player.m_localPlayer;
            if (sys == null || p == null || !sys.CameraThroughRecently)
                return null;
            if (!FarFlags.WeatherParity)
                return Pick(p.transform.position);
            // Far.WeatherParity: an override of yours (your EnvZone, an event around you; EnvMan finds those by where you
            // stand) is the game's weather already; otherwise the pick of your point alone, no window's place.
            EnvMan em = EnvMan.instance;
            if (em == null || !string.IsNullOrEmpty(s_forceEnv(em)) || s_envPeriod(em) == -1L)
                return null;
            return Pick(p.transform.position, false);
        }

        // From the EnvMan.FixedUpdate postfix, after it set its weather flags from the camera's weather.
        internal static void PlayerFlags()
        {
            EnvSetup env = PlayerWeather();
            if (env == null)
                return;
            bool night = !EnvMan.IsDay();
            s_isWet.SetValue(null, env.m_isWet);
            s_isCold.SetValue(null, env.m_isCold || (env.m_isColdAtNight && night));
            s_isFreezing.SetValue(null, env.m_isFreezing || (env.m_isFreezingAtNight && night));
        }

        // From the AudioMan.QueueAmbientLoop / StopAmbientLoop prefixes: when EnvMan.SetEnv sets the weather's sound,
        // the sound of the weather where you stand. False: done here.
        internal static bool AmbientLoop(AudioMan audio, ref AudioClip clip, ref float vol)
        {
            EnvSetup env = Setting != null ? PlayerWeather() : null;
            if (env == null)
                return true;
            if (env.m_ambientLoop == null)
            {
                audio.StopAmbientLoop();
                return false;
            }
            clip = env.m_ambientLoop;
            vol = env.m_ambientVol;
            return true;
        }

        internal static bool AmbientStop(AudioMan audio)
        {
            EnvSetup env = Setting != null ? PlayerWeather() : null;
            if (env?.m_ambientLoop == null)
                return true;
            audio.QueueAmbientLoop(env.m_ambientLoop, env.m_ambientVol);
            return false;
        }

        // ---- the far side's lightning -------------------------------------------------------------------

        sealed class Storm
        {
            public Thunder T;
            public Matrix4x4 ToFar;
            public EnvSetup Env;
            public float Until, NextAt, ThunderAt = -1f;
            public Vector3 FlashAt;
            public readonly List<GameObject> Flashes = new List<GameObject>();
        }

        static readonly Dictionary<Thunder, Storm> s_storms = new Dictionary<Thunder, Storm>();
        static readonly List<Thunder> s_stormDrop = new List<Thunder>();

        // A place over there with a thunderstorm and you without: its lightning strikes over there the way the game's
        // Thunder does, seen only in the windows onto it.
        static void WantStorm(EnvMan em, EnvSetup env, Matrix4x4 toFar)
        {
            if (env.m_envObject == null || env.m_envObject == s_envObject(em))
                return;
            Thunder t = env.m_envObject.GetComponentInChildren<Thunder>(true);
            if (t == null)
                return;
            if (!s_storms.TryGetValue(t, out Storm s))
                s_storms[t] = s = new Storm { T = t, NextAt = Time.time + UnityEngine.Random.Range(t.m_strikeIntervalMin, t.m_strikeIntervalMax) };
            s.ToFar = toFar;
            s.Env = env;
            s.Until = Time.time + 3f;
        }

        static void UpdateStorms(EnvMan em, float now)
        {
            s_stormDrop.Clear();
            Player p = Player.m_localPlayer;
            foreach (Storm s in s_storms.Values)
                if (!StepStorm(em, s, now, p) && s.T != null)
                    s_stormDrop.Add(s.T);
            foreach (Thunder t in s_stormDrop)
                s_storms.Remove(t);
        }

        // One storm's frame: its thunder, and its next strike when due. False: it is over (no window wants it, or the
        // storm is yours now).
        static bool StepStorm(EnvMan em, Storm s, float now, Player p)
        {
            s.Flashes.RemoveAll(f => f == null);
            if (s.T == null || now > s.Until || s.T.gameObject == s_envObject(em))
                return false;
            // Its thunder a few seconds after the flash, heard as if through the portal: from where the flash
            // would be with that place laid over yours.
            if (s.ThunderAt > 0f && now >= s.ThunderAt)
            {
                s.ThunderAt = -1f;
                s.T.m_thunderEffect.Create(s.ToFar.inverse.MultiplyPoint3x4(s.FlashAt), Quaternion.identity);
                LabFarThunders++;
            }
            if (now < s.NextAt || p == null)
                return true;
            s.NextAt = now + UnityEngine.Random.Range(s.T.m_strikeIntervalMin, s.T.m_strikeIntervalMax);
            // As Thunder.DoFlash, around the player standing over there.
            Vector3 center = s.ToFar.MultiplyPoint3x4(p.transform.position);
            float a = UnityEngine.Random.value * Mathf.PI * 2f, d = UnityEngine.Random.Range(s.T.m_flashDistanceMin, s.T.m_flashDistanceMax);
            Vector3 at = center + new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a)) * d + Vector3.up * s.T.m_flashAltitude;
            Quaternion turn = Quaternion.LookRotation((center - at).normalized);
            GameObject[] made = s.T.m_flashEffect.Create(at, Quaternion.identity);
            if (made == null)
                return true;
            LabFarFlashes++;
            s.FlashAt = at;
            s.ThunderAt = now + UnityEngine.Random.Range(s.T.m_thunderDelayMin, s.T.m_thunderDelayMax);
            foreach (GameObject flash in made)
            {
                foreach (Light l in flash.GetComponentsInChildren<Light>())
                {
                    l.transform.rotation = turn;
                    l.enabled = false;
                }
                foreach (Renderer r in flash.GetComponentsInChildren<Renderer>())
                    r.forceRenderingOff = true;
                // Far away: not heard here.
                foreach (AudioSource src in flash.GetComponentsInChildren<AudioSource>())
                    src.mute = true;
                s.Flashes.Add(flash);
            }
            return true;
        }

        static readonly List<Light> s_onLights = new List<Light>();
        // Lab: lightning strikes made over there so far.
        internal static int LabFarFlashes, LabFlashesShown, LabFarThunders;

        // In a window onto a place with a storm: its lightning.
        static void ShowStorms(EnvSetup env)
        {
            foreach (Storm s in s_storms.Values)
                if (s.Env == env)
                    ShowFlashes(s);
        }

        static void ShowFlashes(Storm s)
        {
            foreach (GameObject flash in s.Flashes)
            {
                if (flash == null)
                    continue;
                if (WindowFlags.EnvGroups)
                {
                    Flash parts = PartsOf(flash);
                    foreach (Light l in parts.Lights)
                        if (l != null && !l.enabled && l.gameObject.activeInHierarchy)
                        {
                            l.enabled = true;
                            s_onLights.Add(l);
                            LabFlashesShown++;
                        }
                    foreach (Renderer r in parts.Renderers)
                        if (r != null && r.forceRenderingOff && r.gameObject.activeInHierarchy)
                        {
                            r.forceRenderingOff = false;
                            s_shown.Add(r);
                        }
                    continue;
                }
                foreach (Light l in flash.GetComponentsInChildren<Light>())
                    if (!l.enabled)
                    {
                        l.enabled = true;
                        s_onLights.Add(l);
                        LabFlashesShown++;
                    }
                foreach (Renderer r in flash.GetComponentsInChildren<Renderer>())
                    if (r.forceRenderingOff)
                    {
                        r.forceRenderingOff = false;
                        s_shown.Add(r);
                    }
            }
        }

        static void StopShadow(GameObject go, bool gameRunsIt)
        {
            if (s_shadow.TryGetValue(go, out Shadow s))
                ReleaseMovers(s);
            foreach (Renderer r in RenderersOf(go))
                if (r != null)
                    r.forceRenderingOff = false;
            if (!gameRunsIt)
                SwitchLikeGame(go, false, true);
            // Far.WeatherParity: one that was stopping falling goes on as the game's own.
            else if (s != null && s.Fading)
                SwitchLikeGame(go, true, false);
        }

        // As EnvMan.SetParticleArrayEnabled does for one of its objects.
        static void SwitchLikeGame(GameObject go, bool enabled, bool instant)
        {
            foreach (ParticleSystem ps in go.GetComponentsInChildren<ParticleSystem>())
            {
                ParticleSystem.EmissionModule emission = ps.emission;
                emission.enabled = enabled;
                if (instant && !enabled)
                    ps.Clear();
                if (instant && enabled)
                {
                    ps.Simulate(15f);
                    ps.Play();
                }
            }
            MistEmitter mist = go.GetComponentInChildren<MistEmitter>();
            if (mist != null)
                mist.enabled = enabled;
        }

        // The weather EnvMan.SetEnv is setting (from its prefix), while it switches the particles over.
        internal static EnvSetup Setting;

        // From the EnvMan.SetParticleArrayEnabled prefix (false: done here). When your weather changes at once (as it
        // does when you step through), the game clears the old weather's particles and starts the new one's over,
        // also the ones both have (the mist and clouds of fair and rainy weather alike): the haze would take on a
        // new pattern right as you arrive. Here those carry on as they are, the ones a window already runs for the
        // new weather carry on too, and the old weather's that a window still wants keep running for it.
        internal static bool GameSwitchesWeather(EnvMan em, GameObject[] psystems, bool enabled)
        {
            if (psystems == null)
                return true;
            bool instant = s_instantTimer(em) > 0f;
            foreach (GameObject go in psystems)
            {
                if (go == null)
                    continue;
                if (enabled)
                {
                    if (s_shadow.TryGetValue(go, out Shadow was))
                    {
                        StopShadow(go, true);
                        s_shadow.Remove(go);
                        // Far.WeatherParity: no longer that place's.
                        was.Owner?.Shadows.Remove(was);
                    }
                    else
                        SwitchLikeGame(go, true, instant && !Emitting(go));
                }
                else if (Setting?.m_psystems != null && Array.IndexOf(Setting.m_psystems, go) >= 0)
                    continue;
                // Far.WeatherParity: a place a window looks at has this weather too (yours ran for it): it goes on there as
                // it is, run for that place.
                else if (FarFlags.WeatherParity && s_handTo.TryGetValue(go, out Place to) && s_places.Contains(to))
                {
                    if (!s_shadow.ContainsKey(go))
                        Adopt(to, go);
                }
                else if (s_wanted.TryGetValue(go, out var want))
                {
                    if (!s_shadow.ContainsKey(go))
                    {
                        var s = new Shadow { Env = want.env, ToFar = want.toFar, Place = want.place, Follower = go.GetComponent<FollowPlayer>() };
                        s_shadow[go] = s;
                        StartShadow(go, s, false);
                    }
                }
                else
                    SwitchLikeGame(go, false, instant);
            }
            return false;
        }

        // Running already: one of its systems emits and has particles (the object's own may be an empty parent).
        static bool Emitting(GameObject go)
        {
            foreach (ParticleSystem ps in go.GetComponentsInChildren<ParticleSystem>())
                if (ps.emission.enabled && ps.particleCount > 0)
                    return true;
            return false;
        }

        static readonly AccessTools.FieldRef<EnvMan, Vector4> s_windDir1 = AccessTools.FieldRefAccess<EnvMan, Vector4>("m_windDir1");
        static readonly AccessTools.FieldRef<EnvMan, Vector4> s_windDir2 = AccessTools.FieldRefAccess<EnvMan, Vector4>("m_windDir2");
        static readonly AccessTools.FieldRef<EnvMan, Vector4> s_windNow = AccessTools.FieldRefAccess<EnvMan, Vector4>("m_wind");
        static readonly Action<EnvMan, float> s_updateWindTransition =
            AccessTools.MethodDelegate<Action<EnvMan, float>>(AccessTools.Method(typeof(EnvMan), "UpdateWindTransition", new[] { typeof(float) }));

        // On arriving in another weather: the wind as strong as it is there (as the window showed it), at once;
        // the game would glide its strength over ten seconds or more.
        static void WindArrives(EnvMan em, EnvSetup from, EnvSetup to)
        {
            if (from == null || to == null || em.m_debugWind || (from.m_windMin == to.m_windMin && from.m_windMax == to.m_windMax))
                return;
            Vector4 w1 = s_windDir1(em), w2 = s_windDir2(em), w = s_windNow(em);
            w1.w = Mathf.Clamp(Rescale(w1.w, from, to), 0.05f, 1f);
            w2.w = Mathf.Clamp(Rescale(w2.w, from, to), 0.05f, 1f);
            w.w = Rescale(w.w, from, to);
            s_windDir1(em) = w1;
            s_windDir2(em) = w2;
            s_windNow(em) = w;
            s_updateWindTransition(em, 0f);
        }

        // The light, fog and sky parts of EnvMan.SetEnv.
        static void Set(EnvMan em, EnvSetup env, float wet)
        {
            Light light = em.m_dirLight;
            Material clouds = em.m_clouds != null ? em.m_clouds.sharedMaterial : null;
            float day = s_day, night = s_night, morning = s_morning, evening = s_evening;
            light.transform.rotation = Quaternion.Euler(-90f + env.m_sunAngle, 0f, 0f) * Quaternion.Euler(0f, -90f, 0f) *
                                       Quaternion.Euler(-90f + 360f * s_smoothDay(em), 0f, 0f);
            Vector3 skyDir = -light.transform.forward;
            light.intensity = env.m_lightIntensityDay * day + env.m_lightIntensityNight * night;
            if (night > 0f)
                light.transform.rotation *= Quaternion.Euler(180f, 0f, 0f);
            Color sun = env.m_sunColorNight * night;
            if (day > 0f)
                sun += env.m_sunColorDay * day + env.m_sunColorMorning * morning + env.m_sunColorEvening * evening;
            light.color = sun;
            Color fog = env.m_fogColorNight * night + env.m_fogColorDay * day + env.m_fogColorMorning * morning + env.m_fogColorEvening * evening;
            RenderSettings.fogColor = fog;
            Color sunFog = env.m_fogColorSunNight * night;
            if (day > 0f)
                sunFog += env.m_fogColorSunDay * day + env.m_fogColorSunMorning * morning + env.m_fogColorSunEvening * evening;
            sunFog = Color.Lerp(fog, sunFog, Mathf.Clamp01(Mathf.Max(night, day) * 3f));
            RenderSettings.fogDensity = env.m_fogDensityNight * night + env.m_fogDensityDay * day + env.m_fogDensityMorning * morning + env.m_fogDensityEvening * evening;
            RenderSettings.ambientLight = Color.Lerp(env.m_ambColorNight, env.m_ambColorDay, day);
            if (clouds != null)
            {
                clouds.SetFloat(s_rain, env.m_rainCloudAlpha);
                clouds.SetFloat(s_opacity, env.m_cloudOpacityDay * day + env.m_cloudOpacityEvening * evening + env.m_cloudOpacityMorning * morning + env.m_cloudOpacityNight * night);
            }
            Shader.SetGlobalVector(s_skyboxSunDir, skyDir);
            Shader.SetGlobalVector(s_sunDir, -light.transform.forward);
            Shader.SetGlobalColor(s_sunFogColor, sunFog);
            Shader.SetGlobalColor(s_sunColor, light.color * light.intensity);
            Shader.SetGlobalColor(s_ambientColor, RenderSettings.ambientLight);
            Shader.SetGlobalFloat(s_wet, wet);
            Shader.SetGlobalFloat(s_aurora, env.m_auroraIntensityDay * day + env.m_auroraIntensityEvening * evening + env.m_auroraIntensityMorning * morning + env.m_auroraIntensityNight * night);
            LabLastSet = LabState(em);
        }

        internal static string LabLastSet = "";
        // Lab: the environment a window renders with right now (null: the game's own).
        internal static string LabApplied;

        // ---- Window Pipeline v2 (S2/S3): what a slot camera and the window scheduler read of the far environment ------

        // The environment the window render under way has set (null: the game's own), set and cleared with LabApplied.
        // A slot camera's own SSAO takes that place's tint and strength from it (AoSync), as EnvMan.SetEnv gives them to
        // the game camera's (CameraEffects.SetEnvironmentAOParams): one camera per slot can carry them, the shared camera
        // could not.
        internal static EnvSetup Applied { get; private set; }

        // EnvMan.SetEnv's SSAO part for `env` at the day weights the game lit the world with.
        internal static void AoOf(EnvSetup env, out Color tint, out float intensity)
        {
            tint = env.m_ambientOcclusionColor;
            intensity = env.m_aoIntensityDay * s_day + env.m_aoIntensityEvening * s_evening + env.m_aoIntensityNight * s_night
                        + env.m_aoIntensityMorning * s_morning;
        }

        // The name of the weather a window onto the player's own weather shows (the game's current one; null before it has
        // one). By name: while the game blends into a new weather, EnvMan makes a new EnvSetup (a clone, named after the
        // weather it blends into) every frame, so the object changes every frame but the name once, when the blend starts.
        internal static string GameEnvName
        {
            get
            {
                EnvMan em = EnvMan.instance;
                EnvSetup env = em != null ? s_current(em) : null;
                return env != null ? env.m_name : null;
            }
        }

        // A lightning flash of a far storm over `env` is alive (lit in the windows onto it, ShowStorms).
        internal static bool FarFlashing(EnvSetup env)
        {
            if (env == null || (s_storms.Count == 0 && s_places.Count == 0))
                return false;
            foreach (Storm s in s_storms.Values)
                if (s.Env == env && Flashing(s))
                    return true;
            // Far.WeatherParity: each place's own storm.
            for (int k = 0; k < s_places.Count; k++)
            {
                Storm s = s_places[k].Storm;
                if (s != null && s.Env == env && Flashing(s))
                    return true;
            }
            return false;
        }

        static bool Flashing(Storm s)
        {
            for (int i = 0; i < s.Flashes.Count; i++)
                if (s.Flashes[i] != null)
                    return true;
            return false;
        }

        // A flash of the player's own storm is alive (lit in every view of the player's own weather).
        internal static bool OwnFlashing
        {
            get
            {
                for (int i = 0; i < s_flashes.Count; i++)
                    if (s_flashes[i] != null)
                        return true;
                return false;
            }
        }

        // Lab: the light, fog and ambient as they are now.
        internal static string LabState(EnvMan em)
        {
            Light l = em.m_dirLight;
            return $"light rot {l.transform.rotation.eulerAngles:F0} int {l.intensity:0.00} col {l.color} fog {RenderSettings.fogColor} dens {RenderSettings.fogDensity:0.0000} amb {RenderSettings.ambientLight} " +
                   $"sunfog {Shader.GetGlobalColor(s_sunFogColor)} sunCol {Shader.GetGlobalColor(s_sunColor)} wet {Shader.GetGlobalFloat(s_wet):0.0} weights d{s_day:0.00} n{s_night:0.00} m{s_morning:0.00} e{s_evening:0.00} smooth {s_smoothDay(em):0.000} current {s_current(em)?.m_name} int {s_current(em)?.m_lightIntensityDay:0.00} next {s_next(em)?.m_name ?? "-"}";
        }

        // After a window render (Win.EnvGroups off), after the last window of a frame, after a probe face.
        internal static void Restore()
        {
            RestoreOne();
            EndGrouped();
        }

        static void RestoreOne()
        {
            if (!s_applied)
                return;
            s_applied = false;
            LabApplied = null;
            Applied = null;
            AuroraBack();
            foreach (Renderer r in s_hidden)
                if (r != null)
                    r.forceRenderingOff = false;
            s_hidden.Clear();
            foreach (Renderer r in s_shown)
                if (r != null)
                    r.forceRenderingOff = true;
            s_shown.Clear();
            foreach (Light l in s_offLights)
                if (l != null)
                    l.enabled = true;
            s_offLights.Clear();
            foreach (Light l in s_onLights)
                if (l != null)
                    l.enabled = false;
            s_onLights.Clear();
            if (s_windSet)
            {
                s_windSet = false;
                Shader.SetGlobalVector(s_wind1, s_wind1Value);
                Shader.SetGlobalVector(s_wind2, s_wind2Value);
                Shader.SetGlobalVector(s_windForce, s_windForceValue);
            }
            EnvMan em = EnvMan.instance;
            if (em == null || em.m_dirLight == null)
                return;
            Light light = em.m_dirLight;
            light.transform.rotation = s_lightRot;
            light.intensity = s_lightIntensity;
            light.color = s_lightColor;
            RenderSettings.fogColor = s_fogColor;
            RenderSettings.fogDensity = s_fogDensity;
            RenderSettings.ambientLight = s_ambient;
            Material clouds = em.m_clouds != null ? em.m_clouds.sharedMaterial : null;
            if (clouds != null)
            {
                clouds.SetFloat(s_rain, s_rainValue);
                clouds.SetFloat(s_opacity, s_opacityValue);
            }
            Shader.SetGlobalVector(s_skyboxSunDir, s_skyDir);
            Shader.SetGlobalVector(s_sunDir, s_sunDirValue);
            Shader.SetGlobalColor(s_sunFogColor, s_sunFog);
            Shader.SetGlobalColor(s_sunColor, s_sunCol);
            Shader.SetGlobalColor(s_ambientColor, s_ambientGlobal);
            Shader.SetGlobalFloat(s_wet, s_wetValue);
            Shader.SetGlobalFloat(s_aurora, s_auroraValue);
        }

        // The game camera has just come through a portal into another biome (EnvMan picks the weather by where the
        // camera is). The game would blend over a couple of seconds and let the ground dry or soak over 15 s, but
        // the window already showed it as it is there: switch at once, from this very frame.
        internal static void Arrived(Vector3 camPos)
        {
            EnvMan em = EnvMan.instance;
            if (em == null || s_applied || AreaRenderState.Open || !s_have || em.m_dirLight == null)
                return;
            EnvSetup env = Pick(camPos);
            if (env == null)
                return;
            em.ForceInstantEnvironmentSwitch();
            // The game itself picks and sets it now rather than at its next fixed update, so this frame already
            // has that place's rain and mist, not one frame of yours.
            try
            {
                EnvSetup was = em.GetCurrentEnvironment();
                s_updateEnvironment(em, (long)ZNet.instance.GetTimeSeconds(), s_getBiome(em));
                s_interpolate(em, 0f);
                EnvSetup now = em.GetCurrentEnvironment();
                if (now != null)
                    s_setEnv(em, now, s_day, s_night, s_morning, s_evening, 0f);
                WindArrives(em, was, now);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Could not switch the weather at once: " + e.Message);
            }
            // As wet as the window showed it; the game goes on soaking or drying it from there.
            Set(em, env, WetAt(camPos, env));
        }

        static readonly Action<EnvMan, long, BiomeSector> s_updateEnvironment =
            AccessTools.MethodDelegate<Action<EnvMan, long, BiomeSector>>(AccessTools.Method(typeof(EnvMan), "UpdateEnvironment", new[] { typeof(long), typeof(BiomeSector) }));
        static readonly Func<EnvMan, BiomeSector> s_getBiome =
            AccessTools.MethodDelegate<Func<EnvMan, BiomeSector>>(AccessTools.Method(typeof(EnvMan), "GetBiome", Type.EmptyTypes));
        static readonly Action<EnvMan, float> s_interpolate =
            AccessTools.MethodDelegate<Action<EnvMan, float>>(AccessTools.Method(typeof(EnvMan), "InterpolateEnvironment", new[] { typeof(float) }));
        static readonly Action<EnvMan, EnvSetup, float, float, float, float, float> s_setEnv =
            AccessTools.MethodDelegate<Action<EnvMan, EnvSetup, float, float, float, float, float>>(AccessTools.Method(typeof(EnvMan), "SetEnv", new[] { typeof(EnvSetup), typeof(float), typeof(float), typeof(float), typeof(float), typeof(float) }));

        // ---- Far.WeatherParity (Docs\PARITY-PLAN.md P21, P34, P46, and P33's aurora): far weather as in place -----------
        // The game keeps one weather, the camera's, and runs it as SetEnv does: blended over m_transitionDuration when the
        // period (or an override) changes; its particles switched when that blend ends, new ones starting to fall and old
        // ones only stopping to emit; the outside-only ones (rain, snow) started only while you are out of shelter; its
        // overrides found where you stand. Each far place a live window looks at gets the same, as the game would run it
        // for you standing at its far point (where you would stand over there): its shelter and overrides tested there
        // once a second, its own blend, its own weather objects (a clone when another place runs that weather already)
        // and its own storm. Filled in at once only when a place is first seen (or seen again after a while), as the game
        // fills in on an instant switch. Off: your shelter and forced environment, changes that snap, rain shown already
        // fallen, one weather object for every place with that weather.

        sealed class Place
        {
            // The far portal's centre (the key, as for the wetness), player space -> there, when and in which frame it was
            // last in the list, the period its weather is of, and whether it was seen within the second before this frame
            // (what changes there now changes while watched).
            public Vector3 At;
            public Matrix4x4 ToFar;
            public float SeenAt = -100f, CheckAt;
            public int Frame = -1;
            public long Period = long.MinValue;
            public bool Watched;
            // The far point's override (its EnvZone, an event there; null: none) and shelter, as of the last check.
            public EnvSetup Override;
            public bool Sheltered;
            // The weather there (Target) and the one it blends from (From, null: no blend) since BlendAt, how far (T),
            // and the blend itself: a clone of From refilled every frame, as EnvMan's interpolated environment.
            public EnvSetup Target, From, Mix, MixFrom;
            public float BlendAt, T;
            // The weather whose particles run there (switched when a blend ends), whether its outside-only ones run
            // (started while the far point was out of shelter), and whether they start this frame while watched (then
            // they start falling rather than filled in).
            public EnvSetup Systems;
            public bool Running, Starting;
            public readonly List<Shadow> Shadows = new List<Shadow>();
            public Storm Storm;
        }

        sealed class WeatherClone
        {
            public GameObject Original, Go;
            public float FreeAt;
        }

        static readonly List<Place> s_places = new List<Place>();
        static readonly List<WeatherClone> s_clones = new List<WeatherClone>();
        static readonly Dictionary<GameObject, bool> s_cloneable = new Dictionary<GameObject, bool>();
        // Your running weather objects a place has too (it runs them for itself once yours stop them).
        static readonly Dictionary<GameObject, Place> s_handTo = new Dictionary<GameObject, Place>();
        static readonly Collider[] s_zoneHits = new Collider[32];
        // Places with an override or a blend now (Pick looks for the place only then), and whether KeepFarWeather last
        // ran with Far.WeatherParity.
        static int s_placeStates;
        static bool s_parityRuns;
        // Win.EnvGroups: the place whose weather particles are swapped in now, and the weather of that view.
        static bool s_placeSwapped;
        static Place s_swapPlace;
        static EnvSetup s_swapKeyEnv;
        // Clones at most (each one more weather simulated), how long an unused one is kept (inactive; few are ever made),
        // and how long old weather particles may take to land.
        const int MaxClones = 16;
        const float CloneKeep = 300f, FadeMax = 20f;
        // Shelter and override tests of places seen before, at most this many a frame.
        const int ChecksPerFrame = 2;
        static int s_checkFrame = -1, s_checks;
        static bool s_overrideWarned;

        // Lab: places with an override now, places whose outside-only particles their shelter holds back now; blends
        // started and weather clones made (cumulative).
        internal static int LabPlaceOverrides, LabPlaceSheltered;
        internal static long LabPlaceBlends, LabWeatherClones;

        static readonly AccessTools.FieldRef<EnvMan, long> s_envPeriod = AccessTools.FieldRefAccess<EnvMan, long>("m_environmentPeriod");
        static readonly AccessTools.FieldRef<RandEventSystem, RandomEvent> s_randomEvent = AccessTools.FieldRefAccess<RandEventSystem, RandomEvent>("m_randomEvent");
        static readonly AccessTools.FieldRef<RandEventSystem, RandomEvent> s_forcedEvent = AccessTools.FieldRefAccess<RandEventSystem, RandomEvent>("m_forcedEvent");
        static readonly int s_auroraCurrent = Shader.PropertyToID("_AuroraGradientCurrent");
        static readonly int s_auroraNext = Shader.PropertyToID("_AuroraGradientNext");
        static readonly int s_auroraBlend = Shader.PropertyToID("_AuroraGradientBlend");

        // Every frame (KeepFarWeather) with the places the live windows look at and player space -> each.
        static void KeepPlaces(EnvMan em, List<(Vector3 place, Matrix4x4 toFar)> places)
        {
            float now = Time.time;
            int frame = Time.frameCount;
            GameObject[] own = s_currentPSystems(em);
            GameObject ownObject = s_envObject(em);
            s_wanted.Clear();
            s_handTo.Clear();
            double net = ZNet.instance != null ? ZNet.instance.GetTimeSeconds() : 0.0;
            long period = em.m_environmentDuration > 0 ? (long)net / em.m_environmentDuration : 0L;
            foreach ((Vector3 p, Matrix4x4 toFar) in places)
            {
                Place pl = PlaceAt(p);
                bool fresh = pl == null;
                if (fresh)
                {
                    pl = new Place { At = p };
                    s_places.Add(pl);
                }
                else if (pl.Frame == frame)
                    continue;
                pl.Watched = now - pl.SeenAt < 1f;
                pl.SeenAt = now;
                pl.Frame = frame;
                pl.ToFar = toFar;
                UpdatePlace(em, pl, now, net, period, own, ownObject);
            }
            Player player = Player.m_localPlayer;
            int states = 0, overrides = 0, sheltered = 0;
            for (int i = s_places.Count - 1; i >= 0; i--)
            {
                Place pl = s_places[i];
                if (pl.Frame != frame)
                    KeepUnseen(em, pl, now, own);
                if (pl.Storm != null && !StepStorm(em, pl.Storm, now, player))
                    pl.Storm = null;
                // Forgotten 10 s after the last window onto it (what ran for it stopped 3 s after; seen again, it is a
                // place seen anew: filled in, and blending only right after a period began).
                if (now - pl.SeenAt > 10f && pl.Shadows.Count == 0 && pl.Storm == null)
                {
                    s_places.RemoveAt(i);
                    continue;
                }
                if (pl.Override != null || pl.From != null)
                    states++;
                if (pl.Override != null)
                    overrides++;
                if (pl.Systems != null && pl.Systems.m_psystems != null && !pl.Running)
                    sheltered++;
            }
            s_placeStates = states;
            LabPlaceOverrides = overrides;
            LabPlaceSheltered = sheltered;
            KeepClones(now);
            TrackWet(em, places);
        }

        static void UpdatePlace(EnvMan em, Place pl, float now, double net, long period, GameObject[] own, GameObject ownObject)
        {
            // Once a second, where you would stand over there: its shelter (Player.UpdateCover's test, InShelter's rule)
            // and its overrides; a place seen anew at once, the others at most ChecksPerFrame a frame (a hall's places
            // come live together and would all test in the same frames).
            int frame = Time.frameCount;
            if (frame != s_checkFrame)
            {
                s_checkFrame = frame;
                s_checks = 0;
            }
            if (now >= pl.CheckAt && (pl.CheckAt == 0f || s_checks < ChecksPerFrame))
            {
                s_checks++;
                pl.CheckAt = now + 1f;
                Vector3 at = FarPointOf(pl);
                Cover.GetCoverForPoint(at, out float cover, out bool roof);
                pl.Sheltered = cover >= 0.8f && roof;
                try
                {
                    pl.Override = OverrideAt(em, at);
                }
                catch (Exception e)
                {
                    // An event the game could not read there either (it reads the same for you standing there): none.
                    pl.Override = null;
                    if (!s_overrideWarned)
                    {
                        s_overrideWarned = true;
                        Plugin.Log.LogWarning("Could not read the environment overrides at a far place: " + e.Message);
                    }
                }
            }
            float duration = Mathf.Max(0.01f, em.m_transitionDuration);
            EnvSetup target = pl.Override ?? ZonePick(em, pl.At, period);
            if (target != pl.Target)
            {
                // A new period (or override) there: blended from the weather before, as the game blends yours, from when
                // it began. A new period found late is past its blend already; a place first seen right after one blends
                // from the period before.
                bool newPeriod = pl.Period != period;
                float since = newPeriod ? Mathf.Max(0f, (float)(net - (double)period * em.m_environmentDuration)) : 0f;
                EnvSetup from = pl.Watched ? pl.Target : newPeriod && pl.Override == null ? PickNowInner(em, pl.At, period - 1) : null;
                if (from != null && target != null && from != target && since < duration)
                {
                    pl.From = from;
                    pl.BlendAt = now - since;
                    LabPlaceBlends++;
                }
                else
                    pl.From = null;
                pl.Target = target;
            }
            pl.Period = period;
            StepBlend(pl, now, duration);
            // The particles switch when the blend ends (SetEnv sees the interpolated environment's, the old weather's,
            // until then); outside-only ones start only out of shelter and, once started, run on (as yours do when you
            // walk under a roof).
            EnvSetup sys = pl.From ?? pl.Target;
            pl.Starting = false;
            if (sys != pl.Systems)
            {
                pl.Starting = pl.Watched;
                pl.Systems = sys;
                pl.Running = sys != null && (!sys.m_psystemsOutsideOnly || !pl.Sheltered);
            }
            else if (!pl.Running && sys != null && !pl.Sheltered)
            {
                pl.Running = true;
                pl.Starting = pl.Watched;
            }
            GameObject[] runs = pl.Running ? sys.m_psystems : null;
            foreach (Shadow s in pl.Shadows)
                s.Wanted = false;
            if (runs != null)
                foreach (GameObject go in runs)
                {
                    if (go == null)
                        continue;
                    if (own != null && Array.IndexOf(own, go) >= 0)
                    {
                        // Yours runs it, carried into each window like yours; this place runs it on once yours stops it.
                        if (!s_handTo.ContainsKey(go))
                            s_handTo[go] = pl;
                        continue;
                    }
                    Shadow s = ShadowOf(pl, go);
                    if (s == null && (s = StartInstance(pl, go, !pl.Starting)) == null)
                        continue;
                    if (s.Fading)
                        Revive(s);
                    s.Wanted = true;
                    s.Until = now + 3f;
                    s.Env = sys;
                    s.ToFar = pl.ToFar;
                    s.Place = pl.At;
                }
            // The ones it has no more: yours runs them now (it shows yours), or they stop emitting and what fell lands.
            for (int i = pl.Shadows.Count - 1; i >= 0; i--)
            {
                Shadow s = pl.Shadows[i];
                if (s.Wanted)
                    continue;
                if (s.Go == null || (own != null && Array.IndexOf(own, s.Original) >= 0))
                    EndShadow(s, own);
                else if (!s.Fading)
                    Fade(s, now);
                else if (now > s.FadeUntil || !Alive(s))
                    EndShadow(s, own);
            }
            // Its storm, struck around where you would stand over there (SetEnv switches the storm with the particles).
            Thunder t = sys != null && sys.m_envObject != null && sys.m_envObject != ownObject ? sys.m_envObject.GetComponentInChildren<Thunder>(true) : null;
            if (t == null)
                return;
            if (pl.Storm == null || pl.Storm.T != t)
                pl.Storm = new Storm { T = t, NextAt = now + UnityEngine.Random.Range(t.m_strikeIntervalMin, t.m_strikeIntervalMax) };
            pl.Storm.ToFar = pl.ToFar;
            pl.Storm.Env = sys;
            pl.Storm.Until = now + 3f;
        }

        // How far a place's blend has gone, its mix refilled; over once the game's transition time has passed.
        static void StepBlend(Place pl, float now, float duration)
        {
            if (pl.From == null)
                return;
            pl.T = Mathf.Clamp01((now - pl.BlendAt) / duration);
            if (pl.T >= 1f || pl.Target == null)
            {
                EndBlend(pl);
                return;
            }
            if (pl.Mix == null || pl.MixFrom != pl.From)
            {
                pl.Mix = pl.From.Clone();
                pl.MixFrom = pl.From;
            }
            Mix(pl.Mix, pl.From, pl.Target, pl.T);
        }

        // The mix ends as the weather it blended into (what still holds it, FarWet's per-second look, reads that).
        static void EndBlend(Place pl)
        {
            if (pl.Mix != null && pl.Target != null)
                Mix(pl.Mix, pl.Target, pl.Target, 1f);
            pl.From = null;
            pl.T = 1f;
        }

        // A place no window looks at this frame: its blend goes on by the clock, what still lands ends, and 3 s after the
        // last window what runs for it stops (cleared: nothing shows it); seen again, it is filled in.
        static void KeepUnseen(EnvMan em, Place pl, float now, GameObject[] own)
        {
            StepBlend(pl, now, Mathf.Max(0.01f, em.m_transitionDuration));
            bool gone = now - pl.SeenAt > 3f;
            for (int i = pl.Shadows.Count - 1; i >= 0; i--)
            {
                Shadow s = pl.Shadows[i];
                if (gone || s.Go == null || (s.Fading && (now > s.FadeUntil || !Alive(s))))
                    EndShadow(s, own);
            }
        }

        // EnvMan.InterpolateEnvironment into `mix` (made once per blend, nothing allocated here): the old weather's rain
        // until the blend ends, the cold flags from halfway, the rest lerped. The aurora's colours are blended apart
        // (Aurora).
        static void Mix(EnvSetup mix, EnvSetup a, EnvSetup b, float i)
        {
            mix.m_name = b.m_name;
            mix.m_isWet = a.m_isWet;
            EnvSetup flags = i >= 0.5f ? b : a;
            mix.m_isFreezingAtNight = flags.m_isFreezingAtNight;
            mix.m_isFreezing = flags.m_isFreezing;
            mix.m_isCold = flags.m_isCold;
            mix.m_isColdAtNight = flags.m_isColdAtNight;
            mix.m_alwaysDark = flags.m_alwaysDark;
            mix.m_snowBuildup = Mathf.Lerp(a.m_snowBuildup, b.m_snowBuildup, i);
            mix.m_ambColorDay = Color.Lerp(a.m_ambColorDay, b.m_ambColorDay, i);
            mix.m_ambColorNight = Color.Lerp(a.m_ambColorNight, b.m_ambColorNight, i);
            mix.m_fogColorDay = Color.Lerp(a.m_fogColorDay, b.m_fogColorDay, i);
            mix.m_fogColorEvening = Color.Lerp(a.m_fogColorEvening, b.m_fogColorEvening, i);
            mix.m_fogColorMorning = Color.Lerp(a.m_fogColorMorning, b.m_fogColorMorning, i);
            mix.m_fogColorNight = Color.Lerp(a.m_fogColorNight, b.m_fogColorNight, i);
            mix.m_fogColorSunDay = Color.Lerp(a.m_fogColorSunDay, b.m_fogColorSunDay, i);
            mix.m_fogColorSunEvening = Color.Lerp(a.m_fogColorSunEvening, b.m_fogColorSunEvening, i);
            mix.m_fogColorSunMorning = Color.Lerp(a.m_fogColorSunMorning, b.m_fogColorSunMorning, i);
            mix.m_fogColorSunNight = Color.Lerp(a.m_fogColorSunNight, b.m_fogColorSunNight, i);
            mix.m_fogDensityDay = Mathf.Lerp(a.m_fogDensityDay, b.m_fogDensityDay, i);
            mix.m_fogDensityEvening = Mathf.Lerp(a.m_fogDensityEvening, b.m_fogDensityEvening, i);
            mix.m_fogDensityMorning = Mathf.Lerp(a.m_fogDensityMorning, b.m_fogDensityMorning, i);
            mix.m_fogDensityNight = Mathf.Lerp(a.m_fogDensityNight, b.m_fogDensityNight, i);
            mix.m_sunColorDay = Color.Lerp(a.m_sunColorDay, b.m_sunColorDay, i);
            mix.m_sunColorEvening = Color.Lerp(a.m_sunColorEvening, b.m_sunColorEvening, i);
            mix.m_sunColorMorning = Color.Lerp(a.m_sunColorMorning, b.m_sunColorMorning, i);
            mix.m_sunColorNight = Color.Lerp(a.m_sunColorNight, b.m_sunColorNight, i);
            mix.m_auroraIntensityDay = Mathf.Lerp(a.m_auroraIntensityDay, b.m_auroraIntensityDay, i);
            mix.m_auroraIntensityEvening = Mathf.Lerp(a.m_auroraIntensityEvening, b.m_auroraIntensityEvening, i);
            mix.m_auroraIntensityMorning = Mathf.Lerp(a.m_auroraIntensityMorning, b.m_auroraIntensityMorning, i);
            mix.m_auroraIntensityNight = Mathf.Lerp(a.m_auroraIntensityNight, b.m_auroraIntensityNight, i);
            mix.m_lightIntensityDay = Mathf.Lerp(a.m_lightIntensityDay, b.m_lightIntensityDay, i);
            mix.m_lightIntensityNight = Mathf.Lerp(a.m_lightIntensityNight, b.m_lightIntensityNight, i);
            mix.m_sunAngle = Mathf.Lerp(a.m_sunAngle, b.m_sunAngle, i);
            mix.m_windMin = Mathf.Lerp(a.m_windMin, b.m_windMin, i);
            mix.m_windMax = Mathf.Lerp(a.m_windMax, b.m_windMax, i);
            mix.m_rainCloudAlpha = Mathf.Lerp(a.m_rainCloudAlpha, b.m_rainCloudAlpha, i);
            mix.m_cloudOpacityDay = Mathf.Lerp(a.m_cloudOpacityDay, b.m_cloudOpacityDay, i);
            mix.m_cloudOpacityNight = Mathf.Lerp(a.m_cloudOpacityNight, b.m_cloudOpacityNight, i);
            mix.m_cloudOpacityEvening = Mathf.Lerp(a.m_cloudOpacityEvening, b.m_cloudOpacityEvening, i);
            mix.m_cloudOpacityMorning = Mathf.Lerp(a.m_cloudOpacityMorning, b.m_cloudOpacityMorning, i);
            mix.m_ambientOcclusionColor = Color.Lerp(a.m_ambientOcclusionColor, b.m_ambientOcclusionColor, i);
            mix.m_aoIntensityDay = Mathf.Lerp(a.m_aoIntensityDay, b.m_aoIntensityDay, i);
            mix.m_aoIntensityMorning = Mathf.Lerp(a.m_aoIntensityMorning, b.m_aoIntensityMorning, i);
            mix.m_aoIntensityEvening = Mathf.Lerp(a.m_aoIntensityEvening, b.m_aoIntensityEvening, i);
            mix.m_aoIntensityNight = Mathf.Lerp(a.m_aoIntensityNight, b.m_aoIntensityNight, i);
        }

        // What a view renders for `env` at that place: the blend into it while one runs there, else `env` itself.
        static EnvSetup LookOf(Place pl, EnvSetup env) => pl != null && pl.From != null && pl.Target == env && pl.Mix != null ? pl.Mix : env;

        // The place kept for a far portal (its centre within a metre), or null.
        static Place PlaceAt(Vector3 at)
        {
            for (int i = 0; i < s_places.Count; i++)
                if ((s_places[i].At - at).sqrMagnitude < 1f)
                    return s_places[i];
            return null;
        }

        // Far.WeatherParity: the place a view renders. During a window render that window's own (its far portal's,
        // PortalSystem.RenderingPlace): two far ends a few hundred metres apart, or a nested view, took the place nearest
        // the camera, another window's. Else (a probe face), or when that place is not kept, the one nearest the camera.
        static Place ViewPlace(Vector3 camPos)
        {
            PortalSystem sys = PortalSystem.Instance;
            Place pl = sys != null && sys.RenderingPlace(out Vector3 at) ? PlaceAt(at) : null;
            return pl ?? PlaceNear(camPos);
        }

        // The place nearest to a view's camera (within the wetness's 400 m), or null.
        static Place PlaceNear(Vector3 pos)
        {
            Place best = null;
            float bestD = 400f * 400f;
            for (int i = 0; i < s_places.Count; i++)
            {
                float d = (s_places[i].At - pos).sqrMagnitude;
                if (d < bestD)
                {
                    bestD = d;
                    best = s_places[i];
                }
            }
            return best;
        }

        // Where you would stand over there (your centre, as Player.UpdateCover tests it).
        static Vector3 FarPointOf(Place pl)
        {
            Player p = Player.m_localPlayer;
            return p != null ? pl.ToFar.MultiplyPoint3x4(p.GetCenterPoint()) : pl.At + Vector3.up;
        }

        // EnvMan.GetEnvironmentOverride for a player standing at the point: a forcing EnvZone there (SetForceEnvironment
        // comes before everything), the random event there, the alt biome's forced environment (in the zone's pick
        // already: null here), a persistent event there, an EnvZone that does not force. The debug environment and the
        // intro are Pick's. Null: none.
        static EnvSetup OverrideAt(EnvMan em, Vector3 point)
        {
            if (WorldGenerator.instance == null)
                return null;
            string force = null, zone = null;
            int n = Physics.OverlapSphereNonAlloc(point, 0.1f, s_zoneHits, ~0, QueryTriggerInteraction.Collide);
            for (int i = 0; i < n; i++)
            {
                Collider c = s_zoneHits[i];
                if (c == null || !c.isTrigger || !c.TryGetComponent(out EnvZone z) || string.IsNullOrEmpty(z.m_environment))
                    continue;
                if (z.m_force)
                    force = force ?? z.m_environment;
                else
                    zone = zone ?? z.m_environment;
            }
            if (force != null && string.IsNullOrEmpty(em.m_debugEnv))
                return s_getEnv(em, force);
            string name = EventEnvAt(point);
            if (name == null)
            {
                foreach (AltBiome alt in WorldGenerator.instance.GetBiomeSector(point).AltBiomes)
                    if (!string.IsNullOrEmpty(alt.m_forceEnvironment))
                        return null;
                name = PersistentEnvAt(point) ?? zone;
            }
            return name != null ? s_getEnv(em, name) : null;
        }

        // RandEventSystem's active event for a player at the point (a forced one everywhere, the random one within its
        // range) and its GetEnvOverride (by the point's biome).
        static string EventEnvAt(Vector3 point)
        {
            RandEventSystem rs = RandEventSystem.instance;
            if (rs == null)
                return null;
            RandomEvent ev = s_forcedEvent(rs);
            if (ev == null)
            {
                ev = s_randomEvent(rs);
                if (ev != null && (point.y > 3000f || Utils.DistanceXZ(point, ev.m_pos) >= ev.m_eventRange))
                    ev = null;
            }
            if (ev == null || string.IsNullOrEmpty(ev.m_forceEnvironment) || (WorldGenerator.instance.GetBiome(point) & ev.m_biome) == 0)
                return null;
            return ev.m_forceEnvironment;
        }

        // PersistentEventSystem.GetEnvironmentOverride for a player at the point.
        static string PersistentEnvAt(Vector3 point)
        {
            PersistentEventSystem ps = PersistentEventSystem.instance;
            if (ps == null || ps.m_activePersistentEvents?.list == null)
                return null;
            foreach (PersistentEventSystem.ActivePersistentEvent e in ps.m_activePersistentEvents.list)
                if ((e.position - point).sqrMagnitude < e.radius * e.radius)
                    return e.Source.GetEnvironmentOverride(point);
            return null;
        }

        // P34: how wet a place first seen is: as the weather of the period before left it (fully, the game's soaking or
        // drying of 15 s is long over by then), soaked or dried toward its weather since this period began.
        static float WetStart(EnvMan em, Vector3 place, float target)
        {
            if (ZNet.instance == null || em.m_environmentDuration <= 0)
                return target;
            double net = ZNet.instance.GetTimeSeconds();
            long period = (long)net / em.m_environmentDuration;
            EnvSetup before = PickNowInner(em, place, period - 1);
            float from = before != null && before.m_isWet ? 1f : 0f;
            float since = Mathf.Max(0f, (float)(net - (double)period * em.m_environmentDuration));
            return Mathf.MoveTowards(from, target, since / Mathf.Max(0.01f, s_wetDuration(em)));
        }

        // The view of that place shows other weather particles than yours: its shelter holds back what you run, it runs
        // something of its own (or something still landing), its storm flashes, or your storm is not its.
        static bool PlaceDiffers(EnvMan em, Place pl)
        {
            if (pl.Shadows.Count > 0 || (pl.Storm != null && pl.Storm.Flashes.Count > 0))
                return true;
            GameObject ownObject = s_envObject(em);
            if (ownObject != null && (pl.Systems == null || ownObject != pl.Systems.m_envObject))
                return true;
            GameObject[] runs = pl.Running && pl.Systems != null ? pl.Systems.m_psystems : null;
            return s_currentPSystems(em) != runs;
        }

        // The weather particles of a view of that place: yours hidden where it has none of them (or its shelter holds them
        // back), its own shown (running or still landing), its lightning shown and yours hidden where its storm is not
        // yours. With no place known around the view (a probe of an area no window is live onto) as before.
        static void SwapPlace(EnvMan em, EnvSetup env, Place pl)
        {
            if (pl == null)
            {
                if (env != null)
                    SwapWeather(em, env);
                return;
            }
            if (pl.Storm != null)
                ShowFlashes(pl.Storm);
            GameObject[] own = s_currentPSystems(em);
            EnvSetup sys = pl.Systems;
            GameObject[] runs = pl.Running && sys != null ? sys.m_psystems : null;
            if (own != null && own != runs)
                foreach (GameObject go in own)
                    if (go != null && (runs == null || Array.IndexOf(runs, go) < 0))
                        Hide(RenderersOf(go));
            foreach (Shadow s in pl.Shadows)
                if (s.Go != null)
                    ShowShadow(s.Go);
            // One that could not be cloned for it (TakeClone): the other place's, as before.
            if (runs != null)
                foreach (GameObject go in runs)
                    if (go != null && (own == null || Array.IndexOf(own, go) < 0) && ShadowOf(pl, go) == null && s_shadow.ContainsKey(go))
                        ShowShadow(go);
            GameObject ownObject = s_envObject(em);
            if (ownObject == null || (sys != null && ownObject == sys.m_envObject))
                return;
            HideOwnStorm(ownObject);
        }

        static void ShowShadow(GameObject go)
        {
            foreach (Renderer r in RenderersOf(go))
                if (r != null && r.forceRenderingOff)
                {
                    r.forceRenderingOff = false;
                    s_shown.Add(r);
                }
        }

        static Shadow ShadowOf(Place pl, GameObject go)
        {
            foreach (Shadow s in pl.Shadows)
                if (s.Original == go)
                    return s;
            return null;
        }

        // A weather object run for a place: the weather's own while no other place runs it, else a clone of it (P46);
        // hidden from the game camera, placed over there, filled in (a place seen anew) or starting to fall.
        static Shadow StartInstance(Place pl, GameObject go, bool fill)
        {
            GameObject inst = go;
            if (s_shadow.ContainsKey(go))
            {
                inst = TakeClone(go);
                if (inst == null)
                    return null;
            }
            var s = new Shadow
            {
                Go = inst, Original = go, Owner = pl, Clone = inst != go, Env = pl.Systems, ToFar = pl.ToFar, Place = pl.At,
                Follower = inst.GetComponent<FollowPlayer>(), Systems = inst.GetComponentsInChildren<ParticleSystem>()
            };
            s_shadow[inst] = s;
            pl.Shadows.Add(s);
            StartShadow(inst, s, false);
            StartEmitting(inst, fill);
            return s;
        }

        // From GameSwitchesWeather: your weather object a place has too goes on there as it is, run for that place.
        static void Adopt(Place pl, GameObject go)
        {
            var s = new Shadow
            {
                Go = go, Original = go, Owner = pl, Env = pl.Systems, ToFar = pl.ToFar, Place = pl.At, Wanted = true,
                Follower = go.GetComponent<FollowPlayer>(), Systems = go.GetComponentsInChildren<ParticleSystem>()
            };
            s_shadow[go] = s;
            pl.Shadows.Add(s);
            StartShadow(go, s, false);
        }

        // As SetParticleArrayEnabled switching an object on: filled in (an instant switch: a place first seen, where it
        // has been falling all along) or emitting from now on (it starts falling); a clone made a moment ago plays too.
        static void StartEmitting(GameObject go, bool fill)
        {
            foreach (ParticleSystem ps in go.GetComponentsInChildren<ParticleSystem>())
            {
                ParticleSystem.EmissionModule emission = ps.emission;
                emission.enabled = true;
                if (fill)
                {
                    ps.Simulate(15f);
                    ps.Play();
                }
                else if (!ps.isPlaying)
                    ps.Play();
            }
            MistEmitter mist = go.GetComponentInChildren<MistEmitter>();
            if (mist != null)
                mist.enabled = true;
        }

        // The weather left it: as SetParticleArrayEnabled switching it off without an instant switch (emission off, no
        // Clear), still shown in that place's views while what fell lands.
        static void Fade(Shadow s, float now)
        {
            s.Fading = true;
            s.FadeUntil = now + FadeMax;
            SwitchLikeGame(s.Go, false, false);
        }

        static void Revive(Shadow s)
        {
            s.Fading = false;
            StartEmitting(s.Go, false);
        }

        static bool Alive(Shadow s)
        {
            if (s.Systems != null)
                foreach (ParticleSystem ps in s.Systems)
                    if (ps != null && ps.particleCount > 0)
                        return true;
            return false;
        }

        // What ran for a place stops: the weather's own object goes back to the game (it runs it now, or it is switched
        // off as on an instant switch), a clone goes back to the pool.
        static void EndShadow(Shadow s, GameObject[] own)
        {
            s.Owner?.Shadows.Remove(s);
            GameObject go = s.Go;
            if (ReferenceEquals(go, null))
                return;
            if (go == null)
            {
                s_shadow.Remove(go);
                return;
            }
            if (s.Clone)
            {
                ReleaseMovers(s);
                SwitchLikeGame(go, false, true);
                go.SetActive(false);
                foreach (WeatherClone c in s_clones)
                    if (c.Go == go)
                        c.FreeAt = Time.time;
            }
            else
                StopShadow(go, own != null && Array.IndexOf(own, go) >= 0);
            s_shadow.Remove(go);
        }

        // A clone of a weather object for one more place: a free one, or a new one. Not of an object with mist that places
        // itself (FarMist copies ParticleMist per place itself; MistEmitter runs as a mover, FindMovers), nor of a
        // networked one: those stay one, shared as before (SwapPlace).
        static GameObject TakeClone(GameObject go)
        {
            foreach (WeatherClone c in s_clones)
                if (c.Original == go && c.Go != null && !c.Go.activeSelf && !s_shadow.ContainsKey(c.Go))
                {
                    c.Go.SetActive(true);
                    return c.Go;
                }
            if (s_clones.Count >= MaxClones || !go.activeSelf)
                return null;
            if (!s_cloneable.TryGetValue(go, out bool ok))
                s_cloneable[go] = ok = go.GetComponentInChildren<ParticleMist>(true) == null && go.GetComponentInChildren<MistEmitter>(true) == null
                                       && go.GetComponentInChildren<ZNetView>(true) == null;
            if (!ok)
                return null;
            GameObject made = UnityEngine.Object.Instantiate(go, go.transform.parent);
            // The original's world-space particles run in the space PortalSystem keeps for its follower (shifted with it
            // through each view): the clone's run in the world again, until PortalSystem gives its follower a space of
            // its own.
            foreach (ParticleSystem ps in made.GetComponentsInChildren<ParticleSystem>(true))
            {
                ParticleSystem.MainModule main = ps.main;
                if (main.simulationSpace == ParticleSystemSimulationSpace.Custom
                    && (main.customSimulationSpace == null || !main.customSimulationSpace.IsChildOf(made.transform)))
                {
                    main.simulationSpace = ParticleSystemSimulationSpace.World;
                    main.customSimulationSpace = null;
                }
            }
            s_clones.Add(new WeatherClone { Original = go, Go = made });
            LabWeatherClones++;
            return made;
        }

        // Clones no place used for a minute go, and those whose weather object is gone (once no place runs them).
        static void KeepClones(float now)
        {
            for (int i = s_clones.Count - 1; i >= 0; i--)
            {
                WeatherClone c = s_clones[i];
                if (s_shadow.ContainsKey(c.Go))
                    continue;
                if (c.Go != null && c.Original != null && (c.Go.activeSelf || now - c.FreeAt < CloneKeep))
                    continue;
                DestroyClone(c);
                s_clones.RemoveAt(i);
            }
        }

        static void DestroyClone(WeatherClone c)
        {
            s_renderers.Remove(c.Go);
            if (c.Go != null)
                UnityEngine.Object.Destroy(c.Go);
        }

        // Far.WeatherParity switched on or off: what the other way runs stops (the weather's own objects back to the game,
        // clones gone) and its places and storms are forgotten (their flashes go out on their own).
        static void ResetWeather(EnvMan em)
        {
            GameObject[] own = s_currentPSystems(em);
            s_drop.Clear();
            foreach (KeyValuePair<GameObject, Shadow> kv in s_shadow)
                s_drop.Add(kv.Key);
            foreach (GameObject go in s_drop)
            {
                Shadow s = s_shadow[go];
                if (go == null)
                    continue;
                if (s.Clone)
                    ReleaseMovers(s);
                else
                    StopShadow(go, own != null && Array.IndexOf(own, go) >= 0);
            }
            s_shadow.Clear();
            foreach (WeatherClone c in s_clones)
                DestroyClone(c);
            s_clones.Clear();
            s_wanted.Clear();
            s_handTo.Clear();
            s_storms.Clear();
            s_places.Clear();
            s_placeStates = 0;
        }

        // ---- the aurora's colours (P33's aurora part) ----

        static bool s_auroraSet;
        static Texture s_auroraGameCur, s_auroraGameNext, s_auroraCur, s_auroraNextNow;
        static float s_auroraGameBlend, s_auroraBlendNow;

        // The aurora's colours of the view's weather, blended as the game blends yours (EnvMan sets them only for yours,
        // during and after a blend of yours); set by difference and put back with the rest. Left as they are while the
        // game has set none yet (it has not blended since the world loaded: no place shows other colours then).
        static void Aurora(Place pl, EnvSetup env)
        {
            if (!s_auroraSet && Shader.GetGlobalTexture(s_auroraCurrent) == null)
                return;
            if (pl != null && pl.From != null && pl.Target == env)
                SetAurora(pl.From.m_auroraGradientTexture, pl.Target.m_auroraGradientTexture, pl.T);
            else
                SetAurora(env.m_auroraGradientTexture, null, 0f);
        }

        static void SetAurora(Texture cur, Texture next, float blend)
        {
            if (!s_auroraSet)
            {
                s_auroraSet = true;
                s_auroraGameCur = s_auroraCur = Shader.GetGlobalTexture(s_auroraCurrent);
                s_auroraGameNext = s_auroraNextNow = Shader.GetGlobalTexture(s_auroraNext);
                s_auroraGameBlend = s_auroraBlendNow = Shader.GetGlobalFloat(s_auroraBlend);
            }
            if (cur != null && cur != s_auroraCur)
            {
                Shader.SetGlobalTexture(s_auroraCurrent, cur);
                s_auroraCur = cur;
            }
            if (next != null && next != s_auroraNextNow)
            {
                Shader.SetGlobalTexture(s_auroraNext, next);
                s_auroraNextNow = next;
            }
            if (blend != s_auroraBlendNow)
            {
                Shader.SetGlobalFloat(s_auroraBlend, blend);
                s_auroraBlendNow = blend;
            }
        }

        // The game's aurora colours back (a next texture the game never set stays: its blend is back at 0).
        static void AuroraBack()
        {
            if (!s_auroraSet)
                return;
            s_auroraSet = false;
            if (s_auroraGameCur != null && s_auroraCur != s_auroraGameCur)
                Shader.SetGlobalTexture(s_auroraCurrent, s_auroraGameCur);
            if (s_auroraGameNext != null && s_auroraNextNow != s_auroraGameNext)
                Shader.SetGlobalTexture(s_auroraNext, s_auroraGameNext);
            if (s_auroraBlendNow != s_auroraGameBlend)
                Shader.SetGlobalFloat(s_auroraBlend, s_auroraGameBlend);
            s_auroraGameCur = s_auroraGameNext = s_auroraCur = s_auroraNextNow = null;
        }

        // ---- for FarWet (P16, P35) ----

        // The weather a far object at this place goes by (whether it rains there, how hard the wind blows), as the game
        // would have it for you standing there: that place's environment, or (Far.WeatherParity) the blend into it while
        // one runs there (the game keeps the old weather's rain until its blend ends). Null: yours.
        internal static EnvSetup LocalWeather(Vector3 place)
        {
            EnvSetup env = Pick(place);
            if (env == null || !FarFlags.WeatherParity || s_placeStates == 0)
                return env;
            return LookOf(PlaceNear(place), env);
        }
    }
}
