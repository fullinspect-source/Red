import pathlib
import unittest


ROOT = pathlib.Path(__file__).resolve().parents[1]
APP = (ROOT / "App.xaml.cs").read_text(encoding="utf-8")
UPDATER = (ROOT / "Services" / "AppUpdateService.cs").read_text(encoding="utf-8")
PROJECT = (ROOT / "InspectionEditor.csproj").read_text(encoding="utf-8")


class StartupUpdateCheckStaticTests(unittest.TestCase):
    def test_every_startup_checks_latest_release(self):
        self.assertIn("AppUpdateService.CheckAndInstallIfAvailableAsync()", APP)
        self.assertIn("Check GitHub on every RED startup", APP)
        self.assertNotIn("CheckInterval", UPDATER)
        self.assertNotIn("LastCheckFile", UPDATER)
        self.assertNotIn("SkippedByThrottle", UPDATER)

    def test_force_retry_path_remains(self):
        self.assertIn("CheckAndInstallIfAvailableAsync(bool force = false)", UPDATER)
        self.assertIn("CheckAndInstallIfAvailableAsync(force: true)", (ROOT / "InspectionPickerWindow.xaml.cs").read_text(encoding="utf-8"))

    def test_release_metadata(self):
        self.assertIn("<Version>2.1.19</Version>", PROJECT)
        self.assertIn("<ReleaseDate>2026-09-01</ReleaseDate>", PROJECT)


if __name__ == "__main__":
    unittest.main()