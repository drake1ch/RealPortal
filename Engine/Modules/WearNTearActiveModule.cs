using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace RealPortal.Engine
{
    // WearNTearUpdater drives every building piece (WearNTear) from the one list of all loaded pieces:
    //  - a cycle starts with one frame that calls UpdateCover(Time.deltaTime) on every enabled piece and
    //    UpdateAshlandsMaterialValues(time) on every piece;
    //  - then UpdateWear(time) round-robin, m_updatesPerFrame (5..100) pieces a frame; after the sweep the quota is
    //    tuned so the next sweep ends just before the cycle's second is up, and the next cycle starts after that
    //    second (or at once, when the sweep took longer).
    // Far areas put their pieces into the same list. With ~100k of them the quota sits at 100 and a sweep takes ~17 s
    // at 60 fps: the player's own base gets UpdateWear (rain, support, snow, ash and lava damage; the support
    // recalculation that drops an unsupported beam) every ~17 s instead of every second, and every cycle starts with a
    // frame that walks all of them (about once a second with a few thousand far pieces, once a sweep with more).
    // For pieces outside the player's active area the game's work is next to nothing: UpdateWear's owner branch only
    // makes sure the ZDO says full support (OutsideActiveArea, see Outside below) and returns; for everyone else it
    // re-reads the snow from the ZDO (pieces with a snow mesh) and refreshes the visuals from it and from the health the
    // last RPC_HealthChanged brought; UpdateCover only refreshes the roof flags that UpdateWear reads in the active area.
    //
    // With this on the passes run over the active set only: the pieces in the sectors around the player (Chebyshev
    // TotalSimulationDistance + WindowMargin zones from the reference position's zone, i.e. everything the game itself
    // loads and a margin; the game's active area for pieces reaches 2 zones at most) plus the sectors registered
    // through WearNTearActive.AddSectors (RealPortal's awake far cores). For that set it is the game's updater step for
    // step: the same cycle and sleep, the same frames (the cycle's first one does no wear, the sweep starts on the
    // next), the same quota and the same tuning of it, the same calls with the same arguments. The one change is that
    // the cycle's cover pass is not done in one frame any more: each piece gets its UpdateCover and
    // UpdateAshlandsMaterialValues right before its UpdateWear, in the same visit, so every piece still gets one of
    // each per cycle (~1 s), in the game's order (cover, material, wear), and the cycle's first frame only sets the
    // global ash texture. UpdateCover gets exactly the dt the game would give it: the deltaTime of the cycle's first
    // frame, kept on a clock that advances once per cycle. A piece that was outside the set for a while gets
    // everything it missed in one call when it is back, which is what the game would have added to its roof-check
    // timer bit by bit (the check itself then runs at once instead of at some point in between).
    //
    // Pieces outside the active set keep their visuals as they are: no snow refresh from the ZDO (health changes still
    // arrive through RPC_HealthChanged, which the game sends to everybody) and no UpdateCover (caught up as above).
    // The snow is more than a visual, though: UpdateWear's owner branch adds to the local m_snowBuildup and writes it to
    // the ZDO, and while another client owns a piece only that client moves it (RPC_SetSnow goes to the owner alone),
    // the game's sweep here re-reading it every pass. So a catch-up visit (the first one back) re-reads it from the ZDO
    // before anything else, as the game's last pass would have (pieces with a snow mesh, as there): should this client
    // have taken the piece over meanwhile (a far side's claim, or ZDOMan handing it over on arrival), its owner branch
    // goes on from the ZDO's value instead of the one Awake read. While this client owned the piece all along the two
    // are equal and the read changes nothing.
    // What the game would write for them is kept (Outside): an enabled owned piece whose m_support is not its max
    // support and whose ZDO does not say so yet gets the game's own UpdateWear, which, outside the active area, writes
    // exactly that and returns; so another client, or this one when the piece is back in range, reads what it reads in
    // the game. While other mods patch UpdateWear (Path of Valheiman's prefix writes three ZDO fields of the owner's own
    // pieces), every enabled owned piece also gets the game's UpdateWear once per OutsideWearCycle, so what those
    // patches write is written as well (a disabled one never: the game's updater does not call UpdateWear on it, and
    // ItemDrop keeps the WearNTear of a loose item disabled until it becomes a piece). They also keep
    // UpdateAshlandsMaterialValues, so a burn or ash glow still fades out (the one visual kept: a transient, and next
    // to free). All of it runs from the audit, which looks at every piece once per AuditCycle instead of once per
    // cycle; the game, with far areas loaded, gets to them once per ~17 s sweep. (Pieces this far out are loaded only
    // because a mod keeps them: the game alone never runs UpdateWear on them at all.)
    //
    // Bookkeeping: every piece is filed under the sector of its ZDO position (finalizer on WearNTear.Awake, prefix on
    // OnDestroy) and followed when a visit or the audit sees it moved (ships, carts). The active list is the union of
    // the active sectors' files, updated in O(sectors) when the player crosses a zone border, the simulation distance
    // changes or a sector is registered. The audit walks the game's own list, so a piece the hooks missed is filed
    // within a pass (and at once when the two lists differ in size). The files exist only while this is on; switching
    // on rebuilds them from the game's list on the next frame.
    //
    // Differences from the game (deliberate, harmless): cover/material work is spread over the cycle instead of done
    // in its first frame (per piece the same cadence, dt and order); pieces are visited in the order of the files, not
    // of the game's list (itself arbitrary: it shifts with every swap-remove); a piece moved within the active list
    // while a sweep runs is still visited exactly once in that sweep (the game skips one now and then: what its
    // swap-remove moves into a slot already passed), and one that leaves the set and comes back within a sweep after
    // its visit goes back into the part already visited, not to the end: as in the game, nothing is visited twice in a
    // sweep, so a set that flickers at its edge (a player going back and forth over a zone border) cannot keep a sweep
    // from ending, it lasts at most as long as the pieces that were in the set during it take; a dead entry is
    // dropped, not called (the game would throw on it); an exception thrown by one piece's update is logged and
    // counted and the sweep goes on (the game's updater would throw on that piece every frame and update nothing
    // else); a second WearNTearUpdater, which the game never has, runs as in the game.
    // Pieces outside the set: as above.
    //
    // The difference in gameplay (deliberate; the reason this is off by default): with far areas loaded the game's
    // sweep over every loaded piece takes ~17 s, and this gives the active set the game's own ~1 s cycle back, the
    // cadence the game has without far areas. Several of UpdateWear's effects come per call, not per second of game
    // time, so against Off in the same session the player's own pieces get them up to ~17 times as often: snow buildup
    // (EnvMan's buildup * Time.deltaTime * m_snowBuildupSpeed per call) and its ZDOVars.s_snow write, heavy-snow damage
    // (m_snowDamage per call), persistent-event and required-biome damage; the gated ones up to their gate: ash damage
    // (5 s gate) every ~6 s instead of once a sweep (~3x), lava (2 s gate) every ~3 s (~6x), rain unchanged (60 s
    // gate); an unsupported piece falls within ~1 s instead of up to ~17 s; and the owner's s_snow and s_support
    // writes (ZDO data revisions) are as much more frequent. Without far areas the game's sweep already takes about a
    // second and nothing changes. Lab: with far areas loaded expect CycleSeconds ~1 s on against up to ~17 s off
    // (while this does not run the gauges watch the game's updater, GameGauges); more revision bumps of the player's
    // own pieces in the Deep North and the Ashlands, and more heavy-snow damage and snow per minute in a Deep North
    // base under heavy snow, are this deviation, not a regression.
    //
    // Default off (ARCHITECTURE-v2 §5.3: a flag's default is set once its stage passes): E6 is needed by S7, which has
    // not run, the module has not run in game yet, and the cadence above is a gameplay change against Off that is
    // accepted with the stage. Real Portal's Window.Quality=Continuation turns it on (preset 3, Docs\PARITY-PLAN.md P18:
    // with far areas loaded the player's own base gets the game's cadence back), and with its Far.Viewer it registers the
    // active-area sectors of every awake far core, whose pieces then wear as in an active area (P17).
    //
    // Switching: while this is on the game's updater is skipped and its own state (index, sleep times, quota) is left
    // as it was, so switching off resumes it where it stood. Switching on takes over its sleep times and quota and
    // carries on with a sweep over the active set. A mod that patches WearNTearUpdater.UpdateWearNTear (any patch) or
    // transpiles Update makes this step aside: the game's updater runs (looked at every PatchCheckSeconds). Patches on
    // WearNTear's own methods are unaffected (they are what is called; one on UpdateWear also gets the owned pieces
    // outside the set, see Outside). Real Portal's own transpiler on UpdateWear (Far.Viewer) does not count as another
    // mod's: it routes the active-area and weather tests, which answer as the game's for every piece outside an awake far
    // core's active area, and those inside one are in the set it registers. Nothing here sends anything, changes
    // ownership or writes a ZDO other than through the game's own UpdateWear.
    [EngineModule]
    public static class WearNTearActiveModule
    {
        public static readonly Feature Feature = Features.Register("WearNTearActiveSectors", false,
            "WearNTearUpdater's cover/material/wear passes run over the pieces around the player and registered sectors " +
            "(Real Portal's awake far cores) only, and the once-a-cycle cover pass is spread over the cycle instead of one " +
            "frame. With far areas loaded the player's own pieces get wear (snow, ash, lava, support) every ~1 s again " +
            "instead of every ~17 s, as in the game without them: off by default in the engine, Real Portal's " +
            "Window.Quality=Continuation turns it on. Off: the game's updater over every loaded piece.");

        // ---- Lab counters (read by reflection; cumulative ones are cleared by ResetCounters) ----
        public static int AllPieces;            // pieces filed (the game's list)
        public static int ActivePieces;         // of those, in the active set
        public static int Sectors;              // sectors holding pieces
        public static int ActiveSectors;        // of those, active (refreshed on every change of the active sectors)
        // Gauges of whichever updater does the work: ours while running, else the game's (watched, read only), so an
        // A/B of the switch reads both from the same fields. Cleared on every change of who runs.
        public static bool GameGauges;          // the four below describe the game's updater over every loaded piece
        public static int Quota;                // pieces visited per frame (m_updatesPerFrame; ours: over the active set)
        public static float CycleSeconds;       // between the last two cycle starts: each piece's cover/material/wear period
        public static float SweepSeconds;       // from the last cycle start to the end of its sweep
        public static int SweepFrames;          // frames that sweep took
        public static float AuditSeconds;       // the last full audit pass over the game's list
        public static bool SteppedAside;        // another mod patches the updater: the game's updater runs
        public static bool GaveUp;              // an internal error: the game's updater runs until switched off and on
        public static int MaxVisitsPerFrame;
        public static long Cycles, Visits;
        public static long CatchUps;            // visits of a piece that missed at least one cycle (came back into the set)
        public static long OutsideWear;         // the game's UpdateWear called for an owned piece outside the set (support write due)
        public static long OutsideModWear;      // the same for other mods' patches on UpdateWear (OutsideWearCycle)
        public static bool WearPatched;         // other mods patch WearNTear.UpdateWear (owned pieces outside get it now and then)
        public static long Moved, Missed, Dead, Errors, Rebuilds, Activated, Deactivated;
        public static long Steps;
        public static double MaxStepMs, TotalStepMs;   // our whole step per frame (bookkeeping, audit and the visits)

        // Running: installed, on, and neither stepped aside nor given up (WearNTearActive.Enabled).
        public static bool Running => s_installed && Feature.On && !SteppedAside && !GaveUp;

        public static void ResetCounters()
        {
            Cycles = Visits = CatchUps = OutsideWear = OutsideModWear = Moved = Missed = Dead = Errors = Rebuilds = Activated = Deactivated = 0;
            Steps = 0;
            MaxStepMs = TotalStepMs = 0;
            MaxVisitsPerFrame = 0;
        }

        public static string Report() =>
            $"wear updater {(!s_installed ? "not installed" : !Feature.On ? "off" : GaveUp ? "gave up (internal error)" : SteppedAside ? "stepped aside (other mods patch the updater)" : "on")}: " +
            $"active {ActivePieces}/{AllPieces} pieces in {ActiveSectors}/{Sectors} sectors (window {(s_windowValid ? $"{s_center} r{s_radius}" : "none")}, registered {WearNTearActive.Count}) | " +
            $"{(GameGauges ? "game's updater (all pieces)" : "ours (active set)")}: quota {Quota}, cycle {CycleSeconds:F2} s, " +
            $"sweep {SweepSeconds:F2} s in {SweepFrames} frames | max {MaxVisitsPerFrame}/frame, " +
            $"cycles {Cycles}, visits {Visits}, catch-ups {CatchUps}, outside wear {OutsideWear} + {OutsideModWear} for mods{(WearPatched ? "" : " (none patch it)")}, audit pass {AuditSeconds:F1} s | " +
            $"moved {Moved}, missed {Missed}, dead {Dead}, errors {Errors}, rebuilds {Rebuilds}, in {Activated}, out {Deactivated} | " +
            $"step {(Steps > 0 ? TotalStepMs / Steps : 0):F3} ms avg, max {MaxStepMs:F2} ms";

        // The audit looks at every piece of the game's list once in this long (game time), at most AuditMaxPerFrame a
        // frame; after the game's list and the files were found to differ in size, AuditHurryPerFrame a frame for one
        // full pass.
        const float AuditCycle = 6f;
        const int AuditMaxPerFrame = 2048;
        const int AuditHurryPerFrame = 8192;
        // Zones beyond the game's total simulation distance still counted as the player's: a moving piece whose ZDO is
        // ahead of its transform, and the jitter of a reference position on a zone border. (The game's active area for
        // pieces, PointInsideActiveArea, reaches 1.5 zones from the centre of the player's zone, so 2 zones at most.)
        const int WindowMargin = 1;
        // How often other mods' patches on the updater (and on WearNTear.UpdateWear) are looked at (real time).
        const float PatchCheckSeconds = 5f;
        // While other mods patch WearNTear.UpdateWear: how often an owned piece outside the set still gets it (game
        // time), for whatever those patches write. About the game's own sweep with ~100k far-area pieces loaded.
        const float OutsideWearCycle = 20f;
        // The game's quota bounds (WearNTearUpdater clamps m_updatesPerFrame to these after every sweep).
        const int MinQuota = 5, MaxQuota = 100;

        // ---- the game's private parts (looked up in Install) -------------------------------------------

        static AccessTools.FieldRef<WearNTear, ZNetView> s_viewOf;
        static AccessTools.FieldRef<WearNTear, float> s_supportOf;
        static AccessTools.FieldRef<WearNTear, int> s_indexOf;
        static Func<WearNTear, float> s_maxSupportOf;
        static AccessTools.FieldRef<WearNTearUpdater, float> s_sleepUntilOf, s_sleepUntilNextOf;
        static AccessTools.FieldRef<WearNTearUpdater, int> s_perFrameOf;
        static MethodBase s_updateMethod, s_innerMethod, s_wearMethod;
        static bool s_wearPatched;
        // WearNTearUpdater.s_ashlandsWearTexture (Shader.PropertyToID("_AshlandsWearTexture")), asked on first use.
        static int s_ashTextureId;
        static bool s_haveAshTextureId;

        // Set once every patch is in. A failed install leaves whatever Harmony could not take back inert.
        static bool s_installed;

        static void Install(Harmony harmony)
        {
            Type wnt = typeof(WearNTear), upd = typeof(WearNTearUpdater);
            // Everything is looked up before anything is patched: a game update that renamed one of them leaves the
            // module out whole instead of half-installed.
            MethodInfo update = Need(AccessTools.DeclaredMethod(upd, "Update", Type.EmptyTypes), "WearNTearUpdater.Update");
            MethodInfo inner = Need(AccessTools.DeclaredMethod(upd, "UpdateWearNTear", new[] { typeof(float), typeof(float) }), "WearNTearUpdater.UpdateWearNTear");
            MethodInfo awake = Need(AccessTools.DeclaredMethod(wnt, "Awake", Type.EmptyTypes), "WearNTear.Awake");
            MethodInfo onDestroy = Need(AccessTools.DeclaredMethod(wnt, "OnDestroy", Type.EmptyTypes), "WearNTear.OnDestroy");
            MethodInfo maxSupport = Need(AccessTools.DeclaredMethod(wnt, "GetMaxSupport", Type.EmptyTypes), "WearNTear.GetMaxSupport");
            // The calls the updater makes, which this makes too (public in the game; a signature change leaves us out).
            Need(AccessTools.DeclaredMethod(wnt, nameof(WearNTear.UpdateCover), new[] { typeof(float) }), "WearNTear.UpdateCover");
            MethodInfo wear = Need(AccessTools.DeclaredMethod(wnt, nameof(WearNTear.UpdateWear), new[] { typeof(float) }), "WearNTear.UpdateWear");
            Need(AccessTools.DeclaredMethod(wnt, nameof(WearNTear.UpdateAshlandsMaterialValues), new[] { typeof(float) }), "WearNTear.UpdateAshlandsMaterialValues");
            s_viewOf = AccessTools.FieldRefAccess<WearNTear, ZNetView>(Field(wnt, "m_nview", typeof(ZNetView)));
            s_supportOf = AccessTools.FieldRefAccess<WearNTear, float>(Field(wnt, "m_support", typeof(float)));
            s_indexOf = AccessTools.FieldRefAccess<WearNTear, int>(Field(wnt, "m_myIndex", typeof(int)));
            s_sleepUntilOf = AccessTools.FieldRefAccess<WearNTearUpdater, float>(Field(upd, "m_sleepUntil", typeof(float)));
            s_sleepUntilNextOf = AccessTools.FieldRefAccess<WearNTearUpdater, float>(Field(upd, "m_sleepUntilNext", typeof(float)));
            s_perFrameOf = AccessTools.FieldRefAccess<WearNTearUpdater, int>(Field(upd, "m_updatesPerFrame", typeof(int)));
            if (maxSupport.ReturnType != typeof(float))
                throw new MissingMethodException(nameof(WearNTear), "GetMaxSupport (float)");
            s_maxSupportOf = AccessTools.MethodDelegate<Func<WearNTear, float>>(maxSupport);
            s_updateMethod = update;
            s_innerMethod = inner;
            s_wearMethod = wear;

            // Either way the files start over: off drops them, on rebuilds them from the game's list on the next
            // frame, which also takes the game's updater state over again. Registered first, so no failure below
            // leaves a switch that does not drop them.
            Feature.Switched += Switched;

            Type t = typeof(WearNTearActiveModule);
            var attempted = new List<KeyValuePair<MethodBase, MethodInfo>>();
            void Patch(MethodBase original, HarmonyMethod prefix = null, HarmonyMethod finalizer = null)
            {
                foreach (HarmonyMethod m in new[] { prefix, finalizer })
                    if (m != null)
                        attempted.Add(new KeyValuePair<MethodBase, MethodInfo>(original, m.method));
                harmony.Patch(original, prefix: prefix, finalizer: finalizer);
            }
            try
            {
                // Bookkeeping first: the updater prefix reads what these keep.
                Patch(awake, finalizer: new HarmonyMethod(t, nameof(AwakeFinalizer)));
                Patch(onDestroy, prefix: new HarmonyMethod(t, nameof(DestroyPrefix)));
                // Last, so other mods' prefixes see the call (and may skip the updater) before this takes it.
                Patch(update, prefix: new HarmonyMethod(t, nameof(UpdatePrefix)) { priority = Priority.Last });
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
                        EnginePlugin.Log?.LogWarning($"WearNTearActive: could not take back {p.Value.Name} on {p.Key.Name}: {e.Message}");
                    }
                }
                throw;
            }
            s_installed = true;
        }

        static MethodInfo Need(MethodInfo m, string name) =>
            m ?? throw new MissingMethodException(name);

        static FieldInfo Field(Type type, string name, Type fieldType)
        {
            FieldInfo f = AccessTools.DeclaredField(type, name);
            if (f == null || f.IsStatic || f.FieldType != fieldType)
                throw new MissingFieldException(type.Name, name);
            return f;
        }

        // ---- the files -------------------------------------------------------------------------------------

        sealed class Piece
        {
            public WearNTear W;             // null once dropped
            public ZNetView View;
            public Bucket Bucket;           // its sector's file (null while being moved, or dropped)
            public int Slot;                // its place in Bucket.Items
            public int ActiveSlot = -1;     // its place in s_active, -1 outside the active set
            public double CoverClock;       // s_coverClock when it last had (or, disabled, let go) its UpdateCover
            public long Cycle;              // the cycle of its last visit (CatchUps)
            public long Swept = -1;         // s_sweep at its last visit: visited in the running sweep when equal
            public float NextWear;          // outside the set, owned, other mods patch UpdateWear: its next call (Outside)
        }

        sealed class Bucket
        {
            public readonly int Key;
            public readonly Vector2s Zone;
            public readonly List<Piece> Items = new List<Piece>();
            public bool Active;

            public Bucket(int key, Vector2s zone)
            {
                Key = key;
                Zone = zone;
            }
        }

        sealed class ByRef : IEqualityComparer<WearNTear>
        {
            public bool Equals(WearNTear a, WearNTear b) => ReferenceEquals(a, b);
            public int GetHashCode(WearNTear o) => RuntimeHelpers.GetHashCode(o);
        }

        // One int per sector: x in the high half, y in the low one (Vector2s' own hash folds x and y onto each other).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int Key(Vector2s zone) => (zone.x << 16) | (ushort)zone.y;

        // Not readonly: they are replaced when the last piece leaves (logout) or on a switch, since Clear keeps every
        // table at its peak size (~100k pieces with far areas).
        static Dictionary<WearNTear, Piece> s_pieces = new Dictionary<WearNTear, Piece>(new ByRef());
        static Dictionary<int, Bucket> s_buckets = new Dictionary<int, Bucket>();
        // The active set, in sweep order. [0, s_cursor) is what the running sweep has visited.
        static List<Piece> s_active = new List<Piece>();
        static bool s_built;

        // The player's window: sectors within s_radius (Chebyshev) of s_center.
        static bool s_windowValid;
        static Vector2s s_center;
        static int s_radius;
        static bool s_refresh;
        static int s_registrySeen = -1;
        static bool s_distanceWarned;

        // The game's updater state, ours while this runs (WearNTearUpdater's m_index, m_sleepUntil, m_sleepUntilNext,
        // m_updatesPerFrame), the updater it was taken from, and the cover clock.
        static WearNTearUpdater s_updater;
        static int s_cursor;
        static float s_sleepUntil, s_sleepUntilNext;
        static int s_perFrame = 50;
        static double s_coverClock;
        static long s_cycle;
        static float s_cycleStart = -1f;
        static int s_sweepFrames;
        // Names the running sweep (Piece.Swept); moves on at the end of every sweep and on a take-over, so between two
        // sweeps no piece counts as visited in the next one.
        static long s_sweep;

        // The game's updater, watched (read only) while it does the work, for the gauges.
        static WearNTearUpdater s_watched;
        static float s_watchUntil, s_watchTime, s_watchStart = -1f;
        static int s_watchFrames;
        static bool s_watchSwept;           // its last frame was a sweep frame (its end shows on the next one)

        // The audit over the game's list.
        static int s_auditAt;
        static float s_auditBudget;
        static int s_hurry;
        static int s_lastGap;
        static float s_auditStart = -1f;

        static float s_nextPatchCheck;

        static void Switched(bool on)
        {
            GaveUp = false;
            Reset();
        }

        // Drops the files; the next frame (if on) rebuilds them and takes the game's updater state over again.
        static void Reset()
        {
            foreach (Piece p in s_pieces.Values)
                p.W = null;
            s_pieces = new Dictionary<WearNTear, Piece>(s_pieces.Comparer);
            s_buckets = new Dictionary<int, Bucket>();
            s_active = new List<Piece>();
            s_built = false;
            s_windowValid = false;
            s_refresh = false;
            s_registrySeen = -1;
            s_updater = null;
            s_cursor = 0;
            s_cycleStart = -1f;
            s_auditAt = 0;
            s_auditBudget = 0f;
            s_hurry = 0;
            s_lastGap = 0;
            s_auditStart = -1f;
            AllPieces = ActivePieces = Sectors = ActiveSectors = 0;
            // Who runs changes (switch, stepping aside or back, giving up): the gauges start over for whichever does.
            s_watched = null;
            GameGauges = false;
            Quota = SweepFrames = 0;
            CycleSeconds = SweepSeconds = 0f;
        }

        static void GiveUp(Exception e)
        {
            GaveUp = true;
            Reset();
            EnginePlugin.Log?.LogError("WearNTearActive: internal error, the game's updater runs from now on " +
                $"(switch WearNTearActiveSectors off and on to try again): {e}");
        }

        static void Rebuild(List<WearNTear> all)
        {
            Rebuilds++;
            s_built = true;
            s_registrySeen = WearNTearActive.Version;
            s_refresh = false;
            for (int i = 0; i < all.Count; i++)
            {
                WearNTear w = all[i];
                if ((object)w != null && w)
                    Register(w);
            }
            // A dead entry the game keeps in its list (its OnDestroy skipped by some mod) is not filed: that gap is
            // known, it does not make the audit hurry.
            s_lastGap = all.Count - s_pieces.Count;
            ActiveSectors = CountActiveSectors();
        }

        static void Register(WearNTear w)
        {
            if (s_pieces.ContainsKey(w))
                return;
            ZNetView view = s_viewOf(w);
            ZDO zdo = (object)view != null ? view.GetZDO() : null;
            // The game lists only pieces that had a ZDO in Awake; one torn down since is filed where it stands.
            Vector2s zone = zdo != null ? zdo.GetSector() : ZoneSystem.GetZone(w.transform.position);
            var p = new Piece { W = w, View = view, CoverClock = s_coverClock, Cycle = s_cycle };
            s_pieces.Add(w, p);
            Place(p, Key(zone), zone);
        }

        static void Drop(Piece p)
        {
            s_pieces.Remove(p.W);
            Deactivate(p);
            Lift(p);
            p.W = null;
            if (s_pieces.Count == 0)
                Shrink();
        }

        // The last piece left (logout): every bucket is gone already (Lift drops empty ones) and the active list is
        // empty; fresh tables give the peak size back.
        static void Shrink()
        {
            s_pieces = new Dictionary<WearNTear, Piece>(s_pieces.Comparer);
            s_buckets = new Dictionary<int, Bucket>();
            s_active = new List<Piece>();
            s_cursor = 0;
            s_auditAt = 0;
        }

        static void Place(Piece p, int key, Vector2s zone)
        {
            if (!s_buckets.TryGetValue(key, out Bucket b))
            {
                b = new Bucket(key, zone) { Active = IsActive(key, zone) };
                s_buckets.Add(key, b);
                if (b.Active)
                    ActiveSectors++;
            }
            p.Bucket = b;
            p.Slot = b.Items.Count;
            b.Items.Add(p);
            if (b.Active)
                Activate(p);
            else
                Deactivate(p);
        }

        static void Lift(Piece p)
        {
            Bucket b = p.Bucket;
            if (b == null)
                return;
            p.Bucket = null;
            List<Piece> items = b.Items;
            int last = items.Count - 1;
            Piece moved = items[last];
            items[p.Slot] = moved;
            moved.Slot = p.Slot;
            items.RemoveAt(last);
            // An emptied sector gives its file up, or every zone ever visited would keep one. Nothing else holds on
            // to a bucket (Refresh walks the table, the audit and the sweep hold pieces).
            if (last == 0)
            {
                s_buckets.Remove(b.Key);
                if (b.Active)
                    ActiveSectors--;
            }
        }

        // Where the piece's ZDO stands now; a piece that moved to another sector changes file (and maybe set).
        static void Follow(Piece p, ZDO zdo)
        {
            Vector2s zone = zdo.GetSector();
            int key = Key(zone);
            if (key == p.Bucket?.Key)
                return;
            Lift(p);
            Place(p, key, zone);
            Moved++;
        }

        static bool IsActive(int key, Vector2s zone) =>
            s_windowValid && Math.Abs(zone.x - s_center.x) <= s_radius && Math.Abs(zone.y - s_center.y) <= s_radius
            || WearNTearActive.Contains(key);

        static void Activate(Piece p)
        {
            if (p.ActiveSlot >= 0)
                return;
            // Appended: if a sweep is running it visits this piece too (as the game's sweep reaches what Awake
            // appended to its list).
            int last = s_active.Count;
            p.ActiveSlot = last;
            s_active.Add(p);
            Activated++;
            if (p.Swept != s_sweep)
                return;
            // Back within the sweep that visited it already: into the visited part [0, cursor) instead, the first
            // piece not visited yet going to the end. The game visits a piece once per sweep; and a set that flickers
            // at its edge (a player going back and forth over a zone border) would otherwise add the same pieces to
            // the sweep on every flicker and could keep it from ever ending.
            int c = s_cursor;
            if (c != last)
            {
                Piece m = s_active[c];
                s_active[c] = p;
                p.ActiveSlot = c;
                s_active[last] = m;
                m.ActiveSlot = last;
            }
            s_cursor = c + 1;
        }

        static void Deactivate(Piece p)
        {
            int j = p.ActiveSlot;
            if (j < 0)
                return;
            p.ActiveSlot = -1;
            Deactivated++;
            int last = s_active.Count - 1;
            if (j < s_cursor)
            {
                // Inside the part the running sweep has visited: keep [0, cursor) the visited part. The last visited
                // piece fills the hole and the last piece overall (not visited yet) fills its place, which becomes the
                // next one to visit. So nobody is skipped or visited twice in a sweep.
                int c = s_cursor - 1;
                if (j != c)
                {
                    Piece m = s_active[c];
                    s_active[j] = m;
                    m.ActiveSlot = j;
                }
                if (c != last)
                {
                    Piece m = s_active[last];
                    s_active[c] = m;
                    m.ActiveSlot = c;
                }
                s_cursor = c;
            }
            else if (j != last)
            {
                Piece m = s_active[last];
                s_active[j] = m;
                m.ActiveSlot = j;
            }
            s_active.RemoveAt(last);
        }

        // The active sectors changed (window or registrations): every file whose state flips moves in or out whole.
        static void Refresh()
        {
            s_refresh = false;
            s_registrySeen = WearNTearActive.Version;
            int active = 0;
            foreach (Bucket b in s_buckets.Values)
            {
                bool want = IsActive(b.Key, b.Zone);
                if (want)
                    active++;
                if (want == b.Active)
                    continue;
                b.Active = want;
                List<Piece> items = b.Items;
                for (int i = 0; i < items.Count; i++)
                {
                    if (want)
                        Activate(items[i]);
                    else
                        Deactivate(items[i]);
                }
            }
            ActiveSectors = active;
        }

        static int CountActiveSectors()
        {
            int n = 0;
            foreach (Bucket b in s_buckets.Values)
                if (b.Active)
                    n++;
            return n;
        }

        // ---- hooks -------------------------------------------------------------------------------------------

        // A finalizer, so a piece whose Awake threw after the game listed it is filed too (the game's updater visits
        // it). Never throws itself: that would make Awake throw.
        static void AwakeFinalizer(WearNTear __instance)
        {
            if (!s_built)
                return;
            try
            {
                List<WearNTear> all = WearNTear.GetAllInstances();
                int i = s_indexOf(__instance);
                // Only what the game listed (placement ghosts have no ZDO and are not).
                if (i >= 0 && i < all.Count && ReferenceEquals(all[i], __instance))
                    Register(__instance);
            }
            catch (Exception e)
            {
                GiveUp(e);
            }
        }

        static void DestroyPrefix(WearNTear __instance)
        {
            if (!s_built)
                return;
            try
            {
                if (s_pieces.TryGetValue(__instance, out Piece p))
                    Drop(p);
            }
            catch (Exception e)
            {
                GiveUp(e);
            }
        }

        // Prefix on WearNTearUpdater.Update (Priority.Last). While running it does the updater's work and skips the
        // game's; anything else (off, stepped aside, another prefix skipped the call) leaves the call as it is (off,
        // stepped aside or given up, it reads the game's updater for the gauges, see Watch).
        static bool UpdatePrefix(WearNTearUpdater __instance, bool __runOriginal)
        {
            if (!__runOriginal || !s_installed)
                return true;
            if (!Feature.On || GaveUp)
            {
                Watch(__instance, Time.time);
                return true;
            }
            float now = Time.realtimeSinceStartup;
            if (now >= s_nextPatchCheck)
            {
                s_nextPatchCheck = now + PatchCheckSeconds;
                string wearWho = Foreign(Harmony.GetPatchInfo(s_wearMethod), true);
                if ((wearWho != null) != s_wearPatched)
                {
                    WearPatched = s_wearPatched = wearWho != null;
                    EnginePlugin.Log?.LogInfo(s_wearPatched
                        ? $"WearNTearActive: {wearWho} patches WearNTear.UpdateWear; owned pieces outside the active set " +
                          $"get it every {OutsideWearCycle:F0} s"
                        : "WearNTearActive: no other mod patches WearNTear.UpdateWear any more");
                }
                bool aside = OtherModsPatch(out string who);
                if (aside != SteppedAside)
                {
                    SteppedAside = aside;
                    // Either way the next run starts from the game's list and the game's updater state.
                    Reset();
                    EnginePlugin.Log?.LogWarning(aside
                        ? $"WearNTearActive: {who} patches WearNTearUpdater; the game's updater runs while it does"
                        : "WearNTearActive: no other mod patches WearNTearUpdater any more; taking over again");
                }
            }
            if (SteppedAside)
            {
                Watch(__instance, Time.time);
                return true;
            }
            if (!ReferenceEquals(__instance, s_updater))
            {
                // A second live updater (the game has one): it runs as in the game.
                if (s_updater)
                    return true;
                s_updater = __instance;
                TakeOver(__instance);
            }
            try
            {
                Step(__instance);
            }
            catch (Exception e)
            {
                GiveUp(e);
            }
            return false;
        }

        static bool OtherModsPatch(out string who)
        {
            who = Foreign(Harmony.GetPatchInfo(s_innerMethod), true) ?? Foreign(Harmony.GetPatchInfo(s_updateMethod), false);
            return who != null;
        }

        // The owner of a patch our skipping of the original would bypass: on UpdateWearNTear any patch, on Update
        // only a rewrite (prefixes and postfixes there still run around ours).
        static string Foreign(Patches info, bool any)
        {
            if (info == null)
                return null;
            foreach (Patch p in info.Transpilers)
                if (!Ours(p.owner))
                    return p.owner;
            foreach (Patch p in info.ILManipulators)
                if (!Ours(p.owner))
                    return p.owner;
            if (!any)
                return null;
            foreach (Patch p in info.Prefixes)
                if (!Ours(p.owner))
                    return p.owner;
            foreach (Patch p in info.Postfixes)
                if (!Ours(p.owner))
                    return p.owner;
            foreach (Patch p in info.Finalizers)
                if (!Ours(p.owner))
                    return p.owner;
            return null;
        }

        // The engine itself, the lab's timers, and Real Portal (Far.Viewer's call-site transpiler on WearNTear.UpdateWear;
        // see the top).
        static bool Ours(string owner) =>
            owner == EnginePlugin.Guid || owner == RealPortalGuid
            || owner != null && owner.StartsWith("valheim.realportal.lab", StringComparison.Ordinal);

        // Real Portal's Harmony id (its plugin GUID).
        const string RealPortalGuid = "valheim.realportal";

        // The game's updater state at this moment becomes ours: whether it is asleep until the next cycle or part way
        // through a sweep (then this sweeps the active set and goes on from there), and its tuned quota.
        static void TakeOver(WearNTearUpdater u)
        {
            s_sleepUntil = s_sleepUntilOf(u);
            s_sleepUntilNext = s_sleepUntilNextOf(u);
            s_perFrame = Mathf.Clamp(s_perFrameOf(u), MinQuota, MaxQuota);
            s_cursor = 0;
            s_sweep++;
            s_cycleStart = -1f;
        }

        // The game's updater while it does the work (this off, stepped aside or given up), read only, right before its
        // Update with the state and the time that Update reads: the same gauges as Run keeps, so an A/B of the switch
        // reads both updaters from the same fields. A cycle start and a sweep frame are what Update is about to do
        // (its own tests); the end of a sweep (m_sleepUntil = m_sleepUntilNext) shows on the frame after.
        static void Watch(WearNTearUpdater u, float time)
        {
            float until = s_sleepUntilOf(u), next = s_sleepUntilNextOf(u);
            if (!ReferenceEquals(u, s_watched))
            {
                if (s_watched)
                    return;     // a second updater (the game has one): not watched
                s_watched = u;
                s_watchStart = -1f;
                s_watchSwept = false;
            }
            else if (s_watchSwept && !until.Equals(s_watchUntil) && s_watchStart >= 0f)
            {
                SweepSeconds = s_watchTime - s_watchStart;
                SweepFrames = s_watchFrames;
            }
            GameGauges = true;
            Quota = s_perFrameOf(u);
            s_watchSwept = false;
            s_watchUntil = until;
            s_watchTime = time;
            if (time < until)
                return;
            if (next.Equals(until))
            {
                // A cycle starts this frame (its passes over every piece; the sweep starts on the next frame).
                if (s_watchStart >= 0f)
                    CycleSeconds = time - s_watchStart;
                s_watchStart = time;
                s_watchFrames = 0;
            }
            else
            {
                s_watchFrames++;
                s_watchSwept = true;
            }
        }

        // ---- the step ----------------------------------------------------------------------------------------

        static void Step(WearNTearUpdater updater)
        {
            long t0 = Stopwatch.GetTimestamp();
            // What the game's Update reads.
            float time = Time.time;
            float dt = Time.deltaTime;
            List<WearNTear> all = WearNTear.GetAllInstances();
            Window();
            if (!s_built)
                Rebuild(all);
            else if (s_refresh || s_registrySeen != WearNTearActive.Version)
                Refresh();
            Audit(all, time, dt);
            Run(updater, time, dt);
            AllPieces = s_pieces.Count;
            ActivePieces = s_active.Count;
            Sectors = s_buckets.Count;
            GameGauges = false;
            Quota = s_perFrame;
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            Steps++;
            TotalStepMs += ms;
            if (ms > MaxStepMs)
                MaxStepMs = ms;
        }

        static void Window()
        {
            ZNet znet = ZNet.instance;
            if (znet == null)
                return;     // keep what we had; the game's own updates need ZNet as well
            Vector2s center = ZoneSystem.GetZone(znet.GetReferencePosition());
            int total;
            try
            {
                total = znet.GetSyncedSimulationDistance().TotalSimulationDistance;
            }
            catch (Exception e)
            {
                // The game asks the same in every UpdateWear; should it not answer (no graphics settings?), the
                // original distance (2 + 2) is assumed.
                total = SimulationDistance.OriginalDistance.TotalSimulationDistance;
                if (!s_distanceWarned)
                {
                    s_distanceWarned = true;
                    EnginePlugin.Log?.LogWarning($"WearNTearActive: no simulation distance ({e.Message}); assuming {total} zones");
                }
            }
            int radius = Math.Max(total, 2) + WindowMargin;
            if (s_windowValid && center == s_center && radius == s_radius)
                return;
            s_windowValid = true;
            s_center = center;
            s_radius = radius;
            s_refresh = true;
        }

        // WearNTearUpdater.UpdateWearNTear over the active set, step for step (see the top for the one change).
        static void Run(WearNTearUpdater updater, float time, float dt)
        {
            if (time < s_sleepUntil)
                return;
            if (s_sleepUntilNext.Equals(s_sleepUntil))
            {
                // A new cycle. The game sets the global ash texture and gives every enabled piece UpdateCover(deltaTime)
                // right here; the dt goes on the clock instead and each piece takes it at its visit.
                s_sleepUntilNext = time + 1f;
                if (!s_haveAshTextureId)
                {
                    s_ashTextureId = Shader.PropertyToID("_AshlandsWearTexture");
                    s_haveAshTextureId = true;
                }
                Shader.SetGlobalTexture(s_ashTextureId, updater.m_ashlandsWearTexture);
                s_coverClock += dt;
                s_cycle++;
                Cycles++;
                if (s_cycleStart >= 0f)
                    CycleSeconds = time - s_cycleStart;
                s_cycleStart = time;
                s_sweepFrames = 0;
                // The game's cycle-start frame does its passes and nothing else; the sweep starts on the next frame.
                // Kept, so the sweep takes as many frames as the game's and the quota is tuned exactly as there.
                return;
            }
            s_sweepFrames++;
            int visits = 0;
            while (visits < s_perFrame && s_cursor < s_active.Count)
            {
                Piece p = s_active[s_cursor++];
                visits++;
                Visit(p, time);
            }
            Visits += visits;
            if (visits > MaxVisitsPerFrame)
                MaxVisitsPerFrame = visits;
            if (s_cursor < s_active.Count)
                return;
            s_cursor = 0;
            s_sweep++;
            // The end of a sweep: the game's pace control, as it is.
            float left = s_sleepUntilNext - time;
            if (!(Utils.Abs(left) < 0.1f))
            {
                if (left < -0.8f)
                    s_perFrame += 20;
                else if (left < -0.4f)
                    s_perFrame += 15;
                else if (left < -0.2f)
                    s_perFrame += 10;
                else if (left < 0f)
                    s_perFrame += 5;
                else if (left > 0.8f)
                    s_perFrame -= 20;
                else if (left > 0.6f)
                    s_perFrame -= 15;
                else if (left > 0.3f)
                    s_perFrame -= 10;
                else if (left > 0.2f)
                    s_perFrame -= 5;
            }
            s_sleepUntil = s_sleepUntilNext;
            s_perFrame = Mathf.Max(s_perFrame, MinQuota);
            s_perFrame = Mathf.Min(s_perFrame, MaxQuota);
            if (s_cycleStart >= 0f)
                SweepSeconds = time - s_cycleStart;
            SweepFrames = s_sweepFrames;
        }

        // One piece of the active set: the game's cover pass, material pass and wear sweep calls for it, in that order.
        static void Visit(Piece p, float time)
        {
            WearNTear w = p.W;
            if (!w)
            {
                // Destroyed without its OnDestroy reaching us: the game's list would not hold it either.
                Drop(p);
                Dead++;
                return;
            }
            double cover = s_coverClock - p.CoverClock;
            p.CoverClock = s_coverClock;
            bool catchUp = s_cycle - p.Cycle > 1;
            if (catchUp)
                CatchUps++;
            p.Cycle = s_cycle;
            p.Swept = s_sweep;
            try
            {
                bool enabled = w.enabled;
                // Back after missing cycles: the snow as the ZDO has it now, which the game's pass would have read
                // last (see the top), before the owner branch of UpdateWear adds to it and writes it.
                if (enabled && catchUp && w.m_snow)
                {
                    ZNetView v = p.View;
                    if ((object)v != null && v.IsValid())
                        w.m_snowBuildup = v.GetZDO().GetFloat(ZDOVars.s_snow);
                }
                // Zero only for a piece filed during this cycle (the game's cover pass ran before it existed) or
                // already visited in it.
                if (enabled && cover > 0.0)
                    w.UpdateCover((float)cover);
                w.UpdateAshlandsMaterialValues(time);
                if (enabled)
                    w.UpdateWear(time);
            }
            catch (Exception e)
            {
                PieceError(e, w);
            }
            if ((object)p.W == null)
                return;     // dropped by what it called (DestroyImmediate)
            ZNetView view = p.View;
            ZDO zdo = (object)view != null ? view.GetZDO() : null;
            if (zdo != null)
                Follow(p, zdo);
        }

        // ---- the audit ---------------------------------------------------------------------------------------

        // Walks the game's list at AuditCycle per pass: files what the hooks missed, follows pieces outside the active
        // set that moved (into it, say), and does for those what the game would write or fade for them (Outside).
        static void Audit(List<WearNTear> all, float time, float dt)
        {
            int n = all.Count;
            int gap = n - s_pieces.Count;
            if (gap != s_lastGap)
            {
                // Something changed the game's list behind the hooks: one full pass at once.
                s_lastGap = gap;
                if (gap != 0)
                    s_hurry = n;
            }
            if (n == 0)
            {
                s_auditAt = 0;
                return;
            }
            int take;
            if (s_hurry > 0)
            {
                take = Math.Min(s_hurry, AuditHurryPerFrame);
                s_hurry -= take;
            }
            else
            {
                s_auditBudget += n * Mathf.Min(dt, 0.25f) / AuditCycle;
                take = (int)s_auditBudget;
                s_auditBudget -= take;
                take = Math.Min(take, Math.Min(n, AuditMaxPerFrame));
            }
            for (int k = 0; k < take; k++)
            {
                if (s_auditAt >= all.Count)
                {
                    if (s_auditStart >= 0f)
                        AuditSeconds = time - s_auditStart;
                    s_auditStart = time;
                    s_auditAt = 0;
                    if (all.Count == 0)
                        break;
                }
                AuditOne(all[s_auditAt++], time);
            }
        }

        static void AuditOne(WearNTear w, float time)
        {
            if ((object)w == null)
                return;
            if (!s_pieces.TryGetValue(w, out Piece p))
            {
                if (w)
                {
                    Register(w);
                    Missed++;
                }
                return;
            }
            if (p.ActiveSlot >= 0)
                return;     // the sweep looks after it
            if (!w)
            {
                Drop(p);
                Dead++;
                return;
            }
            ZNetView view = p.View;
            ZDO zdo = (object)view != null ? view.GetZDO() : null;
            if (zdo == null)
                return;     // being torn down: the game's UpdateWear would return at once (!IsValid)
            Follow(p, zdo);
            if (p.ActiveSlot >= 0)
                return;     // moved into the active set
            Outside(p, p.W, view, zdo, time);
        }

        // A piece outside the active set. What the game's updater would do with it that is not a visual refresh:
        //  - UpdateWear (enabled pieces only), owned and past its first 30 s (ShouldUpdate), outside the game's active
        //    area:
        //      if (!m_support.Equals(GetMaxSupport())) zdo.Set(ZDOVars.s_support, GetMaxSupport()); return;
        //    and ZDO.Set bumps the data revision only when the stored value changes. So only a piece whose ZDO does
        //    not hold that value yet has anything to write: for it the game's own UpdateWear is called, which decides
        //    (ownership, ShouldUpdate, OutsideActiveArea) and writes exactly that. m_support is never changed on this
        //    branch, so the test stays exact.
        //  - UpdateAshlandsMaterialValues: the burn/ash damage timers run down and the material value follows them.
        //    Called as the game calls it (it measures the time since its last call itself).
        //  - other mods' patches on UpdateWear (Path of Valheiman's prefix keeps three ZDO fields of the owner's own
        //    pieces in line with its passive tree, wherever they stand): while there are any, every owned piece also
        //    gets the game's UpdateWear once per OutsideWearCycle, so what they write is written too (they write values,
        //    not increments: only how soon after a change it lands differs).
        static void Outside(Piece p, WearNTear w, ZNetView view, ZDO zdo, float time)
        {
            try
            {
                // UpdateWear only for an enabled piece, as the game's updater (its material pass takes them all).
                if (w.enabled && view.IsOwner())
                {
                    float max = s_maxSupportOf(w);
                    bool due = !s_supportOf(w).Equals(max) && !zdo.GetFloat(ZDOVars.s_support, float.NaN).Equals(max);
                    if (due || s_wearPatched && time >= p.NextWear)
                    {
                        p.NextWear = time + OutsideWearCycle;
                        w.UpdateWear(time);
                        if (due)
                            OutsideWear++;
                        else
                            OutsideModWear++;
                    }
                }
                w.UpdateAshlandsMaterialValues(time);
            }
            catch (Exception e)
            {
                PieceError(e, w);
            }
        }

        static void PieceError(Exception e, WearNTear w)
        {
            Errors++;
            if (Errors <= 10)
            {
                string name;
                try
                {
                    name = w ? w.name : "a destroyed piece";
                }
                catch
                {
                    name = "a piece";
                }
                EnginePlugin.Log?.LogWarning($"WearNTearActive: {name} threw in the game's own update; skipped, the sweep goes on " +
                    $"(the game's updater would stop on it every frame){(Errors == 10 ? "; further ones are only counted" : "")}: {e}");
            }
        }
    }

    // The sectors whose pieces get the game's wear/cover/material passes besides the player's own surroundings (see
    // WearNTearActiveModule): Real Portal registers the 3x3 sectors of every awake far core here (Far.Viewer; the
    // sectors of its active area, Docs\PARITY-PLAN.md P18), as they wake and fall asleep. Refcounted: every
    // AddSector(s) is undone by one RemoveSector(s) with the same sectors; a sector stays registered while its count is
    // above zero. Main thread only. It is plain bookkeeping that works whether or not the module is installed or on
    // (Enabled says whether it currently has any effect); registrations survive switching and world changes, so the
    // caller removes what it added (ClearSectors drops everything).
    public static class WearNTearActive
    {
        static readonly Dictionary<int, int> s_refs = new Dictionary<int, int>();

        // Bumped whenever a sector becomes registered or unregistered; the module refreshes its active set then.
        internal static int Version;

        // Removes of sectors that were not registered (a caller's bug; ignored).
        public static long UnbalancedRemoves;

        public static bool Enabled => WearNTearActiveModule.Running;

        // Registered sectors (count > 0).
        public static int Count => s_refs.Count;

        public static void AddSector(Vector2s sector)
        {
            int key = WearNTearActiveModule.Key(sector);
            s_refs.TryGetValue(key, out int n);
            s_refs[key] = n + 1;
            if (n == 0)
                Version++;
        }

        public static void RemoveSector(Vector2s sector)
        {
            int key = WearNTearActiveModule.Key(sector);
            if (!s_refs.TryGetValue(key, out int n))
            {
                if (UnbalancedRemoves++ < 5)
                    EnginePlugin.Log?.LogWarning($"WearNTearActive: RemoveSector({sector}) without a matching AddSector; ignored");
                return;
            }
            if (n <= 1)
            {
                s_refs.Remove(key);
                Version++;
            }
            else
            {
                s_refs[key] = n - 1;
            }
        }

        public static void AddSectors(IEnumerable<Vector2s> sectors)
        {
            if (sectors == null)
                return;
            foreach (Vector2s s in sectors)
                AddSector(s);
        }

        public static void RemoveSectors(IEnumerable<Vector2s> sectors)
        {
            if (sectors == null)
                return;
            foreach (Vector2s s in sectors)
                RemoveSector(s);
        }

        // ZoneSystem.SectorIndex (ZDOMan.m_objectsBySector's key). SectorZero ("outside the zones") is skipped by both.
        public static void AddSectors(IEnumerable<ZoneSystem.SectorIndex> sectors)
        {
            if (sectors == null)
                return;
            foreach (ZoneSystem.SectorIndex s in sectors)
                if (s.Sector != 0)
                    AddSector(ZoneSystem.IndexToSector(s.Sector));
        }

        public static void RemoveSectors(IEnumerable<ZoneSystem.SectorIndex> sectors)
        {
            if (sectors == null)
                return;
            foreach (ZoneSystem.SectorIndex s in sectors)
                if (s.Sector != 0)
                    RemoveSector(ZoneSystem.IndexToSector(s.Sector));
        }

        public static int RefCount(Vector2s sector) =>
            s_refs.TryGetValue(WearNTearActiveModule.Key(sector), out int n) ? n : 0;

        public static void ClearSectors()
        {
            if (s_refs.Count == 0)
                return;
            s_refs.Clear();
            Version++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool Contains(int key) => s_refs.Count != 0 && s_refs.ContainsKey(key);
    }
}
