using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace DeepNorthCompat
{
    internal static class VpoAnimationPatch
    {
        internal const string VpoGuid = "dev.ontrigger.vpo";
        internal const string ZenGuid = "ZenDragon.Zen.ModLib";
        private const string Owner = Plugin.Guid + ".VPO.Animation";
        private const string Zen11422 = "73703E097014D2488A893499714DE0B53775FE357BA3B421BFF6DCCD691438E7";
        private const string Zen11423 = "D57B327BC5A8A167ED62FD43D90FF904EEE90FB9072B8D96A60BACAAC847A30D";
        private static readonly MethodInfo order = AccessTools.DeclaredMethod(typeof(VpoAnimationPatch), nameof(Order));
        private static MethodInfo target = null!, vpoPrefix = null!, zenPrefix = null!;
        private static bool pending;

        internal static void Prepare(Assembly? vpo, Assembly? zen)
        {
            if (pending || Harmony.HasAnyPatches(Owner)) return;
            if (vpo == null || zen == null) { CompatibilityInstaller.Info("VPO.Animation: optional mod absent; inactive."); return; }
            if (Guard.KnownServerBuild()) { CompatibilityInstaller.Info("VPO.Animation: client-only; inactive on dedicated server."); return; }

            Guard.Build(vpo, ExpectedBuilds.Vpo);
            if (Guard.Sha256(vpo.Location) == ExpectedBuilds.Vpo123)
            { CompatibilityInstaller.Info("VPO.Animation: VPO 1.2.3 has no animation cache; inactive."); return; }

            Guard.Build(zen, Zen11422, Zen11423);
            Guard.Build(typeof(ZSyncAnimation).Assembly, ExpectedBuilds.Valheim1021Client);
            target = Guard.Method(typeof(ZSyncAnimation), "SetBool", typeof(void), typeof(int), typeof(bool));
            vpoPrefix = Guard.Method(Guard.Type(vpo, "ValheimPerformanceOptimizations.Patches.AnimatorParameterCachePatch"),
                "SetBoolPrefix", typeof(bool), typeof(ZSyncAnimation), typeof(int), typeof(bool));
            zenPrefix = Guard.Method(Guard.Type(zen, "Zen.FixVanilla.FixExploitAttackAnim"),
                "ZSyncAnimation_SetBool", typeof(void), typeof(ZSyncAnimation), typeof(int), typeof(bool).MakeByRefType());
            pending = true;
        }

        internal static void Verify()
        {
            if (!pending) return;
            pending = false;
            var harmony = new Harmony(Owner);
            try
            {
                Guard.Sole(target, vpoPrefix, VpoGuid, HarmonyPatchType.Prefix);
                Guard.Sole(target, zenPrefix, ZenGuid, HarmonyPatchType.Prefix);
                harmony.Patch(target, prefix: new HarmonyMethod(order) { after = new[] { ZenGuid }, before = new[] { VpoGuid } });
                Patches patches = Harmony.GetPatchInfo(target);
                if (patches.Prefixes.Count(p => p.owner == Owner && p.PatchMethod == order) != 1)
                    throw new NotSupportedException("The animation ordering prefix is not registered exactly once.");
                List<MethodInfo> sorted = PatchProcessor.GetSortedPatchMethods(target, patches.Prefixes.ToArray());
                if (sorted.IndexOf(zenPrefix) < 0 || sorted.IndexOf(vpoPrefix) < 0 || sorted.IndexOf(zenPrefix) >= sorted.IndexOf(vpoPrefix))
                    throw new NotSupportedException("Harmony did not place Zen's animation change before the VPO cache.");
                CompatibilityInstaller.Info("VPO.Animation: APPLIED; animation changes reach the VPO cache in order.");
            }
            catch (Exception exception)
            {
                harmony.UnpatchSelf();
                CompatibilityInstaller.Error($"VPO.Animation: NOT APPLIED; vendor hooks retained. {exception}");
            }
        }

        // This prefix only creates an ordering edge; both vendors keep their hooks and metadata.
        private static void Order() { }
    }
}
