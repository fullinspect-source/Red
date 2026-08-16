#!/usr/bin/env python3
"""Regression checks for RED's offline updater notice."""

from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
APP = (ROOT / "App.xaml.cs").read_text(encoding="utf-8")
UPDATER = (ROOT / "Services" / "AppUpdateService.cs").read_text(encoding="utf-8")


class OfflineUpdateMessageTests(unittest.TestCase):
    def test_notice_says_only_update_check_was_skipped(self):
        self.assertIn("RED still works normally offline.", APP)
        self.assertIn("Only the automatic update check was skipped.", APP)
        self.assertIn("No action is required.", APP)

    def test_title_does_not_imply_red_requires_internet(self):
        self.assertIn('"Update Check Skipped"', APP)
        self.assertNotIn('"Internet Connection Needed"', APP)
        self.assertNotIn("Connect to the internet and try again.", UPDATER)


if __name__ == "__main__":
    unittest.main()
