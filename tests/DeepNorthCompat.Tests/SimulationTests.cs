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
using UnityEngine;

internal static class SimulationTests
{
    private const string Core = "MVP.Valheim_Serverside_Simulations.Core";
    private const string ForkVcp = "MVP.Valheim_Serverside_Simulations.Compat_ValheimCommunityPatch";
    private static readonly BindingFlags Flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static Assembly fork = null!, vpo = null!, vcp = null!, impact = null!;
    private static Type simulation = null!, skills = null!;
    private static readonly List<string> errors = new List<string>();
    private static readonly List<string> messages = new List<string>();
    private static int removals;

    internal static void Policy(Action<string, Action> test)
    {
        test("skill state accepts zero and full skill without changing factors", () =>
        {
            foreach (float f in new[] { 0f, 0.01f, 0.5f, 1f }) Check(SkillState.RequireFactor(1, f) == f, "factor changed");
        });
        test("missing, mismatched and invalid skill snapshots are rejected", () =>
        {
            foreach (float f in new[] { float.NaN, float.NegativeInfinity, float.PositiveInfinity, -0.1f, 1.1f })
                Reject(() => SkillState.RequireFactor(1, f));
            Reject(() => SkillState.RequireFactor(0, 0.8f));
            Reject(() => SkillState.RequireFactor(2, 0.8f));
        });
        test("nearby skill selection chooses one closest player within the existing range", () =>
        {
            var players = new[] { Tuple.Create(8L, 900f), Tuple.Create(5L, 100f), Tuple.Create(2L, 100f), Tuple.Create(1L, 401f) };
            Check(SkillState.Closest(players, p => p.Item2, p => p.Item1, 20) == players[2], "range/tie");
            Check(SkillState.Closest(players, p => p.Item2, p => p.Item1, 5) == null, "out of range");
            Check(SkillState.Closest(Array.Empty<Tuple<long, float>>(), p => p.Item2, p => p.Item1, 20) == null, "empty");
        });
        test("build guard accepts any listed platform build and rejects unlisted builds", () =>
        {
            Assembly assembly = typeof(SimulationTests).Assembly;
            string actual;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (FileStream file = File.OpenRead(assembly.Location))
                actual = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
            string other = new string('0', 64);
            Guard.Build(assembly, other, actual);
            Reject(() => Guard.Build(assembly, other));
        });
    }

    internal static void Run(string lab, Action<string, Action> test)
    {
        AccessTools.Method(typeof(BepInEx.Paths), "SetExecutablePath").Invoke(null, new object[]
        {
            typeof(SimulationTests).Assembly.Location, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox/BepInEx"),
            Environment.GetEnvironmentVariable("DEEPNORTHCOMPAT_MANAGED_PATH")!, new[] { Path.Combine(lab, "BepInEx/core") }
        });
        string upstream = Environment.GetEnvironmentVariable("DEEPNORTHCOMPAT_SIMULATION_PATH")
            ?? throw new Exception("Set DEEPNORTHCOMPAT_SIMULATION_PATH to the locally obtained pinned fork/Tune DLL directory.");
        fork = Assembly.LoadFrom(Path.Combine(upstream, "fork.dll"));
        vpo = Assembly.LoadFrom(Directory.GetFiles(Path.Combine(lab, "BepInEx/plugins"), "ValheimPerformanceOptimizations.dll", SearchOption.AllDirectories).Single());
        vcp = Assembly.LoadFrom(Directory.GetFiles(Path.Combine(lab, "BepInEx/plugins"), "ValheimCommunityPatch.dll", SearchOption.AllDirectories).Single());
        impact = Assembly.LoadFrom(Directory.GetFiles(Path.Combine(lab, "BepInEx/plugins"), "ImpactfulSkills.dll", SearchOption.AllDirectories).Single());
        simulation = typeof(Plugin).Assembly.GetType("DeepNorthCompat.SimulationPatch", true)!;
        skills = typeof(Plugin).Assembly.GetType("DeepNorthCompat.OwnerSkillPatch", true)!;
        Policy(test);
        void Case(string name, Action action) => test("simulation " + name, () =>
        {
            Cleanup(); errors.Clear(); messages.Clear();
            CompatibilityInstaller.Install(_ => null, messages.Add, messages.Add, errors.Add);
            try { action(); } finally { Cleanup(); }
        });

        Case("actual headless dedicated build is recognized and client groups stay inactive", () =>
        {
            CompatibilityInstaller.Install(guid => guid == "MidnightsFX.ImpactfulSkills" ? impact : null,
                messages.Add, messages.Add, errors.Add);
            Check(errors.Count == 0, string.Join("\n", errors));
            Check(messages.Any(m => m.StartsWith("Bow: client-only")) && messages.Any(m => m.StartsWith("Preview.AzuEPI: client-only")), "client exclusion");
        });
        Case("server without the simulation mod leaves owner skills inactive", () =>
        {
            Call(skills, "Prepare", impact, null!); Call(skills, "Verify");
            Check(!(bool)skills.GetProperty("Enabled", Flags)!.GetValue(null), "server bridge enabled without simulation");
            Check(!Harmony.GetAllPatchedMethods().SelectMany(m => Patches(m)).Any(p => p.owner == "DeepNorthCompat.OwnerSkills"), "server bridge registered without simulation");
            Check(errors.Count == 0 && messages.Any(m => m.Contains("dedicated simulation absent")), "missing simulation diagnostic");
        });
        Case("all fork Core targets and transpilers apply to the headless game build", () =>
        {
            int count = 0;
            foreach (Type type in fork.GetType("Valheim_Serverside.Features.Core", true)!.GetNestedTypes(Flags)
                .Where(t => t.GetCustomAttributes(typeof(HarmonyPatch), false).Length > 0))
            { new Harmony(Core).PatchAll(type); count++; }
            Check(count >= 17 && (bool)Call(simulation, "CoreActive", fork)!, "incomplete Core registration");
        });
        Case("Tune's actual server targets apply and its client guard retains every hook", () =>
        {
            Assembly tune = Assembly.LoadFrom(Path.Combine(upstream, "tune.dll"));
            var cfg = new ConfigFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox/tune-never-written.cfg"), false) { SaveOnConfigSet = false };
            Type settings = tune.GetType("ValheimTune.Cfg", true)!;
            AccessTools.Method(settings, "Bind").Invoke(null, new object[] { cfg });
            foreach (string key in new[] { "OverrideSendRate", "OverrideSendWindow", "TopKSort", "SaveDirtyFix", "DeadZdoPrune" })
                ((ConfigEntry<bool>)AccessTools.Field(settings, key).GetValue(null)).Value = false;
            AccessTools.Field(tune.GetType("ValheimTune.Plugin", true), "Log").SetValue(null, new BepInEx.Logging.ManualLogSource("offline Tune"));
            new Harmony("akoozie.valheimtune").PatchAll(tune);
            string[] before = Snapshot();
            Type guard = typeof(Plugin).Assembly.GetType("DeepNorthCompat.TuneClientPatch", true)!;
            Call(guard, "Prepare", tune); Call(guard, "Verify");
            Check(before.SequenceEqual(Snapshot()) && messages.Any(m => m.Contains("server patches retained")), "Tune server hooks changed");
        });
        Case("Core absent leaves all client-oriented vendor hooks intact", () =>
        {
            RegisterVendors(); string[] before = Snapshot(); Prepare(); Call(simulation, "Verify");
            Check(!Active && before.SequenceEqual(Snapshot()), "inactive Core changed hooks");
        });
        Case("removes only the eleven exact conflicting hooks and retains Core", () =>
        {
            RegisterCore(); RegisterVendors();
            MethodInfo target = AccessTools.Method(typeof(ZNetScene), "CreateDestroyObjects");
            new Harmony("DeepNorthCompat.Tests.Foreign").Patch(target, postfix: Hook(nameof(Noop)));
            Prepare(); Call(simulation, "Verify");
            Check(Active && errors.Count == 0, string.Join("\n", errors));
            Check(messages.Count(m => m.Contains("removed 11 exact")) == 1, "removal count");
            Check(Snapshot().Any(s => s.Contains("DeepNorthCompat.Tests.Foreign")), "foreign patch removed");
            Check((bool)Call(simulation, "CoreActive", fork)!, "Core removed");
            Check(!Harmony.GetAllPatchedMethods().SelectMany(m => Patches(m)).Any(p => IsConflict(p.PatchMethod)), "conflicting hooks remain");
            string[] after = Snapshot(); Call(simulation, "Verify"); Check(after.SequenceEqual(Snapshot()), "non-idempotent verification");
        });
        Case("late registration activates the fork's actual VCP takeover", () =>
        {
            RegisterCore(); RegisterVendors();
            var vendor = new Harmony("MidnightsFX.ValheimCommunityPatch");
            foreach ((string type, string target, string hook) in new[]
            {
                ("SpawnQueueCachePatch", "CreateObjectsSorted", "CreateObjectsSortedPrefix"),
                ("ZoneDiffRemovalPatch", "RemoveObjects", "RemoveObjectsPrefix")
            }) vendor.Patch(AccessTools.Method(typeof(ZNetScene), target), prefix: new HarmonyMethod(
                AccessTools.Method(vcp.GetType("ValheimCommunityPatch.Patches.Performance." + type, true), hook)));
            var cfg = new ConfigFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox/fork-never-written.cfg"), false) { SaveOnConfigSet = false };
            Type config = fork.GetType("PluginConfiguration.Configuration", true)!;
            AccessTools.Field(config, "compatVcpSpawnQueue").SetValue(null, cfg.Bind("Compat", "ValheimCommunityPatchSpawnQueue", false));
            AccessTools.Field(config, "compatVcpUnload").SetValue(null, cfg.Bind("Compat", "ValheimCommunityPatchUnload", false));
            Type plugin = fork.GetType("Valheim_Serverside.ServersidePlugin", true)!;
            AccessTools.Field(plugin, "logger").SetValue(null, new BepInEx.Logging.ManualLogSource("offline fork"));
            AccessTools.Field(plugin, "harmony").SetValue(null, new Harmony("MVP.Valheim_Serverside_Simulations"));
            Prepare(); Call(simulation, "Verify");
            Check(Active && errors.Count == 0, string.Join("\n", errors));
            Check(Harmony.GetPatchInfo(AccessTools.Method(typeof(ZNetScene), "Awake"))!.Postfixes
                .Count(p => p.owner == ForkVcp && p.PatchMethod == ForkVcpHook()) == 1, "upstream hook not registered");
            ForkVcpHook().Invoke(null, null);
            foreach (string target in new[] { "CreateObjectsSorted", "RemoveObjects" })
                Check(!(Harmony.GetPatchInfo(AccessTools.Method(typeof(ZNetScene), target))?.Prefixes
                    .Any(p => p.owner == "MidnightsFX.ValheimCommunityPatch") ?? false), "upstream takeover did not remove " + target);
            Check((bool)Call(simulation, "CoreActive", fork)!, "upstream takeover removed Core");
        });
        Case("already registered fork VCP takeover is retained without duplication", () =>
        {
            RegisterCore(); RegisterVendors();
            MethodInfo target = AccessTools.Method(typeof(ZNetScene), "Awake");
            new Harmony(ForkVcp).Patch(target, postfix: new HarmonyMethod(ForkVcpHook()));
            Prepare(); Call(simulation, "Verify");
            Check(Active && errors.Count == 0, string.Join("\n", errors));
            Check(Harmony.GetPatchInfo(target)!.Postfixes.Count(p => p.PatchMethod == ForkVcpHook()) == 1, "duplicated upstream hook");
            Check(!messages.Any(m => m.Contains("registered the fork's existing")), "existing hook was reinstalled");
        });
        Case("missing takeover after world initialization rejects and disables the fork", () =>
        {
            RegisterCore(); RegisterVendors();
            AccessTools.Field(ForkVcpHook().DeclaringType, "s_done").SetValue(null, true);
            string[] before = Snapshot(); Prepare(); Call(simulation, "Verify");
            Check(!Active && VanillaFallback(before), "already-run takeover was silently registered again");
        });
        Case("missing vendor hook rejects the whole removal transaction and disables the fork", () =>
        {
            RegisterCore(); RegisterVendors();
            MethodInfo hook = AccessTools.Method(vcp.GetType("ValheimCommunityPatch.Patches.Performance.SceneIdleSkipPatch", true), "CreateDestroyObjectsPostfix");
            new Harmony("MidnightsFX.ValheimCommunityPatch").Unpatch(AccessTools.Method(typeof(ZNetScene), "CreateDestroyObjects"), hook);
            string[] before = Snapshot(); Prepare(); Call(simulation, "Verify");
            Check(!Active && VanillaFallback(before), "partial removal on rejection");
        });
        Case("changed upstream build is rejected and disables the fork", () =>
        {
            RegisterCore(); RegisterVendors(); string[] before = Snapshot();
            Func<string, Assembly?> resolve = guid => guid == "MVP.Valheim_Serverside_Simulations" ? fork
                : guid == "dev.ontrigger.vpo" ? typeof(SimulationTests).Assembly
                : guid == "MidnightsFX.ValheimCommunityPatch" ? vcp : null;
            Call(simulation, "Prepare", resolve); Call(simulation, "Verify");
            Check(!Active && VanillaFallback(before), "changed vendor mutated state");
        });
        Case("removal failure restores the vendor hooks and disables the fork", () =>
        {
            RegisterCore(); RegisterVendors(); string[] before = Snapshot(); Prepare(); removals = 0;
            var failure = new Harmony("DeepNorthCompat.Tests.Failure");
            failure.Patch(AccessTools.Method(typeof(Harmony), "Unpatch", new[] { typeof(MethodBase), typeof(MethodInfo) }), prefix: Hook(nameof(FailRemoval)));
            try { Call(simulation, "Verify"); }
            finally { failure.UnpatchSelf(); }
            Check(!Active && VanillaFallback(before), "vendor hooks not restored or fork retained");
        });
        Case("changed fork build disables the fork before removing vendor hooks", () =>
        {
            // A renamed copy loads beside the pinned fork with the same types and a different file hash.
            string copy = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox/changed-fork.dll");
            using (var module = Mono.Cecil.ModuleDefinition.ReadModule(fork.Location))
            { module.Assembly.Name.Name += ".Changed"; module.Write(copy); }
            var changed = new Assembly[1];
            var resolve = new Func<string, Assembly?>(guid => guid == "MVP.Valheim_Serverside_Simulations" ? changed[0]
                : guid == "dev.ontrigger.vpo" ? vpo : guid == "MidnightsFX.ValheimCommunityPatch" ? vcp : null);
            changed[0] = Assembly.LoadFrom(copy);
            Check(changed[0].Location != fork.Location, "changed fork resolved to the pinned file");
            RegisterCore(changed[0]); RegisterVendors(); string[] before = Snapshot();
            Call(simulation, "Prepare", resolve); Call(simulation, "Verify");
            Check(!Active && VanillaFallback(before), "changed fork kept Core or removed vendor hooks");
        });
        Case("headless owner skill hooks resolve against actual installed implementations", () =>
        {
            RegisterCore(); RegisterVendors(); Prepare(); Call(simulation, "Verify");
            BindSkillConfig(); RegisterTreeVendor(); Call(skills, "Prepare", impact, fork); Call(skills, "Verify");
            Check(errors.Count == 0, string.Join("\n", errors));
            Check(messages.Any(m => m.StartsWith("OwnerSkills: APPLIED")), "skill module inactive");
            MethodInfo tree = AccessTools.Method(typeof(TreeBase), "RPC_Damage");
            Check(Harmony.GetPatchInfo(tree)!.Transpilers.Any(p => p.owner == "DeepNorthCompat.OwnerSkills"), "tree anchor patch missing");
            Check(!Harmony.GetPatchInfo(tree)!.Transpilers.Any(p => p.PatchMethod.DeclaringType!.Assembly == impact), "old tree transpiler retained");
            foreach (Type type in new[] { typeof(MineRock), typeof(MineRock5), typeof(TreeBase), typeof(TreeLog), typeof(Destructible) })
                Check(Harmony.GetPatchInfo(AccessTools.Method(type, type == typeof(MineRock) ? "RPC_Hit" : "RPC_Damage"))!
                    .Prefixes.Any(p => p.owner == "DeepNorthCompat.OwnerSkills"), "missing resource context " + type);
        });
        Case("tree bonus moves to the felling branch and rejects unknown shapes", () =>
        {
            var original = PatchProcessor.GetOriginalInstructions(AccessTools.Method(typeof(TreeBase), "RPC_Damage")).ToArray();
            var result = ((IEnumerable<CodeInstruction>)Call(skills, "FixTreeAnchor", (object)original)!).ToArray();
            int added = Array.FindIndex(result, c => c.operand is MethodInfo m && m.Name == "TreeDrops");
            Check(added > 0 && result[added - 2].opcode == OpCodes.Bgt_Un, "wrong health branch");
            Check(result.Length == original.Length + 2, "unexpected rewrite");
            Reject(() => Call(skills, "FixTreeAnchor", (object)new[] { new CodeInstruction(OpCodes.Nop) }));
        });
        Case("rewrites the real local-player and skill calls without changing formulas", () =>
        {
            foreach ((string type, string method) in new[]
            {
                ("ImpactfulSkills.patches.Mining", "IncreaseMiningDrops"),
                ("ImpactfulSkills.patches.Woodcutting", "IncreaseWoodDrops"),
                ("ImpactfulSkills.patches.AnimalWhisper+IncreaseTamingSpeed", "Prefix"),
                ("ImpactfulSkills.patches.AnimalWhisper+ScaleTamedAnimalLoot", "Postfix")
            })
            {
                var original = PatchProcessor.GetOriginalInstructions(AccessTools.Method(impact.GetType(type, true), method)).ToArray();
                var result = ((IEnumerable<CodeInstruction>)Call(skills, "RewriteContext", (object)original)!).ToArray();
                Check(result.Length == original.Length && result.Any(c => c.operand is MethodInfo m && m.Name == "Actor"), "context replacement");
                Check(original.Where(c => c.opcode == OpCodes.Ldc_R4).Select(c => c.operand)
                    .SequenceEqual(result.Where(c => c.opcode == OpCodes.Ldc_R4).Select(c => c.operand)), "formula constants changed");
                Check(!result.Any(c => c.opcode == OpCodes.Ldsfld && c.operand is FieldInfo f && f.Name == "m_localPlayer"), "local player retained");
            }
        });
        ServerSkillTests.Run(impact, skills, test, () =>
        {
            Cleanup(); errors.Clear(); messages.Clear();
            RegisterCore(); RegisterVendors(); Prepare(); Call(simulation, "Verify");
            BindSkillConfig(); RegisterTreeVendor(); Call(skills, "Prepare", impact, fork); Call(skills, "Verify");
            Check(errors.Count == 0, string.Join("\n", errors));
        });
        Cleanup();
    }

    private static void RegisterCore(Assembly? source = null)
    {
        foreach ((Type type, string method, string patch) in new[]
        {
            (typeof(ZNetScene), "CreateDestroyObjects", "CreateDestroyObjects_Patch"),
            (typeof(ZDOMan), "ReleaseNearbyZDOS", "ZDOMan_ReleaseNearbyZDOS_Patch"),
            (typeof(ZoneSystem), "Update", "ZoneSystem_Update_Patch")
        }) new Harmony(Core).Patch(AccessTools.Method(type, method), prefix: new HarmonyMethod(AccessTools.Method(
            (source ?? fork).GetType("Valheim_Serverside.Features.Core+" + patch, true), "Prefix")));
    }
    // A rejected integration leaves every vendor hook in place and no fork hook except its console capture.
    private static bool VanillaFallback(string[] before) => errors.Count == 2 && errors[1].StartsWith("Simulation: FORK DISABLED")
        && before.Where(s => !s.Split(':')[1].StartsWith("MVP.Valheim_Serverside_Simulations")).SequenceEqual(Snapshot());
    private static void RegisterVendors()
    {
        new Harmony("dev.ontrigger.vpo").PatchAll(vpo.GetType("ValheimPerformanceOptimizations.Patches.ObjectManagement.ZNetSceneObjectManagementPatch", true)!);
        new Harmony("dev.ontrigger.vpo").PatchAll(vpo.GetType("ValheimPerformanceOptimizations.Patches.ObjectManagement.ZDOManReleaseNearbyPatch", true)!);
        Type idle = vcp.GetType("ValheimCommunityPatch.Patches.Performance.SceneIdleSkipPatch", true)!;
        new Harmony("MidnightsFX.ValheimCommunityPatch").Patch(AccessTools.Method(typeof(ZNetScene), "CreateDestroyObjects"),
            prefix: new HarmonyMethod(AccessTools.Method(idle, "CreateDestroyObjectsPrefix")),
            postfix: new HarmonyMethod(AccessTools.Method(idle, "CreateDestroyObjectsPostfix")));
    }
    private static void RegisterTreeVendor() => new Harmony("MidnightsFX.ImpactfulSkills").PatchAll(
        impact.GetType("ImpactfulSkills.patches.Woodcutting+DamageHandler_Apply_Patch", true)!);
    private static void BindSkillConfig()
    {
        var cfg = new ConfigFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox/skills-never-written.cfg"), false) { SaveOnConfigSet = false };
        Type config = impact.GetType("ImpactfulSkills.ValConfig", true)!;
        foreach (string key in new[] { "EnableAnimalWhisper", "EnableMining", "EnableWoodcutting" })
            AccessTools.Field(config, key).SetValue(null, cfg.Bind("test", key, true));
        AccessTools.Field(config, "AnimalHandlingLootRange").SetValue(null, cfg.Bind("test", "AnimalHandlingLootRange", 20f));
    }
    private static bool Active => (bool)simulation.GetProperty("Active", Flags)!.GetValue(null);
    private static MethodInfo ForkVcpHook() => AccessTools.Method(fork.GetType(
        "Valheim_Serverside.Features.Compat_ValheimCommunityPatch+ZNetScene_Awake_Patch", true), "Postfix");
    private static void Prepare()
    {
        Func<string, Assembly?> resolve = guid => guid == "MVP.Valheim_Serverside_Simulations" ? fork
            : guid == "dev.ontrigger.vpo" ? vpo : guid == "MidnightsFX.ValheimCommunityPatch" ? vcp : null;
        Call(simulation, "Prepare", resolve);
    }
    private static object? Call(Type type, string name, params object[] args) => type.GetMethod(name, Flags)!.Invoke(null, args);
    private static HarmonyMethod Hook(string name) => new HarmonyMethod(typeof(SimulationTests).GetMethod(name, Flags)!);
    private static void Noop() { }
    private static void FailRemoval(MethodInfo patch)
    {
        if (patch.DeclaringType!.Assembly == vpo && ++removals == 2) throw new InvalidOperationException("test removal failure");
    }
    private static IEnumerable<Patch> Patches(MethodBase method)
    {
        var p = Harmony.GetPatchInfo(method)!;
        return p.Prefixes.Concat(p.Postfixes).Concat(p.Transpilers).Concat(p.Finalizers).Concat(p.ILManipulators);
    }
    private static string[] Snapshot() => Harmony.GetAllPatchedMethods().SelectMany(m => Patches(m).Select(p =>
        m.DeclaringType!.FullName + "." + m.Name + ":" + p.owner + ":" + p.PatchMethod.DeclaringType!.FullName + "." + p.PatchMethod.Name))
        .OrderBy(s => s).ToArray();
    private static bool IsConflict(MethodInfo method) => method.DeclaringType!.FullName!.Contains("ZNetSceneObjectManagementPatch")
        || method.DeclaringType.FullName.Contains("ZDOManReleaseNearbyPatch") || method.DeclaringType.FullName.Contains("SceneIdleSkipPatch");
    private static void Cleanup()
    {
        foreach (string owner in Harmony.GetAllPatchedMethods().SelectMany(m => Harmony.GetPatchInfo(m)!.Owners).Distinct().ToArray())
            if (owner.StartsWith("DeepNorthCompat.") || owner == Core || owner == ForkVcp || owner == "dev.ontrigger.vpo" || owner == "MidnightsFX.ValheimCommunityPatch"
                || owner == "MidnightsFX.ImpactfulSkills" || owner == "akoozie.valheimtune") new Harmony(owner).UnpatchSelf();
        AccessTools.Field(ForkVcpHook().DeclaringType, "s_done").SetValue(null, false);
    }
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidOperationException) { return; } catch (NotSupportedException) { return; }
        catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException || e.InnerException is NotSupportedException) { return; }
        throw new Exception("expected rejection");
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
