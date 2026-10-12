using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using BepInEx.Configuration;
using DeepNorthCompat;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

internal static class PlantingTests
{
    private const string Fixture = "DeepNorthCompat.Tests.Planting";
    private const string CostOwner = "DeepNorthCompat.PlantEasily.GridCost";
    private const string DustOwner = "DeepNorthCompat.PlantEverything.Dust";
    private static readonly Dictionary<Object, string> names = new Dictionary<Object, string>();
    private static readonly List<IntPtr> pointers = new List<IntPtr>();
    private static readonly List<(int Index, int[] Amounts)> cells = new List<(int, int[])>();
    private static readonly List<Piece> added = new List<Piece>();
    private static int identifier;
    private static int throwAt = -1;

    private static bool Skip() => false;
    private static bool Name(Object __instance, ref string __result) { __result = names.TryGetValue(__instance, out string? value) ? value : "fixture"; return false; }
    private static bool Ghost(Piece __1, int __4)
    {
        cells.Add((__4, __1.m_resources.Select(r => r.m_amount).ToArray()));
        if (__4 == throwAt) throw new InvalidOperationException("test ghost failure");
        return false;
    }
    // Stands in for upstream's initializer, which needs a loaded ObjectDB; it rebuilds the same two effects.
    private static bool Init()
    {
        foreach (Piece piece in added) piece.m_placeEffect.m_effectPrefabs = Effects();
        return false;
    }

    private static T Fake<T>(string name) where T : Object
    {
        T value = (T)FormatterServices.GetUninitializedObject(typeof(T));
        IntPtr pointer = Marshal.AllocHGlobal(sizeof(long)); pointers.Add(pointer); Marshal.WriteInt64(pointer, ++identifier);
        AccessTools.Field(typeof(Object), "m_CachedPtr").SetValue(value, pointer); names.Add(value, name); return value;
    }
    private static EffectList.EffectData[] Effects() => new[]
    {
        new EffectList.EffectData { m_prefab = Fake<GameObject>("vfx_Place_wood_pole") },
        new EffectList.EffectData { m_prefab = Fake<GameObject>("sfx_build_cultivator") }
    };
    private static Piece Placed(string name)
    {
        Piece piece = Fake<Piece>(name); piece.m_placeEffect = new EffectList { m_effectPrefabs = Effects() }; return piece;
    }
    private static string[] EffectNames(Piece piece) => piece.m_placeEffect.m_effectPrefabs.Select(effect => names[effect.m_prefab]).ToArray();
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static HarmonyMethod Hook(string name) => new HarmonyMethod(AccessTools.Method(typeof(PlantingTests), name)) { priority = Priority.Last };
    private static bool Ours(string owner) => Harmony.GetAllPatchedMethods().Any(method => Harmony.GetPatchInfo(method)!.Owners.Contains(owner));
    private static Assembly? Load(string profile, string file)
    {
        string? path = Directory.GetFiles(Path.Combine(profile, "BepInEx/plugins"), file, SearchOption.AllDirectories).SingleOrDefault();
        return path == null ? null : Assembly.LoadFrom(path);
    }

    internal static void Run(string profile, Action<string, Action> test)
    {
        bool server = Environment.GetEnvironmentVariable("DEEPNORTHCOMPAT_TEST_MODE") == "server";
        Type costPatch = typeof(Plugin).Assembly.GetType("DeepNorthCompat.PlantEasilyCostPatch", true)!;
        Type dustPatch = typeof(Plugin).Assembly.GetType("DeepNorthCompat.PlantEverythingDustPatch", true)!;
        Assembly self = typeof(PlantingTests).Assembly;
        if (server)
        {
            test("Planting groups remain inactive on dedicated servers", () =>
            {
                AccessTools.Method(costPatch, "Prepare").Invoke(null, new object[] { self });
                AccessTools.Method(dustPatch, "Prepare").Invoke(null, new object[] { self, self, self });
                Check(!Ours(CostOwner) && !Ours(DustOwner), "dedicated role leaves planting untouched");
            });
            return;
        }
        var info = new List<string>(); var errors = new List<string>();
        void Install(Func<string, Assembly?> resolve) => CompatibilityInstaller.Install(resolve, info.Add, info.Add, errors.Add);
        void Case(string name, Action action) => test("Planting " + name, () =>
        {
            info.Clear(); errors.Clear(); cells.Clear(); added.Clear(); throwAt = -1;
            try { action(); }
            finally
            {
                foreach (string owner in new[] { Fixture, CostOwner, DustOwner }) new Harmony(owner).UnpatchSelf();
                foreach (IntPtr pointer in pointers) Marshal.FreeHGlobal(pointer);
                pointers.Clear(); names.Clear();
            }
        });

        Assembly? easily = Load(profile, "Advize_PlantEasily.dll");
        if (easily != null)
        {
            Type grid = easily.GetType("Advize_PlantEasily.GhostGrid", true)!;
            Type state = easily.GetType("Advize_PlantEasily.PlacementState", true)!;
            MethodInfo update = AccessTools.Method(grid, "Update");
            var file = new ConfigFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox/planteasily-never-written.cfg"), false) { SaveOnConfigSet = false };
            Type settings = easily.GetType("Advize_PlantEasily.ModConfig", true)!;
            object config = FormatterServices.GetUninitializedObject(settings);
            AccessTools.Field(settings, "rows").SetValue(config, file.Bind("Grid", "Rows", 1));
            AccessTools.Field(settings, "columns").SetValue(config, file.Bind("Grid", "Columns", 501));
            AccessTools.Field(settings, "maxConcurrentPlacements").SetValue(config, file.Bind("Grid", "Maximum", 1000));
            AccessTools.Field(settings, "ghostUpdateBatchSize").SetValue(config, file.Bind("Grid", "Batch", 10000));
            AccessTools.Field(settings, "showGridDirections").SetValue(config, file.Bind("Grid", "Directions", false));
            AccessTools.Field(easily.GetType("Advize_PlantEasily.ModContext", true), "config").SetValue(null, config);

            // Runs upstream's own Update loop and cost scaling; only the Unity-backed ghost work is replaced.
            Piece.Requirement[] Grid(params int[] amounts)
            {
                var fixture = new Harmony(Fixture);
                fixture.Patch(AccessTools.Method(grid, "UpdateVisibility"), prefix: Hook(nameof(Skip)));
                fixture.Patch(AccessTools.Method(grid, "UpdateGridVersion"), prefix: Hook(nameof(Skip)));
                fixture.Patch(AccessTools.Method(grid, "UpdateGhost"), prefix: Hook(nameof(Ghost)));
                Piece piece = Fake<Piece>("Ashwood_Sapling");
                piece.m_resources = amounts.Select(amount => new Piece.Requirement { m_amount = amount }).ToArray();
                AccessTools.Field(state, "PlacementGhost").SetValue(null, Fake<GameObject>("ghost"));
                AccessTools.Field(state, "Piece").SetValue(null, piece);
                AccessTools.Field(grid, "_ghostUpdateIndex").SetValue(null, 0);
                return piece.m_resources;
            }
            void Applied()
            {
                Install(guid => guid == "advize.PlantEasily" ? easily : null);
                AccessTools.Method(costPatch, "Verify").Invoke(null, null);
                Check(errors.Count == 0 && info.Any(line => line.StartsWith("PlantEasily.GridCost: APPLIED")), "audited grid group installs: " + string.Join(Environment.NewLine, errors));
            }

            Case("upstream grid scales only the first ingredient", () =>
            {
                Piece.Requirement[] recipe = Grid(2, 3);
                update.Invoke(null, new object?[] { null });
                Check(cells.Count == 502 && cells.Any(cell => cell.Index == 500), "every cell evaluated");
                Check(cells.All(cell => cell.Amounts[0] == 2 * (cell.Index + 1) && cell.Amounts[1] == 3), "upstream defect changed");
                Check(recipe[0].m_amount == 2 && recipe[1].m_amount == 3, "upstream restore");
            });
            Case("grid scales and restores every ingredient", () =>
            {
                Applied();
                Piece.Requirement[] recipe = Grid(2, 3, 0);
                for (int frame = 0; frame < 2; frame++)
                {
                    cells.Clear(); AccessTools.Field(grid, "_ghostUpdateIndex").SetValue(null, 0);
                    update.Invoke(null, new object?[] { null });
                    Check(cells.Count == 502, "every cell evaluated");
                    Check(cells.All(cell => cell.Amounts.SequenceEqual(new[] { 2 * (cell.Index + 1), 3 * (cell.Index + 1), 0 })), "per-cell cost");
                    Check(cells.Single(cell => cell.Index == 500).Amounts.SequenceEqual(new[] { 1002, 1503, 0 }), "last cell cost");
                    Check(recipe.Select(r => r.m_amount).SequenceEqual(new[] { 2, 3, 0 }), "recipe restored");
                }
            });
            Case("grid keeps single-ingredient recipes on upstream's path", () =>
            {
                Applied();
                Piece.Requirement[] recipe = Grid(2);
                update.Invoke(null, new object?[] { null });
                Check(cells.Count == 502 && cells.All(cell => cell.Amounts[0] == 2 * (cell.Index + 1)), "single ingredient cost");
                Check(recipe[0].m_amount == 2 && AccessTools.Field(costPatch, "resources").GetValue(null) == null, "single ingredient untouched");
            });
            Case("grid restores every ingredient when a ghost update throws", () =>
            {
                Applied();
                Piece.Requirement[] recipe = Grid(2, 3);
                throwAt = 3;
                try { update.Invoke(null, new object?[] { null }); throw new Exception("missing ghost failure"); }
                catch (TargetInvocationException exception) when (exception.InnerException is InvalidOperationException) { }
                Check(cells.Last().Amounts.SequenceEqual(new[] { 8, 12 }), "failed cell cost");
                Check(recipe[0].m_amount == 2 && recipe[1].m_amount == 3, "recipe restored after failure");
                throwAt = -1; cells.Clear(); AccessTools.Field(grid, "_ghostUpdateIndex").SetValue(null, 0);
                update.Invoke(null, new object?[] { null });
                Check(cells.All(cell => cell.Amounts.SequenceEqual(new[] { 2 * (cell.Index + 1), 3 * (cell.Index + 1) })), "next frame cost");
            });
            Case("missing grid hook registration rolls back the group", () =>
            {
                Install(guid => guid == "advize.PlantEasily" ? easily : null);
                new Harmony(CostOwner).Unpatch(AccessTools.Method(grid, "UpdateGhost"), HarmonyPatchType.Prefix, CostOwner);
                AccessTools.Method(costPatch, "Verify").Invoke(null, null);
                Check(errors.Count == 1 && errors[0].StartsWith("PlantEasily.GridCost: NOT APPLIED"), "deferred hook omission is visible");
                Check(!Ours(CostOwner), "no partial grid hooks");
            });
        }
        Case("unsupported PlantEasily rejects only its group", () =>
        {
            Install(guid => guid == "advize.PlantEasily" ? self : null);
            Check(errors.Count == 1 && errors[0].StartsWith("PlantEasily.GridCost: NOT APPLIED"), "unsupported grid build is visible");
            Check(!Ours(CostOwner), "rejected grid group leaves no hooks");
        });

        Assembly? everything = Load(profile, "Advize_PlantEverything.dll");
        Assembly? cultivatorDust = Load(profile, "NoCultivatorDust.dll");
        Assembly? buildDust = Load(profile, "NoBuildDust.dll");
        if (everything != null && (cultivatorDust ?? buildDust) != null)
        {
            Type entry = everything.GetType("Advize_PlantEverything.PieceDB", true)!;
            var refs = (IList)AccessTools.Field(everything.GetType("Advize_PlantEverything.StaticMembers", true), "pieceRefs").GetValue(null);
            MethodInfo init = AccessTools.Method(everything.GetType("Advize_PlantEverything.PluginInitUtils", true), "InitPieces");
            Piece[] Pieces()
            {
                var fixture = new Harmony(Fixture);
                fixture.Patch(AccessTools.PropertyGetter(typeof(Object), "name"), prefix: Hook(nameof(Name)));
                fixture.Patch(init, prefix: Hook(nameof(Init)));
                refs.Clear();
                foreach (string name in new[] { "RaspberryBush", "Pickable_Flint" })
                {
                    object reference = FormatterServices.GetUninitializedObject(entry);
                    Piece piece = Placed(name); added.Add(piece);
                    AccessTools.Field(entry, "piece").SetValue(reference, piece); refs.Add(reference);
                }
                return added.ToArray();
            }
            Assembly? Resolve(string guid, bool dust) => guid == "advize.PlantEverything" ? everything
                : !dust ? null : guid == "Azumatt.NoCultivatorDust" ? cultivatorDust : guid == "Azumatt.NoBuildDust" ? buildDust : null;

            Case("dust stays removed across repeated piece initialization", () =>
            {
                try
                {
                    Piece[] pieces = Pieces(); Piece other = Placed("piece_workbench");
                    Install(guid => Resolve(guid, true));
                    AccessTools.Method(dustPatch, "Verify").Invoke(null, null);
                    Check(errors.Count == 0 && info.Any(line => line.StartsWith("PlantEverything.Dust: APPLIED")), "audited dust group installs: " + string.Join(Environment.NewLine, errors));
                    for (int join = 0; join < 3; join++)
                    {
                        init.Invoke(null, null);
                        Check(pieces.All(piece => EffectNames(piece).SequenceEqual(new[] { "sfx_build_cultivator" })), "dust removed, sound kept");
                        Check(EffectNames(other).SequenceEqual(new[] { "vfx_Place_wood_pole", "sfx_build_cultivator" }), "unrelated piece untouched");
                    }
                    Check(errors.Count == 0, "no initialization errors");
                }
                finally { refs.Clear(); }
            });
            Case("dust group is inactive without a placement dust mod", () =>
            {
                try
                {
                    Piece[] pieces = Pieces();
                    Install(guid => Resolve(guid, false));
                    AccessTools.Method(dustPatch, "Verify").Invoke(null, null);
                    init.Invoke(null, null);
                    Check(errors.Count == 0 && !Ours(DustOwner) && info.Contains("PlantEverything.Dust: no placement dust mod installed; inactive."), "inactive");
                    Check(pieces.All(piece => EffectNames(piece).SequenceEqual(new[] { "vfx_Place_wood_pole", "sfx_build_cultivator" })), "upstream effects retained");
                }
                finally { refs.Clear(); }
            });
            Case("missing dust hook registration rolls back the group", () =>
            {
                Install(guid => Resolve(guid, true));
                new Harmony(DustOwner).UnpatchSelf();
                AccessTools.Method(dustPatch, "Verify").Invoke(null, null);
                Check(errors.Count == 1 && errors[0].StartsWith("PlantEverything.Dust: NOT APPLIED"), "deferred hook omission is visible");
            });
            Case("unsupported dust mod rejects only the dust group", () =>
            {
                Install(guid => guid == "Azumatt.NoBuildDust" ? self : Resolve(guid, true));
                Check(errors.Count == 1 && errors[0].StartsWith("PlantEverything.Dust: NOT APPLIED"), "unsupported dust build is visible");
                Check(!Ours(DustOwner), "rejected dust group leaves no hooks");
            });
        }
        Case("unsupported PlantEverything rejects only its group", () =>
        {
            Install(guid => guid == "advize.PlantEverything" || guid == "Azumatt.NoCultivatorDust" ? self : null);
            Check(errors.Count == 1 && errors[0].StartsWith("PlantEverything.Dust: NOT APPLIED"), "unsupported PlantEverything build is visible");
            Check(!Ours(DustOwner), "rejected dust group leaves no hooks");
        });
    }
}
