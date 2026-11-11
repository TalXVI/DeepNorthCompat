using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using static DeepNorthCompat.ILMatch;

namespace DeepNorthCompat
{
    internal static class SteadyFrameStructurePatch
    {
        internal const string Name = "SteadyFrame.Structures";
        internal const string Owner = Plugin.Guid + "." + Name;
        internal const string Guid = "ru.ivest.steadyframe";
        internal const string VpoGuid = "dev.ontrigger.vpo";
        private static readonly MethodInfo rewrite = AccessTools.DeclaredMethod(typeof(SteadyFrameStructurePatch), nameof(Rewrite));
        private static readonly MethodInfo getEnumerator = Guard.Method(typeof(List<ZDO>), "GetEnumerator", typeof(List<ZDO>.Enumerator));
        private static readonly MethodInfo currentGetter = Guard.Method(typeof(List<ZDO>.Enumerator), "get_Current", typeof(ZDO));
        private static readonly MethodInfo moveNext = Guard.Method(typeof(List<ZDO>.Enumerator), "MoveNext", typeof(bool));
        private static MethodInfo target = null!, vpoPrefix = null!, pendingReadMethod = null!, queueGetEnumerator = null!, queueCurrentGetter = null!, queueMoveNext = null!;
        private static FieldInfo pendingField = null!, queueField = null!, zdoField = null!;
        private static Type queueEnumeratorType = null!;
        private static bool pending, enabled, rewritten;

        internal static void Prepare(Assembly? steadyFrame, Assembly? vpo)
        {
            new Harmony(Owner).UnpatchSelf();
            pending = enabled = rewritten = false;

            if (steadyFrame == null || vpo == null)
            {
                CompatibilityInstaller.Info($"{Name}: optional mod absent; inactive.");
                return;
            }

            if (Guard.KnownServerBuild())
            {
                CompatibilityInstaller.Info($"{Name}: client-only; inactive on dedicated server.");
                return;
            }

            Guard.Build(typeof(ZNetScene).Assembly, ExpectedBuilds.Valheim1021Client);
            Guard.Build(steadyFrame, ExpectedBuilds.SteadyFrame131);
            Guard.Build(vpo, ExpectedBuilds.VpoGrassAndStreaming);
            Type guard = Guard.Type(steadyFrame, "SteadyFrame.Structures.StructureGuard");
            Type management = Guard.Type(vpo, "ValheimPerformanceOptimizations.Patches.ObjectManagement.ZNetSceneObjectManagementPatch");
            Type entry = Guard.Type(vpo, management.FullName + "+QueuedZDO");
            Type queueType = typeof(List<>).MakeGenericType(entry);
            queueEnumeratorType = queueType.GetNestedType("Enumerator")!.MakeGenericType(entry);
            target = Guard.Method(guard, "Collect", typeof(void));
            Guard.Method(guard, "MayCheck", typeof(bool), typeof(WearNTear), typeof(float));
            vpoPrefix = Guard.Method(management, "ZNetScene_CreateDestroyObjects_Prefix", typeof(bool), typeof(ZNetScene));
            pendingField = Field(guard, "Pending", typeof(AccessTools.FieldRef<ZNetScene, List<ZDO>>));
            pendingReadMethod = Guard.Method(pendingField.FieldType, "Invoke", typeof(List<ZDO>).MakeByRefType(), typeof(ZNetScene));
            queueField = Field(management, "QueuedNearObjects", queueType);
            zdoField = Field(entry, "ZDO", typeof(ZDO));
            queueGetEnumerator = Guard.Method(queueType, "GetEnumerator", queueEnumeratorType);
            queueCurrentGetter = Guard.Method(queueEnumeratorType, "get_Current", entry);
            queueMoveNext = Guard.Method(queueEnumeratorType, "MoveNext", typeof(bool));

            if (!target.IsStatic || !vpoPrefix.IsStatic || !pendingField.IsStatic || !queueField.IsStatic || zdoField.IsStatic || !entry.IsValueType)
                throw new NotSupportedException("SteadyFrame/VPO collector layout changed.");

            new Harmony(Owner).Patch(target, transpiler: new HarmonyMethod(rewrite));
            pending = true;
        }

        private static FieldInfo Field(Type type, string name, Type expected)
        {
            FieldInfo? field = AccessTools.DeclaredField(type, name);
            if (field == null || field.FieldType != expected) throw new MissingFieldException(type.FullName, name);
            return field;
        }

        internal static void Verify()
        {
            if (!pending) return;
            pending = false;

            var vendor = Harmony.GetPatchInfo(AccessTools.Method(typeof(ZNetScene), "CreateDestroyObjects"));
            int vendorCount = vendor?.Prefixes.Count(p => p.owner == VpoGuid && p.PatchMethod == vpoPrefix) ?? 0;
            if (vendorCount == 0)
            {
                new Harmony(Owner).UnpatchSelf();
                CompatibilityInstaller.Info($"{Name}: VPO object management is inactive; native collector retained.");
                return;
            }

            var info = Harmony.GetPatchInfo(target);
            if (vendorCount != 1 || !rewritten || info == null || info.Transpilers.Count(p => p.owner == Owner && p.PatchMethod == rewrite) != 1
                || info.Prefixes.Concat(info.Postfixes).Concat(info.Finalizers).Concat(info.ILManipulators).Any(p => p.owner == Owner)
                || info.Transpilers.Count(p => p.owner == Owner) != 1)
            {
                new Harmony(Owner).UnpatchSelf();
                CompatibilityInstaller.Error($"{Name}: NOT APPLIED; collector IL or hook registration changed; native collector retained.");
                return;
            }

            enabled = true;
            CompatibilityInstaller.Info($"{Name}: APPLIED; the existing structure hold reads VPO's pending near objects.");
        }

        // Keep the vendor's once-per-frame collection, !Created/prefab filters, sectors and hold rules.
        // Only its list and struct enumerator change; no list copy or per-entry reflection is needed.
        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod, ILGenerator generator)
        {
            List<CodeInstruction> original = instructions.ToList();
            List<CodeInstruction> code = original.Select(i => new CodeInstruction(i)).ToList();
            int sourceSite = Site(code, i => Shape(code, i, Loads(pendingField), IsLoad(0), Calls(pendingReadMethod), Is(OpCodes.Ldind_Ref), IsStore(1)));
            int itemSite = Site(code, i => Shape(code, i - 2, Address(2), Calls(currentGetter), IsStore(3)));
            // Nopping the field-ref read must not leave an incoming branch inside that stack sequence.
            rewritten = sourceSite >= 0 && itemSite >= 0 && Shape(code, sourceSite + 1, Plain, Plain, Plain, Plain)
                && Locals(__originalMethod, 5, (0, typeof(ZNetScene)), (1, typeof(List<ZDO>)), (2, typeof(List<ZDO>.Enumerator)), (3, typeof(ZDO)), (4, typeof(Vector2s)))
                && code.Count(IsLoad(1)) == 2 && code.Count(IsStore(1)) == 1 && code.Count(IsStore(2)) == 1 && code.Count(Address(2)) == 3
                && code.Count(Calls(getEnumerator)) == 1 && code.Count(Calls(currentGetter)) == 1 && code.Count(Calls(moveNext)) == 1
                && code.Count(i => i.opcode == OpCodes.Constrained && Equals(i.operand, typeof(List<ZDO>.Enumerator))) == 1;

            if (!rewritten)
            {
                Lost(ref enabled, Name, "the pending-object collector no longer matches");
                return original;
            }

            LocalBuilder list = generator.DeclareLocal(queueField.FieldType), iterator = generator.DeclareLocal(queueEnumeratorType);
            foreach (CodeInstruction instruction in code)
            {
                if (IsLoad(1)(instruction)) { instruction.opcode = OpCodes.Ldloc; instruction.operand = list; }
                else if (IsStore(1)(instruction)) { instruction.opcode = OpCodes.Stloc; instruction.operand = list; }
                else if (IsStore(2)(instruction)) { instruction.opcode = OpCodes.Stloc; instruction.operand = iterator; }
                else if (Address(2)(instruction)) { instruction.opcode = OpCodes.Ldloca; instruction.operand = iterator; }
                else if (instruction.Calls(getEnumerator)) instruction.operand = queueGetEnumerator;
                else if (instruction.Calls(currentGetter)) instruction.operand = queueCurrentGetter;
                else if (instruction.Calls(moveNext)) instruction.operand = queueMoveNext;
                else if (instruction.opcode == OpCodes.Constrained && Equals(instruction.operand, typeof(List<ZDO>.Enumerator))) instruction.operand = queueEnumeratorType;
            }

            code[sourceSite].operand = queueField;
            foreach (CodeInstruction instruction in code.Skip(sourceSite + 1).Take(3)) { instruction.opcode = OpCodes.Nop; instruction.operand = null; }
            code.Insert(itemSite, new CodeInstruction(OpCodes.Ldfld, zdoField));
            return code;
        }

        private static Func<CodeInstruction, bool> Address(int index) => instruction =>
            (instruction.opcode == OpCodes.Ldloca || instruction.opcode == OpCodes.Ldloca_S)
            && (instruction.operand is LocalVariableInfo local ? local.LocalIndex : Convert.ToInt32(instruction.operand)) == index;
    }
}
