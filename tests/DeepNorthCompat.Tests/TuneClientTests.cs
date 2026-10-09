using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using BepInEx;
using HarmonyLib;
using UnityEngine;

internal static class TuneClientTests
{
    private static readonly List<IntPtr> pointers = new List<IntPtr>();
    private static readonly Dictionary<Behaviour, bool> enabled = new Dictionary<Behaviour, bool>();
    private static readonly List<(Skills.SkillType Skill, float Amount)> received = new List<(Skills.SkillType, float)>();
    private static int sequence;
    private static bool SetEnabled(Behaviour __instance, bool __0) { enabled[__instance] = __0; return false; }
    private static bool Skip() => false;
    private static void Noop() { }
    private static bool UID(ref long __result) { __result = 900; return false; }
    private static bool LocalFactor(ref float __result) { __result = 0.75f; return false; }
    private static bool Award(Skills.SkillType __0, float __1) { received.Add((__0, __1)); return false; }
    private static HarmonyMethod Hook(string name) => new HarmonyMethod(AccessTools.Method(typeof(TuneClientTests), name));
    private static T Fake<T>() where T : UnityEngine.Object
    {
        var value = (T)FormatterServices.GetUninitializedObject(typeof(T));
        IntPtr pointer = Marshal.AllocHGlobal(sizeof(long)); pointers.Add(pointer); Marshal.WriteInt32(pointer, ++sequence);
        AccessTools.Field(typeof(UnityEngine.Object), "m_CachedPtr").SetValue(value, pointer); return value;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    internal static void Run(string lab, Action<string, Action> test)
    {
        AccessTools.Method(typeof(BepInEx.Paths), "SetExecutablePath").Invoke(null, new object[]
        {
            typeof(TuneClientTests).Assembly.Location, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox/BepInEx"),
            Environment.GetEnvironmentVariable("DEEPNORTHCOMPAT_MANAGED_PATH")!, new[] { Path.Combine(lab, "BepInEx/core") }
        });
        string? upstream = Environment.GetEnvironmentVariable("DEEPNORTHCOMPAT_VENDOR_PATH");
        if (upstream == null) throw new Exception("Set DEEPNORTHCOMPAT_VENDOR_PATH to the audited Tune fixture directory.");
        Assembly tune = Assembly.LoadFrom(Path.Combine(upstream, "tune.dll"));
        Type tunePatch = typeof(DeepNorthCompat.Plugin).Assembly.GetType("DeepNorthCompat.TuneClientPatch", true)!;
        Type tuneType = tune.GetType("ValheimTune.Plugin", true)!;
        FieldInfo instance = AccessTools.Field(tuneType, "Instance");
        if (Environment.GetEnvironmentVariable("DEEPNORTHCOMPAT_TEST_MODE") == "server")
        {
            test("Tune profile policy retains dedicated server hooks", () =>
            {
                var cfg = new BepInEx.Configuration.ConfigFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox/tune-never-written.cfg"), false) { SaveOnConfigSet = false };
                Type settings = tune.GetType("ValheimTune.Cfg", true)!;
                AccessTools.Method(settings, "Bind").Invoke(null, new object[] { cfg });
                foreach (string key in new[] { "OverrideSendRate", "OverrideSendWindow", "TopKSort", "SaveDirtyFix", "DeadZdoPrune" })
                    ((BepInEx.Configuration.ConfigEntry<bool>)AccessTools.Field(settings, key).GetValue(null)).Value = false;
                AccessTools.Field(tuneType, "Log").SetValue(null, new BepInEx.Logging.ManualLogSource("offline Tune"));
                new Harmony("akoozie.valheimtune").PatchAll(tune);
                MethodBase[] before = Harmony.GetAllPatchedMethods().Where(m => Harmony.GetPatchInfo(m)!.Owners.Contains("akoozie.valheimtune")).ToArray();
                AccessTools.Method(tunePatch, "Prepare").Invoke(null, new object[] { tune });
                AccessTools.Method(tunePatch, "Verify").Invoke(null, null);
                Check(before.Length > 0 && before.All(m => Harmony.GetPatchInfo(m)!.Owners.Contains("akoozie.valheimtune")), "server Tune hooks changed");
                new Harmony("akoozie.valheimtune").UnpatchSelf();
            });
            return;
        }
        var info = new List<string>(); var errors = new List<string>();
        void Case(string name, Action action) => test("Tune policy " + name, () =>
        {
            DeepNorthCompat.CompatibilityInstaller.Install(_ => null, info.Add, info.Add, errors.Add);
            info.Clear(); errors.Clear();
            try { action(); }
            finally
            {
                foreach (string owner in new[] { "akoozie.valheimtune", "DeepNorthCompat.Tests.Tune", "DeepNorthCompat.Tests.ClientSkills" }) new Harmony(owner).UnpatchSelf();
                instance.SetValue(null, null); Player.m_localPlayer = null; enabled.Clear(); received.Clear();
                foreach (IntPtr pointer in pointers) Marshal.FreeHGlobal(pointer);
                pointers.Clear();
            }
        });
        Case("disables Tune Update and only Tune's Harmony registrations", () =>
        {
            var plugin = (BaseUnityPlugin)FormatterServices.GetUninitializedObject(tuneType);
            // This scene fixture represents Unity's enabled component property.
            IntPtr pointer = Marshal.AllocHGlobal(sizeof(long)); pointers.Add(pointer); Marshal.WriteInt32(pointer, ++sequence);
            AccessTools.Field(typeof(UnityEngine.Object), "m_CachedPtr").SetValue(plugin, pointer);
            instance.SetValue(null, plugin);
            var fixture = new Harmony("DeepNorthCompat.Tests.Tune");
            fixture.Patch(AccessTools.PropertySetter(typeof(Behaviour), "enabled"), prefix: Hook(nameof(SetEnabled)));
            MethodInfo target = AccessTools.Method(typeof(ZNetScene), "CreateDestroyObjects");
            new Harmony("akoozie.valheimtune").Patch(target, prefix: Hook(nameof(Skip)));
            fixture.Patch(target, postfix: Hook(nameof(Noop)));
            AccessTools.Method(tunePatch, "Prepare").Invoke(null, new object[] { tune });
            AccessTools.Method(tunePatch, "Verify").Invoke(null, null);
            Check(errors.Count == 0 && enabled.TryGetValue(plugin, out bool value) && !value, "Tune component still runs");
            Check(!Harmony.GetPatchInfo(target)!.Owners.Contains("akoozie.valheimtune"), "Tune patches remain");
            Check(Harmony.GetPatchInfo(target)!.Owners.Contains(fixture.Id), "foreign patches removed");
        });
        Case("missing Tune instance logs a failed gate without breaking startup", () =>
        {
            instance.SetValue(null, null);
            AccessTools.Method(tunePatch, "Prepare").Invoke(null, new object[] { tune });
            AccessTools.Method(tunePatch, "Verify").Invoke(null, null);
            Check(errors.Count == 1 && errors[0].Contains("required launch gate failed"), "missing-instance diagnostic");
        });
    }
}
