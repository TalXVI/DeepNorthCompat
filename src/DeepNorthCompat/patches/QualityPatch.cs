using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;

namespace DeepNorthCompat
{
    using ItemStack = ResourceStack<ItemDrop.ItemData, QualityPatch.Source>;
    using ItemNeed = ResourceNeed<QualityPatch.Source>;
    using ItemAllocation = Allocation<ItemDrop.ItemData, QualityPatch.Source>;
    using ItemIngredient = SelectedIngredient<ItemDrop.ItemData, QualityPatch.Source>;
    using ItemPlan = IngredientPlan<ItemDrop.ItemData, QualityPatch.Source>;
    using ItemReservation = IngredientReservation<ItemDrop.ItemData, QualityPatch.Source>;

    internal static class QualityPatch
    {
        internal sealed class Source
        {
            internal readonly Inventory Inventory;
            internal readonly object? Container;
            internal Source(Inventory inventory, object? container) { Inventory = inventory; Container = container; }
        }

        private sealed class Craft
        {
            internal readonly Player Player;
            internal readonly Piece.Requirement[] Requirements;
            internal readonly int Quality;
            internal readonly int Multiplier;
            internal readonly string Prefab;
            internal readonly ItemPlan Plan;
            internal readonly ItemReservation Reservation;
            // The player's inventory before DoCrafting. Vanilla removes an item being upgraded
            // before output, so a failed craft restores this rather than a later snapshot.
            internal readonly List<ItemDrop.ItemData> PlayerItems;
            internal readonly Dictionary<ItemDrop.ItemData, int> PlayerCounts;
            internal Craft(Player player, Piece.Requirement[] requirements, int quality, int multiplier,
                string prefab, ItemPlan plan)
            {
                Player = player; Requirements = requirements; Quality = quality; Multiplier = multiplier;
                Prefab = prefab; Plan = plan; Reservation = new ItemReservation(plan);
                PlayerItems = new List<ItemDrop.ItemData>(player.GetInventory().GetAllItems());
                PlayerCounts = PlayerItems.ToDictionary(item => item, item => item.m_stack);
            }
        }

        private sealed class Scope
        {
            internal Craft? Previous;
            internal Craft? Created;
        }

        [ThreadStatic] private static Craft? current;
        private static FieldInfo amountEnabled = null!;
        private static FieldInfo equipmentEnabled = null!;
        private static FieldInfo range = null!;
        private static FieldInfo leaveOne = null!;
        private static FieldInfo craftRecipe = null!;
        private static FieldInfo craftUpgrade = null!;
        private static FieldInfo multiCrafting = null!;
        private static MethodInfo shouldPrevent = null!;
        private static MethodInfo query = null!;
        private static MethodInfo canPull = null!;
        private static MethodInfo containerInventory = null!;
        private static MethodInfo containerPrefab = null!;
        private static MethodInfo containerSave = null!;
        private static MethodInfo inventoryChanged = null!;
        private static bool previewFailed;

        internal static void Install(Assembly impact, Assembly crafty, bool hasAaa)
        {
            Type config = Guard.Type(impact, "ImpactfulSkills.ValConfig");
            amountEnabled = RequiredConfig(config, "EnableQualityIngredientScaling", typeof(ConfigEntry<bool>));
            equipmentEnabled = RequiredConfig(config, "ScaleCraftedEquipmentQuality", typeof(ConfigEntry<bool>));
            Type craftyPlugin = Guard.Type(crafty, "AzuCraftyBoxes.AzuCraftyBoxesPlugin");
            range = RequiredConfig(craftyPlugin, "mRange", typeof(ConfigEntry<float>));
            leaveOne = AccessTools.Field(craftyPlugin, "leaveOne")
                ?? throw new MissingFieldException("CraftyBoxes.leaveOne");
            Type functions = Guard.Type(crafty, "AzuCraftyBoxes.Util.Functions.MiscFunctions");
            shouldPrevent = Guard.Method(functions, "ShouldPrevent", typeof(bool));
            Type boxes = Guard.Type(crafty, "AzuCraftyBoxes.Util.Functions.Boxes");
            canPull = Guard.Method(boxes, "CanItemBePulled", typeof(bool), typeof(string), typeof(string), typeof(string));
            Type frame = Guard.Type(crafty, "AzuCraftyBoxes.Util.Functions.Boxes+QueryFrame");
            query = frame.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(m => m.Name == "Get" && m.IsGenericMethodDefinition).MakeGenericMethod(typeof(Player));
            Type container = Guard.Type(crafty, "AzuCraftyBoxes.IContainers.IContainer");
            containerInventory = Guard.Method(container, "GetInventory", typeof(Inventory));
            containerPrefab = Guard.Method(container, "GetPrefabName", typeof(string));
            containerSave = Guard.Method(container, "Save", typeof(void));
            MethodInfo process = Guard.Method(functions, "ProcessRequirements", typeof(void),
                typeof(Piece.Requirement[]), typeof(int), typeof(Inventory), typeof(List<>).MakeGenericType(container),
                typeof(int), typeof(int), typeof(CraftingStation));
            Type ingredient = Guard.Type(impact, "ImpactfulSkills.IngredientQuality");
            MethodInfo prototypeTier = Guard.Method(ingredient, "SelectTier", typeof(int),
                typeof(Inventory), typeof(ItemDrop.ItemData), typeof(int));
            MethodInfo namedTier = Guard.Method(ingredient, "SelectTier", typeof(int),
                typeof(Inventory), typeof(string), typeof(int), typeof(int));
            MethodInfo panelMultiplier = Guard.Method(ingredient, "PanelCraftMultiplier", typeof(int));
            MethodInfo crafting = Guard.Method(typeof(InventoryGui), "DoCrafting", typeof(void), typeof(Player));
            MethodInfo add = Guard.Method(typeof(Inventory), "AddItem", typeof(ItemDrop.ItemData),
                typeof(string), typeof(int), typeof(int), typeof(int), typeof(long), typeof(string),
                typeof(Vector2i), typeof(bool), typeof(bool), typeof(bool));
            craftRecipe = AccessTools.Field(typeof(InventoryGui), "m_craftRecipe");
            craftUpgrade = AccessTools.Field(typeof(InventoryGui), "m_craftUpgradeItem");
            multiCrafting = AccessTools.Field(typeof(InventoryGui), "m_multiCrafting");
            inventoryChanged = Guard.Method(typeof(Inventory), "Changed", typeof(void), typeof(bool), typeof(bool));

            var harmony = new Harmony(Plugin.Guid + ".Quality");
            var prepare = Guard.Hook(typeof(QualityPatch), nameof(Prepare), Priority.First);
            prepare.before = new[] { "MidnightsFX.ImpactfulSkills" };
            harmony.Patch(crafting, prefix: prepare, finalizer: Guard.Hook(typeof(QualityPatch), nameof(Finish)));
            harmony.Patch(prototypeTier, prefix: Guard.Hook(typeof(QualityPatch), nameof(PrototypeTier)));
            harmony.Patch(namedTier, prefix: Guard.Hook(typeof(QualityPatch), nameof(NamedTier)));
            harmony.Patch(process, prefix: Guard.Hook(typeof(QualityPatch), nameof(Consume)));
            var reserve = Guard.Hook(typeof(QualityPatch), nameof(Reserve), Priority.Last);
            reserve.after = new[] { "MidnightsFX.ImpactfulSkills" };
            harmony.Patch(add, prefix: reserve, postfix: Guard.Hook(typeof(QualityPatch), nameof(Added)),
                finalizer: Guard.Hook(typeof(QualityPatch), nameof(AddFailed)));
            if (hasAaa)
                harmony.Patch(panelMultiplier, prefix: Guard.Hook(typeof(QualityPatch), nameof(PanelMultiplier)));
            foreach (MethodInfo method in new[] { crafting, prototypeTier, namedTier, process, add }) Guard.Registered(method);
            if (hasAaa) Guard.Registered(panelMultiplier);
        }

        private static FieldInfo RequiredConfig(Type type, string name, Type expected)
        {
            FieldInfo? field = AccessTools.Field(type, name);
            if (field == null || field.FieldType != expected) throw new MissingFieldException(type.FullName, name);
            return field;
        }

        private static bool Enabled => ((ConfigEntry<bool>)amountEnabled.GetValue(null)).Value
            || ((ConfigEntry<bool>)equipmentEnabled.GetValue(null)).Value;
        private static bool Pulling => !(bool)shouldPrevent.Invoke(null, null);
        private static bool LeavingOne => Convert.ToInt32(((ConfigEntryBase)leaveOne.GetValue(null)).BoxedValue) != 0;

        private static bool Allowed(Source source, string prefab)
        {
            return source.Container == null || (bool)canPull.Invoke(null,
                new[] { containerPrefab.Invoke(source.Container, null), prefab, "" });
        }

        private static List<ItemStack> Snapshot(Player player)
        {
            var sources = new List<Source> { new Source(player.GetInventory(), null) };
            var seen = new HashSet<Inventory> { player.GetInventory() };
            var containers = (IEnumerable)query.Invoke(null,
                new object[] { player, ((ConfigEntry<float>)range.GetValue(null)).Value });
            foreach (object container in containers)
            {
                // Drawer adapters have no inventory and cannot expose qualities or exact stacks,
                // so quality crafts never draw from them.
                if (containerInventory.Invoke(container, null) is Inventory inventory && seen.Add(inventory))
                    sources.Add(new Source(inventory, container));
            }
            var stacks = new List<ItemStack>();
            foreach (Source source in sources)
                for (int index = 0; index < source.Inventory.GetAllItems().Count; index++)
                {
                    ItemDrop.ItemData item = source.Inventory.GetAllItems()[index];
                    if (item?.m_shared != null && item.m_stack > 0)
                        stacks.Add(new ItemStack(item, source, item.m_shared.m_name,
                            item.m_quality, item.m_worldLevel, item.m_stack, source.Container != null, index));
                }
            return stacks;
        }

        private static ItemNeed Need(Piece.Requirement requirement, int amount)
        {
            ItemDrop.ItemData item = requirement.m_resItem.m_itemData;
            string prefab = requirement.m_resItem.name;
            return new ItemNeed(item.m_shared.m_name, item.m_shared.m_maxQuality, amount,
                source => Allowed(source, prefab));
        }

        private static bool Prepare(InventoryGui __instance, Player __0, out Scope __state)
        {
            __state = new Scope { Previous = current };
            current = null;
            try
            {
                if (!Enabled || !Pulling || __0 != Player.m_localPlayer || __0.NoCostCheat()
                    || ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoCraftCost)) return true;
                var recipe = (Recipe?)craftRecipe.GetValue(__instance);
                if (recipe == null || recipe.m_requireOnlyOneIngredient) return true;
                CraftingStation station = __0.GetCurrentCraftingStation();
                var requirements = recipe.m_resources.Where(r => r?.m_resItem?.m_itemData?.m_shared != null
                    && r.m_upgraderResource == (station != null && station.m_upgrader)).ToList();
                if (!requirements.Any(r => r.m_resItem.m_itemData.m_shared.m_maxQuality > 1)) return true;
                var upgrade = (ItemDrop.ItemData?)craftUpgrade.GetValue(__instance);
                int quality = upgrade == null ? 1 : upgrade.m_quality + 1;
                int multiplier = (bool)multiCrafting.GetValue(__instance) ? __instance.m_multiCraftAmount : 1;
                var needs = requirements.Select(r => Need(r, checked(r.GetAmount(quality) * multiplier))).ToList();
                ItemPlan? plan = IngredientSelector.Select(Snapshot(__0), needs, Game.m_worldLevel, LeavingOne);
                if (plan == null) return false; // Too few eligible ingredients: skip the craft before vanilla removes anything.
                if (plan.Ingredients.GroupBy(i => new { i.Need.Name, i.Need.Count })
                    .Any(group => group.Select(i => i.Tier).Distinct().Count() > 1))
                    throw new NotSupportedException("Repeated identical recipe requirements need different tiers; "
                        + "upstream SelectTier cannot distinguish them.");
                current = new Craft(__0, recipe.m_resources, quality, multiplier, recipe.m_item.gameObject.name, plan);
                __state.Created = current;
                return true;
            }
            catch (Exception exception)
            {
                CompatibilityInstaller.Error("Quality: craft cancelled before output; no safe ingredient plan. " + exception);
                return false;
            }
        }

        private static Exception? Finish(Exception? __exception, Scope __state)
        {
            try
            {
                Craft? craft = __state.Created;
                if (craft?.Reservation.State == ReservationState.Reserved) Rollback(craft);
            }
            finally { current = __state.Previous; }
            return __exception;
        }

        private static bool FindTier(Inventory inventory, string name, int amount, out int tier)
        {
            tier = 0;
            // After commit, vanilla refreshes the panel inside DoCrafting; preview the remaining ingredients.
            if (current == null || inventory != current.Player.GetInventory()
                || current.Reservation.State == ReservationState.Committed
                || current.Reservation.State == ReservationState.RolledBack) return false;
            ItemIngredient? ingredient = current.Plan.Ingredients.FirstOrDefault(i =>
                i.Need.Name == name && i.Need.Count == amount);
            if (ingredient == null) return false;
            tier = ingredient.Tier;
            return true;
        }

        private static bool NamedTier(Inventory __0, string __1, int __3, ref int __result)
        {
            if (!FindTier(__0, __1, __3, out int tier)) return true;
            __result = tier;
            return false;
        }

        private static bool PrototypeTier(Inventory __0, ItemDrop.ItemData __1, int __2, ref int __result)
        {
            if (__1 == null) return true;
            if (FindTier(__0, __1.m_shared.m_name, __2, out int tier)) { __result = tier; return false; }
            if (!Enabled || !Pulling || Player.m_localPlayer == null || __0 != Player.m_localPlayer.GetInventory()) return true;
            try
            {
                // CraftyBoxes assigns requirement drop prefabs; ObjectDB covers a recipe it has not checked yet.
                UnityEngine.GameObject? dropPrefab = __1.m_dropPrefab != null ? __1.m_dropPrefab
                    : ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(__1.m_shared) : null;
                if (dropPrefab == null) return true;
                string prefab = dropPrefab.name;
                var need = new ItemNeed(__1.m_shared.m_name, __1.m_shared.m_maxQuality, __2,
                    source => Allowed(source, prefab));
                ItemPlan? preview = IngredientSelector.Select(Snapshot(Player.m_localPlayer),
                    new[] { need }, Game.m_worldLevel, LeavingOne);
                __result = preview?.Ingredients.FirstOrDefault()?.Tier ?? 0;
                return false;
            }
            catch (Exception exception)
            {
                if (!previewFailed)
                    CompatibilityInstaller.Warning("Quality preview failed; the crafting panel shows no quality bonus "
                        + "while this persists. Later preview failures are not logged. " + exception);
                previewFailed = true;
                __result = 0;
                return false;
            }
        }

        private static int Count(ItemStack stack)
        {
            ItemDrop.ItemData item = stack.Id;
            Source source = stack.Source;
            return source.Inventory.ContainsItem(item) && item.m_quality == stack.Quality
                && item.m_worldLevel == stack.WorldLevel && item.m_shared.m_name == stack.Name ? item.m_stack : 0;
        }

        private static bool Remove(ItemAllocation allocation)
        {
            // Remove like CraftyBoxes' VanillaContainer: by index or stack size. MultiUserChest
            // refuses Inventory.RemoveItem(ItemData) for a chest another client owns.
            Source source = allocation.Stack.Source;
            ItemDrop.ItemData item = allocation.Stack.Id;
            int index = source.Inventory.GetAllItems().IndexOf(item);
            if (index < 0 || item.m_stack < allocation.Count) return false;
            if (item.m_stack == allocation.Count) return source.Inventory.RemoveItem(index);
            item.m_stack -= allocation.Count;
            return true;
        }

        private static bool Valid(Craft craft)
        {
            if (craft.Plan.Ingredients.Any(ingredient => ingredient.Allocations
                .Any(allocation => !ingredient.Need.AllowsSource(allocation.Stack.Source)))) return false;
            if (LeavingOne)
                foreach (var group in craft.Plan.Allocations.Where(a => a.Stack.Container)
                    .GroupBy(a => new { a.Stack.Source, a.Stack.Name }))
                    if (group.Key.Source.Inventory.CountItems(group.Key.Name) <= group.Sum(a => a.Count)) return false;
            return true;
        }

        private static void Restore(ItemStack stack, int count)
        {
            Source source = stack.Source;
            ItemDrop.ItemData item = stack.Id;
            if (!source.Inventory.ContainsItem(item))
                source.Inventory.GetAllItems().Insert(Math.Min(stack.Index, source.Inventory.GetAllItems().Count), item);
            item.m_stack = count;
        }

        private static void Save(Craft craft)
        {
            foreach (object container in craft.Plan.Allocations.Select(a => a.Stack.Source.Container)
                .OfType<object>().Distinct())
                containerSave.Invoke(container, null);
            // Output and rollback change the player's inventory even when every ingredient came from chests.
            inventoryChanged.Invoke(craft.Player.GetInventory(), new object[] { false, false });
        }

        private static void Rollback(Craft craft)
        {
            craft.Reservation.Rollback(Restore);
            // Restoring the player's pre-craft inventory also removes partial output and returns
            // an item being upgraded.
            List<ItemDrop.ItemData> items = craft.Player.GetInventory().GetAllItems();
            items.Clear(); items.AddRange(craft.PlayerItems);
            foreach (var entry in craft.PlayerCounts) entry.Key.m_stack = entry.Value;
            Save(craft);
        }

        private static bool Reserve(Inventory __instance, string __0, ref bool __9,
            ref ItemDrop.ItemData? __result, out Craft? __state)
        {
            __state = null;
            Craft? craft = current;
            if (craft == null || __instance != craft.Player.GetInventory() || __0 != craft.Prefab
                || craft.Reservation.State != ReservationState.Planned) return true;
            __state = craft;
            try
            {
                if (!Pulling || !Valid(craft) || !craft.Reservation.Reserve(Count, Remove, Restore))
                {
                    Rollback(craft);
                    __result = null;
                    CompatibilityInstaller.Warning("Quality: ingredients changed; output cancelled without consumption.");
                    return false;
                }
                // AddItem can fail after partially filling output stacks. Without a ground drop,
                // rollback removes all partial output.
                __9 = false;
                return true;
            }
            catch (Exception exception)
            {
                Rollback(craft);
                __result = null;
                CompatibilityInstaller.Error("Quality: ingredient reservation failed; output cancelled. " + exception);
                return false;
            }
        }

        private static void Added(ItemDrop.ItemData? __result, Craft? __state)
        {
            if (__state?.Reservation.State != ReservationState.Reserved) return;
            if (__result == null) { Rollback(__state); return; }
            __state.Reservation.Complete(true, Restore);
            Save(__state);
        }

        private static Exception? AddFailed(Exception? __exception, Craft? __state)
        {
            if (__exception != null && __state?.Reservation.State == ReservationState.Reserved) Rollback(__state);
            return __exception;
        }

        private static bool Consume(Piece.Requirement[] __0, int __1, Inventory __2, int __4, int __5)
        {
            Craft? craft = current;
            if (craft == null || __0 != craft.Requirements || __1 != craft.Quality || __2 != craft.Player.GetInventory()
                || __4 >= 0 || __5 != craft.Multiplier) return true;
            if (craft.Reservation.State == ReservationState.Committed) return false;
            // The vanilla upgrader can spend ingredients after a failed upgrade with no output.
            if (craft.Reservation.State == ReservationState.Planned && craft.Reservation.Reserve(Count, Remove, Restore))
            {
                craft.Reservation.Complete(true, Restore);
                Save(craft);
                return false;
            }
            throw new InvalidOperationException("Quality ingredient reservation was not committed; refusing a second consumption.");
        }

        private static bool PanelMultiplier(ref int __result)
        {
            // Patched only when AAA is installed. AAA 2.1.11 disables native multicraft and queues individual crafts. Holding Alt
            // or LStick must not make ImpactfulSkills preview a native batch tier in that UI.
            __result = 1;
            return false;
        }
    }
}
