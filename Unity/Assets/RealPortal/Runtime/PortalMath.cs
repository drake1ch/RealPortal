using UnityEngine;

namespace RealPortal
{
    public static class PortalMath
    {
        static readonly Matrix4x4 HalfTurn = Matrix4x4.Rotate(Quaternion.Euler(0f, 180f, 0f));

        // Maps the space around `from` onto the space around `to`: stepping into the front of `from`
        // (along -forward) comes out of the front of `to` (along +forward). Works for both faces. Foot to foot (see
        // PortalFrame.BaseDrop): for two portals of one kind the same as centre to centre.
        public static Matrix4x4 Pair(PortalFrame from, PortalFrame to) => to.FootToWorld * HalfTurn * from.WorldToFoot;

        // Distances from the opening over which the view scale goes from 1 (and Pair) to its fit.
        public const float ViewScaleFrom = 0.5f, ViewScaleTo = 5f;

        // Where a window camera stands for a viewer at `eye`: Pair, except when the exit's opening is smaller than
        // the entry's (from.ViewFit < 1). Then, the farther the viewer stands from the opening, the more the view is
        // scaled down round the far portal (the place beyond looks bigger), so that the whole entry shows what lies
        // inside the exit's opening and above its floor; up close (and so when walking through) it is Pair again.
        public static Matrix4x4 View(PortalFrame from, PortalFrame to, Vector3 eye, out float scale)
        {
            scale = 1f;
            if (from.ViewFit >= 0.999f)
                return Pair(from, to);
            float t = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(ViewScaleFrom, ViewScaleTo, Mathf.Abs(from.SignedDistance(eye))));
            if (t <= 0f)
                return Pair(from, to);
            scale = Mathf.Lerp(1f, from.ViewFit, t);
            float lift = Mathf.Lerp(from.BaseDrop - to.BaseDrop, from.ViewLift, t);
            return to.LocalToWorld * HalfTurn * Matrix4x4.TRS(new Vector3(0f, lift, 0f), Quaternion.identity, Vector3.one * scale) * from.WorldToLocal;
        }

        public static Quaternion Rotation(Matrix4x4 m) => Quaternion.LookRotation(m.GetColumn(2), m.GetColumn(1));

        // Eric Lengyel's oblique near plane. `plane` is in camera space; its positive side stays visible.
        public static Matrix4x4 Oblique(Matrix4x4 p, Vector4 plane)
        {
            Vector4 q = p.inverse * new Vector4(Mathf.Sign(plane.x), Mathf.Sign(plane.y), 1f, 1f);
            Vector4 c = plane * (2f / Vector4.Dot(plane, q));
            p[2] = c.x - p[3];
            p[6] = c.y - p[7];
            p[10] = c.z - p[11];
            p[14] = c.w - p[15];
            return p;
        }

        // Camera-space plane through `point`, normal pointing at the region that must stay visible.
        public static Vector4 CameraSpacePlane(Matrix4x4 worldToCamera, Vector3 point, Vector3 visibleNormal, float offset)
        {
            Vector3 p = worldToCamera.MultiplyPoint(point + visibleNormal * offset);
            Vector3 n = worldToCamera.MultiplyVector(visibleNormal).normalized;
            return new Vector4(n.x, n.y, n.z, -Vector3.Dot(p, n));
        }

        // Projection that stretches the viewport sub-rect `r` (0..1 screen units) over the whole NDC square,
        // so a camera with rect = r draws exactly those pixels of the full view (a scissor).
        public static Matrix4x4 Scissor(Rect r)
        {
            float a = 2f * r.xMin - 1f, b = 2f * r.xMax - 1f;
            float c = 2f * r.yMin - 1f, d = 2f * r.yMax - 1f;
            Matrix4x4 s = Matrix4x4.identity;
            s.m00 = 2f / (b - a);
            s.m03 = -(a + b) / (b - a);
            s.m11 = 2f / (d - c);
            s.m13 = -(c + d) / (d - c);
            return s;
        }

        // Screen rect (0..1) covered by the given world points under view-projection `vp`.
        // Returns false when nothing is in front of the camera. Points behind the eye make it full screen.
        public static bool ScreenRect(Matrix4x4 vp, Vector3[] points, int count, out Rect rect) => ScreenRect(vp, points, count, out rect, out _);

        // The same, with `extent`: the points' whole width and height on screen (screen units), not cut by the screen's
        // edges (the full screen when a point lies behind the eye).
        public static bool ScreenRect(Matrix4x4 vp, Vector3[] points, int count, out Rect rect, out Vector2 extent)
        {
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            bool behind = false, inFront = false;
            for (int i = 0; i < count; i++)
            {
                Vector4 c = vp * new Vector4(points[i].x, points[i].y, points[i].z, 1f);
                if (c.w <= 0.001f)
                {
                    behind = true;
                    continue;
                }
                inFront = true;
                float x = c.x / c.w * 0.5f + 0.5f, y = c.y / c.w * 0.5f + 0.5f;
                x0 = Mathf.Min(x0, x); x1 = Mathf.Max(x1, x);
                y0 = Mathf.Min(y0, y); y1 = Mathf.Max(y1, y);
            }
            if (behind)
            {
                rect = new Rect(0f, 0f, 1f, 1f);
                extent = Vector2.one;
                return true;
            }
            extent = inFront ? new Vector2(x1 - x0, y1 - y0) : Vector2.zero;
            rect = Rect.MinMaxRect(Mathf.Clamp01(x0), Mathf.Clamp01(y0), Mathf.Clamp01(x1), Mathf.Clamp01(y1));
            return inFront && rect.width > 0f && rect.height > 0f;
        }

        // Destroy that also works outside play mode (the editor test scene).
        public static void Kill(Object o)
        {
            if (o == null)
                return;
            if (Application.isPlaying)
                Object.Destroy(o);
            else
                Object.DestroyImmediate(o);
        }

        public static Rect Intersect(Rect a, Rect b)
        {
            float x0 = Mathf.Max(a.xMin, b.xMin), y0 = Mathf.Max(a.yMin, b.yMin);
            float x1 = Mathf.Min(a.xMax, b.xMax), y1 = Mathf.Min(a.yMax, b.yMax);
            return x1 > x0 && y1 > y0 ? Rect.MinMaxRect(x0, y0, x1, y1) : Rect.zero;
        }

        // Snaps the rect outward to whole pixels of a w x h target so the scissor has no seams.
        public static Rect SnapToPixels(Rect r, int w, int h)
        {
            // The tolerance keeps a rect already on pixel edges as it is (float error would add a pixel).
            const float eps = 1e-3f;
            float x0 = Mathf.Floor(r.xMin * w + eps) / w, y0 = Mathf.Floor(r.yMin * h + eps) / h;
            float x1 = Mathf.Ceil(r.xMax * w - eps) / w, y1 = Mathf.Ceil(r.yMax * h - eps) / h;
            return Rect.MinMaxRect(Mathf.Clamp01(x0), Mathf.Clamp01(y0), Mathf.Clamp01(x1), Mathf.Clamp01(y1));
        }
    }
}
