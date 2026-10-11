using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace DeepNorthCompat
{
    internal static class BetterMapPersistencePatch
    {
        internal const string Owner = Plugin.Guid + ".Map.Persistence";
        private static BetterMapAccess api = null!;
        private static BetterMapHooks? hooks;
        private static MethodInfo vendorRestore = null!;
        private static FieldInfo ruleCategory = null!;
        private static MethodInfo recordHas = null!, recordAdd = null!;
        private static Action<Minimap, Minimap.PinData> destroyMarker = null!;
        private static Action<Minimap> refreshFilters = null!;
        private static ConditionalWeakTable<Minimap.PinData, PinIdentity> identities = new ConditionalWeakTable<Minimap.PinData, PinIdentity>();
        private static Minimap? namesMap;
        private static Dictionary<string, HashSet<int>> names = new Dictionary<string, HashSet<int>>();
        private static bool pending, enabled;
        // Tick revisits the pins only after a load or placement, or when one of these changes.
        private static bool unsettled;
        private static Minimap? settledMap;
        private static Player? settledPlayer;
        private static string? settledRecords;
        [ThreadStatic] private static int loading, removing;
        [ThreadStatic] private static int? placing;

        private sealed class PinIdentity
        {
            internal int Category, OriginalType;
            internal bool Automatic, Legacy, PendingDiscovery;
            internal Minimap? ResolvedMap;
            internal string? LegacyRecords;
        }
        private sealed class LoadState
        {
            internal int Depth;
            internal bool Finished;
            internal bool Shared;
            internal BetterMapMapData? Data;
            internal HashSet<Minimap.PinData> Existing = new HashSet<Minimap.PinData>();
        }

        internal static bool Enabled => enabled;
        internal static void ResetSession()
        {
            identities = new ConditionalWeakTable<Minimap.PinData, PinIdentity>();
            namesMap = null; names.Clear(); loading = 0; removing = 0; placing = null;
            unsettled = false; settledMap = null; settledPlayer = null; settledRecords = null;
        }
        internal static void Prepare(Assembly? assembly)
        {
            if (pending || enabled) return;
            BetterMapAccess? found = BetterMapAccess.Prepare(assembly, "Map.Persistence", true);
            if (found == null) return;
            api = found; hooks = new BetterMapHooks(Owner);
            api.SettingField<bool>("autoPinPortals"); api.SettingField<float>("autoPinMergeDistance");
            ruleCategory = BetterMapAccess.RequiredField(api.Rule, "Category", false, api.Category);
            recordHas = api.Method("Pins.PinRecord", "Has", typeof(bool), api.Category, typeof(Vector3), typeof(float));
            recordAdd = api.Method("Pins.PinRecord", "Add", typeof(void), api.Category, typeof(Vector3));
            destroyMarker = (Action<Minimap, Minimap.PinData>)Delegate.CreateDelegate(typeof(Action<Minimap, Minimap.PinData>),
                Guard.Method(typeof(Minimap), "DestroyPinMarker", typeof(void), typeof(Minimap.PinData)));
            refreshFilters = (Action<Minimap>)Delegate.CreateDelegate(typeof(Action<Minimap>), api.Method("Pins.PinFilters", "Refresh", typeof(void), typeof(Minimap)));
            foreach (string name in new[] { "GetMapData", "GetSharedMapData" })
                Add(Guard.Method(typeof(Minimap), name, typeof(byte[]), name == "GetMapData" ? Type.EmptyTypes : new[] { typeof(byte[]) }), nameof(Save), HarmonyPatchType.Postfix, Priority.Last);
            foreach (string name in new[] { "SetMapData", "AddSharedMapData" })
            {
                MethodInfo target = Guard.Method(typeof(Minimap), name, name == "SetMapData" ? typeof(void) : typeof(bool), typeof(byte[]));
                Add(target, nameof(BeginLoad), HarmonyPatchType.Prefix, Priority.First);
                Add(target, nameof(EndLoad), HarmonyPatchType.Postfix, Priority.Last);
                Add(target, nameof(FinishLoad), HarmonyPatchType.Finalizer);
            }
            MethodInfo add = typeof(Minimap).GetMethods().Single(m => m.Name == "AddPin" && m.GetParameters().Length == 7);
            if (add.ReturnType != typeof(Minimap.PinData) || !add.GetParameters().Take(6).Select(p => p.ParameterType)
                .SequenceEqual(new[] { typeof(Vector3), typeof(Minimap.PinType), typeof(string), typeof(bool), typeof(bool), typeof(long) }))
                throw new NotSupportedException("Minimap.AddPin signature changed.");
            Add(add, nameof(BeforeAdd), HarmonyPatchType.Prefix); Add(add, nameof(AfterAdd), HarmonyPatchType.Postfix);
            MethodInfo removeAt = Guard.Method(typeof(Minimap), "RemovePin", typeof(bool), typeof(Vector3), typeof(float));
            Add(removeAt, nameof(BeginRemoval), HarmonyPatchType.Prefix); Add(removeAt, nameof(EndRemoval), HarmonyPatchType.Finalizer);
            Add(Guard.Method(typeof(Minimap), "RemovePin", typeof(void), typeof(Minimap.PinData)), nameof(BeforeRemoval), HarmonyPatchType.Prefix);
            Add(api.Method("Pins.PinFilters", "Store", typeof(void), typeof(Minimap)), nameof(StoreFilter), HarmonyPatchType.Prefix);
            foreach (MethodInfo target in new[] {
                api.Method("Pins.AutoPins", "Place", typeof(void), api.Rule, typeof(ZNetView), typeof(Vector3)),
                api.Method("Pins.AutoPins", "Portal", typeof(void), typeof(ZDO), typeof(Vector3), typeof(float), typeof(float)) })
            {
                // Runs last, so the portal label refresh never depends on this prefix's result.
                Add(target, nameof(BeginPlacement), HarmonyPatchType.Prefix, Priority.Last);
                Add(target, nameof(EndPlacement), HarmonyPatchType.Finalizer);
            }
            vendorRestore = api.Method("Pins.AutoPins", "Restore", typeof(void));
            Add(vendorRestore, nameof(Restore), HarmonyPatchType.Prefix);
            pending = true;
        }
        private static void Add(MethodBase target, string name, HarmonyPatchType kind, int priority = Priority.Normal) =>
            hooks!.Add(target, typeof(BetterMapPersistencePatch), name, kind, priority);
        internal static void Verify()
        {
            if (!pending) return;
            pending = false;
            MethodInfo vendor = api.Method("Map.MapPatches", "Minimap_SetMapData_Postfix", typeof(void));
            enabled = hooks!.Complete("Map.Persistence", BetterMapHooks.Vendor(
                Guard.Method(typeof(Minimap), "SetMapData", typeof(void), typeof(byte[])), vendor, HarmonyPatchType.Postfix),
                "versioned category identity survives local saves and cartography imports; hidden filters and ambiguous legacy pins retained.");
        }
        private static void Save(Minimap __instance, MethodBase __originalMethod, ref byte[] __result)
        {
            if (!enabled) return;
            // An exception here would abort the character save or table write that asked for these bytes.
            try
            {
                bool shared = __originalMethod.Name == "GetSharedMapData";
                var entries = new List<BetterMapMapData.Identity>();
                foreach (Minimap.PinData pin in BetterMapAccess.Pins(__instance))
                    if (pin.m_save && (!shared || pin.m_type != Minimap.PinType.Death)) entries.Add(IdentityOf(pin));
                __result = BetterMapMapData.Append(__result, entries.ToArray());
            }
            catch (Exception error)
            {
                CompatibilityInstaller.Error("Map.Persistence: category trailer not written; native map data retained. " + BetterMapAccess.Cause(error));
            }
        }
        private static BetterMapMapData.Identity IdentityOf(Minimap.PinData pin)
        {
            if (identities.TryGetValue(pin, out PinIdentity stored))
                return new BetterMapMapData.Identity(stored.Category, stored.Automatic, stored.Legacy);
            return new BetterMapMapData.Identity(api.CategoryFor(pin.m_type), false);
        }
        private static void BeginLoad(Minimap __instance, MethodBase __originalMethod, ref byte[] __0, out LoadState? __state)
        {
            __state = null;
            if (!enabled) return;
            var state = new LoadState { Depth = loading, Shared = __originalMethod.Name == "AddSharedMapData",
                Existing = new HashSet<Minimap.PinData>(BetterMapAccess.Pins(__instance)) };
            __state = state; loading++;
            // The native bytes stay untouched on failure. An exception escaping a local load makes
            // vanilla repeat map generation and loading every frame.
            try
            {
                BetterMapMapData data = Read(__0, state.Shared);
                foreach (BetterMapMapData.Row row in data.Rows)
                {
                    bool legacy = row.Identity.Legacy || !data.HasMetadata && LegacyType(row.Type);
                    if (!legacy) continue;
                    row.Identity = TryLegacy(row.Name, row.Position, row.Type, out int category)
                        ? new BetterMapMapData.Identity(category, false) : new BetterMapMapData.Identity(-1, false, true);
                }
                byte[] translated = data.Translate(api.AllocatedTypeFor);
                state.Data = data; __0 = translated;
            }
            catch (Exception error)
            {
                CompatibilityInstaller.Warning("Map.Persistence: map data not read; no category translation. " + BetterMapAccess.Cause(error));
            }
        }
        // A trailer this build cannot read counts as absent, so the legacy rules still apply to its pins.
        private static BetterMapMapData Read(byte[] bytes, bool shared)
        {
            try { return new BetterMapMapData(bytes, shared); }
            catch (Exception error) when (error is InvalidDataException || error is NotSupportedException)
            {
                CompatibilityInstaller.Warning("Map.Persistence: pin metadata or native data rejected; reading without the trailer. " + BetterMapAccess.Cause(error));
                return new BetterMapMapData(bytes, shared, true);
            }
        }
        private static void EndLoad(Minimap __instance, LoadState? __state)
        {
            if (__state == null) return;
            unsettled = true;
            // The native load has finished and must not fail here.
            try { Classify(__instance, __state); refreshFilters(__instance); }
            catch (Exception error)
            {
                CompatibilityInstaller.Error("Map.Persistence: loaded pins not fully classified. " + BetterMapAccess.Cause(error));
            }
        }
        private static void Classify(Minimap map, LoadState state)
        {
            var added = new List<Minimap.PinData>();
            foreach (Minimap.PinData pin in BetterMapAccess.Pins(map)) if (!state.Existing.Contains(pin)) added.Add(pin);
            BetterMapMapData? data = state.Data;
            if (data == null)
            {
                foreach (Minimap.PinData pin in added) SetIdentity(pin, -1, false, (int)pin.m_type, LegacyType((int)pin.m_type));
                return;
            }
            int slots = BetterMapAccess.Visible(map).Length;
            string[] authors = added.Select(pin => pin.m_author.ToString()).ToArray();
            // Local loading preserves native row order, including identical pins.
            bool ordered = !state.Shared && added.Count == data.Rows.Count;
            for (int i = 0; ordered && i < added.Count; i++) ordered = data.Rows[i].Matches(added[i], authors[i], slots);
            Dictionary<Vector3, List<BetterMapMapData.Row>>? rows = null;
            if (!ordered)
            {
                // Native loading copies positions bit for bit, so an exact key finds the candidate rows.
                rows = new Dictionary<Vector3, List<BetterMapMapData.Row>>();
                foreach (BetterMapMapData.Row row in data.Rows)
                {
                    if (!rows.TryGetValue(row.Position, out List<BetterMapMapData.Row> list)) rows[row.Position] = list = new List<BetterMapMapData.Row>();
                    list.Add(row);
                }
            }
            for (int i = 0; i < added.Count; i++)
            {
                Minimap.PinData pin = added[i];
                BetterMapMapData.Row? row = rows == null ? data.Rows[i] : Find(rows, pin, authors[i], slots, state.Shared);
                if (row == null) { SetIdentity(pin, -1, false, (int)pin.m_type, false); continue; }
                PinIdentity identity = SetIdentity(pin, row.Identity.Category, row.Identity.Automatic, row.Type, row.Identity.Legacy);
                // Only this character's own pins carry discovery records. Placement finds a shared pin
                // on the map instead, because the next table read can remove it.
                identity.PendingDiscovery = row.Identity.Automatic && pin.m_ownerID == 0L;
                RestoreDiscovery(pin, identity);
                Repair(map, pin, identity);
            }
        }
        private static BetterMapMapData.Row? Find(Dictionary<Vector3, List<BetterMapMapData.Row>> rows, Minimap.PinData pin, string author, int slots, bool shared)
        {
            if (!rows.TryGetValue(pin.m_pos, out List<BetterMapMapData.Row> candidates)) return null;
            BetterMapMapData.Row? found = null;
            foreach (BetterMapMapData.Row row in candidates)
            {
                if (!row.Matches(pin, author, slots)) continue;
                // The native importer adds the first of several identical rows and skips the rest.
                if (shared) return row;
                if (found == null) found = row;
                else if (found.Identity.Category != row.Identity.Category || found.Identity.Automatic != row.Identity.Automatic
                    || found.Identity.Legacy != row.Identity.Legacy) return null;
            }
            return found;
        }
        private static void FinishLoad(LoadState? __state)
        {
            if (__state == null || __state.Finished) return;
            __state.Finished = true; loading = __state.Depth;
        }
        private static void BeforeAdd(Minimap __instance, Minimap.PinType type, out bool __state)
        {
            bool[] visible = BetterMapAccess.Visible(__instance);
            int actual = (int)type < 0 || (int)type >= visible.Length ? (int)Minimap.PinType.Icon3 : (int)type;
            __state = enabled && loading > 0 && !visible[actual];
        }
        private static void AfterAdd(Minimap __instance, Minimap.PinData __result, bool __state)
        {
            if (!enabled || __result == null) return;
            if (__state)
            {
                BetterMapAccess.Visible(__instance)[(int)__result.m_type] = false;
                BetterMapAccess.Redraw(__instance);
            }
            if (loading == 0 && __result.m_save)
            {
                int category = placing ?? api.CategoryFor(__result.m_type);
                if (category >= 0) { SetIdentity(__result, category, placing.HasValue, (int)__result.m_type, false); unsettled = true; }
            }
        }
        private static bool StoreFilter() => !enabled || loading == 0;
        private static bool BeginPlacement(MethodBase __originalMethod, object[] __args, out int? __state)
        {
            __state = placing;
            if (!enabled) return true;
            bool portal = __originalMethod.Name == "Portal";
            int category = portal ? 10 : Convert.ToInt32(ruleCategory.GetValue(__args[0]));
            placing = category;
            Minimap map = Minimap.instance;
            if (map == null) return true;
            Vector3 position; float merge;
            if (portal)
            {
                // The vendor's own conditions come first, so a disabled or distant portal changes nothing.
                if (!(__args[0] is ZDO zdo) || !api.Setting("autoPinPortals")) return true;
                position = zdo.GetPosition();
                if ((position - (Vector3)__args[1]).sqrMagnitude > (float)__args[2]) return true;
                merge = (float)__args[3];
            }
            else { position = (Vector3)__args[2]; merge = api.Number("autoPinMergeDistance"); }
            Minimap.PinData? pin = Pinned(map, category, position, merge);
            if (pin == null) return true;
            // The vendor consults only discovery records. A place that already shows an automatic pin,
            // such as one read from a cartography table, must not gain a second one.
            if (pin.m_ownerID == 0L) Record(category, pin.m_pos);
            return false;
        }
        private static void EndPlacement(int? __state) => placing = __state;
        // Uses the vendor's record metric: horizontal distance within the merge distance.
        private static Minimap.PinData? Pinned(Minimap map, int category, Vector3 position, float within)
        {
            Minimap.PinData? found = null; float limit = within * within;
            foreach (Minimap.PinData pin in BetterMapAccess.Pins(map))
            {
                float dx = pin.m_pos.x - position.x, dz = pin.m_pos.z - position.z;
                if (dx * dx + dz * dz > limit || !identities.TryGetValue(pin, out PinIdentity value)
                    || !value.Automatic || value.Category != category) continue;
                if (pin.m_ownerID == 0L) return pin;
                found = pin;
            }
            return found;
        }
        private static void BeginRemoval(out int? __state)
        {
            __state = enabled ? removing : (int?)null;
            if (enabled) removing++;
        }
        private static void EndRemoval(int? __state) { if (__state.HasValue) removing = __state.Value; }
        private static void BeforeRemoval(Minimap.PinData pin)
        {
            // Only removal by hand records the place. A table read removes pins outside this scope.
            if (!enabled || removing == 0 || pin == null || !identities.TryGetValue(pin, out PinIdentity value)
                || !value.Automatic || value.Category < 0) return;
            Record(value.Category, pin.m_pos);
        }
        private static PinIdentity SetIdentity(Minimap.PinData pin, int category, bool automatic, int original, bool legacy)
        {
            var value = new PinIdentity { Category = category, Automatic = automatic, OriginalType = original, Legacy = legacy };
            identities.Remove(pin); identities.Add(pin, value); return value;
        }
        internal static bool AutomaticPortal(Minimap.PinData pin) => enabled && identities.TryGetValue(pin, out PinIdentity value) && value.Automatic && value.Category == 10;
        private static bool Restore()
        {
            if (!enabled) return true;
            // The vendor calls this inside the native load, which must not fail here.
            try { Tick(); }
            catch (Exception error)
            {
                CompatibilityInstaller.Error("Map.Persistence: pin restoration failed during the map load. " + BetterMapAccess.Cause(error));
            }
            return false;
        }
        internal static void Tick()
        {
            Minimap map = Minimap.instance; Player player = Player.m_localPlayer;
            if (!enabled || map == null || player == null || ZNetScene.instance == null || ZNet.instance == null) return;
            player.m_customData.TryGetValue("BetterMap.pinned", out string records);
            if (!unsettled && ReferenceEquals(map, settledMap) && ReferenceEquals(player, settledPlayer)
                && ReferenceEquals(records, settledRecords)) return;
            unsettled = false; settledMap = map; settledPlayer = player; settledRecords = records;
            foreach (Minimap.PinData pin in BetterMapAccess.Pins(map))
            {
                if (!identities.TryGetValue(pin, out PinIdentity value)) continue;
                if (value.Legacy && value.Category < 0 && records != null && records != value.LegacyRecords)
                {
                    value.LegacyRecords = records;
                    if (TryLegacy(pin.m_name, pin.m_pos, value.OriginalType, out int category))
                    { value.Category = category; value.Legacy = false; }
                }
                RestoreDiscovery(pin, value);
                Repair(map, pin, value);
                // A pin still waiting for its record, legend slot or icon keeps the pass running.
                unsettled |= value.PendingDiscovery || value.Category >= 0 && value.Category != 10 && !ReferenceEquals(value.ResolvedMap, map);
            }
        }
        private static void RestoreDiscovery(Minimap.PinData pin, PinIdentity value)
        {
            if (!value.PendingDiscovery || Player.m_localPlayer == null || ZNet.instance == null) return;
            Record(value.Category, pin.m_pos);
            value.PendingDiscovery = false;
        }
        internal static void Record(int category, Vector3 position)
        {
            if (!enabled) return;
            object kind = Enum.ToObject(api.Category, category);
            if (!(bool)recordHas.Invoke(null, new[] { kind, position, (object).1f }))
                recordAdd.Invoke(null, new[] { kind, position });
        }
        private static void Repair(Minimap map, Minimap.PinData pin, PinIdentity value)
        {
            if (value.Category < 0 || value.Category == 10 || ReferenceEquals(value.ResolvedMap, map)) return;
            Minimap.PinType? allocated = api.AllocatedTypeFor(value.Category);
            if (!allocated.HasValue) return;
            Minimap.PinType type = allocated.Value; Sprite icon = api.IconFor(value.Category);
            if (icon != null || value.Category == 9) value.ResolvedMap = map;
            bool changed = pin.m_type != type || icon != null && pin.m_icon != icon;
            if (!changed) return;
            pin.m_type = type;
            if (icon != null)
            {
                pin.m_icon = icon;
                if (pin.m_iconElement != null) pin.m_iconElement.sprite = icon;
            }
            BetterMapAccess.Redraw(map);
        }
        internal static void Rename(Minimap map, Minimap.PinData pin, string label)
        {
            if (pin.m_name == label) return;
            destroyMarker(map, pin);
            pin.m_name = label; pin.m_NamePinData = new Minimap.PinNameData(pin);
            BetterMapAccess.Redraw(map);
        }
        private static bool TryLegacy(string label, Vector3 position, int originalType, out int category)
        {
            category = -1;
            if (!LegacyType(originalType) || Player.m_localPlayer == null || ZNet.instance == null || ZNetScene.instance == null) return false;
            BuildNames();
            if (!names.TryGetValue(label, out HashSet<int> possible) || possible.Count != 1) return false;
            if (!Player.m_localPlayer.m_customData.TryGetValue("BetterMap.pinned", out string text)) return false;
            long world = ZNet.instance.GetWorldUID(); var candidates = new HashSet<int>();
            foreach (string entry in text.Split('|'))
            {
                string[] parts = entry.Split(';');
                if (parts.Length != 4 || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) || id != world
                    || !int.TryParse(parts[1], out int kind) || kind < 0 || kind > 9
                    || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                    || !float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)) continue;
                float dx = x - position.x, dz = z - position.z;
                if (dx * dx + dz * dz <= .01f) candidates.Add(kind);
            }
            if (candidates.Count != 1 || !possible.Contains(candidates.Single())) return false;
            category = candidates.Single(); return true;
        }
        private static bool LegacyType(int type) => type >= 18 || type == (int)Minimap.PinType.Icon3;
        private static void BuildNames()
        {
            if (namesMap == Minimap.instance) return;
            namesMap = Minimap.instance; names = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
            var rules = (System.Collections.IEnumerable)api.Field("Pins.PinRules", "All").GetValue(null);
            FieldInfo prefabs = BetterMapAccess.RequiredField(api.Rule, "Prefabs", false, typeof(string[]));
            FieldInfo token = BetterMapAccess.RequiredField(api.Rule, "NameToken", false, typeof(string));
            FieldInfo location = BetterMapAccess.RequiredField(api.Rule, "IsLocation", false, typeof(bool));
            MethodInfo nameFor = api.Method("Pins.PinNames", "For", typeof(string), api.Rule, typeof(GameObject));
            foreach (object rule in rules)
            {
                int category = Convert.ToInt32(ruleCategory.GetValue(rule));
                if (category < 0 || category > 9) continue;
                void Remember(string? name)
                {
                    if (string.IsNullOrEmpty(name)) return;
                    if (!names.TryGetValue(name!, out HashSet<int> set)) names[name!] = set = new HashSet<int>();
                    set.Add(category);
                }
                Remember((string)token.GetValue(rule));
                var list = (string[]?)prefabs.GetValue(rule);
                if (list == null) continue;
                foreach (string prefab in list)
                {
                    GameObject go = (bool)location.GetValue(rule) ? null! : ZNetScene.instance.GetPrefab(prefab);
                    if (go == null && !(bool)location.GetValue(rule)) continue;
                    Remember((string)nameFor.Invoke(null, new object?[] { rule, go }));
                }
            }
        }
    }
}
