using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace InspectionEditor.Services
{
    public sealed class SyncOptions
    {
        public required string InspectionsFolder { get; init; }
        public required StrandWebsiteSession Session { get; init; }
        public string? LocalRoot { get; init; }                         // RED's app data (state, backups)
        public Func<string, bool>? IsOpenWithUnsavedChanges { get; init; } // a RED editor holds unsaved edits for this path
        public bool PlanOnly { get; init; }                             // V8 "Sync plan": B1 + the three GetFileList calls only
        public Func<string, DateTime?>? ReadInspectLastSyncDate { get; init; } // tests; default reads {IF}\config.json live
    }

    public sealed class SyncReport
    {
        public int Submitted { get; set; }
        public int Downloaded { get; set; }
        public int Uploaded { get; set; }
        public bool Stopped { get; set; }
        public string Headline { get; set; } = "";
        public List<string> Problems { get; } = new();
        public List<string> PlannedDownloads { get; } = new();
        public List<string> PlannedUploads { get; } = new();
        public string Text => string.Join(Environment.NewLine, new[] { Headline }.Concat(Problems).Where(s => s.Length > 0));
    }

    // F-013 §3B: the NonDropBoxInspector Outbox Sync (DashboardWindow::barButtonSync_ItemClick). Uploads each Outbox file
    // with NormalSubmit, then two-way syncs MyList, Review and Archive. RED keeps LastSyncDate in its own state per
    // Inspections folder and never writes INSPECT's config.json (B4, C8). Deletions are never sent either way.
    public static class StrandSyncService
    {
        private static int _running;
        public static bool IsRunning => Volatile.Read(ref _running) == 1;
        public const string SendingText = "Sending inspections ...";
        public static readonly string[] Folders = { "MyList", "Review", "Archive" };

        private sealed record ServerEntry(string SvrPath, string FileName, long FileLength, DateTime? LastModDate);
        private sealed record LocalEntry(string FileName, long Length, DateTime LastWrite, string FullPath);

        public static async Task<SyncReport> SyncAsync(SyncOptions options, CancellationToken token = default)
        {
            var report = new SyncReport();
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            {
                report.Stopped = true;
                report.Headline = "A Sync is already running.";
                return report;
            }
            try
            {
                return await RunAsync(options, report, token);
            }
            finally
            {
                Volatile.Write(ref _running, 0);
            }
        }

        private static async Task<SyncReport> RunAsync(SyncOptions o, SyncReport report, CancellationToken token)
        {
            var session = o.Session;
            string localRoot = o.LocalRoot ?? AppIdentity.LocalAppDataPath;
            if (!session.HasCredentials)
            {
                report.Stopped = true;
                report.Headline = "Sign in to Strand first. Sync uploads to Strand.";
                return report;
            }

            // B1: internet check, an anonymous GET of the website base.
            if (!await IsOnlineAsync(session.Base, token))
            {
                report.Stopped = true;
                report.Headline = $"Cannot connect to {session.Base}. Please check your internet connection and try again.";
                return report;
            }
            try { await session.EnsureAsync(false, token); }
            catch (Exception)
            {
                report.Stopped = true;
                report.Headline = $"Cannot connect to {session.Base}. Please check your internet connection and try again.";
                return report;
            }

            string username = session.Username;
            var uploader = new StrandWebsiteUploader(session);

            // B2: Outbox uploads (not in a plan).
            if (!o.PlanOnly)
            {
                ProcessPendingOutboxDeletes(o.InspectionsFolder, localRoot, report);
                var stillPending = new HashSet<string>(SubmitService.PendingPaths(PendingRoot(localRoot)), StringComparer.OrdinalIgnoreCase);
                string outbox = Path.Combine(o.InspectionsFolder, "Outbox");
                var files = Directory.Exists(outbox)
                    ? Directory.EnumerateFiles(outbox, "*", SearchOption.TopDirectoryOnly)
                        .Where(f => f.EndsWith(".ins", StringComparison.OrdinalIgnoreCase))
                        .Where(f => !stillPending.Contains(Path.GetFullPath(f))) // sent before: never sent twice (C9)
                        .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal).ToList()
                    : new List<string>();
                for (int i = 0; i < files.Count; i++)
                {
                    string file = files[i];
                    string name = Path.GetFileName(file);
                    byte[] bytes;
                    JObject json;
                    try
                    {
                        bytes = File.ReadAllBytes(file);
                        json = InspectionFileReader.ParseDocument(bytes, out _);
                    }
                    catch (Exception)
                    {
                        report.Problems.Add($"{name}: not a readable inspection; it stays in the Outbox.");
                        continue;
                    }
                    var reply = await uploader.UploadInspectionAsync("/Inspect/Inspection/NormalSubmit", name, bytes, token);
                    if (reply.Outcome == WebsiteOutcome.Accepted)
                    {
                        string number = HeaderRules.Text(json["InspectionNumber"]) is { Length: > 0 } n && SubmitService.IsSafeReportName(n)
                            ? n : Path.GetFileNameWithoutExtension(name);
                        bool kept;
                        try
                        {
                            string archive = Path.Combine(InspectFolders.EnsureForWrite(o.InspectionsFolder, InspectFolder.Archive), number + ".ins");
                            SubmitService.WriteReplacing(archive, bytes, Path.Combine(localRoot, SubmitService.SubmitBackupsFolder));
                            kept = true;
                        }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                        {
                            // B2: the Outbox file is the only local copy. Keep a verified copy in RED's own folder first;
                            // without one it stays in the Outbox.
                            kept = KeepLocalCopy(localRoot, name, bytes);
                            report.Problems.Add(kept
                                ? $"{name}: sent, but the Archive copy could not be written; RED kept a copy in its own folder. {ex.Message}"
                                : $"{name}: sent, but no Archive copy could be written, so it stays in the Outbox. Do not Sync it again before Strand confirms it. {ex.Message}");
                        }
                        if (kept && !SubmitService.RemoveSource(file, bytes, PendingRoot(localRoot)))
                            report.Problems.Add($"{name}: sent, but it could not be removed from the Outbox.");
                        report.Submitted++;
                    }
                    else if (reply.Outcome == WebsiteOutcome.Rejected)
                    {
                        report.Stopped = true;
                        report.Headline = $"Cannot submit to server. {reply.Message}";
                        report.Problems.Add($"{name} and the files after it were not sent. Sync stopped.");
                        return report; // the folder sync does not run; LastSyncDate unchanged
                    }
                    else
                    {
                        report.Problems.Add($"{name}: Strand did not answer; it stays in the Outbox.");
                    }
                }
            }

            // B3: folder sync.
            DateTime start = DateTime.Now;
            DateTime? since = EffectiveLastSyncDate(o, localRoot);
            foreach (string folder in Folders)
            {
                var server = await GetFileListAsync(session, folder, username, token);
                if (server == null)
                {
                    report.Stopped = true;
                    report.Headline = $"Could not get the {folder} file list from Strand. Sync stopped.";
                    return report;
                }
                if (since.HasValue)
                    server = server.Where(e => e.LastModDate.HasValue && e.LastModDate.Value >= since.Value).ToList();

                string localFolder = Path.Combine(o.InspectionsFolder, folder);
                var local = Directory.Exists(localFolder)
                    ? Directory.EnumerateFiles(localFolder, "*", SearchOption.AllDirectories)
                        .Where(f => f.EndsWith(".ins", StringComparison.OrdinalIgnoreCase))
                        .Select(f => new FileInfo(f))
                        .Select(f => new LocalEntry(f.Name, f.Length, f.LastWriteTime, f.FullName))
                        .Where(f => !since.HasValue || f.LastWrite >= since.Value)
                        .ToList()
                    : new List<LocalEntry>();

                bool Equal(ServerEntry s, LocalEntry l) => string.Equals(s.FileName, l.FileName, StringComparison.Ordinal) && s.FileLength == l.Length;
                if (server.Count == local.Count && server.Zip(local).All(p => Equal(p.First, p.Second)))
                    continue;

                var downloads = server.Where(s => !local.Any(l => Equal(s, l))).ToList();
                var uploads = local.Where(l => !server.Any(s => Equal(s, l))).ToList();
                if (o.PlanOnly)
                {
                    report.PlannedDownloads.AddRange(downloads.Select(d => $"{folder}\\{d.FileName}"));
                    report.PlannedUploads.AddRange(uploads.Select(u => $"{folder}\\{u.FileName}"));
                    continue;
                }

                foreach (var download in downloads)
                {
                    string target = Path.Combine(localFolder, download.FileName);
                    if (download.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    {
                        report.Problems.Add($"{download.FileName}: not a safe file name; not downloaded.");
                        continue;
                    }
                    if (o.IsOpenWithUnsavedChanges?.Invoke(target) == true)
                    {
                        report.Problems.Add($"{download.FileName}: open with unsaved changes; Strand's copy was not downloaded.");
                        continue;
                    }
                    byte[]? body = await DownloadAsync(session, download.SvrPath + "\\" + download.FileName, token);
                    if (body == null)
                    {
                        report.Problems.Add($"{download.FileName}: download failed.");
                        continue;
                    }
                    try
                    {
                        Directory.CreateDirectory(localFolder);
                        if (File.Exists(target))
                        {
                            string backup = Path.Combine(localRoot, "SyncBackups", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), folder);
                            Directory.CreateDirectory(backup);
                            File.Copy(target, Path.Combine(backup, $"{DateTime.Now:HHmmssfff}-{download.FileName}"), overwrite: false);
                            if (uploads.Any(u => string.Equals(u.FileName, download.FileName, StringComparison.Ordinal)))
                                report.Problems.Add($"{download.FileName}: Strand's copy replaced yours; yours is in RED's sync backups.");
                        }
                        SubmitService.PlaceAtomically(target, body, Path.Combine(localRoot, "sync-staging"), localFolder, download.FileName);
                        SubmitService.ForgetPendingDelete(target, localRoot);

                        report.Downloaded++;
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        report.Problems.Add($"{download.FileName}: download could not be written. {ex.Message}");
                    }
                }
                foreach (var upload in uploads)
                {
                    byte[] bytes;
                    try { bytes = File.ReadAllBytes(upload.FullPath); }
                    catch (Exception) { report.Problems.Add($"{upload.FileName}: could not be read; not uploaded."); continue; }
                    var reply = await UploadAsync(session, folder, username, upload.FileName, bytes, token);
                    if (reply.Outcome == WebsiteOutcome.Accepted) report.Uploaded++;
                    else if (reply.Outcome == WebsiteOutcome.Rejected) report.Problems.Add($"Cannot upload file '{upload.FileName}' to server. {reply.Message}");
                    else report.Problems.Add($"{upload.FileName}: Strand did not answer; not uploaded.");
                }
            }

            if (o.PlanOnly)
            {
                report.Headline = $"Sync plan: {report.PlannedDownloads.Count} to download, {report.PlannedUploads.Count} to upload. Nothing was sent or written.";
                return report;
            }

            // B4: RED's LastSyncDate = start, in RED's own state (never config.json).
            WriteRedLastSyncDate(localRoot, o.InspectionsFolder, start);
            report.Headline = $"Sync complete. {report.Submitted} inspections submitted.";
            report.Problems.Insert(0, $"{report.Downloaded} downloaded, {report.Uploaded} uploaded.");
            return report;
        }

        private static async Task<bool> IsOnlineAsync(Uri baseUri, CancellationToken token)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                using var response = await client.GetAsync(baseUri, token);
                return response.IsSuccessStatusCode;
            }
            catch (Exception) { return false; }
        }

        private static async Task<List<ServerEntry>?> GetFileListAsync(StrandWebsiteSession session, string folder, string username, CancellationToken token)
        {
            string resource = $"/Inspect/Sync/GetFileList?folderName={Uri.EscapeDataString(folder)}&userName={Uri.EscapeDataString(username)}";
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, StrandWebsiteSession.Resource(session.Base, resource));
                using var response = await session.SendAsync(request, TimeSpan.FromSeconds(100), token);
                string body = await response.Content.ReadAsStringAsync(token);
                if (!body.TrimStart().StartsWith("[", StringComparison.Ordinal))
                {
                    if (body.TrimStart().StartsWith("<", StringComparison.Ordinal)) session.Discard();
                    return null;
                }
                using var reader = new JsonTextReader(new StringReader(body)) { DateParseHandling = DateParseHandling.None };
                var array = JArray.Load(reader);
                var list = new List<ServerEntry>();
                foreach (var entry in array.OfType<JObject>())
                {
                    DateTime? modified = DateTime.TryParse((string?)entry["LastModDate"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
                    list.Add(new ServerEntry((string?)entry["SvrPath"] ?? "", (string?)entry["FileName"] ?? "",
                        entry["FileLength"]?.Type is JTokenType.Integer ? (long)entry["FileLength"]! : -1, modified));
                }
                return list;
            }
            catch (Exception) { return null; }
        }

        private static async Task<byte[]?> DownloadAsync(StrandWebsiteSession session, string path, CancellationToken token)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    StrandWebsiteSession.Resource(session.Base, "/Inspect/Sync/Download?path=" + Uri.EscapeDataString(path)));
                using var response = await session.SendAsync(request, TimeSpan.FromSeconds(100), token);
                if (!response.IsSuccessStatusCode) return null;
                byte[] body = await response.Content.ReadAsByteArrayAsync(token);
                // RED safety: only a non-empty body that parses as a JSON object replaces a local file.
                if (body.Length == 0) return null;
                InspectionFileReader.ParseDocument(body, out _);
                return body;
            }
            catch (Exception) { return null; }
        }

        private static async Task<WebsiteReply> UploadAsync(StrandWebsiteSession session, string folder, string username, string fileName, byte[] bytes, CancellationToken token)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, StrandWebsiteSession.Resource(session.Base, "/Inspect/Sync/Upload"))
                {
                    Content = StrandWebsiteUploader.BuildMultipart(new[] { ("inspectionFile", fileName, bytes) },
                        new[] { ("folderName", folder), ("userName", username) })
                };
                using var response = await session.SendAsync(request, StrandWebsiteUploader.UploadTimeout, token);
                return StrandWebsiteUploader.Classify(await response.Content.ReadAsStringAsync(token), session);
            }
            catch (Exception ex) when (StrandWebsiteSession.IsNotSent(ex)) { return new WebsiteReply(WebsiteOutcome.NotSent, null); }
            catch (Exception) { return new WebsiteReply(WebsiteOutcome.NoUsableAnswer, null); }
        }

        // B4: the earlier of RED's stored value and the LastSyncDate in {IF}\config.json; none when either is unset.
        private static DateTime? EffectiveLastSyncDate(SyncOptions o, string localRoot)
        {
            DateTime? red = ReadRedLastSyncDate(localRoot, o.InspectionsFolder);
            DateTime? inspect = (o.ReadInspectLastSyncDate ?? InspectLiveSettings.ReadLastSyncDate)(o.InspectionsFolder);
            if (!red.HasValue || !inspect.HasValue)
                return null;
            return red.Value <= inspect.Value ? red : inspect;
        }

        private static string StatePath(string localRoot) => Path.Combine(localRoot, "sync-state.json");

        private static Dictionary<string, DateTime> ReadState(string localRoot)
        {
            try
            {
                string path = StatePath(localRoot);
                return File.Exists(path)
                    ? JsonConvert.DeserializeObject<Dictionary<string, DateTime>>(File.ReadAllText(path)) ?? new()
                    : new();
            }
            catch (Exception) { return new(); }
        }

        public static DateTime? ReadRedLastSyncDate(string localRoot, string inspectionsFolder) =>
            ReadState(localRoot).TryGetValue(Path.GetFullPath(inspectionsFolder).TrimEnd('\\').ToUpperInvariant(), out var value) ? value : null;

        private static void WriteRedLastSyncDate(string localRoot, string inspectionsFolder, DateTime value)
        {
            var state = ReadState(localRoot);
            state[Path.GetFullPath(inspectionsFolder).TrimEnd('\\').ToUpperInvariant()] = value;
            Directory.CreateDirectory(localRoot);
            string path = StatePath(localRoot);
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(state, Formatting.Indented));
            File.Move(temp, path, overwrite: true);
        }

        private static string PendingRoot(string localRoot) => Path.Combine(localRoot, "sync-pending");

        // A verified copy of a sent Outbox file in RED's app data, for when the Archive cannot be written.
        private static bool KeepLocalCopy(string localRoot, string name, byte[] bytes)
        {
            try
            {
                string folder = Path.Combine(localRoot, "sync-sent-copies");
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, $"{DateTime.Now:yyyyMMdd-HHmmssfff}-{name}");
                File.WriteAllBytes(path, bytes);
                return File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        // B2: an Outbox file sent earlier but not removed is deleted at the start of the next Sync (when unchanged),
        // never sent again.
        private static void ProcessPendingOutboxDeletes(string inspectionsFolder, string localRoot, SyncReport report)
        {
            foreach (var message in SubmitService.ProcessPendingDeletes(PendingRoot(localRoot)))
                report.Problems.Add(message);
        }
    }
}
