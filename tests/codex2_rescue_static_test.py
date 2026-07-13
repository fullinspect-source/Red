import pathlib
import unittest


ROOT = pathlib.Path(__file__).resolve().parents[1]
MAIN = (ROOT / "MainWindow.xaml.cs").read_text(encoding="utf-8")
ENERGY = (ROOT / "Services" / "EnergyComplianceService.cs").read_text(encoding="utf-8")
SLAB = (ROOT / "Services" / "SlabEngineeringService.cs").read_text(encoding="utf-8")
USER_DATA = (ROOT / "Services" / "UserDataService.cs").read_text(encoding="utf-8")


class Codex2RescueStaticTests(unittest.TestCase):
    def test_photo_required_is_actionable(self):
        self.assertIn("photoRequiredButton.Click += InlinePhotoRequiredButton_Click", MAIN)
        self.assertIn("CameraButton_Click(sender, e);", MAIN)

    def test_interactive_children_do_not_toggle_rows(self):
        for control in ("ButtonBase", "TextBoxBase", "ComboBox", "Slider"):
            self.assertIn(f"FindAncestor<{control}>(source)", MAIN)

    def test_design_assist_filters_status_controls_and_uses_both_prompts(self):
        self.assertIn("IsInlineStatusOnlyDesignTarget(item)", MAIN)
        self.assertIn('controlName is "yesno" or "yesnonani" or "passfail" or "passfailnani"', MAIN)
        self.assertIn('new[] { item.DisplayLabel, item.Name }', MAIN)

    def test_slab_depth_aliases_and_cpp_interior_items_use_minimum_comparison(self):
        self.assertIn('key == "SLABDEPTHINCHES" || key == "SLABDEPTH"', ENERGY)
        self.assertIn("actualParsed.Value >= designParsed.Value ? BannerState.Green : BannerState.Red", ENERGY)
        for item_number in ("8.13", "8.16", "8.19", "8.22"):
            self.assertIn(f'{{ "{item_number}", s => s.SlabThicknessInches?.ToString() }}', ENERGY)
        self.assertIn("SdItems.Contains(itemNum)", ENERGY)
        self.assertIn("return actual >= design ? BannerState.Green : BannerState.Red", ENERGY)

    def test_strand_patterns_are_bounded_and_hardware_eol_fallback_is_gone(self):
        self.assertIn(r'TOTAL[\s_]+NUMBER[\s_]+\b(\d{1,3})\b[\s\S]{0,80}?OF[\s_]+STRANDS', SLAB)
        self.assertIn(r'OF\s+STRANDS[\s\S]{0,30}?\b(\d{1,3})\b', SLAB)
        self.assertNotIn('var eolNum = Regex.Match(line.TrimEnd()', SLAB)

    def test_comment_formatter_is_shared_and_prefix_only_keeps_typing_space(self):
        self.assertIn("string commentToSave = BuildComment(prefix, coreComment, new List<string>())", USER_DATA)
        self.assertIn('result = prefix.ToLower() + " - ";', USER_DATA)
        self.assertIn("return prefixOnly ? result : result.Trim();", USER_DATA)
        self.assertIn('if (result.StartsWith("-"))', USER_DATA)
        self.assertIn("FocusInlineCommentEditor(action.Item)", MAIN)

    def test_focused_holddown_crop_and_photo_layout_are_present(self):
        self.assertIn("OcrHolddownTable(rawBytes, w, h, engine)", SLAB)
        self.assertIn("TryGetBoundingBox(PageIteratorLevel.TextLine", SLAB)
        self.assertIn("Preserve the focused table result", SLAB)
        self.assertIn("emptyPhoto.Height = 96", MAIN)
        self.assertGreaterEqual(MAIN.count("Height = 96"), 3)


if __name__ == "__main__":
    unittest.main()
