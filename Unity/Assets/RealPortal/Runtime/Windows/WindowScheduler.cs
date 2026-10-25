using System;
using System.Collections.Generic;
using UnityEngine;

namespace RealPortal
{
    // OWNER: sched (S3). Tiers with hysteresis (§2.2), the error model and the choice under the CPU/GPU budgets (§4.6),
    // the tier effect sets (§4.5). Contract: IWindowScheduler in WindowContracts.cs; semantics: Docs\S2S3-CONTRACTS.md §6.
    //
    // Pure CPU: the manager fills each WindowState's geometry and far-side facts first, then calls PrepareFrame (the Live
    // ranking needs every window), Classify per window, Error per window and Choose once. Nothing here allocates per frame.
    public sealed partial class WindowScheduler : IWindowScheduler
    {
        readonly WindowPipelineSettings _settings;
        readonly IWindowCostModel _cost;
        readonly IGpuBudget _gpu;
        bool _disposed;

        /// <summary>The manager this scheduler works for (WindowMechanisms.Effective); set by the WindowManager constructor.</summary>
        internal IWindowManager Manager;
        /// <summary>PortalRenderSettings.MinScreenFraction (the manager sets it before each Plan): below it a window is
        /// Closed, as the post-S1 renderer leaves it.</summary>
        internal float MinScreenFraction = 0.0005f;
        /// <summary>PortalRenderSettings.MinPixels (set with MinScreenFraction): with Win.DistantLive a window is Closed
        /// below this many pixels tall or wide (WindowState.OpeningSize) instead of below MinScreenFraction.</summary>
        internal float MinPixels = 3f;
        /// <summary>Counters of the last Choose (the manager copies them into WindowFrameStats).</summary>
        internal int LastMandatory, LastOverBudget, LastGuard, LastHardCap, LastDynamicCap;
        /// <summary>The GPU estimate (ms) of the last Choose's mandatory renders: the floor GpuBudget never takes B_gpu
        /// below (the manager hands it over at the next Plan).</summary>
        internal float LastMandatoryGpuMs;
        /// <summary>Win.MandatoryCap, this frame: renders owed outside the budget besides Live and Crossing (first show,
        /// camera jump, urgent), and how many of them showed their cache (or the swirl) instead; the manager's ChooseLate
        /// adds its own deferrals.</summary>
        internal int LastOwed, LastDeferred;
        /// <summary>Lab: owed renders deferred to a later frame by Win.MandatoryCap, since start.</summary>
        internal static long LabCapDeferred;
        /// <summary>Win.ShadowCost: heavy renders that waited past the budget because the frame already had one (the last
        /// Choose; since start).</summary>
        internal int LastStackDeferred;
        internal static long LabStackDeferred;

        // Invalidations the scheduler does not leave to the budget (Choose). A camera jump: no cached view lines up any
        // more, and post-S1 rendered every window on that frame. The far side changed under the cache (weather, a flash,
        // the pair, the far area's content): such a cache renders again no later than the post-S1 cadence would have
        // (Caps.LegacyEvery frames), whatever the count cap or B_cpu say, so an event never shows longer than it did.
        // Win.MandatoryCap takes both out of that: they queue for MandatoryCapPerFrame renders a frame (Choose).
        const WindowInvalid Urgent = WindowInvalid.EnvSwitch | WindowInvalid.Lightning | WindowInvalid.PairMoved | WindowInvalid.SpanChange;
        // A hidden (occluded) window whose cache one of these made wrong gets its guard render now: when it comes out from
        // behind the occluder the readback sees it two frames late, and the wrong view is what shows meanwhile. Lightning
        // is left out (a flash is over before the guard's view would be needed; the flag makes the window render as soon
        // as it is visible again, through Urgent).
        const WindowInvalid GuardNow = WindowInvalid.EnvSwitch | WindowInvalid.PairMoved | WindowInvalid.SpanChange | WindowInvalid.CameraJump;

        readonly List<WindowState> _live = new List<WindowState>(8);
        readonly List<WindowState> _overdue = new List<WindowState>(16);
        readonly List<WindowState> _byError = new List<WindowState>(16);
        readonly List<WindowState> _guards = new List<WindowState>(8);
        readonly List<WindowState> _grouped = new List<WindowState>(16);
        readonly List<WindowState> _owed = new List<WindowState>(16);
        float _cpu, _gpuMs;
        int _count;
        // Win.MandatoryCap: renders past the budget this frame besides Live and Crossing (Choose spends the allowance
        // first, the manager's ChooseLate what is left), against MandatoryCapPerFrame.
        int _capFrame = -100, _capUsed;
        // Win.ShadowCost, this choice: heavy renders are spread (the switch with Win.Budget in force, read once), and the
        // heavy renders taken so far (Live and Crossing never count: they render every frame whatever they cost).
        bool _spread;
        int _heavy;

        // Live candidates: crossing first, then nearest, then largest, by the keys PrepareFrame gives them (an incumbent's
        // distance counts ×(1 - Hysteresis), its share ×(1 + Hysteresis): a Live window keeps its place until a challenger
        // is clearly nearer or larger, so windows at nearly the same distance do not swap places as the player sways).
        static readonly Comparison<WindowState> s_liveOrder = (a, b) =>
        {
            if (a.Crossing != b.Crossing)
                return a.Crossing ? -1 : 1;
            int d = a.LiveKeyDistance.CompareTo(b.LiveKeyDistance);
            return d != 0 ? d : b.LiveKeyShare.CompareTo(a.LiveKeyShare);
        };
        static readonly Comparison<WindowState> s_overdueOrder = (a, b) => b.Overdue.CompareTo(a.Overdue);
        static readonly Comparison<WindowState> s_priorityOrder = (a, b) => b.Priority.CompareTo(a.Priority);
        // Win.MandatoryCap's queue: a first show or a jump before an urgent event alone (an event over there shows late in a
        // cache that still lines up; after a jump nothing does), then the longest waiting (a window owed again after its
        // render goes behind those still waiting, so a burst of jumps cannot starve one), then by share over distance
        // (the windows a jump made wrong all wait from the same frame: the large, near ones first). A comparer object:
        // List.Sort(Comparison) wraps the delegate in a new comparer on every call.
        sealed class OwedOrder : IComparer<WindowState>
        {
            public int Compare(WindowState a, WindowState b)
            {
                if (a.OwedUrgentOnly != b.OwedUrgentOnly)
                    return a.OwedUrgentOnly ? 1 : -1;
                if (a.OwedSince != b.OwedSince)
                    return a.OwedSince.CompareTo(b.OwedSince);
                return b.OwedKey.CompareTo(a.OwedKey);
            }
        }

        static readonly OwedOrder s_owedOrder = new OwedOrder();

        public WindowScheduler(WindowPipelineSettings settings, IWindowCostModel cost, IGpuBudget gpu)
        {
            _settings = settings ?? new WindowPipelineSettings();
            _cost = cost;
            _gpu = gpu;
        }

        public bool Ready => !_disposed && _cost != null;

        bool On(WindowMechanism x) => Manager != null && Manager.Effective(x);

        internal static bool IsVisible(WindowTier t) => t == WindowTier.Live || t == WindowTier.NearCached || t == WindowTier.SmallCached;

        // Rule 3: hidden behind something (0 visible px in the last readbacks, which are recent, and the rect has not grown
        // much since the readback that saw it hidden). A window the player is passing through never counts as hidden.
        bool Occluded(WindowState s, in WindowFrameContext ctx)
        {
            if (!On(WindowMechanism.Occlusion) || s.Crossing)
                return false;
            if (s.ZeroReadbacks < _settings.HiddenAfterZeroReadbacks || s.VisibleFrame < ctx.Frame - _settings.ReadbackMaxAge)
                return false;
            return NotRegrown(s);
        }

        // The rect is not much larger than at the readback that counted it (a rect grown by more than RegrowForce renders
        // whatever the readback said: it may show what the count never saw).
        bool NotRegrown(WindowState s)
        {
            int then = s.RectPxRing[s.VisibleFrame & 7];
            return then <= 0 || s.RectPx <= then * (1f + _settings.RegrowForce);
        }

        // Rule 3 on its way: the last readback (recent) saw none of it, but fewer of them than Hidden needs. An occluder
        // most likely came; Hidden follows with the next readback (a render now would draw a view nobody sees).
        bool ZeroSeen(WindowState s, in WindowFrameContext ctx) =>
            On(WindowMechanism.Occlusion) && !s.Crossing && s.ZeroReadbacks > 0 && s.ZeroReadbacks < _settings.HiddenAfterZeroReadbacks
            && s.VisibleFrame >= ctx.Frame - _settings.ReadbackMaxAge && NotRegrown(s);

        /// <summary>
        /// Before Classify: which windows are Live candidates (rule 4) and their rank (at most LiveMax become Live), with
        /// the hysteresis of the window's current tier. Classify without it (a window that showed up after Plan) only
        /// makes crossing windows Live.
        /// </summary>
        internal void PrepareFrame(IReadOnlyList<WindowState> states, in WindowFrameContext ctx)
        {
            _live.Clear();
            float h = _settings.Hysteresis;
            for (int i = 0; i < states.Count; i++)
            {
                WindowState s = states[i];
                s.LiveCandidate = false;
                s.LiveRank = int.MaxValue;
                if (!s.InFrustum || !s.CanShow || !s.InRange || s.Share <= 0f || Occluded(s, ctx))
                    continue;
                bool live = s.Tier == WindowTier.Live;
                // Thresholds ×(1 + h) to become more expensive, ×(1 - h) to become cheaper (distance: the other way round).
                float share = _settings.TierA_Share * (live ? 1f - h : 1f + h);
                float dist = _settings.LiveDistance * (live ? 1f + h : 1f);
                if (s.Crossing || s.Distance < dist || s.Share >= share)
                {
                    s.LiveCandidate = true;
                    s.LiveKeyDistance = s.Distance * (live ? 1f - h : 1f);
                    s.LiveKeyShare = s.Share * (live ? 1f + h : 1f);
                    _live.Add(s);
                }
            }
            _live.Sort(s_liveOrder);
            // A rank-only change waits for the dwell: a challenger (not crossing) that outranks an incumbent which became
            // Live less than DwellSeconds ago takes its place only once that dwell is over. Classify then demotes the
            // outranked incumbent and promotes the challenger in the same frame, so there are never more than LiveMax.
            int max = Mathf.Max(0, _settings.LiveMax);
            for (int i = 0; i < max && i < _live.Count; i++)
            {
                WindowState c = _live[i];
                if (c.Tier == WindowTier.Live || c.Crossing)
                    continue;
                for (int j = max; j < _live.Count; j++)
                {
                    WindowState inc = _live[j];
                    if (inc.Tier == WindowTier.Live && !inc.Crossing && ctx.Now - inc.TierSince < _settings.DwellSeconds)
                    {
                        _live[i] = inc;
                        _live[j] = c;
                        break;
                    }
                }
            }
            for (int i = 0; i < _live.Count; i++)
                _live[i].LiveRank = i;
        }

        public void Classify(WindowState s, in WindowFrameContext ctx)
        {
            WindowPipelineSettings st = _settings;
            WindowTier cur = s.Tier;
            float h = st.Hysteresis;
            bool curVisible = IsVisible(cur);
            WindowTier raw;
            bool immediate = false;
            float area = s.OpeningRect.width * s.OpeningRect.height;
            if (!s.InFrustum)
            {
                raw = WindowTier.Hidden;
                immediate = true;
            }
            // Today's visible set: what the post-S1 renderer would not render shows the swirl (W-D does not exist in S3).
            // The size test goes by the rect, not the visible pixels, so occlusion never turns a window into the swirl.
            // Win.DistantLive (Docs\PARITY-PLAN.md P2): by the pixel floor on the opening's whole extent instead.
            else if (!s.CanShow || !s.InRange || (WindowFlags.DistantLive
                         ? s.OpeningSize.x * ctx.ScreenW < MinPixels * (curVisible ? 1f - h : 1f)
                           || s.OpeningSize.y * ctx.ScreenH < MinPixels * (curVisible ? 1f - h : 1f)
                         : area < MinScreenFraction * (curVisible ? 1f - h : 1f)))
            {
                raw = WindowTier.Closed;
                immediate = true;
            }
            else if (Occluded(s, ctx))
            {
                // Immediate: the occlusion gate wants zero renders within 3 frames of the occluder (3 of them readback latency; ZeroSeen holds optional renders from the first zero count).
                raw = WindowTier.Hidden;
                immediate = true;
            }
            else if (s.LiveCandidate)
                raw = s.LiveRank < st.LiveMax ? WindowTier.Live : WindowTier.NearCached;
            // A window that showed up after PrepareFrame: only crossing makes it Live.
            else if (s.Crossing)
                raw = WindowTier.Live;
            else if (s.Distance >= st.SmallFromDistance * (cur == WindowTier.SmallCached ? 1f - h : 1f))
                raw = WindowTier.SmallCached;
            else
            {
                bool nearNow = cur == WindowTier.NearCached || cur == WindowTier.Live;
                raw = s.Share >= st.TierB_Share * (nearNow ? 1f - h : 1f + h) ? WindowTier.NearCached : WindowTier.SmallCached;
            }
            if (raw == WindowTier.NearCached && ctx.Budget.DemoteNear)
                raw = WindowTier.SmallCached;
            // Outranked for one of the LiveMax places: at once, or more windows than LiveMax would render every frame (the
            // ranking already kept an incumbent in its dwell, PrepareFrame).
            if (cur == WindowTier.Live && raw != WindowTier.Live && s.LiveCandidate)
                immediate = true;
            if (s.LabTier.HasValue)
            {
                raw = s.LabTier.Value;
                immediate = true;
            }
            s.WantTier = raw;
            if (raw != cur)
            {
                // Dwell between changes, except where waiting would be wrong: becoming visible (out of the frustum or the
                // swirl), going out of sight, and Live by crossing or distance (the window right in front of the player).
                bool forced = immediate || !curVisible || (raw == WindowTier.Live && (s.Crossing || s.Distance < st.LiveDistance));
                if (forced || ctx.Now - s.TierSince >= st.DwellSeconds)
                {
                    s.PrevTier = cur;
                    s.Tier = raw;
                    s.TierSince = ctx.Now;
                }
            }
            if (IsVisible(s.Tier))
            {
                s.LastVisibleTier = s.Tier;
                s.VisibleAt = ctx.Now;
            }
            // Hidden and Closed windows render only as guards (occluded in the frustum): at the tier they had last.
            WindowTier pt = IsVisible(s.Tier) ? s.Tier : s.LastVisibleTier;
            s.Params = ParamsFor(pt, s, ctx);
            s.ParamsFrame = ctx.Frame;
        }

        public float Error(WindowState s, in WindowFrameContext ctx)
        {
            s.EPar = s.ECov = s.ETime = s.ERes = 0f;
            float e;
            WindowCache c = s.Cache;
            if (s.Invalid != WindowInvalid.None || !c.Valid)
                e = float.PositiveInfinity;
            else if (!On(WindowMechanism.ErrorModel))
            {
                // The post-S1 cadence: every 2nd frame for W-B, every 3rd for W-C.
                e = s.AgeFrames(ctx.Frame) >= _settings.CapsOf(s.Tier).LegacyEvery ? float.PositiveInfinity : 0f;
            }
            else
            {
                PortalWindow w = s.Window;
                if (w.Partner == null)
                    e = float.PositiveInfinity;
                else
                {
                    // Where the window camera would stand now against where it stood (far side), split along and across
                    // the cache's view.
                    Matrix4x4 m = PortalMath.View(w.Frame, w.Partner.Frame, ctx.Eye, out _);
                    Vector3 d = m.MultiplyPoint3x4(ctx.Eye) - c.Eye;
                    float along = Vector3.Dot(d, c.Forward);
                    Vector3 across = d - along * c.Forward;
                    float f = ctx.FocalPx;
                    if (On(WindowMechanism.Resolve))
                    {
                        // Depth reprojection. Parallax between the nearest and the farthest scenery seen: a shift across
                        // the view moves it by |Δ⊥|/z, a shift along it by |Δ∥|·tanθ/z at the off-axis angle θ (a window
                        // at the side of the screen while the player runs towards it).
                        float zn = Mathf.Max(Mathf.Max(c.ZNear, c.Near), 0.01f), zf = Mathf.Max(c.ZFar, zn);
                        float tan = EdgeTan(s.OpeningRect, ctx);
                        s.EPar = f * (across.magnitude + Mathf.Abs(along) * tan) * (1f / zn - 1f / zf);
                        // Coverage: rays through the opening now that leave the cached rect (the march then stretches
                        // the cache's edge). The depth stats do not bound it: the far scenery and the sky shift by Δ/D at
                        // the opening's own distance D, and turning brings in what the screen's edge cut off.
                        s.ECov = Coverage(s.OpeningRect, c, ctx);
                    }
                    else
                    {
                        // Laid flat onto the opening (Win.Resolve not in force, an old bundle): everything from the
                        // opening plane out to the sky is off by f·|Δ|·(1/D − 1/far), whatever depth range the stats saw.
                        float dOpen = Mathf.Max(Mathf.Min(OpeningDepth(w.Partner.Frame, c.Eye, c.Forward),
                                                          OpeningDepth(w.Partner.Frame, c.Eye + d, c.Forward)), 0.05f);
                        s.EPar = f * d.magnitude * (1f / dOpen - 1f / Mathf.Max(c.Far, dOpen));
                    }
                    float age = Mathf.Max(0f, ctx.Now - c.Time);
                    s.ETime = age * (8f * s.LifeInView + (s.WeatherActive ? 4f : 0f) + 3f * c.WaterFrac + 1f);
                    if (c.RectPx > 0 && s.RectPx > c.RectPx)
                        s.ERes = 4f * Mathf.Log((float)s.RectPx / c.RectPx, 2f);
                    e = s.EPar + s.ECov + s.ETime + s.ERes;
                }
            }
            s.ErrorPx = e;
            return e;
        }

        // Tangent of the largest off-axis angle inside `r` (main screen, 0..1) for the camera of `ctx`: the cache camera
        // looks the same way (its frustum is the main camera's behind the rect).
        static float EdgeTan(Rect r, in WindowFrameContext ctx)
        {
            if (r.width <= 0f || r.height <= 0f)
                return 0f;
            float f = Mathf.Max(ctx.FocalPx, 1f);
            float x = Mathf.Max(Mathf.Abs(r.xMin - 0.5f), Mathf.Abs(r.xMax - 0.5f)) * ctx.ScreenW / f;
            float y = Mathf.Max(Mathf.Abs(r.yMin - 0.5f), Mathf.Abs(r.yMax - 0.5f)) * ctx.ScreenH / f;
            return Mathf.Sqrt(x * x + y * y);
        }

        // How far (main-screen px) the directions inside the opening's rect now reach beyond the rect the cache was
        // rendered for (its margin included): the band the resolve has no texels for. Directions, so it is exact for the
        // sky and far scenery, which is where the band is widest (nearer scenery seen through those pixels lies between the
        // opening's edge, which the cache covers, and that direction). The current rect is clipped to the screen, so only
        // what is on screen now counts; rotation alone adds nothing unless it brings in what the screen's edge cut off.
        static float Coverage(Rect open, WindowCache c, in WindowFrameContext ctx)
        {
            if (open.width <= 0f || open.height <= 0f)
                return 0f;
            float f = Mathf.Max(ctx.FocalPx, 1f);
            float worst = 0f;
            for (int k = 0; k < 4; k++)
            {
                float u = (k & 1) == 0 ? open.xMin : open.xMax, v = (k & 2) == 0 ? open.yMin : open.yMax;
                Vector3 dir = ctx.Rotation * new Vector3((u - 0.5f) * ctx.ScreenW / f, (v - 0.5f) * ctx.ScreenH / f, 1f);
                // The main camera's non-jittered view-projection at the render, applied to a direction (w = 0).
                Vector4 clip = c.MainVP * new Vector4(dir.x, dir.y, dir.z, 0f);
                if (clip.w <= 1e-5f)
                    return ctx.ScreenH;   // behind the camera it was rendered from: nothing of it is covered
                float cu = clip.x / clip.w * 0.5f + 0.5f, cv = clip.y / clip.w * 0.5f + 0.5f;
                float ox = Mathf.Max(0f, Mathf.Max(c.Rect.xMin - cu, cu - c.Rect.xMax)) * ctx.ScreenW;
                float oy = Mathf.Max(0f, Mathf.Max(c.Rect.yMin - cv, cv - c.Rect.yMax)) * ctx.ScreenH;
                worst = Mathf.Max(worst, Mathf.Max(ox, oy));
            }
            return worst;
        }

        // The nearest depth of the exit opening along `fwd` from `eye` (its bounding box, so never deeper than the rim).
        static float OpeningDepth(PortalFrame exit, Vector3 eye, Vector3 fwd)
        {
            Vector3 right = exit.Rotation * Vector3.right, up = exit.Rotation * Vector3.up;
            return Vector3.Dot(exit.Center - eye, fwd) - Mathf.Abs(Vector3.Dot(right, fwd)) * exit.HalfSize.x
                   - Mathf.Abs(Vector3.Dot(up, fwd)) * exit.HalfSize.y;
        }

        bool Fits(WindowState s, in WindowBudget b) =>
            _cpu + s.CpuEstimate <= b.CpuMs && _gpuMs + s.GpuEstimate <= b.GpuMs && (b.MaxRenders <= 0 || _count < b.MaxRenders);

        void Take(WindowState s, WindowChoice why, List<WindowState> chosen)
        {
            s.Chosen = true;
            s.Reason = why;
            chosen.Add(s);
            _cpu += s.CpuEstimate;
            _gpuMs += s.GpuEstimate;
            _count++;
            if (s.ShadowHeavy)
                _heavy++;
        }

        // Win.ShadowCost: a heavy render (WindowCostModel.Heavy: a view full of shadow casters, by its last renders) past the
        // budget waits while the frame already has a heavy one, so two of them never stack in one frame past it; its
        // cache shows meanwhile and a lighter window may take its place. Not once it is ShadowStackOverdue times past its
        // cap (a reappeared window or a guard an event made wrong never waits): a view that stays heavy must not age on.
        // Within the budget nothing waits: there the budget is the bound.
        bool Stacks(WindowState s)
        {
            if (!_spread || !s.ShadowHeavy || _heavy == 0 || s.Overdue >= _settings.ShadowStackOverdue)
                return false;
            LastStackDeferred++;
            LabStackDeferred++;
            return true;
        }

        // Win.MandatoryCap: the frame's allowance is not spent yet (TakeOverBudget would give one more).
        bool OverBudgetLeft(int frame) => (_capFrame != frame ? 0 : _capUsed) < Mathf.Max(0, _settings.MandatoryCapPerFrame);

        // Win.MandatoryCap: an owed render taken. It leaves the queue: owed again after this render, it waits behind
        // those still waiting. Mandatory is counted by the caller, only for the ones taken past the budget (the column
        // means "outside the budget"; those taken within it already show in Renders).
        void TakeOwed(WindowState s, List<WindowState> chosen)
        {
            Take(s, s.Reason, chosen);
            s.OwedFrame = -100;
        }

        /// <summary>Win.MandatoryCap: one more render past the budget in `frame` besides Live and Crossing, while the
        /// frame's allowance (MandatoryCapPerFrame) lasts. One counter for Choose and the manager's ChooseLate, so the
        /// windows planned at the render cannot add a burst of their own.</summary>
        internal bool TakeOverBudget(int frame)
        {
            if (_capFrame != frame)
            {
                _capFrame = frame;
                _capUsed = 0;
            }
            if (_capUsed >= Mathf.Max(0, _settings.MandatoryCapPerFrame))
                return false;
            _capUsed++;
            return true;
        }

        /// <summary>Win.Prewarm: whether `s` (out of the view, to be rendered ahead with its Params and RenderPx as the
        /// manager set them) fits what this frame's Choose left of the budget; if so it is counted against it. Never past
        /// the budget: a render ahead is not owed, and while Live windows fill the budget it waits for a quieter frame.</summary>
        internal bool TakeAhead(WindowState s, in WindowBudget b)
        {
            WindowTier tier = s.Params.Tier;
            int px = Mathf.Max(1, (int)s.RenderPx);
            // Win.ShadowCost: a render ahead is never judged heavy (nor counted as one): its estimates are the EMA's.
            s.ShadowHeavy = false;
            s.CpuEstimate = _cost != null ? _cost.CpuEstimateMs(s, tier) : 2f;
            s.GpuEstimate = _cost != null ? _cost.GpuEstimateMs(s, tier, px) : 0.5f;
            if (!Fits(s, b))
                return false;
            _cpu += s.CpuEstimate;
            _gpuMs += s.GpuEstimate;
            _count++;
            return true;
        }

        public void Choose(IReadOnlyList<WindowState> states, in WindowFrameContext ctx, List<WindowState> chosen)
        {
            chosen.Clear();
            _overdue.Clear();
            _byError.Clear();
            _guards.Clear();
            _cpu = _gpuMs = 0f;
            _count = 0;
            LastMandatory = LastOverBudget = LastGuard = LastHardCap = LastDynamicCap = 0;
            WindowBudget b = ctx.Budget;
            int frame = ctx.Frame;
            // Win.MandatoryCap: the frame's allowance starts with this choice (read once: a switch flipped mid-frame
            // takes effect at the next Plan).
            bool cap = WindowFlags.MandatoryCap;
            _owed.Clear();
            LastOwed = LastDeferred = 0;
            _capFrame = frame;
            _capUsed = 0;
            // Win.ShadowCost: a rule of the budget, so only while Win.Budget is in force (without it nothing is past one).
            WindowCostModel model = _cost as WindowCostModel;
            _spread = WindowFlags.ShadowCost && model != null && On(WindowMechanism.Budget);
            _heavy = 0;
            LastStackDeferred = 0;

            for (int i = 0; i < states.Count; i++)
            {
                WindowState s = states[i];
                s.Chosen = false;
                s.Reason = WindowChoice.None;
                s.Priority = 0f;
                s.Overdue = 0f;
                WindowTier tier = s.Params.Tier;
                int px = Mathf.Max(1, (int)s.RenderPx);
                s.CpuEstimate = _cost != null ? _cost.CpuEstimateMs(s, tier) : 2f;
                // Win.ShadowCost: heavy among the renders the budget decides on (Live and Crossing render every frame, the
                // lab's cadence is exact). Only a heavy one is budgeted at its peak (and its GPU estimate's cascade term
                // raised by it, so that estimate follows this); every other estimate stays the EMA.
                s.ShadowHeavy = _spread && s.Tier != WindowTier.Live && !s.Crossing && s.LabEvery <= 0 && model.Heavy(s, tier);
                if (s.ShadowHeavy)
                    s.CpuEstimate = model.PeakEstimateMs(s, tier);
                s.GpuEstimate = _cost != null ? _cost.GpuEstimateMs(s, tier, px) : 0.5f;

                if (!IsVisible(s.Tier))
                {
                    // Occluded in the frustum: a guard render every GuardEverySeconds, or now when an event made its cache
                    // wrong (GuardNow); the most overdue first (step 4).
                    if (s.Tier == WindowTier.Hidden && s.InFrustum && s.CanShow && s.InRange && s.LabEvery <= 0)
                    {
                        bool wrong = s.Cache.Valid && (s.Invalid & GuardNow) != 0;
                        float since = ctx.Now - s.LastGuard;
                        if (wrong || since >= _settings.GuardEverySeconds)
                        {
                            s.Overdue = wrong ? float.MaxValue : since / Mathf.Max(_settings.GuardEverySeconds, 0.01f);
                            _guards.Add(s);
                        }
                    }
                    continue;
                }
                // Lab cadence: exactly every LabEvery frames from LabFrom, and never otherwise.
                if (s.LabEvery > 0)
                {
                    if ((frame - s.LabFrom) % s.LabEvery == 0)
                    {
                        Take(s, WindowChoice.Lab, chosen);
                        LastMandatory++;
                    }
                    continue;
                }
                TierCaps caps = _settings.CapsOf(s.Tier);
                int ageFrames = s.AgeFrames(frame);
                // 1. Mandatory, no budget check.
                WindowChoice must = WindowChoice.None;
                if (s.Tier == WindowTier.Live)
                    must = s.Crossing ? WindowChoice.Crossing : WindowChoice.Live;
                else if (s.Crossing)
                    must = WindowChoice.Crossing;
                else if ((s.Invalid & WindowInvalid.FirstShow) != 0 || !s.Cache.Valid)
                    must = WindowChoice.FirstShow;
                else if ((s.Invalid & WindowInvalid.CameraJump) != 0)
                    must = WindowChoice.Invalid;
                else if ((s.Invalid & Urgent) != 0 && ageFrames >= caps.LegacyEvery)
                    must = WindowChoice.Invalid;
                // Win.MandatoryCap: only Live and Crossing render outside the budget at once. A first show, a jump or an
                // urgent event waits in the owed queue (taken below, MandatoryCapPerFrame a frame), showing its cache
                // resolved (or the swirl) meanwhile, its flags kept: a jump or a long frame on the run made every
                // window's cache wrong, and rendering them all in that frame was a burst of 10-16 renders.
                if (cap && must != WindowChoice.None && must != WindowChoice.Live && must != WindowChoice.Crossing)
                {
                    // Waiting since the first frame of this run of owed frames (not owed last frame: a new entry).
                    if (s.OwedFrame < frame - 1)
                        s.OwedSince = frame;
                    s.OwedFrame = frame;
                    s.OwedUrgentOnly = s.Cache.Valid && (s.Invalid & (WindowInvalid.FirstShow | WindowInvalid.CameraJump)) == 0;
                    s.OwedKey = s.Share / Mathf.Max(s.Distance, 1f);
                    s.Reason = must;
                    _owed.Add(s);
                    continue;
                }
                if (must != WindowChoice.None)
                {
                    Take(s, must, chosen);
                    LastMandatory++;
                    continue;
                }
                // Back from behind an occluder with its cache: first among the renders past their caps (§4.8 "priority
                // boost"), within the budget plus the one over-budget render, not mandatory: several windows coming out
                // from behind a pillar in one frame must not render all at once past every budget.
                if ((s.Invalid & WindowInvalid.Reappeared) != 0)
                {
                    s.Overdue = float.MaxValue;
                    s.Reason = WindowChoice.Reappeared;
                    _overdue.Add(s);
                    continue;
                }
                // Its last readback saw nothing: no render by cap or error until the next one (one frame with the default
                // HiddenAfterZeroReadbacks), so renders stop with the occluder's first readback, not one frame after it;
                // the mandatory ones above still go. Win.Occlusion off: never.
                if (ZeroSeen(s, ctx))
                    continue;
                // 2. Past a cap: by how far past it.
                float ageMs = s.AgeMs(ctx.Now);
                bool hard = ageFrames >= caps.AgeCapFrames && ageMs >= caps.AgeCapMs;
                bool dynamic = (s.LifeInView > 0 || s.WeatherActive) && ageFrames >= caps.DynamicCapFrames;
                if (hard || dynamic)
                {
                    float byFrames = ageFrames / (float)Mathf.Max(1, dynamic ? caps.DynamicCapFrames : caps.AgeCapFrames);
                    float byMs = caps.AgeCapMs > 0f ? ageMs / caps.AgeCapMs : 0f;
                    s.Overdue = Mathf.Max(byFrames, byMs);
                    s.Reason = hard ? WindowChoice.HardCap : WindowChoice.DynamicCap;
                    _overdue.Add(s);
                    continue;
                }
                // 3. By error.
                if (s.ErrorPx >= caps.ThresholdPx)
                {
                    float vis = s.VisiblePx > 0 && s.VisibleFrame >= frame - _settings.ReadbackMaxAge ? s.VisiblePx : s.RectPx;
                    s.Priority = Mathf.Sqrt(Mathf.Max(vis, 1f)) * Mathf.Min(s.ErrorPx, 1e6f) / Mathf.Max(s.CpuEstimate, 0.05f);
                    _byError.Add(s);
                }
            }

            // Win.MandatoryCap: the head of the owed queue past the budget, as many as the frame's allowance gives; they
            // count as mandatory (the GPU floor below, the resolution that gives first).
            int owedNext = 0;
            if (_owed.Count > 0)
            {
                _owed.Sort(s_owedOrder);
                LastOwed = _owed.Count;
                if (!_spread)
                    while (owedNext < _owed.Count && TakeOverBudget(frame))
                    {
                        TakeOwed(_owed[owedNext++], chosen);
                        LastMandatory++;
                    }
                else
                    // Win.ShadowCost: a heavy one after a heavy one waits (the next in the queue takes its place); it keeps
                    // its place in the queue, and the first heavy one of a frame always goes, so it renders in a later frame.
                    for (int i = 0; i < _owed.Count && OverBudgetLeft(frame); i++)
                    {
                        WindowState s = _owed[i];
                        if (Stacks(s))
                            continue;
                        TakeOverBudget(frame);
                        TakeOwed(s, chosen);
                        LastMandatory++;
                    }
            }

            LastMandatoryGpuMs = _gpuMs;
            // The mandatory renders alone over the GPU budget: their resolution gives first, never the render.
            if (_gpuMs > b.GpuMs && b.GpuMs > 0f && !float.IsInfinity(b.GpuMs))
            {
                float f = Mathf.Clamp(Mathf.Sqrt(b.GpuMs / _gpuMs), _settings.DynResFloor, 1f);
                for (int i = 0; i < chosen.Count; i++)
                {
                    WindowState s = chosen[i];
                    if (s.Params.Tier == WindowTier.Live || s.Params.Tier == WindowTier.NearCached)
                    {
                        s.Params.ResolutionScale *= f;
                        s.RenderPx *= f * f;
                    }
                }
            }

            // Win.MandatoryCap: the rest of the owed queue within the budget, ahead of the renders past their caps (with
            // twenty windows those fill the budget every frame, a round-robin by age: a jump's windows behind it would
            // wait their turn there, the error model's renders never come). What does not fit shows its cache.
            for (int i = owedNext; i < _owed.Count; i++)
            {
                WindowState s = _owed[i];
                if (s.Chosen)
                    continue;
                if (Fits(s, b))
                {
                    TakeOwed(s, chosen);
                    continue;
                }
                s.Reason = WindowChoice.None;
                LastDeferred++;
                LabCapDeferred++;
            }

            // 2. Reappeared, then past their caps, most overdue first, within the budget; then up to OverBudget more beyond it
            // (Win.MandatoryCap: only while the frame's allowance lasts, which the owed queue spent first).
            _overdue.Sort(s_overdueOrder);
            int over = 0;
            for (int i = 0; i < _overdue.Count; i++)
            {
                WindowState s = _overdue[i];
                WindowChoice why = s.Reason;
                if (Fits(s, b))
                    Take(s, why, chosen);
                else if (over < b.OverBudget && (!cap || OverBudgetLeft(frame)) && !Stacks(s))
                {
                    if (cap)
                        TakeOverBudget(frame);
                    Take(s, why, chosen);
                    over++;
                    LastOverBudget++;
                }
                else
                {
                    s.Reason = WindowChoice.None;
                    continue;
                }
                if (why == WindowChoice.HardCap)
                    LastHardCap++;
                else if (why == WindowChoice.DynamicCap)
                    LastDynamicCap++;
            }

            // 3. By priority while the budget lasts (invalidated caches have E = ∞ and come first).
            _byError.Sort(s_priorityOrder);
            for (int i = 0; i < _byError.Count; i++)
            {
                WindowState s = _byError[i];
                if (Fits(s, b))
                    Take(s, float.IsInfinity(s.ErrorPx) ? WindowChoice.Invalid : WindowChoice.Error, chosen);
            }

            // 4. Guards, the most overdue first: within the budget, plus GuardOverBudget beyond it (the count cap and the
            // mandatory renders fill a busy frame every time, and a guard that never fits leaves an occluded window with a
            // cache of any age to show when it comes out). Win.MandatoryCap: beyond it only while the frame's allowance
            // lasts (a hidden window's guard after a jump waits behind the windows on screen).
            _guards.Sort(s_overdueOrder);
            int guardOver = 0;
            for (int i = 0; i < _guards.Count; i++)
            {
                WindowState s = _guards[i];
                if (Fits(s, b))
                    Take(s, WindowChoice.Guard, chosen);
                else if (guardOver < _settings.GuardOverBudget && (!cap || OverBudgetLeft(frame)) && !Stacks(s))
                {
                    if (cap)
                        TakeOverBudget(frame);
                    Take(s, WindowChoice.Guard, chosen);
                    guardOver++;
                }
                else
                    continue;
                s.LastGuard = ctx.Now;
                LastGuard++;
            }

            // 5. Render order: one group (far environment) after the other, in the order of each group's first render.
            Group(chosen);

            // What every window shows.
            bool resolve = On(WindowMechanism.Resolve);
            for (int i = 0; i < states.Count; i++)
            {
                WindowState s = states[i];
                s.Show = s.Chosen ? WindowShow.Fresh : ShowFor(s, resolve, ctx.Now, _settings.HiddenKeepCacheSeconds);
            }
        }

        // Stable: within a group the choice's order stays.
        internal void Group(List<WindowState> chosen)
        {
            if (chosen.Count < 2)
                return;
            _grouped.Clear();
            for (int i = 0; i < chosen.Count; i++)
            {
                int g = chosen[i].Group;
                bool placed = false;
                for (int j = 0; j < i && !placed; j++)
                    placed = chosen[j].Group == g;
                if (placed)
                    continue;
                for (int j = i; j < chosen.Count; j++)
                    if (chosen[j].Group == g)
                        _grouped.Add(chosen[j]);
            }
            chosen.Clear();
            chosen.AddRange(_grouped);
        }

        /// <summary>What a window that does not render this frame shows (§6 "Show for the rest", D7). `now`: Time.unscaledTime;
        /// `hiddenKeepSeconds`: the oldest cache an occluded window still shows.</summary>
        internal static WindowShow ShowFor(WindowState s, bool resolve, float now, float hiddenKeepSeconds)
        {
            if (!s.InFrustum || !s.Window.Enabled || s.Window.Partner == null)
                return WindowShow.Off;
            switch (s.Tier)
            {
                case WindowTier.Closed:
                    return WindowShow.Fallback;
                case WindowTier.Hidden:
                    // Occluded in the frustum: its cache as it was (no resolve cost), so it never reappears as the swirl;
                    // but never one older than HiddenKeepCacheSeconds (the guards keep it younger while they can): that
                    // is what shows, laid flat, for the two frames the readback needs to see the window again.
                    return s.Cache.Valid && now - s.Cache.Time <= hiddenKeepSeconds ? WindowShow.Reprojected : WindowShow.Fallback;
                default:
                    if (!s.Cache.Valid)
                        return WindowShow.Fallback;
                    return resolve ? WindowShow.Resolved : WindowShow.Reprojected;
            }
        }

        public TierParams ParamsFor(WindowTier tier, WindowState s, in WindowFrameContext ctx)
        {
            int t = (int)tier;
            TierParams p = t >= 0 && t < _settings.Effects.Length ? _settings.Effects[t] : TierParams.Legacy;
            p.Tier = tier;
            if (tier == WindowTier.NearCached)
            {
                // Half-resolution AO only on the larger near windows, and only with GPU headroom (DemoteNear: the GPU
                // budget saw the GPU bind); such a window keeps its slot camera (its AO history and previous view belong to it).
                // With the tiers' hysteresis: AO switched on loosens the cache (a render), so it must not flap at the edge.
                bool had = s != null && s.Cache.Valid && s.Cache.Ao;
                float min = _settings.AoNearMinShare * (had ? 1f - _settings.Hysteresis : 1f + _settings.Hysteresis);
                p.Ao = s != null && s.Share >= min && !ctx.Budget.DemoteNear;
                p.Sticky = p.Ao;
                // An AO render needs the border post-S1 gives it (Win.Margin: at least the SSAO's reach at the opening's
                // depth, at most a quarter of the rect): 4% of the rect is less than Amplify samples past it, which would
                // read clamped edge depth and draw a rim along the opening that the Live render does not have.
                if (p.Ao)
                    p.Margin = WindowMargin.Legacy;
            }
            // The tier's shadow caps where a shadow switch puts them in force for it (Win.ShadowTiers: every tier;
            // Win.ShadowTiersSmall: SmallCached alone), else the game's shadows.
            if (Manager == null || !Manager.ShadowCaps(tier))
            {
                p.ShadowDistanceCap = -1f;
                p.ShadowCascadesCap = -1;
                p.PointLightShadows = true;
            }
            if (!On(WindowMechanism.AoTierA))
            {
                // AO as post-S1: on every render (while the game's is on), full resolution, and with the border it needs
                // (an AO render without its margin samples past the rendered pixels along the window's inner edge).
                p.Ao = true;
                p.AoHalfRes = false;
                p.Margin = WindowMargin.Legacy;
            }
            if (On(WindowMechanism.AoTemporal) && tier == WindowTier.Live)
                p.AoTemporal = true;
            if (!On(WindowMechanism.Tiers))
            {
                // A tier derived by the legacy scheduler: its cadence and the effects their own switches control
                // (ShadowTiers, ShadowTiersSmall, AoTierA), otherwise the post-S1 render: its size, LOD selection and weather.
                p.ResolutionScale = 1f;
                p.Margin = WindowMargin.Legacy;
                p.LodBiasScale = 1f;
                p.WeatherParticles = true;
            }
            else if (tier == WindowTier.Live || tier == WindowTier.NearCached)
                p.ResolutionScale *= Mathf.Clamp(ctx.Budget.ResolutionFactor > 0f ? ctx.Budget.ResolutionFactor : 1f, 0.1f, 1f);
            return p;
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }

    public sealed partial class WindowState
    {
        // WindowScheduler: this frame's Live ranking (PrepareFrame) and its keys (with the incumbent's hysteresis), the
        // estimates Choose used, how far past its cap the cache is (or a guard past its period), the last visible tier (the
        // tier of a guard render) and when it was last in a visible tier (Time.unscaledTime; the slot pool's grace for
        // sticky slots), the coverage part of the error (px the opening reaches past the cached rect), and the opening's
        // pixel area by frame (& 7) for the "rect grew since the readback" test.
        internal bool LiveCandidate;
        internal int LiveRank = int.MaxValue;
        internal float LiveKeyDistance, LiveKeyShare;
        internal float CpuEstimate, GpuEstimate, Overdue;
        internal float RenderPx;
        internal WindowTier LastVisibleTier = WindowTier.NearCached;
        internal float VisibleAt = -100f;
        internal float ECov;
        internal readonly int[] RectPxRing = new int[8];
        // Win.MandatoryCap's queue: the last frame it was owed a render outside the budget (first show, jump, urgent) and
        // the first of that run of frames, whether only an urgent event made it owed, and its share over distance.
        internal int OwedFrame = -100, OwedSince = -100;
        internal bool OwedUrgentOnly;
        internal float OwedKey;
        // Win.ShadowCost: this choice found its render heavy (WindowCostModel.Heavy; never for Live, Crossing, the lab's
        // cadence or a render ahead).
        internal bool ShadowHeavy;
    }

    public sealed partial class WindowPipelineSettings
    {
        /// <summary>Guard renders per frame allowed beyond the budget and the count cap (the most overdue first).</summary>
        public int GuardOverBudget = 1;
        /// <summary>Win.MandatoryCap: renders per frame past the budget besides Live and Crossing (owed first shows, jumps
        /// and urgent events first, then the over-budget render past a cap and the guard's).</summary>
        public int MandatoryCapPerFrame = 2;
        /// <summary>Win.ShadowCost: a heavy render waits past the budget for a frame without another heavy one only while it
        /// is less than this many times past its cap (WindowState.Overdue).</summary>
        public float ShadowStackOverdue = 2f;
    }
}
