using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace DeepNorthCompat
{
    internal static class BetterMapPortalPatch
    {
        internal const string Owner = Plugin.Guid + ".Map.Portals";
        private static BetterMapAccess api = null!;
        private static BetterMapHooks? hooks;
        private static Func<ZDO, string> tag = null!;
        private static Func<string, string> label = null!;
        private static Func<int, bool> isPortal = null!;
        private static MethodInfo mapPins = null!;
        private static bool destructionMatched;
        private static bool pending, enabled;
        internal static void Prepare(Assembly? assembly)
        {
            if (pending || enabled) return;
            BetterMapAccess? found = BetterMapAccess.Prepare(assembly, "Map.Portals", true);
            if (found == null) return;
            api = found;
            tag = (Func<ZDO, string>)Delegate.CreateDelegate(typeof(Func<ZDO, string>), api.Method("Pins.PortalPins", "TagOf", typeof(string), typeof(ZDO)));
            label = (Func<string, string>)Delegate.CreateDelegate(typeof(Func<string, string>), api.Method("Pins.PortalPins", "Label", typeof(string), typeof(string)));
            isPortal = (Func<int, bool>)Delegate.CreateDelegate(typeof(Func<int, bool>), api.Method("Pins.PortalPins", "IsPortal", typeof(bool), typeof(int)));
            mapPins = api.Method("MapPins", "Of", typeof(List<Minimap.PinData>), typeof(Minimap));
            api.SettingField<bool>("autoPinPortals");
            destructionMatched = false;
            hooks = new BetterMapHooks(Owner);
            hooks.Add(api.Method("Pins.NamedPins", "Retag", typeof(void), typeof(Vector3), typeof(string)), typeof(BetterMapPortalPatch), nameof(RetagRequest), HarmonyPatchType.Prefix);
            hooks.Add(api.Method("Pins.AutoPins", "Portal", typeof(void), typeof(ZDO), typeof(Vector3), typeof(float), typeof(float)),
                typeof(BetterMapPortalPatch), nameof(Refresh), HarmonyPatchType.Prefix);
            MethodInfo destroyed = api.Method("Pins.PortalPins", "Destroyed", typeof(void), typeof(ZDO));
            hooks.Add(destroyed, typeof(BetterMapPortalPatch), nameof(DestructionSelection), HarmonyPatchType.Transpiler);
            hooks.Add(destroyed, typeof(BetterMapPortalPatch), nameof(BeforeDestruction), HarmonyPatchType.Prefix);
            pending = true;
        }
        internal static void Verify()
        {
            if (!pending) return;
            pending = false;
            enabled = hooks!.Complete("Map.Portals", BetterMapPersistencePatch.Enabled && destructionMatched,
                "identified automatic portal pins follow accepted tags and destruction without changing manual pins or shared metadata.");
        }
        private static bool RetagRequest() => !enabled;
        private static IEnumerable<CodeInstruction> DestructionSelection(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            destructionMatched = code.Count(i => i.Calls(mapPins)) == 1;
            if (!destructionMatched) return code;
            MethodInfo filter = AccessTools.Method(typeof(BetterMapPortalPatch), nameof(AutomaticPins));
            return code.Select(i => i.Calls(mapPins) ? new CodeInstruction(i) { opcode = OpCodes.Call, operand = filter } : i).ToArray();
        }
        private static List<Minimap.PinData> AutomaticPins(Minimap map) => enabled
            ? BetterMapAccess.Pins(map).Where(BetterMapPersistencePatch.AutomaticPortal).ToList() : BetterMapAccess.Pins(map);
        // The vendor removes a portal pin only after removing its discovery record, and a shared
        // automatic pin has none until its portal is destroyed.
        private static void BeforeDestruction(ZDO zdo)
        {
            Minimap map = Minimap.instance;
            if (!enabled || map == null || zdo == null || !isPortal(zdo.GetPrefab())) return;
            Vector3 position = zdo.GetPosition(); Minimap.PinType type = api.PortalType;
            foreach (Minimap.PinData pin in BetterMapAccess.Pins(map))
                if (pin.m_type == type && (pin.m_pos - position).sqrMagnitude <= 4f && BetterMapPersistencePatch.AutomaticPortal(pin))
                { BetterMapPersistencePatch.Record(10, position); return; }
        }
        private static void Refresh(ZDO zdo, Vector3 origin, float rangeSqr)
        {
            Minimap map = Minimap.instance;
            if (!enabled || map == null || zdo == null || !api.Setting("autoPinPortals")) return;
            Vector3 position = zdo.GetPosition();
            if ((position - origin).sqrMagnitude > rangeSqr) return;
            string current = label(tag(zdo));
            foreach (Minimap.PinData pin in BetterMapAccess.Pins(map))
                if (BetterMapPersistencePatch.AutomaticPortal(pin) && (pin.m_pos - position).sqrMagnitude <= .01f)
                    BetterMapPersistencePatch.Rename(map, pin, current);
        }
    }
}
