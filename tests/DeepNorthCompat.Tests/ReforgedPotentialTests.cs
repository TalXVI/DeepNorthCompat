using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using BepInEx.Configuration;
using DeepNorthCompat;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

internal static class ReforgedPotentialTests
{
    private const string Vendor = "akuichi.ReforgedPotential", Impact = "MidnightsFX.ImpactfulSkills", Owner = "DeepNorthCompat.Crafting.ReforgedPotential",
        Fixture = "DeepNorthCompat.Tests.Reforged", Other = "DeepNorthCompat.Tests.Reforged.Other";
    private static readonly Type Patch = typeof(Plugin).Assembly.GetType("DeepNorthCompat.ReforgedPotentialPatch", true)!;
    private static readonly MethodInfo Craft = AccessTools.Method(typeof(InventoryGui), "DoCrafting");
    private static readonly Dictionary<Object, string> Names = new Dictionary<Object, string>(new Identity());
    private static readonly Dictionary<FieldInfo, object?> Saved = new Dictionary<FieldInfo, object?>();
    private static readonly List<string> Errors = new List<string>();
    private static Type plugin = null!, val = null!;
    private static MethodInfo vendorCraft = null!, odds = null!, restore = null!;
    private static ConfigFile config = null!;
    private static Player player = null!;
    private static InventoryGui gui = null!;
    private static CraftingStation station = null!;
    private static ItemDrop idol = null!;
    private static Recipe recipe = null!;
    private static ItemDrop.ItemData upgrade = null!;
    private static Inventory inventory = null!;
    private static bool deferred;
    private static int vanillaCalls, vendorCalls, checkpoints;
    private static float seenUpgrade, seenBreak;

    internal static void Run(string profile, Action<string, Action> test)
    {
        FieldInfo errorSink = AccessTools.Field(typeof(CompatibilityInstaller), "Error");
        var previousError = (Action<string>)errorSink.GetValue(null);
        errorSink.SetValue(null, (Action<string>)Errors.Add);
        try { Cases(profile, test); }
        finally
        {
            new Harmony(Owner).UnpatchSelf(); new Harmony(Fixture).UnpatchSelf(); new Harmony(Other).UnpatchSelf();
            if (vendorCraft != null) new Harmony(Vendor).Unpatch(Craft, vendorCraft);
            if (odds != null) { new Harmony(Impact).Unpatch(Craft, odds); new Harmony(Impact).Unpatch(Craft, restore); }
            foreach (var saved in Saved) saved.Key.SetValue(null, saved.Value);
            Saved.Clear(); Names.Clear();
            foreach (string name in new[] { "pending", "applied" }) AccessTools.Field(Patch, name).SetValue(null, false);
            errorSink.SetValue(null, previousError);
            Player.m_localPlayer = null;
        }
    }

    private static void Cases(string profile, Action<string, Action> test)
    {
        string path = Path.Combine(profile, "BepInEx/plugins/Akuichi-ReforgedPotential/ReforgedPotential.dll");
        if (!File.Exists(path)) return;
        Assembly vendor = Assembly.LoadFrom(path);
        Assembly impact = Assembly.LoadFrom(Path.Combine(profile, "BepInEx/plugins/MidnightMods-ImpactfulSkills/ImpactfulSkills.dll"));
        if (Environment.GetEnvironmentVariable("DEEPNORTHCOMPAT_TEST_MODE") == "server")
        {
            test("reforged dedicated role installs no crafting hook", () =>
            {
                AccessTools.Method(Patch, "Prepare").Invoke(null, new object[] { vendor });
                AccessTools.Method(Patch, "Verify").Invoke(null, null);
                Check(!Installed() && Errors.Count == 0, "dedicated role stays inactive");
            });
            return;
        }
        AccessTools.Method(typeof(BepInEx.Paths), "SetExecutablePath").Invoke(null, new object[]
        {
            typeof(ReforgedPotentialTests).Assembly.Location, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox/BepInEx"),
            Environment.GetEnvironmentVariable("DEEPNORTHCOMPAT_MANAGED_PATH"), new[] { Path.Combine(profile, "BepInEx/core") }
        });
        config = new ConfigFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox/reforged.cfg"), false) { SaveOnConfigSet = false };
        plugin = vendor.GetType("ReforgedPotential.ReforgedPotential", true)!;
        val = impact.GetType("ImpactfulSkills.ValConfig", true)!;
        Type refinement = impact.GetType("ImpactfulSkills.patches.ForgingRefinement", true)!;
        vendorCraft = AccessTools.Method(vendor.GetType(plugin.FullName + "+InventoryGuiPatch", true), "DoCraftingPrefix");
        odds = AccessTools.Method(impact.GetType(refinement.FullName + "+RefinementChancePatch", true), "Prefix");
        restore = AccessTools.Method(odds.DeclaringType, "Finalizer");
        Configure(impact);
        var fixture = new Harmony(Fixture);
        foreach (Type logger in new[] { Assembly.Load("Jotunn").GetType("Jotunn.Logger", true)!, impact.GetType("ImpactfulSkills.Logger", true)! })
            foreach (MethodInfo log in logger.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Where(m => m.Name.StartsWith("Log")))
                fixture.Patch(log, prefix: Hook(nameof(Skip)));
        fixture.Patch(AccessTools.Method(typeof(Player), "GetCurrentCraftingStation"), prefix: Hook(nameof(Station)));
        fixture.Patch(AccessTools.Method(typeof(Character), "GetSkillLevel"), prefix: Hook(nameof(Level)));
        fixture.Patch(AccessTools.Method(typeof(Player), "GetSkillFactor"), prefix: Hook(nameof(Factor)));
        fixture.Patch(AccessTools.Method(typeof(Recipe), "GetAmount"), prefix: Hook(nameof(Amount)));
        fixture.Patch(AccessTools.Method(typeof(Humanoid), "UnequipItem"), prefix: Hook(nameof(Skip)));
        fixture.Patch(AccessTools.Method(typeof(Player), "NoCostCheat"), prefix: Hook(nameof(NoCost)));
        fixture.Patch(AccessTools.Method(typeof(Player), "GetPlayerID"), prefix: Hook(nameof(Checkpoint)));
        fixture.Patch(AccessTools.PropertyGetter(typeof(Object), "name"), prefix: Hook(nameof(Name)));
        fixture.Patch(Craft, prefix: new HarmonyMethod(AccessTools.Method(typeof(ReforgedPotentialTests), nameof(Defer))) { priority = Priority.First + 100 }, transpiler: Hook(nameof(CraftBody)));
        new Harmony(Vendor).Patch(Craft, prefix: new HarmonyMethod(vendorCraft));
        Setup();
        test("reforged replacement runs the vendor upgrade once and suppresses the native body", () =>
        {
            AccessTools.Method(Patch, "Prepare").Invoke(null, new object[] { vendor });
            AccessTools.Method(Patch, "Verify").Invoke(null, null);
            Check(Errors.Count == 0, string.Join("\n", Errors));
            fixture.Patch(vendorCraft, prefix: Hook(nameof(CountVendor)));
            try
            {
                Craft.Invoke(gui, new object[] { player });
                Check(vendorCalls == 1 && vanillaCalls == 0, "ReforgedPotential runs once and suppresses the native body");
            }
            finally { fixture.Unpatch(vendorCraft, AccessTools.Method(typeof(ReforgedPotentialTests), nameof(CountVendor))); }
        });
        test("reforged deferred craft preserves the upgrade item", () =>
        {
            deferred = true;
            try
            {
                Craft.Invoke(gui, new object[] { player });
                Check(inventory.ContainsItem(upgrade) && checkpoints == 0 && vanillaCalls == 0, "deferral blocks both native and real vendor mutation");
            }
            finally { deferred = false; }
        });
        test("reforged keeps the ImpactfulSkills odds override ahead of the vendor upgrade", () =>
        {
            // Registered after the replacement, as ImpactfulSkills' own ordering names only ReforgedPotential's Harmony ID.
            new Harmony(Impact).Patch(Craft, prefix: new HarmonyMethod(odds) { before = new[] { Vendor } }, finalizer: new HarmonyMethod(restore));
            fixture.Patch(vendorCraft, prefix: Hook(nameof(Observe)));
            try
            {
                Craft.Invoke(gui, new object[] { player });
                // ImpactfulSkills' EffectiveChances: success 0.65 + 0.5 * 0.2; break min(0.25, 0.25) * (1 - 0.5), rolled after a failure.
                Near(seenUpgrade, 0.75f); Near(seenBreak, 0.125f / 0.25f);
                Near(Entry("UpgradeChance").Value, 0.23f); Near(Entry("BreakChance").Value, 0.17f);
            }
            finally
            {
                fixture.Unpatch(vendorCraft, AccessTools.Method(typeof(ReforgedPotentialTests), nameof(Observe)));
                new Harmony(Impact).Unpatch(Craft, odds); new Harmony(Impact).Unpatch(Craft, restore);
            }
        });
        test("reforged rejects an ambiguous vendor registration and retains it", () =>
        {
            // Start from a fresh session with the original vendor registration plus a second one.
            new Harmony(Owner).UnpatchSelf(); AccessTools.Field(Patch, "applied").SetValue(null, false);
            new Harmony(Vendor).Patch(Craft, prefix: new HarmonyMethod(vendorCraft));
            new Harmony(Other).Patch(Craft, prefix: new HarmonyMethod(vendorCraft));
            Errors.Clear();
            AccessTools.Method(Patch, "Prepare").Invoke(null, new object[] { vendor });
            AccessTools.Method(Patch, "Verify").Invoke(null, null);
            Check(Errors.Any(e => e.Contains("NOT APPLIED")), "a second registration of the vendor prefix rejects installation");
            Patch[] prefixes = Harmony.GetPatchInfo(Craft).Prefixes.Where(p => p.PatchMethod == vendorCraft).ToArray();
            Check(prefixes.Length == 2 && prefixes.Any(p => p.owner == Vendor) && !Installed(),
                "both vendor registrations remain and no replacement is added");
            new Harmony(Other).UnpatchSelf();
        });
        test("reforged rejects an unaudited vendor build before adding hooks", () =>
        {
            try
            {
                AccessTools.Method(Patch, "Prepare").Invoke(null, new object[] { typeof(Plugin).Assembly });
                throw new Exception("wrong vendor build was accepted");
            }
            catch (TargetInvocationException exception)
            {
                Check(exception.InnerException is NotSupportedException, "the exact binary guard rejected the fixture");
            }
            Check(!Installed(), "rejection leaves no integration hook");
        });
    }

    private static void Configure(Assembly impact)
    {
        Bind(plugin, "EnableDebugLogging", false);
        foreach (var entry in new[] { ("UpgradeChance", 0.23f), ("BreakChance", 0.17f) }) Bind(plugin, entry.Item1, entry.Item2);
        foreach (var entry in new[] { ("EnableForging", true), ("EnableRefinementBonus", true), ("EnableRefinementDecay", false), ("ForgingBonusesPastLevel100", false) }) Bind(val, entry.Item1, entry.Item2);
        Bind(val, "RefinementBonusLevel", 0);
        Bind(val, "RefineSuccessChanceBonus", 0.2f); Bind(val, "RefineBreakRefundBonus", 0.35f); Bind(val, "RefineBreakChanceReduction", 1f);
        Set(impact.GetType("ImpactfulSkills.compatibility.Modcheck", true)!, "IsReforgedPotentialEnabled", true);
    }

    private static void Setup()
    {
        player = Fake<Player>("player"); Player.m_localPlayer = player;
        station = Fake<CraftingStation>("station"); station.m_upgrader = true;
        gui = Fake<InventoryGui>("gui");
        inventory = (Inventory)FormatterServices.GetUninitializedObject(typeof(Inventory));
        AccessTools.Field(typeof(Inventory), "m_inventory").SetValue(inventory, new List<ItemDrop.ItemData>());
        AccessTools.Field(typeof(Humanoid), "m_inventory").SetValue(player, inventory);
        upgrade = new ItemDrop.ItemData { m_quality = 4, m_stack = 1, m_shared = new ItemDrop.ItemData.SharedData { m_name = "gear", m_maxStackSize = 1 } };
        inventory.GetAllItems().Add(upgrade);
        var output = Fake<ItemDrop>("gear"); output.m_itemData = upgrade;
        idol = Fake<ItemDrop>("Upgrader0Weapon");
        idol.m_itemData = new ItemDrop.ItemData { m_shared = new ItemDrop.ItemData.SharedData { m_name = "idol", m_upgradeChance = 0.65f, m_breakChance = 0.25f, m_breakReturnIngreientsAmount = 0.35f } };
        recipe = Fake<Recipe>("recipe"); recipe.m_item = output;
        recipe.m_resources = new[] { new Piece.Requirement { m_resItem = idol, m_upgraderResource = true, m_amount = 7, m_amountPerLevel = 2 } };
        AccessTools.Field(typeof(InventoryGui), "m_craftUpgradeItem").SetValue(gui, upgrade);
        AccessTools.Field(typeof(InventoryGui), "m_craftRecipe").SetValue(gui, recipe);
    }

    private static void Set(Type type, string name, object value)
    {
        FieldInfo field = AccessTools.Field(type, name);
        if (!Saved.ContainsKey(field)) Saved[field] = field.GetValue(null);
        field.SetValue(null, value);
    }
    private static void Bind<T>(Type type, string name, T value) => Set(type, name, config.Bind(type.FullName!, name, value));
    private static ConfigEntry<float> Entry(string name) => (ConfigEntry<float>)AccessTools.Field(plugin, name).GetValue(null);
    private static bool Installed() => Harmony.GetPatchInfo(Craft)?.Owners.Contains(Owner) == true;
    private static HarmonyMethod Hook(string name) => new HarmonyMethod(AccessTools.Method(typeof(ReforgedPotentialTests), name));
    private static T Fake<T>(string name) where T : Object
    {
        var instance = (T)FormatterServices.GetUninitializedObject(typeof(T));
        AccessTools.Field(typeof(Object), "m_CachedPtr").SetValue(instance, new IntPtr(1));
        Names[instance] = name; return instance;
    }
    private sealed class Identity : IEqualityComparer<Object>
    {
        public bool Equals(Object? first, Object? second) => ReferenceEquals(first, second);
        public int GetHashCode(Object value) => RuntimeHelpers.GetHashCode(value);
    }
    private static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
    private static void Near(float actual, float expected) => Check(Math.Abs(actual - expected) < 0.0001f, $"expected {expected}, got {actual}");
    private static bool Skip() => false;
    private static bool Defer() => !deferred;
    private static bool CountVendor(ref bool __result) { vendorCalls++; __result = false; return false; }
    private static bool Observe(ref bool __result)
    {
        seenUpgrade = Entry("UpgradeChance").Value; seenBreak = Entry("BreakChance").Value;
        __result = false; return false;
    }
    private static void NativeCraft() { vanillaCalls++; }
    private static IEnumerable<CodeInstruction> CraftBody(IEnumerable<CodeInstruction> _) => new[] { new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ReforgedPotentialTests), nameof(NativeCraft))), new CodeInstruction(OpCodes.Ret) };
    private static bool Station(ref CraftingStation __result) { __result = station; return false; }
    private static bool Level(ref float __result) { __result = 50f; return false; }
    private static bool Factor(ref float __result) { __result = 0.5f; return false; }
    private static bool Amount(ref int __result) { __result = 1; return false; }
    private static bool NoCost(ref bool __result) { __result = true; return false; }
    private static bool Checkpoint() { checkpoints++; throw new Exception("Unexpected vendor mutation after deferral."); }
    private static bool Name(Object __instance, ref string __result) { __result = Names[__instance]; return false; }
}
