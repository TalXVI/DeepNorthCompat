using System;
using System.Collections;
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
using Object = UnityEngine.Object;

// Real FarLands and VPO methods run against managed Unity object storage. No scene or GPU is created.
internal static class FarLandsRenderingTests
{
    private const string Owner = "DeepNorthCompat.FarLands.Rendering";
    private static readonly List<IntPtr> pointers = new List<IntPtr>();
    private static readonly Dictionary<Object, string> names = new Dictionary<Object, string>();
    private static readonly Dictionary<Material, Shader> shaders = new Dictionary<Material, Shader>();
    private static readonly Dictionary<Renderer, bool> hidden = new Dictionary<Renderer, bool>();
    private static readonly Dictionary<Renderer, bool> native = new Dictionary<Renderer, bool>();
    private static readonly Dictionary<Renderer, Material[]> materials = new Dictionary<Renderer, Material[]>();
    private static readonly Dictionary<Behaviour, bool> active = new Dictionary<Behaviour, bool>();
    private static readonly Dictionary<Component, Component> companions = new Dictionary<Component, Component>();
    private static readonly Dictionary<Component, Component> parents = new Dictionary<Component, Component>();
    private static readonly Dictionary<GameObject, Component[]> children = new Dictionary<GameObject, Component[]>();
    private static readonly Dictionary<LODGroup, LOD[]> lods = new Dictionary<LODGroup, LOD[]>();
    private static readonly Dictionary<Component, Transform> transforms = new Dictionary<Component, Transform>();
    private static readonly Dictionary<Material, Dictionary<int, float>> floats = new Dictionary<Material, Dictionary<int, float>>();
    private static readonly Dictionary<Material, HashSet<string>> keywords = new Dictionary<Material, HashSet<string>>();
    private static readonly Dictionary<Material, int> queues = new Dictionary<Material, int>();
    private static readonly Dictionary<string, int> properties = new Dictionary<string, int>();
    private static int identity;

    internal static void Run(string profile, Action<string, Action> test)
    {
        test("FarLands rendering remains inactive when either optional mod is absent", () =>
        {
            FarLandsRenderingPatch.Prepare(null, null);
            FarLandsRenderingPatch.Prepare(typeof(FarLandsRenderingTests).Assembly, null);
            FarLandsRenderingPatch.Prepare(null, typeof(FarLandsRenderingTests).Assembly);
            FarLandsRenderingPatch.Verify();
            Check(!Harmony.HasAnyPatches(Owner), "an absent mod installed a rendering hook");
        });
        string plugins = Path.Combine(profile, "BepInEx/plugins");
        string[] farFiles = Directory.GetFiles(plugins, "FarLands.dll", SearchOption.AllDirectories);
        string[] vpoFiles = Directory.GetFiles(plugins, "ValheimPerformanceOptimizations.dll", SearchOption.AllDirectories);
        if (farFiles.Length == 0 || vpoFiles.Length == 0) return;
        Assembly far = Assembly.LoadFrom(farFiles.Single()), vpo = Assembly.LoadFrom(vpoFiles.Single());
        if (Guard.KnownServerBuild())
        {
            test("FarLands rendering remains inactive on a dedicated server", () =>
            {
                FarLandsRenderingPatch.Prepare(far, vpo);
                FarLandsRenderingPatch.Verify();
                Check(!Harmony.HasAnyPatches(Owner), "a client rendering integration installed on a server");
            });
            return;
        }
        test("FarLands rendering rejects unaudited target bytes", () =>
        {
            try { FarLandsRenderingPatch.Prepare(typeof(FarLandsRenderingTests).Assembly, vpo); throw new Exception("unknown FarLands accepted"); }
            catch (NotSupportedException) { }
            try { FarLandsRenderingPatch.Prepare(far, typeof(FarLandsRenderingTests).Assembly); throw new Exception("unknown VPO accepted"); }
            catch (NotSupportedException) { }
            Check(!Harmony.HasAnyPatches(Owner), "an unknown target installed a rendering hook");
        });
        Assembly indirect = Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(vpo.Location)!, "IndirectRendering.dll"));
        Type hiderType = far.GetType("FarLands.PieceHider", true)!;
        Type zoneType = hiderType.GetNestedType("Zone", BindingFlags.NonPublic)!;
        Type batchedType = vpo.GetType("ValheimPerformanceOptimizations.Patches.IndirectRendering.VPOBatchedPrefab", true)!;
        Type instanceType = indirect.GetType("IndirectRendering.IndirectInstance", true)!;
        Type patchType = vpo.GetType("ValheimPerformanceOptimizations.Patches.IndirectRendering.IndirectRendererPatches", true)!;
        Type replacer = vpo.GetType("ValheimPerformanceOptimizations.Patches.IndirectRendering.PieceMaterialReplacer", true)!;
        var replacements = (Dictionary<Material, Material>)AccessTools.Field(replacer, "ReplacementByMaterial").GetValue(null);
        var savedReplacements = new Dictionary<Material, Material>(replacements);
        object? savedEnabled = AccessTools.Field(patchType, "<Enabled>k__BackingField").GetValue(null);
        var fixture = new Harmony("DeepNorthCompat.Tests.FarLandsRendering");

        void Prefix(MethodBase target, string method) => fixture.Patch(target, prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(FarLandsRenderingTests), method)));

        try
        {
            Prefix(AccessTools.PropertyGetter(typeof(Object), "name"), nameof(NameOf));
            Prefix(AccessTools.PropertySetter(typeof(Object), "name"), nameof(SetName));
            Prefix(AccessTools.PropertyGetter(typeof(Material), "shader"), nameof(ShaderOf));
            Prefix(AccessTools.PropertyGetter(typeof(Renderer), "forceRenderingOff"), nameof(Hidden));
            Prefix(AccessTools.PropertySetter(typeof(Renderer), "forceRenderingOff"), nameof(SetHidden));
            Prefix(AccessTools.PropertyGetter(typeof(Renderer), "enabled"), nameof(Native));
            Prefix(AccessTools.PropertySetter(typeof(Renderer), "enabled"), nameof(SetNative));
            Prefix(AccessTools.PropertyGetter(typeof(Renderer), "sharedMaterials"), nameof(Materials));
            Prefix(AccessTools.PropertyGetter(typeof(Behaviour), "enabled"), nameof(Active));
            Prefix(AccessTools.PropertySetter(typeof(Behaviour), "enabled"), nameof(SetActive));
            Prefix(AccessTools.Method(typeof(Component), "GetComponent", new[] { typeof(Type) }), nameof(ComponentOf));
            Prefix(AccessTools.Method(typeof(Component), "GetComponentInParent", new[] { typeof(Type) }), nameof(ParentOf));
            Prefix(AccessTools.GetDeclaredMethods(typeof(Component)).Single(m => m.Name == "TryGetComponent" && m.IsGenericMethod)
                .MakeGenericMethod(instanceType), nameof(TryComponent));
            Prefix(AccessTools.Method(typeof(GameObject), "GetComponentsInternal"), nameof(Children));
            Prefix(AccessTools.PropertyGetter(typeof(Component), "transform"), nameof(TransformOf));
            Prefix(AccessTools.PropertyGetter(typeof(GameObject), "transform"), nameof(RootTransform));
            Prefix(AccessTools.PropertyGetter(typeof(Transform), "lossyScale"), nameof(Scale));
            Prefix(AccessTools.Method(typeof(Transform), "TransformPoint", new[] { typeof(Vector3) }), nameof(Point));
            Prefix(AccessTools.Method(typeof(Transform), "InverseTransformPoint", new[] { typeof(Vector3) }), nameof(Point));
            Prefix(AccessTools.Method(typeof(LODGroup), "GetLODs"), nameof(GetLods));
            Prefix(AccessTools.Method(typeof(LODGroup), "SetLODs"), nameof(SetLods));
            Prefix(AccessTools.PropertyGetter(typeof(LODGroup), "size"), nameof(Size));
            Prefix(AccessTools.PropertyGetter(typeof(LODGroup), "enabled"), nameof(Compatible));
            Prefix(AccessTools.PropertyGetter(typeof(LODGroup), "localReferencePoint"), nameof(ReferencePoint));
            Prefix(AccessTools.Method(typeof(Shader), "PropertyToID"), nameof(Property));
            Prefix(AccessTools.PropertyGetter(typeof(Material), "renderQueue"), nameof(Queue));
            Prefix(AccessTools.PropertySetter(typeof(Material), "renderQueue"), nameof(SetQueue));
            Prefix(AccessTools.PropertySetter(typeof(Object), "hideFlags"), nameof(Ignore));
            Prefix(AccessTools.Method(typeof(Material), "CreateWithShader"), nameof(Create));
            Prefix(AccessTools.Method(typeof(Material), "HasProperty", new[] { typeof(int) }), nameof(Has));
            Prefix(AccessTools.Method(typeof(Material), "HasFloat", new[] { typeof(int) }), nameof(Has));
            Prefix(AccessTools.Method(typeof(Material), "HasInteger", new[] { typeof(int) }), nameof(NoInteger));
            Prefix(AccessTools.Method(typeof(Material), "GetFloat", new[] { typeof(int) }), nameof(Float));
            Prefix(AccessTools.Method(typeof(Material), "SetFloat", new[] { typeof(int), typeof(float) }), nameof(SetFloat));
            Prefix(AccessTools.Method(typeof(Material), "SetColor", new[] { typeof(int), typeof(Color) }), nameof(Ignore));
            Prefix(AccessTools.Method(typeof(Material), "IsKeywordEnabled", new[] { typeof(string) }), nameof(Keyword));
            Prefix(AccessTools.Method(typeof(Material), "EnableKeyword", new[] { typeof(string) }), nameof(EnableKeyword));

            FarLandsRenderingPatch.Prepare(far, vpo);
            FarLandsRenderingPatch.Verify();

            var config = new ConfigFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox/farlands-rendering.cfg"), false) { SaveOnConfigSet = false };
            AccessTools.Field(patchType, "<Enabled>k__BackingField").SetValue(null, config.Bind("fixture", "enabled", true));
            var renderer = Fake<MeshRenderer>();
            var instance = (Behaviour)Fake(instanceType);
            var batch = (Component)Fake(batchedType);
            Array instances = Array.CreateInstance(instanceType, 1);
            instances.SetValue(instance, 0);
            AccessTools.Field(batchedType, "instances").SetValue(batch, instances);
            AccessTools.Field(batchedType, "toggledRenderers").SetValue(batch, new[] { renderer });
            companions[renderer] = instance;
            companions[instance] = renderer;
            parents[renderer] = batch;
            parents[instance] = batch;
            hidden[renderer] = false;
            native[renderer] = true;
            active[instance] = false;
            active[(Behaviour)batch] = true;
            object hider = FormatterServices.GetUninitializedObject(hiderType);
            AccessTools.Field(hiderType, "_clock").SetValue(hider, new System.Diagnostics.Stopwatch());
            AccessTools.Field(hiderType, "_budget").SetValue(hider, double.PositiveInfinity);
            object zone = Activator.CreateInstance(zoneType, true)!;
            MethodInfo hide = AccessTools.DeclaredMethod(hiderType, "SwitchOff");
            MethodInfo restore = AccessTools.DeclaredMethod(hiderType, "RestoreSome");
            MethodInfo resume = AccessTools.DeclaredMethod(batchedType, "ResumeIndirectRendering");
            MethodInfo unity = AccessTools.DeclaredMethod(batchedType, "FallbackToUnityRendering");

            test("FarLands handoff suspends VPO and restores it after returning to native range", () =>
            {
                resume.Invoke(batch, null);
                Check(active[instance] && !native[renderer], "VPO did not select its indirect path");
                hide.Invoke(hider, new[] { (object)renderer, zone });
                bool suspended = !active[instance] && hidden[renderer];
                restore.Invoke(hider, new[] { zone });
                Check(suspended && active[instance] && !hidden[renderer], "native hide did not suspend the independent indirect instance");
            });
            test("FarLands restore respects VPO disable and a later re-enable while hidden", () =>
            {
                hide.Invoke(hider, new[] { (object)renderer, zone });
                unity.Invoke(batch, null);
                restore.Invoke(hider, new[] { zone });
                Check(!active[instance] && native[renderer] && !hidden[renderer], "restore enabled indirect drawing after VPO disabled it");
                hide.Invoke(hider, new[] { (object)renderer, zone });
                resume.Invoke(batch, null);
                bool suspended = !active[instance];
                restore.Invoke(hider, new[] { zone });
                Check(suspended && active[instance], "VPO re-enable escaped the current FarLands hide");
            });

            if (Guard.Sha256(vpo.Location) == ExpectedBuilds.Vpo220)
            {
                test("FarLands retains a hidden piece across VPO component disable and enable", () =>
                {
                    hide.Invoke(hider, new[] { (object)renderer, zone });
                    active[(Behaviour)batch] = false;
                    AccessTools.DeclaredMethod(batchedType, "OnDisable").Invoke(batch, null);
                    Check(hidden[renderer] && !active[instance], "disabling the batch exposed the hidden piece");

                    active[(Behaviour)batch] = true;
                    AccessTools.DeclaredMethod(batchedType, "OnEnable").Invoke(batch, null);
                    Check(hidden[renderer] && !active[instance], "re-enabling the batch escaped the FarLands hide");

                    restore.Invoke(hider, new[] { zone });
                    Check(!hidden[renderer] && active[instance], "restoring the piece did not resume indirect drawing");
                });
            }

            test("FarLands can take a loaded zone before every VPO LOD is out of range", () =>
            {
                Type levels = far.GetType("FarLands.BuildingLevels", true)!;
                MethodInfo ours = levels.GetMethod("Ours", new[] { typeof(bool), typeof(float), typeof(float), typeof(float) })!;
                // One small piece is past its 50 m cutoff, while a larger piece still draws to 300 m.
                bool owns = (bool)ours.Invoke(null, new object[] { false, 100f, 256f, 50f });
                Check(owns && 100f < 300f, "fixture does not reach overlapping active ranges");
            });

            test("FarLands LOD keep and restore update VPO distances", () =>
            {
                var root = Fake<GameObject>();
                var transform = Fake<Transform>();
                var group = Fake<LODGroup>();
                children[root] = new Component[] { transform, group, instance };
                transforms[group] = transform;
                lods[group] = new[] { new LOD(0.05f, new Renderer[] { renderer }) };
                AccessTools.Field(instanceType, "lodRoot").SetValue(instance, transform);
                Type ranges = vpo.GetType("ValheimPerformanceOptimizations.Patches.IndirectRendering.LodRanges", true)!;
                AccessTools.DeclaredMethod(ranges, "Assign").Invoke(null, new object[] { root });
                Check(Math.Abs((float)AccessTools.Field(instanceType, "lodEndDistance").GetValue(instance) - 200f) < 0.001f,
                    "VPO did not capture the original terminal cutoff");
                Type keeper = far.GetType("FarLands.PieceLods", true)!;
                object owner = Activator.CreateInstance(keeper, true)!;
                MethodInfo set = AccessTools.DeclaredMethod(keeper, "Set");
                set.Invoke(owner, new object[] { root, new[] { 0.05f }, true });
                bool extends = float.IsPositiveInfinity((float)AccessTools.Field(instanceType, "lodEndDistance").GetValue(instance));
                set.Invoke(owner, new object[] { root, new[] { 0.05f }, false });
                Check(extends && Math.Abs((float)AccessTools.Field(instanceType, "lodEndDistance").GetValue(instance) - 200f) < 0.001f,
                    "native terminal LOD edits left the indirect distance stale");
            });

            Shader vanillaRock = MakeShader("Custom/StaticRock"), vpoRock = MakeShader("VPO/StaticRock");
            Shader vanillaPlant = MakeShader("Custom/Vegetation"), vpoPlant = MakeShader("VPO/Vegetation");
            Material rock = Material(vanillaRock), rockCopy = Material(vpoRock);
            Material plant = Material(vanillaPlant), plantCopy = Material(vpoPlant);
            replacements.Add(rock, rockCopy);
            replacements.Add(plant, plantCopy);
            // The actual TryCreate cached-result path supplies the material provenance hook.
            Prefix(AccessTools.DeclaredMethod(replacer, "IsCompatible"), nameof(Compatible));
            Type bundle = vpo.GetType("ValheimPerformanceOptimizations.Patches.IndirectRendering.PieceShaderBundle", true)!;
            object? savedRock = AccessTools.Field(bundle, "<StaticRockShader>k__BackingField").GetValue(null);
            object? savedPlant = AccessTools.Field(bundle, "<VegetationShader>k__BackingField").GetValue(null);
            try
            {
                AccessTools.Field(bundle, "<StaticRockShader>k__BackingField").SetValue(null, vpoRock);
                AccessTools.Field(bundle, "<VegetationShader>k__BackingField").SetValue(null, vpoPlant);
                foreach (Material source in new[] { rock, plant })
                    AccessTools.DeclaredMethod(replacer, "TryCreate").Invoke(null, new object?[] { source, null });

                test("FarLands retains rock and understorey shader selection after VPO material replacement", () =>
                {
                    bool Reject(string type, string method, Material material)
                    {
                        Type closure = far.GetType(type + "+<>c", true)!;
                        return (bool)AccessTools.DeclaredMethod(closure, method).Invoke(AccessTools.Field(closure, "<>9").GetValue(null), new object[] { material });
                    }

                    Check(!Reject("FarLands.RockTypes", "<IsBigRock>b__25_1", rockCopy), "a VPO copy stopped being a rock");
                    Check(Reject("FarLands.TreeTypes", "<IsBigPlant>b__14_2", rockCopy), "a VPO rock became a big plant");
                    Check(!Reject("FarLands.TreeTypes", "<IsUnderstorey>b__17_2", plantCopy), "a VPO copy stopped being understorey");
                    Check(Reject("FarLands.RockTypes", "<IsBigRock>b__25_1", plantCopy), "vegetation was accepted as rock");
                    Check(Reject("FarLands.TreeTypes", "<IsUnderstorey>b__17_2", rockCopy), "rock was accepted as understorey");
                });

                test("FarLands native material reads and far-piece copies retain source shader semantics with indirect off", () =>
                {
                    unity.Invoke(batch, null);
                    Shader pieceShader = MakeShader("Custom/Piece"), indirectPiece = MakeShader("VPO/Piece");
                    Material piece = Material(pieceShader), copy = Material(indirectPiece);
                    floats[piece][Shader.PropertyToID("_Cutoff")] = 0.4f;
                    floats[piece][Shader.PropertyToID("_Glossiness")] = 0.2f;
                    floats[piece][Shader.PropertyToID("_AddSnow")] = 1f;
                    replacements.Add(piece, copy);
                    AccessTools.DeclaredMethod(replacer, "TryCreate").Invoke(null, new object?[] { piece, null });
                    materials[renderer] = new[] { copy, plantCopy };
                    Type looks = far.GetType("FarLands.GameLooks", true)!;
                    var read = (Material[])AccessTools.DeclaredMethod(looks, "Materials").Invoke(null, new object[] { Fake<GameObject>(), renderer });
                    Check(ReferenceEquals(read[0], piece) && ReferenceEquals(read[1], plant) && ReferenceEquals(materials[renderer][0], copy),
                        "FarLands read replacement shaders or changed the live renderer's materials");
                    Type converter = far.GetType("FarLands.FarPieceMaterials", true)!;
                    object farMaterials = Activator.CreateInstance(converter, true)!;
                    Shader farShader = MakeShader("FarLands/FarPiece");
                    AccessTools.Field(converter, "_loaded").SetValue(farMaterials, true);
                    AccessTools.Field(converter, "_shader").SetValue(farMaterials, farShader);
                    MethodInfo of = AccessTools.DeclaredMethod(converter, "Of");
                    var converted = (Material)of.Invoke(farMaterials, new object[] { copy });
                    Check(!ReferenceEquals(converted, copy) && ReferenceEquals(shaders[converted], farShader)
                        && floats[converted][Shader.PropertyToID("_Cutoff")] == 0.4f
                        && floats[converted][Shader.PropertyToID("_GlossRgb")] == 1f
                        && keywords[converted].Contains("_FARLANDS_SNOW"), "far-piece conversion lost piece alpha, gloss or snow semantics");
                    Material unknown = Material(MakeShader("OtherMod/Unknown"));
                    Check(ReferenceEquals(of.Invoke(farMaterials, new object[] { unknown }), unknown), "an unknown shader was broadly accepted");
                    Check(native[renderer] && !active[instance] && ReferenceEquals(shaders[copy], indirectPiece), "indirect-off changed the source renderer or shader");
                });
            }
            finally
            {
                AccessTools.Field(bundle, "<StaticRockShader>k__BackingField").SetValue(null, savedRock);
                AccessTools.Field(bundle, "<VegetationShader>k__BackingField").SetValue(null, savedPlant);
            }

            test("FarLands rendering restores suspended VPO instances on update after a rewrite loses its anchor", () =>
            {
                resume.Invoke(batch, null);
                hide.Invoke(hider, new[] { (object)renderer, zone });
                Check(!active[instance], "fixture did not reach a suspended indirect instance");
                var errors = new List<string>();
                Action<string> previousError = CompatibilityInstaller.Error;
                try
                {
                    CompatibilityInstaller.Error = errors.Add;
                    fixture.Patch(hide, transpiler: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(FarLandsRenderingTests), nameof(ChangeHide)))
                    { priority = Priority.First });
                    Check(!active[instance] && errors.Any(message => message.Contains("NO LONGER APPLIED")),
                        "the rewrite ran Unity cleanup or did not report its loss");

                    FarLandsRenderingPatch.Tick();
                    Check(active[instance] && hidden[renderer],
                        "a lost rewrite stranded an indirect instance or went unreported");
                    restore.Invoke(hider, new[] { zone });
                    Check(!hidden[renderer] && active[instance], "vendor restore stayed disabled after the integration stopped");
                }
                finally
                {
                    CompatibilityInstaller.Error = previousError;
                    fixture.Unpatch(hide, HarmonyPatchType.Transpiler, fixture.Id);
                }
            });

            test("FarLands rendering rolls the whole group back when verification loses a required hook", () =>
            {
                new Harmony(Owner).UnpatchSelf();
                FarLandsRenderingPatch.Prepare(far, vpo);
                new Harmony(Owner).Unpatch(AccessTools.DeclaredMethod(hiderType, "RestoreAll"), HarmonyPatchType.Postfix, Owner);
                FarLandsRenderingPatch.Verify();
                Check(!Harmony.HasAnyPatches(Owner), "verification retained a partial rendering group");
            });

            test("FarLands rendering rejects changed IL without leaving a partial rewrite", () =>
            {
                MethodInfo setter = AccessTools.PropertySetter(typeof(Renderer), "forceRenderingOff");
                var code = new List<CodeInstruction>
                {
                    new CodeInstruction(System.Reflection.Emit.OpCodes.Ldc_I4_1), new CodeInstruction(System.Reflection.Emit.OpCodes.Callvirt, setter),
                    new CodeInstruction(System.Reflection.Emit.OpCodes.Ldc_I4_1), new CodeInstruction(System.Reflection.Emit.OpCodes.Callvirt, setter)
                };
                var unchanged = FarLandsRenderingPatch.RewriteHide(code, hide).ToArray();
                Check(unchanged.Length == code.Count && unchanged.Where((instruction, i) => !ReferenceEquals(instruction, code[i])).Count() == 0,
                    "duplicate anchors changed the original input");
            });
        }
        finally
        {
            FarLandsRenderingPatch.Reset();
            fixture.UnpatchSelf();
            replacements.Clear();
            foreach (var item in savedReplacements) replacements.Add(item.Key, item.Value);
            AccessTools.Field(patchType, "<Enabled>k__BackingField").SetValue(null, savedEnabled);
            names.Clear(); shaders.Clear(); hidden.Clear(); native.Clear(); materials.Clear(); active.Clear(); companions.Clear(); parents.Clear();
            children.Clear(); lods.Clear(); transforms.Clear(); floats.Clear(); keywords.Clear(); queues.Clear(); properties.Clear();
            foreach (IntPtr pointer in pointers) Marshal.FreeHGlobal(pointer);
            pointers.Clear();
        }
    }

    private static T Fake<T>() where T : Object => (T)Fake(typeof(T));

    private static Object Fake(Type type)
    {
        var value = (Object)FormatterServices.GetUninitializedObject(type);
        AssignPointer(value);
        return value;
    }

    private static void AssignPointer(Object value)
    {
        IntPtr pointer = Marshal.AllocHGlobal(8);
        pointers.Add(pointer);
        Marshal.WriteInt32(pointer, ++identity);
        AccessTools.Field(typeof(Object), "m_CachedPtr").SetValue(value, pointer);
    }

    private static Shader MakeShader(string name) { var shader = Fake<Shader>(); names[shader] = name; return shader; }
    private static Material Material(Shader shader)
    {
        var material = Fake<Material>();
        shaders[material] = shader;
        names[material] = "fixture " + names[shader];
        floats[material] = new Dictionary<int, float>();
        keywords[material] = new HashSet<string>();
        queues[material] = 2000;
        return material;
    }

    private static bool NameOf(Object __instance, ref string __result) { __result = names[__instance]; return false; }
    private static bool SetName(Object __instance, string value) { names[__instance] = value; return false; }
    private static bool ShaderOf(Material __instance, ref Shader __result) { __result = shaders[__instance]; return false; }
    private static bool Hidden(Renderer __instance, ref bool __result) { __result = hidden[__instance]; return false; }
    private static bool SetHidden(Renderer __instance, bool value) { hidden[__instance] = value; return false; }
    private static bool Native(Renderer __instance, ref bool __result) { __result = native[__instance]; return false; }
    private static bool SetNative(Renderer __instance, bool value) { native[__instance] = value; return false; }
    private static bool Materials(Renderer __instance, ref Material[] __result) { __result = (Material[])materials[__instance].Clone(); return false; }
    private static bool Active(Behaviour __instance, ref bool __result) { __result = active[__instance]; return false; }
    private static bool SetActive(Behaviour __instance, bool value) { active[__instance] = value; return false; }
    private static bool ComponentOf(Component __instance, Type type, ref Component __result)
    {
        __result = companions.TryGetValue(__instance, out Component found) && type.IsInstanceOfType(found) ? found : null!;
        return false;
    }

    private static bool ParentOf(Component __instance, ref Component __result) { __result = parents[__instance]; return false; }
    private static bool TryComponent(Component __instance, object[] __args, ref bool __result)
    {
        __result = companions.TryGetValue(__instance, out Component found);
        __args[0] = found;
        return false;
    }
    private static bool Children(GameObject __instance, Type type, object resultList, ref Array __result)
    {
        Component[] found = children[__instance].Where(type.IsInstanceOfType).ToArray();
        if (resultList is IList list) { list.Clear(); foreach (Component child in found) list.Add(child); }
        __result = Array.CreateInstance(type, found.Length);
        for (int i = 0; i < found.Length; i++) __result.SetValue(found[i], i);
        return false;
    }

    private static bool TransformOf(Component __instance, ref Transform __result) { __result = transforms[__instance]; return false; }
    private static bool RootTransform(GameObject __instance, ref Transform __result) { __result = (Transform)children[__instance].First(c => c is Transform); return false; }
    private static bool Scale(ref Vector3 __result) { __result = Vector3.one; return false; }
    private static bool Point(Vector3 position, ref Vector3 __result) { __result = position; return false; }
    private static bool GetLods(LODGroup __instance, ref LOD[] __result) { __result = (LOD[])lods[__instance].Clone(); return false; }
    private static bool SetLods(LODGroup __instance, LOD[] lods) { FarLandsRenderingTests.lods[__instance] = (LOD[])lods.Clone(); return false; }
    private static bool Size(ref float __result) { __result = 10f; return false; }
    private static bool ReferencePoint(ref Vector3 __result) { __result = Vector3.zero; return false; }
    private static bool Property(string name, ref int __result)
    {
        if (!properties.TryGetValue(name, out int value)) properties.Add(name, value = properties.Count + 1);
        __result = value;
        return false;
    }

    private static bool Queue(Material __instance, ref int __result) { __result = queues[__instance]; return false; }
    private static bool SetQueue(Material __instance, int value) { queues[__instance] = value; return false; }
    private static bool Create(Material self, Shader shader)
    {
        AssignPointer(self);
        shaders[self] = shader;
        floats[self] = new Dictionary<int, float>();
        keywords[self] = new HashSet<string>();
        queues[self] = 2000;
        return false;
    }

    private static bool Has(Material __instance, int nameID, ref bool __result) { __result = floats[__instance].ContainsKey(nameID); return false; }
    private static bool NoInteger(ref bool __result) { __result = false; return false; }
    private static bool Float(Material __instance, int nameID, ref float __result) { __result = floats[__instance][nameID]; return false; }
    private static bool SetFloat(Material __instance, int nameID, float value) { floats[__instance][nameID] = value; return false; }
    private static bool Keyword(Material __instance, string keyword, ref bool __result) { __result = keywords[__instance].Contains(keyword); return false; }
    private static bool EnableKeyword(Material __instance, string keyword) { keywords[__instance].Add(keyword); return false; }
    private static IEnumerable<CodeInstruction> ChangeHide(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(AccessTools.PropertySetter(typeof(Renderer), "forceRenderingOff")))
                yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Nop);
            yield return instruction;
        }
    }

    private static bool Compatible(ref bool __result) { __result = true; return false; }
    private static bool Ignore() => false;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
