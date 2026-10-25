using UnityEngine;

namespace RealPortal
{
    // The opening of one portal: a flat ellipse or rectangle in the local XY plane, facing local +Z.
    // The same plane is used for crossing tests, the window mesh and the oblique clip plane.
    public sealed class PortalFrame
    {
        public Vector3 Center;
        public Quaternion Rotation = Quaternion.identity;
        public Vector2 HalfSize = Vector2.one;
        public bool Round = true;
        // An opening of any other form (a stone arch): its outline around the centre, in units of HalfSize, one point
        // per equal angle step counter-clockwise from +X. Where set it is used instead of Round.
        public Vector2[] Shape;
        // How high the centre stands above the portal's foot (the ground it stands on). A pair is matched foot to
        // foot, so openings of different sizes (the stone arch and the wooden ring) meet at the ground.
        public float BaseDrop;
        // Where this opening and its partner's (carried through the pair) overlap, when the partner's is smaller
        // somewhere: the largest radius (local metres) at equal angle steps counter-clockwise from +X. Outside it
        // nothing goes through (a wall stands there). Null: the partner's opening covers all of this one.
        public float[] Clip;
        // When the partner's opening is smaller, the view through this one is scaled (ViewFit < 1) and lifted
        // (ViewLift: added to the partner-local height, centre to centre) so that all of this opening shows what lies
        // inside the partner's and above its floor; see PortalMath.View.
        public float ViewFit = 1f, ViewLift;

        // The scale and lift (see ViewFit) that fit this opening into `other`'s and above its floor: the largest
        // scale for which some lift keeps every point of this outline there, with the lowest such lift (the floor
        // of the one meeting the floor of the other where it can).
        public static void FitView(PortalFrame mine, PortalFrame other, out float fit, out float lift)
        {
            const int n = 64;
            var outline = new Vector2[n];
            float reach = Mathf.Max(mine.HalfSize.x, mine.HalfSize.y) * 1.5f + 0.1f;
            for (int i = 0; i < n; i++)
            {
                float a = Mathf.PI * 2f * i / n;
                Vector2 d = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                outline[i] = d * Edge(t => mine.InOpening(d * t), reach);
            }
            // What lies under this side's own floor is under the ground here as well (the wooden ring's foot): it
            // need not fit anywhere.
            int kept = 0;
            for (int i = 0; i < n; i++)
                if (outline[i].y >= -mine.BaseDrop)
                    outline[kept++] = outline[i];
            System.Array.Resize(ref outline, kept);
            float floor = -other.BaseDrop - 0.02f;
            float rigid = mine.BaseDrop - other.BaseDrop;
            fit = 1f;
            lift = rigid;
            if (FitsAt(outline, other, floor, 1f, rigid) || Fits(outline, other, floor, 1f, out _))
                return;
            float lo = 0.2f, hi = 1f, h = rigid;
            if (!Fits(outline, other, floor, lo, out h))
                return;
            for (int k = 0; k < 16; k++)
            {
                float mid = (lo + hi) * 0.5f;
                if (Fits(outline, other, floor, mid, out float hm))
                {
                    lo = mid;
                    h = hm;
                }
                else
                    hi = mid;
            }
            // A little smaller: the partner's frame is thick, and seen at an angle its inner side would show at the edge.
            fit = lo * 0.94f;
            lift = Fits(outline, other, floor, fit, out float hf) ? hf : h;
        }

        // Some lift (the lowest, in 1 cm steps) keeps every outline point, scaled, inside `other` and above its floor.
        static bool Fits(Vector2[] outline, PortalFrame other, float floor, float s, out float lift)
        {
            float span = Mathf.Max(other.HalfSize.x, other.HalfSize.y) * 2f + 1f;
            int steps = Mathf.CeilToInt(span * 2f / 0.01f);
            for (int k = 0; k <= steps; k++)
            {
                lift = -span + k * 0.01f;
                if (FitsAt(outline, other, floor, s, lift))
                    return true;
            }
            lift = 0f;
            return false;
        }

        // The outline points sit right on this opening's edge: 1 cm of slack, or the same opening would not fit
        // into itself.
        static bool FitsAt(Vector2[] outline, PortalFrame other, float floor, float s, float lift)
        {
            foreach (Vector2 q in outline)
            {
                // Through the pair the partner sees it mirrored left to right.
                var p = new Vector2(-q.x * s, q.y * s + lift);
                if (p.y < floor || !other.InOpening(p, 0.01f))
                    return false;
            }
            return true;
        }

        public Vector3 Forward => Rotation * Vector3.forward;
        public Matrix4x4 LocalToWorld => Matrix4x4.TRS(Center, Rotation, Vector3.one);
        public Matrix4x4 WorldToLocal => Matrix4x4.TRS(Center, Rotation, Vector3.one).inverse;
        public Vector3 Foot => Center + Rotation * new Vector3(0f, -BaseDrop, 0f);
        public Matrix4x4 FootToWorld => Matrix4x4.TRS(Foot, Rotation, Vector3.one);
        public Matrix4x4 WorldToFoot => FootToWorld.inverse;

        public float SignedDistance(Vector3 world) => Vector3.Dot(Forward, world - Center);

        public bool ContainsLocal(Vector2 p, float margin = 0f)
        {
            if (Clip != null && p.magnitude > ClipRadius(Mathf.Atan2(p.y, p.x)) + margin)
                return false;
            return InOpening(p, margin);
        }

        public float ClipRadius(float a)
        {
            int n = Clip.Length;
            float f = Mathf.Repeat(a, Mathf.PI * 2f) / (Mathf.PI * 2f / n);
            int i = Mathf.FloorToInt(f) % n;
            return Mathf.Lerp(Clip[i], Clip[(i + 1) % n], f - Mathf.Floor(f));
        }

        // The overlap of `mine` with `other` carried through the pair (see Clip), or null when `other` covers all
        // of `mine` (or does not cover its centre: then nothing sensible can be cut).
        public static float[] Overlap(PortalFrame mine, PortalFrame other, int n = 64)
        {
            Matrix4x4 m = other.WorldToLocal * PortalMath.Pair(mine, other) * mine.LocalToWorld;
            if (!other.InOpening(m.MultiplyPoint3x4(Vector3.zero)))
                return null;
            var clip = new float[n];
            bool cut = false;
            float reach = Mathf.Max(mine.HalfSize.x, mine.HalfSize.y) * 1.5f + 0.1f;
            for (int i = 0; i < n; i++)
            {
                float a = Mathf.PI * 2f * i / n;
                Vector2 d = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                float outer = Edge(t => mine.InOpening(d * t), reach);
                float inner = Edge(t => other.InOpening(m.MultiplyPoint3x4(d * t)), outer);
                clip[i] = inner;
                cut |= inner < outer - 0.02f;
            }
            return cut ? clip : null;
        }

        // The farthest t in 0..max still inside (inside at 0).
        static float Edge(System.Func<float, bool> inside, float max)
        {
            if (inside(max))
                return max;
            float lo = 0f, hi = max;
            for (int k = 0; k < 20; k++)
            {
                float mid = (lo + hi) * 0.5f;
                if (inside(mid))
                    lo = mid;
                else
                    hi = mid;
            }
            return lo;
        }

        // Inside this opening's own outline (whatever its partner's).
        public bool InOpening(Vector2 p, float margin = 0f)
        {
            float hx = HalfSize.x + margin, hy = HalfSize.y + margin;
            if (hx <= 0f || hy <= 0f)
                return false;
            if (Shape != null && Shape.Length >= 3)
            {
                // The outline's radius at this angle (straight between its points), grown by the margin.
                Vector2 q = new Vector2(p.x / HalfSize.x, p.y / HalfSize.y);
                float r = ShapeRadius(Mathf.Atan2(q.y, q.x));
                float grow = margin / Mathf.Min(HalfSize.x, HalfSize.y);
                return q.magnitude <= r + grow;
            }
            if (Round)
            {
                float x = p.x / hx, y = p.y / hy;
                return x * x + y * y <= 1f;
            }
            return Mathf.Abs(p.x) <= hx && Mathf.Abs(p.y) <= hy;
        }

        // True when the segment a->b passes through the opening (either direction).
        public bool Crosses(Vector3 a, Vector3 b, out Vector3 hit, out float t, float margin = 0f)
        {
            hit = default;
            t = 0f;
            float da = SignedDistance(a), db = SignedDistance(b);
            if ((da > 0f) == (db > 0f) || Mathf.Approximately(da, db))
                return false;
            t = da / (da - db);
            hit = Vector3.LerpUnclamped(a, b, t);
            Vector3 local = Quaternion.Inverse(Rotation) * (hit - Center);
            return ContainsLocal(local, margin);
        }

        // The shape's distance from the centre (in units of HalfSize) in direction angle a.
        public float ShapeRadius(float a)
        {
            int n = Shape.Length;
            float step = Mathf.PI * 2f / n;
            float f = Mathf.Repeat(a, Mathf.PI * 2f) / step;
            int i = Mathf.FloorToInt(f) % n, j = (i + 1) % n;
            // Where the ray at angle a meets the segment between points i and j.
            Vector2 d = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            Vector2 p = Shape[i], e = Shape[j] - Shape[i];
            float den = d.x * e.y - d.y * e.x;
            if (Mathf.Abs(den) < 1e-6f)
                return Mathf.Lerp(p.magnitude, Shape[j].magnitude, f - Mathf.Floor(f));
            return (p.x * e.y - p.y * e.x) / den;
        }

        // Points along the outline at a given local depth, for screen-rect projection.
        public void Outline(Vector3[] into, int count, float depth)
        {
            for (int i = 0; i < count; i++)
            {
                Vector3 local;
                if (Shape != null && Shape.Length >= 3)
                {
                    float a = Mathf.PI * 2f * i / count;
                    float k = ShapeRadius(a) / Mathf.Cos(Mathf.PI / count);
                    local = new Vector3(Mathf.Cos(a) * HalfSize.x * k, Mathf.Sin(a) * HalfSize.y * k, depth);
                }
                else if (Round)
                {
                    float a = Mathf.PI * 2f * i / count;
                    // Points of the circumscribed polygon so the rect never cuts the real ellipse.
                    float k = 1f / Mathf.Cos(Mathf.PI / count);
                    local = new Vector3(Mathf.Cos(a) * HalfSize.x * k, Mathf.Sin(a) * HalfSize.y * k, depth);
                }
                else
                {
                    int c = i % 4;
                    local = new Vector3(c == 0 || c == 3 ? -HalfSize.x : HalfSize.x, c < 2 ? -HalfSize.y : HalfSize.y, depth);
                }
                into[i] = Center + Rotation * local;
            }
        }
    }
}
