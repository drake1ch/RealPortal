using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace RealPortal.Engine
{
    // Zone unloading. Ten times a second ZoneSystem.UpdateTTL asks ZNetScene.HaveInstanceInSector, for every zone
    // nobody poked for 4 s, whether a loaded non-distant object still stands in it, and unloads the first one that is
    // empty. The game answers by walking every loaded object (ZNetScene.m_instances) and reading its transform until
    // one is in that zone. A zone that still holds objects costs a partial walk on every tick, an empty one (about to
    // be unloaded) a full walk: ~5 ms with 110k far-area objects, and one more every tick while a far area drains (its
    // zones expire one after the other).
    //
    // This keeps a mirror of m_instances with every object filed under the zone it stood in when last looked at, and
    // under the quarter of that zone (32 x 32 m), and answers from there with the game's own test (alive, not
    // m_distant, ZoneSystem.GetZone(transform.position)):
    //  - "occupied" is said only after that test passed, right now, for an object in the game's table: exactly the
    //    game's answer;
    //  - "empty" is said after the test failed, right now, for everything that could stand in the zone, all of it found
    //    from the zone and the zones around it, so an answer costs what is filed there, not what is loaded:
    //    . everything filed in the zone;
    //    . still objects filed in the 8 zones around it less than 32 m (Margin) from its edge; only the quarters of
    //      those zones that reach that band are looked through (2 of an edge neighbour, 1 of a corner one);
    //    . moving objects (Rigidbody or ZSyncTransform, new objects for their first 2 s, anything seen moving) filed
    //      around it as far as each could have come since its position was last read: 32 m plus 50 m/s
    //      (MaxMoverSpeed) times the age of that read, object by object. Each is looked at and filed again within
    //      1 s (MovingPeriod), oldest first, whatever the frame rate, so the zones looked through reach as far as the
    //      oldest filing allows: 5 x 5 in practice (the facing quarters of the outer ring), wider while the upkeep
    //      catches up. Ages are taken on a clock that runs at least as fast as game time and real time (bodies move
    //      by game time, what the network places by real time, and a frame's Time.deltaTime is capped at 0.33 s);
    //    . objects made since the last frame's upkeep, and objects reported moved (NotifyMoved), wherever they are.
    //    Every listed object is looked at again, oldest look first, within 2.5 s of game time whatever the frame rate,
    //    the number of objects or how many were just taken out (what is overdue is looked at on top of the frame's
    //    share, up to 4096 a frame), so the 32 m is for a look under 3 s old. Should the oldest look be older (a burst
    //    too big for that), the band widens with it (32 m per 3 s: the whole of the 8 zones at 6 s); past 6 s the
    //    game's walk answers. Forgotten objects (a far area that drained) cost the answers nothing: they are only
    //    skipped at the front of the queues, so a drain does not hand the answers back to the game.
    // What that can miss, in a zone that expired at that very moment with nothing else counted in it:
    //  - an object with neither Rigidbody nor ZSyncTransform that stood still for over 2 s and then, before it was
    //    looked at again (within 3 s), travelled more than 32 m (more than 32 m per 3 s for an older look, up to 6 s).
    //    Nothing in the game moves like that (what follows a character or flies, like Aoe or LuredWisp, moves all the
    //    time and stays on the moving list). A rebuild files what it finds as it stands (still unless it has a body),
    //    so this also covers an object made just before a rebuild and moved again by its maker;
    //  - a moving object faster than 50 m/s on average since its position was read, past the 32 m on top: covered up
    //    to about 74 m/s for the oldest read (1.33 s), faster for younger ones. Player run and sprint, mounts, ships
    //    and flyers (Character.m_flyFastSpeed 12, birds and Valkyrie 10) are well under 50, LuredWisp caps at 40 and
    //    debug fly at its default speed tops 50 (20 x 2.5 running). Faster are projectiles (arbalest and ballista
    //    bolts) and debug fly with its speed raised;
    //  - a jump, bodies included: a remote object snapping to its synced position (ZSyncTransform snaps past 5 m, and
    //    after a network stall longer than its 2 s of extrapolation), a remote player through a portal or a Real
    //    Portal crossing, an arrow stuck in a creature that crosses: missed until its next filing (within about 1 s)
    //    unless whoever moved it calls NotifyMoved;
    //  - an object that stops being m_distant (nothing in the game does; a mod could), until the audit reaches it
    //    (within 2.5 s, 6 s when behind), unless NotifyMoved is called;
    //  - a new object its maker moved again more than a frame after making it, past what the moving list covers.
    // Why that is harmless. Zones expire (a) outside every area this game keeps; (b) in a far area that drains: its
    // zones are no longer poked from RemoteAreas.StartDraining while KeepAlive keeps its objects loaded for tens of
    // seconds; (c) inside the player's own area while ZoneSystem.CreateLocalZones spawns zones: it stops at each zone
    // it spawns, so the zones after the first unspawned one in its loop miss that tick's poke. In (c) the zone's own
    // vegetation and pieces are filed in it and found first. In (b) what stands there is static and released
    // (RemoteAreas.ReleaseSome sets the owner to 0, so ZSyncTransform.ClientSync turns gravity off), and a miss needs
    // something that came from beyond the reach. What a miss leaves standing in (a) or (b) is not simulated here
    // (released, or owned by another peer: either way a non-owned body gets no gravity) or is about to be removed
    // with its area, so the zone's ground unloading under it drops nothing, and a zone still wanted is spawned again
    // by its next poke. The one thing this game simulates that can be missed is its own projectile faster than
    // ~74 m/s shot into an expiring zone next to the player's: it misses that zone's ground. In verify runs with
    // other players these cases show up as LabFalseEmpty.
    //
    // The mirror follows every place the game changes m_instances (AddInstance, Destroy, OnZDODestroyed,
    // RemoveObjects, Shutdown) by re-reading the game's table for the keys it touched, so it stays exact whatever other
    // patches did there. Those hooks are finalizers: they also run when the method, or another mod's patch on it,
    // throws part way (RemoveObjects does on a view destroyed behind the game's back). Mods that change the table
    // directly are caught by a count check before every answer and by the audit, which checks each listed object is
    // still in the table. What Path of Valheiman's sweep takes out (entries whose view died or lost its ZDO, swept
    // after a failed streaming tick and every 5 s) is known beforehand (ZNetView.OnDestroy, the audit, the finalizers)
    // and settled on the spot; anything else (Jotunn's undo) brings the mirror back in line over a few frames, the
    // game answering meanwhile. Errors inside the index do the same, from an emptied mirror.
    // Off: the game's walk, untouched; the mirror is dropped and its memory given back, and rebuilt the same way when
    // switched on again.
    // Client- and host-local: it only decides when this game unloads zone roots, as the game itself would.
    //
    // Instance events (ARCHITECTURE-v2 §3.3 and §3.12, FARWORLD-V3 §3.2 h). InstanceAdded(zdo, view) and
    // InstanceRemoved(zdo, id) report every key the mirror gains or loses, i.e. every change of m_instances (a key whose
    // view was replaced reports Removed, then Added): Real Portal's FarWorld flips its rows' Created bits from them in the
    // same frame. They are raised at the end of the hook (or the frame's upkeep) that saw the change, never in the middle
    // of the mirror's own work, and only while InstanceEventsLive: installed, on, bound to the scene and complete (every
    // change since the last full rebuild pass went through the mirror; what it corrects later, a sweep behind the hooks
    // or a count mismatch, comes as events too). An emptied mirror (an error, the table replaced, giving up, switched off,
    // a new scene) is not live again until a full rebuild pass is done, and every change of InstanceEventsLive bumps
    // InstanceEpoch: a consumer that sees an epoch it did not sync at re-reads its rows from the game (ZDO.Created,
    // ZNetScene.HaveInstance) and follows the events from there; while not live it follows the game another way (its own
    // hooks on AddInstance, Destroy and OnZDODestroyed). Nothing is queued while nobody subscribed; a subscriber costs a
    // struct store per change and a delegate call per event.
    [EngineModule]
    public static class SectorInstanceIndexModule
    {
        static readonly Feature s_feature = Features.Register("SectorInstanceIndex", true,
            "Zone unloading asks 'is anything still standing in this zone?' from a per-zone index of loaded objects " +
            "instead of walking all of them (ZNetScene.HaveInstanceInSector).");

        static readonly Feature s_verify = Features.Register("SectorInstanceIndexVerify", false,
            "Lab: the game answers HaveInstanceInSector with its own walk, the index answers too, and every " +
            "disagreement is counted (LabFalseEmpty, LabFalseOccupied) and logged.");

        // New objects without Rigidbody or ZSyncTransform count as moving this long (set right after they are made,
        // settling, following something).
        const float Probation = 2f;
        // The audit's pace: every listed object is looked at again within this long of game time...
        const float AuditCycle = 2.5f;
        // ...and a still object is taken to drift at most Margin in this long (the margin over AuditCycle covers one
        // slow frame). With older looks the band around the zone widens in proportion, up to twice this.
        const float AuditPeriod = 3f;
        // Moving objects are looked at and filed again within this long (on the mover clock, see Clock).
        const float MovingPeriod = 1f;
        // The fastest a moving object is taken to travel, on average since its position was read (see the header).
        const float MaxMoverSpeed = 50f;
        // How far outside a zone a still object may stand and still be looked at before saying "empty"; moving
        // objects get it on top of their travel.
        const float Margin = 32f;
        const float HalfZone = 32f;
        const float ZoneSize = 64f;
        // On a zone's outer bounds when deciding which of its quarters can hold an object in reach: GetZone rounds
        // through float, so an object filed in a zone may stand a hair outside it.
        const float Slack = 1f;
        // Moving objects are looked for in up to this many rings of zones around the asked one; a reach past that
        // (the upkeep far behind) looks through every zone's moving objects instead.
        const int MaxRings = 3;
        // A pass's share of a frame (audit, moving) is held to this unless its queue is behind (its oldest look more
        // than Behind past its cycle): what waits comes due and is looked at as overdue, so a slow frame does not make
        // the next one slow too.
        const int ShareCap = 2048;
        const float Behind = 0.5f;
        // Looks per frame and pass on top of the frame's share, for what is overdue.
        const int OverdueCap = 4096;
        // Bringing the mirror back in line: keys per frame; up to this many it is done at once.
        const int ResyncPerFrame = 2048;
        const int ResyncAtOnce = 4096;
        // Zones taken out by Tidy kept for reuse, so filing an object in a zone that had emptied does not allocate.
        const int CellPool = 512;
        // The smallest a list is trimmed to.
        const int MinCapacity = 16;
        // Lab: calls remembered for the recent-call counters.
        const int Recent = 256;

        // ---- the game's private parts ----------------------------------------------------------------

        static readonly AccessTools.FieldRef<ZNetScene, Dictionary<ZDO, ZNetView>> s_instancesOf =
            AccessTools.FieldRefAccess<ZNetScene, Dictionary<ZDO, ZNetView>>("m_instances");
        static readonly AccessTools.FieldRef<ZNetScene, List<ZNetView>> s_removedOf =
            AccessTools.FieldRefAccess<ZNetScene, List<ZNetView>>("m_tempRemoved");
        static readonly AccessTools.FieldRef<ZNetView, Rigidbody> s_bodyOf =
            AccessTools.FieldRefAccess<ZNetView, Rigidbody>("m_body");

        // Set once every patch is in. A failed install leaves whatever Harmony could not take back inert.
        static bool s_installed;

        static void Install(Harmony harmony)
        {
            Type scene = typeof(ZNetScene);
            // Everything is looked up before anything is patched: a game update that renamed one of them leaves the
            // module out whole instead of half-installed.
            MethodInfo have = Need(AccessTools.DeclaredMethod(scene, nameof(ZNetScene.HaveInstanceInSector), new[] { typeof(Vector2s) }), "HaveInstanceInSector");
            MethodInfo add = Need(AccessTools.DeclaredMethod(scene, nameof(ZNetScene.AddInstance), new[] { typeof(ZDO), typeof(ZNetView) }), "AddInstance");
            MethodInfo destroy = Need(AccessTools.DeclaredMethod(scene, nameof(ZNetScene.Destroy), new[] { typeof(GameObject) }), "Destroy");
            MethodInfo zdoDestroyed = Need(AccessTools.DeclaredMethod(scene, "OnZDODestroyed", new[] { typeof(ZDO) }), "OnZDODestroyed");
            MethodInfo removeObjects = Need(AccessTools.DeclaredMethod(scene, "RemoveObjects"), "RemoveObjects");
            MethodInfo shutdown = Need(AccessTools.DeclaredMethod(scene, nameof(ZNetScene.Shutdown)), "Shutdown");
            MethodInfo onDestroy = Need(AccessTools.DeclaredMethod(scene, "OnDestroy"), "OnDestroy");
            MethodInfo update = Need(AccessTools.DeclaredMethod(scene, "Update"), "Update");
            // Optional: without it a view destroyed behind the game's back is only noticed when the audit reaches it,
            // and a sweep that comes first costs a rebuild.
            MethodInfo viewDestroy = AccessTools.DeclaredMethod(typeof(ZNetView), "OnDestroy");

            // Either way the mirror starts over: off frees it, on rebuilds it from the game's table (lazily, from the
            // next hook or frame). Registered first, so no failure below leaves a switch that does not drop it.
            s_feature.Switched += _ => Drop();

            Type t = typeof(SectorInstanceIndexModule);
            HarmonyMethod Own(string name) => new HarmonyMethod(t, name);
            var attempted = new List<KeyValuePair<MethodBase, MethodInfo>>();
            void Patch(MethodBase original, HarmonyMethod prefix = null, HarmonyMethod postfix = null, HarmonyMethod finalizer = null)
            {
                foreach (HarmonyMethod m in new[] { prefix, postfix, finalizer })
                    if (m != null)
                        attempted.Add(new KeyValuePair<MethodBase, MethodInfo>(original, m.method));
                harmony.Patch(original, prefix: prefix, postfix: postfix, finalizer: finalizer);
            }
            try
            {
                // Last, so other mods' prefixes see the call (and may answer it) before the index does.
                Patch(have, prefix: new HarmonyMethod(t, nameof(HavePrefix)) { priority = Priority.Last }, postfix: Own(nameof(HavePostfix)));
                Patch(add, finalizer: Own(nameof(AddFinalizer)));
                Patch(destroy, prefix: Own(nameof(DestroyPrefix)), finalizer: Own(nameof(DestroyFinalizer)));
                Patch(zdoDestroyed, finalizer: Own(nameof(ZdoDestroyedFinalizer)));
                Patch(removeObjects, prefix: Own(nameof(RemoveObjectsPrefix)), finalizer: Own(nameof(RemoveObjectsFinalizer)));
                Patch(shutdown, postfix: Own(nameof(ShutdownPostfix)));
                Patch(onDestroy, postfix: Own(nameof(OnDestroyPostfix)));
                Patch(update, postfix: Own(nameof(UpdatePostfix)));
                if (viewDestroy != null)
                    Patch(viewDestroy, prefix: Own(nameof(ViewDestroyPrefix)));
                else
                    EnginePlugin.Log?.LogWarning("SectorInstanceIndex: ZNetView.OnDestroy not found; views destroyed behind " +
                        "the game's back are only noticed by the audit");
            }
            catch
            {
                // Out whole, as the plugin reports: take back what went in. Only our own patch methods, since the
                // Harmony instance is shared with the other engine modules. Anything that cannot be taken back stays
                // inert (s_installed is false), so the game runs as without this module.
                foreach (KeyValuePair<MethodBase, MethodInfo> p in attempted)
                {
                    try
                    {
                        harmony.Unpatch(p.Key, p.Value);
                    }
                    catch (Exception e)
                    {
                        EnginePlugin.Log?.LogWarning($"SectorInstanceIndex: could not take back {p.Value.Name} on {p.Key.Name}: {e.Message}");
                    }
                }
                throw;
            }
            s_installed = true;
            UpdateLive();
        }

        static MethodInfo Need(MethodInfo m, string name) =>
            m ?? throw new MissingMethodException(nameof(ZNetScene), name);

        // ---- instance events ----------------------------------------------------------------------------

        public delegate void InstanceAddedHandler(ZDO zdo, ZNetView view);
        public delegate void InstanceRemovedHandler(ZDO zdo, ZDOID id);

        // The game's table gained a key: zdo (live, its m_uid valid during the call) has the view now (a mod may have
        // destroyed it already). Main thread, at the end of the hook that saw it: ZNetScene.AddInstance from ZNetView.Awake,
        // so inside ZNetScene.CreateObject's Instantiate for objects the game loads, before the object's other components
        // woke up. Record what you need; do not touch the object. Only while InstanceEventsLive.
        public static event InstanceAddedHandler InstanceAdded;
        // The game's table lost a key (ZNetScene.Destroy, OnZDODestroyed, RemoveObjects, a sweep behind the hooks): id is
        // the key's id when it was listed. The ZDO object may already be back in ZDOPool or handed out again under another
        // id, so match by id. Only while InstanceEventsLive.
        public static event InstanceRemovedHandler InstanceRemoved;

        // The events follow the game's table now (see the header); while false nothing is raised.
        public static bool InstanceEventsLive => s_live;
        // Bumped whenever InstanceEventsLive changes: a consumer re-reads its rows when this differs from the epoch it
        // synced at (start with 0: a live mirror is epoch 1 or later).
        public static int InstanceEpoch => s_epoch;

        // Lab: events raised, and exceptions from handlers.
        public static long LabInstanceAdded, LabInstanceRemoved, LabHandlerErrors;

        struct Change
        {
            public ZDO Zdo;
            public ZDOID Id;
            public ZNetView View;
            public bool Added;
        }

        // What the current hook saw, raised at its end (Flush). Grows to the largest burst (a rebuild step, a far area's
        // RemoveObjects) and stays.
        static Change[] s_changes = new Change[64];
        static int s_changeCount;
        static bool s_flushing;
        // s_complete: a full rebuild pass ran since the mirror was last emptied.
        static bool s_live, s_complete;
        static int s_epoch;

        // InstanceEventsLive as it stands now. Every change is an edge (InstanceEpoch) and drops what was queued: the
        // consumer re-reads from the game.
        static void UpdateLive()
        {
            bool live = s_installed && s_feature.On && !s_gaveUp && s_instances != null && s_complete;
            if (live == s_live)
                return;
            s_live = live;
            s_epoch++;
            Array.Clear(s_changes, 0, s_changeCount);
            s_changeCount = 0;
        }

        // The mirror gained or lost a key: kept for the end of the hook. Nothing while not live or nobody listens.
        static void Queue(ZDO zdo, ZDOID id, ZNetView view, bool added)
        {
            if (!s_live || (added ? InstanceAdded == null : InstanceRemoved == null))
                return;
            if (s_changeCount == s_changes.Length)
                Array.Resize(ref s_changes, s_changes.Length * 2);
            ref Change c = ref s_changes[s_changeCount++];
            c.Zdo = zdo;
            c.Id = id;
            c.View = view;
            c.Added = added;
        }

        // Raises what the hook queued, in order. A handler that changes the table itself (its hooks queue more) has that
        // raised in this same loop; one that switches the module (not live any more) drops the rest.
        static void Flush()
        {
            if (s_flushing || s_changeCount == 0)
                return;
            s_flushing = true;
            try
            {
                for (int i = 0; i < s_changeCount; i++)
                {
                    Change c = s_changes[i];
                    s_changes[i] = default;
                    if (c.Added)
                    {
                        // Recycled since (an earlier handler destroyed it): its Removed comes by id.
                        if (c.Zdo == null || c.Zdo.m_uid != c.Id)
                            continue;
                        LabInstanceAdded++;
                        InstanceAddedHandler added = InstanceAdded;
                        if (added == null)
                            continue;
                        try
                        {
                            added(c.Zdo, c.View);
                        }
                        catch (Exception e)
                        {
                            HandlerFailed(nameof(InstanceAdded), e);
                        }
                    }
                    else
                    {
                        LabInstanceRemoved++;
                        InstanceRemovedHandler removed = InstanceRemoved;
                        if (removed == null)
                            continue;
                        try
                        {
                            removed(c.Zdo, c.Id);
                        }
                        catch (Exception e)
                        {
                            HandlerFailed(nameof(InstanceRemoved), e);
                        }
                    }
                }
            }
            finally
            {
                s_changeCount = 0;
                s_flushing = false;
            }
        }

        static void HandlerFailed(string what, Exception e)
        {
            LabHandlerErrors++;
            if (LabHandlerErrors <= 10)
                EnginePlugin.Log?.LogError($"SectorInstanceIndex: an {what} handler failed: {e}");
        }

        // ---- the mirror ---------------------------------------------------------------------------------

        sealed class Entry
        {
            public ZDO Zdo;
            // The key's id when it was listed (InstanceRemoved: by then the ZDO object may be pooled or reused).
            public ZDOID Id;
            public ZNetView View;
            public Transform T;
            // Where it stood when last looked at (only x and z decide the zone), that zone, and when that look was
            // (on the mover clock).
            public float X, Z;
            public int Key;
            public double Look;
            // When it was last seen moving (or made): a still object leaves the moving list Probation after that.
            public float Since;
            // When it went to the back of the audit queue (game time); a listed object's position was read then.
            public float Stamp;
            // When it went to the back of the moving queue (mover clock); its position was read then, so it has been
            // filed, and looked at, at least as recently.
            public double MStamp;
            // Filed: its zone, the quarter of it (Quad), the list there (Cell.Still or, when Moving, Cell.Moving) and
            // its place in it; null when not filed (new, distant or dead).
            public Cell Cell;
            public int Quad;
            public List<Entry> Bucket;
            public int Slot = -1;
            // New (or no longer distant, or reported moved), not filed yet: its place in s_unfiled (-1: not there) and
            // the frame it came.
            public int UnfiledSlot = -1;
            public int AddedFrame;
            // Has a Rigidbody or ZSyncTransform: always moving.
            public bool Mover;
            // On the moving list: filed in Cell.Moving (or will be, when it is filed) and in the moving queue.
            public bool Moving;
            // In s_movers (it may have left the moving list since: the queue lets go of it when it reaches it).
            public bool MQueued;
            // m_distant (the game's test skips these), and destroyed while still in the table (skipped too).
            public bool Distant, Dead;
            // In the mirror (s_byZdo). The queues and the suspects list let go of an entry once this is false.
            public bool Listed;
            // On s_suspects.
            public bool Suspect;
        }

        // One zone's filed objects, by quarter: bit 0 set past the zone's centre on x, bit 1 past it on z (Quad).
        sealed class Cell
        {
            public int Key;
            // The zone's centre.
            public float CX, CZ;
            public readonly List<Entry>[] Still = Lists();
            public readonly List<Entry>[] Moving = Lists();
            // Tidy passes in a row that found it empty.
            public int Empty;

            public void Reset(int key)
            {
                Key = key;
                CX = CentreX(key);
                CZ = CentreZ(key);
                Empty = 0;
            }

            public bool IsEmpty()
            {
                for (int q = 0; q < 4; q++)
                    if (Still[q].Count > 0 || Moving[q].Count > 0)
                        return false;
                return true;
            }

            static List<Entry>[] Lists() => new[] { new List<Entry>(), new List<Entry>(), new List<Entry>(), new List<Entry>() };
        }

        sealed class ByRef<T> : IEqualityComparer<T> where T : class
        {
            internal static readonly ByRef<T> Instance = new ByRef<T>();
            public bool Equals(T x, T y) => ReferenceEquals(x, y);
            public int GetHashCode(T o) => RuntimeHelpers.GetHashCode(o);
        }

        static ZNetScene s_scene;
        static Dictionary<ZDO, ZNetView> s_instances;
        // The game's table compares ZDOs by id, and the game recycles ZDO objects under new ids. One live ZDO object
        // per id, so keying by the object is the same set of keys, and an entry missed on the way out (a mod removing it
        // behind the hooks) can still be found and dropped after its ZDO was recycled.
        // Not readonly: dropping the mirror replaces these, since Clear keeps every table at its peak size.
        static Dictionary<ZDO, Entry> s_byZdo = new Dictionary<ZDO, Entry>(ByRef<ZDO>.Instance);
        // RemoveObjects hands back views whose ZDO it already cleared: this finds their key.
        static Dictionary<ZNetView, Entry> s_byView = new Dictionary<ZNetView, Entry>(ByRef<ZNetView>.Instance);
        // The zones, by key and as a list (walked by Tidy and by a mover search past MaxRings; only Tidy takes cells
        // out, never while answering), and emptied ones kept for reuse.
        static Dictionary<int, Cell> s_cells = new Dictionary<int, Cell>();
        static List<Cell> s_cellList = new List<Cell>();
        static List<Cell> s_cellPool = new List<Cell>();
        // New objects not filed yet: looked at by every answer, filed by the next frame's upkeep.
        static List<Entry> s_unfiled = new List<Entry>();
        // Every filed moving object, oldest filing first (ones that left the list are let go when they reach the
        // front). Its front says how long ago the oldest moving object was filed, which the mover rings are made from.
        static Queue<Entry> s_movers = new Queue<Entry>();
        static int s_movingCount;
        // Every listed object, oldest look first (forgotten ones are let go when they reach the front). Its front says
        // how old the oldest look is, which the band around a zone is made from.
        static Queue<Entry> s_audit = new Queue<Entry>();
        // Listed objects whose view was destroyed, or lost its ZDO, while the game's table still has them: what a sweep
        // behind the hooks (Path of Valheiman's) takes out.
        static List<Entry> s_suspects = new List<Entry>();
        static List<ZDO> s_snapshot = new List<ZDO>();

        static bool s_trusted, s_resyncing, s_gaveUp;
        // A rebuild is adding what it found: filed as it stands rather than as new.
        static bool s_resyncFiling;
        static int s_snapshotAt;
        static int s_trips;
        static float s_tripAt = -100f, s_tidyAt;

        // The mover clock (see Clock): its reading at the start of the current answer or upkeep, which every look
        // and filing is stamped with, and the reading the moving upkeep last ran at.
        static double s_clock, s_clockGame, s_clockReal;
        static double s_now;
        static double s_movingAt;

        // Lab: queries answered by the index, by the game (index not ready), "occupied" and "empty" answers, "empty"
        // answers left to the game because the oldest look was too old, transforms read while answering, still objects
        // found moved, rebuilds, count mismatches, mismatches settled from the suspects, verify-mode disagreements, and
        // the slowest answer.
        public static int LabQueries, LabFallbacks, LabOccupied, LabEmpty, LabStale, LabRead, LabMoved, LabResyncs, LabTrips, LabSettled;
        public static int LabFalseEmpty, LabFalseOccupied;
        public static double LabSlowestMs;
        // Lab: entries looked through while answering (tested, or passed over by distance) over all queries, and the
        // most in one query; the most transforms read in one query; answers whose mover reach went past MaxRings (every
        // zone's moving objects looked through); the widest mover neighbourhood used (rings around the zone); looks
        // done past a frame's share because they were overdue (audit and moving); the oldest look (game time) and the
        // oldest mover filing (mover clock) seen by an answer, in seconds.
        public static long LabExamined;
        public static int LabExaminedMax, LabReadMax, LabWide, LabRingsMax, LabOverdue;
        public static double LabStillAgeMax, LabMoverAgeMax;
        // Lab, "empty" answers only (what unloading a zone costs, the case the index is for): entries looked through,
        // transforms read and milliseconds, in all and the most in one.
        public static long LabEmptyExamined, LabEmptyRead;
        public static int LabEmptyExaminedMax, LabEmptyReadMax;
        public static double LabEmptyMs, LabEmptyMsMax;

        public static double LabExaminedMean => LabQueries > 0 ? (double)LabExamined / LabQueries : 0.0;
        public static double LabEmptyExaminedMean => LabEmpty > 0 ? (double)LabEmptyExamined / LabEmpty : 0.0;
        public static double LabEmptyReadMean => LabEmpty > 0 ? (double)LabEmptyRead / LabEmpty : 0.0;
        public static double LabEmptyMsMean => LabEmpty > 0 ? LabEmptyMs / LabEmpty : 0.0;
        // Lab, the last Recent calls (LabRecentCalls of them so far): answered "empty", "occupied", left to the game
        // (index not ready, or its look too old); entries looked through and transforms read per call the index
        // looked at (mean, max), and its time per such call; the same for its "empty" answers only.
        public static int LabRecentCalls => s_recentCount;
        public static int LabRecentEmpty => RecentCount(0);
        public static int LabRecentOccupied => RecentCount(1);
        public static int LabRecentToGame => s_recentCount - RecentCount(0) - RecentCount(1);
        public static double LabRecentExaminedMean => RecentMean(s_recentExamined, false);
        public static int LabRecentExaminedMax => RecentMax(s_recentExamined, false);
        public static double LabRecentReadMean => RecentMean(s_recentRead, false);
        public static int LabRecentReadMax => RecentMax(s_recentRead, false);
        public static double LabRecentMsMean => RecentMsMean(false);
        public static double LabRecentEmptyExaminedMean => RecentMean(s_recentExamined, true);
        public static int LabRecentEmptyExaminedMax => RecentMax(s_recentExamined, true);
        public static double LabRecentEmptyReadMean => RecentMean(s_recentRead, true);
        public static int LabRecentEmptyReadMax => RecentMax(s_recentRead, true);
        public static double LabRecentEmptyMsMean => RecentMsMean(true);
        public static double LabRecentEmptyMsMax => RecentMsMax(true);
        // Lab gauges: objects on the moving list, new ones not filed yet, zones, the oldest look and mover filing now
        // (entries forgotten, or off the moving list, since the queues were last trimmed are passed over, as the
        // answers do; nothing is taken out here).
        public static int LabMovingNow => s_movingCount;
        public static int LabUnfiledNow => s_unfiled.Count;
        public static int LabZonesNow => s_cells.Count;
        public static double LabOldestLookNow
        {
            get
            {
                foreach (Entry e in s_audit)
                    if (e.Listed)
                        return Time.time - e.Stamp;
                return 0.0;
            }
        }
        public static double LabOldestMoverNow
        {
            get
            {
                foreach (Entry e in s_movers)
                    if (FiledMoving(e))
                        return PeekClock() - e.MStamp;
                return 0.0;
            }
        }

        // The recent calls: answer (1 occupied, 0 empty, -1 left to the game by the index, -2 index not ready),
        // entries looked through, transforms read, milliseconds.
        static readonly sbyte[] s_recentAnswer = new sbyte[Recent];
        static readonly int[] s_recentExamined = new int[Recent];
        static readonly int[] s_recentRead = new int[Recent];
        static readonly float[] s_recentMs = new float[Recent];
        static int s_recentAt, s_recentCount;
        // This query's entries looked through and transforms read.
        static int s_callExamined, s_callRead;

        public static string LabStats() =>
            $"sector index: {State()}, " +
            $"{s_byZdo.Count} objects ({s_movingCount} moving, {s_unfiled.Count} new, {s_suspects.Count} suspect) in {s_cells.Count} zones, " +
            $"oldest look {LabOldestLookNow:0.0} s, oldest mover filing {LabOldestMoverNow:0.0} s; queries {LabQueries} " +
            $"(occupied {LabOccupied}, empty {LabEmpty}, by the game {LabFallbacks}, empty left to the game {LabStale}, " +
            $"movers looked for everywhere {LabWide}), looked through per query {LabExaminedMean:0.0} mean, {LabExaminedMax} max, " +
            $"transforms read {LabRead} ({LabReadMax} max in one), widest mover rings {LabRingsMax}, oldest look seen " +
            $"{LabStillAgeMax:0.00} s, oldest mover filing seen {LabMoverAgeMax:0.00} s; empty answers: looked through " +
            $"{LabEmptyExaminedMean:0.0} mean, {LabEmptyExaminedMax} max, read {LabEmptyReadMean:0.0} mean, {LabEmptyReadMax} max, " +
            $"{LabEmptyMsMean:0.000} ms mean, {LabEmptyMsMax:0.000} ms max; last {s_recentCount} calls: " +
            $"{LabRecentEmpty} empty, {LabRecentOccupied} occupied, {LabRecentToGame} to the game, looked through " +
            $"{LabRecentExaminedMean:0.0} mean, {LabRecentExaminedMax} max, read {LabRecentReadMean:0.0} mean, " +
            $"{LabRecentReadMax} max, {LabRecentMsMean:0.000} ms mean; of them empty: looked through " +
            $"{LabRecentEmptyExaminedMean:0.0} mean, {LabRecentEmptyExaminedMax} max, read {LabRecentEmptyReadMean:0.0} mean, " +
            $"{LabRecentEmptyReadMax} max, {LabRecentEmptyMsMean:0.000} ms mean, {LabRecentEmptyMsMax:0.000} ms max; " +
            $"slowest {LabSlowestMs:0.000} ms, still objects found moved {LabMoved}, overdue looks {LabOverdue}, " +
            $"rebuilds {LabResyncs}, mismatches {LabTrips} (settled {LabSettled}); instance events " +
            $"{(s_live ? "live" : "not live")}, epoch {s_epoch}, added {LabInstanceAdded}, removed {LabInstanceRemoved}, " +
            $"handler errors {LabHandlerErrors}" +
            (s_verify.On ? $", verify: false empty {LabFalseEmpty}, false occupied {LabFalseOccupied}" : "");

        static string State()
        {
            if (!s_installed)
                return "not installed";
            if (!s_feature.On)
                return "off";
            if (s_gaveUp)
                return "gave up";
            if (s_resyncing)
                return "rebuilding";
            return s_trusted ? "ready" : "idle";
        }

        static void Note(sbyte answer, double ms)
        {
            s_recentAnswer[s_recentAt] = answer;
            s_recentExamined[s_recentAt] = s_callExamined;
            s_recentRead[s_recentAt] = s_callRead;
            s_recentMs[s_recentAt] = (float)ms;
            s_recentAt = s_recentAt + 1 < Recent ? s_recentAt + 1 : 0;
            if (s_recentCount < Recent)
                s_recentCount++;
            if (answer == -2)
                return;
            LabExamined += s_callExamined;
            if (s_callExamined > LabExaminedMax)
                LabExaminedMax = s_callExamined;
            if (s_callRead > LabReadMax)
                LabReadMax = s_callRead;
            if (answer != 0)
                return;
            LabEmptyExamined += s_callExamined;
            LabEmptyRead += s_callRead;
            LabEmptyMs += ms;
            if (s_callExamined > LabEmptyExaminedMax)
                LabEmptyExaminedMax = s_callExamined;
            if (s_callRead > LabEmptyReadMax)
                LabEmptyReadMax = s_callRead;
            if (ms > LabEmptyMsMax)
                LabEmptyMsMax = ms;
        }

        static int RecentCount(sbyte answer)
        {
            int n = 0;
            for (int i = 0; i < s_recentCount; i++)
                if (s_recentAnswer[i] == answer)
                    n++;
            return n;
        }

        // Is recent call i counted: every call the index looked at, or its "empty" answers only.
        static bool Counted(int i, bool emptyOnly) => emptyOnly ? s_recentAnswer[i] == 0 : s_recentAnswer[i] != -2;

        static double RecentMean(int[] values, bool emptyOnly)
        {
            long sum = 0;
            int n = 0;
            for (int i = 0; i < s_recentCount; i++)
                if (Counted(i, emptyOnly))
                {
                    sum += values[i];
                    n++;
                }
            return n > 0 ? (double)sum / n : 0.0;
        }

        static int RecentMax(int[] values, bool emptyOnly)
        {
            int max = 0;
            for (int i = 0; i < s_recentCount; i++)
                if (Counted(i, emptyOnly) && values[i] > max)
                    max = values[i];
            return max;
        }

        static double RecentMsMean(bool emptyOnly)
        {
            double sum = 0.0;
            int n = 0;
            for (int i = 0; i < s_recentCount; i++)
                if (Counted(i, emptyOnly))
                {
                    sum += s_recentMs[i];
                    n++;
                }
            return n > 0 ? sum / n : 0.0;
        }

        static double RecentMsMax(bool emptyOnly)
        {
            double max = 0.0;
            for (int i = 0; i < s_recentCount; i++)
                if (Counted(i, emptyOnly) && s_recentMs[i] > max)
                    max = s_recentMs[i];
            return max;
        }

        // The mover clock: between any two of its readings it advances at least as much as game time and as real time
        // did, since every reading adds the larger of the two since the one before. Mover ages are taken on it: bodies
        // move by game time, what the network places (remote objects snapping to their ZDO) by real time, a frame's
        // Time.deltaTime is capped at Time.maximumDeltaTime (a 2 s hitch adds 0.33 s of game time, while a remote
        // object jumps 2 s worth), and Time.timeScale may be anything.
        static double PeekClock()
        {
            double step = Math.Max(Time.timeAsDouble - s_clockGame, Time.realtimeSinceStartupAsDouble - s_clockReal);
            return step > 0.0 ? s_clock + step : s_clock;
        }

        static double Clock()
        {
            double now = PeekClock();
            s_clock = now;
            s_clockGame = Time.timeAsDouble;
            s_clockReal = Time.realtimeSinceStartupAsDouble;
            return now;
        }

        // Game time since the oldest look at a listed object (the front of the audit queue, forgotten entries let go
        // first).
        static float OldestLook()
        {
            while (s_audit.Count > 0 && !s_audit.Peek().Listed)
                s_audit.Dequeue();
            return s_audit.Count > 0 ? Time.time - s_audit.Peek().Stamp : 0f;
        }

        // Mover-clock time since the oldest filing of a moving object, at most: the front of the moving queue (entries
        // that left the moving list let go first). Every filed moving object is in the queue, and was filed, and its
        // position read, when it went to the back of it or later.
        static float OldestMoverLook()
        {
            while (s_movers.Count > 0 && !FiledMoving(s_movers.Peek()))
                s_movers.Dequeue().MQueued = false;
            return s_movers.Count > 0 ? (float)(s_now - s_movers.Peek().MStamp) : 0f;
        }

        // For other engine modules that add to or remove from ZNetScene.m_instances themselves: call this afterwards
        // with each key touched (any order, repeats are harmless).
        public static void NotifyChanged(ZDO zdo)
        {
            if (!s_installed || !s_feature.On || zdo == null || s_instances == null || s_gaveUp)
                return;
            try
            {
                Reconcile(zdo);
            }
            catch (Exception e)
            {
                Fail(e);
            }
            Flush();
        }

        // For code that moves a loaded networked object in a jump (a teleport, a portal crossing, a snap to a far
        // position) or changes its m_distant: call this afterwards. Until the next frame's upkeep files it again, every
        // answer looks at it wherever it is, as at a new object, instead of only within its old filing's reach.
        public static void NotifyMoved(ZNetView view)
        {
            if (!s_installed || !s_feature.On || view is null || s_instances == null || s_gaveUp)
                return;
            try
            {
                if (!s_byView.TryGetValue(view, out Entry e) || !e.Listed || e.Dead)
                    return;
                if (e.UnfiledSlot >= 0)
                {
                    e.AddedFrame = Time.frameCount;
                    return;
                }
                if (!view)
                {
                    MarkDead(e);
                    return;
                }
                Unfile(e);
                e.Distant = view.m_distant;
                if (e.Distant)
                    return;
                if (!e.Mover)
                    e.Since = Time.time;
                AddUnfiled(e);
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        static int Key(Vector2s zone) => Key(zone.x, zone.y);

        static int Key(int x, int y) => (x << 16) | (ushort)y;

        static float CentreX(int key) => (key >> 16) * ZoneSize;

        static float CentreZ(int key) => (short)key * ZoneSize;

        // The quarter of zone key that a position in it falls in (a position on the centre line goes to the lower one).
        static int QuadOf(float x, float z, int key) => (x > CentreX(key) ? 1 : 0) | (z > CentreZ(key) ? 2 : 0);

        // Frees everything; the next hook, frame or query binds to the scene again and rebuilds from its table.
        static void Drop()
        {
            ClearIndex(free: true);
            s_scene = null;
            s_instances = null;
            s_gaveUp = false;
            s_trips = 0;
            UpdateLive();
        }

        // Empties the mirror. free: also gives the memory back (when it is dropped or given up on, not when it is about
        // to be rebuilt to the same size).
        static void ClearIndex(bool free = false)
        {
            if (free)
            {
                s_byZdo = new Dictionary<ZDO, Entry>(ByRef<ZDO>.Instance);
                s_byView = new Dictionary<ZNetView, Entry>(ByRef<ZNetView>.Instance);
                s_cells = new Dictionary<int, Cell>();
                s_cellList = new List<Cell>();
                s_cellPool = new List<Cell>();
                s_unfiled = new List<Entry>();
                s_movers = new Queue<Entry>();
                s_audit = new Queue<Entry>();
                s_suspects = new List<Entry>();
                s_snapshot = new List<ZDO>();
            }
            else
            {
                s_byZdo.Clear();
                s_byView.Clear();
                s_cells.Clear();
                s_cellList.Clear();
                s_unfiled.Clear();
                s_movers.Clear();
                s_audit.Clear();
                s_suspects.Clear();
                s_snapshot.Clear();
            }
            s_movingCount = 0;
            s_trusted = false;
            s_resyncing = false;
            s_resyncFiling = false;
            s_snapshotAt = 0;
            // Forgotten without events: the instance events start a new epoch once a rebuild pass is done.
            s_complete = false;
            UpdateLive();
        }

        // False when the index gave up on this scene (the game answers everything then).
        static bool Bind(ZNetScene scene)
        {
            if (!ReferenceEquals(scene, s_scene))
            {
                Drop();
                s_scene = scene;
                s_instances = scene != null ? s_instancesOf(scene) : null;
                if (s_instances == null)
                    s_gaveUp = true;
                else
                    StartResync();
            }
            return !s_gaveUp;
        }

        static bool Ready(ZNetScene scene)
        {
            if (!Bind(scene) || s_resyncing || !s_trusted)
                return false;
            Dictionary<ZDO, ZNetView> table = s_instancesOf(scene);
            if (!ReferenceEquals(s_instances, table))
            {
                // Something replaced the game's table (nothing does today): start over on the new one.
                ClearIndex();
                s_instances = table;
                if (table == null)
                    s_gaveUp = true;
                else
                    Trip("the game's table was replaced");
                return false;
            }
            if (s_byZdo.Count != s_instances.Count && !Settle())
            {
                // Something changed the table without passing through the hooks: answer with the game's walk and
                // bring the mirror back in line.
                Trip($"count {s_byZdo.Count} listed, {s_instances.Count} in the game's table");
                return false;
            }
            return true;
        }

        static void Trip(string why)
        {
            LabTrips++;
            float now = Time.realtimeSinceStartup;
            s_trips = now - s_tripAt < 30f ? s_trips + 1 : 1;
            s_tripAt = now;
            if (s_trips > 3)
            {
                // Something keeps changing the table behind the hooks: the game's walk from now on, for this scene.
                EnginePlugin.Log?.LogError($"SectorInstanceIndex: mirror keeps drifting from ZNetScene.m_instances ({why}); " +
                    "zone unloading uses the game's own walk for the rest of this session");
                ClearIndex(free: true);
                s_gaveUp = true;
                return;
            }
            EnginePlugin.Log?.LogWarning($"SectorInstanceIndex: mirror out of line ({why}), rebuilding");
            StartResync();
        }

        // The safety net under every hook: the game answers until the mirror is rebuilt, from an emptied mirror since
        // the error may have left it inconsistent. It must not throw itself (it runs in the hooks' catch blocks, and
        // what escapes there escapes into the game: ZDOMan.HandleDestroyedZDO, ZoneSystem.Update, ZNetView.Awake), so
        // a rebuild that fails again gives up on the index for this scene.
        static void Fail(Exception e)
        {
            try
            {
                EnginePlugin.Log?.LogError("SectorInstanceIndex: " + e);
                if (s_instances == null || s_gaveUp)
                    return;
                ClearIndex();
                Trip("error");
            }
            catch (Exception again)
            {
                ClearIndex(free: true);
                s_gaveUp = true;
                EnginePlugin.Log?.LogError("SectorInstanceIndex: rebuilding after an error failed too; zone unloading uses " +
                    "the game's own walk for the rest of this session: " + again);
            }
        }

        // Every key of the game's table and every key the mirror lists is reconciled: added where the table has it and
        // the mirror does not, dropped where it is the other way round. Hooks keep running meanwhile, so the result is
        // exact once the pass is done.
        static void StartResync()
        {
            LabResyncs++;
            // What it files is stamped with this (a hook may start it before any answer or frame read the clock).
            s_now = Clock();
            s_trusted = false;
            s_resyncing = true;
            s_snapshot.Clear();
            s_snapshot.AddRange(s_instances.Keys);
            s_snapshot.AddRange(s_byZdo.Keys);
            s_snapshotAt = 0;
            if (s_snapshot.Count <= ResyncAtOnce)
                ResyncStep(int.MaxValue);
        }

        static void ResyncStep(int budget)
        {
            // What the pass adds was already standing in the scene: filed as it stands, without probation (see Add).
            s_resyncFiling = true;
            try
            {
                while (budget > 0 && s_snapshotAt < s_snapshot.Count)
                {
                    Reconcile(s_snapshot[s_snapshotAt++]);
                    budget--;
                }
            }
            finally
            {
                s_resyncFiling = false;
            }
            if (s_snapshotAt < s_snapshot.Count)
                return;
            s_snapshot.Clear();
            if (s_snapshot.Capacity > 4096)
                s_snapshot.Capacity = 4096;
            s_resyncing = false;
            // Every key was reconciled: from here on what the hooks and later passes change is every change.
            s_complete = true;
            if (s_byZdo.Count == s_instances.Count || Settle())
                s_trusted = true;
            else
                Trip($"after rebuild {s_byZdo.Count} listed, {s_instances.Count} in the game's table");
            UpdateLive();
        }

        // Makes the mirror's entry for this key match the game's table.
        static void Reconcile(ZDO zdo)
        {
            if (zdo == null)
                return;
            bool inGame = s_instances.TryGetValue(zdo, out ZNetView view);
            if (s_byZdo.TryGetValue(zdo, out Entry e))
            {
                if (inGame && ReferenceEquals(e.View, view))
                    return;
                Forget(e);
            }
            if (inGame)
                Add(zdo, view);
        }

        // Is this listed object still in the game's table under its key, with its view?
        static void Membership(Entry e)
        {
            if (!s_instances.TryGetValue(e.Zdo, out ZNetView view) || !ReferenceEquals(view, e.View))
                Reconcile(e.Zdo);
        }

        // The mirror lists more than the game's table: usually a sweep took dead entries out of it behind the hooks.
        // Those are on the suspects list, so dropping the ones the table no longer has settles it on the spot, without a
        // rebuild and without counting toward giving up. True when the counts agree again.
        static bool Settle()
        {
            if (s_byZdo.Count <= s_instances.Count || s_suspects.Count == 0)
                return false;
            PruneSuspects(check: true);
            if (s_byZdo.Count != s_instances.Count)
                return false;
            LabSettled++;
            return true;
        }

        // Lets go of suspects that left the mirror; check: first drops the ones the game's table no longer has.
        static void PruneSuspects(bool check)
        {
            int kept = 0;
            // Membership may re-add a key under a new entry, which can land on this list too: Count is read each time,
            // and kept never passes i.
            for (int i = 0; i < s_suspects.Count; i++)
            {
                Entry e = s_suspects[i];
                if (check && e.Listed)
                    Membership(e);
                if (e.Listed)
                    s_suspects[kept++] = e;
                else
                    e.Suspect = false;
            }
            s_suspects.RemoveRange(kept, s_suspects.Count - kept);
        }

        static void MarkSuspect(Entry e)
        {
            if (e.Suspect)
                return;
            e.Suspect = true;
            s_suspects.Add(e);
        }

        // Its view is gone: the game's test skips it from now on (it stays in the mirror while the table has it).
        static void MarkDead(Entry e)
        {
            if (!e.Dead)
            {
                e.Dead = true;
                Unlist(e);
            }
            MarkSuspect(e);
        }

        static void Add(ZDO zdo, ZNetView view)
        {
            var e = new Entry { Zdo = zdo, Id = zdo.m_uid, View = view, Listed = true, Stamp = Time.time };
            s_byZdo[zdo] = e;
            s_audit.Enqueue(e);
            Queue(zdo, e.Id, view, true);
            if (view is null)
            {
                e.Dead = true;
                MarkSuspect(e);
                return;
            }
            s_byView[view] = e;
            if (!view)
            {
                e.Dead = true;
                MarkSuspect(e);
                return;
            }
            e.T = view.transform;
            e.Distant = view.m_distant;
            Vector3 p = e.T.position;
            e.X = p.x;
            e.Z = p.z;
            e.Look = s_now;
            e.Key = Key(ZoneSystem.GetZone(p));
            e.Since = Time.time;
            // What the game itself moves around (characters, items, ships, carts, logs, fish, birds, projectiles).
            e.Mover = s_bodyOf(view) != null || view.GetComponent<ZSyncTransform>() != null;
            if (e.Distant)
                return;
            if (s_resyncFiling)
            {
                // Found by a rebuild (first bind, switched on, a mismatch or an error): it was already standing in the
                // scene, so it is filed where it stands, still unless it has a body. Probation would put every object
                // of the scene on the moving lists for 2 s, and the last batch on the new-object list every answer
                // looks through.
                e.Moving = e.Mover;
                File(e);
            }
            else
                AddUnfiled(e);
        }

        static void Forget(Entry e)
        {
            if (s_byZdo.TryGetValue(e.Zdo, out Entry listed) && ReferenceEquals(listed, e))
                s_byZdo.Remove(e.Zdo);
            if (e.View is object && s_byView.TryGetValue(e.View, out Entry byView) && ReferenceEquals(byView, e))
                s_byView.Remove(e.View);
            if (e.Listed)
                Queue(e.Zdo, e.Id, e.View, false);
            Unlist(e);
            // The queues and the suspects list let go of it when they next reach it.
            e.Listed = false;
        }

        // ---- filing -------------------------------------------------------------------------------------
        // Every listed object that counts (alive, not distant) is either new (s_unfiled) or filed under the zone and
        // quarter it stood in when last looked at: Cell.Still, or Cell.Moving and the moving queue when it is moving.
        // Filing always follows a look at its position in the same answer or upkeep pass, so its Look is s_now then.

        // New (or no longer distant, or reported moved): on probation, looked at by every answer until the next
        // frame's upkeep files it.
        static void AddUnfiled(Entry e)
        {
            e.Moving = true;
            e.AddedFrame = Time.frameCount;
            e.UnfiledSlot = s_unfiled.Count;
            s_unfiled.Add(e);
        }

        static void RemoveUnfiled(Entry e)
        {
            int slot = e.UnfiledSlot;
            if (slot < 0)
                return;
            int last = s_unfiled.Count - 1;
            Entry moved = s_unfiled[last];
            s_unfiled[slot] = moved;
            moved.UnfiledSlot = slot;
            s_unfiled.RemoveAt(last);
            e.UnfiledSlot = -1;
        }

        // A zone's cell, made (from the pool when it has one) if it has none yet.
        static Cell CellOf(int key)
        {
            if (s_cells.TryGetValue(key, out Cell c))
                return c;
            int pooled = s_cellPool.Count;
            if (pooled > 0)
            {
                c = s_cellPool[pooled - 1];
                s_cellPool.RemoveAt(pooled - 1);
            }
            else
                c = new Cell();
            c.Reset(key);
            s_cells[key] = c;
            s_cellList.Add(c);
            return c;
        }

        // Under e.Key and its quarter, as it stands now (its position was just read).
        static void File(Entry e)
        {
            Cell c = CellOf(e.Key);
            int q = QuadOf(e.X, e.Z, e.Key);
            List<Entry> b = e.Moving ? c.Moving[q] : c.Still[q];
            e.Cell = c;
            e.Quad = q;
            e.Bucket = b;
            e.Slot = b.Count;
            b.Add(e);
            c.Empty = 0;
            if (!e.Moving)
                return;
            s_movingCount++;
            if (!e.MQueued)
            {
                e.MQueued = true;
                e.MStamp = s_now;
                s_movers.Enqueue(e);
            }
        }

        static void Unfile(Entry e)
        {
            List<Entry> b = e.Bucket;
            if (b == null)
                return;
            if (ReferenceEquals(b, e.Cell.Moving[e.Quad]))
                s_movingCount--;
            int last = b.Count - 1;
            Entry moved = b[last];
            b[e.Slot] = moved;
            moved.Slot = e.Slot;
            b.RemoveAt(last);
            e.Cell = null;
            e.Bucket = null;
            e.Slot = -1;
        }

        // Out of every list but the audit queue (dead, distant or forgotten).
        static void Unlist(Entry e)
        {
            Unfile(e);
            RemoveUnfiled(e);
        }

        // Right after a look at its position.
        static void SetMoving(Entry e, bool moving)
        {
            if (e.Moving == moving)
                return;
            bool filed = e.Bucket != null;
            Unfile(e);
            e.Moving = moving;
            if (filed)
                File(e);
        }

        static bool FiledMoving(Entry e) => e.Listed && e.Moving && e.Bucket != null;

        // The game's test for one object: false when it does not count (destroyed, or m_distant), otherwise the zone it
        // stands in now. The entry is updated on the way: filed under that zone and quarter, a still object that moved
        // goes back on the moving list, a destroyed or distant one leaves the lists (it stays in the mirror while the
        // table has it).
        static bool Where(Entry e, out int key)
        {
            key = 0;
            ZNetView view = e.View;
            if (!view)
            {
                MarkDead(e);
                return false;
            }
            if (view.m_distant)
            {
                if (!e.Distant)
                {
                    e.Distant = true;
                    Unlist(e);
                }
                return false;
            }
            Vector3 p = e.T.position;
            e.Look = s_now;
            key = Key(ZoneSystem.GetZone(p));
            if (e.Distant)
            {
                // Became non-distant (nothing in the game does that; a mod could): a new object from here.
                e.Distant = false;
                e.X = p.x;
                e.Z = p.z;
                e.Key = key;
                e.Since = Time.time;
                AddUnfiled(e);
                return true;
            }
            if (e.UnfiledSlot >= 0)
            {
                // New: the upkeep files it.
                e.X = p.x;
                e.Z = p.z;
                e.Key = key;
                return true;
            }
            if (p.x != e.X || p.z != e.Z)
            {
                e.X = p.x;
                e.Z = p.z;
                bool starts = false;
                if (!e.Mover)
                {
                    e.Since = Time.time;
                    if (!e.Moving)
                    {
                        LabMoved++;
                        starts = true;
                    }
                }
                if (key != e.Key || starts || QuadOf(e.X, e.Z, key) != e.Quad)
                {
                    Unfile(e);
                    e.Key = key;
                    if (starts)
                        e.Moving = true;
                    File(e);
                }
            }
            return true;
        }

        // ---- answering ----------------------------------------------------------------------------------

        // 1 occupied, 0 empty, -1 the index cannot vouch for "empty" right now (the game's walk answers).
        static sbyte Query(Vector2s sector)
        {
            LabQueries++;
            s_now = Clock();
            int key = Key(sector);
            int sx = sector.x, sy = sector.y;
            float cx = sx * ZoneSize, cz = sy * ZoneSize;

            // What is filed in the zone: usually the first one is still there.
            if (s_cells.TryGetValue(key, out Cell here) && TestCell(here, key))
                return Occupied();

            // New objects (and ones reported moved), wherever they are.
            if (TestAll(s_unfiled, key))
                return Occupied();

            // Still objects filed just outside the zone's edge (in case one was nudged across since it was last looked
            // at): the band covers what one drifts between two looks, wider when the oldest look is older; with a look
            // too old for the 8 zones around to cover, the game's walk decides instead.
            float stillAge = OldestLook();
            if (stillAge > LabStillAgeMax)
                LabStillAgeMax = stillAge;
            if (stillAge >= 2f * AuditPeriod)
            {
                LabStale++;
                return -1;
            }
            float stillReach = HalfZone + Margin * Math.Max(1f, stillAge / AuditPeriod);
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                    if ((dx | dy) != 0 && s_cells.TryGetValue(Key(sx + dx, sy + dy), out Cell near) &&
                        TestStill(near, key, cx, cz, stillReach))
                        return Occupied();

            // Moving objects filed as far around as one could have come since its position was read: each within its
            // own reach, the zones looked through as far as the oldest filing's.
            float moverAge = OldestMoverLook();
            if (moverAge > LabMoverAgeMax)
                LabMoverAgeMax = moverAge;
            float travel = Margin + MaxMoverSpeed * moverAge;
            float moverReach = HalfZone + travel;
            if (travel < MaxRings * ZoneSize)
            {
                // Ring r's nearest edge is (r - 0.5) zones from the centre: rings up to 1 + travel / ZoneSize reach.
                int rings = 1 + (int)(travel / ZoneSize);
                if (rings > LabRingsMax)
                    LabRingsMax = rings;
                for (int dy = -rings; dy <= rings; dy++)
                    for (int dx = -rings; dx <= rings; dx++)
                        if ((dx | dy) != 0 && s_cells.TryGetValue(Key(sx + dx, sy + dy), out Cell near) &&
                            TestMoving(near, key, cx, cz, moverReach))
                            return Occupied();
            }
            else
            {
                // The moving upkeep is far behind: every zone's moving objects (the list may grow meanwhile, never
                // shrinks).
                LabWide++;
                for (int i = 0; i < s_cellList.Count; i++)
                {
                    Cell c = s_cellList[i];
                    if (c.Key != key && TestMoving(c, key, cx, cz, moverReach))
                        return Occupied();
                }
            }

            LabEmpty++;
            return 0;
        }

        // Everything filed in the zone.
        static bool TestCell(Cell c, int key)
        {
            for (int q = 0; q < 4; q++)
                if (TestAll(c.Still[q], key))
                    return true;
            for (int q = 0; q < 4; q++)
                if (TestAll(c.Moving[q], key))
                    return true;
            return false;
        }

        // A neighbour's still objects less than reach from the asked zone's centre, in the quarters that can hold one.
        static bool TestStill(Cell c, int key, float cx, float cz, float reach)
        {
            for (int q = 0; q < 4; q++)
                if (c.Still[q].Count > 0 && Faces(c, q, cx, cz, reach) && TestNear(c.Still[q], key, cx, cz, reach))
                    return true;
            return false;
        }

        // A zone's moving objects that could have come into the asked zone, in the quarters that can hold one within
        // reach (the reach of the oldest filing; each object is then held to its own).
        static bool TestMoving(Cell c, int key, float cx, float cz, float reach)
        {
            for (int q = 0; q < 4; q++)
                if (c.Moving[q].Count > 0 && Faces(c, q, cx, cz, reach) && TestNearMoving(c.Moving[q], key, cx, cz))
                    return true;
            return false;
        }

        // Can an object filed in this quarter of the cell stand less than reach from (cx, cz) on both axes? A quarter
        // spans its half of the zone on each axis: up to the centre line (included) for the lower one, past it for the
        // upper one, and Slack past the zone's outer edge.
        static bool Faces(Cell c, int q, float cx, float cz, float reach)
        {
            float lo, hi;
            if ((q & 1) == 0)
            {
                lo = c.CX - HalfZone - Slack;
                hi = c.CX;
            }
            else
            {
                lo = c.CX;
                hi = c.CX + HalfZone + Slack;
            }
            if (!(lo < cx + reach && hi > cx - reach))
                return false;
            if ((q & 2) == 0)
            {
                lo = c.CZ - HalfZone - Slack;
                hi = c.CZ;
            }
            else
            {
                lo = c.CZ;
                hi = c.CZ + HalfZone + Slack;
            }
            return lo < cz + reach && hi > cz - reach;
        }

        // Every entry of the list: true as soon as one stands in the zone now.
        static bool TestAll(List<Entry> list, int key)
        {
            for (int i = 0; i < list.Count;)
            {
                Entry e = list[i];
                s_callExamined++;
                s_callRead++;
                LabRead++;
                if (Where(e, out int now) && now == key)
                    return true;
                // It left the list (filed elsewhere or gone), so another one now sits at i.
                if (i < list.Count && ReferenceEquals(list[i], e))
                    i++;
            }
            return false;
        }

        // The still entries filed less than reach from the zone's centre (on both axes): true as soon as one stands in
        // it now.
        static bool TestNear(List<Entry> list, int key, float cx, float cz, float reach)
        {
            for (int i = 0; i < list.Count;)
            {
                Entry e = list[i];
                s_callExamined++;
                if (Math.Abs(e.X - cx) < reach && Math.Abs(e.Z - cz) < reach)
                {
                    s_callRead++;
                    LabRead++;
                    if (Where(e, out int now) && now == key)
                        return true;
                }
                if (i < list.Count && ReferenceEquals(list[i], e))
                    i++;
            }
            return false;
        }

        // The moving entries that could have come into the zone since their position was read (each within
        // HalfZone + Margin + MaxMoverSpeed times the age of that read, on both axes): true as soon as one stands in it
        // now.
        static bool TestNearMoving(List<Entry> list, int key, float cx, float cz)
        {
            for (int i = 0; i < list.Count;)
            {
                Entry e = list[i];
                s_callExamined++;
                float reach = HalfZone + Margin + MaxMoverSpeed * (float)(s_now - e.Look);
                if (Math.Abs(e.X - cx) < reach && Math.Abs(e.Z - cz) < reach)
                {
                    s_callRead++;
                    LabRead++;
                    if (Where(e, out int now) && now == key)
                        return true;
                }
                if (i < list.Count && ReferenceEquals(list[i], e))
                    i++;
            }
            return false;
        }

        static sbyte Occupied()
        {
            LabOccupied++;
            return 1;
        }

        // ---- upkeep, once a frame -----------------------------------------------------------------------

        // New objects made in an earlier frame, looked at again (their maker is done placing them) and filed, on
        // probation. As many as were made: each cost far more to make than this look.
        static void FileNew()
        {
            int frame = Time.frameCount;
            for (int i = 0; i < s_unfiled.Count;)
            {
                Entry e = s_unfiled[i];
                if (e.AddedFrame < frame && Where(e, out _) && e.UnfiledSlot >= 0)
                {
                    RemoveUnfiled(e);
                    File(e);
                }
                // Filed or gone, so another one now sits at i.
                if (i < s_unfiled.Count && ReferenceEquals(s_unfiled[i], e))
                    i++;
            }
        }

        // Every listed object again, oldest look first: still in the game's table, and where it stands. The frame's
        // share follows the number of listed objects and the frame's game time, so a low frame rate or a burst of new
        // objects does not stretch the cycle past AuditCycle of game time (objects move by game time). The share is
        // held to ShareCap unless the queue is behind: after a slow frame what waits comes due and is looked at on top
        // as overdue (anything AuditCycle old, up to OverdueCap), spread over the next frames instead of all at once.
        // The overdue looks also keep the cycle when many objects just left (the share shrinks with them while the ones
        // left wait) or a burst comes due at once. Forgotten entries are let go for free: a far area that drained
        // leaves thousands of them, and they must not eat the share.
        static void Audit()
        {
            int live = s_byZdo.Count;
            if (live == 0)
            {
                s_audit.Clear();
                return;
            }
            int share = (int)(live * Time.deltaTime / AuditCycle) + 1;
            if (share > ShareCap && OldestLook() <= AuditCycle + Behind)
                share = ShareCap;
            int budget = Math.Min(live, Math.Max(32, share));
            int overdue = 0;
            float now = Time.time;
            while (s_audit.Count > 0)
            {
                Entry e = s_audit.Peek();
                if (!e.Listed)
                {
                    s_audit.Dequeue();
                    continue;
                }
                if (budget <= 0)
                {
                    if (now - e.Stamp < AuditCycle || overdue >= OverdueCap)
                        break;
                    overdue++;
                    LabOverdue++;
                }
                budget--;
                s_audit.Dequeue();
                // May drop it, or replace it with a new entry for the same key (queued at the back).
                Membership(e);
                if (!e.Listed)
                    continue;
                if (!e.Dead)
                    Where(e, out _);
                e.Stamp = now;
                s_audit.Enqueue(e);
            }
        }

        // Moving objects looked at and filed again, oldest first, all within MovingPeriod of the mover clock (the
        // frame's share of them by the clock's step, held to ShareCap unless the queue is behind, plus what is overdue
        // up to OverdueCap); new ones that stood still through probation leave the moving list for their zone's still
        // list.
        static void Moving()
        {
            double now = s_now;
            double step = now - s_movingAt;
            s_movingAt = now;
            if (step < 0.0)
                step = 0.0;
            else if (step > MovingPeriod)
                step = MovingPeriod;
            int share = (int)(s_movingCount * step / MovingPeriod) + 1;
            if (share > ShareCap && OldestMoverLook() <= MovingPeriod + Behind)
                share = ShareCap;
            int budget = Math.Min(s_movingCount, Math.Max(16, share));
            int overdue = 0;
            float gameNow = Time.time;
            while (s_movers.Count > 0)
            {
                Entry e = s_movers.Peek();
                if (!FiledMoving(e))
                {
                    s_movers.Dequeue().MQueued = false;
                    continue;
                }
                if (budget <= 0)
                {
                    if (now - e.MStamp < MovingPeriod || overdue >= OverdueCap)
                        break;
                    overdue++;
                    LabOverdue++;
                }
                budget--;
                s_movers.Dequeue();
                e.MQueued = false;
                // Filing it elsewhere queues it again.
                if (!Where(e, out _) || !FiledMoving(e))
                    continue;
                if (!e.Mover && gameNow - e.Since >= Probation)
                {
                    // Did not move since: still from now on (the audit keeps an eye on it).
                    SetMoving(e, false);
                    continue;
                }
                if (!e.MQueued)
                {
                    e.MQueued = true;
                    e.MStamp = s_now;
                    s_movers.Enqueue(e);
                }
            }
        }

        // Every 10 s (never while answering, which holds on to the zone lists): zones found empty twice in a row go to
        // the pool (a zone objects keep coming back to stays), lists that lost most of their objects (a far area
        // draining) give the room back, and suspects that left the mirror are let go.
        static void Tidy()
        {
            if (Time.realtimeSinceStartup < s_tidyAt)
                return;
            s_tidyAt = Time.realtimeSinceStartup + 10f;
            int kept = 0;
            for (int i = 0; i < s_cellList.Count; i++)
            {
                Cell c = s_cellList[i];
                if (c.IsEmpty())
                {
                    if (++c.Empty >= 2)
                    {
                        s_cells.Remove(c.Key);
                        Pool(c);
                        continue;
                    }
                }
                else
                    c.Empty = 0;
                for (int q = 0; q < 4; q++)
                {
                    Trim(c.Still[q]);
                    Trim(c.Moving[q]);
                }
                s_cellList[kept++] = c;
            }
            s_cellList.RemoveRange(kept, s_cellList.Count - kept);
            Trim(s_unfiled);
            PruneSuspects(check: false);
        }

        static void Pool(Cell c)
        {
            if (s_cellPool.Count >= CellPool)
                return;
            for (int q = 0; q < 4; q++)
            {
                if (c.Still[q].Capacity > MinCapacity)
                    c.Still[q].Capacity = MinCapacity;
                if (c.Moving[q].Capacity > MinCapacity)
                    c.Moving[q].Capacity = MinCapacity;
            }
            s_cellPool.Add(c);
        }

        static void Trim(List<Entry> b)
        {
            if (b.Capacity > 64 && b.Count < b.Capacity / 4)
                b.Capacity = Math.Max(b.Count * 2, MinCapacity);
        }

        // ---- patches ------------------------------------------------------------------------------------

        // __state: -1 the index did not answer, 0 / 1 its answer in verify mode (the game answers then).
        static bool HavePrefix(ZNetScene __instance, Vector2s sector, ref bool __result, bool __runOriginal, out sbyte __state)
        {
            __state = -1;
            if (!s_installed || !s_feature.On || !__runOriginal)
                return true;
            try
            {
                s_callExamined = s_callRead = 0;
                if (!Ready(__instance))
                {
                    LabFallbacks++;
                    Note(-2, 0.0);
                    return true;
                }
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                sbyte have = Query(sector);
                double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (ms > LabSlowestMs)
                    LabSlowestMs = ms;
                Note(have, ms);
                if (have < 0)
                    return true;
                if (s_verify.On)
                {
                    __state = have;
                    return true;
                }
                __result = have == 1;
                return false;
            }
            catch (Exception e)
            {
                Fail(e);
                return true;
            }
            finally
            {
                // Ready may have settled suspects (keys taken out behind the hooks).
                Flush();
            }
        }

        static void HavePostfix(Vector2s sector, bool __result, sbyte __state)
        {
            if (__state < 0 || (__state == 1) == __result)
                return;
            if (__result)
                LabFalseEmpty++;
            else
                LabFalseOccupied++;
            if (LabFalseEmpty + LabFalseOccupied <= 20)
                EnginePlugin.Log?.LogWarning($"SectorInstanceIndex verify: zone {sector.x},{sector.y} the game says " +
                    $"{(__result ? "occupied" : "empty")}, the index says {(__result ? "empty" : "occupied")}. {LabStats()}");
        }

        // The table hooks below are finalizers rather than postfixes: a postfix does not run when the method, or another
        // mod's postfix ahead of it, throws, and the part done before the throw would be missed. They never change the
        // exception (void, so HarmonyX rethrows it as it was), and Reconcile is idempotent, so running after a partial or
        // skipped original is safe.

        static void AddFinalizer(ZNetScene __instance, ZDO zdo)
        {
            if (!s_installed || !s_feature.On)
                return;
            try
            {
                if (Bind(__instance))
                    Reconcile(zdo);
            }
            catch (Exception e)
            {
                Fail(e);
            }
            Flush();
        }

        // The key the game removes is the view's ZDO before it clears it.
        static void DestroyPrefix(GameObject go, out ZDO __state)
        {
            __state = null;
            if (!s_installed || !s_feature.On || s_instances == null)
                return;
            try
            {
                if (!go)
                    return;
                ZNetView view = go.GetComponent<ZNetView>();
                if (view)
                    __state = view.GetZDO();
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        static void DestroyFinalizer(ZNetScene __instance, ZDO __state)
        {
            if (__state == null || !s_installed || !s_feature.On)
                return;
            try
            {
                if (Bind(__instance))
                    Reconcile(__state);
            }
            catch (Exception e)
            {
                Fail(e);
            }
            Flush();
        }

        static void ZdoDestroyedFinalizer(ZNetScene __instance, ZDO zdo, Exception __exception)
        {
            if (!s_installed || !s_feature.On)
                return;
            try
            {
                if (!Bind(__instance))
                    return;
                Reconcile(zdo);
                // Thrown after ResetZDO (Object.Destroy of an already destroyed view): the entry stays in the table
                // without its ZDO, for a sweep to take out later.
                if (__exception != null && zdo != null && s_byZdo.TryGetValue(zdo, out Entry e) && (!e.View || e.View.GetZDO() == null))
                    MarkSuspect(e);
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
            // Inside ZDOMan.HandleDestroyedZDO, before ZDOPool takes the ZDO back: a handler still sees it live.
            Flush();
        }

        // RemoveObjects leaves the views it removed in m_tempRemoved. Another prefix may skip it (Real Portal does on 5
        // ticks of 6), leaving last time's list there: the table's count says whether it removed anything this time.
        static void RemoveObjectsPrefix(out int __state)
        {
            __state = s_instances != null ? s_instances.Count : -1;
        }

        static void RemoveObjectsFinalizer(ZNetScene __instance, int __state, Exception __exception)
        {
            if (!s_installed || !s_feature.On)
                return;
            try
            {
                if (!Bind(__instance) || (__state == s_instances.Count && __exception == null))
                    return;
                List<ZNetView> removed = s_removedOf(__instance);
                for (int i = 0; i < removed.Count; i++)
                {
                    ZNetView view = removed[i];
                    if (view is null || !s_byView.TryGetValue(view, out Entry e))
                        continue;
                    Reconcile(e.Zdo);
                    // Thrown part way, at a view destroyed behind the game's back (Object.Destroy(view.gameObject) on
                    // it throws after ResetZDO): the views before it are out of the table (reconciled above), that one
                    // stays without its ZDO, for a sweep to take out later.
                    if (__exception != null && e.Listed && (!view || view.GetZDO() == null))
                        MarkSuspect(e);
                }
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
            Flush();
        }

        // A view destroyed while the mirror still lists it: something called Object.Destroy on a networked object
        // instead of ZNetScene.Destroy, so the game keeps it in its table, and a sweep may take it out later. Only
        // noted here: the game's test still counts the view until it is gone, and Where drops it from the lists then.
        static void ViewDestroyPrefix(ZNetView __instance)
        {
            if (!s_installed || !s_feature.On || s_instances == null || s_byView.Count == 0)
                return;
            try
            {
                if (s_byView.TryGetValue(__instance, out Entry e) && e.Listed)
                    MarkSuspect(e);
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        static void ShutdownPostfix(ZNetScene __instance)
        {
            if (ReferenceEquals(__instance, s_scene))
                Drop();
        }

        static void OnDestroyPostfix(ZNetScene __instance)
        {
            if (ReferenceEquals(__instance, s_scene))
                Drop();
        }

        static void UpdatePostfix(ZNetScene __instance)
        {
            if (!s_installed || !s_feature.On)
                return;
            try
            {
                s_now = Clock();
                if (!Bind(__instance))
                    return;
                // Also while rebuilding, which makes new entries by the thousand: filed and kept fresh as they come, so
                // the first answers after it need not look through them all.
                FileNew();
                if (s_resyncing)
                {
                    ResyncStep(ResyncPerFrame);
                    // The entries kept from before a rebuild keep their looks fresh too, so a long rebuild does not end
                    // with an oldest look too old to answer from.
                    Audit();
                    Moving();
                    return;
                }
                if (!s_trusted)
                    return;
                Audit();
                Moving();
                Tidy();
            }
            catch (Exception e)
            {
                Fail(e);
            }
            finally
            {
                // A rebuild step or the audit's membership checks.
                Flush();
            }
        }
    }
}
