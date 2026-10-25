using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DeepNorthCompat;
using HarmonyLib;
using UnityEngine;
using SysConsole = System.Console;

internal static class IdleSyncTests
{
    private const string Owner = "DeepNorthCompat.Performance.IdleSync";
    private static readonly Type Patch = typeof(Plugin).Assembly.GetType("DeepNorthCompat.IdleSyncPatch", true)!;
    private static readonly FieldInfo Info = AccessTools.Field(typeof(CompatibilityInstaller), "Info");
    private static readonly FieldInfo Error = AccessTools.Field(typeof(CompatibilityInstaller), "Error");
    private static readonly MethodInfo ClientSync = AccessTools.Method(typeof(ZSyncTransform), "ClientSync", new[] { typeof(float) });
    private static readonly MethodInfo OnDisable = AccessTools.Method(typeof(ZSyncTransform), "OnDisable");

    internal static void Run(Action<string, Action> test)
    {
        Case(test, "installs and verifies the idle sync hooks once", () =>
        {
            var messages = new List<string>();
            var errors = new List<string>();
            Install(messages, errors);
            Check(errors.Count == 0, string.Join(Environment.NewLine, errors));
            Check(Harmony.GetPatchInfo(ClientSync)?.Prefixes.Count(patch => patch.owner == Owner
                && patch.PatchMethod.Name == "Skip") == 1, "one idle prefix on ClientSync");
            Check(Harmony.GetPatchInfo(ClientSync)?.Postfixes.Count(patch => patch.owner == Owner
                && patch.PatchMethod.Name == "Settle") == 1, "one settle postfix on ClientSync");
            Check(Harmony.GetPatchInfo(OnDisable)?.Postfixes.Count(patch => patch.owner == Owner
                && patch.PatchMethod.Name == "Forget") == 1, "one forget postfix on OnDisable");
            Check(messages.Count(message => message.StartsWith("Performance.IdleSync: APPLIED", StringComparison.Ordinal)) == 1,
                "one APPLIED status");
            Verify();
            Check(messages.Count(message => message.StartsWith("Performance.IdleSync: APPLIED", StringComparison.Ordinal)) == 1,
                "a second verification does not repeat the status");
        });

        Case(test, "rolls back all idle sync hooks when OnDisable verification fails", () =>
        {
            var messages = new List<string>();
            var errors = new List<string>();
            Prepare(messages, errors);
            new Harmony(Owner).Unpatch(OnDisable, HarmonyPatchType.Postfix, Owner);
            Verify();
            Check(errors.Count(error => error.StartsWith("Performance.IdleSync: NOT APPLIED", StringComparison.Ordinal)) == 1,
                "failed verification reports NOT APPLIED");
            Check(!OwnsHooks(), "failed verification removes every hook owned by the group");
        });

        Case(test, "Converged follows vanilla sync thresholds", () =>
        {
            bool Converged(bool syncPosition, Vector3 position, Vector3 target, Vector3 velocity, float timer,
                bool syncRotation, Quaternion rotation, Quaternion targetRotation) =>
                (bool)AccessTools.Method(Patch, "Converged").Invoke(null, new object[]
                {
                    syncPosition, position, target, velocity, timer, syncRotation, rotation, targetRotation
                });

            Check(Converged(true, Vector3.zero, Vector3.zero, Vector3.zero, 0f, true, Quaternion.identity, Quaternion.identity),
                "resting zero-velocity pose converges");
            Check(!Converged(true, new Vector3(0.0011f, 0, 0), Vector3.zero, Vector3.zero, 0f, true,
                Quaternion.identity, Quaternion.identity), "position beyond 0.001 does not converge");
            Check(Converged(true, new Vector3(0.0009f, 0, 0), Vector3.zero, Vector3.zero, 0f, true,
                Quaternion.identity, Quaternion.identity), "position within 0.001 converges");
            Check(!Converged(true, Vector3.zero, Vector3.zero, Vector3.right, 1.5f, true,
                Quaternion.identity, Quaternion.identity), "pending extrapolation does not converge");
            Check(Converged(true, Vector3.right * 2f, Vector3.zero, Vector3.right, 2f, true,
                Quaternion.identity, Quaternion.identity), "capped extrapolation converges at its target");
            Check(Converged(false, Vector3.right * 10f, Vector3.zero, Vector3.right, 1.5f, true,
                Quaternion.identity, Quaternion.identity), "disabled position sync ignores position and velocity");
            Check(!Converged(true, Vector3.zero, Vector3.zero, Vector3.zero, 0f, true, Quaternion.identity,
                new Quaternion(0f, 0f, 0.008726535f, 0.9999619f)),
                "rotation beyond 0.001 degrees does not converge");
        });
    }

    internal static void RunServer(Action<string, Action> test) => Case(test, "stays inactive on the dedicated server", () =>
    {
        var messages = new List<string>();
        var errors = new List<string>();
        Prepare(messages, errors);
        Verify();
        Check(errors.Count == 0 && messages.Any(message => message.Contains("inactive on dedicated server")),
            "dedicated role logs its inactive status");
        Check(!(bool)AccessTools.Property(Patch, "Active").GetValue(null), "dedicated role leaves the patch inactive");
        Check(!OwnsHooks(), "dedicated role installs no idle sync hooks");
    });

    private static void Case(Action<string, Action> test, string name, Action action)
    {
        Action<string> oldInfo = (Action<string>)Info.GetValue(null);
        Action<string> oldError = (Action<string>)Error.GetValue(null);
        test("idle sync " + name, () =>
        {
            Cleanup();
            try
            {
                action();
            }
            finally
            {
                Cleanup();
                Info.SetValue(null, oldInfo);
                Error.SetValue(null, oldError);
            }
        });
    }

    private static void Install(List<string> messages, List<string> errors)
    {
        Prepare(messages, errors);
        Verify();
    }

    private static void Prepare(List<string> messages, List<string> errors)
    {
        Info.SetValue(null, (Action<string>)(message =>
        {
            messages.Add(message);
            if (message.StartsWith("Performance.IdleSync:", StringComparison.Ordinal)) SysConsole.WriteLine(message);
        }));
        Error.SetValue(null, (Action<string>)errors.Add);
        AccessTools.Method(Patch, "Prepare").Invoke(null, null);
    }

    private static void Verify() => AccessTools.Method(Patch, "Verify").Invoke(null, null);

    private static bool OwnsHooks() => Harmony.GetAllPatchedMethods().Any(method => Harmony.GetPatchInfo(method)!.Owners.Contains(Owner));

    private static void Cleanup()
    {
        new Harmony(Owner).UnpatchSelf();
        AccessTools.Field(Patch, "pending").SetValue(null, false);
        AccessTools.Field(Patch, "enabled").SetValue(null, false);
        AccessTools.Field(Patch, "Checked").SetValue(null, 0L);
        AccessTools.Field(Patch, "Skipped").SetValue(null, 0L);
        ((IDictionary)AccessTools.Field(Patch, "rest").GetValue(null)).Clear();
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}
