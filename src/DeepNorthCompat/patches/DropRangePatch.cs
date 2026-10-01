using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace DeepNorthCompat
{
    public static class DropRangePatch
    {
        internal static void Install(Assembly assembly, string name)
        {
            Type closure = Guard.Type(assembly, "CreatureManager.Creature+DropList+SerializedDrops+<>c");
            Type drop = Guard.Type(assembly, "CreatureManager.Creature+Drop");
            MethodInfo parser = Guard.Method(closure, "<.ctor>b__3_2", drop, typeof(string[]));
            // Config binding occurs at FejdStartup and drops are assigned at ZNetScene startup.
            // Both occur after BepInEx has run this plugin's Awake.
            var harmony = new Harmony(Plugin.Guid + ".Drops." + name);
            harmony.Patch(parser, transpiler: Guard.Hook(typeof(DropRangePatch), nameof(FixMaximum)));
            Guard.Applied(parser);
        }

        public static IEnumerable<CodeInstruction> FixMaximum(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = instructions.Select(i => new CodeInstruction(i)).ToList();
            MethodInfo parse = typeof(int).GetMethod("TryParse", new[] { typeof(string), typeof(int).MakeByRefType() })!;
            List<int> calls = Enumerable.Range(0, code.Count).Where(i => code[i].Calls(parse)).ToList();

            if (calls.Count != 2 || calls.Any(i => i < 3
                || code[i - 2].opcode != OpCodes.Ldelem_Ref
                || code[i - 3].opcode != OpCodes.Ldc_I4_0))
            {
                throw new NotSupportedException("CreatureManager range defect no longer matches; parser unchanged.");
            }

            // Change only the second array index. Keep minimum, single-value branch, default
            // fallback, chance, one-per-player and level multiplier exactly as upstream.
            code[calls[1] - 3].opcode = OpCodes.Ldc_I4_1;
            return code;
        }
    }
}
