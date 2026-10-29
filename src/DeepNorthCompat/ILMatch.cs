using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace DeepNorthCompat
{
    // Declarative, fail-closed IL matching shared by the transpilers.
    internal static class ILMatch
    {
        private static readonly OpCode[] shortLoads = { OpCodes.Ldloc_0, OpCodes.Ldloc_1, OpCodes.Ldloc_2, OpCodes.Ldloc_3 };
        private static readonly OpCode[] shortStores = { OpCodes.Stloc_0, OpCodes.Stloc_1, OpCodes.Stloc_2, OpCodes.Stloc_3 };

        // The only index that matches, if no label or exception block starts there; otherwise -1.
        internal static int Site(List<CodeInstruction> code, Func<int, bool> matches)
        {
            int site = Unique(code, matches);
            return site >= 0 && Plain(code[site]) ? site : -1;
        }

        // The only index that matches, or -1. Enough for an in-place opcode or operand change, which keeps the instruction's labels and blocks.
        internal static int Unique(List<CodeInstruction> code, Func<int, bool> matches)
        {
            int[] sites = Enumerable.Range(0, code.Count).Where(matches).ToArray();
            return sites.Length == 1 ? sites[0] : -1;
        }

        // True when nothing branches to or starts an exception block at the instruction, so code inserted before it always runs.
        internal static bool Plain(CodeInstruction code) => code.labels.Count == 0 && code.blocks.Count == 0;

        internal static bool Shape(List<CodeInstruction> code, int start, params Func<CodeInstruction, bool>[] pattern) =>
            start >= 0 && start + pattern.Length <= code.Count && Enumerable.Range(0, pattern.Length).All(i => pattern[i](code[start + i]));

        internal static bool Locals(MethodBase method, int count, params (int Index, Type Type)[] expected)
        {
            IList<LocalVariableInfo> locals = method.GetMethodBody()!.LocalVariables;
            return locals.Count == count && expected.All(local => locals[local.Index].LocalType == local.Type);
        }

        internal static Func<CodeInstruction, bool> Is(OpCode opcode) => code => code.opcode == opcode;
        internal static Func<CodeInstruction, bool> Loads(FieldInfo field) => code => code.LoadsField(field);
        internal static Func<CodeInstruction, bool> Calls(MethodInfo method) => code => code.Calls(method);
        internal static Func<CodeInstruction, bool> IsLoad(int index) => code => index < shortLoads.Length && code.opcode == shortLoads[index]
            || (code.opcode == OpCodes.Ldloc || code.opcode == OpCodes.Ldloc_S) && Local(code) == index;
        internal static Func<CodeInstruction, bool> IsStore(int index) => code => index < shortStores.Length && code.opcode == shortStores[index]
            || (code.opcode == OpCodes.Stloc || code.opcode == OpCodes.Stloc_S) && Local(code) == index;
        // Only for the long and short forms: the macro forms have a null operand, which Convert reads as 0.
        private static int Local(CodeInstruction code) => code.operand is LocalVariableInfo local ? local.LocalIndex : Convert.ToInt32(code.operand);

        // A rerun after the group applied no longer matches: the transpiler keeps the incoming IL, so the group stops.
        internal static void Lost(ref bool enabled, string group, string reason)
        {
            if (!enabled) return;
            enabled = false;
            CompatibilityInstaller.Error($"{group}: NO LONGER APPLIED; {reason}; vendor behavior retained.");
        }
    }
}
