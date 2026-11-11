using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using DeepNorthCompat;
using HarmonyLib;
using UnityEngine;

// Readiness, LateUpdate, resets, scheduling, vendor requests and completion run unchanged.
// Unity handles, camera/world availability, collider assignment and vegetation creation are fixtures.
internal static class VpoTerrainGrassTests
{
    private const string Owner = "DeepNorthCompat.VPO.TerrainGrass";
    private const string FixtureOwner = "DeepNorthCompat.Tests.VpoTerrainGrass";
    private const string VpoOwner = "dev.ontrigger.vpo";
    private static Scene current = null!;

    internal static void Run(string profile, Action<string, Action> test)
    {
        test("VPO terrain grass handles absence and rejects unknown bytes without vendor fixtures", () =>
        {
            VpoTerrainGrassPatch.Prepare(null);
            VpoTerrainGrassPatch.Verify();
            Check(!Harmony.HasAnyPatches(Owner), "absence installed a grass hook");
            Reject(typeof(VpoTerrainGrassTests).Assembly);
        });

        string? path = Directory.GetFiles(Path.Combine(profile, "BepInEx/plugins"),
            "ValheimPerformanceOptimizations.dll", SearchOption.AllDirectories).SingleOrDefault();
        if (path == null) return;

        Assembly vendor = Assembly.LoadFrom(path);
        Guard.Build(vendor, ExpectedBuilds.Vpo);
        if (Guard.Sha256(path) != ExpectedBuilds.Vpo210)
        {
            test("VPO terrain grass rejects a known older vendor without aborting other integrations", () => Reject(vendor));
            return;
        }

        void Case(string name, Action<Scene> action) => test("VPO terrain grass " + name, () =>
        {
            using (var scene = new Scene(vendor)) action(scene);
        });

        Case("waits for a neighboring collider before consuming an edit reset", scene =>
        {
            scene.Install();
            Vector2Int inside = new Vector2Int(5, 0), edge = new Vector2Int(4, 0), outside = new Vector2Int(6, 0);
            scene.Prime();
            var before = new Dictionary<Vector2Int, int>(scene.Builds);
            scene.Begin(scene.Neighbor);
            scene.Clutter.ResetGrass(new Vector3(36f, 0f, 0f), 2f);
            scene.Frame();
            Check(scene.Builds.All(pair => before[pair.Key] == pair.Value), "grass regenerated while the neighboring collider was pending");

            scene.Complete(scene.Neighbor);
            scene.Frame();
            Check(scene.Committed.Contains(scene.Neighbor), "the real vendor callback did not install the neighboring collider");
            Check(scene.Builds[inside] == before[inside] + 1 && scene.Builds[edge] == before[edge] + 1,
                "completion did not preserve the original interior and boundary edit reset");
            Check(scene.Builds[outside] == before[outside], "completion expanded the edit into the nearest non-overlapping patch");
            Check(scene.Builds.Sum(pair => pair.Value - before[pair.Key]) == 2, "completion rebuilt patches outside the original edit footprint");
        });

        Case("preserves the camera-tile gate and a small edit across repeated bakes", scene =>
        {
            scene.Install();
            scene.Prime();
            Vector2Int edited = new Vector2Int(0, 0), outside = new Vector2Int(1, 0);
            for (int cycle = 0; cycle < 3; cycle++)
            {
                int total = scene.Builds.Values.Sum(), count = scene.Builds[edited], untouched = scene.Builds[outside];
                scene.Begin(scene.Map);
                scene.Clutter.ResetGrass(Vector3.zero, 2f);
                scene.Frame();
                Check(scene.Builds.Values.Sum() == total, "the camera-tile gate allowed grass before completion");
                scene.Complete(scene.Map);
                scene.Frame();
                Check(scene.Builds[edited] == count + 1 && scene.Builds[outside] == untouched
                    && scene.Builds.Values.Sum() == total + 1, "a small edit caused a tile-wide or repeated rebuild");
                scene.Frame();
                Check(scene.Builds.Values.Sum() == total + 1, "an unchanged frame rebuilt grass again");
            }
        });

        Case("checks the player's grass area when the camera is offset", scene =>
        {
            scene.OffsetCamera();
            scene.Install();
            scene.Prime();
            int before = scene.Builds.Values.Sum();
            scene.Begin(scene.Neighbor);
            scene.Clutter.ResetGrass(new Vector3(-36f, 0f, 0f), 2f);
            scene.Frame();
            Check(scene.Builds.Values.Sum() == before, "the camera offset hid a pending tile in the player's grass area");
            scene.Complete(scene.Neighbor);
            scene.Frame();
            Check(scene.Builds.Values.Sum() == before + 2, "the player-area edit reset was lost or expanded");
        });

        Case("ignores pending terrain outside grass range and preserves vanilla queued work", scene =>
        {
            scene.Install();
            scene.Prime();
            int before = scene.Builds.Values.Sum();
            scene.Begin(scene.Distant);
            scene.Clutter.ResetGrass(Vector3.zero, 2f);
            scene.Frame();
            Check(scene.Builds.Values.Sum() == before + 1, "a pending tile outside grass range blocked the edit");

            AccessTools.Field(typeof(Heightmap), "m_doLateUpdate").SetValue(scene.Map, 2);
            scene.Clutter.ResetGrass(Vector3.zero, 2f);
            scene.Frame();
            Check(scene.Builds.Values.Sum() == before + 1, "the integration bypassed vanilla's queued-regeneration gate");
            AccessTools.Field(typeof(Heightmap), "m_doLateUpdate").SetValue(scene.Map, 0);
            scene.Frame();
            Check(scene.Builds.Values.Sum() == before + 2, "the original edit reset was lost while vanilla work was queued");
        });

        test("VPO terrain grass stays inactive without threaded collision hooks", () =>
        {
            using (var scene = new Scene(vendor, threaded: false))
            {
                scene.Install();
                Check(!Harmony.HasAnyPatches(Owner) && scene.Messages.Any(message => message.Contains("inactive")),
                    "an unused threaded path reported an active integration");
            }
        });

        Case("rejects a lost registration while preserving upstream readiness and completion", scene =>
        {
            VpoTerrainGrassPatch.Prepare(vendor);
            new Harmony(Owner).UnpatchSelf();
            VpoTerrainGrassPatch.Verify();
            Check(scene.Messages.Any(message => message.Contains("VPO.TerrainGrass: NOT APPLIED")), "lost registration was not reported");
            scene.Prime();
            int before = scene.Builds.Values.Sum();
            scene.Begin(scene.Map);
            scene.Clutter.ResetGrass(Vector3.zero, 2f);
            scene.Frame();
            Check(scene.Builds.Values.Sum() == before, "rejection removed the vendor readiness gate");
            scene.Complete(scene.Map);
            scene.Frame();
            Check(scene.Committed.Contains(scene.Map) && scene.Builds.Values.Sum() == before + 1,
                "rejection changed vendor completion or expanded the edit footprint");
        });
    }

    internal static void RunServer(Action<string, Action> test) => test("VPO terrain grass stays inactive on dedicated builds", () =>
    {
        VpoTerrainGrassPatch.Prepare(typeof(VpoTerrainGrassTests).Assembly);
        VpoTerrainGrassPatch.Verify();
        Check(!Harmony.HasAnyPatches(Owner), "dedicated role installed client grass hooks");
    });

    private sealed class Scene : IDisposable
    {
        internal readonly Heightmap Map, Neighbor, Distant;
        internal readonly ClutterSystem Clutter;
        internal readonly Camera Camera;
        internal readonly Dictionary<Vector2Int, int> Builds = new Dictionary<Vector2Int, int>();
        internal readonly HashSet<Heightmap> Committed = new HashSet<Heightmap>();
        internal readonly List<string> Messages = new List<string>();
        private readonly Assembly vendor;
        private readonly Harmony fixture = new Harmony(FixtureOwner), upstream = new Harmony(VpoOwner);
        private readonly List<(MethodInfo Target, MethodInfo Hook)> registrations = new List<(MethodInfo, MethodInfo)>();
        private readonly List<IntPtr> pointers = new List<IntPtr>();
        private readonly Dictionary<Component, Transform> transforms = new Dictionary<Component, Transform>();
        private readonly Dictionary<Transform, Vector3> positions = new Dictionary<Transform, Vector3>();
        private readonly Dictionary<MeshCollider, Heightmap> colliders = new Dictionary<MeshCollider, Heightmap>();
        private readonly Heightmap[] previousMaps = Heightmap.GetAllHeightmaps().ToArray();
        private readonly Player previousPlayer = Player.m_localPlayer;
        private readonly object? previousClutter = AccessTools.Field(typeof(ClutterSystem), "m_instance").GetValue(null);
        private readonly Action<string> info = CompatibilityInstaller.Info, warning = CompatibilityInstaller.Warning, error = CompatibilityInstaller.Error;
        private readonly Type patchData = AccessTools.Inner(typeof(ClutterSystem), "PatchData");
        private readonly IDictionary finished;
        private readonly object baker;
        private readonly MethodInfo request, frame = AccessTools.Method(typeof(ClutterSystem), "LateUpdate");
        private readonly Action<Heightmap> complete;

        internal Scene(Assembly vendor, bool threaded = true)
        {
            this.vendor = vendor;
            current = this;
            Type patches = vendor.GetType("ValheimPerformanceOptimizations.Patches.HeightmapGeneration.ThreadedHeightmapCollisionBakePatch", true)!;
            finished = (IDictionary)AccessTools.Field(patches, "HeightmapFinished").GetValue(null);
            complete = (Action<Heightmap>)Delegate.CreateDelegate(typeof(Action<Heightmap>), AccessTools.Method(patches, "OnBakeDone"));
            Type bakerType = vendor.GetType("ValheimPerformanceOptimizations.Patches.HeightmapGeneration.VPOTerrainCollisionBaker", true)!;
            baker = FormatterServices.GetUninitializedObject(bakerType);
            request = AccessTools.Method(bakerType, "RequestAsyncCollisionBake");
            foreach (string name in new[] { "bakeRequests", "pendingHeightmaps" }) Initialize(baker, name);

            Map = MakeMap(Vector3.zero);
            Neighbor = MakeMap(new Vector3(64f, 0f, 0f));
            Distant = MakeMap(new Vector3(192f, 0f, 0f));
            Camera = Fake<Camera>();
            Place(Camera, Vector3.zero);
            Clutter = Fake<ClutterSystem>();
            Clutter.m_grassPatchSize = 8f;
            Clutter.m_distance = 48f;
            foreach (string name in new[] { "m_patches", "m_freePatches", "m_tempToRemovePair" }) Initialize(Clutter, name);
            FieldInfo quality = AccessTools.Field(typeof(ClutterSystem), "m_quality");
            quality.SetValue(Clutter, Enum.ToObject(quality.FieldType, 1));
            AccessTools.Field(typeof(ClutterSystem), "m_instance").SetValue(null, Clutter);
            Player.m_localPlayer = null!;
            Heightmap.GetAllHeightmaps().Clear();
            Heightmap.GetAllHeightmaps().AddRange(new[] { Map, Neighbor, Distant });
            CompatibilityInstaller.Info = CompatibilityInstaller.Warning = CompatibilityInstaller.Error = Messages.Add;

            fixture.Patch(AccessTools.PropertyGetter(typeof(Component), "transform"), prefix: Hook(nameof(TransformOf)));
            fixture.Patch(AccessTools.PropertyGetter(typeof(Transform), "position"), prefix: Hook(nameof(PositionOf)));
            fixture.Patch(AccessTools.PropertySetter(typeof(MeshCollider), "sharedMesh"), prefix: Hook(nameof(Commit)));
            fixture.Patch(AccessTools.Method(typeof(Utils), "GetMainCamera"), prefix: Hook(nameof(CameraOf)));
            fixture.Patch(AccessTools.Method(typeof(GameCamera), "InFreeFly"), prefix: Hook(nameof(False)));
            fixture.Patch(AccessTools.Method(typeof(Heightmap).Assembly.GetType("RenderGroupSystem", true), "IsGroupActive"), prefix: Hook(nameof(True)));
            fixture.Patch(AccessTools.Method(typeof(Shader), "SetGlobalVector", new[] { typeof(string), typeof(Vector4) }), prefix: Hook(nameof(Skip)));
            fixture.Patch(AccessTools.Method(typeof(ClutterSystem), "GenerateVegPatch"), transpiler: Hook(nameof(VegetationFixture)));

            if (threaded)
            {
                AddUpstream(AccessTools.Method(typeof(Heightmap), "RebuildCollisionMesh"), AccessTools.Method(patches, "RebuildCollisionMeshPatch"), true);
                AddUpstream(AccessTools.Method(typeof(ClutterSystem), "IsHeightmapReady"), AccessTools.Method(patches, "IsHeightmapReadyPatch"), false);
            }
        }

        internal void Install() { VpoTerrainGrassPatch.Prepare(vendor); VpoTerrainGrassPatch.Verify(); }

        internal void Begin(Heightmap map)
        {
            finished[map] = false;
            Committed.Remove(map);
            Check((bool)request.Invoke(baker, new object[] { map, complete }), "the real vendor request did not queue its callback");
        }

        internal void Complete(Heightmap map)
        {
            IList requests = (IList)AccessTools.Field(baker.GetType(), "bakeRequests").GetValue(baker);
            object entry = requests.Cast<object>().Single(item => ReferenceEquals(AccessTools.Field(item.GetType(), "Heightmap").GetValue(item), map));
            var action = (Action<Heightmap>)AccessTools.Field(entry.GetType(), "Callback").GetValue(entry);
            action(map);
            requests.Remove(entry);
        }

        internal void Frame() => frame.Invoke(Clutter, Array.Empty<object>());

        internal void OffsetCamera()
        {
            positions[transforms[Camera]] = new Vector3(24f, 0f, 0f);
            positions[transforms[Neighbor]] = new Vector3(-64f, 0f, 0f);
            Player player = Fake<Player>();
            Place(player, Vector3.zero);
            Player.m_localPlayer = player;
        }

        internal void Prime()
        {
            // Seed the visible cache through the real frame scheduler, including its readiness gate.
            AccessTools.Field(typeof(ClutterSystem), "m_forceRebuild").SetValue(Clutter, true);
            Frame();
            Check(Builds.ContainsKey(new Vector2Int(6, 0)), "fixture did not populate the grass radius");
        }

        internal object Build(Vector2Int patch)
        {
            Builds[patch] = Builds.TryGetValue(patch, out int count) ? count + 1 : 1;
            object result = Activator.CreateInstance(patchData, true)!;
            AccessTools.Field(patchData, "center").SetValue(result, Clutter.GetVegPatchCenter(patch));
            return result;
        }

        private Heightmap MakeMap(Vector3 position)
        {
            Heightmap map = Fake<Heightmap>();
            map.m_width = 64;
            map.m_scale = 1f;
            Place(map, position);
            MeshCollider collider = Fake<MeshCollider>();
            colliders.Add(collider, map);
            AccessTools.Field(typeof(Heightmap), "m_collider").SetValue(map, collider);
            AccessTools.Field(typeof(Heightmap), "m_collisionMesh").SetValue(map, Fake<Mesh>());
            finished[map] = true;
            return map;
        }

        private void Place(Component component, Vector3 position)
        {
            Transform transform = Fake<Transform>();
            transforms.Add(component, transform);
            positions.Add(transform, position);
        }

        private T Fake<T>() where T : UnityEngine.Object
        {
            var result = (T)FormatterServices.GetUninitializedObject(typeof(T));
            IntPtr pointer = Marshal.AllocHGlobal(sizeof(long));
            pointers.Add(pointer);
            Marshal.WriteInt64(pointer, pointers.Count);
            AccessTools.Field(typeof(UnityEngine.Object), "m_CachedPtr").SetValue(result, pointer);
            return result;
        }

        private void AddUpstream(MethodInfo target, MethodInfo hook, bool prefix)
        {
            Patches? patches = Harmony.GetPatchInfo(target);
            Check(patches == null || !patches.Prefixes.Concat(patches.Postfixes).Any(p => p.PatchMethod == hook), "fixture upstream hook was already registered");
            upstream.Patch(target, prefix: prefix ? new HarmonyMethod(hook) : null, postfix: prefix ? null : new HarmonyMethod(hook));
            registrations.Add((target, hook));
        }

        private static void Initialize(object instance, string name)
        {
            FieldInfo field = AccessTools.Field(instance.GetType(), name);
            field.SetValue(instance, Activator.CreateInstance(field.FieldType));
        }

        public void Dispose()
        {
            new Harmony(Owner).UnpatchSelf();
            foreach (var registration in registrations) upstream.Unpatch(registration.Target, registration.Hook);
            fixture.UnpatchSelf();
            foreach (Heightmap map in new[] { Map, Neighbor, Distant }) finished.Remove(map);
            Heightmap.GetAllHeightmaps().Clear();
            Heightmap.GetAllHeightmaps().AddRange(previousMaps);
            Player.m_localPlayer = previousPlayer;
            AccessTools.Field(typeof(ClutterSystem), "m_instance").SetValue(null, previousClutter);
            CompatibilityInstaller.Info = info;
            CompatibilityInstaller.Warning = warning;
            CompatibilityInstaller.Error = error;
            foreach (IntPtr pointer in pointers) Marshal.FreeHGlobal(pointer);
            current = null!;
        }

        internal Transform TransformOf(Component component) => transforms[component];
        internal Vector3 PositionOf(Transform transform) => positions[transform];
        internal void Commit(MeshCollider collider) => Committed.Add(colliders[collider]);
    }

    private static void Reject(Assembly vendor)
    {
        try { VpoTerrainGrassPatch.Prepare(vendor); throw new Exception("unsupported vendor bytes were accepted"); }
        catch (NotSupportedException) { }
        Check(!Harmony.HasAnyPatches(Owner), "rejected vendor retained a grass hook");
    }

    private static bool TransformOf(Component __instance, ref Transform __result) { __result = current.TransformOf(__instance); return false; }
    private static bool PositionOf(Transform __instance, ref Vector3 __result) { __result = current.PositionOf(__instance); return false; }
    private static bool CameraOf(ref Camera __result) { __result = current.Camera; return false; }
    private static bool Commit(MeshCollider __instance) { current.Commit(__instance); return false; }
    private static bool True(ref bool __result) { __result = true; return false; }
    private static bool False(ref bool __result) { __result = false; return false; }
    private static bool Skip() => false;
    private static object Build(Vector2Int patch) => current.Build(patch);

    private static IEnumerable<CodeInstruction> VegetationFixture(MethodBase __originalMethod)
    {
        yield return new CodeInstruction(OpCodes.Ldarg_1);
        yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(VpoTerrainGrassTests), nameof(Build)));
        yield return new CodeInstruction(OpCodes.Castclass, ((MethodInfo)__originalMethod).ReturnType);
        yield return new CodeInstruction(OpCodes.Ret);
    }

    private static HarmonyMethod Hook(string name) => new HarmonyMethod(AccessTools.Method(typeof(VpoTerrainGrassTests), name));
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
