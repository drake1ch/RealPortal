using UnityEngine;

namespace RealPortal
{
    // Where windows show (Docs\PARITY-PLAN.md P1.4, P2.1, P4.1), for every part that must have the far side ready before
    // a window shows it: how far from the camera a window renders, whether one is on screen this frame or about to be
    // (the camera turning or moving toward it), and which half of its far side it shows. The camera's pose, turn rate,
    // speed and frustum are taken once a frame once its pose is final (WindowLayerCull.Update, from the
    // GameCamera.LateUpdate postfix; on first use when that has not run), so asking is a few multiplications and
    // allocates nothing. Main thread only.
    internal static class WindowReach
    {
        // Win.DistantLive: the live range is measured from the player along the ground, a window from the camera: add its
        // arm (longer on a ship) and some height (a portal up a slope).
        const float LiveSlack = 20f;
        // AboutToShow: a window this near counts whatever the direction (turned to in a moment); the lead past the view's
        // half-angle, and how many seconds of the turn rate widen it (at most a half turn); how far past the render reach
        // a window counts, plus the seconds of the camera's motion.
        const float Close = 30f, BaseLead = 45f, LeadSeconds = 0.3f, MaxLead = 180f, ExtraReach = 10f, AheadSeconds = 2f;
        // The turn rate and speed keep their peak, fading over this many seconds, so a frame without input inside a
        // flick does not narrow the lead.
        const float Hold = 0.2f;
        // Between samples farther apart than this (seconds), or a move longer than JumpDistance (a crossing, a teleport,
        // the camera carried through a portal), the rates start over.
        const float StaleAfter = 0.5f, JumpDistance = 8f;
        // P4.1: within this distance of the far portal's plane a point counts in both halves of the far side (a 2 m slab).
        internal const float Slab = 1f;

        static readonly Plane[] s_planes = new Plane[6];
        static int s_frame = -1;
        static bool s_has, s_through;
        static Vector3 s_eye, s_fwd;
        static float s_at, s_turn, s_speed, s_cosLead = 1f;

        // How far from the camera a window renders: with Win.DistantLive as far as a far end stays loaded around the
        // player (PreloadDistance + UnloadExtraDistance), else Window.MaxDistance. Every reach that went by MaxDistance
        // (the renderer's, waking, lights, grass, Win.LayerCull) goes by this; with the switch off it is MaxDistance.
        internal static float Render => WindowFlags.DistantLive
            ? Plugin.PreloadDistance.Value + Plugin.UnloadMargin.Value + LiveSlack
            : Plugin.MaxDistance.Value;

        // Lab and log: the camera's turn rate (degrees a second) and speed (m/s) at this frame's sample, peaks held.
        internal static float TurnRate => s_turn;
        internal static float Speed => s_speed;

        // Once a frame, with the game camera once its pose is final (WindowLayerCull.Update); again within a frame it does
        // nothing.
        internal static void Sample(Camera cam)
        {
            int frame = Time.frameCount;
            if (frame == s_frame)
                return;
            s_frame = frame;
            if (cam == null)
            {
                s_has = false;
                s_turn = s_speed = 0f;
                s_cosLead = 1f;
                return;
            }
            Transform t = cam.transform;
            Vector3 eye = t.position, fwd = t.forward;
            float now = Time.unscaledTime, dt = now - s_at;
            PortalSystem sys = PortalSystem.Instance;
            bool through = sys != null && sys.CameraThroughRecently;
            if (s_has && dt > 0f && dt < StaleAfter && through == s_through && (eye - s_eye).sqrMagnitude < JumpDistance * JumpDistance)
            {
                float keep = Mathf.Exp(-dt / Hold);
                s_turn = Mathf.Max(Vector3.Angle(s_fwd, fwd) / dt, s_turn * keep);
                s_speed = Mathf.Max(Vector3.Distance(s_eye, eye) / dt, s_speed * keep);
            }
            else
                s_turn = s_speed = 0f;
            s_has = true;
            s_through = through;
            s_at = now;
            s_eye = eye;
            s_fwd = fwd;
            GeometryUtility.CalculateFrustumPlanes(cam, s_planes);
            float half = Mathf.Max(cam.fieldOfView, Camera.VerticalToHorizontalFieldOfView(cam.fieldOfView, cam.aspect)) * 0.5f
                         + Mathf.Min(BaseLead + s_turn * LeadSeconds, MaxLead);
            s_cosLead = half >= 180f ? -2f : Mathf.Cos(half * Mathf.Deg2Rad);
        }

        // Last frame's sample stands for the start of this one (the camera moves in LateUpdate); older than that, the
        // sample is taken now (nothing sampled at the camera's LateUpdate).
        static void Ensure()
        {
            if (s_frame < Time.frameCount - 1)
                Sample(Utils.GetMainCamera());
        }

        static float Size(PortalFrame f) => Mathf.Max(f.HalfSize.x, f.HalfSize.y);

        // The window rendered or was shown this frame, or its opening lies inside the camera's frustum now within the
        // render reach (the renderer's own distance test). Before this frame's renders: by the frustum of the camera's
        // last pose.
        internal static bool OnScreen(PortalLink link)
        {
            if (link == null || !link.FrameSet)
                return false;
            PortalWindow w = link.Window;
            int frame = Time.frameCount;
            if (w != null && (w.RenderedAtFrame == frame || w.ShownAtFrame == frame))
                return true;
            Ensure();
            if (!s_has)
                return false;
            PortalFrame f = link.Frame;
            float size = Size(f), reach = Render + size;
            if ((f.Center - s_eye).sqrMagnitude > reach * reach)
                return false;
            return GeometryUtility.TestPlanesAABB(s_planes, new Bounds(f.Center, new Vector3(2f * size, 2f * size, 2f * size)));
        }

        // P1.4: the window is on screen (OnScreen), or will be in a moment: within 30 m of the camera, or within the view's
        // half-angle plus a lead of 45 degrees and 0.3 s of the camera's turn rate (at most a half turn), no farther than
        // the render reach plus 10 m and 2 s of the camera's motion. Only a link to a far side (not the game's teleport).
        internal static bool AboutToShow(PortalLink link)
        {
            if (link == null || link.Vanilla || !link.FrameSet)
                return false;
            if (OnScreen(link))
                return true;
            if (!s_has)
                return false;
            PortalFrame f = link.Frame;
            Vector3 to = f.Center - s_eye;
            float d = to.magnitude;
            if (d > Render + ExtraReach + s_speed * AheadSeconds + Size(f))
                return false;
            return d < Close || Vector3.Dot(to, s_fwd) > s_cosLead * d;
        }

        // P4.1: the half of its far side a window shows to an eye at `eye`: +1 the half in front of the far portal (along
        // its Forward), -1 the half behind it. That is the side of the far portal's plane away from Pair·eye, and Pair
        // takes this opening's front to the far one's back, so it is the side of this opening the eye is on: it flips as
        // the camera crosses the entry's plane.
        internal static int ShownHalf(PortalLink link, Vector3 eye) => link.Frame.SignedDistance(eye) >= 0f ? 1 : -1;

        // P4.1: the half of a far side a point lies in, as ShownHalf counts them (+1 or -1), or 0 within Slab of the far
        // portal's plane (plus the object's own radius, where known): there it counts in both.
        internal static int HalfOf(PortalFrame far, Vector3 pos, float radius = 0f)
        {
            float d = far.SignedDistance(pos), slab = Slab + radius;
            return d > slab ? 1 : d < -slab ? -1 : 0;
        }

        // Whether a point counts in that half of a far side (HalfOf 0 counts in both).
        internal static bool InHalf(PortalFrame far, Vector3 pos, int half, float radius = 0f)
        {
            int h = HalfOf(far, pos, radius);
            return h == 0 || h == half;
        }
    }
}
