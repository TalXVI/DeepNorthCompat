using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace DeepNorthCompat
{
    internal static class NoSmokeStayLitPatch
    {
        internal const string Guid = "TastyChickenLegs.NoSmokeStayLit";
        internal const string Owner = Plugin.Guid + ".NoSmokeStayLit.Controls";
        private static readonly AccessTools.FieldRef<Fireplace, ZNetView> netView =
            AccessTools.FieldRefAccess<Fireplace, ZNetView>("m_nview");
        private static MethodInfo target = null!;
        private static bool pending;

        internal static void Prepare(Assembly? assembly)
        {
            if (assembly == null) { CompatibilityInstaller.Info("NoSmokeStayLit.Controls: optional mod absent; inactive."); return; }
            if (Guard.KnownServerBuild()) { CompatibilityInstaller.Info("NoSmokeStayLit.Controls: client-only; inactive on dedicated server."); return; }

            Guard.Build(assembly, "933361CCDE2BD3BC74CA940F87F498DC8E2B48208748EDF6DEF1AF10384CFD00");
            Guard.Build(typeof(Fireplace).Assembly, ExpectedBuilds.Valheim1020Client);
            target = Guard.Method(Guard.Type(assembly, "NoSmokeStayLit.TastyUtils"), "ToggleFireplaceMode", typeof(void), typeof(Fireplace));
            new Harmony(Owner).Patch(target, prefix: Guard.Hook(typeof(NoSmokeStayLitPatch), nameof(Claim)));
            Guard.Registered(target);
            pending = true;
        }

        internal static void Verify()
        {
            if (!pending) return;
            pending = false;

            if (Harmony.GetPatchInfo(target)?.Prefixes.Count(p => p.owner == Owner
                && p.PatchMethod == AccessTools.DeclaredMethod(typeof(NoSmokeStayLitPatch), nameof(Claim))) != 1)
            {
                new Harmony(Owner).UnpatchSelf();
                CompatibilityInstaller.Error("NoSmokeStayLit.Controls: NOT APPLIED; fireplace control hook registration did not match.");
                return;
            }

            CompatibilityInstaller.Info("NoSmokeStayLit.Controls: APPLIED; fireplace mode controls claim ownership before the vendor writes.");
        }

        private static void Claim(Fireplace __0)
        {
            ZNetView view = netView(__0);
            // Match the vendor's smelter control. ClaimOwnership is synchronous and skips an existing owner.
            if (view != null && view.IsValid()) view.ClaimOwnership();
        }
    }
}
