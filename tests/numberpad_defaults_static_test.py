import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DEFAULTS = (ROOT / "Services" / "NumberpadDefaultService.cs").read_text(encoding="utf-8")
MAIN = (ROOT / "MainWindow.xaml.cs").read_text(encoding="utf-8")


class NumberpadDefaultsStaticTests(unittest.TestCase):
    def test_pre_pour_profiles_use_requested_ranges(self):
        self.assertIn('text.Contains("corner tof to tog")', DEFAULTS)
        self.assertIn('Quarter(0, 30, camera: true)', DEFAULTS)
        self.assertIn('text.Contains("corner tof to bob")', DEFAULTS)
        self.assertIn('Quarter(20, 40, camera: true)', DEFAULTS)
        self.assertIn('return Quarter(0, 15);', DEFAULTS)
        self.assertIn('return Quarter(0, 12);', DEFAULTS)
        self.assertIn('return Quarter(0, 36);', DEFAULTS)

    def test_defaults_cover_requested_report_families(self):
        for code in ('"CPP"', '"SRP"', '"CPR"', '"AFI"', '"HEF"', '"HER"', '"HET"', '"IEF"', '"IER"'):
            self.assertIn(code, DEFAULTS)

    def test_forced_defaults_are_a_one_time_versioned_migration(self):
        self.assertIn("NumberpadDefaultsMigrationVersion", MAIN)
        self.assertIn("NumberpadDefaultService.MigrationVersion", MAIN)
        self.assertIn("CustomNumberpadRanges.Remove(key)", MAIN)
        self.assertIn("ItemClosedSpecialDrawers", MAIN)

    def test_default_tools_hide_comments_and_choose_camera_by_profile(self):
        self.assertIn('if (drawerName == "Comments") return false;', MAIN)
        self.assertIn('if (drawerName == "Photos") return numberpadDefault.CameraByDefault;', MAIN)
        self.assertIn("GetNumberpadDefault(item) != null", MAIN)

    def test_collapsed_slider_yields_to_real_comment_and_does_not_seed_value(self):
        self.assertIn("CreateCollapsedInlineNumberpadSlider(item)", MAIN)
        self.assertIn("if (!string.IsNullOrWhiteSpace(item.Comments))", MAIN)
        self.assertIn("double visualValue = currentValue ?? range.Minimum", MAIN)
        self.assertIn('Text = hasValue ? FormatNumberpadSliderValue', MAIN)

    def test_slider_instances_are_synchronized(self):
        self.assertIn("_inlineNumberpadSliders", MAIN)
        self.assertIn("SynchronizeInlineNumberpadSliders(action.Item, valueNumber, value)", MAIN)
        self.assertIn("RegisterInlineNumberpadSlider(action)", MAIN)


if __name__ == "__main__":
    unittest.main()
