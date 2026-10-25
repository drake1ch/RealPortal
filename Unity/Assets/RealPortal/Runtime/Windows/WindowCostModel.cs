using System;
using UnityEngine;

namespace RealPortal
{
    // OWNER: sched (S3). CPU EMA per window and tier, GPU estimate from the P9 coefficients, B_cpu (§4.6 "Budgets").
    // Contract: IWindowCostModel in WindowContracts.cs.
    //
    // CPU: what the frame loop measures around each render (submit + Render + capture, i.e. RenderLevel with the far
    // environment's BeforeRender/AfterRender), kept per window and tier, since the same tier costs very different amounts
    // through different windows (how much of which far area each one sees). A window not measured at a tier yet takes that
    // tier's mean over all windows, else another tier's mean scaled by the priors, else the prior.
    // GPU: not measurable in the shipped player (FrameTimingManager off, P8) and P9 has not run, so the coefficients are
    // priors from ARCHITECTURE-v2 §4.13 (1080p estimates); they only rank renders against each other and against B_gpu,
    // which the fallback controller (GpuBudget) shrinks from what the windows were estimated to cost when it saw the
    // GPU bind. Replace them with P9's numbers when it runs (public fields, the lab can set them).
    //
    // Win.ShadowCost: what a render costs depends most on the shadow casters its view holds (hall 20: frames above p95
    // drew 523 casters against 397 facing the ring, 1076 against 464 walking in, at about one more window render), and
    // the view a window shows changes little between two of its renders. Unity gives no caster count per camera, so the
    // proxy is the ms of the window's camera Render() alone (PortalRenderer.RenderLevel, its deeper levels' added): it has
    // the shadow passes in it, but not what the render order puts around it (a paused far area's wake, the particles'
    // catch-up, TierEffects' light gather: the frame's first render pays them). It is kept per window and tier as the
    // whole render's ms are, and as a tier mean over all windows. Heavy compares a window's peak of it (its EMA or its
    // last render at that tier, whichever is more: the EMA alone needs some five renders to follow a view that turned
    // heavy) with that mean, and tells the scheduler which renders not to stack past the budget (WindowScheduler.Choose).
    // Only a render judged heavy is budgeted above its EMA, by as much as its last camera ms were past its camera EMA
    // (PeakEstimateMs); every other estimate stays the EMA, so the switch does not tighten the budget for all windows.
    // The GPU estimate's cascade term scales with the same proxy's ratio to the tier's mean (a heavy render's by its
    // peak). A render with another shadow set than the window's samples at that tier (a shadow switch flipped) starts
    // its EMAs afresh.
    public sealed partial class WindowCostModel : IWindowCostModel
    {
        readonly WindowPipelineSettings _settings;

        /// <summary>The manager this model works for (WindowMechanisms.ShadowCaps); set by the WindowManager constructor.</summary>
        internal IWindowManager Manager;

        // Win.ShadowCost: a tier's mean says what "heavier than usual" is only once this many renders fed it.
        const int HeavyMinTierSamples = 8;

        // Main-thread ms of one render by tier before anything was measured (lab, hall10, 37-zone far areas: 1.8-2.5 ms a
        // render; the cheaper tiers save cascades, AO and pixels but not the culling, which dominates).
        static readonly float[] CpuPrior = { 2.0f, 1.7f, 1.4f };

        // GPU priors: fixed ms per render (shadow maps, clears, light setup), ms per million rendered pixels (G-buffer,
        // lighting, fog, copies), per shadow cascade, per million AO pixels, and the motion-vector pass of AoTemporal.
        public float[] GpuFixed = { 0.30f, 0.18f, 0.10f };
        public float[] GpuPerMpx = { 1.10f, 0.90f, 0.70f };
        public float GpuPerCascade = 0.08f, GpuAoPerMpx = 0.60f, GpuMotionPerMpx = 0.15f;

        // EMA weights: per window a few renders (it re-renders every few frames), per tier slower (many windows feed it).
        const float WindowAlpha = 0.2f, TierAlpha = 0.05f;

        readonly float[] _tierMean = new float[3];
        readonly int[] _tierN = new int[3];
        // Win.ShadowCost: the tier's mean of the camera ms (as _tierMean), how many renders fed it since it last started,
        // and whether those were timed around the camera (false: the whole render's ms, the switch off); a render of the
        // other kind starts it afresh, so the switch flipped never compares camera ms with whole renders.
        readonly float[] _tierCamMean = new float[3];
        readonly int[] _tierCamN = new int[3];
        readonly bool[] _tierCamTimed = new bool[3];
        // EMA of the frame's main-thread ms without the window renders (B_cpu's "frame - windowCpu"); < 0: none yet.
        float _restEma = -1f;
        bool _disposed;

        public WindowCostModel(WindowPipelineSettings settings)
        {
            _settings = settings ?? new WindowPipelineSettings();
        }

        public bool Ready => !_disposed;

        /// <summary>The EMA of the frame without the window renders (ms; -1 before the first frame), for the lab.</summary>
        internal float RestEmaMs => _restEma;

        public float CpuEstimateMs(WindowState s, WindowTier tier)
        {
            int t = (int)tier;
            if (t < 0 || t > 2)
                return 0f;
            return EmaEstimate(s, t);
        }

        /// <summary>Win.ShadowCost: the CPU estimate of a render the scheduler judged heavy (Heavy): the window's EMA at
        /// `tier` plus as much as its last camera ms there were past its camera EMA (a view that turned shadow-heavy stays
        /// expensive until a render shows otherwise; the wake, catch-up and gather around the camera never raise it). The
        /// EMA estimate with the switch off or nothing measured.</summary>
        internal float PeakEstimateMs(WindowState s, WindowTier tier)
        {
            int t = (int)tier;
            if (t < 0 || t > 2)
                return 0f;
            if (WindowFlags.ShadowCost && s != null && s.CpuSamples[t] > 0)
                return s.CpuByTier[t] + Mathf.Max(0f, s.CamLast[t] - s.CamByTier[t]);
            return EmaEstimate(s, t);
        }

        // The estimate from the EMAs alone (the window's at that tier, else the tier's mean, else the priors).
        float EmaEstimate(WindowState s, int t)
        {
            if (s != null && s.CpuSamples[t] > 0)
                return s.CpuByTier[t];
            if (_tierN[t] > 0)
                return _tierMean[t];
            for (int k = 0; k < 3; k++)
                if (_tierN[k] > 0)
                    return _tierMean[k] * CpuPrior[t] / CpuPrior[k];
            return CpuPrior[t];
        }

        public float GpuEstimateMs(WindowState s, WindowTier tier, int pixels)
        {
            int t = (int)tier;
            if (t < 0 || t > 2)
                return 0f;
            // The effect set this render would have: the window's own when it was made for that tier this frame, else
            // the tier's defaults.
            bool own = s != null && s.Params.Tier == tier;
            TierParams p = own ? s.Params : _settings.Effects[t];
            // Win.ShadowTiersSmall: the defaults' cascade cap only where the shadow switches put the tier's caps in force
            // (the decision ParamsFor takes for the window's own set); before it the defaults were taken as they are.
            if (!own && Manager != null && Manager.Effective(WindowMechanism.ShadowTiersSmall) && !Manager.ShadowCaps(tier))
                p.ShadowCascadesCap = -1;
            int cascades = QualitySettings.shadowCascades;
            if (p.ShadowCascadesCap >= 0)
                cascades = Mathf.Min(cascades, p.ShadowCascadesCap);
            // Win.ShadowCost: each cascade draws the casters the view holds, and a view that holds many makes its camera
            // take more main-thread ms than the tier's mean (culling and submitting): the cascade term by that ratio of the
            // camera ms (the EMA's; a render the scheduler judged heavy, its peak's), a proxy within 0.5-3 x.
            float shadow = 1f;
            if (WindowFlags.ShadowCost && s != null && CamComparable(s, t) && _tierCamMean[t] > 0.01f)
                shadow = Mathf.Clamp((s.ShadowHeavy ? CamPeak(s, t) : s.CamByTier[t]) / _tierCamMean[t], 0.5f, 3f);
            float mpx = Mathf.Max(0, pixels) * 1e-6f;
            float aoMpx = p.Ao ? (p.AoHalfRes ? 0.25f : 1f) * mpx : 0f;
            float g = GpuFixed[t] + GpuPerMpx[t] * mpx + GpuPerCascade * cascades * shadow + GpuAoPerMpx * aoMpx;
            if (p.AoTemporal)
                g += GpuMotionPerMpx * mpx;
            if (s != null && s.Params.Tier == tier)
                s.GpuEst = g;
            return g;
        }

        public void RecordRender(WindowState s, WindowTier tier, double cpuMs, int pixels)
        {
            int t = (int)tier;
            if (s == null || t < 0 || t > 2 || !(cpuMs >= 0.0) || double.IsInfinity(cpuMs))
                return;
            // One slow render (a shader variant compiled, a GC, a far area's first frame) must not make the window look
            // expensive for seconds: a sample counts for at most three times what it was expected to cost.
            float expected = EmaEstimate(s, t);
            float x = Mathf.Min((float)cpuMs, expected * 3f + 1f);
            // Win.ShadowCost: samples taken with another shadow set (distance and cascade caps, point-light shadows) say
            // nothing about this one; the set and the last sample are kept with the switch off too, so it starts with them.
            int set = ShadowSetOf(s.Cache);
            bool restart = WindowFlags.ShadowCost && s.CpuSamples[t] > 0 && s.ShadowSet[t] != set;
            s.ShadowSet[t] = set;
            s.CpuLast[t] = x;
            // Win.ShadowCost: the camera's Render() alone when RenderLevel timed it in this frame, else (the switch off)
            // the whole render's ms; a sample of the other kind than the window's at that tier starts its EMA afresh.
            bool timed = WindowFlags.ShadowCost && s.CamFrame == Time.frameCount;
            float cam = timed ? Mathf.Min(s.CamMs, x) : x;
            bool camRestart = restart || s.CamTimed[t] != timed;
            s.CamTimed[t] = timed;
            s.CamLast[t] = cam;
            if (s.CpuSamples[t] > 0 && !restart)
                s.CpuByTier[t] = Mathf.Lerp(s.CpuByTier[t], x, WindowAlpha);
            else
                s.CpuByTier[t] = x;
            s.CamByTier[t] = s.CpuSamples[t] > 0 && !camRestart ? Mathf.Lerp(s.CamByTier[t], cam, WindowAlpha) : cam;
            if (s.CpuSamples[t] < int.MaxValue)
                s.CpuSamples[t]++;
            s.CpuEma = s.CpuByTier[t];
            _tierMean[t] = _tierN[t] > 0 ? Mathf.Lerp(_tierMean[t], x, TierAlpha) : x;
            if (_tierN[t] < int.MaxValue)
                _tierN[t]++;
            if (_tierCamTimed[t] != timed)
            {
                _tierCamTimed[t] = timed;
                _tierCamN[t] = 0;
            }
            _tierCamMean[t] = _tierCamN[t] > 0 ? Mathf.Lerp(_tierCamMean[t], cam, TierAlpha) : cam;
            if (_tierCamN[t] < int.MaxValue)
                _tierCamN[t]++;
        }

        public void RecordFrame(double frameMs, double windowCpuMs)
        {
            if (!(frameMs > 0.0) || double.IsInfinity(frameMs))
                return;
            // A hitch (loading, GC) is not the frame's normal cost: a sample counts for at most 100 ms.
            float rest = Mathf.Clamp((float)(frameMs - Math.Max(0.0, windowCpuMs)), 0f, 100f);
            // Time-based EMA (time constant FrameEmaSeconds), so the budget reacts equally fast at 30 and at 144 fps.
            float dt = Mathf.Min((float)(frameMs / 1000.0), 0.25f);
            float a = 1f - Mathf.Exp(-dt / Mathf.Max(_settings.FrameEmaSeconds, 0.01f));
            _restEma = _restEma < 0f ? rest : Mathf.Lerp(_restEma, rest, a);
        }

        public float CpuBudgetMs(float targetFrameMs)
        {
            if (_settings.CpuBudgetMs > 0f)
                return _settings.CpuBudgetMs;
            // Nothing measured yet: the most the windows may ever take, so the first frames are not starved.
            if (_restEma < 0f)
                return _settings.CpuMaxMs;
            return Mathf.Clamp(targetFrameMs - _restEma - _settings.CpuMarginMs, _settings.CpuMinMs, _settings.CpuMaxMs);
        }

        /// <summary>Win.ShadowCost: a render of `s` at `tier` is heavy: its camera ms there (CamPeak) at least
        /// ShadowHeavyFactor × what that tier's cameras take on average over all windows, and at least ShadowHeavyMinMs.
        /// Never before the tier's camera mean has HeavyMinTierSamples renders in it, nor for a window not measured at that
        /// tier the way the mean was (CamComparable).</summary>
        internal bool Heavy(WindowState s, WindowTier tier)
        {
            int t = (int)tier;
            if (s == null || t < 0 || t > 2 || _tierCamN[t] < HeavyMinTierSamples || !CamComparable(s, t))
                return false;
            float peak = CamPeak(s, t);
            return peak >= _settings.ShadowHeavyMinMs && peak >= _tierCamMean[t] * _settings.ShadowHeavyFactor;
        }

        // Win.ShadowCost: a window's camera ms at tier t, its EMA or its last render, whichever is more.
        static float CamPeak(WindowState s, int t) => Mathf.Max(s.CamByTier[t], s.CamLast[t]);

        // Win.ShadowCost: `s` was measured at tier t the way the tier's camera mean was fed lately (both timed around the
        // camera, or both the whole render), so the two compare.
        bool CamComparable(WindowState s, int t) => s.CpuSamples[t] > 0 && _tierCamN[t] > 0 && s.CamTimed[t] == _tierCamTimed[t];

        // The shadow set a render had (the cache's fields as RenderLevel filled them), as one comparable number.
        static int ShadowSetOf(WindowCache c)
        {
            int d = c.ShadowDistanceCap < 0f ? 0 : Mathf.Min(Mathf.RoundToInt(c.ShadowDistanceCap), 100000) + 1;
            int k = Mathf.Clamp(c.ShadowCascadesCap, -1, 14) + 1;
            return (d * 16 + k) * 2 + (c.PointLightShadows ? 1 : 0);
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }

    public sealed partial class WindowState
    {
        // WindowCostModel: EMA of this window's render ms per tier (Live, NearCached, SmallCached) and how many renders fed it.
        internal readonly float[] CpuByTier = new float[3];
        internal readonly int[] CpuSamples = new int[3];
        // Win.ShadowCost: its last render's ms per tier (as the EMA took it) and that render's shadow set (ShadowSetOf).
        internal readonly float[] CpuLast = new float[3];
        internal readonly int[] ShadowSet = new int[3];
        // Win.ShadowCost: the camera Render() ms of the render in progress (PortalRenderer.RenderLevel, its deeper levels'
        // added) and the frame they were timed in; per tier the last render's camera ms, their EMA, and whether those were
        // timed around the camera (false: the whole render's ms, as RecordRender took them with the switch off).
        internal float CamMs;
        internal int CamFrame = -100;
        internal readonly float[] CamLast = new float[3];
        internal readonly float[] CamByTier = new float[3];
        internal readonly bool[] CamTimed = new bool[3];
    }

    public sealed partial class WindowPipelineSettings
    {
        /// <summary>Win.ShadowCost: a render is heavy from this × its tier's mean camera ms over all windows and from
        /// ShadowHeavyMinMs of its own (WindowCostModel.Heavy); past the budget the scheduler takes one heavy render a frame.</summary>
        public float ShadowHeavyFactor = 1.5f, ShadowHeavyMinMs = 1f;
    }
}
