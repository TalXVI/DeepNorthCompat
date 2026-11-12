using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using static DeepNorthCompat.ILMatch;

namespace DeepNorthCompat
{
    internal static class MagicRevampEitrPatch
    {
        internal const string Name = "MagicRevamp.Eitr";
        internal const string Owner = Plugin.Guid + "." + Name;
        private const string VendorOwner = MagicRevampInventoryPatch.Guid;
        private static readonly FieldInfo shared = AccessTools.Field(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.m_shared));
        private static readonly FieldInfo attack = AccessTools.Field(typeof(ItemDrop.ItemData.SharedData), nameof(ItemDrop.ItemData.SharedData.m_attack));
        private static readonly MethodInfo rewrite = AccessTools.DeclaredMethod(typeof(MagicRevampEitrPatch), nameof(Rewrite));
        private static readonly MethodInfo resolve = AccessTools.DeclaredMethod(typeof(MagicRevampEitrPatch), nameof(ResolveCost));
        private static MethodInfo target = null!, vendorTranspiler = null!, vendorHelper = null!;
        private static Func<Humanoid, ItemDrop.ItemData, ItemDrop.ItemData?> findAmmo = null!;
        private static Func<Attack, float> vendorCost = null!;
        private static bool pending, enabled, lost, rewritten;

        internal static void Prepare(Assembly? magic)
        {
            if (pending || enabled || Harmony.HasAnyPatches(Owner)) return;
            if (magic == null) { CompatibilityInstaller.Info($"{Name}: optional mod absent; inactive."); return; }
            if (Guard.KnownServerBuild()) { CompatibilityInstaller.Info($"{Name}: client-only; inactive on dedicated server."); return; }

            Guard.Build(magic, ExpectedBuilds.MagicRevamp151);
            Guard.Build(typeof(Attack).Assembly, ExpectedBuilds.Valheim1021Client);
            Type vendor = Guard.Type(magic, "MagicRevamp.Patches.AttackPatch");
            target = Guard.Method(typeof(Attack), "GetAttackEitr", typeof(float), typeof(Character), typeof(ItemDrop.ItemData));
            vendorTranspiler = Guard.Method(vendor, "EitrTranspiler", typeof(IEnumerable<CodeInstruction>), typeof(IEnumerable<CodeInstruction>));
            vendorHelper = Guard.Method(vendor, "GetAttackEitr", typeof(float), typeof(Attack));
            MethodInfo find = Guard.Method(typeof(Attack), "FindAmmo", typeof(ItemDrop.ItemData), typeof(Humanoid), typeof(ItemDrop.ItemData));
            if (!vendorTranspiler.IsStatic || !vendorHelper.IsStatic || !find.IsStatic || target.IsStatic
                || shared.FieldType != typeof(ItemDrop.ItemData.SharedData) || attack.FieldType != typeof(Attack)
                || !Locals(target, 2, (0, typeof(float)), (1, typeof(float))))
                throw new NotSupportedException("MagicRevamp eitr target shape changed.");

            findAmmo = (Func<Humanoid, ItemDrop.ItemData, ItemDrop.ItemData?>)Delegate.CreateDelegate(
                typeof(Func<Humanoid, ItemDrop.ItemData, ItemDrop.ItemData>), find);
            vendorCost = (Func<Attack, float>)Delegate.CreateDelegate(typeof(Func<Attack, float>), vendorHelper);
            pending = true;
            CompatibilityInstaller.Info($"{Name}: audited build verified; awaiting vendor and inventory hooks.");
        }

        internal static void Verify()
        {
            if (!pending) return;
            pending = false;
            var harmony = new Harmony(Owner);
            try
            {
                if (!MagicRevampInventoryPatch.Ready)
                    throw new NotSupportedException("MagicRevamp inventory queries are not applied.");
                Guard.Sole(target, vendorTranspiler, VendorOwner, HarmonyPatchType.Transpiler);
                rewritten = false;
                lost = false;
                harmony.Patch(target, transpiler: new HarmonyMethod(rewrite) { after = new[] { VendorOwner } });
                Patches? patches = Harmony.GetPatchInfo(target);
                Patch[] ours = patches?.Transpilers.Where(p => p.owner == Owner).ToArray() ?? Array.Empty<Patch>();
                if (!rewritten || patches == null || ours.Length != 1 || ours[0].PatchMethod != rewrite
                    || !ours[0].after.Contains(VendorOwner) || !MagicRevampInventoryPatch.Ready
                    || patches.Prefixes.Concat(patches.Postfixes).Concat(patches.Finalizers).Concat(patches.ILManipulators)
                        .Any(p => p.owner == Owner))
                    throw new NotSupportedException("The eitr rewrite or its inventory dependency did not verify.");
                Guard.Sole(target, vendorTranspiler, VendorOwner, HarmonyPatchType.Transpiler);
                enabled = true;
                CompatibilityInstaller.Info($"{Name}: APPLIED; spell eitr cost follows the selected ammo.");
            }
            catch (Exception exception)
            {
                Reset();
                CompatibilityInstaller.Error($"{Name}: NOT APPLIED; vendor behavior retained. {exception.Message}");
            }
        }

        internal static void Reset()
        {
            pending = enabled = lost = rewritten = false;
            new Harmony(Owner).UnpatchSelf();
        }

        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> original = instructions.ToList();
            if (lost) return original;
            List<CodeInstruction> code = original.Select(instruction => new CodeInstruction(instruction)).ToList();
            int site = Site(code, i => code[i].opcode == OpCodes.Call && code[i].Calls(vendorHelper));
            rewritten = site >= 0 && Shape(code, site - 3,
                Is(OpCodes.Ldarg_2), Loads(shared), Loads(attack), Calls(vendorHelper), IsStore(0));
            if (!rewritten)
            {
                if (enabled) lost = true;
                Lost(ref enabled, Name, "the vendor eitr rewrite no longer matches");
                return original;
            }

            code[site].operand = resolve;
            code.InsertRange(site, new[] { new CodeInstruction(OpCodes.Ldarg_1), new CodeInstruction(OpCodes.Ldarg_2) });
            return code;
        }

        private static float ResolveCost(Attack sharedAttack, Character character, ItemDrop.ItemData weapon)
        {
            if (enabled && !MagicRevampInventoryPatch.Ready)
            {
                lost = true;
                Lost(ref enabled, Name, "the inventory query repair is no longer applied");
            }
            if (!enabled) return vendorCost(sharedAttack);
            if (character is Player player && weapon.m_shared.m_ammoType.Contains("ammo_spells"))
            {
                ItemDrop.ItemData? ammo = findAmmo(player, weapon);
                if (ammo != null) return ammo.m_shared.m_attack.m_attackEitr;
            }
            return sharedAttack.m_attackEitr;
        }
    }
}
