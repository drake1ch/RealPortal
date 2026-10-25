using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using RealPortal.Engine;
using UnityEngine;

namespace RealPortal
{
    // The roles a sector has for the areas that reach it (ARCHITECTURE-v2.md §3.2), as a union over them.
    [Flags]
    internal enum SectorRole : byte
    {
        None = 0,
        // Chebyshev distance <= 1 from an area's far portal zone.
        Core = 1,
        // In an area's near zones (what ZDOMan.FindSectorObjects lists whole around its zone).
        Near = 2,
        // In an area's distant ring (Distant ZDOs only).
        Ring = 4,
    }

    // What one far area (or anything else that wants far content) reaches: the sectors ZDOMan.FindSectorObjects lists
    // around Zone with Sim, in its order and without repeats: Near (its near zones, the centre first; Core or Near) and
    // Ring (its distant ring, whose Distant ZDOs only it lists). Made by SectorRegistry.Acquire and held until Release.
    internal sealed class AreaSectors
    {
        internal readonly SectorRegistry Registry;
        internal readonly Vector2s Zone;
        internal readonly SimulationDistance Sim;
        // Whoever asked (a far area, the lab): for logs only.
        internal readonly object Owner;
        // Sectors (ZoneSystem.SectorIndex.Sector values) and, beside each, its record's slot in the registry.
        internal readonly uint[] Near, Ring;
        internal readonly SectorRole[] NearRoles;
        internal readonly int[] NearRecs, RingRecs;
        // Registered in its registry: false after Release, and when the registry went (Far.V2 or Far.Registry switched
        // off, the scene gone): the holder asks again (FarWorld.Registry).
        internal bool Live;

        internal AreaSectors(SectorRegistry registry, Vector2s zone, SimulationDistance sim, object owner, List<uint> near, List<SectorRole> nearRoles, List<uint> ring)
        {
            Registry = registry;
            Zone = zone;
            Sim = sim;
            Owner = owner;
            Near = near.ToArray();
            NearRoles = nearRoles.ToArray();
            Ring = ring.ToArray();
            NearRecs = new int[Near.Length];
            RingRecs = new int[Ring.Length];
        }

        internal int SectorCount => Near.Length + Ring.Length;

        public override string ToString() => $"area {Zone.x},{Zone.y} ({Near.Length} near, {Ring.Length} ring sectors{(Live ? "" : ", released")})";
    }

    // One registered sector: its references by role, and its rows while the journal follows it.
    internal sealed class SectorRec
    {
        internal uint Sector;
        internal Vector2s Zone;
        internal int Slot = -1;
        internal bool InUse;
        internal int RefCore, RefNear, RefRing;
        // Its rows are the game's list of it as of the journal's head (else it is read from the game's tables).
        internal bool Built;
        // Waiting for a build (QueueToken tells a stale queue entry from the live one).
        internal bool Queued;
        internal int QueueToken;
        // Inside the player's own active area (Chebyshev <= 1 from the reference zone): vanilla owns it.
        internal bool PlayerOwned;
        // Its rows (indices into the registry's row table) and how many are used.
        internal int[] Rows = Array.Empty<int>();
        internal int Count;
        // The sum of its rows' DataRevision (the audit compares it with the game's).
        internal ulong RevSum;
        // MemberRev moves when what it lists changes (a row in or out, a Distant flag flipped, built or dropped, roles);
        // Rev with every event about it, data writes included.
        internal uint MemberRev, Rev;

        internal int Refs => RefCore + RefNear + RefRing;
        // All of its ZDOs are kept (some area has it Core or Near); otherwise only its Distant ones (ring).
        internal bool Full => RefCore + RefNear > 0;
        internal SectorRole Roles =>
            (RefCore > 0 ? SectorRole.Core : 0) | (RefNear > 0 ? SectorRole.Near : 0) | (RefRing > 0 ? SectorRole.Ring : 0);
        // Sector 0 is also where the game files every ZDO outside the zone grid (InvalidateSector): never built, always
        // read from the game's tables.
        internal bool Buildable => Sector != 0 && Sector < ZdoJournal.SectorCount;
    }

    // What a SectorRegistry tells the one who mirrors its rows (FarWorld's proxies, FarProxies; Far.Proxies): a row in or
    // out, a data change (its revision moved; a Distant flag flipped comes with it), its Created bit, a sector built from
    // the game's tables or its rows dropped (before they are freed, so they can still be read), a sector gone or its roles
    // changed, the Created bits read again, the registry gone. Called on the main thread from inside the registry's own
    // calls (a journal drain may come from any query): a listener reads rows through the accessors and never calls back
    // into the registry's lists, and keeps its own errors (an exception here would stop the registry's frame).
    internal interface ISectorRowListener
    {
        void RowAdded(SectorRec rec, int row);
        void RowRemoved(SectorRec rec, int row);
        void RowChanged(SectorRec rec, int row);
        void RowCreated(int row, bool created);
        void SectorBuilt(SectorRec rec);
        void SectorUnbuilt(SectorRec rec);
        void SectorDropped(SectorRec rec);
        void RolesChanged(SectorRec rec);
        void CreatedResynced();
        void RegistryDisposed();
    }

    // FarWorld's SectorRegistry (ARCHITECTURE-v2.md §3.3, §3.2; FARWORLD-V3.md §3.1): the far content of every sector some
    // far area reaches, kept per sector with reference counts by role, so areas that overlap (four near portals into
    // one place, a short-range pair) share one record per sector and list it once. Objects are still real (V2): the
    // registry only knows which ZDOs a sector holds as ZDOMan.FindSectorObjects sees it (its sector list and its portal
    // list), which of them are created, and hands out the lists RemoteAreas made by relisting.
    //
    // Followed through the journal (Far.Journal, ZdoEvents): a sector's rows are built once from the game's tables, then
    // changed by the journal's events only, in O(changes): a row in for Created/MovedIn, out for MovedOut/Removed/
    // Destroyed, its revision for Changed. Each row stores the full ZDOID beside the ZDO (a pooled ZDO handed out again
    // under another id is not ours: the row is stale and the sector built again) and is found in O(1) through the
    // engine's ZDO.rp_row (ZdoJournal.SetRow; rows are hints, validated by id). A sector some area has Core or Near
    // holds every ZDO; one in rings only holds its Distant ones (a Distant flag flipped comes with a Changed: SetDistant,
    // SetPrefab after a new ZDO's flags, a peer's Deserialize). A build waits until the journal is drained to its head
    // and then reads the game's tables of that moment, so nothing before it is applied twice and nothing after it is
    // missed; builds run within RebuildBudgetMs a frame, core sectors first. Until a sector is built (new, after a Reset
    // or a recovery, after an audit miss), and whenever the journal is not followed (Far.Journal off, the engine's
    // ZdoJournal off, events waiting beyond a frame's drain budget), asking for it reads the game's tables right then,
    // which is exactly what a relist gave. So every answer is the game's state as of now. A portal-list entry counts
    // only in its home sector (HomePortal: the game never takes a moved portal off its old list).
    //
    // Audit (mandatory, §3.3): one built sector a frame, round-robin: its ZDO count and the sum of their DataRevision
    // against the game's. A mismatch is a journal miss (LabJournalMiss, logged): the sector is built again. What the
    // journal does not record (ZDO.Remove*, direct property sets, a mod's writes straight into ZDOExtraData) and has no
    // revision is not ours to see in V2: rows carry membership only.
    //
    // Created bits: set and cleared by the engine's instance events in the same frame (ZdoEvents), read again from
    // ZDO.Created whenever those (re)start; CreatedBit answers from ZDO.Created while they are not live.
    // Own area: sectors within the player's active area are marked PlayerOwned (vanilla owns them); kept or listed
    // all the same unless the caller leaves them out.
    // Multiplayer: the journal sees a peer's changes (its copies, moves and destroys); the registry never writes a ZDO
    // nor touches ownership. Main thread only. Nothing is allocated per frame once the tables have grown (Acquire
    // allocates its area's arrays; the lab's comparisons their scratch sets once).
    internal sealed class SectorRegistry : IDisposable
    {
        // Builds per frame: at most this long (at least one). A sector holds about 60-250 ZDOs, a build ~0.01-0.03 ms.
        const double RebuildBudgetMs = 0.3;
        // The journal is kept open this long after the last sector went (a portal walked away from and back to), so its
        // 5 MB ring is not made and dropped again each time.
        const float JournalGraceSeconds = 60f;
        // Estimates for MemoryBytes: one row (the struct, its slot in its sector's list and in the free list); one
        // record (the object and its dictionary entry).
        const int RowBytes = 48;
        const int RecBytes = 160;
        // Audit misses logged in full, then only counted.
        const int MissLogs = 10;

        const byte BitCreated = 1, BitPortal = 2, BitDistant = 4;

        struct Row
        {
            internal ZDO Zdo;
            // The full id it was taken under: the ZDO object may be pooled and handed out again.
            internal ZDOID Id;
            internal uint DataRev;
            // Its record's slot (-1: free) and its place in that record's Rows.
            internal int Rec;
            internal int Index;
            internal byte Bits;
        }

        struct QueueEntry
        {
            internal SectorRec Rec;
            internal int Token;
        }

        sealed class ZdoRef : IEqualityComparer<ZDO>
        {
            internal static readonly ZdoRef Instance = new ZdoRef();
            public bool Equals(ZDO x, ZDO y) => ReferenceEquals(x, y);
            public int GetHashCode(ZDO z) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(z);
        }

        static readonly AccessTools.FieldRef<ZDOMan, List<ZDO>[]> s_bySector = AccessTools.FieldRefAccess<ZDOMan, List<ZDO>[]>("m_objectsBySector");
        // ZDO.OutsideZones (private): a ZDO filed under sector 0 although its position is in a zone (InvalidateSector).
        static readonly Func<ZDO, bool> s_outsideZones = OutsideZonesGetter();

        internal readonly int Generation;
        readonly Dictionary<uint, int> _bySector = new Dictionary<uint, int>();
        SectorRec[] _recs = new SectorRec[64];
        int _recHigh, _recCount;
        int[] _freeRecs = new int[16];
        int _freeRecCount;

        Row[] _rows = new Row[256];
        int _rowHigh, _liveRows;
        int[] _freeRows = new int[64];
        int _freeRowCount;

        // Build queues by priority (Core, Near, Ring), FIFO from each head.
        readonly List<QueueEntry>[] _queue = { new List<QueueEntry>(), new List<QueueEntry>(), new List<QueueEntry>() };
        readonly int[] _queueHead = new int[3];
        int _queued;

        readonly List<AreaSectors> _areas = new List<AreaSectors>();
        ZdoEvents _events;
        bool _rowsClaimed, _claimWarned, _disposed;
        // Since when no sector is registered (-1: some are, or none ever was), for the journal's grace time.
        float _emptySince = -1f;
        bool _held;
        int _auditSlot;
        Vector2s _refZone;
        bool _refKnown;
        // The game's portal lists by sector number (its own dictionary is keyed by a struct without Equals: a lookup
        // there boxes), refreshed once a frame or when the portals change.
        readonly Dictionary<uint, List<ZDO>> _portals = new Dictionary<uint, List<ZDO>>();
        int _portalsFrame = -1, _portalsCount = -1;
        long _portalsRevision = -1;
        // Acquire's scratch.
        readonly List<uint> _tmpNear = new List<uint>(), _tmpRing = new List<uint>();
        readonly List<SectorRole> _tmpRoles = new List<SectorRole>();
        readonly HashSet<uint> _tmpSeen = new HashSet<uint>();

        // Moves whenever any registered sector's list changes, or a sector is registered or dropped.
        internal long Version;

        // Lab: audit misses (journal.miss), audits, builds and the rows they made, the slowest build pass, reads from the
        // game's tables (sectors read directly), stale rows found (a pooled ZDO under another id), rows found in another
        // sector than their event's (moved without a move event), portal rows that followed their home (Rehome), the
        // slowest Update.
        internal long LabJournalMiss, LabAudits, LabRebuilds, LabRebuildRows, LabDirectReads, LabStaleRows, LabRowMoves, LabRehomes;
        internal double LabRebuildMsMax, LabUpdateMsMax;
        int _missLogs;

        internal SectorRegistry(int generation)
        {
            Generation = generation;
        }

        // ---- state ---------------------------------------------------------------------------------------------

        // The rows are the game's state as of now: the journal follows (armed) and nothing waits in it (asked live: the
        // game records more between two drains).
        internal bool Following => _events != null && !_disposed && _events.Armed && _events.Pending == 0;
        internal bool JournalOpen => _events != null;
        // Lab ("ab settle=far"): nothing left to catch up, the journal followed and every queued sector built (without a
        // journal there is nothing to wait for).
        internal bool LabSettled => _events == null || (Following && _queued == 0);
        internal ZdoEvents Events => _events;
        internal long PortalRevision => _events != null ? _events.PortalRevision : 0;
        internal int SectorCount => _recCount;
        internal int RowCount => _liveRows;
        internal int AreaCount => _areas.Count;
        internal int QueuedCount => _queued;

        internal bool IsRegistered(uint sector) => _bySector.ContainsKey(sector);

        internal SectorRole RolesOf(uint sector) => _bySector.TryGetValue(sector, out int slot) ? _recs[slot].Roles : SectorRole.None;

        internal bool IsPlayerOwned(uint sector) => _bySector.TryGetValue(sector, out int slot) && _recs[slot].PlayerOwned;

        // ---- rows for a mirror (FarProxies) ---------------------------------------------------------------------

        // Who mirrors the rows (null: nobody). Set and cleared by the mirror itself.
        internal ISectorRowListener Listener;

        // The journal is open and records: a registered sector that is not built yet will be (its rows follow).
        internal bool WillBuild => _events != null && !_disposed && _events.Armed;
        internal bool CreatedExact => _events != null && !_disposed && _events.CreatedExact;
        internal bool Disposed => _disposed;
        internal int RecHigh => _recHigh;
        internal int RowHigh => _rowHigh;
        internal SectorRec RecAt(int slot) => (uint)slot < (uint)_recHigh ? _recs[slot] : null;
        internal bool TryRec(uint sector, out SectorRec rec)
        {
            rec = _bySector.TryGetValue(sector, out int slot) ? _recs[slot] : null;
            return rec != null;
        }

        internal ZDO RowZdo(int row) => _rows[row].Zdo;
        internal ZDOID RowId(int row) => _rows[row].Id;
        // The row is in use and its ZDO still is the one it was taken under.
        internal bool RowLive(int row) => (uint)row < (uint)_rowHigh && _rows[row].Rec >= 0 && _rows[row].Zdo != null && _rows[row].Zdo.m_uid == _rows[row].Id;
        internal int RowRecSlot(int row) => _rows[row].Rec;
        internal bool RowDistant(int row) => (_rows[row].Bits & BitDistant) != 0;
        internal bool RowPortal(int row) => (_rows[row].Bits & BitPortal) != 0;
        // The ZDO has an instance: the row's bit while the engine's instance events are followed, else ZDO.Created.
        internal bool RowCreated(int row) => CreatedExact ? (_rows[row].Bits & BitCreated) != 0 : _rows[row].Zdo != null && _rows[row].Zdo.Created;

        // The row of a ZDO here (-1 none): its hint, validated by id.
        internal int FindRow(ZDO zdo) => zdo != null && zdo.m_uid != ZDOID.None && !_disposed ? RowOf(zdo, zdo.m_uid, -1) : -1;

        // Every sector of the area is built and the journal follows: its lists change only with AreaVersion(a).
        internal bool Followed(AreaSectors a)
        {
            if (a == null || !a.Live)
                return false;
            Sync();
            if (!Following)
                return false;
            for (int i = 0; i < a.NearRecs.Length; i++)
                if (!_recs[a.NearRecs[i]].Built)
                    return false;
            for (int i = 0; i < a.RingRecs.Length; i++)
                if (!_recs[a.RingRecs[i]].Built)
                    return false;
            return true;
        }

        // Moves whenever what the area lists may have changed (meaningful while Followed; otherwise read it every time).
        internal long AreaVersion(AreaSectors a)
        {
            if (a == null || !a.Live)
                return -1;
            Sync();
            long v = 0;
            for (int i = 0; i < a.NearRecs.Length; i++)
                v += _recs[a.NearRecs[i]].MemberRev;
            for (int i = 0; i < a.RingRecs.Length; i++)
                v += _recs[a.RingRecs[i]].MemberRev;
            return v;
        }

        // ---- the frame ---------------------------------------------------------------------------------------

        // Once a frame (FarWorld.Update): the journal opened or closed as the switches and the sectors say, drained, the
        // Created bits' epoch checked, sectors built within the budget, one audited, the player's area marked.
        internal void Update()
        {
            if (_disposed)
                return;
            long t0 = Stopwatch.GetTimestamp();
            RefreshJournal();
            Sync();
            if (_events != null)
            {
                _events.SyncCreated();
                if (Following)
                {
                    RunBuilds();
                    Audit();
                }
            }
            RefreshPlayerZone();
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            if (ms > LabUpdateMsMax)
                LabUpdateMsMax = ms;
        }

        // What waits in the journal, applied now (every query does this first, so a list is never behind the game).
        internal void Sync()
        {
            if (_events != null && !_disposed)
                _events.Drain();
        }

        void RefreshJournal()
        {
            float now = Time.unscaledTime;
            if (_recCount > 0)
            {
                _emptySince = -1f;
                _held = true;
            }
            else if (_emptySince < 0f && _held)
                _emptySince = now;
            bool want = FarWorld.JournalOn && (_recCount > 0 || (_emptySince >= 0f && now - _emptySince < JournalGraceSeconds));
            if (want && _events == null)
                OpenJournal();
            else if (!want && _events != null)
                CloseJournal();
        }

        void OpenJournal()
        {
            if (!_rowsClaimed)
            {
                _rowsClaimed = ZdoJournal.ClaimRows(this);
                if (!_rowsClaimed)
                {
                    if (!_claimWarned)
                        Plugin.Log.LogWarning("FarWorld: the journal's rows belong to someone else; far sectors are read from the game's tables instead");
                    _claimWarned = true;
                    return;
                }
            }
            _events = new ZdoEvents(this);
            ClearQueues();
            for (int i = 0; i < _recHigh; i++)
            {
                SectorRec rec = _recs[i];
                if (rec == null || !rec.InUse || !rec.Buildable)
                    continue;
                _events.Watch(rec.Sector);
                Queue(rec);
            }
            Plugin.Debug($"FarWorld: journal opened for {_recCount} sector(s) ({ZdoJournal.LabStats()})");
        }

        // The rows are given up whatever throws on the way (else the journal's row owner stays this registry, and every
        // later one reads the game's tables for the rest of the session).
        void CloseJournal()
        {
            try
            {
                _events.Dispose();
                ClearQueues();
                for (int i = 0; i < _recHigh; i++)
                {
                    SectorRec rec = _recs[i];
                    if (rec != null && rec.InUse)
                        Unbuild(rec);
                }
                // No row is live: the tables go back to their first size (a hub's peak is not kept for the session).
                // Hints left on ZDO.rp_row stay safe: RowOf checks them against _rowHigh and the id.
                _rows = new Row[256];
                _rowHigh = 0;
                _liveRows = 0;
                _freeRows = new int[64];
                _freeRowCount = 0;
                for (int i = 0; i < _recHigh; i++)
                    if (_recs[i] != null && _recs[i].Count == 0)
                        _recs[i].Rows = Array.Empty<int>();
            }
            finally
            {
                _events = null;
                if (_rowsClaimed)
                    ZdoJournal.ReleaseRows(this);
                _rowsClaimed = false;
            }
            Plugin.Debug("FarWorld: journal closed; far sectors are read from the game's tables");
        }

        // ---- areas --------------------------------------------------------------------------------------------

        // Registers what an area around zone reaches with sim (as ZDOMan.FindSectorObjects lists it) and returns its
        // handle; Release gives it back. Areas that overlap share the records.
        internal AreaSectors Acquire(Vector2s zone, SimulationDistance sim, object owner)
        {
            if (_disposed || ZoneSystem.instance == null)
                return null;
            Sync();
            Layout(zone, sim, _tmpNear, _tmpRoles, _tmpRing, _tmpSeen);
            var a = new AreaSectors(this, zone, sim, owner, _tmpNear, _tmpRoles, _tmpRing);
            for (int i = 0; i < a.Near.Length; i++)
                a.NearRecs[i] = Ref(a.Near[i], a.NearRoles[i]).Slot;
            for (int i = 0; i < a.Ring.Length; i++)
                a.RingRecs[i] = Ref(a.Ring[i], SectorRole.Ring).Slot;
            a.Live = true;
            _areas.Add(a);
            return a;
        }

        internal void Release(AreaSectors a)
        {
            if (a == null || !a.Live || a.Registry != this)
                return;
            a.Live = false;
            _areas.Remove(a);
            for (int i = 0; i < a.NearRecs.Length; i++)
                Unref(_recs[a.NearRecs[i]], a.NearRoles[i]);
            for (int i = 0; i < a.RingRecs.Length; i++)
                Unref(_recs[a.RingRecs[i]], SectorRole.Ring);
        }

        // The sectors ZDOMan.FindSectorObjects visits around c with sim, in its order: near ones (the centre, then ring
        // by ring the zones within the near radius, or the whole square when classic) and the distant ring (from ring 1,
        // or past the near square when classic, out to the total distance with the ghost-zone margin). Its distant loop
        // tests the first zone of each pair for both (the pairs mirror each other, so it is the same test); a sector it
        // visits twice (only sector 0 can be: every zone outside the grid) is listed once.
        static void Layout(Vector2s c, SimulationDistance sim, List<uint> near, List<SectorRole> roles, List<uint> ring, HashSet<uint> seen)
        {
            near.Clear();
            roles.Clear();
            ring.Clear();
            seen.Clear();
            ZoneSystem zs = ZoneSystem.instance;
            int nr = sim.NearSimulationDistance, total = sim.TotalSimulationDistance;
            bool classic = sim.IsClassic;
            AddNear(c, c, near, roles, seen);
            for (int i = 1; i <= nr; i++)
            {
                for (int x = c.x - i; x <= c.x + i; x++)
                {
                    var p = new Vector2s(x, c.y - i);
                    var q = new Vector2s(x, c.y + i);
                    if (classic || zs.ZonesWithinRadius(c, p, nr))
                        AddNear(c, p, near, roles, seen);
                    if (classic || zs.ZonesWithinRadius(c, q, nr))
                        AddNear(c, q, near, roles, seen);
                }
                for (int y = c.y - i + 1; y <= c.y + i - 1; y++)
                {
                    var p = new Vector2s(c.x - i, y);
                    var q = new Vector2s(c.x + i, y);
                    if (classic || zs.ZonesWithinRadius(c, p, nr))
                        AddNear(c, p, near, roles, seen);
                    if (classic || zs.ZonesWithinRadius(c, q, nr))
                        AddNear(c, q, near, roles, seen);
                }
            }
            for (int k = classic ? 1 + nr : 1; k <= total; k++)
            {
                for (int x = c.x - k; x <= c.x + k; x++)
                {
                    var p = new Vector2s(x, c.y - k);
                    if (!classic && !zs.ZonesWithinRadius(c, p, total, ghostZone: true))
                        continue;
                    AddRing(p, ring, seen);
                    AddRing(new Vector2s(x, c.y + k), ring, seen);
                }
                for (int y = c.y - k + 1; y <= c.y + k - 1; y++)
                {
                    var p = new Vector2s(c.x - k, y);
                    if (!classic && !zs.ZonesWithinRadius(c, p, total, ghostZone: true))
                        continue;
                    AddRing(p, ring, seen);
                    AddRing(new Vector2s(c.x + k, y), ring, seen);
                }
            }
        }

        static void AddNear(Vector2s c, Vector2s z, List<uint> near, List<SectorRole> roles, HashSet<uint> seen)
        {
            uint s = ZoneSystem.SectorToIndex(z).Sector;
            if (!seen.Add(s))
                return;
            near.Add(s);
            roles.Add(Math.Abs(z.x - c.x) <= 1 && Math.Abs(z.y - c.y) <= 1 ? SectorRole.Core : SectorRole.Near);
        }

        static void AddRing(Vector2s z, List<uint> ring, HashSet<uint> seen)
        {
            uint s = ZoneSystem.SectorToIndex(z).Sector;
            if (seen.Add(s))
                ring.Add(s);
        }

        SectorRec Ref(uint sector, SectorRole role)
        {
            SectorRec rec;
            if (_bySector.TryGetValue(sector, out int slot))
                rec = _recs[slot];
            else
            {
                slot = NewRecSlot();
                rec = _recs[slot] ??= new SectorRec();
                rec.Sector = sector;
                rec.Zone = ZoneSystem.IndexToSector(sector);
                rec.Slot = slot;
                rec.InUse = true;
                rec.RefCore = rec.RefNear = rec.RefRing = 0;
                rec.Built = rec.Queued = false;
                rec.Count = 0;
                rec.RevSum = 0;
                rec.PlayerOwned = _refKnown && Chebyshev(rec.Zone, _refZone) <= 1;
                _bySector.Add(sector, slot);
                _recCount++;
                if (_events != null && rec.Buildable)
                {
                    _events.Watch(sector);
                    Queue(rec);
                }
            }
            bool wasFull = rec.Full;
            if (role == SectorRole.Core)
                rec.RefCore++;
            else if (role == SectorRole.Near)
                rec.RefNear++;
            else
                rec.RefRing++;
            Changed(rec, rec.Full != wasFull);
            return rec;
        }

        void Unref(SectorRec rec, SectorRole role)
        {
            if (rec == null || !rec.InUse)
                return;
            bool wasFull = rec.Full;
            if (role == SectorRole.Core)
                rec.RefCore = Math.Max(0, rec.RefCore - 1);
            else if (role == SectorRole.Near)
                rec.RefNear = Math.Max(0, rec.RefNear - 1);
            else
                rec.RefRing = Math.Max(0, rec.RefRing - 1);
            if (rec.Refs == 0)
            {
                Drop(rec);
                return;
            }
            Changed(rec, rec.Full != wasFull);
        }

        // Roles changed; when that changed what it keeps (all of it, or its Distant ones), its rows are made again.
        void Changed(SectorRec rec, bool modeChanged)
        {
            if (modeChanged && rec.Built)
            {
                Unbuild(rec);
                Queue(rec);
            }
            rec.MemberRev++;
            Version++;
            Listener?.RolesChanged(rec);
        }

        void Drop(SectorRec rec)
        {
            Unbuild(rec);
            Listener?.SectorDropped(rec);
            rec.Queued = false;
            rec.QueueToken++;
            if (_events != null && rec.Buildable)
                _events.Unwatch(rec.Sector);
            _bySector.Remove(rec.Sector);
            rec.InUse = false;
            if (rec.Rows.Length > 256)
                rec.Rows = Array.Empty<int>();
            if (_freeRecCount == _freeRecs.Length)
                Array.Resize(ref _freeRecs, _freeRecs.Length * 2);
            _freeRecs[_freeRecCount++] = rec.Slot;
            rec.Slot = -1;
            _recCount--;
            Version++;
        }

        int NewRecSlot()
        {
            if (_freeRecCount > 0)
                return _freeRecs[--_freeRecCount];
            if (_recHigh == _recs.Length)
                Array.Resize(ref _recs, _recs.Length * 2);
            return _recHigh++;
        }

        static int Chebyshev(Vector2s a, Vector2s b) => Math.Max(Math.Abs(a.x - b.x), Math.Abs(a.y - b.y));

        void RefreshPlayerZone()
        {
            if (ZNet.instance == null)
                return;
            Vector2s zone = ZoneSystem.GetZone(ZNet.instance.GetReferencePosition());
            if (_refKnown && zone == _refZone)
                return;
            _refZone = zone;
            _refKnown = true;
            for (int i = 0; i < _recHigh; i++)
            {
                SectorRec rec = _recs[i];
                if (rec != null && rec.InUse)
                    rec.PlayerOwned = Chebyshev(rec.Zone, zone) <= 1;
            }
        }

        // ---- lists --------------------------------------------------------------------------------------------

        // Appends what ZDOMan.FindSectorObjects(a.Zone, a.Sim, near, distant) appends, as sets (the order within a sector
        // is the rows' own): every ZDO of its near sectors (their sector and portal lists) to near, the Distant ones of
        // its ring's sector lists to distant.
        internal void Collect(AreaSectors a, List<ZDO> near, List<ZDO> distant)
        {
            if (a == null || !a.Live || _disposed)
                return;
            Sync();
            bool follow = Following;
            for (int i = 0; i < a.NearRecs.Length; i++)
                Append(_recs[a.NearRecs[i]], near, false, follow);
            if (distant == null)
                return;
            for (int i = 0; i < a.RingRecs.Length; i++)
                Append(_recs[a.RingRecs[i]], distant, true, follow);
        }

        // Every ZDO some area keeps, each once however many areas reach its sector: all of a sector some area has Core
        // or Near, the Distant ones of one in rings only (for the keep-alive pass: ZNetScene.RemoveObjects keeps what is
        // listed). Sectors in the player's own area are left out with skipPlayerOwned (vanilla lists them itself).
        internal void CollectKept(List<ZDO> into, bool skipPlayerOwned = false)
        {
            if (_disposed)
                return;
            Sync();
            bool follow = Following;
            for (int i = 0; i < _recHigh; i++)
            {
                SectorRec rec = _recs[i];
                if (rec == null || !rec.InUse || (skipPlayerOwned && rec.PlayerOwned))
                    continue;
                Append(rec, into, !rec.Full, follow);
            }
        }

        // Whether some area keeps this ZDO (CollectKept would list it).
        internal bool Keeps(ZDO zdo)
        {
            if (zdo == null || _disposed || zdo.m_uid == ZDOID.None)
                return false;
            Sync();
            bool follow = Following;
            if (follow)
            {
                int r = RowOf(zdo, zdo.m_uid, -1);
                if (r >= 0)
                {
                    SectorRec own = _recs[_rows[r].Rec];
                    if (own.Built)
                        return own.Full || ((_rows[r].Bits & BitPortal) == 0 && zdo.Distant);
                }
            }
            bool portal = IsPortal(zdo);
            uint sector = portal || s_outsideZones == null || !s_outsideZones(zdo) ? ZoneSystem.GetSectorIndex(zdo.GetPosition()).Sector : 0u;
            if (!_bySector.TryGetValue(sector, out int slot))
                return false;
            SectorRec rec = _recs[slot];
            if (follow && rec.Built)
                return false;
            return rec.Full || (!portal && zdo.Distant);
        }

        // A sector's list into `into`: its rows while they follow the game, else the game's tables. distantOnly: the
        // Distant ZDOs of its sector list only (FindDistantObjects).
        void Append(SectorRec rec, List<ZDO> into, bool distantOnly, bool follow)
        {
            if (follow && rec.Built)
            {
                int start = into.Count;
                int[] rows = rec.Rows;
                for (int k = 0; k < rec.Count; k++)
                {
                    ref Row row = ref _rows[rows[k]];
                    ZDO z = row.Zdo;
                    if (z.m_uid != row.Id)
                    {
                        // Pooled and handed out again under another id without us seeing it go: what else did we miss?
                        // This sector's list comes from the game's tables, and the sector is built again.
                        LabStaleRows++;
                        into.RemoveRange(start, into.Count - start);
                        Unbuild(rec);
                        Queue(rec);
                        Direct(rec.Sector, into, distantOnly);
                        return;
                    }
                    if (distantOnly && ((row.Bits & BitPortal) != 0 || !z.Distant))
                        continue;
                    into.Add(z);
                }
                return;
            }
            Direct(rec.Sector, into, distantOnly);
        }

        // As the game lists a sector: FindObjects (its sector list, then its portal list) or FindDistantObjects (the
        // Distant ones of its sector list).
        void Direct(uint sector, List<ZDO> into, bool distantOnly)
        {
            LabDirectReads++;
            ZDOMan man = ZDOMan.instance;
            if (man == null)
                return;
            List<ZDO>[] lists = s_bySector(man);
            List<ZDO> list = lists != null && sector < (uint)lists.Length ? lists[sector] : null;
            if (distantOnly)
            {
                if (list != null)
                    for (int i = 0; i < list.Count; i++)
                        if (list[i].Distant)
                            into.Add(list[i]);
                return;
            }
            if (list != null)
                into.AddRange(list);
            List<ZDO> portals = PortalsOf(man, sector);
            if (portals != null)
                into.AddRange(portals);
        }

        List<ZDO> PortalsOf(ZDOMan man, uint sector)
        {
            Dictionary<ZoneSystem.SectorIndex, List<ZDO>> all = man.GetPortals();
            long revision = PortalRevision;
            if (_portalsFrame != Time.frameCount || _portalsCount != all.Count || _portalsRevision != revision)
            {
                _portals.Clear();
                foreach (KeyValuePair<ZoneSystem.SectorIndex, List<ZDO>> kv in all)
                    _portals[kv.Key.Sector] = kv.Value;
                _portalsFrame = Time.frameCount;
                _portalsCount = all.Count;
                _portalsRevision = revision;
            }
            return _portals.TryGetValue(sector, out List<ZDO> list) ? list : null;
        }

        static bool IsPortal(ZDO zdo)
        {
            Game game = Game.instance;
            return game != null && game.PortalPrefabHash.Contains(zdo.GetPrefab());
        }

        // A moved portal can be on several portal lists (AddIfPortal files it where it stands, nothing takes it off the
        // old one), and a destroyed one stays on all but the list of where it stood (pooled, or handed out again as
        // another ZDO). One home per portal: the list of where it stands when that list holds it, else the list it is
        // on. Build, GameCount and the lab's diffs take a portal-list entry only in its home; Direct lists what the game
        // lists (legacy parity).
        bool HomePortal(ZDOMan man, ZDO z, uint sector)
        {
            if (z == null || z.m_uid == ZDOID.None || !IsPortal(z))
                return false;
            uint at = ZoneSystem.GetSectorIndex(z.GetPosition()).Sector;
            if (at == sector)
                return true;
            List<ZDO> there = PortalsOf(man, at);
            return there == null || !there.Contains(z);
        }

        // Whether the game files this ZDO in the sector list of sector: not a portal (by prefab, or on the sector's portal
        // list already: AddIfPortal runs before a new portal's SetPrefab), not outside the zones.
        bool InSectorList(ZDO zdo, uint sector)
        {
            ZDOMan man = ZDOMan.instance;
            if (man == null || IsPortal(zdo))
                return false;
            List<ZDO> portals = PortalsOf(man, sector);
            if (portals != null && portals.Contains(zdo))
                return false;
            if (s_outsideZones != null)
                return !s_outsideZones(zdo) && ZoneSystem.GetSectorIndex(zdo.GetPosition()).Sector == sector;
            List<ZDO>[] lists = s_bySector(man);
            return lists != null && sector < (uint)lists.Length && lists[sector] != null && lists[sector].Contains(zdo);
        }

        static Func<ZDO, bool> OutsideZonesGetter()
        {
            try
            {
                var getter = AccessTools.PropertyGetter(typeof(ZDO), "OutsideZones");
                return getter != null ? AccessTools.MethodDelegate<Func<ZDO, bool>>(getter) : null;
            }
            catch
            {
                return null;
            }
        }

        // ---- created bits -------------------------------------------------------------------------------------

        // Whether the ZDO has an instance (ZNetScene.AddInstance), from its row's bit while the engine's instance events
        // are followed, else from ZDO.Created; exact either way. exact: the bit answered.
        internal bool CreatedBit(ZDO zdo, out bool exact)
        {
            exact = false;
            if (zdo == null)
                return false;
            if (_events != null && _events.CreatedExact)
            {
                int r = RowOf(zdo, zdo.m_uid, -1);
                if (r >= 0)
                {
                    exact = true;
                    return (_rows[r].Bits & BitCreated) != 0;
                }
            }
            return zdo.Created;
        }

        // From ZdoEvents' instance handlers: true when the ZDO has a row here.
        internal bool SetCreated(ZDO zdo, ZDOID id, bool on)
        {
            int r = RowOf(zdo, id, -1);
            if (r < 0)
                return false;
            if (on)
                _rows[r].Bits |= BitCreated;
            else
                _rows[r].Bits = (byte)(_rows[r].Bits & ~BitCreated);
            Listener?.RowCreated(r, on);
            return true;
        }

        // The engine's instance events (re)started: every bit read again.
        internal void ResyncCreated()
        {
            for (int r = 0; r < _rowHigh; r++)
            {
                ref Row row = ref _rows[r];
                if (row.Rec < 0 || row.Zdo == null || row.Zdo.m_uid != row.Id)
                    continue;
                row.Bits = row.Zdo.Created ? (byte)(row.Bits | BitCreated) : (byte)(row.Bits & ~BitCreated);
            }
            Listener?.CreatedResynced();
        }

        // ---- the journal's changes (ZdoEvents) -----------------------------------------------------------------

        // One change, in the journal's order. Returns whether it touched a registered, built sector.
        internal bool Apply(in ZdoChange c)
        {
            SectorRec rec = _bySector.TryGetValue(c.Sector, out int slot) ? _recs[slot] : null;
            if (rec != null && !rec.Built)
                rec = null;
            switch (c.Kind)
            {
                case ZdoChangeKind.Created:
                case ZdoChangeKind.MovedIn:
                    if (rec == null)
                        return false;
                    rec.Rev++;
                    Enter(rec, c);
                    return true;
                case ZdoChangeKind.MovedOut:
                case ZdoChangeKind.Removed:
                {
                    if (rec == null)
                        return false;
                    rec.Rev++;
                    int r = RowOf(c.Zdo, c.Id, c.Row);
                    if (r >= 0 && _rows[r].Rec == rec.Slot)
                        RemoveRow(r);
                    return true;
                }
                case ZdoChangeKind.Destroyed:
                {
                    // Gone for good: its row goes wherever it is (a portal's position may have left the sector it is
                    // filed in, since portals never change sector lists).
                    int r = RowOf(c.Zdo, c.Id, c.Row);
                    if (r < 0)
                        return rec != null;
                    _recs[_rows[r].Rec].Rev++;
                    RemoveRow(r);
                    return true;
                }
                case ZdoChangeKind.Changed:
                    return Data(rec, c);
                case ZdoChangeKind.OwnerChanged:
                    if (rec == null)
                        return false;
                    rec.Rev++;
                    return true;
                case ZdoChangeKind.PortalAdded:
                {
                    // Its home is where it stands now: a row it has in the sector it was filed in before it moved goes
                    // (AddIfPortal leaves it on that list too), registered here or not.
                    bool moved = Rehome(c);
                    if (rec == null)
                        return moved;
                    rec.Rev++;
                    int r = RowOf(c.Zdo, c.Id, c.Row);
                    if (rec.Full)
                    {
                        ZDOMan man = ZDOMan.instance;
                        if (r >= 0 && _rows[r].Rec == rec.Slot)
                            _rows[r].Bits |= BitPortal;
                        else if (c.IsCurrent && man != null && HomePortal(man, c.Zdo, rec.Sector))
                            AddRow(rec, c.Zdo, BitPortal);
                    }
                    else if (r >= 0 && _rows[r].Rec == rec.Slot)
                        RemoveRow(r);   // off the sector list: the distant ring does not list it
                    return true;
                }
                case ZdoChangeKind.PortalRemoved:
                {
                    if (rec == null)
                        return false;
                    rec.Rev++;
                    int r = RowOf(c.Zdo, c.Id, c.Row);
                    if (r >= 0)
                        _rows[r].Bits = (byte)(_rows[r].Bits & ~BitPortal);
                    return true;
                }
            }
            return false;
        }

        // Created / MovedIn: a row for it here (a ring-only sector takes Distant ones only). A ZDO no longer current was
        // destroyed after (its Removed and Destroyed follow, or it was handed out again under another id): nothing.
        void Enter(SectorRec rec, in ZdoChange c)
        {
            if (!c.IsCurrent)
                return;
            ZDO z = c.Zdo;
            if (!rec.Full && !z.Distant)
                return;
            AddRow(rec, z, 0);
        }

        // Changed: the row's revision (and its Distant flag) follow the ZDO. In a ring-only sector a ZDO without a row
        // that is now Distant (SetDistant) comes in. A portal's row first follows its home (Rehome).
        bool Data(SectorRec rec, in ZdoChange c)
        {
            bool moved = (c.Flags & ZdoEventFlags.Portal) != 0 && Rehome(c);
            int r = RowOf(c.Zdo, c.Id, c.Row);
            if (r >= 0)
            {
                if (!c.IsCurrent)
                    return true;
                SectorRec own = _recs[_rows[r].Rec];
                own.Rev++;
                ZDO z = c.Zdo;
                uint revision = z.DataRevision;
                own.RevSum = own.RevSum - _rows[r].DataRev + revision;
                _rows[r].DataRev = revision;
                bool distant = z.Distant;
                if (distant != ((_rows[r].Bits & BitDistant) != 0))
                {
                    if (!own.Full && !distant)
                    {
                        RemoveRow(r);
                        return true;
                    }
                    _rows[r].Bits ^= BitDistant;
                    own.MemberRev++;
                    Version++;
                }
                Listener?.RowChanged(own, r);
                return true;
            }
            if (rec == null)
                return moved;
            rec.Rev++;
            if (!rec.Full && c.IsCurrent && c.Zdo.Distant && InSectorList(c.Zdo, rec.Sector))
                AddRow(rec, c.Zdo, 0);
            return true;
        }

        // A live portal's row follows its home (HomePortal) through its portal events (PortalAdded, a Changed of a
        // portal): off a sector that is no longer its home (the list of where it stands holds it now: a peer's copy of a
        // portal on a ship, or one back in a sector whose list kept it, which says no PortalAdded), into that home when
        // it is built and keeps everything. True when a row went or came.
        bool Rehome(in ZdoChange c)
        {
            ZDOMan man = ZDOMan.instance;
            if (man == null || !c.IsCurrent || !IsPortal(c.Zdo))
                return false;
            ZDO z = c.Zdo;
            int r = RowOf(z, c.Id, c.Row);
            if (r >= 0 && HomePortal(man, z, _recs[_rows[r].Rec].Sector))
                return false;
            uint at = ZoneSystem.GetSectorIndex(z.GetPosition()).Sector;
            List<ZDO> there = PortalsOf(man, at);
            if (there == null || !there.Contains(z))
                return false;   // not listed where it stands: its home is the list it is on (a row there stays)
            bool changed = false;
            if (r >= 0)
            {
                _recs[_rows[r].Rec].Rev++;
                RemoveRow(r);
                changed = true;
            }
            if (_bySector.TryGetValue(at, out int slot) && _recs[slot].Built && _recs[slot].Full)
            {
                _recs[slot].Rev++;
                AddRow(_recs[slot], z, BitPortal);
                changed = true;
            }
            if (changed)
                LabRehomes++;
            return changed;
        }

        // The journal stopped following the world: rows dropped (the sectors are read from the game's tables), every
        // sector queued for when it follows again.
        internal void JournalLost()
        {
            ClearQueues();
            for (int i = 0; i < _recHigh; i++)
            {
                SectorRec rec = _recs[i];
                if (rec == null || !rec.InUse)
                    continue;
                Unbuild(rec);
                Queue(rec);
            }
            Plugin.Debug("FarWorld: the journal does not follow the world (ZdoJournal off or not installed); far sectors are read from the game's tables");
        }

        // Rebuild everything from the game's tables.
        internal void JournalReset()
        {
            ClearQueues();
            for (int i = 0; i < _recHigh; i++)
            {
                SectorRec rec = _recs[i];
                if (rec == null || !rec.InUse)
                    continue;
                Unbuild(rec);
                Queue(rec);
            }
        }

        // The ring lapped us: the registered sectors the lost events named are built again.
        internal void JournalRecovered(List<int> dirty)
        {
            for (int i = 0; i < dirty.Count; i++)
            {
                if (!_bySector.TryGetValue((uint)dirty[i], out int slot))
                    continue;
                SectorRec rec = _recs[slot];
                Unbuild(rec);
                Queue(rec);
            }
        }

        // ---- rows ---------------------------------------------------------------------------------------------

        // The row of the ZDO with this id: the hint (the row its event carried) if it still is that id's, else the
        // ZDO's own (ZDO.rp_row) when the object still is that ZDO; -1 when it has none here.
        int RowOf(ZDO zdo, ZDOID id, int hint)
        {
            if ((uint)hint < (uint)_rowHigh && _rows[hint].Rec >= 0 && _rows[hint].Id == id)
                return hint;
            if (zdo != null && zdo.m_uid == id)
            {
                int r = ZdoJournal.GetRow(zdo);
                if ((uint)r < (uint)_rowHigh && _rows[r].Rec >= 0 && _rows[r].Id == id)
                    return r;
            }
            return -1;
        }

        int AddRow(SectorRec rec, ZDO z, byte bits)
        {
            ZDOID id = z.m_uid;
            if (id == ZDOID.None)
                return -1;
            int old = RowOf(z, id, -1);
            if (old >= 0)
            {
                if (_rows[old].Rec == rec.Slot)
                {
                    _rows[old].Bits |= bits;
                    return old;
                }
                // Filed elsewhere here without a move event between (the journal's order says it is here now).
                LabRowMoves++;
                RemoveRow(old);
            }
            int r;
            if (_freeRowCount > 0)
                r = _freeRows[--_freeRowCount];
            else
            {
                if (_rowHigh == _rows.Length)
                    Array.Resize(ref _rows, _rows.Length * 2);
                r = _rowHigh++;
            }
            ref Row row = ref _rows[r];
            row.Zdo = z;
            row.Id = id;
            row.DataRev = z.DataRevision;
            row.Rec = rec.Slot;
            row.Bits = (byte)(bits | (z.Created ? BitCreated : 0) | (z.Distant ? BitDistant : 0));
            if (rec.Count == rec.Rows.Length)
                Array.Resize(ref rec.Rows, Math.Max(16, rec.Rows.Length * 2));
            row.Index = rec.Count;
            rec.Rows[rec.Count++] = r;
            rec.RevSum += row.DataRev;
            rec.MemberRev++;
            Version++;
            ZdoJournal.SetRow(z, r);
            _liveRows++;
            Listener?.RowAdded(rec, r);
            return r;
        }

        void RemoveRow(int r)
        {
            Listener?.RowRemoved(_recs[_rows[r].Rec], r);
            ref Row row = ref _rows[r];
            SectorRec rec = _recs[row.Rec];
            int i = row.Index, last = --rec.Count;
            if (i != last)
            {
                int moved = rec.Rows[last];
                rec.Rows[i] = moved;
                _rows[moved].Index = i;
            }
            rec.RevSum -= row.DataRev;
            rec.MemberRev++;
            Version++;
            if (row.Zdo != null && row.Zdo.m_uid == row.Id)
                ZdoJournal.SetRow(row.Zdo, -1);
            FreeRow(r);
        }

        void FreeRow(int r)
        {
            _rows[r] = new Row { Rec = -1, Index = -1 };
            if (_freeRowCount == _freeRows.Length)
                Array.Resize(ref _freeRows, _freeRows.Length * 2);
            _freeRows[_freeRowCount++] = r;
            _liveRows--;
        }

        // Its rows dropped: it is read from the game's tables until built again.
        void Unbuild(SectorRec rec)
        {
            if (rec.Built || rec.Count > 0)
                Listener?.SectorUnbuilt(rec);
            for (int k = 0; k < rec.Count; k++)
            {
                int r = rec.Rows[k];
                ref Row row = ref _rows[r];
                if (row.Zdo != null && row.Zdo.m_uid == row.Id)
                    ZdoJournal.SetRow(row.Zdo, -1);
                FreeRow(r);
            }
            if (rec.Built || rec.Count > 0)
            {
                rec.MemberRev++;
                Version++;
            }
            rec.Count = 0;
            rec.RevSum = 0;
            rec.Built = false;
        }

        // ---- builds -------------------------------------------------------------------------------------------

        void Queue(SectorRec rec)
        {
            if (rec.Queued || !rec.InUse || !rec.Buildable || _events == null)
                return;
            rec.Queued = true;
            int priority = rec.RefCore > 0 ? 0 : rec.RefNear > 0 ? 1 : 2;
            _queue[priority].Add(new QueueEntry { Rec = rec, Token = ++rec.QueueToken });
            _queued++;
        }

        void ClearQueues()
        {
            for (int p = 0; p < _queue.Length; p++)
            {
                foreach (QueueEntry e in _queue[p])
                    e.Rec.Queued = false;
                _queue[p].Clear();
                _queueHead[p] = 0;
            }
            _queued = 0;
        }

        // Queued sectors built from the game's tables, core first, within the budget (at least one a frame). Only while
        // Following: the tables are then exactly what the journal has said so far.
        void RunBuilds()
        {
            if (_queued == 0)
                return;
            long t0 = Stopwatch.GetTimestamp();
            double limit = RebuildBudgetMs * Stopwatch.Frequency / 1000.0;
            bool any = false;
            for (int p = 0; p < _queue.Length; p++)
            {
                List<QueueEntry> q = _queue[p];
                while (_queueHead[p] < q.Count)
                {
                    if (any && Stopwatch.GetTimestamp() - t0 > limit)
                        goto done;
                    QueueEntry e = q[_queueHead[p]++];
                    _queued--;
                    if (e.Token != e.Rec.QueueToken || !e.Rec.Queued || !e.Rec.InUse)
                        continue;
                    e.Rec.Queued = false;
                    Build(e.Rec);
                    any = true;
                }
                q.Clear();
                _queueHead[p] = 0;
            }
            done:
            if (_queued < 0)
                _queued = 0;
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            if (ms > LabRebuildMsMax)
                LabRebuildMsMax = ms;
        }

        // Its rows made from the game's tables as they are now: everything of its sector list and the portals at home on
        // its portal list (Full, HomePortal), or the Distant ones of its sector list.
        void Build(SectorRec rec)
        {
            Unbuild(rec);
            ZDOMan man = ZDOMan.instance;
            if (man == null)
                return;
            bool full = rec.Full;
            List<ZDO>[] lists = s_bySector(man);
            List<ZDO> list = lists != null && rec.Sector < (uint)lists.Length ? lists[rec.Sector] : null;
            int before = _liveRows;
            if (list != null)
                for (int i = 0; i < list.Count; i++)
                {
                    ZDO z = list[i];
                    if (z != null && (full || z.Distant))
                        AddRow(rec, z, 0);
                }
            if (full)
            {
                List<ZDO> portals = PortalsOf(man, rec.Sector);
                if (portals != null)
                    for (int i = 0; i < portals.Count; i++)
                        if (HomePortal(man, portals[i], rec.Sector))
                            AddRow(rec, portals[i], BitPortal);
            }
            rec.Built = true;
            rec.MemberRev++;
            Version++;
            LabRebuilds++;
            LabRebuildRows += _liveRows - before;
            Listener?.SectorBuilt(rec);
        }

        // ---- audit --------------------------------------------------------------------------------------------

        // One built sector a frame, round-robin: its row count and DataRevision sum against the game's.
        void Audit()
        {
            if (_recHigh == 0)
                return;
            for (int tries = 0; tries < _recHigh; tries++)
            {
                _auditSlot = (_auditSlot + 1) % _recHigh;
                SectorRec rec = _recs[_auditSlot];
                if (rec == null || !rec.InUse || !rec.Built)
                    continue;
                LabAudits++;
                GameCount(rec, out int count, out ulong sum);
                if (count != rec.Count || sum != rec.RevSum)
                {
                    LabJournalMiss++;
                    if (_missLogs < MissLogs)
                    {
                        _missLogs++;
                        Plugin.Log.LogWarning($"FarWorld audit: journal miss in sector {rec.Zone.x},{rec.Zone.y} ({rec.Roles}{(rec.Full ? "" : ", Distant only")}): " +
                                              $"{rec.Count} rows, revisions {rec.RevSum}; the game lists {count}, revisions {sum}. {DescribeDiff(rec)}Built again." +
                                              (_missLogs == MissLogs ? " (further misses are only counted)" : ""));
                    }
                    Unbuild(rec);
                    Queue(rec);
                }
                return;
            }
        }

        // What the game lists for this sector in its mode, as Build takes it (of its portal list the entries at home
        // there): count and DataRevision sum.
        void GameCount(SectorRec rec, out int count, out ulong sum)
        {
            count = 0;
            sum = 0;
            ZDOMan man = ZDOMan.instance;
            if (man == null)
                return;
            List<ZDO>[] lists = s_bySector(man);
            List<ZDO> list = lists != null && rec.Sector < (uint)lists.Length ? lists[rec.Sector] : null;
            bool full = rec.Full;
            if (list != null)
                for (int i = 0; i < list.Count; i++)
                {
                    ZDO z = list[i];
                    if (z == null || !(full || z.Distant))
                        continue;
                    count++;
                    sum += z.DataRevision;
                }
            if (full)
            {
                List<ZDO> portals = PortalsOf(man, rec.Sector);
                if (portals != null)
                    for (int i = 0; i < portals.Count; i++)
                    {
                        if (!HomePortal(man, portals[i], rec.Sector))
                            continue;
                        count++;
                        sum += portals[i].DataRevision;
                    }
            }
        }

        HashSet<ZDO> _labSetA, _labSetB;
        List<ZDO> _labListA, _labListB, _labListC, _labListD;

        // For a miss's log line: which ZDOs differ, the game's side as Build takes it (portal-list entries not at home
        // there are counted apart as vanilla ghosts). Allocates its scratch once; misses are rare.
        string DescribeDiff(SectorRec rec)
        {
            _labSetA ??= new HashSet<ZDO>(ZdoRef.Instance);
            _labSetB ??= new HashSet<ZDO>(ZdoRef.Instance);
            _labSetA.Clear();
            _labSetB.Clear();
            int ghosts = 0;
            ZDOMan man = ZDOMan.instance;
            if (man != null)
            {
                List<ZDO>[] lists = s_bySector(man);
                List<ZDO> list = lists != null && rec.Sector < (uint)lists.Length ? lists[rec.Sector] : null;
                if (list != null)
                    for (int i = 0; i < list.Count; i++)
                        if (list[i] != null && (rec.Full || list[i].Distant))
                            _labSetA.Add(list[i]);
                List<ZDO> portals = rec.Full ? PortalsOf(man, rec.Sector) : null;
                if (portals != null)
                    for (int i = 0; i < portals.Count; i++)
                    {
                        if (HomePortal(man, portals[i], rec.Sector))
                            _labSetA.Add(portals[i]);
                        else if (portals[i] != null)
                            ghosts++;
                    }
            }
            int stale = 0, revs = 0;
            for (int k = 0; k < rec.Count; k++)
            {
                ref Row row = ref _rows[rec.Rows[k]];
                if (row.Zdo.m_uid != row.Id)
                    stale++;
                else if (row.DataRev != row.Zdo.DataRevision)
                    revs++;
                _labSetB.Add(row.Zdo);
            }
            int missing = 0, extra = 0;
            string first = null;
            foreach (ZDO z in _labSetA)
                if (!_labSetB.Contains(z))
                {
                    missing++;
                    first ??= $"first missing {z.m_uid} prefab {z.GetPrefab()}; ";
                }
            foreach (ZDO z in _labSetB)
                if (!_labSetA.Contains(z))
                {
                    extra++;
                    first ??= $"first extra {z.m_uid} prefab {z.GetPrefab()}; ";
                }
            return $"Missing {missing}, extra {extra}, stale {stale}, revision behind {revs}, vanilla ghosts {ghosts}; {first}";
        }

        // ---- lab ----------------------------------------------------------------------------------------------

        // The gate's set test: what Collect hands out for this area against ZDOMan.FindSectorObjects right now, as sets
        // (nothing in flight: the journal is drained first), the vanilla ghosts left out on both sides (LabDropGhosts;
        // ghosts: how many the game listed). True when both lists match.
        internal bool LabCompare(AreaSectors a, out int nearMissing, out int nearExtra, out int distantMissing, out int distantExtra, out int ghosts)
        {
            nearMissing = nearExtra = distantMissing = distantExtra = ghosts = 0;
            if (a == null || !a.Live || ZDOMan.instance == null)
                return false;
            _labSetA ??= new HashSet<ZDO>(ZdoRef.Instance);
            _labSetB ??= new HashSet<ZDO>(ZdoRef.Instance);
            _labListA ??= new List<ZDO>();
            _labListB ??= new List<ZDO>();
            _labListC ??= new List<ZDO>();
            _labListD ??= new List<ZDO>();
            _labListA.Clear();
            _labListB.Clear();
            _labListC.Clear();
            _labListD.Clear();
            Collect(a, _labListA, _labListB);
            ZDOMan.instance.FindSectorObjects(a.Zone, a.Sim, _labListC, _labListD);
            ghosts = LabDropGhosts(a, _labListC);
            LabDropGhosts(a, _labListA);   // a sector read from the game's tables lists them as the game does
            Diff(_labListA, _labListC, out nearMissing, out nearExtra);
            Diff(_labListB, _labListD, out distantMissing, out distantExtra);
            return nearMissing + nearExtra + distantMissing + distantExtra == 0;
        }

        HashSet<ZDO> _labGhosts;

        // Lab: takes the vanilla ghosts out of a near list of this area (as FindSectorObjects or Collect made it) and
        // returns how many entries went: entries of its near sectors' portal lists that are at home (HomePortal) on none
        // of them and on none of their sector lists either (a moved portal's old list whose new home is outside the area,
        // a destroyed portal's pooled ZDO). The rows never list them. Allocates (lab only).
        internal int LabDropGhosts(AreaSectors a, List<ZDO> near)
        {
            ZDOMan man = ZDOMan.instance;
            if (a == null || near == null || man == null)
                return 0;
            _labGhosts ??= new HashSet<ZDO>(ZdoRef.Instance);
            _labGhosts.Clear();
            for (int i = 0; i < a.Near.Length; i++)
            {
                List<ZDO> portals = PortalsOf(man, a.Near[i]);
                if (portals != null)
                    for (int k = 0; k < portals.Count; k++)
                        if (portals[k] != null && !HomePortal(man, portals[k], a.Near[i]))
                            _labGhosts.Add(portals[k]);
            }
            if (_labGhosts.Count == 0)
                return 0;
            // Listed rightly too (at home on another near sector's portal list, or handed out again onto a sector list
            // here): not a ghost.
            List<ZDO>[] lists = s_bySector(man);
            for (int i = 0; i < a.Near.Length && _labGhosts.Count > 0; i++)
            {
                uint s = a.Near[i];
                List<ZDO> list = lists != null && s < (uint)lists.Length ? lists[s] : null;
                if (list != null)
                    for (int k = 0; k < list.Count; k++)
                        if (list[k] != null)
                            _labGhosts.Remove(list[k]);
                List<ZDO> portals = PortalsOf(man, s);
                if (portals != null)
                    for (int k = 0; k < portals.Count; k++)
                        if (HomePortal(man, portals[k], s))
                            _labGhosts.Remove(portals[k]);
            }
            if (_labGhosts.Count == 0)
                return 0;
            int before = near.Count;
            near.RemoveAll(z => z != null && _labGhosts.Contains(z));
            return before - near.Count;
        }

        void Diff(List<ZDO> ours, List<ZDO> game, out int missing, out int extra)
        {
            _labSetA.Clear();
            _labSetB.Clear();
            foreach (ZDO z in ours)
                _labSetA.Add(z);
            foreach (ZDO z in game)
                _labSetB.Add(z);
            missing = extra = 0;
            foreach (ZDO z in _labSetB)
                if (!_labSetA.Contains(z))
                    missing++;
            foreach (ZDO z in _labSetA)
                if (!_labSetB.Contains(z))
                    extra++;
        }

        // Every built sector against the game's tables now (count and revisions; no rebuild), and the rows' own links.
        internal void LabAuditAll(out int sectors, out int mismatched, out int brokenRows)
        {
            sectors = mismatched = brokenRows = 0;
            Sync();
            if (!Following)
                return;
            for (int i = 0; i < _recHigh; i++)
            {
                SectorRec rec = _recs[i];
                if (rec == null || !rec.InUse || !rec.Built)
                    continue;
                sectors++;
                GameCount(rec, out int count, out ulong sum);
                if (count != rec.Count || sum != rec.RevSum)
                    mismatched++;
                for (int k = 0; k < rec.Count; k++)
                {
                    ref Row row = ref _rows[rec.Rows[k]];
                    if (row.Rec != rec.Slot || row.Index != k || row.Zdo == null || row.Zdo.m_uid != row.Id)
                        brokenRows++;
                }
            }
        }

        // The Created bits against ZDO.Created (meaningful while the engine's instance events are followed).
        internal void LabAuditCreated(out int rows, out int wrong, out bool exact)
        {
            rows = wrong = 0;
            Sync();
            exact = _events != null && _events.CreatedExact;
            for (int r = 0; r < _rowHigh; r++)
            {
                ref Row row = ref _rows[r];
                if (row.Rec < 0 || row.Zdo == null || row.Zdo.m_uid != row.Id)
                    continue;
                rows++;
                if (((row.Bits & BitCreated) != 0) != row.Zdo.Created)
                    wrong++;
            }
        }

        // ---- memory -------------------------------------------------------------------------------------------

        // What the registry holds (an estimate): rows, sector records and their lists, the free lists, the queues.
        internal long MemoryBytes()
        {
            long n = (long)_rows.Length * (RowBytes - 4) + (long)_freeRows.Length * 4 + (long)_recCount * RecBytes;
            for (int i = 0; i < _recHigh; i++)
                if (_recs[i] != null)
                    n += (long)_recs[i].Rows.Length * 4;
            for (int p = 0; p < _queue.Length; p++)
                n += (long)_queue[p].Capacity * 16;
            return n;
        }

        // One area's share (an estimate): each of its sectors' rows and record divided by the references on it.
        internal long AreaBytes(AreaSectors a)
        {
            if (a == null || !a.Live)
                return 0;
            double n = 0;
            for (int i = 0; i < a.NearRecs.Length; i++)
                n += Share(_recs[a.NearRecs[i]]);
            for (int i = 0; i < a.RingRecs.Length; i++)
                n += Share(_recs[a.RingRecs[i]]);
            return (long)n + (long)a.SectorCount * 8 + 128;
        }

        static double Share(SectorRec rec) =>
            rec.Refs > 0 ? ((double)rec.Count * RowBytes + (double)(rec.Rows.Length - rec.Count) * 4 + RecBytes) / rec.Refs : 0.0;

        internal string Describe()
        {
            int core = 0, near = 0, ring = 0, built = 0, owned = 0;
            for (int i = 0; i < _recHigh; i++)
            {
                SectorRec rec = _recs[i];
                if (rec == null || !rec.InUse)
                    continue;
                if (rec.RefCore > 0)
                    core++;
                else if (rec.RefNear > 0)
                    near++;
                else
                    ring++;
                if (rec.Built)
                    built++;
                if (rec.PlayerOwned)
                    owned++;
            }
            return $"registry: {_areas.Count} area(s), {_recCount} sector(s) (core {core}, near {near}, ring only {ring}; built {built}, queued {_queued}, " +
                   $"in the player's area {owned}), {_liveRows} rows, ~{MemoryBytes() / 1024} KB; {(Following ? "following the journal" : _events != null ? "journal not followed now" : "no journal")}; " +
                   $"journal misses {LabJournalMiss} in {LabAudits} audits, builds {LabRebuilds} ({LabRebuildRows} rows, slowest pass {LabRebuildMsMax:0.000} ms), " +
                   $"direct reads {LabDirectReads}, stale rows {LabStaleRows}, row moves {LabRowMoves}, portals rehomed {LabRehomes}, slowest update {LabUpdateMsMax:0.000} ms" +
                   (_events != null ? "; " + _events.Describe() : "");
        }

        internal void LabResetCounters()
        {
            LabJournalMiss = LabAudits = LabRebuilds = LabRebuildRows = LabDirectReads = LabStaleRows = LabRowMoves = LabRehomes = 0;
            LabRebuildMsMax = LabUpdateMsMax = 0.0;
            _missLogs = 0;
        }

        // ---- teardown -----------------------------------------------------------------------------------------

        // Everything goes: the journal closed (its rows given up), every area's handle dead.
        public void Dispose()
        {
            if (_disposed)
                return;
            ISectorRowListener listener = Listener;
            Listener = null;
            listener?.RegistryDisposed();
            if (_events != null)
                CloseJournal();
            // Claimed without a journal (its ZdoEvents threw in OpenJournal): given up all the same.
            if (_rowsClaimed)
            {
                ZdoJournal.ReleaseRows(this);
                _rowsClaimed = false;
            }
            for (int i = 0; i < _areas.Count; i++)
                _areas[i].Live = false;
            _areas.Clear();
            for (int i = 0; i < _recHigh; i++)
                if (_recs[i] != null)
                {
                    _recs[i].InUse = false;
                    _recs[i].Count = 0;
                }
            _bySector.Clear();
            _recCount = 0;
            _portals.Clear();
            _disposed = true;
        }
    }
}
