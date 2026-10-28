using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace DeepNorthCompat
{
    internal static class TeleportEverythingPatch
    {
        internal const string Guid = "odinplus.TeleportEverything";
        internal const string Owner = Plugin.Guid + ".UI.TeleportEverything";
        private static MethodInfo blockInput = null!;
        private static MethodInfo target = null!;
        private static bool pending;

        internal static void Prepare(Assembly? assembly)
        {
            if (assembly == null) { CompatibilityInstaller.Info("UI.TeleportEverything: optional mod absent; inactive."); return; }
            if (Guard.KnownServerBuild()) { CompatibilityInstaller.Info("UI.TeleportEverything: client-only; inactive on dedicated server."); return; }
            Guard.Build(assembly, ExpectedBuilds.TeleportEverything);
            Guard.Build(typeof(Player).Assembly,
                "25A0A107DCE4D834C44C2B72D0EAFD5CB7793933BDA81816ACCFA1EA9543DACE",
                ExpectedBuilds.Valheim1020Client, GameBuilds.Valheim1021Client);
            blockInput = Guard.Method(Guard.Type(assembly, "TeleportEverything.Patches.CharacterPatches"),
                "Postfix", typeof(void), typeof(Character), typeof(bool).MakeByRefType());
            target = Guard.Method(typeof(Player), "TakeInput", typeof(bool));
            // Upstream patches Character.TakeInput, which Player overrides without calling it.
            new Harmony(Owner).Patch(target, postfix: new HarmonyMethod(blockInput) { priority = Priority.Last });
            Guard.Registered(target);
            pending = true;
            CompatibilityInstaller.Info($"UI.TeleportEverything: {assembly.GetName().Version.ToString(3)} build verified; awaiting Player input hook verification.");
        }

        internal static void Verify()
        {
            if (!pending) return;
            pending = false;
            if (Harmony.GetPatchInfo(target)?.Postfixes.Count(p => p.owner == Owner
                && p.PatchMethod == blockInput) != 1)
            {
                new Harmony(Owner).UnpatchSelf();
                CompatibilityInstaller.Error("UI.TeleportEverything: NOT APPLIED; Player input hook registration did not match; vendor behavior retained.");
                return;
            }
            CompatibilityInstaller.Info("UI.TeleportEverything: APPLIED; the configuration panel blocks Player gameplay input.");
        }
    }
}
