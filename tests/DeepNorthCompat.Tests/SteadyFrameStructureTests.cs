using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using BepInEx.Configuration;
using DeepNorthCompat;
using HarmonyLib;
using UnityEngine;

internal static class SteadyFrameStructureTests
{
    private const string FixtureOwner = "DeepNorthCompat.Tests.SteadyFrameStructures";
    private const string VendorOwner = FixtureOwner + ".VPO";
    private static readonly Transform transform = Fake<Transform>();
    private static AccessTools.FieldRef<ZNetScene, List<ZDO>> AlternatePending = AccessTools.FieldRefAccess<ZNetScene, List<ZDO>>("m_tempCurrentObjects2");
    private static readonly MarkerCallback marker = (_, __, ___, ____) => IntPtr.Zero;
    private static readonly FreeCallback free = _ => { };

    internal static void RunClient(string profile, Action<string, Action> test)
    {
        test("structures optional absence installs no adapter", () =>
        {
            SteadyFrameStructurePatch.Prepare(null, null);
            SteadyFrameStructurePatch.Verify();
            Check(!Harmony.HasAnyPatches(SteadyFrameStructurePatch.Owner), "optional absence installed an adapter");
        });

        string steadyPath = Path.Combine(profile, "BepInEx/plugins/Ivvty-SteadyFrame/SteadyFrame.dll");
        string vpoPath = Path.Combine(profile, "BepInEx/plugins/ontrigger-ValheimPerformanceOptimizations/VPO/ValheimPerformanceOptimizations.dll");
        if (!File.Exists(steadyPath) || !File.Exists(vpoPath)) return;

        Assembly steady = Assembly.LoadFrom(steadyPath), vpo = Assembly.LoadFrom(vpoPath);
        string sandbox = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox");
        AccessTools.Method(typeof(BepInEx.Paths), "SetExecutablePath").Invoke(null,
            new object[] { Path.Combine(sandbox, "valheim.exe"), Path.Combine(sandbox, "BepInEx"), Path.GetDirectoryName(typeof(ZNetScene).Assembly.Location)!, Array.Empty<string>() });

        void Case(string name, Action<Scene> action) => test("structures " + name, () =>
        {
            using (var scene = new Scene(steady, vpo)) action(scene);
        });

        Case("reproduces the queue mismatch and restores the real vendor hold", scene =>
        {
            ZDO support = scene.Add();
            Check(scene.Near.Count == 1 && scene.MayUpdate(31f) && scene.Sectors.Count == 0, "unpatched VPO mismatch did not reproduce");
            System.Console.WriteLine("REPRO: VPO queue=1, native list=0, SF pending sectors=0, ShouldUpdate(31)=true.");
            scene.Native.Add(support);
            scene.Frame();
            Check(!scene.MayUpdate(31f) && scene.Sectors.Count == 1, "same native-list entry did not activate the original hold");
            scene.Native.Clear();

            scene.Install();
            scene.Frame();
            Check(!scene.MayUpdate(31f) && scene.Sectors.Count == 1, "adapter did not preserve the hold after vanilla's 30-second grace");
            Check(!scene.MayUpdate(120f) && scene.MayUpdate(120.01f), "original 120-second boundary changed");
        });

        Case("retains vanilla grace, presnow behavior and newly placed pieces", scene =>
        {
            scene.Install();
            Check(!scene.MayUpdate(30f), "vanilla grace was lost with no pending objects");
            Check(scene.MayUpdate(30.01f), "empty queue extended the vanilla grace");
            AccessTools.Field(typeof(WearNTear), "m_addPreSnow").SetValue(scene.Piece, true);
            Check(scene.MayUpdate(10f), "native presnow exception changed");
            scene.Add();
            scene.Frame();
            Check(!scene.MayUpdate(10f), "presnow piece did not retain the vendor pending-object hold");
            scene.Piece.OnPlaced();
            Check(scene.MayUpdate(10f), "OnPlaced's negative creation time was ignored");
        });

        Case("uses mutable queues and keeps the original once-per-frame cache", scene =>
        {
            scene.Install();
            Check(scene.MayUpdate(31f), "empty queue held the piece");
            ZDO support = scene.Add();
            Check(scene.MayUpdate(31f), "the vendor's same-frame cache unexpectedly refreshed");
            scene.Frame();
            Check(!scene.MayUpdate(31f), "next collection ignored a newly queued support");
            support.Created = true;
            scene.Frame();
            Check(scene.MayUpdate(31f), "created entry extended the hold");
            support.Created = false;
            scene.Frame();
            Check(!scene.MayUpdate(31f), "mutable Created state was cached beyond a frame");
            scene.Remove();
            scene.Frame();
            Check(scene.MayUpdate(31f), "queue drain did not release the hold");
        });

        Case("keeps prefab and adjacent-sector filtering and the enabled gate", scene =>
        {
            scene.Install();
            scene.Add(new Vector3(64f, 0f, -64f));
            Check(!scene.MayUpdate(31f), "adjacent sector was ignored");
            scene.Remove();
            scene.Add(new Vector3(128f, 0f, 0f));
            scene.Frame();
            Check(scene.MayUpdate(31f), "distant sector held this piece");
            scene.Remove();
            scene.Add(prefab: 999);
            scene.Frame();
            Check(scene.MayUpdate(31f), "missing prefab extended the hold");
            scene.Remove();
            scene.Add();
            scene.Enabled.Value = false;
            scene.Frame();
            Check(scene.MayUpdate(31f), "master-off gate was lost");
            scene.Enabled.Value = true;
            Check(!scene.MayUpdate(31f), "master-on did not restore the original hold");
        });

        Case("unpatch restores the native list and repeated preparation registers once", scene =>
        {
            scene.Add();
            scene.Install();
            scene.Install();
            scene.Frame();
            Check(!scene.MayUpdate(31f), "repeated preparation lost the adapter");
            Check(Harmony.GetPatchInfo(scene.Collect)!.Transpilers.Count(p => p.owner == SteadyFrameStructurePatch.Owner) == 1, "duplicate adapter");
            new Harmony(SteadyFrameStructurePatch.Owner).UnpatchSelf();
            scene.Frame();
            Check(scene.MayUpdate(31f), "unpatch did not restore the native-list result");
        });

        Case("inactive VPO and a removed adapter roll back before approval", scene =>
        {
            SteadyFrameStructurePatch.Prepare(steady, vpo);
            SteadyFrameStructurePatch.Verify();
            Check(!Harmony.HasAnyPatches(SteadyFrameStructurePatch.Owner), "inactive VPO retained an adapter");
            scene.Vpo();
            SteadyFrameStructurePatch.Prepare(steady, vpo);
            new Harmony(SteadyFrameStructurePatch.Owner).UnpatchSelf();
            SteadyFrameStructurePatch.Verify();
            Check(!Harmony.HasAnyPatches(SteadyFrameStructurePatch.Owner), "verification left partial hooks");
        });

        Case("rejects unsupported vendor bytes without installing hooks", scene =>
        {
            foreach (bool badSteady in new[] { true, false })
            {
                bool rejected = false;
                try { SteadyFrameStructurePatch.Prepare(badSteady ? typeof(SteadyFrameStructureTests).Assembly : steady,
                    badSteady ? vpo : typeof(SteadyFrameStructureTests).Assembly); }
                catch (NotSupportedException exception) { rejected = true; System.Console.WriteLine("Rejected build: " + exception.Message); }
                Check(rejected && !Harmony.HasAnyPatches(SteadyFrameStructurePatch.Owner), "unsupported vendor was accepted");
            }
        });

        Case("rejects changed IL on a real repatch and retains the incoming collector", scene =>
        {
            scene.Add();
            scene.Install();
            new Harmony(FixtureOwner).Patch(scene.Collect, transpiler: new HarmonyMethod(typeof(SteadyFrameStructureTests), nameof(ChangeSource)) { priority = Priority.First });
            scene.Frame();
            Check(scene.MayUpdate(31f), "failed rewrite leaked its queue changes into native IL");
            Check(scene.Errors.Any(message => message.Contains("NO LONGER APPLIED")), "repatch rejection was silent");
            SteadyFrameStructurePatch.Prepare(steady, vpo);
            SteadyFrameStructurePatch.Verify();
            Check(!Harmony.HasAnyPatches(SteadyFrameStructurePatch.Owner), "changed IL passed group verification");
        });

        Case("rejects a label inside the replaced stack sequence", scene =>
        {
            scene.Add();
            new Harmony(FixtureOwner).Patch(scene.Collect, transpiler: new HarmonyMethod(typeof(SteadyFrameStructureTests), nameof(LabelSource)) { priority = Priority.First });
            scene.Vpo();
            SteadyFrameStructurePatch.Prepare(steady, vpo);
            SteadyFrameStructurePatch.Verify();
            scene.Frame();
            Check(!Harmony.HasAnyPatches(SteadyFrameStructurePatch.Owner) && scene.MayUpdate(31f), "labelled stack sequence was rewritten");
        });
    }

    internal static void RunServer(Action<string, Action> test) => test("structures adapter stays inactive on a dedicated server", () =>
    {
        SteadyFrameStructurePatch.Prepare(typeof(SteadyFrameStructureTests).Assembly, typeof(SteadyFrameStructureTests).Assembly);
        SteadyFrameStructurePatch.Verify();
        Check(!Harmony.HasAnyPatches(SteadyFrameStructurePatch.Owner), "dedicated role installed the client adapter");
    });

    private sealed class Scene : IDisposable
    {
        private readonly Assembly steady, vpo;
        private readonly Type guard, management;
        private readonly Stack<Action> restore = new Stack<Action>();
        private readonly IntPtr pointer = Marshal.AllocHGlobal(8);
        private readonly ZNetScene scene = Fake<ZNetScene>();
        private readonly Func<WearNTear, float, bool> shouldUpdate;
        private readonly IDictionary indices;
        private uint id;
        internal readonly WearNTear Piece = Fake<WearNTear>();
        internal readonly List<ZDO> Native = new List<ZDO>();
        internal readonly List<string> Errors = new List<string>();
        internal readonly IList Near;
        internal readonly HashSet<int> Sectors;
        internal readonly MethodInfo Collect;
        internal readonly ConfigEntry<bool> Enabled;

        internal Scene(Assembly steady, Assembly vpo)
        {
            Check(!Harmony.HasAnyPatches(SteadyFrameStructurePatch.VpoGuid), "VPO fixture requires an unpatched owner");
            this.steady = steady;
            this.vpo = vpo;
            guard = steady.GetType("SteadyFrame.Structures.StructureGuard", true)!;
            management = vpo.GetType("ValheimPerformanceOptimizations.Patches.ObjectManagement.ZNetSceneObjectManagementPatch", true)!;
            // Only Unity handles, profiler creation, transforms and time are fixture boundaries.
            mono_add_internal_call("Unity.Profiling.LowLevel.Unsafe.ProfilerUnsafeUtility::CreateMarker_Injected", Marshal.GetFunctionPointerForDelegate(marker));
            mono_add_internal_call("UnityEngine.Bindings.BindingsAllocator::Free", Marshal.GetFunctionPointerForDelegate(free));
            Near = (IList)AccessTools.Field(management, "QueuedNearObjects").GetValue(null);
            indices = (IDictionary)AccessTools.Field(management, "QueuedNearObjectIndices").GetValue(null);
            Check(Near.Count == 0 && indices.Count == 0, "VPO fixture queue was not empty");
            Sectors = (HashSet<int>)AccessTools.Field(guard, "PendingSectors").GetValue(null);
            Collect = AccessTools.Method(guard, "Collect");
            Save(guard, "_collectedFrame", -1);
            Set(typeof(UnityEngine.Object), "m_CachedPtr", scene, pointer);
            Set(typeof(UnityEngine.Object), "m_CachedPtr", Piece, pointer);
            Set(typeof(UnityEngine.Object), "m_CachedPtr", transform, pointer);
            Save(typeof(ZNetScene), "s_instance", scene);
            Set(typeof(ZNetScene), "m_tempCurrentObjects2", scene, Native);
            GameObject prefab = Fake<GameObject>();
            Set(typeof(UnityEngine.Object), "m_CachedPtr", prefab, pointer);
            Set(typeof(ZNetScene), "m_namedPrefabs", scene, new Dictionary<int, GameObject> { [1234] = prefab });
            Set(typeof(WearNTear), "m_createTime", Piece, 0f);

            Type plugin = steady.GetType("SteadyFrame.SteadyFramePlugin", true)!, settings = steady.GetType("SteadyFrame.Core.SteadySettings", true)!;
            object config = FormatterServices.GetUninitializedObject(settings);
            var file = new ConfigFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox", "structures-never-written.cfg"), false) { SaveOnConfigSet = false };
            Enabled = file.Bind("fixture", "Enabled", true);
            Set(settings, "Enabled", config, Enabled);
            Save(plugin, "Cfg", config);
            Action<string> previousError = CompatibilityInstaller.Error;
            restore.Push(() => CompatibilityInstaller.Error = previousError);
            CompatibilityInstaller.Error = Errors.Add;

            var fixture = new Harmony(FixtureOwner);
            fixture.Patch(AccessTools.PropertyGetter(typeof(Component), "transform"), prefix: new HarmonyMethod(typeof(SteadyFrameStructureTests), nameof(TransformOf)));
            fixture.Patch(AccessTools.PropertyGetter(typeof(Transform), "position"), prefix: new HarmonyMethod(typeof(SteadyFrameStructureTests), nameof(PositionOf)));
            new Harmony(VendorOwner).PatchAll(steady.GetType("SteadyFrame.Structures.Patch_WearNTear_ShouldUpdate_Guard", true)!);
            shouldUpdate = (Func<WearNTear, float, bool>)Delegate.CreateDelegate(typeof(Func<WearNTear, float, bool>), AccessTools.Method(typeof(WearNTear), "ShouldUpdate"));
        }

        internal bool MayUpdate(float time) => shouldUpdate(Piece, time);
        internal void Frame() => AccessTools.Field(guard, "_collectedFrame").SetValue(null, -1);
        internal ZDO Add(Vector3 position = default, int prefab = 1234)
        {
            var support = (ZDO)FormatterServices.GetUninitializedObject(typeof(ZDO));
            support.m_uid = new ZDOID(123L, ++id);
            Set(typeof(ZDO), "m_position", support, position);
            Set(typeof(ZDO), "m_prefab", support, prefab);
            Check((bool)AccessTools.Method(management, "AddQueuedObject").Invoke(null, new object[] { Near, indices, support }), "real VPO queue refused entry");
            return support;
        }

        internal void Remove() => AccessTools.Method(management, "RemoveQueuedNearObjectAt").Invoke(null, new object[] { 0 });
        internal void Vpo() => new Harmony(SteadyFrameStructurePatch.VpoGuid).Patch(AccessTools.Method(typeof(ZNetScene), "CreateDestroyObjects"),
            prefix: new HarmonyMethod(AccessTools.Method(management, "ZNetScene_CreateDestroyObjects_Prefix")));
        internal void Install()
        {
            if (!Harmony.HasAnyPatches(SteadyFrameStructurePatch.VpoGuid)) Vpo();
            SteadyFrameStructurePatch.Prepare(steady, vpo);
            SteadyFrameStructurePatch.Verify();
            Check(Harmony.HasAnyPatches(SteadyFrameStructurePatch.Owner) && Errors.Count == 0, string.Join("\n", Errors));
        }

        private void Save(Type type, string name, object? value)
        {
            FieldInfo field = AccessTools.Field(type, name);
            object? previous = field.GetValue(null);
            restore.Push(() => field.SetValue(null, previous));
            field.SetValue(null, value);
        }

        public void Dispose()
        {
            foreach (string owner in new[] { SteadyFrameStructurePatch.Owner, FixtureOwner, VendorOwner, SteadyFrameStructurePatch.VpoGuid }) new Harmony(owner).UnpatchSelf();
            Near.Clear();
            indices.Clear();
            Sectors.Clear();
            while (restore.Count != 0) restore.Pop()();
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static IEnumerable<CodeInstruction> ChangeSource(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.Select(i => new CodeInstruction(i)).ToList();
        code.Single(i => i.opcode == OpCodes.Ldsfld && i.operand is FieldInfo field && field.Name == "Pending").operand = AccessTools.Field(typeof(SteadyFrameStructureTests), nameof(AlternatePending));
        return code;
    }

    private static IEnumerable<CodeInstruction> LabelSource(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
    {
        var code = instructions.Select(i => new CodeInstruction(i)).ToList();
        code.Single(i => i.opcode == OpCodes.Ldind_Ref).labels.Add(generator.DefineLabel());
        return code;
    }

    private static bool TransformOf(ref Transform __result) { __result = transform; return false; }
    private static bool PositionOf(ref Vector3 __result) { __result = Vector3.zero; return false; }
    private static T Fake<T>() => (T)FormatterServices.GetUninitializedObject(typeof(T));
    private static void Set(Type type, string field, object instance, object value) => AccessTools.Field(type, field).SetValue(instance, value);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr MarkerCallback(IntPtr name, ushort category, ushort flags, int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FreeCallback(IntPtr memory);
    [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl)] private static extern void mono_add_internal_call(string name, IntPtr method);
}
