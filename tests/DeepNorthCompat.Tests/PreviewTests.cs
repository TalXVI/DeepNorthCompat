using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.InteropServices;
using DeepNorthCompat;
using HarmonyLib;
using UnityEngine;

// Exercise removal and startup guards against the installed methods and real Harmony
// metadata. Native animator access is stubbed for the focused stance regression;
// no equipment action or camera method runs in this offline fixture.
internal static class PreviewTests
{
    private const string Owner = "Azumatt.AzuExtendedPlayerInventory";
    private const string Foreign = "DeepNorthCompat.Tests.Preview.Foreign";
    private const string Failure = "DeepNorthCompat.Tests.Preview.Failure";
    private const string Ours = "DeepNorthCompat.Preview.AzuEPI";
    private static Assembly vendor = null!;
    private static MethodInfo equip = null!, unequip = null!, visuals = null!, animation = null!, refresh = null!;
    private static MethodInfo equipHook = null!, unequipHook = null!, visualHook = null!;
    private static MethodInfo animationHook = null!;
    private static FieldInfo previewField = null!, animatorField = null!;
    private static Animator sourceAnimator = null!, previewAnimator = null!;
    private static int sourceState, previewState, writes;
    private static float sourceBlend, previewBlend;
    private static bool rejectRemoval;
    private static readonly List<string> errors = new List<string>();
    private static readonly List<string> messages = new List<string>();
    private static readonly List<IntPtr> nativeIds = new List<IntPtr>();

    internal static void Run(string lab, Action<string, Action> test)
    {
        vendor = Assembly.LoadFrom(Path.Combine(lab, "BepInEx", "plugins", "Azumatt-AzuExtendedPlayerInventory", "AzuExtendedPlayerInventory.dll"));
        equip = AccessTools.Method(typeof(Humanoid), "EquipItem", new[] { typeof(ItemDrop.ItemData), typeof(bool) });
        unequip = AccessTools.Method(typeof(Humanoid), "UnequipItem", new[] { typeof(ItemDrop.ItemData), typeof(bool) });
        visuals = AccessTools.Method(typeof(VisEquipment), "UpdateEquipmentVisuals");
        equipHook = VendorHook("EquipItem"); unequipHook = VendorHook("UnequipItem");
        visualHook = VendorHook("VisEquipmentPatch");
        animation = AccessTools.Method(typeof(Humanoid), "SetupAnimationState");
        animationHook = AccessTools.Method(vendor.GetType("AzuEPI.Game.PlayerPreview.UpdatePlayerPreviewVisuals", true), "Postfix");
        refresh = AccessTools.Method(vendor.GetType("AzuEPI.Game.PlayerPreview.PlayerPreviewManager", true), "UpdatePlayerPreview");
        previewField = AccessTools.Field(vendor.GetType("AzuEPI.Game.PlayerPreview.AzuEPICharacterPanel", true), "playerPreviewComp");
        animatorField = AccessTools.Field(typeof(Character), "m_animator");

        void Case(string name, Action action) => test("preview " + name, () =>
        {
            Cleanup(); errors.Clear(); messages.Clear();
            try { action(); } finally { Cleanup(); }
        });

        Case("removes only two exact vendor postfixes, retains visual refresh and other owners", () =>
        {
            Register();
            var owner = new Harmony(Owner);
            owner.Patch(equip, postfix: Hook(nameof(OtherPostfix)));
            var foreign = new Harmony(Foreign);
            foreign.Patch(equip, prefix: Hook(nameof(OtherPrefix)), postfix: Hook(nameof(OtherPostfix)), finalizer: Hook(nameof(OtherFinalizer)));
            foreign.Patch(equip, ilmanipulator: Hook(nameof(OtherManipulator)));
            foreign.Patch(unequip, postfix: Hook(nameof(OtherPostfix)));
            string[] retained = Snapshot(includeIndex: true).Where(s => !s.Contains(Token(equipHook)) && !s.Contains(Token(unequipHook))).ToArray();
            Request(vendor); CompatibilityInstaller.Verify();
            Check(errors.Count == 0, string.Join("\n", errors));
            Check(!Present(equip, equipHook) && !Present(unequip, unequipHook), "broad hooks removed");
            Check(retained.SequenceEqual(Snapshot(includeIndex: true).Where(s => !s.Contains(":" + Ours + ":"))), "all retained hooks and their ordering indices unchanged");
            MethodBase[] ours = Harmony.GetAllPatchedMethods().Where(m => Harmony.GetPatchInfo(m)!.Owners.Contains(Ours)).ToArray();
            Check(ours.Length == 1 && ours[0] == refresh && Harmony.GetPatchInfo(refresh)!.Postfixes.Single().owner == Ours,
                "only the local preview update receives the stance postfix");
            Check(!typeof(Plugin).Assembly.GetReferencedAssemblies().Any(a => a.Name == vendor.GetName().Name), "no hard AzuEPI assembly dependency");
            Check(messages.Count(m => m.StartsWith("Preview.AzuEPI: APPLIED")) == 1, "applied log");
            string[] applied = Snapshot(); CompatibilityInstaller.Verify();
            Check(applied.SequenceEqual(Snapshot()) && messages.Count(m => m.StartsWith("Preview.AzuEPI: APPLIED")) == 1, "verification idempotent");
        });

        Case("waits until Start verification for deferred vendor registrations", () =>
        {
            Request(vendor);
            Register();
            Check(Present(equip, equipHook) && Present(unequip, unequipHook), "prepare leaves vendor behavior intact");
            CompatibilityInstaller.Verify();
            Check(errors.Count == 0 && !Present(equip, equipHook) && !Present(unequip, unequipHook), "deferred registrations removed");
        });

        Case("absent optional mod retains all existing hooks", () =>
        {
            Register(); string[] before = Snapshot();
            Request(null); CompatibilityInstaller.Verify();
            Check(errors.Count == 0 && before.SequenceEqual(Snapshot()), "absent optional mod safe");
        });

        Case("unsupported assembly rejected without mutation", () =>
        {
            Register(); string[] before = Snapshot();
            Request(typeof(PreviewTests).Assembly); CompatibilityInstaller.Verify();
            Check(errors.Count == 1 && errors[0].StartsWith("Preview.AzuEPI: NOT APPLIED"), "unsupported build reported");
            Check(before.SequenceEqual(Snapshot()), "hash rejection leaves hooks intact");
        });

        Case("missing second postfix cannot partially remove the first", () =>
        {
            Register(includeUnequip: false); RejectionLeavesHooks();
        });

        Case("missing retained visual-refresh path leaves broad hooks intact", () =>
        {
            Register(includeVisuals: false); RejectionLeavesHooks();
        });

        Case("missing retained local animation-update path leaves broad hooks intact", () =>
        {
            Register(includeAnimation: false); RejectionLeavesHooks();
        });

        Case("wrong Harmony owner leaves both hooks intact", () =>
        {
            Register(unequipOwner: Foreign); RejectionLeavesHooks();
        });

        Case("expected method registered as a prefix is rejected", () =>
        {
            Register(includeUnequip: false); new Harmony(Owner).Patch(unequip, prefix: new HarmonyMethod(unequipHook));
            RejectionLeavesHooks();
        });

        Case("duplicate expected method as an IL manipulator is rejected", () =>
        {
            Register();
            MethodInfo refresh = AccessTools.Method(vendor.GetType("AzuEPI.Game.PlayerPreview.PlayerPreviewManager", true), "TryRefresh");
            new Harmony(Foreign).Patch(refresh, prefix: Hook(nameof(SkipRefresh)));
            new Harmony(Foreign).Patch(unequip, ilmanipulator: new HarmonyMethod(unequipHook));
            RejectionLeavesHooks();
        });

        Case("failure during second removal restores the first vendor hook", () =>
        {
            Register(); string[] before = Snapshot();
            MethodInfo unpatch = AccessTools.Method(typeof(Harmony), "Unpatch", new[] { typeof(MethodBase), typeof(MethodInfo) });
            new Harmony(Failure).Patch(unpatch, prefix: Hook(nameof(RejectSecondRemoval)));
            rejectRemoval = true;
            Request(vendor); CompatibilityInstaller.Verify();
            Check(errors.Count == 1 && errors[0].Contains("vendor hooks retained"), "rollback reported");
            Check(before.SequenceEqual(Snapshot()), "original owner/method/priority/order constraints restored");
        });

        Case("failure after second removal restores both hooks and removes stance postfix", () =>
        {
            Register(); string[] before = Snapshot();
            MethodInfo unpatch = AccessTools.Method(typeof(Harmony), "Unpatch", new[] { typeof(MethodBase), typeof(MethodInfo) });
            new Harmony(Failure).Patch(unpatch, finalizer: Hook(nameof(RejectAfterSecondRemoval)));
            rejectRemoval = true;
            Request(vendor); CompatibilityInstaller.Verify();
            Check(errors.Count == 1 && errors[0].Contains("vendor hooks retained"), "rollback reported");
            Check(before.SequenceEqual(Snapshot()), "both original hooks restored and replacement removed");
        });

        Case("local weapon stance follows the source without repeating unchanged writes", () =>
        {
            Register(); Request(vendor); CompatibilityInstaller.Verify();
            MethodInfo sync = Harmony.GetPatchInfo(refresh)!.Postfixes.Single(p => p.owner == Ours).PatchMethod;
            Player local = Fake<Player>(1), clone = Fake<Player>(2);
            sourceAnimator = Fake<Animator>(3); previewAnimator = Fake<Animator>(4);
            animatorField.SetValue(local, sourceAnimator); animatorField.SetValue(clone, previewAnimator);
            Player.m_localPlayer = local; previewField.SetValue(null, clone);
            StubAnimator(); sourceState = 3; sourceBlend = 3; previewState = 0; previewBlend = 0; writes = 0;
            sync.Invoke(null, new object[] { local });
            Check(previewState == 3 && previewBlend == 3 && writes == 2, "bow stance copied");
            sync.Invoke(null, new object[] { local });
            Check(writes == 2, "unchanged stance does not write again");
            sourceState = 4; sourceBlend = 4;
            sync.Invoke(null, new object[] { local });
            Check(previewState == 4 && previewBlend == 4 && writes == 4, "sword/shield stance copied");
            sourceState = 0; sourceBlend = 0;
            sync.Invoke(null, new object[] { Fake<Player>(5) });
            Check(writes == 4, "unrelated humanoid ignored");
            previewField.SetValue(null, null);
            sync.Invoke(null, new object[] { local });
            Check(writes == 4, "missing preview ignored");
            previewField.SetValue(null, clone); animatorField.SetValue(clone, null);
            sync.Invoke(null, new object[] { local });
            Check(writes == 4, "missing preview animator ignored");
            animatorField.SetValue(clone, previewAnimator); Player.m_localPlayer = null;
            sync.Invoke(null, new object[] { local });
            Check(writes == 4, "absent local player ignored");
            Player.m_localPlayer = local; animatorField.SetValue(local, null);
            sync.Invoke(null, new object[] { local });
            Check(writes == 4, "missing source animator ignored");
            animatorField.SetValue(local, sourceAnimator);
            AccessTools.Field(typeof(UnityEngine.Object), "m_CachedPtr").SetValue(clone, IntPtr.Zero);
            sync.Invoke(null, new object[] { local });
            Check(writes == 4, "destroyed preview ignored");
        });
    }

    private static MethodInfo VendorHook(string suffix) => AccessTools.Method(vendor.GetType("AzuEPI.Game.PlayerPreview.PreviewInstantRefresh_" + suffix, true), "Postfix");
    private static HarmonyMethod Hook(string name) => new HarmonyMethod(AccessTools.Method(typeof(PreviewTests), name));
    private static void Register(bool includeUnequip = true, bool includeVisuals = true, string unequipOwner = Owner, bool includeAnimation = true)
    {
        new Harmony(Owner).Patch(equip, postfix: new HarmonyMethod(equipHook)
        {
            priority = Priority.High,
            before = new[] { "DeepNorthCompat.Tests.Preview.Before" },
            after = new[] { "DeepNorthCompat.Tests.Preview.After" }
        });
        if (includeUnequip) new Harmony(unequipOwner).Patch(unequip, postfix: new HarmonyMethod(unequipHook));
        if (includeVisuals) new Harmony(Owner).Patch(visuals, postfix: new HarmonyMethod(visualHook));
        if (includeAnimation) new Harmony(Owner).Patch(animation, postfix: new HarmonyMethod(animationHook));
    }
    private static void Request(Assembly? assembly) => CompatibilityInstaller.Install(guid => guid == Owner ? assembly : null,
        messages.Add, messages.Add, errors.Add);
    private static void RejectionLeavesHooks()
    {
        string[] before = Snapshot(); Request(vendor); CompatibilityInstaller.Verify();
        Check(errors.Count == 1 && errors[0].StartsWith("Preview.AzuEPI: NOT APPLIED"), "guard rejected");
        Check(before.SequenceEqual(Snapshot()), "preflight leaves every hook intact");
    }
    private static bool Present(MethodInfo target, MethodInfo method) => Harmony.GetPatchInfo(target)?.Postfixes.Any(p => p.PatchMethod == method) == true;
    private static string Token(MethodInfo method) => method.DeclaringType!.FullName + "." + method.Name;
    private static string[] Snapshot(bool includeIndex = false)
    {
        var result = new List<string>();
        foreach (MethodInfo target in new[] { equip, unequip, visuals, animation, refresh })
        {
            Patches? info = Harmony.GetPatchInfo(target); if (info == null) continue;
            void Add(string kind, IEnumerable<Patch> patches)
            {
                result.AddRange(patches.Select(p => target.Name + ":" + kind + ":" + p.owner + ":" + Token(p.PatchMethod)
                    + ":" + p.priority + ":" + string.Join(",", p.before) + ":" + string.Join(",", p.after)
                    + ":" + p.debug + ":" + p.debugEmitPath + (includeIndex ? ":" + p.index : "")));
            }
            Add("prefix", info.Prefixes); Add("postfix", info.Postfixes); Add("transpiler", info.Transpilers); Add("finalizer", info.Finalizers);
            Add("ilmanipulator", info.ILManipulators);
        }
        return result.OrderBy(s => s, StringComparer.Ordinal).ToArray();
    }
    private static void Cleanup()
    {
        rejectRemoval = false;
        new Harmony(Failure).UnpatchSelf(); new Harmony(Foreign).UnpatchSelf(); new Harmony(Owner).UnpatchSelf();
        new Harmony(Ours).UnpatchSelf();
        Player.m_localPlayer = null;
        if (previewField != null) previewField.SetValue(null, null);
        foreach (IntPtr pointer in nativeIds) Marshal.FreeHGlobal(pointer);
        nativeIds.Clear();
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void OtherPrefix() { }
    private static void OtherPostfix() { }
    private static void OtherManipulator() { }
    private static bool SkipRefresh() => false;
    private static T Fake<T>(int id) where T : UnityEngine.Object
    {
        var value = (T)FormatterServices.GetUninitializedObject(typeof(T));
        // Unity 6 reads the ID through m_CachedPtr. The offline host supplies offset zero;
        // this buffer is only an identity fixture, never a native Unity component.
        IntPtr pointer = Marshal.AllocHGlobal(sizeof(long)); nativeIds.Add(pointer);
        Marshal.WriteInt32(pointer, id);
        AccessTools.Field(typeof(UnityEngine.Object), "m_CachedPtr").SetValue(value, pointer);
        return value;
    }
    private static void StubAnimator()
    {
        var fixture = new Harmony(Foreign);
        fixture.Patch(AccessTools.Method(typeof(Animator), "GetInteger", new[] { typeof(string) }), prefix: Hook(nameof(ReadState)));
        fixture.Patch(AccessTools.Method(typeof(Animator), "GetFloat", new[] { typeof(string) }), prefix: Hook(nameof(ReadBlend)));
        fixture.Patch(AccessTools.Method(typeof(Animator), "SetInteger", new[] { typeof(string), typeof(int) }), prefix: Hook(nameof(WriteState)));
        fixture.Patch(AccessTools.Method(typeof(Animator), "SetFloat", new[] { typeof(string), typeof(float) }), prefix: Hook(nameof(WriteBlend)));
    }
    private static bool ReadState(Animator __instance, string __0, ref int __result)
    {
        Check(__0 == "statei", "only weapon stance integer read");
        __result = ReferenceEquals(__instance, sourceAnimator) ? sourceState : previewState; return false;
    }
    private static bool ReadBlend(Animator __instance, string __0, ref float __result)
    {
        Check(__0 == "statef", "only weapon stance blend read");
        __result = ReferenceEquals(__instance, sourceAnimator) ? sourceBlend : previewBlend; return false;
    }
    private static bool WriteState(Animator __instance, string __0, int __1)
    {
        Check(ReferenceEquals(__instance, previewAnimator) && __0 == "statei", "only preview stance integer written");
        previewState = __1; writes++; return false;
    }
    private static bool WriteBlend(Animator __instance, string __0, float __1)
    {
        Check(ReferenceEquals(__instance, previewAnimator) && __0 == "statef", "only preview stance blend written");
        previewBlend = __1; writes++; return false;
    }
    private static Exception? OtherFinalizer(Exception? __exception) => __exception;
    private static void RejectSecondRemoval(Harmony __instance, MethodBase __0)
    {
        if (rejectRemoval && __instance.Id == Ours && __0 == unequip)
            throw new InvalidOperationException("Injected second-removal failure");
    }
    private static Exception? RejectAfterSecondRemoval(Harmony __instance, MethodBase __0, Exception? __exception)
    {
        return rejectRemoval && __instance.Id == Ours && __0 == unequip
            ? new InvalidOperationException("Injected failure after second removal") : __exception;
    }
}
