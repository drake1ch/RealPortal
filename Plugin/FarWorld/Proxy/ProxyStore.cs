using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace RealPortal
{
    // ProxyStore (ARCHITECTURE-v2.md §3.6): every proxy of the registry's sectors, outside the managed heap. Instances
    // live in NativeArray pages (Allocator.Persistent) of PageSize, one span per registered sector (by its SectorRec slot),
    // each span a contiguous block of a power-of-two capacity (16 .. PageSize; a sector holding more gets a page of its
    // own). A span that outgrows its block moves to a bigger one; freed blocks are reused by their size. Beside each
    // instance its SectorRegistry row (managed, per page), and per registry row the instance's handle (page << 20 | index),
    // so a journal event about a row finds its instance in O(1); a swap-remove tells the moved row its new place.
    // Main thread only; no managed allocation once the tables have grown. Disposed with its world (FarProxies.Stop).
    internal sealed class ProxyStore : IDisposable
    {
        internal const int PageSize = 4096;
        const int MinBlock = 16;
        const int IndexBits = 20;
        const int IndexMask = (1 << IndexBits) - 1;
        // Block classes 16 << c: up to 64k instances in one sector.
        const int Classes = 13;

        sealed class Page
        {
            internal NativeArray<ProxyInst> Data;
            internal int[] Rows;
            internal int Block;
            internal int Carved;
        }

        internal struct Span
        {
            internal int Page, Start, Capacity, Count;
            internal Bounds Bounds;
            internal bool HasBounds;
            // Its instances mirror the registry's rows of the sector (a build ran since it was last cleared).
            internal bool Built;
            // Waiting for a build (FarProxies' queue).
            internal bool Queued;
            // Instances inside a far active area (the boundary pass skips spans without any that lie far from them).
            internal int InActive;
            internal int Version;
            // Builds since its block was taken (a sector built again: what it decodes real may have been a proxy).
            internal int Builds;
        }

        readonly List<Page> _pages = new List<Page>();
        readonly List<long>[] _free = new List<long>[Classes];
        readonly int[] _carving = new int[Classes];
        Span[] _spans = new Span[64];
        int[] _rowInst = new int[256];
        bool _disposed;

        // Instances held now; moves whenever an instance is added, changed or removed (whoever caches a cull of a span).
        internal int Count;
        internal int Version;

        internal ProxyStore()
        {
            for (int c = 0; c < Classes; c++)
            {
                _free[c] = new List<long>();
                _carving[c] = -1;
            }
            // No block yet for any span (Page 0 is a real page: a span left at the default would clear, grow from and free
            // a block it never had, and two spans would share one).
            for (int i = 0; i < _spans.Length; i++)
                _spans[i].Page = -1;
            for (int i = 0; i < _rowInst.Length; i++)
                _rowInst[i] = -1;
        }

        internal int SpanCapacity => _spans.Length;

        internal ref Span SpanAt(int slot)
        {
            EnsureSpan(slot);
            return ref _spans[slot];
        }

        internal bool HasSpan(int slot) => (uint)slot < (uint)_spans.Length;

        // The span holds a block of a page that exists (none before its first instance, none once the store is disposed).
        bool HasBlock(in Span s) => s.Page >= 0 && s.Page < _pages.Count;

        // The instance handle of a registry row (-1 none).
        internal int HandleOf(int row) => (uint)row < (uint)_rowInst.Length ? _rowInst[row] : -1;

        internal ProxyInst Get(int handle) => _pages[handle >> IndexBits].Data[handle & IndexMask];

        internal void Set(int handle, in ProxyInst inst, int slot)
        {
            Page p = _pages[handle >> IndexBits];
            int i = handle & IndexMask;
            CountInActive(ref _spans[slot], p.Data[i].Flags, inst.Flags);
            p.Data[i] = inst;
            Encapsulate(ref _spans[slot], inst);
            _spans[slot].Version++;
            Version++;
        }

        // Flags only (no bounds change).
        internal void SetFlags(int handle, uint flags, int slot)
        {
            Page p = _pages[handle >> IndexBits];
            int i = handle & IndexMask;
            ProxyInst inst = p.Data[i];
            if (inst.Flags == flags)
                return;
            CountInActive(ref _spans[slot], inst.Flags, flags);
            inst.Flags = flags;
            p.Data[i] = inst;
            _spans[slot].Version++;
            Version++;
        }

        static void CountInActive(ref Span s, uint was, uint now)
        {
            bool a = (was & (uint)ProxyFlags.InActive) != 0, b = (now & (uint)ProxyFlags.InActive) != 0;
            if (a != b)
                s.InActive += b ? 1 : -1;
        }

        // The span's instances as a slice of their page (for the drawing part's cull).
        internal bool SpanData(int slot, out NativeArray<ProxyInst> page, out int start, out int count, out Bounds bounds)
        {
            page = default;
            start = count = 0;
            bounds = default;
            if ((uint)slot >= (uint)_spans.Length)
                return false;
            ref Span s = ref _spans[slot];
            if (_disposed || !s.Built || s.Count == 0 || !HasBlock(s))
                return false;
            page = _pages[s.Page].Data;
            start = s.Start;
            count = s.Count;
            bounds = s.Bounds;
            return true;
        }

        // The registry row of the index-th instance of a span.
        internal int RowAt(int slot, int index)
        {
            ref Span s = ref _spans[slot];
            return _pages[s.Page].Rows[s.Start + index];
        }

        internal int HandleAt(int slot, int index)
        {
            ref Span s = ref _spans[slot];
            return (s.Page << IndexBits) | (s.Start + index);
        }

        // Appends an instance for its row (inst.Row) to the span; returns its handle.
        internal int Add(int slot, in ProxyInst inst)
        {
            EnsureSpan(slot);
            EnsureRow(inst.Row);
            ref Span s = ref _spans[slot];
            if (!HasBlock(s) || s.Count == s.Capacity)
                Grow(ref s, s.Count + 1);
            Page p = _pages[s.Page];
            int i = s.Start + s.Count++;
            p.Data[i] = inst;
            p.Rows[i] = inst.Row;
            int handle = (s.Page << IndexBits) | i;
            _rowInst[inst.Row] = handle;
            if ((inst.Flags & (uint)ProxyFlags.InActive) != 0)
                s.InActive++;
            Encapsulate(ref s, inst);
            s.Version++;
            Count++;
            Version++;
            return handle;
        }

        // The row's instance out of the span (swap-remove: the last one moves into its place and its row is told).
        internal void Remove(int slot, int row)
        {
            int handle = HandleOf(row);
            if (handle < 0 || (uint)slot >= (uint)_spans.Length)
                return;
            ref Span s = ref _spans[slot];
            Page p = _pages[handle >> IndexBits];
            int i = handle & IndexMask;
            if ((p.Data[i].Flags & (uint)ProxyFlags.InActive) != 0)
                s.InActive--;
            int last = s.Start + --s.Count;
            if (i != last)
            {
                ProxyInst moved = p.Data[last];
                p.Data[i] = moved;
                p.Rows[i] = moved.Row;
                _rowInst[moved.Row] = handle;
            }
            p.Rows[last] = -1;
            _rowInst[row] = -1;
            s.Version++;
            Count--;
            Version++;
        }

        // Every instance of the span out (its block kept for the next build).
        internal void Clear(int slot)
        {
            if ((uint)slot >= (uint)_spans.Length)
                return;
            ref Span s = ref _spans[slot];
            if (HasBlock(s))
            {
                Page p = _pages[s.Page];
                for (int k = 0; k < s.Count; k++)
                {
                    int row = p.Rows[s.Start + k];
                    if ((uint)row < (uint)_rowInst.Length)
                        _rowInst[row] = -1;
                    p.Rows[s.Start + k] = -1;
                }
            }
            Count -= s.Count;
            s.Count = 0;
            s.HasBounds = false;
            s.Bounds = default;
            s.InActive = 0;
            s.Built = false;
            s.Version++;
            Version++;
        }

        // The span goes (its sector left the registry): instances out, block freed.
        internal void Free(int slot)
        {
            if ((uint)slot >= (uint)_spans.Length)
                return;
            Clear(slot);
            ref Span s = ref _spans[slot];
            if (HasBlock(s))
                FreeBlock(s.Page, s.Start, s.Capacity);
            s.Page = -1;
            s.Start = s.Capacity = 0;
            s.Queued = false;
            s.Builds = 0;
        }

        // Tight bounds again (after removals).
        internal void RecomputeBounds(int slot)
        {
            ref Span s = ref _spans[slot];
            s.HasBounds = false;
            if (!HasBlock(s))
                return;
            Page p = _pages[s.Page];
            for (int k = 0; k < s.Count; k++)
                Encapsulate(ref s, p.Data[s.Start + k]);
        }

        static void Encapsulate(ref Span s, in ProxyInst inst)
        {
            var b = new Bounds(inst.Center, new Vector3(inst.Radius, inst.Radius, inst.Radius) * 2f);
            if (s.HasBounds)
                s.Bounds.Encapsulate(b);
            else
            {
                s.Bounds = b;
                s.HasBounds = true;
            }
        }

        void EnsureSpan(int slot)
        {
            if (slot < _spans.Length)
                return;
            int old = _spans.Length;
            int size = old;
            while (size <= slot)
                size *= 2;
            Array.Resize(ref _spans, size);
            for (int i = old; i < size; i++)
                _spans[i].Page = -1;
        }

        void EnsureRow(int row)
        {
            if (row < _rowInst.Length)
                return;
            int old = _rowInst.Length;
            int size = old;
            while (size <= row)
                size *= 2;
            Array.Resize(ref _rowInst, size);
            for (int i = old; i < size; i++)
                _rowInst[i] = -1;
        }

        // Moves the span into a block of at least need instances.
        void Grow(ref Span s, int need)
        {
            int c = 0;
            while ((MinBlock << c) < need && c < Classes - 1)
                c++;
            int cap = MinBlock << c;
            if (cap < need)
                throw new InvalidOperationException($"a sector with {need} proxies");
            bool had = HasBlock(s);
            Alloc(c, out int page, out int start);
            if (had && s.Count > 0)
            {
                Page from = _pages[s.Page], to = _pages[page];
                NativeArray<ProxyInst>.Copy(from.Data, s.Start, to.Data, start, s.Count);
                Array.Copy(from.Rows, s.Start, to.Rows, start, s.Count);
                for (int k = 0; k < s.Count; k++)
                {
                    int row = to.Rows[start + k];
                    from.Rows[s.Start + k] = -1;
                    if ((uint)row < (uint)_rowInst.Length)
                        _rowInst[row] = (page << IndexBits) | (start + k);
                }
            }
            if (had)
                FreeBlock(s.Page, s.Start, s.Capacity);
            s.Page = page;
            s.Start = start;
            s.Capacity = cap;
        }

        void Alloc(int c, out int page, out int start)
        {
            List<long> free = _free[c];
            if (free.Count > 0)
            {
                long f = free[free.Count - 1];
                free.RemoveAt(free.Count - 1);
                page = (int)(f >> 32);
                start = (int)(f & 0xFFFFFFFF);
                return;
            }
            int block = MinBlock << c;
            int carving = _carving[c];
            if (carving >= 0)
            {
                Page p = _pages[carving];
                if ((p.Carved + 1) * block <= p.Data.Length)
                {
                    page = carving;
                    start = p.Carved++ * block;
                    return;
                }
            }
            int size = Math.Max(PageSize, block);
            if (_pages.Count >= (1 << (31 - IndexBits)))
                throw new InvalidOperationException("proxy store full");
            var made = new Page
            {
                Data = new NativeArray<ProxyInst>(size, Allocator.Persistent, NativeArrayOptions.ClearMemory),
                Rows = new int[size],
                Block = block,
                Carved = 1,
            };
            for (int i = 0; i < size; i++)
                made.Rows[i] = -1;
            _pages.Add(made);
            page = _pages.Count - 1;
            _carving[c] = page;
            start = 0;
        }

        void FreeBlock(int page, int start, int capacity)
        {
            int c = 0;
            while ((MinBlock << c) < capacity && c < Classes - 1)
                c++;
            _free[c].Add(((long)page << 32) | (uint)start);
        }

        // Outside the managed heap (the pages) and in it (rows per page, the row map, spans), bytes.
        internal long NativeBytes
        {
            get
            {
                long n = 0;
                foreach (Page p in _pages)
                    n += (long)p.Data.Length * 112;
                return n;
            }
        }

        internal long ManagedBytes
        {
            get
            {
                long n = (long)_rowInst.Length * 4 + (long)_spans.Length * 64;
                foreach (Page p in _pages)
                    n += (long)p.Rows.Length * 4;
                return n;
            }
        }

        internal int PageCount => _pages.Count;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (Page p in _pages)
                if (p.Data.IsCreated)
                    p.Data.Dispose();
            _pages.Clear();
            Count = 0;
            Version++;
        }
    }
}
