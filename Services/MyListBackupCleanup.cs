using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace InspectionEditor.Services
{
    public sealed class MyListBackupCleanupResult
    {
        public int Days { get; init; }
        public DateTime Cutoff { get; init; }
        public int Candidates { get; set; }  // orphaned and on or before the cutoff date
        public int Taken { get; set; }       // this run's share (at most MaxFilesPerRun)
        public int KeptLive { get; set; }    // backups of reports still in My List: never touched
        public int KeptRecent { get; set; }  // orphaned, but changed after the cutoff date
        public CleanupTally Removed { get; } = new();
        public bool MissingFolder { get; set; }
        public bool Declined { get; set; }   // My List is a link, or could not be listed
    }

    /// <summary>
    /// Storage audit F2 (S-02), owner-approved 2026-10-22. Every save keeps up to three full earlier versions of a My
    /// List report beside it ({report}.ins.red-save-backup-{32 hex}.completed, AtomicInspectionWriter). Submit deletes
    /// them, but a report that leaves My List another way (Remove, a replace by Add or Buyer Walk, INSPECT or another
    /// device) left them behind in the shared Dropbox folder for good. This deletes such a backup when its report is no
    /// longer in My List and the backup was last changed on or before the Archive cutoff: INSPECT's Archive retention
    /// + 1 day, the same rule as the Archive cleanup (30 days when the retention is 0 or less). Only the top level of My
    /// List and only that exact name; a backup whose report is still in My List is never touched, nor anything in
    /// Review, Archive, Outbox or MyList\.red-save-staging. At most 200 per run, oldest first, like the Archive cleanup.
    /// </summary>
    public static class MyListBackupCleanup
    {
        public const int MaxFilesPerRun = ArchiveRetentionService.MaxFilesPerRun;
        public const int FallbackDays = 30;

        private static readonly Regex BackupName = new(@"^(?<report>.+\.ins)\.red-save-backup-[0-9a-f]{32}\.completed$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>The same gates as the Archive cleanup: an installed RED (a dev build only with
        /// RED_DEV_HOUSEKEEPING=1), the "clean the Archive" switch on, and RED's housekeeping.json readable.</summary>
        public static bool Allowed(HousekeepingSettings settings, bool archiveCleanupAllowed) =>
            archiveCleanupAllowed && settings.CleanArchive && !settings.ReadFailed;

        public static DateTime CutoffFor(DateTime now, int days) =>
            days > 0 ? InspectLiveSettings.ArchiveCutoff(now, days) : now.AddDays(-FallbackDays);

        /// <summary>The report a backup belongs to (file name only), or null when the name is not RED's backup name.</summary>
        public static string? ReportOf(string backupFileName)
        {
            var match = BackupName.Match(backupFileName);
            return match.Success ? match.Groups["report"].Value : null;
        }

        // dryRun: counts only, deletes nothing.
        public static MyListBackupCleanupResult Run(string inspectionsFolder, DateTime now, int? days = null, bool dryRun = false)
        {
            int n = days ?? InspectLiveSettings.ReadArchiveRetention(inspectionsFolder);
            var result = new MyListBackupCleanupResult { Days = n, Cutoff = CutoffFor(now, n) };
            try
            {
                var myList = new DirectoryInfo(InspectFolders.PathOf(inspectionsFolder, InspectFolder.MyList));
                if (!myList.Exists) { result.MissingFolder = true; return result; }
                if ((myList.Attributes & FileAttributes.ReparsePoint) != 0) { result.Declined = true; return result; }

                var candidates = new List<FileInfo>();
                foreach (var file in myList.EnumerateFiles("*.red-save-backup-*.completed", SearchOption.TopDirectoryOnly))
                {
                    string? report = ReportOf(file.Name);
                    if (report == null) continue;
                    if (File.Exists(Path.Combine(myList.FullName, report))) { result.KeptLive++; continue; }
                    if (file.LastWriteTime.Date > result.Cutoff.Date) { result.KeptRecent++; continue; }
                    candidates.Add(file);
                }
                result.Candidates = candidates.Count;
                var taken = candidates.OrderBy(f => f.LastWriteTimeUtc).Take(MaxFilesPerRun).ToList();
                result.Taken = taken.Count;
                if (dryRun) return result;
                foreach (var file in taken)
                {
                    // Checked again just before each delete: a report that came back (Dropbox, INSPECT) keeps its backups.
                    if (File.Exists(Path.Combine(myList.FullName, ReportOf(file.Name)!))) { result.KeptLive++; continue; }
                    SafeDelete.File(file, result.Removed);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                result.Declined = true;
            }
            return result;
        }

        /// <summary>RED's own log line: counts and sizes only, never a report name.</summary>
        public static string Describe(MyListBackupCleanupResult result, string when)
        {
            if (result.MissingFolder) return $"My List backups ({when}): no My List folder.";
            if (result.Declined) return $"My List backups ({when}): not cleaned (My List is a link or could not be read).";
            string rule = result.Days > 0
                ? $"Archive retention {result.Days} days + 1"
                : $"Archive retention {result.Days}, so {FallbackDays} days";
            return string.Format(CultureInfo.InvariantCulture,
                "My List backups ({0}): deleted {1} of reports no longer in My List, last changed on or before {2:yyyy-MM-dd} ({3}); " +
                "{4} left for the next run; kept {5} of reports still in My List and {6} newer ones.",
                when, result.Removed, result.Cutoff, rule, Math.Max(0, result.Candidates - result.Taken), result.KeptLive, result.KeptRecent);
        }
    }
}
