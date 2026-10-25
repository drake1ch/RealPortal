using System;
using System.Collections.Generic;
using UnityEngine;

namespace RealPortal
{
    // OWNER: slots (S2). One Camera per render slot (§4.3): each renders at most once per frame, so every camera-targeted
    // draw for it is submitted right before that one Render() (P3(ii): a draw is consumed by the camera's first Render()
    // of the frame, and one queued for a camera that does not render leaks into its next frame). Set up once, not CopyFrom
    // per frame. Contract: IWindowCameraPool in WindowContracts.cs; rules: Docs\S2S3-CONTRACTS.md §7.
    // (The code map's WindowCamera.cs is WindowSlot here: a type named WindowCamera would clash with
    // PortalSystem.WindowCamera inside the plugin. WindowSlot's setup lives in WindowCamera.cs.)
    //
    // Which slot a render gets (Acquire): the window's own slot while it is free this frame (Amplify's history, the
    // camera's previous view-projection and Unity's per-camera render textures then all belong to that window), else the
    // least recently used free slot no window holds, else a new camera, else the least recently used slot of a window
    // that neither keeps it (Params.Sticky) nor renders this frame. With 16 slots and fewer windows on screen every window
    // in practice keeps its own camera. None left: the caller renders on the legacy shared camera.
    public sealed partial class WindowCameraPool : IWindowCameraPool
    {
        readonly WindowPipelineSettings _settings;
        readonly List<WindowSlot> _slots = new List<WindowSlot>(16);
        // Bumped by SettingsChanged: every slot is set up again before its next render.
        int _version = 1;
        int _frame = -100, _acquired, _stickyHits, _count;
        float _sweepAt;
        bool _disposed;

        /// <summary>A slot camera nobody rendered with for this long is destroyed (its Amplify textures and command buffers go with it).</summary>
        public float IdleSeconds = 30f;
        /// <summary>Win.SlotPrewarm: IdleSeconds while portals are live (the manager's sweep picks it).</summary>
        public float LiveIdleSeconds = 600f;
        /// <summary>A sticky window keeps its slot this long after it was last in a visible tier (out of the frustum or
        /// hidden behind something it may come back at once, with its AO history); after that LRU may take it.</summary>
        public float StickyGraceSeconds = 1.5f;

        public WindowCameraPool(WindowPipelineSettings settings)
        {
            _settings = settings;
        }

        public bool Ready => !_disposed;
        public int Capacity => _settings != null ? Mathf.Clamp(_settings.SlotCapacity, 1, 16) : 16;
        public int Count => _count;
        public int AcquiredThisFrame => _frame == Time.frameCount ? _acquired : 0;
        public int StickyHits => _frame == Time.frameCount ? _stickyHits : 0;

        public event Action<Camera> Created;
        public event Action<Camera> Destroyed;

        public Camera CameraAt(int slot) => slot >= 0 && slot < _slots.Count && _slots[slot].Camera != null ? _slots[slot].Camera : null;

        // Asked for any camera, often (the plugin's Amplify patches): references only, no Unity null checks.
        public int SlotOf(Camera cam)
        {
            if (ReferenceEquals(cam, null))
                return -1;
            for (int i = 0; i < _slots.Count; i++)
                if (ReferenceEquals(_slots[i].Camera, cam))
                    return i;
            return -1;
        }

        public bool IsSlotCamera(Camera cam) => SlotOf(cam) >= 0;

        public Camera Acquire(WindowState s, Camera main, PortalRenderSettings settings)
        {
            if (_disposed || s == null || main == null)
                return null;
            int frame = Time.frameCount;
            float now = Time.unscaledTime;
            if (frame != _frame)
            {
                _frame = frame;
                _acquired = 0;
                _stickyHits = 0;
            }
            WindowSlot slot = null;
            bool own = false;
            // Its own slot, while no other window took it and it has not rendered this frame.
            if (s.StickySlot >= 0 && s.StickySlot < _slots.Count)
            {
                WindowSlot mine = _slots[s.StickySlot];
                if (mine.RenderedFrame != frame && (mine.StickyOwner == s || mine.StickyOwner == null))
                {
                    slot = mine;
                    own = mine.StickyOwner == s && mine.Camera != null;
                }
            }
            // A live camera no window holds.
            if (slot == null)
                slot = Lru(frame, now, null, freeOnly: true);
            // A new camera (into a slot whose camera was destroyed for idling, or a new slot).
            if (slot == null)
                slot = NewSlot();
            // A slot of a window that does not keep it and does not render this frame.
            if (slot == null)
                slot = Lru(frame, now, s, freeOnly: false);
            if (slot == null)
                return null;
            TierParams p = s.ParamsNow(frame);
            if (!slot.Ensure(this, main, settings, p, _version))
                return null;
            WindowState before = slot.StickyOwner;
            if (before != null && before != s && before.StickySlot == slot.Index)
                before.StickySlot = -1;
            slot.StickyOwner = s;
            slot.RenderedFrame = frame;
            slot.UsedAt = now;
            s.Slot = slot.Index;
            s.StickySlot = slot.Index;
            _acquired++;
            if (own)
                _stickyHits++;
            slot.Apply(main, settings, p, _version);
            return slot.Camera;
        }

        // Least recently used slot not rendered this frame: freeOnly, a live camera no window holds; else one whose window
        // neither keeps it nor renders this frame.
        WindowSlot Lru(int frame, float now, WindowState asking, bool freeOnly)
        {
            WindowSlot best = null;
            for (int i = 0; i < _slots.Count; i++)
            {
                WindowSlot c = _slots[i];
                if (c.RenderedFrame == frame || c.Camera == null)
                    continue;
                WindowState holder = c.StickyOwner;
                if (freeOnly ? holder != null : holder == null || holder == asking || Keeps(holder, frame, now))
                    continue;
                if (best == null || c.UsedAt < best.UsedAt)
                    best = c;
            }
            return best;
        }

        // The holder keeps its slot while its tier wants it sticky (Live; NearCached with AO) and it is on screen or was
        // within StickyGraceSeconds (its params keep the last visible tier's while it is out of sight, so the tier alone
        // would hold the slot for good: a hub of 20 portals walked past would pin every slot), and a window that renders
        // later this frame wants its slot back too.
        bool Keeps(WindowState holder, int frame, float now)
        {
            if (holder.ParamsFrame >= frame - 1 && holder.Params.Sticky && now - holder.VisibleAt <= StickyGraceSeconds)
                return true;
            return holder.Chosen && holder.PlannedFrame == frame && holder.Cache.Frame != frame;
        }

        WindowSlot NewSlot()
        {
            for (int i = 0; i < _slots.Count; i++)
                if (_slots[i].Camera == null && _slots[i].StickyOwner == null)
                    return _slots[i];
            if (_slots.Count >= Capacity)
                return null;
            var slot = new WindowSlot { Index = _slots.Count };
            _slots.Add(slot);
            return slot;
        }

        /// <summary>
        /// Win.SlotPrewarm: makes the camera of the first slot among the first `count` that has none (set up from `main`
        /// as Acquire would, Created raised, so the plugin's handlers attach) and returns it; null when they all have one or
        /// none can be made. One camera a call: the caller spreads them over frames. A prewarmed slot is free (no window
        /// holds it), so Acquire hands it out before it would make a camera. A slot held by a window whose camera someone
        /// else destroyed is left to that window's next Acquire.
        /// </summary>
        internal Camera Prewarm(int count, Camera main, PortalRenderSettings settings)
        {
            if (_disposed || main == null)
                return null;
            count = Mathf.Min(count, Capacity);
            for (int i = 0; i < count; i++)
            {
                WindowSlot slot;
                if (i < _slots.Count)
                {
                    slot = _slots[i];
                    if (slot.Camera != null || slot.StickyOwner != null)
                        continue;
                }
                else
                {
                    slot = new WindowSlot { Index = _slots.Count };
                    _slots.Add(slot);
                }
                if (!slot.Ensure(this, main, settings, TierParams.Legacy, _version))
                    return null;
                // Idle from now (the sweep), never rendered (Acquire's once-a-frame test).
                slot.UsedAt = Time.unscaledTime;
                return slot.Camera;
            }
            return null;
        }

        // From WindowSlot.Ensure: a slot's camera was destroyed by someone else (no Destroyed event: it is already gone).
        internal void ForgetDestroyed() => _count = Mathf.Max(0, _count - 1);

        // From WindowSlot.Ensure: a camera was made and set up.
        internal void RaiseCreated(Camera cam)
        {
            _count++;
            try
            {
                Created?.Invoke(cam);
            }
            catch (Exception e)
            {
                WindowLog.Warn("Slot camera Created handler failed: " + e);
            }
        }

        void Kill(WindowSlot slot)
        {
            Camera cam = slot.Camera;
            if (ReferenceEquals(cam, null))
                return;
            // Handlers take off what they put on (grass exclusion, fog, AO) while the camera still exists.
            try
            {
                if (cam != null)
                    Destroyed?.Invoke(cam);
            }
            catch (Exception e)
            {
                WindowLog.Warn("Slot camera Destroyed handler failed: " + e);
            }
            if (cam != null)
                PortalMath.Kill(cam.gameObject);
            slot.Camera = null;
            slot.Version = 0;
            _count = Mathf.Max(0, _count - 1);
        }

        /// <summary>Once per frame (manager): cameras idle for IdleSeconds go, at most one check a second.</summary>
        internal void Sweep(float now) => Sweep(now, IdleSeconds, false);

        /// <summary>Cameras idle for more than `idleSeconds` go (0: every one not rendering this frame); at most one check a
        /// second unless `force`.</summary>
        internal void Sweep(float now, float idleSeconds, bool force)
        {
            if (!force && now < _sweepAt)
                return;
            _sweepAt = now + 1f;
            int frame = Time.frameCount;
            for (int i = 0; i < _slots.Count; i++)
            {
                WindowSlot c = _slots[i];
                if (c.Camera == null || c.RenderedFrame == frame || now - c.UsedAt < idleSeconds)
                    continue;
                if (c.StickyOwner != null && c.StickyOwner.StickySlot == c.Index)
                    c.StickyOwner.StickySlot = -1;
                c.StickyOwner = null;
                Kill(c);
            }
        }

        public void Release(WindowState s)
        {
            if (s == null)
                return;
            for (int i = 0; i < _slots.Count; i++)
                if (_slots[i].StickyOwner == s)
                    _slots[i].StickyOwner = null;
            s.Slot = s.StickySlot = -1;
        }

        public void SettingsChanged() => _version++;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            for (int i = 0; i < _slots.Count; i++)
            {
                WindowSlot c = _slots[i];
                if (c.StickyOwner != null)
                    c.StickyOwner.Slot = c.StickyOwner.StickySlot = -1;
                c.StickyOwner = null;
                Kill(c);
            }
            _slots.Clear();
        }
    }

    // OWNER: slots (S2). One slot: its camera and what the pool tracks about it (last frame rendered, sticky owner, the
    // game camera settings it was set up from).
    public sealed partial class WindowSlot
    {
        public int Index;
        public Camera Camera;
        /// <summary>Time.frameCount of its last Render() (a slot renders at most once per frame).</summary>
        public int RenderedFrame = -100;
        /// <summary>The window holding it as its sticky slot, or null.</summary>
        public WindowState StickyOwner;
        /// <summary>Time.unscaledTime of its last use (LRU).</summary>
        public float UsedAt;
    }
}
