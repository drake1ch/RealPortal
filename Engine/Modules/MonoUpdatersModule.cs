using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace RealPortal.Engine
{
    // Every IMonoUpdater type keeps a static List<IMonoUpdater> Instances that MonoUpdaters walks each frame;
    // OnEnable adds the component, OnDisable removes it with List.Remove. Remove finds the element through
    // EqualityComparer<IMonoUpdater>.Default: a virtual UnityEngine.Object.Equals per element (a cast, a native
    // "alive" check, an instance-id compare), ~10-20 ns each, until it reaches the one leaving. Far areas make the big
    // lists bigger (1,272 heightmaps, up to 1,645 grass renderers), and they load, drain, sleep and wake in bursts of
    // hundreds of OnDisable calls.
    //
    // This finds the element by reference (~1 ns per element) and removes it with the same RemoveAt. For these lists
    // that is the same element: the item is always the component itself ('this'), the game's types do not override
    // Equals, and Unity's Equals of two non-null references (destroyed or not) compares instance ids, which are one
    // per object; nulls are skipped by both. So the list
    // holds exactly what the game's would, in the same order, at every moment, and nothing that reads these lists
    // directly needs to know: MonoUpdaters (through MonoUpdatersExtra), Heightmap.ApplySettingsOnAll, Path of
    // Valheiman (Fish.Instances, ZSFX.Instances) and other engine modules (HeightmapIdle reads Heightmap.Instances).
    // There is no state, so switching is instant.
    //
    // Not done: O(1) removal by tombstones and compaction once per frame. The order-preserving compaction plus an
    // item -> index map costs O(n) per changed list per frame anyway, and the tombstones (nulls or leftovers) would be
    // visible to every unpatched reader above until the next MonoUpdaters pass; the ApplySettingsOnAll and
    // HeightmapIdle loops would break on them.
    //
    // Sites (all of the game assembly): Remove in OnDisable of Aoe, BaseAI (List<IUpdateAI>), Character (Player's
    // override calls base.OnDisable), CharacterAnimEvent, CraftingStation, EffectArea, Fish, Floating,
    // FootStep, Heightmap, InstanceRenderer, LightFlicker, LineAttach, MeleeWeaponTrail, RandomFlyingBird, Ship,
    // ShipEffects, Smoke, SmokeSpawner, Tail, VisEquipment, WaterTrigger, ZSFX, ZSyncAnimation, ZSyncTransform, and in
    // LightFlicker.ApplySettings (remove, then add back at the end). Adds (OnEnable, ApplySettings) append and stay as
    // they are. The call is swapped by a transpiler (Priority.Last) that keeps the rest of each method.
    [EngineModule]
    internal static class MonoUpdatersModule
    {
        static readonly Feature s_feature = Features.Register("UpdaterFastRemove", true,
            "A component leaving the MonoUpdaters lists is found by reference instead of by Unity's Object.Equals on every element before it.");

        static readonly Type[] s_types =
        {
            typeof(Aoe), typeof(BaseAI), typeof(Character), typeof(CharacterAnimEvent), typeof(CraftingStation), typeof(EffectArea),
            typeof(Fish), typeof(Floating), typeof(FootStep), typeof(Heightmap), typeof(InstanceRenderer), typeof(LightFlicker),
            typeof(LineAttach), typeof(MeleeWeaponTrail), typeof(RandomFlyingBird), typeof(Ship), typeof(ShipEffects), typeof(Smoke),
            typeof(SmokeSpawner), typeof(Tail), typeof(VisEquipment), typeof(WaterTrigger), typeof(ZSFX), typeof(ZSyncAnimation),
            typeof(ZSyncTransform),
        };

        static readonly MethodInfo s_remove = AccessTools.DeclaredMethod(typeof(MonoUpdatersModule), nameof(Remove));

        static void Install(Harmony harmony)
        {
            if (s_feature == null)
                return;
            var transpiler = new HarmonyMethod(typeof(MonoUpdatersModule), nameof(RemoveCalls)) { priority = Priority.Last };
            var targets = new List<MethodInfo>();
            foreach (Type t in s_types)
                targets.Add(AccessTools.DeclaredMethod(t, "OnDisable"));
            targets.Add(AccessTools.DeclaredMethod(typeof(LightFlicker), "ApplySettings"));
            foreach (MethodInfo m in targets)
            {
                if (m == null)
                {
                    EnginePlugin.Log.LogWarning("UpdaterFastRemove: a target method is missing (game update?), left as it is.");
                    continue;
                }
                try
                {
                    harmony.Patch(m, transpiler: transpiler);
                }
                catch (Exception e)
                {
                    EnginePlugin.Log.LogError($"UpdaterFastRemove: {m.DeclaringType?.Name}.{m.Name} not patched: {e}");
                }
            }
        }

        // List<IMonoUpdater>.Remove / List<IUpdateAI>.Remove -> Remove<T>(list, item): the same stack, the same result.
        static IEnumerable<CodeInstruction> RemoveCalls(IEnumerable<CodeInstruction> code, MethodBase original)
        {
            int n = 0;
            foreach (CodeInstruction ci in code)
            {
                if ((ci.opcode == OpCodes.Callvirt || ci.opcode == OpCodes.Call) && ci.operand is MethodInfo m && m.Name == "Remove"
                    && m.DeclaringType != null && m.DeclaringType.IsGenericType && m.DeclaringType.GetGenericTypeDefinition() == typeof(List<>))
                {
                    Type item = m.DeclaringType.GetGenericArguments()[0];
                    if (item == typeof(IMonoUpdater) || item == typeof(IUpdateAI))
                    {
                        ci.opcode = OpCodes.Call;
                        ci.operand = s_remove.MakeGenericMethod(item);
                        n++;
                    }
                }
                yield return ci;
            }
            if (n != 1)
                EnginePlugin.Log.LogWarning($"UpdaterFastRemove: {original?.DeclaringType?.Name}.{original?.Name}: {n} list removals (expected 1; changed by another mod?).");
        }

        static bool Remove<T>(List<T> list, T item) where T : class
        {
            if (!s_feature.On || !(item is UnityEngine.Object))
                return list.Remove(item);
            int count = list.Count;
            AccessTools.FieldRef<List<T>, T[]> items = Store<T>.Items;
            if (items != null)
            {
                T[] array = items(list);
                for (int i = 0; i < count; i++)
                    if (ReferenceEquals(array[i], item))
                    {
                        list.RemoveAt(i);
                        return true;
                    }
                return false;
            }
            for (int i = 0; i < count; i++)
                if (ReferenceEquals(list[i], item))
                {
                    list.RemoveAt(i);
                    return true;
                }
            return false;
        }

        // The list's own array (Mono's List<T>._items), read without the indexer's call and range check per element.
        static class Store<T>
        {
            internal static readonly AccessTools.FieldRef<List<T>, T[]> Items = Find();

            static AccessTools.FieldRef<List<T>, T[]> Find()
            {
                try
                {
                    FieldInfo f = typeof(List<T>).GetField("_items", BindingFlags.Instance | BindingFlags.NonPublic);
                    return f != null && f.FieldType == typeof(T[]) ? AccessTools.FieldRefAccess<List<T>, T[]>(f) : null;
                }
                catch
                {
                    return null;
                }
            }
        }
    }
}
