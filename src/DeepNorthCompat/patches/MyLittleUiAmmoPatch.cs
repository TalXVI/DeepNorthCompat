using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using TMPro;
using static DeepNorthCompat.ILMatch;

namespace DeepNorthCompat
{
    internal static class MyLittleUiAmmoPatch
    {
        internal const string Guid = "shudnal.MyLittleUI";
        internal const string Owner = Plugin.Guid + ".MyLittleUI.AmmoCount";
        private static readonly string[] Builds =
        {
            "AF4A568F13719421D0FDB3A7984D8A89B05A7672BDD9E3F230A0542E6D978FE2",
            "22413D8E16C194BF97B566279F3BCF2E9525D3CC487CF2BAC33E9520219D48A6",
            "7834183B0E3CCD1D811848C746E25D0F344DFA07A5D523BC34BBD0EB1183DB01"
        };
        private static readonly string[] GameHashes =
        {
            "25A0A107DCE4D834C44C2B72D0EAFD5CB7793933BDA81816ACCFA1EA9543DACE",
            ExpectedBuilds.Valheim1020Client,
            ExpectedBuilds.Valheim1021Client
        };
        private const string TmpHash = "719A2E22767F1A9C7B92F47AEA2F58550FAE2E76189F99181757B04A3595E181";
        private static readonly MethodInfo setText = AccessTools.Method(typeof(TMP_Text), nameof(TMP_Text.SetText), new[] { typeof(string) });
        private static readonly MethodInfo number = AccessTools.Method(typeof(int), nameof(int.ToString), Type.EmptyTypes);
        private static readonly MethodInfo setCount = AccessTools.DeclaredMethod(typeof(MyLittleUiAmmoPatch), nameof(SetCount));
        private static readonly MethodInfo rewrite = AccessTools.DeclaredMethod(typeof(MyLittleUiAmmoPatch), nameof(Rewrite));
        private static MethodInfo target = null!;
        private static bool pending, enabled;
        private static bool? rewritten;

        internal static void Prepare(Assembly? assembly)
        {
            if (pending || enabled) return;
            if (assembly == null) { CompatibilityInstaller.Info("MyLittleUI.AmmoCount: optional mod absent; inactive."); return; }
            if (Guard.KnownServerBuild()) { CompatibilityInstaller.Info("MyLittleUI.AmmoCount: client-only; inactive on dedicated server."); return; }
            Guard.Build(assembly, Builds);
            Guard.Build(typeof(Player).Assembly, GameHashes);
            Guard.Build(typeof(TMP_Text).Assembly, TmpHash);
            target = Guard.Method(Guard.Type(assembly, "MyLittleUI.AmmoCountIcon+ElementExtraData"), "UpdateState",
                typeof(void), Guard.Type(assembly, "MyLittleUI.AmmoCountTemplates"), typeof(Player));
            rewritten = null;
            new Harmony(Owner).Patch(target, transpiler: Guard.Hook(typeof(MyLittleUiAmmoPatch), nameof(Rewrite)));
            pending = true;
            CompatibilityInstaller.Info("MyLittleUI.AmmoCount: audited build verified; awaiting transpiler verification.");
        }

        internal static void Verify()
        {
            if (!pending) return;
            pending = false;
            if (rewritten != true || Harmony.GetPatchInfo(target)?.Transpilers.Count(p => p.owner == Owner
                && p.PatchMethod == rewrite) != 1)
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
            int[] writes = Enumerable.Range(0, code.Count).Where(i => code[i].Calls(setText)).ToArray();
            Type cell = __originalMethod.DeclaringType!;
            FieldInfo? ammo = AccessTools.Field(cell, "m_ammo"), bait = AccessTools.Field(cell, "baitAmountText");
            // m_ammo.SetText(count.ToString()), then baitAmountText.SetText(...)
            bool match = writes.Length == 2 && code.Count(instruction => instruction.Calls(number)) == 2 && ammo != null && bait != null
                && Shape(code, writes[0] - 3, Loads(ammo), CountAddress, Calls(number), Calls(setText))
                && Plain(code[writes[0] - 1]) && Plain(code[writes[0]])
                && Shape(code, writes[1] - 1, Loads(bait), Calls(setText));
            rewritten = match;
            if (!match)
            {
                Lost(ref enabled, "MyLittleUI.AmmoCount", "the ammo label write no longer matches");
                return original;
            }
            int index = writes[0];
            code[index - 2].opcode = OpCodes.Ldloc_S;
            code[index - 1].operand = setCount;
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
