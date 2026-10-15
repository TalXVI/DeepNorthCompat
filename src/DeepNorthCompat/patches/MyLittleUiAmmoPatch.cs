using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using TMPro;

namespace DeepNorthCompat
{
    internal static class MyLittleUiAmmoPatch
    {
        internal const string Guid = "shudnal.MyLittleUI";
        internal const string Owner = Plugin.Guid + ".MyLittleUI.AmmoCount";
        private const string Build = "AF4A568F13719421D0FDB3A7984D8A89B05A7672BDD9E3F230A0542E6D978FE2";
        private static readonly string[] GameHashes =
        {
            "25A0A107DCE4D834C44C2B72D0EAFD5CB7793933BDA81816ACCFA1EA9543DACE",
            "94998885B103D6E5F5480123EAEE0A093E82AF2DDEEEE37536C15BF555F24F3A"
        };
        private const string TmpHash = "719A2E22767F1A9C7B92F47AEA2F58550FAE2E76189F99181757B04A3595E181";
        private static MethodInfo target = null!;
        private static bool pending, enabled;
        private static bool? rewritten;

        internal static void Prepare(Assembly? assembly)
        {
            if (pending || enabled) return;
            if (assembly == null) { CompatibilityInstaller.Info("MyLittleUI.AmmoCount: optional mod absent; inactive."); return; }
            if (Guard.KnownServerBuild()) { CompatibilityInstaller.Info("MyLittleUI.AmmoCount: client-only; inactive on dedicated server."); return; }
            Guard.Build(assembly, Build);
            Guard.Build(typeof(Player).Assembly, GameHashes);
            Guard.Build(typeof(TMP_Text).Assembly, TmpHash);
            target = Guard.Method(Guard.Type(assembly, "MyLittleUI.AmmoCountIcon+ElementExtraData"), "UpdateState",
                typeof(void), Guard.Type(assembly, "MyLittleUI.AmmoCountTemplates"), typeof(Player));
            rewritten = null;
            new Harmony(Owner).Patch(target, transpiler: Guard.Hook(typeof(MyLittleUiAmmoPatch), nameof(Rewrite)));
            pending = true;
            CompatibilityInstaller.Info("MyLittleUI.AmmoCount: 1.2.31 build verified; awaiting transpiler verification.");
        }

        internal static void Verify()
        {
            if (!pending) return;
            pending = false;
            if (rewritten != true || Harmony.GetPatchInfo(target)?.Transpilers.Count(p => p.owner == Owner
                && p.PatchMethod == AccessTools.Method(typeof(MyLittleUiAmmoPatch), nameof(Rewrite))) != 1)
            {
                new Harmony(Owner).UnpatchSelf();
                CompatibilityInstaller.Error("MyLittleUI.AmmoCount: NOT APPLIED; ammo label IL or hook registration did not match; My Little UI behavior retained.");
                return;
            }
            enabled = true;
            CompatibilityInstaller.Info("MyLittleUI.AmmoCount: APPLIED; ammo counts redraw only when the displayed number changes.");
        }

        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            List<CodeInstruction> original = instructions.ToList();
            List<CodeInstruction> code = original.Select(instruction => new CodeInstruction(instruction)).ToList();
            MethodInfo setText = AccessTools.Method(typeof(TMP_Text), nameof(TMP_Text.SetText), new[] { typeof(string) });
            MethodInfo number = AccessTools.Method(typeof(int), nameof(int.ToString), Type.EmptyTypes);
            int[] writes = code.Select((instruction, index) => (instruction, index))
                .Where(pair => pair.instruction.Calls(setText)).Select(pair => pair.index).ToArray();
            Type cell = __originalMethod.DeclaringType!;
            FieldInfo ammo = AccessTools.Field(cell, "m_ammo");
            FieldInfo bait = AccessTools.Field(cell, "baitAmountText");
            int index = writes.Length == 2 ? writes[0] : -1;
            bool match = writes.Length == 2 && code.Count(instruction => instruction.Calls(number)) == 2
                && ammo != null && bait != null && index >= 3 && writes[1] >= 1
                && code[index - 3].LoadsField(ammo)
                && CountAddress(code[index - 2])
                && code[index - 1].Calls(number)
                && code[index - 1].labels.Count == 0 && code[index - 1].blocks.Count == 0
                && code[index].labels.Count == 0 && code[index].blocks.Count == 0
                && code[writes[1] - 1].LoadsField(bait);
            rewritten = match;
            if (!match) return original;
            code[index - 2].opcode = OpCodes.Ldloc_S;
            code[index - 1].operand = AccessTools.Method(typeof(MyLittleUiAmmoPatch), nameof(SetCount));
            code.RemoveAt(index);
            return code;
        }

        private static bool CountAddress(CodeInstruction instruction) =>
            (instruction.opcode == OpCodes.Ldloca_S || instruction.opcode == OpCodes.Ldloca)
            && instruction.operand is LocalBuilder local && local.LocalType == typeof(int);

        // OnPreRenderCanvas and the preferred-size queries reparse m_text, and style, enable and
        // template changes dirty the label themselves, so an unchanged number never needs rewriting.
        private static void SetCount(TMP_Text label, int count)
        {
            if (!Shows(label.text, count)) label.SetText(count.ToString());
        }

        private static bool Shows(string? text, int count)
        {
            if (text == null || count < 0) return false;
            int i = text.Length;
            do { if (--i < 0 || text[i] != (char)('0' + count % 10)) return false; count /= 10; } while (count != 0);
            return i == 0;
        }
    }
}
