using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace DeepNorthCompat
{
    // ReforgedPotential runs its whole Forge of Potential operation in a DoCrafting prefix that ignores
    // cancellation. When chest crafting cancels the craft to wait for ownership, that prefix still removed
    // the item being upgraded. DNC re-registers it behind a wrapper that honors the cancellation.
    internal static class ReforgedPotentialPatch
    {
        internal const string Guid = "akuichi.ReforgedPotential";
        private const string Group = "Crafting.ReforgedPotential";
        private const string Owner = Plugin.Guid + "." + Group;
        private const string VendorHash = "23FAF48FEF0B8A7A024063C65BB2DFE21C7DF823A2B5EB7C1933D30DB4292AEA";
        private static MethodInfo target = null!, vendorCraft = null!;
        private static Func<InventoryGui, Player, bool> craft = null!;
        private static bool pending, applied;

        internal static void Prepare(Assembly? vendor)
        {
            if (pending || applied) return;
            if (vendor == null)
            {
                CompatibilityInstaller.Info($"{Group}: optional mod absent; inactive.");
                return;
            }
            if (Guard.KnownServerBuild())
            {
                CompatibilityInstaller.Info($"{Group}: client-only crafting hook; inactive on dedicated server.");
                return;
            }
            Guard.Build(vendor, VendorHash);
            Guard.Build(typeof(InventoryGui).Assembly, ExpectedBuilds.Valheim1020Client, GameBuilds.Valheim1021Client);
            Type gui = Guard.Type(vendor, "ReforgedPotential.ReforgedPotential+InventoryGuiPatch");
            target = Guard.Method(typeof(InventoryGui), "DoCrafting", typeof(void), typeof(Player));
            vendorCraft = Guard.Method(gui, "DoCraftingPrefix", typeof(bool), typeof(InventoryGui), typeof(Player));
            craft = (Func<InventoryGui, Player, bool>)Delegate.CreateDelegate(typeof(Func<InventoryGui, Player, bool>), vendorCraft);
            pending = true;
            CompatibilityInstaller.Info($"{Group}: audited builds verified; awaiting the vendor crafting registration.");
        }

        internal static void Verify()
        {
            if (!pending) return;
            pending = false;
            Patch? original = null;
            var harmony = new Harmony(Owner);
            try
            {
                original = Guard.Sole(target, vendorCraft, Guid, HarmonyPatchType.Prefix);
                // The vendor operation stands in for the native body, so it runs after every prefix, as that body does.
                HarmonyMethod replacement = Guard.Hook(typeof(ReforgedPotentialPatch), nameof(Craft), Priority.Last);
                harmony.Unpatch(target, vendorCraft);
                harmony.Patch(target, prefix: replacement);
                if (Harmony.GetPatchInfo(target).Prefixes.Count(p => p.owner == Owner && p.PatchMethod == replacement.method) != 1)
                    throw new NotSupportedException("The replacement crafting prefix did not register.");
                applied = true;
                CompatibilityInstaller.Info($"{Group}: APPLIED; Forge of Potential upgrades wait for a deferred craft.");
            }
            catch (Exception exception)
            {
                applied = false;
                try
                {
                    harmony.UnpatchSelf();
                    if (original != null) Guard.Restore(target, original, HarmonyPatchType.Prefix);
                }
                catch (Exception rollback)
                {
                    CompatibilityInstaller.Error($"{Group}: application and rollback failed; inspect Harmony state. {exception} {rollback}");
                    return;
                }
                CompatibilityInstaller.Error($"{Group}: NOT APPLIED; vendor behavior retained. {exception}");
            }
        }

        // An earlier prefix that cancels the craft, such as the chest handoff wait, also cancels the vendor operation.
        private static bool Craft(InventoryGui __instance, Player __0, bool __runOriginal) =>
            __runOriginal && craft(__instance, __0);
    }
}
