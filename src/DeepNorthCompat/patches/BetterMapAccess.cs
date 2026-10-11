using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace DeepNorthCompat
{
    // BetterMap remains optional. Resolve its audited private API after chainloading.
    internal sealed class BetterMapAccess
    {
        internal const string Guid = "xtav1m.BetterMap";
        internal readonly Assembly Assembly;
        internal readonly Type Category, Rule, PluginType;
        private readonly MethodInfo categoryOf, typeOf, hasType, iconFor;
        private readonly Func<Minimap.PinType> portalType;
        private readonly Dictionary<string, FieldInfo> settings = new Dictionary<string, FieldInfo>();

        internal BetterMapAccess(Assembly assembly)
        {
            Assembly = assembly;
            Category = Type("Pins.PinCategory"); Rule = Type("Pins.PinRules+Rule");
            PluginType = Guard.Type(assembly, "BetterMap.Plugin");
            categoryOf = Method("Pins.PinLegend", "CategoryOf", typeof(bool), typeof(Minimap.PinType), Category.MakeByRefType());
            typeOf = Method("Pins.PinLegend", "TypeOf", typeof(Minimap.PinType), Category);
            hasType = Method("Pins.PinLegend", "HasType", typeof(bool), Category);
            iconFor = Method("Icons", "For", typeof(Sprite), Category);
            portalType = (Func<Minimap.PinType>)Delegate.CreateDelegate(typeof(Func<Minimap.PinType>),
                Method("Pins.PortalPins", "get_PinType", typeof(Minimap.PinType)));
        }

        internal static BetterMapAccess? Prepare(Assembly? assembly, string group, bool clientOnly)
        {
            if (assembly == null) { CompatibilityInstaller.Info(group + ": BetterMap absent; inactive."); return null; }
            if (clientOnly && Guard.KnownServerBuild())
            { CompatibilityInstaller.Info(group + ": client-only; inactive on dedicated server."); return null; }
            Guard.Build(assembly, ExpectedBuilds.BetterMap);
            Guard.Build(typeof(Minimap).Assembly, ExpectedBuilds.BetterMapGame);
            return new BetterMapAccess(assembly);
        }

        internal Type Type(string name) => Guard.Type(Assembly, "BetterMap.Scripts." + name);
        internal MethodInfo Method(string type, string name, Type returns, params Type[] arguments) =>
            Guard.Method(Type(type), name, returns, arguments);
        internal FieldInfo Field(string type, string name) => RequiredField(Type(type), name, true);
        internal static FieldInfo RequiredField(Type type, string name, bool isStatic, Type? expected = null)
        {
            FieldInfo field = AccessTools.Field(type, name) ?? throw new MissingFieldException(type.FullName, name);
            if (field.IsStatic != isStatic || expected != null && field.FieldType != expected)
                throw new NotSupportedException(type.FullName + "." + name + " field signature changed.");
            return field;
        }
        internal FieldInfo SettingField<T>(string name)
        {
            if (!settings.TryGetValue(name, out FieldInfo field))
                settings[name] = field = RequiredField(PluginType, name, true, typeof(BepInEx.Configuration.ConfigEntry<T>));
            return field;
        }
        internal bool Setting(string name) => ((BepInEx.Configuration.ConfigEntry<bool>)SettingField<bool>(name).GetValue(null)).Value;
        internal float Number(string name) => ((BepInEx.Configuration.ConfigEntry<float>)SettingField<float>(name).GetValue(null)).Value;
        internal int CategoryFor(Minimap.PinType type)
        {
            object[] arguments = { type, Enum.ToObject(Category, 0) };
            return (bool)categoryOf.Invoke(null, arguments) ? Convert.ToInt32(arguments[1]) : -1;
        }
        internal Minimap.PinType TypeFor(int category) => category == 10 ? PortalType :
            (Minimap.PinType)typeOf.Invoke(null, new[] { Enum.ToObject(Category, category) });
        internal Minimap.PinType? AllocatedTypeFor(int category)
        {
            // Boss altars intentionally use BetterMap's vanilla fallback, without a legend row.
            if (category == 9 || category == 10) return TypeFor(category);
            return (bool)hasType.Invoke(null, new[] { Enum.ToObject(Category, category) }) ? TypeFor(category) : (Minimap.PinType?)null;
        }
        internal Minimap.PinType PortalType => portalType();
        internal Sprite IconFor(int category) => (Sprite)iconFor.Invoke(null, new[] { Enum.ToObject(Category, category) });
        // Reflection wraps a vendor failure; the log needs the vendor's own message.
        internal static string Cause(Exception error) =>
            (error is TargetInvocationException && error.InnerException != null ? error.InnerException : error).Message;
        internal static bool SerializableCategory(int category) => category >= 0 && category <= 10;
        internal static List<Minimap.PinData> Pins(Minimap map) => pins(map);
        internal static bool[] Visible(Minimap map) => visible(map);
        internal static void Redraw(Minimap map) => redraw(map) = true;
        private static readonly AccessTools.FieldRef<Minimap, List<Minimap.PinData>> pins = AccessTools.FieldRefAccess<Minimap, List<Minimap.PinData>>("m_pins");
        private static readonly AccessTools.FieldRef<Minimap, bool[]> visible = AccessTools.FieldRefAccess<Minimap, bool[]>("m_visibleIconTypes");
        private static readonly AccessTools.FieldRef<Minimap, bool> redraw = AccessTools.FieldRefAccess<Minimap, bool>("m_pinUpdateRequired");
    }

    internal sealed class BetterMapHooks
    {
        private readonly string owner;
        private readonly List<(MethodBase Target, MethodInfo Hook, HarmonyPatchType Kind)> hooks =
            new List<(MethodBase, MethodInfo, HarmonyPatchType)>();
        internal BetterMapHooks(string owner) { this.owner = owner; }
        internal void Add(MethodBase target, Type patch, string name, HarmonyPatchType kind, int priority = Priority.Normal)
        {
            var method = Guard.Hook(patch, name, priority);
            hooks.Add((target, method.method, kind));
            new Harmony(owner).Patch(target, prefix: kind == HarmonyPatchType.Prefix ? method : null,
                postfix: kind == HarmonyPatchType.Postfix ? method : null,
                transpiler: kind == HarmonyPatchType.Transpiler ? method : null,
                finalizer: kind == HarmonyPatchType.Finalizer ? method : null, ilmanipulator: null);
        }
        internal bool Installed() => hooks.All(h => Registrations(Harmony.GetPatchInfo(h.Target), h.Kind)
            .Count(p => p.owner == owner && p.PatchMethod == h.Hook) == 1);
        private static IEnumerable<Patch> Registrations(Patches? info, HarmonyPatchType kind) => info == null ? Enumerable.Empty<Patch>() :
            kind == HarmonyPatchType.Prefix ? info.Prefixes : kind == HarmonyPatchType.Postfix ? info.Postfixes :
            kind == HarmonyPatchType.Finalizer ? info.Finalizers : info.Transpilers;
        internal static bool Vendor(MethodBase target, MethodInfo hook, HarmonyPatchType kind) =>
            Registrations(Harmony.GetPatchInfo(target), kind).Count(p => p.owner == BetterMapAccess.Guid && p.PatchMethod == hook) == 1;
        internal bool Complete(string group, bool valid, string description)
        {
            if (valid && Installed()) { CompatibilityInstaller.Info(group + ": APPLIED; " + description); return true; }
            new Harmony(owner).UnpatchSelf();
            CompatibilityInstaller.Error(group + ": NOT APPLIED; audited hooks or IL did not match; vendor behavior retained.");
            return false;
        }
    }
}
