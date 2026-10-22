using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using DeepNorthCompat;
using HarmonyLib;
using UnityEngine;

// Exercises report uploads: the server's handler, its storage and retention, client redaction
// and the previous-session report. The connection, sockets and acknowledgements are fixtures;
// nothing here crosses a real transport.
internal static class ReportUploadTests
{
    private static readonly Type upload = typeof(Plugin).Assembly.GetType("DeepNorthCompat.ReportUpload", true)!;
    private static readonly Type report = typeof(Plugin).Assembly.GetType("DeepNorthCompat.DiagnosticReport", true)!;
    private static readonly Type diagnostics = typeof(Plugin).Assembly.GetType("DeepNorthCompat.ChestDiagnostics", true)!;
    private static readonly List<(string Method, ZPackage Package)> acks = new List<(string, ZPackage)>();
    private static uint id = 95000;

    internal static void Run(Action<string, Action> test)
    {
        var fixture = new Harmony("DeepNorthCompat.Tests.ReportUpload");
        FieldInfo net = AccessTools.Field(typeof(ZNet), "m_instance");
        object previousNet = net.GetValue(null);
        string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox", "upload-" + Guid.NewGuid().ToString("N"));
        string submissions = Path.Combine(directory, "submissions");
        var server = Fake<ZNet>(); var peers = new List<ZNetPeer>();
        AccessTools.Field(typeof(ZNet), "m_peers").SetValue(server, peers);
        ZNetPeer peer = Peer(7, "Talent");
        try
        {
            Hook(fixture, typeof(ZNet), "IsServer", nameof(True));
            Hook(fixture, typeof(ZRpc), "Invoke", nameof(Invoked), new[] { typeof(string), typeof(object[]) });
            Hook(fixture, typeof(ZSteamSocket), "GetHostName", nameof(HostName));
            Hook(fixture, typeof(SystemInfo), "get_operatingSystem", nameof(OperatingSystem));
            net.SetValue(null, server); peers.Add(peer);
            Call(diagnostics, "Configure", directory, true, 1L << 20);
            Set("Hooked", true); Set("Accept", true);

            test("Report uploads: the server saves a report under its connection's identity and confirms it", () =>
            {
                Reset();
                ZPackage ack = Send(peer, Packet(41, "marker", "chest showed old items", "Mallory", "# client report\nclient line"))!;
                Check(ack.ReadLong() == 41 && ack.ReadBool(), "saved");
                string reference = Text(ack);
                string file = Directory.GetFiles(submissions, "*.md").Single();
                Check(Path.GetFileName(file).StartsWith(reference.Split('-')[0] + "-Talent-76561198000000001-", StringComparison.Ordinal), "file name: " + file);
                string saved = File.ReadAllText(file);
                Check(saved.Contains("- Reporter: Talent (platform ID 76561198000000001, peer 7)"), "identity from the connection");
                Check(saved.Contains("- Client claims: Mallory (player ID 99). This differs"), "claimed identity shown apart");
                Check(saved.Contains("- Note: chest showed old items") && saved.Contains("client line") && saved.Contains("# Server report at receipt"), "note, client and server reports");
                string[] index = File.ReadAllLines(Path.Combine(submissions, "index.tsv"));
                Check(index.Length == 2 && index[1].Contains(reference) && index[1].Contains("\tTalent\t76561198000000001\t") && index[1].EndsWith("chest showed old items"), "index line");
            });

            test("Report uploads: unsafe or unidentified uploads are refused before anything is saved", () =>
            {
                Reset();
                Check(Compress(new string('x', 600 * 1024)) == null, "over the text limit");
                var (body, length) = Compress("expands past what it declares")!.Value;
                ZPackage ack = Send(peer, Pack(42, "report", "", "Talent", body, length - 5))!;
                Check(ack.ReadLong() == 42 && !ack.ReadBool() && Text(ack).Contains("longer than declared"), "expansion refused");
                var forged = new ZPackage(); forged.Write(1); forged.Write(43L); forged.Write(int.MaxValue);
                ack = Send(peer, forged)!;
                Check(ack.ReadLong() == 43 && !ack.ReadBool() && Text(ack).Contains("field length"), "oversized field refused");
                Check(Send(Peer(8, "Stranger"), Packet(44, "report", "", "Stranger", "text")) == null, "unknown connection gets no answer");
                Check(!Directory.Exists(submissions) || Directory.GetFiles(submissions, "*.md").Length == 0, "nothing saved");
                Send(peer, Packet(45, "report", "", "Talent", "text"));
                ack = Send(peer, Packet(46, "report", "", "Talent", "text"))!;
                Check(ack.ReadLong() == 46 && !ack.ReadBool() && Text(ack).Contains("too many reports"), "rate limited after a burst");
            });

            test("Report uploads: retention keeps each player's newest reports and their index lines", () =>
            {
                Reset();
                Directory.CreateDirectory(submissions);
                string Name(DateTime at, string player) => at.ToString("yyyyMMddTHHmmssfff") + "-" + player + "-1-abcd.md";
                var lines = new List<string> { "received_utc\treference\tkind\tplayer\tplatform_id\tpeer\tfile\tnote" };
                void Add(string name) { File.WriteAllText(Path.Combine(submissions, name), "x"); lines.Add("t\tr\treport\tp\th\t1\t" + name + "\tn"); }
                DateTime now = DateTime.UtcNow;
                for (int i = 0; i < 25; i++) Add(Name(now.AddMinutes(-i), "A").Replace("-1-", "-111-"));
                Add(Name(now, "B").Replace("-1-", "-222-"));
                Add(Name(now.AddDays(-31), "B").Replace("-1-", "-222-"));
                File.WriteAllLines(Path.Combine(submissions, "index.tsv"), lines);
                File.WriteAllText(Path.Combine(submissions, "crashed.md.tmp"), "x");
                Call(upload, "Prune", submissions);
                string[] kept = Directory.GetFiles(submissions, "*.md").Select(Path.GetFileName).ToArray()!;
                Check(kept.Count(name => name.Contains("-111-")) == 20, "per-player cap");
                Check(kept.Count(name => name.Contains("-222-")) == 1, "age limit");
                Check(!File.Exists(Path.Combine(submissions, "crashed.md.tmp")), "temporary file removed");
                string[] index = File.ReadAllLines(Path.Combine(submissions, "index.tsv"));
                Check(index.Length == 22 && index.Skip(1).All(line => kept.Contains(line.Split('\t')[6])), "index matches the kept files");
            });

            test("Report uploads: redaction removes user folders and addresses and keeps versions and times", () =>
            {
                string text = (string)Call(upload, "Redact", "at C:\\Users\\alice\\AppData\\Roaming\\mod.dll; /home/bob/.config/x; connect 203.0.113.5:2456, lan 192.168.1.20. Then 10.0.0.7; build 1.0.20.3.1; "
                    + "server 5.9.12.4:2456; host 5.9.12.4; server 5.9.12.5; plugin Network 1.1.0.0; mscorlib, Version=4.0.0.0; version=1.3.16.2; 12:34:56.789; 00:42:27: line; "
                    + "fe80::1ff:fe23:4567:890a; fdab::beef; loopback ::1; ::ffff:198.51.100.7; Bed::Interact")!;
                Check(!text.Contains("alice") && !text.Contains("bob"), "user names: " + text);
                Check(!text.Contains("203.0.113.5") && !text.Contains("192.168.1.20") && !text.Contains("5.9.12.4") && !text.Contains("5.9.12.5") && !text.Contains("10.0.0.7") && !text.Contains("fe80")
                    && !text.Contains("fdab") && !text.Contains("::1") && !text.Contains("198.51"), "addresses: " + text);
                Check(text.Contains("Network 1.1.0.0") && text.Contains("build 1.0.20.3.1") && text.Contains("Version=4.0.0.0") && text.Contains("version=1.3.16.2")
                    && text.Contains("12:34:56.789") && text.Contains("00:42:27: line") && text.Contains("Bed::Interact"), "versions, times and names kept: " + text);
            });

            test("Report uploads: the previous session's report survives a restart and records an unclean exit", () =>
            {
                Call(diagnostics, "Configure", directory, false, 1L << 20);
                Call(report, "SaveLatest", false);
                Call(report, "PreservePrevious");
                string reports = Path.Combine(directory, "reports");
                Check(!File.Exists(Path.Combine(reports, "DeepNorthCompat-report-client-latest.md")), "latest moved aside");
                var previous = Previous();
                Check(previous != null && !previous.Value.Clean && !previous.Value.Sent, "unclean and unsent");
                Call(report, "MarkPreviousSent", previous!.Value.Path);
                Check(Previous()?.Sent == true, "marked sent");
                Call(report, "WorldShutdown");
                Call(report, "PreservePrevious");
                Check(Previous()?.Clean == true, "a shutdown is clean");
                Call(diagnostics, "Configure", directory, true, 1L << 20);
            });
        }
        finally
        {
            Call(diagnostics, "Disable"); Call(diagnostics, "Clear"); Call(report, "Clear"); fixture.UnpatchSelf();
            Set("Hooked", false); net.SetValue(null, previousNet);
        }
    }

    private static void Reset()
    {
        Call(upload, "Clear");
        string? submissions = (string?)Call(upload, "Directory");
        if (submissions != null && Directory.Exists(submissions)) Directory.Delete(submissions, true);
    }

    private static ZPackage Packet(long request, string kind, string note, string name, string text)
    {
        var (body, length) = Compress(text)!.Value;
        return Pack(request, kind, note, name, body, length);
    }

    private static ZPackage Pack(long request, string kind, string note, string name, byte[] body, int length)
        => (ZPackage)Call(upload, "Pack", request, kind, note, name, 99L, DateTime.UtcNow, null, ZDOID.None, body, length)!;

    private static (byte[] Body, int Length)? Compress(string text) => ((byte[] Body, int Length)?)Call(upload, "Compress", text);

    // Delivers a package on `from`'s connection and returns the acknowledgement the server sent back.
    private static ZPackage? Send(ZNetPeer from, ZPackage package)
    {
        acks.Clear();
        Call(upload, "Receive", from.m_rpc, new ZPackage(package.GetArray()));
        if (acks.Count == 0) return null;
        Check(acks.Single().Method == "DeepNorthCompat.ReportAck.v1", "acknowledgement name");
        var ack = new ZPackage(acks[0].Package.GetArray());
        Check(ack.ReadInt() == 1, "acknowledgement format");
        return ack;
    }

    private static (string Path, string Text, long World, bool Clean, bool Sent)? Previous()
        => ((string, string, long, bool, bool)?)Call(report, "Previous");

    private static string Text(ZPackage package) => Encoding.UTF8.GetString(package.ReadByteArray());

    private static ZNetPeer Peer(long uid, string name)
    {
        var peer = (ZNetPeer)FormatterServices.GetUninitializedObject(typeof(ZNetPeer));
        peer.m_uid = uid; peer.m_playerName = name;
        peer.m_rpc = (ZRpc)FormatterServices.GetUninitializedObject(typeof(ZRpc));
        peer.m_socket = (ZSteamSocket)FormatterServices.GetUninitializedObject(typeof(ZSteamSocket));
        return peer;
    }

    private static void Set(string field, bool value) => AccessTools.Field(upload, field).SetValue(null, value);
    private static object? Call(Type type, string method, params object?[] arguments) => AccessTools.Method(type, method).Invoke(null, arguments);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static T Fake<T>() where T : UnityEngine.Object
    { var value = (T)FormatterServices.GetUninitializedObject(typeof(T)); AccessTools.Field(typeof(UnityEngine.Object), "m_CachedPtr").SetValue(value, new IntPtr(++id)); return value; }
    private static void Hook(Harmony harmony, Type type, string method, string prefix, Type[]? arguments = null)
        => harmony.Patch(AccessTools.Method(type, method, arguments), prefix: new HarmonyMethod(AccessTools.Method(typeof(ReportUploadTests), prefix)));
    private static bool True(ref bool __result) { __result = true; return false; }
    private static bool Invoked(string __0, object[] __1) { acks.Add((__0, (ZPackage)__1[0])); return false; }
    private static bool HostName(ref string __result) { __result = "76561198000000001"; return false; }
    private static bool OperatingSystem(ref string __result) { __result = "fixture"; return false; }
}
