using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using UnityEngine;

namespace DeepNorthCompat
{
    // Builds a single Markdown report that a player can share: problems with
    // the events that led to them, errors from every mod, markers, peers and environment.
    // `/dnc_report` in chat writes the client report, fetches the server's, saves both and
    // copies them to the clipboard. `/dnc_mark <note>` timestamps a problem on both sides.
    internal static class DiagnosticReport
    {
        internal enum Access { Nobody, Admins, Everyone }
        private sealed class Error
        {
            internal string Source = "", Level = "", Text = "";
            internal int Count;
            internal DateTime First, Last;
            internal string[] Context = Array.Empty<string>();
        }
        private sealed class Budget
        {
            internal int Findings, Context, Errors, ErrorText, Recent;
        }

        private const string HelloRpc = "DeepNorthCompat.Hello.v1", MarkerRpc = "DeepNorthCompat.Marker.v1",
            RequestRpc = "DeepNorthCompat.ReportRequest.v1", ReplyRpc = "DeepNorthCompat.ReportReply.v1";
        private const int ReplyLimit = 96 * 1024, ReportsRetained = 30, ErrorLimit = 40, StartupLimit = 300;
        private static readonly Budget Full = new Budget { Findings = 60, Context = 12, Errors = 30, ErrorText = 2500, Recent = 60 };
        private static readonly Budget Compact = new Budget { Findings = 20, Context = 6, Errors = 12, ErrorText = 1200, Recent = 20 };
        private static readonly string[] Relevant =
        {
            Plugin.Guid, ChestSyncPatch.MucGuid, ChestSyncPatch.QuickGuid, "Azumatt.AzuCraftyBoxes", "Azumatt.AzuAntiArthriticCrafting",
            "MidnightsFX.ImpactfulSkills", SimulationPatch.ForkGuid, "dev.ontrigger.vpo", "MidnightsFX.ValheimCommunityPatch", "akoozie.valheimtune",
        };
        private static readonly object gate = new object();
        private static readonly List<string> startup = new List<string>();
        private static readonly Dictionary<string, Error> errors = new Dictionary<string, Error>(StringComparer.Ordinal);
        private static readonly Dictionary<long, string> hellos = new Dictionary<long, string>();
        private static readonly Dictionary<string, string> hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static int droppedErrors, writtenVersion = -1;
        private static string serverHello = "";
        private static long request;
        private static string? serverReport;
        private static string serverStatus = "";
        private static float nextAutoWrite;
        internal static Access ServerAccess = Access.Admins;

        private sealed class Listener : ILogListener
        {
            public void LogEvent(object sender, LogEventArgs eventArgs) => Capture(eventArgs.Source?.SourceName ?? "", eventArgs.Level, eventArgs.Data?.ToString() ?? "");
            public void Dispose() { }
        }

        internal static void Listen() => BepInEx.Logging.Logger.Listeners.Add(new Listener());

        // Keeps this plugin's own log lines and every error or exception from any source.
        internal static void Capture(string source, LogLevel level, string text)
        {
            if (ChestDiagnostics.Recording) return;
            bool own = source == "DeepNorthCompat";
            bool error = (level & (LogLevel.Error | LogLevel.Fatal)) != 0 || text.IndexOf("Exception", StringComparison.Ordinal) >= 0
                || text.IndexOf("Failed to find rpc method", StringComparison.Ordinal) >= 0;
            if (!own && !error) return;
            DateTime now = DateTime.UtcNow;
            // Read the event context before taking this lock. ChestDiagnostics can log while
            // holding its own lock, so the two locks must never nest in this order.
            string[] context = error ? ChestDiagnostics.Snapshot(5).Recent : Array.Empty<string>();
            lock (gate)
            {
                if (own)
                {
                    startup.Add(now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " " + level + " " + Clip(text, 700));
                    if (startup.Count > StartupLimit) startup.RemoveAt(StartupLimit / 3);
                }
                if (!error) return;
                string[] lines = text.Split('\n');
                string key = source + "|" + lines[0].Trim() + "|" + (lines.Skip(1).FirstOrDefault(line => line.Trim().Length > 0)?.Trim() ?? "");
                if (errors.TryGetValue(key, out Error existing)) { existing.Count++; existing.Last = now; return; }
                if (errors.Count >= ErrorLimit) { droppedErrors++; return; }
                errors[key] = new Error { Source = source, Level = level.ToString(), Text = text.TrimEnd(), Count = 1, First = now, Last = now,
                    Context = context };
            }
            ChestDiagnostics.RecordText("log-error", source + " " + level + ": " + Clip(text.Split('\n')[0], 300), Severity.Info);
        }

        internal static void RegisterCommands()
        {
            new Terminal.ConsoleCommand("dnc_report", "DeepNorthCompat: save a diagnostic report with the server's and copy it to the clipboard.",
                (Terminal.ConsoleEvent)(args => Collect(args.Context)));
            new Terminal.ConsoleCommand("dnc_mark", "DeepNorthCompat: mark a problem now, for example: dnc_mark chest showed old items",
                (Terminal.ConsoleEvent)(args => MarkCommand(args.Context, string.Join(" ", args.Args.Skip(1)))));
        }

        // Game.Start runs once per world session, after ZRoutedRpc exists, on clients and servers.
        internal static void GameStarted()
        {
            ZRoutedRpc.instance.Register<ZPackage>(HelloRpc, ReceiveHello);
            ZRoutedRpc.instance.Register<ZPackage>(MarkerRpc, ReceiveMarker);
            ZRoutedRpc.instance.Register<long>(RequestRpc, ReceiveRequest);
            ZRoutedRpc.instance.Register<ZPackage>(ReplyRpc, ReceiveReply);
            lock (gate) { hellos.Clear(); serverHello = ""; }
            ChestDiagnostics.Record("world-start", null, Environment(compact: true));
        }

        internal static void LocalPlayerSpawned(Player player)
        {
            if (ZNet.instance == null || ZNet.instance.IsServer() || ZRoutedRpc.instance == null) return;
            var packet = new ZPackage();
            packet.Write(PluginVersion); packet.Write(player.GetPlayerName()); packet.Write(player.GetPlayerID()); packet.Write(PatchSummary());
            ZRoutedRpc.instance.InvokeRoutedRPC(HelloRpc, packet);
        }

        private static void ReceiveHello(long sender, ZPackage packet)
        {
            string version = packet.ReadString(), name = packet.ReadString(); long playerId = packet.ReadLong(); string patches = packet.ReadString();
            if (ZNet.instance.IsServer())
            {
                string summary = "DeepNorthCompat " + version + "; player " + name + " (" + playerId + "); " + patches;
                lock (gate) hellos[sender] = summary;
                bool match = version == PluginVersion;
                ChestDiagnostics.Record("peer-hello", null, "peer=" + sender + ";" + summary, match ? Severity.Info : Severity.Problem,
                    match ? "" : "version-mismatch: server runs DeepNorthCompat " + PluginVersion);
                var reply = new ZPackage();
                reply.Write(PluginVersion); reply.Write("server"); reply.Write(0L); reply.Write(PatchSummary());
                ZRoutedRpc.instance.InvokeRoutedRPC(sender, HelloRpc, reply);
            }
            else
            {
                lock (gate) serverHello = "DeepNorthCompat " + version + "; " + patches;
                bool match = version == PluginVersion;
                ChestDiagnostics.Record("server-hello", null, "server=" + sender + ";version=" + version + ";" + patches,
                    match ? Severity.Info : Severity.Problem, match ? "" : "version-mismatch: this client runs DeepNorthCompat " + PluginVersion);
            }
        }

        internal static void PeerDisconnecting(ZNetPeer peer)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            ChestDiagnostics.Record("peer-disconnected", null, "peer=" + peer.m_uid + ";player=" + peer.m_playerName);
            lock (gate) hellos.Remove(peer.m_uid);
        }

        internal static void WorldShutdown()
        {
            ChestDiagnostics.Record("world-shutdown", null);
            Save(Build(Full, includeServer: false), "latest");
        }

        // Rewrites the latest report every few minutes when something changed, so a crash
        // still leaves a recent report behind.
        internal static void Tick()
        {
            if (!ChestDiagnostics.Enabled) return;
            if (nextAutoWrite == 0) nextAutoWrite = Time.realtimeSinceStartup + 300f;
            if (Time.realtimeSinceStartup < nextAutoWrite) return;
            nextAutoWrite = Time.realtimeSinceStartup + 300f;
            if (ChestDiagnostics.Version == writtenVersion) return;
            writtenVersion = ChestDiagnostics.Version;
            Save(Build(Full, includeServer: false), "latest");
        }

        private static void MarkCommand(Terminal context, string note)
        {
            if (!ChestDiagnostics.Enabled) { context.AddString("DeepNorthCompat diagnostics are disabled in its config."); return; }
            if (note.Trim().Length == 0) note = "(no note)";
            string who = Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerName() : "local";
            ChestDiagnostics.Mark(who, note);
            if (ZNet.instance != null && !ZNet.instance.IsServer() && ZRoutedRpc.instance != null)
            {
                var packet = new ZPackage(); packet.Write(who); packet.Write(note);
                ZRoutedRpc.instance.InvokeRoutedRPC(MarkerRpc, packet);
            }
            context.AddString("DeepNorthCompat: marked \"" + note + "\". Reproduce if you can, then type /dnc_report.");
        }

        private static void ReceiveMarker(long sender, ZPackage packet)
        {
            string who = packet.ReadString(), note = packet.ReadString();
            ChestDiagnostics.Mark(who + " (peer " + sender + ")", note);
        }

        private static void Collect(Terminal context)
        {
            if (!ChestDiagnostics.Enabled) { context.AddString("DeepNorthCompat diagnostics are disabled in its config."); return; }
            if (ZNet.instance != null && !ZNet.instance.IsServer() && ZRoutedRpc.instance != null && ZNet.instance.GetServerPeer() != null)
            {
                lock (gate) { request = DateTime.UtcNow.Ticks; serverReport = null; serverStatus = "no reply within 10 seconds"; }
                ZRoutedRpc.instance.InvokeRoutedRPC(RequestRpc, request);
                context.AddString("DeepNorthCompat: collecting the server's report…");
                Plugin.Instance.StartCoroutine(Finish(context, request));
            }
            else
            {
                lock (gate) { serverReport = null; serverStatus = ZNet.instance == null ? "not in a world" : "this peer is the server"; }
                Deliver(context);
            }
        }

        private static IEnumerator Finish(Terminal context, long id)
        {
            float until = Time.realtimeSinceStartup + 10f;
            while (Time.realtimeSinceStartup < until)
            {
                lock (gate) if (request != id || serverReport != null) break;
                yield return null;
            }
            Deliver(context);
        }

        private static void Deliver(Terminal context)
        {
            string text = Build(Full, includeServer: true);
            string? path = Save(text, "combined");
            GUIUtility.systemCopyBuffer = text;
            context.AddString("DeepNorthCompat: report copied to the clipboard (" + (text.Length / 1024 + 1) + " KB)."
                + (path == null ? "" : " Saved to " + path));
        }

        private static void ReceiveRequest(long sender, long id)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            ZNetPeer? peer = ZNet.instance.GetPeer(sender);
            bool allowed = ServerAccess == Access.Everyone
                || ServerAccess == Access.Admins && peer != null && ZNet.instance.IsAdmin(peer.m_socket.GetHostName());
            string text = Build(Full, includeServer: false);
            Save(text, "requested");
            if (Encoding.UTF8.GetByteCount(text) > ReplyLimit) text = Build(Compact, includeServer: false);
            if (Encoding.UTF8.GetByteCount(text) > ReplyLimit) text = text.Substring(0, ReplyLimit / 2) + "\n\n(truncated to fit the network limit)\n";
            ChestDiagnostics.Record("report-request", null, "peer=" + sender + ";player=" + peer?.m_playerName + ";allowed=" + allowed + ";access=" + ServerAccess);
            var reply = new ZPackage();
            reply.Write(id); reply.Write(allowed);
            reply.Write(allowed ? text : "The server saved its report in " + ReportDirectory + ". It sends reports only to "
                + (ServerAccess == Access.Admins ? "admins (adminlist.txt)" : "nobody") + ". Change `Server report access` in the server's "
                + "BepInEx/config/DeepNorthCompat.cfg to allow this.");
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, ReplyRpc, reply);
        }

        private static void ReceiveReply(long sender, ZPackage packet)
        {
            long id = packet.ReadLong(); bool allowed = packet.ReadBool(); string text = packet.ReadString();
            lock (gate)
            {
                if (id != request) return;
                if (allowed) { serverReport = text; serverStatus = "received"; }
                else { serverReport = ""; serverStatus = "refused: " + text; }
            }
        }

        private static string PluginVersion => typeof(Plugin).Assembly.GetName().Version.ToString(3);
        private static string ReportDirectory => Path.Combine(ChestDiagnostics.Directory ?? ".", "reports");

        private static string PatchSummary()
        {
            lock (gate)
                return string.Join(", ", startup.Select(line => line.Substring(Math.Min(line.Length, 9)))
                    .Where(line => line.Contains(": APPLIED") || line.Contains("NOT APPLIED"))
                    .Select(line => line.Contains("NOT APPLIED") ? "FAILED " + line.Split(':')[0].Split(' ').Last() : line.Split(':')[0].Split(' ').Last()));
        }

        internal static string? Save(string text, string kind)
        {
            if (ChestDiagnostics.Directory == null) return null;
            try
            {
                Directory.CreateDirectory(ReportDirectory);
                string role = ChestDiagnostics.Role;
                string name = kind == "latest" ? "DeepNorthCompat-report-" + role + "-latest.md"
                    : "DeepNorthCompat-report-" + role + "-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmss", CultureInfo.InvariantCulture) + "-" + kind + ".md";
                string path = Path.Combine(ReportDirectory, name);
                File.WriteAllText(path, text, new UTF8Encoding(false));
                foreach (FileInfo old in new DirectoryInfo(ReportDirectory).GetFiles("DeepNorthCompat-report-*.md")
                    .Where(file => !file.Name.EndsWith("-latest.md", StringComparison.Ordinal))
                    .OrderByDescending(file => file.LastWriteTimeUtc).Skip(ReportsRetained))
                    old.Delete();
                return path;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                CompatibilityInstaller.Error("Diagnostics could not save a report: " + error);
                return null;
            }
        }

        internal static string Build(bool includeServer) => Build(Full, includeServer);

        private static string Build(Budget budget, bool includeServer)
        {
            var snapshot = ChestDiagnostics.Snapshot(budget.Recent);
            var b = new StringBuilder();
            string role = ChestDiagnostics.Role;
            DateTime now = DateTime.UtcNow;
            b.Append("# DeepNorthCompat diagnostic report (").Append(role).AppendLine(")").AppendLine();
            b.AppendLine("Times are UTC. Chest IDs match across peers; "
                + "owner and peer values are network session IDs. Detailed event logs: `" + (ChestDiagnostics.Directory ?? "disabled") + "`.").AppendLine();
            b.AppendLine("Event lines read `time [severity] event [operation] chest=<id> owner=self|none|<peer> rev=<data revision>/<loaded revision> "
                + "use=<local>/<replicated> payload=<item data hash> items=<name:quality:world level=count> | details`. A chest shows current "
                + "contents when both revisions match. Finding blocks list that chest's earlier events, then the finding after `>`.").AppendLine();

            b.AppendLine("## Overview").AppendLine();
            b.AppendLine("- Generated " + now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "; session " + ChestDiagnostics.Session
                + " started " + ChestDiagnostics.Started.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                + " (" + (int)(now - ChestDiagnostics.Started).TotalMinutes + " min ago).");
            b.AppendLine("- " + Environment(compact: false));
            lock (gate)
            {
                if (role == "client") b.AppendLine("- Server: " + (serverHello.Length == 0 ? "no DeepNorthCompat reply this session. The server may lack DeepNorthCompat 1.1.3 or later." : serverHello));
            }
            b.AppendLine("- Findings: " + ChestDiagnostics.Problems + " problems, " + ChestDiagnostics.Warnings + " warnings"
                + (snapshot.Dropped > 0 ? " (" + snapshot.Dropped + " middle findings omitted)" : "") + ". Errors: " + ErrorSummary() + ". Markers: " + snapshot.Markers.Length + ".");
            if (snapshot.Failures.Length > 0) b.AppendLine("- Diagnostics faults: " + string.Join("; ", snapshot.Failures));
            b.AppendLine();

            if (snapshot.Markers.Length > 0)
            {
                b.AppendLine("## Player markers").AppendLine();
                foreach (var marker in snapshot.Markers)
                {
                    b.AppendLine("### " + marker.Utc.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " " + marker.Who + ": " + marker.Note).AppendLine();
                    Block(b, marker.Context.Skip(Math.Max(0, marker.Context.Length - budget.Recent)));
                }
            }

            b.AppendLine("## Problems and warnings").AppendLine();
            if (snapshot.Findings.Length == 0) b.AppendLine("None recorded.").AppendLine();
            var shown = snapshot.Findings.Skip(Math.Max(0, snapshot.Findings.Length - budget.Findings)).ToArray();
            if (shown.Length < snapshot.Findings.Length) b.AppendLine("Showing the newest " + shown.Length + " of " + snapshot.Findings.Length + ".").AppendLine();
            foreach (var finding in shown)
            {
                b.AppendLine("### " + finding.Severity.ToString().ToUpperInvariant() + " " + finding.Kind + " at " + finding.Utc.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)).AppendLine();
                Block(b, finding.Context.Skip(Math.Max(0, finding.Context.Length - budget.Context)).Concat(new[] { "> " + finding.Line })
                    .Concat(finding.Extra.Length == 0 ? Array.Empty<string>() : new[] { Clip(finding.Extra, budget.ErrorText) }));
            }

            b.AppendLine("## Errors and exceptions from all mods").AppendLine();
            Error[] errorList;
            lock (gate) errorList = errors.Values.OrderBy(error => error.First).ToArray();
            if (errorList.Length == 0) b.AppendLine("None recorded.").AppendLine();
            foreach (Error error in errorList.Skip(Math.Max(0, errorList.Length - budget.Errors)))
            {
                b.AppendLine("### " + error.Level + " from " + (error.Source.Length == 0 ? "unknown" : error.Source) + ", " + error.Count + "x, first "
                    + error.First.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + ", last " + error.Last.ToString("HH:mm:ss", CultureInfo.InvariantCulture)).AppendLine();
                Block(b, new[] { Clip(error.Text, budget.ErrorText) });
                if (error.Context.Length > 0) { b.AppendLine("Events before the first occurrence:").AppendLine(); Block(b, error.Context); }
            }

            if (role == "server" && ZNet.instance != null)
            {
                b.AppendLine("## Connected peers").AppendLine();
                lock (gate)
                    foreach (ZNetPeer peer in ZNet.instance.GetPeers())
                        b.AppendLine("- " + peer.m_uid + " " + peer.m_playerName + ": "
                            + (hellos.TryGetValue(peer.m_uid, out string hello) ? hello : "no DeepNorthCompat hello. This peer lacks DeepNorthCompat 1.1.3 or later."));
                b.AppendLine();
            }

            b.AppendLine("## Recent events").AppendLine();
            Block(b, snapshot.Recent);
            b.AppendLine("## Event counts").AppendLine();
            b.AppendLine(string.Join(", ", snapshot.Counts.Select(entry => entry.Key + " " + entry.Value))).AppendLine();
            b.AppendLine("## DeepNorthCompat log").AppendLine();
            lock (gate) Block(b, startup.ToArray());
            b.AppendLine("## Settings and mods").AppendLine();
            b.AppendLine("- " + Settings());
            b.AppendLine("- Relevant mods: " + RelevantMods());
            b.AppendLine("- All plugins: " + string.Join(", ", Chainloader.PluginInfos.Values.OrderBy(info => info.Metadata.GUID, StringComparer.OrdinalIgnoreCase)
                .Select(info => info.Metadata.GUID + " " + info.Metadata.Version)));
            b.AppendLine();

            if (includeServer)
            {
                string status; string? server;
                lock (gate) { status = serverStatus; server = serverReport; }
                b.AppendLine("---").AppendLine();
                if (!string.IsNullOrEmpty(server)) b.Append(server);
                else b.AppendLine("# Server report unavailable").AppendLine().AppendLine("Status: " + status + ". The server writes "
                    + "`BepInEx/DeepNorthCompat/diagnostics/reports/DeepNorthCompat-report-server-latest.md` every five minutes and on shutdown; attach that file instead.");
            }
            return b.ToString();
        }

        private static string ErrorSummary()
        {
            lock (gate) return errors.Count + " unique, " + errors.Values.Sum(error => error.Count) + " total" + (droppedErrors > 0 ? ", " + droppedErrors + " more not kept" : "");
        }

        private static string Environment(bool compact)
        {
            string text = "DeepNorthCompat " + PluginVersion + " " + ChestDiagnostics.Role + "; Valheim " + global::Version.GetVersionString()
                + "; peer " + (ZDOMan.instance == null ? "none" : ZDOMan.GetSessionID().ToString(CultureInfo.InvariantCulture));
            if (Player.m_localPlayer != null) text += "; player " + Player.m_localPlayer.GetPlayerName() + " (" + Player.m_localPlayer.GetPlayerID() + ")";
            if (compact) return text;
            return text + "; game assembly " + Hash(typeof(ZNet).Assembly.Location) + "; " + SystemInfo.operatingSystem;
        }

        private static string Settings()
        {
            return "Crafty range " + ChestCraftPatch.Range.ToString(CultureInfo.InvariantCulture) + " m, leave one " + ChestCraftPatch.LeavingOne
                + "; crafting handoff " + (ChestCraftPatch.Active ? "active" : "inactive") + "; Quick Stack range " + ChestSyncPatch.StackRange.ToString(CultureInfo.InvariantCulture)
                + " m, restock range " + ChestSyncPatch.RestockRange.ToString(CultureInfo.InvariantCulture) + " m; server report access " + ServerAccess
                + "; tracked chests " + ChestRegistry.Count;
        }

        private static string RelevantMods() => string.Join(", ", Relevant.Select(guid => Chainloader.PluginInfos.TryGetValue(guid, out var info)
            ? guid + " " + info.Metadata.Version + " sha256:" + Hash(info.Location) : guid + " absent"));

        private static string Hash(string path)
        {
            lock (gate)
            {
                if (hashes.TryGetValue(path, out string cached)) return cached;
                string hash;
                try
                {
                    using (SHA256 sha = SHA256.Create())
                    using (FileStream file = File.OpenRead(path))
                        hash = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").Substring(0, 16);
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException) { hash = "unreadable"; }
                return hashes[path] = hash;
            }
        }

        private static void Block(StringBuilder b, IEnumerable<string> lines)
        {
            b.AppendLine("```text");
            foreach (string line in lines) b.AppendLine(line.Replace("```", "'''"));
            b.AppendLine("```").AppendLine();
        }

        private static string Clip(string value, int length) => value.Length <= length ? value : value.Substring(0, length) + "…";

        // Clears captured errors and peer state. Used by the offline tests between cases.
        internal static void Clear()
        {
            lock (gate) { errors.Clear(); droppedErrors = 0; hellos.Clear(); serverHello = ""; serverReport = null; serverStatus = ""; }
        }
    }
}
