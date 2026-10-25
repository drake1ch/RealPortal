using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace RealPortal
{
    // Multiplayer. The game's server sends each player only the objects around them, and generates only the zones
    // around them; a far end beyond a portal would stay empty (its terrain is built from the seed, but nothing stands
    // on it). So the server does for the far ends of the portals near a player what it does for the player's own
    // surroundings: it generates those zones and sends their objects, after everything near the player, the place
    // right around the far portal first. A player's window goes live once the server says that place has arrived.
    // A player whose server runs without this mod still gets windows, with whatever their game knows over there.
    // Others see a thing that went through a portal jump with it, instead of sliding across the map.
    // Far.MpParity (Docs\PARITY-PLAN.md P26). Past 8 far ends a player got nothing, in whatever order the world's portal
    // list gave them, and after 60 s those windows went live on bare terrain; the far objects came after everything the
    // game sends, so they updated in jerks; and the objects of a peer who went elsewhere stood still for ever (nobody
    // took them over). With the switch:
    //  - A player's far ends are those he watches first (a client tells its server which, RpcWatched, as they change), then
    //    the nearest, up to Distance.MultiplayerFarEnds (32).
    //  - What changed at a far end he watches is sent with the game's own priority for his surroundings: merged into the
    //    game's sorted list by the game's own measure (distance, here from the far portal, less the time since it was
    //    last sent), instead of after it; the objects the game forces to the front of the list stay first.
    //  - Far ends inside his own active area count too (his edge areas, RemoteAreas' Far.DupEdge): the sectors of their far
    //    active area outside his own come only this way.
    //  - Client: the core counts as arrived once every object the server counted came, or once its count held still for a
    //    second at nine in ten of them (CoreArrived); no window goes live without it (RemoteAreas.Arrived: past 60 s it
    //    keeps the vanilla look and waits).
    //  - The server's 2 s release (ZDOMan.ReleaseNearbyZDOS) hands a player the persistent objects of his far ends' active
    //    areas whose owner is in neither his own active area nor his far ends', nor where a real player stands (whose own
    //    pass takes them, as in the game): stale owners are handed over (TakeStale). A player only at the far ends he
    //    watches, and only once his game said it simulates far sides (RpcWatched; Peer.Simulates).
    // Off: the first 8 far ends, the core at nine in ten less 3, live after 60 s regardless, far objects after the game's,
    // stale owners kept. A dedicated server registers the switch on its own (WindowFeatures.RegisterServer).
    internal static class PortalServer
    {
        const string RpcCore = "RealPortal_Core";
        const string RpcCoreCount = "RealPortal_CoreCount";
        const string RpcWatched = "RealPortal_Watched";
        static readonly int s_cross = "rp_cross".GetStableHashCode();

        // ---- the game's private parts ----------------------------------------------------------------

        static readonly Type s_peerType = AccessTools.Inner(typeof(ZDOMan), "ZDOPeer");
        static readonly Func<object, ZNetPeer> s_netPeer = PeerGetter();
        static readonly Func<object, ZDO, bool> s_shouldSend = ShouldSendCall();
        static readonly Func<ZoneSystem, Vector3, bool> s_ghostZones =
            AccessTools.MethodDelegate<Func<ZoneSystem, Vector3, bool>>(AccessTools.Method(typeof(ZoneSystem), "CreateGhostZones"));
        static readonly Func<ZoneSystem, Vector2s, bool> s_zoneGenerated =
            AccessTools.MethodDelegate<Func<ZoneSystem, Vector2s, bool>>(AccessTools.Method(typeof(ZoneSystem), "IsZoneGenerated", new[] { typeof(Vector2s) }));
        static readonly System.Reflection.FieldInfo s_portalObjects = AccessTools.Field(typeof(ZDOMan), "m_portalObjects");

        static Func<object, ZNetPeer> PeerGetter()
        {
            var dm = new DynamicMethod("RealPortal_ZDOPeerNet", typeof(ZNetPeer), new[] { typeof(object) }, typeof(PortalServer).Module, true);
            ILGenerator il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, s_peerType);
            il.Emit(OpCodes.Ldfld, AccessTools.Field(s_peerType, "m_peer"));
            il.Emit(OpCodes.Ret);
            return (Func<object, ZNetPeer>)dm.CreateDelegate(typeof(Func<object, ZNetPeer>));
        }

        static Func<object, ZDO, bool> ShouldSendCall()
        {
            var dm = new DynamicMethod("RealPortal_ZDOPeerShouldSend", typeof(bool), new[] { typeof(object), typeof(ZDO) }, typeof(PortalServer).Module, true);
            ILGenerator il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, s_peerType);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, AccessTools.Method(s_peerType, "ShouldSend"));
            il.Emit(OpCodes.Ret);
            return (Func<object, ZDO, bool>)dm.CreateDelegate(typeof(Func<object, ZDO, bool>));
        }

        // Far.MpParity: when the peer was last sent this object (ZDOPeer.m_zdos[id].m_syncTime; -1: never), and the game's
        // order for its send list (ZDOMan.ServerSendCompare). Made on first use; missing (another game version), watched far
        // ends are sent after the game's list as before.
        static Func<object, ZDOID, float> s_syncTime;
        static IComparer<ZDO> s_sendOrder;
        static bool s_sendLooked;

        static bool SendAccess()
        {
            if (s_sendLooked)
                return s_syncTime != null && s_sendOrder != null;
            s_sendLooked = true;
            try
            {
                Type info = AccessTools.Inner(s_peerType, "PeerZDOInfo") ?? throw new MissingMemberException("ZDOPeer.PeerZDOInfo");
                System.Reflection.FieldInfo zdos = AccessTools.Field(s_peerType, "m_zdos") ?? throw new MissingFieldException("ZDOPeer", "m_zdos");
                System.Reflection.FieldInfo synced = AccessTools.Field(info, "m_syncTime") ?? throw new MissingFieldException("PeerZDOInfo", "m_syncTime");
                System.Reflection.MethodInfo tryGet = AccessTools.Method(zdos.FieldType, "TryGetValue") ?? throw new MissingMethodException("m_zdos", "TryGetValue");
                var dm = new DynamicMethod("RealPortal_ZDOPeerSyncTime", typeof(float), new[] { typeof(object), typeof(ZDOID) }, typeof(PortalServer).Module, true);
                ILGenerator il = dm.GetILGenerator();
                LocalBuilder value = il.DeclareLocal(info);
                Label none = il.DefineLabel();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Castclass, s_peerType);
                il.Emit(OpCodes.Ldfld, zdos);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldloca_S, value);
                il.Emit(OpCodes.Callvirt, tryGet);
                il.Emit(OpCodes.Brfalse_S, none);
                il.Emit(OpCodes.Ldloc, value);
                il.Emit(OpCodes.Ldfld, synced);
                il.Emit(OpCodes.Ret);
                il.MarkLabel(none);
                il.Emit(OpCodes.Ldc_R4, -1f);
                il.Emit(OpCodes.Ret);
                s_syncTime = (Func<object, ZDOID, float>)dm.CreateDelegate(typeof(Func<object, ZDOID, float>));
                s_sendOrder = new SendOrder(AccessTools.MethodDelegate<Comparison<ZDO>>(
                    AccessTools.Method(typeof(ZDOMan), "ServerSendCompare") ?? throw new MissingMethodException("ZDOMan", "ServerSendCompare")));
            }
            catch (Exception e)
            {
                s_syncTime = null;
                s_sendOrder = null;
                Plugin.Log.LogWarning("Far.MpParity: watched far ends are sent after the game's own objects (" + e.Message + ")");
            }
            return s_syncTime != null && s_sendOrder != null;
        }

        sealed class SendOrder : IComparer<ZDO>
        {
            readonly Comparison<ZDO> _compare;
            internal SendOrder(Comparison<ZDO> compare) => _compare = compare;
            public int Compare(ZDO x, ZDO y) => _compare(x, y);
        }

        // Far.MpParity: at most this many far ends a player (Distance.MultiplayerFarEnds; 8 without the switch).
        static BepInEx.Configuration.ConfigEntry<int> s_maxEnds;

        internal static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(ZDOMan), "CreateSyncList"), postfix: new HarmonyMethod(typeof(PortalServer), nameof(SyncListPostfix)));
            harmony.Patch(AccessTools.Method(s_peerType, "ZDOSectorInvalidated"), prefix: new HarmonyMethod(typeof(PortalServer), nameof(InvalidatedPrefix)));
            harmony.Patch(AccessTools.Method(typeof(ZoneSystem), "Update"), postfix: new HarmonyMethod(typeof(PortalServer), nameof(ZoneUpdatePostfix)));
            harmony.Patch(AccessTools.Method(typeof(ZSyncTransform), "ClientSync"), postfix: new HarmonyMethod(typeof(PortalServer), nameof(ClientSyncPostfix)));
            // Far.MpParity: where the game's own sorted part of a send list ends, how many forced objects the game put in
            // front of it, and the stale owners' hand-over.
            harmony.Patch(AccessTools.Method(typeof(ZDOMan), "ServerSortSendZDOS"), postfix: new HarmonyMethod(typeof(PortalServer), nameof(SortPostfix)));
            harmony.Patch(AccessTools.Method(typeof(ZDOMan), "AddForceSendZdos"), prefix: new HarmonyMethod(typeof(PortalServer), nameof(ForcePrefix)),
                postfix: new HarmonyMethod(typeof(PortalServer), nameof(ForcePostfix)));
            harmony.Patch(AccessTools.Method(typeof(ZDOMan), "ReleaseNearbyZDOS"), postfix: new HarmonyMethod(typeof(PortalServer), nameof(ReleasePostfix)));
            s_maxEnds = Plugin.Enabled.ConfigFile.Bind("Distance", "MultiplayerFarEnds", 32,
                new BepInEx.Configuration.ConfigDescription("Multiplayer, with a server running Real Portal and the engine switch Far.MpParity: at most this many far ends a " +
                                                            "player gets objects for, the ones he watches first, then the nearest (8 without the switch).",
                    new BepInEx.Configuration.AcceptableValueRange<int>(1, 64)));
            // A dedicated server registers no window switches (Plugin.Awake stops before WindowFeatures.Register): those its
            // far-end service reads are registered alone there.
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                WindowFeatures.RegisterServer();
        }

        // ---- server: what each player sees beyond the portals near them ---------------------------------

        sealed class Peer
        {
            public float At = -10f;
            public float UsedAt;
            public readonly List<Vector2s> Zones = new List<Vector2s>();
            public readonly List<ZDO> Objects = new List<ZDO>();
            // Far.MpParity: per far end (as Zones), its far portal's position, whether the player watches it, and where its
            // objects begin in Objects; and the far ends the player last said he watches (RpcWatched).
            public readonly List<Vector3> Centers = new List<Vector3>();
            public readonly List<bool> EndWatched = new List<bool>();
            public readonly List<int> Starts = new List<int>();
            public readonly List<Vector2s> Watched = new List<Vector2s>();
            // Far.MpParity: his game said it simulates far sides (RpcWatched: Real Portal on, Creatures.LiveFarSide on). Only
            // then are stale objects at his far ends handed to him, and do his far ends count as holding what he owns there:
            // a player without the mod, or with it off, never makes the objects he would be given.
            public bool Simulates;
        }

        // Far.MpParity: a far end a player could get (Refresh), put in order: watched ones first, then the nearest portal.
        struct End
        {
            internal Vector2s Zone;
            internal Vector3 Center;
            internal float D;
            internal bool Watched;
        }

        sealed class EndOrder : IComparer<End>
        {
            internal static readonly EndOrder Instance = new EndOrder();
            public int Compare(End x, End y) => x.Watched != y.Watched ? (x.Watched ? -1 : 1) : x.D.CompareTo(y.D);
        }

        static readonly List<End> s_ends = new List<End>();

        static readonly Dictionary<long, Peer> s_peers = new Dictionary<long, Peer>();
        static readonly List<ZDO> s_portals = new List<ZDO>();
        static float s_portalsAt = -10f;
        static readonly List<ZDO> s_near = new List<ZDO>(), s_distant = new List<ZDO>();
        static readonly HashSet<ZDOID> s_inList = new HashSet<ZDOID>();

        // Lab and log: far objects added to players' sync lists.
        internal static int LabAdded;

        static bool Serving => Plugin.Enabled.Value && ZNet.instance != null && ZNet.instance.IsServer() && ZDOMan.instance != null && ZoneSystem.instance != null;

        // Every portal of the world (the game's own list by sector), read again every 2 s.
        static List<ZDO> Portals()
        {
            if (Time.time - s_portalsAt < 2f)
                return s_portals;
            s_portalsAt = Time.time;
            s_portals.Clear();
            if (s_portalObjects?.GetValue(ZDOMan.instance) is IDictionary bySector)
                foreach (object list in bySector.Values)
                    if (list is List<ZDO> zdos)
                        foreach (ZDO z in zdos)
                            if (z != null && z.IsValid())
                                s_portals.Add(z);
            return s_portals;
        }

        // A player's far ends (the far portals of the pairs within PreloadDistance of them, beyond their own area; with
        // Far.MpParity inside it too) and those places' objects, the place right around each far portal first, then its near zones, then the
        // ring of big distant objects. Once a second.
        static Peer Refresh(ZNetPeer net)
        {
            if (!s_peers.TryGetValue(net.m_uid, out Peer p))
                s_peers[net.m_uid] = p = new Peer();
            p.UsedAt = Time.time;
            if (Time.time - p.At < 1f)
                return p;
            p.At = Time.time;
            p.Zones.Clear();
            p.Objects.Clear();
            p.Centers.Clear();
            p.EndWatched.Clear();
            p.Starts.Clear();
            Vector3 me = net.GetRefPos();
            float preload = Plugin.PreloadDistance.Value;
            // Far.MpParity: every far end in reach first, then the watched ones and the nearest up to the cap.
            bool parity = FarFlags.MpParity;
            s_ends.Clear();
            foreach (ZDO portal in Portals())
            {
                float d = Utils.DistanceXZ(me, portal.GetPosition());
                if (d > preload)
                    continue;
                ZDO far = PortalLink.PartnerOf(portal);
                // Far.MpParity: a far end inside his own active area too (his edge area lists what lies outside it, which
                // only this sends him).
                if (far == null || !far.IsValid() || PortalLink.PartnerOf(far) != portal || (!parity && ZNetScene.InActiveArea(far.GetPosition(), me)))
                    continue;
                Vector2s zone = ZoneSystem.GetZone(far.GetPosition());
                if (parity)
                    s_ends.Add(new End { Zone = zone, Center = far.GetPosition(), D = d, Watched = p.Watched.Contains(zone) });
                else if (!p.Zones.Contains(zone) && p.Zones.Count < 8)
                {
                    p.Zones.Add(zone);
                    p.Centers.Add(far.GetPosition());
                    p.EndWatched.Add(false);
                }
            }
            if (parity)
            {
                s_ends.Sort(EndOrder.Instance);
                int cap = s_maxEnds != null ? s_maxEnds.Value : 32;
                foreach (End e in s_ends)
                    if (p.Zones.Count < cap && !p.Zones.Contains(e.Zone))
                    {
                        p.Zones.Add(e.Zone);
                        p.Centers.Add(e.Center);
                        p.EndWatched.Add(e.Watched);
                    }
                s_ends.Clear();
            }
            SimulationDistance sim = RemoteAreas.AreaDistance();
            foreach (Vector2s zone in p.Zones)
            {
                s_near.Clear();
                s_distant.Clear();
                ZDOMan.instance.FindSectorObjects(zone, sim, s_near, s_distant);
                Vector3 c = ZoneSystem.GetZonePos(zone);
                int from = p.Objects.Count;
                p.Starts.Add(from);
                foreach (ZDO z in s_near)
                {
                    Vector2s s = z.GetSector();
                    bool core = Math.Abs(s.x - zone.x) <= 1 && Math.Abs(s.y - zone.y) <= 1;
                    z.m_tempSortValue = Utils.DistanceSqr(c, z.GetPosition()) + (core ? 0f : 1e12f);
                    p.Objects.Add(z);
                }
                foreach (ZDO z in s_distant)
                {
                    z.m_tempSortValue = Utils.DistanceSqr(c, z.GetPosition()) + 2e12f;
                    p.Objects.Add(z);
                }
                p.Objects.Sort(from, p.Objects.Count - from, SortValue.Instance);
            }
            return p;
        }

        sealed class SortValue : IComparer<ZDO>
        {
            internal static readonly SortValue Instance = new SortValue();
            public int Compare(ZDO x, ZDO y) => x.m_tempSortValue.CompareTo(y.m_tempSortValue);
        }

        // After the game's own list (what is around the player, nearest first): what changed over there, as long as
        // the send has room for it (the game sends from the top of the list until its package is full).
        static void SyncListPostfix(object peer, List<ZDO> toSync)
        {
            if (!Serving || peer == null)
                return;
            ZNetPeer net = s_netPeer(peer);
            if (net == null)
                return;
            Peer p = Refresh(net);
            // Far.MpParity: the forced objects the game put in front of the list (ForcePostfix), and where its sorted part
            // after them ends (SortPostfix), both in this call.
            int forced = ReferenceEquals(s_forcedFor, peer) ? Math.Max(0, Math.Min(s_forcedCount, toSync.Count)) : 0;
            s_forcedFor = null;
            int sorted = ReferenceEquals(s_sortedFor, peer) ? Math.Min(s_sortedCount, toSync.Count - forced) : -1;
            s_sortedFor = null;
            if (p.Objects.Count == 0)
                return;
            s_inList.Clear();
            foreach (ZDO z in toSync)
                s_inList.Add(z.m_uid);
            if (FarFlags.MpParity && sorted >= 0)
                MergeWatched(peer, net, p, toSync, forced, sorted);
            int added = 0;
            foreach (ZDO z in p.Objects)
            {
                if (added >= 2000)
                    break;
                if (z.IsValid() && !s_inList.Contains(z.m_uid) && s_shouldSend(peer, z))
                {
                    toSync.Add(z);
                    added++;
                }
            }
            LabAdded += added;
        }

        // Far.MpParity: the peer whose send list the game sorted last, and how much of it (its own surroundings, nearest and
        // longest unsent first; the rest of the list comes after).
        static object s_sortedFor;
        static int s_sortedCount;
        // Changed objects of watched far ends to merge, and the merged list.
        static readonly List<ZDO> s_watchedSend = new List<ZDO>(), s_merged = new List<ZDO>();
        const int WatchedPerList = 2000;

        // Lab: changed objects of watched far ends merged into the game's part of a send list.
        internal static long LabWatchedMerged;

        static void SortPostfix(List<ZDO> objects, object peer)
        {
            s_sortedFor = peer;
            s_sortedCount = objects != null ? objects.Count : 0;
        }

        // Far.MpParity: the peer whose send list the game last put its forced objects in (container opens, portal connections;
        // ZDOMan.AddForceSendZdos inserts each at the front, after the sort), and how many.
        static object s_forcedFor;
        static int s_forcedCount;

        static void ForcePrefix(List<ZDO> syncList, out int __state) => __state = syncList != null ? syncList.Count : 0;

        static void ForcePostfix(object peer, List<ZDO> syncList, int __state)
        {
            if (!FarFlags.MpParity || syncList == null || !Serving)
                return;
            s_forcedFor = peer;
            s_forcedCount = syncList.Count - __state;
        }

        // What changed at the far ends the player watches goes into the game's sorted part of his send list by the game's own
        // measure (ServerSortSendZDOS: distance, here from that far portal as from a player standing there, less 1.5 m for
        // every second since it was last sent, at most 100; the game's own objects measured again the same way), so it is
        // sent with his surroundings' priority instead of after them. The forced objects the game put in front of that part
        // (`forced` of them) stay first, as the game sends them; the rest of the list (the game's distant objects) stays
        // behind as the game made it.
        static void MergeWatched(object peer, ZNetPeer net, Peer p, List<ZDO> toSync, int forced, int sorted)
        {
            bool any = false;
            for (int e = 0; e < p.EndWatched.Count && !any; e++)
                any = p.EndWatched[e];
            if (!any || !SendAccess())
                return;
            float now = Time.time;
            s_watchedSend.Clear();
            for (int e = 0; e < p.Zones.Count && e < p.Starts.Count; e++)
            {
                if (!p.EndWatched[e])
                    continue;
                int to = e + 1 < p.Starts.Count ? p.Starts[e + 1] : p.Objects.Count;
                Vector3 c = p.Centers[e];
                for (int i = p.Starts[e]; i < to && s_watchedSend.Count < WatchedPerList; i++)
                {
                    ZDO z = p.Objects[i];
                    if (!z.IsValid() || s_inList.Contains(z.m_uid) || !s_shouldSend(peer, z))
                        continue;
                    z.m_tempSortValue = Vector3.Distance(z.GetPosition(), c) - Unsent(peer, z, now) * 1.5f;
                    s_watchedSend.Add(z);
                    s_inList.Add(z.m_uid);
                }
            }
            if (s_watchedSend.Count == 0)
                return;
            Vector3 refPos = net.GetRefPos();
            int end = forced + sorted;
            for (int i = forced; i < end; i++)
            {
                ZDO z = toSync[i];
                z.m_tempSortValue = Vector3.Distance(z.GetPosition(), refPos) - Unsent(peer, z, now) * 1.5f;
            }
            s_watchedSend.Sort(s_sendOrder);
            s_merged.Clear();
            for (int i = 0; i < forced; i++)
                s_merged.Add(toSync[i]);
            int a = forced, b = 0;
            while (a < end || b < s_watchedSend.Count)
            {
                if (b >= s_watchedSend.Count || (a < end && s_sendOrder.Compare(toSync[a], s_watchedSend[b]) <= 0))
                    s_merged.Add(toSync[a++]);
                else
                    s_merged.Add(s_watchedSend[b++]);
            }
            for (int i = end; i < toSync.Count; i++)
                s_merged.Add(toSync[i]);
            toSync.Clear();
            for (int i = 0; i < s_merged.Count; i++)
                toSync.Add(s_merged[i]);
            LabWatchedMerged += s_watchedSend.Count;
            s_merged.Clear();
            s_watchedSend.Clear();
        }

        // Seconds since the object was last sent to the peer, at most 100 (100: never), as ServerSortSendZDOS counts them.
        static float Unsent(object peer, ZDO z, float now)
        {
            float at = s_syncTime(peer, z.m_uid);
            return at < 0f ? 100f : Mathf.Clamp(now - at, 0f, 100f);
        }

        // The game tells a player to forget an object that moved out of their area; one moving about a far end
        // they look at stays theirs.
        static bool InvalidatedPrefix(object __instance, ZDO zdo)
        {
            if (!Serving || zdo == null)
                return true;
            ZNetPeer net = s_netPeer(__instance);
            if (net == null || !s_peers.TryGetValue(net.m_uid, out Peer p))
                return true;
            Vector2s at = ZoneSystem.GetZone(zdo.GetPosition());
            int r = RemoteAreas.AreaDistance().TotalSimulationDistance;
            foreach (Vector2s zone in p.Zones)
                if (Math.Abs(at.x - zone.x) <= r && Math.Abs(at.y - zone.y) <= r)
                    return false;
            return true;
        }

        static float s_ghostAt, s_reportAt;
        static int s_reported;

        // The server generates the zones of far ends nobody has been to yet (with their creatures, trees and
        // stones), one at a time as it does around players. Also: the RPCs, once per connection.
        static void ZoneUpdatePostfix(ZoneSystem __instance)
        {
            Register();
            if (!Serving || !__instance.LocationsGenerated || Time.time < s_ghostAt)
                return;
            s_ghostAt = Time.time + 0.1f;
            if (Time.time >= s_reportAt)
            {
                s_reportAt = Time.time + 10f;
                if (LabAdded != s_reported)
                {
                    int ends = 0;
                    foreach (Peer p in s_peers.Values)
                        ends += p.Zones.Count;
                    Plugin.Log.LogInfo($"Far ends for players: {LabAdded - s_reported} objects queued to send in 10 s ({s_peers.Count} players, {ends} far ends)");
                    s_reported = LabAdded;
                }
            }
            List<long> gone = null;
            foreach (var kv in s_peers)
            {
                if (Time.time - kv.Value.UsedAt > 30f)
                {
                    (gone = gone ?? new List<long>()).Add(kv.Key);
                    continue;
                }
                foreach (Vector2s zone in kv.Value.Zones)
                    if (s_ghostZones(__instance, ZoneSystem.GetZonePos(zone)))
                        return;
            }
            if (gone != null)
                foreach (long id in gone)
                    s_peers.Remove(id);
        }

        // ---- the place around a far portal: has it arrived? -------------------------------------------

        static ZRoutedRpc s_registered;
        static readonly Dictionary<Vector2s, int> s_coreCount = new Dictionary<Vector2s, int>();
        static readonly Dictionary<Vector2s, float> s_askedAt = new Dictionary<Vector2s, float>();
        static float s_firstAsk = -1f;
        static bool s_answered, s_noServerMod;

        static void Register()
        {
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (rpc == null || rpc == s_registered)
                return;
            s_registered = rpc;
            s_peers.Clear();
            s_coreCount.Clear();
            s_askedAt.Clear();
            s_firstAsk = -1f;
            s_answered = s_noServerMod = false;
            // Far.MpParity: nothing held or told of the last session.
            s_held.Clear();
            s_watchSent.Clear();
            s_watchAt = 0f;
            s_watchTold = false;
            rpc.Register<Vector3>(RpcCore, RPC_Core);
            rpc.Register<Vector3, int>(RpcCoreCount, RPC_CoreCount);
            rpc.Register<ZPackage>(RpcWatched, RPC_Watched);
        }

        static SimulationDistance Core => new SimulationDistance(1, 0, true);

        // Server: how many objects stand in the zones around that place (-1 while one is not generated yet).
        static void RPC_Core(long sender, Vector3 at)
        {
            if (!Serving)
                return;
            Vector2s zone = ZoneSystem.GetZone(at);
            int count = 0;
            for (int y = -1; y <= 1 && count >= 0; y++)
                for (int x = -1; x <= 1; x++)
                    if (!s_zoneGenerated(ZoneSystem.instance, new Vector2s(zone.x + x, zone.y + y)))
                    {
                        count = -1;
                        break;
                    }
            if (count == 0)
            {
                s_near.Clear();
                ZDOMan.instance.FindSectorObjects(zone, Core, s_near);
                count = s_near.Count;
            }
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcCoreCount, ZoneSystem.GetZonePos(zone), count);
        }

        static void RPC_CoreCount(long sender, Vector3 at, int count)
        {
            s_answered = true;
            s_coreCount[ZoneSystem.GetZone(at)] = count;
        }

        // Client: the far ends' objects come from a server running this mod.
        internal static bool FromServer => ZNet.instance != null && !ZNet.instance.IsServer() && !s_noServerMod;

        // Client: the objects of the place around the far portal in this zone have come from the server (nine in
        // ten of them: some move in and out). Always so on the server itself, and with a server without this mod
        // (no answer within 10 s).
        internal static bool CoreArrived(Vector2s zone)
        {
            if (ZNet.instance == null || ZNet.instance.IsServer() || s_noServerMod || ZRoutedRpc.instance == null)
                return true;
            float now = Time.time;
            if (!s_askedAt.TryGetValue(zone, out float asked) || now - asked > 2f)
            {
                s_askedAt[zone] = now;
                if (s_firstAsk < 0f)
                    s_firstAsk = now;
                ZRoutedRpc.instance.InvokeRoutedRPC(RpcCore, ZoneSystem.GetZonePos(zone));
            }
            if (!s_answered)
            {
                if (now - s_firstAsk < 10f)
                    return false;
                s_noServerMod = true;
                Plugin.Log.LogInfo("The server does not run Real Portal: far ends show only what this game knows over there.");
                return true;
            }
            if (!s_coreCount.TryGetValue(zone, out int want) || want < 0)
                return false;
            s_near.Clear();
            ZDOMan.instance.FindSectorObjects(zone, Core, s_near);
            int have = s_near.Count;
            if (!FarFlags.MpParity)
                return have >= want * 0.9f - 3;
            // Far.MpParity (P26): every object the server counted is here, or the count held still for a second at no less
            // than that (some move in and out): not the moment it passes nine in ten, while more is still on its way.
            if (!s_held.TryGetValue(zone, out Held held) || held.Count != have)
                s_held[zone] = held = new Held { Count = have, Since = now };
            return have >= want || (now - held.Since >= StableSeconds && have >= want * 0.9f - 3);
        }

        // Far.MpParity: per far core, its object count here and since when it holds.
        struct Held
        {
            internal int Count;
            internal float Since;
        }

        const float StableSeconds = 1f;
        static readonly Dictionary<Vector2s, Held> s_held = new Dictionary<Vector2s, Held>();

        // ---- Far.MpParity: which far ends a player watches ----------------------------------------------------------------

        // Client: the zones of the far ends whose window is watched or about to show, as last told (and being gathered);
        // whether anything was told yet this session, and whether this game simulates far sides as last told.
        static List<Vector2s> s_watchSent = new List<Vector2s>(), s_watchNext = new List<Vector2s>();
        static float s_watchAt;
        static bool s_watchTold, s_simTold;

        // Lab: watch lists sent (client) and taken (server).
        internal static int LabWatchSent, LabWatchTaken;

        // Client, once a frame (RemoteAreas.Update): once a second, the far ends watched now, told to a server running this
        // mod when they changed, and once as soon as the server answered; with them whether this game simulates far sides
        // (Real Portal and Creatures.LiveFarSide on), which the server hands stale objects by.
        internal static void ClientTick()
        {
            ZNet net = ZNet.instance;
            if (!FarFlags.MpParity || net == null || net.IsServer() || !s_answered || s_noServerMod || ZRoutedRpc.instance == null)
                return;
            float now = Time.time;
            if (now < s_watchAt)
                return;
            s_watchAt = now + 1f;
            s_watchNext.Clear();
            foreach (PortalLink link in PortalLink.All)
            {
                ZDO far = link.PartnerZdo;
                if (far == null || link.Vanilla || !(link.Watched || WindowReach.AboutToShow(link)))
                    continue;
                Vector2s zone = ZoneSystem.GetZone(far.GetPosition());
                if (!s_watchNext.Contains(zone))
                    s_watchNext.Add(zone);
            }
            bool sim = Plugin.Enabled.Value && Plugin.LiveFarSide.Value;
            bool same = s_watchTold && sim == s_simTold && s_watchNext.Count == s_watchSent.Count;
            for (int i = 0; i < s_watchNext.Count && same; i++)
                same = s_watchSent.Contains(s_watchNext[i]);
            if (same)
                return;
            List<Vector2s> was = s_watchSent;
            s_watchSent = s_watchNext;
            s_watchNext = was;
            var pkg = new ZPackage();
            pkg.Write(s_watchSent.Count);
            foreach (Vector2s zone in s_watchSent)
                pkg.Write(zone);
            // After the list (a server from before it reads none of it).
            pkg.Write(sim);
            s_watchTold = true;
            s_simTold = sim;
            ZRoutedRpc.instance.InvokeRoutedRPC(RpcWatched, pkg);
            LabWatchSent++;
        }

        // Server: a player's watched far ends (kept with the far ends Refresh gives him; read at its next refresh), and whether
        // his game simulates far sides (a client from before that flag sent a list only while it did).
        static void RPC_Watched(long sender, ZPackage pkg)
        {
            if (!Serving || pkg == null)
                return;
            if (!s_peers.TryGetValue(sender, out Peer p))
                s_peers[sender] = p = new Peer { UsedAt = Time.time };
            p.Watched.Clear();
            int n = Math.Min(pkg.ReadInt(), 256);
            for (int i = 0; i < n; i++)
                p.Watched.Add(pkg.ReadVector2s());
            p.Simulates = pkg.GetPos() < pkg.Size() ? pkg.ReadBool() : true;
            LabWatchTaken++;
        }

        // ---- Far.MpParity: stale owners at far ends ------------------------------------------------------------------------

        static readonly List<ZDO> s_stale = new List<ZDO>();

        // Lab: objects handed over from a stale owner.
        internal static long LabStaleTaken;

        // After the game's 2 s release for a player (or the host itself): the persistent objects of his far ends' active
        // areas whose owner is in neither its own active area nor its far ends', and where no real player stands, are his.
        static void ReleasePostfix(Vector3 refPosition, long uid)
        {
            if (!FarFlags.MpParity || !Serving || ZNet.instance.GetPeers().Count == 0)
                return;
            SimulationDistance sim = ZNet.instance.GetSyncedSimulationDistance();
            float size = ZoneSystem.instance.m_zoneSize;
            if (uid == ZDOMan.GetSessionID())
            {
                RemoteAreas areas = PortalSystem.Instance?.Areas;
                if (areas == null || !Plugin.LiveFarSide.Value)
                    return;
                for (int i = 0; i < areas.Count; i++)
                    if (!areas.AreaDraining(i))
                        TakeStale(areas.AreaZone(i), uid, sim, size);
            }
            // A player only when his game said it simulates far sides, and only at the far ends he watches (whose areas his
            // game surely made): one without the mod, or with it off, never makes what he would be given, so it would stand
            // still for everyone, his own pass keeping it and OwnerHolds keeping it his.
            else if (s_peers.TryGetValue(uid, out Peer p) && p.Simulates)
                for (int e = 0; e < p.Zones.Count && e < p.EndWatched.Count; e++)
                    if (p.EndWatched[e])
                        TakeStale(p.Zones[e], uid, sim, size);
        }

        static void TakeStale(Vector2s zone, long uid, SimulationDistance sim, float size)
        {
            ActiveArea area = ActiveArea.Of(zone, sim, size);
            s_stale.Clear();
            ZDOMan.instance.FindSectorObjects(zone, Core, s_stale);
            foreach (ZDO z in s_stale)
            {
                if (!z.Persistent || !z.HasOwner())
                    continue;
                long owner = z.GetOwner();
                if (owner == uid)
                    continue;
                Vector3 at = z.GetPosition();
                if (!area.Holds(at) || OwnerHolds(owner, at, sim, size) || PlayerStands(at))
                    continue;
                z.SetOwner(uid);
                LabStaleTaken++;
            }
            s_stale.Clear();
        }

        // The owner has the object in its own active area (the game's test, ZDOMan.IsInPeerActiveArea), or in one of its far
        // ends' (the host: its far areas; a player: those this server sends him).
        static bool OwnerHolds(long owner, Vector3 at, SimulationDistance sim, float size)
        {
            if (owner == ZDOMan.GetSessionID())
            {
                if (ZNetScene.InActiveArea(at, ZNet.instance.GetReferencePosition()))
                    return true;
                RemoteAreas areas = PortalSystem.Instance?.Areas;
                return areas != null && areas.InFarActiveArea(at);
            }
            ZNetPeer peer = ZNet.instance.GetPeer(owner);
            if (peer == null)
                return false;
            if (ZNetScene.InActiveArea(at, peer.GetRefPos()))
                return true;
            // His far ends hold it only when his game simulates them (Peer.Simulates).
            if (!s_peers.TryGetValue(owner, out Peer p) || !p.Simulates)
                return false;
            foreach (Vector2s zone in p.Zones)
                if (ActiveArea.Of(zone, sim, size).Holds(at))
                    return true;
            return false;
        }

        // A real player stands there (the host's own player, or any peer): his own pass decides, as in the game.
        static bool PlayerStands(Vector3 at)
        {
            ZNet net = ZNet.instance;
            if (!net.IsDedicated() && ZNetScene.InActiveArea(at, net.GetReferencePosition()))
                return true;
            foreach (ZNetPeer peer in net.GetPeers())
                if (ZNetScene.InActiveArea(at, peer.GetRefPos()))
                    return true;
            return false;
        }

        // Lab: what the server said about the place around a far portal, and what this game has of it.
        internal static string LabDescribe(Vector2s zone)
        {
            if (ZNet.instance == null || ZNet.instance.IsServer())
                return "server: here";
            s_near.Clear();
            ZDOMan.instance?.FindSectorObjects(zone, Core, s_near);
            return $"server says {(s_coreCount.TryGetValue(zone, out int n) ? n.ToString() : s_noServerMod ? "nothing (no mod)" : "-")}, here {s_near.Count}";
        }

        // ---- the far portal itself ---------------------------------------------------------------------

        static readonly Dictionary<ZDOID, float> s_requested = new Dictionary<ZDOID, float>();

        // Client: a portal's partner this game has not heard of yet is asked for (every 10 s at most).
        internal static void AskForPartner(ZDO portal)
        {
            if (portal == null || ZNet.instance == null || ZNet.instance.IsServer() || ZDOMan.instance == null || ZRoutedRpc.instance == null)
                return;
            ZDOID id = portal.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal);
            if (id == ZDOID.None || ZDOMan.instance.GetZDO(id) != null)
                return;
            if (s_requested.TryGetValue(id, out float at) && Time.time - at < 10f)
                return;
            if (s_requested.Count > 256)
                s_requested.Clear();
            s_requested[id] = Time.time;
            ZDOMan.instance.RequestZDO(id);
        }

        // ---- going through, as others see it --------------------------------------------------------

        // Whoever moves a thing through a portal counts it in its ZDO.
        internal static void Crossed(ZDO zdo)
        {
            if (zdo != null && zdo.IsOwner())
                zdo.Set(s_cross, zdo.GetInt(s_cross) + 1);
        }

        static readonly Dictionary<int, int> s_seenCross = new Dictionary<int, int>();

        // Others' games move a thing to where the ZDO says gradually (and turn it slowly); one that went through a
        // portal is put there at once, turned as it came out.
        static void ClientSyncPostfix(ZSyncTransform __instance, ZNetView ___m_nview, Rigidbody ___m_body)
        {
            ZDO zdo = ___m_nview != null ? ___m_nview.GetZDO() : null;
            if (zdo == null || zdo.IsOwner())
                return;
            int n = zdo.GetInt(s_cross);
            if (n == 0)
                return;
            int id = __instance.GetInstanceID();
            if (!s_seenCross.TryGetValue(id, out int seen))
            {
                if (s_seenCross.Count > 4096)
                    s_seenCross.Clear();
                s_seenCross[id] = n;
                return;
            }
            if (seen == n)
                return;
            s_seenCross[id] = n;
            Transform t = __instance.transform;
            t.SetPositionAndRotation(zdo.GetPosition(), zdo.GetRotation());
            if (___m_body != null)
            {
                ___m_body.position = t.position;
                ___m_body.rotation = t.rotation;
            }
        }
    }
}
