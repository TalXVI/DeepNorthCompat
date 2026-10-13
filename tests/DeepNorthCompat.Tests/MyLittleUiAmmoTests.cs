using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using System.Runtime.InteropServices;
using DeepNorthCompat;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

internal static class MyLittleUiAmmoTests
{
    private static readonly Type Patch = typeof(Plugin).Assembly.GetType("DeepNorthCompat.MyLittleUiAmmoPatch", true)!;
    private const string Ours = "DeepNorthCompat.MyLittleUI.AmmoCount";
    private const string Fixture = "DeepNorthCompat.Tests.MyLittleUiAmmo";
    private static readonly List<(object Label, string Text)> writes = new List<(object, string)>();
    private static readonly List<IntPtr> pointers = new List<IntPtr>();
    private static Assembly mlui = null!;
    private static Type cell = null!;
    private static Type tmp = null!;
    private static MethodInfo target = null!;
    private static MethodInfo setText = null!;

    internal static void Run(string profile, Action<string, Action> test)
    {
        void Case(string name, Action action) => test("my little ui ammo " + name, () =>
        {
            Cleanup();
            try { action(); } finally { Cleanup(); }
        });
        mlui = Assembly.LoadFrom(Path.Combine(profile, "BepInEx", "plugins", "shudnal-MyLittleUI", "MyLittleUI.dll"));
        cell = mlui.GetType("MyLittleUI.AmmoCountIcon+ElementExtraData", true)!;
        target = AccessTools.Method(cell, "UpdateState");
        tmp = AccessTools.Field(cell, "m_ammo").FieldType;
        setText = AccessTools.Method(tmp, "SetText", new[] { typeof(string) });
        Natives();
        // Compiling UpdateState initializes MyLittleUI.MyLittleUI, whose static
        // constructor builds a ConfigSync. The embedded host never runs the
        // standalone plugin's Awake, so the runtime flag is set by hand.
        Type ccs = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("ConditionalConfigSync.ConditionalConfigSync"))
            .FirstOrDefault(t => t != null)
            ?? Assembly.LoadFrom(Path.Combine(profile, "BepInEx", "plugins", "shudnal-ConditionalConfigSync", "ConditionalConfigSync.dll"))
                .GetType("ConditionalConfigSync.ConditionalConfigSync", true)!;
        AccessTools.Field(ccs, "runtimeInitialized").SetValue(null, true);
        Case("rewrites only the ammo label call", () =>
        {
            List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(target);
            List<CodeInstruction> result = Rewrite(original).ToList();
            Check(result.Count == original.Count - 1, "one call removed");
            int index = original.FindIndex(i => i.Calls(setText));
            Check(index >= 3 && result[index - 2].opcode == OpCodes.Ldloc_S
                && Equals(result[index - 2].operand, original[index - 2].operand)
                && result[index - 1].Calls(AccessTools.Method(Patch, "SetCount"))
                && result[index - 2].labels.SequenceEqual(original[index - 2].labels)
                && result[index - 2].blocks.SequenceEqual(original[index - 2].blocks)
                && result[index - 1].labels.SequenceEqual(original[index - 1].labels)
                && result[index - 1].blocks.SequenceEqual(original[index - 1].blocks), "only the ammo count call replaced");
            result.Insert(index, new CodeInstruction(original[index]));
            result[index - 2] = original[index - 2];
            result[index - 1] = original[index - 1];
            Check(Same(original, result), "bait write and every other instruction, label and block preserved");
        });
        Case("rejects changed IL without altering it", () =>
        {
            FieldInfo baitAmount = AccessTools.Field(cell, "baitAmount");
            for (int variant = 0; variant < 3; variant++)
            {
                List<CodeInstruction> source = PatchProcessor.GetOriginalInstructions(target);
                int index = source.FindIndex(i => i.Calls(setText));
                if (variant == 0) source.RemoveAt(index);
                else if (variant == 1) source.Insert(index, new CodeInstruction(source[index]));
                else source[index - 3] = new CodeInstruction(source[index - 3]) { operand = baitAmount };
                Check(Same(source, Rewrite(source).ToList()), "variant " + variant + " retained its instructions");
                Check((bool?)AccessTools.Field(Patch, "rewritten").GetValue(null) == false, "variant " + variant + " flagged rejection");
            }
        });
        Case("writes only changed counts with the exact original text", () =>
        {
            object label = FormatterServices.GetUninitializedObject(tmp.Assembly.GetType("TMPro.TextMeshProUGUI", true)!);
            IntPtr pointer = Marshal.AllocHGlobal(sizeof(long)); pointers.Add(pointer); Marshal.WriteInt32(pointer, 1);
            AccessTools.Field(typeof(Object), "m_CachedPtr").SetValue(label, pointer);
            FieldInfo textField = AccessTools.Field(tmp, "m_text");
            AccessTools.Field(tmp, "m_IsTextBackingStringDirty").SetValue(label, false);
            new Harmony(Fixture).Patch(setText, prefix: Hook(nameof(Recorded)));
            MethodInfo setCount = AccessTools.Method(Patch, "SetCount");
            foreach ((string? text, int count) in new (string?, int)[] { ("12", 12), ("12", 13), ("1", 11), ("11", 1),
                ("05", 5), ("5 ", 5), (" 5", 5), ("", 0), (null, 1), ("0", 0), ("-5", -5),
                (int.MaxValue.ToString(), int.MaxValue), ("2147483646", int.MaxValue) })
            {
                writes.Clear();
                textField.SetValue(label, text);
                setCount.Invoke(null, new object?[] { label, count });
                bool expected = count < 0 || text != count.ToString();
                Check(writes.Count == (expected ? 1 : 0), $"count {count} with text '{text}'");
                if (expected) Check(writes[0].Text == count.ToString() && ReferenceEquals(writes[0].Label, label), "exact original text for " + count);
            }
            for (int count = 0; count <= 10000; count++)
            {
                writes.Clear();
                textField.SetValue(label, count.ToString());
                setCount.Invoke(null, new object[] { label, count });
                Check(writes.Count == 0, "unchanged count skipped for " + count);
                writes.Clear();
                textField.SetValue(label, (count + 1).ToString());
                setCount.Invoke(null, new object[] { label, count });
                Check(writes.Count == 1 && writes[0].Text == count.ToString() && ReferenceEquals(writes[0].Label, label), "changed count written for " + count);
            }
        });
        Case("installs, verifies once, and stays idempotent", () =>
        {
            var infos = new List<string>(); var errors = new List<string>();
            Action<string> previousInfo = Info(), previousError = Error();
            try
            {
                Log(infos.Add, errors.Add);
                Prepare(); Verify();
                Check(errors.Count == 0, string.Join(Environment.NewLine, errors));
                Check(infos.Any(m => m.StartsWith("MyLittleUI.AmmoCount: APPLIED")), "applied line");
                Check(Harmony.GetPatchInfo(target)?.Transpilers.Count(p => p.owner == Ours) == 1, "one owned transpiler");
                string[] before = Snapshot();
                Prepare(); Verify();
                Check(before.Length == 1 && before.SequenceEqual(Snapshot()), "second install kept the same registration");
            }
            finally { Log(previousInfo, previousError); }
        });
        Case("failed verification removes only its own group", () =>
        {
            new Harmony(Fixture).Patch(target, prefix: Hook(nameof(OtherPrefix)));
            Patch foreign = Harmony.GetPatchInfo(target)!.Prefixes.Single(p => p.owner == Fixture);
            var errors = new List<string>();
            Action<string> previousInfo = Info(), previousError = Error();
            try
            {
                Log(_ => { }, errors.Add);
                Prepare();
                Rewrite(new[] { new CodeInstruction(OpCodes.Ret) }).ToArray();
                Verify();
                Check(errors.Count == 1 && errors[0].StartsWith("MyLittleUI.AmmoCount: NOT APPLIED"), "rollback reported once");
                Check(!Harmony.GetAllPatchedMethods().Any(m => Harmony.GetPatchInfo(m)!.Owners.Contains(Ours)), "entire group removed");
                Check(Harmony.GetPatchInfo(target)!.Prefixes.Contains(foreign), "foreign prefix survives rollback");
            }
            finally { Log(previousInfo, previousError); }
        });
    }

    internal static void RunServer(string profile, Action<string, Action> test) => test("my little ui ammo dedicated role remains inactive", () =>
    {
        Cleanup();
        var infos = new List<string>(); var errors = new List<string>();
        Action<string> previousInfo = Info(), previousError = Error();
        try
        {
            Log(infos.Add, errors.Add);
            Prepare(Assembly.LoadFrom(Path.Combine(profile, "BepInEx", "plugins", "shudnal-MyLittleUI", "MyLittleUI.dll")));
            Verify();
            Check(infos.Any(m => m.Contains("client-only; inactive on dedicated server")), "server reports inactive");
            Check(errors.Count == 0, string.Join(Environment.NewLine, errors));
            Check(!Harmony.GetAllPatchedMethods().Any(m => Harmony.GetPatchInfo(m)!.Owners.Contains(Ours)), "server has no group hooks");
        }
        finally { Log(previousInfo, previousError); Cleanup(); }
    });

    private static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> code) =>
        (IEnumerable<CodeInstruction>)AccessTools.Method(Patch, "Rewrite").Invoke(null, new object[] { code, target });
    private static void Prepare() => Prepare(mlui);
    private static void Prepare(Assembly? assembly) => AccessTools.Method(Patch, "Prepare").Invoke(null, new object?[] { assembly });
    private static void Verify() => AccessTools.Method(Patch, "Verify").Invoke(null, null);
    private static string[] Snapshot() => Harmony.GetAllPatchedMethods().SelectMany(m =>
    {
        Patches p = Harmony.GetPatchInfo(m)!;
        return p.Prefixes.Concat(p.Postfixes).Concat(p.Transpilers).Concat(p.Finalizers)
            .Where(h => h.owner == Ours).Select(h => m.Name + ":" + h.PatchMethod.Name + ":" + h.index);
    }).OrderBy(s => s).ToArray();
    private static bool Same(IList<CodeInstruction> a, IList<CodeInstruction> b) => a.Count == b.Count && a.Zip(b,
        (x, y) => x.opcode == y.opcode && Equals(x.operand, y.operand) && x.labels.SequenceEqual(y.labels) && x.blocks.SequenceEqual(y.blocks)).All(v => v);
    private static HarmonyMethod Hook(string name) => new HarmonyMethod(AccessTools.Method(typeof(MyLittleUiAmmoTests), name));
    private static bool Recorded(object __instance, string __0) { writes.Add((__instance, __0)); return false; }
    private static void OtherPrefix() { }
    // TMP_Text's static constructor creates ProfilerMarkers, and the MyLittleUI
    // static constructor resolves the UI layer; the embedded host registers
    // neither icall, so any TMP detour or UpdateState compile would fail.
    private static readonly NativeStub markerStub = MarkerStub;
    private static readonly LayerStub layerStub = Layer;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr NativeStub(IntPtr name, ushort category, int flags, int metadata);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int LayerStub(IntPtr name);
    [DllImport("mono-2.0-bdwgc.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void mono_add_internal_call(string name, IntPtr method);
    private static IntPtr MarkerStub(IntPtr name, ushort category, int flags, int metadata) => IntPtr.Zero;
    private static int Layer(IntPtr name) => 5;
    private static bool natives;
    private static void Natives()
    {
        if (natives) return;
        natives = true;
        mono_add_internal_call("Unity.Profiling.LowLevel.Unsafe.ProfilerUnsafeUtility::CreateMarker_Injected",
            Marshal.GetFunctionPointerForDelegate(markerStub));
        mono_add_internal_call("UnityEngine.LayerMask::NameToLayer_Injected", Marshal.GetFunctionPointerForDelegate(layerStub));
    }
    private static Action<string> Info() => (Action<string>)AccessTools.Field(typeof(CompatibilityInstaller), "Info").GetValue(null);
    private static Action<string> Error() => (Action<string>)AccessTools.Field(typeof(CompatibilityInstaller), "Error").GetValue(null);
    private static void Log(Action<string> info, Action<string> error)
    {
        AccessTools.Field(typeof(CompatibilityInstaller), "Info").SetValue(null, info);
        AccessTools.Field(typeof(CompatibilityInstaller), "Error").SetValue(null, error);
    }
    private static void Cleanup()
    {
        new Harmony(Fixture).UnpatchSelf(); new Harmony(Ours).UnpatchSelf();
        foreach (string field in new[] { "pending", "enabled" }) AccessTools.Field(Patch, field).SetValue(null, false);
        AccessTools.Field(Patch, "rewritten").SetValue(null, null);
        foreach (IntPtr pointer in pointers) Marshal.FreeHGlobal(pointer); pointers.Clear(); writes.Clear();
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
