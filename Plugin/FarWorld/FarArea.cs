using System.Collections.Generic;
using UnityEngine;

namespace RealPortal
{
    // Area tiers (ARCHITECTURE-v2.md §2.1, §3.4). V2 keeps every far area real, so AreaPolicy (legacy semantics) names what
    // RemoteAreas or the game holds rather than choosing it: Cold (T0) nothing, Data (T1) wanted and its objects coming in,
    // Visual (T2, from V3), RealCore (T3a) real, LiveCore (T3b) real and simulated by this game.
    internal enum AreaTier : byte
    {
        Cold,
        Data,
        Visual,
        RealCore,
        LiveCore,
    }

    // How far a far side is ready (ARCHITECTURE-v2.md §2.3): its window shows, it takes what is thrown at it (dropped items,
    // creatures, projectiles), you may walk in. Each step includes the ones before it.
    internal enum ReadyLevel : byte
    {
        Closed,
        Show,
        Throw,
        Walk,
    }

    // One far area (ARCHITECTURE-v2.md §3.2; FARWORLD-V3.md §3.1): one per far portal's ZDOID, so every near portal that
    // leads to the same far portal shares it (Path of Valheiman's gates inscribed to one portal). Made by AreaPolicy for
    // every real link within the preload distance, and kept while one of its links is live or it was wanted within the
    // unload delay (as RemoteAreas keeps its areas). Own-area exclusion: a far portal inside the player's active area
    // (OwnArea, a short-range pair) is the game's to load, and Far.NoDupAreas gives it no area of RemoteAreas; sectors of
    // any area within the player's area belong to the game as well (InPlayerArea; the registry marks them PlayerOwned).
    // Areas whose far portals stand in the same or adjacent zones share one AreaContext.
    internal sealed class FarArea
    {
        internal readonly ZDOID Portal;
        // Made in this order (AreaContext ids, logs).
        internal readonly int Serial;
        internal readonly float MadeAt;
        // The far portal's ZDO (validated by Portal), where it stands and its zone.
        internal ZDO Zdo;
        internal Vector3 Center;
        internal Vector2s Zone;
        // The near portals leading here this frame (live or not), the nearest of them to the player and its distance (XZ).
        internal readonly List<PortalLink> Links = new List<PortalLink>(2);
        internal PortalLink Nearest;
        internal float Distance = float.MaxValue;
        // Some link of it is live this frame (PortalLink.IsActive).
        internal bool AnyLive;
        internal float WantedAt;
        // Anything crossed one of its links (either way): the player, a creature, an item, a projectile.
        internal float CrossedAt = -100f;
        // The far portal stands in the player's active area (the game loads it); RemoteAreas' stage of its area (0 none or
        // draining, 1 loading, 2 the place around the portal stands, 3 all of it).
        internal bool OwnArea;
        internal int Stage;
        internal AreaTier Tier;
        // AreaPolicy's score (§3.4; lab only in V2), the closing speed it uses (m/s, smoothed) and when it was last scored.
        internal float Score, Closing;
        internal float ScoredAt = -1f, ScoredDistance = -1f;
        internal AreaContext Context;
        internal readonly VirtualEnd End = new VirtualEnd();

        // Readiness (Readiness.cs): the level reached (latched while a link stays live), when each step came, the next
        // check, and what the last check found: core zones not loaded, core objects not created (all of them, and within
        // ThrowRadius of the far portal), the server's word on the core (multiplayer), the zones around P' not loaded.
        internal ReadyLevel Ready;
        internal float ShowAt = -1f, ThrowAt = -1f, WalkAt = -1f, SettledAt = -1f, NextCheck;
        internal int ZonesMissing, CoreMissing, ThrowMissing, PZonesMissing = -1;
        internal bool Arrived;
        // Opened on time rather than complete (Readiness.CreateTimeout), or on the crossing that left it (Readiness.Crossed).
        internal bool TimedOut, ByCrossing;
        internal bool Dropped;

        internal FarArea(ZDO far, int serial, float now)
        {
            Portal = far.m_uid;
            Serial = serial;
            MadeAt = now;
            WantedAt = now;
            Zdo = far;
            Center = far.GetPosition();
            Zone = ZoneSystem.GetZone(Center);
        }

        // The far portal's own PortalLink while it is loaded (any link's partner: they all lead to it).
        internal PortalLink Partner
        {
            get
            {
                for (int i = 0; i < Links.Count; i++)
                    if (Links[i] != null && Links[i].Partner != null)
                        return Links[i].Partner;
                return null;
            }
        }

        // The role a sector has for this area (§3.2), the way ZDOMan.FindSectorObjects lists around its zone with sim: Core
        // (Chebyshev <= 1), Near (its near zones), Ring (its distant ring, Distant objects only), else None.
        internal SectorRole RoleOf(Vector2s sector, SimulationDistance sim)
        {
            int cheb = Mathf.Max(Mathf.Abs(sector.x - Zone.x), Mathf.Abs(sector.y - Zone.y));
            if (cheb <= 1)
                return SectorRole.Core;
            ZoneSystem zs = ZoneSystem.instance;
            if (zs == null)
                return SectorRole.None;
            if (cheb <= sim.NearSimulationDistance && (sim.IsClassic || zs.ZonesWithinRadius(Zone, sector, sim.NearSimulationDistance)))
                return SectorRole.Near;
            if (cheb <= sim.TotalSimulationDistance && (sim.IsClassic || zs.ZonesWithinRadius(Zone, sector, sim.TotalSimulationDistance, true)))
                return SectorRole.Ring;
            return SectorRole.None;
        }

        // A sector inside the player's active area (ZNetScene.InActiveArea of its centre): the game owns it, no far area.
        internal static bool InPlayerArea(Vector2s sector)
        {
            ZNet net = ZNet.instance;
            return net != null && ZoneSystem.instance != null && ZNetScene.InActiveArea(ZoneSystem.GetZonePos(sector), net.GetReferencePosition());
        }

        // A sector the game itself lists whole and creates around the player (its near zones, ZNetScene.CreateDestroyObjects):
        // what the game fills of a far area that has none of RemoteAreas (Readiness waits only for that).
        internal static bool GameLoads(Vector2s sector)
        {
            ZNet net = ZNet.instance;
            ZoneSystem zs = ZoneSystem.instance;
            if (net == null || zs == null)
                return false;
            Vector2s me = ZoneSystem.GetZone(net.GetReferencePosition());
            SimulationDistance sim = net.GetSyncedSimulationDistance();
            int cheb = Mathf.Max(Mathf.Abs(sector.x - me.x), Mathf.Abs(sector.y - me.y));
            return cheb <= sim.NearSimulationDistance && (cheb == 0 || sim.IsClassic || zs.ZonesWithinRadius(me, sector, sim.NearSimulationDistance));
        }

        internal string Describe()
        {
            string links = Links.Count == 1 ? "1 link" : Links.Count + " links";
            return $"far portal {Portal} at {Center:F0} (zone {Zone.x},{Zone.y}): {links}, nearest {(Distance < float.MaxValue ? Distance.ToString("0") + " m" : "-")}, " +
                   $"{Tier}{(OwnArea ? ", in the player's area" : "")}, stage {Stage}, score {Score:0.0}, context {(Context != null ? Context.Id : 0)}, {Ready}" +
                   (Ready > ReadyLevel.Closed && Ready < ReadyLevel.Walk
                       ? $" (core zones missing {ZonesMissing}, objects missing {CoreMissing}, within {Readiness.ThrowRadius:0} m {ThrowMissing}, arrived {Arrived})" : "") +
                   (TimedOut ? ", opened on time" : "") + (ByCrossing ? ", by a crossing" : "") +
                   (PZonesMissing >= 0 ? $", zones around P' missing {PZonesMissing}" : "") +
                   (End.Known ? End.Real ? ", end real" : ", end virtual" : ", end unknown");
        }
    }

    // Every far area, by its far portal's ZDOID (AreaPolicy makes and drops them). Main thread only; nothing here allocates
    // after an area is made.
    internal static class FarAreas
    {
        static readonly Dictionary<ZDOID, FarArea> s_byPortal = new Dictionary<ZDOID, FarArea>();
        internal static readonly List<FarArea> All = new List<FarArea>();
        static int s_serial;

        // Moves when an area is made or dropped; ContextsDirty also when one's far portal changed zone.
        internal static int Version;
        internal static bool ContextsDirty;

        // Lab: areas made and dropped so far.
        internal static int LabMade, LabDropped;

        internal static int Count => All.Count;

        internal static FarArea Get(ZDOID farPortal) => s_byPortal.TryGetValue(farPortal, out FarArea a) ? a : null;

        // The area of a link's far portal (null before AreaPolicy has seen the link, or with Far.V2 off).
        internal static FarArea Of(PortalLink link)
        {
            FarArea a = link != null ? link.FarArea : null;
            return a != null && !a.Dropped ? a : null;
        }

        internal static FarArea Make(ZDO far, float now)
        {
            var a = new FarArea(far, ++s_serial, now);
            s_byPortal[a.Portal] = a;
            All.Add(a);
            Version++;
            ContextsDirty = true;
            LabMade++;
            return a;
        }

        internal static void Drop(int index)
        {
            FarArea a = All[index];
            a.Dropped = true;
            a.End.Reset();
            for (int i = 0; i < a.Links.Count; i++)
                if (a.Links[i] != null && a.Links[i].FarArea == a)
                    a.Links[i].FarArea = null;
            a.Links.Clear();
            s_byPortal.Remove(a.Portal);
            All[index] = All[All.Count - 1];
            All.RemoveAt(All.Count - 1);
            Version++;
            ContextsDirty = true;
            LabDropped++;
        }

        // Everything goes (Far.V2 off, the scene gone, an error): links fall back to their own IsActive.
        internal static void Clear()
        {
            foreach (FarArea a in All)
            {
                a.Dropped = true;
                a.End.Reset();
            }
            foreach (PortalLink link in PortalLink.All)
                link.FarArea = null;
            All.Clear();
            s_byPortal.Clear();
            AreaContext.Clear();
            Version++;
            ContextsDirty = false;
        }
    }
}
