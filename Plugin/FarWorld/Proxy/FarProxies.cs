using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using RealPortal.Engine;
using Unity.Collections;
using UnityEngine;

namespace RealPortal
{
    // What a far object is for RemoteAreas while Far.Proxies acts (FarProxies.StateOf).
    internal enum ProxyState : byte
    {
        // Instantiate it as before (not proxyable, inside a far active area, or proxies off).
        Real,
        // Drawn as a proxy: not instantiated, and present for readiness.
        Proxy,
        // To be drawn as a proxy once its sector (or its template) is built: not instantiated, not present yet.
        Waiting,
    }

    // FarWorld proxies (Far.Proxies; FARWORLD-V3.md §7 V3, ARCHITECTURE-v2.md §3.5-§3.7, §3.12, §7 S5/S7), the data side:
    // every far object of a far area that the classifier proves static (ProxyTemplates) and that lies outside every far
    // area's active area (ZNetScene.InActiveArea round its far portal's zone, the claims of RemoteAreas' parity code,
    // RemoteAreas.CollectActiveZones) is not instantiated; it is a ProxyInst in ProxyStore, mirrored from FarWorld's
    // SectorRegistry rows (ISectorRowListener), drawn by the slot cameras with the game's own meshes and materials (the
    // drawing part reads CollectSpans and ProxyTables). Inside an active area everything stays real.
    //
    // The builder (ProxyBuilder) follows the registry:
    //  - a sector the registry built is queued and built here within BuildBudgetMs a frame (its prefabs' templates made
    //    first, within TemplateBudgetMs; a sector waits while one is missing), each row decoded (ProxyTemplates.Decode);
    //  - the journal's events in O(1) per row: a row in (decoded at once, or deferred until its template is made), out (its
    //    proxy goes), a data change (decoded again: moved, damaged, picked, snowed on, its material varied), its Created bit
    //    (the handoff, §3.12: a proxy whose ZDO has a live instance is never drawn; read from ZDO.Created every frame while
    //    the engine's instance events are not live), the sector unbuilt or dropped (its span cleared or freed);
    //  - the active-area boundary: whenever RemoteAreas' areas or the simulation distance change, every span near an active
    //    area has its InActive bits set again (a proxy coming into one becomes real: RemoteAreas lists again, RealizeVersion).
    // RemoteAreas asks StateOf for each object it would create: Proxy (built: skip it, it counts as present), Waiting (its
    // proxy comes: skip it, it does not count yet), Real (create it). An object whose proxy is built and that still has an
    // instance (made before the switch, or by the game for the player and left behind as he walked off) is drained: its
    // instance is taken out the way the game's removal does (ResetZDO, Object.Destroy, out of m_instances, the instance
    // index told), within DrainPerFrame and DrainBudgetMs a frame, never one the game's own lists hold for the player, never
    // one inside an active area; its proxy shows in the same frame.
    // The drain waits until the drawing part has gone through a window render with this store (DrawRan: a render reached
    // the spans of the area it shows and finished without an error); an error of the drawing part stops the proxies like
    // an error of their own: every proxied object is made real again. So no real instance is taken out unless its proxy
    // has been drawn.
    // Switching: on, proxies are built and the instances drain; off, StateOf answers Real at once, every area is listed
    // again (RealizeVersion) and creates what was proxied, while the proxies stay drawn (Retiring) until no proxy is left
    // without its instance, or RetireSeconds. The store is disposed with the registry (Far.V2 or Far.Registry off, the
    // scene gone), after an error (stopped until a Far.* switch is set again) and when Retiring ends; a new store (switched
    // on again, a new world) starts from nothing.
    // Never takes ownership, never writes a ZDO. Main thread only; nothing allocated per frame once the tables have grown.
    internal static class FarProxies
    {
        const double BuildBudgetMs = 0.5;
        const double TemplateBudgetMs = 1.0;
        const double LoadingTemplateBudgetMs = 30.0;
        const int DrainPerFrame = 64;
        const double DrainBudgetMs = 0.5;
        // Retiring ends once every proxy's object is made, or after this long (an area that never makes some of them).
        const float RetireSeconds = 300f;

        // A window render drew this store's spans without an error (ProxyDraw); the drain waits for it.
        internal static bool DrawRan;

        static ProxyBuilder s_builder;
        static bool s_broken;
        static int s_errors;
        static float s_errorAt;
        // The drawing part went through a window render with this store (DrawRan), and in which frame first.
        static bool s_drawVerified;
        static int s_drawVerifiedFrame = -1;

        // Moves whenever an object that was proxied (and so neither instantiated nor pending in RemoteAreas) must be real
        // after all: its state changed, an active area came over it, a rebuilt sector decoded it real, proxies went off or
        // were dropped. RemoteAreas lists every area again when it moves.
        internal static int RealizeVersion;

        // Proxies are decided and built: StateOf may answer Proxy or Waiting.
        internal static bool Proxying => s_builder != null && !s_builder.Retiring && !s_builder.Dead;
        // Proxies may be drawn (proxying, or retiring after the switch went off).
        internal static bool Drawing => s_builder != null && !s_builder.Dead;
        // A store exists (drawn or not: one whose registry went is dropped at the next Update).
        internal static bool Alive => s_builder != null;
        internal static ProxyStore Store => s_builder?.Store;
        internal static SectorRegistry Registry => s_builder?.Registry;
        // Moves with every change of the store (an instance in, out or changed): whoever caches a cull per span.
        internal static int Version => s_builder != null ? s_builder.Store.Version : -1;

        // Lab (the data side): instances built (decoded into the store), decodes, instances removed, sector builds, sectors
        // waiting for a template, rows deferred for a template, instances drained (instantiated objects taken out), drained
        // objects kept because the game's lists hold them for the player, relists asked of RemoteAreas, boundary passes,
        // proxies flipped by them, the slowest update (ms), starts, stops, errors; rows kept real now by reason.
        internal static long LabBuilt, LabDecodes, LabRemoved, LabSectorBuilds, LabSectorWaits, LabDeferred, LabDrained, LabParked, LabRealizes;
        // Lab: rows with particle carriers standing now.
        internal static int LabCarriers;
        internal static long LabBoundaryPasses, LabBoundaryFlips;
        internal static double LabUpdateMsMax, LabBuildMsMax;
        internal static int LabStarts, LabStops, LabErrors;
        // Lab: stops for the drawing part (an error there, a window render that did not reach it), frames the drain waited
        // for the first drawn render, sectors held for the game camera (OwnAreaFill) and let go.
        internal static int LabDrawStops;
        internal static long LabDrainWaits, LabHolds, LabHoldEnds;
        internal static readonly int[] LabRealRows = new int[(int)ProxyReal.Count];
        // Lab (the drawing part writes these): instances submitted in all, and this frame's draw calls, instances and cull
        // time (ms), reset by the drawing part at its frame's start.
        internal static long LabInstanced;
        internal static int LabDrawCalls, LabDrawInstances;
        internal static double LabCullMs;

        // A Far.* switch was set (WindowFeatures): an error's stop is lifted, and the next Update decides again.
        internal static void Switched()
        {
            s_broken = false;
        }

        // Once a frame, from FarWorld.Update right after the registry's frame: made or dropped as the switch, the registry
        // and the world say, then its frame (templates, sector builds, the boundary, the Created bits, the drain).
        internal static void Update(SectorRegistry registry)
        {
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                bool want = FarFlags.Proxies && FarWorld.RegistryOn && registry != null && !registry.Disposed && !s_broken && Graphics();
                ProxyBuilder b = s_builder;
                if (b != null && (b.Dead || !ReferenceEquals(b.Registry, registry)))
                {
                    Stop(s_broken ? "an error" : b.Dead ? "the registry went" : "another registry");
                    b = null;
                }
                if (want && b == null)
                    b = Start(registry);
                else if (want && b.Retiring)
                {
                    b.Retiring = false;
                    Plugin.Log.LogInfo("FarProxies: proxying again");
                }
                else if (!want && b != null && !b.Retiring)
                {
                    if (s_broken || registry == null || registry.Disposed || !FarWorld.RegistryOn)
                    {
                        Stop(s_broken ? "an error" : "no registry");
                        b = null;
                    }
                    else
                    {
                        // Off: every proxied object is made again by its area; the proxies stay drawn until it is.
                        b.Retiring = true;
                        b.RetireAt = Time.unscaledTime;
                        RealizeVersion++;
                        LabRealizes++;
                        Plugin.Log.LogInfo("FarProxies: switched off, the proxied objects are made again (proxies drawn meanwhile)");
                    }
                }
                if (b != null)
                {
                    b.Frame();
                    if (b.Retiring && (b.LiveDrawn() == 0 || Time.unscaledTime - b.RetireAt > RetireSeconds))
                        Stop("retired");
                }
                // The game's LOD settings for this frame's window renders (which change the bias for their own renderers).
                if (s_builder != null)
                    ProxyDraw.FrameSync();
            }
            catch (Exception e)
            {
                Failed(e);
            }
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            if (ms > LabUpdateMsMax)
                LabUpdateMsMax = ms;
        }

        // A dedicated server draws nothing.
        static bool Graphics() => SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null;

        static ProxyBuilder Start(SectorRegistry registry)
        {
            var b = new ProxyBuilder(registry);
            s_builder = b;
            DrawRan = false;
            LabStarts++;
            b.Attach();
            Plugin.Log.LogInfo($"FarProxies: on (registry generation {registry.Generation}, {registry.SectorCount} sector(s) queued)");
            return b;
        }

        internal static void Stop(string why)
        {
            ProxyBuilder b = s_builder;
            if (b == null)
                return;
            s_builder = null;
            LabStops++;
            try
            {
                b.Dispose();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("FarProxies: dropping the store: " + e);
            }
            // Whatever was proxied is made again by its area; the drawing part's per-row state means nothing any more.
            RealizeVersion++;
            LabRealizes++;
            Array.Clear(LabRealRows, 0, LabRealRows.Length);
            ProxyDraw.Clear(false);
            Plugin.Log.LogInfo($"FarProxies: off ({why})");
        }

        // The scene went (FarWorld.Clear): the store and every template go.
        internal static void Clear()
        {
            Stop("the scene went");
            ProxyTemplates.Clear();
            ProxyDraw.Clear(true);
        }

        // An exception in the proxies' own frame or a listener call: logged (in full at most every 10 s), and proxies stop
        // until a Far.* switch is set again; RemoteAreas makes what was proxied.
        internal static void Failed(Exception e)
        {
            s_errors++;
            LabErrors++;
            if (Time.unscaledTime >= s_errorAt)
            {
                s_errorAt = Time.unscaledTime + 10f;
                Plugin.Log.LogError($"FarProxies: error {s_errors}, proxies stop until a Far.* switch is set again: {e}");
            }
            s_broken = true;
            if (s_builder != null)
                s_builder.Detach();
        }

        // ---- RemoteAreas ------------------------------------------------------------------------------------------

        // What the far object is for an area that would create it now (RemoteAreas.AddPending and CreateSome).
        internal static ProxyState StateOf(ZDO zdo)
        {
            ProxyBuilder b = s_builder;
            if (b == null || b.Retiring || b.Dead || zdo == null)
                return ProxyState.Real;
            return b.StateOf(zdo);
        }

        // ---- the drawing part ---------------------------------------------------------------------------------------

        // The spans one area's render culls: each sector it reaches with its role (Core, Near: every instance; Ring: the
        // Distant ones, DistantOnly), built and not empty. Clears into first. Main thread; valid until control returns to
        // the game. Returns the count.
        internal static int CollectSpans(AreaSectors a, List<ProxySpanRef> into)
        {
            into.Clear();
            ProxyBuilder b = s_builder;
            if (b == null || b.Dead || a == null || !a.Live || !ReferenceEquals(a.Registry, b.Registry))
                return 0;
            for (int i = 0; i < a.NearRecs.Length; i++)
                b.AddSpan(a.NearRecs[i], a.Near[i], a.NearRoles[i], false, into);
            for (int i = 0; i < a.RingRecs.Length; i++)
                b.AddSpan(a.RingRecs[i], a.Ring[i], SectorRole.Ring, true, into);
            return into.Count;
        }

        // The same for the far area round a far portal (RemoteAreas' area of it, while it does not drain).
        internal static int CollectSpans(ZDOID farPortal, List<ProxySpanRef> into)
        {
            into.Clear();
            RemoteAreas areas = PortalSystem.Instance?.Areas;
            return areas != null && s_builder != null ? CollectSpans(areas.SectorsOf(farPortal), into) : 0;
        }

        // The drawing part found a material it cannot draw exactly instanced (P1 failed for both rungs): every state that
        // shows it stays real from now on (RemoteAreas makes those objects).
        internal static void RejectMaterial(Material m) => ProxyTemplates.RejectMaterial(m);

        // The ZDO's proxy (built, outside every active area; Created may be set: then its object is made and the proxy not
        // drawn). For the handoff's LodFadeInOut skip and first-frame variant (§3.12), which the drawing part decides with
        // its own last-drawn frames.
        internal static bool TryGet(ZDO zdo, out ProxyInst inst)
        {
            inst = default;
            ProxyBuilder b = s_builder;
            return b != null && !b.Dead && zdo != null && b.TryGet(zdo, out inst);
        }

        // The ZDO's proxy whatever its flags (an InActive one too: drawn until its object is made). The handoff asks.
        internal static bool TryGetAny(ZDO zdo, out ProxyInst inst)
        {
            inst = default;
            ProxyBuilder b = s_builder;
            return b != null && !b.Dead && zdo != null && b.TryGetAny(zdo, out inst);
        }

        // Win.Virtual (WindowScreenContent): why a registry row is real (ProxyReal; None or ActiveArea: it has a proxy); false
        // while undecided or without a store.
        internal static bool ReasonOf(int row, out ProxyReal why)
        {
            why = ProxyReal.None;
            ProxyBuilder b = s_builder;
            if (b == null || b.Dead)
                return false;
            byte r = b.ReasonOf(row);
            if (r == 255)
                return false;
            why = (ProxyReal)r;
            return true;
        }

        // ---- lab ----------------------------------------------------------------------------------------------------

        internal static string Describe()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"FarProxies: Far.Proxies {(FarFlags.Proxies ? "on" : "off")}, ");
            ProxyBuilder b = s_builder;
            if (b == null)
                sb.Append(s_broken ? $"stopped after {s_errors} error(s)" : "no store");
            else
                sb.Append(b.Describe());
            sb.Append($"; built {LabBuilt}, decodes {LabDecodes}, removed {LabRemoved}, sector builds {LabSectorBuilds} (waited for templates {LabSectorWaits}, " +
                      $"rows deferred {LabDeferred}, slowest pass {LabBuildMsMax:0.000} ms), drained {LabDrained}, particle carriers {LabCarriers} (kept for the player {LabParked}), " +
                      $"relists asked {LabRealizes}, boundary passes {LabBoundaryPasses} ({LabBoundaryFlips} flips), slowest update {LabUpdateMsMax:0.000} ms, " +
                      $"starts {LabStarts}, stops {LabStops}, errors {LabErrors}; drawing: instanced {LabInstanced}, this frame {LabDrawCalls} calls / " +
                      $"{LabDrawInstances} instances, cull {LabCullMs:0.000} ms, {ProxyDraw.Describe()}; rows real:");
            for (int i = 1; i < (int)ProxyReal.Count; i++)
                if (LabRealRows[i] > 0)
                    sb.Append(' ').Append((ProxyReal)i).Append(' ').Append(LabRealRows[i]);
            sb.Append("; ").Append(ProxyTemplates.Describe());
            return sb.ToString();
        }

        internal static void LabResetCounters()
        {
            LabBuilt = LabDecodes = LabRemoved = LabSectorBuilds = LabSectorWaits = LabDeferred = LabDrained = LabParked = LabRealizes = 0;
            LabBoundaryPasses = LabBoundaryFlips = 0;
            LabUpdateMsMax = LabBuildMsMax = 0.0;
            LabInstanced = 0;
            ProxyTemplates.LabMakeMsMax = 0.0;
            ProxyDraw.LabResetCounters();
        }

        // ---- the builder ----------------------------------------------------------------------------------------

        // One store and its mirror of one registry's rows (the listener).
        sealed class ProxyBuilder : ISectorRowListener, IDisposable
        {
            const byte Unknown = 255;

            internal readonly SectorRegistry Registry;
            internal readonly ProxyStore Store = new ProxyStore();
            internal bool Retiring, Dead;
            internal float RetireAt;

            // Per registry row: why it stays real (ProxyReal; None: it has its proxy), Unknown while undecided.
            byte[] _reason = NewReasons(256);
            // Sectors waiting for a build (SectorRec slots), FIFO.
            readonly List<int> _queue = new List<int>();
            int _queueHead;
            // Rows of built sectors waiting for their template: (slot, row, id).
            struct Deferred
            {
                internal int Slot, Row;
                internal ZDOID Id;
            }
            readonly List<Deferred> _deferred = new List<Deferred>();
            // The far areas' active areas (RemoteAreas' areas, draining ones too), their zones, and what they were made of.
            readonly List<Vector2s> _zones = new List<Vector2s>();
            ActiveArea[] _active = new ActiveArea[16];
            int _activeCount, _activeVersion = int.MinValue, _activeFrame = -1;
            SimulationDistance _activeSim;
            float _activeZoneSize;
            bool _boundaryDirty;
            int _rejectVersion;
            // Instances to drain (made objects whose proxy is built), and those the game's lists held for the player then.
            struct Drain
            {
                internal ZDO Zdo;
                internal ZDOID Id;
            }
            readonly List<Drain> _drain = new List<Drain>();
            readonly List<Drain> _parked = new List<Drain>();
            int _drainHead;
            Vector2s _drainZone;
            bool _drainZoneKnown;
            int _liveFrame = -1, _liveDrawn;
            readonly Stopwatch _clock = new Stopwatch();

            static readonly AccessTools.FieldRef<ZNetScene, Dictionary<ZDO, ZNetView>> s_instances =
                AccessTools.FieldRefAccess<ZNetScene, Dictionary<ZDO, ZNetView>>("m_instances");

            internal ProxyBuilder(SectorRegistry registry)
            {
                Registry = registry;
                _rejectVersion = ProxyTemplates.RejectVersion;
            }

            static byte[] NewReasons(int n)
            {
                var a = new byte[n];
                for (int i = 0; i < n; i++)
                    a[i] = Unknown;
                return a;
            }

            // Listens to the registry and queues every sector it has built already.
            internal void Attach()
            {
                Registry.Listener = this;
                for (int i = 0; i < Registry.RecHigh; i++)
                {
                    SectorRec rec = Registry.RecAt(i);
                    if (rec != null && rec.InUse && rec.Built)
                        Queue(rec.Slot);
                }
                _boundaryDirty = true;
            }

            internal void Detach()
            {
                if (ReferenceEquals(Registry.Listener, this))
                    Registry.Listener = null;
                Dead = true;
            }

            public void Dispose()
            {
                Detach();
                foreach (GameObject[] gos in _carriers.Values)
                    foreach (GameObject go in gos)
                        if (go != null)
                            UnityEngine.Object.Destroy(go);
                LabCarriers -= _carriers.Count;
                _carriers.Clear();
                Store.Dispose();
                _queue.Clear();
                _deferred.Clear();
                _drain.Clear();
                _parked.Clear();
            }

            // ---- the frame --------------------------------------------------------------------------------------

            internal void Frame()
            {
                if (Dead)
                    return;
                RefreshActive();
                if (_rejectVersion != ProxyTemplates.RejectVersion)
                {
                    // A material was rejected: every built sector is decoded again.
                    _rejectVersion = ProxyTemplates.RejectVersion;
                    for (int i = 0; i < Registry.RecHigh; i++)
                    {
                        SectorRec rec = Registry.RecAt(i);
                        if (rec != null && rec.InUse && rec.Built)
                            Rebuild(rec);
                    }
                }
                // Templates of heavy prefabs take up to ~20 ms each, once: made while the loading screen hides the game as far
                // as the loading's own budget there goes, so play meets few of them.
                ProxyTemplates.MakeQueued(RemoteAreas.LoadingScreen() ? LoadingTemplateBudgetMs : TemplateBudgetMs);
                RunDeferred();
                RunBuilds();
                if (_boundaryDirty)
                    Boundary();
                if (!Registry.CreatedExact)
                    PollCreated();
                if (!Retiring)
                    RunDrain();
            }

            void Queue(int slot)
            {
                ref ProxyStore.Span s = ref Store.SpanAt(slot);
                if (s.Queued)
                    return;
                s.Queued = true;
                _queue.Add(slot);
            }

            // The sector's proxies made again from its rows (its span cleared now).
            void Rebuild(SectorRec rec)
            {
                Unbuilt(rec);
                Queue(rec.Slot);
            }

            void RunBuilds()
            {
                if (_queueHead >= _queue.Count)
                {
                    _queue.Clear();
                    _queueHead = 0;
                    return;
                }
                _clock.Restart();
                bool any = false;
                int n = _queue.Count - _queueHead;
                for (int i = 0; i < n; i++)
                {
                    if (any && _clock.Elapsed.TotalMilliseconds > BuildBudgetMs)
                        break;
                    int slot = _queue[_queueHead++];
                    ref ProxyStore.Span s = ref Store.SpanAt(slot);
                    if (!s.Queued)
                        continue;
                    SectorRec rec = Registry.RecAt(slot);
                    if (rec == null || !rec.InUse || !rec.Built)
                    {
                        // Gone or unbuilt since: its SectorBuilt queues it again.
                        s.Queued = false;
                        continue;
                    }
                    if (!TemplatesReady(rec))
                    {
                        // Its templates come within the template budget: back of the queue.
                        LabSectorWaits++;
                        _queue.Add(slot);
                        continue;
                    }
                    s.Queued = false;
                    Build(rec);
                    any = true;
                }
                if (_queueHead > 256 && _queueHead * 2 > _queue.Count)
                {
                    _queue.RemoveRange(0, _queueHead);
                    _queueHead = 0;
                }
                double ms = _clock.Elapsed.TotalMilliseconds;
                if (ms > LabBuildMsMax)
                    LabBuildMsMax = ms;
            }

            // Every row's prefab has its template (each missing one is queued).
            bool TemplatesReady(SectorRec rec)
            {
                bool ready = true;
                for (int k = 0; k < rec.Count; k++)
                {
                    int row = rec.Rows[k];
                    if (!Registry.RowLive(row) || Registry.RowPortal(row))
                        continue;
                    if (ProxyTemplates.Peek(Registry.RowZdo(row).GetPrefab()) == null)
                        ready = false;
                }
                return ready;
            }

            // The sector's span made from its rows as they are now.
            void Build(SectorRec rec)
            {
                Unbuilt(rec);
                ref ProxyStore.Span s = ref Store.SpanAt(rec.Slot);
                s.Built = true;
                bool rebuilt = s.Builds++ > 0;
                bool realize = false;
                for (int k = 0; k < rec.Count; k++)
                {
                    ProxyReal why = Decode(rec.Slot, rec.Rows[k], false, out bool stateReal);
                    realize |= stateReal && why != ProxyReal.None;
                }
                Store.RecomputeBounds(rec.Slot);
                LabSectorBuilds++;
                // A sector built again whose rows decode real by their state (not their prefab, which RemoteAreas' prediction
                // knew): an object of it may have been taken off its area's pending list as a proxy before.
                if (rebuilt && realize)
                    Realize();
            }

            // The span cleared and its rows undecided (the registry unbuilt the sector, or it is built again here).
            void Unbuilt(SectorRec rec)
            {
                if (!Store.HasSpan(rec.Slot))
                    return;
                for (int k = 0; k < rec.Count; k++)
                {
                    SetReason(rec.Rows[k], Unknown);
                    DropFx(rec.Rows[k]);
                }
                Store.Clear(rec.Slot);
            }

            void RunDeferred()
            {
                if (_deferred.Count == 0)
                    return;
                int kept = 0;
                for (int i = 0; i < _deferred.Count; i++)
                {
                    Deferred d = _deferred[i];
                    if (!Registry.RowLive(d.Row) || Registry.RowId(d.Row) != d.Id || Registry.RowRecSlot(d.Row) != d.Slot || !SpanBuilt(d.Slot))
                        continue;
                    if (ProxyTemplates.Peek(Registry.RowZdo(d.Row).GetPrefab()) == null)
                    {
                        _deferred[kept++] = d;
                        continue;
                    }
                    Redecode(d.Slot, d.Row);
                }
                _deferred.RemoveRange(kept, _deferred.Count - kept);
            }

            bool SpanBuilt(int slot) => Store.HasSpan(slot) && Store.SpanAt(slot).Built;

            void Realize()
            {
                RealizeVersion++;
                LabRealizes++;
            }

            // ---- decoding a row ---------------------------------------------------------------------------------

            // The row decoded into the store (its proxy added, replaced or removed); the reason it stays real, None when it
            // is a proxy (InActive ones too: they are real by their place, not their state). stateReal: real by its state
            // (a decoder's answer for a proxyable prefab).
            ProxyReal Decode(int slot, int row, bool deferIfMissing, out bool stateReal)
            {
                stateReal = false;
                LabDecodes++;
                if (!Registry.RowLive(row))
                {
                    RemoveInstance(slot, row);
                    SetReason(row, Unknown);
                    return ProxyReal.None;
                }
                ZDO z = Registry.RowZdo(row);
                ProxyReal why;
                int variant = -1;
                ProxyTemplates.VisualTemplate t = null;
                if (Registry.RowPortal(row))
                    why = ProxyReal.Portal;
                else if (!z.Persistent)
                    why = ProxyReal.NotPersistent;
                else if (z.GetPosition().y > ProxyTemplates.InteriorHeight)
                    why = ProxyReal.Interior;
                else
                {
                    t = ProxyTemplates.Peek(z.GetPrefab());
                    if (t == null)
                    {
                        // Its template is queued: the row waits (StateOf predicts meanwhile).
                        RemoveInstance(slot, row);
                        SetReason(row, Unknown);
                        if (deferIfMissing)
                        {
                            _deferred.Add(new Deferred { Slot = slot, Row = row, Id = Registry.RowId(row) });
                            LabDeferred++;
                        }
                        return ProxyReal.None;
                    }
                    why = t.Proxyable ? ProxyTemplates.Decode(t, z, out variant) : t.Real;
                    stateReal = t.Proxyable && why != ProxyReal.None;
                }
                if (why != ProxyReal.None)
                {
                    RemoveInstance(slot, row);
                    SetReason(row, (byte)why);
                    return why;
                }
                ProxyInst inst = Make(t, z, row, variant);
                int handle = Store.HandleOf(row);
                if (handle >= 0)
                    Store.Set(handle, inst, slot);
                else
                {
                    handle = Store.Add(slot, inst);
                    LabBuilt++;
                }
                SyncFx(row, inst);
                SetReason(row, (inst.Flags & (uint)ProxyFlags.InActive) != 0 ? (byte)ProxyReal.ActiveArea : (byte)ProxyReal.None);
                if ((inst.Flags & ((uint)ProxyFlags.Created | (uint)ProxyFlags.InActive)) == (uint)ProxyFlags.Created)
                    _drain.Add(new Drain { Zdo = z, Id = z.m_uid });
                return ProxyReal.None;
            }

            // A row of a built sector decoded again (its data changed, its template came): a proxy that turns real makes its
            // area list again (it is neither instantiated nor pending there).
            void Redecode(int slot, int row)
            {
                bool was = IsProxied(row);
                Decode(slot, row, true, out _);
                if (was && !IsProxied(row))
                    Realize();
            }

            bool IsProxied(int row)
            {
                int h = Store.HandleOf(row);
                return h >= 0 && (Store.Get(h).Flags & (uint)ProxyFlags.InActive) == 0;
            }

            ProxyInst Make(ProxyTemplates.VisualTemplate t, ZDO z, int row, int variant)
            {
                Matrix4x4 root = ProxyTemplates.RootOf(t, z);
                ProxyVariantData v = ProxyTables.Variants[variant];
                ProxyTemplateData td = ProxyTables.Templates[t.Id];
                var inst = new ProxyInst
                {
                    Root = root,
                    Center = root.MultiplyPoint3x4(v.Center),
                    Radius = v.Extents.magnitude * MaxScale(root),
                    Template = t.Id,
                    Variant = variant,
                    Row = row,
                };
                inst.LodSet = v.LodSet;
                if (v.LodSet >= 0)
                {
                    ProxyTemplateData rule = ProxyTables.LodSets[v.LodSet];
                    Matrix4x4 lod = root * rule.LodLocal;
                    inst.LodRef = lod.MultiplyPoint3x4(rule.LodLocalRef);
                    inst.LodSize = rule.LodSize * MaxScale(lod);
                }
                uint flags = 0;
                if (Registry.RowCreated(row))
                    flags |= (uint)ProxyFlags.Created;
                if (Registry.RowDistant(row))
                    flags |= (uint)ProxyFlags.Distant;
                if (v.PartCount == 0)
                    flags |= (uint)ProxyFlags.Hidden;
                if (InFarActive(z.GetPosition()))
                    flags |= (uint)ProxyFlags.InActive;
                inst.Flags = flags;
                return inst;
            }

            // The largest axis scale of an affine matrix (Unity's lossyScale magnitudes for a TRS chain).
            static float MaxScale(in Matrix4x4 m)
            {
                float x = new Vector3(m.m00, m.m10, m.m20).magnitude;
                float y = new Vector3(m.m01, m.m11, m.m21).magnitude;
                float z = new Vector3(m.m02, m.m12, m.m22).magnitude;
                return Mathf.Max(x, Mathf.Max(y, z));
            }

            void RemoveInstance(int slot, int row)
            {
                DropFx(row);
                if (Store.HandleOf(row) < 0)
                    return;
                Store.Remove(slot, row);
                LabRemoved++;
            }

            // ---- particle carriers ----------------------------------------------------------------------------------

            // A row whose proxy draws (neither Created nor InActive) and whose template carries particle subtrees has copies
            // of them where the real object's would stand, run by the game like any particle system (the real object's
            // own plays instead whenever it exists); moved with the proxy, gone with it.
            readonly Dictionary<int, GameObject[]> _carriers = new Dictionary<int, GameObject[]>();

            void SyncFx(int row, in ProxyInst inst)
            {
                ProxyTemplates.VisualTemplate t = ProxyTemplates.ById(inst.Template);
                bool want = t != null && t.FxSources != null && (inst.Flags & ((uint)ProxyFlags.Created | (uint)ProxyFlags.InActive)) == 0;
                _carriers.TryGetValue(row, out GameObject[] have);
                if (!want)
                {
                    if (have != null)
                        DropFx(row);
                    return;
                }
                if (have == null || have.Length != t.FxSources.Length)
                {
                    if (have != null)
                        DropFx(row);
                    have = new GameObject[t.FxSources.Length];
                    for (int i = 0; i < have.Length; i++)
                    {
                        Matrix4x4 m = inst.Root * t.FxLocals[i];
                        have[i] = UnityEngine.Object.Instantiate(t.FxSources[i], m.GetColumn(3), m.rotation);
                        have[i].transform.localScale = m.lossyScale;
                        if (t.FxLods != null && t.FxLods[i] != null)
                            CopyLod(have[i], t.FxLods[i], inst.Root);
                    }
                    _carriers[row] = have;
                    LabCarriers++;
                    return;
                }
                for (int i = 0; i < have.Length; i++)
                    if (have[i] != null)
                    {
                        Matrix4x4 m = inst.Root * t.FxLocals[i];
                        have[i].transform.SetPositionAndRotation(m.GetColumn(3), m.rotation);
                        have[i].transform.localScale = m.lossyScale;
                    }
            }

            // The real object's LODGroup over the carrier's particle renderers: the same levels, fades and mode, its reference
            // point and size where the real group's would be, so each camera hides them at the distance the real one does.
            static void CopyLod(GameObject carrier, ProxyTemplates.FxLod spec, in Matrix4x4 root)
            {
                Renderer[] rs = carrier.GetComponentsInChildren<Renderer>(true);
                var lods = new LOD[spec.Heights.Length];
                var level = new List<Renderer>();
                for (int l = 0; l < lods.Length; l++)
                {
                    level.Clear();
                    for (int i = 0; i < rs.Length && i < spec.Masks.Length; i++)
                        if ((spec.Masks[i] & (1 << l)) != 0)
                            level.Add(rs[i]);
                    lods[l] = new LOD(spec.Heights[l], level.ToArray()) { fadeTransitionWidth = spec.Fades[l] };
                }
                LODGroup g = carrier.AddComponent<LODGroup>();
                g.fadeMode = spec.FadeMode;
                g.animateCrossFading = spec.Animate;
                g.SetLODs(lods);
                Matrix4x4 group = root * spec.GroupLocal;
                Vector3 refWorld = group.MultiplyPoint3x4(spec.RefLocal);
                float sizeWorld = spec.Size * MaxScale(group);
                g.localReferencePoint = carrier.transform.InverseTransformPoint(refWorld);
                float own = MaxScale(carrier.transform.localToWorldMatrix);
                g.size = own > 0f ? sizeWorld / own : sizeWorld;
            }

            void DropFx(int row)
            {
                if (!_carriers.TryGetValue(row, out GameObject[] have))
                    return;
                foreach (GameObject go in have)
                    if (go != null)
                        UnityEngine.Object.Destroy(go);
                _carriers.Remove(row);
                LabCarriers--;
            }

            void SetReason(int row, byte why)
            {
                if (row < 0)
                    return;
                if (row >= _reason.Length)
                {
                    int old = _reason.Length;
                    int size = old;
                    while (size <= row)
                        size *= 2;
                    Array.Resize(ref _reason, size);
                    for (int i = old; i < size; i++)
                        _reason[i] = Unknown;
                }
                byte was = _reason[row];
                if (was == why)
                    return;
                if (was != Unknown && was != 0)
                    LabRealRows[was]--;
                if (why != Unknown && why != 0)
                    LabRealRows[why]++;
                _reason[row] = why;
            }

            internal byte ReasonOf(int row) => (uint)row < (uint)_reason.Length ? _reason[row] : Unknown;

            // ---- the registry's events (ISectorRowListener) ----------------------------------------------------

            public void RowAdded(SectorRec rec, int row)
            {
                try
                {
                    if (!Dead && SpanBuilt(rec.Slot))
                    {
                        bool had = Store.HandleOf(row) >= 0;
                        Decode(rec.Slot, row, true, out _);
                        // A new object in a built sector (placed there, or arrived over the network): its real one would fade
                        // in (LodFadeInOut), so does its proxy.
                        if (!had && Store.HandleOf(row) >= 0)
                            ProxyDraw.Born(row);
                    }
                }
                catch (Exception e)
                {
                    Failed(e);
                }
            }

            public void RowRemoved(SectorRec rec, int row)
            {
                try
                {
                    if (Dead)
                        return;
                    if (rec != null && Store.HasSpan(rec.Slot))
                        RemoveInstance(rec.Slot, row);
                    ProxyDraw.Gone(row);
                    SetReason(row, Unknown);
                }
                catch (Exception e)
                {
                    Failed(e);
                }
            }

            public void RowChanged(SectorRec rec, int row)
            {
                try
                {
                    if (!Dead && SpanBuilt(rec.Slot))
                        Redecode(rec.Slot, row);
                }
                catch (Exception e)
                {
                    Failed(e);
                }
            }

            public void RowCreated(int row, bool created)
            {
                try
                {
                    if (Dead)
                        return;
                    int h = Store.HandleOf(row);
                    if (h < 0)
                        return;
                    ProxyInst inst = Store.Get(h);
                    uint flags = created ? inst.Flags | (uint)ProxyFlags.Created : inst.Flags & ~(uint)ProxyFlags.Created;
                    Store.SetFlags(h, flags, Registry.RowRecSlot(row));
                    SyncFx(row, Store.Get(h));
                    if (created && (flags & (uint)ProxyFlags.InActive) == 0)
                    {
                        ZDO z = Registry.RowZdo(row);
                        _drain.Add(new Drain { Zdo = z, Id = z.m_uid });
                    }
                }
                catch (Exception e)
                {
                    Failed(e);
                }
            }

            public void SectorBuilt(SectorRec rec)
            {
                try
                {
                    if (!Dead)
                        Queue(rec.Slot);
                }
                catch (Exception e)
                {
                    Failed(e);
                }
            }

            public void SectorUnbuilt(SectorRec rec)
            {
                try
                {
                    if (Dead)
                        return;
                    // Rows dropped while the journal no longer follows (it will not be built again soon): what was proxied
                    // there is made by its areas. Otherwise it is built again within the registry's budget.
                    bool proxied = SpanBuilt(rec.Slot) && Store.SpanAt(rec.Slot).Count > Store.SpanAt(rec.Slot).InActive;
                    Unbuilt(rec);
                    if (proxied && !Registry.WillBuild)
                        Realize();
                }
                catch (Exception e)
                {
                    Failed(e);
                }
            }

            public void SectorDropped(SectorRec rec)
            {
                try
                {
                    if (Dead || !Store.HasSpan(rec.Slot))
                        return;
                    Unbuilt(rec);
                    Store.Free(rec.Slot);
                }
                catch (Exception e)
                {
                    Failed(e);
                }
            }

            // The span holds every row the registry keeps; a Ring role draws only the Distant ones (by their flag).
            public void RolesChanged(SectorRec rec)
            {
            }

            public void CreatedResynced()
            {
                try
                {
                    if (!Dead)
                        RefreshCreated();
                }
                catch (Exception e)
                {
                    Failed(e);
                }
            }

            public void RegistryDisposed()
            {
                Dead = true;
            }

            // ---- Created bits -------------------------------------------------------------------------------------

            // Every proxy's Created bit read again from the registry (its bits, or ZDO.Created while they are not exact).
            void RefreshCreated()
            {
                for (int slot = 0; slot < Store.SpanCapacity; slot++)
                {
                    if (!SpanBuilt(slot))
                        continue;
                    ref ProxyStore.Span s = ref Store.SpanAt(slot);
                    for (int k = 0; k < s.Count; k++)
                    {
                        int h = Store.HandleAt(slot, k);
                        ProxyInst inst = Store.Get(h);
                        bool created = Registry.RowCreated(inst.Row);
                        if (created == ((inst.Flags & (uint)ProxyFlags.Created) != 0))
                            continue;
                        Store.SetFlags(h, created ? inst.Flags | (uint)ProxyFlags.Created : inst.Flags & ~(uint)ProxyFlags.Created, slot);
                        SyncFx(inst.Row, Store.Get(h));
                        if (created && (inst.Flags & (uint)ProxyFlags.InActive) == 0)
                        {
                            ZDO z = Registry.RowZdo(inst.Row);
                            _drain.Add(new Drain { Zdo = z, Id = z.m_uid });
                        }
                    }
                }
            }

            // While the engine's instance events are not live the bits come from ZDO.Created every frame, so a proxy whose
            // object was made is never drawn beside it.
            void PollCreated() => RefreshCreated();

            // ---- the active-area boundary ---------------------------------------------------------------------

            // RemoteAreas' areas (draining ones keep their claims) as active areas, made again when they changed (looked at on
            // every question: an area made this frame counts at once) or the simulation distance did (once a frame); then the
            // boundary pass marks what lies inside them.
            void RefreshActive()
            {
                RemoteAreas areas = PortalSystem.Instance?.Areas;
                int version = areas != null ? areas.ActiveAreasVersion : -1;
                int frame = Time.frameCount;
                if (version == _activeVersion && frame == _activeFrame)
                    return;
                ZNet net = ZNet.instance;
                ZoneSystem zs = ZoneSystem.instance;
                if (net == null || zs == null)
                    return;
                _activeFrame = frame;
                SimulationDistance sim = net.GetSyncedSimulationDistance();
                float size = zs.m_zoneSize;
                if (version == _activeVersion && sim.Equals(_activeSim) && size == _activeZoneSize)
                    return;
                _activeVersion = version;
                _activeSim = sim;
                _activeZoneSize = size;
                _zones.Clear();
                areas?.CollectActiveZones(_zones);
                if (_active.Length < _zones.Count)
                    _active = new ActiveArea[Math.Max(_zones.Count, _active.Length * 2)];
                for (int i = 0; i < _zones.Count; i++)
                    _active[i] = ActiveArea.Of(_zones[i], sim, size);
                _activeCount = _zones.Count;
                _boundaryDirty = true;
            }

            bool InFarActive(Vector3 at)
            {
                for (int i = 0; i < _activeCount; i++)
                    if (_active[i].Holds(at))
                        return true;
                return false;
            }

            // An active zone within two zones of this one (an active area reaches 1.5 zones from its zone's centre).
            bool NearActive(Vector2s zone)
            {
                for (int i = 0; i < _activeCount; i++)
                    if (Math.Abs(_zones[i].x - zone.x) <= 2 && Math.Abs(_zones[i].y - zone.y) <= 2)
                        return true;
                return false;
            }

            // Every built span near an active area (or holding InActive proxies) has its InActive bits set again: one that
            // came into an active area is real now (its area lists again and makes it), one that left is a proxy (its
            // instance drains).
            void Boundary()
            {
                _boundaryDirty = false;
                LabBoundaryPasses++;
                bool realize = false;
                for (int slot = 0; slot < Store.SpanCapacity; slot++)
                {
                    if (!SpanBuilt(slot))
                        continue;
                    ref ProxyStore.Span s = ref Store.SpanAt(slot);
                    SectorRec rec = Registry.RecAt(slot);
                    if (rec == null || s.Count == 0)
                        continue;
                    bool near = NearActive(rec.Zone);
                    if (!near && s.InActive == 0)
                        continue;
                    for (int k = 0; k < s.Count; k++)
                    {
                        int h = Store.HandleAt(slot, k);
                        ProxyInst inst = Store.Get(h);
                        bool was = (inst.Flags & (uint)ProxyFlags.InActive) != 0;
                        bool now = near && InFarActive(new Vector3(inst.Root.m03, inst.Root.m13, inst.Root.m23));
                        if (was == now)
                            continue;
                        LabBoundaryFlips++;
                        uint flags = now ? inst.Flags | (uint)ProxyFlags.InActive : inst.Flags & ~(uint)ProxyFlags.InActive;
                        Store.SetFlags(h, flags, slot);
                        SyncFx(inst.Row, Store.Get(h));
                        SetReason(inst.Row, now ? (byte)ProxyReal.ActiveArea : (byte)ProxyReal.None);
                        if (now)
                            realize = true;
                        else if ((flags & (uint)ProxyFlags.Created) != 0)
                        {
                            ZDO z = Registry.RowZdo(inst.Row);
                            _drain.Add(new Drain { Zdo = z, Id = z.m_uid });
                        }
                    }
                }
                if (realize)
                    Realize();
            }

            // ---- the drain ------------------------------------------------------------------------------------------

            // Made objects whose proxy is built go the way the game's removal takes them, a few a frame; what the game's own
            // lists hold for the player waits until his zone changes.
            void RunDrain()
            {
                ZNet net = ZNet.instance;
                ZoneSystem zs = ZoneSystem.instance;
                ZNetScene scene = ZNetScene.instance;
                if (net == null || zs == null || scene == null)
                    return;
                Vector2s me = ZoneSystem.GetZone(net.GetReferencePosition());
                if (!_drainZoneKnown || me != _drainZone)
                {
                    _drainZone = me;
                    _drainZoneKnown = true;
                    if (_parked.Count > 0)
                    {
                        _drain.AddRange(_parked);
                        _parked.Clear();
                    }
                }
                if (_drainHead >= _drain.Count)
                {
                    _drain.Clear();
                    _drainHead = 0;
                    return;
                }
                if (!DrawRan)
                    return;
                SimulationDistance sim = net.GetSyncedSimulationDistance();
                Dictionary<ZDO, ZNetView> instances = s_instances(scene);
                _clock.Restart();
                int done = 0;
                while (_drainHead < _drain.Count && done < DrainPerFrame && _clock.Elapsed.TotalMilliseconds < DrainBudgetMs)
                {
                    Drain d = _drain[_drainHead++];
                    ZDO z = d.Zdo;
                    if (z == null || z.m_uid != d.Id || !z.Created)
                        continue;
                    int row = Registry.FindRow(z);
                    int h = Store.HandleOf(row);
                    if (h < 0)
                        continue;
                    ProxyInst inst = Store.Get(h);
                    if ((inst.Flags & (uint)ProxyFlags.InActive) != 0 || InFarActive(z.GetPosition()))
                        continue;
                    if (GameLists(z, me, sim, zs))
                    {
                        _parked.Add(d);
                        LabParked++;
                        continue;
                    }
                    if (!instances.TryGetValue(z, out ZNetView view) || view == null || !ReferenceEquals(view.GetZDO(), z))
                        continue;
                    // ZNetScene.RemoveObjects' steps for one object (persistent: its ZDO stays), the instance index told.
                    GameObject go = view.gameObject;
                    view.ResetZDO();
                    instances.Remove(z);
                    UnityEngine.Object.Destroy(go);
                    SectorInstanceIndexModule.NotifyChanged(z);
                    // The proxy shows in this frame (the instance event may not be live to clear the bit).
                    inst = Store.Get(h);
                    if ((inst.Flags & (uint)ProxyFlags.Created) != 0)
                        Store.SetFlags(h, inst.Flags & ~(uint)ProxyFlags.Created, Registry.RowRecSlot(row));
                    LabDrained++;
                    done++;
                }
                if (_drainHead >= _drain.Count)
                {
                    _drain.Clear();
                    _drainHead = 0;
                }
                else if (_drainHead > 1024 && _drainHead * 2 > _drain.Count)
                {
                    _drain.RemoveRange(0, _drainHead);
                    _drainHead = 0;
                }
            }

            // The game's own lists for the player hold it (ZNetScene.CreateDestroyObjects: ZDOMan.FindSectorObjects round his
            // zone): every object of his near zones, the Distant ones of his ring.
            static bool GameLists(ZDO z, Vector2s me, SimulationDistance sim, ZoneSystem zs)
            {
                Vector2s s = z.GetSector();
                int cheb = Math.Max(Math.Abs(s.x - me.x), Math.Abs(s.y - me.y));
                int near = sim.NearSimulationDistance;
                if (cheb <= near && (cheb == 0 || sim.IsClassic || zs.ZonesWithinRadius(me, s, near)))
                    return true;
                int total = sim.TotalSimulationDistance;
                return z.Distant && cheb <= total && (sim.IsClassic || zs.ZonesWithinRadius(me, s, total, ghostZone: true));
            }

            // ---- answers ------------------------------------------------------------------------------------------

            internal ProxyState StateOf(ZDO z)
            {
                RefreshActive();
                int row = Registry.FindRow(z);
                if (row >= 0)
                {
                    int slot = Registry.RowRecSlot(row);
                    if (slot >= 0 && SpanBuilt(slot))
                    {
                        int h = Store.HandleOf(row);
                        if (h >= 0)
                        {
                            ProxyInst inst = Store.Get(h);
                            if ((inst.Flags & (uint)ProxyFlags.InActive) != 0 || InFarActive(z.GetPosition()))
                                return ProxyState.Real;
                            return ProxyState.Proxy;
                        }
                        return ReasonOf(row) == Unknown ? Predict(z) : ProxyState.Real;
                    }
                    return Registry.WillBuild ? Predict(z) : ProxyState.Real;
                }
                // No row: a registered sector not built yet will be (its rows decide then); one built that has no row for it
                // does not list it.
                if (!Registry.WillBuild)
                    return ProxyState.Real;
                uint sector = ZoneSystem.GetSectorIndex(z.GetPosition()).Sector;
                if (sector == 0 || !Registry.TryRec(sector, out SectorRec rec) || rec.Built)
                    return ProxyState.Real;
                return Predict(z);
            }

            // Before its row is decoded: Waiting when its prefab may be proxied (or is not classified yet) and it lies outside
            // every far active area, else Real.
            ProxyState Predict(ZDO z)
            {
                if (!z.Persistent)
                    return ProxyState.Real;
                Vector3 at = z.GetPosition();
                if (at.y > ProxyTemplates.InteriorHeight || InFarActive(at))
                    return ProxyState.Real;
                ProxyTemplates.VisualTemplate t = ProxyTemplates.Peek(z.GetPrefab());
                return t == null || t.Proxyable ? ProxyState.Waiting : ProxyState.Real;
            }

            internal bool TryGet(ZDO z, out ProxyInst inst) => TryGetAny(z, out inst) && (inst.Flags & (uint)ProxyFlags.InActive) == 0;

            internal bool TryGetAny(ZDO z, out ProxyInst inst)
            {
                inst = default;
                int h = Store.HandleOf(Registry.FindRow(z));
                if (h < 0)
                    return false;
                inst = Store.Get(h);
                return true;
            }

            internal void AddSpan(int slot, uint sector, SectorRole role, bool distantOnly, List<ProxySpanRef> into)
            {
                if (!Store.SpanData(slot, out NativeArray<ProxyInst> page, out int start, out int count, out Bounds bounds))
                    return;
                into.Add(new ProxySpanRef { Page = page, Start = start, Count = count, Sector = sector, Role = role, DistantOnly = distantOnly, Bounds = bounds });
            }

            // Proxies drawn now (not Created, InActive or Hidden); counted at most once a frame.
            internal int LiveDrawn()
            {
                if (_liveFrame == Time.frameCount)
                    return _liveDrawn;
                _liveFrame = Time.frameCount;
                int n = 0;
                for (int slot = 0; slot < Store.SpanCapacity; slot++)
                {
                    if (!Store.SpanData(slot, out NativeArray<ProxyInst> page, out int start, out int count, out _))
                        continue;
                    for (int k = 0; k < count; k++)
                        if (page[start + k].Drawn)
                            n++;
                }
                _liveDrawn = n;
                return n;
            }

            internal string Describe()
            {
                int spans = 0, built = 0;
                for (int slot = 0; slot < Store.SpanCapacity; slot++)
                {
                    if (!Store.HasSpan(slot) || Store.SpanAt(slot).Page < 0)
                        continue;
                    spans++;
                    if (Store.SpanAt(slot).Built)
                        built++;
                }
                return $"{(Retiring ? "retiring" : "proxying")}, {Store.Count} proxies ({LiveDrawn()} drawn now) in {built}/{spans} sector spans, {Store.PageCount} pages " +
                       $"(~{Store.NativeBytes / 1024} KB native, ~{Store.ManagedBytes / 1024} KB managed; tables ~{ProxyTables.NativeBytes / 1024} KB), " +
                       $"queued sectors {_queue.Count - _queueHead}, deferred rows {_deferred.Count}, drain {_drain.Count - _drainHead} (+{_parked.Count} kept for the player), " +
                       $"active areas {_activeCount}, created bits {(Registry.CreatedExact ? "from the instance events" : "polled")}";
            }
        }
    }
}
