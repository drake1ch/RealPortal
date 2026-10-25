using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace RealPortal.Engine
{
    // E1 of ARCHITECTURE-v2 (§3.3, §5.2): a journal of ZDO changes, so a consumer (Real Portal's FarWorld
    // SectorRegistry) follows the world in O(changes) instead of rescanning ZDOMan's sector lists. Every place where the
    // game changes which sector list a ZDO is in, bumps its data or owner revision, takes in a peer's copy, destroys it,
    // or puts it on the portal list reports one compact event (ZdoEvent: kind, the ZDO, its full ZDOID, old and new
    // sector, the consumer's row, the frame) into a preallocated ring of 65536. Each reader keeps its own cursor (a
    // sequence number) and reads forward; a reader the ring has lapped is told so (Overflowed) and recovers from a
    // per-sector table of the newest event that named each sector, i.e. it marks every sector touched since its cursor
    // dirty (a burst over 64k, zone generation say, costs a rebuild of the touched sectors, §3.3).
    //
    // Sources (observers only: nothing here changes what the game does, writes a ZDO, sends anything or touches
    // ownership; Harmony postfixes unless said otherwise):
    //   ZDOMan.AddToSector, InitialAddToSector      Added(new sector)
    //   ZDOMan.RemoveFromSector                     Removed(old sector). The game also calls it for ZDOs that are not in
    //                                               that list (portals: AddIfPortal, HandleDestroyedZDO); consumers
    //                                               ignore what has no row. So replaying Added/Removed/Moved gives
    //                                               exactly the game's m_objectsBySector between two Resets
    //   ZDO.SetSector (private; prefix+finalizer)   Moved(old, new): the RemoveFromSector + AddToSector pair inside it,
    //                                               reported once. Portals never move (SetSector returns for them)
    //   ZDO.IncreaseDataRevision (private)          Changed, Flags.Revision (a local write)
    //   ZDO.Deserialize                             Changed (with Flags.Remote inside ZDOMan.RPC_ZDOData: a peer's copy).
    //                                               A Changed of a portal prefab carries Flags.Portal: with PortalAdded
    //                                               and PortalRemoved it is §3.3's PortalChanged (a portal's connection
    //                                               or tag, SetConnection + SetDirtyPortals, or a peer's copy of them)
    //   ZDO.IncreaseOwnerRevision                   OwnerChanged, Flags.Revision (ZDO.SetOwner, which calls it only when
    //                                               the owner changes)
    //   ZDO.SetOwnerInternal (prefix+postfix)       OwnerChanged, only when the owner really changes: a new ZDO's first
    //                                               owner (CreateNewZDO), SetOwner's first half, a peer's handover in
    //                                               RPC_ZDOData (full or owner-only update). RPC_ZDOData calls it for
    //                                               every update it takes in, almost always with the owner the ZDO
    //                                               already has: those record nothing (one owner lookup while recording)
    //   ZDOMan.HandleDestroyedZDO (private;         Removed(old sector) from its RemoveFromSector, PortalRemoved if it
    //     prefix+finalizer)                         took the ZDO off m_portalObjects, then Destroyed(old sector, row).
    //                                               By the time Destroyed is read the ZDO is back in the pool: use Id
    //   ZDOMan.AddIfPortal (private; prefix+postfix) PortalAdded, only when it really put the ZDO on m_portalObjects
    //                                               (it runs for every peer update of every ZDO). The RemoveFromSector
    //                                               inside it, which takes that portal off its sector list (FindSector-
    //                                               Objects finds it on the portal list instead), is a Removed with
    //                                               Flags.Portal | PortalList
    //   ZDOMan.RPC_ZDOData (prefix+finalizer)       everything recorded inside it carries Flags.Remote
    //   ZDOMan.RPC_DestroyZDO (prefix+finalizer)    the same for a peer's destroys (this game's own come back through it
    //                                               too, SendDestroyed routes to everybody: sender is this session, local)
    //   ZDOMan.ShutDown (finalizer), ZDOMan(int)    Reset, Flags.World: every ZDO is gone
    //   ZDOMan.LoadChunks, Load (prefix+finalizer)  nothing ZDO by ZDO while a world loads (millions of InitialAddToSector),
    //                                               one Reset(World) when it is done: rebuild from the game's tables
    // A write that comes in on another thread is not recorded and makes every reader resynchronise (Reset, Flags.Gap):
    // the game never writes a live ZDO off the main thread, so this is a guard, not a path. Consecutive Changed (or
    // OwnerChanged) events of one ZDO that no reader has read yet are folded into one with the flags or-ed, so a
    // ZSyncTransform's position + rotation + velocity writes, or a new object's ZNetView.Awake sets, take one slot. Event
    // order is the game's order; the ZDO itself is always read live by the consumer. Events of a ZDO that is not live
    // (id None: pooled, a mod writing through a stale reference) are not recorded: the game's tables do not have it.
    // Not journalled: SetDirtyPortals (8 IL bytes, so Mono may inline it; every call follows an IncreaseDataRevision of
    // that portal, i.e. a Changed[Portal]), the portal list built during loads (covered by the load's Reset), writes that bypass
    // the revision (ZDO.Remove*, direct property sets such as Persistent/Distant/Type in ZNetView.Awake next to a
    // SetPrefab that does report, a non-owner's SetPosition within one sector), writes straight into ZDOExtraData by a
    // mod. The game does not send those to peers either; the consumer's audit (mandatory per §3.3) covers them.
    //
    // Rows. The preloader (Engine\Preloader -> BepInEx\patchers\RealPortal.Preloader.dll) adds `public int rp_row` to ZDO,
    // set to -1 by every ZDO constructor and at the start of ZDO.Reset (which ZDOPool.Release calls before a ZDO object is
    // reused under another id), and marks the six hooked methods NoInlining (ZDO.IncreaseDataRevision,
    // IncreaseOwnerRevision, SetSector; ZDOMan.AddToSector, InitialAddToSector, RemoveFromSector). At Install the module
    // checks what it got, before it patches anything: the field by reflection, the -1 stores in the IL of Reset and of
    // every constructor, NoInlining via MethodImplementationFlags. With field and stores, GetRow/SetRow are one field
    // access; otherwise rows live in a Dictionary<ZDOID,int>, dropped with the ZDO (Destroyed), on every World reset and
    // when the journal stops. Either way a row never passes to another ZDOID through the pool. A row is the owner's hint:
    // it validates it against its own table (full ZDOID, §3.3), since a row set before a reset may still be on the ZDO.
    // One owner writes rows (ClaimRows); every event carries the row its ZDO had when the event was recorded.
    // Why NoInlining: a Harmony patch detours the method's own code, so a call site the JIT compiled with the method
    // inlined never reaches the hook. Mono inlines only methods under 20 IL bytes (MONO_INLINELIMIT) that are not marked
    // NoInlining; the six are 24-119 bytes in this game version, so nothing is lost without the preloader today. Harmony
    // (MonoMod) also marks what it patches NoInlining at runtime, which covers every call site compiled after Install;
    // the preloader covers the ones compiled before it, after an update that shrinks a target. The self-check names
    // every hooked method that could be inlined and was not marked before the patch (Reliable is then false).
    //
    // Cost. Nothing is recorded until someone subscribes (and Feature is on); until then a hook is Harmony's detour plus
    // one static bool test, ~1-3 ns per call (the hook bodies are under Mono's inline limit, the work is in separate
    // NoInlining methods), and a finalizer's try block on SetSector, HandleDestroyedZDO, RPC_ZDOData, RPC_DestroyZDO and
    // the loads (no cost unless something throws). The hottest hooked method, IncreaseDataRevision, runs a few hundred
    // to a few thousand times per frame, so idle is well under 0.01 ms per frame. Recording: ~20-40 ns per event
    // (thread check, frame number, sector of the position, row, ring store; +20 ns with the dictionary rows; a few ns for
    // the portal test of a new Changed); a folded Changed ~10 ns; a SetOwnerInternal call one owner lookup. Memory: ~3 MB
    // ring + 2 MB sector table, allocated once by the first subscription (kept across feature switches while anyone is
    // subscribed) and released once nobody is subscribed and nothing records (also when the feature went off first). The
    // preloader's field adds 4 bytes per ZDO object (it may round up to 8).
    // Out of the game (the real ZDO/ZDOMan code on Unity's Mono, harness in the scratch build): ZDO.Set ~100 ns vanilla,
    // idle within noise (0-5 ns), recording folded +25 ns; a SetPosition across sectors (4 hooks) +20-30 ns idle.
    //
    // Off (or nobody subscribed): nothing is recorded and the game runs exactly as without the module, since the hooks
    // only read. On: the same game behaviour; the journal only observes. Every start or stop of the ring (feature switched,
    // first subscriber, last one gone) is an edge across which nothing was recorded: every cursor handed out before it
    // reports Overflowed, and Recover says reset (rebuild everything). While a subscriber is not followed (IsArmed false:
    // the feature is off, or the module did not install) Read and Pending say NotArmed, never "nothing changed": a
    // consumer tests IsArmed every frame and follows the game another way meanwhile (a world load only pauses: still
    // armed). Subscribe/Unsubscribe at any time. Everything here is main-thread only, as the game's ZDO code is.
    //
    // Consumers. ZdoJournalReader is the per-frame drain on top of the cursor API: one subscription, a set of watched
    // sectors (bits over the 512 x 512 grid), and Drain(list), which turns the events since the last drain into changes
    // per watched sector as ZDOMan.FindSectorObjects sees the sector (its sector list and its portal list together):
    // Created, MovedIn/MovedOut (a Moved is one change on each watched side), Removed, Changed, OwnerChanged, Destroyed,
    // PortalAdded/PortalRemoved, each with the ZDO, its id, its row, the frame and the event's flags (Remote: a peer's
    // copy or destroy; Revision: a local write). It handles what the raw API leaves to the consumer: NotArmed, an
    // overflow (Recovered: DirtySectors lists the watched sectors to rebuild from the game's tables) and a Reset
    // (rebuild everything). Portal events pass whatever the watch (PortalsAlways). Nothing is allocated per drain once
    // the caller's list has grown.
    public enum ZdoEventKind : byte
    {
        None = 0,
        Added = 1,          // NewSector
        Removed = 2,        // OldSector
        Moved = 3,          // OldSector -> NewSector
        Changed = 4,        // Old = New = the sector of the ZDO's position
        OwnerChanged = 5,   // Old = New = the sector of the ZDO's position
        Destroyed = 6,      // OldSector: the sector of its position; Row: its row when the destroy began
        PortalAdded = 7,    // NewSector: its m_portalObjects key
        PortalRemoved = 8,  // OldSector: its m_portalObjects key
        Reset = 9,          // Flags.World or Flags.Gap; no ZDO, no sectors
    }

    [Flags]
    public enum ZdoEventFlags : byte
    {
        None = 0,
        Revision = 1,       // a local write: the game bumped the ZDO's data or owner revision (it goes to peers)
        Remote = 2,         // recorded inside ZDOMan.RPC_ZDOData (a peer's copy) or a peer's RPC_DestroyZDO
        Portal = 4,         // the ZDO is a portal prefab (Destroyed, PortalAdded, PortalRemoved, a PortalList Removed;
                            // Changed: its prefab at the first of the folded writes)
        World = 8,          // Reset: the game dropped every ZDO (ShutDown, a world load, a new ZDOMan)
        Gap = 16,           // Reset: events were missed (a write off the main thread); the ZDOs are still valid
        PortalList = 32,    // Removed: AddIfPortal took the portal off its sector list because it put it on the portal
                            // list of that sector; FindSectorObjects still finds it there
    }

    public struct ZdoEvent
    {
        // The ZDO object, which may have been recycled by the time the event is read, whatever its kind: a destroyed
        // ZDO goes back to ZDOPool (a stack), so the next CreateNewZDO, in the same frame too, hands this very object
        // out under another id, and the old id's unread events (Changed, Moved, ... and Destroyed) then point at the
        // new ZDO. Check IsCurrent before reading it; otherwise look it up by Id (ZDOMan.GetZDO) or skip the event.
        public ZDO Zdo;
        // Its full id when the event was recorded.
        public ZDOID Id;
        public ZdoEventKind Kind;
        public ZdoEventFlags Flags;
        // ZoneSystem.SectorIndex.Sector values, or ZdoJournal.NoSector.
        public uint OldSector;
        public uint NewSector;
        // Its row (ZdoJournal.SetRow) when the event was recorded, -1 when none.
        public int Row;
        // Time.frameCount when the event was recorded (the first of the folded ones).
        public int Frame;

        // The object still is the ZDO the event was about.
        public bool IsCurrent => Zdo != null && Id != ZDOID.None && Zdo.m_uid == Id;

        public override string ToString() =>
            $"{Kind}{(Flags != ZdoEventFlags.None ? "[" + Flags + "]" : "")} {Id} {Sector(OldSector)}->{Sector(NewSector)} row {Row} frame {Frame}";

        static string Sector(uint s) => s == ZdoJournal.NoSector ? "-" : s.ToString();
    }

    // The journal's public face. Main thread only.
    //
    //   long cursor = ZdoJournal.Subscribe();          // events from now on: build your state from the game's tables now
    //   ...each frame:
    //   if (!ZdoJournal.IsArmed) {
    //       // Nothing is being recorded (the feature is off, or the module did not install): follow the game another
    //       // way (scan, audit). Read and Pending say NotArmed meanwhile; once it arms, the cursor reports Overflowed.
    //   } else {
    //       if (ZdoJournal.Overflowed(cursor))
    //           ZdoJournal.Recover(ref cursor, dirtySectors, out bool reset);   // reset: rebuild everything
    //       ZdoJournal.Read(ref cursor, events);       // or: while (ZdoJournal.Next(ref cursor, out ZdoEvent e)) ...
    //       foreach (ZdoEvent e in events)
    //           ...                                    // e.Zdo only if e.IsCurrent (any kind); otherwise by e.Id
    //   }
    //   ...
    //   ZdoJournal.Unsubscribe();
    public static class ZdoJournal
    {
        public const int Capacity = 1 << 16;
        const int Mask = Capacity - 1;
        public const uint NoSector = uint.MaxValue;
        // ZoneSystem.SectorToIndex: (x + 256) + (y + 256) * 512, and 0 outside.
        public const int SectorCount = 512 * 512;
        // The preloader's field (RealPortal.Preloader.Patcher.RowField).
        public const string RowFieldName = "rp_row";

        // ---- state; Recording is what every hook reads ----

        internal static bool Recording;
        internal static bool Installed;
        // > 0 inside ZDOMan.LoadChunks/Load (nothing recorded) and inside RPC_ZDOData (Flags.Remote).
        internal static int LoadDepth, RemoteDepth;
        // Armed: installed, on and subscribed to, so the ring exists (Recording also needs no world load running).
        static bool s_armed;
        // Bumped whenever recording starts or stops: a capture begun before it is dropped.
        static int s_epoch;
        static int s_subscribers;
        static ZdoEvent[] s_ring;
        // Per sector: 1 + the sequence number of the newest event naming it, 0 when none since the ring was made.
        static long[] s_touch;
        // s_head: the next sequence number. s_floor: the first one of the current ring (a cursor before it lost events
        // at an edge). s_readFence: the furthest any reader has been given; events at or after it are unread by
        // everyone, so they may still be folded. s_floor <= s_readFence <= s_head.
        static long s_head, s_floor, s_readFence, s_lastReset = -1;
        static volatile bool s_gapPending;
        [ThreadStatic] static bool t_main;

        static AccessTools.FieldRef<ZDO, int> s_rowField;
        static readonly Dictionary<ZDOID, int> s_rowMap = new Dictionary<ZDOID, int>();
        static object s_rowOwner;

        // ---- status (set by the self-check at Install) ----

        public static bool IsInstalled => Installed;
        // Events are being recorded now (armed and no world load running).
        public static bool IsRecording => Recording;
        // The journal follows the world for its subscribers: installed, on and subscribed to. A consumer tests it every
        // frame: while it is false nothing is recorded (Read and Pending say NotArmed). A world load does not clear it
        // (recording pauses and the load ends with a Reset[World] event).
        public static bool IsArmed => s_armed;
        // The preloader ran: ZDO has the rp_row field.
        public static bool Preloaded { get; private set; }
        // Rows are the preloader's field (its -1 stores are in place), not the dictionary.
        public static bool RowsByField => s_rowField != null;
        // How many of the six preloader targets were NoInlining before the module patched them.
        public static int NoInliningMarked { get; private set; }
        public const int NoInliningTargets = 6;
        // No hooked method could have been inlined into a call site compiled before Install.
        public static bool InlineSafe { get; private set; }
        // Every hook is installed and InlineSafe.
        public static bool Reliable { get; internal set; }
        public static string SelfCheck { get; private set; } = "not installed";
        public static long Head => s_head;
        public static int Subscribers => s_subscribers;
        internal static int Epoch => s_epoch;

        // ---- lab counters (cumulative ones cleared by ResetCounters) ----

        public static long Events, Coalesced, OffThread, Overruns, Recoveries, Resets, Edges;
        public static readonly long[] ByKind = new long[(int)ZdoEventKind.Reset + 1];
        // The most events recorded in one frame, and the slowest Recover (its sector-table scan).
        public static int MaxBurst;
        public static double MaxRecoverMs;
        static int s_burstFrame = -1, s_burst;

        public static void ResetCounters()
        {
            Events = Coalesced = OffThread = Overruns = Recoveries = Resets = Edges = 0;
            Array.Clear(ByKind, 0, ByKind.Length);
            MaxBurst = 0;
            MaxRecoverMs = 0;
        }

        public static string LabStats() =>
            $"zdo journal: {State()}, {s_subscribers} subscriber(s), head {s_head}, events {Events} " +
            $"(added {ByKind[(int)ZdoEventKind.Added]}, removed {ByKind[(int)ZdoEventKind.Removed]}, moved {ByKind[(int)ZdoEventKind.Moved]}, " +
            $"changed {ByKind[(int)ZdoEventKind.Changed]}, owner {ByKind[(int)ZdoEventKind.OwnerChanged]}, destroyed {ByKind[(int)ZdoEventKind.Destroyed]}, " +
            $"portal +{ByKind[(int)ZdoEventKind.PortalAdded]}/-{ByKind[(int)ZdoEventKind.PortalRemoved]}, resets {Resets}), folded {Coalesced}, " +
            $"max {MaxBurst}/frame, overruns {Overruns}, recoveries {Recoveries} (slowest {MaxRecoverMs:0.000} ms), edges {Edges}, " +
            $"off-thread {OffThread}, rows {(RowsByField ? "field" : "dictionary " + s_rowMap.Count)}; {SelfCheck}";

        static string State()
        {
            if (!Installed)
                return "not installed";
            if (!ZdoJournalModule.Feature.On)
                return "off";
            if (!s_armed)
                return "idle (no subscriber)";
            return Recording ? "recording" : "paused (world loading)";
        }

        // ---- subscription and reading ----

        // Arms the journal if this is the first subscriber (and the feature is on) and returns the cursor of the next
        // event: the caller builds its state from the game's tables now and follows the journal from here. Check
        // IsArmed: when the feature is off or the module did not install, nothing is recorded (Read and Pending say
        // NotArmed), and a cursor taken then reports Overflowed once recording starts (Recover says reset).
        public static long Subscribe()
        {
            s_subscribers++;
            Refresh();
            if (!Installed)
                EnginePlugin.Log?.LogWarning("ZdoJournal: subscribed, but the module is not installed (see its install error " +
                    "in the log): nothing is recorded, Read says NotArmed");
            // Nothing before this point may be folded into: the new reader would never see it.
            s_readFence = s_head;
            return s_head;
        }

        // The last one stops recording and frees the ring. Calls are counted, not matched to callers.
        public static void Unsubscribe()
        {
            if (s_subscribers > 0)
                s_subscribers--;
            Refresh();
        }

        // Read's and Pending's answer while the journal is not armed (IsArmed false): nothing is being recorded, so "no
        // events" would not mean "nothing changed". Cursor unchanged; once it arms the cursor reports Overflowed.
        public const int NotArmed = -2;

        // Whether events this cursor still had to read are gone: the ring lapped it, or it predates the current ring
        // (an edge: the feature was switched, or the journal had no subscriber), or it is not one this journal handed
        // out. Then Recover. False for a cursor taken since the journal disarmed (nothing is lost yet): check IsArmed.
        public static bool Overflowed(long cursor) => cursor < s_floor || cursor > s_head || s_head - cursor > Capacity;

        // Events waiting for this cursor; -1 when it overflowed, NotArmed when nothing is being recorded.
        public static int Pending(long cursor) => !s_armed ? NotArmed : Overflowed(cursor) ? -1 : (int)(s_head - cursor);

        // Appends the events from cursor on (at most max) and moves the cursor past them. Returns how many; -1 when the
        // cursor overflowed (nothing appended, cursor unchanged: Recover first); NotArmed when nothing is being recorded
        // (nothing appended, cursor unchanged: follow the game another way until IsArmed).
        public static int Read(ref long cursor, List<ZdoEvent> into, int max = int.MaxValue)
        {
            if (!s_armed)
                return NotArmed;
            if (Overflowed(cursor))
            {
                Overruns++;
                return -1;
            }
            long end = s_head;
            if (end - cursor > max)
                end = cursor + max;
            int n = 0;
            for (long s = cursor; s < end; s++, n++)
                into.Add(s_ring[(int)(s & Mask)]);
            cursor = end;
            if (end > s_readFence)
                s_readFence = end;
            return n;
        }

        // One event at a time, without a list. False at the head, when the cursor overflowed and when nothing is being
        // recorded: test IsArmed and Overflowed first (as in the loop above).
        public static bool Next(ref long cursor, out ZdoEvent e)
        {
            if (!s_armed || cursor >= s_head || Overflowed(cursor))
            {
                e = default;
                return false;
            }
            e = s_ring[(int)(cursor & Mask)];
            cursor++;
            if (cursor > s_readFence)
                s_readFence = cursor;
            return true;
        }

        // After an overflow: adds every sector named by an event since the cursor (read or lost) to sectors, says
        // whether a Reset was among them or the cursor predates the current ring (then: rebuild everything; sectors
        // is left as it was), and moves the cursor to the head. O(SectorCount), a fraction of a millisecond.
        public static void Recover(ref long cursor, List<int> sectors, out bool reset)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            reset = cursor < s_floor || cursor > s_head || s_lastReset >= cursor;
            long[] touch = s_touch;
            if (sectors != null && touch != null && !reset)
            {
                long since = cursor;
                for (int i = 0; i < touch.Length; i++)
                    if (touch[i] > since)
                        sectors.Add(i);
            }
            cursor = s_head;
            if (cursor > s_readFence)
                s_readFence = cursor;
            Recoveries++;
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (ms > MaxRecoverMs)
                MaxRecoverMs = ms;
        }

        // Moves the cursor to the head without reading: for a consumer that has just rebuilt from the game's tables (what
        // is in the ring up to now is in those tables). Nothing recorded from here on is folded into what it skipped.
        public static void Skip(ref long cursor)
        {
            cursor = s_head;
            if (cursor > s_readFence)
                s_readFence = cursor;
        }

        // The sector a zone's ZDOs are filed under, as events name it (ZoneSystem.SectorToIndex); NoSector outside the
        // 512 x 512 grid, where the game files ZDOs under sector 0 (ZDOs outside zones, InvalidateSector's list).
        public static uint SectorOf(Vector2s zone)
        {
            uint x = (uint)(zone.x + 256), y = (uint)(zone.y + 256);
            return x < 512u && y < 512u ? y * 512u + x : NoSector;
        }

        // The zone of a sector (ZoneSystem.IndexToSector).
        public static Vector2s ZoneOf(uint sector) => ZoneSystem.IndexToSector(sector);

        // ---- rows (one owner) ----

        // The first caller owns the rows (ZDO.rp_row or the dictionary); returns false when someone else does.
        public static bool ClaimRows(object owner)
        {
            if (owner == null || (s_rowOwner != null && !ReferenceEquals(s_rowOwner, owner)))
                return false;
            s_rowOwner = owner;
            return true;
        }

        // Gives the rows up; the dictionary is cleared (rows on the field stay until reset or overwritten: hints only).
        public static void ReleaseRows(object owner)
        {
            if (!ReferenceEquals(s_rowOwner, owner))
                return;
            s_rowOwner = null;
            s_rowMap.Clear();
        }

        // -1 when the ZDO has no row. Without the preloader a dictionary lookup by the ZDO's id.
        public static int GetRow(ZDO zdo) => zdo == null ? -1 : RowOf(zdo);

        // A negative row clears it. Ignored for a ZDO that is not live (pooled, id None): the pool would hand it out
        // again with the row still set.
        public static void SetRow(ZDO zdo, int row)
        {
            if (zdo == null || zdo.m_uid == ZDOID.None)
                return;
            if (row < 0)
                row = -1;
            if (s_rowField != null)
            {
                s_rowField(zdo) = row;
                return;
            }
            if (row < 0)
                s_rowMap.Remove(zdo.m_uid);
            else
                s_rowMap[zdo.m_uid] = row;
        }

        internal static int RowOf(ZDO zdo)
        {
            if (s_rowField != null)
                return s_rowField(zdo);
            return s_rowMap.Count != 0 && s_rowMap.TryGetValue(zdo.m_uid, out int row) ? row : -1;
        }

        // The ZDO went back to the pool: the dictionary's row goes with it, as the field is reset by ZDO.Reset.
        internal static void ForgetRow(ZDOID id)
        {
            if (s_rowField == null && s_rowMap.Count != 0)
                s_rowMap.Remove(id);
        }

        // Every ZDO went back to the pool (and with a new ZDOMan the ids mean something else).
        internal static void ForgetAllRows() => s_rowMap.Clear();

        // ---- recording ----

        internal static void MarkMainThread() => t_main = true;

        // Without OnMainThread's side effects (counting, the gap).
        internal static bool IsMainThread => t_main;

        // Arms iff installed, on and subscribed to; records iff armed and no world load is running. Arming and disarming
        // are edges: the ring is made or dropped and the sequence skips one number, so every cursor from before reports
        // Overflowed (nothing across the edge was recorded). A world load only pauses (it ends with a Reset event).
        internal static void Refresh()
        {
            bool armed = Installed && ZdoJournalModule.Feature.On && s_subscribers > 0;
            if (armed != s_armed)
            {
                s_armed = armed;
                s_epoch++;
                Edges++;
                ZdoJournalModule.ForgetInFlight();
                s_head++;
                s_floor = s_head;
                s_readFence = s_head;
                s_gapPending = false;
                if (armed)
                {
                    // Kept across a feature switch while anyone is subscribed (lab A/B toggles allocate nothing): what
                    // is in them is from before the new floor, so no cursor reads it, nothing folds into it and Recover
                    // does not report it.
                    s_ring ??= new ZdoEvent[Capacity];
                    s_touch ??= new long[SectorCount];
                }
                else
                {
                    s_rowMap.Clear();
                }
            }
            // Dropped once nobody is subscribed and nothing records: at the disarm edge, or later when the feature went
            // off first and the last subscriber left afterwards (no edge then). Nothing reads them while disarmed (Read and
            // Next return before the ring, Recover tests s_touch) and arming makes them again.
            if (!s_armed && s_subscribers == 0 && s_ring != null)
            {
                s_ring = null;
                s_touch = null;
            }
            bool record = armed && LoadDepth == 0;
            if (record != Recording)
            {
                s_epoch++;
                ZdoJournalModule.ForgetInFlight();
                Recording = record;
            }
        }

        // False (and every reader resynchronises later) on any thread but the main one.
        internal static bool OnMainThread()
        {
            if (t_main)
                return true;
            Interlocked.Increment(ref OffThread);
            s_gapPending = true;
            return false;
        }

        // Unity's frame counter; its own method so an out-of-game harness can stand in for it.
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int Frame() => Time.frameCount;

        internal static void PutReset(ZdoEventFlags flags)
        {
            if (OnMainThread())
                Put(ZdoEventKind.Reset, null, ZDOID.None, NoSector, NoSector, flags, -1);
        }

        // Changed / OwnerChanged: folded into the newest event when that is the same kind for the same ZDO and nobody
        // has read it yet. A new Changed of a portal prefab gets Flags.Portal (a folded one keeps what the first had: the
        // game sets a ZDO's prefab before its other writes, SetPrefab in ZNetView.Awake, Deserialize for a peer's copy).
        internal static void PutChange(ZdoEventKind kind, ZDO zdo, ZdoEventFlags flags)
        {
            if (zdo == null || !OnMainThread())
                return;
            ZDOID id = zdo.m_uid;
            if (id == ZDOID.None)
                return;
            if (RemoteDepth > 0)
                flags |= ZdoEventFlags.Remote;
            long last = s_head - 1;
            if (last >= s_readFence && !s_gapPending)
            {
                ref ZdoEvent e = ref s_ring[(int)(last & Mask)];
                if (e.Kind == kind && (object)e.Zdo == zdo && e.Id == id)
                {
                    e.Flags |= flags;
                    Coalesced++;
                    return;
                }
            }
            if (kind == ZdoEventKind.Changed && ZdoJournalModule.IsPortalPrefab(zdo.GetPrefab()))
                flags |= ZdoEventFlags.Portal;
            uint sector = ZoneSystem.GetSectorIndex(zdo.GetPosition()).Sector;
            Put(kind, zdo, id, sector, sector, flags, RowOf(zdo));
        }

        // Main thread, Recording.
        internal static void Put(ZdoEventKind kind, ZDO zdo, ZDOID id, uint from, uint to, ZdoEventFlags flags, int row)
        {
            if (s_gapPending)
            {
                s_gapPending = false;
                Write(ZdoEventKind.Reset, null, ZDOID.None, NoSector, NoSector, ZdoEventFlags.Gap, -1);
            }
            if (RemoteDepth > 0 && kind != ZdoEventKind.Reset)
                flags |= ZdoEventFlags.Remote;
            Write(kind, zdo, id, from, to, flags, row);
        }

        static void Write(ZdoEventKind kind, ZDO zdo, ZDOID id, uint from, uint to, ZdoEventFlags flags, int row)
        {
            long seq = s_head;
            int frame = Frame();
            ref ZdoEvent e = ref s_ring[(int)(seq & Mask)];
            e.Zdo = zdo;
            e.Id = id;
            e.Kind = kind;
            e.Flags = flags;
            e.OldSector = from;
            e.NewSector = to;
            e.Row = row;
            e.Frame = frame;
            s_head = seq + 1;
            long[] touch = s_touch;
            if (from < (uint)touch.Length)
                touch[from] = seq + 1;
            if (to < (uint)touch.Length)
                touch[to] = seq + 1;
            if (kind == ZdoEventKind.Reset)
            {
                s_lastReset = seq;
                Resets++;
            }
            Events++;
            ByKind[(int)kind]++;
            if (frame != s_burstFrame)
            {
                s_burstFrame = frame;
                s_burst = 0;
            }
            if (++s_burst > MaxBurst)
                MaxBurst = s_burst;
        }

        // ---- self-check (Install, before anything is patched: Harmony marks what it patches NoInlining itself) ----

        // marked: the preloader's six NoInlining targets; hooked: the other hooked methods (only checked for inline risk).
        internal static void RunSelfCheck(MethodBase[] marked, MethodBase[] hooked, MethodBase reset)
        {
            var notes = new List<string>();
            FieldInfo field = typeof(ZDO).GetField(RowFieldName, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Preloaded = field != null;
            bool usable = field != null && !field.IsStatic && field.FieldType == typeof(int);
            int ctors = 0, ctorStores = 0;
            bool resetStores = false;
            if (usable)
            {
                resetStores = reset != null && StoresField(reset, field);
                foreach (ConstructorInfo c in typeof(ZDO).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    ctors++;
                    if (StoresField(c, field))
                        ctorStores++;
                }
            }
            s_rowField = null;
            if (usable && resetStores && ctors > 0 && ctorStores == ctors)
            {
                try
                {
                    s_rowField = AccessTools.FieldRefAccess<ZDO, int>(field);
                }
                catch (Exception e)
                {
                    notes.Add("field access failed: " + e.Message);
                }
            }
            if (!Preloaded)
                notes.Add("preloader: no (ZDO has no " + RowFieldName + "; is RealPortal.Preloader.dll in BepInEx\\patchers?)");
            else if (!usable)
                notes.Add($"preloader: ZDO.{RowFieldName} is {(field.IsStatic ? "static " : "")}{field.FieldType.Name}, not usable");
            else
                notes.Add($"preloader: yes (ZDO.{RowFieldName}; -1 in Reset {(resetStores ? "yes" : "NO")}, in {ctorStores}/{ctors} constructor(s))");
            notes.Add(s_rowField != null ? "rows: field" : "rows: dictionary");

            int limit = InlineLimit();
            int count = 0;
            var risky = new List<string>();
            void Check(MethodBase m, bool target)
            {
                if (m == null)
                    return;
                MethodImplAttributes impl = m.MethodImplementationFlags;
                if ((impl & MethodImplAttributes.NoInlining) != 0)
                {
                    if (target)
                        count++;
                    return;
                }
                int size = ILSize(m);
                if ((impl & MethodImplAttributes.AggressiveInlining) != 0 || size < limit)
                    risky.Add($"{m.DeclaringType?.Name}.{m.Name} ({size} IL bytes)");
            }
            foreach (MethodBase m in marked)
                Check(m, true);
            foreach (MethodBase m in hooked)
                Check(m, false);
            NoInliningMarked = count;
            InlineSafe = risky.Count == 0;
            notes.Add($"NoInlining {count}/{marked.Length}" + (count == marked.Length && !Preloaded ? " (marked by another mod's patch)" : ""));
            notes.Add(InlineSafe ? "inline risk: none" : $"inline risk (Mono inlines below {limit} IL bytes): {string.Join(", ", risky)}");
            SelfCheck = string.Join("; ", notes);
        }

        static int InlineLimit()
        {
            string v = Environment.GetEnvironmentVariable("MONO_INLINELIMIT");
            return int.TryParse(v, out int n) && n > 0 ? n : 20;
        }

        static int ILSize(MethodBase m)
        {
            try
            {
                return m.GetMethodBody()?.GetILAsByteArray()?.Length ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        // Whether the IL has `stfld <field>` (0x7D and the field's token).
        static bool StoresField(MethodBase m, FieldInfo field)
        {
            byte[] il;
            try
            {
                il = m.GetMethodBody()?.GetILAsByteArray();
            }
            catch
            {
                return false;
            }
            if (il == null)
                return false;
            int token = field.MetadataToken;
            for (int i = 0; i + 4 < il.Length; i++)
                if (il[i] == 0x7D && BitConverter.ToInt32(il, i + 1) == token)
                    return true;
            return false;
        }
    }

    // One change of one sector's content as ZDOMan.FindSectorObjects sees it (the sector list and the portal list).
    public enum ZdoChangeKind : byte
    {
        None = 0,
        Created = 1,        // came into the sector from no sector: a new ZDO (CreateNewZDO, this game's or a peer's copy),
                            // or a mod's own AddToSector
        MovedIn = 2,        // Other: the sector it came from (0: outside zones, where a peer's InvalidateSector puts ZDOs)
        MovedOut = 3,       // Other: the sector it went to (0: outside zones)
        Removed = 4,        // left the sector for no sector: right before its Destroyed, or a mod's RemoveFromSector
        Changed = 5,        // its data: a local write (Flags.Revision) or a peer's copy (Flags.Remote)
        OwnerChanged = 6,
        Destroyed = 7,      // gone for good; follows its Removed (and a portal's PortalRemoved). The ZDO object is back
                            // in the pool: use Id
        PortalAdded = 8,    // a portal went on the sector's portal list (it is in the sector already, from its Created)
        PortalRemoved = 9,  // off it, right before its Destroyed
    }

    public struct ZdoChange
    {
        // As in ZdoEvent: check IsCurrent before reading it (any kind), otherwise go by Id.
        public ZDO Zdo;
        public ZDOID Id;
        // The sector this change is about, and for MovedIn/MovedOut the other side (else ZdoJournal.NoSector).
        public uint Sector;
        public uint Other;
        public ZdoChangeKind Kind;
        public ZdoEventFlags Flags;
        // The ZDO's row (ZdoJournal.SetRow) when the event was recorded, -1 when none.
        public int Row;
        public int Frame;

        public bool IsCurrent => Zdo != null && Id != ZDOID.None && Zdo.m_uid == Id;
        // A peer's copy or destroy (inside ZDOMan.RPC_ZDOData or a peer's RPC_DestroyZDO).
        public bool IsRemote => (Flags & ZdoEventFlags.Remote) != 0;
        public bool Enters => Kind == ZdoChangeKind.Created || Kind == ZdoChangeKind.MovedIn;
        // Removed and then Destroyed both say it left: treat leaving as idempotent.
        public bool Leaves => Kind == ZdoChangeKind.MovedOut || Kind == ZdoChangeKind.Removed || Kind == ZdoChangeKind.Destroyed;

        public override string ToString() =>
            $"{Kind}{(Flags != ZdoEventFlags.None ? "[" + Flags + "]" : "")} {Id} sector {Sector}" +
            $"{(Other != ZdoJournal.NoSector ? (Kind == ZdoChangeKind.MovedIn ? " from " : " to ") + Other : "")} row {Row} frame {Frame}";
    }

    public enum ZdoDrainResult : byte
    {
        Changes = 0,        // the changes since the last drain were appended (maybe none, maybe up to the budget only)
        NotArmed = 1,       // nothing is being recorded (ZdoJournal.IsArmed false, or the reader is disposed): nothing
                            // appended; follow the game another way until it arms (the first drain then says Reset)
        Recovered = 2,      // the ring lapped the reader: nothing appended; rebuild DirtySectors (watched sectors any
                            // lost event named) from the game's tables, and the portals when PortalsDirty
        Reset = 3,          // rebuild everything from the game's tables now (a world came or went, events were missed,
                            // the journal was switched): nothing appended, the reader follows from here
    }

    // A subscriber that drains the journal once a frame into per-sector changes (see the journal's header). Main thread.
    //
    //   var reader = new ZdoJournalReader();           // subscribes; build your state from the game's tables now
    //   reader.Watch(sector) / Unwatch(sector)          // the sectors you keep records for (or WatchAll)
    //   ...each frame:
    //   switch (reader.Drain(changes, budget)) {
    //       case ZdoDrainResult.NotArmed:  follow the game another way (scan, audit); break;
    //       case ZdoDrainResult.Reset:     rebuild every record from ZDOMan; break;
    //       case ZdoDrainResult.Recovered: rebuild reader.DirtySectors (and the portals if reader.PortalsDirty); break;
    //       default: foreach (ZdoChange c in changes) ...  // c.Zdo only if c.IsCurrent; c.Enters, c.Leaves
    //   }
    //   ...
    //   reader.Dispose();                               // unsubscribes
    public sealed class ZdoJournalReader : IDisposable
    {
        const int Words = ZdoJournal.SectorCount / 64;

        long m_cursor;
        bool m_open;
        // One bit per sector, made by the first Watch.
        ulong[] m_watch;
        int m_watched;
        readonly List<int> m_touched = new List<int>();

        // Every sector counts, watched or not.
        public bool WatchAll;
        // Portal events (PortalAdded, PortalRemoved, and Changed, OwnerChanged and Destroyed carrying Flags.Portal) pass
        // whatever the watch: links are found by portal, wherever it stands.
        public bool PortalsAlways = true;
        // After a Recovered drain: the watched sectors (all touched ones with WatchAll) that an event the reader lost
        // named, and whether lost events may have been about portals elsewhere (PortalsAlways). Cleared by every drain.
        public readonly List<int> DirtySectors = new List<int>();
        public bool PortalsDirty { get; private set; }

        // Lab: events read, changes handed out, events the watch left out, recoveries and resets seen, events in the last
        // drain and the most in one.
        public long EventsRead, ChangesOut, Filtered, Recoveries, Resets;
        public int LastEvents, MaxEvents;

        public ZdoJournalReader()
        {
            m_cursor = ZdoJournal.Subscribe();
            m_open = true;
        }

        public bool IsOpen => m_open;
        public long Cursor => m_cursor;
        public int WatchedSectors => m_watched;
        // Events waiting (ZdoJournal.Pending): -1 when lapped, NotArmed while nothing records.
        public int Pending => m_open ? ZdoJournal.Pending(m_cursor) : ZdoJournal.NotArmed;

        public void Watch(uint sector)
        {
            if (sector >= ZdoJournal.SectorCount)
                return;
            m_watch ??= new ulong[Words];
            ulong bit = 1UL << (int)(sector & 63);
            ref ulong word = ref m_watch[sector >> 6];
            if ((word & bit) != 0)
                return;
            word |= bit;
            m_watched++;
        }

        public void Unwatch(uint sector)
        {
            if (sector >= ZdoJournal.SectorCount || m_watch == null)
                return;
            ulong bit = 1UL << (int)(sector & 63);
            ref ulong word = ref m_watch[sector >> 6];
            if ((word & bit) == 0)
                return;
            word &= ~bit;
            m_watched--;
        }

        public void Watch(Vector2s zone) => Watch(ZdoJournal.SectorOf(zone));

        public void Unwatch(Vector2s zone) => Unwatch(ZdoJournal.SectorOf(zone));

        public bool IsWatched(uint sector) =>
            WatchAll || (m_watched > 0 && sector < ZdoJournal.SectorCount && (m_watch[sector >> 6] & (1UL << (int)(sector & 63))) != 0);

        public void ClearWatch()
        {
            if (m_watch != null)
                Array.Clear(m_watch, 0, m_watch.Length);
            m_watched = 0;
        }

        // The consumer rebuilt from the game's tables (for its own reasons): follow from now on, skipping what is
        // waiting.
        public void Restart()
        {
            if (m_open)
                ZdoJournal.Skip(ref m_cursor);
        }

        // Appends the changes of the events since the last drain (at most maxEvents events; the rest waits for the next
        // drain) to `into` and says what the consumer has to do (ZdoDrainResult). Only Changes appends anything.
        public ZdoDrainResult Drain(List<ZdoChange> into, int maxEvents = int.MaxValue)
        {
            DirtySectors.Clear();
            PortalsDirty = false;
            LastEvents = 0;
            if (!m_open || !ZdoJournal.IsArmed)
                return ZdoDrainResult.NotArmed;
            if (ZdoJournal.Overflowed(m_cursor))
            {
                m_touched.Clear();
                ZdoJournal.Recover(ref m_cursor, m_touched, out bool reset);
                if (reset)
                {
                    m_touched.Clear();
                    Resets++;
                    return ZdoDrainResult.Reset;
                }
                for (int i = 0; i < m_touched.Count; i++)
                    if (IsWatched((uint)m_touched[i]))
                        DirtySectors.Add(m_touched[i]);
                m_touched.Clear();
                PortalsDirty = PortalsAlways;
                Recoveries++;
                return ZdoDrainResult.Recovered;
            }
            int start = into.Count;
            int n = 0;
            while (n < maxEvents && ZdoJournal.Next(ref m_cursor, out ZdoEvent e))
            {
                n++;
                if (e.Kind == ZdoEventKind.Reset)
                {
                    // What came before is void and what came after is in the game's tables already.
                    into.RemoveRange(start, into.Count - start);
                    ZdoJournal.Skip(ref m_cursor);
                    Count(n);
                    Resets++;
                    return ZdoDrainResult.Reset;
                }
                Emit(in e, into);
            }
            Count(n);
            ChangesOut += into.Count - start;
            return ZdoDrainResult.Changes;
        }

        void Count(int n)
        {
            LastEvents = n;
            EventsRead += n;
            if (n > MaxEvents)
                MaxEvents = n;
        }

        void Emit(in ZdoEvent e, List<ZdoChange> into)
        {
            bool portal = PortalsAlways && (e.Flags & ZdoEventFlags.Portal) != 0;
            int before = into.Count;
            switch (e.Kind)
            {
                case ZdoEventKind.Added:
                    if (IsWatched(e.NewSector))
                        Put(into, in e, ZdoChangeKind.Created, e.NewSector, ZdoJournal.NoSector);
                    break;
                case ZdoEventKind.Removed:
                    // Off the sector list onto the portal list of the same sector: still in it for FindSectorObjects.
                    if ((e.Flags & ZdoEventFlags.PortalList) == 0 && IsWatched(e.OldSector))
                        Put(into, in e, ZdoChangeKind.Removed, e.OldSector, ZdoJournal.NoSector);
                    break;
                case ZdoEventKind.Moved:
                    if (IsWatched(e.OldSector))
                        Put(into, in e, ZdoChangeKind.MovedOut, e.OldSector, e.NewSector);
                    if (IsWatched(e.NewSector))
                        Put(into, in e, ZdoChangeKind.MovedIn, e.NewSector, e.OldSector);
                    break;
                case ZdoEventKind.Changed:
                    if (portal || IsWatched(e.NewSector))
                        Put(into, in e, ZdoChangeKind.Changed, e.NewSector, ZdoJournal.NoSector);
                    break;
                case ZdoEventKind.OwnerChanged:
                    if (portal || IsWatched(e.NewSector))
                        Put(into, in e, ZdoChangeKind.OwnerChanged, e.NewSector, ZdoJournal.NoSector);
                    break;
                case ZdoEventKind.Destroyed:
                    if (portal || IsWatched(e.OldSector))
                        Put(into, in e, ZdoChangeKind.Destroyed, e.OldSector, ZdoJournal.NoSector);
                    break;
                case ZdoEventKind.PortalAdded:
                    if (PortalsAlways || IsWatched(e.NewSector))
                        Put(into, in e, ZdoChangeKind.PortalAdded, e.NewSector, ZdoJournal.NoSector);
                    break;
                case ZdoEventKind.PortalRemoved:
                    if (PortalsAlways || IsWatched(e.OldSector))
                        Put(into, in e, ZdoChangeKind.PortalRemoved, e.OldSector, ZdoJournal.NoSector);
                    break;
            }
            if (into.Count == before)
                Filtered++;
        }

        static void Put(List<ZdoChange> into, in ZdoEvent e, ZdoChangeKind kind, uint sector, uint other)
        {
            into.Add(new ZdoChange
            {
                Zdo = e.Zdo,
                Id = e.Id,
                Sector = sector,
                Other = other,
                Kind = kind,
                Flags = e.Flags,
                Row = e.Row,
                Frame = e.Frame,
            });
        }

        // Unsubscribes (the last subscriber stops the journal). Safe to call twice.
        public void Dispose()
        {
            if (!m_open)
                return;
            m_open = false;
            ZdoJournal.Unsubscribe();
        }

        public string Stats() =>
            $"zdo reader: {(m_open ? "open" : "disposed")}, {(WatchAll ? "all sectors" : m_watched + " sector(s)")} watched, " +
            $"pending {Pending}, read {EventsRead} (last {LastEvents}, max {MaxEvents}), changes {ChangesOut}, filtered {Filtered}, " +
            $"recoveries {Recoveries}, resets {Resets}";
    }

    [EngineModule]
    public static class ZdoJournalModule
    {
        public static readonly Feature Feature = Features.Register("ZdoJournal", true,
            "Journal of ZDO sector, data, owner, destroy and portal-list changes for subscribers (Real Portal's FarWorld). " +
            "Records only while something is subscribed; until then each hook is one flag test. Off: nothing is recorded " +
            "and subscribers resynchronise when it comes back on.");

        static void Install(Harmony harmony)
        {
            Type zdo = typeof(ZDO), man = typeof(ZDOMan);
            Type[] zdoAndSector = { typeof(ZDO), typeof(ZoneSystem.SectorIndex) };
            // Looked up before anything is patched: a game update that renamed one leaves the module out whole.
            MethodInfo dataRevision = Need(AccessTools.DeclaredMethod(zdo, "IncreaseDataRevision", Type.EmptyTypes), "ZDO.IncreaseDataRevision");
            MethodInfo ownerRevision = Need(AccessTools.DeclaredMethod(zdo, "IncreaseOwnerRevision", Type.EmptyTypes), "ZDO.IncreaseOwnerRevision");
            MethodInfo setSector = Need(AccessTools.DeclaredMethod(zdo, "SetSector", new[] { typeof(ZoneSystem.SectorIndex) }), "ZDO.SetSector");
            MethodInfo addToSector = Need(AccessTools.DeclaredMethod(man, nameof(ZDOMan.AddToSector), zdoAndSector), "ZDOMan.AddToSector");
            MethodInfo initialAdd = Need(AccessTools.DeclaredMethod(man, "InitialAddToSector", zdoAndSector), "ZDOMan.InitialAddToSector");
            MethodInfo removeFromSector = Need(AccessTools.DeclaredMethod(man, nameof(ZDOMan.RemoveFromSector), zdoAndSector), "ZDOMan.RemoveFromSector");
            MethodInfo deserialize = Need(AccessTools.DeclaredMethod(zdo, nameof(ZDO.Deserialize), new[] { typeof(ZPackage) }), "ZDO.Deserialize");
            MethodInfo destroyed = Need(AccessTools.DeclaredMethod(man, "HandleDestroyedZDO", new[] { typeof(ZDOID) }), "ZDOMan.HandleDestroyedZDO");
            MethodInfo addIfPortal = Need(AccessTools.DeclaredMethod(man, "AddIfPortal", new[] { typeof(ZDO), typeof(int) }), "ZDOMan.AddIfPortal");
            // Self-check only.
            MethodInfo reset = AccessTools.DeclaredMethod(zdo, nameof(ZDO.Reset), Type.EmptyTypes);
            // Optional: without one the journal still works, less completely (see the warning). Reliable says so.
            MethodInfo setOwner = AccessTools.DeclaredMethod(zdo, nameof(ZDO.SetOwnerInternal), new[] { typeof(long) });
            MethodInfo remoteData = AccessTools.DeclaredMethod(man, "RPC_ZDOData", new[] { typeof(ZRpc), typeof(ZPackage) });
            MethodInfo remoteDestroy = AccessTools.DeclaredMethod(man, "RPC_DestroyZDO", new[] { typeof(long), typeof(ZPackage) });
            FieldInfo session = AccessTools.DeclaredField(man, "m_sessionID");
            if (session != null && session.FieldType == typeof(long) && !session.IsStatic)
                s_sessionOf = AccessTools.FieldRefAccess<ZDOMan, long>(session);
            MethodInfo shutDown = AccessTools.DeclaredMethod(man, nameof(ZDOMan.ShutDown), Type.EmptyTypes);
            ConstructorInfo create = AccessTools.DeclaredConstructor(man, new[] { typeof(int) });
            MethodInfo loadChunks = AccessTools.DeclaredMethod(man, nameof(ZDOMan.LoadChunks), new[] { typeof(string), typeof(FileHelpers.FileSource), typeof(global::Version.World) });
            MethodInfo load = AccessTools.DeclaredMethod(man, nameof(ZDOMan.Load), new[] { typeof(BinaryReader), typeof(global::Version.World) });

            // Install runs on the main thread (the plugin's Awake): the hooks record only there.
            ZdoJournal.MarkMainThread();
            ZdoJournal.RunSelfCheck(new MethodBase[] { dataRevision, ownerRevision, setSector, addToSector, initialAdd, removeFromSector },
                new MethodBase[] { deserialize, destroyed, addIfPortal, setOwner, remoteData, remoteDestroy }, reset);

            Feature.Switched += _ => ZdoJournal.Refresh();

            Type t = typeof(ZdoJournalModule);
            HarmonyMethod Own(string name) => new HarmonyMethod(t, name);
            // Observers run after other mods' postfixes, so they report what those left.
            HarmonyMethod Last(string name) => new HarmonyMethod(t, name) { priority = Priority.Last };
            var attempted = new List<KeyValuePair<MethodBase, MethodInfo>>();
            void Patch(MethodBase original, HarmonyMethod prefix = null, HarmonyMethod postfix = null, HarmonyMethod finalizer = null)
            {
                foreach (HarmonyMethod m in new[] { prefix, postfix, finalizer })
                    if (m != null)
                        attempted.Add(new KeyValuePair<MethodBase, MethodInfo>(original, m.method));
                harmony.Patch(original, prefix: prefix, postfix: postfix, finalizer: finalizer);
            }
            var left = new List<string>();
            try
            {
                Patch(addToSector, postfix: Last(nameof(AfterAddToSector)));
                Patch(initialAdd, postfix: Last(nameof(AfterAddToSector)));
                Patch(removeFromSector, postfix: Last(nameof(AfterRemoveFromSector)));
                Patch(setSector, prefix: Own(nameof(BeforeSetSector)), finalizer: Own(nameof(SetSectorFinalizer)));
                Patch(dataRevision, postfix: Last(nameof(AfterDataRevision)));
                Patch(ownerRevision, postfix: Last(nameof(AfterOwnerRevision)));
                Patch(deserialize, postfix: Last(nameof(AfterDeserialize)));
                Patch(destroyed, prefix: Own(nameof(BeforeDestroyed)), finalizer: Own(nameof(DestroyedFinalizer)));
                Patch(addIfPortal, prefix: Own(nameof(BeforeAddIfPortal)), postfix: Last(nameof(AfterAddIfPortal)));
                if (setOwner != null)
                    Patch(setOwner, prefix: Own(nameof(BeforeSetOwner)), postfix: Last(nameof(AfterSetOwner)));
                else
                    left.Add("ZDO.SetOwnerInternal (a new ZDO's first owner and a peer's handover are not journalled)");
                if (remoteData != null)
                    Patch(remoteData, prefix: Own(nameof(BeforeRemoteData)), finalizer: Own(nameof(RemoteDataFinalizer)));
                else
                    left.Add("ZDOMan.RPC_ZDOData (no event carries Flags.Remote)");
                if (remoteDestroy != null && s_sessionOf != null)
                    Patch(remoteDestroy, prefix: Own(nameof(BeforeRemoteDestroy)), finalizer: Own(nameof(RemoteDataFinalizer)));
                else
                    left.Add("ZDOMan.RPC_DestroyZDO or ZDOMan.m_sessionID (a peer's destroys do not carry Flags.Remote)");
                if (shutDown != null)
                    Patch(shutDown, finalizer: Own(nameof(AfterWorldGone)));
                else
                    left.Add("ZDOMan.ShutDown (no Reset when the world closes)");
                if (create != null)
                    Patch(create, postfix: Last(nameof(AfterWorldGone)));
                else
                    left.Add("ZDOMan(int) (no Reset for a new session)");
                if (loadChunks != null)
                    Patch(loadChunks, prefix: Own(nameof(BeforeLoad)), finalizer: Own(nameof(LoadFinalizer)));
                else
                    left.Add("ZDOMan.LoadChunks (a world load is journalled ZDO by ZDO and overflows)");
                if (load != null)
                    Patch(load, prefix: Own(nameof(BeforeLoad)), finalizer: Own(nameof(LoadFinalizer)));
                else
                    left.Add("ZDOMan.Load (an old-format world load is journalled ZDO by ZDO and overflows)");
            }
            catch
            {
                // Out whole, as the plugin reports: take back what went in, only our own patch methods (the Harmony
                // instance is shared with the other modules). Anything that cannot be taken back stays inert
                // (Installed is false, so nothing records).
                foreach (KeyValuePair<MethodBase, MethodInfo> p in attempted)
                {
                    try
                    {
                        harmony.Unpatch(p.Key, p.Value);
                    }
                    catch (Exception e)
                    {
                        EnginePlugin.Log?.LogWarning($"ZdoJournal: could not take back {p.Value.Name} on {p.Key.Name}: {e.Message}");
                    }
                }
                throw;
            }
            ZdoJournal.Reliable = ZdoJournal.InlineSafe && left.Count == 0;
            ZdoJournal.Installed = true;
            ZdoJournal.Refresh();
            EnginePlugin.Log?.LogInfo("ZdoJournal: " + ZdoJournal.SelfCheck);
            if (!ZdoJournal.RowsByField && ZdoJournal.Preloaded)
                EnginePlugin.Log?.LogWarning("ZdoJournal: the preloader's ZDO.rp_row is there but not reset everywhere it must be; rows use the dictionary");
            if (!ZdoJournal.InlineSafe)
                EnginePlugin.Log?.LogWarning("ZdoJournal: a hooked method may have been inlined by the JIT, so events can be missed " +
                    "(consumers rely on their audit); install RealPortal.Preloader.dll in BepInEx\\patchers");
            foreach (string l in left)
                EnginePlugin.Log?.LogWarning("ZdoJournal: not found, left out: " + l);
        }

        static T Need<T>(T member, string name) where T : MemberInfo =>
            member ?? throw new MissingMethodException(name);

        // ---- calls in flight (main thread) ----

        // SetSector between the prefix that owns the capture and its finalizer: the RemoveFromSector/AddToSector of that
        // ZDO inside it are captured, not recorded. Only the outermost SetSector captures; a nested one (another mod
        // moving another ZDO from a sector hook) records its halves as plain Removed/Added.
        static ZDO s_moving;
        static uint s_movedFrom, s_movedTo;
        static int s_moveSeen;   // 1: removed, 2: added

        // AddIfPortal between the prefix that saw a portal not on the list yet and its postfix: the RemoveFromSector of
        // that ZDO inside it is the move from the sector list to the portal list (Flags.PortalList). Used once.
        static ZDO s_listing;

        // RPC_DestroyZDO's sender is compared with this session's id (ZDOMan.m_sessionID, private readonly).
        static AccessTools.FieldRef<ZDOMan, long> s_sessionOf;

        // Recording started or stopped: whatever was half seen belongs to the old state.
        internal static void ForgetInFlight()
        {
            s_moving = null;
            s_moveSeen = 0;
            s_listing = null;
        }

        // HandleDestroyedZDO's prefix to its finalizer, in Harmony's __state (so nested destroys cannot mix). Public only
        // because Harmony's generated method declares a local of it.
        public struct DestroyCapture
        {
            internal ZDO Zdo;
            internal ZDOID Id;
            internal uint Sector;
            internal int Row;
            internal int Epoch;
            internal bool Portal, Listed;
        }

        // ---- hooks: one static bool test each while nothing records; the work is in the NoInlining methods ----

        static void AfterAddToSector(ZDO zdo, ZoneSystem.SectorIndex sectorIndex, bool __runOriginal)
        {
            if (ZdoJournal.Recording && __runOriginal)
                Added(zdo, sectorIndex.Sector);
        }

        static void AfterRemoveFromSector(ZDO zdo, ZoneSystem.SectorIndex sectorIndex, bool __runOriginal)
        {
            if (ZdoJournal.Recording && __runOriginal)
                Removed(zdo, sectorIndex.Sector);
        }

        // __state: this call owns the capture.
        static void BeforeSetSector(ZDO __instance, out bool __state)
        {
            __state = ZdoJournal.Recording && BeginMove(__instance);
        }

        // A finalizer, so a SetSector that throws part way still reports the half that happened and clears the capture.
        static void SetSectorFinalizer(ZDO __instance, bool __state)
        {
            if (__state)
                EndMove(__instance);
        }

        static void AfterDataRevision(ZDO __instance, bool __runOriginal)
        {
            if (ZdoJournal.Recording && __runOriginal)
                ZdoJournal.PutChange(ZdoEventKind.Changed, __instance, ZdoEventFlags.Revision);
        }

        static void AfterOwnerRevision(ZDO __instance, bool __runOriginal)
        {
            if (ZdoJournal.Recording && __runOriginal)
                ZdoJournal.PutChange(ZdoEventKind.OwnerChanged, __instance, ZdoEventFlags.Revision);
        }

        // __state: the owner is about to change. RPC_ZDOData calls SetOwnerInternal for every update it takes in, nearly
        // always with the owner the ZDO has: those are not owner changes and record nothing.
        static void BeforeSetOwner(ZDO __instance, long uid, out bool __state)
        {
            __state = ZdoJournal.Recording && OwnerChanges(__instance, uid);
        }

        static void AfterSetOwner(ZDO __instance, bool __state, bool __runOriginal)
        {
            if (__state && ZdoJournal.Recording && __runOriginal)
                ZdoJournal.PutChange(ZdoEventKind.OwnerChanged, __instance, ZdoEventFlags.None);
        }

        // Flags.Remote comes from RemoteDepth (inside RPC_ZDOData, the only caller in the game).
        static void AfterDeserialize(ZDO __instance, bool __runOriginal)
        {
            if (ZdoJournal.Recording && __runOriginal)
                ZdoJournal.PutChange(ZdoEventKind.Changed, __instance, ZdoEventFlags.None);
        }

        static void BeforeDestroyed(ZDOMan __instance, ZDOID uid, out DestroyCapture __state)
        {
            __state = default;
            if (ZdoJournal.Recording)
                BeginDestroy(__instance, uid, ref __state);
        }

        static void DestroyedFinalizer(ZDOMan __instance, DestroyCapture __state)
        {
            if (__state.Zdo != null)
                EndDestroy(__instance, ref __state);
        }

        // __state: the prefix saw a portal that is not on the list yet (and captured it for the RemoveFromSector inside).
        static void BeforeAddIfPortal(ZDOMan __instance, ZDO zdo, int prefabHash, out bool __state)
        {
            __state = ZdoJournal.Recording && NotListedPortal(__instance, zdo, prefabHash);
        }

        // Also when the call was skipped or recording stopped inside it: the capture is let go either way.
        static void AfterAddIfPortal(ZDOMan __instance, ZDO zdo, bool __state, bool __runOriginal)
        {
            if (__state)
                PortalListed(__instance, zdo, __runOriginal);
        }

        // Kept whether or not anything records (a few calls a second). __state: this prefix ran, so the finalizer
        // undoes exactly what it did even when an earlier prefix threw.
        static void BeforeRemoteData(out bool __state)
        {
            ZdoJournal.RemoteDepth++;
            __state = true;
        }

        static void RemoteDataFinalizer(bool __state)
        {
            if (__state && ZdoJournal.RemoteDepth > 0)
                ZdoJournal.RemoteDepth--;
        }

        // Kept whether or not anything records, as above. __state: a peer sent it, so this prefix counted it as remote
        // (this game's own destroys come back through the RPC with its own session id); the finalizer is RemoteData's.
        static void BeforeRemoteDestroy(ZDOMan __instance, long sender, out bool __state)
        {
            __state = __instance != null && sender != s_sessionOf(__instance);
            if (__state)
                ZdoJournal.RemoteDepth++;
        }

        // ShutDown's finalizer (also when it threw part way) and ZDOMan(int)'s postfix.
        static void AfterWorldGone()
        {
            ForgetInFlight();
            ZdoJournal.ForgetAllRows();
            if (ZdoJournal.Recording)
                ZdoJournal.PutReset(ZdoEventFlags.World);
        }

        static void BeforeLoad(out bool __state)
        {
            ZdoJournal.LoadDepth++;
            __state = true;
            ZdoJournal.Refresh();
        }

        // Also after a load that threw: whatever it left is what the consumers rebuild from.
        static void LoadFinalizer(bool __state)
        {
            if (!__state || ZdoJournal.LoadDepth == 0)
                return;
            ZdoJournal.LoadDepth--;
            ZdoJournal.ForgetAllRows();
            ZdoJournal.Refresh();
            if (ZdoJournal.Recording && ZdoJournal.LoadDepth == 0)
                ZdoJournal.PutReset(ZdoEventFlags.World);
        }

        // ---- the work ----

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Added(ZDO zdo, uint sector)
        {
            if (zdo == null || !ZdoJournal.OnMainThread())
                return;
            if ((object)zdo == s_moving && (s_moveSeen & 2) == 0)
            {
                s_movedTo = sector;
                s_moveSeen |= 2;
                return;
            }
            ZDOID id = zdo.m_uid;
            if (id != ZDOID.None)
                ZdoJournal.Put(ZdoEventKind.Added, zdo, id, ZdoJournal.NoSector, sector, ZdoEventFlags.None, ZdoJournal.RowOf(zdo));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Removed(ZDO zdo, uint sector)
        {
            if (zdo == null || !ZdoJournal.OnMainThread())
                return;
            if ((object)zdo == s_moving && (s_moveSeen & 1) == 0)
            {
                s_movedFrom = sector;
                s_moveSeen |= 1;
                return;
            }
            ZdoEventFlags flags = ZdoEventFlags.None;
            if ((object)zdo == s_listing)
            {
                // AddIfPortal put it on the portal list of its sector and now takes it off the sector list.
                s_listing = null;
                flags = ZdoEventFlags.Portal | ZdoEventFlags.PortalList;
            }
            ZDOID id = zdo.m_uid;
            if (id != ZDOID.None)
                ZdoJournal.Put(ZdoEventKind.Removed, zdo, id, sector, ZdoJournal.NoSector, flags, ZdoJournal.RowOf(zdo));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool BeginMove(ZDO zdo)
        {
            if (zdo == null || s_moving != null || !ZdoJournal.OnMainThread())
                return false;
            s_moving = zdo;
            s_moveSeen = 0;
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void EndMove(ZDO zdo)
        {
            if ((object)s_moving != zdo)
                return;   // forgotten: recording started or stopped inside the call
            int seen = s_moveSeen;
            s_moving = null;
            s_moveSeen = 0;
            ZDOID id = zdo.m_uid;
            if (seen == 0 || !ZdoJournal.Recording || id == ZDOID.None)
                return;
            int row = ZdoJournal.RowOf(zdo);
            if (seen == 3)
                ZdoJournal.Put(ZdoEventKind.Moved, zdo, id, s_movedFrom, s_movedTo, ZdoEventFlags.None, row);
            else if (seen == 1)
                ZdoJournal.Put(ZdoEventKind.Removed, zdo, id, s_movedFrom, ZdoJournal.NoSector, ZdoEventFlags.None, row);
            else
                ZdoJournal.Put(ZdoEventKind.Added, zdo, id, ZdoJournal.NoSector, s_movedTo, ZdoEventFlags.None, row);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void BeginDestroy(ZDOMan man, ZDOID uid, ref DestroyCapture c)
        {
            if (man == null || !ZdoJournal.OnMainThread())
                return;
            ZDO zdo = man.GetZDO(uid);
            if (zdo == null)
                return;   // the game returns here too
            c.Zdo = zdo;
            c.Id = uid;
            // The game reads the sector the same way before anything else.
            c.Sector = zdo.GetSectorIndex().Sector;
            c.Row = ZdoJournal.RowOf(zdo);
            c.Epoch = ZdoJournal.Epoch;
            c.Portal = IsPortalPrefab(zdo.GetPrefab());
            c.Listed = c.Portal && OnPortalList(man, zdo, c.Sector);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void EndDestroy(ZDOMan man, ref DestroyCapture c)
        {
            if (!ZdoJournal.Recording || c.Epoch != ZdoJournal.Epoch || man == null)
                return;
            // Still in the id table: the call threw, or a prefix skipped it, before it got that far. Whatever sector
            // removal happened was recorded by its own hook.
            if ((object)man.GetZDO(c.Id) == c.Zdo)
                return;
            if (c.Listed && !OnPortalList(man, c.Zdo, c.Sector))
                ZdoJournal.Put(ZdoEventKind.PortalRemoved, c.Zdo, c.Id, c.Sector, ZdoJournal.NoSector, ZdoEventFlags.Portal, c.Row);
            ZdoJournal.Put(ZdoEventKind.Destroyed, c.Zdo, c.Id, c.Sector, ZdoJournal.NoSector, c.Portal ? ZdoEventFlags.Portal : ZdoEventFlags.None, c.Row);
            ZdoJournal.ForgetRow(c.Id);
        }

        // GetOwner as the game reads it (0 when not owned: a new or pooled ZDO). Off the main thread true without
        // reading anything: PutChange then turns the call into a gap, as for any other write there.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool OwnerChanges(ZDO zdo, long uid) =>
            zdo != null && (!ZdoJournal.IsMainThread || zdo.GetOwner() != uid);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool NotListedPortal(ZDOMan man, ZDO zdo, int prefabHash)
        {
            bool listing = zdo != null && man != null && IsPortalPrefab(prefabHash) && ZdoJournal.OnMainThread()
                && !OnPortalList(man, zdo, zdo.GetSectorIndex().Sector);
            // Also lets go of a capture whose call threw before its postfix.
            if (ZdoJournal.IsMainThread)
                s_listing = listing ? zdo : null;
            return listing;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void PortalListed(ZDOMan man, ZDO zdo, bool ran)
        {
            if ((object)s_listing == zdo)
                s_listing = null;   // the call had no RemoveFromSector (skipped, or threw before it)
            if (!ran || !ZdoJournal.Recording || man == null || zdo == null || zdo.m_uid == ZDOID.None || !ZdoJournal.OnMainThread())
                return;
            uint sector = zdo.GetSectorIndex().Sector;
            if (OnPortalList(man, zdo, sector))
                ZdoJournal.Put(ZdoEventKind.PortalAdded, zdo, zdo.m_uid, ZdoJournal.NoSector, sector, ZdoEventFlags.Portal, ZdoJournal.RowOf(zdo));
        }

        // A reference test, not Unity's null (no native call); the game reads the same list without any test. The game's
        // List.Contains as a plain loop (a handful of entries; this runs for every new Changed while recording).
        internal static bool IsPortalPrefab(int prefab)
        {
            Game game = Game.instance;
            List<int> portals = (object)game != null ? game.PortalPrefabHash : null;
            if (portals == null)
                return false;
            for (int i = 0; i < portals.Count; i++)
                if (portals[i] == prefab)
                    return true;
            return false;
        }

        // By reference: the game's Contains compares ids, and a pooled ZDO has id None.
        static bool OnPortalList(ZDOMan man, ZDO zdo, uint sector)
        {
            Dictionary<ZoneSystem.SectorIndex, List<ZDO>> portals = man.GetPortals();
            if (portals == null || !portals.TryGetValue(new ZoneSystem.SectorIndex(sector), out List<ZDO> list) || list == null)
                return false;
            for (int i = 0; i < list.Count; i++)
                if ((object)list[i] == zdo)
                    return true;
            return false;
        }
    }
}
