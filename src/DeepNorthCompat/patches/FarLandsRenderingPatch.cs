using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using static DeepNorthCompat.ILMatch;

namespace DeepNorthCompat
{
    internal static class FarLandsRenderingPatch
    {
        private const string Group = "FarLands.Rendering";
        private const string Owner = Plugin.Guid + "." + Group;
        private static readonly Dictionary<string, string> IndirectBuilds = new Dictionary<string, string>
        {
            [ExpectedBuilds.Vpo210] = "1F978C5A5E005E2750D53A601E4CAEDFEF0328D851C55CEC6B6214DE040C0A4E",
            [ExpectedBuilds.Vpo220] = "3F98F7CC06CF5E9C5D4F2AEDA3D4F2C51BA5339F4B440898FAC1A7803F87373A"
        };
        private static readonly MethodInfo forceOff = AccessTools.PropertySetter(typeof(Renderer), nameof(Renderer.forceRenderingOff));
        private static readonly MethodInfo visibility = AccessTools.DeclaredMethod(typeof(FarLandsRenderingPatch), nameof(SetHidden));
        private static readonly List<(MethodInfo Target, MethodInfo Hook, HarmonyPatchType Kind)> hooks = new List<(MethodInfo, MethodInfo, HarmonyPatchType)>();
        private static readonly Dictionary<MethodBase, bool> rewrites = new Dictionary<MethodBase, bool>();
        private static readonly Dictionary<Material, Material> sources = new Dictionary<Material, Material>();
        private static readonly HashSet<Behaviour> suspended = new HashSet<Behaviour>();
        private static Type indirect = null!;
        private static Type batched = null!;
        private static AccessTools.FieldRef<object, bool> fallback = null!;
        private static Func<object, Behaviour[]> instances = null!;
        private static Action<GameObject> assignLods = null!;
        private static bool pending;
        private static bool enabled;

        internal static void Prepare(Assembly? farlands, Assembly? vpo)
        {
            if (pending || Harmony.HasAnyPatches(Owner)) return;

            Reset();
            if (farlands == null || vpo == null) { CompatibilityInstaller.Info($"{Group}: optional mod absent; inactive."); return; }
            if (Guard.KnownServerBuild()) { CompatibilityInstaller.Info($"{Group}: client-only patch; inactive."); return; }

            Guard.Build(farlands, ExpectedBuilds.FarLands141);
            Guard.Build(vpo, ExpectedBuilds.VpoGrassAndStreaming);
            Guard.Build(typeof(ZNet).Assembly, ExpectedBuilds.Valheim1021Client);
            batched = Guard.Type(vpo, "ValheimPerformanceOptimizations.Patches.IndirectRendering.VPOBatchedPrefab");
            FieldInfo instanceField = AccessTools.DeclaredField(batched, "instances");
            if (instanceField == null || instanceField.IsStatic || !instanceField.FieldType.IsArray)
                throw new MissingFieldException(batched.FullName, "instances");
            Assembly renderer = instanceField.FieldType.GetElementType()!.Assembly;
            Guard.Build(renderer, IndirectBuilds[Guard.Sha256(vpo.Location)]);
            indirect = Guard.Type(renderer, "IndirectRendering.IndirectInstance");
            Field(batched, "instances", indirect.MakeArrayType(), false);
            fallback = AccessTools.FieldRefAccess<object, bool>(Field(batched, "fallback", typeof(bool), false));
            ParameterExpression value = Expression.Parameter(typeof(object));
            instances = Expression.Lambda<Func<object, Behaviour[]>>(Expression.Convert(
                Expression.Field(Expression.Convert(value, batched), instanceField), typeof(Behaviour[])), value).Compile();
            Type ranges = Guard.Type(vpo, "ValheimPerformanceOptimizations.Patches.IndirectRendering.LodRanges");
            assignLods = (Action<GameObject>)Delegate.CreateDelegate(typeof(Action<GameObject>),
                Guard.Method(ranges, "Assign", typeof(void), typeof(GameObject)));
            Type replacer = Guard.Type(vpo, "ValheimPerformanceOptimizations.Patches.IndirectRendering.PieceMaterialReplacer");
            var replacements = (Dictionary<Material, Material>)Field(replacer, "ReplacementByMaterial", typeof(Dictionary<Material, Material>), true).GetValue(null)!;
            foreach (var pair in replacements) sources.Add(pair.Value, pair.Key);

            Type hider = Guard.Type(farlands, "FarLands.PieceHider");
            Type zone = Guard.Type(farlands, "FarLands.PieceHider+Zone");
            try
            {
                Add(Guard.Method(hider, "SwitchOff", typeof(void), typeof(Renderer), zone), nameof(RewriteHide), HarmonyPatchType.Transpiler);
                Add(Guard.Method(hider, "RestoreSome", typeof(bool), zone), nameof(RewriteRestore), HarmonyPatchType.Transpiler);
                Add(Guard.Method(hider, "RestoreAll", typeof(void)), nameof(RestoreSuspended), HarmonyPatchType.Postfix);
                Add(Guard.Method(batched, "ResumeIndirectRendering", typeof(void)), nameof(SuspendHidden), HarmonyPatchType.Postfix);
                Add(Guard.Method(Guard.Type(farlands, "FarLands.PieceLods"), "Set", typeof(void), typeof(GameObject), typeof(float[]), typeof(bool)),
                    nameof(RefreshLods), HarmonyPatchType.Postfix);
                Add(Guard.Method(replacer, "TryCreate", typeof(bool), typeof(Material), typeof(Material).MakeByRefType()), nameof(CopySource), HarmonyPatchType.Postfix);
                Add(Guard.Method(Guard.Type(farlands, "FarLands.GameLooks"), "Materials", typeof(Material[]), typeof(GameObject), typeof(MeshRenderer)),
                    nameof(SourceMaterials), HarmonyPatchType.Postfix);
                Add(Guard.Method(Guard.Type(farlands, "FarLands.FarPieceMaterials"), "Of", typeof(Material), typeof(Material)), nameof(SourceMaterial), HarmonyPatchType.Prefix);
                Add(Guard.Method(Guard.Type(farlands, "FarLands.RockTypes+<>c"), "<IsBigRock>b__25_1", typeof(bool), typeof(Material)), nameof(SourceMaterial), HarmonyPatchType.Prefix);
                Add(Guard.Method(Guard.Type(farlands, "FarLands.TreeTypes+<>c"), "<IsBigPlant>b__14_2", typeof(bool), typeof(Material)), nameof(SourceMaterial), HarmonyPatchType.Prefix);
                Add(Guard.Method(Guard.Type(farlands, "FarLands.TreeTypes+<>c"), "<IsUnderstorey>b__17_2", typeof(bool), typeof(Material)), nameof(SourceMaterial), HarmonyPatchType.Prefix);
                pending = true;
            }
            catch
            {
                Reset();
                throw;
            }
        }

        internal static void Reset()
        {
            new Harmony(Owner).UnpatchSelf();
            pending = enabled = false;
            RestoreSuspended();
            sources.Clear();
            hooks.Clear();
            rewrites.Clear();
        }

        internal static void Verify()
        {
            if (!pending) return;
            pending = false;
            bool registered = hooks.All(hook =>
            {
                Patches? info = Harmony.GetPatchInfo(hook.Target);
                if (info == null) return false;
                Patch[] ours = info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Where(p => p.owner == Owner).ToArray();
                IEnumerable<Patch> kind = hook.Kind == HarmonyPatchType.Prefix ? info.Prefixes
                    : hook.Kind == HarmonyPatchType.Postfix ? info.Postfixes : info.Transpilers;
                return ours.Length == 1 && kind.Count(p => p.owner == Owner && p.PatchMethod == hook.Hook) == 1;
            });
            if (!registered || rewrites.Values.Any(result => !result))
            {
                Reset();
                CompatibilityInstaller.Error($"{Group}: NOT APPLIED; a required hook or visibility rewrite did not install; vendor behavior retained.");
                return;
            }

            enabled = true;
            CompatibilityInstaller.Info($"{Group}: APPLIED; FarLands visibility, LOD ranges and source materials follow VPO.");
        }

        private static void Add(MethodInfo target, string method, HarmonyPatchType kind)
        {
            HarmonyMethod hook = Guard.Hook(typeof(FarLandsRenderingPatch), method);
            hooks.Add((target, hook.method, kind));
            if (kind == HarmonyPatchType.Transpiler) rewrites.Add(target, false);
            new Harmony(Owner).Patch(target, prefix: kind == HarmonyPatchType.Prefix ? hook : null,
                postfix: kind == HarmonyPatchType.Postfix ? hook : null, transpiler: kind == HarmonyPatchType.Transpiler ? hook : null);
        }

        internal static IEnumerable<CodeInstruction> RewriteHide(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) =>
            Rewrite(instructions, __originalMethod, OpCodes.Ldc_I4_1);

        private static IEnumerable<CodeInstruction> RewriteRestore(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) =>
            Rewrite(instructions, __originalMethod, OpCodes.Ldc_I4_0);

        private static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions, MethodBase method, OpCode value)
        {
            List<CodeInstruction> original = instructions.ToList();
            List<CodeInstruction> code = original.Select(i => new CodeInstruction(i)).ToList();
            int site = Site(code, i => code[i].Calls(forceOff));
            bool matched = Shape(code, site - 1, Is(value), Calls(forceOff));
            rewrites[method] = matched;
            if (!matched)
            {
                Lost(ref enabled, Group, "the native visibility setter changed");
                return original;
            }

            code[site].opcode = OpCodes.Call;
            code[site].operand = visibility;
            return code;
        }

        private static void SetHidden(Renderer renderer, bool hidden)
        {
            renderer.forceRenderingOff = hidden;
            if (!enabled) return;
            var instance = (Behaviour)renderer.GetComponent(indirect);
            if (instance == null) return;
            if (hidden)
            {
                instance.enabled = false;
                suspended.Add(instance);
            }
            else
            {
                instance.enabled = !UnityPath(renderer);
                suspended.Remove(instance);
            }
        }

        private static bool UnityPath(Component instance)
        {
            Component parent = instance.GetComponentInParent(batched);
            if (parent == null) throw new InvalidOperationException("A VPO indirect instance has no VPOBatchedPrefab parent.");
            return fallback(parent);
        }

        private static void SuspendHidden(object __instance)
        {
            if (!enabled) return;
            foreach (Behaviour instance in instances(__instance))
                if (suspended.Contains(instance)) instance.enabled = false;
        }

        private static void RestoreSuspended()
        {
            foreach (Behaviour instance in suspended)
                if (instance != null) instance.enabled = !UnityPath(instance);
            suspended.Clear();
        }

        internal static void Tick()
        {
            if (!enabled && suspended.Count != 0) RestoreSuspended();
        }

        private static void RefreshLods(GameObject root)
        {
            if (!enabled) return;
            assignLods(root);
            foreach (Behaviour instance in root.GetComponentsInChildren(indirect, true))
            {
                if (!instance.enabled) continue;
                instance.enabled = false;
                instance.enabled = true;
            }
        }

        private static void CopySource(Material source, Material replacement, bool __result)
        {
            if (enabled && __result && !ReferenceEquals(source, replacement)) sources[replacement] = source;
        }

        private static Material? Source(Material? material) => enabled && material is not null && sources.TryGetValue(material, out Material source)
            ? source : material;

        private static void SourceMaterial(ref Material? __0) => __0 = Source(__0);

        private static void SourceMaterials(ref Material?[] __result)
        {
            if (!enabled) return;
            Material?[]? mapped = null;
            for (int i = 0; i < __result.Length; i++)
            {
                Material? source = Source(__result[i]);
                if (ReferenceEquals(source, __result[i])) continue;
                if (mapped == null) mapped = (Material[])__result.Clone();
                mapped[i] = source;
            }
            if (mapped != null) __result = mapped;
        }

        private static FieldInfo Field(Type type, string name, Type expected, bool isStatic)
        {
            FieldInfo field = AccessTools.DeclaredField(type, name);
            if (field == null || field.FieldType != expected || field.IsStatic != isStatic) throw new MissingFieldException(type.FullName, name);
            return field;
        }
    }
}
