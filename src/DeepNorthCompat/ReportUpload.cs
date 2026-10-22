using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using UnityEngine;

namespace DeepNorthCompat
{
    // Sends a player's report to the server they play on, which saves it with its own snapshot
    // for the operator to collect later, and confirms the save. Uploads are direct messages on
    // the peer's connection, so the server names the reporter from that connection: a routed
    // message's sender ID comes from the packet and can be forged.
    internal static class ReportUpload
    {
        internal const string UploadRpc = "DeepNorthCompat.ReportUpload.v1", AckRpc = "DeepNorthCompat.ReportAck.v1";
        internal const int CompressedLimit = 64 * 1024, TextLimit = 512 * 1024, NoteLimit = 500, NameLimit = 64;
        internal const int RetainedDays = 30, RetainedFiles = 200, PerReporter = 20;
        internal const long RetainedBytes = 100L * 1024 * 1024;
        private const int Format = 1, Burst = 3, LoggedRejections = 5;
        private const double RefillSeconds = 20, AckSeconds = 15;
        private const string Index = "index.tsv", IndexHeader = "received_utc\treference\tkind\tplayer\tplatform_id\tpeer\tfile\tnote";
        private static readonly string[] Kinds = { "marker", "report", "last" };
        private static readonly Regex UserFolder = new Regex(@"(?i)((?:[a-z]:)?[\\/]+(?:Users|home)[\\/]+)[^\\/\s""'`<>|:;,]+", RegexOptions.Compiled);
        private static readonly Regex Ipv4 = new Regex(@"(?<!\w|\d\.)(?:25[0-5]|2[0-4]\d|1?\d?\d)(?:\.(?:25[0-5]|2[0-4]\d|1?\d?\d)){2}\.(?<last>25[0-5]|2[0-4]\d|1?\d?\d)(?<port>:\d{1,5})?(?!\w|\.\d)", RegexOptions.Compiled);
        private static readonly Regex VersionBefore = new Regex(@"(?i)\bver(?:sion)?\s*[=:]?\s*$", RegexOptions.Compiled);
        private static readonly Regex Ipv6 = new Regex(@"(?<![\w:])(?:(?:[0-9A-Fa-f]{1,4}:){7}[0-9A-Fa-f]{1,4}|(?:[0-9A-Fa-f]{1,4}:){1,7}:(?:[0-9A-Fa-f]{1,4}(?::[0-9A-Fa-f]{1,4}){0,5})?|::[0-9A-Fa-f]{1,4}(?::[0-9A-Fa-f]{1,4}){0,6})(?![\w:])", RegexOptions.Compiled);

        private sealed class Pending
        {
            internal long Id;
            internal Terminal? Context;
            internal DateTime Until;
            internal string? Sent;
        }
        internal sealed class Upload
        {
            internal long Id, PlayerId;
            internal string Kind = "", Note = "", Name = "", Text = "";
            internal DateTime ClientUtc;
            internal Vector3? Point;
            internal ZDOID Looked = ZDOID.None;
            internal int Compressed;
        }

        internal static bool Send = true, Accept = true, Hooked;
        private static readonly object gate = new object();
        private static readonly Dictionary<long, (double Tokens, DateTime At)> buckets = new Dictionary<long, (double, DateTime)>();
        private static readonly Dictionary<long, int> rejections = new Dictionary<long, int>();
        private static Pending? pending;
        private static bool serverAccepts;

        // A server advertises this in its hello reply. A server without DeepNorthCompat, with
        // diagnostics off or with uploads refused sends no flag, and clients keep reports local.
        internal static bool Accepting => Accept && Hooked && ChestDiagnostics.Enabled && ChestDiagnostics.Directory != null;
        internal static bool ServerAccepts { get { lock (gate) return serverAccepts; } set { lock (gate) serverAccepts = value; } }

        // ZRoutedRpc.AddPeer runs on both sides once a connection is ready: on the server for
        // each player, and on a client for its server.
        internal static void PeerAdded(ZNetPeer peer)
        {
            try
            {
                if (!ChestDiagnostics.Enabled || ZNet.instance == null || peer?.m_rpc == null) return;
                if (ZNet.instance.IsServer()) { if (Accepting) peer.m_rpc.Register<ZPackage>(UploadRpc, Receive); }
                else peer.m_rpc.Register<ZPackage>(AckRpc, ReceiveAck);
            }
            catch (Exception error) { ChestDiagnostics.RecordText("diagnostics-hook-error", "ReportUpload.PeerAdded: " + error, Severity.Info); }
        }

        internal static void WorldStarted()
        {
            lock (gate) { buckets.Clear(); rejections.Clear(); pending = null; serverAccepts = false; }
            if (ZNet.instance != null && ZNet.instance.IsServer() && Accepting) Prune(Directory());
        }

        internal static void PeerGone(long uid)
        {
            lock (gate) { buckets.Remove(uid); rejections.Remove(uid); }
        }

        // ---- Client ----

        // Builds, redacts and sends one report. `build` receives true to ask for the compact budget
        // when the full report is over the size limits. Single player, a host's own reports and
        // a client that turned sending off stay local without a message.
        internal static void Submit(Terminal context, string kind, string note, Func<bool, string> build, Vector3? point, ZDOID looked, string? sent = null)
        {
            if (!Send || ZNet.instance == null || ZNet.instance.IsServer() || ZNet.instance.GetServerPeer() == null) return;
            if (!Hooked) { context.AddString("DeepNorthCompat: report uploads failed to start on this computer (see the BepInEx log), so the report stays here."); return; }
            if (!ServerAccepts) { context.AddString("DeepNorthCompat: this server doesn't collect reports, so the report stays on this computer."); return; }
            lock (gate)
                if (pending != null) { context.AddString("DeepNorthCompat: the server hasn't confirmed the previous report yet. Try again in a few seconds."); return; }
            (byte[] Body, int Length)? packed = Compress(Redact(build(false)));
            if (packed == null) packed = Compress(Redact(build(true)));
            if (packed == null) { context.AddString("DeepNorthCompat: report not sent to the server: it is larger than the upload limit."); return; }
            Player player = Player.m_localPlayer;
            long id = DateTime.UtcNow.Ticks;
            ZPackage package = Pack(id, kind, Redact(note), player != null ? player.GetPlayerName() : "", player != null ? player.GetPlayerID() : 0L,
                DateTime.UtcNow, point, looked, packed.Value.Body, packed.Value.Length);
            lock (gate) pending = new Pending { Id = id, Context = context, Until = DateTime.UtcNow.AddSeconds(AckSeconds), Sent = sent };
            ZNet.instance.GetServerPeer().m_rpc.Invoke(UploadRpc, package);
            context.AddString("DeepNorthCompat: sending the report to the server (" + (packed.Value.Body.Length / 1024 + 1) + " KB)…");
        }

        internal static ZPackage Pack(long id, string kind, string note, string name, long playerId, DateTime clientUtc, Vector3? point, ZDOID looked, byte[] body, int length)
        {
            var package = new ZPackage();
            package.Write(Format); package.Write(id); WriteText(package, kind); WriteText(package, Clip(note, NoteLimit));
            WriteText(package, Clip(name, NameLimit)); package.Write(playerId); package.Write(clientUtc.Ticks);
            package.Write(point.HasValue);
            if (point.HasValue) { package.Write(point.Value); package.Write(looked); }
            package.Write(length); package.Write(body);
            return package;
        }

        // Returns null when the text is over either limit.
        internal static (byte[] Body, int Length)? Compress(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            if (bytes.Length == 0 || bytes.Length > TextLimit) return null;
            using var output = new MemoryStream();
            using (var zip = new GZipStream(output, CompressionMode.Compress)) zip.Write(bytes, 0, bytes.Length);
            byte[] body = output.ToArray();
            return body.Length > CompressedLimit ? null : (body, bytes.Length);
        }

        private static void ReceiveAck(ZRpc rpc, ZPackage package)
        {
            try
            {
                if (ZNet.instance == null || ZNet.instance.GetServerPeer()?.m_rpc != rpc) return;
                if (package.ReadInt() != Format) return;
                long id = package.ReadLong(); bool saved = package.ReadBool(); string message = ReadText(package, 1024);
                Pending? done;
                lock (gate) { done = pending; if (done == null || done.Id != id) return; pending = null; }
                if (saved && done.Sent != null) DiagnosticReport.MarkPreviousSent(done.Sent);
                Print(done.Context, saved ? "DeepNorthCompat: report saved on the server. Reference: " + message
                    : "DeepNorthCompat: the server did not save the report: " + message);
            }
            catch (Exception error) { ChestDiagnostics.RecordText("diagnostics-hook-error", "ReportUpload.ReceiveAck: " + error, Severity.Info); }
        }

        internal static void Tick()
        {
            Pending? expired;
            lock (gate)
            {
                if (pending == null || DateTime.UtcNow < pending.Until) return;
                expired = pending; pending = null;
            }
            Print(expired.Context, "DeepNorthCompat: delivery unconfirmed. The server didn't confirm the report within "
                + (int)AckSeconds + " seconds. It may still have saved it.");
        }

        private static void Print(Terminal? context, string text)
        {
            if (context != null) context.AddString(text);
            else if (Chat.instance != null) Chat.instance.AddString(text);
        }

        // Replaces the folders that name this computer's user and the addresses of machines.
        // Exception text stays: it is usually the most useful line in a report.
        internal static string Redact(string text)
        {
            var roots = new List<(string Path, string Label)>();
            void Add(string? path, string label) { if (!string.IsNullOrWhiteSpace(path) && path!.Trim().Length > 3) roots.Add((path.TrimEnd('\\', '/'), label)); }
            try
            {
                Add(ChestDiagnostics.Directory, "<diagnostics>");
                Add(Paths.BepInExRootPath, "<BepInEx>");
                Add(Paths.GameRootPath, "<game>");
            }
            catch (Exception error) when (error is ArgumentException || error is NullReferenceException) { }
            Add(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), "<home>");
            foreach (var (path, label) in roots.OrderByDescending(root => root.Path.Length))
            {
                string pattern = string.Join(@"[\\/]+", path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape));
                if (path.StartsWith("/", StringComparison.Ordinal)) pattern = "/" + pattern;
                text = Regex.Replace(text, pattern, label, RegexOptions.IgnoreCase);
            }
            text = UserFolder.Replace(text, match => match.Groups[1].Value + "<user>");
            // A four-part version such as 1.1.0.0 is also a valid address. An address with a port
            // is always replaced. Without one, a quad that ends in .0, which names a network
            // rather than a machine, or follows "version" or "ver", stays.
            string source = text;
            text = Ipv4.Replace(source, match => match.Groups["port"].Success
                || (match.Groups["last"].Value != "0" && !VersionBefore.IsMatch(source.Substring(Math.Max(0, match.Index - 12), Math.Min(12, match.Index))))
                ? "<address>" : match.Value);
            return Ipv6.Replace(text, "<address>");
        }

        // ---- Server ----

        private static void Receive(ZRpc rpc, ZPackage package)
        {
            try
            {
                if (!Accepting || ZNet.instance == null || !ZNet.instance.IsServer()) return;
                ZNetPeer? peer = ZNet.instance.GetPeers().FirstOrDefault(value => value.m_rpc == rpc);
                if (peer == null || !peer.IsReady()) return;
                // The format and request ID come first, so even a refusal names the request it answers.
                long id = 0;
                if (package.Size() >= 12 && package.ReadInt() == Format) id = package.ReadLong();
                package.SetPos(0);
                string? error = Admit(peer.m_uid);
                Upload? upload = null;
                if (error == null && package.Size() > CompressedLimit + 4096) error = "the report is larger than the upload limit";
                if (error == null)
                {
                    try { upload = Read(package); }
                    catch (Exception invalid) when (invalid is FormatException || invalid is EndOfStreamException || invalid is IOException
                        || invalid is InvalidDataException || invalid is ArgumentException || invalid is OverflowException)
                    { error = "the report was malformed (" + invalid.Message + ")"; }
                }
                string? reference = upload == null ? null : Store(peer, upload, out error);
                if (reference == null)
                {
                    Reject(peer, error ?? "unknown");
                    Ack(rpc, id, false, error ?? "unknown");
                    return;
                }
                ChestDiagnostics.RecordText("report-upload-saved", "peer=" + peer.m_uid + ";player=" + peer.m_playerName + ";kind=" + upload!.Kind + ";reference=" + reference, Severity.Info);
                Ack(rpc, id, true, reference);
            }
            catch (Exception error) { ChestDiagnostics.RecordText("diagnostics-hook-error", "ReportUpload.Receive: " + error, Severity.Info); }
        }

        // A token bucket per connection: a few reports in a row, then one every 20 seconds.
        private static string? Admit(long uid)
        {
            DateTime now = DateTime.UtcNow;
            lock (gate)
            {
                double tokens = Burst;
                if (buckets.TryGetValue(uid, out var bucket)) tokens = Math.Min(Burst, bucket.Tokens + (now - bucket.At).TotalSeconds / RefillSeconds);
                if (tokens < 1) { buckets[uid] = (tokens, now); return "too many reports in a short time; wait a minute and try again"; }
                buckets[uid] = (tokens - 1, now);
                return null;
            }
        }

        internal static Upload Read(ZPackage package)
        {
            if (package.ReadInt() != Format) throw new FormatException("unsupported report format");
            var upload = new Upload { Id = package.ReadLong() };
            upload.Kind = ReadText(package, 16);
            if (!Kinds.Contains(upload.Kind)) throw new FormatException("unknown report kind");
            upload.Note = Clean(ReadText(package, NoteLimit * 4));
            upload.Name = Clean(ReadText(package, NameLimit * 4));
            upload.PlayerId = package.ReadLong();
            long ticks = package.ReadLong();
            if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) throw new FormatException("invalid client time");
            upload.ClientUtc = new DateTime(ticks, DateTimeKind.Utc);
            if (package.ReadBool())
            {
                Vector3 point = package.ReadVector3(); upload.Looked = package.ReadZDOID();
                float sum = point.x + point.y + point.z;
                if (float.IsNaN(sum) || float.IsInfinity(sum)) throw new FormatException("invalid position");
                upload.Point = point;
            }
            int length = package.ReadInt();
            if (length <= 0 || length > TextLimit) throw new FormatException("declared size out of range");
            byte[] body = ReadBytes(package, CompressedLimit);
            upload.Compressed = body.Length;
            upload.Text = Encoding.UTF8.GetString(Decompress(body, length));
            return upload;
        }

        // Reads exactly `length` bytes, and rejects a body that expands past it.
        private static byte[] Decompress(byte[] body, int length)
        {
            var output = new byte[length];
            using var zip = new GZipStream(new MemoryStream(body), CompressionMode.Decompress);
            int read = 0;
            while (read < length)
            {
                int count = zip.Read(output, read, length - read);
                if (count == 0) throw new FormatException("report shorter than declared");
                read += count;
            }
            if (zip.Read(new byte[1], 0, 1) != 0) throw new FormatException("report longer than declared");
            return output;
        }

        private static string? Store(ZNetPeer peer, Upload upload, out string? error)
        {
            error = null;
            string? directory = Directory();
            if (directory == null) { error = "the server has no diagnostics directory"; return null; }
            DateTime now = DateTime.UtcNow;
            string stamp = now.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture), suffix = Guid.NewGuid().ToString("N").Substring(0, 4);
            string reference = stamp + "-" + suffix;
            string player = Clean(peer.m_playerName ?? ""), host = Clean(peer.m_socket?.GetHostName() ?? "");
            string file = stamp + "-" + Safe(player, 24) + "-" + Safe(host, 40) + "-" + suffix + ".md";
            // The player's report is the evidence, so a failed server snapshot still saves it.
            string server;
            try
            {
                string[]? probe = upload.Point.HasValue ? WorldProbe.Capture(upload.Point.Value, upload.Looked) : null;
                server = DiagnosticReport.Snapshot(compact: false, probe);
            }
            catch (Exception failure) { server = "The server could not build its report: " + failure + "\n"; }
            var b = new StringBuilder();
            b.Append("# DeepNorthCompat player report ").AppendLine(reference).AppendLine();
            b.AppendLine("- Reporter: " + (player.Length == 0 ? "unknown" : player) + " (platform ID " + (host.Length == 0 ? "unknown" : host) + ", peer " + peer.m_uid + ")");
            b.AppendLine("- Client claims: " + (upload.Name.Length == 0 ? "no name" : upload.Name) + " (player ID " + upload.PlayerId + ")"
                + (upload.Name.Length > 0 && upload.Name != player ? ". This differs from the name the connection logged in with." : ""));
            b.AppendLine("- Kind: " + upload.Kind + (upload.Kind == "last" ? " (the client's report from its previous session)" : ""));
            if (upload.Note.Length > 0) b.AppendLine("- Note: " + upload.Note);
            b.AppendLine("- Received " + now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC. Client clock "
                + upload.ClientUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC, offset "
                + ((upload.ClientUtc - now).TotalSeconds).ToString("+0;-0", CultureInfo.InvariantCulture) + " s.");
            b.AppendLine("- Upload: " + upload.Compressed + " bytes compressed, " + Encoding.UTF8.GetByteCount(upload.Text) + " bytes of text.").AppendLine();
            b.AppendLine("---").AppendLine().Append(upload.Text.TrimEnd()).AppendLine().AppendLine();
            b.AppendLine("---").AppendLine().AppendLine("# Server report at receipt").AppendLine().Append(server);
            string path = Path.Combine(directory, file), temporary = path + ".tmp";
            try
            {
                System.IO.Directory.CreateDirectory(directory);
                File.WriteAllText(temporary, b.ToString(), new UTF8Encoding(false));
                File.Move(temporary, path);
                string index = Path.Combine(directory, Index);
                string line = string.Join("\t", new[] { now.ToString("O", CultureInfo.InvariantCulture), reference, upload.Kind, player, host,
                    peer.m_uid.ToString(CultureInfo.InvariantCulture), file, upload.Note }.Select(Field)) + "\n";
                File.AppendAllText(index, (File.Exists(index) ? "" : IndexHeader + "\n") + line, new UTF8Encoding(false));
                Prune(directory);
                return reference;
            }
            catch (Exception failure) when (failure is IOException || failure is UnauthorizedAccessException)
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch (Exception cleanup) when (cleanup is IOException || cleanup is UnauthorizedAccessException) { }
                CompatibilityInstaller.Error("Diagnostics could not save a player report: " + failure);
                error = "the server could not write the report";
                return null;
            }
        }

        internal static string? Directory() => ChestDiagnostics.Directory == null ? null : Path.Combine(ChestDiagnostics.Directory, "submissions");

        // Removes reports past their age, the per-player cap, the file count or the byte total,
        // oldest first, with their index lines, and any temporary file left by a crash.
        internal static void Prune(string? directory)
        {
            if (directory == null || !System.IO.Directory.Exists(directory)) return;
            try
            {
                var info = new DirectoryInfo(directory);
                foreach (FileInfo temporary in info.GetFiles("*.md.tmp")) temporary.Delete();
                // Names start with the UTC time, so name order is age order.
                FileInfo[] files = info.GetFiles("*.md").Where(file => file.Name.Split('-').Length == 4)
                    .OrderByDescending(file => file.Name, StringComparer.Ordinal).ToArray();
                var keep = new HashSet<string>(StringComparer.Ordinal);
                var perReporter = new Dictionary<string, int>(StringComparer.Ordinal);
                string oldest = DateTime.UtcNow.AddDays(-RetainedDays).ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture);
                long total = 0;
                var removed = new List<FileInfo>();
                foreach (FileInfo file in files)
                {
                    string[] parts = file.Name.Split('-');
                    int count = perReporter.TryGetValue(parts[2], out int seen) ? seen : 0;
                    if (string.CompareOrdinal(parts[0], oldest) < 0 || count >= PerReporter || keep.Count >= RetainedFiles || total + file.Length > RetainedBytes)
                    { removed.Add(file); continue; }
                    perReporter[parts[2]] = count + 1; total += file.Length; keep.Add(file.Name);
                }
                foreach (FileInfo file in removed) file.Delete();
                string index = Path.Combine(directory, Index);
                if (removed.Count == 0 || !File.Exists(index)) return;
                var gone = new HashSet<string>(removed.Select(file => file.Name), StringComparer.Ordinal);
                var lines = File.ReadAllLines(index).Where(line => { string[] fields = line.Split('\t'); return fields.Length < 7 || !gone.Contains(fields[6]); });
                File.WriteAllText(index + ".tmp", string.Join("\n", lines) + "\n", new UTF8Encoding(false));
                File.Delete(index); File.Move(index + ".tmp", index);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                CompatibilityInstaller.Error("Diagnostics could not prune player reports: " + error);
            }
        }

        private static void Ack(ZRpc rpc, long id, bool saved, string message)
        {
            var package = new ZPackage();
            package.Write(Format); package.Write(id); package.Write(saved); WriteText(package, message);
            rpc.Invoke(AckRpc, package);
        }

        private static void Reject(ZNetPeer peer, string reason)
        {
            int count;
            lock (gate) { rejections[peer.m_uid] = count = (rejections.TryGetValue(peer.m_uid, out int seen) ? seen : 0) + 1; }
            if (count <= LoggedRejections)
                ChestDiagnostics.RecordText("report-upload-rejected", "peer=" + peer.m_uid + ";player=" + peer.m_playerName + ";reason=" + reason
                    + (count == LoggedRejections ? ";further rejections for this peer are not logged" : ""), Severity.Info);
        }

        // Strings travel as length-prefixed byte arrays so a length is checked before anything
        // is allocated for it. ZPackage.ReadString sizes its buffer from the declared length.
        private static void WriteText(ZPackage package, string text) => package.Write(Encoding.UTF8.GetBytes(text));
        private static string ReadText(ZPackage package, int limit) => Encoding.UTF8.GetString(ReadBytes(package, limit));

        private static byte[] ReadBytes(ZPackage package, int limit)
        {
            int start = package.GetPos();
            int length = package.ReadInt();
            if (length < 0 || length > limit || length > package.Size() - package.GetPos()) throw new FormatException("field length out of range");
            package.SetPos(start);
            return package.ReadByteArray();
        }

        private static string Clean(string value) => new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
        private static string Safe(string value, int length)
        {
            string safe = new string(value.Where(c => c < 128 && (char.IsLetterOrDigit(c) || c == '_')).ToArray());
            return safe.Length == 0 ? "unknown" : safe.Length <= length ? safe : safe.Substring(0, length);
        }
        private static string Field(string value) => value.Replace("\t", " ").Replace("\r", " ").Replace("\n", " ");
        private static string Clip(string value, int length) => value.Length <= length ? value : value.Substring(0, length);

        // Clears connection state. Used by the offline tests between cases.
        internal static void Clear()
        {
            lock (gate) { buckets.Clear(); rejections.Clear(); pending = null; serverAccepts = false; }
        }
    }
}
