using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace DeepNorthCompat
{
    internal static class OdinShipPatch
    {
        internal const string Owner = Plugin.Guid + ".OdinShip.Core";
        private static bool pending, enabled;
        private static bool? canoeRewrite;
        private static bool observeNames;
        private static FieldInfo nameView = null!;
        private static MethodInfo updateNameplates = null!;
        private static MethodInfo[] targets = Array.Empty<MethodInfo>();
        private static readonly List<NameState> names = new List<NameState>();
        private static ConditionalWeakTable<object, NameState> nameStates = new ConditionalWeakTable<object, NameState>();
        private static readonly List<WeakReference> pendingTurrets = new List<WeakReference>();
        private static readonly Stopwatch clock = Stopwatch.StartNew();
        private static double nextNameTick;
        private static AccessTools.FieldRef<Ship, ZNetView> shipView = null!;
        private static AccessTools.FieldRef<Turret, ZNetView> turretView = null!;
        private static readonly ConditionalWeakTable<Turret, Refund> refunds = new ConditionalWeakTable<Turret, Refund>();
        [ThreadStatic] private static Frame? frame;

        internal static void CheckBuild(Assembly assembly)
        {
            Guard.Build(assembly, ExpectedBuilds.OdinShip);
            Guard.Build(typeof(Ship).Assembly, ExpectedBuilds.OdinShipGame);
        }

        internal static void Prepare(Assembly? assembly)
        {
            if (pending || enabled) return;
            if (assembly == null) { CompatibilityInstaller.Info("OdinShip.Core: optional mod absent; inactive."); return; }
            CheckBuild(assembly);
            shipView = AccessTools.FieldRefAccess<Ship, ZNetView>("m_nview");
            turretView = AccessTools.FieldRefAccess<Turret, ZNetView>("m_nview");
            canoeRewrite = null;
            MethodInfo canoe = Guard.Method(Guard.Type(assembly, "OdinShip.ShipPatches"), "CustomFixedUpdate", typeof(void), typeof(Ship));
            Type customization = Guard.Type(assembly, "OdinShip.ShipCustomization");
            nameView = AccessTools.Field(customization, "m_nview");
            if (nameView == null || nameView.FieldType != typeof(ZNetView)) throw new MissingFieldException(customization.FullName, "m_nview");
            updateNameplates = Guard.Method(customization, "UpdateNameplates", typeof(void));
            observeNames = !Guard.KnownServerBuild();
            targets = new[] { Guard.Method(typeof(Ship), "CustomFixedUpdate", typeof(void), typeof(float)), canoe,
                Guard.Method(customization, "Setup", typeof(void)), Guard.Method(typeof(Turret), "Awake", typeof(void)) };
            var harmony = new Harmony(Owner);
            harmony.Patch(targets[0],
                prefix: Guard.Hook(typeof(OdinShipPatch), nameof(Begin), Priority.First),
                finalizer: Guard.Hook(typeof(OdinShipPatch), nameof(End)));
            // Patch the audited handler itself. Its foreign registration and priority stay intact.
            harmony.Patch(canoe, prefix: Guard.Hook(typeof(OdinShipPatch), nameof(Canoe)),
                transpiler: Guard.Hook(typeof(OdinShipPatch), nameof(RewriteCanoe)));
            harmony.Patch(targets[2], postfix: Guard.Hook(typeof(OdinShipPatch), nameof(ObserveName)));
            harmony.Patch(targets[3],
                postfix: Guard.Hook(typeof(OdinShipPatch), nameof(TurretAwake), Priority.Last));
            pending = true;
            CompatibilityInstaller.Info("OdinShip.Core: 0.8.7 build verified; awaiting transpiler verification.");
        }

        internal static void Verify()
        {
            if (!pending) return;
            pending = false;
            if (canoeRewrite != true || !HasHook(0, nameof(Begin), HarmonyPatchType.Prefix)
                || !HasHook(0, nameof(End), HarmonyPatchType.Finalizer) || !HasHook(1, nameof(Canoe), HarmonyPatchType.Prefix)
                || !HasHook(1, nameof(RewriteCanoe), HarmonyPatchType.Transpiler) || !HasHook(2, nameof(ObserveName), HarmonyPatchType.Postfix)
                || !HasHook(3, nameof(TurretAwake), HarmonyPatchType.Postfix))
            {
                new Harmony(Owner).UnpatchSelf();
                frame = null;
                names.Clear();
                nameStates = new ConditionalWeakTable<object, NameState>(); pendingTurrets.Clear();
                CompatibilityInstaller.Error("OdinShip.Core: NOT APPLIED; audited IL did not match; vendor behavior retained.");
                return;
            }
            enabled = true;
            foreach (WeakReference reference in pendingTurrets)
                if (reference.Target is Turret turret && turret != null) TurretAwake(turret);
            pendingTurrets.Clear();
            CompatibilityInstaller.Info("OdinShip.Core: APPLIED; canoe force uses owner/timestep, mounted ammo refunds once, and client nameplates follow authoritative ZDO names.");
        }

        private static bool HasHook(int target, string hook, HarmonyPatchType kind)
        {
            Patches? patches = Harmony.GetPatchInfo(targets[target]);
            if (patches == null) return false;
            IEnumerable<Patch> list = kind == HarmonyPatchType.Prefix ? patches.Prefixes : kind == HarmonyPatchType.Postfix ? patches.Postfixes
                : kind == HarmonyPatchType.Finalizer ? patches.Finalizers : patches.Transpilers;
            return list.Count(p => p.owner == Owner && p.PatchMethod == AccessTools.Method(typeof(OdinShipPatch), hook)) == 1;
        }

        private sealed class Frame
        {
            internal readonly Ship Ship;
            internal readonly float Step;
            internal Frame(Ship ship, float step) { Ship = ship; Step = step; }
        }
        private static void Begin(Ship __instance, float __0, out Frame? __state)
        {
            __state = frame;
            frame = new Frame(__instance, __0);
        }
        private static Exception? End(Exception? __exception, Frame? __state)
        {
            frame = __state;
            return __exception;
        }
        private static bool Canoe(Ship __0) => !enabled ||
            (frame != null && ReferenceEquals(frame.Ship, __0) && shipView(__0) != null && shipView(__0).IsValid() && shipView(__0).IsOwner());
        private static float Step() => frame?.Step ?? throw new InvalidOperationException("OdinShip canoe force has no fixed-update frame.");

        private static IEnumerable<CodeInstruction> RewriteCanoe(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = instructions.ToList();
            MethodInfo getter = AccessTools.PropertyGetter(typeof(Time), nameof(Time.fixedDeltaTime));
            canoeRewrite = code.Count(c => c.Calls(getter)) == 1;
            if (canoeRewrite != true) return code;
            return code.Select(c => c.Calls(getter)
                ? new CodeInstruction(c) { opcode = OpCodes.Call, operand = AccessTools.Method(typeof(OdinShipPatch), nameof(Step)) } : c).ToArray();
        }

        private sealed class NameState
        {
            internal readonly WeakReference Component;
            internal string Name;
            internal bool Warned;
            internal NameState(object component, string name) { Component = new WeakReference(component); Name = name; }
        }
        private static void ObserveName(object __instance)
        {
            if ((!enabled && !pending) || !observeNames) return;
            ZNetView nview = (ZNetView)nameView.GetValue(__instance);
            if (nview == null || !nview.IsValid() || nameStates.TryGetValue(__instance, out _)) return;
            var state = new NameState(__instance, nview.GetZDO().GetString("shipName"));
            nameStates.Add(__instance, state); names.Add(state);
        }
        internal static void Tick()
        {
            if (!enabled || clock.Elapsed.TotalSeconds < nextNameTick) return;
            nextNameTick = clock.Elapsed.TotalSeconds + 0.25;
            for (int i = names.Count - 1; i >= 0; i--)
            {
                object component = names[i].Component.Target!;
                if (component == null || (UnityEngine.Object)component == null) { names.RemoveAt(i); continue; }
                ZNetView nview = (ZNetView)nameView.GetValue(component);
                if (nview == null || !nview.IsValid()) continue;
                string name = nview.GetZDO().GetString("shipName");
                if (name == names[i].Name) continue;
                try { updateNameplates.Invoke(component, null); names[i].Name = name; names[i].Warned = false; }
                catch (Exception error)
                {
                    if (!names[i].Warned) { names[i].Warned = true; CompatibilityInstaller.Warning("OdinShip.Core: nameplate refresh failed. " + error); }
                }
            }
        }

        private static void TurretAwake(Turret __instance)
        {
            if (!enabled)
            {
                if (pending) pendingTurrets.Add(new WeakReference(__instance));
                return;
            }
            if (__instance.name != "warshipturret" || __instance.GetComponent<WearNTear>() != null) return;
            Ship ship = __instance.GetComponentInParent<Ship>();
            if (ship == null || ship.name.Replace("(Clone)", "").Trim() != "WarShip") return;
            ZNetView parentView = ship.GetComponent<ZNetView>();
            if (parentView == null || turretView(__instance) != parentView) return;
            WearNTear parent = ship.GetComponent<WearNTear>();
            if (parent == null) return;
            Action callback = (Action)Delegate.CreateDelegate(typeof(Action), __instance,
                Guard.Method(typeof(Turret), "OnDestroyed", typeof(void)));
            Subscribe(parent, __instance, callback);
        }

        internal static void ResetForTests()
        {
            pending = enabled = false; frame = null; names.Clear(); pendingTurrets.Clear();
            nameStates = new ConditionalWeakTable<object, NameState>(); nextNameTick = 0;
        }

        internal static void Subscribe(WearNTear parent, Turret turret, Action callback)
        {
            if (refunds.TryGetValue(turret, out _)) return;
            // Do not attach a second route if another correct parent subscription already exists.
            if (parent.m_onDestroyed?.GetInvocationList().Contains(callback) == true) return;
            var refund = new Refund(turret, callback);
            refunds.Add(turret, refund);
            parent.m_onDestroyed += refund.Destroyed;
        }
        private sealed class Refund
        {
            private readonly Turret turret;
            private readonly Action callback;
            private bool destroyed;
            internal Refund(Turret turret, Action callback) { this.turret = turret; this.callback = callback; }
            internal void Destroyed()
            {
                if (destroyed) return;
                destroyed = true;
                if (turret == null) return;
                ZNetView nview = turretView(turret);
                if (nview != null && nview.IsValid() && nview.IsOwner() && turret.m_returnAmmoOnDestroy)
                    callback();
            }
        }
    }
}
