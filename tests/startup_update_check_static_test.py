import pathlib
import unittest
import xml.etree.ElementTree as ET
from datetime import date


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
        about = (ROOT / "InspectionPickerWindow.xaml.cs").read_text(encoding="utf-8")
        self.assertIn("AppUpdateService.PrepareAsync(force: true, cancellationToken: token)", about)
        self.assertIn("_aboutLogoClickCount >= 3", about)

    def test_release_metadata(self):
        project = ET.fromstring(PROJECT)
        self.assertRegex(project.findtext("PropertyGroup/Version"), r"^\d+\.\d+\.\d+$")
        date.fromisoformat(project.findtext("PropertyGroup/ReleaseDate"))
        self.assertIn("<Product>RED $(Version)</Product>", PROJECT)


if __name__ == "__main__":
    unittest.main()