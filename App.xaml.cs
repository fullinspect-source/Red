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

            // Check GitHub on every RED startup so a release published after an earlier same-day
            // launch is still installed automatically. Triple-click remains a manual retry path.
            splash.SetStatus("Checking for updates...");
            bool startupInternetRequired = false;
            try
            {
                var appUpdateTask = AppUpdateService.CheckAndInstallIfAvailableAsync();
                var appUpdateCompleted = await Task.WhenAny(appUpdateTask, Task.Delay(TimeSpan.FromSeconds(12)));
                if (appUpdateCompleted == appUpdateTask)
                {
                    var appUpdate = await appUpdateTask;
                    if (appUpdate.InstallerStarted)
                    {
                        splash.SetStatus($"Installing RED v{appUpdate.LatestVersion}...");
                        await Task.Delay(1500);
                        Shutdown();
                        return;
                    }

                    startupInternetRequired = appUpdate.InternetRequired;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("App update check timed out, continuing startup...");
                }

                var dataUpdateTask = DataUpdateService.CheckForUpdatesAsync();
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
            MainWindow? mainWindow = null;
            if (e.Args.Length > 0 && !string.IsNullOrEmpty(e.Args[0]))
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

            if (startupInternetRequired)
            {
                MessageBox.Show(
                    "RED couldn't check for app updates because this device is offline.\n\n" +
                    "RED still works normally offline. Only the automatic update check was skipped.\n\n" +
                    "The updater will try again the next time RED opens. If you want to update, connect to the internet before opening RED.",
                    "Update Check Skipped",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else if (staleWarning != null)
            {
                MessageBox.Show(
                    staleWarning,
                    "Data Update Needed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            // If launched with a file argument (e.g. from "open in new window"), auto-load it
            if (mainWindow != null && e.Args.Length > 0 && !string.IsNullOrEmpty(e.Args[0]))
            {
                string filePath = e.Args[0];
                if (System.IO.File.Exists(filePath))
                    mainWindow.LoadFileFromArgs(filePath);
            }
        }

        private static InspectionPickerWindow CreateHomeListWindow()
        {
            string defaultPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Dropbox", "Inspections", "MyList"
            );

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
