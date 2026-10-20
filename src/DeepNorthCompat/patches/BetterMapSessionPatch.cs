using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace DeepNorthCompat
{
    internal static class BetterMapSessionPatch
    {
        internal const string Owner = Plugin.Guid + ".Map.Session";
        private static BetterMapHooks? hooks;
        private static MethodInfo logout = null!, flush = null!, forgetPins = null!, forgetVehicles = null!;
        private static readonly List<Action> resets = new List<Action>();
        private static readonly AccessTools.FieldRef<Game, bool> shuttingDown = AccessTools.FieldRefAccess<Game, bool>("m_shuttingDown");
        private static Player? pinPlayer, vehiclePlayer;
        private static bool pending, enabled;

        internal static void Prepare(Assembly? assembly)
        {
            if (pending || enabled) return;
            BetterMapAccess? api = BetterMapAccess.Prepare(assembly, "Map.Session", false);
            if (api == null) return;
            resets.Clear();
            flush = api.Method("Pins.PinRecord", "Flush", typeof(void));
            forgetPins = api.Method("Pins.PinRecord", "Forget", typeof(void));
            forgetVehicles = api.Method("Vehicles.VehicleMemory", "Forget", typeof(void));
            foreach (string type in new[] { "Vehicles.VehicleTracker", "Creatures.CreatureTracker" })
            {
                MethodInfo clear = api.Method(type, "Clear", typeof(void));
                resets.Add(() => clear.Invoke(null, null));
            }
            MethodInfo rpc = api.Method("Vehicles.VehicleRpc", "Reset", typeof(void));
            resets.Add(() => rpc.Invoke(null, null));
            resets.Add(() => forgetPins.Invoke(null, null));
            resets.Add(() => forgetVehicles.Invoke(null, null));
            Clear(api, "Vehicles.VehicleTracker", "_gone");
            Set(api, "Vehicles.VehicleTracker", "_named", false);
            Clear(api, "Creatures.CreatureTracker", "_leaving", "_characters");
            Set(api, "Creatures.CreatureTracker", "_nextRefresh", 0f);
            Clear(api, "Vehicles.VehicleIndex", "_records", "_pending", "_found", "_matched");
            Set(api, "Vehicles.VehicleIndex", "_kindIndex", 0); Set(api, "Vehicles.VehicleIndex", "_sectorIndex", 0);
            Set(api, "Vehicles.VehicleIndex", "_scanning", false); Set(api, "Vehicles.VehicleIndex", "_scannedAt", float.NegativeInfinity);
            Clear(api, "Pins.AutoPins", "_holds");
            // _forgotten stays set: "Forget Pinned Places" wipes the records once for each game launch.
            Set(api, "Pins.AutoPins", "_nextSweep", 0f);
            Set(api, "Pins.AutoPins", "_proxyHash", 0);
            foreach (string name in new[] { "_byPrefab", "_byLocation", "_byComponent", "_componentTypes" }) Set(api, "Pins.AutoPins", name, null);
            Set(api, "Pins.PinNames", "_categories", null);
            Set(api, "Pins.PortalPins", "_prefabs", null); Set(api, "Pins.PortalPins", "_typeFound", false);
            Set(api, "Pins.PortalPins", "_type", Minimap.PinType.Icon4);
            Set(api, "Vehicles.VehicleKinds", "_all", null); Set(api, "Vehicles.VehicleKinds", "_byHash", null);
            Clear(api, "Pins.PinLegend", "_types", "_categories");
            Set(api, "Pins.PinLegend", "<CreatureType>k__BackingField", Minimap.PinType.Icon3);
            Set(api, "Pins.PinLegend", "<VehicleType>k__BackingField", Minimap.PinType.Icon0);
            foreach (string name in new[] { "_builtInto", "_panel", "_extras", "_area" }) Set(api, "Pins.PinLegend", name, null);
            Set(api, "Pins.PinFilters", "_restoredOnto", null);
            Set(api, "Creatures.CreatureStars", "_gaveUp", false);
            MethodInfo deaths = api.Method("Map.DeathMarkers", "Invalidate", typeof(void));
            resets.Add(() => deaths.Invoke(null, null));
            foreach (string name in new[] { "Large", "Small" })
            {
                FieldInfo zoom = api.Field("Map.MapZoom", name);
                FieldInfo ready = BetterMapAccess.RequiredField(zoom.FieldType, "Ready", false, typeof(bool));
                resets.Add(() => ready.SetValue(zoom.GetValue(null), false));
            }
            Set(api, "Map.MapZoom", "_input", false);
            logout = api.Method("Vehicles.VehiclePatches", "Game_Logout_Postfix", typeof(void));
            hooks = new BetterMapHooks(Owner);
            Add(logout, nameof(LogoutRequest), HarmonyPatchType.Prefix);
            Add(api.Method("Pins.PinRecord", "Load", typeof(void)), nameof(BeforePinLoad), HarmonyPatchType.Prefix);
            Add(api.Method("Vehicles.VehicleMemory", "Load", typeof(void)), nameof(BeforeVehicleLoad), HarmonyPatchType.Prefix);
            Add(flush, nameof(CanFlush), HarmonyPatchType.Prefix);
            Add(Guard.Method(typeof(PlayerProfile), "SavePlayerData", typeof(void), typeof(Player)), nameof(BeforeSave), HarmonyPatchType.Prefix);
            Add(Guard.Method(typeof(Game), "Shutdown", typeof(void), typeof(bool)), nameof(AfterShutdown), HarmonyPatchType.Postfix);
            ConstructorInfo constructor = AccessTools.Constructor(typeof(ZDOMan), new[] { typeof(int) }) ?? throw new MissingMethodException("ZDOMan", ".ctor");
            Add(constructor, nameof(BeforeSession), HarmonyPatchType.Prefix);
            pending = true;
        }
        private static void Add(MethodBase target, string name, HarmonyPatchType kind) => hooks!.Add(target, typeof(BetterMapSessionPatch), name, kind);
        private static void Set(BetterMapAccess api, string type, string name, object? value)
        {
            FieldInfo field = api.Field(type, name);
            if (value != null && field.FieldType != value.GetType()) throw new NotSupportedException(type + "." + name + " changed.");
            resets.Add(() => field.SetValue(null, value));
        }
        private static void Clear(BetterMapAccess api, string type, params string[] names)
        {
            foreach (string name in names)
            {
                FieldInfo field = api.Field(type, name);
                if (!typeof(IDictionary).IsAssignableFrom(field.FieldType) && !typeof(IList).IsAssignableFrom(field.FieldType))
                    throw new NotSupportedException(type + "." + name + " is no longer a collection.");
                resets.Add(() => { object value = field.GetValue(null); if (value is IDictionary dictionary) dictionary.Clear(); else ((IList)value).Clear(); });
            }
        }
        internal static void Verify()
        {
            if (!pending) return;
            pending = false;
            enabled = hooks!.Complete("Map.Session", BetterMapHooks.Vendor(
                Guard.Method(typeof(Game), "Logout", typeof(void), typeof(bool), typeof(bool)), logout, HarmonyPatchType.Postfix),
                "character records flush before save; transient map and vehicle state reset on committed shutdown and new session.");
        }
        private static bool LogoutRequest() => !enabled;
        private static void BeforePinLoad()
        {
            if (!enabled || Player.m_localPlayer == null || ReferenceEquals(pinPlayer, Player.m_localPlayer)) return;
            forgetPins.Invoke(null, null); pinPlayer = Player.m_localPlayer;
        }
        private static void BeforeVehicleLoad()
        {
            if (!enabled || Player.m_localPlayer == null || ReferenceEquals(vehiclePlayer, Player.m_localPlayer)) return;
            forgetVehicles.Invoke(null, null); vehiclePlayer = Player.m_localPlayer;
        }
        private static bool CanFlush() => !enabled || ReferenceEquals(pinPlayer, Player.m_localPlayer);
        private static void BeforeSave(Player player)
        {
            if (!enabled || !ReferenceEquals(player, pinPlayer) || !ReferenceEquals(player, Player.m_localPlayer)) return;
            // An exception here would abort the character save.
            try { flush.Invoke(null, null); }
            catch (Exception error)
            {
                CompatibilityInstaller.Error("Map.Session: discovery records not flushed before the character save. " + BetterMapAccess.Cause(error));
            }
        }
        private static void AfterShutdown(Game __instance)
        {
            if (enabled && shuttingDown(__instance)) Reset();
        }
        private static void BeforeSession() { if (enabled) Reset(); }
        private static void Reset()
        {
            foreach (Action reset in resets) reset();
            BetterMapPersistencePatch.ResetSession();
            pinPlayer = vehiclePlayer = null;
        }
    }
}
