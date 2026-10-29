using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using DeepNorthCompat;
using HarmonyLib;
using TMPro;
using UnityEngine;

internal static class PerformanceLookupGuardTests
{
    private const string Foreign = "DeepNorthCompat.Tests.LookupForeign";
    private const string Deferred = "DeepNorthCompat.Tests.LookupDeferred";
    private static bool defer;

    private sealed class Subject
    {
        internal TranspilerGroup Group = null!;
        internal MethodInfo Target = null!;
        // The call that each mutation removes, duplicates, relabels, or puts in an exception block.
        internal MethodInfo Anchor = null!;
        // Mutations the rewrite still applies to. An in-place edit ignores labels and blocks.
        internal int[] Accepted = Array.Empty<int>();
        internal Action Prepare = null!;
        internal Func<List<CodeInstruction>, ILGenerator, IEnumerable<CodeInstruction>> Rewrite = null!;
        // A foreign transpiler that breaks the audited window after the group applies.
        internal string ChangeAnchor = null!;
    }

    internal static void Run(string profile, Action<string, Action> test)
    {
        if (Environment.GetEnvironmentVariable("DEEPNORTHCOMPAT_TEST_MODE") == "server") return;
        Assembly rune = Assembly.LoadFrom(Directory.GetFiles(Path.Combine(profile, "BepInEx/plugins"),
            "RuneMagic.dll", SearchOption.AllDirectories).Single());
        MethodInfo isUnlockable = AccessTools.Method(rune.GetType("ValheimMod.UnlockManager", true), "isUnlockable");

        Subject[] subjects =
        {
            new Subject
            {
                Group = VisibleLocalizationPatch.Group,
                Target = AccessTools.Method(typeof(Localization), "ReLocalizeVisible"),
                Anchor = AccessTools.Method(typeof(GameObject), "GetComponentsInChildren", new[] { typeof(bool) }).MakeGenericMethod(typeof(TMP_Text)),
                Accepted = new[] { 2, 3 },
                Prepare = VisibleLocalizationPatch.Prepare,
                Rewrite = (code, _) => VisibleLocalizationPatch.Rewrite(code),
                ChangeAnchor = nameof(InsertBeforeTextSearch)
            },
            new Subject
            {
                Group = RuneRequirementPatch.Group,
                Target = isUnlockable,
                Anchor = AccessTools.PropertyGetter(typeof(UnityEngine.Object), "name"),
                Prepare = () => RuneRequirementPatch.Prepare(rune),
                Rewrite = (code, generator) => RuneRequirementPatch.Rewrite(code, generator, isUnlockable),
                ChangeAnchor = nameof(InsertBeforeNameRead)
            }
        };

        foreach (Subject subject in subjects) Run(subject, test);
    }

    private static void Run(Subject subject, Action<string, Action> test)
    {
        string group = subject.Group.Name;
        string owner = subject.Group.Owner;
        MethodInfo target = subject.Target;

        void Reset()
        {
            defer = false;
            new Harmony(Deferred).UnpatchSelf();
            new Harmony(Foreign).UnpatchSelf();
            new Harmony(owner).UnpatchSelf();
        }

        void Case(string label, Action<List<string>> action) => test(group + " " + label, () =>
        {
            Reset();
            Action<string> saved = CompatibilityInstaller.Error;
            var errors = new List<string>();
            CompatibilityInstaller.Error = errors.Add;

            try { action(errors); }
            finally { Reset(); CompatibilityInstaller.Error = saved; }
        });

        Case("guards changed IL without partial edits", errors =>
        {
            subject.Prepare();
            for (int variant = 0; variant < 5; variant++)
            {
                List<CodeInstruction> code = PatchProcessor.GetOriginalInstructions(target);
                int site = code.FindIndex(i => i.Calls(subject.Anchor));
                ILGenerator generator = new DynamicMethod("rejected", typeof(void), Type.EmptyTypes).GetILGenerator();
                switch (variant)
                {
                    case 0: code.RemoveAt(site); break;
                    case 1: code.Insert(site, new CodeInstruction(code[site])); break;
                    case 2: code[site].labels.Add(generator.DefineLabel()); break;
                    case 3: code[site].blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock)); break;
                    case 4: code[site - 1].opcode = OpCodes.Nop; break;
                }

                List<CodeInstruction> before = code.Select(i => new CodeInstruction(i)).ToList();
                List<CodeInstruction> rewritten = subject.Rewrite(code, generator).ToList();
                bool accepted = subject.Accepted.Contains(variant);
                Check(Same(before, rewritten) != accepted && Same(before, code),
                    "mutation " + variant + (accepted ? " was rejected or changed input IL" : " changed input IL"));
            }

            subject.Group.Verify();
            Check(errors.Count == 1 && errors[0].StartsWith(group + ": NOT APPLIED"), "rejection was not reported");
            Check(!Harmony.HasAnyPatches(owner), "rejected group remained registered");
        });

        Case("rolls back an unexecuted deferred transpiler without removing other owners", errors =>
        {
            new Harmony(Foreign).Patch(target, prefix: Hook(nameof(Noop)));
            new Harmony(Deferred).Patch(AccessTools.Method(AccessTools.TypeByName("HarmonyLib.PatchFunctions"), "UpdateWrapper"),
                prefix: Hook(nameof(SkipWrapper)));

            defer = true;
            subject.Prepare();
            defer = false;
            new Harmony(Deferred).UnpatchSelf();
            subject.Group.Verify();
            subject.Group.Verify();
            Check(errors.Count == 1 && errors[0].StartsWith(group + ": NOT APPLIED"), "deferred rejection was not reported once");
            Check(!Harmony.HasAnyPatches(owner) && Harmony.GetPatchInfo(target)!.Owners.Contains(Foreign), "rollback crossed owners");
        });

        Case("reports a lost rewrite when a later mod changes its anchor", errors =>
        {
            subject.Prepare();
            subject.Group.Verify();
            subject.Prepare();
            subject.Group.Verify();
            Check(Harmony.GetPatchInfo(target)!.Transpilers.Count(p => p.owner == owner) == 1, "duplicate installation");

            new Harmony(Foreign).Patch(target, transpiler: Hook(subject.ChangeAnchor, Priority.First));
            new Harmony(Foreign).Patch(target, prefix: Hook(nameof(Noop)));
            Check(errors.Count == 1 && errors[0].StartsWith(group + ": NO LONGER APPLIED"), "lost rewrite was not reported once");
        });
    }

    // Each inserted nop preserves execution but invalidates the audited contiguous window.
    private static IEnumerable<CodeInstruction> InsertBeforeTextSearch(IEnumerable<CodeInstruction> instructions) =>
        InsertNopBefore(instructions, "GetComponentsInChildren");

    private static IEnumerable<CodeInstruction> InsertBeforeNameRead(IEnumerable<CodeInstruction> instructions) =>
        InsertNopBefore(instructions, "get_name");

    private static IEnumerable<CodeInstruction> InsertNopBefore(IEnumerable<CodeInstruction> instructions, string method)
    {
        List<CodeInstruction> code = instructions.ToList();
        int site = code.FindIndex(i => i.operand is MethodInfo called && called.Name == method);
        code.Insert(site, new CodeInstruction(OpCodes.Nop));
        return code;
    }

    private static bool Same(IList<CodeInstruction> left, IList<CodeInstruction> right) => left.Count == right.Count
        && left.Zip(right, (a, b) => a.opcode == b.opcode && Equals(a.operand, b.operand)
            && a.labels.SequenceEqual(b.labels) && a.blocks.SequenceEqual(b.blocks)).All(v => v);

    private static bool SkipWrapper(ref MethodInfo? __result)
    {
        if (!defer) return true;
        __result = null;
        return false;
    }
    private static void Noop() { }
    private static HarmonyMethod Hook(string name, int priority = Priority.Normal) =>
        new HarmonyMethod(AccessTools.Method(typeof(PerformanceLookupGuardTests), name)) { priority = priority };
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
