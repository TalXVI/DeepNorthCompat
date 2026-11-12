using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using static DeepNorthCompat.ILMatch;

namespace DeepNorthCompat
{
    internal static class MagicRevampCraftingPatch
    {
        internal const string Group = "MagicRevamp.Crafting";
        internal const string Owner = Plugin.Guid + "." + Group;
        private static readonly MethodInfo capture = AccessTools.DeclaredMethod(typeof(MagicRevampCraftingPatch), nameof(Capture));
        private delegate bool AddAt(Inventory inventory, ItemDrop.ItemData item, int amount, int x, int y, bool skipValidPositionCheck);
        private static MethodInfo add = null!, vendor = null!, database = null!, prefab = null!;
        private static AddAt addAt = null!;
        private static Action<Inventory, bool, bool> changed = null!;
        private static Func<Player, Inventory> spellBook = null!;
        private static bool pending, enabled, rewritten;

        [ThreadStatic] private static Scope? current;

        private sealed class Scope
        {
            internal Scope? Previous;
            internal Inventory Source = null!, Destination = null!;
            internal string Name = "";
            internal int Requested;
            internal Dictionary<ItemDrop.ItemData, int>? Before;
        }

        internal static void Prepare(Assembly? magic, Assembly? backpacks)
        {
            if (pending || enabled || Harmony.HasAnyPatches(Owner)) return;
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
            Type item = typeof(ItemDrop.ItemData);
            add = Guard.Method(typeof(Inventory), "AddItem", item, typeof(string), typeof(int), typeof(int), typeof(int),
                typeof(long), typeof(string), typeof(Vector2i), typeof(bool), typeof(bool), typeof(bool));
            vendor = Guard.Method(Guard.Type(magic, "MagicRevamp.Patches.InventoryPatch"), "AddItem_Postfix",
                typeof(void), typeof(Inventory), item.MakeByRefType());
            addAt = (AddAt)Delegate.CreateDelegate(typeof(AddAt), Guard.Method(typeof(Inventory), "AddItem", typeof(bool),
                item, typeof(int), typeof(int), typeof(int), typeof(bool)));
            changed = (Action<Inventory, bool, bool>)Delegate.CreateDelegate(typeof(Action<Inventory, bool, bool>),
                Guard.Method(typeof(Inventory), "Changed", typeof(void), typeof(bool), typeof(bool)));
            database = Guard.Method(typeof(ObjectDB), "get_instance", typeof(ObjectDB));
            prefab = Guard.Method(typeof(ObjectDB), "GetItemPrefab", typeof(UnityEngine.GameObject), typeof(string));
            MethodInfo book = Guard.Method(Guard.Type(magic, "MagicRevamp.Functions.CycleSpells"),
                "SpellBookContainer", typeof(Inventory), typeof(Player));
            spellBook = (Func<Player, Inventory>)Delegate.CreateDelegate(typeof(Func<Player, Inventory>), book);
            pending = true;
            CompatibilityInstaller.Info($"{Group}: audited builds verified; awaiting vendor Harmony registration.");
        }

        internal static void Verify()
        {
            if (!pending) return;
            pending = false;
            var harmony = new Harmony(Owner);
            try
            {
                Guard.Sole(add, vendor, MagicRevampInventoryPatch.Guid, HarmonyPatchType.Postfix);
                rewritten = false;
                harmony.Patch(add, prefix: Guard.Hook(typeof(MagicRevampCraftingPatch), nameof(Open), Priority.First),
                    transpiler: Guard.Hook(typeof(MagicRevampCraftingPatch), nameof(Rewrite)),
                    finalizer: Guard.Hook(typeof(MagicRevampCraftingPatch), nameof(Finish), Priority.Last));
                harmony.Patch(vendor, prefix: Guard.Hook(typeof(MagicRevampCraftingPatch), nameof(Suppress)));
                Require(add, HarmonyPatchType.Prefix, nameof(Open));
                Require(add, HarmonyPatchType.Transpiler, nameof(Rewrite));
                Require(add, HarmonyPatchType.Finalizer, nameof(Finish));
                Require(vendor, HarmonyPatchType.Prefix, nameof(Suppress));
                RequireOwner(add, 1, 1, 1);
                RequireOwner(vendor, 1, 0, 0);
                if (!rewritten) throw new NotSupportedException("The output method body did not match.");
                enabled = true;
                CompatibilityInstaller.Info($"{Group}: APPLIED; spell output moves from the actual credited inventory stacks.");
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
            enabled = false;
            pending = false;
            rewritten = false;
            current = null;
            new Harmony(Owner).UnpatchSelf();
        }

        private static void Require(MethodInfo target, HarmonyPatchType kind, string name)
        {
            Guard.Sole(target, AccessTools.DeclaredMethod(typeof(MagicRevampCraftingPatch), name), Owner, kind);
        }

        private static void RequireOwner(MethodInfo target, int prefixes, int transpilers, int finalizers)
        {
            Patches? info = Harmony.GetPatchInfo(target);
            if (info == null || info.Prefixes.Count(patch => patch.owner == Owner) != prefixes
                || info.Transpilers.Count(patch => patch.owner == Owner) != transpilers
                || info.Finalizers.Count(patch => patch.owner == Owner) != finalizers
                || info.Postfixes.Concat(info.ILManipulators).Any(patch => patch.owner == Owner))
                throw new NotSupportedException($"Unexpected {Group} hook registration on {Guard.Name(target)}.");
        }

        private static bool Suppress() => !enabled;

        private static void Open(out Scope? __state)
        {
            __state = null;
            if (!enabled) return;
            __state = new Scope { Previous = current };
            current = __state;
        }

        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> original = instructions.ToList();
            List<CodeInstruction> code = original.Select(instruction => new CodeInstruction(instruction)).ToList();
            bool match = code.Count > 0 && Plain(code[0])
                && Shape(code, 0, Calls(database), Is(OpCodes.Ldarg_1), Calls(prefab));
            rewritten = match;
            if (!match)
            {
                Lost(ref enabled, Group, "the output method entry no longer matches");
                return original;
            }

            code.InsertRange(0, new[] { new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(OpCodes.Ldarg_2), new CodeInstruction(OpCodes.Call, capture) });
            return code;
        }

        private static void Capture(Inventory inventory, string name, int requested)
        {
            Scope? scope = current;
            Player player = Player.m_localPlayer;
            if (!enabled || scope == null || !player || inventory != player.GetInventory() || requested <= 0
                || name == null || !(name.StartsWith("BMR_") || name.Contains("Scroll"))) return;
            Inventory? book = spellBook(player);
            if (book == null || ReferenceEquals(book, inventory)) return;

            scope.Source = inventory;
            scope.Destination = book;
            scope.Name = name;
            scope.Requested = requested;
            scope.Before = inventory.GetAllItems().ToDictionary(item => item, item => item.m_stack);
        }

        private static Exception? Finish(Exception? __exception, ItemDrop.ItemData? __result, Scope? __state)
        {
            try
            {
                if (__exception == null && enabled && __state?.Before != null && __result != null
                    && __result.m_shared.m_name.StartsWith("$bmr_scroll_") && __state.Destination.HaveEmptySlot())
                    Transfer(__state, __result);
            }
            finally { if (__state != null) current = __state.Previous; }
            return __exception;
        }

        private static void Transfer(Scope scope, ItemDrop.ItemData result)
        {
            Inventory source = scope.Source, book = scope.Destination;
            Dictionary<ItemDrop.ItemData, int> before = scope.Before!;
            List<ItemDrop.ItemData> after = source.GetAllItems();
            if (before.Any(pair => !after.Contains(pair.Key) || pair.Key.m_stack < pair.Value))
            {
                CompatibilityInstaller.Error($"{Group}: inventory changed while adding {scope.Name}; spellbook transfer skipped.");
                return;
            }
            var credits = after.Select(item => (Item: item, Count: item.m_stack - (before.TryGetValue(item, out int count) ? count : 0)))
                .Where(pair => pair.Count > 0).ToList();
            if (credits.Any(pair => pair.Item.m_shared.m_name != result.m_shared.m_name || pair.Item.m_quality != result.m_quality
                    || pair.Item.m_worldLevel != result.m_worldLevel
                    || (ReferenceEquals(pair.Item, result) && pair.Count != result.m_stack))
                || credits.Sum(pair => pair.Count) > scope.Requested)
            {
                CompatibilityInstaller.Error($"{Group}: output credits for {scope.Name} are ambiguous; spellbook transfer skipped.");
                return;
            }

            int returnedCount = result.m_stack;
            try
            {
                foreach (var credit in credits.OrderByDescending(pair => ReferenceEquals(pair.Item, result)))
                {
                    bool returned = ReferenceEquals(credit.Item, result);
                    int remaining = credit.Count;
                    try
                    {
                        foreach (ItemDrop.ItemData target in book.GetAllItems().ToList())
                        {
                            if (remaining == 0) break;
                            if (target.m_quality != credit.Item.m_quality || !target.IsSameType(credit.Item)) continue;
                            int room = target.m_shared.m_maxStackSize - target.m_stack;
                            if (room <= 0) continue;
                            int amount = Math.Min(remaining, room);
                            ItemDrop.ItemData slice = Slice(credit.Item, amount);
                            if (!SameStored(target.Clone(), slice)) continue;
                            remaining -= Move(source, book, credit.Item, slice, amount, target.m_gridPos);
                        }
                        while (remaining > 0)
                        {
                            Vector2i empty = EmptySlot(book);
                            if (empty.x < 0) break;
                            int moved = Move(source, book, credit.Item, Slice(credit.Item, remaining), remaining, empty);
                            remaining -= moved;
                            if (remaining > 0)
                            {
                                CompatibilityInstaller.Error($"{Group}: output transfer rejected; remaining items stay in player inventory.");
                                break;
                            }
                        }
                    }
                    finally
                    {
                        if (returned && result.m_stack != returnedCount)
                        {
                            int index = after.IndexOf(result);
                            if (index >= 0) after[index] = result.Clone();
                            result.m_stack = returnedCount;
                            if (index >= 0) changed(source, false, false);
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                // Vanilla consumes ordinary ingredients only after AddItem returns its output.
                CompatibilityInstaller.Error($"{Group}: spell output transfer interrupted; inspect the inventory and error. {exception}");
            }
        }

        private static Vector2i EmptySlot(Inventory inventory)
        {
            for (int y = 0; y < inventory.GetHeight(); y++)
                for (int x = 0; x < inventory.GetWidth(); x++)
                    if (inventory.GetItemAt(x, y) == null) return new Vector2i(x, y);
            return new Vector2i(-1, -1);
        }

        private static bool SameStored(ItemDrop.ItemData left, ItemDrop.ItemData right)
        {
            return left.IsSameType(right) && left.m_quality == right.m_quality && left.m_variant == right.m_variant
                && left.m_durability.Equals(right.m_durability) && left.m_pickedUp == right.m_pickedUp
                && left.m_cheated == right.m_cheated && left.m_crafterID == right.m_crafterID
                && left.m_crafterName == right.m_crafterName && ReferenceEquals(left.m_dropPrefab, right.m_dropPrefab)
                && left.m_lastAttackTime.Equals(right.m_lastAttackTime) && ReferenceEquals(left.m_lastProjectile, right.m_lastProjectile)
                && left.m_customData.Count == right.m_customData.Count
                && left.m_customData.All(pair => right.m_customData.TryGetValue(pair.Key, out string value) && value == pair.Value);
        }

        private static ItemDrop.ItemData Slice(ItemDrop.ItemData item, int amount)
        {
            ItemDrop.ItemData slice = item.Clone();
            slice.m_stack = amount;
            slice.m_equipped = false;
            return slice;
        }

        private static int Move(Inventory source, Inventory book, ItemDrop.ItemData item, ItemDrop.ItemData slice, int amount, Vector2i position)
        {
            int before = item.m_stack;
            List<ItemDrop.ItemData> items = source.GetAllItems();
            int index = items.IndexOf(item);
            bool wholeStack = amount == before;
            if (index < 0) throw new InvalidOperationException($"{Group}: source output is no longer present.");
            bool debited = false;
            try
            {
                if (wholeStack)
                {
                    bool removed = false;
                    try { removed = source.RemoveItem(item); }
                    finally { debited = !items.Contains(item); }
                    if (!removed || !debited) return 0;
                }
                else
                {
                    item.m_stack -= amount;
                    debited = true;
                    changed(source, false, false);
                }
                addAt(book, slice, amount, position.x, position.y, false);
            }
            finally
            {
                if (debited && slice.m_stack > 0)
                {
                    if (wholeStack)
                    {
                        item.m_stack = slice.m_stack;
                        items.Insert(Math.Min(index, items.Count), item);
                    }
                    else item.m_stack += slice.m_stack;
                    changed(source, false, false);
                }
            }
            return amount - slice.m_stack;
        }
    }
}
