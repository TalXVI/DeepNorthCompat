using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace DeepNorthCompat
{
    internal static class MagicRevampHudPatch
    {
        internal const string Owner = Plugin.Guid + ".MagicRevamp.Hud";
        private const string VendorOwner = MagicRevampInventoryPatch.Guid;
        private static Assembly? pending;
        private static bool applied;

        internal static void Prepare(Assembly? magic)
        {
            if (pending != null || applied) return;
            if (magic == null) { CompatibilityInstaller.Info("MagicRevamp.Hud: optional mod absent; inactive."); return; }
            if (Guard.KnownServerBuild()) { CompatibilityInstaller.Info("MagicRevamp.Hud: client-only; inactive on dedicated server."); return; }

            Guard.Build(magic, ExpectedBuilds.MagicRevamp151);
            Guard.Build(typeof(Hud).Assembly, ExpectedBuilds.Valheim1021Client);
            pending = magic;
            CompatibilityInstaller.Info("MagicRevamp.Hud: audited build verified; awaiting vendor Harmony registrations.");
        }

        internal static void Verify()
        {
            Assembly? magic = pending;
            pending = null;
            if (magic == null) return;

            MethodInfo? target = null;
            Patch? vendor = null;
            try
            {
                target = Guard.Method(typeof(Hud), "UpdateCrosshair", typeof(void), typeof(Player), typeof(float));
                MethodInfo postfix = Guard.Method(Guard.Type(magic, "MagicRevamp.Patches.HudPatch"),
                    "UpdateCrosshair_Postfix", typeof(void), typeof(Hud), typeof(Player));
                vendor = Guard.Sole(target, postfix, VendorOwner, HarmonyPatchType.Postfix);

                new Harmony(Owner).Unpatch(target, postfix);
                if (Harmony.GetPatchInfo(target)?.Postfixes.Any(p => p.PatchMethod == postfix) == true)
                    throw new InvalidOperationException("MagicRevamp crosshair postfix remained registered.");

                applied = true;
                CompatibilityInstaller.Info("MagicRevamp.Hud: APPLIED; the native crosshair retains its own feedback.");
            }
            catch (Exception exception)
            {
                applied = false;
                try
                {
                    if (target != null && vendor != null) Guard.Restore(target, vendor, HarmonyPatchType.Postfix);
                }
                catch (Exception rollback)
                {
                    CompatibilityInstaller.Error($"MagicRevamp.Hud: application and rollback failed; inspect Harmony state. {exception} {rollback}");
                    return;
                }
                CompatibilityInstaller.Error($"MagicRevamp.Hud: NOT APPLIED; vendor hook retained. {exception}");
            }
        }

        internal static void Reset() { pending = null; applied = false; }
    }
}
