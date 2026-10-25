using System;
using System.Collections.Generic;
using BepInEx.Configuration;

namespace RealPortal.Api
{
    // For mods whose own mechanics meet real portals (Path of Valheiman). Everything else Real Portal does is set
    // only in its config, so a player can keep portals mechanically vanilla.
    //
    // Mechanics another mod may force on or off for as long as it wants (a tree node taken or removed):
    //
    //   Mechanics.Force(Mechanics.FriendlyCross, true, "pathofvalheiman:beast_portal");
    //   Mechanics.Release(Mechanics.FriendlyCross, "pathofvalheiman:beast_portal");   // back to the config
    //
    // The latest force decides while it lasts; releasing it brings back the one before, then the config.
    //
    // A player stepping through a real portal (vanilla portals teleport through TeleportWorld.Teleport, which real
    // portals never call): CanPlayerCross may refuse or allow it (ore a mod lets through), PlayerCrossed follows
    // (a fee taken). A mod without a reference to RealPortal.dll can reach these by reflection (type
    // "RealPortal.Api.Mechanics, RealPortal"); the plugin GUID is "valheim.realportal".
    public static class Mechanics
    {
        public const string ApiVersion = "1.0";

        // Friendly creatures (tamed animals and summoned ones, i.e. tamed or of the players' faction) walk through real
        // portals, following you or on their own.
        public const string FriendlyCross = "friendly.cross";

        public static IReadOnlyList<string> All { get; } = new[] { FriendlyCross };

        // A mechanic changed (forced, released, or its config entry edited): its id and whether it is on now.
        public static event Action<string, bool> Changed;

        // Asked whether a player may step through a real portal: the player, the portal stepped into, the one
        // stepped out of, and whether Real Portal would let them (it refuses non-teleportable items like vanilla).
        // Return true or false to decide, null to leave it. The last listener with an answer decides. It is asked
        // often (every physics step while the player is near a portal: the portal's wall and swirl follow it), so it
        // must be quick and change nothing.
        public static event Func<Player, TeleportWorld, TeleportWorld, bool, bool?> CanPlayerCross;

        // A player has stepped through a real portal: the player, the portal stepped into, the one stepped out of.
        public static event Action<Player, TeleportWorld, TeleportWorld> PlayerCrossed;

        static readonly Dictionary<string, List<(string owner, bool on)>> s_forced = new Dictionary<string, List<(string, bool)>>();
        static readonly Dictionary<string, ConfigEntry<bool>> s_config = new Dictionary<string, ConfigEntry<bool>>();

        // Whether a mechanic is on now.
        public static bool IsOn(string mechanic)
        {
            if (mechanic != null && s_forced.TryGetValue(mechanic, out var list) && list.Count > 0)
                return list[list.Count - 1].on;
            return mechanic != null && s_config.TryGetValue(mechanic, out ConfigEntry<bool> entry) && entry.Value;
        }

        // Whether some mod forces it now, and which.
        public static bool IsForced(string mechanic, out string owner)
        {
            owner = null;
            if (mechanic == null || !s_forced.TryGetValue(mechanic, out var list) || list.Count == 0)
                return false;
            owner = list[list.Count - 1].owner;
            return true;
        }

        // Forces a mechanic on or off on behalf of owner (any string naming the mod and what forces it). False for a
        // mechanic that cannot be forced.
        public static bool Force(string mechanic, bool on, string owner)
        {
            if (mechanic == null || !s_config.ContainsKey(mechanic))
                return false;
            owner = owner ?? "";
            bool was = IsOn(mechanic);
            if (!s_forced.TryGetValue(mechanic, out var list))
                s_forced[mechanic] = list = new List<(string, bool)>();
            list.RemoveAll(x => x.owner == owner);
            list.Add((owner, on));
            Plugin.Log?.LogInfo($"[API] {mechanic} forced {(on ? "on" : "off")} by {owner}");
            Notify(mechanic, was);
            return true;
        }

        // Ends owner's force of a mechanic.
        public static void Release(string mechanic, string owner)
        {
            if (mechanic == null || !s_forced.TryGetValue(mechanic, out var list))
                return;
            bool was = IsOn(mechanic);
            if (list.RemoveAll(x => x.owner == (owner ?? "")) > 0)
                Plugin.Log?.LogInfo($"[API] {mechanic} no longer forced by {owner}");
            Notify(mechanic, was);
        }

        // Ends every force owner holds.
        public static void ReleaseAll(string owner)
        {
            foreach (string m in All)
                Release(m, owner);
        }

        internal static void Bind(string mechanic, ConfigEntry<bool> entry)
        {
            s_config[mechanic] = entry;
            entry.SettingChanged += (_, __) =>
            {
                if (!IsForced(mechanic, out string _))
                    Raise(mechanic, entry.Value);
            };
        }

        internal static bool AskCross(Player p, TeleportWorld from, TeleportWorld to, bool allowed)
        {
            if (CanPlayerCross == null)
                return allowed;
            bool result = allowed;
            foreach (Func<Player, TeleportWorld, TeleportWorld, bool, bool?> ask in CanPlayerCross.GetInvocationList())
                try
                {
                    bool? answer = ask(p, from, to, allowed);
                    if (answer.HasValue)
                        result = answer.Value;
                }
                catch (Exception e)
                {
                    Plugin.Log?.LogWarning("[API] a CanPlayerCross listener threw: " + e.Message);
                }
            return result;
        }

        internal static void Crossed(Player p, TeleportWorld from, TeleportWorld to)
        {
            try
            {
                PlayerCrossed?.Invoke(p, from, to);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[API] a PlayerCrossed listener threw: " + e.Message);
            }
        }

        static void Notify(string mechanic, bool was)
        {
            bool now = IsOn(mechanic);
            if (now != was)
                Raise(mechanic, now);
        }

        static void Raise(string mechanic, bool on)
        {
            try
            {
                Changed?.Invoke(mechanic, on);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[API] a Changed listener threw: " + e.Message);
            }
        }
    }
}
