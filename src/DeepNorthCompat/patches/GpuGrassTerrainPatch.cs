using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace DeepNorthCompat
{
    internal static class GpuGrassTerrainPatch
    {
        internal const string Name = "Grass.ColliderGround";
        internal const string Owner = Plugin.Guid + "." + Name;
        internal const string VcpGuid = "MidnightsFX.ValheimCommunityPatch";
        private const string HearthBuild = "85E2F266C36EC916E714832C48B771F6FA61F80C7360DF095E813E21B372C5E1";
        private const string VcpBuild = "65C3F7DF297A318701706AA9726E00708D4321A78BC8A6557074B45DED4C63E8";
        private static readonly List<(MethodInfo Target, MethodInfo Hook)> hooks = new List<(MethodInfo, MethodInfo)>();
        private static readonly MethodInfo colliderGroundHook = AccessTools.DeclaredMethod(typeof(GpuGrassTerrainPatch), nameof(ColliderGround));
        private static readonly MethodInfo gameBuildHook = AccessTools.DeclaredMethod(typeof(GpuGrassTerrainPatch), nameof(GameBuild));
        private static bool pending;

        internal static void Prepare(Assembly? hearth, Assembly? steadyFrame, Assembly? farLands, Assembly? vcp)
        {
            new Harmony(Owner).UnpatchSelf();
            hooks.Clear();
            pending = false;

            if (hearth == null || steadyFrame == null && farLands == null)
            {
                CompatibilityInstaller.Info($"{Name}: optional mod absent; inactive.");
                return;
            }

            if (Guard.KnownServerBuild())
            {
                CompatibilityInstaller.Info($"{Name}: client-only; inactive on dedicated server.");
                return;
            }

            Guard.Build(typeof(ClutterSystem).Assembly, ExpectedBuilds.Valheim1021Client);
            Guard.Build(hearth, HearthBuild);
            Guard.Method(Guard.Type(hearth, "HearthBelow.VoxelMagic.VoxelZone"), "Swap", typeof(void));

            Add(steadyFrame, ExpectedBuilds.SteadyFrame131);
            Add(farLands, ExpectedBuilds.FarLands141);
            if (vcp != null)
            {
                Guard.Build(vcp, VcpBuild);
                MethodInfo target = Guard.Method(Guard.Type(vcp, "ValheimCommunityPatch.GpuGrassCompat"), "get_BuildsGrass", typeof(bool));
                hooks.Add((target, gameBuildHook));
            }

            if (hooks.Any(hook => !hook.Target.IsStatic)) throw new NotSupportedException("GPU grass provider methods must be static.");

            var harmony = new Harmony(Owner);
            foreach (var hook in hooks) harmony.Patch(hook.Target, prefix: new HarmonyMethod(hook.Hook));
            pending = true;
        }

        private static void Add(Assembly? assembly, string build)
        {
            if (assembly == null) return;

            Guard.Build(assembly, build);
            MethodInfo target = Guard.Method(Guard.Type(assembly, "ValheimMods.Shared.GpuGrass"), "ForeignBuild", typeof(bool), typeof(ClutterSystem));
            hooks.Add((target, colliderGroundHook));
        }

        internal static void Verify()
        {
            if (!pending) return;

            pending = false;
            if (hooks.Any(hook => Harmony.GetPatchInfo(hook.Target)?.Prefixes.Count(p => p.owner == Owner && p.PatchMethod == hook.Hook) != 1))
            {
                new Harmony(Owner).UnpatchSelf();
                CompatibilityInstaller.Error($"{Name}: NOT APPLIED; provider hooks did not register exactly once; vendor behavior retained.");
                return;
            }

            CompatibilityInstaller.Info($"{Name}: APPLIED; grass uses the ground collider with HearthBelow, retains GPU drawing, and keeps VCP's game-build hooks when installed.");
        }

        // Both elected helpers already yield construction through this gate and rebuild old patches.
        // HearthBelow changes the collider without changing the worker's copied heightmap.
        private static bool ColliderGround(ref bool __result)
        {
            __result = true;
            return false;
        }

        // VCP asks who constructs grass, not who draws it. Its memory hooks still serve game construction.
        private static bool GameBuild(ref bool __result)
        {
            __result = false;
            return false;
        }
    }
}
