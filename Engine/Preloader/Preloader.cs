#if REALPORTAL_PRELOADER
using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace RealPortal.Preloader
{
    // BepInEx 5 preloader patcher (RealPortal.Preloader.dll in BepInEx\patchers) for the engine's ZDO journal
    // (ZdoJournalModule; ARCHITECTURE-v2 §3.3 and §5.2 E1). Before the game loads assembly_valheim it:
    //  - marks the six methods the journal hooks NoInlining: ZDO.IncreaseDataRevision, ZDO.IncreaseOwnerRevision,
    //    ZDO.SetSector, ZDOMan.AddToSector, ZDOMan.InitialAddToSector, ZDOMan.RemoveFromSector. A Harmony patch detours
    //    the method's own code, so a call site the JIT compiled with the method inlined would never reach the hook.
    //    Mono inlines only bodies under 20 IL bytes and the six are 24-119 bytes in this game version, so today this is
    //    insurance against a game update that shrinks one of them (and clears AggressiveInlining should one gain it);
    //  - adds `public int rp_row` to ZDO, the journal consumer's row index (one field access instead of a dictionary
    //    lookup), and makes it -1 ("no row") in every ZDO constructor and at the start of ZDO.Reset, which ZDOPool.Release
    //    calls before a ZDO object goes back to the pool for reuse under another id. So a row can never outlive the ZDO it
    //    was given to. (MemberwiseClone copies, ZDO.Clone for saving, also get reset by SaveCleanup's Reset and are never
    //    journalled.)
    // Nothing else changes, and none of it changes what the game does: the field is read by nobody else, the stores
    // write only it, NoInlining only changes JIT choices for methods Mono did not inline anyway (Harmony marks what it
    // patches NoInlining at runtime too; this also covers call sites compiled before the engine's plugin loads). Cost:
    // the ZDO object grows by 4 bytes (Pack=1 layout; the allocation may round up by 8).
    //
    // Every target is looked up, with its exact signature, before anything is written: if one is missing or ambiguous
    // (a game update), or ZDO already has an rp_row of another kind, the assembly is left exactly as it was and an error
    // is logged; the engine then runs its journal on Harmony alone with a Dictionary<ZDOID,int> for rows. Idempotent:
    // what is already there (field, flags, stores) is kept and reported, so patching an already patched image (dumped
    // assemblies, a second patcher run) changes nothing. The engine checks the result at startup on its own (field by
    // reflection, the stores in the IL, NoInlining by MethodImplementationFlags) and falls back if anything is off.
    public static class Patcher
    {
        // Read by RealPortal.Engine.ZdoJournal (by name, the engine does not reference this assembly).
        public const string RowField = "rp_row";

        public static IEnumerable<string> TargetDLLs => new[] { "assembly_valheim.dll" };

        static ManualLogSource s_log;

        static ManualLogSource Log => s_log ??= Logger.CreateLogSource("RealPortal.Preloader");

        public static void Patch(AssemblyDefinition assembly)
        {
            try
            {
                Run(assembly.MainModule);
            }
            catch (Exception e)
            {
                // Only reachable from a Cecil failure after every target was found (nothing in the plan can fail on its
                // own). Not rethrown: BepInEx would then drop every patcher's work on this assembly, other mods'
                // included. Whatever was applied is checked piece by piece by the engine at startup, which falls back
                // to Harmony and a dictionary for whatever is missing.
                Log.LogError("assembly_valheim: patching for the ZDO journal failed part way; the engine's self-check " +
                    "decides what it can use. " + e);
            }
        }

        sealed class Plan
        {
            public TypeDefinition Zdo;
            public FieldDefinition Row;
            public readonly List<MethodDefinition> NoInline = new List<MethodDefinition>();
            public readonly List<MethodDefinition> ResetsRow = new List<MethodDefinition>();
        }

        static void Run(ModuleDefinition module)
        {
            var missing = new List<string>();
            Plan plan = Find(module, missing);
            if (missing.Count > 0)
            {
                Log.LogError("assembly_valheim left UNTOUCHED: the ZDO journal's patch targets are not as expected (game " +
                    "update?): " + string.Join("; ", missing.ToArray()) + ". The engine's journal runs on Harmony only, " +
                    "with rows in a dictionary; check its self-check line in the log.");
                return;
            }

            var done = new List<string>();
            var already = new List<string>();
            if (plan.Row == null)
            {
                plan.Row = new FieldDefinition(RowField, FieldAttributes.Public, module.TypeSystem.Int32);
                plan.Zdo.Fields.Add(plan.Row);
                done.Add("added field ZDO." + RowField);
            }
            else
            {
                already.Add("field ZDO." + RowField);
            }

            var marked = new List<string>();
            var markedBefore = new List<string>();
            foreach (MethodDefinition m in plan.NoInline)
            {
                if (m.NoInlining && !m.AggressiveInlining)
                {
                    markedBefore.Add(Name(m));
                    continue;
                }
                m.NoInlining = true;
                m.AggressiveInlining = false;
                marked.Add(Name(m));
            }
            if (marked.Count > 0)
                done.Add("NoInlining on " + string.Join(", ", marked.ToArray()));
            if (markedBefore.Count > 0)
                already.Add("NoInlining on " + string.Join(", ", markedBefore.ToArray()));

            var stores = new List<string>();
            var storesBefore = new List<string>();
            foreach (MethodDefinition m in plan.ResetsRow)
            {
                if (StoresRow(m))
                {
                    storesBefore.Add(Name(m));
                    continue;
                }
                // ldarg.0; ldc.i4.m1; stfld rp_row, ahead of everything else. Branches keep their targets (the old first
                // instruction), and nothing branches back to offset 0 in these bodies; in a constructor a store to the
                // class's own field before the base constructor call is what C# field initializers do.
                ILProcessor il = m.Body.GetILProcessor();
                Instruction first = m.Body.Instructions[0];
                il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(first, il.Create(OpCodes.Ldc_I4_M1));
                il.InsertBefore(first, il.Create(OpCodes.Stfld, plan.Row));
                stores.Add(Name(m));
            }
            if (stores.Count > 0)
                done.Add(RowField + " = -1 at the start of " + string.Join(", ", stores.ToArray()));
            if (storesBefore.Count > 0)
                already.Add(RowField + " = -1 in " + string.Join(", ", storesBefore.ToArray()));

            if (done.Count == 0)
                Log.LogInfo("assembly_valheim already patched for the ZDO journal, nothing changed (" + string.Join("; ", already.ToArray()) + ")");
            else
                Log.LogInfo("assembly_valheim patched for the ZDO journal: " + string.Join("; ", done.ToArray()) +
                    (already.Count > 0 ? " (already there: " + string.Join("; ", already.ToArray()) + ")" : ""));
        }

        // Every target with its exact signature. Writes nothing.
        static Plan Find(ModuleDefinition module, List<string> missing)
        {
            var plan = new Plan();
            TypeDefinition zdo = module.GetType("ZDO");
            TypeDefinition man = module.GetType("ZDOMan");
            TypeDefinition zone = module.GetType("ZoneSystem");
            TypeDefinition sector = zone?.NestedTypes.FirstOrDefault(t => t.Name == "SectorIndex");
            if (zdo == null)
                missing.Add("type ZDO");
            if (man == null)
                missing.Add("type ZDOMan");
            if (sector == null || !sector.IsValueType)
                missing.Add("struct ZoneSystem.SectorIndex");
            if (missing.Count > 0)
                return plan;
            plan.Zdo = zdo;
            string s = sector.FullName;   // "ZoneSystem/SectorIndex"

            void NoInline(TypeDefinition type, string name, params string[] parameters)
            {
                MethodDefinition m = Method(type, name, parameters, missing);
                if (m != null)
                    plan.NoInline.Add(m);
            }
            NoInline(zdo, "IncreaseDataRevision");
            NoInline(zdo, "IncreaseOwnerRevision");
            NoInline(zdo, "SetSector", s);
            NoInline(man, "AddToSector", "ZDO", s);
            NoInline(man, "InitialAddToSector", "ZDO", s);
            NoInline(man, "RemoveFromSector", "ZDO", s);

            MethodDefinition reset = Method(zdo, "Reset", new string[0], missing);
            if (reset != null)
                plan.ResetsRow.Add(reset);
            List<MethodDefinition> ctors = zdo.Methods.Where(m => m.IsConstructor && !m.IsStatic).ToList();
            if (ctors.Count == 0)
                missing.Add("a ZDO instance constructor");
            foreach (MethodDefinition c in ctors)
            {
                if (!c.HasBody || c.Body.Instructions.Count == 0)
                    missing.Add("a body for " + Name(c));
                else
                    plan.ResetsRow.Add(c);
            }

            FieldDefinition existing = zdo.Fields.FirstOrDefault(f => f.Name == RowField);
            if (existing != null && (existing.IsStatic || !existing.IsPublic || existing.FieldType.MetadataType != MetadataType.Int32))
                missing.Add("ZDO." + RowField + " exists but is not a public instance int (" + existing.FieldType.FullName + ")");
            plan.Row = existing;
            return plan;
        }

        // The one instance method of that name and parameter types returning void, with a body.
        static MethodDefinition Method(TypeDefinition type, string name, string[] parameters, List<string> missing)
        {
            string sig = type.Name + "." + name + "(" + string.Join(", ", parameters) + ")";
            List<MethodDefinition> found = type.Methods.Where(m => m.Name == name && !m.IsStatic && m.HasBody
                && m.ReturnType.MetadataType == MetadataType.Void && m.Parameters.Count == parameters.Length
                && m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(parameters)).ToList();
            if (found.Count == 1)
                return found[0];
            missing.Add(found.Count == 0 ? sig + " not found" : sig + " ambiguous");
            return null;
        }

        static bool StoresRow(MethodDefinition m) =>
            m.Body.Instructions.Any(i => i.OpCode.Code == Code.Stfld && i.Operand is FieldReference f
                && f.Name == RowField && f.DeclaringType.FullName == "ZDO");

        static string Name(MethodDefinition m) => m.DeclaringType.Name + "." + m.Name;
    }
}
#endif
