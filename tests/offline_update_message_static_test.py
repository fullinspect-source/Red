"""Request failure is not evidence that the entire device is offline."""
from pathlib import Path
import unittest
ROOT = Path(__file__).resolve().parents[1]
APP = (ROOT / "App.xaml.cs").read_text()
UPDATER = (ROOT / "Services/AppUpdateService.cs").read_text()
class OfflineUpdateMessageTests(unittest.TestCase):
    def test_no_false_device_offline_popup(self):
        self.assertNotIn("this device is offline", APP)
        self.assertNotIn("startupInternetRequired", APP)
        self.assertNotIn('"Update Check Skipped"', APP)
    def test_stale_data_warning_remains_independent(self):
        self.assertIn("if (staleWarning != null)", APP)
        self.assertIn('"Data Update Needed"', APP)
if __name__ == "__main__": unittest.main()
