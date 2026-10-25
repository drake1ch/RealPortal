using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering;

namespace RealPortal
{
    // OWNER: sched (S3). The window frame loop that replaces PortalRenderer.RenderAll's (Docs\S2S3-CONTRACTS.md §4-§6).
    // Contract: IWindowManager in WindowContracts.cs.
    //
    // Three ways through a frame, picked at RenderChosen:
    // - Nothing of S2/S3 in force (every Win.* switch of the stage off, or its parts not Ready): PortalRenderer.RenderAll
    //   as post-S1, untouched (no state, no slot events, no readbacks).
    // - Win.Tiers in force: Plan (GameCamera.LateUpdate) classifies, scores and chooses; RenderChosen re-checks the
    //   geometry with the pose as it renders, renders the chosen windows (on slot cameras with Win.CameraSlots), resolves or
    //   lays flat the others, binds what each window shows, and has PortalRenderer build the game camera's passes.
    // - Otherwise (slots, resolve, occlusion, shadow tiers or the lean composite without tiers): RenderAll's own count
    //   scheduler, with this manager hooked into it (PortalRenderer.Hook): each window it renders gets a tier derived
    //   from its cadence (every frame Live, every 2nd NearCached, every 3rd SmallCached), its render fills the cache, and a
    //   window it shows stale is resolved instead of laid flat while Win.Resolve is in force.
    // In all of them PortalRenderer.RenderLevel raises the slot events for every on-screen render (PortalRenderer.Pipeline).
    public sealed partial class WindowManager : IWindowManager
    {
        static readonly List<PortalWindow> s_none = new List<PortalWindow>();
        static readonly Rect FullRect = new Rect(0f, 0f, 1f, 1f);
        static readonly double s_tickMs = 1000.0 / Stopwatch.Frequency;

        public PortalRenderer Renderer { get; }
        public WindowPipelineSettings Settings { get; }
        public IList<PortalWindow> Windows { get; set; }
        public IWindowWorld World { get; set; }

        public IWindowCameraPool Pool { get; }
        public IWindowScheduler Scheduler { get; }
        public IWindowCostModel Cost { get; }
        public IGpuBudget Gpu { get; }
        public IWindowVisibility Visibility { get; }
        public IWindowResolve Resolver { get; }
        public IWindowComposite Composite { get; }

        bool _disposed;
        public bool Ready => !_disposed;
        public bool WantsPlan => Ready && this.Effective(WindowMechanism.Tiers);

        readonly List<WindowState> _chosen = new List<WindowState>();
        readonly List<WindowState> _states = new List<WindowState>();
        public IReadOnlyList<WindowState> Chosen => _chosen;
        public IReadOnlyList<WindowState> States => _states;
        public WindowFrameStats Stats { get; } = new WindowFrameStats();

        // Every state the manager has seen and not yet seen go (to release its slot and id when its window leaves).
        readonly List<WindowState> _known = new List<WindowState>();
        // States whose PortalWindow fields point at this frame's resolve output (put back first thing next frame).
        readonly List<WindowState> _bound = new List<WindowState>();
        readonly Plane[] _planes = new Plane[6];

        // The plan of this frame.
        WindowFrameContext _ctx;
        int _plannedFrame = -100;
        Vector3 _planEye;
        Quaternion _planRot = Quaternion.identity;
        bool _hasPlanEye;
        float _planMs;
        // Counted at Plan, before RenderChosen resets the frame's stats.
        int _pendingTierChanges, _pendingDisoccluded, _pendingReappeared;
        // Last frame's window render ms and GPU estimate (the budgets' inputs at the next Plan), and this frame's so far.
        float _lastWindowCpuMs, _lastWindowGpuMs, _frameGpuMs;
        bool _tieredFrame;

        // Win.Prewarm: the window this frame's plan renders ahead (out of the view, taken within the budget) and that frame.
        WindowState _ahead;
        int _aheadFrame = -100;
        /// <summary>Win.Prewarm: windows rendered ahead this frame (0 or 1), and since start (lab).</summary>
        internal int LastAhead;
        internal static long LabAheadRenders;
        /// <summary>Win.CacheBudget: bytes of the windows' cached views after this frame's release, and the caches given
        /// back to stay within CacheBudgetMB since start (lab).</summary>
        internal long LastCacheBytes;
        internal static long LabCacheEvicted;

        // The game camera's AfterEverything buffer the visibility counts and readbacks are recorded into (when the composite
        // has not already done it this frame).
        readonly CommandBuffer _visibility = new CommandBuffer { name = "RealPortal window visibility" };
        Camera _visibilityOn;

        public WindowManager(PortalRenderer renderer, WindowPipelineSettings settings, WindowParts parts)
        {
            Renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
            Settings = settings ?? new WindowPipelineSettings();
            parts = parts ?? new WindowParts();
            Pool = parts.Pool;
            Scheduler = parts.Scheduler;
            Cost = parts.Cost;
            Gpu = parts.Gpu;
            Visibility = parts.Visibility;
            Resolver = parts.Resolver;
            Composite = parts.Composite;
            if (Pool != null)
            {
                Pool.Created += SlotCreated;
                Pool.Destroyed += SlotDestroyed;
            }
            if (Scheduler is WindowScheduler ws)
                ws.Manager = this;
            if (Cost is WindowCostModel wc)
                wc.Manager = this;
            renderer.Manager = this;
        }

        // The real parts (the concrete classes of the code map, each owner's file).
        public static WindowManager CreateDefault(PortalRenderer renderer, WindowPipelineSettings settings, WindowAssets assets)
        {
            settings = settings ?? new WindowPipelineSettings();
            var cost = new WindowCostModel(settings);
            var gpu = new GpuBudget(settings);
            var parts = new WindowParts
            {
                Pool = new WindowCameraPool(settings),
                Cost = cost,
                Gpu = gpu,
                Scheduler = new WindowScheduler(settings, cost, gpu),
                Visibility = new WindowVisibility(settings, assets != null ? assets.Count : null),
                Resolver = new WindowResolve(settings, assets != null ? assets.Resolve : null),
                Composite = new WindowComposite(settings, assets != null ? assets.Window : null),
            };
            return new WindowManager(renderer, settings, parts);
        }

        public WindowState StateOf(PortalWindow w) => w == null ? null : WindowState.For(w);

        bool PartReady(IWindowPart p) => p != null && p.Ready;

        // ================================================================================================================
        // Plan (GameCamera.LateUpdate postfix)
        // ================================================================================================================

        public void Plan(Camera main)
        {
            if (!WantsPlan || main == null)
                return;
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                PlanFrame(main, Vector2.zero, Time.frameCount);
            }
            finally
            {
                _planMs = (float)((Stopwatch.GetTimestamp() - t0) * s_tickMs);
            }
        }

        void PlanFrame(Camera main, Vector2 jitter, int frame)
        {
            IList<PortalWindow> windows = Windows ?? s_none;
            WindowFrameContext ctx = WindowFrameContext.From(main, jitter);
            // Carried through a portal or teleported since the last plan: no cached view lines up any more.
            ctx.Jumped = _hasPlanEye && (ctx.Eye - _planEye).sqrMagnitude > 4f;
            ctx.Budget = SampleBudgets(frame);
            Collect(frame);
            SyncStates(windows, frame);

            if (Scheduler is WindowScheduler ws)
            {
                ws.MinScreenFraction = Renderer.Settings.MinScreenFraction;
                ws.MinPixels = Renderer.Settings.MinPixels;
            }
            GeometryUtility.CalculateFrustumPlanes(ctx.VP, _planes);
            for (int i = 0; i < _states.Count; i++)
            {
                WindowState s = _states[i];
                Geometry(s, ctx, _planes, frame, true);
                s.PlanInFrustum = s.InFrustum;
                s.PlannedFrame = frame;
            }
            if (ctx.Jumped)
                for (int i = 0; i < _states.Count; i++)
                    if (_states[i].Cache.Valid)
                        _states[i].Invalid |= WindowInvalid.CameraJump;

            (Scheduler as WindowScheduler)?.PrepareFrame(_states, ctx);
            for (int i = 0; i < _states.Count; i++)
                ClassifyOne(_states[i], ctx, frame);
            for (int i = 0; i < _states.Count; i++)
            {
                WindowState s = _states[i];
                Invalidators(s, ctx);
                Scheduler.Error(s, ctx);
            }
            Scheduler.Choose(_states, ctx, _chosen);
            // Win.Prewarm: last, with what the budget has left after every other render.
            _ahead = null;
            if (WindowFlags.Prewarm && Scheduler is WindowScheduler sched)
                PlanAhead(sched, ctx, frame);

            _ctx = ctx;
            _plannedFrame = frame;
            _planEye = ctx.Eye;
            _planRot = ctx.Rotation;
            _hasPlanEye = true;
        }

        // The budgets of this frame from last frame's costs (§4.6 "Budgets"). Without Win.Budget: the count cap only.
        WindowBudget SampleBudgets(int frame)
        {
            var b = new WindowBudget
            {
                CpuMs = float.PositiveInfinity,
                GpuMs = float.PositiveInfinity,
                ResolutionFactor = 1f,
                MaxRenders = Mathf.Max(0, Settings.MaxRendersPerFrame),
                OverBudget = Mathf.Max(0, Settings.MaxOverBudgetRenders),
            };
            bool budget = this.Effective(WindowMechanism.Budget);
            float target = World != null ? World.TargetFrameMs : 16.7f;
            if (!(target > 0f))
                target = 16.7f;
            float frameMs = Time.unscaledDeltaTime * 1000f;
            float waitMs = 0f;
            if (Gpu != null)
            {
                if (budget)
                    Gpu.Install();
                else
                    Gpu.Uninstall();
                if (budget && Gpu.Ready)
                {
                    if (Gpu is GpuBudget g)
                    {
                        g.TargetFrameMs = target;
                        g.FrameMs = frameMs;
                        g.WindowGpuMs = _lastWindowGpuMs;
                        g.MandatoryGpuMs = Scheduler is WindowScheduler ws ? ws.LastMandatoryGpuMs : 0f;
                    }
                    Gpu.Sample(frame);
                    waitMs = Gpu is GpuBudget gb ? gb.LastWaitMs : 0f;
                }
            }
            // The main thread's own frame: what it spent waiting for the GPU's presentation is not CPU time.
            if (PartReady(Cost))
                Cost.RecordFrame(Mathf.Max(0f, frameMs - waitMs), _lastWindowCpuMs);
            if (!budget)
                return b;
            if (PartReady(Cost))
                b.CpuMs = Cost.CpuBudgetMs(target);
            if (Settings.GpuBudgetMs > 0f)
                b.GpuMs = Settings.GpuBudgetMs;
            else if (PartReady(Gpu))
                b.GpuMs = Gpu.BudgetMs;
            if (PartReady(Gpu))
            {
                b.ResolutionFactor = Mathf.Clamp(Gpu.ResolutionFactor, 0.1f, 1f);
                b.DemoteNear = Gpu.GpuBound;
            }
            return b;
        }

        void Collect(int frame)
        {
            if (!PartReady(Visibility))
                return;
            try
            {
                if (Visibility is WindowVisibility v && World != null)
                    v.WaterLevel = World.WaterLevel;
                Visibility.Collect(frame, _states);
            }
            catch (Exception e)
            {
                WindowLog.WarnEvery("WindowVisibility.Collect", 10f, "Window readbacks failed: " + e);
            }
        }

        // _states = one state per window of this frame's list; the ones that left go (slot, id, cache references).
        void SyncStates(IList<PortalWindow> windows, int frame)
        {
            _states.Clear();
            bool ids = PartReady(Visibility);
            for (int i = 0; i < windows.Count; i++)
            {
                PortalWindow w = windows[i];
                if (w == null)
                    continue;
                WindowState s = WindowState.For(w);
                if (s.SeenFrame == frame)
                    continue;
                s.SeenFrame = frame;
                _states.Add(s);
                if (!s.Known)
                {
                    s.Known = true;
                    s.PrevCanShow = false;
                    _known.Add(s);
                }
                if (ids && s.VisId == 0)
                    Visibility.Register(s);
            }
            for (int i = _known.Count - 1; i >= 0; i--)
            {
                WindowState s = _known[i];
                if (s.SeenFrame >= frame - 1)
                    continue;
                // Gone from the live list: its slot and id are free, the cache is not ours to show any more.
                _known.RemoveAt(i);
                s.Known = false;
                Pool?.Release(s);
                Visibility?.Unregister(s);
                s.OnWindowGone();
                s.Invalid |= WindowInvalid.NoCache | WindowInvalid.FirstShow;
                s.Tier = s.WantTier = WindowTier.Hidden;
                s.Show = s.PrevShow = WindowShow.Off;
                s.HiddenSince = -1f;
            }
        }

        // Rect, share, distance, frustum, and (facts) what only the game knows, as the main camera sees `s` with `ctx`.
        void Geometry(WindowState s, in WindowFrameContext ctx, Plane[] planes, int frame, bool facts)
        {
            PortalWindow w = s.Window;
            bool ok = Renderer.PrepareWindow(w, ctx.Main);
            if (!ok)
            {
                s.InFrustum = s.InRange = s.CanShow = s.Crossing = false;
                s.OpeningRect = s.Rect = default;
                s.OpeningSize = default;
                s.Share = 0f;
                s.RectPx = 0;
                s.RectPxRing[frame & 7] = 0;
                s.Distance = float.MaxValue;
                return;
            }
            s.Distance = Vector3.Distance(ctx.Eye, w.Frame.Center);
            float half = Mathf.Max(w.Frame.HalfSize.x, w.Frame.HalfSize.y);
            s.InRange = s.Distance <= Renderer.Settings.MaxDistance + half;
            bool inFrustum = GeometryUtility.TestPlanesAABB(planes, w.Renderer.bounds);
            Rect open = default;
            Vector2 extent = default;
            if (inFrustum && !Renderer.ScreenRectOf(w, ctx.VP, out open, out extent))
                inFrustum = false;
            s.InFrustum = inFrustum;
            s.OpeningRect = inFrustum ? open : default;
            s.OpeningSize = inFrustum ? extent : default;
            float area = s.OpeningRect.width * s.OpeningRect.height;
            // Lab (PortalRenderer.LabDistantRenders): in view, and only Win.DistantLive's reach or floor would let it render.
            if (inFrustum && WindowFlags.DistantLive && (s.Distance > Renderer.Settings.DistantFrom + half || area < Renderer.Settings.MinScreenFraction))
                w.DistantAtFrame = frame;
            float screenPx = Mathf.Max(1f, (float)ctx.ScreenW * ctx.ScreenH);
            s.RectPx = Mathf.RoundToInt(area * screenPx);
            s.RectPxRing[frame & 7] = s.RectPx;
            bool counted = this.Effective(WindowMechanism.Occlusion) && s.VisiblePx >= 0 && s.VisibleFrame >= frame - Settings.ReadbackMaxAge;
            s.Share = counted ? Mathf.Min(area, s.VisiblePx / screenPx) : area;
            if (!facts)
                return;
            IWindowWorld world = World;
            s.CanShow = world == null || world.CanShow(w);
            s.Crossing = world != null && world.Crossing(w);
            // The renderer's own grouping key when the plugin gave one (the same as RenderAll groups by), else the world's.
            Func<PortalWindow, int> groupOf = Renderer.Settings.GroupOf;
            s.Group = groupOf != null ? groupOf(w) : world != null ? world.GroupOf(w) : 0;
            // Only a window on screen has a cached view whose life matters (E_time, the dynamic cap of a visible tier).
            s.LifeInView = world != null && s.InFrustum ? world.LifeInView(s) : 0;
            s.WeatherActive = world != null && world.WeatherActive(w);
        }

        // Tier (and TierChanged), the invalidators a tier change brings, the render rect for the tier's margin, the
        // render's pixel count.
        void ClassifyOne(WindowState s, in WindowFrameContext ctx, int frame)
        {
            WindowTier before = s.Tier;
            Scheduler.Classify(s, ctx);
            bool visBefore = WindowScheduler.IsVisible(before), visNow = WindowScheduler.IsVisible(s.Tier);
            if (s.Tier != before)
            {
                _pendingTierChanges++;
                RaiseTierChanged(s, before, s.Tier);
            }
            // Occlusion bookkeeping: when it went hidden behind something (its first guard waits a full period).
            if (s.Tier == WindowTier.Hidden && s.InFrustum)
            {
                if (s.HiddenSince < 0f)
                {
                    s.HiddenSince = ctx.Now;
                    s.LastGuard = ctx.Now;
                }
            }
            else if (visNow && before == WindowTier.Hidden && s.HiddenSince >= 0f)
            {
                s.Invalid |= WindowInvalid.Reappeared;
                s.HiddenSince = -1f;
                _pendingReappeared++;
            }
            else
                s.HiddenSince = -1f;
            // First frame it can show: newly showable, or visible again without a cache. Back in the frustum with a cache
            // it shows that (resolved) and renders by its age caps, oldest first: a pan does not make every window that
            // comes into view render at once.
            if (visNow && s.CanShow && !s.PrevCanShow && s.Known)
                s.Invalid |= WindowInvalid.FirstShow;
            else if (visNow && !visBefore && !s.Cache.Valid)
                s.Invalid |= WindowInvalid.FirstShow;
            s.PrevCanShow = s.CanShow;
            if (visNow && s.Cache.Valid && Loosened(s))
                s.Invalid |= WindowInvalid.TierContent;
            RenderRect(s, ctx);
        }

        // The cache was rendered with less than the window's tier now has (to Live, AO or shadows loosened).
        static bool Loosened(WindowState s)
        {
            WindowCache c = s.Cache;
            TierParams p = s.Params;
            if (p.Tier < c.Tier)
                return true;
            if (p.Ao && !c.Ao)
                return true;
            if (p.PointLightShadows && !c.PointLightShadows)
                return true;
            float d0 = c.ShadowDistanceCap < 0f ? float.MaxValue : c.ShadowDistanceCap;
            float d1 = p.ShadowDistanceCap < 0f ? float.MaxValue : p.ShadowDistanceCap;
            int k0 = c.ShadowCascadesCap < 0 ? int.MaxValue : c.ShadowCascadesCap;
            int k1 = p.ShadowCascadesCap < 0 ? int.MaxValue : p.ShadowCascadesCap;
            return d1 > d0 + 0.5f || k1 > k0;
        }

        // The render rect: the opening plus the tier's margin, in size classes (as RenderAll makes it for Legacy).
        void RenderRect(WindowState s, in WindowFrameContext ctx)
        {
            if (!s.InFrustum || s.OpeningRect.width <= 0f)
            {
                s.Rect = default;
                s.RenderPx = 0f;
                return;
            }
            TierParams p = s.Params;
            s.Rect = Renderer.Settings.FullFrame ? FullRect : Renderer.RenderRect(s.Window, s.OpeningRect, p.Margin, p.MarginOfRect, ctx.Main);
            float res = Renderer.Settings.Resolution * Mathf.Clamp(p.ResolutionScale, 0.1f, 1f);
            s.RenderPx = s.Rect.width * ctx.ScreenW * res * s.Rect.height * ctx.ScreenH * res;
        }

        // The invalidators the manager sees itself (the plugin's come through Invalidate).
        void Invalidators(WindowState s, in WindowFrameContext ctx)
        {
            WindowCache c = s.Cache;
            if (!c.Valid)
            {
                s.Invalid |= WindowInvalid.NoCache;
                return;
            }
            PortalWindow w = s.Window;
            if (w.Partner != null && !Same(PortalMath.Pair(w.Frame, w.Partner.Frame), c.PairBase))
                s.Invalid |= WindowInvalid.PairMoved;
            if (c.RectPx > 0 && s.RectPx > c.RectPx * (1f + Settings.RegrowForce))
                s.Invalid |= WindowInvalid.Resized;
            if ((s.Invalid & WindowInvalid.Disoccluded) == 0 && s.VisibleFrame > c.Frame && s.VisiblePx > 0
                && s.DisoccludedPx > Settings.DisocclusionForce * s.VisiblePx)
            {
                s.Invalid |= WindowInvalid.Disoccluded;
                _pendingDisoccluded++;
            }
        }

        static bool Same(in Matrix4x4 a, in Matrix4x4 b)
        {
            for (int i = 0; i < 16; i++)
                if (Mathf.Abs(a[i] - b[i]) > 1e-3f)
                    return false;
            return true;
        }

        // ================================================================================================================
        // RenderChosen (the game camera's onPreCull)
        // ================================================================================================================

        public void RenderChosen(Camera main)
        {
            if (!Ready)
            {
                Renderer.RenderAll(main, Windows ?? s_none);
                return;
            }
            IList<PortalWindow> windows = Windows ?? s_none;
            int frame = Time.frameCount;
            // Last frame's resolve outputs go back; the windows show their own textures again until bound anew.
            RestoreBindings();
            BeginResolves();
            Stats.Reset(frame);
            LastAhead = 0;
            Stats.TierChanges = _pendingTierChanges;
            Stats.Disoccluded = _pendingDisoccluded;
            Stats.Reappeared = _pendingReappeared;
            _pendingTierChanges = _pendingDisoccluded = _pendingReappeared = 0;
            _tieredFrame = false;
            if (main == null)
            {
                Renderer.RenderAll(null, windows);
                DetachVisibility();
                SweepSlots(false);
                return;
            }
            bool tiers = this.Effective(WindowMechanism.Tiers);
            bool any = tiers || this.Effective(WindowMechanism.CameraSlots) || this.Effective(WindowMechanism.Resolve)
                       || this.Effective(WindowMechanism.Occlusion) || this.Effective(WindowMechanism.ShadowTiers)
                       || this.Effective(WindowMechanism.ShadowTiersSmall) || this.Effective(WindowMechanism.CompositeMRT)
                       || this.Effective(WindowMechanism.RestoreFromWindow);
            if (!tiers)
            {
                // Win.LiveFill reads the presentation wait as SampleBudgets does (LiveFillBudget): its hooks stay in.
                if (!any || !LiveFillOn)
                    Gpu?.Uninstall();
                _hasPlanEye = false;
            }
            if (!any)
            {
                // Nothing of S2/S3 in force: the post-S1 renderer, untouched (and no window marked as showing a resolve).
                // Slot cameras made while the pipeline ran go (nothing renders with them any more).
                DetachVisibility();
                ClearMaterialMarks(windows);
                _chosen.Clear();
                SweepSlots(false);
                Renderer.RenderAll(main, windows);
                Stats.Renders = Renderer.RendersLastFrame;
                RaiseFrameDone();
                return;
            }
            Renderer.Pipeline = this;
            try
            {
                if (tiers)
                    RenderTiered(main, windows, frame);
                else
                    RenderLegacy(main, windows, frame);
            }
            finally
            {
                Renderer.Pipeline = null;
                Renderer.Hook = null;
            }
            ScheduleVisibility(main);
            FinishFrame(frame);
        }

        void BeginResolves()
        {
            if (Resolver == null)
                return;
            try
            {
                if (Resolver is WindowResolve r)
                    r.Resolution = Renderer.Settings.Resolution;
                Resolver.BeginFrame();
            }
            catch (Exception e)
            {
                WindowLog.WarnEvery("WindowResolve.BeginFrame", 10f, "Window resolve begin failed: " + e);
            }
        }

        // ---- Win.Tiers ---------------------------------------------------------------------------------------------------

        void RenderTiered(Camera main, IList<PortalWindow> windows, int frame)
        {
            _tieredFrame = true;
            Vector2 jitter = Renderer.Settings.Jitter;
            if (_plannedFrame != frame)
            {
                // Plan did not run this frame (no LateUpdate postfix, or the switch flipped in between): plan here.
                long t0 = Stopwatch.GetTimestamp();
                PlanFrame(main, jitter, frame);
                _planMs = (float)((Stopwatch.GetTimestamp() - t0) * s_tickMs);
            }
            CopyPlanStats();
            SweepSlots(false);
            if (!Renderer.BeginFrame(main, windows))
            {
                // No window could show (the renderer took down its passes): nothing to render or bind.
                for (int i = 0; i < _states.Count; i++)
                    _states[i].Show = WindowShow.Off;
                return;
            }

            // Re-plan: the geometry as the camera renders. The choice stays, except that a chosen window that left the
            // frustum does not render and one that came into it (or into the list) without a cache renders now. When the
            // pose has not moved since Plan (ReplanMeters/ReplanDegrees) Plan's geometry of this frame stands: only windows
            // it did not see are measured.
            WindowFrameContext rc = WindowFrameContext.From(main, jitter);
            rc.Budget = _ctx.Budget;
            rc.Jumped = _ctx.Jumped;
            bool moved = (rc.Eye - _planEye).sqrMagnitude > Settings.ReplanMeters * Settings.ReplanMeters
                         || Quaternion.Angle(rc.Rotation, _planRot) > Settings.ReplanDegrees;
            if (moved)
                Stats.Replans++;
            ResyncAtRender(windows, frame);
            Plane[] planes = Renderer.FramePlanes;
            bool resolve = this.Effective(WindowMechanism.Resolve);
            bool added = false;
            for (int i = 0; i < _states.Count; i++)
            {
                WindowState s = _states[i];
                bool planned = s.PlannedFrame == frame;
                if (planned && !moved)
                    continue;
                bool wasIn = s.PlanInFrustum;
                Geometry(s, rc, planes, frame, !planned);
                if (!planned)
                {
                    // Not in this frame's plan: whatever it was decided last time it was planned does not hold.
                    s.Chosen = false;
                    s.Reason = WindowChoice.None;
                    s.PlannedFrame = frame;
                    s.PlanInFrustum = s.InFrustum;
                    ClassifyOne(s, rc, frame);
                    Invalidators(s, rc);
                    Scheduler.Error(s, rc);
                    added |= ChooseLate(s, resolve);
                    continue;
                }
                if (wasIn == s.InFrustum)
                {
                    RenderRect(s, rc);
                    continue;
                }
                ClassifyOne(s, rc, frame);
                if (!s.InFrustum)
                {
                    if (s.Chosen)
                    {
                        s.Chosen = false;
                        _chosen.Remove(s);
                    }
                    s.Show = WindowShow.Off;
                }
                else
                {
                    Invalidators(s, rc);
                    Scheduler.Error(s, rc);
                    added |= ChooseLate(s, resolve);
                }
            }
            if (added && Scheduler is WindowScheduler ws)
                ws.Group(_chosen);
            // The resolves below use this frame's pose (TAA jitter included) either way.
            _ctx = rc;

            RenderChosenList(frame);
            if (_ahead != null)
                RenderAhead(frame);
            ShowStale(resolve);
            ReleaseHidden();
            Renderer.EndFrame(main, windows, false);
        }

        // A window planned at RenderChosen: renders now if it must (Live, crossing, nothing to show, a cache from before a
        // camera jump), else shows its cache.
        bool ChooseLate(WindowState s, bool resolve)
        {
            bool must = WindowScheduler.IsVisible(s.Tier) && (s.Tier == WindowTier.Live || s.Crossing || !s.Cache.Valid
                                                             || (s.Invalid & (WindowInvalid.FirstShow | WindowInvalid.CameraJump)) != 0);
            // Win.MandatoryCap: a first show or a jump renders here only while the frame's allowance lasts (the counter
            // Choose spent first); otherwise it shows its cache (or the swirl) with its flags kept, and the next Plan
            // queues it. Live and Crossing render as ever.
            if (must && s.LabEvery <= 0 && !s.Chosen && WindowFlags.MandatoryCap && s.Tier != WindowTier.Live && !s.Crossing
                && Scheduler is WindowScheduler ws && !ws.TakeOverBudget(_ctx.Frame))
            {
                must = false;
                ws.LastDeferred++;
                WindowScheduler.LabCapDeferred++;
            }
            if (must && s.LabEvery <= 0 && !s.Chosen)
            {
                s.Chosen = true;
                s.Reason = s.Tier == WindowTier.Live ? WindowChoice.Live
                           : !s.Cache.Valid || (s.Invalid & WindowInvalid.FirstShow) != 0 ? WindowChoice.FirstShow : WindowChoice.Invalid;
                s.Show = WindowShow.Fresh;
                _chosen.Add(s);
                Stats.Mandatory++;
                return true;
            }
            if (!s.Chosen)
                s.Show = WindowScheduler.ShowFor(s, resolve, _ctx.Now, Settings.HiddenKeepCacheSeconds);
            return false;
        }

        // The list may have changed since Plan (links come and go): states for the windows of this call.
        void ResyncAtRender(IList<PortalWindow> windows, int frame)
        {
            bool same = windows.Count == _states.Count;
            for (int i = 0; same && i < windows.Count; i++)
                same = windows[i] != null && windows[i].State == _states[i];
            if (same)
                return;
            for (int i = 0; i < _states.Count; i++)
                _states[i].SeenFrame = frame - 1;
            SyncStates(windows, frame);
            // A chosen window that left the list is not rendered.
            for (int i = _chosen.Count - 1; i >= 0; i--)
                if (_chosen[i].SeenFrame != frame)
                {
                    _chosen[i].Chosen = false;
                    _chosen.RemoveAt(i);
                }
        }

        void RenderChosenList(int frame)
        {
            PortalRenderer r = Renderer;
            bool grouped = WindowFlags.EnvGroups && (r.BeforeGroup != null || r.AfterGroup != null);
            PortalWindow openGroup = null;
            try
            {
                for (int i = 0; i < _chosen.Count; i++)
                {
                    WindowState s = _chosen[i];
                    PortalWindow w = s.Window;
                    if (grouped && (i == 0 || s.Group != _chosen[i - 1].Group))
                    {
                        openGroup = w;
                        r.BeforeGroup?.Invoke(w);
                    }
                    w.ShownAtFrame = frame;
                    long t0 = Stopwatch.GetTimestamp();
                    RenderTexture tex = r.RenderShown(w, s.Rect);
                    Rendered(s, tex, t0);
                    if (grouped && (i == _chosen.Count - 1 || s.Group != _chosen[i + 1].Group))
                    {
                        openGroup = null;
                        r.AfterGroup?.Invoke(w);
                    }
                }
            }
            finally
            {
                if (openGroup != null)
                    r.AfterGroup?.Invoke(openGroup);
            }
        }

        // A render of `s` finished (tex null: its rect snapped to nothing): the new view becomes its cache.
        void Rendered(WindowState s, RenderTexture tex, long t0)
        {
            double ms = (Stopwatch.GetTimestamp() - t0) * s_tickMs;
            PortalWindow w = s.Window;
            Stats.WindowCpuMs += (float)ms;
            if (tex == null)
            {
                s.Show = WindowShow.Fallback;
                return;
            }
            WindowCache c = s.Cache;
            c.Color = w.Texture;
            c.Depth = w.DepthTexture;
            c.Normal = w.NormalTexture;
            s.LastErrorPx = s.ErrorPx;
            Stats.InvalidSeen |= s.Invalid;
            s.Invalid = WindowInvalid.None;
            s.Show = WindowShow.Fresh;
            Stats.Renders++;
            int t = (int)c.Tier;
            if (t >= 0 && t < Stats.RendersByTier.Length)
                Stats.RendersByTier[t]++;
            if (s.Reason == WindowChoice.Guard)
                s.LastGuard = Time.unscaledTime;
            _frameGpuMs += s.GpuEstimate;
            if (PartReady(Cost) && t <= (int)WindowTier.SmallCached)
                Cost.RecordRender(s, c.Tier, ms, c.Width * c.Height);
        }

        // ---- Win.Prewarm: a render ahead of the view ----------------------------------------------------------------------

        // What a window is rendered ahead for: no cache, or one a camera jump made wrong (no cached view lines up). Either
        // way it would be a first show (or a jump render) past the budget in the frame it comes into view.
        const WindowInvalid AheadWants = WindowInvalid.FirstShow | WindowInvalid.CameraJump;
        // A window whose render ahead could not be made (no rect in the turned view, nothing rendered) waits this many
        // frames before it is a candidate again, so it does not hold the one render a frame from the others.
        const int AheadRetryFrames = 30;

        // The window just outside the view that comes into it first with nothing to show: at most PrewarmConeDegrees past
        // the frustum's edge, showable, in range and not so small it would be the swirl in view; the least angle past the
        // edge first, then the nearest. It is rendered at the tier it will most likely have in view (by distance, as
        // Classify), as if the camera turned to face it, and only if it fits what the budget has left after every other
        // render (TakeAhead): one a frame at most, never a mandatory render. A pan towards a ring of windows then finds
        // their caches made over the frames before.
        void PlanAhead(WindowScheduler sched, in WindowFrameContext ctx, int frame)
        {
            Camera main = ctx.Main;
            if (main == null)
                return;
            float halfV = main.fieldOfView * 0.5f;
            float halfH = Mathf.Atan(Mathf.Tan(halfV * Mathf.Deg2Rad) * main.aspect) * Mathf.Rad2Deg;
            float cone = Mathf.Max(0f, Settings.PrewarmConeDegrees);
            float minPx = Renderer.Settings.MinScreenFraction * ctx.ScreenW * ctx.ScreenH;
            // Win.DistantLive (Docs\PARITY-PLAN.md P2): the pixel floor instead, as Classify will judge it once in view.
            bool floor = WindowFlags.DistantLive;
            float minSide = Renderer.Settings.MinPixels;
            Vector3 right = ctx.Rotation * Vector3.right, up = ctx.Rotation * Vector3.up;
            WindowState best = null;
            float bestPast = float.MaxValue;
            for (int i = 0; i < _states.Count; i++)
            {
                WindowState s = _states[i];
                if (s.InFrustum || !s.CanShow || !s.InRange || s.Crossing || s.LabEvery > 0 || s.LabTier.HasValue
                    || s.AheadRetryFrame > frame)
                    continue;
                if (s.Cache.Valid && (s.Invalid & AheadWants) == 0)
                    continue;
                PortalFrame f = s.Window.Frame;
                Vector3 d = f.Center - ctx.Eye;
                float dist = d.magnitude;
                if (dist < 0.1f)
                    continue;
                // Its size on screen when faced at this distance (small-angle): below MinScreenFraction (or the pixel floor)
                // it is the swirl.
                float hx = f.HalfSize.x / dist * ctx.FocalPx, hy = f.HalfSize.y / dist * ctx.FocalPx;
                if (floor ? 2f * hx < minSide || 2f * hy < minSide : 4f * hx * hy < minPx)
                    continue;
                // How far its centre is past the frustum's side planes, less its own angular radius.
                float x = Vector3.Dot(d, right), y = Vector3.Dot(d, up), z = Vector3.Dot(d, ctx.Forward);
                float radius = Mathf.Asin(Mathf.Min(1f, Mathf.Max(f.HalfSize.x, f.HalfSize.y) / dist)) * Mathf.Rad2Deg;
                float past = Mathf.Max(Mathf.Atan2(Mathf.Abs(x), z) * Mathf.Rad2Deg - halfH,
                                       Mathf.Atan2(Mathf.Abs(y), z) * Mathf.Rad2Deg - halfV) - radius;
                if (past > cone)
                    continue;
                past = Mathf.Max(past, 0f);
                if (best == null || past < bestPast || (past == bestPast && s.Distance < best.Distance))
                {
                    best = s;
                    bestPast = past;
                }
            }
            if (best == null)
                return;
            WindowTier tier = best.Distance >= Settings.SmallFromDistance || ctx.Budget.DemoteNear ? WindowTier.SmallCached : WindowTier.NearCached;
            TierParams before = best.Params;
            int beforeFrame = best.ParamsFrame;
            best.Params = sched.ParamsFor(tier, best, ctx);
            best.ParamsFrame = frame;
            if (AheadPose(best, ctx, out _, out _, out Rect rect))
            {
                float res = Renderer.Settings.Resolution * Mathf.Clamp(best.Params.ResolutionScale, 0.1f, 1f);
                best.RenderPx = rect.width * ctx.ScreenW * res * rect.height * ctx.ScreenH * res;
                if (sched.TakeAhead(best, ctx.Budget))
                {
                    _ahead = best;
                    _aheadFrame = frame;
                    return;
                }
            }
            else
                best.AheadRetryFrame = frame + AheadRetryFrames;
            // Not this frame: as Classify left it.
            best.Params = before;
            best.ParamsFrame = beforeFrame;
            best.RenderPx = 0f;
        }

        // The pose of a render ahead of `s`: the camera at the eye of `ctx` turned to face the window (its roll kept), the
        // view-projection the renderer's frame would have with it (non-jittered), and the window's render rect in that view
        // (its tier's margin and size class). A cached view is a set of rays through the opening from that eye, so it
        // resolves onto the window's rect wherever the window is on screen once the player has turned to it.
        bool AheadPose(WindowState s, in WindowFrameContext ctx, out Quaternion rot, out Matrix4x4 vp, out Rect rect)
        {
            PortalWindow w = s.Window;
            Camera main = ctx.Main;
            rot = Quaternion.LookRotation(w.Frame.Center - ctx.Eye, ctx.Rotation * Vector3.up);
            Matrix4x4 view = Matrix4x4.TRS(ctx.Eye, rot, new Vector3(1f, 1f, -1f)).inverse;
            vp = Matrix4x4.Perspective(main.fieldOfView, main.aspect, main.nearClipPlane, main.farClipPlane) * view;
            rect = default;
            if (!Renderer.ScreenRectOf(w, vp, out Rect open) || open.width <= 0f || open.height <= 0f)
                return false;
            TierParams p = s.Params;
            rect = Renderer.Settings.FullFrame ? FullRect : Renderer.RenderRect(w, open, p.Margin, p.MarginOfRect, main);
            return rect.width > 0f && rect.height > 0f;
        }

        // The render ahead this frame's plan took, at RenderChosen's pose (the camera may have moved since Plan): skipped
        // when the window came into the frustum meanwhile (there it renders as a first show) or has something to show by
        // now. Last of the frame's renders, inside its own render group; its view becomes the window's cache, but nothing
        // of it shows this frame.
        void RenderAhead(int frame)
        {
            WindowState s = _ahead;
            _ahead = null;
            if (_aheadFrame != frame || !WindowFlags.Prewarm || s.SeenFrame != frame || s.InFrustum || s.Chosen || !s.CanShow
                || s.Window.Partner == null || (s.Cache.Valid && (s.Invalid & AheadWants) == 0))
                return;
            if (!AheadPose(s, _ctx, out Quaternion rot, out Matrix4x4 vp, out Rect rect))
            {
                s.AheadRetryFrame = frame + AheadRetryFrames;
                return;
            }
            PortalRenderer r = Renderer;
            PortalWindow w = s.Window;
            // The render it would have owed when it came into view, done ahead.
            s.Reason = !s.Cache.Valid || (s.Invalid & WindowInvalid.FirstShow) != 0 ? WindowChoice.FirstShow : WindowChoice.Invalid;
            bool grouped = WindowFlags.EnvGroups && (r.BeforeGroup != null || r.AfterGroup != null);
            if (grouped)
                r.BeforeGroup?.Invoke(w);
            try
            {
                long t0 = Stopwatch.GetTimestamp();
                RenderTexture tex = r.RenderAhead(w, rot, vp, rect);
                Rendered(s, tex, t0);
                if (tex == null)
                    s.AheadRetryFrame = frame + AheadRetryFrames;
            }
            finally
            {
                if (grouped)
                    r.AfterGroup?.Invoke(w);
            }
            // Not on screen this frame; its cache counts as just used (Win.CacheBudget's LRU, the 10 s release).
            s.Show = WindowShow.Off;
            s.UsedTime = Time.unscaledTime;
            LastAhead++;
            LabAheadRenders++;
        }

        // Windows that do not render this frame: resolved onto their rect now, or laid flat as rendered, or the swirl.
        void ShowStale(bool resolve)
        {
            for (int i = 0; i < _states.Count; i++)
            {
                WindowState s = _states[i];
                if (s.Chosen)
                    continue;
                PortalWindow w = s.Window;
                if (s.Show == WindowShow.Resolved)
                {
                    if (resolve && TryResolve(s))
                        continue;
                    s.Show = WindowShow.Reprojected;
                }
                if (s.Show == WindowShow.Reprojected)
                {
                    if (s.Cache.Valid && w.Texture != null)
                        Renderer.ShowStale(w, true);
                    else
                        s.Show = WindowShow.Fallback;
                }
            }
        }

        bool TryResolve(WindowState s)
        {
            if (!PartReady(Resolver) || !s.Cache.Valid)
                return false;
            bool ok;
            try
            {
                ok = Resolver.Resolve(s, _ctx);
            }
            catch (Exception e)
            {
                WindowLog.WarnEvery("WindowResolve.Resolve", 10f, "Window resolve failed: " + e);
                ok = false;
            }
            if (!ok || s.ResolvedColor == null)
                return false;
            BindResolved(s);
            Renderer.ShowStale(s.Window, false);
            s.Show = WindowShow.Resolved;
            return true;
        }

        // The window shows this frame's resolve output (the composite passes read only the PortalWindow fields).
        void BindResolved(WindowState s)
        {
            PortalWindow w = s.Window;
            w.Texture = s.ResolvedColor;
            w.TextureRect = s.ResolvedRect;
            w.DepthTexture = s.ResolvedDepth;
            w.NormalTexture = s.ResolvedNormal;
            // The resolved depth is along the camera that would render now: its view scale now.
            PortalMath.View(w.Frame, w.Partner.Frame, _ctx.Eye, out float viewScale);
            w.DepthScale = 1f / Mathf.Max(viewScale, 1e-4f);
            w.NormalRotation = Matrix4x4.Rotate(PortalMath.Rotation(PortalMath.Pair(w.Partner.Frame, w.Frame)));
            s.BoundResolved = true;
            _bound.Add(s);
        }

        // Start of a frame: windows bound to last frame's resolve output show their cache again (as rendered).
        void RestoreBindings()
        {
            for (int i = 0; i < _bound.Count; i++)
            {
                WindowState s = _bound[i];
                if (!s.BoundResolved)
                    continue;
                s.BoundResolved = false;
                PortalWindow w = s.Window;
                WindowCache c = s.Cache;
                w.Texture = c.Color;
                w.DepthTexture = c.Depth;
                w.NormalTexture = c.Normal;
                w.TextureRect = c.Rect;
                w.DepthScale = 1f / Mathf.Max(c.ViewScale, 1e-4f);
            }
            _bound.Clear();
        }

        // Win.Tiers: a cache out of sight (not shown) for HiddenKeepCacheSeconds goes back to the pool (Win.CacheBudget: by
        // memory instead, ReleaseOverBudget).
        void ReleaseHidden()
        {
            float now = Time.unscaledTime;
            if (WindowFlags.CacheBudget)
            {
                ReleaseOverBudget(now);
                return;
            }
            for (int i = 0; i < _states.Count; i++)
            {
                WindowState s = _states[i];
                if (Shows(s.Show))
                    s.UsedTime = now;
                else if (s.Window.Owned.Count > 0 && now - s.UsedTime > Settings.HiddenKeepCacheSeconds)
                    s.Window.ReleaseOwned();
            }
        }

        // Win.CacheBudget: caches out of sight are kept while every window's cache together fits CacheBudgetMB; over it the
        // one shown longest ago goes first. A window turned back to after more than 10 s then shows its cache resolved and
        // renders by its age caps within the budget, not as a first show past it (a pan across a ring of twenty windows was
        // a burst of first shows). Shown caches and one rendered this frame (ahead) are never given back.
        void ReleaseOverBudget(float now)
        {
            int frame = Time.frameCount;
            long total = 0;
            for (int i = 0; i < _states.Count; i++)
            {
                WindowState s = _states[i];
                if (Shows(s.Show))
                    s.UsedTime = now;
                total += CacheBytes(s);
            }
            long budget = (long)(Mathf.Max(0f, Settings.CacheBudgetMB) * (1 << 20));
            while (total > budget)
            {
                WindowState oldest = null;
                for (int i = 0; i < _states.Count; i++)
                {
                    WindowState s = _states[i];
                    if (Shows(s.Show) || s.Window.Owned.Count == 0 || s.Cache.Frame == frame)
                        continue;
                    if (oldest == null || s.UsedTime < oldest.UsedTime)
                        oldest = s;
                }
                if (oldest == null)
                    break;
                total -= CacheBytes(oldest);
                oldest.Window.ReleaseOwned();
                LabCacheEvicted++;
            }
            LastCacheBytes = total;
        }

        // The bytes a window's textures hold (its view, depth and normals), counted again only after a render: they change
        // with a render or a release, and a release empties the list.
        static long CacheBytes(WindowState s)
        {
            List<RenderTexture> owned = s.Window.Owned;
            if (owned.Count == 0)
                return 0;
            if (s.CacheBytesFrame != s.Cache.Frame)
            {
                long bytes = 0;
                for (int i = 0; i < owned.Count; i++)
                    if (owned[i] != null)
                        bytes += WindowRTPool.Bytes(owned[i]);
                s.CacheBytes = bytes;
                s.CacheBytesFrame = s.Cache.Frame;
            }
            return s.CacheBytes;
        }

        static bool Shows(WindowShow show) => show == WindowShow.Fresh || show == WindowShow.Resolved || show == WindowShow.Reprojected;

        void CopyPlanStats()
        {
            Stats.PlanMs = _planMs;
            Stats.CpuBudgetMs = _ctx.Budget.CpuMs;
            Stats.GpuBudgetMs = _ctx.Budget.GpuMs;
            Stats.ResolutionFactor = _ctx.Budget.ResolutionFactor;
            Stats.GpuWaitMs = Gpu != null ? Gpu.WaitMsEma : 0f;
            if (Scheduler is WindowScheduler ws)
            {
                Stats.Mandatory = ws.LastMandatory;
                Stats.OverBudget = ws.LastOverBudget;
                Stats.Guard = ws.LastGuard;
                Stats.HardCap = ws.LastHardCap;
                Stats.DynamicCap = ws.LastDynamicCap;
            }
        }

        // ---- Legacy scheduler with hooks (Win.Tiers not in force) ----------------------------------------------------

        void RenderLegacy(Camera main, IList<PortalWindow> windows, int frame)
        {
            _ctx = WindowFrameContext.From(main, Renderer.Settings.Jitter);
            _ctx.Budget = new WindowBudget
            {
                CpuMs = float.PositiveInfinity,
                GpuMs = float.PositiveInfinity,
                ResolutionFactor = 1f,
            };
            Collect(frame);
            SyncStates(windows, frame);
            _chosen.Clear();
            for (int i = 0; i < _states.Count; i++)
            {
                WindowState s = _states[i];
                s.Chosen = false;
                s.Reason = WindowChoice.None;
                s.Show = WindowShow.Off;
                s.InFrustum = false;
            }
            // The pose this frame's counts will have been taken with (LegacyOccluded compares the camera against it).
            (Visibility as WindowVisibility)?.NotePose(frame, _ctx.Eye, _ctx.Rotation);
            SweepSlots(false);
            LiveFillBudget();
            Renderer.Hook = this;
            Renderer.RenderAll(main, windows);
        }

        // FARWORLD-V3 §4.8 on the slot path (Win.Occlusion in force, the count scheduler): `s` (this frame's rect set) is
        // hidden behind something by the conservative rule (WindowVisibility.HiddenNow), it has a view to show meanwhile,
        // nobody stands in its opening and the lab does not pace it. Before V0 the count scheduler rendered occluded windows
        // like any other (the counts only fed the tiers); now it skips them only while it is sure, and a window the rule
        // lets go renders on that frame (hidden for over 6 frames, the count scheduler's "kept" test fails and it is due
        // every frame; hidden for less, it is past its cadence by then). With Win.Tiers switched on (Window.Quality=
        // Performance) the count scheduler is only the fallback for tiers that cannot run, and stays as it was.
        bool LegacyOccluded(WindowState s, PortalWindow w, int frame)
        {
            if (WindowFlags.Tiers || w.Texture == null || s.LabEvery > 0 || s.LabTier.HasValue || !this.Effective(WindowMechanism.Occlusion))
                return false;
            // Settings.HiddenMaxFrames (lab-tunable, 0: off): hidden longer than that, it renders once anyway, so the view
            // a moving occluder uncovers (released by the readback 2 frames late) is never older than that. A deviation
            // from §4.8 (a guard render every N frames per hidden window), on only if ghostab's occluder removal says so.
            if (Settings.HiddenMaxFrames > 0 && frame - w.RenderedAtFrame > Settings.HiddenMaxFrames)
                return false;
            if (!(Visibility is WindowVisibility v) || !v.HiddenNow(s, frame, _ctx.Eye, _ctx.Rotation))
                return false;
            return World == null || !World.Crossing(w);
        }

        /// <summary>RenderAll (hooked): `w` is hidden behind something this frame (the slot path's §4.8 rule): it does not
        /// render and shows its last view as it was. Whatever else promotes a window to render this frame (Win.LiveFill)
        /// leaves such a window alone.</summary>
        internal bool LegacyHidden(PortalWindow w)
        {
            WindowState s = w != null ? w.State : null;
            return s != null && s.LegacyHiddenFrame == _ctx.Frame;
        }

        /// <summary>Some window was hidden behind something by the slot path's §4.8 rule this frame (it may leave frames
        /// without any window render, as the cached tiers do: the plugin's LOD cross-fade hold goes by it).</summary>
        internal bool OccludedThisFrame => _legacyHiddenFrame == Time.frameCount;
        int _legacyHiddenFrame = -100;

        /// <summary>RenderAll (hooked): `w` passed its tests and will render or show stale this frame.</summary>
        internal void LegacyCandidate(PortalWindow w, Rect opening, Rect rect, float share, float distance, int every)
        {
            try
            {
                WindowState s = WindowState.For(w);
                int frame = _ctx.Frame;
                s.InFrustum = s.InRange = s.CanShow = s.PrevCanShow = true;
                s.Distance = distance;
                s.OpeningRect = opening;
                s.Rect = rect;
                s.Share = share;
                s.RectPx = Mathf.RoundToInt(opening.width * opening.height * _ctx.ScreenW * _ctx.ScreenH);
                s.RectPxRing[frame & 7] = s.RectPx;
                s.RectPxFrame[frame & 7] = frame;
                s.PlannedFrame = frame;
                // The tier the count scheduler's cadence amounts to (or the lab's); Hidden behind something by §4.8's rule.
                bool hidden = LegacyOccluded(s, w, frame);
                s.LegacyHiddenFrame = hidden ? frame : -100;
                if (hidden)
                    _legacyHiddenFrame = frame;
                WindowTier tier = every <= 1 ? WindowTier.Live : every == 2 ? WindowTier.NearCached : WindowTier.SmallCached;
                if (s.LabTier.HasValue && WindowScheduler.IsVisible(s.LabTier.Value))
                    tier = s.LabTier.Value;
                if (hidden)
                    tier = WindowTier.Hidden;
                if (tier != s.Tier)
                {
                    WindowTier before = s.Tier;
                    s.PrevTier = before;
                    s.Tier = s.WantTier = tier;
                    s.TierSince = _ctx.Now;
                    Stats.TierChanges++;
                    RaiseTierChanged(s, before, tier);
                }
                if (!hidden)
                {
                    s.LastVisibleTier = tier;
                    s.VisibleAt = _ctx.Now;
                }
                // A hidden one renders nothing: the parameters of the tier it had last (as the tiers keep them).
                WindowTier pt = hidden ? s.LastVisibleTier : tier;
                if (PartReady(Scheduler))
                    s.Params = Scheduler.ParamsFor(pt, s, _ctx);
                else
                {
                    s.Params = TierParams.Legacy;
                    s.Params.Tier = pt;
                }
                s.ParamsFrame = frame;
                s.Show = WindowShow.Fresh;
            }
            catch (Exception e)
            {
                WindowLog.WarnEvery("WindowManager.LegacyCandidate", 10f, "Window state update failed: " + e);
            }
        }

        /// <summary>RenderAll (hooked): whether `w` renders this frame, given the count scheduler's answer (lab cadence wins;
        /// a window hidden behind something by §4.8's rule does not).</summary>
        internal bool LegacyDue(PortalWindow w, bool due)
        {
            WindowState s = w.State;
            if (s == null)
                return due;
            if (s.LabEvery <= 0 || w.Texture == null)
                return due && s.LegacyHiddenFrame != _ctx.Frame;
            return (_ctx.Frame - s.LabFrom) % s.LabEvery == 0;
        }

        /// <summary>RenderAll (hooked): `w` shows its last render this frame. True: shown resolved (bound and kept by the
        /// renderer's frusta); false: the renderer lays it flat as post-S1 (always for a hidden one: nothing of it is seen,
        /// so no resolve is spent on it).</summary>
        internal bool LegacyStale(PortalWindow w)
        {
            WindowState s = w.State;
            if (s == null)
                return false;
            s.Show = WindowShow.Reprojected;
            if (s.LegacyHiddenFrame == _ctx.Frame)
                return false;
            if (!this.Effective(WindowMechanism.Resolve) || !s.Cache.Valid || w.Texture != s.Cache.Color)
                return false;
            return TryResolve(s);
        }

        /// <summary>RenderAll (hooked): `w` rendered (tex null: nothing to render); t0 = Stopwatch before RenderShown.</summary>
        internal void LegacyRendered(PortalWindow w, RenderTexture tex, long t0)
        {
            WindowState s = w.State;
            if (s == null)
                return;
            try
            {
                s.Chosen = true;
                s.Reason = WindowChoice.Legacy;
                _chosen.Add(s);
                Rendered(s, tex, t0);
            }
            catch (Exception e)
            {
                WindowLog.WarnEvery("WindowManager.LegacyRendered", 10f, "Window cache update failed: " + e);
            }
        }

        // ---- Win.LiveFill (FARWORLD-V3 §7 V0): the count scheduler's windows off their cadence, rendered while there is time

        /// <summary>The main-thread ms this frame's window renders may take before LiveFill renders no more (RenderAll
        /// counts what is due anyway first): B_cpu's headroom, the target frame less the frame without the window renders
        /// and the margin. The frame is the main thread's own, the presentation wait left out as SampleBudgets leaves it out
        /// (with vSync or a frame limit that wait holds the idle rest of the interval, which is headroom). The renders off
        /// their cadence alone are held to LiveFillExtraMaxMs on top. With [Window] CpuBudgetMs set, that cap is all there
        /// is (unlimited here). A frame the GPU controller reads as GPU-bound gets no extra renders. 0: none this frame.</summary>
        internal float LiveFillBudgetMs { get; private set; }
        /// <summary>RenderAll (Win.LiveFill): the most the renders off their cadence alone may take (main-thread ms, the
        /// due ones not counted): [Window] CpuBudgetMs when set, else B_cpu's CpuMaxMs ceiling, so at an uncapped frame
        /// rate (target 16.7 ms) they cost it no more than the tiers' budget would.</summary>
        internal float LiveFillExtraMaxMs => Settings.CpuBudgetMs > 0f ? Settings.CpuBudgetMs : Settings.CpuMaxMs;
        /// <summary>Lab: windows LiveFill rendered off their cadence last frame, the estimated ms of all that frame's renders
        /// (the due ones first) and the headroom they were held to; LabLiveFillRenders over the session.</summary>
        internal int LastLiveFill;
        internal float LastLiveFillMs, LastLiveFillBudgetMs;
        /// <summary>Lab: last frame's windows that waited for LiveFill and did not fit (shown stale), the cheapest of their
        /// estimates (ms; 0 without any), the main thread's measured frame before it (frame − presentation wait, ms) and
        /// whether the GPU controller held it GPU-bound (then nothing waits: LiveFillBudgetMs is 0).</summary>
        internal int LastLiveFillDenied;
        internal float LastLiveFillDeniedMinMs, LastLiveFillCpuMs;
        internal bool LastLiveFillGpuBound;
        internal static long LabLiveFillRenders;
        int _liveFill, _liveFillDenied;
        float _liveFillDeniedMin;

        // Win.LiveFill in force: the count scheduler's frame (Win.Tiers off) fills its headroom. Win.AllLive (Docs\PARITY-PLAN.md
        // P8) leaves it nothing to fill: RenderAll makes every window it shows due every frame, so neither LiveFill's extra cap
        // nor its headroom ever holds an on-screen window back (the headroom is still measured, for the lab's readings).
        static bool LiveFillOn => WindowFlags.LiveFill && !WindowFlags.Tiers;

        // RenderLegacy, before RenderAll: this frame's headroom. B_cpu's frame EMA is fed here (Plan feeds it with the tiers).
        void LiveFillBudget()
        {
            _liveFill = _liveFillDenied = 0;
            _liveFillDeniedMin = 0f;
            LastLiveFill = LastLiveFillDenied = 0;
            LastLiveFillMs = LastLiveFillDeniedMinMs = LastLiveFillCpuMs = 0f;
            LastLiveFillGpuBound = false;
            LiveFillBudgetMs = 0f;
            if (!LiveFillOn || !PartReady(Cost))
                return;
            try
            {
                float target = World != null ? World.TargetFrameMs : 16.7f;
                if (!(target > 0f))
                    target = 16.7f;
                // The presentation wait as SampleBudgets reads it (Install hooks it once; the count scheduler has no
                // mandatory renders).
                float frameMs = Time.unscaledDeltaTime * 1000f, waitMs = 0f;
                bool gpuBound = false;
                if (Gpu != null)
                {
                    Gpu.Install();
                    if (Gpu.Ready)
                    {
                        if (Gpu is GpuBudget g)
                        {
                            g.TargetFrameMs = target;
                            g.FrameMs = frameMs;
                            g.WindowGpuMs = _lastWindowGpuMs;
                            g.MandatoryGpuMs = 0f;
                        }
                        Gpu.Sample(Time.frameCount);
                        waitMs = Gpu is GpuBudget gb ? gb.LastWaitMs : 0f;
                        gpuBound = Gpu.GpuBound;
                    }
                }
                LastLiveFillCpuMs = Mathf.Max(0f, frameMs - waitMs);
                LastLiveFillGpuBound = gpuBound;
                Cost.RecordFrame(LastLiveFillCpuMs, _lastWindowCpuMs);
                if (Settings.CpuBudgetMs > 0f)
                    // The explicit setting holds the renders off their cadence alone (LiveFillExtraMaxMs), as B_cpu holds
                    // the tiers' renders to it whatever the frame has left.
                    LiveFillBudgetMs = float.MaxValue;
                else if (Cost is WindowCostModel m && m.RestEmaMs >= 0f)
                    LiveFillBudgetMs = Mathf.Max(0f, target - m.RestEmaMs - Settings.CpuMarginMs);
                else
                    // Nothing measured yet: what B_cpu gives then.
                    LiveFillBudgetMs = Cost.CpuBudgetMs(target);
                // The main thread has time the GPU does not: more renders would only slow the frame further.
                if (gpuBound)
                    LiveFillBudgetMs = 0f;
            }
            catch (Exception e)
            {
                LiveFillBudgetMs = 0f;
                WindowLog.WarnEvery("WindowManager.LiveFillBudget", 10f, "Window LiveFill headroom failed: " + e);
            }
            LastLiveFillBudgetMs = LiveFillBudgetMs;
        }

        /// <summary>RenderAll (hooked, Win.LiveFill): `w`, not due this frame by its cadence, may render anyway: it has a view
        /// (so it is on a cadence at all), is not hidden behind something (§4.8), and the lab neither paces it nor pins its tier.</summary>
        internal bool LiveFillCandidate(PortalWindow w)
        {
            WindowState s = w != null ? w.State : null;
            return s != null && LiveFillBudgetMs > 0f && w.Texture != null && s.LabEvery <= 0 && !s.LabTier.HasValue
                   && s.LegacyHiddenFrame != _ctx.Frame;
        }

        /// <summary>RenderAll (hooked, Win.LiveFill): what a render of `w` is expected to cost (main-thread ms, as Rendered
        /// measures it: the far side's setup around the camera included) at the tier its cadence gives it.</summary>
        internal float LiveFillCostMs(PortalWindow w)
        {
            WindowState s = w != null ? w.State : null;
            if (s == null || !PartReady(Cost))
                return 0f;
            WindowTier t = s.Params.Tier;
            if (t < WindowTier.Live || t > WindowTier.SmallCached)
                t = WindowTier.Live;
            return Cost.CpuEstimateMs(s, t);
        }

        /// <summary>RenderAll (hooked, Win.LiveFill): `w` renders this frame off its cadence.</summary>
        internal void LiveFilled(PortalWindow w)
        {
            _liveFill++;
            LabLiveFillRenders++;
        }

        /// <summary>RenderAll (hooked, Win.LiveFill): `w` waited off its cadence and did not fit (`ms`: its estimate); it
        /// shows its last view.</summary>
        internal void LiveFillDenied(PortalWindow w, float ms)
        {
            if (_liveFillDenied == 0 || ms < _liveFillDeniedMin)
                _liveFillDeniedMin = ms;
            _liveFillDenied++;
        }

        /// <summary>RenderAll (hooked, Win.LiveFill): the frame's choice is made; `ms`: the estimate of all its renders.</summary>
        internal void LiveFillDone(float ms)
        {
            LastLiveFill = _liveFill;
            LastLiveFillMs = ms;
            LastLiveFillDenied = _liveFillDenied;
            LastLiveFillDeniedMinMs = _liveFillDeniedMin;
        }

        // ---- Both paths: right before the composite, and after it ----------------------------------------------------

        /// <summary>
        /// From PortalRenderer.EndFrame before ShowOwn and the game camera's passes: what the RenderAll hooks did not
        /// decide (legacy), the per-window material values of the composite (_RP_VisId, _RP_Resolved), and the windows
        /// whose ids the composite draws.
        /// </summary>
        internal void BeforeComposite(Camera main, IList<PortalWindow> windows)
        {
            try
            {
                if (!_tieredFrame)
                {
                    // Not touched by RenderAll's selection: out of the frustum (Off), or in it but too small, too far or
                    // not showable (the swirl).
                    Plane[] planes = Renderer.FramePlanes;
                    for (int i = 0; i < _states.Count; i++)
                    {
                        WindowState s = _states[i];
                        if (s.PlannedFrame == _ctx.Frame)
                            continue;
                        PortalWindow w = s.Window;
                        bool shown = w.Enabled && w.Partner != null && w.Partner.Enabled;
                        s.InFrustum = shown && GeometryUtility.TestPlanesAABB(planes, w.Renderer.bounds);
                        WindowTier tier = s.InFrustum ? WindowTier.Closed : WindowTier.Hidden;
                        s.Show = s.InFrustum ? WindowShow.Fallback : WindowShow.Off;
                        if (tier != s.Tier)
                        {
                            WindowTier before = s.Tier;
                            s.PrevTier = before;
                            s.Tier = s.WantTier = tier;
                            s.TierSince = _ctx.Now;
                            Stats.TierChanges++;
                            RaiseTierChanged(s, before, tier);
                        }
                    }
                }
                for (int i = 0; i < _states.Count; i++)
                {
                    WindowState s = _states[i];
                    Material m = s.Window.Material;
                    if (m == null)
                        continue;
                    m.SetFloat(WindowShaderIds.VisId, s.VisId);
                    m.SetFloat(WindowShaderIds.Resolved, s.Show == WindowShow.Resolved ? 1f : 0f);
                }
                _materialsMarked = true;
                if (Visibility is WindowVisibility v)
                    v.Expect(_states);
            }
            catch (Exception e)
            {
                WindowLog.WarnEvery("WindowManager.BeforeComposite", 10f, "Window composite setup failed: " + e);
            }
        }

        // The counts (when Win.Occlusion is in force and the composite drew the ids) and this frame's depth statistics are
        // read back from the game camera's AfterEverything: recorded here unless the composite did it already.
        void ScheduleVisibility(Camera main)
        {
            // Wanted for the occlusion counts and for the depth statistics of the error model; neither: nothing on the camera.
            bool wanted = this.Effective(WindowMechanism.Occlusion) || this.Effective(WindowMechanism.ErrorModel);
            if (!PartReady(Visibility) || main == null || !wanted)
            {
                DetachVisibility();
                return;
            }
            if (_visibilityOn != main)
            {
                DetachVisibility();
                main.AddCommandBuffer(CameraEvent.AfterEverything, _visibility);
                _visibilityOn = main;
            }
            // Recorded fresh every frame: a stale recording would read back again into another frame's entry.
            _visibility.Clear();
            try
            {
                // Counted only when the composite drew ids this frame (no window the camera may see: nothing to count, and
                // no full-screen dispatch or readback for it).
                RenderTexture ids = Composite != null ? Composite.IdTexture : null;
                bool counts = this.Effective(WindowMechanism.Occlusion) && ids != null && Renderer.IdsDrawnThisFrame;
                if (counts)
                    Visibility.Schedule(_visibility, ids, ids.width, ids.height, true);
                else
                    Visibility.Schedule(_visibility, BuiltinRenderTextureType.None, 0, 0, false);
            }
            catch (Exception e)
            {
                WindowLog.WarnEvery("WindowVisibility.Schedule", 10f, "Window readback scheduling failed: " + e);
            }
        }

        // The per-window composite values back to "nothing of S3" once, when the pipeline stops driving the frames.
        bool _materialsMarked;

        void ClearMaterialMarks(IList<PortalWindow> windows)
        {
            if (!_materialsMarked)
                return;
            _materialsMarked = false;
            for (int i = 0; i < windows.Count; i++)
            {
                Material m = windows[i] != null ? windows[i].Material : null;
                if (m == null)
                    continue;
                m.SetFloat(WindowShaderIds.Resolved, 0f);
            }
        }

        void DetachVisibility()
        {
            if (_visibilityOn != null)
                _visibilityOn.RemoveCommandBuffer(CameraEvent.AfterEverything, _visibility);
            _visibilityOn = null;
            _visibility.Clear();
        }

        void FinishFrame(int frame)
        {
            WindowFrameStats st = Stats;
            st.Windows = _states.Count;
            for (int i = 0; i < _states.Count; i++)
            {
                WindowState s = _states[i];
                if (s.InFrustum)
                    st.InFrustum++;
                switch (s.Show)
                {
                    case WindowShow.Fresh:
                        st.Fresh++;
                        break;
                    case WindowShow.Resolved:
                        st.Resolved++;
                        break;
                    case WindowShow.Reprojected:
                        st.Reprojected++;
                        break;
                    case WindowShow.Fallback:
                        st.Fallback++;
                        break;
                }
                if (Shows(s.Show))
                {
                    st.Shown++;
                    s.UsedTime = Time.unscaledTime;
                    // As RenderAll marks what it shows (its "kept" test and 30-frame release go by it, should Win.Tiers go off).
                    s.Window.ShownAtFrame = frame;
                }
                if (s.Tier == WindowTier.Hidden)
                    st.Hidden++;
                else if (s.Tier == WindowTier.Closed)
                    st.Closed++;
                if (s.Tier == WindowTier.Live && s.Show != WindowShow.Fresh)
                    st.LiveStale++;
                if (s.Show != s.PrevShow)
                {
                    WindowShow before = s.PrevShow;
                    s.PrevShow = s.Show;
                    RaiseShowChanged(s, before, s.Show);
                }
            }
            if (Pool != null)
            {
                st.SlotsUsed = Pool.AcquiredThisFrame;
                st.StickyHits = Pool.StickyHits;
            }
            st.ReadbackLatency = PartReady(Visibility) ? Visibility.LatencyFrames : 0;
            if (!_tieredFrame)
            {
                st.CpuBudgetMs = float.PositiveInfinity;
                st.GpuBudgetMs = float.PositiveInfinity;
                st.ResolutionFactor = 1f;
            }
            _lastWindowCpuMs = st.WindowCpuMs;
            _lastWindowGpuMs = _frameGpuMs;
            _frameGpuMs = 0f;
            RaiseFrameDone();
        }

        // ================================================================================================================
        // Invalidation, settings, teardown
        // ================================================================================================================

        public void Invalidate(PortalWindow w, WindowInvalid why)
        {
            if (w != null)
                WindowState.For(w).Invalid |= why;
        }

        public void InvalidateAll(WindowInvalid why)
        {
            IList<PortalWindow> ws = Windows;
            if (ws == null)
                return;
            for (int i = 0; i < ws.Count; i++)
                if (ws[i] != null)
                    WindowState.For(ws[i]).Invalid |= why;
        }

        public void SettingsChanged() => Pool?.SettingsChanged();

        // Slot cameras idle for the pool's IdleSeconds go (checked once a second); every one of them when Win.CameraSlots
        // is not in force (nothing renders with them then) or `all` (at once: the game camera they were set up from is gone).
        void SweepSlots(bool all)
        {
            if (!(Pool is WindowCameraPool pool))
                return;
            bool idleAll = all || !this.Effective(WindowMechanism.CameraSlots);
            // Win.SlotPrewarm: while portals are live a slot camera stays for LiveIdleSeconds, not IdleSeconds: turning
            // back to the windows after half a minute must not make (and prime) their cameras again.
            float idle = pool.IdleSeconds;
            if (WindowFlags.SlotPrewarm && Windows != null && Windows.Count > 0)
                idle = Mathf.Max(idle, pool.LiveIdleSeconds);
            pool.Sweep(Time.unscaledTime, idleAll ? 0f : idle, all);
        }

        /// <summary>The game camera went away (logout, the plugin switched off): every slot camera goes now, with what the
        /// plugin attached to it; the next game camera gets new ones on demand.</summary>
        internal void ReleaseSlots() => SweepSlots(true);

        public void Reset()
        {
            // Cameras are kept (the contract), but idle ones still go: while suspended nothing else sweeps them.
            SweepSlots(false);
            RestoreBindings();
            BeginResolves();
            try
            {
                Visibility?.Reset();
            }
            catch (Exception e)
            {
                WindowLog.Warn("WindowVisibility.Reset failed: " + e);
            }
            DetachVisibility();
            for (int i = 0; i < _chosen.Count; i++)
                _chosen[i].Chosen = false;
            _chosen.Clear();
            _ahead = null;
            _plannedFrame = -100;
            _hasPlanEye = false;
            Renderer.Pipeline = null;
            Renderer.Hook = null;
        }

        // ---- Slot events: invocation lists cached so raising allocates nothing; each handler isolated --------------------

        Action<WindowState, Camera> _before, _after;
        Action<WindowState, Camera>[] _beforeList = Array.Empty<Action<WindowState, Camera>>(), _afterList = Array.Empty<Action<WindowState, Camera>>();

        public event Action<WindowState, Camera> BeforeSlotRender
        {
            add { _before += value; _beforeList = List(_before); }
            remove { _before -= value; _beforeList = List(_before); }
        }

        public event Action<WindowState, Camera> AfterSlotRender
        {
            add { _after += value; _afterList = List(_after); }
            remove { _after -= value; _afterList = List(_after); }
        }

        public event Action<Camera> OnSlotCameraCreated;
        public event Action<Camera> OnSlotCameraDestroyed;

        static Action<WindowState, Camera>[] List(Action<WindowState, Camera> d)
        {
            if (d == null)
                return Array.Empty<Action<WindowState, Camera>>();
            Delegate[] all = d.GetInvocationList();
            var list = new Action<WindowState, Camera>[all.Length];
            for (int i = 0; i < all.Length; i++)
                list[i] = (Action<WindowState, Camera>)all[i];
            return list;
        }

        public void RaiseBeforeSlotRender(WindowState s, Camera cam) => Raise(_beforeList, s, cam, "BeforeSlotRender");

        public void RaiseAfterSlotRender(WindowState s, Camera cam) => Raise(_afterList, s, cam, "AfterSlotRender");

        // A throwing handler must not skip the render (draws queued by earlier handlers would leak into the camera's next
        // frame, P3(ii)) nor the handlers after it.
        static void Raise(Action<WindowState, Camera>[] list, WindowState s, Camera cam, string what)
        {
            for (int i = 0; i < list.Length; i++)
            {
                try
                {
                    list[i](s, cam);
                }
                catch (Exception e)
                {
                    WindowLog.WarnEvery(what + i, 10f, $"{what} handler {list[i].Method.DeclaringType?.Name}.{list[i].Method.Name} failed: {e}");
                }
            }
        }

        void SlotCreated(Camera cam)
        {
            try
            {
                OnSlotCameraCreated?.Invoke(cam);
            }
            catch (Exception e)
            {
                WindowLog.Warn("OnSlotCameraCreated handler failed: " + e);
            }
        }

        void SlotDestroyed(Camera cam)
        {
            try
            {
                OnSlotCameraDestroyed?.Invoke(cam);
            }
            catch (Exception e)
            {
                WindowLog.Warn("OnSlotCameraDestroyed handler failed: " + e);
            }
        }

        // ---- Lab -------------------------------------------------------------------------------------------------------

        public void LabForceTier(PortalWindow w, WindowTier? tier)
        {
            if (w != null)
            {
                WindowState.For(w).LabTier = tier;
                return;
            }
            IList<PortalWindow> ws = Windows;
            if (ws != null)
                for (int i = 0; i < ws.Count; i++)
                    if (ws[i] != null)
                        WindowState.For(ws[i]).LabTier = tier;
        }

        public void LabCadence(PortalWindow w, int every)
        {
            int frame = Time.frameCount;
            if (w != null)
            {
                WindowState s = WindowState.For(w);
                s.LabEvery = every;
                s.LabFrom = frame;
                return;
            }
            IList<PortalWindow> ws = Windows;
            if (ws == null)
                return;
            for (int i = 0; i < ws.Count; i++)
                if (ws[i] != null)
                {
                    WindowState s = WindowState.For(ws[i]);
                    s.LabEvery = every;
                    s.LabFrom = frame;
                }
        }

        public event Action<WindowState, WindowTier, WindowTier> TierChanged;
        public event Action<WindowState, WindowShow, WindowShow> ShowChanged;
        public event Action<WindowFrameStats> FrameDone;

        void RaiseTierChanged(WindowState s, WindowTier from, WindowTier to)
        {
            if (TierChanged == null)
                return;
            try
            {
                TierChanged(s, from, to);
            }
            catch (Exception e)
            {
                WindowLog.WarnEvery("TierChanged", 10f, "TierChanged handler failed: " + e);
            }
        }

        void RaiseShowChanged(WindowState s, WindowShow from, WindowShow to)
        {
            if (ShowChanged == null)
                return;
            try
            {
                ShowChanged(s, from, to);
            }
            catch (Exception e)
            {
                WindowLog.WarnEvery("ShowChanged", 10f, "ShowChanged handler failed: " + e);
            }
        }

        void RaiseFrameDone()
        {
            if (FrameDone == null)
                return;
            try
            {
                FrameDone(Stats);
            }
            catch (Exception e)
            {
                WindowLog.WarnEvery("FrameDone", 10f, "FrameDone handler failed: " + e);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            Reset();
            _disposed = true;
            // The slot cameras first, while the plugin's handlers still hear about them (grass exclusions, fog, AO go).
            Pool?.Dispose();
            if (Pool != null)
            {
                Pool.Created -= SlotCreated;
                Pool.Destroyed -= SlotDestroyed;
            }
            Composite?.Dispose();
            Resolver?.Dispose();
            Visibility?.Dispose();
            Scheduler?.Dispose();
            Gpu?.Dispose();
            Cost?.Dispose();
            _visibility.Release();
            _chosen.Clear();
            _states.Clear();
            _known.Clear();
            if (Renderer.Manager == this)
                Renderer.Manager = null;
        }
    }

    public sealed partial class WindowState
    {
        // WindowManager bookkeeping: the last frame it was in the list, whether the manager tracks it, its frustum state at
        // Plan (the re-plan sees windows come into or leave the frustum), the last Show raised (ShowChanged), whether its
        // PortalWindow fields point at this frame's resolve output, the last time something of its cache was shown, and
        // CanShow last frame (FirstShow when it becomes showable).
        internal int SeenFrame = -100;
        internal bool Known, PlanInFrustum, BoundResolved, PrevCanShow;
        internal WindowShow PrevShow = WindowShow.Off;
        internal float UsedTime = -100f;
        // Win.CacheBudget: the bytes its textures hold, as counted for the render of CacheBytesFrame (Cache.Frame).
        internal long CacheBytes;
        internal int CacheBytesFrame = -100;
        // Win.Prewarm: not a candidate for a render ahead before this frame (its last one could not be made).
        internal int AheadRetryFrame = -100;
    }

    public sealed partial class WindowPipelineSettings
    {
        /// <summary>Win.CacheBudget: MB the windows' cached views may hold together before the ones out of sight longest
        /// are given back.</summary>
        public float CacheBudgetMB = 192f;
        /// <summary>Win.Prewarm: degrees past the edge of the view within which a window with nothing to show is rendered
        /// ahead.</summary>
        public float PrewarmConeDegrees = 30f;
    }

    public sealed partial class WindowCache
    {
        // Filled with the camera fields at the render (PortalRenderer.RenderLevel): the unscaled pair then (PairMoved), and
        // the effects it had (TierContent when the tier's effects are loosened).
        internal Matrix4x4 PairBase = Matrix4x4.identity;
        internal bool Ao, PointLightShadows = true;
        internal float ShadowDistanceCap = -1f;
        internal int ShadowCascadesCap = -1;
    }
}
