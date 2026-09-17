import pathlib
import unittest


ROOT = pathlib.Path(__file__).resolve().parents[1]
APP = (ROOT / "App.xaml.cs").read_text(encoding="utf-8")
UPDATER = (ROOT / "Services" / "AppUpdateService.cs").read_text(encoding="utf-8")
PROJECT = (ROOT / "InspectionEditor.csproj").read_text(encoding="utf-8")


class StartupUpdateCheckStaticTests(unittest.TestCase):
    def test_normal_startup_uses_24_hour_throttle(self):
        self.assertIn("AppUpdateService.CheckAndInstallIfAvailableAsync(cancellationToken: token)", APP)
        self.assertIn("once every 24 hours", APP)
        self.assertIn("TimeSpan.FromHours(24)", UPDATER)
        self.assertIn("options.MarkerPath", UPDATER)
        self.assertIn("SkippedByThrottle", UPDATER)
        self.assertIn("RecordSuccessfulCheck(options.MarkerPath)", UPDATER)

    def test_force_retry_path_remains(self):
        self.assertIn("CheckAndInstallIfAvailableAsync(bool force = false, CancellationToken cancellationToken = default)", UPDATER)
        self.assertIn("force: true, cancellationToken: cancellation.Token", (ROOT / "InspectionPickerWindow.xaml.cs").read_text(encoding="utf-8"))

    def test_release_metadata(self):
        self.assertIn("<Version>2.1.33</Version>", PROJECT)
        self.assertIn("<ReleaseDate>2026-09-17</ReleaseDate>", PROJECT)


if __name__ == "__main__":
    unittest.main()