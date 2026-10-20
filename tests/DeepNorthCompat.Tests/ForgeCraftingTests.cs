using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using BepInEx.Configuration;
using ConditionalConfigSync;
using DeepNorthCompat;
using HarmonyLib;
using ConfigSync = ConditionalConfigSync.ConditionalConfigSync;

internal static class ForgeCraftingTests
{
    private const string Vendor = "Azumatt.AzuAntiArthriticCrafting";
    private const string Owner = "DeepNorthCompat.Crafting.ForgeOfPotential";
    private const string ForeignOwner = "DeepNorthCompat.Tests.Forge";
    private static readonly MethodInfo Target = AccessTools.Method(typeof(InventoryGui), "UpdateRecipe");
    private static readonly Type Patch = typeof(Plugin).Assembly.GetType("DeepNorthCompat.ForgeCraftingPatch", true)!;
    private static ConfigEntry<bool> enabled = null!;

    internal static void Run(string profile, Action<string, Action> test)
    {
        // The offline host does not run the standalone CCS bootstrap's Awake.
        FieldInfo runtimeInitialized = AccessTools.Field(typeof(ConfigSync), "runtimeInitialized");
        bool initialized = (bool)runtimeInitialized.GetValue(null);
        runtimeInitialized.SetValue(null, true);
        try { RunCases(profile, test); }
        finally { runtimeInitialized.SetValue(null, initialized); }
    }

    private static void RunCases(string profile, Action<string, Action> test)
    {
        var config = new ConfigFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox", "forge.cfg"), false)
        {
            SaveOnConfigSet = false
        };
        object sync = AccessTools.Method(typeof(Plugin), "CreateSync").Invoke(null, new object[] { "1.3.16" });
        AccessTools.Method(Patch, "BindConfig").Invoke(null, new[] { config, sync });
        Check(config.TryGetEntry("Crafting", "Forge of Potential speed", out enabled), "forge config bound");
        test("forge registers only its toggle as conditional and server-controlled by default", () =>
        {
            SyncedConfigEntry<bool>? synced = ConfigSync.ConfigData(enabled);
            Check(synced != null && synced.SyncMode == ConfigSyncMode.Conditional && synced.IsServerControlled,
                "server owns the forge setting unless policy releases it");
            ConfigEntry<bool> diagnostics = config.Bind("Diagnostics", "Enabled", false);
            Check(ConfigSync.ConfigData(diagnostics) == null, "diagnostics remain local");
        });

        Assembly vendor = Assembly.LoadFrom(Path.Combine(profile, "BepInEx", "plugins", "Azumatt-AAA_Crafting", "AzuAntiArthriticCrafting.dll"));
        if (Environment.GetEnvironmentVariable("DEEPNORTHCOMPAT_TEST_MODE") == "server")
        {
            test("forge speed remains inactive on dedicated servers", () =>
            {
                Reset();
                try
                {
                    var messages = new List<string>();
                    CompatibilityInstaller.Install(guid => guid == Vendor ? vendor : null, messages.Add, _ => { }, _ => { });
                    CompatibilityInstaller.Verify();
                    Check(!Installed() && messages.Any(message => message.StartsWith("Crafting.ForgeOfPotential: client-only")),
                        "dedicated role has no forge hook");
                }
                finally { Reset(); }
            });
            return;
        }

        using var aaa = new AaaCraftingFixture(vendor);
        void Case(string name, Action action) => test("forge " + name, () =>
        {
            Reset();
            aaa.Set(true);
            try { action(); }
            finally { Reset(); }
        });
        Case("uses AAA duration at every item level and reads live vendor settings", () =>
        {
            Install(vendor);
            Func<InventoryGui, int, float> timer = Timer();
            InventoryGui gui = Gui();
            foreach (int level in new[] { 0, 1, 5, 10 })
                Check(timer(gui, level) == 0.25f, "forge uses configured seconds at item level " + level);
            aaa.Duration.Value = 3f;
            Check(timer(gui, 5) == 3f, "changed duration takes effect without reinstalling");
            aaa.Set(false);
            Check(timer(gui, 5) == 13f, "AAA override off retains vanilla base plus level duration");
            Check(gui.m_upgraderDuration == 8f && gui.m_upgraderDurationPerLevel == 1f, "GUI timing fields remain unchanged");
        });
        Case("rewrite preserves every instruction outside the upgrader sum", () =>
        {
            List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(Target);
            List<CodeInstruction> result = Rewrite(original);
            Check(result.Count == original.Count, "no instructions added or removed");
            int changes = 0;
            for (int i = 0; i < original.Count; i++)
            {
                Check(original[i].labels.SequenceEqual(result[i].labels) && original[i].blocks.SequenceEqual(result[i].blocks),
                    "branch and exception targets retained");
                if (original[i].opcode == result[i].opcode && Equals(original[i].operand, result[i].operand)) continue;
                changes++;
                Check(original[i].opcode == OpCodes.Add && result[i].Calls(AccessTools.Method(Patch, "Duration")), "only forge sum replaced");
            }
            Check(changes == 1, "normal and batch durations, skill scaling, progress and completion retain their instructions");
        });
        Case("toggle changes duration immediately without reinstalling its hook", () =>
        {
            enabled.Value = false;
            Install(vendor);
            Func<InventoryGui, int, float> timer = Timer();
            InventoryGui gui = Gui();
            Check(Installed() && timer(gui, 5) == 13f, "disabled setting retains vanilla timing with AAA override on");
            enabled.Value = true;
            Check(timer(gui, 5) == 0.25f, "a later enabled value applies immediately");
            enabled.Value = false;
            Check(timer(gui, 5) == 13f, "a later disabled value restores vanilla timing");
        });
        Case("rejects an unaudited vendor without leaving hooks", () =>
        {
            var errors = new List<string>();
            CompatibilityInstaller.Install(guid => guid == Vendor ? typeof(ForgeCraftingTests).Assembly : null, _ => { }, _ => { }, errors.Add);
            Check(!Installed() && errors.Any(message => message.StartsWith("Crafting.ForgeOfPotential: NOT APPLIED")), "unsupported build visibly rejected");
        });
        Case("IL mismatch rolls back only its group and reports failure", () =>
        {
            var errors = new List<string>();
            new Harmony(ForeignOwner).Patch(Target, prefix: new HarmonyMethod(AccessTools.Method(typeof(ForgeCraftingTests), nameof(OtherPrefix))));
            CompatibilityInstaller.Install(guid => guid == Vendor ? vendor : null, _ => { }, _ => { }, errors.Add);
            List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(Target);
            foreach (bool duplicate in new[] { false, true })
            {
                var changed = original.Select(instruction => new CodeInstruction(instruction)).ToList();
                int index = changed.FindIndex(instruction => instruction.LoadsField(AccessTools.Field(typeof(InventoryGui), "m_upgraderDuration")));
                if (duplicate) changed.Insert(index, new CodeInstruction(changed[index]));
                else changed.RemoveAt(index);
                List<CodeInstruction> rejected = Rewrite(changed);
                Check(changed.Count == rejected.Count && changed.Zip(rejected, (a, b) => a.opcode == b.opcode && Equals(a.operand, b.operand)).All(same => same),
                    "changed timer IL retained");
            }
            CompatibilityInstaller.Verify();
            Check(!Installed() && Harmony.GetPatchInfo(Target)!.Owners.Contains(ForeignOwner), "foreign hook survives rollback");
            Check(errors.Any(message => message.StartsWith("Crafting.ForgeOfPotential: NOT APPLIED")), "failed verification is visible");
        });
        Case("later patch that breaks the timer IL reports the loss and keeps vanilla timing", () =>
        {
            var errors = new List<string>();
            CompatibilityInstaller.Install(guid => guid == Vendor ? vendor : null, _ => { }, _ => { }, errors.Add);
            CompatibilityInstaller.Verify();
            Check(errors.Count == 0 && Installed(), "forge applied before the later patch");
            new Harmony(ForeignOwner).Patch(Target, transpiler: new HarmonyMethod(AccessTools.Method(typeof(ForgeCraftingTests), nameof(SplitSum)))
                { priority = Priority.First });
            Check(errors.Any(message => message.StartsWith("Crafting.ForgeOfPotential: NO LONGER APPLIED")), "lost rewrite is visible");
            Check(!PatchProcessor.GetCurrentInstructions(Target).Any(instruction => instruction.Calls(AccessTools.Method(Patch, "Duration"))),
                "vanilla timer sum retained");
        });
    }

    // A valid foreign edit that separates the base-duration load from the rest of the sum.
    private static IEnumerable<CodeInstruction> SplitSum(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = instructions.ToList();
        int index = code.FindIndex(instruction => instruction.LoadsField(AccessTools.Field(typeof(InventoryGui), "m_upgraderDuration")));
        code.Insert(index + 1, new CodeInstruction(OpCodes.Nop));
        return code;
    }

    private static void Install(Assembly vendor)
    {
        CompatibilityInstaller.Install(guid => guid == Vendor ? vendor : null, _ => { }, _ => { }, error => throw new Exception(error));
        CompatibilityInstaller.Verify();
    }

    private static InventoryGui Gui()
    {
        var gui = (InventoryGui)FormatterServices.GetUninitializedObject(typeof(InventoryGui));
        gui.m_upgraderDuration = 8f;
        gui.m_upgraderDurationPerLevel = 1f;
        return gui;
    }

    private static bool Installed() => Harmony.GetPatchInfo(Target)?.Owners.Contains(Owner) == true;
    private static void OtherPrefix() { }
    private static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> code) =>
        ((IEnumerable<CodeInstruction>)AccessTools.Method(Patch, "Rewrite").Invoke(null, new object[] { code })).ToList();
    private static void Reset()
    {
        new Harmony(Owner).UnpatchSelf();
        new Harmony(ForeignOwner).UnpatchSelf();
        foreach (string field in new[] { "pending", "applied" }) AccessTools.Field(Patch, field).SetValue(null, false);
        enabled.Value = true;
    }

    // Execute the installed timer expression without Unity scene and inventory UI calls.
    private static Func<InventoryGui, int, float> Timer()
    {
        List<CodeInstruction> code = PatchProcessor.GetCurrentInstructions(Target);
        int start = code.FindIndex(instruction => instruction.LoadsField(AccessTools.Field(typeof(InventoryGui), "m_upgraderDuration"))) - 1;
        Check(start >= 0, "upgrader timer exists");
        var method = new DynamicMethod("ForgeTimer", typeof(float), new[] { typeof(InventoryGui), typeof(int) }, typeof(ForgeCraftingTests), true);
        ILGenerator il = method.GetILGenerator();
        foreach (CodeInstruction instruction in code.Skip(start).Take(8))
        {
            if (instruction.opcode == OpCodes.Ldloc_2) il.Emit(OpCodes.Ldarg_1);
            else if (instruction.operand is FieldInfo field) il.Emit(instruction.opcode, field);
            else if (instruction.operand is MethodInfo call) il.Emit(instruction.opcode, call);
            else il.Emit(instruction.opcode);
        }
        il.Emit(OpCodes.Ret);
        return (Func<InventoryGui, int, float>)method.CreateDelegate(typeof(Func<InventoryGui, int, float>));
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
