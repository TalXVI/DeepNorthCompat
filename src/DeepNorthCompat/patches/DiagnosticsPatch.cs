using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace DeepNorthCompat
{
    // Observation-only hooks. They record chest state, ownership, network arrivals and
    // session events, and never change game behavior.
    internal static class DiagnosticsPatch
    {
        internal const string Owner = Plugin.Guid + ".Diagnostics";
        private static readonly Dictionary<ZDOID, (long Owner, uint Data, uint Loaded, bool Use, int Replicated)> states = new Dictionary<ZDOID, (long, uint, uint, bool, int)>();
        private static readonly Dictionary<ZDOID, string> contents = new Dictionary<ZDOID, string>();
        // Inventory.Load raises Inventory.Changed. Attribute that change to the load.
        [ThreadStatic] private static Container? loading;

        internal static void Install()
        {
            if (!ChestDiagnostics.Enabled) { CompatibilityInstaller.Info("Diagnostics: disabled by config or an invalid directory; inactive."); return; }
            if (!ChestRegistry.Installed) throw new InvalidOperationException("Chests.Registry is not installed.");
            var harmony = new Harmony(Owner);
            harmony.Patch(Guard.Method(typeof(Container), "SetInUse", typeof(void), typeof(bool)), prefix: Guard.Hook(typeof(DiagnosticsPatch), nameof(Use), Priority.Last));
            harmony.Patch(Guard.Method(typeof(Container), "CheckForChanges", typeof(void)), prefix: Guard.Hook(typeof(DiagnosticsPatch), nameof(Check)));
            harmony.Patch(Guard.Method(typeof(Container), "Load", typeof(bool)), prefix: Guard.Hook(typeof(DiagnosticsPatch), nameof(Loading)),
                postfix: Guard.Hook(typeof(DiagnosticsPatch), nameof(Loaded)), finalizer: Guard.Hook(typeof(DiagnosticsPatch), nameof(LoadEnded)));
            harmony.Patch(Guard.Method(typeof(Container), "Save", typeof(void)), prefix: Guard.Hook(typeof(DiagnosticsPatch), nameof(Saving)),
                postfix: Guard.Hook(typeof(DiagnosticsPatch), nameof(Saved)));
            harmony.Patch(Guard.Method(typeof(Inventory), "Changed", typeof(void), typeof(bool), typeof(bool)), postfix: Guard.Hook(typeof(DiagnosticsPatch), nameof(InventoryChanged)));
            harmony.Patch(Guard.Method(typeof(ZDO), "SetOwner", typeof(void), typeof(long)), prefix: Guard.Hook(typeof(DiagnosticsPatch), nameof(OwnerChanging)));
            harmony.Patch(Guard.Method(typeof(ZDOMan), "RPC_ZDOData", typeof(void), typeof(ZRpc), typeof(ZPackage)), prefix: Guard.Hook(typeof(DiagnosticsPatch), nameof(Received)));
            harmony.Patch(Guard.Method(typeof(Game), "Start", typeof(void)), postfix: Guard.Hook(typeof(DiagnosticsPatch), nameof(GameStarted)));
            harmony.Patch(Guard.Method(typeof(Player), "OnSpawned", typeof(void), typeof(bool)), postfix: Guard.Hook(typeof(DiagnosticsPatch), nameof(Spawned)));
            harmony.Patch(Guard.Method(typeof(ZNet), "Disconnect", typeof(void), typeof(ZNetPeer)), prefix: Guard.Hook(typeof(DiagnosticsPatch), nameof(Disconnecting)));
            harmony.Patch(Guard.Method(typeof(ZNet), "Shutdown", typeof(void), typeof(bool)), prefix: Guard.Hook(typeof(DiagnosticsPatch), nameof(ShuttingDown)));
            CompatibilityInstaller.Info("Diagnostics: APPLIED; chest, crafting, network and error events are recorded.");
        }

        // MultiUserChest calls SetInUse(true) every frame while a chest is open, so record an
        // open only when the local flag is still clear.
        private static void Use(Container __instance, bool __0)
        {
            if (__0 && __instance.IsInUse()) return;
            if (ChestRegistry.LiveZdo(__instance) != null) ChestDiagnostics.Record(__0 ? "open-use" : "close-use", __instance);
        }

        // MultiUserChest calls this every frame for an open chest, so compare cheap fields
        // before building a record.
        private static void Check(Container __instance)
        {
            try
            {
                ZDO? zdo = ChestRegistry.LiveZdo(__instance);
                if (zdo == null) return;
                var state = (zdo.GetOwner(), zdo.DataRevision, ChestRegistry.LoadedRevision(__instance), __instance.IsInUse(), zdo.GetInt(ZDOVars.s_inUse));
                if (states.TryGetValue(zdo.m_uid, out var previous) && previous.Equals(state)) return;
                bool first = !states.ContainsKey(zdo.m_uid);
                states[zdo.m_uid] = state;
                if (!first) ChestDiagnostics.Record("state-change", __instance, Describe(previous, state));
            }
            catch (Exception error) { ChestDiagnostics.RecordText("diagnostics-hook-error", "Check: " + error, Severity.Info); }
        }

        private static string Describe((long Owner, uint Data, uint Loaded, bool Use, int Replicated) before, (long Owner, uint Data, uint Loaded, bool Use, int Replicated) after)
        {
            var changes = new List<string>();
            if (before.Owner != after.Owner) changes.Add("owner " + before.Owner + "->" + after.Owner);
            if (before.Data != after.Data) changes.Add("data_revision " + before.Data + "->" + after.Data);
            if (before.Loaded != after.Loaded) changes.Add("loaded_revision " + before.Loaded + "->" + after.Loaded);
            if (before.Use != after.Use) changes.Add("local_use " + before.Use + "->" + after.Use);
            if (before.Replicated != after.Replicated) changes.Add("replicated_use " + before.Replicated + "->" + after.Replicated);
            return string.Join(";", changes);
        }

        private static void Contents(Container chest, string kind)
        {
            ZDOID? id = ChestRegistry.IdOf(chest);
            if (id == null || ChestRegistry.LiveZdo(chest) == null) return;
            string items = ChestDiagnostics.Items(chest.GetInventory());
            if (contents.TryGetValue(id.Value, out string previous) && previous == items) return;
            contents[id.Value] = items;
            ChestDiagnostics.Record(kind, chest);
        }

        private static void Loading(Container __instance, out Container? __state) { __state = loading; loading = __instance; }
        private static Exception? LoadEnded(Exception? __exception, Container? __state) { loading = __state; return __exception; }

        private static void Loaded(Container __instance, bool __result)
        {
            if (!__result) return;
            try { Contents(__instance, "inventory-refreshed"); }
            catch (Exception error) { ChestDiagnostics.RecordText("diagnostics-hook-error", "Load: " + error, Severity.Info); }
        }

        private static void Saving(Container __instance)
        {
            ZNetView? nview = ChestRegistry.View(__instance);
            if (nview == null || !nview.IsValid() || nview.IsOwner()) return;
            // The game accepts the higher revision from any writer, so this can overwrite the
            // owner's inventory everywhere. The stack names the mod path that saved.
            ChestDiagnostics.Record("NON_OWNER_WRITE", __instance, "a peer saved a chest it does not own", Severity.Problem,
                "stack: " + ChestDiagnostics.Stack(1, 14));
        }

        private static void Saved(Container __instance)
        {
            try { Contents(__instance, "inventory-saved"); }
            catch (Exception error) { ChestDiagnostics.RecordText("diagnostics-hook-error", "Save: " + error, Severity.Info); }
        }

        private static void InventoryChanged(Inventory __instance)
        {
            try
            {
                ItemLedger.InventoryChanged(__instance);
                Container? chest = ChestRegistry.Find(__instance);
                if (chest is object && !ReferenceEquals(chest, loading)) Contents(chest, "inventory-changed");
            }
            catch (Exception error) { ChestDiagnostics.RecordText("diagnostics-hook-error", "Inventory.Changed: " + error, Severity.Info); }
        }

        private static void OwnerChanging(ZDO __instance, long __0)
        {
            try
            {
                Container? chest = ChestRegistry.Find(__instance.m_uid);
                if (chest is object && __instance.GetOwner() != __0)
                    ChestDiagnostics.Record("owner-assignment", chest, "from=" + __instance.GetOwner() + ";to=" + __0, Severity.Info,
                        "stack: " + ChestDiagnostics.Stack(1, 10));
            }
            catch (Exception error) { ChestDiagnostics.RecordText("diagnostics-hook-error", "SetOwner: " + error, Severity.Info); }
        }

        // Reads the packet in place and restores its position. Exceptions must not escape:
        // ZRpc drops the whole packet on an exception, and treats EndOfStreamException as an
        // incompatible version and disconnects.
        private static void Received(ZRpc __0, ZPackage __1)
        {
            if (ChestRegistry.Count == 0) return;
            int start = __1.GetPos();
            try
            {
                int invalidated = __1.ReadInt();
                for (int i = 0; i < invalidated; i++) __1.ReadZDOID();
                while (true)
                {
                    ZDOID id = __1.ReadZDOID();
                    if (id.IsNone()) break;
                    ushort ownerRevision = __1.ReadUShort(); uint revision = __1.ReadUInt(); long owner = __1.ReadLong();
                    __1.ReadVector3(); int size = __1.ReadInt(); __1.SetPos(__1.GetPos() + size);
                    Container? chest = ChestRegistry.Find(id);
                    ZDO? zdo = ChestRegistry.LiveZdo(chest);
                    if (chest is object && zdo != null && (revision != zdo.DataRevision || owner != zdo.GetOwner()))
                        ChestDiagnostics.Record("network-arrival", chest, "from_peer=" + PeerOf(__0) + ";incoming_owner=" + owner
                            + ";incoming_owner_revision=" + ownerRevision + ";local_owner_revision=" + zdo.OwnerRevision
                            + ";incoming_data_revision=" + revision + ";applies_data=" + (revision > zdo.DataRevision));
                }
            }
            catch (Exception error) { ChestDiagnostics.RecordText("diagnostics-hook-error", "RPC_ZDOData: " + error.GetType().Name + ": " + error.Message, Severity.Info); }
            finally { __1.SetPos(start); }
        }

        private static string PeerOf(ZRpc? rpc)
        {
            if (rpc == null || ZNet.instance == null) return "";
            ZNetPeer? peer = ZNet.instance.GetPeers().FirstOrDefault(value => value.m_rpc == rpc);
            return peer == null ? "" : peer.m_uid + (peer.m_server ? "(server)" : "");
        }

        private static void GameStarted() => DiagnosticReport.GameStarted();
        private static void Spawned(Player __instance)
        {
            if (__instance == Player.m_localPlayer) DiagnosticReport.LocalPlayerSpawned(__instance);
        }
        private static void Disconnecting(ZNetPeer __0) => DiagnosticReport.PeerDisconnecting(__0);
        private static void ShuttingDown() => DiagnosticReport.WorldShutdown();

        internal static void Forget(ZDOID id) { states.Remove(id); contents.Remove(id); }
        internal static void Reset() { states.Clear(); contents.Clear(); }
    }
}
