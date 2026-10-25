using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace RealPortal.Engine
{
    // Two multicast events get one subscriber per loaded object, and every += / -= on a multicast delegate builds a
    // new invocation array (Mono's Delegate.Combine copies all n entries; Delegate.Remove scans from the end and copies
    // n - 1). Loading or unloading n subscribers therefore costs O(n^2) copies and about 4n^2 bytes of garbage:
    //  - FanoutWearCache: every building piece does
    //      Start:     m_connectedHeightMap.m_clearConnectedWearNTearCache += ClearCachedSupport   (WearNTear.cs:341)
    //      OnDestroy: m_connectedHeightMap.m_clearConnectedWearNTearCache -= ClearCachedSupport   (WearNTear.cs:370)
    //    and Heightmap.Regenerate (full) invokes it (Heightmap.cs:362). A zone with 3k pieces costs ~36 MB of garbage
    //    to load and the same again to unload (pieces are removed oldest first, so each -= scans the whole list).
    //  - FanoutSettings: every enabled Heightmap and LightFlicker does += in OnEnable and -= in OnDisable on the static
    //    GraphicsSettingsManager.GraphicsSettingsChanged (Heightmap.cs:249,263; LightFlicker.cs:116,123), so every
    //    zone load/unload, every far-area sleep/wake and every light-bearing effect copies the whole list (S = all
    //    enabled heightmaps + flickers, ~1300-2500 with far areas).
    //
    // With the feature on, those four call sites (transpiled, nothing else of the methods changes) put the subscription
    // into a registry instead: an append-only array in subscription order (each entry gets a stamp), with O(1) removal
    // (the entry is found through a target -> entry map and becomes a tombstone; the array is compacted, order kept,
    // once tombstones outnumber live entries). The real event holds one proxy delegate per registry, added at the end
    // of the event's list when the registry gets its first entry; invoking the event invokes the proxy, which calls
    // the registry's delegates in stamp order. Exactly what the multicast would have done:
    //  - the same delegate objects (the game's own newobj Action, ClearCachedSupport / UpdateShadowSettings /
    //    ApplySettings) are called, in subscription order;
    //  - with the multicast's snapshot semantics: the set called is the one registered when the proxy starts (copied
    //    into a pooled buffer), so a delegate removed during the invocation is still called and one added is not;
    //  - an exception from a subscriber stops the rest and propagates, as from Delegate.Invoke;
    //  - -= removes the newest subscription equal to the given delegate (Delegate.Equals: same target and method),
    //    which is Delegate.Remove's "last occurrence"; a delegate that is not in the registry (subscribed while the
    //    feature was off, or by something else) is removed from the real event as before.
    // What is not identical, and why it cannot be observed:
    //  - Interleaving with *other* subscribers of the same event. The proxy sits where the first registry entry would
    //    have been; subscribers that the game or mods add later are called after all registry entries. For the
    //    per-heightmap cache event there are no other subscribers (game, Path of Valheiman, Real Portal checked). For
    //    GraphicsSettingsChanged the others are CameraEffects, ClutterSystem, ClothQualityManager, ZNet (simulation
    //    distance -> Heightmap.ApplySettings: the _LodHideDistance property block) and Real Portal's PortalSystem (sets
    //    a flag). None of them reads what the registry's subscribers write (a heightmap renderer's
    //    shadowCastingMode/receiveShadows; a flicker's light intensity and its LightFlicker.Instances entry), and none
    //    enables or disables a heightmap or light synchronously, so the order between the two groups changes nothing.
    //  - A mod reading the event's invocation list (GetInvocationList, reflection on the backing field) sees the
    //    proxy instead of the individual subscribers. None of the installed mods does.
    //
    // Switching: on -> off puts every registry entry back into its real event at the proxy's place, in order, and drops
    // the proxy, so from then on the game runs exactly its own code on exactly the list it would have had (the list is
    // built as a balanced Combine, O(n log n), not the O(n^2) fold of Delegate.Combine(params); see Merge). off -> on
    // leaves existing real subscriptions where they are (their -= keeps working through the real event); new ones go
    // to a fresh proxy that is appended after them, so the order is still the subscription order.
    // A heightmap's registry is dropped in Heightmap.OnDestroy (its pieces then skip -= because their heightmap
    // compares equal to null, exactly as in the game). That postfix is installed before any call site is routed, and
    // if it cannot be the module routes nothing; the WearNTear sites are routed only when the event's backing field
    // resolves, since without it a registry could not be put back when switched off.
    //
    // Compatibility: a transpiler (Priority.Last) swaps only the one add_/remove_ call. If another mod transpiles one of
    // these methods, or the call is not found exactly once, that site is left as it is, and a registry is only used
    // while both its add and its remove sites are ours (if a remove site stops being ours, the registry is put back into
    // the real event at once). FanoutVerify (lab, off) keeps, next to every registry, the multicast the game would
    // have built, and on every invocation and removal compares it with the registry (order, target and method).
    [EngineModule]
    public static class DelegateFanoutModule
    {
        static readonly Feature s_cache = Features.Register("FanoutWearCache", true,
            "Building pieces subscribe to their heightmap's support-cache event through an ordered registry instead of a multicast delegate (no O(n^2) copies when a base loads or unloads).");
        static readonly Feature s_settings = Features.Register("FanoutSettings", true,
            "Heightmaps and LightFlickers subscribe to GraphicsSettingsChanged through an ordered registry instead of the multicast delegate (no list copy per enable/disable).");
        static readonly Feature s_verify = Features.Register("FanoutVerify", false,
            "Lab: keeps the multicast delegate the game would have built next to every Fanout* registry and compares them on every call and removal. Slow (O(n^2) again).");

        // Lab counters.
        public static long CacheAdds, CacheRemoves, SettingsAdds, SettingsRemoves;
        // Subscriptions that went to (or came from) the real event while a feature was on: not ours to route.
        public static long PassedThrough;
        public static long Invocations, Calls;
        public static long VerifyChecks, VerifyMismatches;

        const string LabOwners = "valheim.realportal.lab";

        // ---- registry ---------------------------------------------------------------------------------------------

        sealed class Entry
        {
            internal Action Action;
            internal object Target;
            internal Fanout Owner;
            internal int Index;
            internal long Stamp;
            internal Entry Older;   // an older live subscription of the same target (any registry), or null
        }

        sealed class Fanout
        {
            internal readonly string Name;
            internal readonly Action Proxy;
            internal bool ProxySubscribed;
            Entry[] _items = new Entry[8];
            int _count;             // slots in use, tombstones included
            int _tombs;
            internal int Live;
            // FanoutVerify: the delegate the game's own += / -= would have produced for these subscriptions.
            internal Delegate Shadow;
            internal bool ShadowValid;

            internal Fanout(string name)
            {
                Name = name;
                Proxy = Invoke;
            }

            internal void Append(Entry e)
            {
                if (_count == _items.Length)
                {
                    if (_tombs > 0)
                        Compact();
                    if (_count == _items.Length)
                        Array.Resize(ref _items, _items.Length * 2);
                }
                e.Owner = this;
                e.Index = _count;
                e.Stamp = ++s_stamp;
                _items[_count++] = e;
                Live++;
            }

            internal void Detach(Entry e)
            {
                _items[e.Index] = null;
                e.Owner = null;
                Live--;
                _tombs++;
                if (Live == 0)
                {
                    Array.Clear(_items, 0, _count);
                    _count = 0;
                    _tombs = 0;
                }
                else if (_tombs > 32 && _tombs * 2 > _count)
                {
                    Compact();
                }
            }

            // Order-preserving: stamps stay increasing along the array.
            void Compact()
            {
                int n = 0;
                for (int i = 0; i < _count; i++)
                {
                    Entry e = _items[i];
                    if (e == null)
                        continue;
                    e.Index = n;
                    _items[n++] = e;
                }
                Array.Clear(_items, n, _count - n);
                _count = n;
                _tombs = 0;
            }

            internal void CopyLive(List<Entry> into)
            {
                for (int i = 0; i < _count; i++)
                    if (_items[i] != null)
                        into.Add(_items[i]);
            }

            // Drops every entry (registry and target map) without touching the real event.
            internal void Clear()
            {
                for (int i = 0; i < _count; i++)
                {
                    Entry e = _items[i];
                    if (e == null)
                        continue;
                    Unmap(e);
                    e.Owner = null;
                }
                Array.Clear(_items, 0, _count);
                _count = 0;
                _tombs = 0;
                Live = 0;
                Shadow = null;
                ShadowValid = false;
            }

            // The proxy: the registry's delegates in stamp order, over a snapshot (multicast semantics).
            void Invoke()
            {
                int live = Live;
                if (live == 0)
                    return;
                int depth = s_depth;
                Action[] buffer = Rent(depth, live);
                s_depth = depth + 1;
                int n = 0;
                try
                {
                    long last = 0;
                    bool ordered = true;
                    for (int i = 0; i < _count; i++)
                    {
                        Entry e = _items[i];
                        if (e == null)
                            continue;
                        if (e.Stamp <= last)
                            ordered = false;
                        last = e.Stamp;
                        buffer[n++] = e.Action;
                    }
                    Invocations++;
                    Calls += n;
                    if (s_verify.On)
                        VerifyInvoke(this, buffer, n, ordered);
                    for (int i = 0; i < n; i++)
                        buffer[i]();
                }
                finally
                {
                    Array.Clear(buffer, 0, n);
                    s_depth = depth;
                }
            }

            public override string ToString() => $"{Name}: {Live} live, {_tombs} tombstones";
        }

        static long s_stamp;
        static int s_depth;
        // One snapshot buffer per nesting depth (a subscriber may raise the event again).
        static Action[][] s_buffers = new Action[4][];

        static Action[] Rent(int depth, int size)
        {
            if (depth >= s_buffers.Length)
                Array.Resize(ref s_buffers, Math.Max(depth + 1, s_buffers.Length * 2));
            Action[] b = s_buffers[depth];
            if (b == null || b.Length < size)
            {
                int cap = 16;
                while (cap < size)
                    cap *= 2;
                b = s_buffers[depth] = new Action[cap];
            }
            return b;
        }

        sealed class ByReference : IEqualityComparer<object>
        {
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object o) => RuntimeHelpers.GetHashCode(o);
        }

        static readonly ByReference s_byRef = new ByReference();
        // The newest live registry subscription of each target (a WearNTear, Heightmap or LightFlicker); older ones of
        // the same target hang off Entry.Older.
        static readonly Dictionary<object, Entry> s_byTarget = new Dictionary<object, Entry>(s_byRef);
        static readonly Dictionary<object, Fanout> s_cacheFanouts = new Dictionary<object, Fanout>(s_byRef);
        static readonly Fanout s_settingsFanout = new Fanout("GraphicsSettingsChanged");
        static readonly List<Entry> s_tempEntries = new List<Entry>();
        static readonly List<Delegate> s_tempDelegates = new List<Delegate>();

        static void Add(Fanout f, Action action)
        {
            object target = action.Target;
            var e = new Entry { Action = action, Target = target };
            f.Append(e);
            if (s_byTarget.TryGetValue(target, out Entry newest))
                e.Older = newest;
            s_byTarget[target] = e;
            if (s_verify.On)
            {
                if (f.Live == 1)
                {
                    // Started empty while verifying: from here the shadow is complete.
                    f.Shadow = null;
                    f.ShadowValid = true;
                }
                if (f.ShadowValid)
                    f.Shadow = Delegate.Combine(f.Shadow, action);
            }
        }

        // Delegate.Remove semantics: the newest subscription of f equal to 'action'. False when f has none.
        static bool Remove(Fanout f, Action action)
        {
            object target = action.Target;
            if (target == null || f.Live == 0 || !s_byTarget.TryGetValue(target, out Entry e))
                return false;
            for (; e != null; e = e.Older)
            {
                if (e.Owner != f || !e.Action.Equals(action))
                    continue;
                Unmap(e);
                f.Detach(e);
                if (s_verify.On)
                    VerifyRemove(f, action);
                return true;
            }
            return false;
        }

        static void Unmap(Entry e)
        {
            if (!s_byTarget.TryGetValue(e.Target, out Entry newest))
                return;
            if (newest == e)
            {
                if (e.Older != null)
                    s_byTarget[e.Target] = e.Older;
                else
                    s_byTarget.Remove(e.Target);
            }
            else
            {
                for (Entry p = newest; p != null; p = p.Older)
                    if (p.Older == e)
                    {
                        p.Older = e.Older;
                        break;
                    }
            }
            e.Older = null;
        }

        // The real event's list with the proxy replaced by the registry's delegates, in order (appended when the proxy
        // is missing). Clears the registry.
        static Delegate Expand(Delegate current, Fanout f)
        {
            s_tempEntries.Clear();
            f.CopyLive(s_tempEntries);
            s_tempDelegates.Clear();
            bool placed = false;
            if (current != null)
            {
                foreach (Delegate d in current.GetInvocationList())
                {
                    if (ReferenceEquals(d, f.Proxy))
                    {
                        if (!placed)
                            foreach (Entry e in s_tempEntries)
                                s_tempDelegates.Add(e.Action);
                        placed = true;
                        continue;
                    }
                    s_tempDelegates.Add(d);
                }
            }
            if (!placed)
                foreach (Entry e in s_tempEntries)
                    s_tempDelegates.Add(e.Action);
            Delegate result = s_tempDelegates.Count == 0 ? null : Merge(s_tempDelegates, 0, s_tempDelegates.Count);
            s_tempEntries.Clear();
            s_tempDelegates.Clear();
            f.Clear();
            f.ProxySubscribed = false;
            return result;
        }

        // Delegate.Combine over list[lo, hi) as a balanced tree. The game's mscorlib (Mono) implements
        // Combine(params Delegate[]) as a left fold of Combine(acc, next), and MulticastDelegate.CombineImpl copies both
        // invocation arrays into a new one each time, so the fold is the O(n^2) copying this module removes (a 10k-piece
        // heightmap: ~400 MB in one frame). Combining halves gives the same invocation array (CombineImpl appends
        // 'follow's array after 'this's, so the leaves keep their order; the multicast's Method is its last leaf's, and
        // every combined delegate is allocated like its left operand, which in both shapes leads back to the first
        // leaf), but each level copies n entries once: O(n log n).
        internal static Delegate Merge(List<Delegate> list, int lo, int hi)
        {
            int n = hi - lo;
            if (n == 1)
                return list[lo];
            if (n == 2)
                return Delegate.Combine(list[lo], list[lo + 1]);
            int mid = lo + n / 2;
            return Delegate.Combine(Merge(list, lo, mid), Merge(list, mid, hi));
        }

        // ---- the game's call sites ----------------------------------------------------------------------------------

        static AccessTools.FieldRef<Heightmap, Action> s_cacheField;
        static FieldInfo s_settingsField;

        // Sites transpiled by us (true) or left as the game's (false); a registry is used only while both of its sites
        // are ours.
        static bool s_wntAdd, s_wntRemove, s_hmAdd, s_hmRemove, s_lfAdd, s_lfRemove;
        static bool CachePaired => s_wntAdd && s_wntRemove;

        // Heightmap::add_m_clearConnectedWearNTearCache(Action) in WearNTear.Start. Stack: heightmap, action.
        static void AddCache(Heightmap heightmap, Action action)
        {
            if (!s_cache.On || !CachePaired || ReferenceEquals(heightmap, null) || action == null || !(action.Target is WearNTear))
            {
                heightmap.m_clearConnectedWearNTearCache += action;   // a null heightmap throws here as in the game
                if (s_cache.On)
                    PassedThrough++;
                return;
            }
            if (!s_cacheFanouts.TryGetValue(heightmap, out Fanout f))
            {
                f = new Fanout("WearNTearCache");
                s_cacheFanouts.Add(heightmap, f);
            }
            if (!f.ProxySubscribed)
            {
                heightmap.m_clearConnectedWearNTearCache += f.Proxy;
                f.ProxySubscribed = true;
            }
            Add(f, action);
            CacheAdds++;
        }

        // Heightmap::remove_m_clearConnectedWearNTearCache(Action) in WearNTear.OnDestroy.
        static void RemoveCache(Heightmap heightmap, Action action)
        {
            if (!ReferenceEquals(heightmap, null) && action != null && s_cacheFanouts.Count != 0
                && s_cacheFanouts.TryGetValue(heightmap, out Fanout f) && Remove(f, action))
            {
                CacheRemoves++;
                return;
            }
            heightmap.m_clearConnectedWearNTearCache -= action;
        }

        // GraphicsSettingsManager::add_GraphicsSettingsChanged(Action) in Heightmap.OnEnable / LightFlicker.OnEnable.
        static void AddSettings(Action action)
        {
            object target = action?.Target;
            bool ours = target is Heightmap ? s_hmAdd && s_hmRemove : target is LightFlicker && s_lfAdd && s_lfRemove;
            if (!s_settings.On || !ours)
            {
                GraphicsSettingsManager.GraphicsSettingsChanged += action;
                if (s_settings.On)
                    PassedThrough++;
                return;
            }
            Fanout f = s_settingsFanout;
            if (!f.ProxySubscribed)
            {
                GraphicsSettingsManager.GraphicsSettingsChanged += f.Proxy;
                f.ProxySubscribed = true;
            }
            Add(f, action);
            SettingsAdds++;
        }

        // GraphicsSettingsManager::remove_GraphicsSettingsChanged(Action) in Heightmap.OnDisable / LightFlicker.OnDisable.
        static void RemoveSettings(Action action)
        {
            if (action != null && Remove(s_settingsFanout, action))
            {
                SettingsRemoves++;
                return;
            }
            GraphicsSettingsManager.GraphicsSettingsChanged -= action;
        }

        // Heightmap.OnDestroy postfix: its event dies with it, and so does its registry.
        static void HeightmapDestroyed(Heightmap __instance)
        {
            if (s_cacheFanouts.Count == 0 || !s_cacheFanouts.TryGetValue(__instance, out Fanout f))
                return;
            f.Clear();
            s_cacheFanouts.Remove(__instance);
        }

        // ---- switching ----------------------------------------------------------------------------------------------

        static readonly List<object> s_tempKeys = new List<object>();

        static void ExpandCache()
        {
            if (s_cacheFanouts.Count == 0)
                return;
            if (s_cacheField == null)
            {
                // Cannot rebuild a live heightmap's event in place: its proxy stays and keeps calling the registry (still
                // exact; the pieces' -= still finds them there). Clearing it would leave those pieces subscribed to
                // nothing. Only the registries of destroyed heightmaps go. (Install does not route the cache sites
                // without the field, so this is a guard.)
                EnginePlugin.Log?.LogWarning("FanoutWearCache: Heightmap.m_clearConnectedWearNTearCache's field not found; the registries stay in use.");
                s_tempKeys.Clear();
                foreach (KeyValuePair<object, Fanout> kv in s_cacheFanouts)
                    if ((Heightmap)kv.Key == null)
                        s_tempKeys.Add(kv.Key);
                foreach (object key in s_tempKeys)
                {
                    s_cacheFanouts[key].Clear();
                    s_cacheFanouts.Remove(key);
                }
                s_tempKeys.Clear();
                return;
            }
            foreach (KeyValuePair<object, Fanout> kv in s_cacheFanouts)
            {
                var hm = (Heightmap)kv.Key;
                Fanout f = kv.Value;
                if (hm != null)
                {
                    ref Action field = ref s_cacheField(hm);
                    field = (Action)Expand(field, f);
                }
                else
                {
                    f.Clear();      // destroyed: its event is gone with it
                }
            }
            s_cacheFanouts.Clear();
        }

        static void ExpandSettings()
        {
            Fanout f = s_settingsFanout;
            if (!f.ProxySubscribed && f.Live == 0)
                return;
            if (s_settingsField == null)
            {
                // Cannot rebuild the event in place: the proxy stays and keeps calling the registry (still exact).
                EnginePlugin.Log.LogWarning("FanoutSettings: GraphicsSettingsChanged's field not found; the registry stays in use.");
                return;
            }
            var current = (Delegate)s_settingsField.GetValue(null);
            s_settingsField.SetValue(null, Expand(current, f));
        }

        // ---- install ------------------------------------------------------------------------------------------------

        static readonly MethodInfo s_addCacheGame = AccessTools.Method(typeof(Heightmap), "add_m_clearConnectedWearNTearCache");
        static readonly MethodInfo s_removeCacheGame = AccessTools.Method(typeof(Heightmap), "remove_m_clearConnectedWearNTearCache");
        static readonly MethodInfo s_addSettingsGame = AccessTools.Method(typeof(GraphicsSettingsManager), "add_GraphicsSettingsChanged");
        static readonly MethodInfo s_removeSettingsGame = AccessTools.Method(typeof(GraphicsSettingsManager), "remove_GraphicsSettingsChanged");

        static void Install(Harmony harmony)
        {
            s_cacheField = TryFieldRef();
            s_settingsField = AccessTools.Field(typeof(GraphicsSettingsManager), "GraphicsSettingsChanged");
            if (s_settingsField != null && s_settingsField.FieldType != typeof(Action))
                s_settingsField = null;
            if (s_addCacheGame == null || s_removeCacheGame == null || s_addSettingsGame == null || s_removeSettingsGame == null)
                throw new MissingMethodException("DelegateFanout: an event accessor of Heightmap or GraphicsSettingsManager is missing (game update?)");

            // The registry cleanup first, outside the per-site guards: without it every heightmap's registry (and its
            // pieces' entries) would outlive the heightmap, so if it cannot be installed nothing is routed at all.
            MethodInfo hmDestroy = AccessTools.DeclaredMethod(typeof(Heightmap), "OnDestroy");
            if (hmDestroy == null)
                throw new MissingMethodException("DelegateFanout: Heightmap.OnDestroy not found (game update?)");
            harmony.Patch(hmDestroy, postfix: new HarmonyMethod(typeof(DelegateFanoutModule), nameof(HeightmapDestroyed)));

            // Without the backing field the cache registries could not be put back into the event when switched off,
            // so the cache sites stay the game's.
            if (s_cacheField != null)
            {
                Transpile(harmony, typeof(WearNTear), "Start", nameof(WntStartSite));
                Transpile(harmony, typeof(WearNTear), "OnDestroy", nameof(WntDestroySite));
            }
            else
            {
                EnginePlugin.Log.LogWarning("FanoutWearCache: Heightmap.m_clearConnectedWearNTearCache's backing field not found (game update?), building pieces subscribe as in the game.");
            }
            Transpile(harmony, typeof(Heightmap), "OnEnable", nameof(HmEnableSite));
            Transpile(harmony, typeof(Heightmap), "OnDisable", nameof(HmDisableSite));
            Transpile(harmony, typeof(LightFlicker), "OnEnable", nameof(LfEnableSite));
            Transpile(harmony, typeof(LightFlicker), "OnDisable", nameof(LfDisableSite));

            s_cache.Switched += on =>
            {
                if (!on)
                    ExpandCache();
            };
            s_settings.Switched += on =>
            {
                if (!on)
                    ExpandSettings();
            };
            s_verify.Switched += on =>
            {
                // From now on every registry is shadowed starting from its current content (removal and order are
                // checked from here; earlier history is not).
                foreach (Fanout f in AllFanouts())
                {
                    f.Shadow = null;
                    f.ShadowValid = on;
                    if (!on)
                        continue;
                    s_tempEntries.Clear();
                    f.CopyLive(s_tempEntries);
                    foreach (Entry e in s_tempEntries)
                        f.Shadow = Delegate.Combine(f.Shadow, e.Action);
                    s_tempEntries.Clear();
                }
            };
        }

        static AccessTools.FieldRef<Heightmap, Action> TryFieldRef()
        {
            try
            {
                FieldInfo fi = AccessTools.Field(typeof(Heightmap), "m_clearConnectedWearNTearCache");
                return fi != null && fi.FieldType == typeof(Action) ? AccessTools.FieldRefAccess<Heightmap, Action>(fi) : null;
            }
            catch
            {
                return null;
            }
        }

        static IEnumerable<Fanout> AllFanouts()
        {
            yield return s_settingsFanout;
            foreach (Fanout f in s_cacheFanouts.Values)
                yield return f;
        }

        static void Transpile(Harmony harmony, Type type, string method, string transpiler)
        {
            MethodInfo original = AccessTools.DeclaredMethod(type, method);
            if (original == null)
            {
                EnginePlugin.Log.LogWarning($"DelegateFanout: {type.Name}.{method} not found, left as it is.");
                return;
            }
            try
            {
                harmony.Patch(original, transpiler: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(DelegateFanoutModule), transpiler)) { priority = Priority.Last });
            }
            catch (Exception e)
            {
                EnginePlugin.Log.LogError($"DelegateFanout: {type.Name}.{method} not patched: {e}");
            }
        }

        static IEnumerable<CodeInstruction> WntStartSite(IEnumerable<CodeInstruction> code, MethodBase original) =>
            Swap(code, original, s_addCacheGame, nameof(AddCache), ref s_wntAdd);

        static IEnumerable<CodeInstruction> WntDestroySite(IEnumerable<CodeInstruction> code, MethodBase original) =>
            Swap(code, original, s_removeCacheGame, nameof(RemoveCache), ref s_wntRemove);

        static IEnumerable<CodeInstruction> HmEnableSite(IEnumerable<CodeInstruction> code, MethodBase original) =>
            Swap(code, original, s_addSettingsGame, nameof(AddSettings), ref s_hmAdd);

        static IEnumerable<CodeInstruction> HmDisableSite(IEnumerable<CodeInstruction> code, MethodBase original) =>
            Swap(code, original, s_removeSettingsGame, nameof(RemoveSettings), ref s_hmRemove);

        static IEnumerable<CodeInstruction> LfEnableSite(IEnumerable<CodeInstruction> code, MethodBase original) =>
            Swap(code, original, s_addSettingsGame, nameof(AddSettings), ref s_lfAdd);

        static IEnumerable<CodeInstruction> LfDisableSite(IEnumerable<CodeInstruction> code, MethodBase original) =>
            Swap(code, original, s_removeSettingsGame, nameof(RemoveSettings), ref s_lfRemove);

        // Harmony reruns every transpiler of a method whenever anyone patches it, so each outcome is logged on change.
        static readonly Dictionary<string, string> s_reported = new Dictionary<string, string>();

        // Replaces the one call to 'game' with a call to ours (same stack in, same stack out). Leaves the method as it
        // is when another mod transpiles it too or when the call is not there exactly once.
        static List<CodeInstruction> Swap(IEnumerable<CodeInstruction> code, MethodBase original, MethodInfo game, string ours, ref bool site)
        {
            var list = new List<CodeInstruction>(code);
            string where = original?.DeclaringType?.Name + "." + original?.Name;
            bool wasOurs = site;
            string outcome;
            if (ForeignTranspiler(original))
            {
                site = false;
                outcome = "another mod transpiles it, left as it is";
            }
            else
            {
                var hits = new List<int>();
                for (int i = 0; i < list.Count; i++)
                    if ((list[i].opcode == OpCodes.Call || list[i].opcode == OpCodes.Callvirt) && Equals(list[i].operand, game))
                        hits.Add(i);
                if (hits.Count == 1)
                {
                    list[hits[0]].opcode = OpCodes.Call;
                    list[hits[0]].operand = AccessTools.DeclaredMethod(typeof(DelegateFanoutModule), ours);
                    site = true;
                    outcome = "routed through the registry";
                }
                else
                {
                    site = false;
                    outcome = $"{hits.Count} {game.Name} calls (expected 1; changed by another mod or an update?), left as it is";
                }
            }
            if (!s_reported.TryGetValue(where, out string last) || last != outcome)
            {
                s_reported[where] = outcome;
                if (site)
                    EnginePlugin.Log?.LogInfo($"DelegateFanout: {where}: {outcome}");
                else
                    EnginePlugin.Log?.LogWarning($"DelegateFanout: {where}: {outcome}");
            }
            // A remove site that stops being ours: whatever the registry holds goes back into the real event now, or
            // the game's -= would miss it.
            if (wasOurs && !site)
            {
                if (ReferenceEquals(game, s_removeCacheGame))
                    ExpandCache();
                else if (ReferenceEquals(game, s_removeSettingsGame))
                    ExpandSettings();
            }
            return list;
        }

        static bool ForeignTranspiler(MethodBase method)
        {
            if (method == null)
                return false;
            HarmonyLib.Patches info;
            try
            {
                info = Harmony.GetPatchInfo(method);
            }
            catch
            {
                return true;
            }
            if (info == null)
                return false;
            return Foreign(info.Transpilers) || Foreign(info.ILManipulators);
        }

        static bool Foreign(IEnumerable<HarmonyLib.Patch> patches)
        {
            if (patches == null)
                return false;
            foreach (HarmonyLib.Patch p in patches)
            {
                if (p.owner == EnginePlugin.Guid || p.owner != null && p.owner.StartsWith(LabOwners, StringComparison.Ordinal))
                    continue;
                return true;
            }
            return false;
        }

        // ---- FanoutVerify -------------------------------------------------------------------------------------------

        static void VerifyInvoke(Fanout f, Action[] snapshot, int n, bool ordered)
        {
            VerifyChecks++;
            if (!ordered)
                Mismatch(f, "stamps out of order");
            if (!f.ShadowValid)
                return;
            Delegate[] expected = f.Shadow?.GetInvocationList() ?? Array.Empty<Delegate>();
            if (expected.Length != n)
            {
                Mismatch(f, $"{n} subscribers called, the game's multicast has {expected.Length}");
                return;
            }
            for (int i = 0; i < n; i++)
                if (!expected[i].Equals(snapshot[i]))
                {
                    Mismatch(f, $"subscriber {i} differs ({snapshot[i].Target} vs {expected[i].Target})");
                    return;
                }
        }

        static void VerifyRemove(Fanout f, Action removed)
        {
            if (!f.ShadowValid)
                return;
            VerifyChecks++;
            Delegate before = f.Shadow;
            f.Shadow = Delegate.Remove(f.Shadow, removed);
            if (ReferenceEquals(before, f.Shadow))
                Mismatch(f, "removed from the registry but not found in the game's multicast");
            int shadowCount = f.Shadow?.GetInvocationList().Length ?? 0;
            if (shadowCount != f.Live)
                Mismatch(f, $"after a removal the registry has {f.Live}, the game's multicast {shadowCount}");
        }

        static void Mismatch(Fanout f, string what)
        {
            VerifyMismatches++;
            if (VerifyMismatches <= 20)
                EnginePlugin.Log?.LogWarning($"FanoutVerify: {f.Name}: {what}");
        }

        // ---- lab ----------------------------------------------------------------------------------------------------

        public static string Describe()
        {
            int cacheLive = 0;
            foreach (Fanout f in s_cacheFanouts.Values)
                cacheLive += f.Live;
            return $"cache: {s_cacheFanouts.Count} heightmaps, {cacheLive} live (adds {CacheAdds}, removes {CacheRemoves}, sites {s_wntAdd}/{s_wntRemove}); "
                   + $"settings: {s_settingsFanout.Live} live (adds {SettingsAdds}, removes {SettingsRemoves}, sites hm {s_hmAdd}/{s_hmRemove} lf {s_lfAdd}/{s_lfRemove}); "
                   + $"passed through {PassedThrough}; invocations {Invocations} ({Calls} calls); verify {VerifyChecks} checks, {VerifyMismatches} mismatches";
        }
    }
}
