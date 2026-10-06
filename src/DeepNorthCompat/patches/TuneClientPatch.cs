using System;
using System.Linq;
using System.Reflection;
using BepInEx;
using HarmonyLib;

namespace DeepNorthCompat
{
    internal static class TuneClientPatch
    {
        private static Assembly? pending;
        private const string Guid = "akoozie.valheimtune";
        internal static void Prepare(Assembly? assembly)
        {
            pending = null;
            if (assembly == null) { CompatibilityInstaller.Info("Tune.Client: optional mod absent; inactive."); return; }
            // 0.7.9 differs from 0.7.8 only in version strings and its known-good game list.
            Guard.Build(assembly, "8043CD81398FF33CA29FBC75A1D4BDD20BBE950E6BB67D8BF66668C0404A9515",
                "8E00C71476806AB8ED6E344AA639CB08D5D9EB4E90D3A7C77E4DE2F0B67C5B9A");
            pending = assembly;
        }

        internal static void Verify()
        {
            Assembly? assembly = pending; pending = null;
            if (assembly == null) return;
            try
            {
                if (Guard.KnownServerBuild())
                {
                    CompatibilityInstaller.Info("Tune.Client: dedicated build; server patches retained.");
                    return;
                }
                Guard.Build(typeof(ZNet).Assembly, ExpectedBuilds.Valheim);
                Type type = Guard.Type(assembly, "ValheimTune.Plugin");
                var plugin = AccessTools.Field(type, "Instance").GetValue(null) as BaseUnityPlugin;
                if (plugin == null) throw new InvalidOperationException("ValheimTune instance missing after chainloading.");
                // Disable Update too: an Awake-only early return would leave unbound config accesses.
                plugin.enabled = false;
                new Harmony(Guid).UnpatchSelf();
                if (Harmony.GetAllPatchedMethods().Any(m => Harmony.GetPatchInfo(m)!.Owners.Contains(Guid)))
                    throw new InvalidOperationException("ValheimTune client hooks remain installed.");
                CompatibilityInstaller.Info("Tune.Client: APPLIED; server-only Tune component and its client hooks disabled.");
            }
            catch (Exception error)
            {
                CompatibilityInstaller.Error("Tune.Client: NOT APPLIED; required launch gate failed. " + error);
            }
        }
    }
}
