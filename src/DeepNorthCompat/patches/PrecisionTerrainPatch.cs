using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using static DeepNorthCompat.ILMatch;

namespace DeepNorthCompat
{
    // The precision tools implement their own vertex loop and do not call the game's raise/smooth bodies.
    internal static class PrecisionTerrainPatch
    {
        internal const string Guid = "Searica.Valheim.TerrainTools";
        internal const string UnlimitedGuid = "digitalroot.mods.heightmapunlimitedjvl";
        internal const string AdvancedHeightMapGuid = "Searica.Valheim.AdvancedHeightMap";
        internal const string LimitOwner = Plugin.Guid + ".Terrain.PrecisionLimits";
        internal const string FillOwner = Plugin.Guid + ".Terrain.HearthBelow.Precision";
        private const string TerrainBuild = "7054FE6C688A5C2885135B64F1079478C0547E0DCA4FE9E92FD11971EDF59879";
        private const string UnlimitedBuild = "F7C2C869CA3CAE30309D657984197B700C9E541C6C18D264AE8E422A9969D4A9";
        private const string HearthBuild = "85E2F266C36EC916E714832C48B771F6FA61F80C7360DF095E813E21B372C5E1";
        private static readonly MethodInfo clamp = AccessTools.Method(typeof(Mathf), nameof(Mathf.Clamp), new[] { typeof(float), typeof(float), typeof(float) });
        private static readonly FieldInfo levelDelta = AccessTools.Field(typeof(TerrainComp), "m_levelDelta");
        private static readonly FieldInfo smoothDelta = AccessTools.Field(typeof(TerrainComp), "m_smoothDelta");
        private static readonly FieldInfo modifiedHeight = AccessTools.Field(typeof(TerrainComp), "m_modifiedHeight");
        private static readonly FieldInfo levelSetting = AccessTools.Field(typeof(TerrainOp.Settings), nameof(TerrainOp.Settings.m_level));
        private static readonly MethodInfo raised = Hook(nameof(Raised)), skipped = Hook(nameof(Skipped)), smoothed = Hook(nameof(Smoothed));
        private static readonly MethodInfo precisionShape = Hook(nameof(PrecisionShape)), precisionRadius = Hook(nameof(PrecisionRadius)), precisionFlatten = Hook(nameof(PrecisionFlatten));
        private static readonly MethodInfo precisionDelta = Hook(nameof(PrecisionDelta));
        private static readonly MethodInfo preparePlacement = Hook(nameof(PreparePlacement));
        private static readonly Dictionary<MethodBase, bool> fillResults = new Dictionary<MethodBase, bool>();
        private static MethodInfo min = null!, max = null!, unlimitedMin = null!, unlimitedMax = null!, raise = null!;
        private static MethodInfo voxelRaise = null!, voxelSmooth = null!, hearthAwake = null!, shapeOf = null!, newOp = null!, voxelOperations = null!, flattenAt = null!, clampRaise = null!;
        private static FieldInfo index = null!, opDepth = null!;
        private static Type indexType = null!, indexEnumerator = null!, overlayType = null!, digProfile = null!, voxelOperationType = null!;
        private static Func<float> spinner = null!;
        private static Func<float, float> clampDelta = null!;
        private static Func<Vector3, float, bool, bool> flatten = null!;
        private static bool limitPending, limitEnabled, limitMatched, fillPending, fillEnabled;

        private static void CheckGame() => Guard.Build(typeof(TerrainComp).Assembly, ExpectedBuilds.Valheim1021Client,
            ExpectedBuilds.Valheim1021WindowsServer, ExpectedBuilds.Valheim1021LinuxServer);

        private static Type Terrain(Assembly assembly)
        {
            Guard.Build(assembly, TerrainBuild);
            CheckGame();
            Type operations = Guard.Type(assembly, "TerrainTools.Core.TerrainOperations");
            Type api = Guard.Type(assembly, "AdvancedHeightMap.API.AdvancedHeightMapAPI");
            min = Guard.Method(api, "GetMinHeightChange", typeof(float));
            max = Guard.Method(api, "GetMaxHeightChange", typeof(float));
            raise = Guard.Method(operations, "PreciseRaiseTerrain", typeof(void), typeof(TerrainComp), typeof(Vector3), typeof(float));
            return operations;
        }

        internal static void PrepareLimits(Assembly? terrain, Assembly? unlimited, Assembly? advancedHeightMap)
        {
            new Harmony(LimitOwner).UnpatchSelf();
            limitPending = limitEnabled = limitMatched = false;
            if (terrain == null || unlimited == null || advancedHeightMap != null)
            {
                CompatibilityInstaller.Info("Terrain.PrecisionLimits: optional integration inactive.");
                return;
            }
            Terrain(terrain);
            Guard.Build(unlimited, UnlimitedBuild);
            Type main = Guard.Type(unlimited, "Digitalroot.HeightmapUnlimitedJvL.Main");
            unlimitedMin = Guard.Method(main, "Min", typeof(float));
            unlimitedMax = Guard.Method(main, "Max", typeof(float));
            new Harmony(LimitOwner).Patch(raise, transpiler: Guard.Hook(typeof(PrecisionTerrainPatch), nameof(RewriteLimits)));
            limitPending = true;
        }

        internal static void PrepareHearth(Assembly? terrain, Assembly? hearth)
        {
            new Harmony(FillOwner).UnpatchSelf();
            fillResults.Clear(); fillPending = fillEnabled = false;
            if (terrain == null || hearth == null)
            {
                CompatibilityInstaller.Info("Terrain.HearthBelow.Precision: optional integration inactive.");
                return;
            }
            if (!HearthTerrainPatch.PrecisionDecoderAvailable) throw new InvalidOperationException("HearthBelow repair decoder is unavailable.");
            Guard.Build(hearth, HearthBuild);
            Type operations = Terrain(terrain);
            indexType = Guard.Type(terrain, "TerrainTools.Core.TerrainOperations+HeightIndex");
            indexEnumerator = typeof(IEnumerator<>).MakeGenericType(indexType);
            index = AccessTools.DeclaredField(indexType, "Index");
            if (index == null || index.FieldType != typeof(int)) throw new MissingFieldException(indexType.FullName, "Index");
            MethodInfo smooth = Guard.Method(operations, "PreciseSmoothTerrain", typeof(void), typeof(TerrainComp), typeof(Vector3));
            Type world = Guard.Type(hearth, "HearthBelow.VoxelMagic.VoxelWorld");
            Type op = Guard.Type(hearth, "HearthBelow.VoxelMagic.CarveOp"), opType = Guard.Type(hearth, "HearthBelow.VoxelMagic.VoxelOpType");
            voxelOperationType = op;
            opDepth = AccessTools.DeclaredField(op, "Depth");
            if (opDepth == null || opDepth.FieldType != typeof(float)) throw new MissingFieldException(op.FullName, "Depth");
            Type route = Guard.Type(hearth, "HearthBelow.Patches.TerrainComp_InternalDoOperation_Patch");
            Type placement = Guard.Type(hearth, "HearthBelow.Patches.TerrainOp_Awake_Patch");
            Type groundSpinner = Guard.Type(terrain, "TerrainTools.Core.GroundLevelSpinner");
            overlayType = Guard.Type(terrain, "TerrainTools.Visualization.OverlayVisualizer");
            digProfile = Guard.Type(hearth, "HearthBelow.Digging.DigProfile");
            spinner = (Func<float>)Delegate.CreateDelegate(typeof(Func<float>), Guard.Method(groundSpinner, "get_Value", typeof(float)));
            shapeOf = Guard.Method(world, "ShapeOf", typeof(byte), typeof(bool));
            newOp = Guard.Method(world, "NewOp", op, opType, typeof(byte), typeof(Vector3), typeof(float));
            voxelOperations = Guard.Method(route, "ApplyAsVoxelOps", typeof(void), typeof(Vector3), typeof(TerrainOp.Settings));
            hearthAwake = Guard.Method(placement, "Prefix", typeof(void), typeof(TerrainOp));
            voxelRaise = Guard.Method(world, "RaiseAt", typeof(bool), typeof(Vector3), typeof(float), typeof(float), typeof(float), typeof(bool));
            voxelSmooth = Guard.Method(world, "SmoothAt", typeof(bool), typeof(Vector3), typeof(float), typeof(float));
            flattenAt = Guard.Method(world, "FlattenAt", typeof(bool), typeof(Vector3), typeof(float), typeof(bool));
            flatten = (Func<Vector3, float, bool, bool>)Delegate.CreateDelegate(typeof(Func<Vector3, float, bool, bool>), flattenAt);
            clampRaise = Guard.Method(world, "ClampRaiseDelta", typeof(float), typeof(float));
            clampDelta = (Func<float, float>)Delegate.CreateDelegate(typeof(Func<float, float>), clampRaise);
            var harmony = new Harmony(FillOwner);
            foreach (MethodInfo target in new[] { raise, smooth, voxelRaise, voxelSmooth, hearthAwake }) fillResults.Add(target, false);
            Add(harmony, raise, nameof(RewriteRaise));
            Add(harmony, smooth, nameof(RewriteSmooth));
            harmony.Patch(voxelRaise, transpiler: Guard.Hook(typeof(PrecisionTerrainPatch), nameof(RewriteVoxelRaise)));
            harmony.Patch(voxelSmooth, transpiler: Guard.Hook(typeof(PrecisionTerrainPatch), nameof(RewriteVoxelSmooth)));
            harmony.Patch(hearthAwake, transpiler: Guard.Hook(typeof(PrecisionTerrainPatch), nameof(RewritePlacement)));
            fillPending = true;
        }

        private static void Add(Harmony harmony, MethodInfo target, string rewrite)
        {
            harmony.Patch(target, prefix: Guard.Hook(typeof(PrecisionTerrainPatch), nameof(Begin)),
                transpiler: Guard.Hook(typeof(PrecisionTerrainPatch), rewrite), finalizer: Guard.Hook(typeof(PrecisionTerrainPatch), nameof(End)));
        }

        internal static void Verify()
        {
            if (limitPending)
            {
                limitPending = false;
                limitEnabled = limitMatched && Harmony.GetPatchInfo(raise)?.Transpilers.Count(p => p.owner == LimitOwner) == 1;
                if (limitEnabled) CompatibilityInstaller.Info("Terrain.PrecisionLimits: APPLIED; precision raising uses Heightmap Unlimited JvL limits.");
                else
                {
                    new Harmony(LimitOwner).UnpatchSelf();
                    CompatibilityInstaller.Error("Terrain.PrecisionLimits: NOT APPLIED; precision raise IL or registration changed.");
                }
            }
            if (!fillPending) return;
            fillPending = false;
            fillEnabled = HearthTerrainPatch.PrecisionReady && fillResults.Count == 5 && fillResults.All(pair => pair.Value && Registered(pair.Key));
            if (fillEnabled) CompatibilityInstaller.Info("Terrain.HearthBelow.Precision: APPLIED; precision tools also fill pickaxe cuts.");
            else
            {
                new Harmony(FillOwner).UnpatchSelf();
                CompatibilityInstaller.Error("Terrain.HearthBelow.Precision: NOT APPLIED; decoder, writer, or precision IL verification failed.");
            }
        }

        private static bool Registered(MethodBase method)
        {
            Patches? info = Harmony.GetPatchInfo(method);
            int wrapping = method == voxelRaise || method == voxelSmooth || method == hearthAwake ? 0 : 1;
            return info != null && info.Prefixes.Count(p => p.owner == FillOwner) == wrapping
                && info.Transpilers.Count(p => p.owner == FillOwner) == 1 && info.Finalizers.Count(p => p.owner == FillOwner) == wrapping;
        }

        private static IEnumerable<CodeInstruction> RewriteLimits(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> original = instructions.ToList(), code = original.Select(i => new CodeInstruction(i)).ToList();
            int site = Unique(code, i => code[i].Calls(min));
            limitMatched = site >= 0 && Shape(code, site, Calls(min), Calls(max))
                && code.Count(i => i.Calls(max)) == 1;
            if (!limitMatched) { Lost(ref limitEnabled, "Terrain.PrecisionLimits", "precision limits changed"); return original; }
            code[site].operand = unlimitedMin;
            code[site + 1].operand = unlimitedMax;
            return code;
        }

        private static IEnumerable<CodeInstruction> RewriteRaise(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) =>
            Transpile(instructions, __originalMethod, code =>
            {
                int site = Site(code, i => code[i].Calls(clamp));
                int skip = Site(code, i => Shape(code, i - 4, IsLoad(1), Is(OpCodes.Ldarg_2), Is(OpCodes.Add), IsStore(3), Is(OpCodes.Ldarg_2)));
                if (!Shape(code, site - 11, Is(OpCodes.Ldarg_0), Loads(levelDelta), IsLoad(6), Loads(index),
                        Is(OpCodes.Ldarg_0), Loads(levelDelta), IsLoad(6), Loads(index), Is(OpCodes.Ldelem_R4), IsMinimum, IsMaximum,
                        Calls(clamp), Is(OpCodes.Stelem_R4), Is(OpCodes.Ldarg_0), Loads(modifiedHeight), IsLoad(6), Loads(index), Is(OpCodes.Ldc_I4_1), Is(OpCodes.Stelem_I1))
                    || skip < 0 || skip >= site || !PrecisionLocals(__originalMethod)) return false;
                code[site].operand = raised;
                code.InsertRange(site, new[] { new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldarg_1), new CodeInstruction(OpCodes.Ldarg_2),
                    Load(6), new CodeInstruction(OpCodes.Ldfld, index), Load(3) });
                code.InsertRange(skip, new[] { new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldarg_1), new CodeInstruction(OpCodes.Ldarg_2),
                    Load(6), new CodeInstruction(OpCodes.Ldfld, index), Load(2), Load(3), new CodeInstruction(OpCodes.Call, skipped) });
                return true;
            });

        private static IEnumerable<CodeInstruction> RewriteSmooth(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) =>
            Transpile(instructions, __originalMethod, code =>
            {
                int site = Site(code, i => code[i].Calls(clamp));
                if (!Shape(code, site - 9, Is(OpCodes.Ldarg_0), Loads(smoothDelta), IsLoad(6), Loads(index), IsLoad(4), IsLoad(3),
                        Is(OpCodes.Add), NegativeOne, PositiveOne, Calls(clamp), Is(OpCodes.Stelem_R4),
                        Is(OpCodes.Ldarg_0), Loads(modifiedHeight), IsLoad(6), Loads(index), Is(OpCodes.Ldc_I4_1), Is(OpCodes.Stelem_I1))
                    || !PrecisionLocals(__originalMethod)) return false;
                code[site].operand = smoothed;
                code.InsertRange(site, new[] { new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldarg_1), Load(6),
                    new CodeInstruction(OpCodes.Ldfld, index), Load(2), Load(4) });
                return true;
            });

        // A negative-infinity radius is ATM's precision sentinel. Preserve HearthBelow's
        // operation and calculations, but give its writer the precision square footprint.
        private static IEnumerable<CodeInstruction> RewriteVoxelRaise(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) =>
            Transpile(instructions, __originalMethod, code =>
            {
                int zero = Unique(code, i => code[i].Calls(flattenAt));
                int delta = Site(code, i => code[i].Calls(clampRaise));
                if (!Shape(code, zero - 3, Is(OpCodes.Ldarg_0), Is(OpCodes.Ldarg_1), Arg4, Calls(flattenAt), Is(OpCodes.Ret))
                    || !Shape(code, delta - 1, Is(OpCodes.Ldarg_2), Calls(clampRaise), i => i.StoresField(opDepth))
                    || !Locals(__originalMethod, 1, (0, voxelOperationType))) return false;
                code[zero].operand = precisionFlatten;
                code[delta].operand = precisionDelta;
                code.Insert(delta, new CodeInstruction(OpCodes.Ldarg_1));
                if (!RewriteVoxelFootprint(code, Is(OpCodes.Ldc_I4_5), Arg4)) return false;
                return true;
            });

        private static IEnumerable<CodeInstruction> RewriteVoxelSmooth(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) =>
            Transpile(instructions, __originalMethod, code =>
                RewriteVoxelFootprint(code, Is(OpCodes.Ldc_I4_4), Is(OpCodes.Ldc_I4_0)));

        private static bool RewriteVoxelFootprint(List<CodeInstruction> code, Func<CodeInstruction, bool> kind, Func<CodeInstruction, bool> square)
        {
            int site = Site(code, i => code[i].Calls(shapeOf));
            if (!Shape(code, site - 2, kind, square, Calls(shapeOf), Is(OpCodes.Ldarg_0), Is(OpCodes.Ldarg_1),
                    Half, Six, Calls(clamp), Calls(newOp)) || !Plain(code[site + 3])) return false;
            code.Insert(site + 3, new CodeInstruction(OpCodes.Call, precisionRadius));
            code.InsertRange(site, new[] { new CodeInstruction(OpCodes.Ldarg_1), new CodeInstruction(OpCodes.Call, precisionShape) });
            return true;
        }

        // The local placement route runs before ATM's deserializer marks precision settings.
        private static IEnumerable<CodeInstruction> RewritePlacement(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) =>
            Transpile(instructions, __originalMethod, code =>
            {
                int site = Site(code, i => code[i].Calls(voxelOperations));
                if (!Shape(code, site - 2, IsLoad(0), IsLoad(1), Calls(voxelOperations), IsLoad(1), Is(OpCodes.Ldc_I4_0),
                        i => i.StoresField(levelSetting))
                    || !Locals(__originalMethod, 3, (0, typeof(Vector3)), (1, typeof(TerrainOp.Settings)), (2, digProfile))) return false;
                code.InsertRange(site, new[] { new CodeInstruction(OpCodes.Dup), new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Call, preparePlacement) });
                return true;
            });

        private static bool PrecisionLocals(MethodBase method) => Locals(method, 7, (0, typeof(int)), (1, typeof(float)), (2, typeof(float)),
            (3, typeof(float)), (4, typeof(float)), (5, indexEnumerator), (6, indexType));
        private static bool IsMinimum(CodeInstruction instruction) => instruction.Calls(min) || unlimitedMin != null && instruction.Calls(unlimitedMin);
        private static bool IsMaximum(CodeInstruction instruction) => instruction.Calls(max) || unlimitedMax != null && instruction.Calls(unlimitedMax);
        private static bool NegativeOne(CodeInstruction instruction) => instruction.opcode == OpCodes.Ldc_R4 && Equals(instruction.operand, -1f);
        private static bool PositiveOne(CodeInstruction instruction) => instruction.opcode == OpCodes.Ldc_R4 && Equals(instruction.operand, 1f);
        private static bool Half(CodeInstruction instruction) => instruction.opcode == OpCodes.Ldc_R4 && Equals(instruction.operand, .5f);
        private static bool Six(CodeInstruction instruction) => instruction.opcode == OpCodes.Ldc_R4 && Equals(instruction.operand, 6f);
        private static bool Arg4(CodeInstruction instruction) => instruction.opcode == OpCodes.Ldarg_S
            && (instruction.operand is ParameterInfo parameter ? parameter.Position == 4 : Equals(instruction.operand, (byte)4));
        private static CodeInstruction Load(int slot) => new CodeInstruction(OpCodes.Ldloc_S, (byte)slot);
        private static MethodInfo Hook(string name) => AccessTools.DeclaredMethod(typeof(PrecisionTerrainPatch), name);

        private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions, MethodBase method, Func<List<CodeInstruction>, bool> edit)
        {
            List<CodeInstruction> original = instructions.ToList(), code = original.Select(i => new CodeInstruction(i)).ToList();
            bool matched = fillResults[method] = edit(code);
            if (matched) return code;
            Lost(ref fillEnabled, "Terrain.HearthBelow.Precision", "precision tool IL changed on " + method.Name);
            return original;
        }

        private static float Raised(float value, float minimum, float maximum, TerrainComp comp, Vector3 target, float step, int vertex, float height)
        {
            float result = Mathf.Clamp(value, minimum, maximum);
            if (Ready) HearthTerrainPatch.PrecisionRaise(comp, target, step, vertex, comp.transform.position.y + height + result - value);
            return result;
        }

        private static void Skipped(TerrainComp comp, Vector3 target, float step, int vertex, float height, float requested)
        {
            if (Ready && requested < height) HearthTerrainPatch.PrecisionRaise(comp, target, step, vertex, comp.transform.position.y + height);
        }

        private static float Smoothed(float value, float minimum, float maximum, TerrainComp comp, Vector3 target, int vertex, float height, float previous)
        {
            float result = Mathf.Clamp(value, minimum, maximum);
            if (Ready) HearthTerrainPatch.PrecisionSmooth(comp, target, vertex, comp.transform.position.y + height + result - previous);
            return result;
        }

        private static bool Ready => fillEnabled && HearthTerrainPatch.PrecisionReady;
        private static bool PrecisionShape(bool square, float radius) => square || Ready && float.IsNegativeInfinity(radius);
        private static float PrecisionRadius(float radius) => Ready && float.IsNegativeInfinity(radius) ? 1f : radius;
        private static bool PrecisionFlatten(Vector3 point, float radius, bool square) =>
            Ready && float.IsNegativeInfinity(radius) ? false : flatten(point, radius, square);
        private static float PrecisionDelta(float delta, float radius) =>
            Ready && float.IsNegativeInfinity(radius) && delta > 0f && delta < .1f ? delta : clampDelta(delta);

        private static void PreparePlacement(TerrainOp.Settings settings, TerrainOp operation)
        {
            if (!Ready || !operation.GetComponentInChildren(overlayType)) return;
            if (settings.m_smooth) settings.m_smoothRadius = float.NegativeInfinity;
            if (settings.m_raise && settings.m_raiseDelta >= 0f)
            {
                settings.m_raiseRadius = float.NegativeInfinity;
                settings.m_raiseDelta = spinner();
            }
        }

        private static void Begin(out bool __state)
        {
            __state = Ready;
            if (__state) HearthTerrainPatch.Begin();
        }

        private static Exception? End(Exception? __exception, bool __state) =>
            __state ? HearthTerrainPatch.End(__exception) : __exception;
    }
}
