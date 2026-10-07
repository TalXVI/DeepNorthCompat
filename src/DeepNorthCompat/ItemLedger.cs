using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace DeepNorthCompat
{
    // Follows the local player's items and each Quick Stack or restock burst until its
    // transfers settle. MultiUserChest takes a whole stack from the player at once and
    // returns what the owner's chest could not hold a moment later, so an operation's own
    // delta is not its outcome. A burst settles once the player stops pressing and every
    // chest it touched has loaded its current data; the settled record must balance.
    internal static class ItemLedger
    {
        private const float PlayerQuiet = 0.5f, BurstQuiet = 3f, BurstLimit = 15f;
        private static readonly FieldInfo? openChest = AccessTools.Field(typeof(InventoryGui), "m_currentContainer");

        private sealed class Burst
        {
            internal float Last;
            internal int Count;
            internal DateTime Started;
            internal readonly List<string> Operations = new List<string>();
            internal Dictionary<string, int> Player = null!;
            internal readonly Dictionary<ZDOID, (Container Chest, Dictionary<string, int> Before)> Chests = new Dictionary<ZDOID, (Container, Dictionary<string, int>)>();
        }

        private static Burst? burst;
        private static Inventory? watched;
        private static Dictionary<string, int> baseline = new Dictionary<string, int>(StringComparer.Ordinal);
        private static float changed = -1;

        internal static void InventoryChanged(Inventory inventory)
        {
            if (watched != null && ReferenceEquals(inventory, watched)) changed = Time.unscaledTime;
        }

        // Returns the chests that joined the burst with this operation.
        internal static Container[] BeginOperation(string name, Player player, IEnumerable<Container> chests)
        {
            if (!ReferenceEquals(player, Player.m_localPlayer)) return Array.Empty<Container>();
            Watch(player.GetInventory());
            Flush();
            if (burst == null) burst = new Burst { Started = DateTime.UtcNow, Player = Counts(player.GetInventory()) };
            burst.Last = Time.unscaledTime; burst.Count++;
            if (burst.Operations.Count == 0 || burst.Operations[burst.Operations.Count - 1] != name) burst.Operations.Add(name);
            var joined = new List<Container>();
            foreach (Container chest in chests)
                if (ChestRegistry.IdOf(chest) is ZDOID id && !burst.Chests.ContainsKey(id))
                {
                    burst.Chests[id] = (chest, Counts(chest.GetInventory()));
                    joined.Add(chest);
                }
            return joined.ToArray();
        }

        // The operation records its own delta, so later player records start from here.
        internal static void EndOperation(Player player)
        {
            if (!ReferenceEquals(player, Player.m_localPlayer) || burst == null) return;
            baseline = Counts(player.GetInventory()); changed = -1;
            burst.Last = Time.unscaledTime;
        }

        internal static void Tick()
        {
            Player? player = Player.m_localPlayer;
            if (player == null) { watched = null; burst = null; return; }
            Watch(player.GetInventory());
            float now = Time.unscaledTime;
            if (changed >= 0 && now - changed >= PlayerQuiet) Flush();
            if (burst == null) return;
            float quiet = now - burst.Last;
            if (quiet < BurstQuiet) return;
            bool pending = burst.Chests.Values.Any(entry => ChestRegistry.LiveZdo(entry.Chest) is ZDO zdo && ChestRegistry.LoadedRevision(entry.Chest) != zdo.DataRevision);
            if (pending && quiet < BurstLimit) return;
            Flush();
            Settle(player, pending);
        }

        private static void Watch(Inventory inventory)
        {
            if (ReferenceEquals(inventory, watched)) return;
            // A new inventory means a new player object, such as after death. Its items are a
            // fresh baseline, and a burst from the old one can no longer balance.
            watched = inventory; baseline = Counts(inventory); changed = -1; burst = null;
        }

        private static void Flush()
        {
            if (watched == null || changed < 0) return;
            changed = -1;
            Dictionary<string, int> current = Counts(watched);
            Dictionary<string, int> delta = ChestDiagnostics.Delta(baseline, current);
            baseline = current;
            if (delta.Count == 0) return;
            // The open chest is the likely other side of a manual move.
            Container? open = openChest == null || InventoryGui.instance == null ? null : openChest.GetValue(InventoryGui.instance) as Container;
            ChestDiagnostics.Record("player-items", open, "delta=" + ChestDiagnostics.Format(delta) + (open == null ? ";open_chest=none" : ";open_chest=this"));
        }

        private static void Settle(Player player, bool pending)
        {
            Burst settled = burst!; burst = null;
            Dictionary<string, int> playerDelta = ChestDiagnostics.Delta(settled.Player, Counts(player.GetInventory()));
            var net = new Dictionary<string, int>(playerDelta, StringComparer.Ordinal);
            var moved = new List<string>();
            var unloaded = new List<string>();
            foreach (var entry in settled.Chests)
            {
                if (ChestRegistry.LiveZdo(entry.Value.Chest) == null) { unloaded.Add(entry.Key.ToString()); continue; }
                Dictionary<string, int> delta = ChestDiagnostics.Delta(entry.Value.Before, Counts(entry.Value.Chest.GetInventory()));
                if (delta.Count == 0) continue;
                moved.Add(entry.Key + " " + ChestDiagnostics.Format(delta));
                foreach (var change in delta) net[change.Key] = (net.TryGetValue(change.Key, out int value) ? value : 0) + change.Value;
            }
            foreach (string key in net.Where(entry => entry.Value == 0).Select(entry => entry.Key).ToArray()) net.Remove(key);
            bool verified = unloaded.Count == 0 && !pending;
            string details = "operations=" + settled.Count + "(" + string.Join(",", settled.Operations) + ")"
                + ";span=" + (DateTime.UtcNow - settled.Started).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s"
                + ";player_delta=" + ChestDiagnostics.Format(playerDelta)
                + ";chest_deltas=" + (moved.Count == 0 ? "none" : string.Join(", ", moved))
                + ";unaccounted=" + ChestDiagnostics.Format(net)
                + (unloaded.Count == 0 ? "" : ";unchecked_unloaded=" + string.Join(",", unloaded))
                + (pending ? ";unchecked_not_loaded=true" : "");
            bool unbalanced = net.Count > 0 && verified;
            ChestDiagnostics.Record("operation-settled", null, details, unbalanced ? Severity.Warning : Severity.Info,
                unbalanced ? "Items appeared or vanished across the player and the chests in range. Negative counts left both; "
                    + "another player or your own pickup, use or crafting in the same seconds can also explain a difference." : "");
        }

        private static Dictionary<string, int> Counts(Inventory inventory) => ChestDiagnostics.Counts(new[] { inventory });

        internal static void Reset() { burst = null; watched = null; baseline = new Dictionary<string, int>(StringComparer.Ordinal); changed = -1; }
    }
}
