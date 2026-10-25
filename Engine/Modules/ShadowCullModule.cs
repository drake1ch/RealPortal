using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace RealPortal.Engine
{
    // Shadow and draw distances by layer, and which point lights get the shadow slots. Client-local; every switch is
    // off by default until lab screenshot diffs accept it.
    //
    // ShadowSunLayerCull. The game never sets Light.layerShadowCullDistances, so the sun's 4 cascades x 150 m (High)
    // hold every caster: dropped items, Default_small and piece_nonsolid props, characters, ships, out to the shadow
    // distance, where a 0.3 m prop is a fraction of a shadow-map texel in the last cascade. Per-layer distances on the
    // sun (EnvMan.m_dirLight) drop those casters from the far cascades; Unity applies them in its own shadow-caster
    // culling, so nothing else in the frame changes. Defaults: item 25 m, Default_small 40 m, piece_nonsolid 60 m. The
    // character* and vehicle layers keep the game's shadow distance (0): a troll, a lox herd, a flying boss or a
    // longship 60-150 m away casts a large, plainly visible shadow, not a fraction of a texel; capping them by layer
    // would make those shadows pop. Pieces, static_solid and Default keep it too. Prior art: ShadowTrim, the same
    // mechanism, measured -1.0 ms per frame outside a 7k-piece base (pieces, not characters or ships).
    //
    // CameraLayerCull. The game never sets Camera.layerCullDistances either: objects without a LODGroup (dropped
    // items, many small props) are culled, sorted and drawn out to the loaded radius (~288 m at the default simulation
    // distance). Per-layer distances on the game camera (spherical) for items (50 m), Default_small (80 m) and
    // piece_nonsolid (120 m); characters, vehicles and effects are off (0) unless set.
    //
    // Both are written through LayerOverlay, which composes with anyone else who writes the same arrays (Real Portal's
    // Win.LayerCull writes the game camera's layerCullDistances while far areas are loaded; a mod like ShadowTrim would
    // write the sun's): a layer gets min(their distance, ours), what they write later becomes the new base under ours,
    // and switching off puts back their value on every layer that still holds ours. Re-read 4 times a second (a read
    // allocates one 32-float array), so another writer's change is composed within 0.25 s.
    //
    // Real Portal takes a copy of the game camera's distances once (WindowLayerCull.Update, when its PortalSystem first
    // exists, i.e. when the first portal loads) as "the game's own", builds its window cameras' distances and its own
    // game-camera cut from that copy, and writes the copy back when it turns off. A copy taken while ours is on would
    // hold our caps: switching ours off would then leave Real Portal's windows culling at our distances and its later
    // writes putting our caps back. So Real Portal must never see our caps: a prefix/postfix on its
    // WindowLayerCull.Update notices a copy taken while ours is on the camera and makes it take the copy again with
    // ours lifted (its Off(), ours released, its Update again, ours composed again the same frame, before anything
    // renders: our GameCamera.LateUpdate postfix runs after Real Portal's). CameraBase gives the same "without ours"
    // view for Real Portal to read directly. If Real Portal is loaded but that hook cannot be installed (its code
    // changed), CameraLayerCull stands down (off) rather than leave our caps in Real Portal's copy.
    //
    // PointShadowVisibleRank. A quality option, not a speed one. LightLod gives its shadow slots (m_shadowLimit, 3 at the
    // default "point light shadows" setting) to the point lights nearest the player, once a second
    // (LightLod.UpdateLights), visible or not. Unity renders no shadow map for a light whose range sphere is outside the
    // camera's frustum, so a slot spent on the torch behind you buys nothing while the hearth in front of you stays
    // unshadowed. With this on, after the game's own ranking, the lights the game keeps lit (the first m_lightLimit of
    // its order: unchanged, so which lights are on is exactly the game's) are reordered among themselves: first those
    // that can use a slot (shadow LOD, within their shadow distance) and whose range sphere touches the game camera's
    // frustum, then those that can use one but are off-screen, then the rest, each group in the game's order. The
    // coroutine reads m_lightPrio as before. Cost: it can only add shadow-map renders at a given setting, never remove
    // any (the game's wasted slots now go to lights Unity does render); GameSlotsInView / RankedSlotsInView count the
    // difference for the lab. The CPU lever is a lower in-game "point light shadows" setting, which this makes look like
    // the higher one.
    // Gameplay: which point lights cast shadows feeds the stealth light level (StealthSystem.GetLightLevel tests
    // light.shadows and uses shadowStrength), which Player.UpdateStealth writes to the player's ZDO (s_stealth) for
    // creature AI on other machines. So while a ranking differs from the game's, StealthSystem.GetLightLevel sees each
    // light whose slot differs with the shadow state the game's order would give it (its steady state; during the 1 s
    // fade after a slot change the game's own state is mid-fade) and then the lights are put back. Without that patch
    // the ranking stands down.
    // Real Portal: its transpiler on UpdateLights/UpdateLoop measures a light's distance also through live windows; a
    // light whose game distance is shorter than its plain distance is counted visible (seen through a window), and
    // VisibleElsewhere (Real Portal may set it) is asked for the rest. Any other mod's transpiler on UpdateLights or the
    // UpdateLoop coroutine makes this stand down (the game's order stays), as does any change to the game's priorities
    // before our postfix.
    //
    // Not done: per-point-light layer culls. Light.layerShadowCullDistances is "directional lights only" (Unity's own
    // documentation, 6000.0), and a point light's cullingMask would also stop it lighting those layers. Shadow
    // resolution by screen size (render analysis) is left for the lab to justify first.
    [EngineModule]
    public static class ShadowCullModule
    {
        public static readonly Feature SunFeature = Features.Register("ShadowSunLayerCull", false,
            "Visual, off until screenshot diffs: dropped items, small props and non-solid pieces stop casting sun shadows beyond per-layer distances (section Engine.Shadows).");
        public static readonly Feature CameraFeature = Features.Register("CameraLayerCull", false,
            "Visual, off until screenshot diffs: the game camera stops drawing items, small props and non-solid pieces beyond per-layer distances (section Engine.Shadows).");
        public static readonly Feature PointRankFeature = Features.Register("PointShadowVisibleRank", false,
            "Quality, not speed: point-light shadow slots go to lit lights you can see before ones behind you, so the frame may render more shadow maps than the game at the same setting (which lights are on, and the stealth light level, stay the game's).");

        // Layer groups, each a setting per target. Index into the distance arrays below.
        public const int Items = 0, SmallProps = 1, NonSolidPieces = 2, Characters = 3, Vehicles = 4, Pieces = 5, StaticSolid = 6, DefaultLayer = 7, Effects = 8, GroupCount = 9;
        static readonly string[] s_groupLayer = { "item", "Default_small", "piece_nonsolid", "character*", "vehicle", "piece", "static_solid", "Default", "effect" };
        static readonly string[] s_groupKey = { "Items", "SmallProps", "NonSolidPieces", "Characters", "Vehicles", "Pieces", "StaticSolid", "Default", "Effects" };

        // Metres; 0 = the game's (the sun: QualitySettings.shadowDistance; the camera: its far clip).
        public static readonly float[] SunDistances = { 25f, 40f, 60f, 0f, 0f, 0f, 0f, 0f, 0f };
        public static readonly float[] CameraDistances = { 50f, 80f, 120f, 0f, 0f, 0f, 0f, 0f, 0f };
        // Which groups each target exposes as settings (the others stay 0).
        static readonly int[] s_sunGroups = { Items, SmallProps, NonSolidPieces, Characters, Vehicles, Pieces, StaticSolid, DefaultLayer };
        static readonly int[] s_cameraGroups = { Items, SmallProps, NonSolidPieces, Characters, Vehicles, Effects };
        // Added to a point light's range in the frustum test (a light just off-screen still lights the screen edge).
        public static float FrustumMargin = 1f;

        // Real Portal: true when a point light with this range sphere is seen through a live window.
        public static Func<Vector3, float, bool> VisibleElsewhere;

        // Lab counters.
        public static int SunLayers, CameraLayers;                 // layers given a distance on each target
        public static long SunWrites, CameraWrites;                // arrays written
        public static long SunForeign, CameraForeign;              // layers someone else rewrote under ours (re-based)
        public static long StaleOurs;                              // layers found holding an older value of ours: not taken as someone else's
        public static long GhostReverts;                           // a copy of our old values put back by someone after off, undone
        public static long RealPortalCaptures;                     // Real Portal took its copy of the game camera's distances
        public static long RealPortalRecaptures;                   // ... with ours on: made to take it again without ours
        public static long Reranks;                                // the game's rankings seen while on
        public static long Reordered;                              // ... where slots moved
        public static long Promoted, Demoted;                      // lights given / denied a slot against the game's order
        public static long GameSlotsInView, RankedSlotsInView;     // slots on lights in view: the game's order vs ours (the difference: extra shadow maps)
        public static long InView, ThroughWindow, ByHook, OffScreen;
        public static long ForeignOrder;                           // rankings left alone: the game's priorities were already changed
        public static long LightSetMismatches;                     // must stay 0: a light's place against the light limit moved, or the priorities are no permutation
        public static long StealthParityCalls;                     // stealth light levels taken with the game's shadow slots
        public static long Errors;
        public static bool OverlayPatched, RankPatched, StealthPatched, StoodDown;
        public static bool RealPortalPresent, RealPortalHooked, CameraStoodDown;
        public static string StandDownBy = "";

        static readonly LayerOverlay s_sun = new LayerOverlay("sun");
        static readonly LayerOverlay s_camera = new LayerOverlay("game camera");
        static readonly float[] s_sunCaps = new float[32], s_cameraCaps = new float[32];
        static int[] s_groupMask;           // layer bits per group, from the game's layer names
        static bool s_dirty = true, s_syncNow;
        static float s_nextSync, s_errorAt;
        static GameCamera s_gameCamera;
        static CameraArray s_cameraArray;
        static SunArray s_sunArray;

        static Harmony s_harmony;
        static string s_harmonyId = EnginePlugin.Guid;
        const string RealPortalGuid = "valheim.realportal";

        static void Install(Harmony harmony)
        {
            s_harmony = harmony;
            s_harmonyId = harmony.Id;
            BindConfig();
            SunFeature.Switched += _ => s_dirty = true;
            CameraFeature.Switched += _ => s_dirty = true;

            try
            {
                MethodInfo late = AccessTools.DeclaredMethod(typeof(GameCamera), "LateUpdate");
                if (late == null)
                    EnginePlugin.Log.LogWarning("ShadowCull: GameCamera.LateUpdate not found; layer distances are not applied.");
                else
                {
                    // After Real Portal's postfix there (Win.LayerCull writes the same camera array), so ours composes
                    // with what it just wrote.
                    harmony.Patch(late, postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(ShadowCullModule), nameof(AfterCameraLateUpdate)))
                    {
                        priority = Priority.Last,
                        after = new[] { RealPortalGuid }
                    });
                    OverlayPatched = true;
                }
            }
            catch (Exception e)
            {
                EnginePlugin.Log.LogError("ShadowCull: GameCamera.LateUpdate not patched: " + e);
            }

            try
            {
                s_prio = AccessTools.FieldRefAccess<LightLod, int>("m_lightPrio");
                s_outer = AccessTools.FieldRefAccess<LightLod, float>("m_cameraDistanceOuter");
                s_baseRange = AccessTools.FieldRefAccess<LightLod, float>("m_baseRange");
                s_baseStrength = AccessTools.FieldRefAccess<LightLod, float>("m_baseShadowStrength");
                s_light = AccessTools.FieldRefAccess<LightLod, Light>("m_light");
                s_updateTimer = AccessTools.StaticFieldRefAccess<float>(AccessTools.DeclaredField(typeof(LightLod), "m_updateTimer"));
                s_sorted = AccessTools.DeclaredField(typeof(LightLod), "m_sortedLights")?.GetValue(null) as List<LightLod>;
                s_updateLights = AccessTools.DeclaredMethod(typeof(LightLod), nameof(LightLod.UpdateLights));
                s_updateLoop = AccessTools.EnumeratorMoveNext(AccessTools.DeclaredMethod(typeof(LightLod), "UpdateLoop"));
                if (s_sorted == null || s_updateLights == null)
                    throw new MissingMemberException("LightLod.m_sortedLights or UpdateLights");
                // The stealth parity first: without it the ranking would change a replicated value, so it does not run.
                MethodInfo lightLevel = AccessTools.DeclaredMethod(typeof(StealthSystem), nameof(StealthSystem.GetLightLevel), new[] { typeof(Vector3) });
                if (lightLevel == null)
                    throw new MissingMethodException("StealthSystem.GetLightLevel(Vector3)");
                harmony.Patch(lightLevel,
                    prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(ShadowCullModule), nameof(BeforeLightLevel))) { priority = Priority.Last },
                    finalizer: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(ShadowCullModule), nameof(AfterLightLevel))));
                StealthPatched = true;
                harmony.Patch(s_updateLights,
                    prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(ShadowCullModule), nameof(BeforeUpdateLights))),
                    postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(ShadowCullModule), nameof(AfterUpdateLights))) { priority = Priority.Last });
                RankPatched = true;
            }
            catch (Exception e)
            {
                EnginePlugin.Log.LogError("ShadowCull: LightLod or StealthSystem not patched, point-light shadow ranking stays the game's: " + e);
            }
        }

        // ---- settings ------------------------------------------------------------------------------------------------

        static void BindConfig()
        {
            ConfigFile config = null;
            try
            {
                config = Chainloader.ManagerObject != null ? Chainloader.ManagerObject.GetComponent<EnginePlugin>()?.Config : null;
            }
            catch
            {
                // no BepInEx chainloader: defaults
            }
            if (config == null)
                return;
            const string section = "Engine.Shadows";
            foreach (int g in s_sunGroups)
            {
                int group = g;
                Bind(config, section, "Sun" + s_groupKey[g], SunDistances[g], 0f, 1000f,
                    $"ShadowSunLayerCull: metres from the camera beyond which layer '{s_groupLayer[g]}' casts no sun shadow. 0: the game's shadow distance.",
                    v => { SunDistances[group] = v; s_dirty = true; });
            }
            foreach (int g in s_cameraGroups)
            {
                int group = g;
                Bind(config, section, "Camera" + s_groupKey[g], CameraDistances[g], 0f, 5000f,
                    $"CameraLayerCull: metres from the game camera beyond which layer '{s_groupLayer[g]}' is not drawn. 0: not limited.",
                    v => { CameraDistances[group] = v; s_dirty = true; });
            }
            Bind(config, section, "PointShadowFrustumMargin", FrustumMargin, 0f, 50f,
                "PointShadowVisibleRank: metres added to a point light's range when testing it against the camera's view.",
                v => FrustumMargin = v);
        }

        static void Bind(ConfigFile config, string section, string key, float def, float min, float max, string description, Action<float> set)
        {
            ConfigEntry<float> e = config.Bind(section, key, def, new ConfigDescription(description, new AcceptableValueRange<float>(min, max)));
            set(e.Value);
            e.SettingChanged += (_, __) => set(e.Value);
        }

        // ---- per-layer distances on the sun and the game camera ----------------------------------------------------

        // Postfix on GameCamera.LateUpdate (every frame; the work only every 0.25 s, at once after a change).
        static void AfterCameraLateUpdate(GameCamera __instance)
        {
            bool any = SunFeature.On || CameraFeature.On;
            if (!any && s_sun.Idle && s_camera.Idle)
                return;
            float now = Time.unscaledTime;
            if (!s_dirty && !s_syncNow && now < s_nextSync)
                return;
            s_syncNow = false;
            s_nextSync = now + (any ? 0.25f : 1f);
            try
            {
                // Before anything of ours is first written to the camera (Real Portal must not copy it unseen).
                if (!s_rpLooked)
                    LookForRealPortal();
                bool cameraOn = CameraFeature.On && (!RealPortalPresent || RealPortalHooked);
                CameraStoodDown = CameraFeature.On && !cameraOn;
                if (s_dirty)
                {
                    s_dirty = false;
                    Caps();
                }
                if (!ReferenceEquals(__instance, s_gameCamera))
                {
                    s_gameCamera = __instance;
                    Camera cam = __instance != null ? __instance.GetComponent<Camera>() : null;
                    s_cameraArray = cam != null ? new CameraArray(cam) : null;
                }
                s_camera.Sync(s_cameraArray, cameraOn, s_cameraCaps, now);
                EnvMan env = EnvMan.instance;
                Light sun = env != null ? env.m_dirLight : null;
                if (!ReferenceEquals(sun, s_sunArray?.Key))
                    s_sunArray = sun != null ? new SunArray(sun) : null;
                s_sun.Sync(s_sunArray, SunFeature.On, s_sunCaps, now);
                SunWrites = s_sun.Writes;
                SunForeign = s_sun.Foreign;
                CameraWrites = s_camera.Writes;
                CameraForeign = s_camera.Foreign;
                StaleOurs = s_sun.StaleOurs + s_camera.StaleOurs;
                GhostReverts = s_sun.GhostReverts + s_camera.GhostReverts;
            }
            catch (Exception e)
            {
                // Never break the game camera over this.
                Errors++;
                if (now >= s_errorAt)
                {
                    s_errorAt = now + 10f;
                    EnginePlugin.Log.LogError("ShadowCull: " + e);
                }
            }
        }

        // The game camera's per-layer distances as they are without CameraLayerCull: on every layer that holds a value
        // of ours, what lies under it; elsewhere the camera's own value. For Real Portal (Win.LayerCull) to take its
        // copy of "the game's own distances" from. `into` must hold 32 floats. Main thread.
        public static void CameraBase(Camera cam, float[] into)
        {
            if (cam == null || into == null || into.Length < 32)
                throw new ArgumentException("a camera and 32 floats");
            s_camera.BaseOf(cam, Normalize(cam.layerCullDistances), into);
        }

        // The settings as 32-layer arrays.
        static void Caps()
        {
            if (s_groupMask == null)
                FindLayers();
            SunLayers = Fill(s_sunCaps, SunDistances);
            CameraLayers = Fill(s_cameraCaps, CameraDistances);
        }

        static int Fill(float[] caps, float[] distances)
        {
            Array.Clear(caps, 0, caps.Length);
            int n = 0;
            for (int g = 0; g < GroupCount; g++)
            {
                float d = distances[g];
                if (!(d > 0f))
                    continue;
                for (int l = 0; l < 32; l++)
                {
                    if ((s_groupMask[g] & (1 << l)) == 0)
                        continue;
                    caps[l] = caps[l] > 0f ? Mathf.Min(caps[l], d) : d;
                    n++;
                }
            }
            return n;
        }

        static void FindLayers()
        {
            s_groupMask = new int[GroupCount];
            var missing = new List<string>();
            for (int g = 0; g < GroupCount; g++)
            {
                string name = s_groupLayer[g];
                if (name.EndsWith("*", StringComparison.Ordinal))
                {
                    string prefix = name.Substring(0, name.Length - 1);
                    for (int l = 0; l < 32; l++)
                    {
                        string n = LayerMask.LayerToName(l);
                        if (!string.IsNullOrEmpty(n) && n.StartsWith(prefix, StringComparison.Ordinal))
                            s_groupMask[g] |= 1 << l;
                    }
                }
                else
                {
                    int l = LayerMask.NameToLayer(name);
                    if (l >= 0)
                        s_groupMask[g] |= 1 << l;
                }
                if (s_groupMask[g] == 0)
                    missing.Add(name);
            }
            if (missing.Count > 0)
                EnginePlugin.Log.LogWarning("ShadowCull: layers not in this game (left alone): " + string.Join(", ", missing));
        }

        // ---- Real Portal's copy of the game camera's distances --------------------------------------------------------

        static bool s_rpLooked, s_rpRerun, s_rpLogged;
        static AccessTools.FieldRef<Camera> s_rpCam;
        static MethodInfo s_rpUpdate, s_rpOff;

        // Real Portal loads after the engine (it depends on it), so its types exist only once the chainloader is past
        // it; the game camera exists only in the game scene, long after every plugin loaded. Looked up once, at the
        // first sync, before anything of ours is on the camera.
        static void LookForRealPortal()
        {
            s_rpLooked = true;
            Assembly asm = null;
            try
            {
                if (Chainloader.PluginInfos.TryGetValue(RealPortalGuid, out PluginInfo info) && info != null && info.Instance != null)
                    asm = info.Instance.GetType().Assembly;
            }
            catch
            {
                // no chainloader (test harness): no Real Portal
            }
            if (asm == null)
                return;
            RealPortalPresent = true;
            HookRealPortal(asm.GetType("RealPortal.WindowLayerCull", false));
        }

        // Prefix and postfix on Real Portal's WindowLayerCull.Update(Camera). Separate for the test harness.
        internal static bool HookRealPortal(Type t)
        {
            try
            {
                MethodInfo update = t == null ? null : AccessTools.DeclaredMethod(t, "Update", new[] { typeof(Camera) });
                MethodInfo off = t == null ? null : AccessTools.DeclaredMethod(t, "Off", Type.EmptyTypes);
                FieldInfo cam = t == null ? null : AccessTools.DeclaredField(t, "s_cam");
                if (update == null || off == null || cam == null || !cam.IsStatic || cam.FieldType != typeof(Camera))
                    throw new MissingMemberException("RealPortal.WindowLayerCull: Update(Camera), Off() or static Camera s_cam");
                s_rpCam = AccessTools.StaticFieldRefAccess<Camera>(cam);
                s_rpUpdate = update;
                s_rpOff = off;
                s_harmony.Patch(update,
                    prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(ShadowCullModule), nameof(BeforeRealPortalCull))),
                    postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(ShadowCullModule), nameof(AfterRealPortalCull))));
                RealPortalHooked = true;
                EnginePlugin.Log.LogInfo("ShadowCull: Real Portal's Win.LayerCull takes the game camera's distances without CameraLayerCull's.");
                return true;
            }
            catch (Exception e)
            {
                EnginePlugin.Log.LogWarning("ShadowCull: Real Portal's WindowLayerCull not hooked; CameraLayerCull stands down while Real Portal is loaded (its copy of the camera's distances would hold ours): " + e.Message);
                return false;
            }
        }

        static void BeforeRealPortalCull(out Camera __state)
        {
            __state = s_rpCam != null ? s_rpCam() : null;
        }

        // After WindowLayerCull.Update(main): if it took its copy of `main`'s distances in this call while ours were on
        // them, make it take the copy again without ours.
        static void AfterRealPortalCull(Camera __0, Camera __state)
        {
            if (s_rpRerun)
                return;
            Camera main = __0;
            Camera captured = s_rpCam != null ? s_rpCam() : null;
            if ((object)main == null || captured == null || !ReferenceEquals(captured, main) || ReferenceEquals(captured, __state))
                return;
            RealPortalCaptures++;
            if (s_cameraArray == null || !ReferenceEquals(s_cameraArray.Key, main) || !s_camera.Holds)
                return;   // nothing of ours on that camera: a clean copy
            s_rpRerun = true;
            try
            {
                s_rpOff.Invoke(null, null);                        // it writes back what it took (ours included) and forgets it
                s_camera.Release();                                // ours off: the camera holds what lies under it
                s_rpUpdate.Invoke(null, new object[] { main });    // it takes the camera's own distances again
                RealPortalRecaptures++;
                s_syncNow = true;                                  // ours on again this frame (our postfix runs after Real Portal's)
                if (!s_rpLogged)
                {
                    s_rpLogged = true;
                    EnginePlugin.Log.LogInfo("ShadowCull: Real Portal copied the game camera's distances with CameraLayerCull on; made it copy them again without.");
                }
            }
            catch (Exception e)
            {
                Errors++;
                float now = Time.unscaledTime;
                if (now >= s_errorAt)
                {
                    s_errorAt = now + 10f;
                    EnginePlugin.Log.LogError("ShadowCull: Real Portal recapture: " + e);
                }
            }
            finally
            {
                s_rpRerun = false;
            }
        }

        // ---- the per-layer arrays ------------------------------------------------------------------------------------

        // One 32-float per-layer array on a Unity object: a camera's layerCullDistances or a light's
        // layerShadowCullDistances. Behind an interface so the overlay logic can be checked out of the game.
        internal interface ILayerArray
        {
            object Key { get; }          // the Unity object; a new one starts the overlay fresh
            bool Alive { get; }
            float[] Read();              // a fresh 32-element copy (Unity's getters allocate one)
            void Write(float[] values);  // Unity copies the array
            bool HasSpherical { get; }   // cameras: layerCullSpherical
            bool Spherical { get; set; }
        }

        sealed class CameraArray : ILayerArray
        {
            readonly Camera _camera;
            internal CameraArray(Camera camera) => _camera = camera;
            public object Key => _camera;
            public bool Alive => _camera != null;
            public float[] Read() => Normalize(_camera.layerCullDistances);
            public void Write(float[] values) => _camera.layerCullDistances = values;
            public bool HasSpherical => true;
            public bool Spherical
            {
                get => _camera.layerCullSpherical;
                set => _camera.layerCullSpherical = value;
            }
        }

        sealed class SunArray : ILayerArray
        {
            readonly Light _light;
            internal SunArray(Light light) => _light = light;
            public object Key => _light;
            public bool Alive => _light != null;
            public float[] Read() => Normalize(_light.layerShadowCullDistances);
            public void Write(float[] values) => _light.layerShadowCullDistances = values;
            public bool HasSpherical => false;
            public bool Spherical
            {
                get => false;
                set { }
            }
        }

        static float[] Normalize(float[] v)
        {
            if (v != null && v.Length == 32)
                return v;
            var fixedUp = new float[32];
            if (v != null)
                Array.Copy(v, fixedUp, Math.Min(32, v.Length));
            return fixedUp;
        }

        // Our per-layer distances laid over one array that others may write too.
        //  - on: each capped layer holds min(base, cap), where base is what someone else last put there (0 = none);
        //  - a layer that no longer holds what we wrote was rewritten by someone else: that is the new base, unless the
        //    value there is one we wrote earlier on that layer (since the last real rewrite by someone else): a copy
        //    taken while that value was ours, put back. Such a value is never taken for someone else's;
        //  - off: every layer still holding one of our values gets its base back. Our last values are remembered for
        //    GhostSeconds (ghost): a writer that puts back a copy taken while ours was on, all of our layers at once, has
        //    it undone; a value on a single layer that happens to equal ours is left alone, and after GhostSeconds
        //    whatever is there is whoever wrote it (the watch, one read a second, ends);
        //  - Release (for Real Portal's recapture): like off, without the ghost, to be composed again at once.
        // A new target (scene change) starts fresh; the old one is put back if it still exists.
        internal sealed class LayerOverlay
        {
            internal const int History = 4;
            internal static float GhostSeconds = 30f;

            readonly string _name;
            ILayerArray _target;
            readonly float[] _base = new float[32];      // what lies under ours (kept after off: what our old values covered)
            readonly float[] _want = new float[32];
            readonly float[] _writtenBuf = new float[32];
            float[] _written;                            // == _writtenBuf while something of ours is on the target
            readonly float[] _history = new float[32 * History];  // older values of ours per layer, newest first; NaN = none
            float[] _ghost;                              // after off: our last values (== _base where we held nothing)
            float _ghostUntil;
            bool _sphericalOurs, _holds;
            internal long Writes, Foreign, StaleOurs, GhostReverts;

            internal LayerOverlay(string name)
            {
                _name = name;
                ClearHistory();
            }

            internal bool Idle => _written == null && _ghost == null;

            // Something of ours (a layer whose value differs from what lies under it) is on the target now.
            internal bool Holds => _written != null && _holds;

            void Write(float[] v)
            {
                _target.Write(v);
                Writes++;
            }

            internal void Sync(ILayerArray target, bool on, float[] caps, float now = 0f)
            {
                if (!ReferenceEquals(target?.Key, _target?.Key))
                {
                    if (_target != null && _target.Alive && _written != null)
                        Put(false, now);
                    _target = target;
                    _written = null;
                    _ghost = null;
                    _sphericalOurs = false;
                    ClearHistory();
                }
                if (_target == null || !_target.Alive)
                    return;
                if (!on)
                {
                    if (_written != null)
                        Put(true, now);
                    else if (_ghost != null)
                        WatchGhost(now);
                    return;
                }
                float[] cur = _target.Read();
                uint rebased = 0;
                if (_written == null)
                {
                    for (int i = 0; i < 32; i++)
                    {
                        // Our old value put back by someone while we were off is not theirs: the base is what it covered.
                        bool ours = _ghost != null && _ghost[i] != _base[i] && cur[i] == _ghost[i] || InHistory(i, cur[i]) && cur[i] != _base[i];
                        if (ours)
                            StaleOurs++;
                        else
                            _base[i] = cur[i];
                    }
                    _ghost = null;
                }
                else
                {
                    for (int i = 0; i < 32; i++)
                    {
                        if (cur[i] == _written[i])
                            continue;
                        if (InHistory(i, cur[i]) && cur[i] != _base[i])
                        {
                            // An older value of ours (a copy taken while it was on, put back): composed over again.
                            StaleOurs++;
                            continue;
                        }
                        _base[i] = cur[i];
                        Foreign++;
                        ClearHistory(i);
                        rebased |= 1u << i;
                    }
                }
                bool differs = false, capped = false, holds = false;
                for (int i = 0; i < 32; i++)
                {
                    float b = _base[i], c = caps[i];
                    float w = c > 0f ? (b > 0f ? Math.Min(b, c) : c) : b;
                    _want[i] = w;
                    differs |= w != cur[i];
                    capped |= c > 0f;
                    holds |= w != b;
                    // The value of ours this replaces (a cap change): remembered, unless someone else rewrote the layer.
                    if (_written != null && (rebased & (1u << i)) == 0 && _written[i] != w && _written[i] != b)
                        PushHistory(i, _written[i]);
                }
                if (differs)
                    Write(_want);
                Array.Copy(_want, _writtenBuf, 32);
                _written = _writtenBuf;
                _holds = holds;
                // Camera distances measured from the camera, not along its view axis (Real Portal sets the same).
                if (_target.HasSpherical && capped && !_target.Spherical)
                {
                    _target.Spherical = true;
                    _sphericalOurs = true;
                }
            }

            // Ours off the target: every layer still holding a value of ours gets its base back.
            internal void Release()
            {
                if (_target != null && _target.Alive && _written != null)
                    Put(false, 0f);
            }

            void Put(bool keepGhost, float now)
            {
                float[] cur = _target.Read();
                bool changed = false, anyGhost = false;
                float[] ghost = keepGhost ? new float[32] : null;
                for (int i = 0; i < 32; i++)
                {
                    bool held = _written[i] != _base[i];
                    if (held && cur[i] == _written[i] || InHistory(i, cur[i]) && cur[i] != _base[i])
                    {
                        cur[i] = _base[i];
                        changed = true;
                    }
                    if (ghost != null)
                    {
                        ghost[i] = held ? _written[i] : _base[i];
                        anyGhost |= held;
                    }
                }
                if (changed)
                    Write(cur);
                if (_sphericalOurs)
                {
                    // Back to planar only if no layer is distance-culled any more (otherwise someone else relies on it).
                    bool none = true;
                    for (int i = 0; i < 32 && none; i++)
                        none = cur[i] == 0f;
                    if (none)
                        _target.Spherical = false;
                    _sphericalOurs = false;
                }
                _written = null;
                _holds = false;
                _ghost = anyGhost ? ghost : null;
                _ghostUntil = now + GhostSeconds;
            }

            void WatchGhost(float now)
            {
                if (now > _ghostUntil)
                {
                    // From now on a value there is whoever wrote it.
                    _ghost = null;
                    ClearHistory();
                    return;
                }
                float[] cur = _target.Read();
                bool all = true, anyLayer = false;
                for (int i = 0; i < 32 && all; i++)
                {
                    if (_ghost[i] == _base[i])
                        continue;
                    anyLayer = true;
                    all = cur[i] == _ghost[i] || InHistory(i, cur[i]) && cur[i] != _base[i];
                }
                if (!anyLayer || !all)
                    return;
                for (int i = 0; i < 32; i++)
                    if (_ghost[i] != _base[i])
                        cur[i] = _base[i];
                Write(cur);
                GhostReverts++;
                EnginePlugin.Log?.LogInfo($"ShadowCull: a copy of our old layer distances came back on the {_name} after switching off; undone.");
            }

            // The target's values without ours (see CameraBase).
            internal void BaseOf(object key, float[] cur, float[] into)
            {
                bool ours = _written != null && _target != null && ReferenceEquals(key, _target.Key);
                for (int i = 0; i < 32; i++)
                {
                    bool held = ours && _written[i] != _base[i] && (cur[i] == _written[i] || InHistory(i, cur[i]));
                    into[i] = held ? _base[i] : cur[i];
                }
            }

            void ClearHistory()
            {
                for (int k = 0; k < _history.Length; k++)
                    _history[k] = float.NaN;
            }

            void ClearHistory(int layer)
            {
                for (int k = 0; k < History; k++)
                    _history[layer * History + k] = float.NaN;
            }

            bool InHistory(int layer, float v)
            {
                int o = layer * History;
                for (int k = 0; k < History; k++)
                    if (_history[o + k] == v)
                        return true;    // NaN (empty) never equals
                return false;
            }

            void PushHistory(int layer, float v)
            {
                if (InHistory(layer, v))
                    return;
                int o = layer * History;
                for (int k = History - 1; k > 0; k--)
                    _history[o + k] = _history[o + k - 1];
                _history[o] = v;
            }
        }

        // ---- point-light shadow slots ------------------------------------------------------------------------------

        static AccessTools.FieldRef<LightLod, int> s_prio;
        static AccessTools.FieldRef<LightLod, float> s_outer, s_baseRange, s_baseStrength;
        static AccessTools.FieldRef<LightLod, Light> s_light;
        static AccessTools.FieldRef<float> s_updateTimer;
        static List<LightLod> s_sorted;
        static MethodInfo s_updateLights, s_updateLoop;
        static readonly Plane[] s_planes = new Plane[6];
        static float s_checkForeignAt = -1f;

        // UpdateLights ranks only when its timer passes 1 s (m_updateTimer += dt; then reset to 0). The prefix tells the
        // postfix whether this call is one of those.
        static void BeforeUpdateLights(float dt, out bool __state)
        {
            __state = (LightLod.m_lightLimit >= 0 || LightLod.m_shadowLimit >= 0) && s_updateTimer() + dt >= 1f;
        }

        static void AfterUpdateLights(bool __state, bool __runOriginal)
        {
            if (!__state || !__runOriginal || s_updateTimer() != 0f)
                return;
            // A new game ranking: the stealth parity of the last one is over (rebuilt below while on).
            s_parityCount = 0;
            if (!PointRankFeature.On)
                return;
            try
            {
                Rerank();
            }
            catch (Exception e)
            {
                // The game's order (already assigned) stays.
                Errors++;
                s_parityCount = 0;
                float now = Time.unscaledTime;
                if (now >= s_errorAt)
                {
                    s_errorAt = now + 10f;
                    EnginePlugin.Log.LogError("ShadowCull: point-light ranking: " + e);
                }
            }
        }

        static void Rerank()
        {
            Camera cam = Utils.GetMainCamera();
            if (cam == null || !StealthPatched || Foreign())
                return;   // the game returned without ranking (no camera), or another mod changed how it ranks
            List<LightLod> sorted = s_sorted;
            int n = sorted.Count;
            // The game has just set m_lightPrio = i along its order; anything else means someone changed it after the
            // game (a postfix before ours): their order stays.
            for (int i = 0; i < n; i++)
            {
                LightLod l = sorted[i];
                if (l != null && s_prio(l) != i)
                {
                    ForeignOrder++;
                    return;
                }
            }
            int shadowLimit = LightLod.m_shadowLimit, lightLimit = LightLod.m_lightLimit;
            int lit = lightLimit < 0 ? n : Math.Min(lightLimit, n);
            Reranks++;
            // No slot to give, or every lit light has one already: the game's order decides nothing here.
            if (shadowLimit <= 0 || shadowLimit >= lit)
                return;
            GeometryUtility.CalculateFrustumPlanes(cam, s_planes);
            // LightLod.GetLightReferencePoint: the camera in free fly or without a player, else the player.
            Vector3 reference = GameCamera.InFreeFly() || Player.m_localPlayer == null ? cam.transform.position : Player.m_localPlayer.transform.position;
            if (s_group.Length < lit)
            {
                s_group = new byte[Math.Max(lit, s_group.Length * 2)];
                s_order = new int[s_group.Length];
                s_seen = new bool[s_group.Length];
            }
            for (int i = 0; i < lit; i++)
            {
                LightLod l = sorted[i];
                if (l == null)
                {
                    s_group[i] = Unusable;
                    continue;
                }
                // The distance the game's ranking used (Real Portal's window distance included): its outer distance
                // is that minus a quarter of the light distance. The coroutine shadows a light only within its
                // m_shadowDistance, so beyond it (or without shadow LOD) a slot would be wasted.
                float d = s_outer(l) + l.m_lightDistance * 0.25f;
                if (!l.m_shadowLod || !(d < l.m_shadowDistance))
                    s_group[i] = Unusable;
                else
                    s_group[i] = Visible(l, reference, d) ? Seen : OffView;
            }
            Order(s_group, lit, s_order);
            long moved = 0;
            for (int i = 0; i < lit; i++)
            {
                LightLod l = sorted[i];
                int prio = s_order[i];
                if (s_group[i] == Seen)
                {
                    if (i < shadowLimit)
                        GameSlotsInView++;
                    if (prio < shadowLimit)
                        RankedSlotsInView++;
                }
                if (l == null || prio == i)
                    continue;
                bool hadSlot = i < shadowLimit, hasSlot = prio < shadowLimit;
                if (hasSlot && !hadSlot)
                    Promoted++;
                else if (hadSlot && !hasSlot)
                    Demoted++;
                s_prio(l) = prio;
                moved++;
                // The stealth light level keeps the game's shadow state for this light (see BeforeLightLevel).
                bool usable = s_group[i] != Unusable;
                bool gameShadow = usable && hadSlot, ourShadow = usable && hasSlot;
                if (gameShadow != ourShadow)
                    AddParity(s_light(l), gameShadow, s_baseStrength(l));
            }
            s_parityShadowLimit = shadowLimit;
            if (moved > 0)
                Reordered++;
            CheckLightSet(sorted, n, lit, lightLimit);
        }

        // Independent of how the new priorities were made: those of the first `lit` lights are distinct and below
        // `lit`, the rest are the game's, and every light's place against m_lightLimit is the game's (its game
        // priority is its index in the game's order). Anything else counts a mismatch.
        static void CheckLightSet(List<LightLod> sorted, int n, int lit, int lightLimit)
        {
            Array.Clear(s_seen, 0, Math.Min(lit, s_seen.Length));
            bool ok = true;
            for (int i = 0; i < n && ok; i++)
            {
                LightLod l = sorted[i];
                if (l == null)
                    continue;
                int p = s_prio(l);
                if (i < lit)
                {
                    if (p < 0 || p >= lit || s_seen[p])
                        ok = false;
                    else
                        s_seen[p] = true;
                }
                else if (p != i)
                    ok = false;
                if (lightLimit >= 0 && (p < lightLimit) != (i < lightLimit))
                    ok = false;
            }
            if (ok)
                return;
            LightSetMismatches++;
            if (LightSetMismatches <= 5)
                EnginePlugin.Log.LogWarning($"ShadowCull: point-light priorities after the ranking are not a permutation that keeps the game's lit set ({n} lights, {lit} lit).");
        }

        // Slot groups, in the order they get slots.
        internal const byte Seen = 0, OffView = 1, Unusable = 2;
        static byte[] s_group = new byte[64];
        static int[] s_order = new int[64];
        static bool[] s_seen = new bool[64];

        // New priorities for the first `count` lights of the game's order: every Seen light first, then OffView, then
        // Unusable, each group keeping the game's order. A permutation of 0..count-1, so a light's place against
        // m_lightLimit (>= count, or count is every light) cannot change: which lights are lit stays the game's.
        internal static void Order(byte[] group, int count, int[] prioOut)
        {
            int p = 0;
            for (byte g = Seen; g <= Unusable; g++)
                for (int i = 0; i < count; i++)
                    if (group[i] == g)
                        prioOut[i] = p++;
        }

        static bool Visible(LightLod l, Vector3 reference, float gameDistance)
        {
            Vector3 c = l.transform.position;
            float r = Mathf.Max(0f, s_baseRange(l)) + FrustumMargin;
            bool inside = true;
            for (int p = 0; p < 6 && inside; p++)
                inside = s_planes[p].GetDistanceToPoint(c) >= -r;
            if (inside)
            {
                InView++;
                return true;
            }
            // Real Portal's distance (through a live window) is shorter than the straight one: seen through a window.
            if (gameDistance < Vector3.Distance(reference, c) - 0.05f)
            {
                ThroughWindow++;
                return true;
            }
            Func<Vector3, float, bool> hook = VisibleElsewhere;
            if (hook != null)
            {
                bool seen;
                try
                {
                    seen = hook(c, r);
                }
                catch (Exception e)
                {
                    Errors++;
                    seen = true;
                    float now = Time.unscaledTime;
                    if (now >= s_errorAt)
                    {
                        s_errorAt = now + 10f;
                        EnginePlugin.Log.LogError("ShadowCull: VisibleElsewhere threw (counted visible): " + e);
                    }
                }
                if (seen)
                {
                    ByHook++;
                    return true;
                }
            }
            OffScreen++;
            return false;
        }

        // Another mod's transpiler on UpdateLights or the coroutine that reads the priorities. Real Portal's (it swaps
        // Vector3.Distance for its window-aware distance, which is what we read back) is known; anything else could
        // change what a priority means, so the game's order stays. Checked every 5 s (Harmony's patch info allocates).
        static bool Foreign()
        {
            float now = Time.unscaledTime;
            if (now < s_checkForeignAt)
                return StoodDown;
            s_checkForeignAt = now + 5f;
            string who = ForeignTranspiler(s_updateLights) ?? ForeignTranspiler(s_updateLoop);
            bool down = who != null;
            if (down != StoodDown || (down && who != StandDownBy))
                EnginePlugin.Log.LogWarning(down
                    ? $"ShadowCull: PointShadowVisibleRank stands down: LightLod is transpiled by {who}; the game's order stays."
                    : "ShadowCull: PointShadowVisibleRank active again (no foreign transpiler on LightLod).");
            StoodDown = down;
            StandDownBy = down ? who : "";
            return down;
        }

        static string ForeignTranspiler(MethodBase original)
        {
            if (original == null)
                return null;
            try
            {
                Patches info = Harmony.GetPatchInfo(original);
                if (info == null)
                    return null;
                foreach (Patch p in info.Transpilers)
                    if (p.owner != s_harmonyId && p.owner != EnginePlugin.Guid && p.owner != RealPortalGuid)
                        return p.owner;
            }
            catch
            {
                // patch info unavailable: nothing known against it
            }
            return null;
        }

        // ---- the stealth light level keeps the game's shadow slots ---------------------------------------------------

        // StealthSystem.GetLightLevel counts a point light within range at full strength unless light.shadows != None
        // and a ray to the player is blocked, then at (1 - shadowStrength). Lights whose slot we moved: the state the
        // game's order gives them (with a slot: Soft at their base strength; without: None), set around the call and
        // put back after it (a finalizer, so an exception in the game's body cannot leave them changed).
        static Light[] s_parityLight = new Light[16];
        static bool[] s_parityShadow = new bool[16];
        static float[] s_parityStrength = new float[16];
        static LightShadows[] s_savedShadows = new LightShadows[16];
        static float[] s_savedStrength = new float[16];
        static int s_parityCount, s_parityShadowLimit = -1;
        static bool s_parityApplied;

        static void AddParity(Light light, bool shadow, float strength)
        {
            if ((object)light == null)
                return;
            if (s_parityCount == s_parityLight.Length)
            {
                int size = s_parityCount * 2;
                Array.Resize(ref s_parityLight, size);
                Array.Resize(ref s_parityShadow, size);
                Array.Resize(ref s_parityStrength, size);
                Array.Resize(ref s_savedShadows, size);
                Array.Resize(ref s_savedStrength, size);
            }
            s_parityLight[s_parityCount] = light;
            s_parityShadow[s_parityCount] = shadow;
            s_parityStrength[s_parityCount] = strength;
            s_parityCount++;
        }

        static void BeforeLightLevel()
        {
            if (s_parityCount == 0 || s_parityApplied)
                return;
            if (LightLod.m_shadowLimit != s_parityShadowLimit)
            {
                // The setting changed since the ranking: the game's slots are no longer what was recorded.
                s_parityCount = 0;
                return;
            }
            StealthParityCalls++;
            for (int k = 0; k < s_parityCount; k++)
            {
                Light l = s_parityLight[k];
                if (l == null)
                    continue;
                s_savedShadows[k] = l.shadows;
                s_savedStrength[k] = l.shadowStrength;
                if (s_parityShadow[k])
                {
                    if (l.shadows == LightShadows.None)
                        l.shadows = LightShadows.Soft;
                    l.shadowStrength = s_parityStrength[k];
                }
                else if (l.shadows != LightShadows.None)
                    l.shadows = LightShadows.None;
            }
            s_parityApplied = true;
        }

        static void AfterLightLevel()
        {
            if (!s_parityApplied)
                return;
            s_parityApplied = false;
            for (int k = 0; k < s_parityCount; k++)
            {
                Light l = s_parityLight[k];
                if (l == null)
                    continue;
                if (l.shadows != s_savedShadows[k])
                    l.shadows = s_savedShadows[k];
                if (l.shadowStrength != s_savedStrength[k])
                    l.shadowStrength = s_savedStrength[k];
            }
        }

        // Lab: one line for the log.
        public static string Report() =>
            $"ShadowCull: sun {(SunFeature.On ? "on" : "off")} {SunLayers} layer(s), {SunWrites} writes, {SunForeign} foreign; " +
            $"camera {(CameraFeature.On ? "on" : "off")}{(CameraStoodDown ? " (stood down: Real Portal not hooked)" : "")} {CameraLayers} layer(s), {CameraWrites} writes, {CameraForeign} foreign; " +
            $"stale ours {StaleOurs}, ghost reverts {GhostReverts}; Real Portal {(RealPortalPresent ? RealPortalHooked ? "hooked" : "NOT hooked" : "absent")}, copies {RealPortalCaptures}, retaken without ours {RealPortalRecaptures}; " +
            $"point rank {(PointRankFeature.On ? "on" : "off")}{(StoodDown ? " (stood down: " + StandDownBy + ")" : "")}: {Reranks} rankings, {Reordered} reordered, foreign order {ForeignOrder}, " +
            $"+{Promoted}/-{Demoted} slots, slots in view game {GameSlotsInView} / ranked {RankedSlotsInView}, in view {InView}, through windows {ThroughWindow}, by hook {ByHook}, off-screen {OffScreen}, " +
            $"light-set mismatches {LightSetMismatches}, stealth parity {StealthParityCalls}; errors {Errors}";
    }
}
