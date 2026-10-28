using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;
using static DeepNorthCompat.ILMatch;

namespace DeepNorthCompat
{
    internal static class AaaRecipeRowsPatch
    {
        internal const string Owner = Plugin.Guid + ".UI.AAARecipeRows";
        private static readonly MethodInfo contains = AccessTools.Method(typeof(HashSet<GameObject>), nameof(HashSet<GameObject>.Contains));
        private static readonly MethodInfo setActive = AccessTools.Method(typeof(GameObject), nameof(GameObject.SetActive));
        private static readonly MethodInfo discard = AccessTools.DeclaredMethod(typeof(AaaRecipeRowsPatch), nameof(Discard));
        private static readonly MethodInfo rewrite = AccessTools.DeclaredMethod(typeof(AaaRecipeRowsPatch), nameof(Rewrite));
        private static MethodInfo target = null!;
        private static bool pending, enabled;
        private static bool? rewritten;

        internal static void Prepare(Assembly? assembly)
        {
            if (pending || enabled) return;
            if (assembly == null) { CompatibilityInstaller.Info("UI.AAARecipeRows: optional mod absent; inactive."); return; }
            if (Guard.KnownServerBuild()) { CompatibilityInstaller.Info("UI.AAARecipeRows: client-only; inactive on dedicated server."); return; }
            Guard.Build(assembly, ExpectedBuilds.AAACrafting);
            Guard.Build(typeof(InventoryGui).Assembly, ExpectedBuilds.Valheim1020Client, GameBuilds.Valheim1021Client);
            target = Guard.Method(Guard.Type(assembly,
                "AzuAntiArthriticCrafting.Patches.PaginatorPatches+InventoryGuiUpdateRecipeListPatch"), "Postfix",
                typeof(void), typeof(InventoryGui), typeof(List<Recipe>).MakeByRefType());
            rewritten = null;
            new Harmony(Owner).Patch(target, transpiler: Guard.Hook(typeof(AaaRecipeRowsPatch), nameof(Rewrite)));
            pending = true;
            CompatibilityInstaller.Info("UI.AAARecipeRows: audited builds verified; awaiting transpiler verification.");
        }

        internal static void Verify()
        {
            if (!pending) return;
            pending = false;
            if (rewritten != true || Harmony.GetPatchInfo(target)?.Transpilers.Count(p => p.owner == Owner && p.PatchMethod == rewrite) != 1)
            {
                new Harmony(Owner).UnpatchSelf();
                CompatibilityInstaller.Error("UI.AAARecipeRows: NOT APPLIED; pagination IL or hook registration did not match; AAA behavior retained.");
                return;
            }
            enabled = true;
            CompatibilityInstaller.Info("UI.AAARecipeRows: APPLIED; discarded upgrade recipe rows are destroyed before pagination drops their cleanup references.");
        }

        private static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            List<CodeInstruction> original = instructions.ToList();
            List<CodeInstruction> code = original.Select(i => new CodeInstruction(i)).ToList();
            // Only the non-craft page's HashSet<GameObject> exclusion branch untracks live rows.
            // The craft branch's HashSet<Transform> pass keeps every row tracked and hides only untracked children.
            int site = Site(code, i => Shape(code, i, IsLoad(10), IsLoad(35), Calls(contains), BranchTrue,
                    IsLoad(35), Is(OpCodes.Ldc_I4_0), Calls(setActive), IsLoad(34))
                && code[i + 3].operand is Label skip && code[i + 7].labels.Contains(skip)
                && code.Skip(i).Take(7).All(Plain));
            rewritten = site >= 0 && Locals(__originalMethod, 47, (10, typeof(HashSet<GameObject>)), (34, typeof(int)), (35, typeof(GameObject)));
            if (rewritten != true)
            {
                Lost(ref enabled, "UI.AAARecipeRows", "the discarded-row branch no longer matches");
                return original;
            }
            code[site + 6].opcode = OpCodes.Call;
            code[site + 6].operand = discard;
            return code;
        }

        private static bool BranchTrue(CodeInstruction code) => code.opcode == OpCodes.Brtrue || code.opcode == OpCodes.Brtrue_S;

        private static void Discard(GameObject row, bool active)
        {
            row.SetActive(active);
            if (enabled) Object.Destroy(row);
        }
    }
}
