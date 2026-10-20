using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

internal sealed class AaaCraftingFixture : IDisposable
{
    private readonly FieldInfo overrideField;
    private readonly FieldInfo durationField;
    private readonly object previousOverride;
    private readonly object previousDuration;
    private readonly ConfigEntryBase overrideSetting;
    internal readonly ConfigEntry<float> Duration;

    internal AaaCraftingFixture(Assembly assembly)
    {
        Type plugin = assembly.GetType("AzuAntiArthriticCrafting.AzuAntiArthriticCraftingPlugin", true)!;
        InitializePlugin(plugin);
        overrideField = AccessTools.Field(plugin, "OverrideCraftingDuration");
        durationField = AccessTools.Field(plugin, "CraftingDuration");
        previousOverride = overrideField.GetValue(null);
        previousDuration = durationField.GetValue(null);

        var config = new ConfigFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox", "aaa.cfg"), false)
        {
            SaveOnConfigSet = false
        };
        Type toggle = plugin.GetNestedType("Toggle")!;
        MethodInfo bind = typeof(ConfigFile).GetMethods().Single(method => method.Name == "Bind" && method.IsGenericMethodDefinition
            && method.GetParameters().Length == 3 && method.GetParameters()[0].ParameterType == typeof(ConfigDefinition));
        overrideSetting = (ConfigEntryBase)bind.MakeGenericMethod(toggle).Invoke(config,
            new object[] { new ConfigDefinition("fixture", "Override"), Enum.ToObject(toggle, 1), new ConfigDescription("") });
        Duration = config.Bind("fixture", "Duration", 0.25f);
        overrideField.SetValue(null, overrideSetting);
        durationField.SetValue(null, Duration);
    }

    internal void Set(bool enabled, float seconds = 0.25f)
    {
        overrideSetting.BoxedValue = Enum.ToObject(overrideSetting.SettingType, enabled ? 1 : 0);
        Duration.Value = seconds;
    }

    public void Dispose()
    {
        overrideField.SetValue(null, previousOverride);
        durationField.SetValue(null, previousDuration);
    }

    private static void InitializePlugin(Type plugin)
    {
        // ServerSync schedules Unity work in its static initializer. This host has no scene or main loop.
        FieldInfo instance = AccessTools.Field(typeof(ThreadingHelper), "<Instance>k__BackingField");
        object previous = instance.GetValue(null);
        var harmony = new Harmony("DeepNorthCompat.Tests.AaaInitialization");
        harmony.Patch(AccessTools.Method(typeof(ThreadingHelper), "StartSyncInvoke"),
            prefix: new HarmonyMethod(AccessTools.Method(typeof(AaaCraftingFixture), nameof(SkipUnityWork))));
        try
        {
            instance.SetValue(null, FormatterServices.GetUninitializedObject(typeof(ThreadingHelper)));
            RuntimeHelpers.RunClassConstructor(plugin.TypeHandle);
        }
        finally
        {
            harmony.UnpatchSelf();
            instance.SetValue(null, previous);
        }
    }

    private static bool SkipUnityWork() => false;
}
