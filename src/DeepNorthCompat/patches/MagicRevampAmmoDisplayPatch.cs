using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using static DeepNorthCompat.ILMatch;

namespace DeepNorthCompat
{
    internal static class MagicRevampAmmoDisplayPatch
    {
        internal const string Group = "MagicRevamp.AmmoDisplay";
        internal const string Owner = Plugin.Guid + "." + Group;
        private const string MyLittleUiBuild = "BF5CDBCC4683CA77F5C05E51D246D06A0D754D2F0D76B54A029076C87C3F3084";
        private const string SpellAmmo = "$ammo_spells";
        private static readonly FieldInfo shared = AccessTools.Field(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.m_shared));
        private static readonly FieldInfo name = AccessTools.Field(typeof(ItemDrop.ItemData.SharedData), nameof(ItemDrop.ItemData.SharedData.m_name));
        private static readonly MethodInfo count = AccessTools.Method(typeof(Inventory), nameof(Inventory.CountItems),
            new[] { typeof(string), typeof(int), typeof(bool) });
        private static readonly MethodInfo rewrite = AccessTools.DeclaredMethod(typeof(MagicRevampAmmoDisplayPatch), nameof(Rewrite));
        private static readonly MethodInfo selectedCount = AccessTools.DeclaredMethod(typeof(MagicRevampAmmoDisplayPatch), nameof(SelectedCount));
        private static MethodInfo target = null!, currentAmmo = null!;
        private static bool pending, enabled, lost, rewritten;

        internal static void Prepare(Assembly? magic, Assembly? backpacks, Assembly? myLittleUi)
        {
            if (pending || enabled || Harmony.HasAnyPatches(Owner)) return;
            if (magic == null || backpacks == null || myLittleUi == null)
            {
                CompatibilityInstaller.Info($"{Group}: MagicRevamp, Backpacks or MyLittleUI absent; inactive.");
                return;
            }
            if (Guard.KnownServerBuild())
            {
                CompatibilityInstaller.Info($"{Group}: client-only; inactive on dedicated server.");
                return;
            }

            Guard.Build(magic, ExpectedBuilds.MagicRevamp151);
            Guard.Build(backpacks, ExpectedBuilds.Backpacks1310);
            Guard.Build(myLittleUi, MyLittleUiBuild);
            Guard.Build(typeof(Player).Assembly, ExpectedBuilds.Valheim1021Client);
            Type cell = Guard.Type(myLittleUi, "MyLittleUI.AmmoCountIcon+ElementExtraData");
            target = Guard.Method(cell, "UpdateState", typeof(void), Guard.Type(myLittleUi, "MyLittleUI.AmmoCountTemplates"), typeof(Player));
            currentAmmo = Guard.Method(cell, "GetCurrentAmmo", typeof(ItemDrop.ItemData), typeof(Player));
            if (target.IsStatic || currentAmmo.IsStatic || count.IsStatic
                || shared.FieldType != typeof(ItemDrop.ItemData.SharedData) || name.FieldType != typeof(string)
                || !Locals(target, 13, (0, typeof(Inventory)), (1, typeof(ItemDrop.ItemData)), (2, typeof(int)),
                    (5, typeof(ItemDrop.ItemData)), (7, typeof(int))))
                throw new NotSupportedException("MyLittleUI ammo count target shape changed.");

            pending = true;
            CompatibilityInstaller.Info($"{Group}: audited builds verified; awaiting inventory and ammo display hooks.");
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
                rewritten = false;
                lost = false;
                harmony.Patch(target, transpiler: new HarmonyMethod(rewrite) { after = new[] { MyLittleUiAmmoPatch.Owner } });
                Patches? patches = Harmony.GetPatchInfo(target);
                Patch[] ours = patches?.Transpilers.Where(p => p.owner == Owner).ToArray() ?? Array.Empty<Patch>();
                if (!rewritten || ours.Length != 1 || ours[0].PatchMethod != rewrite
                    || !ours[0].after.Contains(MyLittleUiAmmoPatch.Owner) || !MagicRevampInventoryPatch.Ready)
                    throw new NotSupportedException("The ammo count rewrite or its inventory dependency did not verify.");

                enabled = true;
                CompatibilityInstaller.Info($"{Group}: APPLIED; the selected spell count uses its inventory.");
            }
            catch (Exception exception)
            {
                enabled = false;
                harmony.UnpatchSelf();
                CompatibilityInstaller.Error($"{Group}: NOT APPLIED; MyLittleUI behavior retained. {exception.Message}");
            }
        }

        internal static void Reset()
        {
            pending = false;
            enabled = false;
            lost = false;
            new Harmony(Owner).UnpatchSelf();
        }

        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> original = instructions.ToList();
            if (lost) return original;
            List<CodeInstruction> code = original.Select(instruction => new CodeInstruction(instruction)).ToList();
            int site = Site(code, i => Shape(code, i - 12,
                Is(OpCodes.Ldarg_0), Is(OpCodes.Ldarg_2), Calls(currentAmmo), IsStore(1), IsLoad(1), Is(OpCodes.Brfalse),
                IsLoad(0), IsLoad(1), Loads(shared), Loads(name), Is(OpCodes.Ldc_I4_M1), Is(OpCodes.Ldc_I4_1), CountCall));
            int bait = Unique(code, i => Shape(code, i - 6, IsLoad(0), IsLoad(5), Loads(shared), Loads(name),
                Is(OpCodes.Ldc_I4_M1), Is(OpCodes.Ldc_I4_1), CountCall));
            rewritten = site >= 0 && bait > site && code.Count(CountCall) == 2;
            if (!rewritten)
            {
                if (enabled) lost = true;
                Lost(ref enabled, Group, "the selected ammo count no longer matches");
                return original;
            }

            code[site].opcode = OpCodes.Call;
            code[site].operand = selectedCount;
            code.InsertRange(site, new[] { new CodeInstruction(OpCodes.Ldarg_2), new CodeInstruction(OpCodes.Ldloc_1) });
            return code;
        }

        private static bool CountCall(CodeInstruction instruction) => instruction.opcode == OpCodes.Callvirt && instruction.Calls(count);

        internal static int SelectedCount(Inventory inventory, string itemName, int quality, bool matchWorldLevel,
            Player player, ItemDrop.ItemData item)
        {
            if (enabled && !MagicRevampInventoryPatch.Ready)
            {
                lost = true;
                Lost(ref enabled, Group, "the inventory query repair is no longer applied");
            }
            if (enabled && item.m_shared.m_ammoType == SpellAmmo && ReferenceEquals(inventory, player.GetInventory()))
            {
                Inventory? book = MagicRevampInventoryPatch.EquippedBook(player);
                if (book != null && book.GetAllItems().Contains(item)) return book.CountItems(itemName, quality, matchWorldLevel);
            }
            return inventory.CountItems(itemName, quality, matchWorldLevel);
        }
    }
}
