using System;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace RealPortal.Engine
{
    // A module is one optimization: a static class marked [EngineModule] with
    //   static void Install(Harmony harmony)
    // It registers its Feature in a static field initializer and patches through the given Harmony instance.
    // Patches stay installed for the whole session and check Feature.On, so switching is instant and A/B-able.
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class EngineModuleAttribute : Attribute
    {
    }

    [BepInPlugin(Guid, Name, Version)]
    public class EnginePlugin : BaseUnityPlugin
    {
        public const string Guid = "valheim.realportal.engine";
        public const string Name = "Real Portal Engine";
        public const string Version = "0.1.0";

        internal static ManualLogSource Log;
        internal static Harmony Harmony;

        void Awake()
        {
            Log = Logger;
            Harmony = new Harmony(Guid);
            foreach (Type t in typeof(EnginePlugin).Assembly.GetTypes().Where(t => t.GetCustomAttribute<EngineModuleAttribute>() != null).OrderBy(t => t.Name))
            {
                MethodInfo install = t.GetMethod("Install", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                try
                {
                    install?.Invoke(null, new object[] { Harmony });
                    Log.LogInfo($"module {t.Name} installed");
                }
                catch (Exception e)
                {
                    Log.LogError($"module {t.Name} failed to install, left out: {e.InnerException ?? e}");
                }
            }
            Features.BindConfig(Config);
            Log.LogInfo("features: " + string.Join(", ", Features.All.Select(f => f.ToString())));
        }
    }
}
