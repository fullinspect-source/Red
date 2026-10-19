using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using InspectionEditor.Services;

namespace InspectionEditor
{
    public partial class App : Application
    {
        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                if (args.ExceptionObject is Exception exception)
                    DiagnosticLogService.Log("AppDomain unhandled exception", exception);
            };
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                DiagnosticLogService.Log("Unobserved task exception", args.Exception);
                args.SetObserved();
            };

            // Validate license before showing main window
            var licenseResult = LicenseService.ValidateLicense();

            // Check for tampering - show error and exit immediately
            if (licenseResult.WasTampered)
            {
                MessageBox.Show(
                    "⛔ This installation has been disabled due to a license violation.\n\n" +
                    "Please contact your administrator for assistance.\n\n" +
                    $"Machine Code: {licenseResult.MachineId}",
                    "RED - Installation Disabled",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown();
                return;
            }

            if (!licenseResult.IsValid)
            {
                // Show activation window
                var activationWindow = new ActivationWindow(licenseResult.Message);
                var activated = activationWindow.ShowDialog() == true;

                if (!activated)
                {
                    // User cancelled or activation failed - exit app
                    Shutdown();
                    return;
                }

                // Re-validate after activation
                licenseResult = LicenseService.ValidateLicense();
                if (!licenseResult.IsValid)
                {
                    MessageBox.Show(
                        "License validation failed. Please contact support.",
                        "Activation Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    Shutdown();
                    return;
                }
            }

            // License is valid - check for expiration warning
            if (LicenseService.IsInGracePeriod(licenseResult.DaysRemaining))
            {
                MessageBox.Show(
                    $"⚠️ Your RED license expires in {licenseResult.DaysRemaining} day{(licenseResult.DaysRemaining == 1 ? "" : "s")}.\n\n" +
                    "Please contact your administrator for a renewal.",
                    "License Expiring Soon",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            // Show splash while we check for updates
            var splash = new SplashWindow();
            splash.Show();

            // Check for RED app + data updates. The app check is throttled to once every 24 hours.
            splash.SetStatus("Checking for updates...");

            try
            {
                var appUpdate = await UpdateUiCoordinator.RunStartupAsync(
                    token => AppUpdateService.CheckAndInstallIfAvailableAsync(cancellationToken: token),
                    TimeSpan.FromSeconds(12));
                if (appUpdate.InstallerStarted)
                {
                    splash.SetStatus($"Installing RED v{appUpdate.LatestVersion}...");
                    await Task.Delay(1500);
                    Shutdown();
                    return;
                }

                var dataUpdateTask = UpdateUiCoordinator.CaptureAsync(async () =>
                {
                    await DataUpdateService.CheckForUpdatesAsync();
                    return true;
                }, ex =>
                {
                    DiagnosticLogService.Log("Startup dataset update failed", ex);
                    return false;
                });
                var dataUpdateCompleted = await Task.WhenAny(dataUpdateTask, Task.Delay(TimeSpan.FromSeconds(8)));
                if (dataUpdateCompleted != dataUpdateTask)
                    System.Diagnostics.Debug.WriteLine("Data update still running, continuing startup...");
            }
            catch
            {
                // Don't let update failures block startup
            }

            // Warn if data files are STILL stale after update attempt
            splash.SetStatus("Loading...");
            var staleWarning = DataUpdateService.CheckForStaleData();

            // Show the right startup surface BEFORE closing splash — closing the last
            // visible window triggers WPF's OnLastWindowClose shutdown.
            // While the inspector profile is incomplete, or the first-run import has something to say, RED opens on
            // Settings, even for a file argument (owner, 2026-10-18).
            InspectorProfileService.EnsureImportedOnFirstRun();
            bool profileComplete = InspectorProfileService.IsCompleteNow();
            // F-003 O10: the user kind (never the value) once per launch, so a support log shows which path RED takes.
            DiagnosticLogService.Info($"Started RED {AppIdentity.VersionDisplay}: user kind {InspectUserType.Classify(InspectorProfileService.Load()?.UserType)}, " +
                                      $"profile {(profileComplete ? "complete" : "incomplete")}");
            InspectFormatLog.Launch(AppIdentity.DisplayName, AppIdentity.Version); // F-019 L7 launch banner (RED's own name and version)
            bool openEditor = profileComplete && InspectorProfileService.FirstRunMessage == null;
            splash.SetStatus("Tidying the Archive...");
            await RunLaunchHousekeepingAsync(); // F-013 step 14 retries, F-015 W1 (before any list is read)
            MainWindow? mainWindow = null;
            if (openEditor && e.Args.Length > 0 && !string.IsNullOrEmpty(e.Args[0]))
            {
                mainWindow = new MainWindow();
                mainWindow.Show();
            }
            else
            {
                var homeWindow = CreateHomeListWindow();
                MainWindow = homeWindow;
                homeWindow.Show();
            }
            splash.Close();
            _ = RunAfterListsLoadAsync(); // F-019 §2, L5, L6 in the background
            StartOfflineCaches();          // F-020 R2

            if (staleWarning != null)
            {
                MessageBox.Show(
                    staleWarning,
                    "Data Update Needed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            foreach (string message in LaunchMessages) // F-013 step 14: a changed My List copy was left in place
                MessageBox.Show(message, "Submit", MessageBoxButton.OK, MessageBoxImage.Information);

            // If launched with a file argument (e.g. from "open in new window"), auto-load it
            if (mainWindow != null && e.Args.Length > 0 && !string.IsNullOrEmpty(e.Args[0]))
            {
                string filePath = e.Args[0];
                if (System.IO.File.Exists(filePath))
                    mainWindow.LoadFileFromArgs(filePath);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { InspectFormatLog.Exit(AppIdentity.DisplayName); } // F-019 L7
            catch (Exception ex) { DiagnosticLogService.Log("Exit log line failed", ex); }
            base.OnExit(e);
        }

        private static InspectionPickerWindow CreateHomeListWindow()
        {
            string defaultPath = InspectorProfileService.MyListFolder();

            var homeWindow = new InspectionPickerWindow(defaultPath, stayOpenHome: true);
            homeWindow.InspectionOpenRequested += (_, filePath) =>
            {
                if (File.Exists(filePath))
                {
                    var editor = InspectionEditor.MainWindow.OpenAsCompanion(filePath);
                    editor.Closed += (_, _) => homeWindow.NotifyInspectionWindowClosed();
                }
            };
            return homeWindow;
        }

        // Settings in the home window (F-003 O9 refusals). Without one, e.g. when RED was started with a file, a new home
        // window opens; it starts on Settings by itself while the profile is incomplete.
        internal static void ShowHomeSettings()
        {
            foreach (Window window in Current.Windows)
            {
                if (window is InspectionPickerWindow { IsHomeWindow: true } home)
                {
                    home.OpenSettingsTab();
                    return;
                }
            }
            CreateHomeListWindow().Show();
        }

        private static void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            DiagnosticLogService.Log("WPF dispatcher exception", e.Exception);
            MessageBox.Show(
                $"RED caught an unexpected UI error and kept running.\n\n{e.Exception.Message}",
                "RED Error",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            e.Handled = true;
        }

    }
}
