using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using DeepNorthCompat;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

// The vendor methods run unchanged. Unity material storage and shader names use managed fakes; no GPU scene is created.
internal static class VpoMaterialsTests
{
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly FieldInfo Pointer = AccessTools.Field(typeof(Object), "m_CachedPtr");
    private static readonly List<IntPtr> Pointers = new List<IntPtr>();
    private static readonly Dictionary<Object, string> Names = new Dictionary<Object, string>();
    private static readonly Dictionary<Material, Shader> Shaders = new Dictionary<Material, Shader>();
    private static readonly Dictionary<Material, float> Bumps = new Dictionary<Material, float>();
    private static readonly Dictionary<Material, Texture> Maps = new Dictionary<Material, Texture>();
    private static readonly List<Material> Loaded = new List<Material>();
    private static int identity;

    internal static void Run(string profile, Action<string, Action> test)
    {
        test("VPO materials stay inactive when an optional mod is absent", () =>
        {
            VpoMaterialsPatch.Prepare(null, null);
            VpoMaterialsPatch.Verify();
            Check(!Harmony.HasAnyPatches("DeepNorthCompat.VPO.Materials"), "a missing mod installed a hook");
        });
        string plugins = Path.Combine(profile, "BepInEx", "plugins");
        string[] vpoFiles = Directory.GetFiles(plugins, "ValheimPerformanceOptimizations.dll", SearchOption.AllDirectories);
        string[] visualsFiles = Directory.GetFiles(plugins, "ValheimVisuals.dll", SearchOption.AllDirectories);
        if (vpoFiles.Length == 0 || visualsFiles.Length == 0) return;
        string vpoPath = vpoFiles.Single(), visualsPath = visualsFiles.Single();
        if (Guard.Sha256(vpoPath) == ExpectedBuilds.Vpo123)
        {
            test("VPO materials stay inactive for VPO 1.2.3", () =>
            {
                VpoMaterialsPatch.Prepare(Assembly.LoadFrom(vpoPath), Assembly.LoadFrom(visualsPath));
                VpoMaterialsPatch.Verify();
                Check(!Harmony.HasAnyPatches("DeepNorthCompat.VPO.Materials"), "VPO 1.2.3 installed a material hook");
            });
            return;
        }
        Assembly vpo = Assembly.LoadFrom(vpoPath);
        Assembly visuals = Assembly.LoadFrom(visualsPath);
        test("VPO materials reject missing or changed vendors before installing", () =>
        {
            VpoMaterialsPatch.Prepare(null, visuals);
            VpoMaterialsPatch.Prepare(vpo, null);
            try { VpoMaterialsPatch.Prepare(typeof(VpoMaterialsTests).Assembly, visuals); throw new Exception("unknown VPO build accepted"); }
            catch (NotSupportedException) { }
            try { VpoMaterialsPatch.Prepare(vpo, typeof(VpoMaterialsTests).Assembly); throw new Exception("unknown Visuals build accepted"); }
            catch (NotSupportedException) { }
            Check(!Harmony.HasAnyPatches("DeepNorthCompat.VPO.Materials"), "an absent or unknown mod installed a hook");
        });
        Type replacer = vpo.GetType("ValheimPerformanceOptimizations.Patches.IndirectRendering.PieceMaterialReplacer", true)!;
        Type bundle = vpo.GetType("ValheimPerformanceOptimizations.Patches.IndirectRendering.PieceShaderBundle", true)!;
        Type materials = visuals.GetType("ValheimVisuals.Materials", true)!;
        Type loaded = visuals.GetType("ValheimVisuals.LoadedMaterials", true)!;
        Type preset = visuals.GetType("ValheimVisuals.PresetValues", true)!;
        MethodInfo create = AccessTools.DeclaredMethod(replacer, "TryCreate");
        MethodInfo sweep = AccessTools.DeclaredMethod(materials, "Sweep");
        MethodInfo apply = AccessTools.DeclaredMethod(materials, "Apply");
        MethodInfo restore = AccessTools.DeclaredMethod(materials, "Restore");
        FieldInfo vegetationShader = AccessTools.Field(bundle, "<VegetationShader>k__BackingField");
        object? previousShader = vegetationShader.GetValue(null);
        var originals = (Dictionary<Material, float>)AccessTools.Field(materials, "Original").GetValue(null);
        var freshMaterials = (List<Material>)AccessTools.Field(materials, "Fresh").GetValue(null);
        var previousOriginals = new Dictionary<Material, float>(originals);
        var previousFresh = new List<Material>(freshMaterials);
        object previousApplied = AccessTools.Field(materials, "_applied").GetValue(null);
        object previousSweep = AccessTools.Field(materials, "_nextSweepAt").GetValue(null);
        Type gameShaders = visuals.GetType("ValheimVisuals.GameShaders", true)!;
        object normalMapped = AccessTools.Field(gameShaders, "NormalMapped").GetValue(null);
        var names = (HashSet<string>)AccessTools.Field(normalMapped.GetType(), "_names").GetValue(normalMapped);
        var known = (Dictionary<int, bool>)AccessTools.Field(normalMapped.GetType(), "_known").GetValue(normalMapped);
        var previousNames = new HashSet<string>(names);
        var previousKnown = new Dictionary<int, bool>(known);
        var fixture = new Harmony("DeepNorthCompat.Tests.VpoMaterials");

        void Prefix(MethodBase target, string method) => fixture.Patch(target, prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(VpoMaterialsTests), method)));

        try
        {
            Prefix(AccessTools.Method(typeof(Material), "CreateWithMaterial"), nameof(Copy));
            Prefix(AccessTools.PropertyGetter(typeof(Material), nameof(Material.shader)), nameof(ShaderOf));
            Prefix(AccessTools.PropertySetter(typeof(Material), nameof(Material.shader)), nameof(SetShader));
            Prefix(AccessTools.PropertyGetter(typeof(Object), nameof(Object.name)), nameof(NameOf));
            Prefix(AccessTools.PropertySetter(typeof(Object), nameof(Object.name)), nameof(SetName));
            Prefix(AccessTools.Method(typeof(Material), nameof(Material.HasProperty), new[] { typeof(string) }), nameof(HasProperty));
            Prefix(AccessTools.Method(typeof(Material), nameof(Material.HasFloat), new[] { typeof(int) }), nameof(HasFloat));
            Prefix(AccessTools.Method(typeof(Material), nameof(Material.HasTexture), new[] { typeof(int) }), nameof(HasTexture));
            Prefix(AccessTools.Method(typeof(Material), nameof(Material.GetFloat), new[] { typeof(int) }), nameof(GetFloat));
            Prefix(AccessTools.Method(typeof(Material), nameof(Material.SetFloat), new[] { typeof(int), typeof(float) }), nameof(SetFloat));
            Prefix(AccessTools.Method(typeof(Material), nameof(Material.GetTexture), new[] { typeof(int) }), nameof(GetTexture));
            Prefix(AccessTools.Method(typeof(Material), nameof(Material.IsKeywordEnabled), new[] { typeof(string) }), nameof(Keyword));
            Prefix(AccessTools.PropertyGetter(typeof(Material), nameof(Material.renderQueue)), nameof(Queue));
            Prefix(AccessTools.PropertySetter(typeof(Material), nameof(Material.renderQueue)), nameof(IgnoreQueue));
            Prefix(AccessTools.PropertySetter(typeof(Material), nameof(Material.enableInstancing)), nameof(IgnoreInstancing));
            Prefix(AccessTools.DeclaredMethod(replacer, "SetKeyword"), nameof(IgnoreKeyword));
            Prefix(AccessTools.DeclaredMethod(loaded, "Get"), nameof(LoadedMaterials));

            var sourceShader = Fake<Shader>();
            var vpoShader = Fake<Shader>();
            var texture = Fake<Texture2D>();
            Names[sourceShader] = "Custom/Vegetation";
            Names[vpoShader] = "VPO/Vegetation";
            vegetationShader.SetValue(null, vpoShader);

            MethodInfo has = AccessTools.DeclaredMethod(normalMapped.GetType(), "Has");
            var classifierProbe = Fake<Material>();
            Shaders[classifierProbe] = vpoShader;
            Check(!(bool)has.Invoke(normalMapped, new object[] { classifierProbe }), "fixture expected the unmodified classifier to reject VPO's shader");
            names.Add("VPO/Piece");
            var errors = new List<string>();
            Action<string> previousError = CompatibilityInstaller.Error;
            try
            {
                CompatibilityInstaller.Error = errors.Add;
                VpoMaterialsPatch.Prepare(vpo, visuals);
                new Harmony("DeepNorthCompat.VPO.Materials").UnpatchSelf();
                VpoMaterialsPatch.Verify();
            }
            finally { CompatibilityInstaller.Error = previousError; }
            test("VPO materials roll back failed verification without removing an existing shader", () =>
                Check(names.Contains("VPO/Piece") && !(bool)has.Invoke(normalMapped, new object[] { classifierProbe })
                    && errors.Any(e => e.Contains("NOT APPLIED")), "failed install left classifier changes or removed a pre-existing name"));
            names.Remove("VPO/Piece");
            VpoMaterialsPatch.Prepare(vpo, visuals);
            VpoMaterialsPatch.Verify();
            test("VPO materials invalidate the cached shader classification", () =>
                Check((bool)has.Invoke(normalMapped, new object[] { classifierProbe }), "cached rejection survived installation"));

            var source = Fake<Material>();
            Names[source] = "fixture leaf";
            Shaders[source] = sourceShader;
            Bumps[source] = 1f;
            Maps[source] = texture;
            Loaded.Add(source);
            object settings = FormatterServices.GetUninitializedObject(preset);
            AccessTools.Field(preset, "NormalBoostEnabled").SetValue(settings, true);
            AccessTools.Field(preset, "NormalIntensity").SetValue(settings, 1.4f);
            AccessTools.Field(materials, "_nextSweepAt").SetValue(null, float.MaxValue);
            sweep.Invoke(null, null);
            apply.Invoke(null, new[] { settings });
            object?[] args = { source, null };
            bool created = (bool)create.Invoke(null, args);
            var clone = (Material)args[1]!;
            Loaded.Add(clone);
            var fresh = (List<Material>)AccessTools.Field(materials, "Fresh").GetValue(null);
            int queued = fresh.Count;
            object?[] cachedArgs = { source, null };
            Check((bool)create.Invoke(null, cachedArgs), "cached replacement failed");
            test("VPO materials reuse a copied baseline without duplicating pending updates", () =>
                Check(ReferenceEquals(clone, cachedArgs[1]) && fresh.Count == queued, "cached material was queued twice"));
            sweep.Invoke(null, null);
            apply.Invoke(null, new[] { settings });
            test("VPO materials do not multiply an already boosted copy twice", () =>
                Check(Math.Abs(Bumps[clone] - 1.4f) < 0.0001f, $"clone={Bumps[clone]}"));
            AccessTools.Field(preset, "NormalIntensity").SetValue(settings, 2f);
            apply.Invoke(null, new[] { settings });
            float beforeRestore = Bumps[clone];
            restore.Invoke(null, null);
            float afterRestore = Bumps[clone];
            test("VPO materials follow intensity changes and restore the original source baseline", () =>
            {
                Check(created && Bumps[source] == 1f && Math.Abs(beforeRestore - 2f) < 0.0001f
                    && Math.Abs(afterRestore - 1f) < 0.0001f,
                    $"source={Bumps[source]} clone before restore={beforeRestore} after restore={afterRestore}");
            });

            var untracked = Fake<Material>();
            Names[untracked] = "fixture stone";
            Shaders[untracked] = sourceShader;
            Bumps[untracked] = 2f;
            Maps[untracked] = texture;
            object?[] secondArgs = { untracked, null };
            Check((bool)create.Invoke(null, secondArgs), "second replacement failed");
            var second = (Material)secondArgs[1]!;
            Loaded.Add(untracked); Loaded.Add(second);
            sweep.Invoke(null, null);
            apply.Invoke(null, new[] { settings });
            test("VPO materials discovered after copying keep their own baseline", () =>
                Check(Bumps[untracked] == 4f && Bumps[second] == 4f, $"source={Bumps[untracked]} clone={Bumps[second]}"));
            restore.Invoke(null, null);
            test("VPO materials restore copies made before Visuals tracked the source", () =>
                Check(Bumps[untracked] == 2f && Bumps[second] == 2f, "untracked source or copy did not restore"));
        }
        finally
        {
            new Harmony("DeepNorthCompat.VPO.Materials").UnpatchSelf();
            names.Clear(); names.UnionWith(previousNames);
            known.Clear();
            foreach (var entry in previousKnown) known.Add(entry.Key, entry.Value);
            originals.Clear();
            foreach (var original in previousOriginals) originals.Add(original.Key, original.Value);
            freshMaterials.Clear(); freshMaterials.AddRange(previousFresh);
            AccessTools.Field(materials, "_applied").SetValue(null, previousApplied);
            AccessTools.Field(materials, "_nextSweepAt").SetValue(null, previousSweep);
            ((Dictionary<Material, Material>)AccessTools.Field(replacer, "ReplacementByMaterial").GetValue(null)).Clear();
            vegetationShader.SetValue(null, previousShader);
            fixture.UnpatchSelf();
            Names.Clear(); Shaders.Clear(); Bumps.Clear(); Maps.Clear(); Loaded.Clear();
            foreach (IntPtr pointer in Pointers) Marshal.FreeHGlobal(pointer);
            Pointers.Clear();
        }
    }

    private static T Fake<T>() where T : Object
    {
        var value = (T)FormatterServices.GetUninitializedObject(typeof(T));
        AssignPointer(value);
        return value;
    }

    private static void AssignPointer(Object value)
    {
        IntPtr pointer = Marshal.AllocHGlobal(8);
        Pointers.Add(pointer);
        Marshal.WriteInt32(pointer, ++identity);
        Pointer.SetValue(value, pointer);
    }

    private static bool Copy(Material self, Material source)
    {
        AssignPointer(self);
        Names[self] = Names[source]; Shaders[self] = Shaders[source]; Bumps[self] = Bumps[source];
        if (Maps.TryGetValue(source, out Texture map)) Maps[self] = map;
        return false;
    }

    private static bool ShaderOf(Material __instance, ref Shader __result) { __result = Shaders[__instance]; return false; }
    private static bool SetShader(Material __instance, Shader value) { Shaders[__instance] = value; return false; }
    private static bool NameOf(Object __instance, ref string __result) { __result = Names[__instance]; return false; }
    private static bool SetName(Object __instance, string value) { Names[__instance] = value; return false; }
    private static bool HasProperty(ref bool __result) { __result = true; return false; }
    private static bool HasFloat(ref bool __result) { __result = true; return false; }
    private static bool HasTexture(Material __instance, ref bool __result) { __result = Maps.ContainsKey(__instance); return false; }
    private static bool GetFloat(Material __instance, ref float __result) { __result = Bumps[__instance]; return false; }
    private static bool SetFloat(Material __instance, float value) { Bumps[__instance] = value; return false; }
    private static bool GetTexture(Material __instance, ref Texture __result) { __result = Maps[__instance]; return false; }
    private static bool Keyword(ref bool __result) { __result = false; return false; }
    private static bool Queue(ref int __result) { __result = 2000; return false; }
    private static bool IgnoreQueue() => false;
    private static bool IgnoreInstancing() => false;
    private static bool IgnoreKeyword() => false;
    private static bool LoadedMaterials(ref Material[] __result) { __result = Loaded.ToArray(); return false; }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
