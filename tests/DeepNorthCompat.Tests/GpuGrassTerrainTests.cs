using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using DeepNorthCompat;
using HarmonyLib;
using UnityEngine;

internal static class GpuGrassTerrainTests
{
    private const string FixtureOwner = "DeepNorthCompat.Tests.GpuGrass";
    private const string GpuSlot = "ValheimMods.Shared.GpuGrass";
    private const string VcpOwner = "MidnightsFX.ValheimCommunityPatch";

    internal static void RunClient(string profile, Action<string, Action> test)
    {
        test("gpu grass optional absence leaves construction and drawing untouched", () =>
        {
            GpuGrassTerrainPatch.Prepare(null, null, null, null);
            GpuGrassTerrainPatch.Verify();
            Check(!Harmony.HasAnyPatches(GpuGrassTerrainPatch.Owner), "absence installed hooks");
        });

        Assembly? steady = Load(profile, "Ivvty-SteadyFrame", "SteadyFrame.dll");
        Assembly? far = Load(profile, "Ivvty-FarLands", "FarLands.dll");
        if (steady == null && far == null) return;

        Assembly hearth = Load(profile, "Azumatt-HearthBelow", "HearthBelow.dll")!;
        Assembly? vcp = Load(profile, "MidnightMods-ValheimCommunityPatch", "ValheimCommunityPatch.dll");
        if (hearth == null) return;

        void Case(string name, Action<Scene> action) => test("gpu grass " + name, () =>
        {
            using (var scene = new Scene(steady, far)) action(scene);
        });

        foreach (Assembly vendor in new[] { steady, far }.Where(a => a != null).Cast<Assembly>())
        {
            Case(vendor.GetName().Name + " keeps collider construction with drawing enabled or disabled", scene =>
            {
                Type gpu = vendor.GetType("ValheimMods.Shared.GpuGrass", true)!;
                Type mapType = gpu.GetNestedType("TerrainCopy", BindingFlags.NonPublic)!;
                object map = Activator.CreateInstance(mapType, true)!;
                Set(mapType, "Width", map, 1);
                Set(mapType, "Scale", map, 1f);
                Set(mapType, "Heights", map, new[] { 10f, 10f, 10f, 10f });
                object[] ground = { map, 0f, 0f, 0f, Vector3.zero };
                Call(gpu, "GroundOf", ground);
                Check((float)ground[3] == 10f, "the real worker should still read its baseline, not the voxel fixture's height 2");
                Check(!scene.NativeConstruction(gpu, true), "unpatched ordinary mode did not reproduce suppression");

                GpuGrassTerrainPatch.Prepare(hearth, vendor == steady ? steady : null, vendor == far ? far : null, null);
                GpuGrassTerrainPatch.Verify();
                foreach (bool verification in new[] { false, true })
                {
                    scene.Verification(verification);
                    foreach (bool drawing in new[] { false, true })
                        Check(scene.NativeConstruction(gpu, drawing), $"native construction suppressed with drawing={drawing}, verification={verification}");
                }
            });
        }

        Case("does not alter construction without HearthBelow or without a helper", scene =>
        {
            GpuGrassTerrainPatch.Prepare(null, steady, far, vcp);
            Check(!Harmony.HasAnyPatches(GpuGrassTerrainPatch.Owner), "HearthBelow absence installed hooks");
            GpuGrassTerrainPatch.Prepare(hearth, null, null, vcp);
            Check(!Harmony.HasAnyPatches(GpuGrassTerrainPatch.Owner), "helper absence installed hooks");
        });

        Case("cached and later foreign hooks cannot restore heightmap-only construction", scene =>
        {
            Type gpu = (steady ?? far)!.GetType("ValheimMods.Shared.GpuGrass", true)!;
            Check(!scene.NativeConstruction(gpu, true), "ordinary-mode control did not build off thread");
            new Harmony(FixtureOwner).Patch(AccessTools.Method(typeof(ClutterSystem), "GenerateVegPatch"),
                prefix: new HarmonyMethod(typeof(GpuGrassTerrainTests), nameof(Observe)));
            Check(!scene.NativeConstruction(gpu, true), "vendor no longer caches the original foreign-hook answer");

            GpuGrassTerrainPatch.Prepare(hearth, steady, far, null);
            GpuGrassTerrainPatch.Verify();
            Check(scene.NativeConstruction(gpu, true), "cached false bypassed collider construction");
            new Harmony(FixtureOwner).Patch(AccessTools.Method(gpu, "ForeignBuild"),
                prefix: new HarmonyMethod(typeof(GpuGrassTerrainTests), nameof(Observe)));
            new Harmony(FixtureOwner).Patch(AccessTools.Method(gpu, "BuildPass"),
                prefix: new HarmonyMethod(typeof(GpuGrassTerrainTests), nameof(Observe)));
            Check(scene.NativeConstruction(gpu, true), "Harmony repatching restored the array-only worker");
            new Harmony(FixtureOwner).UnpatchSelf();
            Check(scene.NativeConstruction(gpu, true), "foreign-hook removal restored the array-only worker");
        });

        Case("elects one copy and preserves renderer activity and the 200 m request", scene =>
        {
            GpuGrassTerrainPatch.Prepare(hearth, steady, far, null);
            GpuGrassTerrainPatch.Verify();
            foreach (bool steadyRuns in steady == null ? new[] { false } : new[] { true, false })
            {
                var offers = new List<object[]>();
                scene.Domain(GpuSlot, offers);
                scene.Domain(GpuSlot + ".elected", null);
                string? installed = null;
                foreach (Assembly vendor in new[] { far, steady }.Where(a => a != null).Cast<Assembly>())
                {
                    Type gpu = vendor.GetType("ValheimMods.Shared.GpuGrass", true)!;
                    bool runs = vendor != steady || steadyRuns;
                    Call(gpu, "Offer", vendor == steady ? SteadyFrameStructurePatch.Guid : FarLandsTerrainPatch.Guid,
                        (Func<bool?>)(() => true), (Func<bool>)(() => runs));
                    // Only the native renderer installation is substituted. Offer, Elect and its tie-break run unchanged.
                    offers[offers.Count - 1][2] = (Action)(() => installed = vendor.GetName().Name);
                }

                Call((steady ?? far)!.GetType("ValheimMods.Shared.GpuGrass", true)!, "Elect");
                string? expected = steadyRuns ? "SteadyFrame" : far == null ? null : "FarLands";
                Check(installed == expected, "wrong elected helper: " + installed);
                if (expected == null) continue;

                Type winner = (expected == "SteadyFrame" ? steady : far)!.GetType("ValheimMods.Shared.GpuGrass", true)!;
                scene.Domain(GpuSlot + ".active", (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), AccessTools.PropertyGetter(winner, "Active")));
                foreach (bool ready in new[] { false, true })
                foreach (bool wanted in new[] { false, true })
                {
                    scene.Field(winner, "_ready", ready);
                    scene.Field(winner, "_wanted", wanted);
                    bool active = (bool)AccessTools.PropertyGetter(winner, "AnyActive").Invoke(null, null);
                    Check(active == (ready && wanted), "construction integration changed renderer readiness or its request");
                    Check(scene.NativeConstruction(winner, active), "elected renderer suppressed native construction");
                    scene.Radius(profile, active);
                }
            }
        });

        Case("honors real master, module, GPU veto and FarLands distance votes", scene =>
        {
            Func<bool?>? steadyOpinion = steady == null ? null : PluginOpinion(steady, "SteadyFrame.SteadyFramePlugin");
            Func<bool?>? farOpinion = far == null ? null : PluginOpinion(far, "FarLands.FarLandsPlugin");
            Type gpu = (steady ?? far)!.GetType("ValheimMods.Shared.GpuGrass", true)!;
            if (far != null)
            {
                Type plugin = far.GetType("FarLands.FarLandsPlugin", true)!;
                var settings = new ConfigFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "farlands-votes-never-written.cfg"), false) { SaveOnConfigSet = false };
                ConfigEntry<bool> enabled = settings.Bind("General", "Enabled", true);
                ConfigEntry<float> distance = settings.Bind("Detail", "GrassDistance", 200f);
                scene.Field(plugin, "Enabled", enabled);
                scene.Field(plugin, "GrassDistance", distance);
                Type radius = far.GetType("FarLands.GrassRadius", true)!;
                scene.Field(radius, "_probed", true);
                scene.Field(radius, "_visualsDistance", settings.Bind("Vegetation", "UseDistance", true));
                enabled.Value = false;
                Check(farOpinion!() == null, "disabled FarLands requested GPU drawing");
                enabled.Value = true;
                distance.Value = 0f;
                Check(farOpinion!() == true, "FarLands ignored Visuals' distance request");
                distance.Value = 200f;
            }

            if (steady != null)
            {
                Type settingsType = steady.GetType("SteadyFrame.Core.SteadySettings", true)!;
                object settings = FormatterServices.GetUninitializedObject(settingsType);
                var file = new ConfigFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "steady-votes-never-written.cfg"), false) { SaveOnConfigSet = false };
                ConfigEntry<bool> master = file.Bind("1 - General", "Enabled", true);
                ConfigEntry<bool> module = file.Bind("2 - Modules", "Grass", true);
                ConfigEntry<bool> drawing = file.Bind("6 - Grass", "Draw on the GPU", true);
                Set(settingsType, "Enabled", settings, master);
                Set(settingsType, "GrassModule", settings, module);
                Set(settingsType, "GpuGrass", settings, drawing);
                Type plugin = steady.GetType("SteadyFrame.SteadyFramePlugin", true)!;
                scene.Field(plugin, "Cfg", settings);
                scene.Field(gpu, "_opinions", farOpinion == null ? new[] { steadyOpinion! } : new[] { steadyOpinion!, farOpinion });
                foreach (bool masterOn in new[] { false, true })
                foreach (bool moduleOn in new[] { false, true })
                foreach (bool drawOn in new[] { false, true })
                {
                    master.Value = masterOn;
                    module.Value = moduleOn;
                    drawing.Value = drawOn;
                    scene.Field(plugin, "_inactive", null);
                    bool? expected = masterOn && moduleOn ? drawOn : (bool?)null;
                    Check(steadyOpinion!() == expected, "SteadyFrame's real vote ignored a disabled state");
                    Check((bool)Call(gpu, "Opinion")! == (expected != false && (expected == true || farOpinion != null)),
                        "the elected helper ignored a veto or an abstention");
                }
            }
        });

        if (vcp != null)
        {
            Case("retains and exercises VCP instance growth when the game constructs grass", scene =>
            {
                Type memory = vcp.GetType("ValheimCommunityPatch.Patches.Performance.GrassInstanceMemoryPatch", true)!;
                MethodInfo grow = AccessTools.Method(memory.GetNestedType("GrowthHooks", BindingFlags.NonPublic), "AddInstancePrefix");
                MethodInfo add = AccessTools.Method(typeof(InstanceRenderer), "AddInstance", new[] { typeof(Matrix4x4) });
                Type startup = memory.GetNestedType("GpuGrassHook", BindingFlags.NonPublic)!;
                scene.Plugin(SteadyFrameStructurePatch.Guid);
                scene.Field(vcp.GetType("ValheimCommunityPatch.ValheimCommunityPatch", true)!, "Log", BepInEx.Logging.Logger.CreateLogSource(FixtureOwner));
                scene.Field(memory, "_gpuChecked", false);
                scene.Field(memory, "_gpuStoodDown", false);
                new Harmony(VcpOwner).Patch(add, prefix: new HarmonyMethod(grow));
                Call(startup, "Prefix");
                Check(!Has(add, grow, VcpOwner), "VCP control did not reproduce its stale-provider removal");

                foreach (bool active in new[] { false, true })
                {
                    scene.Domain(GpuSlot + ".active", (Func<bool>)(() => active));
                    scene.Field(memory, "_gpuChecked", false);
                    scene.Field(memory, "_gpuStoodDown", false);
                    new Harmony(VcpOwner).Patch(add, prefix: new HarmonyMethod(grow));
                    GpuGrassTerrainPatch.Prepare(hearth, steady, far, vcp);
                    GpuGrassTerrainPatch.Verify();
                    Call(startup, "Prefix");
                    Check(Has(add, grow, VcpOwner), "VCP removed the memory hook used by collider-based construction");
                    var renderer = (InstanceRenderer)FormatterServices.GetUninitializedObject(typeof(InstanceRenderer));
                    FieldInfo matrices = AccessTools.Field(typeof(InstanceRenderer), "m_instances");
                    FieldInfo count = AccessTools.Field(typeof(InstanceRenderer), "m_instanceCount");
                    matrices.SetValue(renderer, new Matrix4x4[16]);
                    count.SetValue(renderer, 16);
                    add.Invoke(renderer, new object[] { Matrix4x4.identity });
                    Check(((Matrix4x4[])matrices.GetValue(renderer)).Length == 32 && (int)count.GetValue(renderer) == 17,
                        "retained VCP hook did not grow the real renderer array");
                    new Harmony(VcpOwner).Unpatch(add, grow);
                }
            });
        }

        Case("rejects unknown providers before patching and rolls back an incomplete group", scene =>
        {
            try
            {
                GpuGrassTerrainPatch.Prepare(hearth, typeof(GpuGrassTerrainTests).Assembly, null, null);
                throw new Exception("unknown vendor bytes accepted");
            }
            catch (NotSupportedException) { Check(!Harmony.HasAnyPatches(GpuGrassTerrainPatch.Owner), "unknown build left hooks"); }

            GpuGrassTerrainPatch.Prepare(hearth, steady, far, vcp);
            Type gpu = (steady ?? far)!.GetType("ValheimMods.Shared.GpuGrass", true)!;
            new Harmony(GpuGrassTerrainPatch.Owner).Unpatch(AccessTools.Method(gpu, "ForeignBuild"), HarmonyPatchType.Prefix, GpuGrassTerrainPatch.Owner);
            var errors = new List<string>();
            Action<string> previous = CompatibilityInstaller.Error;
            CompatibilityInstaller.Error = errors.Add;
            try { GpuGrassTerrainPatch.Verify(); }
            finally { CompatibilityInstaller.Error = previous; }
            Check(!Harmony.HasAnyPatches(GpuGrassTerrainPatch.Owner) && errors.Any(m => m.Contains("NOT APPLIED")), "incomplete group stayed partially active");
        });
    }

    internal static void RunServer(Action<string, Action> test) => test("gpu grass is inactive on a dedicated build", () =>
    {
        Assembly invalid = typeof(GpuGrassTerrainTests).Assembly;
        GpuGrassTerrainPatch.Prepare(invalid, invalid, invalid, invalid);
        GpuGrassTerrainPatch.Verify();
        Check(!Harmony.HasAnyPatches(GpuGrassTerrainPatch.Owner), "dedicated server installed graphics hooks");
    });

    private static Assembly? Load(string profile, string package, string name)
    {
        string path = Path.Combine(profile, "BepInEx", "plugins", package, name);
        return File.Exists(path) ? Assembly.LoadFrom(path) : null;
    }

    private static object? Call(Type type, string name, params object[] arguments) => AccessTools.Method(type, name).Invoke(null, arguments);

    private static Func<bool?> PluginOpinion(Assembly assembly, string name)
    {
        MethodInfo method = assembly.GetType(name, true)!.GetNestedTypes(BindingFlags.NonPublic)
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .Single(m => m.Name.Contains("<Awake>") && m.ReturnType == typeof(bool?) && m.GetParameters().Length == 0);
        return (Func<bool?>)Delegate.CreateDelegate(typeof(Func<bool?>), method.IsStatic ? null : Activator.CreateInstance(method.DeclaringType!, true), method);
    }

    private static void Set(Type type, string name, object? instance, object? value) => AccessTools.Field(type, name).SetValue(instance, value);
    private static bool Has(MethodBase target, MethodInfo hook, string owner) => Harmony.GetPatchInfo(target)?.Prefixes.Any(p => p.owner == owner && p.PatchMethod == hook) == true;
    private static void Observe() { }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed class Scene : IDisposable
    {
        private readonly Stack<Action> restore = new Stack<Action>();
        private readonly IntPtr pointer = Marshal.AllocHGlobal(8);
        private readonly ClutterSystem clutter = (ClutterSystem)FormatterServices.GetUninitializedObject(typeof(ClutterSystem));
        private readonly ConfigFile config;

        internal Scene(Assembly? steady, Assembly? far)
        {
            new Harmony(GpuGrassTerrainPatch.Owner).UnpatchSelf();
            string sandbox = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox");
            AccessTools.Method(typeof(Paths), "SetExecutablePath").Invoke(null,
                new object[] { Path.Combine(sandbox, "valheim.exe"), Path.Combine(sandbox, "BepInEx"), Path.GetDirectoryName(typeof(ClutterSystem).Assembly.Location)!, Array.Empty<string>() });
            config = new ConfigFile(Path.Combine(sandbox, "grass-never-written.cfg"), false) { SaveOnConfigSet = false };
            // This host has no Unity scene or GPU. Its object handle and compute capability are fixtures.
            // The vendor's construction decision, election and renderer predicates run unchanged.
            mono_add_internal_call("UnityEngine.SystemInfo::SupportsComputeShaders", Marshal.GetFunctionPointerForDelegate(compute));
            Set(typeof(UnityEngine.Object), "m_CachedPtr", clutter, pointer);
            Field(typeof(ClutterSystem), "m_instance", clutter);
            Field(typeof(ClutterSystem), "m_patches", Activator.CreateInstance(AccessTools.Field(typeof(ClutterSystem), "m_patches").FieldType), clutter);
            clutter.m_clutter = new List<ClutterSystem.Clutter> { new ClutterSystem.Clutter { m_enabled = true } };

            foreach (Assembly vendor in new[] { steady, far }.Where(a => a != null).Cast<Assembly>())
            {
                Type gpu = vendor.GetType("ValheimMods.Shared.GpuGrass", true)!;
                Field(gpu, "_log", BepInEx.Logging.Logger.CreateLogSource(FixtureOwner));
                Field(gpu, "_buildFor", clutter);
                Field(gpu, "_foreignChecked", null);
                Field(gpu, "_foreign", false);
                Field(gpu, "_buildOn", false);
                Field(gpu, "_buildLast", false);
                Field(gpu, "_holding", false);
                Field(gpu, "_buildBroken", false);
                Field(gpu, "BuildPaused", false);
                Field(gpu, "_buildKindsFrame", Time.frameCount);
                Type kind = gpu.GetNestedType("BuildKind", BindingFlags.NonPublic)!;
                Array kinds = Array.CreateInstance(kind, 1);
                kinds.SetValue(Activator.CreateInstance(kind, true), 0);
                Field(gpu, "_buildKinds", kinds);
            }

            if (steady != null)
            {
                Type settings = steady.GetType("SteadyFrame.Core.SteadySettings", true)!;
                object values = FormatterServices.GetUninitializedObject(settings);
                Type verification = steady.GetType("SteadyFrame.Core.Verification", true)!;
                IList all = (IList)AccessTools.Field(verification, "All").GetValue(null);
                int count = all.Count;
                object check = Activator.CreateInstance(verification, BindingFlags.Instance | BindingFlags.NonPublic, null,
                    new object[] { config, "GPU grass", "fixture" }, null)!;
                restore.Push(() => { while (all.Count > count) all.RemoveAt(all.Count - 1); });
                Set(settings, "VerifyGpuGrass", values, check);
                Field(steady.GetType("SteadyFrame.SteadyFramePlugin", true)!, "Cfg", values);
            }
        }

        internal void Verification(bool enabled) => config.Bind("9 - Verification", "GPU grass", false).Value = enabled;

        internal bool NativeConstruction(Type gpu, bool drawing)
        {
            Call(gpu, "BuildPass", drawing);
            Type build = gpu.GetNestedType("Patch_Build", BindingFlags.NonPublic)!;
            try
            {
                Call(build, "Prefix", clutter);
                return clutter.m_clutter[0].m_enabled;
            }
            finally { Call(build, "Finalizer", clutter); }
        }

        internal void Field(Type type, string name, object? value, object? instance = null)
        {
            FieldInfo field = AccessTools.Field(type, name);
            object? original = field.GetValue(instance);
            restore.Push(() => field.SetValue(instance, original));
            field.SetValue(instance, value);
        }

        internal void Domain(string name, object? value)
        {
            object? original = AppDomain.CurrentDomain.GetData(name);
            restore.Push(() => AppDomain.CurrentDomain.SetData(name, original));
            AppDomain.CurrentDomain.SetData(name, value);
        }

        internal void Plugin(string guid)
        {
            bool had = Chainloader.PluginInfos.TryGetValue(guid, out PluginInfo original);
            restore.Push(() => { if (had) Chainloader.PluginInfos[guid] = original; else Chainloader.PluginInfos.Remove(guid); });
            Chainloader.PluginInfos[guid] = (PluginInfo)FormatterServices.GetUninitializedObject(typeof(PluginInfo));
        }

        internal void Radius(string profile, bool active)
        {
            Assembly? visuals = Load(profile, "Ivvty-ValheimVisuals", "ValheimVisuals.dll");
            if (visuals == null) return;

            Type bridge = visuals.GetType("ValheimVisuals.SteadyFrameBridge", true)!;
            Type preset = visuals.GetType("ValheimVisuals.PresetValues", true)!;
            Field(visuals.GetType("ValheimVisuals.VisualsPlugin", true)!, "Log", BepInEx.Logging.Logger.CreateLogSource(FixtureOwner));
            Field(bridge, "_logged", true);
            Field(bridge, "_last", active);
            object request = Activator.CreateInstance(preset)!;
            Set(preset, "UseClutterDistance", request, true);
            Set(preset, "ClutterDistance", request, 200f);
            object result = Call(bridge, "Yield", request)!;
            Check((bool)AccessTools.Field(preset, "UseClutterDistance").GetValue(result) == active
                && (float)AccessTools.Field(preset, "ClutterDistance").GetValue(result) == 200f, "Visuals lost its 200 m request or its actual-GPU gate");
        }

        public void Dispose()
        {
            new Harmony(GpuGrassTerrainPatch.Owner).UnpatchSelf();
            new Harmony(FixtureOwner).UnpatchSelf();
            while (restore.Count > 0) restore.Pop()();
            Marshal.FreeHGlobal(pointer);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Compute();
    private static readonly Compute compute = () => 1;
    [DllImport("mono-2.0-bdwgc.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void mono_add_internal_call(string name, IntPtr method);
}
