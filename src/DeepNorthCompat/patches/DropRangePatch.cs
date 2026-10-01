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
        // Parser -> whether its transpiler ran and matched. Null until Harmony runs it, which
        // a startup optimizer can defer until after Awake.
        private static readonly Dictionary<MethodBase, bool?> parsers = new Dictionary<MethodBase, bool?>();

        internal static void Install(Assembly assembly, string name)
        {
            Type closure = Guard.Type(assembly, "CreatureManager.Creature+DropList+SerializedDrops+<>c");
            Type drop = Guard.Type(assembly, "CreatureManager.Creature+Drop");
            MethodInfo parser = Guard.Method(closure, "<.ctor>b__3_2", drop, typeof(string[]));
            // Config binding occurs at FejdStartup and drops are assigned at ZNetScene startup.
            // Both occur after BepInEx has run this plugin's Awake.
            var harmony = new Harmony(Plugin.Guid + ".Drops." + name);
            parsers[parser] = null;
            harmony.Patch(parser, transpiler: Guard.Hook(typeof(DropRangePatch), nameof(FixMaximum)));
            Guard.Registered(parser);
        }

        internal static void Verify()
        {
            foreach (KeyValuePair<MethodBase, bool?> parser in parsers)
            {
                if (parser.Value == true) CompatibilityInstaller.Info($"Verified: {Guard.Name(parser.Key)}");
                else if (parser.Value == null)
                    CompatibilityInstaller.Error($"{Guard.Name(parser.Key)}: transpiler did not run; parser unchanged.");
            }
        }

        // Returns the original IL on a mismatch instead of throwing: a deferred patch batch may
        // apply this after Awake, where an exception would abort every other mod's patches.
        public static IEnumerable<CodeInstruction> FixMaximum(IEnumerable<CodeInstruction> instructions,
            MethodBase original)
        {
            List<CodeInstruction> source = instructions.ToList();
            List<CodeInstruction> code = source.Select(i => new CodeInstruction(i)).ToList();
            MethodInfo parse = typeof(int).GetMethod("TryParse", new[] { typeof(string), typeof(int).MakeByRefType() })!;
            List<int> calls = Enumerable.Range(0, code.Count).Where(i => code[i].Calls(parse)).ToList();

            if (calls.Count != 2 || calls.Any(i => i < 3
                || code[i - 2].opcode != OpCodes.Ldelem_Ref
                || code[i - 3].opcode != OpCodes.Ldc_I4_0))
            {
                CompatibilityInstaller.Error($"Drops: CreatureManager range defect no longer matches in "
                    + $"{original.DeclaringType?.Assembly.GetName().Name}; parser unchanged.");
                if (parsers.ContainsKey(original)) parsers[original] = false;
                return source;
            }

            // Change only the second array index. Keep minimum, single-value branch, default
            // fallback, chance, one-per-player and level multiplier exactly as upstream.
            code[calls[1] - 3].opcode = OpCodes.Ldc_I4_1;
            if (parsers.ContainsKey(original)) parsers[original] = true;
            return code;
        }
    }
}
