using System;
using System.Globalization;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;

namespace DeepNorthCompat
{
    internal static class BowPatch
    {
        private static FieldInfo enabled = null!;
        private static FieldInfo reduction = null!;
        private static bool active;
        internal static void DisableIf(string name) { if (name == "Bow") active = false; }

        internal static void Install(Assembly impact)
        {
            Type config = Guard.Type(impact, "ImpactfulSkills.ValConfig");
            enabled = AccessTools.Field(config, "EnableWeaponSkill");
            reduction = AccessTools.Field(config, "WeaponSkillBowDrawStaminaCostReduction");
            if (enabled?.FieldType != typeof(ConfigEntry<bool>) || reduction?.FieldType != typeof(ConfigEntry<float>))
            {
                throw new MissingFieldException("ImpactfulSkills bow configuration changed.");
            }

            MethodInfo calculation = Guard.Method(Guard.Type(impact, "ImpactfulSkills.patches.WeaponSkill"),
                "ModifyStaminaDrainCostForBow", typeof(float));
            MethodInfo report = Guard.Method(Guard.Type(impact, "ImpactfulSkills.patches.WeaponSkill+ItemDisplay"),
                "Postfix", typeof(void), typeof(ItemDrop.ItemData), typeof(string).MakeByRefType());

            Guard.Method(typeof(ItemDrop.ItemData), "GetDrawStaminaDrain", typeof(float));

            var harmony = new Harmony(Plugin.Guid + ".Bow");
            harmony.Patch(calculation, prefix: Guard.Hook(typeof(BowPatch), nameof(Coefficient)));
            harmony.Patch(report, postfix: Guard.Hook(typeof(BowPatch), nameof(Report)));
            Guard.Applied(calculation); Guard.Applied(report);
            active = true;
        }

        private static bool Coefficient(ref float __result)
        {
            if (!active) return true;
            __result = BowCalculation.Reduction(((ConfigEntry<float>)reduction.GetValue(null)).Value,
                ((ConfigEntry<bool>)enabled.GetValue(null)).Value && Player.m_localPlayer != null);
            return false;
        }

        private static void Report(ItemDrop.ItemData __0, ref string __1)
        {
            if (!active || !((ConfigEntry<bool>)enabled.GetValue(null)).Value
                || Player.m_localPlayer == null
                || !__1.Contains("item_staminahold")) return;

            float drain = __0.GetDrawStaminaDrain();
            string[] lines = __1.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("item_staminahold")) continue;
                lines[i] = "$item_staminahold: <color=orange>"
                    + __0.m_shared.m_attack.m_drawStaminaDrain.ToString("0.##", CultureInfo.InvariantCulture)
                    + "</color> <color=yellow>(" + drain.ToString("0.##", CultureInfo.InvariantCulture) + ")</color>/s";
            }
            __1 = string.Join("\n", lines);
        }
    }
}
