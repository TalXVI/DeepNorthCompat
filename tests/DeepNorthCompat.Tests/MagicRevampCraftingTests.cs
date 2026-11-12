using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using DeepNorthCompat;
using HarmonyLib;
using UnityEngine;
using ItemData = ItemDrop.ItemData;

// Runs the staged vendor postfix, game inventory methods and Backpacks item-data hooks.
// Unity object creation, prefab lookup and the equipped book lookup are fixtures.
internal static class MagicRevampCraftingTests
{
    private const string Fixture = "DeepNorthCompat.Tests.MagicRevamp.Crafting";
    private const string VendorOwner = "blacks7ar.MagicRevamp";
    private const string ItemManagerOwner = "org.bepinex.helpers.ItemDataManager";
    private static readonly List<string> errors = new List<string>();
    private static readonly ItemData.SharedData shared = new ItemData.SharedData
    {
        m_name = "$bmr_scroll_test", m_maxStackSize = 100, m_itemType = ItemData.ItemType.Ammo
    };
    internal static GameObject prefab = null!;
    internal static ItemDrop prototype = null!;
    internal static Inventory? book;
    internal static bool missing;
    private static GameObject spawned = null!;
    private static ItemDrop produced = null!;
    private static ObjectDB database = null!;
    private static IntPtr nativeId;
    private static ItemData? postfixResult;
    private static int postfixStack, postfixBookCount;
    private static Player player = null!;
    private static MethodInfo add = null!;
    private static Assembly magic = null!, backpacks = null!;
    private static MethodInfo vendorPostfix = null!;
    private static bool rejectRollback;

    internal static void Run(string profile, Action<string, Action> test)
    {
        string plugins = Path.Combine(profile, "BepInEx/plugins");
        string[] magicFiles = Directory.GetFiles(plugins, "MagicRevamp.dll", SearchOption.AllDirectories);
        string[] backpackFiles = Directory.GetFiles(plugins, "Backpacks.dll", SearchOption.AllDirectories);
        if (magicFiles.Length == 0 || backpackFiles.Length == 0)
        {
            test("MagicRevamp crafting remains inactive without its optional composition", () =>
            {
                var messages = new List<string>();
                Action<string> previous = CompatibilityInstaller.Info;
                try
                {
                    CompatibilityInstaller.Info = messages.Add;
                    MagicRevampCraftingPatch.Prepare(null, null); MagicRevampCraftingPatch.Verify();
                    Check(messages.Any(m => m.Contains("absent; inactive")), "absent composition was not reported");
                }
                finally { CompatibilityInstaller.Info = previous; }
            });
            return;
        }
        magic = Assembly.LoadFrom(magicFiles.Single());
        backpacks = Assembly.LoadFrom(backpackFiles.Single());
        if (Environment.GetEnvironmentVariable("DEEPNORTHCOMPAT_TEST_MODE") == "server")
        {
            test("MagicRevamp crafting is inactive on dedicated servers", () =>
            {
                var messages = new List<string>();
                Action<string> previous = CompatibilityInstaller.Info;
                try
                {
                    CompatibilityInstaller.Info = messages.Add;
                    MagicRevampCraftingPatch.Prepare(magic, backpacks); MagicRevampCraftingPatch.Verify();
                    Check(messages.Any(m => m.Contains("inactive on dedicated server")), "dedicated role was not reported");
                }
                finally { CompatibilityInstaller.Info = previous; }
            });
            return;
        }

        string sandbox = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox");
        AccessTools.Method(typeof(BepInEx.Paths), "SetExecutablePath").Invoke(null, new object[]
        {
            typeof(MagicRevampCraftingTests).Assembly.Location, Path.Combine(sandbox, "BepInEx"),
            Environment.GetEnvironmentVariable("DEEPNORTHCOMPAT_MANAGED_PATH")!, new[] { Path.Combine(profile, "BepInEx", "core") }
        });
        Player previousPlayer = Player.m_localPlayer;
        bool previousExists = Player.m_localPlayerExists;
        Action<string> previousInfo = CompatibilityInstaller.Info, previousError = CompatibilityInstaller.Error;
        bool managerAlreadyInstalled = Harmony.HasAnyPatches(ItemManagerOwner);
        FieldInfo managerGuid = AccessTools.Field(backpacks.GetType("ItemDataManager.ItemInfo", true), "_modGuid");
        object? previousManagerGuid = managerGuid.GetValue(null);
        var fixture = new Harmony(Fixture);
        var vendor = new Harmony(VendorOwner);
        nativeId = Marshal.AllocHGlobal(sizeof(long)); Marshal.WriteInt64(nativeId, 1);

        try
        {
            prefab = Fake<GameObject>(); spawned = Fake<GameObject>();
            prototype = Fake<ItemDrop>(); produced = Fake<ItemDrop>(); database = Fake<ObjectDB>();
            prototype.m_itemData = Item(1);
            add = AccessTools.Method(typeof(Inventory), "AddItem", new[] { typeof(string), typeof(int), typeof(int), typeof(int),
                typeof(long), typeof(string), typeof(Vector2i), typeof(bool), typeof(bool), typeof(bool) });
            var instructions = PatchProcessor.GetOriginalInstructions(add);
            MethodInfo getComponent = (MethodInfo)instructions.First(i => i.operand is MethodInfo m && m.Name == "GetComponent").operand;
            MethodInfo instantiate = (MethodInfo)instructions.First(i => i.operand is MethodInfo m && m.Name == "Instantiate").operand;
            fixture.Patch(AccessTools.PropertyGetter(typeof(ObjectDB), "instance"), prefix: Hook(nameof(Database)));
            fixture.Patch(AccessTools.Method(typeof(ObjectDB), "GetItemPrefab", new[] { typeof(string) }), prefix: Hook(nameof(Prefab)));
            fixture.Patch(getComponent, prefix: Hook(nameof(Component)));
            fixture.Patch(instantiate, prefix: Hook(nameof(Instantiate)));
            fixture.Patch(AccessTools.Method(typeof(UnityEngine.Object), "Destroy", new[] { typeof(UnityEngine.Object) }), prefix: Hook(nameof(Skip)));
            fixture.Patch(AccessTools.Method(typeof(ItemDrop), "SetQuality"), prefix: Hook(nameof(SetQuality)));
            fixture.Patch(AccessTools.Method(typeof(ItemData), "GetMaxDurability"), prefix: Hook(nameof(Durability)));
            foreach (string method in new[] { "Log", "LogWarning", "LogError" })
                fixture.Patch(AccessTools.Method(typeof(ZLog), method, new[] { typeof(object) }), prefix: Hook(nameof(Skip)));
            fixture.Patch(AccessTools.PropertyGetter(typeof(UnityEngine.Object), "name"), prefix: Hook(nameof(Name)));
            Type cycle = magic.GetType("MagicRevamp.Functions.CycleSpells", true)!;
            fixture.Patch(AccessTools.Method(cycle, "SpellBookContainer"), prefix: Hook(nameof(Book)));
            player = Fake<Player>(); Player.m_localPlayer = player; Player.m_localPlayerExists = true;
            CompatibilityInstaller.Info = _ => { }; CompatibilityInstaller.Error = errors.Add;
            RuntimeHelpers.RunClassConstructor(backpacks.GetType("ItemDataManager.ItemInfo", true)!.TypeHandle);
            Check(Harmony.HasAnyPatches(ItemManagerOwner), "Backpacks ItemDataManager hooks did not register");
            managerGuid.SetValue(null, "org.bepinex.plugins.backpacks");

            vendorPostfix = AccessTools.Method(magic.GetType("MagicRevamp.Patches.InventoryPatch", true), "AddItem_Postfix");
            test("MagicRevamp crafting rejects a missing vendor postfix", MissingVendor);
            vendor.Patch(add, postfix: new HarmonyMethod(vendorPostfix));
            test("MagicRevamp crafting reproduces vendor full-merge duplication and null crash", VendorBehavior);
            test("MagicRevamp crafting rejects duplicate vendor postfixes", DuplicateVendor);

            MagicRevampCraftingPatch.Prepare(magic, backpacks); MagicRevampCraftingPatch.Verify();
            Check(errors.Count == 0, string.Join("\n", errors));
            fixture.Patch(add, postfix: Hook(nameof(Observe)));

            test("MagicRevamp crafting preserves returned output and later postfix observations", FreshOutput);
            test("MagicRevamp crafting merges compatible fresh output before using a slot", FreshMerge);
            test("MagicRevamp crafting moves merged credits without copying equipped state", EquippedSource);
            test("MagicRevamp crafting preserves differing item metadata", Metadata);
            test("MagicRevamp crafting conserves split credits and capacity overflow", Capacity);
            test("MagicRevamp crafting retains null result and full-book behavior", NullAndFull);
            test("MagicRevamp crafting conserves output when a transfer is refused", RefusedTransfer);
            test("MagicRevamp crafting propagates original add failures", OriginalFailure);
            test("MagicRevamp crafting installs once and verifies all hooks", InstalledOnce);
            test("MagicRevamp crafting disables after a later incompatible transpiler", LaterConflict);
            test("MagicRevamp crafting rejects an incompatible initial transpiler", InitialConflict);
            test("MagicRevamp crafting reports rollback failure", RollbackFailure);
            test("MagicRevamp crafting rejects unsupported builds", Unsupported);

            MagicRevampCraftingIntegrationTests.Run(profile, player, test);
        }
        finally
        {
            missing = false; book = null;
            MagicRevampCraftingPatch.Reset();
            new Harmony(Fixture + ".Conflict").UnpatchSelf();
            new Harmony(Fixture + ".Duplicate").UnpatchSelf();
            new Harmony(Fixture + ".Refusal").UnpatchSelf();
            if (!managerAlreadyInstalled) new Harmony(ItemManagerOwner).UnpatchSelf();
            managerGuid.SetValue(null, previousManagerGuid);
            vendor.UnpatchSelf(); fixture.UnpatchSelf();
            Player.m_localPlayer = previousPlayer; Player.m_localPlayerExists = previousExists;
            CompatibilityInstaller.Info = previousInfo; CompatibilityInstaller.Error = previousError;
            Marshal.FreeHGlobal(nativeId);
        }
    }

    private static void VendorBehavior()
    {
        Inventory source = PlayerInventory(player);
        ItemData old = Item(20); source.GetAllItems().Add(old);
        book = new Inventory("book", null, 3, 1);
        ItemData? result = Output(source);
        Check(result != null && old.m_stack == 25 && book.GetAllItems().Sum(x => x.m_stack) == 5,
            "vendor full merge did not reproduce duplication");
        missing = true;
        try { source.AddItem("missing", 1, 1, 0, 0, "", new Vector2i(-1, -1), false, false, false); throw new Exception("vendor null did not throw"); }
        catch (NullReferenceException) { }
        finally { missing = false; }
    }

    private static void MissingVendor()
    {
        errors.Clear(); MagicRevampCraftingPatch.Prepare(magic, backpacks); MagicRevampCraftingPatch.Verify();
        Check(errors.Count == 1 && errors[0].Contains("NOT APPLIED") && !Harmony.HasAnyPatches(MagicRevampCraftingPatch.Owner),
            "missing vendor hook must leave no crafting hooks");
        errors.Clear();
    }

    private static void DuplicateVendor()
    {
        var duplicate = new Harmony(Fixture + ".Duplicate");
        try
        {
            duplicate.Patch(add, postfix: new HarmonyMethod(vendorPostfix));
            errors.Clear(); MagicRevampCraftingPatch.Prepare(magic, backpacks); MagicRevampCraftingPatch.Verify();
            Check(errors.Count == 1 && errors[0].Contains("NOT APPLIED") && !Harmony.HasAnyPatches(MagicRevampCraftingPatch.Owner),
                "duplicate vendor hook must leave no crafting hooks");
        }
        finally
        {
            duplicate.UnpatchSelf();
            var vendor = new Harmony(VendorOwner);
            vendor.Unpatch(add, HarmonyPatchType.Postfix, VendorOwner);
            vendor.Patch(add, postfix: new HarmonyMethod(vendorPostfix));
            errors.Clear();
        }
    }

    private static void FreshOutput()
    {
        Inventory source = PlayerInventory(player); book = new Inventory("book", null, 3, 1);
        ItemData? result = Output(source, variant: 3);
        Check(result != null && result.m_stack == 5 && source.GetAllItems().Count == 0 && book.GetAllItems().Single().m_stack == 5
            && book.GetAllItems().Single().m_variant == 3 && book.GetAllItems().Single().m_crafterName == "test", "fresh output or metadata changed");
        Check(ReferenceEquals(postfixResult, result) && postfixStack == 5 && postfixBookCount == 0,
            "existing postfix did not observe original output before transfer");
    }

    private static void FreshMerge()
    {
        foreach (int targetCount in new[] { 10, 98 })
        {
            Inventory source = PlayerInventory(player); book = new Inventory("book", null, 2, 1);
            ItemData target = Item(targetCount);
            target.m_shared = new ItemData.SharedData { m_name = shared.m_name, m_maxStackSize = 100, m_itemType = shared.m_itemType };
            target.m_durability = 10; target.m_crafterID = 100; target.m_crafterName = "test";
            book.GetAllItems().Add(target);
            ItemData? result = Output(source);
            int merged = Math.Min(5, 100 - targetCount);
            Check(result != null && result.m_stack == 5 && result.m_crafterID == 100 && result.m_crafterName == "test"
                && ReferenceEquals(postfixResult, result) && target.m_stack == targetCount + merged
                && book.GetAllItems().Sum(item => item.m_stack) == targetCount + 5
                && book.GetAllItems().Count == (merged == 5 ? 1 : 2) && source.GetAllItems().Count == 0,
                "fresh output did not merge first while preserving the returned object");
        }
    }

    private static void EquippedSource()
    {
        Inventory source = PlayerInventory(player); book = new Inventory("book", null, 3, 1);
        ItemData old = Item(20); old.m_equipped = true; old.m_variant = 7; old.m_customData["mark"] = "stored";
        source.GetAllItems().Add(old);
        ItemData? result = Output(source, variant: 3);
        ItemData moved = book.GetAllItems().Single();
        Check(result != null && old.m_stack == 20 && old.m_equipped && moved.m_stack == 5 && !moved.m_equipped
            && moved.m_variant == 7 && moved.m_customData["mark"] == "stored", "merged source state was not preserved");
    }

    private static void Metadata()
    {
        Inventory original = PlayerInventory(player); book = new Inventory("book", null, 3, 1);
        ItemData existing = Item(20), stored = Item(10); original.GetAllItems().Add(existing); book.GetAllItems().Add(stored);
        Type extensions = backpacks.GetType("ItemDataManager.ItemExtensions", true)!;
        Type infoType = backpacks.GetType("ItemDataManager.ItemInfo", true)!;
        object info = AccessTools.Method(extensions, "Data", new[] { typeof(ItemData) }).Invoke(null, new object[] { stored });
        infoType.GetProperty("Item")!.SetValue(info, "blocked", new object[] { "test" });
        Check(Output(original) != null && existing.m_stack == 20 && stored.m_stack == 10
            && book.GetAllItems().Count == 2 && book.GetAllItems().Sum(item => item.m_stack) == 15,
            "Backpacks metadata must prevent merging different stored items");

        foreach (Action<ItemData> difference in new Action<ItemData>[]
        {
            item => item.m_variant++, item => item.m_crafterID++, item => item.m_crafterName = "other",
            item => item.m_durability++, item => item.m_pickedUp = !item.m_pickedUp,
            item => item.m_cheated = !item.m_cheated, item => item.m_customData["mark"] = "other"
        })
        {
            Inventory source = PlayerInventory(player); book = new Inventory("book", null, 3, 1);
            ItemData old = Item(20); old.m_crafterName = "old"; old.m_customData["mark"] = "original"; source.GetAllItems().Add(old);
            ItemData target = old.Clone(); target.m_stack = 10; difference(target); book.GetAllItems().Add(target);
            ItemData? result = Output(source);
            ItemData separate = book.GetAllItems().Single(item => !ReferenceEquals(item, target));
            Check(result != null && old.m_stack == 20 && target.m_stack == 10 && separate.m_stack == 5
                && separate.m_variant == old.m_variant && separate.m_cheated == old.m_cheated
                && separate.m_crafterID == old.m_crafterID && separate.m_crafterName == old.m_crafterName
                && separate.m_durability == old.m_durability && separate.m_pickedUp == old.m_pickedUp
                && separate.m_customData["mark"] == "original", "metadata-different stacks merged");
        }

        Inventory qualitySource = PlayerInventory(player); book = new Inventory("book", null, 3, 1);
        ItemData qualityItem = Item(20), lowerQuality = Item(10);
        qualityItem.m_quality = 2; qualityItem.m_shared.m_maxQuality = 1;
        qualitySource.GetAllItems().Add(qualityItem); book.GetAllItems().Add(lowerQuality);
        ItemData? qualityResult = qualitySource.AddItem("BMR_TestScroll", 5, 2, 0, 100, "test", new Vector2i(-1, -1), false, false, false);
        Check(qualityResult != null && qualityItem.m_stack == 20 && lowerQuality.m_stack == 10 && book.GetAllItems().Count == 2
            && book.GetAllItems().Single(item => item != lowerQuality).m_quality == 2, "different actual qualities merged");
    }

    private static void Capacity()
    {
        Inventory source = PlayerInventory(player); book = new Inventory("book", null, 3, 1);
        ItemData first = Item(98), second = Item(95); first.m_gridPos = new Vector2i(0, 0); second.m_gridPos = new Vector2i(1, 0);
        source.GetAllItems().AddRange(new[] { first, second });
        ItemData? result = Output(source, amount: 8);
        Check(result != null && result.m_stack == 1 && first.m_stack == 98 && second.m_stack == 95
            && book.GetAllItems().Sum(item => item.m_stack) == 8 && source.GetAllItems().Count == 2, "split credits changed");

        source = PlayerInventory(player); book = new Inventory("book", null, 1, 1);
        result = Output(source, amount: 205);
        Check(result != null && result.m_stack == 5 && book.GetAllItems().Sum(item => item.m_stack) == 100
            && source.GetAllItems().Sum(item => item.m_stack) == 105, "partial capacity did not conserve output");
    }

    private static void NullAndFull()
    {
        Inventory source = PlayerInventory(player); book = new Inventory("book", null, 1, 1); book.GetAllItems().Add(Item(1));
        ItemData? result = Output(source);
        Check(result != null && source.GetAllItems().Sum(item => item.m_stack) == 5 && book.GetAllItems().Single().m_stack == 1,
            "full book changed output");
        missing = true;
        try { Check(source.AddItem("missing", 1, 1, 0, 0, "", new Vector2i(-1, -1), false, false, false) == null, "null result changed"); }
        finally { missing = false; }
        source = new Inventory("full", null, 1, 1);
        AccessTools.Field(typeof(Humanoid), "m_inventory").SetValue(player, source);
        ItemData existing = Item(98); source.GetAllItems().Add(existing); book = new Inventory("book", null, 1, 1);
        Check(Output(source) == null && existing.m_stack == 100 && book.GetAllItems().Count == 0,
            "native partial failure must keep its original inventory result");
    }

    private static void RefusedTransfer()
    {
        var refusal = new Harmony(Fixture + ".Refusal");
        try
        {
            MethodInfo remove = AccessTools.Method(typeof(Inventory), "RemoveItem", new[] { typeof(ItemData) });
            MethodInfo addAt = AccessTools.Method(typeof(Inventory), "AddItem", new[] { typeof(ItemData), typeof(int), typeof(int), typeof(int), typeof(bool) });
            foreach (string hook in new[] { nameof(DenyRemove), nameof(DenyDestination), nameof(PartiallyAccept),
                nameof(ThrowDestination), nameof(PartiallyAcceptThenThrow) })
            {
                Inventory source = PlayerInventory(player); book = new Inventory("book", null, 1, 1);
                refusal.Patch(hook == nameof(DenyRemove) ? remove : addAt, prefix: Hook(hook));
                errors.Clear(); ItemData? result = Output(source);
                bool partial = hook == nameof(PartiallyAccept) || hook == nameof(PartiallyAcceptThenThrow);
                bool interrupted = hook == nameof(ThrowDestination) || hook == nameof(PartiallyAcceptThenThrow);
                Check(result != null && result.m_stack == 5 && result.m_crafterID == 100 && ReferenceEquals(result, postfixResult)
                    && source.GetAllItems().Single().m_stack == (partial ? 3 : 5)
                    && book.GetAllItems().Sum(item => item.m_stack) == (partial ? 2 : 0)
                    && ReferenceEquals(result, source.GetAllItems().Single()) != partial
                    && errors.Count == 1 && errors[0].Contains(interrupted ? "spell output transfer interrupted" : "output transfer rejected")
                    && (!interrupted || errors[0].Contains(partial ? "partial destination failure" : "destination failure")),
                    hook + " must conserve output, preserve the return, and report the refusal");
                refusal.UnpatchSelf();
            }
        }
        finally { refusal.UnpatchSelf(); errors.Clear(); }
    }

    private static void OriginalFailure()
    {
        var refusal = new Harmony(Fixture + ".Refusal");
        try
        {
            Inventory source = PlayerInventory(player); book = new Inventory("book", null, 1, 1);
            HarmonyMethod failure = Hook(nameof(ThrowPrefab)); failure.priority = Priority.First;
            refusal.Patch(AccessTools.Method(typeof(ObjectDB), "GetItemPrefab", new[] { typeof(string) }), prefix: failure);
            errors.Clear();
            try { Output(source); throw new Exception("original failure was suppressed"); }
            catch (InvalidOperationException error) when (error.Message == "original add failure") { }
            Check(errors.Count == 0 && source.GetAllItems().Count == 0 && book.GetAllItems().Count == 0,
                "original failure must propagate without transfer or transfer diagnostic");
        }
        finally { refusal.UnpatchSelf(); errors.Clear(); }
    }

    private static void InstalledOnce()
    {
        errors.Clear(); MagicRevampCraftingPatch.Prepare(magic, backpacks); MagicRevampCraftingPatch.Verify();
        Patches addHooks = Harmony.GetPatchInfo(add), vendorHooks = Harmony.GetPatchInfo(vendorPostfix);
        Check(errors.Count == 0 && addHooks.Prefixes.Count(p => p.owner == MagicRevampCraftingPatch.Owner) == 1
            && addHooks.Transpilers.Count(p => p.owner == MagicRevampCraftingPatch.Owner) == 1
            && addHooks.Finalizers.Count(p => p.owner == MagicRevampCraftingPatch.Owner) == 1
            && vendorHooks.Prefixes.Count(p => p.owner == MagicRevampCraftingPatch.Owner) == 1,
            "repeated installation must retain exactly the four owned hooks");
    }

    private static void LaterConflict()
    {
        var conflict = new Harmony(Fixture + ".Conflict");
        try
        {
            errors.Clear();
            conflict.Patch(add, transpiler: new HarmonyMethod(AccessTools.Method(typeof(MagicRevampCraftingTests), nameof(AlterEntry)))
                { priority = Priority.First });
            Check(errors.Count == 1 && errors[0].Contains("NO LONGER APPLIED"), "later conflict must disable the group");
            VendorBehavior();
            MagicRevampCraftingPatch.Prepare(magic, backpacks); MagicRevampCraftingPatch.Verify();
            Check(errors.Count == 1 && Harmony.GetPatchInfo(add).Transpilers.Count(p => p.owner == MagicRevampCraftingPatch.Owner) == 1,
                "repeat installation after conflict must not duplicate or reactivate hooks");
            VendorBehavior();
        }
        finally { conflict.UnpatchSelf(); errors.Clear(); }
    }

    private static void InitialConflict()
    {
        MagicRevampCraftingPatch.Reset();
        var conflict = new Harmony(Fixture + ".Conflict");
        try
        {
            conflict.Patch(add, transpiler: new HarmonyMethod(AccessTools.Method(typeof(MagicRevampCraftingTests), nameof(AlterEntry)))
                { priority = Priority.First });
            errors.Clear(); MagicRevampCraftingPatch.Prepare(magic, backpacks); MagicRevampCraftingPatch.Verify();
            Check(errors.Count == 1 && errors[0].Contains("NOT APPLIED") && !Harmony.HasAnyPatches(MagicRevampCraftingPatch.Owner),
                "initial mismatch must remove all owned hooks");
            VendorBehavior();
        }
        finally { conflict.UnpatchSelf(); errors.Clear(); }
    }

    private static void RollbackFailure()
    {
        var rollback = new Harmony(Fixture + ".Rollback");
        var vendor = new Harmony(VendorOwner);
        try
        {
            vendor.Unpatch(add, HarmonyPatchType.Postfix, VendorOwner);
            rollback.Patch(AccessTools.Method(typeof(Harmony), "UnpatchSelf"), prefix: Hook(nameof(RejectRollback)));
            rejectRollback = true; errors.Clear();
            MagicRevampCraftingPatch.Prepare(magic, backpacks); MagicRevampCraftingPatch.Verify();
            Check(errors.Count == 1 && errors[0].Contains("rollback failed"), "rollback failure must be reported");
        }
        finally
        {
            rejectRollback = false; rollback.UnpatchSelf(); errors.Clear();
            MagicRevampCraftingPatch.Reset(); vendor.Patch(add, postfix: new HarmonyMethod(vendorPostfix));
        }
    }

    private static void Unsupported()
    {
        MagicRevampCraftingPatch.Reset();
        try { MagicRevampCraftingPatch.Prepare(typeof(MagicRevampCraftingTests).Assembly, backpacks); throw new Exception("unsupported build accepted"); }
        catch (NotSupportedException) { }
        MagicRevampCraftingPatch.Prepare(magic, backpacks); MagicRevampCraftingPatch.Verify();
    }

    internal static Inventory PlayerInventory(Player owner)
    {
        var inventory = new Inventory("player", null, 8, 1);
        AccessTools.Field(typeof(Humanoid), "m_inventory").SetValue(owner, inventory);
        return inventory;
    }

    internal static T Fake<T>() where T : UnityEngine.Object
    {
        var value = (T)FormatterServices.GetUninitializedObject(typeof(T));
        AccessTools.Field(typeof(UnityEngine.Object), "m_CachedPtr").SetValue(value, nativeId);
        return value;
    }

    internal static ItemData Item(int stack) => new ItemData { m_shared = shared, m_stack = stack, m_quality = 1, m_dropPrefab = prefab };
    private static ItemData? Output(Inventory inventory, int amount = 5, int variant = 0) =>
        inventory.AddItem("BMR_TestScroll", amount, 1, variant, 100, "test", new Vector2i(-1, -1), false, false, false);
    private static HarmonyMethod Hook(string name) => new HarmonyMethod(AccessTools.Method(typeof(MagicRevampCraftingTests), name));
    private static bool Database(ref ObjectDB __result) { __result = database; return false; }
    private static bool Prefab(ref GameObject? __result) { __result = missing ? null : prefab; return false; }
    private static bool Component(GameObject __instance, ref ItemDrop __result) { __result = ReferenceEquals(__instance, prefab) ? prototype : produced; return false; }
    private static bool Instantiate(ref GameObject __result) { produced.m_itemData = Item(1); __result = spawned; return false; }
    private static bool SetQuality(ItemDrop __instance, int __0) { __instance.m_itemData.m_quality = __0; return false; }
    private static bool Durability(ref float __result) { __result = 10; return false; }
    private static bool Book(ref Inventory? __result) { __result = book; return false; }
    private static bool Name(ref string __result) { __result = "BMR_TestScroll"; return false; }
    private static bool Skip() => false;
    private static void Observe(ItemData? __result)
    {
        postfixResult = __result; postfixStack = __result?.m_stack ?? 0; postfixBookCount = book?.GetAllItems().Count ?? 0;
    }
    private static bool DenyDestination(Inventory __instance, ref bool __result)
    {
        if (!ReferenceEquals(__instance, book)) return true;
        __result = false; return false;
    }
    private static bool DenyRemove(Inventory __instance, ref bool __result)
    {
        if (!ReferenceEquals(__instance, Player.m_localPlayer.GetInventory())) return true;
        __result = false; return false;
    }
    private static bool PartiallyAccept(Inventory __instance, ItemData __0, ref bool __result)
    {
        if (!ReferenceEquals(__instance, book)) return true;
        ItemData accepted = __0.Clone(); accepted.m_stack = 2; __0.m_stack -= 2;
        __instance.GetAllItems().Add(accepted); __result = false; return false;
    }
    private static bool ThrowDestination(Inventory __instance)
    {
        if (ReferenceEquals(__instance, book)) throw new InvalidOperationException("destination failure");
        return true;
    }
    private static bool PartiallyAcceptThenThrow(Inventory __instance, ItemData __0, ref bool __result)
    {
        if (!ReferenceEquals(__instance, book)) return true;
        PartiallyAccept(__instance, __0, ref __result);
        throw new InvalidOperationException("partial destination failure");
    }
    private static void ThrowPrefab() => throw new InvalidOperationException("original add failure");
    private static void RejectRollback(Harmony __instance)
    {
        if (rejectRollback && __instance.Id == MagicRevampCraftingPatch.Owner)
            throw new InvalidOperationException("injected rollback failure");
    }
    private static IEnumerable<CodeInstruction> AlterEntry(IEnumerable<CodeInstruction> instructions) =>
        new[] { new CodeInstruction(OpCodes.Nop) }.Concat(instructions);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
