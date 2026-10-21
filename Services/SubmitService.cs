using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using InspectionEditor.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace InspectionEditor.Services
{
    public enum SubmitStage
    {
        Done, Refused, Invalid, OpenAttachment, ExternalChange, ArchiveFailed, OutboxFailed, SourceNotRemoved,
        WebsiteRejected, WebsiteNotSent, WebsiteNoAnswer, WebsiteSignInNeeded
    }

    public sealed class SubmitResult
    {
        public SubmitStage Stage { get; init; }
        public string Message { get; init; } = "";
        public CompletionResult? Problems { get; init; }
        public string? ArchivePath { get; init; }
        public string? OutboxPath { get; init; }
        // True once step 11 rewrote {SRC}: an editor that stays open must reload the report before editing again.
        public bool SourceRewritten { get; init; }
        public bool Succeeded => Stage is SubmitStage.Done or SubmitStage.SourceNotRemoved;
    }

    // F-019 L7 (INSPECT-format lines) and L8 (RED-only lines). Until F-019's writer exists both go to RED's own log.
    public interface ISubmitLog
    {
        void Inspect(string line);
        void Red(string line);
    }

    public sealed class DiagnosticSubmitLog : ISubmitLog
    {
        public static readonly DiagnosticSubmitLog Instance = new();
        public void Inspect(string line) => DiagnosticLogService.Info(line);
        public void Red(string line) => DiagnosticLogService.Info(line);
    }
    // F-019 L7: Submit's INSPECT-format lines go to the shared INSPECT-format log (F-015's writer), and to RED's own log.
    public sealed class InspectFormatSubmitLog : ISubmitLog
    {
        private readonly InspectLogWriter _writer;
        public static readonly InspectFormatSubmitLog Instance = new(InspectLogWriter.Default);
        public InspectFormatSubmitLog(InspectLogWriter writer) => _writer = writer;
        public void Inspect(string line)
        {
            try { _writer.Info(line); } catch (Exception ex) { DiagnosticLogService.Log("INSPECT-format log write failed", ex); }
            DiagnosticLogService.Info(line);
        }
        public void Red(string line) => DiagnosticLogService.Info(line);
    }

    // F-013 §3A: the website upload a Preinspector Submit makes (and §3B NormalSubmit). Implemented over F-001's website
    // session; tests use a loopback mock.
    public enum WebsiteOutcome { Accepted, Rejected, NotSent, NoUsableAnswer }

    public sealed record WebsiteReply(WebsiteOutcome Outcome, string? Message);

    public interface IStrandWebsite
    {
        bool HasCredentials { get; }
        Task<WebsiteReply> UploadInspectionAsync(string relativePath, string fileName, byte[] bytes, CancellationToken token);
    }

    public sealed class SubmitContext
    {
        public required InspectionFile Model { get; init; }
        public required SurgicalSaveService Saver { get; init; }
        public required string SourcePath { get; init; }         // {SRC}: the file the report was loaded from
        public required CompleteProfile Profile { get; init; }   // F-003 O9 guard result
        public string? RawUserType { get; init; }                // the stored UserType, for the exact IsPreinspection test
        public Func<bool>? SaveWithSweeps { get; init; }         // step 3: RED's Save command (V5c, V5d sweeps)
        public Func<CompletionResult>? Validate { get; init; }   // step 4: F-012
        public Func<bool>? HasOpenAttachment { get; init; }      // step 6
        public Func<int>? OpenAttachmentCount { get; init; }     // step 6 log line
        public Action? RefreshAttachments { get; init; }         // step 7
        public Func<IEnumerable<(string Filename, string? WorkingPath)>>? WorkingCopies { get; init; } // step 15
        public Action<IReadOnlyList<string>>? DeleteRemovedWorkingCopies { get; init; }               // step 16
        public string? LocalRoot { get; init; }                  // staging, backups, pending deletes (RED's app data)
        public ISubmitLog Log { get; init; } = DiagnosticSubmitLog.Instance;
        public IStrandWebsite? Website { get; init; }            // §3A, Preinspector only
        public Action<int>? Sleep { get; init; }                 // tests
    }

    // F-013 Submit (mirrors Inspection::Submit). Every Submit except a Preinspector's is purely local file work.
    public static class SubmitService
    {
        public const string OpenAttachmentMessage = "You have an open attachment. Please save and close it before continuing.";
        public const string ArchiveFailedPrefix = "Cannot submit inspection. Copy to Archive folder failed. ";
        public const string OutboxFailedPrefix = "Cannot submit inspection. Move to Outbox folder failed. ";
        public const string SourceNotRemovedMessage = "Submitted, but the My List copy could not be removed. RED will retry.";
        public const string SentToOutboxMessage = "Sent to Outbox. Strand usually picks it up within a few minutes.";
        public const string SentToOutboxNonDropBoxMessage = "Sent to Outbox. Press Sync in the Outbox to send it to Strand.";
        public const string PreinspectorSignInMessage = "Sign in to Strand first. As a Preinspector, Submit uploads the inspection to Strand.";
        public const string WebsiteRejectedPrefix = "Cannot submit inspection. Cannot submit to server. ";
        public const string WebsiteNotSentMessage = "Cannot submit inspection. RED could not reach Strand. The inspection is still in My List; submit it again when you are online.";
        public const string WebsiteNoAnswerMessage = "Cannot submit inspection. Strand's answer could not be read, so RED cannot tell whether it arrived. The inspection is still in My List. Check with Strand before you submit it again.";
        public const string SubmittedToStrandMessage = "Submitted to Strand.";
        public const string PreinspectorSourceNotMovedMessage = "Submitted to Strand, but the My List copy could not be moved to Archive. RED will retry.";
        public const string ChangedAfterSubmitMessage = "The My List copy of {0} changed after it was submitted. RED left it in place.";
        public const string PreinspectorArchiveFailedMessage = "Submitted to Strand. Do not submit it again. RED could not copy it to the Archive, so the My List copy was kept.";
        private const string SubmitBackupPattern = @"\A\d{8}-\d{9}-.+\z";

        // A report name RED may write into Archive or Outbox: one plain file name, no folders, no "..".
        public static bool IsSafeReportName(string? number) =>
            !string.IsNullOrWhiteSpace(number) && number == number.Trim() && number.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
            !number.Contains("..", StringComparison.Ordinal) && number.Length <= 200;
        // F-018 (owner 2026-10-20): Review is the owner's holding folder; RED never submits, moves or rewrites a Review file.
        public const string ReviewNotSubmittedMessage = "Review files are not submitted from RED.";
        public const string SubmitBackupsFolder = "SubmitBackups";
        public const string PendingDeletesFile = "submit-pending-deletes.json";

        public static string OtherKindNotice(string value) =>
            $"INSPECT user type '{value}' is not one INSPECT offers. RED submits through the Outbox, as INSPECT does.";

        public static async Task<SubmitResult> SubmitAsync(SubmitContext ctx, CancellationToken token = default)
        {
            var model = ctx.Model;
            var saver = ctx.Saver;
            string localRoot = ctx.LocalRoot ?? AppIdentity.LocalAppDataPath;
            string src = Path.GetFullPath(ctx.SourcePath);
            string sourceFolder = Path.GetDirectoryName(src)!;
            string inspections = Path.GetDirectoryName(sourceFolder)!;
            var kind = ctx.Profile.Kind;
            bool preinspector = kind == InspectUserKind.Preinspector;

            // F-018 (owner 2026-10-20): never a Review file, before anything is logged, stamped or saved.
            if (string.Equals(Path.GetFileName(sourceFolder), "Review", StringComparison.OrdinalIgnoreCase))
                return Refused(ReviewNotSubmittedMessage);

            // P5: a Preinspector needs Strand credentials before anything is touched.
            if (preinspector && (ctx.Website == null || !ctx.Website.HasCredentials))
                return new SubmitResult { Stage = SubmitStage.WebsiteSignInNeeded, Message = PreinspectorSignInMessage };

            // 1-2: log and stamp the submitter.
            string number = HeaderRules.Text(saver.GetHeaderValue("InspectionNumber")) ?? "";
            ctx.Log.Inspect($"Submitting inspection {number}");
            saver.SetHeaderValue("SubmittorUserName", new JValue(ctx.Profile.Username), allowAdd: true);

            // 3: save before the completion check, as INSPECT (a refused submit still re-saves SubmittorUserName).
            try
            {
                if (ctx.SaveWithSweeps != null) { if (!ctx.SaveWithSweeps()) return Refused("The report could not be saved."); }
                else saver.Save(model);
            }
            catch (InspectionChangedOnDiskException ex)
            {
                return new SubmitResult { Stage = SubmitStage.ExternalChange, Message = ex.Message };
            }

            // 4: completion check.
            var problems = ctx.Validate?.Invoke() ?? InspectionValidator.Validate(model, saver.GetHeaderValue);
            if (!problems.IsValid)
                return new SubmitResult { Stage = SubmitStage.Invalid, Message = InspectionValidator.SubmitRefused, Problems = problems };

            // 5: blank duplicate rows.
            RemoveBlankItems(model);

            // 6: open-attachment guard (no error, the editor stays open). {n} counts RED working copies on this device.
            ctx.Log.Inspect("Checking for open attachments...");
            int workingCopies = ctx.WorkingCopies?.Invoke().Count(w => !string.IsNullOrEmpty(w.WorkingPath)) ?? 0;
            ctx.Log.Inspect($"Found {workingCopies} attachments in edit path.");
            bool open = ctx.HasOpenAttachment?.Invoke() == true;
            int openCount = open ? Math.Max(1, ctx.OpenAttachmentCount?.Invoke() ?? 1) : 0;
            ctx.Log.Inspect($"Found {openCount} open attachments.");
            if (open)
                return new SubmitResult { Stage = SubmitStage.OpenAttachment, Message = OpenAttachmentMessage };
            ctx.Log.Inspect("   No open attachments found.");

            // 7: refresh attachments (working copies, orientation forms).
            ctx.Log.Inspect("Updating attachments from edit path.");
            ctx.RefreshAttachments?.Invoke();
            ctx.Log.Inspect("Completed updating attachments from edit path.");

            // 8: drop reference-only attachments.
            var removedReference = DropReferenceOnlyAttachments(model, saver);
            if (removedReference.Count > 0)
                ctx.Log.Inspect($"   Removing {removedReference.Count} reference only attachments from inspection.");

            // 9: header finalisation.
            saver.SetHeaderValue("IsTransfer", new JValue(false), allowAdd: true);
            saver.SetHeaderValue("IsPreinspection", new JValue(string.Equals(ctx.RawUserType, "Preinspector", StringComparison.Ordinal)), allowAdd: true);
            saver.SetHeaderValue("SuperintendentName", SuperintendentValue(model), allowAdd: true);
            if (string.IsNullOrWhiteSpace(number))
            {
                // O5: build a blank InspectionNumber first.
                number = HeaderRules.BuildInspectionNumber(HeaderRules.Text(saver.GetHeaderValue("JobNumber")), HeaderRules.Text(saver.GetHeaderValue("InspectionCode")),
                    HeaderRules.Int(saver.GetHeaderValue("Attempt")), HeaderRules.Text(saver.GetHeaderValue("InspectorName")),
                    HeaderRules.Bool(saver.GetHeaderValue("IsPreinspection")), InspectorProfileService.Initials(ctx.Profile.InspectorName));
                saver.SetHeaderValue("InspectionNumber", new JValue(number), allowAdd: true);
                model.InspectionNumber = number;
            }

            // RED safety: the number becomes a file name in Archive and Outbox; never a path.
            string archiveFailed = ArchiveFailedPrefix;
            string outboxFailed = OutboxFailedPrefix;
            if (!IsSafeReportName(number))
                return new SubmitResult { Stage = SubmitStage.ArchiveFailed, Message = archiveFailed + "The inspection number cannot be used as a file name.", SourceRewritten = true };

            // 10-11: Mode A save (recomputes ResultSortOrder, A5), project to INSPECT's dialect, write {SRC} with the
            // external-change check, re-read and compare.
            byte[] bytes;
            try
            {
                ctx.Log.Inspect("   Saving inspection before submit.");
                saver.Save(model);
                foreach (string line in InspectFormatLog.SavingLines(saver.GetHeaderValue, model, src, written: true)) // F-019 L7 save lines
                    ctx.Log.Inspect(line);
                bytes = saver.ProjectCurrent(model).ToUtf8Bytes(); // carries a pending F-004 V1 NA→NI rewrite
                saver.WriteFinalBytes(bytes);
            }
            catch (InspectionChangedOnDiskException ex)
            {
                return new SubmitResult { Stage = SubmitStage.ExternalChange, Message = ex.Message };
            }
            if (!ReadBack(src).SequenceEqual(bytes))
                return new SubmitResult { Stage = SubmitStage.ArchiveFailed, Message = archiveFailed + "The final report could not be verified.", SourceRewritten = true };

            string fileName = number + ".ins";
            // 12: Archive copy (create the folder; replace; local backup of the old copy; atomic; verified).
            string archivePath = Path.Combine(inspections, "Archive", fileName);
            try
            {
                archivePath = Path.Combine(InspectFolders.EnsureForWrite(inspections, InspectFolder.Archive), fileName);
                WriteReplacing(archivePath, bytes, Path.Combine(localRoot, SubmitBackupsFolder));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return new SubmitResult { Stage = SubmitStage.ArchiveFailed, Message = archiveFailed + ex.Message, SourceRewritten = true };
            }
            ctx.Log.Inspect($"Copied inspection {number} to {archivePath}");

            if (preinspector)
                return await PreinspectorAsync(ctx, src, archivePath, fileName, bytes, number, inspections, removedReference, localRoot, token);

            // 13: Outbox placement, atomic.
            string outboxPath = Path.Combine(inspections, "Outbox", fileName);
            try
            {
                outboxPath = Path.Combine(InspectFolders.EnsureForWrite(inspections, InspectFolder.Outbox), fileName);
                // Step 13: the cross-volume fallback stages in {IF}\MyList.
                string fallbackFolder = Directory.Exists(InspectFolders.PathOf(inspections, InspectFolder.MyList)) ? InspectFolders.PathOf(inspections, InspectFolder.MyList) : sourceFolder;
                PlaceAtomically(outboxPath, bytes, Path.Combine(localRoot, "outbox-staging"), fallbackFolder, fileName);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return new SubmitResult { Stage = SubmitStage.OutboxFailed, Message = outboxFailed + ex.Message, ArchivePath = archivePath, SourceRewritten = true };
            }
            ctx.Log.Inspect($"Copied inspection {number} to {outboxPath}");

            // 14: remove {SRC} (unless it is the Outbox file itself, which cannot happen from My List).
            bool removed = RemoveSource(src, bytes, localRoot, ctx.Sleep);
            if (removed) ctx.Log.Inspect($"Removed inspection {number} from {src}");

            // 15-16: attachment copies to the job folder; RED's own leftovers.
            CopyAttachmentsToJob(ctx, inspections, removedReference);
            if (removedReference.Count > 0)
                ctx.Log.Inspect($"   Clearing {removedReference.Count} reference only attachments from temp folder.");
            ctx.DeleteRemovedWorkingCopies?.Invoke(removedReference);
            DeleteBackupSidecars(sourceFolder, Path.GetFileName(src));

            // 17.
            string done = kind == InspectUserKind.NonDropBox ? SentToOutboxNonDropBoxMessage : SentToOutboxMessage;
            return new SubmitResult
            {
                Stage = removed ? SubmitStage.Done : SubmitStage.SourceNotRemoved,
                Message = removed ? done : SourceNotRemovedMessage,
                ArchivePath = archivePath,
                OutboxPath = outboxPath,
                SourceRewritten = true
            };
        }

        private static SubmitResult Refused(string message) => new() { Stage = SubmitStage.Refused, Message = message };

        // §3A: after the Archive copy, upload to the website instead of the Outbox.
        private static async Task<SubmitResult> PreinspectorAsync(SubmitContext ctx, string src, string archivePath, string fileName,
            byte[] bytes, string number, string inspections, IReadOnlyList<string> removedReference, string localRoot, CancellationToken token)
        {
            ctx.Log.Inspect($"Submitting inspection {number} to /Inspect/Inspection/Submit");
            WebsiteReply reply;
            try { reply = await ctx.Website!.UploadInspectionAsync("/Inspect/Inspection/Submit", Path.GetFileName(src), bytes, token); }
            catch (Exception) { reply = new WebsiteReply(WebsiteOutcome.NoUsableAnswer, null); }
            switch (reply.Outcome)
            {
                case WebsiteOutcome.Accepted:
                    ctx.Log.Inspect("   submit successful");
                    // Strand has it now: an Archive problem must never read as "not submitted" (A5, C9: no second POST).
                    try
                    {
                        if (!File.Exists(archivePath) || !ReadBack(archivePath).SequenceEqual(bytes))
                            WriteReplacing(archivePath, bytes, Path.Combine(localRoot, SubmitBackupsFolder));
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        ctx.Log.Red($"Archive copy after an accepted upload failed: {ex.GetType().Name}");
                        return new SubmitResult
                        {
                            Stage = SubmitStage.SourceNotRemoved, Message = PreinspectorArchiveFailedMessage,
                            ArchivePath = archivePath, SourceRewritten = true
                        };
                    }
                    bool removed = RemoveSource(src, bytes, localRoot, ctx.Sleep);
                    CopyAttachmentsToJob(ctx, inspections, removedReference);
                    ctx.DeleteRemovedWorkingCopies?.Invoke(removedReference);
                    DeleteBackupSidecars(Path.GetDirectoryName(src)!, Path.GetFileName(src));
                    return new SubmitResult
                    {
                        Stage = removed ? SubmitStage.Done : SubmitStage.SourceNotRemoved,
                        Message = removed ? SubmittedToStrandMessage : PreinspectorSourceNotMovedMessage,
                        ArchivePath = archivePath, SourceRewritten = true
                    };
                case WebsiteOutcome.Rejected:
                    ctx.Log.Inspect("   submit failed");
                    return new SubmitResult { Stage = SubmitStage.WebsiteRejected, Message = WebsiteRejectedPrefix + (reply.Message ?? ""), ArchivePath = archivePath, SourceRewritten = true };
                case WebsiteOutcome.NotSent:
                    ctx.Log.Inspect("   submit failed");
                    return new SubmitResult { Stage = SubmitStage.WebsiteNotSent, Message = WebsiteNotSentMessage, ArchivePath = archivePath, SourceRewritten = true };
                default:
                    ctx.Log.Inspect("   submit failed");
                    return new SubmitResult { Stage = SubmitStage.WebsiteNoAnswer, Message = WebsiteNoAnswerMessage, ArchivePath = archivePath, SourceRewritten = true };
            }
        }

        // Step 5 (Inspection::RemoveBlankItems): last to first; remove when no pictures, blank Comments, Value exactly
        // "Pass" or "Discussed", and more than one item of the section currently shares its ItemId.
        public static int RemoveBlankItems(InspectionFile model)
        {
            int removed = 0;
            foreach (var section in model.Sections ?? new List<Section>())
            {
                var items = section.Items;
                for (int i = items.Count - 1; i >= 0; i--)
                {
                    var item = items[i];
                    if (item == null) continue;
                    string? value = item.Value is JValue j ? j.Value?.ToString() : item.Value as string;
                    bool blank = (item.Pictures?.Count ?? 0) == 0 && string.IsNullOrWhiteSpace(item.Comments) &&
                                 (string.Equals(value, "Pass", StringComparison.Ordinal) || string.Equals(value, "Discussed", StringComparison.Ordinal));
                    if (blank && items.Count(other => other != null && other.ItemId == item.ItemId) > 1)
                    {
                        items.RemoveAt(i);
                        removed++;
                    }
                }
            }
            return removed;
        }

        // Step 8: remove ReferenceOnly attachments through F-002's attachment-collection edit. Returns their file names.
        public static List<string> DropReferenceOnlyAttachments(InspectionFile model, SurgicalSaveService saver)
        {
            var removed = new List<string>();
            if (model.Attachments == null || model.Attachments.Count == 0)
                return removed;
            var kept = new List<object>();
            foreach (var attachment in model.Attachments)
            {
                var json = attachment as JObject ?? JObject.FromObject(attachment);
                if (json["ReferenceOnly"]?.Type == JTokenType.Boolean && (bool)json["ReferenceOnly"]!)
                    removed.Add((string?)json["Filename"] ?? "");
                else
                    kept.Add(attachment);
            }
            if (removed.Count == 0)
                return removed;
            var expected = saver.GetHeaderValue("Attachments") as JArray ?? new JArray();
            model.Attachments = kept;
            model.AttachmentEdit = new AttachmentCollectionEdit { Expected = expected, Replacement = JArray.FromObject(kept) };
            return removed;
        }

        // Step 9: the Value of the first item whose Name upper-cased is SUPERINTENDENT, or null when missing or blank.
        public static JToken SuperintendentValue(InspectionFile model)
        {
            var item = (model.Sections ?? new List<Section>()).SelectMany(s => s.Items)
                .FirstOrDefault(i => i != null && string.Equals(i.Name?.ToUpperInvariant(), "SUPERINTENDENT", StringComparison.Ordinal));
            string? value = item?.Value?.ToString();
            return string.IsNullOrWhiteSpace(value) ? JValue.CreateNull() : new JValue(value);
        }

        // Step 15 name: stem without a trailing " - insp N-XX", then " - insp {Attempt}-{INITIALS}", then the extension.
        public static string StampedAttachmentName(string fileName, int attempt, string initials)
        {
            string stem = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);
            stem = Regex.Replace(stem, @"\s\-\sinsp\s\d*(\-\w*)?$", "");
            return $"{stem} - insp {attempt.ToString(CultureInfo.InvariantCulture)}-{initials}{ext}";
        }

        private static void CopyAttachmentsToJob(SubmitContext ctx, string inspections, IReadOnlyList<string> removedReference)
        {
            if (ctx.WorkingCopies == null)
                return;
            string job = HeaderRules.Text(ctx.Saver.GetHeaderValue("JobNumber")) ?? "";
            int attempt = HeaderRules.Int(ctx.Saver.GetHeaderValue("Attempt"));
            string initials = InspectorProfileService.Initials(ctx.Profile.InspectorName);
            string engineering = Path.Combine(inspections, "Jobs", job, "Engineering");
            if (job.Length == 0 || job.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return;
            bool any = false;
            foreach (var (filename, workingPath) in ctx.WorkingCopies())
            {
                if (removedReference.Contains(filename) || string.IsNullOrEmpty(workingPath) || !File.Exists(workingPath))
                    continue; // no working copy on this device: skipped, as INSPECT
                if (!any && Directory.Exists(engineering))
                    ctx.Log.Inspect($"   Copying {ctx.WorkingCopies().Count(w => !removedReference.Contains(w.Filename) && !string.IsNullOrEmpty(w.WorkingPath) && File.Exists(w.WorkingPath))} attachments to Inspector folder.");
                any = true;
                if (!Directory.Exists(engineering))
                    break;
                try
                {
                    string target = Path.Combine(engineering, StampedAttachmentName(filename, attempt, initials));
                    if (!File.Exists(target))
                        File.Copy(workingPath, target, overwrite: false); // never overwrite, never create folders
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    ctx.Log.Red($"Attachment copy to the job folder failed: {ex.GetType().Name}");
                }
            }
            if (!Directory.Exists(engineering))
                ctx.Log.Red($"Engineering folder missing for {job}; attachment copy skipped (as INSPECT)"); // A-013-25
            _ = any;
        }

        // Step 12: write bytes over (or as) a file, atomically, keeping a local backup of the replaced bytes (newest 20).
        public static void WriteReplacing(string path, byte[] bytes, string backupFolder)
        {
            byte[]? existing = File.Exists(path) ? ReadBack(path) : null;
            if (existing != null)
            {
                Directory.CreateDirectory(backupFolder);
                string backup = Path.Combine(backupFolder, $"{DateTime.Now:yyyyMMdd-HHmmssfff}-{Path.GetFileName(path)}");
                File.WriteAllBytes(backup, existing);
                // Only this writer's own backups count towards the 20 (step-16 sidecars and Sync backups share the folder).
                foreach (var old in new DirectoryInfo(backupFolder).GetFiles()
                             .Where(f => Regex.IsMatch(f.Name, SubmitBackupPattern) && f.Name.EndsWith("-" + Path.GetFileName(path), StringComparison.OrdinalIgnoreCase))
                             .OrderByDescending(f => f.CreationTimeUtc).ThenByDescending(f => f.Name).Skip(20))
                {
                    try { old.Delete(); } catch (IOException) { }
                }
            }
            AtomicInspectionWriter.Write(path, new UTF8Encoding(false).GetString(bytes), existing);
            if (!ReadBack(path).SequenceEqual(bytes))
                throw new IOException("The copy could not be verified.");
        }

        // Step 13: staging file on the same volume outside the Dropbox tree, then one replacing rename; the staging file is
        // deleted on any failure. Falls back to {IF}\MyList\{num}.ins.red-outbox.tmp only when the volumes differ.
        public static void PlaceAtomically(string target, byte[] bytes, string stagingFolder, string sourceFolder, string fileName)
        {
            string staging;
            if (string.Equals(Path.GetPathRoot(Path.GetFullPath(stagingFolder)), Path.GetPathRoot(Path.GetFullPath(target)), StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(stagingFolder);
                staging = Path.Combine(stagingFolder, fileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
            }
            else
            {
                staging = Path.Combine(sourceFolder, fileName + ".red-outbox.tmp");
            }
            try
            {
                using (var stream = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(staging, target, overwrite: true); // MoveFileEx(MOVEFILE_REPLACE_EXISTING), same volume
                if (!ReadBack(target).SequenceEqual(bytes))
                    throw new IOException("The Outbox copy could not be verified.");
            }
            finally
            {
                try { if (File.Exists(staging)) File.Delete(staging); } catch (IOException) { }
            }
        }

        // Step 14: retry like INSPECT (10 x 10 ms), then 3 more over 3 s; then a pending delete conditional on the SHA-256.
        public static bool RemoveSource(string src, byte[] bytes, string localRoot, Action<int>? sleep = null)
        {
            sleep ??= Thread.Sleep;
            for (int attempt = 0; attempt < 13; attempt++)
            {
                try
                {
                    if (!File.Exists(src)) return true;
                    // Delete only the bytes that were submitted: a copy changed meanwhile stays (and is reported).
                    if (!ReadBack(src).SequenceEqual(bytes)) return false;
                    File.Delete(src);
                    return true;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    sleep(attempt < 10 ? 10 : 1000);
                }
            }
            AddPendingDelete(localRoot, src, bytes);
            return false;
        }

        private sealed class PendingDelete
        {
            public string Path { get; set; } = "";
            public string Sha256 { get; set; } = "";
            public DateTime SubmittedAt { get; set; }
        }

        private static string PendingPath(string localRoot) => Path.Combine(localRoot, PendingDeletesFile);

        private static List<PendingDelete> ReadPending(string localRoot)
        {
            try
            {
                string path = PendingPath(localRoot);
                return File.Exists(path) ? JsonConvert.DeserializeObject<List<PendingDelete>>(File.ReadAllText(path)) ?? new() : new();
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException) { return new(); }
        }

        private static void WritePending(string localRoot, List<PendingDelete> pending)
        {
            Directory.CreateDirectory(localRoot);
            string path = PendingPath(localRoot);
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(pending, Formatting.Indented));
            File.Move(temp, path, overwrite: true);
        }

        private static void AddPendingDelete(string localRoot, string src, byte[] bytes)
        {
            var pending = ReadPending(localRoot);
            pending.RemoveAll(p => string.Equals(p.Path, src, StringComparison.OrdinalIgnoreCase));
            pending.Add(new PendingDelete { Path = src, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), SubmittedAt = DateTime.Now });
            WritePending(localRoot, pending);
        }

        // A path RED writes a report to again (Move back to My List, a Sync download) is no longer a pending delete:
        // the new file must never be taken for the submitted one at the next launch.
        public static void ForgetPendingDelete(string path, string? localRoot = null)
        {
            localRoot ??= AppIdentity.LocalAppDataPath;
            var pending = ReadPending(localRoot);
            if (pending.RemoveAll(p => string.Equals(Path.GetFullPath(p.Path), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) > 0)
                WritePending(localRoot, pending);
        }
        public static IReadOnlyList<string> PendingPaths(string? localRoot = null) =>
            ReadPending(localRoot ?? AppIdentity.LocalAppDataPath).Select(p => p.Path).ToList();

        // At launch: delete a pending source only when its bytes still hash to what was submitted. Returns the messages
        // for files left in place.
        public static List<string> ProcessPendingDeletes(string? localRoot = null)
        {
            localRoot ??= AppIdentity.LocalAppDataPath;
            var messages = new List<string>();
            var pending = ReadPending(localRoot);
            if (pending.Count == 0)
                return messages;
            var still = new List<PendingDelete>();
            foreach (var entry in pending)
            {
                try
                {
                    if (!File.Exists(entry.Path)) continue;
                    // F-018 (owner 2026-10-20): a Review copy left by an older RED's Approve is never deleted; the entry goes.
                    if (string.Equals(Path.GetFileName(Path.GetDirectoryName(entry.Path)), "Review", StringComparison.OrdinalIgnoreCase))
                    {
                        DiagnosticLogService.Info("Submit: a pending delete of a Review file was dropped; RED never deletes Review files.");
                        continue;
                    }
                    string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(entry.Path)));
                    if (!string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        messages.Add(string.Format(CultureInfo.InvariantCulture, ChangedAfterSubmitMessage, Path.GetFileNameWithoutExtension(entry.Path)));
                        continue; // never delete on the name alone
                    }
                    File.Delete(entry.Path);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    still.Add(entry); // still locked: try again next launch
                }
            }
            WritePending(localRoot, still);
            return messages;
        }

        // Step 16: RED's own backup sidecars of this report (its earlier versions from editing) are deleted from My List.
        // Owner 2026-10-21 (tablet test of 2.3.5): simply deleted, no local copy: the submitted report is already verified
        // byte for byte in Archive and the Outbox, and a move out of the shared Dropbox folder made Dropbox ask "Remove ...
        // from everyone's Dropbox?" (a delete does not).
        private static void DeleteBackupSidecars(string sourceFolder, string sourceName)
        {
            try
            {
                foreach (var sidecar in Directory.EnumerateFiles(sourceFolder, sourceName + ".red-save-backup-*.completed"))
                {
                    try { File.Delete(sidecar); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        // Owner 2026-10-21: RED's local SubmitBackups folder (outside Dropbox) never grows without limit: at each start the
        // files in it last changed more than 30 days ago are deleted (top level only; links and folders are left alone).
        // This also clears the editing copies that RED 2.3.x moved there. Returns how many were deleted.
        public const int SubmitBackupsRetentionDays = 30;

        public static int PruneSubmitBackups(string? localRoot, DateTime now)
        {
            string folder = Path.Combine(localRoot ?? AppIdentity.LocalAppDataPath, SubmitBackupsFolder);
            int deleted = 0;
            try
            {
                if (!Directory.Exists(folder)) return 0;
                DateTime cutoff = now.AddDays(-SubmitBackupsRetentionDays);
                foreach (var file in new DirectoryInfo(folder).GetFiles())
                {
                    if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.LastWriteTime >= cutoff) continue;
                    try { file.Delete(); deleted++; }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            return deleted;
        }

        private static byte[] ReadBack(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }
    }
}
