using System;
using System.Text;
using UnityEngine;

namespace RealPortal
{
    // AreaPolicy (ARCHITECTURE-v2.md §3.4; FARWORLD-V3.md §7 V2), with legacy semantics for now: RemoteAreas still decides
    // which far areas load (its Select: every real link within PreloadDistance, nearest first, MaxFarAreas, UnloadDelay)
    // and every loaded one is real, so the policy follows the same rule and names the result instead of choosing it.
    // Once a frame, after the links' Refresh (PortalSystem.Update, FarWorld.UpdateAreas), O(links + areas):
    //  - every real link with a partner ZDO is put under the FarArea of its far portal (made at the preload distance,
    //    kept while a link of it is live or it was wanted within UnloadDelay): near portals into one far portal share it;
    //  - each area's pose, own-area flag (the far portal inside the player's active area: the game's, Far.NoDupAreas),
    //    RemoteAreas stage and tier (Cold, Data while it loads, RealCore/LiveCore once it stands or when it is the
    //    player's own area) and the §3.4 score (crossed, hot, watched, visible, distance; aiming is not promoted in V2);
    //    above 64 areas these go 16 a frame;
    //  - its VirtualEnd (Far.VirtualEnd) and its readiness (Far.Readiness3);
    //  - the AreaContexts again when areas came, went or changed zone; FrameCache written when due.
    // Nothing here writes a ZDO or takes ownership; the only per-frame cost with nothing changing is the loops.
    internal static class AreaPolicy
    {
        const int SliceAbove = 64, SlicePerFrame = 16;
        // §3.4: hot within this, or closing in to arrive within HotSeconds while within HotReach.
        const float HotDistance = 30f, HotSeconds = 3f, HotReach = 60f;
        const float CrossedHot = 10f;

        static int s_slice;
        static bool s_broken;
        static int s_brokenKey;
        static int s_errors;
        static float s_errorAt;

        // Lab: frames run, errors.
        internal static int LabFrames, LabErrors;

        static int SwitchKey => (FarFlags.V2 ? 1 : 0) | (FarFlags.Readiness3 ? 2 : 0) | (FarFlags.VirtualEnd ? 4 : 0);

        internal static void Update(PortalSystem sys)
        {
            if (s_broken)
            {
                // After an error: off until a switch is set again (then the links fall back to IsActive meanwhile).
                if (SwitchKey == s_brokenKey)
                    return;
                s_broken = false;
            }
            ZNet net = ZNet.instance;
            if (!FarWorld.On || net == null || ZNetScene.instance == null || ZoneSystem.instance == null || ZDOMan.instance == null || sys == null)
            {
                if (FarAreas.Count > 0)
                    FarAreas.Clear();
                return;
            }
            LabFrames++;
            float now = Time.time;
            Player p = Player.m_localPlayer;
            Vector3 refPos = net.GetReferencePosition();
            Vector3 at = p != null ? p.transform.position : refPos;
            float preload = Plugin.PreloadDistance.Value, margin = Plugin.UnloadMargin.Value;
            var all = FarAreas.All;
            for (int i = 0; i < all.Count; i++)
            {
                FarArea a = all[i];
                a.Links.Clear();
                a.Nearest = null;
                a.Distance = float.MaxValue;
                a.AnyLive = false;
            }
            var links = PortalLink.All;
            for (int i = 0; i < links.Count; i++)
            {
                PortalLink link = links[i];
                ZDO far = link.IsReal ? link.PartnerZdo : null;
                if (far == null || !far.IsValid())
                {
                    link.FarArea = null;
                    continue;
                }
                float d = Utils.DistanceXZ(at, link.transform.position);
                FarArea a = FarAreas.Get(far.m_uid);
                bool live = link.IsActive;
                // As RemoteAreas.Select: within the preload distance, and the unload margin beyond it once there is an area.
                bool want = d <= preload + (a != null ? margin : 0f);
                if (a == null)
                {
                    if (!want && !live)
                    {
                        link.FarArea = null;
                        continue;
                    }
                    a = FarAreas.Make(far, now);
                }
                a.Zdo = far;
                a.Links.Add(link);
                link.FarArea = a;
                a.AnyLive |= live;
                if (want)
                    a.WantedAt = now;
                if (d < a.Distance)
                {
                    a.Distance = d;
                    a.Nearest = link;
                }
            }
            bool slice = all.Count > SliceAbove;
            int from = slice ? s_slice % all.Count : 0, count = slice ? SlicePerFrame : all.Count;
            if (slice)
                s_slice = (from + SlicePerFrame) % all.Count;
            bool vend = FarWorld.VirtualEndOn;
            float unload = Plugin.UnloadDelay.Value;
            for (int i = all.Count - 1; i >= 0; i--)
            {
                FarArea a = all[i];
                if (!a.AnyLive && now - a.WantedAt > unload)
                {
                    FarAreas.Drop(i);
                    continue;
                }
                // Its far portal's ZDO gone (destroyed, or the pooled object handed out under another id): no link leads
                // here any more; it closes and goes after the unload delay.
                bool valid = a.Zdo != null && a.Zdo.IsValid() && a.Zdo.m_uid == a.Portal;
                if (!valid)
                {
                    a.Zdo = null;
                    a.AnyLive = false;
                }
                int k = i - from;
                if (k < 0)
                    k += all.Count;
                if (valid && (!slice || k < count))
                    Refresh(a, sys.Areas, refPos, now);
                if (vend)
                    a.End.Update(a.Zdo, valid ? a.Partner : null);
                else if (a.End.Known)
                    a.End.Reset();
                Readiness.Update(a, sys.Areas, now);
            }
            if (FarAreas.ContextsDirty)
            {
                FarAreas.ContextsDirty = false;
                AreaContext.Rebuild(all);
            }
            if (vend)
                FrameCache.Tick();
        }

        static void Refresh(FarArea a, RemoteAreas areas, Vector3 refPos, float now)
        {
            ZDO far = a.Zdo;
            Vector3 c = far.GetPosition();
            if (c != a.Center)
            {
                a.Center = c;
                Vector2s zone = ZoneSystem.GetZone(c);
                if (zone.x != a.Zone.x || zone.y != a.Zone.y)
                {
                    a.Zone = zone;
                    FarAreas.ContextsDirty = true;
                }
            }
            a.OwnArea = ZNetScene.InActiveArea(c, refPos);
            a.Stage = areas != null ? areas.Stage(far) : 0;
            // Legacy: what RemoteAreas holds is real from the moment its core stands (simulated with LiveFarSide's claims);
            // the player's own area is the game's, real and simulated; what is wanted but not standing yet is data on its way.
            if (a.Stage >= 2)
                a.Tier = Plugin.LiveFarSide.Value ? AreaTier.LiveCore : AreaTier.RealCore;
            else if (a.OwnArea)
                a.Tier = AreaTier.LiveCore;
            else
                a.Tier = now - a.WantedAt < 0.5f || a.Stage == 1 ? AreaTier.Data : AreaTier.Cold;
            // §3.4's score, kept for the lab and for when the policy chooses (V3): crossed within 10 s, hot (close, or closing
            // in to arrive within 3 s), watched (a link's window seen lately or the player by it), visible, distance.
            float d = a.Distance < float.MaxValue ? a.Distance : Plugin.PreloadDistance.Value + Plugin.UnloadMargin.Value;
            if (a.ScoredAt >= 0f && now > a.ScoredAt && a.ScoredDistance >= 0f)
                a.Closing = Mathf.Lerp(a.Closing, (a.ScoredDistance - d) / (now - a.ScoredAt), 0.3f);
            a.ScoredAt = now;
            a.ScoredDistance = d;
            bool watched = false, visible = false;
            for (int i = 0; i < a.Links.Count; i++)
            {
                PortalLink link = a.Links[i];
                if (link == null)
                    continue;
                watched |= link.Watched;
                visible |= link.Window != null && now - link.Window.LastRendered < 0.5f;
            }
            float tta = d / Mathf.Max(a.Closing, 0.1f);
            bool hot = d < HotDistance || (tta < HotSeconds && d < HotReach);
            a.Score = (now - a.CrossedAt < CrossedHot ? 4f : 0f) + (hot ? 3f : 0f) + (watched ? 2f : 0f) + (visible ? 1f : 0f) - d / 100f;
        }

        // Something went through `from` (PortalSystem): its area is hot. For the player also the link back from where it
        // now stands (the exit, whose far portal is `from`): walkable at once (Readiness.Crossed).
        internal static void Crossed(PortalLink from, bool player)
        {
            if (!FarWorld.On || from == null || s_broken)
                return;
            float now = Time.time;
            FarArea a = FarAreas.Of(from);
            if (a != null)
                a.CrossedAt = now;
            PortalLink exit = from.Partner;
            if (!player || exit == null || exit.Partner != from)
                return;
            ZDO back = exit.PartnerZdo;
            if (back == null || !back.IsValid())
                return;
            FarArea b = FarAreas.Get(back.m_uid) ?? FarAreas.Make(back, now);
            b.CrossedAt = now;
            b.WantedAt = now;
            if (!b.Links.Contains(exit))
                b.Links.Add(exit);
            exit.FarArea = b;
            b.AnyLive |= exit.IsActive;
            if (b.AnyLive)
                Readiness.Crossed(b, now);
        }

        // An exception in the policy's frame: logged (in full at most every 10 s), the areas dropped (every link back on its
        // own IsActive) and the policy off until a switch is set again.
        internal static void Failed(Exception e)
        {
            s_errors++;
            LabErrors++;
            if (Time.unscaledTime >= s_errorAt)
            {
                s_errorAt = Time.unscaledTime + 10f;
                Plugin.Log.LogError($"AreaPolicy: error {s_errors}, far areas off until a Far.* switch is set again: {e}");
            }
            s_broken = true;
            s_brokenKey = SwitchKey;
            try
            {
                FarAreas.Clear();
            }
            catch (Exception e2)
            {
                Plugin.Log.LogError("AreaPolicy: dropping the areas: " + e2);
            }
        }

        // Lab: dedup, contexts, tiers and readiness in one line (detail: every area on a line of its own).
        internal static string Describe(bool detail = false)
        {
            int shared = 0, own = 0, live = 0, show = 0, thr = 0, walk = 0;
            int[] tiers = new int[5];
            foreach (FarArea a in FarAreas.All)
            {
                if (a.Links.Count > 1)
                    shared++;
                if (a.OwnArea)
                    own++;
                if (a.AnyLive)
                    live++;
                tiers[(int)a.Tier]++;
                if (a.Ready == ReadyLevel.Show)
                    show++;
                else if (a.Ready == ReadyLevel.Throw)
                    thr++;
                else if (a.Ready == ReadyLevel.Walk)
                    walk++;
            }
            var sb = new StringBuilder();
            sb.Append($"far areas: {FarAreas.Count} ({shared} shared by several links, {own} in the player's area, {live} live; made {FarAreas.LabMade}, dropped {FarAreas.LabDropped}), " +
                      $"contexts {AreaContext.Count} (largest {AreaContext.LabLargest}, {AreaContext.LabRebuilds} regroupings), " +
                      $"tiers cold {tiers[0]} data {tiers[1]} visual {tiers[2]} real {tiers[3]} live {tiers[4]}; ready: show {show}, throw {thr}, walk {walk}" +
                      (s_broken ? $"; stopped after {s_errors} error(s)" : "") + "; " + Readiness.Describe() + "; " + VirtualEnd.Describe() + "; " + FrameCache.Describe());
            if (detail)
            {
                foreach (FarArea a in FarAreas.All)
                    sb.Append("\n  ").Append(a.Describe());
                foreach (AreaContext c in AreaContext.All)
                    sb.Append("\n  ").Append(c.Describe());
            }
            return sb.ToString();
        }
    }

    // FarWorld's entry points for areas and readiness (the facade is partial; FarWorld.cs).
    internal static partial class FarWorld
    {
        // Once a frame from PortalSystem.Update, after the links' Refresh and before anything crosses.
        internal static void UpdateAreas(PortalSystem sys)
        {
            if (!FarFlags.V2 && FarAreas.Count == 0)
                return;
            try
            {
                AreaPolicy.Update(sys);
            }
            catch (Exception e)
            {
                AreaPolicy.Failed(e);
            }
        }

        // How far a link's far side is ready (Readiness.Level; as before, Walk with IsActive, while Far.Readiness3 is off).
        internal static ReadyLevel ReadinessOf(PortalLink link) => Readiness.Level(link);

        // The player (player true) or anything else went through `from` (PortalSystem.MovePlayer and the other crossings).
        internal static void OnCrossed(PortalLink from, bool player)
        {
            try
            {
                AreaPolicy.Crossed(from, player);
            }
            catch (Exception e)
            {
                AreaPolicy.Failed(e);
            }
        }

        // The scene went (PortalSystem.Clear): every area dropped, the frame cache written if it changed.
        internal static void ClearAreas()
        {
            FarAreas.Clear();
            FrameCache.Flush();
        }

        internal static string DescribeAreas(bool detail = false) => AreaPolicy.Describe(detail);
    }
}
