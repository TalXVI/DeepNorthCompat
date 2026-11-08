using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace DeepNorthCompat
{
    // VPO copies a material after ValheimVisuals may have changed its normal strength. Give that
    // copy the source's original baseline so subsequent Visuals updates and restoration reach it.
    internal static class VpoMaterialsPatch
    {
        internal const string VisualsGuid = "ru.ivest.valheimvisuals";
        private const string Owner = Plugin.Guid + ".VPO.Materials";
        private const string Group = "VPO.Materials";
        private static readonly string[] Visuals =
        {
            "52A6F18EB1B26930689177EC1780915BC8AA7E08A628A647142B87CC50A15D19",
            "536B2BEB1B6FA7FA08FFCE8C448B642B1CF51DC947A02042AC0FB8F72A9238AF"
        };

        private static readonly string[] ShaderNames = { "VPO/Piece", "VPO/StaticRock", "VPO/Vegetation", "VPO/Creature", "VPO/Standard" };
        private static readonly MethodInfo copy = AccessTools.DeclaredMethod(typeof(VpoMaterialsPatch), nameof(CopyBaseline));
        private static readonly List<string> added = new List<string>();
        private static HashSet<string> names = null!;
        private static Dictionary<int, bool> known = null!;
        private static Dictionary<Material, float> originals = null!;
        private static List<Material> fresh = null!;
        private static MethodInfo create = null!;
        private static bool pending;

        internal static void Prepare(Assembly? vpo, Assembly? visuals)
        {
            if (pending || Harmony.HasAnyPatches(Owner)) return;
            if (vpo == null || visuals == null) { CompatibilityInstaller.Info($"{Group}: optional mod absent; inactive."); return; }
            if (Application.platform == RuntimePlatform.WindowsServer || Application.platform == RuntimePlatform.LinuxServer)
            { CompatibilityInstaller.Info($"{Group}: client-only patch; inactive."); return; }
            if (Guard.Sha256(vpo.Location) == ExpectedBuilds.Vpo123) { CompatibilityInstaller.Info($"{Group}: VPO 1.2.3 has no indirect materials; inactive."); return; }

            Guard.Build(vpo, ExpectedBuilds.Vpo2);
            Guard.Build(visuals, Visuals);
            Guard.Build(typeof(ZNet).Assembly, ExpectedBuilds.Valheim1021Client);
            Type replacer = Guard.Type(vpo, "ValheimPerformanceOptimizations.Patches.IndirectRendering.PieceMaterialReplacer");
            Type materials = Guard.Type(visuals, "ValheimVisuals.Materials");
            Type shaders = Guard.Type(visuals, "ValheimVisuals.GameShaders");
            Type shaderSet = Guard.Type(visuals, "ValheimVisuals.ShaderSet");
            create = Guard.Method(replacer, "TryCreate", typeof(bool), typeof(Material), typeof(Material).MakeByRefType());
            var replacements = (Dictionary<Material, Material>)Field(replacer, "ReplacementByMaterial", typeof(Dictionary<Material, Material>), true).GetValue(null)!;
            if (replacements.Count != 0) throw new InvalidOperationException("VPO material copies already exist before the integration loaded.");
            originals = (Dictionary<Material, float>)Field(materials, "Original", typeof(Dictionary<Material, float>), true).GetValue(null)!;
            fresh = (List<Material>)Field(materials, "Fresh", typeof(List<Material>), true).GetValue(null)!;
            object normalMapped = Field(shaders, "NormalMapped", shaderSet, true).GetValue(null)!;
            names = (HashSet<string>)Field(shaderSet, "_names", typeof(HashSet<string>), false).GetValue(normalMapped)!;
            known = (Dictionary<int, bool>)Field(shaderSet, "_known", typeof(Dictionary<int, bool>), false).GetValue(normalMapped)!;

            try
            {
                new Harmony(Owner).Patch(create, postfix: new HarmonyMethod(copy));
                foreach (string name in ShaderNames)
                    if (names.Add(name)) added.Add(name);
                known.Clear();
                pending = true;
            }
            catch
            {
                RemoveNames();
                new Harmony(Owner).UnpatchSelf();
                throw;
            }
        }

        internal static void Verify()
        {
            if (!pending) return;
            pending = false;
            if (Harmony.GetPatchInfo(create)?.Postfixes.Count(p => p.owner == Owner && p.PatchMethod == copy) != 1)
            {
                RemoveNames();
                new Harmony(Owner).UnpatchSelf();
                CompatibilityInstaller.Error($"{Group}: NOT APPLIED; the material-copy hook did not install.");
                return;
            }
            added.Clear();
            CompatibilityInstaller.Info($"{Group}: APPLIED; Valheim Visuals normal strength follows VPO material copies.");
        }

        private static void CopyBaseline(Material source, Material replacement, bool __result)
        {
            if (__result && !ReferenceEquals(source, replacement) && originals.TryGetValue(source, out float baseline)
                && !originals.ContainsKey(replacement))
            {
                originals.Add(replacement, baseline);
                fresh.Add(replacement);
            }
        }

        private static FieldInfo Field(Type type, string name, Type expected, bool isStatic)
        {
            FieldInfo field = AccessTools.DeclaredField(type, name);
            if (field == null || field.IsStatic != isStatic || field.FieldType != expected) throw new MissingFieldException(type.FullName, name);
            return field;
        }

        private static void RemoveNames()
        {
            foreach (string name in added) names.Remove(name);
            added.Clear();
            known.Clear();
        }
    }
}
