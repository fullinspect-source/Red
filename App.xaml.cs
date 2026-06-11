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
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                LogUnhandledException(args.Exception);
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

            // Check for data file updates
            splash.SetStatus("Checking for updates...");
            try
            {
                var updateTask = DataUpdateService.CheckForUpdatesAsync();
                var completed = await Task.WhenAny(updateTask, Task.Delay(TimeSpan.FromSeconds(20)));
                if (completed != updateTask)
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

            if (staleWarning != null)
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
            LogUnhandledException(e.Exception);
            MessageBox.Show(
                $"RED caught an unexpected UI error and kept running.\n\n{e.Exception.Message}",
                "RED Error",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            e.Handled = true;
        }

        private static void LogUnhandledException(Exception ex)
        {
            try
            {
                string logPath = Path.Combine(Path.GetTempPath(), "red_unhandled_error.log");
                File.AppendAllText(logPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{ex}\n\n");
            }
            catch
            {
                // Nothing useful to do if logging itself fails.
            }
        }
    }
}
