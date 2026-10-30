using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace DeepNorthCompat
{
    internal static class PlantEasilyCostPatch
    {
        internal const string Guid = "advize.PlantEasily";
        internal const string Owner = Plugin.Guid + ".PlantEasily.GridCost";
        private static MethodInfo update = null!;
        private static MethodInfo ghost = null!;
        private static Piece.Requirement[]? resources;
        private static int[] amounts = new int[0];
        private static bool updating;
        private static bool pending;

        internal static void Prepare(Assembly? assembly)
        {
            if (assembly == null) { CompatibilityInstaller.Info("PlantEasily.GridCost: optional mod absent; inactive."); return; }
            if (Guard.KnownServerBuild()) { CompatibilityInstaller.Info("PlantEasily.GridCost: client-only; inactive on dedicated server."); return; }
            Guard.Build(assembly, ExpectedBuilds.PlantEasily);
            Guard.Build(typeof(Player).Assembly,
                "25A0A107DCE4D834C44C2B72D0EAFD5CB7793933BDA81816ACCFA1EA9543DACE",
                ExpectedBuilds.Valheim1020Client, ExpectedBuilds.Valheim1021Client);
            Type grid = Guard.Type(assembly, "Advize_PlantEasily.GhostGrid");
            update = Guard.Method(grid, "Update", typeof(void), typeof(Player));
            ghost = Guard.Method(grid, "UpdateGhost", typeof(void), typeof(Player), typeof(Piece), typeof(int), typeof(int), typeof(int));
            Guard.Method(grid, "UpdatePieceCost", typeof(void), typeof(Piece), typeof(int), typeof(int));
            resources = null; updating = false;
            var harmony = new Harmony(Owner);
            harmony.Patch(update, prefix: Guard.Hook(typeof(PlantEasilyCostPatch), nameof(Begin)),
                finalizer: Guard.Hook(typeof(PlantEasilyCostPatch), nameof(End)));
            Guard.Registered(update);
            // Upstream scales only the first requirement, in UpdatePieceCost, which is small enough for
            // Mono to inline. Each scaled cost is read inside UpdateGhost, so the rest are scaled there.
            harmony.Patch(ghost, prefix: Guard.Hook(typeof(PlantEasilyCostPatch), nameof(Scale)));
            Guard.Registered(ghost);
            pending = true;
            CompatibilityInstaller.Info($"PlantEasily.GridCost: {assembly.GetName().Version.ToString(3)} build verified; awaiting grid hook verification.");
        }

        internal static void Verify()
        {
            if (!pending) return;
            pending = false;
            Patches? grid = Harmony.GetPatchInfo(update);
            if (grid?.Prefixes.Count(p => p.owner == Owner && p.PatchMethod == Hook(nameof(Begin))) != 1
                || grid.Finalizers.Count(p => p.owner == Owner && p.PatchMethod == Hook(nameof(End))) != 1
                || Harmony.GetPatchInfo(ghost)?.Prefixes.Count(p => p.owner == Owner && p.PatchMethod == Hook(nameof(Scale))) != 1)
            {
                new Harmony(Owner).UnpatchSelf();
                resources = null; updating = false;
                CompatibilityInstaller.Error("PlantEasily.GridCost: NOT APPLIED; grid hook registration did not match; vendor behavior retained.");
                return;
            }
            CompatibilityInstaller.Info("PlantEasily.GridCost: APPLIED; grid placement checks every ingredient of a multi-ingredient recipe.");
        }

        private static MethodInfo Hook(string name) => AccessTools.DeclaredMethod(typeof(PlantEasilyCostPatch), name);

        private static void Begin()
        {
            resources = null; updating = true;
        }

        private static void Scale(Piece __1, int __4)
        {
            if (!updating) return;
            Piece.Requirement[]? current = __1 != null ? __1.m_resources : null;
            if (resources == null)
            {
                // Upstream evaluates cell 0 before it scales anything, so these are the recipe amounts.
                if (__4 != 0 || current == null || current.Length < 2 || current.Any(r => r == null)) { updating = false; return; }
                resources = current; amounts = current.Select(r => r.m_amount).ToArray();
                return;
            }
            if (current != resources) return;
            // Upstream owns the first requirement; the others get the same per-cell multiplier.
            for (int i = 1; i < resources.Length; i++) resources[i].m_amount = amounts[i] * (__4 + 1);
        }

        // A finalizer also restores the recipe when a ghost update throws partway through the grid.
        private static void End()
        {
            if (resources != null)
                for (int i = 0; i < resources.Length; i++) resources[i].m_amount = amounts[i];
            resources = null; updating = false;
        }
    }
}
