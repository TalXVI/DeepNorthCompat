using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using DeepNorthCompat;
using HarmonyLib;

internal static class VpoBurstTests
{
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Type Patch = typeof(Plugin).Assembly.GetType("DeepNorthCompat.VpoBurstPatch", true)!;
    private static readonly List<string> Errors = new List<string>();
    private static string loadedPath = "";
    private static bool probe;

    internal static void Run(string lab, Action<string, Action> test)
    {
        Assembly vpo = Assembly.LoadFrom(Directory.GetFiles(Path.Combine(lab, "BepInEx/plugins"), "ValheimPerformanceOptimizations.dll", SearchOption.AllDirectories).Single());
        Type loader = vpo.GetType("ValheimPerformanceOptimizations.BurstLoader", true)!;
        var fixture = new Harmony("DeepNorthCompat.Tests.Burst");
        void Cleanup()
        {
            new Harmony("DeepNorthCompat.VPO.Burst").UnpatchSelf(); fixture.UnpatchSelf();
            AccessTools.Field(loader, "<LibraryLoaded>k__BackingField").SetValue(null, false);
            AccessTools.Field(loader, "<JobsAreBursted>k__BackingField").SetValue(null, false);
            AccessTools.Field(Patch, "pending").SetValue(null, null);
            Errors.Clear(); loadedPath = "";
        }
        void Case(string name, Action action) => test("VPO Burst " + name, () =>
        {
            Cleanup(); CompatibilityInstaller.Install(_ => null, _ => { }, _ => { }, Errors.Add);
            try { action(); } finally { Cleanup(); }
        });
        void Prepare() => Call("Prepare", vpo);
        bool server = Environment.GetEnvironmentVariable("DEEPNORTHCOMPAT_TEST_MODE") == "server";
        if (!server)
        {
            Case("client retains upstream initialization", () =>
            {
                Prepare(); Call("Verify");
                Check(!Harmony.GetPatchInfo(AccessTools.Method(loader, "Initialize"))?.Owners.Contains("DeepNorthCompat.VPO.Burst") ?? true, "client initializer modified");
                Check(Errors.Count == 0, string.Join("\n", Errors));
            });
            return;
        }
        Type burst = Assembly.Load("Unity.Burst").GetType("Unity.Burst.BurstRuntime", true)!;
        void NativeFixtures(bool value)
        {
            probe = value;
            fixture.Patch(AccessTools.Method(burst, "LoadAdditionalLibrary"), prefix: Hook(nameof(LoadLibrary)));
            fixture.Patch(AccessTools.Method(loader, "ProbeIsBursted"), prefix: Hook(nameof(Probe)));
            // The vendor logger normally comes from its plugin's Awake.
            Type plugin = vpo.GetType("ValheimPerformanceOptimizations.ValheimPerformanceOptimizations", true)!;
            plugin.GetProperty("Logger", Flags | BindingFlags.DeclaredOnly)!.SetValue(null, new BepInEx.Logging.ManualLogSource("Burst fixture"));
        }
        Case("Windows dedicated selects the native DLL and runs the vendor probe", () =>
        {
            NativeFixtures(true); Prepare(); Call("Verify");
            Check(loadedPath == Path.Combine(Path.GetDirectoryName(vpo.Location)!, "VPOBurst_win_x86_64.dll"), "WindowsServer rejected or selected the wrong library");
            Check((bool)AccessTools.Property(loader, "LibraryLoaded").GetValue(null) && (bool)AccessTools.Property(loader, "JobsAreBursted").GetValue(null), "vendor Burst state inactive");
            Check(Errors.Count == 0, string.Join("\n", Errors));
        });
        Case("failed vendor probe rejects the launch gate and removes the rewrite", () =>
        {
            NativeFixtures(false); Prepare(); Call("Verify");
            Check(Errors.Count == 1 && Errors[0].Contains("NOT APPLIED"), "probe failure hidden");
            Check(!Harmony.GetPatchInfo(AccessTools.Method(loader, "Initialize"))?.Owners.Contains("DeepNorthCompat.VPO.Burst") ?? true, "failed initialization left rewrite installed");
        });
        Case("foreign platform rewrite rejects initialization without removing foreign hooks", () =>
        {
            NativeFixtures(true);
            fixture.Patch(AccessTools.Method(loader, "Initialize"), transpiler: Hook(nameof(ChangePlatformRead)));
            Prepare(); Call("Verify");
            Check(Errors.Count == 1 && Errors[0].Contains("NOT APPLIED"), "unexpected initializer IL accepted");
            Check(loadedPath == "", "rejected initializer loaded the library");
            string[] owners = Harmony.GetPatchInfo(AccessTools.Method(loader, "Initialize"))!.Owners.ToArray();
            Check(!owners.Contains("DeepNorthCompat.VPO.Burst") && owners.Contains("DeepNorthCompat.Tests.Burst"), "rollback changed foreign hooks");
        });
        Case("changed vendor build is rejected before initialization", () =>
        {
            try { Call("Prepare", typeof(VpoBurstTests).Assembly); throw new Exception("changed VPO accepted"); }
            catch (TargetInvocationException error) when (error.InnerException is NotSupportedException) { }
            Check(loadedPath == "", "changed VPO initialized");
        });
        Case("unknown Windows dedicated game rejects initialization", () =>
        {
            NativeFixtures(true);
            Type guard = typeof(Plugin).Assembly.GetType("DeepNorthCompat.Guard", true)!;
            fixture.Patch(AccessTools.Method(guard, "KnownServerBuild"), prefix: Hook(nameof(UnknownGame)));
            try { Prepare(); throw new Exception("unknown Windows dedicated game accepted"); }
            catch (TargetInvocationException error) when (error.InnerException is NotSupportedException) { }
            Check(loadedPath == "" && AccessTools.Field(Patch, "pending").GetValue(null) == null,
                "unknown game initialized or retained pending native work");
        });
    }

    private static bool LoadLibrary(string __0, ref bool __result) { loadedPath = __0; __result = true; return false; }
    private static bool Probe(ref bool __result) { __result = probe; return false; }
    private static bool UnknownGame(ref bool __result) { __result = false; return false; }
    private static IEnumerable<CodeInstruction> ChangePlatformRead(IEnumerable<CodeInstruction> instructions)
    {
        bool changed = false;
        MethodInfo platform = AccessTools.PropertyGetter(typeof(UnityEngine.Application), "platform");
        foreach (CodeInstruction instruction in instructions)
        {
            if (!changed && instruction.Calls(platform))
            {
                changed = true;
                yield return new CodeInstruction(instruction) { opcode = OpCodes.Ldc_I4, operand = 44 };
            }
            else yield return instruction;
        }
    }
    private static HarmonyMethod Hook(string name) => new HarmonyMethod(AccessTools.Method(typeof(VpoBurstTests), name));
    private static object? Call(string name, params object?[] args) => Patch.GetMethod(name, Flags)!.Invoke(null, args);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
