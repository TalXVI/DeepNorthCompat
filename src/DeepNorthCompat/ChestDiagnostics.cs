using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace DeepNorthCompat
{
    internal enum Severity { Info, Warning, Problem }

    // Records chest, crafting and network events. Each process session writes a detailed
    // tab-separated log and keeps a bounded in-memory summary for DiagnosticReport.
    // Recording never throws into the game hooks that call it.
    internal static class ChestDiagnostics
    {
        internal sealed class Finding
        {
            internal DateTime Utc;
            internal Severity Severity;
            internal string Kind = "", Line = "", Extra = "";
            internal string[] Context = Array.Empty<string>();
        }
        internal sealed class Marker
        {
            internal DateTime Utc;
            internal string Who = "", Note = "";
            internal string[] Context = Array.Empty<string>();
        }

        private const string Header = "utc\tsession\trole\tseverity\tevent\toperation\tchest\tpeer\towner\tdata_revision\tloaded_revision\tlocal_use\treplicated_use\tstacks\tfree_slots\titems\tpayload\tdetails";
        private const int RecentLimit = 600, ChestHistory = 12, FindingLimit = 80, KeptEarlyFindings = 10, MarkerLimit = 30, SessionsRetained = 20, PartsPerSession = 16;
        internal static readonly string Session = Guid.NewGuid().ToString("N").Substring(0, 12);
        internal static readonly DateTime Started = DateTime.UtcNow;
        private static readonly object gate = new object();
        private static readonly Queue<(DateTime Utc, string Line)> recent = new Queue<(DateTime, string)>();
        private static readonly Dictionary<ZDOID, Queue<string>> histories = new Dictionary<ZDOID, Queue<string>>();
        private static readonly List<Finding> findings = new List<Finding>();
        private static readonly List<Marker> markers = new List<Marker>();
        private static readonly Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly HashSet<string> reportedFailures = new HashSet<string>(StringComparer.Ordinal);
        private static string? directory, writeError;
        private static string role = "client";
        private static StreamWriter? writer;
        private static long bytes, limit;
        private static int part, droppedFindings, problems, warnings;
        private static bool enabled, writeFailed;
        [ThreadStatic] private static bool recording;
        internal static string Operation = "";
        internal static bool Enabled => enabled;
        internal static bool Recording => recording;
        internal static string? Directory => directory;
        internal static string Role => role;
        internal static int Problems { get { lock (gate) return problems; } }
        internal static int Warnings { get { lock (gate) return warnings; } }
        internal static int Version { get; private set; }

        internal static void Configure(string path, bool server, long maxBytes)
        {
            Disable(); role = server ? "server" : "client";
            try
            {
                if (!Path.IsPathRooted(path)) throw new ArgumentException("Use an absolute diagnostics directory.", nameof(path));
                directory = Path.GetFullPath(path);
            }
            catch (Exception error) when (error is ArgumentException || error is NotSupportedException || error is System.Security.SecurityException)
            {
                CompatibilityInstaller.Error("Diagnostics disabled because the configured directory is invalid: " + error);
                return;
            }
            limit = maxBytes; part = 0; writeFailed = false; enabled = true;
            Record("session-start", null, "version=" + typeof(Plugin).Assembly.GetName().Version + ";role=" + role);
            CompatibilityInstaller.Info("Diagnostics: logs and reports in " + directory + ". Type /dnc_report in chat to collect a report.");
        }

        internal static void Record(string kind, Container? chest, string details = "", Severity severity = Severity.Info, string extra = "")
        {
            if (!enabled || recording) return;
            string? failure = null, notice = null;
            recording = true;
            try
            {
                ZDO? zdo = ChestRegistry.LiveZdo(chest);
                ZDOID? id = chest is object ? ChestRegistry.IdOf(chest) : null;
                Inventory? inventory = chest is object ? chest.GetInventory() : null;
                long peer = ZDOMan.instance == null ? 0 : ZDOMan.GetSessionID();
                long owner = zdo?.GetOwner() ?? 0;
                string items = inventory == null ? "" : Items(inventory);
                string payload = zdo == null ? "" : Short(ChestRegistry.PayloadHash(zdo));
                // Container uses uint.MaxValue until its first load.
                string loaded = chest is not object ? "" : ChestRegistry.LoadedRevision(chest) is uint revision && revision != uint.MaxValue
                    ? revision.ToString(CultureInfo.InvariantCulture) : "none";
                string localUse = chest is object ? (chest.IsInUse() ? "1" : "0") : "";
                string replicatedUse = zdo == null ? "" : zdo.GetInt(ZDOVars.s_inUse).ToString(CultureInfo.InvariantCulture);
                DateTime now = DateTime.UtcNow;
                string[] fields =
                {
                    now.ToString("O", CultureInfo.InvariantCulture), Session, role, severity.ToString(), kind, Operation,
                    id?.ToString() ?? "", peer.ToString(CultureInfo.InvariantCulture), zdo == null ? "" : owner.ToString(CultureInfo.InvariantCulture),
                    zdo?.DataRevision.ToString(CultureInfo.InvariantCulture) ?? "", loaded, localUse, replicatedUse,
                    inventory?.NrOfItems().ToString(CultureInfo.InvariantCulture) ?? "",
                    inventory == null ? "" : Math.Max(0, inventory.GetWidth() * inventory.GetHeight() - inventory.NrOfItems()).ToString(CultureInfo.InvariantCulture),
                    items, payload, details + (extra.Length == 0 ? "" : " | " + extra)
                };
                var line = new StringBuilder();
                line.Append(now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(' ');
                if (severity != Severity.Info) line.Append(severity == Severity.Problem ? "PROBLEM " : "WARNING ");
                line.Append(kind);
                if (Operation.Length > 0) line.Append(" [").Append(Operation).Append(']');
                if (id != null) line.Append(" chest=").Append(id.Value);
                if (zdo != null)
                    line.Append(" owner=").Append(owner == 0 ? "none" : owner == peer ? "self" : owner.ToString(CultureInfo.InvariantCulture))
                        .Append(" rev=").Append(zdo.DataRevision).Append('/').Append(loaded)
                        .Append(" use=").Append(localUse).Append('/').Append(replicatedUse).Append(" payload=").Append(payload);
                else if (id != null) line.Append(" (unloaded)");
                if (inventory != null) line.Append(" items=").Append(Clip(items.Length == 0 ? "empty" : items, 200));
                if (details.Length > 0) line.Append(" | ").Append(Clip(details, 400));
                lock (gate) { Store(kind, severity, id, line.ToString(), extra, fields); notice = writeError; writeError = null; }
            }
            catch (Exception error)
            {
                failure = kind + ": " + error;
            }
            finally { recording = false; }
            if (notice != null) CompatibilityInstaller.Error(notice);
            if (failure != null) ReportFailure(kind, failure);
        }

        // Records an event that has no chest and must not touch Unity objects, such as log
        // messages that can arrive on other threads.
        internal static void RecordText(string kind, string details, Severity severity)
        {
            if (!enabled || recording) return;
            string? failure = null, notice = null;
            recording = true;
            try
            {
                DateTime now = DateTime.UtcNow;
                string[] fields = { now.ToString("O", CultureInfo.InvariantCulture), Session, role, severity.ToString(), kind, "", "", "", "", "", "", "", "", "", "", "", "", details };
                string line = now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + kind + " | " + Clip(details, 400);
                lock (gate) { Store(kind, severity, null, line, "", fields); notice = writeError; writeError = null; }
            }
            catch (Exception error) { failure = kind + ": " + error; }
            finally { recording = false; }
            if (notice != null) CompatibilityInstaller.Error(notice);
            if (failure != null) ReportFailure(kind, failure);
        }

        internal static void Mark(string who, string note)
        {
            if (!enabled) return;
            Record("player-marker", null, "player=" + who + ";note=" + note);
            lock (gate)
            {
                DateTime now = DateTime.UtcNow;
                markers.Add(new Marker { Utc = now, Who = who, Note = note, Context = recent.Where(entry => (now - entry.Utc).TotalSeconds <= 90).Select(entry => entry.Line).Reverse().Take(30).Reverse().ToArray() });
                if (markers.Count > MarkerLimit) markers.RemoveAt(0);
                Version++;
            }
        }

        private static void Store(string kind, Severity severity, ZDOID? id, string line, string extra, string[] fields)
        {
            counts[kind] = counts.TryGetValue(kind, out int count) ? count + 1 : 1;
            string[] context = Array.Empty<string>();
            if (id != null)
            {
                if (!histories.TryGetValue(id.Value, out Queue<string> history))
                {
                    if (histories.Count > 2048) histories.Clear();
                    histories[id.Value] = history = new Queue<string>();
                }
                if (severity != Severity.Info) context = history.ToArray();
                history.Enqueue(line);
                if (history.Count > ChestHistory) history.Dequeue();
            }
            else if (severity != Severity.Info) context = recent.Skip(Math.Max(0, recent.Count - 8)).Select(entry => entry.Line).ToArray();
            recent.Enqueue((DateTime.UtcNow, line));
            if (recent.Count > RecentLimit) recent.Dequeue();
            if (severity != Severity.Info)
            {
                if (severity == Severity.Problem) problems++; else warnings++;
                findings.Add(new Finding { Utc = DateTime.UtcNow, Severity = severity, Kind = kind, Line = line, Extra = extra, Context = context });
                if (findings.Count > FindingLimit) { findings.RemoveAt(KeptEarlyFindings); droppedFindings++; }
            }
            Version++;
            Write(fields);
        }

        private static void Write(string[] fields)
        {
            if (writeFailed || directory == null) return;
            try
            {
                string text = string.Join("\t", fields.Select(Escape)) + "\n";
                int length = Encoding.UTF8.GetByteCount(text);
                if (writer == null || bytes + length > limit) Rotate();
                writer!.Write(text); writer.Flush(); bytes += length;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                // Keep the in-memory summary so reports still work without the file.
                writeFailed = true; Close();
                reportedFailures.Add("event log: " + error.Message);
                writeError = "Diagnostics stopped writing the event log; reports still work from memory. " + error;
            }
        }

        private static void ReportFailure(string kind, string failure)
        {
            bool first;
            lock (gate) first = reportedFailures.Add(kind);
            if (first) CompatibilityInstaller.Error("Diagnostics could not record '" + kind + "'; gameplay continues. " + failure);
        }

        internal static (Finding[] Findings, int Dropped, Marker[] Markers, string[] Recent, KeyValuePair<string, int>[] Counts, string[] Failures, bool WriteFailed) Snapshot(int recentLines)
        {
            lock (gate)
                return (findings.ToArray(), droppedFindings, markers.ToArray(),
                    recent.Skip(Math.Max(0, recent.Count - recentLines)).Select(entry => entry.Line).ToArray(),
                    counts.OrderByDescending(entry => entry.Value).ThenBy(entry => entry.Key, StringComparer.Ordinal).ToArray(),
                    reportedFailures.ToArray(), writeFailed);
        }

        internal static string Items(Inventory inventory) => string.Join(";", inventory.GetAllItems()
            .Where(item => item?.m_shared != null)
            .GroupBy(item => new { item.m_shared.m_name, item.m_quality, item.m_worldLevel })
            .OrderBy(group => group.Key.m_name, StringComparer.Ordinal).ThenBy(group => group.Key.m_quality)
            .ThenBy(group => group.Key.m_worldLevel)
            .Select(group => group.Key.m_name + ":q" + group.Key.m_quality + ":w" + group.Key.m_worldLevel
                + "=" + group.Sum(item => item.m_stack)));

        // Item totals keyed like Items, for before/after comparisons.
        internal static Dictionary<string, int> Counts(IEnumerable<Inventory> inventories)
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (Inventory inventory in inventories)
                foreach (ItemDrop.ItemData item in inventory.GetAllItems())
                {
                    if (item?.m_shared == null) continue;
                    string key = item.m_shared.m_name + ":q" + item.m_quality + ":w" + item.m_worldLevel;
                    result[key] = (result.TryGetValue(key, out int count) ? count : 0) + item.m_stack;
                }
            return result;
        }

        internal static Dictionary<string, int> Delta(Dictionary<string, int> before, Dictionary<string, int> after)
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string key in before.Keys.Union(after.Keys))
            {
                int change = (after.TryGetValue(key, out int a) ? a : 0) - (before.TryGetValue(key, out int b) ? b : 0);
                if (change != 0) result[key] = change;
            }
            return result;
        }

        internal static string Format(Dictionary<string, int> delta) => delta.Count == 0 ? "none" : string.Join(";", delta
            .OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => entry.Key + "=" + (entry.Value > 0 ? "+" : "") + entry.Value));

        internal static string Short(string hash) => hash.Length > 12 ? hash.Substring(0, 12) : hash;

        internal static string Stack(int skip, int frames)
        {
            var trace = new System.Diagnostics.StackTrace(skip + 1, false);
            return string.Join(" < ", trace.GetFrames()?.Select(frame => frame.GetMethod()).Where(method => method != null)
                .Select(method => (method!.DeclaringType?.FullName ?? "?") + "." + method.Name)
                .Where(name => !name.StartsWith("DeepNorthCompat.ChestDiagnostics", StringComparison.Ordinal))
                .Take(frames) ?? Enumerable.Empty<string>());
        }

        private static string Clip(string value, int length) => value.Length <= length ? value : value.Substring(0, length) + "…";
        private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");

        private static void Rotate()
        {
            Close(); System.IO.Directory.CreateDirectory(directory!);
            const string prefix = "DeepNorthCompat-events-";
            string name = prefix + role + "-" + Started.ToString("yyyyMMddTHHmmss", CultureInfo.InvariantCulture) + "-" + Session
                + "-" + (++part).ToString("D3", CultureInfo.InvariantCulture) + ".tsv";
            writer = new StreamWriter(new FileStream(Path.Combine(directory!, name), FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
            string header = "# DeepNorthCompat " + typeof(Plugin).Assembly.GetName().Version + " " + role + " session " + Session
                + " started " + Started.ToString("O", CultureInfo.InvariantCulture) + " part " + part + "\n" + Header + "\n";
            writer.Write(header); bytes = Encoding.UTF8.GetByteCount(header);
            var files = new DirectoryInfo(directory!).GetFiles(prefix + role + "-*.tsv");
            // Keep the newest sessions whole. Within this session, keep the first part, which
            // holds the session start, and the newest parts.
            foreach (var session in files.GroupBy(file => SessionOf(file.Name)).OrderByDescending(group => group.Max(file => file.LastWriteTimeUtc))
                .Where(group => group.Key != Session).Skip(SessionsRetained - 1))
                foreach (FileInfo old in session) old.Delete();
            foreach (FileInfo old in files.Where(file => SessionOf(file.Name) == Session).OrderByDescending(file => file.Name, StringComparer.Ordinal)
                .Skip(PartsPerSession).Where(file => !file.Name.EndsWith("-001.tsv", StringComparison.Ordinal)))
                old.Delete();
        }

        private static string SessionOf(string file)
        {
            string[] parts = Path.GetFileNameWithoutExtension(file).Split('-');
            return parts.Length >= 2 ? parts[parts.Length - 2] : file;
        }

        internal static void Close()
        {
            StreamWriter? closing = writer; writer = null;
            try { closing?.Dispose(); }
            catch (IOException error)
            {
                CompatibilityInstaller.Error("Diagnostics could not finish closing the event log: " + error);
            }
        }

        internal static void Disable()
        {
            lock (gate) { Close(); enabled = false; directory = null; }
        }

        // Clears the in-memory summary. Used by the offline tests between cases.
        internal static void Clear()
        {
            lock (gate)
            {
                recent.Clear(); histories.Clear(); findings.Clear(); markers.Clear(); counts.Clear(); reportedFailures.Clear();
                droppedFindings = problems = warnings = 0; Version++;
            }
        }
    }
}
