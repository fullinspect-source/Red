import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
MAIN = (ROOT / "MainWindow.xaml.cs").read_text(encoding="utf-8")
PARSER = (ROOT / "Services" / "FramingDesignParser.cs").read_text(encoding="utf-8")
SERVICE = (ROOT / "Services" / "FramingDesignService.cs").read_text(encoding="utf-8")
PROJECT = (ROOT / "InspectionEditor.csproj").read_text(encoding="utf-8")


class FramingDesignExtractionRegressionTests(unittest.TestCase):
    def test_supported_report_codes_are_explicit(self):
        for code in ("SWI", "TFF", "TPC", "TRDI", "TRSI", "COH", "FS", "FSF", "ME", "MP"):
            self.assertIn(f'"{code}"', SERVICE)

    def test_selected_plan_pdf_is_passed_to_background_extractor(self):
        self.assertIn("FramingDesignService.GetInfoForInspection(capturedFilePath, capturedPlanPdf)", MAIN)
        self.assertIn("string? selectedPlanPdf = null", SERVICE)

    def test_all_framing_reports_prefer_detail_sheet_plan_sets(self):
        self.assertIn('"TPC" or "TFF" or "TRDI" or "TRSI" =>', MAIN)
        self.assertIn('n.Contains("WITH DETAIL") ? 100', MAIN)

    def test_ocr_is_targeted_cached_and_has_wind_crop_retry(self):
        self.assertIn("FramingDesignParser.IsCandidateSheetText", SERVICE)
        self.assertIn("PageSegMode.SparseText", SERVICE)
        self.assertIn("OcrWindDesignCrop", SERVICE)
        self.assertIn("PageSegMode.SingleBlock", SERVICE)
        self.assertIn('Path.Combine(AppIdentity.LocalAppDataPath, "framing-cache")', SERVICE)
        self.assertIn("readableIndexes.Count >= pageCount", SERVICE)

    def test_safety_guards_block_inferred_wind_stale_tasks_and_destructive_ni(self):
        self.assertNotIn("values.Select(value => value.Speed).Max()", PARSER)
        self.assertNotIn("values.Select(value => value.Speed).Min()", PARSER)
        self.assertIn("capturedRequestId != _framingExtractionRequestId", MAIN)
        self.assertIn('suggestion.Value.Equals("NI"', MAIN)
        self.assertIn("!string.IsNullOrWhiteSpace(currentValue)", MAIN)

    def test_project_specific_prompt_mapping_uses_text_not_item_numbers(self):
        for phrase in (
            "windspeed exposure or c c pressure",
            "top bottom plate grade species",
            "rafter grade species",
            "ceiling joist grade species",
            "floor type",
            "roof sheathing attachment",
            "structural sheathing attachment",
        ):
            self.assertIn(phrase, PARSER)
        self.assertNotIn("item.Number", PARSER)

    def test_tply_rules_match_confirmed_use_classes(self):
        self.assertIn('3\\\" edge / 6\\\" middle', PARSER)
        self.assertIn('6\\\" edge / 6\\\" middle', PARSER)
        self.assertIn('6\\\" edge / 12\\\" middle', PARSER)
        self.assertIn("air\\s+barrier|thermal\\s+boundary", PARSER)
        self.assertIn("non\\s*[- ]?structural", PARSER)

    def test_floor_ni_is_separate_for_type_and_species(self):
        self.assertIn("FloorTypeNotApplicable", PARSER)
        self.assertIn("FloorProductNotApplicable", PARSER)
        self.assertNotIn('fields = new[] { "FloorType", "FloorProductNotApplicable" }', PARSER)

    def test_framing_badges_append_and_hide_only_complete_used_tokens(self):
        self.assertIn("CreateInlineFramingDesignAssistChips", MAIN)
        self.assertIn("AppendValue: suggestion.AppendValue", MAIN)
        self.assertIn("FramingDesignParser.AppendValue", MAIN)
        self.assertIn('Regex.Split(currentValue, @"[|;\\r\\n]+")', PARSER)
        self.assertNotIn("current.Contains(suggested", PARSER)

    def test_inline_ni_is_available_for_value_nani_controls(self):
        self.assertIn('controlName.Contains("nani")', MAIN)
        self.assertIn("AddInlineNiValueButtonIfNeeded(panel, item);", MAIN)
        self.assertIn("!IsInlineStatusOnlyDesignTarget(item)", MAIN)

    def test_status_only_items_never_receive_framing_design_values(self):
        self.assertIn("IsInlineStatusOnlyDesignTarget(item)", MAIN)
        self.assertIn('controlName is "yesno" or "yesnonani" or "passfail" or "passfailnani"', MAIN)

    def test_parser_harness_is_excluded_from_red_application_compile(self):
        self.assertIn('Compile Remove="tests\\FramingDesignParserHarness\\**\\*.cs"', PROJECT)


if __name__ == "__main__":
    unittest.main()
