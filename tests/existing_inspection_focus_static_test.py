#!/usr/bin/env python3
"""Regression checks for reopening an existing inspection from the persistent home screen."""

from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
PICKER = (ROOT / "InspectionPickerWindow.xaml.cs").read_text(encoding="utf-8")
MAIN = (ROOT / "MainWindow.xaml.cs").read_text(encoding="utf-8")


class ExistingInspectionFocusTests(unittest.TestCase):
    def test_home_tap_focuses_existing_editor_instead_of_showing_already_open(self):
        self.assertIn("TryBringOpenInspectionToFront(selected)", PICKER)
        self.assertIn("openInspection.BringInspectionToFront();", PICKER)
        self.assertIn("if (selected.IsTypeAlreadyOpen)", PICKER)
        guard = PICKER.index("if (selected.IsTypeAlreadyOpen)")
        focus = PICKER.index("TryBringOpenInspectionToFront(selected)", guard)
        status = PICKER.index("StatusText.Text", guard)
        self.assertLess(focus, status)

    def test_picker_can_match_exact_file_then_inspection_type(self):
        self.assertIn("internal string? OpenInspectionFilePath => _currentFilePath;", MAIN)
        self.assertIn("internal string? OpenInspectionCode => _currentInspectionCode;", MAIN)
        self.assertIn("PathEquals(w.OpenInspectionFilePath, selected.FilePath)", PICKER)
        self.assertIn("w.OpenInspectionCode", PICKER)


if __name__ == "__main__":
    unittest.main()
