using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace RealPortal.Engine
{
    // HUD texts the game rebuilds every frame from numbers that rarely change (AllocHudText). Each is a new string per
    // frame per widget, so a few hundred bytes to a few KB per frame of garbage on a heap that the GC marks whole:
    //  - Hud.UpdateHealth/UpdateStamina/UpdateAdrenaline/UpdateEitr/UpdateMount: Mathf.CeilToInt(x).ToFastString(); the
    //    game's own table covers 0..256 only, so late-game health (300+) allocates every frame;
    //  - Hud.UpdateFood: CeilToInt(min).ToString() + "m" / FloorToInt(s).ToString() + "s" per food per frame;
    //  - Hud.UpdateGuardianPower and StatusEffect.GetIconText (each status effect icon, every frame): GetTimeString;
    //  - EnemyHud.UpdateHuds: the ridden mount's health and stamina, CeilToInt(x).ToString() per frame;
    //  - InventoryGrid.UpdateGui (every frame while the inventory or a container is open): $"{stack}/{max}" with two
    //    boxed ints and m_quality.ToString() per slot.
    // Each becomes a lookup of a string built once, the game's way, for that value. The text setters receive a string
    // with the same characters (TMP_Text.text compares content and does nothing when it is equal), so the HUD is the
    // same; only identity differs (the same instance every frame instead of a fresh one), which nothing compares.
    // Left alone, and why:
    //  - HotkeyBar.UpdateIcons: already formats "{stack} / {max}" only when the stack changed (ElementData.m_stackText);
    //    its per-frame List.Sort(Comparison) wrapper is not a string;
    //  - EnemyHud's and Hud.UpdateStatusEffects' Localize(name): Localization keeps an LRU of 100 translations, so a
    //    repeated name is a cache hit without allocation;
    //  - Hud.UpdateMount's name line, SE_Cozy's comfort text and the item tooltips: built from several variable strings.
    //
    // Exactness: every cached value is a pure function of the key. int.ToString() of a non-negative int is its decimal
    // digits in every culture (only the negative sign comes from NumberFormatInfo), and so is "{0}/{1}" of two
    // non-negative ints; negative values go to the game's code every time. GetTimeString(t, sufix, always) for t > 0
    // depends only on CeilToInt(t) and the two flags (the minutes, seconds and the "00" format are computed from that
    // integer), so its text is kept per (integer, flags); t <= 0 or NaN, and values beyond the tables, go to the game's
    // code. If another mod patches GetTimeString, every call goes to it (checked now and then). Patches are single-call
    // swaps at Priority.Last; a method another mod transpiles is left as it is. AllocVerify (lab) computes the game's
    // string next to each cached one and counts differences.
    //
    // Which sites run depends on the HUD mod. With SeneaL-UI (the user's profile) and its elements on - the default -
    // SeneaLUI.Patches.HudPatches has bool prefixes 'return !Prefs.Vitals.Active' on Hud.UpdateHealth, UpdateFood and
    // UpdateGuardianPower and 'return !Prefs.Stamina.Active' on UpdateStamina and UpdateEitr, so the game's bodies (and
    // the 6 sites swapped in them) never execute: the late-game health text gain above does not happen there, SeneaL
    // draws those bars itself. Live with SeneaL-UI: StatusEffect.GetIconText (SeneaL's StatusEffectsView calls it per
    // icon), Hud.UpdateAdrenaline and UpdateMount, EnemyHud.UpdateHuds (mount texts) and InventoryGrid.UpdateGui. A lab
    // A/B of AllocHudText in that profile should be budgeted on those; the per-site counters below (*Texts: texts
    // requested at that site, counted with the switch on or off) show which sites actually run.
    [EngineModule]
    public static class UiAllocModule
    {
        static readonly Feature s_feature = Features.Register("AllocHudText", true,
            "HUD numbers (health, stamina, eitr, food timers, status effect timers, mount bars, inventory stack and quality counts) reuse strings built once per value.");
        static readonly Feature s_verify = Features.Register("AllocVerify", false,
            "Lab: also runs the game's own call next to each Alloc* replacement, uses its answer and logs any difference. Slower.");

        // Lab counters: texts answered from the tables, texts built the game's way (first use, out of range, negative),
        // and verify comparisons / differences (acceptance: VerifyMismatches == 0).
        public static long Cached, Built, VerifyChecks, VerifyMismatches;
        // Call sites swapped, and edits left as they are (maintained by CrowdAllocModule.Il.Report).
        public static int SitesPatched, SitesLeft;
        // Another mod patches StatusEffect.GetTimeString: time texts are not cached.
        public static bool TimeStringPatched;
        // Texts requested per call site (switch on or off): 0 means that site's game body does not run in this profile
        // (e.g. Health/Stamina/Eitr/Food/GuardianPower while SeneaL-UI replaces those HUD elements).
        public static long HealthTexts, StaminaTexts, AdrenalineTexts, EitrTexts, MountTexts, FoodTexts, GuardianPowerTexts,
                           IconTexts, EnemyHudTexts, InventoryTexts;

        static readonly MethodInfo s_toFastString = AccessTools.Method(typeof(StringExtensionMethods), nameof(StringExtensionMethods.ToFastString), new[] { typeof(int) });
        static readonly MethodInfo s_intToString = AccessTools.Method(typeof(int), nameof(int.ToString), Type.EmptyTypes);
        static readonly MethodInfo s_concat2 = AccessTools.Method(typeof(string), nameof(string.Concat), new[] { typeof(string), typeof(string) });
        static readonly MethodInfo s_format2 = AccessTools.Method(typeof(string), nameof(string.Format), new[] { typeof(string), typeof(object), typeof(object) });
        static readonly MethodInfo s_getTimeString = AccessTools.Method(typeof(StatusEffect), nameof(StatusEffect.GetTimeString), new[] { typeof(float), typeof(bool), typeof(bool) });

        static MethodInfo Ours(string name) => AccessTools.DeclaredMethod(typeof(UiAllocModule), name);

        static void Install(Harmony harmony)
        {
            // Each group is registered only when the game methods it swaps were found (CodeInstruction.Calls throws on
            // null, and a throwing transpiler would stay registered; see CrowdAllocModule's header).
            var toFastString = ("StringExtensionMethods.ToFastString(int)", s_toFastString);
            var intToString = ("int.ToString()", s_intToString);
            var getTimeString = ("StatusEffect.GetTimeString(float, bool, bool)", s_getTimeString);
            if (CrowdAllocModule.Il.Ready("UiAlloc", "AllocHudText (Hud bars)", toFastString))
                foreach (string m in new[] { "UpdateHealth", "UpdateStamina", "UpdateAdrenaline", "UpdateEitr", "UpdateMount" })
                    CrowdAllocModule.Il.Transpile(harmony, "UiAlloc", typeof(Hud), m, Ours(nameof(FastStringSites)));
            if (CrowdAllocModule.Il.Ready("UiAlloc", "AllocHudText (food timers)", intToString, ("string.Concat(string, string)", s_concat2)))
                CrowdAllocModule.Il.Transpile(harmony, "UiAlloc", typeof(Hud), "UpdateFood", Ours(nameof(FoodSites)));
            if (CrowdAllocModule.Il.Ready("UiAlloc", "AllocHudText (time texts)", getTimeString))
            {
                CrowdAllocModule.Il.Transpile(harmony, "UiAlloc", typeof(Hud), "UpdateGuardianPower", Ours(nameof(TimeSites)));
                CrowdAllocModule.Il.Transpile(harmony, "UiAlloc", typeof(StatusEffect), "GetIconText", Ours(nameof(TimeSites)));
            }
            if (CrowdAllocModule.Il.Ready("UiAlloc", "AllocHudText (enemy HUD)", intToString))
                CrowdAllocModule.Il.Transpile(harmony, "UiAlloc", typeof(EnemyHud), "UpdateHuds", Ours(nameof(EnemyHudSites)));
            if (CrowdAllocModule.Il.Ready("UiAlloc", "AllocHudText (inventory)", intToString, ("string.Format(string, object, object)", s_format2)))
                CrowdAllocModule.Il.Transpile(harmony, "UiAlloc", typeof(InventoryGrid), "UpdateGui", Ours(nameof(InventorySites)));
            s_feature.Switched += on => { if (on) s_timeCheckIn = 0; };
        }

        static void Mismatch(string site, string ours, string game)
        {
            VerifyMismatches++;
            if (VerifyMismatches <= 50)
                EnginePlugin.Log.LogWarning($"AllocVerify: {site}: cached '{ours}' vs the game's '{game}'");
        }

        // ---- Transpilers --------------------------------------------------------------------------------------------

        // Each site calls a thin wrapper that counts its runs (the *Texts counters) and then the shared replacement.
        static IEnumerable<CodeInstruction> FastStringSites(IEnumerable<CodeInstruction> code, MethodBase original) =>
            CrowdAllocModule.Il.SwapAll(code, original, "UiAlloc", "ToFastString -> cached", ci => ci.Calls(s_toFastString),
                                        _ => Ours(FastTextFor(original)), original?.Name == "UpdateMount" ? 2 : 1);

        static string FastTextFor(MethodBase original)
        {
            switch (original?.Name)
            {
                case "UpdateHealth": return nameof(FastTextHealth);
                case "UpdateStamina": return nameof(FastTextStamina);
                case "UpdateAdrenaline": return nameof(FastTextAdrenaline);
                case "UpdateEitr": return nameof(FastTextEitr);
                case "UpdateMount": return nameof(FastTextMount);
                default: return nameof(FastText);
            }
        }

        static IEnumerable<CodeInstruction> TimeSites(IEnumerable<CodeInstruction> code, MethodBase original) =>
            CrowdAllocModule.Il.SwapAll(code, original, "UiAlloc", "GetTimeString -> cached", ci => ci.Calls(s_getTimeString),
                                        _ => Ours(original?.DeclaringType == typeof(StatusEffect) ? nameof(TimeTextIcon) : nameof(TimeTextGuardianPower)), 1);

        static IEnumerable<CodeInstruction> EnemyHudSites(IEnumerable<CodeInstruction> code, MethodBase original) =>
            CrowdAllocModule.Il.SwapAll(code, original, "UiAlloc", "int.ToString -> cached", ci => ci.Calls(s_intToString),
                                        _ => Ours(nameof(IntTextEnemyHud)), 2);

        //   ldloca v; call int.ToString(); ldstr suffix; call string.Concat(string, string)
        // becomes
        //   ldloca v; nop;                 ldstr suffix; call IntSuffixText(ref int, string)
        static IEnumerable<CodeInstruction> FoodSites(IEnumerable<CodeInstruction> code, MethodBase original) =>
            CrowdAllocModule.Il.Guard(code, original, "UiAlloc", list => FoodSitesIn(list, original));

        static List<CodeInstruction> FoodSitesIn(List<CodeInstruction> list, MethodBase original)
        {
            string foreign = CrowdAllocModule.Il.ForeignTranspiler(original);
            if (foreign != null)
            {
                CrowdAllocModule.Il.Report("UiAlloc", original, $"left as it is: transpiled by {foreign}", -1);
                return list;
            }
            var found = new List<int>();
            for (int i = 0; (i = CrowdAllocModule.Il.Find(list, i,
                                 ci => ci.opcode == OpCodes.Ldloca_S || ci.opcode == OpCodes.Ldloca,
                                 ci => ci.Calls(s_intToString) && CrowdAllocModule.Il.Unlabeled(ci),
                                 ci => ci.opcode == OpCodes.Ldstr && ci.operand is string && CrowdAllocModule.Il.Unlabeled(ci),
                                 ci => ci.Calls(s_concat2) && CrowdAllocModule.Il.Unlabeled(ci))) >= 0; i += 4)
                found.Add(i);
            if (found.Count == 2)
            {
                foreach (int i in found)
                {
                    CrowdAllocModule.Il.Set(list[i + 1], OpCodes.Nop, null);
                    CrowdAllocModule.Il.Set(list[i + 3], OpCodes.Call, Ours(nameof(IntSuffixText)));
                }
            }
            CrowdAllocModule.Il.Report("UiAlloc", original, "int.ToString() + suffix -> cached", found.Count, 2);
            return list;
        }

        // m_quality.ToString() and the slot number (i + 1).ToString() become IntText; the stack text
        //   ldstr "{0}/{1}"; ldloc item; ldfld m_stack; box int; ldloc item; ldfld m_shared; ldfld m_maxStackSize; box int;
        //   call string.Format(string, object, object)
        // becomes the same without the two boxes, calling StackText(string, int, int).
        static IEnumerable<CodeInstruction> InventorySites(IEnumerable<CodeInstruction> code, MethodBase original) =>
            CrowdAllocModule.Il.Guard(code, original, "UiAlloc", list => InventorySitesIn(list, original));

        static List<CodeInstruction> InventorySitesIn(List<CodeInstruction> list, MethodBase original)
        {
            string foreign = CrowdAllocModule.Il.ForeignTranspiler(original);
            if (foreign != null)
            {
                CrowdAllocModule.Il.Report("UiAlloc", original, $"left as it is: transpiled by {foreign}", -1);
                return list;
            }
            CrowdAllocModule.Il.SwapGroup(list, original, "UiAlloc", "int.ToString -> cached", ci => ci.Calls(s_intToString), _ => Ours(nameof(IntTextInventory)), 2);
            var found = new List<int>();
            for (int i = 0; (i = CrowdAllocModule.Il.Find(list, i,
                                 ci => ci.opcode == OpCodes.Ldstr && ci.operand is string s && s == StackFormat,
                                 ci => CrowdAllocModule.Il.IsValueLoad(ci) && CrowdAllocModule.Il.Unlabeled(ci),
                                 ci => ci.opcode == OpCodes.Ldfld && CrowdAllocModule.Il.Unlabeled(ci),
                                 ci => IsBoxInt(ci) && CrowdAllocModule.Il.Unlabeled(ci),
                                 ci => CrowdAllocModule.Il.IsValueLoad(ci) && CrowdAllocModule.Il.Unlabeled(ci),
                                 ci => ci.opcode == OpCodes.Ldfld && CrowdAllocModule.Il.Unlabeled(ci),
                                 ci => ci.opcode == OpCodes.Ldfld && CrowdAllocModule.Il.Unlabeled(ci),
                                 ci => IsBoxInt(ci) && CrowdAllocModule.Il.Unlabeled(ci),
                                 ci => ci.Calls(s_format2) && CrowdAllocModule.Il.Unlabeled(ci))) >= 0; i += 9)
                found.Add(i);
            if (found.Count == 1)
            {
                int i = found[0];
                CrowdAllocModule.Il.Set(list[i + 3], OpCodes.Nop, null);
                CrowdAllocModule.Il.Set(list[i + 7], OpCodes.Nop, null);
                CrowdAllocModule.Il.Set(list[i + 8], OpCodes.Call, Ours(nameof(StackText)));
            }
            CrowdAllocModule.Il.Report("UiAlloc", original, "\"{0}/{1}\" stack text -> cached", found.Count, 1);
            return list;
        }

        static bool IsBoxInt(CodeInstruction ci) => ci.opcode == OpCodes.Box && ci.operand is Type t && t == typeof(int);

        // ---- Integer texts ------------------------------------------------------------------------------------------

        // Decimal texts of 0..MaxInt-1, filled on first use (the table grows by doubling, at most 512 KB of references).
        const int MaxInt = 1 << 16;
        static string[] s_ints = new string[512];

        static string CachedInt(int value)
        {
            if (value >= s_ints.Length)
            {
                int size = s_ints.Length;
                while (size <= value)
                    size *= 2;
                Array.Resize(ref s_ints, Math.Min(size, MaxInt));
            }
            string text = s_ints[value];
            if (text == null)
            {
                Built++;
                return s_ints[value] = value.ToString();
            }
            Cached++;
            return text;
        }

        // In place of int.ToString() on a local or field (the call takes its address).
        static string IntText(ref int value)
        {
            int v = value;
            if (!s_feature.On || (uint)v >= MaxInt)
                return v.ToString();
            string text = CachedInt(v);
            if (s_verify.On)
                return Verify("int.ToString", text, v.ToString());
            return text;
        }

        // Per-site entry points (counted, then the shared replacement).
        static string IntTextEnemyHud(ref int value)
        {
            EnemyHudTexts++;
            return IntText(ref value);
        }

        static string IntTextInventory(ref int value)
        {
            InventoryTexts++;
            return IntText(ref value);
        }

        static string FastTextHealth(int value)
        {
            HealthTexts++;
            return FastText(value);
        }

        static string FastTextStamina(int value)
        {
            StaminaTexts++;
            return FastText(value);
        }

        static string FastTextAdrenaline(int value)
        {
            AdrenalineTexts++;
            return FastText(value);
        }

        static string FastTextEitr(int value)
        {
            EitrTexts++;
            return FastText(value);
        }

        static string FastTextMount(int value)
        {
            MountTexts++;
            return FastText(value);
        }

        // In place of StringExtensionMethods.ToFastString(int): its 0..256 table, else int.ToString().
        static string FastText(int value)
        {
            if (!s_feature.On || (uint)value >= MaxInt)
                return value.ToFastString();
            string text = CachedInt(value);
            if (s_verify.On)
                return Verify("ToFastString", text, value.ToFastString());
            return text;
        }

        // value.ToString() + suffix, kept per suffix (the game's suffixes are the literals "m" and "s").
        sealed class SuffixTable
        {
            internal readonly string Suffix;
            internal string[] Texts = new string[64];
            internal SuffixTable(string suffix) => Suffix = suffix;
        }

        static SuffixTable[] s_suffixes = new SuffixTable[0];
        const int MaxSuffixed = 4096;

        static string IntSuffixText(ref int value, string suffix)
        {
            FoodTexts++;   // Hud.UpdateFood is its only site
            int v = value;
            if (!s_feature.On || (uint)v >= MaxSuffixed || suffix == null)
                return v.ToString() + suffix;
            SuffixTable table = SuffixFor(suffix);
            if (v >= table.Texts.Length)
            {
                int size = table.Texts.Length;
                while (size <= v)
                    size *= 2;
                Array.Resize(ref table.Texts, Math.Min(size, MaxSuffixed));
            }
            string text = table.Texts[v];
            if (text == null)
            {
                Built++;
                text = table.Texts[v] = v.ToString() + suffix;
            }
            else
            {
                Cached++;
            }
            if (s_verify.On)
                return Verify("int.ToString() + suffix", text, v.ToString() + suffix);
            return text;
        }

        static SuffixTable SuffixFor(string suffix)
        {
            SuffixTable[] tables = s_suffixes;
            // The suffix comes from an ldstr, so it is the same interned instance every call.
            for (int i = 0; i < tables.Length; i++)
                if ((object)tables[i].Suffix == suffix)
                    return tables[i];
            for (int i = 0; i < tables.Length; i++)
                if (tables[i].Suffix == suffix)
                    return tables[i];
            var created = new SuffixTable(suffix);
            var grown = new SuffixTable[tables.Length + 1];
            Array.Copy(tables, grown, tables.Length);
            grown[tables.Length] = created;
            s_suffixes = grown;
            return created;
        }

        // ---- Inventory stack text -----------------------------------------------------------------------------------

        const string StackFormat = "{0}/{1}";
        const int MaxStackTexts = 4096;
        static readonly Dictionary<long, string> s_stackTexts = new Dictionary<long, string>(256);

        static string StackText(string format, int stack, int max)
        {
            InventoryTexts++;   // InventoryGrid.UpdateGui is its only site
            if (!s_feature.On || stack < 0 || max < 0 || format == null || ((object)format != StackFormat && format != StackFormat))
                return string.Format(format, stack, max);
            long key = ((long)stack << 32) | (uint)max;
            if (!s_stackTexts.TryGetValue(key, out string text))
            {
                Built++;
                text = string.Format(format, stack, max);
                if (s_stackTexts.Count >= MaxStackTexts)
                    s_stackTexts.Clear();
                s_stackTexts.Add(key, text);
            }
            else
            {
                Cached++;
            }
            if (s_verify.On)
                return Verify("inventory stack text", text, string.Format(format, stack, max));
            return text;
        }

        // ---- Time texts ---------------------------------------------------------------------------------------------

        // Per (sufix, alwaysShowMinutes): the text for each whole second 1..MaxSeconds-1, filled on first use.
        const int MaxSeconds = 7200;
        static readonly string[][] s_times = new string[4][];
        static int s_timeCheckIn;

        static string TimeTextGuardianPower(float time, bool sufix, bool alwaysShowMinutes)
        {
            GuardianPowerTexts++;
            return TimeText(time, sufix, alwaysShowMinutes);
        }

        static string TimeTextIcon(float time, bool sufix, bool alwaysShowMinutes)
        {
            IconTexts++;
            return TimeText(time, sufix, alwaysShowMinutes);
        }

        static string TimeText(float time, bool sufix, bool alwaysShowMinutes)
        {
            if (!s_feature.On || !(time > 0f) || TimeStringPatchedNow())
                return StatusEffect.GetTimeString(time, sufix, alwaysShowMinutes);
            int seconds = Mathf.CeilToInt(time);
            if (seconds < 1 || seconds >= MaxSeconds)
                return StatusEffect.GetTimeString(time, sufix, alwaysShowMinutes);
            int slot = (sufix ? 2 : 0) | (alwaysShowMinutes ? 1 : 0);
            string[] table = s_times[slot];
            if (table == null || seconds >= table.Length)
            {
                int size = table?.Length ?? 64;
                while (size <= seconds)
                    size *= 2;
                size = Math.Min(size, MaxSeconds);
                if (table == null)
                    table = new string[size];
                else
                    Array.Resize(ref table, size);
                s_times[slot] = table;
            }
            string text = table[seconds];
            if (text == null)
            {
                Built++;
                text = table[seconds] = StatusEffect.GetTimeString(time, sufix, alwaysShowMinutes);
            }
            else
            {
                Cached++;
            }
            if (s_verify.On)
                return Verify("StatusEffect.GetTimeString", text, StatusEffect.GetTimeString(time, sufix, alwaysShowMinutes));
            return text;
        }

        // A patch of another mod could make the text depend on more than the whole seconds. Checked on the first call
        // and then every 600 calls (Harmony.GetPatchInfo is a dictionary lookup while nothing patches the method).
        static bool TimeStringPatchedNow()
        {
            if (--s_timeCheckIn > 0)
                return TimeStringPatched;
            s_timeCheckIn = 600;
            bool patched = false;
            Patches info = s_getTimeString == null ? null : Harmony.GetPatchInfo(s_getTimeString);
            if (info != null)
            {
                foreach (Patch p in info.Prefixes)
                    patched |= !CrowdAllocModule.Il.Ours(p.owner);
                foreach (Patch p in info.Postfixes)
                    patched |= !CrowdAllocModule.Il.Ours(p.owner);
                foreach (Patch p in info.Transpilers)
                    patched |= !CrowdAllocModule.Il.Ours(p.owner);
                foreach (Patch p in info.Finalizers)
                    patched |= !CrowdAllocModule.Il.Ours(p.owner);
                foreach (Patch p in info.ILManipulators)
                    patched |= !CrowdAllocModule.Il.Ours(p.owner);
            }
            if (patched != TimeStringPatched)
                EnginePlugin.Log.LogInfo(patched
                    ? "AllocHudText: StatusEffect.GetTimeString is patched by another mod; time texts are not cached."
                    : "AllocHudText: StatusEffect.GetTimeString unpatched; time texts are cached.");
            TimeStringPatched = patched;
            return patched;
        }

        static string Verify(string site, string ours, string game)
        {
            VerifyChecks++;
            if (string.Equals(ours, game))
                return ours;
            Mismatch(site, ours, game);
            return game;
        }
    }
}
