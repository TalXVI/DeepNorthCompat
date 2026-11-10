using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using static DeepNorthCompat.ILMatch;

namespace DeepNorthCompat
{
    internal static class FarLandsTerrainPatch
    {
        internal const string Guid = "ru.ivest.farlands";
        internal const string Owner = Plugin.Guid + ".Terrain.FarLands";
        private const string Group = "Terrain.FarLands";
        private const string UnlimitedBuild = "F7C2C869CA3CAE30309D657984197B700C9E541C6C18D264AE8E422A9969D4A9";
        private static readonly MethodInfo minimum = Guard.Method(typeof(Math), nameof(Math.Min), typeof(float), typeof(float), typeof(float));
        private static readonly MethodInfo maximum = Guard.Method(typeof(Math), nameof(Math.Max), typeof(float), typeof(float), typeof(float));
        private static readonly MethodInfo isNaN = Guard.Method(typeof(float), nameof(float.IsNaN), typeof(bool), typeof(float));
        private static readonly MethodInfo clamp = Guard.Method(typeof(Mathf), nameof(Mathf.Clamp), typeof(float), typeof(float), typeof(float), typeof(float));
        private static readonly MethodInfo apply = Guard.Method(typeof(TerrainComp), nameof(TerrainComp.ApplyToHeightmap), typeof(void),
            typeof(Texture2D), typeof(List<float>), typeof(float[]), typeof(float[]), typeof(Heightmap));
        private static readonly MethodInfo cacheIdentity = AccessTools.DeclaredMethod(typeof(FarLandsTerrainPatch), nameof(CacheIdentity));
        private static MethodInfo parse = null!, buildings = null!, worldIdentity = null!, openBuildings = null!;
        private static MethodInfo? minGetter, maxGetter;
        private static Func<float> minDistance = null!, maxOffset = null!;
        private static FieldInfo path = null!, identity = null!, codec = null!, seed = null!, uid = null!;
        private static AccessTools.FieldRef<object, float[]> delta = null!;
        private static Func<object, float[], object> rebuild = null!;
        private static bool pending, enabled, rewritten;

        internal static void Prepare(Assembly? farlands, Assembly? unlimited)
        {
            var harmony = new Harmony(Owner);
            harmony.UnpatchSelf();
            pending = enabled = rewritten = false;

            if (farlands == null || Guard.KnownServerBuild())
            {
                CompatibilityInstaller.Info($"{Group}: optional integration inactive.");
                return;
            }

            Guard.Build(farlands, ExpectedBuilds.FarLands141);
            Guard.Build(typeof(TerrainComp).Assembly, ExpectedBuilds.Valheim1021Client);

            minGetter = maxGetter = null;
            minDistance = maxOffset = () => 8f;

            if (unlimited != null)
            {
                Guard.Build(unlimited, UnlimitedBuild);
                Type main = Guard.Type(unlimited, "Digitalroot.HeightmapUnlimitedJvL.Main");
                minGetter = Guard.Method(main, "MinAbs", typeof(float));
                maxGetter = Guard.Method(main, "Max", typeof(float));
                minDistance = (Func<float>)Delegate.CreateDelegate(typeof(Func<float>), minGetter);
                maxOffset = (Func<float>)Delegate.CreateDelegate(typeof(Func<float>), maxGetter);
            }

            Type edit = Guard.Type(farlands, "FarLands.ZoneEdit");
            parse = Guard.Method(Guard.Type(farlands, "FarLands.TerrainCompData"), "Parse", edit,
                typeof(int), typeof(int), typeof(byte[]));
            if (!DecoderShape(PatchProcessor.GetOriginalInstructions(parse), parse))
                throw new NotSupportedException("FarLands terrain decoder IL changed.");

            delta = AccessTools.FieldRefAccess<object, float[]>(Field(edit, "Delta", typeof(float[])));
            ParameterExpression from = Expression.Parameter(typeof(object)), values = Expression.Parameter(typeof(float[]));
            Expression original = Expression.Convert(from, edit);
            NewExpression replacement = Expression.New(edit.GetConstructor(new[] { typeof(int), typeof(int), typeof(int), typeof(float[]), typeof(uint[]), typeof(bool[]) })
                ?? throw new MissingMethodException(edit.FullName, ".ctor"),
                Expression.Field(original, Field(edit, "ZoneX", typeof(int))), Expression.Field(original, Field(edit, "ZoneZ", typeof(int))),
                Expression.Field(original, Field(edit, "Operations", typeof(int))), values,
                Expression.Field(original, Field(edit, "Paint", typeof(uint[]))), Expression.Field(original, Field(edit, "Painted", typeof(bool[]))));
            rebuild = Expression.Lambda<Func<object, float[], object>>(Expression.Convert(replacement, typeof(object)), from, values).Compile();

            worldIdentity = Guard.Method(Guard.Type(farlands, "FarLands.WorldIdentity"), "Of", typeof(string),
                Guard.Type(farlands, "FarLands.WorldSampler"), typeof(WorldGenerator), typeof(int), typeof(float));

            Type closure = Guard.Type(farlands, "FarLands.FarBuildings+<>c__DisplayClass85_1");
            Type cell = Guard.Type(farlands, "FarLands.BuildingCell");
            Type cellCodec = Guard.Type(farlands, "FarLands.BuildingCodec");
            Type store = Guard.Type(farlands, "FarLands.TileStore`1").MakeGenericType(cell);
            buildings = Guard.Method(closure, "<.ctor>b__4", store);
            path = Field(closure, "path", typeof(string));
            identity = Field(closure, "identity", typeof(string));
            seed = Field(closure, "seed", typeof(int));
            uid = Field(closure, "uid", typeof(long));
            codec = Field(cellCodec, "Instance", cellCodec);
            openBuildings = AccessTools.DeclaredMethod(Guard.Type(farlands, "FarLands.TileStore"), "Open").MakeGenericMethod(cell);

            if (!openBuildings.IsStatic || openBuildings.ReturnType != store
                || !openBuildings.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[]
                {
                    typeof(string), typeof(string), Guard.Type(farlands, "FarLands.ITileCodec`1").MakeGenericType(cell),
                    typeof(int), typeof(long), typeof(string)
                })) throw new MissingMethodException("FarLands.TileStore", "Open");

            try
            {
                harmony.Patch(parse, postfix: Guard.Hook(typeof(FarLandsTerrainPatch), nameof(Decoded)));
                harmony.Patch(buildings, transpiler: Guard.Hook(typeof(FarLandsTerrainPatch), nameof(RewriteBuildings)));
                harmony.Patch(worldIdentity, postfix: Guard.Hook(typeof(FarLandsTerrainPatch), nameof(WorldIdentity)));
                pending = true;
            }
            catch
            {
                harmony.UnpatchSelf();
                throw;
            }
        }

        private static FieldInfo Field(Type type, string name, Type expected)
        {
            FieldInfo field = AccessTools.DeclaredField(type, name);
            if (field == null || field.FieldType != expected) throw new MissingFieldException(type.FullName, name);
            return field;
        }

        internal static void Verify()
        {
            if (!pending) return;

            pending = false;
            enabled = rewritten && DecoderShape(PatchProcessor.GetCurrentInstructions(parse), parse)
                && GameLimitsShape(PatchProcessor.GetCurrentInstructions(apply))
                && Harmony.GetPatchInfo(buildings)?.Transpilers.Count(p => p.owner == Owner) == 1
                && Harmony.GetPatchInfo(parse)?.Postfixes.Count(p => p.owner == Owner) == 1
                && Harmony.GetPatchInfo(worldIdentity)?.Postfixes.Count(p => p.owner == Owner) == 1;

            if (enabled)
            {
                CompatibilityInstaller.Info($"{Group}: APPLIED; saved offsets follow effective terrain limits; derived terrain, building and forest caches include those limits.");
                return;
            }

            new Harmony(Owner).UnpatchSelf();
            CompatibilityInstaller.Error($"{Group}: NOT APPLIED; decoder, game limits or cache identity verification failed; vendor behavior retained.");
        }

        internal static bool DecoderShape(List<CodeInstruction> code, MethodBase method)
        {
            int site = Site(code, i => code[i].Calls(maximum));

            return Locals(method, 18, (4, typeof(float[])), (8, typeof(int)), (9, typeof(float)), (10, typeof(float)))
                && Shape(code, site - 9, IsLoad(4), IsLoad(8), IsLoad(10), Calls(isNaN), Is(OpCodes.Brtrue),
                    i => i.Is(OpCodes.Ldc_R4, -8f), i => i.Is(OpCodes.Ldc_R4, 8f), IsLoad(10), Calls(minimum), Calls(maximum), Is(OpCodes.Br))
                && code.Count(i => i.Calls(minimum)) == 1;
        }

        private static bool GameLimitsShape(List<CodeInstruction> code)
        {
            int site = Site(code, i => code[i].Calls(clamp));

            return Locals(apply, 11, (4, typeof(float)), (5, typeof(float)), (6, typeof(float)), (7, typeof(float)))
                && Shape(code, site - 12, IsLoad(4), Is(OpCodes.Add), IsLoad(5), Is(OpCodes.Add), IsStore(7),
                    IsLoad(7), IsLoad(6), MinimumLimit, Is(OpCodes.Sub), IsLoad(6), MaximumLimit, Is(OpCodes.Add), Calls(clamp), IsStore(7));
        }

        private static bool MinimumLimit(CodeInstruction instruction) => minGetter == null ? instruction.Is(OpCodes.Ldc_R4, 8f) : instruction.Calls(minGetter);
        private static bool MaximumLimit(CodeInstruction instruction) => maxGetter == null ? instruction.Is(OpCodes.Ldc_R4, 8f) : instruction.Calls(maxGetter);

        // HarmonyX cannot round-trip this decoder's exception filter through a transpiler.
        // Let the vendor validate the full record before applying the game's limits to finite saved offsets.
        private static void Decoded(byte[] compressed, ref object __result)
        {
            if (!enabled) return;

            float[] values = delta(__result);
            float lower = -minDistance(), upper = maxOffset();

            using (var gzip = new GZipStream(new MemoryStream(compressed, false), CompressionMode.Decompress))
            using (var reader = new BinaryReader(gzip))
            {
                for (int i = 0; i < 6; i++) reader.ReadInt32();

                int count = reader.ReadInt32();
                if (count != values.Length) throw new InvalidDataException("FarLands terrain height count changed after validation.");

                for (int i = 0; i < count; i++)
                {
                    if (!reader.ReadBoolean()) continue;

                    float saved = reader.ReadSingle() + reader.ReadSingle();
                    if (!float.IsNaN(saved) && !float.IsInfinity(saved)) values[i] = Mathf.Clamp(saved, lower, upper);
                }
            }

            __result = rebuild(__result, values);
        }

        private static IEnumerable<CodeInstruction> RewriteBuildings(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) =>
            Transpile(instructions, code =>
            {
                int site = Site(code, i => code[i].LoadsField(codec));

                if (!Locals(__originalMethod, 0) || code.Count != 12 || site != 4
                    || !Shape(code, 0, Is(OpCodes.Ldarg_0), Loads(path), Is(OpCodes.Ldarg_0), Loads(identity), Loads(codec),
                        Is(OpCodes.Ldarg_0), Loads(seed), Is(OpCodes.Ldarg_0), Loads(uid),
                        i => i.Is(OpCodes.Ldstr, "buildings"), Calls(openBuildings), Is(OpCodes.Ret))) return false;

                code.Insert(site, new CodeInstruction(OpCodes.Call, cacheIdentity));
                return true;
            });

        private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions, Func<List<CodeInstruction>, bool> edit)
        {
            List<CodeInstruction> original = instructions.ToList();
            List<CodeInstruction> code = original.Select(i => new CodeInstruction(i)).ToList();
            rewritten = edit(code);

            if (rewritten) return code;

            Lost(ref enabled, Group, "decoder or cache identity IL changed");
            return original;
        }

        // WorldIdentity is shared by the terrain and forest stores. Buildings use their world UID instead.
        private static string CacheIdentity(string original) => enabled
            ? FormattableString.Invariant($"{original}|dnc-terrain2|{-minDistance():R}|{maxOffset():R}") : original;

        private static void WorldIdentity(ref string __result) => __result = CacheIdentity(__result);
    }
}
