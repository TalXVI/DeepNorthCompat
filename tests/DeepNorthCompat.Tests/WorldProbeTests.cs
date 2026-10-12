using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using DeepNorthCompat;
using HarmonyLib;
using UnityEngine;

// Exercises the server's side of `/dnc_report` through its routed message handlers.
// Transport, native objects and the loaded zone are fixtures. Physics queries have no scene to
// answer them, so ground height, base areas and creatures are not covered here.
internal static class WorldProbeTests
{
    private static readonly Type report = typeof(Plugin).Assembly.GetType("DeepNorthCompat.DiagnosticReport", true)!;
    private static readonly Type diagnostics = typeof(Plugin).Assembly.GetType("DeepNorthCompat.ChestDiagnostics", true)!;
    private static readonly List<object[]> replies = new List<object[]>();
    private static readonly Transform origin = Fake<Transform>();
    private static uint id = 90000;

    internal static void Run(Action<string, Action> test)
    {
        var fixture = new Harmony("DeepNorthCompat.Tests.WorldProbe");
        FieldInfo net = AccessTools.Field(typeof(ZNet), "m_instance"), routed = AccessTools.Field(typeof(ZRoutedRpc), "s_instance"),
            objects = AccessTools.Field(typeof(ZDOMan), "s_instance"), access = AccessTools.Field(report, "ServerAccess");
        object previousNet = net.GetValue(null), previousRouted = routed.GetValue(null), previousObjects = objects.GetValue(null),
            previousAccess = access.GetValue(null);
        var maps = (List<Heightmap>)AccessTools.Field(typeof(Heightmap), "s_heightmaps").GetValue(null);
        var compilers = (List<TerrainComp>)AccessTools.Field(typeof(TerrainComp), "s_instances").GetValue(null);
        Heightmap map = Fake<Heightmap>(); TerrainComp edits = Fake<TerrainComp>();
        string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox", "probe-" + Guid.NewGuid().ToString("N"));
        void Allow(int level) => access.SetValue(null, Enum.ToObject(access.FieldType, level));
        try
        {
            Hook(fixture, typeof(ZNet), "IsServer", nameof(True));
            Hook(fixture, typeof(ZRoutedRpc), "InvokeRoutedRPC", nameof(Reply), new[] { typeof(long), typeof(string), typeof(object[]) });
            Hook(fixture, typeof(SystemInfo), "get_operatingSystem", nameof(OperatingSystem));
            Hook(fixture, typeof(Component), "get_transform", nameof(Origin));
            ZNet server = Fake<ZNet>(); AccessTools.Field(typeof(ZNet), "m_peers").SetValue(server, new List<ZNetPeer>());
            net.SetValue(null, server);
            routed.SetValue(null, FormatterServices.GetUninitializedObject(typeof(ZRoutedRpc)));
            object man = FormatterServices.GetUninitializedObject(typeof(ZDOMan)); AccessTools.Field(typeof(ZDOMan), "m_sessionID").SetValue(man, 1L);
            objects.SetValue(null, man);
            Call(diagnostics, "Configure", directory, true, 1L << 20);

            test("World probe: the server answers one report for the position a peer sent", () =>
            {
                Allow(2);
                Send(7, new Vector3(12, 34, -56));
                Check(Ask(7).Contains("## World probe") && Ask(8).Contains("## World probe") == false, "probe belongs to its sender");
                Send(7, new Vector3(12, 34, -56));
                Check(Ask(7).Contains("position: (12, 34, -56); zone (0, -1)"), "position and its 64 m zone");
                Check(!Ask(7).Contains("## World probe"), "a position answers one request");
                Send(7, new Vector3(float.NaN, 0, 0));
                Check(!Ask(7).Contains("## World probe"), "invalid position ignored");
                Send(7, Vector3.zero);
                var peer = (ZNetPeer)FormatterServices.GetUninitializedObject(typeof(ZNetPeer)); peer.m_uid = 7;
                Call(report, "PeerDisconnecting", peer);
                Check(!Ask(7).Contains("## World probe"), "position dropped with its peer");
            });
            test("World probe: a peer the server refuses gets no probe, now or later", () =>
            {
                Allow(0);
                Send(7, Vector3.zero); Ask(7);
                Allow(2);
                Check(!Ask(7).Contains("## World probe"), "refused position answered later");
            });
            test("World probe: reports a stored terrain edit this peer applies only in part", () =>
            {
                Allow(2);
                const int side = 65, centre = 32 * side + 32;
                var heights = Enumerable.Repeat(10f, side * side).ToList();
                var level = new float[side * side]; level[centre] = 60;
                map.m_width = 64; map.m_scale = 1;
                AccessTools.Field(typeof(Heightmap), "m_heights").SetValue(map, heights);
                AccessTools.Field(typeof(Heightmap), "m_buildData").SetValue(map,
                    new HeightmapBuilder.HMBuildData(Vector3.zero, 64, 1, false, null!) { m_baseHeights = Enumerable.Repeat(10f, side * side).ToList() });
                var view = Fake<ZNetView>(); var record = (ZDO)FormatterServices.GetUninitializedObject(typeof(ZDO)); record.m_uid = new ZDOID(1, ++id); record.SetOwnerInternal(123);
                AccessTools.Field(typeof(ZNetView), "m_zdo").SetValue(view, record);
                foreach (var (field, value) in new (string, object)[] { ("m_size", 64f), ("m_width", 64), ("m_operations", 3), ("m_levelDelta", level),
                    ("m_smoothDelta", new float[side * side]), ("m_nview", view) })
                    AccessTools.Field(typeof(TerrainComp), field).SetValue(edits, value);
                maps.Add(map); compilers.Add(edits);

                // A peer limited to 8 m shows the 60 m edit as 8 m.
                heights[centre] = 18;
                Send(7, Vector3.zero); string text = Ask(7);
                Check(text.Contains("generated 10; applied +8; stored +60") && text.Contains("LIMITED: this peer stores +60 and applies +8"), "limited vertex: " + Lines(text, "heightmap"));
                Check(text.Contains("owner 123") && text.Contains("3 operations; 1 edited vertices, stored from +60 to +60; 1 LIMITED"), "zone summary: " + Lines(text, "terrain edits"));

                heights[centre] = 70;
                Send(7, Vector3.zero); text = Ask(7);
                Check(text.Contains("applied +60; stored +60") && text.Contains("applied in full") && text.Contains("; 0 LIMITED"), "full edit: " + Lines(text, "heightmap"));
            });
            test("World probe: names the mods that patch each terrain height method", () =>
            {
                Allow(2);
                Send(7, Vector3.zero);
                Check(Ask(7).Contains("terrain hooks: LevelTerrain none; RaiseTerrain none; ApplyToHeightmap none"), "unpatched terrain methods");
                var limits = new Harmony("fixture.height.limits");
                try
                {
                    limits.Patch(AccessTools.DeclaredMethod(typeof(TerrainComp), "RaiseTerrain"),
                        transpiler: new HarmonyMethod(AccessTools.Method(typeof(WorldProbeTests), nameof(Same))));
                    Send(7, Vector3.zero);
                    Check(Ask(7).Contains("terrain hooks: LevelTerrain none; RaiseTerrain transpiler fixture.height.limits; ApplyToHeightmap none"), "patched method and owner");
                }
                finally { limits.UnpatchSelf(); }
            });
        }
        finally
        {
            maps.Remove(map); compilers.Remove(edits);
            Call(diagnostics, "Disable"); Call(diagnostics, "Clear"); Call(report, "Clear"); fixture.UnpatchSelf();
            net.SetValue(null, previousNet); routed.SetValue(null, previousRouted); objects.SetValue(null, previousObjects); access.SetValue(null, previousAccess);
        }
    }

    private static void Send(long sender, Vector3 point)
    {
        var packet = new ZPackage(); packet.Write(point); packet.Write(ZDOID.None);
        Call(report, "ReceiveProbe", sender, new ZPackage(packet.GetArray()));
    }

    // Requests the server's report as `sender` and returns the text the server sent back.
    private static string Ask(long sender)
    {
        replies.Clear(); Call(report, "ReceiveRequest", sender, 1L);
        var packet = new ZPackage(((ZPackage)replies.Single()[0]).GetArray());
        packet.ReadLong(); packet.ReadBool(); return packet.ReadString();
    }

    private static string Lines(string text, string start) => string.Join(" | ", text.Split('\n').Where(line => line.StartsWith(start, StringComparison.Ordinal)));
    private static object? Call(Type type, string method, params object?[] arguments) => AccessTools.Method(type, method).Invoke(null, arguments);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static T Fake<T>() where T : UnityEngine.Object
    { var value = (T)FormatterServices.GetUninitializedObject(typeof(T)); AccessTools.Field(typeof(UnityEngine.Object), "m_CachedPtr").SetValue(value, new IntPtr(++id)); return value; }
    private static void Hook(Harmony harmony, Type type, string method, string prefix, Type[]? arguments = null)
        => harmony.Patch(AccessTools.Method(type, method, arguments), prefix: new HarmonyMethod(AccessTools.Method(typeof(WorldProbeTests), prefix)));
    private static IEnumerable<CodeInstruction> Same(IEnumerable<CodeInstruction> code) => code;
    private static bool True(ref bool __result) { __result = true; return false; }
    private static bool Reply(object[] __2) { replies.Add(__2); return false; }
    private static bool OperatingSystem(ref string __result) { __result = "fixture"; return false; }
    private static bool Origin(ref Transform __result) { __result = origin; return false; }
}
