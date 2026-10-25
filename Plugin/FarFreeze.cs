using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace RealPortal
{
    // Far.FreezeLists (Docs\PORTALS20-PLAN.md, B1; ARCHITECTURE-v2 §3.4 and §3.11, MaxLiveCores 1). Every loaded far
    // area ran as the player's own surroundings run: its creatures thought, synced and animated, its fires and smelters
    // ticked, on every physics step (about 5 ms a step at twenty portals against 1.4 without them). Now one far area
    // lives at a time: the one whose portal was crossed in the last 10 s, is aimed through (a bow drawn or a throw on
    // its way through its opening), or is within 30 m (or reached within 3 s, 60 m at most); the best of those by the
    // §3.4 score, kept for 10 s after it stops being one. Every other far area stands still, as the game leaves a place
    // no player is near, but its objects stay loaded, visible and owned as before (taking ownership away would send hits
    // and use through the windows to nobody, and hide its characters: plan §5).
    //
    // How it stands still:
    //  - MonoUpdaters' update lists (the game's passes of ZSyncTransform, ZSyncAnimation, Floating, Character, which is
    //    Humanoid too, CharacterAnimEvent, Fish, RandomFlyingBird) are handed over without that place's objects, the
    //    way HeightmapIdle hands over its heightmaps: the same pass, container, scope and order, a shorter source. The
    //    list given is kept and reused while the game's list is unchanged (its version), extended when entries were
    //    only appended, and made again when the frozen places change and once a second (creatures that walked across).
    //  - AI updates (BaseAI.UpdateAI) return at once for them, from FarLife's prefix on each UpdateAI: a prefix on the
    //    shared MonoUpdatersExtra.UpdateAI would make the engine's AiStagger stand down (another mod's patch there).
    //  - Their bodies are held: the lab's naive try (every script switched off) made physics dearer, away 10.2 -> 14.8
    //    ms and PhysicsFixedUpdate 0.7 -> 2.3 ms. Nothing kept the bodies still any more: an owned creature's Character
    //    sets its velocity every step (and its collider's friction, none off the ground; a flier has no gravity),
    //    Floating wakes and lifts what lies in water every step, a fish swims by its own update; switched off mid-motion
    //    they slid, drifted and sank on and on (a non-owned body only stays asleep because ZSyncTransform.ClientSync
    //    keeps putting it to sleep). So each frozen creature, fish, bird and floating thing has its velocity zeroed and
    //    its body made kinematic (the game does the same to a sleeping creature, Character.m_disableWhileSleeping), and
    //    its animator switched off (it keeps its pose). What else lies still stays asleep: nothing frozen touches it.
    //    Letting go restores what was changed (only that), before the object is handed to its updates again.
    //  - Production and growth timers (fires, smelters, kilns, fermenters, beehives, sap, crops' respawn, doors, taming,
    //    breeding, growing up, idle sounds) return at once. What they count is world time, so they catch up when the
    //    place runs again, as the game does when the player comes back.
    //  - FarLife (seeing, hearing and reaching through portals) only reaches into the live area.
    // Holding and letting go go an object at a time within a time budget each frame (the first always goes): with
    // Far.SleepBudget on, within what is left of its 1 ms after the frame's sleeping and waking.
    // Not frozen: the player's own surroundings (the near zones round ZNet's reference position) and anything another
    // player owns (their game runs it, this one only shows it). Ownership is never touched.
    // Off: every prefix returns at once and FarLife does what it did; switched off, what is held is let go over the
    // next frames and stays out of the updates until then.
    //
    // Far.AiStaggerFar (round 3), the milder sibling: the same live area and the same table of places (the player's
    // near zones and the live area run, every other far area's zones are "frozen"), but nothing stands still; only
    // the AI of a creature this game owns in a frozen place thinks once every 4 AI ticks instead of every tick, with the
    // time it was owed (the engine's AiStaggerModule.IsFarSlow, set here while the switch is on; the engine holds the
    // ticks back and keeps each creature's phase). Creatures by the player and in the live area think as in the game.
    // With Far.AiStaggerFar on and Far.FreezeLists off the table is made for the hook only: nothing is held, left out
    // of the update lists or kept from its timers (s_freeze). With both on, frozen creatures' updates return at once in
    // FarLife's prefix whatever the engine gives them.
    [HarmonyPatch]
    internal static class FarFreeze
    {
        // The live area: the portal crossed this long ago at most, reached within this long, kept for this long after
        // it stops being one; another candidate takes over only this much better (10 m closer).
        const float CrossedSeconds = 10f;
        const float HoldSeconds = 10f;
        const float HotDistance = 30f;
        const float ApproachDistance = 60f;
        const float ApproachSeconds = 3f;
        const float SwitchMargin = 0.1f;
        // An aim through a portal's opening counts this far (arrows land within the preload reach).
        const float AimReach = 120f;
        // A creature moved through a portal stands this close to the far portal right after.
        const float ArrivalRadius = 4f;
        // Lists given are looked through again this often (what walked across, owners changed).
        const float RecheckSeconds = 1f;
        // Holding and letting go: the time a frame without Far.SleepBudget.
        const double TurnMs = 1.0;

        // Per sector, by ZoneSystem.SectorToIndex (how ZDOMan files ZDOs): it runs as the game runs it, or it stands.
        const byte Runs = 1, Frozen = 2;
        const int SectorCount = 512 * 512;

        // ---- lab counters (internal static, for the lab and the 10 s stats) ----
        internal static int LabLiveSwitches, LabFrozenSectors, LabHeld;
        // Far.AiStaggerFar: sectors whose owned creatures think slowly (the frozen ones of the table).
        internal static int LabSlowSectors;
        internal static long LabHolds, LabThaws, LabRebuilds, LabAppends, LabAiSkipped, LabTimersSkipped;
        internal static double LabTurnMsMax;
        // Far.FreezeVerify: lists given checked against the game's, and those found not to be a subsequence of it.
        internal static long LabVerifyChecks, LabVerifyMismatches;

        // One of MonoUpdaters' lists: what was handed over for it, and what that was made of.
        sealed class Pass
        {
            internal readonly string Name;
            internal readonly List<IMonoUpdater> Source;
            internal readonly Func<IMonoUpdater, ZNetView> View;
            // Its frozen entries are held (bodies kinematic, animators off).
            internal readonly bool Holds;
            internal readonly List<IMonoUpdater> Kept = new List<IMonoUpdater>();
            internal int Version, Count, Left, Epoch = -1;
            internal float Until;
            internal IMonoUpdater Last;

            internal Pass(string name, List<IMonoUpdater> source, Func<IMonoUpdater, ZNetView> view, bool holds)
            {
                Name = name;
                Source = source;
                View = view;
                Holds = holds;
            }
        }

        // What holding changed on an object, so letting go restores that and nothing else.
        sealed class Held
        {
            internal Rigidbody Body;
            // An Animator (UnityEngine.AnimationModule, which the plugin does not reference): switched as a Behaviour.
            internal Behaviour Anim;
        }

        static Pass[] s_passes;
        static AccessTools.FieldRef<List<IMonoUpdater>, int> s_listVersion;
        static AccessTools.FieldRef<BaseAI, ZNetView> s_aiView;
        static AccessTools.FieldRef<Character, Rigidbody> s_charBody;
        static AccessTools.FieldRef<Character, Behaviour> s_charAnim;
        static Type s_animatorType;
        static bool s_ready, s_broken;

        static byte[] s_table;
        static readonly List<int> s_marked = new List<int>();
        static int s_frozenSectors;
        static bool s_tableBuilt;
        static int s_tableVersion;
        static ZDOID s_tableLive;
        static Vector2s s_tableZone;
        static int s_tableNear;
        // Moves whenever what is frozen changes (the table, or something let go): the lists given are made again.
        static int s_epoch;
        // Something stands still or is held: the prefixes look at their lists.
        static bool s_active;
        static bool s_inWorld;
        // Far.FreezeLists is on (the table's frozen places stand still); without it the table is Far.AiStaggerFar's only.
        static bool s_freeze;
        // Far.AiStaggerFar: the engine's AI pass asks SlowFar (set while the switch is on).
        static bool s_slowHooked;
        static readonly Func<BaseAI, bool> s_slowFar = SlowFar;

        static ZDOID s_live = ZDOID.None;
        static float s_liveUntil;
        static string s_liveWhy = "";
        static readonly Dictionary<ZDOID, float> s_crossedAt = new Dictionary<ZDOID, float>();
        static readonly List<ZDOID> s_crossedDrop = new List<ZDOID>();
        static int s_through;

        static readonly Dictionary<ZNetView, Held> s_held = new Dictionary<ZNetView, Held>(ByReference.Instance);
        static readonly Stack<Held> s_pool = new Stack<Held>();
        static readonly List<ZNetView> s_toHold = new List<ZNetView>();
        static readonly HashSet<ZNetView> s_holdQueued = new HashSet<ZNetView>(ByReference.Instance);
        static readonly List<ZNetView> s_toThaw = new List<ZNetView>();
        static readonly HashSet<ZNetView> s_thawQueued = new HashSet<ZNetView>(ByReference.Instance);
        static int s_sweptEpoch = -1;
        static float s_sweepAt;
        static readonly Stopwatch s_clock = new Stopwatch();

        static float s_statNext;
        static int s_statHolds, s_statThaws;
        static long s_statAi, s_statTimers;
        static double s_statTurnWorst;

        // ---- the frame (PortalSystem.Update, after RemoteAreas.Update) ----------------------------------------

        internal static void Update(RemoteAreas areas)
        {
            if (areas == null || ZNet.instance == null || ZNetScene.instance == null || ZoneSystem.instance == null || ZDOMan.instance == null)
            {
                if (s_inWorld)
                    Clear();
                return;
            }
            s_inWorld = true;
            FarCores.Update(areas);
            FarViewer.Update(areas);
            if (!Ready())
            {
                if (s_broken && (s_held.Count > 0 || s_tableBuilt))
                    Abandon();
                return;
            }
            float now = Time.time;
            bool freeze = FarFlags.FreezeLists && Plugin.Enabled.Value;
            bool slow = FarFlags.AiStaggerFar && Plugin.Enabled.Value;
            if (freeze || slow)
            {
                NoteArrivals(areas, now);
                ChooseLive(areas, now);
                Table(areas);
            }
            else if (s_tableBuilt)
                ClearTable();
            // What stands changes with the switch even while the table stays (Far.AiStaggerFar's): the lists given are
            // made again and what is held is looked at.
            if (freeze != s_freeze)
            {
                s_freeze = freeze;
                s_epoch++;
            }
            // Only while some far area is slowed: without one the engine's AI pass stays the game's own block.
            HookSlow(slow && s_frozenSectors > 0);
            Sweep(now);
            Turn(areas);
            s_active = (s_freeze && s_frozenSectors > 0) || s_held.Count > 0;
            LabFrozenSectors = s_freeze ? s_frozenSectors : 0;
            LabSlowSectors = slow ? s_frozenSectors : 0;
            LabHeld = s_held.Count;
            Stats();
        }

        // Field accessors and the lists, looked up once; a game update that renamed one leaves the freeze out (off).
        static bool Ready()
        {
            if (s_ready || s_broken)
                return s_ready;
            try
            {
                var zst = AccessTools.FieldRefAccess<ZSyncTransform, ZNetView>("m_nview");
                var zsa = AccessTools.FieldRefAccess<ZSyncAnimation, ZNetView>("m_nview");
                var floating = AccessTools.FieldRefAccess<Floating, ZNetView>("m_nview");
                var character = AccessTools.FieldRefAccess<Character, ZNetView>("m_nview");
                var animEvent = AccessTools.FieldRefAccess<CharacterAnimEvent, ZNetView>("m_nview");
                var fish = AccessTools.FieldRefAccess<Fish, ZNetView>("m_nview");
                var bird = AccessTools.FieldRefAccess<RandomFlyingBird, ZNetView>("m_nview");
                s_aiView = AccessTools.FieldRefAccess<BaseAI, ZNetView>("m_nview");
                s_charBody = AccessTools.FieldRefAccess<Character, Rigidbody>("m_body");
                s_charAnim = AccessTools.FieldRefAccess<Character, Behaviour>("m_animator");
                s_animatorType = AccessTools.Field(typeof(Character), "m_animator").FieldType;
                s_passes = new[]
                {
                    new Pass("ZSyncTransform", ZSyncTransform.Instances, u => u is ZSyncTransform s ? zst(s) : null, false),
                    new Pass("ZSyncAnimation", ZSyncAnimation.Instances, u => u is ZSyncAnimation s ? zsa(s) : null, false),
                    new Pass("Floating", Floating.Instances, u => u is Floating s ? floating(s) : null, true),
                    new Pass("Character", Character.Instances, u => u is Character s ? character(s) : null, true),
                    new Pass("CharacterAnimEvent", CharacterAnimEvent.Instances, u => u is CharacterAnimEvent s ? animEvent(s) : null, false),
                    new Pass("Fish", Fish.Instances, u => u is Fish s ? fish(s) : null, true),
                    new Pass("RandomFlyingBird", RandomFlyingBird.Instances, u => u is RandomFlyingBird s ? bird(s) : null, true),
                };
                // A list's change count (List<T>._version): without it every pass looks its list through again.
                try
                {
                    s_listVersion = AccessTools.FieldRefAccess<List<IMonoUpdater>, int>("_version");
                }
                catch (Exception e)
                {
                    s_listVersion = null;
                    Plugin.Log.LogWarning("Far.FreezeLists: List<T>._version not found, update lists are looked through on every pass: " + e.Message);
                }
                Api.Mechanics.PlayerCrossed += PlayerCrossed;
                s_through = PortalSystem.LabCreaturesThrough;
                s_ready = true;
            }
            catch (Exception e)
            {
                s_broken = true;
                Plugin.Log.LogError("Far.FreezeLists: not set up (game update?), far areas run as before: " + e);
            }
            return s_ready;
        }

        // ---- the live area -----------------------------------------------------------------------------

        // The local player stepped through a real portal: both of its ends were crossed now (the place behind is the
        // far area of the portal stepped into).
        static void PlayerCrossed(Player p, TeleportWorld from, TeleportWorld to)
        {
            if (p == null || p != Player.m_localPlayer)
                return;
            float now = Time.time;
            NoteCrossed(from, now);
            NoteCrossed(to, now);
        }

        static void NoteCrossed(TeleportWorld portal, float now)
        {
            if (portal != null && portal.TryGetComponent(out ZNetView v) && v.GetZDO() != null)
                s_crossedAt[v.GetZDO().m_uid] = now;
        }

        // A creature was moved through a portal (PortalSystem counts them): the far area it came out in was crossed.
        // It stands by that area's far portal right after; one standing there anyway only makes it count as crossed.
        static void NoteArrivals(RemoteAreas areas, float now)
        {
            int through = PortalSystem.LabCreaturesThrough;
            if (through == s_through)
                return;
            s_through = through;
            List<Character> all = Character.GetAllCharacters();
            float r2 = ArrivalRadius * ArrivalRadius;
            foreach (PortalLink link in PortalLink.All)
            {
                ZDO far = link.PartnerZdo;
                if (!link.IsActive || far == null || link.Partner == null || !areas.Has(far))
                    continue;
                Vector3 at = link.Partner.Frame.Center;
                foreach (Character c in all)
                    if (c != null && !c.IsPlayer() && (c.transform.position - at).sqrMagnitude < r2)
                    {
                        s_crossedAt[far.m_uid] = now;
                        break;
                    }
            }
        }

        // §3.4 for a cap of one: score = 4 crossed + 4 aimed + 3 hot - distance / 100 among the areas that are any of
        // the three; promoted at once, kept 10 s after it stops being one.
        static void ChooseLive(RemoteAreas areas, float now)
        {
            if (s_crossedAt.Count > 32)
            {
                s_crossedDrop.Clear();
                foreach (KeyValuePair<ZDOID, float> kv in s_crossedAt)
                    if (now - kv.Value >= CrossedSeconds)
                        s_crossedDrop.Add(kv.Key);
                foreach (ZDOID id in s_crossedDrop)
                    s_crossedAt.Remove(id);
            }
            ZDOID best = ZDOID.None;
            float bestScore = float.NegativeInfinity, liveScore = float.NegativeInfinity;
            string why = "";
            Player p = Player.m_localPlayer;
            if (p != null && !p.IsDead())
            {
                Vector3 at = p.transform.position, v = p.GetVelocity();
                PortalLink aimed = Aimed(p);
                foreach (PortalLink link in PortalLink.All)
                {
                    ZDO far = link.PartnerZdo;
                    if (link.Vanilla || far == null || !areas.Has(far))
                        continue;
                    Vector3 to = link.Frame.Center - at;
                    to.y = 0f;
                    float d = to.magnitude;
                    bool crossed = s_crossedAt.TryGetValue(far.m_uid, out float t) && now - t < CrossedSeconds;
                    bool aim = ReferenceEquals(link, aimed);
                    float closing = d > 0.01f ? (v.x * to.x + v.z * to.z) / d : 0f;
                    bool hot = d < HotDistance || (d < ApproachDistance && closing > 0.1f && d / closing < ApproachSeconds);
                    if (!crossed && !aim && !hot)
                        continue;
                    float score = (crossed ? 4f : 0f) + (aim ? 4f : 0f) + (hot ? 3f : 0f) - d / 100f;
                    if (far.m_uid == s_live && score > liveScore)
                        liveScore = score;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = far.m_uid;
                        why = crossed ? "crossed" : aim ? "aimed through" : "near";
                    }
                }
            }
            if (best != ZDOID.None && best != s_live && (s_live == ZDOID.None || float.IsNegativeInfinity(liveScore) || bestScore > liveScore + SwitchMargin))
            {
                s_live = best;
                s_liveWhy = why;
                s_liveUntil = now + HoldSeconds;
                LabLiveSwitches++;
            }
            else if (!float.IsNegativeInfinity(liveScore))
                s_liveUntil = now + HoldSeconds;
            if (s_live != ZDOID.None && now >= s_liveUntil)
            {
                s_live = ZDOID.None;
                s_liveWhy = "";
            }
        }

        // The live link the local player aims through: a bow drawn, or an attack under way with a weapon that shoots or
        // throws, and the camera's ray crossing its opening (the nearest crossed).
        static PortalLink Aimed(Player p)
        {
            bool aiming = p.IsDrawingBow();
            if (!aiming && p.InAttack())
            {
                ItemDrop.ItemData w = p.GetCurrentWeapon();
                aiming = w != null && (w.m_shared.m_attack.m_attackType == Attack.AttackType.Projectile
                                       || w.m_shared.m_secondaryAttack.m_attackType == Attack.AttackType.Projectile);
            }
            if (!aiming)
                return null;
            Camera cam = Utils.GetMainCamera();
            if (cam == null)
                return null;
            Vector3 from = cam.transform.position, to = from + cam.transform.forward * AimReach;
            PortalLink best = null;
            float bestT = float.MaxValue;
            foreach (PortalLink link in PortalLink.All)
                if (link.IsActive && link.Frame.Crosses(from, to, out _, out float t) && t < bestT)
                {
                    best = link;
                    bestT = t;
                }
            return best;
        }

        // ---- what stands still ---------------------------------------------------------------------------

        // The sectors: the player's own near zones and the live area's run, every other area's zones stand (one that
        // drains too: what it held stays held until it goes, rather than let go to be destroyed). Made again only when
        // one of those changed.
        static void Table(RemoteAreas areas)
        {
            Vector2s me = ZoneSystem.GetZone(ZNet.instance.GetReferencePosition());
            int near = ZNet.instance.GetSyncedSimulationDistance().NearSimulationDistance;
            int version = areas.AreasVersion;
            if (s_tableBuilt && version == s_tableVersion && s_live == s_tableLive && me == s_tableZone && near == s_tableNear)
                return;
            if (s_table == null)
                s_table = new byte[SectorCount];
            Unmark();
            for (int y = me.y - near; y <= me.y + near; y++)
                for (int x = me.x - near; x <= me.x + near; x++)
                    Mark(x, y, Runs);
            int n = areas.Count;
            bool live = false;
            for (int i = 0; i < n; i++)
                if (!areas.AreaDraining(i) && areas.AreaPortal(i) == s_live)
                {
                    live = true;
                    foreach (Vector2s z in areas.AreaZones(i))
                        Mark(z.x, z.y, Runs);
                }
            // The live area is gone (drained, or never had one): none lives until the next candidate.
            if (!live && s_live != ZDOID.None)
            {
                s_live = ZDOID.None;
                s_liveWhy = "";
            }
            for (int i = 0; i < n; i++)
            {
                if (areas.AreaPortal(i) == s_live)
                    continue;
                foreach (Vector2s z in areas.AreaZones(i))
                {
                    int k = Index(z.x, z.y);
                    if (k >= 0 && s_table[k] == 0)
                    {
                        s_table[k] = Frozen;
                        s_marked.Add(k);
                        s_frozenSectors++;
                    }
                }
            }
            s_tableBuilt = true;
            s_tableVersion = version;
            s_tableLive = s_live;
            s_tableZone = me;
            s_tableNear = near;
            s_epoch++;
        }

        static void ClearTable()
        {
            Unmark();
            s_tableBuilt = false;
            s_live = ZDOID.None;
            s_liveWhy = "";
            s_epoch++;
        }

        static void Unmark()
        {
            if (s_table != null)
                foreach (int k in s_marked)
                    s_table[k] = 0;
            s_marked.Clear();
            s_frozenSectors = 0;
        }

        static void Mark(int x, int y, byte b)
        {
            int k = Index(x, y);
            if (k < 0)
                return;
            if (s_table[k] == 0)
                s_marked.Add(k);
            s_table[k] = b;
        }

        // ZoneSystem.SectorToIndex, without its fold of the world's outside onto sector 0.
        static int Index(int x, int y)
        {
            uint ux = (uint)(x + 256), uy = (uint)(y + 256);
            return ux < 512u && uy < 512u ? (int)(uy * 512u + ux) : -1;
        }

        // In a sector that stands, and not another player's (their game runs it).
        static bool FrozenNow(ZNetView v)
        {
            if (!s_freeze || s_frozenSectors == 0)
                return false;
            ZDO z = v.GetZDO();
            if (z == null || s_table[(int)z.GetSectorIndex().Sector] != Frozen)
                return false;
            return !(z.HasOwner() && !z.IsOwner());
        }

        // FarLife: the far side here stands still (nothing is seen, heard or reached there).
        internal static bool FrozenAt(Vector3 at) =>
            s_freeze && s_frozenSectors > 0 && s_table[(int)ZoneSystem.GetSectorIndex(at).Sector] == Frozen;

        // Far.AiStaggerFar, asked by the engine's AI pass for every AI tick: the creature is this game's (only an
        // owner's AI thinks; another player's or nobody's returns after one read) and stands in a frozen place of the
        // table (a far area that is not the live one, outside the player's near zones). Reads only fields: an AI whose
        // object is being destroyed answers from what its view still holds.
        static bool SlowFar(BaseAI ai)
        {
            if (s_frozenSectors == 0 || ai is null)
                return false;
            ZNetView v = s_aiView(ai);
            ZDO z = v is null ? null : v.GetZDO();
            return z != null && s_table[(int)z.GetSectorIndex().Sector] == Frozen && z.IsOwner();
        }

        // Set while Far.AiStaggerFar is on and some far area is not the live one (in a world); cleared, the engine gives
        // each slow AI what it was owed with its next tick.
        static void HookSlow(bool on)
        {
            if (on == s_slowHooked)
                return;
            s_slowHooked = on;
            Engine.AiStaggerModule.IsFarSlow = on ? s_slowFar : null;
        }

        // FarLife's UpdateAI prefix: this AI's creature stands still (or is still held), its update is skipped.
        internal static bool Holds(BaseAI ai)
        {
            if (!s_active || ai is null)
                return false;
            ZNetView v = s_aiView(ai);
            if (v is null || !((s_held.Count > 0 && s_held.ContainsKey(v)) || FrozenNow(v)))
                return false;
            LabAiSkipped++;
            s_statAi++;
            return true;
        }

        // ---- MonoUpdaters' lists -------------------------------------------------------------------------

        // MonoUpdatersExtra.CustomFixedUpdate / CustomLateUpdate(container, source, ...): for the passes of the lists
        // above, the source without what stands still.
        [HarmonyPrefix, HarmonyPatch(typeof(MonoUpdatersExtra), nameof(MonoUpdatersExtra.CustomFixedUpdate))]
        static void FixedPass(ref List<IMonoUpdater> source) => source = Filter(source);

        [HarmonyPrefix, HarmonyPatch(typeof(MonoUpdatersExtra), nameof(MonoUpdatersExtra.CustomLateUpdate))]
        static void LatePass(ref List<IMonoUpdater> source) => source = Filter(source);

        static List<IMonoUpdater> Filter(List<IMonoUpdater> source)
        {
            if (!s_active || source == null)
                return source;
            Pass p = null;
            Pass[] passes = s_passes;
            for (int i = 0; i < passes.Length; i++)
                if (ReferenceEquals(passes[i].Source, source))
                {
                    p = passes[i];
                    break;
                }
            if (p == null)
                return source;
            try
            {
                Refresh(p, source);
                return p.Left > 0 ? p.Kept : source;
            }
            catch (Exception e)
            {
                // Our own bookkeeping failed: the game's whole list from now on.
                s_broken = true;
                s_ready = false;
                s_active = false;
                Plugin.Log.LogError("Far.FreezeLists: failed, far areas run as before for the rest of the session: " + e);
                return source;
            }
        }

        // The list to give stays valid while the game's list is unchanged (the same change count and length), nothing
        // frozen changed and its second is not up. Only appended to since (each Add moves the count by one, and the
        // last entry seen is where it was): the new entries are looked at. Otherwise made again.
        static void Refresh(Pass p, List<IMonoUpdater> source)
        {
            int count = source.Count;
            int version = s_listVersion != null ? s_listVersion(source) : 0;
            float now = Time.time;
            bool same = s_listVersion != null && p.Epoch == s_epoch && now < p.Until;
            if (same && version == p.Version && count == p.Count)
            {
                if (FarFlags.FreezeVerify)
                    Verify(p, source);
                return;
            }
            if (same && count > p.Count && unchecked(version - p.Version) == count - p.Count
                && (p.Count == 0 || ReferenceEquals(source[p.Count - 1], p.Last)))
            {
                for (int i = p.Count; i < count; i++)
                    Take(p, source[i]);
                p.Count = count;
                p.Version = version;
                p.Last = source[count - 1];
                LabAppends++;
                if (FarFlags.FreezeVerify)
                    Verify(p, source);
                return;
            }
            p.Kept.Clear();
            p.Left = 0;
            for (int i = 0; i < count; i++)
                Take(p, source[i]);
            p.Count = count;
            p.Version = version;
            p.Epoch = s_epoch;
            p.Until = now + RecheckSeconds;
            p.Last = count > 0 ? source[count - 1] : null;
            LabRebuilds++;
        }

        static void Take(Pass p, IMonoUpdater item)
        {
            if (Stands(p, item))
                p.Left++;
            else
                p.Kept.Add(item);
        }

        // Held, or in a place that stands still now (for the lists whose objects are held: queued to be held, and left
        // out only once it is).
        static bool Stands(Pass p, IMonoUpdater item)
        {
            ZNetView v = p.View(item);
            if (v is null)
                return false;
            if (s_held.Count > 0 && s_held.ContainsKey(v))
                return true;
            if (!FrozenNow(v))
                return false;
            if (p.Holds)
            {
                // Held first, left out after: until it is held its own update keeps moving it as the game does (left
                // out with a dynamic body it slides, drifts or sinks: plan §5). Turn makes these lists again once held.
                if (s_holdQueued.Add(v))
                    s_toHold.Add(v);
                return false;
            }
            return true;
        }

        // Far.FreezeVerify: the list given is the game's list in its order with only what stands left out (no entry the
        // game took out since, none it added left without a look).
        static void Verify(Pass p, List<IMonoUpdater> source)
        {
            LabVerifyChecks++;
            int j = 0;
            for (int i = 0; i < source.Count && j < p.Kept.Count; i++)
                if (ReferenceEquals(source[i], p.Kept[j]))
                    j++;
            if (j == p.Kept.Count && p.Kept.Count + p.Left == source.Count)
                return;
            LabVerifyMismatches++;
            if (LabVerifyMismatches <= 10)
                Plugin.Log.LogWarning($"Far.FreezeVerify: the {p.Name} list given ({p.Kept.Count} kept, {p.Left} left out) is not the game's ({source.Count}) less what stands; made again");
            p.Epoch = -1;
        }

        // ---- holding and letting go ----------------------------------------------------------------------

        // Held objects no longer in a place that stands (or gone) are let go: looked at when what stands changed, and
        // every second.
        static void Sweep(float now)
        {
            if (s_held.Count == 0 || (s_sweptEpoch == s_epoch && now < s_sweepAt))
                return;
            s_sweptEpoch = s_epoch;
            s_sweepAt = now + RecheckSeconds;
            foreach (KeyValuePair<ZNetView, Held> kv in s_held)
                if ((!kv.Key || !FrozenNow(kv.Key)) && s_thawQueued.Add(kv.Key))
                    s_toThaw.Add(kv.Key);
        }

        // Within the frame's time, letting go first (a place coming alive is what the player is about to look at).
        static void Turn(RemoteAreas areas)
        {
            if (s_toThaw.Count == 0 && s_toHold.Count == 0)
                return;
            double limit = FarFlags.SleepBudget ? Math.Max(0.0, RemoteAreas.TurnBudgetMs - areas.TurnedMs) : TurnMs;
            s_clock.Restart();
            bool first = true;
            int thawed = 0, held = 0;
            while (s_toThaw.Count > 0 && (first || s_clock.Elapsed.TotalMilliseconds < limit))
            {
                ZNetView v = s_toThaw[s_toThaw.Count - 1];
                s_toThaw.RemoveAt(s_toThaw.Count - 1);
                s_thawQueued.Remove(v);
                if (!s_held.TryGetValue(v, out Held h))
                    continue;
                // Frozen again meanwhile (the live area went back): it stays held.
                if (v && FrozenNow(v))
                    continue;
                first = false;
                if (v)
                    LetGo(h);
                s_held.Remove(v);
                h.Body = null;
                h.Anim = null;
                s_pool.Push(h);
                thawed++;
            }
            while (s_toHold.Count > 0 && (first || s_clock.Elapsed.TotalMilliseconds < limit))
            {
                ZNetView v = s_toHold[s_toHold.Count - 1];
                s_toHold.RemoveAt(s_toHold.Count - 1);
                s_holdQueued.Remove(v);
                if (!v || s_held.ContainsKey(v) || !FrozenNow(v))
                    continue;
                first = false;
                Held h = s_pool.Count > 0 ? s_pool.Pop() : new Held();
                Hold(v, h);
                s_held[v] = h;
                LabHolds++;
                s_statHolds++;
                held++;
            }
            // What was held is left out of its updates from the next pass: only the lists that hold are made again
            // (moving the epoch would make all of them again every frame while the queue drains).
            if (held > 0 && s_passes != null)
                foreach (Pass p in s_passes)
                    if (p.Holds)
                        p.Epoch = -1;
            // What was let go is handed to its updates again from the next pass.
            if (thawed > 0)
            {
                LabThaws += thawed;
                s_statThaws += thawed;
                s_epoch++;
            }
            double ms = s_clock.Elapsed.TotalMilliseconds;
            if (ms > LabTurnMsMax)
                LabTurnMsMax = ms;
            if (ms > s_statTurnWorst)
                s_statTurnWorst = ms;
        }

        // Stopped where it is: velocity zeroed and the body kinematic (if it was not), the animator off (it keeps its
        // pose). A creature's own body and animator are its Character's.
        static void Hold(ZNetView v, Held h)
        {
            Rigidbody body;
            Behaviour anim;
            if (v.TryGetComponent(out Character c))
            {
                body = s_charBody(c);
                anim = s_charAnim(c);
            }
            else
            {
                v.TryGetComponent(out body);
                anim = v.GetComponentInChildren(s_animatorType) as Behaviour;
            }
            if (body != null && !body.isKinematic)
            {
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
                body.isKinematic = true;
                h.Body = body;
            }
            if (anim != null && anim.enabled)
            {
                anim.enabled = false;
                h.Anim = anim;
            }
        }

        // Back as it was: only what Hold changed (a creature the game made kinematic meanwhile, asleep, sorts that out
        // in its next step).
        static void LetGo(Held h)
        {
            if (h.Body != null && h.Body.isKinematic)
                h.Body.isKinematic = false;
            if (h.Anim != null && !h.Anim.enabled)
                h.Anim.enabled = true;
        }

        // ---- timers --------------------------------------------------------------------------------------

        // Production and growth (InvokeRepeating) of what stands in a frozen place. All count world time, caught up
        // when the place runs again. Methods missing in this game version are left out.
        [HarmonyPatch]
        static class PieceTimers
        {
            static readonly (Type type, string method)[] s_targets =
            {
                (typeof(Fireplace), "UpdateFireplace"), (typeof(Fireplace), "CheckEnv"), (typeof(Smelter), "UpdateSmelter"),
                (typeof(CookingStation), "UpdateCooking"), (typeof(Fermenter), "SlowUpdate"), (typeof(Beehive), "UpdateBees"),
                (typeof(SapCollector), "UpdateTick"), (typeof(Door), "UpdateState"), (typeof(Pickable), "UpdateRespawn"),
            };

            static bool Prepare() => Targets(s_targets).GetEnumerator().MoveNext();
            static IEnumerable<MethodBase> TargetMethods() => Targets(s_targets);

            static bool Prefix(MonoBehaviour __instance)
            {
                if (!s_active || s_frozenSectors == 0 || !InFrozenPlace(__instance))
                    return true;
                LabTimersSkipped++;
                s_statTimers++;
                return false;
            }

            // Its sector first (most timers are of the player's own surroundings), its ZDO only when that stands.
            static bool InFrozenPlace(MonoBehaviour b)
            {
                if (s_table[(int)ZoneSystem.GetSectorIndex(b.transform.position).Sector] != Frozen)
                    return false;
                return b.TryGetComponent(out ZNetView v) && FrozenNow(v);
            }
        }

        // The same for creatures and loose things (taming, breeding, growing up, idle sounds, an item's or a floating
        // thing's check under the ground, which would set a held body's velocity), also while still held.
        [HarmonyPatch]
        static class BodyTimers
        {
            static readonly (Type type, string method)[] s_targets =
            {
                (typeof(BaseAI), "DoIdleSound"), (typeof(Tameable), "TamingUpdate"), (typeof(Procreation), "Procreate"),
                (typeof(Growup), "GrowUpdate"), (typeof(EggGrow), "GrowUpdate"), (typeof(ItemDrop), "SlowUpdate"),
                (typeof(Floating), "TerrainCheck"),
            };

            static bool Prepare() => Targets(s_targets).GetEnumerator().MoveNext();
            static IEnumerable<MethodBase> TargetMethods() => Targets(s_targets);

            static bool Prefix(MonoBehaviour __instance)
            {
                if (!s_active || !__instance.TryGetComponent(out ZNetView v) || !((s_held.Count > 0 && s_held.ContainsKey(v)) || FrozenNow(v)))
                    return true;
                LabTimersSkipped++;
                s_statTimers++;
                return false;
            }
        }

        static IEnumerable<MethodBase> Targets((Type type, string method)[] targets)
        {
            foreach ((Type type, string method) in targets)
            {
                MethodInfo m = AccessTools.DeclaredMethod(type, method, Type.EmptyTypes);
                if (m != null)
                    yield return m;
            }
        }

        // ---- the rest ------------------------------------------------------------------------------------

        // Broken: everything held is let go at once and nothing stands any more.
        static void Abandon()
        {
            foreach (KeyValuePair<ZNetView, Held> kv in s_held)
                if (kv.Key)
                    LetGo(kv.Value);
            Clear();
            s_inWorld = true;
        }

        // Leaving the world: everything held went with it.
        static void Clear()
        {
            s_inWorld = false;
            FarCores.Clear();
            FarViewer.Clear();
            foreach (KeyValuePair<ZNetView, Held> kv in s_held)
            {
                kv.Value.Body = null;
                kv.Value.Anim = null;
                s_pool.Push(kv.Value);
            }
            s_held.Clear();
            s_toHold.Clear();
            s_holdQueued.Clear();
            s_toThaw.Clear();
            s_thawQueued.Clear();
            s_crossedAt.Clear();
            Unmark();
            s_tableBuilt = false;
            s_live = ZDOID.None;
            s_liveWhy = "";
            s_active = false;
            s_freeze = false;
            HookSlow(false);
            s_epoch++;
            if (s_passes != null)
                foreach (Pass p in s_passes)
                {
                    p.Kept.Clear();
                    p.Left = 0;
                    p.Epoch = -1;
                    p.Last = null;
                }
            LabFrozenSectors = 0;
            LabSlowSectors = 0;
            LabHeld = 0;
        }

        // Every 10 s with the Verbose log, while anything stands still or is held.
        static void Stats()
        {
            if (Time.unscaledTime < s_statNext)
                return;
            s_statNext = Time.unscaledTime + 10f;
            int standing = s_freeze ? s_frozenSectors : 0;
            if (Plugin.Verbose.Value && (standing > 0 || s_held.Count > 0 || s_statThaws > 0))
            {
                int left = 0, total = 0;
                foreach (Pass p in s_passes)
                {
                    left += p.Left;
                    total += p.Count;
                }
                Plugin.Log.LogInfo($"Far freeze: live area {(s_live == ZDOID.None ? "none" : s_live + " (" + s_liveWhy + ")")}, {standing} zones standing still, " +
                                   $"{s_held.Count} objects held; {left} of {total} update entries left out; in 10 s {s_statHolds} held, {s_statThaws} let go, " +
                                   $"{s_statAi} AI updates and {s_statTimers} timers skipped, slowest turn {s_statTurnWorst:0.00} ms" +
                                   (FarFlags.FreezeVerify ? $"; verify: {LabVerifyChecks} checks, {LabVerifyMismatches} lists not the game's" : ""));
            }
            if (Plugin.Verbose.Value && s_slowHooked && !s_freeze && s_frozenSectors > 0)
                Plugin.Log.LogInfo($"Far AI: live area {(s_live == ZDOID.None ? "none" : s_live + " (" + s_liveWhy + ")")}, owned creatures of {s_frozenSectors} far zones " +
                                   $"think every {Engine.AiStaggerModule.FarEvery} AI ticks; engine: {Engine.AiStaggerModule.FarTracked} tracked, " +
                                   $"{Engine.AiStaggerModule.FarSkipped} updates skipped, {Engine.AiStaggerModule.FarThinks} thinks so far");
            s_statHolds = s_statThaws = 0;
            s_statAi = s_statTimers = 0;
            s_statTurnWorst = 0.0;
        }

        sealed class ByReference : IEqualityComparer<ZNetView>
        {
            internal static readonly ByReference Instance = new ByReference();
            public bool Equals(ZNetView a, ZNetView b) => ReferenceEquals(a, b);
            public int GetHashCode(ZNetView v) => RuntimeHelpers.GetHashCode(v);
        }
    }

    // A12: the far cores whose creatures this game runs, named to the engine's navmesh cap (FarNavmeshCapModule.IsFarCore
    // and FarClusterOf; ARCHITECTURE-v2 §3.11 "AI containment"). A far area's core is the 3x3 zones round its far portal,
    // and its creatures ask for tiles well past that (paths poke the tiles 48 m past themselves and their targets,
    // random-move and flee targets lie tens of metres out), so each area answers for its core grown by two zones each way
    // (7x7 zones), one cluster per area. With Far.FreezeLists only the live one's creatures ask at all; tiles asked for
    // before it froze are still capped. The cap is the engine's switch (FarNavmeshCap, off by default in the engine,
    // on under Window.Quality=Continuation): off, nothing here is asked.
    // Docs\PARITY-PLAN.md P19: only areas that sleep are named. An awake one (watched, waking or loading:
    // RemoteAreas.CollectAwakeCores) runs its creatures as a player standing there would, and the game builds a player's
    // tiles uncapped, so its tiles rank Mid (after the player's own tiles of the same kind, never capped). Tiles its
    // creatures asked for before it fell asleep are capped then. A zone two areas share counts as awake when either is,
    // and a tile an awake area's grown core reaches is not capped for a sleeping one's that reaches it too.
    internal static class FarCores
    {
        // The portal's zone, the core's ring, and two more.
        const int Reach = 3;

        // The sleeping areas' zones as named now, and as found this frame; the same for the awake ones.
        static List<Vector2s> s_cores = new List<Vector2s>(), s_next = new List<Vector2s>();
        static List<Vector2s> s_awakeZones = new List<Vector2s>(), s_nextAwake = new List<Vector2s>();
        static readonly List<(Vector3 center, Vector2s zone, int nearRadius, bool watched)> s_awake = new List<(Vector3, Vector2s, int, bool)>();
        static bool s_known, s_hooked;
        static readonly Func<Vector3, bool> s_isFarCore = IsFarCore;
        static readonly Func<Vector3, int> s_clusterOf = ClusterOf;

        // Lab (P19): tiles named in a sleeping far core (capped), cumulative; the sleeping and awake areas now.
        internal static long LabNavFarCapped;
        internal static int LabNavAsleep, LabNavAwake;

        // Which areas sleep changes without AreasVersion (one falls asleep 3 s after its window was last seen, and wakes
        // ahead of the view), so the set is looked at every frame; the engine hears of it only when it changed.
        internal static void Update(RemoteAreas areas)
        {
            if (!s_hooked)
            {
                s_hooked = true;
                Engine.FarNavmeshCapModule.IsFarCore = s_isFarCore;
                Engine.FarNavmeshCapModule.FarClusterOf = s_clusterOf;
            }
            areas.CollectAwakeCores(s_awake);
            s_nextAwake.Clear();
            foreach (var c in s_awake)
                s_nextAwake.Add(c.zone);
            s_next.Clear();
            for (int i = 0; i < areas.Count; i++)
            {
                if (areas.AreaDraining(i))
                    continue;
                Vector2s zone = areas.AreaZone(i);
                if (!Awake(zone))
                    s_next.Add(zone);
            }
            LabNavAsleep = s_next.Count;
            LabNavAwake = s_awake.Count;
            if (s_known && Same(s_next, s_cores) && Same(s_nextAwake, s_awakeZones))
                return;
            s_known = true;
            List<Vector2s> was = s_cores;
            s_cores = s_next;
            s_next = was;
            was = s_awakeZones;
            s_awakeZones = s_nextAwake;
            s_nextAwake = was;
            Engine.FarNavmeshCapModule.HooksChanged();
        }

        static bool Awake(Vector2s zone)
        {
            foreach (var c in s_awake)
                if (c.zone == zone)
                    return true;
            return false;
        }

        static bool Same(List<Vector2s> a, List<Vector2s> b)
        {
            if (a.Count != b.Count)
                return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i] != b[i])
                    return false;
            return true;
        }

        internal static void Clear()
        {
            s_known = false;
            s_awake.Clear();
            s_awakeZones.Clear();
            LabNavAsleep = LabNavAwake = 0;
            if (s_cores.Count == 0)
                return;
            s_cores.Clear();
            Engine.FarNavmeshCapModule.HooksChanged();
        }

        static bool IsFarCore(Vector3 at)
        {
            if (!Nearest(at, out _))
                return false;
            LabNavFarCapped++;
            return true;
        }

        // The nearest core's zone, packed: the same over its whole grown area.
        static int ClusterOf(Vector3 at) => Nearest(at, out Vector2s core) ? (core.x << 16) | (core.y & 0xFFFF) : 0;

        // The nearest sleeping core that reaches the tile, unless an awake one reaches it (P19: Mid then).
        static bool Nearest(Vector3 at, out Vector2s core)
        {
            core = default;
            Vector2s zone = ZoneSystem.GetZone(at);
            foreach (Vector2s c in s_awakeZones)
                if (Math.Max(Math.Abs(zone.x - c.x), Math.Abs(zone.y - c.y)) <= Reach)
                    return false;
            int best = int.MaxValue;
            foreach (Vector2s c in s_cores)
            {
                int d = Math.Max(Math.Abs(zone.x - c.x), Math.Abs(zone.y - c.y));
                if (d <= Reach && d < best)
                {
                    best = d;
                    core = c;
                }
            }
            return best != int.MaxValue;
        }
    }
}
