using System.Collections.Generic;
using UnityEngine;

namespace RealPortal
{
    // AreaContext (ARCHITECTURE-v2.md §3.2, §4.12; FARWORLD-V3.md §2): far areas whose far portals stand in the same zone or in
    // adjacent ones (Chebyshev 1, taken transitively: a row of portals along a road is one place). They are one place for
    // what is set up per far side: the environment snapshot, wind, sky occlusion and the reflection probe, and from V3 the
    // FarDrawList pages and the far shadow cache. V2 forms the groups only (the hub test: four near portals into four far
    // portals standing together are four FarAreas in one AreaContext, over one set of registry sectors).
    // Made again only when the areas change (made, dropped, a far portal moved to another zone), never every frame.
    internal sealed class AreaContext
    {
        // The lowest Serial of its areas: stays while its first area does.
        internal int Id;
        // Its areas' far portals: their mean position and that point's zone, and the zones they span.
        internal Vector3 Center;
        internal Vector2s Zone;
        internal int MinX, MaxX, MinY, MaxY;
        internal readonly List<FarArea> Areas = new List<FarArea>(4);

        static readonly List<AreaContext> s_all = new List<AreaContext>();
        static readonly List<AreaContext> s_pool = new List<AreaContext>();
        static int[] s_parent = new int[64];
        static int s_version;

        internal static IReadOnlyList<AreaContext> All => s_all;
        internal static int Count => s_all.Count;
        // Moves at every regrouping.
        internal static int Version => s_version;
        // Lab: regroupings so far, and the most areas one context held.
        internal static int LabRebuilds, LabLargest;

        // The context of a link's far area (null with Far.V2 off, or before AreaPolicy has seen the link).
        internal static AreaContext Of(PortalLink link) => FarAreas.Of(link)?.Context;

        // The groups made again from the areas (AreaPolicy, when FarAreas.ContextsDirty).
        internal static void Rebuild(List<FarArea> areas)
        {
            Release();
            int n = areas.Count;
            if (s_parent.Length < n)
                s_parent = new int[Mathf.NextPowerOfTwo(n)];
            for (int i = 0; i < n; i++)
                s_parent[i] = i;
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    Vector2s a = areas[i].Zone, b = areas[j].Zone;
                    if (Mathf.Abs(a.x - b.x) <= 1 && Mathf.Abs(a.y - b.y) <= 1)
                        Union(i, j);
                }
            for (int i = 0; i < n; i++)
            {
                int root = Find(i);
                AreaContext c = areas[root].Context;
                // The root's context is made when its first member is met (the root itself, or one before it).
                if (c == null || c.Id == 0)
                {
                    c = Take();
                    areas[root].Context = c;
                }
                c.Add(areas[i]);
                areas[i].Context = c;
            }
            foreach (AreaContext c in s_all)
            {
                c.Center /= c.Areas.Count;
                c.Zone = ZoneSystem.GetZone(c.Center);
                if (c.Areas.Count > LabLargest)
                    LabLargest = c.Areas.Count;
            }
            s_version++;
            LabRebuilds++;
        }

        // Nothing grouped (FarAreas.Clear).
        internal static void Clear()
        {
            Release();
            s_version++;
        }

        static void Release()
        {
            foreach (AreaContext c in s_all)
            {
                foreach (FarArea a in c.Areas)
                    if (a.Context == c)
                        a.Context = null;
                c.Areas.Clear();
                c.Id = 0;
                s_pool.Add(c);
            }
            s_all.Clear();
        }

        static AreaContext Take()
        {
            AreaContext c;
            if (s_pool.Count > 0)
            {
                c = s_pool[s_pool.Count - 1];
                s_pool.RemoveAt(s_pool.Count - 1);
            }
            else
                c = new AreaContext();
            c.Id = int.MaxValue;
            c.Center = Vector3.zero;
            c.MinX = c.MinY = int.MaxValue;
            c.MaxX = c.MaxY = int.MinValue;
            s_all.Add(c);
            return c;
        }

        void Add(FarArea a)
        {
            if (Areas.Contains(a))
                return;
            Areas.Add(a);
            Id = Mathf.Min(Id, a.Serial);
            Center += a.Center;
            MinX = Mathf.Min(MinX, a.Zone.x);
            MaxX = Mathf.Max(MaxX, a.Zone.x);
            MinY = Mathf.Min(MinY, a.Zone.y);
            MaxY = Mathf.Max(MaxY, a.Zone.y);
        }

        static int Find(int i)
        {
            while (s_parent[i] != i)
            {
                s_parent[i] = s_parent[s_parent[i]];
                i = s_parent[i];
            }
            return i;
        }

        static void Union(int i, int j)
        {
            int a = Find(i), b = Find(j);
            if (a != b)
                s_parent[Mathf.Max(a, b)] = Mathf.Min(a, b);
        }

        internal string Describe() =>
            $"context {Id}: {Areas.Count} area(s) about {Center:F0}, zones {MinX},{MinY}..{MaxX},{MaxY}";
    }
}
