using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace DeepNorthCompat
{
    internal static class BetterMapVehiclePatch
    {
        internal const string Owner = Plugin.Guid + ".Map.VehicleUpdates";
        private static BetterMapHooks? hooks;
        private static Func<object, float> heading = null!;
        private static Action<object, float> setHeading = null!;
        private static FieldInfo headingField = null!;
        private static readonly FieldInfo position = BetterMapAccess.RequiredField(typeof(Minimap.PinData), "m_pos", false, typeof(Vector3));
        private static readonly MethodInfo positionHook = AccessTools.DeclaredMethod(typeof(BetterMapVehiclePatch), nameof(Position));
        private static readonly MethodInfo headingHook = AccessTools.DeclaredMethod(typeof(BetterMapVehiclePatch), nameof(Heading));
        private static readonly Dictionary<string, bool> rewrites = new Dictionary<string, bool>();
        private static bool pending, enabled;

        internal static void Prepare(Assembly? assembly)
        {
            if (pending || enabled) return;
            BetterMapAccess? api = BetterMapAccess.Prepare(assembly, "Map.VehicleUpdates", true);
            if (api == null) return;
            Type tracked = api.Type("Vehicles.VehicleTracker+Tracked");
            headingField = BetterMapAccess.RequiredField(tracked, "Heading", false, typeof(float));
            var getter = new DynamicMethod("DNC_BetterMapHeading", typeof(float), new[] { typeof(object) }, typeof(BetterMapVehiclePatch), true);
            ILGenerator code = getter.GetILGenerator(); code.Emit(OpCodes.Ldarg_0); code.Emit(OpCodes.Castclass, tracked); code.Emit(OpCodes.Ldfld, headingField); code.Emit(OpCodes.Ret);
            heading = (Func<object, float>)getter.CreateDelegate(typeof(Func<object, float>));
            var setter = new DynamicMethod("DNC_SetBetterMapHeading", typeof(void), new[] { typeof(object), typeof(float) }, typeof(BetterMapVehiclePatch), true);
            code = setter.GetILGenerator(); code.Emit(OpCodes.Ldarg_0); code.Emit(OpCodes.Castclass, tracked); code.Emit(OpCodes.Ldarg_1); code.Emit(OpCodes.Stfld, headingField); code.Emit(OpCodes.Ret);
            setHeading = (Action<object, float>)setter.CreateDelegate(typeof(Action<object, float>));
            rewrites.Clear(); hooks = new BetterMapHooks(Owner);
            hooks.Add(api.Method("Vehicles.VehicleTracker", "Reconcile", typeof(void)), typeof(BetterMapVehiclePatch), nameof(RedrawReconcile), HarmonyPatchType.Transpiler);
            hooks.Add(api.Method("Vehicles.VehicleTracker", "UpdatePositions", typeof(void)), typeof(BetterMapVehiclePatch), nameof(RedrawPositions), HarmonyPatchType.Transpiler);
            pending = true;
        }
        internal static void Verify()
        {
            if (!pending) return;
            pending = false;
            enabled = hooks!.Complete("Map.VehicleUpdates", rewrites.Count == 2 && rewrites.Values.All(value => value),
                "loaded and remote vehicle position/heading changes request a redraw.");
        }
        // Reconcile writes a pin's position once and a vehicle's heading twice; UpdatePositions writes each once.
        private static IEnumerable<CodeInstruction> RedrawReconcile(IEnumerable<CodeInstruction> instructions) => RedrawChanges(instructions, "Reconcile", 2);
        private static IEnumerable<CodeInstruction> RedrawPositions(IEnumerable<CodeInstruction> instructions) => RedrawChanges(instructions, "UpdatePositions", 1);
        private static IEnumerable<CodeInstruction> RedrawChanges(IEnumerable<CodeInstruction> instructions, string target, int headingWrites)
        {
            var code = instructions.ToList();
            int positions = code.Count(i => i.opcode == OpCodes.Stfld && Equals(i.operand, position));
            int headings = code.Count(i => i.opcode == OpCodes.Stfld && Equals(i.operand, headingField));
            bool match = rewrites[target] = positions == 1 && headings == headingWrites;
            if (!match)
            {
                ILMatch.Lost(ref enabled, "Map.VehicleUpdates", $"VehicleTracker.{target} writes {positions} positions and {headings} headings");
                return code;
            }
            return code.Select(i => i.opcode != OpCodes.Stfld ? i : Equals(i.operand, position) ?
                new CodeInstruction(i) { opcode = OpCodes.Call, operand = positionHook } :
                Equals(i.operand, headingField) ? new CodeInstruction(i) { opcode = OpCodes.Call, operand = headingHook } : i).ToArray();
        }
        private static void Position(Minimap.PinData pin, Vector3 value)
        {
            bool changed = pin.m_pos != value; pin.m_pos = value;
            if (enabled && changed && Minimap.instance != null) BetterMapAccess.Redraw(Minimap.instance);
        }
        private static void Heading(object tracked, float value)
        {
            bool changed = heading(tracked) != value; setHeading(tracked, value);
            if (enabled && changed && Minimap.instance != null) BetterMapAccess.Redraw(Minimap.instance);
        }
    }
}
