using System.Collections.Generic;
using UnityEngine;

namespace RealPortal
{
    // Physics queries that go on through live portals, for the game's own code (swapped in for its Physics calls).
    // A ray that enters an opening is cast on from the matching point of the exit; what it meets over there comes
    // back where it shows from here ("apparent": carried back through the pair), with the distance counted along
    // the way. The game's own checks (how far, what is nearest) then just work. Which colliders were met beyond a
    // portal this frame is remembered, so a hit or a placement can be carried to where the thing really is.
    internal static class PortalPhysics
    {
        static readonly RaycastHit[] s_far = new RaycastHit[64];
        static readonly Dictionary<int, Matrix4x4> s_beyond = new Dictionary<int, Matrix4x4>();
        static int s_beyondFrame = -1;

        // Lab: the last ray that went through a portal.
        internal static string LabLast = "";

        // The first live opening the segment from -> from + dir * max goes through: the link, and how far along.
        internal static bool Crossing(Vector3 from, Vector3 dir, float max, out PortalLink link, out Vector3 hit, out float at)
        {
            link = null;
            hit = default;
            at = 0f;
            PortalSystem sys = PortalSystem.Instance;
            if (sys == null || !Plugin.Reach.Value)
                return false;
            Vector3 to = from + dir * max;
            float best = float.MaxValue;
            foreach (PortalLink l in sys.Active)
                if (l.Frame.Crosses(from, to, out Vector3 h, out float t) && t < best)
                {
                    best = t;
                    hit = h;
                    link = l;
                }
            if (link == null)
                return false;
            at = best * max;
            return true;
        }

        // In place of Physics.RaycastNonAlloc(origin, direction, results, maxDistance, layerMask).
        internal static int RaycastNonAlloc(Vector3 origin, Vector3 direction, RaycastHit[] results, float maxDistance, int layerMask)
        {
            int n = Physics.RaycastNonAlloc(origin, direction, results, maxDistance, layerMask);
            if (!Crossing(origin, direction.normalized, maxDistance, out PortalLink link, out Vector3 entry, out float before))
                return n;
            // What lies behind the opening on this side is not what you see there.
            int kept = 0;
            for (int i = 0; i < n; i++)
                if (results[i].distance <= before)
                    results[kept++] = results[i];
            n = kept;
            Matrix4x4 m = PortalMath.Pair(link.Frame, link.Partner.Frame), back = m.inverse;
            Quaternion turnBack = Quaternion.Inverse(PortalMath.Rotation(m));
            Vector3 farDir = PortalMath.Rotation(m) * direction.normalized;
            Vector3 farFrom = m.MultiplyPoint3x4(entry) + farDir * 0.02f;
            int k = Physics.RaycastNonAlloc(farFrom, farDir, s_far, maxDistance - before, layerMask);
            for (int i = 0; i < k && n < results.Length; i++)
            {
                RaycastHit h = s_far[i];
                if (h.collider == null || IsPortalOf(h.collider, link.Partner))
                    continue;
                h.point = back.MultiplyPoint3x4(h.point);
                h.normal = turnBack * h.normal;
                h.distance += before + 0.02f;
                results[n++] = h;
                Remember(h.collider, m);
            }
            LabLast = $"{n} hits, first {(n > 0 ? results[0].collider.name + " " + results[0].distance.ToString("0.0") : "-")}, array {results.Length}";
            return n;
        }

        static bool IsPortalOf(Collider c, PortalLink link) => c == link.Wall || c.transform.IsChildOf(link.transform);

        static void Remember(Collider c, Matrix4x4 toWorld)
        {
            if (s_beyondFrame != Time.frameCount)
            {
                s_beyond.Clear();
                s_beyondFrame = Time.frameCount;
            }
            s_beyond[c.GetInstanceID()] = toWorld;
        }

        // A collider met this frame beyond a portal: apparent -> its own world.
        internal static bool Beyond(Collider c, out Matrix4x4 toWorld)
        {
            toWorld = Matrix4x4.identity;
            return c != null && s_beyondFrame == Time.frameCount && s_beyond.TryGetValue(c.GetInstanceID(), out toWorld);
        }

        // The shorter of the straight way and the way through a live portal near `from` (12 m), as the game would
        // measure reach (a chest left open, a workbench in use).
        internal static float Distance(Vector3 from, Vector3 to)
        {
            float d = Vector3.Distance(from, to);
            PortalSystem sys = PortalSystem.Instance;
            if (sys == null || !Plugin.Reach.Value)
                return d;
            foreach (PortalLink l in sys.Active)
            {
                if ((l.Frame.Center - from).sqrMagnitude > 12f * 12f)
                    continue;
                // To the opening here, then from the exit to it there.
                float via = Vector3.Distance(from, l.Frame.Center) + Vector3.Distance(l.Partner.Frame.Center, to);
                if (via < d)
                    d = via;
            }
            return d;
        }
    }
}
