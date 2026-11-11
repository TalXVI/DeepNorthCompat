using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace DeepNorthCompat
{
    internal static class VpoTerrainGrassPatch
    {
        internal const string Name = "VPO.TerrainGrass";
        internal const string Owner = Plugin.Guid + "." + Name;
        private static readonly MethodInfo wait = AccessTools.DeclaredMethod(typeof(VpoTerrainGrassPatch), nameof(WaitForGrassTerrain));
        private static MethodInfo target = null!, collision = null!, collisionPrefix = null!, gameReady = null!, readyPostfix = null!;
        private static Dictionary<Heightmap, bool> finished = null!;
        private static bool pending;

        internal static void Prepare(Assembly? vpo)
        {
            new Harmony(Owner).UnpatchSelf();
            pending = false;

            if (vpo == null) { CompatibilityInstaller.Info($"{Name}: optional mod absent; inactive."); return; }
            if (Guard.KnownServerBuild()) { CompatibilityInstaller.Info($"{Name}: client-only; inactive on dedicated server."); return; }

            Guard.Build(vpo, ExpectedBuilds.Vpo210);
            Guard.Build(typeof(Heightmap).Assembly, ExpectedBuilds.Valheim1021Client);
            Type patches = Guard.Type(vpo, "ValheimPerformanceOptimizations.Patches.HeightmapGeneration.ThreadedHeightmapCollisionBakePatch");
            target = Guard.Method(patches, "IsHeightmapReady", typeof(bool), typeof(Vector3));
            collision = Guard.Method(typeof(Heightmap), "RebuildCollisionMesh", typeof(void));
            collisionPrefix = Guard.Method(patches, "RebuildCollisionMeshPatch", typeof(bool), typeof(Heightmap));
            gameReady = Guard.Method(typeof(ClutterSystem), "IsHeightmapReady", typeof(bool));
            readyPostfix = Guard.Method(patches, "IsHeightmapReadyPatch", typeof(void), typeof(ClutterSystem), typeof(bool).MakeByRefType());
            FieldInfo field = AccessTools.DeclaredField(patches, "HeightmapFinished");
            if (!target.IsStatic || field == null || !field.IsStatic || field.FieldType != typeof(Dictionary<Heightmap, bool>))
                throw new NotSupportedException("VPO's terrain readiness signature changed.");

            finished = (Dictionary<Heightmap, bool>)field.GetValue(null);
            new Harmony(Owner).Patch(target, postfix: new HarmonyMethod(wait));
            pending = true;
        }

        internal static void Verify()
        {
            if (!pending) return;

            pending = false;
            var harmony = new Harmony(Owner);
            if (Harmony.GetPatchInfo(collision)?.Prefixes.Any(p => p.PatchMethod == collisionPrefix) != true)
            {
                harmony.UnpatchSelf();
                CompatibilityInstaller.Info($"{Name}: VPO threaded terrain collision is inactive; inactive.");
                return;
            }

            try
            {
                Guard.Sole(collision, collisionPrefix, VpoAnimationPatch.VpoGuid, HarmonyPatchType.Prefix);
                Guard.Sole(gameReady, readyPostfix, VpoAnimationPatch.VpoGuid, HarmonyPatchType.Postfix);
                Guard.Sole(target, wait, Owner, HarmonyPatchType.Postfix);
                CompatibilityInstaller.Info($"{Name}: APPLIED; grass waits for VPO terrain colliders throughout its range.");
            }
            catch (Exception error)
            {
                harmony.UnpatchSelf();
                CompatibilityInstaller.Error($"{Name}: NOT APPLIED; terrain readiness hooks did not match. {error}");
            }
        }

        // VPO checks only the camera's tile. Wait across the actual grass area so a neighboring
        // bake cannot consume an edit's reset against the previous collider.
        private static void WaitForGrassTerrain(Vector3 __0, ref bool __result)
        {
            if (!__result) return;

            ClutterSystem clutter = ClutterSystem.instance;
            if (clutter == null) return;

            Player player = Player.m_localPlayer;
            Vector3 center = !GameCamera.InFreeFly() && player != null ? player.transform.position : __0;
            foreach (Heightmap map in Heightmap.GetAllHeightmaps())
                if (map.IsPointInside(center, clutter.m_distance) && (!finished.TryGetValue(map, out bool ready) || !ready))
                {
                    __result = false;
                    return;
                }
        }
    }
}
