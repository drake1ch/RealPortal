using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("RealPortalLab")]

namespace RealPortal
{
    // "Portal"-style portals for Valheim: every portal (vanilla wooden and stone ones, modded ones) shows the live
    // view of its partner, and players, creatures, dropped items and projectiles pass straight through it.
    [BepInPlugin(Guid, Name, Version)]
    // The engine plugin carries the feature switches (RealPortal.Features) every window flag is registered in, and from
    // 0.1.1 the Burst build of the proxies' jobs (BurstBootstrapModule.ViewCull/ViewEmit).
    [BepInDependency(EngineGuid, "0.1.1")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "valheim.realportal";
        public const string EngineGuid = "valheim.realportal.engine";
        public const string Name = "Real Portal";
        public const string Version = "1.0.5";

        internal const string BundleFile = "realportal.bundle";
        internal const string ShaderAsset = "PortalWindow";

        internal static ManualLogSource Log;
        internal static Material WindowMaterial;
        // realportal.bundle, kept loaded: Windows\WindowAssetLoader takes the S3 shaders from it (null on a server).
        internal static AssetBundle Bundle;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> RespectItemRestrictions;
        internal static ConfigEntry<bool> Items;
        internal static ConfigEntry<bool> Projectiles;
        internal static ConfigEntry<bool> CameraThrough;
        internal static ConfigEntry<bool> Sound;
        internal static ConfigEntry<bool> Reach;
        internal static ConfigEntry<bool> CreatureSight, CreatureHearing, WildCrossing, FriendlyCrossing, MeleeThrough, LiveFarSide;

        internal static ConfigEntry<float> Resolution;
        internal static ConfigEntry<float> MaxDistance;
        internal static ConfigEntry<float> FarClip;
        internal static ConfigEntry<float> ShadowDistance;
        internal static ConfigEntry<float> LodBias;
        internal static ConfigEntry<int> Recursion;
        internal static ConfigEntry<PortalEffectsMode> PortalEffects;
        internal static ConfigEntry<bool> ShowPlayer;
        internal static ConfigEntry<CameraClearFlags> Sky;
        internal static ConfigEntry<RenderingPath> RenderPath;
        internal static ConfigEntry<PortalClip> Clip;
        internal static ConfigEntry<bool> Fog;
        internal static ConfigEntry<bool> VirtualDepth;
        internal static ConfigEntry<bool> SunShafts;
        internal static ConfigEntry<bool> FullFrame;
        internal static ConfigEntry<bool> AmbientOcclusion;
        internal static ConfigEntry<float> Margin;
        internal static ConfigEntry<bool> MotionVectors;
        internal static ConfigEntry<bool> RefreshSmallLess;
        internal static ConfigEntry<int> MaxWindowRenders;
        // FARWORLD-V3 V0: a preset over the window switches (WindowFeatures.BindQuality); QualityApplied is bookkeeping,
        // not a setting: the Quality whose switches were last set (kept in the engine's config, beside them).
        internal static ConfigEntry<WindowQuality> Quality;
        internal static ConfigEntry<string> QualityApplied;
        // Window Pipeline v2 (S3, Win.Tiers / Win.Budget): copied into WindowPipelineSettings every frame (WindowGlue).
        internal static ConfigEntry<float> CpuBudgetMs, GpuBudgetMs, TierAShare, TierBShare, TierCShare;
        // Bookkeeping, not a setting: Real Portal switched the engine's GrassMainCameraOnly on (FarGrassSubmit).
        internal static ConfigEntry<bool> GrassEngineOwned;

        internal static ConfigEntry<float> PreloadDistance;
        internal static ConfigEntry<int> FarAreaZones;
        internal static ConfigEntry<float> UnloadMargin, UnloadDelay;
        internal static ConfigEntry<int> MaxFarAreas;
        internal static ConfigEntry<float> LoadBudgetMs, ActivationBudgetMs;
        internal static ConfigEntry<bool> FarWeather;

        internal static ConfigEntry<float> WoodCenterHeight;
        internal static ConfigEntry<float> WoodDiameter;
        internal static ConfigEntry<bool> Verbose;
        internal static ConfigEntry<KeyboardShortcut> DumpKey;

        private void Awake()
        {
            Log = Logger;
            Enabled = Config.Bind("General", "Enabled", true, "Turn real portals on or off.");
            RespectItemRestrictions = Config.Bind("General", "RespectItemRestrictions", true,
                "Players carrying ore and other non-teleportable items cannot pass, and such dropped items do not either (like vanilla portals).");
            Items = Config.Bind("General", "Items", true, "Dropped items fly through real portals.");
            Projectiles = Config.Bind("General", "Projectiles", true, "Arrows, thrown knives and other projectiles fly through real portals.");
            Sound = Config.Bind("General", "Sound", true,
                "Sounds on the far side of a real portal are heard through it; while the camera looks back through a portal you hear from where you stand.");
            Reach = Config.Bind("General", "ReachThroughPortals", true,
                "What you aim at past a real portal's opening you can use (open a chest, pick up an item, pull a lever) and build on, within the usual reach counted through the portal.");
            CameraThrough = Config.Bind("General", "CameraThroughPortal", true,
                "When the third-person camera behind you would end up past a portal, place it on the other side so it keeps looking at you.");

            // Section "Window" replaces the old "Render" keys, whose defaults (forward, no shadows, 300 m) made the
            // view look different from the game.
            // FARWORLD-V3 V0: the cached tiers of the test builds trailed under the game's TAA and moved at a lower frame
            // rate than the game; Continuation brings back 1.0.3's live windows. It sets engine switches when changed
            // (WindowFeatures.BindQuality), and each of them can still be changed alone in section Engine afterwards.
            Quality = Config.Bind("Window", "Quality", WindowQuality.Continuation,
                "Continuation: the view through every window is live, a continuation of the game (as in 1.0.3: large and near windows " +
                "every frame, the others every frame while there is time to spare, at least every second or third), with no afterimages " +
                "when the camera turns. Performance: small and distant windows keep their last view for several frames, which is much " +
                "cheaper with many portals in sight, but their picture lags and trails as the camera turns. Continuation also shows every " +
                "window exactly as the game would there at a cost in time: distant and nested windows render, every window on screen every frame, " +
                "a window opens once the side it shows is fully loaded, far ends as wide as the game's own, and the windows' antialiasing fixes. " +
                "Changing it sets the engine switches Win.Tiers, Win.PointDepth, Win.ExplicitJitter, Win.LiveFill, Win.EnvOncePerArea, Win.CaptureOnce, " +
                "Far.InViewStep, Far.ParticleWindowGate, Far.MistTickShown, Far.SideReady, Far.FullAreaZones, Win.DistantLive, Win.NestedLive, " +
                "Win.AllLive, Win.TaaViewDepth, Win.BlurInputs, Win.ShadowRes, Win.StaleTaa, WearNTearActiveSectors and FarNavmeshCap " +
                "(section Engine); one changed there afterwards stays as set until this is changed again.");
            // In the engine's config, beside the switches it sets, so the two are reset together (Real Portal's own config
            // only if the engine bound none).
            QualityApplied = (Features.Config ?? Config).Bind("Debug", "WindowQualityApplied", "",
                "Kept by Real Portal, not a setting: the Window.Quality whose engine switches were last set, so they are set again only " +
                "when Quality changes (an engine switch changed alone stays as it is).");
            Resolution = Config.Bind("Window", "Resolution", 1f,
                new ConfigDescription("Window render resolution relative to the screen (1 = as sharp as the game).", new AcceptableValueRange<float>(0.2f, 1f)));
            MaxDistance = Config.Bind("Window", "MaxDistance", 60f,
                "Windows farther than this (m) are not rendered and show the swirl (with the engine switch Win.VanillaLook: the portal's own " +
                "empty ring). Only without the engine switch Win.DistantLive (Quality " +
                "Continuation turns it on): with it windows render as far as their far end stays loaded (PreloadDistance + UnloadExtraDistance).");
            FarClip = Config.Bind("Window", "DrawDistance", 0f,
                "Draw distance (m) of the view through a window; 0 = the same as the game. Below the game's value the sky dome (clouds, haze) and far islands get cut off.");
            ShadowDistance = Config.Bind("Window", "ShadowDistance", -1f, "Shadow distance (m) in the view through a window; negative = the game's, 0 = no shadows.");
            LodBias = Config.Bind("Window", "LodBiasMultiplier", 1f, "LOD bias multiplier for the view through a window: 1 = as the game, lower is cheaper but coarser.");
            Recursion = Config.Bind("Window", "Recursion", 0,
                new ConfigDescription("0: a window seen through a window shows the swirl. 1: one more level (one more render). Only without the " +
                                      "engine switch Win.NestedLive (Quality Continuation turns it on): with it a window seen through a window " +
                                      "renders whenever its own far side is loaded.", new AcceptableValueRange<int>(0, 1)));
            ShowPlayer = Config.Bind("Window", "ShowPlayerThroughPortal", true,
                "While you step through, show the part of your body that is already on the other side.");
            PortalEffects = Config.Bind("Window", "PortalEffects", PortalEffectsMode.Window,
                "The swirl, sparks and light inside the frame of a live portal. Window: only while the portal does not let " +
                "someone through (ore, a creature), so the view stays clear. Vanilla: as in the game, whenever a player who " +
                "may pass stands close (it covers part of the view).");
            Sky = Config.Bind("Window", "Sky", CameraClearFlags.Skybox, "How the window camera clears: Skybox or SolidColor.");
            RenderPath = Config.Bind("Window", "RenderingPath", RenderingPath.UsePlayerSettings,
                "Rendering path of the window camera; UsePlayerSettings = the same as the game (deferred).");
            Clip = Config.Bind("Window", "Clip", PortalClip.NearPlane,
                "NearPlane: rendered exactly like the game (deferred, shadows, fog); objects standing right behind the exit portal are hidden. " +
                "Oblique: exact cut at the portal plane, but deferred lighting, shadows and fog break with it.");
            // "VirtualDepth2": the first key defaulted to on, which the in-game lab showed to look worse.
            VirtualDepth = Config.Bind("Window", "VirtualDepth2", false,
                "Experimental: give the game camera the depth of what is seen through a window (its fog, ambient occlusion, sun shafts, TAA). " +
                "Measured in the lab it brightens the window and adds flicker, so it is off.");
            Fog = Config.Bind("Window", "Fog", true, "The window camera applies the game's distance fog itself, tinted by its own view direction against the sun (the game camera's fog then leaves the window alone).");
            FullFrame = Config.Bind("Window", "FullFrame", false,
                "Render the view through a window over the whole screen, not only the window's rect: slower, but screen-space effects match the game exactly.");
            SunShafts = Config.Bind("Window", "SunShafts", true, "Sun shafts from the sun seen through a window (the game's own effect, copied to the window camera).");
            AmbientOcclusion = Config.Bind("Window", "AmbientOcclusion", true,
                "The game's SSAO (when on in its graphics settings) also on the view through a window (a copy on the window camera).");
            MotionVectors = Config.Bind("Window", "MotionVectors", true,
                "Give the game's TAA and motion blur the motion of what is seen through a window (without it the view smears like a surface right in front of the camera).");
            Margin = Config.Bind("Window", "Margin", 0.05f,
                new ConfigDescription("Extra border rendered around a window (fraction of the screen height): ambient occlusion near the window's edge needs it. 0 = only the window's rect.",
                    new AcceptableValueRange<float>(0f, 0.3f)));
            RefreshSmallLess = Config.Bind("Window", "RefreshSmallWindowsLess", true,
                "Windows covering under 5% of the screen and more than 8 m away get a new view every second or third frame; in between " +
                "their last view is laid onto the opening as it was seen (turning the camera keeps it exact). Large and near windows render every frame. " +
                "With Win.LiveFill (Quality Continuation) the small ones render every frame too while the frame has time to spare; with " +
                "Win.AllLive (Quality Continuation) every window on screen renders every frame and this is not used.");
            MaxWindowRenders = Config.Bind("Window", "MaxRendersPerFrame", 4,
                new ConfigDescription("At most this many small window views rendered in one frame (large and near ones always render); 0 = no limit. " +
                                      "Not used for windows on screen with Win.AllLive (Quality Continuation).",
                    new AcceptableValueRange<int>(0, 16)));
            // Window tiers (engine features Win.Tiers, Win.Budget; in force with Quality=Performance): which windows render
            // every frame and how much time the others get. Defaults of Docs\ARCHITECTURE-v2.md (§2.2, §4.6, Appendix B).
            CpuBudgetMs = Config.Bind("Window", "CpuBudgetMs", 0f,
                new ConfigDescription("Main-thread time (ms per frame) for the window views that need not render every frame (Win.Budget). " +
                                      "0 = automatic: what the frame has left of its target frame time, between 1.5 and 4.5 ms. " +
                                      "With Win.LiveFill (Quality Continuation): the time for the extra renders of small windows, beyond those due anyway " +
                                      "(0 = what the frame has left, at most 4.5 ms).",
                    new AcceptableValueRange<float>(0f, 20f)));
            GpuBudgetMs = Config.Bind("Window", "GpuBudgetMs", 0f,
                new ConfigDescription("Estimated GPU time (ms per frame) for those window views (Win.Budget). 0 = automatic: unlimited until the " +
                                      "graphics card sets the pace, then narrowed (and the views' resolution lowered down to 60%).",
                    new AcceptableValueRange<float>(0f, 30f)));
            TierAShare = Config.Bind("Window", "TierA_Share", 0.05f,
                new ConfigDescription("A window covering at least this share of the screen (or nearer than 8 m, or being walked through) renders every frame " +
                                      "with every effect (at most two such windows: the nearest).", new AcceptableValueRange<float>(0.005f, 1f)));
            TierBShare = Config.Bind("Window", "TierB_Share", 0.01f,
                new ConfigDescription("Between this share and TierA_Share a window keeps its view and renders it again once it would be off by a pixel " +
                                      "(at 3/4 resolution, simpler shadows); below it, or 30 m and farther away, by two pixels at half resolution.",
                    new AcceptableValueRange<float>(0.0005f, 0.5f)));
            TierCShare = Config.Bind("Window", "TierC_Share", 0.0015f,
                new ConfigDescription("Reserved for the panorama of distant windows (a later stage): no effect yet. Small windows show their view down to " +
                                      "the size where they show the swirl today.", new AcceptableValueRange<float>(0f, 0.1f)));
            GrassEngineOwned = Config.Bind("Debug", "GrassMainCameraOnlySetByRealPortal", false,
                "Kept by Real Portal, not a setting: true while the engine's GrassMainCameraOnly is on because Real Portal switched it on " +
                "(Grass.Targeted), so it is switched off again whenever Grass.Targeted is not in force, also after a crash.");
            PreloadDistance = Config.Bind("Distance", "PreloadDistance", 5f,
                new ConfigDescription("Within this distance (m) of a real portal its far end is loaded in the background, so the window is ready " +
                                      "before you get close (windows draw from MaxDistance, or with Win.DistantLive as soon as their far end is ready). " +
                                      "Every portal within it gets its far end. Until it is ready the portal stays closed. 5 m (the default since 1.0.4): " +
                                      "a portal opens as you come up to it, a few seconds later; 120 (1.0.3's default) shows windows from afar but loads " +
                                      "every far end around you.",
                    new AcceptableValueRange<float>(5f, 400f)));
            UnloadMargin = Config.Bind("Distance", "UnloadExtraDistance", 1f,
                new ConfigDescription("A far end stays loaded until you are this much (m) farther than PreloadDistance from its portal, so it does not " +
                                      "load and unload over and over as you walk along the edge.", new AcceptableValueRange<float>(0f, 200f)));
            UnloadDelay = Config.Bind("Distance", "UnloadDelay", 0f,
                new ConfigDescription("Seconds a far end stays loaded after you have left its range (coming straight back finds it ready).",
                    new AcceptableValueRange<float>(0f, 60f)));
            MaxFarAreas = Config.Bind("Distance", "MaxFarAreas", 1,
                new ConfigDescription("At most this many far ends loaded at once, those of the nearest portals (0 = no limit). For a room full of " +
                                      "portals: the others open as you come up to them.", new AcceptableValueRange<int>(0, 64)));
            FarAreaZones = Config.Bind("Distance", "FarAreaZones", 3,
                new ConfigDescription("How many zones around a far portal get everything loaded (0 = as your simulation distance; 3 looked the same in the lab). Big distant " +
                                      "objects (trees, buildings) come in as far as the game's own ring either way; fewer zones cost less memory and time. " +
                                      "Not used with the engine switch Far.FullAreaZones (Quality Continuation turns it on): far ends load as far as your simulation distance.",
                    new AcceptableValueRange<int>(0, 12)));
            LoadBudgetMs = Config.Bind("Distance", "LoadBudgetMs", 2f,
                new ConfigDescription("Main-thread time (ms per frame) spent loading far ends: lower is smoother, higher loads faster.",
                    new AcceptableValueRange<float>(0.5f, 10f)));
            ActivationBudgetMs = Config.Bind("Distance", "ActivationBudgetMs", 3f,
                new ConfigDescription("Main-thread time (ms per frame) for loading the far end of a portal you stand by or are about to look into, " +
                                      "while it opens: lower keeps the frame rate up while a portal opens, higher opens it sooner (1.0.3 used 8).",
                    new AcceptableValueRange<float>(0.5f, 10f)));
            FarWeather = Config.Bind("Distance", "FarWeather", true,
                "A window shows the far side's own weather and light (its biome's fog, sun colour, clouds), as the game would there.");
            // Each mechanic that changes what creatures do has its own switch; all off keeps portals mechanically vanilla.
            CreatureSight = Config.Bind("Creatures", "SeeThroughPortals", true,
                "Creatures see characters on the far side through the opening of a real portal, and go after them.");
            CreatureHearing = Config.Bind("Creatures", "HearThroughPortals", true,
                "Creatures hear characters on the far side through the opening of a real portal, and go after them.");
            WildCrossing = Config.Bind("Creatures", "WildWalkThroughPortals", true,
                "Wild and hostile creatures walk through real portals: after you, away from you.");
            FriendlyCrossing = Config.Bind("Creatures", "FriendlyWalkThroughPortals", true,
                "Friendly creatures (tamed animals, summoned ones) walk through real portals, following you. With Path of Valheiman its own nodes decide instead.");
            MeleeThrough = Config.Bind("Creatures", "MeleeThroughPortals", true,
                "Melee attacks reach through the opening of a real portal, yours and creatures'.");
            LiveFarSide = Config.Bind("Creatures", "LiveFarSide", true,
                "Creatures and everything else around a loaded far end run as they do around you; off: it stands still until you arrive.");
            Api.Mechanics.Bind(Api.Mechanics.FriendlyCross, FriendlyCrossing);
            WoodCenterHeight = Config.Bind("Geometry", "WoodCenterHeight", 1.31f, "Height of the wooden portal's opening centre above its model origin (m).");
            WoodDiameter = Config.Bind("Geometry", "WoodDiameter", 2.85f, "Diameter of the wooden portal's round opening (m).");
            Verbose = Config.Bind("Debug", "Log", false, "Log portal geometry, camera setup and every pass through a portal.");
            DumpKey = Config.Bind("Debug", "DumpKey", new KeyboardShortcut(KeyCode.None),
                "Press while looking into a real portal: saves what the window camera renders (whole view and one picture per layer) " +
                "plus camera details to BepInEx\\RealPortalDebug. Only this mod's own camera is saved, not the screen.");

            var harmony = new Harmony(Guid);
            PortalServer.Install(harmony);
            DropRetiredConfig();
            // A dedicated server has no graphics: only what it does for its players (sending them the far ends).
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                Log.LogInfo("Real Portal on a dedicated server: far ends are generated and sent to the players near portals.");
                return;
            }
            WindowFeatures.Register();
            WindowFeatures.BindQuality(Quality, QualityApplied);
            WindowMaterial = LoadMaterial();
            if (WindowMaterial == null)
                return;
            harmony.PatchAll(typeof(Plugin).Assembly);
            FarLife.Install(harmony);
        }

        // Keys of older versions that nothing reads any more. BepInEx keeps an unknown key in the file for good, so players
        // edited [Render] MaxDistance and saw no change (the window's distance is [Window] MaxDistance, or the far end's
        // reach). Only these known keys go, after every key of this file is bound (PortalServer binds its own above): a
        // key bound later by some other path would otherwise lose its saved value.
        static readonly string[] s_retiredSections = { "Render" };
        static readonly (string Section, string Key)[] s_retiredKeys = { ("Distance", "MaxRemoteAreas") };

        void DropRetiredConfig()
        {
            try
            {
                if (!(AccessTools.Property(typeof(ConfigFile), "OrphanedEntries")?.GetValue(Config) is Dictionary<ConfigDefinition, string> orphans))
                    return;
                var drop = new List<ConfigDefinition>();
                foreach (ConfigDefinition d in orphans.Keys)
                    if (Array.IndexOf(s_retiredSections, d.Section) >= 0 || Array.Exists(s_retiredKeys, k => k.Section == d.Section && k.Key == d.Key))
                        drop.Add(d);
                if (drop.Count == 0)
                    return;
                foreach (ConfigDefinition d in drop)
                    orphans.Remove(d);
                Config.Save();
                Log.LogInfo($"Config: removed {drop.Count} key(s) of older versions that are no longer read ({string.Join(", ", drop.Select(d => d.Section + "." + d.Key))})");
            }
            catch (Exception e)
            {
                Log.LogWarning("Config: could not remove the keys of older versions: " + e.Message);
            }
        }

        private Material LoadMaterial()
        {
            string path = Path.Combine(Path.GetDirectoryName(Info.Location), BundleFile);
            if (!File.Exists(path))
            {
                Log.LogError($"Bundle not found: {path}");
                return null;
            }
            AssetBundle bundle = AssetBundle.LoadFromFile(path);
            Bundle = bundle;
            Shader shader = bundle != null ? bundle.LoadAsset<Shader>(ShaderAsset) : null;
            if (shader == null || !shader.isSupported)
            {
                Log.LogError($"Shader '{ShaderAsset}' missing or unsupported in {path}");
                return null;
            }
            return new Material(shader) { name = "RealPortalWindow" };
        }

        internal static void Debug(string msg)
        {
            if (Verbose.Value)
                Log.LogInfo(msg);
        }
    }
}
