using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using InspectionEditor.Models;
using Newtonsoft.Json.Linq;

namespace InspectionEditor.Services
{
    public sealed record JobFile(string FullPath, string Filename, string FileType, DateTime Created, DateTime Modified,
        string Version, bool Attached);

    public enum JobFileOpenAction
    {
        CreateReference,     // O2: no attachment with this Filename yet
        UseExisting,         // reference-only attachment, job file unchanged since it was copied
        RefreshReference,    // reference-only attachment, the job file changed since: take the newer copy
        AskMarkedUp,         // O3: marked up, and the marked-up copy is newer than the job file
        AskDropboxNewer      // O4: marked up, but the job file is newer
    }

    public sealed record JobFileOpenPlan(JobFileOpenAction Action, int? Index);

    /// <summary>
    /// F-016 Job Files: listing (J1-J2), opening a job file as a reference-only attachment (O1-O8), RequestFiles (F1) and
    /// Update List (F3). Shared job files are only ever read (C3): RED never creates, renames or writes in Jobs.
    /// </summary>
    public static class JobFilesService
    {
        public const string RequestedMessage = "Job files were requested from server. Any received files will be copied to your 'Jobs' folder for this job";
        public const string RequestFailedMessage = "Unable to connect to server to retrieve job files. Please contact IT Support if this persists";
        public const string NotConnectedMessage = "Unable to request this job's files.  It appears you are not connected to the Internet.";
        public const string SignInFirstMessage = "Sign in to Strand in Settings first, then tap Refresh again.";
        public const string ListUpdatedMessage = "Job files list was updated.";
        public const string NoUpdatesMessage = "No updates to the job files list at this current time. Please add a file manually in File Explorer or by the 'Refresh' button.";
        public const string PreInspectorBlockedMessage = "This functionality is not available in PreInspector mode.";
        public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(100);

        public static string MarkedUpQuestion(string file) =>
            $"It appears you have already marked up '{file}'.\r\nDo you want to attach the marked up file? or click No to start fresh.";

        public static string DropboxNewerQuestion(string file) =>
            $"It appears you have already marked up '{file}' but the Dropbox file is newer.\r\nDo you want to attach the marked up file? or click No to use the newer Dropbox file.";

        // U1: only the exact raw value "PreInspector" (F-003 kind Other) blocks Job Files; "Preinspector" does not.
        public static bool IsBlockedFor(string? rawUserType) => string.Equals(rawUserType, "PreInspector", StringComparison.Ordinal);

        public static string Job7(string? jobNumber) =>
            string.IsNullOrEmpty(jobNumber) ? "" : jobNumber.Length > 7 ? jobNumber.Substring(0, 7) : jobNumber;

        public static string JobFolder(string inspectionsFolder, string? jobNumber) =>
            Path.Combine(inspectionsFolder, "Jobs", Job7(jobNumber));

        // J1/J2: top-level *.pdf of Engineering then Inspections. A missing folder lists as empty; nothing is created.
        public static List<JobFile> List(string inspectionsFolder, string? jobNumber, IEnumerable<object>? attachments)
        {
            var result = new List<JobFile>();
            string job7 = Job7(jobNumber);
            if (job7.Length == 0 || string.IsNullOrWhiteSpace(inspectionsFolder)) return result;
            string jobFolder = JobFolder(inspectionsFolder, jobNumber);
            var attached = AttachedNames(attachments);
            foreach (string sub in new[] { "Engineering", "Inspections" })
            {
                string folder = Path.Combine(jobFolder, sub);
                string[] files;
                try { files = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.pdf", SearchOption.TopDirectoryOnly) : Array.Empty<string>(); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { files = Array.Empty<string>(); }
                foreach (string path in files.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    // GetFiles' "*.pdf" also matches longer extensions such as ".pdfx".
                    if (!path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) continue;
                    var info = new FileInfo(path);
                    string name = info.Name;
                    // J2: INSPECT tests the file's path for "Engineering"; RED tests the part below the job folder so an
                    // Inspections folder path that happens to contain the word cannot change the type.
                    string type = Path.Combine(sub, name).Contains("Engineering", StringComparison.Ordinal) ? "Engineering" : "Inspection";
                    result.Add(new JobFile(path, name, type, info.CreationTime, info.LastWriteTime, Version(job7, name), attached.Contains(name)));
                }
            }
            return result;
        }

        // J2: the digits after "R" per INSPECT's regex {job7}R(\d+).+, else "0".
        public static string Version(string job7, string fileName)
        {
            var match = Regex.Match(fileName, Regex.Escape(job7) + @"R(\d+).+", RegexOptions.CultureInvariant);
            return match.Success ? match.Groups[1].Value : "0";
        }

        // J2/R3: the Attached marker counts only attachments that are not reference-only.
        private static HashSet<string> AttachedNames(IEnumerable<object>? attachments) =>
            (attachments ?? Enumerable.Empty<object>()).OfType<JObject>()
                .Where(a => !IsReferenceOnly(a))
                .Select(a => a["Filename"]?.Type == JTokenType.String ? (string)a["Filename"]! : null)
                .Where(n => n != null).Select(n => n!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        public static bool IsReferenceOnly(JObject attachment) =>
            attachment["ReferenceOnly"]?.Type == JTokenType.Boolean && (bool)attachment["ReferenceOnly"]!;

        public static int? FindAttachment(IEnumerable<object>? attachments, string fileName)
        {
            var list = (attachments ?? Enumerable.Empty<object>()).ToList();
            for (int i = 0; i < list.Count; i++)
                if (list[i] is JObject a && a["Filename"]?.Type == JTokenType.String &&
                    string.Equals((string)a["Filename"]!, fileName, StringComparison.OrdinalIgnoreCase))
                    return i;
            return null;
        }

        public static DateTime? AttachmentTime(JObject attachment)
        {
            var token = attachment["LastWriteTime"];
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type == JTokenType.Date) return token.Value<DateTime>();
            return DateTimeOffset.TryParse((string?)token, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed) ? parsed.LocalDateTime : null;
        }

        /// <summary>
        /// O1-O4, O6. A marked-up attachment (ReferenceOnly false) asks O3 or O4 by comparing its LastWriteTime with the
        /// job file's modified time; without a LastWriteTime it asks O3. A reference-only attachment is reused, or refreshed
        /// when the job file changed after it was copied, without a question. Never a second attachment for one Filename.
        /// </summary>
        public static JobFileOpenPlan Plan(IEnumerable<object>? attachments, string fileName, DateTime jobFileModified)
        {
            var list = (attachments ?? Enumerable.Empty<object>()).ToList();
            int? index = FindAttachment(list, fileName);
            if (index == null) return new JobFileOpenPlan(JobFileOpenAction.CreateReference, null);
            var attachment = (JObject)list[index.Value];
            DateTime? copied = AttachmentTime(attachment);
            if (IsReferenceOnly(attachment))
                return new JobFileOpenPlan(copied.HasValue && jobFileModified > copied.Value
                    ? JobFileOpenAction.RefreshReference : JobFileOpenAction.UseExisting, index);
            return new JobFileOpenPlan(copied.HasValue && jobFileModified > copied.Value
                ? JobFileOpenAction.AskDropboxNewer : JobFileOpenAction.AskMarkedUp, index);
        }

        // O2 (O7, O8): the attachment shape RED writes for a job file. EditPath and InspectVersion are null by design.
        public static JObject ReferenceAttachment(string fileName, byte[] bytes, DateTime copiedAt) => new()
        {
            ["InspectVersion"] = null,
            ["Id"] = Guid.NewGuid().ToString(),
            ["EditPath"] = null,
            ["FileData"] = Convert.ToBase64String(bytes),
            ["FileType"] = "pdf",
            ["Filename"] = fileName,
            ["AnnotatedPages"] = new JArray(),
            ["IncludedPages"] = new JArray(),
            ["ReferenceOnly"] = true,
            ["LastWriteTime"] = HeaderRules.FormatDate(copiedAt),
            ["CanExcludePages"] = true
        };

        // O3/O4 "No" and the reference refresh: the attachment keeps its Id and other keys and takes the job file's bytes
        // again, ReferenceOnly true and no annotated pages.
        public static JObject Refreshed(JObject attachment, byte[] bytes, DateTime copiedAt)
        {
            var copy = (JObject)attachment.DeepClone();
            copy["FileData"] = Convert.ToBase64String(bytes);
            copy["ReferenceOnly"] = true;
            copy["AnnotatedPages"] = new JArray();
            copy["LastWriteTime"] = HeaderRules.FormatDate(copiedAt);
            if (copy["ReducedFileData"] != null) copy["ReducedFileData"] = null;
            return copy;
        }

        // F3: compares two listings by name, size-free identity (path, modified time).
        public static bool ListChanged(IReadOnlyList<JobFile> before, IReadOnlyList<JobFile> after) =>
            !before.Select(f => (f.FullPath.ToUpperInvariant(), f.Modified)).OrderBy(x => x.Item1)
                .SequenceEqual(after.Select(f => (f.FullPath.ToUpperInvariant(), f.Modified)).OrderBy(x => x.Item1));

        // F1: PUT {base}/Inspect/Jobs/RequestFiles?jobNumber={job7} on the website session. HTTP 200 = requested.
        public static async Task<string> RequestFilesAsync(StrandWebsiteSession session, string? jobNumber, CancellationToken token)
        {
            string job7 = Job7(jobNumber);
            if (job7.Length < 7) return RequestFailedMessage;
            try
            {
                await session.EnsureAsync(force: false, token);
            }
            catch (StrandAuthRequiredException) { return SignInFirstMessage; }
            catch (Exception ex) when (StrandWebsiteSession.IsNotSent(ex)) { return NotConnectedMessage; }
            catch (Exception) { return RequestFailedMessage; }
            try
            {
                string resource = "Inspect/Jobs/RequestFiles?jobNumber=" + Uri.EscapeDataString(job7);
                using var request = new HttpRequestMessage(HttpMethod.Put, StrandWebsiteSession.Resource(session.Base, resource))
                {
                    Content = new StringContent("")
                };
                using var response = await session.SendAsync(request, RequestTimeout, token);
                return (int)response.StatusCode == 200 ? RequestedMessage : RequestFailedMessage;
            }
            catch (Exception ex) when (StrandWebsiteSession.IsNotSent(ex)) { return NotConnectedMessage; }
            catch (Exception) { return RequestFailedMessage; }
        }
    }
}
