using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace InspectionEditor.Services
{
    public sealed class ArchiveCleanupResult
    {
        public int Days { get; init; }
        public DateTime Cutoff { get; init; }
        public int Total { get; set; }
        public int Taken { get; set; }
        public int Removed { get; set; }
        public int Failed { get; set; }
        public bool Refused { get; set; }      // N2: N <= 0
        public bool Disabled { get; set; }     // N3: switch off
        public bool Declined { get; set; }     // A3: listing failed
        public bool MissingFolder { get; set; } // A1
        public List<string> Candidates { get; } = new();
    }

    // RED's own housekeeping switches (F-015 N3, F-019 §2), in RED's app data; both on by default (owner Q-04).
    public sealed class HousekeepingSettings
    {
        public bool CleanArchive { get; set; } = true;
        public bool PurgeJobFolders { get; set; } = true;
        // Live-risk gate (program release policy, owner Q-55): the Dropbox log prune waits for the owner's supervised check.
        // Only the owner sets this, in housekeeping.json. Since 2026-10-20 the Archive cleanup and the job-folder purge no
        // longer wait for it (owner decisions).
        public bool LiveCleanupVerified { get; set; }
        // A settings file that exists but cannot be read turns everything off for that launch: a user's "off" is never
        // silently replaced by the "on" defaults.
        [JsonIgnore] public bool ReadFailed { get; private set; }

        private static string PathOf(string? root) => Path.Combine(root ?? AppIdentity.LocalAppDataPath, "housekeeping.json");

        public static HousekeepingSettings Load(string? root = null)
        {
            try { return File.Exists(PathOf(root)) ? JsonConvert.DeserializeObject<HousekeepingSettings>(File.ReadAllText(PathOf(root))) ?? Unreadable() : new(); }
            catch (Exception) { return Unreadable(); }
        }

        private static HousekeepingSettings Unreadable() =>
            new() { CleanArchive = false, PurgeJobFolders = false, LiveCleanupVerified = false, ReadFailed = true };

        public void Save(string? root = null)
        {
            string path = PathOf(root);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(this, Formatting.Indented));
            File.Move(temp, path, overwrite: true);
        }
    }

    // Which launch deletions may run in this build (App.Housekeeping). Owner 2026-10-20: the Archive cleanup and the
    // job-folder purge run in every installed RED, each behind its own switch; the Dropbox log prune (no owner decision)
    // keeps waiting for the owner's supervised check. A dev build (Red2Dev) deletes from shared folders only with
    // RED_DEV_HOUSEKEEPING=1.
    public static class HousekeepingGates
    {
        public static bool ArchiveCleanup(bool devBuild, bool devHousekeeping) => !devBuild || devHousekeeping;

        public static bool JobFolderPurge(bool devBuild, bool devHousekeeping) => !devBuild || devHousekeeping;

        public static bool DropboxLogPrune(HousekeepingSettings settings, bool devHousekeeping) =>
            devHousekeeping || (settings.LiveCleanupVerified && !settings.ReadFailed);
    }

    // F-015 Archive retention (INSPECT 2026 ArchiveFilesDeleter): once per launch, before the Archive list is read.
    public static class ArchiveRetentionService
    {
        public const int MaxFilesPerRun = 200;
        // Owner 2026-10-20: RED keeps one day more than INSPECT. It applies INSPECT's own rule as if the setting were N + 1,
        // so with N = 5 on 10/20 it keeps files last changed on 10/15 or later (counting today as day 1: the 5- and 6-day-old
        // files) and deletes those changed on 10/14 or earlier (from day 7). INSPECT, where it also runs, still deletes from
        // day 6.
        public const int ExtraDays = InspectLiveSettings.ArchiveExtraDays;

        public static string RefusedLine(int days) => $"Archive cleanup refused: Archive retention is {days} days; RED needs 1 or more.";

        // Owner 2026-10-20: the cutoff for INSPECT's setting n, one day earlier than INSPECT's own (now - (n + 1) days).
        public static DateTime CutoffFor(DateTime now, int days) => InspectLiveSettings.ArchiveCutoff(now, days);

        // dryRun (V1): computes the candidates and the cutoff only; deletes nothing and writes no log line.
        public static ArchiveCleanupResult Run(string inspectionsFolder, InspectLogWriter log, DateTime now, bool dryRun = false,
            Action<string>? redOnlyLog = null, int? days = null, HousekeepingSettings? settings = null)
        {
            int n = days ?? InspectLiveSettings.ReadArchiveRetention(inspectionsFolder); // N1, read live at each launch
            var result = new ArchiveCleanupResult { Days = n, Cutoff = n > 0 ? CutoffFor(now, n) : now };
            redOnlyLog ??= DiagnosticLogService.Info;

            if (!dryRun && (settings ?? HousekeepingSettings.Load()).CleanArchive == false)
            {
                result.Disabled = true; // N3: no line at all
                return result;
            }
            if (n <= 0)
            {
                result.Refused = true;  // N2: RED-only line, nothing in the INSPECT-format log
                if (!dryRun) redOnlyLog(RefusedLine(n));
                return result;
            }

            string folder = Path.Combine(inspectionsFolder, "Archive");
            try
            {
                if (!Directory.Exists(folder))
                {
                    result.MissingFolder = true;
                    if (!dryRun) log.Info(string.Format(CultureInfo.CurrentCulture, "Archive cleanup: {0} does not exist, nothing to do", folder)); // A1
                    return result;
                }
                // RED safety: never delete through a junction or symbolic link (its files belong to another folder).
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                {
                    result.Declined = true;
                    if (!dryRun) redOnlyLog($"Archive cleanup declined: {folder} is a link to another folder; RED deletes nothing through links.");
                    return result;
                }

                FileInfo[] files;
                try
                {
                    files = new DirectoryInfo(folder).GetFiles("*.ins", SearchOption.TopDirectoryOnly); // A2: top level only
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
                {
                    result.Declined = true;
                    if (!dryRun) log.Warn(string.Format(CultureInfo.CurrentCulture, "Archive cleanup declined: could not read {0}", folder), ex); // A3
                    return result;
                }

                var candidates = files.Where(f => f.LastWriteTime.Date <= result.Cutoff.Date)
                                      .OrderBy(f => f.LastWriteTimeUtc).ToList(); // A2, A5 order
                result.Total = candidates.Count;
                result.Candidates.AddRange(candidates.Select(f => f.FullName));
                if (candidates.Count == 0)
                {
                    if (!dryRun) log.Info(string.Format(CultureInfo.CurrentCulture, "Archive cleanup: nothing in {0} older than {1}", folder, result.Cutoff)); // A4
                    return result;
                }

                var taken = candidates.Take(MaxFilesPerRun).ToList(); // A5 cap
                result.Taken = taken.Count;
                if (dryRun)
                    return result;
                log.Info(string.Format(CultureInfo.CurrentCulture,
                    "Archive cleanup: {0} file(s) in {1} older than {2}; removing {3} this run, {4} left for the next launch",
                    result.Total, folder, result.Cutoff, result.Taken, result.Total - result.Taken)); // A6

                foreach (var file in taken)
                {
                    try
                    {
                        file.Delete(); // A8: permanent; a file already gone counts as removed
                        result.Removed++;
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
                    {
                        result.Failed++;
                        log.Warn(string.Format(CultureInfo.CurrentCulture, "Archive cleanup could not remove {0}", file.FullName), ex);
                    }
                }
                log.Info(string.Format(CultureInfo.CurrentCulture, "Archive cleanup: removed {0} file(s), {1} failed, {2} left for the next launch",
                    result.Removed, result.Failed, result.Total - result.Taken + result.Failed)); // A9
                return result;
            }
            catch (Exception ex)
            {
                if (!dryRun) log.Error(ex.ToString()); // A11: the launch continues
                return result;
            }
        }
    }
}
