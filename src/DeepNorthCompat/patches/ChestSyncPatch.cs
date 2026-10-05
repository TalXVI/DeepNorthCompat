using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;

namespace DeepNorthCompat
{
    // MultiUserChest keeps a chest open after its owner changes. Container.SetInUse only
    // clears the local use flag for owners, and Container.Load refuses to load while that
    // flag is set, so the non-owner's inventory stops updating.
    internal static class ChestSyncPatch
    {
        internal const string Owner = Plugin.Guid + ".Chests";
        internal const string MucGuid = "com.maxsch.valheim.MultiUserChest";
        internal const string QuickGuid = "goldenrevolver.quick_stack_store";
        internal const string MucHash = "AFE0D6B437495FB6624A049BA72C2393572C8C02E1221AC090F16C0D99914514";
        internal const string QuickHash = "4296BE5E9FC8941BE1079A1C979F97FCDFDA79BCA9287F29686E4BBC9E4F0400";
        private static readonly FieldInfo inUse = AccessTools.Field(typeof(Container), "m_inUse");
        private static FieldInfo? stackRange, restockRange;
        private static bool muc;

        internal static float StackRange => Range(stackRange);
        internal static float RestockRange => Range(restockRange);
        private static float Range(FieldInfo? field) => field?.GetValue(null) is ConfigEntry<float> entry ? entry.Value : 0f;

        internal static void Install(Assembly? multiUser, Assembly? quick)
        {
            if (!ChestRegistry.Installed) throw new InvalidOperationException("Chests.Registry is not installed.");
            if (multiUser != null) Guard.Build(multiUser, MucHash);
            if (quick != null) Guard.Build(quick, QuickHash);
            muc = multiUser != null;
            var harmony = new Harmony(Owner);
            harmony.Patch(Guard.Method(typeof(Container), "SetInUse", typeof(void), typeof(bool)), prefix: Guard.Hook(typeof(ChestSyncPatch), nameof(Use), Priority.First));
            harmony.Patch(Guard.Method(typeof(Container), "Load", typeof(bool)), prefix: Guard.Hook(typeof(ChestSyncPatch), nameof(BeforeLoad), Priority.First));
            if (quick != null)
            {
                stackRange = RangeField(quick, "QuickStackStore.QSSConfig+QuickStackConfig", "QuickStackToNearbyRange");
                restockRange = RangeField(quick, "QuickStackStore.QSSConfig+RestockConfig", "RestockFromNearbyRange");
                foreach (var target in new[] { ("QuickStackStore.QuickStackModule", "DoQuickStack"), ("QuickStackStore.RestockModule", "DoRestock") })
                {
                    MethodInfo method = AccessTools.DeclaredMethod(Guard.Type(quick, target.Item1), target.Item2);
                    harmony.Patch(method, prefix: Guard.Hook(typeof(ChestSyncPatch), nameof(BeginOperation)), finalizer: Guard.Hook(typeof(ChestSyncPatch), nameof(EndOperation)));
                }
            }
            CompatibilityInstaller.Info("Chests: APPLIED; non-owner use flags are released and nearby chests refresh before Quick Stack.");
        }

        private static FieldInfo RangeField(Assembly quick, string type, string name)
        {
            FieldInfo? field = AccessTools.Field(Guard.Type(quick, type), name);
            if (field == null || field.FieldType != typeof(ConfigEntry<float>)) throw new MissingFieldException(type, name);
            return field;
        }

        private static void Release(Container chest, string reason)
        {
            // Expected after an ownership change while open. The owner-change record just
            // before this one names the cause.
            ChestDiagnostics.Record("local-use-release", chest, reason, Severity.Warning);
            inUse.SetValue(chest, false);
            ChestCraftPatch.InvalidateQuery();
        }

        private static void Use(Container __instance, bool __0)
        {
            ZNetView? nview = ChestRegistry.View(__instance);
            if (!__0 && __instance.IsInUse() && nview != null && nview.IsValid() && !nview.IsOwner())
                Release(__instance, "closed-after-owner-loss");
        }

        private static void BeforeLoad(Container __instance)
        {
            ZNetView? nview = ChestRegistry.View(__instance);
            if (muc && __instance.IsInUse() && nview != null && nview.IsValid() && !nview.IsOwner())
                Release(__instance, "MUC-non-owner-refresh");
        }

        private sealed class Operation
        {
            internal string Previous = "";
            internal Dictionary<string, int>? Before;
        }

        private static void BeginOperation(MethodBase __originalMethod, Player __0, out Operation __state)
        {
            __state = new Operation { Previous = ChestDiagnostics.Operation };
            ChestDiagnostics.Operation = __originalMethod.Name;
            float range = __originalMethod.Name == "DoRestock" ? RestockRange : StackRange;
            if (ChestDiagnostics.Enabled)
            {
                __state.Before = ChestDiagnostics.Counts(new[] { __0.GetInventory() });
                ChestDiagnostics.Record("operation-start", null, "range=" + range + ";player_items=" + ChestDiagnostics.Items(__0.GetInventory()));
            }
            // Quick Stack reads these inventories directly, so release stale use flags and
            // load current contents first.
            foreach (Container chest in ChestRegistry.Containers)
                if ((ChestRegistry.LiveZdo(chest)!.GetPosition() - __0.transform.position).sqrMagnitude <= range * range)
                {
                    ChestRegistry.Refresh(chest);
                    ChestDiagnostics.Record("operation-chest", chest);
                }
        }

        private static Exception? EndOperation(Exception? __exception, Player __0, Operation __state)
        {
            if (__state.Before != null)
                ChestDiagnostics.Record("operation-end", null, "player_delta=" + ChestDiagnostics.Format(ChestDiagnostics.Delta(__state.Before,
                    ChestDiagnostics.Counts(new[] { __0.GetInventory() }))) + (__exception == null ? "" : ";exception=" + __exception),
                    __exception == null ? Severity.Info : Severity.Problem);
            ChestDiagnostics.Operation = __state.Previous; return __exception;
        }
    }
}
