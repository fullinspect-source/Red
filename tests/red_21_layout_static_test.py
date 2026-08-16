#!/usr/bin/env python3
"""Static regression checks for the RED 2.1 split-screen editor."""

from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
XAML = (ROOT / "MainWindow.xaml").read_text(encoding="utf-8")
CODE = (ROOT / "MainWindow.xaml.cs").read_text(encoding="utf-8")


class Red21LayoutTests(unittest.TestCase):
    def test_complete_inline_checklist_and_selected_item_tools_share_window(self):
        self.assertIn('x:Name="LeftColumn" Width="2.15*"', XAML)
        self.assertIn('x:Name="SelectedItemToolsColumn"', XAML)
        self.assertIn("ChecklistColumnGrid.SetValue(Grid.ColumnSpanProperty, 1);", CODE)
        self.assertIn("EditorScrollViewer.Visibility = _selectedItemToolsCollapsed", CODE)
        self.assertIn("InlineChecklistPanel.Visibility = _inlineEditorMode ? Visibility.Visible", CODE)

    def test_right_pane_opens_by_default_and_keeps_edge_handle_when_collapsed(self):
        self.assertIn("private bool _selectedItemToolsCollapsed = false;", CODE)
        self.assertIn('x:Name="SelectedItemToolsPaneButton"', XAML)
        self.assertIn('Grid.Column="3"', XAML)
        self.assertIn("if (e.ClickCount >= 2)", CODE)
        self.assertIn("SelectedItemToolsColumn.Width = _selectedItemToolsCollapsed", CODE)
        self.assertIn("? new GridLength(0)", CODE)
        self.assertIn('? "Click to reopen selected-item tools"', CODE)

    def test_bottom_action_row_and_existing_inline_drawers_remain(self):
        for control in ("MessagesButton", "SeeDocsButton", "SlabEngButton"):
            self.assertIn(f'x:Name="{control}"', XAML)
        self.assertIn("CreateInlineDrawerHost(item)", CODE)

    def test_single_click_selects_and_double_click_opens_inline_tools(self):
        start = CODE.index("private void InlineItemRow_MouseLeftButtonUp")
        end = CODE.index("private void ToggleInlineItem", start)
        body = CODE[start:end]
        self.assertIn("LoadItemEditor(item);", body)
        self.assertIn("if (e.ClickCount >= 2)", body)
        self.assertIn("ToggleInlineItem(item);", body)

    def test_selected_item_panel_preserves_all_tools(self):
        for control in (
            "PrefixPanel", "SuffixPanel", "CommentsTextBox", "QuickSuggestionsOverlay",
            "SavedCommentsPanel", "PhotoImage", "CameraButton", "GetSuggestionsButton",
            "TranscribeButton",
        ):
            self.assertEqual(XAML.count(f'x:Name="{control}"'), 1, control)

    def test_get_three_and_transcription_share_one_output_surface(self):
        self.assertEqual(XAML.count('x:Name="SuggestionsStack"'), 1)
        self.assertIn("🤖 GENERATIVE AI", XAML)
        self.assertIn("await GetAISuggestionsAsync();", CODE)
        self.assertIn("await GetAISuggestionsAsync(transcribeMode: true);", CODE)

    def test_compact_layout_has_versioned_one_time_migration(self):
        self.assertIn("private const double DefaultChecklistFontSize = 16;", CODE)
        self.assertIn('line.StartsWith("CompactSplitLayoutV21=")', CODE)
        self.assertIn('$"CompactSplitLayoutV21=True\\n"', CODE)


if __name__ == "__main__":
    unittest.main()
