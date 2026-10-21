using System;
using System.Reflection;
using System.IO;
using BepInEx;
using BepInEx.Bootstrap;
using ConditionalConfigSync;

namespace DeepNorthCompat
{
    [BepInPlugin(Guid, "DeepNorthCompat", "1.4.1")]
    [BepInDependency("_shudnal.ConditionalConfigSync", "1.0.11")]
    [BepInDependency("MidnightsFX.ImpactfulSkills", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("Azumatt.AzuCraftyBoxes", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(ForgeCraftingPatch.Guid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("marlthon.SeaAnimals", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("marlthon.AirAnimals", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("Azumatt.AzuExtendedPlayerInventory", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("dev.ontrigger.vpo", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("akoozie.valheimtune", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.maxsch.valheim.MultiUserChest", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("goldenrevolver.quick_stack_store", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("marlthon.OdinShip", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("xtav1m.BetterMap", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("advize.CartographySkill", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(HearthTerrainPatch.Guid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(HearthTerrainPatch.RuneGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(TeleportEverythingPatch.Guid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(MyLittleUiAmmoPatch.Guid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PlantEasilyCostPatch.Guid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PlantEverythingDustPatch.Guid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PlantEverythingDustPatch.CultivatorDust, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PlantEverythingDustPatch.BuildDust, BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "DeepNorthCompat";
        internal static Plugin Instance = null!;
        private ConfigSync sync = null!;

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
            sync = CreateSync(Info.Metadata.Version.ToString());
            ForgeCraftingPatch.BindConfig(Config, sync);
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

        internal static ConfigSync CreateSync(string version)
        {
            return new ConfigSync(Guid)
            {
                DisplayName = "DeepNorthCompat",
                CurrentVersion = version,
                MinimumRequiredVersion = version,
                ModRequired = true
            };
        }

        // Runs after BepInEx finishes loading plugins, when deferred Harmony patches are applied.
        private void Start()
        {
            CompatibilityInstaller.Verify();
            DiagnosticReport.Prewarm();
            if (Chainloader.PluginInfos.ContainsKey("MVP.Valheim_Serverside_Simulations"))
                Logger.LogWarning("Simulation compatibility moved to Serverbound in DeepNorthCompat 1.2.0. The predecessor simulation is loaded without the retired takeover and skill bridge. Stop and follow the coordinated migration guide before using those integrations.");
        }

        private void Update()
        {
            DiagnosticReport.Tick();
            OdinShipPatch.Tick();
            OdinShipFishPatch.Tick();
            BetterMapPersistencePatch.Tick();
        }

        private void OnDestroy() => ChestDiagnostics.Disable();
    }
}
