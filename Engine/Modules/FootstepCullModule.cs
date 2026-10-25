using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace RealPortal.Engine
{
    // Creature footsteps and idle sounds made for nobody.
    //
    // A footstep effect (dust, splash, step sound) is never made directly: FootStep.OnFoot sends the routed RPC "Step"
    // to Everybody, and every machine that has the creature loaded instantiates the step prefabs in RPC_Step ->
    // DoEffect (Instantiate per prefab, two GetComponentsInChildren, and a purge that destroys all but the last 30).
    // Three things feed OnFoot:
    //  - the curve trigger (FootStep.UpdateFootstep): owner only, and only while the owner's camera is within the
    //    creature's m_footstepCullDistance (20 m by default). This is the game's own rule for "nobody would see it";
    //  - footless creatures (UpdateFootlessFootstep): owner only, any distance;
    //  - animation events (CharacterAnimEvent.FootStep): no owner test and no distance test at all, so every client
    //    that animates the creature broadcasts its own Step for the same step (N clients near a creature: every one of
    //    them makes N effects per step), and even alone in single-player every walking creature anywhere in the
    //    loaded area instantiates its steps.
    // BaseAI.DoIdleSound (InvokeRepeating every m_idleSoundInterval, 5 s) instantiates the idle sound of every loaded
    // creature on every machine, at any distance from the listener.
    //
    // What this changes (each part its own switch; with all of them off the game runs exactly as without this module):
    //  - FootstepOwnerOnly: an animation-event step of a creature this machine does not own is not broadcast while the
    //    owner's own Step RPCs for that creature are arriving here (one within OwnerStepWindow, 1.2 s): the owner's
    //    broadcast reaches everybody, this machine included, so each machine makes one effect per step instead of one
    //    per animating client. Evidence, not a guess: the owner's animator may be culled or LOD'd on its machine (an
    //    animator-LOD mod, CullCompletely, a dedicated server), its instance may be gone for the ~2 s an ownership
    //    hand-over takes (ZDOMan.ReleaseZDOS), and then it sends nothing; this machine then broadcasts its own step
    //    exactly as the game does. At worst one window's worth of steps (about 1 s) is missing when the owner goes
    //    quiet, and a step can be doubled when it starts again (as in the game). Without an owner, or in single-player
    //    and for the local player (always the owner), nothing changes.
    //  - FootstepCull: a Step that arrives here (from anybody, this machine included) instantiates nothing when the
    //    creature is farther from the game camera than the game's own footstep rule (m_footstepCullDistance, or the
    //    FootstepCullDistance setting) AND, for the sound in it, farther from the audio listener than the sound can be
    //    heard (see Measure) plus SoundMargin. Only steps whose prefabs are purely cosmetic (see Measure: renderers,
    //    particles, lights, sounds and the game's own effect helpers) are ever dropped; a step with anything else in
    //    it (a ZNetView, a collider, an unknown script) is always made. A CamShaker in a step widens the camera
    //    distance to its shake range. Alone (no peer connected) the owner also skips the send itself when every one of
    //    the creature's step effects would be dropped by that test, since this machine is the only receiver: the same
    //    answer with no RPC packet, ground-material lookup or routed-RPC garbage. In multiplayer the owner always
    //    sends (other players' cameras are elsewhere); receivers cull for themselves.
    //  - IdleSoundCull: BaseAI.DoIdleSound still tests IsSleeping and draws Random.value as the game does (a
    //    transpiler swaps only the EffectList.Create call); the Create is skipped when every prefab in the idle-sound
    //    list is a sound and nothing else (AudioSource, ZSFX, TimedDestruction, audio filters), and the creature is
    //    farther from the listener than those sounds reach plus SoundMargin.
    //
    // Exactness. What is still made is made exactly as before (same RPC, same arguments, same DoEffect). What is left
    // out was out of earshot and outside the game's own visibility rule for footsteps. Not identical:
    //  - the global UnityEngine.Random sequence: a skipped ZSFX would have drawn its delay, clip, pitch and volume
    //    (ZSFX.Awake/Play), and a skipped Create a Random.rotation for m_randomRotation entries. Vanilla's sequence is
    //    already an arbitrary, frame-timing-dependent interleaving of every effect and AI draw on this machine, and
    //    nothing replicated depends on the order (as for the AI stagger);
    //  - FootStep keeps its last 30 step effects alive and destroys older ones; far steps no longer push near ones out;
    //  - a sound that stays audible at any distance only through a rolloff floor (Logarithmic rolloff stops
    //    attenuating at maxDistance) is never dropped unless that floor is below FloorGain, 0.005 (-46 dB, the
    //    effects analysis' inaudibility threshold). Raise it only after an audible A/B in the lab.
    // Multiplayer: ZDOs and RPC formats are untouched; FootstepOwnerOnly only stops duplicate Step RPCs from
    // non-owners while the owner's arrive; FootstepCull and IdleSoundCull are local to this machine (a vanilla peer
    // still gets every Step the owner sends) and never drop a networked or non-cosmetic prefab.
    //
    // Real Portal: an effect by the far end of a live portal can be heard (PortalAudio) or seen (FarParticles, window
    // cameras) far from the game camera. KeepElsewhere (set by Real Portal) is asked before anything is culled; until
    // it is set, with Real Portal loaded, anything within reach of any portal in the game's portal list
    // (ZDOMan.m_portalObjects, by sector) is kept.
    //
    // Other mods: Path of Valheiman postfixes FootStep.OnFoot(Transform) (ChargeStep: the local player's charge wave).
    // The local player's steps are never touched here, and Harmony runs postfixes even when a prefix skips the body.
    // PoV's EffectList.Create prefix/postfix only scale or tint what Create returns. Prefixes here run last
    // (Priority.Last) and step aside when an earlier prefix already skipped the body. The idle transpiler stands down
    // (game's IL) when another mod transpiles BaseAI.DoIdleSound.
    [EngineModule]
    public static class FootstepCullModule
    {
        public static readonly Feature OwnerOnlyFeature = Features.Register("FootstepOwnerOnly", false,
            "Animation-event footsteps of creatures you do not own are left to their owner's broadcast while the owner's steps are arriving (the game makes one per animating client, duplicates in multiplayer); when the owner sends none you broadcast your own as the game does. No change in single-player.");
        public static readonly Feature CullFeature = Features.Register("FootstepCull", false,
            "Purely cosmetic footstep effects farther from the camera than the game's own footstep distance and out of earshot are not instantiated (alone, the step is not even sent).");
        public static readonly Feature IdleFeature = Features.Register("IdleSoundCull", false,
            "Creature idle sounds (every 5 s per creature) that are only a sound are not instantiated when the creature is out of their hearing distance; the game's random draw is kept.");
        public static readonly Feature VerifyFeature = Features.Register("FootstepCullVerify", false,
            "Lab: alone, steps the owner would not send are sent anyway and checked against the receiver's cull (FootstepCull's two halves must agree). Slower.");

        // Settings (section "Engine.Footsteps").
        // > 0: the camera distance beyond which a footstep is not shown, for every creature; 0: each creature's own
        // FootStep.m_footstepCullDistance (20 m by default), the distance the game uses for its own curve-driven steps.
        public static float StepDistance;
        // Added to every sound's hearing distance (the listener moves while the sound plays; ~ a few seconds of running)
        // and to a CamShaker's range.
        public static float SoundMargin = 10f;
        // A sound whose level never falls below this share of full volume at any distance (Logarithmic rolloff stops
        // attenuating at maxDistance; a Custom curve may end above 0) is treated as audible everywhere: never culled.
        // 0.005 is about -46 dB, the effects analysis' threshold (gain x maxVol < 0.005 is inaudible).
        public static float FloorGain = 0.005f;
        // FootstepOwnerOnly: the owner counts as sending steps for a creature while its last Step RPC is at most this old.
        public static float OwnerStepWindow = 1.2f;

        // Real Portal: true when an effect at this point within this radius can be seen or heard through a live portal.
        // Asked only for effects that would be culled. Main thread.
        public static Func<Vector3, float, bool> KeepElsewhere;

        // Lab counters (since start).
        public static long AnimSteps;            // animation-event steps of creatures this machine does not own (the game broadcasts each)
        public static long OwnerOnlySkipped;     // ... not broadcast: the owner's own steps are arriving
        public static long OwnerSilent;          // ... broadcast as in the game: no step from the owner lately, or no owner
        public static long OwnerStepsSeen;       // Step RPCs received from the owner of a creature this machine does not own
        public static long SendsSkipped;         // alone: steps not sent (every step effect of the creature culled)
        public static long StepsSeen;            // Step RPCs handled while FootstepCull is on
        public static long StepsCulled;          // ... nothing instantiated
        public static long StepsKeptAudible;     // ... beyond the camera distance but within hearing
        public static long StepsKeptElsewhere;   // ... kept for a portal (hook or fallback)
        public static long StepsKeptNotCosmetic; // ... kept: a prefab with a ZNetView or a component not known to be cosmetic
        public static long IdleCalls;            // idle-sound Creates the game asked for (after its own random draw)
        public static long IdleCulled;           // ... skipped
        public static long IdleKeptElsewhere;    // ... kept for a portal
        public static long VerifyChecks, VerifyMismatches, HookErrors;
        public static int PrefabsMeasured, PrefabsNotCosmetic;
        public static bool OwnerOnlyPatched, SendPatched, StepPatched, IdlePatched, IdleSwapped;
        public static string IdleStatus = "not patched";

        static AccessTools.FieldRef<CharacterAnimEvent, FootStep> s_caeFootStep;
        static AccessTools.FieldRef<FootStep, ZNetView> s_fsView;
        static AccessTools.FieldRef<FootStep, Character> s_fsCharacter;
        static AccessTools.FieldRef<ZDOMan, Dictionary<ZoneSystem.SectorIndex, List<ZDO>>> s_portals;
        static string s_harmonyId = EnginePlugin.Guid;
        const string RealPortalGuid = "valheim.realportal";

        static void Install(Harmony harmony)
        {
            s_harmonyId = harmony.Id;
            BindConfig();
            s_caeFootStep = AccessTools.FieldRefAccess<CharacterAnimEvent, FootStep>("m_footStep");
            s_fsView = AccessTools.FieldRefAccess<FootStep, ZNetView>("m_nview");
            s_fsCharacter = AccessTools.FieldRefAccess<FootStep, Character>("m_character");
            try
            {
                s_portals = AccessTools.FieldRefAccess<ZDOMan, Dictionary<ZoneSystem.SectorIndex, List<ZDO>>>("m_portalObjects");
            }
            catch (Exception e)
            {
                // Without the list, the Real Portal fallback keeps everything (see Elsewhere).
                EnginePlugin.Log.LogWarning("FootstepCull: ZDOMan.m_portalObjects not found (" + e.Message + "); with Real Portal and no KeepElsewhere hook nothing is culled.");
            }

            OwnerOnlyPatched = Patch(harmony, AccessTools.DeclaredMethod(typeof(CharacterAnimEvent), nameof(CharacterAnimEvent.FootStep), new[] { typeof(AnimationEvent) }),
                nameof(BeforeAnimStep), null);
            SendPatched = Patch(harmony, AccessTools.DeclaredMethod(typeof(FootStep), "OnFoot", new[] { typeof(Transform) }),
                nameof(BeforeOnFoot), nameof(AfterOnFoot));
            StepPatched = Patch(harmony, AccessTools.DeclaredMethod(typeof(FootStep), "RPC_Step", new[] { typeof(long), typeof(int), typeof(Vector3) }),
                nameof(BeforeRpcStep), null);
            try
            {
                MethodInfo idle = AccessTools.DeclaredMethod(typeof(BaseAI), "DoIdleSound");
                if (idle == null)
                    EnginePlugin.Log.LogWarning("FootstepCull: BaseAI.DoIdleSound not found, idle sounds left as they are.");
                else
                {
                    harmony.Patch(idle, transpiler: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(FootstepCullModule), nameof(IdleTranspiler))) { priority = Priority.Last });
                    IdlePatched = true;
                }
            }
            catch (Exception e)
            {
                EnginePlugin.Log.LogError("FootstepCull: BaseAI.DoIdleSound not patched: " + e);
            }

            // Measurements depend on FloorGain only; switching on starts them fresh (prefabs may have been changed by
            // mods between sessions of the switch).
            CullFeature.Switched += on => { if (on) ClearMeasured(); };
            IdleFeature.Switched += on => { if (on) ClearMeasured(); };
            // Stale owner evidence from before a switch-off must not suppress steps after it.
            OwnerOnlyFeature.Switched += _ => s_ownerStepAt.Clear();
        }

        static bool Patch(Harmony harmony, MethodInfo original, string prefix, string postfix)
        {
            if (original == null)
            {
                EnginePlugin.Log.LogWarning($"FootstepCull: a target for {prefix} not found, left as it is.");
                return false;
            }
            try
            {
                harmony.Patch(original,
                    prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(FootstepCullModule), prefix)) { priority = Priority.Last },
                    postfix: postfix == null ? null : new HarmonyMethod(AccessTools.DeclaredMethod(typeof(FootstepCullModule), postfix)) { priority = Priority.Last });
                return true;
            }
            catch (Exception e)
            {
                EnginePlugin.Log.LogError($"FootstepCull: {original.DeclaringType?.Name}.{original.Name} not patched: {e}");
                return false;
            }
        }

        // ---- settings ------------------------------------------------------------------------------------------------

        static void BindConfig()
        {
            // As BurstBootstrapModule: Install runs inside the plugin's Awake, when the plugin component is on the manager
            // object. Out of the game (test harness) the defaults above stay.
            ConfigFile config = null;
            try
            {
                config = Chainloader.ManagerObject != null ? Chainloader.ManagerObject.GetComponent<EnginePlugin>()?.Config : null;
            }
            catch
            {
                // no BepInEx chainloader: defaults
            }
            if (config == null)
                return;
            const string section = "Engine.Footsteps";
            Bind(config, section, "FootstepCullDistance", 0f, 0f, 500f,
                "Metres from the game camera beyond which a footstep effect is not shown (when also out of earshot). 0: each creature's own footstep distance (20 m by default), the one the game uses for its own curve-driven footsteps.",
                v => StepDistance = v);
            Bind(config, section, "SoundMargin", 10f, 0f, 100f,
                "Metres added to a sound's hearing distance (and to a camera shake's range) before a footstep or idle sound is dropped (you move while it plays).",
                v => SoundMargin = v);
            // Not 0.05 (-26 dB): that dropped far Logarithmic sounds the game still plays audibly (a 3/64 m source keeps
            // 4.7% of its volume at any distance).
            Bind(config, section, "SoundFloorGain", 0.005f, 0f, 1f,
                "A sound that never fades below this share of full volume (Logarithmic rolloff floor, or the end of a custom curve) counts as audible at any distance and is never dropped. 0.005 is about -46 dB. Higher drops more; check by ear first.",
                v => { FloorGain = v; ClearMeasured(); });
            Bind(config, section, "OwnerStepWindow", 1.2f, 0.2f, 10f,
                "FootstepOwnerOnly: seconds since the owner's last footstep within which your own copy of a creature's step is left to the owner. Longer: fewer duplicates for slow walkers, a longer gap when the owner stops sending.",
                v => OwnerStepWindow = v);
        }

        static void Bind(ConfigFile config, string section, string key, float def, float min, float max, string description, Action<float> set)
        {
            ConfigEntry<float> e = config.Bind(section, key, def, new ConfigDescription(description, new AcceptableValueRange<float>(min, max)));
            set(e.Value);
            e.SettingChanged += (_, __) => set(e.Value);
        }

        // ---- FootstepOwnerOnly -------------------------------------------------------------------------------------

        // When the owner's last Step RPC arrived, by FootStep instance id (unscaled time). Only creatures this machine
        // does not own are recorded.
        static readonly Dictionary<int, float> s_ownerStepAt = new Dictionary<int, float>();
        static readonly List<int> s_pruneKeys = new List<int>();
        static float s_pruneAt;

        // Prefix on CharacterAnimEvent.FootStep(AnimationEvent). The body runs as in the game unless this is a
        // non-owner's copy of a step the owner is broadcasting itself.
        static bool BeforeAnimStep(CharacterAnimEvent __instance, AnimationEvent e, bool __runOriginal)
        {
            if (!__runOriginal)
                return false;
            if (!OwnerOnlyFeature.On)
                return true;
            FootStep fs = s_caeFootStep(__instance);
            // The game's own early outs (no FootStep, a blended-out clip; its test is in double): its body does nothing then.
            if (!fs || e == null || (double)e.animatorClipInfo.weight < 0.33)
                return true;
            ZNetView view = s_fsView(fs);
            if ((object)view == null || !view.IsValid() || view.IsOwner())
                return true;
            AnimSteps++;
            ZDO zdo = view.GetZDO();
            // Whether the owner fires this event on its machine cannot be known from here (its animator may be culled
            // there, its instance gone during a hand-over); that it is sending steps for this creature can.
            if (zdo == null || zdo.GetOwner() == 0L || !OwnerSending(fs))
            {
                OwnerSilent++;
                return true;
            }
            OwnerOnlySkipped++;
            return false;
        }

        static bool OwnerSending(FootStep fs)
        {
            return s_ownerStepAt.TryGetValue(fs.GetInstanceID(), out float at) && Time.unscaledTime - at <= OwnerStepWindow;
        }

        // From RPC_Step: a step this machine receives from the creature's owner (not from itself, not from another
        // non-owner) is the evidence that the owner sends steps.
        static void NoteOwnerStep(FootStep fs, long sender)
        {
            ZNetView view = s_fsView(fs);
            if ((object)view == null || !view.IsValid() || view.IsOwner())
                return;
            ZDO zdo = view.GetZDO();
            if (zdo == null || sender == 0L || sender != zdo.GetOwner())
                return;
            float now = Time.unscaledTime;
            s_ownerStepAt[fs.GetInstanceID()] = now;
            OwnerStepsSeen++;
            if (s_ownerStepAt.Count > 256 && now >= s_pruneAt)
            {
                // Creatures that left or stopped walking: entries older than any window that matters.
                s_pruneAt = now + 10f;
                s_pruneKeys.Clear();
                foreach (KeyValuePair<int, float> kv in s_ownerStepAt)
                    if (now - kv.Value > OwnerStepWindow + 1f)
                        s_pruneKeys.Add(kv.Key);
                foreach (int k in s_pruneKeys)
                    s_ownerStepAt.Remove(k);
            }
        }

        // ---- FootstepCull: the sender (alone only) -------------------------------------------------------------------

        // Set while a step that the owner would have skipped is sent anyway (FootstepCullVerify); RPC_Step, which the
        // routed RPC calls synchronously for the local machine, checks it and clears it.
        static FootStep s_expect;

        // Prefix on FootStep.OnFoot(Transform): every footstep the owner makes (animation event, footless; the curve
        // trigger is already camera-gated by the game) passes here before its RPC.
        static bool BeforeOnFoot(FootStep __instance, bool __runOriginal)
        {
            if (!__runOriginal)
                return false;
            if (!CullFeature.On || !Alone() || IsLocalPlayer(__instance))
                return true;
            ZNetView view = s_fsView(__instance);
            if ((object)view == null || !view.IsValid())
                return true;
            // Which step effect the game would pick needs its ground and motion lookups; the step is skipped only when
            // every effect of this creature would be culled by RPC_Step's test, so the pick cannot matter.
            if (Judge(__instance, AllEffectsReach(__instance), false) != Verdict.Cull)
                return true;
            if (VerifyFeature.On)
            {
                s_expect = __instance;
                return true;
            }
            SendsSkipped++;
            return false;
        }

        static void AfterOnFoot()
        {
            // No effect matched (no RPC was sent), or it was handled: nothing is expected any more.
            s_expect = null;
        }

        static bool Alone()
        {
            ZNet znet = ZNet.instance;
            return znet != null && znet.GetPeers().Count == 0;
        }

        static bool IsLocalPlayer(FootStep fs)
        {
            Player local = Player.m_localPlayer;
            return (object)local != null && ReferenceEquals(s_fsCharacter(fs), local);
        }

        // ---- FootstepCull: the receiver ------------------------------------------------------------------------------

        // Prefix on FootStep.RPC_Step: DoEffect (the instantiation) only for a step someone can see or hear.
        static bool BeforeRpcStep(FootStep __instance, long sender, int effectIndex, bool __runOriginal)
        {
            bool expected = s_expect != null && ReferenceEquals(s_expect, __instance);
            if (expected)
                s_expect = null;
            // The owner sent it, whatever happens to it here (another mod may skip the body): it is sending steps.
            if (OwnerOnlyFeature.On && (object)__instance != null)
                NoteOwnerStep(__instance, sender);
            if (!__runOriginal)
                return false;
            if (!CullFeature.On || IsLocalPlayer(__instance))
                return true;
            List<FootStep.StepEffect> effects = __instance.m_effects;
            // A bad index throws in the game's body; leave that to it.
            if (effects == null || effectIndex < 0 || effectIndex >= effects.Count || effects[effectIndex] == null)
                return true;
            StepsSeen++;
            Verdict v = Judge(__instance, EffectReach(effects[effectIndex].m_effectPrefabs, default), true);
            if (expected)
            {
                VerifyChecks++;
                if (v != Verdict.Cull)
                {
                    VerifyMismatches++;
                    if (VerifyMismatches <= 20)
                        EnginePlugin.Log.LogWarning($"FootstepCullVerify: {__instance.name} step {effectIndex}: the sender would have skipped it but the receiver keeps it ({v}).");
                }
            }
            if (v != Verdict.Cull)
                return true;
            StepsCulled++;
            return false;
        }

        enum Verdict
        {
            Keep,
            KeepNotCosmetic,
            KeepAudible,
            KeepElsewhere,
            Cull
        }

        // What a step's prefabs do and how far: the farthest sound, the widest camera shake, and whether anything in
        // them is not known to be cosmetic.
        struct Reach
        {
            public float Sound;   // 0 = no sound, +inf = audible anywhere
            public float Shake;   // a CamShaker's range, 0 = none
            public bool Never;    // a ZNetView or a component not on the cosmetic list: never culled

            public Reach Max(PrefabInfo p) => new Reach
            {
                Sound = Mathf.Max(Sound, p.Reach),
                Shake = Mathf.Max(Shake, p.Shake),
                Never = Never || p.Networked || !p.Cosmetic
            };

            public Reach Max(Reach r) => new Reach
            {
                Sound = Mathf.Max(Sound, r.Sound),
                Shake = Mathf.Max(Shake, r.Shake),
                Never = Never || r.Never
            };
        }

        // The game's footstep rule (FootStep.UpdateFootstep: the creature's distance from the game camera against its
        // m_footstepCullDistance), widened by hearing and by camera shake: a step whose sound reaches the listener, or
        // whose shake reaches the camera, is kept.
        static Verdict Judge(FootStep fs, Reach reach, bool count)
        {
            if (reach.Never)
            {
                if (count)
                    StepsKeptNotCosmetic++;
                return Verdict.KeepNotCosmetic;
            }
            if (!Viewer(out Vector3 eye, out Vector3 ear))
                return Verdict.Keep;
            Vector3 pos = fs.transform.position;
            float limit = StepDistance > 0f ? StepDistance : fs.m_footstepCullDistance > 0f ? fs.m_footstepCullDistance : 20f;
            // GameCamera.AddShake shakes the game camera within the shaker's range of the effect.
            float see = reach.Shake > 0f ? Mathf.Max(limit, reach.Shake + SoundMargin) : limit;
            if ((pos - eye).sqrMagnitude <= see * see)
                return Verdict.Keep;
            float radius = limit;
            float soundReach = reach.Sound;
            if (soundReach > 0f)
            {
                if (float.IsPositiveInfinity(soundReach))
                {
                    if (count)
                        StepsKeptAudible++;
                    return Verdict.KeepAudible;
                }
                float hear = soundReach + SoundMargin;
                if ((pos - ear).sqrMagnitude <= hear * hear)
                {
                    if (count)
                        StepsKeptAudible++;
                    return Verdict.KeepAudible;
                }
                radius = Mathf.Max(radius, hear);
            }
            if (Elsewhere(pos, radius))
            {
                if (count)
                    StepsKeptElsewhere++;
                return Verdict.KeepElsewhere;
            }
            return Verdict.Cull;
        }

        static Reach EffectReach(GameObject[] prefabs, Reach reach)
        {
            if (prefabs == null)
                return reach;
            foreach (GameObject p in prefabs)
                reach = reach.Max(Measure(p));
            return reach;
        }

        static Reach AllEffectsReach(FootStep fs)
        {
            Reach reach = default;
            List<FootStep.StepEffect> effects = fs.m_effects;
            if (effects == null)
                return reach;
            for (int i = 0; i < effects.Count; i++)
            {
                FootStep.StepEffect se = effects[i];
                if (se != null)
                    reach = EffectReach(se.m_effectPrefabs, reach);
            }
            return reach;
        }

        // ---- IdleSoundCull ---------------------------------------------------------------------------------------------

        //   callvirt GameObject[] EffectList::Create(Vector3, Quaternion, Transform, float, int, ZDOID); pop
        // becomes
        //   call     GameObject[] FootstepCullModule::IdleCreate(EffectList, Vector3, Quaternion, Transform, float, int, ZDOID); pop
        // Same stack in, the result popped as before; the IsSleeping test and the Random.value draw stay the game's.
        static IEnumerable<CodeInstruction> IdleTranspiler(IEnumerable<CodeInstruction> code, MethodBase original)
        {
            var list = new List<CodeInstruction>(code);
            string foreign = ForeignTranspiler(original);
            if (foreign != null)
            {
                IdleSwapped = false;
                ReportIdle($"stood down: BaseAI.DoIdleSound is also transpiled by {foreign}");
                return list;
            }
            MethodInfo create = AccessTools.DeclaredMethod(typeof(EffectList), nameof(EffectList.Create));
            MethodInfo ours = AccessTools.DeclaredMethod(typeof(FootstepCullModule), nameof(IdleCreate));
            int n = 0, at = -1;
            for (int i = 0; i < list.Count; i++)
            {
                if (create != null && list[i].Calls(create))
                {
                    n++;
                    at = i;
                }
            }
            if (n == 1 && at + 1 < list.Count && list[at + 1].opcode == OpCodes.Pop)
            {
                list[at].opcode = OpCodes.Call;
                list[at].operand = ours;
                IdleSwapped = true;
                ReportIdle("EffectList.Create -> IdleCreate");
            }
            else
            {
                IdleSwapped = false;
                ReportIdle($"{n} EffectList.Create call(s) (the game has 1, result discarded); left as it is");
            }
            return list;
        }

        static void ReportIdle(string status)
        {
            // Harmony reruns every transpiler of a method whenever anyone patches it; log changes only.
            if (status == IdleStatus)
                return;
            IdleStatus = status;
            EnginePlugin.Log.LogInfo("FootstepCull: idle sounds: " + status);
        }

        // Another mod's transpiler on the method (ours and the engine's own excluded): the game's IL is kept then.
        static string ForeignTranspiler(MethodBase original)
        {
            try
            {
                Patches info = original == null ? null : Harmony.GetPatchInfo(original);
                if (info == null)
                    return null;
                foreach (Patch p in info.Transpilers)
                    if (p.owner != s_harmonyId && p.owner != EnginePlugin.Guid)
                        return p.owner;
            }
            catch
            {
                // patch info unavailable: treat as no foreign transpiler
            }
            return null;
        }

        public static GameObject[] IdleCreate(EffectList list, Vector3 basePos, Quaternion baseRot, Transform baseParent, float scale, int variant, ZDOID exclusive)
        {
            if (IdleFeature.On && list != null)
            {
                IdleCalls++;
                if (IdleInaudible(list, basePos))
                {
                    IdleCulled++;
                    return null;    // DoIdleSound pops the result
                }
            }
            return list.Create(basePos, baseRot, baseParent, scale, variant, exclusive);
        }

        // True only when every prefab the list would make is a 3D sound and nothing else, and the creature is beyond
        // the farthest of their hearing distances (plus SoundMargin) from the listener and from every portal.
        static bool IdleInaudible(EffectList list, Vector3 pos)
        {
            EffectList.EffectData[] data = list.m_effectPrefabs;
            if (data == null)
                return false;
            float reach = 0f;
            bool any = false;
            for (int i = 0; i < data.Length; i++)
            {
                EffectList.EffectData d = data[i];
                if (d == null || !d.m_enabled)
                    continue;     // Create skips it too (DoIdleSound passes variant -1)
                PrefabInfo info = Measure(d.m_prefab);
                if (!info.SoundOnly || info.Networked || info.Reach <= 0f || float.IsPositiveInfinity(info.Reach))
                    return false;
                any = true;
                if (info.Reach > reach)
                    reach = info.Reach;
            }
            if (!any || !Viewer(out _, out Vector3 ear))
                return false;
            float hear = reach + SoundMargin;
            if ((pos - ear).sqrMagnitude <= hear * hear)
                return false;
            if (Elsewhere(pos, hear))
            {
                IdleKeptElsewhere++;
                return false;
            }
            return true;
        }

        // ---- what a prefab is and how far it carries -------------------------------------------------------------------

        struct PrefabInfo
        {
            public float Reach;      // 0: no 3D sound; +inf: audible at any distance (or not measurable)
            public float Shake;      // CamShaker range: the game camera within it is shaken (0: none)
            public bool Cosmetic;    // every component is on the cosmetic list (seen, heard or shaken, nothing else)
            public bool SoundOnly;   // only a sound: AudioSource (and filters), ZSFX, TimedDestruction
            public bool Networked;   // a ZNetView: a world object, never culled here
        }

        static readonly PrefabInfo s_unknown = new PrefabInfo { Reach = float.PositiveInfinity };

        // By prefab instance id. Prefabs are assets that live for the session; the cache is cleared when FloorGain
        // changes or a switch comes on.
        static readonly Dictionary<int, PrefabInfo> s_measured = new Dictionary<int, PrefabInfo>();

        static void ClearMeasured()
        {
            s_measured.Clear();
            PrefabsMeasured = PrefabsNotCosmetic = 0;
        }

        // Every component of the prefab, children and inactive ones included, against a list of what an effect may
        // carry without doing anything but being seen or heard. What the list does not know (a gameplay script, a
        // collider, an Aoe, a missing script) makes the prefab non-cosmetic: never culled. The list is the effects
        // analysis' (ZSFX, TimedDestruction, LightFlicker, LightLod, CamShaker, ParticleDecal, EffectFade) plus Unity's
        // renderers, particles, lights and audio.
        static PrefabInfo Measure(GameObject prefab)
        {
            if (prefab == null)
                return s_unknown;
            int id = prefab.GetInstanceID();
            if (s_measured.TryGetValue(id, out PrefabInfo info))
                return info;
            try
            {
                info = new PrefabInfo { Cosmetic = true, SoundOnly = true };
                float reach = 0f;
                foreach (Component c in prefab.GetComponentsInChildren<Component>(true))
                {
                    switch (Classify(c))
                    {
                        case Kind.Neutral:
                            break;
                        case Kind.Sound:
                            reach = Mathf.Max(reach, SourceReach((AudioSource)c));
                            break;
                        case Kind.SoundHelper:
                            break;
                        case Kind.Visual:
                            info.SoundOnly = false;
                            break;
                        case Kind.Shake:
                            info.SoundOnly = false;
                            info.Shake = Mathf.Max(info.Shake, Mathf.Max(0f, ((CamShaker)c).m_range));
                            break;
                        case Kind.Networked:
                            info.Networked = true;
                            info.Cosmetic = false;
                            info.SoundOnly = false;
                            break;
                        default:
                            info.Cosmetic = false;
                            info.SoundOnly = false;
                            break;
                    }
                }
                info.Reach = reach;
            }
            catch (Exception e)
            {
                info = s_unknown;
                EnginePlugin.Log.LogWarning($"FootstepCull: {prefab.name} not measured, never culled: {e.Message}");
            }
            s_measured[id] = info;
            PrefabsMeasured = s_measured.Count;
            if (!info.Cosmetic)
                PrefabsNotCosmetic++;
            return info;
        }

        enum Kind
        {
            Neutral,       // Transform
            Sound,         // AudioSource
            SoundHelper,   // ZSFX, TimedDestruction, audio filters: part of a plain sound
            Visual,        // renderers, particles, lights and the game's visual effect helpers
            Shake,         // CamShaker
            Networked,     // ZNetView
            Other          // anything else, or a missing script
        }

        static Kind Classify(Component c)
        {
            if (c == null)
                return Kind.Other;   // a missing script: unknown
            if (c is Transform)
                return Kind.Neutral;
            if (c is AudioSource)
                return Kind.Sound;
            if (c is ZSFX || c is TimedDestruction || c is AudioLowPassFilter || c is AudioHighPassFilter
                || c is AudioReverbFilter || c is AudioEchoFilter || c is AudioDistortionFilter || c is AudioChorusFilter)
                return Kind.SoundHelper;
            if (c is Renderer || c is MeshFilter || c is ParticleSystem || c is Light || c is LODGroup
                || c is LightFlicker || c is LightLod || c is EffectFade || c is ParticleDecal)
                return Kind.Visual;
            if (c is CamShaker)
                return Kind.Shake;
            if (c is ZNetView)
                return Kind.Networked;
            return Kind.Other;
        }

        // The distance beyond which this source is silent or below FloorGain of full volume, +inf if there is none.
        // Unity's rolloffs: Linear reaches 0 at maxDistance; Logarithmic (minDistance / d) stops attenuating at
        // maxDistance and keeps minDistance / maxDistance from there on; Custom holds its curve's last value beyond
        // maxDistance. ZSFX plays at up to m_maxVol (its volume draw); without a ZSFX the source's own volume.
        static float SourceReach(AudioSource s)
        {
            if (s == null)
                return 0f;
            float maxD = s.maxDistance;
            if (s.spatialBlend < 0.99f || maxD <= 0f || float.IsInfinity(maxD) || float.IsNaN(maxD))
                return float.PositiveInfinity;
            ZSFX z = s.GetComponent<ZSFX>();
            float vol = z != null ? Mathf.Max(z.m_maxVol, z.m_minVol) : s.volume;
            switch (s.rolloffMode)
            {
                case AudioRolloffMode.Linear:
                    return maxD;
                case AudioRolloffMode.Logarithmic:
                    return s.minDistance / maxD * vol <= FloorGain ? maxD : float.PositiveInfinity;
                default:
                    AnimationCurve curve = s.GetCustomCurve(AudioSourceCurveType.CustomRolloff);
                    float end = curve != null && curve.length > 0 ? curve.Evaluate(1f) : 0f;
                    return end * vol <= FloorGain ? maxD : float.PositiveInfinity;
            }
        }

        // ---- where it is seen and heard from ---------------------------------------------------------------------------

        static int s_viewFrame = -1;
        static bool s_viewOk;
        static Vector3 s_eye, s_ear;

        // The game camera (the footstep rule's reference) and the active audio listener, once per frame.
        static bool Viewer(out Vector3 eye, out Vector3 ear)
        {
            int frame = Time.frameCount;
            if (frame != s_viewFrame)
            {
                s_viewFrame = frame;
                Camera cam = Utils.GetMainCamera();
                s_viewOk = cam != null;
                if (s_viewOk)
                {
                    s_eye = cam.transform.position;
                    AudioMan am = AudioMan.instance;
                    AudioListener listener = am != null ? am.GetActiveAudioListener() : null;
                    s_ear = listener != null ? listener.transform.position : s_eye;
                }
            }
            eye = s_eye;
            ear = s_ear;
            return s_viewOk;
        }

        static bool s_realPortal;
        static float s_realPortalCheckAt;
        static float s_hookErrorAt;

        // Kept for a portal: Real Portal's hook when set; otherwise, with Real Portal loaded, any portal within radius.
        static bool Elsewhere(Vector3 pos, float radius)
        {
            Func<Vector3, float, bool> hook = KeepElsewhere;
            if (hook != null)
            {
                try
                {
                    return hook(pos, radius);
                }
                catch (Exception e)
                {
                    HookErrors++;
                    if (Time.unscaledTime >= s_hookErrorAt)
                    {
                        s_hookErrorAt = Time.unscaledTime + 10f;
                        EnginePlugin.Log.LogError("FootstepCull: KeepElsewhere threw (kept): " + e);
                    }
                    return true;
                }
            }
            if (!RealPortalLoaded())
                return false;
            if (s_portals == null)
                return true;
            return NearPortal(pos, radius);
        }

        static bool RealPortalLoaded()
        {
            if (s_realPortal)
                return true;
            // Plugins after this one are added to the chainloader's list as they load: look again now and then.
            float now = Time.unscaledTime;
            if (now < s_realPortalCheckAt)
                return false;
            s_realPortalCheckAt = now + 10f;
            try
            {
                s_realPortal = Chainloader.PluginInfos.ContainsKey(RealPortalGuid);
            }
            catch
            {
                s_realPortal = false;
            }
            return s_realPortal;
        }

        // Any portal ZDO within radius (the game keeps them by sector, every portal of the world, loaded or not).
        static bool NearPortal(Vector3 pos, float radius)
        {
            ZDOMan man = ZDOMan.instance;
            Dictionary<ZoneSystem.SectorIndex, List<ZDO>> portals = man != null ? s_portals(man) : null;
            if (portals == null || portals.Count == 0)
                return false;
            float r2 = radius * radius;
            int x0 = Mathf.FloorToInt((pos.x - radius + 32f) / 64f), x1 = Mathf.FloorToInt((pos.x + radius + 32f) / 64f);
            int y0 = Mathf.FloorToInt((pos.z - radius + 32f) / 64f), y1 = Mathf.FloorToInt((pos.z + radius + 32f) / 64f);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    if (!portals.TryGetValue(ZoneSystem.SectorToIndex(x, y), out List<ZDO> list) || list == null)
                        continue;
                    for (int i = 0; i < list.Count; i++)
                    {
                        ZDO zdo = list[i];
                        if (zdo != null && (zdo.GetPosition() - pos).sqrMagnitude <= r2)
                            return true;
                    }
                }
            return false;
        }

        // Lab: one line for the log.
        public static string Report() =>
            $"FootstepCull: anim steps {AnimSteps} (owner-only skipped {OwnerOnlySkipped}, owner silent {OwnerSilent}; owner steps seen {OwnerStepsSeen}); sends skipped {SendsSkipped}; " +
            $"steps seen {StepsSeen}, culled {StepsCulled}, kept audible {StepsKeptAudible}, kept elsewhere {StepsKeptElsewhere}, kept not cosmetic {StepsKeptNotCosmetic}; " +
            $"idle {IdleCalls}, culled {IdleCulled}, kept elsewhere {IdleKeptElsewhere} ({IdleStatus}); prefabs measured {PrefabsMeasured} ({PrefabsNotCosmetic} not cosmetic); " +
            $"verify {VerifyChecks}/{VerifyMismatches} mismatches; hook errors {HookErrors}";
    }
}
