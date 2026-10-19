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

        // Live-risk gate (program release policy: retention and purge are verified on sandbox copies only until the owner
        // checks them live). Archive cleanup deletes from the shared Archive in every build only once the owner has set
        // LiveCleanupVerified in RED's housekeeping.json after the supervised check (F-015 V2), or for a supervised run with
        // RED_DEV_HOUSEKEEPING=1. Until then RED logs that it waited; the Settings dry run always works.
        internal static bool ArchiveCleanupAllowed(HousekeepingSettings settings) =>
            DevHousekeepingRequested || (settings.LiveCleanupVerified && !settings.ReadFailed);

        // F-019 L5/L6 write into {IF}\Logs in the shared Dropbox folder. A dev build's INSPECT-format lines are its own,
        // so it copies nothing there unless RED_DEV_HOUSEKEEPING=1.
        internal static bool DropboxLogCopyAllowed => !AppIdentity.IsDevBuild || DevHousekeepingRequested;

        // F-019 §2 and L5-L6, in the background once the lists are loaded (INSPECT starts PurgeJobFiles after
        // BindLists). The purge and the Dropbox log prune delete from shared folders: they wait for the owner's
        // supervised check like F-015 (owner Q-55).
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
                if (DropboxLogCopyAllowed && ArchiveCleanupAllowed(settings))
                    InspectLogCopyService.PruneDropbox(folder, InspectLogWriter.Default, DateTime.Now);  // L6, launch only
            }
            catch (Exception ex) { DiagnosticLogService.Log("Log copy or prune failed", ex); }
            try
            {
                if (ArchiveCleanupAllowed(settings))
                    JobFolderPurgeService.Run(folder, InspectLogWriter.Default, settings: settings);
                else
                    DiagnosticLogService.Info("Job folder purge waiting for the owner's supervised check; nothing was deleted.");
            }
            catch (Exception ex) { DiagnosticLogService.Log("Job folder purge failed", ex); }
        });

        // F-020 R2: the stale offline caches at home-window load, once more 10 s later. A dev build makes no website
        // call for them unless RED_DEV_NETWORK=1 (Settings' Download Offline Data still works). C3: with no RED cache
        // yet, INSPECT's local copies are taken read only.
        internal static void StartOfflineCaches()
        {
            if (AppIdentity.IsDevBuild && Environment.GetEnvironmentVariable("RED_DEV_NETWORK") != "1")
            {
                DiagnosticLogService.Info("Dev build: offline caches not refreshed at start (set RED_DEV_NETWORK=1).");
                return;
            }
            // R2 needs the Strand login, which RED has only once a session exists. So the refresh runs at launch when a
            // login is already there, and otherwise once, at the first sign-in of this run (ORDERS, Buyer Walk or Settings).
            Task.Run(() =>
            {
                try { OfflineCacheService.BootstrapFromInspect(); } // C3, once per device, off the UI thread
                catch (Exception ex) { DiagnosticLogService.Info($"Offline cache bootstrap failed: {ex.GetType().Name}"); }
                if (StrandWebsiteSession.Default.HasCredentials)
                    StartOfflineRefresh();
            });
            StrandScheduleService.SessionChanged += () =>
            {
                if (StrandScheduleService.IsSignedIn && StrandWebsiteSession.Default.HasCredentials)
                    StartOfflineRefresh();
            };
        }

        private static int _offlineRefreshStarted;

        private static void StartOfflineRefresh()
        {
            if (Interlocked.Exchange(ref _offlineRefreshStarted, 1) == 1)
                return;
            OfflineCacheService.AutoRefreshAsync(StrandWebsiteSession.Default).ContinueWith(task =>
                DiagnosticLogService.Info($"Offline cache refresh failed: {task.Exception!.GetBaseException().GetType().Name}"), TaskContinuationOptions.OnlyOnFaulted);
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
            if (!ArchiveCleanupAllowed(settings))
            {
                DiagnosticLogService.Info("Archive cleanup waiting for the owner's supervised check (F-015 V2); nothing was deleted.");
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
