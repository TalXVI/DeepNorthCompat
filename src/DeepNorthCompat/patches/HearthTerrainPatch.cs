using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using static DeepNorthCompat.ILMatch;

namespace DeepNorthCompat
{
    // Surface edits can miss HearthBelow's underground routing: their Y is the intended surface,
    // not the pickaxe cut. Keep the heightmap edit and save a fill record for each cut column.
    internal static class HearthTerrainPatch
    {
        internal const string Guid = "Azumatt.HearthBelow";
        internal const string RuneGuid = "hyleanlegend.RuneMagic";
        internal const string Owner = Plugin.Guid + ".Terrain.HearthBelow";
        internal const string ReplayOwner = Owner + ".Replay";
        internal const string HearthBuild = "99AB21C3D0923E1D7D4ED76B7EE14F76414E10CC81523BDBB1A3274A5DBCEB01";
        internal const string RuneBuild = "618B66574C52F5E9C953BF75066A21C0542C4AF1814616AF0527C3C548123856";
        // A fill record is a native operation with an unassigned type. HearthBelow saves, merges, restores,
        // and clears it like its own records, and replays it as a no-op without this decoder.
        // Sequence holds "DNC" and the format version above the offset's float bits.
        private const byte RepairType = 200;
        private const long RepairTag = 0x444E4301L;
        private static readonly Dictionary<MethodBase, bool> rewrites = new Dictionary<MethodBase, bool>(), replays = new Dictionary<MethodBase, bool>();
        private static readonly AccessTools.FieldRef<TerrainComp, Heightmap> heightmap = AccessTools.FieldRefAccess<TerrainComp, Heightmap>("m_hmap");
        private static readonly AccessTools.FieldRef<TerrainComp, ZNetView> netView = AccessTools.FieldRefAccess<TerrainComp, ZNetView>("m_nview");
        private static readonly AccessTools.FieldRef<TerrainComp, float[]> smoothDelta = AccessTools.FieldRefAccess<TerrainComp, float[]>("m_smoothDelta");
        private static readonly AccessTools.FieldRef<TerrainComp, int> compWidth = AccessTools.FieldRefAccess<TerrainComp, int>("m_width");
        private static readonly MethodInfo clamp = AccessTools.Method(typeof(Mathf), nameof(Mathf.Clamp), new[] { typeof(float), typeof(float), typeof(float) });
        private static readonly MethodInfo lerp = AccessTools.Method(typeof(Mathf), nameof(Mathf.Lerp), new[] { typeof(float), typeof(float), typeof(float) });
        private static readonly FieldInfo levelDelta = AccessTools.Field(typeof(TerrainComp), "m_levelDelta");
        private static readonly FieldInfo modifiedHeight = AccessTools.Field(typeof(TerrainComp), "m_modifiedHeight");
        private static readonly FieldInfo vectorY = AccessTools.Field(typeof(Vector3), nameof(Vector3.y));
        private static readonly MethodInfo deltaItem = AccessTools.PropertyGetter(typeof(Dictionary<TerrainComp, float[]>), "Item");
        private static readonly MethodInfo heightItem = AccessTools.PropertyGetter(typeof(Dictionary<TerrainComp, bool[]>), "Item");
        private static readonly MethodInfo levelVertex = Hook(nameof(LevelVertex)), raiseVertex = Hook(nameof(RaiseVertex)), raiseSkipped = Hook(nameof(RaiseSkipped));
        private static readonly MethodInfo smoothVertex = Hook(nameof(SmoothVertex)), foundationVertex = Hook(nameof(FoundationVertex));
        private static readonly MethodInfo repairBounds = Hook(nameof(RepairBounds)), repairDensity = Hook(nameof(RepairDensity));
        private static MethodInfo getZone = null!, sync = null!, zoneKey = null!, newId = null!, bounds = null!, applyDensity = null!;
        private static MethodInfo ensureDensity = null!, rush = null!, read = null!, serialize = null!;
        // Save and Encode run once per filled vertex, so records are built, read, and applied through compiled delegates.
        private static Func<Vector3, float, long, object> encode = null!;
        private static Func<object, Vector3> pointOf = null!;
        private static Action<object, int> setId = null!;
        private static Func<object, object, bool> applyOp = null!;
        private static Action<Heightmap, object> dirtyNeighbors = null!;
        // Replay reads a zone's layout for every fill record, so it uses typed accessors instead of reflection.
        private static AccessTools.FieldRef<object, float[]> density = null!;
        private static AccessTools.FieldRef<object, Vector3> origin = null!;
        private static AccessTools.FieldRef<object, float> spacing = null!;
        private static AccessTools.FieldRef<object, int> nx = null!, ny = null!, nz = null!, ownedX = null!, ownedZ = null!;
        private static Func<object, int, int, float> heightAt = null!;
        private static FieldInfo opSequence = null!, opType = null!, opPoint = null!, floorHeight = null!;
        private static FieldInfo cap = null!, holder = null!, levelDeltas = null!, modifiedHeights = null!;
        // FoundationVertex reads the rune's baseline heights for every selected vertex.
        private static AccessTools.FieldRef<object, Dictionary<TerrainComp, float[]>> bases = null!;
        private static int dataKey;
        private static bool pending, enabled, replayPending, replayEnabled, applying;
        private static readonly HashSet<long> full = new HashSet<long>();
        private static ToolAction? action;
        private static int depth;

        // One tool call is one action. Its records share an Id and one save, so native
        // removal takes the whole action and the operation limit admits all of it or none.
        private sealed class ToolAction
        {
            internal readonly TerrainComp Comp;
            internal readonly object? Zone;
            // The zone's layout and samples stay fixed until Save, so each vertex reads them from here.
            internal readonly VoxelGrid Grid;
            internal readonly float[]? Density;
            internal readonly List<object> Records = new List<object>();
            internal ToolAction(TerrainComp comp, object? zone)
            {
                Comp = comp; Zone = zone;
                if (zone == null) return;
                Grid = new VoxelGrid(zone); Density = Grid.Density;
            }
        }

        // Saved data must remain readable even when a tool integration rejects changed IL.
        internal static void PrepareReplay(Assembly? hearth)
        {
            new Harmony(ReplayOwner).UnpatchSelf();
            replays.Clear(); replayPending = replayEnabled = enabled = false;
            if (hearth == null) { CompatibilityInstaller.Info("Terrain.HearthBelow.Replay: optional mod absent; inactive."); return; }
            Guard.Build(hearth, HearthBuild);
            Guard.Build(typeof(TerrainComp).Assembly,
                "25A0A107DCE4D834C44C2B72D0EAFD5CB7793933BDA81816ACCFA1EA9543DACE",
                "0DFC7E81436F822121148EFED859BA1E661D58B9484BD45E3B6F33B6DD86BFAD",
                "E7220DC5D9CF9D38270E751352D94918308E59CCA86E56C4BDA0210016C847FA",
                "94998885B103D6E5F5480123EAEE0A093E82AF2DDEEEE37536C15BF555F24F3A",
                "EF8C8D3A1F45694EF7A701E8E2A25BF8A3874C951A2639C81DFE614CE3AA5FA3",
                "C36B81C1733C48D00DD567E41010E6DC95F8DAC877E0701CDB6B8C213ADE7812");
            Type world = Guard.Type(hearth, "HearthBelow.VoxelMagic.VoxelWorld"), zone = Guard.Type(hearth, "HearthBelow.VoxelMagic.VoxelZone"),
                op = Guard.Type(hearth, "HearthBelow.VoxelMagic.CarveOp"), data = Guard.Type(hearth, "HearthBelow.VoxelMagic.CarveData"),
                plugin = Guard.Type(hearth, "HearthBelow.HearthBelowPlugin");
            Type list = typeof(List<>).MakeGenericType(op);
            if (!op.IsValueType) throw new NotSupportedException("HearthBelow CarveOp is no longer a value type.");
            getZone = Guard.Method(world, "GetZone", zone, typeof(Heightmap));
            sync = Guard.Method(world, "TryApplyData", typeof(bool), typeof(TerrainComp), typeof(long));
            zoneKey = Guard.Method(world, "ZoneKey", typeof(long), typeof(Vector3));
            newId = Guard.Method(world, "NewOpId", typeof(int));
            MethodInfo dirty = Guard.Method(world, "DirtyNeighborsForOp", typeof(void), typeof(Heightmap), op);
            Type integerRef = typeof(int).MakeByRefType();
            bounds = Guard.Method(zone, "ComputeOpBounds", typeof(bool), op, typeof(int), typeof(int), typeof(int), typeof(int), typeof(bool),
                integerRef, integerRef, integerRef, integerRef, integerRef, integerRef);
            applyDensity = Guard.Method(zone, "ApplyOpDensity", typeof(bool), op, typeof(int), typeof(int), typeof(int), typeof(int), typeof(bool));
            ensureDensity = Guard.Method(zone, "EnsureDensity", typeof(void));
            MethodInfo height = Guard.Method(zone, "H", typeof(float), typeof(int), typeof(int));
            var call = new DynamicMethod("DNC_HearthHeight", typeof(float), new[] { typeof(object), typeof(int), typeof(int) }, typeof(HearthTerrainPatch), true);
            ILGenerator il = call.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, zone); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Call, height); il.Emit(OpCodes.Ret);
            heightAt = (Func<object, int, int, float>)call.CreateDelegate(typeof(Func<object, int, int, float>));
            MethodInfo apply = Guard.Method(zone, "ApplyOp", typeof(bool), op);
            rush = Guard.Method(zone, "Rush", typeof(void));
            read = Guard.Method(data, "Read", Guard.Type(hearth, "HearthBelow.VoxelMagic.CarveDataState"), typeof(byte[]), list.MakeByRefType());
            serialize = Guard.Method(data, "Serialize", typeof(byte[]), list);
            dataKey = (int)Field(data, "ZdoKey", typeof(int)).GetValue(null);
            cap = Field(plugin, "MaxOpsPerZone", typeof(ConfigEntry<int>));
            density = Ref<float[]>(zone, "Density"); origin = Ref<Vector3>(zone, "Origin");
            nx = Ref<int>(zone, "NX"); ny = Ref<int>(zone, "NY"); nz = Ref<int>(zone, "NZ");
            spacing = Ref<float>(zone, "HorizontalSpacing");
            ownedX = Ref<int>(zone, "_ownedMaximumX"); ownedZ = Ref<int>(zone, "_ownedMaximumZ");
            FieldInfo opId = Field(op, "Id", typeof(int)), opRadius = Field(op, "Radius", typeof(float));
            opSequence = Field(op, "Sequence", typeof(long)); opType = Field(op, "Type", typeof(byte));
            opPoint = Field(op, "Point", typeof(Vector3)); floorHeight = Field(op, "FloorHeight", typeof(float));
            encode = Emit<Func<Vector3, float, long, object>>("DNC_HearthEncode", typeof(object), new[] { typeof(Vector3), typeof(float), typeof(long) }, il =>
            {
                LocalBuilder record = il.DeclareLocal(op);
                il.Emit(OpCodes.Ldloca_S, record); il.Emit(OpCodes.Initobj, op);
                void Store(FieldInfo field, Action value) { il.Emit(OpCodes.Ldloca_S, record); value(); il.Emit(OpCodes.Stfld, field); }
                Store(opType, () => il.Emit(OpCodes.Ldc_I4, (int)RepairType));
                Store(opPoint, () => il.Emit(OpCodes.Ldarg_0));
                Store(opRadius, () => il.Emit(OpCodes.Ldc_R4, 0.5f));
                Store(floorHeight, () => il.Emit(OpCodes.Ldarg_1));
                Store(opSequence, () => il.Emit(OpCodes.Ldarg_2));
                il.Emit(OpCodes.Ldloc, record); il.Emit(OpCodes.Box, op); il.Emit(OpCodes.Ret);
            });
            pointOf = Emit<Func<object, Vector3>>("DNC_HearthPoint", typeof(Vector3), new[] { typeof(object) }, il =>
            { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Unbox, op); il.Emit(OpCodes.Ldfld, opPoint); il.Emit(OpCodes.Ret); });
            setId = Emit<Action<object, int>>("DNC_HearthSetId", typeof(void), new[] { typeof(object), typeof(int) }, il =>
            { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Unbox, op); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Stfld, opId); il.Emit(OpCodes.Ret); });
            applyOp = Emit<Func<object, object, bool>>("DNC_HearthApply", typeof(bool), new[] { typeof(object), typeof(object) }, il =>
            { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, zone); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Unbox_Any, op); il.Emit(OpCodes.Callvirt, apply); il.Emit(OpCodes.Ret); });
            dirtyNeighbors = Emit<Action<Heightmap, object>>("DNC_HearthDirty", typeof(void), new[] { typeof(Heightmap), typeof(object) }, il =>
            { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Unbox_Any, op); il.Emit(OpCodes.Call, dirty); il.Emit(OpCodes.Ret); });
            var harmony = new Harmony(ReplayOwner);
            replays.Add(bounds, false); replays.Add(applyDensity, false);
            harmony.Patch(bounds, transpiler: Guard.Hook(typeof(HearthTerrainPatch), nameof(RewriteBounds)));
            harmony.Patch(applyDensity, transpiler: Guard.Hook(typeof(HearthTerrainPatch), nameof(RewriteDensity)));
            replayPending = true;
        }

        internal static void Prepare(Assembly? hearth, Assembly? rune)
        {
            new Harmony(Owner).UnpatchSelf();
            rewrites.Clear(); pending = enabled = false;
            depth = 0; action = null;
            if (hearth == null) { CompatibilityInstaller.Info("Terrain.HearthBelow: optional mod absent; inactive."); return; }
            Guard.Build(hearth, HearthBuild);
            if (!replayPending && !replayEnabled) throw new InvalidOperationException("HearthBelow repair decoder is unavailable.");
            MethodInfo level = Guard.Method(typeof(TerrainComp), "LevelTerrain", typeof(void), typeof(Vector3), typeof(float), typeof(bool));
            MethodInfo raise = Guard.Method(typeof(TerrainComp), "RaiseTerrain", typeof(void), typeof(Vector3), typeof(float), typeof(float), typeof(bool), typeof(float));
            MethodInfo smooth = Guard.Method(typeof(TerrainComp), "SmoothTerrain", typeof(void), typeof(Vector3), typeof(float), typeof(bool), typeof(float));
            MethodInfo? adjust = null;
            if (rune != null)
            {
                Guard.Build(rune, RuneBuild);
                Type foundation = Guard.Type(rune, "ValheimMod.Monobehaviours.FoundationRune"), terrainHolder = Guard.Type(rune, "ValheimMod.TerrainHolder");
                holder = Field(foundation, "terrainHolder", terrainHolder);
                bases = Ref<Dictionary<TerrainComp, float[]>>(terrainHolder, "heightsWithTerrainMods");
                levelDeltas = Field(terrainHolder, "levelDeltas", typeof(Dictionary<TerrainComp, float[]>));
                modifiedHeights = Field(terrainHolder, "modifiedHeights", typeof(Dictionary<TerrainComp, bool[]>));
                adjust = Guard.Method(foundation, "adjustTerrain", typeof(bool), typeof(TerrainComp));
            }
            var harmony = new Harmony(Owner);
            Add(harmony, level, nameof(RewriteLevel));
            Add(harmony, raise, nameof(RewriteRaise));
            Add(harmony, smooth, nameof(RewriteSmooth));
            if (adjust != null) Add(harmony, adjust, nameof(RewriteFoundation));
            pending = true;
        }

        private static FieldInfo Field(Type type, string name, Type fieldType)
        {
            FieldInfo field = AccessTools.DeclaredField(type, name);
            if (field == null || field.FieldType != fieldType) throw new MissingFieldException(type.FullName, name);
            return field;
        }

        private static AccessTools.FieldRef<object, T> Ref<T>(Type type, string name) => AccessTools.FieldRefAccess<object, T>(Field(type, name, typeof(T)));

        private static T Emit<T>(string name, Type returns, Type[] parameters, Action<ILGenerator> body) where T : Delegate
        {
            var method = new DynamicMethod(name, returns, parameters, typeof(HearthTerrainPatch), true);
            body(method.GetILGenerator());
            return (T)method.CreateDelegate(typeof(T));
        }

        private static void Add(Harmony harmony, MethodInfo target, string rewrite)
        {
            rewrites.Add(target, false);
            harmony.Patch(target, prefix: Guard.Hook(typeof(HearthTerrainPatch), nameof(Begin)),
                transpiler: Guard.Hook(typeof(HearthTerrainPatch), rewrite),
                finalizer: Guard.Hook(typeof(HearthTerrainPatch), nameof(End)));
        }

        internal static void Verify()
        {
            if (replayPending)
            {
                replayPending = false;
                replayEnabled = replays.Count == 2 && replays.All(pair => pair.Value
                    && Harmony.GetPatchInfo(pair.Key)?.Transpilers.Count(p => p.owner == ReplayOwner) == 1);
                if (replayEnabled) CompatibilityInstaller.Info("Terrain.HearthBelow.Replay: APPLIED; saved terrain repairs fill their pickaxe cuts.");
                else
                {
                    new Harmony(ReplayOwner).UnpatchSelf();
                    CompatibilityInstaller.Error("Terrain.HearthBelow.Replay: NOT APPLIED; saved terrain repairs replay as the original pickaxe cuts.");
                }
            }
            if (!pending) return;
            pending = false;
            if (!replayEnabled || rewrites.Any(pair => !pair.Value || !Registered(Harmony.GetPatchInfo(pair.Key))))
            {
                new Harmony(Owner).UnpatchSelf();
                CompatibilityInstaller.Error("Terrain.HearthBelow: NOT APPLIED; terrain IL or registration changed; vendor behavior retained.");
                return;
            }
            enabled = true;
            CompatibilityInstaller.Info("Terrain.HearthBelow: APPLIED; surface leveling also fills pickaxe cuts.");
        }

        private static bool Registered(Patches? info) => info != null && info.Transpilers.Count(p => p.owner == Owner) == 1
            && info.Prefixes.Count(p => p.owner == Owner) == 1 && info.Finalizers.Count(p => p.owner == Owner) == 1;

        // LevelTerrain and RaiseTerrain end each vertex with
        // m_levelDelta[v] = Mathf.Clamp(m_levelDelta[v], minimum, maximum); m_modifiedHeight[v] = true;
        private static int LevelClamp(List<CodeInstruction> code, int vertex)
        {
            int site = Site(code, i => code[i].Calls(clamp));
            return Shape(code, site - 6, Is(OpCodes.Ldarg_0), Loads(levelDelta), IsLoad(vertex), Is(OpCodes.Ldelem_R4), Limit, Limit, Calls(clamp),
                Is(OpCodes.Stelem_R4), Is(OpCodes.Ldarg_0), Loads(modifiedHeight), IsLoad(vertex), Is(OpCodes.Ldc_I4_1), Is(OpCodes.Stelem_I1)) ? site : -1;
        }

        private static IEnumerable<CodeInstruction> RewriteLevel(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) =>
            Transpile(instructions, __originalMethod, code =>
            {
                int site = LevelClamp(code, 11);
                if (site < 0 || !Locals(__originalMethod, 12, (7, typeof(int)), (8, typeof(int)), (10, typeof(float)))) return false;
                Redirect(code, site, levelVertex, 8, 7);
                return true;
            });

        private static IEnumerable<CodeInstruction> RewriteRaise(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) =>
            Transpile(instructions, __originalMethod, code =>
            {
                // RaiseTerrain stores its requested height (num7 = vector.y + num6) once, just before it
                // skips vertices whose heightmap is higher.
                int site = LevelClamp(code, 13);
                int skip = Site(code, i => Shape(code, i - 5, IsLoad(2), Loads(vectorY), IsLoad(11), Is(OpCodes.Add), IsStore(12)));
                if (site < 0 || skip < 0 || skip >= site || !Locals(__originalMethod, 16, (7, typeof(int)), (8, typeof(int)),
                    (10, typeof(float)), (11, typeof(float)), (12, typeof(float)), (13, typeof(int)))) return false;
                // RaiseTerrain's step (delta times falloff) and its capped target height.
                Redirect(code, site, raiseVertex, 8, 7, 11, 12);
                // Vanilla's skip test compares the requested height with the heightmap, which hides a cut
                // below it. Visit each vertex first with its step, requested height, and heightmap height.
                code.InsertRange(skip, Arguments(8, 7, 11, 12, 10).Append(new CodeInstruction(OpCodes.Call, raiseSkipped)));
                return true;
            });

        private static IEnumerable<CodeInstruction> RewriteSmooth(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) =>
            Transpile(instructions, __originalMethod, code =>
            {
                int site = Site(code, i => code[i].Calls(lerp));
                if (!Shape(code, site - 3, IsLoad(11), IsLoad(2), IsLoad(12), Calls(lerp), IsLoad(11), Is(OpCodes.Sub))
                    || !Locals(__originalMethod, 15, (7, typeof(int)), (8, typeof(int)), (12, typeof(float)))) return false;
                Redirect(code, site, smoothVertex, 8, 7);
                return true;
            });

        // adjustTerrain ends each selected vertex with
        // terrainHolder.levelDeltas[comp][v] = delta; terrainHolder.modifiedHeights[comp][v] = true;
        private static IEnumerable<CodeInstruction> RewriteFoundation(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) =>
            Transpile(instructions, __originalMethod, code =>
            {
                int site = Site(code, i => code[i].opcode == OpCodes.Stelem_I1);
                if (!Shape(code, site - 15, Is(OpCodes.Ldarg_0), Loads(holder), Loads(levelDeltas), Is(OpCodes.Ldarg_1),
                        Calls(deltaItem), IsLoad(12), IsLoad(15), Is(OpCodes.Stelem_R4),
                        Is(OpCodes.Ldarg_0), Loads(holder), Loads(modifiedHeights), Is(OpCodes.Ldarg_1),
                        Calls(heightItem), IsLoad(12), Is(OpCodes.Ldc_I4_1), Is(OpCodes.Stelem_I1))
                    || !Locals(__originalMethod, 16, (7, typeof(bool)), (9, typeof(int)), (10, typeof(int)), (15, typeof(float)))) return false;
                // changed |= FoundationVertex(terrainHolder, comp, x, z, delta). The insertion point is the loop
                // increment, whose label the out-of-radius and outside-edge branches target, so skipped vertices never call it.
                code.InsertRange(site + 1, new[] {
                    Load(7), new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldfld, holder), new CodeInstruction(OpCodes.Ldarg_1),
                    Load(10), Load(9), Load(15), new CodeInstruction(OpCodes.Call, foundationVertex),
                    new CodeInstruction(OpCodes.Or), new CodeInstruction(OpCodes.Stloc_S, (byte)7) });
                return true;
            });

        // HearthBelow computes a fill record's columns as it does any record's. Each return then tests the
        // record's type inline, so other records pass without a call, and a fill record takes its rows and
        // result from RepairBounds. HearthBelow's result stays on the stack until that test.
        private static IEnumerable<CodeInstruction> RewriteBounds(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod, ILGenerator generator) =>
            Transpile(instructions, __originalMethod, code =>
            {
                if (!Parameters(__originalMethod, "operation", "minimumColumnX", "maximumColumnX", "minimumColumnZ", "maximumColumnZ", "ownedOnly",
                        "minX", "minY", "minZ", "maxX", "maxY", "maxZ")
                    || !code.Any(i => i.opcode == OpCodes.Ret) || code.Any(i => i.blocks.Count > 0)) return false;
                Label exit = generator.DefineLabel(), repair = generator.DefineLabel();
                foreach (CodeInstruction ret in code.Where(i => i.opcode == OpCodes.Ret)) { ret.opcode = OpCodes.Br; ret.operand = exit; }
                List<CodeInstruction> test = IsRepair(OpCodes.Beq, repair);
                test[0].labels.Add(exit);
                code.AddRange(test);
                code.Add(new CodeInstruction(OpCodes.Ret));
                code.Add(new CodeInstruction(OpCodes.Pop) { labels = { repair } });
                code.AddRange(RepairRecord());
                // minX, maxX, minZ, maxZ by value, then minY and maxY by reference.
                foreach (int bound in new[] { 7, 10, 9, 12 }) code.AddRange(new[] { Argument(bound), new CodeInstruction(OpCodes.Ldind_I4) });
                code.AddRange(new[] { Argument(8), Argument(11), new CodeInstruction(OpCodes.Call, repairBounds), new CodeInstruction(OpCodes.Ret) });
                return true;
            }, true);

        // A fill record returns RepairDensity's result before HearthBelow's shapes run.
        private static IEnumerable<CodeInstruction> RewriteDensity(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod, ILGenerator generator) =>
            Transpile(instructions, __originalMethod, code =>
            {
                if (code.Count == 0 || !Parameters(__originalMethod, "operation", "minimumColumnX", "maximumColumnX", "minimumColumnZ", "maximumColumnZ", "dryRun")) return false;
                Label native = generator.DefineLabel();
                code[0].labels.Add(native);
                code.InsertRange(0, IsRepair(OpCodes.Bne_Un, native).Concat(RepairRecord()).Concat(Enumerable.Range(2, 5).Select(Argument))
                    .Append(new CodeInstruction(OpCodes.Call, repairDensity)).Append(new CodeInstruction(OpCodes.Ret)));
                return true;
            }, true);

        private static bool Parameters(MethodBase method, params string[] names) => method.GetParameters().Select(p => p.Name).SequenceEqual(names);

        // operation.Type compared with the fill type, read in place without copying or boxing the record.
        private static List<CodeInstruction> IsRepair(OpCode branch, Label target) => new List<CodeInstruction> {
            new CodeInstruction(OpCodes.Ldarga_S, (byte)1), new CodeInstruction(OpCodes.Ldfld, opType),
            new CodeInstruction(OpCodes.Ldc_I4, (int)RepairType), new CodeInstruction(branch, target) };

        // The zone, then the record's sequence, point, and floor.
        private static IEnumerable<CodeInstruction> RepairRecord() => new[] { new CodeInstruction(OpCodes.Ldarg_0) }.Concat(new[] { opSequence, opPoint, floorHeight }
            .SelectMany(field => new[] { new CodeInstruction(OpCodes.Ldarga_S, (byte)1), new CodeInstruction(OpCodes.Ldfld, field) }));

        private static CodeInstruction Argument(int index) => new CodeInstruction(OpCodes.Ldarg_S, (byte)index);

        // Edits a copy of the IL. When edit rejects the shape, the original instructions run unchanged.
        // Without the replay rewrites, HearthBelow replays fill records as no-ops, so the tool rewrites stop with them.
        private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions, MethodBase method, Func<List<CodeInstruction>, bool> edit, bool replay = false)
        {
            List<CodeInstruction> original = instructions.ToList(), code = original.Select(i => new CodeInstruction(i)).ToList();
            bool match = (replay ? replays : rewrites)[method] = edit(code);
            if (match) return code;
            if (replay && replayEnabled) { replayEnabled = false; CompatibilityInstaller.Error("Terrain.HearthBelow.Replay: NO LONGER APPLIED; terrain IL changed on " + method.Name); }
            if (enabled) { enabled = false; CompatibilityInstaller.Error("Terrain.HearthBelow: NO LONGER APPLIED; terrain IL changed on " + method.Name); }
            return original;
        }

        // Heightmap limit mods replace constants with parameterless float getters.
        private static bool Limit(CodeInstruction code) => code.opcode == OpCodes.Ldc_R4
            || code.opcode == OpCodes.Call && code.operand is MethodInfo method && method.IsStatic
                && method.ReturnType == typeof(float) && method.GetParameters().Length == 0;

        // Points the call at site to hook, which takes the original arguments, then the TerrainComp, the tool target, and locals.
        private static void Redirect(List<CodeInstruction> code, int site, MethodInfo hook, params int[] locals)
        {
            code[site].operand = hook;
            code.InsertRange(site, Arguments(locals));
        }

        private static IEnumerable<CodeInstruction> Arguments(params int[] locals) =>
            new[] { new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldarg_1) }.Concat(locals.Select(Load));

        private static CodeInstruction Load(int local) => new CodeInstruction(OpCodes.Ldloc_S, (byte)local);

        private static MethodInfo Hook(string name) => AccessTools.DeclaredMethod(typeof(HearthTerrainPatch), name);

        // Each hook passes the surface the tool sets in a cut (target) and the heightmap height it
        // leaves (surface). Replay keeps their difference, so later heightmap edits show through.
        private static float LevelVertex(float value, float minimum, float maximum, TerrainComp comp, Vector3 target, int x, int z)
        {
            float clamped = Mathf.Clamp(value, minimum, maximum), surface = target.y + clamped - value;
            if (Floor(comp, x, z, out float floor)) Queue(comp, x, z, floor, surface, surface);
            return clamped;
        }

        // RaiseTerrain skips vertices above the requested height and steps at most delta (times falloff)
        // above each one. A cut column follows the same rules from its cut floor.
        private static float RaiseVertex(float value, float minimum, float maximum, TerrainComp comp, Vector3 target, int x, int z, float step, float raised)
        {
            float clamped = Mathf.Clamp(value, minimum, maximum);
            RaiseFill(comp, target, x, z, step, comp.transform.position.y + raised + clamped - value);
            return clamped;
        }

        // Vanilla leaves this vertex's heightmap alone, but a cut below it can still rise.
        private static void RaiseSkipped(TerrainComp comp, Vector3 target, int x, int z, float step, float raised, float height)
        {
            if (raised < height) RaiseFill(comp, target, x, z, step, comp.transform.position.y + height);
        }

        private static void RaiseFill(TerrainComp comp, Vector3 target, int x, int z, float step, float surface)
        {
            if (step > 0 && Floor(comp, x, z, out float floor)) Queue(comp, x, z, floor, Mathf.Min(target.y + step, floor + step), surface);
        }

        private static float SmoothVertex(float height, float target, float blend, TerrainComp comp, Vector3 worldTarget, int x, int z)
        {
            float result = Mathf.Lerp(height, target, blend);
            if (Floor(comp, x, z, out float floor))
            {
                float previous = smoothDelta(comp)[z * (compWidth(comp) + 1) + x];
                float surface = comp.transform.position.y + height + Mathf.Clamp(previous + result - height, -1f, 1f) - previous;
                Queue(comp, x, z, floor, floor + Mathf.Clamp((worldTarget.y - floor) * blend, -1f, 1f), surface);
            }
            return result;
        }

        private static bool FoundationVertex(object terrainHolder, TerrainComp comp, int x, int z, float delta)
        {
            Heightmap map = heightmap(comp);
            float baseline = bases(terrainHolder)[comp][z * (map.m_width + 1) + x];
            float surface = map.transform.position.y + baseline + delta;
            return Floor(comp, x, z, out float floor) && Queue(comp, x, z, floor, surface, surface);
        }

        // Finds the cut floor: the highest solid-to-air crossing in a column below the heightmap.
        private static bool Floor(TerrainComp comp, int x, int z, out float floor)
        {
            floor = 0;
            // RuneMagic levels an unnetworked compiler to read the height limits. It has no saved terrain.
            if (!enabled || depth == 0 || netView(comp) is null || !comp.IsOwner()) return false;
            action ??= Open(comp);
            if (action.Comp != comp || action.Density == null) return false;
            VoxelGrid grid = action.Grid;
            float[] values = action.Density;
            Heightmap map = heightmap(comp);
            if (grid.Spacing != 1f || map.m_scale != 1f) throw new NotSupportedException("HearthBelow terrain grid spacing changed.");
            grid.Column(VertexPosition(map, x, z), out int gx, out int gz);
            if (!grid.Owns(gx, gz)) return false;
            int top = grid.NY - 1;
            float Sample(int y) => values[grid.Index(gx, y, gz)];
            // A preceding raise can fill the old grid before Heightmap.Poke grows it.
            if (Sample(top) > 0) return false;
            for (int y = top - 1; y >= 2; y--)
            {
                float below = Sample(y), above = Sample(y + 1);
                if (below <= 0 || above > 0) continue;
                floor = grid.Origin.y + y + below / (below - above);
                return floor < map.transform.position.y + map.GetHeight(x, z) - 0.001f;
            }
            return false;
        }

        // Loads the saved cuts, so the first edit of a zone sees them before the vendor's Update poll.
        private static ToolAction Open(TerrainComp comp)
        {
            object key = zoneKey.Invoke(null, new object[] { comp.transform.position });
            if ((bool)sync.Invoke(null, new[] { comp, key })) return new ToolAction(comp, getZone.Invoke(null, new object[] { heightmap(comp) }));
            CompatibilityInstaller.Warning("Terrain.HearthBelow: saved terrain is unreadable; this edit does not fill pickaxe cuts.");
            return new ToolAction(comp, null);
        }

        private static Vector3 VertexPosition(Heightmap map, int x, int z) =>
            map.transform.position + new Vector3((x - map.m_width / 2) * map.m_scale, 0, (z - map.m_width / 2) * map.m_scale);

        // A fill never rises above the surface the tool leaves, which already carries its height limits.
        // The immediate fill (Point.y) and replay (surface plus offset) then reach the same height.
        private static bool Queue(TerrainComp comp, int x, int z, float floor, float target, float surface)
        {
            if (float.IsNaN(target) || float.IsNaN(surface) || float.IsInfinity(surface)) return false;
            target = Mathf.Min(target, surface);
            if (!(target > floor + 0.001f)) return false;
            Vector3 point = VertexPosition(heightmap(comp), x, z);
            point.y = target;
            action!.Records.Add(Encode(point, floor, target - surface));
            return true;
        }

        private static void Begin()
        {
            if (depth++ == 0) action = null;
        }

        private static Exception? End(Exception? __exception)
        {
            if (--depth > 0) return __exception;
            ToolAction? done = action;
            action = null;
            if (__exception == null && done != null && done.Records.Count > 0) Save(done);
            return __exception;
        }

        private static void Save(ToolAction done)
        {
            ZDO zdo = netView(done.Comp).GetZDO();
            object?[] arguments = { zdo.GetByteArray(dataKey, null), null };
            if (Convert.ToInt32(read.Invoke(null, arguments)) == 2)
            {
                CompatibilityInstaller.Warning("Terrain.HearthBelow: saved terrain is unreadable; this edit does not fill pickaxe cuts.");
                return;
            }
            var saved = (IList)arguments[1]!;
            var points = new HashSet<Vector3>();
            List<object> added = done.Records.Where(operation => points.Add(pointOf(operation))).ToList();
            if (saved.Count + added.Count > ((ConfigEntry<int>)cap.GetValue(null)).Value)
            {
                if (full.Add((long)zoneKey.Invoke(null, new object[] { done.Comp.transform.position })))
                    CompatibilityInstaller.Warning("Terrain.HearthBelow: HearthBelow's carve limit for this zone is reached; surface edits no longer fill pickaxe cuts here.");
                return;
            }
            int id = (int)newId.Invoke(null, null);
            foreach (object operation in added) { setId(operation, id); saved.Add(operation); }
            // HearthBelow's payload cache sees the changed bytes and reloads them before its next save.
            zdo.Set(dataKey, (byte[])serialize.Invoke(null, new object[] { saved }));
            Heightmap map = heightmap(done.Comp);
            applying = true;
            try { foreach (object operation in added) { applyOp(done.Zone!, operation); dirtyNeighbors(map, operation); } }
            finally { applying = false; }
            rush.Invoke(done.Zone, null);
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct Bits { [FieldOffset(0)] public float Value; [FieldOffset(0)] public int Raw; }

        private static object Encode(Vector3 point, float floor, float offset) => encode(point, floor, RepairTag << 32 | (uint)new Bits { Value = offset }.Raw);

        // The samples a record fills in its column: from the first at or above its floor to one above its target.
        private struct Fill { internal int X, Z, First, Last; internal float Target; }

        // Replay reads the column's current heightmap height, so later heightmap edits show through.
        // While this action applies its own records, the zone still holds the heights from before it.
        private static bool Decode(VoxelGrid grid, long sequence, Vector3 point, float floor, out Fill fill)
        {
            fill = default;
            if (sequence >> 32 != RepairTag) return false;
            float offset = new Bits { Raw = (int)sequence }.Value;
            if (float.IsNaN(offset) || float.IsInfinity(offset) || offset > 0 || float.IsNaN(floor) || float.IsInfinity(floor)) return false;
            grid.Column(point, out int x, out int z);
            if (x < 0 || x >= grid.NX || z < 0 || z >= grid.NZ) return false;
            float target = applying ? point.y : grid.Surface(x, z) + offset;
            if (!(target > floor)) return false;
            float bottom = grid.Origin.y;
            fill = new Fill { X = x, Z = z, Target = target, First = Math.Max(2, Mathf.CeilToInt(floor - bottom)),
                Last = Math.Min(grid.NY - 1, Mathf.FloorToInt(target - bottom) + 1) };
            return true;
        }

        // ComputeOpBounds for a fill record, after HearthBelow set its columns.
        private static bool RepairBounds(object zone, long sequence, Vector3 point, float floor, int minX, int maxX, int minZ, int maxZ,
            ref int minY, ref int maxY)
        {
            if (!Decode(new VoxelGrid(zone), sequence, point, floor, out Fill fill)) return false;
            minY = fill.First; maxY = fill.Last;
            return minX <= maxX && minZ <= maxZ && minY <= maxY;
        }

        // ApplyOpDensity for a fill record. Raises samples above the saved cut floor to the target surface and
        // never removes material. A cut removed later leaves the record over intact ground, where it changes nothing.
        private static bool RepairDensity(object zone, long sequence, Vector3 point, float floor, int minimumColumnX, int maximumColumnX,
            int minimumColumnZ, int maximumColumnZ, bool dryRun)
        {
            var grid = new VoxelGrid(zone);
            if (!Decode(grid, sequence, point, floor, out Fill fill)) return false;
            int x = fill.X, z = fill.Z;
            if (x < Math.Max(1, minimumColumnX) || x > Math.Min(grid.OwnedX, maximumColumnX)
                || z < Math.Max(1, minimumColumnZ) || z > Math.Min(grid.OwnedZ, maximumColumnZ) || fill.First > fill.Last) return false;
            // Like HearthBelow, read an unmaterialized zone from its heightmap and allocate only to write.
            float[]? values = grid.Density;
            float bottom = grid.Origin.y, target = fill.Target, surface = grid.Surface(x, z);
            int cell = Mathf.FloorToInt(target - bottom);
            float Sample(int y) => values == null ? Mathf.Clamp(surface - (bottom + y), -8f, 8f) : values[grid.Index(x, y, z)];
            bool changed = false;
            for (int y = fill.First; y <= fill.Last; y++)
            {
                float value = Mathf.Clamp(target - bottom - y, -8f, 8f);
                // A target inside the floor's voxel keeps the solid sample below and moves the crossing.
                if (y == fill.First && cell == fill.First - 1 && Sample(cell) > 0)
                {
                    float fraction = target - bottom - cell;
                    value = Sample(cell) * (fraction - 1f) / fraction;
                }
                if (value <= Sample(y)) continue;
                changed = true;
                if (dryRun) return true;
                values ??= grid.Materialize();
                values[grid.Index(x, y, z)] = value;
            }
            return changed;
        }

        // A VoxelZone's samples: columns Spacing apart from Origin, each holding NY samples 1 m apart.
        private readonly struct VoxelGrid
        {
            internal readonly object Zone;
            internal readonly Vector3 Origin;
            internal readonly float Spacing;
            internal readonly int NX, NY, NZ, OwnedX, OwnedZ;

            internal VoxelGrid(object zone)
            {
                Zone = zone;
                Origin = origin(zone); Spacing = spacing(zone);
                NX = nx(zone); NY = ny(zone); NZ = nz(zone);
                OwnedX = ownedX(zone); OwnedZ = ownedZ(zone);
            }

            internal float[]? Density => density(Zone);
            internal float[] Materialize() { ensureDensity.Invoke(Zone, null); return density(Zone); }
            internal int Index(int x, int y, int z) => (y * NZ + z) * NX + x;
            internal bool Owns(int x, int z) => x >= 1 && x <= OwnedX && z >= 1 && z <= OwnedZ;
            internal float Surface(int x, int z) => heightAt(Zone, x, z);
            internal void Column(Vector3 point, out int x, out int z)
            { x = Mathf.RoundToInt((point.x - Origin.x) / Spacing); z = Mathf.RoundToInt((point.z - Origin.z) / Spacing); }
        }
    }
}
