using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace RealPortal
{
    // OWNER: sched (S3). B_gpu and the dynamic resolution factor. FrameTimingManager is off in the shipped player (P8):
    // the source is a Stopwatch around PlayerLoop TimeUpdate.WaitForLastPresentationAndUpdateTime (UnityEngine.LowLevel,
    // inserted by Install), with FrameTimingManager.gpuFrameTime used only when IsFeatureEnabled() says it works.
    // Contract: IGpuBudget in WindowContracts.cs.
    //
    // The controller. The main thread waits in WaitForLastPresentation until the GPU has presented the previous frame; a
    // wait above GpuBoundWaitMs says the GPU, not the CPU, may set the pace. That wait also contains vSync idle, a frame
    // limiter's idle and, with vSync, the rest of the refresh interval a CPU-bound frame missed its vblank by. So a frame
    // counts as GPU-bound only when all three hold: it missed its target (TargetFrameMs × 1.05; a frame that makes it has
    // nothing to gain from cheaper windows), it waited, and its own CPU part (frame − wait) would have made the target
    // (< 0.9 × target). A CPU-bound frame (the hall at 15-19 ms of main thread under 60 Hz vSync) or a hitch (GC, zone
    // load) is therefore never read as GPU-bound. The per-frame verdict goes into a 0.5 s EMA, and the controller is bound
    // while that EMA is above 0.6 (released below 0.4): about half a second of mostly GPU-bound frames to bind, as long to
    // let go, so DemoteNear and the NearCached AO that follow it do not flip from frame to frame.
    // While bound, B_gpu starts at the EMA of what the window renders were estimated to cost (never below the mandatory
    // renders' estimate, which it can never undercut anyway) and shrinks by GpuDecayPerSecond per second down to that
    // floor, the Live/NearCached resolution steps by DynResStep every half second down to DynResFloor, and NearCached
    // windows are treated as SmallCached (GpuBound → DemoteNear). Unbound, the resolution recovers at half that rate and
    // B_gpu is lifted (+∞) after a second. Without any signal (hooks not installed, no FTM) B_gpu is +∞ and the factor 1.
    public sealed partial class GpuBudget : IGpuBudget
    {
        // Marker types of the two PlayerLoop systems Install puts around the presentation wait.
        struct RealPortalPresentWaitBegin
        {
        }

        struct RealPortalPresentWaitEnd
        {
        }

        readonly WindowPipelineSettings _settings;

        // Filled by the PlayerLoop hooks (main thread, once per frame) and emptied by Sample. Static: the PlayerLoop holds
        // the delegates, and one controller exists at a time.
        static long s_waitStart;
        static double s_waitMs;
        static int s_waits;
        static readonly PlayerLoopSystem.UpdateFunction s_onBegin = OnWaitBegin;
        static readonly PlayerLoopSystem.UpdateFunction s_onEnd = OnWaitEnd;
        static readonly double s_tickMs = 1000.0 / Stopwatch.Frequency;

        bool _installed, _installFailed, _disposed;
        float _waitEma, _lastWait;
        bool _hasWait;
        float _budget = float.PositiveInfinity, _factor = 1f;
        bool _bound;
        float _stepAt, _lastSampleAt = -1f;
        int _sampledFrame = -100;
        readonly FrameTiming[] _timings = new FrameTiming[1];
        // EMA (0.5 s) of the per-frame GPU-bound verdict (0/1), of the windows' GPU estimate and of the mandatory renders'
        // share of it; how long the controller has been unbound (s).
        float _boundEma, _estEma = -1f, _mandatoryEma;
        float _unboundFor;

        // Bind above this share of GPU-bound frames, let go below the lower one.
        const float BindAt = 0.6f, ReleaseAt = 0.4f;
        // Unbound this long (s): B_gpu is lifted.
        const float LiftAfter = 1f;

        /// <summary>Set by the manager before Sample: the frame time the game aims for, the last frame's duration, what
        /// its window renders were estimated to cost on the GPU and the mandatory renders' part of that (ms).</summary>
        internal float TargetFrameMs = 16.7f, FrameMs, WindowGpuMs, MandatoryGpuMs;

        public GpuBudget(WindowPipelineSettings settings)
        {
            _settings = settings ?? new WindowPipelineSettings();
        }

        public bool Ready => !_disposed;

        public bool FrameTimingAvailable
        {
            get
            {
                try
                {
                    return FrameTimingManager.IsFeatureEnabled();
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        public float BudgetMs => _budget;
        public float ResolutionFactor => _factor;
        public bool GpuBound => _bound;
        public float WaitMsEma => _waitEma;
        /// <summary>The presentation wait of the frame Sample last read (ms; 0 without hooks).</summary>
        internal float LastWaitMs => _lastWait;
        internal bool Installed => _installed;

        static void OnWaitBegin() => s_waitStart = Stopwatch.GetTimestamp();

        static void OnWaitEnd()
        {
            if (s_waitStart == 0)
                return;
            s_waitMs += (Stopwatch.GetTimestamp() - s_waitStart) * s_tickMs;
            s_waits++;
            s_waitStart = 0;
        }

        public void Install()
        {
            if (_installed || _installFailed || _disposed)
                return;
            try
            {
                PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
                // A leftover of an earlier instance (plugin reloaded) goes first: never two pairs of hooks.
                Remove(ref root);
                if (!Insert(ref root))
                {
                    _installFailed = true;
                    WindowLog.Warn("GpuBudget: TimeUpdate.WaitForLastPresentationAndUpdateTime not found in the player loop; no GPU budget signal (B_gpu stays unlimited).");
                    return;
                }
                PlayerLoop.SetPlayerLoop(root);
                s_waitStart = 0;
                s_waitMs = 0.0;
                s_waits = 0;
                _installed = true;
            }
            catch (Exception e)
            {
                _installFailed = true;
                WindowLog.Warn("GpuBudget: player loop hooks failed (B_gpu stays unlimited): " + e.Message);
            }
        }

        public void Uninstall()
        {
            if (!_installed)
                return;
            _installed = false;
            try
            {
                PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
                if (Remove(ref root))
                    PlayerLoop.SetPlayerLoop(root);
            }
            catch (Exception e)
            {
                WindowLog.Warn("GpuBudget: removing the player loop hooks failed: " + e.Message);
            }
            s_waitStart = 0;
            s_waitMs = 0.0;
            s_waits = 0;
            // No signal from here on: the controller lets go.
            _bound = false;
            _budget = float.PositiveInfinity;
            _factor = 1f;
            _hasWait = false;
            _waitEma = _lastWait = 0f;
            _boundEma = 0f;
            _estEma = -1f;
            _mandatoryEma = 0f;
            _unboundFor = 0f;
        }

        static bool Insert(ref PlayerLoopSystem root)
        {
            PlayerLoopSystem[] top = root.subSystemList;
            if (top == null)
                return false;
            for (int i = 0; i < top.Length; i++)
            {
                if (top[i].type != typeof(TimeUpdate) || top[i].subSystemList == null)
                    continue;
                var list = new List<PlayerLoopSystem>(top[i].subSystemList);
                int k = list.FindIndex(x => x.type == typeof(TimeUpdate.WaitForLastPresentationAndUpdateTime));
                if (k < 0)
                    return false;
                list.Insert(k + 1, new PlayerLoopSystem { type = typeof(RealPortalPresentWaitEnd), updateDelegate = s_onEnd });
                list.Insert(k, new PlayerLoopSystem { type = typeof(RealPortalPresentWaitBegin), updateDelegate = s_onBegin });
                top[i].subSystemList = list.ToArray();
                root.subSystemList = top;
                return true;
            }
            return false;
        }

        static bool Remove(ref PlayerLoopSystem root)
        {
            PlayerLoopSystem[] top = root.subSystemList;
            if (top == null)
                return false;
            bool changed = false;
            for (int i = 0; i < top.Length; i++)
            {
                PlayerLoopSystem[] subs = top[i].subSystemList;
                if (subs == null)
                    continue;
                int ours = 0;
                foreach (PlayerLoopSystem s in subs)
                    if (s.type == typeof(RealPortalPresentWaitBegin) || s.type == typeof(RealPortalPresentWaitEnd))
                        ours++;
                if (ours == 0)
                    continue;
                var keep = new PlayerLoopSystem[subs.Length - ours];
                int n = 0;
                foreach (PlayerLoopSystem s in subs)
                    if (s.type != typeof(RealPortalPresentWaitBegin) && s.type != typeof(RealPortalPresentWaitEnd))
                        keep[n++] = s;
                top[i].subSystemList = keep;
                changed = true;
            }
            if (changed)
                root.subSystemList = top;
            return changed;
        }

        public void Sample(int frame)
        {
            if (_disposed || frame == _sampledFrame)
                return;
            _sampledFrame = frame;
            float now = Time.unscaledTime;
            float dt = _lastSampleAt < 0f ? 0f : Mathf.Clamp(now - _lastSampleAt, 0f, 0.25f);
            _lastSampleAt = now;

            // The inputs: the presentation wait since the last Sample, and the GPU frame time where FTM works.
            float wait = -1f;
            if (s_waits > 0)
            {
                wait = (float)(s_waitMs / s_waits);
                s_waitMs = 0.0;
                s_waits = 0;
            }
            _lastWait = wait >= 0f ? wait : 0f;
            float gpuMs = -1f;
            if (FrameTimingAvailable)
            {
                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, _timings) > 0 && _timings[0].gpuFrameTime > 0.0)
                    gpuMs = (float)_timings[0].gpuFrameTime;
            }
            // Time-based EMA weight (0.5 s), so the controller reacts alike at 30 and at 144 fps.
            float a = 1f - Mathf.Exp(-Mathf.Max(dt, 0.001f) / 0.5f);
            if (wait >= 0f)
            {
                _waitEma = _hasWait ? Mathf.Lerp(_waitEma, wait, a) : wait;
                _hasWait = true;
            }
            if (gpuMs < 0f && !_hasWait)
            {
                // No signal at all.
                _bound = false;
                _budget = float.PositiveInfinity;
                _factor = 1f;
                _boundEma = 0f;
                return;
            }

            float target = Mathf.Max(TargetFrameMs, 1f);
            // This frame's verdict (see the class comment): missed the target, and the GPU, not the main thread, is why.
            bool slow = FrameMs > target * 1.05f;
            bool gpuBound;
            if (gpuMs > 0f)
                gpuBound = slow && gpuMs > target * 0.95f;
            else
            {
                float cpuMs = Mathf.Max(0f, FrameMs - _lastWait);
                gpuBound = slow && _lastWait > _settings.GpuBoundWaitMs && cpuMs < target * 0.9f;
            }
            _boundEma = Mathf.Lerp(_boundEma, gpuBound ? 1f : 0f, a);
            if (_bound ? _boundEma < ReleaseAt : _boundEma > BindAt)
                _bound = !_bound;
            // What the window renders cost (estimated) over the last half second, and what of it was mandatory.
            _estEma = _estEma < 0f ? Mathf.Max(WindowGpuMs, 0f) : Mathf.Lerp(_estEma, Mathf.Max(WindowGpuMs, 0f), a);
            _mandatoryEma = Mathf.Lerp(_mandatoryEma, Mathf.Max(MandatoryGpuMs, 0f), a);
            float floor = Mathf.Max(_mandatoryEma, 0.1f);
            float decay = Mathf.Max(0f, _settings.GpuDecayPerSecond);
            if (_bound)
            {
                _unboundFor = 0f;
                if (float.IsInfinity(_budget))
                    _budget = Mathf.Max(_estEma, 0.5f);
                _budget = Mathf.Max(floor, _budget * (1f - decay * dt));
                if (now >= _stepAt)
                {
                    _factor = Mathf.Max(_settings.DynResFloor, _factor * _settings.DynResStep);
                    _stepAt = now + 0.5f;
                }
            }
            else
            {
                _unboundFor += dt;
                if (!float.IsInfinity(_budget))
                {
                    _budget *= 1f + 0.5f * decay * dt;
                    if (_unboundFor >= LiftAfter)
                        _budget = float.PositiveInfinity;
                }
                if (_factor < 1f && now >= _stepAt)
                {
                    _factor = Mathf.Min(1f, _factor / Mathf.Max(_settings.DynResStep, 0.05f));
                    _stepAt = now + 1f;
                }
            }
        }

        public void Dispose()
        {
            Uninstall();
            _disposed = true;
        }
    }
}
