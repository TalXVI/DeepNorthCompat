using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using BepInEx.Configuration;
using DeepNorthCompat;
using HarmonyLib;
using HearthBelow;
using HearthBelow.VoxelMagic;
using UnityEngine;

// Actual vendor density, serialization, save, and RPC methods run in embedded Mono.
// Unity objects, rendering/scene notifications, and RPC transport are fixtures.
internal static class HearthTerrainTests
{
    private const string Owner = "DeepNorthCompat.Terrain.HearthBelow";
    private const string ReplayOwner = Owner + ".Replay";
    private static readonly Type patch = typeof(Plugin).Assembly.GetType("DeepNorthCompat.HearthTerrainPatch", true)!;
    private sealed class Identity<T> : IEqualityComparer<T> where T : class
    {
        public bool Equals(T? left, T? right) => ReferenceEquals(left, right);
        public int GetHashCode(T value) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
    }
    private static readonly Dictionary<Component, Transform> transforms = new Dictionary<Component, Transform>(new Identity<Component>());
    private static readonly Dictionary<Transform, Vector3> positions = new Dictionary<Transform, Vector3>(new Identity<Transform>());
    private static readonly List<string> messages = new List<string>();
    private static uint nextId = 95000;
    private static TerrainComp comp = null!;
    private static Heightmap map = null!;
    private static VoxelZone zone = null!;
    private static ZNetView view = null!;
    private static ZDO record = null!;
    private static ZDOMan man = null!;
    private static float[] levels = null!, smooths = null!;
    private static Assembly rune = null!;
    private static bool owns { set => record.SetOwnerInternal(value ? ZDOMan.GetSessionID() : 99L); }

    internal static void Run(string lab, Action<string, Action> test)
    {
        bool server = Environment.GetEnvironmentVariable("DEEPNORTHCOMPAT_TEST_MODE") == "server";
        var fixture = new Harmony("DeepNorthCompat.Tests.HearthTerrain");
        object oldMan = AccessTools.Field(typeof(ZDOMan), "s_instance").GetValue(null);
        object oldNet = AccessTools.Field(typeof(ZNet), "m_instance").GetValue(null);
        FieldInfo threading = AccessTools.Field(typeof(BepInEx.ThreadingHelper), "<Instance>k__BackingField");
        object oldThreading = threading.GetValue(null);
        var maps = (List<Heightmap>)AccessTools.Field(typeof(Heightmap), "s_heightmaps").GetValue(null);
        var comps = (List<TerrainComp>)AccessTools.Field(typeof(TerrainComp), "s_instances").GetValue(null);
        var zones = (IDictionary)AccessTools.Field(typeof(VoxelWorld), "Zones").GetValue(null);
        var settings = new ConfigFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox", "terrain.cfg"), false);
        FieldInfo limit = AccessTools.Field(typeof(HearthBelowPlugin), "MaxOpsPerZone");
        FieldInfo carveRadius = AccessTools.Field(typeof(HearthBelowPlugin), "MaxPlayerCarveRadius");
        object? oldLimit = null, oldRadius = null;
        try
        {
            Hook(fixture, typeof(BepInEx.ThreadingHelper), "StartSyncInvoke", nameof(Skip));
            threading.SetValue(null, Fake<BepInEx.ThreadingHelper>());
            oldLimit = limit.GetValue(null); oldRadius = carveRadius.GetValue(null);
            rune = Assembly.LoadFrom(Path.Combine(lab, "BepInEx/plugins/hyleanlegend-Rune_Magic/RuneMagic.dll"));
            Guard.Build(typeof(VoxelWorld).Assembly, "99AB21C3D0923E1D7D4ED76B7EE14F76414E10CC81523BDBB1A3274A5DBCEB01");
            Guard.Build(rune, "618B66574C52F5E9C953BF75066A21C0542C4AF1814616AF0527C3C548123856");
            limit.SetValue(null, settings.Bind("fixture", "operations", 20000));
            carveRadius.SetValue(null, settings.Bind("fixture", "carve radius", 3f));
            CompatibilityInstaller.Install(_ => null, messages.Add, messages.Add, messages.Add);
            CompatibilityInstaller.Verify();
            Hook(fixture, typeof(Component), "get_transform", nameof(TransformOf));
            Hook(fixture, typeof(Transform), "get_position", nameof(PositionOf));
            Hook(fixture, typeof(Heightmap), "GetLava", nameof(NoLava), new[] { typeof(Vector3) });
            Hook(fixture, typeof(ZNetView), "InvokeRPC", nameof(Send), new[] { typeof(string), typeof(object[]) });
            Hook(fixture, typeof(ZDOMan), "ClientChanged", nameof(Skip));
            Hook(fixture, typeof(ZDOMan), "SetDirtySector", nameof(Skip));
            Hook(fixture, typeof(ZNet), "IsServer", nameof(True));
            // These only drive mesh construction, neighbor loading, and scene effects.
            foreach (string name in new[] { "Rush", "SwapNow", "MeshOpNow" }) Hook(fixture, typeof(VoxelZone), name, nameof(Skip));
            foreach (string name in new[] { "EnsureRimZones", "DirtyNeighborsForOp", "MeshNeighborsForOpNow", "OnOpApplied" })
                Hook(fixture, typeof(VoxelWorld), name, nameof(Skip));
            man = Fake<ZDOMan>(); Set(man, "m_sessionID", 1L); AccessTools.Field(typeof(ZDOMan), "s_instance").SetValue(null, man);
            AccessTools.Field(typeof(ZNet), "m_instance").SetValue(null, Fake<ZNet>());
            map = Fake<Heightmap>(); comp = Fake<TerrainComp>(); view = Fake<ZNetView>();
            SetPosition(map, Vector3.zero); SetPosition(comp, Vector3.zero);
            map.m_width = 16; map.m_scale = 1;
            Set(comp, "m_hmap", map); Set(comp, "m_width", 16); Set(comp, "m_size", 16f); Set(comp, "m_nview", view);
            maps.Add(map); comps.Add(comp);

            test("Hearth terrain: installed pickaxe cut survives heightmap leveling without the bridge", () =>
            {
                Reset(); Cut();
                Near(Top(), 9.25f, "0.75 m gradual pickaxe cut");
                Check(!VoxelWorld.IsCarvedGroundAt(new Vector3(0, 10, 0)), "standing-height target misses vendor routing");
                Check(VoxelWorld.IsCarvedGroundAt(new Vector3(0, 9.25f, 0)), "alternate target at cut uses vendor routing");
                Level(10); Near(Top(), 9.25f, "heightmap-only level leaves cut");
                object foundation = Foundation(10);
                Check(!(bool)Call(foundation, "adjustTerrain", comp)!, "rune finds heightmap already level");
                Near(Top(), 9.25f, "Foundation leaves the same cut");
                Apply(new CarveOp { Id = ++nextIdAsInt, Type = 5, Point = new Vector3(0, 9.25f, 0), Radius = 2, Depth = 1, FloorHeight = float.NegativeInfinity });
                Check(Top() > 10, "explicit voxel Raise control works");
            });
            test("Hearth terrain: absent and changed vendors install no hooks", () =>
            {
                Call(patch, "Prepare", null, null); Check(Harmony.GetPatchInfo(AccessTools.Method(typeof(TerrainComp), "LevelTerrain"))?.Transpilers.All(p => p.owner != Owner) != false, "absent hooks");
                Call(patch, "PrepareReplay", typeof(VoxelWorld).Assembly);
                Call(patch, "Prepare", typeof(VoxelWorld).Assembly, null); Call(patch, "Verify");
                Check(messages.Last().StartsWith("Terrain.HearthBelow: APPLIED;", StringComparison.Ordinal), "Hoe integration works without RuneMagic");
                Check(Harmony.GetPatchInfo(AccessTools.Method(rune.GetType("ValheimMod.Monobehaviours.FoundationRune"), "adjustTerrain"))?.Transpilers.All(p => p.owner != Owner) != false, "absent rune has no hook");
                try { Call(patch, "Prepare", typeof(Plugin).Assembly, rune); throw new Exception("changed vendor accepted"); }
                catch (TargetInvocationException error) { Check(error.InnerException is NotSupportedException, "hash rejection"); }
            });
            test("Hearth terrain: actual Hoe level fills a cut and saved replay preserves caves", () =>
            {
                Install(); Reset(); Cut(); Cave();
                float[] before = (float[])zone.Density!.Clone();
                Level(10); Near(Top(), 10, "Hoe repairs cut");
                PreservedBelow(before, 9); Check(Saved().Count > 2, "repair saved in vendor payload");
                Reload(); Near(Top(), 10, "Hoe repair survives replay"); PreservedBelow(before, 9);
                int count = Saved().Count; Level(10); Check(Saved().Count == count, "no duplicate saved repair");
            });
            test("Hearth terrain: untouched surface and a closed cave need no voxel repair", () =>
            {
                Reset(); Level(11); Near(levels[144], 1, "ordinary heightmap leveling retained");
                Check(Saved().Count == 0 && zone.Density == null, "untouched ground stays on heightmap path");
                Reset(); Cave(); float[] before = (float[])zone.Density!.Clone();
                Level(10); Call(Foundation(10), "adjustTerrain", comp);
                Check(Saved().Count == 1 && before.SequenceEqual(zone.Density!), "closed cave unchanged under level ground");
            });
            test("Hearth terrain: saved cuts are loaded before leveling a newly created zone", () =>
            {
                Reset(); Cut(); zone = Grid();
                zones[map] = zone;
                Level(10); Reload(); Near(Top(), 10, "repair uses saved cut before the vendor Update poll");
            });
            test("Hearth terrain: normal smoothing retains its one meter step and falloff", () =>
            {
                Reset(); Cut(3); Cave(); float previous = Top();
                Call(comp, "SmoothTerrain", new Vector3(0, 10, 0), 2f, false, 3f);
                Reload(); Check(Top() > previous && Top() <= previous + 1.001f, "bounded smooth step");
                Reset(); Cut();
                Call(comp, "SmoothTerrain", new Vector3(0, 10, 0), 2f, false, 3f); Reload();
                Check(Top(10, 9) < Top(), "radial falloff retained");
                Check(Top(10, 9) > 9.3f, "falloff also raises the adjacent cut");
            });
            test("Hearth terrain: a fractional raise preserves the solid sample below the cut", () =>
            {
                Reset(); Cut(); Cave(); float[] before = (float[])zone.Density!.Clone();
                Call(Foundation(9.5f, .5f), "adjustTerrain", comp);
                Near(Top(), 9.5f, "raise within one voxel"); PreservedBelow(before, 9);
                Reload(); Near(Top(), 9.5f, "fractional repair after replay");
            });
            test("Hearth terrain: Foundation repairs an unchanged heightmap and retains wave footprint", () =>
            {
                Reset(); Cut(); Cave(); float[] before = (float[])zone.Density!.Clone(); float outside = Top(10, 9);
                object foundation = Foundation(10, 0.5f);
                Check((bool)Call(foundation, "adjustTerrain", comp)!, "voxel-only rune edit reports a change");
                Near(Top(), 10, "Foundation raises cut"); Near(Top(10, 9), outside, "outside wave stays cut");
                Check(levels.All(v => v == 0), "vanilla deltas unchanged");
                Reload(); Near(Top(), 10, "rune replay"); PreservedBelow(before, 9);
            });
            test("Hearth terrain: Foundation uses clamped heights and later lowering survives replay", () =>
            {
                Reset(); Cut();
                Call(Foundation(40), "adjustTerrain", comp); Near(levels[144], 8, "rune clamp retained");
                Reload(); Near(Top(), 18, "clamped rune raise after replay");
                Call(Foundation(10), "adjustTerrain", comp); Reload(); Near(Top(), 10, "later level can lower the repair");
            });
            test("Hearth terrain: successive surface raises survive replay after the cut is filled", () =>
            {
                foreach (float depth in new[] { .75f, 1f })
                {
                    Reset(); Cut(depth); Call(Foundation(10), "adjustTerrain", comp); Reload(); Near(Top(), 10, "first repair");
                    Call(Foundation(11), "adjustTerrain", comp); Reload(); Near(Top(), 11, "second rune raise");
                    Level(12); Reload(); Near(Top(), 12, "later Hoe raise");
                }
            });
            test("Hearth terrain: nonowners write nothing; owner transfer and vendor RPC retain repairs", () =>
            {
                Reset(); Cut(); owns = false;
                Level(10); Check(Saved().Count == 1, "nonowner does not append"); Near(Top(), 9.25f, "nonowner density unchanged");
                Set(man, "m_sessionID", 2L); record.SetOwnerInternal(2); Level(10);
                Check(record.GetOwner() == 2, "bridge never claims ownership");
                byte[] bytes = record.GetByteArray(CarveData.ZdoKey, null!);
                owns = false; var packet = new ZPackage(); packet.Write((byte)0);
                var op = new CarveOp { Id = ++nextIdAsInt, Type = 2, Point = new Vector3(0, 11, 0), Radius = .5f, FloorHeight = 9.25f }; op.Write(packet);
                VoxelWorld.RPC_Carve(comp, 2, new ZPackage(packet.GetArray()));
                Check(record.GetByteArray(CarveData.ZdoKey, null!).SequenceEqual(bytes), "nonowner RPC ignored");
                owns = true; VoxelWorld.RPC_Carve(comp, 2, new ZPackage(packet.GetArray()));
                Check(Saved().Any(savedOp => savedOp.Id == op.Id), "owner receives vendor RPC");
                Reload(); Near(Top(), 11, "RPC repair replays");
                Reset(); Cut();
                var fill = Versioned(new CarveOp { Id = ++nextIdAsInt, Type = 200, Point = new Vector3(0, 10, 0), Radius = .5f, FloorHeight = 9.25f }, 1);
                packet = new ZPackage(); packet.Write((byte)0); fill.Write(packet);
                VoxelWorld.RPC_Carve(comp, 2, new ZPackage(packet.GetArray()));
                Check(Saved().Any(savedOp => savedOp.Id == fill.Id), "owner saves a fill record from vendor RPC");
                Near(Top(), 10, "fill record applies on receipt"); Reload(); Near(Top(), 10, "fill record from RPC replays");
            });
            foreach (bool vendorFirst in new[] { true, false })
                test("Hearth terrain: Heightmap Unlimited composes with " + (vendorFirst ? "vendor" : "compatibility") + " installed first", () =>
                {
                    Assembly unlimited = Assembly.LoadFrom(Path.Combine(lab, "BepInEx/plugins/Digitalroot-Heightmap_Unlimited_JvL/Digitalroot.HeightmapUnlimitedJvL.dll"));
                    Guard.Build(unlimited, "F7C2C869CA3CAE30309D657984197B700C9E541C6C18D264AE8E422A9969D4A9");
                    Type main = unlimited.GetType("Digitalroot.HeightmapUnlimitedJvL.Main", true)!;
                    Type hooks = unlimited.GetType("Digitalroot.HeightmapUnlimitedJvL.Patch+PatchTerrainComp", true)!;
                    FieldInfo instance = AccessTools.Field(main, "Instance"), min = AccessTools.Field(main, "MinHeight"), max = AccessTools.Field(main, "MaxHeight");
                    object oldInstance = instance.GetValue(null), oldMin = min.GetValue(null), oldMax = max.GetValue(null);
                    var vendor = new Harmony("DeepNorthCompat.Tests.Terrain.Unlimited");
                    try
                    {
                        instance.SetValue(null, FormatterServices.GetUninitializedObject(main));
                        min.SetValue(null, settings.Bind("fixture", "minimum", -100f)); max.SetValue(null, settings.Bind("fixture", "maximum", 100f));
                        new Harmony(Owner).UnpatchSelf();
                        if (!vendorFirst) Install();
                        vendor.Patch(AccessTools.Method(typeof(TerrainComp), "LevelTerrain"), transpiler: new HarmonyMethod(hooks, "TerrainCompLevelTerrainTranspiler"));
                        // Serverbound owns the dedicated-server application limit. The client
                        // uses this vendor hook; LevelTerrain must preserve either caller's limits.
                        if (!server) vendor.Patch(AccessTools.Method(typeof(TerrainComp), "ApplyToHeightmap"),
                            transpiler: new HarmonyMethod(hooks, "TerrainCompApplyToHeightmapTranspiler"));
                        if (vendorFirst) Install();
                        Reset(); Cut(); Level(21); Near(levels[144], 11, "vendor limit remains above eight meters");
                        Near(Top(), 21, "voxel repair uses the vendor limit");
                        if (!server) { Reload(); Near(Top(), 21, "voxel repair and vendor heightmap replay agree"); }
                        Check(Harmony.GetPatchInfo(AccessTools.Method(typeof(TerrainComp), "LevelTerrain")).Transpilers.Count(p => p.owner == Owner) == 1, "one compatibility hook");
                    }
                    finally { vendor.UnpatchSelf(); instance.SetValue(null, oldInstance); min.SetValue(null, oldMin); max.SetValue(null, oldMax); }
                });
            foreach (bool vendorFirst in new[] { true, false })
                test("Hearth terrain: Heightmap Unlimited raise composes with " + (vendorFirst ? "vendor" : "compatibility") + " installed first", () =>
                {
                    Assembly unlimited = Assembly.LoadFrom(Path.Combine(lab, "BepInEx/plugins/Digitalroot-Heightmap_Unlimited_JvL/Digitalroot.HeightmapUnlimitedJvL.dll"));
                    Type main = unlimited.GetType("Digitalroot.HeightmapUnlimitedJvL.Main", true)!;
                    Type hooks = unlimited.GetType("Digitalroot.HeightmapUnlimitedJvL.Patch+PatchTerrainComp", true)!;
                    FieldInfo instance = AccessTools.Field(main, "Instance"), min = AccessTools.Field(main, "MinHeight"), max = AccessTools.Field(main, "MaxHeight");
                    object oldInstance = instance.GetValue(null), oldMin = min.GetValue(null), oldMax = max.GetValue(null);
                    var vendor = new Harmony("DeepNorthCompat.Tests.Terrain.Unlimited");
                    MethodInfo raise = AccessTools.Method(typeof(TerrainComp), "RaiseTerrain");
                    try
                    {
                        instance.SetValue(null, FormatterServices.GetUninitializedObject(main));
                        min.SetValue(null, settings.Bind("fixture", "minimum", -100f)); max.SetValue(null, settings.Bind("fixture", "maximum", 100f));
                        new Harmony(Owner).UnpatchSelf();
                        if (!vendorFirst) Install();
                        vendor.Patch(raise, transpiler: new HarmonyMethod(hooks, "TerrainCompRaiseTerrainTranspiler"));
                        if (!server) vendor.Patch(AccessTools.Method(typeof(TerrainComp), "ApplyToHeightmap"),
                            transpiler: new HarmonyMethod(hooks, "TerrainCompApplyToHeightmapTranspiler"));
                        if (vendorFirst) Install();
                        // A 10 m step from a 9.25 m cut floor while the heightmap steps from 10 m to 20 m.
                        // The cut reaches above 20 m; otherwise the raised heightmap buries it on replay.
                        Reset(); TallCut(); Raise(10, 10); Near(levels[144], 10, "vendor limit remains above eight meters");
                        Near(Top(), 19.25f, "raised cut uses the vendor limit");
                        if (!server) { Reload(); Near(Top(), 19.25f, "raised cut after replay"); }
                        Check(Harmony.GetPatchInfo(raise).Transpilers.Count(p => p.owner == Owner) == 1, "one compatibility hook");
                    }
                    finally { vendor.UnpatchSelf(); instance.SetValue(null, oldInstance); min.SetValue(null, oldMin); max.SetValue(null, oldMax); }
                });
            test("Hearth terrain: failed IL verification rolls back only the compatibility group", () =>
            {
                MethodInfo target = AccessTools.Method(typeof(TerrainComp), "LevelTerrain");
                Reset(); Cut(); Cave(); Level(10);
                float[] before = (float[])zone.Density!.Clone();
                Call(patch, "Prepare", typeof(VoxelWorld).Assembly, rune);
                List<CodeInstruction> input = PatchProcessor.GetOriginalInstructions(target);
                int clamp = input.FindIndex(i => i.Calls(AccessTools.Method(typeof(Mathf), "Clamp", new[] { typeof(float), typeof(float), typeof(float) })));
                input[clamp - 4] = new CodeInstruction(OpCodes.Ldloc_S, (byte)8);
                var output = ((IEnumerable<CodeInstruction>)Call(patch, "RewriteLevel", input, target)!).ToArray();
                Check(input.SequenceEqual(output), "changed array index with the same clamp call is rejected intact");
                Call(patch, "Verify");
                Check(Harmony.GetPatchInfo(target)?.Transpilers.All(p => p.owner != Owner) != false, "group rolled back");
                Check(messages.Any(m => m.Contains("Terrain.HearthBelow: NOT APPLIED")), "visible rejection");
                Reload(); Near(Top(), 10, "saved repair survives rejected tool hooks"); PreservedBelow(before, 9);
            });
            test("Hearth terrain: rejected RuneMagic leaves saved repair decoding installed", () =>
            {
                Install(); Reset(); Cut(); Cave(); Level(10); float[] before = (float[])zone.Density!.Clone();
                try { Call(patch, "Prepare", typeof(VoxelWorld).Assembly, typeof(Plugin).Assembly); throw new Exception("changed rune accepted"); }
                catch (TargetInvocationException error) { Check(error.InnerException is NotSupportedException, "rune hash rejection"); }
                Call(patch, "Verify"); Reload(); PreservedBelow(before, 9);
                Check(Harmony.GetPatchInfo(AccessTools.Method(typeof(TerrainComp), "LevelTerrain"))?.Transpilers.All(p => p.owner != Owner) != false, "writer stays inactive");
            });
            test("Hearth terrain: RuneMagic's limit probe still reads LevelTerrain's clamp", () =>
            {
                // TerrainHolder.determineTerrainHeightBounds levels an inactive, unnetworked compiler
                // and falls back to 8 m when LevelTerrain throws. Vanilla limits apply in this fixture.
                Install();
                Heightmap probeMap = Fake<Heightmap>(); TerrainComp probe = Fake<TerrainComp>();
                SetPosition(probeMap, Vector3.zero); SetPosition(probe, Vector3.zero);
                probeMap.m_width = 2; probeMap.m_scale = 1; Set(probeMap, "m_heights", Enumerable.Repeat(0f, 9).ToList());
                float[] probeLevels = new float[9], probeSmooths = new float[9];
                Set(probe, "m_hmap", probeMap); Set(probe, "m_width", 2); Set(probe, "m_modifiedHeight", new bool[9]);
                Set(probe, "m_levelDelta", probeLevels); Set(probe, "m_smoothDelta", probeSmooths);
                probeSmooths[0] = 10000f; Call(probe, "LevelTerrain", Vector3.zero, 1f, true);
                Near(probeLevels[0], 8, "maximum probe");
                probeSmooths[0] = -10000f; Call(probe, "LevelTerrain", Vector3.zero, 1f, true);
                Near(probeLevels[0], -8, "minimum probe");
            });
            test("Hearth terrain: later heightmap edits by any writer show through a repair", () =>
            {
                Install();
                for (int amount = 1; amount <= 3; amount++)
                {
                    Reset(); Cut(); Level(10); Reload();
                    Raise(10, amount); Reload(); Near(Top(), 10 + amount, "Hoe raise over a repair");
                }
                // RuneMagic's Lowering rune and other mods write the delta arrays directly.
                Reset(); Cut(); Cave(); Level(10); Reload();
                for (int i = 0; i < levels.Length; i++) levels[i] += 2;
                Reload(); Near(Top(), 12, "direct raise"); Check(zone.D(9, 36, 9) < 0, "cave below the repair stays open");
                for (int i = 0; i < levels.Length; i++) levels[i] -= 3;
                Reload(); Near(Top(), 9, "direct lowering");
                Reset(); Cut(); Level(12); Reload(); Near(Top(), 12, "repair before Restoration");
                Restoration(Foundation(12)); Reload();
                Near(levels[144], 0, "Restoration reset the heightmap"); Near(Top(), 10, "repair follows Restoration");
            });
            test("Hearth terrain: Hoe raise over a cut matches an equal heightmap pit", () =>
            {
                Install();
                foreach (var input in new[] { (y: 10f, delta: 1f), (y: 8f, delta: 3f) })
                {
                    Reset(); Level(8); Reload(); Raise(input.y, input.delta); Reload(); float native = Top();
                    Near(native, input.y == 10 ? 9 : 11, "vanilla step limit");
                    Reset(); Cut(2); Near(Top(), 8, "two meter cut");
                    Raise(input.y, input.delta); Near(Top(), native, "immediate raise in the cut");
                    Reload(); Near(Top(), native, "raised cut after replay");
                }
                // A partial smooth leaves the cut 2 m below the heightmap, like the native pit above.
                Reset(); Cut(3); Call(comp, "SmoothTerrain", new Vector3(0, 10, 0), 2f, false, 3f); Reload();
                Near(Top(), 8, "partial smooth"); Raise(10, 1); Reload(); Near(Top(), 9, "raise over a partial repair");
                // At the height limit, the cut stops where the clamped heightmap stops.
                Reset(); Level(17.25f); Reload(); Raise(18, 1); Reload(); float limited = Top();
                Near(limited, 18, "vanilla height limit");
                Reset(); Level(18); Reload();
                Apply(new CarveOp { Id = ++nextIdAsInt, Type = 3, Shape = 1, Point = new Vector3(0, 18, 0), Radius = 1.6f, Depth = .75f,
                    Direction = Vector3.down, FloorHeight = float.NegativeInfinity });
                Raise(18, 1); Near(Top(), limited, "immediate raise at the limit");
                Reload(); Near(Top(), limited, "raise at the limit after replay");
                // Off the brush center, a slope: the neighbor's heightmap is above the requested height,
                // so vanilla skips it, but its cut floor is below like a native pit's.
                Reset(); levels[145] = -2; Reload(); Raise(10, 1); Reload(); float slope = Top(10, 9);
                Near(slope, 8.5f, "vanilla falloff step in a pit");
                Reset(); levels[145] = 2; Reload();
                foreach (float y in new[] { 12f, 10f }) Apply(new CarveOp { Id = ++nextIdAsInt, Type = 3, Shape = 1, Point = new Vector3(1, y, 0),
                    Radius = .5f, Depth = 2, Direction = Vector3.down, FloorHeight = float.NegativeInfinity });
                Near(Top(10, 9), 8, "cut below a higher heightmap");
                Raise(10, 1); Near(Top(10, 9), slope, "immediate raise of a cut vanilla skips");
                Reload(); Near(Top(10, 9), slope, "raise of a skipped cut after replay");
                Near(levels[145], 2, "skipped heightmap unchanged");
            });
            test("Hearth terrain: later digging and native removal keep their meaning", () =>
            {
                Install(); Reset(); Cut(); Level(10); Cut(); Reload(); Near(Top(), 9.25f, "later pickaxe cut survives replay");
                Reset(); Cut(); int cut = Saved().Single().Id; Level(10);
                CarveOp repair = Saved().First(op => op.Id != cut && op.Point.x == 0 && op.Point.z == 0);
                RemoveOps(repair.Id); Reload();
                Near(Top(), 9.25f, "restore removes the repair at the target"); Near(Top(10, 9), Top(10, 9, cutOnly: true), "and its whole action");
                // A record left after its cut is removed must not dig a new pit.
                Reset(); Cut(3); cut = Saved().Single().Id;
                Call(comp, "SmoothTerrain", new Vector3(0, 10, 0), 2f, false, 3f); Reload(); Near(Top(), 8, "partial repair");
                RemoveOps(cut); Reload(); Near(Top(), 10, "partial repair over intact ground");
                Check(zone.Density == null, "a repair over intact ground keeps the zone on the heightmap");
            });
            test("Hearth terrain: one tool action saves its repairs once or not at all", () =>
            {
                Install(); Reset(); Cut();
                uint revision = record.DataRevision; Level(10);
                Check(record.DataRevision == revision + 1, "one saved payload for the action");
                Check(Saved().Count > 2, "the action repaired several columns");
                var entry = (ConfigEntry<int>)limit.GetValue(null);
                try
                {
                    Reset(); Cut(); entry.Value = 2; Level(10);
                    Check(Saved().Count == 1, "a batch over the operation limit saves nothing");
                    Near(levels[144], 0, "the heightmap edit still applies"); Reload(); Near(Top(), 9.25f, "cut retained");
                }
                finally { entry.Value = 20000; }
            });
            test("Hearth terrain: a peer without the decoder sees the original cut", () =>
            {
                try
                {
                    Install(); Reset(); Cut(); Cave(); float[] before = (float[])zone.Density!.Clone(); Level(10); Reload(); Near(Top(), 10, "decoded");
                    List<CarveOp> saved = Saved();
                    for (int i = 0; i < saved.Count; i++) if (saved[i].Type != 3 && saved[i].Type != 0) saved[i] = Versioned(saved[i], 2);
                    record.Set(CarveData.ZdoKey, CarveData.Serialize(saved)); Reload();
                    Near(Top(), 9.25f, "an unknown record version is ignored"); PreservedBelow(before, 9);
                    Level(10);
                    new Harmony(Owner).UnpatchSelf(); new Harmony(ReplayOwner).UnpatchSelf(); Reload();
                    Near(Top(), 9.25f, "vendor-only replay leaves the cut"); PreservedBelow(before, 9);
                }
                finally { Install(); }
            });
            test("Hearth terrain: the replay decoder passes other records through unchanged and allocates nothing", () =>
            {
                Install(); Reset(); Cut(); Cave(); Level(10); Reload();
                MethodInfo bounds = AccessTools.Method(typeof(VoxelZone), "ComputeOpBounds"), density = AccessTools.Method(typeof(VoxelZone), "ApplyOpDensity");
                foreach (MethodInfo target in new[] { bounds, density })
                {
                    Patches info = Harmony.GetPatchInfo(target)!;
                    Check(info.Transpilers.Count(p => p.owner == ReplayOwner) == 1
                        && !info.Prefixes.Concat(info.Postfixes).Concat(info.Finalizers).Any(p => p.owner == ReplayOwner), "one replay transpiler on " + target.Name);
                }
                List<CarveOp> saved = Saved();
                CarveOp repair = saved.First(op => op.Type == 200);
                var others = saved.Where(op => op.Type != 200).ToList();
                for (byte type = 0; type <= 5; type++)
                    foreach (byte shape in new byte[] { 0, 1, 2 })
                        others.Add(new CarveOp { Type = type, Shape = shape, Point = new Vector3(1.3f, 8.6f, -2.2f), Radius = 1.7f, Depth = 1.2f,
                            Direction = new Vector3(.3f, -1, .2f), FloorHeight = float.NegativeInfinity });
                string Results()
                {
                    var text = new System.Text.StringBuilder();
                    foreach (CarveOp op in others)
                        foreach (bool owned in new[] { false, true })
                        {
                            object?[] args = { op, 0, 18, 0, 18, owned, null, null, null, null, null, null };
                            text.Append(bounds.Invoke(zone, args)).Append(string.Join(",", args.Skip(6))).Append(';');
                        }
                    // Planes prepare floor columns even on a dry run.
                    foreach (CarveOp op in others.Where(op => !op.IsPlane)) text.Append(density.Invoke(zone, new object[] { op, 0, 18, 0, 18, true })).Append(';');
                    return text.ToString();
                }
                string decoded = Results();
                new Harmony(ReplayOwner).UnpatchSelf();
                try { Check(Results() == decoded, "other records match HearthBelow without the decoder"); }
                finally { Install(); }
                // The decoder once boxed each record and read it by reflection, which allocated on every call.
                Func<VoxelZone, CarveOp, int, int> boundsCalls = Repeat("ComputeOpBounds", true), densityCalls = Repeat("ApplyOpDensity", false);
                foreach (CarveOp op in new[] { saved.First(op => op.Type == 3), repair })
                {
                    boundsCalls(zone, op, 100); densityCalls(zone, op, 100);
                    long before = GC.GetTotalMemory(false);
                    boundsCalls(zone, op, 2000); densityCalls(zone, op, 2000);
                    long allocated = GC.GetTotalMemory(false) - before;
                    Check(allocated < 4000, "type " + op.Type + " records allocated " + allocated + " bytes in 4000 calls");
                }
            });
        }
        finally
        {
            new Harmony(Owner).UnpatchSelf(); new Harmony(ReplayOwner).UnpatchSelf(); fixture.UnpatchSelf();
            if (map != null) { maps.Remove(map); zones.Remove(map); }
            if (comp != null) comps.Remove(comp);
            limit.SetValue(null, oldLimit); carveRadius.SetValue(null, oldRadius);
            AccessTools.Field(typeof(ZDOMan), "s_instance").SetValue(null, oldMan);
            AccessTools.Field(typeof(ZNet), "m_instance").SetValue(null, oldNet);
            threading.SetValue(null, oldThreading);
            transforms.Clear(); positions.Clear();
        }
    }

    // Calls a VoxelZone method count times over every column without reflection; ApplyOpDensity runs dry.
    private static Func<VoxelZone, CarveOp, int, int> Repeat(string name, bool bounds)
    {
        MethodInfo target = AccessTools.Method(typeof(VoxelZone), name);
        var method = new DynamicMethod("Repeat" + name, typeof(int), new[] { typeof(VoxelZone), typeof(CarveOp), typeof(int) }, typeof(VoxelZone), true);
        ILGenerator il = method.GetILGenerator();
        LocalBuilder i = il.DeclareLocal(typeof(int)), sum = il.DeclareLocal(typeof(int)), output = il.DeclareLocal(typeof(int));
        Label top = il.DefineLabel(), check = il.DefineLabel();
        il.Emit(OpCodes.Br, check); il.MarkLabel(top);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ldc_I4, 18); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ldc_I4, 18);
        if (bounds) { il.Emit(OpCodes.Ldc_I4_0); for (int k = 0; k < 6; k++) il.Emit(OpCodes.Ldloca, output); }
        else il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Call, target); il.Emit(OpCodes.Ldloc, sum); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, sum);
        il.Emit(OpCodes.Ldloc, i); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, i);
        il.MarkLabel(check); il.Emit(OpCodes.Ldloc, i); il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Blt, top);
        il.Emit(OpCodes.Ldloc, sum); il.Emit(OpCodes.Ret);
        return (Func<VoxelZone, CarveOp, int, int>)method.CreateDelegate(typeof(Func<VoxelZone, CarveOp, int, int>));
    }
    private static int nextIdAsInt = 100000;
    private static void Install()
    {
        Call(patch, "PrepareReplay", typeof(VoxelWorld).Assembly);
        Call(patch, "Prepare", typeof(VoxelWorld).Assembly, rune); Call(patch, "Verify");
        Check(messages.Last().StartsWith("Terrain.HearthBelow: APPLIED;", StringComparison.Ordinal), "patch verification: " + messages.Last());
    }
    private static void Reset()
    {
        Set(man, "m_sessionID", 1L);
        record = Fake<ZDO>(); record.m_uid = new ZDOID(1, ++nextId); record.SetOwnerInternal(1); Set(view, "m_zdo", record);
        levels = new float[289]; smooths = new float[289];
        Set(comp, "m_levelDelta", levels); Set(comp, "m_smoothDelta", smooths); Set(comp, "m_modifiedHeight", new bool[289]);
        Set(comp, "m_initialized", true); Set(comp, "m_modifiedPaint", new bool[289]);
        Set(map, "m_heights", Enumerable.Repeat(10f, 289).ToList());
        zone = Grid(); ((IDictionary)AccessTools.Field(typeof(VoxelWorld), "Zones").GetValue(null))[map] = zone;
        ((IDictionary)AccessTools.Field(typeof(VoxelWorld), "OpCache").GetValue(null)).Clear();
    }
    private static VoxelZone Grid()
    {
        var result = Fake<VoxelZone>(); result.SourceHeightmap = map; result.NX = result.NZ = 19; result.NY = 64;
        result.Origin = new Vector3(-9, -30, -9); result.HorizontalSpacing = 1;
        Set(result, "Root", Fake<GameObject>()); Set(result, "_ownedMaximumX", 16); Set(result, "_ownedMaximumZ", 16);
        var heights = new float[361];
        for (int z = 0; z < 19; z++) for (int x = 0; x < 19; x++) heights[z * 19 + x] = map.GetHeight(Mathf.Clamp(x - 1, 0, 16), Mathf.Clamp(z - 1, 0, 16));
        Set(result, "_heights", heights); Set(result, "_neighbors", new VoxelZone[9]);
        Set(result, "Operations", new List<CarveOp>()); Set(result, "AppliedKeys", new HashSet<CarveOp.Key>());
        Set(result, "_carvedChunks", new HashSet<Vector3Int>()); Set(result, "_dirty", new HashSet<Vector3Int>());
        return result;
    }
    private static void Cut(float depth = .75f) => Apply(new CarveOp { Id = ++nextIdAsInt, Type = 3, Shape = 1,
        Point = new Vector3(0, 10, 0), Radius = 1.6f, Depth = depth, Direction = Vector3.down, FloorHeight = float.NegativeInfinity });
    private static void TallCut() => Apply(new CarveOp { Id = ++nextIdAsInt, Type = 3, Shape = 1,
        Point = new Vector3(0, 20, 0), Radius = 1.6f, Depth = 10.75f, Direction = Vector3.down, FloorHeight = float.NegativeInfinity });
    private static void Cave() => Apply(new CarveOp { Id = ++nextIdAsInt, Type = 0, Shape = 1,
        Point = new Vector3(0, 6, 0), Radius = 1.5f, FloorHeight = float.NegativeInfinity });
    private static void Apply(CarveOp op)
    {
        zone.ApplyOp(op); record.Set(CarveData.ZdoKey, CarveData.Serialize(zone.Operations));
    }
    private static List<CarveOp> Saved()
    {
        Check(CarveData.Read(record.GetByteArray(CarveData.ZdoKey, null!), out List<CarveOp> operations) != CarveDataState.Unreadable, "readable saved ops");
        return operations;
    }
    private static void Reload()
    {
        List<CarveOp> operations = Saved();
        var heights = Enumerable.Repeat(10f, 289).ToList();
        comp.ApplyToHeightmap(null!, heights, heights.ToArray(), heights.ToArray(), map);
        Set(map, "m_heights", heights); zone = Grid();
        ((IDictionary)AccessTools.Field(typeof(VoxelWorld), "Zones").GetValue(null))[map] = zone;
        foreach (CarveOp operation in operations) zone.ApplyOp(operation);
    }
    private static float Top(int x = 9, int z = 9)
    {
        for (int y = zone.NY - 2; y >= 2; y--)
        {
            float below = zone.D(x, y, z), above = zone.D(x, y + 1, z);
            if (below > 0 && above <= 0) return zone.Origin.y + y + below / (below - above);
        }
        throw new Exception("No surface crossing");
    }
    private static void PreservedBelow(float[] before, float ceiling)
    {
        for (int y = 0; zone.Origin.y + y <= ceiling; y++)
            for (int z = 0; z < zone.NZ; z++) for (int x = 0; x < zone.NX; x++)
            { int i = (y * zone.NZ + z) * zone.NX + x; Check(zone.Density![i] == before[i], "changed density below edit at " + x + "," + y + "," + z); }
    }
    private static void Level(float height) => Call(comp, "LevelTerrain", new Vector3(0, height, 0), 2f, false);
    private static void Raise(float height, float delta) => Call(comp, "RaiseTerrain", new Vector3(0, height, 0), 2f, delta, false, 1f);
    private static void RemoveOps(int id) => AccessTools.Method(typeof(VoxelWorld), "RemoveOps").Invoke(null, new object[] { comp, new HashSet<int> { id } });
    private static float Top(int x, int z, bool cutOnly)
    {
        // The same column with only the pickaxe cut replayed over the current heightmap.
        List<CarveOp> saved = Saved(); var only = saved.Where(op => op.Type == 3).ToList();
        record.Set(CarveData.ZdoKey, CarveData.Serialize(only)); Reload(); float result = Top(x, z);
        record.Set(CarveData.ZdoKey, CarveData.Serialize(saved)); Reload(); return result;
    }
    private static CarveOp Versioned(CarveOp op, int version)
    {
        op.Sequence = (op.Sequence & 0xFFFFFFFFL) | ((long)(0x444E4300 | version) << 32); return op;
    }
    private static void Restoration(object foundation)
    {
        object restore = FormatterServices.GetUninitializedObject(rune.GetType("ValheimMod.Monobehaviours.RestorationRune", true)!);
        SetPointer((UnityEngine.Object)restore); SetPosition((Component)restore, Vector3.zero);
        Set(restore, "terrainHolder", AccessTools.Field(foundation.GetType(), "terrainHolder").GetValue(foundation));
        Set(restore, "adjustmentSpeed", 100f); Set(restore, "minAdjust", 100f);
        Call(restore, "adjustTerrain", comp, 1f, 2f);
    }
    private static object Foundation(float height, float wave = 3)
    {
        Type holderType = rune.GetType("ValheimMod.TerrainHolder", true)!;
        object terrainHolder = FormatterServices.GetUninitializedObject(holderType);
        Set(terrainHolder, "heightmaps", new Dictionary<TerrainComp, Heightmap> { { comp, map } });
        Set(terrainHolder, "levelDeltas", new Dictionary<TerrainComp, float[]> { { comp, levels } });
        Set(terrainHolder, "smoothDeltas", new Dictionary<TerrainComp, float[]> { { comp, smooths } });
        Set(terrainHolder, "modifiedHeights", new Dictionary<TerrainComp, bool[]> { { comp, new bool[289] } });
        Set(terrainHolder, "heightsWithTerrainMods", new Dictionary<TerrainComp, float[]> { { comp, Enumerable.Repeat(10f, 289).ToArray() } });
        object foundation = FormatterServices.GetUninitializedObject(rune.GetType("ValheimMod.Monobehaviours.FoundationRune", true)!);
        SetPointer((UnityEngine.Object)foundation); SetPosition((Component)foundation, new Vector3(0, height, 0));
        Set(foundation, "terrainHolder", terrainHolder); Set(foundation, "effectRadius", 2f);
        Type alterationType = rune.GetType("ValheimMod.RadialProgressiveAlteration", true)!;
        object alteration = Activator.CreateInstance(alterationType, new object[] { Vector3.zero, 1f, 1f })!;
        AccessTools.Property(alterationType, "currentDistance").SetValue(alteration, wave); Set(foundation, "alteration", alteration);
        return foundation;
    }
    private static void SetPosition(Component c, Vector3 position) { var t = Fake<Transform>(); transforms[c] = t; positions[t] = position; }
    private static T Fake<T>() { var result = (T)FormatterServices.GetUninitializedObject(typeof(T)); if (result is UnityEngine.Object obj) SetPointer(obj); return result; }
    private static void SetPointer(UnityEngine.Object obj) => Set(obj, "m_CachedPtr", new IntPtr(++nextId));
    private static void Set(object target, string field, object value) => (AccessTools.Field(target.GetType(), field) ?? throw new MissingFieldException(target.GetType().Name, field)).SetValue(target, value);
    private static object? Call(object target, string method, params object?[] args) => AccessTools.Method(target as Type ?? target.GetType(), method).Invoke(target is Type ? null : target, args);
    private static void Hook(Harmony h, Type type, string method, string prefix, Type[]? args = null) => h.Patch(AccessTools.Method(type, method, args), prefix: new HarmonyMethod(typeof(HearthTerrainTests), prefix));
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Near(float value, float expected, string message) => Check(Math.Abs(value - expected) < .001f, message + ": " + value + " != " + expected);
    private static bool TransformOf(Component __instance, ref Transform __result) { __result = transforms[__instance]; return false; }
    private static bool PositionOf(Transform __instance, ref Vector3 __result) { __result = positions[__instance]; return false; }
    private static bool NoLava(ref float __result) { __result = 0; return false; }
    private static bool True(ref bool __result) { __result = true; return false; }
    private static bool Send(string __0) => throw new Exception("Owner fixture unexpectedly sent RPC " + __0);
    private static bool Skip() => false;
}
