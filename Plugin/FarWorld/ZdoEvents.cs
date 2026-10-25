using System;
using System.Collections.Generic;
using System.Diagnostics;
using RealPortal.Engine;
using UnityEngine;

namespace RealPortal
{
    // The journal's consumer (ARCHITECTURE-v2.md §3.3; Engine\Modules\ZdoJournalModule.cs): one ZdoJournalReader that
    // watches the registry's sectors and is drained into it, in the game's order, whenever the registry is asked or its
    // frame comes (SectorRegistry.Sync), and the engine's instance events (SectorInstanceIndexModule.InstanceAdded /
    // InstanceRemoved) for the rows' Created bits. Made only under Far.V2 with Far.Journal and Far.Registry while the
    // registry holds sectors (and a grace time after the last goes); disposed otherwise, so the journal records nothing
    // for us and no instance handler is subscribed.
    //
    // What a drain says (ZdoDrainResult):
    //  - Changes: applied one by one (SectorRegistry.Apply). Removed and then Destroyed both say a ZDO left: leaving is
    //    idempotent there.
    //  - NotArmed: the journal does not follow the world (the engine's ZdoJournal off, its module not installed): the
    //    registry drops its rows and reads the game's tables whenever asked, as a relist, until the journal arms again
    //    (whose first drain then says Reset).
    //  - Reset (a world came or went, events were missed off the main thread, the journal was switched): every row is
    //    dropped and every sector built again from the game's tables, within the rebuild budget (read directly till then).
    //  - Recovered (the ring lapped us: over 64k events since the last drain, zone generation): the registered sectors the
    //    lost events named are built again, and portals count as changed.
    // Multiplayer: a peer's copies, moves, ownership changes and destroys reach us as the journal records them inside
    // ZDOMan.RPC_ZDOData and RPC_DestroyZDO (IsRemote, counted); nothing here writes a ZDO or touches ownership.
    // A frame drains at most MaxEventsPerSync events, however many queries ask (every query drains first: KeepAliveFast
    // asks once per object); the rest waits for the next frame, and until then (CaughtUp false) the registry reads the
    // game's tables instead of rows that may lag. While nothing records and the registry was told so, a drain returns at
    // once. Nothing is allocated per drain once the change list has grown.
    internal sealed class ZdoEvents : IDisposable
    {
        // ~0.1-0.2 us an event applied (Mono, out of the game): a frame's drains stay near a millisecond however big
        // the burst.
        internal const int MaxEventsPerSync = 8192;

        readonly SectorRegistry _registry;
        readonly ZdoJournalReader _reader;
        readonly List<ZdoChange> _changes = new List<ZdoChange>(256);
        readonly SectorInstanceIndexModule.InstanceAddedHandler _added;
        readonly SectorInstanceIndexModule.InstanceRemovedHandler _removed;
        bool _disposed;
        // The engine's instance epoch the Created bits were last read at (0: never; a live mirror is epoch 1 or later).
        int _instanceEpoch;
        // The frame the events below were drained in (MaxEventsPerSync is a frame's budget, however often it is asked).
        int _drainFrame = -1, _drainedInFrame;

        // The journal follows the world for us: the last drain was not NotArmed.
        internal bool Armed { get; private set; }
        // Nothing waits in the journal for us: what the rows say is the game's state as of now.
        internal bool CaughtUp { get; private set; }
        // Bumped by every portal event the reader passes (a portal made, its connection or tag changed, destroyed:
        // wherever it stands, watched or not), by a Reset and by a recovery that may have lost some. For whoever pairs
        // portals or keeps their frames (PortalLink, FrameCache).
        internal long PortalRevision;

        // Lab: changes applied by kind, a peer's among them, drains that said Reset / Recovered / NotArmed, drains cut by
        // the frame's budget (MaxEventsPerSync); Created bits set and cleared by the instance events, and removals that
        // matched no live ZDO; the most events in one drain and the slowest drain.
        internal long LabChanges, LabEnters, LabLeaves, LabData, LabOwner, LabPortal, LabRemote;
        internal long LabResets, LabRecoveries, LabNotArmed, LabCapped;
        internal long LabCreatedSet, LabCreatedCleared, LabCreatedUnmatched, LabEpochSyncs;
        internal int LabMaxEvents;
        internal double LabMaxDrainMs, LabLastDrainMs;

        // Subscribes: from here on every change is in the journal, so the registry builds its sectors from the game's
        // tables after this (SectorRegistry.OpenJournal queues them all).
        internal ZdoEvents(SectorRegistry registry)
        {
            _registry = registry;
            _reader = new ZdoJournalReader { PortalsAlways = true };
            Armed = ZdoJournal.IsArmed;
            CaughtUp = false;
            _added = InstanceAdded;
            _removed = InstanceRemoved;
            SectorInstanceIndexModule.InstanceAdded += _added;
            SectorInstanceIndexModule.InstanceRemoved += _removed;
        }

        internal bool IsOpen => !_disposed;
        // ZdoJournalReader.Pending: events waiting, -1 when lapped, ZdoJournal.NotArmed while nothing records.
        internal int Pending => _disposed ? ZdoJournal.NotArmed : _reader.Pending;
        // The rows' Created bits follow the engine's instance events now (else the registry reads ZDO.Created).
        internal bool CreatedExact =>
            !_disposed && _instanceEpoch != 0 && SectorInstanceIndexModule.InstanceEventsLive && _instanceEpoch == SectorInstanceIndexModule.InstanceEpoch;
        internal string ReaderStats => _reader.Stats();

        internal void Watch(uint sector) => _reader.Watch(sector);
        internal void Unwatch(uint sector) => _reader.Unwatch(sector);

        // Everything the journal holds for us, applied to the registry (at most what is left of the frame's
        // MaxEventsPerSync events). Cheap when nothing waits, the budget is spent or nothing records.
        internal void Drain()
        {
            if (_disposed)
                return;
            int pending = _reader.Pending;
            if (pending == 0 && Armed)
            {
                CaughtUp = true;
                return;
            }
            // Not recording, and the registry was told when it stopped (JournalLost): nothing to read until it arms
            // (Pending then says -1 or a count).
            if (pending == ZdoJournal.NotArmed && !Armed)
            {
                CaughtUp = false;
                return;
            }
            int frame = Time.frameCount;
            if (frame != _drainFrame)
            {
                _drainFrame = frame;
                _drainedInFrame = 0;
            }
            int budget = MaxEventsPerSync - _drainedInFrame;
            if (budget <= 0)
            {
                // The rest waits for the next frame; queries read the game's tables meanwhile.
                CaughtUp = false;
                return;
            }
            long t0 = Stopwatch.GetTimestamp();
            _changes.Clear();
            ZdoDrainResult result = _reader.Drain(_changes, budget);
            switch (result)
            {
                case ZdoDrainResult.NotArmed:
                    LabNotArmed++;
                    CaughtUp = false;
                    if (Armed)
                    {
                        Armed = false;
                        _registry.JournalLost();
                    }
                    break;
                case ZdoDrainResult.Reset:
                    LabResets++;
                    Armed = true;
                    PortalRevision++;
                    _registry.JournalReset();
                    CaughtUp = _reader.Pending == 0;
                    break;
                case ZdoDrainResult.Recovered:
                    LabRecoveries++;
                    Armed = true;
                    if (_reader.PortalsDirty)
                        PortalRevision++;
                    _registry.JournalRecovered(_reader.DirtySectors);
                    CaughtUp = _reader.Pending == 0;
                    break;
                default:
                    if (!Armed)
                    {
                        // Armed since the last drain without a Reset in between (it armed between our subscription
                        // and the first drain): nothing was lost, the rows are being built from the tables anyway.
                        Armed = true;
                    }
                    for (int i = 0; i < _changes.Count; i++)
                        Count(_registry.Apply(_changes[i]), _changes[i]);
                    CaughtUp = _reader.Pending == 0;
                    if (!CaughtUp)
                        LabCapped++;
                    break;
            }
            _changes.Clear();
            int events = _reader.LastEvents;
            _drainedInFrame += events;
            if (events > LabMaxEvents)
                LabMaxEvents = events;
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            LabLastDrainMs = ms;
            if (ms > LabMaxDrainMs)
                LabMaxDrainMs = ms;
        }

        void Count(bool applied, in ZdoChange c)
        {
            LabChanges++;
            if (c.IsRemote)
                LabRemote++;
            if ((c.Flags & ZdoEventFlags.Portal) != 0 || c.Kind == ZdoChangeKind.PortalAdded || c.Kind == ZdoChangeKind.PortalRemoved)
            {
                PortalRevision++;
                LabPortal++;
            }
            if (!applied)
                return;
            if (c.Enters)
                LabEnters++;
            else if (c.Leaves)
                LabLeaves++;
            else if (c.Kind == ZdoChangeKind.Changed)
                LabData++;
            else if (c.Kind == ZdoChangeKind.OwnerChanged)
                LabOwner++;
        }

        // Once a frame (SectorRegistry.Update): the Created bits are read again from ZDO.Created whenever the engine's
        // instance events (re)started since they were last read (InstanceEpoch moved), as its protocol says; while the
        // events are not live CreatedExact is false and the registry answers from ZDO.Created.
        internal void SyncCreated()
        {
            if (_disposed)
                return;
            if (!SectorInstanceIndexModule.InstanceEventsLive)
            {
                _instanceEpoch = 0;
                return;
            }
            int epoch = SectorInstanceIndexModule.InstanceEpoch;
            if (_instanceEpoch == epoch)
                return;
            _registry.ResyncCreated();
            _instanceEpoch = epoch;
            LabEpochSyncs++;
        }

        // Inside ZNetView.Awake (ZNetScene.AddInstance), before the object's other components woke: only the bit.
        void InstanceAdded(ZDO zdo, ZNetView view)
        {
            if (!_disposed && zdo != null && _registry.SetCreated(zdo, zdo.m_uid, true))
                LabCreatedSet++;
        }

        // The ZDO may be pooled or handed out again under another id by now: matched by id. A destroyed one's row goes
        // with its Removed/Destroyed anyway.
        void InstanceRemoved(ZDO zdo, ZDOID id)
        {
            if (_disposed)
                return;
            if (zdo == null || zdo.m_uid != id)
            {
                LabCreatedUnmatched++;
                return;
            }
            if (_registry.SetCreated(zdo, id, false))
                LabCreatedCleared++;
        }

        // Unsubscribes from the journal (the last subscriber stops it) and from the instance events. Twice is harmless.
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            SectorInstanceIndexModule.InstanceAdded -= _added;
            SectorInstanceIndexModule.InstanceRemoved -= _removed;
            _reader.Dispose();
            _changes.Clear();
            Armed = false;
            CaughtUp = false;
            _instanceEpoch = 0;
        }

        internal string Describe() =>
            $"journal {(_disposed ? "closed" : Armed ? (CaughtUp ? "following" : "catching up") : "not armed")}, " +
            $"changes {LabChanges} (in {LabEnters}, out {LabLeaves}, data {LabData}, owner {LabOwner}, portal {LabPortal}, from peers {LabRemote}), " +
            $"resets {LabResets}, recoveries {LabRecoveries}, not armed {LabNotArmed}, capped {LabCapped}, " +
            $"max {LabMaxEvents} events / {LabMaxDrainMs:0.000} ms a drain; created bits {(CreatedExact ? "exact" : "from ZDO.Created")} " +
            $"(+{LabCreatedSet} -{LabCreatedCleared}, unmatched {LabCreatedUnmatched}, epoch syncs {LabEpochSyncs}); {_reader.Stats()}";
    }
}
