using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using AzuCraftyBoxes.IContainers;
using BepInEx.Configuration;
using DeepNorthCompat;
using HarmonyLib;
using UnityEngine;

// This is an offline Unity boundary fixture. Real installed selection, output-quality,
// resource-consumption and compatibility Harmony hooks execute. Native scene objects,
// container discovery and output instantiation are represented by controlled test objects.
internal static class PipelineTests
{
    private sealed class Chest : IContainer
    {
        internal readonly Inventory Inventory;
        internal readonly string Name;
        internal bool Eligible = true;
        internal int Saves;
        internal Chest(string name, Inventory inventory) { Name = name; Inventory = inventory; }
        public Inventory GetInventory() => Inventory;
        public string GetPrefabName() => Name;
        public Vector3 GetPosition() => default;
        public void Save() { Saves++; }
        public int ItemCount(string name) => Inventory.CountItems(name);
        public void RemoveItem(string name, int amount) => Inventory.RemoveItem(name, amount);
        public int ProcessContainerInventory(string name, int total, int required)
        {
            int take = Math.Min(Inventory.CountItems(name), required - total);
            Inventory.RemoveItem(name, take);
            Save();
            return total + take;
        }
    }

    private static readonly Dictionary<UnityEngine.Object, string> Names = new Dictionary<UnityEngine.Object, string>();
    private static readonly Dictionary<Component, GameObject> GameObjects = new Dictionary<Component, GameObject>();
    private static readonly List<Chest> Chests = new List<Chest>();
    private static ConfigEntry<bool> amountEnabled = null!;
    private static ConfigEntry<bool> equipmentEnabled = null!;
    private static Assembly impact = null!;
    private static MethodInfo craftMethod = null!;
    private static MethodInfo process = null!;
    private static Recipe recipe = null!;
    private static bool pulling = true;
    private static bool failOutput;
    private static bool throwOutput;
    private static int producedQuality;
    private static int producedCount;
    private static int executedCrafts;
    private static ConfigEntryBase leaveOneEntry = null!;
    private static Type toggleType = null!;
    private static float bowSkill;

    internal static void Run(Assembly impactAssembly, Assembly crafty, Action<string, Action> test)
    {
        impact = impactAssembly;
        var fixture = new Harmony("DeepNorthCompat.Tests.Pipeline");
        Hook(fixture, typeof(UnityEngine.Object), "get_name", nameof(ObjectName));
        Hook(fixture, typeof(Component), "get_gameObject", nameof(ComponentObject));
        Hook(fixture, typeof(Inventory), "Changed", nameof(Skip));
        Hook(fixture, typeof(Player), "NoCostCheat", nameof(False));
        Hook(fixture, typeof(ZoneSystem), "GetGlobalKey", nameof(False), new[] { typeof(GlobalKeys) });
        Hook(fixture, typeof(BepInEx.ThreadingHelper), "StartSyncInvoke", nameof(Skip));
        AccessTools.Field(typeof(BepInEx.ThreadingHelper), "<Instance>k__BackingField")
            .SetValue(null, Fake<BepInEx.ThreadingHelper>("thread-helper"));
        Type functions = crafty.GetType("AzuCraftyBoxes.Util.Functions.MiscFunctions", true)!;
        Hook(fixture, functions, "ShouldPrevent", nameof(Prevent));
        Type frame = crafty.GetType("AzuCraftyBoxes.Util.Functions.Boxes+QueryFrame", true)!;
        MethodInfo query = AccessTools.Method(frame, "Get").MakeGenericMethod(typeof(Player));
        fixture.Patch(query, prefix: new HarmonyMethod(AccessTools.Method(typeof(PipelineTests), nameof(Query))));
        Hook(fixture, crafty.GetType("AzuCraftyBoxes.Util.Functions.UiItemBank", true)!, "Begin", nameof(Skip));

        var config = new ConfigFile(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "unsaved-pipeline.cfg"), false)
        { SaveOnConfigSet = false };
        Type valConfig = impact.GetType("ImpactfulSkills.ValConfig", true)!;
        amountEnabled = Bool(config, valConfig, "EnableQualityIngredientScaling", true);
        equipmentEnabled = Bool(config, valConfig, "ScaleCraftedEquipmentQuality", true);
        Bool(config, valConfig, "EnableDebugMode", false);
        Bool(config, valConfig, "RestrictQualityScalingToFish", true);
        Bool(config, valConfig, "ScaleNonStackingCraftOutputs", false);
        AccessTools.Field(valConfig, "QualityIngredientOutputMultiplier").SetValue(null, config.Bind("fixture", "output", 1f));
        Type craftyPlugin = crafty.GetType("AzuCraftyBoxes.AzuCraftyBoxesPlugin", true)!;
        AccessTools.Field(craftyPlugin, "mRange").SetValue(null, config.Bind("fixture", "range", 20f));
        Type toggle = craftyPlugin.GetNestedType("Toggle", BindingFlags.Public | BindingFlags.NonPublic)!;
        MethodInfo bind = typeof(ConfigFile).GetMethods().Single(m => m.Name == "Bind" && m.IsGenericMethodDefinition
            && m.GetParameters().Length == 3 && m.GetParameters()[0].ParameterType == typeof(ConfigDefinition));
        object leave = bind.MakeGenericMethod(toggle).Invoke(config, new object[]
        { new ConfigDefinition("fixture", "leave"), Enum.ToObject(toggle, 0), new ConfigDescription("test") });
        // Bind's overload shape is checked below rather than relying on optional arguments.
        AccessTools.Field(craftyPlugin, "leaveOne").SetValue(null, leave);
        leaveOneEntry = (ConfigEntryBase)leave; toggleType = toggle;
        AccessTools.Field(craftyPlugin, "yamlData").SetValue(null, new Dictionary<string, Dictionary<string, List<string>>>());

        foreach (string type in new[]
        {
            "ImpactfulSkills.IngredientQuality+ConsumeQualityIngredientTierPatch",
            "ImpactfulSkills.patches.Fishing+RecipeQualityIngredientAmountPatch",
            "ImpactfulSkills.patches.Crafting+CraftedItemQualityPatch",
            "ImpactfulSkills.patches.Crafting+CraftedItemQualityAddPatch"
        }) new Harmony("MidnightsFX.ImpactfulSkills").PatchAll(impact.GetType(type, true)!);
        new Harmony("Azumatt.AzuCraftyBoxes").PatchAll(crafty.GetType("AzuCraftyBoxes.Patches.ConsumeResourcesPatch", true)!);
        craftMethod = AccessTools.Method(typeof(InventoryGui), "DoCrafting");
        process = AccessTools.Method(functions, "ProcessRequirements");
        fixture.Patch(craftMethod, transpiler: new HarmonyMethod(AccessTools.Method(typeof(PipelineTests), nameof(CraftBody))));
        fixture.Patch(AccessTools.Method(typeof(Inventory), "AddItem", new[] { typeof(string), typeof(int), typeof(int), typeof(int),
            typeof(long), typeof(string), typeof(Vector2i), typeof(bool), typeof(bool), typeof(bool) }),
            transpiler: new HarmonyMethod(AccessTools.Method(typeof(PipelineTests), nameof(AddBody))));

        void Case(string name, int playerLow, int playerHigh, int firstLow, int firstHigh, int secondHigh,
            int expectedTier, bool equipment = true, bool amountOption = true, bool equipmentOption = true)
        {
            test("real hooks: " + name, () =>
            {
                Player player = Setup(playerLow, playerHigh, firstLow, firstHigh, secondHigh, equipment);
                amountEnabled.Value = amountOption; equipmentEnabled.Value = equipmentOption;
                int before = Total();
                int lowBefore = TotalQuality(1), highBefore = TotalQuality(3);
                var gui = Gui();
                craftMethod.Invoke(gui, new object[] { player });
                Check(before - Total() == 12, "exact consumption");
                Check(executedCrafts == 1, "single craft");
                Check(producedQuality == (equipment && equipmentOption ? Math.Max(1, expectedTier) : 1), "output quality");
                Check(producedCount == (!equipment && amountOption ? Math.Max(1, expectedTier) : 1), "output amount");
                if ((amountOption || equipmentOption) && expectedTier > 0)
                {
                    Check(lowBefore - TotalQuality(1) == (expectedTier == 1 ? 12 : 0), "exact low-quality consumption");
                    Check(highBefore - TotalQuality(3) == (expectedTier == 3 ? 12 : 0), "exact high-quality consumption");
                }
            });
        }
        Case("all player", 0, 12, 0, 0, 0, 3);
        Case("one container", 0, 0, 0, 12, 0, 3);
        Case("player plus container", 0, 4, 0, 8, 0, 3);
        Case("multiple containers", 0, 0, 0, 5, 7, 3);
        Case("low sufficient plus high insufficient", 12, 0, 0, 11, 0, 1);
        Case("high sufficient plus low insufficient", 11, 0, 0, 12, 0, 3);
        Case("both sufficient selects low", 12, 0, 0, 12, 0, 1);
        Case("mixed tiers without a sufficient tier", 6, 0, 0, 6, 0, 0);
        Case("quality ingredient amount", 0, 0, 0, 12, 0, 3, equipment: false);
        Case("amount independently disabled", 0, 0, 0, 12, 0, 3, false, false, true);
        Case("equipment independently disabled", 0, 0, 0, 12, 0, 3, true, true, false);
        Case("both independently disabled", 0, 0, 0, 12, 0, 1, true, false, false);
        test("real hooks: insufficient cancels before output", () =>
        {
            Player player = Setup(0, 0, 0, 11, 0, true);
            int before = Total(); craftMethod.Invoke(Gui(), new object[] { player });
            Check(executedCrafts == 0 && producedCount == 0 && Total() == before, "cancel");
        });
        test("real hooks: failed output rolls back partial output and ingredients", () =>
        {
            Player player = Setup(0, 4, 0, 8, 0, true);
            failOutput = true;
            int before = Total(); craftMethod.Invoke(Gui(), new object[] { player });
            Check(Total() == before && !player.GetInventory().GetAllItems().Any(i => i.m_shared.m_name == "output"), "rollback");
        });
        test("real hooks: thrown output rolls back and preserves exception", () =>
        {
            Player player = Setup(0, 4, 0, 8, 0, true);
            throwOutput = true;
            int before = Total();
            try { craftMethod.Invoke(Gui(), new object[] { player }); throw new Exception("missing throw"); }
            catch (TargetInvocationException exception) { Check(exception.InnerException is InvalidOperationException, "original exception"); }
            Check(Total() == before && !player.GetInventory().GetAllItems().Any(i => i.m_shared.m_name == "output"), "exception rollback");
        });
        test("real hooks: AAA-style sequential batch reselects", () =>
        {
            Player player = Setup(12, 0, 0, 24, 0, true);
            var gui = Gui();
            craftMethod.Invoke(gui, new object[] { player }); Check(producedQuality == 1, "first tier");
            craftMethod.Invoke(gui, new object[] { player }); Check(producedQuality == 3, "second tier");
            craftMethod.Invoke(gui, new object[] { player }); Check(producedQuality == 3, "third tier");
            Check(Total() == 0 && executedCrafts == 3, "batch totals");
        });
        test("real hooks: native multicraft uses the total requirement", () =>
        {
            Player player = Setup(11, 0, 0, 24, 0, true);
            var gui = Gui(); gui.m_multiCraftAmount = 2;
            AccessTools.Field(typeof(InventoryGui), "m_multiCrafting").SetValue(gui, true);
            craftMethod.Invoke(gui, new object[] { player });
            Check(producedCount == 2 && producedQuality == 3 && TotalQuality(1) == 11 && TotalQuality(3) == 0, "native batch");
        });
        test("real hooks: actual CraftyBoxes exclusions are honored", () =>
        {
            Player player = Setup(0, 0, 0, 12, 12, true);
            AccessTools.Field(craftyPlugin, "yamlData").SetValue(null,
                new Dictionary<string, Dictionary<string, List<string>>>
                { ["chest1"] = new Dictionary<string, List<string>> { ["exclude"] = new List<string> { "fish" } } });
            craftMethod.Invoke(Gui(), new object[] { player });
            Check(Chests[0].ItemCount("fish") == 12 && Chests[1].ItemCount("fish") == 0, "excluded container");
            AccessTools.Field(craftyPlugin, "yamlData").SetValue(null, new Dictionary<string, Dictionary<string, List<string>>>());
        });
        test("real hooks: query eligibility is retained", () =>
        {
            Player player = Setup(0, 0, 0, 12, 12, true); Chests[0].Eligible = false;
            craftMethod.Invoke(Gui(), new object[] { player });
            Check(Chests[0].ItemCount("fish") == 12 && Chests[1].ItemCount("fish") == 0, "query eligibility");
        });
        test("real hooks: leave-one preserves another quality", () =>
        {
            Player player = Setup(0, 0, 1, 12, 0, true);
            leaveOneEntry.BoxedValue = Enum.ToObject(toggleType, 1);
            craftMethod.Invoke(Gui(), new object[] { player });
            Check(producedQuality == 3 && TotalQuality(3) == 0 && TotalQuality(1) == 1 && Chests[0].Saves == 1, "leave-one");
        });
        test("real hooks: CraftyBoxes disabled preserves player-only ImpactfulSkills", () =>
        {
            Player player = Setup(0, 12, 0, 24, 0, true);
            pulling = false;
            craftMethod.Invoke(Gui(), new object[] { player });
            Check(producedQuality == 3 && player.GetInventory().CountItems("fish") == 0
                && Chests.Sum(c => c.ItemCount("fish")) == 24, "disabled fallback");
        });

        test("real hooks: optional CraftyBoxes absent preserves player-only crafting", () =>
        {
            typeof(BowCalculation).Assembly.GetType("DeepNorthCompat.QualityPatch", true)!
                .GetMethod("DisableIf", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { "Quality" });
            new Harmony("DeepNorthCompat.Quality").UnpatchSelf();
            new Harmony("Azumatt.AzuCraftyBoxes").UnpatchSelf();
            Player player = Setup(0, 12, 0, 24, 0, true);
            craftMethod.Invoke(Gui(), new object[] { player });
            Check(producedQuality == 3 && player.GetInventory().CountItems("fish") == 0, "optional absence");
        });

        test("real bow drain and tooltip use the same configured formula", () =>
        {
            Hook(fixture, typeof(Player), "GetSkillFactor", nameof(SkillFactor), new[] { typeof(Skills.SkillType) });
            Bool(config, valConfig, "EnableWeaponSkill", true);
            ConfigEntry<float> reduction = config.Bind("fixture", "bow", 0f);
            AccessTools.Field(valConfig, "WeaponSkillBowDrawStaminaCostReduction").SetValue(null, reduction);
            new Harmony("MidnightsFX.ImpactfulSkills").PatchAll(impact.GetType("ImpactfulSkills.patches.WeaponSkill+ModifyStaimaDrainBows", true)!);
            ItemDrop.ItemData bow = Item(1, 1, "bow");
            bow.m_shared.m_skillType = Skills.SkillType.Bows;
            bow.m_shared.m_attack = (Attack)FormatterServices.GetUninitializedObject(typeof(Attack));
            bow.m_shared.m_attack.m_drawStaminaDrain = 10;
            MethodInfo report = impact.GetType("ImpactfulSkills.patches.WeaponSkill+ItemDisplay", true)!
                .GetMethod("Postfix", BindingFlags.Static | BindingFlags.Public)!;
            foreach (float value in new[] { 0f, 0.33f, 0.5f, 1f })
                foreach (float skill in new[] { 0f, 0.5f, 1f })
                {
                    reduction.Value = value; bowSkill = skill;
                    float expected = BowCalculation.Drain(10, skill, value);
                    Check(Math.Abs(bow.GetDrawStaminaDrain() - expected) < 0.00001, "actual drain");
                    object[] args = { bow, "$item_staminahold: 10" }; report.Invoke(null, args);
                    Check(((string)args[1]).Contains("(" + expected.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + ")"), "actual reporting");
                }
        });
    }

    private static ConfigEntry<bool> Bool(ConfigFile file, Type type, string name, bool value)
    {
        ConfigEntry<bool> entry = file.Bind("fixture", name, value);
        AccessTools.Field(type, name).SetValue(null, entry);
        return entry;
    }

    private static T Fake<T>(string name) where T : UnityEngine.Object
    {
        var value = (T)FormatterServices.GetUninitializedObject(typeof(T));
        AccessTools.Field(typeof(UnityEngine.Object), "m_CachedPtr").SetValue(value, new IntPtr(1));
        Names[value] = name;
        return value;
    }

    private static Inventory Inventory()
    {
        var inventory = (Inventory)FormatterServices.GetUninitializedObject(typeof(Inventory));
        AccessTools.Field(typeof(Inventory), "m_inventory").SetValue(inventory, new List<ItemDrop.ItemData>());
        return inventory;
    }

    private static ItemDrop.ItemData Item(int count, int quality, string name = "fish", int maximumQuality = 5)
    {
        var item = (ItemDrop.ItemData)FormatterServices.GetUninitializedObject(typeof(ItemDrop.ItemData));
        item.m_shared = (ItemDrop.ItemData.SharedData)FormatterServices.GetUninitializedObject(typeof(ItemDrop.ItemData.SharedData));
        item.m_shared.m_name = name; item.m_shared.m_maxQuality = maximumQuality; item.m_shared.m_maxStackSize = 50;
        item.m_shared.m_itemType = ItemDrop.ItemData.ItemType.Fish;
        item.m_quality = quality; item.m_stack = count;
        item.m_dropPrefab = Fake<GameObject>(name);
        return item;
    }

    private static void Add(Inventory inventory, int low, int high)
    {
        if (low > 0) inventory.GetAllItems().Add(Item(low, 1));
        if (high > 0) inventory.GetAllItems().Add(Item(high, 3));
    }

    private static Player Setup(int playerLow, int playerHigh, int firstLow, int firstHigh, int secondHigh, bool equipment)
    {
        Names.Clear(); GameObjects.Clear(); Chests.Clear();
        var player = Fake<Player>("player");
        Player.m_localPlayer = player;
        Inventory inventory = Inventory();
        AccessTools.Field(typeof(Humanoid), "m_inventory").SetValue(player, inventory);
        Add(inventory, playerLow, playerHigh);
        var first = new Chest("chest1", Inventory()); Add(first.Inventory, firstLow, firstHigh); Chests.Add(first);
        var second = new Chest("chest2", Inventory()); Add(second.Inventory, 0, secondHigh); Chests.Add(second);
        var input = Fake<ItemDrop>("fish"); input.m_itemData = Item(1, 1);
        var output = Fake<ItemDrop>("output"); output.m_itemData = Item(1, 1, "output", equipment ? 4 : 1);
        output.m_itemData.m_shared.m_itemType = ItemDrop.ItemData.ItemType.Helmet;
        GameObjects[output] = Fake<GameObject>("output");
        recipe = Fake<Recipe>("recipe"); recipe.m_item = output; recipe.m_amount = 1;
        recipe.m_qualityResultAmountMultiplier = 1;
        recipe.m_resources = new[] { new Piece.Requirement { m_resItem = input, m_amount = 12 } };
        AccessTools.Field(typeof(ZoneSystem), "s_instance").SetValue(null, Fake<ZoneSystem>("zone"));
        amountEnabled.Value = true; equipmentEnabled.Value = true;
        pulling = true; failOutput = false; throwOutput = false;
        leaveOneEntry.BoxedValue = Enum.ToObject(toggleType, 0);
        producedQuality = 0; producedCount = 0; executedCrafts = 0;
        return player;
    }

    private static InventoryGui Gui()
    {
        var gui = Fake<InventoryGui>("gui");
        AccessTools.Field(typeof(InventoryGui), "m_craftRecipe").SetValue(gui, recipe);
        return gui;
    }

    private static int Total() => Player.m_localPlayer.GetInventory().CountItems("fish") + Chests.Sum(c => c.ItemCount("fish"));
    private static int TotalQuality(int quality) => Player.m_localPlayer.GetInventory().CountItems("fish", quality)
        + Chests.Sum(c => c.Inventory.CountItems("fish", quality));
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private static void Hook(Harmony harmony, Type type, string method, string hook, Type[]? arguments = null)
        => harmony.Patch(AccessTools.Method(type, method, arguments),
            prefix: new HarmonyMethod(AccessTools.Method(typeof(PipelineTests), hook)));
    private static bool ObjectName(UnityEngine.Object __instance, ref string __result)
    { __result = Names.TryGetValue(__instance, out string name) ? name : "fixture"; return false; }
    private static bool ComponentObject(Component __instance, ref GameObject __result)
    { __result = GameObjects[__instance]; return false; }
    private static bool Skip() => false;
    private static bool False(ref bool __result) { __result = false; return false; }
    private static bool Prevent(ref bool __result) { __result = !pulling; return false; }
    private static bool SkillFactor(ref float __result) { __result = bowSkill; return false; }
    private static bool Query(ref List<IContainer> __result)
    { __result = Chests.Where(c => c.Eligible).Cast<IContainer>().ToList(); return false; }

    private static IEnumerable<CodeInstruction> CraftBody(IEnumerable<CodeInstruction> _)
        => new[] { new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldarg_1),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PipelineTests), nameof(Craft))), new CodeInstruction(OpCodes.Ret) };
    private static IEnumerable<CodeInstruction> AddBody(IEnumerable<CodeInstruction> _)
    {
        var code = Enumerable.Range(0, 11).Select(i => new CodeInstruction(OpCodes.Ldarg, i)).ToList();
        code.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PipelineTests), nameof(Output))));
        code.Add(new CodeInstruction(OpCodes.Ret)); return code;
    }

    private static void Craft(InventoryGui gui, Player player)
    {
        executedCrafts++;
        int multiplier = (bool)AccessTools.Field(typeof(InventoryGui), "m_multiCrafting").GetValue(gui)
            ? gui.m_multiCraftAmount : 1;
        int amount = recipe.GetAmount(1, out _, out _, multiplier);
        ItemDrop.ItemData item = player.GetInventory().AddItem("output", amount, 1, 0, 0, "test", default, false);
        if (item != null)
        {
            player.ConsumeResources(recipe.m_resources, 1, -1, multiplier);
            // Repeating the callback deliberately checks the compatibility consumption guard.
            if (pulling && (amountEnabled.Value || equipmentEnabled.Value))
                process.Invoke(null, new object?[] { recipe.m_resources, 1, player.GetInventory(),
                    Chests.Cast<IContainer>().ToList(), -1, multiplier, null });
        }
    }

    private static ItemDrop.ItemData? Output(Inventory inventory, string name, int count, int quality, int variant,
        long id, string crafter, Vector2i position, bool cheated, bool pickedUp, bool dropIfFull)
    {
        var item = Item(count, quality, name);
        inventory.GetAllItems().Add(item);
        if (failOutput) return null;
        if (throwOutput) throw new InvalidOperationException("test output failure");
        producedQuality = quality; producedCount = count;
        return item;
    }
}
