using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace RealPortal
{
    // Far.ClaimLikeGame (Docs\PARITY-PLAN.md P3.6). The game's server gives up, every 2 s, what this game owns in the
    // player's near zones outside his active area (ZDOMan.ReleaseNearbyZDOS, run for the local session in single player
    // and on the host). For a pair 100-300 m apart that is what a far area claimed round its far portal: its creatures
    // stopped and went invisible in the window and in the base view, and the area's next listing took them again. Here
    // that pass runs the game's own loop with one change, for the local session only: an own object outside the player's
    // active area stays owned while it lies in the active area of a far area that does not drain
    // (RemoteAreas.InFarActiveArea), as it would with a player standing at that far portal. Taking over (the loop's
    // other branch) is the game's, so a real player standing there still takes those objects; every other session's pass
    // is the game's call. Off, with LiveFarSide off, or without a far area: the game's call, untouched.
    [HarmonyPatch]
    internal static class FarOwnership
    {
        // Lab: own objects this pass kept for a far area's active area (each time the game's pass would have let one go).
        internal static long LabReleaseKept;

        static AccessTools.FieldRef<ZDOMan, List<ZDO>> s_nearObjects;
        static Func<ZDOMan, Vector3, long, bool> s_inPeerArea;
        static bool s_looked, s_failed;

        [HarmonyPrefix, HarmonyPatch(typeof(ZDOMan), "ReleaseNearbyZDOS")]
        static bool ReleaseNearbyZDOS(ZDOMan __instance, Vector3 refPosition, long uid)
        {
            if (!FarFlags.ClaimLikeGame || !Plugin.Enabled.Value || !Plugin.LiveFarSide.Value || ZNet.instance == null || ZoneSystem.instance == null
                || uid != ZDOMan.GetSessionID())
                return true;
            RemoteAreas areas = PortalSystem.Instance?.Areas;
            if (areas == null || !areas.HasFarActiveArea || !Resolve())
                return true;
            try
            {
                Release(__instance, refPosition, uid, areas);
                return false;
            }
            catch (Exception e)
            {
                // The game's call from here on (it does the same for what this one already went through).
                s_failed = true;
                Plugin.Log.LogError("Far.ClaimLikeGame: the release pass failed, the game's runs from now on: " + e);
                return true;
            }
        }

        // ZDOMan.ReleaseNearbyZDOS, line for line (its own list, its own tests), but for the one test marked.
        static void Release(ZDOMan man, Vector3 refPosition, long uid, RemoteAreas areas)
        {
            Vector2s zone = ZoneSystem.GetZone(refPosition);
            SimulationDistance synced = ZNet.instance.GetSyncedSimulationDistance();
            List<ZDO> near = s_nearObjects(man);
            near.Clear();
            SimulationDistance sim = new SimulationDistance(synced.NearSimulationDistance, 0, synced.IsClassic);
            man.FindSectorObjects(zone, sim, near);
            foreach (ZDO z in near)
            {
                if (!z.Persistent)
                    continue;
                Vector3 position = z.GetPosition();
                if (z.GetOwner() == uid)
                {
                    if (!ZNetScene.InActiveArea(position, zone))
                    {
                        // The change: a far area's active area holds it, as a player standing there would.
                        if (areas.InFarActiveArea(position))
                        {
                            LabReleaseKept++;
                            continue;
                        }
                        z.SetOwner(0L);
                    }
                }
                else if ((!z.HasOwner() || !s_inPeerArea(man, position, z.GetOwner())) && ZNetScene.InActiveArea(position, zone))
                    z.SetOwner(uid);
            }
        }

        // The game's private parts the loop uses, found once; missing (another game version), the game's call runs.
        static bool Resolve()
        {
            if (!s_looked)
            {
                s_looked = true;
                try
                {
                    s_nearObjects = AccessTools.FieldRefAccess<ZDOMan, List<ZDO>>("m_tempNearObjects");
                    s_inPeerArea = AccessTools.MethodDelegate<Func<ZDOMan, Vector3, long, bool>>(AccessTools.Method(typeof(ZDOMan), "IsInPeerActiveArea"));
                }
                catch (Exception e)
                {
                    s_nearObjects = null;
                    s_inPeerArea = null;
                    Plugin.Log.LogWarning("Far.ClaimLikeGame: the game's release pass runs unchanged (" + e.Message + ")");
                }
            }
            return !s_failed && s_nearObjects != null && s_inPeerArea != null;
        }
    }

    // ZNetScene.InActiveArea(point, zone) for many points: the arithmetic of the game's PointInsideActiveArea, with the
    // synced simulation distance and the zone size read once (Of), so it gives the game's answer for a fraction of the
    // cost. The square 1.5 zones round the zone's centre (1 zone at simulation distance 1), cut to a circle of 1.75 zones at
    // distance 2 outside classic.
    internal readonly struct ActiveArea
    {
        readonly Vector3 _center;
        readonly float _half, _radius2;
        readonly bool _circle;

        ActiveArea(Vector3 center, float half, float radius2, bool circle)
        {
            _center = center;
            _half = half;
            _radius2 = radius2;
            _circle = circle;
        }

        internal static ActiveArea Of(Vector2s zone, SimulationDistance sim, float zoneSize)
        {
            float num = sim.NearSimulationDistance == 1 ? 1f : 1.5f;
            float num2 = zoneSize * 1.75f;
            return new ActiveArea(ZoneSystem.GetZonePos(zone), num * zoneSize, num2 * num2, sim.NearSimulationDistance == 2 && !sim.IsClassic);
        }

        internal bool Holds(Vector3 point)
        {
            point.y = 0f;
            if (!(Utils.ChebyshevDistance(_center, point) <= _half))
                return false;
            return !_circle || (_center - point).sqrMagnitude < _radius2;
        }
    }
}
