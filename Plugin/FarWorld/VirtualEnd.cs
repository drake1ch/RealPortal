using UnityEngine;

namespace RealPortal
{
    // VirtualEnd (ARCHITECTURE-v2.md §3.9; FARWORLD-V3.md §7 V2, Far.VirtualEnd): the far end of an area while its portal is
    // not loaded, stood in for by its prefab's opening from FrameCache placed at the far ZDO's pose; once the far portal is
    // real (and measured), the real opening. One per FarArea.
    // V2 keeps objects real, so a window still draws only with its far portal real: the renderer takes a real
    // PortalWindow as its partner until V3 makes that an IPortalEnd. What the stand-in gives now:
    //  - the near portal's opening set up against the far one before it loads (PortalLink.UpdateClip: the overlap and the
    //    view fit, which are then not measured in the frame the window goes live);
    //  - the far end being known, and the far point P' = M_pair * player, for readiness and the area policy.
    // When the far portal comes in, its measured opening is checked once against the stand-in (LabChecked, LabMismatch:
    // more than 2 cm or half a degree apart, logged): FrameCache learns it again from that portal.
    internal sealed class VirtualEnd
    {
        internal readonly PortalFrame Frame = new PortalFrame();
        // The far end's opening is known: from the real far portal (Real), or FrameCache had its prefab.
        internal bool Known;
        internal bool Real;

        ZDOID _id = ZDOID.None;
        int _prefab, _cacheVersion = -1;
        Vector3 _pos;
        Quaternion _rot;
        bool _checked;

        // Lab: stand-ins placed (a far ZDO or its pose changed), far portals whose prefab FrameCache does not know yet,
        // stand-ins checked against the real portal as it came in, and those that did not match.
        internal static int LabPlaced, LabUnknown, LabChecked, LabMismatch;

        // Once a frame (AreaPolicy): far is the far portal's ZDO, partner its PortalLink when it is loaded (else null).
        internal void Update(ZDO far, PortalLink partner)
        {
            if (partner != null && partner.FrameSet)
            {
                PortalFrame real = partner.Frame;
                if (!_checked && Known && !Real)
                {
                    LabChecked++;
                    if (!SameOpening(Frame, real))
                    {
                        LabMismatch++;
                        if (LabMismatch <= 5)
                            Plugin.Log.LogInfo($"VirtualEnd: far portal {far?.m_uid} came in {Vector3.Distance(Frame.Center, real.Center):0.000} m and " +
                                               $"{Quaternion.Angle(Frame.Rotation, real.Rotation):0.00} deg from its stand-in (half size {Frame.HalfSize:F3} vs {real.HalfSize:F3})");
                    }
                }
                _checked = true;
                CopyOpening(real, Frame);
                Real = true;
                Known = true;
                // Placed again from FrameCache should it unload.
                _id = ZDOID.None;
                return;
            }
            _checked = false;
            Real = false;
            if (far == null || !far.IsValid())
            {
                Known = false;
                _id = ZDOID.None;
                return;
            }
            int prefab = far.GetPrefab();
            Vector3 pos = far.GetPosition();
            Quaternion rot = far.GetRotation();
            if (far.m_uid == _id && prefab == _prefab && pos == _pos && rot == _rot && FrameCache.Version == _cacheVersion)
                return;
            _id = far.m_uid;
            _prefab = prefab;
            _pos = pos;
            _rot = rot;
            _cacheVersion = FrameCache.Version;
            Known = FrameCache.Place(prefab, pos, rot, Frame);
            if (Known)
                LabPlaced++;
            else
                LabUnknown++;
        }

        // Back to nothing (Far.VirtualEnd off, the area dropped).
        internal void Reset()
        {
            Known = false;
            Real = false;
            _checked = false;
            _id = ZDOID.None;
            _cacheVersion = -1;
        }

        // The same opening within 2 cm and half a degree (form and size included): the near portal's overlap and view fit
        // against one hold for the other.
        internal static bool SameOpening(PortalFrame a, PortalFrame b)
        {
            const float metres = 0.02f, degrees = 0.5f;
            if (a == null || b == null || a.Round != b.Round || (a.Shape == null) != (b.Shape == null))
                return false;
            if ((a.Center - b.Center).sqrMagnitude > metres * metres || (a.HalfSize - b.HalfSize).sqrMagnitude > metres * metres
                || Mathf.Abs(a.BaseDrop - b.BaseDrop) > metres || Quaternion.Angle(a.Rotation, b.Rotation) > degrees)
                return false;
            if (a.Shape == null || ReferenceEquals(a.Shape, b.Shape))
                return true;
            if (a.Shape.Length != b.Shape.Length)
                return false;
            for (int i = 0; i < a.Shape.Length; i++)
                if ((a.Shape[i] - b.Shape[i]).sqrMagnitude > 0.01f * 0.01f)
                    return false;
            return true;
        }

        // The opening only (what PortalLink.UpdateFrame sets); Clip and the view fit are each near portal's own.
        internal static void CopyOpening(PortalFrame from, PortalFrame to)
        {
            to.Center = from.Center;
            to.Rotation = from.Rotation;
            to.HalfSize = from.HalfSize;
            to.Round = from.Round;
            to.Shape = from.Shape;
            to.BaseDrop = from.BaseDrop;
        }

        internal static string Describe() =>
            $"virtual ends: {LabPlaced} placed, {LabUnknown} unknown prefab, {LabChecked} checked as their portal came in, {LabMismatch} mismatched";
    }
}
