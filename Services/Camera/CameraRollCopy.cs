using System;
using System.IO;
using System.Threading.Tasks;
using RedCamera.Core;

namespace InspectionEditor.Services.Camera
{
    /// <summary>
    /// F-027 C9 (owner Q-59, 2026-10-20): each still from RED's capture screen is also kept, as captured, in a
    /// <c>red-camera</c> subfolder of the camera roll as <c>RED_yyyyMMdd_HHmmss_fff.jpg</c>. Copies older than twice the
    /// Archive retention days (INSPECT's ArchiveRetention setting) are deleted automatically. Dev builds never write the
    /// real Pictures folder: they use <c>RED_DEV_CAMERA_ROLL</c>, else a CameraRoll folder in the dev app-data folder.
    /// </summary>
    public static class CameraRollCopy
    {
        public const string DevFolderVariable = "RED_DEV_CAMERA_ROLL";
        public const string SubfolderName = "red-camera";

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
        /// Deletes RED's own backup copies (<c>RED_*.jpg</c>, directly in the folder, no subfolders) last written more than
        /// twice <paramref name="archiveRetentionDays"/> days before <paramref name="now"/>. Retention of 0 or less deletes
        /// nothing (as Archive cleanup refuses it). Returns the number deleted.
        /// </summary>
        public static int PurgeOld(DateTime now, int archiveRetentionDays, string? folder = null)
        {
            if (archiveRetentionDays <= 0)
            {
                DiagnosticLogService.Info($"Camera: backup photos kept (Archive retention is {archiveRetentionDays} days).");
                return 0;
            }
            folder ??= TargetFolder();
            if (!Directory.Exists(folder)) return 0;
            DateTime cutoff = now.AddDays(-2.0 * archiveRetentionDays);
            int deleted = 0;
            try
            {
                foreach (var file in new DirectoryInfo(folder).EnumerateFiles("RED_*.jpg", SearchOption.TopDirectoryOnly))
                {
                    if ((file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (file.LastWriteTime >= cutoff) continue;
                    try
                    {
                        file.Delete();
                        deleted++;
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        DiagnosticLogService.Info($"Camera: old backup photo not deleted ({ex.GetType().Name}).");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                DiagnosticLogService.Info($"Camera: backup folder not searched ({ex.GetType().Name}).");
            }
            if (deleted > 0)
                DiagnosticLogService.Info($"Camera: deleted {deleted} backup photo(s) older than {2 * archiveRetentionDays} days.");
            return deleted;
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
