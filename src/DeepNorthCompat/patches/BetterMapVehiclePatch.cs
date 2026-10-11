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
            foreach (string name in new[] { "Reconcile", "UpdatePositions" })
                hooks.Add(api.Method("Vehicles.VehicleTracker", name, typeof(void)), typeof(BetterMapVehiclePatch), nameof(RedrawChanges), HarmonyPatchType.Transpiler);
            pending = true;
        }
        internal static void Verify()
        {
            if (!pending) return;
            pending = false;
            enabled = hooks!.Complete("Map.VehicleUpdates", rewrites.Count == 2 && rewrites.Values.All(value => value),
                "loaded and remote vehicle position/heading changes request a redraw.");
        }
        private static IEnumerable<CodeInstruction> RedrawChanges(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var code = instructions.ToList();
            FieldInfo position = BetterMapAccess.RequiredField(typeof(Minimap.PinData), "m_pos", false, typeof(Vector3));
            int positions = code.Count(i => i.opcode == OpCodes.Stfld && Equals(i.operand, position));
            int headings = code.Count(i => i.opcode == OpCodes.Stfld && Equals(i.operand, headingField));
            bool match = positions == 1 && headings == (__originalMethod.Name == "Reconcile" ? 2 : 1);
            rewrites[__originalMethod.Name] = match;
            if (!match) return code;
            return code.Select(i => i.opcode != OpCodes.Stfld ? i : Equals(i.operand, position) ?
                new CodeInstruction(i) { opcode = OpCodes.Call, operand = AccessTools.Method(typeof(BetterMapVehiclePatch), nameof(Position)) } :
                Equals(i.operand, headingField) ? new CodeInstruction(i) { opcode = OpCodes.Call,
                    operand = AccessTools.Method(typeof(BetterMapVehiclePatch), nameof(Heading)) } : i).ToArray();
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
