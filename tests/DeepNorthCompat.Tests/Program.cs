using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
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
            Test("selected game build passes its role guard", CheckGameBuild);
            RunReforged();
            NoSmokeStayLitTests.Run(Lab, Test);
            if (Environment.GetEnvironmentVariable("DEEPNORTHCOMPAT_TEST_MODE") == "server") RunServer();
            else { Run(); TuneClientTests.Run(Lab, Test); VpoBurstTests.Run(Lab, Test); RunChests(); OdinShipTests.Run(Lab, Test); PlantingTests.Run(Lab, Test); WorldProbeTests.Run(Test); ReportUploadTests.Run(Test); MyLittleUiAmmoTests.Run(Lab, Test); }
            RunTerrain();
            RunBetterMap();
            RuneIceInstallationTests.Run(Lab, Test);
            RuneSpriteMaterialTests.Run(Lab, Test);
            RunForge();
            AaaRecipeRowsTests.Run(Lab, Test);
            SysConsole.WriteLine($"PASS: {passed} test cases"); return 0;
        }
        catch (Exception exception) { SysConsole.Error.WriteLine(exception); return 1; }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void RunServer() { TuneClientTests.Run(Lab, Test); VpoBurstTests.Run(Lab, Test); ChestTests.Run(Lab, Test); TabAudioTests.RunServer(Test); IdleSyncTests.RunServer(Test); OdinShipTests.Run(Lab, Test); PlantingTests.Run(Lab, Test); WorldProbeTests.Run(Test); ReportUploadTests.Run(Test); MyLittleUiAmmoTests.RunServer(Lab, Test); }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void RunChests() => ChestTests.Run(Lab, Test);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void RunBetterMap() => BetterMapTests.Run(Lab, Test);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void RunTerrain() => HearthTerrainTests.Run(Lab, Test);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void RunForge() => ForgeCraftingTests.Run(Lab, Test);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void RunReforged() => ReforgedPotentialTests.Run(Lab, Test);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void CheckGameBuild()
    {
        string role = Environment.GetEnvironmentVariable("DEEPNORTHCOMPAT_TEST_MODE") == "server" ? "Server" : "Valheim";
        var builds = typeof(Plugin).Assembly.GetType("DeepNorthCompat.ExpectedBuilds", true)!;
        Guard.Build(typeof(ZNet).Assembly, (string[])AccessTools.Field(builds, role).GetValue(null));
    }

    private static Assembly? Resolve(object sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name + ".dll";
        string core = Path.Combine(Lab, "BepInEx", "core", name);
        string game = Path.Combine(Managed, name);
        string? file = File.Exists(core) ? core : File.Exists(game) ? game
            : Directory.GetFiles(Path.Combine(Lab, "BepInEx", "plugins"), name, SearchOption.AllDirectories).FirstOrDefault();
        return file == null ? null : Assembly.LoadFrom(file);
    }

    private static string Repository()
    {
        for (DirectoryInfo? directory = new FileInfo(typeof(Program).Assembly.Location).Directory; directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "package", "manifest.json"))) return directory.FullName;
        throw new DirectoryNotFoundException("The test build is outside a DeepNorthCompat checkout.");
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
        Test("file hashes are reused until the file's size or write time changes", () =>
        {
            MethodInfo sha256 = AccessTools.Method(typeof(Guard), "Sha256");
            MethodInfo report = AccessTools.Method(typeof(Plugin).Assembly.GetType("DeepNorthCompat.DiagnosticReport", true), "Hash");
            string Hash(string file) => (string)sha256.Invoke(null, new object[] { file });
            string Expected(byte[] data) { using (var sha = System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", ""); }
            string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "hash-" + Guid.NewGuid().ToString("N") + ".bin");
            byte[] first = { 1, 2, 3, 4 }, second = { 5, 6, 7, 8 }, longer = { 9, 9, 9, 9, 9 };
            var written = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            try
            {
                File.WriteAllBytes(path, first); File.SetLastWriteTimeUtc(path, written);
                Check(Hash(path) == Expected(first), "first hash");
                Check((string)report.Invoke(null, new object[] { path }) == Expected(first).Substring(0, 16), "report shares the hash");
                // The same size and write time reuse the stored hash without reading the file.
                File.WriteAllBytes(path, second); File.SetLastWriteTimeUtc(path, written);
                Check(Hash(path) == Expected(first), "unchanged file reuses its hash");
                File.SetLastWriteTimeUtc(path, written.AddSeconds(1));
                Check(Hash(path) == Expected(second), "new write time rehashes");
                File.WriteAllBytes(path, longer); File.SetLastWriteTimeUtc(path, written.AddSeconds(1));
                Check(Hash(path) == Expected(longer), "new size rehashes");
                File.Delete(path);
                try { Hash(path); throw new Exception("missing file hashed"); }
                catch (TargetInvocationException error) { Check(error.InnerException is FileNotFoundException, "missing file throws like File.OpenRead"); }
                Check((string)report.Invoke(null, new object[] { path }) == "unreadable", "report marks a missing file unreadable");
            }
            finally { File.Delete(path); }
        });
        Test("bow configured percentage, disabled feature and boundaries", () =>
        {
            foreach (float reduction in new[] { 0.01f, 0.1f, 0.5f, 1f })
                Check(BowCalculation.Reduction(reduction, true) == reduction, "configured " + reduction);
            foreach (float invalid in new[] { 0f, -0.5f, 1.01f, float.NaN, float.PositiveInfinity })
                Check(BowCalculation.Reduction(invalid, true) == 0.33f, "vanilla for " + invalid);
            Check(BowCalculation.Reduction(0.5f, false) == 0.33f, "disabled");
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
        // Only the offline host substitutes this native animation helper. It lets
        // Player's static hash constants initialize without a running Unity engine.
        new Harmony("DeepNorthCompat.Tests.AnimationHash").Patch(AccessTools.Method(typeof(UnityEngine.Animator), "StringToHash"),
            prefix: new HarmonyMethod(AccessTools.Method(typeof(Program), nameof(AnimationHash))));
        Test("optional mods absent: startup remains safe", () =>
        {
            CompatibilityInstaller.Install(guid => { resolved.Add(guid); return null; }, _ => { }, _ => { }, error => { throw new Exception(error); });
            CompatibilityInstaller.Verify();
            string plugins = Path.GetFullPath(Path.Combine(Lab, "BepInEx", "plugins"));
            Check(!Harmony.GetAllPatchedMethods().Any(method => Harmony.GetPatchInfo(method)!.Owners
                    .Any(owner => owner.StartsWith("DeepNorthCompat.") && !owner.StartsWith("DeepNorthCompat.Tests."))
                && Path.GetFullPath(method.DeclaringType!.Assembly.Location).StartsWith(plugins, StringComparison.OrdinalIgnoreCase)),
                "an optional integration patched a mod that was not resolved");
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
        using var aaaConfig = new AaaCraftingFixture(assemblies["Azumatt.AzuAntiArthriticCrafting"]);
        // The deferral case adds OdinShip; offline, its construction prefab is never registered, so the full install omits it.
        string? odinPath = Environment.GetEnvironmentVariable("DEEPNORTHCOMPAT_ODINSHIP_PATH")
            ?? Directory.GetFiles(Path.Combine(Lab, "BepInEx", "plugins"), "OdinShip.dll", SearchOption.AllDirectories).SingleOrDefault();
        Assembly? odin = odinPath == null ? null : Assembly.LoadFrom(odinPath);
        Assembly impact = assemblies["MidnightsFX.ImpactfulSkills"];
        Test("BepInEx plugin identity and optional dependencies are valid", () =>
        {
            BepInEx.BepInPlugin identity = typeof(Plugin).GetCustomAttribute<BepInEx.BepInPlugin>()!;
            Check(identity.GUID == "DeepNorthCompat" && identity.Name == "DeepNorthCompat"
                && typeof(Plugin).Assembly.GetName().Name == "DeepNorthCompat", "plugin identity");
            string manifest = File.ReadAllText(Path.Combine(Repository(), "package", "manifest.json"));
            Check(System.Text.RegularExpressions.Regex.Match(manifest, "\"version_number\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value
                == identity.Version.ToString(), "plugin version differs from the package manifest");
            Check(typeof(BepInEx.BaseUnityPlugin).IsAssignableFrom(typeof(Plugin)), "BepInEx entry point");
            var dependencies = typeof(Plugin).GetCustomAttributes<BepInEx.BepInDependency>().ToArray();
            Check(dependencies.Where(d => d.Flags == BepInEx.BepInDependency.DependencyFlags.HardDependency)
                    .Select(d => d.DependencyGUID).SequenceEqual(new[] { "_shudnal.ConditionalConfigSync" }), "hard dependencies");
            // Without a soft dependency, BepInEx can load DNC first and the installer resolves that mod as absent.
            string[] unordered = resolved.Where(guid => !dependencies.Any(d => d.DependencyGUID == guid
                && d.Flags == BepInEx.BepInDependency.DependencyFlags.SoftDependency)).ToArray();
            Check(resolved.Count > 0 && unordered.Length == 0, "resolved without a soft dependency: " + string.Join(", ", unordered));
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
                CompatibilityInstaller.Install(guid => guid == "marlthon.OdinShip" ? odin
                    : guid.StartsWith("marlthon.") && assemblies.TryGetValue(guid, out Assembly value) ? value : null,
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
            Check(errors.Count == (odin == null ? 3 : 7) && errors.Count(e => e.Contains("did not run")) == 2
                && errors.Count(e => e.StartsWith("UI.TabAudio: NOT APPLIED")) == 1,
                string.Join(Environment.NewLine, errors));
            Check(!Harmony.GetAllPatchedMethods().Any(m => Harmony.GetPatchInfo(m)!.Owners.Contains("DeepNorthCompat.UI.TabAudio")),
                "unverified UI group rolled back after deferred wrappers were skipped");
            if (odin != null)
                Check(new[] { "Core", "Input", "FishPress", "Construction" }.All(group => errors.Count(e => e.StartsWith("OdinShip." + group + ": NOT APPLIED")) == 1)
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
            // ImpactfulSkills 0.25.0 fixes the bow drain upstream, so the two Bow targets are not registered.
            bool bowFixedUpstream = impact.GetName().Version >= new System.Version(0, 25, 0, 0);
            Check(applied.Count == (bowFixedUpstream ? 10 : 12), "expected Harmony target count");
            Check(Harmony.GetAllPatchedMethods().Any(m => Harmony.GetPatchInfo(m)!.Owners.Contains("DeepNorthCompat.Bow")) != bowFixedUpstream,
                "Bow hooks are installed only for builds that still double the skill factor");
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

        Test("AAA preview uses one craft, independent of native modifier state", () =>
        {
            MethodInfo multiplier = impact.GetType("ImpactfulSkills.IngredientQuality", true)!
                .GetMethod("PanelCraftMultiplier", BindingFlags.Static | BindingFlags.NonPublic)!;
            var gui = (InventoryGui)FormatterServices.GetUninitializedObject(typeof(InventoryGui));
            AccessTools.Field(typeof(UnityEngine.Object), "m_CachedPtr").SetValue(gui, new IntPtr(1));
            gui.m_multiCraftAmount = 4;
            FieldInfo instance = AccessTools.Field(typeof(InventoryGui), "m_instance");
            var modifier = new Harmony("DeepNorthCompat.Tests.AltPlace");
            modifier.Patch(AccessTools.Method(typeof(ZInput), "GetButton", new[] { typeof(string) }),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(Program), nameof(AltPlaceHeld))));
            try
            {
                instance.SetValue(null, gui);
                Check((int)multiplier.Invoke(null, null) == 1, "AAA preview counted the native batch");
            }
            finally { instance.SetValue(null, null); modifier.UnpatchSelf(); }
        });

        PipelineTests.Run(impact, assemblies["Azumatt.AzuCraftyBoxes"], Test);
        PreviewTests.Run(Lab, Test);
        TabAudioTests.Run(Test);
        IdleSyncTests.Run(Test);

        foreach (string owner in Harmony.GetAllPatchedMethods().SelectMany(m => Harmony.GetPatchInfo(m)!.Owners)
            .Where(id => id.StartsWith("DeepNorthCompat.")).Distinct().ToArray())
            new Harmony(owner).UnpatchSelf();
    }

    private static bool deferring;
    // Every GUID the installer resolves during the absent-mods startup case.
    private static readonly HashSet<string> resolved = new HashSet<string>();

    private static bool SkipUpdate(ref MethodInfo? __result)
    {
        if (!deferring) return true;
        __result = null;
        return false;
    }

    private static bool AltPlaceHeld(string __0, ref bool __result)
    {
        __result = __0 == "AltPlace";
        return false;
    }

    private static bool AnimationHash(string __0, ref int __result)
    {
        __result = __0.GetHashCode();
        return false;
    }
}
