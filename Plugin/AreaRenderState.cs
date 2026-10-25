using System.Collections.Generic;
using UnityEngine;

namespace RealPortal
{
    // Win.EnvGroups. The light, fog, sky and wind a window render gets for the place its camera stands in (what
    // EnvMan.SetEnv would set there, FarEnvironment.Compute) is one set of values per environment per frame. The
    // game's own values are read once, before the first window that changes any; each window then writes only what
    // differs from what is set already (nothing at all after a window onto the same weather), and the game's values
    // go back once, after the last window (End), instead of after every window.
    internal static class AreaRenderState
    {
        internal struct Values
        {
            public Quaternion LightRot;
            public float LightIntensity;
            public Color LightColor;
            public Color FogColor;
            public float FogDensity;
            public Color Ambient;
            public float CloudRain, CloudOpacity;
            public Vector4 SkyDir, SunDir;
            public Color SunFog, SunCol, AmbientGlobal;
            public float Wet, Aurora;
            public Vector4 Wind1, Wind2, WindForce;
        }

        static readonly int s_skyboxSunDir = Shader.PropertyToID("_SkyboxSunDir");
        static readonly int s_sunDir = Shader.PropertyToID("_SunDir");
        static readonly int s_sunFogColor = Shader.PropertyToID("_SunFogColor");
        static readonly int s_sunColor = Shader.PropertyToID("_SunColor");
        static readonly int s_ambientColor = Shader.PropertyToID("_AmbientColor");
        static readonly int s_wet = Shader.PropertyToID("_Wet");
        static readonly int s_aurora = Shader.PropertyToID("_AuroraStrength");
        static readonly int s_rain = Shader.PropertyToID("_Rain");
        static readonly int s_opacity = Shader.PropertyToID("_Opacity");
        static readonly int s_wind1 = Shader.PropertyToID("_GlobalWind1");
        static readonly int s_wind2 = Shader.PropertyToID("_GlobalWind2");
        static readonly int s_windForce = Shader.PropertyToID("_GlobalWindForce");

        // The game's values (read at Begin) and what is set right now.
        static Values s_game, s_cur;
        static Material s_clouds;
        static bool s_open;
        static int s_batch;

        // Something of another environment is (or may be) set: End must run before the game camera renders.
        internal static bool Open => s_open;
        internal static ref readonly Values Game => ref s_game;

        // Lab: how many values were written by difference, and how many windows changed nothing.
        internal static int LabWrites, LabSkipped;

        // Before the first window of a frame (or a probe face) that sets another environment: the game's own values.
        internal static void Begin(EnvMan em)
        {
            Light light = em.m_dirLight;
            s_game.LightRot = light.transform.rotation;
            s_game.LightIntensity = light.intensity;
            s_game.LightColor = light.color;
            s_game.FogColor = RenderSettings.fogColor;
            s_game.FogDensity = RenderSettings.fogDensity;
            s_game.Ambient = RenderSettings.ambientLight;
            s_clouds = em.m_clouds != null ? em.m_clouds.sharedMaterial : null;
            if (s_clouds != null)
            {
                s_game.CloudRain = s_clouds.GetFloat(s_rain);
                s_game.CloudOpacity = s_clouds.GetFloat(s_opacity);
            }
            s_game.SkyDir = Shader.GetGlobalVector(s_skyboxSunDir);
            s_game.SunDir = Shader.GetGlobalVector(s_sunDir);
            s_game.SunFog = Shader.GetGlobalColor(s_sunFogColor);
            s_game.SunCol = Shader.GetGlobalColor(s_sunColor);
            s_game.AmbientGlobal = Shader.GetGlobalColor(s_ambientColor);
            s_game.Wet = Shader.GetGlobalFloat(s_wet);
            s_game.Aurora = Shader.GetGlobalFloat(s_aurora);
            s_game.Wind1 = Shader.GetGlobalVector(s_wind1);
            s_game.Wind2 = Shader.GetGlobalVector(s_wind2);
            s_game.WindForce = Shader.GetGlobalVector(s_windForce);
            s_cur = s_game;
            s_open = true;
            // Snapshots of an earlier batch were made against other game values (wind, clouds).
            s_batch++;
        }

        // Sets `to`, writing only what differs from what is set now.
        internal static void Apply(EnvMan em, in Values to)
        {
            if (!s_open)
                return;
            int writes = 0;
            Light light = em != null ? em.m_dirLight : null;
            if (light != null)
            {
                if (!Eq(s_cur.LightRot, to.LightRot))
                {
                    light.transform.rotation = to.LightRot;
                    writes++;
                }
                if (s_cur.LightIntensity != to.LightIntensity)
                {
                    light.intensity = to.LightIntensity;
                    writes++;
                }
                if (!Eq(s_cur.LightColor, to.LightColor))
                {
                    light.color = to.LightColor;
                    writes++;
                }
            }
            if (!Eq(s_cur.FogColor, to.FogColor))
            {
                RenderSettings.fogColor = to.FogColor;
                writes++;
            }
            if (s_cur.FogDensity != to.FogDensity)
            {
                RenderSettings.fogDensity = to.FogDensity;
                writes++;
            }
            if (!Eq(s_cur.Ambient, to.Ambient))
            {
                RenderSettings.ambientLight = to.Ambient;
                writes++;
            }
            if (s_clouds != null)
            {
                if (s_cur.CloudRain != to.CloudRain)
                {
                    s_clouds.SetFloat(s_rain, to.CloudRain);
                    writes++;
                }
                if (s_cur.CloudOpacity != to.CloudOpacity)
                {
                    s_clouds.SetFloat(s_opacity, to.CloudOpacity);
                    writes++;
                }
            }
            if (!Eq(s_cur.SkyDir, to.SkyDir))
            {
                Shader.SetGlobalVector(s_skyboxSunDir, to.SkyDir);
                writes++;
            }
            if (!Eq(s_cur.SunDir, to.SunDir))
            {
                Shader.SetGlobalVector(s_sunDir, to.SunDir);
                writes++;
            }
            if (!Eq(s_cur.SunFog, to.SunFog))
            {
                Shader.SetGlobalColor(s_sunFogColor, to.SunFog);
                writes++;
            }
            if (!Eq(s_cur.SunCol, to.SunCol))
            {
                Shader.SetGlobalColor(s_sunColor, to.SunCol);
                writes++;
            }
            if (!Eq(s_cur.AmbientGlobal, to.AmbientGlobal))
            {
                Shader.SetGlobalColor(s_ambientColor, to.AmbientGlobal);
                writes++;
            }
            if (s_cur.Wet != to.Wet)
            {
                Shader.SetGlobalFloat(s_wet, to.Wet);
                writes++;
            }
            if (s_cur.Aurora != to.Aurora)
            {
                Shader.SetGlobalFloat(s_aurora, to.Aurora);
                writes++;
            }
            if (!Eq(s_cur.Wind1, to.Wind1))
            {
                Shader.SetGlobalVector(s_wind1, to.Wind1);
                writes++;
            }
            if (!Eq(s_cur.Wind2, to.Wind2))
            {
                Shader.SetGlobalVector(s_wind2, to.Wind2);
                writes++;
            }
            if (!Eq(s_cur.WindForce, to.WindForce))
            {
                Shader.SetGlobalVector(s_windForce, to.WindForce);
                writes++;
            }
            s_cur = to;
            LabWrites += writes;
            if (writes == 0)
                LabSkipped++;
        }

        // A window that renders with the game's own environment: back to it, the batch stays open.
        internal static void ToGame(EnvMan em)
        {
            if (s_open)
                Apply(em, s_game);
        }

        // After the last window: the game's values back, before the game camera renders.
        internal static void End(EnvMan em)
        {
            if (!s_open)
                return;
            if (em != null)
                Apply(em, s_game);
            s_open = false;
            s_clouds = null;
        }

        // ---- one set of values per environment (and wetness) per batch -------------------------------------

        sealed class Snapshot
        {
            public EnvSetup Env;
            public float Wet;
            public Values V;
        }

        static readonly List<Snapshot> s_snapshots = new List<Snapshot>();
        static int s_count, s_snapshotBatch = -1;

        internal static bool Find(EnvSetup env, float wet, out Values v)
        {
            if (s_snapshotBatch == s_batch)
                for (int i = 0; i < s_count; i++)
                {
                    Snapshot s = s_snapshots[i];
                    if (s.Env == env && s.Wet == wet)
                    {
                        v = s.V;
                        return true;
                    }
                }
            v = default;
            return false;
        }

        internal static void Keep(EnvSetup env, float wet, in Values v)
        {
            if (s_snapshotBatch != s_batch)
            {
                s_snapshotBatch = s_batch;
                s_count = 0;
            }
            if (s_count == s_snapshots.Count)
                s_snapshots.Add(new Snapshot());
            Snapshot s = s_snapshots[s_count++];
            s.Env = env;
            s.Wet = wet;
            s.V = v;
        }

        // Exact comparisons (Unity's == on these types tolerates differences a sun direction would show).
        static bool Eq(Quaternion a, Quaternion b) => a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w;
        static bool Eq(Color a, Color b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;
        static bool Eq(Vector4 a, Vector4 b) => a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w;

        internal static void Clear()
        {
            s_snapshots.Clear();
            s_count = 0;
            s_snapshotBatch = -1;
        }
    }
}
