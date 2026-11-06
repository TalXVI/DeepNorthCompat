using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using DeepNorthCompat;
using HarmonyLib;
using UnityEngine;

// The real vendor prefixes run against the game method. Unity identities, network ownership,
// attack timing, the Zen setting and the animator write are fixtures, not a native scene.
internal static class VpoAnimationTests
{
    private const string Owner = "DeepNorthCompat.VPO.Animation";
    private static readonly List<IntPtr> pointers = new List<IntPtr>();
    private static bool value;
    private static int writes;

    internal static void Run(string lab, Action<string, Action> test)
    {
        string plugins = Path.Combine(lab, "BepInEx/plugins");
        string? vpoPath = Directory.GetFiles(plugins, "ValheimPerformanceOptimizations.dll", SearchOption.AllDirectories).SingleOrDefault();
        string? zenPath = Directory.GetFiles(plugins, "Zen.ModLib.dll", SearchOption.AllDirectories).SingleOrDefault();
        if (vpoPath == null || zenPath == null || Guard.Sha256(vpoPath) != ExpectedBuilds.Vpo200) return;

        Assembly vpo = Assembly.LoadFrom(vpoPath), zen = Assembly.LoadFrom(zenPath);
        Type vpoType = vpo.GetType("ValheimPerformanceOptimizations.Patches.AnimatorParameterCachePatch", true)!;
        Type zenType = zen.GetType("Zen.FixVanilla.FixExploitAttackAnim", true)!;
        MethodInfo vpoPrefix = AccessTools.Method(vpoType, "SetBoolPrefix");
        MethodInfo zenPrefix = AccessTools.Method(zenType, "ZSyncAnimation_SetBool");
        MethodInfo target = AccessTools.Method(typeof(ZSyncAnimation), "SetBool", new[] { typeof(int), typeof(bool) });
        var fixture = new Harmony("DeepNorthCompat.Tests.VpoAnimation");
        var vpoOwner = new Harmony(VpoAnimationPatch.VpoGuid);
        var zenOwner = new Harmony(VpoAnimationPatch.ZenGuid);
        var errors = new List<string>();
        Player previous = Player.m_localPlayer;
        Action<string> previousError = CompatibilityInstaller.Error;

        void Reset()
        {
            new Harmony(Owner).UnpatchSelf();
            vpoOwner.Unpatch(target, vpoPrefix);
            zenOwner.Unpatch(target, zenPrefix);
            errors.Clear(); value = false; writes = 0;
        }

        try
        {
            fixture.Patch(target, transpiler: Hook(nameof(ReplaceOriginal)));
            fixture.Patch(AccessTools.Method(typeof(ZNetView), "IsOwner"), prefix: Hook(nameof(Owned)));
            fixture.Patch(AccessTools.Method(typeof(Character), "GetTimeSinceLastAttack"), prefix: Hook(nameof(AttackedRecently)));
            fixture.Patch(AccessTools.PropertyGetter(zenType, "IsFeatureEnabled"), prefix: Hook(nameof(FeatureOn)));
            CompatibilityInstaller.Error = errors.Add;

            test("VPO animation skips absent optional mods", () =>
            {
                Reset(); VpoAnimationPatch.Prepare(vpo, null); VpoAnimationPatch.Verify();
                Check(!Harmony.HasAnyPatches(Owner) && errors.Count == 0, "an absent mod must leave the group inactive");
            });

            test("VPO animation rejects changed vendor builds", () =>
            {
                Reset();
                Reject(() => VpoAnimationPatch.Prepare(typeof(VpoAnimationTests).Assembly, zen));
                Reject(() => VpoAnimationPatch.Prepare(vpo, typeof(VpoAnimationTests).Assembly));
                Check(!Harmony.HasAnyPatches(Owner), "a changed vendor must not install the ordering prefix");
            });

            test("VPO animation rejects a missing vendor hook", () =>
            {
                Reset();
                vpoOwner.Patch(target, prefix: new HarmonyMethod(vpoPrefix));
                VpoAnimationPatch.Prepare(vpo, zen); VpoAnimationPatch.Verify();
                Check(errors.Any(message => message.Contains("VPO.Animation: NOT APPLIED")) && !Harmony.HasAnyPatches(Owner),
                    "a missing vendor prefix must reject the group");
                Check(Harmony.GetPatchInfo(target).Prefixes.Count(p => p.owner == VpoAnimationPatch.VpoGuid && p.PatchMethod == vpoPrefix) == 1,
                    "rejecting the group must retain the vendor prefix");
            });

            test("VPO animation preserves queued attack state in either vendor registration order", () =>
            {
                foreach (bool zenFirst in new[] { false, true })
                {
                    Reset();
                    if (zenFirst) zenOwner.Patch(target, prefix: new HarmonyMethod(zenPrefix));
                    vpoOwner.Patch(target, prefix: new HarmonyMethod(vpoPrefix));
                    if (!zenFirst) zenOwner.Patch(target, prefix: new HarmonyMethod(zenPrefix));

                    Patch originalVpo = Guard.Sole(target, vpoPrefix, VpoAnimationPatch.VpoGuid, HarmonyPatchType.Prefix);
                    Patch originalZen = Guard.Sole(target, zenPrefix, VpoAnimationPatch.ZenGuid, HarmonyPatchType.Prefix);
                    VpoAnimationPatch.Prepare(vpo, zen); VpoAnimationPatch.Verify();
                    Check(errors.Count == 0 && Harmony.HasAnyPatches(Owner), "the audited hooks must be ordered");
                    Check(Unchanged(originalVpo, Guard.Sole(target, vpoPrefix, VpoAnimationPatch.VpoGuid, HarmonyPatchType.Prefix))
                        && Unchanged(originalZen, Guard.Sole(target, zenPrefix, VpoAnimationPatch.ZenGuid, HarmonyPatchType.Prefix)),
                        "the vendors' original hook metadata must remain unchanged");

                    Player player = Fake<Player>();
                    ZSyncAnimation animation = Fake<ZSyncAnimation>();
                    AccessTools.Field(typeof(ZSyncAnimation), "m_nview").SetValue(animation, Fake<ZNetView>());
                    AccessTools.Field(typeof(Character), "m_zanim").SetValue(player, animation);
                    Player.m_localPlayer = player;
                    var pending = (IDictionary)AccessTools.Field(zenType, "ZSyncAnimPending").GetValue(null);
                    pending.Clear();
                    int hash = ZSyncAnimation.GetHash("onGround");

                    animation.SetBool(hash, true);
                    Check(!value && writes == 1 && (bool)pending[hash]!, "Zen must delay the true value while an attack is active");
                    AccessTools.Field(zenType, "_isZSyncAnimUpdatePending").SetValue(null, true);
                    try { animation.SetBool(hash, (bool)pending[hash]!); }
                    finally { AccessTools.Field(zenType, "_isZSyncAnimUpdatePending").SetValue(null, false); }
                    Check(value && writes == 2, "VPO must allow Zen's queued true through to the animator");
                }
            });
        }
        finally
        {
            Reset(); fixture.UnpatchSelf(); Player.m_localPlayer = previous;
            CompatibilityInstaller.Error = previousError;
            foreach (IntPtr pointer in pointers) Marshal.FreeHGlobal(pointer);
            pointers.Clear();
        }
    }

    private static bool Unchanged(Patch before, Patch after) => before.PatchMethod == after.PatchMethod
        && before.owner == after.owner && before.priority == after.priority && before.debug == after.debug
        && before.before.SequenceEqual(after.before) && before.after.SequenceEqual(after.after);

    private static T Fake<T>() where T : UnityEngine.Object
    {
        var result = (T)FormatterServices.GetUninitializedObject(typeof(T));
        IntPtr pointer = Marshal.AllocHGlobal(sizeof(long));
        pointers.Add(pointer); Marshal.WriteInt64(pointer, pointers.Count);
        AccessTools.Field(typeof(UnityEngine.Object), "m_CachedPtr").SetValue(result, pointer);
        return result;
    }

    private static void Reject(Action action)
    {
        try { action(); throw new Exception("changed vendor build was accepted"); }
        catch (NotSupportedException) { }
    }

    private static bool Owned(ref bool __result) { __result = true; return false; }
    private static bool AttackedRecently(ref float __result) { __result = 0.1f; return false; }
    private static bool FeatureOn(ref bool __result) { __result = true; return false; }
    private static void Record(bool current) { value = current; writes++; }
    private static IEnumerable<CodeInstruction> ReplaceOriginal(IEnumerable<CodeInstruction> instructions)
    {
        yield return new CodeInstruction(OpCodes.Ldarg_2);
        yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(VpoAnimationTests), nameof(Record)));
        yield return new CodeInstruction(OpCodes.Ret);
    }

    private static HarmonyMethod Hook(string name) => new HarmonyMethod(AccessTools.Method(typeof(VpoAnimationTests), name));
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
