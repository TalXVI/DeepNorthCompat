using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace DeepNorthCompat
{
    internal static class PlantEverythingDustPatch
    {
        internal const string Guid = "advize.PlantEverything";
        internal const string CultivatorDust = "Azumatt.NoCultivatorDust";
        internal const string BuildDust = "Azumatt.NoBuildDust";
        internal const string Owner = Plugin.Guid + ".PlantEverything.Dust";
        private static MethodInfo target = null!;
        private static FieldInfo pieces = null!;
        private static MethodInfo piece = null!;
        private static bool pending;

        internal static void Prepare(Assembly? assembly, Assembly? cultivatorDust, Assembly? buildDust)
        {
            if (assembly == null) { CompatibilityInstaller.Info("PlantEverything.Dust: optional mod absent; inactive."); return; }
            if (cultivatorDust == null && buildDust == null) { CompatibilityInstaller.Info("PlantEverything.Dust: no placement dust mod installed; inactive."); return; }
            if (Guard.KnownServerBuild()) { CompatibilityInstaller.Info("PlantEverything.Dust: client-only; inactive on dedicated server."); return; }
            Guard.Build(assembly, ExpectedBuilds.PlantEverything);
            if (cultivatorDust != null) Guard.Build(cultivatorDust, ExpectedBuilds.NoCultivatorDust);
            if (buildDust != null) Guard.Build(buildDust, ExpectedBuilds.NoBuildDust);
            Guard.Build(typeof(Piece).Assembly,
                "25A0A107DCE4D834C44C2B72D0EAFD5CB7793933BDA81816ACCFA1EA9543DACE",
                "94998885B103D6E5F5480123EAEE0A093E82AF2DDEEEE37536C15BF555F24F3A");
            Type entry = Guard.Type(assembly, "Advize_PlantEverything.PieceDB");
            piece = Guard.Method(entry, "get_Piece", typeof(Piece));
            pieces = AccessTools.DeclaredField(Guard.Type(assembly, "Advize_PlantEverything.StaticMembers"), "pieceRefs");
            if (pieces == null || !pieces.IsStatic || pieces.FieldType != typeof(List<>).MakeGenericType(entry))
                throw new MissingFieldException("Advize_PlantEverything.StaticMembers", "pieceRefs");
            target = Guard.Method(Guard.Type(assembly, "Advize_PlantEverything.PluginInitUtils"), "InitPieces", typeof(void));
            // The dust mods strip effects once, after ZNetScene.Awake. Upstream rebuilds these arrays on
            // every later piece initialization, which each server join triggers.
            new Harmony(Owner).Patch(target, postfix: Guard.Hook(typeof(PlantEverythingDustPatch), nameof(StripDust), Priority.Last));
            Guard.Registered(target);
            pending = true;
            CompatibilityInstaller.Info("PlantEverything.Dust: audited build verified; awaiting piece initialization hook verification.");
        }

        internal static void Verify()
        {
            if (!pending) return;
            pending = false;
            if (Harmony.GetPatchInfo(target)?.Postfixes.Count(p => p.owner == Owner
                && p.PatchMethod == AccessTools.DeclaredMethod(typeof(PlantEverythingDustPatch), nameof(StripDust))) != 1)
            {
                new Harmony(Owner).UnpatchSelf();
                CompatibilityInstaller.Error("PlantEverything.Dust: NOT APPLIED; piece initialization hook registration did not match; vendor behavior retained.");
                return;
            }
            CompatibilityInstaller.Info("PlantEverything.Dust: APPLIED; placement dust stays removed when PlantEverything reinitializes its pieces.");
        }

        private static void StripDust()
        {
            try
            {
                foreach (object entry in (IEnumerable)pieces.GetValue(null))
                {
                    var added = (Piece)piece.Invoke(entry, null);
                    // This is the dust mods' own filter, so the placement sound stays.
                    added.m_placeEffect.m_effectPrefabs = added.m_placeEffect.m_effectPrefabs
                        .Where(effect => !effect.m_prefab.name.Contains("vfx")).ToArray();
                }
            }
            catch (Exception exception)
            {
                CompatibilityInstaller.Error($"PlantEverything.Dust: could not remove placement dust after piece initialization. {exception}");
            }
        }
    }
}
