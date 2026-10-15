using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace DeepNorthCompat
{
    internal static class TeleportEverythingPatch
    {
        internal const string Guid = "odinplus.TeleportEverything";
        internal const string Owner = Plugin.Guid + ".UI.TeleportEverything";
        private static Func<bool> panelOpen = null!;
        private static MethodInfo target = null!;
        private static bool pending;

        internal static void Prepare(Assembly? assembly)
        {
            if (assembly == null) { CompatibilityInstaller.Info("UI.TeleportEverything: optional mod absent; inactive."); return; }
            if (Guard.KnownServerBuild()) { CompatibilityInstaller.Info("UI.TeleportEverything: client-only; inactive on dedicated server."); return; }
            Guard.Build(assembly, ExpectedBuilds.TeleportEverything);
            Guard.Build(typeof(Player).Assembly,
                "25A0A107DCE4D834C44C2B72D0EAFD5CB7793933BDA81816ACCFA1EA9543DACE",
                "94998885B103D6E5F5480123EAEE0A093E82AF2DDEEEE37536C15BF555F24F3A");
            panelOpen = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>),
                Guard.Method(Guard.Type(assembly, "TeleportEverything.ConfigurationUi.ConfigurationPanel"), "get_IsOpen", typeof(bool)));
            target = Guard.Method(typeof(Player), "TakeInput", typeof(bool));
            // Upstream patches Character.TakeInput, which Player overrides without calling it.
            new Harmony(Owner).Patch(target, postfix: Guard.Hook(typeof(TeleportEverythingPatch), nameof(BlockInput), Priority.Last));
            Guard.Registered(target);
            pending = true;
            CompatibilityInstaller.Info("UI.TeleportEverything: 3.0.9 build verified; awaiting Player input hook verification.");
        }

        internal static void Verify()
        {
            if (!pending) return;
            pending = false;
            if (Harmony.GetPatchInfo(target)?.Postfixes.Count(p => p.owner == Owner
                && p.PatchMethod == AccessTools.Method(typeof(TeleportEverythingPatch), nameof(BlockInput))) != 1)
            {
                new Harmony(Owner).UnpatchSelf();
                CompatibilityInstaller.Error("UI.TeleportEverything: NOT APPLIED; Player input hook registration did not match; vendor behavior retained.");
                return;
            }
            CompatibilityInstaller.Info("UI.TeleportEverything: APPLIED; the configuration panel blocks Player gameplay input.");
        }

        private static void BlockInput(Player __instance, ref bool __result)
        {
            if (__result && __instance == Player.m_localPlayer && panelOpen()) __result = false;
        }
    }
}
