using System.Collections.Generic;
using UnityEngine;

namespace RealPortal
{
    // Readiness (ARCHITECTURE-v2.md §2.3, §3.13; FARWORLD-V3.md §2.3; Far.Readiness3): when a far side shows, takes what is
    // thrown at it, and lets you walk in, as three steps of its FarArea instead of the one IsActive.
    //  - CanShow: a link goes live as before (RemoteAreas.CanGoLive: the place around the far portal stands, or the far
    //    portal is in the player's own area), with its far end known. V2 keeps objects real, so "core proxies built" is the
    //    core real, as today (about 2 s on a visited area); the zones around P' are only counted (PZonesMissing), since
    //    waiting for them would hold windows back against V0. The window shows from here.
    //  - CanThrow: the far portal's 9 core zones are loaded (with their colliders), every object of the core within
    //    ThrowRadius (32 m) of the far portal is created, and in multiplayer the server said the core has arrived (the
    //    existing 90% rule, PortalServer.CoreArrived). Creatures, dropped items and projectiles cross from here; until then
    //    items bounce off the wall, creatures stop in the opening, projectiles go on on this side, and the far side is not
    //    seen, heard or reached into by creatures (FarLife).
    //  - CanWalk: CanThrow, and every object of the core created. The wall opens for the player (with the existing
    //    PlayerMayPass rules on top); until then it stays shut and says nothing (the swirl shows that someone is stopped).
    // Only what will be made is waited for: objects whose prefab the game has (as ZNetScene.IsAreaReady), dungeon interiors
    // only where Far.DeferInteriors does not leave them to the game, and for a far portal that has no area of RemoteAreas
    // (in the player's own area under Far.NoDupAreas) only the zones the game itself loads around the player. Should
    // something still never come, CanWalk opens CreateTimeout after the zones stood and the server answered, the server's
    // answer is waited for at most ArriveTimeout (as RemoteAreas.CoreLoaded), and nothing longer than both after the window
    // showed; each such opening is logged and counted (LabTimeouts).
    // Levels latch while a link of the area stays live (as a link's own _live does): an object appearing in the core
    // later (a creature walking in) does not shut the wall again. They drop back to Closed when no link is live.
    // A check reads the 3x3 core sectors (ZDOMan.FindSectorObjects into one reused list, about 0.01-0.05 ms) at most every
    // CheckEvery per area, only between CanShow and CanWalk; once walkable an area costs nothing.
    // Off (Far.V2 or Far.Readiness3): CanThrow and CanWalk are true, and every gate is the link's IsActive as before. Switched
    // on while links are live, their areas stay walkable (latched); those going live afterwards take the three steps.
    internal static class Readiness
    {
        internal const float ThrowRadius = 32f;
        const float CheckEvery = 0.2f;
        internal const float CreateTimeout = 10f;
        internal const float ArriveTimeout = 60f;
        // Dungeon interiors stand this high (RemoteAreas.InteriorHeight).
        const float InteriorHeight = 3000f;

        static readonly List<ZDO> s_core = new List<ZDO>();
        static readonly SimulationDistance s_coreSim = new SimulationDistance(1, 0, true);
        // Which of the 3x3 core zones are waited for, by (dy + 1) * 3 + (dx + 1).
        static readonly bool[] s_need = new bool[9];

        // Lab: areas that became showable, throwable, walkable; opened on time; levels handed over by a crossing; checks;
        // the longest wait from CanShow to CanWalk (s); refusals: player crossings, creatures, items, projectiles.
        internal static int LabShows, LabThrows, LabWalks, LabTimeouts, LabCrossings, LabChecks;
        internal static float LabShowToWalkMax;
        internal static int LabPlayerHeld, LabCreaturesHeld, LabItemsHeld, LabProjectilesHeld;
        internal static double LabCheckMsMax;

        // The gates as PortalSystem and FarLife test them, beside the link's own IsActive (callers test both).
        internal static bool CanThrow(PortalLink link) => !FarWorld.Readiness3On || Level(link) >= ReadyLevel.Throw;
        internal static bool CanWalk(PortalLink link) => !FarWorld.Readiness3On || Level(link) >= ReadyLevel.Walk;

        // The level of a link's far side. A link AreaPolicy has not seen yet (its first frame, or Far.Readiness3 just
        // switched on) is as before: everything with IsActive.
        internal static ReadyLevel Level(PortalLink link)
        {
            if (link == null)
                return ReadyLevel.Closed;
            FarArea a = FarAreas.Of(link);
            if (a == null || !FarWorld.Readiness3On)
                return link.IsActive ? ReadyLevel.Walk : ReadyLevel.Closed;
            return a.Ready;
        }

        // Once a frame per area (AreaPolicy, after the links' Refresh): the level moved on as far as the far side allows.
        internal static void Update(FarArea a, RemoteAreas areas, float now)
        {
            if (!a.AnyLive)
            {
                if (a.Ready != ReadyLevel.Closed)
                    Close(a);
                return;
            }
            // Off: walkable with its live link, as before. Switched on meanwhile, what is open stays open (latched, as a
            // link's _live), so no wall shuts on a far side already walked into; areas that go live later take the steps.
            if (!FarWorld.Readiness3On)
            {
                if (a.Ready != ReadyLevel.Walk)
                {
                    a.Ready = ReadyLevel.Walk;
                    a.ShowAt = a.ThrowAt = a.WalkAt = now;
                }
                return;
            }
            if (a.Ready == ReadyLevel.Closed)
            {
                a.Ready = ReadyLevel.Show;
                a.ShowAt = now;
                a.ThrowAt = a.WalkAt = a.SettledAt = -1f;
                a.NextCheck = 0f;
                a.TimedOut = false;
                LabShows++;
            }
            if (a.Ready == ReadyLevel.Walk || now < a.NextCheck)
                return;
            a.NextCheck = now + CheckEvery;
            Check(a, areas, now);
            if (a.Ready < ReadyLevel.Throw && a.ZonesMissing == 0 && a.Arrived && a.ThrowMissing == 0)
            {
                a.Ready = ReadyLevel.Throw;
                a.ThrowAt = now;
                LabThrows++;
            }
            if (a.Ready == ReadyLevel.Throw && a.CoreMissing == 0)
                Walkable(a, now);
            if (a.Ready == ReadyLevel.Walk)
                return;
            // Should something never come: once the zones stand and the server has answered, CreateTimeout more; and in any
            // case once ArriveTimeout + CreateTimeout passed since the window showed (a zone that never loads).
            bool settled = a.ZonesMissing == 0 && a.Arrived;
            if (!settled)
                a.SettledAt = -1f;
            else if (a.SettledAt < 0f)
                a.SettledAt = now;
            if ((settled && now - a.SettledAt > CreateTimeout) || now - a.ShowAt > ArriveTimeout + CreateTimeout)
            {
                a.TimedOut = true;
                LabTimeouts++;
                Plugin.Log.LogInfo($"Readiness: far portal {a.Portal} opened after {now - a.ShowAt:0.0} s with {a.ZonesMissing} core zone(s) not loaded, " +
                                   $"{a.CoreMissing} core object(s) not created ({a.ThrowMissing} within {ThrowRadius:0} m), arrived {a.Arrived}");
                if (a.ThrowAt < 0f)
                    a.ThrowAt = now;
                Walkable(a, now);
            }
        }

        static void Walkable(FarArea a, float now)
        {
            a.Ready = ReadyLevel.Walk;
            a.WalkAt = now;
            LabWalks++;
            float waited = now - a.ShowAt;
            if (waited > LabShowToWalkMax)
                LabShowToWalkMax = waited;
            if (waited > 0.5f)
                Plugin.Debug($"Readiness: far portal {a.Portal} walkable {waited:0.0} s after its window showed (throwable after {a.ThrowAt - a.ShowAt:0.0} s)");
        }

        static void Close(FarArea a)
        {
            a.Ready = ReadyLevel.Closed;
            a.ShowAt = a.ThrowAt = a.WalkAt = a.SettledAt = -1f;
            a.TimedOut = false;
            a.ByCrossing = false;
            a.PZonesMissing = -1;
        }

        // The far side's core as it stands now: its zones, its objects, the server's word.
        static void Check(FarArea a, RemoteAreas areas, float now)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            LabChecks++;
            ZoneSystem zs = ZoneSystem.instance;
            ZNetScene scene = ZNetScene.instance;
            ZDO far = a.Zdo;
            if (zs == null || scene == null || ZDOMan.instance == null || far == null || !far.IsValid())
            {
                a.ZonesMissing = 9;
                a.Arrived = false;
                return;
            }
            // RemoteAreas loads all nine of an area it holds; for a far portal it holds no area of, the game loads what lies
            // in its own near zones and nothing else.
            bool remote = areas != null && areas.Has(far);
            Vector2s zone = a.Zone;
            int zonesMissing = 0;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    var z = new Vector2s(zone.x + dx, zone.y + dy);
                    bool need = remote || FarArea.GameLoads(z);
                    s_need[(dy + 1) * 3 + dx + 1] = need;
                    if (need && !zs.IsZoneLoaded(z))
                        zonesMissing++;
                }
            a.ZonesMissing = zonesMissing;
            a.Arrived = PortalServer.CoreArrived(zone) || now - a.ShowAt > ArriveTimeout;
            int coreMissing = 0, throwMissing = 0;
            if (zonesMissing == 0)
            {
                Vector3 c = far.GetPosition();
                bool skipInteriors = FarFlags.DeferInteriors && remote && c.y < InteriorHeight;
                s_core.Clear();
                ZDOMan.instance.FindSectorObjects(zone, s_coreSim, s_core);
                for (int i = 0; i < s_core.Count; i++)
                {
                    ZDO z = s_core[i];
                    if (z == null || z.Created)
                        continue;
                    int prefab = z.GetPrefab();
                    if (prefab == 0 || !scene.HasPrefab(prefab))
                        continue;
                    Vector3 at = z.GetPosition();
                    if (skipInteriors && at.y > InteriorHeight)
                        continue;
                    Vector2s s = z.GetSector();
                    int dx = s.x - zone.x, dy = s.y - zone.y;
                    if (dx < -1 || dx > 1 || dy < -1 || dy > 1 || !s_need[(dy + 1) * 3 + dx + 1])
                        continue;
                    coreMissing++;
                    if (Utils.DistanceXZ(at, c) <= ThrowRadius)
                        throwMissing++;
                }
                s_core.Clear();
            }
            else
                coreMissing = throwMissing = -1;
            a.CoreMissing = coreMissing;
            a.ThrowMissing = throwMissing;
            CountPZones(a, zs);
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (ms > LabCheckMsMax)
                LabCheckMsMax = ms;
        }

        // Lab: of the 3x3 zones around P' (the player carried through the nearest link), how many are not loaded.
        static void CountPZones(FarArea a, ZoneSystem zs)
        {
            Player p = Player.m_localPlayer;
            PortalLink link = a.Nearest;
            PortalFrame exit = link != null ? link.FarFrame : null;
            if (p == null || exit == null)
            {
                a.PZonesMissing = -1;
                return;
            }
            Vector3 there = PortalMath.Pair(link.Frame, exit).MultiplyPoint3x4(p.transform.position);
            Vector2s pz = ZoneSystem.GetZone(there);
            int missing = 0;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                    if (!zs.IsZoneLoaded(new Vector2s(pz.x + dx, pz.y + dy)))
                        missing++;
            a.PZonesMissing = missing;
        }

        // The player walked from one side to the other (FarWorld.OnCrossed): the side just left is the player's own area,
        // real and created, so the link back from where the player now stands is walkable at once (the look-back window
        // is CanShow on the crossing frame, ARCHITECTURE-v2.md §3.13).
        internal static void Crossed(FarArea back, float now)
        {
            if (back == null || back.Ready == ReadyLevel.Walk)
                return;
            if (back.Ready == ReadyLevel.Closed)
            {
                back.ShowAt = now;
                LabShows++;
            }
            back.ThrowAt = back.ThrowAt < 0f ? now : back.ThrowAt;
            back.Ready = ReadyLevel.Walk;
            back.WalkAt = now;
            back.ByCrossing = true;
            LabCrossings++;
        }

        internal static string Describe() =>
            $"readiness: {LabShows} shown, {LabThrows} throwable, {LabWalks} walkable ({LabCrossings} by crossing, {LabTimeouts} on time), " +
            $"longest wait to walk {LabShowToWalkMax:0.0} s, {LabChecks} checks (slowest {LabCheckMsMax:0.000} ms); held: player {LabPlayerHeld}, " +
            $"creatures {LabCreaturesHeld}, items {LabItemsHeld}, projectiles {LabProjectilesHeld}";
    }
}
