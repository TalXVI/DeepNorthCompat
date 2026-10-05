using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace DeepNorthCompat
{
    // CraftyBoxes saves whole inventories synchronously. Obtain an owner-approved
    // handoff before entering that path, as chest opening does, without opening a UI.
    // The owner side is installed with Chests.Registry so every peer can answer requests,
    // even when this peer's crafting hooks are inactive.
    internal static class ChestCraftPatch
    {
        internal const string Owner = Plugin.Guid + ".ChestCraft";
        private const string RequestRpc = "DeepNorthCompat.ChestHandoffRequest.v2";
        private const string ReplyRpc = "DeepNorthCompat.ChestHandoffReply.v2";
        private const float Timeout = 8f, ReservationTime = 10f;
        private const int MaxAttempts = 3;
        private enum Status : byte { Granted, Denied, NotOwner }
        private static readonly FieldInfo recipeField = AccessTools.Field(typeof(InventoryGui), "m_craftRecipe");
        private static readonly FieldInfo upgradeField = AccessTools.Field(typeof(InventoryGui), "m_craftUpgradeItem");
        private static readonly FieldInfo multiField = AccessTools.Field(typeof(InventoryGui), "m_multiCrafting");
        private static readonly FieldInfo timerField = AccessTools.Field(typeof(InventoryGui), "m_craftTimer");
        private static readonly MethodInfo checkAccess = Guard.Method(typeof(Container), "CheckAccess", typeof(bool), typeof(long));
        private static readonly MethodInfo increaseRevision = Guard.Method(typeof(ZDO), "IncreaseDataRevision", typeof(void));
        private static readonly FieldInfo wards = AccessTools.Field(typeof(PrivateArea), "m_allAreas");
        private static readonly FieldInfo wardPiece = AccessTools.Field(typeof(PrivateArea), "m_piece");
        private static readonly MethodInfo wardEnabled = Guard.Method(typeof(PrivateArea), "IsEnabled", typeof(bool));
        private static readonly MethodInfo wardInside = Guard.Method(typeof(PrivateArea), "IsInside", typeof(bool), typeof(Vector3), typeof(float));
        private static readonly MethodInfo wardPermitted = Guard.Method(typeof(PrivateArea), "IsPermitted", typeof(bool), typeof(long));
        private static MethodInfo? query, shouldPrevent, canPull, adapterInventory, adapterCount, adapterPrefab;
        private static MethodInfo? invalidateCounts;
        private static FieldInfo? range, leaveOne;
        private static FieldInfo? queryFrame, queryTime, bankFrame;
        private static bool ownerReady, requesterReady;
        private static long sequence;
        private static Pending? pending;
        [ThreadStatic] private static bool consuming;
        private static readonly Dictionary<Container, float> reserved = new Dictionary<Container, float>(ReferenceComparer<Container>.Instance);

        private sealed class CraftState
        {
            internal readonly string Operation = ChestDiagnostics.Operation;
            internal readonly bool Consuming = consuming;
            internal List<Inventory>? Inventories;
            internal Dictionary<string, int>? Before;
            internal bool Ran, Unmeasured;
            internal string Craft = "";
        }

        private sealed class Request
        {
            internal readonly Container Chest;
            internal long Target, Nonce;
            internal int Attempts;
            internal uint Revision;
            internal string? Hash;
            internal bool Granted;
            internal Request(Container chest) { Chest = chest; }
        }
        private sealed class Pending
        {
            internal readonly InventoryGui Gui;
            internal readonly Player Player;
            internal readonly Recipe Recipe;
            internal readonly object? Upgrade;
            internal readonly int Multiplier;
            internal readonly CraftingStation Station;
            internal readonly List<Request> Requests;
            internal readonly List<Container> Owned;
            internal readonly float Started, Timer;
            internal Pending(InventoryGui gui, Player player, Recipe recipe, IEnumerable<Container> remote, IEnumerable<Container> owned)
            {
                Gui = gui; Player = player; Recipe = recipe; Upgrade = upgradeField.GetValue(gui);
                Multiplier = MultiplierOf(gui); Station = player.GetCurrentCraftingStation();
                Requests = remote.Select(chest => new Request(chest)).ToList(); Owned = owned.ToList(); Started = Time.unscaledTime;
                Timer = (float)timerField.GetValue(gui);
            }
        }
        private static int MultiplierOf(InventoryGui gui) => (bool)multiField.GetValue(gui) ? gui.m_multiCraftAmount : 1;
        internal static float Range => range?.GetValue(null) is ConfigEntry<float> entry ? entry.Value : 60f;
        internal static bool LeavingOne => leaveOne?.GetValue(null) is ConfigEntryBase entry && Convert.ToInt32(entry.BoxedValue) != 0;

        // Called by Chests.Registry. Reads Crafty's range setting by name only, so an
        // unaudited Crafty build still gets an owner that answers with the default range.
        internal static void PrepareOwner(Assembly? crafty)
        {
            Type? plugin = crafty?.GetType("AzuCraftyBoxes.AzuCraftyBoxesPlugin", false);
            FieldInfo? field = plugin == null ? null : AccessTools.Field(plugin, "mRange");
            range = field?.FieldType == typeof(ConfigEntry<float>) ? field : null;
            ownerReady = true;
        }

        internal static void Install(Assembly? crafty)
        {
            if (!ChestRegistry.Installed) throw new InvalidOperationException("Chests.Registry is not installed.");
            if (crafty == null) { CompatibilityInstaller.Info("ChestCraft: Crafty absent; crafting hooks inactive."); return; }
            if (Guard.KnownServerBuild()) { CompatibilityInstaller.Info("ChestCraft: client-only crafting hooks; inactive on dedicated server."); return; }
            Guard.Build(crafty, ExpectedBuilds.CraftyBoxes);
            Type plugin = Guard.Type(crafty, "AzuCraftyBoxes.AzuCraftyBoxesPlugin");
            range = AccessTools.Field(plugin, "mRange"); leaveOne = AccessTools.Field(plugin, "leaveOne");
            Type functions = Guard.Type(crafty, "AzuCraftyBoxes.Util.Functions.MiscFunctions");
            shouldPrevent = Guard.Method(functions, "ShouldPrevent", typeof(bool));
            Type boxes = Guard.Type(crafty, "AzuCraftyBoxes.Util.Functions.Boxes");
            canPull = Guard.Method(boxes, "CanItemBePulled", typeof(bool), typeof(string), typeof(string), typeof(string));
            query = AccessTools.Method(Guard.Type(crafty, "AzuCraftyBoxes.Util.Functions.Boxes+QueryFrame"), "Get").MakeGenericMethod(typeof(Player));
            queryFrame = AccessTools.Field(query.DeclaringType, "FrameId"); queryTime = AccessTools.Field(boxes, "_lastQueryTime");
            bankFrame = AccessTools.Field(Guard.Type(crafty, "AzuCraftyBoxes.Util.Functions.UiItemBank"), "_frameId");
            invalidateCounts = Guard.Method(boxes, "InvalidateCounts", typeof(void));
            Type adapter = Guard.Type(crafty, "AzuCraftyBoxes.IContainers.IContainer");
            adapterInventory = Guard.Method(adapter, "GetInventory", typeof(Inventory));
            adapterCount = Guard.Method(adapter, "ItemCount", typeof(int), typeof(string));
            adapterPrefab = Guard.Method(adapter, "GetPrefabName", typeof(string));
            var harmony = new Harmony(Owner);
            harmony.Patch(query, postfix: new HarmonyMethod(AccessTools.Method(typeof(ChestCraftPatch), nameof(OwnedSources))
                .MakeGenericMethod(adapterInventory.DeclaringType)));
            var before = Guard.Hook(typeof(ChestCraftPatch), nameof(BeforeCraft), Priority.First + 100);
            before.before = new[] { Plugin.Guid + ".Quality", "MidnightsFX.ImpactfulSkills", "Azumatt.AzuAntiArthriticCrafting" };
            harmony.Patch(Guard.Method(typeof(InventoryGui), "DoCrafting", typeof(void), typeof(Player)), prefix: before,
                finalizer: Guard.Hook(typeof(ChestCraftPatch), nameof(EndCraft)));
            harmony.Patch(Guard.Method(typeof(InventoryGui), "OnCraftPressed", typeof(void)), postfix: Guard.Hook(typeof(ChestCraftPatch), nameof(Pressed)));
            harmony.Patch(Guard.Method(typeof(InventoryGui), "UpdateRecipe", typeof(void), typeof(Player), typeof(float)),
                prefix: Guard.Hook(typeof(ChestCraftPatch), nameof(Update), Priority.First + 100),
                postfix: Guard.Hook(typeof(ChestCraftPatch), nameof(KeepTimer), Priority.First + 100));
            foreach (string name in new[] { "Hide", "OnCraftCancelPressed" })
                harmony.Patch(Guard.Method(typeof(InventoryGui), name, typeof(void)), prefix: Guard.Hook(typeof(ChestCraftPatch), nameof(Cancel)));
            requesterReady = true;
            CompatibilityInstaller.Info("ChestCraft: APPLIED; chest crafting waits for an owner-approved handoff and inventory refresh.");
        }

        internal static void Register(Container chest, ZNetView nview)
        {
            if (!ownerReady) return;
            nview.Register<ZPackage>(RequestRpc, (sender, packet) => ReceiveRequest(chest, sender, packet));
            nview.Register<ZPackage>(ReplyRpc, (sender, packet) => ReceiveReply(chest, sender, packet));
        }

        internal static Container? Unwrap(object? adapter)
        {
            if (adapter == null) return null;
            // Only real vanilla-container adapters are handed off. Backpacks and drawers
            // retain their own ownership/transaction protocols.
            if (adapter.GetType().FullName != "AzuCraftyBoxes.IContainers.VanillaContainer") return null;
            FieldInfo field = adapter.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(info => info.FieldType == typeof(Container));
            return (Container?)field.GetValue(adapter);
        }

        internal static void InvalidateQuery()
        {
            queryFrame?.SetValue(null, -1); queryTime?.SetValue(null, float.NegativeInfinity);
            bankFrame?.SetValue(null, -1); invalidateCounts?.Invoke(null, null);
        }

        private static void OwnedSources<T>(ref List<T> __result)
        {
            if (!consuming) return;
            // Rebuild rather than editing Crafty's shared preview cache. If another
            // source becomes eligible during replanning, it must also be owned before
            // the synchronous consumption path can use it.
            __result = __result.Where(adapter => Unwrap(adapter) is not Container chest
                || ChestRegistry.View(chest)?.IsOwner() == true).ToList();
        }

        private static List<object> Sources(Player player) => ((IEnumerable)query!.Invoke(null, new object[] { player, Range })).Cast<object>().ToList();
        private static int Pullable(int count) => count > 0 && LeavingOne ? count - 1 : count;
        private static bool CanPull(object adapter, Piece.Requirement requirement) =>
            (bool)canPull!.Invoke(null, new[] { adapterPrefab!.Invoke(adapter, null), requirement.m_resItem.name, "" });

        // Selects the chests Crafty will consume from, following its own order and rules.
        private static List<Container> Needed(Player player, InventoryGui gui, Recipe recipe)
        {
            float limit = Range * Range;
            foreach (Container chest in ChestRegistry.Containers)
                if ((ChestRegistry.LiveZdo(chest)!.GetPosition() - player.transform.position).sqrMagnitude <= limit)
                    ChestRegistry.Refresh(chest);
            InvalidateQuery();
            int quality = upgradeField.GetValue(gui) is ItemDrop.ItemData upgrade ? upgrade.m_quality + 1 : 1;
            int multiplier = MultiplierOf(gui);
            List<Container>? qualitySources = QualityPatch.CraftContainers(player, recipe, quality, multiplier);
            if (qualitySources != null) return qualitySources;
            CraftingStation station = player.GetCurrentCraftingStation();
            bool upgrader = station != null && station.m_upgrader;
            var requirements = recipe.m_resources.Where(requirement => requirement?.m_resItem != null && requirement.m_upgraderResource == upgrader).ToList();
            List<object> adapters = Sources(player);
            if (recipe.m_requireOnlyOneIngredient) return SingleSource(player, requirements, adapters, quality, multiplier);
            var result = new List<Container>();
            // Crafty's ProcessRequirements: the player's inventory first, then sources in order.
            foreach (Piece.Requirement requirement in requirements)
            {
                string name = requirement.m_resItem.m_itemData.m_shared.m_name;
                int remaining = checked(requirement.GetAmount(quality) * multiplier) - player.GetInventory().CountItems(name);
                foreach (object adapter in adapters)
                {
                    if (remaining <= 0) break;
                    if (!CanPull(adapter, requirement)) continue;
                    int count = Pullable((int)adapterCount!.Invoke(adapter, new object[] { name }));
                    if (count <= 0) continue;
                    if (Unwrap(adapter) is Container chest && !result.Contains(chest, ReferenceComparer<Container>.Instance)) result.Add(chest);
                    remaining -= count;
                }
            }
            return result;
        }

        // Vanilla GetFirstRequiredItem takes the first requirement the player holds in full at
        // one quality. Otherwise Crafty takes the first requirement that one source holds in
        // full, and consumes it only from that source.
        private static List<Container> SingleSource(Player player, List<Piece.Requirement> requirements, List<object> adapters, int quality, int multiplier)
        {
            foreach (Piece.Requirement requirement in requirements)
            {
                ItemDrop.ItemData.SharedData shared = requirement.m_resItem.m_itemData.m_shared;
                int amount = checked(requirement.GetAmount(quality) * multiplier);
                for (int level = 0; level <= shared.m_maxQuality; level++)
                    if (player.GetInventory().CountItems(shared.m_name, level) >= amount) return new List<Container>();
            }
            foreach (Piece.Requirement requirement in requirements)
            {
                string name = requirement.m_resItem.m_itemData.m_shared.m_name;
                int amount = checked(requirement.GetAmount(quality) * multiplier);
                foreach (object adapter in adapters)
                    if (CanPull(adapter, requirement) && Pullable((int)adapterCount!.Invoke(adapter, new object[] { name })) >= amount
                        && (adapterInventory!.Invoke(adapter, null) as Inventory)?.GetItem(name) != null)
                        return Unwrap(adapter) is Container chest ? new List<Container> { chest } : new List<Container>();
            }
            return new List<Container>();
        }

        private static string Ids(IEnumerable<Container> chests) => string.Join(",", chests.Select(chest => ChestRegistry.IdOf(chest)?.ToString() ?? "?"));
        private static string Describe(Recipe recipe, InventoryGui gui) => "recipe=" + (recipe.m_item != null ? recipe.m_item.name : recipe.name)
            + ";quality=" + (upgradeField.GetValue(gui) is ItemDrop.ItemData upgrade ? upgrade.m_quality + 1 : 1) + ";multiplier=" + MultiplierOf(gui)
            + ";single_ingredient=" + recipe.m_requireOnlyOneIngredient;

        private static bool Start(InventoryGui gui, Player player)
        {
            if (pending != null) return false;
            if (query == null || ZNet.instance == null || ZRoutedRpc.instance == null || player != Player.m_localPlayer
                || player.NoCostCheat() || ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoCraftCost)
                || (bool)shouldPrevent!.Invoke(null, null) || !(recipeField.GetValue(gui) is Recipe recipe)) return true;
            List<Container> needed = Needed(player, gui, recipe);
            List<Container> remote = needed.Where(chest => ChestRegistry.View(chest)?.IsOwner() != true).ToList();
            ChestDiagnostics.Record("craft-plan", null, Describe(recipe, gui) + ";sources=" + Ids(needed) + ";handoff=" + Ids(remote));
            if (remote.Count == 0) return true;
            pending = new Pending(gui, player, recipe, remote, needed.Except(remote, ReferenceComparer<Container>.Instance));
            foreach (Request request in pending.Requests) Send(request);
            return false;
        }

        // Asks the chest's current owner. An unowned chest waits for the game to assign one.
        private static void Send(Request request)
        {
            ZNetView nview = ChestRegistry.View(request.Chest)!;
            ZDO zdo = nview.GetZDO();
            long owner = zdo.GetOwner();
            if (owner == 0) { request.Target = 0; return; }
            request.Target = owner; request.Nonce = ++sequence; request.Attempts++;
            var packet = new ZPackage(); packet.Write(request.Nonce); packet.Write(pending!.Player.GetPlayerID()); packet.Write(zdo.DataRevision);
            ChestDiagnostics.Record("craft-handoff-request", request.Chest, "nonce=" + request.Nonce + ";target=" + owner + ";attempt=" + request.Attempts);
            nview.InvokeRPC(owner, RequestRpc, packet);
        }

        private static void Pressed(InventoryGui __instance)
        {
            if ((float)timerField.GetValue(__instance) >= 0 && Player.m_localPlayer != null) Start(__instance, Player.m_localPlayer);
        }
        private static bool Update(InventoryGui __instance)
        {
            if (pending == null || pending.Gui != __instance) return true;
            return Ready();
        }
        private static void KeepTimer(InventoryGui __instance)
        {
            // UpdateRecipe resets the timer after DoCrafting returns. A deferred craft
            // remains in progress, including for AAA's before/after timer-based queue.
            if (pending?.Gui == __instance) timerField.SetValue(__instance, Math.Max(0, pending.Timer));
        }

        private static bool BeforeCraft(InventoryGui __instance, Player __0, out CraftState __state)
        {
            __state = new CraftState(); ChestDiagnostics.Operation = "craft";
            if (pending != null && !Ready()) return false;
            bool ready = Start(__instance, __0);
            consuming = ready; __state.Ran = ready;
            if (ready && ChestDiagnostics.Enabled && !__0.NoCostCheat() && !ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoCraftCost)
                && recipeField.GetValue(__instance) is Recipe recipe) Measure(__instance, __0, recipe, __state);
            return ready;
        }

        // Counts every inventory the craft can take from, so the result shows what moved.
        private static void Measure(InventoryGui gui, Player player, Recipe recipe, CraftState state)
        {
            state.Inventories = new List<Inventory> { player.GetInventory() };
            foreach (object adapter in Sources(player))
            {
                if (adapterInventory!.Invoke(adapter, null) is Inventory inventory) { if (!state.Inventories.Contains(inventory)) state.Inventories.Add(inventory); }
                else state.Unmeasured = true;
            }
            state.Before = ChestDiagnostics.Counts(state.Inventories);
            state.Craft = Describe(recipe, gui);
        }

        private static Exception? EndCraft(InventoryGui __instance, Exception? __exception, Player __0, CraftState __state)
        {
            try
            {
                if (__state.Before != null)
                {
                    Dictionary<string, int> delta = ChestDiagnostics.Delta(__state.Before, ChestDiagnostics.Counts(__state.Inventories!));
                    bool gained = delta.Values.Any(value => value > 0), lost = delta.Values.Any(value => value < 0);
                    CraftingStation station = __0.GetCurrentCraftingStation();
                    string kind = "craft-result"; Severity severity = Severity.Info;
                    if (__exception != null) { kind = "craft-exception"; severity = Severity.Problem; }
                    else if (gained && !lost) { kind = "craft-output-without-consumption"; severity = __state.Unmeasured ? Severity.Warning : Severity.Problem; }
                    // An upgrader station can fail an upgrade after taking its resources.
                    else if (lost && !gained) { kind = "craft-consumed-without-output"; severity = station != null && station.m_upgrader ? Severity.Warning : Severity.Problem; }
                    ChestDiagnostics.Record(kind, null, __state.Craft + ";delta=" + ChestDiagnostics.Format(delta)
                        + (__state.Unmeasured ? ";note=some sources have no inventory and are not counted" : ""), severity,
                        __exception == null ? "" : __exception.ToString());
                }
            }
            finally
            {
                if (__state.Ran) reserved.Clear();
                consuming = __state.Consuming; InvalidateQuery();
                ChestDiagnostics.Operation = __state.Operation;
            }
            return __exception;
        }

        private static bool Ready()
        {
            Pending craft = pending!;
            if (craft.Player != Player.m_localPlayer || !ReferenceEquals(recipeField.GetValue(craft.Gui), craft.Recipe)
                || !ReferenceEquals(upgradeField.GetValue(craft.Gui), craft.Upgrade) || MultiplierOf(craft.Gui) != craft.Multiplier
                || craft.Player.GetCurrentCraftingStation() != craft.Station)
            { CancelPending("craft-context-changed", Severity.Info, null); return false; }
            if (Time.unscaledTime - craft.Started >= Timeout)
            {
                string waiting = string.Join(",", craft.Requests.Where(request => !request.Granted)
                    .Select(request => ChestRegistry.IdOf(request.Chest) + "@peer" + request.Target + "x" + request.Attempts));
                CancelPending("handoff-timeout;no_reply_from=" + waiting + ";hint=the owner may lack DeepNorthCompat 1.1.3 or be lagging", Severity.Problem,
                    "Chest owner did not respond. Try crafting again.");
                return false;
            }
            bool ready = true;
            foreach (Request request in craft.Requests)
            {
                ZNetView? nview = ChestRegistry.View(request.Chest);
                if (nview == null || !nview.IsValid()) { CancelPending("chest-unloaded", Severity.Warning, "Chest unloaded. Try crafting again."); return false; }
                ZDO zdo = nview.GetZDO();
                if (!request.Granted)
                {
                    if (request.Target != 0) { ready = false; continue; }
                    if (nview.IsOwner())
                    {
                        // Ownership arrived from elsewhere, such as opening the chest.
                        request.Granted = true; request.Revision = zdo.DataRevision;
                        ChestDiagnostics.Record("craft-handoff-owned", request.Chest, "ownership arrived without a handoff");
                    }
                    else
                    {
                        if (request.Attempts >= MaxAttempts)
                        { CancelPending("owner-moved;attempts=" + request.Attempts, Severity.Warning, "Chest owner changed. Try crafting again."); return false; }
                        Send(request); ready = false; continue;
                    }
                }
                if (!nview.IsOwner())
                {
                    long owner = zdo.GetOwner();
                    if (owner != request.Target)
                    { CancelPending("ownership-lost-after-grant;owner=" + owner, Severity.Warning, "Another player took the chest. Try crafting again."); return false; }
                    ready = false; continue;
                }
                if (zdo.DataRevision < request.Revision) { ready = false; continue; }
                ChestRegistry.Refresh(request.Chest);
                if (ChestRegistry.LoadedRevision(request.Chest) != zdo.DataRevision)
                {
                    CancelPending("owner-inventory-not-current;loaded=" + ChestRegistry.LoadedRevision(request.Chest) + ";data=" + zdo.DataRevision,
                        Severity.Problem, "Chest ingredients are unavailable. Try crafting again.");
                    return false;
                }
                if (request.Hash != null && zdo.DataRevision == request.Revision && ChestRegistry.PayloadHash(zdo) != request.Hash)
                {
                    CancelPending("payload-diverged;revision=" + zdo.DataRevision + ";owner_payload=" + ChestDiagnostics.Short(request.Hash)
                        + ";local_payload=" + ChestDiagnostics.Short(ChestRegistry.PayloadHash(zdo)), Severity.Problem,
                        "Chest contents differ from the owner's. Try crafting again.");
                    return false;
                }
            }
            if (!ready) return false;
            // Replanning happens in the original craft on this same main-thread turn. Hold the
            // chests until that craft finishes so another requester cannot take them first.
            pending = null; InvalidateQuery();
            foreach (Request request in craft.Requests) reserved[request.Chest] = Time.unscaledTime + ReservationTime;
            ChestDiagnostics.Record("craft-handoff-ready", null, "chests=" + Ids(craft.Requests.Select(request => request.Chest))
                + ";waited_ms=" + (int)((Time.unscaledTime - craft.Started) * 1000));
            return true;
        }

        private static bool Busy(Container chest) =>
            pending != null && (pending.Requests.Any(request => ReferenceEquals(request.Chest, chest)) || pending.Owned.Contains(chest, ReferenceComparer<Container>.Instance))
            || reserved.TryGetValue(chest, out float until) && until > Time.unscaledTime;

        private static string Denial(Container chest, ZNetView nview, long sender, long playerId)
        {
            if (Busy(chest)) return "owner-crafting";
            Player? player = Player.GetPlayer(playerId);
            if (player == null) return "requester-not-loaded";
            if (player.GetOwner() != sender) return "identity-mismatch";
            if (!(bool)checkAccess.Invoke(chest, new object[] { playerId })) return "no-access";
            if (chest.IsInUse()) return "in-use";
            if (chest.m_wagon != null && chest.m_wagon.InUse()) return "wagon-in-use";
            if ((nview.GetZDO().GetPosition() - player.transform.position).sqrMagnitude > Range * Range) return "out-of-range";
            if (!WardAccess(nview.GetZDO().GetPosition(), playerId)) return "ward";
            return "";
        }

        private static void ReceiveRequest(Container chest, long sender, ZPackage packet)
        {
            long nonce = packet.ReadLong(), playerId = packet.ReadLong(); uint requesterRevision = packet.ReadUInt();
            ZNetView? nview = ChestRegistry.View(chest);
            if (nview == null || !nview.IsValid()) return;
            ZDO zdo = nview.GetZDO();
            Status status = Status.NotOwner; string reason = "not-owner"; string hash = ""; uint ownerRevision = zdo.DataRevision;
            if (nview.IsOwner())
            {
                reason = Denial(chest, nview, sender, playerId);
                if (reason.Length == 0)
                {
                    ChestRegistry.Refresh(chest);
                    if (ChestRegistry.LoadedRevision(chest) != zdo.DataRevision) reason = "owner-data-not-loaded";
                }
                status = reason.Length == 0 ? Status.Granted : Status.Denied;
            }
            if (status == Status.Granted)
            {
                // The game keeps whichever copy has the higher revision and ignores an equal
                // one. A requester at this revision usually holds the same data, but a save
                // without ownership can leave a different copy at the same revision that never
                // converges. Move the owner's copy above the requester's so it replaces either.
                if (requesterRevision >= zdo.DataRevision)
                {
                    zdo.DataRevision = requesterRevision; increaseRevision.Invoke(zdo, null);
                    ChestRegistry.SetLoadedRevision(chest, zdo.DataRevision);
                }
                hash = ChestRegistry.PayloadHash(zdo);
                zdo.SetOwner(sender);
                ZDOMan.instance.ForceSendZDO(sender, zdo.m_uid);
            }
            ChestDiagnostics.Record("craft-handoff-owner", chest, "nonce=" + nonce + ";requester=" + sender + ";player=" + playerId + ";status=" + status
                + ";reason=" + reason + ";requester_revision=" + requesterRevision + ";owner_revision=" + ownerRevision + ";revision=" + zdo.DataRevision,
                status == Status.Granted && requesterRevision > ownerRevision ? Severity.Problem : Severity.Info,
                status == Status.Granted && requesterRevision > ownerRevision ? "revision-ahead: the requester held a newer revision than the owner, "
                + "which only a save without ownership produces. The owner's copy replaces it; look for NON_OWNER_WRITE on the requester." : "");
            var reply = new ZPackage();
            reply.Write(nonce); reply.Write((byte)status); reply.Write(reason); reply.Write(zdo.DataRevision); reply.Write(hash); reply.Write(zdo.GetOwner());
            nview.InvokeRPC(sender, ReplyRpc, reply);
        }

        private static bool WardAccess(Vector3 position, long playerId)
        {
            // PrivateArea.CheckAccess checks Player.m_localPlayer and cannot authorize
            // the requester on a dedicated server or a different owning client.
            foreach (PrivateArea ward in (IEnumerable)wards.GetValue(null))
                if ((bool)wardEnabled.Invoke(ward, null) && (bool)wardInside.Invoke(ward, new object[] { position, 0f })
                    && ((Piece)wardPiece.GetValue(ward)).GetCreator() != playerId
                    && !(bool)wardPermitted.Invoke(ward, new object[] { playerId })) return false;
            return true;
        }

        private static void ReceiveReply(Container chest, long sender, ZPackage packet)
        {
            long nonce = packet.ReadLong(); var status = (Status)packet.ReadByte(); string reason = packet.ReadString();
            uint revision = packet.ReadUInt(); string hash = packet.ReadString(); long owner = packet.ReadLong();
            Request? request = pending?.Requests.FirstOrDefault(value => ReferenceEquals(value.Chest, chest) && value.Nonce == nonce && value.Target == sender);
            ChestDiagnostics.Record("craft-handoff-reply", chest, "nonce=" + nonce + ";sender=" + sender + ";status=" + status + ";reason=" + reason
                + ";revision=" + revision + ";owner_payload=" + ChestDiagnostics.Short(hash) + ";owner_reported=" + owner + ";matched=" + (request != null));
            if (request == null) return;
            switch (status)
            {
                case Status.Granted:
                    request.Granted = true; request.Revision = revision; request.Hash = hash.Length == 0 ? null : hash; break;
                case Status.NotOwner:
                    // Ownership moved after this peer chose the target; ask the current owner.
                    request.Target = 0; break;
                default:
                    CancelPending("owner-denied;reason=" + reason, Severity.Warning, Message(reason)); break;
            }
        }

        private static string Message(string reason) => reason switch
        {
            "in-use" => "Another player has this chest open.",
            "wagon-in-use" => "Another player is using this cart.",
            "owner-crafting" => "Another player is crafting from this chest.",
            "no-access" => "This chest is private.",
            "ward" => "A ward blocks access to this chest.",
            "out-of-range" => "The chest owner sees you out of range.",
            _ => "Chest ingredients are unavailable. Try crafting again.",
        };

        private static void Cancel() => CancelPending("player-cancelled", Severity.Info, null);
        private static void CancelPending(string reason, Severity severity, string? message)
        {
            Pending? craft = pending; pending = null; reserved.Clear();
            if (craft == null) return;
            timerField.SetValue(craft.Gui, -1f);
            ChestDiagnostics.Record("craft-handoff-cancelled", null, reason + ";chests=" + Ids(craft.Requests.Select(request => request.Chest)), severity);
            if (message != null)
            {
                CompatibilityInstaller.Warning("ChestCraft: cancelled before consumption: " + reason);
                craft.Player.Message(MessageHud.MessageType.Center, message);
            }
        }

        internal static void Reset() { CancelPending("world-shutdown", Severity.Info, null); reserved.Clear(); consuming = false; }
        internal static bool Active => requesterReady;
    }
}
