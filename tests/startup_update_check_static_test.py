import pathlib
import unittest


ROOT = pathlib.Path(__file__).resolve().parents[1]
APP = (ROOT / "App.xaml.cs").read_text(encoding="utf-8")
UPDATER = (ROOT / "Services" / "AppUpdateService.cs").read_text(encoding="utf-8")
PROJECT = (ROOT / "InspectionEditor.csproj").read_text(encoding="utf-8")


class StartupUpdateCheckStaticTests(unittest.TestCase):
    def test_normal_startup_uses_24_hour_throttle(self):
        self.assertIn("AppUpdateService.CheckAndInstallIfAvailableAsync()", APP)
        self.assertIn("once every 24 hours", APP)
        self.assertIn("TimeSpan.FromHours(24)", UPDATER)
        self.assertIn("LastCheckFile", UPDATER)
        self.assertIn("SkippedByThrottle", UPDATER)
        self.assertIn("File.WriteAllText(LastCheckFile", UPDATER)

    def test_force_retry_path_remains(self):
        self.assertIn("CheckAndInstallIfAvailableAsync(bool force = false)", UPDATER)
        self.assertIn("CheckAndInstallIfAvailableAsync(force: true)", (ROOT / "InspectionPickerWindow.xaml.cs").read_text(encoding="utf-8"))

    def test_release_metadata(self):
        self.assertIn("<Version>2.1.23</Version>", PROJECT)
        self.assertIn("<ReleaseDate>2026-09-01</ReleaseDate>", PROJECT)


if __name__ == "__main__":
    unittest.main()