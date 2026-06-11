import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
GROK = (ROOT / "Services" / "GrokApiClient.cs").read_text()
MAIN = (ROOT / "MainWindow.xaml.cs").read_text()


class RedAiPromptStaticTests(unittest.TestCase):
    def test_quick_comments_are_context_not_hard_exclusions(self):
        self.assertIn("Top 10 existing quick comments for this checklist item", GROK)
        self.assertIn("Do not copy any quick comment word-for-word", GROK)
        self.assertNotIn("DO NOT SUGGEST THESE", GROK)

    def test_comment_question_mark_and_polish_modes_are_explicit(self):
        self.assertIn("QUESTION MARK MODE", GROK)
        self.assertIn("POLISH MODE", GROK)
        self.assertIn("EMPTY COMMENT MODE", GROK)
        self.assertIn("Treat every question mark in the current comment as a blank", GROK)
        self.assertIn("If the current comment is not empty and has no question mark, polish it up", GROK)

    def test_fallback_reports_real_failure_not_fake_builder_comments(self):
        self.assertIn("AI returned no usable suggestion", GROK)
        self.assertIn("Raw response:", GROK)
        self.assertNotIn("Item inspected and found to be in acceptable condition", GROK)
        self.assertNotIn("Item requires attention - see photo for details", GROK)

    def test_mainwindow_sends_real_item_comment_not_unsaved_textbox_only(self):
        self.assertIn("GetCurrentCommentForAi()", MAIN)
        self.assertIn("private string GetCurrentCommentForAi(Item? item = null)", MAIN)

    def test_inspection_code_is_expanded_before_prompting_gemini(self):
        self.assertIn("GetInspectionNameForAi(inspectionType)", GROK)
        self.assertIn('"CPP" => "Concrete Pre Pour', GROK)
        self.assertNotIn("CPP (Concrete Pre-Pour)", GROK)


if __name__ == "__main__":
    unittest.main()
