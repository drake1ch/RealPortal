using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace RealPortal.Engine
{
    // Garbage the game makes over and over in hot code. With far areas loaded the managed heap is ~0.9 GB and the
    // incremental GC marks all of it each cycle, so every collection that this garbage brings forward is expensive (a
    // full one was measured at 53-88 ms on a 300-400 MB heap). These fixes only stop the allocations:
    //  - AllocZdoKeys: cooking stations ("slot" + i every frame per slot) and mine rocks ("Health" + i) take their ZDO
    //    keys from cached strings with the same text;
    //  - AllocOverlap: Physics.OverlapSphere in MonsterAI.FindClosestConsumableItem and ItemDrop.AutoStackItems reuses
    //    arrays;
    //  - AllocProjectile: each flying projectile's hit ray (RaycastAll every physics step) reuses arrays; sphere casts
    //    (SphereCastAll) only with AllocProjectileSphere, a lab switch until AllocVerify has confirmed them;
    //  - AllocSpawnSystem: the spawn timer key (salt + prefab name + index, hashed) is hashed once per spawner;
    //  - AllocHeightmap: a new zone's collision mesh takes its 24k indices from the game's list, not a 96 KB copy;
    //  - AllocTurret: ballistas compare their ammo against cached prefab names (Object.name allocates a new string on
    //    every read) instead of reading every ammo type's name every physics step.
    // Every patch is a transpiler that swaps one call (or one string expression) for ours and leaves the rest of the
    // method, so other mods' prefixes and postfixes keep working. Ours run last (Priority.Last), so other mods'
    // transpilers still see the game's own IL; if one of them already changed a spot, ours finds nothing there (or, for
    // a pooled query, more calls than the game's one) and leaves it as it is (logged). Our replacements compute exactly what the game computed and call the game's own
    // code while their feature is off. "AllocVerify" (lab, off by default) also runs the game's call next to each
    // replacement, uses the game's answer and logs any difference.
    [EngineModule]
    internal static class AllocFixesModule
    {
        static readonly Feature s_zdoKeys = Features.Register("AllocZdoKeys", true,
            "Cooking stations and mine rocks reuse their ZDO key strings (\"slot\" + i, \"Health\" + i) instead of building new ones every frame or check.");
        static readonly Feature s_overlap = Features.Register("AllocOverlap", true,
            "Physics.OverlapSphere in MonsterAI.FindClosestConsumableItem and ItemDrop.AutoStackItems reuses arrays.");
        static readonly Feature s_projectiles = Features.Register("AllocProjectile", true,
            "Projectiles' hit rays (RaycastAll every physics step) reuse arrays.");
        static readonly Feature s_projectileSpheres = Features.Register("AllocProjectileSphere", false,
            "Lab, off until AllocVerify confirms it: with AllocProjectile, projectiles with a ray radius (SphereCastAll) also reuse arrays. Unity's non-allocating sphere cast may normalize the direction once more, so hits could differ by float rounding.");
        static readonly Feature s_spawn = Features.Register("AllocSpawnSystem", true,
            "Spawn systems hash each spawner's timer key once instead of building the string every second.");
        static readonly Feature s_heightmap = Features.Register("AllocHeightmap", true,
            "A new zone's terrain collision mesh takes its indices from the game's list instead of a 96 KB array copy.");
        static readonly Feature s_turret = Features.Register("AllocTurret", true,
            "Ballistas compare ammo against cached prefab names instead of reading (allocating) every ammo type's name every physics step.");
        static readonly Feature s_verify = Features.Register("AllocVerify", false,
            "Lab: also runs the game's own call next to each Alloc* replacement, uses its answer and logs any difference. Slower.");

        // Lab, while AllocVerify is on: comparisons made; differences the game could act on (VerifyMismatches);
        // projectile hits on the same colliders in the same order whose values differ beyond float rounding
        // (VerifyDrift); and differences the game cannot notice, such as the native hit order before its distance
        // sort or rounding-level values (VerifyNoise). The acceptance test is VerifyMismatches == 0 and VerifyDrift == 0.
        internal static long VerifyChecks, VerifyMismatches, VerifyDrift, VerifyNoise;

        static readonly MethodInfo s_intToString = AccessTools.Method(typeof(int), nameof(int.ToString), Type.EmptyTypes);
        static readonly MethodInfo s_concat2 = AccessTools.Method(typeof(string), nameof(string.Concat), new[] { typeof(string), typeof(string) });
        static readonly MethodInfo s_concat3 = AccessTools.Method(typeof(string), nameof(string.Concat), new[] { typeof(string), typeof(string), typeof(string) });
        static readonly MethodInfo s_format1 = AccessTools.Method(typeof(string), nameof(string.Format), new[] { typeof(string), typeof(object) });
        static readonly MethodInfo s_stableHash = AccessTools.Method(typeof(StringExtensionMethods), nameof(StringExtensionMethods.GetStableHashCode), new[] { typeof(string) });
        static readonly MethodInfo s_getName = AccessTools.PropertyGetter(typeof(UnityEngine.Object), nameof(UnityEngine.Object.name));
        static readonly MethodInfo s_listToArray = AccessTools.Method(typeof(List<int>), nameof(List<int>.ToArray));
        static readonly MethodInfo s_setIndices = AccessTools.Method(typeof(Mesh), nameof(Mesh.SetIndices), new[] { typeof(int[]), typeof(MeshTopology), typeof(int) });
        static readonly MethodInfo s_overlap3 = AccessTools.Method(typeof(Physics), nameof(Physics.OverlapSphere), new[] { typeof(Vector3), typeof(float), typeof(int) });
        static readonly MethodInfo s_raycastAll = AccessTools.Method(typeof(Physics), nameof(Physics.RaycastAll), new[] { typeof(Vector3), typeof(Vector3), typeof(float), typeof(int) });
        static readonly MethodInfo s_sphereCastAll = AccessTools.Method(typeof(Physics), nameof(Physics.SphereCastAll), new[] { typeof(Vector3), typeof(float), typeof(Vector3), typeof(float), typeof(int) });
        static readonly FieldInfo s_spawnPrefab = AccessTools.Field(typeof(SpawnSystem.SpawnData), nameof(SpawnSystem.SpawnData.m_prefab));
        static readonly FieldInfo s_ammoField = AccessTools.Field(typeof(Turret.AmmoType), nameof(Turret.AmmoType.m_ammo));

        static MethodInfo Ours(string name) => AccessTools.DeclaredMethod(typeof(AllocFixesModule), name);

        static void Install(Harmony harmony)
        {
            Transpile(harmony, typeof(CookingStation), "GetSlot", nameof(CookingStationKeys));
            Transpile(harmony, typeof(CookingStation), "SetSlot", nameof(CookingStationKeys));
            Transpile(harmony, typeof(CookingStation), "IsEmpty", nameof(CookingStationKeys));
            Transpile(harmony, typeof(CookingStation), "GetFreeSlot", nameof(CookingStationKeys));
            Transpile(harmony, typeof(MineRock), "UpdateVisability", nameof(MineRockKeys));
            Transpile(harmony, typeof(MonsterAI), "FindClosestConsumableItem", nameof(ConsumableOverlap));
            Transpile(harmony, typeof(ItemDrop), "AutoStackItems", nameof(AutoStackOverlap));
            // CraftingStation.GetCraftingStation is left alone: nothing calls it (not the game, Real Portal or Path of
            // Valheiman), so rewriting it would only add surface.
            Transpile(harmony, typeof(Projectile), "FixedUpdate", nameof(ProjectileRays));
            Transpile(harmony, typeof(SpawnSystem), "UpdateSpawnList", nameof(SpawnTimerKeys));
            Transpile(harmony, typeof(SpawnSystem), "UpdateSpawning", nameof(SpawnSalts));
            Transpile(harmony, typeof(Heightmap), "RebuildCollisionMesh", nameof(CollisionIndices));
            Transpile(harmony, typeof(Turret), "UpdateVisualBolt", nameof(TurretAmmoNames));

            if (s_raycastInto == null || s_sphereCastInto == null)
                EnginePlugin.Log.LogWarning("AllocProjectile: Unity's PhysicsScene internals not found; projectile rays stay as in the game.");
            // The name caches assume a prefab keeps its name; switching on again starts them fresh.
            s_spawn.Switched += on => { if (on) s_spawnHashes.Clear(); };
            s_turret.Switched += on => { if (on) s_prefabNames.Clear(); };
        }

        static void Transpile(Harmony harmony, Type type, string method, string transpiler)
        {
            string where = type.Name + "." + method;
            try
            {
                MethodInfo original = AccessTools.DeclaredMethod(type, method);
                if (original == null)
                {
                    EnginePlugin.Log.LogWarning($"AllocFixes: {where} not found, left as it is.");
                    return;
                }
                harmony.Patch(original, transpiler: new HarmonyMethod(Ours(transpiler)) { priority = Priority.Last });
            }
            catch (Exception e)
            {
                EnginePlugin.Log.LogError($"AllocFixes: {where} not patched: {e}");
            }
        }

        // Harmony runs every transpiler of a method again whenever any mod patches that method, so each result is
        // logged when it changes, not on every rerun.
        static readonly Dictionary<string, int> s_reported = new Dictionary<string, int>();

        // 'expected' > 0: the edit was made only when exactly that many sites were found.
        static void Report(MethodBase original, string what, int n, int expected = 0)
        {
            string where = original != null ? original.DeclaringType?.Name + "." + original.Name : "?";
            string key = where + ": " + what;
            if (s_reported.TryGetValue(key, out int last) && last == n)
                return;
            s_reported[key] = n;
            if (n == 0)
                EnginePlugin.Log.LogWarning($"AllocFixes: {key}: not found (changed by another mod?), left as it is.");
            else if (expected > 0 && n != expected)
                EnginePlugin.Log.LogWarning($"AllocFixes: {key}: {n} calls where the game has {expected} (added by another mod or an update?); "
                                            + "each pooled array may serve one call site, so left as it is.");
            else
                EnginePlugin.Log.LogInfo($"AllocFixes: {key} x{n}");
        }

        static bool IsAddressLoad(CodeInstruction ci) =>
            ci.opcode == OpCodes.Ldarga_S || ci.opcode == OpCodes.Ldarga || ci.opcode == OpCodes.Ldloca_S || ci.opcode == OpCodes.Ldloca;

        static bool IsValueLoad(CodeInstruction ci)
        {
            OpCode op = ci.opcode;
            return op == OpCodes.Ldloc_0 || op == OpCodes.Ldloc_1 || op == OpCodes.Ldloc_2 || op == OpCodes.Ldloc_3
                   || op == OpCodes.Ldloc_S || op == OpCodes.Ldloc
                   || op == OpCodes.Ldarg_0 || op == OpCodes.Ldarg_1 || op == OpCodes.Ldarg_2 || op == OpCodes.Ldarg_3
                   || op == OpCodes.Ldarg_S || op == OpCodes.Ldarg;
        }

        // Harmony's CodeMatcher and CodeInstruction.labels mention System.Reflection.Emit types (ILGenerator, Label)
        // that the netstandard2.1 reference set cannot resolve, so the patterns are matched here on a plain list and
        // edited in place, the way CodeMatcher.Set would: an edited instruction keeps its labels and blocks. The
        // inner instructions of a pattern must carry no label (nothing may jump into the middle of what we change).
        static readonly FieldInfo s_labels = AccessTools.Field(typeof(CodeInstruction), "labels");

        static bool Unlabeled(CodeInstruction ci) => !(s_labels?.GetValue(ci) is System.Collections.ICollection labels) || labels.Count == 0;

        // Index of the first run of instructions from 'start' on that matches the pattern, or -1.
        static int Find(List<CodeInstruction> code, int start, params Func<CodeInstruction, bool>[] pattern)
        {
            for (int i = Math.Max(0, start); i + pattern.Length <= code.Count; i++)
            {
                int k = 0;
                while (k < pattern.Length && pattern[k](code[i + k]))
                    k++;
                if (k == pattern.Length)
                    return i;
            }
            return -1;
        }

        static void Set(CodeInstruction ci, OpCode opcode, object operand)
        {
            ci.opcode = opcode;
            ci.operand = operand;
        }

        static void Mismatch(string site, string detail)
        {
            VerifyMismatches++;
            if (VerifyMismatches <= 50)
                EnginePlugin.Log.LogWarning($"AllocVerify: {site} differs from the game's own call: {detail}");
        }

        static void Drift(string site, string detail)
        {
            VerifyDrift++;
            if (VerifyDrift <= 50)
                EnginePlugin.Log.LogWarning($"AllocVerify: {site} hits the same colliders in the same order, but values differ: {detail}");
        }

        static void Noise(string site, string detail)
        {
            VerifyNoise++;
            if (VerifyNoise <= 20)
                EnginePlugin.Log.LogInfo($"AllocVerify: {site} differs only where the game cannot notice: {detail}");
        }

        // Swaps the call of one method for ours (same arguments, same result). Our replacements hand out pooled arrays
        // that stay the game's until the next call through the same pool, so a pool may serve exactly one call site.
        // With any other count (another mod's transpiler, which runs before ours, or a game update added a second
        // call that could run while the game still walks the first result) the method is left as it is.
        static IEnumerable<CodeInstruction> SwapCall(IEnumerable<CodeInstruction> code, MethodBase original, MethodInfo from, MethodInfo to)
        {
            var list = new List<CodeInstruction>(code);
            int n = 0;
            if (from != null)
            {
                foreach (CodeInstruction ci in list)
                    if (ci.Calls(from))
                        n++;
            }
            if (n == 1)
            {
                foreach (CodeInstruction ci in list)
                {
                    if (ci.Calls(from))
                        Set(ci, OpCodes.Call, to);
                }
            }
            Report(original, from?.Name + " -> " + to.Name, n, 1);
            return list;
        }

        // ---- ZDO keys: prefix + i -----------------------------------------------------------------------------------

        // The game builds these keys as prefix + i.ToString() and the ZDO hashes the text, so a cached string with the
        // same text gives the same key. Labels stay on the instructions they were on.
        static IEnumerable<CodeInstruction> CookingStationKeys(IEnumerable<CodeInstruction> code, MethodBase original) =>
            CachedKeys(code, original, new[] { "slot", "slotstatus" });

        static IEnumerable<CodeInstruction> MineRockKeys(IEnumerable<CodeInstruction> code, MethodBase original) =>
            CachedKeys(code, original, new[] { "Health" });

        //   ldstr prefix; ldarga/ldloca i; call int.ToString(); call string.Concat(string, string)
        // becomes
        //   ldstr prefix; ldarga/ldloca i; ldind.i4;            call ZdoKey(string, int)
        static IEnumerable<CodeInstruction> CachedKeys(IEnumerable<CodeInstruction> code, MethodBase original, string[] prefixes)
        {
            MethodInfo ours = Ours(nameof(ZdoKey));
            var list = new List<CodeInstruction>(code);
            int n = 0;
            for (int i = 0; (i = Find(list, i,
                                 ci => ci.opcode == OpCodes.Ldstr && ci.operand is string s && Array.IndexOf(prefixes, s) >= 0,
                                 ci => IsAddressLoad(ci) && Unlabeled(ci),
                                 ci => ci.Calls(s_intToString) && Unlabeled(ci),
                                 ci => ci.Calls(s_concat2) && Unlabeled(ci))) >= 0; i += 4)
            {
                Set(list[i + 2], OpCodes.Ldind_I4, null);
                Set(list[i + 3], OpCodes.Call, ours);
                n++;
            }
            Report(original, "cached ZDO keys", n);
            return list;
        }

        // Per prefix, the strings prefix + "0", prefix + "1", ... as int.ToString() writes them (no sign, so no culture
        // involved). Main thread only, like the ZDO calls they feed.
        sealed class IndexedKeys
        {
            const int Max = 256;
            internal readonly string Prefix;
            string[] _keys = new string[8];

            internal IndexedKeys(string prefix) => Prefix = prefix;

            internal string Get(int index)
            {
                if ((uint)index >= Max)
                    return Prefix + index.ToString();
                if (index >= _keys.Length)
                    Array.Resize(ref _keys, Math.Min(Max, Math.Max(index + 1, _keys.Length * 2)));
                return _keys[index] ?? (_keys[index] = Prefix + index.ToString());
            }
        }

        static IndexedKeys[] s_keySets = new IndexedKeys[0];

        static IndexedKeys KeysFor(string prefix)
        {
            IndexedKeys[] sets = s_keySets;
            // The prefix comes from an ldstr, so it is the same interned instance every call.
            for (int i = 0; i < sets.Length; i++)
                if ((object)sets[i].Prefix == prefix)
                    return sets[i];
            for (int i = 0; i < sets.Length; i++)
                if (sets[i].Prefix == prefix)
                    return sets[i];
            var created = new IndexedKeys(prefix);
            var grown = new IndexedKeys[sets.Length + 1];
            Array.Copy(sets, grown, sets.Length);
            grown[sets.Length] = created;
            s_keySets = grown;
            return created;
        }

        static string ZdoKey(string prefix, int index)
        {
            if (!s_zdoKeys.On)
                return prefix + index.ToString();
            string key = KeysFor(prefix).Get(index);
            if (s_verify.On)
            {
                VerifyChecks++;
                string game = prefix + index.ToString();
                if (game != key)
                {
                    Mismatch("ZDO key", $"'{key}' vs '{game}'");
                    return game;
                }
            }
            return key;
        }

        // ---- OverlapSphere -----------------------------------------------------------------------------------------

        static IEnumerable<CodeInstruction> ConsumableOverlap(IEnumerable<CodeInstruction> code, MethodBase original) =>
            SwapCall(code, original, s_overlap3, Ours(nameof(OverlapConsumable)));

        static IEnumerable<CodeInstruction> AutoStackOverlap(IEnumerable<CodeInstruction> code, MethodBase original) =>
            SwapCall(code, original, s_overlap3, Ours(nameof(OverlapAutoStack)));

        // One pool per call site (SwapCall swaps only a method's single call): the game walks the returned array until
        // its method returns, and neither method can run again from inside its own loop (FindClosestConsumableItem
        // only reads components, AutoStackItems' ZNetView.Destroy defers the actual destruction to Object.Destroy).
        static readonly ColliderPool s_consumablePool = new ColliderPool("MonsterAI.FindClosestConsumableItem");
        static readonly ColliderPool s_autoStackPool = new ColliderPool("ItemDrop.AutoStackItems");

        // Physics.OverlapSphere(position, radius, layerMask) is the four-argument one with UseGlobal.
        static Collider[] OverlapConsumable(Vector3 position, float radius, int layerMask) =>
            s_consumablePool.OverlapSphere(position, radius, layerMask, QueryTriggerInteraction.UseGlobal);

        static Collider[] OverlapAutoStack(Vector3 position, float radius, int layerMask) =>
            s_autoStackPool.OverlapSphere(position, radius, layerMask, QueryTriggerInteraction.UseGlobal);

        // The same query into a reused buffer, copied into a reused array of exactly the hit count: the game code walks
        // array.Length. Both managed entry points take the same arguments to the same scene (decompiled
        // UnityEngine.PhysicsModule: OverlapSphere_Internal vs OverlapSphereNonAlloc_Internal, no preprocessing), and
        // the native side runs one PhysX overlap either way, so the colliders and their order match; that native part
        // is what AllocVerify checks in the lab. MonsterAI picks the closest with a strict '<', so the order matters
        // there only for exact ties; AutoStackItems merges in array order, so there it matters.
        sealed class ColliderPool
        {
            // Results below this size get a kept array per size (at most ~8k slots per pool); a bigger one, i.e. a
            // dense pile of items, gets a new array per call as in the game, still from a single query.
            const int Kept = 128;
            const int MaxBuffer = 2048;

            readonly string _site;
            Collider[] _buffer = new Collider[Kept];
            readonly Collider[][] _exact = new Collider[Kept][];
            Collider[] _handedOut;

            internal ColliderPool(string site) => _site = site;

            internal Collider[] OverlapSphere(Vector3 position, float radius, int layerMask, QueryTriggerInteraction triggers)
            {
                if (!s_overlap.On)
                    return Physics.OverlapSphere(position, radius, layerMask, triggers);
                // The game is done with the array handed out last time (its method returned before this call, see
                // above), so it is emptied now and the shared buffer right after each copy: between calls the pool
                // keeps at most that one result's collider wrappers (destroyed items included) alive.
                if (_handedOut != null)
                {
                    Array.Clear(_handedOut, 0, _handedOut.Length);
                    _handedOut = null;
                }
                int n = Physics.OverlapSphereNonAlloc(position, radius, _buffer, layerMask, triggers);
                if (n >= _buffer.Length)
                {
                    // A full buffer may have cut hits off, so the game's own query answers this call; the buffer then
                    // grows to fit, so the same dense spot costs one query again from the next call on.
                    Collider[] game = Physics.OverlapSphere(position, radius, layerMask, triggers);
                    Grow(game.Length);
                    return game;
                }
                Collider[] result;
                if (n == 0)
                    result = Array.Empty<Collider>();   // the game reads only Length of an empty result
                else if (n < Kept)
                    result = _handedOut = _exact[n] ?? (_exact[n] = new Collider[n]);
                else
                    result = new Collider[n];
                Array.Copy(_buffer, result, n);
                Array.Clear(_buffer, 0, n);
                if (s_verify.On)
                    return Verify(result, Physics.OverlapSphere(position, radius, layerMask, triggers));
                return result;
            }

            // Sizes the buffer above 'count' (doubling, up to MaxBuffer). The old buffer, still full of references, is
            // dropped; at the cap it is emptied instead.
            void Grow(int count)
            {
                int size = _buffer.Length;
                while (size <= count && size < MaxBuffer)
                    size *= 2;
                if (size > _buffer.Length)
                    _buffer = new Collider[Math.Min(size, MaxBuffer)];
                else
                    Array.Clear(_buffer, 0, _buffer.Length);
            }

            Collider[] Verify(Collider[] ours, Collider[] game)
            {
                VerifyChecks++;
                bool same = ours.Length == game.Length;
                for (int i = 0; same && i < ours.Length; i++)
                    same = (object)ours[i] == game[i];
                if (same)
                    return ours;
                // Either difference counts: AutoStackItems acts in array order.
                bool sameSet = ours.Length == game.Length && new HashSet<UnityEngine.Object>(ours, ByReference.Instance).SetEquals(game);
                Mismatch(_site, sameSet ? $"the same {ours.Length} colliders in another order" : $"{ours.Length} vs {game.Length} colliders, not the same ones");
                return game;
            }
        }

        // ---- Projectile rays ---------------------------------------------------------------------------------------

        static IEnumerable<CodeInstruction> ProjectileRays(IEnumerable<CodeInstruction> code, MethodBase original) =>
            SwapCall(SwapCall(code, original, s_raycastAll, Ours(nameof(ProjectileRaycastAll))),
                     original, s_sphereCastAll, Ours(nameof(ProjectileSphereCastAll)));

        // Physics.RaycastAll prepares the ray in managed code (direction / magnitude, then the Ray constructor
        // normalizes once more) and calls the native query; the public NonAlloc PhysicsScene.Raycast prepares it a little
        // differently (direction.normalized). Ours builds the Ray exactly like RaycastAll and calls the NonAlloc
        // internal the public NonAlloc API ends in, so both native queries get the same Ray; that they report the same
        // hits in the same order is what AllocVerify checks in the lab. The game then sorts the hits by distance
        // (Array.Sort, unstable), so even the order of equal distances follows from the same input order and length.
        // Sphere casts are not settled: Physics.SphereCastAll normalizes the direction in managed code before its
        // native query, but the public NonAlloc PhysicsScene.SphereCast hands the RAW direction to
        // Internal_SphereCastNonAlloc, so that native query must normalize it itself. If the native SphereCastAll does
        // not, our already normalized direction gets normalized a second time and may move by an ulp, which can swap
        // two near-equal distances after the sort or drop a hit right at maxDistance. So sphere casts stay on the
        // game's call unless AllocProjectileSphere (lab, off) is on, until AllocVerify shows no difference there.
        delegate int RaycastIntoFn(PhysicsScene scene, Ray ray, RaycastHit[] hits, float maxDistance, int mask, QueryTriggerInteraction triggers);
        delegate int SphereCastIntoFn(PhysicsScene scene, Vector3 origin, float radius, Vector3 direction, RaycastHit[] hits, float maxDistance, int mask, QueryTriggerInteraction triggers);

        static readonly RaycastIntoFn s_raycastInto = Bind<RaycastIntoFn>(typeof(PhysicsScene), "Internal_RaycastNonAlloc");
        static readonly SphereCastIntoFn s_sphereCastInto = Bind<SphereCastIntoFn>(typeof(PhysicsScene), "Internal_SphereCastNonAlloc");

        static T Bind<T>(Type type, string name) where T : Delegate
        {
            try
            {
                MethodInfo mi = AccessTools.DeclaredMethod(type, name);
                return mi == null ? null : (T)Delegate.CreateDelegate(typeof(T), mi, false);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Projectile.FixedUpdate sorts the array in place and walks it, calling OnHit; nothing in there runs another
        // projectile's FixedUpdate, and SwapCall swaps only the method's single call of each kind, so one pool per
        // query kind is enough.
        static readonly HitPool s_rayPool = new HitPool("Projectile.FixedUpdate RaycastAll");
        static readonly HitPool s_spherePool = new HitPool("Projectile.FixedUpdate SphereCastAll");

        static RaycastHit[] ProjectileRaycastAll(Vector3 origin, Vector3 direction, float maxDistance, int layerMask)
        {
            if (!s_projectiles.On || s_raycastInto == null)
                return Physics.RaycastAll(origin, direction, maxDistance, layerMask);
            // Physics.RaycastAll(origin, direction, maxDistance, layerMask, UseGlobal) up to its native call.
            float magnitude = direction.magnitude;
            if (!(magnitude > float.Epsilon))
                return Array.Empty<RaycastHit>();   // the game returns a new empty array; nobody writes to it
            var ray = new Ray(origin, direction / magnitude);
            int n = s_raycastInto(Physics.defaultPhysicsScene, ray, s_rayPool.Buffer, maxDistance, layerMask, QueryTriggerInteraction.UseGlobal);
            if (n >= s_rayPool.Buffer.Length)
            {
                // Possibly cut off: the game's own query answers, and the buffer grows so this costs one query next time.
                RaycastHit[] game = Physics.RaycastAll(origin, direction, maxDistance, layerMask);
                s_rayPool.Grow(game.Length);
                return game;
            }
            RaycastHit[] result = s_rayPool.Take(n);
            if (s_verify.On)
                return s_rayPool.Verify(result, Physics.RaycastAll(origin, direction, maxDistance, layerMask));
            return result;
        }

        static RaycastHit[] ProjectileSphereCastAll(Vector3 origin, float radius, Vector3 direction, float maxDistance, int layerMask)
        {
            if (!s_projectiles.On || !s_projectileSpheres.On || s_sphereCastInto == null)
                return Physics.SphereCastAll(origin, radius, direction, maxDistance, layerMask);
            // Physics.SphereCastAll(origin, radius, direction, maxDistance, layerMask, UseGlobal) up to its native call
            // (whether the native NonAlloc query then normalizes once more is the open question above).
            float magnitude = direction.magnitude;
            if (!(magnitude > float.Epsilon))
                return Array.Empty<RaycastHit>();
            int n = s_sphereCastInto(Physics.defaultPhysicsScene, origin, radius, direction / magnitude, s_spherePool.Buffer, maxDistance, layerMask,
                                     QueryTriggerInteraction.UseGlobal);
            if (n >= s_spherePool.Buffer.Length)
            {
                RaycastHit[] game = Physics.SphereCastAll(origin, radius, direction, maxDistance, layerMask);
                s_spherePool.Grow(game.Length);
                return game;
            }
            RaycastHit[] result = s_spherePool.Take(n);
            if (s_verify.On)
                return s_spherePool.Verify(result, Physics.SphereCastAll(origin, radius, direction, maxDistance, layerMask));
            return result;
        }

        // RaycastHit holds its collider as an instance id, not a reference, so these arrays keep nothing alive.
        sealed class HitPool
        {
            // As in ColliderPool: a kept array per result size below Kept, a new one per call above.
            const int Kept = 64;
            const int MaxBuffer = 1024;
            // Values (distance, point, normal) within this much, relative to their size but at least absolute, are
            // float rounding: one ulp of a coordinate 10 km from the origin is ~1 mm, a 1e-6 relative band ~1 cm.
            const float Tolerance = 1e-6f;

            static readonly Comparison<RaycastHit> s_byDistance = (x, y) => x.distance.CompareTo(y.distance);

            readonly string _site;
            internal RaycastHit[] Buffer = new RaycastHit[Kept];
            readonly RaycastHit[][] _exact = new RaycastHit[Kept][];

            internal HitPool(string site) => _site = site;

            internal RaycastHit[] Take(int n)
            {
                if (n == 0)
                    return Array.Empty<RaycastHit>();
                RaycastHit[] result = n < Kept ? _exact[n] ?? (_exact[n] = new RaycastHit[n]) : new RaycastHit[n];
                Array.Copy(Buffer, result, n);
                return result;
            }

            // Sizes the buffer above 'count' (doubling, up to MaxBuffer) after a full one.
            internal void Grow(int count)
            {
                int size = Buffer.Length;
                while (size <= count && size < MaxBuffer)
                    size *= 2;
                if (size > Buffer.Length)
                    Buffer = new RaycastHit[Math.Min(size, MaxBuffer)];
            }

            // Exactly equal (same hits, same values, same native order) uses ours; anything else uses the game's answer
            // and is sorted by what Projectile.FixedUpdate would notice. It sorts the hits by distance (Array.Sort,
            // unstable) and walks that order reading collider, distance (== 0 picks the ray start as the hit point),
            // point and normal. So a different set of colliders, a different order after that same sort, or a flipped
            // zero distance is a mismatch (another collider gets OnHit first); the same colliders in the same order with
            // values beyond Tolerance is drift (same targets, slightly other hit points); the rest, such as a different
            // native order that the sort evens out or rounding-level values, is noise.
            internal RaycastHit[] Verify(RaycastHit[] ours, RaycastHit[] game)
            {
                VerifyChecks++;
                bool same = ours.Length == game.Length;
                for (int i = 0; same && i < ours.Length; i++)
                {
                    RaycastHit a = ours[i], b = game[i];
                    same = a.colliderInstanceID == b.colliderInstanceID && a.distance.Equals(b.distance) && a.point.Equals(b.point)
                           && a.normal.Equals(b.normal) && a.triangleIndex == b.triangleIndex;
                }
                if (same)
                    return ours;

                if (!SameColliders(ours, game))
                {
                    Mismatch(_site, $"{ours.Length} vs {game.Length} hits, not the same colliders");
                    return game;
                }
                var sortedOurs = (RaycastHit[])ours.Clone();
                var sortedGame = (RaycastHit[])game.Clone();
                Array.Sort(sortedOurs, s_byDistance);
                Array.Sort(sortedGame, s_byDistance);
                float worst = 0f;
                for (int i = 0; i < sortedOurs.Length; i++)
                {
                    RaycastHit a = sortedOurs[i], b = sortedGame[i];
                    if (a.colliderInstanceID != b.colliderInstanceID)
                    {
                        Mismatch(_site, $"the same {ours.Length} colliders, another order after the game's distance sort (hit {i})");
                        return game;
                    }
                    if ((a.distance == 0f) != (b.distance == 0f))
                    {
                        Mismatch(_site, $"hit {i} starts inside the collider in one answer only (distance {a.distance} vs {b.distance})");
                        return game;
                    }
                    worst = Math.Max(worst, Off(a.distance, b.distance));
                    worst = Math.Max(worst, Math.Max(Off(a.point.x, b.point.x), Math.Max(Off(a.point.y, b.point.y), Off(a.point.z, b.point.z))));
                    worst = Math.Max(worst, Math.Max(Off(a.normal.x, b.normal.x), Math.Max(Off(a.normal.y, b.normal.y), Off(a.normal.z, b.normal.z))));
                }
                if (worst > 1f)
                    Drift(_site, $"{ours.Length} hits, values off by up to {worst:0.#}x the rounding band");
                else
                    Noise(_site, $"{ours.Length} hits: native order or rounding-level values only");
                return game;
            }

            // How far apart two values are, in units of the rounding band (> 1: beyond float rounding).
            static float Off(float a, float b)
            {
                float band = Tolerance * Math.Max(1f, Math.Max(Math.Abs(a), Math.Abs(b)));
                return Math.Abs(a - b) / band;
            }

            static bool SameColliders(RaycastHit[] a, RaycastHit[] b)
            {
                if (a.Length != b.Length)
                    return false;
                var ids = new int[a.Length];
                var other = new int[b.Length];
                for (int i = 0; i < a.Length; i++)
                {
                    ids[i] = a[i].colliderInstanceID;
                    other[i] = b[i].colliderInstanceID;
                }
                Array.Sort(ids);
                Array.Sort(other);
                for (int i = 0; i < ids.Length; i++)
                    if (ids[i] != other[i])
                        return false;
                return true;
            }
        }

        // ---- SpawnSystem -------------------------------------------------------------------------------------------

        //   ldfld SpawnData.m_prefab; callvirt Object.get_name; ldloca index; call int.ToString; call string.Concat(3);
        //   call GetStableHashCode
        // becomes
        //   ldfld SpawnData.m_prefab; nop;                      ldloca index; ldind.i4;          nop;
        //   call SpawnTimerKey(string salt, GameObject prefab, int index)
        static IEnumerable<CodeInstruction> SpawnTimerKeys(IEnumerable<CodeInstruction> code, MethodBase original)
        {
            MethodInfo ours = Ours(nameof(SpawnTimerKey));
            var list = new List<CodeInstruction>(code);
            int n = 0;
            for (int i = 0; (i = Find(list, i,
                                 ci => ci.LoadsField(s_spawnPrefab),
                                 ci => ci.Calls(s_getName) && Unlabeled(ci),
                                 ci => IsAddressLoad(ci) && Unlabeled(ci),
                                 ci => ci.Calls(s_intToString) && Unlabeled(ci),
                                 ci => ci.Calls(s_concat3) && Unlabeled(ci),
                                 ci => ci.Calls(s_stableHash) && Unlabeled(ci))) >= 0; i += 6)
            {
                Set(list[i + 1], OpCodes.Nop, null);
                Set(list[i + 3], OpCodes.Ldind_I4, null);
                Set(list[i + 4], OpCodes.Nop, null);
                Set(list[i + 5], OpCodes.Call, ours);
                n++;
            }
            Report(original, "spawn timer key hashes", n);
            return list;
        }

        //   ldstr "m{0}"; ldloc i; box int; call string.Format(string, object)
        // becomes
        //   ldstr "m{0}"; ldloc i; nop;     call SpawnSalt(string, int)
        static IEnumerable<CodeInstruction> SpawnSalts(IEnumerable<CodeInstruction> code, MethodBase original)
        {
            MethodInfo ours = Ours(nameof(SpawnSalt));
            var list = new List<CodeInstruction>(code);
            int n = 0;
            for (int i = 0; (i = Find(list, i,
                                 ci => ci.opcode == OpCodes.Ldstr,
                                 ci => IsValueLoad(ci) && Unlabeled(ci),
                                 ci => ci.opcode == OpCodes.Box && ci.operand is Type t && t == typeof(int) && Unlabeled(ci),
                                 ci => ci.Calls(s_format1) && Unlabeled(ci))) >= 0; i += 4)
            {
                Set(list[i + 2], OpCodes.Nop, null);
                Set(list[i + 3], OpCodes.Call, ours);
                n++;
            }
            Report(original, "spawn salts", n);
            return list;
        }

        // Keyed by the salt's text, the prefab object and the spawner's index; the value is what the game hashes,
        // computed the game's way on first use. The one assumption: a prefab's name does not change while the game
        // runs. The game itself relies on that (ZNetScene and ObjectDB index prefabs by the hash of their name, so a
        // renamed prefab would break its own lookups), and switching the feature on again starts the cache fresh.
        // A missing or destroyed prefab takes the game's path, which throws exactly as the game would.
        readonly struct SpawnKey : IEquatable<SpawnKey>
        {
            readonly string _salt;
            readonly GameObject _prefab;
            readonly int _index;

            internal SpawnKey(string salt, GameObject prefab, int index)
            {
                _salt = salt;
                _prefab = prefab;
                _index = index;
            }

            public bool Equals(SpawnKey other) =>
                (object)_prefab == other._prefab && _index == other._index && string.Equals(_salt, other._salt);

            public override bool Equals(object obj) => obj is SpawnKey other && Equals(other);

            public override int GetHashCode() =>
                (RuntimeHelpers.GetHashCode(_prefab) * 397 ^ _index) * 397 ^ (_salt?.GetHashCode() ?? 0);
        }

        static readonly Dictionary<SpawnKey, int> s_spawnHashes = new Dictionary<SpawnKey, int>();

        static int SpawnTimerKey(string salt, GameObject prefab, int index)
        {
            if (!s_spawn.On || (object)prefab == null || !prefab)
                return (salt + prefab.name + index.ToString()).GetStableHashCode();
            var key = new SpawnKey(salt, prefab, index);
            if (!s_spawnHashes.TryGetValue(key, out int hash))
            {
                hash = (salt + prefab.name + index.ToString()).GetStableHashCode();
                // Spawn lists are fixed per world; a mod that rebuilds them keeps this bounded.
                if (s_spawnHashes.Count >= 4096)
                    s_spawnHashes.Clear();
                s_spawnHashes.Add(key, hash);
            }
            else if (s_verify.On)
            {
                VerifyChecks++;
                int game = (salt + prefab.name + index.ToString()).GetStableHashCode();
                if (game != hash)
                {
                    Mismatch("SpawnSystem timer key", $"{prefab.name} #{index}");
                    s_spawnHashes[key] = game;
                    return game;
                }
            }
            return hash;
        }

        // The corner alt-biome salts "m0".."m3", made by string.Format("m{0}", i) once instead of every second.
        static string s_saltFormat;
        static readonly string[] s_salts = new string[16];

        static string SpawnSalt(string format, int i)
        {
            if (!s_spawn.On || format == null || (uint)i >= s_salts.Length)
                return string.Format(format, (object)i);
            if ((object)format != s_saltFormat)
            {
                if (format != s_saltFormat)
                    Array.Clear(s_salts, 0, s_salts.Length);
                s_saltFormat = format;
            }
            string salt = s_salts[i] ?? (s_salts[i] = string.Format(format, (object)i));
            if (s_verify.On)
            {
                VerifyChecks++;
                string game = string.Format(format, (object)i);
                if (game != salt)
                {
                    Mismatch("SpawnSystem salt", $"'{salt}' vs '{game}'");
                    return game;
                }
            }
            return salt;
        }

        // ---- Heightmap collision indices ---------------------------------------------------------------------------

        // RebuildCollisionMesh fills the shared s_tempIndices list (m_width² * 6 indices) and uploads
        // s_tempIndices.ToArray() when a mesh has no index buffer yet, i.e. once per new heightmap: for a 64 m zone at
        // 1 m spacing a 96 KB array of garbage each, 3.6-9.5 MB per far area of 37-97 zones loaded (and again on
        // every reload). Mesh.SetIndices(List<int>, ...) reaches the same native call
        // (SetIndicesImpl with UInt32 format, start 0, the list's count, calculateBounds true, baseVertex 0) straight
        // from the list's own array; the game's RebuildRenderMesh already uploads that list this way.
        //   ldsfld s_tempIndices; callvirt List<int>.ToArray; ldc.i4.0; ldc.i4.0; callvirt Mesh.SetIndices(int[], MeshTopology, int)
        // becomes
        //   ldsfld s_tempIndices; nop;                        ldc.i4.0; ldc.i4.0; call SetCollisionIndices(Mesh, List<int>, MeshTopology, int)
        static IEnumerable<CodeInstruction> CollisionIndices(IEnumerable<CodeInstruction> code, MethodBase original)
        {
            MethodInfo ours = Ours(nameof(SetCollisionIndices));
            var list = new List<CodeInstruction>(code);
            int n = 0;
            for (int i = 0; (i = Find(list, i,
                                 ci => ci.Calls(s_listToArray),
                                 ci => ci.LoadsConstant() && Unlabeled(ci),
                                 ci => ci.LoadsConstant() && Unlabeled(ci),
                                 ci => ci.Calls(s_setIndices) && Unlabeled(ci))) >= 0; i += 4)
            {
                Set(list[i], OpCodes.Nop, null);
                Set(list[i + 3], OpCodes.Call, ours);
                n++;
            }
            Report(original, "index uploads from the list", n);
            return list;
        }

        static void SetCollisionIndices(Mesh mesh, List<int> indices, MeshTopology topology, int submesh)
        {
            if (!s_heightmap.On)
            {
                mesh.SetIndices(indices.ToArray(), topology, submesh);
                return;
            }
            mesh.SetIndices(indices, topology, submesh, true, 0);
        }

        // ---- Turret ammo names -------------------------------------------------------------------------------------

        //   ldfld AmmoType.m_ammo; callvirt Object.get_name   ->   ldfld AmmoType.m_ammo; call AmmoPrefabName(Object)
        static IEnumerable<CodeInstruction> TurretAmmoNames(IEnumerable<CodeInstruction> code, MethodBase original)
        {
            MethodInfo ours = Ours(nameof(AmmoPrefabName));
            var list = new List<CodeInstruction>(code);
            int n = 0;
            for (int i = 0; (i = Find(list, i,
                                 ci => ci.LoadsField(s_ammoField),
                                 ci => ci.Calls(s_getName) && Unlabeled(ci))) >= 0; i += 2)
            {
                Set(list[i + 1], OpCodes.Call, ours);
                n++;
            }
            Report(original, "cached ammo names", n);
            return list;
        }

        sealed class ByReference : IEqualityComparer<UnityEngine.Object>
        {
            internal static readonly ByReference Instance = new ByReference();
            public bool Equals(UnityEngine.Object x, UnityEngine.Object y) => (object)x == y;
            public int GetHashCode(UnityEngine.Object obj) => RuntimeHelpers.GetHashCode(obj);
        }

        static readonly Dictionary<UnityEngine.Object, string> s_prefabNames = new Dictionary<UnityEngine.Object, string>(ByReference.Instance);

        // m_ammo is an ItemDrop prefab; same assumption as the spawn keys (a prefab keeps its name). A missing or
        // destroyed one reads its name the game's way, throwing exactly as the game would.
        static string AmmoPrefabName(UnityEngine.Object prefab)
        {
            if (!s_turret.On || (object)prefab == null || !prefab)
                return prefab.name;
            if (!s_prefabNames.TryGetValue(prefab, out string name))
            {
                name = prefab.name;
                if (s_prefabNames.Count >= 4096)
                    s_prefabNames.Clear();
                s_prefabNames.Add(prefab, name);
            }
            else if (s_verify.On)
            {
                VerifyChecks++;
                string game = prefab.name;
                if (game != name)
                {
                    Mismatch("Turret ammo name", $"'{name}' vs '{game}'");
                    s_prefabNames[prefab] = game;
                    return game;
                }
            }
            return name;
        }
    }
}
