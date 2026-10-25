using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace RealPortal.Engine
{
    // Two exact changes to how the game runs creature AI, and a hook for Real Portal's far areas that is not exact.
    //
    // AiStagger (off by default). MonoUpdaters.FixedUpdate adds fixedDeltaTime (0.02 s) to m_updateAITimer and, once it
    // reaches 0.05 s, calls UpdateAI(0.05) on every BaseAI.Instances entry and takes 0.05 off. So two fixed steps out of
    // every five carry the whole AI load (LOS rays, target refresh, FindPath, static-target and fire checks) and the
    // other three carry none: with a raid or a horde those two steps are the frame-time spikes.
    // Here the game's accumulator still decides when a tick happens ("source tick"), with the same float arithmetic on
    // the same field, but each AI receives every source tick k steps late, k = its phase 0..4, given once when it is
    // first seen (to the phase with the fewest AIs) and kept. So every AI still gets exactly the game's sequence of
    // UpdateAI(0.05) calls, with the game's spacing (2 or 3 steps at 50 Hz) and the same dt, only shifted by 0..4 fixed
    // steps (0..80 ms at 50 Hz); at 50 Hz the five phases tick in pairs {s, s+2} mod 5, so every step carries ~2/5 of the
    // AIs instead of 0 or all of them. Within a step the AIs keep their BaseAI.Instances order, and the pass is still the
    // game's MonoUpdatersExtra.UpdateAI with the same container, scope string and dt: only its 'source' is the due
    // subset (the way HeightmapIdle swaps the source of CustomLateUpdate), so patches on UpdateAI (lab timers) still run
    // around each pass, and PoV's prefixes on MonsterAI/AnimalAI.UpdateAI run on every tick because nothing is skipped.
    // What changes is only what is between AIs: which AIs tick in the same step, and therefore the order of global RNG
    // draws and of reads of other creatures' state (Time.time inside a tick is up to 80 ms later, the same for every
    // tick of that AI). Nothing replicated depends on that interleaving: the owner writes the same kinds of ZDO values at
    // the same per-AI cadence and RPC formats are untouched. Non-owners tick too in the game (BaseAI.UpdateAI returns
    // early for them) and are staggered the same way. Checked outside the game against a model of the accumulator
    // (dt 0.01-0.1, 1000 AIs, churn, on/off switching): every AI's ticks are exactly the source ticks shifted by its
    // phase, phases stay balanced, and at 50 Hz every step carries 2/5 of the AIs.
    // Off is the game's code: the transpiler only puts "if (AiStep(this, dt)) skip the timer block" in front of the
    // block, and AiStep returns false while off, after recording the game's own tick decision. Switching off while
    // some phases are still owed ticks of the "on" period gives those ticks over the next 4 steps (an AI can then get
    // two ticks in one step once); switching on starts the delays from the next source tick. Every source tick reaches
    // every AI that stays in the list exactly once either way, within one session: ticks still owed when a session's
    // MonoUpdaters goes away (logout) are dropped with it, as the game drops its accumulator, and the next session's
    // MonoUpdaters starts the scheduler afresh (nothing owed, phases and remembered AIs forgotten), so no AI of a new
    // world gets a tick of the old one. Only one MonoUpdaters is staggered at a time; should a second live one appear,
    // it runs the game's block for itself.
    // Stands down (behaves as off) while another mod (not the engine or the Real Portal lab) has a transpiler or IL
    // manipulator on MonoUpdaters.FixedUpdate or any patch on MonoUpdatersExtra.UpdateAI (it may expect the whole list
    // two steps in five); if the timer block does not have the game's exact shape (changed by a transpiler that ran
    // before ours, or by an update), the method is left as it is and the log says so.
    //
    // AiFriendScanSkip (on by default). MonsterAI.SelectBestAttack runs BaseAI.HaveFriendsInRange once a second per
    // engaged humanoid: two passes over every Character (IsEnemy, a distance, GetHealth/GetMaxHealth). Its two results go
    // only to Humanoid.EquipBestWeapon, which reads hurtFriend only for an item whose m_aiTargetType is FriendHurt and
    // friend only for one whose type is Friend (Humanoid.cs, the two else-if branches). When the humanoid's inventory,
    // read live at that moment through the same m_inventory.GetAllItems() that EquipBestWeapon walks a moment later,
    // holds no item of either type, neither value is read, so the scans are skipped and null is passed. The scans have
    // no side effects (no writes, no RNG) and EquipBestWeapon's own branches and Random.Range draws are unchanged, so
    // the equipped weapon is the same. Keyed on the live inventory (not the prefab), so items given at runtime (PoV,
    // other mods) are seen. Stands down while another mod patches HaveFriendsInRange, its two scan helpers or
    // EquipBestWeapon, or transpiles SelectBestAttack. Assumption (as everywhere a call is skipped): patches on the
    // getters the scans call (PoV's Character.GetMaxHealth postfix) only change return values, which are unused here.
    //
    // Far AIs (Real Portal's hook IsFarSlow; its switch Far.AiStaggerFar). The AI pass is in FixedUpdate only (20 Hz, the
    // timer block above; MonoUpdaters.Update and LateUpdate run no AI). Real Portal keeps far areas' creatures owned and
    // running, and an owner's UpdateAI is the whole think (LOS rays, target scans, FindPath); a non-owner's returns
    // after one ZDO read. While IsFarSlow is set, every AI that receives a tick (the game's whole-list tick or
    // AiStagger's due subset) is asked: true (a creature this peer owns in a far area that is not the live one) makes
    // it think once every FarEvery (4) of its ticks, and that think gets UpdateAI(k x 0.05) with k the ticks it was
    // owed, so its dt timers (target refresh, regeneration, takeoff, random-move, danger) count the same time. Its
    // phase (how many ticks to its first think) is given in turn when it turns slow and kept while it stays slow, so
    // AIs that turn slow together (an area loading, the live area moving) think in different ticks. An AI the hook no
    // longer calls slow (the live area, the player's zones, the hook cleared) gets what it was owed with its next tick,
    // and every tick after as in the game. A slow AI that leaves the list keeps its owed ticks for 1-2 x FarSweepSteps
    // fixed steps (asleep and woken again: given then), then they are dropped (at most FarEvery - 1 ticks, 0.15 s). The
    // passes are the game's MonoUpdatersExtra.UpdateAI with the game's container and scope: first the AIs owed one tick
    // (dt 0.05) in BaseAI.Instances order, then one pass per larger k. With the hook unset and nothing owed, the code
    // above runs unchanged (off: the game's block); with it set and AiStagger off, AiStep runs the game's block itself
    // (the same accumulator arithmetic, the whole list at its ticks) so the slow AIs can be held back. Not exact: a
    // slow AI picks its move direction 5 times a second instead of 20 (Character still moves it every fixed step), so it
    // turns, stops and attacks up to 0.15 s later, and BaseAI.MoveAndAvoid's stuck timer (Time.fixedDeltaTime per
    // think, a flier's corner escape) runs at a quarter. Ownership and the ZDO values an owner writes are untouched,
    // only their cadence. Stands down with AiStagger (a foreign patch on the pass: owed ticks dropped, every AI as in
    // the game). Lab counters: FarSkipped (AI updates held back), FarThinks, FarFlushes, FarTicks.
    //
    // AiVerify (lab, off): AiStagger checks that consecutive ticks of every AI that stayed in the list carry
    // consecutive source ticks (none lost, none doubled) and counts the result in VerifyChecks/VerifyMismatches (each
    // time it is switched on the chains start over, so ticks given while it was off are not taken for gaps);
    // AiFriendScanSkip runs the real scans and passes their values (the game's behaviour) and counts in
    // FriendWouldSkip how many scans the skip would have saved.
    [EngineModule]
    public static class AiStaggerModule
    {
        internal static readonly Feature Stagger = Features.Register("AiStagger", false,
            "Creature AI ticks (20 Hz) are spread over all fixed steps by phase instead of all AIs in two steps out of five; each AI keeps the game's tick sequence and dt.");
        internal static readonly Feature FriendSkip = Features.Register("AiFriendScanSkip", true,
            "MonsterAI skips its once-a-second scans for friends when no item in the humanoid's inventory targets friends (the results would not be read).");
        internal static readonly Feature Verify = Features.Register("AiVerify", false,
            "Lab: checks that every AI receives every AI tick exactly once under AiStagger, and runs the friend scans anyway while counting the skips AiFriendScanSkip would make.");

        const float Tick = 0.05f;                       // the game's AI period and the dt it passes
        const string Scope = "MonoUpdaters.FixedUpdate.BaseAI";
        const string LabOwners = "valheim.realportal.lab";
        const float ForeignCheckSeconds = 10f;

        // ---- counters for the lab (public static, read by the lab's 'counters' command) -------------------------
        public static bool StaggerPatched => s_staggerPatched;
        public static bool StaggerForeign => s_staggerForeign;
        public static bool StaggerActive => s_lastOn;
        public static long Steps => s_sched.StepCount;
        public static long SourceTicks => s_sched.Sources;
        public static long Deliveries => s_sched.Delivered;
        public static long Passes;                      // UpdateAI passes made by this module
        public static int LastPass => s_sched.LastPass; // AIs ticked by the last pass of this module
        public static int MaxPass => s_sched.MaxPass;   // most AIs ticked in one fixed step by this module since ResetPeaks
        public static int MaxGamePass;                  // most AIs the game's pass would have ticked in one step (BaseAI.Instances at source ticks)
        public static long VerifyChecks => s_sched.VerifyChecks;
        public static long VerifyMismatches => s_sched.VerifyMismatches;
        public static long SchedulerResets => s_sched.Resets;  // new sessions (a new MonoUpdaters) seen
        public static bool FriendPatched => s_friendPatched;
        public static bool FriendForeign => s_friendForeign;
        public static long FriendScansRun;
        public static long FriendScansSkipped;
        public static long FriendWouldSkip;             // AiVerify: skips that would have happened
        public static bool FarActive => s_farOn;        // the last step: IsFarSlow set and usable, no foreign patch
        public static int FarTracked => s_far.Count;    // slow AIs with a phase (and maybe owed ticks)
        public static long FarTicks;                    // ticks that reached slow AIs
        public static long FarSkipped;                  // of those, held back: AI updates skipped
        public static long FarThinks;                   // slow AIs' updates, each with the ticks owed since its last
        public static long FarFlushes;                  // AIs no longer slow, given what they were owed with their next tick
        public static long FarPasses;                   // passes with dt above one tick (one per owed count, in a step)
        public static long FarDropped;                  // owed ticks dropped (AI gone, a foreign patch, a new session)
        public static long FarHookErrors;

        // ---- Hook for Real Portal (main thread; may be set or cleared at any time) ----------------------------

        // True for an AI that thinks at the slow rate: one whose creature this peer owns, in a far area that is not the
        // live one (Real Portal sets it while Far.AiStaggerFar is on). Asked for every AI at every tick it receives, so
        // it must be cheap and must not touch BaseAI.Instances. Null: every AI as in the game. A hook that throws is
        // taken as null until it is replaced or FarHookRetrySeconds have passed (FarHookErrors; logged once per hook).
        public static Func<BaseAI, bool> IsFarSlow;
        // Ticks per think of a slow AI (1: every tick, as in the game; at most MaxFarEvery). The lab may change it; AIs
        // already slow keep phases spread for the old value until they turn slow again.
        public static int FarEvery = 4;

        public static void ResetPeaks()
        {
            s_sched.MaxPass = 0;
            MaxGamePass = 0;
        }

        public static string Describe() =>
            $"AiStagger: patched {s_staggerPatched}, active {s_lastOn}, foreign {s_staggerForeign}, steps {s_sched.StepCount}, source ticks {s_sched.Sources}, "
            + $"deliveries {s_sched.Delivered}, pass last/max {s_sched.LastPass}/{s_sched.MaxPass} (game max {MaxGamePass}), phase load [{string.Join(",", s_sched.Load)}], "
            + $"verify {s_sched.VerifyChecks}/{s_sched.VerifyMismatches} mismatches, sessions {s_sched.Resets}; "
            + $"far AIs: hook {(IsFarSlow != null ? "set" : "unset")}, active {s_farOn}, every {FarEvery}, tracked {s_far.Count}, ticks {FarTicks}, "
            + $"skipped {FarSkipped}, thinks {FarThinks}, flushed {FarFlushes}, passes {FarPasses}, dropped {FarDropped}, hook errors {FarHookErrors}; "
            + $"AiFriendScanSkip: patched {s_friendPatched}, foreign {s_friendForeign}, run {FriendScansRun}, skipped {FriendScansSkipped}, would skip {FriendWouldSkip}";

        // ---- AiStagger state ----------------------------------------------------------------------------------
        static readonly Scheduler<IUpdateAI> s_sched = new Scheduler<IUpdateAI>();
        static readonly List<IUpdateAI> s_subset = new List<IUpdateAI>();
        static AccessTools.FieldRef<MonoUpdaters, float> s_timer;
        static AccessTools.FieldRef<MonoUpdaters, List<IUpdateAI>> s_container;
        static MethodInfo s_fixedUpdate, s_updateAI;
        static bool s_staggerPatched, s_staggerForeign, s_lastOn, s_broken;
        static float s_nextStaggerCheck;
        // The MonoUpdaters (one per session) whose steps the scheduler follows.
        static MonoUpdaters s_owner;
        // The accumulator after this step's add, stored as float32 exactly as the game stores m_updateAITimer.
        static float s_probe;

        // ---- far AIs' state ------------------------------------------------------------------------------------
        const int MaxFarEvery = 8;
        // A slow AI not seen this many fixed steps (left the list) is forgotten with what it was owed.
        const long FarSweepSteps = 256;
        const float FarHookRetrySeconds = 10f;

        // A slow AI: ticks to its next think, and ticks owed (received and held back, plus the one it thinks with).
        sealed class FarEntry
        {
            internal int Wait, Owed;
            internal long Seen;
        }

        static readonly Dictionary<IUpdateAI, FarEntry> s_far = new Dictionary<IUpdateAI, FarEntry>(FarByReference.Instance);
        static readonly Stack<FarEntry> s_farPool = new Stack<FarEntry>();
        static readonly List<IUpdateAI> s_farStale = new List<IUpdateAI>();
        // The AIs that think in this pass by the ticks they are owed (index 1..MaxFarEvery; 1 also every AI that is not slow).
        static readonly List<IUpdateAI>[] s_owed = NewGroups();
        // This step: the hook to ask (null: none slow), and whether the pass goes through Split (hook set or ticks owed).
        static Func<BaseAI, bool> s_farHook;
        static bool s_farPath, s_farOn;
        static int s_farNext;
        static long s_farSweptAt;
        static Delegate s_farBroken;
        static float s_farBrokenAt;

        // ---- AiFriendScanSkip state ---------------------------------------------------------------------------
        static AccessTools.FieldRef<Humanoid, Inventory> s_inventory;
        static MethodBase[] s_friendWatch;
        static MethodInfo s_selectBestAttack;
        static bool s_friendPatched, s_friendForeign;
        static float s_nextFriendCheck;

        static void Install(Harmony harmony)
        {
            try
            {
                InstallStagger(harmony);
            }
            catch (Exception e)
            {
                s_staggerPatched = false;
                EnginePlugin.Log.LogError("AiStagger: not installed, AI ticks stay as in the game: " + e);
            }
            try
            {
                InstallFriendSkip(harmony);
            }
            catch (Exception e)
            {
                s_friendPatched = false;
                EnginePlugin.Log.LogError("AiFriendScanSkip: not installed, friend scans stay as in the game: " + e);
            }
            Stagger.Switched += _ => s_nextStaggerCheck = 0f;
            FriendSkip.Switched += _ => s_nextFriendCheck = 0f;
            Verify.Switched += on => s_sched.Verify = on;
            s_sched.Verify = Verify.On;
        }

        // ======================================================================================================
        // AiStagger
        // ======================================================================================================

        static void InstallStagger(Harmony harmony)
        {
            s_timer = AccessTools.FieldRefAccess<MonoUpdaters, float>("m_updateAITimer");
            s_container = AccessTools.FieldRefAccess<MonoUpdaters, List<IUpdateAI>>("m_ai");
            s_fixedUpdate = AccessTools.DeclaredMethod(typeof(MonoUpdaters), "FixedUpdate");
            s_updateAI = AccessTools.DeclaredMethod(typeof(MonoUpdatersExtra), nameof(MonoUpdatersExtra.UpdateAI));
            if (s_fixedUpdate == null || s_updateAI == null)
            {
                EnginePlugin.Log.LogWarning("AiStagger: MonoUpdaters.FixedUpdate or MonoUpdatersExtra.UpdateAI not found (game update?), left as in the game.");
                return;
            }
            // End of a session: forget its owed ticks and AIs right away (a new MonoUpdaters is also caught in AiStep,
            // so this is only cleanup during the main menu).
            try
            {
                MethodInfo destroy = AccessTools.DeclaredMethod(typeof(MonoUpdaters), "OnDestroy");
                if (destroy != null)
                    harmony.Patch(destroy, postfix: new HarmonyMethod(typeof(AiStaggerModule), nameof(UpdatersDestroyed)));
            }
            catch (Exception e)
            {
                EnginePlugin.Log.LogWarning("AiStagger: MonoUpdaters.OnDestroy not patched (the next session still starts afresh): " + e.Message);
            }
            harmony.Patch(s_fixedUpdate, transpiler: new HarmonyMethod(typeof(AiStaggerModule), nameof(FixedUpdateIL)) { priority = Priority.Last });
        }

        static void UpdatersDestroyed(MonoUpdaters __instance)
        {
            if (!ReferenceEquals(__instance, s_owner))
                return;
            s_owner = null;
            s_sched.Reset();
            s_subset.Clear();
            ResetFar();
        }

        static readonly FieldInfo s_timerField = AccessTools.Field(typeof(MonoUpdaters), "m_updateAITimer");
        static readonly FieldInfo s_aiField = AccessTools.Field(typeof(MonoUpdaters), "m_ai");
        static readonly MethodInfo s_instancesGetter = AccessTools.PropertyGetter(typeof(BaseAI), nameof(BaseAI.Instances));
        static readonly MethodInfo s_aiStep = AccessTools.DeclaredMethod(typeof(AiStaggerModule), nameof(AiStep));
        static readonly Dictionary<string, string> s_reported = new Dictionary<string, string>();

        // Puts "ldarg.0; <dt>; call AiStep; brtrue <after the block>" in front of the game's timer block
        //   m_updateAITimer += dt; if (m_updateAITimer >= 0.05f) { m_ai.UpdateAI(BaseAI.Instances, scope, 0.05f); m_updateAITimer -= 0.05f; }
        // and leaves the block itself (and the rest of the method) as it is. Harmony reruns this whenever any mod patches
        // FixedUpdate; ours runs last, so it sees what earlier transpilers made of the block and only edits the game's shape.
        static IEnumerable<CodeInstruction> FixedUpdateIL(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var code = new List<CodeInstruction>(instructions);
            int found = -1, matches = 0;
            object end = null;
            for (int i = 0; i + 22 < code.Count; i++)
                if (IsTimerBlock(code, i, out object l))
                {
                    matches++;
                    found = i;
                    end = l;
                }
            if (matches != 1)
            {
                s_staggerPatched = false;
                ReportOnce("stagger", $"AiStagger: the AI timer block of MonoUpdaters.FixedUpdate was found {matches} times (changed by another mod or an update?), left as in the game.", true);
                return code;
            }
            var inject = new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(code[found + 3].opcode, code[found + 3].operand),   // the block's own load of dt
                new CodeInstruction(OpCodes.Call, s_aiStep),
                new CodeInstruction(OpCodes.Brtrue, end),
            };
            // Whatever jumped to the block now jumps to the check in front of it.
            MoveMarks(s_labels, code[found], inject[0]);
            MoveMarks(s_blocks, code[found], inject[0]);
            code.InsertRange(found, inject);
            s_staggerPatched = true;
            ReportOnce("stagger", "AiStagger: MonoUpdaters.FixedUpdate AI timer block wrapped.", false);
            return code;
        }

        // The game's 22 instructions (see FixedUpdateIL), with no jump into their middle, followed by the instruction the
        // block's branch goes to.
        static bool IsTimerBlock(List<CodeInstruction> c, int i, out object end)
        {
            end = null;
            if (!(Is(c[i], OpCodes.Ldarg_0) && Is(c[i + 1], OpCodes.Ldarg_0) && LoadsField(c[i + 2], s_timerField)
                  && IsLocalOrArgLoad(c[i + 3]) && Is(c[i + 4], OpCodes.Add) && StoresField(c[i + 5], s_timerField)
                  && Is(c[i + 6], OpCodes.Ldarg_0) && LoadsField(c[i + 7], s_timerField) && IsR4(c[i + 8], Tick)
                  && (Is(c[i + 9], OpCodes.Blt_Un) || Is(c[i + 9], OpCodes.Blt_Un_S)) && c[i + 9].operand != null
                  && Is(c[i + 10], OpCodes.Ldarg_0) && LoadsField(c[i + 11], s_aiField)
                  && Calls(c[i + 12], s_instancesGetter) && Is(c[i + 13], OpCodes.Ldstr) && IsR4(c[i + 14], Tick)
                  && Calls(c[i + 15], s_updateAI)
                  && Is(c[i + 16], OpCodes.Ldarg_0) && Is(c[i + 17], OpCodes.Ldarg_0) && LoadsField(c[i + 18], s_timerField)
                  && IsR4(c[i + 19], Tick) && Is(c[i + 20], OpCodes.Sub) && StoresField(c[i + 21], s_timerField)))
                return false;
            object branch = c[i + 9].operand;
            if (!(s_labels?.GetValue(c[i + 22]) is System.Collections.IList after) || !after.Contains(branch))
                return false;
            for (int k = i + 1; k <= i + 21; k++)
                if (!Plain(c[k]))
                    return false;
            end = branch;
            return true;
        }

        // Harmony's CodeInstruction.labels/blocks are typed with System.Reflection.Emit types that the netstandard2.1
        // reference set cannot resolve (see AllocFixesModule), so they are handled through reflection, labels boxed.
        static readonly FieldInfo s_labels = AccessTools.Field(typeof(CodeInstruction), "labels");
        static readonly FieldInfo s_blocks = AccessTools.Field(typeof(CodeInstruction), "blocks");

        static bool Plain(CodeInstruction ci) =>
            (!(s_labels?.GetValue(ci) is System.Collections.ICollection labels) || labels.Count == 0)
            && (!(s_blocks?.GetValue(ci) is System.Collections.ICollection blocks) || blocks.Count == 0);

        static void MoveMarks(FieldInfo field, CodeInstruction from, CodeInstruction to)
        {
            if (field?.GetValue(from) is System.Collections.IList src && field.GetValue(to) is System.Collections.IList dst)
            {
                foreach (object o in src)
                    dst.Add(o);
                src.Clear();
            }
        }

        static bool Is(CodeInstruction ci, OpCode op) => ci.opcode == op;
        static bool LoadsField(CodeInstruction ci, FieldInfo f) => f != null && ci.opcode == OpCodes.Ldfld && Equals(ci.operand, f);
        static bool StoresField(CodeInstruction ci, FieldInfo f) => f != null && ci.opcode == OpCodes.Stfld && Equals(ci.operand, f);
        static bool Calls(CodeInstruction ci, MethodInfo m) => m != null && (ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) && Equals(ci.operand, m);
        static bool IsR4(CodeInstruction ci, float v) => ci.opcode == OpCodes.Ldc_R4 && ci.operand is float f && f.Equals(v);

        static bool IsLocalOrArgLoad(CodeInstruction ci) =>
            ci.opcode == OpCodes.Ldloc_0 || ci.opcode == OpCodes.Ldloc_1 || ci.opcode == OpCodes.Ldloc_2 || ci.opcode == OpCodes.Ldloc_3
            || ci.opcode == OpCodes.Ldloc_S || ci.opcode == OpCodes.Ldloc;

        // Called every fixed step in front of the game's AI timer block; true skips that block.
        static bool AiStep(MonoUpdaters updaters, float fixedDeltaTime)
        {
            if (s_broken)
                return false;
            if (!ReferenceEquals(updaters, s_owner))
            {
                // Another session's MonoUpdaters (its m_updateAITimer starts at 0): nothing of the last session is owed
                // to this one's AIs. While the previous one is still alive (not in the game), this one runs the
                // game's block for itself.
                if (s_owner != null)
                    return false;
                s_owner = updaters;
                s_sched.Reset();
                s_subset.Clear();
                ResetFar();
            }
            float now = Time.unscaledTime;
            if (now >= s_nextStaggerCheck)
                CheckStaggerForeign(now);
            bool on = Stagger.On && !s_staggerForeign;
            if (on != s_lastOn)
            {
                s_lastOn = on;
                EnginePlugin.Log?.LogInfo(on ? "AiStagger: AI ticks staggered by phase." : "AiStagger: AI ticks as in the game.");
            }
            FarStep(now);
            ref float timer = ref s_timer(updaters);
            // The game's own decision for this step: m_updateAITimer += dt, then >= 0.05f on the stored float32.
            s_probe = timer + fixedDeltaTime;
            bool tick = s_probe >= Tick;
            List<IUpdateAI> instances = BaseAI.Instances;
            if (tick && instances.Count > MaxGamePass)
                MaxGamePass = instances.Count;
            int due;
            try
            {
                due = s_sched.Step(tick, on, instances, s_subset);
            }
            catch (Exception e)
            {
                // Our own bookkeeping failed (not an AI): the game's block runs from now on.
                s_broken = true;
                s_lastOn = false;
                s_subset.Clear();
                ResetFar();
                s_farOn = false;
                EnginePlugin.Log?.LogError("AiStagger: scheduler failed, AI ticks as in the game for the rest of the session: " + e);
                return false;
            }
            if (!on)
            {
                // Ticks still owed from the 'on' period, then the game's block (accumulator and its whole-list pass).
                if (due > 0)
                    Pass(updaters);
                if (!s_farPath)
                    return false;
                // Far AIs: the game's block run here (the same float arithmetic on the same field, the whole list at its
                // ticks, the decrement after the pass), so that the slow ones can be held back.
                timer = s_probe;
                if (tick)
                {
                    s_subset.AddRange(instances);
                    Pass(updaters);
                    timer -= Tick;
                }
                return true;
            }
            timer = s_probe;
            if (due > 0)
                Pass(updaters);
            // After the pass, as in the game: a pass that throws (an AI's exception, which goes on up as in the game)
            // leaves the tick in the accumulator, as there.
            if (tick)
                timer -= Tick;
            return true;
        }

        static void Pass(MonoUpdaters updaters)
        {
            Passes++;
            List<IUpdateAI> container = s_container(updaters);
            if (!s_farPath)
            {
                // The game's pass method with the game's container, scope and dt; only the source is the due subset.
                MonoUpdatersExtra.UpdateAI(container, s_subset, Scope, Tick);
                s_subset.Clear();
                return;
            }
            // Far AIs: the same pass for the AIs owed one tick (all but slow ones, in the subset's order), then one per
            // larger owed count with its dt. An AI's exception goes on up as in the game (the groups after it wait for
            // the next Split, which clears them).
            Split();
            MonoUpdatersExtra.UpdateAI(container, s_owed[1], Scope, Tick);
            s_owed[1].Clear();
            for (int k = 2; k <= MaxFarEvery; k++)
            {
                List<IUpdateAI> group = s_owed[k];
                if (group.Count == 0)
                    continue;
                FarPasses++;
                MonoUpdatersExtra.UpdateAI(container, group, Scope, k * Tick);
                group.Clear();
            }
        }

        // ---- far AIs --------------------------------------------------------------------------------------------

        // Once a fixed step, before the tick: the hook for this step, and whether the pass goes through Split.
        static void FarStep(float now)
        {
            Func<BaseAI, bool> hook = IsFarSlow;
            if (hook != null && ReferenceEquals(hook, s_farBroken) && now - s_farBrokenAt < FarHookRetrySeconds)
                hook = null;
            if (s_staggerForeign)
            {
                // Another mod on the pass: nothing of ours runs (owed ticks dropped), every AI as in the game.
                hook = null;
                if (s_far.Count > 0)
                    ResetFar();
            }
            s_farHook = hook;
            s_farPath = hook != null || s_far.Count > 0;
            if ((hook != null) != s_farOn)
            {
                s_farOn = hook != null;
                EnginePlugin.Log?.LogInfo(s_farOn ? $"AiStagger: far AIs (Real Portal) think every {FarEvery} ticks." : "AiStagger: far AIs think every tick.");
            }
            if (s_far.Count > 0 && s_sched.StepCount - s_farSweptAt >= FarSweepSteps)
                SweepFar();
        }

        // s_subset (the AIs receiving a tick now, in order) into s_owed by the ticks each thinks with now; slow AIs that
        // do not think now are left out.
        static void Split()
        {
            for (int k = 1; k <= MaxFarEvery; k++)
                s_owed[k].Clear();
            Func<BaseAI, bool> hook = s_farHook;
            int every = FarEvery < 1 ? 1 : FarEvery > MaxFarEvery ? MaxFarEvery : FarEvery;
            long step = s_sched.StepCount;
            List<IUpdateAI> subset = s_subset;
            for (int i = 0; i < subset.Count; i++)
            {
                IUpdateAI item = subset[i];
                int owed = 1;
                if (hook != null && item is BaseAI ai && AskFar(ref hook, ai))
                {
                    FarTicks++;
                    if (!s_far.TryGetValue(item, out FarEntry e))
                    {
                        // Phases in turn: AIs that turn slow together think in different ticks.
                        e = s_farPool.Count > 0 ? s_farPool.Pop() : new FarEntry();
                        s_farNext = (s_farNext + 1) % every;
                        e.Wait = s_farNext + 1;
                        e.Owed = 0;
                        s_far.Add(item, e);
                    }
                    e.Seen = step;
                    e.Owed++;
                    if (--e.Wait > 0)
                    {
                        FarSkipped++;
                        continue;
                    }
                    e.Wait = every;
                    owed = e.Owed;
                    e.Owed = 0;
                    FarThinks++;
                }
                else if (s_far.Count > 0 && item != null && s_far.TryGetValue(item, out FarEntry left))
                {
                    // Not slow any more: what it was owed comes with this tick, every tick after as in the game.
                    owed = left.Owed + 1;
                    s_far.Remove(item);
                    s_farPool.Push(left);
                    FarFlushes++;
                }
                s_owed[owed < 1 ? 1 : owed > MaxFarEvery ? MaxFarEvery : owed].Add(item);
            }
            subset.Clear();
        }

        // The hook's answer; a throw leaves it out (null) for the rest of the pass and FarHookRetrySeconds after.
        static bool AskFar(ref Func<BaseAI, bool> hook, BaseAI ai)
        {
            try
            {
                return hook(ai);
            }
            catch (Exception e)
            {
                FarHookErrors++;
                if (!ReferenceEquals(s_farBroken, hook))
                    EnginePlugin.Log?.LogWarning($"AiStagger: Real Portal's IsFarSlow threw, far AIs think every tick until it is replaced (asked again every {FarHookRetrySeconds:F0} s): {e}");
                s_farBroken = hook;
                s_farBrokenAt = Time.unscaledTime;
                hook = null;
                s_farHook = null;
                return false;
            }
        }

        // Slow AIs not seen for FarSweepSteps (gone from the list) are forgotten with what they were owed.
        static void SweepFar()
        {
            long step = s_sched.StepCount;
            s_farSweptAt = step;
            s_farStale.Clear();
            foreach (KeyValuePair<IUpdateAI, FarEntry> kv in s_far)
                if (step - kv.Value.Seen > FarSweepSteps)
                    s_farStale.Add(kv.Key);
            for (int i = 0; i < s_farStale.Count; i++)
            {
                FarEntry e = s_far[s_farStale[i]];
                FarDropped += e.Owed;
                s_far.Remove(s_farStale[i]);
                s_farPool.Push(e);
            }
            s_farStale.Clear();
        }

        // A new session, a foreign patch or a failure: no AI is owed anything, none is remembered.
        static void ResetFar()
        {
            foreach (KeyValuePair<IUpdateAI, FarEntry> kv in s_far)
            {
                FarDropped += kv.Value.Owed;
                s_farPool.Push(kv.Value);
            }
            s_far.Clear();
            for (int k = 1; k <= MaxFarEvery; k++)
                s_owed[k].Clear();
            s_farStale.Clear();
        }

        static List<IUpdateAI>[] NewGroups()
        {
            var groups = new List<IUpdateAI>[MaxFarEvery + 1];
            for (int k = 1; k <= MaxFarEvery; k++)
                groups[k] = new List<IUpdateAI>();
            return groups;
        }

        sealed class FarByReference : IEqualityComparer<IUpdateAI>
        {
            internal static readonly FarByReference Instance = new FarByReference();
            public bool Equals(IUpdateAI a, IUpdateAI b) => ReferenceEquals(a, b);
            public int GetHashCode(IUpdateAI o) => RuntimeHelpers.GetHashCode(o);
        }

        static void CheckStaggerForeign(float now)
        {
            s_nextStaggerCheck = now + ForeignCheckSeconds;
            bool foreign;
            string why = "";
            try
            {
                HarmonyLib.Patches fu = Harmony.GetPatchInfo(s_fixedUpdate);
                HarmonyLib.Patches ua = Harmony.GetPatchInfo(s_updateAI);
                foreign = fu != null && (Foreign(fu.Transpilers) || Foreign(fu.ILManipulators))
                          || ua != null && (Foreign(ua.Prefixes) || Foreign(ua.Postfixes) || Foreign(ua.Transpilers) || Foreign(ua.Finalizers) || Foreign(ua.ILManipulators));
                if (foreign)
                    why = "another mod patches MonoUpdaters.FixedUpdate (IL) or MonoUpdatersExtra.UpdateAI";
            }
            catch (Exception e)
            {
                // Cannot tell who else is there: behave as the game.
                foreign = true;
                why = "reading Harmony patch info failed (" + e.Message + ")";
            }
            if (foreign != s_staggerForeign)
                EnginePlugin.Log?.LogWarning(foreign ? "AiStagger: " + why + ", AI ticks as in the game." : "AiStagger: no foreign patches any more.");
            s_staggerForeign = foreign;
        }

        // ======================================================================================================
        // AiFriendScanSkip
        // ======================================================================================================

        static void InstallFriendSkip(Harmony harmony)
        {
            MethodInfo haveFriends = AccessTools.DeclaredMethod(typeof(BaseAI), "HaveFriendsInRange",
                new[] { typeof(float), typeof(Character).MakeByRefType(), typeof(Character).MakeByRefType() });
            MethodInfo friendScan = AccessTools.DeclaredMethod(typeof(BaseAI), "HaveFriendInRange", new[] { typeof(List<Character>), typeof(float) });
            MethodInfo hurtScan = AccessTools.DeclaredMethod(typeof(BaseAI), "HaveHurtFriendInRange", new[] { typeof(List<Character>), typeof(float) });
            MethodInfo equip = AccessTools.DeclaredMethod(typeof(Humanoid), nameof(Humanoid.EquipBestWeapon));
            s_selectBestAttack = AccessTools.DeclaredMethod(typeof(MonsterAI), "SelectBestAttack");
            if (haveFriends == null || friendScan == null || hurtScan == null || equip == null || s_selectBestAttack == null)
            {
                EnginePlugin.Log.LogWarning("AiFriendScanSkip: a target method is missing (game update?), left as in the game.");
                return;
            }
            ParameterInfo[] ps = s_selectBestAttack.GetParameters();
            if (ps.Length < 1 || ps[0].ParameterType != typeof(Humanoid))
            {
                EnginePlugin.Log.LogWarning("AiFriendScanSkip: MonsterAI.SelectBestAttack has another signature (game update?), left as in the game.");
                return;
            }
            s_inventory = AccessTools.FieldRefAccess<Humanoid, Inventory>("m_inventory");
            // An open delegate to the method itself, so the game's scan (with any patch on it) runs whenever it is made;
            // made before the transpiler goes in, so a failure leaves SelectBestAttack untouched.
            s_haveFriends = AccessTools.MethodDelegate<HaveFriendsFn>(haveFriends);
            s_friendWatch = new MethodBase[] { haveFriends, friendScan, hurtScan, equip };
            harmony.Patch(s_selectBestAttack, transpiler: new HarmonyMethod(typeof(AiStaggerModule), nameof(SelectBestAttackIL)) { priority = Priority.Last });
        }

        delegate void HaveFriendsFn(BaseAI ai, float range, out Character hurtFriend, out Character friend);
        static HaveFriendsFn s_haveFriends;

        static readonly MethodInfo s_haveFriendsCall = AccessTools.DeclaredMethod(typeof(BaseAI), "HaveFriendsInRange",
            new[] { typeof(float), typeof(Character).MakeByRefType(), typeof(Character).MakeByRefType() });
        static readonly MethodInfo s_friendScan = AccessTools.DeclaredMethod(typeof(AiStaggerModule), nameof(FriendScan));

        //   ...this, range, &hurtFriend, &friend; call BaseAI.HaveFriendsInRange
        // becomes
        //   ...this, range, &hurtFriend, &friend; ldarg.1; call FriendScan
        // (ldarg.1 is the humanoid that EquipBestWeapon is called on right after). Nothing else in the method changes.
        static IEnumerable<CodeInstruction> SelectBestAttackIL(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var code = new List<CodeInstruction>(instructions);
            int found = -1, matches = 0;
            for (int i = 0; i < code.Count; i++)
                if (Calls(code[i], s_haveFriendsCall))
                {
                    matches++;
                    found = i;
                }
            if (matches != 1 || !Plain(code[found]))
            {
                s_friendPatched = false;
                ReportOnce("friend", $"AiFriendScanSkip: MonsterAI.SelectBestAttack calls HaveFriendsInRange {matches} times or jumps onto the call (changed by another mod or an update?), left as in the game.", true);
                return code;
            }
            code[found].opcode = OpCodes.Call;
            code[found].operand = s_friendScan;
            code.Insert(found, new CodeInstruction(OpCodes.Ldarg_1));
            s_friendPatched = true;
            ReportOnce("friend", "AiFriendScanSkip: MonsterAI.SelectBestAttack friend scan routed.", false);
            return code;
        }

        // In place of this.HaveFriendsInRange(range, out hurtFriend, out friend): the game's scan, or null for both when
        // the results cannot be read (see the class comment).
        static void FriendScan(BaseAI ai, float range, out Character hurtFriend, out Character friend, Humanoid humanoid)
        {
            if (FriendSkip.On && humanoid != null)
            {
                float now = Time.unscaledTime;
                if (now >= s_nextFriendCheck)
                    CheckFriendForeign(now);
                if (!s_friendForeign && !NeedsFriends(humanoid))
                {
                    if (!Verify.On)
                    {
                        FriendScansSkipped++;
                        hurtFriend = null;
                        friend = null;
                        return;
                    }
                    FriendWouldSkip++;
                }
            }
            FriendScansRun++;
            s_haveFriends(ai, range, out hurtFriend, out friend);
        }

        // True when an item in the inventory EquipBestWeapon is about to walk targets friends (or the inventory cannot be
        // read the way it reads it), i.e. when the scan results may be read.
        static bool NeedsFriends(Humanoid humanoid)
        {
            try
            {
                return ItemTargetsFriends(humanoid);
            }
            catch (Exception)
            {
                return true;    // cannot tell: the game's scan runs
            }
        }

        static bool ItemTargetsFriends(Humanoid humanoid)
        {
            Inventory inventory = s_inventory(humanoid);
            if (inventory == null)
                return true;
            List<ItemDrop.ItemData> items = inventory.GetAllItems();
            if (items == null)
                return true;
            for (int i = 0; i < items.Count; i++)
            {
                ItemDrop.ItemData item = items[i];
                if (item?.m_shared == null)
                    return true;
                ItemDrop.ItemData.AiTarget target = item.m_shared.m_aiTargetType;
                if (target == ItemDrop.ItemData.AiTarget.Friend || target == ItemDrop.ItemData.AiTarget.FriendHurt)
                    return true;
            }
            return false;
        }

        static void CheckFriendForeign(float now)
        {
            s_nextFriendCheck = now + ForeignCheckSeconds;
            bool foreign = false;
            try
            {
                foreach (MethodBase m in s_friendWatch)
                {
                    HarmonyLib.Patches p = Harmony.GetPatchInfo(m);
                    if (p != null && (Foreign(p.Prefixes) || Foreign(p.Postfixes) || Foreign(p.Transpilers) || Foreign(p.Finalizers) || Foreign(p.ILManipulators)))
                        foreign = true;
                }
                HarmonyLib.Patches sel = Harmony.GetPatchInfo(s_selectBestAttack);
                if (sel != null && (Foreign(sel.Transpilers) || Foreign(sel.ILManipulators)))
                    foreign = true;
            }
            catch (Exception)
            {
                foreign = true;
            }
            if (foreign != s_friendForeign)
                EnginePlugin.Log?.LogWarning(foreign
                    ? "AiFriendScanSkip: another mod patches the friend scans, EquipBestWeapon or SelectBestAttack; scans run as in the game."
                    : "AiFriendScanSkip: no foreign patches any more.");
            s_friendForeign = foreign;
        }

        // ======================================================================================================

        static bool Foreign(IEnumerable<HarmonyLib.Patch> patches)
        {
            if (patches == null)
                return false;
            foreach (HarmonyLib.Patch p in patches)
                if (p.owner != EnginePlugin.Guid && (p.owner == null || !p.owner.StartsWith(LabOwners, StringComparison.Ordinal)))
                    return true;
            return false;
        }

        // Harmony reruns transpilers on every patch of the method: log a result only when it changes.
        static void ReportOnce(string key, string message, bool warning)
        {
            if (s_reported.TryGetValue(key, out string last) && last == message)
                return;
            s_reported[key] = message;
            if (warning)
                EnginePlugin.Log?.LogWarning(message);
            else
                EnginePlugin.Log?.LogInfo(message);
        }

        // ======================================================================================================
        // The phase scheduler. Plain C# (no Unity, no game types), so it can be checked outside the game.
        // ======================================================================================================

        // One Step per fixed step. A source tick recorded while 'on' is due for an item of phase k at step s + k; one
        // recorded while 'off' was given to all items by the game's own pass in that step and is never due here. Step
        // fills 'subset' with the items due now, in the order of 'items', each once (twice cannot happen: one source
        // per step and one phase per item). An item keeps its phase while it is seen at least once every SweepSteps steps.
        public sealed class Scheduler<T> where T : class
        {
            public const int Phases = 5;
            const int RingSize = 8;         // > Phases, power of two
            const int LookAhead = 4;        // removals tolerated between two items before falling back to the map
            const int SweepSteps = 512;
            const byte None = 0, OffSource = 1, OnSource = 2;

            sealed class Entry
            {
                internal int Phase;
                internal int PrevIndex = -1;
                internal long SeenStep = -1;
                internal long LastSeq = -1;     // Verify: source number of the last tick given while 'on'
                internal long Epoch = -1;
            }

            readonly byte[] m_src = new byte[RingSize];
            readonly long[] m_at = new long[RingSize];
            readonly long[] m_seq = new long[RingSize];
            readonly Dictionary<T, Entry> m_entries = new Dictionary<T, Entry>(ByReference.Instance);
            readonly int[] m_load = new int[Phases];
            readonly int[] m_count = new int[Phases];
            readonly List<T> m_stale = new List<T>();
            T[] m_prev = new T[64], m_cur = new T[64];
            Entry[] m_prevE = new Entry[64], m_curE = new Entry[64];
            int m_prevCount;
            long m_step, m_epoch, m_lastWalk, m_onSources;
            bool m_lastOn;

            bool m_verify;

            // Switching Verify on starts every item's chain over (a new epoch): the ticks given while it was off were
            // not numbered against LastSeq, so the first tick after it is not compared with the one before.
            public bool Verify
            {
                get => m_verify;
                set
                {
                    if (value && !m_verify)
                        m_epoch++;
                    m_verify = value;
                }
            }

            // Verify: (item, step, last source number given, this one) for every gap or repeat found.
            public Action<T, long, long, long> Mismatched;
            public long StepCount => m_step;
            public long Resets;
            public long Sources;
            public long Delivered;
            public long Walks;
            public long Created;
            public long VerifyChecks;
            public long VerifyMismatches;
            public int LastPass;
            public int MaxPass;
            public int Tracked => m_entries.Count;
            public int[] Load => (int[])m_load.Clone();

            // The item's phase (its delay in fixed steps), or -1 while it has none.
            public int PhaseOf(T item) => item != null && m_entries.TryGetValue(item, out Entry e) ? e.Phase : -1;

            // A new session (a new accumulator): nothing recorded so far is owed any more, every item is forgotten (a
            // new phase at its first walk) and no reference to the old session's items is kept. The step count and the
            // totals go on.
            public void Reset()
            {
                Array.Clear(m_src, 0, RingSize);
                Array.Clear(m_at, 0, RingSize);
                Array.Clear(m_seq, 0, RingSize);
                m_entries.Clear();
                Array.Clear(m_load, 0, Phases);
                Array.Clear(m_count, 0, Phases);
                m_stale.Clear();
                Array.Clear(m_prev, 0, m_prev.Length);
                Array.Clear(m_prevE, 0, m_prevE.Length);
                Array.Clear(m_cur, 0, m_cur.Length);
                Array.Clear(m_curE, 0, m_curE.Length);
                m_prevCount = 0;
                m_lastWalk = m_step;
                m_epoch++;
                LastPass = 0;
                Resets++;
            }

            // Records this step's source tick (decided by the game's accumulator) under the mode and returns how many
            // items are due now (in 'subset').
            public int Step(bool tick, bool on, List<T> items, List<T> subset)
            {
                long s = ++m_step;
                int slot = (int)(s & (RingSize - 1));
                m_at[slot] = s;
                m_src[slot] = tick ? (on ? OnSource : OffSource) : None;
                if (tick)
                {
                    Sources++;
                    // Verify numbers only the sources this scheduler hands out; 'off' ones go to everyone through the
                    // game's own pass in their step.
                    if (on)
                        m_seq[slot] = ++m_onSources;
                }
                if (on != m_lastOn)
                {
                    m_lastOn = on;
                    m_epoch++;
                }
                if (s % SweepSteps == 0)
                    Sweep(s);
                subset.Clear();
                int due = 0;
                for (int k = 0; k < Phases; k++)
                {
                    long t = s - k;
                    int i = (int)(t & (RingSize - 1));
                    if (t > 0 && m_at[i] == t && m_src[i] == OnSource)
                        due |= 1 << k;
                }
                if (due == 0)
                {
                    LastPass = 0;
                    return 0;
                }
                Walk(s, due, items, subset);
                LastPass = subset.Count;
                if (LastPass > MaxPass)
                    MaxPass = LastPass;
                Delivered += LastPass;
                return LastPass;
            }

            void Walk(long s, int due, List<T> items, List<T> subset)
            {
                Walks++;
                m_lastWalk = s;
                int n = items.Count;
                if (m_cur.Length < n)
                {
                    int size = Math.Max(n, m_cur.Length * 2);
                    m_cur = new T[size];
                    m_curE = new Entry[size];
                }
                Array.Clear(m_count, 0, Phases);
                int j = 0;
                for (int i = 0; i < n; i++)
                {
                    T item = items[i];
                    Entry e = null;
                    if (item != null)
                    {
                        // Fast path: the list only appends and removes in place (OnEnable/OnDisable), so this step's
                        // list is last step's with some items gone and some appended.
                        if (j < m_prevCount && ReferenceEquals(m_prev[j], item))
                            e = m_prevE[j++];
                        else
                        {
                            for (int k = j + 1; k < m_prevCount && k <= j + LookAhead; k++)
                                if (ReferenceEquals(m_prev[k], item))
                                {
                                    e = m_prevE[k];
                                    j = k + 1;
                                    break;
                                }
                            if (e == null)
                            {
                                if (m_entries.TryGetValue(item, out e))
                                {
                                    int p = e.PrevIndex;
                                    if (p >= j && p < m_prevCount && ReferenceEquals(m_prev[p], item))
                                        j = p + 1;
                                }
                                else
                                {
                                    e = new Entry { Phase = Lightest() };
                                    m_load[e.Phase]++;
                                    m_entries.Add(item, e);
                                    Created++;
                                }
                            }
                        }
                    }
                    // A null in the list goes to phase 0: the game's pass would throw on it at its tick, this one throws
                    // at the same rate.
                    int phase = e != null ? e.Phase : 0;
                    m_count[phase]++;
                    // Verify: not in the list (or not walked) last step, so a new stay: nothing to chain to.
                    if (Verify && e != null && e.SeenStep != s - 1 && e.SeenStep != s)
                        e.LastSeq = -1;
                    if ((due >> phase & 1) != 0)
                    {
                        subset.Add(item);
                        if (Verify && e != null)
                            Check(e, s, phase, item);
                    }
                    if (e != null)
                    {
                        e.PrevIndex = i;
                        e.SeenStep = s;
                    }
                    m_cur[i] = item;
                    m_curE[i] = e;
                }
                // The rest of last step's list is gone; keep the arrays for the next step.
                T[] t1 = m_prev;
                m_prev = m_cur;
                m_cur = t1;
                Entry[] t2 = m_prevE;
                m_prevE = m_curE;
                m_curE = t2;
                Array.Clear(m_cur, 0, Math.Min(m_cur.Length, m_prevCount));
                Array.Clear(m_curE, 0, Math.Min(m_curE.Length, m_prevCount));
                m_prevCount = n;
                Array.Copy(m_count, m_load, Phases);
            }

            // Consecutive ticks of an item walked every step since its last tick (same mode) must carry consecutive
            // numbers of the sources this scheduler handed out: none lost, none given twice.
            void Check(Entry e, long s, int phase, T item)
            {
                long t = s - phase;
                long seq = m_seq[(int)(t & (RingSize - 1))];
                bool continuous = e.Epoch == m_epoch && e.LastSeq >= 0;
                if (continuous)
                {
                    VerifyChecks++;
                    if (seq != e.LastSeq + 1)
                    {
                        VerifyMismatches++;
                        Mismatched?.Invoke(item, s, e.LastSeq, seq);
                    }
                }
                e.LastSeq = seq;
                e.Epoch = m_epoch;
            }

            int Lightest()
            {
                int best = 0;
                for (int k = 1; k < Phases; k++)
                    if (m_load[k] < m_load[best])
                        best = k;
                return best;
            }

            // Forgets items not seen for SweepSteps steps (gone from the list): they get a new phase if they come back.
            void Sweep(long s)
            {
                m_stale.Clear();
                foreach (KeyValuePair<T, Entry> kv in m_entries)
                    if (kv.Value.SeenStep < s - SweepSteps)
                        m_stale.Add(kv.Key);
                for (int i = 0; i < m_stale.Count; i++)
                    m_entries.Remove(m_stale[i]);
                m_stale.Clear();
                // No walk for that long (off): drop the last list too, so it does not keep destroyed AIs alive and a
                // later walk cannot pick up a forgotten entry from it.
                if (s - m_lastWalk > SweepSteps && m_prevCount > 0)
                {
                    Array.Clear(m_prev, 0, m_prevCount);
                    Array.Clear(m_prevE, 0, m_prevCount);
                    m_prevCount = 0;
                }
            }

            sealed class ByReference : IEqualityComparer<T>
            {
                internal static readonly ByReference Instance = new ByReference();
                public bool Equals(T a, T b) => ReferenceEquals(a, b);
                public int GetHashCode(T o) => RuntimeHelpers.GetHashCode(o);
            }
        }
    }
}
