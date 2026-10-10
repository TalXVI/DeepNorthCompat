using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using BepInEx.Configuration;
using DeepNorthCompat;
using HarmonyLib;
using SysConsole = System.Console;

internal static class Program
{
    private static readonly string Lab = Environment.GetEnvironmentVariable("DEEPNORTHCOMPAT_LAB_PATH") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "com.kesomannen.gale", "valheim", "profiles", "Deep North");
    private static readonly string Managed = Environment.GetEnvironmentVariable("DEEPNORTHCOMPAT_MANAGED_PATH") ?? Path.Combine(Environment.GetEnvironmentVariable("DEEPNORTHCOMPAT_VALHEIM_PATH")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common", "Valheim"),
        "valheim_Data", "Managed");
    private static int passed;

    private static int Main()
    {
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        try
        {
            if (Environment.GetEnvironmentVariable("DEEPNORTHCOMPAT_TEST_MODE") == "server") RunServer();
            else { Run(); TuneClientTests.Run(Lab, Test); VpoBurstTests.Run(Lab, Test); RunChests(); OdinShipTests.Run(Lab, Test); }
            SysConsole.WriteLine($"PASS: {passed} test cases"); return 0;
        }
        catch (Exception exception) { SysConsole.Error.WriteLine(exception); return 1; }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void RunServer() { TuneClientTests.Run(Lab, Test); VpoBurstTests.Run(Lab, Test); ChestTests.Run(Lab, Test); TabAudioTests.RunServer(Test); OdinShipTests.Run(Lab, Test); }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void RunChests() => ChestTests.Run(Lab, Test);

    private static Assembly? Resolve(object sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name + ".dll";
        string core = Path.Combine(Lab, "BepInEx", "core", name);
        string game = Path.Combine(Managed, name);
        string? file = File.Exists(core) ? core : File.Exists(game) ? game
            : Directory.GetFiles(Path.Combine(Lab, "BepInEx", "plugins"), name, SearchOption.AllDirectories).FirstOrDefault();
        return file == null ? null : Assembly.LoadFrom(file);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Test(string name, Action action)
    {
        action(); passed++; SysConsole.WriteLine("PASS " + name);
    }

    private static ResourceStack<object, string> Stack(string source, int count, int quality = 1, string name = "fish", int world = 0)
        => new ResourceStack<object, string>(new object(), source, name, quality, world, count, source != "player");

    private static IngredientPlan<object, string>? Select(ResourceStack<object, string>[] stacks, int amount, bool leaveOne = false,
        Func<string, bool>? allowed = null)
        => IngredientSelector.Select(stacks, new[] { new ResourceNeed<string>("fish", 5, amount, allowed ?? (_ => true)) }, 0, leaveOne);

    private static void Run()
    {
        Test("bow zero is vanilla at every skill", () =>
        {
            for (int level = 0; level <= 100; level++)
            {
                float skill = level / 100f;
                Check(Math.Abs(BowCalculation.Drain(10, skill, 0) - (10 - 10 * 0.33f * skill)) < 0.00001, "zero");
            }
        });
        Test("bow configured percentage, disabled feature and boundaries", () =>
        {
            foreach (float reduction in new[] { 0.1f, 0.33f, 0.5f, 1f })
                foreach (float skill in new[] { 0f, 0.25f, 0.5f, 1f })
                    Check(Math.Abs(BowCalculation.Drain(10, skill, reduction) - (10 - 10 * reduction * skill)) < 0.00001, "percentage");
            Check(BowCalculation.Drain(10, 1, 1, false) == BowCalculation.Drain(10, 1, 0), "disabled");
            Check(BowCalculation.Reduction(float.NaN, true) == 0.33f, "invalid");
        });

        var cases = new[]
        {
            Tuple.Create("all player", new[] { Stack("player", 12, 3) }, 12, 3),
            Tuple.Create("one eligible container", new[] { Stack("chest1", 12, 3) }, 12, 3),
            Tuple.Create("player and container", new[] { Stack("player", 4, 3), Stack("chest1", 8, 3) }, 12, 3),
            Tuple.Create("several containers", new[] { Stack("chest1", 4, 3), Stack("chest2", 5, 3), Stack("chest3", 3, 3) }, 12, 3),
            Tuple.Create("low sufficient, high insufficient", new[] { Stack("player", 12, 1), Stack("chest1", 11, 5) }, 12, 1),
            Tuple.Create("high sufficient, low insufficient", new[] { Stack("player", 11, 1), Stack("chest1", 12, 5) }, 12, 5),
            Tuple.Create("both sufficient chooses low", new[] { Stack("player", 12, 1), Stack("chest1", 12, 5) }, 12, 1),
            Tuple.Create("mixed insufficient tiers gives no bonus", new[] { Stack("player", 6, 1), Stack("chest1", 6, 3) }, 12, 0),
            Tuple.Create("exact resource boundary", new[] { Stack("player", 4, 2), Stack("chest1", 8, 2) }, 12, 2),
            Tuple.Create("native batch uses entire batch requirement", new[] { Stack("player", 11, 1), Stack("chest1", 24, 4) }, 24, 4)
        };
        foreach (var entry in cases)
            Test(entry.Item1, () =>
            {
                IngredientPlan<object, string> plan = Select(entry.Item2, entry.Item3) ?? throw new Exception("missing plan");
                Check(plan.Ingredients.Single().Tier == entry.Item4, "tier");
                Check(plan.Allocations.Sum(a => a.Count) == entry.Item3, "quantity");
                if (entry.Item4 > 0) Check(plan.Allocations.All(a => a.Stack.Quality == entry.Item4), "consumed quality");
                Commit(plan, entry.Item2);
            });
        Test("insufficient resources never produce a plan", () => Check(Select(new[] { Stack("player", 11, 5) }, 12) == null, "insufficient"));
        Test("container eligibility and private/range policy supplied by CraftyBoxes", () =>
        {
            var stacks = new[] { Stack("player", 1, 1), Stack("excluded", 100, 5), Stack("eligible", 11, 3) };
            IngredientPlan<object, string> plan = Select(stacks, 12, allowed: source => !Equals(source, "excluded"))!;
            Check(plan.Ingredients.Single().Tier == 0 && plan.Allocations.All(a => !Equals(a.Stack.Source, "excluded")), "restriction");
            Check(Select(stacks, 13, allowed: source => !Equals(source, "excluded")) == null, "restriction boundary");
        });
        Test("leave one per container and shared quality budget", () =>
        {
            Check(Select(new[] { Stack("chest1", 12, 3) }, 12, true) == null, "leave one exact");
            Check(Select(new[] { Stack("chest1", 13, 3) }, 12, true)!.Ingredients.Single().Tier == 3, "leave one sufficient");
            Check(Select(new[] { Stack("chest1", 12, 3), Stack("chest1", 1, 1) }, 12, true)!.Ingredients.Single().Tier == 3, "reserve alternative quality");
            Check(Select(new[] { Stack("chest1", 6, 3), Stack("chest2", 6, 3) }, 11, true) == null, "each container");
        });
        Test("world level filtering matches ImpactfulSkills", () =>
        {
            Check(Select(new[] { Stack("player", 20, 5, world: -1) }, 12) == null, "world level");
        });
        Test("multiple distinct requirements consume correct quantities", () =>
        {
            var stacks = new[] { Stack("player", 12, 3), Stack("chest1", 5, name: "linen") };
            var needs = new[] { new ResourceNeed<string>("fish", 5, 12, _ => true), new ResourceNeed<string>("linen", 1, 5, _ => true) };
            IngredientPlan<object, string> plan = IngredientSelector.Select(stacks, needs, 0, false)!;
            Check(plan.Ingredients[0].Tier == 3 && plan.Ingredients[1].Tier == 0, "qualities");
            Commit(plan, stacks);
        });
        Test("AAA sequential batch reselects for each craft", () =>
        {
            var first = new[] { Stack("player", 12, 1), Stack("chest1", 24, 3) };
            IngredientPlan<object, string> plan1 = Select(first, 12)!;
            Check(plan1.Ingredients.Single().Tier == 1, "first");
            var counts = Commit(plan1, first);
            var second = first.Select(s => new ResourceStack<object, string>(s.Id, s.Source, s.Name, s.Quality, s.WorldLevel, counts[s.Id], s.Container)).ToArray();
            Check(Select(second, 12)!.Ingredients.Single().Tier == 3, "second");
            Commit(Select(second, 12)!, second);
        });
        Test("failed output restores every selected stack", () =>
        {
            var stacks = new[] { Stack("player", 4, 3), Stack("chest1", 8, 3) };
            var counts = stacks.ToDictionary(s => s.Id, s => s.Count);
            var reservation = new IngredientReservation<object, string>(Select(stacks, 12)!);
            Check(reservation.Reserve(s => counts[s.Id], a => { counts[a.Stack.Id] -= a.Count; return true; },
                (s, count) => counts[s.Id] = count), "reserve");
            reservation.Complete(false, (s, count) => counts[s.Id] = count);
            Check(stacks.All(s => counts[s.Id] == s.Count), "rollback");
        });
        Test("failed removal after mutation rolls back before output", () =>
        {
            var stacks = new[] { Stack("player", 4, 3), Stack("chest1", 8, 3) };
            var counts = stacks.ToDictionary(s => s.Id, s => s.Count);
            var reservation = new IngredientReservation<object, string>(Select(stacks, 12)!);
            try
            {
                reservation.Reserve(s => counts[s.Id], a =>
                {
                    counts[a.Stack.Id] -= a.Count;
                    if (a.Stack.Container) throw new InvalidOperationException("test removal failure");
                    return true;
                }, (s, count) => counts[s.Id] = count);
                throw new Exception("missing removal failure");
            }
            catch (InvalidOperationException) { }
            Check(stacks.All(s => counts[s.Id] == s.Count), "mutating failure rollback");
        });
        Test("changed inputs cancel before any removal", () =>
        {
            var stacks = new[] { Stack("player", 12, 3) };
            var reservation = new IngredientReservation<object, string>(Select(stacks, 12)!);
            bool removed = false;
            Check(!reservation.Reserve(_ => 11, _ => { removed = true; return true; }, (_, __) => { }), "changed input");
            Check(!removed, "no partial consumption");
        });
        Test("future parser shape rejected without mutation", () =>
        {
            var errors = new List<string>();
            CompatibilityInstaller.Install(_ => null, _ => { }, _ => { }, errors.Add);
            var code = new[] { new CodeInstruction(OpCodes.Nop) };
            List<CodeInstruction> result = DropRangePatch.FixMaximum(code, MethodBase.GetCurrentMethod()!).ToList();
            Check(result.Count == 1 && result[0].opcode == OpCodes.Nop, "original IL returned");
            Check(code[0].opcode == OpCodes.Nop, "unchanged");
            Check(errors.Count == 1, "rejection reported: " + string.Join(Environment.NewLine, errors));
        });

        OfflineIntegration();
    }

    private static Dictionary<object, int> Commit(IngredientPlan<object, string> plan, ResourceStack<object, string>[] stacks)
    {
        var counts = stacks.ToDictionary(s => s.Id, s => s.Count);
        var reservation = new IngredientReservation<object, string>(plan);
        Check(reservation.Reserve(s => counts[s.Id], a => { counts[a.Stack.Id] -= a.Count; return true; },
            (s, count) => counts[s.Id] = count), "reservation");
        reservation.Complete(true, (s, count) => counts[s.Id] = count);
        Check(!reservation.Reserve(s => counts[s.Id], _ => { throw new Exception("double consumption"); }, (_, __) => { }), "idempotence");
        Check(counts.Values.All(count => count >= 0), "nonnegative");
        Check(stacks.Sum(s => s.Count) - counts.Values.Sum() == plan.Allocations.Sum(a => a.Count), "exact consumption");
        return counts;
    }

    private static void OfflineIntegration()
    {
        string sandbox = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox");
        AccessTools.Method(typeof(BepInEx.Paths), "SetExecutablePath").Invoke(null, new object[]
        {
            typeof(Program).Assembly.Location, Path.Combine(sandbox, "BepInEx"), Managed,
            new[] { Path.Combine(Lab, "BepInEx", "core") }
        });
        Test("accepted config values and shortcut order parse without rewriting files", () => ConfigValidation.Run(Lab));
        // Only the offline host substitutes this native animation helper. It lets
        // Player's static hash constants initialize without a running Unity engine.
        new Harmony("DeepNorthCompat.Tests.AnimationHash").Patch(AccessTools.Method(typeof(UnityEngine.Animator), "StringToHash"),
            prefix: new HarmonyMethod(AccessTools.Method(typeof(Program), nameof(AnimationHash))));
        Test("optional mods absent: startup remains safe", () =>
        {
            int inactive = 0;
            CompatibilityInstaller.Install(_ => null, message => { if (message.Contains("inactive")) inactive++; },
                _ => { }, error => { throw new Exception(error); });
            Check(inactive == 12, "optional module count");
        });
        Test("changed ImpactfulSkills assembly rejects bow and quality hooks safely", () =>
        {
            var errors = new List<string>();
            Assembly crafty = Assembly.LoadFrom(Path.Combine(Lab, "BepInEx", "plugins", "Azumatt-AzuCraftyBoxes", "AzuCraftyBoxes.dll"));
            CompatibilityInstaller.Install(guid => guid == "MidnightsFX.ImpactfulSkills" ? typeof(Program).Assembly
                : guid == "Azumatt.AzuCraftyBoxes" ? crafty : null,
                _ => { }, _ => { }, errors.Add);
            Check(errors.Count == 2 && errors.Any(error => error.StartsWith("Bow: NOT APPLIED"))
                && errors.Any(error => error.StartsWith("Quality: NOT APPLIED")), "changed build rejected for both integrations");
            Check(!Harmony.GetAllPatchedMethods().Any(method => Harmony.GetPatchInfo(method)!.Owners
                .Any(owner => owner == "DeepNorthCompat.Bow" || owner == "DeepNorthCompat.Quality")),
                "rejected integrations left hooks installed");
        });

        var assemblies = new Dictionary<string, Assembly>
        {
            ["MidnightsFX.ImpactfulSkills"] = Assembly.LoadFrom(Path.Combine(Lab, "BepInEx", "plugins", "MidnightMods-ImpactfulSkills", "ImpactfulSkills.dll")),
            ["Azumatt.AzuCraftyBoxes"] = Assembly.LoadFrom(Path.Combine(Lab, "BepInEx", "plugins", "Azumatt-AzuCraftyBoxes", "AzuCraftyBoxes.dll")),
            ["Azumatt.AzuAntiArthriticCrafting"] = Assembly.LoadFrom(Path.Combine(Lab, "BepInEx", "plugins", "Azumatt-AAA_Crafting", "AzuAntiArthriticCrafting.dll")),
            ["marlthon.SeaAnimals"] = Assembly.LoadFrom(Path.Combine(Lab, "BepInEx", "plugins", "Marlthon-SeaAnimals", "SeaAnimals.dll")),
            ["marlthon.AirAnimals"] = Assembly.LoadFrom(Path.Combine(Lab, "BepInEx", "plugins", "Marlthon-AirAnimals", "AirAnimals.dll"))
        };
        string? odinPath = Environment.GetEnvironmentVariable("DEEPNORTHCOMPAT_ODINSHIP_PATH");
        if (odinPath != null) assemblies["marlthon.OdinShip"] = Assembly.LoadFrom(odinPath);
        Assembly impact = assemblies["MidnightsFX.ImpactfulSkills"];
        Test("BepInEx plugin identity and optional dependencies are valid", () =>
        {
            BepInEx.BepInPlugin identity = typeof(Plugin).GetCustomAttribute<BepInEx.BepInPlugin>()!;
            Check(identity.GUID == "DeepNorthCompat" && identity.Name == "DeepNorthCompat"
                && identity.Version.ToString() == "1.2.3"
                && typeof(Plugin).Assembly.GetName().Name == "DeepNorthCompat", "plugin identity");
            Check(typeof(BepInEx.BaseUnityPlugin).IsAssignableFrom(typeof(Plugin)), "BepInEx entry point");
            var dependencies = typeof(Plugin).GetCustomAttributes<BepInEx.BepInDependency>().ToArray();
            Check(dependencies.Length == 11 && dependencies.All(d => d.Flags == BepInEx.BepInDependency.DependencyFlags.SoftDependency)
                && dependencies.Any(d => d.DependencyGUID == "marlthon.OdinShip"),
                "optional dependencies");
            Check(!typeof(Plugin).Assembly.GetReferencedAssemblies().Any(reference =>
                assemblies.Values.Any(vendor => reference.Name == vendor.GetName().Name)), "hard vendor reference");
        });
        Test("deferred patching reports a parser transpiler that never ran", () =>
        {
            // This fixture needs a fresh UI registration as well as fresh vendor parsers.
            new Harmony("DeepNorthCompat.UI.TabAudio").UnpatchSelf();
            Type tabAudio = typeof(Plugin).Assembly.GetType("DeepNorthCompat.TabAudioPatch", true)!;
            foreach (string field in new[] { "pending", "enabled" }) AccessTools.Field(tabAudio, field).SetValue(null, false);
            // Models StartupAccelerator, which skips Harmony wrapper updates during plugin
            // loading and applies them in one batch after every Awake has run.
            var defer = new Harmony("DeepNorthCompat.Tests.Defer");
            defer.Patch(AccessTools.Method(AccessTools.TypeByName("HarmonyLib.PatchFunctions"), "UpdateWrapper"),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(Program), nameof(SkipUpdate))));
            var errors = new List<string>();
            try
            {
                deferring = true;
                CompatibilityInstaller.Install(guid => guid.StartsWith("marlthon.") && assemblies.TryGetValue(guid, out Assembly value) ? value : null,
                    _ => { }, _ => { }, errors.Add);
                deferring = false;
                CompatibilityInstaller.Verify();
            }
            finally
            {
                deferring = false;
                defer.UnpatchSelf();
                new Harmony("DeepNorthCompat.Drops.SeaAnimals").UnpatchSelf();
                new Harmony("DeepNorthCompat.Drops.AirAnimals").UnpatchSelf();
            }
            Check(errors.Count == (odinPath == null ? 3 : 6) && errors.Count(e => e.Contains("did not run")) == 2
                && errors.Count(e => e.StartsWith("UI.TabAudio: NOT APPLIED")) == 1,
                string.Join(Environment.NewLine, errors));
            Check(!Harmony.GetAllPatchedMethods().Any(m => Harmony.GetPatchInfo(m)!.Owners.Contains("DeepNorthCompat.UI.TabAudio")),
                "unverified UI group rolled back after deferred wrappers were skipped");
            if (odinPath != null)
                Check(new[] { "Core", "Input", "FishPress" }.All(group => errors.Count(e => e.StartsWith("OdinShip." + group + ": NOT APPLIED")) == 1)
                    && !Harmony.GetAllPatchedMethods().Any(m => Harmony.GetPatchInfo(m)!.Owners.Any(o => o.StartsWith("DeepNorthCompat.OdinShip."))),
                    "deferred OdinShip groups roll back together with accurate status");
        });
        Test("installed BepInEx/Harmony load and every compatibility patch installs", () =>
        {
            var errors = new List<string>();
            var applied = new List<string>();
            var verified = new List<string>();
            CompatibilityInstaller.Install(guid => assemblies.TryGetValue(guid, out Assembly assembly) ? assembly : null,
                message =>
                {
                    SysConsole.WriteLine(message);
                    if (message.StartsWith("Registered:")) applied.Add(message);
                    if (message.StartsWith("Verified:")) verified.Add(message);
                },
                SysConsole.WriteLine, error => errors.Add(error));
            CompatibilityInstaller.Verify();
            Check(errors.Count == 0, string.Join(Environment.NewLine, errors));
            Check(applied.Count == 10, "expected Harmony target count");
            Check(verified.Count == 2, "expected parser transpiler count");
        });

        foreach (string guid in new[] { "marlthon.SeaAnimals", "marlthon.AirAnimals" })
            Test(guid + " actual patched parser ranges, singles and flags", () =>
            {
                Assembly assembly = assemblies[guid];
                Type serialized = assembly.GetType("CreatureManager.Creature+DropList+SerializedDrops", true)!;
                object Parsed(string range) => Activator.CreateInstance(serialized,
                    new object[] { "test:" + range + ":75:onePerPlayer:multiplyByLevel" });
                foreach (var range in new[] { Tuple.Create("5-9", 5f, 9f), Tuple.Create("2-4", 2f, 4f),
                    Tuple.Create("7", 7f, 7f), Tuple.Create("1-1", 1f, 1f), Tuple.Create("5-invalid", 5f, 5f) })
                {
                    var list = (System.Collections.IList)serialized.GetField("Drops")!.GetValue(Parsed(range.Item1));
                    object pair = list[0];
                    object drop = pair.GetType().GetProperty("Value")!.GetValue(pair);
                    object amount = drop.GetType().GetField("Amount")!.GetValue(drop);
                    Check((float)amount.GetType().GetField("min")!.GetValue(amount) == range.Item2, "min");
                    Check((float)amount.GetType().GetField("max")!.GetValue(amount) == range.Item3, "max");
                    Check((bool)drop.GetType().GetField("MultiplyDropByLevel")!.GetValue(drop), "level flag");
                    Check((bool)drop.GetType().GetField("DropOnePerPlayer")!.GetValue(drop), "per-player flag");
                    Check((float)drop.GetType().GetField("DropChance")!.GetValue(drop) == 75, "chance");
                }
                Check(serialized.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                    .Where(c => c.GetParameters().Length != 1 || c.GetParameters()[0].ParameterType != typeof(string))
                    .All(c => Harmony.GetPatchInfo(c) == null), "default constructors untouched");
            });

        Test("actual ImpactfulSkills tier selection agrees with player-only planner", () =>
        {
            Type ingredient = impact.GetType("ImpactfulSkills.IngredientQuality", true)!;
            MethodInfo select = ingredient.GetMethod("SelectTier", BindingFlags.NonPublic | BindingFlags.Static,
                null, new[] { typeof(Inventory), typeof(string), typeof(int), typeof(int) }, null)!;
            var random = new Random(4421);
            for (int trial = 0; trial < 250; trial++)
            {
                var items = new List<ItemDrop.ItemData>();
                var stacks = new List<ResourceStack<object, string>>();
                for (int quality = 1; quality <= 5; quality++)
                {
                    int count = random.Next(0, 16);
                    var item = (ItemDrop.ItemData)FormatterServices.GetUninitializedObject(typeof(ItemDrop.ItemData));
                    item.m_shared = (ItemDrop.ItemData.SharedData)FormatterServices.GetUninitializedObject(typeof(ItemDrop.ItemData.SharedData));
                    item.m_shared.m_name = "fish"; item.m_shared.m_maxQuality = 5;
                    item.m_quality = quality; item.m_stack = count; item.m_worldLevel = 0;
                    items.Add(item); stacks.Add(Stack("player", count, quality));
                }
                var inventory = (Inventory)FormatterServices.GetUninitializedObject(typeof(Inventory));
                AccessTools.Field(typeof(Inventory), "m_inventory").SetValue(inventory, items);
                int required = random.Next(1, 25);
                int upstream = (int)select.Invoke(null, new object[] { inventory, "fish", 5, required });
                int planned = Select(stacks.ToArray(), required)?.Ingredients.Single().Tier ?? 0;
                Check(upstream == planned, $"trial {trial}: upstream {upstream} versus plan {planned}");
            }
        });

        Test("quality options work independently and both disabled remain disabled", () =>
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "unsaved-test.cfg");
            Check(!File.Exists(path), "unexpected test config");
            var config = new ConfigFile(path, false) { SaveOnConfigSet = false };
            ConfigEntry<bool> amount = config.Bind("test", "amount", false);
            ConfigEntry<bool> equipment = config.Bind("test", "equipment", false);
            Type valConfig = impact.GetType("ImpactfulSkills.ValConfig", true)!;
            AccessTools.Field(valConfig, "EnableQualityIngredientScaling").SetValue(null, amount);
            AccessTools.Field(valConfig, "ScaleCraftedEquipmentQuality").SetValue(null, equipment);
            Type qualityPatch = typeof(BowCalculation).Assembly.GetType("DeepNorthCompat.QualityPatch", true)!;
            PropertyInfo enabled = qualityPatch.GetProperty("Enabled", BindingFlags.Static | BindingFlags.NonPublic)!;
            foreach (bool amountValue in new[] { false, true })
                foreach (bool equipmentValue in new[] { false, true })
                {
                    amount.Value = amountValue; equipment.Value = equipmentValue;
                    Check((bool)enabled.GetValue(null) == (amountValue || equipmentValue), "independent switches");
                }
            Check(!File.Exists(path), "configuration was written");
        });
        Test("AAA preview uses one craft, independent of native modifier state", () =>
        {
            MethodInfo multiplier = impact.GetType("ImpactfulSkills.IngredientQuality", true)!
                .GetMethod("PanelCraftMultiplier", BindingFlags.Static | BindingFlags.NonPublic)!;
            Check((int)multiplier.Invoke(null, null) == 1, "AAA preview");
        });

        PipelineTests.Run(impact, assemblies["Azumatt.AzuCraftyBoxes"], Test);
        PreviewTests.Run(Lab, Test);
        TabAudioTests.Run(Test);

        foreach (string owner in Harmony.GetAllPatchedMethods().SelectMany(m => Harmony.GetPatchInfo(m)!.Owners)
            .Where(id => id.StartsWith("DeepNorthCompat.")).Distinct().ToArray())
            new Harmony(owner).UnpatchSelf();
    }

    private static bool deferring;

    private static bool SkipUpdate(ref MethodInfo? __result)
    {
        if (!deferring) return true;
        __result = null;
        return false;
    }

    private static bool AnimationHash(string __0, ref int __result)
    {
        __result = __0.GetHashCode();
        return false;
    }
}
