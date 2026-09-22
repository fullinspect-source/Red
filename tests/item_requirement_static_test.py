import pathlib
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]
MAIN = (ROOT / "MainWindow.xaml.cs").read_text()
XAML = (ROOT / "MainWindow.xaml").read_text()
SERVICE = (ROOT / "Services" / "ItemRequirementService.cs").read_text()


class ItemRequirementStaticTests(unittest.TestCase):
    def test_memo_requirement_uses_comments(self):
        self.assertIn('string.Equals(item.ControlName?.Trim(), "Memo"', SERVICE)
        self.assertIn('? string.IsNullOrWhiteSpace(item.Comments)', SERVICE)
        self.assertIn(': string.IsNullOrWhiteSpace(item.Value?.ToString())', SERVICE)

    def test_incomplete_filters_use_semantic_requirement(self):
        self.assertGreaterEqual(MAIN.count("ItemRequirementService.IsPrimaryRequirementMissing(item)"), 5)

    def test_memo_value_editor_is_replaced_with_comment_guidance(self):
        self.assertIn('StatusLabel.Text = "COMMENT REQUIRED"', MAIN)
        self.assertIn("Inspect2022 does not use Value for Memo items.", MAIN)
        self.assertIn('Text = missing ? "COMMENT REQUIRED" : "COMMENT ✓"', MAIN)

    def test_comment_box_receives_required_highlight(self):
        self.assertIn('x:Name="CommentEditorBorder"', XAML)
        self.assertIn("CommentEditorBorder.Background", MAIN)
        self.assertIn("UpdateRequiredFieldHighlighting(item);", MAIN)

    def test_inline_comment_status_updates_live_without_row_rebuild(self):
        self.assertIn('Name = "InlineCommentRequirement"', MAIN)
        self.assertIn('private void UpdateInlinePrimaryRequirementVisuals(Item item)', MAIN)
        capture = MAIN[MAIN.index('private void CaptureCommentEdit'):MAIN.index('private void CaptureValueEdit')]
        self.assertIn('UpdateInlinePrimaryRequirementVisuals(item);', capture)
        self.assertNotIn('RefreshInlineItemRow(item)', capture)
        helper = MAIN[MAIN.index('private void UpdateInlinePrimaryRequirementVisuals'):MAIN.index('private void InlineValueBox_TextChanged')]
        self.assertIn('label.Text = missing ? "COMMENT REQUIRED" : "COMMENT ✓";', helper)
        self.assertIn('border.Name == "InlineNumberBadge"', helper)

    def test_value_entry_does_not_satisfy_memo_requirement(self):
        self.assertNotIn("valueRequired = item.Required;", MAIN)
        self.assertNotIn("hasRequiredValue = item.Required;", MAIN)


if __name__ == "__main__":
    unittest.main()
