"""UI route wiring; asynchronous behavior is exercised by UpdateUiHarness."""
from pathlib import Path
import unittest
ROOT = Path(__file__).resolve().parents[1]
MAIN = (ROOT / "MainWindow.xaml.cs").read_text()
ABOUT = (ROOT / "InspectionPickerWindow.xaml.cs").read_text()
APP = (ROOT / "App.xaml.cs").read_text()
class UpdateUiTests(unittest.TestCase):
    def test_startup_cancel_and_observe_before_open(self):
        self.assertLess(APP.index("await UpdateUiCoordinator.RunStartupAsync"), APP.index("new MainWindow()"))
        self.assertIn("cancellationToken: token", APP)
        self.assertIn("if (appUpdate.InstallerStarted)", APP)
    def test_no_second_automatic_check_during_editing(self):
        self.assertNotIn("CheckForUpdatesAsync(silent: true)", MAIN)
    def test_check_failures_do_not_mark_success(self):
        self.assertIn("if (redCheckError == null) MarkStartupAppUpdateChecked();", MAIN)
        self.assertIn("if (redCheckError == null && redInstallError == null) MarkStartupAppUpdateChecked();", MAIN)
    def test_independent_app_and_stats_failures(self):
        for source in (MAIN, ABOUT):
            self.assertGreaterEqual(source.count("UpdateUiCoordinator.CaptureAsync("), 2)
            self.assertIn("statsResult = await statsTask;", source)
    def test_about_flush_freeze_and_restore(self):
        start = ABOUT.index("private async Task<AppUpdateResult> RunAboutAppUpdateAsync")
        end = ABOUT.index("private async Task ForceAboutUpdateCoreAsync", start)
        body = ABOUT[start:end]
        self.assertLess(body.index("!editor.TryPrepareForAppUpdate()"), body.index("AppUpdateService.CheckAndInstallIfAvailableAsync"))
        self.assertLess(body.index("entry.Window.IsEnabled = false"), body.index("AppUpdateService.CheckAndInstallIfAvailableAsync"))
        self.assertIn("if (!installerStarted)", body)
        self.assertIn("entry.Window.IsEnabled = entry.WasEnabled", body)
        self.assertIn("if (_aboutUpdateRunning) return;", ABOUT)
    def test_installer_handled_before_stats_wait(self):
        body = ABOUT[ABOUT.index("private async Task ForceAboutUpdateCoreAsync"):]
        self.assertLess(body.index("Application.Current.Shutdown();"), body.index("statsResult = await statsTask;"))
if __name__ == "__main__": unittest.main()
