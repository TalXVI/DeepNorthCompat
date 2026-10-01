using System;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;

namespace DeepNorthCompat
{
    [BepInPlugin(Guid, "DeepNorthCompat", "1.0.2")]
    [BepInDependency("MidnightsFX.ImpactfulSkills", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("Azumatt.AzuCraftyBoxes", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("Azumatt.AzuAntiArthriticCrafting", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("marlthon.SeaAnimals", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("marlthon.AirAnimals", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "DeepNorthCompat";

        private void Awake()
        {
            Assembly? Resolve(string guid)
            {
                return Chainloader.PluginInfos.TryGetValue(guid, out PluginInfo info)
                    ? info.Instance?.GetType().Assembly : null;
            }
            CompatibilityInstaller.Install(Resolve, message => Logger.LogInfo(message),
                message => Logger.LogWarning(message), message => Logger.LogError(message));
        }

        // Runs after BepInEx finishes loading plugins, when deferred Harmony patches are applied.
        private void Start()
        {
            CompatibilityInstaller.Verify();
        }
    }
}
