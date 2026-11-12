using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace DeepNorthCompat
{
    internal static class MagicRevampInventoryPatch
    {
        internal const string Guid = "blacks7ar.MagicRevamp";
        internal const string BackpacksGuid = "org.bepinex.plugins.backpacks";
        internal const string Group = "MagicRevamp.Inventory";
        internal const string Owner = Plugin.Guid + "." + Group;
        private const string SpellAmmo = "$ammo_spells";

        private static MethodInfo contains = null!, ammo = null!, remove = null!;
        private static MethodInfo vendorContains = null!, vendorAmmo = null!, vendorRemove = null!;
        private static Func<Player, Inventory> spellBook = null!;
        private static bool pending, enabled;

        internal static bool Ready => enabled;

        internal static Inventory? EquippedBook(Player player) =>
            enabled && player && player == Player.m_localPlayer ? spellBook(player) : null;

        internal static void Prepare(Assembly? magic, Assembly? backpacks)
        {
            if (pending || enabled) return;
            if (magic == null || backpacks == null)
            {
                CompatibilityInstaller.Info($"{Group}: MagicRevamp or Backpacks absent; inactive.");
                return;
            }
            if (Guard.KnownServerBuild())
            {
                CompatibilityInstaller.Info($"{Group}: local-player inventory hooks; inactive on dedicated server.");
                return;
            }

            Guard.Build(magic, ExpectedBuilds.MagicRevamp151);
            Guard.Build(backpacks, ExpectedBuilds.Backpacks1310);
            Guard.Build(typeof(Inventory).Assembly, ExpectedBuilds.Valheim1021Client);
            Type patch = Guard.Type(magic, "MagicRevamp.Patches.InventoryPatch");
            Type cycle = Guard.Type(magic, "MagicRevamp.Functions.CycleSpells");
            Type item = typeof(ItemDrop.ItemData), result = typeof(bool).MakeByRefType();
            contains = Guard.Method(typeof(Inventory), "ContainsItem", typeof(bool), item);
            ammo = Guard.Method(typeof(Inventory), "GetAmmoItem", item, typeof(string), typeof(string));
            remove = Guard.Method(typeof(Inventory), "RemoveItem", typeof(bool), item, typeof(int));
            vendorContains = Guard.Method(patch, "ContainsItem_Postfix", typeof(void), typeof(Inventory), item, result);
            vendorAmmo = Guard.Method(patch, "GetAmmoItem_Postfix", typeof(void), typeof(Inventory), item.MakeByRefType(), typeof(string), typeof(string));
            vendorRemove = Guard.Method(patch, "RemoveItem_Postfix", typeof(void), typeof(Inventory), item, typeof(int), result);
            MethodInfo book = Guard.Method(cycle, "SpellBookContainer", typeof(Inventory), typeof(Player));
            spellBook = (Func<Player, Inventory>)Delegate.CreateDelegate(typeof(Func<Player, Inventory>), book);
            pending = true;
            CompatibilityInstaller.Info($"{Group}: audited builds verified; awaiting vendor Harmony registrations.");
        }

        internal static void Verify()
        {
            if (!pending) return;
            pending = false;
            var harmony = new Harmony(Owner);
            try
            {
                Guard.Sole(contains, vendorContains, Guid, HarmonyPatchType.Postfix);
                Guard.Sole(ammo, vendorAmmo, Guid, HarmonyPatchType.Postfix);
                Guard.Sole(remove, vendorRemove, Guid, HarmonyPatchType.Postfix);

                var hooks = new[]
                {
                    (Target: vendorContains, Hook: Guard.Hook(typeof(MagicRevampInventoryPatch), nameof(ScopeContains))),
                    (Target: vendorAmmo, Hook: Guard.Hook(typeof(MagicRevampInventoryPatch), nameof(ScopeAmmo))),
                    (Target: vendorRemove, Hook: Guard.Hook(typeof(MagicRevampInventoryPatch), nameof(RemoveFromBook)))
                };
                foreach (var entry in hooks) harmony.Patch(entry.Target, prefix: entry.Hook);
                foreach (var entry in hooks)
                {
                    Patches? info = Harmony.GetPatchInfo(entry.Target);
                    if (info == null || info.Prefixes.Count(p => p.owner == Owner && p.PatchMethod == entry.Hook.method) != 1
                        || info.Prefixes.Count(p => p.owner == Owner) != 1
                        || info.Postfixes.Concat(info.Transpilers).Concat(info.Finalizers).Concat(info.ILManipulators).Any(p => p.owner == Owner))
                        throw new NotSupportedException("The inventory replacement prefix did not register exactly once.");
                }

                enabled = true;
                CompatibilityInstaller.Info($"{Group}: APPLIED; spellbook queries and removals use the local player's inventory.");
            }
            catch (Exception exception)
            {
                enabled = false;
                try { harmony.UnpatchSelf(); }
                catch (Exception rollback)
                {
                    CompatibilityInstaller.Error($"{Group}: application and rollback failed; inspect Harmony state. {exception} {rollback}");
                    return;
                }
                CompatibilityInstaller.Error($"{Group}: NOT APPLIED; vendor behavior retained. {exception}");
            }
        }

        internal static void Reset()
        {
            pending = false;
            enabled = false;
            new Harmony(Owner).UnpatchSelf();
        }

        private static bool PlayerInventory(Inventory inventory, out Player player)
        {
            player = Player.m_localPlayer;
            return player && ReferenceEquals(inventory, player.GetInventory());
        }

        private static bool SpellWeapon(Player player) => player.GetCurrentWeapon()?.m_shared?.m_ammoType == SpellAmmo;
        private static bool SpellItem(ItemDrop.ItemData item) => item?.m_shared?.m_ammoType == SpellAmmo;

        private static bool ScopeContains(Inventory __0, ItemDrop.ItemData __1)
        {
            if (!enabled) return true;
            return PlayerInventory(__0, out Player player) && SpellItem(__1) && SpellWeapon(player);
        }

        private static bool ScopeAmmo(Inventory __0, string __2)
        {
            if (!enabled) return true;
            return __2 == SpellAmmo && PlayerInventory(__0, out Player player) && SpellWeapon(player);
        }

        private static bool RemoveFromBook(Inventory __0, ItemDrop.ItemData __1, int __2, ref bool __3)
        {
            if (!enabled) return true;
            if (!__3 && __2 > 0 && PlayerInventory(__0, out Player player) && SpellItem(__1) && SpellWeapon(player))
            {
                Inventory? book = spellBook(player);
                if (book != null && book.GetAllItems().Contains(__1)) __3 = book.RemoveItem(__1, __2);
            }
            return false;
        }
    }
}
