using System;
using System.Reflection;
using System.IO;
using BepInEx;
using BepInEx.Bootstrap;

namespace DeepNorthCompat
{
    [BepInPlugin(Guid, "DeepNorthCompat", "1.2.3")]
    [BepInDependency("MidnightsFX.ImpactfulSkills", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("Azumatt.AzuCraftyBoxes", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("Azumatt.AzuAntiArthriticCrafting", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("marlthon.SeaAnimals", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("marlthon.AirAnimals", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("Azumatt.AzuExtendedPlayerInventory", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("dev.ontrigger.vpo", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("akoozie.valheimtune", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.maxsch.valheim.MultiUserChest", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("goldenrevolver.quick_stack_store", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("marlthon.OdinShip", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "DeepNorthCompat";
        internal static Plugin Instance = null!;

        private void Awake()
        {
            Instance = this;
            Assembly? Resolve(string guid)
            {
                return Chainloader.PluginInfos.TryGetValue(guid, out PluginInfo info)
                    ? info.Instance?.GetType().Assembly : null;
            }
            CompatibilityInstaller.Info = message => Logger.LogInfo(message);
            CompatibilityInstaller.Warning = message => Logger.LogWarning(message);
            CompatibilityInstaller.Error = message => Logger.LogError(message);
            bool diagnostics = Config.Bind("Diagnostics", "Enabled", true,
                "Record chest, crafting, network and error events, and enable the /dnc_report and /dnc_mark chat commands. Restart after changing.").Value;
            string directory = Config.Bind("Diagnostics", "Directory", "",
                "Empty uses BepInEx/DeepNorthCompat/diagnostics. Otherwise use an absolute directory path. Restart after changing.").Value;
            DiagnosticReport.ServerAccess = Config.Bind("Diagnostics", "Server report access", DiagnosticReport.Access.Admins,
                "Servers only. Who may fetch the server's report with /dnc_report: Nobody, Admins (adminlist.txt) or Everyone.").Value;
            if (diagnostics)
            {
                DiagnosticReport.Listen();
                ChestDiagnostics.Configure(string.IsNullOrWhiteSpace(directory)
                    ? Path.Combine(Paths.BepInExRootPath, "DeepNorthCompat", "diagnostics") : directory,
                    Guard.KnownServerBuild(), 8 * 1024 * 1024);
            }
            CompatibilityInstaller.Install(Resolve, CompatibilityInstaller.Info, CompatibilityInstaller.Warning, CompatibilityInstaller.Error);
            if (ChestDiagnostics.Enabled) DiagnosticReport.RegisterCommands();
        }

        // Runs after BepInEx finishes loading plugins, when deferred Harmony patches are applied.
        private void Start()
        {
            CompatibilityInstaller.Verify();
            if (Chainloader.PluginInfos.ContainsKey("MVP.Valheim_Serverside_Simulations"))
                Logger.LogWarning("Simulation compatibility moved to Serverbound in DeepNorthCompat 1.2.0. The predecessor simulation is loaded without the retired takeover and skill bridge. Stop and follow the coordinated migration guide before using those integrations.");
        }

        private void Update()
        {
            DiagnosticReport.Tick();
            OdinShipPatch.Tick();
            OdinShipFishPatch.Tick();
        }

        private void OnDestroy() => ChestDiagnostics.Disable();
    }
}
