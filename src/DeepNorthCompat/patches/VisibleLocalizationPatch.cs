using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static DeepNorthCompat.ILMatch;

namespace DeepNorthCompat
{
    // Localization.ReLocalizeVisible collects every Text and TMP_Text under the root, inactive ones included,
    // and then skips each label whose own GameObject is inactive. A search for active objects returns exactly
    // the labels that pass that filter, so passing includeInactive: false saves the work without changing the
    // result. The filter stays because Unity returns the root's own components even when the root is inactive.
    internal static class VisibleLocalizationPatch
    {
        internal static readonly TranspilerGroup Group = new TranspilerGroup("UI.VisibleLocalization",
            AccessTools.DeclaredMethod(typeof(VisibleLocalizationPatch), nameof(Rewrite)),
            "visible localization skips inactive descendants and keeps each label's activity check.");
        private static readonly MethodInfo gameObjectGetter = AccessTools.PropertyGetter(typeof(Component), nameof(Component.gameObject));
        private static readonly MethodInfo activeInHierarchyGetter = AccessTools.PropertyGetter(typeof(GameObject), nameof(GameObject.activeInHierarchy));
        private static readonly MethodInfo childSearch = AccessTools.Method(typeof(GameObject), nameof(GameObject.GetComponentsInChildren), new[] { typeof(bool) });
        private static readonly MethodInfo textSearch = childSearch.MakeGenericMethod(typeof(Text));
        private static readonly MethodInfo tmpSearch = childSearch.MakeGenericMethod(typeof(TMP_Text));

        internal static void Prepare()
        {
            if (Group.Installed) return;
            if (Guard.KnownServerBuild()) { CompatibilityInstaller.Info($"{Group.Name}: client-only; inactive on dedicated server."); return; }

            Guard.Build(typeof(Player).Assembly, GameBuilds.Valheim1021Client);
            Guard.Build(typeof(Localization).Assembly, ExpectedBuilds.GuiUtils1021Client);
            Group.Patch(Guard.Method(typeof(Localization), nameof(Localization.ReLocalizeVisible), typeof(void), typeof(Transform)));
        }

        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions) =>
            Group.Transpile(instructions, "ReLocalizeVisible no longer has both audited visible text searches", code =>
            {
                // Both assemblies are pinned by hash, so this fails only if another mod's transpiler removes a label filter.
                bool filtersRemain = code.Count(i => i.Calls(activeInHierarchyGetter)) == 2;
                return filtersRemain && ExcludeInactive(code, textSearch) && ExcludeInactive(code, tmpSearch);
            });

        // root.gameObject.GetComponentsInChildren<T>(true) becomes GetComponentsInChildren<T>(false).
        // The flip changes one opcode in place, so labels and blocks in the window don't matter.
        private static bool ExcludeInactive(List<CodeInstruction> code, MethodInfo search)
        {
            int callSite = Unique(code, i => code[i].Calls(search));
            if (!Shape(code, callSite - 3, Is(OpCodes.Ldarg_1), Calls(gameObjectGetter), Is(OpCodes.Ldc_I4_1), Calls(search))) return false;
            code[callSite - 1].opcode = OpCodes.Ldc_I4_0;
            return true;
        }
    }
}
