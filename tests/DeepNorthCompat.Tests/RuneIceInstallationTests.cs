using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using DeepNorthCompat;
using HarmonyLib;

// Registration and build rejection only. The physics regression runs in native Linux Unity.
internal static class RuneIceInstallationTests
{
    private const string Owner = "DeepNorthCompat.RuneMagic.Ice";

    internal static void Run(string profile, Action<string, Action> test)
    {
        void Install(Assembly? vendor, List<string> messages, List<string> errors)
        {
            CompatibilityInstaller.Install(g => g == "hyleanlegend.RuneMagic" ? vendor : null,
                messages.Add, _ => { }, errors.Add);
            CompatibilityInstaller.Verify();
        }
        void Case(string name, Action action) => test(name, () =>
        {
            try { action(); }
            finally
            {
                foreach (string owner in Harmony.GetAllPatchedMethods().SelectMany(m => Harmony.GetPatchInfo(m)!.Owners)
                    .Where(o => o.StartsWith("DeepNorthCompat.", StringComparison.Ordinal)).Distinct().ToArray())
                    new Harmony(owner).UnpatchSelf();
            }
        });

        Case("RuneMagic ice rejects an unsupported build and leaves absent mods inactive", () =>
        {
            var messages = new List<string>();
            var errors = new List<string>();
            Install(null, messages, errors);
            Check(errors.Count == 0 && !Harmony.HasAnyPatches(Owner), string.Join("\n", errors));
            Install(typeof(RuneIceInstallationTests).Assembly, messages, errors);
            Check(errors.Count(e => e.StartsWith("RuneMagic.Ice: NOT APPLIED")) == 1 && errors.Any(e => e.Contains("Re-audit required.")),
                "unknown RuneMagic build was not rejected: " + string.Join("\n", errors));
            Check(!Harmony.HasAnyPatches(Owner), "unsupported build installed an ice hook");
        });

        string? path = Directory.GetFiles(Path.Combine(profile, "BepInEx/plugins"), "RuneMagic.dll", SearchOption.AllDirectories).SingleOrDefault();
        if (path == null) return;
        Assembly rune = Assembly.LoadFrom(path);
        MethodInfo awake = AccessTools.DeclaredMethod(rune.GetType("ValheimMod.Monobehaviours.IceTimedDestruction", true), "Awake");
        Case("RuneMagic ice initialization is registered and verified on each supported role", () =>
        {
            var messages = new List<string>();
            var errors = new List<string>();
            Install(rune, messages, errors);
            Check(errors.Count == 0, string.Join("\n", errors));
            Check(Harmony.GetPatchInfo(awake)?.Postfixes.Count(p => p.owner == Owner) == 1, "ice initialization hook missing");
            Check(messages.Count(m => m.StartsWith("RuneMagic.Ice: APPLIED")) == 1, "ice startup verification missing");
            CompatibilityInstaller.Verify();
            Check(messages.Count(m => m.StartsWith("RuneMagic.Ice: APPLIED")) == 1, "verification repeated the ice result");
        });
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
