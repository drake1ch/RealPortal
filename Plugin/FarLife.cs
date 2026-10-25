using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RealPortal
{
    // What a creature sees, hears and reaches through a real portal. While a creature by a live portal thinks (its AI
    // update) or swings (a melee attack, yours too), the characters by the partner portal stand where it would see them
    // through the opening, and are put back right after. The game's own AI then finds them, walks to them (into the
    // opening, and through), looks at them and hits them as if they were here; sight counts only through the opening.
    internal static class FarLife
    {
        const float Reach = 60f;

        static readonly List<(Character c, Vector3 pos, Quaternion rot)> s_moved = new List<(Character, Vector3, Quaternion)>();
        static readonly Dictionary<Character, PortalLink> s_through = new Dictionary<Character, PortalLink>();
        static int s_depth;

        // Lab: characters seen through a portal so far, and sight refused because it missed the opening.
        internal static int LabMoved, LabMissedOpening;

        // Far.Readiness3 (FARWORLD V2; ARCHITECTURE-v2.md §3.13, the crossing matrix): only a far side that takes creatures
        // and thrown things (CanThrow: its core real) is seen, heard and reached into. And the characters by each such live
        // link's far portal are listed once a physics step (and once a frame), in the game's order, instead of every
        // character being looked at for every link in each AI update and swing (O(links x characters) each time). The
        // lists reach ListMargin past Reach, so what moves within a step is still found, and anything carried through a
        // portal has them listed again at once (Moved). Off: as before.
        const float ListMargin = 10f;
        static readonly Dictionary<PortalLink, List<Character>> s_byLink = new Dictionary<PortalLink, List<Character>>();
        static readonly List<List<Character>> s_listPool = new List<List<Character>>();
        static int s_listFrame = -1;
        static float s_listFixed = -1f;

        // Lab: listings made, and characters listed in them.
        internal static int LabListings;
        internal static long LabListed;

        // Something was carried through a portal (PortalSystem): the next AI update or swing lists again.
        internal static void Moved() => s_listFrame = -1;

        static void ListByLink(IReadOnlyList<PortalLink> live)
        {
            int frame = Time.frameCount;
            float step = Time.fixedTime;
            if (frame == s_listFrame && step == s_listFixed)
                return;
            s_listFrame = frame;
            s_listFixed = step;
            foreach (KeyValuePair<PortalLink, List<Character>> kv in s_byLink)
            {
                kv.Value.Clear();
                s_listPool.Add(kv.Value);
            }
            s_byLink.Clear();
            List<Character> all = Character.GetAllCharacters();
            float r2 = (Reach + ListMargin) * (Reach + ListMargin);
            for (int i = 0; i < live.Count; i++)
            {
                PortalLink link = live[i];
                if (link == null || link.Partner == null || s_byLink.ContainsKey(link) || !Readiness.CanThrow(link))
                    continue;
                Vector3 far = link.Partner.Frame.Center;
                List<Character> list;
                if (s_listPool.Count > 0)
                {
                    list = s_listPool[s_listPool.Count - 1];
                    s_listPool.RemoveAt(s_listPool.Count - 1);
                }
                else
                    list = new List<Character>();
                for (int k = 0; k < all.Count; k++)
                {
                    Character c = all[k];
                    if (c != null && (c.transform.position - far).sqrMagnitude <= r2)
                        list.Add(c);
                }
                s_byLink[link] = list;
                LabListed += list.Count;
            }
            LabListings++;
        }

        // attack: for a melee swing (else for an AI's update, which needs sight or hearing through portals).
        internal static void Begin(Vector3 here, float reach, bool attack)
        {
            if (s_depth++ > 0)
                return;
            PortalSystem sys = PortalSystem.Instance;
            bool wanted = attack ? Plugin.MeleeThrough.Value
                : Plugin.CreatureSight.Value || Plugin.CreatureHearing.Value;
            if (sys == null || !wanted)
                return;
            IReadOnlyList<PortalLink> live = sys.Active;
            if (live.Count == 0)
                return;
            bool lists = FarWorld.Readiness3On;
            if (lists)
                ListByLink(live);
            float r2 = reach * reach;
            List<Character> all = null;
            foreach (PortalLink link in live)
            {
                if (link == null || link.Partner == null || (link.Frame.Center - here).sqrMagnitude > r2)
                    continue;
                // Far.FreezeLists: only the live far area is reached into; what stands still elsewhere neither sees
                // nor is seen.
                if (FarFreeze.FrozenAt(link.Partner.Frame.Center))
                    continue;
                // Far.Readiness3: the characters listed by this link's far portal; none when it does not take creatures yet.
                if (lists)
                {
                    if (!s_byLink.TryGetValue(link, out all))
                        continue;
                }
                else
                    all = all ?? Character.GetAllCharacters();
                Vector3 far = link.Partner.Frame.Center;
                Matrix4x4 m = PortalMath.Pair(link.Partner.Frame, link.Frame);
                Quaternion turn = PortalMath.Rotation(m);
                bool front = link.Frame.SignedDistance(here) > 0f;
                foreach (Character c in all)
                {
                    if (c == null || s_through.ContainsKey(c))
                        continue;
                    Vector3 at = c.transform.position;
                    // Over there, by the partner, and not here as well (pairs close together).
                    if ((at - far).sqrMagnitude > r2 || (at - here).sqrMagnitude < (at - far).sqrMagnitude)
                        continue;
                    // Only past the opening as seen from here: from behind the frame there is nothing to see through.
                    Vector3 to = m.MultiplyPoint3x4(at);
                    if (link.Frame.SignedDistance(to) > 0f == front)
                        continue;
                    s_moved.Add((c, at, c.transform.rotation));
                    s_through[c] = link;
                    c.transform.SetPositionAndRotation(to, turn * c.transform.rotation);
                    LabMoved++;
                }
            }
            if (s_moved.Count > 0)
                Physics.SyncTransforms();
        }

        internal static void End()
        {
            if (--s_depth > 0)
                return;
            s_depth = 0;
            if (s_moved.Count == 0)
                return;
            for (int i = s_moved.Count - 1; i >= 0; i--)
                if (s_moved[i].c != null)
                    s_moved[i].c.transform.SetPositionAndRotation(s_moved[i].pos, s_moved[i].rot);
            s_moved.Clear();
            s_through.Clear();
            Physics.SyncTransforms();
        }

        // From the BaseAI.CanSeeTarget prefix: a character seen through a portal is seen only through its opening
        // (and only while creatures see through portals).
        internal static bool SeenThroughOpening(Vector3 eye, Character target)
        {
            if (!s_through.TryGetValue(target, out PortalLink link))
                return true;
            if (!Plugin.CreatureSight.Value)
                return false;
            Vector3 to = target.IsCrouching() ? target.GetCenterPoint() : target.m_eye.position;
            if (link.Frame.Crosses(eye, to, out _, out _))
                return true;
            LabMissedOpening++;
            return false;
        }

        // Whether this creature may walk through: friendly ones (tamed, summoned: the players' faction) by their own
        // switch, which Path of Valheiman may decide; wild ones by the config.
        internal static bool MayCross(Character c)
        {
            bool friendly = c.IsTamed() || c.m_faction == Character.Faction.Players;
            return friendly ? Api.Mechanics.IsOn(Api.Mechanics.FriendlyCross) : Plugin.WildCrossing.Value;
        }

        static readonly AccessTools.FieldRef<BaseAI, Character> s_aiCharacter = AccessTools.FieldRefAccess<BaseAI, Character>("m_character");

        // From the BaseAI.MoveTo prefix: a creature going for someone it finds through a portal (a target, the one it
        // follows) goes by the opening, not round the frame to the empty spot behind it. In front of the opening it
        // walks straight through, if it may; else it stays there.
        static bool MoveToBefore(BaseAI __instance, ref Vector3 point, bool run, ref bool __result)
        {
            if (s_through.Count == 0)
                return true;
            PortalLink link = null;
            foreach (KeyValuePair<Character, PortalLink> kv in s_through)
                if (kv.Key != null && (kv.Key.transform.position - point).sqrMagnitude < 4f * 4f)
                {
                    link = kv.Value;
                    break;
                }
            if (link == null)
                return true;
            PortalFrame frame = link.Frame;
            Vector3 at = __instance.transform.position;
            float side = frame.SignedDistance(at) > 0f ? 1f : -1f;
            if (frame.SignedDistance(point) * side > 0f)
                return true;
            Vector3 local = frame.WorldToLocal.MultiplyPoint3x4(at);
            Character self = s_aiCharacter(__instance);
            if (Mathf.Abs(local.x) < frame.HalfSize.x * 0.6f && Mathf.Abs(local.z) < 2.5f)
            {
                if (self == null || !MayCross(self))
                {
                    __instance.StopMoving();
                    __result = true;
                    return false;
                }
                Vector3 through = frame.Center - frame.Forward * side * 2f - at;
                through.y = 0f;
                __instance.MoveTowards(through.normalized, run);
                __result = false;
                return false;
            }
            Vector3 before = frame.Center + frame.Forward * side * 1.5f;
            before.y = at.y;
            point = before;
            return true;
        }

        // ---- patches -----------------------------------------------------------------------------------

        // Each AI's update (UpdateAI is overridden by MonsterAI and AnimalAI, which call the base one): the outermost
        // call puts the far side in reach.
        internal static void Install(Harmony h)
        {
            var pre = new HarmonyMethod(typeof(FarLife), nameof(UpdateAIBefore));
            var fin = new HarmonyMethod(typeof(FarLife), nameof(UpdateAIAfter));
            foreach (Type t in typeof(BaseAI).Assembly.GetTypes())
            {
                if (!typeof(BaseAI).IsAssignableFrom(t))
                    continue;
                MethodInfo m = t.GetMethod("UpdateAI", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, new[] { typeof(float) }, null);
                if (m != null && !m.IsAbstract)
                    h.Patch(m, prefix: pre, finalizer: fin);
            }
            h.Patch(AccessTools.Method(typeof(Attack), "DoMeleeAttack"), prefix: new HarmonyMethod(typeof(FarLife), nameof(AttackBefore)),
                    finalizer: new HarmonyMethod(typeof(FarLife), nameof(AttackAfter)));
            h.Patch(AccessTools.Method(typeof(Attack), "DoAreaAttack"), prefix: new HarmonyMethod(typeof(FarLife), nameof(AttackBefore)),
                    finalizer: new HarmonyMethod(typeof(FarLife), nameof(AttackAfter)));
            h.Patch(AccessTools.Method(typeof(BaseAI), nameof(BaseAI.CanSeeTarget), new[] { typeof(Transform), typeof(Vector3), typeof(float), typeof(float), typeof(bool), typeof(bool), typeof(Character) }),
                    prefix: new HarmonyMethod(typeof(FarLife), nameof(CanSeeTargetBefore)));
            h.Patch(AccessTools.Method(typeof(BaseAI), nameof(BaseAI.CanHearTarget), new[] { typeof(Transform), typeof(float), typeof(Character) }),
                    prefix: new HarmonyMethod(typeof(FarLife), nameof(CanHearTargetBefore)));
            h.Patch(AccessTools.Method(typeof(BaseAI), "MoveTo", new[] { typeof(float), typeof(Vector3), typeof(float), typeof(bool) }),
                    prefix: new HarmonyMethod(typeof(FarLife), nameof(MoveToBefore)));
        }

        // Far.FreezeLists: a creature of a far area that stands still does not think (FarFreeze); its update is skipped
        // here rather than taken out of MonoUpdaters' AI list, where a patch would make the engine's AiStagger stand
        // down. __state: whether Begin ran, so End matches it.
        static bool UpdateAIBefore(BaseAI __instance, out bool __state)
        {
            __state = !FarFreeze.Holds(__instance);
            if (__state)
                Begin(__instance.transform.position, Reach, false);
            return __state;
        }

        static Exception UpdateAIAfter(Exception __exception, bool __state)
        {
            if (__state)
                End();
            return __exception;
        }

        static readonly AccessTools.FieldRef<Attack, Humanoid> s_attacker = AccessTools.FieldRefAccess<Attack, Humanoid>("m_character");

        static void AttackBefore(Attack __instance)
        {
            Humanoid who = s_attacker(__instance);
            Begin(who != null ? who.transform.position : Vector3.one * 1e9f, 12f, true);
        }

        static Exception AttackAfter(Exception __exception)
        {
            End();
            return __exception;
        }

        // A character over there is heard through the portal only while creatures hear through portals.
        static bool CanHearTargetBefore(Character target, ref bool __result)
        {
            if (target == null || !s_through.ContainsKey(target) || Plugin.CreatureHearing.Value)
                return true;
            __result = false;
            return false;
        }

        static bool CanSeeTargetBefore(Vector3 eyePoint, Character target, ref bool __result)
        {
            if (target == null || SeenThroughOpening(eyePoint, target))
                return true;
            __result = false;
            return false;
        }
    }
}
