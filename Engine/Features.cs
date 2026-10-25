using System;
using System.Collections.Generic;
using BepInEx.Configuration;

namespace RealPortal
{
    // Runtime switches for every engine optimization. A patch reads its Feature.On (a plain field) on the hot path;
    // the lab flips them with "feat <Name> on|off" (Features.Set, found by reflection) to measure A/B in one session,
    // and the player can turn any of them off in the config (section "Engine").
    public static class Features
    {
        static readonly Dictionary<string, Feature> s_byName = new Dictionary<string, Feature>(StringComparer.OrdinalIgnoreCase);
        static ConfigFile s_config;

        public static event Action<Feature> Changed;

        public static IEnumerable<Feature> All => s_byName.Values;
        // The engine's config the features are bound to (null until EnginePlugin.Awake): for bookkeeping that must be
        // reset together with them (Real Portal's Window.Quality marker).
        public static ConfigFile Config => s_config;

        public static Feature Register(string name, bool defaultOn, string description)
        {
            if (s_byName.TryGetValue(name, out Feature existing))
                return existing;
            var f = new Feature(name, defaultOn, description);
            s_byName.Add(name, f);
            if (s_config != null)
                f.Bind(s_config);
            return f;
        }

        public static bool Get(string name) => s_byName.TryGetValue(name, out Feature f) && f.On;

        // Returns false when there is no such feature.
        public static bool Set(string name, bool on)
        {
            if (!s_byName.TryGetValue(name, out Feature f))
                return false;
            f.Switch(on);
            return true;
        }

        internal static void BindConfig(ConfigFile config)
        {
            s_config = config;
            foreach (Feature f in s_byName.Values)
                f.Bind(config);
        }

        internal static void Raise(Feature f) => Changed?.Invoke(f);
    }

    public sealed class Feature
    {
        public readonly string Name;
        public readonly string Description;
        // Read directly on hot paths.
        public bool On;
        ConfigEntry<bool> _entry;

        // Called with the new state whenever the feature is switched (config or lab), on the main thread.
        public event Action<bool> Switched;

        internal Feature(string name, bool on, string description)
        {
            Name = name;
            On = on;
            Description = description;
        }

        internal void Bind(ConfigFile config)
        {
            if (_entry != null)
                return;
            _entry = config.Bind("Engine", Name, On, Description);
            Switch(_entry.Value);
            _entry.SettingChanged += (_, __) => Switch(_entry.Value);
        }

        internal void Switch(bool on)
        {
            if (_entry != null && _entry.Value != on)
                _entry.Value = on;   // raises SettingChanged, which comes back here with the same value
            if (On == on)
                return;
            On = on;
            Switched?.Invoke(on);
            Features.Raise(this);
        }

        public override string ToString() => $"{Name}={(On ? "on" : "off")}";
    }
}
