using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using BepInEx.Configuration;
using DeepNorthCompat;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

// Runs the vendor postfix on synthetic HUD state; the native HUD body and Unity color setter are stubbed.
internal static class MagicRevampHudTests
{
    private const string VendorOwner = "blacks7ar.MagicRevamp";
    private const string Fixture = "DeepNorthCompat.Tests.MagicRevamp.Hud";
    private static readonly List<IntPtr> pointers = new List<IntPtr>();
    private static readonly List<string> errors = new List<string>();
    private static Assembly magic = null!;
    private static MethodInfo target = null!, vendor = null!;
    private static Hud hud = null!;
    private static Player player = null!;
    private static object toggle = null!;
    private static Type toggleType = null!;
    private static ConfigEntry<Color> spellColor = null!;
    private static Color observed;
    private static int writes, otherCalls;
    private static bool rejectAfterRemoval;
    private static readonly Color DefaultColor = new Color(255, 255, 255, 1);

    internal static void Run(string profile, Action<string, Action> test)
    {
        string[] files = Directory.GetFiles(Path.Combine(profile, "BepInEx/plugins"), "MagicRevamp.dll", SearchOption.AllDirectories);
        if (files.Length == 0)
        {
            test("MagicRevamp HUD remains inactive without the optional mod", () =>
            {
                var messages = new List<string>();
                Action<string> previous = CompatibilityInstaller.Info;
                try
                {
                    CompatibilityInstaller.Info = messages.Add;
                    MagicRevampHudPatch.Prepare(null); MagicRevampHudPatch.Verify();
                    Check(messages.Any(m => m.Contains("optional mod absent; inactive")), "absent mod is reported inactive");
                }
                finally { CompatibilityInstaller.Info = previous; }
            });
            return;
        }
        magic = Assembly.LoadFrom(files.Single());
        if (Environment.GetEnvironmentVariable("DEEPNORTHCOMPAT_TEST_MODE") == "server")
        {
            test("MagicRevamp HUD is inactive on dedicated servers", () =>
            {
                var messages = new List<string>();
                Action<string> previous = CompatibilityInstaller.Info;
                try
                {
                    CompatibilityInstaller.Info = messages.Add;
                    MagicRevampHudPatch.Prepare(magic); MagicRevampHudPatch.Verify();
                    Check(messages.Any(m => m.Contains("client-only; inactive on dedicated server")), "server role is reported inactive");
                }
                finally { CompatibilityInstaller.Info = previous; }
            });
            return;
        }
        Type patch = magic.GetType("MagicRevamp.Patches.HudPatch", true)!;
        target = AccessTools.Method(typeof(Hud), "UpdateCrosshair", new[] { typeof(Player), typeof(float) });
        vendor = AccessTools.Method(patch, "UpdateCrosshair_Postfix");
        var fixture = new Harmony(Fixture);
        FieldInfo helper = AccessTools.Field(typeof(BepInEx.ThreadingHelper), "<Instance>k__BackingField");
        object? previousHelper = helper.GetValue(null);
        Player previousPlayer = Player.m_localPlayer;
        Action<string> previousInfo = CompatibilityInstaller.Info, previousError = CompatibilityInstaller.Error;

        try
        {
            var threading = (BepInEx.ThreadingHelper)FormatterServices.GetUninitializedObject(typeof(BepInEx.ThreadingHelper));
            AccessTools.Field(typeof(BepInEx.ThreadingHelper), "_invokeLock").SetValue(threading, new object());
            helper.SetValue(null, threading);
            var config = new ConfigFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox/magic-hud-unsaved.cfg"), false)
                { SaveOnConfigSet = false };
            toggleType = magic.GetType("MagicRevamp.Functions.Toggle", true)!;
            MethodInfo bind = typeof(ConfigFile).GetMethods().Single(m => m.Name == "Bind" && m.IsGenericMethodDefinition
                && m.GetParameters().Length == 4 && m.GetParameters()[0].ParameterType == typeof(string)
                && m.GetParameters()[3].ParameterType == typeof(string));
            toggle = bind.MakeGenericMethod(toggleType).Invoke(config, new[] { "2- General", "Spell Hud", Enum.Parse(toggleType, "On"), "" })!;
            spellColor = config.Bind("2- General", "Spell Crosshair Color", DefaultColor, "");
            Type plugin = magic.GetType("MagicRevamp.Plugin", true)!;
            FieldInfo enabledField = AccessTools.Field(plugin, "_enableSpellHud");
            FieldInfo colorField = AccessTools.Field(plugin, "_spellCrosshairColor");
            object? previousEnabled = enabledField.GetValue(null), previousColor = colorField.GetValue(null);
            enabledField.SetValue(null, toggle); colorField.SetValue(null, spellColor);

            try
            {
                fixture.Patch(AccessTools.PropertySetter(typeof(Graphic), "color"), prefix: Hook(nameof(CaptureColor)));
                fixture.Patch(AccessTools.Method(typeof(UnityEngine.Object), "op_Equality"), prefix: Hook(nameof(Equal)));
                fixture.Patch(AccessTools.Method(typeof(UnityEngine.Object), "op_Inequality"), prefix: Hook(nameof(NotEqual)));
                fixture.Patch(target, prefix: Hook(nameof(SkipOriginal)), postfix: Hook(nameof(OtherPostfix)));
                hud = (Hud)FormatterServices.GetUninitializedObject(typeof(Hud));
                player = (Player)FormatterServices.GetUninitializedObject(typeof(Player));
                hud.m_crosshair = Fake<Image>();
                Player.m_localPlayer = player;
                CompatibilityInstaller.Info = _ => { };
                CompatibilityInstaller.Error = errors.Add;

                test("MagicRevamp HUD rejects a wrong owner and duplicate registration", RejectUnexpectedHooks);
                test("MagicRevamp HUD restores the vendor hook if removal fails", RestoreAfterFailure);
                test("MagicRevamp HUD rejects an unsupported build", UnsupportedBuild);
                new Harmony(VendorOwner).Unpatch(target, vendor);
                test("MagicRevamp HUD removes only its native crosshair overwrite", ColorsAndRemoval);
            }
            finally
            {
                enabledField.SetValue(null, previousEnabled); colorField.SetValue(null, previousColor);
            }
        }
        finally
        {
            rejectAfterRemoval = false;
            new Harmony(Fixture).UnpatchSelf();
            new Harmony(VendorOwner).UnpatchSelf();
            new Harmony(MagicRevampHudPatch.Owner).UnpatchSelf();
            MagicRevampHudPatch.Reset();
            CompatibilityInstaller.Info = previousInfo; CompatibilityInstaller.Error = previousError;
            Player.m_localPlayer = previousPlayer; helper.SetValue(null, previousHelper);
            foreach (IntPtr pointer in pointers) Marshal.FreeHGlobal(pointer);
            pointers.Clear();
        }
    }

    private static void ColorsAndRemoval()
    {
        errors.Clear(); Register(VendorOwner);
        SetEnabled(true); spellColor.Value = DefaultColor;
        Invoke(player, Color.yellow);
        Check(writes == 1 && Same(observed, DefaultColor), "the vendor overwrites the native hover color even with its default setting");
        Invoke((Player)FormatterServices.GetUninitializedObject(typeof(Player)), Color.yellow);
        Check(writes == 0 && Same(observed, Color.yellow), "the vendor ignores a nonlocal player");
        SetEnabled(false); Invoke(player, Color.yellow);
        Check(writes == 1 && Same(observed, Color.white), "the vendor overwrites hover when its spell HUD is disabled");

        MagicRevampHudPatch.Prepare(magic);
        Check(Present(vendor), "prepare waits for deferred vendor registrations");
        MagicRevampHudPatch.Verify();
        Check(errors.Count == 0 && !Present(vendor), "the exact vendor postfix is removed");
        foreach (bool enabled in new[] { true, false })
        {
            SetEnabled(enabled);
            foreach (Color color in new[] { Color.white, Color.red })
            {
                spellColor.Value = color;
                foreach (Color native in new[] { Color.yellow, new Color(1, 1, 1, .5f) })
                {
                    Invoke(player, native);
                    Check(writes == 0 && Same(observed, native) && otherCalls == 1,
                        "native color and the unrelated postfix are retained");
                }
            }
        }
        Player.m_localPlayer = null;
        Invoke(null, Color.yellow);
        Check(writes == 0 && Same(observed, Color.yellow) && otherCalls == 1, "no-player invocation leaves native state alone");
        Player.m_localPlayer = player;
        MagicRevampHudPatch.Prepare(magic); MagicRevampHudPatch.Verify();
        Check(errors.Count == 0 && !Present(vendor), "repeated installation is idempotent");
    }

    private static void RejectUnexpectedHooks()
    {
        errors.Clear(); Register(Fixture);
        MagicRevampHudPatch.Prepare(magic); MagicRevampHudPatch.Verify();
        Check(errors.Count == 1 && Present(vendor), "wrong owner leaves the hook intact");
        new Harmony(Fixture).Unpatch(target, vendor);

        errors.Clear(); Register(VendorOwner); Register(Fixture);
        MagicRevampHudPatch.Prepare(magic); MagicRevampHudPatch.Verify();
        Check(errors.Count == 1 && Harmony.GetPatchInfo(target).Postfixes.Count(p => p.PatchMethod == vendor) == 2,
            "duplicate registrations remain intact");
        new Harmony(Fixture).Unpatch(target, vendor);
    }

    private static void RestoreAfterFailure()
    {
        errors.Clear(); Register(VendorOwner, priority: Priority.High);
        MethodInfo unpatch = AccessTools.Method(typeof(Harmony), "Unpatch", new[] { typeof(MethodBase), typeof(MethodInfo) });
        new Harmony(Fixture).Patch(unpatch, finalizer: Hook(nameof(FailAfterRemoval)));
        rejectAfterRemoval = true;
        MagicRevampHudPatch.Prepare(magic); MagicRevampHudPatch.Verify();
        rejectAfterRemoval = false;
        Patch restored = Harmony.GetPatchInfo(target).Postfixes.Single(p => p.PatchMethod == vendor);
        Check(errors.Count == 1 && restored.owner == VendorOwner && restored.priority == Priority.High
            && restored.before.SequenceEqual(new[] { "hud-before" }) && restored.after.SequenceEqual(new[] { "hud-after" }),
            "rollback restores the vendor owner and ordering metadata");
    }

    private static void UnsupportedBuild()
    {
        try { MagicRevampHudPatch.Prepare(typeof(MagicRevampHudTests).Assembly); throw new Exception("unsupported build accepted"); }
        catch (NotSupportedException) { }
        Check(Present(vendor), "hash rejection leaves the vendor postfix intact");
    }

    private static void Register(string owner, int priority = Priority.Normal) => new Harmony(owner).Patch(target, postfix: new HarmonyMethod(vendor)
        { priority = priority, before = new[] { "hud-before" }, after = new[] { "hud-after" } });
    private static bool Present(MethodInfo method) => Harmony.GetPatchInfo(target)?.Postfixes.Any(p => p.PatchMethod == method) == true;
    private static HarmonyMethod Hook(string name) => new HarmonyMethod(AccessTools.Method(typeof(MagicRevampHudTests), name));
    private static void SetEnabled(bool enabled) => toggle.GetType().GetProperty("Value")!.SetValue(toggle, Enum.Parse(toggleType, enabled ? "On" : "Off"));
    private static void Invoke(Player? who, Color seed) { observed = seed; writes = otherCalls = 0; target.Invoke(hud, new object?[] { who, 0f }); }
    private static bool CaptureColor(Color __0) { observed = __0; writes++; return false; }
    private static bool Equal(UnityEngine.Object __0, UnityEngine.Object __1, ref bool __result) { __result = ReferenceEquals(__0, __1); return false; }
    private static bool NotEqual(UnityEngine.Object __0, UnityEngine.Object __1, ref bool __result) { __result = !ReferenceEquals(__0, __1); return false; }
    private static bool SkipOriginal() => false;
    private static void OtherPostfix() => otherCalls++;
    private static bool Same(Color a, Color b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;
    private static Exception? FailAfterRemoval(Harmony __instance, MethodBase __0, Exception? __exception) =>
        rejectAfterRemoval && __instance.Id == MagicRevampHudPatch.Owner && __0 == target
            ? new InvalidOperationException("Injected failure after removal") : __exception;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private static T Fake<T>() where T : UnityEngine.Object
    {
        var result = (T)FormatterServices.GetUninitializedObject(typeof(T));
        IntPtr pointer = Marshal.AllocHGlobal(sizeof(long)); pointers.Add(pointer); Marshal.WriteInt64(pointer, pointers.Count);
        AccessTools.Field(typeof(UnityEngine.Object), "m_CachedPtr").SetValue(result, pointer);
        return result;
    }
}
