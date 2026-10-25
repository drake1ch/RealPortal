using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace RealPortal.Engine
{
    // Timer fleets fire in lockstep. Fireplace.Awake starts InvokeRepeating("UpdateFireplace", 0, 2) and
    // InvokeRepeating("CheckEnv", 4, 4) (Fireplace.cs:142-143); Smelter.Awake InvokeRepeating("UpdateSmelter", 1, 1)
    // (Smelter.cs:123). Unity keeps each timer's phase forever, so every fire or smelter created in the same frame (an
    // area or base load, a portal arrival) runs these in the same frame for the rest of its life. CheckEnv in rain or
    // wind runs Cover.GetCoverForPoint (17 rays of 30 m plus a 100 m sphere cast) after its terrain/roof ray, so 100
    // torches are ~1800 casts in one frame every 4 s; UpdateSmelter checks its roof (Cover.IsUnderRoof) every second;
    // UpdateFireplace does ZDO work (plus Path of Valheiman's prefix) for every fire every 2 s.
    //
    // TimerJitter (opt-in, off by default) gives each such timer a phase of its own after its first call. The first
    // call stays exactly where the game puts it (Invoke(name, delay): the same scheduling as InvokeRepeating's first
    // call), and the repeats run every period from delay + period * (1 - u), u in [0, 1): the second call comes
    // somewhere in (delay, delay + period] instead of at delay + period, and every later one keeps that offset. So
    // every object still runs its method as often as in the game; once, at load, one interval is shorter than the
    // period.
    // The first call has to stay put. UpdateSmelter's first call processes the whole offline accumulator (up to 3600 s,
    // Smelter.cs:420-476) against one sample of the roof, the smoke and a windmill's live wind, and CheckEnv's first
    // call (4 s) sets m_blocked/m_wet, which decide IsBurning, the fuel UpdateFireplace burns and RPC_ToggleOn. Moving
    // either while a base is still being created (roof pieces arrive over several seconds) changes what is produced and
    // written, not only when.
    // u follows the golden-ratio sequence (no UnityEngine.Random draw, so the game's random sequence is untouched;
    // consecutive objects of a fleet get evenly spread phases).
    // What it still changes in play, and why it is off by default: after the first call, the environment is sampled
    // and the ZDO is written at other moments within each period. A fire notices rain, wind, a new roof or terrain up
    // to one period (4 s) sooner or later relative to a change, so a fire that can be turned off burns (fuel) and sends
    // RPC_ToggleOn at another moment; a smelter samples its roof, smoke and a windmill's wind at another offset within
    // the second, so a windmill's flour and a smelter whose smoke or roof changes can come out slightly different;
    // UpdateFireplace's fuel and last-time writes reach peers at other moments (fuel used is elapsed time / secPerFuel
    // as before). The ZDO keys, values and RPCs are the game's, but the replicated totals are not guaranteed to be the
    // game's, so this is a timing change of gameplay, accepted (like WearNTearActiveSectors) only after the lab run.
    //
    // A transpiler (Priority.Last) swaps the InvokeRepeating calls in those Awake methods for ours; ours calls the
    // game's InvokeRepeating unchanged for any other timer and while the feature is off. Every routed timer is
    // remembered (owner, method, Awake time, delay, period, u), so switching also moves the timers that are already
    // running: in the next LateUpdate (after that frame's invokes have run, so the next call is the first grid point
    // after now and none due in this frame is repeated or dropped) each live one is cancelled and rescheduled onto the
    // game's grid (Awake + delay + k * period) when switched off, or onto its jittered grid when switched on; one
    // whose first call is still pending gets that first call at the game's time either way. The interval across the
    // switch is, once, shorter or longer than a period. Switched off, every timer is back on the game's period and in
    // the game's lockstep with the timers created in the same frame (one load), so a lab A/B leg needs no reload. Its
    // phase is the grid's: if Unity reschedules a repeat from the frame it ran in rather than from its due time, the
    // game's own timer drifts later by part of a frame per call, and the grid does not follow that drift. A timer the
    // game or a mod cancelled (not IsInvoking) is left alone. Left out: Windmill.CheckCover (0.1/5; few windmills,
    // and its first cover check feeds power output), Fireplace.UpdateIgnite (Ashlands ignition, gameplay) and
    // UpdateSnowMelt (already random), Door/Container/CookingStation/Sign (cheap bodies). Other fleets can be added
    // to s_timers once the lab shows them in the per-frame spike profile.
    [EngineModule]
    public static class TimerJitterModule
    {
        static readonly Feature s_feature = Features.Register("TimerJitter", false,
            "Opt-in, changes timing: fireplaces' and smelters' repeating timers keep the game's first call but run their later calls at spread phases (same period), so a loaded base does not run all of them in one frame.");

        // Lab counters.
        public static long Jittered, PassedThrough, Rephased, RephaseErrors;
        public static int Tracked => s_tracked.Count;

        const string LabOwners = "valheim.realportal.lab";
        const double Golden = 0.6180339887498949;
        static double s_phase;

        // A routed timer, kept for switching. Owner is a strong reference to the component; dead ones (destroyed
        // objects) are swept a few per new timer and all at every switch.
        sealed class Timer
        {
            internal MonoBehaviour Owner;
            internal string Method;
            internal double Origin;     // Time.timeAsDouble when the game scheduled it (Awake)
            internal float Delay, Period;
            internal float U;           // its jitter phase, [0, 1)
            internal bool Jitter;       // the schedule it is on now
        }

        static readonly List<Timer> s_tracked = new List<Timer>();
        static int s_sweep;
        static TimerJitterRephase s_runner;

        // (type, method name) of the timers this spreads.
        static readonly HashSet<string> s_timers = new HashSet<string>(StringComparer.Ordinal)
        {
            "Fireplace.UpdateFireplace",
            "Fireplace.CheckEnv",
            "Smelter.UpdateSmelter",
        };

        static readonly MethodInfo s_invokeRepeating = AccessTools.Method(typeof(MonoBehaviour), nameof(MonoBehaviour.InvokeRepeating),
            new[] { typeof(string), typeof(float), typeof(float) });

        static void Install(Harmony harmony)
        {
            if (s_invokeRepeating == null)
                throw new MissingMethodException("TimerJitter: MonoBehaviour.InvokeRepeating(string, float, float) not found");
            Transpile(harmony, typeof(Fireplace), "Awake");
            Transpile(harmony, typeof(Smelter), "Awake");
            s_feature.Switched += _ => RequestRephase();
        }

        static void Transpile(Harmony harmony, Type type, string method)
        {
            MethodInfo original = AccessTools.DeclaredMethod(type, method);
            if (original == null)
            {
                EnginePlugin.Log.LogWarning($"TimerJitter: {type.Name}.{method} not found, left as it is.");
                return;
            }
            try
            {
                harmony.Patch(original, transpiler: new HarmonyMethod(typeof(TimerJitterModule), nameof(Timers)) { priority = Priority.Last });
            }
            catch (Exception e)
            {
                EnginePlugin.Log.LogError($"TimerJitter: {type.Name}.{method} not patched: {e}");
            }
        }

        static readonly Dictionary<string, string> s_reported = new Dictionary<string, string>();

        static IEnumerable<CodeInstruction> Timers(IEnumerable<CodeInstruction> code, MethodBase original)
        {
            var list = new List<CodeInstruction>(code);
            string where = original?.DeclaringType?.Name + "." + original?.Name;
            string outcome;
            if (TimerJitterModule.ForeignTranspiler(original, LabOwners))
            {
                outcome = "another mod transpiles it, left as it is";
            }
            else
            {
                MethodInfo ours = AccessTools.DeclaredMethod(typeof(TimerJitterModule), nameof(Repeat));
                int n = 0;
                foreach (CodeInstruction ci in list)
                    if ((ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) && Equals(ci.operand, s_invokeRepeating))
                    {
                        ci.opcode = OpCodes.Call;
                        ci.operand = ours;
                        n++;
                    }
                outcome = n == 0 ? "no InvokeRepeating found, left as it is" : $"{n} timers routed";
            }
            if (!s_reported.TryGetValue(where, out string last) || last != outcome)
            {
                s_reported[where] = outcome;
                EnginePlugin.Log?.LogInfo($"TimerJitter: {where}: {outcome}");
            }
            return list;
        }

        // MonoBehaviour.InvokeRepeating(name, time, repeatRate), same stack (this, name, time, rate).
        static void Repeat(MonoBehaviour self, string methodName, float time, float repeatRate)
        {
            if (repeatRate <= 0f || time < 0f || ReferenceEquals(self, null) || !s_timers.Contains(self.GetType().Name + "." + methodName))
            {
                if (s_feature.On)
                    PassedThrough++;
                self.InvokeRepeating(methodName, time, repeatRate);
                return;
            }
            s_phase += Golden;
            s_phase -= Math.Floor(s_phase);
            var t = new Timer
            {
                Owner = self,
                Method = methodName,
                Origin = Time.timeAsDouble,
                Delay = time,
                Period = repeatRate,
                U = (float)s_phase,
                Jitter = s_feature.On,
            };
            SweepSome(2);
            s_tracked.Add(t);
            if (!t.Jitter)
            {
                self.InvokeRepeating(methodName, time, repeatRate);    // the game's call
                return;
            }
            // The game's first call, then the repeats from delay + period * (1 - u), in (delay, delay + period].
            self.Invoke(methodName, time);
            self.InvokeRepeating(methodName, time + repeatRate * (1f - t.U), repeatRate);
            Jittered++;
        }

        // The first call of a timer's schedule strictly after 'now': the first call at origin + delay, then repeats from
        // origin + delay + step every period (step = period on the game's grid, period * (1 - u) on the jittered one).
        internal static double NextDue(double origin, double delay, double period, double step, double now)
        {
            double first = origin + delay;
            if (first > now)
                return first;
            double g0 = first + step;
            if (g0 > now)
                return g0;
            double next = g0 + (Math.Floor((now - g0) / period) + 1.0) * period;
            while (next <= now)     // rounding
                next += period;
            return next;
        }

        static void SweepSome(int n)
        {
            for (; n > 0 && s_tracked.Count != 0; n--)
            {
                if (s_sweep >= s_tracked.Count)
                    s_sweep = 0;
                if (s_tracked[s_sweep].Owner == null)
                    RemoveAt(s_sweep);
                else
                    s_sweep++;
            }
        }

        static void RemoveAt(int i)
        {
            int last = s_tracked.Count - 1;
            s_tracked[i] = s_tracked[last];
            s_tracked.RemoveAt(last);
        }

        // Feature switched: the running timers are moved in the next LateUpdate. By then Unity has run every invoke due
        // in this frame (the invoke pass runs after Update), so rescheduling onto calls strictly after now neither
        // repeats nor drops one; a switch made in Update (the lab's commands) would otherwise not know whether this
        // frame's due calls had already run.
        static void RequestRephase()
        {
            for (int i = s_tracked.Count - 1; i >= 0; i--)
                if (s_tracked[i].Owner == null)
                    RemoveAt(i);
            if (s_tracked.Count == 0)
                return;
            try
            {
                if (s_runner == null)
                {
                    var go = new GameObject("RealPortal TimerJitter") { hideFlags = HideFlags.HideAndDontSave };
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    s_runner = go.AddComponent<TimerJitterRephase>();
                }
                s_runner.enabled = true;
            }
            catch (Exception e)
            {
                EnginePlugin.Log?.LogWarning("TimerJitter: no LateUpdate runner, the running timers are moved now: " + e.Message);
                Rephase();
            }
        }

        internal static void Rephase()
        {
            bool jitter = s_feature.On;
            double now = Time.timeAsDouble;
            for (int i = s_tracked.Count - 1; i >= 0; i--)
            {
                Timer t = s_tracked[i];
                MonoBehaviour b = t.Owner;
                if (b == null)
                {
                    RemoveAt(i);
                    continue;
                }
                if (t.Jitter == jitter)
                    continue;
                try
                {
                    if (!b.IsInvoking(t.Method))
                    {
                        RemoveAt(i);     // cancelled by the game or a mod: not ours to restart
                        continue;
                    }
                    double first = t.Origin + t.Delay;
                    double step = jitter ? t.Period * (1.0 - t.U) : t.Period;
                    b.CancelInvoke(t.Method);
                    if (first > now)
                    {
                        // The first call is still pending: at the game's time, as at creation.
                        if (jitter)
                        {
                            b.Invoke(t.Method, (float)(first - now));
                            b.InvokeRepeating(t.Method, (float)(first + step - now), t.Period);
                        }
                        else
                        {
                            b.InvokeRepeating(t.Method, (float)(first - now), t.Period);
                        }
                    }
                    else
                    {
                        b.InvokeRepeating(t.Method, (float)(NextDue(t.Origin, t.Delay, t.Period, step, now) - now), t.Period);
                    }
                    t.Jitter = jitter;
                    Rephased++;
                }
                catch (Exception e)
                {
                    RephaseErrors++;
                    if (RephaseErrors <= 5)
                        EnginePlugin.Log?.LogWarning($"TimerJitter: {t.Method} not moved: {e.Message}");
                }
            }
        }

        // Another mod's transpiler or IL manipulator on the method (ours and the lab's do not count).
        internal static bool ForeignTranspiler(MethodBase method, string labOwners)
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
            foreach (IEnumerable<HarmonyLib.Patch> group in new[] { info.Transpilers, info.ILManipulators })
            {
                if (group == null)
                    continue;
                foreach (HarmonyLib.Patch p in group)
                    if (p.owner != EnginePlugin.Guid && !(p.owner != null && p.owner.StartsWith(labOwners, StringComparison.Ordinal)))
                        return true;
            }
            return false;
        }

        public static string Describe() =>
            $"jittered {Jittered} timers, passed through {PassedThrough}; tracked {s_tracked.Count}, moved on switch {Rephased} ({RephaseErrors} errors)";
    }

    // TimerJitter's one-shot LateUpdate (see RequestRephase); disabled again once it has run.
    internal sealed class TimerJitterRephase : MonoBehaviour
    {
        void LateUpdate()
        {
            enabled = false;
            TimerJitterModule.Rephase();
        }
    }

    // Minimap.Explore(Vector3, radius) runs every 2 s (m_exploreInterval) and, when it uncovered any fog pixel, calls
    // m_fogTexture.Apply(): the whole fog texture (m_textureSize^2, R8G8 = 2 bytes per pixel; 8 MB at 2048) is copied to
    // the GPU, for at most (2 * ceil(radius / m_pixelSize) + 1)^2 changed pixels (5 x 5 with the defaults 100 / 64).
    // That is a 1-3 ms main-thread copy (and the same again on the render thread) every 2 s while exploring, and on
    // every portal arrival in unexplored land.
    //
    // MinimapFogPartial (on) uploads only the square Explore just walked (clipped to the texture): its rows are copied
    // from the texture's own CPU data (GetPixelData, no copy of the whole) into a small staging texture of the same
    // format, uploaded, and copied on the GPU into place (Graphics.CopyTexture). The CPU data is the game's (SetPixel in
    // Explore(int, int) already wrote it), and the GPU texture ends up holding exactly the bytes a full Apply would
    // have uploaded for every pixel Explore can have changed; everything else on the GPU already equals the CPU data,
    // because every other writer of the fog in the game (Reset, ExploreAll, map load, shared-map merge) and in Path of
    // Valheiman (EvenMapFog) applies its own changes with a full Apply. The fog texture has no mip chain, so there is
    // nothing else to update.
    // A full Apply is still made (exactly the game's call) when: the feature is off; the texture has mips or is not the
    // expected size; copyTextureSupport lacks Basic; the square is larger than the staging texture; anything throws;
    // or another mod hooks Texture2D.Apply, because skipping the call would skip its hook. Jotunn's MinimapManager
    // hooks every Apply to mark its map overlays dirty; while it has no overlays and no drawings (none of the installed
    // mods creates any) its hook does nothing, so it does not count; once it has some, the full Apply is used.
    // A mod that writes fog pixels without applying them and relies on the next Explore to upload them would see those
    // pixels uploaded later (at the next full Apply); none of the installed mods does.
    // MinimapFogVerify (lab, off) reads every partially uploaded square back from the GPU (AsyncGPUReadback) and
    // compares it with the CPU bytes.
    [EngineModule]
    public static class MinimapFogModule
    {
        static readonly Feature s_feature = Features.Register("MinimapFogPartial", true,
            "Minimap exploring uploads only the fog pixels it uncovered instead of the whole fog texture every 2 s.");
        static readonly Feature s_verify = Features.Register("MinimapFogVerify", false,
            "Lab: reads each partial fog upload back from the GPU and compares it with the texture's CPU data.");

        // Lab counters.
        public static long Partial, Full, BytesSkipped, VerifyChecks, VerifyMismatches, VerifyErrors;
        public static string LastFallback = "";

        const int Staging = 64;
        const string LabOwners = "valheim.realportal.lab";
        const float HookCheckSeconds = 2f;   // the explore interval: a new Jotunn overlay is noticed by the next upload

        delegate void WorldToPixelFn(Minimap self, Vector3 p, out int px, out int py);
        static WorldToPixelFn s_worldToPixel;
        static Texture2D s_staging;
        static MethodInfo s_apply, s_applyDefault;
        static float s_nextHookCheck = float.NegativeInfinity;
        static bool s_foreignHooks;
        static bool s_loggedError;

        static void Install(Harmony harmony)
        {
            MethodInfo explore = AccessTools.DeclaredMethod(typeof(Minimap), "Explore", new[] { typeof(Vector3), typeof(float) });
            MethodInfo worldToPixel = AccessTools.DeclaredMethod(typeof(Minimap), "WorldToPixel", new[] { typeof(Vector3), typeof(int).MakeByRefType(), typeof(int).MakeByRefType() });
            s_apply = AccessTools.DeclaredMethod(typeof(Texture2D), nameof(Texture2D.Apply), new[] { typeof(bool), typeof(bool) });
            s_applyDefault = AccessTools.DeclaredMethod(typeof(Texture2D), nameof(Texture2D.Apply), Type.EmptyTypes);
            if (explore == null || worldToPixel == null)
                throw new MissingMethodException("MinimapFog: Minimap.Explore(Vector3, float) or WorldToPixel not found (game update?)");
            s_worldToPixel = (WorldToPixelFn)Delegate.CreateDelegate(typeof(WorldToPixelFn), worldToPixel);
            harmony.Patch(explore, transpiler: new HarmonyMethod(typeof(MinimapFogModule), nameof(ExploreApply)) { priority = Priority.Last });
            s_feature.Switched += _ => s_nextHookCheck = float.NegativeInfinity;
        }

        static readonly Dictionary<string, string> s_reported = new Dictionary<string, string>();

        // The one m_fogTexture.Apply() in Explore(Vector3, float) -> FogApply(texture, this, p, radius).
        static IEnumerable<CodeInstruction> ExploreApply(IEnumerable<CodeInstruction> code, MethodBase original)
        {
            var list = new List<CodeInstruction>(code);
            MethodInfo game = AccessTools.DeclaredMethod(typeof(Texture2D), nameof(Texture2D.Apply), Type.EmptyTypes);
            string outcome;
            if (TimerJitterModule.ForeignTranspiler(original, LabOwners))
            {
                outcome = "another mod transpiles it, left as it is";
            }
            else
            {
                var hits = new List<int>();
                for (int i = 0; i < list.Count; i++)
                    if ((list[i].opcode == OpCodes.Callvirt || list[i].opcode == OpCodes.Call) && Equals(list[i].operand, game))
                        hits.Add(i);
                if (hits.Count == 1)
                {
                    int at = hits[0];
                    // The call instruction is edited in place into the first new one (ldarg.0), so any labels and blocks
                    // on it stay at the start of the sequence (the stack there still holds the texture); Harmony's
                    // Label type cannot be named from netstandard (see AllocFixesModule).
                    list[at].opcode = OpCodes.Ldarg_0;
                    list[at].operand = null;
                    list.InsertRange(at + 1, new[]
                    {
                        new CodeInstruction(OpCodes.Ldarg_1),
                        new CodeInstruction(OpCodes.Ldarg_2),
                        new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(MinimapFogModule), nameof(FogApply))),
                    });
                    outcome = "partial fog uploads";
                }
                else
                {
                    outcome = $"{hits.Count} Texture2D.Apply() calls (expected 1), left as it is";
                }
            }
            if (!s_reported.TryGetValue("Explore", out string last) || last != outcome)
            {
                s_reported["Explore"] = outcome;
                EnginePlugin.Log?.LogInfo($"MinimapFog: Minimap.Explore: {outcome}");
            }
            return list;
        }

        static void FogApply(Texture2D fog, Minimap minimap, Vector3 p, float radius)
        {
            if (!s_feature.On || !TryPartial(fog, minimap, p, radius))
            {
                Full++;
                fog.Apply();    // the game's own call (a null texture throws here as in the game)
            }
        }

        static bool TryPartial(Texture2D fog, Minimap minimap, Vector3 p, float radius)
        {
            try
            {
                if (fog == null || minimap == null || s_worldToPixel == null)
                    return Fallback("missing texture or minimap");
                if (ForeignApplyHooks())
                    return Fallback("another mod hooks Texture2D.Apply");
                int size = minimap.m_textureSize;
                if (fog.width != size || fog.height != size || fog.mipmapCount != 1 || !fog.isReadable)
                    return Fallback("unexpected fog texture");
                if ((SystemInfo.copyTextureSupport & CopyTextureSupport.Basic) == 0)
                    return Fallback("no CopyTexture");
                GraphicsFormat format = fog.graphicsFormat;
                if (GraphicsFormatUtility.IsCompressedFormat(format))
                    return Fallback("compressed format");
                int bpp = (int)GraphicsFormatUtility.GetBlockSize(format);
                // The square Explore(Vector3, float) walked, computed as it computes it.
                int num = (int)Mathf.Ceil(radius / minimap.m_pixelSize);
                s_worldToPixel(minimap, p, out int px, out int py);
                int x0 = Math.Max(0, px - num), x1 = Math.Min(size - 1, px + num);
                int y0 = Math.Max(0, py - num), y1 = Math.Min(size - 1, py + num);
                if (x0 > x1 || y0 > y1)
                    return Fallback("empty square");
                int w = x1 - x0 + 1, h = y1 - y0 + 1;
                if (w > Staging || h > Staging)
                    return Fallback("square larger than the staging texture");
                if (s_staging == null || s_staging.graphicsFormat != format)
                {
                    if (s_staging != null)
                        UnityEngine.Object.Destroy(s_staging);
                    s_staging = new Texture2D(Staging, Staging, format, TextureCreationFlags.None)
                    {
                        name = "RealPortal fog staging",
                        hideFlags = HideFlags.HideAndDontSave,
                        wrapMode = TextureWrapMode.Clamp,
                    };
                }
                NativeArray<byte> src = fog.GetPixelData<byte>(0);
                NativeArray<byte> dst = s_staging.GetPixelData<byte>(0);
                if (src.Length != size * size * bpp || dst.Length != Staging * Staging * bpp)
                    return Fallback("unexpected pixel data size");
                int rowBytes = w * bpp;
                for (int r = 0; r < h; r++)
                    NativeArray<byte>.Copy(src, ((y0 + r) * size + x0) * bpp, dst, r * Staging * bpp, rowBytes);
                s_staging.Apply(false);
                Graphics.CopyTexture(s_staging, 0, 0, 0, 0, w, h, fog, 0, 0, x0, y0);
                Partial++;
                BytesSkipped += (long)size * size * bpp - (long)Staging * Staging * bpp;
                if (s_verify.On && SystemInfo.supportsAsyncGPUReadback)
                    Verify(fog, src, size, bpp, x0, y0, w, h);
                return true;
            }
            catch (Exception e)
            {
                if (!s_loggedError)
                {
                    s_loggedError = true;
                    EnginePlugin.Log?.LogWarning("MinimapFogPartial: partial upload failed, using the full Apply: " + e);
                }
                return Fallback("exception");
            }
        }

        static bool Fallback(string why)
        {
            LastFallback = why;
            return false;
        }

        // Another mod's patch on Texture2D.Apply() or Apply(bool, bool) would be skipped by a partial upload. Jotunn's
        // MinimapManager hook does nothing while it has no overlays and no drawings.
        static bool ForeignApplyHooks()
        {
            float now = Time.unscaledTime;
            if (now < s_nextHookCheck)
                return s_foreignHooks;
            s_nextHookCheck = now + HookCheckSeconds;
            bool foreign = false;
            try
            {
                foreach (MethodInfo apply in new[] { s_apply, s_applyDefault })
                {
                    HarmonyLib.Patches info = apply == null ? null : Harmony.GetPatchInfo(apply);
                    if (info == null)
                        continue;
                    foreach (IEnumerable<HarmonyLib.Patch> group in new[] { info.Prefixes, info.Postfixes, info.Finalizers, info.Transpilers, info.ILManipulators })
                    {
                        if (group == null)
                            continue;
                        foreach (HarmonyLib.Patch patch in group)
                        {
                            if (patch.owner == EnginePlugin.Guid || patch.owner != null && patch.owner.StartsWith(LabOwners, StringComparison.Ordinal))
                                continue;
                            if (IdleJotunnHook(patch.PatchMethod))
                                continue;
                            foreign = true;
                        }
                    }
                }
            }
            catch
            {
                foreign = true;
            }
            if (foreign != s_foreignHooks)
                EnginePlugin.Log?.LogInfo(foreign ? "MinimapFogPartial: another mod hooks Texture2D.Apply, fog uses full uploads." : "MinimapFogPartial: active.");
            s_foreignHooks = foreign;
            return foreign;
        }

        static bool IdleJotunnHook(MethodInfo hook)
        {
            Type type = hook?.DeclaringType;
            if (type == null || type.FullName != "Jotunn.Managers.MinimapManager" || hook.Name != "Texture2D_Apply")
                return false;
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            // The private _instance, not the Instance getter (which would create the manager).
            object manager = type.GetField("_instance", any)?.GetValue(null);
            if (manager == null)
                return true;
            return Count(type.GetField("Overlays", any)?.GetValue(manager)) == 0 && Count(type.GetField("Drawings", any)?.GetValue(manager)) == 0;
        }

        static int Count(object collection) => collection is System.Collections.ICollection c ? c.Count : -1;

        static void Verify(Texture2D fog, NativeArray<byte> src, int size, int bpp, int x0, int y0, int w, int h)
        {
            var expected = new byte[w * h * bpp];
            for (int r = 0; r < h; r++)
                NativeArray<byte>.Copy(src, ((y0 + r) * size + x0) * bpp, expected, r * w * bpp, w * bpp);
            AsyncGPUReadback.Request(fog, 0, x0, w, y0, h, 0, 1, request =>
            {
                if (request.hasError)
                {
                    VerifyErrors++;
                    return;
                }
                VerifyChecks++;
                NativeArray<byte> got = request.GetData<byte>();
                bool same = got.Length == expected.Length;
                for (int i = 0; same && i < expected.Length; i++)
                    same = got[i] == expected[i];
                if (!same)
                {
                    VerifyMismatches++;
                    if (VerifyMismatches <= 10)
                        EnginePlugin.Log?.LogWarning($"MinimapFogVerify: fog square at {x0},{y0} {w}x{h} differs on the GPU (or the CPU data changed before the readback).");
                }
            });
        }

        public static string Describe() =>
            $"partial {Partial}, full {Full} (last fallback: {LastFallback}), {BytesSkipped / (1024 * 1024)} MB not uploaded; verify {VerifyChecks} checks, {VerifyMismatches} mismatches, {VerifyErrors} errors";
    }
}
