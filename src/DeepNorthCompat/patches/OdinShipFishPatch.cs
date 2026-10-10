using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace DeepNorthCompat
{
    internal static class OdinShipFishPatch
    {
        internal const string Owner = Plugin.Guid + ".OdinShip.FishPress";
        internal const string QueryRpc = "DNC.OdinShip.FishCapability.v2", CapabilityRpc = "DNC.OdinShip.FishCapabilityResult.v2";
        internal const string FeedRpc = "DNC.OdinShip.PaidFish.v2", ReplyRpc = "DNC.OdinShip.FishResult.v2";
        private static bool pending, enabled, feedWrapperRan;
        private static MethodInfo[] targets = Array.Empty<MethodInfo>();
        private static long nonce;
        private static FieldInfo view = null!, allowed = null!, required = null!, initialized = null!, effect = null!;
        private static MethodInfo processing = null!, queueSize = null!, queue = null!, trigger = null!, slotAdd = null!, findEmpty = null!;
        private static AccessTools.FieldRef<Player, bool> isLoading = null!;
        private static readonly ConditionalWeakTable<object, State> states = new ConditionalWeakTable<object, State>();
        private static readonly Dictionary<long, Payment> payments = new Dictionary<long, Payment>();
        private static readonly Stopwatch clock = Stopwatch.StartNew();
        [ThreadStatic] private static long target;
        private sealed class State
        {
            internal bool Registered;
            internal readonly Dictionary<(long Sender, long Nonce), bool> Receipts = new Dictionary<(long, long), bool>();
            internal readonly Queue<(long, long)> Order = new Queue<(long, long)>();
        }
        private enum Stage { Checking, Debiting, AwaitingResult, RefundDue, Restoring }
        private sealed class Payment
        {
            internal readonly long Id, Receiver, Session, Payer;
            internal readonly ZDOID PressId;
            internal readonly Player Actor;
            internal readonly Inventory Inventory;
            internal readonly ItemDrop.ItemData Held;
            internal ItemDrop.ItemData Paid;
            internal Stage Stage;
            internal double Expires;
            internal bool Warned;
            internal bool RefundErrorReported;
            internal Payment(long id, long receiver, ZNetView nview, Player actor, ItemDrop.ItemData held)
            {
                Id = id; Receiver = receiver; Session = ZDOMan.GetSessionID(); PressId = nview.GetZDO().m_uid;
                Actor = actor; Payer = actor.GetPlayerID(); Inventory = actor.GetInventory();
                Held = held; Paid = held.Clone(); Paid.m_stack = 1; Expires = clock.Elapsed.TotalSeconds + 5;
            }
        }
        internal static void Prepare(Assembly? assembly)
        {
            if (pending || enabled) return;
            if (assembly == null) { CompatibilityInstaller.Info("OdinShip.FishPress: optional mod absent; inactive."); return; }
            OdinShipPatch.CheckBuild(assembly);
            feedWrapperRan = false;
            Type press = Guard.Type(assembly, "OdinShip.FishPress");
            FieldInfo Field(string name, Type type)
            {
                FieldInfo f = AccessTools.Field(press, name);
                if (f == null || f.FieldType != type) throw new MissingFieldException(press.FullName, name);
                return f;
            }
            view = Field("m_nview", typeof(ZNetView)); allowed = Field("m_allowedFish", typeof(List<ItemDrop>));
            required = Field("m_fishRequired", typeof(int)); initialized = Field("m_isInitialized", typeof(bool)); effect = Field("m_fishAddedEffect", typeof(GameObject));
            processing = Guard.Method(press, "IsProcessing", typeof(bool)); queueSize = Guard.Method(press, "GetQueueSize", typeof(int));
            queue = Guard.Method(press, "QueueItem", typeof(void), typeof(string)); trigger = Guard.Method(press, "TriggerEffect", typeof(void), typeof(GameObject));
            slotAdd = Guard.Method(typeof(Inventory), "AddItem", typeof(bool), typeof(ItemDrop.ItemData), typeof(int), typeof(int), typeof(int), typeof(bool));
            findEmpty = Guard.Method(typeof(Inventory), "FindEmptySlot", typeof(Vector2i), typeof(bool));
            FieldInfo playerLoading = AccessTools.Field(typeof(Player), "m_isLoading");
            if (playerLoading == null || playerLoading.FieldType != typeof(bool)) throw new MissingFieldException(typeof(Player).FullName, "m_isLoading");
            isLoading = AccessTools.FieldRefAccess<Player, bool>("m_isLoading");
            targets = new[] { Guard.Method(press, "OneTimeInitialization", typeof(void)),
                Guard.Method(press, "OnAddFish", typeof(bool), typeof(Switch), typeof(Humanoid), typeof(ItemDrop.ItemData)),
                Guard.Method(typeof(ZNetView), "HandleRoutedRPC", typeof(void), typeof(ZRoutedRpc.RoutedRPCData)) };
            var harmony = new Harmony(Owner);
            harmony.Patch(targets[0], postfix: Guard.Hook(typeof(OdinShipFishPatch), nameof(Register)));
            harmony.Patch(targets[1], prefix: Guard.Hook(typeof(OdinShipFishPatch), nameof(Feed)),
                transpiler: Guard.Hook(typeof(OdinShipFishPatch), nameof(FeedWrapper)));
            harmony.Patch(targets[2], prefix: Guard.Hook(typeof(OdinShipFishPatch), nameof(BeginRoute)), finalizer: Guard.Hook(typeof(OdinShipFishPatch), nameof(EndRoute)));
            pending = true;
        }
        internal static void Verify()
        {
            if (!pending) return;
            pending = false;
            if (!feedWrapperRan || !HasHook(targets[1], nameof(FeedWrapper), HarmonyPatchType.Transpiler)
                || !HasHook(targets[0], nameof(Register), HarmonyPatchType.Postfix) || !HasHook(targets[1], nameof(Feed), HarmonyPatchType.Prefix)
                || !HasHook(targets[2], nameof(BeginRoute), HarmonyPatchType.Prefix) || !HasHook(targets[2], nameof(EndRoute), HarmonyPatchType.Finalizer))
            {
                new Harmony(Owner).UnpatchSelf();
                CompatibilityInstaller.Error("OdinShip.FishPress: NOT APPLIED; required hooks or compiled wrapper are missing; vendor behavior retained.");
                return;
            }
            enabled = true;
            CompatibilityInstaller.Info("OdinShip.FishPress: APPLIED; v2 owner capability precedes payment; explicit rejection restores the sender's retained fish.");
        }
        private static bool HasHook(MethodInfo method, string hook, HarmonyPatchType kind)
        {
            Patches? patches = Harmony.GetPatchInfo(method);
            if (patches == null) return false;
            IEnumerable<Patch> list = kind == HarmonyPatchType.Postfix ? patches.Postfixes : kind == HarmonyPatchType.Finalizer ? patches.Finalizers
                : kind == HarmonyPatchType.Transpiler ? patches.Transpilers : patches.Prefixes;
            return list.Count(p => p.owner == Owner && p.PatchMethod == AccessTools.Method(typeof(OdinShipFishPatch), hook)) == 1;
        }
        private static IEnumerable<CodeInstruction> FeedWrapper(IEnumerable<CodeInstruction> instructions)
        {
            CodeInstruction[] code = instructions.ToArray();
            feedWrapperRan = true;
            return code;
        }
        private static void BeginRoute(ZRoutedRpc.RoutedRPCData __0, out long __state) { __state = target; target = __0.m_targetPeerID; }
        private static Exception? EndRoute(Exception? __exception, long __state) { target = __state; return __exception; }
        private static ZNetView View(object press) => (ZNetView)view.GetValue(press);
        private static bool Targeted(ZNetView nview) => nview != null && nview.IsValid() && target != 0 && target == ZDOMan.GetSessionID();
        private static void Notify(string message) => Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, message);
        private static void Register(object __instance)
        {
            if (!enabled) return;
            ZNetView nview = View(__instance);
            if (nview == null || !nview.IsValid()) return;
            State state = states.GetOrCreateValue(__instance);
            if (state.Registered) return;
            nview.Register<long>(QueryRpc, (sender, id) => Query(__instance, sender, id));
            nview.Register<long, bool>(CapabilityRpc, (sender, id, supported) => Capability(__instance, sender, id, supported));
            nview.Register<ZPackage>(FeedRpc, (sender, packet) => Receive(__instance, sender, packet));
            nview.Register<long, bool>(ReplyRpc, (sender, id, accepted) => Reply(__instance, sender, id, accepted));
            state.Registered = true;
        }
        private static bool Eligible(object press, Player player, ItemDrop.ItemData item)
        {
            ZNetView nview = View(press);
            return nview != null && nview.IsValid() && (bool)initialized.GetValue(press) && !player.IsDead() && !isLoading(player)
                && player.GetPlayerID() != 0 && item != null && item.m_stack > 0 && item.m_dropPrefab != null
                && item.m_quality > 0 && item.m_variant >= 0 && item.m_worldLevel >= 0 && !float.IsNaN(item.m_durability) && !float.IsInfinity(item.m_durability)
                && player.GetInventory().ContainsItem(item) && ((List<ItemDrop>)allowed.GetValue(press)).Any(f => f.gameObject.name == item.m_dropPrefab.name)
                && !(bool)processing.Invoke(press, null) && (int)queueSize.Invoke(press, null) < (int)required.GetValue(press)
                && (player.transform.position - nview.GetZDO().GetPosition()).sqrMagnitude <= 64f;
        }
        private static bool Feed(object __instance, Humanoid user, ItemDrop.ItemData item, ref bool __result)
        {
            if (!enabled) return true;
            __result = false; Tick();
            if (!(user is Player player) || user != Player.m_localPlayer) return false;
            ZNetView nview = View(__instance);
            if (nview == null || !nview.IsValid() || !(bool)initialized.GetValue(__instance)) { Notify("The fish press is not ready. Your fish has been kept."); return false; }
            item = item ?? ((List<ItemDrop>)allowed.GetValue(__instance)).Select(f => player.GetInventory().GetItem(f.m_itemData.m_shared.m_name)).FirstOrDefault(i => i != null)!;
            if (!Eligible(__instance, player, item)) { Notify("The fish press cannot take that fish now. Your fish has been kept."); return false; }
            long receiver = nview.GetZDO().GetOwner();
            if (receiver == 0) { Notify("The fish press is unavailable. Your fish has been kept."); return false; }
            if (payments.Count >= 64) { Notify("Too many fish deliveries or refunds are pending. Your fish has been kept."); return false; }
            if (payments.Values.Any(p => p.PressId == nview.GetZDO().m_uid && p.Stage == Stage.Checking)) { Notify("Waiting for the fish press. Your fish has been kept."); return false; }
            Register(__instance);
            var payment = new Payment(++nonce, receiver, nview, player, item);
            payments.Add(payment.Id, payment);
            try { nview.InvokeRPC(receiver, QueryRpc, payment.Id); } catch (Exception error) { SendFailed(payment, error); }
            __result = true; return false;
        }
        private static void Query(object press, long sender, long id)
        {
            ZNetView nview = View(press);
            if (!Targeted(nview) || sender == 0 || id <= 0) return;
            bool supported = enabled && nview.IsOwner() && (bool)initialized.GetValue(press) && states.GetOrCreateValue(press).Registered;
            nview.InvokeRPC(sender, CapabilityRpc, id, supported);
        }
        private static bool Match(object press, long sender, long id, Stage stage, out Payment payment)
        {
            payment = null!; ZNetView nview = View(press);
            return Targeted(nview) && payments.TryGetValue(id, out payment) && payment.Stage == stage
                && payment.Receiver == sender && payment.Session == ZDOMan.GetSessionID() && payment.PressId == nview.GetZDO().m_uid;
        }
        private static void Capability(object press, long sender, long id, bool supported)
        {
            if (!Match(press, sender, id, Stage.Checking, out Payment payment)) return;
            if (!enabled || !supported || clock.Elapsed.TotalSeconds >= payment.Expires || View(press).GetZDO().GetOwner() != sender
                || Player.m_localPlayer != payment.Actor || payment.Actor.GetInventory() != payment.Inventory
                || payment.Actor.GetPlayerID() != payment.Payer || !Eligible(press, payment.Actor, payment.Held))
            { payments.Remove(id); Notify("The fish press is unavailable. Your fish has been kept."); return; }
            payment.Paid = payment.Held.Clone(); payment.Paid.m_stack = 1;
            var packet = new ZPackage(); packet.Write(id); packet.Write(payment.Payer); packet.Write(payment.Paid.m_dropPrefab.name);
            if (packet.Size() > 256) { payments.Remove(id); Notify("The fish press cannot take that fish. Your fish has been kept."); return; }
            payment.Stage = Stage.Debiting; int before = payment.Held.m_stack;
            try { if (!payment.Inventory.RemoveItem(payment.Held, 1)) { payments.Remove(id); return; } }
            catch (Exception error)
            {
                bool removed = !payment.Inventory.ContainsItem(payment.Held) || payment.Held.m_stack == before - 1;
                if (removed) { payment.Stage = Stage.RefundDue; Restore(payment); } else payments.Remove(id);
                CompatibilityInstaller.Warning("OdinShip.FishPress: inventory debit failed before dispatch. " + error); return;
            }
            payment.Stage = Stage.AwaitingResult; payment.Expires = clock.Elapsed.TotalSeconds + 5;
            try { View(press).InvokeRPC(sender, FeedRpc, packet); } catch (Exception error) { SendFailed(payment, error); }
        }
        private static void SendFailed(Payment payment, Exception error)
        {
            if (!payments.ContainsKey(payment.Id)) return; // A synchronous reply can already have completed it.
            CompatibilityInstaller.Warning($"OdinShip.FishPress: request {payment.Id} to peer {payment.Receiver} failed. {error}");
            if (payment.Stage == Stage.Checking) { payments.Remove(payment.Id); Notify("The fish press could not be reached. Your fish has been kept."); }
            else if (payment.Stage == Stage.AwaitingResult) Uncertain(payment);
        }
        private static void Reply(object press, long sender, long id, bool accepted)
        {
            if (!Match(press, sender, id, Stage.AwaitingResult, out Payment payment)) return;
            if (accepted) { payments.Remove(id); return; }
            payment.Stage = Stage.RefundDue; payment.Warned = false; Restore(payment);
        }
        private static void Receive(object press, long sender, ZPackage packet)
        {
            ZNetView nview = View(press);
            if (!enabled || !Targeted(nview) || sender == 0) return;
            long id, playerId; string fish;
            try
            {
                if (packet.Size() > 256) return;
                id = packet.ReadLong(); playerId = packet.ReadLong(); fish = packet.ReadString();
                if (id <= 0 || packet.GetPos() != packet.Size()) return;
            }
            catch (Exception error) when (error is EndOfStreamException || error is IOException || error is ArgumentException)
            { CompatibilityInstaller.Warning("OdinShip.FishPress: rejected malformed paid feed: " + error.Message); return; }
            State state = states.GetOrCreateValue(press);
            if (state.Receipts.TryGetValue((sender, id), out bool earlier)) { nview.InvokeRPC(sender, ReplyRpc, id, earlier); return; }
            Player player = Player.GetPlayer(playerId);
            bool accepted = player != null && playerId != 0 && player.GetOwner() == sender && !player.IsDead() && !isLoading(player)
                && ((List<ItemDrop>)allowed.GetValue(press)).Any(f => f.gameObject.name == fish) && nview.IsOwner() && !(bool)processing.Invoke(press, null)
                && (int)queueSize.Invoke(press, null) < (int)required.GetValue(press)
                && (player.transform.position - nview.GetZDO().GetPosition()).sqrMagnitude <= 64f && ChestCraftPatch.WardAccess(nview.GetZDO().GetPosition(), playerId);
            if (accepted) queue.Invoke(press, new object[] { fish }); // A queue exception has an uncertain outcome; never reply false.
            state.Receipts.Add((sender, id), accepted); state.Order.Enqueue((sender, id));
            if (state.Order.Count > 1024) state.Receipts.Remove(state.Order.Dequeue());
            if (accepted)
            {
                try { trigger.Invoke(press, new[] { effect.GetValue(press) }); }
                catch (TargetInvocationException error) { CompatibilityInstaller.Warning("OdinShip.FishPress: feed accepted, but its visual effect failed: " + error.InnerException); }
            }
            nview.InvokeRPC(sender, ReplyRpc, id, accepted);
        }
        private static bool SameMetadata(ItemDrop.ItemData item, ItemDrop.ItemData paid) =>
            item.m_dropPrefab != null && item.m_dropPrefab.name == paid.m_dropPrefab.name && item.m_shared.m_name == paid.m_shared.m_name
            && item.m_quality == paid.m_quality && item.m_variant == paid.m_variant && item.m_worldLevel == paid.m_worldLevel
            && item.m_durability.Equals(paid.m_durability) && item.m_crafterID == paid.m_crafterID && item.m_crafterName == paid.m_crafterName
            && item.m_equipped == paid.m_equipped && item.m_pickedUp == paid.m_pickedUp && item.m_cheated == paid.m_cheated
            && item.m_customData.Count == paid.m_customData.Count && item.m_customData.All(p => paid.m_customData.TryGetValue(p.Key, out string value) && value == p.Value);
        private static void Restore(Payment payment)
        {
            try { RestoreInventory(payment); }
            catch (Exception error)
            {
                if (payment.Stage == Stage.Restoring) payment.Stage = Stage.RefundDue;
                RefundFailed(payment, error);
            }
        }
        private static void RefundFailed(Payment payment, Exception error)
        {
            if (payment.RefundErrorReported) return;
            payment.RefundErrorReported = true;
            CompatibilityInstaller.Warning("OdinShip.FishPress: inventory refund failed; the paid record is retained unless its working copy was consumed. " + error);
        }
        private static void RestoreInventory(Payment payment)
        {
            Player player = Player.m_localPlayer;
            if (player == null || player.IsDead() || isLoading(player) || player.GetPlayerID() != payment.Payer) return;
            Inventory inventory = player.GetInventory();
            ItemDrop.ItemData stack = inventory.GetAllItems().FirstOrDefault(i => i.m_gridPos.x >= 0 && i.m_gridPos.y >= 0
                && i.m_gridPos.x < inventory.GetWidth() && i.m_gridPos.y < inventory.GetHeight()
                && i.m_stack < i.m_shared.m_maxStackSize && SameMetadata(i, payment.Paid))!;
            Vector2i slot = stack != null ? stack.m_gridPos : new Vector2i(-1, -1);
            if (stack == null) slot = (Vector2i)findEmpty.Invoke(inventory, new object[] { false });
            if (slot.x < 0) { if (!payment.Warned) { payment.Warned = true; Notify("Your rejected fish is waiting to be returned. Free an inventory slot."); } return; }
            payment.Stage = Stage.Restoring;
            ItemDrop.ItemData? work = null;
            try { work = payment.Paid.Clone(); slotAdd.Invoke(inventory, new object[] { work, 1, slot.x, slot.y, false }); }
            catch (Exception error) { RefundFailed(payment, error); }
            if (work != null && work.m_stack == 0) { payments.Remove(payment.Id); Notify("The fish press rejected the fish; it has been returned to your inventory."); }
            else payment.Stage = Stage.RefundDue;
        }
        private static void Uncertain(Payment payment)
        {
            if (payment.Warned) return;
            payment.Warned = true; Notify("Fish delivery could not be confirmed. Check the press before adding more fish.");
            CompatibilityInstaller.Warning($"OdinShip.FishPress: payment {payment.Id} to {payment.Receiver} remains unconfirmed; no automatic retry or refund.");
        }
        internal static void Tick()
        {
            if (payments.Count == 0) return;
            long session = ZDOMan.instance == null ? 0 : ZDOMan.GetSessionID();
            foreach (Payment payment in payments.Values.ToArray())
            {
                if (payment.Session != session)
                {
                    if (payment.Stage != Stage.Checking) CompatibilityInstaller.Warning($"OdinShip.FishPress: session ended with payment {payment.Id} in state {payment.Stage}; its in-memory record cannot recover across sessions.");
                    payments.Remove(payment.Id); continue;
                }
                if (payment.Stage == Stage.RefundDue) Restore(payment);
                else if (clock.Elapsed.TotalSeconds >= payment.Expires)
                {
                    if (payment.Stage == Stage.Checking) { payments.Remove(payment.Id); Notify("The fish press did not answer. Your fish has been kept."); }
                    else if (payment.Stage == Stage.AwaitingResult) Uncertain(payment);
                }
            }
        }
        internal static void ResetForTests() { payments.Clear(); pending = enabled = false; target = 0; }
    }
}
