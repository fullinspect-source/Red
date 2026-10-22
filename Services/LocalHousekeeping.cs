using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace InspectionEditor.Services
{
    /// <summary>
    /// Storage audit F4/F5 (R-storage-debris-audit, owner-approved 2026-10-22): one start-up cleanup of RED's own local
    /// safety copies and leftover temporary files, which nothing removed before. It runs at every start, also on a
    /// device with no Inspections folder; a dev build cleans its own app-data folder, and the shared %TEMP% only with
    /// RED_DEV_HOUSEKEEPING=1.
    /// - Recovery copies (Recovery\failed-save-*, replaced-by-overwrite-*, paused-edits-* and Recovery\job-files): 90
    ///   days, then the oldest first while they total more than 1 GB (never one from the last day). 90 days because they
    ///   can hold the only copy of unsaved edits.
    /// - Other safety copies and temporary files: 30 days (SyncBackups by the date in the folder name, MoveBackups,
    ///   SubmitBackups, sync-sent-copies once the Archive has the report, the staging folders' .tmp files, read-only plan
    ///   copies in %TEMP%\RED-JobFiles, Plan Check PDFs, old SaveStaging and red_errors.log, test sandboxes in %TEMP%).
    ///   Preview, print and e-mail files in %TEMP%\RED-Reports keep their existing 1-day rule, now also applied at start.
    /// - Small records: VerifiedSaves and framing-cache 90 days; INSPECT config.json copies, the newest 10.
    /// - The in-app updater's downloads (AppUpdateService.SweepTemp) and RED's backup photos (CameraRollCopy.Purge).
    /// Every deletion matches an exact folder and name pattern, skips links and anything another program has open, and
    /// is logged as counts and sizes only.
    /// </summary>
    public static class LocalHousekeeping
    {
        public const int RecoveryDays = 90;
        public const long RecoveryCapBytes = 1L * 1024 * 1024 * 1024;
        public static readonly TimeSpan RecoveryCapMinAge = TimeSpan.FromDays(1);
        public const int SafetyCopyDays = 30;
        public const int UnconfirmedSentCopyDays = 90;
        public const int RecordDays = 90;
        public const int ReportTempDays = 1;
        public const int InspectConfigCopiesKept = 10;

        public sealed class Locations
        {
            public string LocalAppData { get; init; } = AppIdentity.LocalAppDataPath;
            public string Temp { get; init; } = Path.GetTempPath();
            /// <summary>The profile's Inspections folder (sync-sent-copies, Archive retention); null when none.</summary>
            public string? InspectionsFolder { get; init; }
            /// <summary>False: the shared %TEMP% is left alone (a dev build without RED_DEV_HOUSEKEEPING=1).</summary>
            public bool IncludeTemp { get; init; } = true;
            /// <summary>RED's backup-photo folder; null = CameraRollCopy.TargetFolder().</summary>
            public string? CameraFolder { get; init; }
            public bool IncludeCamera { get; init; } = true;
            public bool IncludeUpdater { get; init; } = true;
            /// <summary>The 1 GB limit on recovery copies; a test passes a small one.</summary>
            public long RecoveryCap { get; init; } = RecoveryCapBytes;
        }

        public sealed class Result
        {
            public SortedDictionary<string, CleanupTally> Areas { get; } = new(StringComparer.Ordinal);
            public CleanupTally Area(string name) => Areas.TryGetValue(name, out var t) ? t : Areas[name] = new CleanupTally();
            public CleanupTally Total
            {
                get { var total = new CleanupTally(); foreach (var t in Areas.Values) total.Add(t); return total; }
            }
        }

        private static readonly Regex RecoveryCopy = Pattern(@"^(failed-save|replaced-by-overwrite|paused-edits)-\d{8}T\d{9}Z-[0-9a-f]{32}\.ins(\.tmp)?$");
        private static readonly Regex ReplacedJobFile = Pattern(@"^.+ \(replaced \d{8}-\d{6}\)\.pdf$");
        private static readonly Regex DateFolder = Pattern(@"^\d{4}-\d{2}-\d{2}$");
        private static readonly Regex MoveBackup = Pattern(@"^\d{8}-\d{9}-(MyList|Outbox|Archive|Review)-.+\.ins$");
        private static readonly Regex SentCopy = Pattern(@"^\d{8}-\d{9}-(?<name>.+\.ins)$");
        private static readonly Regex VerifiedSave = Pattern(@"^[0-9A-F]{64}\.json(\.[0-9a-f]{32}\.tmp)?$");
        private static readonly Regex FramingCache = Pattern(@"^[0-9a-f]{64}\.json(\.tmp)?$");
        private static readonly Regex InspectConfigCopy = Pattern(@"^config-\d{8}-\d{6}-\d{3}\.json$");
        private static readonly Regex StagingTemp = Pattern(@"^.+\.[0-9a-f]{32}\.tmp$");
        private static readonly Regex StateTemp = Pattern(@"^[A-Za-z0-9_\-]+\.json\.[0-9a-f]{32}\.tmp$");
        private static readonly Regex LegacySaveStaging = Pattern(@"\.red-(save-backup(-[0-9a-f]{32}(\.completed)?)?|[0-9a-f]{32}\.tmp)$");
        private static readonly Regex GuidFolder = Pattern(@"^[0-9a-f]{32}$");
        private static readonly Regex PlanCheckPdf = Pattern(@"^RED_PlanCheck_[0-9a-f]{32}\.pdf$");
        // Test-harness sandboxes from RED's own tests (audit S-28), by their exact names.
        private static readonly Regex HarnessSandbox = Pattern(
            @"^((red-save-loss-recovery|red-save-tests|red-atomic-retry|red-recovery-test|red-last-edit|red-attachment-tests|" +
            @"red-f\d{3}(-appdata|-corpus|-cache|-e2)?|red-checklist-visibility|red-hidden-photo|red-usertype|red-profile|" +
            @"red-data-harness|red-airflow-harness|red-f027-harness|redcamera-core-harness|red-storage-[a-z]+)-[0-9a-f]{32}|" +
            @"(RedUpdaterTests|het-test)-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$");

        private static Regex Pattern(string pattern) => new(pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>The start-up run for this device: RED's app data, %TEMP% (a dev build only with
        /// RED_DEV_HOUSEKEEPING=1), the backup photos and the updater's downloads. Never throws; writes one log line.</summary>
        public static Result RunAtStart(DateTime now, bool devBuild, bool devHousekeeping)
        {
            string? inspections = null;
            try { inspections = InspectorProfileService.Load()?.InspectionsFolder; }
            catch (Exception ex) { DiagnosticLogService.Info($"Local cleanup: no profile read ({ex.GetType().Name})."); }
            if (string.IsNullOrWhiteSpace(inspections) || !Directory.Exists(inspections)) inspections = null;
            var result = Run(now, new Locations
            {
                InspectionsFolder = inspections,
                IncludeTemp = !devBuild || devHousekeeping,
                IncludeUpdater = !devBuild || devHousekeeping
            });
            DiagnosticLogService.Info(Describe(result));
            return result;
        }

        public static Result Run(DateTime now, Locations where)
        {
            var result = new Result();
            string lad = where.LocalAppData;
            Step(() => Recovery(now, Path.Combine(lad, "Recovery"), where.RecoveryCap, result.Area("Recovery")));
            Step(() => DatedFolders(now, Path.Combine(lad, "SyncBackups"), result.Area("SyncBackups")));
            Step(() => Files(Path.Combine(lad, FolderMoveService.MovesBackupFolder), MoveBackup, now.AddDays(-SafetyCopyDays), result.Area("MoveBackups")));
            Step(() => SubmitService.PruneSubmitBackups(lad, now, result.Area("SubmitBackups")));
            Step(() => SentCopies(now, Path.Combine(lad, "sync-sent-copies"), where.InspectionsFolder, result.Area("sync-sent-copies")));
            Step(() => Files(Path.Combine(lad, "VerifiedSaves"), VerifiedSave, now.AddDays(-RecordDays), result.Area("records")));
            Step(() => Files(Path.Combine(lad, "framing-cache"), FramingCache, now.AddDays(-RecordDays), result.Area("records")));
            Step(() => KeepNewest(Path.Combine(lad, "Backups", "inspect-config"), InspectConfigCopy, InspectConfigCopiesKept, result.Area("records")));
            Step(() =>
            {
                var staging = result.Area("staging");
                foreach (string name in new[] { "outbox-staging", "move-staging", "sync-staging" })
                {
                    Files(Path.Combine(lad, name), StagingTemp, now.AddDays(-SafetyCopyDays), staging);
                    SafeDelete.EmptyFolder(new DirectoryInfo(Path.Combine(lad, name)));
                }
                Files(lad, StateTemp, now.AddDays(-SafetyCopyDays), staging);
                Files(Path.Combine(lad, "sync-pending"), StateTemp, now.AddDays(-SafetyCopyDays), staging);
            });
            Step(() => LegacyLeftovers(now, lad, result.Area("old leftovers")));
            if (where.IncludeTemp)
            {
                string temp = where.Temp;
                Step(() => result.Area("plan copies").Add(SweepPlanCopies(now, temp)));
                Step(() => Files(temp, PlanCheckPdf, now.AddDays(-SafetyCopyDays), result.Area("Plan Check")));
                Step(() => Folders(Path.Combine(temp, Reports.ReportService.TempFolderName), GuidFolder, now.AddDays(-ReportTempDays), result.Area("previews")));
                Step(() => Folders(temp, HarnessSandbox, now.AddDays(-SafetyCopyDays), result.Area("test sandboxes")));
            }
            if (where.IncludeUpdater)
                Step(() => result.Area("update downloads").Add(AppUpdateService.SweepTemp(now.ToUniversalTime(), null,
                    Path.Combine(where.Temp, "RedUpdate"), where.Temp)));
            if (where.IncludeCamera)
                Step(() => result.Area("backup photos").Add(Camera.CameraRollCopy.Purge(now,
                    InspectLiveSettings.ReadArchiveRetention(where.InspectionsFolder), where.CameraFolder)));
            return result;
        }

        public const string PlanCopiesFolderName = "RED-JobFiles";

        /// <summary>The read-only plan copies in %TEMP%\RED-JobFiles\{guid} more than 30 days old (their read-only flag
        /// is RED's own). Also run before each new copy is made.</summary>
        public static CleanupTally SweepPlanCopies(DateTime now, string? temp = null)
        {
            var tally = new CleanupTally();
            try { Folders(Path.Combine(temp ?? Path.GetTempPath(), PlanCopiesFolderName), GuidFolder, now.AddDays(-SafetyCopyDays), tally, clearReadOnly: true); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException) { }
            return tally;
        }

        /// <summary>RED's own log line: counts and sizes only, never a file name.</summary>
        public static string Describe(Result result)
        {
            var parts = result.Areas.Where(a => a.Value.Any).Select(a => $"{a.Key}: {a.Value}").ToList();
            return parts.Count == 0
                ? "Local cleanup: nothing to delete."
                : $"Local cleanup: {result.Total.Files} file(s), {result.Total.Bytes / 1048576.0:0.0} MB. " + string.Join("; ", parts) + ".";
        }

        // Each step on its own: one that fails never stops the others, and nothing reaches the caller.
        private static void Step(Action step)
        {
            try { step(); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException || ex is ArgumentException)
            {
                DiagnosticLogService.Info($"Local cleanup: a step was skipped ({ex.GetType().Name}).");
            }
        }

        private static DirectoryInfo? Folder(string path)
        {
            var dir = new DirectoryInfo(path);
            return dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) == 0 ? dir : null;
        }

        // Top-level files matching the pattern, last written before the cutoff.
        private static void Files(string folder, Regex pattern, DateTime cutoff, CleanupTally tally, bool clearReadOnly = false)
        {
            var dir = Folder(folder);
            if (dir == null) return;
            foreach (var file in dir.EnumerateFiles("*", SearchOption.TopDirectoryOnly).ToList())
                if (pattern.IsMatch(file.Name) && file.LastWriteTime < cutoff)
                    SafeDelete.File(file, tally, clearReadOnly);
        }

        // Top-level folders matching the pattern, whose own last-write time is before the cutoff, as whole trees.
        private static void Folders(string folder, Regex pattern, DateTime cutoff, CleanupTally tally, bool clearReadOnly = false)
        {
            var dir = Folder(folder);
            if (dir == null) return;
            foreach (var sub in dir.EnumerateDirectories("*", SearchOption.TopDirectoryOnly).ToList())
                if (pattern.IsMatch(sub.Name) && sub.LastWriteTime < cutoff)
                    SafeDelete.Tree(sub, tally, clearReadOnly);
        }

        private static void KeepNewest(string folder, Regex pattern, int keep, CleanupTally tally)
        {
            var dir = Folder(folder);
            if (dir == null) return;
            foreach (var file in dir.EnumerateFiles("*", SearchOption.TopDirectoryOnly).Where(f => pattern.IsMatch(f.Name))
                         .OrderByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase).Skip(keep).ToList())
                SafeDelete.File(file, tally);
        }

        private static void Recovery(DateTime now, string folder, long cap, CleanupTally tally)
        {
            var dir = Folder(folder);
            if (dir == null) return;
            var copies = dir.EnumerateFiles("*", SearchOption.TopDirectoryOnly).Where(f => RecoveryCopy.IsMatch(f.Name)).ToList();
            var jobFiles = Folder(Path.Combine(folder, "job-files"));
            if (jobFiles != null)
                copies.AddRange(jobFiles.EnumerateFiles("*", SearchOption.TopDirectoryOnly).Where(f => ReplacedJobFile.IsMatch(f.Name)));
            DateTime cutoff = now.AddDays(-RecoveryDays);
            var kept = new List<FileInfo>();
            foreach (var file in copies)
            {
                if (file.LastWriteTime < cutoff && SafeDelete.File(file, tally)) continue;
                if (file.Exists) kept.Add(file);
            }
            long total = kept.Sum(f => f.Length);
            DateTime youngest = now - RecoveryCapMinAge;
            foreach (var file in kept.OrderBy(f => f.LastWriteTime))
            {
                if (total <= cap || file.LastWriteTime >= youngest) break;
                long length = file.Length;
                if (SafeDelete.File(file, tally)) total -= length;
            }
        }

        // SyncBackups\{yyyy-MM-dd}: by the date in the name, because File.Copy keeps the old file times.
        private static void DatedFolders(DateTime now, string folder, CleanupTally tally)
        {
            var dir = Folder(folder);
            if (dir == null) return;
            DateTime cutoff = now.Date.AddDays(-SafetyCopyDays);
            foreach (var sub in dir.EnumerateDirectories("*", SearchOption.TopDirectoryOnly).ToList())
                if (DateFolder.IsMatch(sub.Name) &&
                    DateTime.TryParseExact(sub.Name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) &&
                    day < cutoff)
                    SafeDelete.Tree(sub, tally);
        }

        // A sent report kept because the Archive could not be written: 30 days once the Archive has it, else 90.
        private static void SentCopies(DateTime now, string folder, string? inspections, CleanupTally tally)
        {
            var dir = Folder(folder);
            if (dir == null) return;
            foreach (var file in dir.EnumerateFiles("*", SearchOption.TopDirectoryOnly).ToList())
            {
                var match = SentCopy.Match(file.Name);
                if (!match.Success) continue;
                bool archived = inspections != null && File.Exists(Path.Combine(inspections, "Archive", match.Groups["name"].Value));
                if (file.LastWriteTime < now.AddDays(-(archived ? SafetyCopyDays : UnconfirmedSentCopyDays)))
                    SafeDelete.File(file, tally);
            }
        }

        // Written by RED versions before 2.2 only: the old SaveStaging folder and red_errors.log.
        private static void LegacyLeftovers(DateTime now, string lad, CleanupTally tally)
        {
            DateTime cutoff = now.AddDays(-SafetyCopyDays);
            var staging = Folder(Path.Combine(lad, "SaveStaging"));
            if (staging != null)
            {
                foreach (var sub in staging.EnumerateDirectories("*", SearchOption.TopDirectoryOnly).ToList())
                {
                    if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    Files(sub.FullName, LegacySaveStaging, cutoff, tally);
                    SafeDelete.EmptyFolder(sub);
                }
                Files(staging.FullName, LegacySaveStaging, cutoff, tally);
                SafeDelete.EmptyFolder(staging);
            }
            var errors = new FileInfo(Path.Combine(lad, "red_errors.log"));
            if (errors.Exists && errors.LastWriteTime < cutoff)
                SafeDelete.File(errors, tally);
        }
    }
}
