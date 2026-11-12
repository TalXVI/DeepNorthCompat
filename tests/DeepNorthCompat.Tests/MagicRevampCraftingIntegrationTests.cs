using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using AzuCraftyBoxes.IContainers;
using BepInEx.Configuration;
using DeepNorthCompat;
using HarmonyLib;
using UnityEngine;

// Runs the real quality and equipment hooks with synthetic Unity scene, item prefab,
// and container-discovery boundaries. This is not a rendered crafting session.
internal static class MagicRevampCraftingIntegrationTests
{
    private const string Fixture = "DeepNorthCompat.Tests.MagicRevamp.CraftingIntegration";
    private static readonly List<IContainer> containers = new List<IContainer>();
    private static ItemDrop.ItemData? observed;
    private static Action? beforeOutput;

    internal static void Run(string profile, Player player, Action<string, Action> test)
    {
        string plugins = Path.Combine(profile, "BepInEx/plugins");
        string[] impactFiles = Directory.GetFiles(plugins, "ImpactfulSkills.dll", SearchOption.AllDirectories);
        string[] epiFiles = Directory.GetFiles(plugins, "AzuExtendedPlayerInventory.dll", SearchOption.AllDirectories);
        if (impactFiles.Length != 0) Impact(Assembly.LoadFrom(impactFiles.Single()), player, test);
        if (epiFiles.Length != 0) Azu(Assembly.LoadFrom(epiFiles.Single()), player, test);
        if (impactFiles.Length != 0) Quality(Assembly.LoadFrom(impactFiles.Single()), player, test);
    }

    private static void Impact(Assembly impact, Player player, Action<string, Action> test)
    {
        var fixture = new Harmony(Fixture);
        var owner = new Harmony("MidnightMods.ImpactfulSkills");
        MethodInfo add = AccessTools.Method(typeof(Inventory), "AddItem", new[] { typeof(string), typeof(int), typeof(int), typeof(int),
            typeof(long), typeof(string), typeof(Vector2i), typeof(bool), typeof(bool), typeof(bool) });
        Type crafting = impact.GetType("ImpactfulSkills.patches.Crafting", true)!;
        Type quality = crafting.GetNestedType("CraftedItemQualityAddPatch", BindingFlags.Public | BindingFlags.NonPublic)!;
        Type forged = impact.GetType("ImpactfulSkills.patches.ForgedItems", true)!;
        Type forgedHook = forged.GetNestedType("ForgedCraftAddItemPatch", BindingFlags.Public | BindingFlags.NonPublic)!;
        Type context = forged.GetNestedType("CraftContext", BindingFlags.Public | BindingFlags.NonPublic)!;
        FieldInfo pendingField = AccessTools.Field(forged, "PendingCraft");
        FieldInfo qualityField = AccessTools.Field(crafting, "PendingCraftQuality");
        FieldInfo prefabField = AccessTools.Field(crafting, "PendingCraftPrefab");
        object? oldPending = pendingField.GetValue(null), oldQuality = qualityField.GetValue(null), oldPrefab = prefabField.GetValue(null);
        try
        {
            owner.Patch(add, prefix: new HarmonyMethod(AccessTools.Method(quality, "Prefix")));
            owner.Patch(add, prefix: new HarmonyMethod(AccessTools.Method(forgedHook, "Prefix")),
                postfix: new HarmonyMethod(AccessTools.Method(forgedHook, "Postfix")));
            fixture.Patch(AccessTools.Method(impact.GetType("ImpactfulSkills.Logger", true), "LogDebug"), prefix: Hook(nameof(Skip)));
            test("MagicRevamp crafting preserves ImpactfulSkills quality and forged metadata", () =>
            {
                Inventory source = MagicRevampCraftingTests.PlayerInventory(player);
                MagicRevampCraftingTests.book = new Inventory("book", null, 3, 1);
                object pending = FormatterServices.GetUninitializedObject(context);
                AccessTools.Field(context, "PrefabName").SetValue(pending, "BMR_TestScroll");
                AccessTools.Field(context, "AwaitingItem").SetValue(pending, true);
                AccessTools.Field(context, "Forgeable").SetValue(pending, true);
                AccessTools.Field(context, "HadMasterwork").SetValue(pending, true);
                AccessTools.Field(context, "HadLightweight").SetValue(pending, true);
                pendingField.SetValue(null, pending); qualityField.SetValue(null, 2); prefabField.SetValue(null, "BMR_TestScroll");
                ItemDrop.ItemData result = source.AddItem("BMR_TestScroll", 5, 1, 0, 100, "test", new Vector2i(-1, -1), false, false, false);
                ItemDrop.ItemData moved = MagicRevampCraftingTests.book.GetAllItems().Single();
                Check(result != null && result.m_quality == 2 && moved.m_quality == 2
                    && moved.m_customData["MidnightsFX.ImpactfulSkills.Masterwork"] == "1"
                    && moved.m_customData["MidnightsFX.ImpactfulSkills.Lightweight"] == "1", "crafted quality or forged metadata changed");
            });
        }
        finally
        {
            pendingField.SetValue(null, oldPending); qualityField.SetValue(null, oldQuality); prefabField.SetValue(null, oldPrefab);
            owner.UnpatchSelf(); fixture.UnpatchSelf();
        }
    }

    private static void Azu(Assembly epi, Player player, Action<string, Action> test)
    {
        var fixture = new Harmony(Fixture);
        var owner = new Harmony("Azumatt.AzuExtendedPlayerInventory");
        FieldInfo helper = AccessTools.Field(typeof(BepInEx.ThreadingHelper), "<Instance>k__BackingField");
        object? previousHelper = helper.GetValue(null);
        Type plugin = epi.GetType("AzuEPI.AzuExtendedPlayerInventoryPlugin", true)!;
        FieldInfo rowField = AccessTools.Field(plugin, "AddEquipmentRow");
        object? previousRow = null;
        bool rowRead = false;
        try
        {
            fixture.Patch(AccessTools.Method(typeof(BepInEx.ThreadingHelper), "StartSyncInvoke"), prefix: Hook(nameof(Skip)));
            helper.SetValue(null, MagicRevampCraftingTests.Fake<BepInEx.ThreadingHelper>());
            previousRow = rowField.GetValue(null); rowRead = true;
            Type toggle = plugin.GetNestedType("Toggle", BindingFlags.Public | BindingFlags.NonPublic)!;
            var config = new ConfigFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox/azu-crafting-unsaved.cfg"), false)
                { SaveOnConfigSet = false };
            MethodInfo bind = typeof(ConfigFile).GetMethods().Single(method => method.Name == "Bind" && method.IsGenericMethodDefinition
                && method.GetParameters().Length == 3 && method.GetParameters()[0].ParameterType == typeof(ConfigDefinition));
            rowField.SetValue(null, bind.MakeGenericMethod(toggle).Invoke(config,
                new object[] { new ConfigDefinition("fixture", "row"), Enum.ToObject(toggle, 1), new ConfigDescription("") }));
            Type patches = epi.GetType("AzuEPI.Game.Patches.InventoryPatches", true)!;
            Type pos = patches.GetNestedType("AddItem_Pos_Guard_Patch", BindingFlags.Public | BindingFlags.NonPublic)!;
            Type xy = patches.GetNestedType("AddItem_XY_Guard_Patch", BindingFlags.Public | BindingFlags.NonPublic)!;
            owner.Patch(AccessTools.Method(typeof(Inventory), "AddItem", new[] { typeof(ItemDrop.ItemData), typeof(Vector2i) }),
                prefix: new HarmonyMethod(AccessTools.Method(pos, "Prefix")) { priority = Priority.First });
            owner.Patch(AccessTools.Method(typeof(Inventory), "AddItem", new[] { typeof(ItemDrop.ItemData), typeof(int), typeof(int), typeof(int), typeof(bool) }),
                prefix: new HarmonyMethod(AccessTools.Method(xy, "Prefix")) { priority = Priority.Last });
            Type extensions = epi.GetType("AzuEPI.Core.InventoryHandlers.InventoryExtensions", true)!;
            fixture.Patch(AccessTools.Method(extensions, "IsHiddenCell"), prefix: Hook(nameof(False)));
            fixture.Patch(AccessTools.Method(epi.GetType("AzuEPI.API", true), "TryGetSlotIndexAtGridPos"), prefix: Hook(nameof(NoSlot)));
            test("MagicRevamp crafting works with AzuEPI positioned-add guards", () =>
            {
                Inventory source = MagicRevampCraftingTests.PlayerInventory(player);
                MagicRevampCraftingTests.book = new Inventory("book", null, 3, 1);
                ItemDrop.ItemData result = source.AddItem("BMR_TestScroll", 5, 1, 2, 100, "test", new Vector2i(2, 0), false, false, false);
                Check(result != null && result.m_gridPos.x == 2 && result.m_stack == 5 && source.GetAllItems().Count == 0
                    && MagicRevampCraftingTests.book.GetAllItems().Single().m_stack == 5, "positioned output changed");
            });
        }
        finally { if (rowRead) rowField.SetValue(null, previousRow); helper.SetValue(null, previousHelper); owner.UnpatchSelf(); fixture.UnpatchSelf(); }
    }

    private static void Quality(Assembly impact, Player player, Action<string, Action> test)
    {
        Assembly crafty = typeof(IContainer).Assembly;
        var fixture = new Harmony(Fixture);
        int previousMaxQuality = MagicRevampCraftingTests.prototype.m_itemData.m_shared.m_maxQuality;
        FieldInfo helper = AccessTools.Field(typeof(BepInEx.ThreadingHelper), "<Instance>k__BackingField");
        object? previousHelper = helper.GetValue(null);
        Type impactConfig = impact.GetType("ImpactfulSkills.ValConfig", true)!;
        Type plugin = crafty.GetType("AzuCraftyBoxes.AzuCraftyBoxesPlugin", true)!;
        FieldInfo[] fields = { AccessTools.Field(impactConfig, "EnableQualityIngredientScaling"),
            AccessTools.Field(impactConfig, "ScaleCraftedEquipmentQuality"), AccessTools.Field(plugin, "mRange"), AccessTools.Field(plugin, "leaveOne") };
        object?[]? previous = null;
        FieldInfo zoneField = (FieldInfo)PatchProcessor.GetOriginalInstructions(AccessTools.PropertyGetter(typeof(ZoneSystem), "instance"))
            .First(instruction => instruction.opcode == OpCodes.Ldsfld).operand;
        object? previousZone = zoneField.GetValue(null);
        try
        {
            fixture.Patch(AccessTools.Method(typeof(BepInEx.ThreadingHelper), "StartSyncInvoke"), prefix: Hook(nameof(Skip)));
            helper.SetValue(null, MagicRevampCraftingTests.Fake<BepInEx.ThreadingHelper>());
            previous = fields.Select(field => field.GetValue(null)).ToArray();
            Hook(fixture, typeof(Player), "GetCurrentCraftingStation", nameof(NoStation));
            Hook(fixture, typeof(Player), "NoCostCheat", nameof(False));
            fixture.Patch(AccessTools.Method(typeof(ZoneSystem), "GetGlobalKey", new[] { typeof(GlobalKeys) }), prefix: Hook(nameof(False)));
            Hook(fixture, typeof(Component), "get_gameObject", nameof(GameObject));
            Hook(fixture, crafty.GetType("AzuCraftyBoxes.Util.Functions.MiscFunctions", true)!, "ShouldPrevent", nameof(False));
            Type frame = crafty.GetType("AzuCraftyBoxes.Util.Functions.Boxes+QueryFrame", true)!;
            fixture.Patch(AccessTools.Method(frame, "Get").MakeGenericMethod(typeof(Player)), prefix: Hook(nameof(Query)));
            MethodInfo craft = AccessTools.Method(typeof(InventoryGui), "DoCrafting");
            fixture.Patch(craft, transpiler: new HarmonyMethod(AccessTools.Method(typeof(MagicRevampCraftingIntegrationTests), nameof(CraftBody)))
                { priority = Priority.First });
            QualityPatch.Install(impact, crafty, false);
            var config = new ConfigFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox/quality-crafting-unsaved.cfg"), false)
                { SaveOnConfigSet = false };
            fields[0].SetValue(null, config.Bind("fixture", "amount", true));
            fields[1].SetValue(null, config.Bind("fixture", "equipment", true));
            fields[2].SetValue(null, config.Bind("fixture", "range", 60f));
            Type toggle = plugin.GetNestedType("Toggle", BindingFlags.Public | BindingFlags.NonPublic)!;
            MethodInfo bind = typeof(ConfigFile).GetMethods().Single(method => method.Name == "Bind" && method.IsGenericMethodDefinition
                && method.GetParameters().Length == 3 && method.GetParameters()[0].ParameterType == typeof(ConfigDefinition));
            fields[3].SetValue(null, bind.MakeGenericMethod(toggle).Invoke(config,
                new object[] { new ConfigDefinition("fixture", "leave"), Enum.ToObject(toggle, 0), new ConfigDescription("") }));
            zoneField.SetValue(null, MagicRevampCraftingTests.Fake<ZoneSystem>());

            Inventory inventory = MagicRevampCraftingTests.PlayerInventory(player);
            MagicRevampCraftingTests.book = new Inventory("book", null, 3, 1);
            var gui = MagicRevampCraftingTests.Fake<InventoryGui>();
            var recipe = MagicRevampCraftingTests.Fake<Recipe>();
            var resource = MagicRevampCraftingTests.Fake<ItemDrop>();
            resource.m_itemData = MagicRevampCraftingTests.Item(1); resource.m_itemData.m_shared.m_maxQuality = 2;
            recipe.m_resources = new[] { new Piece.Requirement { m_resItem = resource, m_amount = 5 } };
            recipe.m_item = MagicRevampCraftingTests.prototype;
            AccessTools.Field(typeof(InventoryGui), "m_craftRecipe").SetValue(gui, recipe);

            test("MagicRevamp crafting follows quality reservation for same-name input", () =>
            {
                inventory.GetAllItems().Clear(); MagicRevampCraftingTests.book.GetAllItems().Clear();
                ItemDrop.ItemData ingredient = MagicRevampCraftingTests.Item(10); inventory.GetAllItems().Add(ingredient); observed = null;
                craft.Invoke(gui, new object[] { player });
                Check(observed != null && ingredient.m_stack == 5 && MagicRevampCraftingTests.book.GetAllItems().Sum(item => item.m_stack) == 5,
                    "quality reservation or output was not conserved");
            });
            test("MagicRevamp crafting rolls back a null or invalid quality reservation", () =>
            {
                inventory.GetAllItems().Clear(); MagicRevampCraftingTests.book.GetAllItems().Clear();
                ItemDrop.ItemData ingredient = MagicRevampCraftingTests.Item(10); inventory.GetAllItems().Add(ingredient);
                MagicRevampCraftingTests.missing = true; observed = null;
                try { craft.Invoke(gui, new object[] { player }); }
                finally { MagicRevampCraftingTests.missing = false; }
                Check(observed == null && ingredient.m_stack == 10 && MagicRevampCraftingTests.book.GetAllItems().Count == 0,
                    "null output did not roll back");
                inventory.GetAllItems().Clear(); inventory.GetAllItems().Add(ingredient);
                beforeOutput = () => ingredient.m_stack = 1; observed = null;
                try { craft.Invoke(gui, new object[] { player }); }
                finally { beforeOutput = null; }
                Check(observed == null && ingredient.m_stack == 10 && MagicRevampCraftingTests.book.GetAllItems().Count == 0,
                    "changed ingredient did not roll back");
            });
            test("MagicRevamp crafting reserves book ingredients through a Crafty adapter", () =>
            {
                inventory.GetAllItems().Clear(); MagicRevampCraftingTests.book.GetAllItems().Clear();
                ItemDrop.ItemData ingredient = MagicRevampCraftingTests.Item(10);
                MagicRevampCraftingTests.book.GetAllItems().Add(ingredient);
                Type adapterType = crafty.GetType("AzuCraftyBoxes.IContainers.BackpackContainer", true)!;
                Type containerType = adapterType.GetConstructors().Single().GetParameters().Single().ParameterType;
                object synthetic = FormatterServices.GetUninitializedObject(containerType);
                AccessTools.Field(containerType, "Inventory").SetValue(synthetic, MagicRevampCraftingTests.book);
                object adapter = Activator.CreateInstance(adapterType, synthetic);
                fixture.Patch(AccessTools.Method(adapterType, "GetPrefabName"), prefix: Hook(nameof(ContainerName)));
                fixture.Patch(AccessTools.Method(adapterType, "Save"), prefix: Hook(nameof(Skip)));
                Type boxes = crafty.GetType("AzuCraftyBoxes.Util.Functions.Boxes", true)!;
                fixture.Patch(AccessTools.Method(boxes, "CanItemBePulled"), prefix: Hook(nameof(True)));
                containers.Add((IContainer)adapter); observed = null;
                try { craft.Invoke(gui, new object[] { player }); }
                finally { containers.Clear(); }
                Check(observed != null && ingredient.m_stack == 5 && MagicRevampCraftingTests.book.GetAllItems().Sum(item => item.m_stack) == 10
                    && inventory.GetAllItems().Count == 0, "synthetic Crafty adapter did not conserve book input and output");
            });
        }
        finally
        {
            MagicRevampCraftingTests.missing = false; beforeOutput = null; containers.Clear();
            MagicRevampCraftingTests.prototype.m_itemData.m_shared.m_maxQuality = previousMaxQuality;
            zoneField.SetValue(null, previousZone); helper.SetValue(null, previousHelper);
            if (previous != null) for (int i = 0; i < fields.Length; i++) fields[i].SetValue(null, previous[i]);
            new Harmony(QualityPatch.Owner).UnpatchSelf(); fixture.UnpatchSelf();
        }
    }

    private static void Hook(Harmony harmony, Type type, string name, string patch) => harmony.Patch(AccessTools.Method(type, name), prefix: Hook(patch));
    private static HarmonyMethod Hook(string name) => new HarmonyMethod(AccessTools.Method(typeof(MagicRevampCraftingIntegrationTests), name));
    private static bool NoStation(ref CraftingStation? __result) { __result = null; return false; }
    private static bool False(ref bool __result) { __result = false; return false; }
    private static bool True(ref bool __result) { __result = true; return false; }
    private static bool ContainerName(ref string __result) { __result = "BMR_SpellBook"; return false; }
    private static bool NoSlot(out int __2, ref bool __result) { __2 = -1; __result = false; return false; }
    private static bool GameObject(ref GameObject __result) { __result = MagicRevampCraftingTests.prefab; return false; }
    private static bool Query(ref List<IContainer> __result) { __result = containers; return false; }
    private static bool Skip() => false;
    private static IEnumerable<CodeInstruction> CraftBody(IEnumerable<CodeInstruction> _) => new[]
    {
        new CodeInstruction(OpCodes.Ldarg_1),
        new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(MagicRevampCraftingIntegrationTests), nameof(CraftOutput))),
        new CodeInstruction(OpCodes.Ret)
    };
    private static void CraftOutput(Player player)
    {
        beforeOutput?.Invoke();
        observed = player.GetInventory().AddItem("BMR_TestScroll", 5, 1, 0, 100, "test", new Vector2i(-1, -1), false, false, false);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
