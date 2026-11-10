using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using BepInEx.Configuration;
using DeepNorthCompat;
using HarmonyLib;

internal static class FarLandsTerrainTests
{
    private const string Fixture = "DeepNorthCompat.Tests.FarLandsTerrain";
    private const string LimiterFixture = Fixture + ".Limits";
    private static Assembly vendor = null!, unlimited = null!;

    internal static void RunClient(string profile, Action<string, Action> test)
    {
        void Case(string name, Action action) => test("FarLands terrain " + name, () =>
        {
            Cleanup();

            try { action(); }
            finally { Cleanup(); }
        });

        Case("absent integration is inactive", () => Check(!Harmony.HasAnyPatches(FarLandsTerrainPatch.Owner), "no absent hooks"));

        string file = Path.Combine(profile, "BepInEx", "plugins", "Ivvty-FarLands", "FarLands.dll");
        if (!File.Exists(file)) return;

        vendor = Assembly.LoadFrom(file);

        Case("preserves vanilla limits without a limiter", () =>
        {
            Install(null);
            GameHeightApplication(-8f, 8f);
        });

        string limiter = Path.Combine(profile, "BepInEx", "plugins", "Digitalroot-Heightmap_Unlimited_JvL", "Digitalroot.HeightmapUnlimitedJvL.dll");
        if (!File.Exists(limiter)) return;

        unlimited = Assembly.LoadFrom(limiter);

        void LimitedCase(string name, Action action) => Case(name, () => WithLimits(-200f, 200f, action));

        LimitedCase("clamps saved deltas to effective limits and retains vendor nonfinite handling", () =>
        {
            Install(unlimited);
            SavedDeltas(vendor);
        });

        LimitedCase("retains decoder input validation and old paint layout", () =>
        {
            Install(unlimited);

            foreach (byte[] bad in new[] { Saved(20f, 0f, 4224), Saved(20f, 0f, 4225, 4226), new byte[] { 1, 2, 3 }, new byte[0] })
                Throws<InvalidDataException>(() => Parse(vendor, bad));

            foreach (int paints in new[] { 0, 4096, 4225 })
            {
                object edit = Parse(vendor, Saved(15f, 5f, paints: paints));
                Check(((float[])Get(edit, "Delta"))[0] == 20f, "delta retained for paint layout " + paints);
                Check(((uint[])Get(edit, "Paint")).Length == 4225 && ((bool[])Get(edit, "Painted")).Length == 4225, "paint dimensions retained");
            }
        });

        LimitedCase("invalidates legacy derived caches and reuses corrected caches after restart", CacheRoundTrips);
        LimitedCase("invalidates all derived caches when either limit changes and reuses them after restart", CacheLimitChanges);
        LimitedCase("matches game height application before and after runtime limits shrink", () =>
        {
            Install(unlimited);
            GameHeightApplication(-200f, 200f);
            SetLimits(-12.5f, 33.25f);
            GameHeightApplication(-12.5f, 33.25f);
        });

        LimitedCase("rejects an unknown build before installing hooks", () =>
        {
            Install(unlimited);
            Throws<NotSupportedException>(() => FarLandsTerrainPatch.Prepare(typeof(Plugin).Assembly, unlimited));
            Check(!Harmony.HasAnyPatches(FarLandsTerrainPatch.Owner), "unknown build left no group hooks");
            Check(((float[])Get(Parse(vendor, Saved(200f, 0f)), "Delta"))[0] == 8f, "vendor decoder restored");
        });

        LimitedCase("rejects an unknown limiter build without retaining hooks", () =>
        {
            Install(unlimited);
            Throws<NotSupportedException>(() => FarLandsTerrainPatch.Prepare(vendor, typeof(Plugin).Assembly));
            Check(!Harmony.HasAnyPatches(FarLandsTerrainPatch.Owner), "unknown limiter left no group hooks");
            Check(((float[])Get(Parse(vendor, Saved(200f, 0f)), "Delta"))[0] == 8f, "vendor decoder restored after unknown limiter");
        });

        LimitedCase("rejects a known limiter whose game hook is missing", () =>
        {
            FarLandsTerrainPatch.Prepare(vendor, unlimited);
            new Harmony(LimiterFixture).UnpatchSelf();
            FarLandsTerrainPatch.Verify();
            Check(!Harmony.HasAnyPatches(FarLandsTerrainPatch.Owner), "missing game limits rolled back the group");
        });

        LimitedCase("rejects a changed effective game clamp", () =>
        {
            FarLandsTerrainPatch.Prepare(vendor, unlimited);
            new Harmony(Fixture).Patch(AccessTools.Method(typeof(TerrainComp), "ApplyToHeightmap"),
                transpiler: new HarmonyMethod(AccessTools.Method(typeof(FarLandsTerrainTests), nameof(ChangedLimits))) { priority = Priority.Last });
            FarLandsTerrainPatch.Verify();
            Check(!Harmony.HasAnyPatches(FarLandsTerrainPatch.Owner), "changed game limits rolled back the group");
            Check(((float[])Get(Parse(vendor, Saved(200f, 0f)), "Delta"))[0] == 8f, "vendor decoder restored after changed game limits");
        });

        LimitedCase("rolls back the decoder when cache verification fails", () =>
        {
            FarLandsTerrainPatch.Prepare(vendor, unlimited);
            new Harmony(FarLandsTerrainPatch.Owner).Unpatch(AccessTools.Method(Type("WorldIdentity"), "Of"), HarmonyPatchType.Postfix, FarLandsTerrainPatch.Owner);
            FarLandsTerrainPatch.Verify();
            Check(!Harmony.HasAnyPatches(FarLandsTerrainPatch.Owner), "partial cache group rolled back");
            Check(((float[])Get(Parse(vendor, Saved(200f, 0f)), "Delta"))[0] == 8f, "vendor decoder restored after failed verification");
        });

        LimitedCase("rejects changed minimum, maximum and duplicated clamp sites", () =>
        {
            MethodInfo parse = AccessTools.Method(Type("TerrainCompData"), "Parse");

            for (int variant = 0; variant < 3; variant++)
            {
                FarLandsTerrainPatch.Prepare(vendor, unlimited);
                List<CodeInstruction> code = PatchProcessor.GetOriginalInstructions(parse);
                int min = code.FindIndex(i => i.Is(OpCodes.Ldc_R4, -8f));
                int max = code.FindIndex(i => i.Is(OpCodes.Ldc_R4, 8f));

                if (variant == 0) code[min].operand = -9f;
                else if (variant == 1) code[max].operand = 9f;
                else code.AddRange(code.Skip(min).Take(5).Select(i => new CodeInstruction(i)).ToArray());

                Check(!FarLandsTerrainPatch.DecoderShape(code, parse), "changed clamp rejected");
            }
        });

        LimitedCase("stops all injected behavior after a later rewrite no longer matches", () =>
        {
            Install(unlimited);
            MethodInfo buildings = AccessTools.Method(Type("FarBuildings+<>c__DisplayClass85_1"), "<.ctor>b__4");
            new Harmony(Fixture).Patch(buildings, transpiler: new HarmonyMethod(AccessTools.Method(typeof(FarLandsTerrainTests), nameof(ChangedCache))) { priority = Priority.First });
            Check(((float[])Get(Parse(vendor, Saved(200f, 0f)), "Delta"))[0] == 8f, "vendor decoder restored when cache rewrite stops");

            WithWorldIdentity(() => Check(!WorldIdentity().Contains("dnc-terrain"), "cache version hook stopped with the decoder"));
            new Harmony(Fixture).UnpatchSelf();
            Check(((float[])Get(Parse(vendor, Saved(200f, 0f)), "Delta"))[0] == 8f, "remains stopped until explicit reinstall");
            Install(unlimited);
            Check(((float[])Get(Parse(vendor, Saved(200f, 0f)), "Delta"))[0] == 200f, "explicit reinstall restores the group");
        });
    }

    internal static void RunServer(string profile, Action<string, Action> test) => test("FarLands terrain dedicated role is inactive", () =>
    {
        try
        {
            FarLandsTerrainPatch.Prepare(typeof(Plugin).Assembly, typeof(Plugin).Assembly);
            FarLandsTerrainPatch.Verify();
            Check(!Harmony.HasAnyPatches(FarLandsTerrainPatch.Owner), "dedicated role installed no rendering hooks");
        }
        finally { Cleanup(); }
    });

    private static void Install(Assembly? limiter)
    {
        FarLandsTerrainPatch.Prepare(vendor, limiter);
        FarLandsTerrainPatch.Verify();
        Check(Harmony.HasAnyPatches(FarLandsTerrainPatch.Owner), "terrain group installed");
    }

    private static void Cleanup()
    {
        new Harmony(Fixture).UnpatchSelf();
        new Harmony(LimiterFixture).UnpatchSelf();
        FarLandsTerrainPatch.Prepare(null, null);
    }

    internal static void SavedDeltas(Assembly vendor)
    {
        foreach ((float level, float smooth, float expected) in new[]
        {
            (-300f, 0f, -200f), (-200f, 0f, -200f), (-15f, -5f, -20f), (-8f, 0f, -8f), (-3f, 0f, -3f),
            (0f, 0f, 0f), (3f, 0f, 3f), (8f, 0f, 8f), (15f, 5f, 20f), (200f, 0f, 200f),
            (300f, 0f, 200f), (200f, -180f, 20f), (-200f, 180f, -20f), (float.NaN, 0f, 0f),
            (float.PositiveInfinity, 0f, 8f), (float.NegativeInfinity, 0f, -8f)
        })
        {
            byte[] saved = Saved(level, smooth);
            byte[] before = (byte[])saved.Clone();
            object edit = Parse(vendor, saved);
            float[] delta = (float[])edit.GetType().GetField("Delta")!.GetValue(edit)!;
            Check(delta[0] == expected, $"saved {level} + {smooth}: expected {expected}, got {delta[0]}");
            Check(delta[1] == 0f && delta.Length == 4225, "unmodified heights retained");
            Check((int)edit.GetType().GetField("Operations")!.GetValue(edit)! == 17, "operation count retained");
            Check((uint)Get(edit, "Print") == (uint)Get(Parse(vendor, Saved(expected, 0f)), "Print"), "print describes the effective delta");
            Check(Convert.ToBase64String(saved) == Convert.ToBase64String(before), "saved bytes retained");
        }
    }

    private static object Parse(Assembly vendor, byte[] saved, int zoneX = 0, int zoneZ = 0) =>
        vendor.GetType("FarLands.TerrainCompData", true)!.GetMethod("Parse")!.Invoke(null, new object[] { zoneX, zoneZ, saved })!;

    private static void CacheRoundTrips() => WithWorldIdentity(() => Sandbox(directory =>
    {
        object key = Activator.CreateInstance(Type("TileKey"), 0, 0, 0)!;

        foreach (string kind in new[] { "buildings", "farlands", "forest" })
        {
            FarLandsTerrainPatch.Prepare(null, null);
            string otherPath = CachePath(directory, kind, 2);

            using (IDisposable other = OpenCache(directory, kind, 2)) Call(other, "Write", Cell(kind, key, 3f));

            byte[] unrelated = File.ReadAllBytes(otherPath);

            using (IDisposable legacy = OpenCache(directory, kind, 1)) Call(legacy, "Write", Cell(kind, key, 200f));

            using (IDisposable legacy = OpenCache(directory, kind, 1)) Check(Value(kind, Read(legacy, key)) == 8f, "legacy clipped cache survives restart for " + kind);

            byte[] oldBytes = File.ReadAllBytes(CachePath(directory, kind, 1));
            Install(unlimited);

            using (IDisposable corrected = OpenCache(directory, kind, 1))
            {
                Check((int)corrected.GetType().GetProperty("Count")!.GetValue(corrected)! == 0, "legacy derived cache rejected for " + kind);
                Call(corrected, "Write", Cell(kind, key, 200f));
            }

            Check(Directory.GetFiles(directory, Path.GetFileName(CachePath(directory, kind, 1)) + ".bad-*")
                .Any(file => File.ReadAllBytes(file).SequenceEqual(oldBytes)), "vendor set the legacy cache aside for " + kind);

            using (IDisposable reopened = OpenCache(directory, kind, 1))
            {
                Check(Value(kind, Read(reopened, key)) == 200f, "corrected cache reused after restart for " + kind);
                Call(reopened, "Write", Cell(kind, key, -20f));
            }

            using (IDisposable updated = OpenCache(directory, kind, 1)) Check(Value(kind, Read(updated, key)) == -20f, "later update persists for " + kind);

            Check(File.ReadAllBytes(otherPath).SequenceEqual(unrelated), "another world's cache retained for " + kind);
            FarLandsTerrainPatch.Prepare(null, null);

            using (IDisposable restored = OpenCache(directory, kind, 1))
                Check((int)restored.GetType().GetProperty("Count")!.GetValue(restored)! == 0, "vendor identity rejects corrected caches after removal for " + kind);
        }
    }));

    private static void CacheLimitChanges() => WithWorldIdentity(() => Sandbox(directory =>
    {
        object positive = Activator.CreateInstance(Type("TileKey"), 0, 0, 0)!;
        object negative = Activator.CreateInstance(Type("TileKey"), 0, 1, 0)!;

        foreach (string kind in new[] { "buildings", "farlands", "forest" })
        {
            SetLimits(-200f, 200f);
            Install(unlimited);
            string otherPath = CachePath(directory, kind, 2);

            using (IDisposable other = OpenCache(directory, kind, 2)) Call(other, "Write", Cell(kind, positive, 3f));

            byte[] unrelated = File.ReadAllBytes(otherPath);

            foreach ((float lower, float upper) in new[] { (-200f, 200f), (-20.5f, 200f), (-20.5f, 33.25f), (-200f, 200f) })
            {
                FarLandsTerrainPatch.Prepare(null, null);
                SetLimits(lower, upper);
                Install(unlimited);

                using (IDisposable changed = OpenCache(directory, kind, 1))
                {
                    Check((int)changed.GetType().GetProperty("Count")!.GetValue(changed)! == 0, "changed limits reject stale " + kind);
                    Call(changed, "Write", Cell(kind, positive, 300f));
                    Call(changed, "Write", Cell(kind, negative, -300f));
                }

                FarLandsTerrainPatch.Prepare(null, null);
                Install(unlimited);

                using (IDisposable reopened = OpenCache(directory, kind, 1))
                {
                    Check(Value(kind, Read(reopened, positive)) == upper, "reopened positive edit follows " + kind + " limit " + upper);
                    Check(Value(kind, Read(reopened, negative)) == lower, "reopened negative edit follows " + kind + " limit " + lower);
                }

                Check(File.ReadAllBytes(otherPath).SequenceEqual(unrelated), "changed limits retain another world's " + kind);
            }
        }
    }));

    private static string CachePath(string directory, string kind, long world)
    {
        MethodInfo name = AccessTools.Method(Type("TileStore"), kind == "buildings" ? "WorldFileName" : "FileName");
        object[] args = kind == "buildings" ? new object[] { world, kind } : new object[] { 42, 1, "1.0.21", world, kind };
        return Path.Combine(directory, (string)name.Invoke(null, args)!);
    }

    private static IDisposable OpenCache(string directory, string kind, long world)
    {
        string parent = kind == "buildings" ? "FarBuildings+<>c__DisplayClass85_1"
            : kind == "farlands" ? "FarTerrain+<>c__DisplayClass69_0" : "FarForest+<>c__DisplayClass60_0";
        object closure = Activator.CreateInstance(Type(parent))!;
        Set(closure, "path", CachePath(directory, kind, world));
        Set(closure, "identity", kind + "|u" + world.ToString("x16"));
        Set(closure, "seed", 42);
        Set(closure, "uid", world);

        if (kind != "buildings")
        {
            Set(closure, "epoch", 17);
            Set(closure, "radius", 100f);
        }

        return (IDisposable)Call(closure, kind == "buildings" ? "<.ctor>b__4" : kind == "farlands" ? "<.ctor>b__1" : "<.ctor>b__0");
    }

    private static object Cell(string kind, object key, float saved)
    {
        int zoneX = (int)Get(key, "X") * 8, zoneZ = (int)Get(key, "Z") * 8;
        object edit = Parse(vendor, Saved(saved, 0f), zoneX, zoneZ);
        float value = ((float[])Get(edit, "Delta"))[0];

        if (kind == "buildings")
        {
            Array pieces = Array.CreateInstance(Type("PlacedPiece"), 0);
            object zone = Activator.CreateInstance(Type("ZoneBuildings"), zoneX, zoneZ, 123u, pieces, edit)!;
            Array zones = Array.CreateInstance(Type("ZoneBuildings"), 1);
            zones.SetValue(zone, 0);
            return Call(AccessTools.Method(Type("BuildingCell"), "Empty").Invoke(null, new[] { key })!, "With", zones);
        }

        if (kind == "farlands")
        {
            var heights = new float[4225];
            heights[0] = value;
            return Activator.CreateInstance(Type("TerrainTile"), key, heights, new float[12675], new uint[4225], new uint[4225], 0u)!;
        }

        object tree = Activator.CreateInstance(Type("TreeInstance"))!;
        Set(tree, "Y", value);
        Array trees = Array.CreateInstance(Type("TreeInstance"), 1);
        trees.SetValue(tree, 0);
        return Activator.CreateInstance(Type("ForestCell"), key, trees, 1UL, 0u)!;
    }

    private static float Value(string kind, object cell)
    {
        if (kind == "farlands") return ((float[])Get(cell, "Heights"))[0];
        if (kind == "forest") return (float)Get(((Array)Get(cell, "Trees")).GetValue(0)!, "Y");

        object edit = ((IEnumerable)cell.GetType().GetProperty("Edits")!.GetValue(cell)!).Cast<object>().Single();
        return ((float[])Get(edit, "Delta"))[0];
    }

    private static object Read(IDisposable store, object key)
    {
        object?[] args = { key, null };
        Check((bool)AccessTools.Method(store.GetType(), "TryRead").Invoke(store, args)!, "stored record is readable");
        return args[1]!;
    }

    private static void WithWorldIdentity(Action action)
    {
        Type world = Type("WorldIdentity");
        string[] names = { "s_epoch", "s_radius", "s_print" };
        object[] before = names.Select(name => AccessTools.Field(world, name).GetValue(null)!).ToArray();

        try
        {
            // Supply an already sampled world. The real identity method and store paths still run.
            AccessTools.Field(world, names[0]).SetValue(null, 17);
            AccessTools.Field(world, names[1]).SetValue(null, 100f);
            AccessTools.Field(world, names[2]).SetValue(null, 123u);
            action();
        }
        finally
        {
            for (int i = 0; i < names.Length; i++) AccessTools.Field(world, names[i]).SetValue(null, before[i]);
        }
    }

    private static string WorldIdentity() => (string)AccessTools.Method(Type("WorldIdentity"), "Of").Invoke(null, new object?[] { null, null, 17, 100f })!;

    private static void WithLimits(float lower, float upper, Action action) => Sandbox(directory =>
    {
        Guard.Build(unlimited, "F7C2C869CA3CAE30309D657984197B700C9E541C6C18D264AE8E422A9969D4A9");
        Type main = unlimited.GetType("Digitalroot.HeightmapUnlimitedJvL.Main", true)!;
        FieldInfo min = AccessTools.Field(main, "MinHeight"), max = AccessTools.Field(main, "MaxHeight");
        object? oldMin = min.GetValue(null), oldMax = max.GetValue(null);
        var config = new ConfigFile(Path.Combine(directory, "limits.cfg"), false);

        try
        {
            min.SetValue(null, config.Bind("General", "Min Height", lower));
            max.SetValue(null, config.Bind("General", "Max Height", upper));
            Type patch = unlimited.GetType("Digitalroot.HeightmapUnlimitedJvL.Patch+PatchTerrainComp", true)!;
            new Harmony(LimiterFixture).Patch(AccessTools.Method(typeof(TerrainComp), "ApplyToHeightmap"),
                transpiler: new HarmonyMethod(AccessTools.Method(patch, "TerrainCompApplyToHeightmapTranspiler")));
            action();
        }
        finally
        {
            new Harmony(LimiterFixture).UnpatchSelf();
            min.SetValue(null, oldMin);
            max.SetValue(null, oldMax);
        }
    });

    private static void SetLimits(float lower, float upper)
    {
        Type main = unlimited.GetType("Digitalroot.HeightmapUnlimitedJvL.Main", true)!;
        ((ConfigEntry<float>)AccessTools.Field(main, "MinHeight").GetValue(null)!).Value = lower;
        ((ConfigEntry<float>)AccessTools.Field(main, "MaxHeight").GetValue(null)!).Value = upper;
    }

    private static void GameHeightApplication(float lower, float upper)
    {
        foreach ((float level, float smooth) in new[]
        {
            (-300f, 0f), (-200f, 0f), (-20f, 0f), (-8f, 0f), (-3f, 0f), (0f, 0f),
            (3f, 0f), (8f, 0f), (20f, 0f), (200f, 0f), (300f, 0f), (200f, -180f), (-200f, 180f)
        })
        {
            foreach (float basis in new[] { 0f, 64f })
            {
                object terrain = FormatterServices.GetUninitializedObject(typeof(TerrainComp));
                Set(terrain, "m_initialized", true);
                Set(terrain, "m_width", 64);
                var levels = new float[4225];
                var smoothing = new float[4225];
                levels[0] = level;
                smoothing[0] = smooth;
                Set(terrain, "m_levelDelta", levels);
                Set(terrain, "m_smoothDelta", smoothing);
                Set(terrain, "m_modifiedPaint", new bool[4225]);
                float[] baseline = Enumerable.Repeat(basis, 4225).ToArray();
                var heights = new List<float>(baseline);
                Call(terrain, "ApplyToHeightmap", null, heights, baseline, new float[4225], null);
                Check(heights[0] == basis + Math.Max(lower, Math.Min(upper, level + smooth)), "game applies effective terrain limits " + level);
                Check(((float[])Get(Parse(vendor, Saved(level, smooth)), "Delta"))[0] == heights[0] - basis, "decoder agrees with game for " + level);
                Check(heights[1] == basis, "unmodified game height retained");
            }
        }
    }

    private static IEnumerable<CodeInstruction> ChangedLimits(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.operand is MethodInfo method && method.DeclaringType?.FullName == "Digitalroot.HeightmapUnlimitedJvL.Main" && method.Name == "Max")
            {
                instruction.opcode = OpCodes.Ldc_R4;
                instruction.operand = 7f;
            }

            yield return instruction;
        }
    }

    private static IEnumerable<CodeInstruction> ChangedCache(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Is(OpCodes.Ldstr, "buildings")) instruction.operand = "changed";
            yield return instruction;
        }
    }

    private static Type Type(string name) => vendor.GetType("FarLands." + name, true)!;
    private static object Get(object instance, string name) => AccessTools.Field(instance.GetType(), name).GetValue(instance)!;
    private static void Set(object instance, string name, object value) => AccessTools.Field(instance.GetType(), name).SetValue(instance, value);
    private static object Call(object instance, string method, params object?[] args) => AccessTools.Method(instance.GetType(), method).Invoke(instance, args)!;

    private static void Sandbox(Action<string> action)
    {
        string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox"));
        string directory = Path.GetFullPath(Path.Combine(root, "FarLands-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);

        try { action(directory); }
        finally
        {
            Check(directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "isolated cache path");
            Directory.Delete(directory, true);
        }
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (TargetInvocationException exception) when (exception.InnerException is T) { return; }
        catch (T) { return; }

        throw new Exception("Expected " + typeof(T).Name);
    }

    private static byte[] Saved(float level, float smooth, int heights = 4225, int paints = 4225)
    {
        using (var bytes = new MemoryStream())
        {
            using (var gzip = new GZipStream(bytes, CompressionLevel.Optimal, true))
            using (var writer = new BinaryWriter(gzip))
            {
                writer.Write(1);
                writer.Write(17);

                for (int i = 0; i < 4; i++) writer.Write(0f);

                writer.Write(heights);

                for (int i = 0; i < heights; i++)
                {
                    writer.Write(i == 0);

                    if (i == 0)
                    {
                        writer.Write(level);
                        writer.Write(smooth);
                    }
                }

                writer.Write(paints);

                for (int i = 0; i < paints; i++) writer.Write(false);
            }

            return bytes.ToArray();
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
