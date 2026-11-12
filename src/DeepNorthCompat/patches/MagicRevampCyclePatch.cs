using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using static DeepNorthCompat.ILMatch;
using ItemData = ItemDrop.ItemData;

namespace DeepNorthCompat
{
    internal static class MagicRevampCyclePatch
    {
        internal const string Owner = Plugin.Guid + ".MagicRevamp.Cycle";
        private const string Ammo = "$ammo_spells";
        private const string Scroll = "$bmr_scroll_";
        private static MethodInfo cycleMethod = null!, namesMethod = null!, vendorCountMethod = null!, vendorNextMethod = null!;
        private static readonly MethodInfo cycleCountMethod = Hook(nameof(CycleCount)), cycleNextMethod = Hook(nameof(CycleNext));
        private static Func<int> vendorCount = null!;
        private static Func<ItemData> vendorNext = null!;
        private static Func<Player, Inventory> spellBook = null!;
        private static bool pending, enabled, installed, rewritten;

        internal static void Prepare(Assembly? magic, Assembly? backpacks)
        {
            if (pending || installed) return;
            if (magic == null) { CompatibilityInstaller.Info("MagicRevamp.Cycle: optional mod absent; inactive."); return; }
            if (Guard.KnownServerBuild()) { CompatibilityInstaller.Info("MagicRevamp.Cycle: client-only; inactive on dedicated server."); return; }
            if (backpacks == null) throw new InvalidOperationException("MagicRevamp requires Backpacks.");

            Guard.Build(magic, ExpectedBuilds.MagicRevamp151);
            Guard.Build(backpacks, ExpectedBuilds.Backpacks1310);
            Guard.Build(typeof(Player).Assembly, ExpectedBuilds.Valheim1021Client);
            Type type = Guard.Type(magic, "MagicRevamp.Functions.CycleSpells");
            cycleMethod = Guard.Method(type, "CycleSpell", typeof(void));
            namesMethod = Guard.Method(type, "GetSpellHudNames", typeof(ValueTuple<int, string, int, string, int, string>), typeof(Player));
            vendorCountMethod = Guard.Method(type, "CountSpellStacks", typeof(int));
            vendorNextMethod = Guard.Method(type, "EquipNextSpell", typeof(ItemData));
            vendorCount = (Func<int>)Delegate.CreateDelegate(typeof(Func<int>), vendorCountMethod);
            vendorNext = (Func<ItemData>)Delegate.CreateDelegate(typeof(Func<ItemData>), vendorNextMethod);
            spellBook = (Func<Player, Inventory>)Delegate.CreateDelegate(typeof(Func<Player, Inventory>),
                Guard.Method(type, "SpellBookContainer", typeof(Inventory), typeof(Player)));

            rewritten = false;
            var harmony = new Harmony(Owner);
            harmony.Patch(cycleMethod, transpiler: Guard.Hook(typeof(MagicRevampCyclePatch), nameof(RewriteCycle)));
            harmony.Patch(namesMethod, prefix: Guard.Hook(typeof(MagicRevampCyclePatch), nameof(Names)));
            Guard.Registered(cycleMethod);
            Guard.Registered(namesMethod);
            pending = installed = true;
            CompatibilityInstaller.Info("MagicRevamp.Cycle: audited builds verified; awaiting cycle hook verification.");
        }

        internal static void Verify()
        {
            if (!pending) return;
            pending = false;
            try
            {
                Guard.Sole(cycleMethod, Hook(nameof(RewriteCycle)), Owner, HarmonyPatchType.Transpiler);
                Guard.Sole(namesMethod, Hook(nameof(Names)), Owner, HarmonyPatchType.Prefix);
                if (!rewritten) throw new NotSupportedException("The vendor cycle call sites did not match.");
                enabled = true;
                CompatibilityInstaller.Info("MagicRevamp.Cycle: APPLIED; cycling excludes hotbar scrolls and the preview follows the selected sequence.");
            }
            catch (Exception exception)
            {
                enabled = false;
                try
                {
                    new Harmony(Owner).UnpatchSelf();
                    installed = false;
                }
                catch (Exception rollback)
                {
                    CompatibilityInstaller.Error($"MagicRevamp.Cycle: application and rollback failed; inspect Harmony state. {exception} {rollback}");
                    return;
                }
                CompatibilityInstaller.Error($"MagicRevamp.Cycle: NOT APPLIED; vendor behavior retained. {exception}");
            }
        }

        internal static void Reset()
        {
            pending = enabled = installed = false;
            new Harmony(Owner).UnpatchSelf();
        }

        private static MethodInfo Hook(string name) => AccessTools.DeclaredMethod(typeof(MagicRevampCyclePatch), name);

        private static IEnumerable<CodeInstruction> RewriteCycle(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> original = instructions.ToList();
            List<CodeInstruction> code = original.Select(instruction => new CodeInstruction(instruction)).ToList();
            int countSite = Unique(code, index => code[index].Calls(vendorCountMethod));
            int nextSite = Unique(code, index => code[index].Calls(vendorNextMethod));
            rewritten = countSite >= 0 && nextSite >= 0;
            if (!rewritten)
            {
                Lost(ref enabled, "MagicRevamp.Cycle", "vendor cycle call sites changed");
                return original;
            }

            code[countSite].operand = cycleCountMethod;
            code[nextSite].operand = cycleNextMethod;
            return code;
        }

        private static int CycleCount()
        {
            if (!enabled) return vendorCount();
            Player player = Player.m_localPlayer;
            if (player.GetCurrentWeapon().m_shared.m_ammoType != Ammo) return vendorCount();
            Selection selection = Select(player);
            ItemData? current = selection.Current(player.GetAmmoItem());
            // The vendor equips only in its default switch branch; a different sole spell must reach it too.
            return selection.Items.Count == 1 && current?.m_shared.m_name != selection.Items[0].m_shared.m_name
                ? 2 : selection.Items.Count;
        }

        private static ItemData CycleNext()
        {
            if (!enabled) return vendorNext();
            Player player = Player.m_localPlayer;
            if (player.GetCurrentWeapon().m_shared.m_ammoType != Ammo) return vendorNext();
            Selection selection = Select(player);
            return selection.Next(selection.Current(player.GetAmmoItem()));
        }

        private static bool Names(Player __0, ref (int, string, int, string, int, string) __result)
        {
            if (!enabled || __0.GetCurrentWeapon().m_shared.m_ammoType != Ammo) return true;

            Selection selection = Select(__0);
            ItemData? current = selection.Current(__0.GetAmmoItem());
            ItemData? next = selection.Items.Count == 0 ? null : selection.Next(current);
            ItemData? previous = selection.Items.Count == 0 ? null : selection.Previous(current);
            __result = (current?.m_stack ?? 0, current?.m_shared.m_name ?? "", next?.m_stack ?? 0,
                next?.m_shared.m_name ?? "", previous?.m_stack ?? 0, previous?.m_shared.m_name ?? "");
            return false;
        }

        private static Selection Select(Player player)
        {
            Inventory? book = spellBook(player);
            Inventory playerInventory = player.GetInventory();
            Inventory source = book ?? playerInventory;
            string ammo = player.GetCurrentWeapon().m_shared.m_ammoType;
            var names = new HashSet<string>();
            var items = new List<ItemData>();
            foreach (ItemData item in source.GetAllItems())
            {
                if (item.m_shared.m_itemType != ItemData.ItemType.Ammo || item.m_shared.m_ammoType != ammo
                    || !item.m_shared.m_name.StartsWith(Scroll)
                    || book == null && item.m_gridPos.y == 0 && item.m_gridPos.x >= 0 && item.m_gridPos.x < 8
                    || !names.Add(item.m_shared.m_name)) continue;
                items.Add(item);
            }

            items.Sort((a, b) => Position(source, a).CompareTo(Position(source, b)));
            return new Selection(source, playerInventory, items);
        }

        private static int Position(Inventory source, ItemData item) => item.m_gridPos.y * source.GetWidth() + item.m_gridPos.x;

        private static bool IsSpell(ItemData? item, string ammo) => item != null
            && item.m_shared.m_itemType == ItemData.ItemType.Ammo && item.m_shared.m_ammoType == ammo
            && item.m_shared.m_name.StartsWith(Scroll);

        private sealed class Selection
        {
            private readonly Inventory source;
            private readonly Inventory playerInventory;
            internal readonly List<ItemData> Items;

            internal Selection(Inventory source, Inventory playerInventory, List<ItemData> items)
            {
                this.source = source; this.playerInventory = playerInventory; Items = items;
            }

            internal ItemData? Current(ItemData? current) => IsSpell(current, Ammo)
                && (source.GetAllItems().Contains(current!) || playerInventory.GetAllItems().Contains(current!)) ? current : null;

            internal ItemData Next(ItemData? current)
            {
                int position = current != null && source.GetAllItems().Contains(current) ? Position(source, current) : -1;
                return Items.FirstOrDefault(item => Position(source, item) > position) ?? Items[0];
            }

            internal ItemData Previous(ItemData? current)
            {
                int position = current != null && source.GetAllItems().Contains(current) ? Position(source, current) : -1;
                return Items.LastOrDefault(item => Position(source, item) < position) ?? Items[Items.Count - 1];
            }
        }
    }
}
