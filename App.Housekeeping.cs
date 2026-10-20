using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using InspectionEditor.Services;

namespace InspectionEditor
{
    // Launch housekeeping, in INSPECT's order: F-013 step 14 pending My List deletes, then F-015's Archive cleanup, before
    // the home window reads the lists (INSPECT awaits its cleanup before LoadLists).
    public partial class App
    {
        internal static List<string> LaunchMessages { get; } = new();

        // Honoured only by a dev build: an environment variable never unlocks deletions in a release build.
        private static bool DevHousekeepingRequested => AppIdentity.IsDevBuild && Environment.GetEnvironmentVariable("RED_DEV_HOUSEKEEPING") == "1";

        // A dev build (Red2Dev) deletes nothing at launch unless RED_DEV_HOUSEKEEPING=1 is set.
        internal static bool PendingDeletesAllowed => !AppIdentity.IsDevBuild || DevHousekeepingRequested;

        // Owner 2026-10-20 (Q-55 answered): the Archive cleanup runs at every launch of an installed RED, with no supervised
        // check; a dev build only with RED_DEV_HOUSEKEEPING=1. An unreadable housekeeping.json still turns it off for that
        // launch (CleanArchive false).
        internal static bool ArchiveCleanupAllowed => HousekeepingGates.ArchiveCleanup(AppIdentity.IsDevBuild, DevHousekeepingRequested);

        // Owner 2026-10-20: the job-folder purge runs at every launch of an installed RED too, behind its own switch "RED
        // purges job folders"; a dev build only with RED_DEV_HOUSEKEEPING=1.
        internal static bool JobFolderPurgeAllowed => HousekeepingGates.JobFolderPurge(AppIdentity.IsDevBuild, DevHousekeepingRequested);

        // Live-risk gate (program release policy): the Dropbox log prune (no owner decision yet) deletes from the shared
        // {IF}\Logs only once the owner has set LiveCleanupVerified in RED's housekeeping.json, or for a supervised run with
        // RED_DEV_HOUSEKEEPING=1. The Settings dry runs always work.
        internal static bool DropboxLogPruneAllowed(HousekeepingSettings settings) => HousekeepingGates.DropboxLogPrune(settings, DevHousekeepingRequested);

        // F-019 L5/L6 write into {IF}\Logs in the shared Dropbox folder. A dev build's INSPECT-format lines are its own,
        // so it copies nothing there unless RED_DEV_HOUSEKEEPING=1.
        internal static bool DropboxLogCopyAllowed => !AppIdentity.IsDevBuild || DevHousekeepingRequested;

        // F-019 §2 and L5-L6, in the background once the lists are loaded (INSPECT starts PurgeJobFiles after
        // BindLists). The purge runs (owner 2026-10-20); the Dropbox log prune still waits for the owner's supervised check.
        internal static Task RunAfterListsLoadAsync() => Task.Run(() =>
        {
            string? folder = InspectorProfileService.Load()?.InspectionsFolder;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return;
            var settings = HousekeepingSettings.Load();
            try
            {
                if (DropboxLogCopyAllowed)
                    InspectLogCopyService.CopyToDropbox(folder, InspectLogWriter.Default, DateTime.Now); // L5 at launch
                if (DropboxLogCopyAllowed && DropboxLogPruneAllowed(settings))
                    InspectLogCopyService.PruneDropbox(folder, InspectLogWriter.Default, DateTime.Now);  // L6, launch only
            }
            catch (Exception ex) { DiagnosticLogService.Log("Log copy or prune failed", ex); }
            try
            {
                if (JobFolderPurgeAllowed)
                    JobFolderPurgeService.Run(folder, InspectLogWriter.Default, settings: settings);
                else
                    DiagnosticLogService.Info("Dev build: job folder purge skipped (set RED_DEV_HOUSEKEEPING=1 to run it).");
            }
            catch (Exception ex) { DiagnosticLogService.Log("Job folder purge failed", ex); }
            // F-027 C9 (owner Q-59): RED's own backup photos in Camera Roll\red-camera older than 2 x Archive retention.
            // Local copies only, so not behind the shared-folder gate above.
            try { Services.Camera.CameraRollCopy.PurgeOld(DateTime.Now, InspectLiveSettings.ReadArchiveRetention(folder)); }
            catch (Exception ex) { DiagnosticLogService.Log("Camera backup photo cleanup failed", ex); }
        });

        // F-020 R2: the stale offline caches at home-window load, once more 10 s later. A dev build makes no website
        // call for them unless RED_DEV_NETWORK=1 (Settings' Download Offline Data and Add's download still work). C3:
        // with no RED cache yet, INSPECT's local copies are taken read only.
        internal static void StartOfflineCaches()
        {
            if (AppIdentity.IsDevBuild && Environment.GetEnvironmentVariable("RED_DEV_NETWORK") != "1")
            {
                DiagnosticLogService.Info("Dev build: offline caches not refreshed at start (set RED_DEV_NETWORK=1).");
                return;
            }
            // RED holds no Strand session at launch, so the launch refresh uses the saved login on its own website session
            // (OfflineCacheService.SessionForDownload), as INSPECT does with its saved login. Each sign-in of this run
            // checks again (a session that has run past midnight finds yesterday's caches stale).
            Task.Run(() =>
            {
                try { OfflineCacheService.BootstrapFromInspect(); } // C3, once per device, off the UI thread
                catch (Exception ex) { DiagnosticLogService.Info($"Offline cache bootstrap failed: {ex.GetType().Name}"); }
                StartOfflineRefresh("start");
            });
            StrandScheduleService.SessionChanged += () =>
            {
                if (StrandScheduleService.IsSignedIn)
                {
                    OfflineCacheService.AllowSavedLogin();
                    StartOfflineRefresh("sign-in");
                }
            };
        }

        private static int _offlineRefreshRunning, _offlineRefreshAgain;

        private static void StartOfflineRefresh(string trigger)
        {
            // One automatic refresh at a time; a trigger during it (a sign-in) runs once more when it ends.
            if (Interlocked.Exchange(ref _offlineRefreshRunning, 1) == 1)
            {
                Volatile.Write(ref _offlineRefreshAgain, 1);
                return;
            }
            var session = OfflineCacheService.SessionForDownload(StrandWebsiteSession.Default);
            if (session == null)
            {
                DiagnosticLogService.Info($"Offline caches not refreshed at {trigger}: no Strand login is saved.");
                Volatile.Write(ref _offlineRefreshRunning, 0);
                return;
            }
            OfflineCacheService.AutoRefreshAsync(session).ContinueWith(task =>
            {
                if (task.IsFaulted)
                    DiagnosticLogService.Info($"Offline cache refresh failed: {task.Exception!.GetBaseException().GetType().Name}");
                Volatile.Write(ref _offlineRefreshRunning, 0);
                if (Interlocked.Exchange(ref _offlineRefreshAgain, 0) == 1)
                    StartOfflineRefresh("sign-in");
            }, TaskScheduler.Default);
        }
        // F-019 L5: the copy also runs each time an inspection editor opens (never the prune).
        internal static void CopyLogsOnEditorOpen()
        {
            if (!DropboxLogCopyAllowed) return;
            string? folder = InspectorProfileService.Load()?.InspectionsFolder;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
            Task.Run(() =>
            {
                try { InspectLogCopyService.CopyToDropbox(folder, InspectLogWriter.Default, DateTime.Now); }
                catch (Exception ex) { DiagnosticLogService.Log("Log copy failed", ex); }
            });
        }

        private static async Task RunLaunchHousekeepingAsync()
        {
            DiagnosticLogService.PruneOld(DateTime.Now); // F-019 L8: RED-only day files older than 30 days
            if (PendingDeletesAllowed)
            {
                try
                {
                    LaunchMessages.AddRange(await Task.Run(() => SubmitService.ProcessPendingDeletes()));
                }
                catch (Exception ex)
                {
                    DiagnosticLogService.Log("Pending My List deletes could not be processed", ex);
                }
            }
            else
            {
                DiagnosticLogService.Info("Dev build: pending My List deletes skipped (set RED_DEV_HOUSEKEEPING=1 to run them).");
            }

            string? folder = InspectorProfileService.Load()?.InspectionsFolder;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return;
            var settings = HousekeepingSettings.Load();
            if (settings.ReadFailed)
                DiagnosticLogService.Info("housekeeping.json could not be read: Archive cleanup and job folder purge are off for this launch.");
            if (!ArchiveCleanupAllowed)
            {
                DiagnosticLogService.Info("Dev build: Archive cleanup skipped (set RED_DEV_HOUSEKEEPING=1 to run it).");
                return;
            }
            try
            {
                await Task.Run(() => ArchiveRetentionService.Run(folder, InspectLogWriter.Default, DateTime.Now, settings: settings));
            }
            catch (Exception ex)
            {
                DiagnosticLogService.Log("Archive cleanup failed", ex);
            }
        }
    }
}
