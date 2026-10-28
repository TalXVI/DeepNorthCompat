using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using ConditionalConfigSync;
using HarmonyLib;

namespace DeepNorthCompat
{
    internal static class ForgeCraftingPatch
    {
        internal const string Guid = "Azumatt.AzuAntiArthriticCrafting";
        internal const string Owner = Plugin.Guid + ".Crafting.ForgeOfPotential";
        private static readonly FieldInfo upgraderDuration = AccessTools.Field(typeof(InventoryGui), "m_upgraderDuration");
        private static readonly FieldInfo upgraderPerLevel = AccessTools.Field(typeof(InventoryGui), "m_upgraderDurationPerLevel");
        private static readonly MethodInfo rewrite = AccessTools.DeclaredMethod(typeof(ForgeCraftingPatch), nameof(Rewrite));
        private static readonly MethodInfo hook = AccessTools.DeclaredMethod(typeof(ForgeCraftingPatch), nameof(Duration));
        private static ConfigEntry<bool> forgeSpeed = null!;
        private static ConfigEntry<float> duration = null!;
        // AAA's override is a ConfigEntry of its own Toggle enum. The upgrader timer reads it on each UpdateRecipe call, so it is read as an int without boxing.
        private static Func<int> overrideMode = null!;
        private static MethodInfo target = null!;
        private static bool pending, applied;
        private static string? failure;

        internal static void BindConfig(ConfigFile config, ConfigSync sync)
        {
            forgeSpeed = config.Bind("Crafting", "Forge of Potential speed", true,
                "Apply AAA Crafting's Crafting Speed to the Forge of Potential when Override Crafting Speed is on. Server-controlled by default; takes effect immediately.");
            sync.AddConfigEntry(forgeSpeed);
        }

        internal static void Prepare(Assembly? assembly)
        {
            if (pending || applied) return;
            if (assembly == null)
            {
                CompatibilityInstaller.Info("Crafting.ForgeOfPotential: AAA Crafting absent; inactive.");
                return;
            }
            if (Guard.KnownServerBuild())
            {
                CompatibilityInstaller.Info("Crafting.ForgeOfPotential: client-only; inactive on dedicated server.");
                return;
            }
            Guard.Build(assembly, ExpectedBuilds.AAACrafting);
            Guard.Build(typeof(InventoryGui).Assembly, ExpectedBuilds.Valheim1020Client, GameBuilds.Valheim1021Client);
            Type plugin = Guard.Type(assembly, "AzuAntiArthriticCrafting.AzuAntiArthriticCraftingPlugin");
            Type toggle = Guard.Type(assembly, plugin.FullName + "+Toggle");
            FieldInfo enabled = AccessTools.Field(plugin, "OverrideCraftingDuration");
            FieldInfo seconds = AccessTools.Field(plugin, "CraftingDuration");
            if (enabled?.FieldType != typeof(ConfigEntry<>).MakeGenericType(toggle) || seconds?.FieldType != typeof(ConfigEntry<float>)
                || Enum.GetUnderlyingType(toggle) != typeof(int))
                throw new MissingFieldException("AAA Crafting duration configuration changed.");
            overrideMode = Mode(enabled.FieldType, enabled.GetValue(null)
                ?? throw new InvalidOperationException("AAA Crafting's speed override config is not initialized."));
            duration = (ConfigEntry<float>)seconds.GetValue(null)
                ?? throw new InvalidOperationException("AAA Crafting's duration config is not initialized.");
            target = Guard.Method(typeof(InventoryGui), "UpdateRecipe", typeof(void), typeof(Player), typeof(float));
            failure = "the transpiler did not run";
            new Harmony(Owner).Patch(target, transpiler: new HarmonyMethod(rewrite));
            pending = true;
            CompatibilityInstaller.Info("Crafting.ForgeOfPotential: audited builds verified; awaiting transpiler verification.");
        }

        // Binds ConfigEntry<Toggle>.Value to the cached entry, returning the enum's int value.
        private static Func<int> Mode(Type entryType, object entry)
        {
            var read = new DynamicMethod("DNC_AaaOverrideMode", typeof(int), new[] { entryType }, typeof(ForgeCraftingPatch), true);
            ILGenerator il = read.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Callvirt, AccessTools.PropertyGetter(entryType, "Value"));
            il.Emit(OpCodes.Ret);
            return (Func<int>)read.CreateDelegate(typeof(Func<int>), entry);
        }

        internal static void Verify()
        {
            if (!pending) return;
            pending = false;
            if (failure == null && Harmony.GetPatchInfo(target)?.Transpilers.Count(p => p.owner == Owner && p.PatchMethod == rewrite) != 1)
                failure = "the transpiler registration did not match";
            if (failure != null)
            {
                new Harmony(Owner).UnpatchSelf();
                CompatibilityInstaller.Error($"Crafting.ForgeOfPotential: NOT APPLIED; {failure}; vanilla forge timing retained.");
                return;
            }
            applied = true;
            CompatibilityInstaller.Info("Crafting.ForgeOfPotential: APPLIED; Forge of Potential uses AAA Crafting's speed when the synced toggle and AAA override are on; skill scaling retained.");
        }

        // HarmonyX reruns this whenever another mod patches UpdateRecipe. A miss keeps the incoming IL and vanilla timing.
        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> original = instructions.ToList();
            // m_upgraderDuration + itemLevel * m_upgraderDurationPerLevel
            CodeMatch[] sum =
            {
                new CodeMatch(OpCodes.Ldarg_0), new CodeMatch(OpCodes.Ldfld, upgraderDuration), new CodeMatch(OpCodes.Ldloc_2), new CodeMatch(OpCodes.Conv_R4),
                new CodeMatch(OpCodes.Ldarg_0), new CodeMatch(OpCodes.Ldfld, upgraderPerLevel), new CodeMatch(OpCodes.Mul), new CodeMatch(OpCodes.Add, name: "sum")
            };
            var matcher = new CodeMatcher(original).MatchStartForward(sum);
            failure = Reject(original, matcher, sum.Length);
            if (failure != null)
            {
                if (applied)
                {
                    applied = false;
                    CompatibilityInstaller.Error($"Crafting.ForgeOfPotential: NO LONGER APPLIED; {failure}; vanilla forge timing retained.");
                }
                return original;
            }
            CodeInstruction add = matcher.NamedMatch("sum");
            add.opcode = OpCodes.Call;
            add.operand = hook;
            return matcher.InstructionEnumeration();
        }

        private static string? Reject(List<CodeInstruction> code, CodeMatcher matcher, int length)
        {
            if (code.Count(i => i.LoadsField(upgraderDuration)) != 1 || code.Count(i => i.LoadsField(upgraderPerLevel)) != 1)
                return "InventoryGui.UpdateRecipe does not load each upgrader duration field exactly once";
            if (matcher.IsInvalid) return "InventoryGui.UpdateRecipe has no upgrader duration sum";
            if (matcher.Instructions(length).Skip(1).Any(i => i.labels.Count != 0 || i.blocks.Count != 0))
                return "the upgrader duration sum contains a branch target or exception boundary";
            return null;
        }

        private static float Duration(float baseDuration, float levelDuration) =>
            forgeSpeed.Value && overrideMode() != 0 ? duration.Value : baseDuration + levelDuration;
    }
}
