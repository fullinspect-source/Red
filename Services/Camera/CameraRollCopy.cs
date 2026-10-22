using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using RedCamera.Core;

namespace InspectionEditor.Services.Camera
{
    /// <summary>
    /// F-027 C9 (owner Q-59, 2026-10-20): each still from RED's capture screen is also kept, as captured, in a
    /// <c>red-camera</c> subfolder of the camera roll as <c>RED_yyyyMMdd_HHmmss_fff.jpg</c>. Copies older than twice the
    /// Archive retention days (INSPECT's ArchiveRetention setting) are deleted automatically. Dev builds never write the
    /// real Pictures folder: they use <c>RED_DEV_CAMERA_ROLL</c>, else a CameraRoll folder in the dev app-data folder.
    /// Storage audit F7 (owner-approved 2026-10-22): never kept more than 90 days (also when the retention is 0 or less,
    /// which used to mean "forever"), and the folder never holds more than 2 GB: past that the oldest copies go first.
    /// </summary>
    public static class CameraRollCopy
    {
        public const string DevFolderVariable = "RED_DEV_CAMERA_ROLL";
        public const string SubfolderName = "red-camera";
        public const int MaxAgeDays = 90;
        public const long MaxFolderBytes = 2L * 1024 * 1024 * 1024;
        // The size cap never takes a copy from the last day: those are the photos being taken now.
        public static readonly TimeSpan SizeCapMinAge = TimeSpan.FromDays(1);
        private static readonly Regex CopyName = new(@"^RED_\d{8}_\d{6}_\d{3}(_\d+)?\.jpg$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        // RED's own write of a copy, interrupted by a crash (CameraFiles.SaveAtomic stages it in the camera roll itself).
        private static readonly Regex StagingName = new(@"^\.RED_\d{8}_\d{6}_\d{3}(_\d+)?\.jpg\.[0-9a-f]{32}\.tmp$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>Days a backup copy is kept: twice the Archive retention, at most 90; 90 when the retention is 0 or
        /// less.</summary>
        public static int AgeLimitDays(int archiveRetentionDays) =>
            archiveRetentionDays <= 0 ? MaxAgeDays : (int)Math.Min(MaxAgeDays, 2L * archiveRetentionDays);

        /// <summary>The folder the backup copies go to: <c>&lt;camera roll&gt;\red-camera</c>.</summary>
        public static string TargetFolder() => Path.Combine(CameraRollRoot(), SubfolderName);

        private static string CameraRollRoot()
        {
            if (AppIdentity.IsDevBuild)
            {
                string? redirect = Environment.GetEnvironmentVariable(DevFolderVariable);
                return string.IsNullOrWhiteSpace(redirect)
                    ? Path.Combine(AppIdentity.LocalAppDataPath, "CameraRoll")
                    : redirect;
            }
            // INSPECT's resolved camera roll when it is a Camera Roll* folder; else RED's own Pictures\Camera Roll, as
            // CameraService uses when nothing resolves.
            string? resolved = CameraFiles.CameraRollFolderForSaving();
            if (resolved != null && Path.GetFileName(resolved.TrimEnd('\\')).StartsWith("Camera Roll", StringComparison.OrdinalIgnoreCase))
                return resolved;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Camera Roll");
        }

        /// <summary>Writes the copy; returns its path, or null (logged) when it could not be written.</summary>
        public static string? TrySave(byte[] jpeg, DateTime now, string? folder = null)
        {
            try
            {
                return CameraFiles.SaveCopy(jpeg, folder ?? TargetFolder(), now);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                DiagnosticLogService.Info($"Camera: camera roll copy not written ({ex.GetType().Name}).");
                return null;
            }
        }

        /// <summary>
        /// Deletes RED's own backup copies (<c>RED_yyyyMMdd_HHmmss_fff.jpg</c>, directly in the folder, no subfolders)
        /// last written more than <see cref="AgeLimitDays"/> days before <paramref name="now"/>; then, while the copies
        /// left total more than 2 GB, the oldest ones (never one from the last day). Also RED's own interrupted writes
        /// (<c>.RED_*.jpg.{guid}.tmp</c>) more than a day old in the camera roll above. Links and files another program
        /// has open are skipped. Returns the number of photos deleted.
        /// </summary>
        public static int PurgeOld(DateTime now, int archiveRetentionDays, string? folder = null) =>
            Purge(now, archiveRetentionDays, folder).Files;

        // maxBytes: the 2 GB limit; a test passes a small one.
        public static CleanupTally Purge(DateTime now, int archiveRetentionDays, string? folder = null, long maxBytes = MaxFolderBytes)
        {
            var tally = new CleanupTally();
            folder ??= TargetFolder();
            int days = AgeLimitDays(archiveRetentionDays);
            try
            {
                var dir = new DirectoryInfo(folder);
                if (!dir.Exists || (dir.Attributes & FileAttributes.ReparsePoint) != 0) return tally;
                DateTime cutoff = now.AddDays(-days);
                var kept = new List<FileInfo>();
                foreach (var file in dir.EnumerateFiles("RED_*.jpg", SearchOption.TopDirectoryOnly))
                {
                    if (!CopyName.IsMatch(file.Name)) continue;
                    if (file.LastWriteTime >= cutoff || !SafeDelete.File(file, tally))
                        if (file.Exists) kept.Add(file);
                }
                int byAge = tally.Files;
                long total = kept.Sum(f => f.Length);
                if (total > maxBytes)
                {
                    DateTime youngest = now - SizeCapMinAge;
                    foreach (var file in kept.OrderBy(f => f.LastWriteTime))
                    {
                        if (total <= maxBytes || file.LastWriteTime >= youngest) break;
                        long length = file.Length;
                        if (SafeDelete.File(file, tally)) total -= length;
                    }
                }
                int bySize = tally.Files - byAge;
                SweepStaging(now, dir.Parent, tally);
                if (tally.Any)
                    DiagnosticLogService.Info($"Camera: backup photos cleaned: {tally} ({byAge} older than {days} days, " +
                        $"{bySize} over the {maxBytes / 1048576.0:0} MB limit, {tally.Files - byAge - bySize} interrupted writes); " +
                        $"{total / 1048576.0:0.0} MB kept.");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                DiagnosticLogService.Info($"Camera: backup folder not searched ({ex.GetType().Name}).");
            }
            return tally;
        }

        private static void SweepStaging(DateTime now, DirectoryInfo? cameraRoll, CleanupTally tally)
        {
            if (cameraRoll == null || !cameraRoll.Exists || (cameraRoll.Attributes & FileAttributes.ReparsePoint) != 0) return;
            DateTime cutoff = now.AddDays(-1);
            foreach (var file in cameraRoll.EnumerateFiles(".RED_*.tmp", SearchOption.TopDirectoryOnly))
                if (StagingName.IsMatch(file.Name) && file.LastWriteTime < cutoff)
                    SafeDelete.File(file, tally);
        }

        /// <summary>The purge for the signed-in inspector, in the background: retention from INSPECT's settings in the
        /// profile's Inspections folder (INSPECT's default 40 days when none).</summary>
        public static Task PurgeOldInBackground() => Task.Run(() =>
        {
            try
            {
                string? inspections = InspectorProfileService.Load()?.InspectionsFolder;
                PurgeOld(DateTime.Now, InspectLiveSettings.ReadArchiveRetention(inspections));
            }
            catch (Exception ex)
            {
                DiagnosticLogService.Log("Camera backup photo cleanup failed", ex);
            }
        });
    }
}
