#!/usr/bin/env python3
"""Regression checks that the obsolete editor welcome animation stays purged."""

from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
XAML = (ROOT / "MainWindow.xaml").read_text(encoding="utf-8")
CODE = (ROOT / "MainWindow.xaml.cs").read_text(encoding="utf-8")


class ObsoleteWelcomeUiTests(unittest.TestCase):
    def test_editor_never_displays_old_welcome_page_while_opening_ins(self):
        self.assertIn('x:Name="WelcomePanel" Visibility="Collapsed"', XAML)
        opening_start = CODE.index("private void ShowOpeningInspectionState")
        opening_end = CODE.index("// ========== KEYBOARD NAVIGATION", opening_start)
        opening = CODE[opening_start:opening_end]
        self.assertNotIn("WelcomePanel.Visibility = Visibility.Visible", opening)
        self.assertNotIn("WelcomeHint", opening)

    def test_old_logo_tagline_and_slide_animation_are_removed(self):
        for obsolete in (
            "WelcomeLogo", "WelcomeTagline", "WelcomeHint", "TaglineTranslate",
            "HintTranslate", "StartWelcomeHintAnimation", "ShowTagline", "HideTagline",
        ):
            self.assertNotIn(obsolete, XAML + CODE)


if __name__ == "__main__":
    unittest.main()
