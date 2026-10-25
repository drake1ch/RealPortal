using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace RealPortal
{
    // OWNER: gpu (S3). Window occlusion (§4.8) and per-render depth statistics: WindowCount.compute counts the id mask the
    // composite writes (visible and disoccluded pixels per VisId) and reduces each render's linear depth (min, max non-sky,
    // water share); both read back with CommandBuffer.RequestAsyncReadback into a ring of WindowPasses.ReadbackRing
    // entries, applied by Collect. Contract: IWindowVisibility in WindowContracts.cs; GPU names: Docs\S2S3-CONTRACTS.md §8.
    // Ready = false for an old bundle without WindowCount.compute, or a device without compute shaders or async readback.
    //
    // One ring entry per frame (frame % ReadbackRing), each with its own GPU buffers, so a frame's results never mix with
    // another's; an entry whose readbacks are still in flight is not reused (that frame is not counted). The callbacks
    // only copy the data into their entry (they come at some later point of the player loop); Collect (from Plan, or the
    // start of RenderChosen without Plan) applies finished entries oldest first, so state changes at one known point.
    // Ids are only reused a few frames after their window went, so a late result cannot land on another window.
    public sealed partial class WindowVisibility : IWindowVisibility
    {
        readonly WindowPipelineSettings _settings;
        readonly ComputeShader _count;
        readonly int _kClear = -1, _kCount = -1, _kStatsClear = -1, _kDepthStats = -1;
        readonly bool _supported;
        bool _disposed, _failedLogged;

        // A request that never comes back (the game camera did not render that frame) is given up after this many frames.
        const int GiveUpFrames = 15;

        [StructLayout(LayoutKind.Explicit)]
        struct Bits
        {
            [FieldOffset(0)] public uint U;
            [FieldOffset(0)] public float F;
        }

        sealed class Entry
        {
            public ComputeBuffer Counts, Stats;
            public readonly uint[] CountData = new uint[WindowPasses.CountEntries];
            public readonly uint[] StatsData = new uint[WindowPasses.StatsSlots * WindowPasses.StatsStride];
            // VisIds the composite was expected to draw (in the frustum) that frame: only those count a 0 as "hidden".
            public readonly ulong[] Expected = new ulong[4];
            public int Frame = -100, StatsCount;
            public bool StatsCleared, Scheduled, WantCounts, WantStats, CountsDone, StatsDone, Failed;
            public Action<AsyncGPUReadbackRequest> OnCounts, OnStats;

            public bool Finished => (!WantCounts || CountsDone) && (!WantStats || StatsDone);
        }

        readonly Entry[] _ring = new Entry[WindowPasses.ReadbackRing];
        readonly List<Entry> _orphans = new List<Entry>();
        readonly Entry[] _order = new Entry[WindowPasses.ReadbackRing];
        Entry _cur;
        int _curFrame = -100;
        readonly WindowState[] _owner = new WindowState[256];
        readonly int[] _freedAt = new int[256];
        readonly int[] _ints = new int[4];

        /// <summary>World water level (m) for the water share (the manager sets it from IWindowWorld.WaterLevel).</summary>
        internal float WaterLevel = 30f;

        public WindowVisibility(WindowPipelineSettings settings, ComputeShader count)
        {
            _settings = settings;
            _count = count;
            for (int i = 0; i < _freedAt.Length; i++)
                _freedAt[i] = -1000;
            try
            {
                _supported = count != null && SystemInfo.supportsComputeShaders && SystemInfo.supportsAsyncGPUReadback
                             && count.HasKernel(WindowPasses.KernelClear) && count.HasKernel(WindowPasses.KernelCount)
                             && count.HasKernel(WindowPasses.KernelStatsClear) && count.HasKernel(WindowPasses.KernelDepthStats);
                if (_supported)
                {
                    _kClear = count.FindKernel(WindowPasses.KernelClear);
                    _kCount = count.FindKernel(WindowPasses.KernelCount);
                    _kStatsClear = count.FindKernel(WindowPasses.KernelStatsClear);
                    _kDepthStats = count.FindKernel(WindowPasses.KernelDepthStats);
                }
            }
            catch (Exception e)
            {
                _supported = false;
                WindowLog.Warn("WindowVisibility: WindowCount.compute unusable (Win.Occlusion and the depth statistics stay off): " + e.Message);
            }
        }

        public bool Ready => _supported && !_disposed;
        public int LatencyFrames { get; private set; } = 2;

        // ---- Ids ------------------------------------------------------------------------------------------------------

        public void Register(WindowState s)
        {
            if (s == null || s.VisId > 0 || _disposed)
                return;
            int frame = Time.frameCount;
            for (int id = 1; id < 256; id++)
            {
                if (_owner[id] != null || frame - _freedAt[id] <= WindowPasses.ReadbackRing + GiveUpFrames)
                    continue;
                _owner[id] = s;
                s.VisId = id;
                s.VisIdFrame = frame;
                s.VisiblePx = -1;
                s.VisibleFrame = -100;
                s.ZeroReadbacks = 0;
                s.DisoccludedPx = 0;
                return;
            }
        }

        public void Unregister(WindowState s)
        {
            if (s == null)
                return;
            int id = s.VisId;
            if (id > 0 && id < 256 && _owner[id] == s)
            {
                _owner[id] = null;
                _freedAt[id] = Time.frameCount;
            }
            s.VisId = 0;
            s.VisiblePx = -1;
            s.VisibleFrame = -100;
            s.ZeroReadbacks = 0;
            s.DisoccludedPx = 0;
        }

        // ---- This frame's entry ---------------------------------------------------------------------------------------

        Entry EntryFor(int frame)
        {
            if (_curFrame == frame)
                return _cur;
            _curFrame = frame;
            _cur = null;
            int k = frame % _ring.Length;
            if (k < 0)
                k += _ring.Length;
            Entry e = _ring[k];
            if (e != null && e.Scheduled && !e.Finished && !e.Failed)
            {
                if (frame - e.Frame <= GiveUpFrames)
                    return null;
                // Its callbacks may still come: they write into the orphan, never into this frame's entry.
                _orphans.Add(e);
                e = null;
            }
            if (e == null)
                e = _ring[k] = NewEntry();
            e.Frame = frame;
            e.StatsCount = 0;
            e.StatsCleared = e.Scheduled = e.WantCounts = e.WantStats = e.CountsDone = e.StatsDone = e.Failed = false;
            Array.Clear(e.Expected, 0, e.Expected.Length);
            _cur = e;
            return e;
        }

        Entry NewEntry()
        {
            var e = new Entry();
            e.OnCounts = r => Done(e, r, true);
            e.OnStats = r => Done(e, r, false);
            return e;
        }

        // Readback callback: copy only (Collect applies it).
        void Done(Entry e, AsyncGPUReadbackRequest r, bool counts)
        {
            if (r.hasError)
            {
                e.Failed = true;
                if (!_failedLogged)
                {
                    _failedLogged = true;
                    WindowLog.Warn("WindowVisibility: a readback failed (that frame is not counted).");
                }
            }
            else if (counts)
            {
                NativeArray<uint> data = r.GetData<uint>();
                NativeArray<uint>.Copy(data, e.CountData, Mathf.Min(data.Length, e.CountData.Length));
            }
            else
            {
                NativeArray<uint> data = r.GetData<uint>();
                NativeArray<uint>.Copy(data, e.StatsData, Mathf.Min(data.Length, e.StatsData.Length));
            }
            if (counts)
                e.CountsDone = true;
            else
                e.StatsDone = true;
        }

        static void EnsureBuffers(Entry e)
        {
            if (e.Counts == null || !e.Counts.IsValid())
                e.Counts = new ComputeBuffer(WindowPasses.CountEntries, sizeof(uint));
            if (e.Stats == null || !e.Stats.IsValid())
                e.Stats = new ComputeBuffer(WindowPasses.StatsSlots * WindowPasses.StatsStride, sizeof(uint));
        }

        /// <summary>
        /// The windows whose ids the composite draws this frame (in the frustum): only their readbacks may count 0 visible
        /// pixels as hidden (a window out of the frustum has 0 because nothing drew it). The manager calls it once per frame
        /// before the composite.
        /// </summary>
        internal void Expect(IReadOnlyList<WindowState> states)
        {
            if (!Ready)
                return;
            Entry e = EntryFor(Time.frameCount);
            if (e == null)
                return;
            Array.Clear(e.Expected, 0, e.Expected.Length);
            for (int i = 0; i < states.Count; i++)
            {
                WindowState s = states[i];
                int id = s.VisId;
                if (id > 0 && id < 256 && s.InFrustum)
                    e.Expected[id >> 6] |= 1UL << (id & 63);
            }
        }

        /// <summary>Scheduled for this frame already (Schedule records once per frame, whoever calls it first).</summary>
        internal bool ScheduledThisFrame => _curFrame == Time.frameCount && _cur != null && _cur.Scheduled;

        // ---- Recording ------------------------------------------------------------------------------------------------

        public int AppendDepthStats(CommandBuffer capture, WindowState s, RenderTargetIdentifier linearDepth, int width, int height, Camera cam)
        {
            if (!Ready || capture == null || s == null || cam == null || width <= 0 || height <= 0)
                return -1;
            int frame = Time.frameCount;
            Entry e = EntryFor(frame);
            if (e == null || e.Scheduled || e.StatsCount >= WindowPasses.StatsSlots)
                return -1;
            EnsureBuffers(e);
            ComputeShader cs = _count;
            if (!e.StatsCleared)
            {
                // Before the frame's first dispatch on the GPU timeline: the capture buffers run in render order.
                capture.SetComputeBufferParam(cs, _kStatsClear, WindowShaderIds.Stats, e.Stats);
                capture.DispatchCompute(cs, _kStatsClear, 1, 1, 1);
                e.StatsCleared = true;
            }
            int index = e.StatsCount++;
            capture.SetComputeTextureParam(cs, _kDepthStats, WindowShaderIds.StatsDepth, linearDepth);
            _ints[0] = width;
            _ints[1] = height;
            _ints[2] = index;
            _ints[3] = 0;
            capture.SetComputeIntParams(cs, WindowShaderIds.StatsSize, _ints);
            // Sky: what the linear depth puts at (nearly) the far plane.
            capture.SetComputeVectorParam(cs, WindowShaderIds.StatsParams, new Vector4(0.98f * cam.farClipPlane, WaterLevel, 0f, 0f));
            capture.SetComputeMatrixParam(cs, WindowShaderIds.StatsCamToWorld, cam.cameraToWorldMatrix);
            Matrix4x4 p = cam.projectionMatrix;
            capture.SetComputeVectorParam(cs, WindowShaderIds.StatsProj, new Vector4(p.m00, p.m11, p.m02, p.m12));
            capture.SetComputeBufferParam(cs, _kDepthStats, WindowShaderIds.Stats, e.Stats);
            capture.DispatchCompute(cs, _kDepthStats, (width + 7) / 8, (height + 7) / 8, 1);
            e.WantStats = true;
            s.Cache.StatsIndex = index;
            s.Cache.StatsFrame = frame;
            return index;
        }

        public void Schedule(CommandBuffer afterEverything, RenderTargetIdentifier idTexture, int width, int height, bool counts)
        {
            if (!Ready || afterEverything == null)
                return;
            Entry e = EntryFor(Time.frameCount);
            // Once a frame: the composite may call it and the manager after it (whoever comes first records).
            if (e == null || e.Scheduled)
                return;
            bool doCounts = counts && width > 0 && height > 0;
            if (!doCounts && !e.WantStats)
                return;
            EnsureBuffers(e);
            ComputeShader cs = _count;
            if (doCounts)
            {
                afterEverything.SetComputeBufferParam(cs, _kClear, WindowShaderIds.Counts, e.Counts);
                afterEverything.DispatchCompute(cs, _kClear, WindowPasses.CountEntries / 64, 1, 1);
                afterEverything.SetComputeTextureParam(cs, _kCount, WindowShaderIds.IdTex, idTexture);
                _ints[0] = width;
                _ints[1] = height;
                _ints[2] = 0;
                _ints[3] = 0;
                afterEverything.SetComputeIntParams(cs, WindowShaderIds.IdSize, _ints);
                afterEverything.SetComputeBufferParam(cs, _kCount, WindowShaderIds.Counts, e.Counts);
                afterEverything.DispatchCompute(cs, _kCount, (width + 7) / 8, (height + 7) / 8, 1);
                afterEverything.RequestAsyncReadback(e.Counts, e.OnCounts);
                e.WantCounts = true;
            }
            if (e.WantStats)
                afterEverything.RequestAsyncReadback(e.Stats, e.StatsCount * WindowPasses.StatsStride * sizeof(uint), 0, e.OnStats);
            e.Scheduled = true;
        }

        // ---- Results --------------------------------------------------------------------------------------------------

        public void Collect(int frame, IReadOnlyList<WindowState> states)
        {
            if (_disposed)
                return;
            int n = 0;
            for (int k = 0; k < _ring.Length; k++)
            {
                Entry e = _ring[k];
                if (e != null && e.Scheduled && e.Frame < frame && (e.Finished || e.Failed))
                    _order[n++] = e;
            }
            // Oldest first, so a newer frame's result is the one that stays.
            for (int i = 1; i < n; i++)
                for (int j = i; j > 0 && _order[j].Frame < _order[j - 1].Frame; j--)
                {
                    Entry t = _order[j];
                    _order[j] = _order[j - 1];
                    _order[j - 1] = t;
                }
            for (int i = 0; i < n; i++)
            {
                Entry e = _order[i];
                if (!e.Failed)
                    Apply(e, states, frame);
                e.Scheduled = false;
                _order[i] = null;
            }
            // Orphans whose late callbacks came (or never will): their buffers go.
            for (int i = _orphans.Count - 1; i >= 0; i--)
            {
                Entry o = _orphans[i];
                if (o.Finished || o.Failed || frame - o.Frame > 4 * GiveUpFrames)
                {
                    Release(o);
                    _orphans.RemoveAt(i);
                }
            }
        }

        void Apply(Entry e, IReadOnlyList<WindowState> states, int frame)
        {
            if (e.WantCounts && e.CountsDone)
            {
                for (int i = 0; i < states.Count; i++)
                {
                    WindowState s = states[i];
                    int id = s.VisId;
                    if (id <= 0 || id >= 256 || s.VisIdFrame > e.Frame || e.Frame <= s.VisibleFrame)
                        continue;
                    if ((e.Expected[id >> 6] & (1UL << (id & 63))) == 0)
                        continue;
                    int px = (int)Math.Min(e.CountData[id], int.MaxValue);
                    s.VisiblePx = px;
                    s.VisibleFrame = e.Frame;
                    s.ZeroReadbacks = px == 0 ? s.ZeroReadbacks + 1 : 0;
                    s.DisoccludedPx = (int)Math.Min(e.CountData[256 + id], int.MaxValue);
                }
                LatencyFrames = frame - e.Frame;
            }
            if (e.WantStats && e.StatsDone)
            {
                var b = new Bits();
                for (int i = 0; i < states.Count; i++)
                {
                    WindowCache c = states[i].Cache;
                    if (c.StatsFrame != e.Frame || c.StatsIndex < 0 || c.StatsIndex >= e.StatsCount)
                        continue;
                    int k = c.StatsIndex * WindowPasses.StatsStride;
                    uint lo = e.StatsData[k], hi = e.StatsData[k + 1], water = e.StatsData[k + 2], valid = e.StatsData[k + 3];
                    b.U = lo;
                    float zmin = b.F;
                    b.U = hi;
                    float zmax = b.F;
                    if (valid == 0 || !(zmin <= zmax) || float.IsInfinity(zmin) || zmin >= float.MaxValue)
                    {
                        // All sky: nothing near, nothing to parallax.
                        c.ZNear = c.ZFar = c.Far;
                    }
                    else
                    {
                        c.ZNear = Mathf.Max(zmin, c.Near);
                        c.ZFar = Mathf.Max(zmax, c.ZNear);
                    }
                    c.WaterFrac = valid > 0 ? Mathf.Clamp01(water / (float)valid) : 0f;
                    c.StatsKnown = true;
                }
                if (!e.WantCounts)
                    LatencyFrames = frame - e.Frame;
            }
        }

        public void Reset()
        {
            for (int k = 0; k < _ring.Length; k++)
            {
                Entry e = _ring[k];
                if (e == null)
                    continue;
                if (e.Scheduled && !e.Finished && !e.Failed)
                {
                    _orphans.Add(e);
                    _ring[k] = null;
                }
                else
                    e.Scheduled = false;
            }
            _curFrame = -100;
            _cur = null;
        }

        static void Release(Entry e)
        {
            e.Counts?.Release();
            e.Stats?.Release();
            e.Counts = e.Stats = null;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                // Buffers with readbacks in flight are released only once those finished.
                AsyncGPUReadback.WaitAllRequests();
            }
            catch (Exception)
            {
            }
            for (int k = 0; k < _ring.Length; k++)
                if (_ring[k] != null)
                    Release(_ring[k]);
            foreach (Entry o in _orphans)
                Release(o);
            _orphans.Clear();
            for (int i = 0; i < _owner.Length; i++)
                _owner[i] = null;
        }

        // ---- Hidden on the slot path (Docs\FARWORLD-V3.md §4.8, V0): the count scheduler, Win.Tiers not in force ------

        // The main camera's pose by frame (& 7) as the count scheduler rendered it (what that frame's counts saw), and the
        // frame each entry is for.
        readonly Vector3[] _poseEye = new Vector3[8];
        readonly Quaternion[] _poseRot = new Quaternion[8];
        readonly int[] _poseFrame = { -100, -100, -100, -100, -100, -100, -100, -100 };

        /// <summary>The count scheduler renders `frame` with the main camera at eye/rot (the manager, once a frame).</summary>
        internal void NotePose(int frame, Vector3 eye, Quaternion rot)
        {
            int k = frame & 7;
            _poseEye[k] = eye;
            _poseRot[k] = rot;
            _poseFrame[k] = frame;
        }

        /// <summary>
        /// The conservative Hidden of §4.8: true only when nothing can have shown `s` since its counts last saw none of it.
        /// HiddenAfterZeroReadbacks readbacks in a row counted 0 px, the last at most ReadbackMaxAge frames old (its frame
        /// F); the opening's rect now (s.RectPx, this frame's) is no larger than at F (HiddenRectGrowth only for rounding);
        /// and the camera has all but stood still since F (HiddenEyeMove, HiddenTurnDegrees): an occluder's edge sweeps
        /// across a window as the eye moves, and what F counted arrives frames late. Without the rect or the pose of F
        /// (another path rendered it) it is not hidden: any doubt and the window renders this frame, as a window coming
        /// into view does. Win.OcclusionStatic: nor while something that moves stands between the eye and the opening
        /// (MovingBetween): an occluder can also move while the camera stands. The caller adds what it knows besides
        /// (crossing, nothing rendered yet, the lab's cadence).
        /// </summary>
        internal bool HiddenNow(WindowState s, int frame, Vector3 eye, Quaternion rot)
        {
            if (!Ready || s == null || s.VisId == 0)
                return false;
            int seen = s.VisibleFrame;
            if (s.ZeroReadbacks < Mathf.Max(1, _settings.HiddenAfterZeroReadbacks) || seen >= frame || seen < frame - _settings.ReadbackMaxAge)
                return false;
            int k = seen & 7;
            if (_poseFrame[k] != seen || s.RectPxFrame[k] != seen)
                return false;
            if (s.RectPx > s.RectPxRing[k] * (1f + _settings.HiddenRectGrowth))
                return false;
            float move = _settings.HiddenEyeMove;
            if ((eye - _poseEye[k]).sqrMagnitude > move * move)
                return false;
            if (Quaternion.Angle(rot, _poseRot[k]) > _settings.HiddenTurnDegrees)
                return false;
            // Win.OcclusionStatic (Docs\PARITY-PLAN.md P25): hidden only behind things that stay put.
            if (WindowFlags.OcclusionStatic && MovingBetween(s.Window, eye))
            {
                LabOcclusionDynamicVeto++;
                return false;
            }
            return true;
        }

        // ---- Win.OcclusionStatic (Docs\PARITY-PLAN.md P25): hidden only behind static things ---------------------------

        /// <summary>Lab: windows HiddenNow would have hidden and let render instead, because something that moves stood
        /// between the camera and them (Win.OcclusionStatic), since start.</summary>
        internal static long LabOcclusionDynamicVeto;

        // The colliders of one box query (the test stops at the first moving one; a full buffer counts as moving).
        static readonly Collider[] s_hits = new Collider[512];
        // The slabs along the view the pyramid's query is split into (fractions of its depth): the boxes follow its
        // widening, so they hold about half as much beside it as one box would.
        static readonly float[] s_slabs = { 0f, 0.5f, 0.8f, 1f };
        // The eye->opening pyramid: its four sides and the opening's plane, normals inward; the opening's corners.
        readonly Plane[] _pyramid = new Plane[5];
        readonly Vector3[] _corners = new Vector3[4];
        // UnityEngine.Animator, by name (the AnimationModule, which the plugin does not reference): doors, gates and
        // the other animated pieces.
        static Type s_animator;
        static bool s_animatorLooked;

        static Type AnimatorType()
        {
            if (s_animatorLooked)
                return s_animator;
            s_animatorLooked = true;
            try
            {
                s_animator = Type.GetType("UnityEngine.Animator, UnityEngine.AnimationModule", false);
            }
            catch (Exception)
            {
                s_animator = null;
            }
            if (s_animator == null)
                WindowLog.Warn("WindowVisibility: UnityEngine.Animator not found; Win.OcclusionStatic takes only bodies with a Rigidbody as moving.");
            return s_animator;
        }

        /// <summary>
        /// Win.OcclusionStatic: something that can move while the camera stands (a body with a Rigidbody: characters, the
        /// player included, ships, carts, items; or anything under an Animator: doors, gates, other animated pieces) has
        /// its collider within MovingMargin of the pyramid from `eye` to the opening of `w`. The counts that hid the window
        /// are frames old, and such a thing can uncover it meanwhile. The pyramid's base is the opening's bounding
        /// rectangle; three boxes along the view (slabs) cover it for the physics query, and each moving collider's bounds,
        /// grown by the margin (its mesh reaching past it, transforms not yet synced to physics), are tested against the
        /// pyramid's planes. The window's own portal is left out (it frames the opening). Any doubt (the eye on the
        /// opening's plane, a full hit buffer) counts as moving: the window renders. Main thread, nothing allocated.
        /// </summary>
        bool MovingBetween(PortalWindow w, Vector3 eye)
        {
            if (w == null)
                return false;
            PortalFrame f = w.Frame;
            float d = f.SignedDistance(eye);
            if (Mathf.Abs(d) < 0.05f)
                return true;
            // The opening's bounding rectangle (a shaped opening's points are in units of HalfSize and joined straight).
            float hx = f.HalfSize.x, hy = f.HalfSize.y;
            if (f.Shape != null && f.Shape.Length >= 3)
            {
                float sx = 0f, sy = 0f;
                for (int i = 0; i < f.Shape.Length; i++)
                {
                    sx = Mathf.Max(sx, Mathf.Abs(f.Shape[i].x));
                    sy = Mathf.Max(sy, Mathf.Abs(f.Shape[i].y));
                }
                hx *= sx;
                hy *= sy;
            }
            Vector3 right = f.Rotation * Vector3.right, up = f.Rotation * Vector3.up;
            _corners[0] = f.Center - right * hx - up * hy;
            _corners[1] = f.Center + right * hx - up * hy;
            _corners[2] = f.Center + right * hx + up * hy;
            _corners[3] = f.Center - right * hx + up * hy;
            // Planes: the sides through the eye and each edge, and the opening's own (facing the eye), all turned to face
            // a point inside.
            Vector3 inside = Vector3.Lerp(eye, f.Center, 0.75f);
            for (int i = 0; i < 4; i++)
            {
                var p = new Plane(eye, _corners[i], _corners[(i + 1) & 3]);
                _pyramid[i] = p.GetDistanceToPoint(inside) < 0f ? p.flipped : p;
            }
            _pyramid[4] = new Plane(d > 0f ? f.Forward : -f.Forward, f.Center);

            // The query frame: z from the eye towards the opening's centre.
            Vector3 fwd = f.Center - eye;
            float len = fwd.magnitude;
            if (len < 1e-3f)
                return true;
            fwd /= len;
            Vector3 xAxis = Vector3.Cross(up, fwd);
            if (xAxis.sqrMagnitude < 1e-6f)
                xAxis = Vector3.Cross(right, fwd);
            xAxis.Normalize();
            Vector3 yAxis = Vector3.Cross(fwd, xAxis);
            Quaternion orient = Quaternion.LookRotation(fwd, yAxis);
            // The corners' slopes (x/z, y/z) bound every cross-section of the pyramid (its points are s·q for q on the
            // opening, and q.x/q.z is largest and smallest at a corner); a corner at or behind the eye's depth: one box
            // round the eye and the corners.
            float kx0 = float.MaxValue, kx1 = float.MinValue, ky0 = float.MaxValue, ky1 = float.MinValue;
            float zMax = 0f, bx0 = 0f, bx1 = 0f, by0 = 0f, by1 = 0f;
            bool slabs = true;
            for (int i = 0; i < 4; i++)
            {
                Vector3 v = _corners[i] - eye;
                float x = Vector3.Dot(v, xAxis), y = Vector3.Dot(v, yAxis), z = Vector3.Dot(v, fwd);
                zMax = Mathf.Max(zMax, z);
                bx0 = Mathf.Min(bx0, x);
                bx1 = Mathf.Max(bx1, x);
                by0 = Mathf.Min(by0, y);
                by1 = Mathf.Max(by1, y);
                if (z <= 0.01f)
                {
                    slabs = false;
                    continue;
                }
                kx0 = Mathf.Min(kx0, x / z);
                kx1 = Mathf.Max(kx1, x / z);
                ky0 = Mathf.Min(ky0, y / z);
                ky1 = Mathf.Max(ky1, y / z);
            }
            float m = Mathf.Max(0f, _settings.MovingMargin);
            Transform own = w.Root != null ? w.Root.transform.parent : null;
            int boxes = slabs ? s_slabs.Length - 1 : 1;
            for (int j = 0; j < boxes; j++)
            {
                float z0, z1, x0, x1, y0, y1;
                if (slabs)
                {
                    z0 = zMax * s_slabs[j];
                    z1 = zMax * s_slabs[j + 1];
                    x0 = Mathf.Min(kx0 * z0, kx0 * z1);
                    x1 = Mathf.Max(kx1 * z0, kx1 * z1);
                    y0 = Mathf.Min(ky0 * z0, ky0 * z1);
                    y1 = Mathf.Max(ky1 * z0, ky1 * z1);
                }
                else
                {
                    z0 = Mathf.Min(0f, MinDepth(eye, fwd));
                    z1 = zMax;
                    x0 = bx0;
                    x1 = bx1;
                    y0 = by0;
                    y1 = by1;
                }
                Vector3 center = eye + xAxis * ((x0 + x1) * 0.5f) + yAxis * ((y0 + y1) * 0.5f) + fwd * ((z0 + z1) * 0.5f);
                var half = new Vector3((x1 - x0) * 0.5f + m, (y1 - y0) * 0.5f + m, (z1 - z0) * 0.5f + m);
                int n = Physics.OverlapBoxNonAlloc(center, half, s_hits, orient, Physics.AllLayers, QueryTriggerInteraction.Ignore);
                if (n >= s_hits.Length)
                {
                    Array.Clear(s_hits, 0, n);
                    return true;
                }
                bool moving = false;
                for (int i = 0; i < n && !moving; i++)
                    moving = Moving(s_hits[i], own, m);
                Array.Clear(s_hits, 0, n);
                if (moving)
                    return true;
            }
            return false;
        }

        // The nearest corner's depth along `fwd` from the eye (a corner behind the eye is negative).
        float MinDepth(Vector3 eye, Vector3 fwd)
        {
            float z = float.MaxValue;
            for (int i = 0; i < 4; i++)
                z = Mathf.Min(z, Vector3.Dot(_corners[i] - eye, fwd));
            return z;
        }

        // `c` can move by itself and its bounds, grown by `margin`, reach into the pyramid (not part of the window's portal).
        bool Moving(Collider c, Transform own, float margin)
        {
            if (c == null)
                return false;
            Rigidbody body = c.attachedRigidbody;
            Type animator = body == null ? AnimatorType() : null;
            if (body == null && animator == null)
                return false;
            Bounds b = c.bounds;
            b.Expand(2f * margin);
            if (!InPyramid(b))
                return false;
            if (own != null && c.transform.IsChildOf(own))
                return false;
            return body != null || c.GetComponentInParent(animator) != null;
        }

        // The box touches the pyramid by its planes (conservative near the edges: a box outside but across a corner counts).
        bool InPyramid(in Bounds b)
        {
            Vector3 c = b.center, e = b.extents;
            for (int i = 0; i < _pyramid.Length; i++)
            {
                Vector3 n = _pyramid[i].normal;
                float r = Mathf.Abs(n.x) * e.x + Mathf.Abs(n.y) * e.y + Mathf.Abs(n.z) * e.z;
                if (Vector3.Dot(n, c) + _pyramid[i].distance + r < 0f)
                    return false;
            }
            return true;
        }
    }

    public sealed partial class WindowState
    {
        // WindowVisibility: the frame its VisId was given (a readback from before belongs to another window or none).
        internal int VisIdFrame = -100;
        // The slot path's Hidden (HiddenNow): the frame each RectPxRing entry was written for by the count scheduler
        // (WindowManager.LegacyCandidate), and the frame it was last found hidden there.
        internal readonly int[] RectPxFrame = { -100, -100, -100, -100, -100, -100, -100, -100 };
        internal int LegacyHiddenFrame = -100;
    }

    public sealed partial class WindowPipelineSettings
    {
        /// <summary>The slot path's Hidden (WindowVisibility.HiddenNow): how far (m) the eye may have moved, and how far
        /// (degrees) the camera may have turned, since the frame of the readback that saw the window hidden, and how much
        /// larger its rect may be (rounding).</summary>
        public float HiddenEyeMove = 0.05f, HiddenTurnDegrees = 2f, HiddenRectGrowth = 0.02f;
        /// <summary>The slot path's Hidden (WindowManager.LegacyOccluded): a window hidden for more frames than this since
        /// its last render renders once anyway (0: never, as §4.8 says; lab-tunable until ghostab's occluder removal
        /// decides).</summary>
        public int HiddenMaxFrames = 0;
        /// <summary>Win.OcclusionStatic (WindowVisibility.MovingBetween): how far (m) a moving collider may stand outside the
        /// eye-to-opening pyramid and still keep a hidden window rendering (a mesh reaching past its collider, physics a
        /// step behind the transforms).</summary>
        public float MovingMargin = 1f;
    }
}
